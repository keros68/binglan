using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BingLan.Core.Models;

namespace BingLan.App.Windows;

/// <summary>The five shared card settings offered by the appearance picker.</summary>
internal sealed record CardAppearanceSelection(
    GlassMode Glass, string Palette, double GlassDensity, TextInk TextInk, string CustomTextColor)
{
    internal static CardAppearanceSelection From(DesktopStyleState style) =>
        new(style.Glass, style.Palette, style.GlassDensity, style.TextInk, style.CustomTextColor);

    internal void ApplyTo(DesktopStyleState style)
    {
        style.Glass = Glass;
        style.Palette = Palette;
        style.GlassDensity = GlassDensity;
        style.TextInk = TextInk;
        style.CustomTextColor = CustomTextColor;
    }
}

public partial class AppearancePreviewWindow : Window
{
    private readonly CardAppearanceSelection _original;
    private readonly DesktopStyleState _originalStyle;
    private bool _loading = true;
    private bool _showOriginal;
    private bool _previewQueued;
    private bool _closed;

    internal DesktopStyleState Draft { get; }
    internal CardAppearanceSelection? AppliedAppearance { get; private set; }

    public AppearancePreviewWindow(DesktopStyleState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        _original = CardAppearanceSelection.From(current);
        _originalStyle = CopyStyle(current);
        Draft = CopyStyle(current);
        InitializeComponent();
        SourceInitialized += (_, _) => FitWorkArea();
        PaletteSwatches.Fill(PalettePanel, palette =>
        {
            Draft.Palette = palette.Key;
            MaterialSelector.SelectedIndex = (int)GlassMode.Light;
            UpdateControls();
            QueuePreview();
        });
        MaterialSelector.SelectedIndex = (int)Draft.Glass;
        TextInkSelector.SelectedIndex = (int)Draft.TextInk;
        DensitySlider.Value = Draft.GlassDensity;
        PreviewBackgroundSelector.SelectedIndex = 0;
        _loading = false;
        UpdateControls();
        RefreshPreview();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape
                || MaterialSelector.IsDropDownOpen
                || TextInkSelector.IsDropDownOpen
                || PreviewBackgroundSelector.IsDropDownOpen) return;
            e.Handled = true;
            Close();
        };
        Closed += (_, _) => _closed = true;
    }

    private void PickerBody_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = PickerBody.ActualWidth >= 712d;
        // Keep parameter scrolling usable when scaling leaves little vertical room.
        // The example yields before the controls; the footer has its own grid row.
        var previewHeight = Math.Min(290d, Math.Max(0d, PickerBody.ActualHeight - 120d));
        var showPreview = wide || previewHeight >= 96d;
        PickerBody.ColumnDefinitions[1].Width = new GridLength(wide ? 272d : 0d);
        PickerBody.RowDefinitions[0].Height = wide
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(showPreview ? previewHeight : 0d);
        PickerBody.RowDefinitions[1].Height = wide ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(ParameterScroll, wide ? 1 : 0);
        Grid.SetRow(ParameterScroll, wide ? 0 : 1);
        PreviewPanel.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        PreviewPanel.Margin = wide ? new Thickness(0, 0, 24, 0) : new Thickness(0, 0, 0, 10);
        SamplePreview.Height = wide ? double.NaN : Math.Clamp(previewHeight - 90d, 0d, 200d);
    }

    private void FitWorkArea()
    {
        var ownerHandle = Owner is null ? 0 : new System.Windows.Interop.WindowInteropHelper(Owner).Handle;
        var screen = System.Windows.Forms.Screen.FromHandle(ownerHandle);
        var monitor = BingLan.App.Dock.MonitorCatalog.GetAll().FirstOrDefault(item => item.DeviceName == screen.DeviceName);
        var scale = (monitor?.Dpi ?? 96) / 96d;
        var area = new Rect(screen.WorkingArea.Left / scale, screen.WorkingArea.Top / scale,
            screen.WorkingArea.Width / scale, screen.WorkingArea.Height / scale);
        MaxWidth = Math.Max(1d, area.Width - 24d);
        MaxHeight = Math.Max(1d, area.Height - 24d);
        MinWidth = Math.Min(MinWidth, MaxWidth);
        MinHeight = Math.Min(MinHeight, MaxHeight);
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Left + (area.Width - Width) / 2d;
        Top = area.Top + (area.Height - Height) / 2d;
    }

    private static DesktopStyleState CopyStyle(DesktopStyleState style) => new()
    {
        Glass = style.Glass, Palette = style.Palette, GlassDensity = style.GlassDensity,
        TextInk = style.TextInk, CustomTextColor = style.CustomTextColor,
        AccentColor = style.AccentColor, CardShadow = style.CardShadow,
        CardSpacing = style.CardSpacing, Motion = style.Motion
    };

    private void Draft_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Draft.Glass = (GlassMode)MaterialSelector.SelectedIndex;
        Draft.TextInk = (TextInk)TextInkSelector.SelectedIndex;
        UpdateControls();
        QueuePreview();
    }

    private void Density_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        Draft.GlassDensity = e.NewValue;
        UpdateControls();
        QueuePreview();
    }

    private void UpdateControls()
    {
        DensitySlider.IsEnabled = Draft.Glass != GlassMode.Clear;
        DensityText.Text = $"{Draft.GlassDensity * 100:0}%";
        PaletteSwatches.Select(PalettePanel, Draft.Glass == GlassMode.Light ? Draft.Palette : null);
        CustomColorButton.Content = $"选色… {Draft.CustomTextColor}";
    }

    private void CustomColor_Click(object sender, RoutedEventArgs e)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Draft.CustomTextColor)!;
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B), FullOpen = true, AnyColor = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        Draft.CustomTextColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        TextInkSelector.SelectedIndex = (int)TextInk.Custom;
        UpdateControls();
        QueuePreview();
    }

    private void PreviewBackground_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) QueuePreview();
    }

    // Merge slider events after pending input; no sampling timer.
    private void QueuePreview()
    {
        _showOriginal = false;
        if (_previewQueued || _closed) return;
        _previewQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _previewQueued = false;
            if (!_closed) RefreshPreview();
        }));
    }

    internal void RefreshPreview()
    {
        SamplePreview.Update(_showOriginal ? _originalStyle : Draft, PreviewBackgroundSelector.SelectedIndex == 1);
        PreviewStatusText.Text = _showOriginal ? "原外观的示例效果" : "试选外观的示例效果";
        CompareButton.Content = _showOriginal ? "返回试选效果" : "查看原效果";
        System.Windows.Automation.AutomationProperties.SetName(CompareButton, (string)CompareButton.Content);
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        _showOriginal = !_showOriginal;
        RefreshPreview();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var selection = CardAppearanceSelection.From(Draft);
        AppliedAppearance = selection == _original ? null : selection;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
