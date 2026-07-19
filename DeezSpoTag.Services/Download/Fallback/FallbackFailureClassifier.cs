using System.Net.Http;

namespace DeezSpoTag.Services.Download.Fallback;

internal static class FallbackFailureClassifier
{
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

    public static string Classify(Exception exception)
    {
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
