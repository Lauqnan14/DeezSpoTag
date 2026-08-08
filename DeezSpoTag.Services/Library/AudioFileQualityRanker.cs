namespace DeezSpoTag.Services.Library;

public readonly record struct AudioQualityFacts(
    string? Codec,
    int? BitrateKbps,
    int? BitsPerSample,
    int? SampleRateHz,
    int? Channels,
    string? FilePath);

public static class AudioFileQualityRanker
{
    private const int AtmosRank = 5;
    private const int HiResRank = 4;
    private const int LosslessRank = 3;
    private const int LossyRank = 2;
    private const int LowRank = 1;
    private const int HighBitrateThresholdKbps = 192;

    public static int? EstimateRank(
        in AudioQualityFacts facts,
        SignalQualityAnalysis? signalAnalysis = null,
        bool promoteAtmos = true)
    {
        var extension = NormalizeExtension(facts.FilePath);
        var codecText = NormalizeCodec(facts.Codec);
        var baseRank = EstimateBaseRank(facts, signalAnalysis, extension, codecText);

        if (!promoteAtmos)
        {
            return baseRank;
        }

        return AudioVariantResolver.IsAtmosVariant(facts.Channels, facts.Codec, extension, facts.FilePath)
            && baseRank is null or < AtmosRank
            ? AtmosRank
            : baseRank;
    }

    public static int? EstimateRankForFile(
        string? path,
        SignalQualityAnalysis? signalAnalysis = null,
        bool promoteAtmos = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            using var file = TagLib.File.Create(path);
            return EstimateRank(ReadFacts(file, path), signalAnalysis, promoteAtmos)
                ?? EstimateRankFromExtension(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return EstimateRankFromExtension(path);
        }
    }

    public static AudioQualityFacts ReadFacts(TagLib.File file, string? path)
    {
        var properties = file.Properties;
        return new AudioQualityFacts(
            Codec: properties?.Codecs?.FirstOrDefault()?.Description,
            BitrateKbps: PositiveOrNull(properties?.AudioBitrate),
            BitsPerSample: PositiveOrNull(properties?.BitsPerSample),
            SampleRateHz: PositiveOrNull(properties?.AudioSampleRate),
            Channels: PositiveOrNull(properties?.AudioChannels),
            FilePath: path);
    }

    public static int? EstimateRankFromExtension(string? path)
    {
        return NormalizeExtension(path) switch
        {
            ".flac" or ".alac" or ".wav" or ".aiff" or ".aif" => LosslessRank,
            ".mp3" or ".m4a" or ".m4b" or ".aac" or ".ogg" or ".opus" or ".wma" => LossyRank,
            _ => null
        };
    }

    private static int? EstimateBaseRank(
        in AudioQualityFacts facts,
        SignalQualityAnalysis? signalAnalysis,
        string extension,
        string codecText)
    {
        if (IsLosslessAudio(extension, codecText))
        {
            return EstimateLosslessRank(facts.BitsPerSample, facts.SampleRateHz, signalAnalysis);
        }

        return EstimateLossyRank(facts.BitrateKbps, facts.SampleRateHz, signalAnalysis, extension, codecText)
            ?? EstimateBitDepthRank(facts.BitsPerSample);
    }

    private static int EstimateLosslessRank(
        int? bitsPerSample,
        int? sampleRateHz,
        SignalQualityAnalysis? signalAnalysis)
    {
        if (signalAnalysis is { IsLosslessCodecContainer: true, IsTrueLossless: false }
            && signalAnalysis.EquivalentBitrateKbps.HasValue)
        {
            return signalAnalysis.EquivalentBitrateKbps.Value >= HighBitrateThresholdKbps ? LossyRank : LowRank;
        }

        var bitDepthRank = EstimateBitDepthRank(bitsPerSample);
        if (bitDepthRank.HasValue)
        {
            return bitDepthRank.Value;
        }

        return sampleRateHz > 48000 ? HiResRank : LosslessRank;
    }

    private static int? EstimateLossyRank(
        int? bitrateKbps,
        int? sampleRateHz,
        SignalQualityAnalysis? signalAnalysis,
        string extension,
        string codecText)
    {
        if (bitrateKbps.HasValue)
        {
            return bitrateKbps.Value >= HighBitrateThresholdKbps
                ? LossyRank
                : bitrateKbps.Value > 0 ? LowRank : null;
        }

        if (signalAnalysis?.EquivalentBitrateKbps is int estimatedBitrate)
        {
            return estimatedBitrate >= HighBitrateThresholdKbps ? LossyRank : LowRank;
        }

        if (!IsLossyAudio(extension, codecText))
        {
            return null;
        }

        return sampleRateHz >= 44100 ? LossyRank : LowRank;
    }

    private static int? EstimateBitDepthRank(int? bitsPerSample)
    {
        if (!bitsPerSample.HasValue)
        {
            return null;
        }

        if (bitsPerSample.Value >= 24)
        {
            return HiResRank;
        }

        return bitsPerSample.Value >= 16 ? LosslessRank : null;
    }

    private static bool IsLosslessAudio(string extension, string codecText)
        => extension is ".flac" or ".alac" or ".wav" or ".aiff" or ".aif"
            || codecText.Contains("flac", StringComparison.Ordinal)
            || codecText.Contains("alac", StringComparison.Ordinal)
            || codecText.Contains("lossless", StringComparison.Ordinal)
            || codecText.Contains("pcm", StringComparison.Ordinal)
            || codecText.Contains("wave", StringComparison.Ordinal);

    private static bool IsLossyAudio(string extension, string codecText)
        => extension is ".mp3" or ".m4a" or ".m4b" or ".aac" or ".ogg" or ".opus"
            || codecText.Contains("aac", StringComparison.Ordinal)
            || codecText.Contains("mp3", StringComparison.Ordinal)
            || codecText.Contains("mpeg", StringComparison.Ordinal)
            || codecText.Contains("vorbis", StringComparison.Ordinal)
            || codecText.Contains("opus", StringComparison.Ordinal)
            || codecText.Contains("mp4a", StringComparison.Ordinal);

    private static string NormalizeExtension(string? filePath)
        => string.IsNullOrWhiteSpace(filePath)
            ? string.Empty
            : Path.GetExtension(filePath)?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string NormalizeCodec(string? codec)
        => codec?.Trim().ToLowerInvariant() ?? string.Empty;

    private static int? PositiveOrNull(int? value)
        => value is > 0 ? value : null;
}
