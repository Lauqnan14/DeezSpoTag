using DeezSpoTag.Services.Download.Shared;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Prunes the Soulseek state that accumulates over time.
/// </summary>
/// <remarks>
///     <para>
///         Three things grow without bound otherwise: expired peer cooldowns, finished searches slskd is still
///         holding, and terminal transfer records. None of them affect a download in flight, so the sweep is
///         safe to run from the queue loop.
///     </para>
///     <para>
///         The sweep does nothing when the integration is switched off, so a disabled install does not touch
///         slskd at all.
///     </para>
/// </remarks>
public sealed class SoulseekMaintenanceTask : IQueueMaintenanceTask
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(15);

    private readonly ISoulseekCredentialProvider _credentials;
    private readonly ISoulseekPeerPolicyService _peerPolicy;
    private readonly ISoulseekSearchService _search;
    private readonly ISoulseekTransferService _transfer;
    private readonly SoulseekSettingsService _settings;
    private readonly ILogger<SoulseekMaintenanceTask> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTimeOffset _lastRunUtc;

    /// <summary>Initializes a new instance of the <see cref="SoulseekMaintenanceTask"/> class.</summary>
    public SoulseekMaintenanceTask(
        ISoulseekCredentialProvider credentials,
        ISoulseekPeerPolicyService peerPolicy,
        ISoulseekSearchService search,
        ISoulseekTransferService transfer,
        SoulseekSettingsService settings,
        ILogger<SoulseekMaintenanceTask> logger)
    {
        _credentials = credentials;
        _peerPolicy = peerPolicy;
        _search = search;
        _transfer = transfer;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Engine => SoulseekQueueItem.EngineId;

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _lastRunUtc < MinimumInterval)
        {
            return;
        }

        // A sweep must never overlap itself or the manual cleanup endpoints.
        if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            _lastRunUtc = DateTimeOffset.UtcNow;

            var cooldowns = await _peerPolicy.CleanupExpiredCooldownsAsync(cancellationToken).ConfigureAwait(false);
            var settings = _settings.GetSettings();
            var retention = TimeSpan.FromMinutes(Math.Clamp(settings.SearchRetentionMinutes, 1, 1_440));
            var searches = await _search.CleanupStaleSearchesAsync(retention, cancellationToken).ConfigureAwait(false);
            var transfers = await _transfer.CleanupStaleTransfersAsync(cancellationToken).ConfigureAwait(false);

            if (_logger.IsEnabled(LogLevel.Debug) && (cooldowns + searches + transfers) > 0)
            {
                _logger.LogDebug(
                    "Soulseek housekeeping removed {Cooldowns} expired cooldowns, {Searches} stale searches and {Transfers} stale transfers.",
                    cooldowns,
                    searches,
                    transfers);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Housekeeping is best effort. An error here must never stop the queue loop from running downloads.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Soulseek housekeeping failed.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
