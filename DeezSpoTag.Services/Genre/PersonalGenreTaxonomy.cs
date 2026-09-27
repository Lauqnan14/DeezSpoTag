namespace DeezSpoTag.Services.Genre;

public static class PersonalGenreTaxonomy
{
    public const string Version = "personal-genre-v1";

    private static readonly PersonalGenreTaxon[] DefaultTaxa =
    [
        new("afrobeat", "Afrobeat", PersonalGenreTaxonKind.Genre),
        new("afrobeats", "Afrobeats", PersonalGenreTaxonKind.Genre, Aliases: ["Afro Beats"]),
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

        // African and regional canonical genres. Geography remains Context; these are musical forms.
        new("genge", "Genge", PersonalGenreTaxonKind.Genre, Aliases: ["Genge Music"]),
        new("benga", "Benga", PersonalGenreTaxonKind.Genre, Aliases: ["Benga Music"]),
        new("ohangla", "Ohangla", PersonalGenreTaxonKind.Genre),
        new("mugithi", "Mugithi", PersonalGenreTaxonKind.Genre),
        new("taarab", "Taarab", PersonalGenreTaxonKind.Genre),
        new("singeli", "Singeli", PersonalGenreTaxonKind.Genre),
        new("soukous", "Soukous", PersonalGenreTaxonKind.Genre),
        new("congolese-rumba", "Congolese Rumba", PersonalGenreTaxonKind.Genre, Aliases: ["Congo Rumba", "Rumba Congolaise"]),
        new("highlife", "Highlife", PersonalGenreTaxonKind.Genre),
        new("coupe-decale", "Coupé-Décalé", PersonalGenreTaxonKind.Genre, Aliases: ["Coupe Decale", "Coupé Décalé"]),
        new("kwaito", "Kwaito", PersonalGenreTaxonKind.Genre),

        new("afropop", "Afropop", PersonalGenreTaxonKind.Style, ParentIds: ["afrobeats", "pop"], Aliases: ["Afro-Pop", "Afro Pop"]),
        new("afro-fusion", "Afro-Fusion", PersonalGenreTaxonKind.Style, ParentIds: ["afrobeats"]),
        new("afro-soul", "Afro-Soul", PersonalGenreTaxonKind.Style, ParentIds: ["soul", "afrobeats"]),
        new("alt-rnb", "Alternative R&B", PersonalGenreTaxonKind.Style, ParentIds: ["rnb"], Aliases: ["Alt R&B", "Alternative RnB"]),
        new("trap", "Trap", PersonalGenreTaxonKind.Style, ParentIds: ["hip-hop"]),
        new("boom-bap", "Boom Bap", PersonalGenreTaxonKind.Style, ParentIds: ["hip-hop"]),
        new("deep-house", "Deep House", PersonalGenreTaxonKind.Style, ParentIds: ["house"]),
        new("afro-house", "Afro House", PersonalGenreTaxonKind.Style, ParentIds: ["house", "afrobeats"]),
        new("praise", "Praise", PersonalGenreTaxonKind.Style, ParentIds: ["gospel"]),
        new("worship", "Worship", PersonalGenreTaxonKind.Style, ParentIds: ["gospel"]),
        new("bongo-flava-pop", "Bongo Flava Pop", PersonalGenreTaxonKind.Style, ParentIds: ["bongo-flava", "pop"]),
        new("bongo-flava-rnb", "Bongo Flava R&B", PersonalGenreTaxonKind.Style, ParentIds: ["bongo-flava", "rnb"], Aliases: ["Bongo Flava RnB"]),
        new("bongo-flava-rap", "Bongo Flava Rap", PersonalGenreTaxonKind.Style, ParentIds: ["bongo-flava", "hip-hop"], Aliases: ["Bongo Rap"]),
        new("gengetone", "Gengetone", PersonalGenreTaxonKind.Style, ParentIds: ["genge", "hip-hop"]),
        new("kenyan-drill", "Kenyan Drill", PersonalGenreTaxonKind.Style, ParentIds: ["hip-hop"], Aliases: ["Kenya Drill"]),
        new("ndombolo", "Ndombolo", PersonalGenreTaxonKind.Style, ParentIds: ["soukous", "congolese-rumba"]),
        new("east-african-gospel", "East African Gospel", PersonalGenreTaxonKind.Style, ParentIds: ["gospel"]),
        new("swahili-gospel", "Swahili Gospel", PersonalGenreTaxonKind.Style, ParentIds: ["gospel"]),

        new("swahili-pop", "Swahili Pop", PersonalGenreTaxonKind.Substyle, ParentIds: ["bongo-flava", "afropop"]),

        new("afrosounds", "Afrosounds", PersonalGenreTaxonKind.Context, ContextOnly: true),
        new("east-africa", "East Africa", PersonalGenreTaxonKind.Context, ContextOnly: true),
        new("swahili", "Swahili", PersonalGenreTaxonKind.Context, ContextOnly: true),
        new("tanzania", "Tanzania", PersonalGenreTaxonKind.Context, ContextOnly: true),
        new("kenya", "Kenya", PersonalGenreTaxonKind.Context, ContextOnly: true),
        new("uganda", "Uganda", PersonalGenreTaxonKind.Context, ContextOnly: true),
        new("south-africa", "South Africa", PersonalGenreTaxonKind.Context, ContextOnly: true),
        new("nigeria", "Nigeria", PersonalGenreTaxonKind.Context, ContextOnly: true),
        new("ghana", "Ghana", PersonalGenreTaxonKind.Context, ContextOnly: true),
        new("congo", "Congo", PersonalGenreTaxonKind.Context, ContextOnly: true),
        new("drc", "DR Congo", PersonalGenreTaxonKind.Context, ContextOnly: true, Aliases: ["Democratic Republic of the Congo", "DRC"]),
        new("zilizopendwa", "Zilizopendwa", PersonalGenreTaxonKind.Context, ContextOnly: true, Aliases: ["Swahili Oldies"])
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
