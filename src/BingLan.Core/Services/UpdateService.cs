using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
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

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateRelease? Release,
    string? ErrorMessage,
    DateTimeOffset? RetryAfterUtc = null);

public sealed class UpdateDownloadException(string message) : Exception(message);

// GitHub Releases 客户端：只查询最新版本信息，不发送任何个人数据。
public sealed class UpdateService
{
    public const string LatestReleaseEndpoint = "https://api.github.com/repos/keros68/binglan/releases/latest";
    public const string LatestReleasePageEndpoint = "https://github.com/keros68/binglan/releases/latest";
    public const string ReleaseByTagEndpointPrefix = "https://api.github.com/repos/keros68/binglan/releases/tags/";
    public const string ReleaseTagPagePrefix = "https://github.com/keros68/binglan/releases/tag/";
    public const string ReleaseDownloadPrefix = "https://github.com/keros68/binglan/releases/download/";
    public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaxRateLimitCooldown = TimeSpan.FromHours(2);
    public const long MaximumInstallerBytes = 200L * 1024 * 1024;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private const string InstallerPrefix = "BingLan-Setup-";
    private const string ChecksumsFileName = "SHA256SUMS.txt";
    private const int MaxChecksumsBytes = 64 * 1024;
    private const int MaxErrorBodyBytes = 4 * 1024;
    private const string FallbackReleaseNotes = "当前无法从 GitHub 接口获取更新说明，可前往发布页查看完整更新内容。";
    private static readonly string[] TrustedDownloadHosts =
    [
        "github.com",
        "release-assets.githubusercontent.com",
        "objects.githubusercontent.com",
        "github-releases.githubusercontent.com"
    ];

    private readonly HttpClient _httpClient;
    private readonly HttpClient _noRedirectClient;
    private readonly TimeProvider _time;

    public UpdateService(Version currentVersion, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
        : this(currentVersion, handler, noRedirectHandler: handler, timeout, timeProvider: null)
    {
    }

    public static UpdateService Create(
        Version currentVersion,
        HttpMessageHandler? handler = null,
        HttpMessageHandler? noRedirectHandler = null,
        TimeSpan? timeout = null,
        TimeProvider? timeProvider = null) =>
        new(currentVersion, handler, noRedirectHandler, timeout, timeProvider);

    private UpdateService(
        Version currentVersion,
        HttpMessageHandler? handler,
        HttpMessageHandler? noRedirectHandler,
        TimeSpan? timeout,
        TimeProvider? timeProvider)
    {
        CurrentVersion = Normalize(currentVersion);
        CheckTimeout = timeout ?? DefaultTimeout;
        _time = timeProvider ?? TimeProvider.System;
        var userAgent = new ProductInfoHeaderValue("BingLan", CurrentVersion.ToString(3));

        _httpClient = CreateClient(
            handler ?? new SocketsHttpHandler(),
            disposeHandler: handler is null,
            userAgent);
        _noRedirectClient = CreateClient(
            noRedirectHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false },
            disposeHandler: noRedirectHandler is null,
            userAgent);
    }

    public Version CurrentVersion { get; }

    public TimeSpan CheckTimeout { get; }

    // 时钟被调回时 last 会晚于 now，此时也视为到期，避免长时间不再检查。
    public static bool IsAutoCheckDue(DateTimeOffset? lastCheckedAt, DateTimeOffset now) =>
        IsAutoCheckDue(lastCheckedAt, now, retryAfterUtc: null);

    public static bool IsAutoCheckDue(DateTimeOffset? lastCheckedAt, DateTimeOffset now, DateTimeOffset? retryAfterUtc) =>
        IsRetryAllowed(retryAfterUtc, now)
        && (lastCheckedAt is not { } last || last > now || now - last >= AutoCheckInterval);

    // 限流冷却期未到时不重复自动重试；若系统时钟被大幅调回超过最大冷却上限则解除锁定。
    public static bool IsRetryAllowed(DateTimeOffset? retryAfterUtc, DateTimeOffset now) =>
        retryAfterUtc is not { } retryAfter
        || now >= retryAfter
        || retryAfter - now > MaxRateLimitCooldown;

