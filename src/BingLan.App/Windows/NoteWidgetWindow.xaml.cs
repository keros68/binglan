using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BingLan.Core.Models;
using BingLan.Core.Services;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using FontStyle = System.Windows.FontStyle;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextAlignment = System.Windows.TextAlignment;

namespace BingLan.App.Windows;

public partial class NoteWidgetWindow : WidgetWindowBase
{
    private static readonly double[] FontSizePresets = [10d, 12d, 14d, 16d, 18d, 24d, 32d];

    private readonly DispatcherTimer _changeTimer;
    private bool _isApplyingTypography;
    private bool _isSyncingTitle;

    public NoteWidgetWindow(NoteWidgetState state)
    {
        State = state;
        InitializeComponent();
        DataContext = state;
        ApplyAppearance(state.Appearance);
        ApplyPlacement(state.Placement, state.IsLocked);
        WidgetCornerRadius = state.CornerRadius;

        TitleEditor.Text = state.Title;
        BodyEditor.Text = NoteService.ToEditorText(state.Content);

        BodyFontPicker.ItemsSource = InstalledFontCatalog.Names;
        foreach (var size in FontSizePresets)
        {
            BodyFontSizePicker.Items.Add(size);
        }
        ApplyBodyTypography();

        _changeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _changeTimer.Tick += (_, _) =>
        {
            _changeTimer.Stop();
            var previousTitle = State.Title;
            CommitEditors();
            NotifyStateChanged();
            if (!string.Equals(previousTitle, State.Title, StringComparison.Ordinal))
            {
                NotifyTitleChanged();
            }
        };
    }

    public NoteWidgetState State { get; }

    protected override System.Windows.Controls.TextBox? TitleEditorBox => TitleEditor;

    public override bool ApplyWidgetTitle(string? title, IEnumerable<string>? otherSameKindTitles = null)
    {
        var committed = WidgetTitleRules.CommitUniqueTitle(
            title,
            EditableWidgetTitleKind.Note,
            otherSameKindTitles ?? SameKindOtherTitles?.Invoke());
        var changed = !string.Equals(State.Title, committed, StringComparison.Ordinal);
        State.Title = committed;
        if (!string.Equals(TitleEditor.Text, committed, StringComparison.Ordinal))
        {
            _isSyncingTitle = true;
            try
            {
                TitleEditor.Text = committed;
            }
            finally
            {
                _isSyncingTitle = false;
            }
        }
        if (changed)
        {
            NotifyStateChanged();
            NotifyTitleChanged();
        }
        return changed;
    }

    protected override void CommitTitleEdit()
    {
        var previousTitle = State.Title;
        CommitNow();
        _isSyncingTitle = true;
        try
        {
            TitleEditor.Text = State.Title;
        }
        finally
        {
            _isSyncingTitle = false;
        }
        if (!string.Equals(previousTitle, State.Title, StringComparison.Ordinal))
        {
            NotifyTitleChanged();
        }
    }

    protected override void OnLockChanged(bool locked) => State.IsLocked = locked;

    protected override void OnClosed(EventArgs e)
    {
        _changeTimer.Stop();
        base.OnClosed(e);
    }

    public void CaptureState()
    {
        CommitEditors();
        CopyPlacementTo(State.Placement);
        State.CornerRadius = WidgetCornerRadius;
    }

    private void CommitEditors()
    {
        NoteService.CommitTitle(State, TitleEditor.Text, SameKindOtherTitles?.Invoke());
        NoteService.CommitContent(State, BodyEditor.Text);
    }

    private void ScheduleChangeNotification()
    {
        _changeTimer.Stop();
        _changeTimer.Start();
    }

    private void CommitNow()
    {
        _changeTimer.Stop();
        CommitEditors();
        NotifyStateChanged();
    }

