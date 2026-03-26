namespace DeezSpoTag.Web.Services.AutoTag;

internal static class DurationParser
{
    public static TimeSpan ParseMinutesSeconds(string text)
    {
        var parts = text.Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var minutes)
            && int.TryParse(parts[1], out var seconds))
        {
            return TimeSpan.FromSeconds((minutes * 60d) + seconds);
        }

        return TimeSpan.Zero;
    }
}
