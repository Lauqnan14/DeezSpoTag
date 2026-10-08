using DeezSpoTag.Integrations.Soulseek;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Probes slskd and caches the result briefly so that a burst of download work does not hammer the
///     instance with health checks.
/// </summary>
/// <remarks>
///     The login page and sidebar keep using the shipped <c>SoulseekConnectionService</c> probe, so their
///     behaviour is unchanged. This service exists for the download engine, the share service and the new
///     API, so that everything on the download path asks one place whether Soulseek is usable.
/// </remarks>
public sealed class SoulseekConnectionService : ISoulseekConnectionService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReconnectPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>How many stale probe results are re-probed before the last one is answered with anyway.</summary>
    private const int MaxDiscardedProbeAttempts = 2;

    private readonly ISlskdClient _client;
    private readonly ISoulseekCredentialProvider _credentialProvider;
    private readonly ISoulseekRealtimePublisher _realtime;
    private readonly ILogger<SoulseekConnectionService> _logger;
    private readonly SemaphoreSlim _probeLock = new(1, 1);

    /// <summary>
    ///     Guards the cached answer and the event publication only.
    /// </summary>
    /// <remarks>
    ///     Separate from the probe lock so invalidation never has to wait for a network round trip. It is only
    ///     held for the moment it takes to store or clear a value, which is what lets a logout take effect
    ///     immediately even while slskd is being asked a question that is about to be thrown away.
    /// </remarks>
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    private SoulseekConnectionStatus? _cached;
    private DateTimeOffset _cachedAtUtc;

    /// <summary>
    ///     Bumped whenever the cached answer stops describing the world.
    /// </summary>
    /// <remarks>
    ///     Credentials being saved or the reader logging out both invalidate while a probe may already be in
    ///     flight. Without this the probe would land afterwards, write its now-stale answer into the cache and
    ///     publish it - handing back an active source seconds after the reader turned it off, which is exactly
    ///     the outcome logout has to prevent.
    /// </remarks>
    private long _generation;

    /// <summary>Initializes a new instance of the <see cref="SoulseekConnectionService"/> class.</summary>
    public SoulseekConnectionService(
        ISlskdClient client,
        ISoulseekCredentialProvider credentialProvider,
        ILogger<SoulseekConnectionService> logger,
        ISoulseekRealtimePublisher? realtime = null)
    {
        _client = client;
        _credentialProvider = credentialProvider;
        _logger = logger;
        _realtime = realtime ?? NullSoulseekRealtimePublisher.Instance;
    }

    /// <inheritdoc />
    public async Task<SoulseekConnectionStatus> GetStatusAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        // Bounded because each pass is a real network call and credentials are not expected to change in a
        // tight loop. Handing back the last answer beats looping forever if something upstream is thrashing.
        for (var pass = 0; pass <= MaxDiscardedProbeAttempts; pass++)
        {
            var outcome = await ProbeUnderLockAsync(force, cancellationToken).ConfigureAwait(false);
            if (!outcome.Discarded)
            {
                return outcome.Status;
            }

            // The cache was cleared underneath the probe, so the next pass re-reads the world as it is now
            // rather than the configuration the discarded answer described.
            force = false;
        }

        _logger.LogWarning(
            "Soulseek connection details kept changing while probing; answering with the honest uncertainty.");
        // Returning the final probe's answer unconditionally would hand back a positive that was taken under
        // a configuration the reader no longer has - exactly what the discard was meant to prevent.
        return new SoulseekConnectionStatus(
            SoulseekConnectionState.Disconnected,
            "Soulseek connection details changed while checking; refresh to get the current status.",
            LastError: "connection_details_changed_during_probe",
            CheckedAtUtc: DateTimeOffset.UtcNow);
    }

    /// <summary>
    ///     Probes slskd under the lock and reports whether the answer was thrown away as stale.
    /// </summary>
    private async Task<(SoulseekConnectionStatus Status, bool Discarded)> ProbeUnderLockAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        if (!force && _cached is not null && DateTimeOffset.UtcNow - _cachedAtUtc < CacheDuration)
        {
            return (_cached, false);
        }

        await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && _cached is not null && DateTimeOffset.UtcNow - _cachedAtUtc < CacheDuration)
            {
                return (_cached, false);
            }

            // Read before probing so a change that lands mid-probe is detectable below.
            var generation = Interlocked.Read(ref _generation);
            var status = await ProbeAsync(cancellationToken).ConfigureAwait(false);

            if (Interlocked.Read(ref _generation) != generation)
            {
                // Credentials were saved or the reader logged out while slskd was being asked. This answer
                // describes the previous configuration, so it is neither cached nor published.
                _logger.LogInformation(
                    "Discarding a Soulseek connection probe that finished after the connection details changed.");
                return (status, true);
            }

            // Recheck, write and publish are one step under the cache lock, and Invalidate() takes that same lock
            // to bump the generation and clear the cache. That makes the two mutually exclusive: the generation
            // cannot change between the recheck below and the publication above, which is the window that let
            // a probe hand out a positive the reader had already invalidated. The network call is deliberately
            // outside this region - it is why invalidation never has to wait for slskd to answer.
            await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Interlocked.Read(ref _generation) != generation)
                {
                    _logger.LogInformation(
                        "Discarding a Soulseek connection probe that finished after the connection details changed.");
                    return (status, true);
                }

                _cached = status;
                _cachedAtUtc = DateTimeOffset.UtcNow;

                // The connection state changed, so tell anything watching. The engine health event rides along
                // so a client can show "off" without having to interpret the state string itself.
                _realtime.PublishConnectionState(status);
                _realtime.PublishEngineHealth(
                    status.IsUsable ? "ready" : status.State.ToString().ToLowerInvariant(),
                    status.Message);
            }
            finally
            {
                _cacheLock.Release();
            }

            return (status, false);
        }
        finally
        {
            _probeLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<SoulseekConnectionStatus> GetEligibilityAsync(CancellationToken cancellationToken = default)
        => GetStatusAsync(force: true, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => (await GetStatusAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).IsUsable;

    /// <inheritdoc />
    public async Task<SoulseekConnectionStatus> EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(force: true, cancellationToken).ConfigureAwait(false);
        if (status.IsUsable || status.State != SoulseekConnectionState.Disconnected)
        {
            return status;
        }

        var credentials = await _credentialProvider.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        if (credentials is null)
        {
            return status;
        }

        try
        {
            await _client.ConnectAsync(credentials, cancellationToken).ConfigureAwait(false);
        }
        catch (SlskdApiException ex)
        {
            return new SoulseekConnectionStatus(
                ex.IsUnauthorized ? SoulseekConnectionState.Error : SoulseekConnectionState.Unavailable,
                ex.Message,
                LastError: ex.Message,
                CheckedAtUtc: DateTimeOffset.UtcNow);
        }

        var deadline = DateTimeOffset.UtcNow + ReconnectTimeout;
        do
        {
            status = await GetStatusAsync(force: true, cancellationToken).ConfigureAwait(false);
            if (status.IsUsable || status.State != SoulseekConnectionState.Disconnected || DateTimeOffset.UtcNow >= deadline)
            {
                return status;
            }

            await Task.Delay(ReconnectPollInterval, cancellationToken).ConfigureAwait(false);
        }
        while (true);
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        // The generation bump and the cache clear are one atomic step against the probe's check-and-publish
        // step: both happen under _cacheLock, so the bump can never land between the probe's staleness recheck
        // and its publication. Bumping outside this lock - which an earlier attempt did - left exactly that
        // window open, and the probe would publish a positive the reader had already invalidated.
        //
        // It still does not take the probe lock, so it never waits on slskd: the probe holds that lock across a
        // network call, while this one is only ever held for a few in-memory operations. A logout therefore
        // takes effect at once even while a probe is in flight, and the in-flight probe discards its answer when
        // it notices the generation moved.
        _cacheLock.Wait();
        try
        {
            Interlocked.Increment(ref _generation);
            _cached = null;
            _cachedAtUtc = default;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task<SoulseekConnectionStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        var credentials = await _credentialProvider.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        if (credentials is null)
        {
            return new SoulseekConnectionStatus(
                SoulseekConnectionState.NotConfigured,
                "Soulseek is not configured.",
                CheckedAtUtc: DateTimeOffset.UtcNow);
        }

        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var state = await _client.GetServerStateAsync(credentials, cancellationToken).ConfigureAwait(false);
            var responseTimeMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            var connected = state.IsConnected && state.IsLoggedIn;

            // The three inactive-but-reachable cases are reported separately. "Connected to slskd's server but
            // not logged in to Soulseek" and "not connected at all" send the reader to different places, and
            // collapsing them into one sentence is how a source ends up looking ready when it is not.
            return new SoulseekConnectionStatus(
                connected ? SoulseekConnectionState.Connected : SoulseekConnectionState.Disconnected,
                connected
                    ? "slskd is connected to Soulseek."
                    : state.IsConnected
                        ? "slskd is connected but not logged in to Soulseek."
                        : "slskd is not connected to Soulseek.",
                string.IsNullOrWhiteSpace(state.Username) ? null : state.Username,
                CheckedAtUtc: DateTimeOffset.UtcNow,
                ResponseTimeMs: responseTimeMs);
        }
        catch (SlskdApiException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Soulseek connection probe failed with status {StatusCode}.", ex.StatusCode);
            }

            return new SoulseekConnectionStatus(
                ex.IsUnauthorized ? SoulseekConnectionState.Error : SoulseekConnectionState.Unavailable,
                ex.Message,
                LastError: ex.Message,
                CheckedAtUtc: DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Soulseek connection probe failed unexpectedly.");
            }

            return new SoulseekConnectionStatus(
                SoulseekConnectionState.Unavailable,
                "slskd is unavailable.",
                LastError: ex.Message,
                CheckedAtUtc: DateTimeOffset.UtcNow);
        }
    }
}
