using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Image = System.Windows.Controls.Image;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using Binding = System.Windows.Data.Binding;

namespace BingLan.App.Windows;

/// <summary>
/// Lists the programs in the Start menu with their icons and a search box, so apps can be
/// pinned to the dock by ticking them instead of browsing for program files. It is an
/// ordinary window, not a modal one, so the rest of the app stays usable while it is open.
/// </summary>
internal sealed class AppPickerDialog : Window
{
    private static AppPickerDialog? _open;

    /// <summary>Shows the picker, or brings forward the one already open.</summary>
    internal static void Open(Window? owner, IEnumerable<DockPinnedApp> pinned, Action<IReadOnlyList<DockPinnedApp>> chosen)
    {
        if (_open is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }
            existing.Activate();
            return;
        }

        var picker = new AppPickerDialog(pinned, chosen);
        if (owner is { IsVisible: true })
        {
            picker.Owner = owner;
        }
        else
        {
            picker.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        picker.Closed += (_, _) => _open = null;
        _open = picker;
        picker.Show();
        picker.Activate();
    }

    private readonly Action<IReadOnlyList<DockPinnedApp>> _chosen;
    private readonly ObservableCollection<AppChoice> _apps = [];
    private readonly ICollectionView _view;
    private readonly TextBox _search = new() { Height = 34 };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly HashSet<string> _alreadyPinned;

