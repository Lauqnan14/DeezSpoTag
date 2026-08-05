using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Shared;

public sealed class ZarzSignedSessionCoordinator
{
    private static readonly TimeSpan PendingChallengeLifetime = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ILogger<ZarzSignedSessionCoordinator> _logger;
    private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gatesLock = new();

    public ZarzSignedSessionCoordinator(ILogger<ZarzSignedSessionCoordinator> logger)
    {
        _logger = logger;
    }

    public event EventHandler<ZarzSessionStateChangedEventArgs>? StateChanged;

    public async Task<bool> HasUsableSessionAsync(string provider, CancellationToken cancellationToken)
    {
        var gate = GetGate(provider);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return (await LoadNoLockAsync(provider, cancellationToken))?.IsUsable == true;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<ZarzSignedSession> EnsureSessionAsync(
        string provider,
        Func<ZarzSignedSession?, CancellationToken, Task<ZarzSessionBootstrapResult>> bootstrap,
        CancellationToken cancellationToken)
        => EnsureSessionAsync(provider, bootstrap, refresh: null, cancellationToken);

    public async Task<ZarzSignedSession> EnsureSessionAsync(
        string provider,
        Func<ZarzSignedSession?, CancellationToken, Task<ZarzSessionBootstrapResult>> bootstrap,
        Func<ZarzSignedSession, CancellationToken, Task<ZarzSignedSession>>? refresh,
        CancellationToken cancellationToken)
    {
        var gate = GetGate(provider);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadNoLockAsync(provider, cancellationToken);
            if (current?.IsUsable == true)
            {
                if (refresh is not null && current.NeedsRefresh)
                {
                    try
                    {
                        var refreshed = await refresh(current.Copy(), cancellationToken);
                        if (refreshed.IsUsable)
                        {
                            refreshed.InstallId = string.IsNullOrWhiteSpace(refreshed.InstallId)
                                ? current.InstallId
                                : refreshed.InstallId;
                            refreshed.Generation = current.Generation;
                            refreshed.BlockedGeneration = null;
                            refreshed.VerificationUrl = null;
                            refreshed.ChallengeCreatedAtUtc = null;
                            await SaveAndPublishNoLockAsync(provider, current, refreshed, cancellationToken);
                            return refreshed.Copy();
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(
                            ex,
                            "{Provider} signed-session refresh failed; continuing with the current usable session.",
                            provider);
                    }
                }

                return current.Copy();
            }

            if (current?.IsBlocked == true && HasFreshChallenge(current))
            {
                throw VerificationRequired(provider);
            }

            var result = await bootstrap(current?.Copy(), cancellationToken);
            var updated = NormalizeReplacement(current, result.Session);
            ApplyChallenge(updated, result.VerificationUrl);
            await SaveAndPublishNoLockAsync(provider, current, updated, cancellationToken);
            if (updated.IsUsable)
            {
                return updated.Copy();
            }

            throw VerificationRequired(provider);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string?> BeginVerificationAsync(
        string provider,
        Func<ZarzSignedSession?, CancellationToken, Task<ZarzSessionBootstrapResult>> bootstrap,
        CancellationToken cancellationToken)
    {
        var gate = GetGate(provider);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadNoLockAsync(provider, cancellationToken);
            if (current?.IsUsable == true)
            {
                return null;
            }

            if (HasFreshChallenge(current))
            {
                return current!.VerificationUrl;
            }

            var result = await bootstrap(current?.Copy(), cancellationToken);
            var updated = NormalizeReplacement(current, result.Session);
            ApplyChallenge(updated, result.VerificationUrl);
            await SaveAndPublishNoLockAsync(provider, current, updated, cancellationToken);
            return updated.IsUsable ? null : updated.VerificationUrl;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CompleteVerificationAsync(
        string provider,
        string grant,
        Func<ZarzSignedSession, string, CancellationToken, Task<ZarzSignedSession>> exchange,
        CancellationToken cancellationToken)
    {
        var gate = GetGate(provider);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadNoLockAsync(provider, cancellationToken)
                ?? throw new InvalidOperationException($"Start {DisplayName(provider)} public download verification first.");
            var grantHash = ZarzSignedSessionContract.HashGrant(grant);
            if (!string.IsNullOrWhiteSpace(current.CompletedGrantHash)
                && string.Equals(current.CompletedGrantHash, grantHash, StringComparison.Ordinal)
                && current.IsUsable)
            {
                current.BlockedGeneration = null;
                current.VerificationUrl = null;
                current.ChallengeCreatedAtUtc = null;
                await SaveAndPublishNoLockAsync(provider, current, current, cancellationToken);
                return;
            }

            var exchanged = await exchange(current.Copy(), grant, cancellationToken);
            if (!exchanged.HasCredentials || exchanged.IsExpired)
            {
                throw new InvalidOperationException($"{DisplayName(provider)} session exchange did not return a usable session.");
            }

            exchanged.InstallId = string.IsNullOrWhiteSpace(exchanged.InstallId) ? current.InstallId : exchanged.InstallId;
            exchanged.Generation = Math.Max(current.Generation + 1, exchanged.Generation);
            exchanged.BlockedGeneration = null;
            exchanged.VerificationUrl = null;
            exchanged.ChallengeCreatedAtUtc = null;
            exchanged.CompletedGrantHash = grantHash;
            await SaveAndPublishNoLockAsync(provider, current, exchanged, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ZarzResponseDisposition> ProcessResponseAsync(
        string provider,
        ZarzSignedSession requestSession,
        HttpStatusCode statusCode,
        string responseBody,
        CancellationToken cancellationToken)
    {
        var contract = ParseContract(responseBody);
        var disposition = Classify(statusCode, contract);
        if (disposition is not (ZarzResponseDisposition.SessionInvalid
            or ZarzResponseDisposition.VerificationRequired
            or ZarzResponseDisposition.RequestAuthenticationInvalid))
        {
            return disposition;
        }

        var gate = GetGate(provider);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadNoLockAsync(provider, cancellationToken);
            if (current is null || !SameGeneration(current, requestSession))
            {
                return ZarzResponseDisposition.RetryWithCurrentSession;
            }

            if (disposition == ZarzResponseDisposition.RequestAuthenticationInvalid)
            {
                return disposition;
            }

            var previous = current.Copy();
            if (disposition == ZarzResponseDisposition.SessionInvalid)
            {
                current.SessionId = string.Empty;
                current.SessionSecret = string.Empty;
                current.ExpiresAt = null;
                current.BlockedGeneration = null;
                current.VerificationUrl = null;
                current.ChallengeCreatedAtUtc = null;
            }
            else
            {
                current.BlockedGeneration = current.Generation;
            }

            await SaveAndPublishNoLockAsync(provider, previous, current, cancellationToken);
            return disposition;
        }
        finally
        {
            gate.Release();
        }
    }

    internal static ZarzResponseDisposition Classify(HttpStatusCode statusCode, ZarzErrorContract contract)
    {
        if (contract.Origin != "gateway")
        {
            return ZarzResponseDisposition.None;
        }

        if (statusCode == HttpStatusCode.Unauthorized
            && contract.Code == "SESSION_INVALID"
            && contract.Action == "bootstrap_session")
        {
            return ZarzResponseDisposition.SessionInvalid;
        }

        if (statusCode == HttpStatusCode.PreconditionRequired
            && contract.Code == "VERIFY_REQUIRED"
            && contract.Action == "verify")
        {
            return ZarzResponseDisposition.VerificationRequired;
        }

        if (statusCode == HttpStatusCode.Forbidden
            && contract.Code == "REQUEST_AUTH_INVALID"
            && string.IsNullOrWhiteSpace(contract.Action))
        {
            return ZarzResponseDisposition.RequestAuthenticationInvalid;
        }

        return ZarzResponseDisposition.None;
    }

    private static ZarzErrorContract ParseContract(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return new();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ZarzErrorContract>(responseBody, JsonOptions) ?? new();
            parsed.Code = parsed.Code.Trim().ToUpperInvariant();
            parsed.Origin = parsed.Origin.Trim().ToLowerInvariant();
            parsed.Action = parsed.Action.Trim().ToLowerInvariant();
            parsed.RetryMode = parsed.RetryMode.Trim().ToLowerInvariant();
            return parsed;
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private async Task<ZarzSignedSession?> LoadNoLockAsync(string provider, CancellationToken cancellationToken)
    {
        var path = ResolvePath(provider);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var record = JsonSerializer.Deserialize<ZarzSignedSession>(json, JsonOptions);
            if (record is not null && string.IsNullOrWhiteSpace(record.InstallId))
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                record.InstallId = ReadString(root, "InstallId", "install_id");
                record.SessionId = ReadString(root, "SessionId", "session_id");
                record.SessionSecret = ReadString(root, "SessionSecret", "session_secret");
                record.ExpiresAt = ReadDateTimeOffset(root, "ExpiresAt", "expires_at");
            }
            if (record is not null && record.Generation <= 0)
            {
                record.Generation = 1;
            }
            return record;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to load {Provider} Zarz signed session.", provider);
            return null;
        }
    }

    private static string ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString()?.Trim() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement root, params string[] names)
    {
        var value = ReadString(root, names);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private async Task SaveAndPublishNoLockAsync(
        string provider,
        ZarzSignedSession? previous,
        ZarzSignedSession current,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath(provider);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? DeezSpoTagDataRootResolver.Resolve());
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(current, JsonOptions), cancellationToken);
        File.Move(temporaryPath, path, true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var wasUsable = previous?.IsUsable == true;
        if (wasUsable != current.IsUsable || previous?.IsBlocked != current.IsBlocked)
        {
            StateChanged?.Invoke(this, new(provider, current.IsUsable, current.IsBlocked));
        }
    }

    private static ZarzSignedSession NormalizeReplacement(ZarzSignedSession? current, ZarzSignedSession replacement)
    {
        replacement.InstallId = string.IsNullOrWhiteSpace(replacement.InstallId)
            ? current?.InstallId ?? string.Empty
            : replacement.InstallId;
        replacement.Generation = replacement.HasCredentials
            ? Math.Max((current?.Generation ?? 0) + (SameCredentials(current, replacement) ? 0 : 1), 1)
            : Math.Max(current?.Generation ?? 1, 1);
        if (replacement.HasCredentials && !replacement.IsExpired)
        {
            replacement.BlockedGeneration = null;
        }
        return replacement;
    }

    private static void ApplyChallenge(ZarzSignedSession session, string? verificationUrl)
    {
        if (string.IsNullOrWhiteSpace(verificationUrl))
        {
            session.VerificationUrl = null;
            session.ChallengeCreatedAtUtc = null;
            return;
        }
        session.VerificationUrl = verificationUrl.Trim();
        session.ChallengeCreatedAtUtc = DateTimeOffset.UtcNow;
    }

    private static bool HasFreshChallenge(ZarzSignedSession? session)
        => session is not null
           && !string.IsNullOrWhiteSpace(session.VerificationUrl)
           && session.ChallengeCreatedAtUtc.HasValue
           && session.ChallengeCreatedAtUtc.Value > DateTimeOffset.UtcNow.Subtract(PendingChallengeLifetime);

    private SemaphoreSlim GetGate(string provider)
    {
        var normalized = NormalizeProvider(provider);
        lock (_gatesLock)
        {
            if (!_gates.TryGetValue(normalized, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _gates[normalized] = gate;
            }
            return gate;
        }
    }

    private static string ResolvePath(string provider)
        => Path.Join(DeezSpoTagDataRootResolver.Resolve(), NormalizeProvider(provider), "zarz-signed-session.json");

    private static string NormalizeProvider(string provider)
        => provider.Trim().ToLowerInvariant() switch
        {
            "amazon" => "amazon",
            "qobuz" => "qobuz",
            "tidal" => "tidal",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported Zarz session provider.")
        };

    private static string DisplayName(string provider)
        => NormalizeProvider(provider) switch
        {
            "amazon" => "Amazon",
            "qobuz" => "Qobuz",
            _ => "Tidal"
        };

    private static InvalidOperationException VerificationRequired(string provider)
        => new($"{DisplayName(provider)} public download verification is required.");

    private static bool SameGeneration(ZarzSignedSession left, ZarzSignedSession right)
        => left.Generation == right.Generation && SameCredentials(left, right);

    private static bool SameCredentials(ZarzSignedSession? left, ZarzSignedSession? right)
        => left is not null && right is not null
           && string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
           && string.Equals(left.SessionSecret, right.SessionSecret, StringComparison.Ordinal);
}

public sealed class ZarzSignedSession
{
    [JsonPropertyName("install_id")]
    public string InstallId { get; set; } = string.Empty;

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("session_secret")]
    public string SessionSecret { get; set; } = string.Empty;

    [JsonPropertyName("expires_at")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("generation")]
    public long Generation { get; set; } = 1;

    [JsonPropertyName("blocked_generation")]
    public long? BlockedGeneration { get; set; }

    [JsonPropertyName("verification_url")]
    public string? VerificationUrl { get; set; }

    [JsonPropertyName("challenge_created_at_utc")]
    public DateTimeOffset? ChallengeCreatedAtUtc { get; set; }

    [JsonPropertyName("completed_grant_hash")]
    public string? CompletedGrantHash { get; set; }

    [JsonIgnore]
    public bool HasCredentials => !string.IsNullOrWhiteSpace(SessionId) && !string.IsNullOrWhiteSpace(SessionSecret);

    [JsonIgnore]
    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTimeOffset.UtcNow.AddMinutes(1);

    [JsonIgnore]
    public bool IsBlocked => BlockedGeneration.HasValue && BlockedGeneration.Value == Generation;

    [JsonIgnore]
    public bool IsUsable => HasCredentials && !IsExpired && !IsBlocked;

    /// <summary>SpotiFLAC refreshes when expiry is within one hour.</summary>
    [JsonIgnore]
    public bool NeedsRefresh => IsUsable
        && ExpiresAt.HasValue
        && ExpiresAt.Value <= DateTimeOffset.UtcNow.Add(ZarzSignedSessionContract.RefreshSkew);

    public ZarzSignedSession Copy() => (ZarzSignedSession)MemberwiseClone();
}

public sealed record ZarzSessionBootstrapResult(ZarzSignedSession Session, string? VerificationUrl);

public sealed record ZarzSessionStateChangedEventArgs(string Provider, bool IsUsable, bool VerificationRequired);

public sealed class ZarzSessionRateLimitException : InvalidOperationException
{
    public ZarzSessionRateLimitException(string message, int? retryAfterSeconds = null)
        : base(message)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }

    public int? RetryAfterSeconds { get; }

    public static ZarzSessionRateLimitException? TryCreate(
        string operation,
        HttpStatusCode statusCode,
        string responseBody,
        HttpResponseMessage? response = null)
    {
        if (statusCode != HttpStatusCode.TooManyRequests)
        {
            return null;
        }

        var retryAfter = ZarzSignedSessionContract.ReadRetryAfterSeconds(response, responseBody);
        var suffix = retryAfter.HasValue ? $" Retry after {retryAfter.Value} seconds." : string.Empty;
        return new($"{operation} is temporarily rate limited.{suffix}", retryAfter);
    }
}

/// <summary>
/// Shared SpotiFLAC-compatible signed-session request contract (Zarz gateway).
/// </summary>
public static class ZarzSignedSessionContract
{
    public const string CallbackUrl = "spotiflac://session-grant";
    public const string Platform = "extension";
    public const string SchemeLabel = "ZARZ-HMAC-V1";
    public const string HeaderPrefix = "X-Zarz-";
    public const string RefreshPath = "/session/refresh";
    public const int TimeWindowSeconds = 300;
    public const int ExchangeMaxAttempts = 3;
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    public static string BuildBootstrapQuery(string installId, string appVersion)
        => string.Join(
            '&',
            new Dictionary<string, string>
            {
                ["app_version"] = appVersion,
                ["install_id"] = installId
            }.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    /// <summary>
    /// SpotiFLAC mobile deep-link callback (native intercept). Used when no public web base is available.
    /// </summary>
    public static string BuildSpotiflacGrantCallbackUrl(string state)
        => new UriBuilder(CallbackUrl)
        {
            Query = string.Join(
                '&',
                new Dictionary<string, string>
                {
                    ["cb_version"] = "v2grant",
                    ["state"] = state
                }.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
        }.Uri.ToString();

    /// <summary>
    /// Same-origin HTTPS callback for DeezSpoTag web. The verification popup returns here with
    /// <c>grant</c> so the opener can finish without a native <c>spotiflac://</c> handler.
    /// </summary>
    public static string BuildWebGrantCallbackUrl(string publicAppBaseUrl, string state)
    {
        var root = publicAppBaseUrl.Trim().TrimEnd('/');
        var callback = new UriBuilder($"{root}/api/public-download/session-grant")
        {
            Query = string.Join(
                '&',
                new Dictionary<string, string>
                {
                    ["cb_version"] = "v2grant",
                    ["state"] = state
                }.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
        };
        return callback.Uri.ToString();
    }

    public static string BuildChallengeUrl(
        string baseUrl,
        string challengePath,
        string challengeId,
        string state,
        string? publicAppBaseUrl = null)
    {
        var callback = string.IsNullOrWhiteSpace(publicAppBaseUrl)
            ? BuildSpotiflacGrantCallbackUrl(state)
            : BuildWebGrantCallbackUrl(publicAppBaseUrl, state);

        var challengeBase = new Uri(baseUrl.TrimEnd('/') + "/");
        var challengeUri = new Uri(challengeBase, challengePath.TrimStart('/'));
        var builder = new UriBuilder(challengeUri)
        {
            Query = string.Join(
                '&',
                new Dictionary<string, string>
                {
                    ["id"] = challengeId,
                    ["cb"] = callback
                }.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
        };
        return builder.Uri.ToString();
    }

    public static string ResolveVerificationUrl(
        string? authUrl,
        string? challengeUrl,
        string? challengeId,
        string baseUrl,
        string challengePath,
        string state,
        string? publicAppBaseUrl = null)
    {
        // Prefer challenge_id so we control the callback (web HTTPS vs spotiflac deep link).
        if (!string.IsNullOrWhiteSpace(challengeId))
        {
            return BuildChallengeUrl(baseUrl, challengePath, challengeId.Trim(), state, publicAppBaseUrl);
        }

        if (!string.IsNullOrWhiteSpace(authUrl))
        {
            return authUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(challengeUrl))
        {
            return challengeUrl.Trim();
        }

        return string.Empty;
    }

    public static string HashGrant(string grant)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(grant.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static int? ReadRetryAfterSeconds(HttpResponseMessage? response, string? responseBody)
    {
        if (response?.Headers.RetryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
        {
            return (int)Math.Ceiling(delta.TotalSeconds);
        }

        if (response?.Headers.RetryAfter?.Date is DateTimeOffset date)
        {
            var seconds = (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds);
            return seconds > 0 ? seconds : null;
        }

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("retry_after", out var retryAfter)
                && retryAfter.TryGetInt32(out var value)
                && value > 0)
            {
                return value;
            }

            if (document.RootElement.TryGetProperty("retry_after_seconds", out var retryAfterSeconds)
                && retryAfterSeconds.TryGetInt32(out var seconds)
                && seconds > 0)
            {
                return seconds;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static async Task DelayForRetryAsync(int? retryAfterSeconds, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, retryAfterSeconds ?? 1));
        if (delay > MaxRetryAfter)
        {
            delay = MaxRetryAfter;
        }

        await Task.Delay(delay, cancellationToken);
    }

    public static async Task<string> ExchangeGrantAsync(
        HttpClient client,
        Uri exchangeUri,
        string userAgent,
        string grant,
        string installId,
        string appVersion,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            grant,
            install_id = installId,
            app_version = appVersion,
            platform = Platform
        });
        var bodyBytes = System.Text.Encoding.UTF8.GetBytes(payload);
        string? lastBody = null;
        HttpStatusCode lastStatus = 0;
        for (var attempt = 1; attempt <= ExchangeMaxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, exchangeUri)
            {
                Content = new ByteArrayContent(bodyBytes)
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            using var response = await client.SendAsync(request, cancellationToken);
            lastBody = await response.Content.ReadAsStringAsync(cancellationToken);
            lastStatus = response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return lastBody;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < ExchangeMaxAttempts)
            {
                var retryAfter = ReadRetryAfterSeconds(response, lastBody);
                await DelayForRetryAsync(retryAfter, cancellationToken);
                continue;
            }

            var rateLimit = ZarzSessionRateLimitException.TryCreate(
                "session exchange",
                response.StatusCode,
                lastBody,
                response);
            if (rateLimit is not null)
            {
                throw rateLimit;
            }

            throw new InvalidOperationException(
                $"session exchange failed: HTTP {(int)response.StatusCode}: {lastBody.Trim()}");
        }

        throw new InvalidOperationException(
            $"session exchange failed: HTTP {(int)lastStatus}: {(lastBody ?? string.Empty).Trim()}");
    }
}

public enum ZarzResponseDisposition
{
    None,
    SessionInvalid,
    VerificationRequired,
    RequestAuthenticationInvalid,
    RetryWithCurrentSession
}

public sealed class ZarzErrorContract
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("origin")]
    public string Origin { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("retryable")]
    public bool Retryable { get; set; }

    [JsonPropertyName("retry_mode")]
    public string RetryMode { get; set; } = string.Empty;

    [JsonPropertyName("retry_after_seconds")]
    public int RetryAfterSeconds { get; set; }
}
