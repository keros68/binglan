using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BingLan.Core.Models;

public enum DesktopGroupCategory
{
    None,
    Applications,
    Folders,
    Files
}

/// <summary>How a box shows its mappings: tiles with big icons, or a compact one-line list.</summary>
public enum FileBoxViewMode
{
    Tiles,
    List
}

public sealed class WindowPlacement
{
    public double Left { get; set; } = 92;
    public double Top { get; set; } = 92;
    public double Width { get; set; } = 286;
    public double Height { get; set; } = 336;

    /// <summary>
    /// Where the card last sat on each arrangement of monitors, so plugging a second
    /// screen back in puts the card where it was on that arrangement.
    /// </summary>
    public List<DisplayLayoutPlacement> DisplayLayouts { get; set; } = [];
}

/// <summary>The card's bounds, in physical pixels, on one arrangement of monitors.</summary>
public sealed class DisplayLayoutPlacement
{
    public string Layout { get; set; } = string.Empty;
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
}

public sealed class WidgetAppearanceState
{
    private string _backgroundColor = WidgetAppearanceRules.DefaultBackgroundColor;
    private double _backgroundOpacity = WidgetAppearanceRules.DefaultBackgroundOpacity;
    private string _headerColor = WidgetAppearanceRules.DefaultHeaderColor;
    private double _headerOpacity = WidgetAppearanceRules.DefaultHeaderOpacity;
    private string _textColor = WidgetAppearanceRules.DefaultTextColor;
    private string _titleFontFamily = WidgetAppearanceRules.DefaultTitleFontFamily;

    public string BackgroundColor
    {
        get => _backgroundColor;
        set => _backgroundColor = WidgetAppearanceRules.CoerceBackgroundColor(value);
    }

    public double BackgroundOpacity
    {
        get => _backgroundOpacity;
        set => _backgroundOpacity = WidgetAppearanceRules.CoerceBackgroundOpacity(value);
    }

    public string HeaderColor
    {
        get => _headerColor;
        set => _headerColor = WidgetAppearanceRules.CoerceHeaderColor(value);
    }

    public double HeaderOpacity
    {
        get => _headerOpacity;
        set => _headerOpacity = WidgetAppearanceRules.CoerceHeaderOpacity(value);
    }

    public string TextColor
    {
        get => _textColor;
        set => _textColor = WidgetAppearanceRules.CoerceTextColor(value);
    }

    public string TitleFontFamily
    {
        get => _titleFontFamily;
        set => _titleFontFamily = WidgetAppearanceRules.CoerceTitleFontFamily(value);
    }

    public bool TitleFontBold { get; set; } = WidgetAppearanceRules.DefaultTitleFontBold;

    public bool TitleFontItalic { get; set; } = WidgetAppearanceRules.DefaultTitleFontItalic;
}

public sealed class TodoWidgetState
{
    private double _cornerRadius = WidgetAppearanceRules.DefaultCornerRadius;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "今日待办";
    public bool HideCompleted { get; set; }

    /// <summary>Whether the card shows the five-week calendar view instead of today's list.</summary>
    public bool WeeklyView { get; set; }

    /// <summary>How many weeks (1-5) the weekly calendar window spans; the
    /// renderer and the store both clamp, so hand-edited files stay sane.</summary>
    public int WeeklyWeekCount { get; set; } = 4;

    /// <summary>Whether the card is folded up to its title bar.</summary>
    public bool IsCollapsed { get; set; }

    /// <summary>The height to unfold to, in DIPs; 0 while it has never been folded.</summary>
    public double ExpandedHeight { get; set; }

    /// <summary>
    /// The size, in DIPs, the card had in the view that is not showing (the list while
    /// the calendar shows, and the reverse), so toggling back returns to it. 0 while
    /// that view has not been left yet.
    /// </summary>
    public double OtherViewWidth { get; set; }

    /// <inheritdoc cref="OtherViewWidth"/>
    public double OtherViewHeight { get; set; }

    public bool IsLocked { get; set; }
    public WidgetAppearanceState Appearance { get; set; } = new();
    public double CornerRadius
    {
        get => _cornerRadius;
        set => _cornerRadius = WidgetAppearanceRules.CoerceCornerRadius(value);
    }
    public WindowPlacement Placement { get; set; } = new();
    public List<TodoItemState> Items { get; set; } = [];
}

public sealed class TodoItemState : INotifyPropertyChanged
{
    private string _text = "";
    private bool _isCompleted;
    private DateTime? _date;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
            {
                return;
            }
            _text = value;
            OnPropertyChanged();
        }
    }
    public bool IsCompleted
    {
        get => _isCompleted;
        set
        {
            if (_isCompleted == value)
            {
                return;
            }
            _isCompleted = value;
            OnPropertyChanged();
        }
    }
    public int Order { get; set; }

    /// <summary>
    /// The calendar day this entry sits on in both the weekly view and today list.
    /// Null is reserved for legacy data awaiting migration. Stored as an
    /// Unspecified-kind midnight so the JSON round trip keeps
    /// the same ticks even when the file moves between time zones.
    /// </summary>
    public DateTime? Date
    {
        get => _date;
        set => _date = value is { } date
            ? DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified)
            : null;
    }

    /// <summary>When the entry was created; null for entries made before dates were tracked.</summary>
    public DateTimeOffset? CreatedDate { get; set; }

    /// <summary>When the entry was last marked complete; null while it is open.</summary>
    public DateTimeOffset? CompletedDate { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class FileBoxState
{
    private double _cornerRadius = WidgetAppearanceRules.DefaultCornerRadius;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "桌面分组";
    public DesktopGroupCategory DesktopCategory { get; set; }

    /// <summary>
    /// File types this box collects when the desktop is organised, as lower-case
    /// extensions such as ".pdf". Empty with <see cref="CollectFolders"/> off means the
    /// box takes no part in organising, unless it is one of the original category boxes.
    /// </summary>
    public List<string> CollectExtensions { get; set; } = [];

    /// <summary>Whether this box collects desktop folders when the desktop is organised.</summary>
    public bool CollectFolders { get; set; }

    /// <summary>Whether the box is folded up to its title bar.</summary>
    public bool IsCollapsed { get; set; }

    /// <summary>Whether mappings show as tiles or as a compact list; tiles by default.</summary>
    public FileBoxViewMode ViewMode { get; set; }

    /// <summary>The height to unfold to, in DIPs; 0 while it has never been folded.</summary>
    public double ExpandedHeight { get; set; }

    public bool IsLocked { get; set; }
    public WidgetAppearanceState Appearance { get; set; } = new();
    public double CornerRadius
    {
        get => _cornerRadius;
        set => _cornerRadius = WidgetAppearanceRules.CoerceCornerRadius(value);
    }
    public WindowPlacement Placement { get; set; } = new()
    {
        Left = 432,
        Top = 92,
        Width = 366,
        Height = 306
    };
    public List<FileMappingState> Items { get; set; } = [];
}

public sealed class FileMappingState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = "";
    public string? Title { get; set; }
    public int Order { get; set; }
}
