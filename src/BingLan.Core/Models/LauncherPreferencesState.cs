namespace BingLan.Core.Models;

public enum LauncherMainTab
{
    Apps = 0,
    Files = 1,
    Groups = 2
}

/// <summary>
/// Machine-local browsing and privacy preferences for the Ice Blue Overview Card.
/// Never exported in theme packs.
/// </summary>
public sealed class LauncherPreferencesState
{
    public LauncherMainTab LastTab { get; set; } = LauncherMainTab.Apps;
    public bool ShowRecentFiles { get; set; } = true;
    public bool AppsSortByName { get; set; }
    public bool FilesUseListView { get; set; }
    public bool GroupsUseListView { get; set; }

    public void Normalize()
    {
        if (!Enum.IsDefined(LastTab))
        {
            LastTab = LauncherMainTab.Apps;
        }
    }
}
