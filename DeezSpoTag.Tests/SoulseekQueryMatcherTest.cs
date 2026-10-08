using DeezSpoTag.Services.Download.Soulseek;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     The free-text query grade behind manual Soulseek searches.
/// </summary>
/// <remarks>
///     <para>
///         A manual search is a peer query: whatever the user typed may name an artist, a track or an album.
///         Grading it against the whole identity recovered from a filename is what stopped a search for an
///         artist from rejecting every file that artist had.
///     </para>
///     <para>
///         These are pure, so they assert the ladder rather than the whole scorer. The scorer's use of the
///         grade, and the fact that automated matching does not use it at all, are covered by
///         <see cref="SoulseekCandidateScoringTest" /> and <see cref="SoulseekStrictAutomationMatchingTest" />.
///     </para>
/// </remarks>
public sealed class SoulseekQueryMatcherTest
{
    [Fact]
    public void AQueryThatIsTheArtistGradesAsAnExactMatch()
    {
        // The bug this exists for: "mejja" was read as a title, so this file came back as a title mismatch.
        var facts = SoulseekFilenameParser.Parse("Mejja - Thank Me Later - 12 - Cece.mp3");

        Assert.Equal(1.0, SoulseekQueryMatcher.Score("mejja", facts, "Mejja - Thank Me Later - 12 - Cece.mp3"), 4);
        Assert.Equal(1.0, SoulseekQueryMatcher.Score("Mejja", facts, "Mejja - Thank Me Later - 12 - Cece.mp3"), 4);
    }

    [Fact]
    public void CasePunctuationAndAccentsDoNotDecideAMatch()
    {
        var facts = SoulseekFilenameParser.Parse("Céline Dion - Pour que tu m'aimes encore.flac");

        Assert.Equal(1.0, SoulseekQueryMatcher.Score("celine dion", facts), 4);
        Assert.Equal(1.0, SoulseekQueryMatcher.Score("CELINE DION!", facts), 4);
        Assert.Equal(1.0, SoulseekQueryMatcher.Score(" Céline  Dion ", facts), 4);

        // An apostrophe is punctuation, so "m'aime" and "m'aimes" are not the same words. It still matches,
        // and it is graded below an exact one, which is what a partial word agreement deserves.
        var partial = SoulseekQueryMatcher.Score("pour que tu m'aime", facts);
        Assert.True(partial > 0, "An apostrophe must not turn a match into a rejection.");
        Assert.True(partial < 1.0, "A query that is not the whole name cannot be an exact match.");
    }

    [Fact]
    public void AQueryThatIsTheTrackGradesAsAnExactMatch()
    {
        var facts = SoulseekFilenameParser.Parse("Boards of Canada - Roygbiv.mp3");

        Assert.Equal(1.0, SoulseekQueryMatcher.Score("roygbiv", facts), 4);
    }

    [Fact]
    public void TheAlbumNameIsPartOfTheIdentityAQueryIsReadAgainst()
    {
        var facts = SoulseekFilenameParser.Parse("Roygbiv - Boards of Canada - Music Has the Right to Children.mp3");

        Assert.Equal(1.0, SoulseekQueryMatcher.Score("music has the right to children", facts), 4);
    }

    [Fact]
    public void AMultiWordQueryMatchesWhenTheNameIsAPartOfIt()
    {
        // "drake take care" names a track; the file's title is that track, the artist is the other word.
        var facts = SoulseekFilenameParser.Parse("Drake - Take Care.mp3");

        var score = SoulseekQueryMatcher.Score("drake take care", facts);

        Assert.True(score > 0, "A query naming the track and its artist must match the file.");
        Assert.True(score < 1.0, "A query longer than the name it names cannot be an exact match.");
    }

    [Fact]
    public void AQueryInsideALongerNameStillMatches()
    {
        var facts = SoulseekFilenameParser.Parse("Roygbiv (2012 Remaster).mp3");

        var score = SoulseekQueryMatcher.Score("roygbiv", facts);

        Assert.True(score > 0, "A track name inside a decorated name is still the track.");
    }

