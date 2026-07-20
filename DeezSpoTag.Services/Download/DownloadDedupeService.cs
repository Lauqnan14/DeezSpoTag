using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Library;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download;

public sealed class DownloadDedupeService
{
    private const string DeezerPlatform = "deezer";
    private const string SpotifyPlatform = "spotify";
    private const string ApplePlatform = "apple";
    private const string QobuzPlatform = "qobuz";
    private const string TidalPlatform = "tidal";
    private const string AmazonPlatform = "amazon";
    private const string AmazonMusicPlatform = "amazonmusic";
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

    public bool IsLibraryConfigured => _libraryRepository.IsConfigured;

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

    public async Task<DownloadDedupeDecision> CheckLibraryPresenceAsync(
        DownloadDedupeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_libraryRepository.IsConfigured)
        {
            return DownloadDedupeDecision.AllowedDecision;
        }

        // Destination routing must not narrow duplicate detection. The local
        // library identity resolver is the single source of truth used by
        // playlist sync and download dedupe, so both paths agree on whether a
        // monitored track is already available locally.
        var identity = await ResolveLocalLibraryIdentityAsync(request, cancellationToken);
        return identity.Exists
            ? DownloadDedupeDecision.Rejected("library_duplicate", "Skipped: matching file already exists in library.", "library")
            : DownloadDedupeDecision.AllowedDecision;
    }

    public static Task<DownloadDedupeDecision> CheckFinalDestinationAsync(
        DownloadDedupeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.FinalOutputPath) || !File.Exists(request.FinalOutputPath))
        {
            return Task.FromResult(DownloadDedupeDecision.AllowedDecision);
        }

        var existingLocalQualityRank = InferExistingFileLocalQualityRank(request.FinalOutputPath);
        if (IsLossyToLosslessUpgrade(request.RequestedLocalQualityRank, existingLocalQualityRank))
        {
            return Task.FromResult(DownloadDedupeDecision.AllowedDecision);
        }

        var reasonCode = request.RequestedLocalQualityRank.HasValue
            ? "final_destination_quality_not_higher"
            : "final_destination_duplicate";
        return Task.FromResult(DownloadDedupeDecision.Rejected(
            reasonCode,
            $"Skipped before download: final destination already contains '{request.FinalOutputPath}' and the requested quality is not higher.",
            "final-destination"));
    }

    private static bool IsLossyToLosslessUpgrade(int? requestedLocalQualityRank, int? existingLocalQualityRank)
        => requestedLocalQualityRank >= 3 && existingLocalQualityRank is > 0 and < 3;

    public static DownloadDedupeRequest FromQueuePayload(
        EngineQueueItemBase payload,
        int? durationMs,
        int? requestedLocalQualityRank = null,
        string? requestedAudioVariant = null,
        IReadOnlyList<PlaylistTrackBlockRule>? blockRules = null,
        string? finalOutputPath = null)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return new DownloadDedupeRequest
        {
            Isrc = payload.Isrc,
            DeezerTrackId = payload.DeezerId,
            SpotifyTrackId = payload.SpotifyId,
            AppleTrackId = payload.AppleId,
            QobuzTrackId = ResolvePayloadSourceId(payload, QobuzPlatform, "QobuzId"),
            TidalTrackId = ResolvePayloadSourceId(payload, TidalPlatform, "TidalId"),
            AmazonTrackId = ResolvePayloadSourceId(payload, AmazonPlatform, "AmazonId"),
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
            FinalOutputPath = finalOutputPath,
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
            QobuzTrackId = ResolveIntentSourceId(intent, QobuzPlatform),
            TidalTrackId = ResolveIntentSourceId(intent, TidalPlatform),
            AmazonTrackId = ResolveIntentSourceId(intent, AmazonPlatform),
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

    public static DownloadDedupeRequest FromEngineDownloadRequest(
        EngineDownloadRequestBase request,
        string finalOutputPath)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new DownloadDedupeRequest
        {
            Isrc = request.Isrc,
            SpotifyTrackId = request.SpotifyId,
            TrackTitle = request.TrackName,
            TrackArtist = request.ArtistName,
            TrackPrimaryArtist = NormalizePrimaryArtist(request.ArtistName),
            Album = request.AlbumName,
            ReleaseDate = request.ReleaseDate,
            DurationMs = request.DurationSeconds > 0 ? request.DurationSeconds * 1000 : null,
            RequestedLocalQualityRank = request.RequestedLocalQualityRank,
            FinalOutputPath = finalOutputPath
        };
    }

    private static DownloadDedupeDecision CheckPlaylistBlockRules(DownloadDedupeRequest request)
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

        if (request.RequestedLocalQualityRank.HasValue)
        {
            var identity = await ResolveLocalLibraryIdentityAsync(request, cancellationToken);
            var bestLocalQualityRank = identity.LocalTrackId.HasValue
                ? await _libraryRepository.GetBestLocalQualityRankForTrackAsync(
                    identity.LocalTrackId.Value,
                    audioVariant: request.RequestedAudioVariant,
                    cancellationToken: cancellationToken)
                : identity.BestQualityRank;
            if (bestLocalQualityRank.HasValue && request.RequestedLocalQualityRank.Value <= bestLocalQualityRank.Value)
            {
                return DownloadDedupeDecision.Rejected(
                    "library_quality_not_higher",
                    "Skipped: requested quality is not higher than the file already in your library.",
                    "library");
            }

            if (bestLocalQualityRank.HasValue && request.RequestedLocalQualityRank.Value > bestLocalQualityRank.Value)
            {
                return DownloadDedupeDecision.AllowedDecision;
            }
        }

        return await CheckLibraryPresenceAsync(request, cancellationToken);
    }

    private static int? InferExistingFileLocalQualityRank(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            using var tagFile = global::TagLib.File.Create(path);
            var properties = tagFile.Properties;
            var bitsPerSample = properties.BitsPerSample;
            var sampleRate = properties.AudioSampleRate;
            var bitrate = properties.AudioBitrate;
            var codecText = string.Join(' ', properties.Codecs.Select(codec => codec.Description));
            var extensionRank = InferLocalQualityRankFromExtension(path);
            return InferLocalQualityRankFromAudioProperties(bitsPerSample, sampleRate, bitrate, codecText)
                ?? extensionRank;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return InferLocalQualityRankFromExtension(path);
        }
    }

    private static int? InferLocalQualityRankFromAudioProperties(
        int bitsPerSample,
        int sampleRate,
        int bitrate,
        string codecText)
    {
        if (bitsPerSample >= 24 || sampleRate > 48000)
        {
            return 4;
        }

        if (bitsPerSample >= 16
            || codecText.Contains("flac", StringComparison.OrdinalIgnoreCase)
            || codecText.Contains("alac", StringComparison.OrdinalIgnoreCase)
            || codecText.Contains("lossless", StringComparison.OrdinalIgnoreCase)
            || codecText.Contains("pcm", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (bitrate >= 256)
        {
            return 2;
        }

        return bitrate > 0 ? 1 : null;
    }

    private static int? InferLocalQualityRankFromExtension(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".flac" or ".alac" or ".wav" or ".aiff" or ".aif" => 3,
            ".mp3" or ".m4a" or ".aac" or ".ogg" or ".opus" => 2,
            _ => null
        };
    }

    private async Task<LibraryRepository.LocalTrackIdentityResult> ResolveLocalLibraryIdentityAsync(
        DownloadDedupeRequest request,
        CancellationToken cancellationToken)
    {
        return await _libraryRepository.ResolveLocalTrackIdentityAsync(
            new LibraryRepository.LibraryExistenceInput(
                request.Isrc,
                request.TrackTitle,
                request.TrackArtist,
                request.DurationMs,
                ResolvePrimarySource(request),
                ResolvePrimarySourceId(request),
                request.Album),
            audioVariant: request.RequestedAudioVariant,
            cancellationToken: cancellationToken);
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
            QobuzTrackId = request.QobuzTrackId,
            TidalTrackId = request.TidalTrackId,
            AmazonTrackId = request.AmazonTrackId,
            ArtistName = request.TrackArtist,
            TrackTitle = request.TrackTitle,
            DurationMs = request.DurationMs,
            DestinationFolderId = request.DestinationFolderId,
            ContentType = request.ContentType,
            ArtistPrimaryName = request.TrackPrimaryArtist
        };

    private static string? ResolvePrimarySource(DownloadDedupeRequest request)
    {
        var (source, _) = ResolvePrimarySourcePair(request);
        return source;
    }

    private static string? ResolvePrimarySourceId(DownloadDedupeRequest request)
    {
        var (_, sourceId) = ResolvePrimarySourcePair(request);
        return sourceId;
    }

    private static (string? Source, string? SourceId) ResolvePrimarySourcePair(DownloadDedupeRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.DeezerTrackId))
        {
            return (DeezerPlatform, request.DeezerTrackId);
        }

        if (!string.IsNullOrWhiteSpace(request.SpotifyTrackId))
        {
            return (SpotifyPlatform, request.SpotifyTrackId);
        }

        if (!string.IsNullOrWhiteSpace(request.AppleTrackId))
        {
            return (ApplePlatform, request.AppleTrackId);
        }

        if (!string.IsNullOrWhiteSpace(request.QobuzTrackId))
        {
            return (QobuzPlatform, request.QobuzTrackId);
        }

        if (!string.IsNullOrWhiteSpace(request.TidalTrackId))
        {
            return (TidalPlatform, request.TidalTrackId);
        }

        if (!string.IsNullOrWhiteSpace(request.AmazonTrackId))
        {
            return (AmazonPlatform, request.AmazonTrackId);
        }

        return (null, null);
    }

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

    private static string? ResolvePayloadSourceId(EngineQueueItemBase payload, string source, string propertyName)
    {
        return FirstNonEmpty(
            ReadStringProperty(payload, propertyName),
            ExtractSourceTrackId(payload.SourceUrl, source),
            ExtractSourceTrackId(payload.Url, source));
    }

    private static string? ResolveIntentSourceId(DownloadIntent intent, string source)
    {
        return FirstNonEmpty(
            ReadStringProperty(intent, ResolveIntentSourcePropertyName(source)),
            ExtractSourceTrackId(intent.SourceUrl, source),
            ExtractSourceTrackId(intent.Url, source));
    }

    private static string ResolveIntentSourcePropertyName(string source)
        => source.ToLowerInvariant() switch
        {
            QobuzPlatform => "QobuzId",
            TidalPlatform => "TidalId",
            AmazonPlatform or AmazonMusicPlatform => "AmazonId",
            _ => string.Empty
        };

    private static string? ReadStringProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        return property?.GetValue(instance) switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text.Trim(),
            int value when value > 0 => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long value when value > 0 => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null
        };
    }

    private static string? ExtractSourceTrackId(string? value, string source)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim();
        if (string.Equals(source, QobuzPlatform, StringComparison.OrdinalIgnoreCase)
            && long.TryParse(candidate, out var qobuzRawId)
            && qobuzRawId > 0)
        {
            return candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (!SourceHostMatches(host, source))
        {
            return null;
        }

        var queryId = ExtractQueryParameter(uri.Query, "id")
            ?? ExtractQueryParameter(uri.Query, "track_id")
            ?? ExtractQueryParameter(uri.Query, "trackId");
        if (!string.IsNullOrWhiteSpace(queryId))
        {
            return queryId;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Reverse();
        foreach (var segment in segments)
        {
            if (IsSourcePathMarker(segment))
            {
                continue;
            }

            return segment;
        }

        return null;
    }

    private static string? ExtractQueryParameter(string query, string name)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var trimmedQuery = query[0] == '?' ? query[1..] : query;
        foreach (var pair in trimmedQuery.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = pair.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separatorIndex]);
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
        }

        return null;
    }

    private static bool SourceHostMatches(string host, string source)
    {
        return source.ToLowerInvariant() switch
        {
            QobuzPlatform => host.Contains("qobuz", StringComparison.Ordinal),
            TidalPlatform => host.Contains("tidal", StringComparison.Ordinal),
            AmazonPlatform => host.Contains("amazon", StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool IsSourcePathMarker(string segment)
    {
        return segment.Equals("track", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("tracks", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("album", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("albums", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("music", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .FirstOrDefault();
    }
}

public sealed class DownloadDedupeRequest : DownloadIdentityLookupRequest
{
    public string TrackArtist { get; init; } = string.Empty;
    public string? TrackPrimaryArtist { get; init; }
    public string? Album { get; init; }
    public IReadOnlyList<string>? Genres { get; init; }
    public bool? Explicit { get; init; }
    public string? ReleaseDate { get; init; }
    public string? RequestedAudioVariant { get; init; }
    public int? RequestedLocalQualityRank { get; init; }
    public string? FinalOutputPath { get; init; }
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
