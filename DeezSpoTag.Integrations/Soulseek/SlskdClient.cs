using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeezSpoTag.Core.Models.Soulseek;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Integrations.Soulseek;

/// <summary>
///     A mechanical HTTP client over the <c>slskd</c> <c>/api/v0</c> API.
/// </summary>
/// <remarks>
///     <para>
///         Registered as a typed <see cref="HttpClient"/> client so that timeouts and handler configuration
///         come from DI rather than being hard-coded per call.
///     </para>
///     <para>
///         The API key is sent as the <c>X-API-Key</c> header only. It is never placed in a URL or a
///         request body, and it is never written to a log message or an exception message.
///     </para>
/// </remarks>
public sealed class SlskdClient : ISlskdClient
{
    private const string ApiKeyHeader = "X-API-Key";
    private const int MaxDiagnosticBodyLength = 512;

    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<SlskdClient> _logger;

    /// <summary>Initializes a new instance of the <see cref="SlskdClient"/> class.</summary>
    public SlskdClient(HttpClient httpClient, ILogger<SlskdClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SlskdServerState> GetServerStateAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
        => ParseServerState(await ReadElementAsync(credentials, HttpMethod.Get, "server", null, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<SlskdServerState> ConnectAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
        => ParseServerState(await ReadElementAsync(credentials, HttpMethod.Put, "server", null, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task DisconnectAsync(SlskdCredentials credentials, string? message = null, CancellationToken cancellationToken = default)
        => await SendAsync(
                credentials,
                HttpMethod.Delete,
                "server",
                message is null ? null : JsonSerializer.Serialize(message, RequestJsonOptions),
                cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<SlskdSearch> StartSearchAsync(SlskdCredentials credentials, SlskdSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = JsonSerializer.Serialize(request, RequestJsonOptions);
        return SlskdJson.ReadSearch(
            await ReadElementAsync(credentials, HttpMethod.Post, "searches", payload, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<SlskdSearch?> GetSearchAsync(SlskdCredentials credentials, Guid searchId, bool includeResponses = false, CancellationToken cancellationToken = default)
    {
        var path = includeResponses ? $"searches/{searchId}?includeResponses=true" : $"searches/{searchId}";
        var element = await TryReadElementAsync(credentials, HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        return element is null ? null : SlskdJson.ReadSearch(element.Value);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SlskdSearch>> ListSearchesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
    {
        var element = await TryReadElementAsync(credentials, HttpMethod.Get, "searches", null, cancellationToken).ConfigureAwait(false);
        if (element is null)
        {
            return [];
        }

        // slskd returns either a bare array or an id-keyed object.
        var entries = element.Value.ValueKind == JsonValueKind.Array
            ? element.Value.EnumerateArray().ToList()
            : SlskdJson.ReadArray(element.Value, "searches", "items");
        return entries.Select(SlskdJson.ReadSearch).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SlskdSearchResponse>> GetSearchResponsesAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
    {
        var element = await TryReadElementAsync(credentials, HttpMethod.Get, $"searches/{searchId}/responses", null, cancellationToken)
            .ConfigureAwait(false);
        if (element is null)
        {
            return [];
        }

        var entries = element.Value.ValueKind == JsonValueKind.Array
            ? element.Value.EnumerateArray().ToList()
            : SlskdJson.ReadArray(element.Value, "responses", "items");
        return entries.Select(SlskdJson.ReadSearchResponse).ToList();
    }

    /// <inheritdoc />
    public async Task CancelSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
        => await SendAsync(credentials, HttpMethod.Put, $"searches/{searchId}", null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> DeleteSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
        => await TrySendAsync(credentials, HttpMethod.Delete, $"searches/{searchId}", null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<SlskdUserStatus?> GetUserStatusAsync(SlskdCredentials credentials, string username, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var path = $"users/{Uri.EscapeDataString(username)}/info";
        var element = await TryReadElementAsync(credentials, HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        if (element is not { } value)
        {
            return null;
        }

        return new SlskdUserStatus
        {
            Username = username,
            IsOnline = SlskdJson.ReadBool(value, false, "isOnline", "online"),
            QueueLength = SlskdJson.ReadLong(value, 0, "queueLength"),
            UploadSpeed = SlskdJson.ReadLong(value, 0, "uploadSpeed"),
            FreeUploadSlots = SlskdJson.ReadNullableLong(value, "freeUploadSlots")
        };
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SlskdDirectory>> BrowseUserAsync(SlskdCredentials credentials, string username, CancellationToken cancellationToken = default)
        => BrowseUserDirectoryAsync(credentials, username, null, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SlskdDirectory>> BrowseUserDirectoryAsync(
        SlskdCredentials credentials,
        string username,
        string? directory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return [];
        }

        var escaped = Uri.EscapeDataString(username);
        IReadOnlyList<SlskdDirectory> directories;

        if (string.IsNullOrWhiteSpace(directory))
        {
            // The browse call lists the peer's share roots without a request body.
            var element = await TryReadElementAsync(credentials, HttpMethod.Get, $"users/{escaped}/browse", null, cancellationToken)
                .ConfigureAwait(false);
            directories = element is null ? [] : ParseDirectories(element.Value);
        }
        else
        {
            var payload = JsonSerializer.Serialize(new SlskdDirectoryRequest { Directory = directory }, RequestJsonOptions);
            var element = await TryReadElementAsync(credentials, HttpMethod.Post, $"users/{escaped}/directory", payload, cancellationToken)
                .ConfigureAwait(false);
            directories = element is null ? [] : ParseDirectories(element.Value);
        }

        return directories;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SlskdTransfer>> EnqueueDownloadsAsync(
        SlskdCredentials credentials,
        string username,
        IReadOnlyList<SlskdQueueDownload> downloads,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(downloads);
        if (string.IsNullOrWhiteSpace(username) || downloads.Count == 0)
        {
            return [];
        }

        var payload = JsonSerializer.Serialize(downloads, RequestJsonOptions);
        var path = $"transfers/downloads/{Uri.EscapeDataString(username)}";
        var element = await ReadElementAsync(credentials, HttpMethod.Post, path, payload, cancellationToken).ConfigureAwait(false);
        return ParseTransfers(element);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SlskdTransfer>> ListDownloadsAsync(
        SlskdCredentials credentials,
        bool includeRemoved = false,
        CancellationToken cancellationToken = default)
    {
        var path = includeRemoved ? "transfers/downloads?includeRemoved=true" : "transfers/downloads";
        var element = await TryReadElementAsync(credentials, HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        return element is null ? [] : ParseTransfers(element.Value);
    }

    /// <inheritdoc />
    public async Task<SlskdTransfer?> GetDownloadAsync(
        SlskdCredentials credentials,
        string username,
        Guid transferId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var path = $"transfers/downloads/{Uri.EscapeDataString(username)}/{transferId}";
        var element = await TryReadElementAsync(credentials, HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        return element is null ? null : SlskdJson.ReadTransfer(element.Value);
    }

    /// <inheritdoc />
    public async Task<int?> GetDownloadPositionAsync(
        SlskdCredentials credentials,
        string username,
        Guid transferId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var path = $"transfers/downloads/{Uri.EscapeDataString(username)}/{transferId}/position";
        var element = await TryReadElementAsync(credentials, HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        return element is null ? null : SlskdJson.ReadInt(element.Value, 0, "placeInQueue", "position", "value");
    }

    /// <inheritdoc />
    public async Task CancelDownloadAsync(
        SlskdCredentials credentials,
        string username,
        Guid transferId,
        bool remove = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        var path = remove
            ? $"transfers/downloads/{Uri.EscapeDataString(username)}/{transferId}?remove=true"
            : $"transfers/downloads/{Uri.EscapeDataString(username)}/{transferId}";
        await SendAsync(credentials, HttpMethod.Delete, path, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ClearCompletedDownloadsAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
        => await SendAsync(credentials, HttpMethod.Delete, "transfers/downloads/all/completed", null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SlskdShare>> ListSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
    {
        var element = await TryReadElementAsync(credentials, HttpMethod.Get, "shares", null, cancellationToken).ConfigureAwait(false);
        if (element is null)
        {
            return [];
        }

        // slskd returns shares as an id-keyed object.
        if (element.Value.ValueKind == JsonValueKind.Object)
        {
            return element.Value.EnumerateObject()
                .Select(property => SlskdJson.ReadShare(property.Value, property.Name))
                .ToList();
        }

        return SlskdJson.ReadArray(element.Value, "shares", "items").Select(value => SlskdJson.ReadShare(value)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SlskdDirectory>> BrowseSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
    {
        var element = await TryReadElementAsync(credentials, HttpMethod.Get, "shares/contents", null, cancellationToken).ConfigureAwait(false);
        return element is null ? [] : ParseDirectories(element.Value);
    }

    /// <inheritdoc />
    public async Task RescanSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
        => await SendAsync(credentials, HttpMethod.Put, "shares", null, cancellationToken).ConfigureAwait(false);

    private static SlskdServerState ParseServerState(JsonElement element) => new()
    {
        IsConnected = SlskdJson.ReadBool(element, false, "isConnected"),
        IsLoggedIn = SlskdJson.ReadBool(element, false, "isLoggedIn"),
        IsTransitioning = SlskdJson.ReadBool(element, false, "isTransitioning"),
        Username = SlskdJson.ReadString(element, "username", "login", "user"),
        RawState = SlskdJson.ReadString(element, "state")
    };

    private static IReadOnlyList<SlskdDirectory> ParseDirectories(JsonElement element)
    {
        var entries = element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().ToList()
            : SlskdJson.ReadArray(element, "directories", "items", "contents");
        return entries.Select(SlskdJson.ReadDirectory).ToList();
    }

    /// <summary>
    ///     The property names slskd has used to wrap a collection of transfers.
    /// </summary>
    /// <remarks>
    ///     "enqueued" is the batch answer; "files" is the grouped list answer, where a peer's files are
    ///     themselves transfers. "directories" and "groups" are the levels above that in the grouped shape.
    ///     "failed" is deliberately absent: an enqueue that failed produced no transfer, and reporting one would
    ///     make the engine watch something that does not exist.
    /// </remarks>
    private static readonly string[] TransferContainerNames =
        ["enqueued", "transfers", "downloads", "items", "files", "directories", "groups"];

    private static IReadOnlyList<SlskdTransfer> ParseTransfers(JsonElement element)
    {
        var parsed = new List<SlskdTransfer>();
        CollectTransfers(element, parsed, new HashSet<string>(StringComparer.OrdinalIgnoreCase), null);
        return parsed;
    }

    /// <summary>
    ///     Walks a response and collects every object that really is a transfer.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         slskd answers the same logical thing in several shapes: a bare transfer, an array of transfers,
    ///         a batch object with an "enqueued" list, and - for the list route - peers, each with directories,
    ///         each with files. The transfer is the object that carries an id and a filename; everything above it
    ///         is a container, and treating a container as a transfer is what produced records with no id and no
    ///         filename, and so no transfer to watch.
    ///     </para>
    ///     <para>
    ///         The peer name is inherited downwards because the grouped shape does not have to repeat it on
    ///         every file, and the transfer is identified downstream by peer plus path.
    ///     </para>
    /// </remarks>
    private static void CollectTransfers(
        JsonElement element,
        List<SlskdTransfer> into,
        HashSet<string> seenIds,
        string? inheritedUsername)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectTransfers(item, into, seenIds, inheritedUsername);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (IsTransfer(element))
        {
            var transfer = SlskdJson.ReadTransfer(element);
            if (string.IsNullOrWhiteSpace(transfer.Username) && !string.IsNullOrWhiteSpace(inheritedUsername))
            {
                transfer = transfer with { Username = inheritedUsername };
            }

            // The same transfer can appear twice in one answer; watching and recording it twice would
            // double-count the download.
            if (transfer.Id is { } id && !seenIds.Add(id.ToString("D")))
            {
                return;
            }

            into.Add(transfer);
            return;
        }

        var username = SlskdJson.ReadString(element, "username", "user");
        foreach (var container in TransferContainerNames
                     .Select(candidate => SlskdJson.Find(element, candidate))
                     .OfType<JsonElement>())
        {
            CollectTransfers(container, into, seenIds, username);
        }

        // An id-keyed object of transfers, which is how slskd serialises some collections.
        if (into.Count > 0)
        {
            return;
        }

        foreach (var property in element.EnumerateObject()
                     .Where(candidate => candidate.Value.ValueKind == JsonValueKind.Object))
        {
            CollectTransfers(property.Value, into, seenIds, username);
        }
    }

    /// <summary>
    ///     Whether an object is a transfer rather than a container or a group of them.
    /// </summary>
    /// <remarks>
    ///     An id and a filename are what every slskd transfer has and no container has. A peer group carries a
    ///     username and a directory group carries a directory, so neither can be mistaken for one.
    /// </remarks>
    private static bool IsTransfer(JsonElement element)
        => SlskdJson.Find(element, "id") is { } id
            && id.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(id.GetString())
            && SlskdJson.Find(element, "filename", "fileName") is { } filename
            && filename.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(filename.GetString());

    private async Task<JsonElement> ReadElementAsync(
        SlskdCredentials credentials,
        HttpMethod method,
        string apiPath,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(credentials, method, apiPath, jsonBody, cancellationToken).ConfigureAwait(false);
        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseBody(apiPath, body, response.StatusCode);
    }

    private async Task<JsonElement?> TryReadElementAsync(
        SlskdCredentials credentials,
        HttpMethod method,
        string apiPath,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadElementAsync(credentials, method, apiPath, jsonBody, cancellationToken).ConfigureAwait(false);
        }
        catch (SlskdApiException ex) when (ex.IsNotFound)
        {
            return null;
        }
    }

    private async Task SendAsync(
        SlskdCredentials credentials,
        HttpMethod method,
        string apiPath,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(credentials, method, apiPath, jsonBody, cancellationToken).ConfigureAwait(false);
        await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TrySendAsync(
        SlskdCredentials credentials,
        HttpMethod method,
        string apiPath,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(credentials, method, apiPath, jsonBody, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SlskdApiException ex) when (ex.IsNotFound)
        {
            return false;
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        SlskdCredentials credentials,
        HttpMethod method,
        string apiPath,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var requestUri = SlskdBaseUri.TryBuildRequestUri(credentials.BaseUrl, apiPath);
        if (requestUri is null)
        {
            throw new SlskdApiException(0, null, "The configured slskd URL is not a valid http or https address.");
        }

        using var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (credentials.HasApiKey)
        {
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, credentials.ApiKey);
        }

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SlskdApiException(0, null, "slskd did not respond in time.");
        }
        catch (HttpRequestException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "slskd request to {ApiPath} failed to reach the instance.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(apiPath));
            }

            throw new SlskdApiException(0, null, "slskd is unavailable.", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            throw new SlskdApiException(status, body, DescribeFailure(status, apiPath));
        }
    }

    private static JsonElement ParseBody(string apiPath, string body, HttpStatusCode statusCode)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new SlskdApiException(
                (int)statusCode,
                Truncate(body),
                $"slskd returned a response that could not be parsed for {apiPath}.",
                ex);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return string.Empty;
        }
    }

    private static string DescribeFailure(int statusCode, string apiPath) => statusCode switch
    {
        401 or 403 => "slskd rejected the API key.",
        404 => $"slskd does not know about {apiPath}.",
        408 or 504 => "slskd did not respond in time.",
        >= 500 => $"slskd returned HTTP {statusCode}.",
        _ => $"slskd returned HTTP {statusCode} for {apiPath}."
    };

    private static string Truncate(string value)
        => value.Length <= MaxDiagnosticBodyLength
            ? value
            : string.Concat(value.AsSpan(0, MaxDiagnosticBodyLength), "...");
}
