using DeezSpoTag.Core.Models.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Applies the user's Soulseek peer rules.
/// </summary>
/// <remarks>
///     <para>
///         This runs before scoring, so a peer the user does not want never influences candidate selection.
///         A peer is rejected for a blocklist entry, an active cooldown, a busy queue, insufficient upload
///         speed, or a missing free upload slot.
///     </para>
///     <para>
///         Rejections carry a short reason code rather than a sentence, so the same value can be stored on a
///         candidate row and compared in tests.
///     </para>
/// </remarks>
public sealed class SoulseekPeerPolicyService : ISoulseekPeerPolicyService
{
    private readonly SoulseekSettingsService _settings;
    private readonly SoulseekRepository _repository;
    private readonly ILogger<SoulseekPeerPolicyService> _logger;

    /// <summary>Initializes a new instance of the <see cref="SoulseekPeerPolicyService"/> class.</summary>
    public SoulseekPeerPolicyService(
        SoulseekSettingsService settings,
        SoulseekRepository repository,
        ILogger<SoulseekPeerPolicyService> logger)
    {
        _settings = settings;
        _repository = repository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SoulseekPeerDecision> EvaluateAsync(
        SoulseekRawCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        var cooldowns = await ReadCooldownsAsync(cancellationToken).ConfigureAwait(false);
        return Evaluate(candidate, cooldowns);
    }

    /// <inheritdoc />
    public Task<SoulseekPeerDecision> EvaluateAsync(
        SoulseekRawCandidate candidate,
        IReadOnlyList<(string Username, DateTimeOffset CooldownExpiresUtc)> cooldownsInEffect,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Evaluate(candidate, cooldownsInEffect));

    /// <summary>
    ///     The decision itself, which needs no I/O once the cooldowns are in hand.
    /// </summary>
    private SoulseekPeerDecision Evaluate(
        SoulseekRawCandidate candidate,
        IReadOnlyList<(string Username, DateTimeOffset CooldownExpiresUtc)> cooldownsInEffect)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(cooldownsInEffect);

        var settings = _settings.GetSettings();
        var username = candidate.Username.Trim();

        if (IsBlocked(settings, username))
        {
            return Reject("blocked_user");
        }

        if (settings.RequireFreeUploadSlot && !candidate.PeerHasFreeUploadSlot)
        {
            return Reject("no_free_upload_slot");
        }

        if (candidate.PeerQueueLength > settings.MaximumPeerQueueLength)
        {
            return Reject("peer_queue_too_long");
        }

        if (settings.MinimumPeerUploadSpeedBytesPerSecond > 0
            && candidate.PeerUploadSpeed < settings.MinimumPeerUploadSpeedBytesPerSecond)
        {
            return Reject("upload_speed_too_low");
        }

        if (IsInCooldown(cooldownsInEffect, username))
        {
            return Reject("peer_in_cooldown");
        }

        return new SoulseekPeerDecision(true);
    }

    /// <summary>
    ///     Reads every peer currently in cooldown, in one call.
    /// </summary>
    private async Task<IReadOnlyList<(string Username, DateTimeOffset CooldownExpiresUtc)>> ReadCooldownsAsync(
        CancellationToken cancellationToken)
    {
        var stats = await _repository.GetPeersInCooldownAsync(cancellationToken).ConfigureAwait(false);
        return stats
            .Where(stat => stat.CooldownExpiresUtc is { } expires && expires > DateTimeOffset.UtcNow)
            .Select(stat => (stat.Username, stat.CooldownExpiresUtc!.Value))
            .ToList();
    }

