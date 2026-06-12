using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Library;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services;

public sealed class DownloadDedupeService
{
    private const string DeezerPlatform = "deezer";
    private const string SpotifyPlatform = "spotify";
    private const string ApplePlatform = "apple";
    private const string IsrcSource = "isrc";
    private readonly DownloadQueueRepository _queueRepository;
    private readonly LibraryRepository _libraryRepository;
    private readonly ILogger<DownloadDedupeService> _logger;

    public DownloadDedupeService(
        DownloadQueueRepository queueRepository,
        LibraryRepository libraryRepository,
        ILogger<DownloadDedupeService> logger)
    {
        _queueRepository = queueRepository;
        _libraryRepository = libraryRepository;
        _logger = logger;
    }

    public async Task<DownloadDedupeDecision> CheckAsync(
        DownloadDedupeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var blockRuleDecision = CheckPlaylistBlockRules(request);
        if (!blockRuleDecision.Allowed)
        {
            return blockRuleDecision;
        }

        var globalBlockDecision = await CheckGlobalBlocklistAsync(request, cancellationToken);
        if (!globalBlockDecision.Allowed)
        {
            return globalBlockDecision;
        }

        var existingQueueItem = await _queueRepository.GetDuplicateAsync(BuildQueueLookup(request), cancellationToken);
        if (existingQueueItem != null)
        {
            return DownloadDedupeDecision.Rejected(
                "queue_duplicate",
                $"Skipped: matching track is already in queue (status={existingQueueItem.Status}).",
                "queue",
                existingQueueItem.QueueUuid);
        }

        var libraryDecision = await CheckLibraryAsync(request, cancellationToken);
        if (!libraryDecision.Allowed)
        {
            return libraryDecision;
        }

        return DownloadDedupeDecision.AllowedDecision;
    }

