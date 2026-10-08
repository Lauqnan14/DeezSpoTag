using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.Updates;

/// <summary>A published release for the configured repository.</summary>
/// <param name="Tag">The release tag, for example <c>v0.1.27.6-pre</c>.</param>
/// <param name="Name">The release display name.</param>
/// <param name="Url">The release page URL.</param>
/// <param name="IsPrerelease">Whether GitHub marks the release as a prerelease.</param>
/// <param name="IsDraft">Whether the release is still a draft.</param>
/// <param name="PublishedUtc">When the release was published.</param>
public sealed record GitHubReleaseSummary(
    string Tag,
    string Name,
    string Url,
    bool IsPrerelease,
    bool IsDraft,
    DateTimeOffset? PublishedUtc);

/// <summary>
/// Reads published releases for a repository from the public GitHub REST API.
/// </summary>
/// <remarks>
/// Only <c>GET /releases</c> is used. The <c>/releases/latest</c> endpoint deliberately excludes
/// prereleases, so it answers 404 on a repository whose releases are all prereleased.
/// </remarks>
public sealed class GitHubReleaseClient
{
    private const int PageSize = 20;
    private const string ApiBase = "https://api.github.com/";
    private const string UserAgentValue = "DeezSpoTag-AppVersion";

    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubReleaseClient> _logger;

    public GitHubReleaseClient(HttpClient httpClient, ILogger<GitHubReleaseClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(ApiBase);
        }

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentValue);
        }

        if (_httpClient.DefaultRequestHeaders.Accept.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    /// <summary>
    /// Returns the newest published release belonging to <paramref name="channel"/>, or
    /// <see langword="null"/> when the repository has no such release or the request failed.
    /// </summary>
    public async Task<GitHubReleaseSummary?> GetLatestReleaseAsync(
        string owner,
        string repository,
        AppVersionChannel channel,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner)
            || string.IsNullOrWhiteSpace(repository)
            || channel == AppVersionChannel.Unknown)
        {
            return null;
        }

        List<GitHubRelease>? releases;
        try
        {
            using var response = await _httpClient.GetAsync(
                $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases?per_page={PageSize}",
                cancellationToken);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                _logger.LogWarning(
                    "GitHub release lookup was refused with HTTP {Status}; the unauthenticated rate limit may be exhausted.",
                    (int)response.StatusCode);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub release lookup failed with HTTP {Status}.", (int)response.StatusCode);
                return null;
            }

            releases = await response.Content.ReadFromJsonAsync<List<GitHubRelease>>(cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "GitHub release lookup failed: network error.");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "GitHub release lookup failed: response was not valid JSON.");
            return null;
        }

        return (releases ?? [])
            .Where(release => !release.Draft)
            .Where(release => ChannelMatches(release.Prerelease, channel))
            .Select(ToSummary)
            .FirstOrDefault();
    }

    private static bool ChannelMatches(bool isPrerelease, AppVersionChannel channel) => channel switch
    {
        AppVersionChannel.Prerelease => isPrerelease,
        AppVersionChannel.Stable => !isPrerelease,
        _ => false
    };

    private static GitHubReleaseSummary ToSummary(GitHubRelease release) => new(
        release.TagName ?? string.Empty,
        string.IsNullOrWhiteSpace(release.Name) ? release.TagName ?? string.Empty : release.Name!,
        release.HtmlUrl ?? string.Empty,
        release.Prerelease,
        release.Draft,
        release.PublishedAt ?? release.CreatedAt);

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; init; }
    }
}
