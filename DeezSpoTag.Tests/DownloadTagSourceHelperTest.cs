using DeezSpoTag.Services.Download.Shared;
using Xunit;
using System;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Services.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DeezSpoTag.Tests;

public sealed class DownloadTagSourceHelperTest
{
    [Fact]
    public void AtmosEngines_RegisterCatalogMetadataResolversForFollowEngine()
    {
        var services = new ServiceCollection();
        var program = typeof(DeezSpoTag.Web.Program);
        program.GetMethod("RegisterApplicationServices", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { services, new ConfigurationBuilder().Build() });
        // Existing resolvers have concrete implementation types. The three catalog adapters
        // are registered through factories, sharing the existing provider services.
        using var provider = new ServiceCollection().BuildServiceProvider();
        var catalogSources = services.Where(d => d.ServiceType == typeof(IMetadataResolver) && d.ImplementationFactory != null)
            .Select(d => ((IMetadataResolver)d.ImplementationFactory!(provider)).SourceKey).ToArray();
        Assert.Contains("tidal", catalogSources);
        Assert.Contains("apple", catalogSources);
        Assert.Contains("amazon", catalogSources);
    }
    [Theory]
    [InlineData("engine", "engine")]
    [InlineData("spotify", "spotify")]
    [InlineData("deezer", "deezer")]
    [InlineData("apple", "apple")]
    [InlineData("qobuz", "qobuz")]
    [InlineData("tidal", "tidal")]
    [InlineData("amazon", "amazon")]
    [InlineData("unknown", "deezer")]
    public void NormalizeStoredSource_ReturnsExpectedValue(string input, string expected)
    {
        var actual = DownloadTagSourceHelper.NormalizeStoredSource(input, DownloadTagSourceHelper.DeezerSource);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("deezer", "deezer")]
    [InlineData("spotify", "spotify")]
    [InlineData("apple", "apple")]
    [InlineData("itunes", "apple")]
    [InlineData("qobuz", "qobuz")]
    [InlineData("tidal", "tidal")]
    [InlineData("amazon", "amazon")]
    [InlineData(null, null)]
    public void NormalizeResolvedDownloadTagSource_ReturnsExpectedValue(string? input, string? expected)
    {
        var actual = DownloadTagSourceHelper.NormalizeResolvedDownloadTagSource(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolveDownloadTagSource_UsesExplicitSourceBeforeEngineCandidates()
    {
        var actual = DownloadTagSourceHelper.ResolveDownloadTagSource("spotify", "deezer", "qobuz");

        Assert.Equal("spotify", actual);
    }

    [Fact]
    public void ResolveDownloadTagSource_UsesFirstSupportedEngineCandidate_WhenConfiguredToFollowEngine()
    {
        var actual = DownloadTagSourceHelper.ResolveDownloadTagSource("engine", "apple", "qobuz", "deezer");

        Assert.Equal("apple", actual);
    }

    [Fact]
    public void ResolveDownloadTagSource_ResolvesNonSpotifyNonDeezerEngines_WhenConfiguredToFollowEngine()
    {
        var actual = DownloadTagSourceHelper.ResolveDownloadTagSource("engine", "apple", "tidal", "amazon");

        Assert.Equal("apple", actual);
    }

    [Fact]
    public void ResolveDownloadTagSource_ReturnsNull_WhenNoSupportedEngineCandidateExists()
    {
        var actual = DownloadTagSourceHelper.ResolveDownloadTagSource("engine", "unknown");

        Assert.Null(actual);
    }
}
