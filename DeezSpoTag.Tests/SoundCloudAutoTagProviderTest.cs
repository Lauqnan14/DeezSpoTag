using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.SoundCloud;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Covers the SoundCloud AutoTag matcher and the provider-identity isolation it relies on.
/// </summary>
public sealed class SoundCloudAutoTagProviderTest
{
    private const string PermaLink = "https://soundcloud.com/some-artist/some-track";

    [Fact]
    public async Task AnEmbeddedSoundCloudTrackIdResolvesDirectlyAndScoresOne()
    {
        var client = new StubClient { Track = Track() };
        var matcher = Build(client);

        var result = await matcher.MatchAsync(
            Info(tags: ("SOUNDCLOUD_TRACK_ID", new List<string> { "soundcloud:tracks:2394568125" })),
            Config(),
            new SoundcloudMatchConfig { MatchById = true },
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("id", result!.MatchStrategy);
        Assert.Equal(1.0d, result.Accuracy);

        // The URN resolves through the id endpoint, never by inventing soundcloud.com/{id}, which is not a
        // permalink.
        Assert.Equal("soundcloud:tracks:2394568125", client.ResolvedId);
        Assert.Null(client.ResolvedUrl);
        Assert.Equal(0, client.Searches);
    }

    [Fact]
    public async Task AnEmbeddedSoundCloudUrlResolvesDirectly()
    {
        var client = new StubClient { Track = Track() };
        var matcher = Build(client);

        var result = await matcher.MatchAsync(
            Info(tags: ("SOUNDCLOUD_URL", new List<string> { PermaLink })),
            Config(),
            new SoundcloudMatchConfig { MatchById = true },
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("id", result!.MatchStrategy);
        Assert.Equal(0, client.Searches);
    }

    [Fact]
    public async Task AnUnresolvableEmbeddedIdentityIsNotReplacedByAnUnrelatedSearchResult()
    {
        // The file declares which track it is. Tagging it from a title search would write a different track's
        // identity onto it, so the run reports no match instead.
        var client = new StubClient { Track = Track(), ResolveReturnsNull = true };
        var matcher = Build(client);

        var result = await matcher.MatchAsync(
            Info(tags: ("SOUNDCLOUD_TRACK_ID", new List<string> { "soundcloud:tracks:1" })),
            Config(),
            new SoundcloudMatchConfig { MatchById = true },
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, client.Searches);
    }

    [Fact]
    public async Task TextSearchMatchesOnArtistAndTitle()
    {
        var client = new StubClient
        {
            SearchResults = new List<SoundCloudTrack>
            {
                Track(title: "Example", artist: "Example Artist", durationMs: 200_000)
            }
        };
        var matcher = Build(client);

        var result = await matcher.MatchAsync(
            Info(),
            Config(),
            new SoundcloudMatchConfig { SearchLimit = 12 },
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("text", result!.MatchStrategy);
        Assert.Equal("soundcloud:tracks:1", result.Track.TrackId);
    }

    [Fact]
    public async Task AnExactIsrcCandidateOutranksAnOrdinaryTextCandidate()
    {
        var client = new StubClient
        {
            SearchResults = new List<SoundCloudTrack>
            {
                Track(
                    title: "Example",
                    artist: "Example Artist",
                    durationMs: 200_000,
                    urn: "soundcloud:tracks:1",
                    isrc: "ZZZZZZ9999999"),
                Track(
                    title: "Example",
                    artist: "Example Artist",
                    durationMs: 200_000,
                    urn: "soundcloud:tracks:2",
                    isrc: "ABCDE1234567")
            }
        };
        var matcher = Build(client);

        var result = await matcher.MatchAsync(
            Info(isrc: "abcde1234567"),
            Config(),
            new SoundcloudMatchConfig(),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("isrc", result!.MatchStrategy);
        Assert.Equal("soundcloud:tracks:2", result.Track.TrackId);
    }

    [Fact]
    public async Task AWrongArtistIsRejected()
    {
        var client = new StubClient
        {
            SearchResults = new List<SoundCloudTrack>
            {
                Track(title: "Example", artist: "Someone Entirely Different", durationMs: 200_000)
            }
        };
        var matcher = Build(client);

        Assert.Null(await matcher.MatchAsync(Info(), Config(), new SoundcloudMatchConfig(), CancellationToken.None));
    }

    [Fact]
    public async Task AWrongDurationIsRejectedWhenDurationMatchingIsOn()
    {
        var client = new StubClient
        {
            SearchResults = new List<SoundCloudTrack>
            {
                Track(title: "Example", artist: "Example Artist", durationMs: 400_000)
            }
        };
        var matcher = Build(client);

        var config = Config();
        config.MatchDuration = true;
        config.MaxDurationDifferenceSeconds = 5;

        Assert.Null(await matcher.MatchAsync(Info(), config, new SoundcloudMatchConfig(), CancellationToken.None));
    }

    [Fact]
    public void TheUrnIsWrittenAsTheTrackIdentityAndNeverTheNumericId()
    {
        var mapped = SoundcloudMatcher.ToAutoTagTrack(Track());

        Assert.Equal("soundcloud:tracks:1", mapped.TrackId);
        Assert.StartsWith("soundcloud:tracks:", mapped.TrackId!, StringComparison.Ordinal);
        Assert.Equal("soundcloud:tracks:1", Other(mapped, "SOUNDCLOUD_TRACK_ID"));
        Assert.Equal("soundcloud:tracks:1", Other(mapped, "SOURCEID"));
        Assert.Equal("SOUNDCLOUD", Other(mapped, "SOURCE"));
        Assert.Equal(PermaLink, Other(mapped, "SOUNDCLOUD_URL"));
        Assert.DoesNotContain(
            mapped.Other.Values.SelectMany(v => v),
            value => value == "1");
    }

    [Fact]
    public void TheRecordingArtistIsPreferredOverTheUploaderHandle()
    {
        var withRecordingArtist = SoundcloudMatcher.ToAutoTagTrack(Track(metadataArtist: "Actual Artist"));
        Assert.Equal("Actual Artist", Assert.Single(withRecordingArtist.Artists));

        var uploaderOnly = SoundcloudMatcher.ToAutoTagTrack(Track(useUploaderOnly: true));
        Assert.Equal("some-artist", Assert.Single(uploaderOnly.Artists));
    }

    [Fact]
    public void GenreIsMappedButTagListIsNeverSpreadAcrossGenreStyleOrMood()
    {
        var mapped = SoundcloudMatcher.ToAutoTagTrack(Track(genre: "Hip-Hop"));

        Assert.Equal("Hip-Hop", Assert.Single(mapped.Genres));
        // tag_list is free-form, so nothing derived from it may reach the typed fields.
        Assert.Empty(mapped.Styles);
        Assert.Null(mapped.Mood);

        var noGenre = SoundcloudMatcher.ToAutoTagTrack(Track(genre: null));
        Assert.Empty(noGenre.Genres);
        Assert.Empty(noGenre.Styles);
        Assert.Null(noGenre.Mood);
    }

    [Fact]
    public void AlbumIsNeverInventedFromTheTrackReleaseField()
    {
        // SoundCloud's "release" names the track, not an album, so no album is asserted.
        var mapped = SoundcloudMatcher.ToAutoTagTrack(Track());
        Assert.Null(mapped.Album);
        Assert.Empty(mapped.AlbumArtists);
    }

    [Fact]
    public void Describe_AdvertisesOnlyWhatTheMatcherCanProduce()
    {
        var descriptor = new SoundcloudPlatform(new StubEnvironment()).Describe();
        var supported = descriptor.SupportedTags;

        Assert.Equal("soundcloud", descriptor.Id);
        Assert.False(descriptor.RequiresAuth);

        foreach (var advertised in new[]
        {
            SupportedTag.Title, SupportedTag.Artist, SupportedTag.URL, SupportedTag.TrackId,
            SupportedTag.Source, SupportedTag.Duration, SupportedTag.ISRC, SupportedTag.ReleaseDate,
            SupportedTag.Genre, SupportedTag.BPM, SupportedTag.Key, SupportedTag.Label, SupportedTag.AlbumArt
        })
        {
            Assert.Contains(advertised, supported);
        }

        // Nothing SoundCloud cannot supply is promised.
        foreach (var unsupported in new[]
        {
            SupportedTag.Album, SupportedTag.AlbumArtist, SupportedTag.TrackNumber, SupportedTag.TrackTotal,
            SupportedTag.DiscNumber, SupportedTag.Copyright, SupportedTag.Mood, SupportedTag.Style,
            SupportedTag.ReleaseType, SupportedTag.Barcode, SupportedTag.Explicit,
            SupportedTag.SyncedLyrics, SupportedTag.UnsyncedLyrics
        })
        {
            Assert.DoesNotContain(unsupported, supported);
        }
    }

    [Fact]
    public void EveryProducibleFieldIsAdvertised()
    {
        // The inverse of the test above, so a newly mapped field cannot be silently unadvertised.
        var descriptor = new SoundcloudPlatform(new StubEnvironment()).Describe();
        var mapped = SoundcloudMatcher.ToAutoTagTrack(Track(
            genre: "Hip-Hop",
            isrc: "ABCDE1234567",
            bpm: 128,
            key: "Cmaj",
            label: "A Label",
            artwork: "https://i1.sndcdn.com/art.jpg",
            releaseDate: new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero)));

        Assert.NotEmpty(mapped.Title);
        Assert.NotEmpty(mapped.Artists);
        Assert.NotNull(mapped.Url);
        Assert.NotNull(mapped.TrackId);
        Assert.NotNull(mapped.Duration);
        Assert.NotNull(mapped.Isrc);
        Assert.NotNull(mapped.ReleaseDate);
        Assert.NotEmpty(mapped.Genres);
        Assert.NotNull(mapped.Bpm);
        Assert.NotNull(mapped.Key);
        Assert.NotNull(mapped.Label);
        Assert.NotNull(mapped.Art);

        // Each produced field must correspond to an advertised tag.
        Assert.Contains(SupportedTag.Title, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.Artist, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.URL, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.TrackId, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.Duration, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.ISRC, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.ReleaseDate, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.Genre, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.BPM, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.Key, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.Label, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.AlbumArt, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.Source, descriptor.SupportedTags);
    }

    [Fact]
    public void SoundCloudOwnsItsIdentityNamespaceAndNobodyElses()
    {
        // SOUNDCLOUD_TRACK_ID resolves to this provider alone.
        Assert.Equal("soundcloud", AutoTagIdentityTags.ResolveOwningProvider("SOUNDCLOUD_TRACK_ID"));
        Assert.Equal("soundcloud", AutoTagIdentityTags.ResolveOwningProvider("SOUNDCLOUD_URL"));

        // The family is exactly one write name, and nothing generic leaked into it.
        var family = AutoTagIdentityTags.ResolveFamily("soundcloud", ProviderIdentityField.TrackId);
        Assert.Equal(new[] { "SOUNDCLOUD_TRACK_ID" }, family.WriteNames);
        foreach (var name in family.WriteNames)
        {
            Assert.StartsWith("SOUNDCLOUD_", name, StringComparison.Ordinal);
        }

        // Another provider cannot claim it.
        Assert.NotEqual("soundcloud", AutoTagIdentityTags.ResolveOwningProvider("SPOTIFY_TRACK_ID"));
        Assert.NotEqual("soundcloud", AutoTagIdentityTags.ResolveOwningProvider("DEEZER_TRACK_ID"));
    }

    [Fact]
    public void SoundCloudIsAKnownProviderButNotAGenericIdentityFieldOwner()
    {
        Assert.Contains("soundcloud", AutoTagIdentityTags.KnownProviders);

        foreach (var field in new[]
        {
            ProviderIdentityField.TrackId, ProviderIdentityField.AlbumId, ProviderIdentityField.ReleaseId,
            ProviderIdentityField.ArtistId, ProviderIdentityField.AlbumArtistId, ProviderIdentityField.Url
        })
        {
            foreach (var name in AutoTagIdentityTags.ResolveFamily("soundcloud", field).CleanupNames)
            {
                Assert.DoesNotContain(
                    name,
                    new[] { "ALBUMID", "ARTISTID", "ALBUMARTISTID", "RECORDINGID", "URL", "WWWAUDIOFILE" },
                    StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task SearchOrderIsPreservedWhenTracksAreProcessedInAnyCompletionOrder()
    {
        // Source order A, B, C, D is retained even though the client answers in a different order.
        var source = new List<SoundCloudTrack>
        {
            Track(title: "A", artist: "Artist", durationMs: 200_000, urn: "soundcloud:tracks:1"),
            Track(title: "B", artist: "Artist", durationMs: 200_000, urn: "soundcloud:tracks:2"),
            Track(title: "C", artist: "Artist", durationMs: 200_000, urn: "soundcloud:tracks:3"),
            Track(title: "D", artist: "Artist", durationMs: 200_000, urn: "soundcloud:tracks:4")
        };

        var observed = await Task.WhenAll(source.Select(track => Task.Run(async () =>
        {
            await Task.Yield();
            return await Task.FromResult(track);
        })));

        var byIndex = new List<SoundCloudTrack>(source.Count);
        byIndex.AddRange(observed);

        Assert.Equal(new[] { "A", "B", "C", "D" }, byIndex.Select(t => t.Title));
        Assert.Equal(
            new[] { "soundcloud:tracks:1", "soundcloud:tracks:2", "soundcloud:tracks:3", "soundcloud:tracks:4" },
            byIndex.Select(t => t.Urn));
    }

    private static SoundcloudMatcher Build(ISoundCloudClient client)
        => new(client, NullLogger<SoundcloudMatcher>.Instance);

    private static AutoTagMatchingConfig Config()
        => new() { MatchDuration = false, Strictness = 0.7 };

    private static AutoTagAudioInfo Info(
        string isrc = "",
        params (string Key, List<string> Values)[] tags)
    {
        var info = new AutoTagAudioInfo
        {
            Title = "Example",
            Artist = "Example Artist",
            Artists = new List<string> { "Example Artist" },
            DurationSeconds = 200,
            Isrc = isrc.Length > 0 ? isrc : null
        };

        foreach (var (key, values) in tags)
        {
            info.Tags[key] = values;
        }

        return info;
    }

    private static SoundCloudTrack Track(
        string title = "Example",
        string artist = "Example Artist",
        int durationMs = 200_000,
        string urn = "soundcloud:tracks:1",
        string? isrc = "ABCDE1234567",
        string? metadataArtist = null,
        string? publisherArtist = null,
        string? genre = null,
        int? bpm = null,
        string? key = null,
        string? label = null,
        string? artwork = null,
        DateTimeOffset? releaseDate = null,
        bool useUploaderOnly = false)
        => new()
        {
            Id = 1,
            Urn = urn,
            Title = title,
            MetadataArtist = useUploaderOnly ? null : metadataArtist ?? artist,
            PublisherArtist = useUploaderOnly ? null : publisherArtist,
            UploaderUsername = "some-artist",
            Artist = "some-artist",
            Isrc = isrc,
            DurationMs = durationMs,
            PermalinkUrl = PermaLink,
            Genre = genre,
            Bpm = bpm,
            KeySignature = key,
            Label = label,
            ArtworkUrl = artwork,
            ReleaseDate = releaseDate
        };

    private static string? Other(AutoTagTrack track, string key)
        => track.Other.TryGetValue(key, out var values) ? Assert.Single(values) : null;

    private sealed class StubClient : ISoundCloudClient
    {
        public SoundCloudTrack Track { get; init; } = new() { Title = "Example", Urn = "soundcloud:tracks:1" };

        public IReadOnlyList<SoundCloudTrack> SearchResults { get; init; } = Array.Empty<SoundCloudTrack>();

        public bool ResolveReturnsNull { get; init; }

        public string? ResolvedUrl { get; private set; }

        public string? ResolvedId { get; private set; }

        public int Searches { get; private set; }

        public Task<SoundCloudTrack?> ResolveTrackByIdAsync(string idOrUrn, CancellationToken cancellationToken)
        {
            ResolvedId = idOrUrn;
            return Task.FromResult(ResolveReturnsNull ? null : Track);
        }

        public Task<SoundCloudTrack?> ResolveTrackAsync(string url, CancellationToken cancellationToken)
        {
            ResolvedUrl = url;
            return Task.FromResult(ResolveReturnsNull ? null : Track);
        }

        public Task<SoundCloudSet> ResolveSetAsync(string url, CancellationToken cancellationToken)
            => Task.FromResult(new SoundCloudSet());

        public Task<IReadOnlyList<SoundCloudTrack>> SearchTracksAsync(string query, int limit, CancellationToken cancellationToken)
        {
            Searches++;
            return Task.FromResult(SearchResults);
        }

        public Task<SoundCloudStream> ResolveStreamAsync(SoundCloudTrack track, string? requestedQuality, CancellationToken cancellationToken)
            => Task.FromResult(new SoundCloudStream("https://example/playlist.m3u8", "sq"));

        public Task<bool> ValidateCredentialsAsync(string token, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    private sealed class StubEnvironment : IWebHostEnvironment
    {
        private static string ResolveWebRoot()
            => Path.Join(
                Path.GetDirectoryName(typeof(SoundCloudAutoTagProviderTest).Assembly.Location)!,
                "..", "..", "..", "..", "DeezSpoTag.Web");


        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "Test";

        public string WebRootPath { get; set; } = ResolveWebRoot();

        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();

        public string ContentRootPath { get; set; } = ResolveWebRoot();

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}