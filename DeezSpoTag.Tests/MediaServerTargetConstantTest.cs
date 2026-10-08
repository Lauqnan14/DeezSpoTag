using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     The self-hosted server ids - "plex", "jellyfin", "navidrome" - are the keys every writer,
///     every dropdown and every stored target row is matched on. They used to be redeclared as a
///     literal in nine separate places, each with its own name. That is duplication that matters
///     more than duplication that does not: a copy that drifts stops matching, and a target that
///     stops matching is silently not written.
///     <para>
///         Each of those constants now aliases <see cref="MediaServerTargetServices" />, which
///         holds the one definition. This pins that, so re-introducing a literal copy fails here
///         rather than in production.
///     </para>
/// </summary>
public sealed class MediaServerTargetConstantTest
{
    /// <summary>
    ///     The canonical values, stated independently of the production constants. This test is
    ///     only meaningful because it is written against hard-coded text: if it were written
    ///     against <see cref="MediaServerTargetServices" /> it would agree with any value,
    ///     including a wrong one.
    /// </summary>
    public static TheoryData<string, string> CanonicalServers => new()
    {
        { "Plex", "plex" },
        { "Jellyfin", "jellyfin" },
        { "Navidrome", "navidrome" },
    };

    [Theory]
    [MemberData(nameof(CanonicalServers))]
    public void MediaServerTargetServices_HoldsTheOneDefinitionOfEachServerId(string field, string expected)
    {
        var actual = typeof(MediaServerTargetServices)
            .GetField(field, BindingFlags.Public | BindingFlags.Static)
            ?.GetRawConstantValue();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    ///     Every alias must still read back as the same bytes.
    /// </summary>
    /// <remarks>
    ///     Read out of the compiled metadata rather than compared in source, because a constant
    ///     reference is inlined at the call site - <c>Assert.Equal("jellyfin", Alias)</c> would
    ///     compile to comparing two literals and could never fail. Asking the assembly what the
    ///     field actually holds does fail if the canonical value is ever changed, which is the
    ///     drift this is here to catch.
    /// </remarks>
    [Theory]
    [InlineData(typeof(LibraryPlaylistSourceResolver), "PlexServer", "plex")]
    [InlineData(typeof(LibraryPlaylistSourceResolver), "JellyfinServer", "jellyfin")]
    [InlineData(typeof(LibraryPlaylistSourceResolver), "NavidromeServer", "navidrome")]
    [InlineData(typeof(MediaServerSoundtrackConstants), "PlexServer", "plex")]
    [InlineData(typeof(MediaServerSoundtrackConstants), "JellyfinServer", "jellyfin")]
    [InlineData(typeof(MelodayTargetServers), "Plex", "plex")]
    [InlineData(typeof(MelodayTargetServers), "Jellyfin", "jellyfin")]
    [InlineData(typeof(MelodayTargetServers), "Navidrome", "navidrome")]
    [InlineData(typeof(AutoTagLiterals), "PlexPlatform", "plex")]
    [InlineData(typeof(AutoTagLiterals), "JellyfinPlatform", "jellyfin")]
    public void EveryServerIdAlias_StillHoldsTheCanonicalBytes(Type owner, string field, string expected)
    {
        var info = owner.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(info);
        Assert.Equal(expected, info.GetRawConstantValue());
    }

    /// <summary>
    ///     The two surfaces that enumerate servers must offer the same set in the same order.
    /// </summary>
    /// <remarks>
    ///     Meloday and the Folder tab read from separate lists. If they diverge, Melody can
    ///     present a server the writer would reject as unconfigured, which looks to the user like
    ///     a dead checkbox.
    /// </remarks>
    [Fact]
    public void TheSelfHostedServers_AreTheSameSetOnEverySurfaceThatEnumeratesThem()
    {
        var expected = new[] { "plex", "jellyfin", "navidrome" };

        Assert.Equal(expected, MediaServerTargetServices.All);
        Assert.Equal(expected, MelodayTargetServers.All.Take(3));
    }

    /// <summary>
    ///     Every id is compared case-insensitively somewhere upstream, so a differing case across
    ///     two surfaces would silently fail to match at runtime while reading as correct here.
    /// </summary>
    [Fact]
    public void EveryServerId_IsLowerCaseAsStoredIdsAreComparedThatWay()
    {
        foreach (var id in MediaServerTargetServices.All)
        {
            Assert.Equal(id.ToLowerInvariant(), id);
        }
    }
}
