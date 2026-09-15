using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.CoverPort;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Pins the sidecar pass contract: detection happens first and cheaply, a fully-populated
/// file is skipped at once (and reported skipped, not failed), the fetch line advances per
/// file, the network wait is bounded while a disk write in progress is allowed to finish,
/// and a lyrics check that does not finish is "could not be verified" - never "no lyrics".
/// The shared wording stays with <see cref="SidecarFetchActivity.Describe"/>.
/// </summary>
public sealed class SidecarFetchProgressTest
{
    private static readonly string[] OrderedFiles =
    {
        "/music/Artist/Album One/01 - First.flac",
        "/music/Artist/Album One/02 - Second.flac",
        "/music/Artist/Album One/03 - Third.flac",
        "/music/Artist/Album Two/01 - Fourth.flac",
        "/music/Artist/Album Two/02 - Fifth.flac"
    };

    private static string FetchLine(string filePath, AutoTagService.SidecarFileNeeds needs, int processed, int total)
    {
        var fetchMessage = SidecarFetchActivity.Describe(new SidecarFetchWork(
            needs.NeedsStillArtwork,
            needs.NeedsAnimatedArtwork,
            false,
            needs.NeedsLyrics));
        return AutoTagService.DescribeSidecarFetchProgress(filePath, fetchMessage, processed, total);
    }

    private static CoverAlbumMaintenancePlan AlbumPlan(string filePath, bool still, bool animated, bool localOnly = false)
        => new(
            Path.GetDirectoryName(filePath) ?? string.Empty,
            still,
            animated,
            localOnly,
            SkipReason: null);

    private static LyricsRefreshPlan LyricsPlan(string filePath, long trackId, bool shouldFetch)
        => new(trackId, filePath, shouldFetch, Array.Empty<string>(), shouldFetch ? null : "already present");

    /// <summary>
    /// Walks the real per-file resolution (album-artwork ownership + detection results) and
    /// produces the visible line for every file, exactly as the pass does.
    /// </summary>
    private static IReadOnlyList<string> EmitMessages(
        bool runCovers,
        bool runLyrics,
        Func<string, long> trackIdForFile,
        Func<string, AutoTagService.SidecarFetchScope, (CoverAlbumMaintenancePlan? Cover, LyricsRefreshPlan? Lyrics)> detection)
    {
        var plan = new AutoTagService.SidecarFetchPlan(runCovers: runCovers, runLyrics: runLyrics);
        var messages = new List<string>();
        for (var index = 0; index < OrderedFiles.Length; index++)
        {
            var filePath = OrderedFiles[index];
            var scope = plan.Resolve(filePath, trackIdForFile(filePath));
            var (coverPlan, lyricsPlan) = detection(filePath, scope);
            var needs = AutoTagService.ResolveSidecarFileNeeds(
                scope.OwnsAlbumArtwork,
                coverPlan,
                scope.HandlesLyrics,
                lyricsPlan);
            if (needs.IsAlreadyComplete)
            {
                messages.Add(AutoTagService.DescribeSidecarSkip(filePath, index + 1, OrderedFiles.Length));
                continue;
            }

            messages.Add(FetchLine(filePath, needs, index + 1, OrderedFiles.Length));
        }

        return messages;
    }

