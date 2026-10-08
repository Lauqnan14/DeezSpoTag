using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.SoundCloud;
using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Services.Library;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     The SoundCloud engine's public surface and its registration in the shared queue.
/// </summary>
/// <remarks>
///     Registration matters as much as the types here: the processor collection is resolved as a unit, so a
///     host that cannot satisfy one engine's dependencies takes the whole download queue down. These cases
///     resolve the collection the way a host does.
/// </remarks>
public sealed class SoundCloudApiSurfaceTest
{
    [Fact]
    public void EngineId_IsTheCanonicalCatalogId()
    {
        Assert.Equal("soundcloud", SoundCloudQueueItem.EngineId);
        Assert.Equal("soundcloud", DownloadSourceCatalog.NormalizeEngineName("soundcloud"));
        Assert.Equal("soundcloud", DownloadSourceCatalog.NormalizeEngineName("SoundCloud"));
        Assert.Contains(
            DownloadSourceCatalog.GetEngineOptions(),
            option => option.Value == "soundcloud" && option.Label == "SoundCloud");
    }

    [Fact]
    public void QueueItem_DefaultsToTheEngineAndSerializesItsOwnState()
    {
        var payload = new SoundCloudQueueItem();
        Assert.Equal(SoundCloudQueueItem.EngineId, payload.Engine);
        Assert.Equal(SoundCloudQueueItem.EngineId, payload.SourceService);

        payload.SoundCloudId = "42";
        payload.SourceUrl = "https://soundcloud.com/test-artist/test-track";
        payload.SoundCloudResolvedQuality = "HQ";
        payload.SoundCloudResolvedUrl = payload.SourceUrl;
        payload.SoundCloudAcquisitionStage = "audio_acquired";
        payload.Title = "Test Track";

        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        var round = System.Text.Json.JsonSerializer.Deserialize<SoundCloudQueueItem>(json);

        Assert.NotNull(round);
        Assert.Equal("42", round!.SoundCloudId);
        Assert.Equal("https://soundcloud.com/test-artist/test-track", round.SourceUrl);
        Assert.Equal("HQ", round.SoundCloudResolvedQuality);
        Assert.Equal("audio_acquired", round.SoundCloudAcquisitionStage);

        var queuePayload = payload.ToQueuePayload();
        Assert.Equal("soundcloud", queuePayload["engine"]);
        Assert.Equal("audio_acquired", queuePayload["soundCloudAcquisitionStage"]);
        Assert.Equal("HQ", queuePayload["soundCloudResolvedQuality"]);
    }

    [Fact]
    public void QualityCatalog_ExposesExactlySoundCloudsOwnThreeLossyTiers()
    {
        Assert.True(QualityCatalog.GetEngineQualityOptions().TryGetValue("soundcloud", out var options));
        var values = options!.Select(option => option.Value).ToArray();

        Assert.Equal(new[] { "HQ", "SQ", "LQ" }, values);

        // SoundCloud never advertises lossless, Hi-Res, or Atmos, so none of those may be selectable here.
        foreach (var forbidden in new[] { "FLAC", "LOSSLESS", "ALAC", "HI_RES", "DOLBY_ATMOS", "ATMOS" })
        {
            Assert.DoesNotContain(forbidden, values);
        }
    }

    /// <summary>
    ///     SoundCloud's own vocabulary normalizes case-insensitively, defaults to the standard stream, and can
    ///     never resolve to a lossless or Atmos tier however it is spelled.
    /// </summary>
    [Theory]
    [InlineData("HQ", "High")]
    [InlineData("hq", "High")]
    [InlineData("HIGH", "High")]
    [InlineData("SQ", "Standard")]
    [InlineData("sq", "Standard")]
    [InlineData("STANDARD", "Standard")]
    [InlineData("LQ", "Low")]
    [InlineData("lq", "Low")]
    [InlineData("", "Standard")]
    [InlineData("NOT_A_TIER", "Unknown")]
    [InlineData("FLAC", "Unknown")]
    [InlineData("LOSSLESS", "Unknown")]
    [InlineData("HI_RES", "Unknown")]
    [InlineData("DOLBY_ATMOS", "Unknown")]
    [InlineData("ATMOS", "Unknown")]
    public void QualityNormalization_UsesSoundCloudsOwnVocabulary(string code, string expectedTier)
    {
        Assert.Equal(expectedTier, NormalizeTier(code));
    }

