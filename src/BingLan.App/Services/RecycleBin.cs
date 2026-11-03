using System.IO;
using BingLan.App.Interop;

namespace BingLan.App.Services;

internal enum RecycleResult
{
    Recycled,
    Cancelled,
    Failed
}

/// <summary>
/// Moves a file or folder to the Recycle Bin off the UI thread. Undo is always on; if
/// Windows still cannot recycle the item (for example it is larger than the bin), the
/// nuke warning makes Windows ask before anything is destroyed permanently. Callers
/// refuse removable, network and UNC locations before getting here.
/// </summary>
internal static class RecycleBin
{
    /// <summary>Protected folders a card never deletes, nor any folder that contains one.</summary>
    internal static IReadOnlyList<string> ProtectedFolders()
    {
        var folders = new[]
        {
            Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.CommonDesktopDirectory,
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.CommonApplicationData
        }.Select(Environment.GetFolderPath).ToList();
        if (KnownFolders.GetDownloadsPath() is { } downloads)
        {
            folders.Add(downloads);
        }
        return folders.Where(folder => !string.IsNullOrWhiteSpace(folder)).ToList();
    }

    /// <summary>True for paths on a local fixed disk, where the Recycle Bin is available.</summary>
    internal static bool IsOnFixedLocalDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return !string.IsNullOrEmpty(root)
                && !root.StartsWith(@"\\", StringComparison.Ordinal)
                && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static Task<RecycleResult> SendAsync(string path, nint owner)
    {
        // Shell file operations may show UI (the nuke warning), which wants an STA thread.
        var completion = new TaskCompletionSource<RecycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Send(path, owner));
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static RecycleResult Send(string path, nint owner)
    {
        var operation = new NativeMethods.ShFileOperation
        {
            Window = owner,
            Function = NativeMethods.FoDelete,
            From = path + "\0\0",
            Flags = (ushort)(NativeMethods.FofAllowUndo | NativeMethods.FofNoConfirmation
                | NativeMethods.FofWantNukeWarning | NativeMethods.FofSilent | NativeMethods.FofNoErrorUi)
        };
        var code = NativeMethods.SHFileOperation(ref operation);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return RecycleResult.Recycled;
        }

        return code == 0 && operation.AnyOperationsAborted ? RecycleResult.Cancelled : RecycleResult.Failed;
    }
}
