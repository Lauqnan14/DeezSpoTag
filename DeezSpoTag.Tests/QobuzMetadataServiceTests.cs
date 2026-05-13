using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Services.Metadata.Qobuz;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class QobuzMetadataServiceTests
{
    [Fact]
    public async Task SearchTracksAutosuggest_ParsesAlbumAndArtistMetadata()
    {
        var apiClient = new StubQobuzApiClient
        {
            AutosuggestResponse = BuildAutosuggestResponse()
        };
        var options = Options.Create(new QobuzApiConfig { DefaultStore = "us-en" });
        var service = new QobuzMetadataService(
            apiClient,
            new QobuzArtistService(apiClient, new MemoryCache(new MemoryCacheOptions()), options),
            options);

        var tracks = await service.SearchTracksAutosuggest("Daft Punk Discovery", "us-en", CancellationToken.None);

        var track = Assert.Single(tracks);
        Assert.Equal(99112233, track.Id);
        Assert.Equal("Harder, Better, Faster, Stronger", track.Title);
        Assert.Equal("GBDUW0000059", track.ISRC);
        Assert.Equal(224, track.Duration);
        Assert.Equal(101, track.Performer?.Id);
        Assert.Equal("Daft Punk", track.Performer?.Name);
        Assert.Equal("alb-1", track.Album?.Id);
        Assert.Equal("Discovery", track.Album?.Title);
        Assert.Equal(101, track.Album?.Artists[0].Id);
        Assert.Equal("Daft Punk", track.Album?.Artists[0].Name);
    }

    [Fact]
    public async Task SearchTracksAutosuggest_ParsesArrayShape()
    {
        var apiClient = new StubQobuzApiClient
        {
            AutosuggestResponse = BuildArrayAutosuggestResponse()
        };
        var options = Options.Create(new QobuzApiConfig { DefaultStore = "us-en" });
        var service = new QobuzMetadataService(
            apiClient,
            new QobuzArtistService(apiClient, new MemoryCache(new MemoryCacheOptions()), options),
            options);

        var tracks = await service.SearchTracksAutosuggest("False 9 Freestyle Breeder LW", "us-en", CancellationToken.None);

        var track = Assert.Single(tracks);
        Assert.Equal(388061043, track.Id);
        Assert.Equal("False 9 Freestyle", track.Title);
        Assert.Equal("Breeder LW", track.Performer?.Name);
        Assert.Equal("False 9 Freestyle", track.Album?.Title);
    }

    [Fact]
    public async Task SearchTracks_AddsSingleTrackAlbumFromCatalogSearch()
    {
        var apiClient = new StubQobuzApiClient
        {
            CatalogResponse = new QobuzCatalogSearchResponse
            {
                Albums = new QobuzSearchList<QobuzAlbum>
                {
                    Items =
                    [
                        new QobuzAlbum
                        {
                            Id = "b2qy6awwmy7kh",
                            QobuzId = 388061042,
                            Title = "False 9 Freestyle",
                            Duration = 216,
                            TracksCount = 1,
                            Url = "https://www.qobuz.com/us-en/album/false-9-freestyle-breeder-lw/b2qy6awwmy7kh",
                            Artists = [new QobuzArtist { Id = 3984203, Name = "Breeder LW" }]
                        }
                    ]
                }
            },
            AlbumPageTrackIds = [388061043]
        };
        var options = Options.Create(new QobuzApiConfig { DefaultStore = "us-en" });
        var service = new QobuzMetadataService(
            apiClient,
            new QobuzArtistService(apiClient, new MemoryCache(new MemoryCacheOptions()), options),
            options);

        var tracks = await service.SearchTracks("False 9 Freestyle Breeder LW", CancellationToken.None);

        var track = Assert.Single(tracks);
        Assert.Equal(388061043, track.Id);
        Assert.Equal("False 9 Freestyle", track.Title);
        Assert.Equal("Breeder LW", track.Performer?.Name);
        Assert.Equal(216, track.Duration);
    }

    private static QobuzAutosuggestResponse BuildAutosuggestResponse()
    {
        using var document = JsonDocument.Parse("""
{
  "query": "Daft Punk Discovery",
  "tracks": {
    "items": [
      {
        "id": 99112233,
        "title": "Harder, Better, Faster, Stronger",
        "duration": 224,
        "isrc": "GBDUW0000059",
        "maximum_bit_depth": 24,
        "maximum_sampling_rate": 96,
        "hires": true,
        "performer": { "id": 101, "name": "Daft Punk" },
        "album": {
          "id": "alb-1",
          "title": "Discovery",
          "maximum_bit_depth": 24,
          "maximum_sampling_rate": 96,
          "hires": true,
          "streamable": true,
          "downloadable": true,
          "purchasable": true,
          "artist": { "id": 101, "name": "Daft Punk" }
        }
      }
    ]
  }
}
""");
        return new QobuzAutosuggestResponse
        {
            Query = "Daft Punk Discovery",
            Tracks = document.RootElement.GetProperty("tracks").Clone()
        };
    }

    private static QobuzAutosuggestResponse BuildArrayAutosuggestResponse()
    {
        using var document = JsonDocument.Parse("""
{
  "query": "False 9 Freestyle Breeder LW",
  "tracks": [
    {
      "id": 388061043,
      "title": "False 9 Freestyle",
      "duration": 216,
      "artist": "Breeder LW",
      "album": "False 9 Freestyle"
    }
  ]
}
""");
        return new QobuzAutosuggestResponse
        {
            Query = "False 9 Freestyle Breeder LW",
            Tracks = document.RootElement.GetProperty("tracks").Clone()
        };
    }

    private sealed class StubQobuzApiClient : IQobuzApiClient
    {
        public QobuzAutosuggestResponse? AutosuggestResponse { get; init; }
        public QobuzCatalogSearchResponse? CatalogResponse { get; init; }
        public List<int> AlbumPageTrackIds { get; init; } = new();

        public Task<QobuzAutosuggestResponse?> SearchAutosuggestAsync(
            string store,
            string query,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(AutosuggestResponse);
        }

        public Task<QobuzCatalogSearchResponse?> SearchCatalogAsync(
            string query,
            int limit,
            int offset,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(CatalogResponse);
        }

        public Task<List<int>> GetAlbumPageTrackIdsAsync(string albumUrl, CancellationToken cancellationToken)
        {
            return Task.FromResult(AlbumPageTrackIds);
        }

        public Task<QobuzArtist?> GetArtistAsync(int artistId, string store, int offset, int limit, CancellationToken cancellationToken)
        {
            return Task.FromResult<QobuzArtist?>(null);
        }

        public Task<QobuzTrackSearchResponse?> SearchTracksAsync(
            string query,
            int limit,
            int offset,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<QobuzTrackSearchResponse?>(null);
        }

        public Task<QobuzAlbumSearchResponse?> SearchAlbumsAsync(
            string query,
            int limit,
            int offset,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<QobuzAlbumSearchResponse?>(null);
        }

        public Task<QobuzArtistSearchResponse?> SearchArtistsAsync(
            string query,
            int limit,
            int offset,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<QobuzArtistSearchResponse?>(null);
        }

        public Task<QobuzTrack?> GetTrackAsync(int trackId, CancellationToken cancellationToken)
        {
            return Task.FromResult<QobuzTrack?>(null);
        }
    }
}
