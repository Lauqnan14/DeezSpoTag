using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using IOFile = System.IO.File;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Metadata.Qobuz;
using DeezSpoTag.Services.Download.Shared.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Services.Download.Qobuz;

public interface IQobuzDownloadService
{
    Task<bool> HasPublicDownloadSessionAsync(CancellationToken cancellationToken);
    Task<string?> BeginPublicDownloadVerificationAsync(CancellationToken cancellationToken);
    Task CompletePublicDownloadVerificationAsync(string grant, CancellationToken cancellationToken);
    Task<string> DownloadByUrlAsync(QobuzDownloadRequest request, CancellationToken cancellationToken);
}

public sealed class QobuzDownloadService : IQobuzDownloadService
{
    private const string ZarzBaseUrl = "https://api.zarz.moe/v2";
    private const string ZarzDownloadPath = "/dl/qbz";
    private const string ZarzTicketPath = "/tickets";
    private const string ZarzBootstrapPath = "/bootstrap";
    private const string ZarzChallengePath = "/challenge";
    private const string ZarzExchangePath = "/session/exchange";
    private const string ZarzAppVersion = "qobuz-web@1.1.0";
    private const string ZarzPlatform = "extension";
    private const string ZarzExtensionState = "qobuz-web";
    private const string ZarzCallbackScheme = "spotiflac://session-grant";
    private const string ZarzScheme = "ZARZ-HMAC-V1";
    private const int ZarzTimeWindowSeconds = 300;
    private const string ZarzSessionFileName = "zarz-signed-session.json";
    private const string ApplicationJsonContentType = "application/json";
    private const string DownloadUrlUnavailableMessage = "Qobuz download URL not available";
    private const string FlacExtension = ".flac";
    private const string DefaultAppId = "712109809";
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/144.0.0.0 Safari/537.36";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ProviderRequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ProviderCooldown = TimeSpan.FromMinutes(10);
    private const int MaxConcurrentProviderResolutions = 2;
    private static readonly ConcurrentDictionary<string, DateTimeOffset> ProviderBackoffUntil = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim ProviderResolutionGate = new(MaxConcurrentProviderResolutions, MaxConcurrentProviderResolutions);
    private static readonly string[] ProviderUrlPropertyNames = ["url", "download_url", "link"];
    private static readonly (int Start, int End)[] ExtendedLatinRanges =
    {
        (0x0100, 0x024F),
        (0x1E00, 0x1EFF),
        (0x00C0, 0x00FF)
    };
    private static readonly (int Start, int End)[] NonLatinScriptRanges =
    {
        (0x4E00, 0x9FFF),
        (0x3040, 0x309F),
        (0x30A0, 0x30FF),
        (0xAC00, 0xD7AF),
        (0x0600, 0x06FF),
        (0x0400, 0x04FF)
    };
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<QobuzDownloadService> _logger;
    private readonly HttpClient _apiClient;
    private readonly HttpClient _downloadClient;
    private readonly QobuzApiConfig _qobuzConfig;
    private readonly IQobuzCredentialProvider _credentialProvider;
    private readonly IQobuzPublicProviderRegistry _publicProviderRegistry;
    private readonly string _zarzSessionPath;
    private readonly SemaphoreSlim _zarzSessionGate = new(1, 1);

