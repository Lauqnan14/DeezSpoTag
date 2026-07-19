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
        Assert.Equal(SupportedTag.Lyricist, map["lyricist"]);
        Assert.Equal(SupportedTag.Publisher, map["publisher"]);
        Assert.Equal(SupportedTag.Description, map["description"]);
    }

    [Fact]
    public void AudioFilePersistenceVerifier_HandlesEveryAudioFileTagForEachSupportedFormat()
    {
        var source = ReadLocalAutoTagRunnerSource();
        var audioFileTags = Enum.GetValues<SupportedTag>()
            .Where(tag => tag is not SupportedTag.TtmlLyrics and not SupportedTag.OtherTags)
            .ToList();

        AssertSwitchHandlesEveryTag(source, "HasId3Tag", audioFileTags);
        AssertSwitchHandlesEveryTag(source, "HasVorbisTag", audioFileTags);
        AssertSwitchHandlesEveryTag(source, "HasMp4Tag", audioFileTags);
    }

    [Fact]
    public void WriterVerification_UsesActualOverwriteAwareWriteContract()
    {
        var source = ReadLocalAutoTagRunnerSource();

        Assert.Contains("var writeResult = await TagFileAsync(", source, StringComparison.Ordinal);
        Assert.Contains("returnedTags.IntersectWith(writeResult.AttemptedTags);", source, StringComparison.Ordinal);
        Assert.Contains("HashSet<SupportedTag> AttemptedTags", source, StringComparison.Ordinal);
        Assert.Contains("context.AttemptedTags.Add(tag);", source, StringComparison.Ordinal);
        Assert.Contains("ShouldOverwriteTag(context.Config, tag)", source, StringComparison.Ordinal);
        Assert.Contains("ShouldOverwriteRawTag(context.File, context.Extension, context.Config, configTagKey, rawName)", source, StringComparison.Ordinal);
        Assert.Contains("MarkAttemptedIfPresent(context, file, SupportedTag.ReleaseDate);", source, StringComparison.Ordinal);
        Assert.Contains("MarkAttemptedIfPresent(context, file, SupportedTag.TrackNumber);", source, StringComparison.Ordinal);
        Assert.Contains("MarkAttemptedIfPresent(context, file, SupportedTag.AlbumArt);", source, StringComparison.Ordinal);
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
    public void EnhancementTagSelection_ExposesEveryRegisteredPlatformCapability()
    {
        var script = File.ReadAllText(Path.Combine(
            ResolveRepoRoot(),
            "DeezSpoTag.Web",
            "wwwroot",
            "js",
            "autotag.js"));
        var tagList = ExtractConstArray(script, "const TAGS = [");
        var exposed = tagList
            .Split(["tag:"], StringSplitOptions.None)
            .Select(part =>
            {
                var start = part.IndexOf('"');
                if (start < 0)
                {
                    return null;
                }

                var end = part.IndexOf('"', start + 1);
                return end > start ? part[(start + 1)..end] : null;
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in CreateAllPlatforms().Select(platform => platform.Describe()))
        {
            foreach (var tag in descriptor.SupportedTags)
            {
                Assert.Contains(ToUiTagKey(tag), exposed);
            }
        }
    }

    [Fact]
    public void DownloadTagContracts_DoNotOmitNonLyricsProviderCapabilities()
    {
        var normalize = typeof(AutoTagService).GetMethod(
            "NormalizeSupportedTagKey",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AutoTagService.NormalizeSupportedTagKey not found.");

        foreach (var descriptor in CreateAllPlatforms().Select(platform => platform.Describe()).Where(descriptor => descriptor.DownloadTags.Count > 0))
        {
            var normalizedDownloadTags = descriptor.DownloadTags
                .Select(tag => Assert.IsType<string>(normalize.Invoke(null, [tag])))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var supportedTag in descriptor.SupportedTags.Where(tag => !IsLyricsTag(tag)))
            {
                Assert.Contains(ToUiTagKey(supportedTag), normalizedDownloadTags);
            }
        }
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
            SupportedTag.AlbumArtist,
            SupportedTag.Description);
        AssertContainsAll(
            new ShazamPlatform(environment).Describe(),
            SupportedTag.Composer,
            SupportedTag.Lyricist,
            SupportedTag.Publisher,
            SupportedTag.Language);
        AssertContainsAll(
            new SpotifyPlatform(environment).Describe(),
            SupportedTag.Copyright);
        AssertContainsAll(
            new DiscogsPlatform(environment).Describe(),
            SupportedTag.ReleaseCountry,
            SupportedTag.Media,
            SupportedTag.Composer,
            SupportedTag.Lyricist,
            SupportedTag.Publisher,
            SupportedTag.Remixer,
            SupportedTag.InvolvedPeople);

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
            Title = "Apple Track",
            Artists = ["Apple Artist"],
            AlbumArtists = ["Apple Album Artist"],
            Album = "Apple Album",
            Url = "https://music.apple.com/us/song/apple-track/1710609788",
            Duration = TimeSpan.FromMinutes(3),
            Genres = ["Pop"],
            ReleaseDate = new DateTime(2026, 3, 1),
            TrackNumber = 2,
            TrackTotal = 12,
            DiscNumber = 1,
            DiscTotal = 2,
            ReleaseType = "album",
            Isrc = "USAAA2600002",
            Label = "Apple Label",
            Copyright = "℗ 2026 Apple Label",
            Explicit = true,
            Art = "https://example.test/apple.jpg"
        });
        Assert.Equal("iTunes", Assert.Single(itunes.Other["source"]));
        Assert.Equal("1710609788", Assert.Single(itunes.Other["sourceId"]));
        AssertReturnedTagsAreOffered(new ItunesPlatform(new StubWebHostEnvironment()).Describe(), itunes);

        var bandcamp = InvokeMapper<BandcampMatcher, BandcampTrackInfo>(new BandcampTrackInfo
        {
            Title = "Bandcamp Track",
            TrackTotal = 8,
            Description = "Bandcamp description"
        });
        Assert.Equal("album", bandcamp.ReleaseType, ignoreCase: true);
        Assert.Equal("Bandcamp description", bandcamp.Description);
        AssertReturnedTagsAreOffered(new BandcampPlatform(new StubWebHostEnvironment()).Describe(), bandcamp);

        var discogs = InvokeMapper<DiscogsMatcher, DiscogsTrackInfo>(new DiscogsTrackInfo
        {
            Title = "Discogs Track",
            TrackTotal = 9,
            ReleaseCountry = "US",
            Media = ["1 x Vinyl, LP"],
            Composers = ["Discogs Composer"],
            Remixers = ["Discogs Remixer"],
            InvolvedPeople = ["Producer: Discogs Producer"],
            Lyricist = "Discogs Lyricist",
            Publisher = "Discogs Publisher"
        });
        Assert.Equal("album", discogs.ReleaseType, ignoreCase: true);
        Assert.Equal("US", discogs.ReleaseCountry);
        Assert.Equal("1 x Vinyl, LP", Assert.Single(discogs.Media));
        Assert.Equal("Discogs Lyricist", discogs.Lyricist);
        Assert.Equal("Discogs Publisher", discogs.Publisher);
        AssertReturnedTagsAreOffered(new DiscogsPlatform(new StubWebHostEnvironment()).Describe(), discogs);

        var spotify = InvokeMapper<SpotifyMatcher, SpotifyTrackInfo>(new SpotifyTrackInfo
        {
            Title = "Spotify Track",
            Artists = ["Spotify Artist"],
            TrackId = "0VjIjW4GlUZAMYd2vXMi3b",
            ReleaseId = "album-id",
            Duration = TimeSpan.FromMinutes(3),
            Copyright = "℗ 2026 Spotify Label"
        });
        Assert.Equal("℗ 2026 Spotify Label", Assert.Single(spotify.Other["copyright"]));
        AssertReturnedTagsAreOffered(new SpotifyPlatform(new StubWebHostEnvironment()).Describe(), spotify);

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

    private static void AssertSwitchHandlesEveryTag(string source, string methodName, IReadOnlyCollection<SupportedTag> tags)
    {
        var body = ExtractMethodBody(source, methodName);
        foreach (var tag in tags)
        {
            Assert.Contains($"SupportedTag.{tag}", body);
        }
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        var methodStart = source.IndexOf($"private static bool {methodName}", StringComparison.Ordinal);
        if (methodStart < 0)
        {
            throw new InvalidOperationException($"{methodName} method body was not found.");
        }

        var bodyStart = source.IndexOf('{', methodStart);
        if (bodyStart < 0)
        {
            throw new InvalidOperationException($"{methodName} method body was not found.");
        }

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[bodyStart..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException($"{methodName} method body terminator was not found.");
    }

    private static string ReadLocalAutoTagRunnerSource()
        => File.ReadAllText(Path.Combine(
            ResolveRepoRoot(),
            "DeezSpoTag.Web",
            "Services",
            "AutoTag",
            "LocalAutoTagRunner.cs"));

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

    private static bool IsLyricsTag(SupportedTag tag)
        => tag is SupportedTag.SyncedLyrics or SupportedTag.UnsyncedLyrics or SupportedTag.TtmlLyrics;

    private static string ToUiTagKey(SupportedTag tag)
        => tag switch
        {
            SupportedTag.BPM => "bpm",
            SupportedTag.URL => "url",
            SupportedTag.ISRC => "isrc",
            _ => char.ToLowerInvariant(tag.ToString()[0]) + tag.ToString()[1..]
        };

    private static string ExtractConstArray(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"{marker} was not found.");
        }

        var end = source.IndexOf("];", start, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException($"{marker} terminator was not found.");
        }

        return source[start..(end + 2)];
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
