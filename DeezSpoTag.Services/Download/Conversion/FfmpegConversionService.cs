using System.Diagnostics;
using Microsoft.Extensions.Logging;
using DeezSpoTag.Services.Download.Shared.Utils;

namespace DeezSpoTag.Services.Download.Conversion;

public sealed class FfmpegConversionService
{
    private const string M4aAacFormat = "m4a-aac";
    private const string M4aAlacFormat = "m4a-alac";
    private const string AudioCodecArg = "-codec:a";
    private readonly ILogger<FfmpegConversionService> _logger;

    public FfmpegConversionService(ILogger<FfmpegConversionService> logger)
    {
        _logger = logger;
    }

    public async Task<ConversionResult> ConvertIfNeededAsync(
        string inputPath,
        string? convertTo,
        string? bitrate,
        ConversionOptions? options,
        CancellationToken cancellationToken)
    {
        var conversionOptions = options ?? ConversionOptions.Default;
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            return ConversionResult.Skipped("Input file not found.");
        }

        var format = NormalizeFormat(convertTo);
        if (string.IsNullOrWhiteSpace(format))
        {
            return ConversionResult.NoConversion(inputPath);
        }

        var inputFormat = GetInputFormat(inputPath);
        if (conversionOptions.SkipIfSourceMatches && string.Equals(inputFormat, format, StringComparison.OrdinalIgnoreCase))
        {
            return ConversionResult.NoConversion(inputPath);
        }

        if (conversionOptions.SkipLossyToLossless && IsLossyFormat(inputFormat) && IsLosslessTarget(format))
        {
            return ConversionResult.Skipped("Skipping lossy to lossless conversion.");
        }

        if (conversionOptions.WarnLossyToLossless && IsLossyFormat(inputFormat) && IsLosslessTarget(format))
        {
            _logger.LogWarning("Lossy to lossless conversion requested for {Input}", inputPath);
        }

        var outputPath = BuildOutputPath(inputPath, format);
        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            return ConversionResult.NoConversion(inputPath);
        }

        var ffmpegPath = ExternalToolResolver.ResolveFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return ConversionResult.Skipped("ffmpeg not available.");
        }

        var effectiveBitrate = ResolveBitrate(format, bitrate);
        var args = BuildArguments(inputPath, outputPath, format, effectiveBitrate, conversionOptions.ExtraArgs);

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger.LogInformation("FFmpeg conversion started: {Input} -> {Output} ({Format})", inputPath, outputPath, format);

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            return ConversionResult.Skipped("Failed to start ffmpeg.");
        }

        await process.WaitForExitAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);

        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            _logger.LogWarning("FFmpeg conversion failed: {Input} -> {Output}. Error: {Error}", inputPath, outputPath, stderr);
            return ConversionResult.Failed(stderr);
        }

        _logger.LogInformation("FFmpeg conversion completed: {Output}", outputPath);
        return ConversionResult.ConvertedTo(outputPath);
    }

    private static string NormalizeFormat(string? convertTo)
    {
        if (string.IsNullOrWhiteSpace(convertTo))
        {
            return string.Empty;
        }

        return convertTo.Trim().ToLowerInvariant() switch
        {
            "aac" => M4aAacFormat,
            "alac" => M4aAlacFormat,
            "m4a" => M4aAacFormat,
            _ => convertTo.Trim().ToLowerInvariant()
        };
    }

    private static string BuildOutputPath(string inputPath, string format)
    {
        var directory = Path.GetDirectoryName(inputPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var extension = format switch
        {
            M4aAacFormat or M4aAlacFormat => ".m4a",
            _ => "." + format
        };

        return Path.Join(directory, $"{name}{extension}");
    }

    private static string ResolveBitrate(string format, string? bitrate)
    {
        if (!IsLossy(format))
        {
            return string.Empty;
        }

        var value = string.IsNullOrWhiteSpace(bitrate) ? "320k" : bitrate.Trim();
        if (string.Equals(value, "AUTO", StringComparison.OrdinalIgnoreCase))
        {
            return "320k";
        }

        if (value.EndsWith("k", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return int.TryParse(value, out _) ? $"{value}k" : value;
    }

    private static bool IsLossy(string format)
    {
        return format is "mp3" or M4aAacFormat or "ogg" or "opus";
    }

    private static string BuildArguments(string inputPath, string outputPath, string format, string bitrate, string? extraArgs)
    {
        var args = new List<string>
        {
            "-y",
            "-i", Quote(inputPath),
            "-map", "0:a",
            "-map", "0:v?",
            "-map_metadata", "0",
            "-c:v", "copy"
        };

        switch (format)
        {
            case "mp3":
                args.AddRange(new[]
                {
                    AudioCodecArg, "libmp3lame",
                    "-b:a", bitrate,
                    "-id3v2_version", "3"
                });
                break;
            case M4aAacFormat:
                args.AddRange(new[]
                {
                    AudioCodecArg, "aac",
                    "-b:a", bitrate,
                    "-disposition:v:0", "attached_pic"
                });
                break;
            case M4aAlacFormat:
                args.AddRange(new[]
                {
                    AudioCodecArg, "alac",
                    "-disposition:v:0", "attached_pic"
                });
                break;
            case "flac":
                args.AddRange(new[] { AudioCodecArg, "flac" });
                break;
            case "ogg":
                args.AddRange(new[]
                {
                    AudioCodecArg, "libvorbis",
                    "-b:a", bitrate
                });
                break;
            case "opus":
                args.AddRange(new[]
                {
                    AudioCodecArg, "libopus",
                    "-b:a", bitrate
                });
                break;
            case "wav":
                args.AddRange(new[] { AudioCodecArg, "pcm_s16le" });
                break;
        }

        if (!string.IsNullOrWhiteSpace(extraArgs))
        {
            args.Add(extraArgs.Trim());
        }

        args.Add(Quote(outputPath));
        return string.Join(" ", args);
    }

    private static string GetInputFormat(string inputPath)
    {
        var ext = Path.GetExtension(inputPath);
        if (string.IsNullOrWhiteSpace(ext))
        {
            return string.Empty;
        }
        return ext.TrimStart('.').ToLowerInvariant();
    }

    private static bool IsLossyFormat(string format)
    {
        return format is "mp3" or "m4a" or M4aAacFormat or "aac" or "ogg" or "opus";
    }

    private static bool IsLosslessTarget(string format)
    {
        return format is "flac" or "wav" or M4aAlacFormat or "alac";
    }

    private static string Quote(string value) => $"\"{value}\"";
}

public sealed record ConversionResult(bool WasConverted, string OutputPath, string? Error)
{
    public static ConversionResult NoConversion(string path) => new(false, path, null);
    public static ConversionResult ConvertedTo(string path) => new(true, path, null);
    public static ConversionResult Failed(string error) => new(false, string.Empty, error);
    public static ConversionResult Skipped(string error) => new(false, string.Empty, error);
}

public sealed record ConversionOptions(
    bool KeepOriginal,
    bool SkipIfSourceMatches,
    string ExtraArgs,
    bool WarnLossyToLossless,
    bool SkipLossyToLossless)
{
    public static readonly ConversionOptions Default = new(
        KeepOriginal: true,
        SkipIfSourceMatches: true,
        ExtraArgs: "",
        WarnLossyToLossless: false,
        SkipLossyToLossless: false);
}
