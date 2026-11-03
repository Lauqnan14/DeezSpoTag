using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Behavioural tests for the three ways a Soulseek staged sidecar pass failed to deliver.
/// </summary>
/// <remarks>
///     Each of these was a promise the staged pass made and did not keep: it resolved no artwork, it
///     resolved no lyrics, and it could deliver a sidecar under a name nothing would pair it with.
/// </remarks>
public sealed class SoulseekStagedSidecarDeliveryTest
{
    [Fact]
    public async System.Threading.Tasks.Task ShazamSoulseekSkipsCatalogPrefetchBeforeAudioAcquisition()
    {
        var helper = typeof(DeezSpoTag.Services.Download.Soulseek.SoulseekEngineProcessor).Assembly.GetType("DeezSpoTag.Services.Download.Shared.EngineQueueProcessorHelper")!;
        var contextType = helper.GetNestedType("QueueWorkContext`1",System.Reflection.BindingFlags.NonPublic)!
            .MakeGenericType(typeof(DeezSpoTag.Services.Download.Soulseek.SoulseekQueueItem));
        var work = Activator.CreateInstance(contextType)!;
        contextType.GetProperty("Payload")!.SetValue(work,new DeezSpoTag.Services.Download.Soulseek.SoulseekQueueItem {DownloadOrigin="shazam"});
        // A provider-prefetch context must not be read at all for this origin. Its uninitialized fields
        // deliberately fail if the download tries to prepare catalogue artwork or lyrics.
        var context = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(DeezSpoTag.Services.Download.Shared.EngineAudioPostDownloadHelper.EngineTrackContext));
        var method = helper.GetMethod("QueuePrefetchIfNeededAsync",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(DeezSpoTag.Services.Download.Soulseek.SoulseekQueueItem));
        await (System.Threading.Tasks.Task)method.Invoke(null,[work,context])!;
    }

    [Theory]
    [InlineData("jpg", true, false)]
    [InlineData("jpg", true, true)]
    [InlineData("jpg,png", true, false)]
    [InlineData("jpg", false, false)]
    public async System.Threading.Tasks.Task AcceptedPeerArtworkUsesSharedEncodingAndEmbeddingWithoutChangingAudioIdentity(string formats, bool saveArtwork, bool damaged)
    {
        var root = Path.Join(Path.GetTempPath(),"peer-cover-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            var audio = Path.Join(root,"song.wav"); DeliveredAudioQualityGuardTest.WriteWave(audio,16);
            var samplesBefore = ReadWaveSamples(audio);
            var cover = Path.Join(root,"Front Cover.png");
            using(var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(8,8))
                SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image,cover);
            if(damaged) File.WriteAllText(cover,"Not an image");
            var downloader = System.Reflection.DispatchProxy.Create<DeezSpoTag.Services.Download.Soulseek.ISoulseekDownloadService,SidecarProxy>();
            ((SidecarProxy)(object)downloader).Handler = (method,arguments) => {
                Assert.Equal("PromoteAcceptedAudioAsync",method.Name);return System.Threading.Tasks.Task.FromResult(audio);
            };
            var type = typeof(DeezSpoTag.Services.Download.Soulseek.SoulseekEngineProcessor);
            var processor = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            type.GetField("_downloader",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(processor,downloader);
            type.GetField("_logger",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(processor,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Download.Soulseek.SoulseekEngineProcessor>.Instance);
            var payload = new DeezSpoTag.Services.Download.Soulseek.SoulseekQueueItem {DownloadOrigin="shazam",Album="Album",Artist="Artist",SoulseekPeerArtworkPath=cover};
            var settings = new DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings {SaveArtwork=saveArtwork,LocalArtworkFormat=formats,LocalArtworkSize=2,EmbeddedArtworkSize=4,EmbedMaxQualityCover=false,
                Soulseek=new(){UsePeerArtwork=true},Tags=new(){Cover=true}};
            var method = type.GetMethod("AcceptAcquiredAudioAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            Assert.Equal(audio,await (System.Threading.Tasks.Task<string>)method.Invoke(processor,[payload,audio,settings,System.Threading.CancellationToken.None])!);
            if(damaged) {
                Assert.Equal(samplesBefore,ReadWaveSamples(audio));
                Assert.False(File.Exists(Path.Join(root,"cover.jpg")));
                return;
            }
            if (saveArtwork) {
                foreach(var format in formats.Split(',')) {
                    using var sidecar = SixLabors.ImageSharp.Image.Load(Path.Join(root,"cover."+format)); Assert.Equal(2,sidecar.Width);
                }
            } else {
                Assert.Equal(string.Empty,payload.SoulseekPeerArtworkPath);
                Assert.False(File.Exists(Path.Join(root,"cover.jpg")));
            }
            using var tagged = TagLib.File.Create(audio); var picture=Assert.Single(tagged.Tag.Pictures);
            Assert.Equal(TagLib.PictureType.FrontCover,picture.Type);
            using var embedded=SixLabors.ImageSharp.Image.Load(picture.Data.Data);Assert.Equal(4,embedded.Width);
            Assert.False(File.Exists(cover));
            Assert.Equal(samplesBefore,ReadWaveSamples(audio));
        } finally {Directory.Delete(root,true);}
    }

    [Theory]
    [InlineData("shazam", true, true, 1, false)]
    [InlineData("shazam", true, true, 1, true)]
    [InlineData("library", true, true, 0, false)]
    [InlineData("shazam", false, false, 0, false)]
    public async System.Threading.Tasks.Task PeerSidecarAcquisitionDiscoversAndNormalizesOnlyEnabledShazamAssets(
        string origin, bool artwork, bool lyrics, int expectedFetches, bool existingLyrics)
    {
        var root = Path.Join(Path.GetTempPath(), "peer-sidecars-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fetches = 0;
            var search = System.Reflection.DispatchProxy.Create<DeezSpoTag.Services.Download.Soulseek.ISoulseekSearchService, SidecarProxy>();
            ((SidecarProxy)(object)search).Handler = (method, arguments) => {
                Assert.Equal("BrowseAsync", method.Name);
                Assert.Equal("Album", arguments![1]);
                System.Collections.Generic.IReadOnlyList<DeezSpoTag.Integrations.Soulseek.SlskdDirectory> listing = [new() {
                    Directory = "Album", Files = [new() {Filename="front cover.png"}, new() {Filename="back.jpg"},
                        new() {Filename="11 Song.lrc"}, new() {Filename="12 Other.lrc"}] }];
                return System.Threading.Tasks.Task.FromResult(listing);
            };
            var transfer = System.Reflection.DispatchProxy.Create<DeezSpoTag.Services.Download.Soulseek.ISoulseekTransferService, SidecarProxy>();
            ((SidecarProxy)(object)transfer).Handler = (method, arguments) => {
                Assert.Equal("FetchSidecarsAsync", method.Name); fetches++;
                var selected = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<string>>(arguments![1]);
                Assert.Equal(new[] {"Album/front cover.png", "Album/11 Song.lrc"}, selected);
                var output = Assert.IsType<string>(arguments[2]);
                var cover = Path.Join(output, "front cover.png");
                using (var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(8,8))
                    SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, cover);
                var words = Path.Join(output, "11 Song.lrc"); File.WriteAllText(words,"[00:01.00]Peer words");
                System.Collections.Generic.IReadOnlyDictionary<string,string> result = new System.Collections.Generic.Dictionary<string,string> {
                    ["Album/front cover.png"]=cover, ["Album/11 Song.lrc"]=words };
                return System.Threading.Tasks.Task.FromResult(result);
            };
            var type = typeof(DeezSpoTag.Services.Download.Soulseek.SoulseekEngineProcessor);
            var processor = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            type.GetField("_search",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(processor,search);
            type.GetField("_sidecars",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(processor,transfer);
            type.GetField("_logger",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(processor,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Download.Soulseek.SoulseekEngineProcessor>.Instance);
            var payload = new DeezSpoTag.Services.Download.Soulseek.SoulseekQueueItem { DownloadOrigin=origin,
                Artist="Artist", Title="Song", Album="Album", SoulseekUsername="peer", FilePath=Path.Join(root,"Artist - Song.flac") };
            var settings = new DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings { SaveArtwork=true, SaveLyrics=true,
                Soulseek=new() {UsePeerArtwork=artwork,UsePeerLyrics=lyrics} };
            if(existingLyrics) File.WriteAllText(Path.Join(root,"Artist - Song.lrc"),"Existing user lyrics");
            var method = type.GetMethod("TakePeerSidecarsAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
            await (System.Threading.Tasks.Task)method.Invoke(processor,[payload,
                new DeezSpoTag.Services.Download.Soulseek.SoulseekCompletedFile("peer","Album/11 Song.flac",payload.FilePath,100),
                settings,System.Threading.CancellationToken.None])!;
            Assert.Equal(expectedFetches,fetches);
            if(expectedFetches>0) {
                Assert.Equal(Path.Join(root,"front cover.png"),payload.SoulseekPeerArtworkPath);
                Assert.True(File.Exists(payload.SoulseekPeerArtworkPath));
                Assert.Equal(Path.Join(root,"Artist - Song.lrc"),payload.SoulseekPeerLyricsPath);
                Assert.Equal(existingLyrics ? "Existing user lyrics" : "[00:01.00]Peer words",File.ReadAllText(payload.SoulseekPeerLyricsPath));
            }
        }
        finally {Directory.Delete(root,recursive:true);}
    }

    private static byte[] ReadWaveSamples(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        reader.BaseStream.Position = 12;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var kind = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
            var length = reader.ReadInt32();
            if (kind == "data") return reader.ReadBytes(length);
            reader.BaseStream.Position += length + (length % 2);
        }
        throw new InvalidDataException("WAV sample data missing.");
    }

    public class SidecarProxy : System.Reflection.DispatchProxy
    {
        public Func<System.Reflection.MethodInfo,object?[]?,object?> Handler {get;set;} = null!;
        protected override object? Invoke(System.Reflection.MethodInfo? method,object?[]? arguments) => Handler(method!,arguments);
    }

    /// <summary>
    ///     Forcing the profile's lyrics must not switch album artwork off.
    /// </summary>
    /// <remarks>
    ///     The two flags answer independent questions, and coupling them meant every external-file operation
    ///     - which forces the profile's lyrics - did no cover work at all, however clearly the profile asked
    ///     for artwork. A Soulseek release on no streaming service often has its only artwork in the peer's
    ///     folder, so this is the category that matters most here.
    /// </remarks>
    [Fact]
    public void AnArtworkEnabledProfileStillGetsArtworkWork()
    {
        var source = ReadSource("AutoTagService.EnhancementWorkflows.cs");
        var start = source.IndexOf("private async Task<EnhancementWorkflowOutcome> RunConfiguredSidecarsAsync", StringComparison.Ordinal);
        Assert.True(start > 0, "RunConfiguredSidecarsAsync was not found.");
        var method = source[start..];
        method = method[..method.IndexOf("if (!runLyrics && !runCovers)", StringComparison.Ordinal)];

        var runCovers = method;
        Assert.Contains("var runCovers =", runCovers, StringComparison.Ordinal);

        // The decisive property: runCovers must NOT be gated on the lyrics flag being off.
        var assignment = runCovers[runCovers.LastIndexOf("var runCovers =", StringComparison.Ordinal)..];
        assignment = assignment[..assignment.IndexOf(';', StringComparison.Ordinal)];
        Assert.DoesNotContain("!forceProfileLyrics", assignment, StringComparison.Ordinal);
        Assert.Contains("HasExplicitCoverActions", assignment, StringComparison.Ordinal);
        Assert.Contains("sidecarEnabled", assignment, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The staged pass must not force lyrics the profile never enabled.
    /// </summary>
    /// <remarks>
    ///     Forcing means a lookup runs regardless of configuration. The phase already decides which
    ///     categories are enabled from the profile, so forcing past that decision is how a category the
    ///     reader switched off still costs network calls.
    /// </remarks>
    [Fact]
    public void TheStagedPassRunsOnlyTheEnabledCategories()
    {
        var source = ReadSource("AutoTagService.EnhancementWorkflows.cs");
        var start = source.IndexOf("private async Task RunExternalFileStagedSidecarsAsync", StringComparison.Ordinal);
        Assert.True(start > 0);
        var method = source[start..];
        method = method[..method.IndexOf("RunManualEnrichmentBatchSidecarsAsync", StringComparison.Ordinal)];

        Assert.Contains("forceProfileLyrics: false,", method, StringComparison.Ordinal);
        Assert.Contains("HasSidecarLyricsActions(enhancementRoot)", method, StringComparison.Ordinal);
        Assert.Contains("HasExplicitCoverActions(enhancementRoot)", method, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A staged file with no library id still gets its lyrics handled.
    /// </summary>
    /// <remarks>
    ///     <c>SidecarFetchPlan</c> required a library track id, so every staged file resolved trackId 0 and was
    ///     reported as handling no lyrics - the whole staged pass did nothing for the category a reader is
    ///     most likely to want, while appearing to run.
    /// </remarks>
    [Fact]
    public void AStagedFileWithoutALibraryIdStillHandlesLyrics()
    {
        var source = ReadSource("AutoTagService.EnhancementWorkflows.cs");

        var planStart = source.IndexOf("internal sealed class SidecarFetchPlan", StringComparison.Ordinal);
        Assert.True(planStart > 0, "SidecarFetchPlan was not found.");
        var plan = source[planStart..];
        plan = plan[..plan.IndexOf("internal readonly record struct SidecarFetchScope", StringComparison.Ordinal)];

        var resolve = plan[plan.IndexOf("public SidecarFetchScope Resolve", StringComparison.Ordinal)..];
        resolve = resolve[..resolve.IndexOf('}', resolve.IndexOf("return new SidecarFetchScope", StringComparison.Ordinal))];

        Assert.Contains("identityResolvedFromTags", resolve, StringComparison.Ordinal);
        Assert.Contains("trackId > 0 || identityResolvedFromTags", resolve, StringComparison.Ordinal);

        // And the call site has to pass the flag, or the new parameter is decorative.
        Assert.Contains(
            "sidecarRunPlan.Resolve(filePath, trackId, resolveFileIdentityFromTags)",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     The staged lyrics lookup is planned and executed through the file, not a library row.
    /// </summary>
    /// <Fact]
    public void StagedLyricsArePlannedAndWrittenThroughTheFile()
    {
        var workflows = ReadSource("AutoTagService.EnhancementWorkflows.cs");
        Assert.Contains("_lyricsRefreshQueueService.PlanStagedFileRefresh(", workflows, StringComparison.Ordinal);
        Assert.Contains("_lyricsRefreshQueueService.RefreshStagedFileNowAsync(", workflows, StringComparison.Ordinal);

        // The staged path must bypass the library-batch runner, which cannot work without a track id.
        var start = workflows.IndexOf("if (needs.RequiresLyricsStep)", StringComparison.Ordinal);
        Assert.True(start > 0);
        var method = workflows[start..];
        method = method[..method.IndexOf("var verdict = ClassifyLyricsOutcome", StringComparison.Ordinal)];

        var stagedBranch = method.IndexOf("RefreshStagedFileNowAsync(", StringComparison.Ordinal);
        var batchBranch = method.IndexOf("RunLyricsRefreshForBatchAsync(", StringComparison.Ordinal);
        Assert.True(stagedBranch > 0, "The staged lyrics branch is missing.");
        Assert.True(stagedBranch < batchBranch, "The staged branch must come first; it returns before the library batch path.");
    }

    /// <summary>
    ///     The staged lyrics entry points exist and do not require the library.
    /// </summary>
    [Fact]
    public void TheStagedLyricsEntryPointsDoNotRequireALibraryRepository()
    {
        var service = ReadSource("LyricsRefreshQueueService.cs");

        Assert.Contains("public LyricsRefreshPlan PlanStagedFileRefresh(", service, StringComparison.Ordinal);
        Assert.Contains("public async Task<LyricsRefreshTrackResult> RefreshStagedFileNowAsync(", service, StringComparison.Ordinal);

        // Neither may reach for a library row: that is the whole point of them existing.
        foreach (var member in new[] { "PlanStagedFileRefresh", "RefreshStagedFileNowAsync" })
        {
            var start = service.IndexOf(member, StringComparison.Ordinal);
            Assert.True(start > 0);
            var body = service[start..];
            body = body[..body.IndexOf("\n    private ", StringComparison.Ordinal)];

            Assert.DoesNotContain("GetTrackAudioInfoAsync", body, StringComparison.Ordinal);
            Assert.DoesNotContain("GetTrackSourceLinksAsync", body, StringComparison.Ordinal);
        }

        // Identity comes from the file's own tags, read with the runner's raw reader so MP4/ATL quirks are
        // handled once rather than twice.
        Assert.Contains("AutoTag.LocalAutoTagRunner.ReadRawTagValues(", service, StringComparison.Ordinal);
        Assert.Contains("AddStagedProviderId(audio, extension, urls, \"deezer_track_id\"", service, StringComparison.Ordinal);

        // Both paths share one implementation, so they cannot drift.
        Assert.Contains("ProcessResolvedFileLyricsAsync(", service, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A sidecar keeps the stem of the audio it belongs to, even when the audio was renamed.
    /// </summary>
    /// <remarks>
    ///     Renaming a sidecar independently produced <c>01 Track (1).flac</c> beside
    ///     <c>01 Track (1) (1).lrc</c> - two files, nothing to pair them. The name is derived from the audio's
    ///     final destination and used as-is.
    /// </remarks>
    [Fact]
    public void ASidecarKeepsTheStemOfTheMovedAudio()
    {
        var source = ReadSource("AutoTagDownloadMoveService.cs");
        var start = source.IndexOf("private static void MoveAdjacentSidecarsWithAudio", StringComparison.Ordinal);
        Assert.True(start > 0);
        var method = source[start..];
        method = method[..method.IndexOf("private static string? ResolveAlreadyMovedPathUnderRoot", StringComparison.Ordinal)];

        // Derived from the audio's destination, and never independently renamed.
        Assert.Contains("Path.ChangeExtension(destinationPath, extension)", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetUniqueDestinationPath", method, StringComparison.Ordinal);
        Assert.DoesNotContain("IOFile.Move(sidecarPath, GetUniqueDestinationPath", source, StringComparison.Ordinal);

        // An occupied destination is left alone rather than overwritten or renamed.
        Assert.Contains("if (IOFile.Exists(sidecarTarget))", method, StringComparison.Ordinal);
    }

    private static string ReadSource(string fileName)
        => File.ReadAllText(Path.Join(ResolveRepoRoot(), "DeezSpoTag.Web", "Services", fileName));

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