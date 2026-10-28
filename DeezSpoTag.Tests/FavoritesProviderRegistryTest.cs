using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.Favorites;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class FavoritesProviderRegistryTest
{
    private sealed class StubProvider(string key) : IFavoritesProvider
    {
        public string Key { get; } = key;
        public string DisplayName => $"Display {Key}";
        public string IconPath => $"/icons/{Key}.svg";

        public Task<FavoritesResult> GetFavoritesAsync(int limit, CancellationToken cancellationToken)
            => Task.FromResult(new FavoritesResult(true, null, [], [], []));
    }

    [Fact]
    public void Providers_EmptyInput_ReturnsEmptyList()
    {
        var registry = new FavoritesProviderRegistry([]);

        Assert.Empty(registry.Providers);
    }

    [Fact]
    public void Providers_NullInput_ReturnsEmptyList()
    {
        var registry = new FavoritesProviderRegistry(null!);

        Assert.Empty(registry.Providers);
    }

    [Fact]
    public void Providers_DefaultOrder_WhenAllKnownKeysRegistered()
    {
        var registry = new FavoritesProviderRegistry(
        [
            new StubProvider("discogs"),
            new StubProvider("spotify"),
            new StubProvider("tidal"),
            new StubProvider("deezer"),
            new StubProvider("appleMusic"),
            new StubProvider("ytmusic"),
            new StubProvider("qobuz"),
        ]);

        Assert.Equal(
            ["spotify", "deezer", "qobuz", "appleMusic", "ytmusic", "tidal", "discogs"],
            registry.Providers.Select(p => p.Key));
    }

    [Fact]
    public void Providers_DuplicateKey_LastRegistrationWins()
    {
        var first = new StubProvider("spotify");
        var second = new StubProvider("SPOTIFY");

        var registry = new FavoritesProviderRegistry([first, second]);

        var provider = Assert.Single(registry.Providers);
        Assert.Same(second, provider);
        Assert.DoesNotContain(first, registry.Providers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Providers_BlankKey_Skipped(string blankKey)
    {
        var registry = new FavoritesProviderRegistry([new StubProvider(blankKey), new StubProvider("deezer")]);

        var provider = Assert.Single(registry.Providers);
        Assert.Equal("deezer", provider.Key);
    }

    [Fact]
    public void Providers_NullEntries_Skipped()
    {
        var registry = new FavoritesProviderRegistry([null!, new StubProvider("tidal")]);

        var provider = Assert.Single(registry.Providers);
        Assert.Equal("tidal", provider.Key);
    }

    [Fact]
    public void Providers_UnknownKeys_AppendAfterKnownOnesSortedCaseInsensitive()
    {
        var registry = new FavoritesProviderRegistry(
        [
            new StubProvider("zeta"),
            new StubProvider("deezer"),
            new StubProvider("Alpha"),
            new StubProvider("alpha2"),
        ]);

        Assert.Equal(
            ["deezer", "Alpha", "alpha2", "zeta"],
            registry.Providers.Select(p => p.Key));
    }

    [Fact]
    public void Providers_KnownKeyDifferentCase_PlacedAtDefaultPosition()
    {
        var registry = new FavoritesProviderRegistry([new StubProvider("TIDAL"), new StubProvider("deezer")]);

        Assert.Equal(
            ["deezer", "TIDAL"],
            registry.Providers.Select(p => p.Key));
    }
}
