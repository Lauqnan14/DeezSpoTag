using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Fallback;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EngineFallbackCoordinatorParityTests
{
    private static readonly string[] ExpectedAutoThenFallbackSteps = { "deezer|9", "qobuz|27", "tidal|HI_RES_LOSSLESS" };
    private static readonly string[] ExpectedMixedSteps = { "qobuz|27", "deezer|9", "deezer|3", "tidal|LOSSLESS" };
    private static readonly string[] ExpectedAutoSteps = { "qobuz|27", "tidal|HI_RES_LOSSLESS", "deezer|9" };
    private static readonly string[] ExpectedAutoPlusFallbackPlanSteps = { "qobuz|27", "tidal|HI_RES_LOSSLESS", "apple|ALAC", "qobuz|7" };
    private static readonly string[] ExpectedForcedDeezerFallbackSteps = { "deezer|9", "deezer|3", "deezer|1" };

    [Fact]
    public void BuildPlanSteps_PrefersAutoSources_ThenAppendsFallbackPlan()
    {
        var fallbackPlan = new List<FallbackPlanStep>
        {
            new("step-0", "qobuz", "27", Array.Empty<string>(), "direct_url"),
            new("step-1", "tidal", "HI_RES_LOSSLESS", Array.Empty<string>(), "direct_url")
        };
        var settings = new DeezSpoTagSettings { Service = "auto" };

        var steps = BuildPlanSteps(fallbackPlan, new List<string> { "deezer|9" }, settings);

        Assert.Equal(ExpectedAutoThenFallbackSteps, steps);
    }

    [Fact]
    public void BuildPlanSteps_UsesAutoSourcesOrder_WhenFallbackPlanIsStale()
    {
        var fallbackPlan = new List<FallbackPlanStep>
        {
            new("step-0", "qobuz", "27", Array.Empty<string>(), "direct_url"),
            new("step-1", "qobuz", "7", Array.Empty<string>(), "direct_url")
        };
        var autoSources = new List<string>
        {
            "qobuz|27",
            "tidal|HI_RES_LOSSLESS",
            "apple|ALAC"
        };
        var settings = new DeezSpoTagSettings { Service = "auto" };

        var steps = BuildPlanSteps(fallbackPlan, autoSources, settings);

        Assert.Equal(ExpectedAutoPlusFallbackPlanSteps, steps);
    }

    [Fact]
    public void BuildPlanSteps_UsesPayloadSourcesEvenWhenCurrentServiceIsForced()
    {
        var autoSources = new List<string>
        {
            "qobuz|27",
            "deezer|9",
            "deezer|3",
            "tidal|LOSSLESS"
        };
        var settings = new DeezSpoTagSettings { Service = "deezer" };

        var steps = BuildPlanSteps(new List<FallbackPlanStep>(), autoSources, settings);

        Assert.Equal(ExpectedMixedSteps, steps);
    }

    [Fact]
    public void BuildPlanSteps_FallsBackToForcedService_WhenPayloadHasNoPlan()
    {
        var settings = new DeezSpoTagSettings { Service = "deezer" };

        var steps = BuildPlanSteps(new List<FallbackPlanStep>(), new List<string>(), settings);

        Assert.Equal(ExpectedForcedDeezerFallbackSteps, steps);
    }

    [Fact]
    public void BuildPlanSteps_UsesAutoSourcesWhenServiceIsAuto()
    {
        var autoSources = new List<string>
        {
            "qobuz|27",
            "tidal|HI_RES_LOSSLESS",
            "deezer|9"
        };
        var settings = new DeezSpoTagSettings { Service = "auto" };

        var steps = BuildPlanSteps(new List<FallbackPlanStep>(), autoSources, settings);

        Assert.Equal(ExpectedAutoSteps, steps);
    }

    [Fact]
    public void FindStepIndex_UsesEngineAndQuality_ForRetryResumeProgress()
    {
        var autoSources = new List<string>
        {
            "qobuz|27",
            "deezer|9",
            "deezer|3"
        };
        var settings = new DeezSpoTagSettings { Service = "auto" };
        var planSteps = InvokeBuildPlanSteps(new List<FallbackPlanStep>(), autoSources, settings);

        var index = InvokeFindStepIndex(planSteps, "deezer", "3");

        Assert.Equal(2, index);
    }

    [Fact]
    public void TryBuildAppleFallbackUrl_UsesAppleId_WhenAppleStepHasNoResolvedUrl()
    {
        var url = InvokeTryBuildAppleFallbackUrl(
            engine: "apple",
            sourceUrl: "https://www.deezer.com/track/123",
            spotifyId: "spid",
            appleId: "1440857781",
            isrc: null,
            deezerId: "123",
            userCountry: "us",
            fallbackSearchEnabled: true);

        Assert.Equal("https://music.apple.com/us/song/1440857781?i=1440857781", url);
    }

    [Fact]
    public void TryBuildAppleFallbackUrl_BuildsStationUrl_ForStationIds()
    {
        var url = InvokeTryBuildAppleFallbackUrl(
            engine: "apple",
            sourceUrl: string.Empty,
            spotifyId: string.Empty,
            appleId: "ra.1234abcd",
            isrc: null,
            deezerId: string.Empty,
            userCountry: "us",
            fallbackSearchEnabled: false);

        Assert.Equal("https://music.apple.com/us/station/ra.1234abcd", url);
    }

    private static List<string> BuildPlanSteps(
        List<FallbackPlanStep> fallbackPlan,
        List<string> autoSources,
        DeezSpoTagSettings settings)
    {
        var result = InvokeBuildPlanSteps(fallbackPlan, autoSources, settings);
        Assert.IsAssignableFrom<System.Collections.IEnumerable>(result);
        var enumerable = (System.Collections.IEnumerable)result;
        return enumerable.Cast<object>().Select(ToStepString).ToList();
    }

    private static object InvokeBuildPlanSteps(
        List<FallbackPlanStep> fallbackPlan,
        List<string> autoSources,
        DeezSpoTagSettings settings)
    {
        var method = typeof(EngineFallbackCoordinator).GetMethod(
            "BuildPlanSteps",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { fallbackPlan, autoSources, settings });
        Assert.NotNull(result);
        return result!;
    }

    private static int InvokeFindStepIndex(object planSteps, string engine, string quality)
    {
        var method = typeof(EngineFallbackCoordinator).GetMethod(
            "FindStepIndex",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new[] { planSteps, engine, quality });
        Assert.NotNull(result);
        return (int)result!;
    }

    private static string ToStepString(object step)
    {
        var type = step.GetType();
        var source = type.GetField("Item1")?.GetValue(step)?.ToString()
            ?? type.GetProperty("Source")?.GetValue(step)?.ToString()
            ?? string.Empty;
        var quality = type.GetField("Item2")?.GetValue(step)?.ToString()
            ?? type.GetProperty("Quality")?.GetValue(step)?.ToString()
            ?? string.Empty;
        return string.IsNullOrWhiteSpace(quality) ? source : $"{source}|{quality}";
    }

    private static string? InvokeTryBuildAppleFallbackUrl(
        string engine,
        string sourceUrl,
        string spotifyId,
        string appleId,
        string? isrc,
        string deezerId,
        string userCountry,
        bool fallbackSearchEnabled)
    {
        var coordinatorType = typeof(EngineFallbackCoordinator);
        var requestType = coordinatorType.GetNestedType("SourceResolutionRequest", BindingFlags.NonPublic);
        Assert.NotNull(requestType);

        var request = Activator.CreateInstance(
            requestType!,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args:
            [
                engine,
                sourceUrl,
                spotifyId,
                appleId,
                isrc,
                deezerId,
                userCountry,
                fallbackSearchEnabled
            ],
            culture: null);
        Assert.NotNull(request);

        var method = coordinatorType.GetMethod("TryBuildAppleFallbackUrl", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [request]);
        return result as string;
    }
}
