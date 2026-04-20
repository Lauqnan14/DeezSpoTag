using System;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyArtistVerificationGuardrailTests
{
    private static readonly MethodInfo VerifiedCandidateMethod =
        typeof(SpotifyArtistService).GetMethod(
            "IsVerifiedArtistCandidate",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not resolve SpotifyArtistService.IsVerifiedArtistCandidate.");

    [Fact]
    public void VerifiedCandidate_OnlyTrueForVerifiedProfiles()
    {
        var unverified = new SpotifyPathfinderMetadataClient.SpotifyArtistCandidateInfo(
            ArtistId: "0du5cEVh5yTK9QJze8zA0C",
            Verified: false,
            TotalAlbums: 10);
        var verified = unverified with { Verified = true };

        Assert.False(InvokeIsVerifiedCandidate(null));
        Assert.False(InvokeIsVerifiedCandidate(unverified));
        Assert.True(InvokeIsVerifiedCandidate(verified));
    }

    private static bool InvokeIsVerifiedCandidate(SpotifyPathfinderMetadataClient.SpotifyArtistCandidateInfo? info)
    {
        var value = VerifiedCandidateMethod.Invoke(null, [info]);
        return value is true;
    }
}
