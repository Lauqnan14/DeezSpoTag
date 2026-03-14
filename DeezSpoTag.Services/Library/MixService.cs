namespace DeezSpoTag.Services.Library;

public sealed class MixService
{
    private const int TrackLimit = 20;
    private const string TopTracksMixId = "top-tracks";
    private const string RediscoverMixId = "rediscover";
    private const string LibraryShuffleMixId = "library-shuffle";
    private static readonly TimeSpan CacheWindow = TimeSpan.FromHours(24);
    private readonly LibraryRepository _repository;

    public MixService(LibraryRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<MixSummaryDto>> GetMixesAsync(
        long plexUserId,
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        var mixes = new List<MixSummaryDto>();
        var definitions = new[]
        {
            new MixDefinition(TopTracksMixId, "Top Tracks", "Your most played tracks."),
            new MixDefinition(RediscoverMixId, "Rediscover", "Tracks you have not played recently."),
            new MixDefinition(LibraryShuffleMixId, "Library Shuffle", "A random sample from your library.")
        };

        foreach (var definition in definitions)
        {
            var mix = await GetOrCreateMixAsync(definition, plexUserId, libraryId, cancellationToken);
            if (mix is not null)
            {
                mixes.Add(mix);
            }
        }

        return mixes;
    }

    public async Task<MixDetailDto?> GetMixAsync(
        string mixId,
        long plexUserId,
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        var definition = GetDefinition(mixId);
        if (definition is null)
        {
            return null;
        }

        var summary = await GetOrCreateMixAsync(definition, plexUserId, libraryId, cancellationToken);
        if (summary is null)
        {
            return null;
        }

        var mixCacheId = await _repository.GetMixCacheIdAsync(mixId, plexUserId, libraryId, cancellationToken);
        if (mixCacheId is null)
        {
            return null;
        }

        var tracks = await _repository.GetMixTracksAsync(mixCacheId.Value, cancellationToken);
        return new MixDetailDto(summary, tracks);
    }

    private async Task<MixSummaryDto?> GetOrCreateMixAsync(
        MixDefinition definition,
        long plexUserId,
        long libraryId,
        CancellationToken cancellationToken)
    {
        var cached = await _repository.GetMixCacheAsync(definition.Id, plexUserId, libraryId, cancellationToken);
        if (cached is not null && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            return cached;
        }

        var trackIds = await ResolveTrackIdsAsync(definition.Id, plexUserId, libraryId, cancellationToken);
        if (trackIds.Count == 0)
        {
            return null;
        }

        var coverUrls = await _repository.GetCoverPathsAsync(trackIds, 4, cancellationToken);
        var generatedAt = DateTimeOffset.UtcNow;
        var expiresAt = generatedAt.Add(CacheWindow);
        var mixCacheId = await _repository.UpsertMixCacheAsync(
            new LibraryRepository.MixCacheUpsertInput(
                definition.Id,
                plexUserId,
                libraryId,
                definition.Name,
                definition.Description,
                coverUrls,
                trackIds.Count,
                generatedAt,
                expiresAt),
            cancellationToken);
        await _repository.ReplaceMixItemsAsync(mixCacheId, trackIds, cancellationToken);

        return new MixSummaryDto(
            definition.Id,
            definition.Name,
            definition.Description,
            trackIds.Count,
            coverUrls,
            generatedAt,
            expiresAt,
            libraryId);
    }

    private async Task<IReadOnlyList<long>> ResolveTrackIdsAsync(
        string mixId,
        long plexUserId,
        long libraryId,
        CancellationToken cancellationToken)
    {
        return mixId switch
        {
            TopTracksMixId => await _repository.GetTopTrackIdsAsync(plexUserId, libraryId, TrackLimit, cancellationToken),
            RediscoverMixId => await _repository.GetRediscoverTrackIdsAsync(plexUserId, libraryId, TrackLimit, cancellationToken),
            LibraryShuffleMixId => await _repository.GetRandomTrackIdsAsync(libraryId, TrackLimit, cancellationToken),
            _ => Array.Empty<long>()
        };
    }

    private static MixDefinition? GetDefinition(string mixId)
    {
        return mixId switch
        {
            TopTracksMixId => new MixDefinition(TopTracksMixId, "Top Tracks", "Your most played tracks."),
            RediscoverMixId => new MixDefinition(RediscoverMixId, "Rediscover", "Tracks you have not played recently."),
            LibraryShuffleMixId => new MixDefinition(LibraryShuffleMixId, "Library Shuffle", "A random sample from your library."),
            _ => null
        };
    }

    private sealed record MixDefinition(string Id, string Name, string Description);
}
