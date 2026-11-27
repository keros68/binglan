using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using BingLan.App.Themes;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Themes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;

namespace BingLan.App.Windows;

/// <summary>
/// First-run guide: layout or theme, greeting and weather city, starter apps for the
/// dock, desktop mode and startup. Each step applies right away; skipping or closing
/// keeps whatever was already applied and does not show the guide again.
/// </summary>
public partial class OnboardingWindow : Window
{
    private static readonly string[] StepTitles = ["选择桌面", "称呼与天气", "常用入口", "桌面模式"];
    private readonly OnboardingActions _actions;
    private readonly FrameworkElement[] _steps;
    private readonly List<EntryRow> _entryRows = [];
    private CancellationTokenSource? _searchCancellation;
    private int _step;
    private bool _themeImported;
    private bool _completed;
    private string? _downloadsFolder;

    public OnboardingWindow(OnboardingActions actions)
    {
        _actions = actions;
        InitializeComponent();
        _steps = [DesktopStep, PersonalStep, EntriesStep, ModeStep];
        MonitorSelector.ItemsSource = actions.Monitors;
        MonitorSelector.SelectedItem = actions.Monitors.FirstOrDefault(monitor => monitor.IsPrimary)
            ?? actions.Monitors.FirstOrDefault();
        MonitorSelector.IsEnabled = actions.Monitors.Count > 1;
        DisplaySummaryText.Text = actions.Monitors.Count == 0
            ? "没有读取到显示器信息"
            : "卡片放在主显示器；所选显示器用于冰蓝 Dock。缩放沿用 Windows 显示设置，列表中已标出。";
        SetDownloadsFolder(actions.DownloadsFolder);
        var defaults = DesktopExperienceRules.CreateDefault();
        QuietPresetThumb.Source = ThemePreviewRenderer.RenderPreset(DesktopLayoutPreset.QuietInformation, defaults);
        GlacierPresetThumb.Source = ThemePreviewRenderer.RenderPreset(DesktopLayoutPreset.GlacierWorkbench, defaults);
        ApplePresetThumb.Source = ThemePreviewRenderer.RenderPreset(DesktopLayoutPreset.TransparentApple, defaults);
        GreetingNameEditor.Text = actions.GreetingName;
        StartupCheckBox.IsEnabled = actions.CanChangeStartup;
        StartupCheckBox.IsChecked = actions.CanChangeStartup;
        ShowStep(0);
        Loaded += async (_, _) => await LoadEntriesAsync();
        Closing += OnClosing;
    }

    private void ShowStep(int step)
    {
        _step = step;
        for (var index = 0; index < _steps.Length; index++)
        {
            _steps[index].Visibility = index == step ? Visibility.Visible : Visibility.Collapsed;
        }
        StepText.Text = $"第 {step + 1} 步，共 {_steps.Length} 步";
        if (step == _steps.Length - 1)
        {
            // Apps picked in the previous step only show once a mode with the dock is chosen.
            var pinned = _entryRows.Count(row => row.CheckBox.IsChecked == true && row.App is not null);
            DockNoteText.Text = $"已选的 {pinned} 个应用会固定到冰蓝 Dock；选择“冰蓝混合”或“苹果式”后 Dock 才会显示。";
            DockNoteText.Visibility = pinned > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        StepTitleText.Text = StepTitles[step];
        BackButton.IsEnabled = step > 0;
        NextButton.Content = step == _steps.Length - 1 ? "进入桌面" : "下一步";
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 0 when !_themeImported:
                _actions.ApplyPreset(GlacierPresetRadio.IsChecked == true
                    ? DesktopLayoutPreset.GlacierWorkbench
                    : ApplePresetRadio.IsChecked == true
                        ? DesktopLayoutPreset.TransparentApple
                        : DesktopLayoutPreset.QuietInformation);
                break;
            case 1:
                _actions.ApplyGreetingName(GreetingNameEditor.Text.Trim());
                if (CityResultsList.SelectedItem is CitySearchResult city)
                {
                    _actions.ApplyCity(city);
                }
                break;
            case 2:
                _actions.ApplyEntries(
                    _entryRows
                        .Where(row => row.CheckBox.IsChecked == true && row.App is not null)
                        .Select(row => row.App!)
                        .ToList(),
                    DownloadsCheckBox.IsChecked == true ? _downloadsFolder : null);
                break;
            case 3:
                _actions.ApplyDesktopMode(HybridModeRadio.IsChecked == true
                    ? DesktopMode.IceBlueHybrid
                    : AppleModeRadio.IsChecked == true
                        ? DesktopMode.AppleStyle
                        : DesktopMode.WindowsNative);
                if (_actions.CanChangeStartup)
                {
                    _actions.SetStartupEnabled(StartupCheckBox.IsChecked == true);
                }
                Finish();
                return;
        }

        ShowStep(_step + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowStep(Math.Max(0, _step - 1));

    private void Skip_Click(object sender, RoutedEventArgs e) => Finish();

    private void Finish()
    {
        MarkCompleted();
        Close();
        if (OpenSettingsCheckBox.IsChecked == true)
        {
            _actions.OpenSettings();
        }
    }

    private void MarkCompleted()
    {
        if (_completed)
        {
            return;
        }
        _completed = true;
        _actions.Complete();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _searchCancellation?.Cancel();
        MarkCompleted();
    }

    private void MonitorSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MonitorSelector.SelectedItem is OnboardingMonitor monitor)
        {
            _actions.ApplyMonitor(monitor.MonitorKey);
        }
    }

