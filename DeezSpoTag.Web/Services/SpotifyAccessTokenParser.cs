using System.Text;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public static class SpotifyAccessTokenParser
{
    public static SpotifyAccessTokenClaims? TryParse(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var parts = accessToken.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = DecodeBase64Url(parts[1]);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            var subject = TryGetString(root, "sub")
                ?? TryGetString(root, "user_id")
                ?? TryGetString(root, "username");
            var country = TryGetString(root, "country")
                ?? TryGetString(root, "region");
            var product = TryGetString(root, "product")
                ?? TryGetString(root, "account_type");
            var displayName = TryGetString(root, "name")
                ?? TryGetString(root, "display_name")
                ?? TryGetString(root, "displayName");

            return new SpotifyAccessTokenClaims(subject, country, product, displayName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2:
                normalized += "==";
                break;
            case 3:
                normalized += "=";
                break;
        }

        var bytes = Convert.FromBase64String(normalized);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string? TryGetString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}

public sealed record SpotifyAccessTokenClaims(
    string? Subject,
    string? Country,
    string? Product,
    string? DisplayName);
