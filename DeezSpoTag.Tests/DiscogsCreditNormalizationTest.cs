using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Task 2 Discogs credit regressions: registry-driven normalization of the
/// credit labels Discogs publishes, plus strict resolution of the
/// <c>extraartists[].tracks</c> restriction against the matched release track
/// ordering. Credited names (ANV included) are preserved; unresolved
/// restrictions are never treated as permission for all tracks.
/// </summary>
public sealed class DiscogsCreditNormalizationTest
{
    private static readonly MethodInfo ToTrackMethod =
        typeof(DiscogsMatcher).GetMethod("ToTrack", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DiscogsMatcher.ToTrack not found.");

    private static readonly MethodInfo ToAutoTagTrackMethod =
        typeof(DiscogsMatcher).GetMethod("ToAutoTagTrack", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DiscogsMatcher.ToAutoTagTrack not found.");

    private static (DiscogsTrackInfo Info, AutoTagTrack Track) MapSingleTrack(
        DiscogsExtraArtist? releaseCredit = null,
        DiscogsExtraArtist? trackCredit = null,
        params string[] positions)
    {
        var release = BuildRelease(
            trackCredits: trackCredit != null ? [trackCredit] : null,
            releaseCredits: releaseCredit != null ? [releaseCredit] : null,
            positions);
        var info = (DiscogsTrackInfo)ToTrackMethod.Invoke(null, [release, 0, new DiscogsConfig()])!;
        var track = (AutoTagTrack)ToAutoTagTrackMethod.Invoke(null, [info])!;
        return (info, track);
    }

    private static DiscogsRelease BuildRelease(
        List<DiscogsExtraArtist>? trackCredits = null,
        List<DiscogsExtraArtist>? releaseCredits = null,
        params string[] positions)
    {
        var tracks = (positions.Length > 0 ? positions : ["1"])
            .Select(position => new DiscogsTrack
            {
                Position = position,
                Title = "Track " + position,
                ExtraArtists = trackCredits
            })
            .ToList();
        return new DiscogsRelease
        {
            Id = 42,
            Title = "Album",
            Genres = [],
            Artists = [new DiscogsArtist { Name = "Artist", Id = 7 }],
            Tracks = tracks,
            ExtraArtists = releaseCredits
        };
    }

    private static DiscogsExtraArtist Credit(string name, string role, string? anv = null, string? tracks = null)
        => new() { Name = name, Id = 123, Role = role, Anv = anv, Tracks = tracks };

    [Theory]
    [InlineData("Written-By")]
    [InlineData("Written By")]
    [InlineData("Songwriter")]
    [InlineData("Written-By [Unlinked]")]
    public void WrittenByBecomesSongwriterNeverComposer(string role)
    {
            var (info, track) = MapSingleTrack(releaseCredit: Credit("Nile Rodgers", role));

            Assert.Contains("Nile Rodgers", info.Songwriters);
            Assert.DoesNotContain("Nile Rodgers", info.Composers);
            Assert.DoesNotContain("Nile Rodgers", info.Lyricist);
            // The songwriter route is role-valued, not a combined credit entry.
            Assert.Equal(["Nile Rodgers"], track.Other["songwriter"]);
            Assert.DoesNotContain(
                track.Other.GetValueOrDefault("involvedPeople") ?? [],
                value => value.Contains("Songwriter") || value.Contains("Written"));
    }

    [Fact]
    public void ComposedByStaysComposerAndLyricsByStaysLyricist()
    {
        var (composed, _) = MapSingleTrack(releaseCredit: Credit("B. Wilson", "Composed By"));
        Assert.Equal(["B. Wilson"], composed.Composers);
        Assert.Empty(composed.Songwriters);

        var (lyrics, _) = MapSingleTrack(releaseCredit: Credit("E. John", "Lyrics By"));
        Assert.Equal("E. John", lyrics.Lyricist);
        Assert.Empty(lyrics.Composers);
        Assert.Empty(lyrics.Songwriters);
    }

    [Fact]
    public void ProducerWrittenByIsSplitIntoBothRoles()
    {
        var (info, track) = MapSingleTrack(releaseCredit: Credit("Tomas Bangalter", "Producer, Written-By"));

        Assert.Contains("Tomas Bangalter", info.Songwriters);
        var combined = track.Other["involvedPeople"];
        Assert.Contains("Producer: Tomas Bangalter", combined);
        // The songwriter role must not leak into the combined field either.
        Assert.DoesNotContain(combined, value => value.Contains("Written-By: Tomas Bangalter", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Songwriter: Tomas Bangalter"));
    }

    [Fact]
    public void CompoundEngineerBracketExpandsIntoSpecificCombinedEntries()
    {
        var (info, track) = MapSingleTrack(releaseCredit: Credit("Bob Ludwig", "Engineer [Recording Engineer, Mixing Engineer]"));

        var combined = track.Other["involvedPeople"];
        Assert.Contains("Recording Engineer: Bob Ludwig", combined);
        Assert.Contains("Mixing Engineer: Bob Ludwig", combined);
        // The generic parent subsumed by the pair must not duplicate the person.
        Assert.DoesNotContain(combined, value => value.StartsWith("Engineer:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Engineer [", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(info.Composers);
        Assert.Empty(info.Songwriters);
    }

    [Fact]
    public void UnrecognizedLabelRetainsOriginalTextAsCombinedEntry()
    {
        var (info, track) = MapSingleTrack(releaseCredit: Credit("Antoine", "Lacquer Cut By"));

        var combined = track.Other["involvedPeople"];
        Assert.Single(combined);
        Assert.Equal("Lacquer Cut By: Antoine", combined[0]);
        Assert.Empty(info.Composers);
        Assert.Empty(info.Songwriters);
    }

    [Fact]
    public void PerformerInstrumentQualifierSurvivesWithItsValue()
    {
        var (_, track) = MapSingleTrack(releaseCredit: Credit("Carter Lambert", "Instrument [Piano]"));

        var combined = track.Other["involvedPeople"];
        Assert.Contains("Instrument [Piano]: Carter Lambert", combined);
    }

    [Fact]
    public void MultiRoleContributorsSurviveNormalization()
    {
        var (info, track) = MapSingleTrack(releaseCredit: Credit("K. tools", "Mastered By, Arranged By"));

        var combined = track.Other["involvedPeople"];
        Assert.Contains("Mastered By: K. tools", combined);
        Assert.Contains("Arranged By: K. tools", combined);
        Assert.Empty(info.Composers);
    }

    [Fact]
    public void CreditedAnvIsUsedOverCanonicalName()
    {
        var (info, track) = MapSingleTrack(releaseCredit: Credit("Antoine", "Lacquer Cut By", anv: "A. Le Maître"));

        Assert.Contains("A. Le Maître", track.Other["involvedPeople"][0]);
        Assert.DoesNotContain("Generic Artist", info.Artists);
    }

    [Fact]
    public void UnrecognizedWholeCreditWithMultiplePeopleKeepsOneCombinedEntry()
    {
        var (_, track) = MapSingleTrack(releaseCredit: Credit("Circuit Court, Official", "Court Clerk"));

        Assert.Single(track.Other["involvedPeople"]);
        Assert.Equal("Court Clerk: Circuit Court, Official", track.Other["involvedPeople"][0]);
    }

    private static class extensions
    {
        public static T[] AsArray<T>(IEnumerable<T> source) => source.ToArray();
    }

    // -- tracks restriction ------------------------------------------------

    [Fact]
    public void CommaAndRangeRestrictionAppliesOnlyToCoveredTracks()
    {
        var limited = Credit("Tomas Bangalter", "Producer", tracks: "2, 4-6");
        var credits = new List<DiscogsExtraArtist> { limited };
        var release = BuildRelease(positions: ["1", "2", "3", "4", "5", "6", "7"]);
        release.ExtraArtists = credits;

        Assert.True(GetRestriction(release, 1)); // index 1 → track 2
        Assert.True(GetRestriction(release, 3)); // track 4
        Assert.True(GetRestriction(release, 4)); // track 5
        Assert.True(GetRestriction(release, 5)); // track 6
        Assert.False(GetRestriction(release, 0)); // track 1 not covered
        Assert.False(GetRestriction(release, 2)); // track 3 not covered
        Assert.False(GetRestriction(release, 6)); // track 7 not covered
    }

    [Fact]
    public void PositionStringRestrictionMatchesExactMatrixPositions()
    {
        var limited = Credit("Station Guy", "Engineer", tracks: "A1");
        var release = BuildRelease(positions: ["A1", "A2", "B1"]);
        release.ExtraArtists = [limited];

        Assert.True(GetRestriction(release, 0));
        Assert.False(GetRestriction(release, 1));
        Assert.False(GetRestriction(release, 2));
    }

    [Fact]
    public void RangeCanCrossAGapBetweenSides()
    {
        var limited = Credit("Mix Person", "Mixing Engineer", tracks: "1 to 4");
        var release = BuildRelease(positions: ["A", "B", "1", "2", "3", "4", "5"]);
        release.ExtraArtists = [limited];

        // Ordering resolves over the release tracklist (1-based), not the
        // literal position strings.
        Assert.False(GetRestriction(release, 0));
        Assert.False(GetRestriction(release, 1));
        Assert.True(GetRestriction(release, 2)); // third track of side ordering
        Assert.True(GetRestriction(release, 5));
        Assert.False(GetRestriction(release, 6));
    }

    [Theory]
    [InlineData("A2 to B1")]
    [InlineData("A2-B1")]
    public void VinylRangeUsesExactReleasePositions(string restriction)
    {
        var release = BuildRelease(positions: ["A1", "A2", "B1", "B2"]);
        release.ExtraArtists = [Credit("Engineer", "Engineer", tracks: restriction)];
        Assert.False(GetRestriction(release, 0));
        Assert.True(GetRestriction(release, 1));
        Assert.True(GetRestriction(release, 2));
        Assert.False(GetRestriction(release, 3));
    }

    [Theory]
    [InlineData("A1, A2 to Unknown")]
    [InlineData("A1, B2 to A2")]
    public void InvalidRangeCannotBeAcceptedByAnEarlierListEntry(string restriction)
    {
        var release = BuildRelease(positions: ["A1", "A2", "B1", "B2"]);
        release.ExtraArtists = [Credit("Engineer", "Engineer", tracks: restriction)];
        Assert.False(GetRestriction(release, 0));
    }

    [Fact]
    public void MalformedRestrictionEndpointsAreNeverPermissionForAllTracks()
    {
        var limited = Credit("Older Fellow", "Producer", tracks: "2-A");
        var release = BuildRelease(positions: ["1", "2"]);
        release.ExtraArtists = [limited];

        // "2-A" has a malformed endpoint: it must not fall back to covering
        // every track, including not track 2 itself.
        Assert.False(GetRestriction(release, 0));
        Assert.False(GetRestriction(release, 1));
    }

    [Fact]
    public void BlankRestrictionKeepsReleaseLevelProvenance()
    {
        var unscoped = Credit("Releasing Person", "Producer");
        var release = BuildRelease(positions: ["1", "2"]);
        release.ExtraArtists = [unscoped];

        // Ambiguous scope still resolves the merge (existing behavior), but the
        // nonblank distinction is preserved for full-track credits.
        Assert.True(GetRestriction(release, 0));
        Assert.True(GetRestriction(release, 1));
    }

    [Fact]
    public void RestrictedCreditExcludedFromWrongTrackOutput()
    {
        var limited = Credit("Tomas Bangalter", "Producer", tracks: "2");
        var release = BuildRelease(releaseCredits: [limited], positions: ["1", "2"]);

        var wrongTrack = (DiscogsTrackInfo)ToTrackMethod.Invoke(null, [release, 0, new DiscogsConfig()])!;
        Assert.Empty(wrongTrack.InvolvedPeople);

        var coveredTrack = (DiscogsTrackInfo)ToTrackMethod.Invoke(null, [release, 1, new DiscogsConfig()])!;
        Assert.Contains("Producer: Tomas Bangalter", coveredTrack.InvolvedPeople);
    }

    private static bool GetRestriction(DiscogsRelease release, int trackIndex)
    {
        // Mirrors ToTrack's filtering: the restriction of each release-level
        // credit must decide whether the credit reaches the track.
        var credit = release.ExtraArtists![0];
        var restricted = RestrictionApplies(release, trackIndex, credit);
        return restricted;
    }

    private static bool RestrictionApplies(DiscogsRelease release, int trackIndex, DiscogsExtraArtist credit)
    {
        var method = typeof(DiscogsMatcher).GetMethod("RestrictionAppliesToTrack", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("RestrictionAppliesToTrack not found.");
        return (bool)method.Invoke(null, [credit.Tracks, release, trackIndex])!;
    }
}
