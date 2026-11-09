using System.Text;

namespace DeezSpoTag.Web.Services.AutoTag;

/// <summary>
/// One canonical credit role recognized across every AutoTag provider.
/// <see cref="Key"/> is the canonical configuration key (camelCase), used in
/// profile tag-selection and overwrite whitelists. <see cref="RawTag"/> is the
/// physical custom tag the shared writer binds this role to (uppercase).
/// </summary>
public sealed record CreditRoleDefinition(
    string Key,
    string DisplayName,
    string RawTag,
    params string[] Aliases);

/// <summary>One role resolved from a source credit label.</summary>
/// <param name="Key">
/// Canonical role key, or an empty string when the label is not a recognized
/// canonical role. Unrecognized labels must be preserved as legacy data, never
/// guessed into a canonical role.
/// </param>
/// <param name="OriginalLabel">
/// The source label this resolution came from, including any bracketed
/// qualifier text, so provenance survives lossless round trips.
/// </param>
public sealed record NormalizedCreditRole(string Key, string OriginalLabel);

/// <summary>
/// The one shared credit-role registry: canonical names, raw-field bindings and
/// recognized aliases. Providers map their explicit roles through this
/// registry; the shared profile canonicalizer, configuration, writer and
/// overwrite guards all resolve the same definitions here. This is not another
/// provider or writer.
/// </summary>
public static class SupportedTagCreditMappings
{
    /// <summary>
    /// Roles historically written through INVOLVEDPEOPLE. Deezer's writer,
    /// author and lyricist contributors were included there, so a legacy
    /// selection must retain their new independent controls too. Versioned
    /// profiles are never expanded; their unchecked roles remain off.
    /// </summary>
    public static readonly IReadOnlyList<string> LegacyCombinedCreditRoles =
    [
        "songwriter",
        "lyricist",
        "producer",
        "executiveProducer",
        "coProducer",
        "additionalProducer",
        "vocalProducer",
        "engineer",
        "recordingEngineer",
        "mixingEngineer",
        "masteringEngineer",
        "assistantEngineer",
        "arranger",
        "orchestrator",
        "conductor",
        "performer",
        "djMixer",
        "director"
    ];

    private static readonly CreditRoleDefinition[] Roles =
    [
        new("songwriter", "Songwriter", "SONGWRITER",
            "songwriter", "songwriters", "written by", "written-by", "writtenby",
            "written", "writer", "writers", "author", "authors"),
        new("composer", "Composer", "COMPOSER",
            "composer", "composers", "composed by", "composed-by", "composedby", "composed", "composition", "music by", "musicby"),
        new("lyricist", "Lyricist", "LYRICIST",
            "lyricist", "lyricists", "lyrics by", "lyrics-by", "lyricsby", "lyrics", "librettist", "text by"),
        new("producer", "Producer", "PRODUCER",
            "producer", "producers", "produced by", "producedby", "production"),
        new("executiveProducer", "Executive Producer", "EXECUTIVEPRODUCER",
            "executive producer", "executive-producer", "executiveproducer"),
        new("coProducer", "Co-Producer", "COPRODUCER",
            "co-producer", "co producer", "coproducer", "co-produced by"),
        new("additionalProducer", "Additional Producer", "ADDITIONALPRODUCER",
            "additional producer", "additional-producer", "additionalproducer"),
        new("vocalProducer", "Vocal Producer", "VOCALPRODUCER",
            "vocal producer", "vocal-producer", "vocalproducer", "vocal production"),
        new("engineer", "Engineer", "ENGINEER",
            "engineer", "engineers", "engineered by", "engineering", "sound engineer", "studio engineer"),
        new("recordingEngineer", "Recording Engineer", "RECORDINGENGINEER",
            "recording engineer", "recording-engineer", "recordingengineer", "recorded by"),
        new("mixingEngineer", "Mixing Engineer", "MIXINGENGINEER",
            "mixer", "mixing engineer", "mixing-engineer", "mixingengineer", "mix engineer", "mixed by", "mixing", "mix"),
        new("masteringEngineer", "Mastering Engineer", "MASTERINGENGINEER",
            "mastering engineer", "mastering-engineer", "masteringengineer", "master engineer", "mastered by", "mastering"),
        new("assistantEngineer", "Assistant Engineer", "ASSISTANTENGINEER",
            "assistant engineer", "assistant-engineer", "assistantengineer", "assist. engineer", "assistant recording engineer", "assistant mastering engineer"),
        new("arranger", "Arranger", "ARRANGER",
            "arranger", "arrangers", "arranged by", "arrangement", "arrangements"),
        new("orchestrator", "Orchestrator", "ORCHESTRATOR",
            "orchestrator", "orchestrators", "orchestrated by", "orchestration"),
        new("conductor", "Conductor", "CONDUCTOR",
            "conductor", "conductors", "conducted by", "conductedby"),
        new("performer", "Performer", "PERFORMER",
            "performer", "performers", "performed by", "vocals", "vocal", "singer", "leading vocals",
            "lead vocals", "background vocals", "instrument", "instruments", "guitar", "bass", "drums",
            "keyboard", "keyboards", "piano", "saxophone", "trumpet", "synthesizer", "programming"),
        new("remixer", "Remixer", "REMIXER",
            "remixer", "remixers", "remix", "remixed by", "remixing", "additional remixing"),
        new("djMixer", "DJ Mixer", "DJMIXER",
            "dj mixer", "dj-mixer", "djmixer", "dj mix", "dj programming"),
        new("publisher", "Publisher", "PUBLISHER",
            "publisher", "publishers", "published by", "publishing", "publishedby"),
        new("director", "Director", "DIRECTOR",
            "director", "directors", "directed by", "directedby", "video director", "music video director")
    ];

