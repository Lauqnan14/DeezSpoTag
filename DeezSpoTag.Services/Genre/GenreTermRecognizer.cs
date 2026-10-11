namespace DeezSpoTag.Services.Genre;

using DeezSpoTag.Core.Utils;

/// <summary>
/// Decides whether a complete metadata value is already one known term.
///
/// The reader and Genre Normalization both ask this before they split on a
/// separator. Recognition order is the saved normalization alias, then the
/// researched exclusion and ambiguity lists, then the taxonomy (canonical names,
/// researched aliases and custom terms). A value that matches any of those is
/// one term.
/// </summary>
public static class GenreTermRecognizer
{
    public static bool IsWholeValueRecognized(
        string? value,
        PersonalGenreCatalog? catalog = null,
        GenreNormalizationSnapshot? normalization = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        catalog ??= PersonalGenreCatalog.Default;
        var key = GenreTagAliasNormalizer.ToLookupKey(value);
        if (key.Length == 0)
        {
            return false;
        }

        if (normalization?.AliasMap.ContainsKey(key) == true)
        {
            return true;
        }

        if (catalog.IsResearchedExclusion(value) || catalog.IsResearchedAmbiguity(value))
        {
            return true;
        }

        return catalog.LookupLabel(value).Status != PersonalGenreCatalog.LabelLookupStatus.None;
    }
}
