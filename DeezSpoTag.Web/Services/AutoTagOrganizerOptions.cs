namespace DeezSpoTag.Web.Services;

public sealed class AutoTagOrganizerOptions
{
    public const string DuplicateConflictKeepBest = "keep_best";
    public const string DuplicateConflictKeepLower = "keep_lower";
    public const string DuplicateConflictMoveToDuplicates = "move_to_duplicates";
    public const string DuplicateConflictKeepBoth = "keep_both";

    public const string ArtworkPolicyPreserveExisting = "preserve_existing";
    public const string ArtworkPolicyPreferHigherResolution = "prefer_higher_resolution";
    public const string ArtworkPolicyPreserveAll = "preserve_all";

    public const string LyricsPolicyPreserveExisting = "preserve_existing";
    public const string LyricsPolicyMerge = "merge";
    public const string LyricsPolicyPreferIncoming = "prefer_incoming";

    public bool OnlyMoveWhenTagged { get; set; }
    public string? MoveTaggedPath { get; set; }
    public string? MoveUntaggedPath { get; set; }
    public bool DryRun { get; set; }
    public bool IncludeSubfolders { get; set; } = true;
    public bool MoveMisplacedFiles { get; set; } = true;
    public bool RenameFilesToTemplate { get; set; } = true;
    public bool RemoveEmptyFolders { get; set; } = true;
    public bool MergeIntoExistingDestinationFolders { get; set; } = true;
    public bool ResolveSameTrackQualityConflicts { get; set; } = true;
    public bool KeepBothOnUnresolvedConflicts { get; set; } = true;
    public bool OnlyReorganizeAlbumsWithFullTrackSets { get; set; }
    public bool SkipCompilationFolders { get; set; }
    public bool SkipVariousArtistsFolders { get; set; }
    public bool GenerateReconciliationReport { get; set; }
    public bool UseShazamForUntaggedFiles { get; set; }
    public string DuplicateConflictPolicy { get; set; } = DuplicateConflictKeepBest;
    public string DuplicatesFolderName { get; set; } = DuplicateCleanerService.DuplicatesFolderName;
    public string ArtworkPolicy { get; set; } = ArtworkPolicyPreserveExisting;
    public string LyricsPolicy { get; set; } = LyricsPolicyMerge;
    public List<string> PreferredExtensions { get; set; } = new();
    public bool? UsePrimaryArtistFoldersOverride { get; set; }
    public string? MultiArtistSeparatorOverride { get; set; }
}
