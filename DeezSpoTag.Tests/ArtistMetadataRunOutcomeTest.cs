using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Behavioural coverage for the run outcome vocabulary. These pin the two defects that made the
/// reported artist counts wrong: counters that could not be reconciled after a resume, and an
/// "updated" bucket that included no-ops, capability limits and rescan-only work.
/// </summary>
public class ArtistMetadataRunOutcomeTest
{
    [Fact]
    public void PausedRun_ResumedCountsKeepTheFrozenDenominatorAndRetryableOutcomes()
    {
        var run = new ArtistMetadataActiveRun { Paused = true, TargetArtistIds = new() { 1, 2, 3, 4 } };
        var outcomes = new[] { new ArtistRunOutcomeRecord(1, ArtistRunOutcomes.Succeeded),
            new ArtistRunOutcomeRecord(2, ArtistRunOutcomes.Failed) };
        var counters = InvokeCounters("FromOutcomes", run.TargetArtistIds.Count, outcomes);
        Assert.Equal(4, ReadCounter(counters, "TotalArtists"));
        Assert.Equal(2, ReadCounter(counters, "ProcessedArtists"));
        Assert.Equal(2, ReadCounter(counters, "ResumedArtists"));
        Assert.Contains(1, ArtistRunOutcomes.BuildResumeSkipSet(outcomes));
        Assert.DoesNotContain(2, ArtistRunOutcomes.BuildResumeSkipSet(outcomes));
    }

    [Fact]
    public void Counters_AlwaysReconcileWithTheProcessedTotal()
    {
        var outcomes = new[]
        {
            new ArtistRunOutcomeRecord(1, ArtistRunOutcomes.Succeeded),
            new ArtistRunOutcomeRecord(2, ArtistRunOutcomes.Partial),
            new ArtistRunOutcomeRecord(3, ArtistRunOutcomes.NoMetadata),
            new ArtistRunOutcomeRecord(4, ArtistRunOutcomes.Failed),
            new ArtistRunOutcomeRecord(5, ArtistRunOutcomes.Skipped, ArtistRunSkipReasons.NotDue),
            new ArtistRunOutcomeRecord(6, ArtistRunOutcomes.Skipped, ArtistRunSkipReasons.ScanOnly)
        };

        var counters = InvokeCounters("FromOutcomes", 6, outcomes);

        Assert.Equal(6, ReadCounter(counters, "ProcessedArtists"));
        Assert.Equal(
            ReadCounter(counters, "ProcessedArtists"),
            ReadCounter(counters, "SuccessfulArtists")
            + ReadCounter(counters, "PartialArtists")
            + ReadCounter(counters, "NoMetadataArtists")
            + ReadCounter(counters, "FailedArtists")
            + ReadCounter(counters, "SkippedArtists"));
    }

    [Fact]
    public void Counters_ForAResumedRunIncludeTheCarriedOverArtists()
    {
        // 400 finished before the interrupt, 600 still to do. The resumed run must report 1000
        // processed with every artist in exactly one bucket, not 1000 processed and 600 tallied.
        var carried = Enumerable.Range(1, 400)
            .Select(id => new ArtistRunOutcomeRecord(id, ArtistRunOutcomes.Succeeded))
            .ToList();
        var counters = InvokeCounters("FromOutcomes", 1000, carried);

        Assert.Equal(400, ReadCounter(counters, "ProcessedArtists"));
        Assert.Equal(400, ReadCounter(counters, "SuccessfulArtists"));
        Assert.Equal(400, ReadCounter(counters, "ResumedArtists"));
    }

    [Fact]
    public void ResumeSkipSet_RetriesFailuresAndMissingMetadata()
    {
        var outcomes = new[]
        {
            new ArtistRunOutcomeRecord(1, ArtistRunOutcomes.Succeeded),
            new ArtistRunOutcomeRecord(2, ArtistRunOutcomes.Partial),
            new ArtistRunOutcomeRecord(3, ArtistRunOutcomes.NoMetadata),
            new ArtistRunOutcomeRecord(4, ArtistRunOutcomes.Failed),
            new ArtistRunOutcomeRecord(5, ArtistRunOutcomes.Skipped, ArtistRunSkipReasons.ScanOnly),
            new ArtistRunOutcomeRecord(6, ArtistRunOutcomes.Skipped, ArtistRunSkipReasons.SyncBlocked)
        };

        var skip = ArtistRunOutcomes.BuildResumeSkipSet(outcomes);

        // Only genuine work is terminal. A failure must be retried, otherwise an interrupted run
        // permanently loses every artist that was failing when it stopped.
        Assert.Equal(new long[] { 1, 2 }, skip.OrderBy(id => id).ToArray());
    }

