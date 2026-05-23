using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Web.Controllers.Api;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyIdResolverIdentityTests
{
    [Fact]
    public void ResolveTrackIdFromItems_WithIsrcRequiresIsrcMatch()
    {
        using var doc = JsonDocument.Parse("""
            {
              "tracks": {
                "items": [
                  {
                    "id": "wrong-spotify-id",
                    "name": "Nataka",
                    "external_ids": { "isrc": "QZHZ32654396" },
                    "artists": [{ "name": "Lilmaina" }]
                  },
                  {
                    "id": "strong-title-artist-but-unverified",
                    "name": "Nataka",
                    "artists": [{ "name": "Stereo Singasinga" }]
                  }
                ]
              }
            }
            """);

        var items = doc.RootElement.GetProperty("tracks").GetProperty("items");
        var result = InvokeResolveTrackIdFromItems(items, "nataka", "stereosingasinga", "TZA1X2200742");

        Assert.Null(result);
    }

    [Fact]
    public void ResolveTrackIdFromItems_WithoutIsrcAllowsStrongTitleArtistMatch()
    {
        using var doc = JsonDocument.Parse("""
            {
              "tracks": {
                "items": [
                  {
                    "id": "wrong-artist",
                    "name": "Nataka",
                    "artists": [{ "name": "Lilmaina" }]
                  },
                  {
                    "id": "expected-spotify-id",
                    "name": "Nataka",
                    "artists": [{ "name": "Stereo Singasinga" }]
                  }
                ]
              }
            }
            """);

        var items = doc.RootElement.GetProperty("tracks").GetProperty("items");
        var result = InvokeResolveTrackIdFromItems(items, "nataka", "stereosingasinga", null);

        Assert.Equal("expected-spotify-id", result);
    }

    [Theory]
    [InlineData("https://play.qobuz.com/track/166186694", "166186694")]
    [InlineData("https://open.qobuz.com/track/166186694", "166186694")]
    [InlineData("https://www.qobuz.com/us-en/track/166186694", "166186694")]
    public void QobuzDownloadController_ExtractsTrackIdFromDirectQobuzUrl(string url, string expected)
    {
        var method = typeof(QobuzDownloadApiController).GetMethod(
            "TryExtractQobuzTrackId",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var result = method!.Invoke(null, [url]);

        Assert.Equal(expected, result);
    }

    private static string? InvokeResolveTrackIdFromItems(JsonElement items, string normalizedTitle, string normalizedArtist, string? isrc)
    {
        var method = typeof(SpotifyIdResolver).GetMethod(
            "ResolveTrackIdFromItems",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        return method!.Invoke(null, [items, normalizedTitle, normalizedArtist, isrc]) as string;
    }
}
