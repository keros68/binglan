using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace BingLan.App.Tray;

internal readonly record struct TrayIdentity(uint ExplorerProcessId, string RuntimeId);
internal sealed record TrayEntry(string Key, string Name, string Source, uint ExplorerProcessId, AutomationElement Element)
{
    internal TrayIdentity Identity => new(ExplorerProcessId, Key);
}
internal sealed record TrayPanelEntry(TrayIdentity Identity, string Name, string Source, BitmapSource? Thumbnail, TrayFeishuOwner? FeishuOwner = null);
internal sealed record ProbeNode(string Name, string Id, string Class, string Type, bool Offscreen, bool Enabled, string[] Patterns);
internal sealed record ChildReport(string Handle, string Class, bool Visible, string[] RawFirstChildren, int? AccessibleChildren, string? Error);
internal sealed record SurfaceReport(string Source, int ProcessId, string Class, ProbeNode[] Nodes, ChildReport[] Windows, string? Error);
internal sealed record TrayReport(string Windows, int SessionId, bool Interactive, SurfaceReport[] Surfaces, int Actionable, string Limitation);
internal sealed record TrayRead(TrayReport Report, TrayEntry[] Entries);

internal static class TrayReader
{
    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(8);
    private const int MaximumSnapshotEntries = 128;
    private const int MaximumThumbnailDimension = 64;
    private static readonly object SnapshotGate = new();
    private static Task<TrayPanelEntry[]?>? _snapshotRead;
    private static TrayPanelEntry[]? _cachedSnapshot;
    private static int _invoking;

    internal static TrayPanelEntry[]? CachedSnapshot => Volatile.Read(ref _cachedSnapshot);

    internal static Task<TrayPanelEntry[]?> WarmSnapshotAsync()
    {
        lock (SnapshotGate) return _snapshotRead ??= Task.Run(ReadSnapshot);
    }

    internal static Task<TrayPanelEntry[]?> RefreshSnapshotAsync()
    {
        // UIA calls inside the provider cannot be interrupted. Waiters can time out
        // independently, and a retry joins the current read until it actually returns.
        lock (SnapshotGate)
        {
            if (_snapshotRead is null || _snapshotRead.IsCompleted) _snapshotRead = Task.Run(ReadSnapshot);
            return _snapshotRead;
        }
    }

