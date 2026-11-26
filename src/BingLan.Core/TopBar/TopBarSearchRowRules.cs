using System.Net;
using System.Net.Sockets;

namespace BingLan.Core.TopBar;

/// <summary>Where one row of the top bar's inline search comes from.</summary>
public enum TopBarSearchRowKind
{
    /// <summary>A file or folder on this machine that the row opens directly.</summary>
    Local,

    /// <summary>The browser action (direct URL or search engine fallback) that opens in the default browser.</summary>
    Browser
}

/// <summary>Resolved search engine preset or custom template for the inline search popup.</summary>
public sealed record TopBarSearchEngineInfo(
    string Key,
    string DisplayName,
    string UrlTemplate);

/// <summary>
/// One row the inline search shows: a local candidate to open, or a browser action.
/// Pure data — the window only renders what the rules return.
/// </summary>
public sealed record TopBarSearchRow(
    TopBarSearchRowKind Kind,
    string Title,
    string Subtitle,
    TopBarSearchCandidate? Local,
    string? BrowserUrl);

/// <summary>
/// Row composition for the top bar's inline search: local matches first, followed by either a
/// direct-website action (when the query looks like a URL) or a default-browser search engine
/// fallback. Pure and thread-safe.
/// </summary>
public static class TopBarSearchRowRules
{
    public const string EngineBing = "bing";
    public const string EngineGoogle = "google";
    public const string EngineBaidu = "baidu";
    public const string EngineCustom = "custom";

    /// <summary>The default engine the fallback row opens; the template must carry <c>{0}</c>.</summary>
    public const string DefaultBrowserName = "必应";

    public const string DefaultBrowserUrlTemplate = "https://www.bing.com/search?q={0}";

    public static TopBarSearchEngineInfo BingEngine { get; } =
        new(EngineBing, DefaultBrowserName, DefaultBrowserUrlTemplate);

    public static TopBarSearchEngineInfo GoogleEngine { get; } =
        new(EngineGoogle, "Google", "https://www.google.com/search?q={0}");

    public static TopBarSearchEngineInfo BaiduEngine { get; } =
        new(EngineBaidu, "百度", "https://www.baidu.com/s?wd={0}");

    /// <summary>
    /// Longest query echoed in the row title. The whole query still goes into the URL —
    /// only the label is shortened, because a pasted sentence would push the row to two
    /// lines and hide the results above it.
    /// </summary>
    public const int BrowserTitleQueryLimit = 32;

