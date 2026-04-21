using DeezSpoTag.Core.Models;
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
}
