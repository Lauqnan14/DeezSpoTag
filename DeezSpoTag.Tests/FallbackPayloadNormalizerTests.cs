using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class FallbackPayloadNormalizerTests
{
    private static readonly string[] ExpectedAppleVideoSource = { "apple|video" };
    private static readonly string[] ExpectedAppleAtmosSource = { "apple|ATMOS" };
    private static readonly string[] ExpectedAutoSourcePreferred = { "deezer|9", "tidal|LOSSLESS" };
    private static readonly string[] ExpectedCanonicalSources = { "qobuz|27", "tidal|LOSSLESS" };

    [Fact]
    public void ResolveCanonicalState_UsesSingleAppleVideoStep_ForVideoPayload()
    {
        var item = CreateQueueItem(contentType: DownloadContentTypes.Video);
        var payload = new JsonObject
        {
            ["Quality"] = "AAC"
        };

        var state = FallbackPayloadNormalizer.ResolveCanonicalState(item, new DeezSpoTagSettings(), payload);

        Assert.Equal(ExpectedAppleVideoSource, state.AutoSources);
        var step = Assert.Single(state.FallbackPlan);
        Assert.Equal("apple", step.Engine);
        Assert.Equal(DownloadContentTypes.Video, step.Quality);
        Assert.Equal("direct_url", step.ResolutionStrategy);
        Assert.Equal("apple", state.FirstStep.Source);
        Assert.Equal(DownloadContentTypes.Video, state.FirstStep.Quality);
    }

    [Fact]
    public void ResolveCanonicalState_UsesSingleAppleAtmosStep_ForAtmosPayload()
    {
        var item = CreateQueueItem(contentType: DownloadContentTypes.Stereo);
        var payload = new JsonObject
        {
            ["Quality"] = "ATMOS"
        };

        var state = FallbackPayloadNormalizer.ResolveCanonicalState(item, new DeezSpoTagSettings(), payload);

        Assert.Equal(ExpectedAppleAtmosSource, state.AutoSources);
        var step = Assert.Single(state.FallbackPlan);
        Assert.Equal("apple", step.Engine);
        Assert.Equal("ATMOS", step.Quality);
        Assert.Equal("direct_url", step.ResolutionStrategy);
        Assert.Equal("apple", state.FirstStep.Source);
        Assert.Equal("ATMOS", state.FirstStep.Quality);
    }

    [Fact]
    public void ResolveCanonicalState_PrefersAutoSources_OverMismatchedFallbackPlan()
    {
        var item = CreateQueueItem(engine: "deezer");
        var payload = new JsonObject
        {
            ["AutoSources"] = new JsonArray("deezer|9", "tidal|LOSSLESS"),
            ["FallbackPlan"] = new JsonArray(
                new JsonObject
                {
                    ["StepId"] = "step-0",
                    ["Engine"] = "qobuz",
                    ["Quality"] = "27",
                    ["RequiredInputs"] = new JsonArray(),
                    ["ResolutionStrategy"] = "direct_url"
                },
                new JsonObject
                {
                    ["StepId"] = "step-1",
                    ["Engine"] = "tidal",
                    ["Quality"] = "LOSSLESS",
                    ["RequiredInputs"] = new JsonArray(),
                    ["ResolutionStrategy"] = "direct_url"
                })
        };

        var state = FallbackPayloadNormalizer.ResolveCanonicalState(item, new DeezSpoTagSettings(), payload);

        Assert.Equal(ExpectedAutoSourcePreferred, state.AutoSources);
        Assert.Equal(2, state.FallbackPlan.Count);
        Assert.Equal("deezer", state.FirstStep.Source);
        Assert.Equal("9", state.FirstStep.Quality);
        Assert.Equal("deezer", state.FallbackPlan[0].Engine);
        Assert.Equal("9", state.FallbackPlan[0].Quality);
    }

    [Fact]
    public void ResolveCanonicalState_ReusesMatchingFallbackPlan_WhenItMatchesAutoSources()
    {
        var item = CreateQueueItem(engine: "qobuz");
        var payload = new JsonObject
        {
            ["AutoSources"] = new JsonArray("qobuz|27", "tidal|LOSSLESS"),
            ["FallbackPlan"] = new JsonArray(
                new JsonObject
                {
                    ["StepId"] = "custom-0",
                    ["Engine"] = "qobuz",
                    ["Quality"] = "27",
                    ["RequiredInputs"] = new JsonArray("ISRC"),
                    ["ResolutionStrategy"] = "isrc"
                },
                new JsonObject
                {
                    ["StepId"] = "custom-1",
                    ["Engine"] = "tidal",
                    ["Quality"] = "LOSSLESS",
                    ["RequiredInputs"] = new JsonArray("SpotifyId"),
                    ["ResolutionStrategy"] = "songlink_url"
                })
        };

        var state = FallbackPayloadNormalizer.ResolveCanonicalState(item, new DeezSpoTagSettings(), payload);

        Assert.Equal(new[] { "qobuz|27", "tidal|LOSSLESS" }, state.AutoSources);
        Assert.Equal("custom-0", state.FallbackPlan[0].StepId);
        Assert.Equal("isrc", state.FallbackPlan[0].ResolutionStrategy);
        Assert.Equal("custom-1", state.FallbackPlan[1].StepId);
        Assert.Equal("songlink_url", state.FallbackPlan[1].ResolutionStrategy);
    }

    [Fact]
    public void ApplyCanonicalState_ResetsRoutingAndHistory_WhenRequested()
    {
        var payload = new JsonObject
        {
            ["AutoSources"] = new JsonArray("deezer|9"),
            ["AutoIndex"] = 3,
            ["Engine"] = "deezer",
            ["SourceService"] = "deezer",
            ["FallbackHistory"] = new JsonArray(new JsonObject { ["status"] = "failed" }),
            ["FallbackQueuedExternally"] = true,
            ["Quality"] = "9"
        };
        var state = new FallbackPayloadNormalizer.CanonicalFallbackState(
            new List<string> { "qobuz|27", "tidal|LOSSLESS" },
            new List<FallbackPlanStep>
            {
                new("step-0", "qobuz", "27", Array.Empty<string>(), "direct_url"),
                new("step-1", "tidal", "LOSSLESS", Array.Empty<string>(), "direct_url")
            },
            new DownloadSourceOrder.AutoSourceStep("qobuz", "27"));

        var changed = FallbackPayloadNormalizer.ApplyCanonicalState(payload, state, resetIndexAndHistory: true);

        Assert.True(changed);
        Assert.Equal(ExpectedCanonicalSources, ReadStringArray(payload, "AutoSources"));
        Assert.Equal(0, payload["AutoIndex"]!.GetValue<int>());
        Assert.Equal("qobuz", payload["Engine"]!.GetValue<string>());
        Assert.Equal("qobuz", payload["SourceService"]!.GetValue<string>());
        Assert.Empty((JsonArray)payload["FallbackHistory"]!);
        Assert.False(payload["FallbackQueuedExternally"]!.GetValue<bool>());
        Assert.Equal("27", payload["Quality"]!.GetValue<string>());
        Assert.Equal(2, ((JsonArray)payload["FallbackPlan"]!).Count);
    }

    [Fact]
    public void BuildDirectUrlPlanFromAutoSources_BuildsIndexedCanonicalSteps()
    {
        var autoSources = new List<string>
        {
            "qobuz|27",
            "tidal|HI_RES_LOSSLESS",
            "deezer|9"
        };

        var plan = FallbackPayloadNormalizer.BuildDirectUrlPlanFromAutoSources(autoSources);

        Assert.Equal(3, plan.Count);
        Assert.Equal("step-0", plan[0].StepId);
        Assert.Equal("qobuz", plan[0].Engine);
        Assert.Equal("27", plan[0].Quality);
        Assert.Equal("direct_url", plan[0].ResolutionStrategy);
        Assert.Equal("step-1", plan[1].StepId);
        Assert.Equal("tidal", plan[1].Engine);
        Assert.Equal("HI_RES_LOSSLESS", plan[1].Quality);
        Assert.Equal("step-2", plan[2].StepId);
        Assert.Equal("deezer", plan[2].Engine);
        Assert.Equal("9", plan[2].Quality);
    }

    private static DownloadQueueItem CreateQueueItem(string engine = "deezer", string? contentType = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new DownloadQueueItem(
            Id: 1,
            QueueUuid: "queue-1",
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
            ContentType: contentType,
            Status: "queued",
            PayloadJson: null,
            Progress: null,
            Downloaded: null,
            Failed: null,
            Error: null,
            CreatedAt: now,
            UpdatedAt: now);
    }

    private static string[] ReadStringArray(JsonObject payload, string key)
    {
        if (payload[key] is not JsonArray array)
        {
            return Array.Empty<string>();
        }

        return array
            .Select(static value => value?.GetValue<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToArray();
    }
}
