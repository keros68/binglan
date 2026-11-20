using System.Runtime.CompilerServices;

namespace BingLan.Core.TopBar;

public enum TopBarSearchItemKind
{
    App,
    Folder,
    File
}

/// <summary>
/// One thing search can open: an application, folder or file from local search
/// sources. Pure data — the app obtains candidates and the rules score them.
/// </summary>
public sealed record TopBarSearchCandidate(
    TopBarSearchItemKind Kind,
    string DisplayName,
    string FullFileName,
    string Path);

/// <summary>A parsed global hotkey: Win32 modifier flags plus a virtual-key code.</summary>
public sealed record TopBarHotkeyBinding(uint Modifiers, int VirtualKey);

/// <summary>
/// Matching and ranking for the top bar's search, plus the hotkey text format the
/// settings persist. Everything here is pure and thread-safe: the window snapshots
/// candidates, scores them off the UI thread and marshals the result back.
/// </summary>
public static class TopBarSearchRules
{
    /// <summary>Results beyond this many matches add scroll without adding signal.</summary>
    public const int MaxResults = 24;

    // Win32 RegisterHotKey modifier flags; the letters are what the settings file stores.
    public const uint ModifierAlt = 0x0001;
    public const uint ModifierControl = 0x0002;
    public const uint ModifierShift = 0x0004;
    public const uint ModifierWindows = 0x0008;

    private const int ScoreExact = 6;
    private const int ScoreWordExact = 5;
    private const int ScorePrefix = 4;
    private const int ScoreContains = 3;
    private const int ScoreFuzzy = 1;
    private static readonly char[] QuerySeparators = [' ', '　', '\t'];
    private static readonly char[] WordSeparators =
        [' ', '\t', '.', '_', '-', '(', ')', '[', ']', '+', '&', '，', '、', '·'];
    private static readonly char[] PathSeparators = ['\\', '/'];
    private static readonly ConditionalWeakTable<TopBarSearchCandidate, CachedCandidateKeys> KeyCache = new();

    private sealed record CachedCandidateKeys(
        string Name,
        string FileName,
        string Initials);

    private readonly record struct CandidateKeys(
        string Name,
        string FileName,
        string Initials);

    /// <summary>
    /// Scores candidates against the whitespace-separated query. Every token must hit
    /// the name, pinyin initials or full file name. Regular matches rank by exact,
    /// whole-word, prefix and contains quality. Only an empty regular result enables
    /// typo matching for tokens of 4+ characters. All tokens must still match.
    /// Apps receive a fixed boost; ties fall back to kind, name and path.
    /// </summary>
    public static IReadOnlyList<TopBarSearchCandidate> Search(
        IEnumerable<TopBarSearchCandidate> candidates,
        string query,
        int limit = MaxResults) =>
        SearchCore(candidates, query, allowFuzzy: true, limit);

    /// <summary>
    /// Keeps already-rendered stage-1 results in their exact order and appends ranked,
    /// deduplicated stage-2 index matches below them up to <see cref="MaxResults"/>.
    /// </summary>
    public static IReadOnlyList<TopBarSearchCandidate> AppendSupplementalResults(
        IReadOnlyList<TopBarSearchCandidate> primaryResults,
        IEnumerable<TopBarSearchCandidate> supplementalCandidates,
        string query)
    {
        if (primaryResults.Count >= MaxResults)
        {
            return primaryResults;
        }

        var seenPaths = new HashSet<string>(primaryResults.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
        var novelCandidates = new List<TopBarSearchCandidate>();
        foreach (var candidate in supplementalCandidates)
        {
            if (seenPaths.Add(candidate.Path))
            {
                novelCandidates.Add(candidate);
            }
        }

        if (novelCandidates.Count == 0)
        {
            return primaryResults;
        }

        var appended = SearchCore(novelCandidates, query, allowFuzzy: primaryResults.Count == 0);
        if (appended.Count == 0)
        {
            return primaryResults;
        }

        var merged = new List<TopBarSearchCandidate>(Math.Min(MaxResults, primaryResults.Count + appended.Count));
        merged.AddRange(primaryResults);
        var remaining = MaxResults - merged.Count;
        for (var index = 0; index < appended.Count && index < remaining; index++)
        {
            merged.Add(appended[index]);
        }
        return merged;
    }

    /// <summary>
    /// Compacts long file paths from the middle so both the root/drive and the immediate
    /// parent folder remain visible instead of being clipped by tail ellipsis.
    /// </summary>
    public static string FormatLocation(string path, int maxLength = 52)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= maxLength)
        {
            return path;
        }

