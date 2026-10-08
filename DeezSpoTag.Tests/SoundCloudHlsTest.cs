using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.SoundCloud;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     SoundCloud HLS playlist parsing, AES-128 decryption, and the atomic transfer.
/// </summary>
/// <remarks>
///     These exercise the real crypto rather than a stub of it: the AES cases encrypt with a known key and
///     decrypt back through the production path, so a wrong key size, a wrong IV derivation, or a padding
///     mistake fails here instead of producing a silent file of noise.
/// </remarks>
public sealed class SoundCloudHlsTest
{
    private const string PlaylistUrl = "https://media.sndcdn.com/hls/playlist.m3u8";

    /// <summary>A fixed 16-byte AES-128 key, used for every case that needs one.</summary>
    private static readonly byte[] Key =
    [
        0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
        0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF
    ];

    [Fact]
    public void Parse_ReadsAMediaPlaylistAndResolvesRelativeUris()
    {
        var playlist = SoundCloudHlsPlaylistParser.Parse(
            PlaylistUrl,
            SoundCloudFixtures.PlainPlaylist);

        Assert.Equal(3, playlist.Segments.Count);
        Assert.Equal(0L, playlist.MediaSequence);
        Assert.All(playlist.Segments, segment => Assert.Null(segment.Key));
        Assert.All(
            playlist.Segments,
            segment => Assert.StartsWith("https://media.sndcdn.com/hls/", segment.Uri, StringComparison.Ordinal));

        // Absolute URIs are left alone rather than being rebuilt against the base.
        var absolute = SoundCloudHlsPlaylistParser.Parse(
            PlaylistUrl,
            "#EXTM3U\n#EXTINF:10.0,\nhttps://other.example.com/seg.aac\n#EXT-X-ENDLIST\n");
        Assert.Equal("https://other.example.com/seg.aac", Assert.Single(absolute.Segments).Uri);
    }

    [Fact]
    public void Parse_ReadsKeyRotationPlaintextReversionAndTheSequence()
    {
        var playlist = SoundCloudHlsPlaylistParser.Parse(PlaylistUrl, SoundCloudFixtures.EncryptedPlaylist);

        Assert.Equal(0L, playlist.MediaSequence);
        Assert.Equal(6, playlist.Segments.Count);

        // Key URIs are relative in the playlist and are resolved against the playlist's own URL, so what the
        // downloader sees is absolute.
        const string key0 = "https://media.sndcdn.com/hls/keys/key-0.bin";
        const string key1 = "https://media.sndcdn.com/hls/keys/key-1.bin";

        // The playlist-level key and its IV stay in force until the next #EXT-X-KEY, so segments 0 and 1 both
        // carry key-0 and its explicit IV. Segment 2 begins the rotated key, which declares no IV of its own
        // and therefore falls back to the sequence number.
        Assert.Equal(key0, playlist.Segments[0].Key!.Uri);
        Assert.Equal("0x000102030405060708090a0b0c0d0e0f", playlist.Segments[0].Key!.Iv);
        Assert.Equal(key0, playlist.Segments[1].Key!.Uri);
        Assert.Equal("0x000102030405060708090a0b0c0d0e0f", playlist.Segments[1].Key!.Iv);
        Assert.Equal(key1, playlist.Segments[2].Key!.Uri);
        Assert.Equal(string.Empty, playlist.Segments[2].Key!.Iv);
        Assert.Equal(key1, playlist.Segments[3].Key!.Uri);

        // METHOD=NONE reverts the remaining segments to plaintext.
        Assert.Null(playlist.Segments[4].Key);
        Assert.Null(playlist.Segments[5].Key);

        // A segment-level key overrides the playlist-level one for the segments it precedes.
        var withSegmentKey = SoundCloudHlsPlaylistParser.Parse(
            PlaylistUrl,
            "#EXTM3U\n"
            + "#EXT-X-MEDIA-SEQUENCE:5\n"
            + "#EXT-X-KEY:METHOD=AES-128,URI=\"playlist.bin\"\n"
            + "#EXTINF:10.0,\nseg-0\n"
            + "#EXT-X-KEY:METHOD=AES-128,URI=\"segment.bin\"\n"
            + "#EXTINF:10.0,\nseg-1\n"
            + "#EXT-X-ENDLIST\n");

        Assert.Equal(5L, withSegmentKey.MediaSequence);
        Assert.Equal("https://media.sndcdn.com/hls/playlist.bin", withSegmentKey.Segments[0].Key!.Uri);
        Assert.Equal("https://media.sndcdn.com/hls/segment.bin", withSegmentKey.Segments[1].Key!.Uri);
    }

