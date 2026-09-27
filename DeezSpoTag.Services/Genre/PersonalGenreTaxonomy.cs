namespace DeezSpoTag.Services.Genre;

public static class PersonalGenreTaxonomy
{
    public const string Version = "personal-genre-v1";

    private static readonly PersonalGenreTaxon[] DefaultTaxa =
    [
        new("afrobeats", "Afrobeats", PersonalGenreTaxonKind.Genre, Aliases: ["Afrobeat", "Afro Beats"]),
        new("afropop", "Afropop", PersonalGenreTaxonKind.Genre, Aliases: ["Afro-Pop", "Afro Pop"]),
        new("bongo-flava", "Bongo Flava", PersonalGenreTaxonKind.Genre, Aliases: ["Bongo-Flava", "BongoFlava"]),
        new("amapiano", "Amapiano", PersonalGenreTaxonKind.Genre),
        new("hip-hop", "Hip-Hop", PersonalGenreTaxonKind.Genre, Aliases: ["Hip Hop", "HipHop"]),
        new("rnb", "R&B", PersonalGenreTaxonKind.Genre, Aliases: ["Rnb", "R and B", "Rhythm and Blues"]),
        new("reggae", "Reggae", PersonalGenreTaxonKind.Genre),
        new("dancehall", "Dancehall", PersonalGenreTaxonKind.Genre),
        new("gospel", "Gospel", PersonalGenreTaxonKind.Genre),
        new("electronic", "Electronic", PersonalGenreTaxonKind.Genre),
        new("house", "House", PersonalGenreTaxonKind.Genre),
        new("pop", "Pop", PersonalGenreTaxonKind.Genre),
        new("soul", "Soul", PersonalGenreTaxonKind.Genre),
        new("rock", "Rock", PersonalGenreTaxonKind.Genre),
        new("jazz", "Jazz", PersonalGenreTaxonKind.Genre),
        new("classical", "Classical", PersonalGenreTaxonKind.Genre),

        new("afro-fusion", "Afro-Fusion", PersonalGenreTaxonKind.Style),
        new("afro-soul", "Afro-Soul", PersonalGenreTaxonKind.Style),
        new("alt-rnb", "Alternative R&B", PersonalGenreTaxonKind.Style, ParentId: "rnb", Aliases: ["Alt R&B", "Alternative RnB"]),
        new("trap", "Trap", PersonalGenreTaxonKind.Style, ParentId: "hip-hop"),
        new("boom-bap", "Boom Bap", PersonalGenreTaxonKind.Style, ParentId: "hip-hop"),
        new("deep-house", "Deep House", PersonalGenreTaxonKind.Style, ParentId: "house"),
        new("afro-house", "Afro House", PersonalGenreTaxonKind.Style, ParentId: "house"),
        new("praise", "Praise", PersonalGenreTaxonKind.Style, ParentId: "gospel"),
        new("worship", "Worship", PersonalGenreTaxonKind.Style, ParentId: "gospel"),

        new("swahili-pop", "Swahili Pop", PersonalGenreTaxonKind.Substyle, ParentId: "bongo-flava"),
        new("bongo-rap", "Bongo Rap", PersonalGenreTaxonKind.Substyle, ParentId: "bongo-flava"),

        new("east-africa", "East Africa", PersonalGenreTaxonKind.Context, ContextOnly: true),
        new("tanzania", "Tanzania", PersonalGenreTaxonKind.Context, ContextOnly: true),
        new("kenya", "Kenya", PersonalGenreTaxonKind.Context, ContextOnly: true),
        new("uganda", "Uganda", PersonalGenreTaxonKind.Context, ContextOnly: true),
        new("south-africa", "South Africa", PersonalGenreTaxonKind.Context, ContextOnly: true),
        new("nigeria", "Nigeria", PersonalGenreTaxonKind.Context, ContextOnly: true),
        new("ghana", "Ghana", PersonalGenreTaxonKind.Context, ContextOnly: true)
    ];

    private static readonly IReadOnlyDictionary<string, PersonalGenreTaxon> ById =
        DefaultTaxa.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, PersonalGenreTaxon> ByLookup =
        BuildLookup();

    public static IReadOnlyList<PersonalGenreTaxon> GetDefaultTaxa() => DefaultTaxa;

    public static bool TryGetById(string? id, out PersonalGenreTaxon taxon)
        => ById.TryGetValue(id?.Trim() ?? string.Empty, out taxon!);

    public static bool TryMatch(string? value, out PersonalGenreTaxon taxon)
        => ByLookup.TryGetValue(Normalize(value), out taxon!);

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value.Trim()
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static IReadOnlyDictionary<string, PersonalGenreTaxon> BuildLookup()
    {
        var result = new Dictionary<string, PersonalGenreTaxon>(StringComparer.Ordinal);
        foreach (var taxon in DefaultTaxa)
        {
            Add(taxon.Name, taxon);
            Add(taxon.Id, taxon);
            if (taxon.Aliases is not null)
            {
                foreach (var alias in taxon.Aliases)
                {
                    Add(alias, taxon);
                }
            }
        }

        return result;

        void Add(string value, PersonalGenreTaxon taxon)
        {
            var key = Normalize(value);
            if (key.Length > 0)
            {
                result[key] = taxon;
            }
        }
    }
}
