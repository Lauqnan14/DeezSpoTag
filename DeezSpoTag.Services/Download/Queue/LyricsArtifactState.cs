using System.Text.Json.Serialization;
using DeezSpoTag.Services.Download.Utils;

namespace DeezSpoTag.Services.Download.Queue;

public sealed class LyricsArtifactState
{
    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "disabled";

    [JsonPropertyName("requestedFormats")]
    public List<string> RequestedFormats { get; set; } = new();

    [JsonPropertyName("plainFallbackAllowed")]
    public bool PlainFallbackAllowed { get; set; }

    [JsonPropertyName("providers")]
    public List<string> Providers { get; set; } = new();

    [JsonPropertyName("providersAttempted")]
    public List<string> ProvidersAttempted { get; set; } = new();

    [JsonPropertyName("resolvedFormats")]
    public List<string> ResolvedFormats { get; set; } = new();

    [JsonPropertyName("downloadedFormats")]
    public List<string> DownloadedFormats { get; set; } = new();

    [JsonPropertyName("sourcesByFormat")]
    public Dictionary<string, string> SourcesByFormat { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("filesByFormat")]
    public Dictionary<string, string> FilesByFormat { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    public static LyricsArtifactState Fetching(LyricsResolutionPlan plan, LyricsArtifactState? previous = null)
    {
        var state = new LyricsArtifactState
        {
            Revision = Math.Max(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                (previous?.Revision ?? 0) + 1),
            Status = plan.RequestedFormats.Count == 0 ? "disabled" : "fetching",
            RequestedFormats = NormalizeFormats(plan.RequestedFormats),
            PlainFallbackAllowed = plan.PlainFallbackAllowed,
            Providers = NormalizeTokens(plan.Providers),
            ResolvedFormats = NormalizeFormats(previous?.ResolvedFormats ?? []),
            DownloadedFormats = NormalizeFormats(previous?.DownloadedFormats ?? []),
            SourcesByFormat = new Dictionary<string, string>(previous?.SourcesByFormat ?? new(), StringComparer.OrdinalIgnoreCase),
            FilesByFormat = new Dictionary<string, string>(previous?.FilesByFormat ?? new(), StringComparer.OrdinalIgnoreCase)
        };
        state.SuppressPlainWhenRichExists();
        if (state.Satisfies(plan))
        {
            state.Status = "completed";
        }
        return state;
    }

    public bool Satisfies(LyricsResolutionPlan plan)
    {
        var available = ResolvedFormats
            .Concat(DownloadedFormats)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return plan.RequestedFormats.Count > 0
            && plan.RequestedFormats.All(format => available.Contains(format));
    }

    public void ApplyResolution(LyricsResolutionResult result)
    {
        Revision++;
        ProvidersAttempted = NormalizeTokens(result.ProvidersAttempted);
        foreach (var format in NormalizeFormats(result.ResolvedFormats))
        {
            if (!ResolvedFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
            {
                ResolvedFormats.Add(format);
            }
        }
        foreach (var pair in result.SourcesByFormat.Where(pair =>
                     ResolvedFormats.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)))
        {
            SourcesByFormat[pair.Key.ToLowerInvariant()] = pair.Value;
        }
        Error = result.Error;
        SuppressPlainWhenRichExists();
        Status = ResolvedFormats.Count > 0 ? "resolved" : "unavailable";
    }

    public void ApplyDownloadedFiles(
        IReadOnlyDictionary<string, string> filesByFormat,
        bool ttmlSynthesized = false)
    {
        Revision++;
        FilesByFormat = filesByFormat
            .Where(pair => IsSupportedFormat(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(
                pair => pair.Key.Trim().ToLowerInvariant(),
                pair => DownloadPathResolver.NormalizeDisplayPath(pair.Value),
                StringComparer.OrdinalIgnoreCase);
        DownloadedFormats = NormalizeFormats(FilesByFormat.Keys);
        foreach (var format in DownloadedFormats)
        {
            if (!ResolvedFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
            {
                ResolvedFormats.Add(format);
            }
        }
        if (ttmlSynthesized && DownloadedFormats.Contains("ttml", StringComparer.OrdinalIgnoreCase))
        {
            SourcesByFormat["ttml"] = "synthesized";
        }
        SuppressPlainWhenRichExists();
        Status = ResolvedFormats.Count > 0 ? "completed" : Status;
    }

    public bool HasLyricsArtifacts()
        => RequestedFormats.Count > 0
           || ResolvedFormats.Count > 0
           || DownloadedFormats.Count > 0
           || FilesByFormat.Count > 0;

    private void SuppressPlainWhenRichExists()
    {
        var hasRich = ResolvedFormats.Contains("ttml", StringComparer.OrdinalIgnoreCase)
            || ResolvedFormats.Contains("elrc", StringComparer.OrdinalIgnoreCase)
            || ResolvedFormats.Contains("lrc", StringComparer.OrdinalIgnoreCase)
            || DownloadedFormats.Contains("ttml", StringComparer.OrdinalIgnoreCase)
            || DownloadedFormats.Contains("elrc", StringComparer.OrdinalIgnoreCase)
            || DownloadedFormats.Contains("lrc", StringComparer.OrdinalIgnoreCase);
        if (!hasRich)
        {
            return;
        }

        ResolvedFormats.RemoveAll(format => string.Equals(format, "txt", StringComparison.OrdinalIgnoreCase));
        DownloadedFormats.RemoveAll(format => string.Equals(format, "txt", StringComparison.OrdinalIgnoreCase));
        SourcesByFormat.Remove("txt");
        FilesByFormat.Remove("txt");
    }

    private static bool IsSupportedFormat(string format)
        => format.Trim().ToLowerInvariant() is "ttml" or "elrc" or "lrc" or "txt";

    private static List<string> NormalizeFormats(IEnumerable<string> formats)
        => formats.Select(static value => value.Trim().ToLowerInvariant())
            .Where(static value => value is "ttml" or "elrc" or "lrc" or "txt")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> NormalizeTokens(IEnumerable<string> values)
        => values.Select(static value => value.Trim().ToLowerInvariant())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
