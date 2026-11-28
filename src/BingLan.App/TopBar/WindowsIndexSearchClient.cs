using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BingLan.Core.Services;
using BingLan.Core.TopBar;

namespace BingLan.App.TopBar;

internal sealed record WindowsIndexSearchResult(bool Available, IReadOnlyList<TopBarSearchCandidate> Candidates)
{
    internal static WindowsIndexSearchResult Unavailable { get; } = new(false, []);
}

/// <summary>Reads the existing Windows Search catalog without changing its scope or building an index.</summary>
internal static class WindowsIndexSearchClient
{
    internal const int MaximumCandidates = 128;
    // A slow provider may outlive the UI wait. Keep at most one native query alive;
    // a separate search service is warranted only if measured demand exceeds this limit.
    private static readonly SemaphoreSlim QueryGate = new(1, 1);
    private static readonly Lazy<Type?> ConnectionType = new(() => Type.GetTypeFromProgID("ADODB.Connection"));
    private static readonly Lazy<Type?> RecordsetType = new(() => Type.GetTypeFromProgID("ADODB.Recordset"));

    internal static Task<WindowsIndexSearchResult> SearchAsync(string query, CancellationToken cancellationToken) =>
        SearchAsync(query, cancellationToken, ReadIndex);

    internal static async Task<WindowsIndexSearchResult> SearchAsync(
        string query, CancellationToken cancellationToken, Func<string, CancellationToken, WindowsIndexSearchResult> readIndex)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query) || query.Length > 256)
        {
            return WindowsIndexSearchResult.Unavailable;
        }
        if (!await QueryGate.WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken))
        {
            return WindowsIndexSearchResult.Unavailable;
        }
        var work = Task.Run(() => readIndex(query, cancellationToken), cancellationToken);
        _ = work.ContinueWith(static completed =>
        {
            // The caller can stop waiting before the provider returns.
            _ = completed.Exception;
            QueryGate.Release();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            return await work.WaitAsync(TimeSpan.FromMilliseconds(900), cancellationToken);
        }
        catch (TimeoutException)
        {
            return WindowsIndexSearchResult.Unavailable;
        }
    }

    internal static string CreateQuery(string query, bool useWordPrefix = true)
    {
        var conditions = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(16)
            .Select(token => "System.FileName LIKE '%" + token.Replace("'", "''")
                .Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%'");
        var word = Regex.Match(query, @"[\p{L}\p{Nd}]{2,}").Value;
        var prefix = useWordPrefix && word.Length > 0 ? $"CONTAINS(System.FileName, '\"{word}*\"') AND " : "";
        return $"SELECT TOP {MaximumCandidates} System.ItemUrl, System.FileAttributes FROM SystemIndex WHERE scope='file:' AND " + prefix
            + string.Join(" AND ", conditions);
    }

    /// <summary>
    /// Short ASCII queries (such as 2–3 letter pinyin initials) are already covered by
    /// the indexed word-prefix query; falling back to an unindexed <c>LIKE '%xx%'</c>
    /// scan would sweep the entire catalog without matching Chinese filenames anyway.
    /// </summary>
    internal static bool ShouldFallbackToSubstringScan(string query)
    {
        var match = Regex.Match(query, @"[\p{L}\p{Nd}]{2,}");
        if (!match.Success)
        {
            return false;
        }
        foreach (var ch in query)
        {
            if (!char.IsAscii(ch))
            {
                return true;
            }
        }
        return match.Value.Length >= 4;
    }

    private static WindowsIndexSearchResult ReadIndex(string query, CancellationToken cancellationToken)
    {
        object? connectionObject = null;
        object? rowsObject = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connectionType = ConnectionType.Value;
            var rowsType = RecordsetType.Value;
            if (connectionType is null || rowsType is null)
            {
                return WindowsIndexSearchResult.Unavailable;
            }
            connectionObject = Activator.CreateInstance(connectionType)!;
            rowsObject = Activator.CreateInstance(rowsType)!;
            dynamic connection = connectionObject;
            dynamic rows = rowsObject;
            connection.ConnectionTimeout = 1;
            connection.CommandTimeout = 1;
            connection.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            cancellationToken.ThrowIfCancellationRequested();
            // Forward-only, read-only SQL over indexed properties; never query file contents.
            rows.Open(CreateQuery(query), connection, 0, 1, 1);
            cancellationToken.ThrowIfCancellationRequested();
            // A filename word-prefix narrows the property-store scan. If it finds
            // nothing, try bounded substring matching only when mid-word input is plausible.
            if (rows.EOF && ShouldFallbackToSubstringScan(query))
            {
                rows.Close();
                rows.Open(CreateQuery(query, useWordPrefix: false), connection, 0, 1, 1);
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (rows.EOF)
            {
                return new WindowsIndexSearchResult(true, []);
            }
            return new WindowsIndexSearchResult(true, ParseRows((object[,])rows.GetRows(MaximumCandidates)));
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException
            or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return WindowsIndexSearchResult.Unavailable;
        }
        finally
        {
            CloseAndRelease(rowsObject);
            CloseAndRelease(connectionObject);
        }
    }

    internal static IReadOnlyList<TopBarSearchCandidate> ParseRows(object[,] rows)
    {
        var candidates = new List<TopBarSearchCandidate>();
        for (var index = 0; index < Math.Min(rows.GetLength(1), MaximumCandidates); index++)
        {
            // Display paths can contain translated folder names that do not exist
            // on disk. The indexed file URL preserves the actual target for opening.
            if (rows[0, index] is not string url || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile) continue;
            var path = uri.LocalPath;
            if (!Path.IsPathFullyQualified(path)) continue;
            var attributes = rows[1, index] is null or DBNull ? 0L : Convert.ToInt64(rows[1, index]);
            candidates.Add(new TopBarSearchCandidate(
                (attributes & (long)FileAttributes.Directory) != 0 ? TopBarSearchItemKind.Folder : TopBarSearchItemKind.File,
                FileMappingService.GetDisplayName(path), Path.GetFileName(path), path));
        }
        return candidates;
    }

    private static void CloseAndRelease(object? value)
    {
        if (value is null) return;
        try
        {
            dynamic resource = value;
            if (resource.State != 0) resource.Close();
        }
        catch (COMException) { }
        finally { Marshal.FinalReleaseComObject(value); }
    }
}
