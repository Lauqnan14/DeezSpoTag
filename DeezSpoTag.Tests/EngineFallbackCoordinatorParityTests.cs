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
    private static readonly string[] ExpectedExplicitFallbackSteps = { "qobuz|27", "tidal|HI_RES_LOSSLESS" };
    private static readonly string[] ExpectedForcedDeezerSteps = { "deezer|9", "deezer|3" };
    private static readonly string[] ExpectedAutoSteps = { "qobuz|27", "tidal|HI_RES_LOSSLESS", "deezer|9" };

    [Fact]
    public void BuildPlanSteps_PrefersExplicitFallbackPlan_WhenPresent()
    {
        var fallbackPlan = new List<FallbackPlanStep>
        {
            new("step-0", "qobuz", "27", Array.Empty<string>(), "direct_url"),
            new("step-1", "tidal", "HI_RES_LOSSLESS", Array.Empty<string>(), "direct_url")
        };
        var settings = new DeezSpoTagSettings { Service = "auto" };

        var steps = BuildPlanSteps(fallbackPlan, new List<string> { "deezer|9" }, settings);

        Assert.Equal(ExpectedExplicitFallbackSteps, steps);
    }

    [Fact]
    public void BuildPlanSteps_FiltersToForcedEngine_WhenServiceIsNotAuto()
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

        Assert.Equal(ExpectedForcedDeezerSteps, steps);
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
}
