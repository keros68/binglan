using System.Windows;
using System.Windows.Input;

namespace BingLan.App.Windows;

/// <summary>
/// One calendar day's entry editor: a numbered multi-line body where every line is
/// one of that day's to-dos, mirroring the familiar desktop-calendar popup.
/// Closing the window through any path commits the current lines.
/// </summary>
public partial class DayEditWindow : Window
{
    public event Action<DateTime, IReadOnlyList<string>>? Committed;

    private readonly DateTime _date;

    public DayEditWindow(DateTime date, IReadOnlyList<string> initialLines)
    {
        InitializeComponent();
        _date = date;
        DateTitle.Text = date.ToString("yyyy年M月d日");
        Editor.Text = string.Join(Environment.NewLine,
            initialLines.Select((line, index) => $"{index + 1}. {line}"));
        Loaded += (_, _) => Editor.Focus();
    }

    private void Editor_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not Key.Enter)
        {
            return;
        }
        e.Handled = true;

        // A new line automatically carries the next sequential number, so every
        // numbered line is one of the day's entries.
        var next = 1;
        for (var line = 0; line < Editor.LineCount; line++)
        {
            if (Editor.GetLineText(line).Trim().Length > 0)
            {
                next++;
            }
        }

        var caret = Editor.CaretIndex;
        var insert = Environment.NewLine + next + ". ";
        Editor.Text = Editor.Text.Insert(caret, insert);
        Editor.CaretIndex = caret + insert.Length;
    }

    private void Commit_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
        {
            return;
        }
        Committed?.Invoke(_date, ReadLines());
    }

    private IReadOnlyList<string> ReadLines()
    {
        var body = Editor.Text.Replace("\r\n", "\n");
        return body.Split('\n');
    }
}