    public QobuzDownloadService(
        ILogger<QobuzDownloadService> logger,
        IOptions<QobuzApiConfig> qobuzOptions,
        IQobuzCredentialProvider credentialProvider,
        IQobuzPublicProviderRegistry? publicProviderRegistry = null)
    {
        _logger = logger;
        _qobuzConfig = qobuzOptions.Value ?? new QobuzApiConfig();
        _credentialProvider = credentialProvider;
        _publicProviderRegistry = publicProviderRegistry
            ?? throw new ArgumentNullException(nameof(publicProviderRegistry));
        _apiClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        _apiClient.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        _apiClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(ApplicationJsonContentType));
        _downloadClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        _downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        _zarzSessionPath = Path.Join(DeezSpoTagDataRootResolver.Resolve(), "qobuz", ZarzSessionFileName);
    }

    public async Task<string> DownloadByUrlAsync(QobuzDownloadRequest request, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.OutputDir);

        var sourceUrl = request.TrackUrl ?? request.ServiceUrl;
        var trackId = TryExtractTrackId(sourceUrl);
        if (trackId == null || trackId <= 0)
        {
            throw new InvalidOperationException("Qobuz download requires a valid track URL.");
        }

        var expectedPath = BuildSanitizedOutputPath(request, FlacExtension);
        var outputPath = expectedPath;
        await DownloadTrackWithProviderFallbackAsync(trackId.Value, request, outputPath, cancellationToken);
        return outputPath;
    }

    private async Task NotifySelectedQualityAsync(QobuzDownloadRequest request, string selectedQuality)
    {
        if (string.IsNullOrWhiteSpace(selectedQuality) || request.SelectedQualityCallback == null)
        {
            return;
        }

        try
        {
            await request.SelectedQualityCallback(selectedQuality);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Qobuz selected-quality callback failed for quality {Quality}", selectedQuality);
        }
    }

    private static long? TryExtractTrackId(string trackUrl)
    {
        if (string.IsNullOrWhiteSpace(trackUrl))
        {
            return null;
        }

        if (Uri.TryCreate(trackUrl, UriKind.Absolute, out var parsed))
        {
            var host = parsed.Host ?? string.Empty;
            if (host.Contains("qobuz.com", StringComparison.OrdinalIgnoreCase))
            {
                // Accept both locale and non-locale forms:
                // - https://www.qobuz.com/us-en/album/.../track/<id>
                // - https://open.qobuz.com/track/<id>
                // - https://play.qobuz.com/track/<id>
                var pathMatch = Regex.Match(
                    parsed.AbsolutePath,
                    @"(?:^|/)track/(?<id>\d+)(?:/|$)",
                    RegexOptions.IgnoreCase,
                    RegexTimeout);
                if (pathMatch.Success && long.TryParse(pathMatch.Groups["id"].Value, out var pathTrackId))
                {
                    return pathTrackId;
                }
            }
        }

        var match = Regex.Match(
            trackUrl,
            @"(?:qobuz\.com\/.*\/track\/|play\.qobuz\.com\/track\/|open\.qobuz\.com\/track\/)(?<id>\d+)",
            RegexOptions.IgnoreCase,
            RegexTimeout);
        if (!match.Success || !long.TryParse(match.Groups["id"].Value, out var trackId))
        {
            return null;
        }

        return trackId;
    }

    private static string BuildSanitizedOutputPath(QobuzDownloadRequest request, string extension)
    {
        var outputPathContext = new AudioFilePathHelper.AudioPathContext
        {
            OutputDir = request.OutputDir,
            Title = DownloadFileUtilities.SanitizeFilename(request.TrackName),
            Artist = DownloadFileUtilities.SanitizeFilename(request.ArtistName),
            Album = DownloadFileUtilities.SanitizeFilename(request.AlbumName),
            AlbumArtist = DownloadFileUtilities.SanitizeFilename(request.AlbumArtist),
            ReleaseDate = request.ReleaseDate,
            TrackNumber = request.SpotifyTrackNumber,
            DiscNumber = request.SpotifyDiscNumber,
            FilenameFormat = request.FilenameFormat,
            IncludeTrackNumber = request.IncludeTrackNumber,
            Position = request.Position,
            UseAlbumTrackNumber = request.UseAlbumTrackNumber,
            Sanitize = static value => value
        };
        return AudioFilePathHelper.BuildOutputPath(outputPathContext, extension);
    }

    private async Task DownloadTrackWithProviderFallbackAsync(
        long trackId,
        QobuzDownloadRequest request,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await EnsureFinalDestinationAllowedAsync(request, outputPath, cancellationToken);

        var requestedQuality = NormalizeQobuzQualityCode(request.Quality);
        var qualityOrder = GetQualityFallbackOrder(requestedQuality, request.AllowQualityFallback);
        Exception? lastFailure = null;

        foreach (var qualityCode in qualityOrder)
        {
            var officialAttempt = await TryDownloadWithOfficialCredentialsAsync(
                trackId,
                qualityCode,
                request,
                outputPath,
                cancellationToken);
            if (officialAttempt.Succeeded)
            {
                return;
            }
            if (officialAttempt.Failure != null)
            {
                lastFailure = officialAttempt.Failure;
            }

            var providers = await BuildPublicProvidersAsync(trackId, qualityCode, cancellationToken);
            foreach (var provider in providers)
            {
                var attempt = await TryDownloadWithProviderAsync(
                    provider,
                    trackId,
                    qualityCode,
                    request,
                    outputPath,
                    cancellationToken);
                if (attempt.Succeeded)
                {
                    return;
                }

                if (attempt.Failure != null)
                {
                    lastFailure = attempt.Failure;
                }
            }
        }

        if (lastFailure != null)
        {
            throw lastFailure;
        }

        throw new InvalidOperationException(DownloadUrlUnavailableMessage);
    }

    private sealed record ProviderDownloadAttempt(bool Succeeded, Exception? Failure);

    private async Task<ProviderDownloadAttempt> TryDownloadWithOfficialCredentialsAsync(
        long trackId,
        string qualityCode,
        QobuzDownloadRequest request,
        string outputPath,
        CancellationToken cancellationToken)
    {
        string? downloadUrl;
        try
        {
            downloadUrl = await TryResolveOfficialStreamUrlAsync(trackId, qualityCode, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Qobuz official credentials failed for track {TrackId} quality {Quality}.",
                trackId,
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(qualityCode));
            return new ProviderDownloadAttempt(false, ex);
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            return new ProviderDownloadAttempt(false, null);
        }

        try
        {
            request.Quality = qualityCode;
            await NotifySelectedQualityAsync(request, qualityCode);
            await ExecuteDownloadAndTagAsync(new DownloadExecutionContext
            {
                DownloadUrl = downloadUrl,
                OutputPath = outputPath,
                Request = request
            }, cancellationToken);
            return new ProviderDownloadAttempt(true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DownloadFileUtilities.TryDeleteFile(outputPath);
            _logger.LogWarning(
                ex,
                "Qobuz official credentials stream failed for track {TrackId} quality {Quality}.",
                trackId,
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(qualityCode));
            return new ProviderDownloadAttempt(false, ex);
        }
    }

    private async Task<ProviderDownloadAttempt> TryDownloadWithProviderAsync(
        ProviderCandidate provider,
        long trackId,
        string qualityCode,
        QobuzDownloadRequest request,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (IsProviderCoolingDown(provider.Name))
        {
            return new ProviderDownloadAttempt(false, null);
        }

        var resolution = await TryResolveProviderAsync(provider, trackId, qualityCode, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolution.DownloadUrl))
        {
            return new ProviderDownloadAttempt(false, resolution.Failure);
        }

        try
        {
            request.Quality = qualityCode;
            await NotifySelectedQualityAsync(request, qualityCode);
            await ExecuteDownloadAndTagAsync(new DownloadExecutionContext
            {
                DownloadUrl = resolution.DownloadUrl,
                OutputPath = outputPath,
                Request = request
            }, cancellationToken);
            return new ProviderDownloadAttempt(true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DownloadFileUtilities.TryDeleteFile(outputPath);
            await _publicProviderRegistry.RecordFailureAsync(
                provider.Id,
                ClassifyProviderFailure(ex),
                0,
                ShouldApplyProviderCooldown(ex) ? DateTimeOffset.UtcNow.Add(ProviderCooldown) : null,
                CancellationToken.None);
            if (!IsProviderStreamFailure(ex))
            {
                throw;
            }

            _logger.LogWarning(
                ex,
                "Qobuz provider {Provider} stream failed for track {TrackId} quality {Quality}; trying next provider.",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(provider.Name),
                trackId,
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(qualityCode));
            return new ProviderDownloadAttempt(false, ex);
        }
    }

    private static bool IsProviderStreamFailure(Exception ex)
    {
        if (ex is HttpRequestException)
        {
            return true;
        }

        if (ex is InvalidOperationException invalidOperation)
        {
            return ExceptionMessageContainsAny(
                invalidOperation,
                "Qobuz download failed",
                "duration",
                "identity validation failed",
                "output file is missing");
        }

        return false;
    }

    private async Task DownloadFileWithRetryAsync(
        string url,
        string outputPath,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await DownloadFileAsync(url, outputPath, progressCallback, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                DownloadFileUtilities.TryDeleteFile(outputPath);
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }

        await DownloadFileAsync(url, outputPath, progressCallback, cancellationToken);
    }

    private static string NormalizeQobuzQualityCode(string? quality) => QobuzQualityCodeNormalizer.Normalize(quality, defaultCode: "6");

    private static List<string> GetQualityFallbackOrder(string quality, bool allowQualityFallback)
    {
        if (!allowQualityFallback)
        {
            return new List<string> { string.IsNullOrWhiteSpace(quality) ? "6" : quality };
        }

        if (string.Equals(quality, "27", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { "27", "7", "6" };
        }

        if (string.Equals(quality, "7", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { "7", "6" };
        }

        if (string.Equals(quality, "6", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { "6" };
        }

        return new List<string> { string.IsNullOrWhiteSpace(quality) ? "6" : quality };
    }

    private sealed record ProviderResolutionAttempt(string? DownloadUrl, Exception? Failure);

    private async Task<ProviderResolutionAttempt> TryResolveProviderAsync(
        ProviderCandidate provider,
        long trackId,
        string qualityCode,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await ProviderResolutionGate.WaitAsync(cancellationToken);
        try
        {
            var resolved = await provider.ResolveAsync(cancellationToken);
            stopwatch.Stop();
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                await _publicProviderRegistry.RecordSuccessAsync(provider.Id, stopwatch.ElapsedMilliseconds, cancellationToken);
            }
            else
            {
                await _publicProviderRegistry.RecordFailureAsync(provider.Id, "empty_response", stopwatch.ElapsedMilliseconds, null, cancellationToken);
            }
            return new ProviderResolutionAttempt(resolved, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            if (ShouldApplyProviderCooldown(ex))
            {
                MarkProviderCoolingDown(provider.Name);
            }

            await _publicProviderRegistry.RecordFailureAsync(
                provider.Id,
                "timeout",
                stopwatch.ElapsedMilliseconds,
                DateTimeOffset.UtcNow.Add(ProviderCooldown),
                CancellationToken.None);

            _logger.LogWarning(
                ex,
                "Qobuz provider {Provider} canceled/timed out for track {TrackId} quality {Quality}",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(provider.Name),
                trackId,
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(qualityCode));
            return new ProviderResolutionAttempt(null, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ShouldApplyProviderCooldown(ex))
            {
                MarkProviderCoolingDown(provider.Name);
            }


            await _publicProviderRegistry.RecordFailureAsync(
                provider.Id,
                ClassifyProviderFailure(ex),
                stopwatch.ElapsedMilliseconds,
                ShouldApplyProviderCooldown(ex) ? DateTimeOffset.UtcNow.Add(ProviderCooldown) : null,
                CancellationToken.None);

            _logger.LogWarning(
                ex,
                "Qobuz provider {Provider} failed for track {TrackId} quality {Quality}",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(provider.Name),
                trackId,
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(qualityCode));
            return new ProviderResolutionAttempt(null, ex);
        }
        finally
        {
            ProviderResolutionGate.Release();
        }
    }


    private async Task<HttpResponseMessage> SendProviderRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var providerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        providerCts.CancelAfter(ProviderRequestTimeout);
        if (request.Headers.UserAgent.Count == 0)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        }

        try
        {
            return await _apiClient.SendAsync(request, providerCts.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Provider request timed out after {ProviderRequestTimeout.TotalSeconds:0} seconds.", ex);
        }
    }

    private static bool IsProviderCoolingDown(string providerName)
    {
        return ProviderBackoffUntil.TryGetValue(providerName, out var until)
            && until > DateTimeOffset.UtcNow;
    }

    private static void MarkProviderCoolingDown(string providerName)
    {
        ProviderBackoffUntil[providerName] = DateTimeOffset.UtcNow.Add(ProviderCooldown);
    }

    private static bool ShouldApplyProviderCooldown(Exception ex)
    {
        if (ex.Message.Contains("public download verification is required", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsTransientProviderFailure(ex);
    }

    private static bool IsTransientProviderFailure(Exception ex)
    {
        if (ex is TimeoutException or HttpRequestException)
        {
            return true;
        }

        if (ex is InvalidOperationException invalidOperation)
        {
            return ExceptionMessageContainsAny(
                invalidOperation,
                "HTTP 408",
                "HTTP 429",
                "HTTP 500",
                "HTTP 502",
                "HTTP 503",
                "HTTP 504",
                "timed out",
                "service unavailable",
                "upstream fetch failed",
                "empty response");
        }

        return false;
    }

    private static bool ExceptionMessageContainsAny(Exception ex, params string[] fragments)
    {
        var message = ex.Message;
        return fragments.Any(fragment => message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ProviderCandidate[]> BuildPublicProvidersAsync(long trackId, string qualityCode, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return [];
    }

    private async Task<string?> TryResolveOfficialStreamUrlAsync(
        long trackId,
        string qualityCode,
        CancellationToken cancellationToken)
    {
        await ProviderResolutionGate.WaitAsync(cancellationToken);
        try
        {
            return await TryGetOfficialQobuzStreamUrlAsync(trackId, qualityCode, cancellationToken);
        }
        finally
        {
            ProviderResolutionGate.Release();
        }
    }

    private async Task<string?> TryGetOfficialQobuzStreamUrlAsync(
        long trackId,
        string qualityCode,
        CancellationToken cancellationToken)
    {
        var credentials = await _credentialProvider.GetCredentialsAsync(cancellationToken);
        var appId = string.IsNullOrWhiteSpace(credentials.AppId)
            ? DefaultAppId
            : credentials.AppId.Trim();
        var authToken = credentials.AuthToken?.Trim();
        var secret = credentials.AppSecret?.Trim();
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(authToken))
        {
            return null;
        }

        var apiBase = string.IsNullOrWhiteSpace(_qobuzConfig.ApiBase)
            ? "https://www.qobuz.com/api.json/0.2/"
            : _qobuzConfig.ApiBase.Trim();
        if (!apiBase.EndsWith('/'))
        {
            apiBase += "/";
        }

        var normalizedQuality = NormalizeQobuzQualityCode(qualityCode);
        var requestTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sigInput = $"trackgetFileUrlformat_id{normalizedQuality}intentstreamtrack_id{trackId}{requestTs}{secret}";
        var requestSig = ComputeMd5Hex(sigInput);
        var url = $"{apiBase}track/getFileUrl?track_id={trackId}&format_id={normalizedQuality}&intent=stream&request_ts={requestTs}&request_sig={requestSig}&app_id={Uri.EscapeDataString(appId)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-app-id", appId);
        request.Headers.TryAddWithoutValidation("x-user-auth-token", authToken);
        using var response = await SendProviderRequestAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Qobuz official API returned HTTP {(int)response.StatusCode}: {DownloadFileUtilities.TruncateForLog(body)}");
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (TryExtractCommonProviderUrlPayload(payload, "Qobuz official API", out var directUrl))
        {
            return directUrl;
        }

        throw new InvalidOperationException("Qobuz official API response did not contain a usable stream URL.");
    }

    private static string ComputeMd5Hex(string input)
        => QobuzOfficialSignature.ComputeProtocolDigestHex(input);

    private static string ClassifyProviderFailure(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("429", StringComparison.OrdinalIgnoreCase)) return "rate_limited";
        if (message.Contains("captcha", StringComparison.OrdinalIgnoreCase)) return "captcha_required";
        if (exception is TimeoutException or HttpRequestException || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return "transient";
        return "unavailable";
    }

    private sealed record ProviderCandidate(string Id, string Name, Func<CancellationToken, Task<string?>> ResolveAsync);

    private async Task<string?> TryGetSignedZarzStreamUrlAsync(
        long trackId,
        string qualityCode,
        CancellationToken cancellationToken)
    {
        var trackUrl = $"https://open.qobuz.com/track/{trackId}";
        var ticket = await RequestZarzTicketAsync(trackUrl, cancellationToken);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            quality = MapZarzQuality(qualityCode),
            upload_to_r2 = false,
            id = trackId.ToString(CultureInfo.InvariantCulture),
            type = "track",
            url = trackUrl
        });
        using var response = await SendZarzSignedRequestAsync(
            HttpMethod.Post,
            ZarzDownloadPath,
            payload,
            new Dictionary<string, string> { ["X-Zarz-Ticket"] = ticket },
            cancellationToken);
        var body = await ReadProviderResponseBodyAsync(response, "zarz", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(BuildZarzFailure("zarz", response, body));
        }

        if (TryExtractCommonProviderUrlPayload(body, "zarz", out var directUrl))
        {
            return directUrl;
        }

        throw new InvalidOperationException("Qobuz API v1.1.0 did not return a usable stream URL.");
    }

    private async Task<string> RequestZarzTicketAsync(string trackUrl, CancellationToken cancellationToken)
    {
        var resourceHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"qbz:track:{trackUrl.ToLowerInvariant()}")))
            .ToLowerInvariant();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            capability = "download_ticket",
            provider = "qbz",
            resource_hash = resourceHash
        });
        using var response = await SendZarzSignedRequestAsync(
            HttpMethod.Post,
            ZarzTicketPath,
            payload,
            null,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(BuildZarzFailure("Qobuz ticket provider", response, body));
        }

        var ticket = JsonSerializer.Deserialize<ZarzTicketResponse>(body, SerializerOptions);
        var value = !string.IsNullOrWhiteSpace(ticket?.TicketId) ? ticket.TicketId : ticket?.Ticket;
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException("Qobuz ticket provider did not return a ticket.");
    }

    private async Task<HttpResponseMessage> SendZarzSignedRequestAsync(
        HttpMethod method,
        string path,
        byte[] body,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        var session = await EnsureZarzSessionAsync(cancellationToken);
        var uri = BuildZarzUri(path);
        var timestamp = DateTimeOffset.UtcNow;
        var timestampValue = timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var window = timestamp.ToUnixTimeSeconds() / ZarzTimeWindowSeconds;
        var rollingInput = $"{window}:{session.SessionId}";
        var rollingKey = Base64UrlNoPadding(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(session.SessionSecret),
            Encoding.UTF8.GetBytes(rollingInput)));
        var escapedPath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        var canonical = string.Join('\n',
            ZarzScheme,
            method.Method.ToUpperInvariant(),
            "/" + escapedPath.TrimStart('/'),
            string.Empty,
            bodyHash,
            timestampValue,
            nonce,
            session.SessionId,
            ZarzAppVersion,
            ZarzPlatform);
        var signature = Base64UrlNoPadding(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(rollingKey),
            Encoding.UTF8.GetBytes(canonical)));

        var request = new HttpRequestMessage(method, uri)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(ApplicationJsonContentType);
        request.Headers.TryAddWithoutValidation("Accept", ApplicationJsonContentType);
        request.Headers.TryAddWithoutValidation("User-Agent", $"SpotiFLAC-Mobile/{ZarzAppVersion}");
        request.Headers.TryAddWithoutValidation("X-Zarz-Session", session.SessionId);
        request.Headers.TryAddWithoutValidation("X-Zarz-Timestamp", timestampValue);
        request.Headers.TryAddWithoutValidation("X-Zarz-Nonce", nonce);
        request.Headers.TryAddWithoutValidation("X-Zarz-Body-SHA256", bodyHash);
        request.Headers.TryAddWithoutValidation("X-Zarz-Signature", signature);
        request.Headers.TryAddWithoutValidation("X-Zarz-App-Version", ZarzAppVersion);
        request.Headers.TryAddWithoutValidation("X-Zarz-Platform", ZarzPlatform);
        if (headers is not null)
        {
            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        var response = await _apiClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            await ClearZarzSessionAsync(cancellationToken);
        }
        return response;
    }

    private async Task<ZarzSessionRecord> EnsureZarzSessionAsync(CancellationToken cancellationToken)
    {
        await _zarzSessionGate.WaitAsync(cancellationToken);
        try
        {
            var session = await LoadZarzSessionNoLockAsync(cancellationToken);
            return session?.IsUsable == true
                ? session
                : throw new InvalidOperationException(
                    "Qobuz public download verification is required. Verify zarz in the Qobuz Public API Providers section.");
        }
        finally
        {
            _zarzSessionGate.Release();
        }
    }

    public async Task<bool> HasPublicDownloadSessionAsync(CancellationToken cancellationToken)
    {
        await _zarzSessionGate.WaitAsync(cancellationToken);
        try
        {
            return (await LoadZarzSessionNoLockAsync(cancellationToken))?.IsUsable == true;
        }
        finally
        {
            _zarzSessionGate.Release();
        }
    }

    public async Task<string?> BeginPublicDownloadVerificationAsync(CancellationToken cancellationToken)
    {
        await _zarzSessionGate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadZarzSessionNoLockAsync(cancellationToken);
            if (current?.IsUsable == true)
            {
                return null;
            }
            return await BootstrapZarzSessionNoLockAsync(current?.InstallId, cancellationToken);
        }
        finally
        {
            _zarzSessionGate.Release();
        }
    }

    public async Task CompletePublicDownloadVerificationAsync(string grant, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(grant))
        {
            throw new ArgumentException("Qobuz public download grant is required.", nameof(grant));
        }

        await _zarzSessionGate.WaitAsync(cancellationToken);
        try
        {
            var record = await LoadZarzSessionNoLockAsync(cancellationToken)
                ?? throw new InvalidOperationException("Start Qobuz public download verification first.");
            var payload = JsonSerializer.Serialize(new
            {
                grant = grant.Trim(),
                install_id = record.InstallId,
                app_version = ZarzAppVersion,
                platform = ZarzPlatform
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildZarzUri(ZarzExchangePath))
            {
                Content = new StringContent(payload, Encoding.UTF8, ApplicationJsonContentType)
            };
            request.Headers.TryAddWithoutValidation("User-Agent", $"SpotiFLAC-Mobile/{ZarzAppVersion}");
            using var response = await _apiClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(BuildZarzFailure("Qobuz session exchange", response, body));
            }

            var exchanged = JsonSerializer.Deserialize<ZarzBootstrapResponse>(body, SerializerOptions);
            record.SessionId = exchanged?.SessionId ?? string.Empty;
            record.SessionSecret = exchanged?.SessionSecret ?? string.Empty;
            record.ExpiresAt = ParseZarzExpiry(exchanged?.ExpiresAt);
            if (!record.IsUsable)
            {
                throw new InvalidOperationException("Qobuz session exchange did not return a usable session.");
            }
            await SaveZarzSessionNoLockAsync(record, cancellationToken);
        }
        finally
        {
            _zarzSessionGate.Release();
        }
    }

    private async Task<string> BootstrapZarzSessionNoLockAsync(string? installId, CancellationToken cancellationToken)
    {
        installId = string.IsNullOrWhiteSpace(installId)
            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()
            : installId;
        var builder = new UriBuilder(BuildZarzUri(ZarzBootstrapPath))
        {
            Query = string.Join('&', new Dictionary<string, string>
            {
                ["install_id"] = installId,
                ["app_version"] = ZarzAppVersion
            }.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        request.Headers.TryAddWithoutValidation("User-Agent", $"SpotiFLAC-Mobile/{ZarzAppVersion}");
        using var response = await _apiClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(BuildZarzFailure("Qobuz session bootstrap", response, body));
        }

        var payload = JsonSerializer.Deserialize<ZarzBootstrapResponse>(body, SerializerOptions);
        var record = new ZarzSessionRecord
        {
            InstallId = installId,
            SessionId = payload?.SessionId ?? string.Empty,
            SessionSecret = payload?.SessionSecret ?? string.Empty,
            ExpiresAt = ParseZarzExpiry(payload?.ExpiresAt)
        };
        await SaveZarzSessionNoLockAsync(record, cancellationToken);
        if (record.IsUsable)
        {
            return string.Empty;
        }
        var verificationUrl = FirstNonEmpty(payload?.AuthUrl, payload?.ChallengeUrl);
        if (!string.IsNullOrWhiteSpace(verificationUrl))
        {
            return verificationUrl;
        }

        return !string.IsNullOrWhiteSpace(payload?.ChallengeId)
            ? BuildZarzChallengeUrl(payload.ChallengeId)
            : throw new InvalidOperationException("Qobuz session bootstrap did not return a verification challenge.");
    }

    private async Task<ZarzSessionRecord?> LoadZarzSessionNoLockAsync(CancellationToken cancellationToken)
    {
        if (!IOFile.Exists(_zarzSessionPath))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ZarzSessionRecord>(
                await IOFile.ReadAllTextAsync(_zarzSessionPath, cancellationToken),
                SerializerOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load Qobuz signed session.");
            return null;
        }
    }

    private async Task SaveZarzSessionNoLockAsync(ZarzSessionRecord record, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_zarzSessionPath) ?? DeezSpoTagDataRootResolver.Resolve());
        await IOFile.WriteAllTextAsync(_zarzSessionPath, JsonSerializer.Serialize(record, SerializerOptions), cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            IOFile.SetUnixFileMode(_zarzSessionPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private async Task ClearZarzSessionAsync(CancellationToken cancellationToken)
    {
        await _zarzSessionGate.WaitAsync(cancellationToken);
        try
        {
            if (IOFile.Exists(_zarzSessionPath))
            {
                IOFile.Delete(_zarzSessionPath);
            }
        }
        finally
        {
            _zarzSessionGate.Release();
        }
    }

    private static Uri BuildZarzUri(string path) => new($"{ZarzBaseUrl.TrimEnd('/')}/{path.TrimStart('/')}");

    private static string BuildZarzChallengeUrl(string challengeId)
    {
        var callbackQuery = string.Join('&', new Dictionary<string, string>
        {
            ["cb_version"] = "v2grant",
            ["state"] = ZarzExtensionState
        }.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        var callback = $"{ZarzCallbackScheme}?{callbackQuery}";
        var builder = new UriBuilder(BuildZarzUri(ZarzChallengePath))
        {
            Query = string.Join('&', new Dictionary<string, string>
            {
                ["id"] = challengeId,
                ["cb"] = callback
            }.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
        };
        return builder.Uri.ToString();
    }

    private static string BuildZarzFailure(string name, HttpResponseMessage response, string body)
        => $"{name} failed with HTTP {(int)response.StatusCode}: {body.Trim()}";

    private static DateTimeOffset? ParseZarzExpiry(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static string Base64UrlNoPadding(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string MapZarzQuality(string qualityCode)
    {
        return NormalizeQobuzQualityCode(qualityCode) switch
        {
            "27" => "hi-res-max",
            "7" => "hi-res",
            _ => "cd"
        };
    }

    private static async Task<string> ReadProviderResponseBodyAsync(
        HttpResponseMessage response,
        string providerLabel,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException($"{providerLabel} returned an empty response.");
        }

        return body;
    }

    private static bool TryExtractCommonProviderUrlPayload(
        string body,
        string providerLabel,
        out string? directUrl)
    {
        if (TryExtractDirectUrlPayload(body, out directUrl))
        {
            return true;
        }

        if (LooksLikeHtml(body))
        {
            throw new InvalidOperationException($"{providerLabel} returned HTML instead of JSON.");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (TryExtractProviderUrl(doc.RootElement, out directUrl))
            {
                return true;
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{providerLabel} response was not valid JSON.");
        }

        return false;
    }

    private static bool TryExtractProviderUrl(JsonElement root, out string? url)
    {
        url = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (TryReadProviderUrl(root, out url))
        {
            return true;
        }

        if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object)
        {
            return TryReadProviderUrl(dataProp, out url);
        }

        return false;
    }

    private static bool TryReadProviderUrl(JsonElement element, out string? url)
    {
        url = ProviderUrlPropertyNames
            .Select(propertyName => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null)
            .FirstOrDefault(static providerUrl => !string.IsNullOrWhiteSpace(providerUrl));

        return !string.IsNullOrWhiteSpace(url);
    }

    private async Task DownloadFileAsync(string url, string outputPath, Func<double, double, Task>? progressCallback, CancellationToken cancellationToken)
    {
        using var response = await _downloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Qobuz download failed ({(int)response.StatusCode})");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = IOFile.Create(outputPath);
        await DownloadStreamHelper.CopyToAsyncWithProgress(stream, file, response.Content.Headers.ContentLength, progressCallback, cancellationToken);
    }

    private static async Task EnsureFinalDestinationAllowedAsync(
        QobuzDownloadRequest request,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var decision = await DownloadDedupeService.CheckFinalDestinationAsync(
            DownloadDedupeService.FromEngineDownloadRequest(request, outputPath),
            cancellationToken);
        if (!decision.Allowed)
        {
            throw new InvalidOperationException(decision.Message ?? "Qobuz final destination rejected by dedupe.");
        }
    }

    private async Task ExecuteDownloadAndTagAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await DownloadFileWithRetryAsync(
                context.DownloadUrl,
                context.OutputPath,
                context.Request.ProgressCallback,
                cancellationToken);

            var durationValidation = AudioDurationGuard.ValidateAgainstPreview(
                context.OutputPath,
                context.Request.DurationSeconds);
            if (!durationValidation.Success)
            {
                DownloadFileUtilities.TryDeleteFile(context.OutputPath);
                throw new InvalidOperationException(durationValidation.Message);
            }

            var identityValidation = ValidateDownloadedAudioIdentity(
                context.OutputPath,
                context.Request);
            if (!identityValidation.Success)
            {
                DownloadFileUtilities.TryDeleteFile(context.OutputPath);
                throw new InvalidOperationException(identityValidation.Message);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DownloadFileUtilities.TryDeleteFile(context.OutputPath);
            throw;
        }
    }

    private sealed class DownloadExecutionContext
    {
        public required string DownloadUrl { get; init; }
        public required string OutputPath { get; init; }
        public required QobuzDownloadRequest Request { get; init; }
    }

    private static AudioIdentityGuardResult ValidateDownloadedAudioIdentity(
        string filePath,
        QobuzDownloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !IOFile.Exists(filePath))
        {
            return AudioIdentityGuardResult.Fail("Audio identity validation failed: output file is missing.");
        }

        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            var expectedIsrc = request.Isrc?.Trim();
            var actualIsrc = tagFile.Tag.ISRC?.Trim();
            if (!string.IsNullOrWhiteSpace(expectedIsrc)
                && !string.IsNullOrWhiteSpace(actualIsrc)
                && !string.Equals(actualIsrc, expectedIsrc, StringComparison.OrdinalIgnoreCase))
            {
                return AudioIdentityGuardResult.Fail(
                    $"Audio identity validation failed: ISRC mismatch (expected {expectedIsrc}, got {actualIsrc}).");
            }

            var expectedTitle = request.TrackName?.Trim();
            var actualTitle = tagFile.Tag.Title?.Trim();
            if (!string.IsNullOrWhiteSpace(expectedTitle)
                && !string.IsNullOrWhiteSpace(actualTitle)
                && !QobuzTitlesMatch(expectedTitle, actualTitle))
            {
                return AudioIdentityGuardResult.Fail(
                    $"Audio identity validation failed: title mismatch (expected '{expectedTitle}', got '{actualTitle}').");
            }

            var expectedArtist = request.ArtistName?.Trim();
            var actualArtist = FirstNonEmpty(
                tagFile.Tag.FirstPerformer?.Trim(),
                tagFile.Tag.FirstAlbumArtist?.Trim());
            if (!string.IsNullOrWhiteSpace(expectedArtist)
                && !string.IsNullOrWhiteSpace(actualArtist)
                && !QobuzArtistsMatch(expectedArtist, actualArtist))
            {
                return AudioIdentityGuardResult.Fail(
                    $"Audio identity validation failed: artist mismatch (expected '{expectedArtist}', got '{actualArtist}').");
            }

            return AudioIdentityGuardResult.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AudioIdentityGuardResult.Fail($"Audio identity validation failed: {ex.Message}");
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private sealed record AudioIdentityGuardResult(bool Success, string Message)
    {
        public static AudioIdentityGuardResult Ok() => new(true, string.Empty);
        public static AudioIdentityGuardResult Fail(string message) => new(false, message);
    }

    private static string GetTrackArtist(QobuzTrack track)
    {
        return GetPerformerName(track)
            ?? track.Artist?.Name
            ?? track.Album?.Artist?.Name
            ?? string.Empty;
    }

    private static bool QobuzArtistsMatch(string expectedArtist, string foundArtist)
    {
        return TrackTitleMatcher.ArtistsMatch(expectedArtist, foundArtist)
            || IsCrossScriptVariant(expectedArtist, foundArtist);
    }

    private static bool IsCrossScriptVariant(string expectedArtist, string foundArtist)
    {
        return IsLatinScript(expectedArtist) != IsLatinScript(foundArtist);
    }

    private static bool QobuzTitlesMatch(string expectedTitle, string foundTitle)
    {
        var normExpected = NormalizeText(expectedTitle);
        var normFound = NormalizeText(foundTitle);
        if (string.IsNullOrWhiteSpace(normExpected) || string.IsNullOrWhiteSpace(normFound))
        {
            return false;
        }

        if (normExpected == normFound)
        {
            return true;
        }

        if (normExpected.Contains(normFound) || normFound.Contains(normExpected))
        {
            return true;
        }

        var cleanExpected = CleanTitle(normExpected);
        var cleanFound = CleanTitle(normFound);
        if (!string.IsNullOrWhiteSpace(cleanExpected) && cleanExpected == cleanFound)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(cleanExpected) && !string.IsNullOrWhiteSpace(cleanFound) &&
            (cleanExpected.Contains(cleanFound) || cleanFound.Contains(cleanExpected)))
        {
            return true;
        }

        var coreExpected = QobuzTitleHelpers.ExtractCoreTitle(normExpected);
        var coreFound = QobuzTitleHelpers.ExtractCoreTitle(normFound);
        if (!string.IsNullOrWhiteSpace(coreExpected) && coreExpected == coreFound)
        {
            return true;
        }

        if (IsLatinScript(expectedTitle) != IsLatinScript(foundTitle))
        {
            return true;
        }

        return false;
    }

    private static string NormalizeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\s+", " ", RegexOptions.None, RegexTimeout);
        return normalized;
    }

    private static string CleanTitle(string title)
    {
        var cleaned = title;
        var versionPatterns = new[]
        {
            "remaster", "remastered", "deluxe", "bonus", "single",
            "album version", "radio edit", "original mix", "extended",
            "club mix", "remix", "live", "acoustic", "demo"
        };

        while (true)
        {
            var startParen = cleaned.LastIndexOf('(');
            var endParen = cleaned.LastIndexOf(')');
            if (startParen >= 0 && endParen > startParen)
            {
                var content = cleaned[(startParen + 1)..endParen].ToLowerInvariant();
                if (versionPatterns.Any(pattern => content.Contains(pattern)))
                {
                    cleaned = $"{cleaned[..startParen].Trim()} {cleaned[(endParen + 1)..].Trim()}".Trim();
                    continue;
                }
            }
            break;
        }

        while (true)
        {
            var startBracket = cleaned.LastIndexOf('[');
            var endBracket = cleaned.LastIndexOf(']');
            if (startBracket >= 0 && endBracket > startBracket)
            {
                var content = cleaned[(startBracket + 1)..endBracket].ToLowerInvariant();
                if (versionPatterns.Any(pattern => content.Contains(pattern)))
                {
                    cleaned = $"{cleaned[..startBracket].Trim()} {cleaned[(endBracket + 1)..].Trim()}".Trim();
                    continue;
                }
            }
            break;
        }

        var dashPatterns = new[]
        {
            " - remaster", " - remastered", " - single version", " - radio edit",
            " - live", " - acoustic", " - demo", " - remix"
        };
        var matchedSuffix = dashPatterns.FirstOrDefault(pattern => cleaned.EndsWith(pattern, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(matchedSuffix))
        {
            cleaned = cleaned[..^matchedSuffix.Length];
        }

        cleaned = Regex.Replace(cleaned, @"\s+", " ", RegexOptions.None, RegexTimeout);
        return cleaned.Trim();
    }

    private static bool IsLatinScript(string value)
    {
        foreach (var code in value.EnumerateRunes().Select(rune => rune.Value))
        {
            if (code < 0x80)
            {
                continue;
            }

            if (IsLatinExtendedCodePoint(code))
            {
                continue;
            }

            if (IsKnownNonLatinScriptCodePoint(code))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLatinExtendedCodePoint(int code)
    {
        return IsInAnyRange(code, ExtendedLatinRanges);
    }

    private static bool IsKnownNonLatinScriptCodePoint(int code)
    {
        return IsInAnyRange(code, NonLatinScriptRanges);
    }

    private static bool IsInAnyRange(int code, (int Start, int End)[] ranges)
    {
        foreach (var (start, end) in ranges)
        {
            if (code >= start && code <= end)
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeHtml(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            return ch == '<';
        }

        return false;
    }

    private static bool TryExtractDirectUrlPayload(string value, out string? url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        url = trimmed;
        return true;
    }

    private static string DecodeBase64(string value)
    {
        return Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }

    private sealed class QobuzSearchResponse
    {
        [JsonPropertyName("tracks")]
        public QobuzSearchTracks Tracks { get; set; } = new();
    }

    private sealed class QobuzSearchTracks
    {
        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("items")]
        public List<QobuzTrack> Items { get; set; } = new();
    }

    private static string? GetPerformerName(QobuzTrack track)
    {
        var performer = track.Performer;
        switch (performer.ValueKind)
        {
            case JsonValueKind.String:
                return performer.GetString();
            case JsonValueKind.Object:
                if (performer.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    return name.GetString();
                }
                if (performer.TryGetProperty("artist", out var artist) && artist.ValueKind == JsonValueKind.Object &&
                    artist.TryGetProperty("name", out var artistName) && artistName.ValueKind == JsonValueKind.String)
                {
                    return artistName.GetString();
                }
                break;
        }

        return null;
    }

    private sealed class QobuzTrack
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("isrc")]
        public string? Isrc { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("duration")]
        public int? Duration { get; set; }

        [JsonPropertyName("track_number")]
        public int? TrackNumber { get; set; }

        [JsonPropertyName("media_number")]
        public int? DiscNumber { get; set; }

        [JsonPropertyName("performer")]
        public JsonElement Performer { get; set; }

        [JsonPropertyName("artist")]
        public QobuzArtist? Artist { get; set; }

        [JsonPropertyName("album")]
        public QobuzAlbum? Album { get; set; }
    }

    private sealed class QobuzArtist
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class QobuzAlbum
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("artist")]
        public QobuzArtist? Artist { get; set; }
    }

    private sealed class ZarzSessionRecord
    {
        public string InstallId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string SessionSecret { get; set; } = string.Empty;
        public DateTimeOffset? ExpiresAt { get; set; }

        [JsonIgnore]
        public bool IsUsable =>
            !string.IsNullOrWhiteSpace(SessionId)
            && !string.IsNullOrWhiteSpace(SessionSecret)
            && (!ExpiresAt.HasValue || ExpiresAt.Value > DateTimeOffset.UtcNow.AddMinutes(1));
    }

    private sealed class ZarzBootstrapResponse
    {
        [JsonPropertyName("auth_url")]
        public string? AuthUrl { get; set; }

        [JsonPropertyName("challenge_url")]
        public string? ChallengeUrl { get; set; }

        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        [JsonPropertyName("session_secret")]
        public string? SessionSecret { get; set; }

        [JsonPropertyName("expires_at")]
        public string? ExpiresAt { get; set; }

        [JsonPropertyName("challenge_id")]
        public string? ChallengeId { get; set; }
    }

    private sealed class ZarzTicketResponse
    {
        [JsonPropertyName("ticket_id")]
        public string? TicketId { get; set; }

        [JsonPropertyName("ticket")]
        public string? Ticket { get; set; }
    }

}