    [Fact]
    public void ResumeSkipSet_TreatsNotDueSkipsAsTerminal()
    {
        var outcomes = new[]
        {
            new ArtistRunOutcomeRecord(1, ArtistRunOutcomes.Skipped, ArtistRunSkipReasons.NotDue),
            new ArtistRunOutcomeRecord(2, ArtistRunOutcomes.Skipped, ArtistRunSkipReasons.NotDue)
        };

        var skip = ArtistRunOutcomes.BuildResumeSkipSet(outcomes);

        // The interval has not elapsed, so retrying cannot change the result.
        Assert.Equal(new long[] { 1, 2 }, skip.OrderBy(id => id).ToArray());
    }

    [Fact]
    public void ResumeSkipSet_IgnoresNonPositiveIdsAndEmptyInput()
    {
        Assert.Empty(ArtistRunOutcomes.BuildResumeSkipSet(null));
        Assert.Empty(ArtistRunOutcomes.BuildResumeSkipSet(Array.Empty<ArtistRunOutcomeRecord>()));
        Assert.Empty(ArtistRunOutcomes.BuildResumeSkipSet(
            new[] { new ArtistRunOutcomeRecord(0, ArtistRunOutcomes.Succeeded) }));
    }

    [Fact]
    public void PartialSuccessIsNotAFailure_WhenAnotherTargetSucceeded()
    {
        var pushed = BuildPushOutcome(
            new ArtistTargetResult("plex", ArtistTargetOutcome.Updated, new[] { ArtistTargetFields.Avatar }),
            new ArtistTargetResult("jellyfin", ArtistTargetOutcome.Failed, Error: "Jellyfin artist not found."));

        var outcome = InvokeClassify(pushed, popularSongsSynced: false);

        Assert.Equal("Partial", outcome);
    }

    [Fact]
    public void PartialSuccessIsNotAFailure_ForEveryTargetPair()
    {
        var pairs = new (string Succeeded, string Failed)[]
        {
            ("plex", "jellyfin"),
            ("jellyfin", "plex"),
            ("plex", "navidrome"),
            ("navidrome", "jellyfin"),
            ("jellyfin", "navidrome"),
            ("navidrome", "plex")
        };

        foreach (var (succeeded, failed) in pairs)
        {
            var pushed = BuildPushOutcome(
                new ArtistTargetResult(succeeded, ArtistTargetOutcome.Updated, new[] { ArtistTargetFields.Avatar }),
                new ArtistTargetResult(failed, ArtistTargetOutcome.Failed, Error: $"{failed} update failed."));

            Assert.Equal("Partial", InvokeClassify(pushed, popularSongsSynced: false));
        }
    }

    [Fact]
    public void NavidromeReadOnlyBiographyIsNotAPartialOrAFailure()
    {
        // Plex stored the artwork; Navidrome stored the artwork and reported its biography as a
        // declared capability limit. That limit must not degrade the artist outcome.
        var pushed = BuildPushOutcome(
            new ArtistTargetResult("plex", ArtistTargetOutcome.Updated, new[] { ArtistTargetFields.Avatar }),
            new ArtistTargetResult(
                "navidrome",
                ArtistTargetOutcome.Updated,
                new[] { ArtistTargetFields.Avatar },
                new[] { "Navidrome biography is read-only and was not updated." }));

        Assert.Equal("Succeeded", InvokeClassify(pushed, popularSongsSynced: false));
    }

    [Fact]
    public void NavidromeScanOnlyIsNotAnUpdate()
    {
        // A rescan asks Navidrome to re-read; it is a notification, not a metadata write.
        var pushed = BuildPushOutcome(
            new ArtistTargetResult("navidrome", ArtistTargetOutcome.Unchanged, new[] { ArtistTargetFields.Scan }));

        Assert.Equal("ScanOnly", InvokeClassify(pushed, popularSongsSynced: false));
    }

