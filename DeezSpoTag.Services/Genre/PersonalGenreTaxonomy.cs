namespace DeezSpoTag.Services.Genre;

public static class PersonalGenreTaxonomy
{
    public const string Version = "personal-genre-v1";

    // Region slugs are regional navigation metadata only. They organize vocabulary;
    // they never gate resolution. A term with no Regions is unscoped and therefore
    // global, which is why the universal genres below carry none.
    private static readonly PersonalGenreTaxon[] DefaultTaxa =
    [
        // =================================================================
        // Global / universal Genres. Unscoped on purpose: these are used
        // worldwide and are never duplicated per region.
        // =================================================================
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

        // Conservative additions to the universal vocabulary. Top-level forms with
        // no speculative parents: Genre Intelligence only walks parents when
        // "Include parent genres" is on, and inventing a tree here would change what
        // that setting produces.
        new("blues", "Blues", PersonalGenreTaxonKind.Genre),
        new("funk", "Funk", PersonalGenreTaxonKind.Genre),
        new("disco", "Disco", PersonalGenreTaxonKind.Genre),
        new("metal", "Metal", PersonalGenreTaxonKind.Genre),
        new("punk", "Punk", PersonalGenreTaxonKind.Genre),
        new("folk", "Folk", PersonalGenreTaxonKind.Genre),
        new("country", "Country", PersonalGenreTaxonKind.Genre),
        new("techno", "Techno", PersonalGenreTaxonKind.Genre),

        // =================================================================
        // Africa. These entries, their names, aliases and parents are the
        // shipped African taxonomy and are unchanged; only region metadata
        // was added.
        // =================================================================
        new("afrobeat", "Afrobeat", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Africa]),
        new("afrobeats", "Afrobeats", PersonalGenreTaxonKind.Genre, Aliases: ["Afro Beats"], Regions: [PersonalGenreRegions.Africa]),
        new("amapiano", "Amapiano", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Africa]),
        new("bongo-flava", "Bongo Flava", PersonalGenreTaxonKind.Genre, Aliases: ["Bongo-Flava", "BongoFlava"], Regions: [PersonalGenreRegions.Africa]),
        new("genge", "Genge", PersonalGenreTaxonKind.Genre, Aliases: ["Genge Music"], Regions: [PersonalGenreRegions.Africa]),
        new("benga", "Benga", PersonalGenreTaxonKind.Genre, Aliases: ["Benga Music"], Regions: [PersonalGenreRegions.Africa]),
        new("ohangla", "Ohangla", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Africa]),
        new("mugithi", "Mugithi", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Africa]),
        new("taarab", "Taarab", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Africa]),
        new("singeli", "Singeli", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Africa]),
        new("soukous", "Soukous", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Africa]),
        new("congolese-rumba", "Congolese Rumba", PersonalGenreTaxonKind.Genre, Aliases: ["Congo Rumba", "Rumba Congolaise"], Regions: [PersonalGenreRegions.Africa]),
        new("highlife", "Highlife", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Africa]),
        new("coupe-decale", "Coupé-Décalé", PersonalGenreTaxonKind.Genre, Aliases: ["Coupe Decale", "Coupé Décalé"], Regions: [PersonalGenreRegions.Africa]),
        new("kwaito", "Kwaito", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Africa]),

        // =================================================================
        // North America.
        // =================================================================
        new("bluegrass", "Bluegrass", PersonalGenreTaxonKind.Style, ParentIds: ["country"], Regions: [PersonalGenreRegions.NorthAmerica]),
        new("americana", "Americana", PersonalGenreTaxonKind.Style, ParentIds: ["folk", "country"], Regions: [PersonalGenreRegions.NorthAmerica]),
        new("cajun", "Cajun", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.NorthAmerica]),
        new("zydeco", "Zydeco", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.NorthAmerica]),
        new("honky-tonk", "Honky Tonk", PersonalGenreTaxonKind.Style, ParentIds: ["country"], Aliases: ["Hony Tonk"], Regions: [PersonalGenreRegions.NorthAmerica]),
        new("western-swing", "Western Swing", PersonalGenreTaxonKind.Style, ParentIds: ["country"], Regions: [PersonalGenreRegions.NorthAmerica]),
        new("old-time", "Old-Time", PersonalGenreTaxonKind.Style, ParentIds: ["folk", "country"], Regions: [PersonalGenreRegions.NorthAmerica]),
        // Tejano spans the US-Mexican border, so it belongs to both regional
        // vocabularies without being duplicated.
        new("tejano", "Tejano", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.NorthAmerica, PersonalGenreRegions.LatinAmericaCaribbean]),

        // =================================================================
        // Latin America & Caribbean.
        // =================================================================
        new("reggaeton", "Reggaeton", PersonalGenreTaxonKind.Genre, Aliases: ["Reggaetón"], Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("salsa", "Salsa", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("bachata", "Bachata", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("merengue", "Merengue", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("cumbia", "Cumbia", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("samba", "Samba", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("bossa-nova", "Bossa Nova", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("mariachi", "Mariachi", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("ranchera", "Ranchera", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("vallenato", "Vallenato", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("tango", "Tango", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("bolero", "Bolero", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("son-cubano", "Son Cubano", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("timba", "Timba", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("mambo", "Mambo", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("cha-cha-cha", "Cha-Cha-Chá", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("corrido", "Corrido", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("norteno", "Norteño", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("forro", "Forró", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("mpb", "MPB", PersonalGenreTaxonKind.Genre, Aliases: ["Música Brasileira"], Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("soca", "Soca", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("calypso", "Calypso", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("compas", "Compas", PersonalGenreTaxonKind.Genre, Aliases: ["Compás"], Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),
        new("zouk", "Zouk", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.LatinAmericaCaribbean]),

        // =================================================================
        // Europe.
        // =================================================================
        new("fado", "Fado", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Europe]),
        new("flamenco", "Flamenco", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Europe]),
        new("chanson", "Chanson", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Europe]),
        new("eurodance", "Eurodance", PersonalGenreTaxonKind.Style, ParentIds: ["electronic", "pop"], Regions: [PersonalGenreRegions.Europe]),
        new("europop", "Europop", PersonalGenreTaxonKind.Style, ParentIds: ["pop"], Regions: [PersonalGenreRegions.Europe]),
        new("schlager", "Schlager", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Europe]),
        new("italo-disco", "Italo Disco", PersonalGenreTaxonKind.Style, ParentIds: ["disco"], Regions: [PersonalGenreRegions.Europe]),
        new("rebetiko", "Rebetiko", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Europe]),
        // Grime descends from UK garage and dancehall rather than from one parent,
        // so it carries no parent rather than a convenient wrong one.
        new("grime", "Grime", PersonalGenreTaxonKind.Style, Regions: [PersonalGenreRegions.Europe]),
        new("uk-garage", "UK Garage", PersonalGenreTaxonKind.Style, ParentIds: ["electronic"], Regions: [PersonalGenreRegions.Europe]),
        // A top-level electronic form, sibling to House rather than a sub-style of it.
        new("drum-and-bass", "Drum and Bass", PersonalGenreTaxonKind.Genre, Aliases: ["Drum & Bass", "DnB"], Regions: [PersonalGenreRegions.Europe]),
        new("trip-hop", "Trip-Hop", PersonalGenreTaxonKind.Style, ParentIds: ["hip-hop"], Regions: [PersonalGenreRegions.Europe]),
        new("irish-folk", "Irish Folk", PersonalGenreTaxonKind.Style, ParentIds: ["folk"], Regions: [PersonalGenreRegions.Europe]),

        // =================================================================
        // Middle East & North Africa. Raï and Gnawa are listed under both
        // Africa and MENA on purpose: the groupings genuinely overlap.
        // =================================================================
        new("rai", "Raï", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Africa, PersonalGenreRegions.Mena]),
        new("gnawa", "Gnawa", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Africa, PersonalGenreRegions.Mena]),
        new("arabic-pop", "Arabic Pop", PersonalGenreTaxonKind.Style, ParentIds: ["pop"], Regions: [PersonalGenreRegions.Mena]),
        new("khaliji", "Khaliji", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Mena]),
        new("dabke", "Dabke", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Mena]),
        new("persian-pop", "Persian Pop", PersonalGenreTaxonKind.Style, ParentIds: ["pop"], Regions: [PersonalGenreRegions.Mena]),
        new("persian-classical", "Persian Classical", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Mena]),
        new("arabic-classical", "Arabic Classical", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.Mena]),

        // =================================================================
        // Central Asia. A conservative first pass: two established forms that can
        // be defended on their own terms. Cultural practices and instrument names
        // (akyn, aitys, epic storytelling, dombra) are deliberately absent, because
        // whether any of those is a Genre, a Style, a Scene or a Context is a
        // separate semantic decision and not one to make by padding a region.
        // =================================================================
        new("shashmaqom", "Shashmaqom", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.CentralAsia]),
        new("kuy", "Kuy", PersonalGenreTaxonKind.Genre, Aliases: ["Dombra Kuy"], Regions: [PersonalGenreRegions.CentralAsia]),

        // =================================================================
        // South Asia.
        // =================================================================
        new("indian-classical", "Indian Classical", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.SouthAsia]),
        new("hindustani", "Hindustani", PersonalGenreTaxonKind.Style, ParentIds: ["indian-classical"], Regions: [PersonalGenreRegions.SouthAsia]),
        new("carnatic", "Carnatic", PersonalGenreTaxonKind.Style, ParentIds: ["indian-classical"], Regions: [PersonalGenreRegions.SouthAsia]),
        new("dhrupad", "Dhrupad", PersonalGenreTaxonKind.Style, ParentIds: ["hindustani"], Regions: [PersonalGenreRegions.SouthAsia]),
        new("bhangra", "Bhangra", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.SouthAsia]),
        new("qawwali", "Qawwali", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.SouthAsia]),
        new("ghazal", "Ghazal", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.SouthAsia]),
        new("indian-pop", "Indian Pop", PersonalGenreTaxonKind.Style, ParentIds: ["pop"], Regions: [PersonalGenreRegions.SouthAsia]),
        // Bollywood is a film/entertainment context spanning many musical forms,
        // so it stays Context rather than becoming a canonical Genre.
        new("bollywood", "Bollywood", PersonalGenreTaxonKind.Context, ContextOnly: true, Regions: [PersonalGenreRegions.SouthAsia]),

        // =================================================================
        // East Asia.
        // =================================================================
        new("j-pop", "J-Pop", PersonalGenreTaxonKind.Style, ParentIds: ["pop"], Aliases: ["Japanese Pop"], Regions: [PersonalGenreRegions.EastAsia]),
        new("k-pop", "K-Pop", PersonalGenreTaxonKind.Style, ParentIds: ["pop"], Aliases: ["Korean Pop"], Regions: [PersonalGenreRegions.EastAsia]),
        new("mandopop", "Mandopop", PersonalGenreTaxonKind.Style, ParentIds: ["pop"], Regions: [PersonalGenreRegions.EastAsia]),
        new("cantopop", "Cantopop", PersonalGenreTaxonKind.Style, ParentIds: ["pop"], Regions: [PersonalGenreRegions.EastAsia]),
        new("city-pop", "City Pop", PersonalGenreTaxonKind.Style, ParentIds: ["pop"], Regions: [PersonalGenreRegions.EastAsia]),
        new("shibuya-kei", "Shibuya-kei", PersonalGenreTaxonKind.Style, ParentIds: ["pop"], Regions: [PersonalGenreRegions.EastAsia]),
        new("j-rock", "J-Rock", PersonalGenreTaxonKind.Style, ParentIds: ["rock"], Regions: [PersonalGenreRegions.EastAsia]),
        new("k-rock", "K-Rock", PersonalGenreTaxonKind.Style, ParentIds: ["rock"], Regions: [PersonalGenreRegions.EastAsia]),
        new("enka", "Enka", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.EastAsia]),
        new("trot", "Trot", PersonalGenreTaxonKind.Genre, Aliases: ["Korean Trot"], Regions: [PersonalGenreRegions.EastAsia]),
        new("pansori", "Pansori", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.EastAsia]),

        // =================================================================
        // Southeast Asia.
        // =================================================================
        new("dangdut", "Dangdut", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.SoutheastAsia]),
        new("keroncong", "Keroncong", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.SoutheastAsia]),
        new("gamelan", "Gamelan", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.SoutheastAsia]),
        new("jaipongan", "Jaipongan", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.SoutheastAsia]),
        new("luk-thung", "Luk Thung", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.SoutheastAsia]),
        new("mor-lam", "Mor Lam", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.SoutheastAsia]),
        new("p-pop", "P-Pop", PersonalGenreTaxonKind.Style, ParentIds: ["pop"], Regions: [PersonalGenreRegions.SoutheastAsia]),
        new("v-pop", "V-Pop", PersonalGenreTaxonKind.Style, ParentIds: ["pop"], Regions: [PersonalGenreRegions.SoutheastAsia]),
        new("t-pop", "T-Pop", PersonalGenreTaxonKind.Style, ParentIds: ["pop"], Regions: [PersonalGenreRegions.SoutheastAsia]),
        // OPM is a national popular-music industry, not one musical form.
        new("opm", "OPM", PersonalGenreTaxonKind.Context, ContextOnly: true, Regions: [PersonalGenreRegions.SoutheastAsia]),

        // =================================================================
        // Oceania / Pacific. Deliberately conservative: Maori, Aboriginal,
        // Polynesian, Melanesian and Micronesian are cultural and geographic
        // identities rather than automatically musical genres, so none of them
        // is created here. Context or Scene would be the right home if one is
        // ever needed, and that should be a deliberate decision.
        // =================================================================
        new("pacific-reggae", "Pacific Reggae", PersonalGenreTaxonKind.Style, ParentIds: ["reggae"], Regions: [PersonalGenreRegions.OceaniaPacific]),
        new("jawaiian", "Jawaiian", PersonalGenreTaxonKind.Style, ParentIds: ["pacific-reggae"], Regions: [PersonalGenreRegions.OceaniaPacific]),
        new("hawaiian-pop", "Hawaiian Pop", PersonalGenreTaxonKind.Style, ParentIds: ["pop"], Regions: [PersonalGenreRegions.OceaniaPacific]),
        new("aboriginal-rock", "Aboriginal Rock", PersonalGenreTaxonKind.Style, ParentIds: ["rock"], Regions: [PersonalGenreRegions.OceaniaPacific]),
        new("australian-country", "Australian Country", PersonalGenreTaxonKind.Style, ParentIds: ["country"], Regions: [PersonalGenreRegions.OceaniaPacific]),

        // =================================================================
        // Universal Styles. Unscoped, like the universal Genres above.
        // =================================================================
        new("alt-rnb", "Alternative R&B", PersonalGenreTaxonKind.Style, ParentIds: ["rnb"], Aliases: ["Alt R&B", "Alternative RnB"]),
        new("trap", "Trap", PersonalGenreTaxonKind.Style, ParentIds: ["hip-hop"]),
        new("boom-bap", "Boom Bap", PersonalGenreTaxonKind.Style, ParentIds: ["hip-hop"]),
        new("deep-house", "Deep House", PersonalGenreTaxonKind.Style, ParentIds: ["house"]),
        new("praise", "Praise", PersonalGenreTaxonKind.Style, ParentIds: ["gospel"]),
        new("worship", "Worship", PersonalGenreTaxonKind.Style, ParentIds: ["gospel"]),

        // =================================================================
        // Africa Styles, Substyle, Context, Scene and Language.
        // =================================================================
        new("afropop", "Afropop", PersonalGenreTaxonKind.Style, ParentIds: ["afrobeats", "pop"], Aliases: ["Afro-Pop", "Afro Pop"], Regions: [PersonalGenreRegions.Africa]),
        new("afro-fusion", "Afro-Fusion", PersonalGenreTaxonKind.Style, ParentIds: ["afrobeats"], Regions: [PersonalGenreRegions.Africa]),
        new("afro-soul", "Afro-Soul", PersonalGenreTaxonKind.Style, ParentIds: ["soul", "afrobeats"], Regions: [PersonalGenreRegions.Africa]),
        new("afro-house", "Afro House", PersonalGenreTaxonKind.Style, ParentIds: ["house", "afrobeats"], Regions: [PersonalGenreRegions.Africa]),
        new("bongo-flava-pop", "Bongo Flava Pop", PersonalGenreTaxonKind.Style, ParentIds: ["bongo-flava", "pop"], Regions: [PersonalGenreRegions.Africa]),
        new("bongo-flava-rnb", "Bongo Flava R&B", PersonalGenreTaxonKind.Style, ParentIds: ["bongo-flava", "rnb"], Aliases: ["Bongo Flava RnB"], Regions: [PersonalGenreRegions.Africa]),
        new("bongo-flava-rap", "Bongo Flava Rap", PersonalGenreTaxonKind.Style, ParentIds: ["bongo-flava", "hip-hop"], Aliases: ["Bongo Rap"], Regions: [PersonalGenreRegions.Africa]),
        new("gengetone", "Gengetone", PersonalGenreTaxonKind.Style, ParentIds: ["genge", "hip-hop"], Regions: [PersonalGenreRegions.Africa]),
        new("kenyan-drill", "Kenyan Drill", PersonalGenreTaxonKind.Style, ParentIds: ["hip-hop"], Aliases: ["Kenya Drill"], Regions: [PersonalGenreRegions.Africa]),
        new("ndombolo", "Ndombolo", PersonalGenreTaxonKind.Style, ParentIds: ["soukous", "congolese-rumba"], Regions: [PersonalGenreRegions.Africa]),
        new("east-african-gospel", "East African Gospel", PersonalGenreTaxonKind.Style, ParentIds: ["gospel"], Regions: [PersonalGenreRegions.Africa]),
        new("swahili-gospel", "Swahili Gospel", PersonalGenreTaxonKind.Style, ParentIds: ["gospel"], Regions: [PersonalGenreRegions.Africa]),

        new("swahili-pop", "Swahili Pop", PersonalGenreTaxonKind.Substyle, ParentIds: ["bongo-flava", "afropop"], Regions: [PersonalGenreRegions.Africa]),

        new("afrosounds", "Afrosounds", PersonalGenreTaxonKind.Context, ContextOnly: true, Regions: [PersonalGenreRegions.Africa]),
        new("east-africa", "East Africa", PersonalGenreTaxonKind.Context, ContextOnly: true, Regions: [PersonalGenreRegions.Africa]),
        new("swahili", "Swahili", PersonalGenreTaxonKind.Language, ContextOnly: true, Regions: [PersonalGenreRegions.Africa]),
        new("tanzania", "Tanzania", PersonalGenreTaxonKind.Context, ContextOnly: true, Regions: [PersonalGenreRegions.Africa]),
        new("kenya", "Kenya", PersonalGenreTaxonKind.Context, ContextOnly: true, Regions: [PersonalGenreRegions.Africa]),
        new("uganda", "Uganda", PersonalGenreTaxonKind.Context, ContextOnly: true, Regions: [PersonalGenreRegions.Africa]),
        new("south-africa", "South Africa", PersonalGenreTaxonKind.Context, ContextOnly: true, Regions: [PersonalGenreRegions.Africa]),
        new("nigeria", "Nigeria", PersonalGenreTaxonKind.Context, ContextOnly: true, Regions: [PersonalGenreRegions.Africa]),
        new("ghana", "Ghana", PersonalGenreTaxonKind.Context, ContextOnly: true, Regions: [PersonalGenreRegions.Africa]),
        new("congo", "Congo", PersonalGenreTaxonKind.Context, ContextOnly: true, Regions: [PersonalGenreRegions.Africa]),
        new("drc", "DR Congo", PersonalGenreTaxonKind.Context, ContextOnly: true, Aliases: ["Democratic Republic of the Congo", "DRC"], Regions: [PersonalGenreRegions.Africa]),
        new("zilizopendwa", "Zilizopendwa", PersonalGenreTaxonKind.Scene, ContextOnly: true, Aliases: ["Swahili Oldies"], Regions: [PersonalGenreRegions.Africa])
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
