using System;
using System.Collections.Generic;
using System.Reflection;
using DeezSpoTag.Services.Download.Deezer;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadIntentPayloadPopulationTests
{
    private static readonly MethodInfo PopulateStandardQueuePayloadMethod =
        typeof(DownloadIntentService).GetMethod(
            "PopulateStandardQueuePayload",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DownloadIntentService.PopulateStandardQueuePayload not found.");

    private static readonly Type StandardPayloadContextType =
        typeof(DownloadIntentService).GetNestedType("StandardPayloadContext", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("DownloadIntentService.StandardPayloadContext not found.");

    [Fact]
    public void PopulateStandardQueuePayload_PreservesPreResolvedDeezerId()
    {
        var payload = new DeezerQueueItem
        {
            DeezerId = "3466216111"
        };
        var intent = new DownloadIntent
        {
            DeezerId = string.Empty,
            Title = "Hot Body",
            Artist = "Ayra Starr"
        };

        InvokePopulateStandardQueuePayload(payload, intent);

        Assert.Equal("3466216111", payload.DeezerId);
    }

    [Fact]
    public void PopulateStandardQueuePayload_UsesIntentDeezerId_WhenPayloadIsEmpty()
    {
        var payload = new DeezerQueueItem();
        var intent = new DownloadIntent
        {
            DeezerId = "3947111201",
            Title = "Ahere",
            Artist = "Willy Paul"
        };

        InvokePopulateStandardQueuePayload(payload, intent);

        Assert.Equal("3947111201", payload.DeezerId);
    }

    private static void InvokePopulateStandardQueuePayload(DeezerQueueItem payload, DownloadIntent intent)
    {
        var context = Activator.CreateInstance(
            StandardPayloadContextType,
            "https://www.deezer.com/track/3466216111",
            "album",
            "stereo",
            new List<string> { "deezer|1" },
            0,
            new List<FallbackPlanStep>(),
            string.Empty,
            0,
            null,
            string.Empty);

        Assert.NotNull(context);
        PopulateStandardQueuePayloadMethod.Invoke(null, new object?[] { payload, intent, context });
    }
}
