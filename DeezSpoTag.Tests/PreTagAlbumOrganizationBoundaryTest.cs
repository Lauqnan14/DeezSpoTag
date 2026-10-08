using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Guardrails for the pre-tag album-organization boundary.
/// </summary>
/// <remarks>
///     <para>
///         For an external-file operation the first accepted platform match establishes the file's identity.
///         That match is the point at which the whole album must be organized, before a single tag is
///         written, so every track of one release lands in one staging folder.
///     </para>
///     <para>
///         The previous behaviour materialized each file immediately before its own tag write, so track 1 was
///         moved and tagged while track 2 was still being identified - and track 2 could resolve to a
///         different folder. An album would arrive split across two directories, each with a partial set of
///         tags, which is precisely the state the folder-uniformity rules exist to prevent.
///     </para>
/// </remarks>
public sealed class PreTagAlbumOrganizationBoundaryTest
{
    // ─────────────────────────── Shazam is a recognizer, not a gate ───────────────────────────

    /// <summary>
    ///     A Shazam miss must let the rest of the chain run.
    /// </summary>
    /// <remarks>
    ///     The old branch sent the file straight to review and returned, so MusicBrainz and every later
    ///     platform were never consulted. For a track a recognizer could not hear but a catalogue could
    ///     identify, that produced a review card instead of a tagged file - and the resulting review was
    ///     indistinguishable from a file no platform could identify at all.
    /// </remarks>
    [Fact]
    public void AShazamNoMatchNoLongerSendsTheFileToReviewOnItsOwn()
    {
        var source = ReadRunner("LocalAutoTagRunner.RunOrchestration.cs");

        Assert.DoesNotContain(
            "\"Shazam could not identify the staged audio file.\"",
            source,
            StringComparison.Ordinal);

        // The remaining branch already continued the chain; the manual-only twin that returned early is gone.
        Assert.DoesNotContain(
            "isManualEnrichment && shazamResult.FailureKind == ShazamFailureKind.NoMatch",
            source,
            StringComparison.Ordinal);

        Assert.Contains(
            "shazamResult.FailureKind == ShazamFailureKind.NoMatch",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     Review is still reachable, and only when the whole enabled chain failed.
    /// </summary>
    [Fact]
    public void ReviewSurvivesWhenTheWholeChainYieldsNoIdentity()
    {
        var source = ReadRunner("LocalAutoTagRunner.RunOrchestration.cs");
        var emitting = ReadRunner("LocalAutoTagRunner.StatusEmitting.cs");

        Assert.Contains("EmitReviewStatus(", source, StringComparison.Ordinal);
        Assert.Contains("ReviewedFiles.Add(", source, StringComparison.Ordinal);

        // The last-platform rejection helper still ends in review rather than a silent skip.
        Assert.Contains(
            "if (IsLastPlatform(context) && !WasTaggedByAnyPlatform(context))",
            emitting,
            StringComparison.Ordinal);
    }

    // ─────────────────────────── the boundary itself ───────────────────────────

    /// <summary>
    ///     Only external-file operations organize before tagging.
    /// </summary>
    /// <remarks>
    ///     An ordinary library enrichment run must not reorganize the library root as a side effect of being
    ///     tagged, and download_enrichment leaves its move to orchestration. Both keep the existing per-file
    ///     behaviour exactly.
    /// </remarks>
    [Theory]
    [InlineData("album", 7L, true)]
    [InlineData("single", 1L, true)]
    [InlineData("album", null, false)]
    [InlineData(null, 7L, false)]
    [InlineData(null, null, false)]
    [InlineData("", 7L, false)]
    [InlineData("album", 0L, false)]
    public void ThePrePassIsGatedOnTheExternalFileOperationShape(
        string? releasePreference,
        long? destinationFolderId,
        bool expected)
        => Assert.Equal(expected, IsExternalFileOrganizationRun(releasePreference, destinationFolderId));

    /// <summary>
    ///     The organization pass must run for the whole batch before the platform loop starts.
    /// </summary>
    /// <remarks>
    ///     This is the whole point of the change. A pre-pass invoked per (file, platform) would still tag
    ///     track 1 before track 2 was identified, which is the defect being fixed. The ordering assertion is
    ///     the only thing that distinguishes a real boundary from a helper called in the wrong place.
    /// </remarks>
    [Fact]
    public void OrganizationRunsForTheWholeBatchBeforeThePlatformLoopStarts()
    {
        var source = ReadRunner("LocalAutoTagRunner.RunOrchestration.cs");

        var passIndex = source.IndexOf("ExecuteLibraryWideEnhancementBatchesAsync", StringComparison.Ordinal);
        Assert.True(passIndex > 0, "The library-wide pass was not found.");

        var organizeIndex = source.IndexOf("OrganizeExternalFileBatchBeforeTaggingAsync", passIndex, StringComparison.Ordinal);
        var platformLoopIndex = source.IndexOf(
            "for (var platformIndex = firstPlatformIndex; platformIndex < plan.PlatformCount; platformIndex++)",
            passIndex,
            StringComparison.Ordinal);

        Assert.True(organizeIndex > 0, "The pre-tag organization pass was not found inside the library-wide pass.");
        Assert.True(
            platformLoopIndex > organizeIndex,
            "The organization pass must be invoked before the platform loop begins, or track 1 is still tagged "
            + "before track 2 has been identified.");

        // It has to sit inside the batch loop too: albums are organized as complete groups.
        var batchLoopIndex = source.IndexOf(
            "for (var rangeIndex = resumeRangeIndex; rangeIndex < ranges.Count; rangeIndex++)",
            passIndex,
            StringComparison.Ordinal);
        Assert.True(batchLoopIndex > 0 && organizeIndex > batchLoopIndex);
    }

    /// <summary>
    ///     The tagging pass must reuse the pre-organized path instead of moving the file again.
    /// </summary>
    [Fact]
    public void LaterPlatformsReuseThePreOrganizationPath()
    {
        var apply = ReadRunner("LocalAutoTagRunner.PlatformMatching.cs");
        var start = apply.IndexOf("private async Task ApplyResolvedMatchAsync", StringComparison.Ordinal);
        Assert.True(start > 0);
        var method = apply[start..];

        // This is what makes the reuse work: a file the pre-pass already organized must not be materialized
        // a second time when its own tagging platform runs.
        Assert.Contains(
            "context.Plan.MaterializedManualPaths.TryGetValue(context.FileIndex, out var materializedPath)",
            method,
            StringComparison.Ordinal);

        // And the plan is rewritten in place, so every later platform sees the new location.
        Assert.Contains("context.Plan.Files[context.FileIndex] = context.File", method, StringComparison.Ordinal);
    }

    // ─────────────────────────── real album-uniformity behaviour ───────────────────────────

    /// <summary>
    ///     Every track of one recognized album resolves to a single shared staging root.
    /// </summary>
    /// <remarks>
    ///     The album case the boundary exists for, against real directories rather than a source string: two
    ///     tracks of one release and one of another must be handed the root of their own album, so both
    ///     tracks of the first land in one folder and the third does not join them.
    /// </remarks>
    [Fact]
    public void EveryTrackOfOneAlbumResolvesToASingleSharedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dst-pretag-{Guid.NewGuid():N}");
        var albumA = Path.Combine(root, "Boards of Canada", "Music Has the Right to Children");
        var albumB = Path.Combine(root, "Boards of Canada", "Geogaddi");
        try
        {
            Directory.CreateDirectory(albumA);
            Directory.CreateDirectory(albumB);

            // Two tracks of one release, plus one of a different release.
            var oneAlbum = new List<string?> { albumA, albumA };
            var other = new List<string?> { albumB };

            var sharedForOneAlbum = ResolveSharedAlbumRoot(oneAlbum);
            var sharedForOther = ResolveSharedAlbumRoot(other);

            Assert.Equal(Normalize(albumA), Normalize(sharedForOneAlbum!));
            Assert.Equal(Normalize(albumB), Normalize(sharedForOther!));

            // The decisive assertion: two tracks of one album get the SAME root, and a different album does not.
            Assert.NotEqual(Normalize(sharedForOneAlbum!), Normalize(sharedForOther!));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    ///     A group whose members disagree about their root resolves to the first established one, not to
    ///     whichever member happens to be processed last.
    /// </summary>
    [Fact]
    public void ADisagreeingGroupResolvesToTheFirstEstablishedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dst-pretag-disagree-{Guid.NewGuid():N}");
        try
        {
            var first = Path.Combine(root, "first");
            var second = Path.Combine(root, "second");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);

            var resolved = ResolveSharedAlbumRoot([first, second]);

            // First-wins keeps the whole group in one place; picking arbitrarily would split the album again.
            Assert.Equal(Normalize(first), Normalize(resolved!));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void AGroupWithNoEstablishedRootFallsBackToTheSuppliedRoot()
    {
        var fallback = Path.Combine(Path.GetTempPath(), "dst-pretag-fallback");
        var resolved = ResolveSharedAlbumRoot([null, null], fallback);

        Assert.NotNull(resolved);
        Assert.Equal(Normalize(fallback), Normalize(resolved!));
    }

    [Fact]
    public void AnEmptyGroupResolvesToNothing()
        => Assert.Null(ResolveSharedAlbumRoot([]));

    /// <summary>
    ///     A file already sitting at its organized path is not treated as a different file.
    /// </summary>
    /// <remarks>
    ///     The pre-pass rewrites the runtime config's target paths as it organizes, so after a restart the
    ///     plan already points at the organized location and re-running organization must be inert. This is
    ///     what makes the "interrupted after organization" resume case safe.
    /// </remarks>
    [Fact]
    public void AnAlreadyOrganizedPathIsRecognisedAsTheSameFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dst-pretag-idem-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var organized = Path.Combine(root, "Boards of Canada", "Roygbiv", "01 Track.flac");
            var elsewhere = Path.Combine(root, "staging", "01 Track.flac");
            Directory.CreateDirectory(Path.GetDirectoryName(organized)!);
            Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
            File.WriteAllText(organized, "audio");

            Assert.True(PathsReferToSameFile(organized, organized));
            Assert.False(PathsReferToSameFile(organized, elsewhere));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    ///     A collision with an unrelated file must not overwrite it.
    /// </summary>
    /// <remarks>
    ///     Two different releases can legitimately resolve to the same filename. Overwriting the other file
    ///     would destroy a track the reader already has.
    /// </remarks>
    [Fact]
    public void ACollisionAllocatesANewNameInsteadOfOverwriting()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dst-pretag-collide-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var destination = Path.Combine(root, "01 Track.flac");
            File.WriteAllText(destination, "someone else's audio");

            var source = Path.Combine(root, "mine.flac");
            File.WriteAllText(source, "mine");

            var method = typeof(LocalAutoTagRunner).GetMethod(
                "ResolveTemplateMaterializationDestination",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var settings = new Core.Models.Settings.DeezSpoTagSettings();

            var resolved = (string)method.Invoke(null, [source, destination, settings])!;

            Assert.NotEqual(Normalize(destination), Normalize(resolved));
            Assert.Equal("someone else's audio", File.ReadAllText(destination));
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ─────────────────────────── helpers ───────────────────────────

    private static bool IsExternalFileOrganizationRun(string? releasePreference, long? destinationFolderId)
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "IsExternalFileOrganizationRun",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        return (bool)method.Invoke(null, [releasePreference, destinationFolderId])!;
    }

    private static string? ResolveSharedAlbumRoot(IReadOnlyList<string?> albumRoots, string? fallback = null)
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "ResolveSharedAlbumRoot",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        return (string?)method.Invoke(null, [albumRoots, fallback]);
    }

    private static bool PathsReferToSameFile(string left, string right)
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "PathsReferToSameFile",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        return (bool)method.Invoke(null, [left, right])!;
    }

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    private static string ReadRunner(string fileName)
        => File.ReadAllText(Path.Join(ResolveRepoRoot(), "DeezSpoTag.Web", "Services", "AutoTag", fileName));

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Services")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}