    private void FormatToggle_Click(object sender, RoutedEventArgs e)
    {
        FormatBar.Visibility = FormatBar.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void TitleEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && !_isSyncingTitle)
        {
            ScheduleChangeNotification();
        }
    }

    private void BodyEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ScheduleChangeNotification();
        }
    }

    private void BodyEditor_LostFocus(object sender, RoutedEventArgs e) => CommitNow();

    private void ApplyBodyTypography()
    {
        _isApplyingTypography = true;
        try
        {
            var typography = State.BodyTypography;
            var resolvedFamily = InstalledFontCatalog.Resolve(typography.FontFamily);
            typography.FontFamily = resolvedFamily;
            BodyEditor.FontFamily = InstalledFontCatalog.Create(resolvedFamily);
            BodyEditor.FontSize = typography.FontSize;
            var brush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(typography.Color)!);
            brush.Freeze();
            BodyEditor.Foreground = brush;
            BodyEditor.FontWeight = typography.Bold
                ? FontWeights.Bold
                : InstalledFontCatalog.WeightOf(resolvedFamily, FontWeights.Normal);
            BodyEditor.FontStyle = typography.Italic ? FontStyles.Italic : FontStyles.Normal;
            BodyEditor.TextAlignment = typography.Alignment switch
            {
                NoteBodyTextAlignment.Center => TextAlignment.Center,
                NoteBodyTextAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left
            };

            BodyFontPicker.SelectedItem = resolvedFamily;
            BodyFontSizePicker.Text = typography.FontSize.ToString("0.#");
            BodyColorEditor.Text = typography.Color;
            BodyBoldToggle.IsChecked = typography.Bold;
            BodyItalicToggle.IsChecked = typography.Italic;
            AlignLeftToggle.IsChecked = typography.Alignment == NoteBodyTextAlignment.Left;
            AlignCenterToggle.IsChecked = typography.Alignment == NoteBodyTextAlignment.Center;
            AlignRightToggle.IsChecked = typography.Alignment == NoteBodyTextAlignment.Right;
        }
        finally
        {
            _isApplyingTypography = false;
        }
    }

    /// <summary>Sets the body text colour, for example when the shared card material changes.</summary>
    internal void SetBodyColor(string color)
    {
        State.BodyTypography.Color = color;
        TypographyChanged();
    }

    private void TypographyChanged()
    {
        ApplyBodyTypography();
        NotifyStateChanged();
    }

    private void BodyFontPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingTypography || BodyFontPicker.SelectedItem is not string family)
        {
            return;
        }
        State.BodyTypography.FontFamily = family;
        TypographyChanged();
    }

    private void BodyFontSizePicker_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        CommitFontSize();

    private void BodyFontSizePicker_LostFocus(object sender, RoutedEventArgs e) =>
        CommitFontSize();

    private void BodyFontSizePicker_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitFontSize();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void CommitFontSize()
    {
        if (_isApplyingTypography ||
            !double.TryParse(BodyFontSizePicker.Text, out var size))
        {
            return;
        }
        State.BodyTypography.FontSize = size;
        TypographyChanged();
    }

    private void BodyColorEditor_LostFocus(object sender, RoutedEventArgs e) => CommitBodyColor();

    private void BodyColorEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitBodyColor();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void CommitBodyColor()
    {
        if (_isApplyingTypography)
        {
            return;
        }
        State.BodyTypography.Color = BodyColorEditor.Text.Trim();
        TypographyChanged();
    }

    private void BodyBoldToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingTypography)
        {
            return;
        }
        State.BodyTypography.Bold = BodyBoldToggle.IsChecked == true;
        TypographyChanged();
    }

    private void BodyItalicToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingTypography)
        {
            return;
        }
        State.BodyTypography.Italic = BodyItalicToggle.IsChecked == true;
        TypographyChanged();
    }

    private void AlignmentToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingTypography || sender is not ToggleButton pressed)
        {
            return;
        }

        var alignment = pressed.Name switch
        {
            nameof(AlignCenterToggle) => NoteBodyTextAlignment.Center,
            nameof(AlignRightToggle) => NoteBodyTextAlignment.Right,
            _ => NoteBodyTextAlignment.Left
        };
        if (pressed.IsChecked != true && State.BodyTypography.Alignment == alignment)
        {
            pressed.IsChecked = true;
            return;
        }

        State.BodyTypography.Alignment = alignment;
        TypographyChanged();
    }
}
