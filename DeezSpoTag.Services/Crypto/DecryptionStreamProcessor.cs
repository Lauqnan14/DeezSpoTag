using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Download;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Linq;
using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Services.Crypto;

/// <summary>
/// Stream processor for decrypting Deezer audio streams (exact port from deezspotag decryption.ts)
/// </summary>
public class DecryptionStreamProcessor
{
    private const int DownloadTimeoutMs = 5000;
    private const string DownloadCanceledMessage = "DownloadCanceled";
    private const string DownloadEmptyMessage = "DownloadEmpty";
    private const string DownloadTimeoutMessage = "DownloadTimeout";
    private const string UserAgentHeader = "User-Agent";
    private const string LinuxChrome79UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/79.0.3945.130 Safari/537.36";
    private readonly ILogger<DecryptionStreamProcessor> _logger;
    private readonly CryptoService _cryptoService;
    private readonly IHttpClientFactory _httpClientFactory;

    public readonly record struct StreamTrackRetryPolicy(
        int MaxRetries,
        int RetryDelaySeconds,
        int RetryDelayIncrease,
        int Attempt = 0)
    {
        public static StreamTrackRetryPolicy NoRetries { get; } = new(0, 0, 0, 0);

        public int NormalizedMaxRetries => Math.Max(0, MaxRetries);

        public int ResolveDelaySeconds()
            => Math.Max(0, RetryDelaySeconds + (RetryDelayIncrease * Attempt));

        public StreamTrackRetryPolicy NextAttempt()
            => this with { Attempt = Attempt + 1 };
    }

    public readonly record struct StreamResponseHeaders(
        int StatusCode,
        string ContentType,
        long? ContentLength,
        string? ContentRange);

    public DecryptionStreamProcessor(ILogger<DecryptionStreamProcessor> logger, CryptoService cryptoService, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _cryptoService = cryptoService;
        _httpClientFactory = httpClientFactory;
    }

