using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using IOFile = System.IO.File;
using DeezSpoTag.Services.Metadata.Qobuz;
using DeezSpoTag.Services.Download.Shared.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Services.Download.Utils;

namespace DeezSpoTag.Services.Download.Qobuz;

public interface IQobuzDownloadService
{
    Task<bool> IsrcAvailableAsync(string isrc, CancellationToken cancellationToken);
    Task<string> DownloadByUrlAsync(QobuzDownloadRequest request, CancellationToken cancellationToken);
    Task<string> DownloadByIsrcAsync(QobuzDownloadRequest request, CancellationToken cancellationToken);
    Task<QobuzResolvedStreamUrl> ResolveStreamUrlByTrackIdAsync(
        int trackId,
        string quality,
        bool allowQualityFallback,
        CancellationToken cancellationToken);
}

public readonly record struct QobuzResolvedStreamUrl(string Url, string SelectedQuality);

public sealed class QobuzDownloadService : IQobuzDownloadService
{
    private const string ApplicationJsonContentType = "application/json";
    private const string DownloadUrlUnavailableMessage = "Qobuz download URL not available";
    private const string FlacExtension = ".flac";
    private const string DefaultAppId = "712109809";
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/144.0.0.0 Safari/537.36";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ProviderRequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ProviderTransientRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ProviderCooldown = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PreferredProviderTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StreamProbeTimeout = TimeSpan.FromSeconds(3);
    private const int ProviderHttpMaxAttempts = 2;
    private const int DownloadUrlResolutionMaxAttempts = 2;
    private const int StreamProbeReadLimitBytes = 64 * 1024;
    private static readonly ConcurrentDictionary<string, DateTimeOffset> ProviderBackoffUntil = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] ProviderUrlPropertyNames = ["url", "download_url", "link"];
    private static readonly string[] MonochromeQobuzProviderBases =
    [
        "https://trypt-hifi-dl-456461932686.us-west1.run.app",
        "https://qobuz.kennyy.com.br"
    ];
    private static readonly string[] MusicDlProviderEndpoints =
    [
        "https://api.zarz.moe/v1/dl/qbz",
        "https://dl.musicdl.me/dl/qbz",
        "https://qobuz.spotbye.qzz.io/dl/qbz"
    ];
    private const string MusicDlUserAgent = "QobuzDL/1.0";
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
    private readonly QobuzApiConfig _qobuzConfig;
    private readonly ResolveProxyClient _resolveProxyClient;
    private static readonly object PreferredProviderLock = new();
    private static string? _preferredProviderName;
    private static DateTimeOffset _preferredProviderSetAtUtc;

    public QobuzDownloadService(
        ILogger<QobuzDownloadService> logger,
        QobuzTrackResolver trackResolver,
        ResolveProxyClient resolveProxyClient,
        IOptions<QobuzApiConfig> qobuzOptions)
    {
        _logger = logger;
        _trackResolver = trackResolver;
        _resolveProxyClient = resolveProxyClient;
        _qobuzConfig = qobuzOptions.Value ?? new QobuzApiConfig();
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

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Download resolution path keeps explicit provider/metadata fallback ordering to preserve deterministic provider selection.")]
    public async Task<string> DownloadByIsrcAsync(QobuzDownloadRequest request, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.OutputDir);

        var resolvedIsrc = await ResolveRequestIsrcAsync(request, cancellationToken);
        if (TryResolveExistingDownloadPath(request, resolvedIsrc, out var existingDownloadPath))
        {
            return existingDownloadPath;
        }

        var expectedPath = BuildSanitizedOutputPath(request, FlacExtension);
        CleanUnverifiedExpectedOutput(expectedPath);

        var resolution = await _trackResolver.ResolveTrackAsync(
            resolvedIsrc,
            request.TrackName,
            request.ArtistName,
            request.AlbumName,
            request.DurationSeconds > 0 ? request.DurationSeconds * 1000 : null,
            cancellationToken);
        if (resolution == null)
        {
            return await DownloadByFallbackTrackIdAsync(request, resolvedIsrc, expectedPath, cancellationToken);
        }

        var track = resolution.Track;
        var outputPath = expectedPath;
        await DownloadTrackWithProviderFallbackAsync(track.Id, request, outputPath, cancellationToken);
        return outputPath;
    }

    private async Task<string?> ResolveRequestIsrcAsync(QobuzDownloadRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Isrc))
        {
            return request.Isrc;
        }

        var expectedDurationSec = request.DurationSeconds > 0 ? request.DurationSeconds : 0;
        var metadataTrack = await SearchByQueryAsync(
            request.TrackName,
            request.ArtistName,
            expectedDurationSec,
            requireStrongMatch: true,
            cancellationToken);
        if (metadataTrack == null)
        {
            throw new InvalidOperationException("Qobuz download requires an ISRC or a strict metadata match.");
        }

        return metadataTrack.Isrc;
    }

    private static bool TryResolveExistingDownloadPath(QobuzDownloadRequest request, string? resolvedIsrc, out string existingPath)
    {
        if (!string.IsNullOrWhiteSpace(resolvedIsrc)
            && AudioFilePathHelper.TryFindExistingByIsrc(request.OutputDir, resolvedIsrc, out var existingByIsrc, FlacExtension))
        {
            existingPath = existingByIsrc;
            return true;
        }

        var expectedPath = BuildSanitizedOutputPath(request, FlacExtension);
        if (TryResolveExpectedExisting(expectedPath, resolvedIsrc ?? string.Empty, out var resolvedPath))
        {
            existingPath = resolvedPath;
            return true;
        }

        existingPath = string.Empty;
        return false;
    }

    private async Task<string> DownloadByFallbackTrackIdAsync(
        QobuzDownloadRequest request,
        string? resolvedIsrc,
        string expectedPath,
        CancellationToken cancellationToken)
    {
        var fallbackTrackId = await ResolveFallbackTrackIdAsync(request, resolvedIsrc, cancellationToken);
        if (!fallbackTrackId.HasValue || fallbackTrackId.Value <= 0)
        {
            throw new InvalidOperationException("Qobuz track not found for ISRC or metadata.");
        }

        await DownloadTrackWithProviderFallbackAsync(fallbackTrackId.Value, request, expectedPath, cancellationToken);

        return expectedPath;
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

        var expectedPath = BuildSanitizedOutputPath(request, FlacExtension);
        if (TryResolveExpectedExisting(expectedPath, string.Empty, out var resolvedPath))
        {
            return resolvedPath;
        }
        CleanUnverifiedExpectedOutput(expectedPath);

        var outputPath = expectedPath;
        await DownloadTrackWithProviderFallbackAsync(trackId.Value, request, outputPath, cancellationToken);
        return outputPath;
    }

    public async Task<QobuzResolvedStreamUrl> ResolveStreamUrlByTrackIdAsync(
        int trackId,
        string quality,
        bool allowQualityFallback,
        CancellationToken cancellationToken)
    {
        if (trackId <= 0)
        {
            throw new InvalidOperationException("Qobuz track id must be greater than zero.");
        }

        var normalizedQuality = NormalizeQobuzQualityCode(quality);
        var resolution = await GetDownloadUrlWithRetryAsync(
            trackId,
            normalizedQuality,
            allowQualityFallback,
            0,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(resolution.Url))
        {
            throw new InvalidOperationException(DownloadUrlUnavailableMessage);
        }

        return new QobuzResolvedStreamUrl(resolution.Url!, resolution.SelectedQuality);
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

    private async Task<string> ResolveEffectiveSelectedQualityAsync(
        DownloadUrlResolution resolution,
        CancellationToken cancellationToken)
    {
        var selected = NormalizeQobuzQualityCode(resolution.SelectedQuality);
        if (!ShouldProbeStreamBitDepth(selected) || string.IsNullOrWhiteSpace(resolution.Url))
        {
            return selected;
        }

        var probed = await TryProbeStreamQualityCodeAsync(resolution.Url!, cancellationToken);
        if (string.IsNullOrWhiteSpace(probed))
        {
            return selected;
        }

        var normalizedProbed = NormalizeQobuzQualityCode(probed);
        if (!string.Equals(normalizedProbed, selected, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Qobuz stream probe adjusted selected quality from {Selected} to {Probed}",
                selected,
                normalizedProbed);
        }

        return normalizedProbed;
    }

    private static bool ShouldProbeStreamBitDepth(string qualityCode)
        => string.Equals(qualityCode, "7", StringComparison.OrdinalIgnoreCase)
           || string.Equals(qualityCode, "27", StringComparison.OrdinalIgnoreCase);

    private async Task<string?> TryProbeStreamQualityCodeAsync(
        string downloadUrl,
        CancellationToken cancellationToken)
    {
        var probe = await TryProbeFlacStreamInfoAsync(downloadUrl, cancellationToken);
        if (probe == null)
        {
            return null;
        }

        return MapQobuzQualityFromProbe(probe.Value.BitsPerSample, probe.Value.SampleRate);
    }

    private async Task<FlacStreamProbeResult?> TryProbeFlacStreamInfoAsync(
        string downloadUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(StreamProbeTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            request.Headers.Range = new RangeHeaderValue(0, StreamProbeReadLimitBytes - 1);
            using var response = await _downloadClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                probeCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(probeCts.Token);
            var buffer = new byte[StreamProbeReadLimitBytes];
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var bytesRead = await stream.ReadAsync(
                    buffer.AsMemory(totalRead, buffer.Length - totalRead),
                    probeCts.Token);
                if (bytesRead <= 0)
                {
                    break;
                }

                totalRead += bytesRead;
                if (totalRead >= 42)
                {
                    break;
                }
            }

            if (totalRead <= 0)
            {
                return null;
            }

            if (!TryExtractFlacStreamInfo(
                    buffer.AsSpan(0, totalRead),
                    out var bitsPerSample,
                    out var sampleRate,
                    out var durationSeconds))
            {
                return null;
            }

            return new FlacStreamProbeResult(bitsPerSample, sampleRate, durationSeconds);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Qobuz stream probe failed for quality inference.");
            }

            return null;
        }
    }

    private static bool TryExtractFlacStreamInfo(
        ReadOnlySpan<byte> payload,
        out int bitsPerSample,
        out int sampleRate,
        out double durationSeconds)
    {
        bitsPerSample = 0;
        sampleRate = 0;
        durationSeconds = 0;

        var marker = "fLaC"u8;
        var markerIndex = payload.IndexOf(marker);
        if (markerIndex < 0)
        {
            return false;
        }

        var cursor = markerIndex + marker.Length;
        while (cursor + 4 <= payload.Length)
        {
            var header = payload[cursor];
            var blockType = header & 0x7F;
            var blockLength =
                (payload[cursor + 1] << 16)
                | (payload[cursor + 2] << 8)
                | payload[cursor + 3];
            cursor += 4;

            if (cursor + blockLength > payload.Length)
            {
                return false;
            }

            if (blockType == 0)
            {
                if (blockLength < 34)
                {
                    return false;
                }

                var streamInfo = payload.Slice(cursor, 34);
                sampleRate =
                    (streamInfo[10] << 12)
                    | (streamInfo[11] << 4)
                    | ((streamInfo[12] & 0xF0) >> 4);
                bitsPerSample = (((streamInfo[12] & 0x01) << 4) | ((streamInfo[13] & 0xF0) >> 4)) + 1;
                ulong totalSamples =
                    ((ulong)(streamInfo[13] & 0x0F) << 32)
                    | ((ulong)streamInfo[14] << 24)
                    | ((ulong)streamInfo[15] << 16)
                    | ((ulong)streamInfo[16] << 8)
                    | streamInfo[17];
                if (sampleRate > 0 && totalSamples > 0)
                {
                    durationSeconds = totalSamples / (double)sampleRate;
                }

                return sampleRate > 0 && bitsPerSample > 0;
            }

            cursor += blockLength;
        }

        return false;
    }

    private readonly record struct FlacStreamProbeResult(
        int BitsPerSample,
        int SampleRate,
        double DurationSeconds);

    private static string MapQobuzQualityFromProbe(int bitsPerSample, int sampleRate)
    {
        if (bitsPerSample >= 24)
        {
            return sampleRate >= 96000 ? "27" : "7";
        }

        return "6";
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
        int expectedDurationSeconds,
        CancellationToken cancellationToken)
    {
        var normalizedRequestedQuality = NormalizeQobuzQualityCode(quality);
        for (var attempt = 1; attempt <= DownloadUrlResolutionMaxAttempts; attempt++)
        {
            try
            {
                return await GetDownloadUrlAsync(
                    trackId,
                    quality,
                    allowQualityFallback,
                    expectedDurationSeconds,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (attempt < DownloadUrlResolutionMaxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }

        return new DownloadUrlResolution(null, normalizedRequestedQuality);
    }

    private async Task DownloadTrackWithProviderFallbackAsync(
        long trackId,
        QobuzDownloadRequest request,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var requestedQuality = NormalizeQobuzQualityCode(request.Quality);
        var qualityOrder = GetQualityFallbackOrder(requestedQuality, request.AllowQualityFallback);
        Exception? lastFailure = null;

        foreach (var qualityCode in qualityOrder)
        {
            var providers = BuildOrderedProviders(trackId, qualityCode);
            foreach (var provider in providers)
            {
                if (IsProviderCoolingDown(provider.Name))
                {
                    continue;
                }

                var downloadUrl = await TryResolveProviderAsync(provider, trackId, qualityCode, cancellationToken);
                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    continue;
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
                    MarkPreferredProvider(provider.Name);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastFailure = ex;
                    DownloadFileUtilities.TryDeleteFile(outputPath);
                    ClearPreferredProvider(provider.Name);
                    if (IsProviderStreamFailure(ex))
                    {
                        _logger.LogWarning(
                            ex,
                            "Qobuz provider {Provider} stream failed for track {TrackId} quality {Quality}; trying next provider.",
                            DeezSpoTag.Core.Security.LogSanitizer.OneLine(provider.Name),
                            trackId,
                            DeezSpoTag.Core.Security.LogSanitizer.OneLine(qualityCode));
                        continue;
                    }

                    throw;
                }
            }
        }

        if (lastFailure != null)
        {
            throw lastFailure;
        }

        throw new InvalidOperationException(DownloadUrlUnavailableMessage);
    }

    private ProviderCandidate[] BuildOrderedProviders(long trackId, string qualityCode)
    {
        var providers = BuildProviders(trackId, qualityCode).ToList();
        var preferredProviderName = GetPreferredProviderName();
        if (string.IsNullOrWhiteSpace(preferredProviderName))
        {
            return providers.ToArray();
        }

        var preferredIndex = providers.FindIndex(provider =>
            string.Equals(provider.Name, preferredProviderName, StringComparison.OrdinalIgnoreCase));
        if (preferredIndex <= 0)
        {
            return providers.ToArray();
        }

        var preferred = providers[preferredIndex];
        providers.RemoveAt(preferredIndex);
        providers.Insert(0, preferred);
        return providers.ToArray();
    }

    private static bool IsProviderStreamFailure(Exception ex)
    {
        if (ex is HttpRequestException)
        {
            return true;
        }

        if (ex is InvalidOperationException invalidOperation)
        {
            var message = invalidOperation.Message;
            return message.Contains("Qobuz download failed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("duration", StringComparison.OrdinalIgnoreCase)
                || message.Contains("identity validation failed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("output file is missing", StringComparison.OrdinalIgnoreCase);
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

        return SelectBestSearchTrack(allTracks, title, artist, expectedDurationSec, requireStrongMatch);
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
        int expectedDurationSeconds,
        CancellationToken cancellationToken)
    {
        var requestedQuality = NormalizeQobuzQualityCode(quality);
        var qualityOrder = GetQualityFallbackOrder(requestedQuality, allowQualityFallback);

        foreach (var qualityCode in qualityOrder)
        {
            var url = await TryGetDownloadUrlForQualityAsync(
                trackId,
                qualityCode,
                expectedDurationSeconds,
                cancellationToken);
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

    private async Task<string?> TryGetDownloadUrlForQualityAsync(
        long trackId,
        string qualityCode,
        int expectedDurationSeconds,
        CancellationToken cancellationToken)
    {
        var providers = BuildProviders(trackId, qualityCode);
        var preferredProviderName = GetPreferredProviderName();
        if (!string.IsNullOrWhiteSpace(preferredProviderName))
        {
            var preferredProvider = providers.FirstOrDefault(provider =>
                string.Equals(provider.Name, preferredProviderName, StringComparison.OrdinalIgnoreCase));
            if (preferredProvider != null && !IsProviderCoolingDown(preferredProvider.Name))
            {
                var preferredResolved = await TryResolveProviderAsync(preferredProvider, trackId, qualityCode, cancellationToken);
                if (!string.IsNullOrWhiteSpace(preferredResolved)
                    && await IsProviderStreamAcceptableAsync(
                        preferredProvider.Name,
                        trackId,
                        qualityCode,
                        preferredResolved,
                        expectedDurationSeconds,
                        cancellationToken))
                {
                    MarkPreferredProvider(preferredProvider.Name);
                    return preferredResolved;
                }

                ClearPreferredProvider(preferredProvider.Name);
            }
        }

        providers = providers
            .Where(provider => !IsProviderCoolingDown(provider.Name))
            .Where(provider => string.IsNullOrWhiteSpace(preferredProviderName)
                || !string.Equals(provider.Name, preferredProviderName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (providers.Length == 0)
        {
            return null;
        }

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = providers
            .ToDictionary(
                provider => provider.Name,
                provider => TryResolveProviderAsync(provider, trackId, qualityCode, raceCts.Token),
                StringComparer.OrdinalIgnoreCase);

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending.Values);
            var completedKey = pending.First(pair => ReferenceEquals(pair.Value, completed)).Key;
            pending.Remove(completedKey);

            var resolved = await completed;
            if (!string.IsNullOrWhiteSpace(resolved)
                && await IsProviderStreamAcceptableAsync(
                    completedKey,
                    trackId,
                    qualityCode,
                    resolved,
                    expectedDurationSeconds,
                    cancellationToken))
            {
                MarkPreferredProvider(completedKey);
                await raceCts.CancelAsync();
                return resolved;
            }
        }

        return null;
    }

    private static string? GetPreferredProviderName()
    {
        lock (PreferredProviderLock)
        {
            if (string.IsNullOrWhiteSpace(_preferredProviderName))
            {
                return null;
            }

            if ((DateTimeOffset.UtcNow - _preferredProviderSetAtUtc) > PreferredProviderTtl)
            {
                _preferredProviderName = null;
                _preferredProviderSetAtUtc = default;
                return null;
            }

            return _preferredProviderName;
        }
    }

    private static void MarkPreferredProvider(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return;
        }

        lock (PreferredProviderLock)
        {
            _preferredProviderName = providerName;
            _preferredProviderSetAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private static void ClearPreferredProvider(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return;
        }

        lock (PreferredProviderLock)
        {
            if (!string.Equals(_preferredProviderName, providerName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _preferredProviderName = null;
            _preferredProviderSetAtUtc = default;
        }
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

            _logger.LogWarning(
                ex,
                "Qobuz provider {Provider} canceled/timed out for track {TrackId} quality {Quality}",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(provider.Name),
                trackId,
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(qualityCode));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ShouldApplyProviderCooldown(ex))
            {
                MarkProviderCoolingDown(provider.Name);
            }

            _logger.LogWarning(
                ex,
                "Qobuz provider {Provider} failed for track {TrackId} quality {Quality}",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(provider.Name),
                trackId,
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(qualityCode));
            return null;
        }
    }


    private async Task<HttpResponseMessage> SendProviderRequestWithRetryAsync(
        string url,
        Uri? referrer,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= ProviderHttpMaxAttempts; attempt++)
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
            catch (Exception ex) when (attempt < ProviderHttpMaxAttempts && IsTransientProviderFailure(ex))
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

    private async Task<HttpResponseMessage> SendProviderRequestWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= ProviderHttpMaxAttempts; attempt++)
        {
            try
            {
                using var request = requestFactory();
                return await SendProviderRequestAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < ProviderHttpMaxAttempts && IsTransientProviderFailure(ex))
            {
                await Task.Delay(ProviderTransientRetryDelay, cancellationToken);
            }
        }

        using var finalRequest = requestFactory();
        return await SendProviderRequestAsync(finalRequest, cancellationToken);
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
        return !IsTransientProviderFailure(ex);
    }

    private async Task<bool> IsProviderStreamAcceptableAsync(
        string providerName,
        long trackId,
        string qualityCode,
        string resolvedUrl,
        int expectedDurationSeconds,
        CancellationToken cancellationToken)
    {
        if (expectedDurationSeconds <= 0 || string.IsNullOrWhiteSpace(resolvedUrl))
        {
            return true;
        }

        var probe = await TryProbeFlacStreamInfoAsync(resolvedUrl, cancellationToken);
        if (probe == null || probe.Value.DurationSeconds <= 0)
        {
            return true;
        }

        if (!IsSevereDurationMismatch(probe.Value.DurationSeconds, expectedDurationSeconds))
        {
            return true;
        }

        MarkProviderCoolingDown(providerName);
        _logger.LogWarning(
            "Qobuz provider {Provider} rejected for track {TrackId} quality {Quality}: stream duration {ActualDuration:F1}s mismatches expected {ExpectedDuration}s",
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(providerName),
            trackId,
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(qualityCode),
            probe.Value.DurationSeconds,
            expectedDurationSeconds);
        return false;
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
        var squidBase = "https://qobuz.squid.wtf/api/download-music";

        return
        [
            new ProviderCandidate("qobuz-official", ct => TryGetOfficialQobuzStreamUrlAsync(trackId, qualityCode, ct)),
            new ProviderCandidate("qobuz.squid.wtf/us", ct => TryGetSquidStreamUrlAsync(squidBase, trackId, qualityCode, "US", ct)),
            new ProviderCandidate("qobuz.squid.wtf/fr", ct => TryGetSquidStreamUrlAsync(squidBase, trackId, qualityCode, "FR", ct)),
            new ProviderCandidate("qobuz.spotbye.qzz.io", ct => TryGetMusicDlStreamUrlAsync(MusicDlProviderEndpoints[2], trackId, qualityCode, ct)),
            new ProviderCandidate("api.zarz.moe/dl/qbz", ct => TryGetMusicDlStreamUrlAsync(MusicDlProviderEndpoints[0], trackId, qualityCode, ct)),
            new ProviderCandidate("dl.musicdl.me", ct => TryGetMusicDlStreamUrlAsync(MusicDlProviderEndpoints[1], trackId, qualityCode, ct)),
            new ProviderCandidate("monochrome-qobuz:trypt-hifi-dl-456461932686.us-west1.run.app", ct => TryGetMonochromeQobuzStreamUrlByTrackIdAsync(MonochromeQobuzProviderBases[0], trackId, qualityCode, ct)),
            new ProviderCandidate("monochrome-qobuz:qobuz.kennyy.com.br", ct => TryGetMonochromeQobuzStreamUrlByTrackIdAsync(MonochromeQobuzProviderBases[1], trackId, qualityCode, ct))
        ];
    }

    private async Task<string?> TryGetOfficialQobuzStreamUrlAsync(
        long trackId,
        string qualityCode,
        CancellationToken cancellationToken)
    {
        var appId = string.IsNullOrWhiteSpace(_qobuzConfig.AppId)
            ? DefaultAppId
            : _qobuzConfig.AppId.Trim();
        var authToken = _qobuzConfig.AuthToken?.Trim();
        var secret = _qobuzConfig.DownloadSecret?.Trim();
        if (string.IsNullOrWhiteSpace(authToken) || string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("Qobuz download credentials are missing. Configure Qobuz:AuthToken and Qobuz:DownloadSecret.");
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

    private async Task<long?> TryResolveTrackIdByIsrcAsync(string isrc, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        var apiBase = DecodeBase64("aHR0cHM6Ly93d3cucW9idXouY29tL2FwaS5qc29uLzAuMi90cmFjay9zZWFyY2g/cXVlcnk9");
        var query = $"isrc:{isrc}";
        var url = $"{apiBase}{Uri.EscapeDataString(query)}&limit=20&app_id={DefaultAppId}";
        using var response = await _apiClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var payload = JsonSerializer.Deserialize<QobuzSearchResponse>(body, SerializerOptions);
        var match = payload?.Tracks?.Items?.FirstOrDefault(track =>
            !string.IsNullOrWhiteSpace(track.Isrc)
            && string.Equals(track.Isrc, isrc, StringComparison.OrdinalIgnoreCase));
        if (match?.Id > 0)
        {
            return match.Id;
        }

        return null;
    }

    private async Task<long?> ResolveFallbackTrackIdAsync(
        QobuzDownloadRequest request,
        string? resolvedIsrc,
        CancellationToken cancellationToken)
    {
        var fromProxy = await TryResolveTrackIdViaResolveProxyAsync(request, cancellationToken);
        if (fromProxy.HasValue && fromProxy.Value > 0)
        {
            var validated = await _trackResolver.ValidateTrackIdAsync(
                checked((int)fromProxy.Value),
                resolvedIsrc,
                request.TrackName,
                request.ArtistName,
                request.AlbumName,
                request.DurationSeconds > 0 ? request.DurationSeconds * 1000 : null,
                cancellationToken);
            if (validated?.Track.Id > 0)
            {
                return validated.Track.Id;
            }

            _logger.LogWarning(
                "Rejected Qobuz resolve-proxy track id {TrackId} because it did not match requested metadata for {Artist} - {Title}.",
                fromProxy.Value,
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(request.ArtistName),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(request.TrackName));
        }

        return await TryResolveTrackIdByIsrcAsync(resolvedIsrc ?? string.Empty, cancellationToken);
    }

    private async Task<long?> TryResolveTrackIdViaResolveProxyAsync(
        QobuzDownloadRequest request,
        CancellationToken cancellationToken)
    {
        if (_resolveProxyClient == null)
        {
            return null;
        }

        SongLinkResult? proxyResult = null;
        if (!string.IsNullOrWhiteSpace(request.ServiceUrl))
        {
            proxyResult = await _resolveProxyClient.ResolveUrlAsync(request.ServiceUrl, cancellationToken);
        }

        if (proxyResult == null
            && !string.IsNullOrWhiteSpace(request.SpotifyId))
        {
            proxyResult = await _resolveProxyClient.ResolvePlatformIdAsync(
                "spotify",
                "track",
                request.SpotifyId,
                cancellationToken);
        }

        if (proxyResult?.QobuzUrl == null)
        {
            return null;
        }

        return TryExtractTrackId(proxyResult.QobuzUrl);
    }

    private static string ComputeMd5Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = MD5.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<string?> TryGetSquidStreamUrlAsync(
        string squidBase,
        long trackId,
        string qualityCode,
        string country,
        CancellationToken cancellationToken)
    {
        var normalizedQuality = NormalizeQobuzQualityCode(qualityCode);
        var url = $"{squidBase}?track_id={trackId}&quality={normalizedQuality}&country={country}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var squidUserAgent = string.IsNullOrWhiteSpace(_qobuzConfig.SquidUserAgent)
            ? BrowserUserAgent
            : _qobuzConfig.SquidUserAgent.Trim();
        request.Headers.TryAddWithoutValidation("User-Agent", squidUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", ApplicationJsonContentType);

        var clearance = _qobuzConfig.SquidCfClearance?.Trim();
        if (!string.IsNullOrWhiteSpace(clearance))
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"cf_clearance={clearance}");
        }

        using var response = await SendProviderRequestAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Squid returned HTTP {(int)response.StatusCode}: {DownloadFileUtilities.TruncateForLog(body)}");
        }

        if (TryExtractCommonProviderUrlPayload(body, "Squid Qobuz", out var directUrl))
        {
            return directUrl;
        }

        throw new InvalidOperationException("Squid response did not contain a usable stream URL.");
    }

    private sealed record ProviderCandidate(string Name, Func<CancellationToken, Task<string?>> ResolveAsync);

    private async Task<string?> TryGetMonochromeQobuzStreamUrlByTrackIdAsync(
        string providerBase,
        long trackId,
        string qualityCode,
        CancellationToken cancellationToken)
    {
        var url = $"{providerBase.TrimEnd('/')}/api/download-music?track_id={trackId}&quality={NormalizeQobuzQualityCode(qualityCode)}";
        using var response = await SendProviderRequestWithRetryAsync(url, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Monochrome Qobuz download returned HTTP {(int)response.StatusCode}");
        }

        var body = await ReadProviderResponseBodyAsync(response, "Monochrome Qobuz download", cancellationToken);
        if (TryExtractCommonProviderUrlPayload(body, "Monochrome Qobuz download", out var directUrl))
        {
            return directUrl;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("success", out var successProp)
                && successProp.ValueKind == JsonValueKind.False)
            {
                throw new InvalidOperationException("Monochrome Qobuz download reported success=false.");
            }

            if (TryExtractProviderUrl(doc.RootElement, out var providerUrl))
            {
                return providerUrl;
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Monochrome Qobuz download response was not valid JSON.");
        }

        throw new InvalidOperationException("Monochrome Qobuz download response did not contain a usable stream URL.");
    }

    private async Task<string?> TryGetMusicDlStreamUrlAsync(
        string endpoint,
        long trackId,
        string qualityCode,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            quality = MapMusicDlQuality(qualityCode),
            upload_to_r2 = false,
            url = $"https://open.qobuz.com/track/{trackId}"
        };

        using var response = await SendProviderRequestWithRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(payload, SerializerOptions),
                        Encoding.UTF8,
                        ApplicationJsonContentType)
                };
                request.Headers.TryAddWithoutValidation("User-Agent", MusicDlUserAgent);
                request.Headers.TryAddWithoutValidation("Accept", ApplicationJsonContentType);
                return request;
            },
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"MusicDL provider returned HTTP {(int)response.StatusCode}");
        }

        var body = await ReadProviderResponseBodyAsync(response, "MusicDL provider", cancellationToken);
        if (TryExtractCommonProviderUrlPayload(body, "MusicDL provider", out var directUrl))
        {
            return directUrl;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("success", out var successProp)
                && successProp.ValueKind == JsonValueKind.False)
            {
                if (doc.RootElement.TryGetProperty("message", out var messageProp)
                    && messageProp.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(messageProp.GetString()))
                {
                    throw new InvalidOperationException(messageProp.GetString()!);
                }

                throw new InvalidOperationException("MusicDL provider reported success=false.");
            }

            if (doc.RootElement.TryGetProperty("error", out var errorProp)
                && errorProp.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(errorProp.GetString()))
            {
                throw new InvalidOperationException(errorProp.GetString()!);
            }

            if (doc.RootElement.TryGetProperty("detail", out var detailProp)
                && detailProp.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(detailProp.GetString()))
            {
                throw new InvalidOperationException(detailProp.GetString()!);
            }

            if (TryExtractProviderUrl(doc.RootElement, out var providerUrl))
            {
                return providerUrl;
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("MusicDL provider response was not valid JSON.");
        }

        throw new InvalidOperationException("MusicDL provider response did not contain a usable stream URL.");
    }

    private static string MapMusicDlQuality(string qualityCode)
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
        foreach (var propertyName in ProviderUrlPropertyNames)
        {
            if (element.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String)
            {
                url = property.GetString();
                return !string.IsNullOrWhiteSpace(url);
            }
        }

        url = null;
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
            var durationSeconds = tagFile.Properties.Duration.TotalSeconds;
            if (request.DurationSeconds > 0
                && IsSevereDurationMismatch(durationSeconds, request.DurationSeconds))
            {
                return AudioIdentityGuardResult.Fail(
                    $"Audio identity validation failed: output duration is {durationSeconds:F1}s but expected about {request.DurationSeconds}s.");
            }

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

    private static bool IsSevereDurationMismatch(double actualSeconds, int expectedSeconds)
    {
        if (expectedSeconds <= 0 || actualSeconds <= 0)
        {
            return false;
        }

        var ratio = actualSeconds / expectedSeconds;
        if (ratio < 0.55d || ratio > 1.45d)
        {
            return true;
        }

        return Math.Abs(actualSeconds - expectedSeconds) > 120d;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private sealed record AudioIdentityGuardResult(bool Success, string Message)
    {
        public static AudioIdentityGuardResult Ok() => new(true, string.Empty);
        public static AudioIdentityGuardResult Fail(string message) => new(false, message);
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

    private static void CleanUnverifiedExpectedOutput(string expectedPath)
    {
        if (string.IsNullOrWhiteSpace(expectedPath))
        {
            return;
        }

        DownloadFileUtilities.TryDeleteFile(expectedPath);
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

}
