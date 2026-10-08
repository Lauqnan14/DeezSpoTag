namespace DeezSpoTag.Services.Genre;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Separate, versioned construction research; never modifies the recognition catalog.</summary>
public sealed class StyleConstructionRuleCatalog
{
    private static readonly Lazy<StyleConstructionRuleCatalog> Embedded = new(Load);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private StyleConstructionRuleCatalog(int schemaVersion, string datasetVersion, IReadOnlyList<StyleConstructionRule> candidates)
    {
        SchemaVersion = schemaVersion;
        DatasetVersion = datasetVersion;
        Candidates = candidates;
    }

    public static StyleConstructionRuleCatalog Default => Embedded.Value;
    public int SchemaVersion { get; }
    public string DatasetVersion { get; }
    public IReadOnlyList<StyleConstructionRule> Candidates { get; }

    public static StyleConstructionRuleCatalog Parse(string json, PersonalGenreCatalog catalog)
    {
        var data = JsonSerializer.Deserialize<Dataset>(json, Options) ?? throw new InvalidDataException("Missing construction dataset.");
        Require(data.SchemaVersion == 1, "Unsupported construction schema version.");
        Require(!string.IsNullOrWhiteSpace(data.DatasetVersion), "Missing construction dataset version.");
        Require(data.Candidates is { Count: > 0 }, "Missing construction candidates.");
        var ruleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recognition = new PersonalGenreCatalog();
        foreach (var rule in data.Candidates)
        {
            Require(rule is not null && !string.IsNullOrWhiteSpace(rule.RuleId) && ruleIds.Add(rule.RuleId), "Missing or duplicate rule ID.");
            Require(!string.IsNullOrWhiteSpace(rule!.Version), "Missing rule version.");
            Require(recognition.TryGetById(rule.TargetStyleId, out var target) && target.Kind == PersonalGenreTaxonKind.Style,
                "Construction target must be a recognized canonical Style.");
            Require(Enum.IsDefined(rule.ResearchDisposition), "Invalid research disposition.");
            Require(rule.RequiredAll is not null && rule.Supporting is not null && rule.ForbiddenAny is not null, "Missing predicate lists.");
            Require(rule.AllowedEvidenceOrigins is { Count: > 0 } && rule.AllowedEvidenceOrigins.All(Enum.IsDefined), "Invalid allowed origins.");
            Require(rule.AllowedScopes is { Count: > 0 } && rule.AllowedScopes.All(Enum.IsDefined), "Invalid allowed scopes.");
            Require(rule.ResearchDisposition != ConstructionResearchDisposition.RecognitionOnly || (!rule.Enabled && !rule.InferenceAllowed),
                "RecognitionOnly rules cannot be enabled or inferable.");
            Require(!rule.Enabled || (rule.InferenceAllowed && rule.ResearchDisposition == ConstructionResearchDisposition.Inferable && rule.RequiredAll!.Count > 0),
                "Enabled rules require researched mandatory predicates.");
            Require(!string.IsNullOrWhiteSpace(rule.ResearchRationale) && rule.Research is not null, "Missing research rationale or dossier.");
            ValidateResearch(rule.Research!);
            Require(rule.ResearchSources is { Count: > 0 }, "Missing research sources.");
            foreach (var source in rule.ResearchSources!)
                Require(source is not null && !string.IsNullOrWhiteSpace(source.Title) && !string.IsNullOrWhiteSpace(source.SourceType)
                    && !string.IsNullOrWhiteSpace(source.Access) && DateOnly.TryParse(source.ObservedAt, out _)
                    && Uri.TryCreate(source.Url, UriKind.Absolute, out var url) && url.Scheme == "https"
                    && Nonempty(source.SupportedClaims), "Incomplete research source.");

            var predicateIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in rule.RequiredAll!.Concat(rule.Supporting!).Concat(rule.ForbiddenAny!))
            {
                Require(group is not null && !string.IsNullOrWhiteSpace(group.PredicateId) && predicateIds.Add(group.PredicateId), "Missing or duplicate predicate ID.");
                Require(group!.AnyOf is { Count: > 0 }, "Predicate group requires alternatives.");
                foreach (var alternative in group.AnyOf)
                {
                    Require(alternative is not null && Enum.IsDefined(alternative.Kind)
                        && catalog.TryGetById(alternative.CanonicalId, out var term) && term.Kind == alternative.Kind,
                        "Predicate must refer to an existing canonical ID and kind.");
                    Require(alternative!.ArtistRole is null or ConstructionArtistRole.Main or ConstructionArtistRole.Featured,
                        "Unknown artist role cannot be a qualifying predicate.");
                }
            }
        }
        return new(data.SchemaVersion, data.DatasetVersion, Array.AsReadOnly(data.Candidates.ToArray()));
    }

    private static StyleConstructionRuleCatalog Load()
    {
        using var stream = typeof(StyleConstructionRuleCatalog).Assembly.GetManifestResourceStream(
            "DeezSpoTag.Services.Genre.Data.style-construction-rules.json")
            ?? throw new InvalidDataException("Missing embedded Style Construction research dataset.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), new());
    }

    private static void ValidateResearch(StyleConstructionResearch research)
    {
        string[] descriptions = [research.Definition, research.MusicalAncestry, research.RequiredParentGenres,
            research.ExistingStyleRelationships, research.SceneRelationship, research.CountryRelationship,
            research.CityRelationship, research.LanguageRelationship, research.EmergencePeriod,
            research.EraRequirement, research.GeographyRequirement, research.SufficientEvidence];
        Require(descriptions.All(s => !string.IsNullOrWhiteSpace(s)) && research.RecognitionAliases is not null
            && Nonempty(research.InsufficientEvidence) && Nonempty(research.FalsePositives)
            && Nonempty(research.DisqualifyingCases) && Nonempty(research.ResearchGaps), "Incomplete construction research dossier.");
    }

    private static bool Nonempty(IReadOnlyList<string>? values) => values is { Count: > 0 } && values.All(s => !string.IsNullOrWhiteSpace(s));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed record Dataset
    {
        public required int SchemaVersion { get; init; }
        public required string DatasetVersion { get; init; }
        public required IReadOnlyList<StyleConstructionRule> Candidates { get; init; }
    }
}
