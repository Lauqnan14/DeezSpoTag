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

public sealed class QobuzTrackResolverTests
{
    [Fact]
    public async Task ResolveTrackUrlAsync_AcceptsExactIsrcWithoutTitleArtist()
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

        var result = await resolver.ResolveTrackUrlAsync(
            "GBDUW0000059",
            title: null,
            artist: null,
            album: null,
            durationMs: null,
            CancellationToken.None);

        Assert.Equal("https://play.qobuz.com/track/411245095", result);
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
        public QobuzTrack? IsrcResult { get; init; }
        public Func<string, List<QobuzTrack>> SearchHandler { get; set; } = _ => new List<QobuzTrack>();
        public Func<string, List<QobuzTrack>> AlbumSearchHandler { get; set; } = _ => new List<QobuzTrack>();
        public List<string> Queries { get; } = new();

        public Task<QobuzTrack?> FindTrackByISRC(string isrc, CancellationToken ct)
        {
            return Task.FromResult(IsrcResult);
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
            return Task.FromResult<QobuzTrack?>(null);
        }

        public Task<QobuzQualityInfo?> GetTrackQuality(int trackId, CancellationToken ct)
        {
            return Task.FromResult<QobuzQualityInfo?>(null);
        }
    }
}