    [Fact]
    public void SidecarMessagesAdvancePerFileAndClaimAlbumArtworkOncePerAlbum()
    {
        var messages = EmitMessages(
            runCovers: true,
            runLyrics: true,
            _ => 42,
            (filePath, scope) => (
                // Detection says the album's first file still needs both artwork kinds and
                // every file still needs its own lyrics.
                scope.OwnsAlbumArtwork ? AlbumPlan(filePath, still: true, animated: true) : null,
                scope.HandlesLyrics ? LyricsPlan(filePath, 42, shouldFetch: true) : null));

        // Every file gets its own line; none is silently dropped.
        Assert.Equal(OrderedFiles.Length, messages.Count);
        Assert.All(messages, message => Assert.False(string.IsNullOrWhiteSpace(message)));
        Assert.Equal(OrderedFiles.Length, messages.Distinct(StringComparer.Ordinal).Count());

        // The album's first file carries artwork + lyrics; the rest of that album carries
        // lyrics only. Nothing rests on the first file while the run continues.
        Assert.StartsWith(
            "Fetching album artwork, animated artwork and lyrics (file 1/5: 01 - First.flac)",
            messages[0],
            StringComparison.Ordinal);
        Assert.StartsWith(
            "Fetching lyrics (file 2/5: 02 - Second.flac)",
            messages[1],
            StringComparison.Ordinal);
        Assert.StartsWith(
            "Fetching lyrics (file 3/5: 03 - Third.flac)",
            messages[2],
            StringComparison.Ordinal);
        Assert.StartsWith(
            "Fetching album artwork, animated artwork and lyrics (file 4/5: 01 - Fourth.flac)",
            messages[3],
            StringComparison.Ordinal);
        Assert.StartsWith(
            "Fetching lyrics (file 5/5: 02 - Fifth.flac)",
            messages[4],
            StringComparison.Ordinal);

        // Artwork is claimed exactly twice - once per album - and never repeated.
        Assert.Equal(2, messages.Count(message => message.Contains("album artwork", StringComparison.Ordinal)));
        Assert.Equal(2, messages.Count(message => message.Contains("animated artwork", StringComparison.Ordinal)));
    }

    [Fact]
    public void LyricsOnlyRunEmitsALyricsLineForEveryFile()
    {
        var messages = EmitMessages(
            runCovers: false,
            runLyrics: true,
            _ => 7,
            (filePath, _) => (null, LyricsPlan(filePath, 7, shouldFetch: true)));

        Assert.All(
            messages,
            message => Assert.StartsWith("Fetching lyrics (file ", message, StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Contains("artwork", StringComparison.Ordinal));
    }

    /// <summary>
    /// A file that already has everything is detected as complete and the pass skips it before
    /// any fetch is started - so it never pays a timeout sized for files that need work - and
    /// it is reported as skipped, not as a failure.
    /// </summary>
    [Fact]
    public void FullyPopulatedFileIsDetectedCompleteSkippedFastAndReportedSkipped()
    {
        // Not the album's first file, and nothing is missing: no artwork work is owned and
        // detection says the lyrics are already there.
        var plan = new AutoTagService.SidecarFetchPlan(runCovers: true, runLyrics: true);
        plan.Resolve(OrderedFiles[0], 42); // that album's first file claims its artwork
        var filePath = OrderedFiles[1];
        var scope = plan.Resolve(filePath, 42);
        Assert.False(scope.OwnsAlbumArtwork);

        var needs = AutoTagService.ResolveSidecarFileNeeds(
            scope.OwnsAlbumArtwork,
            AlbumPlan(filePath, still: false, animated: false),
            scope.HandlesLyrics,
            LyricsPlan(filePath, 42, shouldFetch: false));

        // Complete: the loop continues before it can ever enter a fetch step, so no network
        // budget is armed and nothing is made to wait.
        Assert.True(needs.IsAlreadyComplete);
        Assert.False(needs.RequiresFetch);
        Assert.False(needs.RequiresArtworkStep);

        var skip = AutoTagService.DescribeSidecarSkip(filePath, 2, OrderedFiles.Length);
        Assert.Contains("02 - Second.flac", skip, StringComparison.Ordinal);
        Assert.Contains("file 2/5", skip, StringComparison.Ordinal);
        Assert.Contains("already complete", skip, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch failed", skip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("did not finish", skip, StringComparison.OrdinalIgnoreCase);

        // The skip decision is pure, local detection: it cannot block on a network wait.
        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < 1_000; index++)
        {
            _ = AutoTagService.ResolveSidecarFileNeeds(false, null, true, LyricsPlan(filePath, 42, false));
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));

        // A file whose album artwork detection returns nothing at all is equally complete.
        Assert.True(AutoTagService.ResolveSidecarFileNeeds(false, null, true, LyricsPlan(filePath, 42, false)).IsAlreadyComplete);
    }

