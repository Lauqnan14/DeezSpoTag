using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.Notifications;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotificationTransportProvider
{
    [JsonStringEnumMemberName("apprise")]
    Apprise = 0,

    [JsonStringEnumMemberName("genericWebhook")]
    GenericWebhook = 1
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApprisePayloadMode
{
    [JsonStringEnumMemberName("universalCompatibility")]
    UniversalCompatibility = 0,

    [JsonStringEnumMemberName("nativeTitleBody")]
    NativeTitleBody = 1
}

public static class NotificationTransportAdapter
{
    public const string AppriseProviderValue = "apprise";
    public const string GenericWebhookProviderValue = "genericWebhook";
    public const string UniversalCompatibilityModeValue = "universalCompatibility";
    public const string NativeTitleBodyModeValue = "nativeTitleBody";

    public static object BuildPayload(NotificationEntry entry, NotificationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(preferences);

        var type = ResolveType(entry);
        var body = ResolveBody(entry);
        if (preferences.ResolvedProvider == NotificationTransportProvider.Apprise)
        {
            // WhatsApp (and any Apprise service with title_maxlen=0) only delivers `body`.
            // A separate title is ignored, and a non-empty title plus body can fail template
            // substitution. Always render title into body; native mode also sends type.
            var merged = RenderUniversalBody(entry.Title, entry.Body);
            if (preferences.ResolvedApprisePayloadMode == ApprisePayloadMode.NativeTitleBody)
            {
                return new
                {
                    body = merged,
                    type
                };
            }

            return new
            {
                body = merged
            };
        }

        return new
        {
            title = entry.Title,
            body,
            type,
            id = entry.Id,
            kind = entry.Kind,
            severity = entry.Severity.ToString(),
            entityType = entry.EntityType,
            entityId = entry.EntityId,
            link = entry.Link,
            occurrenceCount = entry.OccurrenceCount,
            timestamp = entry.LastSeenUtc
        };
    }

    public static string RenderUniversalBody(string? title, string? body)
    {
        var trimmedTitle = (title ?? string.Empty).Trim();
        var trimmedBody = (body ?? string.Empty).Trim();
        var boldTitle = string.IsNullOrEmpty(trimmedTitle) ? string.Empty : $"*{trimmedTitle}*";
        if (string.IsNullOrEmpty(trimmedBody) || string.Equals(trimmedBody, trimmedTitle, StringComparison.Ordinal))
        {
            return boldTitle;
        }

        if (string.IsNullOrEmpty(boldTitle))
        {
            return trimmedBody;
        }

        return $"{boldTitle} {trimmedBody}";
    }

    public static string ResolveType(NotificationEntry entry)
    {
        if (string.Equals(entry.Kind, NotificationKinds.DownloadFailed, StringComparison.OrdinalIgnoreCase))
        {
            return "failure";
        }

        if (string.Equals(entry.Kind, NotificationKinds.RunCompleted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Kind, NotificationKinds.ProviderRecovered, StringComparison.OrdinalIgnoreCase))
        {
            return "success";
        }

        return entry.Severity switch
        {
            NotificationSeverity.Warning => "warning",
            NotificationSeverity.ActionRequired => "warning",
            _ => "info"
        };
    }

    public static string ResolveProviderValue(NotificationTransportProvider provider)
        => provider == NotificationTransportProvider.GenericWebhook
            ? GenericWebhookProviderValue
            : AppriseProviderValue;

    public static string ResolvePayloadModeValue(ApprisePayloadMode mode)
        => mode == ApprisePayloadMode.NativeTitleBody
            ? NativeTitleBodyModeValue
            : UniversalCompatibilityModeValue;

    public static NotificationTransportProvider ParseProvider(string? value, NotificationTransportProvider fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var token = value.Trim().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        if (token.Equals("apprise", StringComparison.OrdinalIgnoreCase))
        {
            return NotificationTransportProvider.Apprise;
        }

        if (token.Equals("genericwebhook", StringComparison.OrdinalIgnoreCase)
            || token.Equals("generic", StringComparison.OrdinalIgnoreCase)
            || token.Equals("webhook", StringComparison.OrdinalIgnoreCase))
        {
            return NotificationTransportProvider.GenericWebhook;
        }

        return fallback;
    }

    public static ApprisePayloadMode ParsePayloadMode(string? value, ApprisePayloadMode fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var token = value.Trim().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        if (token.Equals("nativetitlebody", StringComparison.OrdinalIgnoreCase)
            || token.Equals("native", StringComparison.OrdinalIgnoreCase))
        {
            return ApprisePayloadMode.NativeTitleBody;
        }

        if (token.Equals("universalcompatibility", StringComparison.OrdinalIgnoreCase)
            || token.Equals("universal", StringComparison.OrdinalIgnoreCase)
            || token.Equals("compatibility", StringComparison.OrdinalIgnoreCase))
        {
            return ApprisePayloadMode.UniversalCompatibility;
        }

        return fallback;
    }

    private static string ResolveBody(NotificationEntry entry)
        => string.IsNullOrWhiteSpace(entry.Body) ? entry.Title : entry.Body;
}
