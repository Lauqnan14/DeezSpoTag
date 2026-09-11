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

    private async Task PrepareRuntimeConfigAndRunJobAsync(
        AutoTagJob job,
        string normalizedPath,
        string configJson,
        StartJobOptions options)
    {
        try
        {
            AppendLog(job, "runtime config preparing");
            var runtimeConfigJson = SanitizeConfigJson(configJson);
            runtimeConfigJson = await InjectPlatformDefaultsAsync(runtimeConfigJson);
            runtimeConfigJson = await InjectPlatformAuthAsync(runtimeConfigJson);
            runtimeConfigJson = InjectRunTrigger(runtimeConfigJson, job.Trigger);
            runtimeConfigJson = InjectProfileRuntimeSettings(
                runtimeConfigJson,
                options.TechnicalOverride,
                options.FolderStructureOverride,
                job.ProfileId,
                job.ProfileName);
            var persistedConfigJson = RedactSensitiveConfigJson(runtimeConfigJson);
            var runtimeConfigPath = WriteRuntimeConfigFile(job.Id, "base", runtimeConfigJson);
            TrySaveLastConfig(persistedConfigJson);
            // Persist the redacted config on the job so POST /jobs/{id}/resume can restart
            // this exact scope even if runtime-config files were cleaned up meanwhile.
            if (!string.Equals(job.ResumeConfigJson, persistedConfigJson, StringComparison.Ordinal))
            {
                job.ResumeConfigJson = persistedConfigJson;
                SaveJob(job);
            }
            AppendLog(job, "runtime config ready");

            await RunJobAsync(job, normalizedPath, runtimeConfigPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "AutoTag runtime config preparation failed for job {JobId}.", job.Id);
            job.Status = AutoTagLiterals.FailedStatus;
            job.Error = $"Runtime config preparation failed: {ex.Message}";
            job.ExitCode = 1;
            job.FinishedAt = DateTimeOffset.UtcNow;
            AppendLog(job, job.Error);
            SaveJob(job);
            AppendActivityLog(job.Id, "autotag failed: runtime config preparation");
            NotifyCompleted(job);
            _activeJobStages.TryRemove(job.Id, out _);
            _activeJobIds.TryRemove(job.Id, out _);
            Volatile.Write(ref _latestTerminalJob, CreateCompactTerminalJob(job));
            _jobs.TryRemove(job.Id, out _);
            _lastActivityLines.TryRemove(job.Id, out _);
            _archiveLocks.TryRemove(job.Id, out _);
            _lastRunIndexUpdateUtc.TryRemove(job.Id, out _);
        }
    }

    private static string NormalizePathForJob(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return path.Trim();
        }
    }

    private static string? NormalizeRootPath(string rootPath)
    {
        var normalizedRoot = NormalizePathForJob(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalizedRoot) || !Directory.Exists(normalizedRoot))
        {
            return null;
        }

        return normalizedRoot;
    }

    private static bool TryParseAutoTagConfig(string configJson, out JsonObject root)
    {
        root = new JsonObject();
        try
        {
            root = JsonNode.Parse(configJson) as JsonObject ?? new JsonObject();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private static string NormalizeCompareValue(object? value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        if (value is string text)
        {
            return text.Trim().ToLowerInvariant();
        }

        if (value is IEnumerable<string> stringValues)
        {
            return string.Join(
                "|",
                stringValues
                    .Select(item => item?.Trim() ?? string.Empty)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.ToLowerInvariant()));
        }

        if (value is bool boolean)
        {
            return boolean ? "true" : "false";
        }

        return value.ToString()?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static HashSet<string> NormalizeCompareParts(object? value)
    {
        if (value is IEnumerable<string> stringValues)
        {
            return stringValues
                .Select(item => item?.Trim() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);
        }

        var normalized = NormalizeCompareValue(value);
        return string.IsNullOrWhiteSpace(normalized)
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(new[] { normalized }, StringComparer.Ordinal);
    }

    public string? TryGetLastConfigJson()
    {
        try
        {
            if (!File.Exists(_lastConfigPath))
            {
                return null;
            }

            var json = File.ReadAllText(_lastConfigPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return RedactSensitiveConfigJson(SanitizeConfigJson(json));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load last AutoTag config.");
            return null;
        }
    }

    private static HashSet<string> InitializeRuntimeConfigPaths(string configPath)
    {
        var runtimeConfigPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            runtimeConfigPaths.Add(configPath);
        }

        return runtimeConfigPaths;
    }

    private JsonObject? LoadConfigRoot(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return null;
            }

            var json = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag config could not be read.");
            return null;
        }
    }

    private static string ComputeConfigHash(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(configJson);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static List<string> FilterSupportedTags(
        IEnumerable<string> requested,
        IEnumerable<string> platforms,
        Dictionary<string, PlatformTagCapabilities> platformCaps)
    {
        var supported = AutoTagPlatformTagContract.ToSupportedTagMap(
            platformCaps,
            static caps => caps.SupportedTags);
        return AutoTagPlatformTagContract.FilterOfferedTags(
            requested,
            platforms,
            supported,
            NormalizeSupportedTagKey);
    }

    private static string NormalizeDownloadTagSource(string? downloadTagSource)
    {
        return DownloadTagSourceHelper.NormalizeStoredSource(downloadTagSource, AutoTagLiterals.DeezerSource);
    }

    private static string? NormalizeSupportedTagKey(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var normalized = tag.Trim();
        return SupportedTagKeyMap.TryGetValue(normalized, out var mapped) ? mapped : null;
    }

    private async Task TriggerConfiguredMediaServerRefreshAfterEnhancementAsync(
        AutoTagJob job,
        bool includesEnhancementStage,
        CancellationToken cancellationToken)
    {
        if (!includesEnhancementStage
            || (!ShouldRunEnhancementForIntent(job.RunIntent)
                && !IsManualEnrichmentRunIntent(job.RunIntent)))
        {
            return;
        }

        try
        {
            AppendLog(job, "media server metadata refresh starting after enhancement (request only; not waiting for a full library reindex).");
            using var refreshTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            refreshTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            var refresh = await _mediaServerRefreshService.RefreshConfiguredServersAsync(
                refreshTimeout.Token,
                updateTrackIndex: false);
            AppendLog(
                job,
                $"media server metadata refresh requested after enhancement: configured={refresh.ConfiguredServerCount}, refreshed={refresh.RefreshedServerCount}, failed=[{string.Join(", ", refresh.FailedServers)}]");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppendLog(job, "media server metadata refresh after enhancement timed out; enhancement run will finish without waiting.");
        }
        catch (OperationCanceledException)
        {
            AppendLog(job, "media server metadata refresh after enhancement was canceled");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag job {JobId}: configured media server refresh after enhancement failed.", job.Id);
            AppendLog(job, $"media server metadata refresh after enhancement failed: {ex.Message}");
        }
    }

    private async Task<PlexAuth?> LoadConfiguredPlexForScanAsync(AutoTagJob job)
    {
        try
        {
            var authState = await _platformAuthService.LoadAsync();
            var plex = authState.Plex;
            if (!IsPlexAuthenticated(plex))
            {
                return null;
            }

            return plex;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag job {JobId}: failed loading Plex auth state for scan.", job.Id);
            return null;
        }
    }

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

    private List<AutoTagRunSummary> NormalizeRunIndexSummaries(IEnumerable<AutoTagRunSummary> summaries)
    {
        return summaries
            .Where(static summary => !string.IsNullOrWhiteSpace(summary.Id))
            .GroupBy(GetRunIndexGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(summary => summary.StartedAt).First())
            .OrderByDescending(static summary => summary.StartedAt)
            .ToList();
    }

    private void NormalizeLoadedJobState(AutoTagJob job)
    {
        job.Trigger = NormalizeRunTrigger(job.Trigger);
        job.RunIntent = NormalizeRunIntent(job.RunIntent);
        if (job.LastActivityAt <= DateTimeOffset.MinValue)
        {
            job.LastActivityAt = ResolveLastActivityTimestamp(job);
        }

        if (!string.Equals(job.Status, AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_activeJobIds.ContainsKey(job.Id))
        {
            return;
        }

        job.Status = AutoTagLiterals.InterruptedStatus;
        job.ExitCode = 1;
        job.FinishedAt ??= DateTimeOffset.UtcNow;
        job.Error ??= "AutoTag job was interrupted by an application restart; resume is available.";
        SaveJob(job);
        // Archived summaries and job records are immutable history beyond this point:
        // restart-interrupted enhancement runs are queued for resume, never rewritten.
        if (IsEnhancementRunIntent(job.RunIntent) || IsManualEnrichmentRunIntent(job.RunIntent))
        {
            AppendLog(job, "stale recovery: job interrupted by application restart; resume queued for enhancement.");
            JobRecovered?.Invoke(job);
        }
    }

    private string? TryFindRuntimeConfigPath(string jobId, string stage)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(stage) || !Directory.Exists(_runtimeConfigDir))
            {
                return null;
            }

            var stageToken = NormalizeConfigKeyForRedaction(stage);
            var pattern = $"autotag-{jobId}-{stageToken}-*.json";
            return Directory
                .EnumerateFiles(_runtimeConfigDir, pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to locate runtime config for stale recovery job {JobId}.", jobId);
            }
            return null;
        }
    }
}
