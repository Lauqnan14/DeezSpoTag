using System;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyArtistNameMatchingGuardrailTests
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

    private static bool InvokeEquivalent(string candidate, string target)
    {
        var value = EquivalentMethod.Invoke(null, [candidate, target]);
        return value is true;
    }
}
