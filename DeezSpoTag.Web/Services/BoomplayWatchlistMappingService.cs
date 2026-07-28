using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed record BoomplayWatchlistTrackInput(
    string BoomplayTrackId,
    string? SourceUrl,
    string? Title,
    string? Artist,
    string? Album,
    string? Isrc,
    int? DurationMs,
    string? CoverUrl);

public sealed record BoomplayWatchlistMappedTrack(
    string BoomplayTrackId,
    string? DeezerTrackId,
    string? Isrc,
    string Title,
    string Artist,
    string Album,
    int? DurationMs,
    string? CoverUrl,
    string MappingStatus,
    string? MappingError)
{
    public bool IsMatched => string.Equals(MappingStatus, "matched", StringComparison.Ordinal)
                             && !string.IsNullOrWhiteSpace(DeezerTrackId);
}

public sealed class BoomplayWatchlistMappingService
{
    internal const string MatcherVersion = "boomplay-deezer-watchlist-v1";
    internal const string MatchedStatus = "matched";
    internal const string MappingRetryStatus = "mapping_retry";
    private static readonly TimeSpan MappingRetryDelay = TimeSpan.FromHours(24);
    private static readonly Dictionary<string, TrackResolutionGate> TrackResolutionLocks =
        new(StringComparer.Ordinal);
    private static readonly object TrackResolutionLocksGate = new();

    private readonly LibraryRepository _repository;
    private readonly Func<BoomplayDeezerMatchRequest, CancellationToken, Task<BoomplayDeezerMatchResult?>> _resolver;
    private readonly ILogger<BoomplayWatchlistMappingService> _logger;

    public BoomplayWatchlistMappingService(
        LibraryRepository repository,
        BoomplayDeezerMatchService matchService,
        ILogger<BoomplayWatchlistMappingService> logger)
        : this(
            repository,
            (request, cancellationToken) => matchService.ResolveAsync(request, cancellationToken, includeMeta: true),
            logger)
    {
    }

    internal BoomplayWatchlistMappingService(
        LibraryRepository repository,
        Func<BoomplayDeezerMatchRequest, CancellationToken, Task<BoomplayDeezerMatchResult?>> resolver,
        ILogger<BoomplayWatchlistMappingService> logger)
    {
        _repository = repository;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BoomplayWatchlistMappedTrack>> ResolveTracksAsync(
        IReadOnlyCollection<BoomplayWatchlistTrackInput> tracks,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return [];
        }

        var persistedMappings = await _repository.GetBoomplayDeezerTrackMappingsAsync(
            tracks.Select(static track => track.BoomplayTrackId),
            cancellationToken);
        var resolved = new List<BoomplayWatchlistMappedTrack>(tracks.Count);
        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            persistedMappings.TryGetValue(track.BoomplayTrackId?.Trim() ?? string.Empty, out var persisted);
            resolved.Add(await ResolveTrackAsync(track, persisted, cancellationToken));
        }

