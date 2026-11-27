using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Automation;
using Microsoft.Win32;

namespace BingLan.App.Tray;

internal static class TrayIconArtwork
{
    private const int MaximumSnapshotBytes = 128 * 1024;
    private const int MaximumTotalBytes = 8 * 1024 * 1024;
    private const int MaximumDimension = 256;
    private sealed record Snapshot(string Tooltip, string FirstLine, string Application, string Path, byte[] Bytes, string Digest, uint? Uid, Guid IconGuid);

    internal sealed record Artwork(string Name, BitmapSource? Image);

    internal static IReadOnlyDictionary<string, Artwork> Load(IReadOnlyList<TrayEntry> entries)
    {
        var result = new Dictionary<string, Artwork>(StringComparer.Ordinal);
        if (entries.Count == 0) return result;
        try
        {
            var snapshots = ReadSnapshots();
            var running = ReadRunningProcesses(snapshots);
            var runningPaths = running.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var located = entries.Any(entry => string.IsNullOrWhiteSpace(entry.Name))
                ? LocateRegisteredIcons(snapshots, running) : [];
            foreach (var entry in entries)
            {
                if (entry.Name.Length > 4096) continue;
                var name = Normalize(entry.Name);
                if (name.Length == 0)
                {
                    if (ResolveUnnamed(entry, located) is { } resolved) result[entry.Key] = resolved;
                    continue;
                }
                var firstLine = Normalize(FirstLine(entry.Name));
                if (IsOneDriveAccountName(firstLine))
                {
                    result[entry.Key] = new Artwork(entry.Name, CreateOneDriveArtwork());
                    continue;
                }
                var matches = snapshots.Where(snapshot => snapshot.Bytes.Length > 0).Select(snapshot => (Snapshot: snapshot, Score: Match(name, firstLine, snapshot)))
                    .Where(match => match.Score > 0).ToArray();
                if (matches.Length == 0) continue;
                var bestScore = matches.Max(match => match.Score);
                var best = matches.Where(match => match.Score == bestScore).Select(match => match.Snapshot).ToArray();
                var products = best.Select(snapshot => Product(snapshot.Application)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                // OneDrive snapshots contain historical sync/account state, not neutral artwork.
                if (products.Any(product => product.Equals("OneDrive", StringComparison.OrdinalIgnoreCase))) continue;
                if (products.Length == 1)
                {
                    var current = matches.Select(match => match.Snapshot).Where(snapshot =>
                        Product(snapshot.Application).Equals(products[0], StringComparison.OrdinalIgnoreCase)
                        && runningPaths.Contains(snapshot.Path)).ToArray();
                    if (current.Length > 0) best = current;
                }
                // Cached records can outlive applications and repeat across installations.
                // Equal labels are usable only when every best candidate has the same artwork.
                BitmapSource? image;
                if (best.Select(snapshot => snapshot.Digest).Distinct(StringComparer.Ordinal).Take(2).Count() == 1)
                    image = Decode(best[0].Bytes);
                else if (products.Length == 1)
                {
                    var paths = runningPaths.Where(path => Product(Path.GetFileNameWithoutExtension(path))
                        .Equals(products[0], StringComparison.OrdinalIgnoreCase)).ToArray();
                    image = paths.Length == 1 ? ExtractApplicationIcon(paths[0]) : null;
                }
                else image = null;
                if (image is not null) result[entry.Key] = new Artwork(entry.Name, image);
            }
        }
        catch (Exception exception) when (IsReadFailure(exception)) { }
        return result;
    }

    private static List<Snapshot> ReadSnapshots()
    {
        var result = new List<Snapshot>();
        using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: false);
        if (root is null) return result;
        var totalBytes = 0;
        // This is a bounded, on-demand cache lookup; it does not enumerate executable files.
        foreach (var name in root.GetSubKeyNames().Take(2048))
        {
            try
            {
                using var key = root.OpenSubKey(name, writable: false);
                if (key is null) continue;
                var bytes = totalBytes < MaximumTotalBytes ? ReadSnapshot(key) ?? [] : [];
                if (totalBytes + bytes.Length > MaximumTotalBytes) bytes = [];
                totalBytes += bytes.Length;
                var tooltip = key.GetValue("InitialTooltip", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                var path = key.GetValue("ExecutablePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                if (tooltip.Length > 4096 || path.Length > 32768) continue;
                // Taking only the filename also handles unresolved known-folder GUID paths.
                // Use executable resources only for paths confirmed against a running process.
                var application = Normalize(Path.GetFileNameWithoutExtension(path));
                result.Add(new Snapshot(Normalize(tooltip), Normalize(FirstLine(tooltip)), application, ResolvePath(path),
                    bytes, bytes.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(bytes)),
                    key.GetValue("UID") is int uid ? unchecked((uint)uid) : null,
                    Guid.TryParse(key.GetValue("IconGuid") as string, out var guid) ? guid : Guid.Empty));
            }
            catch (Exception exception) when (IsReadFailure(exception)) { }
        }
        return result;
    }

    private static List<(Snapshot Icon, Rect Bounds)> LocateRegisteredIcons(
        List<Snapshot> snapshots, Dictionary<uint, string> running)
    {
        var locations = new List<(Snapshot, Rect)>();
        var byPath = snapshots.ToLookup(icon => icon.Path, StringComparer.OrdinalIgnoreCase);
        var dpi = SetThreadDpiAwarenessContext(-4); // UI Automation supplies physical screen coordinates.
        try
        {
            void Locate(Snapshot icon, nint owner)
            {
                var id = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(),
                    Owner = owner, Uid = icon.Uid.GetValueOrDefault(), Guid = icon.IconGuid };
                if (Shell_NotifyIconGetRect(ref id, out var r) == 0 && r.Right > r.Left && r.Bottom > r.Top)
                    locations.Add((icon, new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)));
            }
            foreach (var path in running.Values.Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var icon in byPath[path])
                    if (icon.IconGuid != Guid.Empty) Locate(icon, 0);

            void CheckOwner(nint window)
            {
                Native.GetWindowThreadProcessId(window, out var pid);
                if (!running.TryGetValue(pid, out var path)) return;
                foreach (var icon in byPath[path])
                    if (icon.IconGuid == Guid.Empty && icon.Uid.HasValue) Locate(icon, window);
            }
            Native.EnumWindows((window, _) => { CheckOwner(window); return true; }, 0);
            // Some applications register a message-only window as the tray callback owner.
            nint message = 0;
            for (var count = 0; count < 4096; count++)
            {
                message = FindWindowExW(-3, message, null, null);
                if (message == 0) break;
                CheckOwner(message);
            }
        }
        finally { if (dpi != 0) SetThreadDpiAwarenessContext(dpi); }
        return locations;
    }

    private static Artwork? ResolveUnnamed(TrayEntry entry, List<(Snapshot Icon, Rect Bounds)> locations)
    {
        Rect bounds;
        try { bounds = entry.Element.Current.BoundingRectangle; }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException) { return null; }
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return null;
        // Explorer's accessibility and shell rectangles can differ by a physical pixel at mixed DPI.
        var matches = locations.Where(item => Math.Abs(item.Bounds.Left - bounds.Left) <= 2
            && Math.Abs(item.Bounds.Top - bounds.Top) <= 2 && Math.Abs(item.Bounds.Right - bounds.Right) <= 2
            && Math.Abs(item.Bounds.Bottom - bounds.Bottom) <= 2).Select(item => item.Icon).Distinct().ToArray();
        if (matches.Length != 1) return null;
        var icon = matches[0];
        string? displayName = null;
        try { displayName = FileVersionInfo.GetVersionInfo(icon.Path).ProductName?.Trim(); }
        catch (Exception ex) when (IsReadFailure(ex) || ex is System.ComponentModel.Win32Exception) { }
        if (string.IsNullOrWhiteSpace(displayName)) displayName = icon.Application;
        if (displayName.Equals("Feishu", StringComparison.OrdinalIgnoreCase)) displayName = "飞书";
        var image = IsOneDriveAccountName(icon.Application) ? CreateOneDriveArtwork()
            : (icon.Bytes.Length > 0 ? Decode(icon.Bytes) : null) ?? ExtractApplicationIcon(icon.Path);
        return new Artwork(displayName, image);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconIdentifier { internal uint Size; internal nint Owner; internal uint Uid; internal Guid Guid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenRect { internal int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out ScreenRect location);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint FindWindowExW(nint parent, nint after, string? className, string? title);
    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    private static int Match(string name, string firstLine, Snapshot snapshot)
    {
        if (snapshot.Tooltip.Length > 0 && name.Equals(snapshot.Tooltip, StringComparison.OrdinalIgnoreCase)) return 4;
        if (snapshot.FirstLine.Length >= 4 && firstLine.Equals(snapshot.FirstLine, StringComparison.OrdinalIgnoreCase)) return 3;
        // Shell tooltips may be truncated at the native tooltip capacity.
        var shorter = name.Length < snapshot.Tooltip.Length ? name : snapshot.Tooltip;
        var longer = name.Length < snapshot.Tooltip.Length ? snapshot.Tooltip : name;
        if (shorter.Length >= 120 && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase)) return 2;
        if (snapshot.Application.Length < 4) return 0;
        if (Product(snapshot.Application) == "weixin" && firstLine is "微信" or "Weixin" or "WeChat") return 1;
        // Match an executable's complete name, permitting its customary spaces/hyphens.
        // Deliberately avoid substrings and fuzzy matching; unknown icons retain a fallback.
        var application = Compact(snapshot.Application);
        if (application.Length < 4) return 0;
        var matched = 0;
        for (var index = 0; index < firstLine.Length; index++)
        {
            if (!char.IsLetterOrDigit(firstLine[index])) continue;
            if (char.ToUpperInvariant(firstLine[index]) != char.ToUpperInvariant(application[matched++])) return 0;
            if (matched != application.Length) continue;
            return index + 1 == firstLine.Length || !char.IsLetterOrDigit(firstLine[index + 1]) ? 1 : 0;
        }
        return 0;
    }

    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string FirstLine(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    private static string Compact(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());
    private static string Product(string application) => application.Equals("WeChat", StringComparison.OrdinalIgnoreCase)
        || application.Equals("Weixin", StringComparison.OrdinalIgnoreCase) ? "weixin" : application;

    private static bool IsOneDriveAccountName(string firstLine)
    {
        const string product = "OneDrive";
        if (firstLine.Equals(product, StringComparison.OrdinalIgnoreCase)) return true;
        if (!firstLine.StartsWith(product, StringComparison.OrdinalIgnoreCase)) return false;
        var account = firstLine[product.Length..].TrimStart();
        return account.Length > 1 && account[0] is '-' or '–' or '—'
            && !string.IsNullOrWhiteSpace(account[1..]);
    }

    private static BitmapSource CreateOneDriveArtwork()
    {
        // This cloud identifies the product; it does not describe account or sync status.
        var cloud = Geometry.Parse("M8,25 C4.7,25 2,22.5 2,19.4 C2,16.5 4.2,14 7,13.6 C7.3,9.8 10.5,7 14.3,7 C17.5,7 20.3,9 21.4,11.9 C22.2,11.6 23.1,11.4 24,11.4 C27.3,11.4 30,14.1 30,17.4 C30,21.6 27.3,25 23.4,25 Z");
        var brush = new LinearGradientBrush(Color.FromRgb(62, 165, 245), Color.FromRgb(17, 101, 202), new Point(0, 0), new Point(0.7, 1));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new ScaleTransform(2, 2));
            drawing.DrawGeometry(brush, null, cloud);
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static string ResolvePath(string path)
    {
        if (path.StartsWith('{'))
        {
            var end = path.IndexOf('}');
            if (end > 0 && Guid.TryParse(path[..(end + 1)], out var folder))
            {
                nint buffer = 0;
                try
                {
                    if (SHGetKnownFolderPath(ref folder, 0, 0, out buffer) == 0 && buffer != 0)
                        path = Path.Combine(Marshal.PtrToStringUni(buffer)!, path[(end + 1)..].TrimStart('\\', '/'));
                }
                finally { if (buffer != 0) Marshal.FreeCoTaskMem(buffer); }
            }
        }
        return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : path;
    }

    private static Dictionary<uint, string> ReadRunningProcesses(IReadOnlyList<Snapshot> snapshots)
    {
        var result = new Dictionary<uint, string>();
        var applications = snapshots.Select(snapshot => snapshot.Application).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!applications.Contains(process.ProcessName)) continue;
                    var path = process.MainModule?.FileName;
                    if (path is not null) result[(uint)process.Id] = Path.GetFullPath(path);
                }
                catch (Exception exception) when (IsReadFailure(exception) || exception is System.ComponentModel.Win32Exception) { }
            }
        }
        return result;
    }

    private static BitmapSource? ExtractApplicationIcon(string path)
    {
        // Packaged app executable resources commonly expose only a generic icon.
        // Their current-install tray snapshot remains preferable to that resource.
        if (path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return null;
        var info = new ShellFileInfo();
        try
        {
            if (SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100 | 0x1) == 0 || info.Icon == 0) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            if (!image.CanFreeze) return null;
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (IsReadFailure(exception)) { return null; }
        finally { if (info.Icon != 0) DestroyIcon(info.Icon); }
    }

    private static byte[]? ReadSnapshot(RegistryKey key)
    {
        uint size = 0;
        if (RegQueryValueExW(key.Handle, "IconSnapshot", 0, out var kind, null, ref size) != 0
            || kind != 3 || size < 24 || size > MaximumSnapshotBytes) return null;
        var bytes = new byte[size];
        if (RegQueryValueExW(key.Handle, "IconSnapshot", 0, out kind, bytes, ref size) != 0 || kind != 3 || size != bytes.Length) return null;
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!bytes.AsSpan(0, 8).SequenceEqual(signature) || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return null;
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        return width is > 0 and <= MaximumDimension && height is > 0 and <= MaximumDimension ? bytes : null;
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1) return null;
            var image = decoder.Frames[0];
            if (image.PixelWidth is <= 0 or > MaximumDimension || image.PixelHeight is <= 0 or > MaximumDimension || !image.CanFreeze) return null;
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (IsReadFailure(exception)) { return null; }
    }

    private static bool IsReadFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or SecurityException or ArgumentException or InvalidOperationException or NotSupportedException or COMException or FormatException;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegQueryValueExW(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string valueName,
        nint reserved, out uint type, byte[]? data, ref uint dataSize);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, nint token, out nint path);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint SHGetFileInfoW(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        internal nint Icon;
        internal int IconIndex;
        internal uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] internal string TypeName;
    }
}
