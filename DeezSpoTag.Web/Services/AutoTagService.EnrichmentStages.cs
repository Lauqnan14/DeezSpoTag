using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeezSpoTag.Web.Services;

public partial class AutoTagService
{
    private const string ShazamPlatformId = "shazam";

    private enum EnrichmentRunMode
    {
        Manual,
        AutomaticDownload
    }

    private sealed record EnrichmentStagePlan(
        List<string> RequestedTags,
        List<string> Platforms,
        string? ExcludedPlatform,
        bool ForceShazamFingerprint = false,
        bool OrganizeSidecarsIntoTemplateFolders = false,
        bool MaterializeToTemplatePath = false);

    private bool TryBuildEnrichmentStages(
        JsonObject baseRoot,
        Dictionary<string, PlatformTagCapabilities> platformCaps,
        IReadOnlyList<string> eligiblePlatforms,
        EnrichmentBuildContext context,
        out List<AutoTagStageConfig> stages,
        out string skipReason,
        out List<string> strippedKeys)
    {
        stages = new List<AutoTagStageConfig>();
        var mode = ResolveEnrichmentRunMode(context.RunIntent);
        if (mode == EnrichmentRunMode.AutomaticDownload)
        {
            if (!TryBuildAutomaticDownloadEnrichmentStage(baseRoot, platformCaps, eligiblePlatforms, context, out var downloadStage, out skipReason, out strippedKeys))
            {
                return false;
            }

            stages.Add(downloadStage);
            return true;
        }

        return TryBuildManualEnrichmentStages(baseRoot, platformCaps, eligiblePlatforms, context, out stages, out skipReason, out strippedKeys);
    }

    private bool TryBuildManualEnrichmentStages(
        JsonObject baseRoot,
        Dictionary<string, PlatformTagCapabilities> platformCaps,
        IReadOnlyList<string> eligiblePlatforms,
        EnrichmentBuildContext context,
        out List<AutoTagStageConfig> stages,
        out string skipReason,
        out List<string> strippedKeys)
    {
        stages = new List<AutoTagStageConfig>();
        strippedKeys = new List<string>();

        var plan = BuildManualEnrichmentStagePlan(baseRoot, eligiblePlatforms);
        if (!plan.Platforms.Any(platform => string.Equals(platform, ShazamPlatformId, StringComparison.OrdinalIgnoreCase)))
        {
            skipReason = "manual enrichment requires Shazam to be enabled";
            return false;
        }

        var enrichmentPlatforms = plan.Platforms
            .Where(platform => !string.Equals(platform, ShazamPlatformId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (enrichmentPlatforms.Count == 0)
        {
            enrichmentPlatforms.Add(ShazamPlatformId);
        }

        var manualPlan = plan with
        {
            Platforms = enrichmentPlatforms,
            ForceShazamFingerprint = true
        };
        if (!TryBuildEnrichmentStageFromPlan(
            baseRoot,
            platformCaps,
            context,
            manualPlan,
            out var manualStage,
            out skipReason,
            out var manualStrippedKeys))
        {
            skipReason = $"manual enrichment failed: {skipReason}";
            return false;
        }

        strippedKeys.AddRange(manualStrippedKeys);
        stages.Add(manualStage);
        skipReason = string.Empty;
        return true;
    }

    private bool TryBuildAutomaticDownloadEnrichmentStage(
        JsonObject baseRoot,
        Dictionary<string, PlatformTagCapabilities> platformCaps,
        IReadOnlyList<string> eligiblePlatforms,
        EnrichmentBuildContext context,
        out AutoTagStageConfig stage,
        out string skipReason,
        out List<string> strippedKeys)
    {
        return TryBuildEnrichmentStageFromPlan(
            baseRoot,
            platformCaps,
            context,
            BuildAutomaticDownloadEnrichmentStagePlan(baseRoot, eligiblePlatforms, platformCaps),
            out stage,
            out skipReason,
            out strippedKeys);
    }

    private static EnrichmentStagePlan BuildManualEnrichmentStagePlan(
        JsonObject baseRoot,
        IReadOnlyList<string> eligiblePlatforms)
    {
        return new EnrichmentStagePlan(
            RequestedTags: ResolveEnrichmentRequestedTags(baseRoot),
            Platforms: eligiblePlatforms.ToList(),
            ExcludedPlatform: null,
            OrganizeSidecarsIntoTemplateFolders: true,
            MaterializeToTemplatePath: true);
    }

    private static EnrichmentStagePlan BuildAutomaticDownloadEnrichmentStagePlan(
        JsonObject baseRoot,
        IReadOnlyList<string> eligiblePlatforms,
        Dictionary<string, PlatformTagCapabilities> platformCaps)
    {
        var excludedPlatform = ResolveDownloadSourcePlatform(baseRoot);
        var requestedTags = ResolveAutomaticDownloadEnrichmentRequestedTags(baseRoot);
        var sourceFilteredPlatforms = string.IsNullOrWhiteSpace(excludedPlatform)
            ? eligiblePlatforms.ToList()
            : eligiblePlatforms
                .Where(platform => !string.Equals(platform, excludedPlatform, StringComparison.OrdinalIgnoreCase))
                .ToList();
        var platforms = FilterAutomaticDownloadEnrichmentPlatforms(sourceFilteredPlatforms, requestedTags, platformCaps);
        return new EnrichmentStagePlan(
            RequestedTags: requestedTags,
            Platforms: platforms,
            ExcludedPlatform: excludedPlatform);
    }

    private static List<string> FilterAutomaticDownloadEnrichmentPlatforms(
        IEnumerable<string> platforms,
        IReadOnlyCollection<string> requestedTags,
        Dictionary<string, PlatformTagCapabilities> platformCaps)
    {
        var requested = requestedTags
            .Select(NormalizeSupportedTagKey)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0)
        {
            return new List<string>();
        }

        return platforms
            .Where(platform => !IsLyricsProviderPlatform(platform))
            .Where(platform => PlatformSupportsAnyRequestedTag(platform, requested, platformCaps))
            .ToList();
    }

    private static bool IsLyricsProviderPlatform(string? platform)
        => string.Equals(platform?.Trim(), "lrclib", StringComparison.OrdinalIgnoreCase)
           || string.Equals(platform?.Trim(), "musixmatch", StringComparison.OrdinalIgnoreCase);

    private static bool PlatformSupportsAnyRequestedTag(
        string platform,
        HashSet<string> requestedTags,
        Dictionary<string, PlatformTagCapabilities> platformCaps)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return false;
        }