    [Fact]
    public void Parse_RejectsAPlaylistItCannotAssemble()
    {
        // A master playlist means the wrong URL was fetched; silently choosing a variant would download
        // audio nobody asked for.
        var master = Assert.Throws<SoundCloudNoStreamException>(
            () => SoundCloudHlsPlaylistParser.Parse(
                PlaylistUrl,
                "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=320000\nvariant.m3u8\n"));
        Assert.Contains("master playlist", master.Message, StringComparison.OrdinalIgnoreCase);

        // Only AES-128 is supported. Anything else is refused rather than passed through undecrypted.
        var unsupportedMethod = Assert.Throws<SoundCloudNoStreamException>(
            () => SoundCloudHlsPlaylistParser.Parse(
                PlaylistUrl,
                "#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"k.bin\"\n#EXTINF:10.0,\ns\n"));
        Assert.Contains("SAMPLE-AES", unsupportedMethod.Message, StringComparison.Ordinal);

        Assert.Throws<SoundCloudNoStreamException>(
            () => SoundCloudHlsPlaylistParser.Parse(PlaylistUrl, "#EXTM3U\n#EXT-X-ENDLIST\n"));

        // A relative segment URI cannot be resolved without an absolute playlist URL to resolve it against.
        Assert.Throws<SoundCloudNoStreamException>(
            () => SoundCloudHlsPlaylistParser.Parse("not a url", "#EXTM3U\n#EXTINF:1.0,\ns\n"));

        // A segment with no #EXTINF header has no declared duration and is not something to guess at.
        Assert.Throws<SoundCloudNoStreamException>(
            () => SoundCloudHlsPlaylistParser.Parse(PlaylistUrl, "#EXTM3U\nsegment-without-extinf.aac\n"));
    }

