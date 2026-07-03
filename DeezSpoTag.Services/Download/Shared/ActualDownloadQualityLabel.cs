using TagLib;
using DeezSpoTag.Services.Download.Amazon;

namespace DeezSpoTag.Services.Download.Shared;

internal static class ActualDownloadQualityLabel
{
    public static void ApplyTo(EngineQueueItemBase payload, string filePath)
    {
        var label = payload is AmazonQueueItem
            ? TryBuildAmazon(filePath)
            : TryBuild(filePath);
        if (!string.IsNullOrWhiteSpace(label))
        {
            payload.Quality = label;
        }
    }

    public static string? TryBuild(string filePath)
        => TryBuildCore(filePath, TryBuildLosslessLabel);

    private static string? TryBuildAmazon(string filePath)
        => TryBuildCore(filePath, TryBuildAmazonLosslessLabel);

    private static string? TryBuildCore(
        string filePath,
        Func<int, int, string, string, string?> losslessLabelBuilder)
    {
        var ioPath = DeezSpoTag.Services.Download.Utils.DownloadPathResolver.ResolveIoPath(filePath);
        if (string.IsNullOrWhiteSpace(ioPath) || !System.IO.File.Exists(ioPath))
        {
            return null;
        }

        try
        {
            using var file = TagLib.File.Create(ioPath);
            var properties = file.Properties;
            var bitsPerSample = properties.BitsPerSample;
            var sampleRate = properties.AudioSampleRate;
            var bitrate = properties.AudioBitrate;
            var extension = System.IO.Path.GetExtension(ioPath);
            var codec = ResolveCodecText(properties);

            if (IsFlac(extension, codec)
                && (bitsPerSample <= 0 || sampleRate <= 0)
                && TryReadFlacProperties(ioPath, out var flacBitsPerSample, out var flacSampleRate))
            {
                bitsPerSample = flacBitsPerSample;
                sampleRate = flacSampleRate;
            }

            if (IsLossy(extension, codec))
            {
                return TryBuildLossyLabel(bitrate, extension, codec);
            }

            return losslessLabelBuilder(bitsPerSample, sampleRate, extension, codec)
                   ?? TryBuildLossyLabel(bitrate, extension, codec);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool TryReadFlacProperties(
        string filePath,
        out int bitsPerSample,
        out int sampleRate)
    {
        bitsPerSample = 0;
        sampleRate = 0;

        try
        {
            using var stream = System.IO.File.OpenRead(filePath);
            Span<byte> marker = stackalloc byte[4];
            if (stream.Read(marker) != marker.Length || !marker.SequenceEqual("fLaC"u8))
            {
                return false;
            }

            while (stream.Position + 4 <= stream.Length)
            {
                Span<byte> blockHeader = stackalloc byte[4];
                if (stream.Read(blockHeader) != blockHeader.Length)
                {
                    return false;
                }

                var blockType = blockHeader[0] & 0x7F;
                var blockLength = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];
                if (blockLength < 0 || stream.Position + blockLength > stream.Length)
                {
                    return false;
                }

                if (blockType != 0)
                {
                    stream.Seek(blockLength, SeekOrigin.Current);
                    continue;
                }

                if (blockLength < 18)
                {
                    return false;
                }

                Span<byte> streamInfo = stackalloc byte[18];
                if (stream.Read(streamInfo) != streamInfo.Length)
                {
                    return false;
                }

                ulong packed = 0;
                for (var index = 10; index < 18; index++)
                {
                    packed = (packed << 8) | streamInfo[index];
                }

                sampleRate = (int)((packed >> 44) & 0xFFFFF);
                bitsPerSample = (int)(((packed >> 36) & 0x1F) + 1);
                return sampleRate > 0 && bitsPerSample > 0;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        return false;
    }

    private static string? TryBuildLosslessLabel(
        int bitsPerSample,
        int sampleRate,
        string extension,
        string codec)
    {
        if (bitsPerSample <= 0 || sampleRate <= 0)
        {
            return null;
        }

        var exact = $"{bitsPerSample}-bit/{FormatSampleRate(sampleRate)}";
        if (bitsPerSample >= 24)
        {
            return sampleRate > 96000
                ? $"Max Hi-Res ({exact})"
                : $"Hi-Res ({exact})";
        }

        if (IsFlac(extension, codec) && !IsCdLossless(bitsPerSample, sampleRate))
        {
            return $"FLAC ({exact})";
        }

        return $"CD Lossless ({exact})";
    }

    private static string? TryBuildAmazonLosslessLabel(
        int bitsPerSample,
        int sampleRate,
        string extension,
        string codec)
    {
        if (bitsPerSample <= 0 || sampleRate <= 0 || !IsFlac(extension, codec))
        {
            return TryBuildLosslessLabel(bitsPerSample, sampleRate, extension, codec);
        }

        var exact = $"{bitsPerSample}-bit/{FormatSampleRate(sampleRate)}";
        return bitsPerSample >= 24
            ? $"Ultra HD FLAC ({exact})"
            : $"HD FLAC ({exact})";
    }

    private static string? TryBuildLossyLabel(int bitrate, string extension, string codec)
    {
        if (bitrate <= 0)
        {
            return null;
        }

        if (IsAac(extension, codec))
        {
            return $"AAC-LC {bitrate} kbps";
        }

        return $"MP3 {bitrate} kbps";
    }

    private static bool IsCdLossless(int bitsPerSample, int sampleRate)
        => bitsPerSample <= 16 && sampleRate <= 44100;

    private static bool IsFlac(string extension, string codec)
        => string.Equals(extension, ".flac", StringComparison.OrdinalIgnoreCase)
           || codec.Contains("flac", StringComparison.OrdinalIgnoreCase);

    private static bool IsAac(string extension, string codec)
        => string.Equals(extension, ".m4a", StringComparison.OrdinalIgnoreCase)
           || string.Equals(extension, ".aac", StringComparison.OrdinalIgnoreCase)
           || codec.Contains("aac", StringComparison.OrdinalIgnoreCase)
           || codec.Contains("mp4", StringComparison.OrdinalIgnoreCase);

    private static bool IsLossy(string extension, string codec)
        => string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase)
           || string.Equals(extension, ".aac", StringComparison.OrdinalIgnoreCase)
           || codec.Contains("mp3", StringComparison.OrdinalIgnoreCase)
           || codec.Contains("mpeg audio", StringComparison.OrdinalIgnoreCase)
           || (codec.Contains("aac", StringComparison.OrdinalIgnoreCase)
               && !codec.Contains("alac", StringComparison.OrdinalIgnoreCase));

    private static string FormatSampleRate(int sampleRate)
    {
        if (sampleRate % 1000 == 0)
        {
            return $"{sampleRate / 1000}kHz";
        }

        var khz = sampleRate / 1000.0;
        return $"{khz:0.#}kHz";
    }

    private static string ResolveCodecText(Properties properties)
    {
        try
        {
            return properties.Codecs == null
                ? string.Empty
                : string.Join(' ', properties.Codecs.Select(codec => codec.Description ?? string.Empty));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }
}
