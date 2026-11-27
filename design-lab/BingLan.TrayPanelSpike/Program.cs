using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BingLan.App.Tray;

namespace BingLan.TrayPanelSpike;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--verify-feishu-snapshot")) return VerifyFeishuSnapshot();
        if (args.Contains("--probe"))
        {
            var read = Task.Run(() => TrayReader.Read(diagnostics: true));
            if (!read.Wait(TimeSpan.FromSeconds(8)))
            {
                Console.Error.WriteLine("UIA read timed out; no action issued.");
                return 1;
            }
            Console.WriteLine(JsonSerializer.Serialize(read.Result.Report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var verifyContext = args.Contains("--verify-own-context");
        using var ownIcon = args.Contains("--own-icon") || args.Contains("--verify-own") || verifyContext ? new OwnTrayIcon() : null;
        if (args.Contains("--verify-own") || verifyContext)
        {
            var exitCode = 1;
            app.Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    if (ownIcon is null || !ownIcon.Added) return;
                    Console.WriteLine($"OWN-REGISTERED={ownIcon.Added} name={ownIcon.Name} hwnd=0x{ownIcon.Handle:X}");
                    await Task.Delay(1000);
                    var snapshot = await TrayReader.WarmSnapshotAsync().WaitAsync(TrayReader.OperationTimeout);
                    var entry = snapshot?.SingleOrDefault(e => e.Name.Contains(ownIcon.Name, StringComparison.Ordinal));
                    if (entry is null) { Console.WriteLine("OWN-UNAVAILABLE: Shell registered icon is not exposed as an actionable UIA element."); return; }
                    if (!ReferenceEquals(snapshot, TrayReader.CachedSnapshot))
                    {
                        Console.WriteLine("OWN-CACHE-UNAVAILABLE: The snapshot was not published to the shared cache.");
                        return;
                    }
                    Console.WriteLine("OWN-CACHED=True");
                    var before = ownIcon.SelectionCount;
                    var beforeContext = ownIcon.ContextCount;
                    var beforePopup = ownIcon.MenuPopupCount;
                    var status = await TrayReader.InvokeAsync(entry.Identity, contextMenu: verifyContext);
                    await Task.Delay(1000);
                    var after = ownIcon.SelectionCount;
                    if (verifyContext)
                    {
                        for (var check = 0; ownIcon.IsMenuOpen && check < 60; check++) await Task.Delay(100);
                        Console.WriteLine($"OWN-CONTEXT={status}; contexts-before={beforeContext}; contexts-after={ownIcon.ContextCount}; popups-before={beforePopup}; popups-after={ownIcon.MenuPopupCount}; menu-open={ownIcon.IsMenuOpen}; messages={string.Join(',', ownIcon.Messages)}");
                        if (status == "context-requested" && ownIcon.ContextCount > beforeContext && ownIcon.MenuPopupCount > beforePopup && !ownIcon.IsMenuOpen) exitCode = 0;
                    }
                    else
                    {
                        Console.WriteLine($"OWN-INVOKE={status}; callbacks-before={before}; callbacks-after={after}; messages={string.Join(',', ownIcon.Messages)}");
                        if (status == "invoked" && after > before) exitCode = 0;
                    }
                }
                catch (Exception exception) { Console.WriteLine("OWN-ERROR=" + exception.Message); }
                finally
                {
                    ownIcon?.CloseMenu();
                    if (ownIcon?.IsMenuOpen == true) await Task.Delay(100);
                    ownIcon?.Dispose();
                    app.Shutdown(exitCode);
                }
            });
            return app.Run();
        }
        var panel = new TrayPanelWindow();
        panel.Closing += (_, eventArgs) =>
        {
            if (ownIcon?.IsMenuOpen != true) return;
            eventArgs.Cancel = true;
            ownIcon.CloseMenu();
            app.Dispatcher.BeginInvoke(async () => { await Task.Delay(100); panel.Close(); });
        };
        panel.Closed += (_, _) => { ownIcon?.Dispose(); app.Shutdown(); };
        var secondsArgument = args.FirstOrDefault(arg => arg.StartsWith("--seconds=", StringComparison.Ordinal));
        if (secondsArgument is not null && int.TryParse(secondsArgument.AsSpan(10), out var seconds) && seconds is >= 1 and <= 300)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) => { timer.Stop(); panel.Close(); };
            timer.Start();
        }
        return app.Run(panel);
    }

    private static int VerifyFeishuSnapshot()
    {
        try
        {
            var snapshot = TrayReader.WarmSnapshotAsync().WaitAsync(TrayReader.OperationTimeout).GetAwaiter().GetResult();
            if (snapshot is null || !ReferenceEquals(snapshot, TrayReader.CachedSnapshot))
                throw new InvalidOperationException("The tray snapshot was unavailable in the shared cache.");
            var candidates = snapshot.Where(item => item.Name == "飞书" && item.FeishuOwner is not null).Take(2).ToArray();
            if (candidates is not [var entry] || entry.Thumbnail is not { IsFrozen: true } thumbnail)
                throw new InvalidOperationException("No unique unnamed UI Automation entry became 飞书 with a verified owner and frozen thumbnail.");

            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            var running = new Dictionary<uint, string>();
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("Feishu"))
            {
                using (process)
                {
                    if (process.SessionId == currentProcess.SessionId && process.MainModule?.FileName is { } path)
                    {
                        running[(uint)process.Id] = path;
                    }
                }
            }

            var actualPixels = CopyThumbnailPixels(thumbnail);
            using var tray = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (tray is null) throw new InvalidOperationException("The Explorer tray artwork cache was unavailable.");
            foreach (var id in tray.GetSubKeyNames())
            {
                using var key = tray.OpenSubKey(id);
                if (key?.GetValue("ExecutablePath") is not string path || !running.Values.Contains(path, StringComparer.OrdinalIgnoreCase)
                    || key.GetValue("InitialTooltip") is not string { Length: 0 }
                    || key.GetValue("IconSnapshot") is not byte[] png) continue;

                using var stream = new System.IO.MemoryStream(png, writable: false);
                var expected = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                if (thumbnail.PixelWidth != expected.PixelWidth || thumbnail.PixelHeight != expected.PixelHeight
                    || !actualPixels.AsSpan().SequenceEqual(CopyThumbnailPixels(expected))) continue;

                var (owner, missing) = Task.Run(() =>
                {
                    var current = TrayReader.Read().Entries.Where(item => item.Identity == entry.Identity).Take(2).ToArray();
                    var outsideTray = new TrayEntry(entry.Identity.RuntimeId, "", entry.Source,
                        entry.Identity.ExplorerProcessId, AutomationElement.RootElement);
                    var feishu = current is [var live] ? TrayIconArtwork.VerifyFeishuOwner(entry, live) : null;
                    var unmatched = !TrayIconArtwork.Load([outsideTray]).ContainsKey(outsideTray.Key)
                        && TrayIconArtwork.VerifyFeishuOwner(entry, outsideTray) is null;
                    return (feishu, unmatched);
                }).WaitAsync(TrayReader.OperationTimeout).GetAwaiter().GetResult();
                if (!missing) throw new InvalidOperationException("An unrelated element reused the tray identity and inherited its owner.");
                if (owner is not { } matched || !running.TryGetValue(matched.ProcessId, out var processPath)
                    || !matched.ExecutablePath.Equals(processPath, StringComparison.OrdinalIgnoreCase)
                    || !matched.ExecutablePath.Equals(path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The Feishu tray icon did not resolve to a registered owner from the running Feishu executable.");
                Console.WriteLine($"FEISHU-SNAPSHOT PASS: empty UIA name became {entry.Name}; {thumbnail.PixelWidth}x{thumbnail.PixelHeight} pixels match the running application's tray artwork; shared cache published.");
                Console.WriteLine("FEISHU-OWNER PASS: the unlabeled notification icon matched a registered Feishu owner window.");
                Console.WriteLine("UNMATCHED-SNAPSHOT PASS: an unrelated element with the same tray identity retained the fallback.");
                return 0;
            }

            throw new InvalidOperationException("The thumbnail did not match a blank-tooltip snapshot for the running Feishu executable.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FEISHU-SNAPSHOT FAIL: {exception.Message}");
            return 1;
        }
    }

    private static byte[] CopyThumbnailPixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
