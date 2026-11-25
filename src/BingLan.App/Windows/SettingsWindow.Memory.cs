using System.Windows;

namespace BingLan.App.Windows;

public partial class SettingsWindow
{
    private bool _presetPreviewDirty;

    internal bool HasBuiltSearchIndex => _searchIndex is not null;

    private bool IsPresetPreviewPageVisible() =>
        SchemePage.Visibility == Visibility.Visible
        || AppearancePage.Visibility == Visibility.Visible;

    private void EnsurePresetPreviewRendered()
    {
        if (_presetPreviewDirty || PresetPreviewImage.Source is null)
        {
            _presetPreviewDirty = false;
            UpdatePresetPreview(GetSelectedPreset());
        }
    }
}
