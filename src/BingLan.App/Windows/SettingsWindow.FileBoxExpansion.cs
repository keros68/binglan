using System.Windows;

namespace BingLan.App.Windows;

public partial class SettingsWindow
{
    private void FileBoxExpandUpward_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingViewMode || _selectedComponentEntry?.Window is not FileBoxWindow box || box.IsEdgeDocked)
        {
            return;
        }

        var enabled = FileBoxExpandUpwardCheckBox.IsChecked == true;
        box.SetExpandUpward(enabled);
        ComponentSettingsStatusText.Text = enabled
            ? $"“{box.State.Title}”已设为向上展开"
            : $"“{box.State.Title}”已设为向下展开";
    }
}
