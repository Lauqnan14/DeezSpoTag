using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using IOFile = System.IO.File;
using Microsoft.Extensions.Logging;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Download.Shared.Utils;
using TagLib;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Services.Matching;

namespace DeezSpoTag.Services.Download.Tidal;

public sealed class TidalDownloadService
{
    private const string AudioKeyword = "audio";
    private const string ManifestPrefix = "MANIFEST:";
    private const string TidalPublicApiHost = "tidal.com";
    private const string TidalPublicApiBasePath = "v1";
    private const string TidalListenHost = "listen.tidal.com";
    private const string TidalListenTrackPathPrefix = "track";
    // Sonar exception policy: this is the only allowed hardcoded token exception (public partner token).
    [SuppressMessage("Security", "S6418", Justification = "Only allowed exception: public Tidal partner token, not a private credential.")]
    private const string TidalPublicToken = "txNoH4kkV41MfH25";
    private const string TidalPublicCountryCode = "US";
    private const string TidalPublicLocale = "en_US";
    private const string TidalPublicDeviceType = "BROWSER";
    private const int MaxConcurrentProviderResolutions = 2;
    private static readonly string[] PlaylistLineSeparators = { "\r\n", "\n" };
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly SemaphoreSlim ProviderResolutionGate = new(MaxConcurrentProviderResolutions, MaxConcurrentProviderResolutions);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static Match MatchWithTimeout(string input, string pattern, RegexOptions options = RegexOptions.None)
        => Regex.Match(input, pattern, options, RegexTimeout);
    private static MatchCollection MatchesWithTimeout(string input, string pattern, RegexOptions options = RegexOptions.None)
        => Regex.Matches(input, pattern, options, RegexTimeout);

    private readonly ILogger<TidalDownloadService> _logger;
    private readonly HttpClient _client;
    private readonly TidalApiProviderSource _providerSource;
    private readonly SpotifyTrackMetadataResolver? _spotifyTrackMetadataResolver;
    private readonly ITidalAccessTokenProvider _accessTokenProvider;

    public TidalDownloadService(
        ILogger<TidalDownloadService> logger,
        TidalApiProviderSource providerSource,
        ITidalAccessTokenProvider accessTokenProvider,
        SpotifyTrackMetadataResolver? spotifyTrackMetadataResolver = null)
    {
        _logger = logger;
        _providerSource = providerSource;
        _accessTokenProvider = accessTokenProvider;
        _spotifyTrackMetadataResolver = spotifyTrackMetadataResolver;
        _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<string> DownloadAsync(
        TidalDownloadRequest request,
        bool embedMaxQualityCover,
        DeezSpoTag.Core.Models.Settings.TagSettings? tagSettings,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        if (request.IsVideo)
        {
            return await DownloadVideoAsync(request, progressCallback, cancellationToken);
        }

        Directory.CreateDirectory(request.OutputDir);
        string? tidalUrl = request.ServiceUrl;
        if (string.IsNullOrWhiteSpace(tidalUrl)
            && long.TryParse(request.TidalId, out var tidalTrackId)
            && tidalTrackId > 0)
        {
            tidalUrl = BuildTidalTrackListenUrl(tidalTrackId);
        }

        if (string.IsNullOrWhiteSpace(tidalUrl) && !string.IsNullOrWhiteSpace(request.SpotifyId))
        {
            tidalUrl = await GetTidalUrlFromSpotifyAsync(request.SpotifyId, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(tidalUrl))
        {
            try
            {
                return await DownloadByUrlAsync(
                    request,
                    tidalUrl,
                    progressCallback,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is InvalidOperationException)
            {
                _logger.LogWarning(ex, "Tidal URL download failed. Url={Url}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(tidalUrl));
                throw new InvalidOperationException($"Tidal URL download failed: {ex.Message}", ex);
            }
        }

        throw new InvalidOperationException("Tidal download requires a valid Tidal ID, service URL, or Spotify ID for native link regeneration.");
    }

    public Task<string> ResolveVideoStreamUrlAsync(long videoId, CancellationToken cancellationToken)
    {
        if (videoId <= 0)
        {
            throw new InvalidOperationException("Invalid Tidal video ID");
        }

        return GetVideoStreamUrlAsync(videoId, 1080, cancellationToken);
    }

    private async Task<string> DownloadVideoAsync(
        TidalDownloadRequest request,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        var tidalUrl = request.ServiceUrl;
        if (string.IsNullOrWhiteSpace(tidalUrl))
        {
            throw new InvalidOperationException("Tidal video download requires a valid video URL.");
        }

        var videoId = GetVideoIdFromUrl(tidalUrl);
        var outputRoot = !string.IsNullOrWhiteSpace(request.VideoOutputRoot)
            ? DownloadPathResolver.ResolveIoPath(request.VideoOutputRoot)
            : request.OutputDir;
        Directory.CreateDirectory(outputRoot);

        var outputPath = BuildTidalVideoOutputPath(outputRoot, request, videoId);
        await EnsureFinalDestinationAllowedAsync(request, outputPath, cancellationToken);

        var streamUrl = await GetVideoStreamUrlAsync(videoId, request.VideoMaxResolution, cancellationToken);
        await DownloadVideoStreamWithFfmpegAsync(streamUrl, outputPath, progressCallback, cancellationToken);
        return outputPath;
    }

    public async Task<string?> ResolveTrackUrlAsync(
        string trackTitle,
        string artistName,
        string isrc,
        int expectedDuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var trackInfo = await SearchTrackByMetadataWithIsrcAsync(
                trackTitle,
                artistName,
                isrc,
                expectedDuration,
                cancellationToken);
            if (trackInfo == null || trackInfo.Id <= 0)
            {
                return null;
            }

            return BuildTidalTrackListenUrl(trackInfo.Id);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Tidal metadata resolution failed for {Title} - {Artist}",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(trackTitle),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(artistName));
            return null;
        }
    }

    public async Task<string?> ResolveAtmosTrackUrlAsync(
        string trackTitle,
        string artistName,
        string isrc,
        int expectedDuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var trackInfo = await SearchAtmosTrackByMetadataWithIsrcAsync(
                trackTitle,
                artistName,
                isrc,
                expectedDuration,
                cancellationToken);
            if (trackInfo == null || trackInfo.Id <= 0)
            {
                return null;
            }

            return BuildTidalTrackListenUrl(trackInfo.Id);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Tidal Atmos metadata resolution failed for {Title} - {Artist}",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(trackTitle),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(artistName));
            return null;
        }
    }

    private async Task<string> DownloadByUrlAsync(
        TidalDownloadRequest request,
        string tidalUrl,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        var trackId = GetTrackIdFromUrl(tidalUrl);
        var trackInfo = await GetTrackInfoByIdAsync(trackId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.Isrc)
            && !string.Equals(trackInfo.Isrc, request.Isrc, StringComparison.OrdinalIgnoreCase)
            && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "ISRC mismatch for Tidal URL download: expected {ExpectedIsrc}, got {ActualIsrc}. Proceeding with URL-specified track.",
                request.Isrc,
                trackInfo.Isrc);
        }

        var outputPathContext = new AudioFilePathHelper.AudioPathContext
        {
            OutputDir = request.OutputDir,
            Title = request.TrackName,
            Artist = request.ArtistName,
            Album = request.AlbumName,
            AlbumArtist = request.AlbumArtist,
            ReleaseDate = request.ReleaseDate,
            TrackNumber = request.SpotifyTrackNumber,
            DiscNumber = request.SpotifyDiscNumber,
            FilenameFormat = request.FilenameFormat,
            IncludeTrackNumber = request.IncludeTrackNumber,
            Position = request.Position,
            UseAlbumTrackNumber = request.UseAlbumTrackNumber,
            Sanitize = value => DownloadFileUtilities.SanitizeFilename(value)
        };
        var isAtmosRequest = IsTidalAtmosRequest(request);
        var outputPath = AudioFilePathHelper.BuildOutputPath(outputPathContext, isAtmosRequest ? ".m4a" : ".flac");
        await EnsureFinalDestinationAllowedAsync(request, outputPath, cancellationToken);

        var candidateUrls = await GetDownloadUrlCandidatesAsync(trackInfo.Id, request.Quality, cancellationToken);
        var expectedDurationSeconds = ResolveExpectedDurationSeconds(request.DurationSeconds, trackInfo.Duration);
        await DownloadValidatedFileAsync(
            candidateUrls,
            outputPath,
            expectedDurationSeconds,
            isAtmosRequest,
            progressCallback,
            cancellationToken);
        return outputPath;
    }

