using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Genre;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services.AutoTag;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Orchestrates Genre Intelligence around the audio file.
///
/// The file is the only semantic input. A run reads the file's own tags, asks
/// the resolver what they mean under the user's taxonomy, records both the
/// observed and the resolved state, and writes the result back. There is no
/// provider evidence, no Vibe evidence and no confidence fusion anywhere in this
/// path.
/// </summary>
public sealed class PersonalGenreService
{
    /// <summary>
    /// Bumped when the on-disk configuration shape changed.
    ///
    /// Version 1 files carried provider <c>source</c> filters; they are still
    /// imported, with any source filter dropped, because a provider name is not a
    /// file field.
    ///
    /// Version 3 adds the optional per-taxon <c>Regions</c> list. It is purely
    /// additive: a version 1 or 2 payload simply has no Regions, which is read as
    /// unscoped, so both remain importable unchanged.
    /// </summary>
    private const int ConfigurationSchemaVersion = 3;

    private const int SupportedConfigurationSchemaVersion = 1;

    private readonly PersonalGenreStore _store;
    private readonly LibraryRepository _repository;
    private readonly GenreNormalizationProvider? _normalizationProvider;
    private readonly ILogger<PersonalGenreService> _logger;
    private readonly ArtistLocation.ArtistLocationResolver? _artistLocations;
    private readonly DeezSpoTag.Services.Library.ArtistLocationOverrideStore? _artistLocationOverrides;

    public PersonalGenreService(
        PersonalGenreStore store,
        LibraryRepository repository,
        ILogger<PersonalGenreService> logger,
        GenreNormalizationProvider? normalizationProvider = null,
        ArtistLocation.ArtistLocationResolver? artistLocations = null,
        DeezSpoTag.Services.Library.ArtistLocationOverrideStore? artistLocationOverrides = null)
    {
        _store = store;
        _repository = repository;
        _normalizationProvider = normalizationProvider;
        _logger = logger;
        _artistLocations = artistLocations;
        _artistLocationOverrides = artistLocationOverrides;
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

    public async Task<PersonalGenreSettings> SaveSettingsAsync(
        PersonalGenreSettings settings,
        CancellationToken cancellationToken = default)
    {
        var saved = await _store.SaveSettingsAsync(settings, cancellationToken);
        // The tag-writing paths read the preferences through the provider, so a
        // save has to refresh it. Otherwise a change would not reach a download or
        // a QuickTag write until the process restarted.
        if (_normalizationProvider is not null)
        {
            await _normalizationProvider.RefreshAsync(cancellationToken);
        }

        return saved;
    }

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

    public async Task<PersonalGenreTrackResult?> GetTrackResultAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        var current = await _store.GetTrackResultAsync(trackId, cancellationToken);
        var path = await _repository.GetTrackPrimaryFilePathAsync(trackId, cancellationToken);
        var autoTag = path is null ? null : await _store.GetLatestAutoTagResultAsync(trackId, path, cancellationToken);
        return autoTag != null && (current is null || autoTag.ResolvedAtUtc > current.ResolvedAtUtc) ? autoTag : current;
    }

    public Task<IReadOnlyList<PersonalGenreLock>> GetLocksAsync(
        long trackId,
        CancellationToken cancellationToken = default)
        => _store.GetLocksAsync(trackId, cancellationToken);

    public Task<IReadOnlyList<PersonalGenreScopedLock>> GetScopedLocksAsync(
        string scopeType,
        long scopeId,
        CancellationToken cancellationToken = default)
        => _store.GetScopedLocksAsync(scopeType, scopeId, cancellationToken);

    public Task<PersonalGenreScopedLock> SaveScopedLockAsync(
        PersonalGenreScopedLock item,
        CancellationToken cancellationToken = default)
        => _store.SaveScopedLockAsync(item, cancellationToken);

    public Task DeleteScopedLockAsync(
        string scopeType,
        long scopeId,
        string taxonId,
        CancellationToken cancellationToken = default)
        => _store.DeleteScopedLockAsync(scopeType, scopeId, taxonId, cancellationToken);

    public Task<IReadOnlyList<PersonalGenreResolutionHistoryItem>> GetTrackHistoryAsync(
        long trackId,
        int limit = 20,
        CancellationToken cancellationToken = default)
        => _store.GetTrackHistoryAsync(trackId, limit, cancellationToken);

