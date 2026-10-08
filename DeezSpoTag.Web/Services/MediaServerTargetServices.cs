namespace DeezSpoTag.Web.Services;

/// <summary>
/// The media servers a sync or metadata push can target. This is the same set the Folder tab and
/// the per-surface target checkboxes present, so every surface offers the same servers in the same
/// order and none of them can offer a server the writers would reject as unconfigured.
/// </summary>
public static class MediaServerTargetServices
{
    public const string Plex = "plex";
    public const string Jellyfin = "jellyfin";
    public const string Navidrome = "navidrome";

    /// <summary>Canonical display order.</summary>
    public static IReadOnlyList<string> All { get; } = [Plex, Jellyfin, Navidrome];

    /// <summary>
    /// Whether a service from the configured-server list is a media server this app can target.
    /// The configured list also carries YouTube Music, which is not a media server.
    /// </summary>
    public static bool Contains(string? service)
    {
        var normalized = (service ?? string.Empty).Trim().ToLowerInvariant();
        return All.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    public static string Label(string? service)
        => (service ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            Plex => "Plex",
            Jellyfin => "Jellyfin",
            Navidrome => "Navidrome",
            _ => service ?? string.Empty
        };
}
