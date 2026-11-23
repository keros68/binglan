using System.Globalization;

namespace BingLan.Core.Services;

/// <summary>
/// Resolved icon target declared inside a Windows Internet Shortcut (<c>.url</c>) file,
/// such as desktop shortcuts created by Steam games (<c>IconFile=...ico</c>, <c>IconIndex=0</c>).
/// </summary>
public readonly record struct InternetShortcutIcon(string FilePath, int Index);

/// <summary>
/// Pure parser for <c>.url</c> shortcut icon metadata. Reads only text lines under
/// <c>[InternetShortcut]</c>; never executes code or touches network UNC paths.
/// </summary>
public static class InternetShortcutRules
{
    public const int MaximumShortcutBytes = 64 * 1024;

    public static InternetShortcutIcon? ParseIconLocation(string? content, string? shortcutDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        string? rawFile = null;
        int? explicitIndex = null;
        var inSection = false;

        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                inSection = line.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Equals("IconFile", StringComparison.OrdinalIgnoreCase))
            {
                rawFile = value;
            }
            else if (key.Equals("IconIndex", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIndex))
            {
                explicitIndex = parsedIndex;
            }
        }

        if (string.IsNullOrWhiteSpace(rawFile))
        {
            return null;
        }

        var pathPart = rawFile.Trim();
        var inlineIndex = 0;
        var hasInlineIndex = false;
        if (pathPart.Length >= 2 && pathPart[0] == '"')
        {
            var closingQuote = pathPart.IndexOf('"', 1);
            if (closingQuote > 1)
            {
                var trailing = pathPart[(closingQuote + 1)..].Trim();
                pathPart = pathPart[1..closingQuote].Trim();
                if (trailing.StartsWith(',')
                    && int.TryParse(trailing[1..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var trailingIdx))
                {
                    inlineIndex = trailingIdx;
                    hasInlineIndex = true;
                }
            }
            else
            {
                pathPart = pathPart.Trim('"').Trim();
            }
        }
        else
        {
            var lastComma = pathPart.LastIndexOf(',');
            if (lastComma > 0
                && int.TryParse(pathPart[(lastComma + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var suffixIdx))
            {
                pathPart = pathPart[..lastComma].Trim().Trim('"');
                inlineIndex = suffixIdx;
                hasInlineIndex = true;
            }
        }

        if (string.IsNullOrWhiteSpace(pathPart))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(pathPart).Trim().Trim('"');
        if (expanded.Length == 0 || expanded.StartsWith(@"\\", StringComparison.Ordinal) || expanded.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.IsPathRooted(expanded)
                ? Path.GetFullPath(expanded)
                : !string.IsNullOrWhiteSpace(shortcutDirectory)
                    ? Path.GetFullPath(Path.Combine(shortcutDirectory, expanded))
                    : string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(fullPath) || fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        var index = explicitIndex ?? (hasInlineIndex ? inlineIndex : 0);
        return new InternetShortcutIcon(fullPath, index);
    }
}
