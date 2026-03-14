namespace DeezSpoTag.Web.Services;

public static class SpotifyAudioFeatureMapper
{
    private static readonly string[] KeyNames =
    {
        "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"
    };

    public static string? MapKey(int? key, int? mode)
    {
        if (!key.HasValue || key.Value < 0 || key.Value >= KeyNames.Length)
        {
            return null;
        }

        var name = KeyNames[key.Value];
        if (mode.HasValue)
        {
            if (mode.Value == 0)
            {
                return $"{name}m";
            }

            if (mode.Value == 1)
            {
                return name;
            }
        }

        return name;
    }
}
