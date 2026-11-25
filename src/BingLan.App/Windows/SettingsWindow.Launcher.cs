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
    private bool _loadingLauncherEnabled = true;

    private void InitializeLauncherLook()
    {
        LoadLauncherEnabled();
        PaletteSwatches.Fill(LauncherPaletteSwatches, palette => ApplyLauncherColor(palette.Body));
        LoadLauncherLook();
    }

    private void LoadLauncherEnabled()
    {
        _loadingLauncherEnabled = true;
        try
        {
            var enabled = _maintenance.IsLauncherEnabled();
            LauncherEnabledCheckBox.IsChecked = enabled;
            DockLauncherButtonCheckBox.IsEnabled = enabled;
            if (LauncherRecentFilesCheckBox is not null)
            {
                LauncherRecentFilesCheckBox.IsChecked = _maintenance.IsLauncherShowRecentFiles();
                LauncherRecentFilesCheckBox.IsEnabled = enabled;
            }
            if (LauncherEverythingCheckBox is not null)
            {
                LauncherEverythingCheckBox.IsChecked = _maintenance.IsLauncherUseEverything();
                LauncherEverythingCheckBox.IsEnabled = enabled;
            }
            if (LauncherInlineSearchCheckBox is not null)
            {
                LauncherInlineSearchCheckBox.IsChecked = _maintenance.IsLauncherSearchInline();
                LauncherInlineSearchCheckBox.IsEnabled = enabled;
            }
            if (LauncherFavoriteModeComboBox is not null)
            {
                LauncherFavoriteModeComboBox.SelectedIndex = (int)_maintenance.GetLauncherFavoriteMode();
                LauncherFavoriteModeComboBox.IsEnabled = enabled;
            }
            if (LauncherBackdropModeComboBox is not null)
            {
                LauncherBackdropModeComboBox.SelectedIndex = (int)_maintenance.GetLauncherBackdropMode();
                LauncherBackdropModeComboBox.IsEnabled = enabled;
            }
            if (TopBarSearchHotkeyButton is not null)
            {
                TopBarSearchHotkeyButton.IsEnabled = enabled && _topBarState is not null;
            }
        }
        finally
        {
            _loadingLauncherEnabled = false;
        }
    }

    private void LauncherEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingLauncherEnabled) return;

        _maintenance.SetLauncherEnabled(LauncherEnabledCheckBox.IsChecked == true);
        LoadLauncherEnabled();
    }

    private void LauncherRecentFiles_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingLauncherEnabled) return;

        _maintenance.SetLauncherShowRecentFiles(LauncherRecentFilesCheckBox.IsChecked == true);
        LoadLauncherEnabled();
    }

    private void LauncherEverything_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingLauncherEnabled) return;

        _maintenance.SetLauncherUseEverything(LauncherEverythingCheckBox.IsChecked == true);
        LoadLauncherEnabled();
    }

    private void LauncherInlineSearch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingLauncherEnabled) return;

        _maintenance.SetLauncherSearchInline(LauncherInlineSearchCheckBox.IsChecked == true);
        LoadLauncherEnabled();
    }

    private void LauncherFavoriteMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingLauncherEnabled || LauncherFavoriteModeComboBox is null) return;
        var index = LauncherFavoriteModeComboBox.SelectedIndex;
        var mode = index switch
        {
            1 => LauncherFavoriteMode.Independent,
            2 => LauncherFavoriteMode.Off,
            _ => LauncherFavoriteMode.FollowDock
        };
        _maintenance.SetLauncherFavoriteMode(mode);
        LoadLauncherEnabled();
    }

    private void LauncherBackdropMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingLauncherEnabled || LauncherBackdropModeComboBox is null) return;
        var index = LauncherBackdropModeComboBox.SelectedIndex;
        var mode = index switch
        {
            1 => LauncherBackdropMode.WallpaperCanvas,
            2 => LauncherBackdropMode.ShowDesktop,
            _ => LauncherBackdropMode.LiveWindow
        };
        _maintenance.SetLauncherBackdropMode(mode);
        LoadLauncherEnabled();
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
