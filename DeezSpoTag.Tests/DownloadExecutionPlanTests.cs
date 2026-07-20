using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadExecutionPlanTests
{
    [Fact]
    public void SingleEnginePlan_ContainsOnlySelectedEngineAndHonorsQualityFallbackToggle()
    {
        var settings = new DeezSpoTagSettings
        {
            Service = "qobuz",
            FallbackBitrate = true
        };

        var fallback = DownloadSourceOrder.ResolveFallbackPlanSources(
            settings,
            [],
            "qobuz",
            "27",
            strict: false,
            includeDeezer: true);
        var strict = DownloadSourceOrder.ResolveFallbackPlanSources(
            settings,
            [],
            "qobuz",
            "27",
            strict: true,
            includeDeezer: true);

        Assert.Equal(["qobuz|27", "qobuz|7", "qobuz|6", "qobuz|5"], fallback);
        Assert.Equal(["qobuz|27"], strict);
    }

    [Fact]
    public void TidalSingleEnginePlan_KeepsStereoQualitiesSeparated()
    {
        var settings = new DeezSpoTagSettings
        {
            Service = "tidal",
            FallbackBitrate = true
        };

        var maxPlan = DownloadSourceOrder.ResolveFallbackPlanSources(
            settings,
            [],
            "tidal",
            "HI_RES_LOSSLESS",
            strict: false,
            includeDeezer: true);
        var hiResPlan = DownloadSourceOrder.ResolveFallbackPlanSources(
            settings,
            [],
            "tidal",
            "HI_RES",
            strict: false,
            includeDeezer: true);
        var cdPlan = DownloadSourceOrder.ResolveFallbackPlanSources(
            settings,
            [],
            "tidal",
            "LOSSLESS",
            strict: false,
            includeDeezer: true);

        Assert.Equal(["tidal|HI_RES_LOSSLESS", "tidal|HI_RES", "tidal|LOSSLESS", "tidal|HIGH", "tidal|LOW"], maxPlan);
        Assert.Equal(["tidal|HI_RES", "tidal|LOSSLESS", "tidal|HIGH", "tidal|LOW"], hiResPlan);
        Assert.Equal(["tidal|LOSSLESS", "tidal|HIGH", "tidal|LOW"], cdPlan);
    }

    [Fact]
    public void TidalStrictPlan_KeepsOnlyTheSelectedStereoQuality()
    {
        var settings = new DeezSpoTagSettings
        {
            Service = "tidal",
            FallbackBitrate = false
        };

        var maxPlan = DownloadSourceOrder.ResolveFallbackPlanSources(
            settings,
            [],
            "tidal",
            "HI_RES_LOSSLESS",
            strict: true,
            includeDeezer: true);
        var hiResPlan = DownloadSourceOrder.ResolveFallbackPlanSources(
            settings,
            [],
            "tidal",
            "HI_RES",
            strict: true,
            includeDeezer: true);

        Assert.Equal(["tidal|HI_RES_LOSSLESS"], maxPlan);
        Assert.Equal(["tidal|HI_RES"], hiResPlan);
    }

    [Fact]
    public void TidalPersistedPlan_PreservesMaxAndHiResAsDistinctSteps()
    {
        var plan = DownloadExecutionPlan.FromEncodedSources([
            "tidal|HI_RES_LOSSLESS",
            "tidal|HI_RES",
            "tidal|LOSSLESS",
            "tidal|HIGH",
            "tidal|LOW"
        ]);

        Assert.Equal(
            new[] { "HI_RES_LOSSLESS", "HI_RES", "LOSSLESS", "HIGH", "LOW" },
            plan.Select(step => step.Quality).ToArray());
    }

    [Fact]
    public void AutoAndCustomShareCanonicalPriorityWhileCustomOnlyFiltersSelections()
    {
        var auto = new DeezSpoTagSettings { Service = "auto" };
        var custom = new DeezSpoTagSettings
        {
            Service = "custom",
            DownloadEngineOrder = DownloadEngineOrderSettings.CreateDefault()
        };
        custom.DownloadEngineOrder.Enabled = true;
        foreach (var engine in custom.DownloadEngineOrder.Engines)
        {
            engine.Enabled = engine.Engine is "tidal" or "deezer";
            foreach (var quality in engine.Qualities)
            {
                quality.Enabled = quality.Quality is "LOSSLESS" or "9";
            }
        }

        var autoSources = DownloadSourceOrder.ResolveQualityAutoSources(auto, true, null);
        var customSources = DownloadSourceOrder.ResolveQualityAutoSources(custom, true, null);

        Assert.Equal(["tidal|LOSSLESS", "deezer|9"], customSources);
        Assert.True(autoSources.IndexOf("tidal|LOSSLESS") < autoSources.IndexOf("deezer|9"));
    }

    [Fact]
    public void PersistedPlan_IsTheOnlyRuntimePlanPayload()
    {
        var plan = DownloadExecutionPlan.FromEncodedSources(["qobuz|27", "tidal|HI_RES_LOSSLESS"]);
        var payload = new JsonObject
        {
            ["FallbackPlan"] = JsonSerializer.SerializeToNode(plan)
        };

        var restored = DownloadExecutionPlan.Read(payload);

        Assert.Equal(2, restored.Count);
        Assert.Equal("qobuz", restored[0].Engine);
        Assert.Equal("tidal", restored[1].Engine);
        Assert.Null(payload["AutoSources"]);
        Assert.Null(payload["FallbackQueuedExternally"]);
    }

    [Fact]
    public void PersistedPlan_ResumesAtCurrentEngineAndQualityInsteadOfRestarting()
    {
        var plan = new List<FallbackPlanStep>
        {
            new("step-0", "qobuz", "27", [], "direct_url"),
            new("step-1", "qobuz", "7", [], "direct_url"),
            new("step-2", "tidal", "HI_RES_LOSSLESS", [], "direct_url"),
            new("step-3", "deezer", "9", [], "direct_url")
        };
        var payload = new JsonObject
        {
            ["AutoIndex"] = 2,
            ["Quality"] = "HI_RES_LOSSLESS"
        };
        var item = CreateQueueItem("tidal");

        var index = DownloadIntentService.ResolveSavedPlanStartIndex(item, payload, plan);

        Assert.Equal(2, index);
    }

    private static DownloadQueueItem CreateQueueItem(string engine) => new(
        Id: 1,
        QueueUuid: "plan-resume",
        Engine: engine,
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
        ContentType: "track",
        FinalizationStatus: null,
        EnrichmentStatus: null,
        Status: "queued",
        PayloadJson: null,
        Progress: 0,
        Downloaded: 0,
        Failed: 0,
        Error: null,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);
}