    public Task<PersonalGenreTrackScope?> GetTrackScopeAsync(
        long trackId,
        CancellationToken cancellationToken = default)
        => _store.GetTrackScopeAsync(trackId, cancellationToken);

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
        if (configuration.SchemaVersion is < SupportedConfigurationSchemaVersion or > ConfigurationSchemaVersion)
        {
            throw new ArgumentException(
                $"Unsupported Personal Genre configuration schema version {configuration.SchemaVersion}.",
                nameof(configuration));
        }

        await _store.SaveSettingsAsync(configuration.Settings, cancellationToken);

        var customTaxaImported = 0;
        var pendingTaxa = (configuration.CustomTaxa ?? Array.Empty<PersonalGenreTaxon>())
            .GroupBy(item => item.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        var existingCustomTaxa = await _store.GetCustomTaxaAsync(cancellationToken);
        var availableTaxonIds = PersonalGenreTaxonomy.GetDefaultTaxa()
            .Select(item => item.Id)
            .Concat(existingCustomTaxa.Select(item => item.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Parents may be declared after their children in a hand-edited file, so
        // taxa are imported in dependency order and a cycle is reported by name
        // rather than silently dropping a branch.
        while (pendingTaxa.Count > 0)
        {
            var importedThisPass = 0;
            foreach (var taxon in pendingTaxa.ToArray())
            {
                var parents = taxon.ParentIds ?? Array.Empty<string>();
                if (parents.Any(parentId => !availableTaxonIds.Contains(parentId)))
                {
                    continue;
                }

                var saved = await _store.UpsertCustomTaxonAsync(taxon, cancellationToken);
                availableTaxonIds.Add(saved.Id);
                pendingTaxa.Remove(taxon);
                customTaxaImported++;
                importedThisPass++;
            }

            if (importedThisPass > 0)
            {
                continue;
            }

            var unresolved = pendingTaxa
                .SelectMany(item => item.ParentIds ?? Array.Empty<string>())
                .Where(parentId => !availableTaxonIds.Contains(parentId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase);
            throw new ArgumentException(
                $"Custom taxonomy contains unresolved or cyclic parent references: {string.Join(", ", unresolved)}.",
                nameof(configuration));
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

    public async Task<PersonalGenreRebuildResult> RebuildBatchAsync(
        long afterTrackId,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(batchSize, 1, 500);
        var ids = await _store.GetResolvableTrackIdsAfterAsync(
            Math.Max(0, afterTrackId),
            pageSize + 1,
            cancellationToken);

        var hasMore = ids.Count > pageSize;
        var page = ids.Take(pageSize).ToArray();
        var resolved = 0;
        var skipped = 0;
        long lastTrackId = afterTrackId;

        foreach (var trackId in page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastTrackId = trackId;
            var result = await ResolveTrackAsync(trackId, cancellationToken);
            if (result is null)
            {
                skipped++;
            }
            else
            {
                resolved++;
            }
        }

        return new PersonalGenreRebuildResult(
            page.Length,
            resolved,
            skipped,
            Math.Max(0, lastTrackId),
            hasMore);
    }

    /// <summary>
    /// Re-resolves a track by reading its audio file.
    ///
    /// Nothing here consults analysis results. A track resolves from whatever its
    /// tags currently say, so a rebuilt classification always describes the file
    /// rather than a provider's opinion recorded weeks earlier.
    /// </summary>
    public async Task<PersonalGenreTrackResult?> ResolveTrackAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        var settings = await _store.GetSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            return null;
        }

        var path = await _repository.GetTrackPrimaryFilePathAsync(trackId, cancellationToken);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        var postSnapshot = ReadFileSnapshot(path);
        var resolution = await ResolveAndStoreAsync(
            trackId, postSnapshot, preSnapshot: null, GenreIntelligenceLocationPolicy.ProviderChain,
            cancellationToken);
        return resolution.Result;
    }

    /// <summary>
    /// Resolves one AutoTag file from its observed state.
    ///
    /// The post-AutoTag snapshot is the input. The pre-AutoTag snapshot is
    /// reconciled against it so a personal value the platforms did not replace
    /// is carried into the resolution instead of being lost when the file is
    /// rewritten.
    /// </summary>
    public async Task<GenreIntelligenceFileResolution> ResolveFileAsync(
        long? trackId,
        string filePath,
        GenreSemanticSnapshot postSnapshot,
        GenreSemanticSnapshot? preSnapshot,
        AutoTagGenreIntelligenceSettings options,
        CancellationToken cancellationToken)
    {
        // An AutoTag run is the only caller allowed to resolve location through the
        // provider chain: it is already spending network budget on this file.
        var settings = new PersonalGenreSettings(
            true,
            Math.Clamp(options.MaxGenres, 1, 10),
            options.PreserveUnmappedTags,
            options.IncludeParentGenres);

        return await ResolveAndStoreAsync(
            trackId,
            postSnapshot,
            preSnapshot,
            GenreIntelligenceLocationPolicy.ProviderChain,
            cancellationToken, filePath, runSettings: settings);
    }

    /// <summary>
    /// Reads a file's semantic tags using the same reader AutoTag writes with.
    /// </summary>
    /// <param name="stylesTagName">
    /// The physical tag the user's configuration uses for Style. AutoTag lets
    /// this be a custom name per container, so the reader has to be told which
    /// one rather than assuming.
    /// </param>
    public static GenreSemanticSnapshot ReadFileSnapshot(string path, string stylesTagName = "STYLE")
    {
        var extension = Path.GetExtension(path);
        using var file = TagLib.File.Create(path);
        return GenreSemanticTagIo.ReadSnapshot(file, extension, stylesTagName);
    }

    /// <summary>
    /// Obtains the artist-location evidence a caller is allowed to use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="GenreIntelligenceLocationPolicy.ProviderChain"/> is the existing
    /// behaviour: manual override, then MusicBrainz, then Audiomack. Only an
    /// AutoTag run uses it, because that run already spends network budget on
    /// metadata for the same file.
    /// </para>
    /// <para>
    /// <see cref="GenreIntelligenceLocationPolicy.StoredOnly"/> reads just the
    /// manual override table. A preview endpoint uses it, so rendering a preview can
    /// never start an outbound request. When nothing is stored the result is empty,
    /// which costs one sentence of annotation and changes no classification: the
    /// resolver reads location only to append provenance text to a reason string.
    /// </para>
    /// </remarks>
    private async Task<GenreArtistLocationProvenance?> ResolveArtistLocationAsync(
        long? trackId,
        GenreIntelligenceLocationPolicy policy,
        CancellationToken cancellationToken,
        string? filePath = null)
    {
        if (!_repository.IsConfigured || (_artistLocations is null && _artistLocationOverrides is null))
            return null;

        if (!trackId.HasValue && !string.IsNullOrWhiteSpace(filePath))
            trackId = await _repository.GetTrackIdForFilePathAsync(filePath, cancellationToken);
        long? artistId = null;
        if (trackId.HasValue)
            artistId = await _repository.GetArtistIdForTrackAsync(trackId.Value, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            using var file = TagLib.File.Create(filePath);
            var mainArtist = file.Tag.FirstPerformer;
            if (!string.IsNullOrWhiteSpace(mainArtist))
                artistId = await _repository.FindArtistIdByNameAsync(mainArtist, cancellationToken);
        }
        if (!artistId.HasValue) return null;

        if (policy == GenreIntelligenceLocationPolicy.StoredOnly)
            return await ReadStoredArtistLocationAsync(artistId.Value, cancellationToken);

        if (_artistLocations is null) return null;
        var artistName = trackId.HasValue
            ? await _repository.GetArtistNameForTrackAsync(trackId.Value, cancellationToken)
            : (await _repository.GetArtistAsync(artistId.Value, cancellationToken))?.Name;
        if (string.IsNullOrWhiteSpace(artistName)) return null;

        var location = await _artistLocations.ResolveAsync(artistId.Value, artistName, cancellationToken);
        return location is null ? null : new GenreArtistLocationProvenance(
            location.Country, location.City, location.Region, location.Source,
            location.SourceReference, location.KnownAt, location.ResolutionMethod);
    }

    /// <summary>
    /// Reads the manual override row directly, with no provider lookup.
    /// </summary>
    private async Task<GenreArtistLocationProvenance?> ReadStoredArtistLocationAsync(
        long artistId,
        CancellationToken cancellationToken)
    {
        if (_artistLocationOverrides is null) return null;
        var stored = await _artistLocationOverrides.GetAsync(artistId, cancellationToken);
        if (stored is null) return null;
        return new GenreArtistLocationProvenance(
            stored.Country,
            stored.City,
            null,
            ArtistLocation.ArtistLocationResolver.ManualSourceName,
            $"artist-location-override:{artistId}",
            stored.UpdatedAt,
            "manual-override");
    }

    /// <summary>
    /// The single place resolver inputs are assembled.
    /// </summary>
    /// <remarks>
    /// Every caller goes through here, which is what stops the AutoTag terminal
    /// stage, the cleanup preview and the Style Construction preview from each
    /// constructing a slightly different set of arguments. It reads state only: no
    /// file is opened, nothing is written and nothing is persisted, so a preview
    /// endpoint can call it and stay read-only.
    /// </remarks>
    private async Task<GenreIntelligenceResolutionContext> BuildResolutionContextAsync(
        PersonalGenreSettings settings,
        GenreSemanticSnapshot postSnapshot,
        GenreSemanticSnapshot? preSnapshot,
        GenreIntelligenceLocationPolicy locationPolicy,
        long? trackId,
        CancellationToken cancellationToken,
        string? filePath = null)
    {
        // The shared Genre Normalization preferences are preprocessing, so they run
        // before anything is classified and identically on every path. The snapshot
        // comes from the one provider that also serves downloads, QuickTag and the
        // AutoTag provider stage, so a value the user's block list forbids is
        // forbidden here too rather than being handled by a second rule set.
        var normalization = await ResolveNormalizationAsync(cancellationToken);
        var normalizedPost = GenreNormalizationPreprocessor.Apply(postSnapshot, normalization);
        var normalizedPre = preSnapshot is null
            ? null
            : GenreNormalizationPreprocessor.Apply(preSnapshot, normalization);

        var location = await ResolveArtistLocationAsync(trackId, locationPolicy, cancellationToken, filePath);

        return new GenreIntelligenceResolutionContext(
            settings,
            normalization,
            postSnapshot,
            normalizedPost,
            normalizedPre,
            CollectRemovals(normalizedPost, normalizedPre),
            await _store.GetMappingsAsync(cancellationToken),
            await _store.GetRulesAsync(cancellationToken),
            trackId.HasValue
                ? await _store.GetEffectiveLocksAsync(trackId.Value, cancellationToken)
                : [],
            await _store.GetCustomTaxaAsync(cancellationToken),
            location?.ToGenreContext() ?? []);
    }

    private async Task<GenreIntelligenceFileResolution> ResolveAndStoreAsync(
        long? trackId,
        GenreSemanticSnapshot postSnapshot,
        GenreSemanticSnapshot? preSnapshot,
        GenreIntelligenceLocationPolicy locationPolicy,
        CancellationToken cancellationToken,
        string? filePath = null,
        PersonalGenreSettings? runSettings = null)
    {
        var settings = runSettings ?? await _store.GetSettingsAsync(cancellationToken);
        var context = await BuildResolutionContextAsync(
            settings, postSnapshot, preSnapshot, locationPolicy, trackId, cancellationToken, filePath);
        var resolution = context.Resolve();

        var result = new PersonalGenreTrackResult(
            trackId ?? 0,
            resolution,
            DateTimeOffset.UtcNow,
            preSnapshot,
            postSnapshot);

        if (trackId.HasValue)
        {
            await _store.SaveTrackResultAsync(result, cancellationToken);
        }

        return new GenreIntelligenceFileResolution(trackId, result, context);
    }

    /// <summary>
    /// Produces the inspectable cleanup result for one file, without writing it.
    /// </summary>
    /// <remarks>
    /// This reads the file and reports what would change. It never writes, so it is
    /// safe to call as a preview, and it reports the same plan the terminal stage
    /// would consume rather than a second opinion about it.
    /// </remarks>
    public async Task<GenreCleanupPreview?> GetCleanupPreviewAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        var path = await _repository.GetTrackPrimaryFilePathAsync(trackId, cancellationToken);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        var settings = await _store.GetSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            return null;
        }

        // The same context the AutoTag stage builds, so the answer here is the answer
        // a run would write. Two inputs are deliberately absent, and both are stated
        // rather than forgotten:
        //   - the pre-AutoTag snapshot, which is keyed by AutoTag job id and file path
        //     and does not exist for a track page, so carry-forward cannot apply;
        //   - a provider lookup, because StoredOnly keeps a preview offline.
        // Neither can change a classification: carry-forward only re-admits values
        // this resolver cannot interpret, and location only annotates a reason.
        var context = await BuildResolutionContextAsync(
            settings,
            ReadFileSnapshot(path),
            preSnapshot: null,
            GenreIntelligenceLocationPolicy.StoredOnly,
            trackId,
            cancellationToken);

        return GenreCleanupPreviewBuilder.Build(
            context.Resolve(), context.PostSnapshot.Observations, context.RemovedByNormalization);
    }

    /// <summary>Evaluates construction research against current cleaned file evidence without saving or writing.</summary>
    public async Task<StyleConstructionPreview?> GetStyleConstructionPreviewAsync(
        long trackId, CancellationToken cancellationToken = default)
    {
        var path = await _repository.GetTrackPrimaryFilePathAsync(trackId, cancellationToken);
        if (path is null || !File.Exists(path)) return null;
        var settings = await _store.GetSettingsAsync(cancellationToken);
        if (!settings.Enabled) return null;

        // Shares the cleanup stage with AutoTag and the cleanup preview, so the
        // evidence below describes the same cleanup those two would produce. The same
        // two documented absences apply: no AutoTag job context to carry forward from,
        // and no provider lookup.
        var context = await BuildResolutionContextAsync(
            settings,
            ReadFileSnapshot(path),
            preSnapshot: null,
            GenreIntelligenceLocationPolicy.StoredOnly,
            trackId,
            cancellationToken);

        var catalog = new PersonalGenreCatalog(context.CustomTaxa);
        var cleanup = context.Resolve();
        var snapshot = StyleConstructionEvidenceBuilder.Build(
            trackId, context.PostSnapshot, cleanup, catalog, context.Locks, context.Mappings, context.Rules,
            await BuildArtistIdentityAsync(trackId, cancellationToken),
            await BuildArtistLocationProvenanceAsync(trackId, cancellationToken));
        return StyleConstructionEvaluator.Evaluate(snapshot, StyleConstructionRuleCatalog.Default, catalog);
    }

    /// <summary>
    /// The track's artist identity, for construction evidence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Production artist-scoped Style Construction evidence is currently
    /// entirely ineligible.</b> DeezSpoTag has no structured track-level
    /// artist-credit source carrying Main/Featured roles. The library binds a track
    /// to exactly one artist and does so through the album:
    /// <c>track → album → album.artist_id</c>. That identifies an <i>album</i>
    /// artist, which is not the track's main artist — a compilation credits every
    /// track to one album artist while each track has its own performer. Inferring
    /// <c>Main</c> from it would state something the data does not say, so
    /// <see cref="StyleConstructionArtistIdentity.Role"/> stays
    /// <see cref="ConstructionArtistRole.Unknown"/>.
    /// </para>
    /// <para>
    /// The consequence is stronger than "role-qualified predicates fail". Because
    /// this method also reports <see cref="ConstructionEvidenceScope.Artist"/>, and
    /// the evaluator requires a proven binding for any artist-scoped fact, every
    /// fact produced from an identity built here is rejected: by the scope gate when
    /// a rule allows only track scope, and by the artist-binding gate when a rule
    /// allows artist scope. Artist evidence therefore fails closed in production
    /// today, and this wiring exists so the identity and its provenance are
    /// inspectable and so a genuine track-credit source can be attached without
    /// changing the builder or the evaluator.
    /// </para>
    /// <para>
    /// The binding reference records how the artist was reached, so a later consumer
    /// can tell this weak album-artist binding from a genuine track-performer one.
    /// </para>
    /// </remarks>
    private async Task<StyleConstructionArtistIdentity?> BuildArtistIdentityAsync(
        long trackId,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured) return null;
        var artistId = await _repository.GetArtistIdForTrackAsync(trackId, cancellationToken);
        if (artistId is not > 0) return null;

        return new StyleConstructionArtistIdentity(
            artistId.Value,
            ConstructionArtistRole.Unknown,
            $"library-album-artist:{artistId.Value}",
            ConstructionEvidenceScope.Artist);
    }

