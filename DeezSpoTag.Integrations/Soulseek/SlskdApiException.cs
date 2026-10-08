namespace DeezSpoTag.Integrations.Soulseek;

/// <summary>
///     Raised when <c>slskd</c> answers with a failure status.
/// </summary>
/// <remarks>
///     Carries the HTTP status and slskd's response body so callers can distinguish "unauthorized",
///     "not found" and "slskd is down" without re-reading the response. The body is included for
///     diagnostics only; the API key is never part of a request URL or body, so it cannot leak here.
/// </remarks>
public sealed class SlskdApiException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SlskdApiException"/> class.</summary>
    public SlskdApiException(int statusCode, string? responseBody, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    /// <summary>Initializes a new instance of the <see cref="SlskdApiException"/> class with an inner cause.</summary>
    public SlskdApiException(int statusCode, string? responseBody, string message, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    /// <summary>Gets the HTTP status code returned by slskd.</summary>
    public int StatusCode { get; }

    /// <summary>Gets the raw response body, truncated. May be empty.</summary>
    public string? ResponseBody { get; }

    /// <summary>Gets a value indicating whether slskd rejected the supplied API key.</summary>
    public bool IsUnauthorized => StatusCode is 401 or 403;

    /// <summary>Gets a value indicating whether slskd reported the target as missing.</summary>
    public bool IsNotFound => StatusCode == 404;
}
