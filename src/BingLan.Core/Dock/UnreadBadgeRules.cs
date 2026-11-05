namespace BingLan.Core.Dock;

/// <summary>
/// Reads an unread count an app chose to publish at the end of a window title,
/// such as "微信(3)" or "Name [12]". A flash with a count shows the number on the
/// dock; one without stays a plain attention dot. Multi-instance titles like
/// "记事本 (2) - 记事本" do not qualify: only a trailing bracket counts.
/// </summary>
public static class UnreadBadgeRules
{
    /// <summary>Parses the count from a window title; null when it publishes none.</summary>
    public static int? ParseCount(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var span = title.AsSpan().TrimEnd();
        if (TrailingBracketCount(span, '(', ')') is { } halfWidth)
        {
            return halfWidth;
        }
        if (TrailingBracketCount(span, '（', '）') is { } fullWidth)
        {
            return fullWidth;
        }
        if (TrailingBracketCount(span, '[', ']') is { } square)
        {
            return square;
        }

        return null;
    }

    /// <summary>The badge text: 99+ past the cap, matching the dock's small badge.</summary>
    public static string BadgeText(int count) => count > 99 ? "99+" : count.ToString();

    private static int? TrailingBracketCount(ReadOnlySpan<char> span, char open, char close)
    {
        if (span.IsEmpty || span[^1] != close)
        {
            return null;
        }

        var opened = span.LastIndexOf(open);
        if (opened < 0)
        {
            return null;
        }

        var digits = span[(opened + 1)..^1];
        var count = 0;
        if (digits.IsEmpty || digits.Length > 4)
        {
            return null;
        }

        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return null;
            }
            count = count * 10 + (c - '0');
        }

        return count > 0 ? count : null;
    }
}
