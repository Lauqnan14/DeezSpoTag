using System.Text.Json;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Metadata.Qobuz;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class ExternalTrackMatchService
{
    private readonly record struct ResolvedPlatformLinks(
        string? QobuzTrackId,
        string? QobuzUrl,
        string? AppleMusicId,
        string? AppleMusicUrl);

    private static readonly ResolvedPlatformLinks EmptyResolvedPlatformLinks = new(null, null, null, null);
    private readonly DeezerClient _deezerClient;
    private readonly SongLinkResolver _songLinkResolver;
    private readonly IQobuzMetadataService _qobuzMetadataService;
    private readonly AppleMusicCatalogService _appleCatalogService;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IAcousticIdMatcher _acousticIdMatcher;
    private readonly ILogger<ExternalTrackMatchService> _logger;

    public ExternalTrackMatchService(
        DeezerClient deezerClient,
        SongLinkResolver songLinkResolver,
        IQobuzMetadataService qobuzMetadataService,
        AppleMusicCatalogService appleCatalogService,
        DeezSpoTagSettingsService settingsService,
        IAcousticIdMatcher acousticIdMatcher,
        ILogger<ExternalTrackMatchService> logger)
    {
        _deezerClient = deezerClient;
        _songLinkResolver = songLinkResolver;
        _qobuzMetadataService = qobuzMetadataService;
        _appleCatalogService = appleCatalogService;
        _settingsService = settingsService;
        _acousticIdMatcher = acousticIdMatcher;
        _logger = logger;
    }

    public async Task<ExternalMatchResult> MatchSpotifyToDeezerAsync(
        ExternalMatchRequest request,
        CancellationToken cancellationToken)
    {
        var spotifyTrack = request.SpotifyTrack;
        if (spotifyTrack is null)
        {
            return BuildResult(
                deezerTrackId: request.DeezerTrackId,
                matchedBy: string.IsNullOrWhiteSpace(request.DeezerTrackId) ? "missing-spotify" : "provided",
                missingSpotify: true,
                missingDeezer: string.IsNullOrWhiteSpace(request.DeezerTrackId),
                acousticIdScore: null,
                notes: "Spotify track missing.",
                links: EmptyResolvedPlatformLinks);
        }

        if (!string.IsNullOrWhiteSpace(request.DeezerTrackId))
        {
            return BuildResult(
                deezerTrackId: request.DeezerTrackId,
                matchedBy: "provided",
                missingSpotify: false,
                missingDeezer: false,
                acousticIdScore: null,
                notes: "Deezer track id provided.",
                links: EmptyResolvedPlatformLinks);
        }

        var links = EmptyResolvedPlatformLinks;
        var isrc = NormalizeIsrc(spotifyTrack.Isrc);
        if (!string.IsNullOrWhiteSpace(isrc))
        {
            var isrcResolution = await ResolveIsrcMatchesAsync(request, isrc, cancellationToken);
            links = isrcResolution.Links;
            var byIsrc = isrcResolution.DeezerTrackId;
            if (!string.IsNullOrWhiteSpace(byIsrc))
            {
                return BuildResult(
                    deezerTrackId: byIsrc,
                    matchedBy: "isrc",
                    missingSpotify: false,
                    missingDeezer: false,
                    acousticIdScore: null,
                    notes: "Matched by Spotify ISRC.",
                    links: links);
            }
        }

        var acousticMatch = await TryResolveAcousticMatchAsync(request, spotifyTrack, links, cancellationToken);
        if (acousticMatch != null)
        {
            return acousticMatch;
        }

        var resolved = await ResolveByFallbackAsync(request, spotifyTrack, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return BuildResult(
                deezerTrackId: resolved,
                matchedBy: "fallback",
                missingSpotify: false,
                missingDeezer: false,
                acousticIdScore: null,
                notes: "Matched by fallback search or SongLink.",
                links: links);
        }

        return BuildResult(
            deezerTrackId: null,
            matchedBy: "unmatched",
            missingSpotify: false,
            missingDeezer: true,
            acousticIdScore: null,
            notes: "No match found.",
            links: links);
    }

    private async Task<(string? DeezerTrackId, ResolvedPlatformLinks Links)> ResolveIsrcMatchesAsync(
        ExternalMatchRequest request,
        string isrc,
        CancellationToken cancellationToken)
    {
        var deezerTask = TryResolveDeezerByIsrcAsync(isrc);
        var qobuzTask = request.AllowQobuzIsrcLookup
            ? TryResolveQobuzByIsrcAsync(isrc, cancellationToken)
            : Task.FromResult<(string Id, string Url)?>(null);
        var appleTask = request.AllowAppleIsrcLookup
            ? TryResolveAppleByIsrcAsync(isrc, cancellationToken)
            : Task.FromResult<(string Id, string Url)?>(null);

        await Task.WhenAll(deezerTask, qobuzTask, appleTask);

        var qobuzResult = qobuzTask.Result;
        var appleResult = appleTask.Result;
        var links = new ResolvedPlatformLinks(
            QobuzTrackId: qobuzResult?.Id,
            QobuzUrl: qobuzResult?.Url,
            AppleMusicId: appleResult?.Id,
            AppleMusicUrl: appleResult?.Url);
        return (deezerTask.Result, links);
    }

    private async Task<ExternalMatchResult?> TryResolveAcousticMatchAsync(
        ExternalMatchRequest request,
        SpotifyTrackSummary spotifyTrack,
        ResolvedPlatformLinks links,
        CancellationToken cancellationToken)
    {
        if (!request.AllowAcousticId || !_acousticIdMatcher.IsAvailable)
        {
            return null;
        }

        var acoustic = await _acousticIdMatcher.MatchAsync(
            new AcousticIdMatchRequest(
                SpotifyPreviewUrl: request.SpotifyPreviewUrl,
                DeezerPreviewUrl: request.DeezerPreviewUrl,
                ExpectedTitle: spotifyTrack.Name,
                ExpectedArtist: spotifyTrack.Artists,
                ExpectedDurationMs: spotifyTrack.DurationMs),
            cancellationToken);
        if (acoustic?.DeezerTrackId is not { Length: > 0 })
        {
            return null;
        }

        return BuildResult(
            deezerTrackId: acoustic.DeezerTrackId,
            matchedBy: "acousticid",
            missingSpotify: false,
            missingDeezer: false,
            acousticIdScore: acoustic.Score,
            notes: "Matched by AcousticID.",
            links: links);
    }

    private async Task<string?> ResolveByFallbackAsync(
        ExternalMatchRequest request,
        SpotifyTrackSummary spotifyTrack,
        CancellationToken cancellationToken)
    {
        return await SpotifyTracklistResolver.ResolveDeezerTrackIdAsync(
            _deezerClient,
            _songLinkResolver,
            spotifyTrack,
            new SpotifyTrackResolveOptions(
                AllowFallbackSearch: request.AllowFallbackSearch,
                PreferIsrcOnly: request.PreferIsrcOnly,
                UseSongLink: request.AllowSongLink,
                StrictMode: false,
                BypassNegativeCanonicalCache: false,
                Logger: _logger,
                CancellationToken: cancellationToken));
    }

    private static ExternalMatchResult BuildResult(
        string? deezerTrackId,
        string matchedBy,
        bool missingSpotify,
        bool missingDeezer,
        double? acousticIdScore,
        string? notes,
        ResolvedPlatformLinks links)
    {
        return new ExternalMatchResult(
            DeezerTrackId: deezerTrackId,
            MatchedBy: matchedBy,
            MissingSpotify: missingSpotify,
            MissingDeezer: missingDeezer,
            AcousticIdScore: acousticIdScore,
            Notes: notes,
            QobuzTrackId: links.QobuzTrackId,
            QobuzUrl: links.QobuzUrl,
            AppleMusicId: links.AppleMusicId,
            AppleMusicUrl: links.AppleMusicUrl);
    }

    private async Task<string?> TryResolveDeezerByIsrcAsync(string isrc)
    {
        try
        {
            var deezer = await _deezerClient.GetTrackByIsrcAsync(isrc);
            return deezer?.Id?.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Deezer ISRC lookup failed: {Isrc}", isrc);
            return null;
        }
    }

    private static string? NormalizeIsrc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var normalized = value.Replace("-", string.Empty).Trim().ToUpperInvariant();
        return normalized.Length == 12 && normalized.All(char.IsLetterOrDigit)
            ? normalized
            : null;
    }

    private async Task<(string Id, string Url)?> TryResolveQobuzByIsrcAsync(string isrc, CancellationToken cancellationToken)
    {
        try
        {
            var track = await _qobuzMetadataService.FindTrackByISRC(isrc, cancellationToken);
            if (track == null || track.Id <= 0)
            {
                return null;
            }
            return (track.Id.ToString(), $"https://play.qobuz.com/track/{track.Id}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Qobuz ISRC lookup failed: {Isrc}", isrc);
            return null;
        }
    }

    private async Task<(string Id, string Url)?> TryResolveAppleByIsrcAsync(string isrc, CancellationToken cancellationToken)
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic.Storefront) ? "us" : settings.AppleMusic.Storefront;
            using var doc = await _appleCatalogService.GetSongByIsrcAsync(
                isrc,
                storefront,
                "en-US",
                cancellationToken,
                settings.AppleMusic.MediaUserToken);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            foreach (var item in data.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }
                var attributes = item.TryGetProperty("attributes", out var attrs) ? attrs : default;
                var url = attributes.ValueKind == JsonValueKind.Object &&
                          attributes.TryGetProperty("url", out var urlElement)
                    ? urlElement.GetString()
                    : null;
                return (id!, url ?? string.Empty);
            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Apple Music ISRC lookup failed: {Isrc}", isrc);
            return null;
        }
    }
}

