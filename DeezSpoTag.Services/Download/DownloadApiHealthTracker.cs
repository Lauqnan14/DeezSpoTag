using System.Collections.Concurrent;
namespace DeezSpoTag.Services.Download;

public interface IDownloadApiHealthTracker
{
    void ReportSuccess(string engine);

    IReadOnlyList<string> PrioritizeSources(
        IEnumerable<string> encodedSources,
        string? protectedEngine = null,
        DateTimeOffset? now = null);
}

public sealed class DownloadApiHealthTracker : IDownloadApiHealthTracker
{
    private static readonly TimeSpan RecentSuccessWindow = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, HealthEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    public void ReportSuccess(string engine)
    {
        var normalized = NormalizeEngine(engine);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var entry = _entries.GetOrAdd(normalized, static _ => new HealthEntry());
        lock (entry.Gate)
        {
            entry.LastSuccessUtc = DateTimeOffset.UtcNow;
        }
    }

    public IReadOnlyList<string> PrioritizeSources(
        IEnumerable<string> encodedSources,
        string? protectedEngine = null,
        DateTimeOffset? now = null)
    {
        var sourceList = encodedSources
            .Where(static source => !string.IsNullOrWhiteSpace(source))
            .ToList();
        if (sourceList.Count <= 1)
        {
            return sourceList;
        }

        var timestamp = now ?? DateTimeOffset.UtcNow;
        var ranked = sourceList
            .Select((encoded, index) => new RankedSource(
                Encoded: encoded,
                Source: DownloadSourceOrder.DecodeAutoSource(encoded).Source,
                OriginalIndex: index))
            .Where(static source => !string.IsNullOrWhiteSpace(source.Source))
            .Select(source => source with
            {
                Rank = ResolveRank(source.Source, protectedEngine, timestamp),
                LastSuccessUtc = ReadLastSuccessUtc(source.Source)
            })
            .ToList();

        return ranked.Count == 0
            ? sourceList
            : ranked
            .OrderBy(static source => source.Rank)
            .ThenByDescending(static source => source.LastSuccessUtc ?? DateTimeOffset.MinValue)
            .ThenBy(static source => source.OriginalIndex)
            .Select(static source => source.Encoded)
            .ToList();
    }

    private SourceRank ResolveRank(string engine, string? protectedEngine, DateTimeOffset now)
    {
        if (IsProtectedEngine(engine, protectedEngine))
        {
            return SourceRank.Neutral;
        }

        if (!_entries.TryGetValue(engine, out var entry))
        {
            return SourceRank.Neutral;
        }

        lock (entry.Gate)
        {
            if (entry.LastSuccessUtc.HasValue
                && now - entry.LastSuccessUtc.Value < RecentSuccessWindow)
            {
                return SourceRank.RecentSuccess;
            }

            return SourceRank.Neutral;
        }
    }

    private DateTimeOffset? ReadLastSuccessUtc(string engine)
    {
        if (!_entries.TryGetValue(engine, out var entry))
        {
            return null;
        }

        lock (entry.Gate)
        {
            return entry.LastSuccessUtc;
        }
    }

    private static bool IsProtectedEngine(string engine, string? protectedEngine)
        => !string.IsNullOrWhiteSpace(protectedEngine)
           && string.Equals(engine, protectedEngine, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeEngine(string? engine)
        => string.IsNullOrWhiteSpace(engine) ? string.Empty : engine.Trim().ToLowerInvariant();

    private sealed class HealthEntry
    {
        public object Gate { get; } = new();

        public DateTimeOffset? LastSuccessUtc { get; set; }
    }

    private sealed record RankedSource(
        string Encoded,
        string Source,
        int OriginalIndex)
    {
        public SourceRank Rank { get; init; } = SourceRank.Neutral;

        public DateTimeOffset? LastSuccessUtc { get; init; }
    }

    private enum SourceRank
    {
        RecentSuccess = 0,
        Neutral = 1
    }
}
