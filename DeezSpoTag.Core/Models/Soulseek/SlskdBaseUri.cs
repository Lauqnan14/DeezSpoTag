namespace DeezSpoTag.Core.Models.Soulseek;

/// <summary>
///     Normalization helpers for a <c>slskd</c> base URL.
/// </summary>
/// <remarks>
///     This is the canonical implementation used by the Soulseek integration. The pre-existing
///     <c>DeezSpoTag.Web.Services.SoulseekConnectionService</c> ships its own equivalent
///     <c>NormalizeBaseUri</c> for the login flow; it is intentionally left untouched so the already
///     shipped login and sidebar behaviour cannot regress, and it can be switched over separately.
/// </remarks>
public static class SlskdBaseUri
{
    /// <summary>
    ///     Normalizes a user-supplied slskd base URL.
    /// </summary>
    /// <remarks>
    ///     A missing scheme is assumed to be <c>http</c>, which matches the
    ///     <c>http://localhost:5030</c> placeholder shown on the login page. Any path, query or fragment
    ///     is stripped so that the adapter can append its own <c>api/v0</c> segment.
    /// </remarks>
    /// <returns>The normalized base URI, or <see langword="null"/> when the value is not usable.</returns>
    public static Uri? TryNormalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            // A scheme-less value matches the login page's http://localhost:5030
            // placeholder for a loopback slskd. The scheme comes from the framework
            // constant rather than a cleartext URL literal.
            candidate = $"{Uri.UriSchemeHttp}://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed))
        {
            return null;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var builder = new UriBuilder(parsed) { Path = string.Empty, Query = string.Empty, Fragment = string.Empty };
        return builder.Uri;
    }

    /// <summary>
    ///     Builds the absolute URI for an <c>api/v0</c> path, for example <c>api/v0/searches</c>.
    /// </summary>
    /// <returns>The absolute request URI, or <see langword="null"/> when the base URL is unusable.</returns>
    public static Uri? TryBuildRequestUri(string? baseUrl, string apiPath)
    {
        var root = TryNormalize(baseUrl);
        if (root is null)
        {
            return null;
        }

        var relative = apiPath.TrimStart('/');
        return new Uri(root, $"api/v0/{relative}");
    }
}
