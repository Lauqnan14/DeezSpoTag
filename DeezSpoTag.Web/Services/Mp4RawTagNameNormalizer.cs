namespace DeezSpoTag.Web.Services;

internal static class Mp4RawTagNameNormalizer
{
    public static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var cleaned = name;
        if (cleaned.StartsWith("----:", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[5..];
        }
        if (cleaned.StartsWith("iTunes:", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned["iTunes:".Length..];
        }
        if (cleaned.StartsWith("com.apple.iTunes:", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned["com.apple.iTunes:".Length..];
        }

        if (cleaned.Contains(':'))
        {
            var parts = cleaned.Split(':', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? cleaned : parts[^1];
        }

        return cleaned;
    }
}
