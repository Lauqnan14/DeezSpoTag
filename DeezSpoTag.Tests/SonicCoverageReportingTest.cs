using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Phase 3: the numbers the Vibe Analysis panel puts in front of a person.
///
/// <para>These are pure computations, but they are the ones that decide what a
/// library owner believes about their own library, so they get tested directly
/// rather than only through the HTTP surface. A coverage figure that counts
/// out-of-date vectors, or a backlog that stays at zero while tracks are waiting,
/// is worse than no figure at all: it is confidently wrong.</para>
///
/// <para>The vocabulary matters and is easy to break:</para>
/// <list type="bullet">
/// <item><c>TracksWithEmbedding</c> is already net of stale vectors.</item>
/// <item><c>StaleEmbeddings</c> counts vectors whose source file changed.</item>
/// <item><c>PendingEmbeddings</c> is what still needs analysing, stale included.</item>
/// </list>
public sealed class SonicCoverageReportingTest
{
    private static SonicLibraryStatusDto Library(
        string name,
        int totalTracks,
        int analyzed,
        int embedded,
        int stale,
        int unavailable = 0)
        => new()
        {
            LibraryId = Math.Abs(StringComparer.Ordinal.GetHashCode(name)),
            LibraryName = name,
            TotalTracks = totalTracks,
            TracksAnalyzed = analyzed,
            TracksWithEmbedding = embedded,
            StaleEmbeddings = stale,
            TracksUnavailable = unavailable,
        };

    [Fact]
    public void AnEmptyLibrary_ReportsZeroRatherThanDividingByZero()
    {
        var status = new SonicAnalysisStatusDto();

        Assert.Equal(0, status.TotalTracks);
        Assert.Equal(0, status.TotalEmbedded);
        Assert.Equal(0, status.PendingEmbeddings);
        Assert.Equal(0d, status.CoveragePercent);
    }

    [Fact]
    public void ALibraryWithNoTracks_ReportsZeroCoverage()
    {
        var status = new SonicAnalysisStatusDto
        {
            Libraries = new[] { Library("Empty", totalTracks: 0, analyzed: 0, embedded: 0, stale: 0) },
        };

        Assert.Equal(0d, status.CoveragePercent);
    }

    [Fact]
    public void CoverageMatchesTheDesignDocumentsWorkedExample()
    {
        // "12,418 / 12,950 tracks ready, 95.9% coverage" from the Phase 3 design.
        var status = new SonicAnalysisStatusDto
        {
            Libraries = new[] { Library("Main", totalTracks: 12_950, analyzed: 12_500, embedded: 12_418, stale: 0) },
        };

        Assert.Equal(12_418, status.TotalEmbedded);
        Assert.Equal(12_950, status.TotalTracks);
        Assert.Equal(95.9d, status.CoveragePercent);
    }

    [Fact]
    public void CoverageIsRoundedToOneDecimalPlace()
    {
        // 1/3 is 33.333..., which must not surface as a long decimal.
        var status = new SonicAnalysisStatusDto
        {
            Libraries = new[] { Library("Third", totalTracks: 3, analyzed: 3, embedded: 1, stale: 0) },
        };

        Assert.Equal(33.3d, status.CoveragePercent);
    }

    [Fact]
    public void PendingCountsAnalysedTracksWithNoUsableVector()
    {
        // 20 analysed, 12 usable, so 8 are waiting: 5 never embedded and 3 whose
        // file changed after the vector was produced.
        var status = new SonicAnalysisStatusDto
        {
            Libraries = new[] { Library("Main", totalTracks: 30, analyzed: 20, embedded: 12, stale: 3) },
        };

        Assert.Equal(8, status.PendingEmbeddings);
        Assert.Equal(3, status.TotalStale);
    }

    [Fact]
    public void StaleTracksAreCountedOnceAsPendingAndOnceAsStale()
    {
        // The regression for a double subtraction. With 10 analysed, 4 fresh and
        // 6 stale, all 6 are still waiting; the stale figure only says why.
        var status = new SonicAnalysisStatusDto
        {
            Libraries = new[] { Library("Main", totalTracks: 20, analyzed: 10, embedded: 4, stale: 6) },
        };

        Assert.Equal(4, status.TotalEmbedded);
        Assert.Equal(6, status.PendingEmbeddings);
        Assert.Equal(6, status.TotalStale);
    }

    [Fact]
    public void APendingCountNeverGoesNegative()
    {
        // An embedding can outlive the track row it was counted against, so the
        // raw subtraction can undershoot. A negative backlog is nonsense to show.
        var status = new SonicAnalysisStatusDto
        {
            Libraries = new[] { Library("Main", totalTracks: 3, analyzed: 2, embedded: 5, stale: 0) },
        };

        Assert.Equal(0, status.PendingEmbeddings);
    }

