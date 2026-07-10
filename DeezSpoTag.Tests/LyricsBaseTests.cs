using DeezSpoTag.Core.Models;
using System.Text.Json;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LyricsBaseTests
{
    [Fact]
    public void IsSynced_ReturnsTrue_WhenSingleValidLineExists()
    {
        var lyrics = new LyricsSource
        {
            SyncedLyrics =
            [
                new SynchronizedLyric("Only line", "[00:01.00]", 1000)
            ]
        };

        Assert.True(lyrics.IsSynced());
    }

    [Fact]
    public void GenerateLrcContent_EmitsSingleValidLine()
    {
        var lyrics = new LyricsSource
        {
            SyncedLyrics =
            [
                new SynchronizedLyric("Only line", "[00:01.00]", 1000)
            ]
        };

        var lrc = lyrics.GenerateLrcContent();

        Assert.Contains("[00:01.00]Only line", lrc);
    }

    [Fact]
    public void CanSaveLrcSidecar_AllowsProviderSyncedJson()
    {
        var lyrics = new LyricsSource
        {
            SyncedLyrics =
            [
                new SynchronizedLyric("Only line", "[00:01.00]", 1000)
            ],
            SyncedLyricsSourceFormat = LyricsSourceFormat.ProviderSyncedJson
        };

        Assert.True(lyrics.CanSaveLrcSidecar());
    }

    [Fact]
    public void LyricsNew_DoesNotInventTimestamp_WhenProviderJsonHasNoTiming()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "data": {
                "track": {
                  "lyrics": {
                    "synchronizedLines": [
                      { "line": "No timing" }
                    ]
                  }
                }
              }
            }
            """);

        var lyrics = new LyricsNew(document.RootElement);

        Assert.Empty(lyrics.SyncedLyrics!);
        Assert.False(lyrics.CanSaveLrcSidecar());
    }

    [Fact]
    public void LyricsNew_UsesProviderMilliseconds_WhenExplicitlyPresent()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "data": {
                "track": {
                  "lyrics": {
                    "synchronizedLines": [
                      { "line": "Timed", "milliseconds": 1234 }
                    ]
                  }
                }
              }
            }
            """);

        var lyrics = new LyricsNew(document.RootElement);

        Assert.True(lyrics.CanSaveLrcSidecar());
        Assert.Equal("[00:01.23]", lyrics.SyncedLyrics![0].LrcTimestamp);
    }
}
