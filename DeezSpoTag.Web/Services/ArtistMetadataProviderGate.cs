using System.Net;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Provider pacing for artist metadata: one in-flight request per provider
/// and minimum spacing. HTTP 429 puts the provider into a time-based backoff window
/// instead of disabling it for the rest of the run, so later artists still reach the
/// platform once the window expires. An empty or failed lookup is "no metadata for
/// this artist", not a run-wide outage.
/// </summary>
public sealed class ArtistMetadataProviderGate
{
    public const string UnavailableMessage = "skipped; provider rate limited (backing off)";
    internal static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(90);

    /// <summary>How long a provider rests after a rate-limit hit before it is retried.</summary>
    internal static readonly TimeSpan RateLimitBackoff = TimeSpan.FromMinutes(10);

    private readonly ILogger _logger;
    private readonly Func<string, TimeSpan> _minInterval;
    private readonly TimeSpan _operationTimeout;
    private readonly Dictionary<string, SemaphoreSlim> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastRequestUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _rateLimitedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public ArtistMetadataProviderGate(
        ILogger logger,
        Func<string, TimeSpan>? minInterval = null,
        TimeSpan? operationTimeout = null)
    {
        _logger = logger;
        _minInterval = minInterval ?? DefaultMinInterval;
        _operationTimeout = operationTimeout ?? DefaultOperationTimeout;
        if (_operationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }
    }

    public bool IsUnavailable(string provider)
    {
        lock (_sync)
        {
            return _rateLimitedUntil.TryGetValue(provider, out var until)
                && until > DateTimeOffset.UtcNow;
        }
    }

    public async Task<T?> RunAsync<T>(
        string provider,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(work);

        if (IsUnavailable(provider))
        {
            return default;
        }

        var inflight = GetInflight(provider);
        await inflight.WaitAsync(cancellationToken);
        CancellationTokenSource? operationCancellation = null;
        Task<T>? operation = null;
        try
        {
            if (IsUnavailable(provider))
            {
                return default;
            }

            await WaitSpacingAsync(provider, cancellationToken);
            operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                operation = work(operationCancellation.Token);
                var result = await operation.WaitAsync(_operationTimeout, cancellationToken);
                RecordRequest(provider);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Deliver cancellation before returning. Keep the provider slot and cancellation
                // source alive until the underlying task has finished unwinding.
                operationCancellation.Cancel();
                RecordRequest(provider);
                _logger.LogWarning(
                    "Artist metadata provider {Provider} timed out after {TimeoutSeconds} seconds.",
                    provider,
                    _operationTimeout.TotalSeconds);
                return default;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && IsRateLimited(ex))
            {
                RecordRequest(provider);
                MarkUnavailable(provider, "rate limited");
                return default;
            }
        }
        finally
        {
            if (operation is { IsCompleted: false } && operationCancellation is not null)
            {
                // Return promptly on cancellation/timeout, but keep this provider occupied
                // until its actual work exits. Other providers retain independent slots.
                _ = ReleaseAfterCompletionAsync(operation, inflight, operationCancellation, provider);
            }
            else
            {
                // Cancellation can win WaitAsync just as the provider faults.
                if (operation?.IsFaulted == true) _ = operation.Exception;
                operationCancellation?.Dispose();
                inflight.Release();
            }
        }
    }

    private async Task ReleaseAfterCompletionAsync(
        Task operation, SemaphoreSlim inflight, CancellationTokenSource cancellation, string provider)
    {
        try { await operation; }
        catch (Exception) { /* Observe abandoned provider failure; the caller already completed. */ }
        finally
        {
            RecordRequest(provider);
            cancellation.Dispose();
            inflight.Release();
        }
    }

    public static void ThrowIfRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException(
                "Provider rate limited (429).",
                inner: null,
                statusCode: HttpStatusCode.TooManyRequests);
        }
    }

    public static bool IsRateLimited(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
            {
                return true;
            }

            var message = current.Message;
            if (message.Contains("429", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static TimeSpan DefaultMinInterval(string provider)
        => provider.Trim().ToLowerInvariant() switch
        {
            "deezer" or "apple" or "itunes" => TimeSpan.FromMilliseconds(200),
            "lastfm" => TimeSpan.FromMilliseconds(250),
            _ => TimeSpan.FromMilliseconds(250)
        };

    private SemaphoreSlim GetInflight(string provider)
    {
        lock (_sync)
        {
            if (!_inflight.TryGetValue(provider, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _inflight[provider] = gate;
            }

            return gate;
        }
    }

    private async Task WaitSpacingAsync(string provider, CancellationToken cancellationToken)
    {
        TimeSpan remaining;
        lock (_sync)
        {
            if (!_lastRequestUtc.TryGetValue(provider, out var lastRequestUtc))
            {
                return;
            }

            remaining = _minInterval(provider) - (DateTimeOffset.UtcNow - lastRequestUtc);
        }

        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }

    private void RecordRequest(string provider)
    {
        lock (_sync)
        {
            _lastRequestUtc[provider] = DateTimeOffset.UtcNow;
        }
    }

    private void MarkUnavailable(string provider, string reason)
    {
        DateTimeOffset until;
        lock (_sync)
        {
            until = DateTimeOffset.UtcNow + RateLimitBackoff;
            if (_rateLimitedUntil.TryGetValue(provider, out var existing)
                && existing > DateTimeOffset.UtcNow
                && existing <= until)
            {
                return;
            }

            _rateLimitedUntil[provider] = until;
        }

        _logger.LogWarning(
            "Artist metadata provider {Provider} is backing off until {Until} ({Reason}).",
            provider,
            until.ToString("HH:mm:ss"),
            reason);
    }
}
