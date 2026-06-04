using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Web.Controllers;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ActivitiesControllerContractTests
{
    [Fact]
    public void CancelDownloadRequest_RejectsEmptyUuid()
    {
        var request = new CancelDownloadRequest { Uuid = string.Empty };
        var validationResults = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            validationResults,
            validateAllProperties: true);

        Assert.False(isValid);
    }

    [Fact]
    public void BuildQueuePayload_IncludesPersistedErrorField()
    {
        var buildQueuePayload = typeof(ActivitiesController).GetMethod(
            "BuildQueuePayload",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(buildQueuePayload);

        var queueItem = new DownloadQueueItem(
            Id: 1,
            QueueUuid: "task-1",
            Engine: "deezer",
            ArtistName: "Artist",
            TrackTitle: "Track",
            Isrc: null,
            DeezerTrackId: null,
            DeezerAlbumId: null,
            DeezerArtistId: null,
            SpotifyTrackId: null,
            SpotifyAlbumId: null,
            SpotifyArtistId: null,
            AppleTrackId: null,
            AppleAlbumId: null,
            AppleArtistId: null,
            DurationMs: null,
            DestinationFolderId: null,
            QualityRank: null,
            QueueOrder: null,
            Status: "failed",
            PayloadJson: "{}",
            Progress: null,
            Downloaded: null,
            Failed: null,
            Error: "Network timeout",
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        var payload = (Dictionary<string, object>)buildQueuePayload!.Invoke(
            null,
            [queueItem, new DeezSpoTagSettings()])!;

        Assert.True(payload.TryGetValue("error", out var error));
        Assert.Equal("Network timeout", error);
    }

    [Fact]
    public void MapStatusForUi_MapsSkippedToCompleted()
    {
        var mapStatusForUi = typeof(ActivitiesController).GetMethod(
            "MapStatusForUi",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(mapStatusForUi);

        var mapped = mapStatusForUi!.Invoke(null, ["skipped"]) as string;
        Assert.Equal("completed", mapped);
    }

    [Fact]
    public void MapStatusForUi_KeepsRetryingActionable()
    {
        var mapStatusForUi = typeof(ActivitiesController).GetMethod(
            "MapStatusForUi",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(mapStatusForUi);

        var mapped = mapStatusForUi!.Invoke(null, ["retrying"]) as string;
        Assert.Equal("retrying", mapped);
    }

    [Fact]
    public void ActivitiesDownloadsTab_HandlesQueueRemovalEvents()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));

        Assert.Contains("connection.on('removedFromQueue'", source, StringComparison.Ordinal);
        Assert.Contains("connection.on('removedAllDownloads'", source, StringComparison.Ordinal);
        Assert.Contains("connection.on('removedFinishedDownloads'", source, StringComparison.Ordinal);
        Assert.Contains("connection.on('addedToQueue'", source, StringComparison.Ordinal);
        Assert.Contains("refreshQueueViewState", source, StringComparison.Ordinal);
        Assert.Contains("isActiveQueueTask", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesDeleteFailed_EmitsRemovedFromQueue()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Controllers/ActivitiesController.cs"));

        Assert.Contains("_deezspotagListener.SendRemovedFromQueue(request.Uuid);", source, StringComparison.Ordinal);
        Assert.Contains("MarkActivitiesClearedByUuidAsync(request.Uuid", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadScript_DoesNotOwnQueueUiOutsideActivities()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/wwwroot/js/download.js"));

        Assert.DoesNotContain("deezerQueueHub", source, StringComparison.Ordinal);
        Assert.DoesNotContain("cancelDownload(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("retryDownload(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("download-queue", source, StringComparison.Ordinal);
        Assert.DoesNotContain("queue-list", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesDownloadsTab_AllowsCancelDuringRetrying()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));

        Assert.Contains("allowDuringRetry === true", source, StringComparison.Ordinal);
        Assert.Contains("statusForUi === 'retrying'", source, StringComparison.Ordinal);
        Assert.Contains("beginTaskAction(taskId, { allowDuringRetry: true })", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PauseQueuedAsync_CoversAllPendingActiveStatuses()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Queue/DownloadQueueRepository.cs"));

        Assert.Contains("lower(status) IN ('queued', 'inqueue', 'resolving', 'retrying')", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovedAllDownloadsEvent_AlwaysSendsPayload()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Shared/Models/IDeezSpoTagListener.cs"));

        Assert.Contains("Send(\"removedAllDownloads\", new { currentItem });", source, StringComparison.Ordinal);
    }
}
