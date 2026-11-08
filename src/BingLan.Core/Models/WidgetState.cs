using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using BingLan.Core.Services;

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
    /// The size, in DIPs, the card had when it last left the today list, so switching
    /// back from the calendar returns to it. 0 while the list has not been left yet.
    /// </summary>
    public double ListWidth { get; set; }

    /// <inheritdoc cref="ListWidth"/>
    public double ListHeight { get; set; }

    /// <summary>The size, in DIPs, the card had when it last left the calendar; 0 until then.</summary>
    public double WeeklyWidth { get; set; }

    /// <inheritdoc cref="WeeklyWidth"/>
    public double WeeklyHeight { get; set; }

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

/// <summary>Whether an entry is a checkable to-do, a calendar event, or a countdown.</summary>
public enum TodoItemKind
{
    Todo = 0,
    Event = 1,
    Countdown = 2
}

public sealed class TodoItemState : INotifyPropertyChanged
{
    private string _text = "";
    private bool _isCompleted;
    private DateTime? _date;
    private TodoItemKind _kind;
    private TimeOnly? _time;

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
            OnPropertyChanged(nameof(CountdownLabel));
            OnPropertyChanged(nameof(IsDueSoon));
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
        set
        {
            _date = value is { } date
                ? DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified)
                : null;
            OnPropertyChanged(nameof(CountdownLabel));
        }
    }

    /// <summary>
    /// Remaining time for an open to-do with a deadline or a standalone countdown.
    /// </summary>
    [JsonIgnore]
    public string? CountdownLabel => TodoService.CountdownLabel(this, DateTime.Now);

    private DateTime? _deadlineDate;
    private TimeOnly? _deadlineTime;

    /// <summary>Optional local calendar deadline, independent of the entry's calendar placement.</summary>
    public DateTime? DeadlineDate
    {
        get => _deadlineDate;
        set
        {
            _deadlineDate = value is { } date
                ? DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified) : null;
            RefreshCountdownLabel();
        }
    }

    /// <summary>Optional local time of the deadline; null means a calendar-day countdown.</summary>
    public TimeOnly? DeadlineTime
    {
        get => _deadlineTime;
        set
        {
            _deadlineTime = value is { } time ? new TimeOnly(time.Hour, time.Minute) : null;
            RefreshCountdownLabel();
        }
    }

    [JsonIgnore]
    public string? DeadlineDescription => DeadlineDate is { } date
        ? $"{(Kind == TodoItemKind.Countdown ? "目标" : "截止")}：{date:yyyy-MM-dd}" +
            (DeadlineTime is { } time ? $" {time:HH:mm}（本机时间）" : "")
        : null;

    /// <summary>
    /// Whether the entry is a checkable to-do, an untickable calendar event, or an
    /// untickable day countdown.
    /// </summary>
    public TodoItemKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value)
            {
                return;
            }
            _kind = value;
            OnPropertyChanged();
            RefreshCountdownLabel();
        }
    }

    /// <summary>The event's optional time of day; to-dos do not use it.</summary>
    public TimeOnly? Time
    {
        get => _time;
        set
        {
            if (_time == value)
            {
                return;
            }
            _time = value;
            OnPropertyChanged();
        }
    }

    /// <summary>When the entry was created; null for entries made before dates were tracked.</summary>
    public DateTimeOffset? CreatedDate { get; set; }

    /// <summary>When the entry was last marked complete; null while it is open.</summary>
    public DateTimeOffset? CompletedDate { get; set; }

    /// <summary>
    /// Files or folders this entry links to, as full local paths, so a long-running
    /// piece of work keeps its documents one click away. Local only: links are never
    /// uploaded and never leave the machine in a theme package.
    /// </summary>
    public ObservableCollection<string> LinkedPaths { get; set; } = [];

    /// <summary>Whether an open to-do's deadline is close enough to surface in the list.</summary>
    [JsonIgnore]
    public bool IsDueSoon => TodoService.IsDueSoon(this, DateTime.Now);

    /// <summary>
    /// Refreshes clock-derived bindings without changing persisted state.
    /// </summary>
    public void RefreshCountdownLabel()
    {
        OnPropertyChanged(nameof(CountdownLabel));
        OnPropertyChanged(nameof(DeadlineDescription));
        OnPropertyChanged(nameof(IsDueSoon));
    }

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
