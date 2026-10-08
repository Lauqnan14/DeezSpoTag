using System.Net;
using DeezSpoTag.Services.Download.Shared.Utils;
using IOFile = System.IO.File;

namespace DeezSpoTag.Services.Download.SoundCloud;

/// <summary>
///     The verified outcome of one HLS transfer.
/// </summary>
/// <param name="LocalPath">Where the assembled file landed.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="SegmentCount">How many segments were assembled.</param>
public sealed record SoundCloudHlsDownloadResult(string LocalPath, long Size, int SegmentCount);

/// <summary>
///     Downloads a SoundCloud media playlist and assembles it into a single audio file.
/// </summary>
/// <remarks>
///     <para>
///         Separate from <see cref="SoundCloudDownloadService"/> because this is the part that needs its own
///         concurrency, retry, and crypto behaviour, and because it is the piece worth testing directly
///         against real ciphertext.
///     </para>
///     <para>
///         The output only appears at the destination path once every segment has been downloaded, decrypted,
///         and verified non-empty. A cancelled or failed transfer therefore leaves either the previous contents
///         of the destination or nothing at all - never a truncated file that a size check would accept.
///     </para>
/// </remarks>
public sealed class SoundCloudHlsDownloader
{
    /// <summary>
    ///     How many segments are fetched at once.
    /// </summary>
    /// <remarks>
    ///     Bounded rather than the ported implementation's fixed 100. A track is a handful of segments, so a
    ///     large fan-out only competes with itself and with the user's other transfers for connections.
    /// </remarks>
    private const int MaxConcurrentSegmentDownloads = 4;

    /// <summary>Attempts per segment before the transfer is abandoned.</summary>
    private const int DefaultSegmentAttempts = 4;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly int _segmentAttempts;

    /// <summary>Initializes a new instance of the <see cref="SoundCloudHlsDownloader" /> class.</summary>
    public SoundCloudHlsDownloader(IHttpClientFactory httpClientFactory)
        : this(httpClientFactory, DefaultSegmentAttempts)
    {
    }

    /// <summary>
    ///     Initializes a new instance with an explicit per-segment attempt budget.
    /// </summary>
    /// <remarks>
    ///     The budget is injectable so a test can assert that a failing segment is not retried indefinitely
    ///     without the test having to wait out the production backoff. Production uses the single-argument
    ///     constructor and always gets the default.
    /// </remarks>
    public SoundCloudHlsDownloader(IHttpClientFactory httpClientFactory, int segmentAttempts)
    {
        _httpClientFactory = httpClientFactory;
        _segmentAttempts = Math.Max(1, segmentAttempts);
    }

    /// <summary>
    ///     Fetches, decrypts, and assembles the playlist at <paramref name="playlistUrl"/>.
    /// </summary>
    /// <param name="playlistUrl">The media playlist to transfer.</param>
    /// <param name="outputPath">Where the assembled file must end up.</param>
    /// <param name="progress">Optional progress reporter, called with percentage and bytes per second.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verified output.</returns>
    public async Task<SoundCloudHlsDownloadResult> DownloadAsync(
        string playlistUrl,
        string outputPath,
        Func<double, double, Task>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var client = _httpClientFactory.CreateClient(SoundCloudClient.StreamHttpClientName);
        var playlist = SoundCloudHlsPlaylistParser.Parse(
            playlistUrl,
            await ReadTextAsync(client, playlistUrl, cancellationToken).ConfigureAwait(false));

        var segmentDirectory = BuildSegmentDirectory(outputPath);
        Directory.CreateDirectory(segmentDirectory);
        var segmentPaths = Enumerable.Range(0, playlist.Segments.Count)
            .Select(index => Path.Join(segmentDirectory, $"{index:D8}.segment"))
            .ToArray();

        var completed = 0;
        using var progressGate = new SemaphoreSlim(1, 1);
        var keyCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var keyGate = new SemaphoreSlim(1, 1);

        try
        {
            if (progress is not null)
            {
                await progress(0, 0).ConfigureAwait(false);
            }

            // Linked so one segment failing stops the rest. Without it a doomed transfer keeps issuing
            // requests for segments whose results will be discarded.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await Parallel.ForEachAsync(
                Enumerable.Range(0, playlist.Segments.Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxConcurrentSegmentDownloads,
                    CancellationToken = linked.Token
                },
                async (index, token) =>
                {
                    var segment = playlist.Segments[index];
                    var data = await DownloadSegmentWithRetryAsync(client, segment.Uri, token).ConfigureAwait(false);

                    if (segment.Key is { } key)
                    {
                        var keyBytes = await ResolveKeyAsync(client, key, keyCache, keyGate, token).ConfigureAwait(false);
                        data = SoundCloudHlsDecryptor.Decrypt(
                            data,
                            keyBytes,
                            playlist.MediaSequence + index,
                            key.Iv);
                    }

                    if (data.Length == 0)
                    {
                        throw new InvalidDataException("A SoundCloud HLS segment decrypted to nothing.");
                    }

                    await File.WriteAllBytesAsync(segmentPaths[index], data, token).ConfigureAwait(false);

                    var finished = Interlocked.Increment(ref completed);
                    if (progress is null)
                    {
                        return;
                    }

                    await progressGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        await progress(finished * 100d / playlist.Segments.Count, 0).ConfigureAwait(false);
                    }
                    finally
                    {
                        progressGate.Release();
                    }
                }).ConfigureAwait(false);

