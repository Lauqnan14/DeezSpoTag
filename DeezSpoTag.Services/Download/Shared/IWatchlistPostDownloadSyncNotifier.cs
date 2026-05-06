namespace DeezSpoTag.Services.Download.Shared;

public interface IWatchlistPostDownloadSyncNotifier
{
    ValueTask NotifyCompletedAsync(
        string source,
        string playlistId,
        string trackId,
        long? destinationFolderId,
        CancellationToken cancellationToken = default);
}
