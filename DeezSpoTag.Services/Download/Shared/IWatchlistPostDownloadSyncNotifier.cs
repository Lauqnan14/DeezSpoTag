namespace DeezSpoTag.Services.Download.Shared;

public interface IWatchlistPostDownloadSyncNotifier
{
    ValueTask RequestAllPlaylistSyncAsync(CancellationToken cancellationToken = default);
}
