using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/media-server")]
[Authorize]
public class MediaServerScanApiController : ControllerBase
{
    private readonly PlatformAuthService _authService;
    private readonly PlexApiClient _plexApiClient;
    private readonly JellyfinApiClient _jellyfinApiClient;

    public MediaServerScanApiController(
        PlatformAuthService authService,
        PlexApiClient plexApiClient,
        JellyfinApiClient jellyfinApiClient)
    {
        _authService = authService;
        _plexApiClient = plexApiClient;
        _jellyfinApiClient = jellyfinApiClient;
    }

    [HttpPost("scan")]
    public async Task<IActionResult> Scan(CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        var plex = state.Plex;
        var jellyfin = state.Jellyfin;
        if (!HasConfiguredServer(plex?.Url, plex?.Token) && !HasConfiguredServer(jellyfin?.Url, jellyfin?.ApiKey))
        {
            return BadRequest("No media server is configured.");
        }

        var refreshed = 0;
        var messages = new List<string>();
        if (HasConfiguredServer(plex?.Url, plex?.Token))
        {
            var plexUrl = plex!.Url!;
            var plexToken = plex.Token!;
            var plexResult = await RefreshPlexAsync(plexUrl, plexToken, cancellationToken);
            refreshed += plexResult.Refreshed;
            if (!string.IsNullOrWhiteSpace(plexResult.Message))
            {
                messages.Add(plexResult.Message);
            }
        }

        if (HasConfiguredServer(jellyfin?.Url, jellyfin?.ApiKey))
        {
            var jellyfinUrl = jellyfin!.Url!;
            var jellyfinApiKey = jellyfin.ApiKey!;
            messages.Add(await RefreshJellyfinMessageAsync(jellyfinUrl, jellyfinApiKey, cancellationToken));
        }

        if (refreshed == 0 && messages.Count == 0)
        {
            return Ok(new { success = false, refreshed = 0, message = "No libraries refreshed." });
        }

        return Ok(new { success = true, refreshed, message = string.Join(' ', messages) });
    }

    private static bool HasConfiguredServer(string? url, string? tokenOrKey)
        => !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(tokenOrKey);

    private async Task<(int Refreshed, string Message)> RefreshPlexAsync(
        string plexUrl,
        string plexToken,
        CancellationToken cancellationToken)
    {
        var sections = await _plexApiClient.GetLibrarySectionsAsync(plexUrl, plexToken, cancellationToken);
        var musicSections = sections
            .Where(section => string.Equals(section.Type, "artist", StringComparison.OrdinalIgnoreCase))
            .Where(section => !section.Title.Contains("audiobook", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (musicSections.Count == 0)
        {
            return (0, "No music libraries found in Plex.");
        }

        var refreshed = 0;
        foreach (var section in musicSections)
        {
            if (await _plexApiClient.RefreshLibraryAsync(plexUrl, plexToken, section.Key, cancellationToken))
            {
                refreshed++;
            }
        }

        return (refreshed, $"Plex refresh requested for {musicSections.Count} libraries.");
    }

    private async Task<string> RefreshJellyfinMessageAsync(string jellyfinUrl, string jellyfinApiKey, CancellationToken cancellationToken)
    {
        var jellyfinOk = await _jellyfinApiClient.RefreshLibraryAsync(jellyfinUrl, jellyfinApiKey, cancellationToken);
        return jellyfinOk
            ? "Jellyfin refresh requested."
            : "Jellyfin refresh failed.";
    }
}
