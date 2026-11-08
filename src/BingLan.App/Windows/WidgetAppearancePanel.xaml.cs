using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace BingLan.App.Windows;

public partial class WidgetAppearancePanel : WpfUserControl
{
    private WidgetWindowBase? _window;
    private bool _isRefreshing;
    // A typed size applies after a short pause, or at once on Enter or leaving the box.
    private readonly System.Windows.Threading.DispatcherTimer _sizeTimer =
        new() { Interval = TimeSpan.FromMilliseconds(400) };

    public WidgetAppearancePanel()
    {
        InitializeComponent();
        TitleFontFamilyPicker.ItemsSource = InstalledFontCatalog.Names;
        // One click sets the body, title bar and text colours together; the boxes below
        // stay for fine adjustment.
        PaletteSwatches.Fill(PaletteSwatchPanel, palette =>
        {
            if (_window is null)
            {
                return;
            }
            var colors = BingLan.Core.Models.DesktopStyleRules.FromPalette(
                palette, _window.WidgetAppearance.BackgroundOpacity);
            _window.SetWidgetBackgroundColor(colors.BackgroundColor);
            _window.SetWidgetHeaderColor(colors.HeaderColor);
            _window.SetWidgetHeaderOpacity(colors.HeaderOpacity);
            _window.SetWidgetTextColor(colors.TextColor);
            RefreshValues();
        });
        _sizeTimer.Tick += (_, _) => ApplySize(showResult: false);
    }

    /// <summary>The shared card material, for the light-glass palette and density.</summary>
    internal BingLan.Core.Models.DesktopStyleState SharedStyle { get; set; } = new();

    // The segmented radios show the current material; custom colours read as the
    // nearer of light and dark glass when the panel refreshes.
    private void CardLook_Changed(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing
            || _window is null
            || sender is not FrameworkElement { Tag: string tag }
            || !Enum.TryParse<BingLan.Core.Models.GlassMode>(tag, out var mode))
        {
            return;
        }

