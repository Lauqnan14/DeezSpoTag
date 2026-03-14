namespace DeezSpoTag.Web.Services;

public sealed class AutoTagOrganizerOptions
{
    public bool OnlyMoveWhenTagged { get; set; }
    public string? MoveTaggedPath { get; set; }
    public string? MoveUntaggedPath { get; set; }
    public bool DryRun { get; set; }
    public bool IncludeSubfolders { get; set; } = true;
    public bool MoveMisplacedFiles { get; set; } = true;
    public bool RenameFilesToTemplate { get; set; } = true;
    public bool RemoveEmptyFolders { get; set; } = true;
    public List<string> PreferredExtensions { get; set; } = new();
    public bool? UsePrimaryArtistFoldersOverride { get; set; }
    public string? MultiArtistSeparatorOverride { get; set; }
}
