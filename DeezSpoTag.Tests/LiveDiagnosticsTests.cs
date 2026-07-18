using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Conversion;
using DeezSpoTag.Services.Download.Identity;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LiveDiagnosticsTests
{
    private const string AppleWrapperLiveFlag = "DEEZSPOTAG_LIVE_APPLE_WRAPPER_TESTS";
    private const string ShazamLiveFlag = "DEEZSPOTAG_LIVE_SHAZAM_TESTS";
    private const string FfmpegLiveFlag = "DEEZSPOTAG_LIVE_FFMPEG_TESTS";
    private const string LibraryScanLiveFlag = "DEEZSPOTAG_LIVE_LIBRARY_SCAN_TESTS";
    private const string LibraryScanRootEnv = "DEEZSPOTAG_LIVE_LIBRARY_SCAN_ROOT";
    private const string IdentityResolverLiveFlag = "DEEZSPOTAG_LIVE_IDENTITY_RESOLVER_TESTS";
    private const string AppleLyricsLiveFlag = "DEEZSPOTAG_LIVE_APPLE_LYRICS_TESTS";
    private const string DataRootEnv = "DEEZSPOTAG_DATA_DIR";

    [Fact]
    public async Task FfmpegLiveTools_AreUsableWhenEnabled()
    {
        if (!IsEnabled(FfmpegLiveFlag))
        {
            return;
        }

        Assert.True(await RunToolVersionAsync("ffmpeg"), "ffmpeg must be executable for live conversion tests.");
        Assert.True(await RunToolVersionAsync("ffprobe"), "ffprobe must be executable for live audio validation tests.");
    }

    [Fact]
    public async Task ShazamLiveRuntime_IsUsableWhenEnabled()
    {
        if (!IsEnabled(ShazamLiveFlag))
        {
            return;
        }

        var python = FirstNonEmpty(Environment.GetEnvironmentVariable("SHAZAM_PYTHON"), "python3");
        Assert.True(
            await RunProcessAsync(python, "-c", "import shazamio"),
            "Shazam live tests require a Python runtime that can import shazamio.");
    }

    [Fact]
    public void AppleWrapperLiveTools_AreUsableWhenEnabled()
    {
        if (!IsEnabled(AppleWrapperLiveFlag))
        {
            return;
        }

        Assert.True(AppleExternalToolRunner.HasMp4Decrypt(), "Apple live tests require mp4decrypt.");
        Assert.True(AppleExternalToolRunner.HasMp4Box(), "Apple live tests require MP4Box.");
    }

    [Fact]
    public void LibraryScanLiveRoot_IsUsableWhenEnabled()
    {
        if (!IsEnabled(LibraryScanLiveFlag))
        {
            return;
        }

        var root = Environment.GetEnvironmentVariable(LibraryScanRootEnv);
        Assert.False(string.IsNullOrWhiteSpace(root), $"{LibraryScanRootEnv} must point to a real library folder.");
        Assert.True(Directory.Exists(root), $"{LibraryScanRootEnv} does not exist: {root}");
        Assert.NotEmpty(Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OfflineFailurePaths_ReturnControlledFailures()
    {
        var appleTools = new AppleExternalToolRunner(NullLogger<AppleExternalToolRunner>.Instance);
        Assert.False(await appleTools.RunMp4DecryptAsync("", "/missing/in.m4a", "/missing/out.m4a", CancellationToken.None));
        Assert.False(await AppleExternalToolRunner.HasAudioTrackAsync("/missing/audio.m4a", CancellationToken.None));

        var converter = new FfmpegConversionService(NullLogger<FfmpegConversionService>.Instance);
        var conversion = await converter.ConvertIfNeededAsync(
            "/missing/source.flac",
            "mp3",
            "320k",
            ConversionOptions.Default,
            CancellationToken.None);

        Assert.False(conversion.WasConverted);
        Assert.Equal("Input file not found.", conversion.Error);
    }

    [Fact]
    public async Task CentralIdentityResolver_AllPlatformsResolveLive()
    {
        if (!IsEnabled(IdentityResolverLiveFlag))
        {
            return;
        }

        var dataRoot = Environment.GetEnvironmentVariable(DataRootEnv);
        Assert.False(string.IsNullOrWhiteSpace(dataRoot), $"{DataRootEnv} must point to the configured application data directory.");
        Assert.True(Directory.Exists(dataRoot), $"{DataRootEnv} does not exist: {dataRoot}");

        await using var provider = BuildIdentityResolverProvider(dataRoot);
        var resolver = provider.GetRequiredService<ITrackIdentityResolver>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var request = new TrackIdentityResolutionRequest(
            SourcePlatform: null,
            SourceUrl: null,
            Title: "Blinding Lights",
            Artist: "The Weeknd",
            Album: "After Hours",
            Isrc: "USUG11904206",
            DurationMs: 200_040,
            TargetPlatforms: new[] { "spotify", "deezer", "apple", "qobuz", "tidal", "amazon" },
            Storefront: "ke",
            Language: "en-US");

        var result = await resolver.ResolveAsync(request, timeout.Token);
        WriteResolution(result);

        AssertResolved("Spotify", result.SpotifyId, result.SpotifyUrl, result.Candidates);
        AssertResolved("Deezer", result.DeezerId, result.DeezerUrl, result.Candidates);
        AssertResolved("Apple", result.AppleId, result.AppleUrl, result.Candidates);
        AssertResolved("Qobuz", result.QobuzId, result.QobuzUrl, result.Candidates);
        AssertResolved("Tidal", result.TidalId, result.TidalUrl, result.Candidates);
        AssertResolved("Amazon", result.AmazonId, result.AmazonUrl, result.Candidates);
        Assert.Equal("USUG11904206", NormalizeIsrc(result.Isrc));
    }

    [Fact]
    public async Task CentralIdentityResolver_SpotifyResolveLive()
    {
        if (!IsEnabled(IdentityResolverLiveFlag))
        {
            return;
        }

        var dataRoot = Environment.GetEnvironmentVariable(DataRootEnv);
        Assert.False(string.IsNullOrWhiteSpace(dataRoot), $"{DataRootEnv} must point to the configured application data directory.");
        Assert.True(Directory.Exists(dataRoot), $"{DataRootEnv} does not exist: {dataRoot}");

        await using var provider = BuildIdentityResolverProvider(dataRoot);
        var resolver = provider.GetRequiredService<ITrackIdentityResolver>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = new TrackIdentityResolutionRequest(
            SourcePlatform: null,
            SourceUrl: null,
            Title: "Blinding Lights",
            Artist: "The Weeknd",
            Album: "After Hours",
            Isrc: "USUG11904206",
            DurationMs: 200_040,
            TargetPlatforms: new[] { "spotify" },
            Storefront: "us",
            Language: "en-US");

        var result = await resolver.ResolveAsync(request, timeout.Token);
        WriteResolution(result);

        AssertResolved("Spotify", result.SpotifyId, result.SpotifyUrl, result.Candidates);
        Assert.Equal("0VjIjW4GlUZAMYd2vXMi3b", result.SpotifyId);
    }

    [Fact]
    public async Task CentralIdentityResolver_AppleResolveToleratesVariantIsrcLive()
    {
        if (!IsEnabled(IdentityResolverLiveFlag))
        {
            return;
        }

        var dataRoot = Environment.GetEnvironmentVariable(DataRootEnv);
        Assert.False(string.IsNullOrWhiteSpace(dataRoot), $"{DataRootEnv} must point to the configured application data directory.");
        Assert.True(Directory.Exists(dataRoot), $"{DataRootEnv} does not exist: {dataRoot}");

        await using var provider = BuildIdentityResolverProvider(dataRoot);
        var resolver = provider.GetRequiredService<ITrackIdentityResolver>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = new TrackIdentityResolutionRequest(
            SourcePlatform: "qobuz",
            SourceUrl: "https://play.qobuz.com/track/90957992",
            Title: "In Your Eyes",
            Artist: "The Weeknd",
            Album: "After Hours",
            Isrc: "USUG12000657",
            DurationMs: 238_000,
            SpotifyId: "7szuecWAPwGoV1e5vGu8tl",
            QobuzId: "90957992",
            TargetPlatforms: new[] { "apple" },
            Storefront: "us",
            Language: "en-US");

        var result = await resolver.ResolveAsync(request, timeout.Token);
        WriteResolution(result);

        AssertResolved("Apple", result.AppleId, result.AppleUrl, result.Candidates);
        Assert.Equal("After Hours", result.AppleAlbumName);
        Assert.Equal("The Weeknd", result.AppleArtistName);
    }

    [Fact]
    public async Task CentralIdentityResolver_SpotifyResolveMultipleTracksLive()
    {
        if (!IsEnabled(IdentityResolverLiveFlag))
        {
            return;
        }

        var dataRoot = Environment.GetEnvironmentVariable(DataRootEnv);
        Assert.False(string.IsNullOrWhiteSpace(dataRoot), $"{DataRootEnv} must point to the configured application data directory.");
        Assert.True(Directory.Exists(dataRoot), $"{DataRootEnv} does not exist: {dataRoot}");

        await using var provider = BuildIdentityResolverProvider(dataRoot);
        var resolver = provider.GetRequiredService<ITrackIdentityResolver>();
        var samples = new[]
        {
            new SpotifyIdentitySample("Blinding Lights", "The Weeknd", "After Hours", "USUG11904206", 200_040),
            new SpotifyIdentitySample("Shape of You", "Ed Sheeran", "÷ (Deluxe)", "GBAHS1600463", 233_713),
            new SpotifyIdentitySample("Starboy", "The Weeknd", "Starboy", "USUG11600976", 230_453),
            new SpotifyIdentitySample("As It Was", "Harry Styles", "Harry's House", "USSM12200612", 167_303),
            new SpotifyIdentitySample("Flowers", "Miley Cyrus", "Endless Summer Vacation", "USSM12209777", 200_600)
        };

        foreach (var sample in samples)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var request = new TrackIdentityResolutionRequest(
                SourcePlatform: null,
                SourceUrl: null,
                Title: sample.Title,
                Artist: sample.Artist,
                Album: sample.Album,
                Isrc: sample.Isrc,
                DurationMs: sample.DurationMs,
                TargetPlatforms: new[] { "spotify" },
                Storefront: "us",
                Language: "en-US");

            var started = Stopwatch.StartNew();
            var result = await resolver.ResolveAsync(request, timeout.Token);
            started.Stop();
            Console.WriteLine(
                $"SpotifyLiveSample title=\"{sample.Title}\" artist=\"{sample.Artist}\" id=\"{result.SpotifyId}\" url=\"{result.SpotifyUrl}\" elapsedMs={started.ElapsedMilliseconds}");
            WriteResolution(result);

            AssertResolved("Spotify", result.SpotifyId, result.SpotifyUrl, result.Candidates);
        }
    }

    [Fact]
    public async Task AppleLyrics_AuthenticatedThenPaxsenixFallbackReturnsOnlyValidFormatsLive()
    {
        if (!IsEnabled(AppleLyricsLiveFlag))
        {
            return;
        }

        var dataRoot = Environment.GetEnvironmentVariable(DataRootEnv);
        Assert.False(string.IsNullOrWhiteSpace(dataRoot), $"{DataRootEnv} must point to the configured application data directory.");
        Assert.True(Directory.Exists(dataRoot), $"{DataRootEnv} does not exist: {dataRoot}");

        await using var provider = BuildIdentityResolverProvider(dataRoot);
        var service = provider.GetRequiredService<LyricsService>();
        var settings = provider.GetRequiredService<DeezSpoTagSettingsService>().LoadSettings();
        settings.AppleMusic.MediaUserToken = string.Empty;
        settings.SyncedLyrics = true;
        settings.SaveLyrics = true;
        settings.LyricsFallbackEnabled = true;
        settings.LyricsFallbackOrder = "apple";
        settings.LrcType = "lyrics,syllable-lyrics,unsynced-lyrics";
        settings.LrcFormat = "ttml";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var timedTrack = new Track { Source = "apple", SourceId = "1550875220", Id = "1550875220" };
        var timed = await service.ResolveLyricsAsync(
            timedTrack,
            settings,
            timeout.Token);
        var directFallback = await ResolveApplePublicFallbackDirectlyAsync(service, timedTrack, timeout.Token);
        Assert.True(
            AppleLyricsService.IsTimedTtml(directFallback?.TtmlLyrics),
            $"Apple public fallback did not return timed TTML directly: {directFallback?.ErrorMessage}");
        Assert.True(
            AppleLyricsService.IsTimedTtml(timed?.TtmlLyrics),
            $"Known timed Apple track did not return timed TTML: {timed?.ErrorMessage}");
        await AssertTimedTtmlSidecarSavedAsync(service, timed!, settings, timeout.Token);

        var wabebe = await service.ResolveLyricsAsync(
            new Track { Source = "apple", SourceId = "270672927", Id = "270672927" },
            settings,
            timeout.Token);
        Assert.True(
            string.IsNullOrWhiteSpace(wabebe?.TtmlLyrics) || AppleLyricsService.IsTimedTtml(wabebe.TtmlLyrics),
            "Wabebe returned Apple timing=None TTML as rich lyrics.");
    }

    [Fact]
    public async Task DeezerProviderTimedJsonCreatesLrcSidecarLive()
    {
        if (!IsEnabled(AppleLyricsLiveFlag))
        {
            return;
        }

        var dataRoot = Environment.GetEnvironmentVariable(DataRootEnv);
        Assert.False(string.IsNullOrWhiteSpace(dataRoot), $"{DataRootEnv} must point to the configured application data directory.");
        Assert.True(Directory.Exists(dataRoot), $"{DataRootEnv} does not exist: {dataRoot}");

        await using var provider = BuildIdentityResolverProvider(dataRoot);
        var service = provider.GetRequiredService<LyricsService>();
        var settings = provider.GetRequiredService<DeezSpoTagSettingsService>().LoadSettings();
        settings.SyncedLyrics = true;
        settings.SaveLyrics = true;
        settings.LyricsFallbackEnabled = true;
        settings.LyricsFallbackOrder = "deezer";
        settings.LrcType = "lyrics";
        settings.LrcFormat = "lrc";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var track = new Track
        {
            Id = "deezer-live-lyrics-908604612",
            Source = "deezer",
            SourceId = "908604612",
            Title = "Blinding Lights",
            ArtistString = "The Weeknd",
            ISRC = "USUG11904206"
        };
        var lyrics = await service.ResolveLyricsAsync(track, settings, timeout.Token);

        Assert.True(lyrics?.IsSynced() == true, $"Deezer did not return synced lyrics: {lyrics?.ErrorMessage}");
        Assert.Equal(LyricsSourceFormat.ProviderSyncedJson, lyrics!.SyncedLyricsSourceFormat);
        Assert.True(lyrics.CanSaveLrcSidecar(), "Deezer provider timed JSON was not eligible for LRC sidecar creation.");

        await AssertLrcSidecarSavedAsync(service, lyrics, track, settings, timeout.Token);
    }

    [Fact]
    public async Task SpotifyProviderTimedJsonCreatesLrcSidecarLive()
    {
        if (!IsEnabled(AppleLyricsLiveFlag))
        {
            return;
        }

        var dataRoot = Environment.GetEnvironmentVariable(DataRootEnv);
        Assert.False(string.IsNullOrWhiteSpace(dataRoot), $"{DataRootEnv} must point to the configured application data directory.");
        Assert.True(Directory.Exists(dataRoot), $"{DataRootEnv} does not exist: {dataRoot}");

        await using var provider = BuildIdentityResolverProvider(dataRoot);
        var service = provider.GetRequiredService<LyricsService>();
        var settings = provider.GetRequiredService<DeezSpoTagSettingsService>().LoadSettings();
        settings.SyncedLyrics = true;
        settings.SaveLyrics = true;
        settings.LyricsFallbackEnabled = true;
        settings.LyricsFallbackOrder = "spotify";
        settings.LrcType = "lyrics";
        settings.LrcFormat = "lrc";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var track = new Track
        {
            Id = "spotify-live-lyrics-0VjIjW4GlUZAMYd2vXMi3b",
            Source = "spotify",
            SourceId = "0VjIjW4GlUZAMYd2vXMi3b",
            Title = "Blinding Lights",
            ArtistString = "The Weeknd",
            ISRC = "USUG11904206",
            Urls =
            {
                ["spotify_track_id"] = "0VjIjW4GlUZAMYd2vXMi3b"
            }
        };
        var lyrics = await service.ResolveLyricsAsync(track, settings, timeout.Token);

        Assert.True(lyrics?.IsSynced() == true, $"Spotify did not return synced lyrics: {lyrics?.ErrorMessage}");
        Assert.Equal(LyricsSourceFormat.ProviderSyncedJson, lyrics!.SyncedLyricsSourceFormat);
        Assert.True(lyrics.CanSaveLrcSidecar(), "Spotify provider timed JSON was not eligible for LRC sidecar creation.");

        await AssertLrcSidecarSavedAsync(service, lyrics, track, settings, timeout.Token);
    }

    [Fact]
    public async Task MusixmatchProviderTimedJsonCreatesEnhancedLrcSidecarLive()
    {
        if (!IsEnabled(AppleLyricsLiveFlag))
        {
            return;
        }

        var dataRoot = Environment.GetEnvironmentVariable(DataRootEnv);
        Assert.False(string.IsNullOrWhiteSpace(dataRoot), $"{DataRootEnv} must point to the configured application data directory.");
        Assert.True(Directory.Exists(dataRoot), $"{DataRootEnv} does not exist: {dataRoot}");

        await using var provider = BuildIdentityResolverProvider(dataRoot);
        var service = provider.GetRequiredService<LyricsService>();
        var settings = provider.GetRequiredService<DeezSpoTagSettingsService>().LoadSettings();
        settings.SyncedLyrics = true;
        settings.SaveLyrics = true;
        settings.LyricsFallbackEnabled = true;
        settings.LyricsFallbackOrder = "musixmatch";
        settings.LrcType = "lyrics,syllable-lyrics";
        settings.LrcFormat = "elrc";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var track = new Track
        {
            Id = "musixmatch-live-lyrics-aje",
            Source = "spotify",
            Title = "AJE",
            ArtistString = "Alikiba"
        };
        var lyrics = await service.ResolveLyricsAsync(track, settings, timeout.Token);

        Assert.True(lyrics?.IsSynced() == true, $"Musixmatch did not return synced lyrics: {lyrics?.ErrorMessage}");
        Assert.Equal(LyricsSourceFormat.ProviderSyncedJson, lyrics!.SyncedLyricsSourceFormat);
        Assert.True(lyrics.HasEnhancedSynchronizedLyrics(), "Musixmatch did not return richsync word timing eligible for .elrc creation.");

        await AssertLrcSidecarSavedAsync(service, lyrics, track, settings, timeout.Token, expectElrc: true);
    }

    [Fact]
    public async Task DeezerAndSpotifyLyricsResolveThroughCentralIdentityResolverLive()
    {
        if (!IsEnabled(AppleLyricsLiveFlag))
        {
            return;
        }

        var dataRoot = Environment.GetEnvironmentVariable(DataRootEnv);
        Assert.False(string.IsNullOrWhiteSpace(dataRoot), $"{DataRootEnv} must point to the configured application data directory.");
        Assert.True(Directory.Exists(dataRoot), $"{DataRootEnv} does not exist: {dataRoot}");

        await using var provider = BuildIdentityResolverProvider(dataRoot);
        var identityResolver = provider.GetRequiredService<ITrackIdentityResolver>();
        var lyricsService = provider.GetRequiredService<LyricsService>();
        var baseSettings = provider.GetRequiredService<DeezSpoTagSettingsService>().LoadSettings();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        var samples = new[]
        {
            new LyricsIdentitySample("Blinding Lights", "The Weeknd", "After Hours", "USUG11904206", 200_040)
        };

        var results = new List<string>();
        foreach (var sample in samples)
        {
            var identity = await identityResolver.ResolveAsync(
                new TrackIdentityResolutionRequest(
                    SourcePlatform: null,
                    SourceUrl: null,
                    Title: sample.Title,
                    Artist: sample.Artist,
                    Album: sample.Album,
                    Isrc: sample.Isrc,
                    DurationMs: sample.DurationMs,
                    TargetPlatforms: new[] { "deezer", "spotify" },
                    Storefront: "us",
                    Language: "en-US"),
                timeout.Token);

            WriteResolution(identity);

            var track = new Track
            {
                Id = $"lyrics-matrix-{NormalizeIsrc(sample.Isrc)}",
                Source = "local",
                Title = sample.Title,
                ArtistString = sample.Artist,
                ISRC = sample.Isrc,
                Duration = sample.DurationMs / 1000
            };

            if (!string.IsNullOrWhiteSpace(identity.DeezerId))
            {
                track.Urls["deezer_track_id"] = identity.DeezerId;
            }

            if (!string.IsNullOrWhiteSpace(identity.SpotifyId))
            {
                track.Urls["spotify_track_id"] = identity.SpotifyId;
            }

            results.Add(await ResolveLyricsMatrixProviderAsync(lyricsService, baseSettings, track, sample, "deezer", timeout.Token));
            results.Add(await ResolveLyricsMatrixProviderAsync(lyricsService, baseSettings, track, sample, "spotify", timeout.Token));
        }

        foreach (var result in results)
        {
            Console.WriteLine(result);
        }

        var details = string.Join(Environment.NewLine, results);
        Assert.True(
            results.Any(result => result.Contains("provider=deezer", StringComparison.OrdinalIgnoreCase)
                && result.Contains("synced=True", StringComparison.OrdinalIgnoreCase)),
            details);
        Assert.True(
            results.Any(result => result.Contains("provider=spotify", StringComparison.OrdinalIgnoreCase)
                && result.Contains("synced=True", StringComparison.OrdinalIgnoreCase)),
            details);
    }

    private static async Task<string> ResolveLyricsMatrixProviderAsync(
        LyricsService lyricsService,
        DeezSpoTagSettings baseSettings,
        Track track,
        LyricsIdentitySample sample,
        string provider,
        CancellationToken cancellationToken)
    {
        var settings = CloneLyricsSettings(baseSettings);
        settings.SyncedLyrics = true;
        settings.SaveLyrics = true;
        settings.LyricsFallbackEnabled = true;
        settings.LyricsFallbackOrder = provider;
        settings.LrcType = "lyrics";
        settings.LrcFormat = "lrc";

        var lyrics = await lyricsService.ResolveLyricsAsync(track, settings, cancellationToken);
        var synced = lyrics?.IsSynced() == true;
        var lineCount = lyrics?.SyncedLyrics?.Count ?? 0;
        var sourceFormat = lyrics?.SyncedLyricsSourceFormat.ToString() ?? "none";
        var error = lyrics?.ErrorMessage ?? string.Empty;
        return $"sample=\"{sample.Title}\" provider={provider} synced={synced} lines={lineCount} sourceFormat={sourceFormat} error=\"{error}\" deezerId={track.Urls.GetValueOrDefault("deezer_track_id")} spotifyId={track.Urls.GetValueOrDefault("spotify_track_id")}";
    }

    private static DeezSpoTagSettings CloneLyricsSettings(DeezSpoTagSettings source)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(source);
        return System.Text.Json.JsonSerializer.Deserialize<DeezSpoTagSettings>(json)
            ?? new DeezSpoTagSettings();
    }

    private static async Task AssertTimedTtmlSidecarSavedAsync(
        LyricsService service,
        LyricsBase lyrics,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        var directory = CreateLiveLyricsDirectory();
        try
        {
            await service.SaveLyricsAsync(
                lyrics,
                new Track { Id = "apple-live-ttml", Title = "Tuesday", ArtistString = "Burak Yeter" },
                BuildLyricsPaths(directory),
                settings,
                cancellationToken);

            var ttmlPath = Path.Join(directory, "track.ttml");
            Assert.True(File.Exists(ttmlPath), "Timed Apple TTML was resolved but no .ttml sidecar was saved.");
            var ttml = await File.ReadAllTextAsync(ttmlPath, cancellationToken);
            Assert.True(AppleLyricsService.IsTimedTtml(ttml), "Saved Apple .ttml sidecar is not timed TTML.");
            Assert.DoesNotContain("timing=\"None\"", ttml, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Join(directory, "track.lrc")), "TTML-only live test unexpectedly wrote an LRC sidecar.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertLrcSidecarSavedAsync(
        LyricsService service,
        LyricsBase lyrics,
        Track track,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken,
        bool expectElrc = false)
    {
        var directory = CreateLiveLyricsDirectory();
        try
        {
            await service.SaveLyricsAsync(lyrics, track, BuildLyricsPaths(directory), settings, cancellationToken);

            var lrcPath = Path.Join(directory, "track.lrc");
            if (expectElrc)
            {
                Assert.False(File.Exists(lrcPath), "ELRC-only live test unexpectedly wrote a standard .lrc sidecar.");
                var elrcPath = Path.Join(directory, "track.elrc");
                Assert.True(File.Exists(elrcPath), "Musixmatch richsync was resolved but no .elrc sidecar was saved.");
                var elrcLines = await File.ReadAllLinesAsync(elrcPath, cancellationToken);
                Assert.Contains(elrcLines, line => Regex.IsMatch(line, @"^\[\d{2}:\d{2}\.\d{2}\]<\d{2}:\d{2}\.\d{3}>.+"));
            }
            else
            {
                Assert.True(File.Exists(lrcPath), "Timed provider JSON was resolved but no .lrc sidecar was saved.");
                var lines = await File.ReadAllLinesAsync(lrcPath, cancellationToken);
                Assert.Contains(lines, line => Regex.IsMatch(line, @"^\[\d{2}:\d{2}\.\d{2}\].+"));
            }
            Assert.False(File.Exists(Path.Join(directory, "track.ttml")), "LRC-only live test unexpectedly wrote a TTML sidecar.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateLiveLyricsDirectory()
    {
        var directory = Path.Join(Path.GetTempPath(), $"deezspotag-live-lyrics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static (string FilePath, string Filename, string ExtrasPath, string CoverPath, string ArtistPath) BuildLyricsPaths(string directory)
        => (directory, "track", directory, directory, directory);

    private static async Task<LyricsBase?> ResolveApplePublicFallbackDirectlyAsync(
        LyricsService service,
        Track track,
        CancellationToken cancellationToken)
    {
        var method = typeof(LyricsService).GetMethod(
            "ResolvePaxsenixAppleLyricsByIdAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task<LyricsBase?>)(method.Invoke(service, [track, cancellationToken])
            ?? throw new InvalidOperationException("Apple public fallback invocation returned null task."));
        return await task;
    }

    private static ServiceProvider BuildIdentityResolverProvider(string dataRoot)
    {
        var webRoot = ResolveWebProjectRoot();
        var configuration = new ConfigurationBuilder()
            .SetBasePath(webRoot)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var environment = new LiveWebHostEnvironment(webRoot);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddLogging();
        services.AddHttpContextAccessor();

        var keyDirectory = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_PROTECTION_KEYS_DIR");
        if (string.IsNullOrWhiteSpace(keyDirectory))
        {
            keyDirectory = Path.Join(dataRoot, "security", "data-protection-keys");
        }
        Assert.True(Directory.Exists(keyDirectory), $"Data-protection keys are missing: {keyDirectory}");
        services.AddSingleton<IDataProtectionProvider>(DataProtectionProvider.Create(
            new DirectoryInfo(keyDirectory),
            builder => builder.SetApplicationName("DeezSpoTag")));
        services.Configure<QobuzApiConfig>(configuration.GetSection("Qobuz"));

        InvokeProgramRegistration("ConfigureCoreServices", services);
        InvokeProgramRegistration("RegisterApplicationServices", services, configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = false,
            ValidateOnBuild = false
        });
    }

    private static void InvokeProgramRegistration(string methodName, params object[] arguments)
    {
        var method = typeof(Program).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(null, arguments);
    }

    private static string ResolveWebProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Join(directory.FullName, "DeezSpoTag.Web", "DeezSpoTag.Web.csproj");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the DeezSpoTag.Web project root.");
    }

    private static void AssertResolved(
        string platform,
        string? id,
        string? url,
        IReadOnlyList<PlatformIdentityCandidate> candidates)
    {
        var candidate = candidates.LastOrDefault(item =>
            item.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase));
        Assert.False(
            string.IsNullOrWhiteSpace(id),
            $"{platform} ID was not resolved. Candidate: {FormatCandidate(candidate)}");
        Assert.True(
            Uri.TryCreate(url, UriKind.Absolute, out _),
            $"{platform} URL was not resolved. Candidate: {FormatCandidate(candidate)}");
        Assert.True(
            candidate?.Accepted == true,
            $"{platform} provider did not accept its result. Candidate: {FormatCandidate(candidate)}");
    }

    private static void WriteResolution(TrackIdentityResolution result)
    {
        Console.WriteLine($"ISRC={result.Isrc}");
        Console.WriteLine($"Spotify={result.SpotifyId} {result.SpotifyUrl}");
        Console.WriteLine($"Deezer={result.DeezerId} {result.DeezerUrl}");
        Console.WriteLine($"Apple={result.AppleId} {result.AppleUrl}");
        Console.WriteLine($"Qobuz={result.QobuzId} {result.QobuzUrl}");
        Console.WriteLine($"Tidal={result.TidalId} {result.TidalUrl}");
        Console.WriteLine($"Amazon={result.AmazonId} {result.AmazonUrl}");
        foreach (var candidate in result.Candidates)
        {
            Console.WriteLine($"Candidate={FormatCandidate(candidate)}");
        }
    }

    private static string FormatCandidate(PlatformIdentityCandidate? candidate)
        => candidate == null
            ? "none"
            : $"{candidate.Platform}:{candidate.Accepted}:{candidate.Source}:{candidate.Reason ?? "ok"}:score={candidate.Score}";

    private sealed record LyricsIdentitySample(
        string Title,
        string Artist,
        string Album,
        string Isrc,
        int DurationMs);

    private sealed record SpotifyIdentitySample(
        string Title,
        string Artist,
        string Album,
        string Isrc,
        int DurationMs);

    private static string? NormalizeIsrc(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static bool IsEnabled(string envName)
    {
        var value = Environment.GetEnvironmentVariable(envName);
        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstNonEmpty(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static async Task<bool> RunToolVersionAsync(string tool)
        => await RunProcessAsync(tool, "-version");

    private static async Task<bool> RunProcessAsync(string fileName, params string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private sealed class LiveWebHostEnvironment : IWebHostEnvironment
    {
        public LiveWebHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new PhysicalFileProvider(contentRootPath);
            WebRootPath = Path.Join(contentRootPath, "wwwroot");
            WebRootFileProvider = Directory.Exists(WebRootPath)
                ? new PhysicalFileProvider(WebRootPath)
                : new NullFileProvider();
        }

        public string ApplicationName { get; set; } = typeof(Program).Assembly.GetName().Name!;
        public IFileProvider WebRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
