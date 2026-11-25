using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using BingLan.Core.Models;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;

namespace BingLan.App.Windows;

public partial class SettingsWindow
{
    private string? _activeScreenKey;
    private IReadOnlyList<MonitorCardOption> _multiScreenOptions = [];
    private bool _updatingMultiScreenUi;

    internal string? ActiveScreenKey => _activeScreenKey;

    internal bool IsMultiScreenUiVisible =>
        SchemeMultiScreenHost?.Visibility == Visibility.Visible;

    internal bool IsMultiScreenTopologyBarVisible =>
        ComponentsMultiScreenBarHost?.Visibility == Visibility.Visible;

    internal bool HasInstantiatedMultiScreenChildren =>
        SchemeMultiScreenHost?.Child is not null
        || ComponentsMultiScreenBarHost?.Child is not null
        || LayoutMultiScreenBarHost?.Child is not null
        || BuiltInMoveScreenHost?.Child is not null
        || SelectedWidgetMoveScreenHost?.Child is not null;

    private bool IsMultiScreenActive =>
        _maintenance.MultiScreen is { } bridge
        && bridge.IsEnabled()
        && bridge.HasMultipleMonitors();

    private DesktopExperienceState ActiveDesktopExperience =>
        _maintenance.MultiScreen is { } bridge
        && bridge.IsEnabled()
        && _activeScreenKey is not null
            ? bridge.GetExperience(_activeScreenKey)
            : _desktopExperience;

    private DesktopExperienceState GetExperienceForInformationWindow(InformationWidgetWindow window)
    {
        if (_maintenance.MultiScreen is { } bridge && bridge.IsEnabled())
        {
            var screenKey = bridge.GetScreenKeyForWindow(window);
            if (screenKey is not null)
            {
                return bridge.GetExperience(screenKey);
            }
        }

        return _desktopExperience;
    }

    private void ApplyActiveDesktopExperience(DesktopExperienceState experience)
    {
        if (_maintenance.MultiScreen is { } bridge && bridge.IsEnabled() && _activeScreenKey is not null)
        {
            bridge.ApplyExperience(_activeScreenKey, experience);
            RefreshMultiScreenTopologyBarsOnly();
            return;
        }

        _applyDesktopExperience(experience);
        if (IsMultiScreenActive)
        {
            RefreshMultiScreenTopologyBarsOnly();
        }
    }

    private void ApplyActiveLayoutPreset(DesktopExperienceState experience)
    {
        if (_maintenance.MultiScreen is { } bridge && bridge.IsEnabled() && _activeScreenKey is not null)
        {
            bridge.ApplyLayoutPreset(_activeScreenKey, experience);
            RefreshMultiScreenTopologyBarsOnly();
            return;
        }

        _applyLayoutPreset(experience);
        if (IsMultiScreenActive)
        {
            RefreshMultiScreenTopologyBarsOnly();
        }
    }

    private IReadOnlyList<WidgetWindowBase> GetActiveScreenWindows()
    {
        if (_maintenance.MultiScreen is { } bridge && bridge.IsEnabled())
        {
            return bridge.GetWindowsForScreen(_activeScreenKey);
        }

        return _widgetProvider();
    }

    private Guid? GetActivePrimaryTodoId()
    {
        if (_maintenance.MultiScreen is { } bridge && bridge.IsEnabled())
        {
            return bridge.GetPrimaryTodoId(_activeScreenKey);
        }

        return _maintenance.PrimaryTodoId();
    }

    private IReadOnlyList<QuickPlaceState> GetActiveQuickPlaces()
    {
        if (_maintenance.MultiScreen is { } bridge && bridge.IsEnabled() && _activeScreenKey is not null)
        {
            return bridge.GetQuickPlaces(_activeScreenKey);
        }

        return _maintenance.QuickPlaces();
    }

    private void SaveActiveQuickPlaces(List<QuickPlaceState> items)
    {
        if (_maintenance.MultiScreen is { } bridge && bridge.IsEnabled() && _activeScreenKey is not null)
        {
            bridge.SetQuickPlaces(_activeScreenKey, items);
            return;
        }

        _maintenance.SetQuickPlaces(items);
    }