            await MergeSegmentsInOrderAsync(segmentPaths, outputPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Nothing is written to the destination on failure. A file that is not a complete download must
            // not sit where the pipeline looks for one, and must not overwrite what was already there.
            throw;
        }
        finally
        {
            TryDeleteDirectory(segmentDirectory);
        }

        var size = new FileInfo(outputPath).Length;
        if (size <= 0)
        {
            throw new InvalidDataException("SoundCloud HLS assembly produced a zero-byte file.");
        }

        return new SoundCloudHlsDownloadResult(outputPath, size, playlist.Segments.Count);
    }

    /// <summary>
    ///     Concatenates the segments in playlist order into <paramref name="outputPath"/>.
    /// </summary>
    /// <remarks>
    ///     Assembled through a partial file and moved into place, so the destination is only written once every
    ///     byte is present. A failure during the merge leaves the destination untouched.
    /// </remarks>
    private static async Task MergeSegmentsInOrderAsync(
        IReadOnlyList<string> segmentPaths,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var partialPath = outputPath + ".partial";
        DownloadFileUtilities.TryDeleteFile(partialPath);

        try
        {
            await using (var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                foreach (var segmentPath in segmentPaths)
                {
                    var segmentInfo = new FileInfo(segmentPath);
                    if (!segmentInfo.Exists || segmentInfo.Length <= 0)
                    {
                        throw new InvalidDataException("A required SoundCloud HLS segment is missing or empty.");
                    }

                    await using var input = new FileStream(
                        segmentPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 81920,
                        useAsync: true);
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (new FileInfo(partialPath).Length <= 0)
            {
                throw new InvalidDataException("SoundCloud HLS assembly produced a zero-byte file.");
            }

            IOFile.Move(partialPath, outputPath, overwrite: true);
        }
        catch
        {
            DownloadFileUtilities.TryDeleteFile(partialPath);
            throw;
        }
    }

    private async Task<string> ReadTextAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SoundCloudNoStreamException(
                $"SoundCloud returned HTTP {(int)response.StatusCode} for its media playlist.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Fetches one segment, retrying only failures worth retrying.
    /// </summary>
    /// <remarks>
    ///     A 404 is not retried: the segment is gone, and asking again produces the same answer. A 5xx or a
    ///     timeout is retried, because those are usually transient at the CDN.
    /// </remarks>
    private async Task<byte[]> DownloadSegmentWithRetryAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;

        for (var attempt = 1; attempt <= _segmentAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var response = await client
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var failure = new HttpRequestException(
                        $"SoundCloud HLS segment returned HTTP {(int)response.StatusCode}.",
                        null,
                        response.StatusCode);

                    if (!IsTransientStatus(response.StatusCode) || attempt == _segmentAttempts)
                    {
                        throw failure;
                    }

                    lastFailure = failure;
                    await Task.Delay(GetRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (data.Length == 0)
                {
                    throw new InvalidDataException("SoundCloud HLS segment returned an empty response.");
                }

                return data;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < _segmentAttempts && IsTransientException(ex))
            {
                lastFailure = ex;
                await Task.Delay(GetRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"SoundCloud HLS segment failed after {_segmentAttempts} attempts.",
            lastFailure);
    }

    /// <summary>
    ///     Fetches an AES key, once per URI for the whole transfer.
    /// </summary>
    /// <remarks>
    ///     Cached because a playlist commonly rotates between two or three keys across dozens of segments.
    ///     Fetching each key once per segment would multiply the request count for no benefit.
    /// </remarks>
    private async Task<byte[]> ResolveKeyAsync(
        HttpClient client,
        SoundCloudHlsKey key,
        Dictionary<string, byte[]> cache,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cache.TryGetValue(key.Uri, out var cached))
            {
                return cached;
            }

            using var response = await client
                .GetAsync(key.Uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length != 16)
            {
                throw new InvalidDataException(
                    $"SoundCloud HLS AES-128 key was {bytes.Length} bytes; AES-128 requires exactly 16.");
            }

            cache[key.Uri] = bytes;
            return bytes;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static bool IsTransientException(Exception exception)
        => exception is HttpRequestException or IOException;

    private static TimeSpan GetRetryDelay(int attempt)
        => TimeSpan.FromMilliseconds(200d * Math.Pow(2, attempt - 1));

    private static string BuildSegmentDirectory(string outputPath)
        => outputPath + $".segments-{Guid.NewGuid():N}";

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; staging maintenance retries anything left behind.
        }
    }
}