    public static DownloadDedupeRequest FromQueuePayload(
        EngineQueueItemBase payload,
        int? durationMs,
        int? requestedLocalQualityRank = null,
        string? requestedAudioVariant = null,
        IReadOnlyList<PlaylistTrackBlockRule>? blockRules = null)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return new DownloadDedupeRequest
        {
            Isrc = payload.Isrc,
            DeezerTrackId = payload.DeezerId,
            SpotifyTrackId = payload.SpotifyId,
            AppleTrackId = payload.AppleId,
            TrackTitle = payload.Title,
            TrackArtist = payload.Artist,
            TrackPrimaryArtist = NormalizePrimaryArtist(payload.Artist),
            Album = payload.Album,
            Genres = payload.Genres,
            Explicit = payload.Explicit,
            ReleaseDate = payload.ReleaseDate,
            DurationMs = durationMs,
            DestinationFolderId = payload.DestinationFolderId,
            ContentType = payload.ContentType,
            RequestedAudioVariant = requestedAudioVariant,
            RequestedLocalQualityRank = requestedLocalQualityRank,
            BlockRules = blockRules
        };
    }

    public static DownloadDedupeRequest FromDownloadIntent(
        DownloadIntent intent,
        int? requestedLocalQualityRank = null,
        string? requestedAudioVariant = null,
        IReadOnlyList<PlaylistTrackBlockRule>? blockRules = null)
    {
        ArgumentNullException.ThrowIfNull(intent);

        return new DownloadDedupeRequest
        {
            Isrc = intent.Isrc,
            DeezerTrackId = intent.DeezerId,
            DeezerAlbumId = intent.DeezerAlbumId,
            DeezerArtistId = intent.DeezerArtistId,
            SpotifyTrackId = intent.SpotifyId,
            AppleTrackId = intent.AppleId,
            TrackTitle = intent.Title,
            TrackArtist = intent.Artist,
            TrackPrimaryArtist = NormalizePrimaryArtist(intent.Artist),
            Album = intent.Album,
            Genres = intent.Genres,
            Explicit = intent.Explicit,
            ReleaseDate = intent.ReleaseDate,
            DurationMs = intent.DurationMs > 0 ? intent.DurationMs : null,
            DestinationFolderId = intent.DestinationFolderId,
            ContentType = intent.ContentType,
            RequestedAudioVariant = requestedAudioVariant,
            RequestedLocalQualityRank = requestedLocalQualityRank,
            BlockRules = blockRules
        };
    }

    private DownloadDedupeDecision CheckPlaylistBlockRules(DownloadDedupeRequest request)
    {
        var matchedRule = PlaylistTrackBlockRuleMatcher.FindMatch(
            request.TrackTitle,
            request.TrackArtist,
            request.Album,
            request.Genres,
            request.Explicit,
            request.ReleaseDate,
            request.BlockRules);
        if (matchedRule == null)
        {
            return DownloadDedupeDecision.AllowedDecision;
        }

        var ruleDescription = PlaylistTrackBlockRuleMatcher.Describe(matchedRule);
        return DownloadDedupeDecision.Rejected(
            "blocklist_match",
            $"Skipped: blocked by rule ({ruleDescription}).",
            "blocked-rule");
    }

    private async Task<DownloadDedupeDecision> CheckGlobalBlocklistAsync(
        DownloadDedupeRequest request,
        CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return DownloadDedupeDecision.AllowedDecision;
        }

        try
        {
            var match = await _libraryRepository.FindMatchingDownloadBlocklistAsync(
                request.TrackTitle,
                request.TrackArtist,
                request.Album,
                request.Genres,
                cancellationToken);
            if (match == null)
            {
                return DownloadDedupeDecision.AllowedDecision;
            }

            return DownloadDedupeDecision.Rejected(
                "blocklist_match",
                $"Skipped: blocked by global {match.Field} rule ({match.Value}).",
                "blocked-rule");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Global blocklist check failed; continuing dedupe flow.");
            return DownloadDedupeDecision.AllowedDecision;
        }
    }

    private async Task<DownloadDedupeDecision> CheckLibraryAsync(
        DownloadDedupeRequest request,
        CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return DownloadDedupeDecision.AllowedDecision;
        }

        var localUpgradeEligible = false;
        if (request.RequestedLocalQualityRank.HasValue)
        {
            var bestLocalQualityRank = await _libraryRepository.GetBestLocalQualityRankAsync(
                request.TrackArtist,
                request.TrackTitle,
                request.DurationMs,
                artistPrimaryName: request.TrackPrimaryArtist,
                cancellationToken: cancellationToken);
            if (bestLocalQualityRank.HasValue && request.RequestedLocalQualityRank.Value <= bestLocalQualityRank.Value)
            {
                return DownloadDedupeDecision.Rejected(
                    "library_quality_not_higher",
                    "Skipped: requested quality is not higher than the file already in your library.",
                    "library");
            }

            localUpgradeEligible = bestLocalQualityRank.HasValue && request.RequestedLocalQualityRank.Value > bestLocalQualityRank.Value;
        }

        if (localUpgradeEligible)
        {
            return DownloadDedupeDecision.AllowedDecision;
        }

        var exists = request.DestinationFolderId.HasValue
            ? await ExistsLibraryDuplicateInFolderAsync(request, request.DestinationFolderId.Value, cancellationToken)
            : await ExistsLibraryDuplicateGloballyAsync(request, cancellationToken);
        return exists
            ? DownloadDedupeDecision.Rejected("library_duplicate", "Skipped: matching file already exists in library.", "library")
            : DownloadDedupeDecision.AllowedDecision;
    }

    private async Task<bool> ExistsLibraryDuplicateInFolderAsync(
        DownloadDedupeRequest request,
        long destinationFolderId,
        CancellationToken cancellationToken)
    {
        foreach (var (source, value) in BuildSourceChecks(request))
        {
            if (!string.IsNullOrWhiteSpace(value)
                && await _libraryRepository.ExistsTrackSourceInFolderAsync(
                    source,
                    value,
                    destinationFolderId,
                    audioVariant: request.RequestedAudioVariant,
                    cancellationToken: cancellationToken))
            {
                return true;
            }
        }

        foreach (var (source, albumId, artistId) in BuildAlbumChecks(request))
        {
            if (!string.IsNullOrWhiteSpace(albumId)
                && await _libraryRepository.ExistsTrackByAlbumSourceInFolderAsync(
                    source,
                    albumId,
                    request.TrackTitle,
                    artistId,
                    destinationFolderId,
                    audioVariant: request.RequestedAudioVariant,
                    cancellationToken: cancellationToken))
            {
                return true;
            }
        }

        var metadataArtists = BuildMetadataArtists(request)
            .Where(static artist => !string.IsNullOrWhiteSpace(artist))
            .ToArray();
        for (var index = 0; index < metadataArtists.Length; index++)
        {
            var artist = metadataArtists[index];
            if (await _libraryRepository.ExistsTrackByMetadataInFolderAsync(
                    request.TrackTitle,
                    artist,
                    request.DurationMs,
                    destinationFolderId,
                    audioVariant: request.RequestedAudioVariant,
                    cancellationToken: cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> ExistsLibraryDuplicateGloballyAsync(
        DownloadDedupeRequest request,
        CancellationToken cancellationToken)
    {
        foreach (var (source, value) in BuildSourceChecks(request))
        {
            if (!string.IsNullOrWhiteSpace(value)
                && await _libraryRepository.ExistsTrackSourceAsync(
                    source,
                    value,
                    audioVariant: request.RequestedAudioVariant,
                    cancellationToken: cancellationToken))
            {
                return true;
            }
        }

        foreach (var (source, albumId, artistId) in BuildAlbumChecks(request))
        {
            if (!string.IsNullOrWhiteSpace(albumId)
                && await _libraryRepository.ExistsTrackByAlbumSourceAsync(
                    source,
                    albumId,
                    request.TrackTitle,
                    artistId,
                    audioVariant: request.RequestedAudioVariant,
                    cancellationToken: cancellationToken))
            {
                return true;
            }
        }

        foreach (var artist in BuildMetadataArtists(request))
        {
            var result = await _libraryRepository.ExistsInLibraryAsync(
                new[]
                {
                    new LibraryRepository.LibraryExistenceInput(null, request.TrackTitle, artist, request.DurationMs)
                },
                cancellationToken);
            if (result.Count > 0 && result[0])
            {
                return true;
            }
        }

        return false;
    }

    private static DuplicateLookupRequest BuildQueueLookup(DownloadDedupeRequest request)
        => new()
        {
            Isrc = request.Isrc,
            DeezerTrackId = request.DeezerTrackId,
            DeezerAlbumId = request.DeezerAlbumId,
            DeezerArtistId = request.DeezerArtistId,
            SpotifyTrackId = request.SpotifyTrackId,
            SpotifyAlbumId = request.SpotifyAlbumId,
            SpotifyArtistId = request.SpotifyArtistId,
            AppleTrackId = request.AppleTrackId,
            AppleAlbumId = request.AppleAlbumId,
            AppleArtistId = request.AppleArtistId,
            ArtistName = request.TrackArtist,
            TrackTitle = request.TrackTitle,
            DurationMs = request.DurationMs,
            DestinationFolderId = request.DestinationFolderId,
            ContentType = request.ContentType,
            ArtistPrimaryName = request.TrackPrimaryArtist
        };

    private static IEnumerable<(string Source, string? Value)> BuildSourceChecks(DownloadDedupeRequest request)
    {
        yield return (IsrcSource, request.Isrc);
        yield return (DeezerPlatform, request.DeezerTrackId);
        yield return (SpotifyPlatform, request.SpotifyTrackId);
        yield return (ApplePlatform, request.AppleTrackId);
    }

    private static IEnumerable<(string Source, string? AlbumId, string? ArtistId)> BuildAlbumChecks(DownloadDedupeRequest request)
    {
        yield return (DeezerPlatform, request.DeezerAlbumId, request.DeezerArtistId);
        yield return (SpotifyPlatform, request.SpotifyAlbumId, request.SpotifyArtistId);
        yield return (ApplePlatform, request.AppleAlbumId, request.AppleArtistId);
    }

    private static IReadOnlyList<string> BuildMetadataArtists(DownloadDedupeRequest request)
        => new[]
        {
            request.TrackArtist,
            request.TrackPrimaryArtist
        }
        .Where(static artist => !string.IsNullOrWhiteSpace(artist))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(static artist => artist!)
        .ToList();

    private static string? NormalizePrimaryArtist(string? artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        var primary = ArtistNameNormalizer.ExtractPrimaryArtist(artistName);
        if (string.IsNullOrWhiteSpace(primary))
        {
            return null;
        }

        return string.Equals(primary, artistName.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : primary;
    }
}

public sealed class DownloadDedupeRequest
{
    public string? Isrc { get; init; }
    public string? DeezerTrackId { get; init; }
    public string? DeezerAlbumId { get; init; }
    public string? DeezerArtistId { get; init; }
    public string? SpotifyTrackId { get; init; }
    public string? SpotifyAlbumId { get; init; }
    public string? SpotifyArtistId { get; init; }
    public string? AppleTrackId { get; init; }
    public string? AppleAlbumId { get; init; }
    public string? AppleArtistId { get; init; }
    public string TrackTitle { get; init; } = string.Empty;
    public string TrackArtist { get; init; } = string.Empty;
    public string? TrackPrimaryArtist { get; init; }
    public string? Album { get; init; }
    public IReadOnlyList<string>? Genres { get; init; }
    public bool? Explicit { get; init; }
    public string? ReleaseDate { get; init; }
    public int? DurationMs { get; init; }
    public long? DestinationFolderId { get; init; }
    public string? ContentType { get; init; }
    public string? RequestedAudioVariant { get; init; }
    public int? RequestedLocalQualityRank { get; init; }
    public IReadOnlyList<PlaylistTrackBlockRule>? BlockRules { get; init; }
}

public sealed record DownloadDedupeDecision(
    bool Allowed,
    string? ReasonCode,
    string? Message,
    string? Source,
    string? QueueUuid)
{
    public static DownloadDedupeDecision AllowedDecision { get; } = new(true, null, null, null, null);

    public static DownloadDedupeDecision Rejected(
        string reasonCode,
        string message,
        string source,
        string? queueUuid = null)
        => new(false, reasonCode, message, source, queueUuid);
}