    private static TrayPanelEntry[]? ReadSnapshot()
    {
        try
        {
            var read = Read();
            var taskbar = read.Report.Surfaces.FirstOrDefault(surface => surface.Source == "taskbar" && surface.Error is null);
            if (taskbar is null) return null;
            var previous = CachedSnapshot?.ToDictionary(entry => entry.Identity);
            // Bound the retained data even if Explorer exposes an unusually large tree.
            var entries = read.Entries.OrderBy(entry => entry.Source == "taskbar" ? 0 : 1)
                .Take(MaximumSnapshotEntries).ToArray();
            var pictures = TrayIconArtwork.Load(entries);
            var snapshot = new TrayPanelEntry[entries.Length];
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                BitmapSource? thumbnail = null;
                pictures.TryGetValue(entry.Key, out var artwork);
                if (artwork?.Image is { } image)
                {
                    var oldThumbnail = previous?.GetValueOrDefault(entry.Identity)?.Thumbnail;
                    thumbnail = CreateThumbnail(image, oldThumbnail);
                }
                snapshot[i] = new TrayPanelEntry(entry.Identity, artwork?.Name ?? entry.Name, entry.Source, thumbnail, artwork?.FeishuOwner);
            }
            if (!IsCurrentExplorer((uint)taskbar.ProcessId)) return null;
            Volatile.Write(ref _cachedSnapshot, snapshot);
            return snapshot;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException
            or COMException or ArgumentException or NotSupportedException or System.IO.IOException)
        {
            return null;
        }
    }

    private static BitmapSource CreateThumbnail(BitmapSource image, BitmapSource? previous)
    {
        var scale = Math.Min(1, (double)MaximumThumbnailDimension / Math.Max(image.PixelWidth, image.PixelHeight));
        var resized = scale < 1 ? new TransformedBitmap(image, new ScaleTransform(scale, scale)) : image;
        var converted = new FormatConvertedBitmap(resized, PixelFormats.Pbgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        if (previous is not null && previous.PixelWidth == converted.PixelWidth && previous.PixelHeight == converted.PixelHeight)
        {
            var previousPixels = new byte[pixels.Length];
            previous.CopyPixels(previousPixels, stride, 0);
            if (pixels.AsSpan().SequenceEqual(previousPixels)) return previous;
        }

        // Copy the thumbnail so the frozen bitmap does not retain a decoder or the full-size artwork.
        var thumbnail = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96,
            PixelFormats.Pbgra32, null, pixels, stride);
        thumbnail.Freeze();
        return thumbnail;
    }

    internal static TrayRead Read(bool diagnostics = false)
    {
        var reports = new List<SurfaceReport>();
        var entries = new List<TrayEntry>();
        var condition = diagnostics ? System.Windows.Automation.Condition.TrueCondition
            : new PropertyCondition(AutomationElement.AutomationIdProperty, "NotifyItemIcon");
        var tray = Native.FindWindowW("Shell_TrayWnd", null);
        Native.GetWindowThreadProcessId(tray, out var shellPid);
        var surfaces = new List<nint>();
        Native.EnumWindows((window, parameter) =>
        {
            var className = Native.ClassName(window);
            if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow") surfaces.Add(window);
            return true;
        }, 0);
        if (tray != 0 && !surfaces.Contains(tray)) surfaces.Add(tray);
        foreach (var window in surfaces)
        {
            var className = Native.ClassName(window);
            Native.GetWindowThreadProcessId(window, out var pid);
            if (pid != shellPid || pid == 0) continue;
            var source = className.Contains("Overflow", StringComparison.Ordinal) ? "existing-overflow" : "taskbar";
            var windows = new List<ChildReport>();
            var roots = new List<nint> { window };
            var childWindows = new List<nint>();
            Native.EnumChildWindows(window, (child, unused) =>
            {
                if (childWindows.Count < 32) childWindows.Add(child);
                return true;
            }, 0);
            if (diagnostics) windows.Add(ReadWindow(window));
            foreach (var child in childWindows)
            {
                Native.GetWindowThreadProcessId(child, out var childPid);
                if (childPid == shellPid)
                {
                    if (diagnostics) windows.Add(ReadWindow(child));
                    if (Native.ClassName(child) == "Windows.UI.Input.InputSite.WindowClass") roots.Add(child);
                }
            }
            try
            {
                var nodes = new List<ProbeNode>();
                var seen = new HashSet<string>();
                foreach (var rootHandle in roots)
                {
                    // Hidden XAML providers can be disconnected from their parent's UIA tree.
                    // Read their existing child HWND directly; never show or focus it.
                    var root = AutomationElement.FromHandle(rootHandle);
                    var descendants = root.FindAll(TreeScope.Descendants, condition);
                    // The provider controls FindAll; these limits bound only local traversal.
                    for (var i = 0; i < Math.Min(descendants.Count, 512); i++)
                    {
                        var element = descendants[i];
                        var runtimeId = element.GetRuntimeId();
                        if (runtimeId.Length is < 1 or > 64) continue;
                        var key = string.Join(".", runtimeId);
                        if (!seen.Add(key)) continue;
                        var current = element.Current;
                        if (diagnostics)
                        {
                            var patterns = element.GetSupportedPatterns().Select(p => p.ProgrammaticName).ToArray();
                            nodes.Add(new ProbeNode(current.Name, current.AutomationId, current.ClassName, current.ControlType.ProgrammaticName,
                                current.IsOffscreen, current.IsEnabled, patterns));
                        }
                        if (current.ProcessId != shellPid || current.AutomationId != "NotifyItemIcon"
                            || current.ClassName is not ("SystemTray.NotifyIconView" or "SystemTray.NormalButton")) continue;
                        var canInvoke = element.TryGetCurrentPattern(InvokePattern.Pattern, out _);
                        if (!canInvoke || !current.IsEnabled) continue;
                        if (entries.Any(e => e.Key == key)) continue;
                        var name = current.Name;
                        if (name.Length > 4096) name = name[..4096];
                        entries.Add(new TrayEntry(key, name, source, shellPid, element));
                    }
                }
                reports.Add(new SurfaceReport(source, (int)pid, className, nodes.ToArray(), windows.ToArray(), null));
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                reports.Add(new SurfaceReport(source, (int)pid, className, [], windows.ToArray(), exception.Message));
            }
        }
        return new TrayRead(new TrayReport(Environment.OSVersion.Version.ToString(), System.Diagnostics.Process.GetCurrentProcess().SessionId, Environment.UserInteractive, reports.ToArray(), entries.Count,
            "Only existing same-Explorer UI Automation elements with Invoke are actionable; unmaterialized overflow icons are unavailable."), entries.ToArray());
    }

    private static ChildReport ReadWindow(nint window)
    {
        var children = new List<string>();
        int? accessibleCount = null;
        string? error = null;
        try
        {
            var root = AutomationElement.FromHandle(window);
            var walker = TreeWalker.RawViewWalker;
            var child = walker.GetFirstChild(root);
            while (child is not null && children.Count < 32)
            {
                var current = child.Current;
                children.Add($"{current.ClassName}|{current.AutomationId}|{current.Name}");
                child = walker.GetNextSibling(child);
            }
            var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
            var hr = Native.AccessibleObjectFromWindow(window, 0xFFFFFFFC, ref iid, out var accessible);
            if (hr == 0 && accessible is IAccessibleDispatch dispatch)
            {
                accessibleCount = dispatch.ChildCount;
                Marshal.ReleaseComObject(accessible);
            }
            else error = $"MSAA HRESULT=0x{hr:X8}";
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            error = exception.Message;
        }
        return new ChildReport($"0x{window:X}", Native.ClassName(window), Native.IsWindowVisible(window), children.ToArray(), accessibleCount, error);
    }

    internal static async Task<string> InvokeAsync(
        TrayIdentity identity,
        bool contextMenu = false,
        CancellationToken cancellation = default,
        Func<uint, string, CancellationToken, Task<bool?>>? activateFeishu = null,
        TrayPanelEntry? displayedEntry = null)
    {
        if (Interlocked.CompareExchange(ref _invoking, 1, 0) != 0) return "action-in-progress";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(OperationTimeout);
        var token = timeout.Token;
        return await Task.Run(async () =>
        {
            try
            {
                if (token.IsCancellationRequested || !IsCurrentExplorer(identity.ExplorerProcessId)) return "element-unavailable";
                var fresh = Read().Entries.SingleOrDefault(entry => entry.Identity == identity);
                if (fresh is null || token.IsCancellationRequested) return "element-unavailable";
                var runtimeId = fresh.Element.GetRuntimeId();
                if (fresh.Element.Current.ProcessId != identity.ExplorerProcessId || string.Join(".", runtimeId) != identity.RuntimeId)
                    return "element-unavailable";
                if (contextMenu)
                {
                    var cancellationHandle = token.WaitHandle.SafeWaitHandle;
                    if (token.IsCancellationRequested) return "cancelled-before-context";
                    var result = Native.BingLanTrayShowContextMenu(runtimeId, runtimeId.Length, identity.ExplorerProcessId, cancellationHandle);
                    return result >= 0 ? "context-requested" : $"context-error-0x{result:X8}";
                }
                if (activateFeishu is not null && displayedEntry is not null
                    && TrayIconArtwork.VerifyFeishuOwner(displayedEntry, fresh) is { } owner)
                {
                    if (!IsCurrentExplorer(identity.ExplorerProcessId) || token.IsCancellationRequested)
                        return "cancelled-before-invoke";
                    var activated = await activateFeishu(owner.ProcessId, owner.ExecutablePath, token);
                    // Only an unclaimed action can fall back to Explorer's default action.
                    if (activated.HasValue) return activated.Value ? "invoked" : "activation-failed";
                }
                if (token.IsCancellationRequested) return "cancelled-before-invoke";
                if (!fresh.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) || pattern is not InvokePattern invoke) return "invoke-unavailable";
                // After discovery, check the shell again before dispatching the action.
                if (!IsCurrentExplorer(identity.ExplorerProcessId) || token.IsCancellationRequested) return "cancelled-before-invoke";
                invoke.Invoke();
                return "invoked";
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException
                or DllNotFoundException or EntryPointNotFoundException)
            {
                return exception.GetType().Name;
            }
            finally
            {
                Volatile.Write(ref _invoking, 0);
            }
        }).WaitAsync(token);
    }

    private static bool IsCurrentExplorer(uint expectedProcessId)
    {
        var tray = Native.FindWindowW("Shell_TrayWnd", null);
        return tray != 0 && Native.GetWindowThreadProcessId(tray, out var processId) != 0
            && processId == expectedProcessId;
    }
}

[ComImport, Guid("618736E0-3C3D-11CF-810C-00AA00389B71"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IAccessibleDispatch
{
    [DispId(-5001)] int ChildCount { get; }
}

internal static class Native
{
    [DllImport("BingLan.TrayContextNative.dll", ExactSpelling = true)]
    internal static extern int BingLanTrayShowContextMenu([In] int[] runtimeId, int runtimeIdLength,
        uint expectedExplorerPid, SafeWaitHandle cancellationHandle);
    internal delegate bool EnumProc(nint window, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindowW(string className, string? name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumProc callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumChildWindows(nint parent, EnumProc callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
    [DllImport("oleacc.dll")] internal static extern int AccessibleObjectFromWindow(nint window, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? accessible);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, System.Text.StringBuilder text, int count);
    internal static string ClassName(nint window)
    {
        var text = new System.Text.StringBuilder(256);
        GetClassNameW(window, text, text.Capacity);
        return text.ToString();
    }
}
