using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace BingLan.Core.Services;

/// <summary>The installer attached to a release, with the checksum GitHub publishes for it.</summary>
public sealed record UpdateAsset(string Name, Uri DownloadUrl, long Size, string Sha256);

/// <summary>
/// A published release. <see cref="Installer"/> is null when the release has no installer
/// that can be verified; it can then only be opened on its release page.
/// </summary>
public sealed record UpdateRelease(Version Version, string Notes, Uri PageUrl, UpdateAsset? Installer);

public enum UpdateCheckStatus
{
    UpToDate,
    Available,
    Failed
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateRelease? Release, string? ErrorMessage);

public sealed class UpdateDownloadException(string message) : Exception(message);

// GitHub Releases 客户端：只查询最新版本信息，不发送任何个人数据。
public sealed class UpdateService
{
    public const string LatestReleaseEndpoint = "https://api.github.com/repos/keros68/binglan/releases/latest";
    public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(24);
    public const long MaximumInstallerBytes = 200L * 1024 * 1024;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private const string InstallerPrefix = "BingLan-Setup-";

    private readonly HttpClient _httpClient;

    public UpdateService(Version currentVersion, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        CurrentVersion = Normalize(currentVersion);
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        // Downloads are bounded by the token passed to DownloadAsync instead.
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        CheckTimeout = timeout ?? DefaultTimeout;
        // GitHub's API refuses requests without a User-Agent.
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("BingLan", CurrentVersion.ToString(3)));
    }

    public Version CurrentVersion { get; }

    public TimeSpan CheckTimeout { get; }

    // 时钟被调回时 last 会晚于 now，此时也视为到期，避免长时间不再检查。
    public static bool IsAutoCheckDue(DateTimeOffset? lastCheckedAt, DateTimeOffset now) =>
        lastCheckedAt is not { } last || last > now || now - last >= AutoCheckInterval;

    public static Version? ParseVersion(string? tag)
    {
        var text = tag?.Trim() ?? string.Empty;
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }
        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    public static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build));

    /// <summary>
    /// Formats the persistent update badge label for the settings sidebar, or returns null
    /// when no newer release is available.
    /// </summary>
    public static string? FormatAvailableBadgeText(UpdateRelease? available) =>
        available is null ? null : $"有新版本 {Normalize(available.Version).ToString(3)}";

    public static UpdateRelease? ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (ReadBool(root, "draft") || ReadBool(root, "prerelease")
            || ParseVersion(ReadString(root, "tag_name")) is not { } version
            || !Uri.TryCreate(ReadString(root, "html_url"), UriKind.Absolute, out var page)
            || page.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        UpdateAsset? installer = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            installer = assets.EnumerateArray()
                .Select(ParseInstaller)
                .FirstOrDefault(asset => asset is not null);
        }

        return new UpdateRelease(version, ReadString(root, "body")?.Trim() ?? string.Empty, page, installer);
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var release = ParseRelease(json);
            return release is not null && release.Version > CurrentVersion
                ? new UpdateCheckResult(UpdateCheckStatus.Available, release, null)
                : new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, null);
        }
        catch (Exception exception) when (exception is OperationCanceledException
            or HttpRequestException
            or JsonException)
        {
            var message = exception switch
            {
                OperationCanceledException => "请求超时",
                JsonException => "返回数据无法解析",
                _ => "网络请求失败"
            };
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, message);
        }
    }

    /// <summary>
    /// Downloads the installer into <paramref name="directory"/> and returns its path once
    /// its size and SHA-256 match what the release lists; otherwise the file is deleted
    /// and an <see cref="UpdateDownloadException"/> says why.
    /// </summary>
    public async Task<string> DownloadAsync(
        UpdateAsset asset,
        string directory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Path.GetFileName(asset.Name));
        try
        {
            using var response = await _httpClient
                .GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = File.Create(path))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > asset.Size)
                    {
                        throw new UpdateDownloadException("下载的文件大小与发布信息不符");
                    }
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report((double)total / asset.Size);
                }
                if (total != asset.Size)
                {
                    throw new UpdateDownloadException("下载的文件大小与发布信息不符");
                }
                var actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateDownloadException("下载的文件校验失败");
                }
            }
            return path;
        }
        catch (Exception exception)
        {
            TryDelete(path);
            throw exception switch
            {
                UpdateDownloadException => exception,
                OperationCanceledException => new UpdateDownloadException("下载已取消或超时"),
                HttpRequestException => new UpdateDownloadException("网络请求失败"),
                IOException or UnauthorizedAccessException => new UpdateDownloadException("无法保存下载的文件"),
                _ => exception
            };
        }
    }

    private static UpdateAsset? ParseInstaller(JsonElement asset)
    {
        var name = ReadString(asset, "name");
        var digest = ReadString(asset, "digest");
        if (name is null
            || !name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(name) != name
            || digest is null
            || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(ReadString(asset, "browser_download_url"), UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps
            || !asset.TryGetProperty("size", out var sizeElement)
            || !sizeElement.TryGetInt64(out var size)
            || size <= 0
            || size > MaximumInstallerBytes)
        {
            return null;
        }

        var sha256 = digest["sha256:".Length..];
        return sha256.Length == 64 && sha256.All(Uri.IsHexDigit)
            ? new UpdateAsset(name, url, size, sha256)
            : null;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