    [Fact]
    public void WordOrderDoesNotDecideAMatch()
    {
        var facts = SoulseekFilenameParser.Parse("Take Care.mp3");

        Assert.True(SoulseekQueryMatcher.Score("care take", facts) > 0);
    }

    [Fact]
    public void AQueryTheNameSaysNothingAboutScoresNothing()
    {
        var facts = SoulseekFilenameParser.Parse("Boards of Canada - Roygbiv.mp3");

        Assert.Equal(SoulseekQueryMatcher.NoMatch, SoulseekQueryMatcher.Score("kavinsky", facts), 4);
    }

    [Fact]
    public void HalfAWordIsNotAWord()
    {
        // Containment is per word on purpose. A fragment match is what makes a search return everything
        // remotely adjacent to the query, which is worse than returning nothing.
        var facts = SoulseekFilenameParser.Parse("Kavinsky - Nightcall.mp3");

        Assert.Equal(SoulseekQueryMatcher.NoMatch, SoulseekQueryMatcher.Score("kavin", facts, "Kavinsky - Nightcall.mp3"), 4);
        Assert.Equal(SoulseekQueryMatcher.NoMatch, SoulseekQueryMatcher.Score("night", facts, "Kavinsky - Nightcall.mp3"), 4);
    }

    [Fact]
    public void AnEmptyQueryMatchesNothing()
    {
        var facts = SoulseekFilenameParser.Parse("Boards of Canada - Roygbiv.mp3");

        Assert.Equal(SoulseekQueryMatcher.NoMatch, SoulseekQueryMatcher.Score("   ", facts), 4);
        Assert.Equal(SoulseekQueryMatcher.NoMatch, SoulseekQueryMatcher.Score(null, facts), 4);
    }

    [Fact]
    public void TheRejectionReasonIsSharedByTheScorerAndItsTests()
    {
        Assert.Equal("query_mismatch", SoulseekQueryMatcher.NoMatchReason);
    }

    [Fact]
    public void AFolderCarryingTheArtistAndAlbumIsPartOfTheIdentity()
    {
        // The layout that made an album read as one match in eleven. The parser reads the filename, and this
        // filename is "03 - Teardrop.flac", so it recovers a title and nothing else - but the folders around it
        // name the artist and the album outright. Rejecting this file rejected the whole release.
        const string path = @"@@akkia\Massive Attack - Mezzanine (20th Anniversary Deluxe Edition) (Remastered 2018) [24-88.2]-was95\CD 1\03 - Teardrop.flac";
        var facts = SoulseekFilenameParser.Parse(path);

        Assert.Equal("Teardrop", facts.Title);
        Assert.True(string.IsNullOrEmpty(facts.Artist), "the filename names no artist");
        Assert.True(string.IsNullOrEmpty(facts.Album), "the filename names no album");

        Assert.True(
            SoulseekQueryMatcher.Score("Massive Attack Mezzanine", facts, path) > SoulseekQueryMatcher.NoMatch,
            "a file inside a folder named for the artist and album is a match for a query naming both");
    }

    [Theory]
    [InlineData(@"Music\Massive Attack\Mezzanine\03 - Teardrop.flac")]
    [InlineData("Music/Massive Attack/Mezzanine/03 - Teardrop.flac")]
    [InlineData(@"\\server\share\Massive Attack - Mezzanine (Deluxe)\CD 2\07 - Exchange.flac")]
    public void FoldersAreReadWithEitherSeparatorAndAtAnyDepth(string path)
    {
        // A peer's share is authored on one operating system and read on another, so the separator it was
        // written with is not knowable, and an album folder is not always at a fixed depth.
        var facts = SoulseekFilenameParser.Parse(path);

        Assert.True(
            SoulseekQueryMatcher.Score("Massive Attack Mezzanine", facts, path) > SoulseekQueryMatcher.NoMatch,
            $"'{path}' names the artist and album in its folders and should match");
    }

