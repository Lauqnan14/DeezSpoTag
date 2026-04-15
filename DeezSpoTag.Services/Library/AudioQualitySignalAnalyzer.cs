using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using DeezSpoTag.Services.Download.Shared.Utils;
using Microsoft.Extensions.Logging;
using NAudio.Dsp;

namespace DeezSpoTag.Services.Library;

public sealed class AudioQualitySignalAnalyzer
{
    private const int MaxLoadSeconds = 300;
    private const int MaxSamples = 12_000_000;
    private const double TargetHzResolution = 5.0;
    private const double RelativeDbThreshold = -90.0;
    private const double FallbackDbThreshold = -75.0;
    private const double GapHzThreshold = 700.0;
    private static readonly string[] LosslessCodecHints =
    {
        "flac", "alac", "wav", "wave", "aiff", "pcm"
    };

    private readonly ILogger<AudioQualitySignalAnalyzer> _logger;
    private static readonly Lazy<string?> FfprobePath = new(ResolveFfprobePath);
    private static readonly Lazy<string?> FfmpegPath = new(ResolveFfmpegPath);

    public AudioQualitySignalAnalyzer(ILogger<AudioQualitySignalAnalyzer> logger)
    {
        _logger = logger;
    }

    public SignalQualityAnalysis? Analyze(
        string filePath,
        string? codecHint,
        int? sampleRateHint,
        int? bitrateHintKbps)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        var ffmpeg = FfmpegPath.Value;
        var ffprobe = FfprobePath.Value;
        if (!HasRequiredTools(filePath, ffmpeg, ffprobe))
        {
            return null;
        }

        var probe = ProbeAudio(ffprobe!, filePath);
        var sampleRate = probe?.SampleRateHz ?? sampleRateHint ?? 0;
        if (sampleRate <= 0)
        {
            return null;
        }

        var codec = string.IsNullOrWhiteSpace(probe?.CodecName) ? codecHint : probe!.CodecName;
        var statedBitrate = probe?.BitrateKbps ?? bitrateHintKbps;
        var samples = DecodeMonoFloatSamples(ffmpeg!, filePath);
        if (!HasEnoughDecodedSamples(filePath, samples))
        {
            return null;
        }

        var maxFreq = AnalyzePeakFrequency(samples!, sampleRate, codec);
        if (maxFreq <= 0)
        {
            return null;
        }

        var nyquist = sampleRate / 2d;
        var ratio = nyquist > 0 ? maxFreq / nyquist : 0;
        var isLosslessCodec = IsLosslessCodec(codec);
        int? equivalent = ClassifyEquivalentBitrate(maxFreq);
        var isTrueLossless = isLosslessCodec && ratio >= 0.95;

        // For true lossless tracks, prefer the measured/container bitrate so we do not
        // accidentally downgrade to lossy-equivalent ranks.
        if (isTrueLossless)
        {
            equivalent = statedBitrate;
        }

