using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services;

internal static class EnhancementTargetReasons
{
    public const string ExplicitTarget = "explicit-target";
    public const string RecentDownloads = "recent-downloads";
    public const string MissingCoreMetadata = "missing-core-metadata";
    public const string FolderEnumeration = "folder-enumeration";
}

internal sealed class EnhancementRunManifest
{
    public string Reason { get; set; } = EnhancementTargetReasons.FolderEnumeration;
    public int RequestedCount { get; set; }
    public int UsableCount { get; set; }
    public List<EnhancementRunManifestItem> Items { get; set; } = new();

    [JsonIgnore]
    public IReadOnlyList<string> CurrentPaths => Items
        .Select(item => item.CurrentPath)
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .ToList();

    [JsonIgnore]
    public IReadOnlyList<long> TrackIds => Items
        .Where(item => item.TrackId is > 0)
        .Select(item => item.TrackId!.Value)
        .Distinct()
        .ToList();
}

internal sealed class EnhancementRunManifestItem
{
    public long? TrackId { get; set; }
    public string OriginalPath { get; set; } = string.Empty;
    public string CurrentPath { get; set; } = string.Empty;
}
