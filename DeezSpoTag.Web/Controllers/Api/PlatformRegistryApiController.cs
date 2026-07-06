using System.Text.Json;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/platform-registry")]
[Authorize]
public class PlatformRegistryApiController : ControllerBase
{
    private const string LastFmPlatform = "lastfm";
    private const string AppleMusicPlatform = "applemusic";
    private const string AmazonMusicPlatform = "amazonmusic";
    private const string BpmSupremePlatform = "bpmsupreme";
    private const string QobuzPlatform = "qobuz";
    private const string TidalPlatform = "tidal";
    private const string SoulseekPlatform = "soulseek";
    private const string BoomplayPlatform = "boomplay";
    private const string NavidromePlatform = "navidrome";

    private static readonly string[] SidebarOrder =
    [
        "deezer",
        "spotify",
        AppleMusicPlatform,
        AmazonMusicPlatform,
        QobuzPlatform,
        TidalPlatform,
        SoulseekPlatform,
        "discogs",
        LastFmPlatform,
        BpmSupremePlatform,
        BoomplayPlatform,
        "plex",
        "jellyfin",
        NavidromePlatform,
        "beatport",
        "traxsource",
        "junodownload",
        "musicbrainz",
        "itunes",
        "bandcamp",
        "musixmatch",
        "shazam"
    ];

    private static readonly Dictionary<string, int> SidebarOrderIndex = SidebarOrder
        .Select((id, index) => new { id, index })
        .ToDictionary(item => item.id, item => item.index, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AuthRequiredPlatforms = new(StringComparer.OrdinalIgnoreCase)
    {
        "deezer",
        "spotify",
        LastFmPlatform,
        "discogs",
        BpmSupremePlatform,
        "plex",
        "jellyfin",
        NavidromePlatform,
        AppleMusicPlatform,
        AmazonMusicPlatform,
        QobuzPlatform,
        TidalPlatform,
        SoulseekPlatform,
        BoomplayPlatform
    };

    private static readonly Dictionary<string, string> LoginTabMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deezer"] = "deezer-login",
        ["spotify"] = "spotify-login",
        [AppleMusicPlatform] = "apple-music-login",
        [AmazonMusicPlatform] = "amazon-music-login",
        [QobuzPlatform] = "qobuz-login",
        [TidalPlatform] = "tidal-login",
        [SoulseekPlatform] = "soulseek-login",
        ["discogs"] = "discogs-login",
        [LastFmPlatform] = "lastfm-login",
        [BpmSupremePlatform] = "bpmsupreme-login",
        [BoomplayPlatform] = "boomplay-login",
        ["plex"] = "plex-login",
        ["jellyfin"] = "jellyfin-login",
        [NavidromePlatform] = "navidrome-login"
    };