        var parts = path.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 3)
        {
            return path;
        }

        var tail = string.Join('\\', parts[^2], parts[^1]);
        var head = parts[0] + '\\' + parts[1];
        var candidate = head + @"\…\" + tail;
        if (candidate.Length <= maxLength)
        {
            return candidate;
        }

        var shortCandidate = parts[0] + @"\…\" + tail;
        return shortCandidate.Length <= path.Length ? shortCandidate : path;
    }

    private static IReadOnlyList<TopBarSearchCandidate> SearchCore(
        IEnumerable<TopBarSearchCandidate> candidates,
        string query,
        bool allowFuzzy,
        int limit = MaxResults)
    {
        var tokens = query.Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim().ToLowerInvariant())
            .Where(token => token.Length > 0)
            .ToArray();
        if (tokens.Length == 0)
        {
            return [];
        }

        // The fallback may need a second pass, including for a one-shot enumerable.
        var source = candidates as IReadOnlyList<TopBarSearchCandidate> ?? candidates.ToArray();
        var scored = new List<(int Score, TopBarSearchCandidate Candidate)>();
        var maxPasses = allowFuzzy ? 2 : 1;
        for (var pass = 0; pass < maxPasses; pass++)
        {
            foreach (var candidate in source)
            {
                var keys = GetCandidateKeys(candidate);
                var total = 0;
                foreach (var token in tokens)
                {
                    var score = TokenScore(keys, token);
                    if (score == 0 && pass == 1)
                    {
                        score = FuzzyScore(keys.Name, token);
                    }
                    if (score == 0)
                    {
                        total = 0;
                        break;
                    }
                    total += score;
                }
                if (total > 0)
                {
                    if (candidate.Kind == TopBarSearchItemKind.App)
                    {
                        total += 2;
                    }
                    scored.Add((total, candidate));
                }
            }
            if (scored.Count > 0 || !tokens.Any(token => token.Length >= 4))
            {
                break;
            }
        }

        return scored
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Candidate.Kind)
            .ThenBy(entry => entry.Candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(entry => entry.Candidate)
            .ToArray();
    }

    private static CandidateKeys GetCandidateKeys(TopBarSearchCandidate candidate)
    {
        var cached = KeyCache.GetValue(
            candidate,
            static item => new CachedCandidateKeys(
                item.DisplayName.ToLowerInvariant(),
                item.FullFileName.ToLowerInvariant(),
                PinyinInitials(item.DisplayName)));
        return new CandidateKeys(cached.Name, cached.FileName, cached.Initials);
    }

    private static int TokenScore(CandidateKeys keys, string token)
    {
        // The full file name carries the extension, so its match always scores one
        // step below the same hit on the display name ("微信" over "微信.lnk").
        var nameScore = Quality(keys.Name, token, 0);
        var score = Math.Max(
            nameScore,
            Math.Max(Quality(keys.Initials, token, 0), Quality(keys.FileName, token, -1)));
        if (nameScore > 0 && score < ScoreWordExact)
        {
            foreach (var range in keys.Name.AsSpan().SplitAny(WordSeparators))
            {
                if (keys.Name.AsSpan(range).SequenceEqual(token))
                {
                    return ScoreWordExact;
                }
            }
        }
        return score;
    }

    private static int Quality(string key, string token, int offset) => key.Length == 0
        ? 0
        : key.Equals(token, StringComparison.Ordinal)
            ? ScoreExact + offset
            : key.StartsWith(token, StringComparison.Ordinal)
                ? ScorePrefix + offset
                : key.Contains(token, StringComparison.Ordinal)
                    ? ScoreContains + offset
                    : 0;

    private static int FuzzyScore(string name, string token)
    {
        // The distance budget grows with the token so one wrong letter in a short
        // word cannot flood the results with guesses.
        var maxDistance = token.Length switch { >= 7 => 2, >= 4 => 1, _ => 0 };
        if (maxDistance == 0)
        {
            return 0;
        }

        foreach (var range in name.AsSpan().SplitAny(WordSeparators))
        {
            var word = name.AsSpan(range);
            if (word.Length >= token.Length - maxDistance && MatchesTypoPrefix(token, word, maxDistance))
            {
                return ScoreFuzzy;
            }
        }
        return 0;
    }

    private static bool MatchesTypoPrefix(ReadOnlySpan<char> token, ReadOnlySpan<char> word, int maxDistance)
    {
        // Only the bounded word head participates. This quadratic DP is for short
        // filename tokens after an empty regular search; use a banded DP if candidate
        // sources expand to long text. Typical tokens need no heap allocation.
        var windowLength = Math.Min(word.Length, token.Length + maxDistance);
        var width = windowLength + 1;
        Span<int> buffer = width <= 128 ? stackalloc int[width * 3] : new int[width * 3];
        var previousPrevious = buffer[..width];
        var previous = buffer.Slice(width, width);
        var current = buffer.Slice(width * 2, width);
        for (var column = 0; column <= windowLength; column++)
        {
            previous[column] = column;
        }
        for (var row = 1; row <= token.Length; row++)
        {
            current[0] = row;
            var rowMinimum = row;
            for (var column = 1; column <= windowLength; column++)
            {
                var cost = token[row - 1] == word[column - 1] ? 0 : 1;
                var distance = Math.Min(
                    Math.Min(previous[column] + 1, current[column - 1] + 1),
                    previous[column - 1] + cost);
                if (row > 1 && column > 1
                    && token[row - 1] == word[column - 2]
                    && token[row - 2] == word[column - 1])
                {
                    distance = Math.Min(distance, previousPrevious[column - 2] + 1);
                }
                current[column] = distance;
                rowMinimum = Math.Min(rowMinimum, distance);
            }
            if (rowMinimum > maxDistance)
            {
                return false;
            }
            var spare = previousPrevious;
            previousPrevious = previous;
            previous = current;
            current = spare;
        }
        // The best prefix may be shorter or longer than the query after a missing
        // or extra character; the rest of the word does not consume typo budget.
        for (var column = token.Length - maxDistance; column <= windowLength; column++)
        {
            if (previous[column] <= maxDistance)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The pinyin initial letters of the text's Chinese characters, with ASCII letters
    /// and digits kept as-is ("QQ音乐" -> "qqyy"). Everything else is skipped, so the
    /// initials read as one contiguous string that prefix and substring queries match.
    /// </summary>
    public static string PinyinInitials(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character >= PinyinInitialsTable.FirstCodePoint
                && character <= PinyinInitialsTable.LastCodePoint)
            {
                var letter = PinyinInitialsTable.Letters[
                    character - PinyinInitialsTable.FirstCodePoint];
                if (letter != '-')
                {
                    builder.Append(letter);
                }
            }
            else if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// Parses the persisted hotkey text ("Win+Shift+F"). At least one modifier and
    /// exactly one plain key (letter, digit, F1–F24 or Space) is required — a bare key
    /// would swallow normal typing everywhere. Anything else reads as null.
    /// </summary>
    public static TopBarHotkeyBinding? ParseHotkey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        uint modifiers = 0;
        var hasKey = false;
        var virtualKey = 0;
        foreach (var rawToken in text.Split('+'))
        {
            var token = rawToken.Trim();
            if (token.Length == 0)
            {
                continue;
            }
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModifierControl;
                    continue;
                case "alt":
                    modifiers |= ModifierAlt;
                    continue;
                case "shift":
                    modifiers |= ModifierShift;
                    continue;
                case "win" or "windows":
                    modifiers |= ModifierWindows;
                    continue;
            }

            if (hasKey || VirtualKeyFromKeyName(token) is not { } key)
            {
                return null;
            }
            hasKey = true;
            virtualKey = key;
        }

        return modifiers != 0 && hasKey ? new TopBarHotkeyBinding(modifiers, virtualKey) : null;
    }

    public static bool IsValidHotkey(string? text) => ParseHotkey(text) is not null;

    /// <summary>The canonical text form: modifiers in a fixed order, then the key.</summary>
    public static string FormatHotkey(TopBarHotkeyBinding binding)
    {
        var parts = new List<string>(5);
        if ((binding.Modifiers & ModifierControl) != 0)
        {
            parts.Add("Ctrl");
        }
        if ((binding.Modifiers & ModifierAlt) != 0)
        {
            parts.Add("Alt");
        }
        if ((binding.Modifiers & ModifierShift) != 0)
        {
            parts.Add("Shift");
        }
        if ((binding.Modifiers & ModifierWindows) != 0)
        {
            parts.Add("Win");
        }
        parts.Add(KeyNameFromVirtualKey(binding.VirtualKey) ?? "??");
        return string.Join("+", parts);
    }

    /// <summary>Letters, digits, F1–F24 and Space map straight to their virtual keys.</summary>
    public static int? VirtualKeyFromKeyName(string token) => token.ToUpperInvariant() switch
    {
        var letter when letter.Length == 1 && letter[0] is >= 'A' and <= 'Z' => letter[0],
        var digit when digit.Length == 1 && digit[0] is >= '0' and <= '9' => digit[0],
        "SPACE" or "SPACEBAR" => 0x20,
        var function when function.Length > 1
            && function[0] == 'F'
            && int.TryParse(function[1..], out var number)
            && number is >= 1 and <= 24 => 0x70 + number - 1,
        _ => null
    };

    private static string? KeyNameFromVirtualKey(int virtualKey) => virtualKey switch
    {
        0x20 => "Space",
        >= 0x70 and <= 0x87 => "F" + (virtualKey - 0x70 + 1),
        >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        _ => null
    };
}
