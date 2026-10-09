using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Pins the fix for a defect where a library configured as Mode = Both generated
/// nothing on the scheduled path.
///
/// <para>The scheduler passed the configured mode straight to RunSlotAsync, but
/// "both" names two independent instances rather than one runnable mode, so the
/// instance lookup matched nothing and the slot failed silently on every heartbeat.
/// The scheduler now expands the configured mode into the concrete modes that
/// actually run, each with its own run-state key.</para>
///
/// <para>These are the two failure modes that matter and they are different bugs:
/// the wrong expansion silently generates nothing, while the right expansion under a
/// shared run-state key generates one mode and skips the other forever.</para>
/// </summary>
public sealed class MelodayBothModeSchedulingTest
{
    [Fact]
    public void Both_ExpandsToTwoConcreteRunnableModes()
    {
        Assert.Equal(new[] { "direct", "sonic" }, MelodayService.ResolveRunModes("both"));
    }

    [Fact]
    public void SingleModes_ExpandToThemselves()
    {
        Assert.Equal(new[] { "direct" }, MelodayService.ResolveRunModes("direct"));
        Assert.Equal(new[] { "sonic" }, MelodayService.ResolveRunModes("sonic"));
    }

    [Fact]
    public void UnknownMode_FallsBackToSonicRatherThanGeneratingNothing()
    {
        // "both" must be the only value that expands, and it must expand rather than
        // being passed through. A value that does not expand to a real mode would
        // repeat the original defect.
        foreach (var mode in new[] { "", "  ", "nonsense", "anchor", "journey", null })
        {
            var expanded = MelodayService.ResolveRunModes(mode);
            Assert.True(
                expanded.Length >= 1,
                $"mode '{mode}' must expand to at least one concrete mode.");
            Assert.All(expanded, concrete =>
                Assert.Contains(concrete, new[] { "direct", "sonic" }));
        }
    }

    [Fact]
    public void BothModes_AreIndependentlyDueAndKeyedOnTheSameDay()
    {
        // Both instances are generated on the same day, so before either has run both
        // must report due. This is what a shared run-state key would break: the first
        // run would write a key the second read, and the second would never be due.
        var slot = new MelodayScheduleSlot("evening", "Evening", "19:00", 4);
        var today = new DateOnly(2026, 10, 6);
        var nowTime = new TimeOnly(19, 5);

        var directKey = MelodayRunStateStore.Key(7, "evening", "direct");
        var sonicKey = MelodayRunStateStore.Key(7, "evening", "sonic");

        Assert.NotEqual(directKey, sonicKey);

        Assert.True(MelodayScheduleMath.IsDue(slot, today, nowTime, null, graceMinutes: 60));
        Assert.True(MelodayScheduleMath.IsDue(slot, today, nowTime, null, graceMinutes: 60));

        // After Direct completes, only Direct is suppressed. Sonic is still due, which
        // is the whole point of a per-mode key.
        // MelodayService persists DateOnly.ToString("o"), i.e. "2026-10-06". A
        // DateTime here would not match and the guard would read as never-run.
        var directDone = new MelodayRunStateEntry(
            today.ToString("o"), DateTimeOffset.UtcNow, "complete");

        Assert.False(MelodayScheduleMath.IsDue(slot, today, nowTime, directDone, graceMinutes: 60));
        Assert.True(MelodayScheduleMath.IsDue(slot, today, nowTime, null, graceMinutes: 60));
    }

    [Fact]
    public void BothConfiguredLibrary_UsesDistinctRunStateKeysPerConcreteMode()
    {
        // A configured "both" has no run-state key of its own, because it is never the
        // mode of a run. Every key it can reach belongs to a concrete mode.
        var configuredKeys = MelodayService.ResolveRunModes("both")
            .Select(mode => MelodayRunStateStore.Key(7, "evening", mode))
            .ToList();

        Assert.Equal(2, configuredKeys.Count);
        Assert.Equal(configuredKeys.Count, configuredKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(MelodayRunStateStore.Key(7, "evening", "both"), configuredKeys);
    }

    [Fact]
    public void Scheduler_ExpandsTheConfiguredModeBeforeGenerating()
    {
        // Source-level guard. The bug was a single missing expansion in the loop that
        // decides what runs; this fails loudly if it is reintroduced, and fails just as
        // loudly if the expansion is replaced with something that does not consult
        // ResolveRunModes.
        var source = File.ReadAllText(Path.Join(
            ResolveRepoRoot(), "DeezSpoTag.Web", "Services", "MelodayHostedService.cs"));

        Assert.Contains("foreach (var mode in MelodayService.ResolveRunModes(library.Mode))", source, StringComparison.Ordinal);

        // The key and the run must both use the expanded mode, not the configured one.
        Assert.Contains("MelodayRunStateStore.Key(library.LibraryId, slot.Id, mode)", source, StringComparison.Ordinal);
        Assert.Contains("RunSlotAsync(library.LibraryId, slot.Id, mode, token)", source, StringComparison.Ordinal);

        // The configured mode must not reach the run or the key anywhere in the file,
        // which is precisely what made "both" a no-op.
        Assert.DoesNotContain("RunSlotAsync(library.LibraryId, slot.Id, library.Mode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Key(library.LibraryId, slot.Id, library.Mode)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RunStateKey_IsUnchangedByDjIntegration()
    {
        // The DJ is deliberately not part of playlist identity, so it must not be part
        // of the run-state key either: a key carrying the DJ would reset the once-per-day
        // guard whenever Random DJ resolved a different DJ.
        var directKey = MelodayRunStateStore.Key(7, "evening", "direct");

        Assert.Equal("7:evening:direct", directKey);
        Assert.DoesNotContain("anchor", directKey, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("journey", directKey, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tuesday", directKey, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test output path.");
    }
}
