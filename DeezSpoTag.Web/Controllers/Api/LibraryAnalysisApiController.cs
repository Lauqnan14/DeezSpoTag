using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/analysis")]
[ApiController]
[Authorize]
public class LibraryAnalysisApiController : ControllerBase
{
    private sealed record AudioVariantResolutionResult(
        AlbumTrackAudioInfoDto? Variant,
        bool RequestedPathMismatch);

    private readonly LibraryRepository _repository;
    private readonly TrackAnalysisBackgroundService _analysisService;
    private readonly AudioQualitySignalAnalyzer _signalAnalyzer;
    private readonly SpectrogramService _spectrogramService;
    private readonly ILogger<LibraryAnalysisApiController> _logger;
    private static readonly Lazy<string?> FfmpegPath = new(ResolveFfmpegPath);
    private static readonly Lazy<string?> FfprobePath = new(ResolveFfprobePath);

    public LibraryAnalysisApiController(
        LibraryRepository repository,
        TrackAnalysisBackgroundService analysisService,
        AudioQualitySignalAnalyzer signalAnalyzer,
        SpectrogramService spectrogramService,
        ILogger<LibraryAnalysisApiController> logger)
    {
        _repository = repository;
        _analysisService = analysisService;
        _signalAnalyzer = signalAnalyzer;
        _spectrogramService = spectrogramService;
        _logger = logger;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        var status = await _repository.GetAnalysisStatusAsync(cancellationToken);
        return Ok(status);
    }

    [HttpGet("latest")]
    public async Task<IActionResult> GetLatest(CancellationToken cancellationToken)
    {
        var latest = await _repository.GetLatestTrackAnalysisAsync(cancellationToken);
        if (latest is null)
        {
            return NotFound();
        }
        return Ok(latest);
    }

    [HttpGet("current")]
    public Task<IActionResult> GetCurrent(CancellationToken cancellationToken)
    {
        return GetCurrentProcessingResultAsync(cancellationToken);
    }

    [HttpGet("processing")]
    public Task<IActionResult> GetProcessing(CancellationToken cancellationToken)
    {
        return GetCurrentProcessingResultAsync(cancellationToken);
    }

    private async Task<IActionResult> GetCurrentProcessingResultAsync(CancellationToken cancellationToken)
    {
        var processing = await _repository.GetProcessingTrackAsync(cancellationToken);
        if (processing is null)
        {
            return NotFound();
        }
        return Ok(processing);
    }

    [HttpPost("run")]
    public IActionResult Run([FromQuery] int batchSize = 100, CancellationToken cancellationToken = default)
    {
        batchSize = Math.Clamp(batchSize, 10, 500);
        _ = Task.Run(
            () => _analysisService.AnalyzeNowAsync(batchSize, CancellationToken.None, forceWhenDisabled: true),
            cancellationToken);
        return Ok(new { queued = batchSize, fullScan = true });
    }

    [HttpPost("reset")]
    public async Task<IActionResult> Reset(CancellationToken cancellationToken)
    {
        await _repository.ResetAllAnalysisAsync(cancellationToken);
        return Ok(new { reset = true });
    }

    [HttpGet("track/{trackId:long}")]
    public async Task<IActionResult> GetTrack(long trackId, CancellationToken cancellationToken)
    {
        var analysis = await _repository.GetTrackAnalysisAsync(trackId, cancellationToken);
        if (analysis is null)
        {
            return NotFound();
        }
        return Ok(analysis);
    }

    [HttpGet("track/{trackId:long}/spectral")]
    public async Task<IActionResult> GetSpectralAnalysis(long trackId, CancellationToken cancellationToken)
    {
        var filePath = await _repository.GetTrackPrimaryFilePathAsync(trackId, cancellationToken);
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
        {
            return NotFound();
        }

        var analysis = _signalAnalyzer.Analyze(filePath, null, null, null);
        if (analysis is null)
        {
            return StatusCode(StatusCodes.Status422UnprocessableEntity, new
            {
                message = "Unable to analyze audio signal for this track."
            });
        }

        return Ok(new
        {
            trackId,
            filePath,
            analysis.Codec,
            analysis.SampleRateHz,
            analysis.StatedBitrateKbps,
            analysis.MaxFrequencyHz,
            analysis.NyquistFrequencyHz,
            analysis.PeakFrequencyRatio,
            analysis.EquivalentBitrateKbps,
            analysis.IsTrueLossless,
            analysis.IsLosslessCodecContainer
        });
    }

