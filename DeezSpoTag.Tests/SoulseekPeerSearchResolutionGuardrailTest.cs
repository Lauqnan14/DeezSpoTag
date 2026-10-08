using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Why a Soulseek item must never be reported as an unresolvable engine mapping.
/// </summary>
/// <remarks>
///     <para>
///         A Soulseek download is a peer search, not a catalogue lookup, so it has no store URL to resolve.
///         The fallback service answers such a step with an obviously-fake sentinel, and the engine runs the
///         search itself when the item downloads.
///     </para>
///     <para>
///         The sentinel was recognised nowhere, so it read as a store link belonging to some other service:
///         the resolution reported a mapping failure, the item was filed as unavailable - terminal, never
///         retried - and a perfectly good Soulseek download never started. That is what a queue full of
///         UNAVAILABLE items meant.
///     </para>
/// </remarks>
public sealed class SoulseekPeerSearchResolutionGuardrailTest
{
    private static readonly MethodInfo IsServiceUrlMatchMethod =
        typeof(DownloadIntentService).GetMethod(
            "IsServiceUrlMatch",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DownloadIntentService.IsServiceUrlMatch not found.");

    [Fact]
    public void ThePeerSearchSentinelIsSoulseeksOwnResolution()
    {
        var matches = (bool)IsServiceUrlMatchMethod.Invoke(
            null,
            [SoulseekQueueItem.PeerSearchResolutionSentinel, SoulseekQueueItem.EngineId])!;

        Assert.True(
            matches,
            "The sentinel is what the fallback service returns for a Soulseek step, so it has to count as a "
            + "match for the Soulseek engine rather than as a foreign store URL.");
    }

    [Fact]
    public void TheSentinelIsTheOneTheFallbackServiceActuallyReturns()
    {
        // If the two ever drift, the sentinel stops matching again and Soulseek items go unavailable. The
        // fallback service's own constant is private, so it is compared through the value it produces.
        var produced = ResolveSoulseekFallbackUrl();

        Assert.Equal(SoulseekQueueItem.PeerSearchResolutionSentinel, produced);
    }

    [Theory]
    [InlineData("spotify")]
    [InlineData("deezer")]
    [InlineData("tidal")]
    [InlineData("amazon")]
    [InlineData("qobuz")]
    public void TheSoulseekSentinelIsNotAnotherEnginesStoreUrl(string engine)
    {
        var matches = (bool)IsServiceUrlMatchMethod.Invoke(
            null,
            [SoulseekQueueItem.PeerSearchResolutionSentinel, engine])!;

        Assert.False(matches, "A peer-search sentinel is not a store URL for any catalogue engine.");
    }

    private static string ResolveSoulseekFallbackUrl()
    {
        // A Soulseek step is answered before any catalogue is consulted, so the service is built with nothing
        // injected: if it reaches for a catalogue at all to answer this engine, the test fails loudly.
        var service = new EngineFallbackSearchService(
            new AppleMusicCatalogService(null!, null!, null!, null!),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EngineFallbackSearchService>.Instance);

        var result = service.ResolveAsync(new EngineFallbackSearchRequest(
            Engine: SoulseekQueueItem.EngineId,
            SourceUrl: string.Empty,
            SpotifyId: null,
            AppleId: null,
            QobuzId: null,
            TidalId: null,
            AmazonId: null,
            Isrc: null,
            Title: "Roygbiv",
            Artist: "Boards of Canada",
            Album: string.Empty,
            DurationMs: null,
            DeezerId: null,
            Quality: "FLAC",
            ContentType: "stereo",
            Storefront: "us",
            Language: "en-US",
            MediaUserToken: null,
            UserCountry: "us",
            FallbackSearchEnabled: true),
            CancellationToken.None).GetAwaiter().GetResult();

        return result.ResolvedUrl ?? string.Empty;
    }
}