        return platformCaps.TryGetValue(platform.Trim(), out var caps)
            && caps.SupportedTags.Any(requestedTags.Contains);
    }

    private static List<string> ResolveAutomaticDownloadEnrichmentRequestedTags(JsonObject baseRoot)
    {
        return ResolveEnrichmentRequestedTags(baseRoot)
            .Where(tag => !IsLyricsTag(tag))
            .ToList();
    }

    private static bool IsLyricsTag(string? tag)
    {
        var normalized = NormalizeSupportedTagKey(tag);
        return normalized is "unsyncedLyrics" or "syncedLyrics" or "ttmlLyrics";
    }

    private bool TryBuildEnrichmentStageFromPlan(
        JsonObject baseRoot,
        Dictionary<string, PlatformTagCapabilities> platformCaps,
        EnrichmentBuildContext context,
        EnrichmentStagePlan plan,
        out AutoTagStageConfig stage,
        out string skipReason,
        out List<string> strippedKeys)
    {
        stage = null!;
        skipReason = "tags not configured";
        strippedKeys = new List<string>();

        if (plan.RequestedTags.Count == 0)
        {
            return false;
        }

        if (plan.Platforms.Count == 0)
        {
            skipReason = string.IsNullOrWhiteSpace(plan.ExcludedPlatform)
                ? "no eligible enrichment platforms enabled"
                : $"no enrichment platforms enabled after excluding download source ({plan.ExcludedPlatform})";
            return false;
        }

        var filtered = FilterSupportedTags(plan.RequestedTags, plan.Platforms, platformCaps);
        if (filtered.Count == 0)
        {
            skipReason = "no supported enrichment tags for enabled platforms";
            return false;
        }

        var stageRoot = CloneRoot(baseRoot);
        WriteStringList(stageRoot, "tags", filtered);
        WriteStringList(stageRoot, AutoTagLiterals.PlatformsKey, plan.Platforms);
        var platformCount = ReadStringList(stageRoot, AutoTagLiterals.PlatformsKey).Count;
        stageRoot[AutoTagLiterals.MultiPlatformKey] = platformCount > 1;
        if (plan.ForceShazamFingerprint)
        {
            ConfigureShazamFingerprintBootstrap(stageRoot);
        }
        if (plan.OrganizeSidecarsIntoTemplateFolders)
        {
            stageRoot["organizeSidecarsIntoTemplateFolders"] = true;
        }
        if (plan.MaterializeToTemplatePath)
        {
            stageRoot["materializeToTemplatePath"] = true;
        }
        if (string.Equals(context.RunIntent, AutoTagLiterals.RunIntentManualEnrichment, StringComparison.OrdinalIgnoreCase))
        {
            stageRoot[AutoTagLiterals.LibraryWideEnhancementBatchSizeKey] = 40;
        }

        strippedKeys = ApplyStageSchema(stageRoot, EnrichmentStageAllowedKeys);

        var configJson = stageRoot.ToJsonString(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        var configPath = WriteRuntimeConfigFile(context.JobId, AutoTagLiterals.EnrichmentStage, configJson);
        stage = new AutoTagStageConfig(
            AutoTagLiterals.EnrichmentStage,
            configPath,
            filtered.Count,
            ComputeConfigHash(configJson));
        return true;
    }

    private static void ConfigureShazamFingerprintBootstrap(JsonObject stageRoot)
    {
        stageRoot["enableShazam"] = true;
        stageRoot["forceShazam"] = true;

        if (stageRoot["custom"] is not JsonObject custom)
        {
            custom = new JsonObject();
            stageRoot["custom"] = custom;
        }

        custom[ShazamPlatformId] = new JsonObject
        {
            ["id_first"] = false,
            ["fingerprint_fallback"] = true,
            ["fallback_missing_core_tags"] = true,
            ["force_match"] = true,
            ["prefer_hq_artwork"] = true,
            ["include_album"] = true,
            ["include_genre"] = true,
            ["include_label"] = true,
            ["include_release_date"] = true
        };

        stageRoot["organizeSidecarsIntoTemplateFolders"] = true;
    }

    private static EnrichmentRunMode ResolveEnrichmentRunMode(string? runIntent)
    {
        return string.Equals(
            NormalizeRunIntent(runIntent),
            AutoTagLiterals.RunIntentDownloadEnrichment,
            StringComparison.OrdinalIgnoreCase)
                ? EnrichmentRunMode.AutomaticDownload
                : EnrichmentRunMode.Manual;
    }

    private static List<string> ResolveEnrichmentRequestedTags(JsonObject baseRoot)
    {
        return ReadStringList(baseRoot, "tags");
    }
}