    /// <summary>
    ///     A host that only calls the shared queue registration: no platform auth, no credential store, no
    ///     per-engine services added by the web tier.
    /// </summary>
    private static ServiceProvider BuildHostProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = "Data Source=:memory:",
                ["ConnectionStrings:Library"] = "Data Source=:memory:"
            })
            .Build();

        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton(NullLoggerFactory.Instance)
            .AddSingleton<LibraryRepository>(_ => new LibraryRepository(
                configuration,
                NullLogger<LibraryRepository>.Instance))
            .AddDeezSpoTagQueue()
            .BuildServiceProvider();
    }

    [Fact]
    public void TheSoundCloudProcessorIsActivatableFromTheSharedQueueRegistration()
    {
        using var provider = BuildHostProvider();

        var processor = ActivatorUtilities.CreateInstance<SoundCloudEngineProcessor>(provider);

        Assert.Equal("soundcloud", processor.Engine);
    }

    [Fact]
    public void EverySoundCloudServiceResolvesFromTheSharedQueueRegistration()
    {
        using var provider = BuildHostProvider();

        Assert.NotNull(provider.GetRequiredService<ISoundCloudClient>());
        Assert.NotNull(provider.GetRequiredService<SoundCloudDownloadService>());

        // A host without a protected credential store keeps public-only operation instead of failing.
        Assert.IsType<NullSoundCloudCredentialProvider>(
            provider.GetRequiredService<ISoundCloudCredentialProvider>());
    }

    [Fact]
    public void TheProcessorIsRegisteredForEveryHostThatRunsTheQueue()
    {
        // Checked on the registrations rather than by resolving the whole collection: the other engines'
        // services come from their own registration paths, so a bare queue-only host cannot activate them all.
        // What matters here is that SoundCloud's processor is declared alongside Soulseek's in the shared
        // block, which is what makes it resolvable in the workers host.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDeezSpoTagQueue();

        var registered = services
            .Where(descriptor => descriptor.ServiceType == typeof(IQueueEngineProcessor))
            .Select(descriptor => descriptor.ImplementationType)
            .ToList();

        Assert.Contains(typeof(SoundCloudEngineProcessor), registered);
        Assert.Contains(typeof(SoulseekEngineProcessor), registered);
    }

    /// <summary>
    ///     Normalizes a SoundCloud quality code and renders the resulting tier's name.
    /// </summary>
    private static string NormalizeTier(string code)
    {
        var assembly = typeof(QualityCatalog).Assembly;
        var qualityType = assembly.GetType(
            "DeezSpoTag.Services.Download.SoundCloud.SoundCloudStereoQuality",
            throwOnError: true)!;
        var method = qualityType.GetMethod(
            "Normalize",
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!;
        var tier = method.Invoke(null, new object?[] { code })!;
        return tier.ToString()!;
    }

    /// <summary>
    ///     Builds the shared delivered-audio fact type and asks a quality tier whether it accepts it.
    /// </summary>
    /// <remarks>
    ///     Fully reflective because both <c>SoundCloudStereoQualityTier</c> and <c>ActualAudioQuality</c> are
    ///     internal to the Services assembly, which is the same approach the existing quality-order guardrail
    ///     takes. The enum is named by string rather than referenced, so making the type public is not required
    ///     just to test it.
    /// </remarks>
    [Theory]
    [InlineData("Low", 64, true)]
    [InlineData("Low", 32, false)]
    [InlineData("Standard", 128, true)]
    [InlineData("Standard", 96, false)]
    [InlineData("High", 320, true)]
    [InlineData("High", 128, false)]
    public void EveryTierCarriesABitrateFloor(string tierName, int bitrateKbps, bool expected)
    {
        var assembly = typeof(QualityCatalog).Assembly;
        var tierType = assembly.GetType(
            "DeezSpoTag.Services.Download.SoundCloud.SoundCloudStereoQualityTier",
            throwOnError: true)!;
        var tier = Enum.Parse(tierType, tierName);

        var actualType = assembly.GetType(
            "DeezSpoTag.Services.Download.Shared.ActualAudioQuality",
            throwOnError: true)!;
        var actual = Activator.CreateInstance(
            actualType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { $"MP3 {bitrateKbps} kbps", 0, 44100, bitrateKbps, false },
            culture: null)!;

        var qualityType = assembly.GetType(
            "DeezSpoTag.Services.Download.SoundCloud.SoundCloudStereoQuality",
            throwOnError: true)!;
        var method = qualityType.GetMethod(
            "Accepts",
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!;

        Assert.Equal(expected, (bool)method.Invoke(null, new object?[] { tier, actual })!);
    }
}