namespace DeezSpoTag.Core.Enums;

/// <summary>
/// Featured artists handling options (ported from deezspotag FeaturesOption)
/// </summary>
public enum FeaturesOption
{
    /// <summary>
    /// Do nothing
    /// </summary>
    NoChange = 0,
    
    /// <summary>
    /// Remove from track title
    /// </summary>
    RemoveTitle = 1,
    
    /// <summary>
    /// Move to track title
    /// </summary>
    MoveTitle = 2,
    
    /// <summary>
    /// Remove from track title and album title
    /// </summary>
    RemoveTitleAlbum = 3
}