    [Fact]
    public void EveryTrackOfAFolderPerAlbumReleaseMatches()
    {
        // The whole release, not one lucky file whose title happened to equal the album name.
        var paths = new[]
        {
            @"@@akkia\Massive Attack - Mezzanine (20th Anniversary Deluxe Edition) (Remastered 2018) [24-88.2]-was95\CD 1\01 - Angel.flac",
            @"@@akkia\Massive Attack - Mezzanine (20th Anniversary Deluxe Edition) (Remastered 2018) [24-88.2]-was95\CD 1\03 - Teardrop.flac",
            @"@@akkia\Massive Attack - Mezzanine (20th Anniversary Deluxe Edition) (Remastered 2018) [24-88.2]-was95\CD 1\05 - Exchange.flac",
            @"@@akkia\Massive Attack - Mezzanine (20th Anniversary Deluxe Edition) (Remastered 2018) [24-88.2]-was95\CD 1\10 - Group Four.flac",
            @"@@akkia\Massive Attack - Mezzanine (20th Anniversary Deluxe Edition) (Remastered 2018) [24-88.2]-was95\CD 2\02 - Angel (Angel Dust).flac"
        };

        foreach (var path in paths)
        {
            var facts = SoulseekFilenameParser.Parse(path);
            Assert.True(
                SoulseekQueryMatcher.Score("Massive Attack Mezzanine", facts, path) > SoulseekQueryMatcher.NoMatch,
                $"'{path}' should match the query that names its release");
        }
    }

    [Fact]
    public void AGenericFolderDoesNotMakeAnUnrelatedFileAMatch()
    {
        // The folders must not become a licence to match anything. "Music" and "Albums" are the folders of
        // very many shares and name nobody, so they cannot carry a match on their own.
        foreach (var folder in new[] { "Music", "Albums", "Downloads", "New", "CD", "FLAC" })
        {
            var path = $@"C:\Users\someone\Music\{folder}\Bonobo - Kerala.mp3";
            var facts = SoulseekFilenameParser.Parse(path);

            Assert.Equal(
                SoulseekQueryMatcher.NoMatch,
                SoulseekQueryMatcher.Score("massive attack mezzanine", facts, path),
                4);
        }
    }

    [Fact]
    public void AFolderEqualToTheQueryIsNotAnExactIdentity()
    {
        // A folder that happens to equal the query is good evidence but it is not a parsed artist or title,
        // so it must not claim the top grade that a recovered identity earns.
        const string path = @"Music\Massive Attack Mezzanine\01 - Angel.flac";
        var facts = SoulseekFilenameParser.Parse(path);

        var score = SoulseekQueryMatcher.Score("Massive Attack Mezzanine", facts, path);

        Assert.True(score > SoulseekQueryMatcher.NoMatch, "the folder still carries the match");
        Assert.True(score < 1.0, $"a folder must not grade as an exact identity, got {score}");
    }

    [Fact]
    public void HalfAWordInAFolderIsNotAWord()
    {
        // The per-word rule that protects the filename has to protect the folders too, or a fragment starts
        // matching everything again.
        const string path = @"Music\Kavinsky\Nightcall.mp3";
        var facts = SoulseekFilenameParser.Parse(path);

        Assert.Equal(
            SoulseekQueryMatcher.NoMatch,
            SoulseekQueryMatcher.Score("kavin", facts, path),
            4);
    }

    [Fact]
    public void APathWithNoFoldersIsUnchanged()
    {
        // A bare filename has no folders, so nothing about its grading may move.
        var facts = SoulseekFilenameParser.Parse("Kavinsky - Nightcall.mp3");

        Assert.Equal(SoulseekQueryMatcher.NoMatch, SoulseekQueryMatcher.Score("massive attack", facts, "Kavinsky - Nightcall.mp3"), 4);
        Assert.True(SoulseekQueryMatcher.Score("nightcall", facts, "Kavinsky - Nightcall.mp3") > SoulseekQueryMatcher.NoMatch);
    }
}
