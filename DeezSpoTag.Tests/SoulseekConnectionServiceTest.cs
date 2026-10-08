using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Soulseek;
using DeezSpoTag.Integrations.Soulseek;
using DeezSpoTag.Services.Download.Soulseek;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Tests for the Soulseek connection service that the download path uses.
/// </summary>
/// <remarks>
///     The login page and sidebar keep using the shipped web-tier probe, so these tests cover the new
///     service only: that it reports the same states, that it caches briefly, and that it never throws at
///     the download pipeline when slskd is down.
/// </remarks>
public sealed class SoulseekConnectionServiceTest
{
    [Fact]
    public async Task GetStatusAsync_ReportsNotConfiguredWhenNoCredentialsAreStored()
    {
        var service = new SoulseekConnectionService(new StubClient(), new StubCredentialProvider(null), NullLogger<SoulseekConnectionService>.Instance);

        var status = await service.GetStatusAsync();

        Assert.Equal(SoulseekConnectionState.NotConfigured, status.State);
        Assert.False(status.IsUsable);
    }

    /// <summary>
    ///     Every state that is not a verified login must leave the source inactive.
    /// </summary>
    /// <remarks>
    ///     This is the admission rule the whole feature rests on, so it is stated once over the cases that
    ///     matter rather than implied by the individual probes above. A URL, a saved API key, a
    ///     <c>saved=true</c> response or a persisted ConnectionValid are none of them a login, and each of
    ///     these has been mistaken for one somewhere in this path before.
    /// </remarks>
    [Theory]
    [InlineData(SoulseekConnectionState.NotConfigured)]
    [InlineData(SoulseekConnectionState.Disconnected)]
    [InlineData(SoulseekConnectionState.Unavailable)]
    [InlineData(SoulseekConnectionState.Error)]
    public async Task NoVerifiedLoginMeansTheSourceStaysInactive(SoulseekConnectionState expected)
    {
        var client = new StubClient();
        var provider = new StubCredentialProvider(Credentials());
        switch (expected)
        {
            case SoulseekConnectionState.NotConfigured:
                provider = new StubCredentialProvider(null);
                break;
            case SoulseekConnectionState.Disconnected:
                // Connected to slskd's server but never logged in to Soulseek: the case that looks most like success.
                client.State = new SlskdServerState { IsConnected = true, IsLoggedIn = false };
                break;
            case SoulseekConnectionState.Unavailable:
                client.Failure = new SlskdApiException(0, null, "slskd is unavailable.");
                break;
            case SoulseekConnectionState.Error:
                client.Failure = new SlskdApiException(401, null, "slskd rejected the API key.");
                break;
        }

        var service = new SoulseekConnectionService(client, provider, NullLogger<SoulseekConnectionService>.Instance);

        var status = await service.GetEligibilityAsync();

        Assert.Equal(expected, status.State);
        Assert.False(status.IsUsable);
        Assert.False(await service.IsAvailableAsync());
    }

    [Fact]
    public async Task GetEligibilityAsync_OnlyGrantsWorkOnAVerifiedLogin()
    {
        var client = new StubClient { State = new SlskdServerState { IsConnected = true, IsLoggedIn = true, Username = "listener" } };
        var service = new SoulseekConnectionService(client, new StubCredentialProvider(Credentials()), NullLogger<SoulseekConnectionService>.Instance);

        var status = await service.GetEligibilityAsync();

        Assert.True(status.IsUsable);
        Assert.Equal("listener", status.Username);
    }

    /// <summary>
    ///     Admission must not be answered from a cache that is up to twenty seconds old.
    /// </summary>
    [Fact]
    public async Task GetEligibilityAsync_AlwaysProbesRatherThanReadingTheCache()
    {
        var client = new StubClient { State = new SlskdServerState { IsConnected = true, IsLoggedIn = true } };
        var service = new SoulseekConnectionService(client, new StubCredentialProvider(Credentials()), NullLogger<SoulseekConnectionService>.Instance);

        await service.GetStatusAsync();
        await service.GetStatusAsync();
        Assert.Equal(1, client.ServerStateCalls);

        // A plain read would have been served from the cache. Admission asks slskd every time.
        await service.GetEligibilityAsync();
        Assert.Equal(2, client.ServerStateCalls);
    }