    public async Task DownloadEncryptedWithResumeAsync(
        string downloadUrl,
        string tempPath,
        DownloadObject downloadObject,
        IDownloadListener? listener,
        CancellationToken cancellationToken = default)
    {
        if (downloadObject.IsCanceled)
        {
            throw new OperationCanceledException(DownloadCanceledMessage);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(tempPath) ?? Path.GetTempPath());
        var existingLength = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0L;

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        request.Headers.Add(UserAgentHeader, LinuxChrome79UserAgent);
        if (existingLength > 0)
        {
            request.Headers.TryAddWithoutValidation("Range", $"bytes={existingLength}-");
        }

        var client = _httpClientFactory.CreateClient("DeezSpoTagDownload");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength ?? 0;
        var totalExpected = existingLength + contentLength;
        if (totalExpected == 0)
        {
            throw new InvalidOperationException(DownloadEmptyMessage);
        }

        using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(tempPath, FileMode.Append, FileAccess.Write, FileShare.Read, 8192, useAsync: true);
        var buffer = new byte[8192];
        long downloaded = existingLength;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            downloaded += bytesRead;

            if (totalExpected > 0)
            {
                downloadObject.ProgressNext += (double)bytesRead / totalExpected / Math.Max(downloadObject.Size, 1) * 100;
                downloadObject.UpdateProgress(listener);
            }
        }
    }

    public async Task DecryptFileAsync(
        string encryptedPath,
        string outputPath,
        Track track,
        CancellationToken cancellationToken = default)
    {
        var blowfishKey = CryptoService.GenerateBlowfishKeyString(ResolveStreamTrackId(track));
        using var input = new FileStream(encryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 8192, useAsync: true);

        var buffer = new byte[8192];
        var decryptionBuffer = Array.Empty<byte>();
        var isFirstChunk = true;
        long chunkCounter = 0;

        while (true)
        {
            var bytesRead = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            var chunkData = new byte[bytesRead];
            Array.Copy(buffer, chunkData, bytesRead);

            var combinedBuffer = new byte[decryptionBuffer.Length + chunkData.Length];
            Array.Copy(decryptionBuffer, combinedBuffer, decryptionBuffer.Length);
            Array.Copy(chunkData, 0, combinedBuffer, decryptionBuffer.Length, chunkData.Length);

            (decryptionBuffer, isFirstChunk) = await ProcessDecryptBufferAsync(
                output,
                combinedBuffer,
                blowfishKey,
                chunkCounter,
                isFirstChunk,
                cancellationToken);
            chunkCounter += (combinedBuffer.Length / 2048);
        }

        if (decryptionBuffer.Length > 0)
        {
            var finalData = DecryptEveryThirdChunk(decryptionBuffer, blowfishKey, ref chunkCounter);
            if (finalData.Length > 0)
            {
                await output.WriteAsync(finalData.AsMemory(0, finalData.Length), cancellationToken);
            }
        }
    }

    private async Task<(byte[] remainingBuffer, bool isFirstChunk)> ProcessDecryptBufferAsync(
        Stream output,
        byte[] combinedBuffer,
        string blowfishKey,
        long chunkCounter,
        bool isFirstChunk,
        CancellationToken cancellationToken)
    {
        var processedLength = (combinedBuffer.Length / 2048) * 2048;
        if (processedLength <= 0)
        {
            return (combinedBuffer, isFirstChunk);
        }

        var toProcess = new byte[processedLength];
        Array.Copy(combinedBuffer, toProcess, processedLength);
        var processedData = DecryptEveryThirdChunk(toProcess, blowfishKey, ref chunkCounter);
        var normalizedChunk = NormalizeFirstChunk(processedData, ref isFirstChunk);
        await WriteChunkIfAnyAsync(output, normalizedChunk, cancellationToken);

        var remainingLength = combinedBuffer.Length - processedLength;
        var remainingBuffer = new byte[remainingLength];
        Array.Copy(combinedBuffer, processedLength, remainingBuffer, 0, remainingLength);
        return (remainingBuffer, isFirstChunk);
    }

    private static byte[] NormalizeFirstChunk(byte[] chunk, ref bool isFirstChunk)
    {
        if (!isFirstChunk || chunk.Length == 0)
        {
            return chunk;
        }

        isFirstChunk = false;
        return DeezerAudioPadding.RemoveLeadingNullPadding(chunk);
    }

    private static async Task WriteChunkIfAnyAsync(Stream output, byte[] chunk, CancellationToken cancellationToken)
    {
        if (chunk.Length == 0)
        {
            return;
        }

        await output.WriteAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
    }

    private byte[] DecryptEveryThirdChunk(byte[] data, string blowfishKey, ref long chunkCounter)
    {
        if (data == null || data.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var result = new byte[data.Length];
        var offset = 0;

        while (offset < data.Length)
        {
            var chunkSize = Math.Min(2048, data.Length - offset);
            if (chunkSize == 2048 && (chunkCounter % 3) == 0)
            {
                var chunk = new byte[2048];
                Array.Copy(data, offset, chunk, 0, 2048);
                var decrypted = _cryptoService.DecryptChunk(chunk, blowfishKey);
                Array.Copy(decrypted, 0, result, offset, decrypted.Length);
            }
            else
            {
                Array.Copy(data, offset, result, offset, chunkSize);
            }

            offset += chunkSize;
            if (chunkSize == 2048)
            {
                chunkCounter++;
            }
        }

        return result;
    }

    /// <summary>
    /// Stream and decrypt track data (EXACT port of streamTrack function from deezspotag)
    /// </summary>
    public async Task StreamTrackAsync(
        string writePath,
        Track track,
        string? downloadUrl,
        DownloadObject downloadObject,
        IDownloadListener? listener,
        CancellationToken cancellationToken = default)
    {
        await StreamTrackAsync(
            writePath,
            track,
            downloadUrl,
            downloadObject,
            listener,
            StreamTrackRetryPolicy.NoRetries,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Stream and decrypt track data with retry policy.
    /// </summary>
    public async Task StreamTrackAsync(
        string writePath,
        Track track,
        string? downloadUrl,
        DownloadObject downloadObject,
        IDownloadListener? listener,
        StreamTrackRetryPolicy retryPolicy,
        CancellationToken cancellationToken = default)
    {
        if (downloadObject.IsCanceled)
        {
            throw new OperationCanceledException(DownloadCanceledMessage);
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new ArgumentNullException(nameof(downloadUrl));
        }

        var resolvedDownloadUrl = downloadUrl;
        var headers = new Dictionary<string, string>
        {
            [UserAgentHeader] = LinuxChrome79UserAgent
        };

        var chunkLength = 0L;
        var complete = 0L;
        var isCryptedStream = downloadUrl.Contains("/mobile/") || downloadUrl.Contains("/media/");
        string? blowfishKey = null;

        using var outputStream = new FileStream(writePath, FileMode.Create, FileAccess.Write, FileShare.Read, 8192, useAsync: true);

        var error = "";

        if (isCryptedStream)
        {
            blowfishKey = CryptoService.GenerateBlowfishKeyString(ResolveStreamTrackId(track));
        }

        _logger.LogInformation("Attempting to download from URL: {DownloadUrl}", resolvedDownloadUrl);
        _logger.LogInformation("Is crypted stream: {IsCryptedStream}", isCryptedStream);

        try
        {
            using var response = await SendRequestWithSslFallbackAsync(resolvedDownloadUrl, headers[UserAgentHeader], rangeHeader: null, cancellationToken);
            response.EnsureSuccessStatusCode();
            complete = response.Content.Headers.ContentLength ?? 0;
            if (complete == 0)
            {
                error = DownloadEmptyMessage;
                throw new InvalidOperationException(DownloadEmptyMessage);
            }

            using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);

            using var timeout = new Timer(_ =>
            {
                error = DownloadTimeoutMessage;
            }, null, Timeout.Infinite, Timeout.Infinite);
            timeout.Change(DownloadTimeoutMs, Timeout.Infinite);

            var result = await ProcessDeezSpoTagPipelineAsync(responseStream, outputStream, new PipelineProcessingContext
            {
                IsCryptedStream = isCryptedStream,
                BlowfishKey = blowfishKey,
                Complete = complete,
                DownloadObject = downloadObject,
                Listener = listener,
                ChunkLength = chunkLength,
                Error = error,
                Timeout = timeout,
                BufferSize = 8192,
                DropBytes = 0,
                IsDepadderStart = true
            }, cancellationToken);

            chunkLength = result.chunkLength;
            error = result.error;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DeletePartialFileIfExists(writePath);
            await HandleTrackDownloadFailureAsync(
                ex,
                error,
                new TrackDownloadFailureContext
                {
                    ChunkLength = chunkLength,
                    Complete = complete,
                    WritePath = writePath,
                    Track = track,
                    DownloadUrl = resolvedDownloadUrl,
                    DownloadObject = downloadObject,
                    Listener = listener,
                    RetryPolicy = retryPolicy
                },
                cancellationToken);
        }
    }

    private static void DeletePartialFileIfExists(string writePath)
    {
        if (File.Exists(writePath))
        {
            File.Delete(writePath);
        }
    }

    private async Task HandleTrackDownloadFailureAsync(
        Exception ex,
        string error,
        TrackDownloadFailureContext context,
        CancellationToken cancellationToken)
    {
        if (!IsDeezSpoTagRetryableError(ex, error))
        {
            ThrowNonRetryableTrackDownloadException(context.Track, error, ex);
            return;
        }

        var normalizedMaxRetries = context.RetryPolicy.NormalizedMaxRetries;
        if (context.RetryPolicy.Attempt >= normalizedMaxRetries)
        {
            _logger.LogWarning(ex, "Retry limit reached for track {TrackId} after {Attempts} attempts", context.Track.Id, context.RetryPolicy.Attempt);
            throw new InvalidOperationException($"Download failed after {context.RetryPolicy.Attempt} retries for track '{context.Track.Id}'.", ex);
        }

        RollbackDownloadProgress(context.DownloadObject, context.Listener, context.ChunkLength, context.Complete);
        var delaySeconds = NotifyRetryScheduled(context.DownloadObject, context.Listener, context.RetryPolicy, normalizedMaxRetries);
        if (delaySeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
        }

        await StreamTrackAsync(
            context.WritePath,
            context.Track,
            context.DownloadUrl,
            context.DownloadObject,
            context.Listener,
            context.RetryPolicy.NextAttempt(),
            cancellationToken);
    }

    private static void ThrowNonRetryableTrackDownloadException(Track track, string error, Exception ex)
    {
        if (error == DownloadEmptyMessage)
        {
            throw new InvalidOperationException(DownloadEmptyMessage);
        }

        if (error == DownloadCanceledMessage)
        {
            throw new OperationCanceledException(DownloadCanceledMessage);
        }

        throw new InvalidOperationException($"Failed to download track {track.Id}", ex);
    }

    private static void RollbackDownloadProgress(
        DownloadObject downloadObject,
        IDownloadListener? listener,
        long chunkLength,
        long complete)
    {
        if (chunkLength == 0 || complete <= 0)
        {
            return;
        }

        downloadObject.ProgressNext -= (double)chunkLength / complete / downloadObject.Size * 100;
        downloadObject.UpdateProgress(listener);
    }

    private static int NotifyRetryScheduled(
        DownloadObject downloadObject,
        IDownloadListener? listener,
        StreamTrackRetryPolicy retryPolicy,
        int normalizedMaxRetries)
    {
        var delaySeconds = retryPolicy.ResolveDelaySeconds();
        var attemptDisplay = retryPolicy.Attempt + 1;
        var retryMessage = $"Retrying download ({attemptDisplay}/{normalizedMaxRetries}) after {delaySeconds}s";
        listener?.OnDownloadInfo(downloadObject, retryMessage, "downloadRetry");
        return delaySeconds;
    }

    /// <summary>
    /// Stream and decrypt track data directly to an output stream (preview-friendly).
    /// </summary>
    public async Task StreamTrackToStreamAsync(
        Stream outputStream,
        Track track,
        string downloadUrl,
        DownloadObject downloadObject,
        IDownloadListener? listener,
        CancellationToken cancellationToken = default)
    {
        if (downloadObject.IsCanceled)
        {
            throw new OperationCanceledException(DownloadCanceledMessage);
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new ArgumentNullException(nameof(downloadUrl));
        }

        var headers = new Dictionary<string, string>
        {
            [UserAgentHeader] = LinuxChrome79UserAgent
        };

        var chunkLength = 0L;
        var complete = 0L;
        var isCryptedStream = downloadUrl.Contains("/mobile/") || downloadUrl.Contains("/media/");
        string? blowfishKey = null;

        var error = string.Empty;

        if (isCryptedStream)
        {
            blowfishKey = CryptoService.GenerateBlowfishKeyString(ResolveStreamTrackId(track));
        }

        _logger.LogInformation("Attempting preview stream from URL: {DownloadUrl}", downloadUrl);
        _logger.LogInformation("Is crypted stream: {IsCryptedStream}", isCryptedStream);

        using var response = await SendRequestWithSslFallbackAsync(downloadUrl, headers[UserAgentHeader], rangeHeader: null, cancellationToken);
        response.EnsureSuccessStatusCode();
        complete = response.Content.Headers.ContentLength ?? 0;
        if (complete == 0)
        {
            error = DownloadEmptyMessage;
            throw new InvalidOperationException(DownloadEmptyMessage);
        }

        using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        using var timeout = new Timer(_ => { error = DownloadTimeoutMessage; }, null, Timeout.Infinite, Timeout.Infinite);
        timeout.Change(DownloadTimeoutMs, Timeout.Infinite);

        var (_, resultError) = await ProcessDeezSpoTagPipelineAsync(responseStream, outputStream, new PipelineProcessingContext
        {
            IsCryptedStream = isCryptedStream,
            BlowfishKey = blowfishKey,
            Complete = complete,
            DownloadObject = downloadObject,
            Listener = listener,
            ChunkLength = chunkLength,
            Error = error,
            Timeout = timeout,
            BufferSize = 2048,
            DropBytes = 0,
            IsDepadderStart = true
        }, cancellationToken);

        error = resultError;
    }

    public async Task StreamTrackToStreamAsync(
        Stream outputStream,
        Track track,
        string downloadUrl,
        DownloadObject downloadObject,
        IDownloadListener? listener,
        long startBytes,
        long? endBytes,
        Func<StreamResponseHeaders, Task>? configureResponse,
        CancellationToken cancellationToken = default)
    {
        if (downloadObject.IsCanceled)
        {
            throw new OperationCanceledException(DownloadCanceledMessage);
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new ArgumentNullException(nameof(downloadUrl));
        }

        var isCryptedStream = downloadUrl.Contains("/mobile/") || downloadUrl.Contains("/media/");
        var requestedStartBytes = Math.Max(0, startBytes);
        long? requestedEndBytes = endBytes.HasValue && endBytes.Value >= requestedStartBytes
            ? endBytes.Value
            : null;
        var upstreamStartBytes = isCryptedStream && requestedStartBytes > 0
            ? requestedStartBytes - (requestedStartBytes % 2048)
            : requestedStartBytes;
        var dropBytes = isCryptedStream ? (int)(requestedStartBytes - upstreamStartBytes) : 0;
        var rangeHeader = requestedStartBytes > 0 || requestedEndBytes.HasValue
            ? $"bytes={upstreamStartBytes}-{(requestedEndBytes.HasValue ? requestedEndBytes.Value.ToString() : string.Empty)}"
            : null;
        var blowfishKey = isCryptedStream
            ? CryptoService.GenerateBlowfishKeyString(ResolveStreamTrackId(track))
            : null;

        using var response = await SendRequestWithSslFallbackAsync(downloadUrl, LinuxChrome79UserAgent, rangeHeader, cancellationToken);
        response.EnsureSuccessStatusCode();

        var sourceLength = response.Content.Headers.ContentLength;
        var sourceTotalLength = response.Content.Headers.ContentRange?.Length;
        long? outputLength = sourceLength.HasValue ? Math.Max(0L, sourceLength.Value - dropBytes) : null;

        if (configureResponse != null)
        {
            await configureResponse(new StreamResponseHeaders(
                requestedStartBytes > 0 || requestedEndBytes.HasValue ? (int)HttpStatusCode.PartialContent : (int)HttpStatusCode.OK,
                ResolveContentType(downloadObject),
                outputLength,
                BuildContentRangeHeaderValue(requestedStartBytes, outputLength, sourceTotalLength)));
        }

        using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var timeout = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        timeout.Change(DownloadTimeoutMs, Timeout.Infinite);

        await ProcessDeezSpoTagPipelineAsync(responseStream, outputStream, new PipelineProcessingContext
        {
            IsCryptedStream = isCryptedStream,
            BlowfishKey = blowfishKey,
            Complete = sourceLength ?? 0,
            DownloadObject = downloadObject,
            Listener = listener,
            ChunkLength = 0L,
            Error = string.Empty,
            Timeout = timeout,
            BufferSize = 2048,
            DropBytes = dropBytes,
            IsDepadderStart = requestedStartBytes == 0
        }, cancellationToken);
    }

    private static bool IsSslHandshakeError(HttpRequestException exception)
    {
        var message = exception.Message;
        return message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
            || message.Contains("handshake", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> SendRequestWithSslFallbackAsync(
        string downloadUrl,
        string userAgent,
        string? rangeHeader,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        request.Headers.Add(UserAgentHeader, userAgent);
        if (!string.IsNullOrWhiteSpace(rangeHeader))
        {
            request.Headers.TryAddWithoutValidation("Range", rangeHeader);
        }

        try
        {
            var client = _httpClientFactory.CreateClient("DeezSpoTagDownload");
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException httpEx) when (IsSslHandshakeError(httpEx))
        {
            _logger.LogWarning(httpEx, "Primary SSL configuration failed for {Url}, trying fallback configurations", downloadUrl);

            var response = await TryWithFallbackSslAsync(downloadUrl, userAgent, rangeHeader, SslProtocols.Tls13, cancellationToken);
            if (response == null)
            {
                response = await TryWithFallbackSslAsync(downloadUrl, userAgent, rangeHeader, SslProtocols.Tls12, cancellationToken);
            }

            response ??= await TryWithFallbackSslAsync(downloadUrl, userAgent, rangeHeader, SslProtocols.None, cancellationToken);
            if (response != null)
            {
                _logger.LogInformation("Fallback SSL configuration succeeded for {Url}", downloadUrl);
                return response;
            }

            throw new HttpRequestException(
                $"Primary and fallback SSL configurations failed for download URL '{downloadUrl}'.",
                httpEx);
        }
    }

    private async Task<HttpResponseMessage?> TryWithFallbackSslAsync(
        string url,
        string userAgent,
        string? rangeHeader,
        SslProtocols sslProtocol,
        CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new HttpClientHandler();
            TlsPolicy.ApplyIfAllowed(handler, configuration: null);
            handler.UseCookies = false;
            handler.AllowAutoRedirect = true;
            handler.MaxAutomaticRedirections = 10;
            handler.SslProtocols = sslProtocol;
            handler.ClientCertificateOptions = ClientCertificateOption.Manual;
            handler.PreAuthenticate = false;
            handler.UseDefaultCredentials = false;
            handler.AutomaticDecompression = DecompressionMethods.None;

            using var fallbackClient = new HttpClient(handler);
            fallbackClient.DefaultRequestHeaders.Add(UserAgentHeader, userAgent);
            fallbackClient.Timeout = TimeSpan.FromSeconds(30);

            using var fallbackRequest = new HttpRequestMessage(HttpMethod.Get, url);
            fallbackRequest.Headers.Add(UserAgentHeader, userAgent);
            if (!string.IsNullOrWhiteSpace(rangeHeader))
            {
                fallbackRequest.Headers.TryAddWithoutValidation("Range", rangeHeader);
            }

            var response = await fallbackClient.SendAsync(fallbackRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            _logger.LogInformation("Fallback SSL protocol {Protocol} succeeded for {Url}", sslProtocol, url);
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Fallback SSL protocol {Protocol} failed for {Url}", sslProtocol, url);
            return null;
        }
    }

    private static string ResolveStreamTrackId(Track track)
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

    private static bool IsDeezSpoTagRetryableError(Exception ex, string error)
    {
        if (ex is TaskCanceledException || ex is TimeoutException)
        {
            return true;
        }

        if (error == DownloadTimeoutMessage)
        {
            return true;
        }

        var message = ex.Message.ToLowerInvariant();
        return message.Contains("esockettimedout")
            || message.Contains("err_stream_premature_close")
            || message.Contains("etimedout")
            || message.Contains("econnreset");
    }

    private async Task<(long chunkLength, string error)> ProcessDeezSpoTagPipelineAsync(
        Stream responseStream,
        Stream outputStream,
        PipelineProcessingContext context,
        CancellationToken cancellationToken)
    {
        var state = new PipelineState(
            context.ChunkLength,
            context.Error,
            new List<byte>(),
            isDepadderStart: context.IsDepadderStart,
            dropBytes: context.DropBytes,
            decryptChunkCounter: 0,
            flushedFirstChunk: false);
        var bufferSize = context.BufferSize >= 1024 ? context.BufferSize : 1024;
        var buffer = new byte[bufferSize];

        while (true)
        {
            var bytesRead = await ReadPipelineChunkAsync(responseStream, buffer, context.DownloadObject, cancellationToken);
            if (bytesRead <= 0)
            {
                break;
            }

            state.ChunkLength += bytesRead;
            UpdatePipelineProgress(context.DownloadObject, context.Listener, context.Complete, bytesRead);
            context.Timeout?.Change(DownloadTimeoutMs, Timeout.Infinite);
            var chunk = ExtractChunk(buffer, bytesRead);
            await WriteProcessedPipelineChunksAsync(
                chunk,
                outputStream,
                context.IsCryptedStream,
                context.BlowfishKey,
                isFinal: false,
                state,
                cancellationToken);
        }

        await WriteProcessedPipelineChunksAsync(
            Array.Empty<byte>(),
            outputStream,
            context.IsCryptedStream,
            context.BlowfishKey,
            isFinal: true,
            state,
            cancellationToken);

        context.ChunkLength = state.ChunkLength;
        context.Error = state.Error;

        return (state.ChunkLength, state.Error);
    }

    private sealed class PipelineProcessingContext
    {
        public required bool IsCryptedStream { get; init; }
        public required string? BlowfishKey { get; init; }
        public required long Complete { get; init; }
        public required DownloadObject? DownloadObject { get; init; }
        public required IDownloadListener? Listener { get; init; }
        public required long ChunkLength { get; set; }
        public required string Error { get; set; }
        public required Timer? Timeout { get; init; }
        public required int BufferSize { get; init; }
        public required int DropBytes { get; init; }
        public required bool IsDepadderStart { get; init; }
    }

    private sealed class PipelineState(
        long chunkLength,
        string error,
        List<byte> modifiedStream,
        bool isDepadderStart,
        int dropBytes,
        long decryptChunkCounter,
        bool flushedFirstChunk)
    {
        public long ChunkLength = chunkLength;
        public string Error = error;
        public List<byte> ModifiedStream { get; } = modifiedStream;
        public bool IsDepadderStart = isDepadderStart;
        public int DropBytes = dropBytes;
        public long DecryptChunkCounter = decryptChunkCounter;
        public bool FlushedFirstChunk = flushedFirstChunk;
    }

    private static async Task<int> ReadPipelineChunkAsync(
        Stream responseStream,
        byte[] buffer,
        DownloadObject? downloadObject,
        CancellationToken cancellationToken)
    {
        if (downloadObject?.IsCanceled == true)
        {
            throw new OperationCanceledException(DownloadCanceledMessage);
        }

        return await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
    }

    private sealed class TrackDownloadFailureContext
    {
        public required long ChunkLength { get; init; }
        public required long Complete { get; init; }
        public required string WritePath { get; init; }
        public required Track Track { get; init; }
        public required string DownloadUrl { get; init; }
        public required DownloadObject DownloadObject { get; init; }
        public required IDownloadListener? Listener { get; init; }
        public required StreamTrackRetryPolicy RetryPolicy { get; init; }
    }

    private static void UpdatePipelineProgress(
        DownloadObject? downloadObject,
        IDownloadListener? listener,
        long complete,
        int bytesRead)
    {
        if (downloadObject == null || complete <= 0)
        {
            return;
        }

        downloadObject.ProgressNext += (double)bytesRead / complete / downloadObject.Size * 100;
        downloadObject.UpdateProgress(listener);
    }

    private static byte[] ExtractChunk(byte[] buffer, int bytesRead)
    {
        var chunk = new byte[bytesRead];
        Array.Copy(buffer, 0, chunk, 0, bytesRead);
        return chunk;
    }

    private async Task WriteProcessedPipelineChunksAsync(
        byte[] chunk,
        Stream outputStream,
        bool isCryptedStream,
        string? blowfishKey,
        bool isFinal,
        PipelineState state,
        CancellationToken cancellationToken)
    {
        var decryptedChunks = ProcessDeezSpoTagDecrypter(
            chunk,
            state.ModifiedStream,
            isCryptedStream,
            blowfishKey,
            isFinal,
            ref state.DecryptChunkCounter);

        foreach (var decryptedChunk in decryptedChunks)
        {
            var rangedChunk = ApplyInitialDropBytes(decryptedChunk, ref state.DropBytes);
            var depaddedChunk = ProcessDeezSpoTagDepadder(rangedChunk, ref state.IsDepadderStart);
            if (depaddedChunk.Length == 0)
            {
                continue;
            }

            await outputStream.WriteAsync(depaddedChunk.AsMemory(0, depaddedChunk.Length), cancellationToken);
            if (!state.FlushedFirstChunk)
            {
                await outputStream.FlushAsync(cancellationToken);
                state.FlushedFirstChunk = true;
            }
        }
    }

    private List<byte[]> ProcessDeezSpoTagDecrypter(
        byte[] chunk,
        List<byte> modifiedStream,
        bool isCryptedStream,
        string? blowfishKey,
        bool isFinal,
        ref long chunkCounter)
    {
        var results = new List<byte[]>();

        if (!isCryptedStream)
        {
            if (chunk.Length > 0)
            {
                results.Add(chunk);
            }
            return results;
        }

        modifiedStream.AddRange(chunk);

        while (modifiedStream.Count >= 2048)
        {
            var block = modifiedStream.Take(2048).ToArray();
            modifiedStream.RemoveRange(0, 2048);

            if ((chunkCounter % 3) == 0)
            {
                block = _cryptoService.DecryptChunk(block, blowfishKey!);
            }

            results.Add(block);
            chunkCounter++;
        }

        if (isFinal && modifiedStream.Count > 0)
        {
            results.Add(modifiedStream.ToArray());
            modifiedStream.Clear();
        }

        return results;
    }

    private static byte[] ProcessDeezSpoTagDepadder(byte[] chunk, ref bool isStart)
    {
        if (chunk.Length == 0)
        {
            return chunk;
        }

        chunk = RemoveLeadingPaddingIfNeeded(chunk, isStart);

        isStart = false;
        return chunk;
    }

    private static byte[] ApplyInitialDropBytes(byte[] chunk, ref int dropBytes)
    {
        if (chunk.Length == 0 || dropBytes <= 0)
        {
            return chunk;
        }

        if (dropBytes >= chunk.Length)
        {
            dropBytes -= chunk.Length;
            return Array.Empty<byte>();
        }

        var result = new byte[chunk.Length - dropBytes];
        Array.Copy(chunk, dropBytes, result, 0, result.Length);
        dropBytes = 0;
        return result;
    }

    private static string ResolveContentType(DownloadObject downloadObject)
        => downloadObject.Bitrate >= 1000 ? "audio/flac" : "audio/mpeg";

    private static string? BuildContentRangeHeaderValue(long startBytes, long? contentLength, long? totalLength)
    {
        if (!contentLength.HasValue || !totalLength.HasValue || contentLength.Value <= 0)
        {
            return null;
        }

        var endBytes = startBytes + contentLength.Value - 1;
        return $"bytes {startBytes}-{endBytes}/{totalLength.Value}";
    }

    private static byte[] RemoveLeadingPaddingIfNeeded(byte[] chunk, bool isStart)
    {
        if (!isStart || chunk[0] != 0)
        {
            return chunk;
        }

        if (LooksLikeMp4Chunk(chunk))
        {
            return chunk;
        }

        var firstNonZeroIndex = FindFirstNonZeroByte(chunk);
        if (firstNonZeroIndex <= 0)
        {
            return chunk;
        }

        var result = new byte[chunk.Length - firstNonZeroIndex];
        Array.Copy(chunk, firstNonZeroIndex, result, 0, result.Length);
        return result;
    }

    private static bool LooksLikeMp4Chunk(byte[] chunk)
    {
        if (chunk.Length < 8)
        {
            return false;
        }

        try
        {
            return Encoding.ASCII.GetString(chunk, 4, 4) == "ftyp";
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int FindFirstNonZeroByte(byte[] chunk)
    {
        for (var i = 0; i < chunk.Length; i++)
        {
            if (chunk[i] != 0)
            {
                return i;
            }
        }

        return -1;
    }
}
