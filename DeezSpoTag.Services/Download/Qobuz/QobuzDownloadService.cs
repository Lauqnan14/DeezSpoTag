using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using IOFile = System.IO.File;
using DeezSpoTag.Services.Metadata.Qobuz;
using DeezSpoTag.Services.Download.Shared.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Qobuz;

public interface IQobuzDownloadService
{
    Task<bool> IsrcAvailableAsync(string isrc, CancellationToken cancellationToken);
    Task<string> DownloadByUrlAsync(QobuzDownloadRequest request, CancellationToken cancellationToken);
    Task<string> DownloadByIsrcAsync(QobuzDownloadRequest request, CancellationToken cancellationToken);
}

public sealed class QobuzDownloadService : IQobuzDownloadService
{
    private const string DefaultAppId = "712109809";
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/144.0.0.0 Safari/537.36";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ProviderRequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ProviderTransientRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ProviderCooldown = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> ProviderBackoffUntil = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Uri JumoReferrerUri = new UriBuilder(Uri.UriSchemeHttps, "jumo-dl.pages.dev").Uri;
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
    private readonly QobuzTrackResolver _trackResolver;

    public QobuzDownloadService(
        ILogger<QobuzDownloadService> logger,
        QobuzTrackResolver trackResolver)
    {
        _logger = logger;
        _trackResolver = trackResolver;
        _apiClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        _apiClient.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        _apiClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _downloadClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        _downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
    }

    public async Task<bool> IsrcAvailableAsync(string isrc, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return false;
        }

        var apiBase = DecodeBase64("aHR0cHM6Ly93d3cucW9idXouY29tL2FwaS5qc29uLzAuMi90cmFjay9zZWFyY2g/cXVlcnk9");
        var query = $"isrc:{isrc}";
        var url = $"{apiBase}{Uri.EscapeDataString(query)}&limit=50&app_id={DefaultAppId}";

