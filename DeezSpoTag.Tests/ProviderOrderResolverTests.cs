using System;
using DeezSpoTag.Services.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ProviderOrderResolverTests
{
    [Fact]
    public void Resolve_UsesConfiguredUniqueOrder_WhenEnabled()
    {
        var resolved = ProviderOrderResolver.Resolve(
            enabled: true,
            configuredOrder: "  Spotify , DEEZER ,spotify  ",
            defaultOrder: ["deezer", "spotify"],
            normalizeToken: token => token?.Trim().ToLowerInvariant() ?? string.Empty);

        Assert.Equal(["spotify", "deezer"], resolved);
    }

    [Fact]
    public void Resolve_UsesDefaultOrder_WhenConfiguredOrderIsEmpty()
    {
        var resolved = ProviderOrderResolver.Resolve(
            enabled: true,
            configuredOrder: "   ",
            defaultOrder: ["deezer", "spotify"],
            normalizeToken: token => token?.Trim().ToLowerInvariant() ?? string.Empty);

        Assert.Equal(["deezer", "spotify"], resolved);
    }

    [Fact]
    public void Resolve_ReturnsPrimaryOnly_WhenFeatureDisabled()
    {
        var resolved = ProviderOrderResolver.Resolve(
            enabled: false,
            configuredOrder: "spotify,deezer",
            defaultOrder: ["deezer", "spotify"],
            normalizeToken: token => token?.Trim().ToLowerInvariant() ?? string.Empty);

        Assert.Equal(["spotify"], resolved);
    }
}
