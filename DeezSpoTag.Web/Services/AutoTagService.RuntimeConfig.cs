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

    private static string SanitizeConfigJson(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return configJson;
        }

        try
        {
            var node = JsonNode.Parse(configJson);
            if (node == null)
            {
                return configJson;
            }
            RemoveNulls(node);
            EnsureEffectivePlatforms(node);
            EnsureSupportedDownloadTagSource(node);
            EnsureOverwriteDefaults(node);
            EnsureTracknameTemplateCanonical(node);
            EnsureEnhancementFolderScopesCanonical(node);
            EnsureLegacyFolderUniformityStructureMirrorsRemoved(node);
            EnsureLegacyOrganizerConfigRemoved(node);
            EnsureLegacyDeezerAuthRemoved(node);
            EnsureSpotifySecret(node);
            return node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return configJson;
        }
    }

    private static void EnsureEffectivePlatforms(JsonNode node)
    {
        if (node is not JsonObject root)
        {
            return;
        }

        var platforms = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root[AutoTagLiterals.PlatformsKey] is JsonArray platformArray)
        {
            foreach (var platformId in platformArray
                         .Select(static entry => entry?.GetValue<string>()?.Trim())
                         .Where(static id => !string.IsNullOrWhiteSpace(id))
                         .Cast<string>()
                         .Where(seen.Add))
            {
                platforms.Add(platformId);
            }
        }

        var normalized = new JsonArray();
        foreach (var platform in platforms)
        {
            normalized.Add(platform);
        }

        root[AutoTagLiterals.PlatformsKey] = normalized;
        root[AutoTagLiterals.MultiPlatformKey] = platforms.Count > 1;
        EnsureShazamFlagsFollowPlatforms(root, platforms);
    }

    private static void EnsureShazamFlagsFollowPlatforms(JsonObject root, IReadOnlyCollection<string> platforms)
    {
        var shazamEnabled = platforms.Any(platform => string.Equals(platform, "shazam", StringComparison.OrdinalIgnoreCase));
        root["enableShazam"] = shazamEnabled;
        if (!shazamEnabled)
        {
            root["forceShazam"] = false;
        }
    }

    private static void EnsureTracknameTemplateCanonical(JsonNode node)
    {
        if (node is not JsonObject root)
        {
            return;
        }

        root.Remove("filenameTemplate");
        root.Remove("albumTracknameTemplate");
        root.Remove("playlistTracknameTemplate");
    }

    private static void EnsureSupportedDownloadTagSource(JsonNode node)
    {
        if (node is not JsonObject root)
        {
            return;
        }

        if (!root.TryGetPropertyValue(AutoTagLiterals.DownloadTagSourceKey, out var sourceNode) || sourceNode is null)
        {
            return;
        }

        if (sourceNode is JsonValue sourceValue && sourceValue.TryGetValue<string>(out var rawSource))
        {
            root[AutoTagLiterals.DownloadTagSourceKey] = NormalizeDownloadTagSource(rawSource);
            return;
        }

        root[AutoTagLiterals.DownloadTagSourceKey] = AutoTagLiterals.DeezerSource;
    }

    private static void EnsureOverwriteDefaults(JsonNode node)
    {
        if (node is not JsonObject root)
        {
            return;
        }

        if (!root.TryGetPropertyValue(AutoTagLiterals.OverwriteKey, out var overwriteNode)
            || overwriteNode is not JsonValue overwriteValue
            || !overwriteValue.TryGetValue<bool>(out _))
        {
            root[AutoTagLiterals.OverwriteKey] = false;
        }

        if (!root.TryGetPropertyValue(AutoTagLiterals.OverwriteTagsKey, out var overwriteTagsNode) || overwriteTagsNode is not JsonArray overwriteArray)
        {
            root[AutoTagLiterals.OverwriteTagsKey] = new JsonArray();
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new JsonArray();
        foreach (var entry in overwriteArray)
        {
            if (entry is not JsonValue value || !value.TryGetValue<string>(out var tag) || string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            var trimmed = tag.Trim();
            if (!seen.Add(trimmed))
            {
                continue;
            }

            normalized.Add(trimmed);
        }

        root[AutoTagLiterals.OverwriteTagsKey] = normalized;
    }

    private static void EnsureEnhancementFolderScopesCanonical(JsonNode node)
    {
        if (node is not JsonObject root
            || root[AutoTagLiterals.EnhancementStage] is not JsonObject enhancement)
        {
            return;
        }

        CanonicalizeEnhancementFolderScopeSection(enhancement, "folderUniformity");
        CanonicalizeEnhancementFolderScopeSection(enhancement, "coverMaintenance");
        CanonicalizeEnhancementFolderScopeSection(enhancement, "qualityChecks");
        EnhancementWorkflowSelection.CanonicalizeSidecars(enhancement);
        CanonicalizeEnhancementFolderScopeSection(enhancement, "sidecars");
    }

    private static void CanonicalizeEnhancementFolderScopeSection(JsonObject enhancement, string sectionName)
    {
        if (enhancement[sectionName] is not JsonObject section)
        {
            return;
        }

        var folderIds = ParseFolderIds(section, "folderIds");
        if (folderIds.Count == 0 && TryParseLegacyFolderId(section["folderId"], out var legacyFolderId))
        {
            folderIds.Add(legacyFolderId);
        }

        var normalized = new JsonArray();
        foreach (var folderId in folderIds.Distinct())
        {
            normalized.Add(folderId);
        }

        section["folderIds"] = normalized;
        section.Remove("folderId");
    }

    private static bool TryParseLegacyFolderId(JsonNode? folderIdNode, out long folderId)
    {
        folderId = 0;
        if (folderIdNode is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue<long>(out var longValue) && longValue > 0)
        {
            folderId = longValue;
            return true;
        }

        if (value.TryGetValue<int>(out var intValue) && intValue > 0)
        {
            folderId = intValue;
            return true;
        }

        if (value.TryGetValue<string>(out var stringValue)
            && long.TryParse(stringValue, out var parsedValue)
            && parsedValue > 0)
        {
            folderId = parsedValue;
            return true;
        }

        return false;
    }

    private static void EnsureLegacyFolderUniformityStructureMirrorsRemoved(JsonNode node)
    {
        if (node is not JsonObject root
            || root[AutoTagLiterals.EnhancementStage] is not JsonObject enhancement
            || enhancement["folderUniformity"] is not JsonObject folderUniformity)
        {
            return;
        }

        folderUniformity.Remove("usePrimaryArtistFolders");
        folderUniformity.Remove(AutoTagLiterals.MultiArtistSeparatorKey);
        folderUniformity.Remove("createArtistFolder");
        folderUniformity.Remove("artistNameTemplate");
        folderUniformity.Remove("createAlbumFolder");
        folderUniformity.Remove("albumNameTemplate");
        folderUniformity.Remove("createCDFolder");
        folderUniformity.Remove("createStructurePlaylist");
        folderUniformity.Remove("createSingleFolder");
        folderUniformity.Remove("createPlaylistFolder");
        folderUniformity.Remove("playlistNameTemplate");
        folderUniformity.Remove("illegalCharacterReplacer");
        folderUniformity.Remove("renameSpotifyArtistFolders");
    }

    private static void EnsureLegacyOrganizerConfigRemoved(JsonNode node)
    {
        if (node is not JsonObject root)
        {
            return;
        }

        root.Remove("organizer");
    }

    private static void EnsureLegacyDeezerAuthRemoved(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(static pair => pair.Key).ToList())
                {
                    if (string.Equals(key, "arl", StringComparison.OrdinalIgnoreCase))
                    {
                        obj.Remove(key);
                        continue;
                    }

                    if (obj[key] is { } child)
                    {
                        EnsureLegacyDeezerAuthRemoved(child);
                    }
                }
                break;
            case JsonArray array:
                foreach (var child in array.Where(static item => item != null))
                {
                    EnsureLegacyDeezerAuthRemoved(child!);
                }
                break;
        }
    }

    private static string RedactSensitiveConfigJson(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return configJson;
        }

        try
        {
            var node = JsonNode.Parse(configJson);
            if (node == null)
            {
                return configJson;
            }

            RedactSensitiveNode(node);
            return node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return configJson;
        }
    }

    private static void RedactSensitiveNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                {
                    foreach (var key in obj.Select(pair => pair.Key).ToList())
                    {
                        if (ShouldRedactConfigKey(key))
                        {
                            obj.Remove(key);
                            continue;
                        }

                        if (obj[key] is { } child)
                        {
                            RedactSensitiveNode(child);
                        }
                    }
                    break;
                }
            case JsonArray array:
                {
                    foreach (var item in array.Where(static item => item != null))
                    {
                        RedactSensitiveNode(item!);
                    }
                    break;
                }
        }
    }

    private static bool ShouldRedactConfigKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (RedactedConfigKeys.Contains(key))
        {
            return true;
        }

        var normalized = NormalizeConfigKeyForRedaction(key);
        return RedactedConfigKeys.Contains(normalized);
    }

    private static string NormalizeConfigKeyForRedaction(string value)
    {
        var chars = value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }

    private void TrySaveLastConfig(string configJson)
    {
        try
        {
            var safeConfig = RedactSensitiveConfigJson(configJson);
            File.WriteAllText(_lastConfigPath, safeConfig, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to persist last AutoTag config.");
        }
    }

    private string WriteRuntimeConfigFile(string jobId, string stage, string configJson)
    {
        Directory.CreateDirectory(_runtimeConfigDir);
        var stageToken = string.IsNullOrWhiteSpace(stage)
            ? "stage"
            : NormalizeConfigKeyForRedaction(stage);
        var fileName = $"autotag-{jobId}-{stageToken}-{Guid.NewGuid():N}.json";
        var path = Path.Join(_runtimeConfigDir, fileName);
        File.WriteAllText(path, configJson, new UTF8Encoding(false));
        return path;
    }

    private void CleanupRuntimeConfigFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!IsRuntimeConfigPath(path))
                {
                    continue;
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Failed deleting runtime AutoTag config file {Path}", path);
                }
            }
        }
    }

    private bool IsRuntimeConfigPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRuntimeRoot = Path.GetFullPath(_runtimeConfigDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRuntimeRoot, StringComparison.OrdinalIgnoreCase);
    }

    private void TrySaveLastJobId(string jobId)
    {
        try
        {
            var payload = new JsonObject { ["jobId"] = jobId };
            File.WriteAllText(_lastJobPath, payload.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to persist last AutoTag job id.");
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

    private static JsonObject GetOrCreateCustomNode(JsonObject node)
    {
        if (node[AutoTagLiterals.CustomKey] is JsonObject custom)
        {
            return custom;
        }

        custom = new JsonObject();
        node[AutoTagLiterals.CustomKey] = custom;
        return custom;
    }

    private static JsonObject GetOrCreatePlatformCustomNode(JsonObject custom, string platformId)
    {
        if (custom[platformId] is JsonObject platformCustom)
        {
            return platformCustom;
        }

        platformCustom = new JsonObject();
        custom[platformId] = platformCustom;
        return platformCustom;
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

    private static string NormalizeRunTrigger(string? trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger))
        {
            return AutoTagLiterals.ManualTrigger;
        }

        return trigger.Trim().ToLowerInvariant() switch
        {
            AutoTagLiterals.ManualTrigger => AutoTagLiterals.ManualTrigger,
            AutoTagLiterals.AutomationTrigger => AutoTagLiterals.AutomationTrigger,
            AutoTagLiterals.ScheduleTrigger => AutoTagLiterals.ScheduleTrigger,

            // Resume successors are admitted with the recovery trigger; coercing it to
            // "invalid" here made every enhancement resume fail the trigger policy and
            // land as a blocked job with zero logs.
            AutoTagLiterals.RecoveryTrigger => AutoTagLiterals.RecoveryTrigger,
            _ => AutoTagLiterals.InvalidTrigger
        };
    }

    private static string NormalizeRunIntent(string? runIntent)
    {
        if (string.IsNullOrWhiteSpace(runIntent))
        {
            return AutoTagLiterals.RunIntentDefault;
        }

        return runIntent.Trim().ToLowerInvariant() switch
        {
            AutoTagLiterals.RunIntentDownloadEnrichment => AutoTagLiterals.RunIntentDownloadEnrichment,
            AutoTagLiterals.RunIntentEnhancementOnly => AutoTagLiterals.RunIntentEnhancementOnly,
            AutoTagLiterals.RunIntentEnhancementRecentDownloads => AutoTagLiterals.RunIntentEnhancementRecentDownloads,
            AutoTagLiterals.RunIntentManualEnrichment => AutoTagLiterals.RunIntentManualEnrichment,
            AutoTagLiterals.RunIntentAliasMerge => AutoTagLiterals.RunIntentAliasMerge,
            _ => AutoTagLiterals.RunIntentDefault
        };
    }

    private static string? NormalizeEnhancementFeature(string? feature)
    {
        if (string.IsNullOrWhiteSpace(feature))
        {
            return null;
        }

        return feature.Trim().ToLowerInvariant() switch
        {
            AutoTagLiterals.EnhancementFeatureGapFill => AutoTagLiterals.EnhancementFeatureGapFill,
            AutoTagLiterals.EnhancementFeatureFolderUniformity => AutoTagLiterals.EnhancementFeatureFolderUniformity,
            AutoTagLiterals.EnhancementFeatureQualityChecks => AutoTagLiterals.EnhancementFeatureQualityChecks,
            AutoTagLiterals.EnhancementFeatureSidecars => AutoTagLiterals.EnhancementFeatureSidecars,
            AutoTagLiterals.EnhancementFeatureCoverMaintenance => AutoTagLiterals.EnhancementFeatureSidecars,
            AutoTagLiterals.EnhancementFeatureLyricsRefreshLegacy => null,
            AutoTagLiterals.EnhancementFeatureManualEnrichment => AutoTagLiterals.EnhancementFeatureManualEnrichment,
            _ => null
        };
    }

    private static bool IsEnhancementRunIntent(string? runIntent)
    {
        return NormalizeRunIntent(runIntent) switch
        {
            AutoTagLiterals.RunIntentEnhancementOnly => true,
            AutoTagLiterals.RunIntentEnhancementRecentDownloads => true,
            AutoTagLiterals.RunIntentAliasMerge => true,
            _ => false
        };
    }

    private static bool IsManualEnrichmentRunIntent(string? runIntent)
        => string.Equals(
            NormalizeRunIntent(runIntent),
            AutoTagLiterals.RunIntentManualEnrichment,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedEnhancementTrigger(string? trigger)
    {
        if (string.Equals(trigger, AutoTagLiterals.ManualTrigger, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trigger, AutoTagLiterals.ScheduleTrigger, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The recovery trigger is only ever produced by resume paths (explicit resume
        // endpoint and stuck-job recovery). Without it, explicitly resuming an
        // enhancement job would be admitted as a blocked successor while the source
        // job was stamped resumed - reporting "running" while nothing runs.
        if (string.Equals(trigger, AutoTagLiterals.RecoveryTrigger, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool ShouldRunEnrichmentForIntent(string? runIntent)
    {
        return !IsEnhancementRunIntent(runIntent);
    }

    private static bool ShouldRunEnhancementForIntent(string? runIntent)
    {
        var normalized = NormalizeRunIntent(runIntent);
        return !string.Equals(normalized, AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalized, AutoTagLiterals.RunIntentManualEnrichment, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The integrated enhancement workflows (sidecar lookup, quality checks, folder
    /// uniformity) run for enhancement intents and for manual enrichment — manual
    /// enrichment's just-moved files get the sidecar lookup with their library track
    /// identity. Download enrichment is excluded: its sidecars are produced by the
    /// download prefetch and no sidecar lookup runs there.
    /// The gap-fill STAGE keeps using <see cref="ShouldRunEnhancementForIntent"/>,
    /// which also excludes manual enrichment (manual runs tag via the enrichment stage).
    /// </summary>
    private static bool ShouldRunIntegratedWorkflowsForIntent(string? runIntent)
    {
        var normalized = NormalizeRunIntent(runIntent);
        return !string.Equals(normalized, AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase);
    }

    private static string InjectRunTrigger(string configJson, string trigger)
    {
        try
        {
            var node = JsonNode.Parse(configJson) as JsonObject;
            if (node == null)
            {
                return configJson;
            }

            node["runTrigger"] = NormalizeRunTrigger(trigger);
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

    private string InjectProfileRuntimeSettings(
        string configJson,
        TechnicalTagSettings? technical,
        FolderStructureSettings? folderStructure,
        string? profileId,
        string? profileName)
    {
        if (technical == null
            && folderStructure == null
            && string.IsNullOrWhiteSpace(profileId)
            && string.IsNullOrWhiteSpace(profileName))
        {
            return configJson;
        }

        if (string.IsNullOrWhiteSpace(configJson))
        {
            return configJson;
        }

        try
        {
            if (JsonNode.Parse(configJson) is not JsonObject root)
            {
                return configJson;
            }

            if (technical != null)
            {
                root["technical"] = JsonSerializer.SerializeToNode(technical, _jsonOptions);
            }

            if (folderStructure != null)
            {
                root["folderStructure"] = JsonSerializer.SerializeToNode(folderStructure, _jsonOptions);
            }

            if (!string.IsNullOrWhiteSpace(profileId))
            {
                root["profileId"] = profileId.Trim();
            }

            if (!string.IsNullOrWhiteSpace(profileName))
            {
                root["profileName"] = profileName.Trim();
            }

            return root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to inject profile runtime settings into AutoTag config.");
            return configJson;
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

    private static void ApplyDiscogsAuthDefaults(JsonObject custom, DiscogsAuth? discogsAuth)
    {
        if (string.IsNullOrWhiteSpace(discogsAuth?.Token))
        {
            return;
        }

        var discogs = GetOrCreatePlatformCustomNode(custom, AutoTagLiterals.DiscogsPlatform);
        SetIfEmpty(discogs, "token", discogsAuth.Token);
    }

    private static void ApplyLastFmAuthDefaults(JsonObject custom, LastFmAuth? lastFmAuth)
    {
        if (string.IsNullOrWhiteSpace(lastFmAuth?.ApiKey))
        {
            return;
        }

        var lastFm = GetOrCreatePlatformCustomNode(custom, AutoTagLiterals.LastFmPlatform);
        SetIfEmpty(lastFm, "apiKey", lastFmAuth.ApiKey);
    }

    private static void ApplyBpmSupremeAuthDefaults(JsonObject custom, BpmSupremeAuth? bpmAuth)
    {
        if (bpmAuth == null)
        {
            return;
        }

        var bpm = GetOrCreatePlatformCustomNode(custom, AutoTagLiterals.BpmSupremePlatform);
        SetIfEmpty(bpm, "email", bpmAuth.Email);
        SetIfEmpty(bpm, "password", bpmAuth.Password);
        SetIfEmpty(bpm, "library", bpmAuth.Library);
    }

    private static void SetIfEmpty(JsonObject target, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (target.TryGetPropertyValue(key, out var existingNode)
            && existingNode is JsonValue existingValue
            && existingValue.TryGetValue<string>(out var existingText)
            && !string.IsNullOrWhiteSpace(existingText))
        {
            return;
        }

        target[key] = value.Trim();
    }


    private static void RemoveNulls(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kvp => kvp.Key).ToList())
            {
                var value = obj[key];
                if (value is null || value.GetValueKind() == JsonValueKind.Null)
                {
                    obj.Remove(key);
                }
                else
                {
                    RemoveNulls(value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(static child => child != null))
            {
                RemoveNulls(child!);
            }
        }
    }

    private static void EnsureSpotifySecret(JsonNode node)
    {
        if (node is not JsonObject root)
        {
            return;
        }

        if (root[AutoTagLiterals.SpotifySource] is not JsonObject spotify)
        {
            return;
        }

        if (!spotify.TryGetPropertyValue("clientSecret", out var secret))
        {
            spotify["clientSecret"] = "";
            return;
        }
        if (secret is null || secret.GetValueKind() == JsonValueKind.Null)
        {
            spotify["clientSecret"] = "";
        }
    }
}
