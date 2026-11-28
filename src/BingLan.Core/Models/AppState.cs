using System.Text.Json;
using System.Text.Json.Serialization;

namespace BingLan.Core.Models;

public sealed class AppState
{
    public const int CurrentSchemaVersion = 70;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// Unknown top-level properties from a newer version of the app, preserved on save so
    /// opening a newer state file does not wipe personal data or drop future settings.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public List<TodoWidgetState> TodoWidgets { get; set; } = [];
    public List<NoteWidgetState> NoteWidgets { get; set; } = [];
    public List<InformationWidgetState> InformationWidgets { get; set; } = [];
    public List<FileBoxState> FileBoxes { get; set; } = [];
    public DesktopExperienceState DesktopExperience { get; set; } =
        DesktopExperienceRules.CreateDefault();
    public DockState Dock { get; set; } = new();
    public bool LauncherEnabled { get; set; } = true;
    public LauncherAppearanceState LauncherAppearance { get; set; } = new();
    public LauncherPreferencesState LauncherPreferences { get; set; } = new();
    public List<LauncherGroupState> LauncherGroups { get; set; } = [];
    public Dictionary<string, LauncherAppCategory> LauncherAppCategoryOverrides { get; set; } = new(StringComparer.Ordinal);
    public TaskbarState Taskbar { get; set; } = new();
    public TopBarState TopBar { get; set; } = new();
    public GlobalHotkeysState Hotkeys { get; set; } = new();
    public DesktopStyleState Style { get; set; } = new();
    public UpdateState Updates { get; set; } = new();

    /// <summary>Whether the first-run guide has been finished or skipped.</summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>
    /// Desktop categories whose box the user deleted; organising leaves their items on the
    /// desktop instead of creating the box again.
    /// </summary>
    public List<DesktopGroupCategory> DismissedDesktopCategories { get; set; } = [];

    /// <summary>The icons in the quick-launch row. Personal: never part of a theme.</summary>
    public List<QuickPlaceState> QuickPlaces { get; set; } = QuickPlaceRules.CreateDefaults();

    /// <summary>Whether the native desktop icons stay hidden while the app runs.</summary>
    public bool CleanDesktopEnabled { get; set; }

    /// <summary>
    /// Whether left double-clicking an empty area of the desktop toggles
    /// <see cref="CleanDesktopEnabled"/>. Machine-local setting, never exported into themes;
    /// null or missing defaults to off.
    /// </summary>
    public bool? DoubleClickDesktopToToggleIcons { get; set; }

    /// <summary>
    /// Whether file boxes watch the desktop and mapped folders: new desktop items join
    /// existing boxes and mappings whose originals were deleted are cleared on their own.
    /// Off by default; while off, organising still clears gone mappings.
    /// </summary>
    public bool FileBoxAutomationEnabled { get; set; }

    /// <summary>
    /// Whether file boxes offer moving an item's original to the Recycle Bin. Off by
    /// default; while off a box only ever removes its mappings and never touches files.
    /// </summary>
    public bool FileBoxDeleteOriginalsEnabled { get; set; }

    /// <summary>
    /// Whether card title-bar buttons stay hidden until the pointer is over the card
    /// (or the card is selected from the keyboard). Off by default.
    /// </summary>
    public bool HeaderToolsOnHover { get; set; }

    /// <summary>
    /// Last page key visited in the settings window. Machine-local preference, never exported
    /// into themes; null defaults to the home page ("Scheme"), legacy "Bars" normalizes to "Dock".
    /// </summary>
    public string? LastSettingsPage { get; set; }

    /// <summary>
    /// Optional per-monitor card profiles for secondary screens. Null while unused so single-screen
    /// profiles stay compact and allocate nothing extra at runtime. Machine-local: never exported
    /// into themes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MultiScreenState? MultiScreen { get; set; }
}
