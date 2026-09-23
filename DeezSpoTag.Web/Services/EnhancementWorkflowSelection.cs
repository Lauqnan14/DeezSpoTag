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

    /// <summary>
    /// Conflicting sidecar toggles are mutually exclusive: at most one toggle of a pair
    /// may be active, and activating the other requires disabling the active one first.
    /// The UI blocks the second enable and profile saves are validated; canonicalization
    /// resolves stored configurations that still hold both (legacy data) to the
    /// non-destructive member of the pair.
    /// </summary>
    public sealed record ExclusiveSidecarTogglePair(
        string Section,
        string FirstKey,
        string SecondKey,
        string FirstLabel,
        string SecondLabel,
        string PreservedKey);

    public static readonly ExclusiveSidecarTogglePair[] ExclusiveSidecarTogglePairs =
    [
        new(
            "sidecars",
            "removeLineSyncedTtml",
            "rewriteLineSyncedTtml",
            "Remove line-synced TTML",
            "Rewrite line-synced TTML with word timing",
            "rewriteLineSyncedTtml"),
        new(
            "coverMaintenance",
            "renameExistingAnimatedArtwork",
            "overwriteExistingAnimatedArtwork",
            "Rename existing animated artwork",
            "Overwrite existing animated artwork",
            "renameExistingAnimatedArtwork")
    ];

    /// <summary>
    /// The workflows that run together in one combined enhancement job, in execution order.
    /// Quality Checks is deliberately absent: it only inspects and flags rather than rewriting
    /// files, so it runs as its own independently triggered job instead of joining this sequence.
    /// </summary>
    public static readonly string[] OrderedFeatures =
    [
        GapFill,
        Sidecars,
        FolderUniformity
    ];

    public const string FolderUniformityModeBatchScoped = "batch-scoped";
    public const string FolderUniformityModeLibraryWide = "library-wide";

    public static IReadOnlyList<string> OrderSelectedFeatures(IEnumerable<string?>? features)
    {
        var selected = NormalizeSelectedFeatures(features);
        return OrderedFeatures
            .Concat([QualityChecks, ManualEnrichment])
            .Where(selected.Contains)
            .ToList();
    }

    public static string? ResolveFolderUniformityRunMode(IReadOnlyCollection<string> selectedFeatures)
    {
        var selected = NormalizeSelectedFeatures(selectedFeatures);
        if (!selected.Contains(FolderUniformity))
        {
            return null;
        }

        return selected.Count == 1
            ? FolderUniformityModeLibraryWide
            : FolderUniformityModeBatchScoped;
    }

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

        // A checks run is not a feature selection: the profile decides what is switched on.
        // Its request carries the checks marker alone, so the other sections keep whatever
        // enablement the profile stored instead of being switched off by the request. The
        // legacy qualityChecks.enabled flag is no longer written as a run marker — the run is
        // identified by the "quality-checks" feature on the job, and a stored value is left
        // untouched so profiles written by older versions keep working.
        var isChecksRun = selected.Contains(QualityChecks)
            && !selected.Contains(GapFill)
            && !selected.Contains(Sidecars)
            && !selected.Contains(FolderUniformity);
        if (!isChecksRun && selected.Count > 0)
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

        foreach (var pair in ExclusiveSidecarTogglePairs)
        {
            var section = enhancement[pair.Section] as JsonObject;
            if (section == null)
            {
                continue;
            }

            changed |= ResolveExclusiveTogglePairToNonDestructive(section, pair);
        }

        return changed;
    }

    /// <summary>
    /// A stored configuration that still holds both members of an exclusive pair (legacy
    /// data, or a save predating the exclusivity validation) resolves to the
    /// non-destructive member of the pair: Rewrite wins over Remove, Rename wins over
    /// Overwrite. A stored setting is never silently wiped to disabled.
    /// </summary>
    private static bool ResolveExclusiveTogglePairToNonDestructive(JsonObject section, ExclusiveSidecarTogglePair pair)
    {
        var first = ReadBool(section, pair.FirstKey) == true;
        var second = ReadBool(section, pair.SecondKey) == true;
        if (!first || !second)
        {
            return false;
        }

        // The destructive toggle loses; the constructive one stays enabled.
        var preservedIsFirst = string.Equals(pair.PreservedKey, pair.FirstKey, StringComparison.OrdinalIgnoreCase);
        var otherKey = preservedIsFirst ? pair.SecondKey : pair.FirstKey;
        section[pair.PreservedKey] = true;
        section[otherKey] = false;
        return true;
    }

    /// <summary>
    /// Returns a user-facing message when both members of an exclusive sidecar toggle
    /// pair are enabled, or null when the configuration is valid.
    /// </summary>
    public static string? DescribeSidecarToggleConflict(JsonObject? enhancement)
    {
        if (enhancement == null)
        {
            return null;
        }

        foreach (var pair in ExclusiveSidecarTogglePairs)
        {
            if (enhancement[pair.Section] is not JsonObject section)
            {
                continue;
            }

            if (ReadBool(section, pair.FirstKey) == true && ReadBool(section, pair.SecondKey) == true)
            {
                return $"\"{pair.SecondLabel}\" conflicts with the active \"{pair.FirstLabel}\". "
                    + $"Disable \"{pair.FirstLabel}\" first.";
            }
        }

        return null;
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

    public static bool IsSidecarsEnabledForFolder(JsonObject configNode, long folderId)
    {
        if (configNode[AutoTagLiterals.EnhancementStage] is not JsonObject enhancement
            || enhancement["sidecars"] is not JsonObject sidecars
            || ReadBool(sidecars, "enabled") != true)
        {
            return false;
        }

        var folderIds = ParseFolderIds(sidecars);
        return folderIds.Count == 0 || folderIds.Contains(folderId);
    }

    public static bool IsSidecarsRunnable(JsonObject enhancementRoot)
    {
        return enhancementRoot["sidecars"] is JsonObject sidecars
            && ReadBool(sidecars, "enabled") == true
            && (HasSidecarLyricsActions(enhancementRoot) || HasExplicitCoverActions(enhancementRoot));
    }

    /// <summary>
    /// True when any individual quality check is configured, regardless of the legacy
    /// <c>enabled</c> flag. Quality Checks is manual-only — it has no schedule tick — so its own
    /// "Run Selected Checks" action depends on the checks being configured, not on that flag.
    /// </summary>
    public static bool HasConfiguredQualityChecks(JsonObject enhancementRoot)
    {
        if (enhancementRoot["qualityChecks"] is not JsonObject qualityChecks)
        {
            return false;
        }

        return ReadBool(qualityChecks, "flagDuplicates") == true
            || ReadBool(qualityChecks, "flagMissingTags") == true
            || ReadBool(qualityChecks, "flagMismatchedMetadata") == true
            || ReadBool(qualityChecks, "queueAtmosAlternatives") == true
            || ReadBool(qualityChecks, "queueTechnicalProfileUpgrades") == true;
    }

    /// <summary>
    /// True when the profile has at least one repair section switched on, so a checks run over
    /// the library can actually rewrite files. When every repair section is off the run can only
    /// report; the checks path warns about that before it starts instead of silently doing nothing.
    /// Takes the config root because the gap-fill tag selection lives at the root, not under
    /// <c>enhancement</c>.
    /// </summary>
    public static bool HasAnyRepairSectionsEnabled(JsonObject configRoot)
    {
        if (configRoot[AutoTagLiterals.EnhancementStage] is not JsonObject enhancementRoot)
        {
            return false;
        }

        return IsGapFillRunnable(configRoot)
            || IsSidecarsRunnable(enhancementRoot)
            || IsFolderUniformityRunnable(enhancementRoot);
    }

    /// <summary>
    /// True when the profile has any enhancement work that a scheduled run would perform.
    /// Quality Checks is excluded: it is manual-only, so configuring checks alone must not make
    /// a profile count as having scheduled enhancement work.
    /// </summary>
    public static bool HasConfiguredEnhancementWorkflows(JsonObject root)
    {
        return root[AutoTagLiterals.EnhancementStage] is JsonObject enhancementRoot
            && (IsFolderUniformityRunnable(enhancementRoot)
                || IsSidecarsRunnable(enhancementRoot));
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
