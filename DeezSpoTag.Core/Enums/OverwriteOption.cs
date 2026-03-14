namespace DeezSpoTag.Core.Enums;

/// <summary>
/// Overwrite options for file handling (ported from deezspotag OverwriteOption)
/// </summary>
public enum OverwriteOption
{
    Overwrite,
    DontOverwrite,
    DontCheckExt,
    KeepBoth,
    OnlyTags,
    OnlyLowerBitrates
}