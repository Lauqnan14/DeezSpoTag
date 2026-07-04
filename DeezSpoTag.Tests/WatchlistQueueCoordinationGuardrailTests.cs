using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WatchlistQueueCoordinationGuardrailTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void PlaylistWatchQueue_UsesRunBudgetAsTheOnlyQueueItemLimit()
    {
        var source = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");

        Assert.Contains("_watchlistRunQueueBudget.TryReserve(1)", source, StringComparison.Ordinal);
        Assert.Contains("_watchlistRunQueueBudget.Release(1)", source, StringComparison.Ordinal);
        Assert.Contains("result.Queued.Count", source, StringComparison.Ordinal);
        Assert.Contains("allowAutomaticSecondaryQuality: false", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetActiveDownloadCountAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WatchQueueCapacity", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchQueue_UsesStrictQueueGateAndTracksGateDeferrals()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");
        var intentSource = ReadSource("DeezSpoTag.Web/Services/DownloadIntentService.cs");

        Assert.Contains("EvaluateDownloadGateAsync", watchSource, StringComparison.Ordinal);
        Assert.Contains("EnqueueAsync", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("EnqueueManualAsync", watchSource, StringComparison.Ordinal);
        Assert.Contains("ShouldDeferWatchTrack", watchSource, StringComparison.Ordinal);
        Assert.Contains("download_gate_paused", watchSource, StringComparison.Ordinal);
        Assert.Contains("download_gate_paused", intentSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"failed\",\r\n                    cancellationToken);\r\n                failedCount++;\r\n                break;", watchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchQueue_DoesNotUseExistingQueueRowsAsRunBudget()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");

        Assert.DoesNotContain("GetUnfinishedWatchlistDownloadCountAsync", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetActiveWatchlistDownloadCountAsync", watchSource, StringComparison.Ordinal);
        Assert.Contains("_watchlistRunQueueBudget.TryReserve(1)", watchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchQueue_SetsDeferredWhenTrackIsDeferredByDownloadGate()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");

        Assert.Contains("var deferred = false;", watchSource, StringComparison.Ordinal);
        Assert.Contains("deferred = true;", watchSource, StringComparison.Ordinal);
        Assert.Contains("new QueueWatchResult(", watchSource, StringComparison.Ordinal);
        Assert.Contains("Deferred: deferred", watchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchQueue_UsesResolutionAttemptBudgetSeparateFromQueueBudget()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");

        Assert.Contains("watchSettings.WatchMaxTracksPerPlaylistCheck", watchSource, StringComparison.Ordinal);
        Assert.Contains("attemptedCount >= maxResolutionAttempts", watchSource, StringComparison.Ordinal);
        Assert.Contains("watch queue reached resolution-attempt budget", watchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchTriggers_DoNotWaitAndStartAnotherBudgetedRun()
    {
        var hostedSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchHostedService.cs");

        Assert.Contains("Interlocked.Exchange(ref _triggerPending, 1)", hostedSource, StringComparison.Ordinal);
        Assert.Contains("if (!await _runLock.WaitAsync(0, cancellationToken))", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("await _runLock.WaitAsync(cancellationToken);", hostedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HostedCycle_ChecksExistingWatchlistDownloadsBeforeOpeningRunBudget()
    {
        var hostedSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchHostedService.cs");
        var activeCheckIndex = hostedSource.IndexOf("HasActiveWatchlistDownloadsAsync", StringComparison.Ordinal);
        var beginRunIndex = hostedSource.IndexOf("runQueueBudget?.BeginRun", StringComparison.Ordinal);

        Assert.True(activeCheckIndex >= 0);
        Assert.True(beginRunIndex > activeCheckIndex);
        Assert.Contains("WatchlistQueueBlockReason.PreviousWatchlistRunActive", hostedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviousWatchlistRunBlock_HasSpecificNonFailureStatusAndMessage()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");

        Assert.Contains("Waiting for downloads from the previous watchlist run to finish.", watchSource, StringComparison.Ordinal);
        Assert.Contains("queue_deferred_previous_watchlist_active", watchSource, StringComparison.Ordinal);
        Assert.Contains("WatchQueueStopReason.PreviousWatchlistRunActive", watchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalTrackUnavailableFailure_PersistsWatchlistCooldown()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");
        var downloadSource = ReadSource("DeezSpoTag.Services/Download/Shared/EngineAudioPostDownloadHelper.cs");

        Assert.Contains("WatchlistUnavailableSettingsFingerprint = BuildUnavailableSettingsFingerprint(options)", watchSource, StringComparison.Ordinal);
        Assert.Contains("IsTrackUnavailableFailure(failureMessage)", downloadSource, StringComparison.Ordinal);
        Assert.Contains("MarkPlaylistWatchTrackUnavailableAsync(", downloadSource, StringComparison.Ordinal);
        Assert.Contains("DateTimeOffset.UtcNow.AddDays(WatchlistUnavailableRetryDays)", downloadSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Qobuz track not found for ISRC or metadata.", true)]
    [InlineData("Enabled fallback sources could not resolve this track after tidal failed.", true)]
    [InlineData("Amazon download API failed with HTTP 404: Track not available", true)]
    [InlineData("Tidal operation timed out or was canceled by an external provider.", false)]
    [InlineData("No Tidal download provider is currently available.", false)]
    [InlineData("Qobuz official credentials are missing.", false)]
    public void TerminalTrackUnavailableFailure_OnlyClassifiesCatalogueMisses(string message, bool expected)
    {
        var method = typeof(DeezSpoTag.Services.Download.Shared.EngineAudioPostDownloadHelper)
            .GetMethod("IsTrackUnavailableFailure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal(expected, method.Invoke(null, [message]));
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
