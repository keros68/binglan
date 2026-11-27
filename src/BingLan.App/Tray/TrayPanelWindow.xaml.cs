using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace BingLan.App.Tray;

internal sealed partial class TrayPanelWindow : Window
{
    private const int FirstRowIcons = 5;
    private static readonly TimeSpan LoadingPromptDelay = TimeSpan.FromMilliseconds(300);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Button> _entryButtons = [];
    private TrayPanelEntry[]? _shown;
    private bool _expanded;
    private bool _refreshing;
    private bool _invoking;
    private bool _closed;
    private string? _readMessage;
    private string? _actionMessage;

    private readonly bool _anchored;

    private readonly Action? _openNativeTray;

    internal TrayPanelWindow(Action? openNativeTray = null)
    {
        InitializeComponent();
        _openNativeTray = openNativeTray;
        _anchored = openNativeTray is not null;
        NativeTrayButton.Visibility = _anchored ? Visibility.Visible : Visibility.Collapsed;
        if (!_anchored)
        {
            var area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left, area.Right - Width - 8);
            Top = area.Top + 16;
        }
        if (SystemParameters.HighContrast)
        {
            Resources["Surface"] = SystemColors.WindowBrush;
            Resources["Ink"] = Resources["SoftInk"] = Resources["Edge"] = SystemColors.WindowTextBrush;
            Resources["Hover"] = SystemColors.ControlBrush;
            Resources["FocusRing"] = SystemColors.HighlightBrush;
        }
        Loaded += async (_, _) => await RefreshAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        ShowCachedEntries();
        UpdateStatus();
        base.OnSourceInitialized(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnOpenNativeTray(object sender, RoutedEventArgs e) => _openNativeTray?.Invoke();

    internal void ApplyCaptionShadow(Color color)
    {
        var shadow = new DropShadowEffect { Color = color, BlurRadius = 3, ShadowDepth = 1, Opacity = 0.42 };
        shadow.Freeze();
        PanelTitle.Effect = Status.Effect = shadow;
    }
    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        _actionMessage = null;
        UpdateStatus();
        await RefreshAsync();
    }
    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (!_anchored && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void UpdateStatus()
    {
        var text = _actionMessage ?? _readMessage ?? (_shown is { Length: 0 } ? "暂无可用托盘图标" : null);
        Status.Text = text ?? "";
        Status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _closed) return;
        _refreshing = true;
        try
        {
            var refresh = TrayReader.RefreshSnapshotAsync().WaitAsync(TrayReader.OperationTimeout, _lifetime.Token);
            // The background read can complete between SourceInitialized and Loaded.
            ShowCachedEntries();
            _readMessage = null;
            UpdateStatus();
            if (_shown is null && !refresh.IsCompleted
                && await Task.WhenAny(refresh, Task.Delay(LoadingPromptDelay, _lifetime.Token)) != refresh && !_closed)
            {
                ShowCachedEntries();
                _readMessage = _shown is null ? "正在读取…" : null;
                UpdateStatus();
            }

            var snapshot = await refresh;
            if (_closed) return;
            if (snapshot is null) _readMessage = "无法读取托盘，可再次刷新。";
            else
            {
                ShowEntries(snapshot);
                _readMessage = null;
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (TimeoutException) { _readMessage = "读取超时，可再次刷新。"; }
        catch (Exception) { _readMessage = "无法读取托盘，可再次刷新。"; }
        finally
        {
            _refreshing = false;
            if (!_closed) UpdateStatus();
        }
    }

    private void ShowCachedEntries()
    {
        if (_shown is null && TrayReader.CachedSnapshot is { } snapshot)
        {
            ShowEntries(snapshot);
            _readMessage = null;
        }
    }

    private void ShowEntries(TrayPanelEntry[] entries)
    {
        if (ReferenceEquals(_shown, entries)) return;
        if (_shown is { } shown && shown.Select(entry => entry.Identity).SequenceEqual(entries.Select(entry => entry.Identity)))
        {
            for (var i = 0; i < entries.Length; i++)
                if (shown[i] != entries[i]) UpdateEntry(_entryButtons[i], entries[i]);
        }
        else
        {
            var focusedIndex = _entryButtons.FindIndex(button => button.IsKeyboardFocused);
            var focusedIdentity = focusedIndex >= 0 ? _shown?[focusedIndex].Identity : null;
            Entries.Children.Clear();
            _entryButtons.Clear();
            foreach (var entry in entries) _entryButtons.Add(CreateEntry(entry));
            foreach (var button in _entryButtons.Take(FirstRowIcons)) Entries.Children.Add(button);

            if (_entryButtons.Count > FirstRowIcons)
            {
                var more = new Button { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13 };
                Entries.Children.Add(more);
                var overflow = _entryButtons.Skip(FirstRowIcons).ToArray();
                foreach (var item in overflow) Entries.Children.Add(item);
                void SetExpanded()
                {
                    more.Content = _expanded ? "\uE70E" : "\uE70D";
                    more.ToolTip = _expanded ? "收起" : $"展开其余 {overflow.Length} 项";
                    AutomationProperties.SetName(more, (string)more.ToolTip);
                    foreach (var item in overflow) item.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
                }
                more.Click += (_, _) => { _expanded = !_expanded; SetExpanded(); };
                SetExpanded();
            }
            if (focusedIdentity is { } identity)
            {
                var index = Array.FindIndex(entries, entry => entry.Identity == identity);
                if (index >= 0) _entryButtons[index].Focus();
            }
        }
        _shown = entries;
    }

    private Button CreateEntry(TrayPanelEntry entry)
    {
        var button = new Button();
        UpdateEntry(button, entry);
        var identity = entry.Identity;
        button.Click += async (_, _) => await InvokeAsync(identity);
        button.PreviewMouseRightButtonUp += async (_, e) =>
        {
            e.Handled = true;
            await InvokeAsync(identity, contextMenu: true);
        };
        button.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.Apps && !(e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)) return;
            e.Handled = true;
            await InvokeAsync(identity, contextMenu: true);
        };
        return button;
    }