    private static long GetVideoIdFromUrl(string tidalUrl)
    {
        if (!Uri.TryCreate(tidalUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Invalid Tidal video URL");
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!segments[i].Equals("video", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (long.TryParse(segments[i + 1], out var id) && id > 0)
            {
                return id;
            }
        }

        throw new InvalidOperationException("Invalid Tidal video ID");
    }

    private static string BuildTidalVideoOutputPath(string outputRoot, TidalDownloadRequest request, long videoId)
    {
        var artist = DownloadFileUtilities.SanitizeFilename(
            string.IsNullOrWhiteSpace(request.ArtistName) ? "Unknown Artist" : request.ArtistName);
        var title = DownloadFileUtilities.SanitizeFilename(
            string.IsNullOrWhiteSpace(request.TrackName) ? $"Tidal Video {videoId}" : request.TrackName);
        return Path.Join(outputRoot, $"{artist} - {title}.mp4");
    }

    private async Task<string> GetTidalUrlFromSpotifyAsync(string spotifyId, CancellationToken cancellationToken)
    {
        if (_spotifyTrackMetadataResolver == null)
        {
            throw new InvalidOperationException("Spotify metadata resolver is not available for Tidal URL regeneration.");
        }

        var spotifyTrack = await _spotifyTrackMetadataResolver.ResolveTrackAsync(spotifyId, cancellationToken);
        if (spotifyTrack == null
            || string.IsNullOrWhiteSpace(spotifyTrack.Title)
            || string.IsNullOrWhiteSpace(spotifyTrack.Artist))
        {
            throw new InvalidOperationException("Unable to hydrate Spotify metadata for Tidal link regeneration.");
        }

        var expectedDuration = spotifyTrack.DurationMs.HasValue && spotifyTrack.DurationMs.Value > 0
            ? (int)Math.Round(spotifyTrack.DurationMs.Value / 1000d)
            : 0;

        var resolved = await ResolveTrackUrlAsync(
            spotifyTrack.Title,
            spotifyTrack.Artist,
            spotifyTrack.Isrc ?? string.Empty,
            expectedDuration,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException("Tidal URL regeneration failed for the provided Spotify track.");
        }

        return resolved;
    }

    private async Task<TidalTrack> SearchTrackByMetadataWithIsrcAsync(string trackName, string artistName, string isrc, int expectedDuration, CancellationToken cancellationToken)
    {
        var queries = BuildSearchQueries(trackName, artistName);
        var allTracks = new List<TidalTrack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in queries)
        {
            if (!seen.Add(query))
            {
                continue;
            }
            var result = await SearchTracksAsync(query, 100, cancellationToken);
            if (result.Count > 0)
            {
                allTracks.AddRange(result);
            }
        }

        if (allTracks.Count == 0)
        {
            throw new InvalidOperationException("No tracks found");
        }

        var isrcMatch = FindIsrcMatch(allTracks, isrc);
        if (isrcMatch != null)
        {
            return isrcMatch;
        }

        var validatedMatch = FindValidatedMetadataMatch(allTracks, trackName, artistName, isrc, expectedDuration);
        if (validatedMatch != null)
        {
            return validatedMatch;
        }

        throw new InvalidOperationException("No validated Tidal track match found");
    }

    private async Task<TidalTrack> SearchAtmosTrackByMetadataWithIsrcAsync(
        string trackName,
        string artistName,
        string isrc,
        int expectedDuration,
        CancellationToken cancellationToken)
    {
        var queries = BuildSearchQueries(trackName, artistName);
        var allTracks = new List<TidalTrack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in queries)
        {
            if (!seen.Add(query))
            {
                continue;
            }

            var result = await SearchTracksAsync(query, 100, cancellationToken);
            if (result.Count > 0)
            {
                allTracks.AddRange(result.Where(HasTidalAtmosMode));
            }
        }

        if (allTracks.Count == 0)
        {
            throw new InvalidOperationException("No Tidal Atmos tracks found");
        }

        var isrcMatch = FindIsrcMatch(allTracks, isrc);
        if (isrcMatch != null)
        {
            return isrcMatch;
        }

        var validatedMatch = FindValidatedMetadataMatch(allTracks, trackName, artistName, isrc, expectedDuration);
        if (validatedMatch != null)
        {
            return validatedMatch;
        }

        throw new InvalidOperationException("No validated Tidal Atmos track match found");
    }

    private static bool HasTidalAtmosMode(TidalTrack track)
    {
        return track.AudioModes?.Any(static mode =>
            string.Equals(mode, "DOLBY_ATMOS", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static List<string> BuildSearchQueries(string trackName, string artistName)
    {
        var queries = new List<string>();
        if (!string.IsNullOrWhiteSpace(artistName) && !string.IsNullOrWhiteSpace(trackName))
        {
            queries.Add($"{artistName} {trackName}");
        }

        if (!string.IsNullOrWhiteSpace(trackName))
        {
            queries.Add(trackName);
        }

        if (!string.IsNullOrWhiteSpace(artistName))
        {
            queries.Add(artistName);
        }

        return queries;
    }

    private TidalTrack? FindIsrcMatch(List<TidalTrack> allTracks, string isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        var match = allTracks.FirstOrDefault(track => string.Equals(track.Isrc, isrc, StringComparison.OrdinalIgnoreCase));
        if (match == null && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("No ISRC match for {Isrc}, falling back to duration/title matching", DeezSpoTag.Core.Security.LogSanitizer.OneLine(isrc));
        }

        return match;
    }

    private TidalTrack? FindValidatedMetadataMatch(
        List<TidalTrack> allTracks,
        string trackName,
        string artistName,
        string isrc,
        int expectedDuration)
    {
        var source = new TrackMatchSource(
            isrc,
            trackName,
            artistName,
            Album: null,
            expectedDuration > 0 ? expectedDuration * 1000 : null);
        var options = new TrackCandidateValidationOptions(
            StrictWithoutIsrc: true,
            AllowMissingCandidateArtist: true,
            RequireCandidateDurationWhenSourceHasDuration: true,
            MaxIsrcDurationDifferenceMs: 20_000,
            MaxMetadataDurationDifferenceMs: 3_000);

        TidalTrack? bestTrack = null;
        var bestScore = double.MinValue;
        foreach (var track in allTracks.Where(static track => track.Id > 0))
        {
            var validation = TrackCandidateValidator.Validate(
                source,
                new TrackMatchCandidate(
                    track.Id.ToString(CultureInfo.InvariantCulture),
                    track.Isrc,
                    track.Title,
                    ResolveTidalArtistName(track),
                    track.Album?.Title,
                    track.Duration > 0 ? track.Duration * 1000 : null),
                options);
            if (!validation.Accepted)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Rejected Tidal candidate id={TrackId} reason={Reason}",
                        track.Id,
                        validation.Reason);
                }
                continue;
            }

            if (validation.Score > bestScore)
            {
                bestScore = validation.Score;
                bestTrack = track;
            }
        }

        return bestTrack;
    }

    private static string ResolveTidalArtistName(TidalTrack track)
    {
        if (!string.IsNullOrWhiteSpace(track.Artist?.Name))
        {
            return track.Artist.Name;
        }

        if (track.Artists is { Count: > 0 })
        {
            return string.Join(", ", track.Artists
                .Select(static artist => artist.Name)
                .Where(static name => !string.IsNullOrWhiteSpace(name)));
        }

        return string.Empty;
    }

    private async Task<List<TidalTrack>> SearchTracksAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var oauthResult = await SearchTracksViaOauthAsync(query, limit, cancellationToken);
        if (oauthResult.Count > 0)
        {
            return oauthResult;
        }

