using System.Net;
using Microsoft.Extensions.Configuration;

namespace DeezSpoTag.API.Services;

public sealed class DeezSpoTagSearchProxyService
{
    private const string DefaultProxyScheme = "http";
    private const string DefaultProxyHost = "localhost";
    private const int DefaultProxyPort = 8668;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _baseUrl;

    public DeezSpoTagSearchProxyService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _baseUrl = configuration["DeezSpoTagWebBaseUrl"]?.TrimEnd('/')
            ?? BuildDefaultBaseUrl();
    }

    public Task<(HttpStatusCode Status, string Body)> SearchAsync(
        string engine,
        string query,
        int limit,
        int offset,
        string? types,
        CancellationToken cancellationToken)
    {
        var url = engine switch
        {
            "apple" => $"{_baseUrl}/api/apple/search?term={Uri.EscapeDataString(query)}&limit={limit}&offset={offset}"
                       + (string.IsNullOrWhiteSpace(types) ? "" : $"&types={Uri.EscapeDataString(types)}"),
            "spotify" => $"{_baseUrl}/api/spotify/search?query={Uri.EscapeDataString(query)}&limit={limit}",
            "deezer" => $"{_baseUrl}/api/deezer/search?query={Uri.EscapeDataString(query)}&limit={limit}&offset={offset}",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.FromResult((HttpStatusCode.BadRequest, "{\"error\":\"Invalid engine\"}"));
        }

        return GetAsync(url, cancellationToken);
    }

    public Task<(HttpStatusCode Status, string Body)> SearchByTypeAsync(
        string engine,
        string query,
        string type,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        var url = engine switch
        {
            "spotify" => $"{_baseUrl}/api/spotify/search/type?query={Uri.EscapeDataString(query)}&type={Uri.EscapeDataString(type)}&limit={limit}&offset={offset}",
            "deezer" => $"{_baseUrl}/api/deezer/search/type?query={Uri.EscapeDataString(query)}&type={Uri.EscapeDataString(type)}&limit={limit}&offset={offset}",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.FromResult((HttpStatusCode.BadRequest, "{\"error\":\"Invalid engine\"}"));
        }

        return GetAsync(url, cancellationToken);
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response.StatusCode, body);
    }

    private static string BuildDefaultBaseUrl()
        => new UriBuilder(DefaultProxyScheme, DefaultProxyHost, DefaultProxyPort).Uri.ToString().TrimEnd('/');
}
