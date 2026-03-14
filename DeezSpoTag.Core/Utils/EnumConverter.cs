using DeezSpoTag.Core.Enums;

namespace DeezSpoTag.Core.Utils;

/// <summary>
/// Utility class for converting between string and enum types used in deezspotag settings
/// </summary>
public static class EnumConverter
{
    /// <summary>
    /// Convert string overwrite option to OverwriteOption enum
    /// Ported from deezspotag overwrite option mapping
    /// </summary>
    public static OverwriteOption StringToOverwriteOption(string value)
    {
        return value switch
        {
            "y" or "overwrite" => OverwriteOption.Overwrite,
            "n" or "dont_overwrite" => OverwriteOption.DontOverwrite,
            "e" or "dont_check_ext" => OverwriteOption.DontCheckExt,
            "b" or "keep_both" => OverwriteOption.KeepBoth,
            "t" or "only_tags" => OverwriteOption.OnlyTags,
            "l" or "only_lower_bitrates" => OverwriteOption.OnlyLowerBitrates,
            _ => OverwriteOption.DontOverwrite // Default
        };
    }

    /// <summary>
    /// Convert OverwriteOption enum to string
    /// </summary>
    public static string OverwriteOptionToString(OverwriteOption option)
    {
        return option switch
        {
            OverwriteOption.Overwrite => "y",
            OverwriteOption.DontOverwrite => "n",
            OverwriteOption.DontCheckExt => "e",
            OverwriteOption.KeepBoth => "b",
            OverwriteOption.OnlyTags => "t",
            OverwriteOption.OnlyLowerBitrates => "l",
            _ => "n" // Default
        };
    }

    /// <summary>
    /// Convert string features option to FeaturesOption enum
    /// Ported from deezspotag features option mapping
    /// </summary>
    public static FeaturesOption StringToFeaturesOption(string value)
    {
        return value switch
        {
            "0" or "no_change" => FeaturesOption.NoChange,
            "1" or "remove_title" => FeaturesOption.RemoveTitle,
            "2" or "move_title" => FeaturesOption.MoveTitle,
            "3" or "remove_title_album" => FeaturesOption.RemoveTitleAlbum,
            _ => FeaturesOption.NoChange // Default
        };
    }

    /// <summary>
    /// Convert FeaturesOption enum to string
    /// </summary>
    public static string FeaturesOptionToString(FeaturesOption option)
    {
        return option switch
        {
            FeaturesOption.NoChange => "0",
            FeaturesOption.RemoveTitle => "1",
            FeaturesOption.MoveTitle => "2",
            FeaturesOption.RemoveTitleAlbum => "3",
            _ => "0" // Default
        };
    }
}