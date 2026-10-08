using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;

namespace DeezSpoTag.Services.Genre;

/// <summary>
/// The genre-normalization preferences, prepared once so consumers on hot paths
/// do not rebuild the lookup tables per file.
///
/// The values are owned by Genre Intelligence. This type only holds a prepared
/// view of them, which is what lets a download or a tag write apply the user's
/// spelling preferences without knowing where the preferences are stored or
/// touching the database on every track.
/// </summary>
public sealed class GenreNormalizationSnapshot
{
    /// <summary>The preferences a user gets before configuring anything.</summary>
    public static GenreNormalizationSnapshot Default { get; } = Create(
        false,
        PersonalGenreSettings.DefaultAliasRules,
        null);

    private GenreNormalizationSnapshot(
        bool enabled,
        IReadOnlyDictionary<string, string> aliasMap,
        IReadOnlyList<string> blockList)
    {
        Enabled = enabled;
        AliasMap = aliasMap;
        BlockList = blockList;
    }

    /// <summary>
    /// Whether normalization is switched on. The block list still applies when it
    /// is off, matching the behaviour the settings had before the move: a blocked
    /// value was never written regardless of the toggle.
    /// </summary>
    public bool Enabled { get; }

    /// <summary>Alias lookup, keyed the way the normalizer keys it.</summary>
    public IReadOnlyDictionary<string, string> AliasMap { get; }

    public IReadOnlyList<string> BlockList { get; }

    public static GenreNormalizationSnapshot Create(
        bool enabled,
        IReadOnlyList<PersonalGenreAliasRule>? rules,
        IReadOnlyList<string>? blockList)
    {
        var legacyRules = MergeDefaultAliasRules(rules, PersonalGenreSettings.DefaultAliasRules)
            .Select(rule => new GenreTagAliasRule { Alias = rule.Alias, Canonical = rule.Canonical })
            .ToList();
        return new GenreNormalizationSnapshot(
            enabled,
            enabled ? GenreTagAliasNormalizer.BuildAliasMap(legacyRules) : new Dictionary<string, string>(StringComparer.Ordinal),
            blockList ?? GenreTagAliasNormalizer.DefaultBlockedGenres);
    }

    public static GenreNormalizationSnapshot Create(PersonalGenreSettings settings) => Create(
        settings.NormalizeGenreTags,
        settings.GenreTagAliasRules,
        settings.GenreTagBlockList);

    /// <summary>
    /// Folds the shipped default rules into a user's list.
    ///
    /// This reproduces what the settings normalizer did on every save, and it is
    /// kept because it is user-visible behaviour: a user who never edited the list
    /// relies on the defaults, and a rule they added is never displaced by a
    /// default of the same name.
    ///
    /// Note the asymmetry with the block list, which is deliberate. The defaults
    /// are re-added here because the alias list has always been treated as a set
    /// of conventions to guarantee, whereas the block list has always been purely
    /// the user's own, so an emptied block list stays empty.
    /// </summary>
    public static IReadOnlyList<PersonalGenreAliasRule> MergeDefaultAliasRules(
        IReadOnlyList<PersonalGenreAliasRule>? configured,
        IReadOnlyList<PersonalGenreAliasRule> defaults)
    {
        var asLegacy = (IEnumerable<PersonalGenreAliasRule>? source) =>
            (source ?? [])
            .Select(rule => new GenreTagAliasRule { Alias = rule.Alias, Canonical = rule.Canonical });

        var normalized = GenreTagAliasNormalizer.NormalizeRules(asLegacy(configured).ToList());
        var keys = new HashSet<string>(
            normalized.Select(rule => GenreTagAliasNormalizer.ToLookupKey(rule.Alias)),
            StringComparer.Ordinal);

        foreach (var rule in GenreTagAliasNormalizer.NormalizeRules(asLegacy(defaults).ToList()))
        {
            var key = GenreTagAliasNormalizer.ToLookupKey(rule.Alias);
            if (!string.IsNullOrWhiteSpace(key) && keys.Add(key))
            {
                normalized.Add(new GenreTagAliasRule { Alias = rule.Alias, Canonical = rule.Canonical });
            }
        }

        return normalized
            .Select(rule => new PersonalGenreAliasRule(rule.Alias, rule.Canonical))
            .ToArray();
    }
}
