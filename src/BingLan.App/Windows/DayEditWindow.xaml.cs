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
    private string _lastCommittedText;

    /// <param name="fontScale">The todo card's 字号缩放 setting, so the editor reads at the card's size.</param>
    /// <param name="fontFamily">The todo card's 字体 setting; null keeps the interface font.</param>
    public DayEditWindow(
        DateTime date,
        IReadOnlyList<string> initialLines,
        double fontScale = 1d,
        System.Windows.Media.FontFamily? fontFamily = null)
    {
        InitializeComponent();
        _date = date;
        DateTitle.FontSize *= fontScale;
        Editor.FontSize *= fontScale;
        if (fontFamily is not null)
        {
            // The app-wide TextBox style pins its font, so the editor is set directly.
            FontFamily = fontFamily;
            Editor.FontFamily = fontFamily;
        }
        DateTitle.Text = date.ToString("yyyy年M月d日");
        // The first entry is numbered "1." from the moment the editor opens — the
        // first thing to do always gets the first number.
        Editor.Text = initialLines.Count == 0
            ? "1. "
            : string.Join(Environment.NewLine,
                initialLines.Select((line, index) => $"{index + 1}. {line}"));
        _lastCommittedText = Editor.Text;
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

    public void CommitPendingChanges()
    {
        if (Editor.Text == _lastCommittedText)
        {
            return;
        }
        // A commit can cause the host to capture its state again.
        _lastCommittedText = Editor.Text;
        Committed?.Invoke(_date, ReadLines());
    }

    protected override void OnClosed(EventArgs e)
    {
        CommitPendingChanges();
        base.OnClosed(e);
    }

    private IReadOnlyList<string> ReadLines()
    {
        var body = Editor.Text.Replace("\r\n", "\n");
        return body.Split('\n');
    }
}