    [HttpGet("track/{trackId:long}/summary")]
    public async Task<IActionResult> GetTrackSummary(
        long trackId,
        [FromQuery] string? filePath,
        CancellationToken cancellationToken)
    {
        var info = await _repository.GetTrackAudioInfoAsync(trackId, cancellationToken);
        if (info is null)
        {
            return NotFound();
        }

        var resolvedPath = await ResolveTrackFilePathAsync(trackId, filePath, preferBrowserPlayable: false, cancellationToken)
            ?? info.FilePath;
        if (string.IsNullOrWhiteSpace(resolvedPath) || !System.IO.File.Exists(resolvedPath))
        {
            return NotFound();
        }

        var state = CreateTrackSummaryState(info);
        TryReadTagAudioProperties(resolvedPath, trackId, state);
        await PopulateTrackSummaryStateFromProbeAsync(resolvedPath, state, cancellationToken);
        ApplyFragmentedMp4DurationCorrection(resolvedPath, state);

        var fileInfo = new FileInfo(resolvedPath);
        var extension = Path.GetExtension(resolvedPath)?.TrimStart('.').ToUpperInvariant();
        var signal = _signalAnalyzer.Analyze(resolvedPath, null, state.BitrateKbps, state.SampleRateHz);
        ApplySignalMetadataFallbacks(state, signal);

        var analysis = await _repository.GetTrackAnalysisAsync(trackId, cancellationToken);
        var maxAnalysisSeconds = ComputeMaxAnalysisSeconds(state.DurationSeconds);
        var pcmStats = await AnalyzePcmStatsAsync(resolvedPath, state.SampleRateHz ?? 44100, maxAnalysisSeconds, cancellationToken);
        var summaryLevels = ComputeSummaryLevels(pcmStats, analysis?.Loudness, analysis?.DynamicRange);
        var (sampleCount, durationSeconds) = ResolveSampleCountAndDuration(pcmStats?.SampleCount, state.SampleRateHz, state.DurationSeconds);
        var nyquistHz = ResolveNyquistHz(state.SampleRateHz, signal);
        var spectrogramSeconds = ComputeSpectrogramSeconds(durationSeconds);

        return Ok(new
        {
            trackId = info.TrackId,
            title = info.Title,
            artist = info.ArtistName,
            album = info.AlbumTitle,
            filePath = resolvedPath,
            fileSize = fileInfo.Exists ? fileInfo.Length : 0L,
            extension,
            sampleRateHz = state.SampleRateHz,
            channels = state.Channels,
            bitsPerSample = state.BitsPerSample,
            bitrateKbps = state.BitrateKbps,
            durationSeconds,
            totalSamples = sampleCount,
            nyquistHz,
            peakAmplitudeDb = summaryLevels.PeakAmplitudeDb,
            rmsLevelDb = summaryLevels.RmsDb,
            dynamicRangeDb = summaryLevels.DynamicRangeDb,
            spectralPeakFrequencyHz = signal?.MaxFrequencyHz,
            equivalentBitrateKbps = signal?.EquivalentBitrateKbps,
            isTrueLossless = signal?.IsTrueLossless,
            spectrogramSeconds,
            spectrogramWidth = 1600,
            spectrogramHeight = 720,
            analysisWarning = pcmStats?.Error
        });
    }

    private static TrackSummaryState CreateTrackSummaryState(TrackAudioInfoDto info)
    {
        return new TrackSummaryState
        {
            DurationSeconds = info.DurationMs.HasValue && info.DurationMs.Value > 0
                ? info.DurationMs.Value / 1000d
                : null
        };
    }

    private async Task PopulateTrackSummaryStateFromProbeAsync(
        string resolvedPath,
        TrackSummaryState state,
        CancellationToken cancellationToken)
    {
        var preferProbeCodec = ShouldPreferFfprobeMetadata(state.CodecDescription);
        if (!NeedsProbeMetadata(state, preferProbeCodec))
        {
            return;
        }

        var probe = await ProbeAudioInfoAsync(resolvedPath, cancellationToken);
        if (probe is null)
        {
            return;
        }

        ApplyProbeMetadata(state, probe, preferProbeCodec);
    }

    private static bool NeedsProbeMetadata(TrackSummaryState state, bool preferProbeCodec)
    {
        return preferProbeCodec
            || state.DurationSeconds is null or <= 0
            || state.Channels is null or <= 0
            || state.SampleRateHz is null or <= 0;
    }

    private static void ApplyProbeMetadata(TrackSummaryState state, FfprobeAudioInfo probe, bool preferProbeCodec)
    {
        if (probe.SampleRateHz is > 0 && state.SampleRateHz is null or <= 0)
        {
            state.SampleRateHz = probe.SampleRateHz.Value;
        }

        if (probe.Channels is > 0 && (preferProbeCodec || state.Channels is null or <= 0))
        {
            state.Channels = probe.Channels.Value;
        }

        if (probe.BitrateKbps is > 0 && state.BitrateKbps is null or <= 0)
        {
            state.BitrateKbps = probe.BitrateKbps.Value;
        }

        if (probe.DurationSeconds is > 0 && (preferProbeCodec || state.DurationSeconds is null or <= 0))
        {
            state.DurationSeconds = probe.DurationSeconds.Value;
        }
    }

