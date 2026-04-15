using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Download;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Enums;
using DeezSpoTag.Services.Crypto;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Integrations.Deezer;
using Microsoft.Extensions.Logging;
using System.Text;

namespace DeezSpoTag.Services.Downloader;

/// <summary>
/// Deezer download service (ported from deezspotag downloader.ts)
/// </summary>
public class DeezerDownloadService
{
    private readonly ILogger<DeezerDownloadService> _logger;
    private readonly HttpClient _httpClient;
    private readonly CryptoService _cryptoService;
    private readonly AudioTagger _audioTagger;
    private readonly EnhancedPathTemplateProcessor _pathProcessor;
    private readonly Download.AuthenticatedDeezerService _authenticatedDeezerService;

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

    private static readonly Dictionary<int, (string Name, int Code)> MediaFormats = new()
    {
        { 9, ("FLAC", 9) },
        { 3, ("MP3_320", 3) },
        { 1, ("MP3_128", 1) }
    };

    public DeezerDownloadService(
        ILogger<DeezerDownloadService> logger,
        HttpClient httpClient,
        CryptoService cryptoService,
        AudioTagger audioTagger,
        EnhancedPathTemplateProcessor pathProcessor,
        Download.AuthenticatedDeezerService authenticatedDeezerService)
    {
        _logger = logger;
        _httpClient = httpClient;
        _cryptoService = cryptoService;
        _audioTagger = audioTagger;
        _pathProcessor = pathProcessor;
        _authenticatedDeezerService = authenticatedDeezerService;

        // Set user agent for Deezer requests
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/79.0.3945.130 Safari/537.36"
        );
    }

    /// <summary>
    /// Download a single track (port of download method from deezspotag)
    /// </summary>
    public async Task<DownloadResult> DownloadTrackAsync(
        DeezSpoTag.Core.Models.Track track,
        DeezSpoTagSettings settings,
        IDownloadListener? listener = null,
        CancellationToken cancellationToken = default)
    {
        var result = new DownloadResult
        {
            Success = false,
            Track = track
        };

        try
        {
            // Check if download is cancelled
            cancellationToken.ThrowIfCancellationRequested();

            // Validate track
            if (string.IsNullOrEmpty(track.Id) || track.Id == "0")
            {
                throw new InvalidOperationException("Track not available on Deezer");
            }

            if (string.IsNullOrEmpty(track.MD5) || string.IsNullOrEmpty(track.MediaVersion))
            {
                await TryEnrichTrackAsync(track);
            }

            // Check if track is encoded
            if (string.IsNullOrEmpty(track.MD5))
            {
                throw new InvalidOperationException("Track is not encoded");
            }

            // Generate file paths
            var paths = _pathProcessor.GeneratePaths(track, "track", settings);
            var extension = Extensions.GetValueOrDefault(track.Bitrate, ".mp3");
            var writePath = Path.Join(paths.FilePath, $"{paths.Filename}{extension}");

            // Create directory if it doesn't exist
            Directory.CreateDirectory(paths.FilePath);

            // Check if file should be downloaded
            if (!ShouldDownloadFile(writePath, settings))
            {
                result.Success = true;
                result.FilePath = writePath;
                result.AlreadyExists = true;
                return result;
            }

            // Generate download URL (refreezer: media.deezer.com/v1/get_url, fallback to crypted URL)
            var downloadUrl = await ResolveDownloadUrlAsync(track);
            if (string.IsNullOrEmpty(downloadUrl))
            {
                throw new InvalidOperationException("Could not generate download URL");
            }

            // Download and decrypt the track
            await DownloadAndDecryptTrackAsync(downloadUrl, writePath, track, listener, cancellationToken);

            // Tag the file if it's not a local file (exact port from deezspotag downloader.ts)
            if (!track.IsLocal)
            {
                try
                {
                    await _audioTagger.TagTrackAsync(extension, writePath, track, settings.Tags);
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("Successfully tagged track: {TrackId}", track.Id);                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to tag track: {TrackId}", track.Id);
                    // Don't throw - tagging failure shouldn't fail the download
                }
            }

            result.Success = true;
            result.FilePath = writePath;

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Successfully downloaded track: {Title} by {Artist}", track.Title, track.MainArtist?.Name);            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error downloading track: {TrackId}", track.Id);
            result.Error = ex.Message;
            return result;
        }
    }

    private async Task TryEnrichTrackAsync(DeezSpoTag.Core.Models.Track track)
    {
        try
        {
            var deezerClient = await _authenticatedDeezerService.GetAuthenticatedClientAsync();
            if (deezerClient == null)
            {
                _logger.LogWarning("Deezer client not authenticated for track enrichment");
                return;
            }

            var gwTrack = await deezerClient.Gw.GetTrackWithFallbackAsync(track.Id);
            if (gwTrack == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(track.MD5))
            {
                track.MD5 = gwTrack.Md5Origin;
            }

            if (string.IsNullOrEmpty(track.MediaVersion))
            {
                track.MediaVersion = gwTrack.MediaVersion.ToString();
            }

            if (string.IsNullOrEmpty(track.TrackToken))
            {
                track.TrackToken = gwTrack.TrackToken;
                track.TrackTokenExpire = gwTrack.TrackTokenExpire;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to enrich track metadata for {TrackId}", track.Id);            }
        }
    }

    /// <summary>
    /// Download and decrypt track stream (port of streamTrack function)
    /// </summary>
    private async Task DownloadAndDecryptTrackAsync(
        string downloadUrl,
        string writePath,
        DeezSpoTag.Core.Models.Track track,
        IDownloadListener? listener,
        CancellationToken cancellationToken)
    {
        var streamContext = CreateStreamContext(downloadUrl, track);
        using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var contentLength = EnsureContentLength(response);
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(writePath, FileMode.Create, FileAccess.Write);

        var buffer = new byte[8192];
        var state = new StreamState(0L, Array.Empty<byte>(), true);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkData = await ReadChunkAsync(responseStream, buffer, cancellationToken);
            if (chunkData.Length == 0)
            {
                break;
            }

            state = state with { TotalBytesRead = state.TotalBytesRead + chunkData.Length };
            var processedData = ProcessChunk(chunkData, streamContext, state, out var updatedState);
            state = updatedState;
            await WriteChunkAsync(fileStream, processedData, cancellationToken);
            ReportDownloadProgress(listener, track, state.TotalBytesRead, contentLength);
        }

        await FlushRemainingBufferAsync(fileStream, streamContext, state, cancellationToken);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Downloaded {TotalBytes} bytes for track {TrackId}", state.TotalBytesRead, track.Id);        }
    }

    private static StreamContext CreateStreamContext(string downloadUrl, DeezSpoTag.Core.Models.Track track)
    {
        var isCryptedStream = downloadUrl.Contains("/mobile/", StringComparison.Ordinal)
            || downloadUrl.Contains("/media/", StringComparison.Ordinal);
        var streamTrackId = ResolveStreamTrackId(track);
        var blowfishKey = isCryptedStream ? DecryptionService.GenerateBlowfishKey(streamTrackId) : string.Empty;
        return new StreamContext(isCryptedStream, blowfishKey);
    }

    private static long EnsureContentLength(HttpResponseMessage response)
    {
        var contentLength = response.Content.Headers.ContentLength ?? 0;
        if (contentLength == 0)
        {
            throw new InvalidOperationException("Empty download response");
        }

        return contentLength;
    }

    private static async Task<byte[]> ReadChunkAsync(Stream responseStream, byte[] buffer, CancellationToken cancellationToken)
    {
        var bytesRead = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        if (bytesRead == 0)
        {
            return Array.Empty<byte>();
        }

        return buffer[..bytesRead];
    }

    private byte[] ProcessChunk(byte[] chunkData, StreamContext context, StreamState state, out StreamState updatedState)
    {
        var processedData = context.IsCryptedStream
            ? ProcessEncryptedChunk(chunkData, context, state, out updatedState)
            : ProcessPlainChunk(chunkData, state, out updatedState);
        return StripLeadingPadding(processedData, updatedState, out updatedState);
    }

    private byte[] ProcessEncryptedChunk(byte[] chunkData, StreamContext context, StreamState state, out StreamState updatedState)
    {
        var combinedBuffer = CombineBuffers(state.DecryptionBuffer, chunkData);
        var processedLength = (combinedBuffer.Length / 2048) * 2048;
        if (processedLength <= 0)
        {
            updatedState = state with { DecryptionBuffer = combinedBuffer };
            return Array.Empty<byte>();
        }

        var toProcess = combinedBuffer[..processedLength];
        var remaining = combinedBuffer[processedLength..];
        updatedState = state with { DecryptionBuffer = remaining };
        return _cryptoService.DecryptChunks(toProcess, context.BlowfishKey);
    }

    private static byte[] ProcessPlainChunk(byte[] chunkData, StreamState state, out StreamState updatedState)
    {
        updatedState = state;
        return chunkData;
    }

    private static byte[] CombineBuffers(byte[] existingBuffer, byte[] chunkData)
    {
        if (existingBuffer.Length == 0)
        {
            return chunkData.ToArray();
        }

        var combined = new byte[existingBuffer.Length + chunkData.Length];
        Buffer.BlockCopy(existingBuffer, 0, combined, 0, existingBuffer.Length);
        Buffer.BlockCopy(chunkData, 0, combined, existingBuffer.Length, chunkData.Length);
        return combined;
    }

    private static byte[] StripLeadingPadding(byte[] processedData, StreamState state, out StreamState updatedState)
    {
        updatedState = state;
        if (!state.IsFirstChunk || processedData.Length == 0)
        {
            return processedData;
        }

        updatedState = state with { IsFirstChunk = false };
        return DeezerAudioPadding.RemoveLeadingNullPadding(processedData);
    }

    private static async Task WriteChunkAsync(Stream fileStream, byte[] data, CancellationToken cancellationToken)
    {
        if (data.Length == 0)
        {
            return;
        }

        await fileStream.WriteAsync(data.AsMemory(0, data.Length), cancellationToken);
    }

    private static void ReportDownloadProgress(
        IDownloadListener? listener,
        DeezSpoTag.Core.Models.Track track,
        long totalBytesRead,
        long contentLength)
    {
        if (contentLength <= 0)
        {
            return;
        }

        var progress = (double)totalBytesRead / contentLength * 100;
        listener?.OnDownloadInfo(
            new SingleDownloadObject { Track = track },
            $"Downloaded {totalBytesRead}/{contentLength} bytes ({progress:F1}%)",
            "downloading");
    }

    private async Task FlushRemainingBufferAsync(
        Stream fileStream,
        StreamContext context,
        StreamState state,
        CancellationToken cancellationToken)
    {
        if (!context.IsCryptedStream || state.DecryptionBuffer.Length == 0)
        {
            return;
        }

        var finalData = _cryptoService.DecryptChunks(state.DecryptionBuffer, context.BlowfishKey);
        await WriteChunkAsync(fileStream, finalData, cancellationToken);
    }

    private static string ResolveStreamTrackId(DeezSpoTag.Core.Models.Track track)
    {
        if (track.FallbackID > 0)
        {
            return track.FallbackID.ToString();
        }

        if (track.FallbackId > 0)
        {
            return track.FallbackId.ToString();
        }

        return track.Id;
    }

    /// <summary>
    /// Generate download URL for track (ported from deezspotag URL generation)
    /// </summary>
    private async Task<string?> ResolveDownloadUrlAsync(DeezSpoTag.Core.Models.Track track)
    {
        var (formatName, formatNumber) = ResolveMediaFormat(track.Bitrate);
        var tokenBasedUrl = await TryResolveTrackTokenDownloadUrlAsync(track, formatName);
        if (!string.IsNullOrWhiteSpace(tokenBasedUrl))
        {
            return tokenBasedUrl;
        }

        if (CanBuildCryptedUrl(track))
        {
            return BuildCryptedUrl(track, formatNumber);
        }

        _logger.LogError(
            "Cannot generate download URL for track {TrackId} - missing MD5 ({MD5}) or MediaVersion ({MediaVersion})",
            track.Id,
            track.MD5 ?? "NULL",
            track.MediaVersion ?? "NULL");
        return null;
    }

    private async Task<string?> TryResolveTrackTokenDownloadUrlAsync(DeezSpoTag.Core.Models.Track track, string formatName)
    {
        if (string.IsNullOrWhiteSpace(track.TrackToken))
        {
            return null;
        }

        var deezerClient = await _authenticatedDeezerService.GetAuthenticatedClientAsync();
        if (deezerClient == null)
        {
            return null;
        }

        var mediaResult = await deezerClient.GetTrackUrlWithStatusAsync(track.TrackToken, formatName);
        if (ShouldRefreshTrackToken(mediaResult.Url, mediaResult.ErrorCode) && await RefreshTrackTokenAsync(track, deezerClient))
        {
            mediaResult = await deezerClient.GetTrackUrlWithStatusAsync(track.TrackToken, formatName);
        }

        return string.IsNullOrWhiteSpace(mediaResult.Url) ? null : mediaResult.Url;
    }

    private static bool ShouldRefreshTrackToken(string? mediaUrl, int? errorCode)
        => string.IsNullOrEmpty(mediaUrl) && errorCode == 2001;

    private static bool CanBuildCryptedUrl(DeezSpoTag.Core.Models.Track track)
        => !string.IsNullOrEmpty(track.MD5) && !string.IsNullOrEmpty(track.MediaVersion);

    private static string BuildCryptedUrl(DeezSpoTag.Core.Models.Track track, int formatNumber)
        => CryptoService.GenerateCryptedStreamUrl(
            track.Id,
            track.MD5!,
            track.MediaVersion!,
            formatNumber.ToString());

    private static (string Name, int Code) ResolveMediaFormat(int bitrate)
    {
        return MediaFormats.TryGetValue(bitrate, out var value)
            ? value
            : MediaFormats[1];
    }

    private async Task<bool> RefreshTrackTokenAsync(DeezSpoTag.Core.Models.Track track, DeezerClient deezerClient)
    {
        return await TrackTokenRefreshHelper.RefreshTrackTokenAsync(
            track,
            deezerClient,
            _logger,
            includeFileSizes: false);
    }

    /// <summary>
    /// Check if file should be downloaded based on overwrite settings
    /// </summary>
    private static bool ShouldDownloadFile(string filePath, DeezSpoTagSettings settings)
    {
        if (!System.IO.File.Exists(filePath))
            return true;

        return settings.OverwriteFile switch
        {
            "y" => true,
            "n" => false,
            "b" => true,
            "t" => false,
            "l" => true,
            _ => false
        };
    }

    private sealed record StreamContext(bool IsCryptedStream, string BlowfishKey);
    private sealed record StreamState(long TotalBytesRead, byte[] DecryptionBuffer, bool IsFirstChunk);
}

/// <summary>
/// Download result information
/// </summary>
public class DownloadResult
{
    public bool Success { get; set; }
    public string? FilePath { get; set; }
    public string? Error { get; set; }
    public bool AlreadyExists { get; set; }
    public DeezSpoTag.Core.Models.Track? Track { get; set; }
}
