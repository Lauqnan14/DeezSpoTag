using System;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.SoundCloud;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     SoundCloud's place in the settings catalog, the quality ladder, and the fallback resolution path.
/// </summary>
/// <remarks>
///     These are the contracts a reader's saved settings depend on. They are checked here rather than in the
///     queue tests because a change that silently drops a saved engine or reorders the ladder is exactly the
///     kind of regression that only shows up after someone upgrades.
/// </remarks>
public sealed class SoundCloudIntentAndFallbackTest
{
    [Fact]
    public void Catalog_OffersSoundCloudAsANormalEngine()
    {
        Assert.Equal("soundcloud", DownloadSourceCatalog.NormalizeEngineName("SoundCloud"));
        Assert.Equal("soundcloud", DownloadSourceCatalog.NormalizeEngineName(" SOUNDCLOUD "));

        Assert.Contains(
            DownloadSourceCatalog.GetSettingsSourceOptions(),
            option => option.Value == "soundcloud" && option.Label == "SoundCloud");

        // Auto and Custom still lead the list; SoundCloud joins the engines rather than replacing one.
        var settingsOptions = DownloadSourceCatalog.GetSettingsSourceOptions();
        Assert.Equal("auto", settingsOptions.First().Value);
        Assert.Equal("custom", settingsOptions[1].Value);
    }