    /// <summary>
    /// Recognized web TLDs for scheme-less direct URL detection. Purposely excludes common
    /// Windows file extensions (.txt, .md, .cs, .js, .py, .sh, .exe, .pdf, etc.) so local
    /// filenames stay local/search queries instead of being misclassified as websites.
    /// </summary>
    private static readonly HashSet<string> KnownWebTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "cn", "net", "org", "io", "dev", "app", "ai", "gov", "edu",
        "co", "me", "tv", "gg", "xyz", "top", "info", "biz",
        "uk", "jp", "de", "fr", "kr", "tw", "hk", "sg", "ru", "ca", "au", "eu", "us",
        "to", "fm", "ly", "be", "ch", "nl", "se",
        "pro", "tech", "site", "online", "store", "cloud", "blog", "wiki", "page", "link",
        "live", "news", "work", "run", "moe"
    };

    /// <summary>Normalizes a persisted engine key to one of the four known identifiers.</summary>
    public static string NormalizeEngineKey(string? engineKey)
    {
        var key = engineKey?.Trim();
        if (string.Equals(key, EngineGoogle, StringComparison.OrdinalIgnoreCase))
        {
            return EngineGoogle;
        }
        if (string.Equals(key, EngineBaidu, StringComparison.OrdinalIgnoreCase))
        {
            return EngineBaidu;
        }
        if (string.Equals(key, EngineCustom, StringComparison.OrdinalIgnoreCase))
        {
            return EngineCustom;
        }
        return EngineBing;
    }

    /// <summary>
    /// Validates a user-supplied search engine URL template: must contain <c>{0}</c>, keep the
    /// placeholder out of the hostname, and produce a valid absolute <c>http</c> or <c>https</c>
    /// URI even for queries containing spaces, punctuation, and non-ASCII characters.
    /// </summary>
    public static bool IsValidCustomTemplate(string? template)
    {
        var trimmed = template?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !trimmed.Contains("{0}", StringComparison.Ordinal))
        {
            return false;
        }

        var simpleProbe = BuildBrowserUrl(trimmed, "alpha");
        var complexProbe = BuildBrowserUrl(trimmed, "hello world&a=1 测试");
        if (!Uri.TryCreate(simpleProbe, UriKind.Absolute, out var simpleUri)
            || !Uri.TryCreate(complexProbe, UriKind.Absolute, out var complexUri))
        {
            return false;
        }

        return (simpleUri.Scheme == Uri.UriSchemeHttp || simpleUri.Scheme == Uri.UriSchemeHttps)
            && complexUri.Scheme == simpleUri.Scheme
            && !string.IsNullOrEmpty(simpleUri.Host)
            && string.Equals(simpleUri.Host, complexUri.Host, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(simpleUri.UserInfo)
            && string.IsNullOrEmpty(complexUri.UserInfo)
            && !string.Equals(
                simpleUri.PathAndQuery + simpleUri.Fragment,
                complexUri.PathAndQuery + complexUri.Fragment,
                StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves the effective search engine from saved settings. Invalid or empty custom
    /// templates fall back cleanly to Bing so the search bar is always usable.
    /// </summary>
    public static TopBarSearchEngineInfo ResolveEngine(string? engineKey, string? customUrlTemplate)
    {
        return NormalizeEngineKey(engineKey) switch
        {
            EngineGoogle => GoogleEngine,
            EngineBaidu => BaiduEngine,
            EngineCustom when IsValidCustomTemplate(customUrlTemplate) =>
                new TopBarSearchEngineInfo(EngineCustom, "自定义引擎", customUrlTemplate!.Trim()),
            _ => BingEngine
        };
    }

    /// <summary>
    /// Detects whether <paramref name="input"/> is a direct web address (explicit <c>http(s)://</c>,
    /// <c>localhost[:port]</c>, IPv4 address, or a bare domain with a recognized web TLD) and
    /// returns the normalized absolute URL plus a compact host label for display.
    /// </summary>
    public static bool TryNormalizeDirectUrl(string? input, out string absoluteUrl, out string displayHost)
    {
        absoluteUrl = string.Empty;
        displayHost = string.Empty;

        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Any(char.IsControl))
        {
            return false;
        }

        // Check explicit http(s):// before rejecting whitespace so pasted intranet/SharePoint
        // links with spaces in the path or query stay direct URLs instead of leaking to a search engine.
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(text, UriKind.Absolute, out var explicitUri)
                && (explicitUri.Scheme == Uri.UriSchemeHttp || explicitUri.Scheme == Uri.UriSchemeHttps)
                && !string.IsNullOrEmpty(explicitUri.Host)
                && explicitUri.HostNameType != UriHostNameType.Unknown)
            {
                absoluteUrl = explicitUri.AbsoluteUri;
                displayHost = FormatDisplayHost(explicitUri);
                return true;
            }
            return false;
        }

        if (text.Any(char.IsWhiteSpace)
            || text.Contains('\\', StringComparison.Ordinal)
            || text.Contains("://", StringComparison.Ordinal)
            || text.StartsWith('.')
            || text.StartsWith('/'))
        {
            return false;
        }

        if (IsLocalhostAddress(text))
        {
            if (Uri.TryCreate("http://" + text, UriKind.Absolute, out var localUri)
                && localUri.Scheme == Uri.UriSchemeHttp
                && string.Equals(localUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(localUri.UserInfo))
            {
                absoluteUrl = localUri.AbsoluteUri;
                displayHost = FormatDisplayHost(localUri);
                return true;
            }
            return false;
        }

        if (!Uri.TryCreate("https://" + text, UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(candidate.UserInfo))
        {
            return false;
        }

        if (candidate.HostNameType == UriHostNameType.IPv4)
        {
            var rawHost = ExtractRawHost(text);
            if (rawHost.Count(c => c == '.') == 3
                && IPAddress.TryParse(rawHost, out var ip)
                && ip.AddressFamily == AddressFamily.InterNetwork)
            {
                if (Uri.TryCreate("http://" + text, UriKind.Absolute, out var ipUri)
                    && ipUri.Scheme == Uri.UriSchemeHttp
                    && string.IsNullOrEmpty(ipUri.UserInfo))
                {
                    absoluteUrl = ipUri.AbsoluteUri;
                    displayHost = FormatDisplayHost(ipUri);
                    return true;
                }
            }
            return false;
        }

        if (candidate.HostNameType != UriHostNameType.Dns)
        {
            return false;
        }

        var host = candidate.Host;
        var labels = host.Split('.');
        if (labels.Length < 2 || labels.Any(label => label.Length == 0))
        {
            return false;
        }

        var tld = labels[^1];
        if (!KnownWebTlds.Contains(tld))
        {
            return false;
        }

        absoluteUrl = candidate.AbsoluteUri;
        displayHost = FormatDisplayHost(candidate);
        return true;
    }

    public static IReadOnlyList<TopBarSearchRow> Compose(
        IEnumerable<TopBarSearchCandidate>? local,
        string? query,
        TopBarSearchEngineInfo engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return Compose(local, query, engine.DisplayName, engine.UrlTemplate);
    }

    /// <summary>
    /// Local candidates come first. When local matches exist, no web-search fallback row is
    /// added (explicit <c>http(s)://</c> URLs still surface as a direct website action). When
    /// nothing local matches, a direct-website row or default-browser search row is returned.
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
            var subtitle = candidate.Kind == TopBarSearchItemKind.App
                && candidate.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                ? "系统或商店应用"
                : TopBarSearchRules.FormatLocation(candidate.Path);
            rows.Add(new TopBarSearchRow(
                TopBarSearchRowKind.Local,
                candidate.DisplayName,
                subtitle,
                candidate,
                null));
        }

        var text = query?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return rows;
        }

        if (HasExplicitHttpScheme(text))
        {
            if (TryNormalizeDirectUrl(text, out var explicitUrl, out var explicitHost))
            {
                rows.Add(new TopBarSearchRow(
                    TopBarSearchRowKind.Browser,
                    $"打开网站 {explicitHost}",
                    $"{explicitUrl} · 在默认浏览器中打开",
                    null,
                    explicitUrl));
            }
            return rows;
        }

        if (rows.Count > 0)
        {
            return rows;
        }

        if (TryNormalizeDirectUrl(text, out var directUrl, out var displayHost))
        {
            rows.Add(new TopBarSearchRow(
                TopBarSearchRowKind.Browser,
                $"打开网站 {displayHost}",
                $"{directUrl} · 在默认浏览器中打开",
                null,
                directUrl));
            return rows;
        }

        if (string.IsNullOrWhiteSpace(browserName)
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
    /// Escapes the query for a URL. A template without <c>{0}</c> is returned as-is, and stray
    /// braces elsewhere in the template never throw.
    /// </summary>
    public static string BuildBrowserUrl(string template, string query)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        var escaped = Uri.EscapeDataString((query ?? string.Empty).Trim());
        return template.Contains("{0}", StringComparison.Ordinal)
            ? template.Replace("{0}", escaped, StringComparison.Ordinal)
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

    public static bool HasExplicitHttpScheme(string? input)
    {
        var text = input?.Trim();
        return text is { Length: > 0 }
            && (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLocalhostAddress(string text)
    {
        if (!text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return text.Length == 9 || text[9] is ':' or '/' or '?' or '#';
    }

    private static string ExtractRawHost(string text)
    {
        var end = text.IndexOfAny(['/', '?', '#', ':']);
        return end < 0 ? text : text[..end];
    }

    private static string FormatDisplayHost(Uri uri) =>
        uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
}