    private WidgetWindowBase? AddWidgetOnActiveScreen(NewWidgetKind kind)
    {
        if (_maintenance.MultiScreen is { } bridge && bridge.IsEnabled() && _activeScreenKey is not null)
        {
            var created = bridge.AddWidgetOnScreen(_activeScreenKey, kind);
            RefreshMultiScreenTopologyBarsOnly();
            return created;
        }

        var result = _maintenance.AddWidget(kind);
        if (IsMultiScreenActive)
        {
            RefreshMultiScreenTopologyBarsOnly();
        }
        return result;
    }

    private bool ResetActiveWidgetPlacement(WidgetWindowBase? window, DesktopComponentKind? kind)
    {
        if (_maintenance.MultiScreen is { } bridge && bridge.IsEnabled() && _activeScreenKey is not null)
        {
            return bridge.ResetWidgetPlacement(_activeScreenKey, window, kind);
        }

        return _maintenance.ResetWidgetPlacement(window, kind);
    }

    private string ActiveScreenDisplayName()
    {
        if (_activeScreenKey is null)
        {
            return "主屏";
        }

        return _multiScreenOptions
            .FirstOrDefault(option => string.Equals(option.ScreenKey, _activeScreenKey, StringComparison.Ordinal))
            ?.DisplayLabel ?? "副屏";
    }

    private void SyncActiveScreenForTargetWindow(WidgetWindowBase window)
    {
        if (_maintenance.MultiScreen is not { } bridge || !bridge.IsEnabled())
        {
            _activeScreenKey = null;
            return;
        }

        var key = bridge.GetScreenKeyForWindow(window);
        if (!string.Equals(_activeScreenKey, key, StringComparison.Ordinal))
        {
            _activeScreenKey = key;
            RefreshMultiScreenUi();
        }
    }

    private void InitializeMultiScreenUi() => RefreshMultiScreenUi();

