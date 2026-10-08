using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     spotify, deezer, qobuz, tidal and amazon were spelled out as raw literals in sixteen files
///     while two classes already declared the same values. That matters more than duplication
///     usually does, because these ids are compared against stored rows: a copy that drifts from
///     the vocabulary the rows were written with stops matching, and a track whose id stops
///     matching is not written. Nothing is reported, because the write simply does not happen.
///     <para>
///         Every such literal now points at the single definition in
///         <see cref="DownloadTagSourceHelper" />. This pins that, so re-introducing a literal
///         copy fails here rather than as a silently missing track.
///     </para>
/// </summary>
public sealed class DownloadSourceIdCanonicalTest
{
    /// <summary>
    ///     The canonical values, stated independently of the production constants. Written against
    ///     hard-coded text on purpose: a test written against the constants would agree with any
    ///     value, including a wrong one.
    /// </summary>
    [Theory]
    [InlineData("DeezerSource", "deezer")]
    [InlineData("SpotifySource", "spotify")]
    [InlineData("AppleSource", "apple")]
    [InlineData("QobuzSource", "qobuz")]
    [InlineData("TidalSource", "tidal")]
    [InlineData("AmazonSource", "amazon")]
    public void TheCanonicalSourceIds_AreTheOnesStoredRowsAreWrittenWith(string field, string expected)
    {
        var actual = typeof(DownloadTagSourceHelper)
            .GetField(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?.GetRawConstantValue();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    ///     The platform resolver compares stored ids against its own constants. If its copy drifted
    ///     from the vocabulary the rows were written with, every comparison would fail and no
    ///     candidate would ever be written - silently, with no error to trace.
    /// </summary>
    /// <remarks>
    ///     Read from compiled metadata rather than compared in source: a constant reference is
    ///     inlined at compile time, so <c>Assert.Equal("deezer", Alias)</c> would compare two
    ///     literals and could never fail. Asking the assembly what the field holds does fail when
    ///     the canonical value changes, which is the drift this exists to catch.
    /// </remarks>
    [Theory]
    [InlineData("DeezerService", "deezer")]
    [InlineData("SpotifyService", "spotify")]
    [InlineData("QobuzService", "qobuz")]
    [InlineData("TidalService", "tidal")]
    public void EveryPlatformResolverId_MatchesTheStoredSourceVocabulary(string field, string expected)
    {
        var info = typeof(PlatformTrackIdentityResolver)
            .GetField(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        Assert.NotNull(info);
        Assert.Equal(expected, info.GetRawConstantValue());
    }

    /// <summary>
    ///     Apple Music's platform id is "applemusic", which is a different value from the "apple"
    ///     download source. They are close enough that aliasing one to the other looks harmless and
    ///     is not: it would make the platform resolver compare against a vocabulary no stored row
    ///     was written with. This is the one place the two must stay distinct.
    /// </summary>
    [Fact]
    public void AppleMusicsPlatformIdIsNotInterchangeableWithTheAppleDownloadSource()
    {
        Assert.Equal("applemusic", PlatformTrackIdentityResolver.AppleMusicService);
        Assert.Equal("apple", DownloadTagSourceHelper.AppleSource);
        Assert.NotEqual(
            PlatformTrackIdentityResolver.AppleMusicService,
            DownloadTagSourceHelper.AppleSource);
    }

    /// <summary>
    ///     The stored-source aliases must normalise to themselves. A resolver id that normalised to
    ///     null would be rejected as an unrecognised source before it was ever compared.
    /// </summary>
    [Theory]
    [InlineData("DeezerService")]
    [InlineData("SpotifyService")]
    [InlineData("QobuzService")]
    [InlineData("TidalService")]
    public void EveryPlatformResolverId_IsAValueTheStoredSourceNormaliserAccepts(string field)
    {
        var value = (string)typeof(PlatformTrackIdentityResolver)
            .GetField(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()!;

        Assert.Equal(value, DownloadTagSourceHelper.NormalizeResolvedDownloadTagSource(value));
    }

    /// <summary>
    ///     Every canonical id has to survive normalisation unchanged, since that is what decides
    ///     whether a stored row is readable at all.
    /// </summary>
    [Fact]
    public void EveryCanonicalSourceId_NormalisesToItself()
    {
        foreach (var id in new[]
                 {
                     DownloadTagSourceHelper.DeezerSource,
                     DownloadTagSourceHelper.SpotifySource,
                     DownloadTagSourceHelper.AppleSource,
                     DownloadTagSourceHelper.QobuzSource,
                     DownloadTagSourceHelper.TidalSource,
                     DownloadTagSourceHelper.AmazonSource,
                 })
        {
            Assert.Equal(id, DownloadTagSourceHelper.NormalizeResolvedDownloadTagSource(id));
        }
    }
}
