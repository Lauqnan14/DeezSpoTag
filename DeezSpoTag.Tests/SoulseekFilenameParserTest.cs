using System;
using DeezSpoTag.Services.Download.Soulseek;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     What a peer's filename is taken to say about a track.
/// </summary>
/// <remarks>
///     The parser is the only identity a Soulseek candidate has, so everything downstream depends on it: the
///     matcher reads these fields to accept or reject a file, the search tab shows them, and a queued download
///     is tagged with them. A name read the wrong way round is therefore not a cosmetic problem - it files the
///     track under the wrong artist.
/// </remarks>
public sealed class SoulseekFilenameParserTest
{
    /// <summary>
    ///     The four-part shape: artist, album, track number, title.
    /// </summary>
    /// <remarks>
    ///     This is the shape a ripper produces when it has the tag it wants to write, and it is what the observed
    ///     failure looked like. The three-part reader took it as "artist, title, album" and so read the album as
    ///     the title and the track number as part of the album - which the matcher then rejected, because no
    ///     track on the network is called "04 - Isabella".
    /// </remarks>
    [Theory]
    [InlineData("Sauti Sol - Live and Die in Afrika - 04 - Isabella.flac", 4)]
    [InlineData("Sauti Sol_Live and Die in Afrika_04_Isabella.flac", 4)]
    [InlineData("Sauti Sol - Live and Die in Afrika - 004 - Isabella.flac", 4)]
    [InlineData("Sauti Sol - Live and Die in Afrika - 12 - Isabella.mp3", 12)]
    public void TheArtistAlbumTrackAndTitleAreAllReadFromTheFourPartShape(
        string filename,
        int expectedTrack)
    {
        var facts = SoulseekFilenameParser.Parse(filename);

        Assert.Equal("Isabella", facts.Title);
        Assert.Equal("Sauti Sol", facts.Artist);
        Assert.Equal("Live and Die in Afrika", facts.Album);
        Assert.Equal(expectedTrack, facts.TrackNumber);
    }

    [Theory]
    [InlineData("Sauti Sol - Music From The Mau Mau - 07 - African Air.flac", "Music From The Mau Mau", "African Air")]
    [InlineData("Sauti Sol - Excess - 02 - Kilio Kilio.mp3", "Excess", "Kilio Kilio")]
    public void AMultiWordAlbumAndTitleSurviveTheSplit(string filename, string expectedAlbum, string expectedTitle)
    {
        var facts = SoulseekFilenameParser.Parse(filename);

        Assert.Equal(expectedAlbum, facts.Album);
        Assert.Equal(expectedTitle, facts.Title);
        Assert.Equal("Sauti Sol", facts.Artist);
    }

    [Theory]
    [InlineData("04 - Isabella.mp3", "Isabella")]
    [InlineData("Sauti Sol - Isabella.mp3", "Isabella")]
    [InlineData("Sauti Sol - Isabella - Live and Die in Afrika.mp3", "Isabella")]
    public void TheShorterShapesStillParse(string filename, string expectedTitle)
    {
        var facts = SoulseekFilenameParser.Parse(filename);

        Assert.Equal(expectedTitle, facts.Title);
    }

    [Fact]
    public void AnUnderscoreTitleIsNotReadAsALayoutWhenItIsNotOne()
    {
        // A real, tagged file that happens to use underscores is the common case. Treating every underscore as
        // a separator would split "W_commercially_Free" into three parts, and a four-part reader would then read
        // a track number out of a word.
        var facts = SoulseekFilenameParser.Parse("Sauti Sol - W_commercially_Free.mp3");

        Assert.Equal("Sauti Sol", facts.Artist);
        Assert.Contains("W_commercially", facts.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDirectoryNameDoesNotReplaceWhatTheFileNameSays()
    {
        var facts = SoulseekFilenameParser.Parse(@"@@peer\Live and Die in Afrika\04 Isabella.mp3");

        // The album in the peer's folder is not evidence about the file, so the leaf name is all that is read.
        Assert.Null(facts.Artist);
        Assert.Null(facts.Album);
    }

    [Fact]
    public void ALeadingNumberSeparatedOnlyBySpaceIsLeftInTheTitle()
    {
        // "04 - Isabella" and "04.Isabella" both lose the number, because a separator says it is one. A bare
        // space does not, and stripping it anyway would turn "2 Live Crew" into "Live Crew" and lose the file.
        // The trade-off is deliberate: a file that is really a title beginning with a number is a rarity, and
        // stripping it costs a candidate that could have matched.
        var facts = SoulseekFilenameParser.Parse("04 Isabella.mp3");

        Assert.Equal("04 Isabella", facts.Title);
    }

    [Theory]
    // The common shape.
    [InlineData("Boards of Canada - Roygbiv.flac", "Roygbiv", "Boards of Canada", null)]
    // A scene release's double numbering: "01•01" is a track number, not an artist called "01".
    [InlineData("01•01 - Boards Of Canada - Kid For Today.flac", "Kid For Today", "Boards Of Canada", null)]
    [InlineData("01.02 - Boards Of Canada - Kid For Today.flac", "Kid For Today", "Boards Of Canada", null)]
    [InlineData("01 - 02 - Boards Of Canada - Kid For Today.flac", "Kid For Today", "Boards Of Canada", null)]
    // The three-part shape still reads as artist, title, album.
    [InlineData("Roygbiv - Boards of Canada - Music Has the Right to Children.mp3", "Boards of Canada", "Roygbiv", "Music Has the Right to Children")]
    public void TheArtistAndTheTitleAreReadInTheRightOrder(
        string filename,
        string expectedTitle,
        string expectedArtist,
        string? expectedAlbum)
    {
        var facts = SoulseekFilenameParser.Parse(filename);

        Assert.Equal(expectedTitle, facts.Title);
        Assert.Equal(expectedArtist, facts.Artist);
        Assert.Equal(expectedAlbum, facts.Album);
    }

    [Theory]
    [InlineData("Portishead • Glory Box.flac")]
    [InlineData("Portishead · Glory Box.flac")]
    [InlineData("Portishead – Glory Box.flac")]
    [InlineData("Portishead — Glory Box.flac")]
    public void ASceneReleaseSeparatorIsAnArtistTitleSeparatorToo(string filename)
    {
        // "Portishead • Glory Box" read as one long title has no artist at all, so the track cannot be filed
        // or matched. The separators sit in the same position as the hyphen and mean the same thing.
        var facts = SoulseekFilenameParser.Parse(filename);

        Assert.Equal("Glory Box", facts.Title);
        Assert.Equal("Portishead", facts.Artist);
    }

    [Fact]
    public void TheTrackNumberSurvivesTheDoubleNumberingForm()
    {
        Assert.Equal(1, SoulseekFilenameParser.Parse("01•01 - Boards Of Canada - Kid For Today.flac").TrackNumber);
        Assert.Equal(2, SoulseekFilenameParser.Parse("02 - Roygbiv.flac").TrackNumber);
    }

    [Fact]
    public void AVersionTagStaysInTheTitleSoVersionDriftIsStillVisible()
    {
        // Folding separators must not swallow the bracket handling: a version marker belongs in the title,
        // because that is what makes the shared matcher refuse a live take in place of the studio master.
        var facts = SoulseekFilenameParser.Parse("Boards of Canada - Roygbiv (Live).flac");

        Assert.Contains("Live", facts.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANameWithNoSeparatorIsAllTitleAndNoArtist()
    {
        var facts = SoulseekFilenameParser.Parse("Roygbiv.flac");

        Assert.Equal("Roygbiv", facts.Title);
        Assert.Null(facts.Artist);
    }
}