        var look = SettingsWindow.CardLook(mode, SharedStyle, _window.WidgetAppearance.BackgroundOpacity);
        _window.SetWidgetBackgroundColor(look.BackgroundColor);
        _window.SetWidgetBackgroundOpacity(look.BackgroundOpacity);
        _window.SetWidgetHeaderColor(look.HeaderColor);
        _window.SetWidgetHeaderOpacity(look.HeaderOpacity);
        _window.SetWidgetTextColor(look.TextColor);
        if (_window is NoteWidgetWindow note)
        {
            note.SetBodyColor(look.TextColor);
        }
        RefreshValues();
    }

    public void Attach(WidgetWindowBase window)
    {
        _window = window;
        RefreshValues();
    }

    public void RefreshValues()
    {
        if (_window is null)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            BackgroundColorEditor.Text = _window.WidgetAppearance.BackgroundColor;
            HeaderColorEditor.Text = _window.WidgetAppearance.HeaderColor;
            TextColorEditor.Text = _window.WidgetAppearance.TextColor;
            BackgroundOpacitySlider.Value = _window.WidgetAppearance.BackgroundOpacity;
            HeaderOpacitySlider.Value = _window.WidgetAppearance.HeaderOpacity;
            CornerRadiusSlider.Value = _window.WidgetCornerRadius;
            var titleFontFamily = InstalledFontCatalog.Resolve(
                _window.WidgetAppearance.TitleFontFamily);
            TitleFontFamilyPicker.SelectedItem = titleFontFamily;
            TitleFontBoldToggle.IsChecked = _window.WidgetAppearance.TitleFontBold;
            TitleFontItalicToggle.IsChecked = _window.WidgetAppearance.TitleFontItalic;
            var inferredLook = SettingsWindow.InferLook(_window.WidgetAppearance);
            CardLookLightRadio.IsChecked = inferredLook == BingLan.Core.Models.GlassMode.Light;
            CardLookDarkRadio.IsChecked = inferredLook == BingLan.Core.Models.GlassMode.Dark;
            CardLookClearRadio.IsChecked = inferredLook == BingLan.Core.Models.GlassMode.Clear;
            WidthEditor.Text = $"{_window.ActualWidth:0.#}";
            HeightEditor.Text = $"{_window.ActualHeight:0.#}";
            RefreshLabels();
            RefreshColorChips();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void BackgroundOpacitySlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isRefreshing)
        {
            _window?.SetWidgetBackgroundOpacity(e.NewValue);
        }
        RefreshLabels();
    }

    private void HeaderOpacitySlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isRefreshing)
        {
            _window?.SetWidgetHeaderOpacity(e.NewValue);
        }
        RefreshLabels();
    }

    private void CornerRadiusSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isRefreshing && _window is not null)
        {
            _window.WidgetCornerRadius = e.NewValue;
        }
        RefreshLabels();
    }

    private void TitleFontFamilyPicker_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_isRefreshing &&
            TitleFontFamilyPicker.SelectedItem is string fontFamily)
        {
            _window?.SetWidgetTitleFontFamily(fontFamily);
        }
    }

    private void TitleFontStyleToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing || _window is null)
        {
            return;
        }

        _window.SetWidgetTitleFontBold(TitleFontBoldToggle.IsChecked == true);
        _window.SetWidgetTitleFontItalic(TitleFontItalicToggle.IsChecked == true);
    }

    private void BackgroundColorEditor_LostFocus(object sender, RoutedEventArgs e) =>
        CommitBackgroundColor();

    private void HeaderColorEditor_LostFocus(object sender, RoutedEventArgs e) =>
        CommitHeaderColor();

    private void ColorEditor_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (ReferenceEquals(sender, BackgroundColorEditor))
        {
            CommitBackgroundColor();
        }
        else
        {
            if (ReferenceEquals(sender, HeaderColorEditor))
            {
                CommitHeaderColor();
            }
            else
            {
                CommitTextColor();
            }
        }
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void BackgroundColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_window is null || !TryChooseColor(_window.WidgetAppearance.BackgroundColor, out var color))
        {
            return;
        }
        _window.SetWidgetBackgroundColor(color);
        RefreshValues();
    }

    private void HeaderColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_window is null || !TryChooseColor(_window.WidgetAppearance.HeaderColor, out var color))
        {
            return;
        }
        _window.SetWidgetHeaderColor(color);
        RefreshValues();
    }

    private void TextColorEditor_LostFocus(object sender, RoutedEventArgs e) =>
        CommitTextColor();

    private void TextColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_window is null || !TryChooseColor(_window.WidgetAppearance.TextColor, out var color))
        {
            return;
        }
        _window.SetWidgetTextColor(color);
        RefreshValues();
    }

    private void QuickTextColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_window is null || sender is not FrameworkElement { Tag: string color })
        {
            return;
        }
        _window.SetWidgetTextColor(color);
        RefreshValues();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _window?.ResetWidgetAppearance();
        RefreshValues();
    }

    private void SizeEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isRefreshing || _window is null)
        {
            return;
        }
        _sizeTimer.Stop();
        _sizeTimer.Start();
    }

    private void SizeEditor_LostFocus(object sender, RoutedEventArgs e) => ApplySize(showResult: true);

    private void SizeEditor_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplySize(showResult: true);
            e.Handled = true;
        }
    }

    // While typing, the box keeps what was typed; when editing ends it shows the size the
    // card actually took, for example after a value outside the allowed range.
    private void ApplySize(bool showResult)
    {
        _sizeTimer.Stop();
        if (_window is null)
        {
            return;
        }

        var maximumWidth = double.IsPositiveInfinity(_window.MaxWidth)
            ? 1600d
            : _window.MaxWidth;
        var maximumHeight = double.IsPositiveInfinity(_window.MaxHeight)
            ? 1200d
            : _window.MaxHeight;
        _window.Width = ParseSize(
            WidthEditor.Text,
            _window.ActualWidth,
            _window.MinWidth,
            maximumWidth);
        _window.Height = ParseSize(
            HeightEditor.Text,
            _window.ActualHeight,
            _window.MinHeight,
            maximumHeight);
        if (showResult)
        {
            Dispatcher.BeginInvoke(RefreshValues, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void CommitBackgroundColor()
    {
        if (_window is null)
        {
            return;
        }
        _window.SetWidgetBackgroundColor(BackgroundColorEditor.Text);
        RefreshValues();
    }

    private void CommitHeaderColor()
    {
        if (_window is null)
        {
            return;
        }
        _window.SetWidgetHeaderColor(HeaderColorEditor.Text);
        RefreshValues();
    }

    private void CommitTextColor()
    {
        if (_window is null)
        {
            return;
        }
        _window.SetWidgetTextColor(TextColorEditor.Text);
        RefreshValues();
    }

    private void RefreshLabels()
    {
        if (BackgroundOpacityText is null ||
            HeaderOpacityText is null ||
            CornerRadiusText is null)
        {
            return;
        }
        BackgroundOpacityText.Text = $"{BackgroundOpacitySlider.Value:P0}";
        HeaderOpacityText.Text = $"{HeaderOpacitySlider.Value:P0}";
        CornerRadiusText.Text = $"{CornerRadiusSlider.Value:0.#}";
    }

    private void RefreshColorChips()
    {
        SetColorChip(BackgroundColorChip, BackgroundColorEditor.Text);
        SetColorChip(HeaderColorChip, HeaderColorEditor.Text);
        SetColorChip(TextColorChip, TextColorEditor.Text);
    }

    private static void SetColorChip(Border chip, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            chip.Background = null;
            return;
        }

        try
        {
            var color = ParseMediaColor(value);
            var brush = new SolidColorBrush(MediaColor.FromRgb(color.R, color.G, color.B));
            brush.Freeze();
            chip.Background = brush;
        }
        catch (FormatException)
        {
            // Keep the last valid swatch while a colour is still being typed.
        }
    }

    private static bool TryChooseColor(string current, out string color)
    {
        var mediaColor = ParseMediaColor(current);
        using var dialog = new Forms.ColorDialog
        {
            Color = DrawingColor.FromArgb(mediaColor.R, mediaColor.G, mediaColor.B),
            FullOpen = true,
            AnyColor = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            color = current;
            return false;
        }

        color = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        return true;
    }

    private static MediaColor ParseMediaColor(string value) =>
        (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(value)!;

    private static double ParseSize(
        string value,
        double fallback,
        double minimum,
        double maximum) =>
        double.TryParse(value, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;

}
