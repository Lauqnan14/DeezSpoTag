using DeezSpoTag.Core.Diagnostics;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Genre;

/// <summary>
/// Serves the current genre-normalization preferences to code that has no access
/// to the store.
///
/// Genre Intelligence owns the settings. Consumers that need them — the download
/// tagger, QuickTag, the AutoTag provider path — ask here rather than reading the
/// general application settings, which no longer own these values.
///
/// The value is cached and refreshed when Genre Intelligence settings are saved,
/// so a save takes effect immediately and a hot path does not hit the database
/// per track.
/// </summary>
public sealed class GenreNormalizationProvider
{
    // Instance state, not static. The provider is a DI singleton, so the cache is
    // already shared by everything that matters; making it static as well would
    // let one instance's loaded value leak into another, which is invisible in
    // production and shows up as tests passing alone and failing together.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GenreNormalizationSnapshot _current = GenreNormalizationSnapshot.Default;
    private bool _loaded;

    private readonly PersonalGenreStore _store;
    private readonly ILogger<GenreNormalizationProvider> _logger;

    public GenreNormalizationProvider(
        PersonalGenreStore store,
        ILogger<GenreNormalizationProvider> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// The last known preferences, without ever blocking.
    ///
    /// This is read from synchronous code paths that cannot await — a download
    /// tagging a file, a track being prepared — so it must never wait on the
    /// database. Until the first load it reports the shipped defaults, which is
    /// the same thing a fresh installation has, and priming happens during startup
    /// before any of those paths can run.
    /// </summary>
    public GenreNormalizationSnapshot Current => Volatile.Read(ref _current);

    /// <summary>Whether the cached value has been loaded from the store yet.</summary>
    public bool IsLoaded => Volatile.Read(ref _loaded);

    /// <summary>Reloads from the store. Called after Genre Intelligence settings are saved.</summary>
    public async Task<GenreNormalizationSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = await _store.GetSettingsAsync(cancellationToken);
            Volatile.Write(ref _current, GenreNormalizationSnapshot.Create(settings));
            Volatile.Write(ref _loaded, true);
            return Volatile.Read(ref _current);
        }
        catch (Exception ex) when (ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // A database that cannot be read must not stop a download or a tag
            // write. The defaults are inert rather than destructive: normalization
            // off and the standard block list.
            _logger.LogWarning(ex, "Genre normalization preferences could not be loaded; using defaults.");
            Volatile.Write(ref _current, GenreNormalizationSnapshot.Default);
            Volatile.Write(ref _loaded, true);
            return GenreNormalizationSnapshot.Default;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Copies the general application settings' genre-normalization values into
    /// Genre Intelligence, once.
    ///
    /// An existing installation may already have the toggle on, custom alias rules
    /// and a custom block list. Those are user preferences and have to survive the
    /// move, so they are imported rather than replaced by the defaults.
    ///
    /// The migration is marked as applied, so a later startup ignores the old
    /// values entirely. That is what stops this from reverting any change the user
    /// makes here afterwards.
    /// </summary>
    public const string LegacySettingsMigrationName = "genre-normalization-from-app-settings-v1";

    /// <summary>
    /// Runs the one-time import and then primes the cache.
    ///
    /// This is awaited during startup, before anything that reads the preferences
    /// can run. Doing it here rather than on first use is what keeps the
    /// synchronous read path free of a database round trip.
    /// </summary>
    public async Task MigrateAndPrimeAsync(
        DeezSpoTagSettings? legacy,
        CancellationToken cancellationToken = default)
    {
        await MigrateLegacySettingsAsync(legacy, cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    public async Task MigrateLegacySettingsAsync(
        DeezSpoTagSettings? legacy,
        CancellationToken cancellationToken = default)
    {
        if (await _store.HasMigrationAsync(LegacySettingsMigrationName, cancellationToken))
        {
            return;
        }

        if (legacy is null)
        {
            // Nothing to import. Still mark it, so a later startup does not pick up
            // a stale configuration file and surprise the user.
            await _store.MarkMigrationAsync(LegacySettingsMigrationName, "no legacy settings available", cancellationToken);
            return;
        }

        var current = await _store.GetSettingsAsync(cancellationToken);
#pragma warning disable CS0618 // The one and only runtime read of the deprecated values.
        var migrated = current with
        {
            NormalizeGenreTags = legacy.NormalizeGenreTags,
            // The user's own rules are kept and the shipped defaults are folded
            // back in, which is what the settings normalizer used to do on save.
            GenreTagAliasRules = GenreNormalizationSnapshot.MergeDefaultAliasRules(
                legacy.GenreTagAliasRules?
                    .Where(rule => !string.IsNullOrWhiteSpace(rule.Alias) && !string.IsNullOrWhiteSpace(rule.Canonical))
                    .Select(rule => new PersonalGenreAliasRule(rule.Alias.Trim(), rule.Canonical.Trim()))
                    .ToArray(),
                PersonalGenreSettings.DefaultAliasRules),
            GenreTagBlockList = GenreTagAliasNormalizer
                .NormalizeBlockedValues(legacy.GenreTagBlockList ?? [])
                .ToArray()
        };
#pragma warning restore CS0618

        await _store.SaveSettingsAsync(migrated, cancellationToken);
        await _store.MarkMigrationAsync(
            LegacySettingsMigrationName,
            $"enabled={migrated.NormalizeGenreTags}; aliases={migrated.GenreTagAliasRules?.Count ?? 0}; blocked={migrated.GenreTagBlockList?.Count ?? 0}",
            cancellationToken);

        _logger.LogInformation(
            "Imported genre normalization preferences into Genre Intelligence: enabled={Enabled}, {AliasCount} alias rules, {BlockCount} blocked values.",
            migrated.NormalizeGenreTags,
            migrated.GenreTagAliasRules?.Count ?? 0,
            migrated.GenreTagBlockList?.Count ?? 0);
    }
}
