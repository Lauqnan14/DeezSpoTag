using System.Globalization;
using System.Net;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public sealed record QobuzAccountProfile(
    string? UserId,
    string? DisplayName,
    string? Country,
    string? Zone,
    string? CredentialLabel,
    string? SubscriptionOffer);

public enum QobuzAccountProfileStatus
{
    Valid,
    InvalidToken,
    Unavailable
}

public sealed record QobuzAccountProfileResult(
    QobuzAccountProfileStatus Status,
    QobuzAccountProfile? Profile,
    string? Error)
{
    public bool IsValid => Status == QobuzAccountProfileStatus.Valid;
}

public sealed class QobuzAccountProfileService
{
    private const string UserLoginEndpoint = "https://www.qobuz.com/api.json/0.2/user/login";
    private readonly HttpClient _httpClient;
    private readonly ILogger<QobuzAccountProfileService> _logger;

    public QobuzAccountProfileService(
        HttpClient httpClient,
        ILogger<QobuzAccountProfileService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<QobuzAccountProfileResult> FetchAsync(
        string appId,
        string authToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(authToken))
        {
            return new QobuzAccountProfileResult(
                QobuzAccountProfileStatus.InvalidToken,
                null,
                "Qobuz App ID and User Auth Token are required.");
        }

        var url = $"{UserLoginEndpoint}?app_id={Uri.EscapeDataString(appId.Trim())}&extra=partner";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-App-Id", appId.Trim());
        request.Headers.TryAddWithoutValidation("X-User-Auth-Token", authToken.Trim());

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                return new QobuzAccountProfileResult(
                    QobuzAccountProfileStatus.InvalidToken,
                    null,
                    "Qobuz User Auth Token is invalid or expired.");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Qobuz account profile request failed with HTTP {StatusCode}.",
                    (int)response.StatusCode);
                return new QobuzAccountProfileResult(
                    QobuzAccountProfileStatus.Unavailable,
                    null,
                    $"Qobuz account lookup returned HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("user", out var user)
                || user.ValueKind != JsonValueKind.Object)
            {
                return new QobuzAccountProfileResult(
                    QobuzAccountProfileStatus.Unavailable,
                    null,
                    "Qobuz account response did not contain a user profile.");
            }

            var profile = new QobuzAccountProfile(
                ReadScalarAsString(user, "id"),
                ReadFirstString(user, "display_name", "login", "email"),
                ReadString(user, "country"),
                ReadString(user, "zone"),
                ReadNestedString(user, "credential", "parameters", "short_label"),
                ReadNestedString(user, "subscription", "offer"));
            return new QobuzAccountProfileResult(QobuzAccountProfileStatus.Valid, profile, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Qobuz account profile request failed.");
            return new QobuzAccountProfileResult(
                QobuzAccountProfileStatus.Unavailable,
                null,
                "Qobuz account lookup failed.");
        }
    }

    private static string? ReadFirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = ReadString(element, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static string? ReadScalarAsString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var number)
                ? number.ToString(CultureInfo.InvariantCulture)
                : value.GetRawText(),
            _ => null
        };
    }

    private static string? ReadNestedString(JsonElement element, params string[] propertyPath)
    {
        var current = element;
        foreach (var propertyName in propertyPath)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(propertyName, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
