using DeezSpoTag.Services.Matching;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TrackCandidateValidatorTests
{
    [Fact]
    public void Validate_AcceptsExactIsrc_WhenMetadataIsCompatible()
    {
        var result = TrackCandidateValidator.Validate(
            new TrackMatchSource("USAT22102123", "Essence", "Wizkid", "Made In Lagos", 248000),
            new TrackMatchCandidate("tidal-1", "USAT22102123", "Essence", "Wizkid", "Made In Lagos", 249000));

        Assert.True(result.Accepted);
        Assert.Equal("isrc", result.Reason);
    }

    [Fact]
    public void Validate_AcceptsExactIsrc_WhenOnlyAlbumDiffers()
    {
        var result = TrackCandidateValidator.Validate(
            new TrackMatchSource("USAT22102123", "Essence", "Wizkid", "Made In Lagos", 248000),
            new TrackMatchCandidate("tidal-1", "USAT22102123", "Essence", "Wizkid", "Wrong Album", 249000));

        Assert.True(result.Accepted);
        Assert.Equal("isrc", result.Reason);
    }

    [Fact]
    public void Validate_RejectsNoIsrcCandidate_WhenDurationDiffers()
    {
        var result = TrackCandidateValidator.Validate(
            new TrackMatchSource(null, "Essence", "Wizkid", "Made In Lagos", 248000),
            new TrackMatchCandidate("qobuz-1", null, "Essence", "Wizkid", "Made In Lagos", 220000));

        Assert.False(result.Accepted);
        Assert.Equal("duration_mismatch", result.Reason);
    }

    [Theory]
    [InlineData("Essence - Remix")]
    [InlineData("Essence (Acapella)")]
    [InlineData("Essence (As Made Famous By Wizkid)")]
    public void Validate_RejectsUnrequestedVariants(string candidateTitle)
    {
        var result = TrackCandidateValidator.Validate(
            new TrackMatchSource(null, "Essence", "Wizkid", "Made In Lagos", 248000),
            new TrackMatchCandidate("candidate-1", null, candidateTitle, "Wizkid", "Made In Lagos", 248000));

        Assert.False(result.Accepted);
        Assert.Equal("title_mismatch", result.Reason);
    }

    [Fact]
    public void Validate_AllowsRequestedVariant_WhenCandidateMatchesSameVariant()
    {
        var result = TrackCandidateValidator.Validate(
            new TrackMatchSource(null, "Essence - Remix", "Wizkid", "Made In Lagos", 248000),
            new TrackMatchCandidate("candidate-1", null, "Essence (Remix)", "Wizkid", "Made In Lagos", 248000));

        Assert.True(result.Accepted);
    }

    [Fact]
    public void Validate_RejectsNoIsrcCandidate_WhenArtistIsOnlyLooseContainment()
    {
        var result = TrackCandidateValidator.Validate(
            new TrackMatchSource(null, "Raha", "Arrow Bwoy", "Focus", 173000),
            new TrackMatchCandidate("qobuz-1", null, "Raha", "Arrow Bwoy Tribute", "Focus", 173000));

        Assert.False(result.Accepted);
        Assert.Equal("artist_mismatch", result.Reason);
    }

    [Fact]
    public void Validate_AllowsStereoCatalog_WhenAtmosIsrcDiffers()
    {
        var result = TrackCandidateValidator.Validate(
            new TrackMatchSource("ATMOSISRC001", "No Brainer", "DJ Khaled", "Father Of Asahd", 260000),
            new TrackMatchCandidate(
                "spotify-stereo",
                "USUM71806679",
                "No Brainer",
                "DJ Khaled",
                "Father Of Asahd",
                260000),
            new TrackCandidateValidationOptions(AllowIsrcMismatch: true));

        Assert.True(result.Accepted);
        Assert.Equal("metadata", result.Reason);
    }

    [Fact]
    public void Validate_RejectsExactIsrc_WhenTitleDriftsToRemix()
    {
        var result = TrackCandidateValidator.Validate(
            new TrackMatchSource("USUG12001949", "Save Your Tears", "The Weeknd", "After Hours", 215000),
            new TrackMatchCandidate(
                "bpm-remix",
                "USUG12001949",
                "Save Your Tears (Remix) (feat. Ariana Grande)",
                "The Weeknd",
                "After Hours",
                191000));

        Assert.False(result.Accepted);
        Assert.Equal("version_drift", result.Reason);
    }

    [Fact]
    public void Validate_RejectsExactIsrc_WhenTitleDriftsToInstrumental()
    {
        var result = TrackCandidateValidator.Validate(
            new TrackMatchSource("USUG12001949", "Save Your Tears", "The Weeknd", "After Hours", 215000),
            new TrackMatchCandidate(
                "instrumental-1",
                "USUG12001949",
                "Save Your Tears (Instrumental)",
                "The Weeknd",
                "After Hours",
                215000));

        Assert.False(result.Accepted);
        Assert.Equal("version_drift", result.Reason);
    }

    [Fact]
    public void Validate_RejectsWrongTidalIdentity_WhenRequestedTrackHasIsrc()
    {
        var result = TrackCandidateValidator.Validate(
            new TrackMatchSource("QZPYN2109553", "Fatuma", "Ethic Entertainment", "Fatuma", 232000),
            new TrackMatchCandidate(
                "424139114",
                null,
                "Ethical Carbon Neutral",
                "adatch Entertainment Team",
                null,
                null),
            new TrackCandidateValidationOptions(
                StrictWithoutIsrc: true,
                AllowMissingCandidateArtist: false,
                RequireCandidateDurationWhenSourceHasDuration: true,
                MaxIsrcDurationDifferenceMs: 20_000,
                MaxMetadataDurationDifferenceMs: 3_000));

        Assert.False(result.Accepted);
        Assert.Equal("title_mismatch", result.Reason);
    }
}
