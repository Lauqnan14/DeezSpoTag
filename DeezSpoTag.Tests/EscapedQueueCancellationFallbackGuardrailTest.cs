using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EscapedQueueCancellationFallbackGuardrailTest
{
    [Fact]
    public void EscapedProcessorCancellation_AttemptsFallbackBeforeFinalFailure()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../",
            "DeezSpoTag.Services",
            "Download",
            "Shared",
            "DeezSpoTagApp.cs"));

        var handlerStart = source.IndexOf(
            "private async Task HandleUnhandledProcessorCancellationAsync",
            StringComparison.Ordinal);
        var fallbackIndex = source.IndexOf(
            "await TryAdvanceFallbackAsync(item)",
            handlerStart,
            StringComparison.Ordinal);
        var failureIndex = source.IndexOf(
            "await MarkQueueItemAsFailedAndRetryAsync(item, timeoutException.Message)",
            handlerStart,
            StringComparison.Ordinal);

        Assert.True(handlerStart >= 0);
        Assert.True(fallbackIndex > handlerStart);
        Assert.True(failureIndex > fallbackIndex);
    }

    [Fact]
    public void EscapedProcessorCancellation_FallbackSupportsEveryQueueEngine()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../",
            "DeezSpoTag.Services",
            "Download",
            "Shared",
            "DeezSpoTagApp.cs"));

        Assert.Contains("TryAdvanceFallbackAsync<QobuzQueueItem>", source, StringComparison.Ordinal);
        Assert.Contains("TryAdvanceFallbackAsync<TidalQueueItem>", source, StringComparison.Ordinal);
        Assert.Contains("TryAdvanceFallbackAsync<AmazonQueueItem>", source, StringComparison.Ordinal);
        Assert.Contains("TryAdvanceFallbackAsync<AppleQueueItem>", source, StringComparison.Ordinal);
        Assert.Contains("TryAdvanceFallbackAsync<DeezerQueueItem>", source, StringComparison.Ordinal);
        Assert.Contains("TryAdvanceFallbackAsync<SoulseekQueueItem>", source, StringComparison.Ordinal);
        Assert.Contains("CancellationToken.None", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapedProcessorCancellation_FallbackListenerPayloadSupportsEveryQueueEngine()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../",
            "DeezSpoTag.Services",
            "Download",
            "Shared",
            "DeezSpoTagApp.cs"));

        // BuildFallbackQueuePayload is the other half of the same switch. An engine that can be advanced but
        // has no payload arm turns a silent no-op into a thrown exception at runtime, so the two have to be
        // pinned together rather than one at a time.
        Assert.Contains("QobuzQueueItem qobuz => qobuz.ToQueuePayload()", source, StringComparison.Ordinal);
        Assert.Contains("TidalQueueItem tidal => tidal.ToQueuePayload()", source, StringComparison.Ordinal);
        Assert.Contains("AmazonQueueItem amazon => amazon.ToQueuePayload()", source, StringComparison.Ordinal);
        Assert.Contains("AppleQueueItem apple => apple.ToQueuePayload()", source, StringComparison.Ordinal);
        Assert.Contains("DeezerQueueItem deezer => deezer.ToQueuePayload()", source, StringComparison.Ordinal);
        Assert.Contains("SoulseekQueueItem soulseek => soulseek.ToQueuePayload()", source, StringComparison.Ordinal);
    }
}