    [Fact]
    public void NavidromeScanOnlyIsNotSuccess_EvenWithNoOtherTargets()
    {
        var pushed = BuildPushOutcome(
            new ArtistTargetResult("navidrome", ArtistTargetOutcome.Unchanged, new[] { ArtistTargetFields.Scan }),
            new ArtistTargetResult(
                "navidrome",
                ArtistTargetOutcome.Updated,
                new[] { ArtistTargetFields.Scan }));

        Assert.NotEqual("Succeeded", InvokeClassify(pushed, popularSongsSynced: false));
    }

    [Fact]
    public void SelectedButUnconfiguredTargetIsReportedNotIgnored()
    {
        // "X is not configured" used to be an anonymous warning that never affected the outcome, so a
        // target with no credentials looked identical to one that worked.
        var notConfigured = ArtistTargetResult.NotConfigured("navidrome", "Navidrome is not configured.");
        var pushed = BuildPushOutcome(
            new ArtistTargetResult("plex", ArtistTargetOutcome.Updated, new[] { ArtistTargetFields.Avatar }),
            notConfigured);

        Assert.Equal("Succeeded", InvokeClassify(pushed, popularSongsSynced: false));
        Assert.Equal(ArtistTargetOutcome.NotConfigured, notConfigured.Outcome);
        Assert.Contains("Navidrome is not configured.", notConfigured.LimitationList);
    }

    [Fact]
    public void NoUpstreamMetadataIsNotAFailure()
    {
        // An artist that exists locally but is absent from the selected server is "unchanged", not a
        // failure. Genuine "no upstream metadata at all" is decided before the push is attempted and
        // is reported as NoMetadata, which is likewise not a failure.
        var notFound = BuildPushOutcome(
            new ArtistTargetResult("plex", ArtistTargetOutcome.NotFound, new[] { ArtistTargetFields.Avatar }));

        Assert.Equal("Unchanged", InvokeClassify(notFound, popularSongsSynced: false));

        // Popular songs synced but nothing else: still not a failure.
        var nothingPushed = BuildPushOutcome();
        Assert.Equal("NoMetadata", InvokeClassify(nothingPushed, popularSongsSynced: true));
        Assert.Equal("Unchanged", InvokeClassify(nothingPushed, popularSongsSynced: false));
    }

    [Fact]
    public void GenuineFailureOnEveryTargetIsStillAFailure()
    {
        var pushed = BuildPushOutcome(
            new ArtistTargetResult("plex", ArtistTargetOutcome.Failed, Error: "Plex update failed."),
            new ArtistTargetResult("jellyfin", ArtistTargetOutcome.Failed, Error: "Jellyfin update failed."));

        Assert.Equal("Failed", InvokeClassify(pushed, popularSongsSynced: false));
    }