    /// <summary>
    /// The animated-artwork conversion legitimately writes to disk for a long time. Once the
    /// service reports the write phase, the deadline switches from the short network budget to
    /// the much larger write budget, so a write in progress is never killed.
    /// </summary>
    [Fact]
    public async Task SlowAnimatedArtworkWriteIsNotKilledByTheNetworkBudget()
    {
        var networkBudget = TimeSpan.FromMilliseconds(100);
        using var budget = new AutoTagService.SidecarFetchBudget(
            networkBudget,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        var stopwatch = Stopwatch.StartNew();
        var result = await AutoTagService.RunSidecarFetchStepAsync(
            async token =>
            {
                // Stands in for the animated-artwork download followed by the long local write:
                // the fetch is well inside the network budget, the write is well outside it.
                await Task.Delay(TimeSpan.FromMilliseconds(30), token);
                await budget.BeginWrite(token);
                await Task.Delay(TimeSpan.FromMilliseconds(400), token);
                return "written";
            },
            budget,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.Equal("written", result.Result);
        Assert.True(budget.WritePhaseStarted);
        // The write ran past the network budget without being killed: the deadline followed the
        // step into its write phase instead of aborting work in progress.
        Assert.True(stopwatch.Elapsed > networkBudget);
    }

    /// <summary>
    /// A step that never reports its write phase is still bounded by the network budget, so one
    /// unresponsive provider cannot freeze the per-file pass.
    /// </summary>
    [Fact]
    public async Task NetworkWaitWithoutAWritePhaseIsStillBounded()
    {
        using var budget = new AutoTagService.SidecarFetchBudget(
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMinutes(12),
            CancellationToken.None);

        var result = await AutoTagService.RunSidecarFetchStepAsync(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            },
            budget,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.TimedOut);
        Assert.False(budget.WritePhaseStarted);
        Assert.Contains("the fetch did not finish within", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SidecarFetchStepReturnsTheResultWhenItCompletesInTime()
    {
        using var budget = new AutoTagService.SidecarFetchBudget(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var result = await AutoTagService.RunSidecarFetchStepAsync(
            _ => Task.FromResult(17),
            budget,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.Null(result.FailureMessage);
        Assert.Equal(17, result.Result);
    }

    [Fact]
    public async Task SidecarFetchStepStillPropagatesARealRunCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var budget = new AutoTagService.SidecarFetchBudget(
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5),
            cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AutoTagService.RunSidecarFetchStepAsync(
                async token =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return true;
                },
                budget,
                cancellation.Token));
    }

    /// <summary>
    /// A lyrics check that hangs is recorded as unverified: it is never "no lyrics", it never
    /// reaches the write (so existing lyrics are untouched), and it is never marked complete.
    /// </summary>
    [Fact]
    public async Task LyricsTimeoutIsUnverifiedNeverNoLyricsAndNeverReachesTheWrite()
    {
        var reachedTheWrite = false;
        using var budget = new AutoTagService.SidecarFetchBudget(
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMinutes(12),
            CancellationToken.None);

        var step = await AutoTagService.RunSidecarFetchStepAsync(
            async token =>
            {
                // Mirrors LyricsService.SaveLyricsAsync: the provider lookup must return before
                // anything is written, so a hang here leaves the existing lyrics in place.
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                reachedTheWrite = true;
                return true;
            },
            budget,
            CancellationToken.None);

        Assert.False(reachedTheWrite);

        // No result came back, and elapsed time is classified as unverified - never absent.
        var verdict = AutoTagService.ClassifyLyricsOutcome(null, step.TimedOut);
        Assert.Equal(AutoTagService.SidecarLyricsOutcome.Unverified, verdict);
        Assert.NotEqual(AutoTagService.SidecarLyricsOutcome.Absent, verdict);

        var recorded = AutoTagService.ResolveSidecarLyricsVerdict(verdict);
        Assert.False(recorded.ConfirmsAbsence);
        Assert.True(recorded.MarksFileUnverified);
        Assert.Equal(AutoTagLiterals.ReviewStatus, recorded.Status);
        Assert.NotEqual(AutoTagLiterals.OkStatus, recorded.Status);
        Assert.NotEqual(AutoTagLiterals.CompletedStatus, recorded.Status);

        // The message says "could not be verified" and never claims there are no lyrics.
        var message = AutoTagService.DescribeSidecarLyricsUnverified(
            "/music/Artist/Album One/02 - Second.flac",
            step.FailureMessage,
            2,
            5);
        Assert.Contains("could not be verified", message, StringComparison.Ordinal);
        Assert.Contains("02 - Second.flac", message, StringComparison.Ordinal);
        Assert.Contains("file 2/5", message, StringComparison.Ordinal);
        Assert.Contains("existing lyrics were kept", message, StringComparison.Ordinal);
        Assert.DoesNotContain("No lyrics were returned", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only a genuine negative result from the configured sources marks lyrics absent; a check
    /// that was skipped or errored is not a verdict at all.
    /// </summary>
    [Fact]
    public void GenuineNegativeLyricsResultMarksLyricsAbsent()
    {
        var absent = LyricsRefreshTrackResult.ConfirmedAbsent(
            42,
            "/music/Artist/Album One/02 - Second.flac",
            "No lyrics were returned by the enabled providers.");
        Assert.True(absent.LyricsConfirmedAbsent);

        Assert.Equal(
            AutoTagService.SidecarLyricsOutcome.Absent,
            AutoTagService.ClassifyLyricsOutcome(absent, timedOut: false));
        Assert.Equal(
            AutoTagService.SidecarLyricsOutcome.Found,
            AutoTagService.ClassifyLyricsOutcome(
                LyricsRefreshTrackResult.Completed(42, "/music/Artist/Album One/02 - Second.flac", new[] { "lrc" }, true),
                timedOut: false));

        // Existing lyrics kept, or an error with no file, is not a confirmed absence.
        var kept = LyricsRefreshTrackResult.Skipped(
            42,
            "/music/Artist/Album One/02 - Second.flac",
            "Existing lyrics kept; overwrite was not selected.");
        Assert.False(kept.LyricsConfirmedAbsent);
        Assert.Equal(
            AutoTagService.SidecarLyricsOutcome.LyricsAlreadyPresent,
            AutoTagService.ClassifyLyricsOutcome(kept, timedOut: false));
        Assert.NotEqual(
            AutoTagService.SidecarLyricsOutcome.Absent,
            AutoTagService.ClassifyLyricsOutcome(
                LyricsRefreshTrackResult.Skipped(42, null, "provider exploded"),
                timedOut: false));

        var absentVerdict = AutoTagService.ResolveSidecarLyricsVerdict(AutoTagService.SidecarLyricsOutcome.Absent);
        Assert.True(absentVerdict.ConfirmsAbsence);
        Assert.False(absentVerdict.MarksFileUnverified);

        // A timeout is never promoted to a confirmed absence by any path.
        Assert.False(
            AutoTagService.ResolveSidecarLyricsVerdict(
                AutoTagService.ClassifyLyricsOutcome(absent, timedOut: true)).ConfirmsAbsence);
    }

    [Fact]
    public void TimeoutFailureAndCompletionLinesReportTheFileTheReasonAndTheTotals()
    {
        var failure = AutoTagService.DescribeSidecarFetchFailure(
            "/music/Artist/Album One/02 - Second.flac",
            "the fetch did not finish within 90s",
            2,
            5);

        Assert.Contains("02 - Second.flac", failure, StringComparison.Ordinal);
        Assert.Contains("file 2/5", failure, StringComparison.Ordinal);
        Assert.Contains("the fetch did not finish within 90s", failure, StringComparison.Ordinal);
        Assert.Contains("existing artwork and lyrics were kept", failure, StringComparison.Ordinal);

        var completion = AutoTagService.DescribeSidecarCompletion(new AutoTagService.SidecarFetchCounters
        {
            Processed = 5,
            SkippedAlreadyComplete = 2,
            ArtworkFetched = 1,
            LyricsFound = 1,
            LyricsAlreadyPresent = 0,
            LyricsAbsent = 1,
            Unverified = 1
        });

        // The distinctions stay separate: skipped is not a failure, and "absent" is not
        // collapsed with "unverified".
        Assert.Contains("5 file(s) processed", completion, StringComparison.Ordinal);
        Assert.Contains("2 already complete (skipped)", completion, StringComparison.Ordinal);
        Assert.Contains("1 artwork fetch(es)", completion, StringComparison.Ordinal);
        Assert.Contains("1 lyrics found", completion, StringComparison.Ordinal);
        Assert.Contains("1 lyrics confirmed absent", completion, StringComparison.Ordinal);
        Assert.Contains("1 unverified/timed-out", completion, StringComparison.Ordinal);
    }
}
