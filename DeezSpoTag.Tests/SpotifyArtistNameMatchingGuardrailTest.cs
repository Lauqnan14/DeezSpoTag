using System;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyArtistNameMatchingGuardrailTest
{
    private static readonly MethodInfo EquivalentMethod =
        typeof(SpotifyArtistService).GetMethod(
            "IsEquivalentArtistName",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not resolve SpotifyArtistService.IsEquivalentArtistName.");

    [Theory]
    [InlineData("2 pac", "2pac")]
    [InlineData("Jay-Z", "Jay Z")]
    [InlineData("Soulja Boy Tell 'Em", "Soulja Boy")]
    [InlineData("Marvin Gaye", "marvin gaye")]
    [InlineData("ROMANS", "RØMANS")]
    public void EquivalentArtistNames_AreRecognized(string left, string right)
    {
        var equivalent = InvokeEquivalent(left, right);
        Assert.True(equivalent);
    }

    [Theory]
    [InlineData("2 pac", "2 chainz")]
    [InlineData("Bob Marley", "Bob Dylan")]
    [InlineData("Adele", "Alicia Keys")]
    public void DifferentArtistNames_AreNotMatched(string left, string right)
    {
        var equivalent = InvokeEquivalent(left, right);
        Assert.False(equivalent);
    }

    [Fact]
    public void ExactNameMatch_DoesNotUseCanonicalFallbackWhenLocalAlbumsExist()
    {
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(
            FindSourceRoot(),
            "DeezSpoTag.Web",
            "Services",
            "SpotifyArtistService.cs"));
        var methodStart = source.IndexOf("private async Task<string?> TrySelectExactCandidateArtistIdAsync", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("private async Task<string?> ResolveArtistIdWithShazamFallbackAsync", methodStart, StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains("if (localAlbumTitleSet.Count > 0)", method, StringComparison.Ordinal);
        Assert.Contains("skipped canonical name fallback", method, StringComparison.Ordinal);
        Assert.True(
            method.IndexOf("if (localAlbumTitleSet.Count > 0)", StringComparison.Ordinal)
            < method.IndexOf("TrySelectCanonicalFallbackExactCandidateAsync", StringComparison.Ordinal));
    }

    private static string FindSourceRoot()
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "DeezSpoTag.Web", "Services", "SpotifyArtistService.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate source root.");
    }

    private static bool InvokeEquivalent(string candidate, string target)
    {
        var value = EquivalentMethod.Invoke(null, [candidate, target]);
        return value is true;
    }
}
