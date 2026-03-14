using System.Text;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Services;

internal static class SpotifyCredentialParser
{
    private static readonly Regex SpotifyClientIdPattern = new(
        "^[a-f0-9]{32}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(250));

    public static (string? ClientId, string? ClientSecret) ParseClientCredentials(string? rawClientId, string? rawClientSecret)
    {
        if (string.IsNullOrWhiteSpace(rawClientId) || string.IsNullOrWhiteSpace(rawClientSecret))
        {
            return (rawClientId?.Trim(), rawClientSecret?.Trim());
        }

        var clientId = rawClientId.Trim();
        var clientSecret = rawClientSecret.Trim();

        // Only decode legacy base64 values if the decoded clientId matches the canonical Spotify client id format.
        // This avoids falsely decoding normal secrets that happen to be valid base64 strings.
        var decodedClientId = TryDecodeBase64Utf8(clientId)?.Trim();
        if (string.IsNullOrWhiteSpace(decodedClientId) || !SpotifyClientIdPattern.IsMatch(decodedClientId))
        {
            return (clientId, clientSecret);
        }

        var decodedSecret = TryDecodeBase64Utf8(clientSecret)?.Trim();
        if (string.IsNullOrWhiteSpace(decodedSecret))
        {
            return (decodedClientId, clientSecret);
        }

        return (decodedClientId, decodedSecret);
    }

    private static string? TryDecodeBase64Utf8(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return null;
        }
    }
}
