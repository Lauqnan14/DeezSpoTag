using System.Linq;
using DeezSpoTag.Services.Download.Soulseek;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Deciding whether a peer publishes the folder a search result named.
/// </summary>
/// <remarks>
///     <para>
///         This is what stopped the app claiming peers were empty. A search result's path is not proof of a
///         folder: slskd reports full nested paths for a file, but its directory endpoint answers with leaf
///         names and no directory prefix, and only for a share root. So a folder taken from a filename could
///         not be walked back to, and the empty answer read as "this peer holds nothing" for peers holding tens
///         of thousands of files.
///     </para>
///     <para>
///         The matching is deliberately exact, and the tests below are mostly about the ways it must stay
///         exact: no nearest match, no prefix match, no fuzzy credit. Substituting a folder the reader did not
///         ask for would file someone else's tracks under a release name.
///     </para>
/// </remarks>
public sealed class SoulseekFolderPathTest
{
    [Theory]
    [InlineData("music\\Artist\\Album", "music\\Artist\\Album")]
    [InlineData("music\\Artist\\Album", "music/Artist/Album")]
    [InlineData("music/Artist/Album", "music\\Artist\\Album")]
    [InlineData("music\\Artist\\Album", "music\\Artist\\Album\\")]
    [InlineData("music\\Artist\\Album", "  music\\Artist\\Album  ")]
    public void TheSameFolderMatchesWhicheverWayPeersWriteSeparators(
        string published,
        string requested)
        => Assert.True(SoulseekFolderPath.IsSameFolder(published, requested));

    /// <summary>
    ///     Peers disagree about spacing as well as separators, and it is punctuation rather than identity.
    /// </summary>
    [Theory]
    [InlineData("music\\100 gecs (2019)", "music\\100  gecs  (2019)")]
    [InlineData("music\\Album", "music\\Album ")]
    public void SpacingIsNormalizedBecauseItIsPunctuationRatherThanIdentity(string published, string requested)
        => Assert.True(SoulseekFolderPath.IsSameFolder(published, requested));

    /// <summary>
    ///     Whitespace is collapsed, never added or removed. A folder the peer spells without a space is a
    ///     different folder name, and treating the two as one would resolve to a folder the peer does not
    ///     publish. This is the line between normalizing punctuation and inventing a path.
    /// </summary>
    [Theory]
    [InlineData("music\\100 gecs(2019)", "music\\100 gecs (2019)")]
    [InlineData("music\\Album", "music\\  Album")]
    [InlineData("music\\Artist (2019)", "music\\Artist(2019)")]
    public void AMissingSpaceMakesTwoDifferentFoldersRatherThanOneFolder(string published, string requested)
        => Assert.False(SoulseekFolderPath.IsSameFolder(published, requested));

    /// <summary>
    ///     Case is normalized, because a Soulseek share is addressed case-insensitively and two peers disagree
    ///     about the casing of the same folder.
    /// </summary>
    /// <summary>
    ///     Case does not make two spellings of one folder, because a Soulseek share is addressed
    ///     case-insensitively and two peers disagree about the casing of the same folder name.
    /// </summary>
    [Theory]
    [InlineData("Music\\Massive Attack", "music\\massive attack")]
    [InlineData("music\\Artist", "MUSIC\\ARTIST")]
    [InlineData("MUSIC\\ARTIST", "music\\Artist")]
    public void CaseDoesNotMakeTwoSpellingsOfOneFolder(string published, string requested)
        => Assert.True(SoulseekFolderPath.IsSameFolder(published, requested));

    /// <summary>
    ///     A folder that is not the one asked for is not the one asked for. Every case here is a way of getting
    ///     this wrong, and each has to be refused: a nearest match would put a different release's files under
    ///     the name of the release the reader chose.
    /// </summary>
    [Theory]
    [InlineData("music\\Massive Attack\\Mezzanine", "music\\Massive Attack\\Mezzanine (Remixes)")]
    [InlineData("music\\Massive Attack\\Mezzanine", "music\\Massive Attack")]
    [InlineData("music\\Massive Attack", "music\\Massive Attack\\Mezzanine")]
    [InlineData("music\\Artist\\Album", "music\\Other\\Album")]
    [InlineData("music\\Album", "downloads\\Album")]
    [InlineData("music\\Album (Disc 1)", "music\\Album (Disc 2)")]
    [InlineData("music\\Album", "music\\Album2")]
    public void AFolderThatIsNotTheOneAskedForIsRefused(string published, string requested)
        => Assert.False(SoulseekFolderPath.IsSameFolder(published, requested));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyPathMatchesNothing(string? path)
    {
        Assert.False(SoulseekFolderPath.IsSameFolder(path, "music\\Album"));
        Assert.False(SoulseekFolderPath.IsSameFolder("music\\Album", path));
        Assert.False(SoulseekFolderPath.IsSameFolder(path, path));
    }

    /// <summary>
    ///     The real shape of the defect, held as a regression: a folder a peer does not publish must be
    ///     refused rather than silently resolved to the nearest folder that does exist.
    /// </summary>
    [Theory]
    [InlineData("Mezzanine", "mezzanine")]
    [InlineData("MASSIVE ATTACK", "massive attack")]
    public void FoldersDifferingOnlyByCaseAreTheSameFolder(string published, string requested)
        => Assert.True(SoulseekFolderPath.IsSameFolder(published, requested));

    [Fact]
    public void AFolderThePeerDoesNotPublishIsNotResolvedToTheNearestOneItDoes()
    {
        // These are real paths from one peer's published directory list.
        string[] published =
        {
            "music\\Massive Attack\\Mezzanine (1998)",
            "music\\Massive Attack\\Mezzanine - The Remixes (2006)"
        };

        // A search reported this path. It is not published, and answering with the remixes would file the
        // wrong release under the name the reader chose.
        const string requested = "music\\Massive Attack\\Mezzanine";

        Assert.False(published.Any(path => SoulseekFolderPath.IsSameFolder(path, requested)));
        Assert.True(published.Any(path => SoulseekFolderPath.IsSameFolder(path, "music\\Massive Attack/Mezzanine (1998)")));
    }
}