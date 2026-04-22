namespace DeezSpoTag.Web.Services;

internal static class MelodayClamp
{
    public static int PositiveOrDefault(int value, int fallback, int min, int max)
    {
        var effective = value <= 0 ? fallback : value;
        return Math.Clamp(effective, min, max);
    }

    public static double PositiveOrDefault(double value, double fallback, double min, double max)
    {
        var effective = value <= 0 ? fallback : value;
        return Math.Clamp(effective, min, max);
    }

    public static int AllowZeroOrDefault(int value, int fallback, int min, int max)
    {
        var effective = value < 0 ? fallback : value;
        return Math.Clamp(effective, min, max);
    }

    public static double AllowZeroOrDefault(double value, double fallback, double min, double max)
    {
        var effective = value < 0 ? fallback : value;
        return Math.Clamp(effective, min, max);
    }
}
