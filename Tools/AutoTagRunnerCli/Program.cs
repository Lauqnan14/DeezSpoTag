using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Http;
using DeezSpoTag.Web.Services.AutoTag;
using TagLib;
using TagLib.Id3v2;
using IOFile = System.IO.File;

internal static class Program
{
    private static readonly string[] SupportedExtensions = { ".mp3", ".flac", ".m4a", ".mp4" };
    private static readonly string DefaultE2eSource = Path.Join(Directory.GetCurrentDirectory(), "sample.mp3");

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && (args[0].Equals("spotify-e2e-url", StringComparison.OrdinalIgnoreCase) || args[0].Equals("--spotify-e2e-url", StringComparison.OrdinalIgnoreCase)))
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Missing Spotify track URL.");
                return 2;
            }

            var trackUrl = args[1];
            var sourceFile = args.Length > 2 ? args[2] : DefaultE2eSource;
            return await RunSpotifyE2EUrlAsync(trackUrl, sourceFile);
        }

        if (args.Length > 0 && (args[0].Equals("spotify-e2e", StringComparison.OrdinalIgnoreCase) || args[0].Equals("--spotify-e2e", StringComparison.OrdinalIgnoreCase)))
        {
            var sourceFile = args.Length > 1 ? args[1] : DefaultE2eSource;
            return await RunSpotifyE2EAsync(sourceFile);
        }

        var root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        if (!Directory.Exists(root))
        {
            Console.WriteLine($"Missing folder: {root}");
            return 2;
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSimpleConsole(o => { o.SingleLine = true; }).SetMinimumLevel(LogLevel.Information));
        services.AddHttpClient();
        RegisterSharedAutoTagServices(services);

        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("autotag-cli");

        var files = EnumerateFiles(root).ToList();
        if (files.Count == 0)
        {
            Console.WriteLine("No audio files found.");
            return 3;
        }

        var matchingConfig = new AutoTagMatchingConfig
        {
            MatchDuration = false,
            MaxDurationDifferenceSeconds = 30,
            Strictness = 0.7,
            MultipleMatches = MultipleMatchesSort.Default
        };

        var musicBrainzConfig = new MusicBrainzMatchConfig();
        var beatportConfig = new BeatportMatchConfig();
        var beatsourceConfig = new BeatsourceMatchConfig();
        var bpmConfig = new BpmSupremeConfig();
        var itunesConfig = new ItunesMatchConfig();
        var deezerConfig = new DeezerConfig();
        var discogsConfig = new DiscogsConfig();

        var platforms = new List<string>
        {
            "musicbrainz",
            "beatport",
            "discogs",
            "traxsource",
            "junodownload",
            "bandcamp",
            "beatsource",
            "bpmsupreme",
            "itunes",
            "deezer",
            "musixmatch"
        };

        var results = new Dictionary<string, int>();
        foreach (var p in platforms) results[p] = 0;

        foreach (var file in files)
        {
            var info = BuildAudioInfo(file);
            logger.LogInformation("Testing {File}", file);

            foreach (var platform in platforms)
            {
                try
                {
                    AutoTagMatchResult? match = platform switch
                    {
                        "musicbrainz" => await provider.GetRequiredService<MusicBrainzMatcher>().MatchAsync(info, matchingConfig, musicBrainzConfig, CancellationToken.None),
                        "beatport" => await provider.GetRequiredService<BeatportMatcher>().MatchAsync(info, matchingConfig, beatportConfig, includeReleaseMeta: false, matchById: false, CancellationToken.None),
                        "discogs" => await provider.GetRequiredService<DiscogsMatcher>().MatchAsync(info, matchingConfig, discogsConfig, matchById: false, needsLabelOrCatalog: false, CancellationToken.None),
                        "traxsource" => await provider.GetRequiredService<TraxsourceMatcher>().MatchAsync(info, matchingConfig, extend: false, albumMeta: false, CancellationToken.None),
                        "junodownload" => await provider.GetRequiredService<JunoDownloadMatcher>().MatchAsync(info, matchingConfig, CancellationToken.None),
                        "bandcamp" => await provider.GetRequiredService<BandcampMatcher>().MatchAsync(info, matchingConfig, CancellationToken.None),
                        "beatsource" => await provider.GetRequiredService<BeatsourceMatcher>().MatchAsync(info, matchingConfig, beatsourceConfig, CancellationToken.None),
                        "bpmsupreme" => await provider.GetRequiredService<BpmSupremeMatcher>().MatchAsync(info, matchingConfig, bpmConfig, CancellationToken.None),
                        "itunes" => await provider.GetRequiredService<ItunesMatcher>().MatchAsync(info, matchingConfig, itunesConfig, CancellationToken.None),
                        "deezer" => await provider.GetRequiredService<DeezerMatcher>().MatchAsync(info, matchingConfig, deezerConfig, CancellationToken.None),
                        "musixmatch" => await provider.GetRequiredService<MusixmatchMatcher>().MatchAsync(info, CancellationToken.None),
                        _ => null
                    };

                    if (match != null)
                    {
                        results[platform] += 1;
                        logger.LogInformation("{Platform} matched: {Title} - {Artist} ({Accuracy:P0})", platform, match.Track.Title, string.Join(", ", match.Track.Artists), match.Accuracy);
                    }
                    else
                    {
                        logger.LogInformation("{Platform} no match", platform);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "{Platform} error", platform);
                }
            }
        }

        Console.WriteLine("\nSummary (matches per platform):");
        foreach (var kvp in results)
        {
            Console.WriteLine($"{kvp.Key}: {kvp.Value}");
        }

        return 0;
    }

    private static async Task<int> RunSpotifyE2EAsync(string sourceFile)
    {
        EnsureWorkersDataEnvironment();

        if (!IOFile.Exists(sourceFile))
        {
            Console.WriteLine($"Missing test file: {sourceFile}");
            return 2;
        }

        var sessionDir = Path.Join("/tmp/autotag-e2e-spotify", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(sessionDir);
        var targetFile = Path.Join(sessionDir, Path.GetFileName(sourceFile));
        IOFile.Copy(sourceFile, targetFile, overwrite: true);

        var beforeTags = DumpTags(targetFile);
        var beforePath = Path.Join(sessionDir, "before-tags.json");
        await IOFile.WriteAllTextAsync(beforePath, JsonSerializer.Serialize(beforeTags, new JsonSerializerOptions { WriteIndented = true }));

        var configPath = await BuildSpotifyE2EConfigAsync(sessionDir);
        var provider = BuildRunnerProvider();
        var runner = provider.GetRequiredService<LocalAutoTagRunner>();
        var statusEvents = new List<TaggingStatusWrap>();
        var logs = new List<string>();

        var result = await runner.RunAsync(
            Guid.NewGuid().ToString("N"),
            sessionDir,
            configPath,
            status =>
            {
                lock (statusEvents)
                {
                    statusEvents.Add(new TaggingStatusWrap
                    {
                        Platform = status.Platform,
                        Progress = status.Progress,
                        Status = status.Status == null
                            ? null
                            : new TaggingStatus
                            {
                                Status = status.Status.Status,
                                Path = status.Status.Path,
                                Message = status.Status.Message,
                                Accuracy = status.Status.Accuracy
                            }
                    });
                }
            },
            log =>
            {
                lock (logs)
                {
                    logs.Add(log);
                }
            },
            null,
            CancellationToken.None);

        var afterTags = DumpTags(targetFile);
        var afterPath = Path.Join(sessionDir, "after-tags.json");
        await IOFile.WriteAllTextAsync(afterPath, JsonSerializer.Serialize(afterTags, new JsonSerializerOptions { WriteIndented = true }));

        var diff = DiffTags(beforeTags, afterTags);
        var diffPath = Path.Join(sessionDir, "tag-diff.json");
        await IOFile.WriteAllTextAsync(diffPath, JsonSerializer.Serialize(diff, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"run_success={result.Success}");
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            Console.WriteLine($"run_error={result.Error}");
        }

        var matchLines = statusEvents
            .Where(e => e.Status != null && !string.IsNullOrWhiteSpace(e.Status.Path))
            .Select(e => $"{e.Platform}:{e.Status!.Status}:{Path.GetFileName(e.Status.Path)}:{e.Status.Accuracy?.ToString("P0") ?? "-"}")
            .Distinct()
            .ToList();
        Console.WriteLine("status_events:");
        foreach (var line in matchLines)
        {
            Console.WriteLine($"  {line}");
        }

        Console.WriteLine("changed_tags:");
        foreach (var item in diff.Changed)
        {
            Console.WriteLine($"  ~ {item.Key} :: '{item.Before}' => '{item.After}'");
        }

        Console.WriteLine("added_tags:");
        foreach (var item in diff.Added)
        {
            Console.WriteLine($"  + {item.Key} :: '{item.After}'");
        }

        Console.WriteLine("removed_tags:");
        foreach (var item in diff.Removed)
        {
            Console.WriteLine($"  - {item.Key} :: '{item.Before}'");
        }

        Console.WriteLine($"artifacts_dir={sessionDir}");
        Console.WriteLine($"before_json={beforePath}");
        Console.WriteLine($"after_json={afterPath}");
        Console.WriteLine($"diff_json={diffPath}");

        return result.Success ? 0 : 1;
    }

    private static async Task<int> RunSpotifyE2EUrlAsync(string trackUrl, string sourceFile)
    {
        EnsureWorkersDataEnvironment();

        if (!IOFile.Exists(sourceFile))
        {
            Console.WriteLine($"Missing test file: {sourceFile}");
            return 2;
        }

        var sessionDir = Path.Join("/tmp/autotag-e2e-spotify", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(sessionDir);
        var targetFile = Path.Join(sessionDir, Path.GetFileName(sourceFile));
        IOFile.Copy(sourceFile, targetFile, overwrite: true);

        var beforeTags = DumpTags(targetFile);
        var beforePath = Path.Join(sessionDir, "before-tags.json");
        await IOFile.WriteAllTextAsync(beforePath, JsonSerializer.Serialize(beforeTags, new JsonSerializerOptions { WriteIndented = true }));

        var provider = BuildRunnerProvider();
        var metadataService = provider.GetRequiredService<SpotifyMetadataService>();
        var runner = provider.GetRequiredService<LocalAutoTagRunner>();

        var metadata = await metadataService.FetchByUrlAsync(trackUrl, CancellationToken.None);
        if (metadata == null || metadata.TrackList.Count == 0)
        {
            Console.WriteLine("Spotify metadata fetch failed (no track list).");
            return 3;
        }

        var summary = metadata.TrackList[0];
        var track = BuildAutoTagTrack(summary);

        var configJson = await LoadAutoTagConfigJsonAsync();
        var config = BuildConfigFromJson(runner, configJson);
        var tagSettings = BuildTagSettingsViaReflection(runner, config);

        await InvokeTagFileAsync(runner, targetFile, track, tagSettings, config, "spotify", CancellationToken.None);

        var afterTags = DumpTags(targetFile);
        var afterPath = Path.Join(sessionDir, "after-tags.json");
        await IOFile.WriteAllTextAsync(afterPath, JsonSerializer.Serialize(afterTags, new JsonSerializerOptions { WriteIndented = true }));

        var diff = DiffTags(beforeTags, afterTags);
        var diffPath = Path.Join(sessionDir, "tag-diff.json");
        await IOFile.WriteAllTextAsync(diffPath, JsonSerializer.Serialize(diff, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine("mode=spotify-e2e-url");
        Console.WriteLine($"track_url={trackUrl}");
        Console.WriteLine("changed_tags:");
        foreach (var item in diff.Changed)
        {
            Console.WriteLine($"  ~ {item.Key} :: '{item.Before}' => '{item.After}'");
        }

        Console.WriteLine("added_tags:");
        foreach (var item in diff.Added)
        {
            Console.WriteLine($"  + {item.Key} :: '{item.After}'");
        }

        Console.WriteLine("removed_tags:");
        foreach (var item in diff.Removed)
        {
            Console.WriteLine($"  - {item.Key} :: '{item.Before}'");
        }

        Console.WriteLine($"artifacts_dir={sessionDir}");
        Console.WriteLine($"before_json={beforePath}");
        Console.WriteLine($"after_json={afterPath}");
        Console.WriteLine($"diff_json={diffPath}");

        return 0;
    }

    private static ServiceProvider BuildRunnerProvider()
    {
        var dataRoot = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR") ?? AppDataPathResolver.GetDefaultWorkersDataDir();
        var contentRoot = ResolveWebContentRoot();
        var environment = new CliHostEnvironment(contentRoot);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={Path.Join(dataRoot, "deezspotag.db")}"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSimpleConsole(o => { o.SingleLine = true; }).SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddSingleton<ISpotifyUserContextAccessor, SpotifyUserContextAccessor>();
        services.AddHttpClient();

        services.AddSingleton<PlatformCapabilitiesStore>(sp =>
            new PlatformCapabilitiesStore(dataRoot, sp.GetService<ILogger<PlatformCapabilitiesStore>>()));

        services.AddSingleton<LibraryRepository>();
        services.AddSingleton<LibraryConfigStore>();
        services.AddSingleton<PlatformAuthService>();
        services.AddSingleton<SpotifyUserAuthStore>();
        services.AddSingleton<SpotifyBlobService>();
        services.AddSingleton<SpotifyAppTokenService>();
        services.AddSingleton<SpotifyPathfinderMetadataClient>();
        services.AddSingleton<SpotifyMetadataService>();
        services.AddSingleton<SpotifySearchService>();
        services.AddSingleton<SpotifyClient>();

        RegisterSharedAutoTagServices(services);
        services.AddTransient<SpotifyMatcher>();
        services.AddTransient<LastFmMatcher>();
        services.AddTransient<LocalAutoTagRunner>();

        return services.BuildServiceProvider();
    }

    private static void RegisterSharedAutoTagServices(IServiceCollection services)
    {
        services.AddTransient<MusicBrainzClient>();
        services.AddTransient<BeatportClient>();
        services.AddTransient<DiscogsClient>();
        services.AddTransient<TraxsourceClient>();
        services.AddTransient<JunoDownloadClient>();
        services.AddTransient<BandcampClient>();
        services.AddTransient<BeatsourceTokenManager>();
        services.AddTransient<BeatsourceClient>();
        services.AddTransient<BpmSupremeClient>();
        services.AddTransient<ItunesClient>();
        services.AddTransient<DeezerClient>();
        services.AddTransient<MusixmatchClient>();

        services.AddTransient<MusicBrainzMatcher>();
        services.AddTransient<BeatportMatcher>();
        services.AddTransient<DiscogsMatcher>();
        services.AddTransient<TraxsourceMatcher>();
        services.AddTransient<JunoDownloadMatcher>();
        services.AddTransient<BandcampMatcher>();
        services.AddTransient<BeatsourceMatcher>();
        services.AddTransient<BpmSupremeMatcher>();
        services.AddTransient<ItunesMatcher>();
        services.AddTransient<DeezerMatcher>();
        services.AddTransient<MusixmatchMatcher>();
    }

    private static string ResolveWebContentRoot()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Join(Directory.GetCurrentDirectory(), "DeezSpoTag.Web")),
            Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "..", "DeezSpoTag.Web")),
            Directory.GetCurrentDirectory()
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private static async Task<JsonObject> LoadAutoTagConfigJsonAsync()
    {
        var dataRoot = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR") ?? AppDataPathResolver.GetDefaultWorkersDataDir();
        var lastConfigPath = Path.Join(dataRoot, "autotag", "last-config.json");
        if (IOFile.Exists(lastConfigPath))
        {
            var parsed = JsonNode.Parse(await IOFile.ReadAllTextAsync(lastConfigPath)) as JsonObject;
            return parsed ?? new JsonObject();
        }

        return new JsonObject();
    }

    private static object BuildConfigFromJson(LocalAutoTagRunner runner, JsonObject configJson)
    {
        var runnerType = runner.GetType();
        var configType = runnerType.GetNestedType("AutoTagRunnerConfig", BindingFlags.NonPublic);
        if (configType == null)
        {
            throw new InvalidOperationException("AutoTagRunnerConfig type not found.");
        }

        var configString = configJson.ToJsonString();
        var config = JsonSerializer.Deserialize(configString, configType, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters =
            {
                new MultipleMatchesSortConverter()
            }
        }) ?? Activator.CreateInstance(configType)!;

        SetProperty(config, "Platforms", new List<string> { "spotify" });
        SetProperty(config, "DownloadTagSource", "spotify");
        SetProperty(config, "IncludeSubfolders", false);
        SetProperty(config, "SkipTagged", false);
        SetProperty(config, "Multiplatform", false);
        SetProperty(config, "Overwrite", true);

        if (configJson.TryGetPropertyValue("tags", out var tagsNode) && tagsNode is JsonArray tagsArray)
        {
            var tags = tagsArray
                .Select(node => node?.GetValue<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList()!;
            SetProperty(config, "Tags", tags);
        }
        else
        {
            SetProperty(config, "Tags", new List<string>
            {
                "title",
                "artist",
                "artists",
                "album",
                "albumArtist",
                "trackNumber",
                "trackTotal",
                "discNumber",
                "genre",
                "label",
                "duration",
                "isrc",
                "explicit",
                "releaseDate",
                "albumArt",
                "url",
                "releaseId",
                "trackId",
                "metaTags"
            });
        }

        return config;
    }

    private static TagSettings BuildTagSettingsViaReflection(LocalAutoTagRunner runner, object config)
    {
        var runnerType = runner.GetType();
        var buildTagSettings = runnerType.GetMethod("BuildTagSettings", BindingFlags.NonPublic | BindingFlags.Static);
        if (buildTagSettings == null)
        {
            throw new InvalidOperationException("BuildTagSettings method not found.");
        }

        return (TagSettings)buildTagSettings.Invoke(null, new[] { config })!;
    }

    private static async Task InvokeTagFileAsync(
        LocalAutoTagRunner runner,
        string filePath,
        AutoTagTrack track,
        TagSettings settings,
        object config,
        string platformId,
        CancellationToken token)
    {
        var runnerType = runner.GetType();
        var tagFileAsync = runnerType.GetMethod("TagFileAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        if (tagFileAsync == null)
        {
            throw new InvalidOperationException("TagFileAsync method not found.");
        }

        var task = (Task)tagFileAsync.Invoke(runner, new object[] { filePath, track, settings, config, platformId, token })!;
        await task.ConfigureAwait(false);
    }

    private static AutoTagTrack BuildAutoTagTrack(SpotifyTrackSummary summary)
    {
        var artists = SplitArtists(summary.Artists);
        return new AutoTagTrack
        {
            Title = summary.Name,
            Artists = artists,
            Album = summary.Album,
            AlbumArtists = string.IsNullOrWhiteSpace(summary.AlbumArtist)
                ? new List<string>()
                : new List<string> { summary.AlbumArtist },
            Url = summary.SourceUrl,
            TrackId = summary.Id,
            ReleaseId = summary.AlbumId ?? string.Empty,
            Duration = summary.DurationMs.HasValue ? TimeSpan.FromMilliseconds(summary.DurationMs.Value) : null,
            Art = summary.ImageUrl,
            Isrc = summary.Isrc,
            ReleaseDate = TryParseDate(summary.ReleaseDate),
            Explicit = summary.Explicit,
            TrackNumber = summary.TrackNumber,
            DiscNumber = summary.DiscNumber,
            TrackTotal = summary.TrackTotal,
            Label = summary.Label,
            Genres = summary.Genres?.ToList() ?? new List<string>()
        };
    }

    private static List<string> SplitArtists(string? artists)
    {
        return artists?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
               ?? new List<string>();
    }

    private static DateTime? TryParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, out var parsed))
        {
            return parsed;
        }

        if (value.Length == 4 && int.TryParse(value, out var year) && year is > 0 and < 10000)
        {
            return new DateTime(year, 1, 1);
        }

        return null;
    }

    private static void SetProperty(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop == null || !prop.CanWrite)
        {
            return;
        }

        prop.SetValue(target, value);
    }

    private static async Task<string> BuildSpotifyE2EConfigAsync(string runPath)
    {
        var dataRoot = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR") ?? AppDataPathResolver.GetDefaultWorkersDataDir();
        var lastConfigPath = Path.Join(dataRoot, "autotag", "last-config.json");
        JsonObject config;

        if (IOFile.Exists(lastConfigPath))
        {
            var parsed = JsonNode.Parse(await IOFile.ReadAllTextAsync(lastConfigPath)) as JsonObject;
            config = parsed ?? new JsonObject();
        }
        else
        {
            config = new JsonObject();
        }

        config["platforms"] = new JsonArray("spotify");
        config["path"] = runPath;
        config["downloadTagSource"] = "spotify";
        config["multiplatform"] = false;
        config["includeSubfolders"] = false;
        config["skipTagged"] = false;
        if (config["tags"] is not JsonArray)
        {
            config["tags"] = new JsonArray(
                "title",
                "artist",
                "artists",
                "album",
                "albumArtist",
                "trackNumber",
                "trackTotal",
                "discNumber",
                "genre",
                "label",
                "duration",
                "isrc",
                "explicit",
                "releaseDate",
                "albumArt",
                "metaTags");
        }

        var configPath = Path.Join(runPath, "autotag-config.spotify-e2e.json");
        await IOFile.WriteAllTextAsync(configPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return configPath;
    }

    private static void EnsureWorkersDataEnvironment()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR")))
        {
            Environment.SetEnvironmentVariable("DEEZSPOTAG_DATA_DIR", AppDataPathResolver.GetDefaultWorkersDataDir());
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR")))
        {
            Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", AppDataPathResolver.GetDefaultWorkersDataDir());
        }
    }

    private static Dictionary<string, List<string>> DumpTags(string filePath)
    {
        var output = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using var file = TagLib.File.Create(filePath);

        AddValues(output, "common.title", file.Tag.Title);
        AddValues(output, "common.artists", file.Tag.Performers);
        AddValues(output, "common.album", file.Tag.Album);
        AddValues(output, "common.albumArtists", file.Tag.AlbumArtists);
        AddValues(output, "common.genres", file.Tag.Genres);
        AddValues(output, "common.comment", file.Tag.Comment);
        AddValues(output, "common.lyrics", file.Tag.Lyrics);
        AddValues(output, "common.isrc", file.Tag.ISRC);
        AddValues(output, "common.year", file.Tag.Year > 0 ? file.Tag.Year.ToString() : null);
        AddValues(output, "common.track", file.Tag.Track > 0 ? file.Tag.Track.ToString() : null);
        AddValues(output, "common.trackCount", file.Tag.TrackCount > 0 ? file.Tag.TrackCount.ToString() : null);
        AddValues(output, "common.disc", file.Tag.Disc > 0 ? file.Tag.Disc.ToString() : null);
        AddValues(output, "common.discCount", file.Tag.DiscCount > 0 ? file.Tag.DiscCount.ToString() : null);
        AddValues(output, "common.pictures", file.Tag.Pictures?.Length > 0 ? file.Tag.Pictures.Length.ToString() : null);

        if (Path.GetExtension(filePath).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag?)file.GetTag(TagTypes.Id3v2, false);
            if (id3 != null)
            {
                foreach (var frame in id3.GetFrames<TextInformationFrame>())
                {
                    AddValues(output, $"id3.{frame.FrameId}", frame.Text);
                }

                foreach (var frame in id3.GetFrames<UserTextInformationFrame>())
                {
                    var key = string.IsNullOrWhiteSpace(frame.Description)
                        ? "id3.TXXX"
                        : $"id3.TXXX:{frame.Description}";
                    AddValues(output, key, frame.Text);
                }

                foreach (var frame in id3.GetFrames<CommentsFrame>())
                {
                    var key = string.IsNullOrWhiteSpace(frame.Description)
                        ? "id3.COMM"
                        : $"id3.COMM:{frame.Description}";
                    AddValues(output, key, frame.Text);
                }

                foreach (var frame in id3.GetFrames<UnsynchronisedLyricsFrame>())
                {
                    var key = string.IsNullOrWhiteSpace(frame.Description)
                        ? "id3.USLT"
                        : $"id3.USLT:{frame.Description}";
                    AddValues(output, key, frame.Text);
                }

                foreach (var frame in id3.GetFrames<AttachedPictureFrame>())
                {
                    AddValues(output, "id3.APIC", $"{frame.MimeType}:{frame.Data.Count}");
                }
            }
        }

        return output
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static TagDiffResult DiffTags(
        Dictionary<string, List<string>> before,
        Dictionary<string, List<string>> after)
    {
        var keys = new HashSet<string>(before.Keys, StringComparer.OrdinalIgnoreCase);
        keys.UnionWith(after.Keys);

        var added = new List<TagDiffRow>();
        var changed = new List<TagDiffRow>();
        var removed = new List<TagDiffRow>();

        foreach (var key in keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var hasBefore = before.TryGetValue(key, out var beforeValues);
            var hasAfter = after.TryGetValue(key, out var afterValues);
            var beforeText = hasBefore ? string.Join(" | ", beforeValues!) : string.Empty;
            var afterText = hasAfter ? string.Join(" | ", afterValues!) : string.Empty;

            if (!hasBefore && hasAfter)
            {
                added.Add(new TagDiffRow(key, string.Empty, afterText));
                continue;
            }

            if (hasBefore && !hasAfter)
            {
                removed.Add(new TagDiffRow(key, beforeText, string.Empty));
                continue;
            }

            if (!string.Equals(beforeText, afterText, StringComparison.Ordinal))
            {
                changed.Add(new TagDiffRow(key, beforeText, afterText));
            }
        }

        return new TagDiffResult(added, changed, removed);
    }

    private static void AddValues(Dictionary<string, List<string>> target, string key, IEnumerable<string>? values)
    {
        if (values == null)
        {
            return;
        }

        foreach (var raw in values)
        {
            AddValues(target, key, raw);
        }
    }

    private static void AddValues(Dictionary<string, List<string>> target, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!target.TryGetValue(key, out var list))
        {
            list = new List<string>();
            target[key] = list;
        }

        if (!list.Contains(value, StringComparer.Ordinal))
        {
            list.Add(value);
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static AutoTagAudioInfo BuildAudioInfo(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var title = file.Tag.Title ?? Path.GetFileNameWithoutExtension(filePath);
            var artists = file.Tag.Performers?.Where(a => !string.IsNullOrWhiteSpace(a)).ToList() ?? new List<string>();
            var artist = artists.FirstOrDefault() ?? file.Tag.FirstPerformer ?? "";
            return new AutoTagAudioInfo
            {
                Title = title,
                Artist = artist,
                Artists = artists.Count == 0 && !string.IsNullOrWhiteSpace(artist) ? new List<string> { artist } : artists,
                DurationSeconds = (int?)file.Properties.Duration.TotalSeconds,
                Isrc = string.IsNullOrWhiteSpace(file.Tag.ISRC) ? null : file.Tag.ISRC
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AutoTagAudioInfo
            {
                Title = Path.GetFileNameWithoutExtension(filePath),
                Artist = "",
                Artists = new List<string>()
            };
        }
    }

    private sealed record TagDiffRow(string Key, string Before, string After);
    private sealed record TagDiffResult(List<TagDiffRow> Added, List<TagDiffRow> Changed, List<TagDiffRow> Removed);

    private sealed class CliHostEnvironment : IWebHostEnvironment, IHostEnvironment
    {
        public CliHostEnvironment(string contentRootPath)
        {
            ApplicationName = "AutoTagRunnerCli";
            EnvironmentName = Environments.Development;
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new PhysicalFileProvider(contentRootPath);
            WebRootPath = Path.Join(contentRootPath, "wwwroot");
            if (!Directory.Exists(WebRootPath))
            {
                Directory.CreateDirectory(WebRootPath);
            }
            WebRootFileProvider = new PhysicalFileProvider(WebRootPath);
        }

        public string ApplicationName { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
        public string ContentRootPath { get; set; }
        public string EnvironmentName { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
    }
}
