namespace DeezSpoTag.Services.Apple;

public static class AppleAtmosHeuristics
{
    private static readonly char[] ChannelSeparators = ['/', ',', ';', '-', 'x', 'X'];

    public static bool ContainsAtmosToken(string? value, string atmosToken = "atmos")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return normalized.Contains(atmosToken, StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("joc", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAtmosChannels(string? channels)
    {
        if (string.IsNullOrWhiteSpace(channels))
        {
            return false;
        }

        var normalized = channels.Trim();
        if (normalized.Contains("joc", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var token = normalized
            .Split(ChannelSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return int.TryParse(token, out var channelCount) && channelCount >= 16;
    }
}
