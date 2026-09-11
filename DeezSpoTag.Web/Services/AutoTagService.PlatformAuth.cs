using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Security;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Web.Services.CoverPort;
using DeezSpoTag.Web.Services.AutoTag;

namespace DeezSpoTag.Web.Services;

public partial class AutoTagService
{

    private async Task<IReadOnlyList<string>> ResolveAllowedLibraryRootsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var folders = _libraryRepository.IsConfigured
                ? await _libraryRepository.GetFoldersAsync(cancellationToken)
                : await _activityLog.GetFoldersAsync();
            return LibraryFolderRootResolver.ResolveAccessibleRoots(folders);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return LibraryFolderRootResolver.ResolveAccessibleRoots(await _activityLog.GetFoldersAsync());
        }
    }

    private async Task<Dictionary<string, PlatformTagCapabilities>> LoadPlatformCapabilitiesAsync()
    {
        var result = new Dictionary<string, PlatformTagCapabilities>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = await _metadataService.GetPlatformsJsonAsync();
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            if (JsonNode.Parse(json) is not JsonArray array)
            {
                return result;
            }

            foreach (var node in array.OfType<JsonObject>())
            {
                var id = GetPlatformId(node);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var supportedTags = ReadPlatformList(node, "supportedTags");
                var downloadTags = ReadPlatformList(node, AutoTagLiterals.DownloadTagsKey);
                var requiresAuth = ReadPlatformRequiresAuth(node);

                var normalizedSupported = supportedTags
                    .Select(NormalizeSupportedTagKey)
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(tag => tag!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var tag in downloadTags
                             .Select(NormalizeSupportedTagKey)
                             .Where(tag => !string.IsNullOrWhiteSpace(tag))
                             .Select(tag => tag!))
                {
                    normalizedSupported.Add(tag);
                }

                result[id.Trim()] = new PlatformTagCapabilities(normalizedSupported, requiresAuth);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load AutoTag platform metadata.");
        }

        return result;
    }

    private static string? GetPlatformId(JsonObject node)
    {
        return node["id"]?.GetValue<string>()
            ?? node[AutoTagLiterals.PlatformKey]?["id"]?.GetValue<string>();
    }

    private static List<string> ReadPlatformList(JsonObject node, string key)
    {
        var values = ReadStringList(node, key);
        if (values.Count == 0 && node[AutoTagLiterals.PlatformKey] is JsonObject platformNode)
        {
            values = ReadStringList(platformNode, key);
        }

        return values;
    }

    private static bool ReadPlatformRequiresAuth(JsonObject node)
    {
        return ReadBool(node, "requiresAuth")
            ?? (node[AutoTagLiterals.PlatformKey] is JsonObject platformNode ? ReadBool(platformNode, "requiresAuth") : null)
            ?? false;
    }

    private async Task<List<string>> ResolveEligiblePlatformsAsync(
        JsonObject baseRoot,
        Dictionary<string, PlatformTagCapabilities> platformCaps,
        AutoTagJob job)
    {
        var configured = ReadStringList(baseRoot, AutoTagLiterals.PlatformsKey)
            .Where(platform => !string.IsNullOrWhiteSpace(platform))
            .Select(platform => platform.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (configured.Count == 0)
        {
            return new List<string>();
        }

        var candidates = configured;

        if (candidates.Count == 0)
        {
            return candidates;
        }

        PlatformAuthState? authState = null;
        try
        {
            authState = await _platformAuthService.LoadAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load platform auth state while filtering AutoTag platforms.");
        }

        var removedUnauthenticated = new List<string>();
        var eligible = new List<string>();

        foreach (var platform in candidates)
        {
            if (!RequiresPlatformAuth(platform, platformCaps))
            {
                eligible.Add(platform);
                continue;
            }

            if (IsPlatformAuthenticated(platform, authState))
            {
                eligible.Add(platform);
                continue;
            }

            removedUnauthenticated.Add(platform);
        }

        if (removedUnauthenticated.Count > 0)
        {
            AppendLog(job, $"platform filter: excluded unauthenticated platforms ({string.Join(", ", removedUnauthenticated)})");
        }

        return eligible;
    }

    private static bool RequiresPlatformAuth(string platformId, Dictionary<string, PlatformTagCapabilities> platformCaps)
    {
        if (string.IsNullOrWhiteSpace(platformId))
        {
            return false;
        }

        if (platformCaps.TryGetValue(platformId.Trim(), out var caps))
        {
            return caps.RequiresAuth;
        }

        return platformId.Trim().ToLowerInvariant() switch
        {
            AutoTagLiterals.SpotifySource => true,
            AutoTagLiterals.DiscogsPlatform => true,
            AutoTagLiterals.LastFmPlatform => true,
            AutoTagLiterals.BpmSupremePlatform => true,
            AutoTagLiterals.AppleMusicPlatform => true,
            AutoTagLiterals.PlexPlatform => true,
            AutoTagLiterals.JellyfinPlatform => true,
            _ => false
        };
    }

    private static bool IsPlatformAuthenticated(string platformId, PlatformAuthState? state)
    {
        var key = platformId.Trim().ToLowerInvariant();
        return key switch
        {
            AutoTagLiterals.SpotifySource => IsSpotifyAuthenticated(state?.Spotify),
            AutoTagLiterals.DiscogsPlatform => !string.IsNullOrWhiteSpace(state?.Discogs?.Token),
            AutoTagLiterals.LastFmPlatform => !string.IsNullOrWhiteSpace(state?.LastFm?.ApiKey),
            AutoTagLiterals.BpmSupremePlatform => HasBpmSupremeCredentials(state?.BpmSupreme),
            AutoTagLiterals.AppleMusicPlatform => state?.AppleMusic?.WrapperReady == true,
            AutoTagLiterals.ITunesPlatform => state?.AppleMusic?.WrapperReady == true,
            AutoTagLiterals.PlexPlatform => IsPlexAuthenticated(state?.Plex),
            AutoTagLiterals.JellyfinPlatform => IsJellyfinAuthenticated(state?.Jellyfin),
            _ => false
        };
    }

    private static bool IsSpotifyAuthenticated(SpotifyConfig? spotify)
    {
        if (spotify == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(spotify.ActiveAccount))
        {
            var active = spotify.Accounts.FirstOrDefault(account =>
                account.Name.Equals(spotify.ActiveAccount, StringComparison.OrdinalIgnoreCase));
            if (active != null && !string.IsNullOrWhiteSpace(active.BlobPath) && File.Exists(active.BlobPath))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(spotify.WebPlayerSpDc))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(spotify.ClientId) &&
            !string.IsNullOrWhiteSpace(spotify.ClientSecret);
    }

    private static bool HasBpmSupremeCredentials(BpmSupremeAuth? bpmSupreme)
    {
        var email = bpmSupreme?.Email;
        var password = bpmSupreme?.Password;
        return !string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(password);
    }

    private static bool IsPlexAuthenticated(PlexAuth? plex)
    {
        return plex is not null &&
            !string.IsNullOrWhiteSpace(plex.Url) &&
            !string.IsNullOrWhiteSpace(plex.Token);
    }

    private static bool IsJellyfinAuthenticated(JellyfinAuth? jellyfin)
    {
        return jellyfin is not null &&
            !string.IsNullOrWhiteSpace(jellyfin.Url) &&
            (!string.IsNullOrWhiteSpace(jellyfin.ApiKey) ||
             !string.IsNullOrWhiteSpace(jellyfin.Username));
    }

    private static string? ResolveDownloadSourcePlatform(JsonObject root)
    {
        if (!root.TryGetPropertyValue(AutoTagLiterals.DownloadTagSourceKey, out var sourceNode) || sourceNode is null)
        {
            return null;
        }

        if (sourceNode is not JsonValue sourceValue || !sourceValue.TryGetValue<string>(out var rawSource))
        {
            return null;
        }

        return NormalizeDownloadTagSource(rawSource) switch
        {
            AutoTagLiterals.DeezerSource => AutoTagLiterals.DeezerSource,
            AutoTagLiterals.SpotifySource => AutoTagLiterals.SpotifySource,
            _ => null
        };
    }

    private async Task<bool> TriggerPlexScanAfterMoveAsync(AutoTagJob job, CancellationToken cancellationToken)
    {
        var plex = await LoadConfiguredPlexForScanAsync(job);
        if (plex == null)
        {
            return false;
        }

        return await TriggerPlexScanAsync(job, plex, "after auto-move", cancellationToken);
    }

    private async Task<bool> TriggerPlexScanAsync(
        AutoTagJob job,
        PlexAuth plex,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var plexUrl = plex.Url;
            var plexToken = plex.Token;
            if (string.IsNullOrWhiteSpace(plexUrl) || string.IsNullOrWhiteSpace(plexToken))
            {
                return false;
            }

            AppendLog(job, $"plex scan starting {reason}");

            var sections = await _plexApiClient.GetLibrarySectionsAsync(plexUrl, plexToken, cancellationToken);
            var musicSections = sections
                .Where(section => string.Equals(section.Type, AutoTagLiterals.ArtistTag, StringComparison.OrdinalIgnoreCase))
                .Where(section => !section.Title.Contains("audiobook", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (musicSections.Count == 0)
            {
                AppendLog(job, "plex scan skipped: no music libraries found");
                return false;
            }

            var refreshed = 0;
            foreach (var section in musicSections)
            {
                refreshed += await _plexApiClient.RefreshLibraryAsync(plexUrl, plexToken, section.Key, cancellationToken) ? 1 : 0;
            }

            AppendLog(job, $"plex scan requested: {musicSections.Count} libraries (refreshed={refreshed})");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag job {JobId}: Plex scan {Reason} failed.", job.Id, reason);
            AppendLog(job, $"plex scan failed: {ex.Message}");
            return false;
        }
    }

    private async Task<string> InjectPlatformDefaultsAsync(string configJson)
    {
        try
        {
            var platformsJson = await _metadataService.GetPlatformsJsonAsync();
            if (string.IsNullOrWhiteSpace(platformsJson))
            {
                return configJson;
            }

            var platformDoc = JsonNode.Parse(platformsJson) as JsonArray;
            if (platformDoc == null)
            {
                return configJson;
            }

            var node = JsonNode.Parse(configJson) as JsonObject;
            if (node == null)
            {
                return configJson;
            }

            var custom = GetOrCreateCustomNode(node);

            foreach (var entry in platformDoc)
            {
                if (entry is not JsonObject platform || !TryGetPlatformOptionDefaults(platform, out var platformId, out var customOptions))
                {
                    continue;
                }

                var platformCustom = GetOrCreatePlatformCustomNode(custom, platformId);

                foreach (var optionNode in customOptions)
                {
                    TryApplyPlatformOptionDefault(platformCustom, optionNode);
                }
            }

            return node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return configJson;
        }
    }

    private static bool TryGetPlatformOptionDefaults(
        JsonObject platform,
        out string platformId,
        out JsonArray customOptions)
    {
        var platformInfo = platform[AutoTagLiterals.PlatformKey] as JsonObject ?? platform;
        platformId = platform["id"]?.GetValue<string>() ?? platformInfo["id"]?.GetValue<string>() ?? string.Empty;
        customOptions = platformInfo["customOptions"]?["options"] as JsonArray ?? new JsonArray();
        return !string.IsNullOrWhiteSpace(platformId) && customOptions.Count > 0;
    }

    private static void TryApplyPlatformOptionDefault(JsonObject platformCustom, JsonNode? optionNode)
    {
        if (optionNode is not JsonObject option)
        {
            return;
        }

        var optionId = option["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(optionId) || platformCustom[optionId] != null)
        {
            return;
        }

        var value = option["value"]?["value"];
        if (value != null)
        {
            platformCustom[optionId] = value.DeepClone();
        }
    }

    private async Task<string> InjectPlatformAuthAsync(string configJson)
    {
        try
        {
            var state = await _platformAuthService.LoadAsync();
            if (state == null)
            {
                return configJson;
            }

            var node = JsonNode.Parse(configJson) as JsonObject;
            if (node == null)
            {
                return configJson;
            }

            var custom = GetOrCreateCustomNode(node);
            ApplyDiscogsAuthDefaults(custom, state.Discogs);
            ApplyLastFmAuthDefaults(custom, state.LastFm);
            ApplyBpmSupremeAuthDefaults(custom, state.BpmSupreme);

            return node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return configJson;
        }
    }

    private static bool HasShazamReviewCandidate(TaggingStatus status)
    {
        return !string.IsNullOrWhiteSpace(status.CandidateTitle)
            || !string.IsNullOrWhiteSpace(status.CandidateArtist)
            || !string.IsNullOrWhiteSpace(status.CandidateIsrc)
            || status.CandidateDurationSeconds.HasValue;
    }

    private void TrackStartedPlatform(AutoTagJob job, string line)
    {
        var cleaned = AnsiRegex.Replace(line, string.Empty).Trim();
        cleaned = StripLinePrefix(cleaned);
        if (!TryExtractStartedPlatform(cleaned, out var platform))
        {
            return;
        }

        lock (job.Logs)
        {
            if (!job.StartedPlatforms.Any(p => string.Equals(p, platform, StringComparison.OrdinalIgnoreCase)))
            {
                job.StartedPlatforms.Add(platform);
                SaveJob(job);
            }
        }
    }

    private static bool TryExtractStartedPlatform(string line, out string platform)
    {
        platform = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        const string marker = AutoTagProtocol.LogMarker;
        var markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var message = line[(markerIndex + marker.Length)..].TrimStart();
        const string starting = AutoTagProtocol.StartingPlatformMessage;
        if (!message.StartsWith(starting, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = message[starting.Length..].Trim();
        if (rest.StartsWith("tagger", StringComparison.OrdinalIgnoreCase) ||
            rest.StartsWith("tagging", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(rest))
        {
            return false;
        }

        platform = rest;
        return true;
    }
}
