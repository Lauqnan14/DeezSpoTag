using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Download;
using DeezSpoTag.Services.Crypto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DecryptionStreamProcessorTest
{
    private const int ChunkSize = 2048;
    private static readonly int[] ExpectedEncryptedBlocks = { 0, 3, 6, 9 };

    #region StreamTrackRetryPolicy

    [Fact]
    public void RetryPolicy_NormalizesNegativeRetriesToZero()
    {
        var policy = new DecryptionStreamProcessor.StreamTrackRetryPolicy(-3, 5, 1);
        Assert.Equal(0, policy.NormalizedMaxRetries);
    }

    [Fact]
    public void RetryPolicy_DelayGrowsWithAttempts()
    {
        var policy = new DecryptionStreamProcessor.StreamTrackRetryPolicy(3, 2, 5);
        Assert.Equal(2, policy.ResolveDelaySeconds());
        Assert.Equal(7, policy.NextAttempt().ResolveDelaySeconds());
        Assert.Equal(12, policy.NextAttempt().NextAttempt().ResolveDelaySeconds());
    }

    [Fact]
    public void RetryPolicy_NegativeDelayClampedToZero()
    {
        var policy = new DecryptionStreamProcessor.StreamTrackRetryPolicy(2, -4, -1);
        Assert.Equal(0, policy.ResolveDelaySeconds());
    }

    [Fact]
    public void RetryPolicy_NoRetriesConstant_HasZeroEverything()
    {
        var policy = DecryptionStreamProcessor.StreamTrackRetryPolicy.NoRetries;
        Assert.Equal(0, policy.MaxRetries);
        Assert.Equal(0, policy.Attempt);
        Assert.Equal(0, policy.ResolveDelaySeconds());
    }

    #endregion

    #region DecryptFileAsync round-trips

    /// <summary>
    /// Builds a complete Deezer-style file: absolute blocks 0, 3, 6, ... are encrypted,
    /// each with a fresh CBC IV. Encryption is independent of read windows and HTTP ranges.
    /// </summary>
    private static byte[] BuildEncryptedFixture(byte[] plaintext, byte[] key, out List<int> encryptedBlocks)
    {
        encryptedBlocks = new List<int>();
        var blockCount = plaintext.Length / ChunkSize;
        var encrypted = new byte[plaintext.Length];
        Array.Copy(plaintext, encrypted, plaintext.Length);

        for (var block = 0; block < blockCount; block += 3)
        {
            EncryptBfCbc(Slice(plaintext, block), key).CopyTo(encrypted, block * ChunkSize);
            encryptedBlocks.Add(block);
        }

        return encrypted;
    }

    private static readonly byte[] BcIv = { 0, 1, 2, 3, 4, 5, 6, 7 };

    private static byte[] EncryptBfCbc(byte[] plaintext, byte[] key)
    {
        var cipher = new Org.BouncyCastle.Crypto.Modes.CbcBlockCipher(new Org.BouncyCastle.Crypto.Engines.BlowfishEngine());
        cipher.Init(true, new Org.BouncyCastle.Crypto.Parameters.ParametersWithIV(
            new Org.BouncyCastle.Crypto.Parameters.KeyParameter(key), BcIv));
        var output = new byte[plaintext.Length];
        for (var offset = 0; offset < plaintext.Length; offset += 8)
        {
            cipher.ProcessBlock(plaintext, offset, output, offset);
        }

        return output;
    }

    private static byte[] UniquePlaintext(int blockCount, byte firstByte = 0x41, int tailBytes = 0)
    {
        var data = new byte[blockCount * ChunkSize + tailBytes];
        new Random(20261028).NextBytes(data);
        data[0] = firstByte;
        for (var i = 0; i < blockCount; i++)
        {
            data[i * ChunkSize] = (byte)('A' + (i % 26)); // make each block distinguishable
        }

        return data;
    }

    [Fact]
    public async Task DecryptFileAsync_RestoresEveryThirdChunkEncryptedFile()
    {
        var track = new Track { Id = "1234567" };
        var key = CryptoService.GenerateBlowfishKey(track.Id);
        var plaintext = UniquePlaintext(12, tailBytes: 100);
        var fixture = BuildEncryptedFixture(plaintext, key, out var encryptedBlocks);
        Assert.Equal(ExpectedEncryptedBlocks, encryptedBlocks);

        var tempDir = Path.Combine(Path.GetTempPath(), "dst-decrypt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var encryptedPath = Path.Combine(tempDir, "in.bin");
            var outputPath = Path.Combine(tempDir, "out.bin");
            await File.WriteAllBytesAsync(encryptedPath, fixture);

            var processor = new DecryptionStreamProcessor(
                NullLogger<DecryptionStreamProcessor>.Instance,
                new CryptoService(NullLogger<CryptoService>.Instance),
                new StubHttpClientFactory(new StubHandler(_ => throw new InvalidOperationException("not used"))));
            await processor.DecryptFileAsync(encryptedPath, outputPath, track, CancellationToken.None);

            Assert.Equal(plaintext, await File.ReadAllBytesAsync(outputPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DecryptFileAsync_StripsLeadingNullPaddingOfFirstChunk()
    {
        var track = new Track { Id = "7654321" };
        var key = CryptoService.GenerateBlowfishKey(track.Id);
        var plaintext = UniquePlaintext(4);
        var withPadding = new byte[plaintext.Length + 5];
        withPadding[5] = plaintext[0];
        // Leave bytes 0..4 as zero padding; production's first-chunk depadder removes them.
        for (var i = 0; i < plaintext.Length; i++)
        {
            withPadding[5 + i] = plaintext[i];
        }

        var fixture = BuildEncryptedFixture(withPadding, key, out _);

        var tempDir = Path.Combine(Path.GetTempPath(), "dst-decrypt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var encryptedPath = Path.Combine(tempDir, "in.bin");
            var outputPath = Path.Combine(tempDir, "out.bin");
            await File.WriteAllBytesAsync(encryptedPath, fixture);

            var processor = new DecryptionStreamProcessor(
                NullLogger<DecryptionStreamProcessor>.Instance,
                new CryptoService(NullLogger<CryptoService>.Instance),
                new StubHttpClientFactory(new StubHandler(_ => throw new InvalidOperationException("not used"))));
            await processor.DecryptFileAsync(encryptedPath, outputPath, track, CancellationToken.None);

            Assert.Equal(plaintext, await File.ReadAllBytesAsync(outputPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DecryptFileAsync_PrefersFallbackIDOverIdForKeyDerivation()
    {
        var track = new Track { Id = "1111111", FallbackID = 2222222 };
        var fallbackKey = CryptoService.GenerateBlowfishKey("2222222");
        var plaintext = UniquePlaintext(4);
        var fixture = BuildEncryptedFixture(plaintext, fallbackKey, out var encryptedBlocks);
        Assert.NotEmpty(encryptedBlocks); // fixture really is encrypted with the FallbackID-derived key

        var tempDir = Path.Combine(Path.GetTempPath(), "dst-decrypt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var encryptedPath = Path.Combine(tempDir, "in.bin");
            var outputPath = Path.Combine(tempDir, "out.bin");
            await File.WriteAllBytesAsync(encryptedPath, fixture);

            var processor = new DecryptionStreamProcessor(
                NullLogger<DecryptionStreamProcessor>.Instance,
                new CryptoService(NullLogger<CryptoService>.Instance),
                new StubHttpClientFactory(new StubHandler(_ => throw new InvalidOperationException("not used"))));
            await processor.DecryptFileAsync(encryptedPath, outputPath, track, CancellationToken.None);

            Assert.Equal(plaintext, await File.ReadAllBytesAsync(outputPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    #endregion

    #region StreamTrackToStreamAsync

    private static readonly string PlainUrl = "http://download.test/api/1/plain";
    private static readonly string CryptedUrl = "http://download.test/mobile/1/crypted";

    [Fact]
    public async Task StreamTrackToStreamAsync_PlainUrl_PassesBytesThrough()
    {
        var payload = UniquePlaintext(3, tailBytes: 45);
        string? capturedUserAgent = null;
        var handler = new StubHandler(_ =>
        {
            capturedUserAgent = GetUserAgent(_!);
            return BinaryResponse(HttpStatusCode.OK, payload);
        });

        var output = new MemoryStream();
        var processor = CreateProcessor(handler);
        await processor.StreamTrackToStreamAsync(
            output,
            new Track { Id = "1" },
            PlainUrl,
            new SingleDownloadObject { Bitrate = 320 },
            listener: null,
            CancellationToken.None);

        Assert.Equal(payload, output.ToArray());
        Assert.Equal("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/79.0.3945.130 Safari/537.36", capturedUserAgent);
    }

    [Fact]
    public async Task StreamTrackToStreamAsync_CryptedUrl_RestoresEveryThirdChunk()
    {
        var track = new Track { Id = "2468135" };
        var key = CryptoService.GenerateBlowfishKey(track.Id);
        var plaintext = UniquePlaintext(5, tailBytes: 1000);
        // The streaming pipeline decrypts relative blocks 0, 3, ... of the response body.
        var fixture = new byte[plaintext.Length];
        Array.Copy(plaintext, fixture, plaintext.Length);
        for (var block = 0; block * ChunkSize + ChunkSize <= plaintext.Length; block += 3)
        {
            var slice = new byte[ChunkSize];
            Array.Copy(plaintext, block * ChunkSize, slice, 0, ChunkSize);
            EncryptBfCbc(slice, key).CopyTo(fixture, block * ChunkSize);
        }

        var handler = new StubHandler(_ => BinaryResponse(HttpStatusCode.OK, fixture));
        var output = new MemoryStream();
        var processor = CreateProcessor(handler);

        await processor.StreamTrackToStreamAsync(
            output,
            track,
            CryptedUrl,
            new SingleDownloadObject { Bitrate = 320 },
            listener: null,
            CancellationToken.None);

        Assert.Equal(plaintext, output.ToArray());
    }

    [Fact]
    public async Task StreamTrackToStreamAsync_CanceledObject_ThrowsOperationCanceled()
    {
        var processor = CreateProcessor(new StubHandler(_ => BinaryResponse(HttpStatusCode.OK, new byte[16])));
        var downloadObject = new SingleDownloadObject { IsCanceled = true };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processor.StreamTrackToStreamAsync(new MemoryStream(), new Track { Id = "1" }, PlainUrl, downloadObject, null, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StreamTrackToStreamAsync_MissingUrl_ThrowsArgumentNull(string? url)
    {
        var processor = CreateProcessor(new StubHandler(_ => BinaryResponse(HttpStatusCode.OK, new byte[16])));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => processor.StreamTrackToStreamAsync(new MemoryStream(), new Track { Id = "1" }, url!, new SingleDownloadObject(), null, CancellationToken.None));
    }

    [Fact]
    public async Task StreamTrackToStreamAsync_ConfiguresResponseWithResolvedHeaders()
    {
        var payload = UniquePlaintext(2, tailBytes: 10);
        var handler = new StubHandler(_ => BinaryResponse(HttpStatusCode.OK, payload));
        DecryptionStreamProcessor.StreamResponseHeaders headers = default;
        var downloadObject = new SingleDownloadObject { Bitrate = 320 };

        var processor = CreateProcessor(handler);
        await processor.StreamTrackToStreamAsync(
            new MemoryStream(),
            new DecryptionStreamProcessor.StreamTrackRequest(
                new Track { Id = "1" },
                PlainUrl,
                downloadObject,
                Listener: null,
                ConfigureResponse: value =>
                {
                    headers = value;
                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        Assert.Equal(200, headers.StatusCode);
        Assert.Equal("audio/mpeg", headers.ContentType);
        Assert.Equal(payload.Length, headers.ContentLength);
        Assert.Null(headers.ContentRange);
    }

    [Fact]
    public async Task StreamTrackToStreamAsync_FlacBitrate_ReportsFlacContentType()
    {
        var payload = UniquePlaintext(1, tailBytes: 5);
        var handler = new StubHandler(_ => BinaryResponse(HttpStatusCode.OK, payload));
        DecryptionStreamProcessor.StreamResponseHeaders headers = default;
        var downloadObject = new SingleDownloadObject { Bitrate = 1411 };

        var processor = CreateProcessor(handler);
        await processor.StreamTrackToStreamAsync(
            new MemoryStream(),
            new DecryptionStreamProcessor.StreamTrackRequest(
                new Track { Id = "1" },
                PlainUrl,
                downloadObject,
                Listener: null,
                ConfigureResponse: value =>
                {
                    headers = value;
                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        Assert.Equal("audio/flac", headers.ContentType);
    }

    [Fact]
    public async Task StreamTrackToStreamAsync_PlainRangeRequest_AlignsRangeHeaderAndDropsNothing()
    {
        var payload = UniquePlaintext(4, tailBytes: 100);
        string? rangeHeader = null;
        var handler = new StubHandler(request =>
        {
            rangeHeader = request.Headers.Range?.ToString();
            var start = ParseRangeStart(request);
            var body = new byte[payload.Length - start];
            Array.Copy(payload, start, body, 0, body.Length);
            var response = BinaryResponse(HttpStatusCode.PartialContent, body);
            // The processor derives the reported total from the server's Content-Range header.
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                start, payload.Length - 1, payload.Length) { Unit = "bytes" };
            return response;
        });

        DecryptionStreamProcessor.StreamResponseHeaders headers = default;
        var processor = CreateProcessor(handler);
        using var output = new MemoryStream();
        await processor.StreamTrackToStreamAsync(
            output,
            new DecryptionStreamProcessor.StreamTrackRequest(
                new Track { Id = "1" },
                PlainUrl,
                new SingleDownloadObject { Bitrate = 320 },
                Listener: null,
                StartBytes: 2500,
                ConfigureResponse: value =>
                {
                    headers = value;
                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        Assert.Equal("bytes=2500-", rangeHeader);
        Assert.Equal(payload.AsMemory(2500).ToArray(), output.ToArray());
        Assert.Equal(206, headers.StatusCode);
        Assert.Equal(payload.Length - 2500, headers.ContentLength);
        Assert.Equal($"bytes 2500-{payload.Length - 1}/{payload.Length}", headers.ContentRange);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2048)]
    [InlineData(2500)]
    [InlineData(4096)]
    [InlineData(6144)]
    [InlineData(6500)]
    [InlineData(12300)]
    public async Task StreamTrackToStreamAsync_CryptedRangeRequest_RealignsToChunkBoundaryAndDropsPrefix(int startBytes)
    {
        var track = new Track { Id = "1357246" };
        var key = CryptoService.GenerateBlowfishKey(track.Id);
        var plaintext = UniquePlaintext(12, tailBytes: 1000);
        // A seek must preserve zero bytes at the requested offset, not depad them.
        plaintext[2500] = 0;
        var fixture = BuildEncryptedFixture(plaintext, key, out var encryptedBlocks);
        Assert.Equal(ExpectedEncryptedBlocks, encryptedBlocks);

        string? rangeHeader = null;
        var handler = new StubHandler(request =>
        {
            rangeHeader = request.Headers.Range?.ToString();
            var start = ParseRangeStart(request);
            var body = new byte[fixture.Length - start];
            Array.Copy(fixture, start, body, 0, body.Length);
            return BinaryResponse(HttpStatusCode.PartialContent, body);
        });

        var processor = CreateProcessor(handler);
        var output = new MemoryStream();
        await processor.StreamTrackToStreamAsync(
            output,
            new DecryptionStreamProcessor.StreamTrackRequest(
                track,
                CryptedUrl,
                new SingleDownloadObject { Bitrate = 320 },
                Listener: null,
                StartBytes: startBytes),
            CancellationToken.None);

        var upstreamStart = startBytes - startBytes % ChunkSize;
        Assert.Equal(startBytes == 0 ? null : $"bytes={upstreamStart}-", rangeHeader);
        Assert.Equal(plaintext.AsMemory(startBytes).ToArray(), output.ToArray());
    }

    [Fact]
    public async Task StreamTrackToStreamAsync_SslHandshakeFailure_TriesFallbacksThenThrowsComposedError()
    {
        var primaryFailure = new HttpRequestException("The SSL connection could not be established.");
        var primaryCalls = 0;
        var handler = new StubHandler(_ =>
        {
            primaryCalls++;
            throw primaryFailure;
        });
        var processor = new FallbackTestProcessor(handler, (_, _) => throw new HttpRequestException("simulated fallback failure"));
        using var output = new MemoryStream();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => processor.StreamTrackToStreamAsync(
                output,
                new Track { Id = "1" },
                PlainUrl,
                new SingleDownloadObject { Bitrate = 320 },
                null,
                CancellationToken.None));

        Assert.Contains("Primary and fallback SSL configurations failed", exception.Message);
        Assert.Same(primaryFailure, exception.InnerException);
        Assert.Equal(1, primaryCalls);
        Assert.Equal(new[] { SslProtocols.Tls13, SslProtocols.Tls12, SslProtocols.None }, processor.AttemptedProtocols);
        Assert.Empty(output.ToArray());
    }

    [Theory]
    [InlineData(SslProtocols.Tls13)]
    [InlineData(SslProtocols.Tls12)]
    [InlineData(SslProtocols.None)]
    public void SslFallbackHandler_PreservesTransportSettings(SslProtocols protocol)
    {
        var processor = new FallbackTestProcessor(
            new StubHandler(_ => throw new InvalidOperationException("No request expected")),
            (_, _) => throw new InvalidOperationException("No request expected"));
        using var handler = Assert.IsType<HttpClientHandler>(processor.CreateDefaultFallbackHandler(protocol));

        Assert.Equal(protocol, handler.SslProtocols);
        Assert.False(handler.UseCookies);
        Assert.True(handler.AllowAutoRedirect);
        Assert.Equal(10, handler.MaxAutomaticRedirections);
        Assert.Equal(ClientCertificateOption.Manual, handler.ClientCertificateOptions);
        Assert.False(handler.PreAuthenticate);
        Assert.False(handler.UseDefaultCredentials);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }

    [Fact]
    public async Task StreamTrackToStreamAsync_SslFallbackSuccess_PreservesRangeAndStopsTrying()
    {
        var payload = UniquePlaintext(4, tailBytes: 100);
        var handler = new StubHandler(_ => throw new HttpRequestException("SSL handshake failed"));
        var processor = new FallbackTestProcessor(handler, (protocol, request) =>
        {
            Assert.Equal(PlainUrl, request.RequestUri!.AbsoluteUri);
            Assert.Equal("bytes=2500-", request.Headers.Range?.ToString());
            Assert.Contains("Mozilla/5.0", GetUserAgent(request)!);
            if (protocol == SslProtocols.Tls13)
            {
                throw new HttpRequestException("simulated TLS 1.3 failure");
            }

            return BinaryResponse(HttpStatusCode.PartialContent, payload.AsMemory(2500).ToArray());
        });
        using var output = new MemoryStream();

        await processor.StreamTrackToStreamAsync(
            output,
            new DecryptionStreamProcessor.StreamTrackRequest(
                new Track { Id = "1" }, PlainUrl, new SingleDownloadObject { Bitrate = 320 },
                Listener: null, StartBytes: 2500),
            CancellationToken.None);

        Assert.Equal(new[] { SslProtocols.Tls13, SslProtocols.Tls12 }, processor.AttemptedProtocols);
        Assert.Equal(payload.AsMemory(2500).ToArray(), output.ToArray());
    }

    [Fact]
    public async Task StreamTrackToStreamAsync_NonSslFailure_DoesNotTrySslFallbacks()
    {
        var failure = new HttpRequestException("simulated connection failure");
        var handler = new StubHandler(_ => throw failure);
        var processor = new FallbackTestProcessor(handler, (_, _) => throw new InvalidOperationException("Fallback must not be called"));
        using var output = new MemoryStream();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => processor.StreamTrackToStreamAsync(
            output, new Track { Id = "1" }, PlainUrl, new SingleDownloadObject(), null, CancellationToken.None));

        Assert.Same(failure, exception);
        Assert.Empty(processor.AttemptedProtocols);
    }

    #endregion

    #region StreamTrackAsync failure and retry paths

    [Fact]
    public async Task StreamTrackAsync_CanceledObject_ThrowsOperationCanceled()
    {
        var processor = CreateProcessor(new StubHandler(_ => BinaryResponse(HttpStatusCode.OK, new byte[16])));
        var path = TempPath();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => processor.StreamTrackAsync(path, new Track { Id = "1" }, PlainUrl, new SingleDownloadObject { IsCanceled = true }, null, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StreamTrackAsync_NullUrl_ThrowsArgumentNull()
    {
        var processor = CreateProcessor(new StubHandler(_ => BinaryResponse(HttpStatusCode.OK, new byte[16])));
        var path = TempPath();
        try
        {
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => processor.StreamTrackAsync(path, new Track { Id = "1" }, null, new SingleDownloadObject(), null, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StreamTrackAsync_TransientFailureThenSuccess_WritesFileAndNotifiesRetries()
    {
        var payload = UniquePlaintext(2, tailBytes: 8);
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            // Message-based retryable error (TaskCanceledException derives from
            // OperationCanceledException and is deliberately excluded from the retry path).
            return calls <= 2
                ? throw new HttpRequestException("esockettimedout")
                : BinaryResponse(HttpStatusCode.OK, payload);
        });
        var listener = new RecordingListener();
        var path = TempPath();
        try
        {
            var processor = CreateProcessor(handler);
            await processor.StreamTrackAsync(
                path,
                new Track { Id = "42" },
                PlainUrl,
                new SingleDownloadObject { Bitrate = 320 },
                listener,
                new DecryptionStreamProcessor.StreamTrackRetryPolicy(2, 0, 0),
                CancellationToken.None);

            Assert.Equal(3, calls);
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
            Assert.Contains(listener.InfoMessages, m => m.Contains("Retrying download (1/2)"));
            Assert.Contains(listener.InfoMessages, m => m.Contains("Retrying download (2/2)"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StreamTrackAsync_RetriesExhausted_ThrowsAndDeletesPartialFile()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("esockettimedout"));
        var path = TempPath();
        try
        {
            var processor = CreateProcessor(handler);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => processor.StreamTrackAsync(
                    path,
                    new Track { Id = "43" },
                    PlainUrl,
                    new SingleDownloadObject { Bitrate = 320 },
                    listener: null,
                    new DecryptionStreamProcessor.StreamTrackRetryPolicy(1, 0, 0),
                    CancellationToken.None));

            Assert.Contains("Download failed after 1 retries", exception.Message);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StreamTrackAsync_NonRetryableFailure_ThrowsImmediatelyWithoutRetrying()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            throw new HttpRequestException("simulated permanent failure");
        });
        var path = TempPath();
        try
        {
            var processor = CreateProcessor(handler);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => processor.StreamTrackAsync(
                    path,
                    new Track { Id = "44" },
                    PlainUrl,
                    new SingleDownloadObject { Bitrate = 320 },
                    listener: null,
                    new DecryptionStreamProcessor.StreamTrackRetryPolicy(3, 0, 0),
                    CancellationToken.None));

            Assert.Equal(1, calls);
            Assert.Contains("Failed to download track 44", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion

    #region plumbing

    private static DecryptionStreamProcessor CreateProcessor(HttpMessageHandler handler)
        => new(
            NullLogger<DecryptionStreamProcessor>.Instance,
            new CryptoService(NullLogger<CryptoService>.Instance),
            new StubHttpClientFactory(handler));

    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), "dst-stream-" + Guid.NewGuid().ToString("N") + ".bin");

    private static byte[] Slice(byte[] source, int block)
    {
        var slice = new byte[ChunkSize];
        Array.Copy(source, block * ChunkSize, slice, 0, ChunkSize);
        return slice;
    }

    private static long ParseRangeStart(HttpRequestMessage request)
    {
        var header = request.Headers.Range?.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("bytes="))
        {
            return 0;
        }

        var startPart = header["bytes=".Length..].Split('-')[0];
        return long.TryParse(startPart, out var start) ? start : 0;
    }

    private static string? GetUserAgent(HttpRequestMessage request)
        => request.Headers.TryGetValues("User-Agent", out var values) ? string.Join(" ", values) : null;

    private static HttpResponseMessage BinaryResponse(HttpStatusCode status, byte[] body)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        return response;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return Task.FromResult(responder(request));
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }

    private sealed class FallbackTestProcessor(
        HttpMessageHandler primaryHandler,
        Func<SslProtocols, HttpRequestMessage, HttpResponseMessage> responder)
        : DecryptionStreamProcessor(
            NullLogger<DecryptionStreamProcessor>.Instance,
            new CryptoService(NullLogger<CryptoService>.Instance),
            new StubHttpClientFactory(primaryHandler))
    {
        public List<SslProtocols> AttemptedProtocols { get; } = new();

        public HttpMessageHandler CreateDefaultFallbackHandler(SslProtocols protocol)
            => base.CreateFallbackHandler(protocol);

        protected override HttpMessageHandler CreateFallbackHandler(SslProtocols sslProtocol)
        {
            AttemptedProtocols.Add(sslProtocol);
            return new StubHandler(request => responder(sslProtocol, request));
        }
    }

    private sealed class RecordingListener : IDownloadListener
    {
        public List<string> InfoMessages { get; } = new();

        public void OnProgressUpdate(DownloadObject downloadObject) { }

        public void OnDownloadStart(DownloadObject downloadObject) { }

        public void OnDownloadComplete(DownloadObject downloadObject) { }

        public void OnDownloadError(DownloadObject downloadObject, DownloadError error) { }

        public void OnDownloadInfo(DownloadObject downloadObject, string message, string state)
            => InfoMessages.Add(message);

        public void OnDownloadWarning(DownloadObject downloadObject, string message, string state, string solution) { }

        public void OnCurrentItemCancelled(DownloadObject downloadObject) { }

        public void OnRemovedFromQueue(DownloadObject downloadObject) { }

        public void OnFinishDownload(DownloadObject downloadObject) { }

        public void OnUpdateQueue(DownloadObject downloadObject, bool downloaded = false, bool failed = false, bool alreadyDownloaded = false) { }
    }

    #endregion
}
