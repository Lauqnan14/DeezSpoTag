namespace DeezSpoTag.Services.Library;

public static class AudioVariantResolver
{
    public const string AtmosVariant = "atmos";
    public const string StereoVariant = "stereo";

    public static string ResolveAudioVariant(
        string? storedVariant,
        int? channels,
        string? filePath,
        string? codec = null,
        string? extension = null)
    {
        var normalized = NormalizeAudioVariant(storedVariant);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        return IsAtmosVariant(channels, codec, extension, filePath) ? AtmosVariant : StereoVariant;
    }

    public static string? NormalizeAudioVariant(string? value)
    {
        var normalized = NormalizeToken(value);
        return normalized switch
        {
            AtmosVariant => AtmosVariant,
            StereoVariant => StereoVariant,
            _ => null
        };
    }

    public static bool IsAtmosVariant(int? channels, string? codec, string? extension, string? filePath)
    {
        if (IsLosslessPcmFamily(codec, extension))
        {
            return false;
        }

        if (IsExplicitAtmosCodec(codec))
        {
            return true;
        }

        if (IsAtmosCodecFamily(codec, extension) && channels.HasValue && channels.Value > 2)
        {
            return true;
        }

        var pathHint = IsAtmosVariantPath(filePath);
        if (!pathHint)
        {
            return false;
        }

        if (channels.HasValue && channels.Value > 2)
        {
            return true;
        }

        return IsAtmosCodecFamily(codec, extension);
    }

    private static bool IsExplicitAtmosCodec(string? codec)
    {
        return ContainsAny(codec, "dolby atmos", "joc", AtmosVariant);
    }

    private static bool IsAtmosCodecFamily(string? codec, string? extension)
    {
        if (ContainsAny(codec, "ec-3", "eac3", "ac-3", "ac3", "truehd", "mlp"))
        {
            return true;
        }

        return NormalizeExtension(extension) is ".ec3" or ".ac3" or ".mlp";
    }

    private static bool IsLosslessPcmFamily(string? codec, string? extension)
    {
        if (ContainsAny(codec, "flac", "alac", "pcm", "wave", "wav", "aiff"))
        {
            return true;
        }

        return NormalizeExtension(extension) is ".flac" or ".wav" or ".aiff" or ".aif" or ".alac";
    }

    private static bool IsAtmosVariantPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var normalized = filePath.Replace('\\', '/').ToLowerInvariant();
        if (normalized.Contains("/atmos/", StringComparison.Ordinal)
            || normalized.Contains("/dolby atmos/", StringComparison.Ordinal)
            || normalized.Contains("/spatial/", StringComparison.Ordinal))
        {
            return true;
        }

        var fileName = Path.GetFileNameWithoutExtension(normalized);
        return fileName.Contains(AtmosVariant, StringComparison.Ordinal);
    }

    private static bool ContainsAny(string? value, params string[] tokens)
    {
        var normalized = NormalizeToken(value);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        return tokens.Any(token => normalized.Contains(token, StringComparison.Ordinal));
    }

    private static string NormalizeExtension(string? extension)
    {
        var normalizedExtension = NormalizeToken(extension);
        if (string.IsNullOrEmpty(normalizedExtension))
        {
            return string.Empty;
        }

        return normalizedExtension.StartsWith('.')
            ? normalizedExtension
            : $".{normalizedExtension}";
    }

    private static string NormalizeToken(string? value)
        => value?.Trim().ToLowerInvariant() ?? string.Empty;
}
