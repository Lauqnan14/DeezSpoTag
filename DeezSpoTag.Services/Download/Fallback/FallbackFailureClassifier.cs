using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Services.Download.Fallback;

public static class FallbackFailureClassifier
{
    public const string CatalogLookupFailed = "catalog_lookup_failed";
    public const string IdentityUnresolved = "identity_unresolved";
    public const string ProviderQualityUnknown = "provider_quality_unknown";
    public const string CatalogQualityBelowRequested = "catalog_quality_below_requested";
    public const string QualityBelowRequested = "quality_below_requested";
    public const string SameEngineBlocked = "same_engine_blocked";
    public const string Unresolved = "unresolved";
    public const string Unsupported = "unsupported";
    public const string Unavailable = "unavailable";
    public const string NotConfigured = "not_configured";
    public const string AuthenticationRequired = "authentication_required";
    public const string ProviderTimeout = "provider_timeout";
    public const string ProviderRateLimited = "provider_rate_limited";
    public const string ProviderVerificationRequired = "provider_verification_required";
    public const string ProviderManifestUnavailable = "provider_manifest_unavailable";
    public const string ProviderTransient = "provider_transient";
    public const string DownloadStreamFailed = "download_stream_failed";
    public const string DownloadFailed = "download_failed";

    public static bool IsTemporaryProviderClass(string? errorClass)
        => errorClass is ProviderTimeout or ProviderRateLimited or ProviderTransient
            or ProviderManifestUnavailable or CatalogLookupFailed or DownloadStreamFailed;

    public static bool IsTemporaryProviderFailure(Exception exception)
        => IsTemporaryProviderClass(Classify(exception))
           || (exception is AggregateException aggregate
               ? aggregate.InnerExceptions.Any(IsTemporaryProviderFailure)
               : exception.InnerException is { } inner && IsTemporaryProviderFailure(inner));

