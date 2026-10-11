using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Themes;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Themes;
using BingLan.Core.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;

namespace BingLan.App.Windows;

public partial class SettingsWindow : Window
{
    private static readonly DesktopComponentKind[] CurrentComponentKinds =
    [
        DesktopComponentKind.TimeDate,
        DesktopComponentKind.Greeting,
        DesktopComponentKind.Weather,
        DesktopComponentKind.Performance,
        DesktopComponentKind.Todo,
        DesktopComponentKind.QuickLaunch
    ];

    private readonly CityLookupService _citySearchService;
    private readonly Action<string, bool> _applyDisplaySettings;
    private readonly Action<CitySearchResult> _applyCity;
    private readonly Action<DesktopExperienceState> _applyDesktopExperience;
    private readonly Action<DesktopExperienceState> _applyLayoutPreset;
    private readonly Func<IReadOnlyList<WidgetWindowBase>> _widgetProvider;
    private readonly Action<WidgetWindowBase, bool> _applyWidgetLock;
    private readonly DesktopExperienceState _desktopExperience;
    private readonly DockState _dockState;
    private readonly Action _applyDock;
    private readonly TaskbarState _taskbarState;
    private readonly Action _applyTaskbar;
    private readonly Func<(TaskbarMode ActiveMode, string Problem)> _taskbarStatus;
    private readonly SettingsMaintenance _maintenance;
    private readonly TopBarState? _topBarState;
    private readonly Action? _applyTopBar;
    private DesktopComponentSettingsEntry? _selectedComponentEntry;
    private CancellationTokenSource? _searchCancellation;
    private bool _loadingExperience;
    private bool _loadingComponentEditor;
    // Settings apply as soon as they change; typed values wait for a short pause first.
    private readonly DispatcherTimer _componentApplyTimer =
        new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _displaySettingsTimer =
        new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _loadingDock;
    private bool _loadingTaskbar;
    private bool _loadingDesktopMode;
    private bool _loadingStartup;
    private bool _loadingCleanDesktop;
    private bool _loadingFileBoxAutomation;
    private bool _loadingStyle;
    private bool _loadingTopBar;
    private bool _loadingTopBarLook;

    public SettingsWindow(
        CityLookupService citySearchService,
        InformationWidgetState state,
        Action<string, bool> applyDisplaySettings,
        Action<CitySearchResult> applyCity)
        : this(
            citySearchService,
            state,
            DesktopExperienceRules.CreateDefault(),
            applyDisplaySettings,
            applyCity,
            _ => { })
    {
    }