        return resolved;
    }

    private async Task<BoomplayWatchlistMappedTrack> ResolveTrackAsync(
        BoomplayWatchlistTrackInput track,
        BoomplayDeezerTrackMappingDto? persisted,
        CancellationToken cancellationToken)
    {
        var normalizedTrackId = track.BoomplayTrackId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTrackId))
        {
            return BuildRetryResult(track, "Boomplay track id is missing.");
        }

        var normalizedTrack = track with { BoomplayTrackId = normalizedTrackId };
        var fingerprint = BuildSourceFingerprint(normalizedTrack);
        if (CanReuseMatchedMapping(persisted))
        {
            return BuildMappedResult(persisted!);
        }

        if (CanReusePendingRetry(persisted, fingerprint, DateTimeOffset.UtcNow))
        {
            return BuildMappedResult(persisted!);
        }

        var resolutionGate = RentTrackResolutionGate(normalizedTrackId);
        var acquiredResolutionGate = false;
        try
        {
            await resolutionGate.Semaphore.WaitAsync(cancellationToken);
            acquiredResolutionGate = true;
            persisted = await _repository.GetBoomplayDeezerTrackMappingAsync(normalizedTrackId, cancellationToken);
            if (CanReuseMatchedMapping(persisted)
                || CanReusePendingRetry(persisted, fingerprint, DateTimeOffset.UtcNow))
            {
                return BuildMappedResult(persisted!);
            }

            var match = await _resolver(
                new BoomplayDeezerMatchRequest(
                    normalizedTrack.SourceUrl,
                    normalizedTrack.Title,
                    normalizedTrack.Artist,
                    normalizedTrack.Album,
                    normalizedTrack.Isrc,
                    normalizedTrack.DurationMs),
                cancellationToken);
            if (match != null && !string.IsNullOrWhiteSpace(match.DeezerId))
            {
                var mapped = BuildMatchedResult(normalizedTrack, match);
                await PersistMatchedAsync(mapped, fingerprint, cancellationToken);
                return mapped;
            }

            return await PreserveMatchOrScheduleRetryAsync(
                normalizedTrack,
                persisted,
                fingerprint,
                "No validated Deezer match was found.",
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(
                ex,
                "Boomplay Watchlist mapping failed for track {BoomplayTrackId}; retaining any verified Deezer mapping.",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedTrackId));
            return await PreserveMatchOrScheduleRetryAsync(
                normalizedTrack,
                persisted,
                fingerprint,
                ex.Message,
                cancellationToken);
        }
        finally
        {
            if (acquiredResolutionGate)
            {
                resolutionGate.Semaphore.Release();
            }
            ReturnTrackResolutionGate(normalizedTrackId, resolutionGate);
        }
    }

    private static TrackResolutionGate RentTrackResolutionGate(string trackId)
    {
        lock (TrackResolutionLocksGate)
        {
            if (!TrackResolutionLocks.TryGetValue(trackId, out var gate))
            {
                gate = new TrackResolutionGate();
                TrackResolutionLocks[trackId] = gate;
            }

            gate.Users++;
            return gate;
        }
    }

    private static void ReturnTrackResolutionGate(string trackId, TrackResolutionGate gate)
    {
        lock (TrackResolutionLocksGate)
        {
            gate.Users--;
            if (gate.Users == 0
                && TrackResolutionLocks.TryGetValue(trackId, out var current)
                && ReferenceEquals(current, gate))
            {
                TrackResolutionLocks.Remove(trackId);
                gate.Semaphore.Dispose();
            }
        }
    }

    private async Task<BoomplayWatchlistMappedTrack> PreserveMatchOrScheduleRetryAsync(
        BoomplayWatchlistTrackInput track,
        BoomplayDeezerTrackMappingDto? persisted,
        string fingerprint,
        string error,
        CancellationToken cancellationToken)
    {
        if (IsMatchedMapping(persisted))
        {
            return BuildMappedResult(persisted!);
        }

        var retry = BuildRetryResult(track, error);
        await _repository.UpsertBoomplayDeezerTrackMappingAsync(
            new LibraryRepository.BoomplayDeezerTrackMappingUpsertInput(
                retry.BoomplayTrackId,
                DeezerTrackId: null,
                retry.Isrc,
                retry.Title,
                retry.Artist,
                retry.Album,
                retry.CoverUrl,
                retry.DurationMs,
                fingerprint,
                MatcherVersion,
                MappingRetryStatus,
                error,
                DateTimeOffset.UtcNow + MappingRetryDelay),
            cancellationToken);
        return retry;
    }

    private async Task PersistMatchedAsync(
        BoomplayWatchlistMappedTrack mapped,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        await _repository.UpsertBoomplayDeezerTrackMappingAsync(
            new LibraryRepository.BoomplayDeezerTrackMappingUpsertInput(
                mapped.BoomplayTrackId,
                mapped.DeezerTrackId,
                mapped.Isrc,
                mapped.Title,
                mapped.Artist,
                mapped.Album,
                mapped.CoverUrl,
                mapped.DurationMs,
                fingerprint,
                MatcherVersion,
                MatchedStatus,
                LastError: null,
                NextRetryUtc: null),
            cancellationToken);
    }

    private static bool CanReuseMatchedMapping(BoomplayDeezerTrackMappingDto? persisted)
        => IsMatchedMapping(persisted)
           && string.Equals(persisted!.MatcherVersion, MatcherVersion, StringComparison.Ordinal);

    private static bool CanReusePendingRetry(
        BoomplayDeezerTrackMappingDto? persisted,
        string fingerprint,
        DateTimeOffset nowUtc)
        => persisted != null
           && string.Equals(persisted.Status, MappingRetryStatus, StringComparison.OrdinalIgnoreCase)
           && string.Equals(persisted.MatcherVersion, MatcherVersion, StringComparison.Ordinal)
           && string.Equals(persisted.SourceFingerprint, fingerprint, StringComparison.Ordinal)
           && persisted.NextRetryUtc > nowUtc;

    private static bool IsMatchedMapping(BoomplayDeezerTrackMappingDto? persisted)
        => persisted != null
           && string.Equals(persisted.Status, MatchedStatus, StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(persisted.DeezerTrackId);

    private static BoomplayWatchlistMappedTrack BuildMatchedResult(
        BoomplayWatchlistTrackInput source,
        BoomplayDeezerMatchResult match)
        => new(
            source.BoomplayTrackId,
            match.DeezerId.Trim(),
            FirstNonEmpty(match.Isrc, source.Isrc),
            FirstNonEmpty(match.Title, source.Title) ?? string.Empty,
            FirstNonEmpty(match.Artist, source.Artist) ?? string.Empty,
            FirstNonEmpty(match.Album, source.Album) ?? string.Empty,
            match.DurationMs is > 0 ? match.DurationMs : source.DurationMs,
            FirstNonEmpty(match.CoverMedium, source.CoverUrl),
            MatchedStatus,
            MappingError: null);

    private sealed class TrackResolutionGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private static BoomplayWatchlistMappedTrack BuildMappedResult(BoomplayDeezerTrackMappingDto mapping)
        => new(
            mapping.BoomplayTrackId,
            mapping.DeezerTrackId,
            mapping.Isrc,
            mapping.Title,
            mapping.Artist,
            mapping.Album,
            mapping.DurationMs,
            mapping.CoverUrl,
            mapping.Status,
            mapping.LastError);

    private static BoomplayWatchlistMappedTrack BuildRetryResult(BoomplayWatchlistTrackInput track, string error)
        => new(
            track.BoomplayTrackId?.Trim() ?? string.Empty,
            DeezerTrackId: null,
            string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc.Trim(),
            track.Title?.Trim() ?? string.Empty,
            track.Artist?.Trim() ?? string.Empty,
            track.Album?.Trim() ?? string.Empty,
            track.DurationMs is > 0 ? track.DurationMs : null,
            string.IsNullOrWhiteSpace(track.CoverUrl) ? null : track.CoverUrl.Trim(),
            MappingRetryStatus,
            error);

    internal static string BuildSourceFingerprint(BoomplayWatchlistTrackInput track)
    {
        var payload = JsonSerializer.Serialize(new
        {
            boomplayTrackId = NormalizeFingerprintValue(track.BoomplayTrackId),
            isrc = NormalizeFingerprintValue(track.Isrc),
            title = NormalizeFingerprintValue(track.Title),
            artist = NormalizeFingerprintValue(track.Artist)
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string NormalizeFingerprintValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