    /// <summary>
    ///     Reports whether a peer is in cooldown according to an already-read snapshot.
    /// </summary>
    /// <remarks>
    ///     The caller has already filtered out expired cooldowns, so presence alone is the answer. The
    ///     comparison is ordinal-ignore-case because Soulseek usernames are case-insensitive on the network.
    /// </remarks>
    private static bool IsInCooldown(
        IReadOnlyList<(string Username, DateTimeOffset CooldownExpiresUtc)> cooldownsInEffect,
        string username)
    {
        for (var i = 0; i < cooldownsInEffect.Count; i++)
        {
            if (string.Equals(cooldownsInEffect[i].Username, username, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public Task RecordFailureAsync(string username, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return Task.CompletedTask;
        }

        var settings = _settings.GetSettings();
        if (settings.PeerCooldownMinutes <= 0)
        {
            // Cooldowns are switched off, so do not accumulate rows that nothing will ever act on.
            return Task.CompletedTask;
        }

        return _repository.RecordPeerFailureAsync(
            username.Trim(),
            string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim(),
            TimeSpan.FromMinutes(settings.PeerCooldownMinutes),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task RecordSuccessAsync(string username, CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(username)
            ? Task.CompletedTask
            : _repository.RecordPeerSuccessAsync(username.Trim(), cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<(string Username, DateTimeOffset CooldownExpiresUtc)>> GetCooldownsAsync(
        CancellationToken cancellationToken = default)
    {
        var stats = await _repository.GetPeersInCooldownAsync(cancellationToken).ConfigureAwait(false);
        return stats
            .Where(stat => stat.CooldownExpiresUtc.HasValue)
            .Select(stat => (stat.Username, stat.CooldownExpiresUtc!.Value))
            .ToList();
    }

    /// <inheritdoc />
    public Task<int> CleanupExpiredCooldownsAsync(CancellationToken cancellationToken = default)
        => _repository.DeleteExpiredCooldownsAsync(cancellationToken);

    /// <summary>
    ///     Returns a value indicating whether a filename matches one of the user's blocked patterns.
    /// </summary>
    /// <remarks>
    ///     Matching is case-insensitive substring matching, which is what a user writing "live" or
    ///     "unreleased" expects. A <c>*</c> or <c>?</c> turns the entry into a simple wildcard so a pattern
    ///     can be anchored, for example <c>*/live/*</c>. Path separators are normalized to forward slashes
    ///     first, because Soulseek filenames use backslashes and a user writing a path-style pattern should
    ///     not have to know that. Anything else stays a plain substring test, so a pattern that is not valid
    ///     regular expression syntax can never throw.
    /// </remarks>
    public bool IsBlockedFilename(string? filename)
        => IsBlockedFilename(filename, _settings.GetSettings());

    /// <summary>
    ///     Returns a value indicating whether a filename matches one of the supplied settings' blocked patterns.
    /// </summary>
    public static bool IsBlockedFilename(string? filename, SoulseekDownloadSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(filename))
        {
            return false;
        }

        if (settings.BlockedFilenamePatterns.Count == 0)
        {
            return false;
        }

        var haystack = filename.Replace('\\', '/').ToLowerInvariant();
        return settings.BlockedFilenamePatterns.Any(pattern => MatchesPattern(
            haystack,
            pattern.Replace('\\', '/').ToLowerInvariant()));
    }

    /// <summary>
    ///     Returns a value indicating whether a username is on the blocklist.
    /// </summary>
    public static bool IsBlockedUser(string? username, SoulseekDownloadSettings settings)
    {
        if (string.IsNullOrWhiteSpace(username) || settings.BlockedUsers.Count == 0)
        {
            return false;
        }

        var normalized = username.Trim();
        return settings.BlockedUsers.Any(blocked => string.Equals(blocked.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsBlocked(SoulseekDownloadSettings settings, string username)
    {
        if (!IsBlockedUser(username, settings))
        {
            return false;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Soulseek peer {Username} is on the user's blocklist.", username);
        }

        return true;
    }

    private static bool MatchesPattern(string haystack, string pattern)
    {
        var needle = pattern.Trim();
        if (needle.Length == 0)
        {
            return false;
        }

        var lowered = needle.ToLowerInvariant();
        if (!lowered.Contains('*') && !lowered.Contains('?') && !lowered.Contains('['))
        {
            return haystack.Contains(lowered, StringComparison.Ordinal);
        }

        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(
                haystack,
                System.Text.RegularExpressions.Regex.Escape(needle).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal),
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException)
        {
            return haystack.Contains(lowered, StringComparison.Ordinal);
        }
    }

    private static SoulseekPeerDecision Reject(string reason) => new(false, reason);
}