    [Fact]
    public void DefaultEngineOrder_ListsSoundCloudWithItsThreeLossyTiers()
    {
        var defaults = DownloadEngineOrderSettings.CreateDefault();
        var soundCloud = Assert.Single(defaults.Engines.Where(engine => engine.Engine == "soundcloud"));

        Assert.True(soundCloud.Enabled);
        Assert.Equal(
            new[] { "HQ", "SQ", "LQ" },
            soundCloud.Qualities.Select(quality => quality.Quality).ToArray());
        Assert.All(soundCloud.Qualities, quality => Assert.True(quality.Enabled));

        // No Atmos and no lossless tier: SoundCloud does not advertise either.
        Assert.DoesNotContain(
            soundCloud.Qualities,
            quality => quality.Quality.Contains("ATMOS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AutoLadder_WalksSoundCloudAtItsLossyBitratePositions()
    {
        var settings = new DeezSpoTagSettings { Service = "auto" };
        var ladder = DownloadSourceOrder.ResolveQualityAutoSources(
            settings,
            includeDeezer: true,
            targetQuality: null)
            .Select(DownloadSourceOrder.DecodeAutoSource)
            .ToList();

        var hqIndex = ladder.FindIndex(step => step.Source == "soundcloud" && step.Quality == "HQ");
        var sqIndex = ladder.FindIndex(step => step.Source == "soundcloud" && step.Quality == "SQ");
        var lqIndex = ladder.FindIndex(step => step.Source == "soundcloud" && step.Quality == "LQ");

        Assert.True(hqIndex >= 0 && sqIndex >= 0 && lqIndex >= 0, "All three SoundCloud tiers must be on the ladder.");

        // HQ is the 256kbps Go+ stream: the highest SoundCloud advertises, so it ranks with the 320kbps
        // steps and ahead of the sub-256 rungs.
        var mp3_320Index = ladder.FindIndex(step => step.Quality == "MP3_320");
        Assert.True(hqIndex < mp3_320Index, "SoundCloud HQ must be tried before a 320kbps peer step.");

        // SQ is the 128kbps tier, beside the other 128kbps steps.
        var deezer128Index = ladder.FindIndex(step => step.Quality == "1");
        Assert.True(sqIndex > deezer128Index);

        // LQ is 64kbps, worse than every other rung, so it is last.
        Assert.Equal(ladder.Count - 1, lqIndex);
    }

    [Fact]
    public void AutoLadder_NeverPlacesSoundCloudInTheAtmosLadder()
    {
        var settings = new DeezSpoTagSettings { Service = "auto" };
        var atmos = DownloadSourceOrder.ResolveQualityAutoSources(
            settings,
            includeDeezer: true,
            targetQuality: "DOLBY_ATMOS")
            .Select(DownloadSourceOrder.DecodeAutoSource)
            .ToList();

        Assert.NotEmpty(atmos);
        Assert.DoesNotContain(atmos, step => step.Source == "soundcloud");
    }

    [Fact]
    public void SoundCloudIsAlwaysAvailableAsASource()
    {
        // Public tracks need no configuration, so SoundCloud must not be filtered out of the ladder for a
        // missing credential. A saved token only adds private tracks and the hq stream.
        var settings = new DeezSpoTagSettings { Service = "custom" };
        settings.DownloadEngineOrder = DownloadEngineOrderSettings.CreateDefault();
        settings.DownloadEngineOrder.Enabled = true;

        var ladder = DownloadSourceOrder.ResolveQualityAutoSources(
            settings,
            includeDeezer: true,
            targetQuality: null)
            .Select(DownloadSourceOrder.DecodeAutoSource)
            .ToList();

        Assert.Contains(ladder, step => step.Source == "soundcloud");
    }

    [Fact]
    public void Normalization_GivesAPreSoundCloudSavedOrderAValidSoundCloudEntry()
    {
        // An order saved before SoundCloud existed: no SoundCloud entry at all.
        var saved = new DownloadEngineOrderSettings
        {
            Enabled = true,
            Engines =
            [
                new DownloadEngineOrderItem { Engine = "qobuz", Enabled = true, Qualities = [Quality("5", true), Quality("6", false)] },
                new DownloadEngineOrderItem { Engine = "deezer", Enabled = false, Qualities = [] }
            ]
        };

        var normalized = DownloadSourceOrder.NormalizeDownloadEngineOrderSettings(saved);
        var soundCloud = normalized.Engines.SingleOrDefault(engine => engine.Engine == "soundcloud");

        Assert.NotNull(soundCloud);
        Assert.False(soundCloud!.Enabled);

        // The existing entries keep their position and their enabled flags.
        Assert.Equal(new[] { "qobuz", "deezer" }, normalized.Engines.Take(2).Select(e => e.Engine));
        var qobuz = normalized.Engines[0];
        Assert.True(qobuz.Enabled);
        Assert.False(normalized.Engines[1].Enabled);

        // The reader's own quality choices survive normalization untouched, in their order and with their
        // flags. Normalization does append the engine's remaining default qualities behind these - that is
        // existing behaviour for every engine and is what keeps a saved order valid as the catalog grows - so
        // the assertion is on the prefix the reader actually chose.
        Assert.Equal(
            new[] { ("5", true), ("6", false) },
            qobuz.Qualities.Take(2).Select(q => (q.Quality, q.Enabled)).ToArray());

        // Every engine the saved order did not mention is appended, disabled, after the ones it did.
        Assert.All(normalized.Engines.Skip(2), engine => Assert.False(engine.Enabled));

        // The appended entry carries the engine's own three tiers, none of them silently enabled.
        Assert.Equal(new[] { "HQ", "SQ", "LQ" }, soundCloud.Qualities.Select(q => q.Quality).ToArray());
        Assert.All(soundCloud.Qualities, quality => Assert.False(quality.Enabled));
    }

    [Fact]
    public void Normalization_LeavesAnOrderThatAlreadyHasSoundCloudAlone()
    {
        var withSoundCloud = DownloadEngineOrderSettings.CreateDefault();
        var soundCloud = withSoundCloud.Engines.Single(engine => engine.Engine == "soundcloud");
        soundCloud.Enabled = false;
        soundCloud.Qualities[0].Enabled = false;

        var normalized = DownloadSourceOrder.NormalizeDownloadEngineOrderSettings(withSoundCloud);
        var result = normalized.Engines.Single(engine => engine.Engine == "soundcloud");

        // A user who switched SoundCloud off keeps it off.
        Assert.False(result.Enabled);
        Assert.False(result.Qualities[0].Enabled);
    }

    [Fact]
    public void FallbackRequestCarriesTheSoundCloudPermalinkWithoutBreakingExistingCallers()
    {
        // The new parameter is optional and trailing, so every existing construction still compiles. This
        // test pins that: a SoundCloud field added in the middle of the record would silently reshuffle every
        // positional construction in the codebase.
        var constructor = typeof(EngineFallbackSearchRequest)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single();

        var parameters = constructor.GetParameters();
        var soundCloud = parameters.Single(parameter => parameter.Name == "SoundCloudUrl");

        Assert.True(soundCloud.IsOptional);
        Assert.Equal(parameters.Length - 1, soundCloud.Position);
    }

    [Fact]
    public void ServiceUrlMatch_RecognisesASoundCloudTrackButNotASet()
    {
        // Reached through the intent service's own matcher, which is what decides whether a step counts as
        // already resolved or needs a search.
        var isServiceUrlMatch = ResolveIsServiceUrlMatch();

        Assert.True(isServiceUrlMatch("https://soundcloud.com/artist/track", "soundcloud"));
        Assert.False(isServiceUrlMatch("https://soundcloud.com/artist/sets/some-set", "soundcloud"));
        Assert.False(isServiceUrlMatch("https://example.com/artist/track", "soundcloud"));
    }

    /// <summary>
    ///     Calls the intent service's private URL matcher by reflection.
    /// </summary>
    /// <remarks>
    ///     Done reflectively because the matcher is private and the intent service is a large web-tier type.
    ///     Asserting through it rather than reimplementing the rule is what keeps this test honest.
    /// </remarks>
    private static Func<string, string, bool> ResolveIsServiceUrlMatch()
    {
        var type = typeof(DeezSpoTag.Web.Services.DownloadIntentService);
        var method = type.GetMethod(
            "IsServiceUrlMatch",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (url, engine) => (bool)method!.Invoke(null, new object[] { url, engine })!;
    }

    private static DownloadEngineQualityItem Quality(string value, bool enabled)
        => new() { Quality = value, Enabled = enabled };
}