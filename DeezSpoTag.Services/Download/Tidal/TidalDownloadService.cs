using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using IOFile = System.IO.File;
using Microsoft.Extensions.Logging;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Core.Utils;
using TagLib;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Services.Matching;
using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Services.Download.Tidal;

public sealed class TidalDownloadService
{
    private const string AudioKeyword = "audio";
    private const string ManifestPrefix = "MANIFEST:";
    private const string TidalNativeApiHost = "api.tidal.com";
    private const string TidalPublicSearchHost = "tidal.com";
    private const string TidalPublicApiBasePath = "v1";
    private const string TidalListenHost = "listen.tidal.com";
    private const string TidalListenTrackPathPrefix = "track";
    // Sonar exception policy: this is the only allowed hardcoded token exception (public partner token).
    [SuppressMessage("Security", "S6418", Justification = "Only allowed exception: public Tidal partner token, not a private credential.")]
    private const string TidalPublicToken = "txNoH4kkV41MfH25";
    private const string TidalPublicCountryCode = "US";
    private const string TidalPublicLocale = "en_US";
    private const string TidalPublicDeviceType = "BROWSER";
    private const string TidalPublicUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const int MaxConcurrentProviderResolutions = 2;
    private const string ZarzSignedBaseUrl = "https://api.zarz.moe/v2";
    private const string ZarzSignedDownloadPath = "/dl/tid";
    private const string ZarzTicketsPath = "/tickets";
    private const string ZarzBootstrapPath = "/bootstrap";
    private const string ZarzChallengePath = "/challenge";
    private const string ZarzExchangePath = "/session/exchange";
    private const string ZarzAppVersion = "tidal-web@1.1.0";
    private const string ZarzPlatform = "extension";
    private const string ZarzSchemeLabel = "ZARZ-HMAC-V1";
    private const int ZarzTimeWindowSeconds = 300;
    private const string ZarzSessionFileName = "zarz-signed-session.json";
    private static readonly string[] PlaylistLineSeparators = { "\r\n", "\n" };
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly SemaphoreSlim ProviderResolutionGate = new(MaxConcurrentProviderResolutions, MaxConcurrentProviderResolutions);
    private static readonly SemaphoreSlim ZarzSessionGate = new(1, 1);
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
    private readonly ITidalAccessTokenProvider _accessTokenProvider;
    private readonly string _zarzSessionPath;

    public sealed record TidalResolvedTrack(
        string Url,
        long Id,
        string Title,
        string Artist,
        string Album,
        string Isrc,
        int DurationSeconds);

    public TidalDownloadService(
        ILogger<TidalDownloadService> logger,
        TidalApiProviderSource providerSource,
        ITidalAccessTokenProvider accessTokenProvider)
    {
        _logger = logger;
        _providerSource = providerSource;
        _accessTokenProvider = accessTokenProvider;
        _zarzSessionPath = Path.Join(DeezSpoTagDataRootResolver.Resolve(), "tidal", ZarzSessionFileName);
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

        throw new InvalidOperationException("Tidal download requires a valid Tidal ID or service URL.");
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

    public async Task<bool> ValidateTrackUrlAsync(
        string tidalUrl,
        string trackTitle,
        string artistName,
        string albumName,
        string isrc,
        int expectedDuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var trackId = GetTrackIdFromUrl(tidalUrl);
            var trackInfo = await GetTrackInfoByIdAsync(trackId, cancellationToken);
            return ValidateResolvedTrack(
                trackInfo,
                trackTitle,
                artistName,
                albumName,
                isrc,
                expectedDuration).Accepted;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Tidal identity validation failed for {Title} - {Artist}",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(trackTitle),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(artistName));
            return false;
        }
    }

    public async Task<TidalResolvedTrack?> ResolveTrackMetadataAsync(
        string? tidalIdOrUrl,
        CancellationToken cancellationToken)
    {
        var normalizedId = EngineLinkParser.NormalizeNumericTrackId(tidalIdOrUrl)
            ?? EngineLinkParser.TryExtractTidalTrackId(tidalIdOrUrl);
        if (!long.TryParse(normalizedId, out var trackId) || trackId <= 0)
        {
            return null;
        }

        try
        {
            var trackInfo = await GetTrackInfoByIdAsync(trackId, cancellationToken);
            return BuildResolvedTrack(trackInfo);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Tidal metadata lookup failed for track ID {TrackId}", trackId);
            return null;
        }
    }

    public async Task<string?> ResolveAtmosTrackUrlAsync(
        string trackTitle,
        string artistName,
        string isrc,
        int expectedDuration,
        CancellationToken cancellationToken)
        => (await ResolveAtmosTrackAsync(
            trackTitle,
            artistName,
            albumName: string.Empty,
            tidalId: string.Empty,
            isrc,
            expectedDuration,
            cancellationToken))?.Url;

