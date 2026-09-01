using DeezSpoTag.Services.Download.Shared.Models;
using Microsoft.Extensions.Options;

namespace DeezSpoTag.Web.Services;

public sealed class BoomplaySessionRecoveryOptions
{
    public bool Enabled { get; set; } = true;
    public int CooldownMinutes { get; set; } = 15;
    public int MaxConsecutiveFailures { get; set; } = 3;
}

public interface IBoomplaySessionRecoveryService
{
    Task<bool> TryRecoverAsync(string reason, CancellationToken cancellationToken);
}

/// <summary>
/// Self-heals the Boomplay Cloudflare session: on a challenge, solves it with a real headless
/// browser, merges the harvested cf_clearance into the stored Boomplay session (login pairs are
/// preserved), persists it, and wakes the watchlist. Repeated failures fall back to the manual
/// cookie path with a clear notification instead of retrying forever.
/// </summary>
public sealed class BoomplaySessionRecoveryService : IBoomplaySessionRecoveryService
{
    private const string NotificationKind = "boomplay_session";
    private const string ChallengeDedupeKey = "boomplay-session-challenge";
    private const string RecoveredDedupeKey = "boomplay-session-recovered";

    private readonly IBoomplayChallengeSolver _solver;
    private readonly PlatformAuthService _platformAuthService;
    private readonly INotificationSink _notifications;
    private readonly WatchlistRunSignal? _runSignal;
    private readonly IOptions<BoomplaySessionRecoveryOptions> _options;
    private readonly ILogger<BoomplaySessionRecoveryService> _logger;

    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private DateTimeOffset _lastAttemptUtc = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private bool _disabledByRepeatedFailures;

    public BoomplaySessionRecoveryService(
        IBoomplayChallengeSolver solver,
        PlatformAuthService platformAuthService,
        IOptions<BoomplaySessionRecoveryOptions> options,
        ILogger<BoomplaySessionRecoveryService> logger,
        INotificationSink? notifications = null,
        WatchlistRunSignal? runSignal = null)
    {
        _solver = solver;
        _platformAuthService = platformAuthService;
        _options = options;
        _logger = logger;
        _notifications = notifications ?? NullNotificationSink.Instance;
        _runSignal = runSignal;
    }

    public async Task<bool> TryRecoverAsync(string reason, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.Enabled || _disabledByRepeatedFailures)
        {
            return false;
        }

        // Concurrent challenge triggers coalesce into a single solve attempt.
        if (!_singleFlight.Wait(0, cancellationToken))
        {
            return false;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var cooldown = TimeSpan.FromMinutes(Math.Max(0, options.CooldownMinutes));
            if (now - _lastAttemptUtc < cooldown)
            {
                return false;
            }

            _lastAttemptUtc = now;

            BoomplayChallengeSolveResult? solved;
            try
            {
                solved = await _solver.SolveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RegisterFailure($"the challenge solver failed ({ex.Message})", options);
                return false;
            }

            if (solved is null)
            {
                RegisterFailure("the challenge did not clear within the solve window", options);
                return false;
            }

            var state = await _platformAuthService.LoadAsync();
            var mergedCookie = BoomplaySessionCookieMerger.Merge(
                state.Boomplay?.Cookie,
                solved.ChallengeCookiePair);
            if (!BoomplaySessionCookie.TryNormalize(mergedCookie, out var normalizedCookie)
                || string.IsNullOrWhiteSpace(solved.UserAgent))
            {
                RegisterFailure("the harvested Cloudflare cookie failed validation", options);
                return false;
            }

            state.Boomplay ??= new BoomplayAuth();
            state.Boomplay.Cookie = normalizedCookie;
            state.Boomplay.UserAgent = solved.UserAgent;
            state.Boomplay.SessionValid = true;
            state.Boomplay.LastStatus = "recovered";
            state.Boomplay.SavedAt = DateTimeOffset.UtcNow;
            await _platformAuthService.SaveAsync(state);

            _consecutiveFailures = 0;
            _notifications.Resolve(
                ChallengeDedupeKey,
                manuallyResolved: true,
                recoveryTitle: "Boomplay session recovered",
                recoveryBody: "Cloudflare challenge cleared; Boomplay playlist fetching resumed automatically.");
            _notifications.Raise(
                NotificationKind,
                "Boomplay session recovered",
                $"Cloudflare challenge cleared ({reason}); the refreshed session was stored automatically.",
                "Info",
                dedupeKey: RecoveredDedupeKey);
            _runSignal?.Request(WatchlistWakeReason.Reconciliation);
            _logger.LogInformation(
                "Boomplay Cloudflare session recovered ({Reason}); harvested cf_clearance persisted.",
                reason);
            return true;
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    private void RegisterFailure(string detail, BoomplaySessionRecoveryOptions options)
    {
        _consecutiveFailures++;
        var attempts = Math.Max(1, options.MaxConsecutiveFailures);
        if (_consecutiveFailures >= attempts)
        {
            _disabledByRepeatedFailures = true;
            _notifications.Raise(
                NotificationKind,
                "Boomplay session recovery disabled",
                $"Automatic Cloudflare recovery failed {detail} on {_consecutiveFailures} consecutive attempts. Refresh the Boomplay cookie on the Login page to restore session fetching.",
                "Warning",
                dedupeKey: ChallengeDedupeKey);
            _logger.LogWarning(
                "Boomplay session recovery disabled after {ConsecutiveFailures} consecutive failures: {Detail}",
                _consecutiveFailures,
                detail);
            return;
        }

        _notifications.Raise(
            NotificationKind,
            "Boomplay session recovery failed",
            $"Automatic Cloudflare recovery failed ({detail}); retrying after the cooldown. Boomplay fetching stays degraded until it succeeds.",
            "Warning",
            dedupeKey: ChallengeDedupeKey);
        _logger.LogWarning(
            "Boomplay session recovery attempt {Attempt} failed: {Detail}",
            _consecutiveFailures,
            detail);
    }
}

internal static class BoomplaySessionCookieMerger
{
    /// <summary>
    /// Merges a harvested cookie pair into the stored cookie header: existing pairs are kept in
    /// order, any stale pair with the harvested name is dropped, and the harvested pair is
    /// appended last.
    /// </summary>
    public static string? Merge(string? existingCookieHeader, string harvestedCookiePair)
    {
        if (string.IsNullOrWhiteSpace(harvestedCookiePair))
        {
            return existingCookieHeader;
        }

        var harvested = SplitPair(harvestedCookiePair);
        if (harvested.Name is null || string.IsNullOrWhiteSpace(harvested.Value))
        {
            return existingCookieHeader;
        }

        var pairs = new List<(string Name, string Value)>();
        foreach (var rawPair in (existingCookieHeader ?? string.Empty).Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = SplitPair(rawPair);
            if (pair.Name is null || string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            if (string.Equals(pair.Name, harvested.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            pairs.Add((pair.Name, pair.Value));
        }

        pairs.Add((harvested.Name, harvested.Value));
        return string.Join("; ", pairs.Select(pair => $"{pair.Name}={pair.Value}"));
    }

    private static (string? Name, string? Value) SplitPair(string rawPair)
    {
        var separatorIndex = rawPair.IndexOf('=');
        if (separatorIndex <= 0)
        {
            return (null, null);
        }

        var name = rawPair[..separatorIndex].Trim();
        var value = rawPair[(separatorIndex + 1)..].Trim();
        return name.Length == 0 ? (null, null) : (name, value);
    }
}
