using System.Text;

namespace DeezSpoTag.Services.Download.SoundCloud;

/// <summary>
///     Removes SoundCloud authorization material from a URL before it is logged or put in an error message.
/// </summary>
/// <remarks>
///     <para>
///         A SoundCloud stream URL carries its authorization as query parameters:
///         <c>track_authorization</c> is a per-track secret, <c>secret_token</c> unlocks a private share link,
///         and <c>client_id</c> identifies the app. The OAuth token itself is only ever a header or a cookie,
///         but it is also accepted in the <c>access_token</c> parameter on some endpoints.
///     </para>
///     <para>
///         This runs before the shared <c>LogSanitizer</c>, which only flattens and truncates text. It has to
///         run first because truncation can hide the tail of a secret rather than removing it.
///     </para>
/// </remarks>
public static class SoundCloudUrlRedactor
{
    /// <summary>
    ///     Query parameters whose values are authorization material and must never be logged.
    /// </summary>
    private static readonly string[] SensitiveQueryKeys =
    [
        "track_authorization",
        "secret_token",
        "access_token",
        "oauth_token",
        "client_secret",
        "authorization"
    ];

    /// <summary>
    ///     Replaces the value of every sensitive query parameter with a redaction marker.
    /// </summary>
    /// <param name="url">The URL to redact. A value that is not a URL is returned as redacted text.</param>
    /// <returns>A string safe to log, or an empty string when there was nothing to redact.</returns>
    public static string Redact(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            // Not a URL. It could still be a bare token pasted into a message, so redact it wholesale rather
            // than log it unchanged.
            return "[redacted]";
        }

        var query = uri.Query;
        if (query.Length <= 1)
        {
            return uri.GetLeftPart(UriPartial.Path);
        }

        var builder = new StringBuilder();
        builder.Append(uri.GetLeftPart(UriPartial.Path));

        var pairs = query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries);
        var seenQuery = false;
        foreach (var pair in pairs)
        {
            if (seenQuery)
            {
                builder.Append('&');
            }

            seenQuery = true;

            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                builder.Append(pair);
                continue;
            }

            var key = pair[..separator];
            builder.Append(key).Append('=');
            builder.Append(IsSensitiveKey(key) ? "[redacted]" : pair[(separator + 1)..]);
        }

        return builder.ToString();
    }

    private static bool IsSensitiveKey(string key)
        => SensitiveQueryKeys.Any(
            sensitive => key.Trim().Equals(sensitive, StringComparison.OrdinalIgnoreCase));
}