    private static readonly Dictionary<string, string> DisplayNameOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        [LastFmPlatform] = "Last.fm",
        [BpmSupremePlatform] = "BPM Supreme",
        ["junodownload"] = "Juno Download",
        ["musicbrainz"] = "MusicBrainz",
        ["itunes"] = "iTunes",
        [AppleMusicPlatform] = "Apple Music",
        [AmazonMusicPlatform] = "Amazon Music",
        [QobuzPlatform] = "Qobuz",
        [TidalPlatform] = "Tidal",
        [SoulseekPlatform] = "Soulseek",
        [BoomplayPlatform] = "Boomplay",
        [NavidromePlatform] = "Navidrome"
    };

    private readonly AutoTagMetadataService _metadataService;
    private readonly ILogger<PlatformRegistryApiController> _logger;

    public PlatformRegistryApiController(
        AutoTagMetadataService metadataService,
        ILogger<PlatformRegistryApiController> logger)
    {
        _metadataService = metadataService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var entries = await BuildEntriesFromMetadataAsync();
        AddMissingLoginPlatforms(entries);
        return Ok(OrderEntries(entries));
    }

    private async Task<Dictionary<string, PlatformRegistryEntry>> BuildEntriesFromMetadataAsync()
    {
        var entries = new Dictionary<string, PlatformRegistryEntry>(StringComparer.OrdinalIgnoreCase);
        var platformsJson = await _metadataService.GetPlatformsJsonAsync();
        if (string.IsNullOrWhiteSpace(platformsJson))
        {
            return entries;
        }

        try
        {
            AddMetadataEntries(entries, platformsJson);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to parse /api/autotag/platforms while building unified platform registry.");
        }

        return entries;
    }

    private static void AddMetadataEntries(
        Dictionary<string, PlatformRegistryEntry> entries,
        string platformsJson)
    {
        using var document = JsonDocument.Parse(platformsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in document.RootElement.EnumerateArray().Select(CreateEntryOrNull).Where(static entry => entry is not null))
        {
            entries[entry!.Id] = entry;
        }
    }

    private static PlatformRegistryEntry? CreateEntryOrNull(JsonElement element)
    {
        var id = ReadString(element, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var normalizedId = NormalizeId(id);
        var platformName = TryReadPlatformName(element);
        return CreateEntry(normalizedId, platformName);
    }

    private static string? TryReadPlatformName(JsonElement element)
    {
        var platformObject = element.TryGetProperty("platform", out var platformValue)
            ? platformValue
            : default;
        return platformObject.ValueKind == JsonValueKind.Object
            ? ReadString(platformObject, "name")
            : null;
    }

    private static PlatformRegistryEntry CreateEntry(string platformId, string? suggestedName)
    {
        return new PlatformRegistryEntry
        {
            Id = platformId,
            Name = ResolveDisplayName(platformId, suggestedName),
            Icon = ResolveIconPath(platformId),
            RequiresAuth = AuthRequiredPlatforms.Contains(platformId),
            LoginTabId = LoginTabMap.TryGetValue(platformId, out var tabId) ? tabId : null
        };
    }

    private static void AddMissingLoginPlatforms(Dictionary<string, PlatformRegistryEntry> entries)
    {
        foreach (var loginPlatform in LoginTabMap.Keys.Where(loginPlatform => !entries.ContainsKey(loginPlatform)))
        {
            entries[loginPlatform] = new PlatformRegistryEntry
            {
                Id = loginPlatform,
                Name = ResolveDisplayName(loginPlatform, null),
                Icon = ResolveIconPath(loginPlatform),
                RequiresAuth = true,
                LoginTabId = LoginTabMap[loginPlatform]
            };
        }
    }

    private static List<PlatformRegistryEntry> OrderEntries(Dictionary<string, PlatformRegistryEntry> entries)
    {
        return entries.Values
            .OrderBy(entry => SidebarOrderIndex.TryGetValue(entry.Id, out var index) ? index : int.MaxValue)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeId(string id) => id.Trim().ToLowerInvariant();

    private static string ResolveDisplayName(string platformId, string? suggested)
    {
        if (!string.IsNullOrWhiteSpace(suggested))
        {
            return suggested.Trim();
        }

        if (DisplayNameOverrides.TryGetValue(platformId, out var overrideName))
        {
            return overrideName;
        }

        if (string.IsNullOrWhiteSpace(platformId))
        {
            return "Unknown";
        }

        return char.ToUpperInvariant(platformId[0]) + platformId[1..];
    }

    private static string ResolveIconPath(string platformId)
    {
        if (string.Equals(platformId, LastFmPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return "/images/icons/last-fm.png";
        }
        if (string.Equals(platformId, AppleMusicPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return "/images/icons/apple-music.png";
        }
        if (string.Equals(platformId, AmazonMusicPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return "/images/icons/amazon-music.png";
        }
        if (string.Equals(platformId, QobuzPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return "/images/icons/qobuz.png";
        }

        return $"/images/icons/{platformId}.png";
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return value.GetString();
    }

    public sealed class PlatformRegistryEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public bool RequiresAuth { get; set; }
        public string? LoginTabId { get; set; }
    }
}
