namespace DeezSpoTag.Services.Genre;

/// <summary>
/// One built-in region, as the Genre Intelligence UI presents it.
/// </summary>
/// <param name="Slug">Stable identifier used by <see cref="PersonalGenreTaxon.Regions"/>.</param>
/// <param name="Name">Display name for the navigation tab.</param>
/// <param name="Order">Position in the built-in region order.</param>
/// <param name="Icon">Font Awesome class for the navigation tab, or null when the label stands alone.</param>
public sealed record PersonalGenreRegion(string Slug, string Name, int Order, string? Icon = null);

/// <summary>
/// The authoritative list of built-in region slugs.
/// </summary>
/// <remarks>
/// <para>
/// These are navigation areas for vocabulary, not a continent taxonomy and not a
/// partition of the world. MENA overlaps Africa and Latin America &amp; Caribbean
/// crosses conventional continental boundaries, so a term may belong to more than one
/// region and nothing here claims a country belongs to exactly one region.
/// </para>
/// <para>
/// This is deliberately a list of records rather than an enum. An enum would make the
/// set of regions a compile-time constant that a shipped build could never extend, and
/// it would invite code that treats a region as a permanent classification axis. A
/// region added here is immediately usable by a taxon on the next start, with no
/// database migration, because region membership is stored as free-form slug strings.
/// </para>
/// <para>
/// "Global" and "All" are deliberately absent. Neither is a stored region: a taxon is
/// global by having no region at all, and "All" is a browsing view over every term.
/// </para>
/// </remarks>
public static class PersonalGenreRegions
{
    public const string Africa = "africa";
    public const string NorthAmerica = "north-america";
    public const string LatinAmericaCaribbean = "latin-america-caribbean";
    public const string Europe = "europe";
    public const string Mena = "mena";
    public const string CentralAsia = "central-asia";
    public const string SouthAsia = "south-asia";
    public const string EastAsia = "east-asia";
    public const string SoutheastAsia = "southeast-asia";
    public const string OceaniaPacific = "oceania-pacific";

    /// <remarks>
    /// The order below is the navigation order of the region tabs: North America,
    /// Latin America &amp; Caribbean, Africa, MENA, Europe, Central Asia, South Asia,
    /// East Asia, Southeast Asia and Oceania / Pacific. MENA deliberately precedes
    /// Europe so the strip reads Africa, MENA, Europe, grouping the two regions that
    /// share North Africa rather than separating them.
    ///
    /// <para>
    /// Central Asia is an ordinary entry and was a genuine omission from the original
    /// specification rather than a judgement that those countries belong to South
    /// Asia. Kazakhstan, Kyrgyzstan, Tajikistan, Turkmenistan and Uzbekistan are
    /// Central Asia in current geographic classification, and folding them into a
    /// neighbouring tab to avoid adding a region would have been the wrong call.
    /// </para>
    /// <para>
    /// No region is privileged in the data model. The ordering here is presentation
    /// only, exactly as Africa's position is.
    /// </para>
    /// </remarks>
    private static readonly PersonalGenreRegion[] BuiltIn =
    [
        new(NorthAmerica, "North America", 10, "fa-globe-americas"),
        new(LatinAmericaCaribbean, "Latin America & Caribbean", 20),
        new(Africa, "Africa", 25, "fa-globe-africa"),
        new(Mena, "MENA", 30),
        new(Europe, "Europe", 40),
        new(CentralAsia, "Central Asia", 50),
        new(SouthAsia, "South Asia", 60),
        new(EastAsia, "East Asia", 70),
        new(SoutheastAsia, "Southeast Asia", 80),
        new(OceaniaPacific, "Oceania / Pacific", 90),
    ];

    private static readonly IReadOnlyDictionary<string, PersonalGenreRegion> BySlug =
        BuiltIn.ToDictionary(item => item.Slug, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every built-in region, in display order.</summary>
    public static IReadOnlyList<PersonalGenreRegion> All => BuiltIn;

    /// <summary>Every built-in region slug, in display order.</summary>
    public static IReadOnlyList<string> AllSlugs => BuiltIn.Select(item => item.Slug).ToArray();

    /// <summary>Whether the slug names a built-in region.</summary>
    public static bool IsKnown(string? slug)
        => !string.IsNullOrWhiteSpace(slug) && BySlug.ContainsKey(slug.Trim());

    /// <summary>
    /// The built-in region for a slug, or null when the slug is not one.
    /// </summary>
    /// <remarks>
    /// A stored slug that no longer names a built-in region is still preserved by
    /// <see cref="Order"/>; it simply has no display name here. Dropping it would lose
    /// a user's own grouping the first time a region were renamed.
    /// </remarks>
    public static PersonalGenreRegion? Find(string? slug)
        => slug is not null && BySlug.TryGetValue(slug.Trim(), out var region) ? region : null;

    /// <summary>The display name for a slug, or the slug itself when unrecognized.</summary>
    public static string DisplayName(string? slug) => Find(slug)?.Name ?? (slug?.Trim() ?? string.Empty);

    /// <summary>
    /// Cleans up a user-supplied region selection.
    /// </summary>
    /// <remarks>
    /// Blank entries are dropped, duplicates collapse, and the result is ordered by the
    /// built-in display order so the same selection always reads the same way. A null
    /// result means "unscoped", which is a distinct state from an explicitly empty
    /// selection and is stored as NULL rather than '[]' for exactly that reason.
    /// </remarks>
    public static IReadOnlyList<string>? Normalize(IEnumerable<string>? slugs)
    {
        if (slugs is null)
        {
            return null;
        }

        var seen = slugs
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .Select(slug => slug.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (seen.Count == 0)
        {
            return null;
        }

        return seen
            .OrderBy(slug => Find(slug)?.Order ?? int.MaxValue)
            .ThenBy(slug => slug, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
