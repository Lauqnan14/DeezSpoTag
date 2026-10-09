namespace DeezSpoTag.Services.Library.Dj;

/// <summary>
/// Valid DJ selections.
/// </summary>
/// <remarks>
/// <para>Lives in the DJ domain rather than in Meloday's settings model, because the
/// resolver needs it and Meloday must not be a dependency of the DJ engine. It is the
/// whole of the shared vocabulary between them.</para>
///
/// <para>The special values "random" and "none" are known here. Specific DJs are whatever the
/// strategy catalogue registers, so a new DJ needs no change to this file, to the
/// settings schema, or to the UI's option list — it appears because it was
/// registered.</para>
///
/// <para>An unrecognised value normalises to Random rather than throwing or falling back
/// to a hard-coded DJ: settings are hand-editable, and a typo should cost a playlist
/// its DJ, not its generation.</para>
/// </remarks>
public static class MelodayDjSelections
{
    /// <summary>Choose one eligible registered DJ per time occurrence.</summary>
    public const string Random = "random";

    /// <summary>Use Meloday's normal track selection and ordering without a DJ.</summary>
    public const string None = "none";

    public static bool IsRandom(string? value)
        => string.Equals(Normalize(value), Random, StringComparison.Ordinal);

    public static bool IsNone(string? value)
        => string.Equals(Normalize(value), None, StringComparison.Ordinal);

    public static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Random
            : value.Trim().ToLowerInvariant();
}
