using System;
using System.Reflection;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagReleaseCategoryTest
{
    [Theory]
    [InlineData("id", "US1234567890")]
    [InlineData("text", "US1234567890")]
    public void ExternalReleaseCannotUseSharedIsrcOrIdToIgnoreWrongAudioDuration(string strategy, string isrc)
    {
        var runnerType = typeof(LocalAutoTagRunner);
        var planType = runnerType.GetNestedType("AutoTagRunPlan", BindingFlags.NonPublic)!;
        var contextType = runnerType.GetNestedType("AutoTagFileRunContext", BindingFlags.NonPublic)!;
        var plan = Activator.CreateInstance(planType, nonPublic: true)!;
        planType.GetProperty("MatchingConfig")!.SetValue(plan, new AutoTagMatchingConfig
            { PreferAvailableRelease = true, MaxDurationDifferenceSeconds = 5 });
        planType.GetProperty("Config")!.SetValue(plan, new LocalAutoTagRunner.AutoTagRunnerConfig
            { ManualReleasePreference = "single", ManualDestinationFolderId = 7 });
        var context = Activator.CreateInstance(contextType, nonPublic: true)!;
        contextType.GetProperty("Plan")!.SetValue(context, plan);
        contextType.GetProperty("Platform")!.SetValue(context, "itunes");
        contextType.GetProperty("File")!.SetValue(context, "/tmp/recording.flac");
        var source = new AutoTagAudioInfo { Title = "Song", Artist = "Artist", Artists = new() { "Artist" },
            Isrc = isrc, DurationSeconds = 200, HasEmbeddedArtist = true, HasEmbeddedTitle = true };
        var match = new AutoTagMatchResult { MatchStrategy = strategy, Track = new AutoTagTrack
            { Title = "Song", Artists = new() { "Artist" }, Isrc = isrc, Duration = TimeSpan.FromSeconds(260), ReleaseType = "single" } };
        var method = runnerType.GetMethod("IsAcceptableFirstMatch", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.False((bool)method.Invoke(null, new object[] { context, source, match })!);
        match.Track.Duration = TimeSpan.FromSeconds(202);
        Assert.True((bool)method.Invoke(null, new object[] { context, source, match })!);
    }

    [Fact]
    public async System.Threading.Tasks.Task ExternalArtworkUsesPreparedProvidersWithoutAnotherCatalogPass()
    {
        var calls = 0;
        using var http = new System.Net.Http.HttpClient(new CatalogHandler(_ =>
        {
            calls++;
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new System.Net.Http.StringContent("{\"data\":[]}") };
        }));
        var runnerType = typeof(LocalAutoTagRunner);
        var runner = (LocalAutoTagRunner)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(runnerType);
        runnerType.GetField("_logger", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(runner,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalAutoTagRunner>.Instance);
        runnerType.GetField("_deezerMatcher", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(runner,
            new DeezerMatcher(new DeezerClient(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezerClient>.Instance),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezerMatcher>.Instance));
        var planType = runnerType.GetNestedType("AutoTagRunPlan", BindingFlags.NonPublic)!;
        var contextType = runnerType.GetNestedType("AutoTagFileRunContext", BindingFlags.NonPublic)!;
        var plan = Activator.CreateInstance(planType, nonPublic: true)!;
        planType.GetProperty("Config")!.SetValue(plan, new LocalAutoTagRunner.AutoTagRunnerConfig
            { ManualReleasePreference = "album", ManualDestinationFolderId = 7, Tags = new() { "albumArt" } });
        planType.GetProperty("Settings")!.SetValue(plan, new DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings
            { SaveArtwork = true, ArtworkFallbackEnabled = true, ArtworkFallbackOrder = "deezer" });
        planType.GetProperty("MatchingConfig")!.SetValue(plan, new AutoTagMatchingConfig());
        var context = Activator.CreateInstance(contextType, nonPublic: true)!;
        contextType.GetProperty("Plan")!.SetValue(context, plan);
        contextType.GetProperty("Platform")!.SetValue(context, "itunes");
        contextType.GetProperty("File")!.SetValue(context, "/tmp/song.flac");
        contextType.GetProperty("LogCallback")!.SetValue(context, new Action<string>(_ => { }));
        var track = new AutoTagTrack { Title = "Song", Artists = new() { "Artist" }, Album = "Album", ReleaseType = "album" };
        var method = runnerType.GetMethod("EnsureArtworkFallbackAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (System.Threading.Tasks.Task)method.Invoke(runner, new object[] { context,
            new AutoTagAudioInfo { Title = "Song", Artist = "Artist" }, track, default(System.Threading.CancellationToken) })!;
        Assert.Equal(0, calls);
        var prepared = (System.Collections.IDictionary)planType.GetProperty("ExternalMatchResults")!.GetValue(plan)!;
        prepared.Add((0, "deezer"), new AutoTagMatchResult { Track = new AutoTagTrack
            { Title = "Song", Artists = new() { "Artist" }, Album = "Album", ReleaseType = "album", Art = "https://example.test/verified.jpg" } });
        await (System.Threading.Tasks.Task)method.Invoke(runner, new object[] { context,
            new AutoTagAudioInfo { Title = "Song", Artist = "Artist" }, track, default(System.Threading.CancellationToken) })!;
        Assert.Equal(0, calls);
        Assert.Equal("https://example.test/verified.jpg", track.Art);
    }

    [Theory]
    [InlineData("Other Song", "Artist")]
    [InlineData("Song", "Other Performer")]
    [InlineData("Song (Live)", "Artist")]
    public void SuccessfulFingerprintGuardsNativeIdMatchesEvenWithoutEmbeddedTags(string title, string artist)
    {
        var type = typeof(LocalAutoTagRunner);
        var planType = type.GetNestedType("AutoTagRunPlan", BindingFlags.NonPublic)!;
        var contextType = type.GetNestedType("AutoTagFileRunContext", BindingFlags.NonPublic)!;
        var plan = Activator.CreateInstance(planType, nonPublic: true)!;
        planType.GetProperty("MatchingConfig")!.SetValue(plan, new AutoTagMatchingConfig { PreferAvailableRelease = true });
        planType.GetProperty("Config")!.SetValue(plan, new LocalAutoTagRunner.AutoTagRunnerConfig { ManualDestinationFolderId = 7, ManualReleasePreference = "album" });
        ((System.Collections.Generic.HashSet<int>)planType.GetProperty("ShazamIdentifiedFiles")!.GetValue(plan)!).Add(0);
        var context = Activator.CreateInstance(contextType, nonPublic: true)!;
        contextType.GetProperty("Plan")!.SetValue(context, plan);
        contextType.GetProperty("Platform")!.SetValue(context, "spotify");
        contextType.GetProperty("File")!.SetValue(context, "/tmp/unknown.flac");
        var fingerprint = new AutoTagAudioInfo { Title = "Song", Artist = "Artist", Artists = new() { "Artist" } };
        var match = new AutoTagMatchResult { MatchStrategy = "id", Track = new AutoTagTrack { Title = title, Artists = new() { artist } } };
        Assert.False((bool)type.GetMethod("IsAcceptableFirstMatch", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { context, fingerprint, match })!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task MusicBrainzCatalogErrorsCannotEstablishReleaseAbsence(bool releaseFailure)
    {
        using var http = new System.Net.Http.HttpClient(new CatalogHandler(request =>
        {
            if (releaseFailure && request.RequestUri!.AbsolutePath.EndsWith("/release", StringComparison.Ordinal))
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.BadRequest);
            var json = releaseFailure
                ? "{\"recordings\":[{\"id\":\"00000000-0000-0000-0000-000000000001\",\"title\":\"Song\",\"length\":200000,\"artist-credit\":[{\"name\":\"Artist\",\"artist\":{\"id\":\"00000000-0000-0000-0000-000000000002\",\"name\":\"Artist\"}}]}]}"
                : "invalid json";
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new System.Net.Http.StringContent(json) };
        }));
        var client = new MusicBrainzClient(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicBrainzClient>.Instance);
        var matcher = new MusicBrainzMatcher(client, Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicBrainzMatcher>.Instance);
        var action = () => matcher.MatchAsync(new AutoTagAudioInfo { Title = "Song", Artist = "Artist", Artists = new() { "Artist" }, DurationSeconds = 200 },
            new AutoTagMatchingConfig { PreferredReleaseType = "album", PreferAvailableRelease = true },
            new MusicBrainzMatchConfig(), default);
        if (releaseFailure) await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(action);
        else await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(action);
    }

    [Theory]
    [InlineData("itunes", "12345")]
    [InlineData("deezer", null)]
    public async System.Threading.Tasks.Task AppleExtrasReuseOnlyPreparedNativeAppleIdentity(string provider, string? expectedId)
    {
        var type = typeof(LocalAutoTagRunner);
        var runner = (LocalAutoTagRunner)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("_logger", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(runner,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalAutoTagRunner>.Instance);
        var payload = new ProviderIdentityPayload(provider, "12345", "23456", "23456", "34567", "34567", "https://example.test/song", true);
        var method = type.GetMethod("ResolveAppleIdentityForExtrasAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = await (System.Threading.Tasks.Task<DeezSpoTag.Services.Download.Identity.TrackIdentityResolution?>)
            method.Invoke(runner, new object[] { new AutoTagTrack { Title = "Song", Artists = new() { "Artist" }, Album = "Selected Album" },
                "us", new DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings(), default(System.Threading.CancellationToken), payload, true })!;
        Assert.Equal(expectedId, result?.AppleId);
        if (expectedId is not null)
        {
            Assert.Equal("23456", result!.AppleAlbumId);
            Assert.Equal("Selected Album", result.AppleAlbumName);
        }
    }

    [Theory]
    [InlineData(true, "single", "Selected Single")]
    [InlineData(false, "album", "Existing Album")]
    public async System.Threading.Tasks.Task MusicBrainzHonorsAvailableReleaseChoiceInsteadOfAnOldAlbumId(bool hasSingle, string expectedType, string expectedAlbum)
    {
        const string recording = "00000000-0000-0000-0000-000000000001";
        const string albumId = "00000000-0000-0000-0000-000000000002";
        const string singleId = "00000000-0000-0000-0000-000000000003";
        object Release(string id, string title, string primary) => new
        {
            id, title, status = "Official", country = "US", date = "2000-01-01",
            release_group = new { primary_type = primary },
            media = new[] { new { position = 1, track_count = primary == "Single" ? 1 : 12,
                tracks = new[] { new { title = "Song", number = "1", position = 1, length = 200000, recording = new { id = recording } } } } }
        };
        using var http = new System.Net.Http.HttpClient(new CatalogHandler(request =>
        {
            object record = new { id = recording, title = "Song", length = 200000,
                artist_credit = new[] { new { name = "Ada", artist = new { id = albumId, name = "Ada" } } } };
            object payload = request.RequestUri!.AbsolutePath.EndsWith("/release", StringComparison.Ordinal)
                ? new { releases = hasSingle ? new[] { Release(albumId, "Existing Album", "Album"), Release(singleId, "Selected Single", "Single") }
                    : new[] { Release(albumId, "Existing Album", "Album") } }
                : request.RequestUri.AbsolutePath.Contains("/recording/", StringComparison.Ordinal) ? record : new { recordings = new[] { record } };
            var json = System.Text.Json.JsonSerializer.Serialize(payload)
                .Replace("release_group", "release-group", StringComparison.Ordinal)
                .Replace("primary_type", "primary-type", StringComparison.Ordinal)
                .Replace("track_count", "track-count", StringComparison.Ordinal)
                .Replace("artist_credit", "artist-credit", StringComparison.Ordinal);
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(json) };
        }));
        var matcher = new MusicBrainzMatcher(new MusicBrainzClient(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicBrainzClient>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicBrainzMatcher>.Instance);
        var source = new AutoTagAudioInfo { Title = "Song", Artist = "Ada", Artists = new() { "Ada" }, Album = "Existing Album", DurationSeconds = 200 };
        source.Tags["MUSICBRAINZ_ALBUMID"] = new() { albumId };
        var matched = await matcher.MatchAsync(source, new AutoTagMatchingConfig { PreferredReleaseType = "single", PreferAvailableRelease = true }, new MusicBrainzMatchConfig(), default);
        Assert.NotNull(matched);
        Assert.Equal(expectedType, matched!.Track.ReleaseType);
        Assert.Equal(expectedAlbum, matched.Track.Album);
    }

    [Fact]
    public async System.Threading.Tasks.Task MusicBrainzBrowseUsesSupportedIncludesAndKeepsReturnedCountryEvents()
    {
        using var http = new System.Net.Http.HttpClient(new CatalogHandler(request =>
        {
            if (request.RequestUri!.Query.Contains("release-events", StringComparison.Ordinal))
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.BadRequest);
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(
                "{\"release-count\":1,\"releases\":[{\"id\":\"verified-release\",\"title\":\"Album\",\"release-events\":[{\"area\":{\"iso-3166-1-codes\":[\"US\"]}}]}]}") };
        }));
        var client = new MusicBrainzClient(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicBrainzClient>.Instance);
        var result = await client.GetReleasesAsync("verified-recording", default);
        var release = Assert.Single(result!.Releases);
        Assert.Equal("US", Assert.Single(Assert.Single(release.ReleaseEvents!).Area!.Iso3166_1Codes!));
    }

    [Fact]
    public async System.Threading.Tasks.Task EnrichmentCannotTreatAppleOutageAsNoRelease()
    {
        using var http = new System.Net.Http.HttpClient(new CatalogHandler(_ =>
            new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)));
        var client = new ItunesClient(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<ItunesClient>.Instance);
        client.SetRateLimit(1000000);
        var matcher = new ItunesMatcher(client);
        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => matcher.MatchAsync(
            new AutoTagAudioInfo { Title = "Song", Artist = "Artist" },
            new AutoTagMatchingConfig { PreferredReleaseType = "single", PreferAvailableRelease = true },
            new ItunesMatchConfig(), default));
    }

    private sealed class CatalogHandler(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> response)
        : System.Net.Http.HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            => System.Threading.Tasks.Task.FromResult(response(request));
    }

    [Fact]
    public async System.Threading.Tasks.Task EnrichmentCannotTreatDeezerAuthenticationFailureAsNoRelease()
    {
        using var http = new System.Net.Http.HttpClient(new CatalogHandler(_ =>
            new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)));
        var client = new DeezerClient(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezerClient>.Instance);
        var matcher = new DeezerMatcher(client, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezerMatcher>.Instance);
        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => matcher.MatchAsync(
            new AutoTagAudioInfo { Title = "Song", Artist = "Artist" },
            new AutoTagMatchingConfig { PreferredReleaseType = "single", PreferAvailableRelease = true },
            new DeezerConfig(), default));
        Assert.False(client.RequiredResults);
    }

    [Fact]
    public async System.Threading.Tasks.Task LegacyAppleLookupStillReturnsNoMatchOnHttpFailure()
    {
        using var http = new System.Net.Http.HttpClient(new CatalogHandler(_ =>
            new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)));
        var client = new ItunesClient(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<ItunesClient>.Instance);
        Assert.Null(await client.SearchAsync("Song Artist", "us", 20, default));
    }

    [Fact]
    public void FrozenReleaseRetainsItsPositionDateAndCoverWithoutCopyingForeignProviderIds()
    {
        var type = typeof(LocalAutoTagRunner).GetNestedType("ManualReleaseIdentity", BindingFlags.NonPublic)!;
        var selected = new AutoTagTrack { Title = "Song", Artists = new() { "Artist" }, Album = "Selected Single",
            ReleaseType = "single", TrackNumber = 1, TrackTotal = 1, DiscNumber = 1, DiscTotal = 1,
            ReleaseDate = new DateTime(2026, 1, 2), Art = "https://example.test/single.jpg", ReleaseId = "apple-release" };
        var frozen = type.GetMethod("FromTrack")!.Invoke(null, new object[] { selected });
        var later = new AutoTagTrack { Title = "Song", Artists = new() { "Artist" }, Album = "Selected Single",
            TrackNumber = 8, TrackTotal = 12, DiscNumber = 2, ReleaseDate = new DateTime(2000, 1, 1),
            Art = "https://example.test/album.jpg", ReleaseId = "spotify-release" };
        type.GetMethod("ApplyTo")!.Invoke(frozen, new object[] { later });
        Assert.Equal(1, later.TrackNumber);
        Assert.Equal(1, later.TrackTotal);
        Assert.Equal(1, later.DiscNumber);
        Assert.Equal(selected.ReleaseDate, later.ReleaseDate);
        Assert.Equal(selected.Art, later.Art);
        Assert.Equal("spotify-release", later.ReleaseId);
    }

    [Theory]
    [InlineData("album", "single")]
    [InlineData("single", "album")]
    public void AvailableReleasePreferenceRetainsTheOnlyVerifiedRelease(string actual, string preferred)
    {
        var track = new AutoTagTrack { Title = "Song", Artists = new() { "Artist" }, ReleaseType = actual, TrackTotal = actual == "album" ? 10 : 1 };
        var selected = AutoTagMatchSelection.MatchPreferredTrack(
            new AutoTagAudioInfo { Title = "Song", Artist = "Artist", Artists = new() { "Artist" } },
            new System.Collections.Generic.List<AutoTagTrack> { track },
            new AutoTagMatchingConfig { PreferredReleaseType = preferred, PreferAvailableRelease = true },
            new OneTaggerMatching.TrackSelectors<AutoTagTrack>(x => x.Title, _ => null, x => x.Artists, x => x.Duration, x => x.ReleaseDate),
            x => x.ReleaseType, x => x.TrackTotal);
        Assert.NotNull(selected);
        Assert.Same(track, selected!.Track);
        Assert.Equal(actual, track.ReleaseType);
    }

    [Theory]
    [InlineData("album")]
    [InlineData("single")]
    public void AvailableReleasePreferenceChoosesOnlyMatchingRecording(string preferred)
    {
        var album = new AutoTagTrack { Title = "Song", Artists = new() { "Artist" }, ReleaseType = "album", TrackTotal = 10 };
        var single = new AutoTagTrack { Title = "Song", Artists = new() { "Artist" }, ReleaseType = "single", TrackTotal = 1 };
        var candidates = new System.Collections.Generic.List<AutoTagTrack> { album, single };
        var selected = AutoTagMatchSelection.MatchPreferredTrack(
            new AutoTagAudioInfo { Title = "Song", Artist = "Artist", Artists = new() { "Artist" } }, candidates,
            new AutoTagMatchingConfig { PreferredReleaseType = preferred, PreferAvailableRelease = true },
            new OneTaggerMatching.TrackSelectors<AutoTagTrack>(x => x.Title, _ => null, x => x.Artists, x => x.Duration, x => x.ReleaseDate),
            x => x.ReleaseType, x => x.TrackTotal);
        Assert.Equal(preferred, selected!.Track.ReleaseType);
        (preferred == "album" ? album : single).Title = "Completely Different Recording";
        selected = AutoTagMatchSelection.MatchPreferredTrack(
            new AutoTagAudioInfo { Title = "Song", Artist = "Artist", Artists = new() { "Artist" } }, candidates,
            new AutoTagMatchingConfig { PreferredReleaseType = preferred, PreferAvailableRelease = true, Strictness = .9 },
            new OneTaggerMatching.TrackSelectors<AutoTagTrack>(x => x.Title, _ => null, x => x.Artists, x => x.Duration, x => x.ReleaseDate),
            x => x.ReleaseType, x => x.TrackTotal);
        Assert.NotNull(selected);
        Assert.Equal("Song", selected!.Track.Title);
    }

    [Theory]
    [InlineData("single", "single")]
    [InlineData("Single", "single")]
    [InlineData("album", "album")]
    [InlineData("LP", "album")]
    [InlineData("EP", "ep")]
    [InlineData("Extended Play", "ep")]
    [InlineData("Compilation", "compilation")]
    [InlineData("Album; Compilation", "compilation")]
    public void Resolve_UsesExplicitReleaseTypeWhenAvailable(string input, string expected)
    {
        Assert.Equal(expected, AutoTagReleaseCategory.Resolve(input, 12));
    }

    [Theory]
    [InlineData(1, "single")]
    [InlineData(2, "album")]
    [InlineData(14, "album")]
    public void Resolve_UsesTrackTotalWhenExplicitReleaseTypeIsMissing(int trackTotal, string expected)
    {
        Assert.Equal(expected, AutoTagReleaseCategory.Resolve(null, trackTotal));
    }

    [Fact]
    public void Resolve_ReturnsNullWhenReleaseShapeIsUnknown()
    {
        Assert.Null(AutoTagReleaseCategory.Resolve(null, null));
    }

    [Theory]
    [InlineData("Album", "Compilation", "compilation")]
    [InlineData("Album", "EP", "ep")]
    [InlineData(null, "Single", "single")]
    public void Resolve_UsesProviderClassificationSignalsBeforeTrackCount(
        string? primaryType,
        string additionalType,
        string expected)
    {
        Assert.Equal(
            expected,
            AutoTagReleaseCategory.Resolve(primaryType, [additionalType], 12));
    }

    [Theory]
    [InlineData("single", 1, "single", true)]
    [InlineData("album", 12, "album", true)]
    [InlineData("EP", 5, "ep", true)]
    [InlineData("EP", 5, "album", false)]
    [InlineData("compilation", 30, "compilation", true)]
    [InlineData("compilation", 30, "album", false)]
    [InlineData("album", 12, "single", false)]
    public void MatchesPreference_EnforcesExplicitManualReleaseChoice(
        string releaseType,
        int trackTotal,
        string preference,
        bool expected)
    {
        Assert.Equal(
            expected,
            AutoTagReleaseCategory.MatchesPreference(releaseType, trackTotal, preference));
    }

    [Fact]
    public void MusicBrainzMapper_UsesCompilationSecondaryType()
    {
        var track = new MusicBrainzTrack { TrackTotal = 12 };
        var release = new Release
        {
            Id = "release-id",
            ReleaseGroup = new ReleaseGroup
            {
                Id = "release-group-id",
                PrimaryType = "Album",
                SecondaryTypes = ["Compilation"]
            }
        };
        var applyReleaseMetadata = typeof(MusicBrainzMatcher).GetMethod(
            "ApplyReleaseMetadata",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MusicBrainz release mapper not found.");

        var preferences = Activator.CreateInstance(
            applyReleaseMetadata.GetParameters()[2].ParameterType, nonPublic: true);
        applyReleaseMetadata.Invoke(null, [track, release, preferences]);

        Assert.Equal("compilation", track.ReleaseType);
        var releaseTypeTag = Assert.Single(track.Other, value => value.Key == "RELEASETYPE");
        Assert.Equal("compilation", Assert.Single(releaseTypeTag.Values));
    }

    [Theory]
    [InlineData("EP", "ep")]
    [InlineData("Compilation", "compilation")]
    public void DiscogsMapper_UsesFormatDescription(string description, string expected)
    {
        var release = new DiscogsRelease
        {
            Id = 123,
            Title = "Release",
            Tracks = [new DiscogsTrack { Position = "1", Title = "Track" }],
            Formats =
            [
                new DiscogsReleaseFormat
                {
                    Name = "Vinyl",
                    Descriptions = [description]
                }
            ]
        };
        var toTrack = typeof(DiscogsMatcher).GetMethod(
            "ToTrack",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Discogs release mapper not found.");
        var toAutoTagTrack = typeof(DiscogsMatcher).GetMethod(
            "ToAutoTagTrack",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Discogs AutoTag mapper not found.");

        var providerTrack = Assert.IsType<DiscogsTrackInfo>(
            toTrack.Invoke(null, [release, 0, new DiscogsConfig()]));
        var autoTagTrack = Assert.IsType<AutoTagTrack>(
            toAutoTagTrack.Invoke(null, [providerTrack]));

        Assert.Equal(expected, autoTagTrack.ReleaseType);
    }
}
