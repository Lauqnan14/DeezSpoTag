using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Tidal;
using DeezSpoTag.Integrations.Tidal;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TidalApiProviderSourceTests
{
    [Fact]
    public async Task GetRotatedProvidersAsync_UsesSevenCataloguedNonMonochromeProviders()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "deezspotag-tests", Guid.NewGuid().ToString("N"));
        using var scope = new TestConfigRootScope(rootPath);
        var service = CreateService();

        var providers = await service.GetRotatedProvidersAsync(CancellationToken.None);

        Assert.Equal(8, providers.Count);
        Assert.DoesNotContain(providers, provider => provider.Contains("monochrome", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(providers, provider => provider.Contains("squid", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("https://api.zarz.moe/v1/dl/tid2", providers[0]);
        Assert.Contains("https://hifi.geeked.wtf", providers);
        Assert.Contains("https://hifi-one.spotisaver.net", providers);
        Assert.Contains("https://hifi-two.spotisaver.net", providers);
    }

    [Fact]
    public async Task RememberSuccessAsync_RotatesLastSuccessfulProviderToTheEnd()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "deezspotag-tests", Guid.NewGuid().ToString("N"));
        using var scope = new TestConfigRootScope(rootPath);
        var service = CreateService();

        await service.RememberSuccessAsync("https://hifi.geeked.wtf", CancellationToken.None);
        var providers = await service.GetRotatedProvidersAsync(CancellationToken.None);

        Assert.Equal("https://hifi.p1nkhamster.xyz", providers[0]);
        Assert.Equal("https://hifi.geeked.wtf", providers[^1]);
    }

    [Fact]
    public async Task GetRotatedProvidersAsync_UsesCleanCatalogNames()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "deezspotag-tests", Guid.NewGuid().ToString("N"));
        using var scope = new TestConfigRootScope(rootPath);
        var registry = new InMemoryProviderRegistry();
        var service = new TidalApiProviderSource(registry);

        _ = await service.GetRotatedProvidersAsync(CancellationToken.None);
        var providers = await registry.GetProvidersAsync(CancellationToken.None);

        string[] expectedNames = ["Zarz", "Geeked", "Pink Hamster", "QQDL Vogel", "SpotiSaver One", "SpotiSaver Two", "KinoPlus", "Binimum"];
        Assert.Equal(expectedNames, providers.Select(provider => provider.DisplayName).ToArray());
    }

    [Fact]
    public async Task GetRotatedProvidersAsync_ExcludesDisabledProvider()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "deezspotag-tests", Guid.NewGuid().ToString("N"));
        using var scope = new TestConfigRootScope(rootPath);
        var registry = new InMemoryProviderRegistry();
        var service = new TidalApiProviderSource(registry);

        _ = await service.GetRotatedProvidersAsync(CancellationToken.None);
        await registry.SetEnabledAsync("geeked", false, CancellationToken.None);
        var providers = await service.GetRotatedProvidersAsync(CancellationToken.None);

        Assert.Equal(7, providers.Count);
        Assert.DoesNotContain("https://hifi.geeked.wtf", providers);
    }

    private static TidalApiProviderSource CreateService() => new(new InMemoryProviderRegistry());

    private sealed class InMemoryProviderRegistry : ITidalPublicProviderRegistry
    {
        private readonly List<TidalPublicProvider> _providers = TidalPublicProviderDefaults.Providers
            .Select(definition => new TidalPublicProvider(
                definition.Id,
                definition.DisplayName,
                definition.Kind,
                definition.Endpoint,
                definition.HealthEndpoint,
                definition.HealthServiceKey,
                true,
                "unknown",
                null,
                null,
                null,
                null,
                null,
                null))
            .ToList();

        public Task<IReadOnlyList<TidalPublicProvider>> GetProvidersAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<TidalPublicProvider>>(_providers.ToArray());

        public Task<IReadOnlyList<TidalPublicProvider>> CheckEnabledProvidersAsync(CancellationToken cancellationToken)
            => GetProvidersAsync(cancellationToken);

        public Task<TidalPublicProvider?> SetEnabledAsync(string providerId, bool enabled, CancellationToken cancellationToken)
        {
            var index = _providers.FindIndex(provider => provider.Id == providerId);
            if (index < 0) return Task.FromResult<TidalPublicProvider?>(null);
            _providers[index] = _providers[index] with { Enabled = enabled };
            return Task.FromResult<TidalPublicProvider?>(_providers[index]);
        }

        public Task RecordSuccessAsync(string endpoint, long responseTimeMs, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordFailureAsync(string endpoint, string category, long responseTimeMs, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordFailureAsync(string endpoint, string category, long responseTimeMs, DateTimeOffset? cooldownUntil, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
