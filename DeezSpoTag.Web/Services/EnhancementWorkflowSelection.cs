using System.Text.Json.Nodes;

namespace DeezSpoTag.Web.Services;

internal static class EnhancementWorkflowSelection
{
    public const string GapFill = AutoTagLiterals.EnhancementFeatureGapFill;
    public const string FolderUniformity = AutoTagLiterals.EnhancementFeatureFolderUniformity;
    public const string QualityChecks = AutoTagLiterals.EnhancementFeatureQualityChecks;
    public const string Sidecars = AutoTagLiterals.EnhancementFeatureSidecars;
    public const string CoverMaintenance = AutoTagLiterals.EnhancementFeatureCoverMaintenance;
    public const string ManualEnrichment = AutoTagLiterals.EnhancementFeatureManualEnrichment;

    private static readonly string[] SidecarLyricsKeys =
    [
        "queueLyricsRefresh",
        "removeLineSyncedTtml",
        "rewriteLineSyncedTtml"
    ];

    public static readonly string[] OrderedFeatures =
    [
        GapFill,
        Sidecars,
        QualityChecks,
        FolderUniformity
    ];

    public static HashSet<string> NormalizeSelectedFeatures(IEnumerable<string?>? features)
    {
        return (features ?? Array.Empty<string?>())
            .Select(value =>
            {
                var normalized = value?.Trim().ToLowerInvariant();
                return string.Equals(normalized, CoverMaintenance, StringComparison.OrdinalIgnoreCase)
                    ? Sidecars
                    : normalized;
            })
            .Where(value => value is GapFill
                or FolderUniformity
                or QualityChecks
                or Sidecars
                or ManualEnrichment)
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static void ApplyFeatureSelection(
        JsonObject configNode,
        IReadOnlyCollection<string> selectedFeatures,
        IReadOnlyList<long>? folderIds = null,
        IReadOnlyList<string>? targetFiles = null)
    {
        var enhancement = configNode[AutoTagLiterals.EnhancementStage] as JsonObject ?? new JsonObject();
        configNode[AutoTagLiterals.EnhancementStage] = enhancement;

        var selected = selectedFeatures as HashSet<string>
            ?? selectedFeatures.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count > 0)
        {
            SetSectionEnabled(enhancement, "folderUniformity", selected.Contains(FolderUniformity));
            SetSectionEnabled(enhancement, "sidecars", selected.Contains(Sidecars));
            SetSectionEnabled(
                enhancement,
                "coverMaintenance",
                selected.Contains(Sidecars) && HasExplicitCoverActions(enhancement));
            SetSectionEnabled(enhancement, "qualityChecks", selected.Contains(QualityChecks));
            SetSectionEnabled(enhancement, "gapFilling", selected.Contains(GapFill));
            if (selected.Contains(GapFill))
            {
                EnsureGapFillTagsMirrorRequestedTags(configNode);
            }
            else
            {
                configNode["gapFillTags"] = new JsonArray();
            }
        }

        if (folderIds is { Count: > 0 })
        {
            ApplyFolderScope(enhancement, "folderUniformity", folderIds);
            ApplyFolderScope(enhancement, "coverMaintenance", folderIds);
            ApplyFolderScope(enhancement, "qualityChecks", folderIds);
            ApplyFolderScope(enhancement, "gapFilling", folderIds);
            ApplyFolderScope(enhancement, "sidecars", folderIds);
        }

        if (targetFiles is { Count: > 0 })
        {
            configNode[AutoTagLiterals.TargetFilesKey] = new JsonArray(
                targetFiles.Select(value => JsonValue.Create(value)).ToArray());
        }
    }

    public static bool CanonicalizeSidecars(JsonObject enhancement)
    {
        var changed = false;
        var sidecars = enhancement["sidecars"] as JsonObject;
        if (sidecars == null)
        {
            sidecars = new JsonObject();
            enhancement["sidecars"] = sidecars;
            changed = true;
        }

        var qualityChecks = enhancement["qualityChecks"] as JsonObject;
        var coverMaintenance = enhancement["coverMaintenance"] as JsonObject;
        foreach (var key in SidecarLyricsKeys)
        {
            var qcValue = qualityChecks == null ? null : ReadBool(qualityChecks, key);
            if (qcValue is bool qcBool && sidecars[key] is null)
            {
                sidecars[key] = qcBool;
                changed = true;
            }

            if (qualityChecks != null && qualityChecks.Remove(key))
            {
                changed = true;
            }
        }

        if (sidecars["enabled"] is null)
        {
            var coverEnabled = coverMaintenance != null && ReadBool(coverMaintenance, "enabled") == true;
            var qualityEnabled = qualityChecks != null && ReadBool(qualityChecks, "enabled") == true;
            sidecars["enabled"] = coverEnabled || (qualityEnabled && HasSidecarLyricsActions(enhancement));
            changed = true;
        }

        if (ParseFolderIds(sidecars).Count == 0 && coverMaintenance != null)
        {
            var coverFolderIds = ParseFolderIds(coverMaintenance);
            if (coverFolderIds.Count > 0)
            {
                sidecars["folderIds"] = new JsonArray(
                    coverFolderIds.Select(id => JsonValue.Create(id)).ToArray());
                changed = true;
            }
        }

        return changed;
    }