    internal void RefreshMultiScreenUi(bool refreshComponentList = false)
    {
        if (_isClosed)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => RefreshMultiScreenUi(refreshComponentList));
            return;
        }

        var bridge = _maintenance.MultiScreen;
        if (bridge is null)
        {
            CollapseAllMultiScreenHosts();
            return;
        }

        var hasMultiple = bridge.HasMultipleMonitors();
        var hasSavedSecondary = bridge.HasSavedSecondaryState();
        if (!hasMultiple && !hasSavedSecondary)
        {
            // Strict zero-overhead single-screen path: collapse all placeholders with Child = null.
            CollapseAllMultiScreenHosts();
            return;
        }

        var enabled = bridge.IsEnabled();
        var connectedCount = hasMultiple ? Math.Max(2, bridge.GetConnectedMonitorCount()) : 1;
        // Never invoke ResolveConnected()/QueryDisplayConfig while multi-screen is disabled.
        IReadOnlyList<MonitorCardOption> screens = enabled && hasMultiple ? bridge.GetScreens() : [];
        var disconnected = bridge.GetDisconnectedWidgets();
        _multiScreenOptions = screens;

        if (!enabled
            || _activeScreenKey is not null
            && !screens.Any(option => string.Equals(option.ScreenKey, _activeScreenKey, StringComparison.Ordinal)))
        {
            _activeScreenKey = null;
        }

        _updatingMultiScreenUi = true;
        try
        {
            BuildSchemeMultiScreenSection(bridge, hasMultiple, enabled, connectedCount, disconnected);

            if (enabled && screens.Count >= 2)
            {
                ComponentsMultiScreenBarHost.Child = BuildTopologySwitcherCard(
                    bridge,
                    screens,
                    disconnected,
                    isLayoutBar: false);
                ComponentsMultiScreenBarHost.Visibility = Visibility.Visible;

                LayoutMultiScreenBarHost.Child = BuildTopologySwitcherCard(
                    bridge,
                    screens,
                    disconnected: [],
                    isLayoutBar: true);
                LayoutMultiScreenBarHost.Visibility = Visibility.Visible;
            }
            else if (disconnected.Count > 0)
            {
                ComponentsMultiScreenBarHost.Child = BuildDisconnectedRecoveryBanner(bridge, disconnected);
                ComponentsMultiScreenBarHost.Visibility = Visibility.Visible;
                LayoutMultiScreenBarHost.Child = null;
                LayoutMultiScreenBarHost.Visibility = Visibility.Collapsed;
            }
            else
            {
                ComponentsMultiScreenBarHost.Child = null;
                ComponentsMultiScreenBarHost.Visibility = Visibility.Collapsed;
                LayoutMultiScreenBarHost.Child = null;
                LayoutMultiScreenBarHost.Visibility = Visibility.Collapsed;
            }

            UpdateMoveScreenControls(_selectedComponentEntry);
        }
        finally
        {
            _updatingMultiScreenUi = false;
        }

        if (refreshComponentList && !_isClosed)
        {
            RefreshComponentList(_selectedComponentEntry?.Window, _selectedComponentEntry?.Kind);
        }
    }

    private void RefreshMultiScreenTopologyBarsOnly()
    {
        if (_isClosed || _updatingMultiScreenUi || _maintenance.MultiScreen is not { } bridge)
        {
            return;
        }

        if (!bridge.IsEnabled() || !bridge.HasMultipleMonitors())
        {
            return;
        }

        var screens = bridge.GetScreens();
        var disconnected = bridge.GetDisconnectedWidgets();
        _multiScreenOptions = screens;
        if (screens.Count >= 2)
        {
            ComponentsMultiScreenBarHost.Child = BuildTopologySwitcherCard(bridge, screens, disconnected, isLayoutBar: false);
            ComponentsMultiScreenBarHost.Visibility = Visibility.Visible;
            LayoutMultiScreenBarHost.Child = BuildTopologySwitcherCard(bridge, screens, disconnected: [], isLayoutBar: true);
            LayoutMultiScreenBarHost.Visibility = Visibility.Visible;
        }
    }

    internal void SelectActiveScreen(string? screenKey)
    {
        var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
        if (string.Equals(_activeScreenKey, normalized, StringComparison.Ordinal))
        {
            return;
        }

        CommitPendingTitleEdits();
        if (_componentApplyTimer.IsEnabled)
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: false);
        }

        _activeScreenKey = normalized;
        RefreshMultiScreenUi();
        SyncPresetRadiosForActiveScreen();
        RefreshComponentList();
    }

    internal void SetMultiScreenEnabledFromUi(bool enabled)
    {
        if (_maintenance.MultiScreen is not { } bridge)
        {
            return;
        }

        bridge.SetEnabled(enabled);
        if (!enabled)
        {
            _activeScreenKey = null;
        }
        RefreshMultiScreenUi();
        SyncPresetRadiosForActiveScreen();
        RefreshComponentList();
    }

    private void CollapseAllMultiScreenHosts()
    {
        _activeScreenKey = null;
        _multiScreenOptions = [];
        if (SchemeMultiScreenHost is not null)
        {
            SchemeMultiScreenHost.Child = null;
            SchemeMultiScreenHost.Visibility = Visibility.Collapsed;
        }
        if (ComponentsMultiScreenBarHost is not null)
        {
            ComponentsMultiScreenBarHost.Child = null;
            ComponentsMultiScreenBarHost.Visibility = Visibility.Collapsed;
        }
        if (LayoutMultiScreenBarHost is not null)
        {
            LayoutMultiScreenBarHost.Child = null;
            LayoutMultiScreenBarHost.Visibility = Visibility.Collapsed;
        }
        if (BuiltInMoveScreenHost is not null)
        {
            BuiltInMoveScreenHost.Child = null;
            BuiltInMoveScreenHost.Visibility = Visibility.Collapsed;
        }
        if (SelectedWidgetMoveScreenHost is not null)
        {
            SelectedWidgetMoveScreenHost.Child = null;
            SelectedWidgetMoveScreenHost.Visibility = Visibility.Collapsed;
        }
    }

    private void SyncPresetRadiosForActiveScreen()
    {
        var experience = ActiveDesktopExperience;
        DesktopExperienceRules.Normalize(experience);
        var previousLoading = _loadingExperience;
        _loadingExperience = true;
        try
        {
            if (QuietInformationPresetRadio is not null)
            {
                QuietInformationPresetRadio.IsChecked =
                    experience.ActivePreset == DesktopLayoutPreset.QuietInformation;
            }
            if (GlacierWorkbenchPresetRadio is not null)
            {
                GlacierWorkbenchPresetRadio.IsChecked =
                    experience.ActivePreset == DesktopLayoutPreset.GlacierWorkbench;
            }
            if (TransparentApplePresetRadio is not null)
            {
                TransparentApplePresetRadio.IsChecked =
                    experience.ActivePreset == DesktopLayoutPreset.TransparentApple;
            }
            if (CenterClockPresetRadio is not null)
            {
                CenterClockPresetRadio.IsChecked =
                    experience.ActivePreset == DesktopLayoutPreset.CenterClock;
            }
            UpdatePresetPreview(experience.ActivePreset);
        }
        finally
        {
            _loadingExperience = previousLoading;
        }
    }

    private void BuildSchemeMultiScreenSection(
        MultiScreenSettingsBridge bridge,
        bool hasMultiple,
        bool enabled,
        int connectedMonitorCount,
        IReadOnlyList<DisconnectedScreenWidget> disconnected)
    {
        var stack = new StackPanel();
        var header = new TextBlock { Text = "多屏" };
        if (TryFindResource("SectionHeaderStyle") is Style headerStyle)
        {
            header.Style = headerStyle;
        }
        stack.Children.Add(header);

        var group = new SettingsGroup();
        var toggleRow = new SettingsRow
        {
            Header = "每屏独立桌面卡片",
            Description = hasMultiple
                ? $"已识别 {connectedMonitorCount} 台显示器；开启后可分别为每块屏幕配置桌面卡片与布局预设，新副屏默认保持空白。关闭后立即释放副屏窗口并保留配置。"
                : "当前仅连接 1 台显示器；已保存的副屏配置会在重连原显示器时自动恢复。"
        };

        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        if (enabled && hasMultiple)
        {
            var jumpButton = new Button
            {
                Content = "管理各屏卡片 →",
                Margin = new Thickness(0, 0, 12, 0)
            };
            if (TryFindResource("GhostButtonStyle") is Style ghostStyle)
            {
                jumpButton.Style = ghostStyle;
            }
            AutomationProperties.SetName(jumpButton, "前往桌面卡片管理各屏");
            jumpButton.Click += (_, _) =>
            {
                SelectNavigationPage("Components");
                ShowPage("Components");
                RefreshMultiScreenUi();
            };
            controls.Children.Add(jumpButton);
        }

        var toggle = new CheckBox
        {
            IsChecked = enabled,
            IsEnabled = hasMultiple || enabled
        };
        if (TryFindResource("ToggleSwitchStyle") is Style toggleStyle)
        {
            toggle.Style = toggleStyle;
        }
        AutomationProperties.SetName(toggle, "每屏独立桌面卡片");
        toggle.Checked += (_, _) =>
        {
            if (!_updatingMultiScreenUi)
            {
                SetMultiScreenEnabledFromUi(true);
            }
        };
        toggle.Unchecked += (_, _) =>
        {
            if (!_updatingMultiScreenUi)
            {
                SetMultiScreenEnabledFromUi(false);
            }
        };
        controls.Children.Add(toggle);
        toggleRow.Content = controls;
        group.Items.Add(toggleRow);
        stack.Children.Add(group);

        if (disconnected.Count > 0)
        {
            var recovery = BuildDisconnectedRecoveryBanner(bridge, disconnected);
            recovery.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(recovery);
        }

        SchemeMultiScreenHost.Child = stack;
        SchemeMultiScreenHost.Visibility = Visibility.Visible;
    }

    private Border BuildTopologySwitcherCard(
        MultiScreenSettingsBridge bridge,
        IReadOnlyList<MonitorCardOption> screens,
        IReadOnlyList<DisconnectedScreenWidget> disconnected,
        bool isLayoutBar)
    {
        var outer = new Border
        {
            Padding = new Thickness(14, 10, 14, 10)
        };
        if (TryFindResource("SettingsPanelStyle") is Style panelStyle)
        {
            outer.Style = panelStyle;
        }

        var rootStack = new StackPanel();
        var rowGrid = new Grid();
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left: proportional mini monitor topology map reflecting real bounds and Y offsets.
        var miniMap = BuildMiniTopologyCanvas(screens);
        Grid.SetColumn(miniMap, 0);
        rowGrid.Children.Add(miniMap);

        // Center: screen selector buttons + summary.
        var centerStack = new StackPanel
        {
            Margin = new Thickness(14, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var titleLine = new TextBlock
        {
            Text = isLayoutBar
                ? $"当前布局作用屏幕：{ActiveScreenDisplayName()}"
                : $"当前正在配置：{ActiveScreenDisplayName()}",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        };
        centerStack.Children.Add(titleLine);

        var pillWrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var screen in screens)
        {
            var isSelected = string.Equals(screen.ScreenKey, _activeScreenKey, StringComparison.Ordinal);
            var pill = new Button
            {
                Margin = new Thickness(0, 0, 8, 4),
                Padding = new Thickness(10, 4, 10, 4),
                Content = $"{screen.ShortLabel} · {screen.ResolutionSummary} ({screen.CardCount}张)",
                ToolTip = screen.DisplayLabel
            };
            if (isSelected && TryFindResource("AccentButtonStyle") is Style accentStyle)
            {
                pill.Style = accentStyle;
            }
            AutomationProperties.SetName(pill, $"选择屏幕 {screen.ShortLabel}");
            var targetKey = screen.ScreenKey;
            pill.Click += (_, _) => SelectActiveScreen(targetKey);
            pillWrap.Children.Add(pill);
        }
        centerStack.Children.Add(pillWrap);
        Grid.SetColumn(centerStack, 1);
        rowGrid.Children.Add(centerStack);

        // Right: copy primary profile action when configuring a secondary screen.
        if (_activeScreenKey is { } secondaryKey)
        {
            var copyButton = new Button
            {
                Content = "从主屏复制方案到本屏",
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(12, 6, 12, 6)
            };
            AutomationProperties.SetName(copyButton, "从主屏复制方案到本屏");
            copyButton.Click += (_, _) =>
            {
                var status = bridge.CopyPrimaryProfileToScreen(secondaryKey);
                if (ComponentSettingsStatusText is not null)
                {
                    ComponentSettingsStatusText.Text = status;
                }
                if (LayoutStatusText is not null)
                {
                    LayoutStatusText.Text = status;
                }
                RefreshMultiScreenUi();
                SyncPresetRadiosForActiveScreen();
                RefreshComponentList();
            };
            Grid.SetColumn(copyButton, 2);
            rowGrid.Children.Add(copyButton);
        }

        rootStack.Children.Add(rowGrid);

        if (disconnected.Count > 0)
        {
            var recovery = BuildDisconnectedRecoveryBanner(bridge, disconnected);
            recovery.Margin = new Thickness(0, 10, 0, 0);
            rootStack.Children.Add(recovery);
        }

        outer.Child = rootStack;
        return outer;
    }

    private Border BuildMiniTopologyCanvas(IReadOnlyList<MonitorCardOption> screens)
    {
        const double canvasWidth = 148d;
        const double canvasHeight = 52d;
        const double pad = 4d;

        var frame = new Border
        {
            Width = canvasWidth,
            Height = canvasHeight,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true
        };
        frame.SetResourceReference(Border.BackgroundProperty, "SettingsPreviewBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "SettingsSurfaceBorderBrush");

        var canvas = new Canvas { Width = canvasWidth, Height = canvasHeight };
        if (screens.Count > 0)
        {
            var minX = screens.Min(s => s.Bounds.Left);
            var minY = screens.Min(s => s.Bounds.Top);
            var maxX = screens.Max(s => s.Bounds.Right);
            var maxY = screens.Max(s => s.Bounds.Bottom);
            var spanX = Math.Max(1d, maxX - minX);
            var spanY = Math.Max(1d, maxY - minY);
            var availW = canvasWidth - (pad * 2d);
            var availH = canvasHeight - (pad * 2d);
            var scale = Math.Min(availW / spanX, availH / spanY);
            var drawnW = spanX * scale;
            var drawnH = spanY * scale;
            var offsetX = (canvasWidth - drawnW) / 2d;
            var offsetY = (canvasHeight - drawnH) / 2d;

            foreach (var screen in screens)
            {
                var left = offsetX + ((screen.Bounds.Left - minX) * scale);
                var top = offsetY + ((screen.Bounds.Top - minY) * scale);
                var width = Math.Max(20d, (screen.Bounds.Width * scale) - 2d);
                var height = Math.Max(16d, (screen.Bounds.Height * scale) - 2d);
                var isSelected = string.Equals(screen.ScreenKey, _activeScreenKey, StringComparison.Ordinal);

                var tile = new Button
                {
                    Width = width,
                    Height = height,
                    Padding = new Thickness(0),
                    MinWidth = 0,
                    MinHeight = 0,
                    BorderThickness = new Thickness(isSelected ? 2 : 1),
                    ToolTip = $"{screen.DisplayLabel}\n{screen.ResolutionSummary}\n已显示 {screen.CardCount} 张卡片",
                    Content = new TextBlock
                    {
                        Text = screen.IsPrimary ? "主" : screen.DisplayIndex.ToString(),
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                tile.SetResourceReference(
                    Control.BackgroundProperty,
                    isSelected ? "SettingsAccentSoftBrush" : "SettingsSurfaceBrush");
                tile.SetResourceReference(
                    Control.BorderBrushProperty,
                    isSelected ? "SettingsSelectedTextBrush" : "SettingsControlBorderBrush");
                AutomationProperties.SetName(tile, $"拓扑切换到 {screen.ShortLabel}");
                var targetKey = screen.ScreenKey;
                tile.Click += (_, _) => SelectActiveScreen(targetKey);
                Canvas.SetLeft(tile, left + 1d);
                Canvas.SetTop(tile, top + 1d);
                canvas.Children.Add(tile);
            }
        }

        frame.Child = canvas;
        return frame;
    }

    private Border BuildDisconnectedRecoveryBanner(
        MultiScreenSettingsBridge bridge,
        IReadOnlyList<DisconnectedScreenWidget> disconnected)
    {
        var banner = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8)
        };
        banner.SetResourceReference(Border.BackgroundProperty, "SettingsWarnBrush");
        banner.SetResourceReference(Border.BorderBrushProperty, "SettingsSurfaceBorderBrush");

        var stack = new StackPanel();
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var notice = new TextBlock
        {
            Text = $"有 {disconnected.Count} 张卡片位于当前未连接的副屏（重连该显示器会自动恢复，或立即移回主屏）：",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        notice.SetResourceReference(TextBlock.ForegroundProperty, "SettingsWarnTextBrush");
        Grid.SetColumn(notice, 0);
        headerGrid.Children.Add(notice);

        var moveAllButton = new Button
        {
            Content = "全部移回主屏",
            Margin = new Thickness(10, 0, 0, 0),
            Padding = new Thickness(10, 4, 10, 4)
        };
        AutomationProperties.SetName(moveAllButton, "将离线副屏卡片全部移回主屏");
        moveAllButton.Click += (_, _) =>
        {
            var totalMoved = 0;
            foreach (var screenKey in disconnected.Select(item => item.ScreenKey).Distinct(StringComparer.Ordinal))
            {
                totalMoved += bridge.MoveAllDisconnectedFromScreenToPrimary(screenKey);
            }
            if (ComponentSettingsStatusText is not null)
            {
                ComponentSettingsStatusText.Text = totalMoved > 0
                    ? $"已将 {totalMoved} 张离线卡片移回主屏"
                    : "没有可移回主屏的卡片";
            }
            RefreshMultiScreenUi();
            RefreshComponentList();
        };
        Grid.SetColumn(moveAllButton, 1);
        headerGrid.Children.Add(moveAllButton);
        stack.Children.Add(headerGrid);

        var itemWrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var item in disconnected.Take(6))
        {
            var moveOne = new Button
            {
                Content = $"移回主屏：{item.Title}（{item.KindName}）",
                Margin = new Thickness(0, 2, 8, 2),
                Padding = new Thickness(8, 3, 8, 3),
                FontSize = 12
            };
            AutomationProperties.SetName(moveOne, $"将 {item.Title} 移回主屏");
            var id = item.Id;
            var kindName = item.KindName;
            var title = item.Title;
            moveOne.Click += (_, _) =>
            {
                if (bridge.MoveDisconnectedWidgetToPrimary(id, kindName))
                {
                    if (ComponentSettingsStatusText is not null)
                    {
                        ComponentSettingsStatusText.Text = $"已将“{title}”移回主屏";
                    }
                    RefreshMultiScreenUi();
                    RefreshComponentList();
                }
            };
            itemWrap.Children.Add(moveOne);
        }
        stack.Children.Add(itemWrap);

        banner.Child = stack;
        return banner;
    }

    private void UpdateMoveScreenControls(DesktopComponentSettingsEntry? entry)
    {
        if (BuiltInMoveScreenHost is null || SelectedWidgetMoveScreenHost is null)
        {
            return;
        }

        var bridge = _maintenance.MultiScreen;
        if (bridge is null
            || !bridge.IsEnabled()
            || _multiScreenOptions.Count < 2
            || entry?.Window is not { } targetWindow)
        {
            BuiltInMoveScreenHost.Child = null;
            BuiltInMoveScreenHost.Visibility = Visibility.Collapsed;
            SelectedWidgetMoveScreenHost.Child = null;
            SelectedWidgetMoveScreenHost.Visibility = Visibility.Collapsed;
            return;
        }

        var currentKey = bridge.GetScreenKeyForWindow(targetWindow);
        var otherScreens = _multiScreenOptions
            .Where(option => option.IsConnected
                && !string.Equals(option.ScreenKey, currentKey, StringComparison.Ordinal))
            .ToList();
        if (otherScreens.Count == 0)
        {
            BuiltInMoveScreenHost.Child = null;
            BuiltInMoveScreenHost.Visibility = Visibility.Collapsed;
            SelectedWidgetMoveScreenHost.Child = null;
            SelectedWidgetMoveScreenHost.Visibility = Visibility.Collapsed;
            return;
        }

        var buttonRow = new WrapPanel();
        foreach (var destination in otherScreens)
        {
            var moveButton = new Button
            {
                Content = $"移至 {destination.ShortLabel}",
                Margin = new Thickness(0, 0, 6, 0)
            };
            AutomationProperties.SetName(moveButton, $"移至{destination.ShortLabel}");
            var destKey = destination.ScreenKey;
            moveButton.Click += (_, _) =>
            {
                var result = bridge.MoveWidgetToScreen(targetWindow, destKey);
                if (ComponentSettingsStatusText is not null)
                {
                    ComponentSettingsStatusText.Text = result.Message;
                }
                if (result.Succeeded)
                {
                    _activeScreenKey = destKey;
                    RefreshMultiScreenUi();
                    SyncPresetRadiosForActiveScreen();
                    RefreshComponentList(result.ResultingWindow ?? targetWindow, entry.Kind);
                }
            };
            buttonRow.Children.Add(moveButton);
        }

        if (entry.Kind is not null)
        {
            BuiltInMoveScreenHost.Child = buttonRow;
            BuiltInMoveScreenHost.Visibility = Visibility.Visible;
            SelectedWidgetMoveScreenHost.Child = null;
            SelectedWidgetMoveScreenHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            SelectedWidgetMoveScreenHost.Child = buttonRow;
            SelectedWidgetMoveScreenHost.Visibility = Visibility.Visible;
            BuiltInMoveScreenHost.Child = null;
            BuiltInMoveScreenHost.Visibility = Visibility.Collapsed;
        }
    }
}
