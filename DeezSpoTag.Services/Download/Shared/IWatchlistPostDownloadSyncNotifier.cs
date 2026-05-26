namespace DeezSpoTag.Services.Download.Shared;

public interface IWatchlistPostDownloadSyncNotifier
{
    ValueTask NotifyFinalizedAsync(
        string source,
        string playlistId,
        string trackId,
        long? destinationFolderId,
        IReadOnlyList<string>? finalFilePaths = null,
        CancellationToken cancellationToken = default);
}
