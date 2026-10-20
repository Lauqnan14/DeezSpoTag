using System.Net;

namespace DeezSpoTag.Integrations;

public enum TargetLookupStatus
{
    Success,
    NotFound,
    Transient
}

public sealed record TargetPlaylistLookup<T>(
    TargetLookupStatus Status,
    T? Value,
    int? HttpStatusCode)
{
    public static TargetPlaylistLookup<T> Found(T value, int? httpStatusCode = 200)
        => new(TargetLookupStatus.Success, value, httpStatusCode);

    public static TargetPlaylistLookup<T> Missing(int? httpStatusCode = 404)
        => new(TargetLookupStatus.NotFound, default, httpStatusCode);

    public static TargetPlaylistLookup<T> Unavailable(int? httpStatusCode = null)
        => new(TargetLookupStatus.Transient, default, httpStatusCode);
}

public static class TargetLookupClassifier
{
    public static TargetLookupStatus FromHttpStatus(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        if (statusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return TargetLookupStatus.NotFound;
        }

        if (statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || code >= 500)
        {
            return TargetLookupStatus.Transient;
        }

        if (code >= 200 && code < 300)
        {
            return TargetLookupStatus.Success;
        }

        return TargetLookupStatus.Transient;
    }

    public static bool IsTransientTransport(Exception exception, CancellationToken cancellationToken)
        => exception is HttpRequestException
           || exception is TimeoutException
           || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);
}

/// <summary>
/// Thrown by clients when a caller required a provably successful response and the server returned
/// something that cannot be trusted as evidence: failed HTTP, a malformed payload or a server
/// application error. The caller classifies the affected artists as unknown instead of reading the
/// failure as absence or as a complete count.
/// </summary>
public sealed class TargetLookupException : System.IO.IOException
{
    public TargetLookupException(string message, Exception? innerException = null,
        string? target = null, IReadOnlyDictionary<long, string>? unresolvedArtists = null)
        : base(message, innerException)
    {
        Target = target;
        UnresolvedArtists = unresolvedArtists;
    }

    public string? Target { get; }
    public IReadOnlyDictionary<long, string>? UnresolvedArtists { get; }
}
