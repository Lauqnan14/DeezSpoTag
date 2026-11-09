using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagSharedSongDetailsTest
{
    private static MethodInfo Method(string name) => typeof(LocalAutoTagRunner).GetMethod(name,
        BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Missing shared integration: " + name);

    [Fact]
    public void SharedDetailsOnlyFillSelectedMissingValuesAndKeepNativeMetadata()
    {
        var track = new AutoTagTrack { Title = "Song", Artists = ["Artist"], Album = "Preferred Album", Energy = .8 };
        track.Other["producer"] = ["Preferred Producer"];
        var details = new AutoTagTrack { Title = "Song", Artists = ["Artist"], Album = "Other Album", Energy = .2,
            Melodicness = .9, Acousticness = .3, Bpm = 104 };
        details.Other["producer"] = ["Shazam Producer"];
        details.Other["songwriter"] = ["Shazam Writer"];
        var added = (IEnumerable<string>)Method("MergeSharedSongDetails").Invoke(null,
            [track, details, new HashSet<string>(["energy", "melodicness", "producer", "songwriter"], StringComparer.OrdinalIgnoreCase)])!;
        Assert.Equal(.8, track.Energy);
        Assert.Equal(.9, track.Melodicness);
        Assert.Null(track.Acousticness);
        Assert.Null(track.Bpm);
        Assert.Equal("Preferred Album", track.Album);
        Assert.Equal(["Preferred Producer"], track.Other["producer"]);
        Assert.Equal(["Shazam Writer"], track.Other["songwriter"]);
        Assert.Equal(["melodicness", "songwriter"], added.OrderBy(x => x));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("artist")]
    [InlineData("isrc")]
    [InlineData("duration")]
    public void SharedDetailsRejectConflictingProviderRecording(string conflict)
    {
        var track = new AutoTagTrack { Title = "Song", Artists = ["Artist"], Isrc = "GBBKS1000335", Duration = TimeSpan.FromSeconds(228) };
        var details = new AutoTagTrack { Title = conflict == "title" ? "Song (Live)" : "Song",
            Artists = [conflict == "artist" ? "Another Artist" : "Artist"],
            Isrc = conflict == "isrc" ? "USUG11904206" : "GBBKS1000335",
            Duration = TimeSpan.FromSeconds(conflict == "duration" ? 260 : 228), Energy = .7 };
        var added = (IEnumerable<string>)Method("MergeSharedSongDetails").Invoke(null,
            [track, details, new HashSet<string>(["energy"])])!;
        Assert.Empty(added);
        Assert.Null(track.Energy);
    }

    [Fact]
    public void NativeProviderCanReplaceOnlyProvisionalSharedFieldsWithoutMutatingSavedSettings()
    {
        var config = new LocalAutoTagRunner.AutoTagRunnerConfig { Tags = ["energy", "producer", "songwriter"], OverwriteTags = ["songwriter"] };
        var native = new AutoTagTrack { Energy = .8 };
        native.Other["producer"] = ["Native Producer"];
        var effective = (LocalAutoTagRunner.AutoTagRunnerConfig)Method("BuildSharedDetailsWriteConfig")
            .Invoke(null, [config, new HashSet<string>(["energy"]), native])!;
        Assert.Contains("energy", effective.OverwriteTags);
        Assert.DoesNotContain("producer", effective.OverwriteTags);
        Assert.Equal(["songwriter"], config.OverwriteTags);
        Assert.False(config.Overwrite);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("manual_enrichment")]
    [InlineData("soulseek_enrichment")]
    [InlineData("download_enrichment")]
    public async Task EveryRunReusesRecognitionAndOneDetailsLookupAcrossPathChanges(string intent)
    {
        var planType = typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunPlan", BindingFlags.NonPublic)!;
        var contextType = typeof(LocalAutoTagRunner).GetNestedType("AutoTagFileRunContext", BindingFlags.NonPublic)!;
        var plan = Activator.CreateInstance(planType, true)!;
        var context = Activator.CreateInstance(contextType, true)!;
        void Set(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
        var config = new LocalAutoTagRunner.AutoTagRunnerConfig { Platforms = ["itunes"], Tags = ["melodicness"],
            Custom = new System.Text.Json.Nodes.JsonObject { ["run_intent"] = intent } };
        Set(plan, "Config", config);
        var recognized = new ShazamRecognitionInfo { Title = "Song", Artist = "Artist", TrackId = "42" };
        var detailsProperty = typeof(ShazamRecognitionInfo).GetProperty("SongDetails");
        Assert.NotNull(detailsProperty);
        detailsProperty.SetValue(recognized, new AutoTagTrack { Title = "Song", Artists = ["Artist"], Melodicness = .9 });
        Set(plan, "ShazamCache", new Dictionary<string, ShazamRecognitionInfo?> { ["before.flac"] = recognized });
        Set(context, "Plan", plan); Set(context, "File", "before.flac"); Set(context, "FileIndex", 3);
        Set(context, "Token", CancellationToken.None); Set(context, "LogCallback", (Action<string>)(_ => {}));
        var collaborators = (LocalAutoTagRunner.LocalAutoTagRunnerCollaborators)Activator.CreateInstance(typeof(LocalAutoTagRunner.LocalAutoTagRunnerCollaborators))!;
        collaborators.GetType().GetProperty("Logger")!.SetValue(collaborators, NullLogger<LocalAutoTagRunner>.Instance);
        var runner = new LocalAutoTagRunner(collaborators);
        var first = (Task<AutoTagTrack?>)Method("GetSharedSongDetailsAsync").Invoke(runner, [context])!;
        Assert.Equal(.9, (await first)!.Melodicness);
        Set(context, "File", "materialized/after.flac");
        var second = (Task<AutoTagTrack?>)Method("GetSharedSongDetailsAsync").Invoke(runner, [context])!;
        Assert.Same(await first, await second);
        var state = (IEnumerable)planType.GetProperty("SharedSongDetails")!.GetValue(plan)!;
        Assert.Single(state.Cast<object>());
    }


    [Fact]
    public void ShazamAdvertisesOnlyTheNewCapabilitiesThatVerifiedDetailsCanSupply()
    {
        var environment = new TestEnvironment();
        var platform = new ShazamPlatform(environment);
        var tags = platform.Describe().SupportedTags;
        foreach (var tag in new[] { SupportedTag.BPM, SupportedTag.Melodicness, SupportedTag.Acousticness,
            SupportedTag.Energy, SupportedTag.Valence, SupportedTag.Danceability, SupportedTag.Songwriter, SupportedTag.MixingEngineer })
            Assert.Contains(tag, tags);
        Assert.DoesNotContain(SupportedTag.Instrumentalness, tags);
    }
    [Fact]
    public void ProviderWithoutCreditsStillHasSharedEligibility()
    {
        var type = typeof(LocalAutoTagRunner);
        var plan = Activator.CreateInstance(type.GetNestedType("AutoTagRunPlan", BindingFlags.NonPublic)!, true)!;
        var context = Activator.CreateInstance(type.GetNestedType("AutoTagFileRunContext", BindingFlags.NonPublic)!, true)!;
        void Set(object target, string key, object value) => target.GetType().GetProperty(key)!.SetValue(target, value);
        Set(plan, "Config", new LocalAutoTagRunner.AutoTagRunnerConfig { Tags = ["producer", "melodicness"] });
        Set(plan, "PlatformSupportedTags", new Dictionary<string, HashSet<SupportedTag>> { ["itunes"] = [SupportedTag.Title] });
        Set(context, "Plan", plan); Set(context, "Platform", "itunes"); Set(context, "File", "missing.flac");
        var tagPlan = Method("BuildProviderTagPlan").Invoke(null, [context])!;
        var eligible = (HashSet<SupportedTag>)tagPlan.GetType().GetProperty("Eligible")!.GetValue(tagPlan)!;
        Assert.Contains(SupportedTag.Producer, eligible);
        Assert.Contains(SupportedTag.Melodicness, eligible);
    }
    [Fact]
    public void MaterializationTransfersRecognitionWithoutLosingStableDetailState()
    {
        var planType = typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunPlan", BindingFlags.NonPublic)!;
        var plan = Activator.CreateInstance(planType, true)!;
        var recognized = new ShazamRecognitionInfo { Title = "Song", Artist = "Artist" };
        var cache = new Dictionary<string, ShazamRecognitionInfo?> { ["before.flac"] = recognized };
        planType.GetProperty("ShazamCache")!.SetValue(plan, cache);
        Method("TransferShazamRecognitionCache").Invoke(null, [plan, "before.flac", "album/after.flac"]);
        Assert.False(cache.ContainsKey("before.flac"));
        Assert.Same(recognized, cache["album/after.flac"]);
    }

    [Fact]
    public void SharedSelectionsSurviveStagePlanningWithoutShazamSelected()
    {
        var service = typeof(AutoTagService);
        var caps = service.GetNestedType("PlatformTagCapabilities", BindingFlags.NonPublic)!;
        var dictionary = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), caps))!;
        var result = (List<string>)service.GetMethod("FilterSupportedTags", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [new[] { "producer", "melodicness", "energy", "liveness" }, new[] { "itunes" }, dictionary])!;
        Assert.Equal(["producer", "melodicness", "energy"], result);
    }


    [Theory]
    [InlineData("Live", null, false)]
    [InlineData("Radio Edit", null, false)]
    [InlineData(null, "Live", false)]
    [InlineData("Live", "Live", true)]
    public void SharedDetailsRespectSeparateVersionMetadata(string? nativeVersion, string? detailsVersion, bool compatible)
    {
        var native = new AutoTagTrack { Title = "Song", Artists = ["Artist"], Version = nativeVersion };
        var details = new AutoTagTrack { Title = "Song", Artists = ["Artist"], Version = detailsVersion, Energy = .7 };
        var added = (HashSet<string>)Method("MergeSharedSongDetails").Invoke(null, [native, details, new HashSet<string>(["energy"])])!;
        Assert.Equal(compatible, added.Contains("energy"));
        Assert.Equal(compatible ? .7 : (double?)null, native.Energy);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, true, false)]
    public void SharedOverwriteHonorsUserChoiceAndProtectsNativeFields(bool fieldOverwrite, bool globalOverwrite, bool nativeOwned, bool eligible)
    {
        object?[] create = ["flac", null];
        var file = (string)typeof(AutoTagCreditRoleFileRoundTripTest).GetMethod("NewTrack", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, create)!;
        try
        {
            using (var audio = TagLib.File.Create(file))
            { ((TagLib.Ogg.XiphComment)audio.GetTag(TagLib.TagTypes.Xiph, true)).SetField("ENERGY", ".2"); audio.Save(); }
            var plan = Activator.CreateInstance(typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunPlan", BindingFlags.NonPublic)!, true)!;
            var context = Activator.CreateInstance(typeof(LocalAutoTagRunner).GetNestedType("AutoTagFileRunContext", BindingFlags.NonPublic)!, true)!;
            void Set(object target, string key, object value) => target.GetType().GetProperty(key)!.SetValue(target, value);
            Set(plan, "Config", new LocalAutoTagRunner.AutoTagRunnerConfig { Tags = ["energy"],
                OverwriteTags = fieldOverwrite ? ["energy"] : [], Overwrite = globalOverwrite });
            Set(context, "Plan", plan); Set(context, "Platform", "itunes"); Set(context, "File", file); Set(context, "FileIndex", 1);
            var property = plan.GetType().GetProperty("NativeSongDetailTags");
            Assert.NotNull(property);
            var owners = (Dictionary<int, HashSet<string>>)property.GetValue(plan)!;
            if (nativeOwned) owners[1] = ["energy"];
            var missing = (HashSet<string>)Method("MissingSharedDetailKeys").Invoke(null, [context])!;
            Assert.Equal(eligible, missing.Contains("energy"));
        }
        finally { Directory.Delete((string)create[1]!, true); }
    }

    [Fact]
    public async Task ActualProviderPassSharesVerifiedDetailsAndReportsPersistedFields()
    {
        object?[] create = ["flac", null];
        var file = (string)typeof(AutoTagCreditRoleFileRoundTripTest).GetMethod("NewTrack", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, create)!;
        var directory = (string)create[1]!;
        try
        {
            var card = ShazamSongDetailsTest.SourceCard();
            using (var audio = TagLib.File.Create(file))
            { audio.Tag.Title = card.Title; audio.Tag.Performers = [card.Artist]; audio.Tag.Album = card.Album; audio.Save(); }
            using var handler = new ReplayDetailsHandler();
            using var http = new System.Net.Http.HttpClient(handler);
            var environment = new TestEnvironment { AppDataRoot = Path.Join(directory, "data") };
            var discovery = new ShazamDiscoveryService(http, NullLogger<ShazamDiscoveryService>.Instance, environment);
            using var recognition = new ShazamRecognitionService(environment, discovery, NullLogger<ShazamRecognitionService>.Instance);
            var collaborators = (LocalAutoTagRunner.LocalAutoTagRunnerCollaborators)Activator.CreateInstance(typeof(LocalAutoTagRunner.LocalAutoTagRunnerCollaborators))!;
            collaborators.GetType().GetProperty("Logger")!.SetValue(collaborators, NullLogger<LocalAutoTagRunner>.Instance);
            collaborators.GetType().GetProperty("ShazamRecognitionService")!.SetValue(collaborators, recognition);
            var runner = new LocalAutoTagRunner(collaborators);
            var type = typeof(LocalAutoTagRunner);
            var plan = Activator.CreateInstance(type.GetNestedType("AutoTagRunPlan", BindingFlags.NonPublic)!, true)!;
            var context = Activator.CreateInstance(type.GetNestedType("AutoTagFileRunContext", BindingFlags.NonPublic)!, true)!;
            void Set(object target, string key, object value) => target.GetType().GetProperty(key)!.SetValue(target, value);
            var config = new LocalAutoTagRunner.AutoTagRunnerConfig { Platforms = ["itunes"], Tags = ["energy", "melodicness"],
                SaveArtwork = false, SaveArtworkArtist = false, MaterializeToTemplatePath = false };
            var recognized = (ShazamRecognitionInfo)typeof(ShazamRecognitionService).GetMethod("BuildInfo", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [card])!;
            Set(plan, "Config", config); Set(plan, "JobId", "shared-detail-test"); Set(plan, "ConfigPath", Path.Join(directory, "config.json"));
            Set(plan, "TargetPath", directory); Set(plan, "MatchingConfig", new AutoTagMatchingConfig());
            Set(plan, "Settings", new DeezSpoTagSettings()); Set(plan, "TagSettings", new TagSettings { Energy = true, Melodicness = true, Cover = false });
            Set(plan, "Files", new List<string> { file }); Set(plan, "EffectivePlatforms", new List<string> { "itunes" });
            Set(plan, "PlatformSupportedTags", new Dictionary<string, HashSet<SupportedTag>> { ["itunes"] = [SupportedTag.Title] });
            Set(plan, "ShazamCache", new Dictionary<string, ShazamRecognitionInfo?> { [file] = recognized });
            ((HashSet<int>)plan.GetType().GetProperty("PreparedExternalFiles")!.GetValue(plan)!).Add(0);
            var matches = (Dictionary<(int, string), AutoTagMatchResult?>)plan.GetType().GetProperty("ExternalMatchResults")!.GetValue(plan)!;
            matches[(0, "itunes")] = new AutoTagMatchResult { Accuracy = 1, Track = new AutoTagTrack {
                Title = card.Title, Artists = [card.Artist], Album = card.Album, TrackId = "1544491233", Energy = .8 } };
            var statuses = new List<TaggingStatusWrap>();
            Set(context, "Plan", plan); Set(context, "File", file); Set(context, "FileIndex", 0); Set(context, "Platform", "itunes");
            Set(context, "PlatformIndex", 0); Set(context, "Token", CancellationToken.None);
            Set(context, "StatusCallback", (Action<TaggingStatusWrap>)statuses.Add); Set(context, "LogCallback", (Action<string>)(_ => {}));
            await (Task)Method("ProcessPlatformFileAsync").Invoke(runner, [context])!;
            Assert.Contains(statuses, status => status.Status.Status == "tagged" && status.Status.WrittenTags.Contains("melodicness"));
            Assert.DoesNotContain(statuses, status => status.Status.Status is "review" or "error");
            using var saved = TagLib.File.Create(file);
            var xiph = (TagLib.Ogg.XiphComment)saved.GetTag(TagLib.TagTypes.Xiph, false);
            Assert.Equal("0.8", Assert.Single(xiph.GetField("ENERGY")));
            Assert.Equal("0.98", Assert.Single(xiph.GetField("MELODICNESS")));
            await (Task)Method("ProcessPlatformFileAsync").Invoke(runner, [context])!;
            Assert.Equal(1, handler.Calls);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class ReplayDetailsHandler : System.Net.Http.HttpMessageHandler
    {
        public int Calls;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new System.Net.Http.StringContent(ShazamSongDetailsTest.SongPage(nestedCreditBody: true)) });
        }
    }

    private sealed class TestEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment, IAppDataRootOverride
    {
        public string AppDataRoot { get; set; } = Path.GetTempPath();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = "Test";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

}
