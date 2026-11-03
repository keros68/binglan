namespace BingLan.Core.Services;

public enum OriginalFileDeleteBlock
{
    None,
    ShellEntry,
    Missing,
    ProtectedLocation,
    NotRecyclable
}

/// <summary>
/// Decides whether a file-box item may have its original moved to the Recycle Bin.
/// Only items that can be restored from the Recycle Bin qualify: removable, network and
/// UNC locations usually delete permanently, so they are refused rather than offered a
/// permanent delete. Drive roots and well-known folders (and folders that contain one,
/// such as the user profile's parent) are never deleted from a card.
/// </summary>
public static class OriginalFileDeleteRules
{
    public static OriginalFileDeleteBlock Check(
        string path,
        bool exists,
        bool onFixedLocalDrive,
        IEnumerable<string> protectedFolders)
    {
        ArgumentNullException.ThrowIfNull(protectedFolders);
        if (FileMappingService.IsShellEntry(path))
        {
            return OriginalFileDeleteBlock.ShellEntry;
        }

        if (!exists)
        {
            return OriginalFileDeleteBlock.Missing;
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return OriginalFileDeleteBlock.ProtectedLocation;
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)
            || string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
        {
            return OriginalFileDeleteBlock.ProtectedLocation;
        }

        foreach (var folder in protectedFolders)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            var guarded = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (string.Equals(guarded, full, StringComparison.OrdinalIgnoreCase)
                || guarded.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return OriginalFileDeleteBlock.ProtectedLocation;
            }
        }

        return onFixedLocalDrive ? OriginalFileDeleteBlock.None : OriginalFileDeleteBlock.NotRecyclable;
    }

    public static string Describe(OriginalFileDeleteBlock block) => block switch
    {
        OriginalFileDeleteBlock.ShellEntry => "系统入口不能删除，可以选择“移除映射”",
        OriginalFileDeleteBlock.Missing => "原文件已不存在，可以选择“移除映射”",
        OriginalFileDeleteBlock.ProtectedLocation => "这是磁盘根目录或系统文件夹，不能从分组盒删除",
        OriginalFileDeleteBlock.NotRecyclable => "此位置无法放入回收站，请在文件管理器中删除",
        _ => string.Empty
    };
}