    /// <summary>
    /// Stored artist-location provenance for construction evidence.
    /// </summary>
    /// <remarks>
    /// Reads the manual override row only. No provider lookup, so rendering a
    /// preview stays read-only and offline. The provenance travels with the evidence
    /// so a future consumer can reason about where it came from; nothing today
    /// derives a term from it and no production rule is enabled.
    /// </remarks>
    private async Task<StyleConstructionArtistLocation?> BuildArtistLocationProvenanceAsync(
        long trackId,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured || _artistLocationOverrides is null) return null;
        var artistId = await _repository.GetArtistIdForTrackAsync(trackId, cancellationToken);
        if (artistId is not > 0) return null;

        var stored = await _artistLocationOverrides.GetAsync(artistId.Value, cancellationToken);
        if (stored is null) return null;

        return new StyleConstructionArtistLocation(
            stored.Country, stored.City, null,
            ArtistLocation.ArtistLocationResolver.ManualSourceName,
            $"artist-location-override:{artistId.Value}",
            stored.UpdatedAt,
            "manual-override");
    }

    /// <summary>
    /// Resolves the shared genre-normalization preferences for this call.
    /// </summary>
    /// <remarks>
    /// The cached snapshot is used once it exists, which keeps the per-file AutoTag
    /// hot path off the database. Before that, and whenever the cache has failed to
    /// load, the store is read directly instead of falling back to the shipped
    /// defaults: silently reverting to defaults would let a user's block list stop
    /// applying, which is exactly the defect this preprocessing exists to remove.
    /// </remarks>
    private async Task<GenreNormalizationSnapshot> ResolveNormalizationAsync(CancellationToken cancellationToken)
    {
        if (_normalizationProvider is { IsLoaded: true })
        {
            return _normalizationProvider.Current;
        }

        try
        {
            return GenreNormalizationSnapshot.Create(await _store.GetSettingsAsync(cancellationToken));
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // A database that cannot be read must not stop a classification. The
            // defaults are inert rather than destructive: no alias rewriting, and
            // the standard block list still applied.
            _logger.LogWarning(ex, "Genre normalization preferences could not be read; using defaults.");
            return GenreNormalizationSnapshot.Default;
        }
    }

    /// <summary>
    /// Merges the removals from both snapshots into one reportable list.
    /// </summary>
    /// <remarks>
    /// The same blocked value commonly appears in the file before and after
    /// AutoTag, and reporting it twice would make the decision trail read as if two
    /// separate tags had been dropped. The first occurrence wins, and the post-run
    /// state is checked first because that is the state being written.
    /// </remarks>
    private static IReadOnlyList<RemovedGenreTagValue> CollectRemovals(
        GenreNormalizationResult post,
        GenreNormalizationResult? pre)
    {
        var output = new List<RemovedGenreTagValue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var removal in post.Removed.Concat(pre?.Removed ?? Array.Empty<RemovedGenreTagValue>()))
        {
            if (seen.Add($"{removal.InputField}:{removal.Value}"))
            {
                output.Add(removal);
            }
        }

        return output;
    }

    private static bool SameMappingIdentity(
        PersonalGenreMapping left,
        PersonalGenreMapping right)
        => string.Equals(
               PersonalGenreTaxonomy.Normalize(left.MatchValue),
               PersonalGenreTaxonomy.Normalize(right.MatchValue),
               StringComparison.Ordinal)
           && string.Equals(
               (left.TargetTaxonId ?? string.Empty).Trim(),
               (right.TargetTaxonId ?? string.Empty).Trim(),
               StringComparison.OrdinalIgnoreCase)
           && left.InputField == right.InputField
           && left.Action == right.Action;

    private static bool SameRuleIdentity(
        PersonalGenreRule left,
        PersonalGenreRule right)
        => string.Equals(
               PersonalGenreTaxonomy.Normalize(left.MatchValue),
               PersonalGenreTaxonomy.Normalize(right.MatchValue),
               StringComparison.Ordinal)
           && string.Equals(
               left.TargetTaxonId.Trim(),
               right.TargetTaxonId.Trim(),
               StringComparison.OrdinalIgnoreCase)
           && left.InputField == right.InputField;
}