    public static bool IsGapFillRunnable(JsonObject configNode)
    {
        if (configNode[AutoTagLiterals.EnhancementStage] is JsonObject enhancement
            && enhancement["gapFilling"] is JsonObject gapFilling
            && ReadBool(gapFilling, "enabled") is bool enabled)
        {
            return enabled && AutoTagPlatformTagContract.ResolveRequestedTags(configNode).Count > 0;
        }

        return configNode["gapFillTags"] is JsonArray tags && tags.Count > 0;
    }

    public static bool IsFolderUniformityRunnable(JsonObject enhancementRoot)
    {
        return enhancementRoot["folderUniformity"] is JsonObject config
            && ReadBool(config, "enabled") == true
            && (ReadBool(config, "enforceFolderStructure") != false || ReadBool(config, "runDedupe") != false);
    }

    public static bool HasSidecarLyricsActions(JsonObject enhancementRoot)
    {
        return enhancementRoot["sidecars"] is JsonObject sidecars
            && SidecarLyricsKeys.Any(key => ReadBool(sidecars, key) == true);
    }

    public static bool HasExplicitCoverActions(JsonObject enhancementRoot)
    {
        if (enhancementRoot["coverMaintenance"] is not JsonObject coverMaintenance)
        {
            return false;
        }

        return ReadBool(coverMaintenance, "replaceMissingEmbeddedCovers") == true
            || ReadBool(coverMaintenance, "syncExternalCovers") == true
            || ReadBool(coverMaintenance, "upgradeLowResolutionCovers") == true
            || ReadBool(coverMaintenance, "queueAnimatedArtwork") == true
            || ReadBool(coverMaintenance, "overwriteExistingAnimatedArtwork") == true
            || ReadBool(coverMaintenance, "removeOldAnimatedArtwork") == true;
    }

    public static bool IsSidecarsRunnable(JsonObject enhancementRoot)
    {
        return enhancementRoot["sidecars"] is JsonObject sidecars
            && ReadBool(sidecars, "enabled") == true
            && (HasSidecarLyricsActions(enhancementRoot) || HasExplicitCoverActions(enhancementRoot));
    }

    public static bool IsQualityChecksRunnable(JsonObject enhancementRoot)
    {
        if (enhancementRoot["qualityChecks"] is not JsonObject qualityChecks
            || ReadBool(qualityChecks, "enabled") != true)
        {
            return false;
        }

        return ReadBool(qualityChecks, "flagDuplicates") == true
            || ReadBool(qualityChecks, "flagMissingTags") == true
            || ReadBool(qualityChecks, "flagMismatchedMetadata") == true
            || ReadBool(qualityChecks, "queueAtmosAlternatives") == true
            || ReadBool(qualityChecks, "queueTechnicalProfileUpgrades") == true;
    }

    public static bool IsMissingCoreMetadataScanEnabled(JsonObject configNode)
    {
        return configNode[AutoTagLiterals.EnhancementStage] is JsonObject enhancement
            && enhancement["qualityChecks"] is JsonObject qualityChecks
            && ReadBool(qualityChecks, "enabled") == true
            && ReadBool(qualityChecks, "flagMissingTags") == true;
    }

    public static bool HasConfiguredEnhancementWorkflows(JsonObject root)
    {
        return root[AutoTagLiterals.EnhancementStage] is JsonObject enhancementRoot
            && (IsFolderUniformityRunnable(enhancementRoot)
                || IsSidecarsRunnable(enhancementRoot)
                || IsQualityChecksRunnable(enhancementRoot));
    }

    private static void EnsureGapFillTagsMirrorRequestedTags(JsonObject configNode)
    {
        if (configNode["gapFillTags"] is JsonArray existing && existing.Count > 0)
        {
            return;
        }

        var requested = AutoTagPlatformTagContract.ResolveRequestedTags(configNode);
        configNode["gapFillTags"] = new JsonArray(
            requested.Select(value => JsonValue.Create(value)).ToArray());
    }

    private static void SetSectionEnabled(JsonObject enhancement, string name, bool enabled)
    {
        var feature = enhancement[name] as JsonObject ?? new JsonObject();
        feature["enabled"] = enabled;
        enhancement[name] = feature;
    }

    private static void ApplyFolderScope(JsonObject enhancement, string name, IReadOnlyList<long> folderIds)
    {
        if (enhancement[name] is not JsonObject feature)
        {
            return;
        }

        feature["folderIds"] = new JsonArray(
            folderIds.Select(value => JsonValue.Create(value)).ToArray());
    }

    private static List<long> ParseFolderIds(JsonObject section)
    {
        if (section["folderIds"] is not JsonArray array)
        {
            return [];
        }

        return array
            .Select(node =>
            {
                if (node is JsonValue value)
                {
                    if (value.TryGetValue<long>(out var longValue) && longValue > 0)
                    {
                        return longValue;
                    }

                    if (value.TryGetValue<string>(out var raw)
                        && long.TryParse(raw, out var parsed)
                        && parsed > 0)
                    {
                        return parsed;
                    }
                }

                return 0L;
            })
            .Where(id => id > 0)
            .Distinct()
            .ToList();
    }

    private static bool? ReadBool(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<bool>(out var boolValue))
        {
            return boolValue;
        }

        return value.TryGetValue<string>(out var raw) && bool.TryParse(raw, out var parsed)
            ? parsed
            : null;
    }
}
