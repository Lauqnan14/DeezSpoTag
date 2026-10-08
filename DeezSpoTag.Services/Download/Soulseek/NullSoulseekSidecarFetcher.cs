namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     A sidecar fetcher for a host that has no Soulseek transport at all.
/// </summary>
/// <remarks>
///     The engine is registered for every host, including workers that have no slskd, so this keeps the
///     processor constructible there. It takes nothing, which is the honest answer: without a transport there
///     is no peer to ask, and a download on such a host has no sidecars to take.
/// </remarks>
public sealed class NullSoulseekSidecarFetcher : ISoulseekTransferService
{
    /// <summary>The shared instance.</summary>
    public static NullSoulseekSidecarFetcher Instance { get; } = new();

    /// <inheritdoc />
    public Task<Guid?> EnqueueAsync(string username, string filename, long size, CancellationToken cancellationToken = default)
        => Task.FromResult<Guid?>(null);

    /// <inheritdoc />
    public Task<SoulseekTransferStatus?> FindTransferAsync(string username, string filename, CancellationToken cancellationToken = default)
        => Task.FromResult<SoulseekTransferStatus?>(null);

    /// <inheritdoc />
    public Task<SoulseekTransferStatus?> GetStatusAsync(string username, Guid transferId, CancellationToken cancellationToken = default)
        => Task.FromResult<SoulseekTransferStatus?>(null);

    /// <inheritdoc />
    public Task<SoulseekCompletedFile?> WaitForCompletionAsync(
        string username,
        Guid transferId,
        string expectedPath,
        string queueUuid,
        string? completedDownloadsRoot = null,
        Func<double, double, Task>? progress = null,
        CancellationToken cancellationToken = default,
        CancellationToken hostStoppingToken = default)
        => Task.FromResult<SoulseekCompletedFile?>(null);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, string>> FetchSidecarsAsync(
        string username,
        IReadOnlyList<string> remotePaths,
        string outputDirectory,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <inheritdoc />
    public Task CancelAsync(string username, Guid transferId, bool remove = false, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<int> CleanupStaleTransfersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}