    [Fact]
    public void SeveralLibrariesAggregateIntoOneHonestFigure()
    {
        // Coverage has to be weighted by library size, not averaged per library,
        // or a 5-track library would distort a 20,000-track one.
        var status = new SonicAnalysisStatusDto
        {
            Libraries = new[]
            {
                Library("Big", totalTracks: 10_000, analyzed: 9_000, embedded: 9_000, stale: 0),
                Library("Small", totalTracks: 100, analyzed: 100, embedded: 0, stale: 0),
            },
        };

        Assert.Equal(10_100, status.TotalTracks);
        Assert.Equal(9_000, status.TotalEmbedded);
        Assert.Equal(9_100, status.TotalAnalyzed);
        Assert.Equal(100, status.PendingEmbeddings);
        Assert.Equal(89.1d, status.CoveragePercent);
    }

    [Fact]
    public void PerLibraryCoverageUsesThatLibrarysOwnTotals()
    {
        var library = Library("Main", totalTracks: 4, analyzed: 4, embedded: 3, stale: 1);

        Assert.Equal(75d, library.CoveragePercent);
    }

    [Fact]
    public void UnavailableTracksAreReportedSeparatelyFromPending()
    {
        // A file that cannot be read is a different problem from a file that has
        // not been embedded yet, and conflating them hides a broken mount.
        var status = new SonicAnalysisStatusDto
        {
            Libraries = new[] { Library("Main", totalTracks: 10, analyzed: 7, embedded: 5, stale: 1, unavailable: 3) },
        };

        Assert.Equal(3, status.TotalFailed);
        Assert.Equal(2, status.PendingEmbeddings);
        Assert.Equal(1, status.TotalStale);
    }

    [Fact]
    public void StatusCarriesTheModelIdentityAndIndexShape()
    {
        // Read-only context for the panel, not a set of knobs.
        var status = new SonicAnalysisStatusDto
        {
            Enabled = true,
            ModelId = "discogs-effnet-bs64-1",
            ModelVersion = "1",
            EmbeddingVersion = "embedding-v1",
            Dimensions = 1280,
            ReanalyzeBatchSize = 50,
            IndexVectorCount = 12_418,
            IndexMegabytes = 60.6,
            IndexBuildMilliseconds = 412,
        };

        Assert.True(status.Enabled);
        Assert.Equal(1280, status.Dimensions);
        Assert.Equal(12_418, status.IndexVectorCount);
        Assert.Equal(60.6, status.IndexMegabytes);
        Assert.Equal(412, status.IndexBuildMilliseconds);
    }

    [Fact]
    public void DefaultsKeepSonicOffAndTheBatchBounded()
    {
        // Enabling Sonic must be a deliberate act, and the first re-analysis pass
        // must not be able to queue an entire library at once.
        var settings = SonicAnalysisSettingsDto.Defaults();

        Assert.False(settings.Enabled);
        Assert.True(settings.ReanalyzeStale);
        Assert.Equal(50, settings.ReanalyzeBatchSize);
        Assert.Equal(50, settings.Normalize().ReanalyzeBatchSize);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(9, 10)]
    [InlineData(-100, 10)]
    [InlineData(10, 10)]
    [InlineData(250, 250)]
    [InlineData(500, 500)]
    [InlineData(501, 500)]
    [InlineData(10_000, 500)]
    public void ReanalyzeBatchSizeIsClampedToASaneRange(int requested, int expected)
    {
        var settings = new SonicAnalysisSettingsDto { ReanalyzeBatchSize = requested }.Normalize();

        Assert.Equal(expected, settings.ReanalyzeBatchSize);
    }

    [Fact]
    public void NormalizePreservesTheOtherFields()
    {
        var settings = new SonicAnalysisSettingsDto
        {
            Enabled = true,
            ReanalyzeStale = false,
            ReanalyzeBatchSize = 9999,
        }.Normalize();

        Assert.True(settings.Enabled);
        Assert.False(settings.ReanalyzeStale);
        Assert.Equal(500, settings.ReanalyzeBatchSize);
    }

    [Fact]
    public void StatusWithNoLibrariesReportsEveryTotalAsZero()
    {
        var status = new SonicAnalysisStatusDto { Enabled = false };

        Assert.Empty(status.Libraries);
        Assert.Equal(0, status.TotalTracks);
        Assert.Equal(0, status.TotalEmbedded);
        Assert.Equal(0, status.TotalAnalyzed);
        Assert.Equal(0, status.TotalStale);
        Assert.Equal(0, status.TotalFailed);
        Assert.Equal(0, status.PendingEmbeddings);
        Assert.Equal(0d, status.CoveragePercent);
    }
}
