using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Guards against provider-driven title rewrites (MB writing "(Remastered)", "(Live)",
/// "(Instrumental)" over the user's title) and locks the album-boundary batching rule
/// for enhancement runs: batches of at most 40 files, exceeded only by the album that
/// is currently being processed.
/// </summary>
public sealed class AutoTagVariantTitleGuardTest
{
    // --- remaster is a variant intent for drift purposes -----------------------------

    [Theory]
    [InlineData("Song", "Song (2011 Remaster)", true)]
    [InlineData("Song", "Song (Remastered)", true)]
    [InlineData("Song", "Song Remastered 2011", true)]
    [InlineData("Song", "Song - 2011 Remastered Version", true)]
    [InlineData("Song", "Song (Live)", true)]
    [InlineData("Song", "Song (Instrumental)", true)]
    [InlineData("Song (2011 Remaster)", "Song (2012 Remaster)", false)]
    [InlineData("Song (Live)", "Song (Live at Wembley)", false)]
    [InlineData("Song", "Song", false)]
    public void HasVersionDrift_TreatsRemasterAsVariantIntent(string source, string candidate, bool expected)
    {
        Assert.Equal(expected, TrackTitleMatcher.HasVersionDrift(source, candidate));
    }

    // --- same-intent rewrites must preserve the source title wording -----------------

    [Theory]
    [InlineData("Song (Live)", "Song (Live at Wembley)", true)]
    [InlineData("Song (Remastered)", "Song (2011 Remaster)", true)]
    [InlineData("Song (Instrumental)", "Song (Instrumental Version)", true)]
    [InlineData("Song", "Song", false)]
    [InlineData("Song", "Song (Live)", false)]
    [InlineData("Unknown", "Song (Live at Wembley)", false)]
    [InlineData("Song (Live)", "Different Song", false)]
    public void ShouldPreserveSourceTitleWording_OnlyForSameWorkSameIntentDifferentWording(
        string source,
        string incoming,
        bool expected)
    {
        Assert.Equal(expected, TrackTitleMatcher.ShouldPreserveSourceTitleWording(source, incoming));
    }

    [Fact]
    public void Runner_PreserveSourceTitleWording_KeepsSourceVariantTextAndDropsIncomingVersion()
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "PreserveSourceTitleWording",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("LocalAutoTagRunner.PreserveSourceTitleWording not found.");

        var info = new AutoTagAudioInfo { Title = "Song (Live)" };
        var track = new AutoTagTrack { Title = "Song", Version = "Live at Wembley" };
        method.Invoke(null, new object?[] { info, track });

        Assert.Equal("Song (Live)", track.Title);
        Assert.Null(track.Version);
    }

    [Fact]
    public void Runner_PreserveSourceTitleWording_LeavesDifferentIntentUntouched()
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "PreserveSourceTitleWording",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("LocalAutoTagRunner.PreserveSourceTitleWording not found.");

        var info = new AutoTagAudioInfo { Title = "Song" };
        var track = new AutoTagTrack { Title = "Song (Live)", Version = null };
        method.Invoke(null, new object?[] { info, track });

        // Different variant intent is not a wording rewrite — such candidates are
        // rejected by the drift guard instead.
        Assert.Equal("Song (Live)", track.Title);
    }

    // --- noisy-word titles keep their identity protections ---------------------------

    [Theory]
    [InlineData("Master of Puppets", false)]
    [InlineData("Audio", true)]
    [InlineData("official audio", true)]
    [InlineData("lyric video", true)]
    [InlineData("Song", false)]
    [InlineData("Video Games", false)]
    [InlineData("Unknown", true)]
    public void IsWeakMetadataValue_OnlyDisarmsGuardsForJunkValues(string value, bool expected)
    {
        Assert.Equal(expected, TrackIdentityTrust.IsWeakMetadataValue(value));
    }

    // --- MusicBrainz variant gate covers whole-title forms ---------------------------

    [Theory]
    [InlineData("Song", "Song (Live)", false)]
    [InlineData("Song", "Song - Live at Wembley", false)]
    [InlineData("Song", "Song Remastered 2011", false)]
    [InlineData("Song", "Song (2011 Remaster)", false)]
    [InlineData("Song (Live)", "Song (Live at Wembley)", true)]
    [InlineData("Song (Instrumental)", "Song - Instrumental", true)]
    public void MusicBrainzVariantGate_ScansWholeTitle(string source, string candidate, bool expected)
    {
        Assert.Equal(expected, MusicBrainzMatcher.IsVariantCompatible(source, candidate));
    }

    // --- album-boundary batching: 40 files, only the active album may surpass --------

    private static string[] AlbumFiles(params (string Album, int Count)[] albums)
    {
        var files = new List<string>();
        foreach (var (album, count) in albums)
        {
            for (var i = 1; i <= count; i++)
            {
                files.Add($"/library/{album}/{album}-track-{i:00}.flac");
            }
        }

        return files.ToArray();
    }

    [Fact]
    public void BatchRanges_StayAtOrUnderLimitAcrossAlbums()
    {
        var files = AlbumFiles(("A", 30), ("B", 30), ("C", 30));

        var ranges = LocalAutoTagRunner.BuildLibraryWideEnhancementBatchRanges(files, batchSize: 40);

        Assert.All(ranges, range => Assert.True(range.End - range.Start <= 40 + 29, "Only an active album may extend a batch."));
        Assert.Equal(0, ranges[0].Start);
        Assert.True(ranges.All(range => range.End > range.Start));
        Assert.Equal(files.Length, ranges.Sum(range => range.End - range.Start));
    }

    [Fact]
    public void BatchRanges_LetSingleActiveAlbumSurpassTheLimit()
    {
        var files = AlbumFiles(("Big", 95));

        var ranges = LocalAutoTagRunner.BuildLibraryWideEnhancementBatchRanges(files, batchSize: 40);

        var range = Assert.Single(ranges);
        Assert.Equal(0, range.Start);
        Assert.Equal(95, range.End);
    }

    [Fact]
    public void BatchRanges_NeverSplitAnAlbum()
    {
        var files = AlbumFiles(("A", 45), ("B", 45), ("C", 10), ("D", 10));

        var ranges = LocalAutoTagRunner.BuildLibraryWideEnhancementBatchRanges(files, batchSize: 40);

        // Every album's files must land inside exactly one batch range.
        foreach (var albumGroup in files
                     .Select((file, index) => (File: file, Index: index))
                     .GroupBy(entry => GetAlbumSegment(entry.File), StringComparer.OrdinalIgnoreCase))
        {
            var containingRanges = ranges
                .Where(range => range.Start <= albumGroup.Min(entry => entry.Index)
                                && range.End > albumGroup.Max(entry => entry.Index))
                .ToList();
            var albumRange = Assert.Single(containingRanges);
            Assert.True(albumRange.Start <= albumGroup.Min(entry => entry.Index));
            Assert.True(albumRange.End > albumGroup.Max(entry => entry.Index));
        }

        Assert.Equal(files.Length, ranges.Sum(range => range.End - range.Start));
    }

    [Fact]
    public void BatchRanges_EmptyInputYieldsNoRanges()
    {
        Assert.Empty(LocalAutoTagRunner.BuildLibraryWideEnhancementBatchRanges(Array.Empty<string>(), 40));
    }

    [Fact]
    public void Runner_OrdersFilesByMainArtistAlphabeticallyInTwoWaves()
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "OrderFilesForEnhancementRun",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("LocalAutoTagRunner.OrderFilesForEnhancementRun not found.");
        var metaType = typeof(LocalAutoTagRunner).GetNestedType("ArtistSortMeta", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LocalAutoTagRunner.ArtistSortMeta not found.");

        var files = new[]
        {
            "/library/Drake/Album/t1.flac",        // wave 2, drake
            "/library/21 Savage & Drake/Collab/t1.flac", // wave 2, 21 savage (first main artist)
            "/library/Unknown Artist/Unknown Album/t1.flac", // wave 1 (weak identity)
            "/library/Adele/30/t1.flac",           // wave 2, adele
        };
        object Meta(string artistKey, string albumKey, int? track, bool weak) =>
            Activator.CreateInstance(metaType, artistKey, albumKey, track, weak)!;
        var meta = NewMetaDictionary(metaType);
        meta.Add.Invoke(meta.Instance, new[] { files[0], Meta("drake", "album", 1, false) });
        meta.Add.Invoke(meta.Instance, new[] { files[1], Meta("21 savage", "collab", 1, false) });
        meta.Add.Invoke(meta.Instance, new[] { files[2], Meta(string.Empty, string.Empty, 1, true) });
        meta.Add.Invoke(meta.Instance, new[] { files[3], Meta("adele", "30", 1, false) });
        var priority = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var ordered = Assert.IsType<List<string>>(method.Invoke(null, new object?[] { files, meta.Instance, priority }));

        // Wave 1 (weak identity) first, then wave 2 alphabetical by main artist:
        // "21 savage" sorts before "adele" (numbers before letters) and before "drake".
        Assert.Equal(new[] { files[2], files[1], files[3], files[0] }, ordered);
    }

    [Fact]
    public void Runner_OrdersPriorityFilesFirst()
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "OrderFilesForEnhancementRun",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("LocalAutoTagRunner.OrderFilesForEnhancementRun not found.");
        var metaType = typeof(LocalAutoTagRunner).GetNestedType("ArtistSortMeta", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LocalAutoTagRunner.ArtistSortMeta not found.");

        var flagged = "/library/Zed/Album/t1.flac";
        var files = new[] { "/library/Adele/30/t1.flac", flagged };
        object Meta(string artistKey) => Activator.CreateInstance(metaType, artistKey, "album", 1, false)!;
        var meta = NewMetaDictionary(metaType);
        meta.Add.Invoke(meta.Instance, new[] { files[0], Meta("adele") });
        meta.Add.Invoke(meta.Instance, new[] { flagged, Meta("zed") });
        var priority = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { flagged };

        var ordered = Assert.IsType<List<string>>(method.Invoke(null, new object?[] { files, meta.Instance, priority }));

        // The library-DB-flagged file runs in wave 1 even though "zed" sorts last.
        Assert.Equal(new[] { flagged, files[0] }, ordered);
    }

    // --- cross-platform album consistency --------------------------------------------

    [Theory]
    [InlineData("6b6c1e79-8b6b-4b1e-9c2f-2f0f6d5a9c01", "6b6c1e79-8b6b-4b1e-9c2f-2f0f6d5a9c01")]
    [InlineData("123456", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("deezer-album-987", null)]
    public void GenericAlbumIdTags_OnlyCarryGuidShapedIds(string? value, string? expected)
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "ToMusicBrainzShapedId",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("LocalAutoTagRunner.ToMusicBrainzShapedId not found.");

        Assert.Equal(expected, method.Invoke(null, new object?[] { value }));
    }

    [Fact]
    public void AlbumConsensus_IsFolderKeyedAndAdoptsEstablishedWording()
    {
        var runner = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        // The consensus keys on the album folder so every platform pass for the same
        // folder adopts one established identity.
        Assert.Contains("ResolveAlbumFolderKey(context, track)", runner, StringComparison.Ordinal);
        Assert.Contains("AlbumFolderIdentities.TryGetValue(folderKey, out var establishedFolder)", runner, StringComparison.Ordinal);

        // Same edition → adopt the folder's established album wording.
        var folderBody = ExtractMethodBody(runner, "private void ApplyFolderAlbumIdentity(");
        Assert.Contains("AlbumTitleNormalizer.IsSameEdition(establishedFolder.AlbumTitle, track.Album)", folderBody, StringComparison.Ordinal);
        Assert.Contains("track.Album = establishedFolder.AlbumTitle", folderBody, StringComparison.Ordinal);
        Assert.Contains("ApplyEstablishedAlbumIdentity(track, establishedFolder.Identity, context.Platform)", folderBody, StringComparison.Ordinal);

        // Different edition → keep the folder's established edition (never rewrite).
        Assert.Contains("AlbumTitleNormalizer.IsEditionConflict(establishedFolder.AlbumTitle, track.Album)", folderBody, StringComparison.Ordinal);
        Assert.Contains("ApplyEstablishedAlbumIdentity(track, establishedFolder.Identity, context.Platform)", folderBody, StringComparison.Ordinal);
    }

    [Fact]
    public void AlbumConsensus_UsesCompleteIdentityAndMajoritySiblingSeed()
    {
        var runner = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        var consensusBody = ExtractMethodBody(runner, "private void ApplyAlbumIdentityConsensus(");
        Assert.Contains("BuildAlbumIdentityCandidate(track, context.Platform)", consensusBody, StringComparison.Ordinal);

        var readBody = ExtractMethodBody(runner, "private static AlbumIdentity ReadAlbumIdentityFromDirectory(");
        Assert.Contains("BuildMajorityAlbumIdentity", readBody, StringComparison.Ordinal);
        Assert.DoesNotContain("break;", readBody, StringComparison.Ordinal);
        Assert.Contains("MUSICBRAINZ_RELEASE_ID", runner, StringComparison.Ordinal);
        Assert.Contains("MUSICBRAINZ_RELEASEGROUPID", runner, StringComparison.Ordinal);
        Assert.Contains("DEEZER_RELEASE_ID", runner, StringComparison.Ordinal);
        Assert.Contains("SPOTIFY_RELEASE_ID", runner, StringComparison.Ordinal);
        Assert.Contains("ITUNES_RELEASE_ID", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void AlbumConsensus_AppliesCompleteFolderIdentityBeforeTagging()
    {
        var runner = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        var applyBody = ExtractMethodBody(runner, "private static void ApplyEstablishedAlbumIdentity(");
        Assert.Contains("track.ReleaseGroupId = identity.ReleaseGroupId", applyBody, StringComparison.Ordinal);
        Assert.Contains("track.ReleaseCountry = identity.ReleaseCountry", applyBody, StringComparison.Ordinal);
        Assert.Contains("track.Barcode = identity.Barcode", applyBody, StringComparison.Ordinal);
        Assert.Contains("track.ReleaseType = identity.ReleaseType", applyBody, StringComparison.Ordinal);
        Assert.Contains("identity.PlatformReleaseIds", applyBody, StringComparison.Ordinal);
        Assert.Contains("SetOtherValue(track, ReleaseGroupIdRawTag", applyBody, StringComparison.Ordinal);
        Assert.Contains("SetOtherValue(track, BarcodeRawTag", applyBody, StringComparison.Ordinal);
    }

    [Fact]
    public void AlbumConsensus_RemainsSharedByEnhancementAndEnrichmentRuns()
    {
        var runner = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        var consensusIndex = runner.IndexOf("ApplyAlbumIdentityConsensus(context, validationBasis, match.Track)", StringComparison.Ordinal);
        var tagIndex = runner.IndexOf("TagFileAsync(", consensusIndex < 0 ? 0 : consensusIndex, StringComparison.Ordinal);

        Assert.True(consensusIndex >= 0, "Album identity consensus must run in the shared match path.");
        Assert.True(tagIndex > consensusIndex, "Album identity consensus must run before tag writing.");
        Assert.DoesNotContain("isManualEnrichment && ApplyAlbumIdentityConsensus", runner, StringComparison.Ordinal);
        Assert.Contains("public Dictionary<string, FolderAlbumIdentity> AlbumFolderIdentities { get; } = new", runner, StringComparison.Ordinal);
    }

    // --- batch display reflects the real album-boundary batches ----------------------

    [Fact]
    public void BatchDisplay_RunnerReportsActualAlbumBatchPosition()
    {
        var runner = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        var service = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");

        // The batched loop stamps the true range position on every file context.
        var batchBody = ExtractMethodBody(runner, "private async Task ExecuteLibraryWideEnhancementBatchesAsync");
        Assert.Contains("BatchNumber = rangeIndex + 1", batchBody, StringComparison.Ordinal);
        Assert.Contains("BatchCount = ranges.Count", batchBody, StringComparison.Ordinal);
        Assert.Contains("BatchSize = batchEnd - batchStart", batchBody, StringComparison.Ordinal);
        Assert.Contains("BatchProcessed = fileIndex - batchStart + 1", batchBody, StringComparison.Ordinal);

        // The non-batched pass does not fabricate batch positions.
        var plainBody = ExtractMethodBody(runner, "private async Task ExecutePlatformPassesAsync");
        Assert.DoesNotContain("BatchNumber =", plainBody, StringComparison.Ordinal);

        // EmitStatus carries the context batch fields to the service.
        var emitBody = ExtractMethodBody(runner, "private static void EmitStatus(");
        Assert.Contains("BatchNumber = context.BatchNumber", emitBody, StringComparison.Ordinal);
        Assert.Contains("BatchProcessed = context.BatchProcessed", emitBody, StringComparison.Ordinal);

        // The service prefers runner-reported values over fixed-window math.
        var updateBody = ExtractMethodBody(service, "private void UpdateStatus(");
        Assert.Contains("status.BatchNumber is int batchNumber", updateBody, StringComparison.Ordinal);
        Assert.Contains("job.BatchSize = batchSize", updateBody, StringComparison.Ordinal);
    }

    private static string GetAlbumSegment(string path)
        => Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))) ?? string.Empty;

    private static (object Instance, MethodInfo Add) NewMetaDictionary(Type metaType)
    {
        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), metaType);
        var instance = Activator.CreateInstance(dictionaryType)!;
        var add = dictionaryType.GetMethod("Add", new[] { typeof(string), metaType })
            ?? throw new InvalidOperationException("Dictionary.Add not found.");
        return (instance, add);
    }

    private static string ExtractMethodBody(string source, string signatureStart)
    {
        var start = source.IndexOf(signatureStart, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing method starting with: {signatureStart}");
        var bodyStart = source.IndexOf('{', start);
        var depth = 0;
        for (var i = bodyStart; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[bodyStart..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Unterminated method body for: {signatureStart}");
    }

    private static string ReadSource(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Join(current.FullName, Path.Join(relativeParts));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            current = current.Parent;
        }

        throw new InvalidOperationException($"Unable to locate source: {Path.Join(relativeParts)}");
    }
}
