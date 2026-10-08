using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Which sources a queued item is allowed to fall back to.
/// </summary>
/// <remarks>
///     <para>
///         A download queued from the Soulseek tab is a choice of an exact peer file and quality. Its plan
///         contains only that step. The former cross-engine and same-engine quality ladders both violated it.
///     </para>
///     <para>
///         Everything else keeps the ladder. A library, playlist or other-platform download is free to walk
///         the ladder, and Soulseek is one of its steps, because a download that reaches Soulseek that way is
///         still that download and still walks on.
///     </para>
/// </remarks>
public sealed class SoulseekQueueEngineStickinessTest
{
    private static readonly MethodInfo ResolveVisibleSourcesMethod =
        typeof(DownloadIntentService).GetMethod(
            "ResolveVisiblePreResolutionSources",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DownloadIntentService.ResolveVisiblePreResolutionSources not found.");

    private static IReadOnlyList<string> Resolve(DownloadIntent intent, string engine, DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings? settings = null)
        => (IReadOnlyList<string>)ResolveVisibleSourcesMethod.Invoke(
            null,
            [intent, settings ?? Settings(), engine, "FLAC", "stereo"])!;

    /// <summary>
    ///     The user's own configuration: several sources enabled, which is the case the leak happened in.
    /// </summary>
    private static DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings Settings()
        => new()
        {
            Service = "auto",
            TidalQuality = "HI_RES",
            QobuzQuality = "7"
        };

    [Fact]
    public void AQueueFromTheSoulseekTabKeepsOnlyTheSelectedFileQuality()
    {
        var sources = Resolve(new DownloadIntent
        {
            SourceService = "soulseek",
            PreferredEngine = "soulseek",
            Title = "Roygbiv",
            Artist = "Boards of Canada",
            ContentType = "stereo",
            Quality = "FLAC",
            SoulseekUsername = "chosen-peer",
            SoulseekRemotePath = "Album/Roygbiv.flac"
        }, "soulseek");

        Assert.Equal(["soulseek|FLAC"], sources);
    }

    [Fact]
    public void AQueueFromTheSoulseekTabDoesNotWalkOtherEnabledSoulseekQualities()
    {
        // Source enables which files can be chosen, but selecting one FLAC file does not authorize a later
        // FLAC_HI_RES file from another peer if this transfer fails.
        var settings = Settings();
        settings.Service = "auto";

        // The tick boxes are the reader's statement of which qualities are acceptable, and they are only read
        // when the engine-order selection is switched on. Without that, the full ladder is the answer - which
        // is the "all eight" this must not be.
        settings.DownloadEngineOrder.Enabled = true;
        var soulseek = settings.DownloadEngineOrder.Engines
            .First(engine => engine.Engine.Contains("soulseek", System.StringComparison.OrdinalIgnoreCase));
        foreach (var quality in soulseek.Qualities)
        {
            quality.Enabled = quality.Quality is "FLAC_HI_RES" or "FLAC";
        }

        var sources = Resolve(
            new DownloadIntent
            {
                SourceService = "soulseek",
                PreferredEngine = "soulseek",
                Title = "Roygbiv",
                Artist = "Boards of Canada",
                ContentType = "stereo",
                Quality = "FLAC",
                SoulseekUsername = "chosen-peer",
                SoulseekRemotePath = "Album/Roygbiv.flac"
            },
            "soulseek",
            settings);

        Assert.Equal(["soulseek|FLAC"], sources);
    }

    [Fact]
    public void AQueueFromAnotherPlatformKeepsTheCrossEngineLadder()
    {
        // The engine argument is the engine the item is being enqueued for; the source service is where it
        // came from. A Spotify-driven item is enqueued for whichever engine the ladder picks, and the ladder -
        // Soulseek steps included - is what it must still get.
        var intent = new DownloadIntent
        {
            SourceService = "spotify",
            PreferredEngine = "spotify",
            Title = "Roygbiv",
            Artist = "Boards of Canada",
            ContentType = "stereo"
        };

        var sources = Resolve(intent, "spotify");

        Assert.NotEmpty(sources);

        // The ladder is not narrowed to one engine by the Soulseek rule: several sources remain reachable.
        var engines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var separator = source.IndexOf('|');
            engines.Add(separator > 0 ? source[..separator].Trim() : source.Trim());
        }

        Assert.True(
            engines.Count > 1,
            $"A Spotify-driven queue must keep the ladder, but it resolved to only: {string.Join(", ", engines)}");
    }
}
