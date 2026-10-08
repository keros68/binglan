namespace BingLan.Core.Models;

public sealed class AppState
{
    public const int CurrentSchemaVersion = 23;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<TodoWidgetState> TodoWidgets { get; set; } = [];
    public List<NoteWidgetState> NoteWidgets { get; set; } = [];
    public List<InformationWidgetState> InformationWidgets { get; set; } = [];
    public List<FileBoxState> FileBoxes { get; set; } = [];
    public DesktopExperienceState DesktopExperience { get; set; } =
        DesktopExperienceRules.CreateDefault();
    public DockState Dock { get; set; } = new();
    public TaskbarState Taskbar { get; set; } = new();
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
    /// Whether file boxes watch the desktop and mapped folders: new desktop items join
    /// existing boxes and mappings whose originals were deleted are cleared on their own.
    /// Off by default; while off, organising still clears gone mappings.
    /// </summary>
    public bool FileBoxAutomationEnabled { get; set; }
}
