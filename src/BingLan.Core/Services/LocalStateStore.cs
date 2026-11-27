using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.Core.Services;

public sealed record BackupSummary(string PresetName, int CardCount, int DockAppCount);

public sealed class LocalStateStore
{
    public const int MaximumAutomaticBackups = 15;
    private const string NewerSchemaBackup = "widgets-newer-schema";

    private static readonly string[] AutomaticBackupPrefixes =
    [
        "widgets-before-migration-",
        "widgets-unreadable-",
        NewerSchemaBackup + "-"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new FiniteDoubleConverter() }
    };

    private readonly object _saveGate = new();
    private readonly object _deferredSaveGate = new();
    private byte[]? _deferredSnapshot;
    private Task<Exception?>? _deferredWriter;
    private byte[]? _lastSavedState;
    private (byte[] Bytes, string Path)? _stateArchive;
    private bool _loadedFromUnavailableState;

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
    public bool RequiresReloadBeforeSaving => _loadedFromUnavailableState;

    public AppState Load()
    {
        WaitForDeferredSave();
        lock (_saveGate)
        {
            Directory.CreateDirectory(DataDirectory);
            ApplyPendingRestore();
            _loadedFromUnavailableState = false;

            var primary = ReadStateFile(StatePath);
            if (primary.State is { } state)
            {
                if (primary.SchemaVersion > AppState.CurrentSchemaVersion)
                {
                    TryArchiveState(NewerSchemaBackup, primary.Bytes!);
                }
                SaveMigratedDates(state, primary);
                return state;
            }

            var backup = ReadStateFile(StatePath + ".bak");
            // Data read from a fallback must not overwrite a temporarily hidden original,
            // even when its lock is released later during this session.
            _loadedFromUnavailableState =
                primary.Status == StateFileStatus.Unavailable || backup.Status == StateFileStatus.Unavailable;
            if (backup.State is { } recovered)
            {
                if (backup.SchemaVersion > AppState.CurrentSchemaVersion)
                {
                    TryArchiveState(NewerSchemaBackup, backup.Bytes!);
                }
                SaveMigratedDates(recovered, backup);
                return recovered;
            }

            if (primary.Status == StateFileStatus.Invalid)
            {
                TryArchiveState("widgets-unreadable", primary.Bytes!);
            }
            return CreateDefault();
        }
    }

    // A remembered view size is either unset (both 0) or two finite positive lengths.
    private static bool IsViewSize(double width, double height) =>
        (width == 0 && height == 0)
        || (double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0);

    private void SaveMigratedDates(AppState state, StateFile file)
    {
        // Persist dates once so legacy entries without timestamps don't move to a different
        // "today" on each load. A write failure must not discard a readable state.
        if (file.MigratedDates && !_loadedFromUnavailableState)
        {
            try
            {
                SaveCore(() => SerializeState(state));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static StateFile ReadStateFile(string path, byte[]? knownState = null)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(StateFileStatus.Missing, Error: exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(StateFileStatus.Unavailable, Error: exception);
        }

        if (knownState is not null && bytes.AsSpan().SequenceEqual(knownState))
        {
            // Only bytes already written by this store can skip deserialization and validation.
            return new(StateFileStatus.Valid, bytes, SchemaVersion: AppState.CurrentSchemaVersion);
        }

        try
        {
            var state = DeserializeState(bytes) ?? throw new JsonException("状态文件没有内容");
            var version = state.SchemaVersion;
            MigrateAndNormalize(state);
            var migratedDates = version <= AppState.CurrentSchemaVersion
                && MigrateLegacyTodoDates(state, DateTime.Today) > 0;
            // A legacy backup is stored in the current schema. Validate that its normalized
            // data can be written before considering the original a usable recovery source.
            var migratedBytes = version < AppState.CurrentSchemaVersion ? SerializeNormalizedState(state) : null;
            return new(StateFileStatus.Valid, bytes, state, version, migratedDates, MigratedBytes: migratedBytes);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return new(StateFileStatus.Invalid, bytes, Error: exception);
        }
    }

    private static AppState? DeserializeState(byte[] bytes)
    {
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith("\uFEFF"u8))
        {
            return JsonSerializer.Deserialize<AppState>(json[3..], JsonOptions);
        }
        if (json.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])
            || json.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])
            || json.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
        {
            // Keep support for state files edited in a tool that writes a UTF-16/32 BOM.
            using var reader = new StreamReader(new MemoryStream(bytes));
            return JsonSerializer.Deserialize<AppState>(reader.ReadToEnd(), JsonOptions);
        }
        return JsonSerializer.Deserialize<AppState>(json, JsonOptions);
    }

    public void Save(AppState state)
    {
        WaitForDeferredSave();
        SaveCore(() => SerializeState(state));
    }

    /// <summary>
    /// Captures the state on the caller's thread and writes the latest pending snapshot in the background.
    /// The state owner calls this and the synchronous save/backup operations in request order.
    /// </summary>
    public Task<Exception?> SaveDeferred(AppState state)
    {
        var snapshot = SerializeState(state);
        lock (_deferredSaveGate)
        {
            _deferredSnapshot = snapshot;
            return _deferredWriter ??= Task.Run(WriteDeferredSnapshots);
        }
    }

    private Exception? WriteDeferredSnapshots()
    {
        Exception? error = null;
        while (true)
        {
            byte[] snapshot;
            lock (_deferredSaveGate)
            {
                if (_deferredSnapshot is null)
                {
                    _deferredWriter = null;
                    return error;
                }
                snapshot = _deferredSnapshot;
                _deferredSnapshot = null;
            }

            try
            {
                SaveCore(() => snapshot);
                error = null;
            }
            catch (Exception exception)
            {
                // Report the write failure to the caller and leave the writer available for the next snapshot.
                error = exception;
            }
        }
    }

    private void WaitForDeferredSave()
    {
        Task<Exception?>? writer;
        lock (_deferredSaveGate)
        {
            writer = _deferredWriter;
        }
        // A failed background write must not prevent the next explicit save from retrying.
        writer?.GetAwaiter().GetResult();
    }

    private void SaveCore(Func<byte[]> getSnapshot)
    {
        lock (_saveGate)
        {
            if (_loadedFromUnavailableState)
            {
                throw new IOException("启动时无法读取原设置。请在文件可访问后重新启动，然后再保存设置。");
            }

            var primary = ReadStateFile(StatePath, _lastSavedState);
            ThrowIfUnavailable(primary);
            var backupPath = StatePath + ".bak";
            var temporaryPath = StatePath + ".tmp";
            var rotateCurrentPrimary = primary.Status == StateFileStatus.Valid
                && primary.SchemaVersion >= AppState.CurrentSchemaVersion;
            if (primary.SchemaVersion > AppState.CurrentSchemaVersion)
            {
                ArchiveState(NewerSchemaBackup, primary.Bytes!);
            }
            else if (primary.Status == StateFileStatus.Invalid)
            {
                ArchiveState("widgets-unreadable", primary.Bytes!);
            }

            StateFile migrationBackup = default;
            if (!rotateCurrentPrimary)
            {
                migrationBackup = primary.Status == StateFileStatus.Valid ? primary : ReadStateFile(backupPath);
                ThrowIfUnavailable(migrationBackup);
            }

            var bytes = getSnapshot();
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllBytes(temporaryPath, bytes);

            if (rotateCurrentPrimary)
            {
                File.Replace(temporaryPath, StatePath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                if (migrationBackup.MigratedBytes is { } migrated)
                {
                    WriteBackup("widgets-before-migration", migrationBackup.Bytes!);
                    var temporaryBackupPath = backupPath + ".tmp";
                    File.WriteAllBytes(temporaryBackupPath, migrated);
                    File.Move(temporaryBackupPath, backupPath, true);
                }
                else if (migrationBackup.Status == StateFileStatus.Invalid)
                {
                    WriteBackup("widgets-unreadable", migrationBackup.Bytes!);
                }
                else if (migrationBackup.SchemaVersion > AppState.CurrentSchemaVersion)
                {
                    ArchiveState(NewerSchemaBackup, migrationBackup.Bytes!);
                }
                File.Move(temporaryPath, StatePath, true);
            }
            _lastSavedState = bytes;
        }
    }

    private static void ThrowIfUnavailable(StateFile file)
    {
        if (file.Status == StateFileStatus.Unavailable)
        {
            throw new IOException("暂时无法读取状态文件。", file.Error);
        }
    }

    private static byte[] SerializeState(AppState state)
    {
        MigrateAndNormalize(state);
        MigrateLegacyTodoDates(state, DateTime.Today);
        return SerializeNormalizedState(state);
    }

    private static byte[] SerializeNormalizedState(AppState state)
    {
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("设置包含无法保存的数值。", exception);
        }
    }

    private enum StateFileStatus { Missing, Unavailable, Invalid, Valid }

    private sealed class FiniteDoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out var value) || !double.IsFinite(value))
            {
                throw new JsonException("设置数值必须是有限数字。");
            }
            return value;
        }

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value);
    }

    private readonly record struct StateFile(
        StateFileStatus Status,
        byte[]? Bytes = null,
        AppState? State = null,
        int SchemaVersion = 0,
        bool MigratedDates = false,
        Exception? Error = null,
        byte[]? MigratedBytes = null);

    public string CreateBackup(AppState state, bool allowWhenReloadRequired = true)
    {
        WaitForDeferredSave();
        lock (_saveGate)
        {
            if (_loadedFromUnavailableState && allowWhenReloadRequired)
            {
                return WriteBackup("widgets", SerializeState(state));
            }
            SaveCore(() => SerializeState(state));
            return WriteBackup("widgets", _lastSavedState!);
        }
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
    /// Removes one backup file for good. Only .json files inside the backup folder
    /// qualify; anything else is refused instead of deleted. False when nothing was deleted.
    /// </summary>
    public bool DeleteBackup(string backupPath)
    {
        var directory = Path.GetFullPath(BackupDirectory);
        var fullPath = Path.GetFullPath(backupPath);
        if (!fullPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(fullPath) != ".json"
            || !File.Exists(fullPath))
        {
            return false;
        }

        File.Delete(fullPath);
        return true;
    }

    /// <summary>
    /// What a backup holds, for telling backups apart in a list: the layout preset and
    /// how many cards and dock apps it has. Null when the file cannot be read.
    /// </summary>
    public static BackupSummary? DescribeBackup(string backupPath)
    {
        if (ReadStateFile(backupPath).State is not { } state)
        {
            return null;
        }
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

    /// <summary>
    /// Validates a backup and queues it for the next <see cref="Load"/>.
    /// If supplied, the current state is backed up before the restore is queued.
    /// </summary>
    public void ScheduleRestore(string backupPath, AppState? currentState = null)
    {
        WaitForDeferredSave();
        lock (_saveGate)
        {
            var backup = ReadStateFile(backupPath);
            ThrowIfUnavailable(backup);
            if (backup.Error is { } error)
            {
                ExceptionDispatchInfo.Throw(error);
            }
            if (currentState is not null)
            {
                WriteBackup("widgets", SerializeState(currentState));
            }
            Directory.CreateDirectory(DataDirectory);
            var temporaryPath = PendingRestorePath + ".tmp";
            File.WriteAllBytes(temporaryPath, backup.Bytes!);
            File.Move(temporaryPath, PendingRestorePath, true);
        }
    }

    private void ApplyPendingRestore()
    {
        if (!File.Exists(PendingRestorePath))
        {
            return;
        }

        try
        {
            var pending = ReadStateFile(PendingRestorePath);
            if (pending.Status == StateFileStatus.Invalid)
            {
                File.Delete(PendingRestorePath);
                return;
            }
            if (pending.State is null)
            {
                return;
            }
            // Before replacing the primary, prove the original can be archived.
            var primary = ReadStateFile(StatePath);
            ThrowIfUnavailable(primary);
            if (primary.Bytes is { } bytes)
            {
                WriteBackup("widgets-before-restore", bytes);
            }
            File.Move(PendingRestorePath, StatePath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The files are busy; the restore stays queued for the next start.
        }
    }

    private void TryArchiveState(string prefix, byte[] bytes)
    {
        try
        {
            ArchiveState(prefix, bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Loading can continue. Saving over these bytes still requires a successful archive.
        }
    }

    private void ArchiveState(string prefix, byte[] bytes)
    {
        if (_stateArchive is { } archive && bytes.AsSpan().SequenceEqual(archive.Bytes) && File.Exists(archive.Path))
        {
            return;
        }
        _stateArchive = (bytes, WriteBackup(prefix, bytes));
    }

    private string WriteBackup(string prefix, byte[] bytes)
    {
        Directory.CreateDirectory(BackupDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(BackupDirectory, $"{prefix}-{stamp}.json");
        for (var suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(BackupDirectory, $"{prefix}-{stamp}-{suffix}.json");
        }
        var temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, bytes);
        File.Move(temporaryPath, path);
        if (AutomaticBackupPrefixes.Any(autoPrefix =>
                string.Equals(prefix + "-", autoPrefix, StringComparison.Ordinal)))
        {
            PruneAutomaticBackups(path);
        }
        return path;
    }

    private void PruneAutomaticBackups(string newBackup)
    {
        var automatic = Directory.EnumerateFiles(BackupDirectory, "*.json")
            .Where(file =>
            {
                var name = Path.GetFileName(file);
                return AutomaticBackupPrefixes.Any(prefix =>
                    name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            })
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (automatic.Length <= MaximumAutomaticBackups)
        {
            return;
        }

        // Keep the new archive even if the clock moved backwards, and one readable future
        // original for opening again after upgrading.
        var futureBackup = IsNewerSchemaBackup(newBackup) ? newBackup : automatic.FirstOrDefault(file =>
            IsNewerSchemaBackup(file) && ReadStateFile(file).SchemaVersion > AppState.CurrentSchemaVersion);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { newBackup };
        if (futureBackup is not null)
        {
            reserved.Add(futureBackup);
        }
        var stale = automatic.Where(file => !reserved.Contains(file)).Skip(MaximumAutomaticBackups - reserved.Count);
        foreach (var file in stale)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool IsNewerSchemaBackup(string path) =>
        Path.GetFileName(path).StartsWith(NewerSchemaBackup + "-", StringComparison.OrdinalIgnoreCase);

    public static AppState CreateDefault()
    {
        var topBar = new TopBarState();
        TopBarRules.ApplyDensityPreset(topBar, TopBarDensityPreset.MacMinimal);
        return new AppState
        {
            TopBar = topBar,
            InformationWidgets = [new InformationWidgetState()],
            TodoWidgets = [TodoService.CreateDefaultWidget()],
            FileBoxes = [new FileBoxState()]
        };
    }

    // Legacy default values persisted through AppState.SchemaVersion 45 prior to the v46 visual polish.
    private const double LegacyV45CornerRadius = 15.5d;
    private const double LegacyV45GlassDensity = 0.32d;
    private const double LegacyV45ItemSurfaceStrength = 0.48d;
    private const string LegacyV45HeaderColor = "#A0B4E1";
    private const string LegacyV45IceTextColor = "#243146";
    private const string LegacyV45DarkBackgroundColor = "#18222F";
    private const string LegacyV45DarkHeaderColor = "#2C3E57";
    private const double LegacyV45DarkHeaderOpacity = 0.82d;
    private const double LegacyValueTolerance = 0.0001d;

    private static readonly (string BodyColor, string LegacyTextColor, string CurrentTextColor)[] LegacyV45PaletteMappings =
    [
        (WidgetAppearanceRules.DefaultBackgroundColor, LegacyV45IceTextColor, WidgetAppearanceRules.DefaultTextColor),
        ("#F6EBDD", "#4A3728", "#2E2118"),
        ("#EEF5E6", "#2F4A2A", "#1F331B"),
        ("#ECEFF4", "#2E3440", "#232933"),
        ("#FBEDEF", "#4E2A33", "#331B22"),
        ("#E8F7F2", "#1F4A40", "#16352D")
    ];

    private static void ValidateEntries<T>(List<T>? entries, string field) where T : class
    {
        if (entries?.Contains(null!) == true)
        {
            throw new InvalidDataException($"设置中的 {field} 含有空条目。");
        }
    }

    private static void ValidatePlacement(WindowPlacement? placement, string field)
    {
        if (placement is null)
        {
            throw new InvalidDataException($"设置中的 {field} 缺少位置数据。");
        }
        ValidateEntries(placement.DisplayLayouts, nameof(WindowPlacement.DisplayLayouts));
        placement.DisplayLayouts ??= [];
    }

    private static void ValidateWidgetState(AppState state)
    {
        ValidateEntries(state.TodoWidgets, nameof(AppState.TodoWidgets));
        ValidateEntries(state.NoteWidgets, nameof(AppState.NoteWidgets));
        ValidateEntries(state.InformationWidgets, nameof(AppState.InformationWidgets));
        ValidateEntries(state.FileBoxes, nameof(AppState.FileBoxes));
        foreach (var todo in state.TodoWidgets)
        {
            ValidatePlacement(todo.Placement, nameof(AppState.TodoWidgets));
            ValidateEntries(todo.Items, nameof(TodoWidgetState.Items));
            todo.Items ??= [];
            foreach (var item in todo.Items)
            {
                item.LinkedPaths ??= [];
            }
        }
        foreach (var note in state.NoteWidgets)
        {
            ValidatePlacement(note.Placement, nameof(AppState.NoteWidgets));
        }
        foreach (var information in state.InformationWidgets)
        {
            ValidatePlacement(information.Placement, nameof(AppState.InformationWidgets));
        }
        foreach (var box in state.FileBoxes)
        {
            ValidatePlacement(box.Placement, nameof(AppState.FileBoxes));
            ValidateEntries(box.Items, nameof(FileBoxState.Items));
            box.Items ??= [];
        }

        ValidateEntries(state.DesktopExperience?.Components, nameof(DesktopExperienceState.Components));
        foreach (var component in state.DesktopExperience?.Components ?? [])
        {
            if (component.Placement is { } placement)
            {
                ValidatePlacement(placement, nameof(DesktopExperienceState.Components));
            }
        }
        foreach (var profile in state.MultiScreen?.Screens?.Values ?? Enumerable.Empty<ScreenProfileState>())
        {
            foreach (var component in profile?.Experience?.Components ?? [])
            {
                if (component?.Placement is { } placement)
                {
                    ValidatePlacement(placement, nameof(DesktopExperienceState.Components));
                }
            }
        }
    }

    private static int MigrateLegacyTodoDates(AppState state, DateTime fallbackDate) =>
        state.TodoWidgets.Sum(todo => TodoService.MigrateUndatedItems(todo, fallbackDate));

    private static void MigrateAndNormalize(AppState state)
    {
        // A hand-edited file can hold null lists; every step below expects lists.
        state.TodoWidgets ??= [];
        state.NoteWidgets ??= [];
        state.InformationWidgets ??= [];
        state.FileBoxes ??= [];
        ValidateWidgetState(state);

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
            // The top bar gains an adjustable height and the frosted-glass switch;
            // missing values fall back to their defaults in TopBarRules.Normalize,
            // nothing else has to move.
            state.SchemaVersion = 28;
        }
        if (state.SchemaVersion < 29)
        {
            // The top bar gains the centre search box: its module switch defaults on
            // and the hotkey keeps the default text, both via TopBarRules.Normalize.
            state.SchemaVersion = 29;
        }
        if (state.SchemaVersion < 30)
        {
            // Search sources and presentation use their machine-local defaults.
            state.SchemaVersion = 30;
        }
        if (state.SchemaVersion < 31)
        {
            // Independent search appearance uses local defaults, preserving its layout.
            state.SchemaVersion = 31;
        }
        if (state.SchemaVersion < 32)
        {
            // The Start menu entry is enabled for existing Dock users, matching new installs.
            state.Dock ??= new DockState();
            state.Dock.ShowStartMenuButton = true;
            state.SchemaVersion = 32;
        }
        if (state.SchemaVersion < 33)
        {
            state.TopBar ??= new TopBarState();
            state.TopBar.Modules ??= new TopBarModuleSwitches();
            state.TopBar.Modules.TrayOverflow = true;
            state.SchemaVersion = 33;
        }
        if (state.SchemaVersion < 34)
        {
            // Missing calendar fields use model defaults; existing dates and timestamps stay intact.
            state.SchemaVersion = 34;
        }
        if (state.SchemaVersion < 35)
        {
            // The todo card folds up to its title bar; existing cards stay unfolded at their height.
            state.SchemaVersion = 35;
        }
        if (state.SchemaVersion < 36)
        {
            // The dock reveal mode defaults to the screen edge plus the handle, as before.
            state.SchemaVersion = 36;
        }
        if (state.SchemaVersion < 37)
        {
            // Deleting originals from a file box is opt-in and stays off for existing users.
            state.FileBoxDeleteOriginalsEnabled = false;
            state.SchemaVersion = 37;
        }
        if (state.SchemaVersion < 38)
        {
            // Card title-bar buttons stay always visible for existing users.
            state.HeaderToolsOnHover = false;
            state.SchemaVersion = 38;
        }
        if (state.SchemaVersion < 39)
        {
            // Entries without a kind stay untimed to-dos; dates, completion and timestamps stay intact.
            state.SchemaVersion = 39;
        }
        if (state.SchemaVersion < 40)
        {
            // The countdown kind only appears in v40+ files; entries without it stay as they were.
            state.SchemaVersion = 40;
        }
        if (state.SchemaVersion < 41)
        {
            foreach (var widget in state.TodoWidgets)
            {
                foreach (var item in (widget.Items ?? []).Where(x => x.Kind == TodoItemKind.Countdown))
                {
                    item.DeadlineDate = item.Date ?? item.CreatedDate?.Date ?? DateTime.Today;
                }
            }
            state.SchemaVersion = 41;
        }
        if (state.SchemaVersion < 42)
        {
            // New field with its default; spelled out so the schema bump stays explicit.
            state.Dock ??= new DockState();
            state.Dock.AutoIconSize = true;
            state.SchemaVersion = 42;
        }
        if (state.SchemaVersion < 43)
        {
            // New field with its default; spelled out so the schema bump stays explicit.
            state.Style ??= new DesktopStyleState();
            state.Style.GridSnap = false;
            state.SchemaVersion = 43;
        }
        if (state.SchemaVersion < 44)
        {
            // New field with its default; spelled out so the schema bump stays explicit.
            foreach (var widget in state.TodoWidgets)
            {
                foreach (var item in widget.Items ?? [])
                {
                    item.LinkedPaths ??= [];
                }
            }
            state.SchemaVersion = 44;
        }
        if (state.SchemaVersion < 45)
        {
            // Additional to-do cards now carry their own font scale and font family instead
            // of being overwritten by the primary to-do component on every launch.
            state.DesktopExperience ??= DesktopExperienceRules.CreateDefault();
            DesktopExperienceRules.Normalize(state.DesktopExperience);
            var todoComponent = state.DesktopExperience.GetComponent(DesktopComponentKind.Todo);
            foreach (var widget in state.TodoWidgets)
            {
                widget.FontScale = todoComponent.FontScale;
                widget.FontFamily = todoComponent.FontFamily;
            }
            state.SchemaVersion = 45;
        }
        if (state.SchemaVersion < 46)
        {
            MigrateLegacyV45Defaults(state);
            state.SchemaVersion = 46;
        }
        if (state.SchemaVersion < 47)
        {
            // Clock layout and date visibility default to Classic and shown for migrated configs.
            foreach (var information in state.InformationWidgets)
            {
                information.ClockLayout = ClockLayoutRules.CoercePreset(information.ClockLayout);
            }
            state.SchemaVersion = 47;
        }
        if (state.SchemaVersion < 48)
        {
            state.SchemaVersion = 48;
        }
        state.LauncherAppearance ??= new LauncherAppearanceState();
        if (state.SchemaVersion < 49)
        {
            state.SchemaVersion = 49;
        }
        if (state.SchemaVersion < 50)
        {
            // Missing launcher visibility uses the DockState default.
            state.SchemaVersion = 50;
        }
        state.LauncherGroups = LauncherGroupRules.Normalize(state.LauncherGroups);
        if (state.SchemaVersion < 51)
        {
            state.SchemaVersion = 51;
        }
        state.LauncherAppCategoryOverrides ??= new Dictionary<string, LauncherAppCategory>(StringComparer.Ordinal);
        if (state.SchemaVersion < 54)
        {
            foreach (var key in state.LauncherAppCategoryOverrides.Keys.ToArray())
            {
                var raw = (int)state.LauncherAppCategoryOverrides[key];
                if (raw == 3)
                {
                    state.LauncherAppCategoryOverrides[key] = LauncherAppCategory.SystemTools;
                }
                else if (raw == 5)
                {
                    state.LauncherAppCategoryOverrides[key] = LauncherAppCategory.Unclassified;
                }
            }
            state.SchemaVersion = 54;
        }
        foreach (var key in state.LauncherAppCategoryOverrides
                     .Where(pair => string.IsNullOrWhiteSpace(pair.Key) || !Enum.IsDefined(pair.Value))
                     .Select(pair => pair.Key).ToArray())
        {
            state.LauncherAppCategoryOverrides.Remove(key);
        }
        state.LauncherPreferences ??= new LauncherPreferencesState();
        state.TopBar ??= new TopBarState();
        if (state.SchemaVersion < 55)
        {
            if (state.TopBar.ExtensionData is { } legacyTopBar)
            {
                var legacyEverythingKey = legacyTopBar.Keys.FirstOrDefault(key =>
                    string.Equals(key, "UseEverything", StringComparison.OrdinalIgnoreCase));
                if (legacyEverythingKey is not null
                    && legacyTopBar[legacyEverythingKey].ValueKind == JsonValueKind.True)
                {
                    state.LauncherPreferences.UseEverything = true;
                }

                string[] retiredKeys =
                [
                    "UseEverything",
                    "SearchPresentation",
                    "SearchWindowWidth",
                    "SearchWindowPlacement",
                    "SearchFollowCardLook",
                    "SearchSurfaceColor",
                    "SearchSurfaceOpacity",
                    "SearchCornerRadius"
                ];
                foreach (var existingKey in legacyTopBar.Keys.ToArray())
                {
                    if (retiredKeys.Any(retired => string.Equals(existingKey, retired, StringComparison.OrdinalIgnoreCase)))
                    {
                        legacyTopBar.Remove(existingKey);
                    }
                }
                if (legacyTopBar.Count == 0)
                {
                    state.TopBar.ExtensionData = null;
                }
            }
            state.SchemaVersion = 55;
        }
        state.Hotkeys ??= new GlobalHotkeysState();
        if (state.SchemaVersion < 56)
        {
            // Carry over any custom TopBar search hotkey to the standalone global hotkey host.
            state.Hotkeys.Launcher = BingLan.Core.TopBar.TopBarSearchRules.ParseHotkey(state.TopBar.SearchHotkey) is { } migratedHotkey
                ? BingLan.Core.TopBar.TopBarSearchRules.FormatHotkey(migratedHotkey)
                : GlobalHotkeysState.DefaultLauncherHotkey;
            state.Hotkeys.PeekFileBoxes = GlobalHotkeysState.DefaultPeekFileBoxesHotkey;
            state.SchemaVersion = 56;
        }
        GlobalHotkeyRules.Normalize(state.Hotkeys);
        state.LauncherPreferences.Normalize();
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
            todo.FontScale = DesktopExperienceRules.CoerceFontScale(todo.FontScale);
            todo.FontFamily = string.IsNullOrWhiteSpace(todo.FontFamily)
                ? null
                : WidgetAppearanceRules.CoerceTitleFontFamily(todo.FontFamily);
            if (todo.WeeklyWeekCount is < 1 or > 5)
            {
                todo.WeeklyWeekCount = 4;
            }
            if (todo.ExpandedHeight < 0)
            {
                todo.ExpandedHeight = 0;
            }
            if (!IsViewSize(todo.ListWidth, todo.ListHeight))
            {
                todo.ListWidth = 0;
                todo.ListHeight = 0;
            }
            if (!IsViewSize(todo.WeeklyWidth, todo.WeeklyHeight))
            {
                todo.WeeklyWidth = 0;
                todo.WeeklyHeight = 0;
            }
            todo.ScreenKey = MultiScreenRules.NormalizeScreenKey(todo.ScreenKey);
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
            box.AutoCollectExcludedPaths = (box.AutoCollectExcludedPaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (box.EdgeDockSide is { } dockSide && !Enum.IsDefined(dockSide))
            {
                box.EdgeDockSide = null;
            }
            if (box.EdgeDockOffsetDip is { } dockOffset && !double.IsFinite(dockOffset))
            {
                box.EdgeDockOffsetDip = null;
            }
            if (string.IsNullOrWhiteSpace(box.EdgeDockMonitorDeviceName))
            {
                box.EdgeDockMonitorDeviceName = null;
            }
            if (box.IdleHideDelaySeconds is { } hideDelay)
            {
                box.IdleHideDelaySeconds = FileBoxHoverRevealRules.CoerceDelaySeconds(hideDelay);
            }
            box.ScreenKey = MultiScreenRules.NormalizeScreenKey(box.ScreenKey);
        }
        FileBoxGroupRules.NormalizeGroups(state.FileBoxes);
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
            note.ScreenKey = MultiScreenRules.NormalizeScreenKey(note.ScreenKey);
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
            information.ClockLayout = ClockLayoutRules.CoercePreset(information.ClockLayout);
        }

        state.DesktopExperience ??= DesktopExperienceRules.CreateDefault();
        DesktopExperienceRules.Normalize(state.DesktopExperience);
        state.Dock ??= new DockState();
        DockPinRules.Normalize(state.Dock);
        state.TopBar ??= new TopBarState();
        TopBarRules.Normalize(state.TopBar);
        state.Taskbar ??= new TaskbarState();
        if (state.SchemaVersion < 57)
        {
            // Legacy v56 configs only carried RevealOnlyAtCorners; preserve their explicit
            // choice instead of letting the newer CornersOnly property default overwrite it.
            state.Taskbar.EdgeRevealMode = state.Taskbar.RevealOnlyAtCorners
                ? TaskbarEdgeRevealMode.CornersOnly
                : TaskbarEdgeRevealMode.AnyEdge;
            state.SchemaVersion = 57;
        }
        if (state.SchemaVersion < 58)
        {
            state.Dock.Edge = DockEdgeRules.Normalize(state.Dock.Edge);
            state.SchemaVersion = 58;
        }
        if (state.SchemaVersion < 59)
        {
            TopBarRules.Normalize(state.TopBar);
            state.SchemaVersion = 59;
        }
        if (state.SchemaVersion < 60)
        {
            state.Dock.BounceOnAttention = false;
            state.SchemaVersion = 60;
        }
        if (state.SchemaVersion < 61)
        {
            // TimeDate now follows the shared desktop card look instead of being pinned to
            // unbacked naked text; upgrade untouched TimeDate defaults while keeping custom colours.
            var timeDate = state.DesktopExperience.GetComponent(DesktopComponentKind.TimeDate);
            var oldGlass = DesktopStyleRules.Appearance(state.Style);
            var oldNakedText = DesktopStyleRules.NakedTextColor(state.Style);
            if (timeDate.Appearance.BackgroundOpacity <= WidgetAppearanceRules.MinimumBackgroundOpacity + 0.005d
                && string.Equals(timeDate.Appearance.BackgroundColor, oldGlass.BackgroundColor, StringComparison.OrdinalIgnoreCase)
                && string.Equals(timeDate.Appearance.TextColor, oldNakedText, StringComparison.OrdinalIgnoreCase))
            {
                var nextLook = DesktopStyleRules.ComponentLook(DesktopComponentKind.TimeDate, state.Style, 0.18d);
                timeDate.Appearance.BackgroundColor = nextLook.BackgroundColor;
                timeDate.Appearance.BackgroundOpacity = nextLook.BackgroundOpacity;
                timeDate.Appearance.TextColor = nextLook.TextColor;
                if (nextLook.BackgroundOpacity >= 0.05d && timeDate.CornerRadius <= 0.001d)
                {
                    timeDate.CornerRadius = WidgetAppearanceRules.DefaultCornerRadius;
                }
            }
            state.SchemaVersion = 61;
        }
        if (state.SchemaVersion < 62)
        {
            state.SchemaVersion = 62;
        }
        if (state.SchemaVersion < 63)
        {
            state.SchemaVersion = 63;
        }
        if (state.SchemaVersion < 64)
        {
            state.SchemaVersion = 64;
        }
        if (state.SchemaVersion < 65)
        {
            state.SchemaVersion = 65;
        }
        if (state.SchemaVersion < 66)
        {
            // Earlier root layouts have no physical-monitor evidence. Keep their content and
            // ownership intact until the user explicitly binds the legacy layout.
            state.SchemaVersion = 66;
        }
        if (state.MultiScreen is not null)
        {
            MultiScreenRules.Normalize(state.MultiScreen, state.TodoWidgets);
        }
        state.Dock.MonitorKey = MultiScreenRules.NormalizeScreenKey(state.Dock.MonitorKey);
        state.TopBar.MonitorKey = MultiScreenRules.NormalizeScreenKey(state.TopBar.MonitorKey);
        state.TopBar.MonitorDeviceName = string.IsNullOrWhiteSpace(state.TopBar.MonitorDeviceName)
            ? null
            : state.TopBar.MonitorDeviceName.Trim();
        if (!Enum.IsDefined(state.Taskbar.Mode))
        {
            state.Taskbar.Mode = TaskbarMode.SystemDefault;
        }
        if (!Enum.IsDefined(state.Taskbar.EdgeRevealMode))
        {
            state.Taskbar.EdgeRevealMode = TaskbarEdgeRevealMode.CornersOnly;
        }
        state.Taskbar.RevealOnlyAtCorners =
            state.Taskbar.EdgeRevealMode == TaskbarEdgeRevealMode.CornersOnly;
        state.LastSettingsPage = string.IsNullOrWhiteSpace(state.LastSettingsPage)
            ? null
            : SettingsNavigationRules.NormalizePageKey(state.LastSettingsPage);

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

    private static void MigrateLegacyV45Defaults(AppState state)
    {
        state.Style ??= new DesktopStyleState();
        if (NearLegacyValue(state.Style.GlassDensity, LegacyV45GlassDensity))
        {
            state.Style.GlassDensity = DesktopStyleRules.DefaultGlassDensity;
        }
        if (NearLegacyValue(state.Style.ItemSurfaceStrength, LegacyV45ItemSurfaceStrength))
        {
            state.Style.ItemSurfaceStrength = DesktopStyleRules.DefaultItemSurfaceStrength;
        }

        foreach (var todo in state.TodoWidgets)
        {
            todo.CornerRadius = MigrateLegacyV45CornerRadius(todo.CornerRadius);
            MigrateLegacyV45Appearance(todo.Appearance, state.Style);
        }
        foreach (var box in state.FileBoxes)
        {
            box.CornerRadius = MigrateLegacyV45CornerRadius(box.CornerRadius);
            MigrateLegacyV45Appearance(box.Appearance, state.Style);
        }
        foreach (var note in state.NoteWidgets)
        {
            note.CornerRadius = MigrateLegacyV45CornerRadius(note.CornerRadius);
            if (note.BodyTypography is not null)
            {
                note.BodyTypography.Color = MigrateLegacyV45TextColor(
                    note.BodyTypography.Color,
                    note.Appearance,
                    state.Style);
            }
            MigrateLegacyV45Appearance(note.Appearance, state.Style);
        }
        foreach (var information in state.InformationWidgets)
        {
            information.CornerRadius = MigrateLegacyV45CornerRadius(information.CornerRadius);
            MigrateLegacyV45Appearance(information.Appearance, state.Style);
        }
        foreach (var component in state.DesktopExperience?.Components ?? [])
        {
            component.CornerRadius = MigrateLegacyV45CornerRadius(component.CornerRadius);
            MigrateLegacyV45Appearance(component.Appearance, state.Style);
        }
    }

    private static double MigrateLegacyV45CornerRadius(double cornerRadius) =>
        NearLegacyValue(cornerRadius, LegacyV45CornerRadius)
            ? WidgetAppearanceRules.DefaultCornerRadius
            : cornerRadius;

    private static void MigrateLegacyV45Appearance(
        WidgetAppearanceState? appearance,
        DesktopStyleState style)
    {
        if (appearance is null)
        {
            return;
        }

        // Resolve text ink mapping before rewriting BackgroundColor / BackgroundOpacity so the card's
        // palette association is evaluated against its saved v45 values.
        appearance.TextColor = MigrateLegacyV45TextColor(appearance.TextColor, appearance, style);

        var hadLegacyOpacity = NearLegacyValue(appearance.BackgroundOpacity, LegacyV45GlassDensity);
        if (hadLegacyOpacity)
        {
            appearance.BackgroundOpacity = WidgetAppearanceRules.DefaultBackgroundOpacity;
        }

        if (string.Equals(appearance.HeaderColor, LegacyV45HeaderColor, StringComparison.OrdinalIgnoreCase))
        {
            appearance.HeaderColor = WidgetAppearanceRules.DefaultHeaderColor;
        }

        if (style.Glass == GlassMode.Dark
            && string.Equals(appearance.BackgroundColor, LegacyV45DarkBackgroundColor, StringComparison.OrdinalIgnoreCase)
            && string.Equals(appearance.HeaderColor, LegacyV45DarkHeaderColor, StringComparison.OrdinalIgnoreCase)
            && string.Equals(appearance.TextColor, "#FFFFFF", StringComparison.OrdinalIgnoreCase))
        {
            appearance.BackgroundColor = "#16202C";
            if (hadLegacyOpacity && NearLegacyValue(appearance.HeaderOpacity, LegacyV45DarkHeaderOpacity))
            {
                appearance.HeaderOpacity = WidgetAppearanceRules.DefaultHeaderOpacity;
            }
        }
    }

    private static string MigrateLegacyV45TextColor(
        string currentTextColor,
        WidgetAppearanceState? cardAppearance,
        DesktopStyleState style)
    {
        if (cardAppearance is null)
        {
            return currentTextColor;
        }

        var isClear = cardAppearance.BackgroundOpacity <= WidgetAppearanceRules.MinimumBackgroundOpacity + 0.005d;
        if (isClear)
        {
            return style.TextInk == TextInk.Dark
                   && string.Equals(currentTextColor, LegacyV45IceTextColor, StringComparison.OrdinalIgnoreCase)
                ? WidgetAppearanceRules.DefaultTextColor
                : currentTextColor;
        }

        foreach (var mapping in LegacyV45PaletteMappings)
        {
            if (string.Equals(cardAppearance.BackgroundColor, mapping.BodyColor, StringComparison.OrdinalIgnoreCase)
                && string.Equals(currentTextColor, mapping.LegacyTextColor, StringComparison.OrdinalIgnoreCase))
            {
                return mapping.CurrentTextColor;
            }
        }

        return currentTextColor;
    }

    private static bool NearLegacyValue(double actual, double legacy) =>
        Math.Abs(actual - legacy) < LegacyValueTolerance;
}
