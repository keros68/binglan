namespace BingLan.Core.Services;

public enum EditableWidgetTitleKind
{
    Todo,
    Note,
    FileBox
}

public static class WidgetTitleRules
{
    public const string DefaultTodoTitle = "今日待办";
    public const string TodoEmptyTitleFallback = "待办便签";
    public const string DefaultNoteTitle = "便签";
    public const string DefaultFileBoxTitle = "文件收纳盒";
    public const string LegacyDefaultFileBoxTitle = "桌面分组";

    /// <summary>Default title assigned when creating a new card of the given kind.</summary>
    public static string GetDefaultTitle(EditableWidgetTitleKind kind) => kind switch
    {
        EditableWidgetTitleKind.Todo => DefaultTodoTitle,
        EditableWidgetTitleKind.Note => DefaultNoteTitle,
        EditableWidgetTitleKind.FileBox => DefaultFileBoxTitle,
        _ => DefaultFileBoxTitle
    };

    /// <summary>Fallback title used when committing a blank title while renaming a card.</summary>
    public static string GetEmptyFallbackTitle(EditableWidgetTitleKind kind) => kind switch
    {
        EditableWidgetTitleKind.Todo => TodoEmptyTitleFallback,
        EditableWidgetTitleKind.Note => DefaultNoteTitle,
        EditableWidgetTitleKind.FileBox => DefaultFileBoxTitle,
        _ => DefaultFileBoxTitle
    };

    /// <summary>
    /// Normalizes a user-edited title: strips zero-width and Unicode control characters,
    /// trims outer whitespace, and falls back to the card kind's default title when blank.
    /// </summary>
    public static string CommitTitle(string? rawTitle, EditableWidgetTitleKind kind) =>
        CommitTitle(rawTitle, GetEmptyFallbackTitle(kind));

    /// <summary>
    /// Normalizes a user-edited title using an explicit fallback for blank input:
    /// strips zero-width (U+200B–U+200D, U+2060, U+FEFF) and Unicode control characters,
    /// then trims outer whitespace.
    /// </summary>
    public static string CommitTitle(string? rawTitle, string fallbackTitle)
    {
        if (string.IsNullOrEmpty(rawTitle))
        {
            return fallbackTitle;
        }

        var cleaned = StripDisallowedChars(rawTitle).Trim();
        return cleaned.Length == 0 ? fallbackTitle : cleaned;
    }

    /// <summary>
    /// Normalizes a user-edited title and deduplicates against other cards of the same kind
    /// (caller excludes self), appending " 2", " 3", ... when a collision occurs — including
    /// after blank fallback.
    /// </summary>
    public static string CommitUniqueTitle(
        string? rawTitle,
        EditableWidgetTitleKind kind,
        IEnumerable<string>? otherSameKindTitles) =>
        CommitUniqueTitle(rawTitle, GetEmptyFallbackTitle(kind), otherSameKindTitles);

    /// <summary>
    /// Normalizes a user-edited title with an explicit blank fallback and deduplicates
    /// against <paramref name="otherSameKindTitles"/> (excluding self).
    /// </summary>
    public static string CommitUniqueTitle(
        string? rawTitle,
        string fallbackTitle,
        IEnumerable<string>? otherSameKindTitles)
    {
        var committed = CommitTitle(rawTitle, fallbackTitle);
        return otherSameKindTitles is null
            ? committed
            : NextTitle(committed, otherSameKindTitles);
    }

    private static string StripDisallowedChars(string value)
    {
        var needsFilter = false;
        foreach (var ch in value)
        {
            if (IsDisallowedTitleChar(ch))
            {
                needsFilter = true;
                break;
            }
        }

        if (!needsFilter)
        {
            return value;
        }

        var buffer = new char[value.Length];
        var written = 0;
        foreach (var ch in value)
        {
            if (!IsDisallowedTitleChar(ch))
            {
                buffer[written++] = ch;
            }
        }

        return new string(buffer, 0, written);
    }

    private static bool IsDisallowedTitleChar(char ch) =>
        char.IsControl(ch) ||
        ch is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF';

    /// <summary>Whether the title matches an unmodified default file-box title (current or legacy).</summary>
    public static bool IsDefaultFileBoxTitle(string? title) =>
        string.Equals(title, DefaultFileBoxTitle, StringComparison.Ordinal) ||
        string.Equals(title, LegacyDefaultFileBoxTitle, StringComparison.Ordinal);

    /// <summary>
    /// A title for a new card that differs from the existing ones, so cards of the same
    /// kind can be told apart in lists: the default title first, then "标题 2", "标题 3", …
    /// </summary>
    public static string NextTitle(string baseTitle, IEnumerable<string> existingTitles)
    {
        var taken = existingTitles.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!taken.Contains(baseTitle))
        {
            return baseTitle;
        }

        for (var number = 2; ; number++)
        {
            var candidate = $"{baseTitle} {number}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Generates the next non-colliding default title for a newly created card of the given kind.
    /// </summary>
    public static string NextTitle(EditableWidgetTitleKind kind, IEnumerable<string> existingTitles) =>
        NextTitle(GetDefaultTitle(kind), existingTitles);
}
