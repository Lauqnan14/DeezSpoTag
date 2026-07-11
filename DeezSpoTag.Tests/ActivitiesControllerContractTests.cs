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
            PayloadJson: """
                {
                  "PrefetchArtworkStatus": "fetching",
                  "PrefetchLyricsStatus": "fetching",
                  "PrefetchLyricsType": "time-synced"
                }
                """,
            Progress: null,
            Downloaded: null,
            Failed: null,
            Error: "Network timeout",
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        var payload = (Dictionary<string, object>)buildQueuePayload!.Invoke(
            null,
            [queueItem, new DeezSpoTagSettings(), new HashSet<string>(StringComparer.OrdinalIgnoreCase)])!;

        Assert.True(payload.TryGetValue("error", out var error));
        Assert.Equal("Network timeout", error);
        Assert.Equal("fetching", payload["prefetchArtworkStatus"]);
        Assert.Equal("fetching", payload["prefetchLyricsStatus"]);
        Assert.Equal("time-synced", payload["prefetchLyricsType"]);
    }

    [Fact]
    public void MapStatusForUi_MapsSkippedToCompleted()
    {
        var mapStatusForUi = typeof(ActivitiesController).GetMethod(
            "MapStatusForUi",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(mapStatusForUi);

        var mapped = mapStatusForUi!.Invoke(null, ["skipped"]) as string;
        Assert.Equal("complete", mapped);
    }

    [Theory]
    [InlineData("completed", "complete")]
    [InlineData("complete", "complete")]
    [InlineData("finished", "complete")]
    [InlineData("download finished", "complete")]
    [InlineData("done", "complete")]
    [InlineData("success", "complete")]
    [InlineData("failed", "failed")]
    [InlineData("error", "failed")]
    [InlineData("canceled", "canceled")]
    [InlineData("cancelled", "canceled")]
    [InlineData("resolving", "queued")]
    [InlineData("inqueue", "queued")]
    [InlineData("downloading", "running")]
    public void MapStatusForUi_ReturnsCanonicalActivityStatus(string rawStatus, string expectedStatus)
    {
        var mapStatusForUi = typeof(ActivitiesController).GetMethod(
            "MapStatusForUi",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(mapStatusForUi);

        var mapped = mapStatusForUi!.Invoke(null, [rawStatus]) as string;
        Assert.Equal(expectedStatus, mapped);
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
    public void ActivitiesDownloadsTab_RendersActionsFromBackendFlags()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));

        Assert.Contains("canPause: getQueueBoolFlag(item, 'canPause', 'CanPause')", source, StringComparison.Ordinal);
        Assert.Contains("const canCancel = task.canCancel === true", source, StringComparison.Ordinal);
        Assert.Contains("const canRetry = task.canRetry === true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("clearVisibleQueueTasks(isCompletedQueueTask);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("clearVisibleQueueTasks(isCanceledQueueTask);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesDownloadsTab_ResetsProgressCacheWhenRetryResetsProgress()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));

        Assert.Contains("function resetQueueProgressCache(taskId)", source, StringComparison.Ordinal);
        Assert.Contains("resetQueueProgressCache(taskId);", source, StringComparison.Ordinal);
        Assert.Contains("resetQueueProgressCache(updatedId);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesDownloadsTab_LyricsBadgesUseCompletedSidecarEvidenceOnly()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));

        Assert.Contains("mappedStatus === 'complete' ? getLyricsBadgesFromFiles(item) : []", source, StringComparison.Ordinal);
        Assert.Contains("lyricsBadges: Array.isArray(incoming.lyricsBadges) ? incoming.lyricsBadges : []", source, StringComparison.Ordinal);
        Assert.Contains("name.endsWith('.ttml')", source, StringComparison.Ordinal);
        Assert.Contains("name.endsWith('.lrc')", source, StringComparison.Ordinal);
        Assert.Contains("name.endsWith('.txt')", source, StringComparison.Ordinal);
        Assert.DoesNotContain("function getLyricsBadgesFromStatus", source, StringComparison.Ordinal);
        Assert.DoesNotContain("existing.lyricsBadges", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTag_ProtectsExistingAlbumFromLossyPlatformMatches()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Services/AutoTag/LocalAutoTagRunner.cs"));

        Assert.Contains("ApplyAlbumLossyOverwriteGuard(effectiveTagSettings, sourceTrack, file.Tag.Album);", source, StringComparison.Ordinal);
        Assert.Contains("sourceTrack.Album = currentAlbum;", source, StringComparison.Ordinal);
        Assert.Contains("effectiveTagSettings.Album = false;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadEngines_UseCanonicalRunningStartEvent()
    {
        var queueHelperSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Queue/QueueHelperUtils.cs"));
        var deezerSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Deezer/DeezerEngineProcessor.cs"));
        var qobuzSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Qobuz/QobuzEngineProcessor.cs"));
        var appleSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Apple/AppleEngineProcessor.cs"));
        var sharedSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Shared/EngineAudioPostDownloadHelper.cs"));

        Assert.Contains("SendRunningStartedAsync", queueHelperSource, StringComparison.Ordinal);
        Assert.Contains("status = \"running\"", queueHelperSource, StringComparison.Ordinal);
        Assert.Contains("progress = 0", queueHelperSource, StringComparison.Ordinal);
        Assert.Contains("QueueHelperUtils.SendRunningStartedAsync", deezerSource, StringComparison.Ordinal);
        Assert.Contains("QueueHelperUtils.SendRunningStartedAsync", qobuzSource, StringComparison.Ordinal);
        Assert.Contains("QueueHelperUtils.SendRunningStartedAsync", appleSource, StringComparison.Ordinal);
        Assert.Contains("QueueHelperUtils.SendRunningStartedAsync", sharedSource, StringComparison.Ordinal);
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
    public void DownloadClient_DoesNotOwnQueueRealtimeConnectionOrQueueUi()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/wwwroot/js/download-client.js"));
        var layoutSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Shared/_Layout.cshtml"));

        Assert.Contains("DeezSpoTag.DownloadClient", source, StringComparison.Ordinal);
        Assert.Contains("globalThis.DeezSpoTagDownload = DeezSpoTag.DownloadClient", source, StringComparison.Ordinal);
        Assert.Contains("addToQueue(url", source, StringComparison.Ordinal);
        Assert.Contains("addMultipleToQueue(urls", source, StringComparison.Ordinal);
        Assert.Contains("ensureDestinationSelects()", source, StringComparison.Ordinal);
        Assert.Contains("getDestinationFolderId(requireSelection", source, StringComparison.Ordinal);
        Assert.Contains("~/js/download-client.js", layoutSource, StringComparison.Ordinal);
        Assert.DoesNotContain("~/js/download.js", layoutSource, StringComparison.Ordinal);
        Assert.DoesNotContain("/deezerQueueHub", source, StringComparison.Ordinal);
        Assert.DoesNotContain("connection.on(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("cancelDownload(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("retryDownload(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("download-queue", source, StringComparison.Ordinal);
        Assert.DoesNotContain("queue-list", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesDownloadsTab_OwnsQueueRealtimeConnection()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));

        Assert.Contains(".withUrl('/deezerQueueHub')", source, StringComparison.Ordinal);
        Assert.Contains("connection.on('updateQueue'", source, StringComparison.Ordinal);
        Assert.Contains("connection.on('downloadProgress'", source, StringComparison.Ordinal);
        Assert.Contains("connection.on('startDownload'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DeezSpoTagDownload?.connection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DeezSpoTag?.Download?.connection", source, StringComparison.Ordinal);
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