    public async Task<TidalResolvedTrack?> ResolveAtmosTrackAsync(
        string trackTitle,
        string artistName,
        string albumName,
        string tidalId,
        string isrc,
        int expectedDuration,
        CancellationToken cancellationToken)
    {
        try
        {
            if (long.TryParse(EngineLinkParser.NormalizeNumericTrackId(tidalId), out var persistedTrackId))
            {
                var persistedTrack = await GetAtmosTrackInfoByIdAsync(persistedTrackId, cancellationToken);
                if (persistedTrack != null && HasTidalAtmosMode(persistedTrack))
                {
                    var validation = ValidateResolvedTrack(
                        persistedTrack,
                        trackTitle,
                        artistName,
                        NormalizeUsableAlbum(albumName) ?? string.Empty,
                        string.Empty,
                        expectedDuration);
                    if (validation.Accepted)
                    {
                        return BuildResolvedTrack(persistedTrack);
                    }

                    _logger.LogWarning(
                        "Persisted Tidal Atmos identity {TrackId} rejected for {Title} - {Artist}: {Reason}",
                        persistedTrackId,
                        DeezSpoTag.Core.Security.LogSanitizer.OneLine(trackTitle),
                        DeezSpoTag.Core.Security.LogSanitizer.OneLine(artistName),
                        validation.Reason);
                }
            }

            var trackInfo = await SearchAtmosTrackByMetadataWithIsrcAsync(
                trackTitle,
                artistName,
                albumName,
                isrc,
                expectedDuration,
                cancellationToken);
            if (trackInfo == null || trackInfo.Id <= 0)
            {
                return null;
            }

            return BuildResolvedTrack(trackInfo);
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

    private static TidalResolvedTrack BuildResolvedTrack(TidalTrack track)
        => new(
            BuildTidalTrackListenUrl(track.Id),
            track.Id,
            track.Title,
            ResolveTidalArtistName(track),
            track.Album?.Title ?? string.Empty,
            track.Isrc,
            track.Duration);

    private async Task<string> DownloadByUrlAsync(
        TidalDownloadRequest request,
        string tidalUrl,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        var trackId = GetTrackIdFromUrl(tidalUrl);

        var outputPathContext = new AudioFilePathHelper.AudioPathContext
        {
            OutputDir = request.OutputDir,
            Title = request.TrackName,
            Artist = request.ArtistName,
            Album = TrackTitleMatcher.RemoveAtmosVersionMarker(request.AlbumName),
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

        var expectedDurationSeconds = Math.Max(0, request.DurationSeconds);
        var candidateUrls = await GetDownloadUrlCandidatesAsync(trackId, request.Quality, cancellationToken);
        await DownloadValidatedFileAsync(
            candidateUrls,
            outputPath,
            expectedDurationSeconds,
            request.Quality,
            isAtmosRequest,
            progressCallback,
            cancellationToken);
        return outputPath;
    }

    private static TrackCandidateValidationResult ValidateResolvedTrack(
        TidalTrack track,
        string trackTitle,
        string artistName,
        string albumName,
        string isrc,
        int expectedDuration)
    {
        return TrackCandidateValidator.Validate(
            new TrackMatchSource(
                isrc,
                trackTitle,
                artistName,
                albumName,
                expectedDuration > 0 ? expectedDuration * 1000 : null),
            new TrackMatchCandidate(
                track.Id.ToString(CultureInfo.InvariantCulture),
                track.Isrc,
                track.Title,
                ResolveTidalArtistName(track),
                track.Album?.Title,
                track.Duration > 0 ? track.Duration * 1000 : null),
            new TrackCandidateValidationOptions(
                StrictWithoutIsrc: true,
                AllowMissingCandidateArtist: false,
                RequireCandidateDurationWhenSourceHasDuration: true,
                MaxIsrcDurationDifferenceMs: 20_000,
                MaxMetadataDurationDifferenceMs: 3_000));
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

    private async Task<TidalTrack> SearchTrackByMetadataWithIsrcAsync(string trackName, string artistName, string isrc, int expectedDuration, CancellationToken cancellationToken)
    {
        var isrcTracks = await SearchTracksByIsrcAsync(isrc, 25, cancellationToken);
        var exactIsrcMatch = FindIsrcMatch(isrcTracks, isrc);
        if (exactIsrcMatch != null)
        {
            return exactIsrcMatch;
        }

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

        var validatedMatch = FindValidatedMetadataMatch(allTracks, trackName, artistName, albumName: null, isrc, expectedDuration);
        if (validatedMatch != null)
        {
            return validatedMatch;
        }

        throw new InvalidOperationException("No validated Tidal track match found");
    }

    private async Task<TidalTrack> SearchAtmosTrackByMetadataWithIsrcAsync(
        string trackName,
        string artistName,
        string albumName,
        string isrc,
        int expectedDuration,
        CancellationToken cancellationToken)
    {
        var sourceAlbum = NormalizeUsableAlbum(albumName);
        var isrcCandidates = await SearchTracksByIsrcAsync(isrc, 25, cancellationToken);
        var isrcAtmosTracks = await HydrateTidalAtmosCandidatesAsync(
            RankAtmosCandidates(isrcCandidates, trackName, artistName, albumName, isrc, expectedDuration),
            cancellationToken);
        var exactIsrcMatch = FindIsrcMatch(isrcAtmosTracks, isrc, sourceAlbum);
        if (exactIsrcMatch != null)
        {
            return exactIsrcMatch;
        }

        var queries = BuildSearchQueries(trackName, artistName);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in queries)
        {
            if (!seen.Add(query))
            {
                continue;
            }

            var candidates = await SearchTracksAsync(query, 25, cancellationToken);
            var atmosTracks = await HydrateTidalAtmosCandidatesAsync(
                RankAtmosCandidates(candidates, trackName, artistName, albumName, isrc, expectedDuration),
                cancellationToken);
            var isrcMatch = FindIsrcMatch(atmosTracks, isrc, sourceAlbum);
            if (isrcMatch != null)
            {
                return isrcMatch;
            }

            // Atmos editions commonly use a different ISRC from the stereo master.
            // Exact ISRC resolution has already failed, so validate the edition by
            // title, artist, album, and duration instead of rejecting that variant.
            var validatedMatch = FindValidatedMetadataMatch(atmosTracks, trackName, artistName, sourceAlbum, string.Empty, expectedDuration);
            if (validatedMatch != null)
            {
                return validatedMatch;
            }
        }

        throw new InvalidOperationException("No validated Tidal Atmos track match found");
    }

    private static bool HasTidalAtmosMode(TidalTrack track)
    {
        return track.AudioModes?.Any(static mode =>
            string.Equals(mode, "DOLBY_ATMOS", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private async Task<List<TidalTrack>> HydrateTidalAtmosCandidatesAsync(
        IEnumerable<TidalTrack> tracks,
        CancellationToken cancellationToken)
    {
        const int maximumHydratedCandidates = 8;
        var candidates = tracks
            .Where(static track => track.Id > 0)
            .GroupBy(static track => track.Id)
            .Select(static group => group.First())
            .Take(maximumHydratedCandidates)
            .ToList();
        var hydrationTasks = candidates.Select(async track =>
        {
            if (HasTidalAtmosMode(track))
            {
                return track;
            }

            var detailed = await GetAtmosTrackInfoByIdAsync(track.Id, cancellationToken);
            return detailed != null && HasTidalAtmosMode(detailed) ? detailed : null;
        });
        var hydrated = await Task.WhenAll(hydrationTasks);
        return hydrated.Where(static track => track != null).Cast<TidalTrack>().ToList();
    }

    private static IEnumerable<TidalTrack> RankAtmosCandidates(
        IEnumerable<TidalTrack> tracks,
        string trackName,
        string artistName,
        string albumName,
        string isrc,
        int expectedDuration)
    {
        var normalizedAlbum = NormalizeUsableAlbum(albumName) ?? string.Empty;
        return tracks
            .Where(track => ValidateResolvedTrack(
                track,
                trackName,
                artistName,
                normalizedAlbum,
                string.Empty,
                expectedDuration).Accepted)
            .OrderByDescending(track => !string.IsNullOrWhiteSpace(isrc)
                && string.Equals(track.Isrc, isrc, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(HasTidalAtmosMode);
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

    private TidalTrack? FindIsrcMatch(List<TidalTrack> allTracks, string isrc, string? albumName = null)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        var matches = allTracks
            .Where(track => string.Equals(track.Isrc, isrc, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var match = string.IsNullOrWhiteSpace(albumName)
            ? matches.FirstOrDefault()
            : matches.FirstOrDefault(track => AlbumsCompatible(albumName, track.Album?.Title));
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
        string? albumName,
        string isrc,
        int expectedDuration)
    {
        var source = new TrackMatchSource(
            isrc,
            trackName,
            artistName,
            albumName,
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

    private static string? NormalizeUsableAlbum(string? albumName)
    {
        if (string.IsNullOrWhiteSpace(albumName))
        {
            return null;
        }

        var normalized = albumName.Trim();
        return normalized.Equals("Unknown Album", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    private static bool AlbumsCompatible(string? expectedAlbum, string? candidateAlbum)
    {
        var normalizedExpected = NormalizeUsableAlbum(expectedAlbum);
        if (string.IsNullOrWhiteSpace(normalizedExpected))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(candidateAlbum)
               && TrackTitleMatcher.TitlesMatch(normalizedExpected, candidateAlbum);
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
        return await SearchTracksViaPublicApiAsync(query, limit, cancellationToken);
    }

    private async Task<List<TidalTrack>> SearchTracksByIsrcAsync(string isrc, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return new List<TidalTrack>();
        }

        var url = BuildTidalNativeApiUrl(
            "tracks",
            new Dictionary<string, string>
            {
                ["isrc"] = isrc.Trim(),
                ["limit"] = Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture),
                ["offset"] = "0"
            });
        var payload = await SendTidalPublicJsonOrDefaultAsync<TidalSearchResponse>(url, null, _ => { }, cancellationToken);
        return payload?.Items ?? new List<TidalTrack>();
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
        var publicTrack = await TryGetTrackInfoByIdViaPublicApiAsync(trackId, cancellationToken);
        if (publicTrack != null)
        {
            return publicTrack;
        }

        throw new InvalidOperationException($"Tidal track not found for track ID {trackId}.");
    }

    private async Task<TidalTrack?> GetAtmosTrackInfoByIdAsync(long trackId, CancellationToken cancellationToken)
    {
        var publicTrack = await TryGetTrackInfoByIdViaPublicApiAsync(trackId, cancellationToken);
        if (publicTrack != null && HasTidalAtmosMode(publicTrack))
        {
            return publicTrack;
        }

        return publicTrack;
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
            BuildTidalNativeApiUrl($"tracks/{trackId}"),
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
            request.Headers.TryAddWithoutValidation("User-Agent", TidalPublicUserAgent);

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
        var builder = new UriBuilder(Uri.UriSchemeHttps, TidalPublicSearchHost)
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

    private static string BuildTidalNativeApiUrl(string path, IDictionary<string, string>? query = null)
    {
        var builder = new UriBuilder(Uri.UriSchemeHttps, TidalNativeApiHost)
        {
            Path = $"{TidalPublicApiBasePath}/{path.TrimStart('/')}"
        };
        var allQuery = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["countryCode"] = TidalPublicCountryCode
        };
        if (query != null)
        {
            foreach (var pair in query)
            {
                allQuery[pair.Key] = pair.Value;
            }
        }

        builder.Query = string.Join("&", allQuery.Select(pair =>
            $"{WebUtility.UrlEncode(pair.Key)}={WebUtility.UrlEncode(pair.Value)}"));
        return builder.Uri.ToString();
    }

    private static string BuildTidalTrackListenUrl(long trackId)
    {
        return new UriBuilder(Uri.UriSchemeHttps, TidalListenHost)
        {
            Path = $"{TidalListenTrackPathPrefix}/{trackId}"
        }.Uri.ToString();
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
            if (!string.Equals(provider.Kind, TidalPublicProviderDefaults.ZarzProviderKind, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unsupported Tidal public provider kind: {provider.Kind}.");
            }

            return await FetchManifestFromZarzAsync(trackId, quality, cancellationToken);
        }
        finally
        {
            ProviderResolutionGate.Release();
        }
    }

    private async Task<string?> FetchManifestFromZarzAsync(
        long trackId,
        string quality,
        CancellationToken cancellationToken)
    {
        var normalizedQuality = NormalizeTidalDownloadQuality(quality);
        if (string.Equals(normalizedQuality, "DOLBY_ATMOS", StringComparison.OrdinalIgnoreCase))
        {
            return await FetchAtmosManifestFromZarzAsync(trackId, cancellationToken);
        }

        var payload = new
        {
            id = trackId.ToString(CultureInfo.InvariantCulture),
            quality = normalizedQuality
        };
        var body = await SendZarzDownloadJsonAsync(payload, trackId, cancellationToken);
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
        long trackId,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            id = trackId.ToString(CultureInfo.InvariantCulture),
            endpoint = "manifests",
            formats = new[] { "EAC3_JOC" }
        };
        var body = await SendZarzDownloadJsonAsync(payload, trackId, cancellationToken);
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

    private async Task<string> SendZarzDownloadJsonAsync<TPayload>(
        TPayload payload,
        long trackId,
        CancellationToken cancellationToken)
    {
        var ticket = await RequestZarzTicketAsync(trackId, cancellationToken);
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
        using var response = await SendZarzSignedRequestAsync(
            HttpMethod.Post,
            ZarzSignedDownloadPath,
            bodyBytes,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Zarz-Ticket"] = ticket
            },
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(BuildZarzHttpFailureMessage("Tidal Zarz provider", response, body));
        }

        return body;
    }

    private async Task<string> RequestZarzTicketAsync(long trackId, CancellationToken cancellationToken)
    {
        var resourceHash = Sha256Hex(Encoding.UTF8.GetBytes($"tid:track:{trackId.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}"));
        var payload = new
        {
            capability = "download_ticket",
            provider = "tid",
            resource_hash = resourceHash
        };
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
        using var response = await SendZarzSignedRequestAsync(
            HttpMethod.Post,
            ZarzTicketsPath,
            bodyBytes,
            null,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(BuildZarzHttpFailureMessage("Tidal Zarz ticket provider", response, body));
        }

        try
        {
            var ticket = JsonSerializer.Deserialize<ZarzTicketResponse>(body, SerializerOptions);
            var value = !string.IsNullOrWhiteSpace(ticket?.TicketId) ? ticket.TicketId : ticket?.Ticket;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Tidal Zarz ticket provider returned invalid JSON.", ex);
        }

        throw new InvalidOperationException("Tidal Zarz ticket provider did not return a ticket.");
    }

    private async Task<HttpResponseMessage> SendZarzSignedRequestAsync(
        HttpMethod method,
        string path,
        byte[]? bodyBytes,
        IReadOnlyDictionary<string, string>? extraHeaders,
        CancellationToken cancellationToken)
    {
        var session = await EnsureZarzSignedSessionAsync(cancellationToken);
        var uri = BuildZarzSignedUri(path);
        var bytes = bodyBytes ?? Array.Empty<byte>();
        var timestamp = DateTimeOffset.UtcNow;
        var timestampValue = timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var nonce = RandomHex(12);
        var bodyHash = Sha256Hex(bytes);
        var window = timestamp.ToUnixTimeSeconds() / ZarzTimeWindowSeconds;
        var rollingInput = $"{window}:{session.SessionId}";
        var rollingKey = Base64UrlNoPadding(HmacSha256(Encoding.UTF8.GetBytes(session.SessionSecret), Encoding.UTF8.GetBytes(rollingInput)));
        var escapedPath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        var signingInput = string.Join(
            "\n",
            ZarzSchemeLabel,
            method.Method.ToUpperInvariant(),
            "/" + escapedPath.TrimStart('/'),
            string.Empty,
            bodyHash,
            timestampValue,
            nonce,
            session.SessionId,
            ZarzAppVersion,
            ZarzPlatform);
        var signature = Base64UrlNoPadding(HmacSha256(Encoding.UTF8.GetBytes(rollingKey), Encoding.UTF8.GetBytes(signingInput)));

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", $"SpotiFLAC-Mobile/{ZarzAppVersion}");
        request.Headers.TryAddWithoutValidation("X-Zarz-Session", session.SessionId);
        request.Headers.TryAddWithoutValidation("X-Zarz-Timestamp", timestampValue);
        request.Headers.TryAddWithoutValidation("X-Zarz-Nonce", nonce);
        request.Headers.TryAddWithoutValidation("X-Zarz-Body-SHA256", bodyHash);
        request.Headers.TryAddWithoutValidation("X-Zarz-Signature", signature);
        request.Headers.TryAddWithoutValidation("X-Zarz-App-Version", ZarzAppVersion);
        request.Headers.TryAddWithoutValidation("X-Zarz-Platform", ZarzPlatform);
        if (extraHeaders != null)
        {
            foreach (var header in extraHeaders)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (bodyBytes != null)
        {
            request.Content = new ByteArrayContent(bodyBytes);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        var response = await _client.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            await ClearZarzSignedSessionAsync(cancellationToken);
        }

        return response;
    }

    private async Task<ZarzSignedSessionRecord> EnsureZarzSignedSessionAsync(CancellationToken cancellationToken)
    {
        await ZarzSessionGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await LoadZarzSignedSessionNoLockAsync(cancellationToken);
            if (existing?.IsUsable == true)
            {
                return existing;
            }

            var verificationUrl = await BootstrapZarzSignedSessionNoLockAsync(existing?.InstallId, cancellationToken);
            existing = await LoadZarzSignedSessionNoLockAsync(cancellationToken);
            if (existing?.IsUsable == true)
            {
                return existing;
            }

            throw new InvalidOperationException(string.IsNullOrWhiteSpace(verificationUrl)
                ? "Tidal public download verification could not be completed automatically."
                : "Tidal public download verification requires an interactive challenge and cannot run automatically.");
        }
        finally
        {
            ZarzSessionGate.Release();
        }
    }

    public async Task<bool> HasPublicDownloadSessionAsync(CancellationToken cancellationToken)
    {
        await ZarzSessionGate.WaitAsync(cancellationToken);
        try
        {
            return (await LoadZarzSignedSessionNoLockAsync(cancellationToken))?.IsUsable == true;
        }
        finally
        {
            ZarzSessionGate.Release();
        }
    }

    public async Task<string?> BeginPublicDownloadVerificationAsync(CancellationToken cancellationToken)
    {
        await ZarzSessionGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await LoadZarzSignedSessionNoLockAsync(cancellationToken);
            if (existing?.IsUsable == true)
            {
                return null;
            }

            return await BootstrapZarzSignedSessionNoLockAsync(existing?.InstallId, cancellationToken);
        }
        finally
        {
            ZarzSessionGate.Release();
        }
    }

    public async Task CompletePublicDownloadVerificationAsync(
        string grant,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(grant))
        {
            throw new InvalidOperationException("Tidal public download verification grant is missing.");
        }

        await ZarzSessionGate.WaitAsync(cancellationToken);
        try
        {
            var record = await LoadZarzSignedSessionNoLockAsync(cancellationToken)
                ?? throw new InvalidOperationException("Tidal public download verification was not started.");
            if (string.IsNullOrWhiteSpace(record.InstallId))
            {
                throw new InvalidOperationException("Tidal public download install identity is missing.");
            }

            var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    grant = grant.Trim(),
                    install_id = record.InstallId,
                    app_version = ZarzAppVersion,
                    platform = ZarzPlatform
                },
                SerializerOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildZarzSignedUri(ZarzExchangePath))
            {
                Content = new ByteArrayContent(bodyBytes)
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", $"SpotiFLAC-Mobile/{ZarzAppVersion}");
            using var response = await _client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(BuildZarzHttpFailureMessage("Tidal Zarz session exchange", response, body));
            }

            var exchanged = JsonSerializer.Deserialize<ZarzBootstrapResponse>(body, SerializerOptions);
            record.SessionId = exchanged?.SessionId ?? string.Empty;
            record.SessionSecret = exchanged?.SessionSecret ?? string.Empty;
            record.ExpiresAt = exchanged?.ExpiresAt;
            if (!record.IsUsable)
            {
                throw new InvalidOperationException("Tidal Zarz session exchange did not return a usable session.");
            }

            await SaveZarzSignedSessionNoLockAsync(record, cancellationToken);
        }
        finally
        {
            ZarzSessionGate.Release();
        }
    }

    private async Task<ZarzSignedSessionRecord?> LoadZarzSignedSessionNoLockAsync(CancellationToken cancellationToken)
    {
        if (!IOFile.Exists(_zarzSessionPath))
        {
            return null;
        }

        try
        {
            var json = await IOFile.ReadAllTextAsync(_zarzSessionPath, cancellationToken);
            return JsonSerializer.Deserialize<ZarzSignedSessionRecord>(json, SerializerOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to load Tidal Zarz signed session.");
            return null;
        }
    }

    private async Task SaveZarzSignedSessionNoLockAsync(ZarzSignedSessionRecord session, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_zarzSessionPath) ?? DeezSpoTagDataRootResolver.Resolve());
        await IOFile.WriteAllTextAsync(_zarzSessionPath, JsonSerializer.Serialize(session, SerializerOptions), cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            IOFile.SetUnixFileMode(_zarzSessionPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private async Task ClearZarzSignedSessionAsync(CancellationToken cancellationToken)
    {
        await ZarzSessionGate.WaitAsync(cancellationToken);
        try
        {
            if (IOFile.Exists(_zarzSessionPath))
            {
                IOFile.Delete(_zarzSessionPath);
            }
        }
        finally
        {
            ZarzSessionGate.Release();
        }
    }

    private async Task<string?> BootstrapZarzSignedSessionNoLockAsync(
        string? existingInstallId,
        CancellationToken cancellationToken)
    {
        var installId = string.IsNullOrWhiteSpace(existingInstallId) ? Guid.NewGuid().ToString("N") : existingInstallId;
        var builder = new UriBuilder(BuildZarzSignedUri(ZarzBootstrapPath))
        {
            Query = string.Join(
                "&",
                new Dictionary<string, string>
                {
                    ["app_version"] = ZarzAppVersion,
                    ["install_id"] = installId
                }.Select(pair => $"{WebUtility.UrlEncode(pair.Key)}={WebUtility.UrlEncode(pair.Value)}"))
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", $"SpotiFLAC-Mobile/{ZarzAppVersion}");
        using var response = await _client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(BuildZarzHttpFailureMessage("Tidal Zarz bootstrap", response, body));
        }

        var payload = JsonSerializer.Deserialize<ZarzBootstrapResponse>(body, SerializerOptions);
        var session = new ZarzSignedSessionRecord
        {
            InstallId = installId,
            SessionId = payload?.SessionId ?? string.Empty,
            SessionSecret = payload?.SessionSecret ?? string.Empty,
            ExpiresAt = payload?.ExpiresAt
        };
        if (session.IsUsable)
        {
            await SaveZarzSignedSessionNoLockAsync(session, cancellationToken);
            return null;
        }

        await SaveZarzSignedSessionNoLockAsync(session, cancellationToken);
        var verificationUrl = payload?.AuthUrl ?? payload?.ChallengeUrl;
        if (string.IsNullOrWhiteSpace(verificationUrl) && !string.IsNullOrWhiteSpace(payload?.ChallengeId))
        {
            verificationUrl = BuildZarzChallengeUrl(payload.ChallengeId);
        }

        return !string.IsNullOrWhiteSpace(verificationUrl)
            ? verificationUrl
            : throw new InvalidOperationException("Tidal Zarz bootstrap did not return a verification challenge.");
    }

    private static string BuildZarzChallengeUrl(string challengeId)
    {
        var challengeBuilder = new UriBuilder(BuildZarzSignedUri(ZarzChallengePath))
        {
            Query = $"id={WebUtility.UrlEncode(challengeId)}"
        };
        return challengeBuilder.Uri.ToString();
    }

    private static Uri BuildZarzSignedUri(string path)
    {
        var baseUri = new Uri(ZarzSignedBaseUrl.TrimEnd('/') + "/");
        return new Uri(baseUri, path.TrimStart('/'));
    }

    private static string Sha256Hex(byte[] value)
    {
        var hash = SHA256.HashData(value);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] HmacSha256(byte[] key, byte[] value)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(value);
    }

    private static string Base64UrlNoPadding(byte[] value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string RandomHex(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Convert.ToHexString(bytes).ToLowerInvariant();
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
        string requestedQuality,
        bool preserveManifestAudio,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        List<string>? mismatchSummaries = null;
        for (var index = 0; index < candidateUrls.Count; index++)
        {
            var candidateUrl = candidateUrls[index];
            var candidateOutputPath = DownloadFileUtilities.BuildFormatPreservingStagingPath(
                outputPath,
                $"candidate-{index + 1}.part");
            try
            {
                EnsureTidalManifestMatchesRequestedQuality(candidateUrl, requestedQuality, preserveManifestAudio);
                await DownloadManifestCandidateAsync(candidateUrl, candidateOutputPath, preserveManifestAudio, progressCallback, cancellationToken);
                if (IsDownloadedCandidateAcceptable(candidateOutputPath, expectedDurationSeconds, preserveManifestAudio, out var actualDurationSeconds))
                {
                    IOFile.Move(candidateOutputPath, outputPath, overwrite: false);
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
                DeleteCandidateArtifacts(candidateOutputPath);
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
        DownloadFileUtilities.TryDeleteFile(candidateOutputPath + ".m4a.tmp");

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
        if (!IOFile.Exists(filePath))
        {
            return false;
        }

        var validation = AudioDurationGuard.ValidateAgainstPreview(filePath, expectedDurationSeconds);
        return validation.Success;
    }

    private static bool IsAtmosDurationAcceptable(string filePath, int expectedDurationSeconds, out double actualDurationSeconds)
    {
        actualDurationSeconds = 0;
        if (!IOFile.Exists(filePath) || new FileInfo(filePath).Length == 0)
        {
            return false;
        }

        if (expectedDurationSeconds <= 0)
        {
            return true;
        }

        if (!TryReadFfprobeAtmosAudio(filePath, out actualDurationSeconds))
        {
            return true;
        }

        return AudioDurationGuard.IsExpectedDurationAcceptable(actualDurationSeconds, expectedDurationSeconds);
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
            var direct = JsonSerializer.Deserialize<TidalPlaybackInfoResponse>(body, SerializerOptions);
            if (!string.IsNullOrWhiteSpace(direct?.Manifest))
            {
                manifest = ManifestPrefix + direct.Manifest;
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
        var credentialManifest = await TryFetchManifestFromCredentialApiAsync(trackId, quality, cancellationToken);
        if (!string.IsNullOrWhiteSpace(credentialManifest))
        {
            return [credentialManifest];
        }

        var providers = await _providerSource.GetRotatedProviderRecordsAsync(cancellationToken);
        if (providers.Count == 0)
        {
            throw new InvalidOperationException("Tidal API pool is empty");
        }

        var availableProviders = providers
            .Where(candidate => !IsProviderCoolingDown(candidate))
            .ToArray();
        if (availableProviders.Length == 0)
        {
            throw new InvalidOperationException("No Tidal download provider is currently available.");
        }

        Exception? lastFailure = null;
        foreach (var provider in availableProviders)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var manifest = await FetchManifestFromProviderAsync(provider, trackId, quality, cancellationToken);
                stopwatch.Stop();
                if (string.IsNullOrWhiteSpace(manifest))
                {
                    await _providerSource.RememberFailureAsync(provider, "empty_response", stopwatch.ElapsedMilliseconds, cancellationToken);
                    lastFailure = new InvalidOperationException(
                        $"Tidal provider {provider.DisplayName} returned no download manifest.");
                    continue;
                }

                EnsureTidalManifestMatchesRequestedQuality(manifest, quality, preserveManifestAudio: false);
                await _providerSource.RememberHealthSuccessAsync(provider, stopwatch.ElapsedMilliseconds, cancellationToken);
                await _providerSource.RememberSuccessAsync(provider, cancellationToken);
                return [manifest.Trim()];
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();
                lastFailure = ex;
                var category = ClassifyProviderFailure(ex);
                await _providerSource.RememberFailureAsync(provider, category, stopwatch.ElapsedMilliseconds, ResolveProviderCooldown(category, ex), cancellationToken);
                _logger.LogWarning(
                    ex,
                    "Tidal public provider {Provider} failed for track {TrackId} quality {Quality}.",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(provider.DisplayName),
                    trackId,
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(quality));
            }
        }

        throw new InvalidOperationException(
            "All enabled Tidal download providers failed to return a usable manifest.",
            lastFailure);
    }

    private async Task<string?> TryFetchManifestFromCredentialApiAsync(
        long trackId,
        string quality,
        CancellationToken cancellationToken)
    {
        try
        {
            var token = await _accessTokenProvider.GetAccessTokenAsync(cancellationToken);
            var countryCode = await _accessTokenProvider.GetCountryCodeAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            var normalizedQuality = NormalizeTidalDownloadQuality(quality);
            var builder = new UriBuilder(Uri.UriSchemeHttps, "api.tidal.com")
            {
                Path = $"v1/tracks/{trackId}/playbackinfopostpaywall",
                Query = string.Join(
                    "&",
                    new Dictionary<string, string>
                    {
                        ["audioquality"] = normalizedQuality,
                        ["playbackmode"] = "STREAM",
                        ["assetpresentation"] = "FULL",
                        ["countryCode"] = string.IsNullOrWhiteSpace(countryCode) ? TidalPublicCountryCode : countryCode
                    }.Select(pair => $"{WebUtility.UrlEncode(pair.Key)}={WebUtility.UrlEncode(pair.Value)}"))
            };

            using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await _client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Tidal credential playback info returned HTTP {StatusCode} for track {TrackId} quality {Quality}.",
                        (int)response.StatusCode,
                        trackId,
                        DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedQuality));
                }

                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (BodyContainsPreviewAsset(body))
            {
                _logger.LogWarning(
                    "Tidal credential playback info returned a preview asset for track {TrackId} quality {Quality}; trying the download provider fallback.",
                    trackId,
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedQuality));
                return null;
            }

            if (!TryParseManifest(body, out var manifest))
            {
                return null;
            }

            EnsureTidalManifestMatchesRequestedQuality(manifest, quality, preserveManifestAudio: false);
            return manifest;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    ex,
                    "Tidal credential playback info failed for track {TrackId} quality {Quality}.",
                    trackId,
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(quality));
            }

            return null;
        }
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

    private static DateTimeOffset? ResolveProviderCooldown(string category, Exception exception)
    {
        if (category != "rate_limited")
        {
            return category is "empty_response" ? DateTimeOffset.UtcNow.AddMinutes(15) : null;
        }

        var retryAfter = ExtractRetryAfterSeconds(exception.Message);
        return retryAfter.HasValue
            ? DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(retryAfter.Value, 60, 86400))
            : DateTimeOffset.UtcNow.AddMinutes(15);
    }

    private static string BuildZarzHttpFailureMessage(string providerName, HttpResponseMessage response, string body)
    {
        var status = (int)response.StatusCode;
        var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds;
        if (!retryAfter.HasValue)
        {
            retryAfter = ExtractRetryAfterSeconds(body);
        }

        return retryAfter.HasValue
            ? $"{providerName} returned HTTP {status}; retry_after={Math.Ceiling(retryAfter.Value).ToString(CultureInfo.InvariantCulture)}."
            : $"{providerName} returned HTTP {status}.";
    }

    private static int? ExtractRetryAfterSeconds(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = MatchWithTimeout(value, "\"?retry_after\"?\\s*[:=]\\s*(\\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;
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
        => TidalStereoQuality.ToTidalRequestQuality(quality);

    private static void EnsureTidalManifestMatchesRequestedQuality(
        string candidate,
        string requestedQuality,
        bool preserveManifestAudio)
    {
        if (preserveManifestAudio || !candidate.StartsWith(ManifestPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var manifest = ParseManifest(candidate);
        var actual = ClassifyTidalManifestQuality(manifest);
        if (TidalManifestQualityMatchesRequest(TidalStereoQuality.Normalize(requestedQuality), actual))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Tidal manifest quality mismatch: requested {TidalStereoQuality.FormatRequested(requestedQuality)} but provider returned {FormatTidalManifestQuality(actual)}.");
    }

    private static bool TidalManifestQualityMatchesRequest(
        TidalStereoQualityTier requested,
        TidalManifestQuality actual)
    {
        return requested switch
        {
            TidalStereoQualityTier.Low or TidalStereoQualityTier.High
                => actual is TidalManifestQuality.Unknown or TidalManifestQuality.Lossy,
            TidalStereoQualityTier.CdLossless
                => actual is TidalManifestQuality.Unknown or TidalManifestQuality.CdLossless,
            TidalStereoQualityTier.HiRes
                => actual is TidalManifestQuality.Unknown or TidalManifestQuality.HiRes,
            TidalStereoQualityTier.MaxHiRes
                => actual is TidalManifestQuality.Unknown or TidalManifestQuality.MaxHiRes,
            TidalStereoQualityTier.DolbyAtmos => true,
            _ => true
        };
    }

    private static TidalManifestQuality ClassifyTidalManifestQuality(TidalManifestInfo manifest)
    {
        if (IsAtmosManifest(manifest))
        {
            return TidalManifestQuality.Atmos;
        }

        var haystack = string.Join(
            ' ',
            manifest.MimeType,
            manifest.Codecs,
            manifest.RawText).Trim();
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return TidalManifestQuality.Unknown;
        }

        var hasFlac = ContainsFlacSignal(haystack);
        if (!hasFlac && ContainsLossySignal(haystack))
        {
            return TidalManifestQuality.Lossy;
        }

        if (!hasFlac)
        {
            return TidalManifestQuality.Unknown;
        }

        var sampleRate = ExtractMaximumTidalManifestSampleRate(haystack);
        var bitDepth = ExtractMaximumTidalManifestBitDepth(haystack);
        if (sampleRate > 96000)
        {
            return TidalManifestQuality.MaxHiRes;
        }

        if (sampleRate > 48000 || bitDepth >= 24 || ContainsHiResSignal(haystack))
        {
            return TidalManifestQuality.HiRes;
        }

        return TidalManifestQuality.CdLossless;
    }

    private static bool ContainsFlacSignal(string value)
        => value.Contains("FLAC", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsHiResSignal(string value)
        => value.Contains("FLAC_HIRES", StringComparison.OrdinalIgnoreCase)
           || value.Contains("HIRES_LOSSLESS", StringComparison.OrdinalIgnoreCase)
           || value.Contains("HI_RES_LOSSLESS", StringComparison.OrdinalIgnoreCase)
           || value.Contains("HI-RES", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsLossySignal(string value)
        => value.Contains("MP4A", StringComparison.OrdinalIgnoreCase)
           || value.Contains("AAC", StringComparison.OrdinalIgnoreCase)
           || value.Contains("HEAAC", StringComparison.OrdinalIgnoreCase)
           || value.Contains("HE-AAC", StringComparison.OrdinalIgnoreCase)
           || value.Contains("AUDIO/MP4", StringComparison.OrdinalIgnoreCase)
           || value.Contains("M4A", StringComparison.OrdinalIgnoreCase);

    private static int ExtractMaximumTidalManifestSampleRate(string value)
    {
        var maximum = 0;
        foreach (Match match in MatchesWithTimeout(
                     value,
                     "(?:audioSamplingRate|sampleRate|samplingRate|sample_rate)\\s*[\"':=]+\\s*\"?(?<rate>\\d{4,6})",
                     RegexOptions.IgnoreCase))
        {
            if (int.TryParse(match.Groups["rate"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rate))
            {
                maximum = Math.Max(maximum, rate);
            }
        }

        return maximum;
    }

    private static int ExtractMaximumTidalManifestBitDepth(string value)
    {
        var maximum = 0;
        foreach (Match match in MatchesWithTimeout(
                     value,
                     "(?:bitDepth|bitsPerSample|bit_depth)\\s*[\"':=]+\\s*\"?(?<bits>\\d{1,2})",
                     RegexOptions.IgnoreCase))
        {
            if (int.TryParse(match.Groups["bits"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits))
            {
                maximum = Math.Max(maximum, bits);
            }
        }

        return maximum;
    }

    private static string FormatTidalManifestQuality(TidalManifestQuality quality)
        => quality switch
        {
            TidalManifestQuality.Lossy => "lossy AAC/M4A",
            TidalManifestQuality.CdLossless => "Tidal CD Lossless",
            TidalManifestQuality.HiRes => "Tidal Hi-Res",
            TidalManifestQuality.MaxHiRes => "Tidal Max Hi-Res",
            TidalManifestQuality.Atmos => "Tidal Dolby Atmos",
            _ => "unverified Tidal quality"
        };

    private enum TidalManifestQuality
    {
        Unknown = 0,
        Lossy = 1,
        CdLossless = 2,
        HiRes = 3,
        MaxHiRes = 4,
        Atmos = 5
    }

    private sealed class ZarzSignedSessionRecord
    {
        [JsonPropertyName("install_id")]
        public string InstallId { get; set; } = "";

        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("session_secret")]
        public string SessionSecret { get; set; } = "";

        [JsonPropertyName("expires_at")]
        public DateTimeOffset? ExpiresAt { get; set; }

        [JsonIgnore]
        public bool IsUsable
            => !string.IsNullOrWhiteSpace(SessionId)
                && !string.IsNullOrWhiteSpace(SessionSecret)
                && (!ExpiresAt.HasValue || ExpiresAt.Value > DateTimeOffset.UtcNow.AddMinutes(1));
    }

    private sealed class ZarzBootstrapResponse
    {
        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        [JsonPropertyName("session_secret")]
        public string? SessionSecret { get; set; }

        [JsonPropertyName("expires_at")]
        public DateTimeOffset? ExpiresAt { get; set; }

        [JsonPropertyName("auth_url")]
        public string? AuthUrl { get; set; }

        [JsonPropertyName("challenge_url")]
        public string? ChallengeUrl { get; set; }

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

    private sealed class TidalPlaybackInfoResponse
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