    public static string FormatRateLimitMessage(DateTimeOffset? retryAfterUtc, TimeZoneInfo? localTimeZone = null)
    {
        if (retryAfterUtc is { } resetUtc)
        {
            var local = TimeZoneInfo.ConvertTime(resetUtc, localTimeZone ?? TimeZoneInfo.Local);
            return $"GitHub 访问受限（当前出口 IP 请求过多），请于 {local.ToString("HH:mm", CultureInfo.InvariantCulture)} 后重试或前往发布页查看";
        }
        return "GitHub 访问受限（当前出口 IP 请求过多），请稍后重试或前往发布页查看";
    }

    public static bool IsTrustedDownloadUri(Uri? uri) =>
        uri is not null
        && uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && TrustedDownloadHosts.Any(host => string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase));

    public static bool TryParseLatestRedirectLocation(
        Uri? location,
        Uri requestUri,
        out string tag,
        out Version version,
        out Uri pageUrl)
    {
        tag = string.Empty;
        version = new Version(0, 0, 0);
        pageUrl = requestUri;
        if (location is null)
        {
            return false;
        }

        var resolved = location.IsAbsoluteUri ? location : new Uri(requestUri, location);
        if (resolved.Scheme != Uri.UriSchemeHttps
            || !resolved.IsDefaultPort
            || !string.Equals(resolved.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(resolved.Query)
            || !string.IsNullOrEmpty(resolved.Fragment))
        {
            return false;
        }

        const string tagPathPrefix = "/keros68/binglan/releases/tag/";
        if (!resolved.AbsolutePath.StartsWith(tagPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rawTag = resolved.AbsolutePath[tagPathPrefix.Length..].TrimEnd('/');
        if (string.IsNullOrEmpty(rawTag)
            || rawTag.Any(char.IsWhiteSpace)
            || rawTag.Contains('/')
            || rawTag.Contains('\\')
            || rawTag.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (ParseVersion(rawTag) is not { } parsedVersion)
        {
            return false;
        }

        tag = rawTag;
        version = parsedVersion;
        pageUrl = new Uri($"{ReleaseTagPagePrefix}{Uri.EscapeDataString(tag)}");
        return true;
    }

    public static bool TryParseSha256FromChecksums(string content, string expectedFileName, out string sha256)
    {
        sha256 = string.Empty;
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(expectedFileName))
        {
            return false;
        }

        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim().TrimStart('\uFEFF');
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = trimmed.IndexOfAny([' ', '\t']);
            if (separatorIndex != 64)
            {
                continue;
            }

            var candidateHash = trimmed[..64];
            if (!candidateHash.All(Uri.IsHexDigit))
            {
                continue;
            }

            var filePart = trimmed[64..].TrimStart(' ', '\t');
            if (filePart.StartsWith('*'))
            {
                filePart = filePart[1..];
            }

            if (string.Equals(filePart, expectedFileName, StringComparison.OrdinalIgnoreCase))
            {
                sha256 = candidateHash;
                return true;
            }
        }

        return false;
    }

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
            || !IsTrustedReleasePageUri(page))
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
            var discovery = await TryDiscoverLatestReleaseAsync(timeout.Token).ConfigureAwait(false);
            if (discovery.Failure is { } discoveryFailure)
            {
                return discoveryFailure;
            }

            if (discovery.Discovered is { } latest)
            {
                if (latest.Version <= CurrentVersion)
                {
                    return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, null);
                }

                return await ResolveNewerReleaseAsync(latest, cancellationToken, timeout.Token).ConfigureAwait(false);
            }

            return await CheckViaLatestApiAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException
            or HttpRequestException
            or IOException
            or JsonException)
        {
            return ClassifyException(exception, cancellationToken);
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
            if (!response.IsSuccessStatusCode)
            {
                var failure = await BuildFailureFromResponseAsync(response, cancellationToken).ConfigureAwait(false);
                throw new UpdateDownloadException(failure.ErrorMessage ?? "网络请求失败");
            }
            if (IsTrustedDownloadUri(asset.DownloadUrl)
                && response.RequestMessage?.RequestUri is { } finalUri
                && !IsTrustedDownloadUri(finalUri))
            {
                throw new UpdateDownloadException("下载地址不受信任");
            }
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
                HttpRequestException httpException => new UpdateDownloadException(
                    ClassifyHttpRequestException(httpException).ErrorMessage ?? "网络请求失败"),
                HttpIOException => new UpdateDownloadException("网络连接中断，请稍后重试"),
                IOException or UnauthorizedAccessException => new UpdateDownloadException("无法保存下载的文件"),
                _ => exception
            };
        }
    }

    private sealed record DiscoveredRelease(string Tag, Version Version, Uri PageUrl);

    private sealed record DiscoveryOutcome(DiscoveredRelease? Discovered, UpdateCheckResult? Failure);

    private async Task<DiscoveryOutcome> TryDiscoverLatestReleaseAsync(CancellationToken cancellationToken)
    {
        var requestUri = new Uri(LatestReleasePageEndpoint);
        using (var headRequest = new HttpRequestMessage(HttpMethod.Head, requestUri))
        using (var headResponse = await _noRedirectClient
            .SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false))
        {
            if (IsRedirectStatus(headResponse.StatusCode))
            {
                return TryParseLatestRedirectLocation(headResponse.Headers.Location, requestUri, out var tag, out var version, out var pageUrl)
                    ? new DiscoveryOutcome(new DiscoveredRelease(tag, version, pageUrl), null)
                    : new DiscoveryOutcome(null, null);
            }

            if (IsRateLimitedHeaderOrReason(headResponse))
            {
                return new DiscoveryOutcome(null, BuildRateLimitFailure(headResponse));
            }

            if (headResponse.StatusCode == HttpStatusCode.ProxyAuthenticationRequired
                || (int)headResponse.StatusCode is >= 500 and <= 599)
            {
                return new DiscoveryOutcome(
                    null,
                    BuildFailureFromStatusCode(headResponse.StatusCode, retryAfterUtc: null, isRateLimited: false));
            }
        }

        // 部分代理或网关不支持 HEAD 或返回非 301/302 时，回退 GET（仅读响应头）。
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using var getResponse = await _noRedirectClient
            .SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (IsRedirectStatus(getResponse.StatusCode))
        {
            return TryParseLatestRedirectLocation(getResponse.Headers.Location, requestUri, out var tag, out var version, out var pageUrl)
                ? new DiscoveryOutcome(new DiscoveredRelease(tag, version, pageUrl), null)
                : new DiscoveryOutcome(null, null);
        }

        if (getResponse.StatusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.Forbidden
            or HttpStatusCode.ProxyAuthenticationRequired
            || (int)getResponse.StatusCode is >= 500 and <= 599)
        {
            return new DiscoveryOutcome(
                null,
                await BuildFailureFromResponseAsync(getResponse, cancellationToken).ConfigureAwait(false));
        }

        return new DiscoveryOutcome(null, null);
    }

    private async Task<UpdateCheckResult> ResolveNewerReleaseAsync(
        DiscoveredRelease discovered,
        CancellationToken callerToken,
        CancellationToken timeoutToken)
    {
        var apiRelease = await TryFetchReleaseByTagAsync(discovered.Tag, discovered.Version, callerToken, timeoutToken)
            .ConfigureAwait(false);
        if (apiRelease is not null)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Available, apiRelease, null);
        }

        var fallbackInstaller = await TryResolveFallbackInstallerAsync(
            discovered.Tag,
            discovered.Version,
            callerToken,
            timeoutToken).ConfigureAwait(false);
        var fallbackRelease = new UpdateRelease(
            discovered.Version,
            FallbackReleaseNotes,
            discovered.PageUrl,
            fallbackInstaller);
        return new UpdateCheckResult(UpdateCheckStatus.Available, fallbackRelease, null);
    }

    private async Task<UpdateRelease?> TryFetchReleaseByTagAsync(
        string tag,
        Version expectedVersion,
        CancellationToken callerToken,
        CancellationToken timeoutToken)
    {
        try
        {
            var endpoint = $"{ReleaseByTagEndpointPrefix}{Uri.EscapeDataString(tag)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(timeoutToken).ConfigureAwait(false);
            var release = ParseRelease(json);
            return release is not null && release.Version == expectedVersion ? release : null;
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException
            or HttpRequestException
            or IOException
            or JsonException)
        {
            return null;
        }
    }

    private async Task<UpdateAsset?> TryResolveFallbackInstallerAsync(
        string tag,
        Version version,
        CancellationToken callerToken,
        CancellationToken timeoutToken)
    {
        try
        {
            var escapedTag = Uri.EscapeDataString(tag);
            var installerName = $"{InstallerPrefix}{version.ToString(3)}.exe";
            var checksumsUri = new Uri($"{ReleaseDownloadPrefix}{escapedTag}/{ChecksumsFileName}");
            var downloadUri = new Uri($"{ReleaseDownloadPrefix}{escapedTag}/{installerName}");

            string sha256;
            using (var checksumsRequest = new HttpRequestMessage(HttpMethod.Get, checksumsUri))
            using (var checksumsResponse = await _httpClient
                .SendAsync(checksumsRequest, HttpCompletionOption.ResponseHeadersRead, timeoutToken)
                .ConfigureAwait(false))
            {
                if (!checksumsResponse.IsSuccessStatusCode)
                {
                    return null;
                }
                var checksumsFinalUri = checksumsResponse.RequestMessage?.RequestUri ?? checksumsUri;
                if (!IsTrustedDownloadUri(checksumsFinalUri))
                {
                    return null;
                }
                if (checksumsResponse.Content.Headers.ContentLength is > MaxChecksumsBytes)
                {
                    return null;
                }
                var checksumsText = await ReadBoundedStringAsync(
                    checksumsResponse.Content,
                    MaxChecksumsBytes,
                    timeoutToken).ConfigureAwait(false);
                if (!TryParseSha256FromChecksums(checksumsText, installerName, out sha256))
                {
                    return null;
                }
            }

            using var headRequest = new HttpRequestMessage(HttpMethod.Head, downloadUri);
            using var headResponse = await _httpClient
                .SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, timeoutToken)
                .ConfigureAwait(false);
            if (!headResponse.IsSuccessStatusCode)
            {
                return null;
            }
            var installerFinalUri = headResponse.RequestMessage?.RequestUri ?? downloadUri;
            if (!IsTrustedDownloadUri(installerFinalUri))
            {
                return null;
            }
            if (headResponse.Content.Headers.ContentLength is not { } size
                || size <= 0
                || size > MaximumInstallerBytes)
            {
                return null;
            }

            return new UpdateAsset(installerName, downloadUri, size, sha256);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException
            or HttpRequestException
            or IOException)
        {
            return null;
        }
    }

    private async Task<UpdateCheckResult> CheckViaLatestApiAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return await BuildFailureFromResponseAsync(response, cancellationToken).ConfigureAwait(false);
        }
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var release = ParseRelease(json);
        return release is not null && release.Version > CurrentVersion
            ? new UpdateCheckResult(UpdateCheckStatus.Available, release, null)
            : new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, null);
    }

    private async Task<UpdateCheckResult> BuildFailureFromResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (IsRateLimitedHeaderOrReason(response))
        {
            return BuildRateLimitFailure(response);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var bodySnippet = await ReadBoundedStringAsync(response.Content, MaxErrorBodyBytes, cancellationToken)
                .ConfigureAwait(false);
            if (bodySnippet.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            {
                return BuildRateLimitFailure(response);
            }
        }

        return BuildFailureFromStatusCode(response.StatusCode, retryAfterUtc: null, isRateLimited: false);
    }

    private UpdateCheckResult BuildRateLimitFailure(HttpResponseMessage response)
    {
        var retryAfterUtc = TryParseRetryAfterUtc(response, _time.GetUtcNow());
        return BuildFailureFromStatusCode(response.StatusCode, retryAfterUtc, isRateLimited: true);
    }

    private UpdateCheckResult BuildFailureFromStatusCode(
        HttpStatusCode statusCode,
        DateTimeOffset? retryAfterUtc,
        bool isRateLimited)
    {
        if (isRateLimited || statusCode == HttpStatusCode.TooManyRequests)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                null,
                FormatRateLimitMessage(retryAfterUtc, _time.LocalTimeZone),
                retryAfterUtc);
        }
        if (statusCode == HttpStatusCode.ProxyAuthenticationRequired)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "代理连接失败，请检查系统代理设置");
        }
        var code = (int)statusCode;
        if (code is >= 500 and <= 599)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, $"GitHub 服务暂时不可用（HTTP {code}），请稍后重试");
        }
        if (statusCode == HttpStatusCode.Forbidden)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "GitHub 拒绝访问（HTTP 403），请检查网络或代理设置");
        }
        return new UpdateCheckResult(UpdateCheckStatus.Failed, null, $"更新服务返回异常（HTTP {code}）");
    }

    private UpdateCheckResult ClassifyException(Exception exception, CancellationToken callerToken) =>
        exception switch
        {
            OperationCanceledException => new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                null,
                callerToken.IsCancellationRequested ? "请求已取消" : "请求超时"),
            JsonException => new UpdateCheckResult(UpdateCheckStatus.Failed, null, "返回数据无法解析"),
            HttpRequestException httpException => ClassifyHttpRequestException(httpException),
            IOException => new UpdateCheckResult(UpdateCheckStatus.Failed, null, "网络连接中断，请稍后重试"),
            _ => new UpdateCheckResult(UpdateCheckStatus.Failed, null, "网络请求失败")
        };

    private UpdateCheckResult ClassifyHttpRequestException(HttpRequestException exception)
    {
        if (exception.HttpRequestError == HttpRequestError.ProxyTunnelError
            || exception.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "代理连接失败，请检查系统代理设置");
        }
        if (exception.HttpRequestError == HttpRequestError.NameResolutionError)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "无法解析 GitHub 域名，请检查网络或 DNS 设置");
        }
        if (exception.HttpRequestError == HttpRequestError.SecureConnectionError
            || HasInnerException<AuthenticationException>(exception))
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "安全连接（TLS）失败，请检查系统时间或代理证书");
        }
        if (exception.StatusCode is { } statusCode)
        {
            return BuildFailureFromStatusCode(
                statusCode,
                retryAfterUtc: null,
                isRateLimited: statusCode == HttpStatusCode.TooManyRequests);
        }
        return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "网络请求失败");
    }

    private static HttpClient CreateClient(
        HttpMessageHandler handler,
        bool disposeHandler,
        ProductInfoHeaderValue userAgent)
    {
        var client = new HttpClient(handler, disposeHandler)
        {
            // Downloads are bounded by the token passed to DownloadAsync instead.
            Timeout = Timeout.InfiniteTimeSpan
        };
        // GitHub's API refuses requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(userAgent);
        return client;
    }

    private static bool IsRedirectStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool IsRateLimitedHeaderOrReason(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return true;
        }
        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return false;
        }
        if (response.Headers.RetryAfter is not null)
        {
            return true;
        }
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && remaining.Any(value => string.Equals(value.Trim(), "0", StringComparison.Ordinal)))
        {
            return true;
        }
        return response.ReasonPhrase?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static DateTimeOffset? TryParseRetryAfterUtc(HttpResponseMessage response, DateTimeOffset nowUtc)
    {
        DateTimeOffset? candidate = null;
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
            {
                candidate = nowUtc + delta;
            }
            else if (retryAfter.Date is { } retryDate)
            {
                candidate = retryDate.ToUniversalTime();
            }
        }

        if (candidate is null
            && response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues))
        {
            foreach (var raw in resetValues)
            {
                if (long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds)
                    && unixSeconds > 0)
                {
                    candidate = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                    break;
                }
            }
        }

        if (candidate is not { } parsedUtc)
        {
            return null;
        }

        var maxAllowed = nowUtc + MaxRateLimitCooldown;
        if (parsedUtc > maxAllowed)
        {
            return maxAllowed;
        }
        return parsedUtc <= nowUtc ? nowUtc + TimeSpan.FromMinutes(1) : parsedUtc;
    }

    private static async Task<string> ReadBoundedStringAsync(
        HttpContent? content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (content is null || maxBytes <= 0)
        {
            return string.Empty;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[maxBytes];
        var total = 0;
        while (total < maxBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, maxBytes - total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static bool HasInnerException<TException>(Exception exception) where TException : Exception
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is TException)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsTrustedReleasePageUri(Uri? uri) =>
        uri is not null
        && uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/keros68/binglan/releases/", StringComparison.OrdinalIgnoreCase);

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
            || !IsTrustedDownloadUri(url)
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