        using var response = await _apiClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var payload = JsonSerializer.Deserialize<QobuzSearchResponse>(body, SerializerOptions);
        return payload?.Tracks?.Total > 0;
    }

    public async Task<string> DownloadByIsrcAsync(QobuzDownloadRequest request, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.OutputDir);

        var resolvedIsrc = request.Isrc;
        if (string.IsNullOrWhiteSpace(resolvedIsrc))
        {
            var expectedDurationSec = request.DurationSeconds > 0 ? request.DurationSeconds : 0;
            var metadataTrack = await SearchByQueryAsync(
                request.TrackName,
                request.ArtistName,
                expectedDurationSec,
                requireStrongMatch: false,
                cancellationToken);
            if (metadataTrack == null)
            {
                throw new InvalidOperationException("Qobuz download requires an ISRC or metadata match.");
            }
            resolvedIsrc = metadataTrack.Isrc;
        }

        if (!string.IsNullOrWhiteSpace(resolvedIsrc)
            && AudioFilePathHelper.TryFindExistingByIsrc(request.OutputDir, resolvedIsrc, out var existingPath, ".flac"))
        {
            return existingPath;
        }

        var expectedPath = BuildSanitizedOutputPath(request, ".flac");
        if (TryResolveExpectedExisting(expectedPath, resolvedIsrc ?? string.Empty, out var resolvedPath))
        {
            return resolvedPath;
        }

        var resolution = await _trackResolver.ResolveTrackAsync(
            resolvedIsrc,
            request.TrackName,
            request.ArtistName,
            request.AlbumName,
            request.DurationSeconds > 0 ? request.DurationSeconds * 1000 : null,
            cancellationToken);
        if (resolution == null)
        {
            throw new InvalidOperationException("Qobuz track not found for ISRC or metadata.");
        }

        var track = resolution.Track;
        var downloadResolution = await GetDownloadUrlWithRetryAsync(track.Id, request.Quality, request.AllowQualityFallback, cancellationToken);
        if (string.IsNullOrWhiteSpace(downloadResolution.Url))
        {
            throw new InvalidOperationException("Qobuz download URL not available");
        }
        request.Quality = downloadResolution.SelectedQuality;
        await NotifySelectedQualityAsync(request, downloadResolution.SelectedQuality);

        var outputPath = expectedPath;
        await ExecuteDownloadAndTagAsync(new DownloadExecutionContext
        {
            DownloadUrl = downloadResolution.Url!,
            OutputPath = outputPath,
            Request = request
        }, cancellationToken);
        return outputPath;
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

        // Keep explicit Qobuz track URLs authoritative to avoid unintentionally swapping
        // to a different catalog entry/edition during metadata resolution.
        if (!IsExplicitQobuzTrackUrl(sourceUrl))
        {
            var resolution = await _trackResolver.ResolveTrackAsync(
                isrc: null,
                title: request.TrackName,
                artist: request.ArtistName,
                album: request.AlbumName,
                durationMs: request.DurationSeconds > 0 ? request.DurationSeconds * 1000 : null,
                cancellationToken);
            if (resolution?.Track.Id > 0 && resolution.Track.Id != trackId.Value)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Qobuz download URL corrected by resolver: requested={RequestedTrackId} resolved={ResolvedTrackId} source={Source} score={Score}",
                        trackId.Value,
                        resolution.Track.Id,
                        resolution.Source,
                        resolution.Score);                }
                trackId = resolution.Track.Id;
            }
        }

        var expectedPath = BuildSanitizedOutputPath(request, ".flac");
        if (TryResolveExpectedExisting(expectedPath, string.Empty, out var resolvedPath))
        {
            return resolvedPath;
        }

        var downloadResolution = await GetDownloadUrlWithRetryAsync(trackId.Value, request.Quality, request.AllowQualityFallback, cancellationToken);
        if (string.IsNullOrWhiteSpace(downloadResolution.Url))
        {
            throw new InvalidOperationException("Qobuz download URL not available");
        }
        request.Quality = downloadResolution.SelectedQuality;
        await NotifySelectedQualityAsync(request, downloadResolution.SelectedQuality);

        var outputPath = expectedPath;
        await ExecuteDownloadAndTagAsync(new DownloadExecutionContext
        {
            DownloadUrl = downloadResolution.Url!,
            OutputPath = outputPath,
            Request = request
        }, cancellationToken);
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

    private readonly record struct DownloadUrlResolution(string? Url, string SelectedQuality);

    private async Task<DownloadUrlResolution> GetDownloadUrlWithRetryAsync(
        long trackId,
        string quality,
        bool allowQualityFallback,
        CancellationToken cancellationToken)
    {
        var normalizedRequestedQuality = NormalizeQobuzQualityCode(quality);
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await GetDownloadUrlAsync(trackId, quality, allowQualityFallback, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }

        return new DownloadUrlResolution(null, normalizedRequestedQuality);
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

    private async Task<QobuzTrack?> SearchByQueryAsync(
        string title,
        string artist,
        int expectedDurationSec,
        bool requireStrongMatch,
        CancellationToken cancellationToken)
    {
        var queries = BuildSearchQueries(title, artist);
        if (queries.Count == 0)
        {
            return null;
        }

        var allTracks = await SearchTracksByQueriesAsync(queries, cancellationToken);
        if (allTracks.Count == 0)
        {
            return null;
        }

        var best = SelectBestSearchTrack(allTracks, title, artist, expectedDurationSec, requireStrongMatch);
        return best ?? (requireStrongMatch ? null : allTracks[0]);
    }

    private async Task<List<QobuzTrack>> SearchTracksByQueriesAsync(
        IReadOnlyList<string> queries,
        CancellationToken cancellationToken)
    {
        var allTracks = new List<QobuzTrack>();
        var seenTrackIds = new HashSet<long>();
        foreach (var query in queries)
        {
            var queryTracks = await SearchTracksForQueryAsync(query, cancellationToken);
            AddUniqueTracks(allTracks, seenTrackIds, queryTracks);
        }

        return allTracks;
    }

    private async Task<List<QobuzTrack>> SearchTracksForQueryAsync(string query, CancellationToken cancellationToken)
    {
        var apiBase = DecodeBase64("aHR0cHM6Ly93d3cucW9idXouY29tL2FwaS5qc29uLzAuMi90cmFjay9zZWFyY2g/cXVlcnk9");
        var url = $"{apiBase}{Uri.EscapeDataString(query)}&limit=20&app_id={DefaultAppId}";
        using var response = await _apiClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Qobuz search by metadata failed (status={Status}) url={Url} body={Body}",
                (int)response.StatusCode,
                url,
                DownloadFileUtilities.TruncateForLog(errorBody));
            return new List<QobuzTrack>();
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            _logger.LogWarning("Qobuz search by metadata returned empty response url={Url}", url);
            return new List<QobuzTrack>();
        }

        var payload = JsonSerializer.Deserialize<QobuzSearchResponse>(body, SerializerOptions);
        return payload?.Tracks?.Items ?? new List<QobuzTrack>();
    }

    private static void AddUniqueTracks(List<QobuzTrack> allTracks, HashSet<long> seenTrackIds, List<QobuzTrack> items)
    {
        foreach (var item in items)
        {
            if (item.Id <= 0 || seenTrackIds.Add(item.Id))
            {
                allTracks.Add(item);
            }
        }
    }

    private static QobuzTrack? SelectBestSearchTrack(
        List<QobuzTrack> allTracks,
        string title,
        string artist,
        int expectedDurationSec,
        bool requireStrongMatch)
    {
        QobuzTrack? best = null;
        var bestScore = -1;
        foreach (var item in allTracks)
        {
            var (strongMatch, score) = EvaluateSearchTrack(item, title, artist, expectedDurationSec);
            if (requireStrongMatch && !strongMatch)
            {
                continue;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = item;
            }
        }

        return best;
    }

    private static (bool strongMatch, int score) EvaluateSearchTrack(
        QobuzTrack item,
        string title,
        string artist,
        int expectedDurationSec)
    {
        var titleMatch = QobuzTitlesMatch(title, item.Title ?? string.Empty);
        var artistMatch = QobuzArtistsMatch(artist, GetTrackArtist(item));
        var durationMatch = expectedDurationSec > 0 && item.Duration.HasValue &&
            Math.Abs(item.Duration.Value - expectedDurationSec) <= 10;
        var strongMatch = titleMatch && artistMatch && (expectedDurationSec <= 0 || durationMatch);

        var score = 0;
        if (titleMatch)
        {
            score += 2;
        }

        if (artistMatch)
        {
            score += 2;
        }

        if (durationMatch)
        {
            score += 1;
        }

        return (strongMatch, score);
    }


    private async Task<DownloadUrlResolution> GetDownloadUrlAsync(
        long trackId,
        string quality,
        bool allowQualityFallback,
        CancellationToken cancellationToken)
    {
        var requestedQuality = NormalizeQobuzQualityCode(quality);
        var qualityOrder = GetQualityFallbackOrder(requestedQuality, allowQualityFallback);

        foreach (var qualityCode in qualityOrder)
        {
            var url = await TryGetDownloadUrlForQualityAsync(trackId, qualityCode, cancellationToken);
            if (!string.IsNullOrWhiteSpace(url))
            {
                return new DownloadUrlResolution(url, qualityCode);
            }
        }

        return new DownloadUrlResolution(null, requestedQuality);
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

        return new List<string> { string.IsNullOrWhiteSpace(quality) ? "6" : quality };
    }

    private async Task<string?> TryGetDownloadUrlForQualityAsync(long trackId, string qualityCode, CancellationToken cancellationToken)
    {
        var providers = BuildProviders(trackId, qualityCode)
            .OrderBy(_ => Random.Shared.Next())
            .ToArray();

        foreach (var provider in providers)
        {
            if (IsProviderCoolingDown(provider.Name))
            {
                continue;
            }

            var resolved = await TryResolveProviderAsync(provider, trackId, qualityCode, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    private async Task<string?> TryResolveProviderAsync(
        ProviderCandidate provider,
        long trackId,
        string qualityCode,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.ResolveAsync(cancellationToken);
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

            _logger.LogWarning(ex, "Qobuz provider {Provider} canceled/timed out for track {TrackId} quality {Quality}", provider.Name, trackId, qualityCode);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ShouldApplyProviderCooldown(ex))
            {
                MarkProviderCoolingDown(provider.Name);
            }

            _logger.LogWarning(ex, "Qobuz provider {Provider} failed for track {TrackId} quality {Quality}", provider.Name, trackId, qualityCode);
            return null;
        }
    }

    private async Task<string?> TryGetJumoStreamUrlAsync(long trackId, string qualityCode, CancellationToken cancellationToken)
    {
        var formatId = qualityCode switch
        {
            "27" => 27,
            "7" => 7,
            _ => 6
        };

        var url = $"https://jumo-dl.pages.dev/get?track_id={trackId}&format_id={formatId}&region=US";
        using var response = await SendProviderRequestWithRetryAsync(
            url,
            JumoReferrerUri,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Jumo returned HTTP {(int)response.StatusCode}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Jumo returned an empty response.");
        }

        if (TryExtractDirectUrlPayload(body, out var directUrl))
        {
            return directUrl;
        }

        if (LooksLikeHtml(body))
        {
            throw new InvalidOperationException("Jumo returned HTML instead of a stream payload.");
        }

        if (!TryExtractJumoUrl(body, out var resolved))
        {
            var decoded = DecodeJumoXor(body);
            if (!TryExtractJumoUrl(decoded, out resolved))
            {
                throw new InvalidOperationException("Jumo response did not contain a usable stream URL.");
            }
        }

        return resolved;
    }

    private static bool TryExtractJumoUrl(string body, out string? url)
    {
        url = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
            {
                url = urlProp.GetString();
                return !string.IsNullOrWhiteSpace(url);
            }

            if (root.TryGetProperty("data", out var dataProp)
                && dataProp.ValueKind == JsonValueKind.Object
                && dataProp.TryGetProperty("url", out var dataUrl)
                && dataUrl.ValueKind == JsonValueKind.String)
            {
                url = dataUrl.GetString();
                return !string.IsNullOrWhiteSpace(url);
            }

            if (root.TryGetProperty("link", out var linkProp) && linkProp.ValueKind == JsonValueKind.String)
            {
                url = linkProp.GetString();
                return !string.IsNullOrWhiteSpace(url);
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static string DecodeJumoXor(string input)
    {
        var runes = input.ToCharArray();
        var output = new char[runes.Length];
        for (var i = 0; i < runes.Length; i++)
        {
            var key = (char)((i * 17) % 128);
            output[i] = (char)(runes[i] ^ 253 ^ key);
        }

        return new string(output);
    }

    private async Task<string?> TryGetStreamUrlAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await SendProviderRequestWithRetryAsync(url, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Provider returned HTTP {(int)response.StatusCode}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Provider returned an empty response.");
        }

        if (TryExtractDirectUrlPayload(body, out var directUrl))
        {
            return directUrl;
        }

        if (LooksLikeHtml(body))
        {
            throw new InvalidOperationException("Provider returned HTML instead of JSON.");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var errorProp)
                && errorProp.ValueKind == JsonValueKind.String)
            {
                throw new InvalidOperationException(errorProp.GetString() ?? "Provider returned an error.");
            }

            if (TryExtractProviderUrl(doc.RootElement, out var providerUrl))
            {
                return providerUrl;
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Provider response was not valid JSON.");
        }

        var payload = JsonSerializer.Deserialize<QobuzStreamResponse>(body, SerializerOptions);
        if (!string.IsNullOrWhiteSpace(payload?.Url))
        {
            return payload.Url;
        }

        throw new InvalidOperationException("Provider response did not contain a usable stream URL.");
    }

    private async Task<HttpResponseMessage> SendProviderRequestWithRetryAsync(
        string url,
        Uri? referrer,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (referrer != null)
                {
                    request.Headers.Referrer = referrer;
                }

                return await SendProviderRequestAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientProviderFailure(ex))
            {
                await Task.Delay(ProviderTransientRetryDelay, cancellationToken);
            }
        }

        using var finalRequest = new HttpRequestMessage(HttpMethod.Get, url);
        if (referrer != null)
        {
            finalRequest.Headers.Referrer = referrer;
        }

        return await SendProviderRequestAsync(finalRequest, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendProviderRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var providerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        providerCts.CancelAfter(ProviderRequestTimeout);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);

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
            var message = invalidOperation.Message;
            return message.Contains("HTTP 408", StringComparison.OrdinalIgnoreCase)
                || message.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase)
                || message.Contains("HTTP 500", StringComparison.OrdinalIgnoreCase)
                || message.Contains("HTTP 502", StringComparison.OrdinalIgnoreCase)
                || message.Contains("HTTP 503", StringComparison.OrdinalIgnoreCase)
                || message.Contains("HTTP 504", StringComparison.OrdinalIgnoreCase)
                || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || message.Contains("service unavailable", StringComparison.OrdinalIgnoreCase)
                || message.Contains("upstream fetch failed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("empty response", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private ProviderCandidate[] BuildProviders(long trackId, string qualityCode)
    {
        var primaryBase = DecodeBase64("aHR0cHM6Ly9kYWIueWVldC5zdS9hcGkvc3RyZWFtP3RyYWNrSWQ9");
        var fallbackBase = DecodeBase64("aHR0cHM6Ly9kYWJtdXNpYy54eXovYXBpL3N0cmVhbT90cmFja0lkPQ==");
        var squidBase = DecodeBase64("aHR0cHM6Ly9xb2J1ei5zcXVpZC53dGYvYXBpL2Rvd25sb2FkLW11c2ljP3RyYWNrX2lkPQ==");
        var spotByeBase = "https://qobuz.spotbye.qzz.io/api/track/";
        var qbzBase = "https://qbz.afkarxyz.qzz.io/api/track/";

        return
        [
            new ProviderCandidate("dab.yeet.su", ct => TryGetStreamUrlAsync($"{primaryBase}{trackId}&quality={qualityCode}", ct)),
            new ProviderCandidate("dabmusic.xyz", ct => TryGetStreamUrlAsync($"{fallbackBase}{trackId}&quality={qualityCode}", ct)),
            new ProviderCandidate("qobuz.spotbye.qzz.io", ct => TryGetStreamUrlAsync($"{spotByeBase}{trackId}?quality={qualityCode}", ct)),
            new ProviderCandidate("qbz.afkarxyz.qzz.io", ct => TryGetStreamUrlAsync($"{qbzBase}{trackId}?quality={qualityCode}", ct)),
            new ProviderCandidate("qobuz.squid.wtf/us", ct => TryGetStreamUrlAsync($"{squidBase}{trackId}&quality={qualityCode}&country=US", ct)),
            new ProviderCandidate("qobuz.squid.wtf/fr", ct => TryGetStreamUrlAsync($"{squidBase}{trackId}&quality={qualityCode}&country=FR", ct)),
            new ProviderCandidate("jumo-dl", ct => TryGetJumoStreamUrlAsync(trackId, qualityCode, ct))
        ];
    }

    private sealed record ProviderCandidate(string Name, Func<CancellationToken, Task<string?>> ResolveAsync);

    private static bool TryExtractProviderUrl(JsonElement root, out string? url)
    {
        url = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
        {
            url = urlProp.GetString();
            return !string.IsNullOrWhiteSpace(url);
        }

        if (root.TryGetProperty("link", out var linkProp) && linkProp.ValueKind == JsonValueKind.String)
        {
            url = linkProp.GetString();
            return !string.IsNullOrWhiteSpace(url);
        }

        if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object)
        {
            if (dataProp.TryGetProperty("url", out var nestedUrl) && nestedUrl.ValueKind == JsonValueKind.String)
            {
                url = nestedUrl.GetString();
                return !string.IsNullOrWhiteSpace(url);
            }

            if (dataProp.TryGetProperty("link", out var nestedLink) && nestedLink.ValueKind == JsonValueKind.String)
            {
                url = nestedLink.GetString();
                return !string.IsNullOrWhiteSpace(url);
            }
        }

        return false;
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

    private static bool TryResolveExpectedExisting(string expectedPath, string isrc, out string resolvedPath)
    {
        resolvedPath = "";
        if (string.IsNullOrWhiteSpace(expectedPath) || string.IsNullOrWhiteSpace(isrc))
        {
            return false;
        }

        try
        {
            var fileInfo = new FileInfo(expectedPath);
            if (!fileInfo.Exists || fileInfo.Length <= 100 * 1024)
            {
                return false;
            }

            using var tagFile = TagLib.File.Create(expectedPath);
            if (!string.IsNullOrWhiteSpace(tagFile.Tag.ISRC) &&
                string.Equals(tagFile.Tag.ISRC, isrc, StringComparison.OrdinalIgnoreCase))
            {
                resolvedPath = expectedPath;
                return true;
            }

            DownloadFileUtilities.TryDeleteFile(expectedPath);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
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
        var normExpected = NormalizeText(expectedArtist);
        var normFound = NormalizeText(foundArtist);
        if (string.IsNullOrWhiteSpace(normExpected) || string.IsNullOrWhiteSpace(normFound))
        {
            return false;
        }

        if (HasDirectArtistMatch(normExpected, normFound))
        {
            return true;
        }

        var expectedArtists = SplitArtists(normExpected);
        var foundArtists = SplitArtists(normFound);
        return HasOverlappingArtistTokens(expectedArtists, foundArtists)
            || IsCrossScriptVariant(expectedArtist, foundArtist);
    }

    private static bool HasDirectArtistMatch(string normExpected, string normFound)
    {
        return normExpected == normFound
            || normExpected.Contains(normFound, StringComparison.Ordinal)
            || normFound.Contains(normExpected, StringComparison.Ordinal);
    }

    private static bool HasOverlappingArtistTokens(List<string> expectedArtists, List<string> foundArtists)
    {
        return expectedArtists.Any(exp => foundArtists.Any(fnd =>
            exp == fnd
            || exp.Contains(fnd, StringComparison.Ordinal)
            || fnd.Contains(exp, StringComparison.Ordinal)
            || SameWordsUnordered(exp, fnd)));
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

    private static List<string> SplitArtists(string artists)
    {
        var normalized = artists;
        normalized = normalized.Replace(" feat. ", "|")
            .Replace(" feat ", "|")
            .Replace(" ft. ", "|")
            .Replace(" ft ", "|")
            .Replace(" & ", "|")
            .Replace(" and ", "|")
            .Replace(", ", "|")
            .Replace(" x ", "|");

        var parts = normalized.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? new List<string>() : parts.ToList();
    }

    private static bool SameWordsUnordered(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        var wordsA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wordsB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (wordsA.Length != wordsB.Length || wordsA.Length == 0)
        {
            return false;
        }

        Array.Sort(wordsA, StringComparer.Ordinal);
        Array.Sort(wordsB, StringComparer.Ordinal);
        for (var i = 0; i < wordsA.Length; i++)
        {
            if (!string.Equals(wordsA[i], wordsB[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
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

    private static List<string> BuildSearchQueries(string title, string artist)
    {
        var queries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var combined = string.Join(' ', new[] { artist, title }.Where(part => !string.IsNullOrWhiteSpace(part)));
        AddSearchQuery(queries, seen, combined);
        AddSearchQuery(queries, seen, title);
        AddJapaneseRomajiQueries(queries, seen, title, artist);
        AddSearchQuery(queries, seen, artist);

        return queries;
    }

    private static void AddSearchQuery(List<string> queries, HashSet<string> seen, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = NormalizeText(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (seen.Add(normalized))
        {
            queries.Add(value.Trim());
        }
    }

    private static void AddJapaneseRomajiQueries(List<string> queries, HashSet<string> seen, string title, string artist)
    {
        if (!QobuzRomajiHelper.ContainsJapanese(title) && !QobuzRomajiHelper.ContainsJapanese(artist))
        {
            return;
        }

        var romajiTitle = QobuzRomajiHelper.JapaneseToRomaji(title);
        var romajiArtist = QobuzRomajiHelper.JapaneseToRomaji(artist);
        var cleanRomajiTitle = QobuzRomajiHelper.CleanToAscii(romajiTitle);
        var cleanRomajiArtist = QobuzRomajiHelper.CleanToAscii(romajiArtist);

        if (!string.IsNullOrWhiteSpace(cleanRomajiArtist) && !string.IsNullOrWhiteSpace(cleanRomajiTitle))
        {
            AddSearchQuery(queries, seen, $"{cleanRomajiArtist} {cleanRomajiTitle}");
        }

        if (!string.IsNullOrWhiteSpace(cleanRomajiTitle)
            && !string.Equals(cleanRomajiTitle, title, StringComparison.OrdinalIgnoreCase))
        {
            AddSearchQuery(queries, seen, cleanRomajiTitle);
        }

        AddSearchQuery(queries, seen, cleanRomajiArtist);
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

    private static bool IsExplicitQobuzTrackUrl(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (!parsed.Host.Contains("qobuz.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Regex.IsMatch(
            parsed.AbsolutePath,
            @"(?:^|/)track/\d+(?:/|$)",
            RegexOptions.IgnoreCase,
            RegexTimeout);
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

    private sealed class QobuzStreamResponse
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}