    [Theory]
    [InlineData("plex", ArtistTargetCapability.Full)]
    [InlineData("jellyfin", ArtistTargetCapability.Full)]
    [InlineData("navidrome", ArtistTargetCapability.ArtworkOnly)]
    public void TargetCapabilities_MatchDeclaredSupport(string target, ArtistTargetCapability expected)
    {
        var method = typeof(ArtistMetadataUpdaterService)
            .GetMethod("ResolveTargetCapability", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        Assert.Equal(expected, method!.Invoke(null, [target]));
    }

    private static object BuildPushOutcome(params ArtistTargetResult[] targets)
    {
        var type = typeof(ArtistMetadataUpdaterService)
            .GetNestedType("PushOutcome", System.Reflection.BindingFlags.NonPublic)!;
        return Activator.CreateInstance(type, [targets, Array.Empty<string>()])!;
    }

    /// <summary>Invokes the private per-target classifier and returns the resulting outcome name.</summary>
    private static string InvokeClassify(object pushOutcome, bool popularSongsSynced)
    {
        var method = typeof(ArtistMetadataUpdaterService)
            .GetMethod("ClassifyArtistPushOutcome", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ClassifyArtistPushOutcome was not found.");
        var result = method.Invoke(null, [pushOutcome, popularSongsSynced])
            ?? throw new InvalidOperationException("ClassifyPushOutcome returned null.");
        return result.ToString()!;
    }

    [Fact]
    public void ResumedRun_ShowsTheOriginalTotalAndContinuesFromWhereItStopped()
    {
        // The scenario: 100 of 1000 artists finished, the app restarts. The resumed run must report
        // 100/1000 and then 101/1000, not restart at 1 and not shrink the denominator to 900.
        const int total = 1000;
        const int alreadyDone = 100;

        var outcomes = Enumerable.Range(1, alreadyDone)
            .Select(id => new ArtistRunOutcomeRecord(id, ArtistRunOutcomes.Succeeded))
            .ToList();

        var counters = InvokeCounters("FromOutcomes", total, outcomes);
        var skipSet = ArtistRunOutcomes.BuildResumeSkipSet(outcomes);

        // The denominator stays the full original target, and the numerator starts where it stopped.
        Assert.Equal(total, ReadCounter(counters, "TotalArtists"));
        Assert.Equal(alreadyDone, ReadCounter(counters, "ProcessedArtists"));
        Assert.Equal(alreadyDone, skipSet.Count);

        // The first artist that is not skipped takes the count to 101.
        counters = InvokeCounters("FromOutcomes", total, outcomes);
        var nextArtist = Enumerable.Range(1, total).First(id => !skipSet.Contains((long)id));
        counters.GetType()
            .GetMethod("Apply", new[] { typeof(ArtistRunOutcomeRecord) })!
            .Invoke(counters, [new ArtistRunOutcomeRecord(nextArtist, ArtistRunOutcomes.Succeeded)]);

        Assert.Equal(101, ReadCounter(counters, "ProcessedArtists"));
        Assert.Equal(101, ReadCounter(counters, "SuccessfulArtists"));
    }

    [Fact]
    public void ResumedRun_KeepsTheOriginalTargetSetRatherThanOnlyTheRemainder()
    {
        // A resume must scope to the artist set the run resolved to when it started, not to just the
        // artists still outstanding. Scoping to the remainder would shrink the reported total and make
        // the progress bar restart.
        var artists = Enumerable.Range(1, 10)
            .Select(id => new MetadataUpdaterTrackedArtist { ArtistId = id, ArtistName = $"Artist {id}" })
            .ToList();
        var frozenTargetSet = Enumerable.Range(1, 10).Select(id => (long)id).ToHashSet();

        var resumed = InvokeBuildRunCandidates(artists, frozenTargetSet);

        // 10 of 10, even though the first three are already finished.
        Assert.Equal(10, resumed.Count);
        Assert.Equal(10, resumed.Select(artist => artist.ArtistId).Distinct().Count());

        // The already-finished artists are still present in the candidate list; they are filtered out
        // by the skip set at run time, not removed from the target.
        var skipSet = ArtistRunOutcomes.BuildResumeSkipSet(new[]
        {
            new ArtistRunOutcomeRecord(1, ArtistRunOutcomes.Succeeded),
            new ArtistRunOutcomeRecord(2, ArtistRunOutcomes.Succeeded),
            new ArtistRunOutcomeRecord(3, ArtistRunOutcomes.Succeeded)
        });
        var outstanding = resumed.Where(artist => !skipSet.Contains(artist.ArtistId)).ToList();

        Assert.Equal(7, outstanding.Count);
    }

    private static List<MetadataUpdaterTrackedArtist> InvokeBuildRunCandidates(
        List<MetadataUpdaterTrackedArtist> artists,
        HashSet<long>? scoped)    {
        var method = typeof(ArtistMetadataUpdaterService)
            .GetMethod("BuildRunCandidates", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildRunCandidates was not found.");
        var result = method.Invoke(null, [artists, new MetadataUpdaterRunRequest(), scoped])
            ?? throw new InvalidOperationException("BuildRunCandidates returned null.");
        return (List<MetadataUpdaterTrackedArtist>)result;
    }

    private static int ReadInt(object value, string property)
        => (int)value.GetType().GetProperty(property)!.GetValue(value)!;

    private static object InvokeCounters(string name, int total, IReadOnlyList<ArtistRunOutcomeRecord> outcomes)
    {
        var type = typeof(ArtistMetadataUpdaterService)
            .GetNestedType("MetadataRunCounters", System.Reflection.BindingFlags.NonPublic)!;
        var method = type.GetMethod(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!;
        return method.Invoke(null, [total, outcomes])!;
    }

    private static int ReadCounter(object counters, string property)
        => (int)counters.GetType().GetProperty(property)!.GetValue(counters)!;
}
