using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Pins the sidecar pass message sequence: every file gets its own emission naming the file
/// and its position, album artwork is claimed once per album (on that album's first file),
/// and a step that never returns is bounded by the per-step timeout instead of freezing the
/// run. The shared wording stays with <see cref="SidecarFetchActivity.Describe"/>.
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

    private static IReadOnlyList<string> EmitMessages(
        bool stillArtwork,
        bool animatedArtwork,
        bool lyrics,
        Func<string, long> trackIdForFile)
    {
        var plan = new AutoTagService.SidecarFetchPlan(
            runCovers: stillArtwork || animatedArtwork,
            runLyrics: lyrics);
        return OrderedFiles
            .Select((filePath, index) =>
            {
                var scope = plan.Resolve(filePath, trackIdForFile(filePath));
                var fetchMessage = SidecarFetchActivity.Describe(new SidecarFetchWork(
                    scope.OwnsAlbumArtwork && stillArtwork,
                    scope.OwnsAlbumArtwork && animatedArtwork,
                    false,
                    scope.HandlesLyrics && lyrics));
                return AutoTagService.DescribeSidecarFetchProgress(
                    filePath,
                    fetchMessage,
                    index + 1,
                    OrderedFiles.Length);
            })
            .ToList();
    }

    [Fact]
    public void SidecarMessagesAdvancePerFileAndClaimAlbumArtworkOncePerAlbum()
    {
        var messages = EmitMessages(
            stillArtwork: true,
            animatedArtwork: true,
            lyrics: true,
            _ => 42);

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
    public void AlbumArtworkOnlyRunStillEmitsEveryFileWithoutRepeatingArtworkWithinAnAlbum()
    {
        var messages = EmitMessages(
            stillArtwork: true,
            animatedArtwork: false,
            lyrics: false,
            _ => 0);

        Assert.Equal(OrderedFiles.Length, messages.Count);
        Assert.StartsWith(
            "Fetching album artwork (file 1/5: 01 - First.flac)",
            messages[0],
            StringComparison.Ordinal);
        // The degenerate case that used to be dropped before the recording path: a file
        // with no fetch work still gets its own line.
        Assert.StartsWith(
            "No sidecar fetch needed (file 2/5: 02 - Second.flac)",
            messages[1],
            StringComparison.Ordinal);
        Assert.StartsWith(
            "No sidecar fetch needed (file 3/5: 03 - Third.flac)",
            messages[2],
            StringComparison.Ordinal);
        Assert.StartsWith(
            "Fetching album artwork (file 4/5: 01 - Fourth.flac)",
            messages[3],
            StringComparison.Ordinal);
        Assert.StartsWith(
            "No sidecar fetch needed (file 5/5: 02 - Fifth.flac)",
            messages[4],
            StringComparison.Ordinal);
    }

    [Fact]
    public void LyricsOnlyRunEmitsALyricsLineForEveryFile()
    {
        var messages = EmitMessages(
            stillArtwork: false,
            animatedArtwork: false,
            lyrics: true,
            _ => 7);

        Assert.All(
            messages,
            message => Assert.StartsWith("Fetching lyrics (file ", message, StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Contains("artwork", StringComparison.Ordinal));
    }

    [Fact]
    public void UnindexedFileInACombinedRunStillGetsItsOwnLine()
    {
        var messages = EmitMessages(
            stillArtwork: true,
            animatedArtwork: true,
            lyrics: true,
            filePath => filePath.Contains("Second", StringComparison.Ordinal) ? 0 : 42);

        // No library track id means no lyrics work for that file, and it is not the album's
        // first file, so it used to emit nothing at all.
        Assert.StartsWith(
            "No sidecar fetch needed (file 2/5: 02 - Second.flac)",
            messages[1],
            StringComparison.Ordinal);
        Assert.StartsWith(
            "Fetching lyrics (file 3/5: 03 - Third.flac)",
            messages[2],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SidecarFetchStepReturnsTheResultWhenItCompletesInTime()
    {
        var result = await AutoTagService.RunSidecarFetchStepAsync(
            _ => Task.FromResult(17),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.FailureMessage);
        Assert.Equal(17, result.Result);
    }

    [Fact]
    public async Task SidecarFetchStepTimesOutInsteadOfBlockingTheBatch()
    {
        var started = DateTimeOffset.UtcNow;

        var result = await AutoTagService.RunSidecarFetchStepAsync(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            },
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("did not finish within", result.FailureMessage, StringComparison.Ordinal);
        // The batch is not blocked: the step is abandoned at the bound.
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SidecarFetchStepStillPropagatesARealRunCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AutoTagService.RunSidecarFetchStepAsync(
                async token =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return true;
                },
                TimeSpan.FromMinutes(5),
                cancellation.Token));
    }

    [Fact]
    public void TimeoutFailureAndCompletionLinesReportTheFileTheReasonAndTheTotals()
    {
        var failure = AutoTagService.DescribeSidecarFetchFailure(
            "/music/Artist/Album One/02 - Second.flac",
            "did not finish within 120s",
            2,
            5);

        Assert.Contains("02 - Second.flac", failure, StringComparison.Ordinal);
        Assert.Contains("file 2/5", failure, StringComparison.Ordinal);
        Assert.Contains("did not finish within 120s", failure, StringComparison.Ordinal);
        Assert.Contains("existing artwork and lyrics were kept", failure, StringComparison.Ordinal);

        var completion = AutoTagService.DescribeSidecarCompletion(
            processed: 5,
            artworkAlbumsServiced: 2,
            lyricsTracksHandled: 5,
            failures: 1);

        Assert.Contains("5 file(s) processed", completion, StringComparison.Ordinal);
        Assert.Contains("2 album artwork fetch(es)", completion, StringComparison.Ordinal);
        Assert.Contains("5 lyrics refresh(es)", completion, StringComparison.Ordinal);
        Assert.Contains("1 failure(s)", completion, StringComparison.Ordinal);
    }
}