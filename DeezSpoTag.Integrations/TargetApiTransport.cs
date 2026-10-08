using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeezSpoTag.Integrations;

/// <summary>The shape of one destination-side HTTP result, before it is turned into a target outcome.</summary>
public sealed record TargetApiResponse(
    bool Success,
    HttpStatusCode StatusCode,
    string Body,
    string? TransportError)
{
    public int Status => (int)StatusCode;

    /// <summary>
    /// Whether the request reached the provider and was answered. A 404 is a real answer; a
    /// transport failure is not, and must never be read as an empty playlist.
    /// </summary>
    public bool Reached => TransportError is null && Success;

    public bool IsAuthRejection => Status is 401 or 403;

    public bool IsThrottled => Status == 429;

    /// <summary>5xx and 429 are the two cases worth retrying; everything else is a decision.</summary>
    public bool IsTransientFailure
        => TransportError is not null || Status == 429 || Status >= 500;

    /// <summary>
    /// A request that never produced an answer. Status is 0 so it is distinguishable from any
    /// real HTTP status, and <see cref="Reached"/> is false.
    /// </summary>
    public static TargetApiResponse Fail(string transportError)
        => new(false, 0, string.Empty, transportError);
}

/// <summary>
/// The one place a platform target talks HTTP.
/// <para>
/// Every platform adapter needs the same four calls, and each of them has the same failure
/// taxonomy: an auth rejection must stop the pass rather than look like an empty playlist, a 429
/// or 5xx is worth another attempt, and a transport failure is never an answer at all. Centralising
/// that here is what keeps a new adapter from inventing its own version of the removal safety model
/// by accident.
/// </para>
/// <para>
/// Rate limiting is handled per instance because the provider's limit is per account, not per
/// request. <see cref="MinimumInterval"/> is enforced between calls so a large playlist cannot
/// trip a throttle, which is also what keeps date-added order intact on the providers that only
/// preserve it when each track is appended by its own request.
/// </para>
/// </summary>
public sealed class TargetApiTransport
{
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _paceGate = new(1, 1);
    private DateTimeOffset _lastRequestUtc = DateTimeOffset.MinValue;

    public TargetApiTransport(HttpClient httpClient, TimeSpan? minimumInterval = null)
    {
        _httpClient = httpClient;
        MinimumInterval = minimumInterval ?? TimeSpan.FromMilliseconds(300);
    }

    /// <summary>Shortest permitted gap between two calls to this provider.</summary>
    public TimeSpan MinimumInterval { get; }

    /// <summary>Retries a throttled or 5xx response before giving up.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// When true, no minimum interval is enforced between calls. Only tests set this, where the
    /// delay would dominate the runtime and pacing is not what is under test.
    /// </summary>
    public bool SkipPacing { get; init; }

