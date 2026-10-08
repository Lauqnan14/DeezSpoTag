namespace DeezSpoTag.Web.Services;

internal static class BoomplaySessionCookie
{
    /// <summary>
    /// The only Boomplay cookie that authenticates a session. The rest of the browser
    /// jar (imei, name, email, afid, cf_clearance, analytics) is either device metadata
    /// or irrelevant to authentication.
    /// </summary>
    public const string SessionIdCookieName = "sessionID";

    private const int MaxCookieHeaderLength = 8192;
    private const int MaxUserAgentLength = 1024;
    private const int MinBareSessionIdLength = 24;

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

        // Copying a cookie out of browser devtools yields the bare value with no
        // "sessionID=" prefix, which the name=value parser below would reject outright. That
        // made a perfectly good session look like "Boomplay cookie is required". Wrap the bare
        // token so the value a user actually pastes is accepted. Guarded so that only a lone
        // opaque token qualifies: any '=', ';', whitespace or control character falls through to the
        // normal path and keeps the existing header-injection rejection.
        if (LooksLikeBareSessionId(trimmed))
        {
            trimmed = $"{SessionIdCookieName}={trimmed}";
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

    /// <summary>
    /// Extracts the <c>sessionID</c> value from a cookie header produced by
    /// <see cref="TryNormalize"/>. Reuses the same <c>name=value</c> segmentation so the
    /// normalization and header-injection rules stay the single source of truth.
    /// </summary>
    public static bool TryExtractSessionId(string? normalizedCookie, out string sessionId)
    {
        sessionId = string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedCookie))
        {
            return false;
        }

        foreach (var segment in normalizedCookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                continue;
            }

            if (!segment[..separatorIndex].Trim().Equals(SessionIdCookieName, StringComparison.Ordinal))
            {
                continue;
            }

            var value = segment[(separatorIndex + 1)..].Trim();
            if (value.Length == 0)
            {
                return false;
            }

            sessionId = value;
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when the input is a single opaque token with no name=value structure, which is
    /// what a devtools cookie copy looks like. The length floor keeps short nonsense
    /// (and words typed by mistake)
    /// on the rejecting path instead of silently wrapping them.
    /// </summary>
    private static bool LooksLikeBareSessionId(string trimmed)
    {
        if (trimmed.Length < MinBareSessionIdLength || trimmed.Length > MaxCookieHeaderLength)
        {
            return false;
        }

        if (trimmed.Contains('=', StringComparison.Ordinal)
            || trimmed.Contains(';', StringComparison.Ordinal)
            || trimmed.Contains(' ', StringComparison.Ordinal)
            || ContainsControlCharacter(trimmed))
        {
            return false;
        }

        return true;
    }

    private static bool ContainsControlCharacter(string value)
    {
        return value.Any(candidate => char.IsControl(candidate) || candidate == '\u007f');
    }

    private static bool IsCookieName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] == '$')
        {
            return false;
        }

        return !value.Any(candidate => !IsTokenCharacter(candidate));
    }

    private static bool IsTokenCharacter(char character)
    {
        return character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
    }
}