    public SettingsWindow(
        CityLookupService citySearchService,
        InformationWidgetState state,
        DesktopExperienceState desktopExperience,
        Action<string, bool> applyDisplaySettings,
        Action<CitySearchResult> applyCity,
        Action<DesktopExperienceState> applyDesktopExperience,
        Action<DesktopExperienceState>? applyLayoutPreset = null,
        Func<IReadOnlyList<WidgetWindowBase>>? widgetProvider = null,
        Action<WidgetWindowBase, bool>? applyWidgetLock = null,
        DockState? dockState = null,
        Action? applyDock = null,
        TaskbarState? taskbarState = null,
        Action? applyTaskbar = null,
        Func<(TaskbarMode ActiveMode, string Problem)>? taskbarStatus = null,
        SettingsMaintenance? maintenance = null,
        TopBarState? topBarState = null,
        Action? applyTopBar = null)
    {
        AccessibilityThemeManager.EnsureInitialized();
        _citySearchService = citySearchService;
        _applyDisplaySettings = applyDisplaySettings;
        _applyCity = applyCity;
        _applyDesktopExperience = applyDesktopExperience;
        _applyLayoutPreset = applyLayoutPreset ?? applyDesktopExperience;
        _widgetProvider = widgetProvider ?? (() => []);
        _applyWidgetLock = applyWidgetLock ?? ((_, _) => { });
        _desktopExperience = desktopExperience;
        DesktopExperienceRules.Normalize(_desktopExperience);
        _dockState = dockState ?? new DockState();
        _applyDock = applyDock ?? (() => { });
        _taskbarState = taskbarState ?? new TaskbarState();
        _applyTaskbar = applyTaskbar ?? (() => { });
        _taskbarStatus = taskbarStatus ?? (() => (TaskbarMode.SystemDefault, string.Empty));
        _maintenance = maintenance ?? new SettingsMaintenance();
        _topBarState = topBarState;
        _applyTopBar = applyTopBar;

        InitializeComponent();
        SourceInitialized += (_, _) => IsBackdropActive = SettingsBackdrop.Apply(this);
        AccessibilityThemeManager.HighContrastChanged += ReapplyBackdrop;
        Closed += (_, _) => AccessibilityThemeManager.HighContrastChanged -= ReapplyBackdrop;
        PaletteSwatches.Fill(GlassPaletteSwatches, palette =>
        {
            _maintenance.Style.Palette = palette.Key;
            _maintenance.Style.Glass = GlassMode.Light;
            _maintenance.ApplyGlass();
            LoadStyle();
        });
        PaletteSwatches.Fill(DockPaletteSwatches, palette =>
        {
            _dockState.FollowCardLook = false;
            _dockState.SurfaceColor = palette.Body;
            ApplyDockChange($"Dock 已改为{palette.Name}配色");
            LoadDockLook();
        });
        PaletteSwatches.Fill(ComponentPaletteSwatches, palette =>
        {
            SelectedComponentBackgroundColorEditor.Text = palette.Body;
            SelectedComponentTextColorEditor.Text = palette.Text;
            ScheduleComponentApply();
        });
        SelectedComponentFontPicker.ItemsSource = new[] { DefaultFontLabel }.Concat(InstalledFontCatalog.Names).ToList();
        _componentApplyTimer.Tick += (_, _) =>
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: false);
        };
        _displaySettingsTimer.Tick += (_, _) =>
        {
            _displaySettingsTimer.Stop();
            ApplyDisplaySettings();
        };

        GreetingNameEditor.Text = state.GreetingName;
        Use24HourClockCheckBox.IsChecked = state.Use24HourClock;
        UpdateCurrentCity(state.WeatherCity);
        LoadDesktopExperience();
        RefreshComponentList();
        LoadDockSettings();
        if (_topBarState is null)
        {
            // Tests and older call sites construct the window without a top bar; the
            // page leaves the navigation entirely so their index-based expectations,
            // and the default page set, stay exactly as they were.
            SettingsNavigationList.Items.Remove(TopBarNavItem);
        }
        else
        {
            LoadTopBarSettings();
        }
        LoadTaskbarSettings();
        LoadDesktopMode();
        LoadPrivacySettings();
        LoadUpdateSettings();
        RefreshCleanDesktopSettings();
        LoadFileBoxAutomation();
        LoadStyle();
        RefreshBackupList();
        ShowPage("Appearance");
        Closing += (_, _) => CommitPendingChanges();
        Closed += (_, _) => CancelSearch();
        if (_maintenance.Updater is { } updater)
        {
            updater.Changed += RefreshUpdateView;
            Closed += (_, _) => updater.Changed -= RefreshUpdateView;
        }
    }

    /// <summary>Whether the window shows the Mica material instead of the solid page colour.</summary>
    internal bool IsBackdropActive { get; private set; }

    private void ReapplyBackdrop(object? sender, EventArgs e) => IsBackdropActive = SettingsBackdrop.Apply(this);

    private void LoadDesktopExperience()
    {
        _loadingExperience = true;
        try
        {
            QuietInformationPresetRadio.IsChecked =
                _desktopExperience.ActivePreset == DesktopLayoutPreset.QuietInformation;
            GlacierWorkbenchPresetRadio.IsChecked =
                _desktopExperience.ActivePreset == DesktopLayoutPreset.GlacierWorkbench;
            TransparentApplePresetRadio.IsChecked =
                _desktopExperience.ActivePreset == DesktopLayoutPreset.TransparentApple;
            CenterClockPresetRadio.IsChecked =
                _desktopExperience.ActivePreset == DesktopLayoutPreset.CenterClock;
            UpdatePresetPreview(_desktopExperience.ActivePreset);
        }
        finally
        {
            _loadingExperience = false;
        }
    }

    private void DesktopComponentList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (DesktopComponentList?.SelectedItem is not DesktopComponentSettingsEntry entry ||
            SelectedComponentVisibleCheckBox is null)
        {
            return;
        }

        // A change typed for the previous card is saved before its editor is replaced.
        if (_componentApplyTimer.IsEnabled)
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: false);
        }
        _selectedComponentEntry = entry;
        if (entry.Kind is { } kind)
        {
            SelectedWidgetAppearanceHost.Content = null;
            FileBoxCollectPanel.Visibility = Visibility.Collapsed;
            SelectedWidgetEditorPanel.Visibility = Visibility.Collapsed;
            BuiltInComponentEditorPanel.Visibility = Visibility.Visible;
            LoadSelectedComponentEditor(kind, entry.Window);
            DisplayContentSettingsCard.Visibility = kind is DesktopComponentKind.TimeDate or
                DesktopComponentKind.Greeting
                ? Visibility.Visible
                : Visibility.Collapsed;
            _loadingDivider = true;
            var clock = _desktopExperience.GetComponent(DesktopComponentKind.TimeDate);
            ClockDividerCheckBox.IsChecked = clock.ShowDivider;
            ClockDividerThicknessSlider.Value = clock.DividerThickness;
            ClockDividerThicknessText.Text = $"{clock.DividerThickness:0.#} px";
            _loadingDivider = false;
            WeatherContentSettingsCard.Visibility = kind == DesktopComponentKind.Weather
                ? Visibility.Visible
                : Visibility.Collapsed;
            QuickPlacesSettingsCard.Visibility = kind == DesktopComponentKind.QuickLaunch
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (kind == DesktopComponentKind.QuickLaunch)
            {
                _quickPlaces = _maintenance.QuickPlaces()
                    .Select(item => new QuickPlaceState { Icon = item.Icon, Name = item.Name, Target = item.Target })
                    .ToList();
                ShowQuickPlaces(0);
            }
            ComponentDetailsScrollViewer.ScrollToTop();
            return;
        }

        if (entry.Window is null)
        {
            return;
        }

        var panel = new WidgetAppearancePanel { SharedStyle = _maintenance.Style };
        panel.Attach(entry.Window);
        LoadFileBoxViewMode(entry.Window as FileBoxWindow);
        LoadFileBoxCollect(entry.Window as FileBoxWindow);
        SelectedWidgetAppearanceHost.Content = panel;
        SelectedWidgetHeadingText.Text = entry.Name;
        SelectedWidgetLockedCheckBox.IsChecked = entry.Window.IsWidgetLocked;
        BuiltInComponentEditorPanel.Visibility = Visibility.Collapsed;
        SelectedWidgetEditorPanel.Visibility = Visibility.Visible;
        DisplayContentSettingsCard.Visibility = Visibility.Collapsed;
        WeatherContentSettingsCard.Visibility = Visibility.Collapsed;
        QuickPlacesSettingsCard.Visibility = Visibility.Collapsed;
        ComponentDetailsScrollViewer.ScrollToTop();
    }

    private void LoadSelectedComponentEditor(
        DesktopComponentKind kind,
        WidgetWindowBase? targetWindow)
    {
        _loadingComponentEditor = true;
        try
        {
            var component = _desktopExperience.GetComponent(kind);
            SelectedComponentVisibleCheckBox.IsChecked = component.IsVisible;
            SelectedComponentLockedCheckBox.IsChecked =
                targetWindow?.IsWidgetLocked ?? component.IsLocked;
            SelectedComponentFontScaleSlider.Value = component.FontScale;
            SelectedComponentFontPicker.SelectedItem = component.FontFamily is { } font
                ? InstalledFontCatalog.Resolve(font)
                : DefaultFontLabel;
            SelectedComponentTextColorEditor.Text = component.Appearance.TextColor;
            SelectedComponentBackgroundColorEditor.Text =
                component.Appearance.BackgroundColor;
            SelectedComponentOpacitySlider.Value = component.Appearance.BackgroundOpacity;
            SelectedComponentCornerRadiusSlider.Value = component.CornerRadius;
            SelectedComponentWidthEditor.Text = $"{component.Placement.Width:0.#}";
            SelectedComponentHeightEditor.Text = $"{component.Placement.Height:0.#}";
            SelectedComponentDensitySelector.SelectedIndex = component.Density switch
            {
                DesktopInformationDensity.Comfortable => 0,
                DesktopInformationDensity.Compact => 2,
                _ => 1
            };
            UpdateComponentStyleValueText();
            SelectedComponentHeadingText.Text =
                _selectedComponentEntry?.Name ?? DesktopExperienceRules.GetComponentName(kind);
        }
        finally
        {
            _loadingComponentEditor = false;
        }
    }

    private void ScheduleComponentApply()
    {
        if (_loadingComponentEditor || _selectedComponentEntry?.Kind is null)
        {
            return;
        }
        _componentApplyTimer.Stop();
        _componentApplyTimer.Start();
    }

    /// <summary>Applies any change still waiting for its typing pause.</summary>
    public void CommitPendingChanges()
    {
        if (_componentApplyTimer.IsEnabled)
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: false);
        }
        if (_displaySettingsTimer.IsEnabled)
        {
            _displaySettingsTimer.Stop();
            ApplyDisplaySettings();
        }
    }

    private void ComponentEditor_TextChanged(object sender, TextChangedEventArgs e) =>
        ScheduleComponentApply();

    private const string DefaultFontLabel = "默认（界面字体）";

    private void ComponentFont_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingComponentEditor && _selectedComponentEntry?.Kind is not null)
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: false);
        }
    }

    // One card takes light glass, dark glass or no backing without changing the others.
    private void ComponentLook_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<GlassMode>(tag, out var mode))
        {
            return;
        }

        var look = CardLook(mode, _maintenance.Style, SelectedComponentOpacitySlider.Value);
        SelectedComponentBackgroundColorEditor.Text = look.BackgroundColor;
        SelectedComponentTextColorEditor.Text = look.TextColor;
        SelectedComponentOpacitySlider.Value = look.BackgroundOpacity;
        _componentApplyTimer.Stop();
        ApplySelectedComponent(refreshList: false);
    }

    /// <summary>
    /// The colours of one card in a material, keeping the card's opacity when it has a
    /// visible body and falling back to the shared glass density when it had none.
    /// </summary>
    internal static GlassAppearance CardLook(GlassMode mode, DesktopStyleState shared, double currentOpacity)
    {
        var density = mode == GlassMode.Clear || currentOpacity >= 0.05d ? currentOpacity : shared.GlassDensity;
        return DesktopStyleRules.Appearance(new DesktopStyleState
        {
            Glass = mode,
            Palette = shared.Palette,
            GlassDensity = density,
            TextInk = TextInk.Auto
        });
    }

    private void ComponentDensity_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingComponentEditor && _selectedComponentEntry?.Kind is not null)
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: false);
        }
    }

    // Showing or locking a card changes its label in the list, so the list is refreshed.
    private void ComponentToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loadingComponentEditor && _selectedComponentEntry?.Kind is not null)
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: true);
        }
    }

    private void DisplaySettings_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        _displaySettingsTimer.Stop();
        if (sender is CheckBox)
        {
            ApplyDisplaySettings();
        }
        else
        {
            _displaySettingsTimer.Start();
        }
    }

    // The quick-launch icons being edited. An added icon stays here until it has a
    // location; the saved list only holds icons that open something.
    private List<QuickPlaceState> _quickPlaces = [];
    private bool _loadingQuickPlace;

    private sealed record QuickPlaceRow(string Glyph, string Name, string Target);

    private void ShowQuickPlaces(int select)
    {
        QuickPlaceList.ItemsSource = _quickPlaces
            .Select(item => new QuickPlaceRow(QuickPlaceRules.GetIcon(item.Icon).Glyph, item.Name, DescribeTarget(item.Target)))
            .ToList();
        QuickPlaceIconPicker.ItemsSource ??= QuickPlaceRules.Icons;
        QuickPlaceList.SelectedIndex = _quickPlaces.Count == 0 ? -1 : Math.Clamp(select, 0, _quickPlaces.Count - 1);
        LoadQuickPlaceEditor();
    }

    private static string DescribeTarget(string target) => target switch
    {
        "" => "（还没有选择位置）",
        _ when target.StartsWith(QuickPlaceRules.KnownPrefix, StringComparison.OrdinalIgnoreCase) =>
            $"系统{QuickPlaceRules.GetIcon(target[QuickPlaceRules.KnownPrefix.Length..]).Name}文件夹",
        _ when target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) => "系统位置",
        _ => target
    };

    private QuickPlaceState? SelectedQuickPlace =>
        QuickPlaceList.SelectedIndex is var index && index >= 0 && index < _quickPlaces.Count ? _quickPlaces[index] : null;

    private void LoadQuickPlaceEditor()
    {
        _loadingQuickPlace = true;
        var item = SelectedQuickPlace;
        QuickPlaceEditor.IsEnabled = item is not null;
        QuickPlaceIconPicker.SelectedItem = item is null ? null : QuickPlaceRules.GetIcon(item.Icon);
        QuickPlaceNameEditor.Text = item?.Name ?? string.Empty;
        QuickPlaceTargetEditor.Text = item is null || item.Target.StartsWith(QuickPlaceRules.KnownPrefix, StringComparison.OrdinalIgnoreCase)
            || item.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            ? DescribeTarget(item?.Target ?? string.Empty)
            : item.Target;
        _loadingQuickPlace = false;
    }

    private void SaveQuickPlaces()
    {
        var index = QuickPlaceList.SelectedIndex;
        _maintenance.SetQuickPlaces(_quickPlaces
            .Select(item => new QuickPlaceState { Icon = item.Icon, Name = item.Name, Target = item.Target })
            .ToList());
        ShowQuickPlaces(index);
        ComponentSettingsStatusText.Text = "快捷入口已更新";
    }

    private void QuickPlaceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingQuickPlace)
        {
            LoadQuickPlaceEditor();
        }
    }

    private void QuickPlaceIcon_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingQuickPlace || SelectedQuickPlace is not { } item || QuickPlaceIconPicker.SelectedItem is not QuickPlaceIcon icon)
        {
            return;
        }
        item.Icon = icon.Key;
        SaveQuickPlaces();
    }

    private void QuickPlaceName_Commit(object sender, RoutedEventArgs e)
    {
        if (_loadingQuickPlace || SelectedQuickPlace is not { } item || item.Name == QuickPlaceNameEditor.Text.Trim())
        {
            return;
        }
        item.Name = QuickPlaceNameEditor.Text.Trim();
        SaveQuickPlaces();
    }

    // A typed path replaces the location; the friendly text shown for a system place is
    // left alone.
    private void QuickPlaceTarget_Commit(object sender, RoutedEventArgs e)
    {
        var text = QuickPlaceTargetEditor.Text.Trim().Trim('"');
        if (_loadingQuickPlace || SelectedQuickPlace is not { } item || text.Length == 0
            || text == item.Target || text == DescribeTarget(item.Target))
        {
            return;
        }
        item.Target = text;
        SaveQuickPlaces();
    }

    private void QuickPlaceEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }
        if (ReferenceEquals(sender, QuickPlaceNameEditor))
        {
            QuickPlaceName_Commit(sender, e);
        }
        else
        {
            QuickPlaceTarget_Commit(sender, e);
        }
        e.Handled = true;
    }

    private void QuickPlaceChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQuickPlace is not { } item)
        {
            return;
        }
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择快捷入口打开的文件夹" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        SetQuickPlaceTarget(item, dialog.FolderName, Path.GetFileName(dialog.FolderName.TrimEnd(Path.DirectorySeparatorChar)));
    }

    private void QuickPlaceChooseFile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQuickPlace is not { } item)
        {
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择快捷入口打开的程序或文件",
            Filter = "程序 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*",
            DereferenceLinks = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        var name = Path.GetExtension(dialog.FileName).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            ? BingLan.App.Dock.DockAppResolver.GetFileDescription(dialog.FileName) ?? Path.GetFileNameWithoutExtension(dialog.FileName)
            : Path.GetFileNameWithoutExtension(dialog.FileName);
        SetQuickPlaceTarget(item, dialog.FileName, name);
    }

    // A new icon takes the chosen item's name; a named one keeps the name it was given.
    private void SetQuickPlaceTarget(QuickPlaceState item, string target, string name)
    {
        if (item.Target.Length == 0 && name.Length > 0)
        {
            item.Name = name[..Math.Min(name.Length, QuickPlaceRules.MaximumNameLength)];
        }
        item.Target = target;
        SaveQuickPlaces();
    }

    private void QuickPlaceAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_quickPlaces.Count >= QuickPlaceRules.MaximumItems)
        {
            ComponentSettingsStatusText.Text = $"快捷入口最多 {QuickPlaceRules.MaximumItems} 个";
            return;
        }
        _quickPlaces.Add(new QuickPlaceState { Icon = QuickPlaceRules.FolderIcon, Name = "新入口", Target = string.Empty });
        ShowQuickPlaces(_quickPlaces.Count - 1);
        ComponentSettingsStatusText.Text = "选择图标，再用“选文件夹…”或“选程序或文件…”指定位置";
    }

    private void QuickPlaceRemove_Click(object sender, RoutedEventArgs e)
    {
        if (QuickPlaceList.SelectedIndex is var index and >= 0 && index < _quickPlaces.Count)
        {
            _quickPlaces.RemoveAt(index);
            SaveQuickPlaces();
            ShowQuickPlaces(index);
        }
    }

    private void QuickPlaceUp_Click(object sender, RoutedEventArgs e) => MoveQuickPlace(-1);

    private void QuickPlaceDown_Click(object sender, RoutedEventArgs e) => MoveQuickPlace(1);

    private void MoveQuickPlace(int offset)
    {
        var index = QuickPlaceList.SelectedIndex;
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _quickPlaces.Count)
        {
            return;
        }
        (_quickPlaces[index], _quickPlaces[target]) = (_quickPlaces[target], _quickPlaces[index]);
        SaveQuickPlaces();
        ShowQuickPlaces(target);
    }

    private void QuickPlaceReset_Click(object sender, RoutedEventArgs e)
    {
        _quickPlaces = QuickPlaceRules.CreateDefaults();
        SaveQuickPlaces();
        ShowQuickPlaces(0);
    }

    private bool _loadingDivider;

    private void ClockDivider_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingDivider || ClockDividerThicknessText is null)
        {
            return;
        }
        var clock = _desktopExperience.GetComponent(DesktopComponentKind.TimeDate);
        clock.ShowDivider = ClockDividerCheckBox.IsChecked == true;
        clock.DividerThickness = ClockDividerThicknessSlider.Value;
        ClockDividerThicknessText.Text = $"{clock.DividerThickness:0.#} px";
        _applyDesktopExperience(_desktopExperience);
        DisplaySettingsStatusText.Text = "已保存";
    }

    private void ApplyDisplaySettings()
    {
        _applyDisplaySettings(
            GreetingNameEditor.Text.Trim(),
            Use24HourClockCheckBox.IsChecked == true);
        DisplaySettingsStatusText.Text = "已保存";
    }

    private void ComponentStyleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loadingComponentEditor &&
            SelectedComponentFontScaleSlider is not null &&
            SelectedComponentFontScaleValueText is not null &&
            SelectedComponentOpacitySlider is not null &&
            SelectedComponentOpacityValueText is not null &&
            SelectedComponentCornerRadiusSlider is not null &&
            SelectedComponentCornerRadiusValueText is not null)
        {
            UpdateComponentStyleValueText();
            ScheduleComponentApply();
        }
    }

    private void UpdateComponentStyleValueText()
    {
        SelectedComponentFontScaleValueText.Text =
            $"{SelectedComponentFontScaleSlider.Value * 100:0}%";
        SelectedComponentOpacityValueText.Text =
            $"{SelectedComponentOpacitySlider.Value * 100:0}%";
        SelectedComponentCornerRadiusValueText.Text =
            $"{SelectedComponentCornerRadiusSlider.Value:0}px";
    }

    private void QuickTextColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            SelectedComponentTextColorEditor.Text = color;
        }
    }

    private void QuickBackgroundColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            SelectedComponentBackgroundColorEditor.Text = color;
        }
    }

    private void ApplySelectedComponent(bool refreshList)
    {
        if (_selectedComponentEntry?.Kind is not { } kind)
        {
            return;
        }

        var component = _desktopExperience.GetComponent(kind);
        component.IsVisible = SelectedComponentVisibleCheckBox.IsChecked == true;
        component.FontScale = SelectedComponentFontScaleSlider.Value;
        component.FontFamily = SelectedComponentFontPicker.SelectedItem is string font && font != DefaultFontLabel
            ? font
            : null;
        component.Appearance.TextColor = WidgetAppearanceRules.CoerceTextColor(
            SelectedComponentTextColorEditor.Text);
        component.Appearance.BackgroundColor = WidgetAppearanceRules.CoerceBackgroundColor(
            SelectedComponentBackgroundColorEditor.Text);
        component.Appearance.BackgroundOpacity =
            WidgetAppearanceRules.CoerceBackgroundOpacity(
                SelectedComponentOpacitySlider.Value);
        component.CornerRadius = SelectedComponentCornerRadiusSlider.Value;
        var (minimumWidth, minimumHeight) = GetMinimumComponentSize(kind);
        component.Placement.Width = ParseSize(
            SelectedComponentWidthEditor.Text,
            component.Placement.Width,
            minimumWidth,
            1600d);
        component.Placement.Height = ParseSize(
            SelectedComponentHeightEditor.Text,
            component.Placement.Height,
            minimumHeight,
            1200d);
        component.Density = SelectedComponentDensitySelector.SelectedIndex switch
        {
            0 => DesktopInformationDensity.Comfortable,
            2 => DesktopInformationDensity.Compact,
            _ => DesktopInformationDensity.Standard
        };
        if (_selectedComponentEntry.Window is { } targetWindow)
        {
            _applyWidgetLock(
                targetWindow,
                SelectedComponentLockedCheckBox.IsChecked == true);
        }
        else
        {
            component.IsLocked = SelectedComponentLockedCheckBox.IsChecked == true;
        }
        _applyDesktopExperience(_desktopExperience);
        ComponentSettingsStatusText.Text = $"已保存“{_selectedComponentEntry.Name}”设置";
        if (refreshList)
        {
            RefreshComponentList(_selectedComponentEntry.Window, kind);
        }
    }

    public void ShowWidgetSettings(WidgetWindowBase window)
    {
        SelectNavigationPage("Components");
        ShowPage("Components");
        RefreshComponentList(window);
    }

    /// <summary>
    /// Opens the components page with one kind selected, for deep links from surfaces
    /// that show a card's data without the card itself, like the top bar.
    /// </summary>
    public void ShowComponentSettings(DesktopComponentKind kind)
    {
        SelectNavigationPage("Components");
        ShowPage("Components");
        RefreshComponentList(null, kind);
    }

    private bool _loadingCollect;

    private bool _loadingViewMode;

    private void LoadFileBoxViewMode(FileBoxWindow? box)
    {
        FileBoxViewModePanel.Visibility = box is null ? Visibility.Collapsed : Visibility.Visible;
        if (box is null)
        {
            return;
        }

        _loadingViewMode = true;
        try
        {
            var list = box.State.ViewMode == FileBoxViewMode.List;
            FileBoxTilesRadio.IsChecked = !list;
            FileBoxListRadio.IsChecked = list;
        }
        finally
        {
            _loadingViewMode = false;
        }
    }

    private void FileBoxViewMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingViewMode || _selectedComponentEntry?.Window is not FileBoxWindow box)
        {
            return;
        }

        box.SetViewMode(FileBoxListRadio.IsChecked == true ? FileBoxViewMode.List : FileBoxViewMode.Tiles);
        ComponentSettingsStatusText.Text = $"已保存“{box.State.Title}”的显示方式";
    }

    private void LoadFileBoxCollect(FileBoxWindow? box)
    {
        FileBoxCollectPanel.Visibility = box is null ? Visibility.Collapsed : Visibility.Visible;
        if (box is null)
        {
            return;
        }

        _loadingCollect = true;
        try
        {
            FileBoxCollectTypes.Children.Clear();
            var folders = new CheckBox
            {
                Content = "文件夹",
                Tag = "folders",
                Margin = new Thickness(0, 4, 18, 4),
                IsChecked = box.State.CollectFolders
            };
            folders.Click += FileBoxCollect_Changed;
            FileBoxCollectTypes.Children.Add(folders);
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in FileMappingService.CollectTypes)
            {
                var check = new CheckBox
                {
                    Content = type.Name,
                    Tag = type.Key,
                    Margin = new Thickness(0, 4, 18, 4),
                    ToolTip = string.Join(" ", type.Extensions),
                    IsChecked = type.Extensions.All(box.State.CollectExtensions.Contains)
                };
                check.Click += FileBoxCollect_Changed;
                FileBoxCollectTypes.Children.Add(check);
                if (check.IsChecked == true)
                {
                    known.UnionWith(type.Extensions);
                }
            }
            FileBoxExtraExtensionsEditor.Text = string.Join(
                " ",
                box.State.CollectExtensions.Where(extension => !known.Contains(extension)));
        }
        finally
        {
            _loadingCollect = false;
        }
    }

    private void FileBoxCollect_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingCollect || _selectedComponentEntry?.Window is not FileBoxWindow box)
        {
            return;
        }

        var checks = FileBoxCollectTypes.Children.OfType<CheckBox>().ToList();
        var extensions = FileMappingService.CollectTypes
            .Where(type => checks.Any(check => Equals(check.Tag, type.Key) && check.IsChecked == true))
            .SelectMany(type => type.Extensions)
            .Concat(FileMappingService.NormalizeExtensions([FileBoxExtraExtensionsEditor.Text]));
        var folders = checks.Any(check => Equals(check.Tag, "folders") && check.IsChecked == true);
        box.SetCollectRule(extensions, folders);
        ComponentSettingsStatusText.Text = $"已保存“{box.State.Title}”的收纳类型";
    }

    private void SelectedWidgetLockedCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedComponentEntry?.Window is not { } window)
        {
            return;
        }

        _applyWidgetLock(window, SelectedWidgetLockedCheckBox.IsChecked == true);
        RefreshComponentList(window);
    }


    private void AddTodo_Click(object sender, RoutedEventArgs e) => AddWidget(NewWidgetKind.Todo);

    private void AddNote_Click(object sender, RoutedEventArgs e) => AddWidget(NewWidgetKind.Note);

    private void AddFileBox_Click(object sender, RoutedEventArgs e) => AddWidget(NewWidgetKind.FileBox);

    // The new card is selected so it can be adjusted right away.
    private void AddWidget(NewWidgetKind kind)
    {
        if (_maintenance.AddWidget(kind) is { } window)
        {
            RefreshComponentList(window);
            ComponentSettingsStatusText.Text = $"已新建“{GetWidgetName(window)}”";
        }
    }

    private void ExitApp_Click(object sender, RoutedEventArgs e) => _maintenance.ExitApp();

    private void SettingsNavigationList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (SettingsNavigationList.SelectedItem is ListBoxItem item && item.Tag is string page)
        {
            ShowPage(page);
        }
    }

    // Older page names still open the page that now holds their settings.
    private static string PageFor(string page) => page switch
    {
        "Theme" or "Layout" => "Appearance",
        "Privacy" or "Backup" or "About" => "General",
        _ => page
    };

    private void ShowPage(string page)
    {
        if (AppearancePage is null || ComponentsPage is null || DockPage is null || TaskbarPage is null ||
            GeneralPage is null || TopBarPage is null)
        {
            return;
        }

        page = PageFor(page);
        AppearancePage.Visibility = page == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        ComponentsPage.Visibility = page == "Components" ? Visibility.Visible : Visibility.Collapsed;
        DockPage.Visibility = page == "Dock" ? Visibility.Visible : Visibility.Collapsed;
        TopBarPage.Visibility = page == "TopBar" && _topBarState is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        TaskbarPage.Visibility = page == "Taskbar" ? Visibility.Visible : Visibility.Collapsed;
        GeneralPage.Visibility = page == "General" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "General")
        {
            RefreshBackupList();
        }

        if (page == "Components")
        {
            RefreshComponentList(
                _selectedComponentEntry?.Window,
                _selectedComponentEntry?.Kind);
        }
    }

    internal void ShowSettingsPage(string page) => SelectNavigationPage(page);

    internal void RefreshDesktopExperience() => LoadDesktopExperience();

    internal void RefreshDockSettings()
    {
        LoadDockSettings();
        LoadDesktopMode();
    }

    internal void RefreshTopBarSettings()
    {
        if (_topBarState is null)
        {
            return;
        }

        LoadTopBarSettings();
        LoadDesktopMode();
    }

    internal void RefreshTaskbarSettings()
    {
        LoadTaskbarSettings();
        LoadDesktopMode();
    }

    private void LoadPrivacySettings()
    {
        _loadingStartup = true;
        try
        {
            StartupCheckBox.IsChecked = _maintenance.IsStartupEnabled();
            StartupCheckBox.IsEnabled = _maintenance.CanChangeStartup;
            if (!_maintenance.CanChangeStartup)
            {
                StartupNoteText.Text = "测试模式不修改开机启动。";
                StartupNoteText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _loadingStartup = false;
        }
        DataDirectoryText.Text = _maintenance.DataDirectory;
    }

    private void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingStartup)
        {
            return;
        }

        _maintenance.SetStartupEnabled(StartupCheckBox.IsChecked == true);
        LoadPrivacySettings();
    }

    internal void RefreshCleanDesktopSettings()
    {
        _loadingCleanDesktop = true;
        try
        {
            CleanDesktopCheckBox.IsChecked = _maintenance.IsCleanDesktopEnabled();
        }
        finally
        {
            _loadingCleanDesktop = false;
        }

        // A restore that did not complete stays visible here until it is resolved.
        var notice = _maintenance.CleanDesktopNotice();
        CleanDesktopRetryButton.Visibility = notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (notice.Length > 0)
        {
            CleanDesktopStatusText.Text = notice;
        }
    }

    private async void CleanDesktopRetry_Click(object sender, RoutedEventArgs e)
    {
        CleanDesktopRetryButton.IsEnabled = false;
        CleanDesktopStatusText.Text = "正在恢复桌面图标…";
        try
        {
            CleanDesktopStatusText.Text = await _maintenance.RetryCleanDesktopRestore();
        }
        finally
        {
            CleanDesktopRetryButton.IsEnabled = true;
            RefreshCleanDesktopSettings();
        }
    }

    internal void LoadStyle()
    {
        _loadingStyle = true;
        try
        {
            SelectByTag(CardShadowSelector, _maintenance.Style.CardShadow.ToString());
            SelectByTag(MotionSelector, _maintenance.Style.Motion.ToString());
            var spacing = CardSpacingSelector.Items.OfType<ComboBoxItem>()
                .OrderBy(item => Math.Abs(double.Parse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture)
                    - _maintenance.Style.CardSpacing))
                .First();
            CardSpacingSelector.SelectedItem = spacing;
            var style = _maintenance.Style;
            GlassLightRadio.IsChecked = style.Glass == GlassMode.Light;
            GlassDarkRadio.IsChecked = style.Glass == GlassMode.Dark;
            GlassClearRadio.IsChecked = style.Glass == GlassMode.Clear;
            TextInkAutoRadio.IsChecked = style.TextInk == TextInk.Auto;
            TextInkWhiteRadio.IsChecked = style.TextInk == TextInk.White;
            TextInkDarkRadio.IsChecked = style.TextInk == TextInk.Dark;
            TextInkCustomRadio.IsChecked = style.TextInk == TextInk.Custom;
            CustomTextColorChip.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(style.CustomTextColor)!);
            GlassDensitySlider.Value = style.GlassDensity;
            GlassDensityText.Text = $"{style.GlassDensity * 100:0}%";
            GlassDensityPanel.IsEnabled = style.Glass != GlassMode.Clear;
            PaletteSwatches.Select(GlassPaletteSwatches, style.Glass == GlassMode.Light ? style.Palette : null);
        }
        finally
        {
            _loadingStyle = false;
        }
    }

    private void Glass_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingStyle || GlassClearRadio is null || TextInkCustomRadio is null)
        {
            return;
        }

        var style = _maintenance.Style;
        style.Glass = GlassDarkRadio.IsChecked == true
            ? GlassMode.Dark
            : GlassClearRadio.IsChecked == true ? GlassMode.Clear : GlassMode.Light;
        style.TextInk = TextInkCustomRadio.IsChecked == true
            ? TextInk.Custom
            : TextInkDarkRadio.IsChecked == true
                ? TextInk.Dark
                : TextInkWhiteRadio.IsChecked == true ? TextInk.White : TextInk.Auto;
        _maintenance.ApplyGlass();
        LoadStyle();
    }

    // Reads a skin's look, shows what will change and applies it after a yes.
    private async void ImportRainmeterLook_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择一个 Rainmeter 皮肤文件夹",
            InitialDirectory = BingLan.App.Themes.RainmeterSkinFiles.SkinsFolder() ?? string.Empty
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var folder = dialog.FolderName;
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        RainmeterImportStatusText.Text = "正在读取…";
        var look = await Task.Run(() => RainmeterSkinReader.Read(BingLan.App.Themes.RainmeterSkinFiles.Collect(folder)));
        if (look.IsEmpty)
        {
            RainmeterImportStatusText.Text = $"“{name}”中没有找到字体或颜色设置";
            return;
        }

        var lines = new List<string>();
        if (look.FontFace is { } font) lines.Add($"字体：{font}");
        if (look.TextColor is { } text) lines.Add($"文字颜色：{text}");
        if (look.BackgroundColor is { } backing) lines.Add($"卡片底色：{backing}，不透明度 {look.BackgroundOpacity:P0}");
        if (look.AccentColor is { } accent) lines.Add($"强调色：{accent}");
        if (look.CornerRadius is { } radius) lines.Add($"圆角：{radius:0}");
        var answer = System.Windows.MessageBox.Show(
            this,
            $"将按“{name}”调整所有卡片：\n\n{string.Join("\n", lines)}\n\n当前设置会先备份，可在“通用”页的备份中恢复。继续吗？",
            "从 Rainmeter 皮肤导入外观",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            RainmeterImportStatusText.Text = "已取消";
            return;
        }

        RainmeterImportStatusText.Text = _maintenance.ImportRainmeterLook(look, name);
        LoadStyle();
    }

    // Picking a colour also switches the cards to it.
    private void CustomTextColor_Click(object sender, RoutedEventArgs e)
    {
        var style = _maintenance.Style;
        var current = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(style.CustomTextColor)!;
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            FullOpen = true,
            AnyColor = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        style.CustomTextColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        style.TextInk = TextInk.Custom;
        _maintenance.ApplyGlass();
        LoadStyle();
    }

    private void GlassDensity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingStyle || GlassDensityText is null)
        {
            return;
        }

        _maintenance.Style.GlassDensity = e.NewValue;
        GlassDensityText.Text = $"{_maintenance.Style.GlassDensity * 100:0}%";
        _maintenance.ApplyGlass();
    }

    private static void SelectByTag(System.Windows.Controls.ComboBox selector, string tag) =>
        selector.SelectedItem = selector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals((string)item.Tag, tag, StringComparison.Ordinal));

    private void StyleSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingStyle
            || CardShadowSelector.SelectedItem is not ComboBoxItem { Tag: string shadowTag }
            || CardSpacingSelector.SelectedItem is not ComboBoxItem { Tag: string spacingTag }
            || MotionSelector.SelectedItem is not ComboBoxItem { Tag: string motionTag })
        {
            return;
        }

        _maintenance.Style.CardShadow = Enum.Parse<CardShadow>(shadowTag);
        _maintenance.Style.CardSpacing = double.Parse(spacingTag, System.Globalization.CultureInfo.InvariantCulture);
        _maintenance.Style.Motion = Enum.Parse<MotionLevel>(motionTag);
        _maintenance.ApplyStyle();
    }

    private void LoadFileBoxAutomation()
    {
        _loadingFileBoxAutomation = true;
        try
        {
            FileBoxAutomationCheckBox.IsChecked = _maintenance.IsFileBoxAutomationEnabled();
        }
        finally
        {
            _loadingFileBoxAutomation = false;
        }
    }

    private void FileBoxAutomationCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingFileBoxAutomation)
        {
            return;
        }

        _maintenance.SetFileBoxAutomation(FileBoxAutomationCheckBox.IsChecked == true);
    }

    private async void CleanDesktopCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingCleanDesktop)
        {
            return;
        }

        var enable = CleanDesktopCheckBox.IsChecked == true;
        CleanDesktopCheckBox.IsEnabled = false;
        CleanDesktopStatusText.Text = enable ? "正在隐藏桌面图标…" : "正在恢复桌面图标…";
        try
        {
            CleanDesktopStatusText.Text = await _maintenance.SetCleanDesktop(enable);
        }
        finally
        {
            CleanDesktopCheckBox.IsEnabled = true;
            RefreshCleanDesktopSettings();
        }
    }

    private void OpenDataDirectory_Click(object sender, RoutedEventArgs e) =>
        _maintenance.OpenDataDirectory();

    private void RefreshBackupList()
    {
        var entries = _maintenance.ListBackups()
            .Select(path => new BackupEntry(path))
            .ToList();
        BackupList.ItemsSource = entries;
        BackupEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _maintenance.CreateBackup();
            RefreshBackupList();
            BackupStatusText.Text = $"已备份到 {path}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            BackupStatusText.Text = $"备份未完成：{exception.Message}";
        }
    }

    private void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupEntry entry)
        {
            BackupStatusText.Text = "请先选择一个备份";
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            this,
            $"将用 {entry.Label} 的备份替换当前桌面组件、个人内容和设置。当前状态会先另存为备份，随后冰蓝桌面会重新启动。继续吗？",
            "从备份恢复",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _maintenance.RestoreBackup(entry.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidDataException)
        {
            BackupStatusText.Text = $"无法使用这个备份：{exception.Message}";
        }
    }

    private void ResetComponentPlacement_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedComponentEntry is not { } entry)
        {
            return;
        }

        var reset = _maintenance.ResetWidgetPlacement(entry.Window, entry.Kind);
        ComponentSettingsStatusText.Text = reset ? "已恢复默认位置" : "这张卡片没有默认位置";
        RefreshComponentList(entry.Window, entry.Kind);
    }

    // Same as the card's own "删除" menu: it asks first, and built-in cards are hidden
    // rather than losing their settings.
    private void DeleteComponent_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedComponentEntry?.Window is not { } window)
        {
            ComponentSettingsStatusText.Text = "这张卡片当前没有显示在桌面上";
            return;
        }
        if (_maintenance.DeleteWidget(window))
        {
            ComponentSettingsStatusText.Text = "已删除";
            RefreshComponentList();
        }
    }

    private void ExportTheme_Click(object sender, RoutedEventArgs e)
    {
        var nameDialog = new ThemeExportDialog { Owner = this };
        if (nameDialog.ShowDialog() != true)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出冰蓝桌面主题",
            Filter = $"冰蓝桌面主题 (*{ThemeArchive.FileExtension})|*{ThemeArchive.FileExtension}",
            FileName = nameDialog.ThemeName,
            DefaultExt = ThemeArchive.FileExtension,
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ThemeStatusText.Text = _maintenance.ExportTheme(dialog.FileName, nameDialog.ThemeName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ThemeStatusText.Text = $"导出未完成：{exception.Message}";
        }
    }

    private async void ImportTheme_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入冰蓝桌面主题",
            Filter = $"冰蓝桌面主题 (*{ThemeArchive.FileExtension})|*{ThemeArchive.FileExtension}"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var preview = _maintenance.ReadTheme(dialog.FileName);
            if (preview.Package is not { } package)
            {
                ThemeStatusText.Text = preview.Error;
                return;
            }

            var confirmDialog = new ThemeImportDialog(package, preview.PreviewPng) { Owner = this };
            if (confirmDialog.ShowDialog() != true)
            {
                return;
            }

            ThemeStatusText.Text = "正在匹配本机应用…";
            var outcome = await _maintenance.ImportTheme(dialog.FileName);
            ThemeStatusText.Text = outcome.Message;
            LoadDockSettings();
            LoadDesktopMode();
            RefreshBackupList();

            if (outcome.MissingSlots.Count > 0)
            {
                new ThemeBindingWizardWindow(outcome.MissingSlots, _maintenance.PinBoundApp) { Owner = this }
                    .ShowDialog();
                LoadDockSettings();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException)
        {
            ThemeStatusText.Text = $"导入未完成：{exception.Message}";
        }
    }

    private void LoadDesktopMode()
    {
        var mode = DesktopModeRules.Detect(_dockState, _taskbarState);
        _loadingDesktopMode = true;
        try
        {
            WindowsNativeModeRadio.IsChecked = mode == DesktopMode.WindowsNative;
            IceBlueHybridModeRadio.IsChecked = mode == DesktopMode.IceBlueHybrid;
            AppleStyleModeRadio.IsChecked = mode == DesktopMode.AppleStyle;
        }
        finally
        {
            _loadingDesktopMode = false;
        }

        var (activeMode, problem) = _taskbarStatus();
        DesktopModeStatusText.Text = mode is null
            ? "当前为自定义组合：可在“Dock”和“任务栏”页分别调整。"
            : problem.Length > 0 && activeMode != _taskbarState.Mode
                ? $"任务栏保持系统默认：{problem}"
                : "Dock 与任务栏也可在各自页面单独调整。";
    }

    private void DesktopMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingDesktopMode
            || sender is not RadioButton { Tag: string tag }
            || !Enum.TryParse<DesktopMode>(tag, out var mode))
        {
            return;
        }

        if (_topBarState is null)
        {
            DesktopModeRules.Apply(mode, _dockState, _taskbarState);
        }
        else
        {
            // The apple mode also turns the top bar on and hides the three information
            // cards it replaces, so the same facts do not show twice.
            DesktopModeRules.Apply(mode, _dockState, _taskbarState, _topBarState, _desktopExperience);
            _applyDesktopExperience(_desktopExperience);
            _applyTopBar?.Invoke();
            LoadTopBarSettings();
        }
        _applyDock();
        _applyTaskbar();
        LoadDockSettings();
        LoadTaskbarSettings();
        LoadDesktopMode();
    }

    private void LoadTaskbarSettings()
    {
        _loadingTaskbar = true;
        try
        {
            TaskbarDefaultRadio.IsChecked = _taskbarState.Mode == TaskbarMode.SystemDefault;
            TaskbarTransparentRadio.IsChecked = _taskbarState.Mode == TaskbarMode.Transparent;
            TaskbarBlurRadio.IsChecked = _taskbarState.Mode == TaskbarMode.Blur;
            TaskbarSmartHideRadio.IsChecked = _taskbarState.Mode == TaskbarMode.SmartHide;
            RevealOnlyAtCornersCheckBox.IsChecked = _taskbarState.RevealOnlyAtCorners;
        }
        finally
        {
            _loadingTaskbar = false;
        }

        var (activeMode, problem) = _taskbarStatus();
        TaskbarStatusText.Text = problem.Length > 0
            ? $"当前保持系统默认：{problem}"
            : activeMode switch
            {
                TaskbarMode.Transparent => "当前：透明",
                TaskbarMode.Blur => "当前：模糊",
                TaskbarMode.SmartHide => "当前：自动隐藏",
                _ => "当前：系统默认"
            };
    }

    private void RevealOnlyAtCorners_Click(object sender, RoutedEventArgs e)
    {
        _taskbarState.RevealOnlyAtCorners = RevealOnlyAtCornersCheckBox.IsChecked == true;
        _applyTaskbar();
        TaskbarStatusText.Text = _taskbarState.RevealOnlyAtCorners
            ? "任务栏只在底边左右两端弹出"
            : "已恢复：鼠标碰到底边任何位置都可弹出任务栏";
    }

    private void TaskbarMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingTaskbar
            || sender is not RadioButton { Tag: string tag }
            || !Enum.TryParse<TaskbarMode>(tag, out var mode))
        {
            return;
        }

        _taskbarState.Mode = mode;
        _applyTaskbar();
        LoadTaskbarSettings();
        LoadDesktopMode();
    }

    private void LoadDockSettings()
    {
        _loadingDock = true;
        try
        {
            DockEnabledCheckBox.IsChecked = _dockState.IsEnabled;
            DockOptionsPanel.IsEnabled = _dockState.IsEnabled;
            DockReserveRadio.IsChecked =
                _dockState.VisibilityMode == DockVisibilityMode.ReserveWorkArea;
            DockSmartHideRadio.IsChecked =
                _dockState.VisibilityMode == DockVisibilityMode.SmartHide;
            DockIconSizeSlider.Value = _dockState.IconSize;
            QuietTaskbarFlashCheckBox.IsChecked = !BingLan.App.Services.TaskbarFlashingSetting.IsEnabled();
            LoadDockLook();
            DockIconSizeText.Text = $"{_dockState.IconSize:0}";
            DockMaximizeReleaseCheckBox.IsChecked = _dockState.ReleaseWhenMaximized;
            DockBottomGapSlider.Value = _dockState.BottomGapDip;
            DockBottomGapText.Text = $"{_dockState.BottomGapDip:0} DIP";

            DockMonitorComboBox.Items.Clear();
            foreach (var monitor in MonitorCatalog.GetAll())
            {
                var item = new ComboBoxItem { Content = monitor.Label, Tag = monitor.DeviceName };
                DockMonitorComboBox.Items.Add(item);
                if (string.Equals(
                        monitor.DeviceName,
                        _dockState.MonitorDeviceName,
                        StringComparison.OrdinalIgnoreCase)
                    || (DockMonitorComboBox.SelectedItem is null && monitor.IsPrimary))
                {
                    DockMonitorComboBox.SelectedItem = item;
                }
            }

            RefreshDockPinnedList();
            RefreshDockHiddenList();
        }
        finally
        {
            _loadingDock = false;
        }
    }

    private void RefreshDockPinnedList(DockPinnedApp? selected = null)
    {
        var entries = _dockState.PinnedApps
            .Select(app => new DockPinnedEntry(app))
            .ToList();
        DockPinnedList.ItemsSource = entries;
        DockPinnedList.SelectedItem = entries.FirstOrDefault(entry => ReferenceEquals(entry.App, selected));
        DockPinnedEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshDockHiddenList()
    {
        var entries = _dockState.HiddenApps
            .Select(app => new DockPinnedEntry(app))
            .ToList();
        DockHiddenList.ItemsSource = entries;
        DockHiddenEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UnhideDockApp_Click(object sender, RoutedEventArgs e)
    {
        if (DockHiddenList.SelectedItem is not DockPinnedEntry entry)
        {
            DockStatusText.Text = "请先选择一个应用";
            return;
        }

        if (DockPinRules.Unhide(_dockState, DockPinRules.IdentityKey(entry.App)))
        {
            RefreshDockHiddenList();
            ApplyDockChange($"{entry.App.DisplayName} 会重新显示在 Dock 上");
        }
    }

    private void DockSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingDock || DockEnabledCheckBox is null || DockMonitorComboBox is null)
        {
            return;
        }

        _dockState.IsEnabled = DockEnabledCheckBox.IsChecked == true;
        DockOptionsPanel.IsEnabled = _dockState.IsEnabled;
        _dockState.VisibilityMode = DockSmartHideRadio.IsChecked == true
            ? DockVisibilityMode.SmartHide
            : DockVisibilityMode.ReserveWorkArea;
        _dockState.ReleaseWhenMaximized = DockMaximizeReleaseCheckBox.IsChecked == true;
        if (DockMonitorComboBox.SelectedItem is ComboBoxItem { Tag: string deviceName })
        {
            _dockState.MonitorDeviceName = deviceName;
        }
        ApplyDockChange(_dockState.IsEnabled ? "已应用" : "冰蓝 Dock 已关闭");
        LoadDesktopMode();
    }

    private bool _loadingDockLook;

    private void LoadDockLook()
    {
        _loadingDockLook = true;
        try
        {
            DockFollowLookRadio.IsChecked = _dockState.FollowCardLook;
            DockCustomLookRadio.IsChecked = !_dockState.FollowCardLook;
            DockCustomLookPanel.IsEnabled = !_dockState.FollowCardLook;
            DockColorEditor.Text = _dockState.SurfaceColor;
            DockOpacitySlider.Value = _dockState.SurfaceOpacity;
            DockOpacityText.Text = $"{_dockState.SurfaceOpacity * 100:0}%";
            var preview = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dockState.SurfaceColor)!);
            preview.Freeze();
            DockColorPreview.Background = preview;
            PaletteSwatches.Select(
                DockPaletteSwatches,
                _dockState.FollowCardLook
                    ? null
                    : DesktopStyleRules.Palettes.FirstOrDefault(palette => palette.Body == _dockState.SurfaceColor)?.Key);
        }
        finally
        {
            _loadingDockLook = false;
        }
    }

    private void DockLook_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingDockLook || _loadingDock || DockCustomLookRadio is null)
        {
            return;
        }

        _dockState.FollowCardLook = DockFollowLookRadio.IsChecked == true;
        ApplyDockChange(_dockState.FollowCardLook ? "Dock 跟随卡片外观" : "Dock 使用自定义外观");
        LoadDockLook();
    }

    private void DockOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingDockLook || _loadingDock || DockOpacityText is null)
        {
            return;
        }

        _dockState.SurfaceOpacity = e.NewValue;
        DockOpacityText.Text = $"{e.NewValue * 100:0}%";
        ApplyDockChange("已调整 Dock 不透明度");
    }

    private void DockColorEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            DockColorEditor_Commit(sender, e);
            e.Handled = true;
        }
    }

    private void DockColorEditor_Commit(object sender, RoutedEventArgs e)
    {
        if (_loadingDockLook)
        {
            return;
        }

        var color = WidgetAppearanceRules.CoerceColorOrDefault(DockColorEditor.Text, _dockState.SurfaceColor);
        if (color != _dockState.SurfaceColor)
        {
            _dockState.SurfaceColor = color;
            ApplyDockChange("已调整 Dock 颜色");
        }
        LoadDockLook();
    }

    private void DockChooseColor_Click(object sender, RoutedEventArgs e)
    {
        var current = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dockState.SurfaceColor)!;
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            FullOpen = true,
            AnyColor = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        _dockState.SurfaceColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        ApplyDockChange("已调整 Dock 颜色");
        LoadDockLook();
    }

    // ---------------------------------------------------------------- 顶端信息条

    private void LoadTopBarSettings()
    {
        if (_topBarState is not { } state)
        {
            return;
        }

        _loadingTopBar = true;
        try
        {
            TopBarEnabledCheckBox.IsChecked = state.IsEnabled;
            TopBarOptionsPanel.IsEnabled = state.IsEnabled;
            TopBarReserveRadio.IsChecked = state.VisibilityMode == TopBarVisibilityMode.ReserveTopEdge;
            TopBarSmartHideRadio.IsChecked = state.VisibilityMode == TopBarVisibilityMode.SmartHide;
            TopBarClockCheckBox.IsChecked = state.Modules.Clock;
            TopBarTodoSummaryCheckBox.IsChecked = state.Modules.TodoSummary;
            TopBarWeatherCheckBox.IsChecked = state.Modules.Weather;
            TopBarPerformanceCheckBox.IsChecked = state.Modules.Performance;
            TopBarAttentionCheckBox.IsChecked = state.Modules.Attention;
            TopBarInputMethodCheckBox.IsChecked = state.Modules.InputMethod;
            TopBarVolumeCheckBox.IsChecked = state.Modules.Volume;
            TopBarNetworkCheckBox.IsChecked = state.Modules.Network;
            TopBarBatteryCheckBox.IsChecked = state.Modules.Battery;

            TopBarMonitorComboBox.Items.Clear();
            foreach (var monitor in MonitorCatalog.GetAll())
            {
                var item = new ComboBoxItem { Content = monitor.Label, Tag = monitor.DeviceName };
                TopBarMonitorComboBox.Items.Add(item);
                if (string.Equals(
                        monitor.DeviceName,
                        state.MonitorDeviceName,
                        StringComparison.OrdinalIgnoreCase)
                    || (TopBarMonitorComboBox.SelectedItem is null && monitor.IsPrimary))
                {
                    TopBarMonitorComboBox.SelectedItem = item;
                }
            }

            LoadTopBarLook();
        }
        finally
        {
            _loadingTopBar = false;
        }
    }

    private void TopBarSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingTopBar || _topBarState is not { } state
            || TopBarEnabledCheckBox is null || TopBarMonitorComboBox is null)
        {
            return;
        }

        state.IsEnabled = TopBarEnabledCheckBox.IsChecked == true;
        TopBarOptionsPanel.IsEnabled = state.IsEnabled;
        state.VisibilityMode = TopBarSmartHideRadio.IsChecked == true
            ? TopBarVisibilityMode.SmartHide
            : TopBarVisibilityMode.ReserveTopEdge;
        state.Modules.Clock = TopBarClockCheckBox.IsChecked == true;
        state.Modules.TodoSummary = TopBarTodoSummaryCheckBox.IsChecked == true;
        state.Modules.Weather = TopBarWeatherCheckBox.IsChecked == true;
        state.Modules.Performance = TopBarPerformanceCheckBox.IsChecked == true;
        state.Modules.Attention = TopBarAttentionCheckBox.IsChecked == true;
        state.Modules.InputMethod = TopBarInputMethodCheckBox.IsChecked == true;
        state.Modules.Volume = TopBarVolumeCheckBox.IsChecked == true;
        state.Modules.Network = TopBarNetworkCheckBox.IsChecked == true;
        state.Modules.Battery = TopBarBatteryCheckBox.IsChecked == true;
        if (TopBarMonitorComboBox.SelectedItem is ComboBoxItem { Tag: string deviceName })
        {
            state.MonitorDeviceName = deviceName;
        }
        ApplyTopBarChange(state.IsEnabled ? "已应用" : "顶端信息条已关闭");
    }

    private void LoadTopBarLook()
    {
        if (_topBarState is not { } state)
        {
            return;
        }

        _loadingTopBarLook = true;
        try
        {
            TopBarFollowLookRadio.IsChecked = state.FollowCardLook;
            TopBarCustomLookRadio.IsChecked = !state.FollowCardLook;
            TopBarCustomLookPanel.IsEnabled = !state.FollowCardLook;
            TopBarColorEditor.Text = state.SurfaceColor;
            TopBarOpacitySlider.Value = state.SurfaceOpacity;
            TopBarOpacityText.Text = $"{state.SurfaceOpacity * 100:0}%";
            var preview = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(state.SurfaceColor)!);
            preview.Freeze();
            TopBarColorPreview.Background = preview;
        }
        finally
        {
            _loadingTopBarLook = false;
        }
    }

    private void TopBarLook_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingTopBarLook || _loadingTopBar || _topBarState is null || TopBarCustomLookRadio is null)
        {
            return;
        }

        _topBarState.FollowCardLook = TopBarFollowLookRadio.IsChecked == true;
        ApplyTopBarChange(_topBarState.FollowCardLook ? "信息条跟随卡片外观" : "信息条使用自定义外观");
        LoadTopBarLook();
    }

    private void TopBarOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingTopBarLook || _loadingTopBar || _topBarState is null || TopBarOpacityText is null)
        {
            return;
        }

        _topBarState.SurfaceOpacity = e.NewValue;
        TopBarOpacityText.Text = $"{e.NewValue * 100:0}%";
        ApplyTopBarChange("已调整信息条不透明度");
    }

    private void TopBarColorEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            TopBarColorEditor_Commit(sender, e);
            e.Handled = true;
        }
    }

    private void TopBarColorEditor_Commit(object sender, RoutedEventArgs e)
    {
        if (_loadingTopBarLook || _topBarState is not { } state)
        {
            return;
        }

        var color = WidgetAppearanceRules.CoerceColorOrDefault(TopBarColorEditor.Text, state.SurfaceColor);
        if (color != state.SurfaceColor)
        {
            state.SurfaceColor = color;
            ApplyTopBarChange("已调整信息条颜色");
        }
        LoadTopBarLook();
    }

    private void TopBarChooseColor_Click(object sender, RoutedEventArgs e)
    {
        if (_topBarState is not { } state)
        {
            return;
        }

        var current = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(state.SurfaceColor)!;
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            FullOpen = true,
            AnyColor = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        state.SurfaceColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        ApplyTopBarChange("已调整信息条颜色");
        LoadTopBarLook();
    }

    private void ApplyTopBarChange(string status)
    {
        _applyTopBar?.Invoke();
        if (TopBarStatusText is not null)
        {
            TopBarStatusText.Text = status;
        }
        LoadDesktopMode();
    }

    private void QuietTaskbarFlash_Click(object sender, RoutedEventArgs e)
    {
        var quiet = QuietTaskbarFlashCheckBox.IsChecked == true;
        try
        {
            BingLan.App.Services.TaskbarFlashingSetting.SetEnabled(!quiet);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or System.Security.SecurityException or IOException)
        {
            QuietTaskbarFlashCheckBox.IsChecked = !quiet;
            TaskbarStatusText.Text = $"无法更改任务栏闪烁设置：{exception.Message}";
            return;
        }
        TaskbarStatusText.Text = quiet
            ? "已关闭任务栏应用闪烁，新消息只在 Dock 上标记"
            : "已恢复 Windows 默认的任务栏应用闪烁";
    }

    private void DockIconSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingDock || DockIconSizeText is null)
        {
            return;
        }

        _dockState.IconSize = e.NewValue;
        DockIconSizeText.Text = $"{e.NewValue:0}";
        ApplyDockChange("已调整图标大小");
    }

    private void DockBottomGap_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingDock || DockBottomGapText is null)
        {
            return;
        }

        _dockState.BottomGapDip = e.NewValue;
        DockBottomGapText.Text = $"{e.NewValue:0} DIP";
        ApplyDockChange("已调整距底部高度");
    }

    private void ImportTaskbarPins_Click(object sender, RoutedEventArgs e)
    {
        var added = _maintenance.ImportTaskbarPins();
        RefreshDockPinnedList();
        DockStatusText.Text = added > 0 ? $"已导入 {added} 个应用" : "任务栏上的应用都已在 Dock 中";
    }

    private void AddDockApp_Click(object sender, RoutedEventArgs e)
    {
        AppPickerDialog.Open(this, _dockState.PinnedApps, chosen =>
        {
            var pinned = chosen.Where(app => DockPinRules.Pin(_dockState, app)).ToList();
            RefreshDockPinnedList(pinned.LastOrDefault());
            ApplyDockChange(pinned.Count > 0 ? $"已固定 {pinned.Count} 个应用" : "所选应用已在 Dock 中");
        });
    }

    private void MoveDockAppUp_Click(object sender, RoutedEventArgs e) => MoveSelectedDockApp(-1);

    private void MoveDockAppDown_Click(object sender, RoutedEventArgs e) => MoveSelectedDockApp(1);

    private void MoveSelectedDockApp(int offset)
    {
        if (DockPinnedList.SelectedItem is not DockPinnedEntry entry)
        {
            DockStatusText.Text = "请先选择一个应用";
            return;
        }

        var key = DockPinRules.IdentityKey(entry.App);
        if (DockPinRules.Move(_dockState, key, DockPinRules.IndexOf(_dockState, key) + offset))
        {
            RefreshDockPinnedList(entry.App);
            ApplyDockChange("已调整顺序");
        }
    }

    private void UnpinDockApp_Click(object sender, RoutedEventArgs e)
    {
        if (DockPinnedList.SelectedItem is not DockPinnedEntry entry)
        {
            DockStatusText.Text = "请先选择一个应用";
            return;
        }

        if (DockPinRules.Unpin(_dockState, DockPinRules.IdentityKey(entry.App)))
        {
            RefreshDockPinnedList();
            ApplyDockChange($"已取消固定 {entry.App.DisplayName}");
        }
    }

    private bool _loadingUpdates;

    private void LoadUpdateSettings()
    {
        _loadingUpdates = true;
        try
        {
            var updater = _maintenance.Updater;
            AppVersionText.Text = updater is null
                ? "冰蓝桌面"
                : $"冰蓝桌面 {updater.CurrentVersion.ToString(3)}";
            AutoCheckUpdatesCheckBox.IsChecked = updater?.AutoCheck == true;
            AutoCheckUpdatesCheckBox.IsEnabled = updater is not null;
        }
        finally
        {
            _loadingUpdates = false;
        }
        RefreshUpdateView();
    }

    private void RefreshUpdateView()
    {
        var updater = _maintenance.Updater;
        CheckUpdatesButton.IsEnabled = updater is { IsBusy: false };
        UpdateStatusText.Text = updater?.Status ?? "此运行方式不提供更新检查。";
        if (updater?.Available is { } release)
        {
            UpdateAvailableCard.Visibility = Visibility.Visible;
            UpdateAvailableTitle.Text = $"新版本 {release.Version.ToString(3)}";
            UpdateNotesText.Text = release.Notes.Length > 0 ? release.Notes : "这个版本没有附更新说明。";
            var installable = release.Installer is not null;
            InstallUpdateButton.Visibility = installable ? Visibility.Visible : Visibility.Collapsed;
            InstallUpdateButton.IsEnabled = !updater.IsBusy;
            InstallUpdateNoteText.Text = installable
                ? "安装时冰蓝桌面会暂时退出，装完自动重新打开。"
                : "这个版本没有可校验的安装包，请在发布页下载。";
        }
        else
        {
            UpdateAvailableCard.Visibility = Visibility.Collapsed;
        }
    }

    private void AutoCheckUpdates_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUpdates || _maintenance.Updater is not { } updater)
        {
            return;
        }
        updater.AutoCheck = AutoCheckUpdatesCheckBox.IsChecked == true;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_maintenance.Updater is { } updater)
        {
            await updater.CheckNowAsync();
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_maintenance.Updater is { } updater)
        {
            await updater.DownloadAndInstallAsync();
        }
    }

    private void OpenReleasePage_Click(object sender, RoutedEventArgs e) =>
        _maintenance.Updater?.OpenReleasePage();

    private void OpenRepository_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("https://github.com/keros68/binglan") { UseShellExecute = true })?.Dispose();

    private void ApplyDockChange(string status)
    {
        _applyDock();
        DockStatusText.Text = status;
    }

    private void SelectNavigationPage(string page)
    {
        page = PageFor(page);
        foreach (var candidate in SettingsNavigationList.Items.OfType<ListBoxItem>())
        {
            if (Equals(candidate.Tag, page))
            {
                SettingsNavigationList.SelectedItem = candidate;
                return;
            }
        }
    }

    private void RefreshComponentList(
        WidgetWindowBase? preferredWindow = null,
        DesktopComponentKind? preferredKind = null)
    {
        if (DesktopComponentList is null)
        {
            return;
        }

        var windows = _widgetProvider();
        var entries = new List<DesktopComponentSettingsEntry>();
        foreach (var kind in CurrentComponentKinds)
        {
            var matchingWindows = windows
                .Where(window =>
                    TryGetDesktopComponentKind(window, out var windowKind) &&
                    windowKind == kind)
                .ToArray();
            if (matchingWindows.Length == 0)
            {
                entries.Add(CreateComponentEntry(kind, null));
                continue;
            }

            entries.AddRange(matchingWindows.Select(window => CreateComponentEntry(kind, window)));
        }
        entries.AddRange(windows
            .Where(window => !TryGetDesktopComponentKind(window, out _))
            .Select(CreateWidgetEntry));

        DesktopComponentList.ItemsSource = entries;
        var selected = preferredWindow is not null
            ? entries.FirstOrDefault(entry => ReferenceEquals(entry.Window, preferredWindow))
            : null;
        selected ??= preferredKind is { } requestedKind
            ? entries.FirstOrDefault(entry => entry.Kind == requestedKind)
            : null;
        selected ??= entries.FirstOrDefault();
        DesktopComponentList.SelectedItem = selected;
        if (selected is not null)
        {
            DesktopComponentList.ScrollIntoView(selected);
        }
    }

    private DesktopComponentSettingsEntry CreateComponentEntry(
        DesktopComponentKind kind,
        WidgetWindowBase? window)
    {
        var component = _desktopExperience.GetComponent(kind);
        var locked = window?.IsWidgetLocked ?? component.IsLocked;
        return new DesktopComponentSettingsEntry(
            window is null ? DesktopExperienceRules.GetComponentName(kind) : GetWidgetName(window),
            kind == DesktopComponentKind.Todo ? "待办卡片" : "桌面信息",
            !component.IsVisible ? "未显示" : locked ? "已锁定" : "可拖动",
            kind,
            window);
    }

    private static DesktopComponentSettingsEntry CreateWidgetEntry(WidgetWindowBase window)
    {
        var typeName = window switch
        {
            NoteWidgetWindow => "普通便签",
            FileBoxWindow => "桌面分组盒",
            _ => "桌面卡片"
        };
        return new DesktopComponentSettingsEntry(
            GetWidgetName(window),
            typeName,
            window.IsWidgetLocked ? "已锁定" : "可拖动",
            null,
            window);
    }

    private static bool TryGetDesktopComponentKind(
        WidgetWindowBase window,
        out DesktopComponentKind kind)
    {
        switch (window)
        {
            case TodoWidgetWindow:
                kind = DesktopComponentKind.Todo;
                return true;
            case InformationWidgetWindow { ComponentKind: { } componentKind }:
                kind = componentKind;
                return true;
            case QuickPlacesWindow places:
                kind = places.ComponentKind;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static string GetWidgetName(WidgetWindowBase window) => window switch
    {
        TodoWidgetWindow todo => todo.State.Title,
        NoteWidgetWindow note => note.State.Title,
        FileBoxWindow box => box.State.Title,
        InformationWidgetWindow { ComponentKind: { } kind } =>
            DesktopExperienceRules.GetComponentName(kind),
        _ => window.Title
    };

    private void PresetRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingExperience || sender is not RadioButton radio || radio.Tag is not string tag)
        {
            return;
        }
        if (Enum.TryParse<DesktopLayoutPreset>(tag, out var preset))
        {
            UpdatePresetPreview(preset);
        }
    }

    // Previews are drawn from the same placements the preset applies, in the current
    // card colours, so they show exactly what applying the preset will do.
    private void UpdatePresetPreview(DesktopLayoutPreset preset)
    {
        PresetPreviewImage.Source = ThemePreviewRenderer.RenderPreset(preset, _desktopExperience);
        QuietPresetThumb.Source ??= ThemePreviewRenderer.RenderPreset(DesktopLayoutPreset.QuietInformation, _desktopExperience);
        GlacierPresetThumb.Source ??= ThemePreviewRenderer.RenderPreset(DesktopLayoutPreset.GlacierWorkbench, _desktopExperience);
        ApplePresetThumb.Source ??= ThemePreviewRenderer.RenderPreset(DesktopLayoutPreset.TransparentApple, _desktopExperience);
        CenterClockPresetThumb.Source ??= ThemePreviewRenderer.RenderPreset(DesktopLayoutPreset.CenterClock, _desktopExperience);
    }

    private void ApplyLayout_Click(object sender, RoutedEventArgs e)
    {
        _desktopExperience.ActivePreset = GetSelectedPreset();
        DesktopExperienceRules.Normalize(_desktopExperience);
        _applyLayoutPreset(_desktopExperience);
        LayoutStatusText.Text =
            $"已应用“{DesktopExperienceRules.GetPresetName(_desktopExperience.ActivePreset)}”";
    }

    private DesktopLayoutPreset GetSelectedPreset()
    {
        if (GlacierWorkbenchPresetRadio.IsChecked == true)
        {
            return DesktopLayoutPreset.GlacierWorkbench;
        }
        if (CenterClockPresetRadio.IsChecked == true)
        {
            return DesktopLayoutPreset.CenterClock;
        }
        return TransparentApplePresetRadio.IsChecked == true
            ? DesktopLayoutPreset.TransparentApple
            : DesktopLayoutPreset.QuietInformation;
    }

    private static (double Width, double Height) GetMinimumComponentSize(
        DesktopComponentKind kind) =>
        kind == DesktopComponentKind.Todo ? (226d, 166d) : (180d, 72d);

    private static double ParseSize(
        string value,
        double fallback,
        double minimum,
        double maximum) =>
        double.TryParse(value, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;

    private async void SearchCity_Click(object sender, RoutedEventArgs e) =>
        await SearchCityAsync();

    private async Task SearchCityAsync()
    {
        CancelSearch();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        SearchCityButton.IsEnabled = false;
        ApplyCityButton.IsEnabled = false;
        CityResultsList.ItemsSource = null;
        CityResultsList.Visibility = Visibility.Collapsed;
        CitySearchStatusText.Text = "正在搜索…";

        try
        {
            var outcome = await _citySearchService.SearchAsync(
                CityQueryEditor.Text,
                cancellation.Token);
            if (cancellation.IsCancellationRequested ||
                !ReferenceEquals(_searchCancellation, cancellation))
            {
                return;
            }

            CityResultsList.ItemsSource = outcome.Results;
            // The list only takes room while there is something to choose.
            CityResultsList.Visibility = outcome.Results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            CitySearchStatusText.Text = outcome.Message;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 关闭窗口或开始新搜索时安静取消旧请求。
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                _searchCancellation = null;
                SearchCityButton.IsEnabled = true;
            }
            cancellation.Dispose();
        }
    }

    private void CityQueryEditor_KeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        _ = SearchCityAsync();
        e.Handled = true;
    }

    private void CityResultsList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        ApplyCityButton.IsEnabled = CityResultsList.SelectedItem is CitySearchResult;

    private void ApplyCity_Click(object sender, RoutedEventArgs e)
    {
        if (CityResultsList.SelectedItem is not CitySearchResult selected)
        {
            return;
        }

        _applyCity(selected);
        UpdateCurrentCity(selected.DisplayName);
        CitySearchStatusText.Text = "城市已保存，正在刷新天气";
    }

    private void UpdateCurrentCity(string city) =>
        CurrentCityText.Text = string.IsNullOrWhiteSpace(city)
            ? "当前城市：未设置"
            : $"当前城市：{city.Trim()}";

    private void CancelSearch()
    {
        _searchCancellation?.Cancel();
        _searchCancellation = null;
    }

    public sealed record DesktopComponentSettingsEntry(
        string Name,
        string TypeName,
        string Status,
        DesktopComponentKind? Kind,
        WidgetWindowBase? Window);
}

internal sealed class DockPinnedEntry(DockPinnedApp app)
{
    public DockPinnedApp App { get; } = app;
    public string DisplayName => App.DisplayName;
    public string Detail => App.ExecutablePath ?? "Microsoft Store 或网页应用";
}

internal sealed class BackupEntry(string path)
{
    public string Path { get; } = path;

    // The time plus what the backup holds, so the right one can be picked without opening it.
    public string Label { get; } = LocalStateStore.DescribeBackup(path) is { } summary
        ? $"{File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}  ·  {summary.PresetName}  ·  {summary.CardCount} 张卡片  ·  Dock {summary.DockAppCount} 个应用"
        : $"{File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}  ·  无法读取此备份";
}
