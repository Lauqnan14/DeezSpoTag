namespace DeezSpoTag.Services.Genre;

public sealed class PersonalGenreCatalog
{
    private readonly IReadOnlyList<PersonalGenreTaxon> _taxa;
    private readonly IReadOnlyDictionary<string, PersonalGenreTaxon> _byId;
    private readonly IReadOnlyDictionary<string, PersonalGenreTaxon> _byLookup;

    public PersonalGenreCatalog(IReadOnlyList<PersonalGenreTaxon>? customTaxa = null)
    {
        var taxa = new List<PersonalGenreTaxon>();
        taxa.AddRange(PersonalGenreTaxonomy.GetDefaultTaxa());

        if (customTaxa is not null)
        {
            var builtInIds = taxa.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var taxon in customTaxa)
            {
                if (string.IsNullOrWhiteSpace(taxon.Id)
                    || string.IsNullOrWhiteSpace(taxon.Name)
                    || builtInIds.Contains(taxon.Id))
                {
                    continue;
                }

                var existingIndex = taxa.FindIndex(item =>
                    string.Equals(item.Id, taxon.Id, StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                {
                    taxa[existingIndex] = taxon;
                }
                else
                {
                    taxa.Add(taxon);
                }
            }
        }

        var normalizedTaxa = taxa
            .Select(item => item.ContextOnly && item.Kind != PersonalGenreTaxonKind.Context
                ? item with { Kind = PersonalGenreTaxonKind.Context }
                : item)
            .ToList();

        _taxa = normalizedTaxa;
        _byId = normalizedTaxa.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        _byLookup = BuildLookup(normalizedTaxa);
    }

    public IReadOnlyList<PersonalGenreTaxon> Taxa => _taxa;

    public bool TryGetById(string? id, out PersonalGenreTaxon taxon)
        => _byId.TryGetValue(id?.Trim() ?? string.Empty, out taxon!);

    public bool TryMatch(string? value, out PersonalGenreTaxon taxon)
        => _byLookup.TryGetValue(PersonalGenreTaxonomy.Normalize(value), out taxon!);

    private static IReadOnlyDictionary<string, PersonalGenreTaxon> BuildLookup(
        IReadOnlyList<PersonalGenreTaxon> taxa)
    {
        var result = new Dictionary<string, PersonalGenreTaxon>(StringComparer.Ordinal);
        foreach (var rawTaxon in taxa)
        {
            var taxon = rawTaxon.ContextOnly && rawTaxon.Kind != PersonalGenreTaxonKind.Context
                ? rawTaxon with { Kind = PersonalGenreTaxonKind.Context }
                : rawTaxon;
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
            var normalized = PersonalGenreTaxonomy.Normalize(value);
            if (normalized.Length > 0 && !result.ContainsKey(normalized))
            {
                result[normalized] = taxon;
            }
        }
    }
}
