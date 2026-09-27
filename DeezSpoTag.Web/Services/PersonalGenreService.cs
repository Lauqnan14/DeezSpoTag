using System.Text.Json;
using DeezSpoTag.Services.Genre;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class PersonalGenreService
{
    private const int ConfigurationSchemaVersion = 1;
    private readonly PersonalGenreStore _store;
    private readonly LibraryRepository _repository;
    private readonly ILogger<PersonalGenreService> _logger;

    public PersonalGenreService(
        PersonalGenreStore store,
        LibraryRepository repository,
        ILogger<PersonalGenreService> logger)
    {
        _store = store;
        _repository = repository;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PersonalGenreTaxon>> GetTaxonomyAsync(
        CancellationToken cancellationToken = default)
    {
        var custom = await _store.GetCustomTaxaAsync(cancellationToken);
        return new PersonalGenreCatalog(custom).Taxa;
    }

    public Task<IReadOnlyList<PersonalGenreTaxon>> GetCustomTaxaAsync(
        CancellationToken cancellationToken = default)
        => _store.GetCustomTaxaAsync(cancellationToken);

    public Task<PersonalGenreTaxon> UpsertCustomTaxonAsync(
        PersonalGenreTaxon taxon,
        CancellationToken cancellationToken = default)
        => _store.UpsertCustomTaxonAsync(taxon, cancellationToken);

    public Task DeleteCustomTaxonAsync(
        string taxonId,
        CancellationToken cancellationToken = default)
        => _store.DeleteCustomTaxonAsync(taxonId, cancellationToken);

    public Task<PersonalGenreSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        => _store.GetSettingsAsync(cancellationToken);

    public Task<PersonalGenreSettings> SaveSettingsAsync(
        PersonalGenreSettings settings,
        CancellationToken cancellationToken = default)
        => _store.SaveSettingsAsync(settings, cancellationToken);

    public Task<IReadOnlyList<PersonalGenreMapping>> GetMappingsAsync(CancellationToken cancellationToken = default)
        => _store.GetMappingsAsync(cancellationToken);

    public Task<PersonalGenreMapping> UpsertMappingAsync(
        PersonalGenreMapping mapping,
        CancellationToken cancellationToken = default)
        => _store.UpsertMappingAsync(mapping, cancellationToken);

    public Task DeleteMappingAsync(long id, CancellationToken cancellationToken = default)
        => _store.DeleteMappingAsync(id, cancellationToken);

    public Task<IReadOnlyList<PersonalGenreRule>> GetRulesAsync(CancellationToken cancellationToken = default)
        => _store.GetRulesAsync(cancellationToken);

    public Task<PersonalGenreRule> UpsertRuleAsync(
        PersonalGenreRule rule,
        CancellationToken cancellationToken = default)
        => _store.UpsertRuleAsync(rule, cancellationToken);

    public Task DeleteRuleAsync(long id, CancellationToken cancellationToken = default)
        => _store.DeleteRuleAsync(id, cancellationToken);

    public Task<PersonalGenreTrackResult?> GetTrackResultAsync(
        long trackId,
        CancellationToken cancellationToken = default)
        => _store.GetTrackResultAsync(trackId, cancellationToken);

    public Task<IReadOnlyList<PersonalGenreLock>> GetLocksAsync(
        long trackId,
        CancellationToken cancellationToken = default)
        => _store.GetLocksAsync(trackId, cancellationToken);

    public async Task<PersonalGenreTrackResult?> SaveLockAndResolveAsync(
        PersonalGenreLock item,
        CancellationToken cancellationToken = default)
    {
        await _store.SaveLockAsync(item, cancellationToken);
        return await ResolveTrackAsync(item.TrackId, cancellationToken);
    }

    public async Task<PersonalGenreTrackResult?> DeleteLockAndResolveAsync(
        long trackId,
        string taxonId,
        CancellationToken cancellationToken = default)
    {
        await _store.DeleteLockAsync(trackId, taxonId, cancellationToken);
        return await ResolveTrackAsync(trackId, cancellationToken);
    }

    public async Task<PersonalGenreConfiguration> ExportConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await _store.GetSettingsAsync(cancellationToken);
        var customTaxa = await _store.GetCustomTaxaAsync(cancellationToken);
        var mappings = await _store.GetMappingsAsync(cancellationToken);
        var rules = await _store.GetRulesAsync(cancellationToken);

        return new PersonalGenreConfiguration(
            ConfigurationSchemaVersion,
            PersonalGenreTaxonomy.Version,
            DateTimeOffset.UtcNow,
            settings,
            customTaxa,
            mappings.Select(item => item with { Id = 0 }).ToArray(),
            rules.Select(item => item with { Id = 0 }).ToArray());
    }

    public async Task<PersonalGenreImportResult> ImportConfigurationAsync(
        PersonalGenreConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (configuration.SchemaVersion != ConfigurationSchemaVersion)
        {
            throw new ArgumentException(
                $"Unsupported Personal Genre configuration schema version {configuration.SchemaVersion}.",
                nameof(configuration));
        }

        await _store.SaveSettingsAsync(configuration.Settings, cancellationToken);

        var customTaxaImported = 0;
        foreach (var taxon in configuration.CustomTaxa ?? Array.Empty<PersonalGenreTaxon>())
        {
            await _store.UpsertCustomTaxonAsync(taxon, cancellationToken);
            customTaxaImported++;
        }

        var existingMappings = (await _store.GetMappingsAsync(cancellationToken)).ToList();
        var existingRules = (await _store.GetRulesAsync(cancellationToken)).ToList();
        var mappingsImported = 0;
        var rulesImported = 0;
        var duplicatesSkipped = 0;

        foreach (var mapping in configuration.Mappings ?? Array.Empty<PersonalGenreMapping>())
        {
            if (existingMappings.Any(existing => SameMappingIdentity(existing, mapping)))
            {
                duplicatesSkipped++;
                continue;
            }

            var saved = await _store.UpsertMappingAsync(mapping with { Id = 0 }, cancellationToken);
            existingMappings.Add(saved);
            mappingsImported++;
        }

        foreach (var rule in configuration.Rules ?? Array.Empty<PersonalGenreRule>())
        {
            if (existingRules.Any(existing => SameRuleIdentity(existing, rule)))
            {
                duplicatesSkipped++;
                continue;
            }

            var saved = await _store.UpsertRuleAsync(rule with { Id = 0 }, cancellationToken);
            existingRules.Add(saved);
            rulesImported++;
        }

        return new PersonalGenreImportResult(
            customTaxaImported,
            mappingsImported,
            rulesImported,
            duplicatesSkipped);
    }

    public async Task<PersonalGenreTrackResult?> ResolveTrackAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        var analysis = await _repository.GetTrackAnalysisAsync(trackId, cancellationToken);
        return analysis is null
            ? null
            : await ResolveAndStoreAsync(analysis, cancellationToken);
    }

    public async Task<PersonalGenreTrackResult?> ResolveAndStoreAsync(
        TrackAnalysisResultDto analysis,
        CancellationToken cancellationToken = default)
    {
        var settings = await _store.GetSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            return null;
        }

        var evidence = ParseEvidence(analysis.SemanticEvidenceJson);
        if (evidence.Count == 0)
        {
            evidence = BuildFallbackEvidence(analysis);
        }

        var mappings = await _store.GetMappingsAsync(cancellationToken);
        var rules = await _store.GetRulesAsync(cancellationToken);
        var locks = await _store.GetLocksAsync(analysis.TrackId, cancellationToken);
        var customTaxa = await _store.GetCustomTaxaAsync(cancellationToken);
        var resolution = PersonalGenreResolver.Resolve(
            evidence,
            mappings,
            rules,
            locks,
            customTaxa,
            settings);
        var result = new PersonalGenreTrackResult(
            analysis.TrackId,
            resolution,
            DateTimeOffset.UtcNow);

        await _store.SaveTrackResultAsync(result, cancellationToken);
        return result;
    }

    private IReadOnlyList<PersonalGenreEvidence> ParseEvidence(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<PersonalGenreEvidence>();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<PersonalGenreEvidence>();
            }

            var output = new List<PersonalGenreEvidence>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var rawValue = GetString(item, "rawValue");
                if (string.IsNullOrWhiteSpace(rawValue))
                {
                    continue;
                }

                var source = GetString(item, "source") ?? "unknown";
                var kind = ParseKind(GetString(item, "kind"));
                var scope = GetString(item, "scope");
                var canonicalValue = GetString(item, "canonicalValue");
                var weight = GetDouble(item, "finalWeight") ?? GetDouble(item, "strength") ?? 1d;
                output.Add(new PersonalGenreEvidence(
                    source,
                    rawValue,
                    kind,
                    Math.Clamp(weight, 0d, 10d),
                    scope,
                    canonicalValue));
            }

            return output;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Personal Genre could not parse Vibe semantic evidence.");
            return Array.Empty<PersonalGenreEvidence>();
        }
    }

    private static IReadOnlyList<PersonalGenreEvidence> BuildFallbackEvidence(TrackAnalysisResultDto analysis)
    {
        var output = new List<PersonalGenreEvidence>();
        Append(output, analysis.ResolvedGenres, PersonalGenreTaxonKind.Genre);
        Append(output, analysis.ResolvedStyles, PersonalGenreTaxonKind.Style);
        return output;

        static void Append(
            ICollection<PersonalGenreEvidence> target,
            IReadOnlyList<string>? values,
            PersonalGenreTaxonKind kind)
        {
            if (values is null)
            {
                return;
            }

            foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                target.Add(new PersonalGenreEvidence("vibe-resolved", value.Trim(), kind, 1d, "track"));
            }
        }
    }

    private static bool SameMappingIdentity(
        PersonalGenreMapping left,
        PersonalGenreMapping right)
        => string.Equals(NormalizeValue(left.MatchValue), NormalizeValue(right.MatchValue), StringComparison.Ordinal)
           && string.Equals(left.TargetTaxonId.Trim(), right.TargetTaxonId.Trim(), StringComparison.OrdinalIgnoreCase)
           && string.Equals(NormalizeSource(left.Source), NormalizeSource(right.Source), StringComparison.Ordinal);

    private static bool SameRuleIdentity(
        PersonalGenreRule left,
        PersonalGenreRule right)
        => string.Equals(NormalizeValue(left.MatchValue), NormalizeValue(right.MatchValue), StringComparison.Ordinal)
           && string.Equals(left.TargetTaxonId.Trim(), right.TargetTaxonId.Trim(), StringComparison.OrdinalIgnoreCase)
           && string.Equals(NormalizeSource(left.Source), NormalizeSource(right.Source), StringComparison.Ordinal);

    private static string NormalizeValue(string value)
        => PersonalGenreTaxonomy.Normalize(value);

    private static string NormalizeSource(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static PersonalGenreTaxonKind? ParseKind(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "genre" => PersonalGenreTaxonKind.Genre,
            "style" => PersonalGenreTaxonKind.Style,
            "substyle" => PersonalGenreTaxonKind.Substyle,
            "context" => PersonalGenreTaxonKind.Context,
            _ => null
        };

    private static string? GetString(JsonElement item, string property)
        => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? GetDouble(JsonElement item, string property)
        => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var parsed)
            ? parsed
            : null;
}