    [Fact]
    public void Decrypt_RoundTripsWithAnExplicitIv()
    {
        var plaintext = Encoding.ASCII.GetBytes("SoundCloud segment payload for decryption.");
        var iv = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

        var decrypted = SoundCloudHlsDecryptor.Decrypt(Encrypt(plaintext, iv), Key, 0, "0x000102030405060708090a0b0c0d0e0f");

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_UsesTheMediaSequenceNumberWhenNoIvIsDeclared()
    {
        var plaintext = Encoding.ASCII.GetBytes("sequence-derived iv");

        // The IV is the absolute sequence number as a 16-byte big-endian value, which is what HLS specifies
        // for a segment that declares no IV.
        foreach (var sequence in new long[] { 0, 1, 7, 258 })
        {
            var iv = SequenceIv(sequence);
            var decrypted = SoundCloudHlsDecryptor.Decrypt(Encrypt(plaintext, iv), Key, sequence, null);
            Assert.Equal(plaintext, decrypted);
        }

        // Using the wrong sequence number yields a wrong IV and therefore a wrong plaintext.
        var wrongIv = SequenceIv(99);
        Assert.NotEqual(
            plaintext,
            SoundCloudHlsDecryptor.Decrypt(Encrypt(plaintext, wrongIv), Key, 5, null));
    }

    [Fact]
    public void Decrypt_RejectsAnInvalidKeyIvOrCiphertext()
    {
        var plaintext = Encoding.ASCII.GetBytes("payload");
        var iv = SequenceIv(0);
        var ciphertext = Encrypt(plaintext, iv);

        Assert.Throws<InvalidDataException>(
            () => SoundCloudHlsDecryptor.Decrypt(ciphertext, new byte[15], 0, null));
        Assert.Throws<InvalidDataException>(
            () => SoundCloudHlsDecryptor.Decrypt(ciphertext, new byte[32], 0, null));

        // Not a whole number of AES blocks.
        Assert.Throws<InvalidDataException>(
            () => SoundCloudHlsDecryptor.Decrypt(ciphertext[..(ciphertext.Length - 1)], Key, 0, null));

        // An IV that is not sixteen bytes of hex.
        Assert.Throws<InvalidDataException>(
            () => SoundCloudHlsDecryptor.Decrypt(ciphertext, Key, 0, "0xABCD"));
        Assert.Throws<InvalidDataException>(
            () => SoundCloudHlsDecryptor.Decrypt(ciphertext, Key, 0, "nothexatall0000"));
    }

    [Fact]
    public void Decrypt_RejectsInvalidPaddingRatherThanReturningNoise()
    {
        // A block that decrypts cleanly but whose trailing byte is not a valid PKCS#7 length must be
        // refused. Returning it would produce a file that passes a size check and plays as silence.
        var iv = SequenceIv(0);
        var block = new byte[32];

        using var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using (var encryptor = aes.CreateEncryptor())
        {
            encryptor.TransformBlock(block, 0, block.Length, block, 0);
        }

        // Force a padding byte that cannot be valid.
        block[^1] = 0xFF;

        var failure = Assert.Throws<InvalidDataException>(
            () => SoundCloudHlsDecryptor.Decrypt(block, Key, 0, null));
        Assert.Contains("padding", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Download_AssemblesSegmentsInOrderAndLeavesNoTemporaryFiles()
    {
        var bodies = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["https://media.sndcdn.com/hls/segment-0.aac"] = Encoding.ASCII.GetBytes("FIRST-"),
            ["https://media.sndcdn.com/hls/segment-1.aac"] = Encoding.ASCII.GetBytes("SECOND-"),
            ["https://media.sndcdn.com/hls/segment-2.aac"] = Encoding.ASCII.GetBytes("THIRD")
        };

        var requested = new List<string>();
        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            requested.Add(url);

            if (url == PlaylistUrl)
            {
                return Text(SoundCloudFixtures.PlainPlaylist);
            }

            return bodies.TryGetValue(url, out var body)
                ? Bytes(body)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var directory = new TempDirectory();
        var output = Path.Combine(directory.Path, "track.mp3");

        var downloader = BuildDownloader(handler);
        var progress = new List<double>();

        var result = await downloader.DownloadAsync(
            PlaylistUrl,
            output,
            (percent, _) =>
            {
                lock (progress)
                {
                    progress.Add(percent);
                }

                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(output, result.LocalPath);
        Assert.Equal(3, result.SegmentCount);
        Assert.True(result.Size > 0);
        Assert.Equal("FIRST-SECOND-THIRD", File.ReadAllText(output));

        // Progress was reported and reached the end.
        Assert.NotEmpty(progress);
        Assert.Equal(100d, progress.Max());

        // Nothing left behind: no partial file and no segment directory.
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial"));
        Assert.Empty(Directory.GetDirectories(directory.Path, "*.segments-*"));
    }

    [Fact]
    public async Task Download_DecryptsEverySegmentBeforeAssembling()
    {
        // Six segments so the rotated-key and METHOD=NONE parts of the encrypted fixture are covered:
        // segments 0-1 under key-0, 2-3 under the rotated key-1, and 4-5 in the clear again.
        var plaintext = new[] { "alpha-", "bravo-", "charlie-", "delta-", "echo-", "foxtrot" };
        var bodies = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        // The fixture declares an explicit IV for segments 0-1 and rotates to a key with no IV for 2-3, so the
        // encryption here has to match that exactly: the first two use the declared IV, the next two fall back
        // to the sequence number.
        var explicitIv = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

        for (var index = 0; index < plaintext.Length; index++)
        {
            var iv = index < 2 ? explicitIv : SequenceIv((long)index);
            bodies[$"https://media.sndcdn.com/hls/segment-{index}.aac"] = index < 4
                ? Encrypt(Encoding.ASCII.GetBytes(plaintext[index]), iv)
                : Encoding.ASCII.GetBytes(plaintext[index]);
        }

        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == PlaylistUrl)
            {
                return Text(SoundCloudFixtures.EncryptedPlaylist);
            }

            if (url.Contains("/keys/", StringComparison.Ordinal))
            {
                return Bytes(Key);
            }

            return bodies.TryGetValue(url, out var body)
                ? Bytes(body)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var directory = new TempDirectory();
        var output = Path.Combine(directory.Path, "track.mp3");

        var downloader = BuildDownloader(handler);
        var result = await downloader.DownloadAsync(PlaylistUrl, output, null, CancellationToken.None);

        Assert.Equal(6, result.SegmentCount);
        Assert.Equal(
            "alpha-bravo-charlie-delta-echo-foxtrot",
            File.ReadAllText(output));
    }

    [Fact]
    public async Task Download_LeavesNothingAtTheDestinationWhenASegmentFails()
    {
        // The destination already holds a file. A failed transfer must leave it exactly as it was rather
        // than truncating it, because the queue may already have a good download at that path.
        using var directory = new TempDirectory();
        var output = Path.Combine(directory.Path, "track.mp3");
        const string original = "an existing good download";
        await File.WriteAllTextAsync(output, original);

        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == PlaylistUrl)
            {
                return Text(SoundCloudFixtures.PlainPlaylist);
            }

            return url.EndsWith("segment-1.aac", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Bytes(Encoding.ASCII.GetBytes("content"));
        });

        var downloader = BuildDownloader(handler, segmentAttempts: 1);

        await Assert.ThrowsAnyAsync<Exception>(
            () => downloader.DownloadAsync(PlaylistUrl, output, null, CancellationToken.None));

        Assert.Equal(original, File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial"));
        Assert.Empty(Directory.GetDirectories(directory.Path, "*.segments-*"));
    }

    [Fact]
    public async Task Download_RejectsAnEmptySegmentAndNeverWritesAZeroByteFile()
    {
        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == PlaylistUrl)
            {
                return Text(SoundCloudFixtures.PlainPlaylist);
            }

            return url.EndsWith("segment-2.aac", StringComparison.Ordinal)
                ? Bytes([])
                : Bytes(Encoding.ASCII.GetBytes("content"));
        });

