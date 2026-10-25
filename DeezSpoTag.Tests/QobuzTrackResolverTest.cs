using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Services.Metadata.Qobuz;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class QobuzTrackResolverTest
{
    [Fact]
    public async Task KnownId_WithVerifiedRecordingMetadataDoesNotDependOnSearchIndex()
    {
        var service = new StubQobuzMetadataService { TrackResult = new QobuzTrack {
            Id = 124134888, Title = "Don't Mess With My Man", Version = "Remix", ISRC = "FR8GV1715720",
            Duration = 188, Performer = new QobuzArtist { Name = "Nivea" } } };
        Assert.True(QobuzTrackId.TryCreate(124134888, out var id));
        var result = await CreateResolver(service).ValidateTrackIdAsync(id, "FR8GV1715720",
            "Don't Mess With My Man - Remix", "Nivea", null, 188000, CancellationToken.None);
        Assert.Equal(124134888, result?.Track.Id);
        Assert.Empty(service.Queries);
    }

    [Theory]
    [InlineData("FR8GV1715720", "An Entirely Different Song")]
    [InlineData(null, "An Entirely Different Song")]
    [InlineData("FR8GV1715720", "Don't Mess With My Man - Another Song")]
    [InlineData(null, "Don't Mess With My Man - Another Song")]
    public async Task MetadataResult_CannotOverrideADifferentTitleByCopyingTheSourceIsrc(string? sourceIsrc, string candidateTitle)
    {
        var service = new StubQobuzMetadataService { SearchHandler = _ => [new QobuzTrack {
            Id = 124134888, ISRC = "FR8GV1715720", Title = candidateTitle, Duration = 188,
            Performer = new QobuzArtist { Name = "Nivea" } }] };
        Assert.Null(await CreateResolver(service).ResolveTrackAsync(sourceIsrc, "Don't Mess With My Man",
            "Nivea", null, 188000, CancellationToken.None));
    }

    [Theory]
    [InlineData("Don't Mess With My Man - Remix", "Remix", true)]
    [InlineData("Don't Mess With My Man - Remix", "Live", false)]
    [InlineData("Don't Mess With My Man", "Remix", false)]
    [InlineData("Don't Mess With My Man - Remix", null, false)]
    public async Task SeparateVersionField_IsIncludedInExactRecordingValidation(string sourceTitle, string? version, bool accepted)
    {
        var service = new StubQobuzMetadataService { IsrcResult = new QobuzTrack {
            Id = 124134888, ISRC = "FR8GV1715720", Title = "Don't Mess With My Man", Version = version,
            Duration = 188, Performer = new QobuzArtist { Name = "Nivea" } } };
        var result = await CreateResolver(service).ResolveTrackAsync("FR8GV1715720", sourceTitle,
            "Nivea", null, 188000, CancellationToken.None);
        Assert.Equal(accepted, result is not null);
        if (accepted) Assert.Equal(124134888, result!.Track.Id);
    }

    [Fact]
    public async Task ValidatedMetadataSearchCanFinishWhileIsrcLookupIsStillPending()
    {
        var pending = new TaskCompletionSource<QobuzTrack?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StubQobuzMetadataService { PendingIsrc = pending.Task,
            SearchHandler = _ => [new QobuzTrack { Id = 74444082, ISRC = "USGZ20930467", Title = "Big Boy", Duration = 176,
                Performer = new QobuzArtist { Name = "Jackson 5" } }] };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            var result = await CreateResolver(service).ResolveTrackAsync("USGZ20930467", "Big Boy", "The Jackson Five", null, 176000, deadline.Token);
            Assert.Equal(74444082, result?.Track.Id);
        }
        finally { pending.TrySetResult(null); }
    }

    [Theory]
    [InlineData("Artist", 180, true)]
    [InlineData("Different Artist", 180, false)]
    [InlineData("Artist", 250, false)]
    public async Task YouTubeConversion_ValidatesReturnedQobuzIdBeforeDownload(string hydratedArtist, int duration, bool expected)
    {
        var service = new StubQobuzMetadataService {
            SearchHandler = _ => [new QobuzTrack { Id = 123, Title = "Song", Performer = new QobuzArtist { Name = "Artist" } }],
            TrackResult = new QobuzTrack { Id = 123, Title = "Song", Duration = duration, Performer = new QobuzArtist { Name = hydratedArtist } }
        };
        var result = await CreateResolver(service).ResolveTrackAsync(null, "Song", "Artist", null, 180000, CancellationToken.None,
            rejectAmbiguous: true, conversionQuery: "Song Artist");
        Assert.Equal(expected, result is not null);
        if (expected) Assert.Equal(123, result!.Track.Id);
        Assert.Equal(new[] { "Song Artist" }, service.Queries);
    }

    [Fact]
    public async Task ResolveTrackAsync_StrictRejectsAmbiguousRecordings()
    {
        var resolver = CreateResolver(new StubQobuzMetadataService { SearchHandler = _ => [
            new QobuzTrack { Id = 1, Title = "Song", Duration = 180, Performer = new QobuzArtist { Name = "Artist" } },
            new QobuzTrack { Id = 2, Title = "Song", Duration = 180, Performer = new QobuzArtist { Name = "Artist" } }] });
        Assert.Null(await resolver.ResolveTrackAsync(null, "Song", "Artist", null, 180000, CancellationToken.None, rejectAmbiguous: true));
    }

    [Fact]
    public async Task ResolveTrackAsync_PreservesLookupFailureWhenNoSourceResolvesIdentity()
    {
        var service = new StubQobuzMetadataService { IsrcFailure = new InvalidOperationException("catalog source unavailable") };
        var failure = await Assert.ThrowsAsync<AggregateException>(() => CreateResolver(service).ResolveTrackAsync(
            "USGZ20930467", "Big Boy", "The Jackson Five", "The First Recordings", 176000, CancellationToken.None));
        Assert.Contains("catalog lookup failed", failure.Message);
        Assert.Contains(failure.InnerExceptions, exception => exception.Message == "catalog source unavailable");
    }

    [Fact]
    public async Task ResolveTrackAsync_ExistingMetadataSearchCanRecoverFromIsrcFailure()
    {
        var service = new StubQobuzMetadataService
        {
            IsrcFailure = new InvalidOperationException("catalog source unavailable"),
            SearchHandler = _ => [new QobuzTrack { Id = 74444082, ISRC = "USGZ20930467", Title = "Big Boy",
                Duration = 176, Performer = new QobuzArtist { Name = "Jackson 5" } }]
        };
        var result = await CreateResolver(service).ResolveTrackAsync("USGZ20930467", "Big Boy", "The Jackson Five", "The First Recordings", 176000, CancellationToken.None);
        Assert.Equal(74444082, result?.Track.Id);
    }

    [Fact]
    public async Task ResolveTrackAsync_SelectsBestValidCandidateAfterInvalidHigherScore()
    {
        var invalid = new QobuzTrack { Id = 1, Title = "Big Boy", ISRC = "USGZ20930467", Duration = 222,
            Performer = new QobuzArtist { Name = "The Jackson Five" }, Album = new QobuzAlbum { Title = "The First Recordings" },
            MaximumBitDepth = 24, MaximumSamplingRate = 96 };
        var valid = new QobuzTrack { Id = 74444082, Title = "Big Boy", ISRC = "USGZ20930467", Duration = 190,
            Performer = new QobuzArtist { Name = "The Jackson Five" } };
        var resolver = CreateResolver(new StubQobuzMetadataService { SearchHandler = _ => [invalid, valid] });
        var result = await resolver.ResolveTrackAsync("USGZ20930467", "Big Boy", "The Jackson Five", "The First Recordings", 176000, CancellationToken.None);
        Assert.Equal(74444082, result?.Track.Id);
    }

    [Theory]
    [InlineData("USSM11604298", "For Free (feat. Drake)", "DJ Khaled", 184000, "USCM51600263", 183)]
    [InlineData("GBAHS1800090", "Cry", "Anne-Marie", 271000, "GBAHS1800286", 215)]
    [InlineData("GBAHS1800795", "Perfect To Me", "Anne-Marie", 288000, "GBAHS1800729", 197)]
    public async Task ResolveTrackAsync_DoesNotSubstituteDifferentRecording(string isrc, string title, string artist,
        int duration, string candidateIsrc, int candidateDuration)
    {
        var resolver = CreateResolver(new StubQobuzMetadataService { SearchHandler = _ => [new QobuzTrack {
            Id = 1, ISRC = candidateIsrc, Title = title, Duration = candidateDuration, Performer = new QobuzArtist { Name = artist } }] });
        Assert.Null(await resolver.ResolveTrackAsync(isrc, title, artist, null, duration, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveTrackAsync_AcceptsBigBoyExactIsrcWithVerifiedArtistAlias()
    {
        var resolver = CreateResolver(new StubQobuzMetadataService
        {
            IsrcResult = new QobuzTrack { Id = 74444082, ISRC = "USGZ20930467", Title = "Big Boy",
                Duration = 176, Performer = new QobuzArtist { Name = "Jackson 5" } }
        });
        var result = await resolver.ResolveTrackAsync("USGZ20930467", "Big Boy", "The Jackson Five",
            "The First Recordings", 176000, CancellationToken.None);
        Assert.Equal(74444082, result?.Track.Id);
    }

    [Fact]
    public async Task ResolveTrackAsync_AcceptsExactIsrcWithoutTitleArtist()
    {
        var resolver = CreateResolver(new StubQobuzMetadataService
        {
            IsrcResult = new QobuzTrack
            {
                Id = 411245095,
                ISRC = "GBDUW0000059",
                Title = "Harder Better Faster Stronger",
                Performer = new QobuzArtist { Name = "Daft Punk" }
            }
        });

        var result = await resolver.ResolveTrackAsync(
            "GBDUW0000059",
            title: null,
            artist: null,
            album: null,
            durationMs: null,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(411245095, result!.Track.Id);
    }

    [Fact]
    public async Task ResolveTrackAsync_RejectsContradictoryIsrcMetadata()
    {
        var resolver = CreateResolver(new StubQobuzMetadataService
        {
            IsrcResult = new QobuzTrack
            {
                Id = 411245095,
                ISRC = "GBDUW0000059",
                Title = "Wrong Song",
                Performer = new QobuzArtist { Name = "Wrong Artist" },
                Duration = 120
            }
        });

        var result = await resolver.ResolveTrackAsync(
            "GBDUW0000059",
            "Harder Better Faster Stronger",
            "Daft Punk",
            "Discovery",
            durationMs: 224000,
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveTrackAsync_UsesAlbumAwareQueries()
    {
        var service = new StubQobuzMetadataService();
        service.AlbumSearchHandler = query => query.Contains("Discovery", StringComparison.OrdinalIgnoreCase)
            ? new List<QobuzTrack>
            {
                new()
                {
                    Id = 99112233,
                    Title = "Harder, Better, Faster, Stronger",
                    Duration = 224,
                    Performer = new QobuzArtist { Name = "Daft Punk" },
                    Album = new QobuzAlbum { Title = "Discovery" }
                }
            }
            : new List<QobuzTrack>();
        var resolver = CreateResolver(service);

        var result = await resolver.ResolveTrackAsync(
            isrc: null,
            title: "Harder Better Faster Stronger",
            artist: "Daft Punk",
            album: "Discovery",
            durationMs: 224000,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(99112233, result!.Track.Id);
        Assert.Contains(service.Queries, query => query.Contains("Discovery", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveTrackAsync_RejectsMetadataMatchWhenAlbumContradictsSource()
    {
        var service = new StubQobuzMetadataService();
        service.SearchHandler = _ => new List<QobuzTrack>
        {
            new()
            {
                Id = 77112233,
                Title = "Raha",
                Duration = 173,
                Performer = new QobuzArtist { Name = "Arrow Bwoy" },
                Album = new QobuzAlbum { Title = "Wrong Album" }
            }
        };
        var resolver = CreateResolver(service);

        var result = await resolver.ResolveTrackAsync(
            isrc: null,
            title: "Raha",
            artist: "Arrow Bwoy",
            album: "Focus",
            durationMs: 173000,
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveTrackAsync_RejectsMetadataMatchWhenDurationIsMissing()
    {
        var service = new StubQobuzMetadataService();
        service.SearchHandler = _ => new List<QobuzTrack>
        {
            new()
            {
                Id = 88112233,
                Title = "Raha",
                Duration = 0,
                Performer = new QobuzArtist { Name = "Arrow Bwoy" },
                Album = new QobuzAlbum { Title = "Focus" }
            }
        };
        var resolver = CreateResolver(service);

        var result = await resolver.ResolveTrackAsync(
            isrc: null,
            title: "Raha",
            artist: "Arrow Bwoy",
            album: "Focus",
            durationMs: 173000,
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveTrackAsync_RejectsMetadataMatchWhenArtistOnlyContainsExpected()
    {
        var service = new StubQobuzMetadataService();
        service.SearchHandler = _ => new List<QobuzTrack>
        {
            new()
            {
                Id = 99112234,
                Title = "Raha",
                Duration = 173,
                Performer = new QobuzArtist { Name = "Arrow Bwoy Tribute" },
                Album = new QobuzAlbum { Title = "Focus" }
            }
        };
        var resolver = CreateResolver(service);

        var result = await resolver.ResolveTrackAsync(
            isrc: null,
            title: "Raha",
            artist: "Arrow Bwoy",
            album: "Focus",
            durationMs: 173000,
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveTrackAsync_RejectsUnrequestedVariantWithoutIsrc()
    {
        var service = new StubQobuzMetadataService();
        service.SearchHandler = _ => new List<QobuzTrack>
        {
            new()
            {
                Id = 99112235,
                Title = "Raha (Acapella)",
                Duration = 173,
                Performer = new QobuzArtist { Name = "Arrow Bwoy" },
                Album = new QobuzAlbum { Title = "Focus" }
            }
        };
        var resolver = CreateResolver(service);

        var result = await resolver.ResolveTrackAsync(
            isrc: null,
            title: "Raha",
            artist: "Arrow Bwoy",
            album: "Focus",
            durationMs: 173000,
            CancellationToken.None);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(
        "USJZ10900031",
        "Empire State Of Mind",
        "JAŸ-Z",
        "Jay Z",
        "The Blueprint 3",
        277000,
        20654859)]
    [InlineData(
        "USUM71312049",
        "23",
        "Mike WiLL Made-It",
        "Mike Will Made It",
        "23",
        252000,
        11411080)]
    public async Task ResolveTrackAsync_AcceptsExactIsrcCandidateWhenArtistIdentityDiffersByService(
        string isrc,
        string title,
        string sourceArtist,
        string qobuzArtist,
        string album,
        int durationMs,
        int qobuzTrackId)
    {
        var service = new StubQobuzMetadataService();
        service.SearchHandler = _ => new List<QobuzTrack>
        {
            new()
            {
                Id = qobuzTrackId,
                ISRC = isrc,
                Title = title,
                Duration = (int)Math.Round(durationMs / 1000d),
                Performer = new QobuzArtist { Name = qobuzArtist },
                Album = new QobuzAlbum { Title = album }
            }
        };
        var resolver = CreateResolver(service);

        var result = await resolver.ResolveTrackAsync(
            isrc,
            title,
            sourceArtist,
            album,
            durationMs,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(qobuzTrackId, result!.Track.Id);
    }

    [Fact]
    public async Task ResolveTrackAsync_CleansSourceReleaseTypeSuffixBeforeAlbumLookup()
    {
        var service = new StubQobuzMetadataService();
        service.AlbumSearchHandler = query => string.Equals(query, "Aminia Nyashinski", StringComparison.OrdinalIgnoreCase)
            ? new List<QobuzTrack>
            {
                new()
                {
                    Id = 370472406,
                    Title = "Aminia",
                    Duration = 239,
                    Performer = new QobuzArtist { Name = "Nyashinski" },
                    Album = new QobuzAlbum { Title = "Aminia" }
                }
            }
            : new List<QobuzTrack>();
        var resolver = CreateResolver(service);

        var result = await resolver.ResolveTrackAsync(
            isrc: "ZA41S1733415",
            title: "Aminia",
            artist: "Nyashinski",
            album: "Aminia - Single",
            durationMs: 239000,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(370472406, result!.Track.Id);
        Assert.Contains("Aminia Nyashinski", service.Queries);
    }

    [Fact]
    public async Task ValidateTrackIdAsync_RejectsExactIsrcWhenMetadataContradictsRequestedTrack()
    {
        var service = new StubQobuzMetadataService
        {
            TrackResult = new QobuzTrack
            {
                Id = 370472406,
                ISRC = "ZA41S1733415",
                Title = "Wrong Song",
                Duration = 120,
                Performer = new QobuzArtist { Name = "Wrong Artist" },
                Album = new QobuzAlbum { Title = "Wrong Album" }
            }
        };
        var resolver = CreateResolver(service);
        Assert.True(QobuzTrackId.TryCreate(370472406, out var trackId));

        var result = await resolver.ValidateTrackIdAsync(
            trackId,
            "ZA41S1733415",
            "Aminia",
            "Nyashinski",
            "Aminia",
            239000,
            CancellationToken.None);

        Assert.Null(result);
    }

    private static QobuzTrackResolver CreateResolver(StubQobuzMetadataService metadataService)
    {
        return new QobuzTrackResolver(
            metadataService,
            Options.Create(new QobuzApiConfig
            {
                DefaultStore = "us-en",
                PreferredStores = new List<string> { "us-en" }
            }),
            NullLogger<QobuzTrackResolver>.Instance);
    }

    private sealed class StubQobuzMetadataService : IQobuzMetadataService
    {
        public Exception? IsrcFailure { get; init; }
        public Task<QobuzTrack?>? PendingIsrc { get; init; }
        public QobuzTrack? IsrcResult { get; init; }
        public QobuzTrack? TrackResult { get; init; }
        public Func<string, List<QobuzTrack>> SearchHandler { get; set; } = _ => new List<QobuzTrack>();
        public Func<string, List<QobuzTrack>> AlbumSearchHandler { get; set; } = _ => new List<QobuzTrack>();
        public List<string> Queries { get; } = new();

        public Task<QobuzTrack?> FindTrackByISRC(string isrc, CancellationToken ct)
        {
            if (PendingIsrc is not null) return PendingIsrc.WaitAsync(ct);
            return IsrcFailure == null ? Task.FromResult(IsrcResult) : Task.FromException<QobuzTrack?>(IsrcFailure);
        }

        public Task<QobuzAlbum?> FindAlbumByUPC(string upc, CancellationToken ct)
        {
            return Task.FromResult<QobuzAlbum?>(null);
        }

        public Task<QobuzArtist?> FindArtistByName(string name, CancellationToken ct)
        {
            return Task.FromResult<QobuzArtist?>(null);
        }

        public Task<List<QobuzTrack>> SearchTracks(string query, CancellationToken ct)
        {
            Queries.Add(query);
            return Task.FromResult(SearchHandler(query));
        }

        public Task<List<QobuzTrack>> SearchAlbumTracks(string query, CancellationToken ct)
        {
            Queries.Add($"album:{query}");
            return Task.FromResult(AlbumSearchHandler(query));
        }

        public Task<List<QobuzTrack>> SearchTracksAutosuggest(string query, string? store, CancellationToken ct)
        {
            Queries.Add($"{store}:{query}");
            return Task.FromResult(new List<QobuzTrack>());
        }

        public Task<List<QobuzAlbum>> SearchAlbums(string query, CancellationToken ct)
        {
            return Task.FromResult(new List<QobuzAlbum>());
        }

        public Task<List<QobuzArtist>> SearchArtists(string query, CancellationToken ct)
        {
            return Task.FromResult(new List<QobuzArtist>());
        }

        public Task<QobuzArtist?> GetArtistDiscography(int artistId, string store, CancellationToken ct)
        {
            return Task.FromResult<QobuzArtist?>(null);
        }

        public Task<List<QobuzAlbum>> GetArtistAlbums(int artistId, string store, CancellationToken ct)
        {
            return Task.FromResult(new List<QobuzAlbum>());
        }

        public Task<QobuzTrack?> GetTrack(int trackId, CancellationToken ct)
        {
            return Task.FromResult(TrackResult);
        }

    }
}
