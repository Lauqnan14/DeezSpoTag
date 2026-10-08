using DeezSpoTag.Integrations.Soulseek;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     The default <see cref="ISoulseekShareService"/>.
/// </summary>
/// <remarks>
///     <para>
///         This service reads and reports. It does not configure slskd, because slskd has no API for creating or
///         updating shares: a share is declared in the instance's own configuration, with <c>[Alias]\path</c>
///         entries and <c>!</c> excludes. So the folder tab is the source of truth in DeezSpoTag, and this
///         service reports the difference between what the user asked for and what slskd actually serves,
///         plus a configuration block the user can paste.
///     </para>
///     <para>
///         The comparison and rendering rules live in <see cref="SoulseekShareConfiguration"/> so they can be
///         tested without I/O.
///     </para>
/// </remarks>
public sealed class SoulseekShareService : ISoulseekShareService
{
    private readonly ISlskdClient _client;
    private readonly ISoulseekCredentialProvider _credentials;
    private readonly ISoulseekConnectionService _connection;
    private readonly SoulseekSettingsService _settings;
    private readonly SoulseekRepository _repository;
    private readonly ISoulseekRealtimePublisher _realtime;
    private readonly ILogger<SoulseekShareService> _logger;

    /// <summary>Initializes a new instance of the <see cref="SoulseekShareService"/> class.</summary>
    public SoulseekShareService(
        ISlskdClient client,
        ISoulseekCredentialProvider credentials,
        ISoulseekConnectionService connection,
        SoulseekSettingsService settings,
        SoulseekRepository repository,
        ILogger<SoulseekShareService> logger,
        ISoulseekRealtimePublisher? realtime = null)
    {
        _client = client;
        _credentials = credentials;
        _connection = connection;
        _settings = settings;
        _repository = repository;
        _logger = logger;
        _realtime = realtime ?? NullSoulseekRealtimePublisher.Instance;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SoulseekDesiredShare>> GetDesiredSharesAsync(CancellationToken cancellationToken = default)
        => _settings.GetDesiredSharesAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SoulseekActualShare>> GetActualSharesAsync(CancellationToken cancellationToken = default)
    {
        var credentials = await _credentials.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        if (credentials is null)
        {
            return [];
        }

        var shares = await _client.ListSharesAsync(credentials, cancellationToken).ConfigureAwait(false);
        return shares
            .Select(share => new SoulseekActualShare(
                share.Id,
                share.LocalPath,
                share.Alias,
                share.IsExcluded,
                share.RemotePath,
                share.Files))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<SoulseekShareReconciliation> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var desired = await _settings.GetDesiredSharesAsync(cancellationToken).ConfigureAwait(false);
        var skipped = new List<(long FolderId, string Reason)>();

        foreach (var share in desired.Where(share => string.IsNullOrWhiteSpace(share.LocalPath)))
        {
            skipped.Add((share.FolderId, "no_path"));
        }

        var generated = GenerateShareConfiguration(desired);

        if (!await _connection.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            var status = await _connection.GetStatusAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var unavailable = SoulseekShareConfiguration.Diff(
                desired,
                actual: [],
                slskdUnavailable: true,
                skipped,
                status.Message);

            var unavailableResult = new SoulseekShareReconciliation(
                DateTimeOffset.UtcNow,
                desired,
                skipped,
                unavailable.MissingFromSlskd,
                unavailable.UnexpectedlyShared,
                unavailable.Diagnostics,
                generated,
                SlskdUnavailable: true);

            _realtime.PublishShareSyncUpdate(unavailableResult);
            return unavailableResult;
        }

        var actual = await GetActualSharesAsync(cancellationToken).ConfigureAwait(false);
        var diff = SoulseekShareConfiguration.Diff(desired, actual, skipped: skipped);

        var result = new SoulseekShareReconciliation(
            DateTimeOffset.UtcNow,
            desired,
            skipped,
            diff.MissingFromSlskd,
            diff.UnexpectedlyShared,
            diff.Diagnostics,
            generated,
            SlskdUnavailable: false);

        _realtime.PublishShareSyncUpdate(result);
        return result;
    }

    /// <inheritdoc />
    public string GenerateShareConfiguration(IReadOnlyList<SoulseekDesiredShare> desiredShares)
        => SoulseekShareConfiguration.Generate(desiredShares);

    /// <inheritdoc />
    public async Task<bool> RequestRescanAsync(CancellationToken cancellationToken = default)
    {
        var credentials = await _credentials.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        if (credentials is null)
        {
            return false;
        }

        await _repository.RecordShareScanStartedAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _client.RescanSharesAsync(credentials, cancellationToken).ConfigureAwait(false);
            var shares = await _client.ListSharesAsync(credentials, cancellationToken).ConfigureAwait(false);
            var fileCount = shares.Sum(share => (long)(share.Files ?? 0));
            await _repository.RecordShareScanCompletedAsync(shares.Count, fileCount, error: null, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (SlskdApiException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Could not start a Soulseek share rescan.");
            }

            await _repository.RecordShareScanCompletedAsync(0, 0, ex.Message, CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<SoulseekShareScanStatus> GetScanStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = await _repository.GetShareScanStateAsync(cancellationToken).ConfigureAwait(false);
        var available = await _connection.IsAvailableAsync(cancellationToken).ConfigureAwait(false);

        var status = new SoulseekShareScanStatus(
            state?.IsRunning ?? false,
            state?.CompletedAtUtc,
            state?.ShareCount ?? 0,
            state?.FileCount ?? 0,
            state?.Error,
            SlskdUnavailable: !available);

        _realtime.PublishShareScanUpdate(status);
        return status;
    }
}
