using System.Linq;

namespace DeezSpoTag.Web.Services;

public interface ISpotifyTracklistMatchStore
{
    void Start(string token, int pendingCount, string? signature);
    void IncrementPending(string token, int pendingCount);
    void RecordProgress(string token, int index, string? spotifyId, string status, string reason, int attempt);
    void RecordMatch(string token, int index, string deezerId, string? spotifyId, string status, string reason, int attempt);
    SpotifyTracklistMatchSnapshot? GetSnapshot(string token);
    SpotifyTracklistMatchSnapshot? GetSnapshotBySignature(string signature);
    void CacheSignatureSnapshot(string signature, IReadOnlyList<SpotifyTracklistMatchEntry> matches);
    void Activate(string token);
    bool IsActive(string token);
}

public sealed class SpotifyTracklistMatchStore : ISpotifyTracklistMatchStore
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan SignatureCacheTtl = TimeSpan.FromHours(2);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MatchState> _matches = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SignatureSnapshot> _signatureSnapshots = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _activeTokens =
        new(StringComparer.Ordinal);

    public void Start(string token, int pendingCount, string? signature)
    {
        if (_matches.TryGetValue(token, out var state))
        {
            var signatureChanged = !string.IsNullOrWhiteSpace(signature)
                && !string.Equals(state.Signature, signature, StringComparison.Ordinal);

            if (signatureChanged)
            {
                state.Matches.Clear();
                state.Pending = Math.Max(0, pendingCount);
                state.Signature = signature;
            }
            else
            {
                var matchedCount = state.Matches.Count;
                state.Pending = Math.Max(0, pendingCount - matchedCount);
                if (string.IsNullOrWhiteSpace(state.Signature) && !string.IsNullOrWhiteSpace(signature))
                {
                    state.Signature = signature;
                }
            }

            state.LastUpdated = DateTimeOffset.UtcNow;
            return;
        }

        _matches[token] = new MatchState(pendingCount, signature);
    }

    public void IncrementPending(string token, int pendingCount)
    {
        if (pendingCount <= 0)
        {
            return;
        }

        if (!_matches.TryGetValue(token, out var state))
        {
            state = new MatchState(0, null);
            _matches[token] = state;
        }

        state.Pending += pendingCount;
        state.LastUpdated = DateTimeOffset.UtcNow;
    }

    public void RecordProgress(string token, int index, string? spotifyId, string status, string reason, int attempt)
    {
        if (!_matches.TryGetValue(token, out var state))
        {
            state = new MatchState(0, null);
            _matches[token] = state;
        }

        state.Matches[index] = new SpotifyTracklistMatchEntry(
            index,
            string.Empty,
            spotifyId ?? string.Empty,
            status,
            reason,
            attempt);
        state.LastUpdated = DateTimeOffset.UtcNow;
    }

    public void RecordMatch(string token, int index, string deezerId, string? spotifyId, string status, string reason, int attempt)
    {
        if (!_matches.TryGetValue(token, out var state))
        {
            state = new MatchState(0, null);
            _matches[token] = state;
        }

        state.Matches[index] = new SpotifyTracklistMatchEntry(
            index,
            deezerId,
            spotifyId ?? string.Empty,
            status,
            reason,
            attempt);
        state.Pending = Math.Max(0, state.Pending - 1);
        state.LastUpdated = DateTimeOffset.UtcNow;

        if (state.Pending == 0 && !string.IsNullOrWhiteSpace(state.Signature))
        {
            CacheSignatureSnapshot(state.Signature!, BuildEntries(state.Matches));
        }
    }

    public SpotifyTracklistMatchSnapshot? GetSnapshot(string token)
    {
        if (!_matches.TryGetValue(token, out var state))
        {
            return null;
        }

        if (DateTimeOffset.UtcNow - state.LastUpdated > CacheTtl)
        {
            _matches.TryRemove(token, out _);
            return null;
        }

        return BuildSnapshot(state.Pending, BuildEntries(state.Matches));
    }

    public SpotifyTracklistMatchSnapshot? GetSnapshotBySignature(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return null;
        }

        if (!_signatureSnapshots.TryGetValue(signature, out var snapshot))
        {
            return null;
        }

        if (DateTimeOffset.UtcNow - snapshot.LastUpdated > SignatureCacheTtl)
        {
            _signatureSnapshots.TryRemove(signature, out _);
            return null;
        }

        return BuildSnapshot(0, snapshot.Matches);
    }

    public void CacheSignatureSnapshot(string signature, IReadOnlyList<SpotifyTracklistMatchEntry> matches)
    {
        if (string.IsNullOrWhiteSpace(signature) || matches.Count == 0)
        {
            return;
        }

        _signatureSnapshots[signature] = new SignatureSnapshot(matches);
    }

    public void Activate(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        _activeTokens[token] = DateTimeOffset.UtcNow;
        PruneActiveTokens();
    }

    public bool IsActive(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (!_activeTokens.TryGetValue(token, out var stamp))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - stamp > CacheTtl)
        {
            _activeTokens.TryRemove(token, out _);
            return false;
        }

        return true;
    }

    private void PruneActiveTokens()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _activeTokens.Where(entry => now - entry.Value > CacheTtl))
        {
            _activeTokens.TryRemove(entry.Key, out _);
        }
    }

    private sealed class MatchState
    {
        public MatchState(int pending, string? signature)
        {
            Pending = pending;
            LastUpdated = DateTimeOffset.UtcNow;
            Signature = signature;
        }

        public int Pending { get; set; }
        public DateTimeOffset LastUpdated { get; set; }
        public string? Signature { get; set; }
        public System.Collections.Concurrent.ConcurrentDictionary<int, SpotifyTracklistMatchEntry> Matches { get; } = new();
    }

    private sealed class SignatureSnapshot
    {
        public SignatureSnapshot(IReadOnlyList<SpotifyTracklistMatchEntry> matches)
        {
            Matches = matches;
            LastUpdated = DateTimeOffset.UtcNow;
        }

        public IReadOnlyList<SpotifyTracklistMatchEntry> Matches { get; }
        public DateTimeOffset LastUpdated { get; }
    }

    private static List<SpotifyTracklistMatchEntry> BuildEntries(
        System.Collections.Concurrent.ConcurrentDictionary<int, SpotifyTracklistMatchEntry> matches)
    {
        return matches
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => kvp.Value)
            .ToList();
    }

    private static SpotifyTracklistMatchSnapshot BuildSnapshot(
        int pending,
        IReadOnlyList<SpotifyTracklistMatchEntry> entries)
    {
        var matched = entries.Count(entry => !string.IsNullOrWhiteSpace(entry.DeezerId));
        var failed = entries.Count(entry =>
            string.Equals(entry.Status, "unmatched_final", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.Status, "hard_mismatch", StringComparison.OrdinalIgnoreCase));
        var rechecking = entries.Count(entry =>
            string.Equals(entry.Status, "rechecking", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.Status, "transient_failure", StringComparison.OrdinalIgnoreCase));
        return new SpotifyTracklistMatchSnapshot(pending, matched, failed, rechecking, entries.ToList());
    }
}

public sealed record SpotifyTracklistMatchSnapshot(
    int Pending,
    int Matched,
    int Failed,
    int Rechecking,
    List<SpotifyTracklistMatchEntry> Matches);

public sealed record SpotifyTracklistMatchEntry(
    int Index,
    string DeezerId,
    string SpotifyId,
    string Status,
    string Reason,
    int Attempt);
