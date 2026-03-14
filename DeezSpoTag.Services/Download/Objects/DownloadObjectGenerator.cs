using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Download;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Core.Models.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Download.Shared.Errors;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Utils;
using static DeezSpoTag.Integrations.Deezer.DeezerGatewayService;
using DeezerModels = DeezSpoTag.Integrations.Deezer;
using System.Net.Http;
using System.Linq;
using System.Text.Json;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace DeezSpoTag.Services.Download.Objects;

/// <summary>
/// Unified download object generator (merged from multiple implementations)
/// Generate download objects from Deezer content (ported from deezspotag download-objects)
/// Enhanced with URL parsing and metadata population
/// </summary>
public class DownloadObjectGenerator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private const string TrackType = "track";
    private const string EpisodeType = "episode";
    private const string ShowType = "show";
    private const string AlbumType = "album";
    private const string PlaylistType = "playlist";
    private const string UnknownValue = "Unknown";
    private const string ArtistType = "artist";
    private const string ArtistTopType = "artist_top";
    private const string CoverPictureType = "cover";
    private const string ArtistPictureType = "artist";
    private const string UnknownArtist = "Unknown Artist";
    private const string ResultsField = "results";
    private const string FeaturedArtistGroup = "Featured";
    private const string DeezerClientNotAuthenticatedMessage = "Deezer client not authenticated";
    private readonly ILogger<DownloadObjectGenerator> _logger;
    private readonly AuthenticatedDeezerService _authenticatedDeezerService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DeezerPipeService _deezerPipeService;
    private readonly TrackEnrichmentService _trackEnrichmentService;
    private static bool IsMatchWithTimeout(string input, string pattern, System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.None)
        => System.Text.RegularExpressions.Regex.IsMatch(input, pattern, options, RegexTimeout);
    private static System.Text.RegularExpressions.Match MatchWithTimeout(string input, string pattern, System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.None)
        => System.Text.RegularExpressions.Regex.Match(input, pattern, options, RegexTimeout);

    public DownloadObjectGenerator(
        ILogger<DownloadObjectGenerator> logger,
        AuthenticatedDeezerService authenticatedDeezerService,
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        DeezerPipeService deezerPipeService,
        TrackEnrichmentService trackEnrichmentService)
    {
        _logger = logger;
        _authenticatedDeezerService = authenticatedDeezerService;
        _serviceProvider = serviceProvider;
        _httpClientFactory = httpClientFactory;
        _deezerPipeService = deezerPipeService;
        _trackEnrichmentService = trackEnrichmentService;
    }

    #region Enhanced Methods (merged from EnhancedDownloadObjectGenerator)

    /// <summary>
    /// Generate download object from URL with enhanced metadata
    /// Ported from: generateDownloadObject function in deezspotag
    /// </summary>
    public async Task<List<DownloadObject>> GenerateDownloadObjectFromUrlAsync(string url, int bitrate)
    {
        try
        {
            _logger.LogDebug("Generating download object for URL: {Url}", url);

            var (parsedUrl, type, id) = await ParseDeezerUrlAsync(url);

            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(id))
            {
                throw new LinkNotRecognizedException(parsedUrl);
            }

            return type.ToLower() switch
            {
                TrackType => new List<DownloadObject> { await GenerateTrackItemAsync(id, bitrate) },
                EpisodeType => new List<DownloadObject> { await GenerateEpisodeItemAsync(id, bitrate) },
                ShowType => await GenerateShowItemsAsync(id, bitrate),
                AlbumType => new List<DownloadObject> { await GenerateAlbumItemAsync(id, bitrate) },
                PlaylistType => new List<DownloadObject> { await GeneratePlaylistItemAsync(id, bitrate) },
                ArtistType => await GenerateArtistItemAsync(id, bitrate, "all"),
                ArtistTopType => new List<DownloadObject> { await GenerateArtistTopItemAsync(id, bitrate) },
                var tabType when tabType.StartsWith($"{ArtistType}_") => await GenerateArtistItemAsync(id, bitrate, tabType.Substring(7)),
                _ => throw new LinkNotSupportedException(parsedUrl)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Error generating download object for URL: {url}", ex);
        }
    }

    /// <summary>
    /// Parse Deezer URL to extract type and ID (exact port of deezspotag parseLink)
    /// </summary>
    private async Task<(string url, string? type, string? id)> ParseDeezerUrlAsync(string url)
    {
        try
        {
            var resolvedUrl = await ResolveDeezerPageLinkAsync(url);
            var cleanedUrl = CleanDeezerUrl(resolvedUrl);

            // Handle direct IDs
            if (IsMatchWithTimeout(cleanedUrl, @"^\d+$"))
            {
                return (cleanedUrl, TrackType, cleanedUrl); // Default to track for numeric IDs
            }

            // Handle ISRC
            if (cleanedUrl.StartsWith("isrc"))
            {
                return (cleanedUrl, TrackType, cleanedUrl);
            }

            // Handle UPC
            if (cleanedUrl.StartsWith("upc"))
            {
                return (cleanedUrl, AlbumType, cleanedUrl);
            }

            if (!cleanedUrl.Contains("deezer"))
            {
                return (cleanedUrl, null, null);
            }

            // Handle Deezer URLs (exact regex patterns from deezspotag)
            var trackMatch = MatchWithTimeout(cleanedUrl, @"/track/(.+)");
            if (trackMatch.Success)
            {
                return (cleanedUrl, TrackType, trackMatch.Groups[1].Value);
            }

            var episodeMatch = MatchWithTimeout(cleanedUrl, @"/episode/(\d+)");
            if (episodeMatch.Success)
            {
                return (cleanedUrl, EpisodeType, episodeMatch.Groups[1].Value);
            }

            var playlistMatch = MatchWithTimeout(cleanedUrl, @"/playlist/(\d+)");
            if (playlistMatch.Success)
            {
                return (cleanedUrl, PlaylistType, playlistMatch.Groups[1].Value);
            }

            var albumMatch = MatchWithTimeout(cleanedUrl, @"/album/(.+)");
            if (albumMatch.Success)
            {
                return (cleanedUrl, AlbumType, albumMatch.Groups[1].Value);
            }

            var artistTopMatch = MatchWithTimeout(cleanedUrl, @"/artist/(\d+)/top_track");
            if (artistTopMatch.Success)
            {
                return (cleanedUrl, "artist_top", artistTopMatch.Groups[1].Value);
            }

            var artistTabMatch = MatchWithTimeout(cleanedUrl, @"/artist/(\d+)/(.+)");
            if (artistTabMatch.Success)
            {
                return (cleanedUrl, $"artist_{artistTabMatch.Groups[2].Value}", artistTabMatch.Groups[1].Value);
            }

            var artistMatch = MatchWithTimeout(cleanedUrl, @"/artist/(\d+)");
            if (artistMatch.Success)
            {
                return (cleanedUrl, ArtistType, artistMatch.Groups[1].Value);
            }

            var showMatch = MatchWithTimeout(cleanedUrl, @"/show/(\d+)");
            if (showMatch.Success)
            {
                return (cleanedUrl, ShowType, showMatch.Groups[1].Value);
            }

            return (cleanedUrl, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error parsing Deezer URL: {Url}", url);
            throw new InvalidOperationException($"Invalid Deezer URL: {url}");
        }
    }

    private async Task<string> ResolveDeezerPageLinkAsync(string url)
    {
        if (!url.Contains("deezer.page.link"))
        {
            return url;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("DeezSpoTagDownload");
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            var resolved = response.RequestMessage?.RequestUri?.ToString();
            return string.IsNullOrEmpty(resolved) ? url : resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve Deezer short link: {Url}", url);
            return url;
        }
    }

    private static string CleanDeezerUrl(string url)
    {
        var cleanedUrl = url;
        if (cleanedUrl.Contains('?'))
        {
            cleanedUrl = cleanedUrl.Substring(0, cleanedUrl.IndexOf('?', StringComparison.Ordinal));
        }
        if (cleanedUrl.Contains('&'))
        {
            cleanedUrl = cleanedUrl.Substring(0, cleanedUrl.IndexOf('&', StringComparison.Ordinal));
        }
        if (cleanedUrl.EndsWith('/'))
        {
            cleanedUrl = cleanedUrl.TrimEnd('/');
        }
        return cleanedUrl;
    }

    /// <summary>
    /// Generate cover URL for images
    /// </summary>
    public static string GenerateCoverUrl(string md5, string type, int size = 75)
    {
        if (string.IsNullOrEmpty(md5))
        {
            return $"https://e-cdns-images.dzcdn.net/images/{type}//{size}x{size}-000000-80-0-0.jpg";
        }

        return $"https://e-cdns-images.dzcdn.net/images/{type}/{md5}/{size}x{size}-000000-80-0-0.jpg";
    }

    /// <summary>
    /// Generate artist picture URL
    /// </summary>
    public static string GenerateArtistPictureUrl(string md5, int size)
    {
        if (string.IsNullOrEmpty(md5))
        {
            return $"https://e-cdns-images.dzcdn.net/images/artist//{size}x{size}-000000-80-0-0.jpg";
        }

        return $"https://e-cdns-images.dzcdn.net/images/artist/{md5}/{size}x{size}-000000-80-0-0.jpg";
    }

    #endregion

    /// <summary>
    /// Generate track download object (port of generateDeezSpoTag.Integrations.Deezer.DeezSpoTag.Integrations.Deezer.DeezSpoTag.Integrations.Deezer.DeezSpoTag.Integrations.Deezer.TrackItem)
    /// CRITICAL FIX: Now properly populates metadata like deezspotag parseData method
    /// </summary>
    public async Task<SingleDownloadObject> GenerateTrackItemAsync(
        string trackId,
        int bitrate,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Generating track item for ID: {TrackId}", trackId);

        var deezerClient = await _authenticatedDeezerService.GetAuthenticatedClientAsync();
        if (deezerClient == null)
        {
            throw new InvalidOperationException(DeezerClientNotAuthenticatedMessage);
        }

        // CRITICAL FIX: Get track data from authenticated client (includes MD5, MediaVersion, etc.)
        var trackData = await deezerClient.GetTrackWithFallbackAsync(trackId);
        if (trackData == null)
        {
            throw new InvalidOperationException($"Track not found: {trackId}");
        }

        DeezSpoTag.Core.Models.Track track = MapGwTrackToTrack(trackData);
        
        // CRITICAL FIX: Parse track data like deezspotag parseTrack method
        ParseTrackData(track, trackData);

        // Reuse shared enrichment engine so object-generation and download phases use one enrichment core.
        await _trackEnrichmentService.EnrichCoreTrackAsync(track, new Dictionary<string, object>(), cancellationToken);
        var publicTrackData = await TryEnhanceTrackWithPublicApiDataAsync(deezerClient, trackId, track);
        await TryApplyPipeMetadataAsync(trackId, track, cancellationToken);
        await TryLoadTrackLyricsAsync(deezerClient, track, cancellationToken);
        var (album, fetchedAlbumData) = await TryAttachTrackAlbumAsync(deezerClient, trackData, track);
        album ??= EnsureMinimalTrackAlbum(track, trackData);
        track.Album = album;
        ApplyTrackSettings(track, album, publicTrackData, fetchedAlbumData);

        var downloadObject = new SingleDownloadObject
        {
            Title = track.Title,
            Type = TrackType,
            Bitrate = bitrate,
            Track = track,
            Album = album
        };

        downloadObject.Uuid = $"track_{trackId}_{bitrate}";
        
        _logger.LogInformation("Generated track download object with full metadata: {Title} by {Artist} from {Album}", 
            downloadObject.Title, track.MainArtist?.Name, album?.Title);
        return downloadObject;
    }

    private async Task<ApiTrack?> TryEnhanceTrackWithPublicApiDataAsync(
        DeezerModels.DeezerClient deezerClient,
        string trackId,
        DeezSpoTag.Core.Models.Track track)
    {
        try
        {
            var publicTrackData = await deezerClient.GetTrackAsync(trackId);
            if (publicTrackData == null)
            {
                return null;
            }

            TryParsePublicTrackIntoTrack(trackId, track, publicTrackData);
            ApplyPublicTrackMetadata(track, publicTrackData);
            _logger.LogDebug("Enhanced track with public API data: BPM={Bpm}, Copyright={Copyright}",
                track.Bpm, track.Copyright);
            return publicTrackData;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not get public API data for track {TrackId}, continuing with GW data", trackId);
            return null;
        }
    }

    private void TryParsePublicTrackIntoTrack(string trackId, DeezSpoTag.Core.Models.Track track, ApiTrack publicTrackData)
    {
        try
        {
            track.ParseTrack(publicTrackData);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to parse public API track data for {TrackId}", trackId);
        }
    }

    private static void ApplyPublicTrackMetadata(DeezSpoTag.Core.Models.Track track, ApiTrack publicTrackData)
    {
        track.Bpm = publicTrackData.Bpm;
        track.Copyright = publicTrackData.Copyright ?? "";
        if (!string.IsNullOrEmpty(publicTrackData.ReleaseDate)
            && DateTime.TryParse(publicTrackData.ReleaseDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseDate))
        {
            track.Date.Day = releaseDate.Day.ToString("D2");
            track.Date.Month = releaseDate.Month.ToString("D2");
            track.Date.Year = releaseDate.Year.ToString();
            track.Date.FixDayMonth();
        }
    }

    private async Task TryApplyPipeMetadataAsync(string trackId, DeezSpoTag.Core.Models.Track track, CancellationToken cancellationToken)
    {
        try
        {
            var pipeMetadata = await _deezerPipeService.TryGetTrackMetadataAsync(trackId, cancellationToken);
            if (pipeMetadata == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(track.Title) && !string.IsNullOrWhiteSpace(pipeMetadata.Title))
            {
                track.Title = pipeMetadata.Title;
            }

            if (track.Duration <= 0 && pipeMetadata.Duration.HasValue)
            {
                track.Duration = pipeMetadata.Duration.Value;
            }

            if (string.IsNullOrWhiteSpace(track.ISRC) && !string.IsNullOrWhiteSpace(pipeMetadata.Isrc))
            {
                track.ISRC = pipeMetadata.Isrc;
            }

            if (track.MainArtist == null && !string.IsNullOrWhiteSpace(pipeMetadata.ArtistId))
            {
                track.MainArtist = new DeezSpoTag.Core.Models.Artist(pipeMetadata.ArtistId, pipeMetadata.ArtistName ?? UnknownValue, "Main");
            }

            if (track.Album == null && !string.IsNullOrWhiteSpace(pipeMetadata.AlbumId))
            {
                track.Album = BuildPipeAlbum(pipeMetadata.AlbumId, pipeMetadata.AlbumTitle, pipeMetadata.AlbumCoverUrl);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Pipe metadata lookup failed for track {TrackId}", trackId);
        }
    }

    private static DeezSpoTag.Core.Models.Album BuildPipeAlbum(string albumId, string? albumTitle, string? albumCoverUrl)
    {
        var pipeAlbum = new DeezSpoTag.Core.Models.Album(albumId, albumTitle ?? UnknownValue);
        var md5 = ExtractCoverMd5(albumCoverUrl);
        if (!string.IsNullOrWhiteSpace(md5))
        {
            pipeAlbum.Md5Image = md5;
            pipeAlbum.Pic = new Picture(md5, CoverPictureType);
        }
        return pipeAlbum;
    }

    private async Task TryLoadTrackLyricsAsync(
        DeezerModels.DeezerClient deezerClient,
        DeezSpoTag.Core.Models.Track track,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(track.LyricsId) || track.LyricsId == "0")
        {
            return;
        }

        try
        {
            var lyricsData = await deezerClient.Gw.GetLyricsAsync(track.Id, cancellationToken: cancellationToken);
            if (lyricsData == null)
            {
                return;
            }

            track.Lyrics = new DeezSpoTag.Core.Models.Lyrics
            {
                Id = track.LyricsId,
                Sync = lyricsData.GetValueOrDefault("LYRICS_SYNC_JSON", "")?.ToString() ?? "",
                Unsync = lyricsData.GetValueOrDefault("LYRICS_TEXT", "")?.ToString() ?? ""
            };
            _logger.LogDebug("Retrieved lyrics for track {TrackId}: Sync={HasSync}, Unsync={HasUnsync}",
                track.Id,
                !string.IsNullOrEmpty(track.Lyrics.Sync),
                !string.IsNullOrEmpty(track.Lyrics.Unsync));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not get lyrics for track {TrackId}", track.Id);
            track.LyricsId = string.Empty;
        }
    }

    private async Task<(DeezSpoTag.Core.Models.Album? Album, ApiAlbum? ApiAlbum)> TryAttachTrackAlbumAsync(
        DeezerModels.DeezerClient deezerClient,
        GwTrack trackData,
        DeezSpoTag.Core.Models.Track track)
    {
        if (string.IsNullOrWhiteSpace(trackData.AlbId) || trackData.AlbId == "0")
        {
            return (null, null);
        }

        try
        {
            var fetchedAlbumData = await deezerClient.GetAlbumAsync(trackData.AlbId);
            if (fetchedAlbumData == null)
            {
                return (null, null);
            }

            var album = MapApiAlbumToAlbum(fetchedAlbumData);
            track.Album = album;
            if (track.Date.Year == "XXXX" && album.Date.Year != "XXXX")
            {
                track.Date = album.Date;
            }

            await TryPopulateAlbumMainArtistPictureAsync(deezerClient, album);
            _logger.LogDebug("Attached album {AlbumTitle} to track {TrackTitle}", album.Title, track.Title);
            return (album, fetchedAlbumData);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to get album data for track {TrackId}, album {AlbumId}", track.Id, trackData.AlbId);
            return (null, null);
        }
    }

    private async Task TryPopulateAlbumMainArtistPictureAsync(DeezerModels.DeezerClient deezerClient, DeezSpoTag.Core.Models.Album album)
    {
        if (album.MainArtist == null || (album.MainArtist.Pic != null && !string.IsNullOrEmpty(album.MainArtist.Pic.Md5)))
        {
            return;
        }

        try
        {
            _logger.LogDebug("Album main artist missing picture, fetching artist data for {ArtistId}", album.MainArtist.Id);
            var artistData = await deezerClient.GetArtistAsync(album.MainArtist.Id.ToString());
            if (artistData == null)
            {
                return;
            }

            album.MainArtist = MapApiArtistToArtist(artistData);
            _logger.LogDebug("Updated album main artist picture from artist API: {ArtistName} -> {ArtistPicMd5}",
                album.MainArtist.Name, album.MainArtist.Pic?.Md5 ?? "NULL");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to get artist data for album main artist {ArtistId}", album.MainArtist.Id);
        }
    }

    private DeezSpoTag.Core.Models.Album EnsureMinimalTrackAlbum(DeezSpoTag.Core.Models.Track track, GwTrack trackData)
    {
        var mainArtist = track.MainArtist ?? new DeezSpoTag.Core.Models.Artist(UnknownArtist);
        if (mainArtist.Pic == null)
        {
            mainArtist.Pic = new Picture(string.Empty, ArtistPictureType);
            _logger.LogDebug("Set empty artist picture for minimal album (GW data doesn't include artist pictures)");
        }

        var fallbackAlbumTitle = trackData.AlbTitle?.Trim();
        if (string.IsNullOrWhiteSpace(fallbackAlbumTitle))
        {
            fallbackAlbumTitle = !string.IsNullOrWhiteSpace(track.Title) ? track.Title : "Unknown Album";
        }

        var album = new DeezSpoTag.Core.Models.Album(trackData.AlbId ?? "0", fallbackAlbumTitle)
        {
            MainArtist = mainArtist,
            TrackTotal = 1,
            DiscTotal = 1
        };
        album.Artist["Main"] = new List<string> { album.MainArtist.Name };
        album.Artists = new List<string> { album.MainArtist.Name };
        album.Pic = !string.IsNullOrEmpty(trackData.AlbPicture)
            ? new Picture(trackData.AlbPicture, CoverPictureType)
            : new Picture(string.Empty, CoverPictureType);
        if (album.Pic.Md5.Length > 0)
        {
            _logger.LogDebug("Set album cover from track data: {AlbPicture}", trackData.AlbPicture);
        }
        else
        {
            _logger.LogDebug("Set empty album cover for minimal album");
        }
        _logger.LogDebug("Created minimal album for track {TrackTitle}", track.Title);
        return album;
    }

    private void ApplyTrackSettings(
        DeezSpoTag.Core.Models.Track track,
        DeezSpoTag.Core.Models.Album? album,
        ApiTrack? publicTrackData,
        ApiAlbum? fetchedAlbumData)
    {
        using var scope = _serviceProvider.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<DeezSpoTagSettingsService>();
        var currentSettings = settingsService.LoadSettings();
        if (currentSettings.Tags?.SingleAlbumArtist != false)
        {
            NormalizeTrackArtists(track, publicTrackData);
            if (album != null)
            {
                NormalizeAlbumArtist(album, fetchedAlbumData);
            }
        }
        track.ApplySettings(currentSettings);
    }

    private static string? ExtractCoverMd5(string? coverUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            return null;
        }

        var marker = "/images/cover/";
        var start = coverUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = coverUrl.IndexOf('/', start);
        if (end <= start)
        {
            return null;
        }

        return coverUrl.Substring(start, end - start);
    }

    /// <summary>
    /// Generate episode download object for podcasts (streamed directly from Deezer episode URL)
    /// </summary>
    public async Task<SingleDownloadObject> GenerateEpisodeItemAsync(
        string episodeId,
        int bitrate,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Generating episode item for ID: {EpisodeId}", episodeId);

        var deezerClient = await _authenticatedDeezerService.GetAuthenticatedClientAsync();
        if (deezerClient == null)
        {
            throw new InvalidOperationException(DeezerClientNotAuthenticatedMessage);
        }

        var episodeInfo = await GetEpisodeMetadataAsync(episodeId, cancellationToken);
        if (episodeInfo == null)
        {
            throw new InvalidOperationException($"Episode not found: {episodeId}");
        }

        string? streamUrl = null;
        if (!IsDeezerEpisodePage(episodeInfo.DirectUrl ?? string.Empty))
        {
            streamUrl = episodeInfo.DirectUrl;
        }

        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            _logger.LogWarning("Episode stream URL not available yet; deferring resolution to downloader for {EpisodeId}", episodeId);
        }

        var showArtist = new DeezSpoTag.Core.Models.Artist(episodeInfo.ShowId, episodeInfo.ShowTitle, "Main");
        if (!string.IsNullOrWhiteSpace(episodeInfo.ShowPictureMd5))
        {
            showArtist.Pic = new Picture(episodeInfo.ShowPictureMd5, "talk");
        }

        var showAlbum = new DeezSpoTag.Core.Models.Album(episodeInfo.ShowId, episodeInfo.ShowTitle)
        {
            MainArtist = showArtist,
            TrackTotal = 1,
            DiscTotal = 1,
            Pic = string.IsNullOrWhiteSpace(episodeInfo.ShowPictureMd5) ? new Picture("", "talk") : new Picture(episodeInfo.ShowPictureMd5, "talk")
        };

        showAlbum.Artist["Main"] = new List<string> { showArtist.Name };
        showAlbum.Artists = new List<string> { showArtist.Name };

        var track = new DeezSpoTag.Core.Models.Track
        {
            Id = episodeId,
            Title = episodeInfo.Title,
            Duration = episodeInfo.Duration,
            MainArtist = showArtist,
            Album = showAlbum,
            TrackNumber = episodeInfo.EpisodeNumber,
            DiscNumber = 1,
            DiskNumber = 1,
            DownloadURL = string.IsNullOrWhiteSpace(streamUrl) ? string.Empty : streamUrl
        };

        if (!string.IsNullOrWhiteSpace(episodeInfo.ReleaseDate) &&
            DateTime.TryParse(episodeInfo.ReleaseDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseDate))
        {
            track.Date.Day = releaseDate.Day.ToString("D2");
            track.Date.Month = releaseDate.Month.ToString("D2");
            track.Date.Year = releaseDate.Year.ToString();
            track.Date.FixDayMonth();
        }

        track.Artist["Main"] = new List<string> { showArtist.Name };
        track.Artists = new List<string> { showArtist.Name };

        using (var scope = _serviceProvider.CreateScope())
        {
            var settingsService = scope.ServiceProvider.GetRequiredService<DeezSpoTagSettingsService>();
            var currentSettings = settingsService.LoadSettings();
            track.ApplySettings(currentSettings);
        }

        var downloadObject = new SingleDownloadObject
        {
            Title = track.Title,
            Type = EpisodeType,
            Bitrate = bitrate,
            Track = track,
            Album = showAlbum
        };

        downloadObject.Uuid = $"episode_{episodeId}_{bitrate}";

        _logger.LogInformation("Generated episode download object: {Title} ({Show})", track.Title, episodeInfo.ShowTitle);
        return downloadObject;
    }

    private async Task<List<DownloadObject>> GenerateShowItemsAsync(
        string showId,
        int bitrate,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Generating show items for ID: {ShowId}", showId);

        using var scope = _serviceProvider.CreateScope();
        var gatewayService = scope.ServiceProvider.GetRequiredService<DeezerGatewayService>();
        var showPage = await gatewayService.GetShowPageAsync(showId);
        var results = showPage[ResultsField] as JObject ?? showPage;
        var episodes = results["EPISODES"] as JObject ?? results["episodes"] as JObject;
        var episodesData = episodes?["data"] as JArray
                           ?? episodes?["DATA"] as JArray
                           ?? results["EPISODES"] as JArray
                           ?? results["episodes"] as JArray;

        if (episodesData == null || episodesData.Count == 0)
        {
            throw new InvalidOperationException($"Show has no episodes or could not be loaded: {showId}");
        }

        var uniqueEpisodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var downloadObjects = new List<DownloadObject>();

        foreach (var episodeToken in episodesData)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (episodeToken is not JObject episode)
            {
                continue;
            }

            var episodeId = episode.Value<string>("EPISODE_ID")
                           ?? episode.Value<string>("id");
            if (string.IsNullOrWhiteSpace(episodeId) || !uniqueEpisodeIds.Add(episodeId))
            {
                continue;
            }

            try
            {
                downloadObjects.Add(await GenerateEpisodeItemAsync(episodeId, bitrate, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to generate episode item {EpisodeId} for show {ShowId}", episodeId, showId);
            }
        }

        if (downloadObjects.Count == 0)
        {
            throw new InvalidOperationException($"No downloadable episodes found for show: {showId}");
        }

        _logger.LogInformation("Generated show download list for {ShowId}: {Count} episodes", showId, downloadObjects.Count);
        return downloadObjects;
    }

    private async Task<EpisodeMetadata?> GetEpisodeMetadataAsync(string episodeId, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("DeezSpoTagDownload");
            using var response = await client.GetAsync($"https://api.deezer.com/episode/{episodeId}", cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out _))
            {
                _logger.LogWarning("Episode metadata API returned error for {EpisodeId}", episodeId);
                return await GetEpisodeMetadataFromGatewayAsync(episodeId);
            }

            var metadata = BuildEpisodeMetadataFromApi(episodeId, root);
            if (RequiresEpisodeGatewayMetadata(metadata))
            {
                MergeEpisodeMetadata(metadata, await GetEpisodeMetadataFromGatewayAsync(episodeId));
            }

            return metadata;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch episode metadata for {EpisodeId}", episodeId);
            return await GetEpisodeMetadataFromGatewayAsync(episodeId);
        }
    }

    private static EpisodeMetadata BuildEpisodeMetadataFromApi(string episodeId, JsonElement root)
    {
        var metadata = new EpisodeMetadata
        {
            EpisodeId = episodeId,
            Title = GetJsonString(root, "title") ?? "Unknown Episode",
            Duration = GetJsonInt(root, "duration"),
            EpisodeNumber = GetJsonInt(root, "episode_number", 1),
            ReleaseDate = GetJsonString(root, "release_date"),
            DirectUrl = GetJsonString(root, "direct_stream_url")
                        ?? GetJsonString(root, "direct_url")
                        ?? GetJsonString(root, "url"),
            ShowId = "0",
            ShowTitle = "Unknown Show",
            ShowPictureMd5 = null
        };

        if (!root.TryGetProperty("show", out var show))
        {
            return metadata;
        }

        metadata.ShowId = GetJsonString(show, "id") ?? metadata.ShowId;
        metadata.ShowTitle = GetJsonString(show, "title") ?? metadata.ShowTitle;
        var showPictureUrl = GetJsonString(show, "picture")
                             ?? GetJsonString(show, "picture_big")
                             ?? GetJsonString(show, "picture_xl");
        metadata.ShowPictureMd5 = ExtractImageMd5(showPictureUrl, "talk");
        return metadata;
    }

    private static bool RequiresEpisodeGatewayMetadata(EpisodeMetadata metadata)
    {
        return string.IsNullOrWhiteSpace(metadata.DirectUrl)
            || IsDeezerEpisodePage(metadata.DirectUrl ?? string.Empty);
    }

    private static void MergeEpisodeMetadata(EpisodeMetadata metadata, EpisodeMetadata? gatewayMetadata)
    {
        if (gatewayMetadata == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(gatewayMetadata.DirectUrl))
        {
            metadata.DirectUrl = gatewayMetadata.DirectUrl;
        }

        if (string.IsNullOrWhiteSpace(metadata.ShowId) || metadata.ShowId == "0")
        {
            metadata.ShowId = gatewayMetadata.ShowId;
        }

        if (string.IsNullOrWhiteSpace(metadata.ShowTitle)
            || string.Equals(metadata.ShowTitle, "Unknown Show", StringComparison.OrdinalIgnoreCase))
        {
            metadata.ShowTitle = gatewayMetadata.ShowTitle;
        }

        if (string.IsNullOrWhiteSpace(metadata.ShowPictureMd5))
        {
            metadata.ShowPictureMd5 = gatewayMetadata.ShowPictureMd5;
        }

        if (metadata.Duration <= 0)
        {
            metadata.Duration = gatewayMetadata.Duration;
        }

        if (metadata.EpisodeNumber <= 0)
        {
            metadata.EpisodeNumber = gatewayMetadata.EpisodeNumber;
        }

        if (string.IsNullOrWhiteSpace(metadata.ReleaseDate))
        {
            metadata.ReleaseDate = gatewayMetadata.ReleaseDate;
        }
    }

    private async Task<EpisodeMetadata?> GetEpisodeMetadataFromGatewayAsync(string episodeId)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var gatewayService = scope.ServiceProvider.GetRequiredService<DeezerGatewayService>();
            var page = await gatewayService.GetEpisodePageAsync(episodeId);
            var results = page[ResultsField] as JObject ?? page;

            var episode = results["EPISODE"] as JObject
                          ?? results["episode"] as JObject
                          ?? results;

            var title = GetJObjectString(episode, "EPISODE_TITLE", "title") ?? "Unknown Episode";
            var duration = GetJObjectInt(episode, "DURATION", "duration");
            var releaseDate = GetJObjectString(episode, "EPISODE_PUBLISHED_TIMESTAMP", "release_date");
            var episodeNumber = GetJObjectInt(episode, "EPISODE_NUMBER", "episode_number", 1);
            var directUrl = GetJObjectString(episode, "EPISODE_DIRECT_STREAM_URL", "direct_stream_url", "direct_url", "url");

            var showId = GetJObjectString(episode, "SHOW_ID", "show_id") ?? "0";
            var showTitle = GetJObjectString(episode, "SHOW_NAME", "show_title", "SHOW_TITLE") ?? "Unknown Show";
            var showPictureMd5 = GetJObjectString(episode, "SHOW_ART_MD5", "show_art_md5", "SHOW_PICTURE");

            if (string.IsNullOrWhiteSpace(directUrl) || IsDeezerEpisodePage(directUrl))
            {
                directUrl = await GetEpisodeStreamUrlAsync(showId, episodeId)
                            ?? await GetEpisodeStreamUrlFromGatewayAsync(episodeId);
            }

            return new EpisodeMetadata
            {
                EpisodeId = episodeId,
                Title = title,
                Duration = duration,
                EpisodeNumber = episodeNumber,
                ReleaseDate = releaseDate,
                DirectUrl = directUrl,
                ShowId = showId,
                ShowTitle = showTitle,
                ShowPictureMd5 = showPictureMd5
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch episode metadata via GW for {EpisodeId}", episodeId);
            return null;
        }
    }

    private async Task<string?> GetEpisodeStreamUrlAsync(string showId, string episodeId)
    {
        if (string.IsNullOrWhiteSpace(showId))
        {
            return null;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var gatewayService = scope.ServiceProvider.GetRequiredService<DeezerGatewayService>();
            var showPage = await gatewayService.GetShowPageAsync(showId);
            return DeezerEpisodeStreamResolver.ResolveStreamUrl(
                showPage,
                episodeId,
                includeLinkFallback: false,
                rejectDeezerEpisodePages: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve episode stream URL for {EpisodeId}", episodeId);
        }

        return null;
    }

    private async Task<string?> GetEpisodeStreamUrlFromGatewayAsync(string episodeId)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var gatewayService = scope.ServiceProvider.GetRequiredService<DeezerGatewayService>();
            var page = await gatewayService.GetEpisodePageAsync(episodeId);
            var results = page[ResultsField] as JObject ?? page;
            var episode = results["EPISODE"] as JObject
                          ?? results["episode"] as JObject
                          ?? results;

            var streamUrl = episode?.Value<string>("EPISODE_DIRECT_STREAM_URL")
                            ?? episode?.Value<string>("EPISODE_URL");

            return IsDeezerEpisodePage(streamUrl ?? string.Empty) ? null : streamUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve episode stream URL via GW for {EpisodeId}", episodeId);
            return null;
        }
    }

    private static bool IsDeezerEpisodePage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Contains("deezer.com", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.Contains("/episode", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (element.TryGetProperty(propertyName, out value) && value.ValueKind == JsonValueKind.Number)
        {
            return value.GetRawText();
        }

        return null;
    }

    private static int GetJsonInt(JsonElement element, string propertyName, int fallback = 0)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out intValue))
        {
            return intValue;
        }

        return fallback;
    }

    private static string? GetJObjectString(JObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var token = obj.GetValue(key, StringComparison.OrdinalIgnoreCase);
            if (token == null || token.Type == JTokenType.Null)
            {
                continue;
            }

            if (token.Type == JTokenType.String)
            {
                return token.Value<string>();
            }

            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                return token.ToString();
            }
        }

        return null;
    }

    private static int GetJObjectInt(JObject obj, string primaryKey, string fallbackKey, int fallback = 0)
    {
        var token = obj.GetValue(primaryKey, StringComparison.OrdinalIgnoreCase)
                    ?? obj.GetValue(fallbackKey, StringComparison.OrdinalIgnoreCase);

        if (token == null || token.Type == JTokenType.Null)
        {
            return fallback;
        }

        if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
        {
            return token.Value<int>();
        }

        if (token.Type == JTokenType.String && int.TryParse(token.Value<string>(), out var intValue))
        {
            return intValue;
        }

        return fallback;
    }

    private static string? ExtractImageMd5(string? imageUrl, string imageType)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        var marker = $"/images/{imageType}/";
        var start = imageUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = imageUrl.IndexOf('/', start);
        if (end <= start)
        {
            return null;
        }

        return imageUrl.Substring(start, end - start);
    }

    private sealed class EpisodeMetadata
    {
        public string EpisodeId { get; set; } = "";
        public string Title { get; set; } = "";
        public int Duration { get; set; }
        public int EpisodeNumber { get; set; } = 1;
        public string? ReleaseDate { get; set; }
        public string? DirectUrl { get; set; }
        public string ShowId { get; set; } = "";
        public string ShowTitle { get; set; } = "";
        public string? ShowPictureMd5 { get; set; }
    }

    /// <summary>
    /// Generate album download object (port of generateAlbumItem)
    /// CRITICAL FIX: Now properly populates metadata like deezspotag parseData method
    /// </summary>
    public async Task<CollectionDownloadObject> GenerateAlbumItemAsync(
        string albumId,
        int bitrate,
        DeezSpoTag.Core.Models.Artist? rootArtist = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Generating album item for ID: {AlbumId}", albumId);

        var deezerClient = await _authenticatedDeezerService.GetAuthenticatedClientAsync();
        if (deezerClient == null)
        {
            throw new InvalidOperationException(DeezerClientNotAuthenticatedMessage);
        }

        var albumData = await deezerClient.GetAlbumAsync(albumId);
        if (albumData == null)
        {
            throw new InvalidOperationException($"Album not found: {albumId}");
        }

        using var settingsScope = _serviceProvider.CreateScope();
        var settingsService = settingsScope.ServiceProvider.GetRequiredService<DeezSpoTagSettingsService>();
        var currentSettings = settingsService.LoadSettings();
        var usePrimaryArtistFolders = currentSettings.Tags?.SingleAlbumArtist != false;

        DeezSpoTag.Core.Models.Album album = MapApiAlbumToAlbum(albumData);
        if (usePrimaryArtistFolders)
        {
            NormalizeAlbumArtist(album, albumData);
        }
        if (rootArtist != null)
        {
            album.RootArtist = rootArtist;
        }
        var tracks = await BuildAlbumTracksAsync(
            deezerClient,
            albumId,
            album,
            currentSettings,
            usePrimaryArtistFolders,
            cancellationToken);

        var downloadObject = new CollectionDownloadObject
        {
            Title = album.Title,
            Type = AlbumType,
            Bitrate = bitrate,
            Album = album,
            Tracks = tracks
        };

        downloadObject.UpdateSize();
        downloadObject.Uuid = $"album_{albumId}_{bitrate}";

        _logger.LogInformation("Generated album download object with full metadata: {Title} with {TrackCount} tracks", 
            downloadObject.Title, tracks.Count);
        return downloadObject;
    }

    /// <summary>
    /// Generate playlist download object (port of generateDeezSpoTag.Integrations.Deezer.DeezSpoTag.Integrations.Deezer.DeezSpoTag.Integrations.Deezer.DeezSpoTag.Integrations.Deezer.PlaylistItem)
    /// CRITICAL FIX: Now properly populates metadata like deezspotag parseData method
    /// </summary>
    public async Task<CollectionDownloadObject> GeneratePlaylistItemAsync(
        string playlistId,
        int bitrate,
        ApiPlaylist? playlistData = null,
        List<DeezSpoTag.Core.Models.Deezer.GwTrack>? playlistTracks = null,
        string? overridePlaylistId = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Generating playlist item for ID: {PlaylistId}", playlistId);

        var deezerClient = await _authenticatedDeezerService.GetAuthenticatedClientAsync();
        if (deezerClient == null)
        {
            throw new InvalidOperationException(DeezerClientNotAuthenticatedMessage);
        }

        playlistData = await ResolvePlaylistDataAsync(deezerClient, playlistId, playlistData);
        ValidatePlaylistOwnership(deezerClient, playlistData, playlistId);

        DeezSpoTag.Core.Models.Playlist playlist = MapApiPlaylistToPlaylist(playlistData, overridePlaylistId);
        using var settingsScope = _serviceProvider.CreateScope();
        var settingsService = settingsScope.ServiceProvider.GetRequiredService<DeezSpoTagSettingsService>();
        var currentSettings = settingsService.LoadSettings();
        var usePrimaryArtistFolders = currentSettings.Tags?.SingleAlbumArtist != false;
        var tracks = await BuildPlaylistTracksAsync(
            deezerClient,
            playlistId,
            playlist,
            playlistTracks,
            currentSettings,
            usePrimaryArtistFolders,
            cancellationToken);

        var downloadObject = new CollectionDownloadObject
        {
            Title = playlist.Title,
            Type = PlaylistType,
            Bitrate = bitrate,
            Playlist = playlist,
            Tracks = tracks
        };

        downloadObject.UpdateSize();
        downloadObject.Uuid = $"playlist_{playlistId}_{bitrate}";

        _logger.LogInformation("Generated playlist download object with full metadata: {Title} with {TrackCount} tracks", 
            downloadObject.Title, tracks.Count);
        return downloadObject;
    }

    private async Task<List<DeezSpoTag.Core.Models.Track>> BuildAlbumTracksAsync(
        DeezerClient deezerClient,
        string albumId,
        DeezSpoTag.Core.Models.Album album,
        DeezSpoTagSettings currentSettings,
        bool usePrimaryArtistFolders,
        CancellationToken cancellationToken)
    {
        var tracks = new List<DeezSpoTag.Core.Models.Track>();
        var albumTracks = await deezerClient.Gw.GetAlbumTracksAsync(albumId);
        if (albumTracks == null)
        {
            return tracks;
        }

        foreach (var trackData in albumTracks)
        {
            var track = await BuildAlbumTrackAsync(
                deezerClient,
                albumId,
                trackData,
                album,
                currentSettings,
                usePrimaryArtistFolders,
                cancellationToken);
            tracks.Add(track);
        }

        return tracks;
    }

    private async Task<DeezSpoTag.Core.Models.Track> BuildAlbumTrackAsync(
        DeezerClient deezerClient,
        string albumId,
        DeezSpoTag.Core.Models.Deezer.GwTrack trackData,
        DeezSpoTag.Core.Models.Album album,
        DeezSpoTagSettings currentSettings,
        bool usePrimaryArtistFolders,
        CancellationToken cancellationToken)
    {
        var track = MapGwTrackToTrack(trackData);
        ParseTrackData(track, trackData);

        var publicTrackData = await TryGetPublicTrackDataAsync(deezerClient, track.Id, AlbumType, albumId);
        if (publicTrackData != null)
        {
            ApplyTrackMetadataFromPublicApi(track, publicTrackData, includeExtendedFields: true);
        }

        await TryPopulateLyricsAsync(deezerClient, track, AlbumType, albumId, cancellationToken);

        track.Album = album;
        if (usePrimaryArtistFolders)
        {
            NormalizeTrackArtists(track, publicTrackData);
        }

        track.ApplySettings(currentSettings);
        return track;
    }

    private async Task<ApiPlaylist> ResolvePlaylistDataAsync(
        DeezerClient deezerClient,
        string playlistId,
        ApiPlaylist? playlistData)
    {
        if (playlistData != null)
        {
            return playlistData;
        }

        playlistData = await TryGetPlaylistFromApiAsync(deezerClient, playlistId);
        if (playlistData != null)
        {
            return playlistData;
        }

        playlistData = await TryGetPlaylistFromGatewayAsync(deezerClient, playlistId);
        if (playlistData != null)
        {
            return playlistData;
        }

        throw new InvalidOperationException($"Playlist not found: {playlistId}");
    }

    private async Task<ApiPlaylist?> TryGetPlaylistFromApiAsync(DeezerClient deezerClient, string playlistId)
    {
        try
        {
            return await deezerClient.GetPlaylistAsync(playlistId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load playlist via API for {PlaylistId}", playlistId);
            return null;
        }
    }

    private async Task<ApiPlaylist?> TryGetPlaylistFromGatewayAsync(DeezerClient deezerClient, string playlistId)
    {
        try
        {
            var playlistPage = await deezerClient.Gw.GetPlaylistPageAsync(playlistId);
            return MapGwPlaylistToApiPlaylist(playlistPage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load playlist via GW for {PlaylistId}", playlistId);
            throw new GenerationException($"https://deezer.com/playlist/{playlistId}", ex.Message);
        }
    }

    private static void ValidatePlaylistOwnership(DeezerClient deezerClient, ApiPlaylist playlistData, string playlistId)
    {
        if (playlistData.Public != false || playlistData.Creator == null || deezerClient.CurrentUser == null)
        {
            return;
        }

        var currentUserId = deezerClient.CurrentUser.Id;
        if (!string.IsNullOrEmpty(currentUserId) && playlistData.Creator.Id.ToString() != currentUserId)
        {
            throw new NotYourPrivatePlaylistException($"https://deezer.com/playlist/{playlistId}");
        }
    }

    private async Task<List<DeezSpoTag.Core.Models.Track>> BuildPlaylistTracksAsync(
        DeezerClient deezerClient,
        string playlistId,
        DeezSpoTag.Core.Models.Playlist playlist,
        List<DeezSpoTag.Core.Models.Deezer.GwTrack>? playlistTracks,
        DeezSpoTagSettings currentSettings,
        bool usePrimaryArtistFolders,
        CancellationToken cancellationToken)
    {
        if (playlistTracks == null)
        {
            playlistTracks = await deezerClient.Gw.GetPlaylistTracksAsync(playlistId);
        }

        var tracks = new List<DeezSpoTag.Core.Models.Track>();
        if (playlistTracks == null)
        {
            return tracks;
        }

        foreach (var trackData in playlistTracks)
        {
            var track = await BuildPlaylistTrackAsync(
                deezerClient,
                playlistId,
                playlist,
                trackData,
                currentSettings,
                usePrimaryArtistFolders,
                cancellationToken);
            tracks.Add(track);
        }

        return tracks;
    }

    private async Task<DeezSpoTag.Core.Models.Track> BuildPlaylistTrackAsync(
        DeezerClient deezerClient,
        string playlistId,
        DeezSpoTag.Core.Models.Playlist playlist,
        DeezSpoTag.Core.Models.Deezer.GwTrack trackData,
        DeezSpoTagSettings currentSettings,
        bool usePrimaryArtistFolders,
        CancellationToken cancellationToken)
    {
        var track = MapGwTrackToTrack(trackData);
        ParseTrackData(track, trackData);

        var publicTrackData = await TryGetPublicTrackDataAsync(deezerClient, track.Id, PlaylistType, playlistId);
        if (publicTrackData != null)
        {
            ApplyTrackMetadataFromPublicApi(track, publicTrackData, includeExtendedFields: false);
        }

        await ApplyPlaylistAlbumMetadataAsync(
            deezerClient,
            playlistId,
            trackData,
            track,
            usePrimaryArtistFolders);
        await TryPopulateLyricsAsync(deezerClient, track, PlaylistType, playlistId, cancellationToken);

        track.Playlist = playlist;
        if (track.Position == null || track.Position <= 0)
        {
            track.Position = trackData.Position + 1;
        }

        if (usePrimaryArtistFolders)
        {
            NormalizeTrackArtists(track, publicTrackData);
        }

        track.ApplySettings(currentSettings);
        return track;
    }

    private static void ApplyTrackMetadataFromPublicApi(
        DeezSpoTag.Core.Models.Track track,
        ApiTrack publicTrackData,
        bool includeExtendedFields)
    {
        track.ParseTrack(publicTrackData);
        if (!includeExtendedFields)
        {
            return;
        }

        track.Bpm = publicTrackData.Bpm;
        track.Copyright = publicTrackData.Copyright ?? track.Copyright;
        track.Rank = publicTrackData.Rank;
        track.Gain = publicTrackData.Gain;
        if (string.IsNullOrWhiteSpace(track.LyricsId))
        {
            track.LyricsId = publicTrackData.LyricsId;
        }
    }

    private async Task<ApiTrack?> TryGetPublicTrackDataAsync(
        DeezerClient deezerClient,
        string trackId,
        string parentType,
        string parentId)
    {
        try
        {
            return await deezerClient.GetTrackAsync(trackId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not get public API data for track {TrackId} in {ParentType} {ParentId}", trackId, parentType, parentId);
            return null;
        }
    }

    private async Task ApplyPlaylistAlbumMetadataAsync(
        DeezerClient deezerClient,
        string playlistId,
        DeezSpoTag.Core.Models.Deezer.GwTrack trackData,
        DeezSpoTag.Core.Models.Track track,
        bool usePrimaryArtistFolders)
    {
        if (string.IsNullOrEmpty(trackData.AlbId) || trackData.AlbId == "0")
        {
            return;
        }

        var playlistAlbumData = await TryGetPlaylistAlbumDataAsync(deezerClient, trackData.AlbId, track.Id, playlistId);
        if (playlistAlbumData == null)
        {
            return;
        }

        var album = MapApiAlbumToAlbum(playlistAlbumData);
        if (usePrimaryArtistFolders)
        {
            NormalizeAlbumArtist(album, playlistAlbumData);
        }

        track.Album = album;
        if (track.Date.Year == "XXXX" && album.Date.Year != "XXXX")
        {
            track.Date = album.Date;
        }
    }

    private async Task<ApiAlbum?> TryGetPlaylistAlbumDataAsync(
        DeezerClient deezerClient,
        string albumId,
        string trackId,
        string playlistId)
    {
        try
        {
            return await deezerClient.GetAlbumAsync(albumId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not get album data for track {TrackId} in playlist {PlaylistId}", trackId, playlistId);
            return null;
        }
    }

    private async Task TryPopulateLyricsAsync(
        DeezerClient deezerClient,
        DeezSpoTag.Core.Models.Track track,
        string parentType,
        string parentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(track.LyricsId) || track.LyricsId == "0")
        {
            return;
        }

        try
        {
            var lyricsData = await deezerClient.Gw.GetLyricsAsync(track.Id, cancellationToken: cancellationToken);
            if (lyricsData != null)
            {
                track.Lyrics = new DeezSpoTag.Core.Models.Lyrics
                {
                    Id = track.LyricsId,
                    Sync = lyricsData.GetValueOrDefault("LYRICS_SYNC_JSON", "")?.ToString() ?? "",
                    Unsync = lyricsData.GetValueOrDefault("LYRICS_TEXT", "")?.ToString() ?? ""
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not get lyrics for track {TrackId} in {ParentType} {ParentId}", track.Id, parentType, parentId);
            track.LyricsId = string.Empty;
        }
    }

    /// <summary>
    /// Generate artist download object (port of generateArtistItem)
    /// </summary>
    public async Task<List<DownloadObject>> GenerateArtistItemAsync(
        string artistId,
        int bitrate,
        string tab = "all",
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Generating artist item for ID: {ArtistId}, tab: {Tab}", artistId, tab);

        var deezerClient = await _authenticatedDeezerService.GetAuthenticatedClientAsync();
        if (deezerClient == null)
        {
            throw new InvalidOperationException(DeezerClientNotAuthenticatedMessage);
        }

        var artistData = await deezerClient.GetArtistAsync(artistId);
        if (artistData == null)
        {
            throw new InvalidOperationException($"Artist not found: {artistId}");
        }

        var rootArtist = MapApiArtistToArtist(artistData);
        var discographyTabs = await deezerClient.Gw.GetArtistDiscographyTabsAsync(artistId, 100);
        var albumIds = ResolveArtistAlbumIds(discographyTabs, tab);
        var albumList = await GenerateArtistAlbumObjectsAsync(
            albumIds,
            bitrate,
            rootArtist,
            artistId,
            cancellationToken);

        _logger.LogDebug("Generated artist download list for {ArtistId}: {Count} items", artistId, albumList.Count);
        return albumList;
    }

    private static List<string> ResolveArtistAlbumIds(
        Dictionary<string, List<object>> discographyTabs,
        string tab)
    {
        IEnumerable<object> releaseEntries = Enumerable.Empty<object>();
        if (tab == "discography")
        {
            releaseEntries = discographyTabs
                .Where(static entry => entry.Key != "all")
                .SelectMany(static entry => entry.Value);
        }
        else if (discographyTabs.TryGetValue(tab, out var releases))
        {
            releaseEntries = releases;
        }

        var albumIds = new List<string>();
        foreach (var albumEntry in releaseEntries)
        {
            var albumId = ExtractAlbumId(albumEntry);
            if (!string.IsNullOrEmpty(albumId))
            {
                albumIds.Add(albumId);
            }
        }

        return albumIds;
    }

    private async Task<List<DownloadObject>> GenerateArtistAlbumObjectsAsync(
        IReadOnlyList<string> albumIds,
        int bitrate,
        DeezSpoTag.Core.Models.Artist rootArtist,
        string artistId,
        CancellationToken cancellationToken)
    {
        var albumList = new List<DownloadObject>();
        foreach (var albumId in albumIds)
        {
            try
            {
                var albumObject = await GenerateAlbumItemAsync(albumId, bitrate, rootArtist, cancellationToken);
                albumList.Add(albumObject);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to generate album {AlbumId} for artist {ArtistId}", albumId, artistId);
            }
        }

        return albumList;
    }

    /// <summary>
    /// Generate artist top tracks download object (port of generateArtistTopItem)
    /// </summary>
    public async Task<CollectionDownloadObject> GenerateArtistTopItemAsync(
        string artistId,
        int bitrate,
        CancellationToken cancellationToken = default)
    {
        var deezerClient = await _authenticatedDeezerService.GetAuthenticatedClientAsync();
        if (deezerClient == null)
        {
            throw new InvalidOperationException(DeezerClientNotAuthenticatedMessage);
        }

        var artistData = await deezerClient.GetArtistAsync(artistId);
        if (artistData == null)
        {
            throw new InvalidOperationException($"Artist not found: {artistId}");
        }

        var playlistApi = new ApiPlaylist
        {
            Id = artistData.Id,
            Title = $"{artistData.Name} - Top Tracks",
            Description = $"Top Tracks for {artistData.Name}",
            Duration = 0,
            Public = true,
            IsLovedTrack = false,
            Collaborative = false,
            NbTracks = 0,
            Fans = artistData.NbFan,
            Link = $"https://www.deezer.com/artist/{artistData.Id}/top_track",
            Picture = artistData.Picture,
            PictureSmall = artistData.PictureSmall,
            PictureMedium = artistData.PictureMedium,
            PictureBig = artistData.PictureBig,
            PictureXl = artistData.PictureXl,
            Tracklist = $"https://api.deezer.com/artist/{artistData.Id}/top",
            CreationDate = "XXXX-00-00",
            Creator = new ApiArtist { Id = artistData.Id, Name = artistData.Name, Type = "user" },
            Type = PlaylistType
        };

        var topTracks = await deezerClient.Gw.GetArtistTopTracksAsync(artistId, 100);
        var topId = $"{artistData.Id}_top_track";
        return await GeneratePlaylistItemAsync(topId, bitrate, playlistApi, topTracks, topId, cancellationToken);
    }

    /// <summary>
    /// Map GW track to internal track model (includes download metadata) - following deezspotag mapGwTrackToDeezer
    /// CRITICAL FIX: Now properly sets up Artist dictionary like deezspotag parseTrack
    /// </summary>
    private DeezSpoTag.Core.Models.Track MapGwTrackToTrack(DeezSpoTag.Core.Models.Deezer.GwTrack gwTrack)
    {
        var artistName = string.IsNullOrWhiteSpace(gwTrack.ArtName) ? UnknownArtist : gwTrack.ArtName.Trim();
        var mainArtist = BuildMainArtistFromGwTrack(gwTrack, artistName);
        var album = BuildAlbumFromGwTrack(gwTrack, mainArtist, artistName);
        var track = BuildTrackFromGwTrack(gwTrack, mainArtist, album, artistName);

        _logger.LogInformation("Mapped GW track {TrackId} '{Title}' by '{Artist}': MD5='{MD5}', MediaVersion='{MediaVersion}', FileSizes={FileSizeCount}, TrackToken='{TrackToken}'", 
            track.Id, track.Title, track.MainArtist?.Name ?? UnknownValue, track.MD5, track.MediaVersion, track.FileSizes.Count, track.TrackToken);
        
        // CRITICAL: Log if track is not encoded (MD5 is 0) - EXACT PORT from deezspotag
        if (string.IsNullOrWhiteSpace(gwTrack.Md5Origin))
        {
            _logger.LogWarning("Track {TrackId} '{Title}' by '{Artist}' is not yet encoded by Deezer (MD5 is {MD5}). This is common for very new releases.", 
                track.Id, track.Title, track.MainArtist?.Name ?? UnknownValue, gwTrack.Md5Origin);
        }

        return track;
    }

    private DeezSpoTag.Core.Models.Artist BuildMainArtistFromGwTrack(
        DeezSpoTag.Core.Models.Deezer.GwTrack gwTrack,
        string artistName)
    {
        var mainArtist = new DeezSpoTag.Core.Models.Artist(gwTrack.ArtId, artistName);
        var artistPicMd5 = string.IsNullOrWhiteSpace(gwTrack.ArtPicture) ? string.Empty : gwTrack.ArtPicture.Trim();
        mainArtist.Pic = new Picture(artistPicMd5, ArtistPictureType);
        _logger.LogDebug("Set main artist for track {TrackId}: '{ArtistName}' (ID: {ArtistId})", gwTrack.SngId, artistName, gwTrack.ArtId);
        return mainArtist;
    }

    private static DeezSpoTag.Core.Models.Album BuildAlbumFromGwTrack(
        DeezSpoTag.Core.Models.Deezer.GwTrack gwTrack,
        DeezSpoTag.Core.Models.Artist mainArtist,
        string artistName)
    {
        var albumTitle = string.IsNullOrWhiteSpace(gwTrack.AlbTitle) ? "Unknown Album" : gwTrack.AlbTitle.Trim();
        var albumId = string.IsNullOrWhiteSpace(gwTrack.AlbId) ? "0" : gwTrack.AlbId.Trim();
        var albumPicMd5 = string.IsNullOrWhiteSpace(gwTrack.AlbPicture) ? string.Empty : gwTrack.AlbPicture.Trim();

        var album = new DeezSpoTag.Core.Models.Album(albumId, albumTitle, albumPicMd5)
        {
            MainArtist = mainArtist,
            RootArtist = mainArtist,
            Pic = new Picture(albumPicMd5, CoverPictureType)
        };
        album.Artist["Main"] = new List<string> { artistName };
        album.Artists = new List<string> { artistName };
        return album;
    }

    private static DeezSpoTag.Core.Models.Track BuildTrackFromGwTrack(
        DeezSpoTag.Core.Models.Deezer.GwTrack gwTrack,
        DeezSpoTag.Core.Models.Artist mainArtist,
        DeezSpoTag.Core.Models.Album album,
        string artistName)
    {
        var track = new DeezSpoTag.Core.Models.Track
        {
            Id = gwTrack.SngId.ToString(),
            Title = gwTrack.SngTitle ?? string.Empty,
            Duration = gwTrack.Duration,
            TrackNumber = gwTrack.TrackNumber,
            DiscNumber = gwTrack.DiskNumber,
            Explicit = gwTrack.ExplicitLyrics,
            ISRC = gwTrack.Isrc ?? string.Empty,
            MD5 = gwTrack.Md5Origin,
            MediaVersion = gwTrack.MediaVersion.ToString(CultureInfo.InvariantCulture),
            TrackToken = gwTrack.TrackToken ?? string.Empty,
            LyricsId = int.TryParse(gwTrack.LyricsId, out var lyricsId) ? lyricsId.ToString(CultureInfo.InvariantCulture) : string.Empty,
            ReplayGain = gwTrack.Gain.ToString("F2", CultureInfo.InvariantCulture),
            MainArtist = mainArtist,
            Album = album,
            FallbackID = gwTrack.FallbackId ?? 0
        };

        if (gwTrack.Position >= 0)
        {
            track.Position = gwTrack.Position + 1;
        }

        track.FallbackId = track.FallbackID;
        track.Artist["Main"] = new List<string> { artistName };
        track.Artists = new List<string> { artistName };
        track.GenerateMainFeatStrings();
        track.FileSizes = BuildTrackFileSizes(gwTrack);
        return track;
    }

    private static Dictionary<string, int> BuildTrackFileSizes(DeezSpoTag.Core.Models.Deezer.GwTrack gwTrack)
    {
        var fileSizes = new Dictionary<string, int>();
        AddFileSizeIfPresent(fileSizes, "mp3_128", gwTrack.FilesizeMp3128);
        AddFileSizeIfPresent(fileSizes, "mp3_320", gwTrack.FilesizeMp3320);
        AddFileSizeIfPresent(fileSizes, "flac", gwTrack.FilesizeFlac);
        AddFileSizeIfPresent(fileSizes, "mp4_ra1", gwTrack.FilesizeMp4Ra1);
        AddFileSizeIfPresent(fileSizes, "mp4_ra2", gwTrack.FilesizeMp4Ra2);
        AddFileSizeIfPresent(fileSizes, "mp4_ra3", gwTrack.FilesizeMp4Ra3);
        return fileSizes;
    }

    private static void AddFileSizeIfPresent(Dictionary<string, int> fileSizes, string key, int? value)
    {
        if (value.HasValue && value.Value > 0)
        {
            fileSizes[key] = value.Value;
        }
    }

    /// <summary>
    /// Parse track data like deezspotag parseTrack method - CRITICAL for settings to work
    /// </summary>
    private void ParseTrackData(DeezSpoTag.Core.Models.Track track, DeezSpoTag.Core.Models.Deezer.GwTrack gwTrack)
    {
        // CRITICAL: Set up date information like deezspotag parseTrack
        if (!string.IsNullOrEmpty(gwTrack.PhysicalReleaseDate)
            && DateTime.TryParse(gwTrack.PhysicalReleaseDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseDate))
        {
            track.Date.Day = releaseDate.Day.ToString("D2");
            track.Date.Month = releaseDate.Month.ToString("D2");
            track.Date.Year = releaseDate.Year.ToString();
            track.Date.FixDayMonth();
        }

        // CRITICAL: Set up contributors like deezspotag parseTrack
        // For now, we only have main artist from GW data, but this structure is essential
        track.Contributors = new Dictionary<string, object>();
        
        // CRITICAL: Ensure Artist dictionary is properly set up (already done in MapGwTrackToTrack but reinforcing)
        if (track.Artist["Main"].Count == 0)
        {
            track.Artist["Main"] = new List<string> { track.MainArtist?.Name ?? UnknownArtist };
        }

        if (track.Artists.Count == 0)
        {
            track.Artists = new List<string> { track.MainArtist?.Name ?? UnknownArtist };
        }

        // CRITICAL: Generate artist strings (already done but ensuring it's called)
        track.GenerateMainFeatStrings();

        _logger.LogDebug("Parsed track data for {TrackId}: Artists={Artists}, Date={Date}", 
            track.Id, string.Join(", ", track.Artists), track.Date.Year);
    }

    /// <summary>
    /// Map API album to internal album model
    /// CRITICAL FIX: Now properly sets up Artist dictionary like deezspotag
    /// </summary>
    private DeezSpoTag.Core.Models.Album MapApiAlbumToAlbum(ApiAlbum apiAlbum)
    {
        var albumTitle = string.IsNullOrWhiteSpace(apiAlbum.Title) ? "Unknown Album" : apiAlbum.Title.Trim();
        DeezSpoTag.Core.Models.Album album = new DeezSpoTag.Core.Models.Album(apiAlbum.Id.ToString(), albumTitle);
        
        // Set properties that exist in Core Album model
        album.Barcode = apiAlbum.Upc ?? "";
        album.Explicit = apiAlbum.ExplicitLyrics ?? false;
        album.Label = apiAlbum.Label ?? "";
        album.TrackTotal = apiAlbum.NbTracks ?? 0;
        album.DiscTotal = apiAlbum.NbDisk ?? 1;
        album.RecordType = apiAlbum.RecordType ?? "";
        album.MainArtist = apiAlbum.Artist != null ? MapApiArtistToArtist(apiAlbum.Artist) : new DeezSpoTag.Core.Models.Artist();
        album.Copyright = apiAlbum.Copyright ?? "";

        // CRITICAL FIX: Set up Artist dictionary like deezspotag parseAlbum method
        var artistName = album.MainArtist?.Name ?? UnknownArtist;
        album.Artist["Main"] = new List<string> { artistName };
        album.Artists = new List<string> { artistName };

        // CRITICAL FIX: Parse release date like deezspotag
        if (!string.IsNullOrEmpty(apiAlbum.ReleaseDate)
            && DateTime.TryParse(apiAlbum.ReleaseDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseDate))
        {
            album.Date.Day = releaseDate.Day.ToString("D2");
            album.Date.Month = releaseDate.Month.ToString("D2");
            album.Date.Year = releaseDate.Year.ToString();
            album.Date.FixDayMonth();
        }

        // CRITICAL FIX: Set up album cover
        if (!string.IsNullOrEmpty(apiAlbum.Md5Image))
        {
            album.Pic = new Picture(apiAlbum.Md5Image, CoverPictureType);
            album.Md5Image = apiAlbum.Md5Image;
        }
        else
        {
            var fallbackMd5 = ExtractAlbumPictureMd5(apiAlbum);
            if (!string.IsNullOrEmpty(fallbackMd5))
            {
                album.Pic = new Picture(fallbackMd5, CoverPictureType);
                album.Md5Image = fallbackMd5;
            }
            else
            {
                album.Pic = new Picture(string.Empty, CoverPictureType);
            }
        }

        _logger.LogDebug("Mapped album {AlbumId}: Title={Title}, ArtistPic={ArtistPic}", 
            album.Id, album.Title, album.MainArtist?.Pic?.Md5 ?? "NULL");

        return album;
    }

    /// <summary>
    /// Map API playlist to internal playlist model
    /// </summary>
    private DeezSpoTag.Core.Models.Playlist MapApiPlaylistToPlaylist(ApiPlaylist apiPlaylist, string? overrideId)
    {
        var playlistId = !string.IsNullOrEmpty(overrideId) ? overrideId : apiPlaylist.Id.ToString();
        DeezSpoTag.Core.Models.Playlist playlist = new DeezSpoTag.Core.Models.Playlist(playlistId, apiPlaylist.Title ?? "");
        
        // Set properties that exist in Core Playlist model
        playlist.Description = apiPlaylist.Description ?? "";
        playlist.TrackTotal = apiPlaylist.NbTracks ?? 0;
        playlist.IsPublic = apiPlaylist.Public ?? false;
        playlist.Owner = apiPlaylist.Creator != null ? MapApiArtistToArtist(apiPlaylist.Creator) : new DeezSpoTag.Core.Models.Artist();
        playlist.Duration = apiPlaylist.Duration ?? 0;
        playlist.IsCollaborative = apiPlaylist.Collaborative ?? false;
        playlist.Checksum = apiPlaylist.Checksum ?? "";

        if (!string.IsNullOrEmpty(apiPlaylist.CreationDate))
        {
            playlist.Date = CustomDate.FromString(apiPlaylist.CreationDate);
            playlist.DateString = playlist.Date.Format("ymd");
        }

        var (playlistPicMd5, playlistPicType) = ExtractPlaylistPictureMd5(apiPlaylist);
        if (!string.IsNullOrEmpty(playlistPicMd5))
        {
            playlist.Pic = new Picture(playlistPicMd5, playlistPicType);
        }

        return playlist;
    }

    private static (string? md5, string type) ExtractPlaylistPictureMd5(ApiPlaylist apiPlaylist)
    {
        var pictureUrl = apiPlaylist.PictureSmall ?? apiPlaylist.PictureMedium ?? apiPlaylist.PictureBig ?? apiPlaylist.PictureXl;
        if (string.IsNullOrEmpty(pictureUrl))
        {
            return (null, PlaylistType);
        }

        try
        {
            var parts = pictureUrl.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if ((parts[i] == PlaylistType || parts[i] == CoverPictureType || parts[i] == ArtistPictureType)
                    && i + 1 < parts.Length)
                {
                    return (parts[i + 1], parts[i]);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return (null, PlaylistType);
        }

        return (null, PlaylistType);
    }

    private static string? ExtractAlbumPictureMd5(ApiAlbum apiAlbum)
    {
        var pictureUrl = new[] { apiAlbum.CoverXl, apiAlbum.CoverBig, apiAlbum.CoverMedium, apiAlbum.CoverSmall, apiAlbum.Cover }
            .FirstOrDefault(url => !string.IsNullOrEmpty(url));
        if (string.IsNullOrEmpty(pictureUrl))
        {
            return null;
        }

        try
        {
            var parts = pictureUrl.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if ((parts[i] == CoverPictureType || parts[i] == ArtistPictureType || parts[i] == PlaylistType)
                    && i + 1 < parts.Length)
                {
                    return parts[i + 1];
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return null;
        }

        return null;
    }

    private static string? ExtractAlbumId(object? albumEntry)
    {
        if (albumEntry is Dictionary<string, object> dict && dict.TryGetValue("id", out var idValue))
        {
            return idValue?.ToString();
        }
        else if (albumEntry is JsonElement element
            && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("id", out var idElement))
        {
            return idElement.ToString();
        }

        return null;
    }

    private static ApiPlaylist MapGwPlaylistToApiPlaylist(GwPlaylistPageResponse playlistPage)
    {
        var playlistData = playlistPage.Data;
        var ownerId = !string.IsNullOrWhiteSpace(playlistData.OwnerId) ? playlistData.OwnerId : playlistData.UserId;
        var ownerNumericId = long.TryParse(ownerId, out var parsedOwnerId) ? parsedOwnerId : 0L;
        var ownerName = !string.IsNullOrWhiteSpace(playlistData.OwnerName) ? playlistData.OwnerName : playlistData.UserName;
        var ownerPicture = !string.IsNullOrWhiteSpace(playlistData.OwnerPicture) ? playlistData.OwnerPicture : playlistData.UserPicture;
        var creator = string.IsNullOrWhiteSpace(ownerId) && string.IsNullOrWhiteSpace(ownerName)
            ? null
            : new ApiArtist
            {
                Id = ownerNumericId,
                Name = ownerName ?? "",
                Md5Image = ownerPicture ?? "",
                PictureSmall = BuildOwnerPictureUrl(ownerPicture, "125x125-000000-80-0-0.jpg"),
                PictureMedium = BuildOwnerPictureUrl(ownerPicture, "250x250-000000-80-0-0.jpg")
            };
        var playlist = new ApiPlaylist
        {
            Id = long.TryParse(playlistData.PlaylistId, out var id) ? id : 0,
            Title = playlistData.Title,
            Description = playlistData.Description,
            Picture = playlistData.PlaylistPicture,
            PictureSmall = !string.IsNullOrEmpty(playlistData.PlaylistPicture)
                ? $"https://e-cdns-images.dzcdn.net/images/playlist/{playlistData.PlaylistPicture}/56x56-000000-80-0-0.jpg"
                : null,
            PictureMedium = !string.IsNullOrEmpty(playlistData.PlaylistPicture)
                ? $"https://e-cdns-images.dzcdn.net/images/playlist/{playlistData.PlaylistPicture}/250x250-000000-80-0-0.jpg"
                : null,
            PictureBig = !string.IsNullOrEmpty(playlistData.PlaylistPicture)
                ? $"https://e-cdns-images.dzcdn.net/images/playlist/{playlistData.PlaylistPicture}/500x500-000000-80-0-0.jpg"
                : null,
            PictureXl = !string.IsNullOrEmpty(playlistData.PlaylistPicture)
                ? $"https://e-cdns-images.dzcdn.net/images/playlist/{playlistData.PlaylistPicture}/1000x1000-000000-80-0-0.jpg"
                : null,
            NbTracks = playlistData.NbSong,
            Duration = playlistData.Duration,
            Public = playlistData.IsPublic || playlistData.Status == 1,
            Collaborative = playlistData.Collaborative || playlistData.Status == 2,
            Checksum = playlistData.Checksum,
            CreationDate = playlistData.CreationDate,
            IsLovedTrack = playlistData.IsLovedTrack,
            Fans = playlistData.Fans,
            Creator = creator
        };

        return playlist;
    }

    /// <summary>
    /// Map API artist to internal artist model (EXACT PORT from deezspotag Album.parseAlbum)
    /// </summary>
    private DeezSpoTag.Core.Models.Artist MapApiArtistToArtist(ApiArtist apiArtist)
    {
        DeezSpoTag.Core.Models.Artist artist = new DeezSpoTag.Core.Models.Artist(apiArtist.Id, apiArtist.Name ?? "");
        
        // EXACT PORT from deezspotag Album.parseAlbum lines 67-75:
        // Getting artist image ID
        // ex: https://e-cdns-images.dzcdn.net/images/artist/f2bc007e9133c946ac3c3907ddc5d2ea/56x56-000000-80-0-0.jpg
        string artPic = "";
        
        // First try md5_image if available
        if (!string.IsNullOrEmpty(apiArtist.Md5Image))
        {
            artPic = apiArtist.Md5Image;
            _logger.LogDebug("Using md5_image for artist {ArtistName}: {Md5Image}", artist.Name, artPic);
        }
        // Otherwise extract from picture_small URL like deezspotag does
        else if (!string.IsNullOrEmpty(apiArtist.PictureSmall))
        {
            var pictureSmall = apiArtist.PictureSmall;
            _logger.LogDebug("Extracting artist picture from URL for {ArtistName}: {PictureSmallUrl}", artist.Name, pictureSmall);
            
            var artistIndex = pictureSmall.IndexOf("artist/");
            if (artistIndex >= 0)
            {
                // Match the upstream slice behavior by removing the trailing size suffix.
                var startIndex = artistIndex + 7; // "artist/".Length
                var endIndex = pictureSmall.Length - 24; // Remove last 24 characters
                if (startIndex < endIndex && endIndex > startIndex)
                {
                    artPic = pictureSmall.Substring(startIndex, endIndex - startIndex);
                    _logger.LogDebug("Extracted artist picture MD5 from URL for {ArtistName}: {ArtPic}", artist.Name, artPic);
                }
                else
                {
                    _logger.LogDebug("Failed to extract MD5 from artist URL for {ArtistName}: startIndex={StartIndex}, endIndex={EndIndex}, length={Length}", 
                        artist.Name, startIndex, endIndex, pictureSmall.Length);
                }
            }
            else
            {
                _logger.LogDebug("No 'artist/' found in picture URL for {ArtistName}: {PictureSmallUrl}", artist.Name, pictureSmall);
            }
        }
        else
        {
            _logger.LogDebug("No picture_small or md5_image available for artist {ArtistName}", artist.Name);
        }
        
        // Set the picture (even if empty)
        artist.Pic = new Picture(artPic, ArtistPictureType);
        
        _logger.LogDebug("Final artist picture MD5 for {ArtistName}: '{ArtPic}' (empty: {IsEmpty})", artist.Name, artPic, string.IsNullOrEmpty(artPic));
        
        return artist;
    }

    private static string? BuildOwnerPictureUrl(string? ownerPicture, string sizeSuffix)
    {
        if (string.IsNullOrWhiteSpace(ownerPicture))
        {
            return null;
        }

        return $"https://e-cdns-images.dzcdn.net/images/user/{ownerPicture}/{sizeSuffix}";
    }

    #region Artist Normalization (Deezer-only post-processing)

    /// <summary>
    /// Normalize track artists to enforce a single Main artist.
    /// Additional artists are moved to Featured. Uses contributor data for correct IDs.
    /// Called AFTER all parsing, BEFORE ApplySettings.
    /// </summary>
    private void NormalizeTrackArtists(DeezSpoTag.Core.Models.Track track, ApiTrack? publicTrackData)
    {
        NormalizeCombinedTrackMainArtist(track, publicTrackData);
        MoveExtraTrackMainArtistsToFeatured(track);
        SyncTrackMainArtistFromMainGroup(track, publicTrackData);
        track.Artists = BuildOrderedArtistList(track.MainArtist?.Name, track.Artist);
        track.GenerateMainFeatStrings();
    }

    private void NormalizeCombinedTrackMainArtist(DeezSpoTag.Core.Models.Track track, ApiTrack? publicTrackData)
    {
        if (!DeezSpoTag.Core.Utils.ArtistNameNormalizer.IsCombinedName(track.MainArtist?.Name))
        {
            return;
        }

        var resolved = TryResolveMainArtistFromContributors(
            track.MainArtist!.Name, publicTrackData?.Contributors);

        if (resolved != null)
        {
            _logger.LogInformation(
                "Normalized track MainArtist from combined '{Combined}' to '{Resolved}' (ID: {Id})",
                track.MainArtist.Name, resolved.Name, resolved.Id);
            track.MainArtist = new DeezSpoTag.Core.Models.Artist(
                resolved.Id, resolved.Name, "Main", track.MainArtist.Pic?.Md5);
            return;
        }

        var (primary, additional) = DeezSpoTag.Core.Utils.ArtistNameNormalizer
            .SplitCombinedName(track.MainArtist.Name);
        _logger.LogInformation(
            "Split track MainArtist name from '{Combined}' to primary '{Primary}' + {Count} additional",
            track.MainArtist.Name, primary, additional.Count);
        track.MainArtist = new DeezSpoTag.Core.Models.Artist(
            track.MainArtist.Id, primary, "Main", track.MainArtist.Pic?.Md5);

        var featuredArtists = EnsureFeaturedArtistGroup(track.Artist);
        foreach (var extra in additional.Where(extra => !featuredArtists.Contains(extra, StringComparer.OrdinalIgnoreCase)))
        {
            featuredArtists.Add(extra);
            if (!track.Artists.Contains(extra, StringComparer.OrdinalIgnoreCase))
            {
                track.Artists.Add(extra);
            }
        }
    }

    private void MoveExtraTrackMainArtistsToFeatured(DeezSpoTag.Core.Models.Track track)
    {
        var mainArtists = track.Artist.GetValueOrDefault("Main", new List<string>());
        if (mainArtists.Count <= 1)
        {
            return;
        }

        var primaryName = mainArtists[0];
        var extras = mainArtists.Skip(1).ToList();
        _logger.LogInformation(
            "Enforcing single Main artist for track '{Title}': keeping '{Primary}', moving {Count} to Featured",
            track.Title, primaryName, extras.Count);

        track.Artist["Main"] = new List<string> { primaryName };
        var featuredArtists = EnsureFeaturedArtistGroup(track.Artist);
        foreach (var extra in extras.Where(extra => !featuredArtists.Contains(extra, StringComparer.OrdinalIgnoreCase)))
        {
            featuredArtists.Add(extra);
        }
    }

    private static void SyncTrackMainArtistFromMainGroup(DeezSpoTag.Core.Models.Track track, ApiTrack? publicTrackData)
    {
        var mainArtists = track.Artist.GetValueOrDefault("Main", new List<string>());
        if (mainArtists.Count != 1 || string.Equals(track.MainArtist?.Name, mainArtists[0], StringComparison.Ordinal))
        {
            return;
        }

        var mainName = mainArtists[0];
        var contributor = publicTrackData?.Contributors?
            .FirstOrDefault(c => string.Equals(c.Name, mainName, StringComparison.OrdinalIgnoreCase));
        track.MainArtist = contributor != null
            ? new DeezSpoTag.Core.Models.Artist(
                contributor.Id, contributor.Name, "Main", track.MainArtist?.Pic?.Md5)
            : new DeezSpoTag.Core.Models.Artist(
                track.MainArtist?.Id ?? "0", mainName, "Main", track.MainArtist?.Pic?.Md5);
    }

    /// <summary>
    /// Normalize album artist to enforce a single Main artist.
    /// Called AFTER MapApiAlbumToAlbum/ParseAlbum, BEFORE assigning to track.
    /// </summary>
    private void NormalizeAlbumArtist(DeezSpoTag.Core.Models.Album album, ApiAlbum? albumData)
    {
        NormalizeCombinedAlbumMainArtist(album, albumData);
        MoveExtraAlbumMainArtistsToFeatured(album);
        SyncAlbumMainArtistFromMainGroup(album, albumData);
        album.Artists = BuildOrderedArtistList(album.MainArtist?.Name, album.Artist);
    }

    private void NormalizeCombinedAlbumMainArtist(DeezSpoTag.Core.Models.Album album, ApiAlbum? albumData)
    {
        if (!DeezSpoTag.Core.Utils.ArtistNameNormalizer.IsCombinedName(album.MainArtist?.Name))
        {
            return;
        }

        var resolved = TryResolveMainArtistFromContributors(album.MainArtist!.Name, albumData?.Contributors);
        if (resolved != null)
        {
            _logger.LogInformation(
                "Normalized album MainArtist from combined '{Combined}' to '{Resolved}' (ID: {Id})",
                album.MainArtist.Name, resolved.Name, resolved.Id);
            album.MainArtist = new DeezSpoTag.Core.Models.Artist(
                resolved.Id, resolved.Name, "Main", album.MainArtist.Pic?.Md5);
            return;
        }

        var (primary, additional) = DeezSpoTag.Core.Utils.ArtistNameNormalizer
            .SplitCombinedName(album.MainArtist.Name);
        _logger.LogInformation(
            "Split album MainArtist name from '{Combined}' to primary '{Primary}'",
            album.MainArtist.Name, primary);
        album.MainArtist = new DeezSpoTag.Core.Models.Artist(
            album.MainArtist.Id, primary, "Main", album.MainArtist.Pic?.Md5);

        var featuredArtists = EnsureFeaturedArtistGroup(album.Artist);
        foreach (var extra in additional.Where(extra => !featuredArtists.Contains(extra, StringComparer.OrdinalIgnoreCase)))
        {
            featuredArtists.Add(extra);
            if (!album.Artists.Contains(extra, StringComparer.OrdinalIgnoreCase))
            {
                album.Artists.Add(extra);
            }
        }
    }

    private void MoveExtraAlbumMainArtistsToFeatured(DeezSpoTag.Core.Models.Album album)
    {
        var mainArtists = album.Artist.GetValueOrDefault("Main", new List<string>());
        if (mainArtists.Count <= 1)
        {
            return;
        }

        var primaryName = mainArtists[0];
        var extras = mainArtists.Skip(1).ToList();
        _logger.LogInformation(
            "Enforcing single Main artist for album '{Title}': keeping '{Primary}', moving {Count} to Featured",
            album.Title, primaryName, extras.Count);

        album.Artist["Main"] = new List<string> { primaryName };
        var featuredArtists = EnsureFeaturedArtistGroup(album.Artist);
        foreach (var extra in extras.Where(extra => !featuredArtists.Contains(extra, StringComparer.OrdinalIgnoreCase)))
        {
            featuredArtists.Add(extra);
        }
    }

    private static void SyncAlbumMainArtistFromMainGroup(DeezSpoTag.Core.Models.Album album, ApiAlbum? albumData)
    {
        var mainArtists = album.Artist.GetValueOrDefault("Main", new List<string>());
        if (mainArtists.Count != 1 || string.Equals(album.MainArtist?.Name, mainArtists[0], StringComparison.Ordinal))
        {
            return;
        }

        var mainName = mainArtists[0];
        var contributor = albumData?.Contributors?
            .FirstOrDefault(c => string.Equals(c.Name, mainName, StringComparison.OrdinalIgnoreCase));
        album.MainArtist = contributor != null
            ? new DeezSpoTag.Core.Models.Artist(
                contributor.Id, contributor.Name, "Main", album.MainArtist?.Pic?.Md5)
            : new DeezSpoTag.Core.Models.Artist(
                album.MainArtist?.Id ?? "0", mainName, "Main", album.MainArtist?.Pic?.Md5);
    }

    private static List<string> EnsureFeaturedArtistGroup(Dictionary<string, List<string>> artistGroups)
    {
        if (!artistGroups.TryGetValue(FeaturedArtistGroup, out var featuredArtists))
        {
            featuredArtists = new List<string>();
            artistGroups[FeaturedArtistGroup] = featuredArtists;
        }

        return featuredArtists;
    }

    private static List<string> BuildOrderedArtistList(
        string? primaryArtistName,
        IReadOnlyDictionary<string, List<string>> artistGroups)
    {
        var allArtists = new List<string>();
        var seenArtists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(primaryArtistName))
        {
            allArtists.Add(primaryArtistName);
            seenArtists.Add(primaryArtistName);
        }

        allArtists.AddRange(artistGroups.Values
            .SelectMany(static role => role)
            .Where(static artistName => !string.IsNullOrWhiteSpace(artistName))
            .Where(seenArtists.Add));

        return allArtists;
    }

    /// <summary>
    /// Try to resolve the primary artist from the contributors list.
    /// Returns the first contributor with role "Main" whose name matches the first part
    /// of a combined name, or just the first "Main" contributor if available.
    /// </summary>
    private static ApiContributor? TryResolveMainArtistFromContributors(
        string combinedName,
        List<ApiContributor>? contributors)
    {
        if (contributors == null || contributors.Count == 0)
            return null;

        var (primaryName, _) = DeezSpoTag.Core.Utils.ArtistNameNormalizer
            .SplitCombinedName(combinedName);

        // Look for a contributor whose name matches the primary part of the split
        var match = contributors.FirstOrDefault(c =>
            string.Equals(c.Name?.Trim(), primaryName, StringComparison.OrdinalIgnoreCase)
            && (c.Role == "Main" || c.Role == null));

        if (match != null)
            return match;

        // If no name match, return the first Main contributor (better than the combined name)
        var firstMain = contributors.FirstOrDefault(c => c.Role == "Main");
        return firstMain;
    }

    #endregion
}
