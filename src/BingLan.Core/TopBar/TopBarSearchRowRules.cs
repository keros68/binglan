namespace BingLan.Core.TopBar;

/// <summary>Where one row of the top bar's inline search comes from.</summary>
public enum TopBarSearchRowKind
{
    /// <summary>A file or folder on this machine that the row opens directly.</summary>
    Local,

    /// <summary>The browser fallback that answers what the file index cannot.</summary>
    Browser
}

/// <summary>
/// One row the inline search shows: a local candidate to open, or the browser fallback.
/// Pure data — the window only renders what the rules return.
/// </summary>
public sealed record TopBarSearchRow(
    TopBarSearchRowKind Kind,
    string Title,
    string Subtitle,
    TopBarSearchCandidate? Local,
    string? BrowserUrl);

/// <summary>
/// Row composition for the top bar's inline search: local matches first, the browser
/// fallback last. The bar promises "local first, and only the browser entry when nothing
/// local matches", so an empty local result still yields a usable row instead of an
/// empty panel. Pure and thread-safe.
/// </summary>
public static class TopBarSearchRowRules
{
    /// <summary>The engine the fallback row opens; the template must carry one <c>{0}</c>.</summary>
    public const string DefaultBrowserName = "百度";

    public const string DefaultBrowserUrlTemplate = "https://www.baidu.com/s?wd={0}";

    /// <summary>
    /// Longest query echoed in the row title. The whole query still goes into the URL —
    /// only the label is shortened, because a pasted sentence would push the row to two
    /// lines and hide the results above it.
    /// </summary>
    public const int BrowserTitleQueryLimit = 32;

    /// <summary>
    /// Local candidates become one row each, then a single browser row is appended when
    /// the query is not blank. A blank query keeps the local rows only: an empty box must
    /// not offer to search the web for nothing.
    /// </summary>
    public static IReadOnlyList<TopBarSearchRow> Compose(
        IEnumerable<TopBarSearchCandidate>? local,
        string? query,
        string browserName = DefaultBrowserName,
        string browserUrlTemplate = DefaultBrowserUrlTemplate)
    {
        var rows = new List<TopBarSearchRow>();
        foreach (var candidate in local ?? [])
        {
            rows.Add(new TopBarSearchRow(
                TopBarSearchRowKind.Local,
                candidate.DisplayName,
                TopBarSearchRules.FormatLocation(candidate.Path),
                candidate,
                null));
        }

        var text = query?.Trim() ?? string.Empty;
        if (text.Length == 0
            || string.IsNullOrWhiteSpace(browserName)
            || string.IsNullOrWhiteSpace(browserUrlTemplate))
        {
            return rows;
        }

        var shown = text.Length > BrowserTitleQueryLimit
            ? text[..BrowserTitleQueryLimit] + "…"
            : text;
        rows.Add(new TopBarSearchRow(
            TopBarSearchRowKind.Browser,
            $"用{browserName}搜索「{shown}」",
            "在默认浏览器中打开",
            null,
            BuildBrowserUrl(browserUrlTemplate, text)));
        return rows;
    }

    /// <summary>
    /// Escapes the query for a URL. A template without the placeholder is returned as-is,
    /// so a mistyped setting degrades to opening that address instead of throwing inside
    /// the search path.
    /// </summary>
    public static string BuildBrowserUrl(string template, string query)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        var escaped = Uri.EscapeDataString((query ?? string.Empty).Trim());
        return template.Contains("{0}", StringComparison.Ordinal)
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, template, escaped)
            : template;
    }

    /// <summary>
    /// Clamps the popup's horizontal origin in physical pixels so it stays centered under
    /// <paramref name="anchorX"/> while remaining inside the target monitor's working area.
    /// When the working area is narrower than the popup, aligns to the left edge.
    /// </summary>
    public static int ClampHorizontalPixels(int anchorX, int widthPx, int workLeft, int workRight, int marginPx)
    {
        var minLeft = workLeft + marginPx;
        var maxLeft = workRight - widthPx - marginPx;
        if (maxLeft < minLeft)
        {
            return workLeft;
        }

        var desired = anchorX - widthPx / 2;
        return Math.Clamp(desired, minLeft, maxLeft);
    }
}
