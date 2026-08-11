using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.Notifications;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotificationSeverity
{
    Info = 0,
    Warning = 1,
    ActionRequired = 2
}

public static class NotificationKinds
{
    public const string VerificationRequired = "verification_required";
    public const string ArtistNewRelease = "artist_new_release";
    public const string PlaylistUpdated = "playlist_updated";
    public const string DownloadFailed = "download_failed";
    public const string ProviderUnhealthy = "provider_unhealthy";
    public const string RunPaused = "run_paused";
    public const string RunResumed = "run_resumed";
    public const string RunCompleted = "run_completed";
    public const string ProviderRecovered = "provider_recovered";

    public static readonly IReadOnlyList<string> All =
    [
        VerificationRequired,
        ArtistNewRelease,
        PlaylistUpdated,
        DownloadFailed,
        ProviderUnhealthy,
        RunPaused,
        RunResumed,
        RunCompleted,
        ProviderRecovered
    ];

    public static bool IsKnown(string? kind)
        => !string.IsNullOrWhiteSpace(kind)
           && All.Contains(kind.Trim(), StringComparer.OrdinalIgnoreCase);
}

public sealed record NotificationEntry
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string DedupeKey { get; init; }
    public NotificationSeverity Severity { get; init; } = NotificationSeverity.Info;
    public required string Title { get; init; }
    public string Body { get; init; } = string.Empty;
    public string? EntityType { get; init; }
    public string? EntityId { get; init; }
    public string? Link { get; init; }
    public int OccurrenceCount { get; init; } = 1;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadUtc { get; init; }
    public DateTimeOffset? ResolvedUtc { get; init; }
    public bool ManuallyResolved { get; init; }

    [JsonIgnore]
    public bool IsRead => ReadUtc.HasValue;

    /// <summary>
    /// An incident stays open until the condition clears, the user acts on it, or the user reviews
    /// it. Repeats of an open incident are counted, never re-announced.
    /// </summary>
    [JsonIgnore]
    public bool IsOpen => !ResolvedUtc.HasValue;
}

public sealed record NotificationRequest(
    string Kind,
    string Title,
    string Body,
    NotificationSeverity Severity = NotificationSeverity.Info,
    string? DedupeKey = null,
    string? EntityType = null,
    string? EntityId = null,
    string? Link = null);

public sealed class NotificationChannelPreference
{
    public bool InApp { get; set; } = true;
    public bool Webhook { get; set; }
}

public sealed class NotificationPreferences
{
    public Dictionary<string, NotificationChannelPreference> Events { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string WebhookUrl { get; set; } = string.Empty;
    public int RetentionDays { get; set; } = 30;

    public static NotificationPreferences CreateDefault()
    {
        var preferences = new NotificationPreferences();
        preferences.EnsureDefaults();
        return preferences;
    }

    public void EnsureDefaults()
    {
        Events ??= new Dictionary<string, NotificationChannelPreference>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in NotificationKinds.All)
        {
            if (!Events.ContainsKey(kind))
            {
                Events[kind] = new NotificationChannelPreference { InApp = true, Webhook = false };
            }
        }

        foreach (var key in Events.Keys.Where(key => !NotificationKinds.IsKnown(key)).ToList())
        {
            Events.Remove(key);
        }

        if (RetentionDays <= 0)
        {
            RetentionDays = 30;
        }
    }

    public NotificationChannelPreference Resolve(string kind)
        => Events.TryGetValue(kind, out var preference)
            ? preference
            : new NotificationChannelPreference { InApp = true, Webhook = false };
}
