using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Services.Download.Utils;

namespace DeezSpoTag.Services.Download.Queue;

public sealed class LyricsArtifactState
{
    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "disabled";

    [JsonPropertyName("attemptId")]
    public string AttemptId { get; set; } = string.Empty;

    [JsonPropertyName("planFingerprint")]
    public string PlanFingerprint { get; set; } = string.Empty;

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

    [JsonPropertyName("lrcTiming")]
    public string? LrcTiming { get; set; }

    [JsonPropertyName("fileHashesByFormat")]
    public Dictionary<string, string> FileHashesByFormat { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    public static LyricsArtifactState Fetching(LyricsResolutionPlan plan, LyricsArtifactState? previous = null)
    {
        var planFingerprint = BuildPlanFingerprint(plan);
        var canReusePrevious = previous == null
            || string.IsNullOrWhiteSpace(previous.PlanFingerprint)
            || string.Equals(previous.PlanFingerprint, planFingerprint, StringComparison.Ordinal);
        var verifiedFiles = canReusePrevious
            ? VerifyExistingFiles(previous?.FilesByFormat, previous?.FileHashesByFormat)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var verifiedFormats = NormalizeFormats(verifiedFiles.Keys);
        var state = new LyricsArtifactState
        {
            Revision = Math.Max(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                (previous?.Revision ?? 0) + 1),
            Status = plan.RequestedFormats.Count == 0 ? "disabled" : "fetching",
            AttemptId = Guid.NewGuid().ToString("N"),
            PlanFingerprint = planFingerprint,
            RequestedFormats = NormalizeFormats(plan.RequestedFormats),
            PlainFallbackAllowed = plan.PlainFallbackAllowed,
            Providers = NormalizeTokens(plan.Providers),
            ResolvedFormats = verifiedFormats.ToList(),
            DownloadedFormats = verifiedFormats.ToList(),
            SourcesByFormat = (previous?.SourcesByFormat ?? new Dictionary<string, string>())
                .Where(pair => verifiedFormats.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            FilesByFormat = verifiedFiles,
            LrcTiming = verifiedFiles.ContainsKey("lrc") ? previous?.LrcTiming : null,
            FileHashesByFormat = verifiedFiles
                .Select(pair => new
                {
                    pair.Key,
                    Hash = TryComputeFileHash(DownloadPathResolver.ResolveIoPath(pair.Value))
                })
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Hash))
                .ToDictionary(pair => pair.Key, pair => pair.Hash!, StringComparer.OrdinalIgnoreCase)
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

    public void ApplyDownloadedFiles(IReadOnlyDictionary<string, string> filesByFormat)
    {
        Revision++;
        FilesByFormat = filesByFormat
            .Where(pair => IsSupportedFormat(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(
                pair => pair.Key.Trim().ToLowerInvariant(),
                pair => DownloadPathResolver.NormalizeDisplayPath(pair.Value),
                StringComparer.OrdinalIgnoreCase);
        DownloadedFormats = NormalizeFormats(FilesByFormat.Keys);
        FileHashesByFormat = FilesByFormat
            .Select(pair => new
            {
                pair.Key,
                Hash = TryComputeFileHash(DownloadPathResolver.ResolveIoPath(pair.Value))
            })
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Hash))
            .ToDictionary(pair => pair.Key, pair => pair.Hash!, StringComparer.OrdinalIgnoreCase);
        foreach (var format in DownloadedFormats)
        {
            if (!ResolvedFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
            {
                ResolvedFormats.Add(format);
            }
        }
        LrcTiming = TryReadLrcTiming(
            FilesByFormat.TryGetValue("lrc", out var lrcPath) ? lrcPath : null);
        SuppressPlainWhenRichExists();
        Status = ResolvedFormats.Count > 0 ? "completed" : Status;
    }

    /// <summary>
    /// Applies move-rebased sidecar paths (staging → library). Unlike
    /// <see cref="ApplyDownloadedFiles"/>, this never downgrades timing precision
    /// because a file was temporarily unreadable: when the sidecar has not landed
    /// at its destination path yet (the destination move is still in flight), the
    /// timing verified at fetch time is preserved instead of being re-derived
    /// from a failed read.
    /// </summary>
    public void ApplyRebasedFiles(IReadOnlyDictionary<string, string> rebasedFiles)
    {
        Revision++;
        var previousFiles = new Dictionary<string, string>(FilesByFormat, StringComparer.OrdinalIgnoreCase);
        var previousTiming = LrcTiming;
        FilesByFormat = rebasedFiles
            .Where(pair => IsSupportedFormat(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(
                pair => pair.Key.Trim().ToLowerInvariant(),
                pair => DownloadPathResolver.NormalizeDisplayPath(pair.Value),
                StringComparer.OrdinalIgnoreCase);
        DownloadedFormats = NormalizeFormats(FilesByFormat.Keys);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var format in FilesByFormat.Keys)
        {
            if (previousFiles.TryGetValue(format, out var previousPath)
                && FileHashesByFormat.TryGetValue(format, out var previousHash)
                && string.Equals(previousPath, FilesByFormat[format], StringComparison.OrdinalIgnoreCase))
            {
                // Same file, same content: keep the verified hash.
                hashes[format] = previousHash;
                continue;
            }

            var hash = TryComputeFileHash(DownloadPathResolver.ResolveIoPath(FilesByFormat[format]));
            if (!string.IsNullOrWhiteSpace(hash))
            {
                hashes[format] = hash;
            }
        }

        FileHashesByFormat = hashes;
        foreach (var format in DownloadedFormats)
        {
            if (!ResolvedFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
            {
                ResolvedFormats.Add(format);
            }
        }

        if (FilesByFormat.TryGetValue("lrc", out var rebasedLrcPath))
        {
            LrcTiming = TryReadLrcTiming(rebasedLrcPath)
                ?? (previousFiles.ContainsKey("lrc") ? previousTiming : null);
        }
        else
        {
            LrcTiming = null;
        }

        SuppressPlainWhenRichExists();
        Status = ResolvedFormats.Count > 0 ? "completed" : Status;
    }

    /// <summary>
    /// Reads timing precision from the sidecar on disk. A missing or unreadable
    /// file yields <c>null</c> (unknown) — never a false "line" claim.
    /// </summary>
    private static string? TryReadLrcTiming(string? lrcPath)
    {
        if (string.IsNullOrWhiteSpace(lrcPath))
        {
            return null;
        }

        try
        {
            var path = DownloadPathResolver.ResolveIoPath(lrcPath);
            if (!File.Exists(path))
            {
                return null;
            }

            return LrcContent.IsWordSynchronized(File.ReadAllText(path))
                ? "word"
                : "line";
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return null;
        }
    }

    public bool HasLyricsArtifacts()
        => RequestedFormats.Count > 0
           || ResolvedFormats.Count > 0
           || DownloadedFormats.Count > 0
           || FilesByFormat.Count > 0;

    private void SuppressPlainWhenRichExists()
    {
        var hasRich = ResolvedFormats.Contains("ttml", StringComparer.OrdinalIgnoreCase)
            || ResolvedFormats.Contains("lrc", StringComparer.OrdinalIgnoreCase)
            || DownloadedFormats.Contains("ttml", StringComparer.OrdinalIgnoreCase)
            || DownloadedFormats.Contains("lrc", StringComparer.OrdinalIgnoreCase);
        if (!hasRich)
        {
            return;
        }

        ResolvedFormats.RemoveAll(format => string.Equals(format, "txt", StringComparison.OrdinalIgnoreCase));
        DownloadedFormats.RemoveAll(format => string.Equals(format, "txt", StringComparison.OrdinalIgnoreCase));
        SourcesByFormat.Remove("txt");
        FilesByFormat.Remove("txt");
        FileHashesByFormat?.Remove("txt");
    }

    private static bool IsSupportedFormat(string format)
        => format.Trim().ToLowerInvariant() is "ttml" or "lrc" or "txt";

    private static Dictionary<string, string> VerifyExistingFiles(
        IReadOnlyDictionary<string, string>? filesByFormat,
        IReadOnlyDictionary<string, string>? hashesByFormat)
    {
        if (filesByFormat == null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var verified = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in filesByFormat)
        {
            var format = pair.Key.Trim().ToLowerInvariant();
            if (!IsSupportedFormat(format) || string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            var path = DownloadPathResolver.ResolveIoPath(pair.Value);
            if (!File.Exists(path)
                || !string.Equals(
                    Path.GetExtension(path),
                    $".{format}",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (new FileInfo(path).Length > 0)
                {
                    var actualHash = ComputeFileHash(path);
                    if (hashesByFormat?.TryGetValue(format, out var expectedHash) == true
                        && !string.IsNullOrWhiteSpace(expectedHash)
                        && !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    verified[format] = DownloadPathResolver.NormalizeDisplayPath(path);
                }
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                // Missing or unreadable artifacts are intentionally refetched.
            }
        }
        return verified;
    }

    private static string BuildPlanFingerprint(LyricsResolutionPlan plan)
    {
        var value = $"{string.Join(',', NormalizeFormats(plan.RequestedFormats))}\u001f"
            + $"{string.Join(',', NormalizeTokens(plan.Providers))}\u001f"
            + plan.PlainFallbackAllowed;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string? TryComputeFileHash(string path)
    {
        try
        {
            return File.Exists(path) ? ComputeFileHash(path) : null;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return null;
        }
    }

    private static List<string> NormalizeFormats(IEnumerable<string> formats)
        => formats.Select(static value => value.Trim().ToLowerInvariant())
            .Where(static value => value is "ttml" or "lrc" or "txt")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> NormalizeTokens(IEnumerable<string> values)
        => values.Select(static value => value.Trim().ToLowerInvariant())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