    private AppPickerDialog(IEnumerable<DockPinnedApp> pinned, Action<IReadOnlyList<DockPinnedApp>> chosen)
    {
        _chosen = chosen;
        Title = "添加到 Dock";
        // The app-wide button style is meant for cards on the wallpaper (white text on a
        // clear background); this window uses the settings window's buttons instead.
        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter(MinHeightProperty, 34d));
        buttonStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(14, 6, 14, 6)));
        buttonStyle.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("IceInkBrush")));
        buttonStyle.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("SettingsButtonBrush")));
        buttonStyle.Setters.Add(new Setter(BorderBrushProperty, new DynamicResourceExtension("SettingsButtonBorderBrush")));
        Resources[typeof(Button)] = buttonStyle;
        var boxStyle = new Style(typeof(TextBox));
        boxStyle.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("IceInkBrush")));
        boxStyle.Setters.Add(new Setter(TextBox.CaretBrushProperty, new DynamicResourceExtension("IceInkBrush")));
        boxStyle.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("SettingsInputBrush")));
        boxStyle.Setters.Add(new Setter(BorderBrushProperty, new DynamicResourceExtension("SettingsInputBorderBrush")));
        boxStyle.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(1)));
        boxStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(10, 0, 10, 0)));
        boxStyle.Setters.Add(new Setter(VerticalContentAlignmentProperty, VerticalAlignment.Center));
        Resources[typeof(TextBox)] = boxStyle;
        Width = 460;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "IcePageBrush");
        SetResourceReference(ForegroundProperty, "IceInkBrush");
        FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont");
        _alreadyPinned = pinned
            .Select(DockPinRules.IdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _view = CollectionViewSource.GetDefaultView(_apps);
        _view.Filter = item => item is AppChoice app
            && (_search.Text.Trim().Length == 0
                || app.Name.Contains(_search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
        _search.TextChanged += (_, _) => _view.Refresh();
        AutomationProperties.SetName(_search, "搜索应用或系统位置");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "IceMutedInkBrush");

        var list = new ListBox
        {
            ItemsSource = _view,
            Margin = new Thickness(0, 10, 0, 0),
            ItemTemplate = CreateRowTemplate()
        };
        list.SetResourceReference(BackgroundProperty, "SettingsInputBrush");
        AutomationProperties.SetName(list, "应用和系统位置");
        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedItem is AppChoice app)
            {
                app.IsChecked = !app.IsChecked;
            }
        };

        var browse = new Button { Content = "浏览程序文件…" };
        browse.Click += (_, _) => Browse();
        var confirm = new Button { Content = "固定所选", Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        confirm.SetResourceReference(ForegroundProperty, "IceActionTextBrush");
        confirm.SetResourceReference(BackgroundProperty, "IceActionBrush");
        confirm.SetResourceReference(BorderBrushProperty, "IceActionBrush");
        confirm.Click += (_, _) => Finish(_apps.Where(app => app.IsChecked).Select(app => app.App).ToList());
        var cancel = new Button { Content = "取消", Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        cancel.Click += (_, _) => Close();

        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        DockPanel.SetDock(browse, System.Windows.Controls.Dock.Left);
        DockPanel.SetDock(cancel, System.Windows.Controls.Dock.Right);
        DockPanel.SetDock(confirm, System.Windows.Controls.Dock.Right);
        buttons.Children.Add(browse);
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        var layout = new DockPanel { Margin = new Thickness(20) };
        var heading = new TextBlock { Text = "选择要固定的应用或系统位置", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(heading, System.Windows.Controls.Dock.Top);
        DockPanel.SetDock(_search, System.Windows.Controls.Dock.Top);
        DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
        DockPanel.SetDock(_status, System.Windows.Controls.Dock.Bottom);
        layout.Children.Add(heading);
        layout.Children.Add(_search);
        layout.Children.Add(buttons);
        layout.Children.Add(_status);
        layout.Children.Add(list);
        Content = layout;

        Loaded += async (_, _) =>
        {
            _search.Focus();
            _status.Text = "正在读取开始菜单…";
            await LoadAsync();
        };
    }

    private void Finish(IReadOnlyList<DockPinnedApp> apps)
    {
        if (apps.Count == 0)
        {
            _status.Text = "请先勾选要固定的项目";
            return;
        }
        _chosen(apps);
        Close();
    }

    private async Task LoadAsync()
    {
        var shortcutIcons = new Dictionary<DockPinnedApp, (string Path, int Index)>();
        var apps = await Task.Run(() => ReadSystemPlaces().Concat(
            DockShortcuts.ReadStartMenuApps(preserveLaunchVariants: false, shortcutIcons: shortcutIcons)).ToArray());
        foreach (var app in apps.Where(app => !_alreadyPinned.Contains(DockPinRules.IdentityKey(app))))
        {
            _apps.Add(new AppChoice(app));
        }
        _status.Text = _apps.Count == 0
            ? "可点“浏览程序文件…”添加应用"
            : $"共 {_apps.Count} 个可固定项目";

        // Icons come in afterwards so the list shows at once.
        foreach (var choice in _apps.ToList())
        {
            (string Path, int Index)? shortcutIcon = shortcutIcons.TryGetValue(choice.App, out var location)
                ? location : null;
            choice.Icon = await Task.Run(() => LoadChoiceIcon(choice.App, shortcutIcon));
        }
    }

    internal static ImageSource? LoadChoiceIcon(DockPinnedApp app, (string Path, int Index)? shortcutIcon)
    {
        if (app.ExecutablePath is { } executable)
        {
            var declared = shortcutIcon is { } location
                ? ShellIconProvider.GetLocalIconResource(location.Path, location.Index)
                : null;
            return declared
                ?? ShellIconProvider.GetLocalIconResource(executable, 0)
                ?? ShellIconProvider.GetIcon(executable);
        }

        return app.TargetPath is { } path ? ShellIconProvider.GetIcon(path) : null;
    }

    private static IEnumerable<DockPinnedApp> ReadSystemPlaces()
    {
        foreach (var place in QuickPlaceRules.CreateDefaults())
        {
            if (DockPinRules.CreateFromSystemPlace(place.Target) is { } systemPin)
            {
                yield return systemPin;
            }
            else if (QuickPlacesWindow.Resolve(place.Target) is { Length: > 0 } folder
                && Directory.Exists(folder)
                && DockPinRules.CreateFromPath(folder, place.Name) is { } folderPin)
            {
                yield return folderPin;
            }
        }
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要固定到 Dock 的应用",
            Filter = "应用程序 (*.exe;*.lnk)|*.exe;*.lnk",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            DereferenceLinks = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var apps = dialog.FileNames.Select(DockShortcuts.CreatePin).OfType<DockPinnedApp>().ToList();
        if (apps.Count == 0)
        {
            _status.Text = "只能固定应用程序（.exe）或指向应用程序的快捷方式";
            return;
        }
        Finish(apps);
    }

    private static DataTemplate CreateRowTemplate()
    {
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        row.SetValue(MarginProperty, new Thickness(2, 3, 2, 3));

        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(AppChoice.IsChecked)) { Mode = BindingMode.TwoWay });
        check.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(AppChoice.Name)));
        row.AppendChild(check);

        var icon = new FrameworkElementFactory(typeof(Image));
        icon.SetValue(WidthProperty, 24d);
        icon.SetValue(HeightProperty, 24d);
        icon.SetValue(MarginProperty, new Thickness(8, 0, 10, 0));
        icon.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
        icon.SetBinding(Image.SourceProperty, new Binding(nameof(AppChoice.Icon)));
        row.AppendChild(icon);

        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(AppChoice.Name)));
        row.AppendChild(name);

        return new DataTemplate { VisualTree = row };
    }

    private sealed class AppChoice(DockPinnedApp app) : INotifyPropertyChanged
    {
        private bool _isChecked;
        private ImageSource? _icon;

        public DockPinnedApp App { get; } = app;
        public string Name => App.DisplayName;

        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; Changed(); }
        }

        public ImageSource? Icon
        {
            get => _icon;
            set { _icon = value; Changed(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Changed([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
