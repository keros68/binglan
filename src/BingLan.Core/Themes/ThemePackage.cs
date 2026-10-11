using BingLan.Core.Models;

namespace BingLan.Core.Themes;

/// <summary>
/// A shareable theme: visual tokens, component switches, a layout relative to the work
/// area and the dock structure as semantic app slots. It never contains the greeting
/// name, weather city, todos, notes, file mappings, absolute paths or credentials, and
/// it carries no wallpaper, fonts or scripts.
/// </summary>
public sealed class ThemePackage
{
    public ThemeManifest Manifest { get; set; } = new();
    public ThemeTokens Tokens { get; set; } = new();
    public ThemeLayout Layout { get; set; } = new();
    public ThemeBindings Bindings { get; set; } = new();
}

public sealed class ThemeManifest
{
    public const string FormatName = "binglan-theme";

    public string Format { get; set; } = FormatName;
    public int SchemaVersion { get; set; } = ThemeArchive.CurrentSchemaVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string ThemeVersion { get; set; } = "1.0.0";
    public string MinimumAppVersion { get; set; } = "0.1.0";
}

public sealed class ThemeTokens
{
    public List<ThemeComponentTokens> Components { get; set; } = [];

    /// <summary>Desktop-wide style; absent in packages from before it existed.</summary>
    public ThemeStyleTokens? Style { get; set; }
}

/// <summary>
/// Desktop-wide visual tokens: card shadow, card spacing, dock motion and the taskbar
/// material, which is the only blur the app draws.
/// </summary>
public sealed class ThemeStyleTokens
{
    public CardShadow CardShadow { get; set; } = CardShadow.None;
    public double CardSpacing { get; set; } = DesktopStyleRules.DefaultCardSpacing;
    public MotionLevel Motion { get; set; } = MotionLevel.Standard;
    public TaskbarMaterial TaskbarMaterial { get; set; } = TaskbarMaterial.SystemDefault;
}

public enum TaskbarMaterial
{
    SystemDefault,
    Transparent,
    Blur
}

public sealed class ThemeComponentTokens
{
    public DesktopComponentKind Kind { get; set; }
    public bool IsVisible { get; set; } = true;
    public double FontScale { get; set; } = 1d;
    public double CornerRadius { get; set; } = WidgetAppearanceRules.DefaultCornerRadius;
    public DesktopInformationDensity Density { get; set; } = DesktopInformationDensity.Standard;
    public WidgetAppearanceState Appearance { get; set; } = new();
}

public sealed class ThemeLayout
{
    public DesktopLayoutPreset Preset { get; set; } = DesktopLayoutPreset.QuietInformation;

    /// <summary>Component bounds as fractions (0–1) of the primary work area.</summary>
    public List<ThemeComponentPlacement> Components { get; set; } = [];
}

public sealed class ThemeComponentPlacement
{
    public DesktopComponentKind Kind { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class ThemeBindings
{
    public bool DockEnabled { get; set; }
    public DockVisibilityMode DockVisibility { get; set; } = DockVisibilityMode.ReserveWorkArea;
    public List<ThemeAppBinding> Apps { get; set; } = [];

    // The top bar's shareable settings; the monitor it sits on stays machine-local.
    public bool TopBarEnabled { get; set; }
    public TopBarVisibilityMode TopBarVisibility { get; set; } = TopBarVisibilityMode.ReserveTopEdge;
    public TopBarModuleSwitches? TopBarModules { get; set; }
    public bool TopBarFollowCardLook { get; set; } = true;
    public string TopBarSurfaceColor { get; set; } = TopBarState.DefaultSurfaceColor;
    public double TopBarSurfaceOpacity { get; set; } = TopBarState.DefaultSurfaceOpacity;
}

/// <summary>
/// One dock slot. Only the executable file name or app model ID is stored, never a
/// local path; the importing machine resolves it to its own copy of the app.
/// </summary>
public sealed class ThemeAppBinding
{
    public string Slot { get; set; } = AppSlotCatalog.OtherSlot;
    public string DisplayName { get; set; } = string.Empty;
    public string? ExecutableName { get; set; }
    public string? AppUserModelId { get; set; }

    /// <summary>Package entry of a custom dock icon ("icons/N.png"), or null.</summary>
    public string? IconEntry { get; set; }
}
