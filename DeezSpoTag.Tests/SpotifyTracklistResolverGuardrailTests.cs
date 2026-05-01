using System;
using System.Reflection;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyTracklistResolverGuardrailTests
{
    private static readonly MethodInfo ValidateCandidateMethod =
        typeof(SpotifyTracklistResolver).GetMethod(
            "ValidateCandidate",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpotifyTracklistResolver.ValidateCandidate not found.");

    [Fact]
    public void ValidateCandidate_RejectsExactTitleWithDifferentArtist()
    {
        var source = new SpotifyTrackSummary(
            Id: "5rdXPdk1QvAeoopHdtPkNF",
            Name: "Joy",
            Artists: "Diamond Platnumz, Jux",
            Album: "Joy",
            DurationMs: 219000,
            SourceUrl: "https://open.spotify.com/track/5rdXPdk1QvAeoopHdtPkNF",
            ImageUrl: null,
            Isrc: null,
            ReleaseDate: "2026-04-11");
        var candidate = new ApiTrack
        {
            Id = "3907519851",
            Title = "Joy",
            TitleShort = "Joy",
            Duration = 221,
            ReleaseDate = "2026-04-10",
            Artist = new ApiArtist { Id = 4341139, Name = "DOGSTAR" },
            Album = new ApiAlbum { Id = 0, Title = "Joy", ReleaseDate = "2026-04-10" }
        };

        var validation = ValidateCandidateMethod.Invoke(null, new object?[] { source, candidate, false });
        var accepted = validation?.GetType().GetProperty("IsAccepted")?.GetValue(validation);

        Assert.False(Assert.IsType<bool>(accepted));
    }
}
