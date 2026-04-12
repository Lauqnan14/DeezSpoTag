using System;
using System.Collections.Generic;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Shared;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EngineFallbackPlanPolicyTests
{
    [Fact]
    public void ShouldUseInEngineFallback_ReturnsFalse_WhenNextStepUsesDifferentEngine()
    {
        var payload = new QobuzQueueItem
        {
            AutoSources = new List<string>
            {
                "qobuz|27",
                "tidal|HI_RES_LOSSLESS",
                "apple|ALAC",
                "qobuz|7"
            },
            AutoIndex = 0,
            Quality = "27"
        };

        var allowed = EngineFallbackPlanPolicy.ShouldUseInEngineFallback(payload, "qobuz");

        Assert.False(allowed);
    }

    [Fact]
    public void ShouldUseInEngineFallback_UsesAutoSourcesOrder_WhenFallbackPlanIsStale()
    {
        var payload = new QobuzQueueItem
        {
            AutoSources = new List<string>
            {
                "qobuz|27",
                "tidal|HI_RES_LOSSLESS",
                "apple|ALAC",
                "qobuz|7"
            },
            AutoIndex = 0,
            Quality = "27",
            FallbackPlan = new List<FallbackPlanStep>
            {
                new("step-0", "qobuz", "27", Array.Empty<string>(), "direct_url"),
                new("step-1", "qobuz", "7", Array.Empty<string>(), "direct_url")
            }
        };

        var allowed = EngineFallbackPlanPolicy.ShouldUseInEngineFallback(payload, "qobuz");

        Assert.False(allowed);
    }

    [Fact]
    public void ShouldUseInEngineFallback_ReturnsTrue_WhenRemainingPlanIsSameEngineOnly()
    {
        var payload = new QobuzQueueItem
        {
            AutoSources = new List<string>
            {
                "qobuz|27",
                "qobuz|7",
                "qobuz|6"
            },
            AutoIndex = 0,
            Quality = "27"
        };

        var allowed = EngineFallbackPlanPolicy.ShouldUseInEngineFallback(payload, "qobuz");

        Assert.True(allowed);
    }
}
