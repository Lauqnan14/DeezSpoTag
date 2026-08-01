namespace DeezSpoTag.Services.Download.Shared;

public interface IWatchlistPostDownloadSyncNotifier
{
    ValueTask RequestPlaylistSyncAsync(
        string source,
        string playlistId,
        CancellationToken cancellationToken = default);
}
