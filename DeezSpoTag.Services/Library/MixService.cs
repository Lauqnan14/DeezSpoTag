namespace DeezSpoTag.Services.Library;

public sealed class MixService
{
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
        return await _repository.GetGeneratedMixCachesAsync(plexUserId, libraryId, cancellationToken);
    }

    public async Task<IReadOnlyList<MixSummaryDto>> GetMixesAsync(
        long plexUserId,
        CancellationToken cancellationToken = default)
    {
        return await _repository.GetGeneratedMixCachesAsync(plexUserId, cancellationToken);
    }

    public async Task<MixDetailDto?> GetMixAsync(
        string mixId,
        long plexUserId,
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        var summary = await _repository.GetGeneratedMixCacheAsync(mixId, plexUserId, libraryId, cancellationToken);
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
}