        return new SignalQualityAnalysis(
            Codec: codec,
            SampleRateHz: sampleRate,
            StatedBitrateKbps: statedBitrate,
            MaxFrequencyHz: maxFreq,
            NyquistFrequencyHz: nyquist,
            PeakFrequencyRatio: ratio,
            EquivalentBitrateKbps: equivalent,
            IsTrueLossless: isTrueLossless,
            IsLosslessCodecContainer: isLosslessCodec);
    }

    private bool HasRequiredTools(string filePath, string? ffmpeg, string? ffprobe)
    {
        if (!string.IsNullOrWhiteSpace(ffmpeg) && !string.IsNullOrWhiteSpace(ffprobe))
        {
            return true;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Signal analyzer skipped for {File}: ffmpeg/ffprobe not available.", filePath);
        }
        return false;
    }

    private bool HasEnoughDecodedSamples(string filePath, float[]? samples)
    {
        if (samples is { Length: >= 1024 })
        {
            return true;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Signal analyzer skipped for {File}: unable to decode enough PCM samples.", filePath);
        }
        return false;
    }

    private static ProbeInfo? ProbeAudio(string ffprobePath, string filePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-select_streams");
            startInfo.ArgumentList.Add("a:0");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("stream=codec_name,sample_rate,bit_rate");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("json");
            startInfo.ArgumentList.Add(filePath);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (string.IsNullOrWhiteSpace(stdout))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
            {
                return null;
            }

            var stream = streams[0];
            var codec = stream.TryGetProperty("codec_name", out var codecEl) ? codecEl.GetString() : null;
            var sampleRate = stream.TryGetProperty("sample_rate", out var srEl) && int.TryParse(srEl.GetString(), out var sr) ? sr : 0;
            var bitrate = stream.TryGetProperty("bit_rate", out var brEl) && long.TryParse(brEl.GetString(), out var br)
                ? (int)Math.Max(0, br / 1000)
                : (int?)null;

            return new ProbeInfo(codec, sampleRate, bitrate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static float[]? DecodeMonoFloatSamples(string ffmpegPath, string filePath)
    {
        var maxBytes = MaxSamples * sizeof(float);
        var isFmp4 = FragmentedMp4DurationReader.IsFragmentedMp4(filePath);

        try
        {
            using var process = Process.Start(BuildDecodeProcessStartInfo(ffmpegPath, filePath, isFmp4));
            if (process == null)
            {
                return null;
            }

            if (isFmp4)
            {
                StartFragmentedMp4InputPump(filePath, process);
            }

            var bytes = ReadDecodedBytes(process, maxBytes);
            if (bytes.Length == 0)
            {
                return null;
            }

            return ConvertToFloatSamples(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static ProcessStartInfo BuildDecodeProcessStartInfo(string ffmpegPath, string filePath, bool isFmp4)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = isFmp4,
            UseShellExecute = false,
            CreateNoWindow = true
        };
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

        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:a:0");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(MaxLoadSeconds.ToString());
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("f32le");
        startInfo.ArgumentList.Add("-");
        return startInfo;
    }

    private static void StartFragmentedMp4InputPump(string filePath, Process process)
    {
        // Feed clean mdat payloads to FFmpeg stdin without blocking the main decode path.
        _ = Task.Run(async () =>
        {
            try
            {
                await FragmentedMp4DurationReader.ExtractMdatPayloadsAsync(
                    filePath,
                    process.StandardInput.BaseStream);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best effort only.
            }
            finally
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Process may already be dead.
                }
            }
        });
    }

    private static byte[] ReadDecodedBytes(Process process, int maxBytes)
    {
        using var ms = new MemoryStream(Math.Min(maxBytes, 8 * 1024 * 1024));
        var rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = process.StandardOutput.BaseStream.Read(rented, 0, rented.Length);
                if (read <= 0)
                {
                    break;
                }

                var remaining = maxBytes - (int)ms.Length;
                if (remaining <= 0)
                {
                    TryKill(process);
                    break;
                }

                var toWrite = Math.Min(read, remaining);
                ms.Write(rented, 0, toWrite);
                if (toWrite < read)
                {
                    TryKill(process);
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        process.WaitForExit(5000);
        return ms.ToArray();
    }

    private static float[] ConvertToFloatSamples(byte[] bytes)
    {
        var sampleCount = bytes.Length / sizeof(float);
        var samples = new float[sampleCount];
        Buffer.BlockCopy(bytes, 0, samples, 0, sampleCount * sizeof(float));
        return samples;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort only.
        }
    }

    private static double AnalyzePeakFrequency(float[] samples, int sampleRate, string? codec)
    {
        var idealNperseg = sampleRate / TargetHzResolution;
        var nfft = NextPowerOfTwo(Math.Max(1024, (int)Math.Ceiling(idealNperseg)));
        if (samples.Length < nfft)
        {
            return 0;
        }

        if (!TryBuildAverageSpectrum(samples, nfft, out var psd))
        {
            return 0;
        }

        var max = psd.Max();
        if (max <= 0)
        {
            return 0;
        }

        var db = ConvertToRelativeDb(psd, max);
        var binHz = sampleRate / (double)nfft;
        return ApplyCodecAwareCutoff(db, binHz, sampleRate, codec);
    }

    private static bool TryBuildAverageSpectrum(float[] samples, int nfft, out double[] psd)
    {
        var bins = (nfft / 2) + 1;
        psd = new double[bins];
        var segment = new float[nfft];
        var window = BuildHannWindow(nfft);
        var hop = nfft / 2;
        var fftPower = (int)Math.Log2(nfft);
        var segments = 0;

        for (var start = 0; start + nfft <= samples.Length; start += hop)
        {
            Array.Copy(samples, start, segment, 0, nfft);
            NormalizeInPlace(segment);
            var complex = new Complex[nfft];
            for (var i = 0; i < nfft; i++)
            {
                complex[i].X = (float)(segment[i] * window[i]);
                complex[i].Y = 0f;
            }

            FastFourierTransform.FFT(true, fftPower, complex);
            AccumulatePowerSpectrum(psd, complex);
            segments++;
        }

        if (segments == 0)
        {
            return false;
        }

        for (var i = 0; i < psd.Length; i++)
        {
            psd[i] /= segments;
        }

        return true;
    }

    private static void AccumulatePowerSpectrum(double[] psd, Complex[] complex)
    {
        for (var k = 0; k < psd.Length; k++)
        {
            var re = complex[k].X;
            var im = complex[k].Y;
            psd[k] += (re * re) + (im * im);
        }
    }

    private static double[] ConvertToRelativeDb(double[] psd, double maxPower)
    {
        var db = new double[psd.Length];
        for (var i = 0; i < psd.Length; i++)
        {
            db[i] = 10d * Math.Log10(psd[i] / maxPower);
        }

        return db;
    }

    private static double ApplyCodecAwareCutoff(double[] db, double binHz, int sampleRate, string? codec)
    {
        var maxFreq = FindCutoffFrequency(db, binHz, RelativeDbThreshold);
        maxFreq = ClampLosslessHiResCutoff(maxFreq, sampleRate, codec);

        var nyquist = sampleRate / 2d;
        if (nyquist <= 0 || (maxFreq / nyquist) <= 0.99)
        {
            return maxFreq;
        }

        var fallbackFreq = FindCutoffFrequency(db, binHz, FallbackDbThreshold);
        return ClampLosslessHiResCutoff(fallbackFreq, sampleRate, codec);
    }

    private static double ClampLosslessHiResCutoff(double maxFreq, int sampleRate, string? codec)
    {
        return IsLosslessCodec(codec) && sampleRate > 48000 && maxFreq > 24000
            ? 24000
            : maxFreq;
    }

    private static double FindCutoffFrequency(double[] db, double binHz, double threshold)
    {
        var significant = new List<int>(db.Length / 8);
        for (var i = 0; i < db.Length; i++)
        {
            if (db[i] > threshold)
            {
                significant.Add(i);
            }
        }

        if (significant.Count == 0)
        {
            return 0;
        }

        var previous = significant[0];
        for (var i = 1; i < significant.Count; i++)
        {
            var current = significant[i];
            if ((current - previous) * binHz > GapHzThreshold)
            {
                return previous * binHz;
            }

            previous = current;
        }

        return previous * binHz;
    }

    private static int ClassifyEquivalentBitrate(double maxFrequencyHz)
    {
        if (maxFrequencyHz >= 19500)
        {
            return 320;
        }

        if (maxFrequencyHz >= 19090)
        {
            return 256;
        }

        if (maxFrequencyHz >= 17000)
        {
            return 192;
        }

        return 128;
    }

    private static bool IsLosslessCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return false;
        }

        var normalized = codec.Trim().ToLowerInvariant();
        return LosslessCodecHints.Any(hint => normalized.Contains(hint, StringComparison.Ordinal));
    }

    private static double[] BuildHannWindow(int length)
    {
        var window = new double[length];
        if (length <= 1)
        {
            window[0] = 1;
            return window;
        }

        for (var n = 0; n < length; n++)
        {
            window[n] = 0.5d * (1d - Math.Cos((2d * Math.PI * n) / (length - 1)));
        }

        return window;
    }

    private static void NormalizeInPlace(float[] values)
    {
        var maxAbs = 0f;
        for (var i = 0; i < values.Length; i++)
        {
            var abs = Math.Abs(values[i]);
            if (abs > maxAbs)
            {
                maxAbs = abs;
            }
        }

        if (maxAbs <= 1e-9f)
        {
            return;
        }

        var inv = 1f / maxAbs;
        for (var i = 0; i < values.Length; i++)
        {
            values[i] *= inv;
        }
    }

    private static int NextPowerOfTwo(int value)
    {
        var n = 1;
        while (n < value)
        {
            n <<= 1;
        }

        return n;
    }

    private static string? ResolveFfprobePath() => ExternalToolResolver.ResolveFfprobePath();

    private static string? ResolveFfmpegPath() => ExternalToolResolver.ResolveFfmpegPath();

    private sealed record ProbeInfo(string? CodecName, int SampleRateHz, int? BitrateKbps);
}

public sealed record SignalQualityAnalysis(
    string? Codec,
    int SampleRateHz,
    int? StatedBitrateKbps,
    double MaxFrequencyHz,
    double NyquistFrequencyHz,
    double PeakFrequencyRatio,
    int? EquivalentBitrateKbps,
    bool IsTrueLossless,
    bool IsLosslessCodecContainer);
