using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Core.Enums;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Core.Models.Download;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Shared.Errors;
using DeezSpoTag.Services.Download.Objects;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Metadata;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Integrations.Deezer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CoreTrack = DeezSpoTag.Core.Models.Track;
using CoreAlbum = DeezSpoTag.Core.Models.Album;
using CorePlaylist = DeezSpoTag.Core.Models.Playlist;
using CorePicture = DeezSpoTag.Core.Models.Picture;
using CoreStaticPicture = DeezSpoTag.Core.Models.StaticPicture;
using PathGenerationResult = DeezSpoTag.Services.Download.Shared.Models.PathGenerationResult;
using System.Collections.Concurrent;
using DeezSpoTag.Services.Crypto;
using System.Globalization;

namespace DeezSpoTag.Services.Download.Shared;

/// <summary>
/// EXACT PORT of deezspotag Downloader class from downloader.ts
/// Merged with DeezSpoTagDownloadEngine functionality for complete implementation
/// </summary>
public class DeezSpoTagDownloader : IDeezSpoTagDownloader
{
    private readonly ILogger? _logger;
    private readonly IActivityLogWriter? _activityLog;
    private readonly ImageDownloader _imageDownloader;
    private readonly IServiceProvider _serviceProvider;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private bool _disposed;
    private TagSettings? _resolvedTagSettings;
    private string? _resolvedDownloadTagSource;
    private bool _tagSettingsResolved;

    public DeezSpoTagDownloadObject DownloadObject { get; }
    public DeezSpoTagSettings Settings { get; }
    public IDeezSpoTagListener? Listener { get; }

    private readonly string _tempDir;

    public List<PlaylistUrl> PlaylistUrls { get; } = new();

    public ConcurrentDictionary<string, Task<string?>> CoverQueue { get; } = new();

    public string? PlaylistCovername { get; private set; }

    private readonly object _playlistLock = new();

    // File extensions mapping (ported from deezspotag extensions)
    private static readonly Dictionary<int, string> Extensions = new()
    {
        { 9, ".flac" },   // FLAC
        { 3, ".mp3" },    // MP3_320
        { 1, ".mp3" },    // MP3_128
        { 8, ".mp3" },    // DEFAULT
        { 13, ".mp4" },   // MP4_RA3
        { 14, ".mp4" },   // MP4_RA2
        { 15, ".mp4" },   // MP4_RA1
        { 0, ".mp3" }     // LOCAL
    };

    private static readonly int[] DeezerQualityOrder = { 9, 3, 1 };
    private const string UpdateQueueEvent = "updateQueue";
    private const string SpotifySource = "spotify";
    private const string DeezerSource = "deezer";
    private const string TitleKey = "title";
    private const string ArtistKey = "artist";
    private const string UnknownValue = "Unknown";
    private const string Mp3MiscFormat = "MP3_MISC";
    private const string FallbackSolution = "fallback";
    private const string SearchSolution = "search";

    private static int? GetNextLowerDeezerQuality(int current)
    {
        var index = Array.IndexOf(DeezerQualityOrder, current);
        if (index < 0 || index + 1 >= DeezerQualityOrder.Length)
        {
            return null;
        }
        return DeezerQualityOrder[index + 1];
    }

    public DeezSpoTagDownloader(
        DeezSpoTagDownloadObject downloadObject,
        DeezSpoTagSettings settings,
        IDeezSpoTagListener? listener,
        ImageDownloader imageDownloader,
        IServiceProvider serviceProvider,
        ILogger? logger = null,
        IActivityLogWriter? activityLog = null)
    {
        DownloadObject = downloadObject;
        Settings = settings;
        Listener = listener;
        _imageDownloader = imageDownloader;
        _logger = logger;
        _activityLog = activityLog;
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

        _tempDir = Path.Join(Path.GetTempPath(), "deezspotag-imgs");
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>
    /// Start download process - EXACT port from deezspotag downloader.ts start method
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            await HandleDownloadByObjectTypeAsync();
            NotifyCancellationIfNeeded();
        }
        catch (OperationCanceledException ex)
        {
            DownloadObject.IsCanceled = true;
            SendCancellationEvents();
            if (_logger?.IsEnabled(LogLevel.Information) == true)
            {
                _logger?.LogInformation(ex, "Download cancelled: {Title}", DownloadObject.Title);            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Error during download: {DownloadObject.Title}", ex);
        }
    }

    private async Task HandleDownloadByObjectTypeAsync()
    {
        switch (DownloadObject)
        {
            case DeezSpoTagSingle single:
                await HandleSingleDownloadAsync(single);
                break;
            case DeezSpoTagCollection collection:
                await HandleCollectionDownloadAsync(collection);
                break;
        }
    }

    private void NotifyCancellationIfNeeded()
    {
        if (DownloadObject.IsCanceled)
        {
            SendCancellationEvents();
        }
    }

    private async Task HandleSingleDownloadAsync(DeezSpoTagSingle single)
    {
        if (string.Equals(single.Type, "episode", StringComparison.OrdinalIgnoreCase))
        {
            await DownloadEpisodeAsync(single);
            return;
        }

        var track = await DownloadWrapperAsync(new DownloadExtraData
        {
            TrackAPI = single.Single.TrackAPI,
            AlbumAPI = single.Single.AlbumAPI
        });

        if (track != null)
        {
            await AfterDownloadSingleAsync(track);
        }
    }

    private async Task HandleCollectionDownloadAsync(DeezSpoTagCollection collection)
    {
        var tracks = new DownloadResult?[collection.Collection.Tracks.Count];
        var queueWorkerConcurrency = Math.Max(1, Settings.MaxConcurrentDownloads);
        if (_logger?.IsEnabled(LogLevel.Information) == true)
        {
            _logger?.LogInformation("DeezSpoTag collection worker concurrency: {Concurrency}", queueWorkerConcurrency);        }

        using var q = new DeezSpoTagAsyncQueue<TrackQueueData>(
            async (data) =>
            {
                if (DownloadObject is not DeezSpoTagCollection)
                {
                    return;
                }

                var result = await DownloadWrapperAsync(new DownloadExtraData
                {
                    TrackAPI = data.Track,
                    AlbumAPI = collection.Collection.AlbumAPI,
                    PlaylistAPI = collection.Collection.PlaylistAPI
                });
                tracks[data.Position] = result;
            },
            queueWorkerConcurrency,
            _logger
        );

        for (int pos = 0; pos < collection.Collection.Tracks.Count; pos++)
        {
            var track = collection.Collection.Tracks[pos];
            q.Push(new TrackQueueData { Track = track, Position = pos }, () => { });
        }

        await q.DrainAsync();
        await AfterDownloadCollectionAsync(tracks.ToList());
    }

    private void SendCancellationEvents()
    {
        Listener?.Send("currentItemCancelled", new
        {
            uuid = DownloadObject.UUID,
            title = DownloadObject.Title
        });

        Listener?.Send("removedFromQueue", new
        {
            uuid = DownloadObject.UUID,
            title = DownloadObject.Title
        });
    }

    private async Task DownloadEpisodeAsync(DeezSpoTagSingle single)
    {
        var trackApi = single.Single.TrackAPI;
        var streamUrl = await ResolveEpisodeDownloadUrlAsync(trackApi, single);

        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            throw new InvalidOperationException("Episode stream URL missing.");
        }

        var track = BuildTrackFromApi(trackApi);
        track.DownloadURL = streamUrl;
        track.ApplySettings(Settings);
        await ApplyMetadataOverridesAsync(track);
        track.ApplySettings(Settings);

        var pathResult = await GenerateEpisodePathsAsync(track);
        Directory.CreateDirectory(pathResult.FilePath);

        using var response = await OpenEpisodeDownloadResponseAsync(streamUrl);
        var extension = GetEpisodeExtension(response.Content.Headers.ContentType?.MediaType, streamUrl);
        var filename = EnsureEpisodeFilenameExtension(pathResult.Filename, extension);
        var outputPath = Path.Join(pathResult.FilePath, filename);

        if (System.IO.File.Exists(outputPath) && (string.IsNullOrWhiteSpace(Settings.OverwriteFile) || Settings.OverwriteFile == "n"))
        {
            if (_logger?.IsEnabled(LogLevel.Information) == true)
            {
                _logger?.LogInformation("Episode already exists, skipping: {Path}", outputPath);            }
            DownloadObject.CompleteTrackProgress(Listener);
            return;
        }

        DownloadObject.ExtrasPath = pathResult.ExtrasPath;
        await SaveEpisodeToFileAsync(response, outputPath);

        DownloadObject.Downloaded += 1;
        DownloadObject.CompleteTrackProgress(Listener);

        if (!System.IO.File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new InvalidOperationException($"Episode download failed: output file missing or empty at {outputPath}");
        }

        await TryEmbedEpisodeArtworkAsync(track, outputPath);

        Listener?.Send(UpdateQueueEvent, new
        {
            uuid = DownloadObject.UUID,
            downloaded = true,
            downloadPath = outputPath,
            extrasPath = DownloadObject.ExtrasPath
        });