    public static bool IsPlanUnavailable(IReadOnlyList<FallbackPlanStep> plan, IReadOnlyList<FallbackAttempt> history)
    {
        if (plan.Count == 0 || history.Any(attempt => attempt is null)
            || plan.Any(step => step is null || string.IsNullOrWhiteSpace(step.StepId))
            || plan.Select(step => step.StepId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Count)
            return false;
        return plan.All(step =>
        {
            var outcome = history.LastOrDefault(attempt => string.Equals(attempt.StepId, step.StepId, StringComparison.OrdinalIgnoreCase));
            return outcome is not null && outcome.Status is "failed" or "skipped" or "rejected"
                && outcome.ErrorClass is Unavailable or QualityBelowRequested;
        });
    }

    public static void AttachProviderErrorContract(Exception exception, string body)
    {
        var contract = ReadProviderErrorContract(body);
        if (contract is not null) exception.Data["ZarzErrorContract"] = contract;
    }

    private static ZarzErrorContract? ReadProviderErrorContract(string message)
    {
        var start = message.IndexOf('{');
        var end = message.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(message[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("retryable", out var retryable)
                || retryable.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !doc.RootElement.TryGetProperty("retry_mode", out var mode) || mode.ValueKind != JsonValueKind.String)
                return null;
            return doc.RootElement.Deserialize<ZarzErrorContract>();
        }
        catch (JsonException) { return null; }
    }

    public static TimeSpan? MinimumRetryDelay(Exception exception)
    {
        var seconds = exception is ZarzSessionRateLimitException rate ? rate.RetryAfterSeconds ?? 0 : 0;
        var contract = exception.Data["ZarzErrorContract"] as ZarzErrorContract ?? ReadProviderErrorContract(exception.Message);
        seconds = Math.Max(seconds, contract?.RetryAfterSeconds ?? 0);
        var match = Regex.Match(exception.Message, @"retry[\s_-]*after(?:[\s_-]*seconds)?\s*[:=]?\s*(\d+)",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
        if (match.Success && int.TryParse(match.Groups[1].Value, out var advertised)) seconds = Math.Max(seconds, advertised);
        var children = exception is AggregateException aggregate ? aggregate.InnerExceptions.AsEnumerable()
            : exception.InnerException is { } inner ? new[] { inner } : Enumerable.Empty<Exception>();
        foreach (var child in children)
        {
            var delay = MinimumRetryDelay(child);
            if (delay.HasValue) seconds = Math.Max(seconds, (int)delay.Value.TotalSeconds);
        }
        // Same default cooldown used by the public provider registries when no server delay is supplied.
        return seconds > 0 ? TimeSpan.FromSeconds(seconds)
            : Classify(exception) == ProviderRateLimited ? TimeSpan.FromMinutes(15) : null;
    }

    public static bool IsRecoverableProviderFailure(Exception exception)
        => DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(exception)
           || (exception is AggregateException aggregate
               && aggregate.Flatten().InnerExceptions.Count > 0
               && aggregate.Flatten().InnerExceptions.All(
                   DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable));

    public static string Classify(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            var classes = aggregate.Flatten().InnerExceptions.Select(Classify).ToList();
            foreach (var failureClass in new[] { ProviderRateLimited, ProviderVerificationRequired, ProviderTimeout })
            {
                if (classes.Contains(failureClass)) return failureClass;
            }
            if (aggregate.Message.Contains("catalog lookup failed", StringComparison.OrdinalIgnoreCase))
            {
                return classes.Count > 0 && classes.All(static value => value == AuthenticationRequired)
                    ? AuthenticationRequired
                    : CatalogLookupFailed;
            }
            return classes.FirstOrDefault(static value => !IsTerminalErrorClass(value))
                ?? classes.FirstOrDefault() ?? DownloadFailed;
        }
        if (exception.InnerException is HttpRequestException httpFailure)
        {
            var cause = Classify(httpFailure);
            return exception.Message.Contains("catalog lookup failed", StringComparison.OrdinalIgnoreCase)
                && cause is ProviderTransient or DownloadFailed or ProviderManifestUnavailable
                    ? CatalogLookupFailed
                    : cause;
        }
        var message = exception.Message ?? string.Empty;
        var normalized = message.Trim().ToLowerInvariant();

        if (exception is TimeoutException
            || exception is TaskCanceledException
            || normalized.Contains("timed out", StringComparison.Ordinal)
            || normalized.Contains("timeout", StringComparison.Ordinal))
        {
            return ProviderTimeout;
        }

        if (normalized.Contains("429", StringComparison.Ordinal)
            || normalized.Contains("rate limit", StringComparison.Ordinal)
            || normalized.Contains("rate_limited", StringComparison.Ordinal)
            || normalized.Contains("rate-limit", StringComparison.Ordinal)
            || normalized.Contains("too many requests", StringComparison.Ordinal))
        {
            return ProviderRateLimited;
        }

        if (normalized.Contains("interactive challenge", StringComparison.Ordinal)
            || normalized.Contains("verification requires", StringComparison.Ordinal)
            || normalized.Contains("public download verification", StringComparison.Ordinal)
            || normalized.Contains("session bootstrap", StringComparison.Ordinal)
            || normalized.Contains("session exchange", StringComparison.Ordinal))
        {
            return ProviderVerificationRequired;
        }

        if (exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
            || normalized.Contains("http 401", StringComparison.Ordinal)
            || normalized.Contains("http 403", StringComparison.Ordinal)
            || normalized.Contains("authentication required", StringComparison.Ordinal)
            || normalized.Contains("metadata session could not be initialized", StringComparison.Ordinal))
        {
            return AuthenticationRequired;
        }

        var providerError = exception.Data["ZarzErrorContract"] as ZarzErrorContract ?? ReadProviderErrorContract(message);
        var absenceStatus = exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.UnprocessableEntity }
            || normalized.Contains("http 404", StringComparison.Ordinal) || normalized.Contains("http 410", StringComparison.Ordinal)
            || normalized.Contains("http 409", StringComparison.Ordinal) || normalized.Contains("http 422", StringComparison.Ordinal);
        if (absenceStatus && providerError is { Retryable: false, RetryMode: "none" })
        {
            if (providerError.Code == "TRACK_UNAVAILABLE") return Unavailable;
            if (providerError.Code == "QUALITY_UNAVAILABLE") return QualityBelowRequested;
        }

        if (normalized.Contains("public download session is invalid", StringComparison.Ordinal)
            || normalized.Contains("download verification session", StringComparison.Ordinal))
        {
            return ProviderVerificationRequired;
        }

        if (normalized.Contains("catalog lookup failed", StringComparison.Ordinal))
        {
            return CatalogLookupFailed;
        }

        if (normalized.Contains("identity_unresolved", StringComparison.Ordinal))
        {
            return IdentityUnresolved;
        }

        if (normalized.Contains("quality_below_requested", StringComparison.Ordinal))
        {
            return QualityBelowRequested;
        }

        if (normalized.Contains("provider_quality_unknown", StringComparison.Ordinal))
        {
            return ProviderQualityUnknown;
        }

        if (normalized.Contains("manifest", StringComparison.Ordinal)
            || normalized.Contains("empty response", StringComparison.Ordinal)
            || normalized.Contains("no data", StringComparison.Ordinal)
            || normalized.Contains("preview asset", StringComparison.Ordinal))
        {
            return ProviderManifestUnavailable;
        }

        if (exception is HttpRequestException
            || normalized.Contains("http 500", StringComparison.Ordinal)
            || normalized.Contains("http 502", StringComparison.Ordinal)
            || normalized.Contains("http 503", StringComparison.Ordinal)
            || normalized.Contains("http 504", StringComparison.Ordinal)
            || normalized.Contains("service unavailable", StringComparison.Ordinal)
            || normalized.Contains("upstream fetch failed", StringComparison.Ordinal)
            || normalized.Contains("provider failure", StringComparison.Ordinal)
            || normalized.Contains("provider failed", StringComparison.Ordinal)
            || normalized.Contains("provider unavailable", StringComparison.Ordinal)
            || normalized.Contains("provider is cooling down", StringComparison.Ordinal)
            || normalized.Contains("no download provider is currently available", StringComparison.Ordinal)
            || normalized.Contains("no qobuz public download provider is currently available", StringComparison.Ordinal))
        {
            return ProviderTransient;
        }

        if (normalized.Contains("stream failed", StringComparison.Ordinal)
            || normalized.Contains("download stream", StringComparison.Ordinal))
        {
            return DownloadStreamFailed;
        }

        if (normalized.Contains("not configured", StringComparison.Ordinal))
        {
            return NotConfigured;
        }

        if (normalized.Contains("authentication required", StringComparison.Ordinal)
            || normalized.Contains("not authenticated", StringComparison.Ordinal)
            || normalized.Contains("missing credentials", StringComparison.Ordinal))
        {
            return AuthenticationRequired;
        }

        return DownloadFailed;
    }

    public static bool IsTerminal(FallbackAttempt attempt)
    {
        if (string.Equals(attempt.Status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attempt.Status, "tagged", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsTerminalErrorClass(attempt.ErrorClass);
    }

    public static bool IsTerminalErrorClass(string? errorClass)
        => (errorClass ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            CatalogLookupFailed => false,
            IdentityUnresolved => true,
            ProviderQualityUnknown => false,
            CatalogQualityBelowRequested => true,
            QualityBelowRequested => true,
            SameEngineBlocked => true,
            Unresolved => true,
            Unsupported => true,
            Unavailable => true,
            NotConfigured => true,
            AuthenticationRequired => true,
            ProviderTimeout => false,
            ProviderRateLimited => false,
            ProviderVerificationRequired => false,
            ProviderManifestUnavailable => false,
            ProviderTransient => false,
            DownloadStreamFailed => false,
            DownloadFailed => false,
            "timeout" => false,
            _ => false
        };
}
