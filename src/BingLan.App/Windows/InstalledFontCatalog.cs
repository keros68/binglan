using System.Windows;
using System.Windows.Media;
using BingLan.Core.Models;
using FontFamily = System.Windows.Media.FontFamily;

namespace BingLan.App.Windows;

/// <summary>
/// Fonts offered in the font pickers: the open-licence fonts shipped with the app first,
/// then the fonts installed on this computer. Shipped fonts load from the app itself and
/// are never installed into Windows.
/// </summary>
internal static class InstalledFontCatalog
{
    private static readonly Uri BundledFontsUri = new("pack://application:,,,/BingLan;component/Assets/Fonts/");

    // SIL Open Font License 1.1; the licence texts ship next to the program.
    private static readonly (string Name, string Family, FontWeight Weight)[] Bundled =
    [
        ("得意黑", "得意黑", FontWeights.Normal),
        ("Jost 极细", "Jost*", FontWeights.ExtraLight),
        ("Jost 细", "Jost*", FontWeights.Light),
        ("Jost", "Jost*", FontWeights.Normal),
        ("Jost 中粗", "Jost*", FontWeights.Medium),
        ("Quicksand 细", "Quicksand", FontWeights.Light),
        ("Quicksand", "Quicksand", FontWeights.Normal),
        // A Bodoni-style display serif, for titles such as an italic "TODAY".
        ("Abril Fatface 衬线", "Abril Fatface", FontWeights.Normal)
    ];

    private static readonly Lazy<IReadOnlyList<string>> InstalledNames =
        new(LoadInstalledNames);

    public static IReadOnlyList<string> Names => InstalledNames.Value;

    /// <summary>The names of the fonts shipped with the app.</summary>
    public static IReadOnlyList<string> BundledNames { get; } = Bundled.Select(font => font.Name).ToArray();

    // Google Sans used to ship with the app; saved cards and imported themes may still name it.
    private const string LegacyGoogleSansPrefix = "Google Sans";
    private const string LegacyGoogleSansReplacement = "Segoe UI Variable Text";

    private static string? MapLegacyName(string? name) =>
        name is not null && name.StartsWith(LegacyGoogleSansPrefix, StringComparison.OrdinalIgnoreCase)
            ? LegacyGoogleSansReplacement
            : name;

    public static string Resolve(string? requested)
    {
        requested = MapLegacyName(requested);
        var bundledExact = BundledNames.FirstOrDefault(
            name => string.Equals(name, requested, StringComparison.CurrentCultureIgnoreCase));
        if (bundledExact is not null)
        {
            return bundledExact;
        }

        var exact = Names.FirstOrDefault(
            name => string.Equals(name, requested, StringComparison.CurrentCultureIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        foreach (var fallback in new[]
                 {
                     WidgetAppearanceRules.DefaultTitleFontFamily,
                     "Segoe UI Variable Text",
                     "Segoe UI"
                 })
        {
            var installed = Names.FirstOrDefault(
                name => string.Equals(name, fallback, StringComparison.CurrentCultureIgnoreCase));
            if (installed is not null)
            {
                return installed;
            }
        }

        return Names[0];
    }

    /// <summary>The font family for a picker name, loading shipped fonts from the app.</summary>
    public static FontFamily Create(string name)
    {
        name = MapLegacyName(name) ?? name;
        var bundled = Bundled.FirstOrDefault(font => font.Name == name);
        return bundled.Name is null
            ? new FontFamily(name)
            : new FontFamily(BundledFontsUri, "./#" + bundled.Family);
    }

    /// <summary>
    /// Loads a bundled font with a Windows CJK UI fallback for surfaces that mix Latin
    /// display numerals with localized date labels.
    /// </summary>
    internal static FontFamily CreateWithUiFallback(string name)
    {
        var bundled = Bundled.FirstOrDefault(font => font.Name == name);
        return bundled.Name is null
            ? new FontFamily(name)
            : new FontFamily(BundledFontsUri, $"./#{bundled.Family}, Microsoft YaHei UI");
    }

    /// <summary>
    /// The weight a picker name stands for ("Jost 极细" is the thin Jost), or the given
    /// default for fonts without one. Bold text still uses bold.
    /// </summary>
    public static FontWeight WeightOf(string name, FontWeight fallback)
    {
        var bundled = Bundled.FirstOrDefault(font => font.Name == name);
        return bundled.Name is null ? fallback : bundled.Weight;
    }

    private static IReadOnlyList<string> LoadInstalledNames() =>
        BundledNames
            .Concat(Fonts.SystemFontFamilies
                .Select(font => font.Source)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
}
