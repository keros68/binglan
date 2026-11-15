using System.IO;
using System.Text;
using BingLan.Core.Models;

namespace BingLan.Core.Dock;

public enum DockFileDropAction
{
    None,
    PinToDock,
    OpenWithApp
}

public readonly record struct DroppedPathInfo(
    string RawPath,
    string ResolvedPath,
    bool IsDirectory,
    bool IsProgram);

/// <summary>
/// Pure rules for dropping external files onto dock items: decides whether a drop
/// opens the files with the target app or pins items to the dock, quotes command-line
/// paths safely, and tracks the hover delay before raising a running window.
/// </summary>
public static class DockFileDropRules
{
    public static bool IsExecutablePath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && string.Equals(Path.GetExtension(path.Trim()), ".exe", StringComparison.OrdinalIgnoreCase);

    public static bool IsFrameHostPath(string? executablePath) =>
        !string.IsNullOrWhiteSpace(executablePath)
        && string.Equals(
            Path.GetFileName(executablePath.Trim()),
            "ApplicationFrameHost.exe",
            StringComparison.OrdinalIgnoreCase);

    public static bool CanOpenFiles(DockItem? item)
    {
        if (item is null)
        {
            return false;
        }

        if (item.Pinned is { } pinned)
        {
            if (DockPinRules.IsPathPin(pinned))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(pinned.AppUserModelId))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(pinned.ExecutablePath)
                && !IsFrameHostPath(pinned.ExecutablePath))
            {
                return true;
            }
        }

        if (item.Group is { } group)
        {
            return group.Windows.Any(window =>
                !string.IsNullOrWhiteSpace(window.AppUserModelId)
                || (!string.IsNullOrWhiteSpace(window.ExecutablePath)
                    && !IsFrameHostPath(window.ExecutablePath)));
        }

        return false;
    }

    public static IReadOnlyList<string> SelectOpenTargets(IReadOnlyList<DroppedPathInfo> dropped)
    {
        ArgumentNullException.ThrowIfNull(dropped);

        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in dropped)
        {
            if (info.IsProgram || info.IsDirectory || string.IsNullOrWhiteSpace(info.ResolvedPath))
            {
                continue;
            }

            var normalized = WindowGrouping.NormalizeExecutablePath(info.ResolvedPath);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                targets.Add(normalized);
            }
        }

        return targets;
    }

    public static DockFileDropAction Decide(DockItem? target, IReadOnlyList<DroppedPathInfo>? dropped)
    {
        if (dropped is null || dropped.Count == 0)
        {
            return DockFileDropAction.None;
        }

        if (!CanOpenFiles(target))
        {
            return DockFileDropAction.PinToDock;
        }

        return SelectOpenTargets(dropped).Count > 0
            ? DockFileDropAction.OpenWithApp
            : DockFileDropAction.PinToDock;
    }

    /// <summary>
    /// Quotes an argument for Windows CommandLineToArgvW parsing, doubling any
    /// backslashes that immediately precede an internal or closing quotation mark.
    /// </summary>
    public static string QuoteArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        var builder = new StringBuilder(argument.Length + 4);
        builder.Append('"');
        var backslashes = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\')
            {
                backslashes++;
            }
            else if (ch == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
            }
            else
            {
                if (backslashes > 0)
                {
                    builder.Append('\\', backslashes);
                    backslashes = 0;
                }
                builder.Append(ch);
            }
        }

        if (backslashes > 0)
        {
            builder.Append('\\', backslashes * 2);
        }
        builder.Append('"');
        return builder.ToString();
    }

    public static string BuildLaunchArguments(string? baseArguments, IReadOnlyList<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var quotedFiles = string.Join(
            " ",
            filePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(QuoteArgument));
        if (string.IsNullOrWhiteSpace(baseArguments))
        {
            return quotedFiles;
        }

        var trimmedBase = baseArguments.Trim();
        return string.IsNullOrEmpty(quotedFiles)
            ? trimmedBase
            : $"{trimmedBase} {quotedFiles}";
    }
}

/// <summary>
/// Tracks which dock item the user is dragging files over so a running app's window
/// is raised once after a brief hover.
/// </summary>
public sealed class DockDragHoverState
{
    public static readonly TimeSpan RaiseDelay = TimeSpan.FromMilliseconds(500);

    private DateTimeOffset _enteredAt;
    private bool _raised;

    public string? HoveredKey { get; private set; }

    public bool Hover(string? itemKey, bool isRunning, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(itemKey) || !isRunning)
        {
            Clear();
            return false;
        }

        if (!string.Equals(HoveredKey, itemKey, StringComparison.OrdinalIgnoreCase))
        {
            HoveredKey = itemKey;
            _enteredAt = now;
            _raised = false;
            return false;
        }

        if (_raised || now - _enteredAt < RaiseDelay)
        {
            return false;
        }

        _raised = true;
        return true;
    }

    public void Clear()
    {
        HoveredKey = null;
        _enteredAt = default;
        _raised = false;
    }
}
