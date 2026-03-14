namespace DeezSpoTag.Web.Services.CoverPort;

public sealed record SacadSearchOptionInput(
    int Size,
    int SizeTolerancePercent = 25,
    bool PreserveFormat = false,
    IReadOnlyCollection<string>? CoverSources = null);

public static class CoverSacadOptionMapper
{
    public static CoverSearchOptions Map(
        SacadSearchOptionInput input,
        string? referenceImagePath = null,
        byte[]? referenceImageBytes = null,
        int maxCandidatesToTry = 20)
    {
        var enabledSources = ParseSources(input.CoverSources);
        return new CoverSearchOptions(
            TargetSize: Math.Clamp(input.Size <= 0 ? 1200 : input.Size, 64, 10000),
            SizeTolerancePercent: Math.Clamp(input.SizeTolerancePercent, 0, 95),
            PreserveSourceFormat: input.PreserveFormat,
            PreferPng: false,
            CrunchPng: true,
            UsePerceptualHashScoring: true,
            ReferenceImagePath: referenceImagePath,
            ReferenceImageBytes: referenceImageBytes,
            MaxCandidatesToTry: Math.Clamp(maxCandidatesToTry, 1, 200),
            ScoringMode: CoverScoringMode.SacadCompatibility,
            EnabledSources: enabledSources);
    }

    public static IReadOnlyCollection<CoverSourceName>? ParseSources(IReadOnlyCollection<string>? sources)
    {
        if (sources == null || sources.Count == 0)
        {
            return null;
        }

        var mapped = sources
            .Select(source => TryParseSource(source, out var sourceName) ? (CoverSourceName?)sourceName : null)
            .Where(static sourceName => sourceName.HasValue)
            .Select(static sourceName => sourceName!.Value)
            .Distinct()
            .ToList();

        return mapped.Count == 0 ? null : mapped;
    }

    public static bool TryParseSource(string? value, out CoverSourceName source)
    {
        source = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant().Replace("-", string.Empty).Replace("_", string.Empty);
        source = normalized switch
        {
            "coverartarchive" => CoverSourceName.CoverArtArchive,
            "deezer" => CoverSourceName.Deezer,
            "discogs" => CoverSourceName.Discogs,
            "itunes" => CoverSourceName.Itunes,
            "lastfm" => CoverSourceName.LastFm,
            _ => default
        };

        return normalized is "coverartarchive" or "deezer" or "discogs" or "itunes" or "lastfm";
    }
}
