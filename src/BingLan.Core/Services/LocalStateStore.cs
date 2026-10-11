using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.Core.Services;

public sealed record BackupSummary(string PresetName, int CardCount, int DockAppCount);

public sealed class LocalStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public LocalStateStore(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BingLanWidgets");
    }

    public string DataDirectory { get; }
    public string StatePath => Path.Combine(DataDirectory, "widgets.json");
    public string BackupDirectory => Path.Combine(DataDirectory, "Backups");
    public string PendingRestorePath => Path.Combine(DataDirectory, "widgets.restore.json");

    public AppState Load()
    {
        Directory.CreateDirectory(DataDirectory);
        ApplyPendingRestore();
        if (!File.Exists(StatePath))
        {
            return CreateDefault();
        }

        if (TryLoad(StatePath, out var state) || TryLoad(StatePath + ".bak", out state))
        {
            return state;
        }

        // Neither copy can be read (damaged, or written by a newer version): the file is
        // kept among the backups for restoring later, and the app starts from defaults
        // instead of failing on every start.
        CopyStateToBackup("widgets-unreadable");
        return CreateDefault();
    }

    private static bool TryLoad(string path, [NotNullWhen(true)] out AppState? state)
    {
        state = null;
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            state = DeserializeAndMigrate(File.ReadAllText(path));
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return false;
        }
    }

    public void Save(AppState state)
    {
        MigrateAndNormalize(state);
        Directory.CreateDirectory(DataDirectory);
        var temporaryPath = StatePath + ".tmp";
        var json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(temporaryPath, json);

        // Only a readable state becomes the backup, so a damaged main file can never
        // replace the last good copy it was just recovered from.
        if (IsReadableState(StatePath))
        {
            File.Copy(StatePath, StatePath + ".bak", true);
        }
        File.Move(temporaryPath, StatePath, true);
    }

    public string CreateBackup(AppState state)
    {
        Save(state);
        return CopyStateToBackup("widgets");
    }

    public IReadOnlyList<string> ListBackups()
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(BackupDirectory, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
    }

    /// <summary>
    /// What a backup holds, for telling backups apart in a list: the layout preset and
    /// how many cards and dock apps it has. Null when the file cannot be read.
    /// </summary>
    public static BackupSummary? DescribeBackup(string backupPath)
    {
        try
        {
            var state = DeserializeAndMigrate(File.ReadAllText(backupPath));
            var informationCards = DesktopExperienceRules.ComponentOrder
                .Where(kind => kind is DesktopComponentKind.TimeDate or DesktopComponentKind.Greeting
                    or DesktopComponentKind.Weather or DesktopComponentKind.Performance)
                .Count(kind => state.DesktopExperience.GetComponent(kind).IsVisible);
            var cards = informationCards + state.TodoWidgets.Count + state.NoteWidgets.Count + state.FileBoxes.Count;
            return new BackupSummary(
                DesktopExperienceRules.GetPresetName(state.DesktopExperience.ActivePreset),
                cards,
                state.Dock.PinnedApps.Count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Validates a backup and queues it; it replaces the current state on the next
    /// <see cref="Load"/>, after the running app has saved and exited.
    /// </summary>
    public void ScheduleRestore(string backupPath)
    {
        DeserializeAndMigrate(File.ReadAllText(backupPath));
        Directory.CreateDirectory(DataDirectory);
        File.Copy(backupPath, PendingRestorePath, true);
    }

    private void ApplyPendingRestore()
    {
        if (!File.Exists(PendingRestorePath))
        {
            return;
        }

        try
        {
            if (!TryLoad(PendingRestorePath, out _))
            {
                File.Delete(PendingRestorePath);
                return;
            }
            if (File.Exists(StatePath))
            {
                CopyStateToBackup("widgets-before-restore");
            }
            File.Move(PendingRestorePath, StatePath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The files are busy; the restore stays queued for the next start.
        }
    }

    private string CopyStateToBackup(string prefix)
    {
        Directory.CreateDirectory(BackupDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(BackupDirectory, $"{prefix}-{stamp}.json");
        for (var suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(BackupDirectory, $"{prefix}-{stamp}-{suffix}.json");
        }
        File.Copy(StatePath, path);
        return path;
    }

    public static AppState CreateDefault()
    {
        return new AppState
        {
            InformationWidgets = [new InformationWidgetState()],
            TodoWidgets = [TodoService.CreateDefaultWidget()],
            FileBoxes = [new FileBoxState()]
        };
    }

    private static AppState DeserializeAndMigrate(string json)
    {
        var state = JsonSerializer.Deserialize<AppState>(json, JsonOptions)
            ?? throw new JsonException("状态文件没有内容");
        MigrateAndNormalize(state);
        return state;
    }

    private static bool IsReadableState(string path)
    {
        try
        {
            return File.Exists(path)
                && JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), JsonOptions) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void MigrateAndNormalize(AppState state)
    {
        if (state.SchemaVersion > AppState.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"状态文件版本 {state.SchemaVersion} 高于当前支持版本 {AppState.CurrentSchemaVersion}。");
        }

        // A hand-edited file can hold null lists; every step below expects lists.
        state.TodoWidgets ??= [];
        state.NoteWidgets ??= [];
        state.InformationWidgets ??= [];
        state.FileBoxes ??= [];

        if (state.SchemaVersion < 2)
        {
            foreach (var todo in state.TodoWidgets)
            {
                todo.CornerRadius = WidgetAppearanceRules.DefaultCornerRadius;
            }
            foreach (var box in state.FileBoxes)
            {
                box.CornerRadius = WidgetAppearanceRules.DefaultCornerRadius;
            }
            state.SchemaVersion = 2;
        }

        if (state.SchemaVersion < 3)
        {
            foreach (var todo in state.TodoWidgets)
            {
                MigrateLegacyLayeredPlacement(todo.Placement);
            }
            foreach (var box in state.FileBoxes)
            {
                MigrateLegacyLayeredPlacement(box.Placement);
            }
            state.SchemaVersion = 3;
        }

        if (state.SchemaVersion < 4)
        {
            foreach (var todo in state.TodoWidgets)
            {
                todo.Appearance ??= new WidgetAppearanceState();
            }
            foreach (var box in state.FileBoxes)
            {
                box.Appearance ??= new WidgetAppearanceState();
            }
            state.SchemaVersion = 4;
        }

        if (state.SchemaVersion < 5)
        {
            foreach (var todo in state.TodoWidgets)
            {
                todo.Appearance ??= new WidgetAppearanceState();
                todo.Appearance.TitleFontFamily = WidgetAppearanceRules.CoerceTitleFontFamily(
                    todo.Appearance.TitleFontFamily);
            }
            foreach (var box in state.FileBoxes)
            {
                box.Appearance ??= new WidgetAppearanceState();
                box.Appearance.TitleFontFamily = WidgetAppearanceRules.CoerceTitleFontFamily(
                    box.Appearance.TitleFontFamily);
            }
            state.SchemaVersion = 5;
        }

        if (state.SchemaVersion < 6)
        {
            state.SchemaVersion = 6;
        }

        if (state.SchemaVersion < 7)
        {
            state.SchemaVersion = 7;
        }

        if (state.SchemaVersion < 8)
        {
            state.SchemaVersion = 8;
        }

        if (state.SchemaVersion < 9)
        {
            state.SchemaVersion = 9;
        }

        if (state.SchemaVersion < 10)
        {
            state.DesktopExperience ??= DesktopExperienceRules.CreateDefault();
            state.SchemaVersion = 10;
        }

        if (state.SchemaVersion < 11)
        {
            state.DesktopExperience ??= DesktopExperienceRules.CreateDefault();
            DesktopExperienceRules.Normalize(state.DesktopExperience);
            var weather = state.DesktopExperience.GetComponent(DesktopComponentKind.Weather);
            if (weather.Placement.Height <= 118.5d)
            {
                weather.Placement.Height = 150d;
            }
            var performance = state.DesktopExperience
                .GetComponent(DesktopComponentKind.Performance);
            if (performance.Placement.Height <= 96.5d)
            {
                performance.Placement.Height = 126d;
            }
            var greeting = state.DesktopExperience
                .GetComponent(DesktopComponentKind.Greeting);
            if (greeting.Appearance.TextColor == WidgetAppearanceRules.DefaultTextColor)
            {
                greeting.Appearance.TextColor = "#FFFFFF";
            }
            var todo = state.DesktopExperience.GetComponent(DesktopComponentKind.Todo);
            if (todo.Placement.Top <= 520.5d)
            {
                todo.Placement.Top = 570d;
            }
            state.DesktopExperience
                .GetComponent(DesktopComponentKind.QuickLaunch)
                .IsVisible = false;
            state.SchemaVersion = 11;
        }

        if (state.SchemaVersion < 12)
        {
            state.DesktopExperience ??= DesktopExperienceRules.CreateDefault();
            DesktopExperienceRules.Normalize(state.DesktopExperience);
            var timeDate = state.DesktopExperience
                .GetComponent(DesktopComponentKind.TimeDate);
            if (timeDate.Appearance.TextColor == WidgetAppearanceRules.DefaultTextColor)
            {
                timeDate.Appearance.TextColor = "#FFFFFF";
            }
            state.SchemaVersion = 12;
        }

        if (state.SchemaVersion < 13)
        {
            state.Dock ??= new DockState();
            state.SchemaVersion = 13;
        }

        if (state.SchemaVersion < 14)
        {
            state.Taskbar ??= new TaskbarState();
            state.SchemaVersion = 14;
        }

        if (state.SchemaVersion < 15)
        {
            // People upgrading already set up their desktop; only new installs see the guide.
            state.OnboardingCompleted = true;
            state.SchemaVersion = 15;
        }

        if (state.SchemaVersion < 16)
        {
            // Clean desktop is off until the user turns it on.
            state.CleanDesktopEnabled = false;
            state.SchemaVersion = 16;
        }

        if (state.SchemaVersion < 17)
        {
            // Earlier versions had no desktop-wide style; the defaults match how they looked.
            state.Style ??= new DesktopStyleState();
            state.SchemaVersion = 17;
        }

        if (state.SchemaVersion < 18)
        {
            // Dock icon size and the one-time taskbar import are new; the defaults keep the
            // earlier look, and a dock that is still empty gets the taskbar's apps once.
            state.Dock ??= new DockState();
            state.Dock.IconSize = DockState.DefaultIconSize;
            // The time and greeting are plain text on the wallpaper; their faint 8 % backing
            // showed as a pale box, so the untouched default becomes invisible.
            state.DesktopExperience ??= DesktopExperienceRules.CreateDefault();
            DesktopExperienceRules.Normalize(state.DesktopExperience);
            foreach (var kind in new[] { DesktopComponentKind.TimeDate, DesktopComponentKind.Greeting })
            {
                var appearance = state.DesktopExperience.GetComponent(kind).Appearance;
                if (Math.Abs(appearance.BackgroundOpacity - 0.08d) < 0.001d)
                {
                    appearance.BackgroundOpacity = WidgetAppearanceRules.MinimumBackgroundOpacity;
                }
            }
            state.SchemaVersion = 18;
        }

        if (state.SchemaVersion < 19)
        {
            // A custom text colour, an accent colour, the time card's line, the list of
            // deleted category boxes, folding a file box and the editable quick-launch
            // icons are new; all start at what showed before.
            state.SchemaVersion = 19;
        }

        if (state.SchemaVersion < 20)
        {
            // The list of apps the dock leaves out is new and starts empty.
            state.SchemaVersion = 20;
        }

        if (state.SchemaVersion < 21)
        {
            // Update checking is new and starts on: one check a day, only to say a release exists.
            state.SchemaVersion = 21;
        }

        if (state.SchemaVersion < 22)
        {
            // Cards remember a position per arrangement of monitors; the list starts empty
            // and fills on the arrangement in use.
            state.SchemaVersion = 22;
        }

        if (state.SchemaVersion < 23)
        {
            // A box may show its mappings as a compact list; existing boxes keep tiles.
            state.SchemaVersion = 23;
        }

        if (state.SchemaVersion < 24)
        {
            // File-box automation is new and starts off: organising keeps clearing gone
            // mappings, and nothing is watched until the user opts in.
            state.SchemaVersion = 24;
        }

        if (state.SchemaVersion < 25)
        {
            // The dock's bottom gap becomes adjustable and a maximized window may be
            // allowed to take the reserved strip; both defaults keep the previous look
            // and behaviour.
            state.SchemaVersion = 25;
        }

        if (state.SchemaVersion < 26)
        {
            // The handle shown while the dock is hidden starts in the default corner
            // and only moves when the user drags it.
            state.SchemaVersion = 26;
        }

        if (state.SchemaVersion < 27)
        {
            // The top bar is new and starts off; nobody's desktop changes on upgrade.
            state.SchemaVersion = 27;
        }
        if (state.SchemaVersion < 28)
        {
            // The top bar gains an adjustable height; a missing value falls back to
            // the default in TopBarRules.Normalize, nothing else has to move.
            state.SchemaVersion = 28;
        }
        state.Updates ??= new UpdateState();
        state.QuickPlaces = QuickPlaceRules.Normalize(state.QuickPlaces);
        state.DismissedDesktopCategories = (state.DismissedDesktopCategories ?? [])
            .Where(category => category is not DesktopGroupCategory.None && Enum.IsDefined(category))
            .Distinct()
            .ToList();

        state.Style ??= new DesktopStyleState();
        DesktopStyleRules.Normalize(state.Style);

        foreach (var todo in state.TodoWidgets)
        {
            todo.Appearance ??= new WidgetAppearanceState();
            todo.Appearance.TitleFontFamily = WidgetAppearanceRules.CoerceTitleFontFamily(
                todo.Appearance.TitleFontFamily);
            todo.Appearance.TextColor = WidgetAppearanceRules.CoerceTextColor(
                todo.Appearance.TextColor);
            todo.CornerRadius = WidgetAppearanceRules.CoerceCornerRadius(todo.CornerRadius);
        }
        foreach (var box in state.FileBoxes)
        {
            box.Appearance ??= new WidgetAppearanceState();
            box.Appearance.TitleFontFamily = WidgetAppearanceRules.CoerceTitleFontFamily(
                box.Appearance.TitleFontFamily);
            box.Appearance.TextColor = WidgetAppearanceRules.CoerceTextColor(
                box.Appearance.TextColor);
            box.CornerRadius = WidgetAppearanceRules.CoerceCornerRadius(box.CornerRadius);
            if (!Enum.IsDefined(box.DesktopCategory))
            {
                box.DesktopCategory = DesktopGroupCategory.None;
            }
            box.CollectExtensions = FileMappingService.NormalizeExtensions(box.CollectExtensions);
        }
        foreach (var note in state.NoteWidgets)
        {
            NoteService.CommitTitle(note, note.Title);
            NoteService.CommitContent(note, note.Content);
            note.Appearance ??= new WidgetAppearanceState();
            note.Appearance.TitleFontFamily = WidgetAppearanceRules.CoerceTitleFontFamily(
                note.Appearance.TitleFontFamily);
            note.Appearance.TextColor = WidgetAppearanceRules.CoerceTextColor(
                note.Appearance.TextColor);
            note.CornerRadius = WidgetAppearanceRules.CoerceCornerRadius(note.CornerRadius);
            note.BodyTypography ??= new NoteBodyTypography();
            note.BodyTypography.FontFamily = NoteBodyTypographyRules.CoerceFontFamily(
                note.BodyTypography.FontFamily);
            note.BodyTypography.FontSize = NoteBodyTypographyRules.CoerceFontSize(
                note.BodyTypography.FontSize);
            note.BodyTypography.Color = NoteBodyTypographyRules.CoerceColor(
                note.BodyTypography.Color);
            if (!Enum.IsDefined(note.BodyTypography.Alignment))
            {
                note.BodyTypography.Alignment = NoteBodyTextAlignment.Left;
            }
        }
        foreach (var information in state.InformationWidgets)
        {
            information.Title = string.IsNullOrWhiteSpace(information.Title)
                ? "桌面信息"
                : information.Title.Trim();
            information.GreetingName = information.GreetingName?.Trim() ?? string.Empty;
            information.WeatherCity = information.WeatherCity?.Trim() ?? string.Empty;
            information.Appearance ??= new WidgetAppearanceState();
            information.Appearance.TitleFontFamily = WidgetAppearanceRules.CoerceTitleFontFamily(
                information.Appearance.TitleFontFamily);
            information.Appearance.TextColor = WidgetAppearanceRules.CoerceTextColor(
                information.Appearance.TextColor);
            information.CornerRadius = WidgetAppearanceRules.CoerceCornerRadius(
                information.CornerRadius);
        }

        state.DesktopExperience ??= DesktopExperienceRules.CreateDefault();
        DesktopExperienceRules.Normalize(state.DesktopExperience);
        state.Dock ??= new DockState();
        DockPinRules.Normalize(state.Dock);
        state.TopBar ??= new TopBarState();
        TopBarRules.Normalize(state.TopBar);
        state.Taskbar ??= new TaskbarState();
        if (!Enum.IsDefined(state.Taskbar.Mode))
        {
            state.Taskbar.Mode = TaskbarMode.SystemDefault;
        }

        state.SchemaVersion = AppState.CurrentSchemaVersion;
    }

    private static void MigrateLegacyLayeredPlacement(WindowPlacement placement)
    {
        const double legacyShadowInset = 12d;
        const double legacyShadowInsets = legacyShadowInset * 2d;

        placement.Left += legacyShadowInset;
        placement.Top += legacyShadowInset;
        placement.Width = Math.Max(1d, placement.Width - legacyShadowInsets);
        placement.Height = Math.Max(1d, placement.Height - legacyShadowInsets);
    }
}
