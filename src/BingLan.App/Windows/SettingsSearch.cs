using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Panel = System.Windows.Controls.Panel;
using RadioButton = System.Windows.Controls.RadioButton;

namespace BingLan.App.Windows;

/// <summary>One searchable setting: where it sits and what to reveal to reach it.</summary>
internal sealed record SettingsSearchEntry(
    string Name,
    string Path,
    string Keywords,
    string Description,
    FrameworkElement Target,
    FrameworkElement Page,
    IReadOnlyList<Expander> Folds,
    string? Scope)
{
    /// <summary>“页面 › 分区 › 设置名”, as the result list and screen readers show it.</summary>
    public string Display => $"{Path} › {Name}";
}

/// <summary>
/// The settings search index is read from the pages themselves: every titled
/// <see cref="SettingsRow"/>, section heading, choice card and fold becomes an entry, so
/// the index cannot fall behind the pages. Attached properties add synonyms, mark the
/// component a panel belongs to, or leave out lists that are filled at run time.
/// </summary>
internal static class SettingsSearch
{
    public static readonly DependencyProperty KeywordsProperty = DependencyProperty.RegisterAttached(
        "Keywords", typeof(string), typeof(SettingsSearch), new PropertyMetadata(string.Empty));

    /// <summary>
    /// Which desktop component a panel edits ("Builtin", "Widget", "FileBox", "Todo" or
    /// "Kind:Weather"); the search selects a matching component before showing the row.
    /// </summary>
    public static readonly DependencyProperty ScopeProperty = DependencyProperty.RegisterAttached(
        "Scope", typeof(string), typeof(SettingsSearch), new PropertyMetadata(null));

