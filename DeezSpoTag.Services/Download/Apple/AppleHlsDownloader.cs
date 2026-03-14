using System.Net.Http;
using DeezSpoTag.Services.Apple;

namespace DeezSpoTag.Services.Download.Apple;

public sealed class AppleHlsDownloader
{
    private readonly IHttpClientFactory _httpClientFactory;
    private const int ParallelSegments = 10;

    public AppleHlsDownloader(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<AppleHlsDownloadResult> DownloadAsync(string mediaPlaylistUrl, string outputPath, Func<double, double, Task>? progressCallback, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mediaPlaylistUrl))
        {
            return AppleHlsDownloadResult.Fail("Media playlist URL missing.");
        }

        var client = _httpClientFactory.CreateClient();
        var playlistText = await GetStringWithHeadersAsync(client, mediaPlaylistUrl, cancellationToken);
        var playlist = AppleHlsManifestParser.ParseMedia(playlistText, new Uri(mediaPlaylistUrl));

        // Always download init + all media segments explicitly.
        // Some Apple BYTERANGE playlists resolve to chunk URLs where downloading the base URI once
        // can produce only a short fragment (~15s) instead of the full track.

        if (string.IsNullOrWhiteSpace(playlist.InitSegment) || playlist.Segments.Count == 0)
        {
            return AppleHlsDownloadResult.Fail("Media playlist missing init segment or segments.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? string.Empty);
        var result = await DownloadSegmentsParallelAsync(client, playlist, outputPath, progressCallback, cancellationToken);
        return result.Success ? AppleHlsDownloadResult.Ok(outputPath, playlist.KeyUri) : result;
    }

    private static async Task CopyUrlAsync(
        HttpClient client,
        string url,
        Stream output,
        CancellationToken cancellationToken,
        AppleHlsByteRange? byteRange = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", AppleUserAgentPool.GetRandomUserAgent());
        if (byteRange?.Length != null)
        {
            var offset = byteRange.Offset ?? 0;
            var end = offset + byteRange.Length - 1;
            request.Headers.TryAddWithoutValidation("Range", $"bytes={offset}-{end}");
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await stream.CopyToAsync(output, cancellationToken);
    }

    private static Task ReportProgressAsync(Func<double, double, Task>? callback, int completed, int total)
    {
        if (callback == null)
        {
            return Task.CompletedTask;
        }

        var progress = total == 0 ? 0 : (completed / (double)total) * 100;
        return callback(progress, EncodeSegmentProgress(completed, total));
    }

    private static async Task<AppleHlsDownloadResult> DownloadSegmentsParallelAsync(
        HttpClient client,
        AppleHlsMediaPlaylist playlist,
        string outputPath,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        var totalParts = 1 + playlist.Segments.Count;
        var completed = 0;
        var tempDir = Path.Join(Path.GetTempPath(), $"apple-hls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var tempInit = Path.Join(tempDir, "init.mp4");
        var tempSegments = new string[playlist.Segments.Count];

        try
        {
            await DownloadToFileAsync(client, playlist.InitSegment, tempInit, playlist.InitRange, cancellationToken);
            EnsureFileNotEmpty(tempInit, "init segment");
            completed++;
            await ReportProgressAsync(progressCallback, completed, totalParts);

            using var semaphore = new SemaphoreSlim(ParallelSegments, ParallelSegments);
            var tasks = playlist.Segments.Select(async (segment, index) =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var tempPath = Path.Join(tempDir, $"seg-{index:D6}.m4s");
                    tempSegments[index] = tempPath;
                    await DownloadToFileAsync(client, segment.Uri, tempPath, segment.Range, cancellationToken);
                    EnsureFileNotEmpty(tempPath, "media segment");
                    var nowCompleted = Interlocked.Increment(ref completed);
                    await ReportProgressAsync(progressCallback, nowCompleted, totalParts);
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);

            var tempOutput = Path.Join(tempDir, "output.mp4");
            await using var output = File.Create(tempOutput);
            await using (var initStream = File.OpenRead(tempInit))
            {
                await initStream.CopyToAsync(output, cancellationToken);
            }

            foreach (var segmentPath in tempSegments)
            {
                if (string.IsNullOrWhiteSpace(segmentPath) || !File.Exists(segmentPath))
                {
                    return AppleHlsDownloadResult.Fail("Apple HLS download failed: missing segment.");
                }
                EnsureFileNotEmpty(segmentPath, "media segment");
                await using var segmentStream = File.OpenRead(segmentPath);
                await segmentStream.CopyToAsync(output, cancellationToken);
            }

            output.Close();
            File.Move(tempOutput, outputPath, overwrite: true);
            return AppleHlsDownloadResult.Ok(outputPath, playlist.KeyUri);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AppleHlsDownloadResult.Fail($"Apple HLS download failed: {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static async Task DownloadToFileAsync(
        HttpClient client,
        string url,
        string path,
        AppleHlsByteRange? byteRange,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
        const int maxAttempts = 3;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                await using var output = File.Create(path);
                await CopyUrlAsync(client, url, output, cancellationToken, byteRange);
                return;
            }
            catch (HttpRequestException) when (attempt < maxAttempts - 1)
            {
                TryDeleteFile(path);
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (attempt < maxAttempts - 1)
            {
                TryDeleteFile(path);
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static void EnsureFileNotEmpty(string path, string label)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            throw new InvalidOperationException($"Apple HLS download failed: {label} empty.");
        }
    }

    private static double EncodeSegmentProgress(int completed, int total)
    {
        if (total <= 0)
        {
            return 0;
        }

        return (total * 100000d) + completed;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup.
        }
        catch (ArgumentException)
        {
            // Best effort cleanup.
        }
        catch (NotSupportedException)
        {
            // Best effort cleanup.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup.
        }
        catch (ArgumentException)
        {
            // Best effort cleanup.
        }
        catch (NotSupportedException)
        {
            // Best effort cleanup.
        }
    }

    private static async Task<string> GetStringWithHeadersAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", AppleUserAgentPool.GetRandomUserAgent());
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < maxAttempts - 1)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            if ((int)response.StatusCode >= 500 && attempt < maxAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                continue;
            }

            response.EnsureSuccessStatusCode();
        }

        throw new InvalidOperationException("Apple HLS playlist fetch failed after retries.");
    }

}

public sealed record AppleHlsDownloadResult(bool Success, string Message, string OutputPath, string KeyUri)
{
    public static AppleHlsDownloadResult Ok(string outputPath, string keyUri) => new(true, string.Empty, outputPath, keyUri);
    public static AppleHlsDownloadResult Fail(string message) => new(false, message, string.Empty, string.Empty);
}