    private static void ApplyFragmentedMp4DurationCorrection(string filePath, TrackSummaryState state)
    {
        if (!IsFragmentedMp4Candidate(filePath, state.DurationSeconds, state.BitrateKbps))
        {
            return;
        }

        var fmp4 = FragmentedMp4DurationReader.TryRead(filePath);
        if (fmp4 is null || fmp4.DurationSeconds <= (state.DurationSeconds ?? 0))
        {
            return;
        }

        state.DurationSeconds = fmp4.DurationSeconds;
        if (fmp4.SampleRateHz > 0 && state.SampleRateHz is null or <= 0)
        {
            state.SampleRateHz = fmp4.SampleRateHz;
        }
    }

    private static void ApplySignalMetadataFallbacks(TrackSummaryState state, SignalQualityAnalysis? signal)
    {
        if (state.SampleRateHz is null or <= 0 && signal is { SampleRateHz: > 0 })
        {
            state.SampleRateHz = signal.SampleRateHz;
        }

        if (state.BitrateKbps is null or <= 0 && signal?.StatedBitrateKbps is int bitrate and > 0)
        {
            state.BitrateKbps = bitrate;
        }
    }

    private static int ComputeMaxAnalysisSeconds(double? durationSeconds)
    {
        return durationSeconds is > 0
            ? Math.Clamp((int)Math.Ceiling(durationSeconds.Value), 15, 600)
            : 240;
    }

    private static SummaryLevels ComputeSummaryLevels(
        PcmStatsResult? pcmStats,
        double? analysisLoudness,
        double? analysisDynamicRange)
    {
        var rmsDb = pcmStats?.RmsDb ?? analysisLoudness;
        var dynamicRangeDb = pcmStats?.DynamicRangeDb ?? analysisDynamicRange;
        var peakAmplitudeDb = pcmStats?.PeakDb;
        if (!peakAmplitudeDb.HasValue && rmsDb.HasValue && dynamicRangeDb.HasValue)
        {
            peakAmplitudeDb = rmsDb.Value + dynamicRangeDb.Value;
        }

        return new SummaryLevels(peakAmplitudeDb, rmsDb, dynamicRangeDb);
    }

    private static (long? SampleCount, double? DurationSeconds) ResolveSampleCountAndDuration(
        long? pcmSampleCount,
        int? sampleRateHz,
        double? durationSeconds)
    {
        long? sampleCount = pcmSampleCount is > 0 ? pcmSampleCount : null;
        if (!sampleCount.HasValue && sampleRateHz.HasValue && durationSeconds.HasValue)
        {
            sampleCount = (long?)Math.Round(durationSeconds.Value * sampleRateHz.Value);
        }

        if (durationSeconds is not > 0
            && sampleCount.HasValue
            && sampleRateHz is > 0)
        {
            durationSeconds = sampleCount.Value / (double)sampleRateHz.Value;
        }

        return (sampleCount, durationSeconds);
    }

    private static double? ResolveNyquistHz(int? sampleRateHz, SignalQualityAnalysis? signal)
    {
        if (sampleRateHz.HasValue)
        {
            return sampleRateHz.Value / 2d;
        }

        return signal?.NyquistFrequencyHz is > 0
            ? signal.NyquistFrequencyHz
            : null;
    }

    private static int ComputeSpectrogramSeconds(double? durationSeconds)
    {
        return durationSeconds is > 0
            ? Math.Clamp((int)Math.Ceiling(durationSeconds.Value), 10, 600)
            : 120;
    }

    [HttpGet("track/{trackId:long}/spectrogram")]
    public async Task<IActionResult> GetSpectrogram(
        long trackId,
        [FromQuery] int? width,
        [FromQuery] int? height,
        [FromQuery] int? seconds,
        [FromQuery] string? filePath,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = await ResolveTrackFilePathAsync(trackId, filePath, preferBrowserPlayable: false, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolvedPath) || !System.IO.File.Exists(resolvedPath))
        {
            return NotFound();
        }

