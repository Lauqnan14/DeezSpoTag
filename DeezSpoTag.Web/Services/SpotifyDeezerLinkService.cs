using System.Text.Json;
using System.Linq;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyDeezerLinkService
{
    private const string DeezerArtistEntity = "artist";
    private const string DeezerAlbumEntity = "album";
    private const string DeezerTrackEntity = "track";
    private readonly DeezerClient _deezerClient;
    private readonly SpotifyPathfinderMetadataClient _pathfinderMetadataClient;
    private readonly LibraryRepository _libraryRepository;
    private readonly SongLinkResolver _songLinkResolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SpotifyDeezerLinkService> _logger;

    public SpotifyDeezerLinkService(
        DeezerClient deezerClient,
        SpotifyPathfinderMetadataClient pathfinderMetadataClient,
        LibraryRepository libraryRepository,
        LibraryConfigStore configStore,
        SongLinkResolver songLinkResolver,
        IHttpClientFactory httpClientFactory,
        ILogger<SpotifyDeezerLinkService> logger)
    {
        _deezerClient = deezerClient;
        _pathfinderMetadataClient = pathfinderMetadataClient;
        _libraryRepository = libraryRepository;
        _ = configStore;
        _songLinkResolver = songLinkResolver;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<SpotifyArtistPageResult> EnrichAsync(long localArtistId, string artistName, SpotifyArtistPageResult result, CancellationToken cancellationToken)
    {
        var existingDeezerId = await _libraryRepository.GetArtistSourceIdAsync(localArtistId, "deezer", cancellationToken);
        var deezerArtistId = !string.IsNullOrWhiteSpace(existingDeezerId)
            ? existingDeezerId
            : await ResolveArtistIdAsync(artistName);
        if (!string.IsNullOrWhiteSpace(deezerArtistId) && string.IsNullOrWhiteSpace(existingDeezerId))
        {
            await _libraryRepository.UpsertArtistSourceIdAsync(localArtistId, "deezer", deezerArtistId, cancellationToken);
        }

        var updatedArtist = result.Artist with
        {
            DeezerId = deezerArtistId,
            DeezerUrl = BuildDeezerUrl(deezerArtistId, DeezerArtistEntity)
        };

        var albums = await EnrichAlbumListAsync(result.Albums, cancellationToken);
        var appearsOn = await EnrichAlbumListAsync(result.AppearsOn, cancellationToken);
        var tracks = await EnrichTrackListAsync(result.TopTracks, artistName, cancellationToken);
        var related = await EnrichRelatedArtistsAsync(result.RelatedArtists);

        return result with
        {
            Artist = updatedArtist,
            Albums = albums,
            AppearsOn = appearsOn,
            TopTracks = tracks,
            RelatedArtists = related
        };
    }

    private async Task<List<SpotifyAlbum>> EnrichAlbumListAsync(
        List<SpotifyAlbum> albums,
        CancellationToken cancellationToken)
    {
        var enriched = new List<SpotifyAlbum>(albums.Count);
        foreach (var album in albums)
        {
            var deezerId = await ResolveAlbumIdViaTracklistAsync(album, cancellationToken);
            enriched.Add(album with
            {
                DeezerId = deezerId,
                DeezerUrl = BuildDeezerUrl(deezerId, DeezerAlbumEntity)
            });
        }

        return enriched;
    }

    private async Task<List<SpotifyTrack>> EnrichTrackListAsync(
        List<SpotifyTrack> tracks,
        string artistName,
        CancellationToken cancellationToken)
    {
        var enriched = new List<SpotifyTrack>(tracks.Count);
        foreach (var track in tracks)
        {
            var deezerId = await ResolveTopTrackDeezerIdAsync(track, artistName, cancellationToken);
            enriched.Add(track with
            {
                DeezerId = deezerId,
                DeezerUrl = BuildDeezerUrl(deezerId, DeezerTrackEntity)
            });
        }

        return enriched;
    }

    private async Task<List<SpotifyRelatedArtist>> EnrichRelatedArtistsAsync(List<SpotifyRelatedArtist> relatedArtists)
    {
        var enriched = new List<SpotifyRelatedArtist>(relatedArtists.Count);
        foreach (var relatedArtist in relatedArtists)
        {
            var deezerId = await ResolveArtistIdAsync(relatedArtist.Name);
            enriched.Add(relatedArtist with
            {
                DeezerId = deezerId,
                DeezerUrl = BuildDeezerUrl(deezerId, DeezerArtistEntity)
            });
        }

        return enriched;
    }

    private static string? BuildDeezerUrl(string? deezerId, string entityType)
    {
        return string.IsNullOrWhiteSpace(deezerId)
            ? null
            : $"https://www.deezer.com/{entityType}/{deezerId}";
    }

    private async Task<string?> ResolveArtistIdAsync(string artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        var result = await _deezerClient.SearchArtistAsync(artistName, new ApiOptions { Limit = 8 });
        return SelectBestArtistId(result, artistName) ?? SelectFirstId(result);
    }

    private async Task<string?> ResolveAlbumIdViaTracklistAsync(
        SpotifyAlbum album,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(album.Id))
        {
            return null;
        }

        try
        {
            var tracks = await _pathfinderMetadataClient.FetchAlbumTracksAsync(album.Id, cancellationToken)
                ?? new List<SpotifyTrackSummary>();
            if (tracks.Count == 0)
            {
                return null;
            }

            foreach (var track in tracks)
            {
                if (string.IsNullOrWhiteSpace(track.Isrc))
                {
                    continue;
                }

                var deezerTrackId = await SpotifyTracklistResolver.ResolveDeezerTrackIdAsync(
                    _deezerClient,
                    _songLinkResolver,
                    track,
                    new SpotifyTrackResolveOptions(
                        AllowFallbackSearch: false,
                        PreferIsrcOnly: true,
                        UseSongLink: false,
                        StrictMode: false,
                        BypassNegativeCanonicalCache: false,
                        Logger: _logger,
                        CancellationToken: cancellationToken));
                if (string.IsNullOrWhiteSpace(deezerTrackId))
                {
                    continue;
                }

                var deezerTrack = await _deezerClient.GetTrack(deezerTrackId);
                var albumId = deezerTrack?.Album?.Id.ToString();
                if (!string.IsNullOrWhiteSpace(albumId))
                {
                    return albumId;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve Deezer album for Spotify album AlbumName (Artist)");
        }

        return null;
    }

    private async Task<string?> ResolveTrackIdAsync(string trackName, string artistName)
    {
        if (string.IsNullOrWhiteSpace(trackName) || string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        var query = $"{trackName} {artistName}".Trim();
        var result = await _deezerClient.SearchTrackAsync(query, new ApiOptions { Limit = 8 });
        return SelectBestTrackId(result, trackName, artistName) ?? SelectFirstId(result);
    }

    private async Task<string?> ResolveTopTrackDeezerIdAsync(
        SpotifyTrack track,
        string artistName,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(track.Isrc))
        {
            var summary = new SpotifyTrackSummary(
                track.Id,
                track.Name,
                artistName,
                null,
                track.DurationMs,
                track.SourceUrl ?? string.Empty,
                track.AlbumImages.FirstOrDefault()?.Url,
                track.Isrc);

            var deezerId = await SpotifyTracklistResolver.ResolveDeezerTrackIdAsync(
                _deezerClient,
                _songLinkResolver,
                summary,
                new SpotifyTrackResolveOptions(
                    AllowFallbackSearch: false,
                    PreferIsrcOnly: true,
                    UseSongLink: false,
                    StrictMode: false,
                    BypassNegativeCanonicalCache: false,
                    Logger: _logger,
                    CancellationToken: cancellationToken));
            if (!string.IsNullOrWhiteSpace(deezerId))
            {
                return deezerId;
            }
        }

        return await ResolveTrackIdAsync(track.Name, artistName);
    }

    private static string? SelectBestArtistId(DeezerSearchResult result, string artistName)
    {
        if (result.Data == null || result.Data.Length == 0)
        {
            return null;
        }

        var target = SpotifyTextNormalizer.NormalizeToken(artistName);
        foreach (var item in result.Data)
        {
            if (item is not JsonElement element)
            {
                continue;
            }

            var name = GetString(element, "name");
            var normName = SpotifyTextNormalizer.NormalizeToken(name);
            if (!string.IsNullOrWhiteSpace(target) && normName == target)
            {
                return GetString(element, "id");
            }
        }

        return null;
    }

    private static string? SelectBestTrackId(DeezerSearchResult result, string trackName, string artistName)
    {
        if (result.Data == null || result.Data.Length == 0)
        {
            return null;
        }

        var targetTitle = SpotifyTextNormalizer.NormalizeToken(trackName);
        var targetArtist = SpotifyTextNormalizer.NormalizeToken(artistName);
        foreach (var item in result.Data)
        {
            if (item is not JsonElement element)
            {
                continue;
            }

            var title = GetString(element, "title") ?? GetString(element, "name");
            var artist = GetNestedString(element, "artist", "name") ?? GetString(element, "artist");
            var normTitle = SpotifyTextNormalizer.NormalizeToken(title);
            var normArtist = SpotifyTextNormalizer.NormalizeToken(artist);
            var titleMatches = !string.IsNullOrWhiteSpace(targetTitle) && normTitle == targetTitle;
            var artistMatches = string.IsNullOrWhiteSpace(targetArtist)
                || (!string.IsNullOrWhiteSpace(normArtist) && normArtist == targetArtist);
            if (titleMatches && artistMatches)
            {
                return GetString(element, "id");
            }
        }

        return null;
    }

    private static string? SelectFirstId(DeezerSearchResult result)
    {
        if (result.Data == null || result.Data.Length == 0)
        {
            return null;
        }

        var first = result.Data[0];
        if (first is JsonElement element)
        {
            return GetString(element, "id");
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string? GetNestedString(JsonElement element, string parent, string name)
    {
        if (element.TryGetProperty(parent, out var parentProp) && parentProp.ValueKind == JsonValueKind.Object &&
            parentProp.TryGetProperty(name, out var valueProp) && valueProp.ValueKind == JsonValueKind.String)
        {
            return valueProp.GetString();
        }

        return null;
    }

    public async Task<DeezerValidationResult> ValidateSpotifyCandidateAsync(
        IReadOnlyList<string> spotifyAlbumTitles,
        string artistName,
        double minimumOverlap,
        CancellationToken cancellationToken)
    {
        try
        {
            var deezerArtistId = await ResolveArtistIdAsync(artistName);
            if (string.IsNullOrWhiteSpace(deezerArtistId))
            {
                return new DeezerValidationResult(DeezerValidationStatus.SkipValidation, null, 0);
            }

            var deezerAlbumTitles = await FetchDeezerArtistAlbumTitlesAsync(deezerArtistId, cancellationToken);
            if (deezerAlbumTitles.Count < 3)
            {
                return new DeezerValidationResult(DeezerValidationStatus.SkipValidation, deezerArtistId, 0);
            }

            if (spotifyAlbumTitles.Count < 3)
            {
                return new DeezerValidationResult(DeezerValidationStatus.SkipValidation, deezerArtistId, 0);
            }

            var normalizedDeezer = new HashSet<string>(
                deezerAlbumTitles.Select(SpotifyTextNormalizer.NormalizeToken).Where(t => !string.IsNullOrWhiteSpace(t)),
                StringComparer.OrdinalIgnoreCase);
            var normalizedSpotify = spotifyAlbumTitles
                .Select(SpotifyTextNormalizer.NormalizeToken)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            var matchCount = normalizedSpotify.Count(title => normalizedDeezer.Contains(title));
            var denominator = Math.Min(normalizedSpotify.Count, normalizedDeezer.Count);
            var overlap = denominator > 0 ? (double)matchCount / denominator : 0;

            var status = overlap >= minimumOverlap
                ? DeezerValidationStatus.Valid
                : DeezerValidationStatus.Invalid;

            return new DeezerValidationResult(status, deezerArtistId, overlap);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Deezer validation failed for ArtistName");
            return new DeezerValidationResult(DeezerValidationStatus.SkipValidation, null, 0);
        }
    }

    private async Task<List<string>> FetchDeezerArtistAlbumTitlesAsync(
        string deezerArtistId,
        CancellationToken cancellationToken)
    {
        var titles = new List<string>();
        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            var url = $"https://api.deezer.com/artist/{Uri.EscapeDataString(deezerArtistId)}/albums?limit=200";
            var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return titles;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return titles;
            }

            titles.AddRange(data.EnumerateArray()
                .Select(static item => item.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String
                    ? titleProp.GetString()
                    : null)
                .Where(static title => !string.IsNullOrWhiteSpace(title))
                .Select(static title => title!));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch Deezer albums for artist {DeezerArtistId}", deezerArtistId);
        }

        return titles;
    }
}

public sealed record DeezerValidationResult(
    DeezerValidationStatus Status,
    string? DeezerArtistId,
    double OverlapPercentage);

public enum DeezerValidationStatus { Valid, Invalid, SkipValidation }