        using var directory = new TempDirectory();
        var output = Path.Combine(directory.Path, "track.mp3");

        var downloader = BuildDownloader(handler, segmentAttempts: 1);

        await Assert.ThrowsAnyAsync<Exception>(
            () => downloader.DownloadAsync(PlaylistUrl, output, null, CancellationToken.None));

        Assert.False(File.Exists(output), "A failed transfer must not leave a zero-byte file at the destination.");
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial"));
        Assert.Empty(Directory.GetDirectories(directory.Path, "*.segments-*"));
    }

    [Fact]
    public async Task Download_CancellationLeavesNothingBehind()
    {
        using var cancellation = new CancellationTokenSource();
        using var directory = new TempDirectory();
        var output = Path.Combine(directory.Path, "track.mp3");

        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == PlaylistUrl)
            {
                return Text(SoundCloudFixtures.PlainPlaylist);
            }

            // Cancel as soon as the first segment is requested, so the transfer is interrupted mid-flight.
            cancellation.Cancel();
            return Bytes(Encoding.ASCII.GetBytes("content"));
        });

        var downloader = BuildDownloader(handler, segmentAttempts: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => downloader.DownloadAsync(PlaylistUrl, output, null, cancellation.Token));

        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial"));
        Assert.Empty(Directory.GetDirectories(directory.Path, "*.segments-*"));
    }

    [Fact]
    public async Task Download_ReportsAnUnusablePlaylistWithoutWritingAnything()
    {
        var handler = new StubHandler(_ => Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nv.m3u8\n"));

        using var directory = new TempDirectory();
        var output = Path.Combine(directory.Path, "track.mp3");

        var downloader = BuildDownloader(handler);

        await Assert.ThrowsAsync<SoundCloudNoStreamException>(
            () => downloader.DownloadAsync(PlaylistUrl, output, null, CancellationToken.None));

        Assert.False(File.Exists(output));
    }

    /// <summary>
    ///     Encrypts with PKCS#7 padding so the production decryptor's padding validation is exercised.
    /// </summary>
    private static byte[] Encrypt(byte[] plaintext, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    private static byte[] SequenceIv(long sequenceNumber)
    {
        var iv = new byte[16];
        var value = sequenceNumber;
        for (var index = 15; index >= 0 && value > 0; index--)
        {
            iv[index] = (byte)(value & 0xFF);
            value >>= 8;
        }

        return iv;
    }

    private static HttpResponseMessage Text(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Bytes(byte[] body)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static SoundCloudHlsDownloader BuildDownloader(HttpMessageHandler handler, int segmentAttempts = 4)
        => new(new StubClientFactory(handler), segmentAttempts);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }

    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ds-soundcloud-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}