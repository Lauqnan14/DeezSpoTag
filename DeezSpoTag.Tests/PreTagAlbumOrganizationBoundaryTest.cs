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
///         For an external-file operation a verified release is selected after provider matching.
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task FortyFiveExternalFilesAreResolvedInProviderOrderAndReusedWithoutAnotherLookup(bool conflictingFingerprint)
    {
        var root = Path.Join(Path.GetTempPath(), "external-enrichment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new List<string>();
            var recognition = new Dictionary<string, DeezSpoTag.Web.Services.ShazamRecognitionInfo?>(StringComparer.OrdinalIgnoreCase);
            for (var n = 1; n <= 45; n++)
            {
                var path = Path.Join(root, $"Song Number {n:D2}.wav");
                DeliveredAudioQualityGuardTest.WriteWave(path, 16);
                using (var audio = TagLib.File.Create(path))
                {
                    audio.Tag.Title = $"Song Number {n:D2}";
                    audio.Tag.Performers = ["Test Performer"];
                    audio.Tag.AlbumArtists = ["Test Performer"];
                    audio.Tag.Album = "Original Album";
                    audio.Save();
                }
                files.Add(path);
                recognition[path] = new() { Title = $"Song Number {n:D2}", Artist = "Test Performer" };
            }
            if (conflictingFingerprint) recognition[files[0]] = new() { Title = "Completely Different Recording", Artist = "Another Performer" };
            var expectedCount = conflictingFingerprint ? 44 : 45;
            var calls = new List<string>();
            using var http = new System.Net.Http.HttpClient(new ExternalCatalogHandler(calls));
            var apple = new ItunesClient(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<ItunesClient>.Instance);
            apple.SetRateLimit(int.MaxValue);
            var deezer = new DeezerClient(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezerClient>.Instance);
            var runner = (LocalAutoTagRunner)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LocalAutoTagRunner));
            void Field(string name, object value) => typeof(LocalAutoTagRunner).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(runner, value);
            Field("_logger", Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalAutoTagRunner>.Instance);
            Field("_itunesMatcher", new ItunesMatcher(apple));
            Field("_deezerMatcher", new DeezerMatcher(deezer, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezerMatcher>.Instance));
            Field("_shazamRecognitionService", System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DeezSpoTag.Web.Services.ShazamRecognitionService)));
            var config = new LocalAutoTagRunner.AutoTagRunnerConfig
            {
                ManualReleasePreference = "single", ManualDestinationFolderId = 7, MaterializeToTemplatePath = true,
                Platforms = new() { "itunes", "deezer" }, Tags = new() { "title", "artist", "album" }
            };
            var planType = typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunPlan", BindingFlags.NonPublic)!;
            var plan = Activator.CreateInstance(planType, nonPublic: true)!;
            void Set(string name, object value) => planType.GetProperty(name)!.SetValue(plan, value);
            var settings = new DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings { DownloadLocation = root };
            Set("JobId", "test"); Set("ConfigPath", Path.Join(root, "runtime.json")); Set("Config", config);
            Set("TargetPath", root); Set("Settings", settings); Set("TagSettings", new DeezSpoTag.Core.Models.Settings.TagSettings());
            Set("Files", files); Set("ShazamCache", recognition); Set("ForceShazamMatch", true); Set("EnableShazamFallback", true);
            Set("EffectivePlatforms", new List<string> { "itunes", "deezer" });
            Set("MatchingConfig", new AutoTagMatchingConfig { PreferredReleaseType = "single", PreferAvailableRelease = true });
            Set("PlatformSupportedTags", Activator.CreateInstance(planType.GetProperty("PlatformSupportedTags")!.PropertyType)!);
            var cacheType = typeof(LocalAutoTagRunner).GetNestedType("JobMatchCacheState", BindingFlags.NonPublic)!;
            var cache = Activator.CreateInstance(cacheType, nonPublic: true)!;
            var statuses = new List<DeezSpoTag.Web.Services.TaggingStatusWrap>();
            var logs = new List<string>();
            var prepare = typeof(LocalAutoTagRunner).GetMethod("PrepareExternalFileEnrichmentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (System.Threading.Tasks.Task)prepare.Invoke(runner, new object[] { plan, cache,
                new Action<DeezSpoTag.Web.Services.TaggingStatusWrap>(statuses.Add), new Action<string>(logs.Add), default(System.Threading.CancellationToken) })!;
            Assert.Equal(expectedCount, logs.Count(line => line.Contains("itunes match starting", StringComparison.Ordinal)));
            Assert.Equal(expectedCount, logs.Count(line => line.Contains("deezer match starting", StringComparison.Ordinal)));
            Assert.True(calls.FindLastIndex(call => call.StartsWith("itunes:", StringComparison.Ordinal))
                < calls.FindIndex(call => call.StartsWith("deezer:", StringComparison.Ordinal)));
            Assert.Equal(45, statuses.Count(status => status.Status?.Outcome == "fingerprint_identified"));
            var frozen = (System.Collections.IDictionary)planType.GetProperty("FrozenManualReleases")!.GetValue(plan)!;
            Assert.True(frozen.Count == expectedCount, $"Expected {expectedCount} frozen releases, got {frozen.Count}.\n" + string.Join("\n", logs));
            Assert.Equal(conflictingFingerprint, statuses.Any(status => status.Status?.Outcome == "fingerprint_conflict"));
            for (var index = conflictingFingerprint ? 1 : 0; index < 45; index++)
            {
                var release = frozen[index]!;
                Assert.Equal(index < 15 ? "album" : "single", release.GetType().GetProperty("ReleaseType")!.GetValue(release));
                Assert.Equal(index < 15 ? "Verified Album" : $"Single {index + 1:D2}", release.GetType().GetProperty("Album")!.GetValue(release));
            }
            var count = calls.Count;
            await (System.Threading.Tasks.Task)prepare.Invoke(runner, new object[] { plan, cache,
                new Action<DeezSpoTag.Web.Services.TaggingStatusWrap>(statuses.Add), new Action<string>(logs.Add), default(System.Threading.CancellationToken) })!;
            Assert.Equal(count, calls.Count);
            using var unchanged = TagLib.File.Create(files[0]);
            Assert.Equal("Original Album", unchanged.Tag.Album);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ExternalCatalogHandler(List<string> calls) : System.Net.Http.HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var apple = uri.Host == "itunes.apple.com";
            calls.Add((apple ? "itunes:" : "deezer:") + uri.PathAndQuery);
            var decoded = Uri.UnescapeDataString(uri.Query).Replace('+', ' ');
            var number = System.Text.RegularExpressions.Regex.Match(decoded, "Song Number (\\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var n = number.Success ? int.Parse(number.Groups[1].Value) : int.TryParse(uri.Segments.Last(), out var id) ? id - 1000 : 1;
            object payload;
            if (apple)
            {
                object Track(bool single) => new { wrapperType = "track", kind = "song", artistName = "Test Performer",
                    trackName = $"Song Number {n:D2}", trackId = 1000 + n, collectionId = single ? 300 : 200,
                    collectionName = single ? $"Single {n:D2}" : "Verified Album", trackCount = single ? 1 : 45,
                    trackNumber = single ? 1 : n, trackTimeMillis = 1000 };
                payload = new { results = n <= 15 ? new[] { Track(false) } : n <= 30 ? new[] { Track(true) } : new[] { Track(false), Track(true) } };
            }
            else if (uri.AbsolutePath.StartsWith("/album/", StringComparison.Ordinal))
                payload = new { id = 200, title = "Verified Album", record_type = "album", nb_tracks = 45,
                    artist = new { id = 1, name = "Test Performer" }, contributors = Array.Empty<object>(), genres = new { data = Array.Empty<object>() } };
            else
            {
                var track = new { id = 1000 + n, title = $"Song Number {n:D2}", duration = 1,
                    artist = new { id = 1, name = "Test Performer" }, album = new { id = 200, title = "Verified Album" },
                    track_position = n, disk_number = 1 };
                payload = uri.AbsolutePath.StartsWith("/search/", StringComparison.Ordinal) ? new { data = new[] { track } } : (object)track;
            }
            return System.Threading.Tasks.Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(payload))
            });
        }
    }

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

        var organizeIndex = source.IndexOf("OrganizeExternalFileBatchBeforeTagging", passIndex, StringComparison.Ordinal);
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
