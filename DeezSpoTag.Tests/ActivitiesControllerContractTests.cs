using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
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
}
