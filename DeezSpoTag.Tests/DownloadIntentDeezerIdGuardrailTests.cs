using System;
using System.Reflection;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadIntentDeezerIdGuardrailTests
{
    private static readonly MethodInfo NormalizeDeezerTrackIdMethod =
        typeof(DownloadIntentService).GetMethod(
            "NormalizeDeezerTrackId",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DownloadIntentService.NormalizeDeezerTrackId not found.");

    private static readonly MethodInfo TryExtractDeezerTrackIdMethod =
        typeof(DownloadIntentService).GetMethod(
            "TryExtractDeezerTrackId",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DownloadIntentService.TryExtractDeezerTrackId not found.");

    private static readonly MethodInfo ResolveDeezerTrackIdForEnqueueMethod =
        typeof(DownloadIntentService).GetMethod(
            "ResolveDeezerTrackIdForEnqueue",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DownloadIntentService.ResolveDeezerTrackIdForEnqueue not found.");

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeDeezerTrackId_RejectsInvalidOrZeroValues(string? value)
    {
        var normalized = NormalizeDeezerTrackId(value);

        Assert.Null(normalized);
    }

    [Theory]
    [InlineData("123", "123")]
    [InlineData("00123", "123")]
    public void NormalizeDeezerTrackId_NormalizesPositiveNumericValues(string value, string expected)
    {
        var normalized = NormalizeDeezerTrackId(value);

        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void TryExtractDeezerTrackId_RejectsZeroTrackId()
    {
        var extracted = TryExtractDeezerTrackId("https://www.deezer.com/track/0");

        Assert.Null(extracted);
    }

    [Fact]
    public void TryExtractDeezerTrackId_ReturnsPositiveTrackId()
    {
        var extracted = TryExtractDeezerTrackId("https://www.deezer.com/track/3135556?utm=1");

        Assert.Equal("3135556", extracted);
    }

    [Fact]
    public void ResolveDeezerTrackIdForEnqueue_UsesIntentSourceUrl_WhenResolvedSourceUrlIsMissing()
    {
        var intent = new DownloadIntent
        {
            SourceUrl = "https://www.deezer.com/track/3135556",
            DeezerId = string.Empty
        };

        var resolved = ResolveDeezerTrackIdForEnqueue(
            intent,
            resolvedSourceUrl: null,
            isPodcastIntent: false);

        Assert.Equal("3135556", resolved);
    }

    private static string? NormalizeDeezerTrackId(string? value)
    {
        return NormalizeDeezerTrackIdMethod.Invoke(null, new object?[] { value }) as string;
    }

    private static string? TryExtractDeezerTrackId(string? value)
    {
        return TryExtractDeezerTrackIdMethod.Invoke(null, new object?[] { value }) as string;
    }

    private static string? ResolveDeezerTrackIdForEnqueue(
        DownloadIntent intent,
        string? resolvedSourceUrl,
        bool isPodcastIntent)
    {
        return ResolveDeezerTrackIdForEnqueueMethod.Invoke(
            null,
            new object?[] { intent, resolvedSourceUrl, isPodcastIntent }) as string;
    }
}