    private static readonly IReadOnlyDictionary<string, CreditRoleDefinition> AliasLookup = BuildAliasLookup();

    private static IReadOnlyDictionary<string, CreditRoleDefinition> BuildAliasLookup()
    {
        var lookup = new Dictionary<string, CreditRoleDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in Roles)
        {
            lookup[role.Key] = role;
            lookup[role.DisplayName] = role;
            lookup[role.RawTag] = role;
            foreach (var alias in role.Aliases)
            {
                lookup[alias] = role;
            }
        }

        return lookup;
    }

    /// <summary>All registered credit roles in canonical order.</summary>
    public static IReadOnlyList<CreditRoleDefinition> Definitions => Roles;

    public static bool IsCreditRoleKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return AliasLookup.ContainsKey(NormalizeAliasPhrase(key));
    }

    public static bool TryGetRole(string? key, out CreditRoleDefinition role)
    {
        role = null!;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (AliasLookup.TryGetValue(NormalizeAliasPhrase(key), out var definition))
        {
            role = definition;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves a source credit label into canonical roles. A compound source
    /// credit cannot be represented by a single returned role: "Producer,
    /// Written-By" returns producer and songwriter, "Engineer [Recording
    /// Engineer, Mixing Engineer]" returns recordingEngineer and
    /// mixingEngineer. Every branch keeps the original label so qualifiers and
    /// formatting survive losslessly. Labels that no alias recognizes are
    /// returned with an empty canonical key as legacy data.
    /// </summary>
    public static IReadOnlyList<NormalizedCreditRole> NormalizeRoles(string? sourceRole)
    {
        if (string.IsNullOrWhiteSpace(sourceRole))
        {
            return Array.Empty<NormalizedCreditRole>();
        }

        var results = new List<NormalizedCreditRole>();
        foreach (var token in SplitTopLevelSegments(sourceRole))
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            results.AddRange(NormalizeToken(token.Trim()));
        }

        return results;
    }

    /// <summary>
    /// Splits commas outside brackets/parentheses. Splitting inside a bracketed
    /// qualifier at this stage would destroy the qualifier's structure; the
    /// bracket content is resolved by <see cref="NormalizeToken"/>.
    /// </summary>
    private static IEnumerable<string> SplitTopLevelSegments(string role)
    {
        var segments = new List<string>();
        var builder = new StringBuilder();
        var depth = 0;
        foreach (var ch in role)
        {
            if (ch is '[' or '(')
            {
                depth++;
                builder.Append(ch);
                continue;
            }

            if (ch is ']' or ')')
            {
                depth = Math.Max(0, depth - 1);
                builder.Append(ch);
                continue;
            }

            if (ch == ',' && depth == 0)
            {
                segments.Add(builder.ToString());
                builder.Clear();
                continue;
            }

            builder.Append(ch);
        }

        segments.Add(builder.ToString());
        return segments;
    }

    private static IEnumerable<NormalizedCreditRole> NormalizeToken(string token)
    {
        var openBracket = IndexOfOpeningQualifier(token);
        if (openBracket < 0)
        {
            return ResolvePlainLabel(token, token);
        }

        var mainLabel = token[..openBracket].Trim();
        var qualifier = token[openBracket..].Trim();
        var qualifierPieces = SplitTopLevelSegments(
            qualifier.Trim('[', ']', '(', ')').Trim())
            .Select(piece => piece.Trim())
            .Where(piece => piece.Length > 0)
            .ToList();

        var resolvedPieces = qualifierPieces
            .Select(piece => (piece, role: ResolveLabelToRole(piece)))
            .ToList();
        var mainResolved = ResolveLabelToRole(mainLabel);

        if (resolvedPieces.All(entry => entry.role is null))
        {
            // Qualifiers the registry does not recognize (e.g. "Engineer
            // [Loudness Mastering]") stay one unrecognized legacy label; the
            // qualifier text is never silently dropped.
            if (mainResolved is null)
            {
                return [new NormalizedCreditRole(string.Empty, token)];
            }

            return [new NormalizedCreditRole(mainResolved.Key, token)];
        }

        // A qualifier can specialize an explicit parent credit, but does not
        // establish an unrelated second contribution (e.g. Producer [Remix]).
        if (mainResolved is not null
            && NarrowerRoles.TryGetValue(mainResolved.Key, out var narrowings)
            && resolvedPieces.All(entry => entry.role is not null && narrowings.Contains(entry.role.Key)))
        {
            return resolvedPieces
                .Select(entry => new NormalizedCreditRole(entry.role!.Key, token))
                .DistinctBy(role => role.Key)
                .ToList();
        }

        return [new NormalizedCreditRole(mainResolved?.Key ?? string.Empty, token)];
    }

    /// <summary>
    /// Specializations that subsume their generic parent label when the
    /// qualifier listing resolves entirely within the family.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> NarrowerRoles =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["engineer"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "recordingEngineer", "mixingEngineer", "masteringEngineer", "assistantEngineer"
            },
            ["producer"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "executiveProducer", "coProducer", "additionalProducer", "vocalProducer"
            }
        };

    private static IReadOnlyList<NormalizedCreditRole> ResolvePlainLabel(string token, string original)
    {
        var role = ResolveLabelToRole(token);
        return role is null
            ? [new NormalizedCreditRole(string.Empty, original)]
            : [new NormalizedCreditRole(role.Key, original)];
    }

    private static int IndexOfOpeningQualifier(string token)
    {
        return token.IndexOfAny(['[', '(']);
    }

    private static CreditRoleDefinition? ResolveLabelToRole(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        return AliasLookup.TryGetValue(NormalizeAliasPhrase(label), out var role) ? role : null;
    }

    private static string NormalizeAliasPhrase(string input)
    {
        var builder = new StringBuilder(input.Length);
        var lastSeparator = false;
        foreach (var ch in input.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastSeparator = false;
                continue;
            }

            if (!lastSeparator)
            {
                builder.Append(' ');
                lastSeparator = true;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Recognized role entries formatted the way legacy combined-credit
    /// representations (INVOLVEDPEOPLE, TiDL style) store them: "Role: Name".
    /// </summary>
    public static bool TryParseCombinedCreditEntry(string? entry, out string? canonicalKey, out string? name)
    {
        canonicalKey = null;
        name = null;
        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var separatorIndex = entry.IndexOf(':');
        if (separatorIndex <= 0)
        {
            return false;
        }

        var label = entry[..separatorIndex].Trim();
        var value = entry[(separatorIndex + 1)..].Trim();
        if (label.Length == 0 || value.Length == 0)
        {
            return false;
        }

        var resolved = NormalizeRoles(label);
        if (resolved.Count != 1 || string.IsNullOrWhiteSpace(resolved[0].Key))
        {
            return false;
        }

        canonicalKey = resolved[0].Key;
        name = value;
        return true;
    }

    /// <summary>Normalize explicit provider credit data at the shared boundary.</summary>
    public static void NormalizeTrackCredits(AutoTagTrack track)
    {
        var additions = new List<(string Key, string Name)>();
        foreach (var (label, names) in track.Other.ToArray())
        {
            if (string.Equals(label, "involvedPeople", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var entry in names)
                {
                    var separator = entry.IndexOf(':');
                    if (separator <= 0) continue;
                    var originalLabel = entry[..separator].Trim();
                    var name = entry[(separator + 1)..].Trim();
                    if (name.Length == 0) continue;
                    foreach (var role in NormalizeRoles(originalLabel).Where(role => role.Key.Length > 0))
                    {
                        var creditedName = role.Key == "performer"
                            && !string.Equals(originalLabel, "performer", StringComparison.OrdinalIgnoreCase)
                            ? $"{name} [{originalLabel}]" : name;
                        additions.Add((role.Key, creditedName));
                    }
                }
            }
            else if (TryGetRole(label, out var role))
            {
                foreach (var name in names.Where(value => !string.IsNullOrWhiteSpace(value)))
                    additions.Add((role.Key, name));
            }
        }

        foreach (var (key, name) in additions)
        {
            if (!track.Other.TryGetValue(key, out var names))
                track.Other[key] = names = new List<string>();
            if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
        }
    }

    /// <summary>
    /// Adds every canonical role (and Melodicness) to a config-key →
    /// SupportedTag lookup, e.g. the runner's SupportedTagMap.
    /// </summary>
    public static void AddSupportedTagMappings(Dictionary<string, SupportedTag> map)
    {
        foreach (var role in Roles)
        {
            if (TryResolveEnumTag(role, out var tag))
            {
                map[role.Key] = tag;
            }
        }

        map["melodicness"] = SupportedTag.Melodicness;
    }

    private static bool TryResolveEnumTag(CreditRoleDefinition role, out SupportedTag tag)
    {
        var upper = role.RawTag;
        foreach (var name in Enum.GetNames<SupportedTag>())
        {
            if (name.Equals(upper, StringComparison.OrdinalIgnoreCase))
            {
                tag = (SupportedTag)Enum.Parse(typeof(SupportedTag), name);
                return true;
            }
        }

        tag = default;
        return false;
    }

    /// <summary>The registry role bound to a SupportedTag member, when any.</summary>
    public static bool TryGetRoleFor(SupportedTag tag, out CreditRoleDefinition role)
    {
        foreach (var candidate in Roles)
        {
            if (TryResolveEnumTag(candidate, out var member) && member == tag)
            {
                role = candidate;
                return true;
            }
        }

        role = null!;
        return false;
    }

    /// <summary>The physical raw tag a SupportedTag credit member binds to, when any.</summary>
    public static bool TryGetRawTagFor(SupportedTag tag, out string rawTag)
    {
        rawTag = string.Empty;
        if (tag == SupportedTag.Melodicness)
        {
            rawTag = "MELODICNESS";
            return true;
        }

        if (TryGetRoleFor(tag, out var role))
        {
            rawTag = role.RawTag;
            return true;
        }

        return false;
    }

    /// <summary>The physical raw tag for a canonical credit role key.</summary>
    public static IReadOnlyList<string> RawTagsFor(string canonicalKey)
    {
        return TryGetRole(canonicalKey, out var role) ? [role.RawTag] : [];
    }
}
