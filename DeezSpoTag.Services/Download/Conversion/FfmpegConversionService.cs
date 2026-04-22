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
        var setup = TryBuildConversionSetup(inputPath, convertTo, bitrate, options);
        if (setup.Result is not null)
        {
            return setup.Result;
        }

        return await ExecuteConversionAsync(setup.Context!, cancellationToken);
    }

    private bool TryApplyLossyToLosslessPolicy(
        ConversionOptions options,
        string inputFormat,
        string targetFormat,
        string inputPath,
        out ConversionResult result)
    {
        result = ConversionResult.NoConversion(inputPath);
        var lossyToLossless = IsLossyFormat(inputFormat) && IsLosslessTarget(targetFormat);
        if (!lossyToLossless)
        {
            return false;
        }

        if (options.SkipLossyToLossless)
        {
            result = ConversionResult.Skipped("Skipping lossy to lossless conversion.");
            return true;
        }

        if (options.WarnLossyToLossless)
        {
            _logger.LogWarning("Lossy to lossless conversion requested for {Input}", inputPath);
        }

        return false;
    }

    private static ProcessStartInfo BuildProcessStartInfo(string ffmpegPath, string arguments)
    {
        return new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
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

    private ConversionSetup TryBuildConversionSetup(
        string inputPath,
        string? convertTo,
        string? bitrate,
        ConversionOptions? options)
    {
        var conversionOptions = options ?? ConversionOptions.Default;
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            return ConversionSetup.WithResult(ConversionResult.Skipped("Input file not found."));
        }

        var format = NormalizeFormat(convertTo);
        if (string.IsNullOrWhiteSpace(format))
        {
            return ConversionSetup.WithResult(ConversionResult.NoConversion(inputPath));
        }

        var inputFormat = GetInputFormat(inputPath);
        if (conversionOptions.SkipIfSourceMatches && string.Equals(inputFormat, format, StringComparison.OrdinalIgnoreCase))
        {
            return ConversionSetup.WithResult(ConversionResult.NoConversion(inputPath));
        }

        if (TryApplyLossyToLosslessPolicy(conversionOptions, inputFormat, format, inputPath, out var policyResult))
        {
            return ConversionSetup.WithResult(policyResult);
        }

        var outputPath = BuildOutputPath(inputPath, format);
        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            return ConversionSetup.WithResult(ConversionResult.NoConversion(inputPath));
        }

        var ffmpegPath = ExternalToolResolver.ResolveFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return ConversionSetup.WithResult(ConversionResult.Skipped("ffmpeg not available."));
        }

        var effectiveBitrate = ResolveBitrate(format, bitrate);
        var args = BuildArguments(inputPath, outputPath, format, effectiveBitrate, conversionOptions.ExtraArgs);
        var context = new ConversionExecutionContext(inputPath, outputPath, format, ffmpegPath, args);
        return ConversionSetup.WithContext(context);
    }

    private async Task<ConversionResult> ExecuteConversionAsync(
        ConversionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var startInfo = BuildProcessStartInfo(context.FfmpegPath, context.Arguments);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "FFmpeg conversion started: {Input} -> {Output} ({Format})",
                context.InputPath,
                context.OutputPath,
                context.Format);
        }

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            return ConversionResult.Skipped("Failed to start ffmpeg.");
        }

        await process.WaitForExitAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        var outputHasContent = OutputHasContent(context.OutputPath);
        if (process.ExitCode != 0 || !outputHasContent)
        {
            DeleteOutputIfExists(context.OutputPath);
            _logger.LogWarning(
                "FFmpeg conversion failed: {Input} -> {Output}. Error: {Error}",
                context.InputPath,
                context.OutputPath,
                stderr);
            return ConversionResult.Failed(stderr);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("FFmpeg conversion completed: {Output}", context.OutputPath);
        }

        return ConversionResult.ConvertedTo(context.OutputPath);
    }

    private static bool OutputHasContent(string outputPath)
    {
        if (!File.Exists(outputPath))
        {
            return false;
        }

        try
        {
            return new FileInfo(outputPath).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void DeleteOutputIfExists(string outputPath)
    {
        if (!File.Exists(outputPath))
        {
            return;
        }

        try
        {
            File.Delete(outputPath);
        }
        catch (Exception)
        {
            // best effort cleanup
        }
    }

    private static string BuildArguments(string inputPath, string outputPath, string format, string bitrate, string? extraArgs)
    {
        var args = new List<string>
        {
            "-y",
            "-i", Quote(inputPath),
            "-map", "0:a",
            "-map_metadata", "0"
        };

        var preserveAttachedArtwork = format is "mp3" or M4aAacFormat or M4aAlacFormat;
        if (preserveAttachedArtwork)
        {
            args.Add("-map");
            args.Add("0:v?");
            args.Add("-c:v");
            args.Add("copy");
        }

        switch (format)
        {
            case "mp3":
                args.Add(AudioCodecArg);
                args.Add("libmp3lame");
                args.Add("-b:a");
                args.Add(bitrate);
                args.Add("-id3v2_version");
                args.Add("3");
                break;
            case M4aAacFormat:
                args.Add(AudioCodecArg);
                args.Add("aac");
                args.Add("-b:a");
                args.Add(bitrate);
                args.Add("-disposition:v:0");
                args.Add("attached_pic");
                break;
            case M4aAlacFormat:
                args.Add(AudioCodecArg);
                args.Add("alac");
                args.Add("-disposition:v:0");
                args.Add("attached_pic");
                break;
            case "flac":
                args.Add(AudioCodecArg);
                args.Add("flac");
                break;
            case "ogg":
                args.Add(AudioCodecArg);
                args.Add("libvorbis");
                args.Add("-b:a");
                args.Add(bitrate);
                break;
            case "opus":
                args.Add(AudioCodecArg);
                args.Add("libopus");
                args.Add("-b:a");
                args.Add(bitrate);
                break;
            case "wav":
                args.Add(AudioCodecArg);
                args.Add("pcm_s16le");
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

    private sealed record ConversionExecutionContext(
        string InputPath,
        string OutputPath,
        string Format,
        string FfmpegPath,
        string Arguments);

    private sealed record ConversionSetup(ConversionExecutionContext? Context, ConversionResult? Result)
    {
        public static ConversionSetup WithContext(ConversionExecutionContext context) => new(context, null);
        public static ConversionSetup WithResult(ConversionResult result) => new(null, result);
    }
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
