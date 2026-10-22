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
        // The first entry is numbered "1." from the moment the editor opens — the
        // first thing to do always gets the first number.
        Editor.Text = initialLines.Count == 0
            ? "1. "
            : string.Join(Environment.NewLine,
                initialLines.Select((line, index) => $"{index + 1}. {line}"));
        Editor.CaretIndex = Editor.Text.Length;
        Loaded += (_, _) => Editor.Focus();
    }

    private void Editor_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not Key.Enter)
        {
            return;
        }
        e.Handled = true;

        // Enter always starts the next numbered line: one line, one entry.
        var next = Editor.Text.Trim().Length == 0 ? 1 : Editor.LineCount + 1;

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