        return await SearchTracksViaPublicApiAsync(query, limit, cancellationToken);
    }

    private static long GetTrackIdFromUrl(string tidalUrl)
    {
        var match = MatchWithTimeout(tidalUrl, @"\/track\/(?<id>\d+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw new InvalidOperationException("Invalid Tidal URL");
        }

        if (!long.TryParse(match.Groups["id"].Value, out var trackId))
        {
            throw new InvalidOperationException("Invalid Tidal track ID");
        }

        return trackId;
    }

    private async Task<TidalTrack> GetTrackInfoByIdAsync(long trackId, CancellationToken cancellationToken)
    {
        var oauthTrack = await TryGetTrackInfoByIdViaOauthAsync(trackId, cancellationToken);
        if (oauthTrack != null)
        {
            return oauthTrack;
        }

        var publicTrack = await TryGetTrackInfoByIdViaPublicApiAsync(trackId, cancellationToken);
        if (publicTrack != null)
        {
            return publicTrack;
        }

        throw new InvalidOperationException($"Tidal track not found for track ID {trackId}.");
    }

    private async Task<List<TidalTrack>> SearchTracksViaOauthAsync(string query, int limit, CancellationToken cancellationToken)
    {
        return await SendTidalJsonOrDefaultAsync<TidalSearchResponse, List<TidalTrack>>(
            async _ =>
            {
                var token = await _accessTokenProvider.GetAccessTokenAsync(cancellationToken);
                var countryCode = await _accessTokenProvider.GetCountryCodeAsync(cancellationToken);
                var baseUrl = Encoding.UTF8.GetString(Convert.FromBase64String("aHR0cHM6Ly9hcGkudGlkYWwuY29tL3YxL3NlYXJjaC90cmFja3M/cXVlcnk9"));
                var url = $"{baseUrl}{WebUtility.UrlEncode(query)}&limit={limit}&offset=0&countryCode={countryCode}";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                return request;
            },
            static payload => payload?.Items ?? new List<TidalTrack>(),
            new List<TidalTrack>(),
            ex =>
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Tidal OAuth search failed for query {Query}.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(query));
                }
            },
            cancellationToken);
    }

    private async Task<List<TidalTrack>> SearchTracksViaPublicApiAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var url = BuildTidalPublicApiUrl(
            "search/tracks",
            new Dictionary<string, string>
            {
                ["query"] = query,
                ["limit"] = limit > 0 ? limit.ToString() : "20",
                ["offset"] = "0"
            });
        var payload = await SendTidalPublicJsonOrDefaultAsync<TidalSearchResponse>(
            url,
            null,
            ex =>
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Tidal public API search failed for query {Query}.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(query));
                }
            },
            cancellationToken);
        return payload?.Items ?? new List<TidalTrack>();
    }

    private async Task<TidalTrack?> TryGetTrackInfoByIdViaPublicApiAsync(long trackId, CancellationToken cancellationToken)
    {
        return await SendTidalPublicJsonOrDefaultAsync<TidalTrack>(
            BuildTidalPublicApiUrl($"tracks/{trackId}"),
            null,
            ex =>
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Tidal public API track lookup failed for track ID {TrackId}.", trackId);
                }
            },
            cancellationToken);
    }

    private async Task<TPayload?> SendTidalPublicJsonOrDefaultAsync<TPayload>(
        string url,
        TPayload? fallback,
        Action<Exception> logFailure,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("x-tidal-token", TidalPublicToken);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await _client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return fallback;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<TPayload>(body, SerializerOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            logFailure(ex);
            return fallback;
        }
        catch (IOException ex)
        {
            logFailure(ex);
            return fallback;
        }
        catch (JsonException ex)
        {
            logFailure(ex);
            return fallback;
        }
        catch (InvalidOperationException ex)
        {
            logFailure(ex);
            return fallback;
        }
    }

    private async Task<TidalTrack?> TryGetTrackInfoByIdViaOauthAsync(long trackId, CancellationToken cancellationToken)
    {
        return await SendTidalJsonOrDefaultAsync<TidalTrack, TidalTrack?>(
            async _ =>
            {
                var token = await _accessTokenProvider.GetAccessTokenAsync(cancellationToken);
                var countryCode = await _accessTokenProvider.GetCountryCodeAsync(cancellationToken);
                var baseUrl = Encoding.UTF8.GetString(Convert.FromBase64String("aHR0cHM6Ly9hcGkudGlkYWwuY29tL3YxL3RyYWNrcy8="));
                var url = $"{baseUrl}{trackId}?countryCode={countryCode}";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                return request;
            },
            static payload => payload,
            null,
            ex =>
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Tidal OAuth track lookup failed for track ID {TrackId}.", trackId);
                }
            },
            cancellationToken);
    }

    private async Task<TResult> SendTidalJsonOrDefaultAsync<TPayload, TResult>(
        Func<CancellationToken, Task<HttpRequestMessage>> requestFactory,
        Func<TPayload?, TResult> mapPayload,
        TResult fallback,
        Action<Exception> logFailure,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = await requestFactory(cancellationToken);
            using var response = await _client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return fallback;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var payload = JsonSerializer.Deserialize<TPayload>(body, SerializerOptions);
            return mapPayload(payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logFailure(ex);
            return fallback;
        }
    }

    private static string BuildTidalPublicApiUrl(string path, IDictionary<string, string>? query = null)
    {
        var builder = new UriBuilder(Uri.UriSchemeHttps, TidalPublicApiHost)
        {
            Path = $"{TidalPublicApiBasePath}/{path.TrimStart('/')}"
        };
        var allQuery = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["countryCode"] = TidalPublicCountryCode,
            ["locale"] = TidalPublicLocale,
            ["deviceType"] = TidalPublicDeviceType
        };

        if (query != null)
        {
            foreach (var pair in query)
            {
                allQuery[pair.Key] = pair.Value;
            }
        }

        builder.Query = string.Join(
            "&",
            allQuery
                .Where(static pair => !string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => $"{WebUtility.UrlEncode(pair.Key)}={WebUtility.UrlEncode(pair.Value)}"));

        return builder.Uri.ToString();
    }

    private static string BuildTidalTrackListenUrl(long trackId)
    {
        return new UriBuilder(Uri.UriSchemeHttps, TidalListenHost)
        {
            Path = $"{TidalListenTrackPathPrefix}/{trackId}"
        }.Uri.ToString();
    }

    private async Task<string?> FetchManifestFromApiAsync(
        string apiBase,
        long trackId,
        string quality,
        CancellationToken cancellationToken)
    {
        return await FetchManifestFromLegacyTrackEndpointAsync(apiBase, trackId, quality, cancellationToken)
            ?? await FetchManifestFromTrackManifestsEndpointAsync(apiBase, trackId, quality, cancellationToken);
    }

    private async Task<string?> FetchManifestFromProviderAsync(
        TidalPublicProvider provider,
        long trackId,
        string quality,
        CancellationToken cancellationToken)
    {
        await ProviderResolutionGate.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(provider.Kind, TidalPublicProviderDefaults.ZarzProviderKind, StringComparison.OrdinalIgnoreCase))
            {
                return await FetchManifestFromZarzAsync(provider.Endpoint, trackId, quality, cancellationToken);
            }

            return await FetchManifestFromApiAsync(provider.Endpoint, trackId, quality, cancellationToken);
        }
        finally
        {
            ProviderResolutionGate.Release();
        }
    }

    private async Task<string?> FetchManifestFromZarzAsync(
        string endpoint,
        long trackId,
        string quality,
        CancellationToken cancellationToken)
    {
        var normalizedQuality = NormalizeTidalDownloadQuality(quality);
        if (string.Equals(normalizedQuality, "DOLBY_ATMOS", StringComparison.OrdinalIgnoreCase))
        {
            return await FetchAtmosManifestFromZarzAsync(endpoint, trackId, cancellationToken);
        }

        var payload = new
        {
            id = trackId.ToString(CultureInfo.InvariantCulture),
            quality = normalizedQuality
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SerializerOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("User-Agent", "SpotiFLAC-Mobile/4.6.0");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await _client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Tidal Zarz provider returned HTTP {(int)response.StatusCode}.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Tidal Zarz provider returned an empty response.");
        }

        if (BodyContainsPreviewAsset(body))
        {
            throw new InvalidOperationException("Tidal Zarz provider returned a preview asset.");
        }

        return TryParseManifest(body, out var manifest) ? manifest : null;
    }

    private async Task<string?> FetchAtmosManifestFromZarzAsync(
        string endpoint,
        long trackId,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            id = trackId.ToString(CultureInfo.InvariantCulture),
            endpoint = "manifests",
            formats = new[] { "EAC3_JOC" }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SerializerOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("User-Agent", "SpotiFLAC-Mobile/4.6.0");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await _client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Tidal Zarz Atmos provider returned HTTP {(int)response.StatusCode}.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!TryExtractZarzAtmosManifestUri(body, out var manifestUri))
        {
            throw new InvalidOperationException("Tidal Zarz Atmos provider did not return a manifest URI.");
        }

        using var manifestResponse = await _client.GetAsync(manifestUri, cancellationToken);
        if (!manifestResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Tidal Zarz Atmos manifest fetch returned HTTP {(int)manifestResponse.StatusCode}.");
        }

        var manifestText = await manifestResponse.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(manifestText)
            ? null
            : ManifestPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(manifestText));
    }

    private async Task<string?> FetchManifestFromLegacyTrackEndpointAsync(
        string apiBase,
        long trackId,
        string quality,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{apiBase}/track/?id={trackId}&quality={quality}";
            using var response = await _client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return TryParseManifest(body, out var manifest) ? manifest : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Tidal legacy API {Api} failed for track {TrackId}", apiBase, trackId);
            }

            return null;
        }
    }

    private async Task<string?> FetchManifestFromTrackManifestsEndpointAsync(
        string apiBase,
        long trackId,
        string quality,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = BuildTrackManifestsUrl(apiBase, trackId, quality);
            using var response = await _client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (TryParseManifest(body, out var manifest))
            {
                return manifest;
            }

            if (!TryExtractManifestUri(body, out var manifestUri))
            {
                return null;
            }

            using var manifestResponse = await _client.GetAsync(manifestUri, cancellationToken);
            if (!manifestResponse.IsSuccessStatusCode)
            {
                return null;
            }

            var manifestText = await manifestResponse.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(manifestText))
            {
                return null;
            }

            return ManifestPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(manifestText));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Tidal trackManifests API {Api} failed for track {TrackId}", apiBase, trackId);
            }

            return null;
        }
    }

    private static string BuildTrackManifestsUrl(string apiBase, long trackId, string quality)
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("id", trackId.ToString()),
            new("quality", string.IsNullOrWhiteSpace(quality) ? "LOSSLESS" : quality),
            new("adaptive", "false"),
            new("formats", "FLAC_HIRES"),
            new("formats", "FLAC"),
            new("formats", "AACLC")
        };

        var encodedQuery = string.Join(
            "&",
            query.Select(pair => $"{WebUtility.UrlEncode(pair.Key)}={WebUtility.UrlEncode(pair.Value)}"));
        return $"{apiBase.TrimEnd('/')}/trackManifests/?{encodedQuery}";
    }

    private async Task DownloadFileAsync(string url, string outputPath, Func<double, double, Task>? progressCallback, CancellationToken cancellationToken)
    {
        if (url.StartsWith(ManifestPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await DownloadFromManifestAsync(url, outputPath, preserveManifestAudio: false, progressCallback, cancellationToken);
            return;
        }

        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = IOFile.Create(outputPath);
        await DownloadStreamHelper.CopyToAsyncWithProgress(stream, file, response.Content.Headers.ContentLength, progressCallback, cancellationToken);
    }

    private static async Task EnsureFinalDestinationAllowedAsync(
        TidalDownloadRequest request,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var decision = await DownloadDedupeService.CheckFinalDestinationAsync(
            DownloadDedupeService.FromEngineDownloadRequest(request, outputPath),
            cancellationToken);
        if (!decision.Allowed)
        {
            throw new InvalidOperationException(decision.Message ?? "Tidal final destination rejected by dedupe.");
        }
    }

    private async Task DownloadValidatedFileAsync(
        IReadOnlyList<string> candidateUrls,
        string outputPath,
        int expectedDurationSeconds,
        bool preserveManifestAudio,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        List<string>? mismatchSummaries = null;
        for (var index = 0; index < candidateUrls.Count; index++)
        {
            var candidateUrl = candidateUrls[index];
            var candidateOutputPath = candidateUrls.Count == 1
                ? outputPath
                : $"{outputPath}.candidate-{index + 1}.tmp";
            try
            {
                await DownloadManifestCandidateAsync(candidateUrl, candidateOutputPath, preserveManifestAudio, progressCallback, cancellationToken);
                if (IsDownloadedCandidateAcceptable(candidateOutputPath, expectedDurationSeconds, preserveManifestAudio, out var actualDurationSeconds))
                {
                    if (!string.Equals(candidateOutputPath, outputPath, StringComparison.Ordinal))
                    {
                        IOFile.Move(candidateOutputPath, outputPath, overwrite: true);
                    }

                    return;
                }

                mismatchSummaries ??= new List<string>();
                mismatchSummaries.Add($"{actualDurationSeconds:F1}s");
                _logger.LogWarning(
                    "Rejected Tidal download candidate for {Output}: expected about {ExpectedDuration}s, got {ActualDuration:F1}s.",
                    outputPath,
                    expectedDurationSeconds,
                    actualDurationSeconds);
                DeleteCandidateArtifacts(candidateOutputPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DeleteCandidateArtifacts(candidateOutputPath);
                throw;
            }
            finally
            {
                if (!string.Equals(candidateOutputPath, outputPath, StringComparison.Ordinal))
                {
                    DeleteCandidateArtifacts(candidateOutputPath);
                }
            }
        }

        if (mismatchSummaries?.Count > 0)
        {
            throw new InvalidOperationException(
                $"Tidal download candidates resolved to the wrong duration. Expected about {expectedDurationSeconds}s, got {string.Join(", ", mismatchSummaries)}.");
        }

        throw new InvalidOperationException("Tidal download failed before any audio candidate completed.");
    }

    private async Task DownloadManifestCandidateAsync(
        string candidateUrl,
        string candidateOutputPath,
        bool preserveManifestAudio,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        if (candidateUrl.StartsWith(ManifestPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await DownloadFromManifestAsync(candidateUrl, candidateOutputPath, preserveManifestAudio, progressCallback, cancellationToken);
            return;
        }

        if (preserveManifestAudio)
        {
            throw new InvalidOperationException("Tidal Atmos download requires an Atmos manifest; direct stereo assets are not accepted.");
        }

        await DownloadFileAsync(candidateUrl, candidateOutputPath, progressCallback, cancellationToken);
    }

    private static void DeleteCandidateArtifacts(string candidateOutputPath)
    {
        DownloadFileUtilities.TryDeleteFile(candidateOutputPath);

        var preservedSourcePath = Path.ChangeExtension(candidateOutputPath, ".m4a");
        if (!string.Equals(preservedSourcePath, candidateOutputPath, StringComparison.OrdinalIgnoreCase))
        {
            DownloadFileUtilities.TryDeleteFile(preservedSourcePath);
        }
    }

    private async Task DownloadFromManifestAsync(
        string manifestB64,
        string outputPath,
        bool preserveManifestAudio,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        var manifest = ParseManifest(manifestB64);
        if (preserveManifestAudio && !IsAtmosManifest(manifest))
        {
            throw new InvalidOperationException("Tidal Atmos manifest validation failed; provider returned a non-Atmos asset.");
        }

        var (directUrl, initUrl, mediaUrls, mimeType) = manifest;
        if (!string.IsNullOrWhiteSpace(directUrl))
        {
            if (preserveManifestAudio || IsLikelyFlacMimeType(mimeType) || string.IsNullOrWhiteSpace(mimeType))
            {
                await DownloadFileAsync(directUrl, outputPath, progressCallback, cancellationToken);
                return;
            }

            var directTempPath = outputPath + ".m4a.tmp";
            await DownloadFileAsync(directUrl, directTempPath, progressCallback, cancellationToken);
            await ConvertTempToFlacAsync(directTempPath, outputPath, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(initUrl) || mediaUrls.Count == 0)
        {
            throw new InvalidOperationException("Invalid manifest");
        }

        var tempPath = preserveManifestAudio ? outputPath : outputPath + ".m4a.tmp";
        await using var output = IOFile.Create(tempPath);
        var totalSegments = 1 + mediaUrls.Count;
        var completed = 0;

        if (progressCallback != null)
        {
            await progressCallback(0, 0);
        }

        await DownloadSegmentAsync(initUrl, output, cancellationToken);
        completed++;
        if (progressCallback != null)
        {
            await progressCallback(completed * 100d / totalSegments, 0);
        }

        foreach (var media in mediaUrls)
        {
            await DownloadSegmentAsync(media, output, cancellationToken);
            completed++;
            if (progressCallback != null)
            {
                await progressCallback(completed * 100d / totalSegments, 0);
            }
        }

        output.Close();
        if (preserveManifestAudio)
        {
            return;
        }

        await ConvertTempToFlacAsync(tempPath, outputPath, cancellationToken);
    }

    private async Task DownloadSegmentAsync(string url, Stream output, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await stream.CopyToAsync(output, cancellationToken);
    }

    private static TidalManifestInfo ParseManifest(string manifestPayload)
    {
        var manifestStr = TryDecodeManifest(manifestPayload);
        if (TryParseBtsManifest(manifestStr, out var btsManifest))
        {
            return btsManifest with { RawText = manifestStr };
        }

        if (TryParseDashTemplate(manifestStr, out var initUrl, out var mediaTemplate, out var startNumber, out var segmentCount, out var dashMimeType, out var dashCodecs))
        {
            return new TidalManifestInfo(
                string.Empty,
                initUrl,
                BuildDashMediaUrls(mediaTemplate, startNumber, segmentCount),
                dashMimeType,
                dashCodecs,
                manifestStr);
        }

        return ParseDashFallbackManifest(manifestStr);
    }

    private static bool TryParseBtsManifest(
        string manifestStr,
        out TidalManifestInfo manifest)
    {
        manifest = TidalManifestInfo.Empty;
        if (!manifestStr.StartsWith('{'))
        {
            return false;
        }

        var bts = JsonSerializer.Deserialize<TidalBtsManifest>(manifestStr, SerializerOptions);
        if (bts?.Urls == null || bts.Urls.Count == 0)
        {
            throw new InvalidOperationException("No URLs in manifest");
        }

        manifest = new TidalManifestInfo(
            bts.Urls[0],
            string.Empty,
            new List<string>(),
            bts.MimeType ?? string.Empty,
            bts.Codecs ?? string.Empty,
            manifestStr);
        return true;
    }

    private static List<string> BuildDashMediaUrls(string mediaTemplate, int startNumber, int segmentCount)
    {
        var mediaUrls = new List<string>(segmentCount);
        for (var i = 0; i < segmentCount; i++)
        {
            var segmentNumber = startNumber + i;
            mediaUrls.Add(ReplaceNumberPlaceholder(mediaTemplate, segmentNumber));
        }

        return mediaUrls;
    }

    private static TidalManifestInfo ParseDashFallbackManifest(string manifestStr)
    {
        var initRe = MatchWithTimeout(manifestStr, "initialization=\"([^\"]+)\"");
        var mediaRe = MatchWithTimeout(manifestStr, "media=\"([^\"]+)\"");
        var initFallback = initRe.Success ? DecodeXmlUrl(initRe.Groups[1].Value) : "";
        var mediaFallback = mediaRe.Success ? DecodeXmlUrl(mediaRe.Groups[1].Value) : "";
        if (string.IsNullOrWhiteSpace(initFallback) || string.IsNullOrWhiteSpace(mediaFallback))
        {
            throw new InvalidOperationException("Invalid DASH manifest");
        }

        var countFallback = CountFallbackSegments(manifestStr);
        var mediaUrlsFallback = new List<string>(countFallback);
        for (var i = 0; i < countFallback; i++)
        {
            mediaUrlsFallback.Add(ReplaceNumberPlaceholder(mediaFallback, i + 1));
        }

        return new TidalManifestInfo(
            string.Empty,
            initFallback,
            mediaUrlsFallback,
            ExtractDashMimeType(manifestStr),
            ExtractDashCodecs(manifestStr),
            manifestStr);
    }

    private static int CountFallbackSegments(string manifestStr)
    {
        var count = 0;
        var segmentTags = MatchesWithTimeout(manifestStr, "<S\\b[^>]*>", RegexOptions.IgnoreCase);
        foreach (Match tag in segmentTags)
        {
            if (!tag.Success)
            {
                continue;
            }

            var repeatMatch = MatchWithTimeout(tag.Value, "\\br=\"(-?\\d+)\"", RegexOptions.IgnoreCase);
            var repeat = repeatMatch.Success && int.TryParse(repeatMatch.Groups[1].Value, out var parsedRepeat)
                ? Math.Max(0, parsedRepeat)
                : 0;
            count += repeat + 1;
        }

        return count <= 0 ? 1 : count;
    }

    private static bool TryParseDashTemplate(
        string manifestXml,
        out string initUrl,
        out string mediaTemplate,
        out int startNumber,
        out int segmentCount,
        out string mimeType,
        out string codecs)
    {
        initUrl = string.Empty;
        mediaTemplate = string.Empty;
        startNumber = 1;
        segmentCount = 0;
        mimeType = string.Empty;
        codecs = string.Empty;

        try
        {
            var doc = new XmlDocument
            {
                XmlResolver = null
            };
            doc.LoadXml(manifestXml);

            var (selectedTemplate, selectedMime, selectedCodecs) = SelectBestAudioSegmentTemplate(doc);

            if (selectedTemplate == null)
            {
                selectedTemplate = doc.SelectSingleNode("//*[local-name()='SegmentTemplate']");
            }

            if (selectedTemplate == null)
            {
                return false;
            }

            (initUrl, mediaTemplate, startNumber, segmentCount, mimeType, codecs) = BuildDashTemplateMetadata(
                selectedTemplate,
                selectedMime,
                selectedCodecs,
                manifestXml);
            return !string.IsNullOrWhiteSpace(initUrl) && !string.IsNullOrWhiteSpace(mediaTemplate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static (XmlNode? Template, string? MimeType, string? Codecs) SelectBestAudioSegmentTemplate(XmlDocument doc)
    {
        var selection = new DashTemplateSelection(null, int.MinValue, null, null);
        var adaptationSets = doc.SelectNodes("//*[local-name()='AdaptationSet']");
        if (adaptationSets == null)
        {
            return (null, null, null);
        }

        foreach (XmlNode adaptationSet in adaptationSets)
        {
            selection = TrySelectAudioTemplateFromAdaptationSet(adaptationSet, selection);
        }

        return (selection.Template, selection.MimeType, selection.Codecs);
    }

    private static DashTemplateSelection TrySelectAudioTemplateFromAdaptationSet(
        XmlNode adaptationSet,
        DashTemplateSelection selection)
    {
        var adaptationMime = adaptationSet.Attributes?["mimeType"]?.Value ?? string.Empty;
        var adaptationContentType = adaptationSet.Attributes?["contentType"]?.Value ?? string.Empty;
        var adaptationLooksAudio = adaptationContentType.Equals(AudioKeyword, StringComparison.OrdinalIgnoreCase)
                                   || adaptationMime.Contains(AudioKeyword, StringComparison.OrdinalIgnoreCase);

        var representations = adaptationSet.SelectNodes("./*[local-name()='Representation']");
        if (representations != null)
        {
            foreach (XmlNode representation in representations)
            {
                selection = TrySelectAudioRepresentationTemplate(
                    representation,
                    adaptationMime,
                    adaptationLooksAudio,
                    selection);
            }
        }

        if (selection.Template != null || !adaptationLooksAudio)
        {
            return selection;
        }

        var adaptationTemplate = adaptationSet.SelectSingleNode("./*[local-name()='SegmentTemplate']");
        if (adaptationTemplate == null)
        {
            return selection;
        }

        return new DashTemplateSelection(adaptationTemplate, 0, adaptationMime, string.Empty);
    }

    private static DashTemplateSelection TrySelectAudioRepresentationTemplate(
        XmlNode representation,
        string adaptationMime,
        bool adaptationLooksAudio,
        DashTemplateSelection selection)
    {
        var templateNode = representation.SelectSingleNode("./*[local-name()='SegmentTemplate']");
        if (templateNode == null)
        {
            return selection;
        }

        var representationMime = representation.Attributes?["mimeType"]?.Value;
        var representationCodecs = representation.Attributes?["codecs"]?.Value;
        var mimeCandidate = !string.IsNullOrWhiteSpace(representationMime)
            ? representationMime
            : adaptationMime;
        var representationLooksAudio = adaptationLooksAudio
            || (!string.IsNullOrWhiteSpace(mimeCandidate)
                && mimeCandidate.Contains(AudioKeyword, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(representationCodecs)
                && IsAudioCodec(representationCodecs));
        if (!representationLooksAudio)
        {
            return selection;
        }

        var bandwidth = ParseIntAttribute(representation.Attributes?["bandwidth"]?.Value, 0);
        if (selection.Template != null && bandwidth <= selection.Bandwidth)
        {
            return selection;
        }

        return new DashTemplateSelection(templateNode, bandwidth, mimeCandidate, representationCodecs);
    }

    private readonly record struct DashTemplateSelection(XmlNode? Template, int Bandwidth, string? MimeType, string? Codecs);

    private static (string InitUrl, string MediaTemplate, int StartNumber, int SegmentCount, string MimeType, string Codecs) BuildDashTemplateMetadata(
        XmlNode selectedTemplate,
        string? selectedMime,
        string? selectedCodecs,
        string manifestXml)
    {
        var initUrl = DecodeXmlUrl(selectedTemplate.Attributes?["initialization"]?.Value ?? string.Empty);
        var mediaTemplate = DecodeXmlUrl(selectedTemplate.Attributes?["media"]?.Value ?? string.Empty);
        var startNumber = ParseIntAttribute(selectedTemplate.Attributes?["startNumber"]?.Value, 1);
        if (startNumber <= 0)
        {
            startNumber = 1;
        }

        var segmentCount = CountDashSegments(selectedTemplate);
        if (segmentCount <= 0 && selectedTemplate.ParentNode != null)
        {
            segmentCount = CountDashSegments(selectedTemplate.ParentNode);
        }

        if (segmentCount <= 0)
        {
            segmentCount = 1;
        }

        var mimeType = !string.IsNullOrWhiteSpace(selectedMime)
            ? selectedMime
            : ExtractDashMimeType(manifestXml);

        var codecs = !string.IsNullOrWhiteSpace(selectedCodecs)
            ? selectedCodecs
            : ExtractDashCodecs(manifestXml);

        return (initUrl, mediaTemplate, startNumber, segmentCount, mimeType, codecs);
    }

    private static int CountDashSegments(XmlNode node)
    {
        var timeline = node.SelectSingleNode("./*[local-name()='SegmentTimeline']");
        if (timeline == null)
        {
            return 0;
        }

        var count = 0;
        var segments = timeline.SelectNodes("./*[local-name()='S']");
        if (segments == null || segments.Count == 0)
        {
            return 0;
        }

        foreach (XmlNode segment in segments)
        {
            var repeat = ParseIntAttribute(segment.Attributes?["r"]?.Value, 0);
            if (repeat < 0)
            {
                repeat = 0;
            }

            count += repeat + 1;
        }

        return count;
    }

    private static int ParseIntAttribute(string? rawValue, int fallback)
    {
        return int.TryParse(rawValue, out var parsed) ? parsed : fallback;
    }

    private static string DecodeXmlUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return raw.Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase).Trim();
    }

    private static string ReplaceNumberPlaceholder(string template, int number)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return template;
        }

        if (template.Contains("$Number$", StringComparison.OrdinalIgnoreCase))
        {
            return template.Replace("$Number$", number.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        var paddedMatch = MatchWithTimeout(template, "\\$Number%0(?<width>\\d+)d\\$", RegexOptions.IgnoreCase);
        if (paddedMatch.Success && int.TryParse(paddedMatch.Groups["width"].Value, out var width) && width > 0)
        {
            var padded = number.ToString().PadLeft(width, '0');
            return template.Replace(paddedMatch.Value, padded, StringComparison.OrdinalIgnoreCase);
        }

        return template;
    }

    private static string ExtractDashMimeType(string manifestXml)
    {
        if (string.IsNullOrWhiteSpace(manifestXml))
        {
            return string.Empty;
        }

        var mimeMatches = MatchesWithTimeout(manifestXml, "mimeType=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        var audioCandidate = mimeMatches
            .Select(static match => match.Groups.Count > 1 ? match.Groups[1].Value : string.Empty)
            .FirstOrDefault(candidate => candidate.Contains(AudioKeyword, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(audioCandidate))
        {
            return audioCandidate;
        }

        return mimeMatches.Count > 0 && mimeMatches[0].Groups.Count > 1
            ? mimeMatches[0].Groups[1].Value
            : string.Empty;
    }

    private static string ExtractDashCodecs(string manifestXml)
    {
        if (string.IsNullOrWhiteSpace(manifestXml))
        {
            return string.Empty;
        }

        var codecMatches = MatchesWithTimeout(manifestXml, "codecs=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return codecMatches.Count > 0 && codecMatches[0].Groups.Count > 1
            ? codecMatches[0].Groups[1].Value
            : string.Empty;
    }

    private static bool IsAudioCodec(string codec)
    {
        return codec.Contains("flac", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("ec-3", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("eac3", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("mp4a", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyFlacMimeType(string mimeType)
    {
        return !string.IsNullOrWhiteSpace(mimeType)
            && mimeType.Contains("flac", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTidalAtmosRequest(TidalDownloadRequest request)
    {
        return string.Equals(NormalizeTidalDownloadQuality(request.Quality), "DOLBY_ATMOS", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAtmosManifest(TidalManifestInfo manifest)
    {
        return ContainsAtmosSignal(manifest.MimeType)
            || ContainsAtmosSignal(manifest.Codecs)
            || ContainsAtmosSignal(manifest.RawText);
    }

    private static bool ContainsAtmosSignal(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && (value.Contains("EAC3_JOC", StringComparison.OrdinalIgnoreCase)
                || value.Contains("DOLBY_ATMOS", StringComparison.OrdinalIgnoreCase)
                || value.Contains("ATMOS", StringComparison.OrdinalIgnoreCase)
                || value.Contains("JOC", StringComparison.OrdinalIgnoreCase));
    }

    private static int ResolveExpectedDurationSeconds(int requestDurationSeconds, int trackInfoDurationSeconds)
    {
        return requestDurationSeconds > 0 ? requestDurationSeconds : Math.Max(0, trackInfoDurationSeconds);
    }

    private static bool IsDownloadedCandidateAcceptable(
        string filePath,
        int expectedDurationSeconds,
        bool preserveManifestAudio,
        out double actualDurationSeconds)
    {
        return preserveManifestAudio
            ? IsAtmosDurationAcceptable(filePath, expectedDurationSeconds, out actualDurationSeconds)
            : IsDurationAcceptable(filePath, expectedDurationSeconds, out actualDurationSeconds);
    }

    private static bool IsDurationAcceptable(string filePath, int expectedDurationSeconds, out double actualDurationSeconds)
    {
        actualDurationSeconds = 0;
        if (expectedDurationSeconds <= 0 || !IOFile.Exists(filePath))
        {
            return true;
        }

        try
        {
            using var file = TagLib.File.Create(filePath);
            actualDurationSeconds = file.Properties.Duration.TotalSeconds;
            if (actualDurationSeconds <= 0)
            {
                return false;
            }

            var allowedDelta = Math.Max(5d, expectedDurationSeconds * 0.12d);
            return Math.Abs(actualDurationSeconds - expectedDurationSeconds) <= allowedDelta;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private static bool IsAtmosDurationAcceptable(string filePath, int expectedDurationSeconds, out double actualDurationSeconds)
    {
        actualDurationSeconds = 0;
        if (expectedDurationSeconds <= 0 || !IOFile.Exists(filePath))
        {
            return true;
        }

        if (!TryReadFfprobeAtmosAudio(filePath, out actualDurationSeconds))
        {
            return false;
        }

        var allowedDelta = Math.Max(5d, expectedDurationSeconds * 0.12d);
        return Math.Abs(actualDurationSeconds - expectedDurationSeconds) <= allowedDelta;
    }

    private static bool TryReadFfprobeAtmosAudio(string filePath, out double durationSeconds)
    {
        durationSeconds = 0;
        var ffprobePath = ResolveFfprobePath();
        if (string.IsNullOrWhiteSpace(ffprobePath))
        {
            return false;
        }

        try
        {
            var startInfo = ExternalToolProcessStartInfo.CreateRedirected(ffprobePath);
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-select_streams");
            startInfo.ArgumentList.Add("a:0");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("stream=codec_name,codec_tag_string:format=duration");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("json");
            startInfo.ArgumentList.Add(filePath);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                TryKillProcess(process);
                return false;
            }

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                return false;
            }

            using var doc = JsonDocument.Parse(stdout);
            return TryReadAtmosProbe(doc.RootElement, out durationSeconds);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private static bool TryReadAtmosProbe(JsonElement root, out double durationSeconds)
    {
        durationSeconds = 0;
        if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
        {
            return false;
        }

        var stream = streams[0];
        var codec = stream.TryGetProperty("codec_name", out var codecElement)
            ? codecElement.GetString()
            : string.Empty;
        var codecTag = stream.TryGetProperty("codec_tag_string", out var codecTagElement)
            ? codecTagElement.GetString()
            : string.Empty;
        if (!IsAtmosAudioCodec(codec) && !IsAtmosAudioCodec(codecTag))
        {
            return false;
        }

        if (!root.TryGetProperty("format", out var format)
            || !format.TryGetProperty("duration", out var durationElement)
            || !double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out durationSeconds))
        {
            return false;
        }

        return durationSeconds > 0;
    }

    private static bool IsAtmosAudioCodec(string? codec)
    {
        return !string.IsNullOrWhiteSpace(codec)
            && (codec.Contains("eac3", StringComparison.OrdinalIgnoreCase)
                || codec.Contains("ec-3", StringComparison.OrdinalIgnoreCase));
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
        }
    }

    private static string? ResolveFfmpegPath() => ExternalToolResolver.ResolveFfmpegPath();

    private static string? ResolveFfprobePath() => ExternalToolResolver.ResolveFfprobePath();

    private static async Task ConvertTempToFlacAsync(
        string tempPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (!IOFile.Exists(tempPath))
        {
            throw new InvalidOperationException("Temporary audio file is missing.");
        }

        var ffmpegPath = ResolveFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            var fallbackPath = Path.ChangeExtension(outputPath, ".m4a");
            IOFile.Move(tempPath, fallbackPath, overwrite: true);
            throw new InvalidOperationException($"ffmpeg not available; source kept as {fallbackPath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-y -i \"{tempPath}\" -vn -c:a flac \"{outputPath}\"",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start ffmpeg process.");
        }

        await process.WaitForExitAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        if (process.ExitCode != 0 || !IOFile.Exists(outputPath))
        {
            var fallbackPath = Path.ChangeExtension(outputPath, ".m4a");
            IOFile.Move(tempPath, fallbackPath, overwrite: true);
            throw new InvalidOperationException($"ffmpeg conversion failed; source kept as {fallbackPath}. Error: {stderr}");
        }

        IOFile.Delete(tempPath);
    }

    private static bool TryParseManifest(string body, out string manifest)
    {
        manifest = "";
        try
        {
            var v2 = JsonSerializer.Deserialize<TidalApiResponseV2>(body, SerializerOptions);
            if (!string.IsNullOrWhiteSpace(v2?.Data?.Manifest))
            {
                manifest = ManifestPrefix + v2.Data.Manifest;
                return true;
            }
        }
        catch (JsonException)
        {
            manifest = "";
        }

        try
        {
            var v1 = JsonSerializer.Deserialize<List<TidalApiResponse>>(body, SerializerOptions);
            var direct = v1?.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.OriginalTrackUrl));
            if (direct != null)
            {
                manifest = direct.OriginalTrackUrl;
                return true;
            }
        }
        catch (JsonException)
        {
            manifest = "";
        }

        return false;
    }

    private static bool TryExtractManifestUri(string body, out string manifestUri)
    {
        manifestUri = "";
        try
        {
            using var document = JsonDocument.Parse(body);
            return TryFindManifestUri(document.RootElement, out manifestUri);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryFindManifestUri(JsonElement element, out string manifestUri)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => TryFindManifestUriInObject(element, out manifestUri),
            JsonValueKind.Array => TryFindManifestUriInArray(element, out manifestUri),
            _ => NoManifestUri(out manifestUri)
        };
    }

    private static bool TryFindManifestUriInObject(JsonElement element, out string manifestUri)
    {
        if (TryReadManifestUriProperty(element, out manifestUri))
        {
            return true;
        }

        var resolvedManifestUri = element.EnumerateObject()
            .Select(property => TryFindManifestUri(property.Value, out var resolvedUri) ? resolvedUri : null)
            .FirstOrDefault(static resolvedUri => !string.IsNullOrWhiteSpace(resolvedUri));
        if (!string.IsNullOrWhiteSpace(resolvedManifestUri))
        {
            manifestUri = resolvedManifestUri;
            return true;
        }

        return false;
    }

    private static bool TryReadManifestUriProperty(JsonElement element, out string manifestUri)
    {
        manifestUri = "";
        if (!element.TryGetProperty("uri", out var uriProperty)
            || uriProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        manifestUri = uriProperty.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(manifestUri);
    }

    private static bool TryFindManifestUriInArray(JsonElement element, out string manifestUri)
    {
        var resolvedManifestUri = element.EnumerateArray()
            .Select(item => TryFindManifestUri(item, out var resolvedUri) ? resolvedUri : null)
            .FirstOrDefault(static resolvedUri => !string.IsNullOrWhiteSpace(resolvedUri));
        if (!string.IsNullOrWhiteSpace(resolvedManifestUri))
        {
            manifestUri = resolvedManifestUri;
            return true;
        }

        manifestUri = "";
        return false;
    }

    private static bool NoManifestUri(out string manifestUri)
    {
        manifestUri = "";
        return false;
    }

    private static string TryDecodeManifest(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return "";
        }

        if (payload.StartsWith(ManifestPrefix, StringComparison.OrdinalIgnoreCase))
        {
            payload = payload[ManifestPrefix.Length..];
        }

        try
        {
            var decoded = Convert.FromBase64String(payload);
            return Encoding.UTF8.GetString(decoded);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return payload;
        }
    }

    private async Task<IReadOnlyList<string>> GetDownloadUrlCandidatesAsync(long trackId, string quality, CancellationToken cancellationToken)
    {
        return await GetDownloadUrlCandidatesAsync(trackId, quality, allowRefresh: true, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> GetDownloadUrlCandidatesAsync(
        long trackId,
        string quality,
        bool allowRefresh,
        CancellationToken cancellationToken)
    {
        var providers = await _providerSource.GetRotatedProviderRecordsAsync(cancellationToken);
        if (providers.Count == 0)
        {
            throw new InvalidOperationException("Tidal API pool is empty");
        }

        var manifests = new List<string>();
        foreach (var provider in providers)
        {
            if (IsProviderCoolingDown(provider))
            {
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var manifest = await FetchManifestFromProviderAsync(provider, trackId, quality, cancellationToken);
                stopwatch.Stop();
                if (string.IsNullOrWhiteSpace(manifest))
                {
                    await _providerSource.RememberFailureAsync(provider, "empty_response", stopwatch.ElapsedMilliseconds, cancellationToken);
                    continue;
                }

                await _providerSource.RememberHealthSuccessAsync(provider, stopwatch.ElapsedMilliseconds, cancellationToken);
                await _providerSource.RememberSuccessAsync(provider, cancellationToken);
                manifests.Add(manifest.Trim());
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();
                await _providerSource.RememberFailureAsync(provider, ClassifyProviderFailure(ex), stopwatch.ElapsedMilliseconds, cancellationToken);
                _logger.LogWarning(
                    ex,
                    "Tidal public provider {Provider} failed for track {TrackId} quality {Quality}.",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(provider.DisplayName),
                    trackId,
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(quality));
            }
        }

        manifests = manifests
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (manifests.Count > 0)
        {
            return manifests;
        }

        if (allowRefresh)
        {
            await TidalApiProviderSource.RefreshAsync(force: true, cancellationToken);
            return await GetDownloadUrlCandidatesAsync(trackId, quality, allowRefresh: false, cancellationToken);
        }

        throw new InvalidOperationException("Tidal download URL not available");
    }

    private async Task<string> GetVideoStreamUrlAsync(long videoId, int maxResolution, CancellationToken cancellationToken)
    {
        var token = await _accessTokenProvider.GetAccessTokenAsync(cancellationToken);
        var url = $"https://api.tidal.com/v1/videos/{videoId}/playbackinfo?videoquality=HIGH&playbackmode=STREAM&assetpresentation=FULL";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Tidal video playback info failed with status {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("manifest", out var manifestElement)
            || manifestElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Tidal video playback info did not return a manifest.");
        }

        var masterPlaylistUrl = ExtractStreamUrlFromManifest(manifestElement.GetString() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(masterPlaylistUrl))
        {
            throw new InvalidOperationException("Tidal video stream URL not available.");
        }

        return await SelectTidalVideoVariantAsync(masterPlaylistUrl, NormalizeTidalVideoMaxResolution(maxResolution), cancellationToken);
    }

    private async Task<string> SelectTidalVideoVariantAsync(
        string masterPlaylistUrl,
        int maxResolution,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(masterPlaylistUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Tidal video manifest failed with status {(int)response.StatusCode}.");
        }

        var playlist = await response.Content.ReadAsStringAsync(cancellationToken);
        var variants = ParseTidalVideoVariants(playlist, masterPlaylistUrl)
            .Where(variant => variant.Height <= maxResolution)
            .OrderByDescending(variant => variant.Height)
            .ThenByDescending(variant => variant.Bandwidth)
            .ToList();

        if (variants.Count == 0)
        {
            throw new InvalidOperationException($"Tidal video manifest does not contain a stream at or below {maxResolution}p.");
        }

        return variants[0].Url;
    }

    private static List<TidalVideoVariant> ParseTidalVideoVariants(string playlist, string masterPlaylistUrl)
    {
        var variants = new List<TidalVideoVariant>();
        if (string.IsNullOrWhiteSpace(playlist))
        {
            return variants;
        }

        var lines = playlist
            .Split(PlaylistLineSeparators, StringSplitOptions.None)
            .Select(static line => line.Trim())
            .ToList();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var resolutionMatch = MatchWithTimeout(line, @"RESOLUTION=\d+x(?<height>\d+)", RegexOptions.IgnoreCase);
            if (!resolutionMatch.Success || !int.TryParse(resolutionMatch.Groups["height"].Value, out var height))
            {
                continue;
            }

            var bandwidth = 0;
            var bandwidthMatch = MatchWithTimeout(line, @"(?:AVERAGE-)?BANDWIDTH=(?<bandwidth>\d+)", RegexOptions.IgnoreCase);
            if (bandwidthMatch.Success)
            {
                _ = int.TryParse(bandwidthMatch.Groups["bandwidth"].Value, out bandwidth);
            }

            var variantUrl = lines.Skip(i + 1)
                .FirstOrDefault(static candidate => !string.IsNullOrWhiteSpace(candidate) && !candidate.StartsWith('#'));
            if (string.IsNullOrWhiteSpace(variantUrl))
            {
                continue;
            }

            variants.Add(new TidalVideoVariant(height, bandwidth, ResolveTidalManifestUrl(masterPlaylistUrl, variantUrl)));
        }

        return variants;
    }

    private static string ResolveTidalManifestUrl(string masterPlaylistUrl, string variantUrl)
    {
        if (Uri.TryCreate(variantUrl, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (Uri.TryCreate(masterPlaylistUrl, UriKind.Absolute, out var master)
            && Uri.TryCreate(master, variantUrl, out var resolved))
        {
            return resolved.ToString();
        }

        return variantUrl;
    }

    private static int NormalizeTidalVideoMaxResolution(int maxResolution)
        => maxResolution is 360 or 480 or 720 or 1080
            ? maxResolution
            : throw new InvalidOperationException($"Unsupported Tidal video max resolution: {maxResolution}.");

    private sealed record TidalVideoVariant(int Height, int Bandwidth, string Url);

    private static string ExtractStreamUrlFromManifest(string manifest)
    {
        var decoded = TryDecodeManifest(manifest);
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(decoded);
            if (document.RootElement.TryGetProperty("urls", out var urls)
                && urls.ValueKind == JsonValueKind.Array)
            {
                var first = urls.EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString() ?? string.Empty)
                    .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
                if (!string.IsNullOrWhiteSpace(first))
                {
                    return first;
                }
            }
        }
        catch (JsonException)
        {
            var match = Regex.Match(decoded, @"https?:\/\/[^\s""'<>]+", RegexOptions.IgnoreCase, RegexTimeout);
            return match.Success ? match.Value : string.Empty;
        }

        return string.Empty;
    }

    private static async Task DownloadVideoStreamWithFfmpegAsync(
        string streamUrl,
        string outputPath,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            throw new InvalidOperationException("Tidal video stream URL is empty.");
        }

        var ffmpegPath = ResolveFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            throw new InvalidOperationException("ffmpeg not available for Tidal video download.");
        }

        if (progressCallback != null)
        {
            await progressCallback(0, 0);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(streamUrl);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start ffmpeg process.");
        }

        await process.WaitForExitAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        if (process.ExitCode != 0 || !IOFile.Exists(outputPath))
        {
            DownloadFileUtilities.TryDeleteFile(outputPath);
            throw new InvalidOperationException($"Tidal video download failed: {DownloadFileUtilities.TruncateForLog(stderr)}");
        }

        if (progressCallback != null)
        {
            await progressCallback(100, 0);
        }
    }

    public async Task CheckPublicProvidersAsync(CancellationToken cancellationToken)
    {
        const long healthTrackId = 251380836;
        const string healthQuality = "LOSSLESS";
        var providers = await _providerSource.GetRotatedProviderRecordsAsync(cancellationToken);
        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var healthy = !string.IsNullOrWhiteSpace(provider.HealthEndpoint)
                    ? await CheckProviderHealthEndpointAsync(provider, cancellationToken)
                    : !string.IsNullOrWhiteSpace(await FetchManifestFromProviderAsync(provider, healthTrackId, healthQuality, cancellationToken));
                stopwatch.Stop();
                if (!healthy)
                {
                    await _providerSource.RememberFailureAsync(provider, "empty_response", stopwatch.ElapsedMilliseconds, cancellationToken);
                }
                else
                {
                    await _providerSource.RememberHealthSuccessAsync(provider, stopwatch.ElapsedMilliseconds, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                await _providerSource.RememberFailureAsync(provider, "timeout", stopwatch.ElapsedMilliseconds, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Tidal public provider health check failed for {Provider}", provider.DisplayName);
                }

                await _providerSource.RememberFailureAsync(provider, ClassifyProviderFailure(ex), stopwatch.ElapsedMilliseconds, cancellationToken);
            }
        }
    }

    private async Task<bool> CheckProviderHealthEndpointAsync(TidalPublicProvider provider, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(provider.HealthEndpoint, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(provider.HealthServiceKey))
        {
            return true;
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("services", out var services)
            || services.ValueKind != JsonValueKind.Object
            || !services.TryGetProperty(provider.HealthServiceKey, out var service)
            || service.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (service.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (service.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number)
        {
            return status.GetInt32() is >= 200 and < 300;
        }

        return false;
    }

    private static bool IsProviderCoolingDown(TidalPublicProvider provider)
        => provider.CooldownUntil.HasValue && provider.CooldownUntil.Value > DateTimeOffset.UtcNow;

    private static string ClassifyProviderFailure(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("429", StringComparison.OrdinalIgnoreCase)) return "rate_limited";
        if (exception is TimeoutException or HttpRequestException || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return "timeout";
        if (message.Contains("preview", StringComparison.OrdinalIgnoreCase)
            || message.Contains("missing manifest", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no data", StringComparison.OrdinalIgnoreCase))
        {
            return "empty_response";
        }

        return "transient";
    }

    private static bool BodyContainsPreviewAsset(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return TryFindStringProperty(document.RootElement, "assetPresentation", out var assetPresentation)
                && string.Equals(assetPresentation, "PREVIEW", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return body.Contains("PREVIEW", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool TryExtractZarzAtmosManifestUri(string body, out string manifestUri)
    {
        manifestUri = string.Empty;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!TryFindStringProperty(document.RootElement, "uri", out var uri)
                || string.IsNullOrWhiteSpace(uri))
            {
                return false;
            }

            if (TryFindStringArrayProperty(document.RootElement, "formats", out var formats)
                && !formats.Any(static format => string.Equals(format, "EAC3_JOC", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            manifestUri = uri;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryFindStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    value = property.Value.GetString() ?? string.Empty;
                    return !string.IsNullOrWhiteSpace(value);
                }

                if (TryFindStringProperty(property.Value, propertyName, out value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindStringProperty(item, propertyName, out value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindStringArrayProperty(JsonElement element, string propertyName, out IReadOnlyList<string> values)
    {
        values = Array.Empty<string>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Array)
                {
                    values = property.Value.EnumerateArray()
                        .Where(static item => item.ValueKind == JsonValueKind.String)
                        .Select(static item => item.GetString() ?? string.Empty)
                        .Where(static item => !string.IsNullOrWhiteSpace(item))
                        .ToArray();
                    return true;
                }

                if (TryFindStringArrayProperty(property.Value, propertyName, out values))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindStringArrayProperty(item, propertyName, out values))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizeTidalDownloadQuality(string? quality)
    {
        var normalized = (quality ?? string.Empty).Trim().ToUpperInvariant();
        return normalized switch
        {
            "" => "LOSSLESS",
            "HI_RES" => "HI_RES_LOSSLESS",
            "MAX_HI_RES" => "HI_RES_LOSSLESS",
            "ATMOS" => "DOLBY_ATMOS",
            _ => normalized
        };
    }

    private sealed class TidalSearchResponse
    {
        [JsonPropertyName("items")]
        public List<TidalTrack> Items { get; set; } = new();
    }

    private sealed class TidalTrack
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("isrc")]
        public string Isrc { get; set; } = "";

        [JsonPropertyName("duration")]
        public int Duration { get; set; }

        [JsonPropertyName("artist")]
        public TidalArtist? Artist { get; set; }

        [JsonPropertyName("artists")]
        public List<TidalArtist>? Artists { get; set; }

        [JsonPropertyName("album")]
        public TidalAlbum? Album { get; set; }

        [JsonPropertyName("audioModes")]
        public List<string>? AudioModes { get; set; }
    }

    private sealed class TidalArtist
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
    }

    private sealed class TidalAlbum
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = "";
    }

    private sealed class TidalApiResponse
    {
        [JsonPropertyName("OriginalTrackUrl")]
        public string OriginalTrackUrl { get; set; } = "";
    }

    private sealed class TidalApiResponseV2
    {
        [JsonPropertyName("data")]
        public TidalApiResponseV2Data? Data { get; set; }
    }

    private sealed class TidalApiResponseV2Data
    {
        [JsonPropertyName("manifest")]
        public string Manifest { get; set; } = "";

        [JsonPropertyName("manifestMimeType")]
        public string ManifestMimeType { get; set; } = "";
    }

    private sealed class TidalBtsManifest
    {
        [JsonPropertyName("mimeType")]
        public string MimeType { get; set; } = "";

        [JsonPropertyName("codecs")]
        public string Codecs { get; set; } = "";

        [JsonPropertyName("urls")]
        public List<string> Urls { get; set; } = new();
    }

    private sealed record TidalManifestInfo(
        string DirectUrl,
        string InitUrl,
        List<string> MediaUrls,
        string MimeType,
        string Codecs,
        string RawText)
    {
        public static readonly TidalManifestInfo Empty = new(
            string.Empty,
            string.Empty,
            new List<string>(),
            string.Empty,
            string.Empty,
            string.Empty);

        public void Deconstruct(out string directUrl, out string initUrl, out List<string> mediaUrls, out string mimeType)
        {
            directUrl = DirectUrl;
            initUrl = InitUrl;
            mediaUrls = MediaUrls;
            mimeType = MimeType;
        }
    }
}
