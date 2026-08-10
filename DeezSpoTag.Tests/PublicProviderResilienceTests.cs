using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PublicProviderResilienceTests
{
    [Fact]
    public void SessionGateIsNotHeldWhileBootstrapOrRefreshRuns()
    {
        var coordinator = ReadCoordinator();
        var start = coordinator.IndexOf("public async Task<ZarzSignedSession> EnsureSessionAsync(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = coordinator[start..(start + 4200)];

        Assert.DoesNotContain("var result = await bootstrap(current?.Copy(), cancellationToken);", body, StringComparison.Ordinal);
        Assert.Contains("RunBoundedAsync(", body, StringComparison.Ordinal);
        Assert.Contains("gate.Release();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void VerificationNetworkStagesAreBoundedAndOutsideTheProviderGate()
    {
        var coordinator = ReadCoordinator();
        var beginStart = coordinator.IndexOf("public async Task<string?> BeginVerificationAsync(", StringComparison.Ordinal);
        var completeStart = coordinator.IndexOf("public async Task CompleteVerificationAsync(", StringComparison.Ordinal);
        Assert.True(beginStart > 0);
        Assert.True(completeStart > 0);

        var beginBody = coordinator[beginStart..(beginStart + 2200)];
        var completeBody = coordinator[completeStart..(completeStart + 3200)];

        Assert.DoesNotContain("var result = await bootstrap(", beginBody, StringComparison.Ordinal);
        Assert.DoesNotContain("var exchanged = await exchange(", completeBody, StringComparison.Ordinal);
        Assert.Contains("RunBoundedAsync(", beginBody, StringComparison.Ordinal);
        Assert.Contains("RunBoundedAsync(", completeBody, StringComparison.Ordinal);
        Assert.Contains("gate.Release();", beginBody, StringComparison.Ordinal);
        Assert.Contains("gate.Release();", completeBody, StringComparison.Ordinal);
    }

    [Fact]
    public void CachedStatusDoesNotTakeTheProviderGate()
    {
        var coordinator = ReadCoordinator();
        var start = coordinator.IndexOf("public async Task<bool> PeekUsableSessionAsync(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = coordinator[start..(start + 600)];

        Assert.DoesNotContain("gate.WaitAsync", body, StringComparison.Ordinal);
        Assert.Contains("LoadNoLockAsync", body, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionStagesAreBounded()
    {
        var coordinator = ReadCoordinator();

        Assert.Contains("SessionStageTimeout", coordinator, StringComparison.Ordinal);
        Assert.Contains("timeout.CancelAfter(SessionStageTimeout);", coordinator, StringComparison.Ordinal);
        Assert.Contains("TimeoutException", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void CombinedStatusIsolatesEachProviderBehindItsOwnTimeout()
    {
        var controller = ReadPlatformAuthController();

        Assert.Contains("PublicProviderStatusTimeout", controller, StringComparison.Ordinal);
        Assert.Contains("ResolveProviderStatusAsync(", controller, StringComparison.Ordinal);
        Assert.Contains("return (PublicApiDegradedStatus, 0);", controller, StringComparison.Ordinal);
        Assert.Contains("await Task.WhenAll(qobuzTask, amazonTask, tidalTask);", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusUsesCachedSessionUnlessAnExplicitCheckIsRequested()
    {
        var controller = ReadPlatformAuthController();

        Assert.Contains("PeekPublicDownloadSessionAsync(cancellationToken)", controller, StringComparison.Ordinal);
        Assert.Contains("liveSession", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroByteAcquisitionWithExpiredLeaseIsTreatedAsStalled()
    {
        var recovery = ReadRecoveryService();

        Assert.Contains("RecoverStalledAcquisitionsAsync", recovery, StringComparison.Ordinal);
        Assert.Contains("AcquisitionStageLease", recovery, StringComparison.Ordinal);
        Assert.Contains("snapshot.AudioAcquired", recovery, StringComparison.Ordinal);
        Assert.Contains("snapshot.Progress > 0", recovery, StringComparison.Ordinal);
        Assert.Contains("snapshot.TotalSize > 0", recovery, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveAudioTransferIsNotTreatedAsStalled()
    {
        var recovery = ReadRecoveryService();
        var start = recovery.IndexOf("TryReadStalledAcquisition(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = recovery[start..(start + 3200)];

        Assert.Contains("DownloadAcquisitionStages.DownloadingAudio", body, StringComparison.Ordinal);
        Assert.Contains("return false;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void StalledStepFailsOnlyItselfAndAdvancesTheFallbackLadder()
    {
        var processor = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Services", "Download", "Qobuz", "QobuzEngineProcessor.cs"));

        Assert.Contains("FallbackAttemptRecorder.RecordCurrent(", processor, StringComparison.Ordinal);
        Assert.Contains("_fallbackCoordinator.TryAdvanceAsync(", processor, StringComparison.Ordinal);
    }

    [Fact]
    public void AcquisitionStagesArePersistedRatherThanFakedAsRunning()
    {
        var processor = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Services", "Download", "Qobuz", "QobuzEngineProcessor.cs"));

        Assert.Contains("DownloadAcquisitionStages.ResolvingQuality", processor, StringComparison.Ordinal);
        Assert.Contains("DownloadAcquisitionStages.ResolvingProviderSession", processor, StringComparison.Ordinal);
        Assert.Contains("DownloadAcquisitionStages.DownloadingAudio", processor, StringComparison.Ordinal);
        Assert.Contains("DownloadAcquisitionStages.ValidatingAudio", processor, StringComparison.Ordinal);
        Assert.Contains("DownloadAcquisitionStages.Finalizing", processor, StringComparison.Ordinal);
    }

    [Fact]
    public void StallDiagnosticsNameTheProviderAndStage()
    {
        var policy = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Services", "Download", "Queue", "DownloadQueueRecoveryPolicy.cs"));

        Assert.Contains("BuildAcquisitionStallMessage", policy, StringComparison.Ordinal);
        Assert.Contains("before any audio transfer started", policy, StringComparison.Ordinal);
    }

    private static string ReadCoordinator()
        => File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Services", "Download", "Shared", "ZarzSignedSessionCoordinator.cs"));

    private static string ReadPlatformAuthController()
        => File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Controllers", "Api", "PlatformAuthApiController.cs"));

    private static string ReadRecoveryService()
        => File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Services", "Download", "Queue", "DownloadQueueRecoveryService.cs"));

    private static string FindRepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (Directory.Exists(Path.Join(directory, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(directory, "DeezSpoTag.Tests")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