        var request = SpectrogramService.NormalizeRequest(width, height, seconds);
        var result = await _spectrogramService.GetOrCreateAsync(resolvedPath, request, force, cancellationToken);
        if (result is null || !result.Success || string.IsNullOrWhiteSpace(result.FilePath) || !System.IO.File.Exists(result.FilePath))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = result?.ErrorMessage ?? "Spectrogram generation failed."
            });
        }

        Response.Headers.CacheControl = "public,max-age=86400";
        return PhysicalFile(result.FilePath, "image/png");
    }

    [HttpGet("track/{trackId:long}/audio")]
    public async Task<IActionResult> GetTrackAudio(
        long trackId,
        [FromQuery] long? audioFileId,
        [FromQuery] string? filePath,
        CancellationToken cancellationToken)
    {
        var variants = await _repository.GetTrackAudioVariantsAsync(trackId, cancellationToken);
        var playbackRoots = await ResolvePlaybackRootCandidatesAsync(cancellationToken);
        var resolution = ResolveTrackAudioVariant(audioFileId, filePath, variants, playbackRoots);
        if (resolution.RequestedPathMismatch)
        {
            _logger.LogWarning(
                "Local track playback request mismatch for track {TrackId}: audioFileId={AudioFileId}, path='{RequestedPath}' did not match a known variant.",
                trackId,
                audioFileId,
                filePath);
            return NotFound();
        }

        var selectedVariant = resolution.Variant;
        if (selectedVariant is null)
        {
            _logger.LogWarning("Local track playback failed for track {TrackId}: no accessible on-disk path resolved.", trackId);
            return NotFound();
        }

        if (!IsBrowserPlayableVariant(selectedVariant))
        {
            var transcoded = TryCreateTranscodedAudioResult(selectedVariant.FilePath!, cancellationToken);
            if (transcoded is not null)
            {
                return transcoded;
            }
        }

        return CreateTrackAudioFileResult(trackId, selectedVariant.FilePath!);
    }

    private static AudioVariantResolutionResult ResolveTrackAudioVariant(
        long? audioFileId,
        string? filePath,
        IReadOnlyList<AlbumTrackAudioInfoDto> variants,
        IReadOnlyList<string> playbackRoots)
    {
        var normalizedAudioFileId = audioFileId.GetValueOrDefault();
        var hasExplicitAudioFileId = normalizedAudioFileId > 0;
        var hasExplicitRequestedPath = !string.IsNullOrWhiteSpace(filePath);
        var hasExplicitSelector = hasExplicitAudioFileId || hasExplicitRequestedPath;

        AlbumTrackAudioInfoDto? requestedVariant = null;
        if (hasExplicitAudioFileId)
        {
            requestedVariant = variants.FirstOrDefault(variant => variant.AudioFileId == normalizedAudioFileId);
        }
        else if (hasExplicitRequestedPath)
        {
            requestedVariant = FindRequestedVariant(variants, filePath);
        }

        if (hasExplicitAudioFileId && hasExplicitRequestedPath && requestedVariant is not null)
        {
            var normalizedRequestedPath = NormalizePathForComparison(filePath);
            var normalizedVariantPath = NormalizePathForComparison(requestedVariant.FilePath);
            if (!string.Equals(normalizedRequestedPath, normalizedVariantPath, StringComparison.Ordinal))
            {
                requestedVariant = null;
            }
        }

        var requestedPathMismatch = hasExplicitSelector && requestedVariant is null;
        if (requestedPathMismatch)
        {
            return new AudioVariantResolutionResult(null, true);
        }

        var selectedVariant = ResolveVariantToExistingPath(requestedVariant, playbackRoots);
        if (selectedVariant is null && !hasExplicitSelector)
        {
            selectedVariant = ResolveFirstExistingVariant(variants, playbackRoots);
        }

        return new AudioVariantResolutionResult(selectedVariant, requestedPathMismatch);
    }

    private static AlbumTrackAudioInfoDto? ResolveFirstExistingVariant(
        IReadOnlyList<AlbumTrackAudioInfoDto> variants,
        IReadOnlyList<string> playbackRoots)
    {
        return variants
            .Select(variant => ResolveVariantToExistingPath(variant, playbackRoots))
            .FirstOrDefault(variant => variant is not null);
    }


    private IActionResult CreateTrackAudioFileResult(long trackId, string resolvedPath)
    {
        var contentType = GetAudioContentType(resolvedPath);
        FileStream stream;
        try
        {
            stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Local track playback failed for track {TrackId}: could not open {Path}.", trackId, resolvedPath);
            return Problem(
                detail: $"Unable to access local file '{resolvedPath}'. Check folder mount/path permissions in the running container.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Local playback unavailable");
        }

        return new FileStreamResult(stream, contentType)
        {
            EnableRangeProcessing = true
        };
    }

    private async Task<string?> ResolveTrackFilePathAsync(
        long trackId,
        string? requestedFilePath,
        bool preferBrowserPlayable,
        CancellationToken cancellationToken)
    {
        var variants = await _repository.GetTrackAudioVariantsAsync(trackId, cancellationToken);
        if (variants.Count == 0)
        {
            return null;
        }

        var normalizedRequested = NormalizePathForComparison(requestedFilePath);
        AlbumTrackAudioInfoDto? requestedVariant = null;
        if (!string.IsNullOrWhiteSpace(normalizedRequested))
        {
            requestedVariant = variants.FirstOrDefault(v =>
                NormalizePathForComparison(v.FilePath) == normalizedRequested);
        }

        if (requestedVariant is not null && System.IO.File.Exists(requestedVariant.FilePath))
        {
            if (!preferBrowserPlayable || IsBrowserPlayableVariant(requestedVariant))
            {
                return requestedVariant.FilePath;
            }

            var requestedFallback = variants.FirstOrDefault(v =>
                !string.IsNullOrWhiteSpace(v.FilePath)
                && System.IO.File.Exists(v.FilePath)
                && IsBrowserPlayableVariant(v));
            return requestedFallback?.FilePath ?? requestedVariant.FilePath;
        }

        if (preferBrowserPlayable)
        {
            var playable = variants.FirstOrDefault(v =>
                !string.IsNullOrWhiteSpace(v.FilePath)
                && System.IO.File.Exists(v.FilePath)
                && IsBrowserPlayableVariant(v));
            if (!string.IsNullOrWhiteSpace(playable?.FilePath))
            {
                return playable.FilePath;
            }
        }

        var firstExisting = variants.FirstOrDefault(v =>
            !string.IsNullOrWhiteSpace(v.FilePath)
            && System.IO.File.Exists(v.FilePath));
        return firstExisting?.FilePath;
    }

    private static AlbumTrackAudioInfoDto? FindRequestedVariant(
        IReadOnlyList<AlbumTrackAudioInfoDto> variants,
        string? requestedFilePath)
    {
        var normalizedRequested = NormalizePathForComparison(requestedFilePath);
        if (string.IsNullOrWhiteSpace(normalizedRequested))
        {
            return null;
        }

        return variants.FirstOrDefault(v =>
            NormalizePathForComparison(v.FilePath) == normalizedRequested);
    }

    private static bool IsBrowserPlayableVariant(AlbumTrackAudioInfoDto variant)
    {
        var extension = NormalizeExtension(variant.Extension, variant.FilePath);
        var codec = (variant.Codec ?? string.Empty).Trim().ToLowerInvariant();
        if (codec.Contains("ec-3", StringComparison.Ordinal)
            || codec.Contains("eac3", StringComparison.Ordinal)
            || codec.Contains("ac-3", StringComparison.Ordinal))
        {
            return false;
        }

        return extension is ".mp3"
            or ".flac"
            or ".wav"
            or ".aiff"
            or ".aif"
            or ".ogg"
            or ".opus"
            or ".aac"
            or ".m4a"
            or ".alac"
            or ".mp4";
    }

    private FileStreamResult? TryCreateTranscodedAudioResult(string filePath, CancellationToken cancellationToken)
    {
        var ffmpegPath = FfmpegPath.Value;
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return null;
        }

        var isFmp4 = FragmentedMp4DurationReader.IsFragmentedMp4(filePath);
        var startInfo = CreateFfmpegStartInfo(ffmpegPath, filePath, isFmp4);
        AddAacTranscodeArguments(startInfo);

        var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        RegisterProcessCleanup(process, cancellationToken);
        StartFragmentedInputPumpIfNeeded(isFmp4, filePath, process, cancellationToken);
        StartTranscodeDiagnosticsPump(process, filePath, cancellationToken);

        return new FileStreamResult(process.StandardOutput.BaseStream, "audio/aac");
    }

    private static void AddAacTranscodeArguments(ProcessStartInfo startInfo)
    {
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add("-c:a");
        startInfo.ArgumentList.Add("aac");
        startInfo.ArgumentList.Add("-b:a");
        startInfo.ArgumentList.Add("256k");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("adts");
        startInfo.ArgumentList.Add("-");
    }

    private void RegisterProcessCleanup(Process process, CancellationToken cancellationToken)
    {
        cancellationToken.Register(() => TryKill(process));
        HttpContext.Response.OnCompleted(() =>
        {
            TryKill(process);
            process.Dispose();
            return Task.CompletedTask;
        });
    }

    private void StartFragmentedInputPumpIfNeeded(
        bool isFmp4,
        string filePath,
        Process process,
        CancellationToken cancellationToken)
    {
        if (!isFmp4)
        {
            return;
        }

        _ = Task.Run(
            () => PumpFragmentedMp4IntoFfmpegAsync(filePath, process, cancellationToken),
            cancellationToken);
    }

    private async Task PumpFragmentedMp4IntoFfmpegAsync(
        string filePath,
        Process process,
        CancellationToken cancellationToken)
    {
        try
        {
            await FragmentedMp4DurationReader.ExtractMdatPayloadsAsync(
                filePath,
                process.StandardInput.BaseStream,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on early client disconnect.
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "fMP4 mdat extraction failed for {FilePath}", filePath);
            }
        }
        finally
        {
            try { process.StandardInput.Close(); }
            catch { /* Process may already be dead */ }
        }
    }

    private void StartTranscodeDiagnosticsPump(Process process, string filePath, CancellationToken cancellationToken)
    {
        _ = Task.Run(
            () => ReadTranscodeDiagnosticsAsync(process, filePath, cancellationToken),
            cancellationToken);
    }

    private async Task ReadTranscodeDiagnosticsAsync(
        Process process,
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr) && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "ffmpeg transcode exited with code {ExitCode} for {FilePath}: {Error}",
                    process.ExitCode,
                    filePath,
                    stderr.Trim());
            }
        }
        catch
        {
            // Best effort diagnostics only.
        }
    }

    private static string NormalizeExtension(string? extension, string? filePath)
    {
        var value = (extension ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            value = Path.GetExtension(filePath ?? string.Empty);
        }

        if (value.Length > 0 && value[0] != '.')
        {
            value = $".{value}";
        }

        return value.ToLowerInvariant();
    }

    private static string? NormalizePathForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var normalized = Path.GetFullPath(value.Trim())
                .Replace('\\', '/')
                .Trim()
                .ToLowerInvariant();
            return normalized;
        }
        catch
        {
            return value.Replace('\\', '/').Trim().ToLowerInvariant();
        }
    }

    private static AlbumTrackAudioInfoDto? ResolveVariantToExistingPath(
        AlbumTrackAudioInfoDto? variant,
        IReadOnlyList<string> playbackRoots)
    {
        if (variant is null || string.IsNullOrWhiteSpace(variant.FilePath))
        {
            return null;
        }

        var resolvedPath = ResolveExistingPathWithRoots(variant.FilePath, playbackRoots);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return null;
        }

        return string.Equals(resolvedPath, variant.FilePath, StringComparison.OrdinalIgnoreCase)
            ? variant
            : variant with { FilePath = resolvedPath };
    }

    private async Task<IReadOnlyList<string>> ResolvePlaybackRootCandidatesAsync(CancellationToken cancellationToken)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = await _repository.GetFoldersAsync(cancellationToken);
        foreach (var rootPath in folders.Select(static folder => folder.RootPath))
        {
            AddRootIfExists(roots, rootPath);
            AddRootIfExists(roots, DownloadPathResolver.ResolveIoPath(rootPath));
        }

        AddRootIfExists(roots, "/downloads");
        AddRootIfExists(roots, "/library");
        AddRootIfExists(roots, "/music");
        AddRootIfExists(roots, "/data");

        return roots
            .OrderByDescending(root => root.Length)
            .ToList();
    }

    private static void AddRootIfExists(HashSet<string> roots, string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(rootPath.Trim());
            if (Directory.Exists(fullPath))
            {
                roots.Add(fullPath);
            }
        }
        catch
        {
            // Ignore invalid root paths.
        }
    }

    private static string? ResolveExistingPathWithRoots(string path, IReadOnlyList<string> playbackRoots)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var directPath = path.Trim();
        if (System.IO.File.Exists(directPath))
        {
            return directPath;
        }

        var normalizedPath = directPath.Replace('\\', '/').Trim();
        var segments = normalizedPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2 || playbackRoots.Count == 0)
        {
            return null;
        }

        var maxSuffixLength = Math.Min(segments.Length, 10);
        for (var suffixLength = maxSuffixLength; suffixLength >= 2; suffixLength--)
        {
            var suffixSegments = segments[^suffixLength..];
            foreach (var root in playbackRoots)
            {
                var candidate = Path.Combine(new[] { root }.Concat(suffixSegments).ToArray());
                if (System.IO.File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private async Task<PcmStatsResult?> AnalyzePcmStatsAsync(
        string filePath,
        int sampleRateHz,
        int secondsLimit,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = FfmpegPath.Value;
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return new PcmStatsResult(null, null, null, 0, "ffmpeg executable was not found.");
        }

        var isFmp4 = FragmentedMp4DurationReader.IsFragmentedMp4(filePath);

        var startInfo = CreateFfmpegStartInfo(ffmpegPath, filePath, isFmp4);
        AddPcmExtractionArguments(startInfo, secondsLimit, sampleRateHz);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new PcmStatsResult(null, null, null, 0, "Failed to start ffmpeg.");
        }

        StartFragmentedInputPumpIfNeeded(isFmp4, filePath, process, cancellationToken);

        try
        {
            var stats = await ReadPcmAggregationAsync(process.StandardOutput.BaseStream, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
                var error = string.IsNullOrWhiteSpace(stderr)
                    ? "ffmpeg failed while extracting PCM stats."
                    : stderr.Trim();
                return new PcmStatsResult(null, null, null, stats.SampleCount, error);
            }

            return BuildPcmStatsResult(stats);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            TryKill(process);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to compute PCM stats for {FilePath}", filePath);
            }
            return new PcmStatsResult(null, null, null, 0, ex.Message);
        }
    }

    private static void AddPcmExtractionArguments(ProcessStartInfo startInfo, int secondsLimit, int sampleRateHz)
    {
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(Math.Max(10, secondsLimit).ToString());
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add(Math.Max(8000, sampleRateHz).ToString());
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("f32le");
        startInfo.ArgumentList.Add("-");
    }

    private static async Task<PcmAggregation> ReadPcmAggregationAsync(Stream output, CancellationToken cancellationToken)
    {
        await using var pcmStream = output;
        var buffer = new byte[64 * 1024];
        var leftover = new byte[4];
        var leftoverCount = 0;
        long sampleCount = 0;
        var peak = 0.0;
        var sumSquares = 0.0;

        while (true)
        {
            var bytesRead = await pcmStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead <= 0)
            {
                break;
            }

            var index = 0;
            if (leftoverCount > 0)
            {
                var toCopy = Math.Min(4 - leftoverCount, bytesRead);
                Buffer.BlockCopy(buffer, 0, leftover, leftoverCount, toCopy);
                leftoverCount += toCopy;
                index += toCopy;
                if (leftoverCount == 4)
                {
                    ConsumeSample(leftover, 0, ref peak, ref sumSquares, ref sampleCount);
                    leftoverCount = 0;
                }
            }

            var alignedBytes = ((bytesRead - index) / 4) * 4;
            var end = index + alignedBytes;
            for (var i = index; i < end; i += 4)
            {
                ConsumeSample(buffer, i, ref peak, ref sumSquares, ref sampleCount);
            }

            var remaining = bytesRead - end;
            if (remaining > 0)
            {
                Buffer.BlockCopy(buffer, end, leftover, 0, remaining);
                leftoverCount = remaining;
            }
        }

        return new PcmAggregation(peak, sumSquares, sampleCount);
    }

    private static PcmStatsResult BuildPcmStatsResult(PcmAggregation stats)
    {
        if (stats.SampleCount <= 0)
        {
            return new PcmStatsResult(null, null, null, 0, "No PCM samples decoded.");
        }

        var rmsLinear = Math.Sqrt(stats.SumSquares / stats.SampleCount);
        var peakDb = stats.Peak > 0 ? 20 * Math.Log10(stats.Peak) : (double?)null;
        var rmsDb = rmsLinear > 0 ? 20 * Math.Log10(rmsLinear) : (double?)null;
        var dynamicRangeDb = peakDb.HasValue && rmsDb.HasValue
            ? peakDb.Value - rmsDb.Value
            : (double?)null;

        return new PcmStatsResult(peakDb, rmsDb, dynamicRangeDb, stats.SampleCount, null);
    }

    private static void ConsumeSample(byte[] buffer, int offset, ref double peak, ref double sumSquares, ref long sampleCount)
    {
        var sample = BitConverter.ToSingle(buffer, offset);
        if (float.IsNaN(sample) || float.IsInfinity(sample))
        {
            return;
        }

        var abs = Math.Abs(sample);
        if (abs > peak)
        {
            peak = abs;
        }
        sumSquares += sample * sample;
        sampleCount++;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    private static string? ResolveFfmpegPath() => ExternalToolResolver.ResolveFfmpegPath();

    private static string? ResolveFfprobePath() => ExternalToolResolver.ResolveFfprobePath();

    private void TryReadTagAudioProperties(
        string filePath,
        long trackId,
        TrackSummaryState state)
    {
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            var properties = tagFile.Properties;
            if (properties is null)
            {
                return;
            }

            if (properties.AudioSampleRate > 0)
            {
                state.SampleRateHz = properties.AudioSampleRate;
            }
            if (properties.AudioChannels > 0)
            {
                state.Channels = properties.AudioChannels;
            }
            if (properties.BitsPerSample > 0)
            {
                state.BitsPerSample = properties.BitsPerSample;
            }
            if (properties.AudioBitrate > 0)
            {
                state.BitrateKbps = properties.AudioBitrate;
            }

            state.CodecDescription = properties.Codecs?.FirstOrDefault()?.Description;
            if (properties.Duration.TotalSeconds > 0)
            {
                state.DurationSeconds = properties.Duration.TotalSeconds;
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read TagLib audio properties for track {TrackId}", trackId);
            }
        }
    }

    private static ProcessStartInfo CreateFfmpegStartInfo(string ffmpegPath, string filePath, bool isFmp4)
    {
        var startInfo = CreateRedirectedProcessStartInfo(ffmpegPath, isFmp4);
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-nostdin");

        if (isFmp4)
        {
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("eac3");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add("pipe:0");
        }
        else
        {
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(filePath);
        }

        return startInfo;
    }

    private static ProcessStartInfo CreateRedirectedProcessStartInfo(string fileName, bool redirectStandardInput = false)
    {
        return new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStandardInput,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private async Task<FfprobeAudioInfo?> ProbeAudioInfoAsync(string filePath, CancellationToken cancellationToken)
    {
        var ffprobePath = FfprobePath.Value;
        if (string.IsNullOrWhiteSpace(ffprobePath))
        {
            return null;
        }

        var startInfo = CreateRedirectedProcessStartInfo(ffprobePath);
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-print_format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add(filePath);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        try
        {
            var probeJson = await TryReadFfprobeJsonAsync(process, filePath, cancellationToken);
            if (probeJson is null)
            {
                return null;
            }

            return ParseFfprobeAudioInfo(probeJson.Value);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            TryKill(process);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "ffprobe parse failed for {FilePath}", filePath);
            }
            return null;
        }
    }

    private async Task<JsonElement?> TryReadFfprobeJsonAsync(
        Process process,
        string filePath,
        CancellationToken cancellationToken)
    {
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            if (!string.IsNullOrWhiteSpace(stderr) && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("ffprobe failed for {FilePath}: {Error}", filePath, stderr.Trim());
            }
            return null;
        }

        using var json = JsonDocument.Parse(stdout);
        return json.RootElement.Clone();
    }

    private static FfprobeAudioInfo? ParseFfprobeAudioInfo(JsonElement root)
    {
        if (!TryGetFirstAudioStream(root, out var audioStream))
        {
            return null;
        }

        var sampleRateHz = TryReadInt(audioStream, "sample_rate");
        var channels = TryReadInt(audioStream, "channels");
        var streamBitrate = TryReadInt(audioStream, "bit_rate");
        int? bitrateKbps = streamBitrate is > 0
            ? Math.Max(1, (streamBitrate.Value + 500) / 1000)
            : null;
        var durationSeconds = root.TryGetProperty("format", out var format)
            ? TryReadDouble(format, "duration")
            : null;
        return new FfprobeAudioInfo(sampleRateHz, channels, bitrateKbps, durationSeconds);
    }

    private static bool TryGetFirstAudioStream(JsonElement root, out JsonElement audioStream)
    {
        audioStream = default;
        if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var stream in streams.EnumerateArray())
        {
            if (!stream.TryGetProperty("codec_type", out var codecType)
                || !string.Equals(codecType.GetString(), "audio", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            audioStream = stream;
            return true;
        }

        return false;
    }

    private static bool IsFragmentedMp4Candidate(string filePath, double? durationSeconds, int? bitrateKbps)
    {
        var extension = Path.GetExtension(filePath)?.Trim().ToLowerInvariant() ?? string.Empty;
        if (extension is not (".m4a" or ".m4b" or ".ec3" or ".ac3"))
        {
            return false;
        }

        if (!durationSeconds.HasValue || durationSeconds.Value <= 0)
        {
            return true;
        }

        if (!bitrateKbps.HasValue || bitrateKbps.Value <= 0)
        {
            return true;
        }

        try
        {
            var fi = new FileInfo(filePath);
            if (fi.Exists && fi.Length > 0)
            {
                var expectedSeconds = fi.Length * 8.0 / (bitrateKbps.Value * 1000.0);
                if (expectedSeconds > durationSeconds.Value * 4)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore file access errors
        }

        return false;
    }

    private static bool ShouldPreferFfprobeMetadata(string? codecDescription)
    {
        var normalized = (codecDescription ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return normalized.Contains("ec-3", StringComparison.Ordinal)
            || normalized.Contains("eac3", StringComparison.Ordinal)
            || normalized.Contains("ac-3", StringComparison.Ordinal)
            || normalized.Contains("atmos", StringComparison.Ordinal)
            || normalized.Contains("joc", StringComparison.Ordinal);
    }

    private static int? TryReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double? TryReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string GetAudioContentType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".wav" => "audio/wav",
            ".aiff" => "audio/aiff",
            ".aif" => "audio/aiff",
            ".alac" => "audio/mp4",
            ".m4a" => "audio/mp4",
            ".mp4" => "audio/mp4",
            ".ogg" => "audio/ogg",
            ".opus" => "audio/ogg",
            ".aac" => "audio/aac",
            ".wma" => "audio/x-ms-wma",
            _ => "application/octet-stream"
        };
    }

    private sealed record PcmStatsResult(
        double? PeakDb,
        double? RmsDb,
        double? DynamicRangeDb,
        long SampleCount,
        string? Error);

    private sealed class TrackSummaryState
    {
        public int? SampleRateHz { get; set; }
        public int? Channels { get; set; }
        public int? BitsPerSample { get; set; }
        public int? BitrateKbps { get; set; }
        public string? CodecDescription { get; set; }
        public double? DurationSeconds { get; set; }
    }

    private sealed record SummaryLevels(
        double? PeakAmplitudeDb,
        double? RmsDb,
        double? DynamicRangeDb);

    private sealed record PcmAggregation(
        double Peak,
        double SumSquares,
        long SampleCount);

    private sealed record FfprobeAudioInfo(
        int? SampleRateHz,
        int? Channels,
        int? BitrateKbps,
        double? DurationSeconds);
}
