using DeezSpoTag.Core.Models.Soulseek;

namespace DeezSpoTag.Integrations.Soulseek;

/// <summary>
///     A thin, mechanical client over the <c>slskd</c> <c>/api/v0</c> HTTP API.
/// </summary>
/// <remarks>
///     <para>
///         This layer translates HTTP into typed results and nothing more. It never decides whether a
///         search result is the right track, never maps quality, and never chooses a destination path.
///         Those decisions belong to <c>DeezSpoTag.Services</c>.
///     </para>
///     <para>
///         Every call takes <see cref="SlskdCredentials"/> explicitly so the adapter stays free of any
///         dependency on how credentials are stored.
///     </para>
/// </remarks>
public interface ISlskdClient
{
    /// <summary>Reads the slskd server state, including Soulseek connection and login status.</summary>
    Task<SlskdServerState> GetServerStateAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>Asks slskd to connect and log in to Soulseek.</summary>
    Task<SlskdServerState> ConnectAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>Asks slskd to disconnect from Soulseek.</summary>
    Task DisconnectAsync(SlskdCredentials credentials, string? message = null, CancellationToken cancellationToken = default);

    /// <summary>Starts a Soulseek search.</summary>
    Task<SlskdSearch> StartSearchAsync(SlskdCredentials credentials, SlskdSearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads a single search by id.</summary>
    Task<SlskdSearch?> GetSearchAsync(SlskdCredentials credentials, Guid searchId, bool includeResponses = false, CancellationToken cancellationToken = default);

    /// <summary>Lists the searches slskd currently knows about.</summary>
    Task<IReadOnlyList<SlskdSearch>> ListSearchesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>Reads the peer responses for a search.</summary>
    Task<IReadOnlyList<SlskdSearchResponse>> GetSearchResponsesAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default);

    /// <summary>Cancels a running search.</summary>
    Task CancelSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default);

    /// <summary>Deletes a search from slskd.</summary>
    Task<bool> DeleteSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default);

    /// <summary>Reads a peer's advertised status.</summary>
    Task<SlskdUserStatus?> GetUserStatusAsync(SlskdCredentials credentials, string username, CancellationToken cancellationToken = default);

    /// <summary>Browses a peer's share root.</summary>
    Task<IReadOnlyList<SlskdDirectory>> BrowseUserAsync(SlskdCredentials credentials, string username, CancellationToken cancellationToken = default);

    /// <summary>Lists a specific directory within a peer's share.</summary>
    Task<IReadOnlyList<SlskdDirectory>> BrowseUserDirectoryAsync(SlskdCredentials credentials, string username, string? directory, CancellationToken cancellationToken = default);

    /// <summary>Enqueues downloads from a single peer.</summary>
    Task<IReadOnlyList<SlskdTransfer>> EnqueueDownloadsAsync(SlskdCredentials credentials, string username, IReadOnlyList<SlskdQueueDownload> downloads, CancellationToken cancellationToken = default);

    /// <summary>Lists the downloads slskd currently knows about.</summary>
    Task<IReadOnlyList<SlskdTransfer>> ListDownloadsAsync(SlskdCredentials credentials, bool includeRemoved = false, CancellationToken cancellationToken = default);

    /// <summary>Reads a single download transfer.</summary>
    Task<SlskdTransfer?> GetDownloadAsync(SlskdCredentials credentials, string username, Guid transferId, CancellationToken cancellationToken = default);

    /// <summary>Reads a download's position in the peer's queue.</summary>
    Task<int?> GetDownloadPositionAsync(SlskdCredentials credentials, string username, Guid transferId, CancellationToken cancellationToken = default);

    /// <summary>Cancels a download transfer, optionally removing it from the queue.</summary>
    Task CancelDownloadAsync(SlskdCredentials credentials, string username, Guid transferId, bool remove = false, CancellationToken cancellationToken = default);

    /// <summary>Clears slskd's completed download history.</summary>
    Task ClearCompletedDownloadsAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>Lists the shares slskd is configured to serve.</summary>
    Task<IReadOnlyList<SlskdShare>> ListSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>Lists the contents of every configured share.</summary>
    Task<IReadOnlyList<SlskdDirectory>> BrowseSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>Asks slskd to rescan its shares.</summary>
    Task RescanSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default);
}