public sealed record ExternalMatchRequest(
    SpotifyTrackSummary? SpotifyTrack,
    string? DeezerTrackId = null,
    string? DeezerPreviewUrl = null,
    string? SpotifyPreviewUrl = null,
    bool AllowFallbackSearch = true,
    bool AllowSongLink = true,
    bool PreferIsrcOnly = false,
    bool AllowAcousticId = true,
    bool AllowQobuzIsrcLookup = true,
    bool AllowAppleIsrcLookup = true);

public sealed record ExternalMatchResult(
    string? DeezerTrackId,
    string MatchedBy,
    bool MissingSpotify,
    bool MissingDeezer,
    double? AcousticIdScore,
    string? Notes,
    string? QobuzTrackId,
    string? QobuzUrl,
    string? AppleMusicId,
    string? AppleMusicUrl);

public interface IAcousticIdMatcher
{
    bool IsAvailable { get; }
    Task<AcousticIdMatchResult?> MatchAsync(AcousticIdMatchRequest request, CancellationToken cancellationToken);
}

public sealed record AcousticIdMatchRequest(
    string? SpotifyPreviewUrl,
    string? DeezerPreviewUrl,
    string? ExpectedTitle,
    string? ExpectedArtist,
    int? ExpectedDurationMs);

public sealed record AcousticIdMatchResult(
    string? DeezerTrackId,
    double? Score,
    string? Notes);
