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
    public async Task GetRotatedProvidersAsync_UsesZarzProvider()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "deezspotag-tests", Guid.NewGuid().ToString("N"));
        using var scope = new TestConfigRootScope(rootPath);
        var service = CreateService();

        var providers = await service.GetRotatedProviderRecordsAsync(CancellationToken.None);

        Assert.Equal(["https://api.zarz.moe/v2/dl/tid"], providers.Select(static provider => provider.Endpoint));
    }

    [Fact]
    public async Task RememberSuccessAsync_KeepsSingleProviderAvailable()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "deezspotag-tests", Guid.NewGuid().ToString("N"));
        using var scope = new TestConfigRootScope(rootPath);
        var service = CreateService();

        await service.RememberSuccessAsync("https://api.zarz.moe/v2/dl/tid", CancellationToken.None);
        var providers = await service.GetRotatedProviderRecordsAsync(CancellationToken.None);

        Assert.Equal(["https://api.zarz.moe/v2/dl/tid"], providers.Select(static provider => provider.Endpoint));
    }

    [Fact]
    public async Task GetRotatedProvidersAsync_UsesCleanCatalogNames()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "deezspotag-tests", Guid.NewGuid().ToString("N"));
        using var scope = new TestConfigRootScope(rootPath);
        var registry = new InMemoryProviderRegistry();
        var service = new TidalApiProviderSource(registry);

        _ = await service.GetRotatedProviderRecordsAsync(CancellationToken.None);
        var providers = await registry.GetProvidersAsync(CancellationToken.None);

        Assert.Equal(new[] { "zarz" }, providers.Select(provider => provider.DisplayName).ToArray());
    }

    [Fact]
    public async Task GetRotatedProvidersAsync_ExcludesDisabledProvider()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "deezspotag-tests", Guid.NewGuid().ToString("N"));
        using var scope = new TestConfigRootScope(rootPath);
        var registry = new InMemoryProviderRegistry();
        var service = new TidalApiProviderSource(registry);

        _ = await service.GetRotatedProviderRecordsAsync(CancellationToken.None);
        await registry.SetEnabledAsync("zarz", false, CancellationToken.None);
        var providers = await service.GetRotatedProviderRecordsAsync(CancellationToken.None);

        Assert.Empty(providers);
    }

    [Fact]
    public void PublicProviderDefinitions_DeclareCapabilitiesAndVerificationIndependently()
    {
        var provider = Assert.Single(TidalPublicProviderDefaults.Providers);

        Assert.True(provider.RequiresVerification);
        Assert.NotNull(provider.Capabilities);
        Assert.True(provider.Capabilities!.SupportsStereo);
        Assert.True(provider.Capabilities.SupportsAtmos);
        Assert.True(provider.Capabilities.SupportsManifests);
    }

    [Fact]
    public void TidalDownloadService_UsesProviderAdaptersWithoutProviderKindBranching()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var service = File.ReadAllText(Path.Combine(
            root,
            "DeezSpoTag.Services/Download/Tidal/TidalDownloadService.cs"));
        var adapters = File.ReadAllText(Path.Combine(
            root,
            "DeezSpoTag.Services/Download/Tidal/TidalPublicDownloadProviderAdapter.cs"));

        Assert.Contains("_publicProviderAdapters.Resolve(provider)", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Unsupported Tidal public provider kind", service, StringComparison.Ordinal);
        Assert.Contains("ITidalPublicDownloadProviderAdapter", adapters, StringComparison.Ordinal);
        Assert.Contains("ZarzTidalPublicDownloadProviderAdapter", adapters, StringComparison.Ordinal);
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
