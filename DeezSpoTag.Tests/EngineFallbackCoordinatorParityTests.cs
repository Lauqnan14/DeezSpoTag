using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EngineFallbackCoordinatorParityTests
{
    private static readonly string[] ExpectedAutoThenFallbackSteps =
    {
        "deezer|9",
        "qobuz|27",
        "tidal|HI_RES_LOSSLESS"
    };
    private static readonly string[] ExpectedMixedSteps = { "qobuz|27", "deezer|9", "deezer|3", "tidal|LOSSLESS" };
    private static readonly string[] ExpectedAutoSteps =
    {
        "qobuz|27",
        "tidal|HI_RES_LOSSLESS",
        "deezer|9"
    };
    private static readonly string[] ExpectedAutoPlusFallbackPlanSteps =
    {
        "qobuz|27",
        "tidal|HI_RES_LOSSLESS",
        "apple|ALAC",
        "qobuz|7"
    };
    private static readonly string[] ExpectedForcedDeezerFallbackSteps = { "deezer|9", "deezer|3", "deezer|1" };
    private static readonly string[] ExpectedCustomFallbackSteps = { "apple|ALAC", "qobuz|6" };
    [Fact]
    public void BuildPlanSteps_UsesAutoSourcesBeforePersistedFallbackPlan()
    {
        var fallbackPlan = new List<FallbackPlanStep>
        {
            new("step-0", "qobuz", "27", Array.Empty<string>(), "direct_url"),
            new("step-1", "tidal", "HI_RES_LOSSLESS", Array.Empty<string>(), "direct_url")
        };
        var settings = new DeezSpoTagSettings { Service = "auto" };

        var steps = BuildPlanSteps(fallbackPlan, new List<string> { "deezer|9" }, settings);

        Assert.Equal(["deezer|9"], steps);
    }

    [Fact]
    public void BuildPlanSteps_PreservesAutoSourcesOrder_WhenFallbackPlanDiffers()
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

        Assert.Equal(["qobuz|27", "tidal|HI_RES_LOSSLESS", "apple|ALAC"], steps);
    }

    [Fact]
    public void BuildPlanSteps_UsesAutoSources_WhenFallbackPlanIsMissing()
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

        Assert.Equal(["qobuz|27", "deezer|9", "deezer|3", "tidal|LOSSLESS"], steps);
    }

    [Fact]
    public void BuildPlanSteps_RequiresPersistedPlan_WhenPayloadHasNoPlan()
    {
        var settings = new DeezSpoTagSettings { Service = "deezer" };

        var steps = BuildPlanSteps(new List<FallbackPlanStep>(), new List<string>(), settings);

        Assert.Empty(steps);
    }

    [Fact]
    public void BuildPlanSteps_DoesNotRebuildCustomOrder_WhenPayloadHasNoPlanOrAutoSources()
    {
        var settings = new DeezSpoTagSettings
        {
            Service = "auto",
            DownloadEngineOrder = new DownloadEngineOrderSettings
            {
                Enabled = true,
                Engines = new List<DownloadEngineOrderItem>
                {
                    new()
                    {
                        Engine = "apple",
                        Enabled = true,
                        Qualities = new List<DownloadEngineQualityItem>
                        {
                            new() { Quality = "ALAC", Enabled = true },
                            new() { Quality = "AAC", Enabled = false }
                        }
                    },
                    new()
                    {
                        Engine = "qobuz",
                        Enabled = true,
                        Qualities = new List<DownloadEngineQualityItem>
                        {
                            new() { Quality = "27", Enabled = false },
                            new() { Quality = "7", Enabled = false },
                            new() { Quality = "6", Enabled = true },
                            new() { Quality = "5", Enabled = false }
                        }
                    },
                    new()
                    {
                        Engine = "tidal",
                        Enabled = false,
                        Qualities = new List<DownloadEngineQualityItem>
                        {
                            new() { Quality = "HI_RES_LOSSLESS", Enabled = true },
                            new() { Quality = "HI_RES", Enabled = true },
                            new() { Quality = "LOSSLESS", Enabled = true },
                            new() { Quality = "HIGH", Enabled = true },
                            new() { Quality = "LOW", Enabled = true }
                        }
                    },
                    new()
                    {
                        Engine = "amazon",
                        Enabled = false,
                        Qualities = new List<DownloadEngineQualityItem>
                        {
                            new() { Quality = "FLAC", Enabled = true }
                        }
                    },
                    new()
                    {
                        Engine = "deezer",
                        Enabled = false,
                        Qualities = new List<DownloadEngineQualityItem>
                        {
                            new() { Quality = "9", Enabled = true },
                            new() { Quality = "3", Enabled = true },
                            new() { Quality = "1", Enabled = true }
                        }
                    }
                }
            }
        };

        var steps = BuildPlanSteps(new List<FallbackPlanStep>(), new List<string>(), settings);

        Assert.Empty(steps);
    }

    [Fact]
    public void BuildPlanSteps_UsesAutoSourcesForMultiSourceFallback()
    {
        var autoSources = new List<string>
        {
            "qobuz|27",
            "tidal|HI_RES_LOSSLESS",
            "deezer|9"
        };
        var settings = new DeezSpoTagSettings { Service = "auto" };

        var steps = BuildPlanSteps(new List<FallbackPlanStep>(), autoSources, settings);

        Assert.Equal(["qobuz|27", "tidal|HI_RES_LOSSLESS", "deezer|9"], steps);
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
        var planSteps = InvokeBuildPlanSteps(
            [
                new FallbackPlanStep("qobuz-27", "qobuz", "27", Array.Empty<string>(), "persisted"),
                new FallbackPlanStep("deezer-9", "deezer", "9", Array.Empty<string>(), "persisted"),
                new FallbackPlanStep("deezer-3", "deezer", "3", Array.Empty<string>(), "persisted")
            ],
            autoSources,
            settings);

        var index = InvokeFindStepIndex(planSteps, "deezer", "3");

        Assert.Equal(2, index);
    }

    [Fact]
    public async Task TryBuildAppleFallbackUrl_UsesAppleId_WhenAppleStepHasNoResolvedUrl()
    {
        var url = await InvokeTryBuildAppleFallbackUrlAsync(
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
    public async Task TryBuildAppleFallbackUrl_BuildsStationUrl_ForStationIds()
    {
        var url = await InvokeTryBuildAppleFallbackUrlAsync(
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

        var requestType = method.GetParameters()[0].ParameterType;
        var request = Activator.CreateInstance(
            requestType,
            "queue-test",
            settings.Service ?? "auto",
            autoSources,
            0,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "Title",
            "Artist",
            "Album",
            null,
            string.Empty,
            "stereo",
            new QueueSourceSettingsSnapshot(),
            fallbackPlan);
        Assert.NotNull(request);

        var result = method!.Invoke(null, new object[] { request!, settings });
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

    private static async Task<string?> InvokeTryBuildAppleFallbackUrlAsync(
        string engine,
        string sourceUrl,
        string spotifyId,
        string appleId,
        string? isrc,
        string deezerId,
        string userCountry,
        bool fallbackSearchEnabled)
    {
        var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var appleCatalogService = new AppleMusicCatalogService(
            new StubHttpClientFactory(),
            settingsService,
            NullLogger<AppleMusicCatalogService>.Instance,
            new MemoryCache(new MemoryCacheOptions()));
        var fallbackSearchService = new EngineFallbackSearchService(
            appleCatalogService,
            NullLogger<EngineFallbackSearchService>.Instance);

        var result = await fallbackSearchService.ResolveAsync(
            new EngineFallbackSearchRequest(
                engine,
                sourceUrl,
                spotifyId,
                appleId,
                string.Empty,
                string.Empty,
                string.Empty,
                isrc,
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                deezerId,
                string.Empty,
                string.Empty,
                "us",
                "en-US",
                null,
                userCountry,
                fallbackSearchEnabled),
            CancellationToken.None);
        return result.ResolvedUrl;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
