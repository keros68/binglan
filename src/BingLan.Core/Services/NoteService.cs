using BingLan.Core.Models;

namespace BingLan.Core.Services;

public static class NoteService
{
    public const string DefaultTitle = WidgetTitleRules.DefaultNoteTitle;

    public static NoteWidgetState CreateDefaultWidget() => new();

    public static void CommitTitle(NoteWidgetState note, string? title) =>
        CommitTitle(note, title, otherNoteTitles: null);

    public static void CommitTitle(NoteWidgetState note, string? title, IEnumerable<string>? otherNoteTitles)
    {
        ArgumentNullException.ThrowIfNull(note);
        note.Title = WidgetTitleRules.CommitUniqueTitle(title, EditableWidgetTitleKind.Note, otherNoteTitles);
    }

    public static void CommitContent(NoteWidgetState note, string? content)
    {
        ArgumentNullException.ThrowIfNull(note);
        note.Content = NormalizeLineEndings(content ?? string.Empty);
    }

    public static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n").Replace('\r', '\n');

    public static string ToEditorText(string content) =>
        content.Replace("\n", Environment.NewLine);
}
