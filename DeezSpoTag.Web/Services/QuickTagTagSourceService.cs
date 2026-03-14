using System.Text.Json;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.Extensions.DependencyInjection;

namespace DeezSpoTag.Web.Services;

public sealed class QuickTagTagSourceService
{
    private const string SpotifyProvider = "spotify";
    private const string DeezerProvider = "deezer";
    private const string BoomplayProvider = "boomplay";
    private const string AppleProvider = "apple";
    private const string ShazamProvider = "shazam";
    private const string MusicBrainzProvider = "musicbrainz";
    private const string DiscogsProvider = "discogs";
    private const string AlbumType = "album";
    private const string TrackType = "track";
    private const string UntitledLabel = "(Untitled)";
    private static readonly bool AppleDisabled = ReadAppleDisabled();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly DeezSpoTagSearchService _searchService;
    private readonly IServiceProvider _serviceProvider;
    private readonly PlatformAuthService _platformAuthService;
    private readonly DeezerApiService _deezerApiService;
    private readonly AppleMusicCatalogService _appleCatalog;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly ILogger<QuickTagTagSourceService> _logger;

    private MusicBrainzClient MusicBrainzClient => _serviceProvider.GetRequiredService<MusicBrainzClient>();
    private DiscogsClient DiscogsClient => _serviceProvider.GetRequiredService<DiscogsClient>();
    private ShazamDiscoveryService ShazamDiscoveryService => _serviceProvider.GetRequiredService<ShazamDiscoveryService>();
    private BoomplayMetadataService BoomplayMetadataService => _serviceProvider.GetRequiredService<BoomplayMetadataService>();
    private SpotifyMetadataService SpotifyMetadataService => _serviceProvider.GetRequiredService<SpotifyMetadataService>();

    public QuickTagTagSourceService(
        DeezSpoTagSearchService searchService,
        PlatformAuthService platformAuthService,
        DeezerApiService deezerApiService,
        AppleMusicCatalogService appleCatalog,
        DeezSpoTagSettingsService settingsService,
        IServiceProvider serviceProvider,
        ILogger<QuickTagTagSourceService> logger)
    {
        _searchService = searchService;
        _platformAuthService = platformAuthService;
        _deezerApiService = deezerApiService;
        _appleCatalog = appleCatalog;
        _settingsService = settingsService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<QuickTagTagSourceSearchResult> SearchAsync(QuickTagTagSourceSearchRequest request, CancellationToken cancellationToken)
    {
        var provider = NormalizeProvider(request.Provider);
        var query = BuildQuery(request);
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new InvalidOperationException("Search query is required.");
        }

        switch (provider)
        {
            case SpotifyProvider:
                return await SearchStreamingEngineAsync(SpotifyProvider, query, cancellationToken);
            case DeezerProvider:
                return await SearchDeezerAsync(request, query, cancellationToken);
            case BoomplayProvider:
                return await SearchBoomplayAsync(query, cancellationToken);
            case AppleProvider:
                if (AppleDisabled)
                {
                    return Unsupported(provider, "Apple Music is disabled.");
                }
                return await SearchStreamingEngineAsync(AppleProvider, query, cancellationToken);
            case ShazamProvider:
                return await SearchShazamAsync(query, cancellationToken);
            case MusicBrainzProvider:
                return await SearchMusicBrainzAsync(query, cancellationToken);
            case DiscogsProvider:
                return await SearchDiscogsAsync(request, query, cancellationToken);
            case "acoustid":
                return Unsupported(provider, "AcoustID search requires audio fingerprinting and is not wired into Quick Tag yet.");
            case "amazon":
                return Unsupported(provider, "Amazon catalog search requires provider credentials and is not configured in this project yet.");
            default:
                return Unsupported(provider, "Unknown tag source provider.");
        }
    }

    private async Task<QuickTagTagSourceSearchResult> SearchMusicBrainzAsync(string query, CancellationToken cancellationToken)
    {
        var response = await MusicBrainzClient.SearchAsync(query, cancellationToken);
        var recordings = response?.Recordings ?? new List<Recording>();
        var items = recordings
            .Take(60)
            .Select(recording =>
            {
                var artist = BuildArtistText(recording.ArtistCredit);
                var year = ParseYear(recording.FirstReleaseDate);
                var album = recording.Releases?.FirstOrDefault()?.Title ?? string.Empty;
                var detailsParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(album))
                {
                    detailsParts.Add(album);
                }
                if (year.HasValue)
                {
                    detailsParts.Add(year.Value.ToString());
                }

                return new QuickTagTagSourceSearchItem
                {
                    Id = recording.Id,
                    Title = string.IsNullOrWhiteSpace(recording.Title) ? UntitledLabel : recording.Title,
                    Subtitle = artist,
                    Details = string.Join(" • ", detailsParts),
                    Url = string.IsNullOrWhiteSpace(recording.Id) ? string.Empty : $"https://musicbrainz.org/recording/{recording.Id}",
                    Year = year
                };
            })
            .ToList();

