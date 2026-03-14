using System;

namespace DeezSpoTag.Core.Models.Settings;

public class TaggingProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; } = false;
    public UnifiedTagConfig TagConfig { get; set; } = new();
    public AutoTagSettings AutoTag { get; set; } = new();
    public TechnicalTagSettings Technical { get; set; } = new();
    public FolderStructureSettings FolderStructure { get; set; } = new();
    public VerificationSettings Verification { get; set; } = new();
}
