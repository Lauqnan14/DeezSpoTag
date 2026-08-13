using DeezSpoTag.Core.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TrackIdentityTrustTests
{
    [Theory]
    [InlineData("/library/Music/Karun/01 - 01 - 01 - SLIDE.flac", true)]
    [InlineData("01 - 01 - Dai Dai.flac", true)]
    [InlineData("00 - 01 - Ahere.m4a", true)]
    [InlineData("/library/Music/Karun/Catch A Vibe/01 - Catch A Vibe.flac", false)]
    [InlineData("Kiss Me.flac", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void RepeatedNumericFilenamePrefix_MatchesCorruptLibraryNames(string? path, bool expected)
    {
        Assert.Equal(expected, TrackIdentityTrust.HasRepeatedNumericFilenamePrefix(path));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [InlineData("unknown", true)]
    [InlineData("Unknown Artist", true)]
    [InlineData("untitled", true)]
    [InlineData("track", true)]
    [InlineData("audio", true)]
    [InlineData("official audio", true)]
    [InlineData("a", true)]
    [InlineData("SLIDE", false)]
    [InlineData("Karun", false)]
    public void WeakMetadata_CoversLibraryAndRunnerTokens(string? value, bool expected)
    {
        Assert.Equal(expected, TrackIdentityTrust.IsWeakMetadataValue(value));
    }

    [Fact]
    public void UntrustedIdentity_WhenPrefixOrWeakTags()
    {
        Assert.True(TrackIdentityTrust.IsUntrustedIdentity(
            "SLIDE",
            "Karun",
            "/library/Music/01 - 01 - 01 - SLIDE.flac"));
        Assert.True(TrackIdentityTrust.IsUntrustedIdentity("unknown", "Karun", "song.flac"));
        Assert.True(TrackIdentityTrust.IsUntrustedIdentity("SLIDE", null, "song.flac"));
        Assert.False(TrackIdentityTrust.IsUntrustedIdentity("SLIDE", "Karun", "/library/Music/Karun/SLIDE.flac"));
    }
}
