using System.Text.Json;
using DeezSpoTag.Services.Download.Tidal;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistArtworkIdentityPayloadTests
{
    [Fact]
    public void EngineQueuePayload_PersistsArtistIdentitiesAndArtworkProvenance()
    {
        var item = new TidalQueueItem
        {
            AppleArtistId = "apple-artist",
            DeezerArtistId = "12345",
            SpotifyArtistId = "spotify-artist",
            ArtistArtworkProvider = "spotify",
            ArtistArtworkSourceUrl = "https://i.scdn.co/image/square",
            ArtistArtworkResolutionMethod = "artist-id",
            ArtistArtworkWidth = 640,
            ArtistArtworkHeight = 640,
            ArtistArtworkExistingRetained = true
        };

        var json = JsonSerializer.Serialize(item.ToQueuePayload());
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("apple-artist", root.GetProperty("appleArtistId").GetString());
        Assert.Equal("12345", root.GetProperty("deezerArtistId").GetString());
        Assert.Equal("spotify-artist", root.GetProperty("spotifyArtistId").GetString());
        Assert.Equal("spotify", root.GetProperty("artistArtworkProvider").GetString());
        Assert.Equal("https://i.scdn.co/image/square", root.GetProperty("artistArtworkSourceUrl").GetString());
        Assert.Equal("artist-id", root.GetProperty("artistArtworkResolutionMethod").GetString());
        Assert.Equal(640, root.GetProperty("artistArtworkWidth").GetInt32());
        Assert.Equal(640, root.GetProperty("artistArtworkHeight").GetInt32());
        Assert.True(root.GetProperty("artistArtworkExistingRetained").GetBoolean());
    }
}
