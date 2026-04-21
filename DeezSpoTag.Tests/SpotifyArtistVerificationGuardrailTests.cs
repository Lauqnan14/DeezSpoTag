using System;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyArtistVerificationGuardrailTests
{
    private static readonly MethodInfo ExactCandidateScoreMethod =
        typeof(SpotifyArtistService).GetMethod(
            "ComputeExactCandidateScore",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not resolve SpotifyArtistService.ComputeExactCandidateScore.");

    [Fact]
    public void VerifiedCandidate_AppliesVerificationTiebreakerOnlyForVerifiedProfiles()
    {
        var unverified = new SpotifyPathfinderMetadataClient.SpotifyArtistCandidateInfo(
            ArtistId: "0du5cEVh5yTK9QJze8zA0C",
            Verified: false,
            TotalAlbums: 10,
            TotalTracks: 120);
        var verified = unverified with { Verified = true };

        var unverifiedScore = InvokeExactCandidateScore(localAlbumOverlap: 0, unverified);
        var verifiedScore = InvokeExactCandidateScore(localAlbumOverlap: 0, verified);
        var nullInfoScore = InvokeExactCandidateScore(localAlbumOverlap: 0, info: null);

        Assert.Equal(0, nullInfoScore);
        Assert.Equal(25, verifiedScore - unverifiedScore);
    }

    private static int InvokeExactCandidateScore(int localAlbumOverlap, SpotifyPathfinderMetadataClient.SpotifyArtistCandidateInfo? info)
    {
        var value = ExactCandidateScoreMethod.Invoke(null, [localAlbumOverlap, info]);
        return value is int score ? score : 0;
    }
}