    private void UpdateEntry(Button button, TrayPanelEntry entry)
    {
        var name = entry.Name.Trim();
        var location = entry.Source == "existing-overflow" ? "折叠区" : "常显区";
        var label = name.Length == 0 ? "未命名的托盘图标" : name;
        button.ToolTip = $"{label}\n{location}";
        AutomationProperties.SetName(button, label);
        var image = entry.Thumbnail;
        if (image is null && name == "冰蓝桌面") image = (BitmapSource)FindResource("BingLanMark");
        if (image is not null)
        {
            var backing = ContrastBacking(image);
            var size = backing is null ? 21 : 17;
            var icon = new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform };
            button.Content = backing is not null
                ? new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = backing, Child = icon }
                : icon;
            RenderOptions.SetBitmapScalingMode(button, BitmapScalingMode.HighQuality);
        }
        else if (name is "" or "蓝牙设备" or "Bluetooth Devices")
        {
            button.Content = new TextBlock { Text = name.Length == 0 ? "\uE71D" : "\uE702", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 19 };
        }
        else
        {
            var initial = new TextBlock { Text = StringInfo.GetNextTextElement(name),
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var tile = new Border { Width = 21, Height = 21, CornerRadius = new CornerRadius(6), Child = initial };
            tile.SetResourceReference(BackgroundProperty, "Hover");
            button.Content = tile;
        }
    }

    private Brush? ContrastBacking(BitmapSource image)
    {
        if (TryFindResource("Ink") is not SolidColorBrush ink) return null;
        bool darkSurface = ink.Color.R * 299 + ink.Color.G * 587 + ink.Color.B * 114 > 128000;
        var size = Math.Min(1, 16d / Math.Max(image.PixelWidth, image.PixelHeight));
        var sample = new TransformedBitmap(image, new ScaleTransform(size, size));
        var pixels = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);
        var data = new byte[pixels.PixelWidth * pixels.PixelHeight * 4];
        pixels.CopyPixels(data, pixels.PixelWidth * 4, 0);
        int visible = 0, lowContrast = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            if (data[i + 3] < 64) continue;
            visible++;
            var value = data[i] * 114 + data[i + 1] * 587 + data[i + 2] * 299;
            if (darkSurface ? value < 96000 : value > 210000) lowContrast++;
        }
        if (visible == 0 || lowContrast < visible * 0.90) return null;
        return darkSurface ? Brushes.WhiteSmoke : Brushes.DarkSlateGray;
    }

    private async Task InvokeAsync(TrayIdentity identity, bool contextMenu = false)
    {
        if (_invoking || _closed) return;
        _invoking = true;
        try
        {
            var result = await TrayReader.InvokeAsync(identity, contextMenu, _lifetime.Token);
            _actionMessage = result switch
            {
                "invoked" or "context-requested" => null,
                "action-in-progress" => "已有托盘操作正在处理。",
                _ => "未能操作该图标，请刷新后重试。"
            };
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (OperationCanceledException) { _actionMessage = "操作超时，可重试。"; }
        catch (Exception) { _actionMessage = "未能操作该图标，请刷新后重试。"; }
        finally
        {
            _invoking = false;
            if (!_closed) UpdateStatus();
        }
    }
}
