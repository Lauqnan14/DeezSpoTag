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

    private async Task<string?> ValidateRunIntentScopeAsync(
        string normalizedPath,
        string runIntent,
        CancellationToken cancellationToken)
    {
        if (string.Equals(runIntent, AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase))
        {
            if (!ConfiguredDownloadRootResolver.TryResolve(
                    _settingsService,
                    "download location",
                    "download location is not configured.",
                    out var downloadRoot,
                    out var error))
            {
                return $"Download enrichment run blocked: {error}";
            }

            if (!IsPathUnderRoot(normalizedPath, downloadRoot))
            {
                return $"Download enrichment run blocked: path '{normalizedPath}' is outside configured download location '{downloadRoot}'.";
            }

            return null;
        }

        if (!IsEnhancementRunIntent(runIntent))
        {
            return null;
        }

        var libraryRoots = await ResolveAllowedLibraryRootsAsync(cancellationToken);
        if (libraryRoots.Count == 0)
        {
            return "Enhancement run blocked: no accessible library folders are configured.";
        }

        if (!libraryRoots.Any(root => IsPathUnderRoot(normalizedPath, root)))
        {
            return $"Enhancement run blocked: path '{normalizedPath}' is outside configured library roots.";
        }

        if (ConfiguredDownloadRootResolver.TryResolve(
                _settingsService,
                "download location",
                "download location is not configured.",
                out var configuredDownloadRoot,
                out _)
            && IsPathUnderRoot(normalizedPath, configuredDownloadRoot))
        {
            return $"Enhancement run blocked: path '{normalizedPath}' is inside download location '{configuredDownloadRoot}'.";
        }

        return null;
    }

    private async Task ApplyJobProfileOrganizerOverridesAsync(
        AutoTagJob job,
        AutoTagOrganizerOptions options,
        CancellationToken cancellationToken)
    {
        var profile = await ResolveJobProfileAsync(job, cancellationToken);
        if (profile != null)
        {
            AutoTagOrganizerProfileOverlay.ApplyTaggingProfileOverrides(options, profile);
            return;
        }
        throw new InvalidOperationException("AutoTag organization requires a valid profile.");
    }

    private async Task<TaggingProfile?> ResolveJobProfileAsync(AutoTagJob job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.ProfileId) && string.IsNullOrWhiteSpace(job.ProfileName))
        {
            return null;
        }

        try
        {
            var state = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: false, cancellationToken);
            return AutoTagProfileResolutionService.ResolveProfileReference(state.Profiles, job.ProfileId)
                ?? AutoTagProfileResolutionService.ResolveProfileReference(state.Profiles, job.ProfileName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve AutoTag profile for organizer overrides.");
            return null;
        }
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

    private bool ShouldUpdateRunIndex(AutoTagRunSummary summary)
    {
        if (IsTerminalRunStatus(summary.Status))
        {
            _lastRunIndexUpdateUtc[summary.Id] = DateTimeOffset.UtcNow;
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        var lastUpdate = _lastRunIndexUpdateUtc.GetOrAdd(summary.Id, now);
        if (lastUpdate == now || now - lastUpdate < RunIndexUpdateInterval)
        {
            return lastUpdate == now;
        }

        _lastRunIndexUpdateUtc[summary.Id] = now;
        return true;
    }

    internal static bool ShouldThrottleJobSave(DateTimeOffset? lastSaveUtc, DateTimeOffset now)
    {
        return lastSaveUtc.HasValue && now - lastSaveUtc.Value < JobSaveThrottleInterval;
    }
}
