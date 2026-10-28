using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeezSpoTag.Services.Download.Shared.Utils;

public static class DownloadStreamHelper
{
    private sealed record TransferCheckpoint(string Fingerprint, string Validator, long Total);

    /// <summary>Keep interrupted bytes only when the server can validate their representation.</summary>
    public static async Task DownloadFileAsync(
        HttpClient client, string url, string outputPath,
        Func<double, double, Task>? progressCallback, CancellationToken cancellationToken,
        Func<HttpRequestMessage>? requestFactory = null)
    {
        var partial = outputPath + ".partial";
        var checkpointPath = partial + ".checkpoint.json";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransferCheckpoint? checkpoint = null;
            if (File.Exists(checkpointPath))
            {
                try { checkpoint = JsonSerializer.Deserialize<TransferCheckpoint>(await File.ReadAllTextAsync(checkpointPath, cancellationToken)); }
                catch (JsonException)
                {
                    // A corrupt checkpoint must not fail the download; remove it so the next
                    // attempt starts clean instead of re-reading the same invalid file.
                    File.Delete(checkpointPath);
                }
            }
            var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (checkpoint?.Fingerprint != fingerprint || string.IsNullOrWhiteSpace(checkpoint.Validator)
                || offset <= 0 || checkpoint.Total <= offset)
            {
                offset = 0;
                File.Delete(partial);
                File.Delete(checkpointPath);
                checkpoint = null;
            }
            var retryDelay = TimeSpan.FromMilliseconds(500 * (attempt + 1));
            try
            {
                using var request = requestFactory?.Invoke() ?? new HttpRequestMessage(HttpMethod.Get, url);
                if (offset > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(offset, null);
                    request.Headers.TryAddWithoutValidation("If-Range", checkpoint!.Validator);
                }
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (offset > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    File.Delete(partial);
                    File.Delete(checkpointPath);
                    if (attempt < 3) continue;
                }
                var advertisedDelay = ZarzSignedSessionContract.ReadRetryAfterSeconds(response, null);
                if (advertisedDelay > 0) retryDelay = TimeSpan.FromSeconds(advertisedDelay.Value);
                response.EnsureSuccessStatusCode();
                var validator = response.Headers.ETag is { IsWeak: false } etag
                    ? etag.ToString() : response.Content.Headers.LastModified?.ToString("R");
                var range = response.Content.Headers.ContentRange;
                if (offset > 0 && response.StatusCode == HttpStatusCode.OK) offset = 0;
                if (response.StatusCode == HttpStatusCode.PartialContent
                    && (range?.From != offset || !range.Length.HasValue
                        || (offset > 0 && (validator != checkpoint!.Validator || range.Length != checkpoint.Total))))
                {
                    File.Delete(partial);
                    File.Delete(checkpointPath);
                    if (attempt < 3) continue;
                    throw new InvalidDataException("Audio server returned an inconsistent resumed range.");
                }
                var total = response.StatusCode == HttpStatusCode.PartialContent
                    ? range!.Length : response.Content.Headers.ContentLength;
                if (total == 0) throw new InvalidDataException("Audio server returned an empty file.");
                if (!string.IsNullOrWhiteSpace(validator) && total > 0)
                    await File.WriteAllTextAsync(checkpointPath,
                        JsonSerializer.Serialize(new TransferCheckpoint(fingerprint, validator, total.Value)), cancellationToken);
                else File.Delete(checkpointPath);
                await using (var file = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await CopyToAsyncWithProgress(input, file, total, progressCallback, cancellationToken, offset);
                    await file.FlushAsync(cancellationToken);
                    if (file.Length <= 0 || (total.HasValue && file.Length != total.Value))
                        throw new IOException("Audio transfer ended before the declared file size was received.");
                }
                File.Move(partial, outputPath, overwrite: true);
                File.Delete(checkpointPath);
                return;
            }
            catch (Exception ex) when (attempt < 3 && !cancellationToken.IsCancellationRequested
                && (ex is IOException or InvalidDataException or TaskCanceledException
                    || ex is HttpRequestException http && (http.StatusCode is null
                        || http.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)http.StatusCode >= 500)))
            {
                await Task.Delay(retryDelay, cancellationToken);
            }
        }
    }

    public static async Task CopyToAsyncWithProgress(
        Stream input,
        Stream output,
        long? totalBytes,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken,
        long initialBytes = 0)
    {
        var buffer = new byte[81920];
        long readTotal = initialBytes;
        var progressState = new ProgressState();

        if (progressCallback != null)
        {
            await progressCallback(0, 0);
        }

        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            readTotal += read;
            UpdateSpeed(progressState, read);
            await ReportProgressIfNeeded(progressCallback, totalBytes, readTotal, progressState);
        }

        if (progressCallback != null)
        {
            await progressCallback(95, 0);
        }
    }

    private sealed class ProgressState
    {
        public int LastPercent { get; set; } = -1;
        public DateTimeOffset LastReportAt { get; set; } = DateTimeOffset.UtcNow;
        public long SpeedWindowBytes { get; set; }
        public DateTimeOffset SpeedWindowStart { get; set; } = DateTimeOffset.UtcNow;
        public double CurrentSpeedMbps { get; set; }
    }

    private static void UpdateSpeed(ProgressState state, int read)
    {
        state.SpeedWindowBytes += read;
        var now = DateTimeOffset.UtcNow;
        var speedWindowSeconds = (now - state.SpeedWindowStart).TotalSeconds;
        if (speedWindowSeconds < 1)
        {
            return;
        }

        var bytesPerSecond = state.SpeedWindowBytes / speedWindowSeconds;
        state.CurrentSpeedMbps = (bytesPerSecond * 8) / 1024 / 1024;
        state.SpeedWindowBytes = 0;
        state.SpeedWindowStart = now;
    }

    private static async Task ReportProgressIfNeeded(
        Func<double, double, Task>? progressCallback,
        long? totalBytes,
        long readTotal,
        ProgressState state)
    {
        if (progressCallback == null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (totalBytes.HasValue && totalBytes.Value > 0)
        {
            var percent = Math.Min(95, (int)Math.Floor(readTotal * 100d / totalBytes.Value));
            if (percent != state.LastPercent || (now - state.LastReportAt).TotalSeconds >= 1)
            {
                state.LastPercent = percent;
                state.LastReportAt = now;
                await progressCallback(percent, state.CurrentSpeedMbps);
            }

            return;
        }

        if ((now - state.LastReportAt).TotalSeconds >= 1)
        {
            state.LastReportAt = now;
            await progressCallback(0, state.CurrentSpeedMbps);
        }
    }
}
