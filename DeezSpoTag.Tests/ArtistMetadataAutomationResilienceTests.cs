using System;
using System.IO;
using System.Linq;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistMetadataAutomationResilienceTests
{
    [Fact]
    public void ManualRunsUseARealCancellationTokenNotNone()
    {
        var source = ReadCoordinator();
        var start = source.IndexOf("private async Task RunManualOperationAsync", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = source[start..(start + 2500)];

        Assert.Contains("await run(cts.Token)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("run(CancellationToken.None)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationIsLinkedToShutdownSoTheAppCanStopARun()
    {
        var source = ReadCoordinator();

        Assert.Contains("CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken)", source, StringComparison.Ordinal);
        Assert.Contains("public bool Cancel()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelEndpointExists()
    {
        var controller = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Controllers", "Api", "ArtistMetadataAutomationApiController.cs"));

        Assert.Contains("[HttpPost(\"cancel\")]", controller, StringComparison.Ordinal);
        Assert.Contains("coordinator.Cancel()", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualRunFailuresAreObservedInsteadOfSwallowed()
    {
        var source = ReadCoordinator();
        var start = source.IndexOf("private async Task RunManualOperationAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private void RecordOperationFailure", start, StringComparison.Ordinal);
        var body = source[start..end];

        Assert.Contains("catch (OperationCanceledException)", body, StringComparison.Ordinal);
        Assert.Contains("ExpectedExceptionPolicy.IsRecoverable(ex)", body, StringComparison.Ordinal);
        Assert.Contains("RecordOperationFailure", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduleLoopSurvivesAStrayCancellationException()
    {
        var source = ReadCoordinator();
        var start = source.IndexOf("protected override async Task ExecuteAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private static async Task<bool> DelayOrStopAsync", start, StringComparison.Ordinal);
        var body = source[start..end];

        Assert.Contains("catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)", body, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException ex)", body, StringComparison.Ordinal);
        Assert.Contains("ExpectedExceptionPolicy.IsRecoverable(ex)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (Exception ex) when (ex is not OperationCanceledException)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void InterruptedRunsResumeOnStartup()
    {
        var source = ReadCoordinator();

        Assert.Contains("await ResumeInterruptedRunAsync(stoppingToken);", source, StringComparison.Ordinal);
        Assert.Contains("state.ActiveRun", source, StringComparison.Ordinal);
        Assert.Contains("run.CompletedArtistIds", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BothOperationsPersistCompletedArtistsAndSkipThemOnResume()
    {
        var coordinator = ReadCoordinator();
        var cache = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistMetadataCacheRefreshService.cs"));
        var updater = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistMetadataUpdaterService.cs"));

        Assert.Contains("CheckpointCompletedIds()", coordinator, StringComparison.Ordinal);
        Assert.Contains("NoteArtistCompleted(value.CompletedArtistId)", coordinator, StringComparison.Ordinal);
        Assert.Contains("completedArtistIds is null || !completedArtistIds.Contains(artist.Id)", cache, StringComparison.Ordinal);
        Assert.Contains("completedArtistIds is not null && completedArtistIds.Contains(tracked.ArtistId)", updater, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveRunIsClearedWhenAnOperationFinishes()
    {
        var source = ReadCoordinator();

        Assert.Contains("state.ActiveRun = null;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduledRunsShareTheManualEnqueuePathRatherThanDuplicatingIt()
    {
        var source = ReadCoordinator();
        var start = source.IndexOf("private async Task RunScheduledOperationsAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task WaitForActiveOperationAsync", start, StringComparison.Ordinal);
        var body = source[start..end];

        Assert.Contains("await EnqueueAsync(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_operationGate.WaitAsync", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtistNameIsNotRenderedTwice()
    {
        var updater = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistMetadataUpdaterService.cs"));

        Assert.Contains("Phase = \"Updating artists\"", updater, StringComparison.Ordinal);
        Assert.DoesNotContain("Phase = $\"Updating {artistName}\"", updater, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentArtistIsAlwaysRendered()
    {
        var view = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));

        Assert.Contains("`Current Artist: ${currentArtist}`", view, StringComparison.Ordinal);
        Assert.DoesNotContain("if (running && currentArtist)", view, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelButtonIsDrivenByStatusNotHardcodedDisabled()
    {
        var view = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));

        Assert.Contains("metadataCancel.disabled = !metadataRunning;", view, StringComparison.Ordinal);
        Assert.DoesNotContain("cancellation is not available yet", view, StringComparison.Ordinal);
        Assert.Contains("id=\"metadata-cancel-button\" class=\"action-btn action-btn-sm\" type=\"button\">Cancel<", view, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressTicksDoNotRebuildTheWholeStatusSnapshot()
    {
        var source = ReadCoordinator();
        var start = source.IndexOf("private void UpdateCacheProgress", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = source[start..(start + 700)];

        Assert.DoesNotContain("GetStatus()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void LateProgressCallbacksCannotResurrectAFinishedRun()
    {
        var source = ReadCoordinator();
        var start = source.IndexOf("private void UpdateCacheProgress", StringComparison.Ordinal);
        var body = source[start..(start + 800)];

        Assert.Contains("if (!_status.CacheRefresh.Running)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveOperation = \"cache-refresh\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BiographyProvidersAreQueriedConcurrently()
    {
        var cache = ReadCacheRefresh();

        Assert.Contains("await Task.WhenAll(requestedProviders", cache, StringComparison.Ordinal);
        Assert.DoesNotContain("var biography = await ResolveBiographyAsync(", cache, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderOrderStillDecidesTheSelectedBiography()
    {
        var cache = ReadCacheRefresh();

        Assert.Contains("biographies.Add((requestedProviders[index], biography!));", cache, StringComparison.Ordinal);
        Assert.Contains("var selectedBiographyProvider = biographies.FirstOrDefault().Provider;", cache, StringComparison.Ordinal);
    }

    private static string ReadCacheRefresh()
        => File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistMetadataCacheRefreshService.cs"));

    [Fact]
    public void FinishedRunKeepsShowingItsOwnOutcomeInsteadOfTheIdleOperation()
    {
        var view = ReadActivitiesView();
        var start = view.IndexOf("function pickMetadataStatus", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = view[start..(start + 900)];

        Assert.Contains("cacheFinished > targetFinished", body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "automationStatus?.activeOperation === 'cache-refresh'\n                ? automationStatus?.cacheRefresh",
            view,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedRunShowsSucceededAndFailedCounts()
    {
        var view = ReadActivitiesView();

        Assert.Contains("if (!running && message) {", view, StringComparison.Ordinal);
    }

    [Fact]
    public void CacheRefreshReportsAnOutcomeMessage()
    {
        var coordinator = ReadCoordinator();

        Assert.Contains("result.Error is null ? \"Cache refresh completed\" : \"Cache refresh failed\"", coordinator, StringComparison.Ordinal);
        Assert.Contains("$\"{result.Succeeded} succeeded, {result.Failed} failed.\"", coordinator, StringComparison.Ordinal);
    }

    private static string ReadActivitiesView()
        => File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));

    private static string ReadCoordinator()
        => File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistMetadataAutomationCoordinator.cs"));

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