    /// <summary>
    ///     Saving credentials must not leave the previous answer standing.
    /// </summary>
    [Fact]
    public async Task Invalidate_MakesTheNextAdmissionRecheckAgainstTheNewDetails()
    {
        var client = new StubClient { State = new SlskdServerState { IsConnected = true, IsLoggedIn = true } };
        var service = new SoulseekConnectionService(client, new StubCredentialProvider(Credentials()), NullLogger<SoulseekConnectionService>.Instance);
        Assert.True((await service.GetEligibilityAsync()).IsUsable);

        // The reader logs out, or saves details that point somewhere else.
        client.State = new SlskdServerState { IsConnected = false, IsLoggedIn = false };
        service.Invalidate();

        var after = await service.GetEligibilityAsync();

        Assert.False(after.IsUsable);
        Assert.Equal(SoulseekConnectionState.Disconnected, after.State);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsConnectedOnlyWhenConnectedAndLoggedIn()
    {
        var client = new StubClient { State = new SlskdServerState { IsConnected = true, IsLoggedIn = true, Username = "listener" } };
        var service = new SoulseekConnectionService(client, new StubCredentialProvider(Credentials()), NullLogger<SoulseekConnectionService>.Instance);

        var status = await service.GetStatusAsync();

        Assert.Equal(SoulseekConnectionState.Connected, status.State);
        Assert.True(status.IsUsable);
        Assert.Equal("listener", status.Username);
        Assert.Equal(1, client.ServerStateCalls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task GetStatusAsync_ReportsDisconnectedUnlessBothFlagsAreSet(bool connected, bool loggedIn)
    {
        var client = new StubClient { State = new SlskdServerState { IsConnected = connected, IsLoggedIn = loggedIn } };
        var service = new SoulseekConnectionService(client, new StubCredentialProvider(Credentials()), NullLogger<SoulseekConnectionService>.Instance);

        var status = await service.GetStatusAsync();

        Assert.Equal(SoulseekConnectionState.Disconnected, status.State);
        Assert.False(status.IsUsable);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsUnavailableWhenSlskdCannotBeReached()
    {
        var client = new StubClient { Failure = new SlskdApiException(0, null, "slskd is unavailable.") };
        var service = new SoulseekConnectionService(client, new StubCredentialProvider(Credentials()), NullLogger<SoulseekConnectionService>.Instance);

        var status = await service.GetStatusAsync();

        Assert.Equal(SoulseekConnectionState.Unavailable, status.State);
        Assert.False(status.IsUsable);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsErrorWhenSlskdRejectsTheApiKey()
    {
        var client = new StubClient { Failure = new SlskdApiException(401, null, "slskd rejected the API key.") };
        var service = new SoulseekConnectionService(client, new StubCredentialProvider(Credentials()), NullLogger<SoulseekConnectionService>.Instance);

        var status = await service.GetStatusAsync();

        Assert.Equal(SoulseekConnectionState.Error, status.State);
        Assert.Equal("slskd rejected the API key.", status.LastError);
    }

    [Fact]
    public async Task GetStatusAsync_CachesWithinTheWindow_AndForceProbesAgain()
    {
        var client = new StubClient { State = new SlskdServerState { IsConnected = true, IsLoggedIn = true } };
        var service = new SoulseekConnectionService(client, new StubCredentialProvider(Credentials()), NullLogger<SoulseekConnectionService>.Instance);

        await service.GetStatusAsync();
        await service.GetStatusAsync();
        Assert.Equal(1, client.ServerStateCalls);

        await service.GetStatusAsync(force: true);
        Assert.Equal(2, client.ServerStateCalls);
    }

    [Fact]
    public async Task Invalidate_ForcesTheNextReadToProbeAgain()
    {
        var client = new StubClient { State = new SlskdServerState { IsConnected = true, IsLoggedIn = true } };
        var service = new SoulseekConnectionService(client, new StubCredentialProvider(Credentials()), NullLogger<SoulseekConnectionService>.Instance);

        await service.GetStatusAsync();
        service.Invalidate();
        await service.GetStatusAsync();

        Assert.Equal(2, client.ServerStateCalls);
    }

    [Fact]
    public async Task IsAvailableAsync_IsFalseWhenSlskdIsNotConfigured()
    {
        var service = new SoulseekConnectionService(new StubClient(), new StubCredentialProvider(null), NullLogger<SoulseekConnectionService>.Instance);

        Assert.False(await service.IsAvailableAsync());
    }

    [Fact]
    public async Task EnsureAvailableAsync_DoesNotReconnectAnAlreadyUsableSession()
    {
        var client = new StubClient
        {
            State = new SlskdServerState { IsConnected = true, IsLoggedIn = true, Username = "listener" }
        };
        var service = new SoulseekConnectionService(client, new StubCredentialProvider(Credentials()), NullLogger<SoulseekConnectionService>.Instance);

        var status = await service.EnsureAvailableAsync();

        Assert.True(status.IsUsable);
        Assert.Equal(0, client.ConnectCalls);
        Assert.Equal(1, client.ServerStateCalls);
    }

    [Fact]
    public async Task EnsureAvailableAsync_ReconnectsAndReprobesADisconnectedSession()
    {
        var client = new StubClient
        {
            State = new SlskdServerState(),
            ConnectResult = new SlskdServerState { IsConnected = true, IsLoggedIn = true, Username = "listener" }
        };
        var service = new SoulseekConnectionService(client, new StubCredentialProvider(Credentials()), NullLogger<SoulseekConnectionService>.Instance);

        var status = await service.EnsureAvailableAsync();

        Assert.True(status.IsUsable);
        Assert.Equal("listener", status.Username);
        Assert.Equal(1, client.ConnectCalls);
        Assert.Equal(2, client.ServerStateCalls);
    }

    [Fact]
    public async Task EnsureAvailableAsync_ReturnsTheConnectFailureInsteadOfThrowing()
    {
        var client = new StubClient
        {
            State = new SlskdServerState(),
            ConnectFailure = new SlskdApiException(409, null, "slskd is not logged in to Soulseek.")
        };
        var service = new SoulseekConnectionService(client, new StubCredentialProvider(Credentials()), NullLogger<SoulseekConnectionService>.Instance);

        var status = await service.EnsureAvailableAsync();

        Assert.Equal(SoulseekConnectionState.Unavailable, status.State);
        Assert.Equal("slskd is not logged in to Soulseek.", status.Message);
        Assert.Equal(1, client.ConnectCalls);
    }

    [Fact]
    public async Task EnsureAvailableAsync_StopsPollingWhenReconnectNeverCompletes()
    {
        var client = new StubClient { State = new SlskdServerState(), ConnectResult = new SlskdServerState() };
        var service = new SoulseekConnectionService(client, new StubCredentialProvider(Credentials()), NullLogger<SoulseekConnectionService>.Instance);
        var stopwatch = Stopwatch.StartNew();

        var status = await service.EnsureAvailableAsync();

        Assert.Equal(SoulseekConnectionState.Disconnected, status.State);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.Equal(1, client.ConnectCalls);
        Assert.True(client.ServerStateCalls > 1);
    }

    [Fact]
    public async Task EnsureAvailableAsync_RespectsCancellation()
    {
        var client = new StubClient { State = new SlskdServerState(), ConnectResult = new SlskdServerState() };
        var service = new SoulseekConnectionService(client, new StubCredentialProvider(Credentials()), NullLogger<SoulseekConnectionService>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EnsureAvailableAsync(cancellation.Token));
    }

    /// <summary>
    ///     A logout must not leave a positive answer sitting in the cache.
    /// </summary>
    /// <remarks>
    ///     The probe now takes the same lock invalidation does, so a change arriving mid-probe waits for it
    ///     rather than interleaving. That makes the ordering deterministic - and it means the property worth
    ///     asserting is the one that follows: once the reader has logged out, the next read must not be served
    ///     the "active" answer that was cached before the logout.
    /// </remarks>
    [Fact]
    public async Task ALogoutClearsAPositiveAnswerBeforeTheNextRead()
    {
        var client = new StubClient { State = new SlskdServerState { IsConnected = true, IsLoggedIn = true } };
        var service = new SoulseekConnectionService(client, new StubCredentialProvider(Credentials()), NullLogger<SoulseekConnectionService>.Instance);

        Assert.True((await service.GetEligibilityAsync()).IsUsable);
        Assert.True((await service.GetStatusAsync()).IsUsable);

        // The reader logs out. slskd is no longer reachable for them.
        client.State = new SlskdServerState();
        service.Invalidate();

        var afterLogout = await service.GetStatusAsync();

        Assert.False(afterLogout.IsUsable);
        Assert.Equal(SoulseekConnectionState.Disconnected, afterLogout.State);
    }


    /// <summary>
    ///     A logout that lands while a probe is in flight must never publish a positive.
    /// </summary>
    /// <remarks>
    ///     Asserted on what was published rather than on what a later read returns, because the retry is
    ///     working as designed and a fresh answer to a fresh question is exactly what it should produce. What
    ///     must never happen is the pre-logout answer being handed out or broadcast - that is work admitted
    ///     after the reader switched the source off.
    /// </remarks>
    [Fact]
    public async Task ALogoutDuringAProbePublishesNoPositive()
    {
        var client = new StubClient
        {
            State = new SlskdServerState { IsConnected = true, IsLoggedIn = true, Username = "listener" },
            ProbeDelay = TimeSpan.FromMilliseconds(60)
        };
        var realtime = new RecordingConnectionPublisher();
        var service = new SoulseekConnectionService(
            client,
            new StubCredentialProvider(Credentials()),
            NullLogger<SoulseekConnectionService>.Instance,
            realtime);

        var probe = service.GetEligibilityAsync();

        // The reader logs out while slskd is still being asked.
        var logout = Task.Run(() =>
        {
            Thread.Sleep(15);
            client.State = new SlskdServerState();
            service.Invalidate();
        });

        await probe;
        await logout;

        // Nothing that said the source was usable may have gone out - not to the cache, not to the hub.
        Assert.DoesNotContain(realtime.Published, status => status.IsUsable);
        Assert.DoesNotContain(realtime.Health, entry => entry.state == "ready");

        // And the recorded answer agrees.
        Assert.False((await service.GetStatusAsync()).IsUsable);
    }

    /// <summary>
    ///     The generation bump and the probe's publish must be mutually exclusive.
    /// </summary>
    /// <remarks>
    ///     Three earlier arrangements each failed a different way, and none of them could be caught by a
    ///     runtime test because the interleaving they leave open is a few instructions wide: comparing once
    ///     left the write outside the check; comparing twice still left the publication outside both; and
    ///     taking the probe lock in <c>Invalidate()</c> made a logout queue behind the whole network call, so
    ///     the probe published a positive and returned it first. What holds is that the bump and the cache
    ///     clear happen under the same lock as the probe's recheck, write and publication - so neither can land
    ///     inside the other's step - while no lock is ever held across the network call.
    /// </remarks>
    [Fact]
    public void TheGenerationBumpIsMutuallyExclusiveWithTheProbesPublication()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Soulseek/SoulseekConnectionService.cs"));

        var probe = source[source.IndexOf("private async Task<(SoulseekConnectionStatus Status, bool Discarded)> ProbeUnderLockAsync", StringComparison.Ordinal)..];
        probe = probe[..probe.IndexOf("\n    private ", StringComparison.Ordinal)];

        var invalidate = source[source.IndexOf("public void Invalidate()", StringComparison.Ordinal)..];
        invalidate = invalidate[..invalidate.IndexOf("\n    private ", StringComparison.Ordinal)];

        // The network call happens before the cache lock is taken, so no lock spans slskd.
        var networkCall = probe.IndexOf("await ProbeAsync(", StringComparison.Ordinal);
        var probeCacheLock = probe.IndexOf("await _cacheLock.WaitAsync(", StringComparison.Ordinal);
        Assert.True(networkCall >= 0 && probeCacheLock > networkCall,
            "The cache lock must not be held across the network call.");

        // Recheck, write and publication are one step inside that lock.
        var recheck = probe.IndexOf("Interlocked.Read(ref _generation) != generation", probeCacheLock, StringComparison.Ordinal);
        var write = probe.IndexOf("_cached = status;", StringComparison.Ordinal);
        var publish = probe.IndexOf("_realtime.PublishConnectionState(status);", StringComparison.Ordinal);
        var release = probe.IndexOf("_cacheLock.Release();", StringComparison.Ordinal);
        Assert.True(recheck > probeCacheLock && write > recheck && publish > write && release > publish,
            "Recheck, cache write and publication must all sit inside the cache lock.");

        // Invalidation bumps and clears inside that same lock, so it cannot land mid-publish.
        var invalidateLock = invalidate.IndexOf("_cacheLock.Wait();", StringComparison.Ordinal);
        var bump = invalidate.IndexOf("Interlocked.Increment(ref _generation);", StringComparison.Ordinal);
        var clear = invalidate.IndexOf("_cached = null;", StringComparison.Ordinal);
        var invalidateRelease = invalidate.IndexOf("_cacheLock.Release();", StringComparison.Ordinal);
        Assert.True(invalidateLock >= 0 && bump > invalidateLock && clear > bump && invalidateRelease > clear,
            "Invalidate must bump the generation and clear the cache under the cache lock.");

        // And it must never take the probe lock, or a logout queues behind the network again.
        Assert.DoesNotContain("_probeLock.Wait", invalidate, StringComparison.Ordinal);
    }

    /// <summary>Records what the service publishes, so a test can prove a positive never went out.</summary>



    /// <summary>Records what the service publishes, so a test can prove a positive never went out.</summary>
    private sealed class RecordingConnectionPublisher : ISoulseekRealtimePublisher
    {
        public List<SoulseekConnectionStatus> Published { get; } = [];

        public List<(string state, string? message)> Health { get; } = [];

        public void PublishConnectionState(SoulseekConnectionStatus status) => Published.Add(status);

        public void PublishEngineHealth(string state, string? message) => Health.Add((state, message));

        public void PublishDownloadUpdate(SoulseekDownloadProgress progress) { }

        public void PublishSearchUpdate(SoulseekSearchProgress progress) { }

        public void PublishSearchResult(SoulseekSearchOutcome outcome) { }

        public void PublishShareSyncUpdate(SoulseekShareReconciliation reconciliation) { }

        public void PublishShareScanUpdate(SoulseekShareScanStatus status) { }

        public void PublishImportUpdate(SoulseekImportUpdate update) { }
    }

    private static SlskdCredentials Credentials() => new("http://localhost:5030", "key");

    private sealed class StubCredentialProvider(SlskdCredentials? credentials) : ISoulseekCredentialProvider
    {
        public Task<SlskdCredentials?> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(credentials);
    }

    private sealed class StubClient : ISlskdClient
    {
        public SlskdServerState State { get; set; } = new();

        public Exception? Failure { get; set; }

        public SlskdServerState ConnectResult { get; init; } = new();

        public Exception? ConnectFailure { get; init; }

        public int ServerStateCalls { get; private set; }

        public int ConnectCalls { get; private set; }

        /// <summary>
        ///     Runs once, after the answer to a status question has been decided. Lets a test invalidate
        ///     mid-probe and observe that the stale answer is thrown away.
        /// </summary>
        public Action? OnServerState { get; set; }

        /// <summary>Makes the probe take long enough for a concurrent change to land while it runs.</summary>
        public TimeSpan ProbeDelay { get; set; }

        public async Task<SlskdServerState> GetServerStateAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
        {
            ServerStateCalls++;
            if (ProbeDelay > TimeSpan.Zero)
            {
                await Task.Delay(ProbeDelay, cancellationToken);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            // Snapshot first: the hook stands in for something happening while slskd was answering, so it
            // must not be able to change the answer that question already produced.
            var answered = State;
            var hook = OnServerState;
            OnServerState = null;
            hook?.Invoke();
            return answered;
        }

        public Task<SlskdServerState> ConnectAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCalls++;
            if (ConnectFailure is not null)
            {
                throw ConnectFailure;
            }

            State = ConnectResult;
            return Task.FromResult(ConnectResult);
        }

        public Task DisconnectAsync(SlskdCredentials credentials, string? message = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SlskdSearch> StartSearchAsync(SlskdCredentials credentials, SlskdSearchRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SlskdSearch?> GetSearchAsync(SlskdCredentials credentials, Guid searchId, bool includeResponses = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdSearch>> ListSearchesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdSearchResponse>> GetSearchResponsesAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SlskdUserStatus?> GetUserStatusAsync(SlskdCredentials credentials, string username, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdDirectory>> BrowseUserAsync(SlskdCredentials credentials, string username, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdDirectory>> BrowseUserDirectoryAsync(SlskdCredentials credentials, string username, string? directory, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdTransfer>> EnqueueDownloadsAsync(SlskdCredentials credentials, string username, IReadOnlyList<SlskdQueueDownload> downloads, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdTransfer>> ListDownloadsAsync(SlskdCredentials credentials, bool includeRemoved = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SlskdTransfer?> GetDownloadAsync(SlskdCredentials credentials, string username, Guid transferId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int?> GetDownloadPositionAsync(SlskdCredentials credentials, string username, Guid transferId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelDownloadAsync(SlskdCredentials credentials, string username, Guid transferId, bool remove = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ClearCompletedDownloadsAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdShare>> ListSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdDirectory>> BrowseSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RescanSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
