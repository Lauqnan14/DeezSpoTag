using System;
using System.Reflection;
using System.Text;
using DeezSpoTag.Services.Download.Tidal;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TidalManifestCacheExpiryTests
{
    private static DateTimeOffset? ResolveCacheExpiry(string manifest)
    {
        var method = typeof(TidalDownloadService).GetMethod(
            "ResolveManifestCacheExpiry",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (DateTimeOffset?)method.Invoke(null, [manifest]);
    }

    private static string EncodeManifest(string body)
        => "MANIFEST:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(body));

    [Fact]
    public void ResolveManifestCacheExpiry_UsesSignedExpiry_WhenPresent()
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(30);
        var manifest = EncodeManifest(
            $"<MPD><SegmentURL media=\"https://cdn.tidal.com/seg1.mp4?exp={expiry.ToUnixTimeSeconds()}\" /></MPD>");

        var resolved = ResolveCacheExpiry(manifest);

        Assert.NotNull(resolved);
        Assert.InRange(resolved!.Value, expiry.AddSeconds(-2), expiry.AddSeconds(2));
    }

    [Fact]
    public void ResolveManifestCacheExpiry_FallsBackToShortTtl_WhenAtmosManifestCarriesNoExpiry()
    {
        var manifest = EncodeManifest(
            "<MPD><SegmentTemplate media=\"https://cdn.tidal.com/atmos/$Number$.mp4\" /></MPD>");

        var resolved = ResolveCacheExpiry(manifest);

        Assert.NotNull(resolved);
        Assert.InRange(
            resolved!.Value,
            DateTimeOffset.UtcNow.AddSeconds(30),
            DateTimeOffset.UtcNow.AddSeconds(75));
    }

    [Fact]
    public void ResolveManifestCacheExpiry_DoesNotCacheUntilAStaleExpiry()
    {
        var expired = DateTimeOffset.UtcNow.AddMinutes(-5);
        var manifest = EncodeManifest(
            $"<MPD><SegmentURL media=\"https://cdn.tidal.com/seg1.mp4?exp={expired.ToUnixTimeSeconds()}\" /></MPD>");

        var resolved = ResolveCacheExpiry(manifest);

        Assert.NotNull(resolved);
        Assert.True(
            resolved!.Value > DateTimeOffset.UtcNow,
            "An already-expired manifest must not be cached against its stale expiry.");
    }

    [Fact]
    public void ResolveManifestCacheExpiry_ReturnsNull_WhenManifestCannotBeDecoded()
    {
        Assert.Null(ResolveCacheExpiry(string.Empty));
    }
}
