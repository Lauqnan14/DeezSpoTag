using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Services.Download.Tidal;
using DeezSpoTag.Services.Download.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TidalStereoQualitySeparationTests
{
    [Fact]
    public void TidalAtmosValidation_FailsClosedWhenFfprobeCannotConfirmAtmos()
    {
        var source = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Tidal/TidalDownloadService.cs")));
        var methodStart = source.IndexOf("private static bool IsAtmosDurationAcceptable", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("private static bool TryReadFfprobeAtmosAudio", methodStart, StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];

        Assert.Contains("if (!TryReadFfprobeAtmosAudio", method, StringComparison.Ordinal);
        Assert.Contains("return false;", method, StringComparison.Ordinal);
        Assert.DoesNotContain("return true;\n        }", method, StringComparison.Ordinal);
    }

    [Fact]
    public void TidalProviderStagesAndDirectAssets_HaveBoundedTransientRetries()
    {
        var source = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Tidal/TidalDownloadService.cs")));

        Assert.Contains("MaxProviderStageAttempts", source, StringComparison.Ordinal);
        Assert.Contains("ExecuteProviderJsonStageWithRetryAsync", source, StringComparison.Ordinal);
        Assert.Contains("FetchProviderTextWithRetryAsync", source, StringComparison.Ordinal);
        Assert.Contains("ResolveProviderRetryDelay", source, StringComparison.Ordinal);
        Assert.Contains("response.Headers.RetryAfter", source, StringComparison.Ordinal);
        Assert.Contains("Tidal audio asset download produced a zero-byte file", source, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("LOW", "LOW")]
    [InlineData("HIGH", "HIGH")]
    [InlineData("LOSSLESS", "LOSSLESS")]
    [InlineData("HI_RES", "HI_RES")]
    [InlineData("HI_RES_LOSSLESS", "HI_RES_LOSSLESS")]
    [InlineData("MAX_HI_RES", "HI_RES_LOSSLESS")]
    [InlineData("ATMOS", "DOLBY_ATMOS")]
    [InlineData("DOLBY_ATMOS", "DOLBY_ATMOS")]
    public void TidalRequestBuilder_PreservesDistinctFallbackTier(string inputQuality, string expectedQueueQuality)
    {
        var item = new TidalQueueItem { Quality = inputQuality };
        var settings = new DeezSpoTagSettings { TidalQuality = "LOSSLESS" };

        var request = TidalRequestBuilder.BuildRequest(item, settings);

        Assert.Equal(expectedQueueQuality, request.Quality);
    }

    [Fact]
    public void TidalRequestBuilder_UsesConfiguredTidalQualityWhenPayloadHasNoQuality()
    {
        var item = new TidalQueueItem();
        var settings = new DeezSpoTagSettings { TidalQuality = "HI_RES" };

        var request = TidalRequestBuilder.BuildRequest(item, settings);

        Assert.Equal("HI_RES", request.Quality);
    }

    [Fact]
    public void TidalDirectDownload_PerformsQualityAwareCatalogResolutionBeforeManifestAcquisition()
    {
        var source = File.ReadAllText(Path.Join(
            FindRepoRoot(),
            "DeezSpoTag.Services",
            "Download",
            "Tidal",
            "TidalDownloadService.cs"));
        var qualityResolution = source.IndexOf(
            "tidalUrl = await ResolveTrackUrlForQualityAsync(",
            StringComparison.Ordinal);
        var manifestAcquisition = source.IndexOf(
            "return await DownloadByUrlAsync(",
            qualityResolution,
            StringComparison.Ordinal);

        Assert.True(qualityResolution >= 0);
        Assert.True(manifestAcquisition > qualityResolution);
        Assert.Contains("if (!IsTidalAtmosRequest(request))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TidalFallbackResolution_UsesTheActiveStepsQuality()
    {
        var source = File.ReadAllText(Path.Join(
            FindRepoRoot(),
            "DeezSpoTag.Services",
            "Download",
            "Fallback",
            "EngineFallbackCoordinator.cs"));

        Assert.Contains(
            "Quality = step.Quality ?? context.ResolutionRequest.Quality",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TidalManifestRouting_UsesAuthenticatedSessionOtherwisePublicProviderRegistry()
    {
        var source = File.ReadAllText(Path.Join(
            FindRepoRoot(),
            "DeezSpoTag.Services",
            "Download",
            "Tidal",
            "TidalDownloadService.cs"));
        var methodStart = source.IndexOf(
            "private async Task<IReadOnlyList<string>> GetDownloadUrlCandidatesAsync(",
            StringComparison.Ordinal);
        var authenticatedBranch = source.IndexOf(
            "if (await _accessTokenProvider.HasAuthenticatedSessionAsync(cancellationToken))",
            methodStart,
            StringComparison.Ordinal);
        var authenticatedFetch = source.IndexOf(
            "FetchManifestFromAuthenticatedApiAsync(",
            authenticatedBranch,
            StringComparison.Ordinal);
        var publicProviderRegistry = source.IndexOf(
            "_providerSource.GetRotatedProviderRecordsAsync(cancellationToken)",
            authenticatedFetch,
            StringComparison.Ordinal);
        var nextMethod = source.IndexOf(
            "private async Task<string> FetchManifestFromAuthenticatedApiAsync(",
            publicProviderRegistry,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(authenticatedBranch > methodStart);
        Assert.True(authenticatedFetch > authenticatedBranch);
        Assert.True(publicProviderRegistry > authenticatedFetch);
        Assert.True(nextMethod > publicProviderRegistry);

        var routingMethod = source[methodStart..nextMethod];
        Assert.DoesNotContain("Zarz", routingMethod, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TryFetchManifestFromCredentialApiAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TidalDashSegments_RetryIndependentlyAndMergeInManifestOrder()
    {
        using var server = new SegmentTestServer();
        server.Start();
        var outputDirectory = Path.Join(Path.GetTempPath(), $"deezspotag-tidal-segments-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Join(outputDirectory, "combined.bin");

        try
        {
            var service = new TidalDownloadService(
                NullLogger<TidalDownloadService>.Instance,
                new TidalApiProviderSource(new EmptyTidalPublicProviderRegistry()),
                new UnauthenticatedTidalAccessTokenProvider(),
                new ZarzSignedSessionCoordinator(NullLogger<ZarzSignedSessionCoordinator>.Instance));
            var method = typeof(TidalDownloadService).GetMethod(
                "DownloadSegmentsAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var task = Assert.IsAssignableFrom<Task>(method.Invoke(
                service,
                [
                    new[]
                    {
                        server.Url("init"),
                        server.Url("media-1"),
                        server.Url("media-2")
                    },
                    outputPath,
                    null,
                    CancellationToken.None
                ]));
            await task;

            Assert.Equal("INIT-ONE-TWO", await File.ReadAllTextAsync(outputPath));
            Assert.Equal(1, server.RequestCount("init"));
            Assert.Equal(2, server.RequestCount("media-1"));
            Assert.Equal(2, server.RequestCount("media-2"));
            Assert.Empty(Directory.GetDirectories(outputDirectory, "*.segments-*"));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task TidalDashSegments_ProduceTaggableAudioWithoutDroppingWrittenMetadata()
    {
        var outputDirectory = Path.Join(Path.GetTempPath(), $"deezspotag-tidal-tags-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var sourcePath = Path.Join(outputDirectory, "source.flac");
        var outputPath = Path.Join(outputDirectory, "downloaded.flac");

        try
        {
            await GenerateTestFlacAsync(sourcePath);
            var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
            var firstBoundary = sourceBytes.Length / 3;
            var secondBoundary = firstBoundary * 2;
            var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["init"] = sourceBytes[..firstBoundary],
                ["media-1"] = sourceBytes[firstBoundary..secondBoundary],
                ["media-2"] = sourceBytes[secondBoundary..]
            };

            using var server = new SegmentTestServer(payloads, simulateFailures: false);
            server.Start();
            await InvokeSegmentDownloadAsync(
                server,
                outputPath,
                ["init", "media-1", "media-2"]);

            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(outputPath));

            using (var tagFile = TagLib.File.Create(outputPath))
            {
                tagFile.Tag.Title = "Segment Download Title";
                tagFile.Tag.Performers = ["Primary Artist", "Featured Artist"];
                tagFile.Tag.AlbumArtists = ["Album Artist"];
                tagFile.Tag.Album = "Segment Download Album";
                tagFile.Tag.Genres = ["Hip-Hop", "R&B"];
                tagFile.Tag.Track = 7;
                tagFile.Tag.TrackCount = 12;
                tagFile.Tag.Disc = 1;
                tagFile.Tag.DiscCount = 2;
                tagFile.Tag.Year = 2026;
                tagFile.Tag.Comment = "Tidal segment metadata verification";
                tagFile.Save();
            }

            using var verified = TagLib.File.Create(outputPath);
            Assert.Equal("Segment Download Title", verified.Tag.Title);
            Assert.Equal(new[] { "Primary Artist", "Featured Artist" }, verified.Tag.Performers);
            Assert.Equal(new[] { "Album Artist" }, verified.Tag.AlbumArtists);
            Assert.Equal("Segment Download Album", verified.Tag.Album);
            Assert.Equal(new[] { "Hip-Hop", "R&B" }, verified.Tag.Genres);
            Assert.Equal(7u, verified.Tag.Track);
            Assert.Equal(12u, verified.Tag.TrackCount);
            Assert.Equal(1u, verified.Tag.Disc);
            Assert.Equal(2u, verified.Tag.DiscCount);
            Assert.Equal(2026u, verified.Tag.Year);
            Assert.Equal("Tidal segment metadata verification", verified.Tag.Comment);
            Assert.True(verified.Properties.Duration > TimeSpan.Zero);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("LOSSLESS", "LOSSLESS")]
    [InlineData("HI_RES", "HI_RES")]
    [InlineData("HI_RES_LOSSLESS", "HI_RES_LOSSLESS")]
    public void TidalApiRequestQuality_PreservesStereoFallbackStep(string inputQuality, string expectedRequestQuality)
    {
        var type = typeof(DeezSpoTag.Services.Download.QualityCatalog).Assembly.GetType(
            "DeezSpoTag.Services.Download.Shared.TidalStereoQuality",
            throwOnError: true)!;
        var method = type.GetMethod(
            "ToTidalRequestQuality",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string)],
            modifiers: null)!;

        var requestQuality = (string)method.Invoke(null, [inputQuality])!;

        Assert.Equal(expectedRequestQuality, requestQuality);
    }

    [Theory]
    [InlineData("HI_RES", "audio/mp4", "mp4a.40.2", 44100, 0)]
    [InlineData("HI_RES", "audio/flac", "flac", 44100, 16)]
    [InlineData("HI_RES_LOSSLESS", "audio/flac", "flac", 96000, 24)]
    [InlineData("LOSSLESS", "audio/flac", "flac", 96000, 24)]
    public void TidalManifestGate_RejectsWrongStereoQualityBeforeDownload(
        string requestedQuality,
        string mimeType,
        string codec,
        int sampleRate,
        int bitDepth)
    {
        var exception = Assert.Throws<TargetInvocationException>(() =>
            EnsureManifestMatchesRequest(
                BuildDashManifestCandidate(mimeType, codec, sampleRate, bitDepth),
                requestedQuality));

        Assert.Contains("Tidal manifest quality mismatch", exception.InnerException?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("LOSSLESS", "audio/flac", "flac", 44100, 16)]
    [InlineData("HI_RES", "audio/flac", "flac", 96000, 24)]
    [InlineData("HI_RES_LOSSLESS", "audio/flac", "flac", 192000, 24)]
    public void TidalManifestGate_AcceptsMatchingStereoQualityBeforeDownload(
        string requestedQuality,
        string mimeType,
        string codec,
        int sampleRate,
        int bitDepth)
    {
        EnsureManifestMatchesRequest(
            BuildDashManifestCandidate(mimeType, codec, sampleRate, bitDepth),
            requestedQuality);
    }

    private static void EnsureManifestMatchesRequest(string candidate, string requestedQuality)
    {
        var method = typeof(TidalDownloadService).GetMethod(
            "EnsureTidalManifestMatchesRequestedQuality",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, [candidate, requestedQuality, false]);
    }

    // Regression coverage for a real production failure: track 328353593 ("Been looking for
    // you" by EMDA), confirmed stereo via the provider's own response (audioMode="STEREO",
    // codecs="flac"), was rejected with "provider returned Tidal Dolby Atmos" purely because
    // its CDN-signed media segment token -- a long, effectively random base64 auth string --
    // happened to contain the substring "joc". IsAtmosManifest scans the raw decoded manifest
    // text for Atmos signals (see TidalManifestGate_DetectsGenuineAtmosManifestViaRepresentationId
    // below for why it has to), but must redact the URL-bearing DASH attributes first so a
    // token's coincidental substring can't trigger a false positive. A stereo manifest whose
    // token happens to contain an Atmos-looking substring must still be accepted for a
    // stereo/Hi-Res request.
    [Fact]
    public void TidalManifestGate_IgnoresAtmosLookingSubstringInsideCdnSignedSegmentToken()
    {
        var manifest = BuildDashManifestCandidateWithSegmentToken(
            "audio/flac", "flac", 96000, 24, "abcJOCdef1234SignedTokenValue");

        EnsureManifestMatchesRequest(manifest, "HI_RES");
    }

    // Regression coverage for the real Atmos manifest structure captured live from the Zarz
    // provider for Tidal track 360943742 ("Espresso" by Sabrina Carpenter, a genuine Dolby Atmos
    // release, captured 2026-08-07): mimeType="audio/mp4" and codecs="ec-3" carry NO Atmos
    // signal at all -- the only indicator anywhere on that manifest is the DASH Representation's
    // id="EAC3_JOC" attribute. An earlier version of this fix (removing RawText scanning
    // entirely to fix the false positive above) would have made IsAtmosManifest return false for
    // every genuine Atmos manifest, since neither MimeType nor Codecs ever carries the signal.
    // IsAtmosManifest must still scan RawText (with URLs redacted) to catch this.
    [Fact]
    public void TidalManifestGate_DetectsGenuineAtmosManifestViaRepresentationId()
    {
        var manifest = BuildAtmosDashManifestCandidate();

        Assert.True(InvokeIsAtmosManifest(manifest));
    }

    // Regression coverage for the resolution-time counterpart to the manifest-gate tests
    // above: Tidal represents an Atmos master as a distinct track ID from its stereo
    // counterpart, but tags it with the same quality-tier tags (e.g. HIRES_LOSSLESS) as its
    // underlying encode. A track whose audioModes is ["DOLBY_ATMOS"] must never be accepted
    // for a stereo/Hi-Res/Lossless request just because its tags look hi-res -- otherwise the
    // Atmos ID gets persisted as the resolved identity, and the mismatch only surfaces later,
    // at download time, once the provider actually returns Atmos content for a stereo ask.
    [Theory]
    [InlineData("DOLBY_ATMOS", "HIRES_LOSSLESS", "HI_RES", false)]
    [InlineData("DOLBY_ATMOS", "HIRES_LOSSLESS", "HI_RES_LOSSLESS", false)]
    [InlineData("DOLBY_ATMOS", "HIRES_LOSSLESS", "LOSSLESS", false)]
    [InlineData("DOLBY_ATMOS", "LOSSLESS", "LOW", false)]
    [InlineData("DOLBY_ATMOS", "HIRES_LOSSLESS", "DOLBY_ATMOS", true)]
    [InlineData("DOLBY_ATMOS", "HIRES_LOSSLESS", "ATMOS", true)]
    [InlineData("STEREO", "HIRES_LOSSLESS", "HI_RES", true)]
    [InlineData("STEREO", "HIRES_LOSSLESS", "DOLBY_ATMOS", false)]
    public void TidalTrackCanSatisfyQuality_NeverAcceptsAnAtmosOnlyTrackForAStereoRequest(
        string audioMode,
        string tag,
        string requestedQuality,
        bool expected)
    {
        var track = BuildTidalTrack(audioMode, tag);
        var method = typeof(TidalDownloadService).GetMethod(
            "TidalTrackCanSatisfyQuality",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (bool)method.Invoke(null, [track, requestedQuality])!;

        Assert.Equal(expected, result);
    }

    [Fact]
    public void AtmosOnlyTidalIdentity_ResolvesStereoCounterpartInsteadOfFailingTheJob()
    {
        var source = File.ReadAllText(Path.Join(
            FindRepoRoot(),
            "DeezSpoTag.Services",
            "Download",
            "Tidal",
            "TidalDownloadService.cs"));
        var fallbackSearch = File.ReadAllText(Path.Join(
            FindRepoRoot(),
            "DeezSpoTag.Services",
            "Download",
            "Fallback",
            "EngineFallbackSearchService.cs"));

        Assert.Contains("TryResolveStereoCounterpartAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetAlbumTracksAsync", source, StringComparison.Ordinal);
        Assert.Contains("IsTidalAtmosOnlyTrack", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "&& !string.Equals(request.Engine, TidalEngine, StringComparison.OrdinalIgnoreCase)",
            fallbackSearch,
            StringComparison.Ordinal);
    }

    private static object BuildTidalTrack(string audioMode, string tag)
    {
        var trackType = typeof(TidalDownloadService).GetNestedType("TidalTrack", BindingFlags.NonPublic)!;
        var json = $$"""
            {
                "id": 328353593,
                "title": "Been looking for you",
                "audioQuality": "LOSSLESS",
                "audioModes": ["{{audioMode}}"],
                "mediaMetadata": { "tags": ["{{tag}}"] }
            }
            """;
        return System.Text.Json.JsonSerializer.Deserialize(json, trackType)!;
    }

    private static string BuildDashManifestCandidate(
        string mimeType,
        string codec,
        int sampleRate,
        int bitDepth)
    {
        var bitDepthAttribute = bitDepth > 0 ? $" bitDepth=\"{bitDepth}\"" : string.Empty;
        var manifest = $"""
            <MPD>
              <Period>
                <AdaptationSet mimeType="{mimeType}" contentType="audio">
                  <Representation bandwidth="1000000" codecs="{codec}" audioSamplingRate="{sampleRate}"{bitDepthAttribute}>
                    <SegmentTemplate initialization="https://media.example/init.mp4" media="https://media.example/segment-$Number$.m4s" startNumber="1">
                      <SegmentTimeline><S d="1" /></SegmentTimeline>
                    </SegmentTemplate>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        return "MANIFEST:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(manifest));
    }

    private static string BuildDashManifestCandidateWithSegmentToken(
        string mimeType,
        string codec,
        int sampleRate,
        int bitDepth,
        string segmentToken)
    {
        var bitDepthAttribute = bitDepth > 0 ? $" bitDepth=\"{bitDepth}\"" : string.Empty;
        var manifest = $"""
            <MPD>
              <Period>
                <AdaptationSet mimeType="{mimeType}" contentType="audio">
                  <Representation bandwidth="1000000" codecs="{codec}" audioSamplingRate="{sampleRate}"{bitDepthAttribute}>
                    <SegmentTemplate initialization="https://media.example/init.mp4?token={segmentToken}" media="https://media.example/segment-$Number$.m4s?token={segmentToken}" startNumber="1">
                      <SegmentTimeline><S d="1" /></SegmentTimeline>
                    </SegmentTemplate>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        return "MANIFEST:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(manifest));
    }

    // Mirrors the structure of the live Zarz Atmos DASH manifest captured for track 360943742:
    // mimeType="audio/mp4", codecs="ec-3", and a Representation id="EAC3_JOC" -- the only
    // Atmos-indicating attribute on the whole document -- alongside CDN-signed init/media URLs
    // carrying an opaque, non-"joc" token (so this test can't accidentally pass for the wrong
    // reason).
    private static string BuildAtmosDashManifestCandidate()
    {
        const string manifest = """
            <?xml version='1.0' encoding='UTF-8'?><MPD xmlns="urn:mpeg:dash:schema:mpd:2011"><Period id="0"><AdaptationSet id="0" contentType="audio" mimeType="audio/mp4" lang="und" group="main" segmentAlignment="true"><Representation id="EAC3_JOC" codecs="ec-3" bandwidth="769208" audioSamplingRate="48000"><SegmentTemplate timescale="48000" initialization="https://sp-ad-fa.audio.tidal.com/mediatracks/abc123.mp4?token=1786133899~L21lZGlhdHJhY2tz" media="https://sp-ad-fa.audio.tidal.com/mediatracks/abc123/$Number$.mp4?token=1786133899~L21lZGlhdHJhY2tz" startNumber="1"><SegmentTimeline><S d="192000" r="42"/><S d="167424"/></SegmentTimeline></SegmentTemplate></Representation></AdaptationSet></Period></MPD>
            """;
        return "MANIFEST:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(manifest));
    }

    private static bool InvokeIsAtmosManifest(string candidate)
    {
        var parseManifest = typeof(TidalDownloadService).GetMethod(
            "ParseManifest",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var manifest = parseManifest.Invoke(null, [candidate]);

        var isAtmosManifest = typeof(TidalDownloadService).GetMethod(
            "IsAtmosManifest",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)isAtmosManifest.Invoke(null, [manifest])!;
    }

    private static string FindRepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (Directory.Exists(Path.Join(directory, "DeezSpoTag.Services"))
                && Directory.Exists(Path.Join(directory, "DeezSpoTag.Tests")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static async Task InvokeSegmentDownloadAsync(
        SegmentTestServer server,
        string outputPath,
        IReadOnlyList<string> paths)
    {
        var service = new TidalDownloadService(
            NullLogger<TidalDownloadService>.Instance,
            new TidalApiProviderSource(new EmptyTidalPublicProviderRegistry()),
            new UnauthenticatedTidalAccessTokenProvider(),
            new ZarzSignedSessionCoordinator(NullLogger<ZarzSignedSessionCoordinator>.Instance));
        var method = typeof(TidalDownloadService).GetMethod(
            "DownloadSegmentsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var urls = paths.Select(server.Url).ToArray();
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(
            service,
            [urls, outputPath, null, CancellationToken.None]));
        await task;
    }

    private static async Task GenerateTestFlacAsync(string outputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("lavfi");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("sine=frequency=440:duration=1");
        startInfo.ArgumentList.Add("-c:a");
        startInfo.ArgumentList.Add("flac");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, error);
        Assert.True(new FileInfo(outputPath).Length > 0);
    }

    private sealed class SegmentTestServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Dictionary<string, int> _requestCounts = new(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, byte[]>? _payloads;
        private readonly bool _simulateFailures;
        private Task? _acceptLoop;

        public SegmentTestServer(
            IReadOnlyDictionary<string, byte[]>? payloads = null,
            bool simulateFailures = true)
        {
            _payloads = payloads;
            _simulateFailures = simulateFailures;
        }

        public void Start()
        {
            _listener.Start();
            _acceptLoop = AcceptLoopAsync(_shutdown.Token);
        }

        public string Url(string path)
            => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{path}";

        public int RequestCount(string path)
        {
            lock (_requestCounts)
            {
                return _requestCounts.GetValueOrDefault(path);
            }
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                _ = HandleAsync(client, cancellationToken);
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(
                    stream,
                    Encoding.ASCII,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024,
                    leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(cancellationToken);
                string? line;
                do
                {
                    line = await reader.ReadLineAsync(cancellationToken);
                }
                while (!string.IsNullOrEmpty(line));

                var path = requestLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .ElementAtOrDefault(1)?
                    .TrimStart('/') ?? string.Empty;
                int count;
                lock (_requestCounts)
                {
                    count = _requestCounts.GetValueOrDefault(path) + 1;
                    _requestCounts[path] = count;
                }

                if (_simulateFailures && path == "media-1" && count == 1)
                {
                    await WriteResponseAsync(stream, "503 Service Unavailable", [], "Retry-After: 0\r\n", cancellationToken);
                    return;
                }

                if (_simulateFailures && path == "media-2" && count == 1)
                {
                    await WriteResponseAsync(stream, "200 OK", [], string.Empty, cancellationToken);
                    return;
                }

                var payload = _payloads?.GetValueOrDefault(path)
                    ?? Encoding.UTF8.GetBytes(path switch
                    {
                        "init" => "INIT-",
                        "media-1" => "ONE-",
                        "media-2" => "TWO",
                        _ => "UNKNOWN"
                    });
                await WriteResponseAsync(stream, "200 OK", payload, string.Empty, cancellationToken);
            }
        }

        private static async Task WriteResponseAsync(
            Stream stream,
            string status,
            byte[] payload,
            string extraHeaders,
            CancellationToken cancellationToken)
        {
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n{extraHeaders}\r\n");
            await stream.WriteAsync(headers, cancellationToken);
            if (payload.Length > 0)
            {
                await stream.WriteAsync(payload, cancellationToken);
            }

            await stream.FlushAsync(cancellationToken);
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            _listener.Stop();
            _shutdown.Dispose();
        }
    }

    private sealed class EmptyTidalPublicProviderRegistry : ITidalPublicProviderRegistry
    {
        public Task<IReadOnlyList<TidalPublicProvider>> GetProvidersAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<TidalPublicProvider>>([]);

        public Task<IReadOnlyList<TidalPublicProvider>> CheckEnabledProvidersAsync(CancellationToken cancellationToken)
            => GetProvidersAsync(cancellationToken);

        public Task<TidalPublicProvider?> SetEnabledAsync(string providerId, bool enabled, CancellationToken cancellationToken)
            => Task.FromResult<TidalPublicProvider?>(null);

        public Task RecordSuccessAsync(string endpoint, long responseTimeMs, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RecordFailureAsync(string endpoint, string category, long responseTimeMs, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RecordFailureAsync(
            string endpoint,
            string category,
            long responseTimeMs,
            DateTimeOffset? cooldownUntil,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class UnauthenticatedTidalAccessTokenProvider : ITidalAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);

        public Task<string> GetCountryCodeAsync(CancellationToken cancellationToken)
            => Task.FromResult("US");

        public Task<bool> HasAuthenticatedSessionAsync(CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<bool> ValidateCredentialsAsync(CancellationToken cancellationToken)
            => Task.FromResult(false);

        public void Invalidate()
        {
        }
    }
}
