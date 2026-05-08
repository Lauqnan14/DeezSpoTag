using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using DeezSpoTag.Services.Download.Queue;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class QueuePreResolutionPlannerTests
{
    [Fact]
    public void SelectNext_UsesFirstUnresolvedItemInsideTenItemWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var tasks = new List<DownloadQueueItem>();
        for (var i = 1; i <= 12; i++)
        {
            tasks.Add(CreateItem(i, i <= 9 ? ResolvedPayload() : "{}"));
        }

        var selected = QueuePreResolutionPlanner.SelectNext(
            tasks,
            "fifo",
            10,
            TimeSpan.FromMinutes(2),
            now);

        Assert.NotNull(selected);
        Assert.Equal("queue-10", selected!.QueueUuid);
    }

    [Fact]
    public void SelectNext_WhenFirstResolvedItemLeavesQueuedWindow_SelectsEleventhItem()
    {
        var now = DateTimeOffset.UtcNow;
        var tasks = new List<DownloadQueueItem>();
        tasks.Add(CreateItem(1, ResolvedPayload(), status: "running"));
        for (var i = 2; i <= 11; i++)
        {
            tasks.Add(CreateItem(i, i <= 10 ? ResolvedPayload() : "{}"));
        }

        var selected = QueuePreResolutionPlanner.SelectNext(
            tasks,
            "fifo",
            10,
            TimeSpan.FromMinutes(2),
            now);

        Assert.NotNull(selected);
        Assert.Equal("queue-11", selected!.QueueUuid);
    }

    [Fact]
    public void SelectNext_SkipsResolvingAndFailedItemsOnCooldown()
    {
        var now = DateTimeOffset.UtcNow;
        var failedPayload = new JsonObject();
        QueuePreResolutionPayload.ApplyFailed(failedPayload, "temporary", now.AddSeconds(-30));
        var tasks = new List<DownloadQueueItem>
        {
            CreateItem(1, ResolvingPayload()),
            CreateItem(2, failedPayload.ToJsonString()),
            CreateItem(3, "{}")
        };

        var selected = QueuePreResolutionPlanner.SelectNext(
            tasks,
            "fifo",
            10,
            TimeSpan.FromMinutes(2),
            now);

        Assert.NotNull(selected);
        Assert.Equal("queue-3", selected!.QueueUuid);
    }

    private static string ResolvedPayload()
    {
        var payload = new JsonObject();
        QueuePreResolutionPayload.ApplyResolved(
            payload,
            new QueuePreResolutionPayload.ResolutionResult(
                "deezer",
                "https://www.deezer.com/track/1",
                "9",
                0,
                null,
                null),
            DateTimeOffset.UtcNow);
        return payload.ToJsonString();
    }

    private static string ResolvingPayload()
    {
        var payload = new JsonObject();
        QueuePreResolutionPayload.MarkResolving(payload, DateTimeOffset.UtcNow);
        return payload.ToJsonString();
    }

    private static DownloadQueueItem CreateItem(int order, string payloadJson, string status = "queued")
        => new(
            Id: order,
            QueueUuid: $"queue-{order}",
            Engine: "deezer",
            ArtistName: "Artist",
            TrackTitle: $"Track {order}",
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
            DurationMs: 180000,
            DestinationFolderId: null,
            QualityRank: null,
            QueueOrder: order,
            ContentType: "stereo",
            Status: status,
            PayloadJson: payloadJson,
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow.AddSeconds(order),
            UpdatedAt: DateTimeOffset.UtcNow.AddSeconds(order));
}
