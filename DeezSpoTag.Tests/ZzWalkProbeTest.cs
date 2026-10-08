using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using Xunit;
using Xunit.Abstractions;

namespace DeezSpoTag.Tests;

/// <summary>
///     Walks a resolved fallback ladder exactly the way
///     <c>EngineFallbackCoordinator.TryAdvanceCoreAsync</c> does, calling the production
///     <c>ShouldSkipStep</c> so this can never drift from the real gate.
/// </summary>
public sealed class ZzWalkProbeTest
{
    private readonly ITestOutputHelper _out;
    public ZzWalkProbeTest(ITestOutputHelper o) => _out = o;

    private static bool ShouldSkipStep(string stepSource, string currentEngine, bool fallbackBitrate)
    {
        var t = typeof(DeezSpoTag.Services.Download.Fallback.EngineFallbackCoordinator);
        var m = t.GetMethod("ShouldSkipStep", BindingFlags.NonPublic | BindingFlags.Static)!;
        var step = ValueTuple.Create(stepSource, (string?)null);
        return (bool)m.Invoke(null, new object?[] { step, currentEngine, fallbackBitrate })!;
    }

    private static List<string> Walk(List<string> ladder, bool fallbackBitrate)
    {
        var attempted = new List<string>();
        var currentEngine = DownloadSourceOrder.DecodeAutoSource(ladder[0]).Source;
        foreach (var encoded in ladder)
        {
            var step = DownloadSourceOrder.DecodeAutoSource(encoded);
            if (ShouldSkipStep(step.Source, currentEngine, fallbackBitrate))
            {
                attempted.Add("SKIP  " + encoded);
                continue;
            }

            attempted.Add("TRY   " + encoded);
            currentEngine = step.Source; // it failed, so the next gate compares against it
        }

        return attempted;
    }

    private static DeezSpoTagSettings Custom(params (string Engine, string[] On)[] cfg)
    {
        var s = new DeezSpoTagSettings { Service = "custom", FallbackBitrate = false };
        var order = DownloadEngineOrderSettings.CreateDefault();
        order.Enabled = true;
        foreach (var e in order.Engines) { e.Enabled = false; foreach (var q in e.Qualities) q.Enabled = false; }
        foreach (var (engine, on) in cfg)
        {
            var e = order.Engines.First(x => x.Engine == engine);
            e.Enabled = true;
            foreach (var q in e.Qualities) q.Enabled = on.Contains(q.Quality, StringComparer.OrdinalIgnoreCase);
        }
        s.DownloadEngineOrder = order;
        return s;
    }

    [Fact]
    public void WalkQobuzDeezer_FallbackBitrateOff()
    {
        var settings = Custom(("qobuz", new[] { "27", "6" }), ("deezer", new[] { "9", "3" }));
        var ladder = DownloadSourceOrder.ResolveQualityAutoSources(settings, true, null);
        _out.WriteLine("LADDER: " + string.Join(" -> ", ladder));
        foreach (var line in Walk(ladder, settings.FallbackBitrate)) _out.WriteLine("  " + line);
    }

    [Fact]
    public void WalkEverything_FallbackBitrateOff()
    {
        var s = new DeezSpoTagSettings { Service = "custom", FallbackBitrate = false };
        var order = DownloadEngineOrderSettings.CreateDefault();
        order.Enabled = true;
        foreach (var e in order.Engines) { e.Enabled = true; foreach (var q in e.Qualities) q.Enabled = true; }
        s.DownloadEngineOrder = order;
        var ladder = DownloadSourceOrder.ResolveQualityAutoSources(s, true, null);
        _out.WriteLine("LADDER: " + string.Join(" -> ", ladder));
        var walked = Walk(ladder, s.FallbackBitrate);
        foreach (var line in walked) _out.WriteLine("  " + line);
        var skipped = walked.Count(l => l.StartsWith("SKIP", StringComparison.Ordinal));
        _out.WriteLine($"TOTAL={walked.Count} SKIPPED={skipped}");
    }
}