        return new QuickTagTagSourceSearchResult
        {
            Provider = MusicBrainzProvider,
            Supported = true,
            Message = $"Found {items.Count} result(s) from MusicBrainz.",
            Items = items
        };
    }

    private async Task<QuickTagTagSourceSearchResult> SearchDiscogsAsync(
        QuickTagTagSourceSearchRequest request,
        string query,
        CancellationToken cancellationToken)
    {
        await TryApplyDiscogsTokenAsync("search");
        var results = await DiscogsClient.SearchAsync(
            type: null,
            query: query,
            title: string.IsNullOrWhiteSpace(request.Title) ? null : request.Title,
            artist: string.IsNullOrWhiteSpace(request.Artist) ? null : request.Artist,
            cancellationToken);

        var items = results
            .Take(60)
            .Select(MapDiscogsSearchResult)
            .ToList();

        return new QuickTagTagSourceSearchResult
        {
            Provider = DiscogsProvider,
            Supported = true,
            Message = $"Found {items.Count} result(s) from Discogs.",
            Items = items
        };
    }

    private async Task TryApplyDiscogsTokenAsync(string context)
    {
        try
        {
            var authState = await _platformAuthService.LoadAsync();
            var token = authState.Discogs?.Token?.Trim();
            if (!string.IsNullOrWhiteSpace(token))
            {
                DiscogsClient.SetToken(token);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Discogs auth token load failed. Context: {Context}", context);
        }
    }

    private static QuickTagTagSourceSearchItem MapDiscogsSearchResult(DiscogsSearchResult result)
    {
        var format = result.Formats != null && result.Formats.Count > 0
            ? string.Join(", ", result.Formats.Where(value => !string.IsNullOrWhiteSpace(value)))
            : string.Empty;
        var label = result.Labels != null && result.Labels.Count > 0
            ? string.Join(", ", result.Labels.Where(value => !string.IsNullOrWhiteSpace(value)))
            : string.Empty;
        var detailsParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(format))
        {
            detailsParts.Add(format);
        }
        if (!string.IsNullOrWhiteSpace(label))
        {
            detailsParts.Add(label);
        }
        if (result.Year.HasValue)
        {
            detailsParts.Add(result.Year.Value.ToString());
        }

        return new QuickTagTagSourceSearchItem
        {
            Id = result.Id > 0 ? $"{result.Type.ToString().ToLowerInvariant()}:{result.Id}" : string.Empty,
            Title = string.IsNullOrWhiteSpace(result.Title) ? $"Discogs {result.Type}" : result.Title,
            Subtitle = result.Type.ToString(),
            Details = string.Join(" • ", detailsParts),
            Url = ResolveDiscogsUrl(result),
            Year = result.Year
        };
    }

    private static QuickTagTagSourceSearchResult Unsupported(string provider, string message)
    {
        return new QuickTagTagSourceSearchResult
        {
            Provider = provider,
            Supported = false,
            Message = message,
            Items = new List<QuickTagTagSourceSearchItem>()
        };
    }

    private async Task<QuickTagTagSourceSearchResult> SearchStreamingEngineAsync(
        string engine,
        string query,
        CancellationToken cancellationToken)
    {
        var request = new DeezSpoTagSearchRequest(
            Engine: engine,
            Query: query,
            Limit: 50,
            Offset: 0);
        var result = await _searchService.SearchAsync(request, cancellationToken);

        if (result == null)
        {
            return Unsupported(engine, $"{ToTitleCase(engine)} search is currently unavailable.");
        }

        var items = new List<QuickTagTagSourceSearchItem>();
        items.AddRange(MapStreamingItems(result.Tracks, TrackType));
        items.AddRange(MapStreamingItems(result.Albums, AlbumType));
        items = items.Take(60).ToList();

        return new QuickTagTagSourceSearchResult
        {
            Provider = engine,
            Supported = true,
            Message = $"Found {items.Count} result(s) from {ToTitleCase(engine)}.",
            Items = items
        };
    }

    private async Task<QuickTagTagSourceSearchResult> SearchDeezerAsync(
        QuickTagTagSourceSearchRequest request,
        string query,
        CancellationToken cancellationToken)
    {
        QuickTagTagSourceSearchItem? resolvedItem = null;
        try
        {
            var resolvedTrack = await TryResolveDeezerTrackAsync(request, cancellationToken);
            if (resolvedTrack != null)
            {
                resolvedItem = BuildDeezerResolvedItem(resolvedTrack);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Metadata-first Deezer resolution failed.");
        }

        var deezerRequest = new DeezSpoTagSearchRequest(
            Engine: DeezerProvider,
            Query: query,
            Limit: 50,
            Offset: 0,
            Title: request.Title,
            Artist: request.Artist,
            Album: request.Album,
            Isrc: request.Isrc,
            DurationMs: request.DurationMs);
        var result = await _searchService.SearchAsync(deezerRequest, cancellationToken);

        if (result == null)
        {
            if (resolvedItem != null)
            {
                return new QuickTagTagSourceSearchResult
                {
                    Provider = DeezerProvider,
                    Supported = true,
                    Message = "Found 1 result(s) from Deezer.",
                    Items = new List<QuickTagTagSourceSearchItem> { resolvedItem }
                };
            }

            return Unsupported(DeezerProvider, "Deezer search is currently unavailable.");
        }

        var items = new List<QuickTagTagSourceSearchItem>();
        if (resolvedItem != null)
        {
            items.Add(resolvedItem);
        }

        items.AddRange(MapStreamingItems(result.Tracks, TrackType));
        items.AddRange(MapStreamingItems(result.Albums, AlbumType));
        items = DeduplicateTagSourceItems(items).Take(60).ToList();

        return new QuickTagTagSourceSearchResult
        {
            Provider = DeezerProvider,
            Supported = true,
            Message = $"Found {items.Count} result(s) from Deezer.",
            Items = items
        };
    }

    private async Task<QuickTagTagSourceSearchResult> SearchShazamAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var results = await ShazamDiscoveryService.SearchTracksAsync(query, limit: 50, offset: 0, cancellationToken);
        var items = results
            .Select(card =>
            {
                var year = ParseYear(card.ReleaseDate);
                var detailsParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(card.Album))
                {
                    detailsParts.Add(card.Album);
                }
                if (year.HasValue)
                {
                    detailsParts.Add(year.Value.ToString());
                }
                if (card.DurationMs.HasValue && card.DurationMs.Value > 0)
                {
                    detailsParts.Add(FormatDuration(TimeSpan.FromMilliseconds(card.DurationMs.Value)));
                }

                return new QuickTagTagSourceSearchItem
                {
                    Id = card.Id,
                    Title = string.IsNullOrWhiteSpace(card.Title) ? UntitledLabel : card.Title,
                    Subtitle = card.Artist,
                    Details = string.Join(" • ", detailsParts),
                    Url = FirstNonEmpty(card.Url, card.AppleMusicUrl, card.SpotifyUrl) ?? string.Empty,
                    Year = year
                };
            })
            .Take(60)
            .ToList();

        return new QuickTagTagSourceSearchResult
        {
            Provider = ShazamProvider,
            Supported = true,
            Message = $"Found {items.Count} result(s) from Shazam.",
            Items = items
        };
    }

    private async Task<QuickTagTagSourceSearchResult> SearchBoomplayAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var tracks = await BoomplayMetadataService.SearchSongsAsync(query, limit: 30, cancellationToken);
        var items = tracks
            .Where(static track => track != null)
            .Select(track =>
            {
                var year = ParseYear(track.ReleaseDate);
                var detailsParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(track.Album))
                {
                    detailsParts.Add(track.Album.Trim());
                }
                if (year.HasValue)
                {
                    detailsParts.Add(year.Value.ToString());
                }
                if (track.DurationMs > 0)
                {
                    detailsParts.Add(FormatDuration(TimeSpan.FromMilliseconds(track.DurationMs)));
                }

                var url = track.Url;
                if (string.IsNullOrWhiteSpace(url))
                {
                    url = string.IsNullOrWhiteSpace(track.Id)
                        ? string.Empty
                        : $"https://www.boomplay.com/songs/{track.Id}";
                }

                return new QuickTagTagSourceSearchItem
                {
                    Id = track.Id,
                    Title = string.IsNullOrWhiteSpace(track.Title) ? UntitledLabel : track.Title,
                    Subtitle = track.Artist ?? string.Empty,
                    Details = string.Join(" • ", detailsParts),
                    Url = url,
                    Year = year
                };
            })
            .Take(60)
            .ToList();

        return new QuickTagTagSourceSearchResult
        {
            Provider = BoomplayProvider,
            Supported = true,
            Message = $"Found {items.Count} result(s) from Boomplay.",
            Items = items
        };
    }

    private static IEnumerable<QuickTagTagSourceSearchItem> MapStreamingItems(IReadOnlyList<object> source, string defaultType)
    {
        if (source == null || source.Count == 0)
        {
            yield break;
        }

        foreach (var item in source)
        {
            if (TryMapStreamingItem(item, defaultType, out var mappedItem))
            {
                yield return mappedItem;
            }
        }
    }

    private static bool TryMapStreamingItem(object item, string defaultType, out QuickTagTagSourceSearchItem mappedItem)
    {
        mappedItem = default!;
        var element = JsonSerializer.SerializeToElement(item, JsonOptions);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var title = ReadString(element, "name", "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var id = ReadString(element, "spotifyId", "deezerId", "appleId", "id");
        var url = ReadString(element, "spotifyUrl", "deezerUrl", "appleUrl", "link", "url", "sourceUrl");
        var sourceType = ReadString(element, "type");
        var resultType = string.IsNullOrWhiteSpace(sourceType) ? defaultType : sourceType;
        var artist = ReadArtistName(element);
        var album = ReadAlbumName(element);
        var year = ReadYear(element);
        var durationText = ReadDurationText(element);

        mappedItem = new QuickTagTagSourceSearchItem
        {
            Id = id,
            Title = title,
            Subtitle = BuildStreamingSubtitle(artist, album, resultType),
            Details = BuildStreamingDetails(resultType, year, durationText),
            Url = url,
            Year = year
        };
        return true;
    }

    private static string BuildStreamingSubtitle(string artist, string album, string resultType)
    {
        var subtitleParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(artist))
        {
            subtitleParts.Add(artist);
        }

        if (!string.IsNullOrWhiteSpace(album) && !string.Equals(resultType, AlbumType, StringComparison.OrdinalIgnoreCase))
        {
            subtitleParts.Add(album);
        }

        return string.Join(" • ", subtitleParts);
    }

    private static string BuildStreamingDetails(string resultType, int? year, string durationText)
    {
        var detailsParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(resultType))
        {
            detailsParts.Add(resultType);
        }
        if (year.HasValue)
        {
            detailsParts.Add(year.Value.ToString());
        }
        if (!string.IsNullOrWhiteSpace(durationText))
        {
            detailsParts.Add(durationText);
        }

        return string.Join(" • ", detailsParts);
    }

    private static string BuildQuery(QuickTagTagSourceSearchRequest request)
    {
        var query = (request.Query ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(query))
        {
            return query;
        }

        var parts = new[]
        {
            request.Artist,
            request.Title,
            request.Album,
            request.Isrc
        };

        return string.Join(" ", parts.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
    }

    private async Task<ApiTrack?> TryResolveDeezerTrackAsync(QuickTagTagSourceSearchRequest request, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (!string.IsNullOrWhiteSpace(request.Isrc))
        {
            var byIsrc = await TryGetDeezerTrackByIsrcAsync(request.Isrc!);
            if (byIsrc != null)
            {
                return byIsrc;
            }
        }

        var artist = (request.Artist ?? string.Empty).Trim();
        var title = (request.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return await TryGetDeezerTrackByMetadataAsync(
            artist,
            title,
            (request.Album ?? string.Empty).Trim(),
            request.DurationMs);
    }

    private async Task<ApiTrack?> TryGetDeezerTrackByIsrcAsync(string isrc)
    {
        try
        {
            var track = await _deezerApiService.GetTrackByIsrcAsync(isrc.Trim());
            return IsValidDeezerTrack(track) ? track : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Deezer ISRC lookup failed in Quick Tag for Isrc.");
            return null;
        }
    }

    private async Task<ApiTrack?> TryGetDeezerTrackByMetadataAsync(
        string artist,
        string title,
        string album,
        long? durationMs)
    {
        try
        {
            var duration = durationMs.HasValue && durationMs.Value > 0
                ? (int?)Math.Clamp(durationMs.Value, 1L, int.MaxValue)
                : null;
            var trackId = await _deezerApiService.GetTrackIdFromMetadataAsync(artist, title, album, duration);

            if (string.IsNullOrWhiteSpace(trackId) || trackId == "0")
            {
                return null;
            }

            var track = await _deezerApiService.GetTrackAsync(trackId);
            return IsValidDeezerTrack(track) ? track : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Deezer metadata lookup failed in Quick Tag for Artist - Title.");
            return null;
        }
    }

    private static bool IsValidDeezerTrack(ApiTrack? track)
    {
        return track != null
            && !string.IsNullOrWhiteSpace(track.Id)
            && !string.Equals(track.Id, "0", StringComparison.OrdinalIgnoreCase);
    }

    private static QuickTagTagSourceSearchItem BuildDeezerResolvedItem(ApiTrack track)
    {
        var title = string.IsNullOrWhiteSpace(track.TitleShort) ? track.Title : track.TitleShort;
        var artist = track.Artist?.Name ?? string.Empty;
        var album = track.Album?.Title ?? string.Empty;
        var year = ParseYear(track.ReleaseDate);

        var subtitleParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(artist))
        {
            subtitleParts.Add(artist);
        }
        if (!string.IsNullOrWhiteSpace(album))
        {
            subtitleParts.Add(album);
        }

        var detailsParts = new List<string> { "track" };
        if (year.HasValue)
        {
            detailsParts.Add(year.Value.ToString());
        }
        if (track.Duration > 0)
        {
            detailsParts.Add(FormatDuration(TimeSpan.FromSeconds(track.Duration)));
        }
        if (!string.IsNullOrWhiteSpace(track.Isrc))
        {
            detailsParts.Add(track.Isrc.Trim());
        }

        var url = !string.IsNullOrWhiteSpace(track.Link)
            ? track.Link
            : $"https://www.deezer.com/track/{track.Id}";

        return new QuickTagTagSourceSearchItem
        {
            Id = track.Id,
            Title = string.IsNullOrWhiteSpace(title) ? UntitledLabel : title,
            Subtitle = string.Join(" • ", subtitleParts),
            Details = string.Join(" • ", detailsParts),
            Url = url,
            Year = year
        };
    }

    private static List<QuickTagTagSourceSearchItem> DeduplicateTagSourceItems(IEnumerable<QuickTagTagSourceSearchItem> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<QuickTagTagSourceSearchItem>();
        foreach (var item in items)
        {
            if (item == null)
            {
                continue;
            }

            var id = (item.Id ?? string.Empty).Trim();
            var url = (item.Url ?? string.Empty).Trim();
            string key;
            if (!string.IsNullOrWhiteSpace(id))
            {
                key = $"id:{id}";
            }
            else if (!string.IsNullOrWhiteSpace(url))
            {
                key = $"url:{url}";
            }
            else
            {
                key = $"name:{(item.Title ?? string.Empty).Trim()}|{(item.Subtitle ?? string.Empty).Trim()}";
            }
            if (!seen.Add(key))
            {
                continue;
            }

            deduped.Add(item);
        }

        return deduped;
    }

    private static string NormalizeProvider(string? provider)
    {
        var normalized = (provider ?? SpotifyProvider).Trim().ToLowerInvariant();
        return normalized switch
        {
            SpotifyProvider => SpotifyProvider,
            DeezerProvider => DeezerProvider,
            BoomplayProvider => BoomplayProvider,
            AppleProvider => AppleProvider,
            "applemusic" => AppleProvider,
            ShazamProvider => ShazamProvider,
            MusicBrainzProvider => MusicBrainzProvider,
            "acoustid" => "acoustid",
            "amazon" => "amazon",
            DiscogsProvider => DiscogsProvider,
            _ => normalized
        };
    }

    private static string BuildArtistText(List<ArtistCredit>? credits)
    {
        if (credits == null || credits.Count == 0)
        {
            return string.Empty;
        }

        var names = credits
            .Select(credit => !string.IsNullOrWhiteSpace(credit.Name) ? credit.Name : credit.Artist?.Name ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        return string.Join(", ", names);
    }

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return null;
        }

        if (date.Length >= 4 && int.TryParse(date[..4], out var year))
        {
            return year;
        }

        return null;
    }

    private static string ResolveDiscogsUrl(DiscogsSearchResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Uri))
        {
            if (Uri.TryCreate(result.Uri, UriKind.Absolute, out var absolute))
            {
                return absolute.ToString();
            }

            if (result.Uri.StartsWith('/'))
            {
                return $"https://www.discogs.com{result.Uri}";
            }
        }

        return result.Url ?? string.Empty;
    }

    private static string ToTitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static string ReadArtistName(JsonElement element)
    {
        if (TryGetPropertyIgnoreCase(element, "artist", out var artistNode))
        {
            if (artistNode.ValueKind == JsonValueKind.String)
            {
                return artistNode.GetString() ?? string.Empty;
            }

            if (artistNode.ValueKind == JsonValueKind.Object)
            {
                var nestedName = ReadString(artistNode, "name", "title");
                if (!string.IsNullOrWhiteSpace(nestedName))
                {
                    return nestedName;
                }
            }
        }

        return ReadString(element, "owner", "curator", "subtitle");
    }

    private static string ReadAlbumName(JsonElement element)
    {
        if (TryGetPropertyIgnoreCase(element, "album", out var albumNode))
        {
            if (albumNode.ValueKind == JsonValueKind.String)
            {
                return albumNode.GetString() ?? string.Empty;
            }

            if (albumNode.ValueKind == JsonValueKind.Object)
            {
                var nested = ReadString(albumNode, "title", "name");
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return ReadString(element, "albumTitle");
    }

    private static int? ReadYear(JsonElement element)
    {
        var direct = ReadString(element, "year");
        if (int.TryParse(direct, out var year) && year > 0)
        {
            return year;
        }

        var date = ReadString(element, "releaseDate", "release_date");
        if (!string.IsNullOrWhiteSpace(date) && date.Length >= 4 && int.TryParse(date[..4], out year) && year > 0)
        {
            return year;
        }

        return null;
    }

    private static string ReadDurationText(JsonElement element)
    {
        if (TryReadInt64(element, out var durationMs, "durationMs", "duration_ms") && durationMs > 0)
        {
            return FormatDuration(TimeSpan.FromMilliseconds(durationMs));
        }

        if (TryReadInt64(element, out var durationSeconds, "duration") && durationSeconds > 0)
        {
            return FormatDuration(TimeSpan.FromSeconds(durationSeconds));
        }

        return string.Empty;
    }

    private static string FormatDuration(TimeSpan value)
    {
        var totalMinutes = (int)value.TotalMinutes;
        var seconds = value.Seconds;
        return $"{totalMinutes}:{seconds:00}";
    }

    private static bool TryReadInt64(JsonElement element, out long value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value))
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? string.Empty;
            }

            if (property.ValueKind == JsonValueKind.Number || property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
            {
                return property.ToString();
            }
        }

        return string.Empty;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var match = element
                .EnumerateObject()
                .Where(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(property => (JsonElement?)property.Value)
                .FirstOrDefault();
            if (match.HasValue)
            {
                value = match.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool ReadAppleDisabled()
    {
        var value = Environment.GetEnvironmentVariable("DEEZSPOTAG_APPLE_DISABLED");
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values
            .Select(static value => value?.Trim())
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    public async Task<QuickTagTagSourceDetail?> GetDetailAsync(
        string provider,
        string id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        try
        {
            return (provider.Trim().ToLowerInvariant()) switch
            {
                DeezerProvider => await GetDeezerDetailAsync(id),
                SpotifyProvider => await GetSpotifyDetailAsync(id, cancellationToken),
                BoomplayProvider => await GetBoomplayDetailAsync(id, cancellationToken),
                AppleProvider => await GetAppleDetailAsync(id, cancellationToken),
                ShazamProvider => await GetShazamDetailAsync(id, cancellationToken),
                MusicBrainzProvider => await GetMusicBrainzDetailAsync(id, cancellationToken),
                DiscogsProvider => await GetDiscogsDetailAsync(id, cancellationToken),
                _ => null
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch tag source detail for Provider:Id.");
            return null;
        }
    }

    private async Task<QuickTagTagSourceDetail?> GetDeezerDetailAsync(string id)
    {
        var track = await _deezerApiService.GetTrackAsync(id);
        if (track == null)
        {
            return null;
        }

        var genre = track.Genres != null && track.Genres.Count > 0
            ? string.Join("; ", track.Genres)
            : null;

        return new QuickTagTagSourceDetail
        {
            Provider = DeezerProvider,
            Id = id,
            Title = NullIfEmpty(track.Title),
            Artist = NullIfEmpty(track.Artist?.Name),
            Album = NullIfEmpty(track.Album?.Title),
            AlbumArtist = NullIfEmpty(track.Album?.Artist?.Name),
            TrackNumber = track.TrackPosition > 0 ? track.TrackPosition : null,
            DiscNumber = track.DiskNumber > 0 ? track.DiskNumber : null,
            Date = NullIfEmpty(track.ReleaseDate),
            Year = ParseYear(track.ReleaseDate),
            Genre = genre,
            Label = NullIfEmpty(track.Album?.Label),
            Isrc = NullIfEmpty(track.Isrc),
            Copyright = NullIfEmpty(track.Copyright),
            Bpm = track.Bpm > 0 ? (int)Math.Round(track.Bpm) : null,
            CoverUrl = NullIfEmpty(track.Album?.CoverBig) ?? NullIfEmpty(track.Album?.CoverXl),
            Url = NullIfEmpty(track.Link),
            DurationMs = track.Duration > 0 ? track.Duration * 1000L : null,
            Composer = track.Contributors != null
                ? NullIfEmpty(string.Join("; ", track.Contributors
                    .Where(c => string.Equals(c.Role, "Composer", StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))))
                : null,
            Lyricist = track.Contributors != null
                ? NullIfEmpty(string.Join("; ", track.Contributors
                    .Where(c => string.Equals(c.Role, "Author", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(c.Role, "Lyricist", StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))))
                : null
        };
    }

    private async Task<QuickTagTagSourceDetail?> GetSpotifyDetailAsync(string id, CancellationToken cancellationToken)
    {
        var url = $"https://open.spotify.com/track/{id}";
        var meta = await SpotifyMetadataService.FetchByUrlAsync(url, cancellationToken);
        if (meta == null)
        {
            return null;
        }

        var track = meta.TrackList.FirstOrDefault();
        if (track == null)
        {
            return null;
        }

        var genre = track.Genres != null && track.Genres.Count > 0
            ? string.Join("; ", track.Genres)
            : null;

        return new QuickTagTagSourceDetail
        {
            Provider = SpotifyProvider,
            Id = id,
            Title = NullIfEmpty(track.Name),
            Artist = NullIfEmpty(track.Artists),
            AlbumArtist = NullIfEmpty(track.AlbumArtist),
            Album = NullIfEmpty(track.Album),
            TrackNumber = track.TrackNumber,
            DiscNumber = track.DiscNumber,
            Date = NullIfEmpty(track.ReleaseDate),
            Year = ParseYear(track.ReleaseDate),
            Genre = genre,
            Label = NullIfEmpty(track.Label),
            Isrc = NullIfEmpty(track.Isrc),
            Bpm = track.Tempo.HasValue && track.Tempo.Value > 0 ? (int)Math.Round(track.Tempo.Value) : null,
            Key = NullIfEmpty(SpotifyAudioFeatureMapper.MapKey(track.Key, track.Mode)),
            CoverUrl = NullIfEmpty(track.ImageUrl),
            Url = NullIfEmpty(track.SourceUrl),
            DurationMs = track.DurationMs.HasValue && track.DurationMs.Value > 0 ? track.DurationMs.Value : null,
            Danceability = track.Danceability,
            Energy = track.Energy,
            Valence = track.Valence,
            Acousticness = track.Acousticness,
            Instrumentalness = track.Instrumentalness,
            Speechiness = track.Speechiness,
            Loudness = track.Loudness,
            Tempo = track.Tempo,
            TimeSignature = track.TimeSignature,
            Liveness = track.Liveness
        };
    }

    private async Task<QuickTagTagSourceDetail?> GetBoomplayDetailAsync(string id, CancellationToken cancellationToken)
    {
        var track = await BoomplayMetadataService.GetSongAsync(id, cancellationToken);
        if (track == null)
        {
            return null;
        }

        return new QuickTagTagSourceDetail
        {
            Provider = BoomplayProvider,
            Id = id,
            Title = NullIfEmpty(track.Title),
            Artist = NullIfEmpty(track.Artist),
            AlbumArtist = NullIfEmpty(track.AlbumArtist),
            Album = NullIfEmpty(track.Album),
            TrackNumber = track.TrackNumber > 0 ? track.TrackNumber : null,
            DiscNumber = track.DiscNumber > 0 ? track.DiscNumber : null,
            Date = NullIfEmpty(track.ReleaseDate),
            Year = ParseYear(track.ReleaseDate),
            Genre = track.Genres.Count > 0 ? string.Join("; ", track.Genres) : null,
            Isrc = NullIfEmpty(track.Isrc),
            Publisher = NullIfEmpty(track.Publisher),
            Composer = NullIfEmpty(track.Composer),
            Bpm = track.Bpm > 0 ? track.Bpm : null,
            Key = NullIfEmpty(track.Key),
            Language = NullIfEmpty(track.Language),
            CoverUrl = NullIfEmpty(track.CoverUrl),
            Url = NullIfEmpty(track.Url),
            DurationMs = track.DurationMs > 0 ? track.DurationMs : null
        };
    }

    private async Task<QuickTagTagSourceDetail?> GetAppleDetailAsync(string id, CancellationToken cancellationToken)
    {
        if (AppleDisabled)
        {
            return null;
        }

        var document = await LoadAppleSongDocumentAsync(id, cancellationToken);
        if (document is null)
        {
            return null;
        }

        using (document)
        {
            if (!TryGetAppleSongAttributes(document.RootElement, out var track, out var attributes))
            {
                return null;
            }

            return BuildAppleSongDetail(id, track, attributes);
        }
    }

    private async Task<JsonDocument?> LoadAppleSongDocumentAsync(string id, CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        var storefront = await _appleCatalog.ResolveStorefrontAsync(
            settings.AppleMusic?.Storefront,
            settings.AppleMusic?.MediaUserToken,
            cancellationToken);

        return await _appleCatalog.GetSongAsync(
            id,
            storefront,
            language: "en-US",
            cancellationToken,
            settings.AppleMusic?.MediaUserToken);
    }

    private static bool TryGetAppleSongAttributes(JsonElement root, out JsonElement track, out JsonElement attributes)
    {
        track = default;
        attributes = default;
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() == 0)
        {
            return false;
        }

        track = data[0];
        if (!track.TryGetProperty("attributes", out attributes) || attributes.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return true;
    }

    private static QuickTagTagSourceDetail BuildAppleSongDetail(string fallbackId, JsonElement track, JsonElement attributes)
    {
        var genre = ReadAppleGenres(attributes);
        var date = attributes.TryGetProperty("releaseDate", out var dateElement) && dateElement.ValueKind == JsonValueKind.String
            ? dateElement.GetString()
            : null;

        return new QuickTagTagSourceDetail
        {
            Provider = AppleProvider,
            Id = NullIfEmpty(track.TryGetProperty("id", out var idElement) ? idElement.GetString() : null) ?? fallbackId,
            Title = NullIfEmpty(attributes.TryGetProperty("name", out var titleElement) ? titleElement.GetString() : null),
            Artist = NullIfEmpty(attributes.TryGetProperty("artistName", out var artistElement) ? artistElement.GetString() : null),
            AlbumArtist = NullIfEmpty(attributes.TryGetProperty("artistName", out var albumArtistElement) ? albumArtistElement.GetString() : null),
            Album = NullIfEmpty(attributes.TryGetProperty("albumName", out var albumElement) ? albumElement.GetString() : null),
            TrackNumber = TryReadPositiveInt(attributes, out var trackNumber, "trackNumber") ? trackNumber : null,
            DiscNumber = TryReadPositiveInt(attributes, out var discNumber, "discNumber") ? discNumber : null,
            Date = NullIfEmpty(date),
            Year = ParseYear(date),
            Genre = NullIfEmpty(genre),
            Label = NullIfEmpty(attributes.TryGetProperty("recordLabel", out var labelElement) ? labelElement.GetString() : null),
            Isrc = NullIfEmpty(attributes.TryGetProperty("isrc", out var isrcElement) ? isrcElement.GetString() : null),
            Copyright = NullIfEmpty(attributes.TryGetProperty("copyright", out var copyrightElement) ? copyrightElement.GetString() : null),
            Composer = NullIfEmpty(attributes.TryGetProperty("composerName", out var composerElement) ? composerElement.GetString() : null),
            CoverUrl = ReadAppleArtworkUrl(attributes),
            Url = NullIfEmpty(attributes.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null),
            DurationMs = TryReadPositiveInt64(attributes, out var durationMs, "durationInMillis") ? durationMs : null
        };
    }

    private async Task<QuickTagTagSourceDetail?> GetShazamDetailAsync(string id, CancellationToken cancellationToken)
    {
        var card = await ShazamDiscoveryService.GetTrackAsync(id, cancellationToken);
        if (card == null)
        {
            return null;
        }

        return new QuickTagTagSourceDetail
        {
            Provider = ShazamProvider,
            Id = id,
            Title = NullIfEmpty(card.Title),
            Artist = NullIfEmpty(card.Artist),
            AlbumArtist = NullIfEmpty(card.Artist),
            Album = NullIfEmpty(card.Album),
            TrackNumber = card.TrackNumber,
            DiscNumber = card.DiscNumber,
            Date = NullIfEmpty(card.ReleaseDate),
            Year = ParseYear(card.ReleaseDate),
            Genre = NullIfEmpty(card.Genre),
            Label = NullIfEmpty(card.Label),
            Isrc = NullIfEmpty(card.Isrc),
            Publisher = NullIfEmpty(card.Publisher),
            Composer = NullIfEmpty(card.Composer),
            Lyricist = NullIfEmpty(card.Lyricist),
            Language = NullIfEmpty(card.Language),
            Key = NullIfEmpty(card.Key),
            Explicit = card.Explicit,
            CoverUrl = NullIfEmpty(card.ArtworkUrl),
            Url = FirstNonEmpty(card.Url, card.AppleMusicUrl, card.SpotifyUrl),
            DurationMs = card.DurationMs.HasValue && card.DurationMs.Value > 0 ? card.DurationMs.Value : null,
            OtherTags = card.Tags != null && card.Tags.Count > 0
                ? card.Tags.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase)
                : null
        };
    }

    private async Task<QuickTagTagSourceDetail?> GetMusicBrainzDetailAsync(string id, CancellationToken cancellationToken)
    {
        var recording = await MusicBrainzClient.GetRecordingAsync(id, cancellationToken);
        if (recording == null)
        {
            return null;
        }

        var artist = BuildArtistText(recording.ArtistCredit);
        var release = recording.Releases?.FirstOrDefault();
        var date = NullIfEmpty(recording.FirstReleaseDate) ?? NullIfEmpty(release?.Date);

        return new QuickTagTagSourceDetail
        {
            Provider = MusicBrainzProvider,
            Id = id,
            Title = NullIfEmpty(recording.Title),
            Artist = NullIfEmpty(artist),
            Album = NullIfEmpty(release?.Title),
            Date = date,
            Year = ParseYear(date),
            Isrc = recording.Isrcs != null && recording.Isrcs.Count > 0 ? recording.Isrcs[0] : null,
            DurationMs = recording.Length,
            Url = $"https://musicbrainz.org/recording/{id}"
        };
    }

    private async Task<QuickTagTagSourceDetail?> GetDiscogsDetailAsync(string id, CancellationToken cancellationToken)
    {
        var releaseType = DiscogsReleaseType.Release;
        var numericId = id;

        var colonIndex = id.IndexOf(':');
        if (colonIndex > 0)
        {
            var prefix = id[..colonIndex];
            numericId = id[(colonIndex + 1)..];
            if (string.Equals(prefix, "master", StringComparison.OrdinalIgnoreCase))
            {
                releaseType = DiscogsReleaseType.Master;
            }
        }

        if (!long.TryParse(numericId, out var releaseId) || releaseId <= 0)
        {
            return null;
        }

        await TryApplyDiscogsTokenAsync("detail");

        var release = await DiscogsClient.GetReleaseAsync(releaseType, releaseId, cancellationToken);
        if (release == null)
        {
            return null;
        }

        var artist = release.Artists.Count > 0
            ? string.Join("; ", release.Artists.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)))
            : null;
        var genre = release.Genres.Count > 0
            ? string.Join("; ", release.Genres)
            : null;
        var label = release.Labels != null && release.Labels.Count > 0
            ? release.Labels[0].Name
            : null;
        var coverUrl = release.Images != null && release.Images.Count > 0
            ? release.Images.FirstOrDefault(i => i.ImageType == "primary")?.Url ?? release.Images[0].Url
            : null;

        return new QuickTagTagSourceDetail
        {
            Provider = DiscogsProvider,
            Id = id,
            Title = NullIfEmpty(release.Title),
            Artist = NullIfEmpty(artist),
            Date = NullIfEmpty(release.Released),
            Year = release.Year,
            Genre = genre,
            Label = NullIfEmpty(label),
            CoverUrl = NullIfEmpty(coverUrl),
            Url = NullIfEmpty(release.Url)
        };
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadAppleArtworkUrl(JsonElement attributes)
    {
        if (!attributes.TryGetProperty("artwork", out var artwork) || artwork.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var template = NullIfEmpty(ReadString(artwork, "url"));
        if (template == null)
        {
            return null;
        }

        var width = TryReadPositiveInt(artwork, out var parsedWidth, "width") ? parsedWidth : 1200;
        var height = TryReadPositiveInt(artwork, out var parsedHeight, "height") ? parsedHeight : 1200;

        return template
            .Replace("{w}", width.ToString(), StringComparison.Ordinal)
            .Replace("{h}", height.ToString(), StringComparison.Ordinal)
            .Replace("{f}", "jpg", StringComparison.Ordinal);
    }

    private static string? ReadAppleGenres(JsonElement attributes)
    {
        if (!attributes.TryGetProperty("genreNames", out var genres) || genres.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<string>();
        foreach (var entry in genres.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var genre = entry.GetString();
            if (!string.IsNullOrWhiteSpace(genre))
            {
                values.Add(genre.Trim());
            }
        }

        if (values.Count == 0)
        {
            return null;
        }

        return string.Join("; ", values.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static bool TryReadPositiveInt(JsonElement element, out int value, params string[] names)
    {
        if (TryReadInt64(element, out var raw, names)
            && raw > 0
            && raw <= int.MaxValue)
        {
            value = (int)raw;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryReadPositiveInt64(JsonElement element, out long value, params string[] names)
    {
        if (TryReadInt64(element, out var raw, names) && raw > 0)
        {
            value = raw;
            return true;
        }

        value = 0;
        return false;
    }

}

public sealed class QuickTagTagSourceSearchRequest
{
    public string? Provider { get; set; }
    public string? Query { get; set; }
    public string? Path { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public int? Year { get; set; }
    public int? TrackNumber { get; set; }
    public string? Isrc { get; set; }
    public long? DurationMs { get; set; }
}

public sealed class QuickTagTagSourceSearchResult
{
    public string Provider { get; set; } = "musicbrainz";
    public bool Supported { get; set; } = true;
    public string Message { get; set; } = string.Empty;
    public List<QuickTagTagSourceSearchItem> Items { get; set; } = new();
}

public sealed class QuickTagTagSourceSearchItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int? Year { get; set; }
}

public sealed class QuickTagTagSourceDetail : AudioFeaturesBase
{
    public string Provider { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Album { get; set; }
    public int? TrackNumber { get; set; }
    public int? DiscNumber { get; set; }
    public int? Year { get; set; }
    public string? Date { get; set; }
    public string? Genre { get; set; }
    public string? Label { get; set; }
    public string? Isrc { get; set; }
    public string? Publisher { get; set; }
    public string? Copyright { get; set; }
    public bool? Explicit { get; set; }
    public string? Composer { get; set; }
    public string? Conductor { get; set; }
    public string? Lyricist { get; set; }
    public string? Language { get; set; }
    public int? Bpm { get; set; }
    public string? Key { get; set; }
    public string? CoverUrl { get; set; }
    public string? Url { get; set; }
    public long? DurationMs { get; set; }
    public Dictionary<string, List<string>>? OtherTags { get; set; }
}
