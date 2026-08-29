namespace DeezSpoTag.Web.Services;

internal static class BoomplaySessionCookie
{
    private const int MaxCookieHeaderLength = 8192;
    private const int MaxUserAgentLength = 1024;

    public static bool TryNormalize(string? rawCookie, out string normalizedCookie)
    {
        normalizedCookie = string.Empty;
        if (string.IsNullOrWhiteSpace(rawCookie))
        {
            return false;
        }

        var trimmed = rawCookie.Trim();
        if (trimmed.Length > MaxCookieHeaderLength || ContainsControlCharacter(trimmed))
        {
            return false;
        }

        var pairs = new List<string>();
        foreach (var segment in trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                return false;
            }

            var name = segment[..separatorIndex].Trim();
            var value = segment[(separatorIndex + 1)..].Trim();
            if (!IsCookieName(name) || ContainsControlCharacter(value) || value.Contains(';', StringComparison.Ordinal))
            {
                return false;
            }

            pairs.Add($"{name}={value}");
        }

        if (pairs.Count == 0)
        {
            return false;
        }

        normalizedCookie = string.Join("; ", pairs);
        return true;
    }

    public static bool TryNormalizeUserAgent(string? rawUserAgent, out string normalizedUserAgent)
    {
        normalizedUserAgent = string.Empty;
        if (string.IsNullOrWhiteSpace(rawUserAgent))
        {
            return false;
        }

        var trimmed = rawUserAgent.Trim();
        if (trimmed.Length > MaxUserAgentLength || ContainsControlCharacter(trimmed))
        {
            return false;
        }

        normalizedUserAgent = trimmed;
        return true;
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character) || character == '\u007f')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCookieName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] == '$')
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!IsTokenCharacter(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTokenCharacter(char character)
    {
        return character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
    }
}