    public async Task<TargetApiResponse> SendAsync(
        HttpMethod method,
        string url,
        Action<HttpRequestMessage>? buildRequest = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        TargetApiResponse? last = null;

        for (var attempt = 1; attempt <= Math.Max(1, MaxAttempts); attempt++)
        {
            await PaceAsync(cancellationToken);
            last = await SendOnceAsync(method, url, buildRequest, headers, cancellationToken);
            if (!last.IsTransientFailure || attempt == MaxAttempts)
            {
                return last;
            }

            // Honour Retry-After when the provider states one, otherwise back off. The delay is
            // what keeps a throttled provider from being hammered into a longer ban.
            var backoff = last.IsThrottled
                ? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1))
                : TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1));
            await Task.Delay(backoff, cancellationToken);
        }

        return last ?? TargetApiResponse.Fail("no request was attempted");
    }

    public Task<TargetApiResponse> GetAsync(string url, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, url, null, headers, cancellationToken);

    /// <summary>
    /// DELETEs a resource, optionally with a JSON body. A body is required by JSON:API-style
    /// endpoints such as TIDAL's relationship removal, where the entries being unlinked are named in
    /// the payload rather than in the URL.
    /// </summary>
    public Task<TargetApiResponse> DeleteAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default,
        JsonElement? jsonBody = null)
        => SendAsync(
            HttpMethod.Delete,
            url,
            jsonBody is null
                ? null
                : request =>
                {
                    request.Content = new StringContent(
                        jsonBody.Value.GetRawText(), Encoding.UTF8, "application/json");
                },
            headers,
            cancellationToken);

    /// <summary>POSTs a JSON body, or form fields when <paramref name="formFields"/> is supplied.</summary>
    public Task<TargetApiResponse> PostAsync(
        string url,
        JsonElement? jsonBody = null,
        IReadOnlyDictionary<string, string>? formFields = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
        => SendAsync(
            HttpMethod.Post,
            url,
            request =>
            {
                if (formFields is not null)
                {
                    request.Content = new FormUrlEncodedContent(formFields);
                }
                else if (jsonBody.HasValue)
                {
                    request.Content = new StringContent(
                        jsonBody.Value.GetRawText(), Encoding.UTF8, "application/json");
                }
            },
            headers,
            cancellationToken);

    /// <summary>PUTs a JSON body.</summary>
    public Task<TargetApiResponse> PutAsync(
        string url,
        JsonElement? jsonBody = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
        => SendAsync(
            HttpMethod.Put,
            url,
            request =>
            {
                if (jsonBody.HasValue)
                {
                    request.Content = new StringContent(
                        jsonBody.Value.GetRawText(), Encoding.UTF8, "application/json");
                }
            },
            headers,
            cancellationToken);

    private async Task<TargetApiResponse> SendOnceAsync(
        HttpMethod method,
        string url,
        Action<HttpRequestMessage>? buildRequest,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (buildRequest is not null)
            {
                buildRequest(request);
            }

            if (headers is not null)
            {
                foreach (var header in headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken);
            return new TargetApiResponse(response.IsSuccessStatusCode, response.StatusCode, body, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            // A transport failure is not an answer. Callers must be able to tell it apart from an
            // empty playlist, or a mirror pass would treat "unreachable" as "no tracks" and delete
            // the destination's contents.
            return TargetApiResponse.Fail(ex.Message);
        }
    }

    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        if (SkipPacing || MinimumInterval <= TimeSpan.Zero)
        {
            return;
        }

        await _paceGate.WaitAsync(cancellationToken);
        try
        {
            var since = DateTimeOffset.UtcNow - _lastRequestUtc;
            if (since < MinimumInterval)
            {
                await Task.Delay(MinimumInterval - since, cancellationToken);
            }

            _lastRequestUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _paceGate.Release();
        }
    }
}

internal static class TargetApiResponseExtensions
{
    /// <summary>Parses the body as JSON, or returns null when the provider answered with something else.</summary>
    public static JsonElement? TryParseJson(this TargetApiResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.Body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a string at a dotted path, e.g. "data.0.id". Returns null rather than throwing when
    /// the provider returned a different shape, because a shape change is a normal failure here and
    /// must surface as "could not read", not as an exception.
    /// </summary>
    public static string? ReadString(this JsonElement element, string path)
    {
        if (TryWalk(element, path, out var found))
        {
            return found.ValueKind switch
            {
                JsonValueKind.String => found.GetString(),
                JsonValueKind.Number => found.GetRawText(),
                _ => null,
            };
        }

        return null;
    }

    public static int ReadInt(this JsonElement element, string path)
        => TryWalk(element, path, out var found) && found.ValueKind == JsonValueKind.Number
            ? found.GetInt32()
            : 0;

    public static bool ReadBool(this JsonElement element, string path)
        => TryWalk(element, path, out var found) && found.ValueKind is JsonValueKind.True;

    public static IReadOnlyList<JsonElement> ReadArray(this JsonElement element, string path)
        => TryWalk(element, path, out var found) && found.ValueKind == JsonValueKind.Array
            ? found.EnumerateArray().ToList()
            : Array.Empty<JsonElement>();

    /// <summary>Reads a nested object, or null when the path is absent or is not an object.</summary>
    public static JsonElement? ReadObject(this JsonElement element, string path)
        => TryWalk(element, path, out var found) && found.ValueKind == JsonValueKind.Object
            ? found
            : null;

    private static bool TryWalk(JsonElement element, string path, out JsonElement found)
    {
        found = element;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (found.ValueKind == JsonValueKind.Object
                && found.TryGetProperty(segment, out var next))
            {
                found = next;
                continue;
            }

            if (found.ValueKind == JsonValueKind.Array
                && int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index >= 0
                && index < found.GetArrayLength())
            {
                found = found[index];
                continue;
            }

            found = element;
            return false;
        }

        return found.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
    }
}
