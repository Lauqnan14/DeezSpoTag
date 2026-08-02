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

    public async Task<ZarzSignedSession> EnsureSessionAsync(
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

    [JsonIgnore]
    public bool HasCredentials => !string.IsNullOrWhiteSpace(SessionId) && !string.IsNullOrWhiteSpace(SessionSecret);

    [JsonIgnore]
    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTimeOffset.UtcNow.AddMinutes(1);

    [JsonIgnore]
    public bool IsBlocked => BlockedGeneration.HasValue && BlockedGeneration.Value == Generation;

    [JsonIgnore]
    public bool IsUsable => HasCredentials && !IsExpired && !IsBlocked;

    public ZarzSignedSession Copy() => (ZarzSignedSession)MemberwiseClone();
}

public sealed record ZarzSessionBootstrapResult(ZarzSignedSession Session, string? VerificationUrl);

public sealed record ZarzSessionStateChangedEventArgs(string Provider, bool IsUsable, bool VerificationRequired);

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
