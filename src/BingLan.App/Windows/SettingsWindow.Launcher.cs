using System.Windows;
using System.Windows.Input;
using BingLan.App.Interop;
using BingLan.Core.Models;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace BingLan.App.Windows;

public partial class SettingsWindow
{
    private bool _loadingLauncherLook;

    private void InitializeLauncherLook()
    {
        PaletteSwatches.Fill(LauncherPaletteSwatches, palette => ApplyLauncherColor(palette.Body));
        LoadLauncherLook();
    }

    private void LoadLauncherLook()
    {
        var look = _maintenance.LauncherAppearance;
        _loadingLauncherLook = true;
        try
        {
            LauncherFollowLookRadio.IsChecked = look.FollowCardLook;
            LauncherCustomLookRadio.IsChecked = !look.FollowCardLook;
            LauncherCustomLookPanel.IsEnabled = !look.FollowCardLook;
            LauncherColorEditor.Text = look.SurfaceColor;
            var preview = new SolidColorBrush((Color)ColorConverter.ConvertFromString(look.SurfaceColor));
            preview.Freeze();
            LauncherColorPreview.Background = preview;
            LauncherOpacitySlider.Value = look.SurfaceOpacity;
            LauncherOpacityText.Text = $"{look.SurfaceOpacity:P0}";
            LauncherCornerRadiusSlider.Value = look.CornerRadius;
            LauncherCornerRadiusText.Text = $"{look.CornerRadius:0} DIP";
            PaletteSwatches.Select(LauncherPaletteSwatches, look.FollowCardLook ? null
                : DesktopStyleRules.Palettes.FirstOrDefault(palette => palette.Body == look.SurfaceColor)?.Key);
        }
        finally
        {
            _loadingLauncherLook = false;
        }
        RefreshAppearanceOverrides();
    }

    private void LauncherLook_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingLauncherLook || LauncherCustomLookPanel is null) return;

        _maintenance.LauncherAppearance.FollowCardLook = LauncherFollowLookRadio.IsChecked == true;
        _maintenance.ApplyLauncherAppearance();
        LoadLauncherLook();
    }

    private void LauncherOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingLauncherLook || LauncherOpacityText is null) return;

        _maintenance.LauncherAppearance.SurfaceOpacity = e.NewValue;
        LauncherOpacityText.Text = $"{_maintenance.LauncherAppearance.SurfaceOpacity:P0}";
        _maintenance.ApplyLauncherAppearance();
    }

    private void LauncherCornerRadius_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingLauncherLook || LauncherCornerRadiusText is null) return;

        _maintenance.LauncherAppearance.CornerRadius = e.NewValue;
        LauncherCornerRadiusText.Text = $"{_maintenance.LauncherAppearance.CornerRadius:0} DIP";
        _maintenance.ApplyLauncherAppearance();
    }

    private void LauncherColorEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        LauncherColorEditor_Commit(sender, e);
        e.Handled = true;
    }

    private void LauncherColorEditor_Commit(object sender, RoutedEventArgs e)
    {
        if (_loadingLauncherLook || _maintenance.LauncherAppearance.FollowCardLook) return;

        ApplyLauncherColor(WidgetAppearanceRules.CoerceColorOrDefault(LauncherColorEditor.Text.Trim(),
            _maintenance.LauncherAppearance.SurfaceColor));
    }

    private void LauncherChooseColor_Click(object sender, RoutedEventArgs e)
    {
        var current = (Color)ColorConverter.ConvertFromString(_maintenance.LauncherAppearance.SurfaceColor);
        if (NativeColorDialog.TryPickColor(this, current, out var chosen))
        {
            ApplyLauncherColor(chosen);
        }
    }

    private void ApplyLauncherColor(string color)
    {
        if (color != _maintenance.LauncherAppearance.SurfaceColor)
        {
            _maintenance.LauncherAppearance.SurfaceColor = color;
            _maintenance.ApplyLauncherAppearance();
        }
        LoadLauncherLook();
    }
}
