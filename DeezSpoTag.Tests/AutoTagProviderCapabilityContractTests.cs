using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagProviderCapabilityContractTests
{
    private static readonly SupportedTag[] UnsupportedSpotifyAudioTags =
    [
        SupportedTag.BPM,
        SupportedTag.Key,
        SupportedTag.Danceability,
        SupportedTag.Energy,
        SupportedTag.Valence,
        SupportedTag.Acousticness,
        SupportedTag.Instrumentalness,
        SupportedTag.Speechiness,
        SupportedTag.Loudness,
        SupportedTag.Tempo,
        SupportedTag.TimeSignature,
        SupportedTag.Liveness
    ];

    [Fact]
    public void Spotify_DoesNotAdvertiseUnavailableAudioFeatures()
    {
        var descriptor = new SpotifyPlatform(new StubWebHostEnvironment()).Describe();

        foreach (var tag in UnsupportedSpotifyAudioTags)
        {
            Assert.DoesNotContain(tag, descriptor.SupportedTags);
        }

        foreach (var tag in new[]
                 {
                     "bpm", "key", "danceability", "energy", "valence", "acousticness",
                     "instrumentalness", "speechiness", "loudness", "tempo", "timeSignature",
                     "liveness"
                 })
        {
            Assert.DoesNotContain(tag, descriptor.DownloadTags, StringComparer.OrdinalIgnoreCase);
        }

        Assert.Contains(SupportedTag.Source, descriptor.SupportedTags);
        Assert.Contains("source", descriptor.DownloadTags, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SpotifyAutoTag_DoesNotRequestOrPropagateUnavailableAudioFeatures()
    {
        var clientSource = File.ReadAllText(Path.Combine(
            ResolveRepoRoot(),
            "DeezSpoTag.Web",
            "Services",
            "AutoTag",
            "SpotifyClient.cs"));
        var metadataSource = File.ReadAllText(Path.Combine(
            ResolveRepoRoot(),
            "DeezSpoTag.Web",
            "Services",
            "SpotifyMetadataService.cs"));
        var pathfinderSource = File.ReadAllText(Path.Combine(
            ResolveRepoRoot(),
            "DeezSpoTag.Web",
            "Services",
            "SpotifyPathfinderMetadataClient.cs"));
        Assert.DoesNotContain("HydrateTrackAudioFeaturesAsync", clientSource, StringComparison.Ordinal);
        Assert.DoesNotContain("HydrateTrackAudioFeaturesAsync", metadataSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FetchTrackAudioFeaturesByIdsAsync", pathfinderSource, StringComparison.Ordinal);

        var input = new SpotifyTrackInfo
        {
            TrackId = "0VjIjW4GlUZAMYd2vXMi3b",
            Danceability = 0.75,
            Energy = 0.8,
            Tempo = 120,
            Key = "C"
        };
        var mapped = (AutoTagTrack)typeof(SpotifyMatcher)
            .GetMethod("ToAutoTagTrack", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [input])!;

        Assert.Null(mapped.Danceability);
        Assert.Null(mapped.Energy);
        Assert.Null(mapped.Tempo);
        Assert.Null(mapped.Bpm);
        Assert.Null(mapped.Key);
    }

    [Fact]
    public void AutoTag_UsesStoredSpotifyAuthBeforeBackgroundLiveValidation()
    {
        var script = File.ReadAllText(Path.Combine(
            ResolveRepoRoot(),
            "DeezSpoTag.Web",
            "wwwroot",
            "js",
            "autotag.js"));

        Assert.Contains("function applyStoredPlatformAuthReadiness()", script, StringComparison.Ordinal);
        Assert.Contains("state.platformAuth?.spotifyConnected === true", script, StringComparison.Ordinal);
        Assert.Contains("connected: hasSpotifyAuthFromPlatformState()", script, StringComparison.Ordinal);
        Assert.Contains("state.authReady = true;", script, StringComparison.Ordinal);

        var storedAuthIndex = script.IndexOf("const authData = await loadStoredAuth();", StringComparison.Ordinal);
        var readinessIndex = script.IndexOf("applyStoredPlatformAuthReadiness();", storedAuthIndex, StringComparison.Ordinal);
        var liveValidationIndex = script.IndexOf("const spotifyStatusRefresh = loadSpotifyStatus()", storedAuthIndex, StringComparison.Ordinal);
        var initialRenderIndex = script.IndexOf("loadConfigToUI();", storedAuthIndex, StringComparison.Ordinal);

        Assert.True(storedAuthIndex >= 0);
        Assert.True(readinessIndex > storedAuthIndex);
        Assert.True(liveValidationIndex > readinessIndex);
        Assert.True(initialRenderIndex > liveValidationIndex);
    }

    [Fact]
    public void AutoTagTrack_RawTagLookupIsCaseInsensitive()
    {
        var track = new AutoTagTrack();

        track.Other["SOURCE"] = ["SPOTIFY"];

        Assert.Equal("SPOTIFY", Assert.Single(track.Other["source"]));
    }

    [Fact]
    public void CapabilityPlanner_MapsEveryProviderSpecificWritableTag()
    {
        var field = typeof(LocalAutoTagRunner).GetField(
            "SupportedTagMap",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("SupportedTagMap not found.");
        var map = Assert.IsAssignableFrom<IDictionary>(field.GetValue(null));

        Assert.Equal(SupportedTag.ReplayGain, map["replayGain"]);
        Assert.Equal(SupportedTag.Source, map["source"]);
        Assert.Equal(SupportedTag.Rating, map["rating"]);
        Assert.Equal(SupportedTag.Language, map["language"]);
    }

    [Fact]
    public void EveryRegisteredPlatformHasAnExplicitWritableCapabilityContract()
    {
        var descriptors = CreateAllPlatforms().Select(platform => platform.Describe()).ToList();
        var map = GetSupportedTagMap();
        var writable = map.Values.Cast<SupportedTag>().ToHashSet();

        Assert.Equal(14, descriptors.Count);
        Assert.Equal(14, descriptors.Select(descriptor => descriptor.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(descriptors, descriptor =>
        {
            Assert.NotEmpty(descriptor.SupportedTags);
            Assert.Equal(descriptor.SupportedTags, descriptor.Platform.SupportedTags);
            Assert.Equal(descriptor.DownloadTags, descriptor.Platform.DownloadTags);
            Assert.All(descriptor.SupportedTags, tag => Assert.Contains(tag, writable));
            Assert.All(descriptor.DownloadTags, tag => Assert.True(map.Contains(tag), $"{descriptor.Id} offers unwritable download tag '{tag}'."));
        });
    }

    [Fact]
    public async System.Threading.Tasks.Task MetadataRendering_UsesOnlyExplicitContracts_NotHistoricalObservedTags()
    {
        var environment = new StubWebHostEnvironment();
        var registry = new PortedPlatformRegistry(
        [
            new ShazamPlatform(environment),
            new SpotifyPlatform(environment)
        ]);
        var service = new AutoTagMetadataService(registry, NullLogger<AutoTagMetadataService>.Instance);

        var json = await service.GetPlatformsJsonAsync();
        using var document = JsonDocument.Parse(Assert.IsType<string>(json));
        var shazam = document.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetString() == "shazam");
        var spotify = document.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetString() == "spotify");
        var shazamTags = shazam.GetProperty("supportedTags").EnumerateArray().Select(item => item.GetString()).ToList();
        var spotifyTags = spotify.GetProperty("supportedTags").EnumerateArray().Select(item => item.GetString()).ToList();

        Assert.DoesNotContain("releaseId", shazamTags);
        Assert.DoesNotContain("catalogNumber", shazamTags);
        Assert.DoesNotContain("bpm", spotifyTags);
        Assert.DoesNotContain("danceability", spotifyTags);
    }

    [Fact]
    public void ProviderDescriptors_IncludeOnlyProviderOwnedMappedMetadata()
    {
        var environment = new StubWebHostEnvironment();

        AssertContainsAll(
            new DeezerPlatform(environment).Describe(),
            SupportedTag.Version,
            SupportedTag.ReplayGain,
            SupportedTag.Copyright,
            SupportedTag.Source,
            SupportedTag.Composer,
            SupportedTag.InvolvedPeople);
        AssertContainsAll(
            new BoomplayPlatform(environment).Describe(),
            SupportedTag.ReleaseId,
            SupportedTag.AlbumId,
            SupportedTag.Composer,
            SupportedTag.Language,
            SupportedTag.Source);
        AssertContainsAll(
            new ItunesPlatform(environment).Describe(),
            SupportedTag.Source);
        AssertContainsAll(
            new BandcampPlatform(environment).Describe(),
            SupportedTag.AlbumArtist);

        Assert.DoesNotContain(SupportedTag.CatalogNumber, new DeezerPlatform(environment).Describe().SupportedTags);
        Assert.DoesNotContain(SupportedTag.Rating, new DeezerPlatform(environment).Describe().SupportedTags);
        Assert.DoesNotContain(SupportedTag.CatalogNumber, new BpmSupremePlatform(environment).Describe().SupportedTags);
        Assert.DoesNotContain(SupportedTag.ReleaseId, new ShazamPlatform(environment).Describe().SupportedTags);
        Assert.DoesNotContain(SupportedTag.Barcode, new ItunesPlatform(environment).Describe().SupportedTags);
        Assert.DoesNotContain(SupportedTag.AlbumArtistId, new ItunesPlatform(environment).Describe().SupportedTags);
    }

    [Fact]
    public void ProviderMappers_PreserveOwnedIdentifiersAndDoNotFabricateCatalogNumbers()
    {
        var deezer = new DeezerTrack
        {
            Id = 3135556,
            Title = "Harder Better Faster Stronger",
            TitleShort = "Harder Better Faster Stronger",
            Artist = new DeezerArtist { Id = 27, Name = "Daft Punk" },
            Album = new DeezerAlbum { Id = 302127, Title = "Discovery" }
        }.ToTrackInfo();
        Assert.Equal("3135556", deezer.TrackId);
        Assert.Null(typeof(DeezerTrackInfo).GetProperty("CatalogNumber"));

        var bpm = Assert.Single(new BpmSupremeSong
        {
            Id = 41234,
            Title = "Club Edit",
            Artist = "Test Artist",
            Genre = new BpmSupremeGenre { Name = "House" }
        }.ToTracks());
        Assert.Equal("41234", bpm.TrackId);
        Assert.Null(typeof(BpmSupremeTrackInfo).GetProperty("CatalogNumber"));

        var boomplay = InvokeMapper<BoomplayMatcher, BoomplayTrackMetadata>(
            new BoomplayTrackMetadata
            {
                Id = "12345678",
                AlbumId = "87654321",
                Title = "Owned Identity",
                Artist = "Artist"
            });
        Assert.Equal("12345678", boomplay.TrackId);
        Assert.Equal("87654321", boomplay.ReleaseId);
        Assert.Equal("87654321", boomplay.AlbumId);
    }

    [Fact]
    public void ProviderMappers_ReturnEveryNewlyOfferedValue()
    {
        var itunes = InvokeMapper<ItunesMatcher, ItunesTrackInfo>(new ItunesTrackInfo
        {
            TrackId = "1710609788",
            ReleaseId = "1710609780",
            ArtistId = "1234",
            Title = "Apple Track"
        });
        Assert.Equal("iTunes", Assert.Single(itunes.Other["source"]));
        Assert.Equal("1710609788", Assert.Single(itunes.Other["sourceId"]));

        var bandcamp = InvokeMapper<BandcampMatcher, BandcampTrackInfo>(new BandcampTrackInfo
        {
            Title = "Bandcamp Track",
            TrackTotal = 8
        });
        Assert.Equal("album", bandcamp.ReleaseType, ignoreCase: true);

        var discogs = InvokeMapper<DiscogsMatcher, DiscogsTrackInfo>(new DiscogsTrackInfo
        {
            Title = "Discogs Track",
            TrackTotal = 9
        });
        Assert.Equal("album", discogs.ReleaseType, ignoreCase: true);

        var beatport = AutoTagTrackFactory.FromBeatport(new BeatportTrackInfo
        {
            Title = "Beatport Track",
            Version = "Extended Mix",
            Artists = ["Artist"],
            AlbumArtists = ["Album Artist"],
            Album = "Release",
            Key = "8A",
            Bpm = 124,
            Genres = ["House"],
            Styles = ["Deep House"],
            Art = "https://example.test/beatport.jpg",
            Url = "https://beatport.com/track/test/100",
            Label = "Label",
            CatalogNumber = "CAT100",
            TrackId = "100",
            ReleaseId = "200",
            Duration = TimeSpan.FromMinutes(6),
            Remixers = ["Remixer"],
            TrackNumber = 2,
            TrackTotal = 10,
            Isrc = "USAAA2600001",
            ReleaseDate = new DateTime(2026, 1, 1),
            PublishDate = new DateTime(2025, 12, 1),
            Other = [("BEATPORT_EXCLUSIVE", ["1"])]
        });
        AssertReturnedTagsAreOffered(new BeatportPlatform(new StubWebHostEnvironment()).Describe(), beatport);

        var traxsource = AutoTagTrackFactory.FromTraxsource(new TraxsourceTrackInfo
        {
            Title = "Traxsource Track",
            Version = "Original Mix",
            Artists = ["Artist"],
            AlbumArtists = ["Album Artist"],
            Album = "Release",
            Key = "Am",
            Bpm = 122,
            Genres = ["Soulful House"],
            Art = "https://example.test/traxsource.jpg",
            Url = "https://traxsource.com/track/300/test",
            Label = "Label",
            CatalogNumber = "CAT300",
            TrackId = "300",
            ReleaseId = "400",
            Duration = TimeSpan.FromMinutes(5),
            TrackNumber = 1,
            TrackTotal = 8,
            ReleaseDate = new DateTime(2026, 2, 1)
        });
        AssertReturnedTagsAreOffered(new TraxsourcePlatform(new StubWebHostEnvironment()).Describe(), traxsource);
    }

    [Fact]
    public void PlatformLyricsResolution_IsRestrictedToTheCurrentProvider()
    {
        var source = File.ReadAllText(Path.Combine(
            ResolveRepoRoot(),
            "DeezSpoTag.Web",
            "Services",
            "AutoTag",
            "LocalAutoTagRunner.cs"));

        Assert.Contains("lookupSettings.LyricsFallbackOrder = provider;", source, StringComparison.Ordinal);
        Assert.Contains("RestrictLyricsRequestToProvider", source, StringComparison.Ordinal);
        Assert.Contains("provider is not AppleProvider and not DeezerPlatform and not SpotifyPlatform", source, StringComparison.Ordinal);
    }

    private static void AssertContainsAll(AutoTagPlatformDescriptor descriptor, params SupportedTag[] tags)
    {
        foreach (var tag in tags)
        {
            Assert.Contains(tag, descriptor.SupportedTags);
        }
    }

    private static IDictionary GetSupportedTagMap()
    {
        var field = typeof(LocalAutoTagRunner).GetField(
            "SupportedTagMap",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("SupportedTagMap not found.");
        return Assert.IsAssignableFrom<IDictionary>(field.GetValue(null));
    }

    private static AutoTagTrack InvokeMapper<TMatcher, TInput>(TInput input)
    {
        var method = typeof(TMatcher).GetMethod(
            "ToAutoTagTrack",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{typeof(TMatcher).Name}.ToAutoTagTrack not found.");
        return Assert.IsType<AutoTagTrack>(method.Invoke(null, [input]));
    }

    private static void AssertReturnedTagsAreOffered(AutoTagPlatformDescriptor descriptor, AutoTagTrack track)
    {
        var collect = typeof(LocalAutoTagRunner).GetMethod(
            "CollectAutoTagTags",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("LocalAutoTagRunner.CollectAutoTagTags not found.");
        var returned = Assert.IsType<List<string>>(collect.Invoke(null, [track]));
        var map = GetSupportedTagMap();

        foreach (var tag in returned)
        {
            var mapped = Assert.IsType<SupportedTag>(map[tag]);
            Assert.Contains(mapped, descriptor.SupportedTags);
        }
    }

    private static IReadOnlyList<IAutoTagPlatform> CreateAllPlatforms()
    {
        var environment = new StubWebHostEnvironment();
        return
        [
            new MusicBrainzPlatform(environment),
            new ShazamPlatform(environment),
            new BandcampPlatform(environment),
            new BpmSupremePlatform(environment),
            new ItunesPlatform(environment),
            new MusixmatchPlatform(environment),
            new LrclibPlatform(environment),
            new SpotifyPlatform(environment),
            new LastFmPlatform(environment),
            new DeezerPlatform(environment),
            new BoomplayPlatform(environment),
            new BeatportPlatform(environment),
            new DiscogsPlatform(environment),
            new TraxsourcePlatform(environment)
        ];
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !Directory.Exists(Path.Combine(current.FullName, "DeezSpoTag.Web")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