    /// <summary>The name a fold or component panel adds to the result path.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.RegisterAttached(
        "Title", typeof(string), typeof(SettingsSearch), new PropertyMetadata(null));

    public static readonly DependencyProperty ExcludeProperty = DependencyProperty.RegisterAttached(
        "Exclude", typeof(bool), typeof(SettingsSearch), new PropertyMetadata(false));

    public static string GetKeywords(DependencyObject element) => (string)element.GetValue(KeywordsProperty);
    public static void SetKeywords(DependencyObject element, string value) => element.SetValue(KeywordsProperty, value);
    public static string? GetScope(DependencyObject element) => (string?)element.GetValue(ScopeProperty);
    public static void SetScope(DependencyObject element, string? value) => element.SetValue(ScopeProperty, value);
    public static string? GetTitle(DependencyObject element) => (string?)element.GetValue(TitleProperty);
    public static void SetTitle(DependencyObject element, string? value) => element.SetValue(TitleProperty, value);
    public static bool GetExclude(DependencyObject element) => (bool)element.GetValue(ExcludeProperty);
    public static void SetExclude(DependencyObject element, bool value) => element.SetValue(ExcludeProperty, value);

    internal static List<SettingsSearchEntry> Build(
        IEnumerable<(FrameworkElement Page, string Title)> pages,
        Style sectionHeaderStyle,
        Style choiceCardStyle)
    {
        var entries = new List<SettingsSearchEntry>();
        foreach (var (page, title) in pages)
        {
            Walk(page, page, [title], [], null);
        }
        return entries;

        void Walk(DependencyObject node, FrameworkElement page, List<string> path, List<Expander> folds, string? scope)
        {
            if (GetExclude(node))
            {
                return;
            }

            if (GetScope(node) is { } nodeScope)
            {
                scope = nodeScope;
            }
            if (node is not Expander && GetTitle(node) is { Length: > 0 } scopeTitle)
            {
                path = [.. path, scopeTitle];
            }

            switch (node)
            {
                case Expander fold:
                    var foldTitle = GetTitle(fold) ?? AutomationProperties.GetName(fold);
                    if (foldTitle.Length > 0)
                    {
                        entries.Add(Entry(foldTitle, path, fold, fold, page, folds, scope, includeChoices: false));
                        path = [.. path, foldTitle];
                    }
                    folds = [.. folds, fold];
                    break;
                case SettingsRow { Header.Length: > 0 } row:
                    // A row without a control introduces the block below it, such as the
                    // top bar's module list; that block's options describe the row.
                    var options = row.Content is null ? NextSibling(row) ?? row : row;
                    entries.Add(Entry(row.Header, path, row, row, page, folds, scope, row.Description, options: options));
                    break;
                case TextBlock { Text.Length: > 0 } heading when ReferenceEquals(heading.Style, sectionHeaderStyle):
                    // A heading stands for the block that follows it.
                    var block = NextSibling(heading) ?? heading;
                    entries.Add(Entry(heading.Text, path, heading, block, page, folds, scope));
                    break;
                case RadioButton card when ReferenceEquals(card.Style, choiceCardStyle)
                    && AutomationProperties.GetName(card) is { Length: > 0 } cardName:
                    entries.Add(Entry(cardName, path, card, card, page, folds, scope));
                    break;
            }

            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            {
                Walk(child, page, path, folds, scope);
            }
        }
    }

    private static SettingsSearchEntry Entry(
        string name,
        List<string> path,
        DependencyObject source,
        FrameworkElement target,
        FrameworkElement page,
        List<Expander> folds,
        string? scope,
        string description = "",
        bool includeChoices = true,
        FrameworkElement? options = null)
    {
        // Choices inside a row (switch labels, list options) count as keywords, so
        // "电量" finds the module list and "把手" finds the Dock reveal setting.
        var choices = includeChoices
            ? Descendants(options ?? target)
                .Select(child => child switch
                {
                    ToggleButton { Content: string text } => text,
                    ComboBoxItem { Content: string text } => text,
                    _ => null
                })
                .OfType<string>()
            : [];
        var keywords = string.Join(' ', new[] { GetKeywords(source) }.Concat(choices));
        return new SettingsSearchEntry(name, string.Join(" › ", path), keywords, description, target, page, folds, scope);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static FrameworkElement? NextSibling(FrameworkElement element)
    {
        if (element.Parent is not Panel panel)
        {
            return null;
        }
        var index = panel.Children.IndexOf(element);
        return index >= 0 && index + 1 < panel.Children.Count ? panel.Children[index + 1] as FrameworkElement : null;
    }

    /// <summary>
    /// Matches by name first, then synonyms and choices, then the path, then the
    /// description; ties keep page order. Only entries the page can show are returned.
    /// </summary>
    internal static List<SettingsSearchEntry> Find(
        IReadOnlyList<SettingsSearchEntry> entries,
        string query,
        Func<SettingsSearchEntry, bool> isAvailable,
        int limit = 12)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return [];
        }

        return entries
            .Select((entry, order) => (entry, order, rank: Rank(entry, words)))
            .Where(match => match.rank < int.MaxValue && isAvailable(match.entry))
            .OrderBy(match => match.rank)
            .ThenBy(match => match.order)
            .Select(match => match.entry)
            .DistinctBy(entry => (entry.Path, entry.Name))
            .Take(limit)
            .ToList();
    }

    private static int Rank(SettingsSearchEntry entry, string[] words)
    {
        var rank = 0;
        foreach (var word in words)
        {
            var wordRank = Contains(entry.Name, word)
                ? entry.Name.StartsWith(word, StringComparison.OrdinalIgnoreCase) ? 0 : 1
                : Contains(entry.Keywords, word) ? 2
                : Contains(entry.Path, word) ? 3
                : Contains(entry.Description, word) ? 4
                : int.MaxValue;
            if (wordRank == int.MaxValue)
            {
                return int.MaxValue;
            }
            rank = Math.Max(rank, wordRank);
        }
        return rank;
    }

    private static bool Contains(string text, string word) =>
        text.Contains(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether nothing between the element and <paramref name="stop"/> is collapsed; the
    /// page itself and folds are left to the caller, which opens them.
    /// </summary>
    internal static bool IsShown(FrameworkElement element, DependencyObject stop)
    {
        for (DependencyObject? node = element; node is not null && !ReferenceEquals(node, stop); node = LogicalTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: Visibility.Collapsed })
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Briefly tints the element so the eye finds it after a jump. With animations
    /// turned off in Windows the tint appears and disappears without fading.
    /// </summary>
    internal static void Flash(FrameworkElement element)
    {
        if (AdornerLayer.GetAdornerLayer(element) is not { } layer
            || element.TryFindResource("SettingsHighlightBrush") is not Brush brush)
        {
            return;
        }

        var adorner = new HighlightAdorner(element, brush);
        layer.Add(adorner);
        var hold = TimeSpan.FromMilliseconds(900);
        var fade = TimeSpan.FromMilliseconds(700);
        if (SystemParameters.ClientAreaAnimation)
        {
            var animation = new DoubleAnimation(1, 0, new Duration(fade)) { BeginTime = hold };
            animation.Completed += (_, _) => layer.Remove(adorner);
            adorner.BeginAnimation(UIElement.OpacityProperty, animation);
            return;
        }

        var timer = new DispatcherTimer { Interval = hold + fade };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            layer.Remove(adorner);
        };
        timer.Start();
    }

    private sealed class HighlightAdorner : Adorner
    {
        private readonly Brush _brush;

        public HighlightAdorner(UIElement element, Brush brush)
            : base(element)
        {
            _brush = brush;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var size = AdornedElement.RenderSize;
            drawingContext.DrawRoundedRectangle(_brush, null, new Rect(size), 8, 8);
        }
    }
}