        AddDownloadFile(new DownloadResult
        {
            Filename = filename,
            Path = outputPath,
            Data = trackApi
        });

    }

    private async Task<string?> ResolveEpisodeDownloadUrlAsync(Dictionary<string, object> trackApi, DeezSpoTagSingle single)
    {
        var streamUrl = GetDictString(trackApi, "direct_stream_url")
                        ?? GetDictString(trackApi, "EPISODE_DIRECT_STREAM_URL")
                        ?? GetDictString(trackApi, "direct_url")
                        ?? GetDictString(trackApi, "url")
                        ?? GetDictString(trackApi, "episode_url")
                        ?? GetDictString(trackApi, "EPISODE_URL")
                        ?? GetDictString(trackApi, "link");

        if (!string.IsNullOrWhiteSpace(streamUrl) && !IsDeezerEpisodePage(streamUrl))
        {
            return streamUrl;
        }

        var episodeId = GetDictString(trackApi, "id") ?? single.Id?.ToString();
        var showId = ResolveEpisodeShowId(trackApi);
        var resolvedUrl = await ResolveEpisodeStreamUrlAsync(episodeId, showId);
        return !string.IsNullOrWhiteSpace(resolvedUrl) ? resolvedUrl : streamUrl;
    }

    private async Task<PathGenerationResult> GenerateEpisodePathsAsync(CoreTrack track)
    {
        var originalDownloadLocation = Settings.DownloadLocation;
        var destinationRoot = await ResolveEpisodeDestinationRootAsync();
        if (!string.IsNullOrWhiteSpace(destinationRoot))
        {
            Settings.DownloadLocation = destinationRoot;
        }

        try
        {
            using var pathScope = _serviceProvider.CreateScope();
            var pathProcessor = pathScope.ServiceProvider.GetRequiredService<EnhancedPathTemplateProcessor>();
            return pathProcessor.GeneratePaths(track, "episode", Settings);
        }
        finally
        {
            Settings.DownloadLocation = originalDownloadLocation;
        }
    }

    private async Task<HttpResponseMessage> OpenEpisodeDownloadResponseAsync(string streamUrl)
    {
        var httpClientFactory = _serviceProvider.GetRequiredService<IHttpClientFactory>();
        var response = await httpClientFactory.CreateClient("DeezSpoTagDownload")
            .GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, _cancellationTokenSource.Token);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static string EnsureEpisodeFilenameExtension(string baseFilename, string extension)
    {
        return baseFilename.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? baseFilename
            : $"{baseFilename}{extension}";
    }

    private async Task SaveEpisodeToFileAsync(HttpResponseMessage response, string outputPath)
    {
        var contentLength = response.Content.Headers.ContentLength;
        long totalRead = 0;

        await using var contentStream = await response.Content.ReadAsStreamAsync(_cancellationTokenSource.Token);
        await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), _cancellationTokenSource.Token)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), _cancellationTokenSource.Token);
            totalRead += bytesRead;
            UpdateEpisodeDownloadProgress(totalRead, contentLength);
        }
    }

    private void UpdateEpisodeDownloadProgress(long totalRead, long? contentLength)
    {
        if (!contentLength.HasValue || contentLength.Value <= 0)
        {
            return;
        }

        var percent = (totalRead / (double)contentLength.Value) * 100.0;
        DownloadObject.ProgressNext = percent;
        DownloadObject.UpdateProgress(Listener);
    }

    private static bool IsDeezerEpisodePage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Contains("deezer.com", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.Contains("/episode", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<TagSettings> ResolveTagSettingsAsync()
    {
        if (_tagSettingsResolved)
        {
            Settings.MetadataSource = _resolvedDownloadTagSource
                ?? DownloadTagSourceHelper.NormalizeMetadataResolverSource(Settings.MetadataSource)
                ?? string.Empty;
            return _resolvedTagSettings ?? Settings.Tags ?? new TagSettings();
        }

        _tagSettingsResolved = true;
        Settings.MetadataSource = DownloadTagSourceHelper.NormalizeMetadataResolverSource(Settings.MetadataSource) ?? string.Empty;
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var resolver = scope.ServiceProvider.GetService<IDownloadTagSettingsResolver>();
            if (resolver != null)
            {
                var profile = await resolver.ResolveProfileAsync(DownloadObject.DestinationFolderId, _cancellationTokenSource.Token);
                _resolvedTagSettings = TagSettingsMerge.UseProfileOnly(profile?.TagSettings);
                _resolvedDownloadTagSource = DownloadTagSourceHelper.ResolveMetadataSource(
                    profile?.DownloadTagSource,
                    DeezerSource,
                    Settings.Service);
                Settings.MetadataSource = _resolvedDownloadTagSource ?? Settings.MetadataSource;
            }

            var conversionOverlay = scope.ServiceProvider.GetService<IFolderConversionSettingsOverlay>();
            if (conversionOverlay != null)
            {
                await conversionOverlay.ApplyAsync(Settings, DownloadObject.DestinationFolderId, _cancellationTokenSource.Token);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger?.LogDebug(ex, "Failed to resolve tag settings for download {Uuid}", DownloadObject.UUID);            }
        }

        return _resolvedTagSettings ?? Settings.Tags ?? new TagSettings();
    }
    private async Task<string?> ResolveEpisodeStreamUrlAsync(string? episodeId, string? showId)
    {
        if (string.IsNullOrWhiteSpace(episodeId))
        {
            return null;
        }

        try
        {
            var httpClientFactory = _serviceProvider.GetRequiredService<IHttpClientFactory>();
            using var response = await httpClientFactory.CreateClient("DeezSpoTagDownload")
                .GetAsync($"https://api.deezer.com/episode/{episodeId}", _cancellationTokenSource.Token);
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync(_cancellationTokenSource.Token);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(contentStream, cancellationToken: _cancellationTokenSource.Token);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out _))
            {
                return null;
            }

            var directUrl = GetJsonString(root, "direct_stream_url")
                            ?? GetJsonString(root, "direct_url")
                            ?? GetJsonString(root, "url");

            if (!IsDeezerEpisodePage(directUrl ?? string.Empty))
            {
                return directUrl;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger?.LogDebug(ex, "Failed to resolve episode stream URL for {EpisodeId}", episodeId);            }
        }

        var gatewayStream = await ResolveEpisodeStreamUrlFromGatewayAsync(episodeId);
        if (!string.IsNullOrWhiteSpace(gatewayStream))
        {
            return gatewayStream;
        }

        return await ResolveEpisodeStreamUrlFromShowAsync(showId, episodeId);
    }

    private async Task<string?> ResolveEpisodeStreamUrlFromGatewayAsync(string episodeId)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var gatewayService = scope.ServiceProvider.GetRequiredService<DeezerGatewayService>();
            var page = await gatewayService.GetEpisodePageAsync(episodeId);
            var results = page["results"] as Newtonsoft.Json.Linq.JObject ?? page;
            var episode = results["EPISODE"] as Newtonsoft.Json.Linq.JObject
                          ?? results["episode"] as Newtonsoft.Json.Linq.JObject
                          ?? results;

            var streamUrl = episode?.Value<string>("EPISODE_DIRECT_STREAM_URL")
                            ?? episode?.Value<string>("EPISODE_URL");
            return IsDeezerEpisodePage(streamUrl ?? string.Empty) ? null : streamUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger?.LogDebug(ex, "Failed to resolve episode stream URL via gateway for {EpisodeId}", episodeId);            }
            return null;
        }
    }

    private Task<string?> ResolveEpisodeStreamUrlFromShowAsync(string? showId, string episodeId)
        => DeezerDownloadSharedHelpers.ResolveEpisodeStreamUrlFromShowAsync(
            _serviceProvider,
            showId,
            episodeId,
            (ex, id) =>
            {
                if (_logger?.IsEnabled(LogLevel.Debug) == true)
                {
                    _logger.LogDebug(ex, "Failed to resolve episode stream URL via show page for {EpisodeId}", id);
                }
            });

    private static string? ResolveEpisodeShowId(Dictionary<string, object> trackApi)
    {
        var showDict = GetDictObject(trackApi, "show");
        var showDictUpper = GetDictObject(trackApi, "SHOW");
        var candidates = new[]
        {
            GetDictString(trackApi, "show_id"),
            GetDictString(trackApi, "SHOW_ID"),
            GetDictString(showDict, "id"),
            GetDictString(showDict, "SHOW_ID"),
            GetDictString(showDictUpper, "id")
        };

        return candidates
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && long.TryParse(candidate, out _));
    }

    private static string? GetJsonString(System.Text.Json.JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) ? value.GetString() : null;
    }

    private async Task<string?> ResolveEpisodeDestinationRootAsync()
    {
        if (DownloadObject.DestinationFolderId is null)
        {
            return null;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var libraryRepository = scope.ServiceProvider.GetService<DeezSpoTag.Services.Library.LibraryRepository>();
            if (libraryRepository == null || !libraryRepository.IsConfigured)
            {
                return null;
            }

            var folders = await libraryRepository.GetFoldersAsync(_cancellationTokenSource.Token);
            var folder = folders.FirstOrDefault(item => item.Id == DownloadObject.DestinationFolderId.Value && item.Enabled);
            return folder?.RootPath;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to resolve podcast destination folder");
            return null;
        }
    }

    private async Task ApplyMetadataOverridesAsync(CoreTrack track)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Settings.MetadataSource))
            {
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var registry = scope.ServiceProvider.GetService<DeezSpoTag.Services.Metadata.IMetadataResolverRegistry>();
            var resolver = registry?.GetResolver(Settings.MetadataSource);
            if (resolver == null)
            {
                return;
            }

            await resolver.ResolveTrackAsync(track, Settings, _cancellationTokenSource.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger?.LogDebug(ex, "Metadata resolver failed for track {TrackId}", track.Id);            }
        }
    }

    private static CoreTrack BuildTrackFromApi(Dictionary<string, object> trackApi)
    {
        var title = GetDictString(trackApi, TitleKey) ?? "Unknown Episode";
        var duration = GetDictInt(trackApi, "duration");
        var trackNumber = GetDictInt(trackApi, "track_position", 1);

        var artistDict = GetDictObject(trackApi, ArtistKey);
        var artistId = GetDictString(artistDict, "id") ?? "0";
        var artistName = GetDictString(artistDict, "name") ?? UnknownValue;
        var artist = new Core.Models.Artist(artistId, artistName, "Main");

        var albumDict = GetDictObject(trackApi, "album");
        var albumId = GetDictString(albumDict, "id") ?? artistId;
        var albumTitle = GetDictString(albumDict, TitleKey) ?? artistName;
        var albumMd5 = GetDictString(albumDict, "md5_image");
        var album = new CoreAlbum(albumId, albumTitle)
        {
            MainArtist = artist,
            TrackTotal = 1,
            DiscTotal = 1,
            Pic = string.IsNullOrWhiteSpace(albumMd5) ? new Picture("", "talk") : new Picture(albumMd5, "talk")
        };
        album.Artist["Main"] = new List<string> { artistName };
        album.Artists = new List<string> { artistName };

        var track = new CoreTrack
        {
            Id = GetDictString(trackApi, "id") ?? "0",
            Title = title,
            Duration = duration,
            MainArtist = artist,
            Album = album,
            TrackNumber = trackNumber,
            DiscNumber = 1,
            DiskNumber = 1
        };

        track.Artist["Main"] = new List<string> { artistName };
        track.Artists = new List<string> { artistName };
        return track;
    }

    private static Dictionary<string, object> GetDictObject(Dictionary<string, object> dict, string key)
        => DeezerDownloadSharedHelpers.GetDictObject(dict, key);

    private static string? GetDictString(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var value) && value != null)
        {
            if (value is string str)
            {
                return str;
            }

            if (value is System.Text.Json.JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return jsonElement.GetString();
                }

                return jsonElement.GetRawText();
            }

            return value.ToString();
        }

        return null;
    }

    private static int GetDictInt(Dictionary<string, object> dict, string key, int fallback = 0)
    {
        if (dict.TryGetValue(key, out var value) && value != null)
        {
            if (value is int intValue)
            {
                return intValue;
            }

            if (value is long longValue)
            {
                return (int)longValue;
            }

            if (value is System.Text.Json.JsonElement jsonElement
                && jsonElement.ValueKind == System.Text.Json.JsonValueKind.Number
                && jsonElement.TryGetInt32(out var parsed))
            {
                return parsed;
            }

            if (int.TryParse(value.ToString(), out intValue))
            {
                return intValue;
            }
        }

        return fallback;
    }

    private static string GetEpisodeExtension(string? contentType, string streamUrl)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            if (contentType.Contains("audio/mp4", StringComparison.OrdinalIgnoreCase) ||
                contentType.Contains("audio/m4a", StringComparison.OrdinalIgnoreCase) ||
                contentType.Contains("audio/aac", StringComparison.OrdinalIgnoreCase))
            {
                return ".m4a";
            }

            if (contentType.Contains("audio/mpeg", StringComparison.OrdinalIgnoreCase) ||
                contentType.Contains("audio/mp3", StringComparison.OrdinalIgnoreCase))
            {
                return ".mp3";
            }
        }

        if (streamUrl.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ||
            streamUrl.Contains(".m4a?", StringComparison.OrdinalIgnoreCase))
        {
            return ".m4a";
        }

        if (streamUrl.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
            streamUrl.Contains(".mp3?", StringComparison.OrdinalIgnoreCase))
        {
            return ".mp3";
        }

        return ".mp3";
    }

    private async Task TryEmbedEpisodeArtworkAsync(CoreTrack track, string outputPath)
    {
        if (track.Album?.Pic == null)
        {
            return;
        }

        try
        {
            var embeddedCoverPath = await EnsureEpisodeEmbeddedCoverAsync(track);
            if (string.IsNullOrWhiteSpace(embeddedCoverPath) || !System.IO.File.Exists(embeddedCoverPath))
            {
                return;
            }

            await EmbedCoverIntoEpisodeFileAsync(outputPath, embeddedCoverPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to embed episode artwork for {Path}", outputPath);
        }
    }

    private async Task<string?> EnsureEpisodeEmbeddedCoverAsync(CoreTrack track)
    {
        var album = track.Album;
        var albumPicture = album?.Pic;
        if (album == null || albumPicture == null)
        {
            return album?.EmbeddedCoverPath;
        }

        EnsurePictureType(albumPicture, "talk");
        if (!string.IsNullOrWhiteSpace(album.EmbeddedCoverPath))
        {
            return album.EmbeddedCoverPath;
        }

        var embeddedSize = Settings.EmbedMaxQualityCover ? Settings.LocalArtworkSize : Settings.EmbeddedArtworkSize;
        var embeddedFormat = ResolveEmbeddedFormat();
        var embeddedUrl = GetPictureUrl(albumPicture, embeddedSize, embeddedFormat);
        if (string.IsNullOrWhiteSpace(embeddedUrl))
        {
            return album.EmbeddedCoverPath;
        }

        var coverExtension = embeddedUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
        var embeddedPath = Path.Join(_tempDir, $"talk_{album.Id}_{embeddedSize}{coverExtension}");
        var downloadedPath = await _imageDownloader.DownloadImageAsync(embeddedUrl, embeddedPath, Settings.OverwriteFile, true);
        if (!string.IsNullOrWhiteSpace(downloadedPath))
        {
            album.EmbeddedCoverPath = downloadedPath;
        }

        return album.EmbeddedCoverPath;
    }

    private static async Task EmbedCoverIntoEpisodeFileAsync(string outputPath, string embeddedCoverPath)
    {
        using var file = TagLib.File.Create(outputPath);
        var coverData = await System.IO.File.ReadAllBytesAsync(embeddedCoverPath);
        if (coverData.Length == 0)
        {
            return;
        }

        var picture = new TagLib.Picture(coverData)
        {
            Type = TagLib.PictureType.FrontCover,
            Description = "cover"
        };
        file.Tag.Pictures = new TagLib.IPicture[] { picture };
        file.Save();
    }

    /// <summary>
    /// Cancel the download
    /// </summary>
    public void Cancel()
    {
        DownloadObject.IsCanceled = true;
        _cancellationTokenSource.Cancel();
    }

    /// <summary>
    /// Download wrapper with EXACT error handling and fallbacks from deezspotag downloadWrapper
    /// EXACT port from: downloadWrapper method in downloader.ts
    /// </summary>
    private async Task<DownloadResult?> DownloadWrapperAsync(DownloadExtraData extraData, CoreTrack? track = null)
    {
        var trackAPI = extraData.TrackAPI;
        var itemData = BuildDownloadItemData(trackAPI);

        DownloadResult? result;
        try
        {
            result = await DownloadAsync(extraData, track);
        }
        catch (DeezSpoTagDownloadFailedException e)
        {
            var bitrate = DownloadObject.Bitrate > 0 ? DownloadObject.Bitrate.ToString() : "unknown";
            _activityLog?.Warn($"Download failed (quality={bitrate}): {DownloadObject.UUID} {e.Message}");
            result = await HandleTrackDownloadFailureAsync(extraData, e, itemData);
        }
        catch (DeezSpoTag.Services.Download.Shared.Errors.DownloadCanceledException)
        {
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            result = new DownloadResult
            {
                Error = new DownloadError
                {
                    Message = e.Message,
                    Data = itemData,
                    Stack = e.StackTrace,
                    Type = "track"
                }
            };
        }

        ReportDownloadResultError(result);

        return result;
    }

    private static Dictionary<string, object> BuildDownloadItemData(Dictionary<string, object> trackAPI)
    {
        return new Dictionary<string, object>
        {
            ["id"] = trackAPI.GetValueOrDefault("id")?.ToString() ?? "0",
            [TitleKey] = trackAPI.GetValueOrDefault(TitleKey)?.ToString() ?? UnknownValue,
            [ArtistKey] = GetArtistName(trackAPI)
        };
    }

    private async Task<DownloadResult> HandleTrackDownloadFailureAsync(
        DownloadExtraData extraData,
        DeezSpoTagDownloadFailedException error,
        object itemData)
    {
        if (error.Track == null)
        {
            return BuildTrackErrorResult(error.Message, error.ErrorId, itemData);
        }

        var trackObj = error.Track;
        _logger?.LogWarning(
            error,
            "Track {TrackId} failed with error {ErrorId}. FallbackID={FallbackID}, AlbumsFallback={AlbumsFallbackCount}, Searched={Searched}, Settings: FallbackSearch={FallbackSearch}, FallbackISRC={FallbackISRC}",
            trackObj.Id,
            error.ErrorId,
            trackObj.FallbackID,
            trackObj.AlbumsFallback?.Count ?? 0,
            trackObj.Searched,
            Settings.FallbackSearch,
            Settings.FallbackISRC);

        var fallbackResult = await TryExecuteTrackFallbackAsync(extraData, trackObj, error.ErrorId, itemData);
        if (fallbackResult != null)
        {
            return fallbackResult;
        }

        var finalErrorId = error.ErrorId + "NoAlternative";
        var finalMessage = DeezSpoTag.Core.Exceptions.ErrorMessages.HasMessage(finalErrorId)
            ? DeezSpoTag.Core.Exceptions.ErrorMessages.GetMessage(finalErrorId)
            : error.Message;
        return BuildTrackErrorResult(finalMessage, finalErrorId, itemData);
    }

    private static DownloadResult BuildTrackErrorResult(string message, string? errorId, object itemData)
    {
        return new DownloadResult
        {
            Error = new DownloadError
            {
                Message = message,
                ErrorId = errorId,
                Data = itemData,
                Type = "track"
            }
        };
    }

    private async Task<DownloadResult?> TryExecuteTrackFallbackAsync(
        DownloadExtraData extraData,
        CoreTrack trackObj,
        string? errorId,
        object itemData)
    {
        return await TryFallbackToLinkedTrackAsync(extraData, trackObj, errorId, itemData)
            ?? await TryFallbackToAlbumTrackAsync(extraData, trackObj, errorId, itemData)
            ?? await TryFallbackToMetadataSearchAsync(extraData, trackObj, errorId, itemData);
    }

    private async Task<DownloadResult?> TryFallbackToLinkedTrackAsync(
        DownloadExtraData extraData,
        CoreTrack trackObj,
        string? errorId,
        object itemData)
    {
        if (trackObj.FallbackID == 0)
        {
            return null;
        }

        Warn(itemData, errorId ?? FallbackSolution, FallbackSolution);

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var deezerClient = scope.ServiceProvider.GetRequiredService<DeezerClient>();
            var gwTrack = await deezerClient.Gw.GetTrackWithFallbackAsync(trackObj.FallbackID.ToString());
            if (gwTrack == null)
            {
                return null;
            }

            trackObj.ParseEssentialData(gwTrack);
            return await DownloadWrapperAsync(extraData, trackObj);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Fallback track failed for {TrackId}", trackObj.Id);
            return null;
        }
    }

    private async Task<DownloadResult?> TryFallbackToAlbumTrackAsync(
        DownloadExtraData extraData,
        CoreTrack trackObj,
        string? errorId,
        object itemData)
    {
        if (!Settings.FallbackISRC || trackObj.AlbumsFallback is not { Count: > 0 })
        {
            return null;
        }

        var albumIndex = trackObj.AlbumsFallback.Count - 1;
        var newAlbumID = trackObj.AlbumsFallback[albumIndex];
        trackObj.AlbumsFallback.RemoveAt(albumIndex);

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var deezerClient = scope.ServiceProvider.GetRequiredService<DeezerClient>();
            var newAlbum = await deezerClient.Gw.GetAlbumPageAsync(newAlbumID);
            var fallbackID = ResolveFallbackIdByIsrc(newAlbum, trackObj.ISRC);
            if (fallbackID == 0)
            {
                return null;
            }

            Warn(itemData, errorId ?? FallbackSolution, FallbackSolution);
            var gwTrack = await deezerClient.Gw.GetTrackWithFallbackAsync(fallbackID.ToString());
            if (gwTrack == null)
            {
                return null;
            }

            trackObj.ParseEssentialData(gwTrack);
            return await DownloadWrapperAsync(extraData, trackObj);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Album fallback failed for track {TrackId}, album {AlbumId}", trackObj.Id, newAlbumID);
            return null;
        }
    }

    private static int ResolveFallbackIdByIsrc(object? albumPage, string? isrc)
    {
        if (albumPage == null || string.IsNullOrWhiteSpace(isrc))
        {
            return 0;
        }

        var songs = albumPage.GetType().GetProperty("Songs")?.GetValue(albumPage);
        var data = songs?.GetType().GetProperty("Data")?.GetValue(songs) as System.Collections.IEnumerable;
        if (data == null)
        {
            return 0;
        }

        foreach (var item in data)
        {
            if (item == null)
            {
                continue;
            }

            var itemType = item.GetType();
            var itemIsrc = itemType.GetProperty("Isrc")?.GetValue(item)?.ToString();
            if (!string.Equals(itemIsrc, isrc, StringComparison.Ordinal))
            {
                continue;
            }

            var sngIdValue = itemType.GetProperty("SngId")?.GetValue(item);
            if (sngIdValue is long longId)
            {
                return (int)longId;
            }

            if (sngIdValue != null && long.TryParse(sngIdValue.ToString(), out var parsedId))
            {
                return (int)parsedId;
            }
        }

        return 0;
    }

    private async Task<DownloadResult?> TryFallbackToMetadataSearchAsync(
        DownloadExtraData extraData,
        CoreTrack trackObj,
        string? errorId,
        object itemData)
    {
        var allowSearchFallback = string.IsNullOrWhiteSpace(trackObj.ISRC);
        if (!allowSearchFallback)
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger?.LogDebug(
                    "Skipping metadata search fallback because ISRC is set for track {TrackId} ({Isrc})",
                    trackObj.Id,
                    trackObj.ISRC);            }
            return null;
        }

        if (trackObj.Searched || !Settings.FallbackSearch)
        {
            return null;
        }

        Warn(itemData, errorId ?? SearchSolution, SearchSolution);

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var deezerClient = scope.ServiceProvider.GetRequiredService<DeezerClient>();
            var searchedID = await deezerClient.Api.GetTrackIdFromMetadataAsync(
                trackObj.MainArtist?.Name ?? string.Empty,
                trackObj.Title,
                trackObj.Album?.Title ?? string.Empty);

            if (string.IsNullOrEmpty(searchedID) || searchedID == "0")
            {
                return null;
            }

            var gwTrack = await deezerClient.Gw.GetTrackWithFallbackAsync(searchedID);
            if (gwTrack == null)
            {
                return null;
            }

            trackObj.ParseEssentialData(gwTrack);
            trackObj.Searched = true;
            Log(itemData, "searchFallback");
            return await DownloadWrapperAsync(extraData, trackObj);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Search fallback failed for track {TrackId}", trackObj.Id);
            return null;
        }
    }

    private void ReportDownloadResultError(DownloadResult? result)
    {
        if (result?.Error == null)
        {
            return;
        }

        if (DownloadObject is DeezSpoTagSingle || DownloadObject is DeezSpoTagCollection)
        {
            DownloadObject.CompleteTrackProgress(Listener);
        }

        DownloadObject.Failed += 1;
        DownloadObject.Errors.Add(new DeezSpoTag.Services.Download.Shared.Models.DeezSpoTagDownloadError
        {
            Message = result.Error.Message,
            ErrorId = result.Error.ErrorId,
            Data = result.Error.Data is Dictionary<string, object> errorData
                ? errorData
                : new Dictionary<string, object>(),
            Stack = result.Error.Stack,
            Type = result.Error.Type
        });

        if (Listener == null)
        {
            return;
        }

        var error = result.Error;
        Listener.Send(UpdateQueueEvent, new
        {
            uuid = DownloadObject.UUID,
            title = DownloadObject.Title,
            failed = true,
            data = error.Data,
            error = error.Message,
            errid = error.ErrorId,
            stack = error.Stack,
            type = error.Type
        });
    }

    /// <summary>
    /// Download individual track - EXACT port from deezspotag download method
    /// </summary>
    private async Task<DownloadResult> DownloadAsync(DownloadExtraData extraData, CoreTrack? track = null)
    {
        var trackAPI = extraData.TrackAPI;
        var albumAPI = NormalizeApiDictionary(extraData.AlbumAPI, "id", TitleKey);
        var playlistAPI = NormalizeApiDictionary(extraData.PlaylistAPI, "id", TitleKey);
        ThrowIfDownloadCanceled();

        var trackId = trackAPI.GetValueOrDefault("id")?.ToString();
        if (string.IsNullOrEmpty(trackId) || trackId == "0")
        {
            throw new DeezSpoTagDownloadFailedException("notOnDeezer");
        }

        trackAPI["size"] = DownloadObject.Size;
        var returnData = new DownloadResult
        {
            Data = new
            {
                id = trackId,
                title = trackAPI.GetValueOrDefault(TitleKey)?.ToString() ?? UnknownValue,
                artist = GetArtistName(trackAPI)
            }
        };

        track ??= BuildTrackFromDownloadApis(trackAPI, albumAPI, playlistAPI);
        await PopulateTrackMetadataAsync(track, trackId, trackAPI, albumAPI, playlistAPI);

        ThrowIfDownloadCanceled();
        EnsureTrackIsEncoded(track);

        var selectedFormat = await ResolvePreferredBitrateAsync(track, trackId);
        ApplyTrackBitrate(track, selectedFormat);

        track.ApplySettings(Settings);
        await ApplyMetadataOverridesAsync(track);
        track.ApplySettings(Settings);

        using var pathScope = _serviceProvider.CreateScope();
        var pathProcessor = pathScope.ServiceProvider.GetRequiredService<EnhancedPathTemplateProcessor>();
        var pathResult = pathProcessor.GeneratePaths(track, DownloadObject.Type, Settings)
            ?? throw new DeezSpoTagDownloadFailedException("pathGenerationFailed");
        var pathState = BuildDownloadPathState(track, pathResult);

        var skipResult = await TryHandleAlreadyDownloadedAsync(track, pathResult, pathState, returnData);
        if (skipResult.Handled)
        {
            returnData.TaggingSucceeded = skipResult.TaggingSucceeded;
            return returnData;
        }

        ThrowIfDownloadCanceled();
        ApplyKeepBothOverwrite(pathResult, pathState);
        returnData.Data = new
        {
            id = track.Id,
            title = track.Title,
            artist = track.MainArtist?.Name ?? UnknownValue
        };

        if (!string.IsNullOrEmpty(pathResult.ExtrasPath) && string.IsNullOrEmpty(DownloadObject.ExtrasPath))
        {
            DownloadObject.ExtrasPath = pathState.DisplayExtrasPath;
        }

        await PopulateArtworkAsync(track, pathResult, returnData, pathProcessor);
        await TryPopulateTrackLyricsAsync(track, pathResult, pathState);
        await DownloadTrackMediaWithFallbackAsync(track, pathState.WritePathIo);

        var taggingSucceeded = track.Local || await TryTagDownloadedTrackAsync(track, pathState.Extension, pathState.WritePathIo);
        FinalizeSuccessfulTrackDownload(track, returnData, pathState, taggingSucceeded);
        return returnData;
    }

    private void ThrowIfDownloadCanceled()
    {
        if (DownloadObject.IsCanceled)
        {
            throw new DeezSpoTag.Services.Download.Shared.Errors.DownloadCanceledException();
        }
    }

    private CoreTrack BuildTrackFromDownloadApis(
        Dictionary<string, object> trackAPI,
        Dictionary<string, object>? albumAPI,
        Dictionary<string, object>? playlistAPI)
    {
        var track = new CoreTrack();
        var apiTrack = ConvertToApiTrack(trackAPI);
        track.ParseTrack(apiTrack);

        if (albumAPI != null)
        {
            track.Album = new CoreAlbum(GetStringValue(albumAPI, "id"), GetStringValue(albumAPI, TitleKey));
        }

        if (playlistAPI != null)
        {
            var cleanedPlaylistDict = ConvertJsonElementsToValues(playlistAPI);
            track.Playlist = new CorePlaylist(cleanedPlaylistDict);
        }

        return track;
    }

    private async Task PopulateTrackMetadataAsync(
        CoreTrack track,
        string trackId,
        Dictionary<string, object> trackAPI,
        Dictionary<string, object>? albumAPI,
        Dictionary<string, object>? playlistAPI)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var deezerClient = scope.ServiceProvider.GetRequiredService<DeezerClient>();
            var apiTrack = ConvertToApiTrack(trackAPI);
            var apiAlbumObj = albumAPI != null ? ConvertToApiAlbum(albumAPI) : null;
            var apiPlaylistObj = playlistAPI != null ? ConvertToApiPlaylist(playlistAPI) : null;
            await track.ParseData(deezerClient, trackId, apiTrack, apiAlbumObj, apiPlaylistObj);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex.Message == "AlbumDoesntExists")
            {
                throw new DeezSpoTagDownloadFailedException("albumDoesntExists");
            }

            if (ex.Message == "MD5NotFound")
            {
                throw new DeezSpoTagDownloadFailedException("notLoggedIn");
            }

            throw;
        }
    }

    private static void EnsureTrackIsEncoded(CoreTrack track)
    {
        if (string.IsNullOrEmpty(track.MD5) || track.MD5 == "0")
        {
            throw new DeezSpoTagDownloadFailedException("notEncoded", track);
        }
    }

    private async Task<int> ResolvePreferredBitrateAsync(CoreTrack track, string trackId)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var bitrateSelector = scope.ServiceProvider.GetRequiredService<BitrateSelector>();
            return await bitrateSelector.GetPreferredBitrateAsync(
                track,
                DownloadObject.Bitrate,
                Settings.FallbackBitrate,
                Settings.FeelingLucky,
                DownloadObject.UUID,
                new DownloadListenerAdapter(Listener));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (e.Message.Contains("WrongLicense"))
            {
                throw new DeezSpoTagDownloadFailedException("wrongLicense");
            }

            if (e.Message.Contains("WrongGeolocation"))
            {
                throw new DeezSpoTagDownloadFailedException("wrongGeolocation", track);
            }

            if (e.Message.Contains("PreferredBitrateNotFound"))
            {
                throw new DeezSpoTagDownloadFailedException("wrongBitrate", track);
            }

            if (e.Message.Contains("TrackNot360"))
            {
                throw new DeezSpoTagDownloadFailedException("no360RA");
            }

            throw new InvalidOperationException($"Error getting preferred bitrate for track {trackId}", e);
        }
    }

    private void ApplyTrackBitrate(CoreTrack track, int selectedFormat)
    {
        track.Bitrate = selectedFormat;
        DownloadObject.Bitrate = selectedFormat;
        if (track.Album != null)
        {
            track.Album.Bitrate = selectedFormat;
        }
    }

    private static DownloadPathState BuildDownloadPathState(CoreTrack track, PathGenerationResult pathResult)
    {
        var displayFilePath = pathResult.FilePath;
        var displayExtrasPath = pathResult.ExtrasPath;
        var ioFilePath = DownloadPathResolver.ResolveIoPath(displayFilePath);
        var ioExtrasPath = DownloadPathResolver.ResolveIoPath(displayExtrasPath);
        Directory.CreateDirectory(ioFilePath);

        var extension = Extensions.GetValueOrDefault(track.Bitrate, ".mp3");
        var writePathIo = Path.Join(ioFilePath, $"{pathResult.Filename}{extension}");
        var writePathDisplay = DownloadPathResolver.NormalizeDisplayPath(writePathIo);

        return new DownloadPathState
        {
            DisplayFilePath = displayFilePath,
            DisplayExtrasPath = displayExtrasPath,
            IoFilePath = ioFilePath,
            IoExtrasPath = ioExtrasPath,
            Extension = extension,
            WritePathIo = writePathIo,
            WritePathDisplay = writePathDisplay
        };
    }

    private async Task<(bool Handled, bool TaggingSucceeded)> TryHandleAlreadyDownloadedAsync(
        CoreTrack track,
        PathGenerationResult pathResult,
        DownloadPathState pathState,
        DownloadResult returnData)
    {
        var shouldDownload = DownloadUtils.CheckShouldDownload(
            pathResult.Filename,
            pathState.IoFilePath,
            pathState.Extension,
            pathState.WritePathIo,
            EnumConverter.StringToOverwriteOption(Settings.OverwriteFile),
            track);

        if (shouldDownload)
        {
            return (false, false);
        }

        var taggingSucceeded = await TryTagExistingTrackAsync(track, pathState.Extension, pathState.WritePathIo);
        Listener?.Send(UpdateQueueEvent, new
        {
            uuid = DownloadObject.UUID,
            alreadyDownloaded = true,
            downloadPath = pathState.WritePathDisplay,
            extrasPath = DownloadObject.ExtrasPath,
        });

        returnData.Filename = pathState.WritePathDisplay.Substring(pathState.DisplayExtrasPath.Length + 1);
        returnData.Path = pathState.WritePathDisplay;
        returnData.TaggingSucceeded = taggingSucceeded;
        DownloadObject.CompleteTrackProgress(Listener);
        DownloadObject.Downloaded++;
        AddDownloadFile(returnData);
        return (true, taggingSucceeded);
    }

    private async Task<bool> TryTagExistingTrackAsync(CoreTrack track, string extension, string writePathIo)
    {
        if (Settings.OverwriteFile != "t" && Settings.OverwriteFile != "y")
        {
            return false;
        }

        try
        {
            await ApplyMetadataOverridesAsync(track);
            using var taggerScope = _serviceProvider.CreateScope();
            var audioTagger = taggerScope.ServiceProvider.GetRequiredService<AudioTagger>();
            var tagSettings = await ResolveTagSettingsAsync();
            await audioTagger.TagTrackAsync(extension, writePathIo, track, tagSettings);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Tagging failed for track {TrackId} ({Title})", track.Id, track.Title);
            Warn(new
            {
                id = track.Id,
                title = track.Title,
                artist = track.MainArtist?.Name ?? UnknownValue
            }, "taggingFailed", "tag");
            return false;
        }
    }

    private void ApplyKeepBothOverwrite(PathGenerationResult pathResult, DownloadPathState pathState)
    {
        if (Settings.OverwriteFile != "b")
        {
            return;
        }

        var originalFilename = Path.Join(pathState.IoFilePath, pathResult.Filename);
        var count = 0;
        var currentFilename = originalFilename;
        while (System.IO.File.Exists(currentFilename + pathState.Extension))
        {
            count++;
            currentFilename = $"{originalFilename} ({count})";
        }

        pathState.WritePathIo = currentFilename + pathState.Extension;
        pathState.WritePathDisplay = DownloadPathResolver.NormalizeDisplayPath(pathState.WritePathIo);
    }

    private async Task TryPopulateTrackLyricsAsync(CoreTrack track, PathGenerationResult pathResult, DownloadPathState pathState)
    {
        var lyricsTagSettings = await ResolveTagSettingsAsync();
        if (!LyricsSettingsPolicy.CanFetchLyrics(Settings) && !lyricsTagSettings.Lyrics && !lyricsTagSettings.SyncedLyrics)
        {
            return;
        }

        try
        {
            using var lyricsScope = _serviceProvider.CreateScope();
            var lyricsService = lyricsScope.ServiceProvider.GetRequiredService<DeezSpoTag.Services.Download.Utils.LyricsService>();
            var lyrics = await lyricsService.ResolveLyricsAsync(track, Settings, CancellationToken.None);
            if (lyrics == null || !string.IsNullOrEmpty(lyrics.ErrorMessage))
            {
                return;
            }

            track.Lyrics ??= new DeezSpoTag.Core.Models.Lyrics(track.LyricsId ?? "0");
            track.Lyrics.Unsync = lyrics.UnsyncedLyrics ?? string.Empty;
            if (lyrics.IsSynced())
            {
                track.Lyrics.Sync = lyrics.GenerateLrcContent(track.Title, track.MainArtist?.Name, track.Album?.Title);
            }

            var coverPathIo = DownloadPathResolver.ResolveIoPath(pathResult.CoverPath ?? string.Empty);
            var artistPathIo = DownloadPathResolver.ResolveIoPath(pathResult.ArtistPath ?? string.Empty);
            await lyricsService.SaveLyricsAsync(
                lyrics,
                track,
                (pathState.IoFilePath, pathResult.Filename, pathState.IoExtrasPath, coverPathIo, artistPathIo),
                Settings);
            AddLyricsFiles(pathResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Unable to fetch lyrics data for track {TrackId}", track.Id);
        }
    }

    private async Task DownloadTrackMediaWithFallbackAsync(CoreTrack track, string writePathIo)
    {
        var currentQuality = track.Bitrate;
        while (true)
        {
            await EnsureTrackDownloadUrlAsync(track, currentQuality);
            if (string.IsNullOrEmpty(track.DownloadURL))
            {
                if (TryGetFallbackQuality(currentQuality, out var nextQuality))
                {
                    _logger?.LogWarning(
                        "Download URL missing for {TrackId} at {Quality}, retrying at {Fallback}",
                        track.Id,
                        currentQuality,
                        nextQuality);
                    currentQuality = nextQuality;
                    continue;
                }

                throw new DeezSpoTagDownloadFailedException("notAvailable", track);
            }

            try
            {
                await StreamTrackAsync(track, currentQuality, writePathIo);
                return;
            }
            catch (HttpRequestException ex) when (TryGetFallbackQuality(currentQuality, out var nextQuality))
            {
                _logger?.LogWarning(
                    ex,
                    "Stream failed for {TrackId} at {Quality}, retrying at {Fallback}",
                    track.Id,
                    currentQuality,
                    nextQuality);
                currentQuality = nextQuality;
            }
        }
    }

    private async Task EnsureTrackDownloadUrlAsync(CoreTrack track, int currentQuality)
    {
        var formatName = GetFormatName(currentQuality);
        track.Bitrate = currentQuality;
        if (track.Album != null)
        {
            track.Album.Bitrate = currentQuality;
        }

        track.DownloadURL = track.Urls?.GetValueOrDefault(formatName) ?? string.Empty;
        if (!string.IsNullOrEmpty(track.DownloadURL))
        {
            return;
        }

        try
        {
            using var urlScope = _serviceProvider.CreateScope();
            var authService = urlScope.ServiceProvider.GetRequiredService<AuthenticatedDeezerService>();
            var deezerClient = await authService.GetAuthenticatedClientAsync();
            if (deezerClient == null)
            {
                return;
            }

            await TryPopulateMediaApiUrlAsync(track, formatName, deezerClient);
            if (string.IsNullOrEmpty(track.DownloadURL))
            {
                await TryRefreshTrackUrlsFromGatewayAsync(track, formatName, deezerClient);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to get media API URL for track {TrackId}", track.Id);
        }
    }

    private static async Task TryPopulateMediaApiUrlAsync(CoreTrack track, string formatName, DeezerClient deezerClient)
    {
        if (string.IsNullOrEmpty(track.TrackToken))
        {
            return;
        }

        var mediaFormatName = formatName == Mp3MiscFormat ? "MP3_128" : formatName;
        var mediaUrl = await deezerClient.GetTrackUrlAsync(track.TrackToken, mediaFormatName);
        if (string.IsNullOrEmpty(mediaUrl))
        {
            return;
        }

        track.DownloadURL = mediaUrl;
        track.Urls ??= new Dictionary<string, string>();
        track.Urls[formatName] = mediaUrl;
    }

    private static async Task TryRefreshTrackUrlsFromGatewayAsync(CoreTrack track, string formatName, DeezerClient deezerClient)
    {
        var gwTrack = await deezerClient.Gw.GetTrackWithFallbackAsync(track.Id);
        if (gwTrack == null)
        {
            return;
        }

        track.ParseEssentialData(gwTrack);
        track.DownloadURL = track.Urls?.GetValueOrDefault(formatName) ?? string.Empty;
    }

    private bool TryGetFallbackQuality(int currentQuality, out int nextQuality)
    {
        nextQuality = 0;
        var allowLocalDeezerFallback = Settings.FallbackBitrate
            && !string.Equals(Settings.Service, "auto", StringComparison.OrdinalIgnoreCase);
        if (!allowLocalDeezerFallback)
        {
            return false;
        }

        var fallback = GetNextLowerDeezerQuality(currentQuality);
        if (!fallback.HasValue)
        {
            return false;
        }

        nextQuality = fallback.Value;
        return true;
    }

    private async Task StreamTrackAsync(CoreTrack track, int currentQuality, string writePathIo)
    {
        var isCryptedStream = track.DownloadURL.Contains("/mobile/") || track.DownloadURL.Contains("/media/");
        using var streamScope = _serviceProvider.CreateScope();
        var streamProcessor = streamScope.ServiceProvider.GetRequiredService<DecryptionStreamProcessor>();
        var downloadObjectAdapter = new DownloadObjectAdapter(DownloadObject, Listener);
        var listenerAdapter = new DownloadListenerAdapter(Listener);

        if (isCryptedStream)
        {
            await DownloadAndDecryptEncryptedTrackAsync(streamProcessor, track, currentQuality, writePathIo, downloadObjectAdapter, listenerAdapter);
            return;
        }

        await streamProcessor.StreamTrackAsync(
            writePathIo,
            track,
            track.DownloadURL,
            downloadObjectAdapter,
            listenerAdapter,
            new DecryptionStreamProcessor.StreamTrackRetryPolicy(
                Settings.MaxRetries,
                Settings.RetryDelaySeconds,
                Settings.RetryDelayIncrease),
            cancellationToken: _cancellationTokenSource.Token);
    }

    private async Task DownloadAndDecryptEncryptedTrackAsync(
        DecryptionStreamProcessor streamProcessor,
        CoreTrack track,
        int currentQuality,
        string writePathIo,
        DownloadObjectAdapter downloadObjectAdapter,
        DownloadListenerAdapter listenerAdapter)
    {
        var tempEnc = Path.Join(Path.GetTempPath(), $"{track.Id}-{currentQuality}.enc");
        await streamProcessor.DownloadEncryptedWithResumeAsync(
            track.DownloadURL,
            tempEnc,
            downloadObjectAdapter,
            listenerAdapter,
            _cancellationTokenSource.Token);
        await streamProcessor.DecryptFileAsync(
            tempEnc,
            writePathIo,
            track,
            _cancellationTokenSource.Token);
        try
        {
            File.Delete(tempEnc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger?.LogDebug(ex, "Failed to delete temp encrypted file {Path}", tempEnc);            }
        }
    }

    private async Task<bool> TryTagDownloadedTrackAsync(CoreTrack track, string extension, string writePathIo)
    {
        if (track.Local)
        {
            return true;
        }

        try
        {
            await ApplyMetadataOverridesAsync(track);
            using var taggerScope = _serviceProvider.CreateScope();
            var audioTagger = taggerScope.ServiceProvider.GetRequiredService<AudioTagger>();
            var tagSettings = await ResolveTagSettingsAsync();
            var sidecarState = GetLyricsSidecarState(writePathIo);
            if (sidecarState.HasAny && track.Lyrics != null)
            {
                track.Lyrics.Unsync = string.Empty;
                track.Lyrics.Sync = string.Empty;
                track.Lyrics.SyncID3?.Clear();
            }

            await audioTagger.TagTrackAsync(extension, writePathIo, track, tagSettings);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Tagging failed for track {TrackId} ({Title})", track.Id, track.Title);
            Warn(new
            {
                id = track.Id,
                title = track.Title,
                artist = track.MainArtist?.Name ?? UnknownValue
            }, "taggingFailed", "tag");
            return false;
        }
    }

    private void FinalizeSuccessfulTrackDownload(
        CoreTrack track,
        DownloadResult returnData,
        DownloadPathState pathState,
        bool taggingSucceeded)
    {
        DownloadObject.CompleteTrackProgress(Listener);
        DownloadObject.Downloaded += 1;

        Listener?.Send(UpdateQueueEvent, new
        {
            uuid = DownloadObject.UUID,
            downloaded = true,
            downloadPath = pathState.WritePathDisplay,
            extrasPath = DownloadObject.ExtrasPath,
        });

        returnData.Filename = pathState.WritePathDisplay.Substring(pathState.DisplayExtrasPath.Length + 1);
        returnData.Path = pathState.WritePathDisplay;
        returnData.TaggingSucceeded = taggingSucceeded;
        returnData.Searched = track.Searched;
        AddDownloadFile(returnData);
    }

    /// <summary>
    /// Post-processing for single track downloads - EXACT port from deezspotag afterDownloadSingle
    /// </summary>
    private async Task AfterDownloadSingleAsync(DownloadResult track)
    {
        if (track == null || track.Error != null) return;

        if (string.IsNullOrEmpty(DownloadObject.ExtrasPath))
        {
            DownloadObject.ExtrasPath = Settings.DownloadLocation;
        }

        await TrySaveLocalArtworkAsync(track, resolveIoPath: true);

        // EXACT PORT: Create searched logfile
        try
        {
            if (Settings.LogSearched && track.Searched)
            {
                var artist = GetDataValue(track.Data, ArtistKey, UnknownValue);
                var title = GetDataValue(track.Data, TitleKey, UnknownValue);
                var filename = $"{artist} - {title}";
                var searchedFilePath = Path.Join(DownloadObject.ExtrasPath, "searched.txt");
                string searchedFile;

                try
                {
                    searchedFile = await System.IO.File.ReadAllTextAsync(searchedFilePath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    searchedFile = "";
                }

                if (!searchedFile.Contains(filename))
                {
                    if (!string.IsNullOrEmpty(searchedFile))
                        searchedFile += "\r\n";
                    searchedFile += filename + "\r\n";
                    await System.IO.File.WriteAllTextAsync(searchedFilePath, searchedFile);
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AfterDownloadErrorReport("CreateSearchedLog", e);
        }

        await TryExecuteAfterDownloadCommandAsync(track.Filename ?? string.Empty);

    }

    /// <summary>
    /// Post-processing for collection downloads - EXACT port from deezspotag afterDownloadCollection
    /// </summary>
    private async Task AfterDownloadCollectionAsync(List<DownloadResult?> tracks)
    {
        if (string.IsNullOrEmpty(DownloadObject.ExtrasPath))
        {
            DownloadObject.ExtrasPath = Settings.DownloadLocation;
        }

        var playlist = new List<string>();
        var errors = new System.Text.StringBuilder();
        var searched = new System.Text.StringBuilder();
        await BuildCollectionOutputsAsync(tracks, playlist, errors, searched);

        await TryWriteCollectionLogAsync("CreateErrorLog", Settings.LogErrors, "errors.txt", errors);
        await TryWriteCollectionLogAsync("CreateSearchedLog", Settings.LogSearched, "searched.txt", searched);
        await TrySavePlaylistArtworkForCollectionAsync();
        await TryCreateCollectionPlaylistFileAsync(playlist);
        await TryExecuteAfterDownloadCommandAsync(string.Empty);
    }

    private async Task BuildCollectionOutputsAsync(
        List<DownloadResult?> tracks,
        List<string> playlist,
        System.Text.StringBuilder errors,
        System.Text.StringBuilder searched)
    {
        foreach (var track in tracks)
        {
            if (track == null)
            {
                continue;
            }

            AppendCollectionTrackError(track, errors);
            AppendCollectionTrackSearch(track, searched);
            await TrySaveLocalArtworkAsync(track, resolveIoPath: false);
            playlist.Add(track.Filename ?? string.Empty);
        }
    }

    private static void AppendCollectionTrackError(DownloadResult track, System.Text.StringBuilder errors)
    {
        if (track.Error == null)
        {
            return;
        }

        var id = GetDataValue(track.Error.Data, "id", "0");
        var artist = GetDataValue(track.Error.Data, ArtistKey, UnknownValue);
        var title = GetDataValue(track.Error.Data, TitleKey, UnknownValue);
        errors.Append(id)
            .Append(" | ")
            .Append(artist)
            .Append(" - ")
            .Append(title)
            .Append(" | ")
            .Append(track.Error.Message)
            .Append("\r\n");
    }

    private static void AppendCollectionTrackSearch(DownloadResult track, System.Text.StringBuilder searched)
    {
        if (!track.Searched)
        {
            return;
        }

        var artist = GetDataValue(track.Data, ArtistKey, UnknownValue);
        var title = GetDataValue(track.Data, TitleKey, UnknownValue);
        searched.Append(artist)
            .Append(" - ")
            .Append(title)
            .Append("\r\n");
    }

    private async Task TryWriteCollectionLogAsync(string position, bool enabled, string fileName, System.Text.StringBuilder content)
    {
        try
        {
            if (enabled && content.Length > 0)
            {
                await System.IO.File.WriteAllTextAsync(Path.Join(DownloadObject.ExtrasPath, fileName), content.ToString());
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AfterDownloadErrorReport(position, e);
        }
    }

    private async Task TrySavePlaylistArtworkForCollectionAsync()
    {
        try
        {
            var tagSettings = await ResolveTagSettingsAsync();
            if (!Settings.SaveArtwork
                || string.IsNullOrEmpty(PlaylistCovername)
                || tagSettings.SavePlaylistAsCompilation
                || PlaylistUrls.Count == 0)
            {
                return;
            }

            foreach (var image in PlaylistUrls)
            {
                var imagePath = Path.Join(DownloadObject.ExtrasPath, $"{PlaylistCovername}.{image.Extension}");
                await _imageDownloader.DownloadImageAsync(image.Url, imagePath, Settings.OverwriteFile, true);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AfterDownloadErrorReport("SavePlaylistArt", e);
        }
    }

    private async Task TryCreateCollectionPlaylistFileAsync(List<string> playlist)
    {
        try
        {
            if (!Settings.CreateM3U8File)
            {
                return;
            }

            var filename = GeneratePlaylistFilename();
            await System.IO.File.WriteAllTextAsync(Path.Join(DownloadObject.ExtrasPath, $"{filename}.m3u8"), string.Join("\n", playlist));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AfterDownloadErrorReport("CreatePlaylistFile", e);
        }
    }

    private async Task TryExecuteAfterDownloadCommandAsync(string filename)
    {
        try
        {
            if (string.IsNullOrEmpty(Settings.ExecuteCommand))
            {
                return;
            }

            var filenameValue = string.IsNullOrEmpty(filename) ? string.Empty : ShellEscape(filename);
            var command = Settings.ExecuteCommand
                .Replace("%folder%", ShellEscape(DownloadObject.ExtrasPath))
                .Replace("%filename%", filenameValue);

            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    ArgumentList = { "-c", command },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            process.Start();
            await process.WaitForExitAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AfterDownloadErrorReport("ExecuteCommand", e);
        }
    }

    /// <summary>
    /// Report error after download - EXACT port from deezspotag afterDownloadErrorReport
    /// </summary>
    private void AfterDownloadErrorReport(string position, Exception error, object? itemData = null)
    {
        var errorData = new Dictionary<string, object>
        {
            ["position"] = position
        };

        if (itemData != null)
        {
            errorData["itemData"] = itemData;
        }

        DownloadObject.Errors.Add(new DeezSpoTag.Services.Download.Shared.Models.DeezSpoTagDownloadError
        {
            Message = error.Message,
            Stack = error.StackTrace,
            Data = errorData,
            Type = "post"
        });

        if (Listener != null)
        {
            Listener.Send(UpdateQueueEvent, new
            {
                uuid = DownloadObject.UUID,
                postFailed = true,
                error = error.Message,
                data = new { position, itemData },
                stack = error.StackTrace,
                type = "post"
            });
        }
    }

    /// <summary>
    /// Log download info - EXACT port from deezspotag
    /// </summary>
    private void Log(object data, string state)
    {
        if (Listener != null)
        {
            Listener.Send("downloadInfo", new
            {
                uuid = DownloadObject.UUID,
                title = DownloadObject.Title,
                data,
                state
            });
        }
    }

    /// <summary>
    /// Log download warning - EXACT port from deezspotag
    /// </summary>
    private void Warn(object data, string state, string solution)
    {
        Listener?.Send("downloadWarn", new
        {
            uuid = DownloadObject.UUID,
            data,
            state,
            solution
        });
    }

    private async Task TrySaveLocalArtworkAsync(DownloadResult track, bool resolveIoPath)
    {
        await TryRunAfterDownloadStepAsync("SaveLocalAlbumArt", track, () => SaveLocalAlbumArtworkAsync(track, resolveIoPath));
        await TryRunAfterDownloadStepAsync("SaveLocalArtistArt", track, () => SaveLocalArtistArtworkAsync(track, resolveIoPath));
    }

    private async Task TryRunAfterDownloadStepAsync(string position, DownloadResult track, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AfterDownloadErrorReport(position, e, track.Data);
        }
    }

    private async Task PopulateArtworkAsync(
        CoreTrack track,
        PathGenerationResult pathResult,
        DownloadResult result,
        EnhancedPathTemplateProcessor pathProcessor)
    {
        var state = CreateArtworkPopulationState(track, pathResult, result, pathProcessor);
        await ResolveCoverArtworkAsync(state);
        await ResolveArtistArtworkAsync(state);
        if (!EnsureAlbumIsReadyForArtwork(state))
        {
            return;
        }

        await PopulateEmbeddedCoverAsync(state);
        PopulateAlbumArtworkUrls(state);
        await PopulateFallbackEmbeddedCoverAsync(state);
        PopulateArtistArtworkUrls(state);
        PopulatePlaylistArtwork(state);
    }

    private ArtworkPopulationState CreateArtworkPopulationState(
        CoreTrack track,
        PathGenerationResult pathResult,
        DownloadResult result,
        EnhancedPathTemplateProcessor pathProcessor)
    {
        var album = track.Album;
        return new ArtworkPopulationState
        {
            Track = track,
            PathResult = pathResult,
            Result = result,
            PathProcessor = pathProcessor,
            HttpClientFactory = _serviceProvider.GetService<IHttpClientFactory>(),
            AppleCatalog = _serviceProvider.GetService<AppleMusicCatalogService>(),
            SpotifyArtworkResolver = _serviceProvider.GetService<ISpotifyArtworkResolver>(),
            SpotifyIdResolver = _serviceProvider.GetService<ISpotifyIdResolver>(),
            DeezerClient = _serviceProvider.GetService<DeezerClient>(),
            Logger = _logger ?? NullLogger<DeezSpoTagDownloader>.Instance,
            AllowAppleCover = AllowsJpegArtwork(),
            AllowAppleArtist = AllowsJpegArtistArtwork(),
            AllowSpotifyCover = AllowsJpegArtwork(),
            AllowSpotifyArtist = AllowsJpegArtistArtwork(),
            FallbackOrder = ResolveArtworkFallbackOrder(),
            ArtistFallbackOrder = ResolveArtistArtworkFallbackOrder(),
            AppleArtworkSize = AppleQueueHelpers.GetAppleArtworkSize(Settings),
            AppleTrackId = ArtworkFallbackHelper.TryExtractAppleTrackId(track),
            DeezerTrackId = ArtworkFallbackHelper.TryExtractDeezerTrackId(track),
            Album = album,
            AlbumConstraint = ArtworkFallbackHelper.ResolveAlbumConstraintForArtwork(album?.Title),
            HasDeezerCover = album?.Pic != null && !string.IsNullOrEmpty(album.Pic.Md5),
            HasDeezerArtist = track.Album?.MainArtist?.Pic != null
                && !string.IsNullOrEmpty(track.Album.MainArtist.Pic.Md5)
        };
    }

    private async Task ResolveCoverArtworkAsync(ArtworkPopulationState state)
    {
        foreach (var source in state.FallbackOrder)
        {
            switch (source)
            {
                case "apple":
                    await TryResolveAppleCoverAsync(state);
                    break;
                case DeezerSource:
                    await TryResolveDeezerCoverAsync(state);
                    break;
                case SpotifySource:
                    await TryResolveSpotifyCoverAsync(state);
                    break;
            }

            if (!string.IsNullOrWhiteSpace(state.ResolvedCoverUrl) || state.UseDeezerCover)
            {
                return;
            }
        }
    }

    private async Task TryResolveAppleCoverAsync(ArtworkPopulationState state)
    {
        if (!state.AllowAppleCover || state.AppleCatalog == null || !string.IsNullOrWhiteSpace(state.AppleCoverUrl))
        {
            return;
        }

        var storefront = string.IsNullOrWhiteSpace(Settings.AppleMusic?.Storefront) ? "us" : Settings.AppleMusic!.Storefront;
        state.AppleCoverUrl = await AppleQueueHelpers.ResolveAppleCoverFromCatalogAsync(
            state.AppleCatalog,
            new AppleQueueHelpers.AppleCatalogCoverLookup
            {
                AppleId = state.AppleTrackId,
                Title = state.Track.Title,
                Artist = state.Track.MainArtist?.Name,
                Album = state.AlbumConstraint,
                Storefront = storefront,
                Size = state.AppleArtworkSize,
                Logger = state.Logger
            },
            CancellationToken.None);

        if (string.IsNullOrWhiteSpace(state.AppleCoverUrl) && state.HttpClientFactory != null)
        {
            state.AppleCoverUrl = await AppleQueueHelpers.ResolveAppleCoverAsync(
                state.HttpClientFactory,
                state.Track.Title,
                state.Track.MainArtist?.Name,
                state.AlbumConstraint,
                state.AppleArtworkSize,
                state.Logger,
                CancellationToken.None);
        }

        if (string.IsNullOrWhiteSpace(state.AppleCoverUrl))
        {
            return;
        }

        if (state.Logger.IsEnabled(LogLevel.Information))
        {
            state.Logger.LogInformation("Apple album art selected: {Url}", state.AppleCoverUrl);        }
        state.ResolvedCoverUrl = state.AppleCoverUrl;
        state.CoverIsApple = true;
    }

    private async Task TryResolveDeezerCoverAsync(ArtworkPopulationState state)
    {
        if (!string.IsNullOrWhiteSpace(state.ResolvedCoverUrl))
        {
            return;
        }

        var artworkTrackId = state.DeezerTrackId;
        if (state.DeezerClient != null && ArtworkFallbackHelper.IsCompilationLikeAlbum(state.Album))
        {
            var preferredTrackId = await TryResolvePreferredArtworkTrackIdAsync(
                state.DeezerClient,
                state.Track,
                state.AlbumConstraint,
                state.Logger,
                CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(preferredTrackId))
            {
                artworkTrackId = preferredTrackId;
            }
        }

        var deezerCoverUrl = await ArtworkFallbackHelper.TryResolveDeezerCoverAsync(
            state.DeezerClient,
            artworkTrackId,
            Settings.LocalArtworkSize,
            state.Logger,
            CancellationToken.None,
            state.AlbumConstraint);

        if (string.IsNullOrWhiteSpace(deezerCoverUrl)
            && !string.IsNullOrWhiteSpace(artworkTrackId)
            && !string.Equals(artworkTrackId, state.DeezerTrackId, StringComparison.Ordinal))
        {
            deezerCoverUrl = await ArtworkFallbackHelper.TryResolveDeezerCoverAsync(
                state.DeezerClient,
                state.DeezerTrackId,
                Settings.LocalArtworkSize,
                state.Logger,
                CancellationToken.None,
                state.AlbumConstraint);
        }

        if (!string.IsNullOrWhiteSpace(deezerCoverUrl))
        {
            if (state.Logger.IsEnabled(LogLevel.Information))
            {
                state.Logger.LogInformation("Deezer album art selected: {Url}", deezerCoverUrl);            }
            state.ResolvedCoverUrl = deezerCoverUrl;
            return;
        }

        if (state.HasDeezerCover)
        {
            state.UseDeezerCover = true;
        }
    }

    private async Task TryResolveSpotifyCoverAsync(ArtworkPopulationState state)
    {
        if (!state.AllowSpotifyCover || state.SpotifyArtworkResolver == null || state.SpotifyIdResolver == null)
        {
            return;
        }

        state.SpotifyId ??= await state.SpotifyIdResolver.ResolveTrackIdAsync(
            state.Track.Title ?? string.Empty,
            state.Track.MainArtist?.Name ?? string.Empty,
            state.AlbumConstraint,
            state.Track.ISRC,
            CancellationToken.None);
        if (string.IsNullOrWhiteSpace(state.SpotifyId))
        {
            return;
        }

        state.SpotifyCoverUrl = await state.SpotifyArtworkResolver.ResolveAlbumCoverUrlAsync(state.SpotifyId, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(state.SpotifyCoverUrl))
        {
            return;
        }

        if (_logger?.IsEnabled(LogLevel.Information) == true)
        {
            _logger?.LogInformation("Spotify album art selected: {Url}", state.SpotifyCoverUrl);        }
        state.ResolvedCoverUrl = state.SpotifyCoverUrl;
    }

    private async Task ResolveArtistArtworkAsync(ArtworkPopulationState state)
    {
        foreach (var source in state.ArtistFallbackOrder)
        {
            switch (source)
            {
                case "apple":
                    await TryResolveAppleArtistAsync(state);
                    break;
                case DeezerSource:
                    await TryResolveDeezerArtistAsync(state);
                    break;
                case SpotifySource:
                    await TryResolveSpotifyArtistAsync(state);
                    break;
            }

            if (!string.IsNullOrWhiteSpace(state.ResolvedArtistUrl) || state.UseDeezerArtist)
            {
                return;
            }
        }
    }

    private async Task TryResolveAppleArtistAsync(ArtworkPopulationState state)
    {
        if (!state.AllowAppleArtist || state.AppleCatalog == null || string.IsNullOrWhiteSpace(state.Track.MainArtist?.Name))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(state.AppleArtistUrl))
        {
            try
            {
                state.AppleArtistUrl = await ArtworkFallbackHelper.TryResolveAppleArtistImageAsync(
                    state.AppleCatalog,
                    state.HttpClientFactory,
                    Settings,
                    state.AppleTrackId,
                    state.Track.MainArtist!.Name,
                    state.Logger,
                    CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(state.AppleArtistUrl) && state.Logger.IsEnabled(LogLevel.Information))
                {
                    state.Logger.LogInformation("Apple artist art selected: {Url}", state.AppleArtistUrl);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (state.Logger.IsEnabled(LogLevel.Debug))
                {
                    state.Logger.LogDebug(ex, "Apple artist lookup failed for {ArtistName}", state.Track.MainArtist!.Name);                }
            }
        }

        if (string.IsNullOrWhiteSpace(state.AppleArtistUrl))
        {
            return;
        }

        state.ResolvedArtistUrl = state.AppleArtistUrl;
        state.ArtistIsApple = true;
    }

    private async Task TryResolveDeezerArtistAsync(ArtworkPopulationState state)
    {
        if (string.IsNullOrWhiteSpace(state.ResolvedArtistUrl))
        {
            var deezerArtistUrl = await ArtworkFallbackHelper.TryResolveDeezerArtistImageAsync(
                state.DeezerClient,
                state.DeezerTrackId,
                Settings.LocalArtworkSize,
                state.Logger,
                CancellationToken.None,
                state.Track.MainArtist?.Name);
            if (!string.IsNullOrWhiteSpace(deezerArtistUrl))
            {
                if (state.Logger.IsEnabled(LogLevel.Information))
                {
                    state.Logger.LogInformation("Deezer artist art selected: {Url}", deezerArtistUrl);                }
                state.ResolvedArtistUrl = deezerArtistUrl;
                return;
            }
        }

        if (state.HasDeezerArtist)
        {
            state.UseDeezerArtist = true;
        }
    }

    private async Task TryResolveSpotifyArtistAsync(ArtworkPopulationState state)
    {
        if (!state.AllowSpotifyArtist || state.SpotifyArtworkResolver == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(state.SpotifyArtistUrl) && !string.IsNullOrWhiteSpace(state.SpotifyId))
        {
            state.SpotifyArtistUrl = await state.SpotifyArtworkResolver.ResolveArtistImageUrlAsync(state.SpotifyId, CancellationToken.None);
        }

        if (string.IsNullOrWhiteSpace(state.SpotifyArtistUrl))
        {
            state.SpotifyArtistUrl = await state.SpotifyArtworkResolver.ResolveArtistImageByNameAsync(
                state.Track.MainArtist?.Name,
                CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(state.SpotifyArtistUrl) && _logger?.IsEnabled(LogLevel.Information) == true)
            {
                _logger.LogInformation("Spotify artist art selected by name: {Url}", state.SpotifyArtistUrl);
            }
        }
        else
        {
            if (_logger?.IsEnabled(LogLevel.Information) == true)
            {
                _logger?.LogInformation("Spotify artist art selected: {Url}", state.SpotifyArtistUrl);            }
        }

        if (!string.IsNullOrWhiteSpace(state.SpotifyArtistUrl))
        {
            state.ResolvedArtistUrl = state.SpotifyArtistUrl;
        }
    }

    private static bool EnsureAlbumIsReadyForArtwork(ArtworkPopulationState state)
    {
        if (state.Album == null)
        {
            if (string.IsNullOrWhiteSpace(state.ResolvedCoverUrl))
            {
                return false;
            }

            state.Album = new CoreAlbum("Unknown Album");
            state.Track.Album = state.Album;
        }

        if (!string.IsNullOrWhiteSpace(state.ResolvedCoverUrl))
        {
            return true;
        }

        if (state.Album.Pic == null || !state.UseDeezerCover)
        {
            return false;
        }

        EnsurePictureType(state.Album.Pic, "cover");
        return true;
    }

    private async Task PopulateEmbeddedCoverAsync(ArtworkPopulationState state)
    {
        state.EmbeddedSize = Settings.EmbedMaxQualityCover ? Settings.LocalArtworkSize : Settings.EmbeddedArtworkSize;
        var embeddedFormat = ResolveEmbeddedFormat();
        var embeddedUrl = !string.IsNullOrWhiteSpace(state.ResolvedCoverUrl)
            ? state.ResolvedCoverUrl
            : GetPictureUrl(state.Album?.Pic, state.EmbeddedSize, embeddedFormat);
        if (string.IsNullOrEmpty(embeddedUrl) || state.Album == null)
        {
            return;
        }

        string extension;
        if (state.CoverIsApple)
        {
            extension = $".{AppleQueueHelpers.GetAppleArtworkExtension(state.ResolvedCoverUrl ?? string.Empty, AppleQueueHelpers.GetAppleArtworkFormat(Settings))}";
        }
        else
        {
            extension = embeddedUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
        }
        var prefix = state.Track.Playlist != null ? $"pl{state.Track.Playlist.Id}" : $"alb{state.Album.Id}";
        var embeddedPath = Path.Join(_tempDir, $"{prefix}_{state.EmbeddedSize}{extension}");
        var downloadedPath = await QueueArtworkDownloadAsync(embeddedPath, embeddedUrl, state.CoverIsApple, state.EmbeddedSize, state.Logger);
        if (!string.IsNullOrEmpty(downloadedPath))
        {
            state.Album.EmbeddedCoverPath = downloadedPath;
        }

        CoverQueue.TryRemove(embeddedPath, out _);
    }

    private void PopulateAlbumArtworkUrls(ArtworkPopulationState state)
    {
        if (!Settings.SaveArtwork || string.IsNullOrEmpty(state.PathResult.CoverPath) || state.Album == null)
        {
            return;
        }

        state.Result.AlbumPath = state.PathResult.CoverPath;
        state.Result.AlbumFilename = state.PathProcessor.GenerateAlbumName(
            Settings.CoverImageTemplate,
            state.Album,
            Settings,
            state.Track.Playlist);

        foreach (var format in SplitArtworkFormats().Where(format =>
                     (format == "jpg" || format == "png")
                     && (state.Album.Pic == null || !IsStaticPicture(state.Album.Pic) || format == "jpg")))
        {
            TryAddAlbumArtworkUrl(state, format);
        }
    }

    private void TryAddAlbumArtworkUrl(ArtworkPopulationState state, string format)
    {
        if (!string.IsNullOrWhiteSpace(state.ResolvedCoverUrl))
        {
            if (!state.CoverIsApple && format != "jpg")
            {
                return;
            }

            state.Result.AlbumUrls ??= new List<ImageUrl>();
            state.Result.AlbumUrls.Add(new ImageUrl
            {
                Url = state.ResolvedCoverUrl,
                Extension = format
            });
            return;
        }

        var extendedFormat = format == "jpg" ? $"jpg-{Settings.JpegImageQuality}" : format;
        var url = GetPictureUrl(state.Album?.Pic, Settings.LocalArtworkSize, extendedFormat);
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        state.Result.AlbumUrls ??= new List<ImageUrl>();
        state.Result.AlbumUrls.Add(new ImageUrl { Url = url, Extension = format });
    }

    private async Task PopulateFallbackEmbeddedCoverAsync(ArtworkPopulationState state)
    {
        if (state.Album == null
            || !string.IsNullOrEmpty(state.Album.EmbeddedCoverPath)
            || state.Result.AlbumUrls is not { Count: > 0 })
        {
            return;
        }

        var fallbackUrl = state.Result.AlbumUrls[0].Url;
        if (string.IsNullOrEmpty(fallbackUrl))
        {
            return;
        }

        var extension = fallbackUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
        var prefix = state.Track.Playlist != null ? $"pl{state.Track.Playlist.Id}" : $"alb{state.Album.Id}";
        var fallbackPath = Path.Join(_tempDir, $"{prefix}_embedded{extension}");
        var downloadedPath = await QueueArtworkDownloadAsync(
            fallbackPath,
            fallbackUrl,
            IsAppleArtworkUrl(fallbackUrl),
            state.EmbeddedSize,
            state.Logger);
        if (!string.IsNullOrEmpty(downloadedPath))
        {
            state.Album.EmbeddedCoverPath = downloadedPath;
        }

        CoverQueue.TryRemove(fallbackPath, out _);
    }

    private void PopulateArtistArtworkUrls(ArtworkPopulationState state)
    {
        if (Settings.SaveArtworkArtist && !string.IsNullOrWhiteSpace(state.ResolvedArtistUrl))
        {
            foreach (var format in SplitArtworkFormats().Where(format => format == "jpg" || format == "png"))
            {
                state.Result.ArtistUrls ??= new List<ImageUrl>();
                if (state.ArtistIsApple)
                {
                    state.Result.ArtistUrls.Add(new ImageUrl
                    {
                        Url = state.ResolvedArtistUrl,
                        Extension = format
                    });
                }
                else if (format == "jpg")
                {
                    state.Result.ArtistUrls.Add(new ImageUrl
                    {
                        Url = state.ResolvedArtistUrl,
                        Extension = "jpg"
                    });
                }
            }

            state.Result.ArtistPath = state.PathResult.ArtistPath ?? state.PathResult.CoverPath ?? state.PathResult.FilePath;
            state.Result.ArtistFilename = state.PathProcessor.GenerateArtistName(
                Settings.ArtistImageTemplate,
                state.Track.Album?.MainArtist,
                Settings,
                state.Track.Album?.RootArtist);
            return;
        }

        if (!Settings.SaveArtworkArtist
            || state.Track.Album?.MainArtist?.Pic == null
            || !state.UseDeezerArtist
            || string.IsNullOrEmpty(state.Track.Album.MainArtist.Pic.Md5))
        {
            return;
        }

        EnsurePictureType(state.Track.Album.MainArtist.Pic, "artist");
        var artistUrl = GetPictureUrl(state.Track.Album.MainArtist.Pic, Settings.LocalArtworkSize, $"jpg-{Settings.JpegImageQuality}");
        if (string.IsNullOrEmpty(artistUrl))
        {
            return;
        }

        state.Result.ArtistUrls ??= new List<ImageUrl>();
        state.Result.ArtistUrls.Add(new ImageUrl { Url = artistUrl, Extension = "jpg" });
        state.Result.ArtistPath = state.PathResult.ArtistPath ?? state.PathResult.CoverPath ?? state.PathResult.FilePath;
        state.Result.ArtistFilename = state.PathProcessor.GenerateArtistName(
            Settings.ArtistImageTemplate,
            state.Track.Album.MainArtist,
            Settings,
            state.Track.Album.RootArtist);
    }

    private void PopulatePlaylistArtwork(ArtworkPopulationState state)
    {
        if (state.Track.Playlist == null)
        {
            return;
        }

        lock (_playlistLock)
        {
            if (PlaylistUrls.Count == 0)
            {
                foreach (var format in SplitArtworkFormats().Where(format =>
                             (format == "jpg" || format == "png")
                             && (!IsStaticPicture(state.Track.Playlist.Pic) || format == "jpg")))
                {
                    EnsurePictureType(state.Track.Playlist.Pic, "playlist");
                    var extendedFormat = format == "jpg" ? $"jpg-{Settings.JpegImageQuality}" : format;
                    var url = GetPictureUrl(state.Track.Playlist.Pic, Settings.LocalArtworkSize, extendedFormat);
                    if (string.IsNullOrEmpty(url))
                    {
                        continue;
                    }

                    PlaylistUrls.Add(new PlaylistUrl { Url = url, Extension = format });
                }
            }

            if (!string.IsNullOrEmpty(PlaylistCovername))
            {
                return;
            }

            state.Track.Playlist.Bitrate = state.Track.Bitrate;
            state.Track.Playlist.DateString = state.Track.Playlist.Date.Format(Settings.DateFormat);

            var playlistAlbum = new CoreAlbum($"pl_{state.Track.Playlist.Id}", state.Track.Playlist.Title);
            playlistAlbum.MakePlaylistCompilation(state.Track.Playlist);
            playlistAlbum.Genre = new List<string> { "Compilation" };
            playlistAlbum.Bitrate = state.Track.Bitrate;
            playlistAlbum.DateString = state.Track.Playlist.DateString;
            PlaylistCovername = state.PathProcessor.GenerateAlbumName(
                Settings.CoverImageTemplate,
                playlistAlbum,
                Settings,
                state.Track.Playlist);
        }
    }

    private sealed class ArtworkPopulationState
    {
        public required CoreTrack Track { get; init; }
        public required PathGenerationResult PathResult { get; init; }
        public required DownloadResult Result { get; init; }
        public required EnhancedPathTemplateProcessor PathProcessor { get; init; }
        public required ILogger Logger { get; init; }
        public required List<string> FallbackOrder { get; init; }
        public required List<string> ArtistFallbackOrder { get; init; }

        public IHttpClientFactory? HttpClientFactory { get; init; }
        public AppleMusicCatalogService? AppleCatalog { get; init; }
        public ISpotifyArtworkResolver? SpotifyArtworkResolver { get; init; }
        public ISpotifyIdResolver? SpotifyIdResolver { get; init; }
        public DeezerClient? DeezerClient { get; init; }

        public bool AllowAppleCover { get; init; }
        public bool AllowAppleArtist { get; init; }
        public bool AllowSpotifyCover { get; init; }
        public bool AllowSpotifyArtist { get; init; }
        public int AppleArtworkSize { get; init; }

        public string? AppleTrackId { get; init; }
        public string? DeezerTrackId { get; init; }
        public CoreAlbum? Album { get; set; }
        public string? AlbumConstraint { get; init; }
        public bool HasDeezerCover { get; init; }
        public bool HasDeezerArtist { get; init; }

        public string? ResolvedCoverUrl { get; set; }
        public string? ResolvedArtistUrl { get; set; }
        public string? SpotifyId { get; set; }
        public string? AppleCoverUrl { get; set; }
        public string? AppleArtistUrl { get; set; }
        public string? SpotifyCoverUrl { get; set; }
        public string? SpotifyArtistUrl { get; set; }

        public bool UseDeezerCover { get; set; }
        public bool UseDeezerArtist { get; set; }
        public bool CoverIsApple { get; set; }
        public bool ArtistIsApple { get; set; }
        public int EmbeddedSize { get; set; }
    }
    private IEnumerable<string> SplitArtworkFormats()
    {
        return Settings.LocalArtworkFormat
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(format => format.Trim());
    }

    private Task<string?> QueueArtworkDownloadAsync(string outputPath, string rawUrl, bool isAppleArtwork, int size, ILogger logger)
    {
        return CoverQueue.GetOrAdd(outputPath, async requestedOutputPath =>
        {
            if (isAppleArtwork)
            {
                return await AppleQueueHelpers.DownloadAppleArtworkAsync(
                    _imageDownloader,
                    new AppleQueueHelpers.AppleArtworkDownloadRequest
                    {
                        RawUrl = rawUrl,
                        OutputPath = requestedOutputPath,
                        Settings = Settings,
                        Size = size,
                        Overwrite = Settings.OverwriteFile,
                        PreferMaxQuality = true,
                        Logger = logger
                    },
                    CancellationToken.None);
            }

            return await _imageDownloader.DownloadImageAsync(rawUrl, requestedOutputPath, Settings.OverwriteFile, true);
        });
    }

    private string ResolveEmbeddedFormat()
    {
        var formats = SplitArtworkFormats().Select(format => format.ToLowerInvariant()).ToList();
        if (formats.Contains("png"))
        {
            return "png";
        }

        return $"jpg-{Settings.JpegImageQuality}";
    }

    private List<string> ResolveArtworkFallbackOrder()
    {
        return ArtworkFallbackHelper.ResolveOrder(Settings).ToList();
    }

    private List<string> ResolveArtistArtworkFallbackOrder()
    {
        var configuredOrder = ArtworkFallbackHelper.ResolveArtistOrder(Settings)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .ToList();

        configuredOrder.RemoveAll(source => string.Equals(source, DeezerSource, StringComparison.OrdinalIgnoreCase));
        configuredOrder.Insert(0, DeezerSource);

        return configuredOrder;
    }

    private static async Task<string?> TryResolvePreferredArtworkTrackIdAsync(
        DeezerClient deezerClient,
        CoreTrack track,
        string? albumConstraint,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        return await ArtworkFallbackHelper.TryResolvePreferredArtworkTrackIdAsync(
            deezerClient,
            track,
            albumConstraint,
            logger,
            cancellationToken);
    }

    private static bool ShouldOverwriteArtistArtwork(IEnumerable<ImageUrl>? artistUrls)
    {
        if (artistUrls == null)
        {
            return false;
        }

        foreach (var image in artistUrls)
        {
            if (string.IsNullOrWhiteSpace(image?.Url))
            {
                continue;
            }

            if (image.Url.Contains("dzcdn.net/images/artist/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task SaveLocalAlbumArtworkAsync(DownloadResult track, bool resolveIoPath)
    {
        if (!Settings.SaveArtwork || string.IsNullOrEmpty(track.AlbumPath) || track.AlbumUrls is not { Count: > 0 })
        {
            return;
        }

        var albumPath = resolveIoPath ? DownloadPathResolver.ResolveIoPath(track.AlbumPath) : track.AlbumPath;
        foreach (var image in track.AlbumUrls)
        {
            var imagePath = Path.Join(albumPath, $"{track.AlbumFilename}.{image.Extension}");
            await _imageDownloader.DownloadImageAsync(image.Url, imagePath, Settings.OverwriteFile, true);
        }
    }

    private async Task SaveLocalArtistArtworkAsync(DownloadResult track, bool resolveIoPath)
    {
        if (!Settings.SaveArtworkArtist || string.IsNullOrEmpty(track.ArtistPath) || track.ArtistUrls is not { Count: > 0 })
        {
            return;
        }

        var artistPath = resolveIoPath ? DownloadPathResolver.ResolveIoPath(track.ArtistPath) : track.ArtistPath;
        var overwriteArtistArtwork = ShouldOverwriteArtistArtwork(track.ArtistUrls);
        foreach (var image in track.ArtistUrls)
        {
            var imagePath = Path.Join(artistPath, $"{track.ArtistFilename}.{image.Extension}");
            await _imageDownloader.DownloadImageAsync(
                image.Url,
                imagePath,
                overwriteArtistArtwork ? "y" : Settings.OverwriteFile,
                true);
        }
    }

    private bool AllowsJpegArtwork()
    {
        var formats = (Settings.LocalArtworkFormat ?? "jpg")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(format => format.ToLowerInvariant());
        return formats.Contains("jpg");
    }

    private bool AllowsJpegArtistArtwork()
    {
        return AllowsJpegArtwork();
    }

    private static string GetPictureUrl(CorePicture? picture, int size, string format)
    {
        if (picture == null)
        {
            return string.Empty;
        }

        return picture is CoreStaticPicture staticPicture
            ? staticPicture.GetURL(size, format)
            : picture.GetURL(size, format);
    }

    private static bool IsStaticPicture(CorePicture picture)
    {
        return picture is CoreStaticPicture;
    }

    private static void EnsurePictureType(CorePicture picture, string fallbackType)
    {
        if (string.IsNullOrEmpty(picture.Type) ||
            !string.Equals(picture.Type, fallbackType, StringComparison.OrdinalIgnoreCase))
        {
            picture.Type = fallbackType;
        }
    }

    private static bool IsAppleArtworkUrl(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && url.Contains("mzstatic.com", StringComparison.OrdinalIgnoreCase);

    private static string GetDataValue(object? data, string key, string fallback)
    {
        if (data == null)
            return fallback;

        if (data is Dictionary<string, object> dict && dict.TryGetValue(key, out var dictValue))
            return dictValue?.ToString() ?? fallback;

        if (data is System.Text.Json.JsonElement element
            && element.ValueKind == System.Text.Json.JsonValueKind.Object
            && element.TryGetProperty(key, out var property))
        {
            return property.ToString() ?? fallback;
        }

        var prop = data.GetType().GetProperty(key);
        if (prop != null)
        {
            var value = prop.GetValue(data);
            if (value != null)
                return value.ToString() ?? fallback;
        }

        return fallback;
    }

    private void AddDownloadFile(DownloadResult result)
    {
        var entry = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(result.Filename))
            entry["filename"] = result.Filename;
        if (!string.IsNullOrEmpty(result.Path))
            entry["path"] = result.Path;
        if (result.Data != null)
            entry["data"] = result.Data;
        if (result.Searched)
            entry["searched"] = true;
        if (!string.IsNullOrEmpty(result.AlbumPath))
            entry["albumPath"] = result.AlbumPath;
        if (!string.IsNullOrEmpty(result.AlbumFilename))
            entry["albumFilename"] = result.AlbumFilename;
        if (result.AlbumUrls is { Count: > 0 })
        {
            entry["albumURLs"] = result.AlbumUrls.Select(url => new Dictionary<string, object>
            {
                ["url"] = url.Url,
                ["ext"] = url.Extension
            }).ToList();
        }
        if (!string.IsNullOrEmpty(result.ArtistPath))
            entry["artistPath"] = result.ArtistPath;
        if (!string.IsNullOrEmpty(result.ArtistFilename))
            entry["artistFilename"] = result.ArtistFilename;
        if (result.ArtistUrls is { Count: > 0 })
        {
            entry["artistURLs"] = result.ArtistUrls.Select(url => new Dictionary<string, object>
            {
                ["url"] = url.Url,
                ["ext"] = url.Extension
            }).ToList();
        }

        if (entry.Count > 0)
            DownloadObject.Files.Add(entry);
    }

    private void AddLyricsFiles(PathGenerationResult pathResult)
    {
        if (string.IsNullOrWhiteSpace(pathResult.FilePath) || string.IsNullOrWhiteSpace(pathResult.Filename))
        {
            return;
        }

        var filePathIo = DownloadPathResolver.ResolveIoPath(pathResult.FilePath);
        var lrcPath = Path.Join(filePathIo, $"{pathResult.Filename}.lrc");
        var ttmlPath = Path.Join(filePathIo, $"{pathResult.Filename}.ttml");
        var txtPath = Path.Join(filePathIo, $"{pathResult.Filename}.txt");

        AddLyricsFileEntry(lrcPath, pathResult.ExtrasPath);
        AddLyricsFileEntry(ttmlPath, pathResult.ExtrasPath);
        AddLyricsFileEntry(txtPath, pathResult.ExtrasPath);
    }

    private void AddLyricsFileEntry(string fullPath, string extrasPath)
    {
        if (!System.IO.File.Exists(fullPath))
        {
            return;
        }

        var displayPath = DownloadPathResolver.NormalizeDisplayPath(fullPath);
        var filename = string.IsNullOrWhiteSpace(extrasPath)
            ? Path.GetFileName(displayPath)
            : displayPath.Substring(extrasPath.Length + 1);

        var entry = new Dictionary<string, object>
        {
            ["filename"] = filename,
            ["path"] = displayPath
        };

        DownloadObject.Files.Add(entry);
    }

    private static (bool HasAny, bool HasLrc, bool HasTtml, bool HasTxt) GetLyricsSidecarState(string audioPath)
    {
        if (string.IsNullOrWhiteSpace(audioPath))
        {
            return (false, false, false, false);
        }

        var directory = Path.GetDirectoryName(audioPath);
        var baseName = Path.GetFileNameWithoutExtension(audioPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseName))
        {
            return (false, false, false, false);
        }

        var hasLrc = System.IO.File.Exists(Path.Join(directory, $"{baseName}.lrc"));
        var hasTtml = System.IO.File.Exists(Path.Join(directory, $"{baseName}.ttml"));
        var hasTxt = System.IO.File.Exists(Path.Join(directory, $"{baseName}.txt"));
        return (hasLrc || hasTtml || hasTxt, hasLrc, hasTtml, hasTxt);
    }

    // Helper methods for deezspotag compatibility
    private static string GetArtistName(Dictionary<string, object> trackAPI)
    {
        if (trackAPI.TryGetValue(ArtistKey, out var artist))
        {
            if (artist is Dictionary<string, object> artistDict)
            {
                return artistDict.GetValueOrDefault("name")?.ToString() ?? UnknownValue;
            }
            return artist?.ToString() ?? UnknownValue;
        }
        return UnknownValue;
    }

    private string GeneratePlaylistFilename()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var pathProcessor = scope.ServiceProvider.GetRequiredService<EnhancedPathTemplateProcessor>();
            var downloadObjectAdapter = new DownloadObjectAdapter(DownloadObject, Listener);
            var filename = pathProcessor.GenerateDownloadObjectName(Settings.PlaylistFilenameTemplate, downloadObjectAdapter, Settings);
            if (!string.IsNullOrWhiteSpace(filename))
            {
                return filename;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to generate playlist filename, falling back to template");
        }

        return Settings.PlaylistFilenameTemplate?.Replace("%title%", DownloadObject.Title ?? "playlist") ?? "playlist";
    }

    private static string ShellEscape(string input)
    {
        return $"\"{input.Replace("\"", "\\\"")}\"";
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _cancellationTokenSource.Dispose();
        }

        _disposed = true;
    }

    /// <summary>
    /// EXACT PORT of deezspotag formatsName mapping from Track.ts
    /// </summary>
    private static string GetFormatName(int bitrate)
    {
        return bitrate switch
        {
            9 => "FLAC",      // TrackFormats.FLAC
            3 => "MP3_320",   // TrackFormats.MP3_320
            1 => "MP3_128",   // TrackFormats.MP3_128
            0 => Mp3MiscFormat,  // TrackFormats.LOCAL
            8 => Mp3MiscFormat,  // TrackFormats.DEFAULT
            13 => "MP4_RA1",  // TrackFormats.MP4_RA1
            14 => "MP4_RA2",  // TrackFormats.MP4_RA2
            15 => "MP4_RA3",  // TrackFormats.MP4_RA3
            _ => Mp3MiscFormat
        };
    }

    /// <summary>
    /// Convert dictionary to ApiTrack, handling JsonElement conversion properly
    /// </summary>
    private ApiTrack ConvertToApiTrack(Dictionary<string, object> trackAPI)
    {
        try
        {
            var cleanedDict = ConvertJsonElementsToValues(trackAPI);
            NormalizeApiTrackDict(cleanedDict);
            var json = JsonConvert.SerializeObject(cleanedDict);
            return JsonConvert.DeserializeObject<ApiTrack>(json) ?? new ApiTrack();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to convert trackAPI to ApiTrack, using fallback");

            // Fallback that adapts to either string or long ID properties using reflection
            var apiTrack = new ApiTrack
            {
                Title = GetStringValue(trackAPI, TitleKey),
                Artist = new ApiArtist
                {
                    Name = GetStringValue(trackAPI, ArtistKey, "name")
                }
            };

            // Set Track.Id flexibly
            TrySetFlexibleId(apiTrack, GetStringValue(trackAPI, "id"));

            // Ensure Artist exists, then set Id flexibly
            if (apiTrack.Artist == null) apiTrack.Artist = new ApiArtist();
            TrySetFlexibleId(apiTrack.Artist, GetStringValue(trackAPI, ArtistKey, "id"));

            return apiTrack;
        }
    }

    private ApiAlbum ConvertToApiAlbum(Dictionary<string, object> albumAPI)
    {
        try
        {
            var cleanedDict = ConvertJsonElementsToValues(albumAPI);
            NormalizeApiAlbumDict(cleanedDict);
            var json = JsonConvert.SerializeObject(cleanedDict);
            return JsonConvert.DeserializeObject<ApiAlbum>(json) ?? new ApiAlbum();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to convert albumAPI to ApiAlbum, using fallback");

            var apiAlbum = new ApiAlbum
            {
                Title = GetStringValue(albumAPI, TitleKey)
            };
            TrySetFlexibleId(apiAlbum, GetStringValue(albumAPI, "id"));
            return apiAlbum;
        }
    }

    private static void NormalizeApiTrackDict(Dictionary<string, object> dict)
    {
        NormalizeNumericField(dict, "duration");
        NormalizeNumericField(dict, "track_position");
        NormalizeNumericField(dict, "disk_number");
        NormalizeNumericField(dict, "rank");
        NormalizeNumericField(dict, "explicit_content_lyrics");
        NormalizeNumericField(dict, "explicit_content_cover");
        NormalizeNumericField(dict, "position");
        NormalizeNumericField(dict, "size");
        NormalizeNumericField(dict, "bpm", allowDouble: true);
        NormalizeNumericField(dict, "gain", allowDouble: true);

        NormalizeBoolField(dict, "explicit_lyrics");
        NormalizeBoolField(dict, "readable");
        NormalizeBoolField(dict, "unseen");

        NormalizeNestedNumericField(dict, ArtistKey, "id", useLong: true);
        NormalizeNestedNumericField(dict, "album", "id", useLong: true);
    }

    private static void NormalizeApiAlbumDict(Dictionary<string, object> dict)
    {
        NormalizeNumericField(dict, "id", useLong: true);
        NormalizeNumericField(dict, "nb_tracks");
        NormalizeNumericField(dict, "nb_disk");
        NormalizeNumericField(dict, "duration");
        NormalizeNumericField(dict, "fans");
        NormalizeNumericField(dict, "explicit_content_lyrics");
        NormalizeNumericField(dict, "explicit_content_cover");
        NormalizeNumericField(dict, "genre_id");

        NormalizeBoolField(dict, "explicit_lyrics");
        NormalizeBoolField(dict, "available");

        NormalizeNestedNumericField(dict, ArtistKey, "id", useLong: true);
        NormalizeNestedNumericField(dict, "root_artist", "id", useLong: true);
    }

    private static void NormalizeNumericField(
        Dictionary<string, object> dict,
        string key,
        bool allowDouble = false,
        bool useLong = false)
    {
        if (!dict.TryGetValue(key, out var value) || value == null)
        {
            return;
        }

        if (value is string textValue)
        {
            NormalizeNumericStringValue(dict, key, textValue, allowDouble, useLong);
            return;
        }

        if (allowDouble)
        {
            NormalizeDoubleValue(dict, key, value);
            return;
        }

        if (useLong)
        {
            NormalizeLongValue(dict, key, value);
            return;
        }

        NormalizeIntValue(dict, key, value);
    }

    private static void NormalizeNumericStringValue(
        Dictionary<string, object> dict,
        string key,
        string textValue,
        bool allowDouble,
        bool useLong)
    {
        if (allowDouble)
        {
            if (double.TryParse(textValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedDouble))
            {
                dict[key] = parsedDouble;
            }
            return;
        }

        if (useLong)
        {
            if (long.TryParse(textValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedLong))
            {
                dict[key] = parsedLong;
            }
            else if (double.TryParse(textValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedLongFromDouble))
            {
                dict[key] = (long)Math.Truncate(parsedLongFromDouble);
            }
            return;
        }

        if (int.TryParse(textValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedInt))
        {
            dict[key] = parsedInt;
        }
        else if (double.TryParse(textValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedIntFromDouble))
        {
            dict[key] = (int)Math.Truncate(parsedIntFromDouble);
        }
    }

    private static void NormalizeDoubleValue(Dictionary<string, object> dict, string key, object value)
    {
        switch (value)
        {
            case double:
                return;
            case float floatValue:
                dict[key] = (double)floatValue;
                return;
            case decimal decimalValue:
                dict[key] = (double)decimalValue;
                return;
            case long longValue:
                dict[key] = (double)longValue;
                return;
            case int intValue:
                dict[key] = (double)intValue;
                return;
        }
    }

    private static void NormalizeLongValue(Dictionary<string, object> dict, string key, object value)
    {
        switch (value)
        {
            case long:
                return;
            case int intValue:
                dict[key] = (long)intValue;
                return;
            case double doubleValue:
                dict[key] = (long)Math.Truncate(doubleValue);
                return;
            case float floatValue:
                dict[key] = (long)Math.Truncate(floatValue);
                return;
            case decimal decimalValue:
                dict[key] = (long)Math.Truncate(decimalValue);
                return;
        }
    }

    private static void NormalizeIntValue(Dictionary<string, object> dict, string key, object value)
    {
        switch (value)
        {
            case int:
                return;
            case long longValue:
                dict[key] = (int)Math.Clamp(longValue, int.MinValue, int.MaxValue);
                return;
            case double doubleValue:
                dict[key] = (int)Math.Truncate(doubleValue);
                return;
            case float floatValue:
                dict[key] = (int)Math.Truncate(floatValue);
                return;
            case decimal decimalValue:
                dict[key] = (int)Math.Truncate(decimalValue);
                return;
        }
    }

    private static void NormalizeBoolField(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value == null)
        {
            return;
        }

        if (value is bool)
        {
            return;
        }

        if (value is string textValue)
        {
            if (bool.TryParse(textValue, out var parsedBool))
            {
                dict[key] = parsedBool;
                return;
            }
            if (int.TryParse(textValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedInt))
            {
                dict[key] = parsedInt != 0;
            }
            return;
        }

        if (value is int intValue)
        {
            dict[key] = intValue != 0;
            return;
        }
        if (value is long longValue)
        {
            dict[key] = longValue != 0;
            return;
        }
        if (value is double doubleValue)
        {
            dict[key] = Math.Abs(doubleValue) > 0;
        }
    }

    private static void NormalizeNestedNumericField(
        Dictionary<string, object> dict,
        string parentKey,
        string childKey,
        bool useLong = false)
    {
        if (!dict.TryGetValue(parentKey, out var parent) || parent == null)
        {
            return;
        }

        if (parent is Dictionary<string, object> parentDict)
        {
            NormalizeNumericField(parentDict, childKey, useLong: useLong);
        }
    }

    private ApiPlaylist ConvertToApiPlaylist(Dictionary<string, object> playlistAPI)
    {
        try
        {
            var cleanedDict = ConvertJsonElementsToValues(playlistAPI);
            var json = JsonConvert.SerializeObject(cleanedDict);
            return JsonConvert.DeserializeObject<ApiPlaylist>(json) ?? new ApiPlaylist();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to convert playlistAPI to ApiPlaylist, using fallback");

            var apiPlaylist = new ApiPlaylist
            {
                Title = GetStringValue(playlistAPI, TitleKey)
            };
            TrySetFlexibleId(apiPlaylist, GetStringValue(playlistAPI, "id"));
            return apiPlaylist;
        }
    }

    /// <summary>
    /// Sets an object's "Id" property whether it's string or long (or int).
    /// Uses reflection to avoid compile-time type mismatches across model variants.
    /// </summary>
    private static void TrySetFlexibleId(object target, string idText)
    {
        try
        {
            var prop = target.GetType().GetProperty("Id");
            if (prop == null || !prop.CanWrite) return;

            if (prop.PropertyType == typeof(string))
            {
                prop.SetValue(target, idText);
            }
            else if (prop.PropertyType == typeof(long))
            {
                if (long.TryParse(idText, out var l)) prop.SetValue(target, l);
            }
            else if (prop.PropertyType == typeof(int))
            {
                if (int.TryParse(idText, out var i)) prop.SetValue(target, i);
            }
            else
            {
                // Best-effort: try change type
                var converted = Convert.ChangeType(idText, prop.PropertyType);
                prop.SetValue(target, converted);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // swallow – ID is optional for fallbacks
        }
    }

    private Dictionary<string, object> ConvertJsonElementsToValues(Dictionary<string, object> dict)
    {
        var result = new Dictionary<string, object>();

        foreach (var kvp in dict)
        {
            result[kvp.Key] = ConvertJsonElementValue(kvp.Value);
        }

        return result;
    }

    private object ConvertJsonElementValue(object value)
    {
        if (value == null) return null!;

        if (value.GetType().Name == "JsonElement")
        {
            var jsonElement = (System.Text.Json.JsonElement)value;
            return jsonElement.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => jsonElement.GetString() ?? "",
                System.Text.Json.JsonValueKind.Number => jsonElement.TryGetInt64(out var longVal) ? longVal : jsonElement.GetDouble(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Null => null!,
                System.Text.Json.JsonValueKind.Object => ConvertJsonElementObject(jsonElement),
                System.Text.Json.JsonValueKind.Array => ConvertJsonElementArray(jsonElement),
                _ => value.ToString() ?? ""
            };
        }

        if (value is Dictionary<string, object> nestedDict)
        {
            return ConvertJsonElementsToValues(nestedDict);
        }

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            var list = new List<object>();
            foreach (var item in enumerable)
            {
                list.Add(ConvertJsonElementValue(item));
            }
            return list;
        }

        return value;
    }

    private Dictionary<string, object> ConvertJsonElementObject(System.Text.Json.JsonElement jsonElement)
    {
        var result = new Dictionary<string, object>();

        foreach (var property in jsonElement.EnumerateObject())
        {
            result[property.Name] = ConvertJsonElementValue(property.Value);
        }

        return result;
    }

    private List<object> ConvertJsonElementArray(System.Text.Json.JsonElement jsonElement)
    {
        var result = new List<object>();

        foreach (var item in jsonElement.EnumerateArray())
        {
            result.Add(ConvertJsonElementValue(item));
        }

        return result;
    }

    private static string GetStringValue(Dictionary<string, object> dict, params string[] keys)
    {
        object? current = dict;

        foreach (var key in keys)
        {
            if (current is Dictionary<string, object> currentDict && currentDict.TryGetValue(key, out var value))
            {
                current = value;
            }
            else
            {
                return "";
            }
        }

        if (current == null) return "";

        if (current.GetType().Name == "JsonElement")
        {
            var jsonElement = (System.Text.Json.JsonElement)current;
            return jsonElement.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => jsonElement.GetString() ?? "",
                System.Text.Json.JsonValueKind.Number => jsonElement.GetRawText(), // numeric IDs as string
                System.Text.Json.JsonValueKind.True => "true",
                System.Text.Json.JsonValueKind.False => "false",
                System.Text.Json.JsonValueKind.Null => "",
                _ => jsonElement.GetRawText()
            };
        }

        return current.ToString() ?? "";
    }

    private static Dictionary<string, object>? NormalizeApiDictionary(Dictionary<string, object>? apiDict, params string[] keysToCheck)
    {
        if (apiDict == null || apiDict.Count == 0)
        {
            return null;
        }

        var hasValidValue = false;
        foreach (var key in keysToCheck)
        {
            var value = GetStringValue(apiDict, key);
            if (!string.IsNullOrWhiteSpace(value) && value != "0")
            {
                hasValidValue = true;
                break;
            }
        }

        return hasValidValue ? apiDict : null;
    }
}

internal sealed class DownloadPathState
{
    public string DisplayFilePath { get; set; } = string.Empty;
    public string DisplayExtrasPath { get; set; } = string.Empty;
    public string IoFilePath { get; set; } = string.Empty;
    public string IoExtrasPath { get; set; } = string.Empty;
    public string Extension { get; set; } = ".mp3";
    public string WritePathIo { get; set; } = string.Empty;
    public string WritePathDisplay { get; set; } = string.Empty;
}

/// <summary>
/// Extra data for download processing
/// </summary>
public class DownloadExtraData
{
    public Dictionary<string, object> TrackAPI { get; set; } = new();
    public Dictionary<string, object>? AlbumAPI { get; set; }
    public Dictionary<string, object>? PlaylistAPI { get; set; }
}

/// <summary>
/// Download result
/// </summary>
public class DownloadResult
{
    public bool Success { get; set; }
    public string? Filename { get; set; }
    public string? Path { get; set; }
    public string? AlbumPath { get; set; }
    public string? ArtistPath { get; set; }
    public string? AlbumFilename { get; set; }
    public string? ArtistFilename { get; set; }
    public List<ImageUrl>? AlbumUrls { get; set; } = new();
    public List<ImageUrl>? ArtistUrls { get; set; } = new();
    public bool Searched { get; set; }
    public bool TaggingSucceeded { get; set; }
    public object? Data { get; set; }
    public DownloadError? Error { get; set; }
}

/// <summary>
/// Download error
/// </summary>
public class DownloadError
{
    public string Message { get; set; } = "";
    public string? ErrorId { get; set; }
    public object? Data { get; set; }
    public string Type { get; set; } = "";
    public string? Stack { get; set; }
}

/// <summary>
/// Playlist URL for artwork
/// </summary>
public class PlaylistUrl
{
    public string Url { get; set; } = "";
    public string Extension { get; set; } = "";
}

/// <summary>
/// Adapter to convert DeezSpoTagDownloadObject to DownloadObject for existing services
/// </summary>
internal class DownloadObjectAdapter : DownloadObject
{
    private readonly DeezSpoTagDownloadObject _deezspotagObject;
    private readonly IDeezSpoTagListener? _listener;

    public DownloadObjectAdapter(DeezSpoTagDownloadObject deezspotagObject, IDeezSpoTagListener? listener)
    {
        _deezspotagObject = deezspotagObject;
        _listener = listener;

        Uuid = deezspotagObject.UUID;
        Title = deezspotagObject.Title ?? "";
        Type = deezspotagObject.Type;
        Size = deezspotagObject.Size;
        Downloaded = deezspotagObject.Downloaded;
        Failed = deezspotagObject.Failed;
        Progress = deezspotagObject.Progress;
        ProgressNext = deezspotagObject.ProgressNext;
        IsCanceled = deezspotagObject.IsCanceled;
        ExtrasPath = deezspotagObject.ExtrasPath ?? "";
        Bitrate = deezspotagObject.Bitrate;
    }

    public override void UpdateProgress(IDownloadListener? listener = null)
    {
        _deezspotagObject.ProgressNext = ProgressNext;
        _deezspotagObject.Progress = Progress;
        _deezspotagObject.UpdateProgress(_listener);
        Progress = _deezspotagObject.Progress;
    }

    public override void CompleteTrackProgress(IDownloadListener? listener = null)
    {
        _deezspotagObject.CompleteTrackProgress(_listener);
        ProgressNext = _deezspotagObject.ProgressNext;
        Progress = _deezspotagObject.Progress;
    }
}

/// <summary>
/// Adapter to convert IDeezSpoTagListener to IDownloadListener for existing services
/// </summary>
internal class DownloadListenerAdapter : IDownloadListener
{
    private readonly IDeezSpoTagListener? _deezspotagListener;

    public DownloadListenerAdapter(IDeezSpoTagListener? deezspotagListener)
    {
        _deezspotagListener = deezspotagListener;
    }

    public void OnProgressUpdate(DownloadObject downloadObject)
    {
        Send(
            "downloadProgress",
            downloadObject,
            ("progress", downloadObject.Progress),
            ("progressNext", downloadObject.ProgressNext));
    }

    public void OnDownloadStart(DownloadObject downloadObject)
    {
        Send("downloadStart", downloadObject);
    }

    public void OnDownloadInfo(DownloadObject downloadObject, string message, string state)
    {
        Send("downloadInfo", downloadObject, ("data", message), ("state", state));
    }

    public void OnDownloadComplete(DownloadObject downloadObject)
    {
        Send("downloadComplete", downloadObject);
    }

    public void OnDownloadError(DownloadObject downloadObject, DeezSpoTag.Core.Models.Download.DownloadError error)
    {
        Send(
            "downloadError",
            downloadObject,
            ("error", error.Message),
            ("errorId", error.ErrorId),
            ("stack", error.Stack),
            ("type", error.Type),
            ("data", error.Data));
    }

    public void OnDownloadWarning(DownloadObject downloadObject, string message, string state, string solution)
    {
        Send("downloadWarn", downloadObject, ("data", message), ("state", state), ("solution", solution));
    }

    public void OnCurrentItemCancelled(DownloadObject downloadObject)
    {
        Send("currentItemCancelled", downloadObject);
    }

    public void OnRemovedFromQueue(DownloadObject downloadObject)
    {
        Send("removedFromQueue", downloadObject);
    }

    public void OnFinishDownload(DownloadObject downloadObject)
    {
        // FinishDownload is emitted by DeezSpoTagApp after status updates to avoid duplicates.
    }

    public void OnUpdateQueue(DownloadObject downloadObject, bool downloaded = false, bool failed = false, bool alreadyDownloaded = false)
    {
        Send(
            "updateQueue",
            downloadObject,
            ("downloaded", downloaded),
            ("failed", failed),
            ("alreadyDownloaded", alreadyDownloaded),
            ("extrasPath", downloadObject.ExtrasPath));
    }

    private void Send(string eventName, DownloadObject downloadObject, params (string Key, object? Value)[] fields)
    {
        if (_deezspotagListener == null)
        {
            return;
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["uuid"] = downloadObject.Uuid,
            ["title"] = downloadObject.Title
        };

        foreach (var (key, value) in fields)
        {
            payload[key] = value;
        }

        _deezspotagListener.Send(eventName, payload);
    }
}