    private void SetDownloadsFolder(string? folder)
    {
        _downloadsFolder = folder;
        DownloadsFolderText.Text = folder ?? "没有找到下载文件夹，请选择一个文件夹";
        DownloadsCheckBox.IsEnabled = folder is not null;
        DownloadsCheckBox.IsChecked = folder is not null;
    }

    private void ChooseDownloads_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择作为“下载”入口的文件夹",
            InitialDirectory = _downloadsFolder ?? string.Empty
        };
        if (dialog.ShowDialog(this) == true)
        {
            SetDownloadsFolder(dialog.FolderName);
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
            var preview = _actions.ReadTheme(dialog.FileName);
            if (preview.Package is null)
            {
                ThemeStatusText.Text = preview.Error;
                return;
            }

            ThemeStatusText.Text = "正在匹配本机应用…";
            var outcome = await _actions.ImportTheme(dialog.FileName);
            ThemeStatusText.Text = outcome.Message;
            _themeImported = outcome.Succeeded;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException)
        {
            ThemeStatusText.Text = $"导入未完成：{exception.Message}";
        }
    }

    private async void SearchCity_Click(object sender, RoutedEventArgs e) => await SearchCityAsync();

    private void CityQueryEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = SearchCityAsync();
            e.Handled = true;
        }
    }

    private async Task SearchCityAsync()
    {
        _searchCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        SearchCityButton.IsEnabled = false;
        CityResultsList.ItemsSource = null;
        CitySearchStatusText.Text = "正在搜索…";
        try
        {
            var outcome = await _actions.CitySearch.SearchAsync(CityQueryEditor.Text, cancellation.Token);
            if (!ReferenceEquals(_searchCancellation, cancellation))
            {
                return;
            }
            CityResultsList.ItemsSource = outcome.Results;
            CitySearchStatusText.Text = outcome.Results.Count > 0
                ? "选中城市后点击“下一步”保存"
                : outcome.Message;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer search or closing the guide cancels quietly.
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

    private async Task LoadEntriesAsync()
    {
        EntriesStatusText.Text = "正在查找本机应用…";
        IReadOnlyList<OnboardingEntry> entries;
        try
        {
            entries = await _actions.DetectEntries();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"查找本机应用失败：{exception}");
            EntriesStatusText.Text = "没有能查找本机应用，之后可在 Dock 页手动添加。";
            return;
        }
        EntryRows.Children.Clear();
        _entryRows.Clear();
        foreach (var entry in entries)
        {
            var row = new EntryRow(entry.Slot, entry.App);
            _entryRows.Add(row);
            EntryRows.Children.Add(BuildRow(row));
        }
        EntriesStatusText.Text = entries.Any(entry => entry.App is not null)
            ? string.Empty
            : "没有找到常用应用，可以点击“选择…”手动指定。";
    }

    private FrameworkElement BuildRow(EntryRow row)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = true };
        var choose = new Button { Content = "选择…", Margin = new Thickness(12, 0, 0, 0) };
        AutomationProperties.SetName(choose, $"为{AppSlotCatalog.GetName(row.Slot)}选择应用");
        DockPanel.SetDock(choose, System.Windows.Controls.Dock.Right);
        choose.Click += (_, _) => ChooseApp(row);
        panel.Children.Add(choose);
        row.CheckBox.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(row.CheckBox);
        row.Refresh();
        return panel;
    }

    private void ChooseApp(EntryRow row)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"选择{AppSlotCatalog.GetName(row.Slot)}应用",
            Filter = "应用程序 (*.exe;*.lnk)|*.exe;*.lnk",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            DereferenceLinks = true
        };
        if (dialog.ShowDialog(this) != true
            || !string.Equals(Path.GetExtension(dialog.FileName), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        row.App = DockPinRules.CreateFromExecutable(dialog.FileName);
        if (row.App is not null)
        {
            row.App.DisplayName = BingLan.App.Dock.DockAppResolver.GetFileDescription(dialog.FileName) ?? row.App.DisplayName;
        }
        row.CheckBox.IsChecked = row.App is not null;
        row.Refresh();
    }

    private sealed class EntryRow(string slot, DockPinnedApp? app)
    {
        public string Slot { get; } = slot;
        public DockPinnedApp? App { get; set; } = app;
        public CheckBox CheckBox { get; } = new() { IsChecked = app is not null };

        public void Refresh()
        {
            CheckBox.IsEnabled = App is not null;
            CheckBox.Content = App is null
                ? $"{AppSlotCatalog.GetName(Slot)}：未找到"
                : $"{AppSlotCatalog.GetName(Slot)}：{App.DisplayName}";
            AutomationProperties.SetName(CheckBox, $"固定{CheckBox.Content}");
        }
    }
}
