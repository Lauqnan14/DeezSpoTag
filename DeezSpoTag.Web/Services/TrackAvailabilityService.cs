using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Identity;

namespace DeezSpoTag.Web.Services;

public sealed class TrackAvailabilityService
{
    private static readonly TimeSpan CentralResolverTimeout = TimeSpan.FromSeconds(6);
    private readonly ITrackIdentityResolver _trackIdentityResolver;

    public TrackAvailabilityService(ITrackIdentityResolver trackIdentityResolver)
    {
        _trackIdentityResolver = trackIdentityResolver;
    }

    public async Task<TrackAvailabilityResult> ResolveAsync(
        TrackAvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        var input = BuildInput(request);
        if (!input.HasLookupSignal)
        {
            return TrackAvailabilityResult.Failed("spotifyId, url, isrc, deezerId, appleId, tidalId, qobuzId, or amazonId is required.");
        }

        return await BuildResultAsync(input, cancellationToken);
    }

    private async Task<TrackAvailabilityResult> BuildResultAsync(
        AvailabilityInput input,
        CancellationToken cancellationToken)
    {
        var request = new TrackIdentityResolutionRequest(
            SourcePlatform: InferPlatformFromUrl(input.Url),
            SourceUrl: input.Url,
            Title: input.Title,
            Artist: input.Artist,
            Album: input.Album,
            Isrc: input.Isrc,
            DurationMs: input.DurationMs,
            SpotifyId: input.SpotifyId,
            DeezerId: input.NormalizedDeezerId,
            AppleId: input.AppleId,
            QobuzId: input.QobuzId,
            TidalId: input.TidalId,
            AmazonId: input.AmazonId,
            TargetPlatforms: ResolveAvailabilityTargets(input));
        var central = await ResolveCentralAvailabilityAsync(request, cancellationToken);

        var spotifyId = FirstNonEmpty(input.SpotifyId, central.SpotifyId);
        spotifyId = LooksLikeSpotifyId(spotifyId) ? spotifyId : null;

        var deezerId = FirstNonEmpty(
            input.NormalizedDeezerId,
            central.DeezerId);
        var appleUrl = central.AppleUrl;
        var appleId = FirstNonEmpty(input.AppleId, ExtractAppleId(appleUrl), central.AppleId);
        var appleUnknown = false;
        if (IsFabricatedAppleIdentity(deezerId, appleId, appleUrl))
        {
            appleId = null;
            appleUrl = null;
        }

        var tidalId = FirstNonEmpty(input.TidalId, central.TidalId);
        var qobuzId = FirstNonEmpty(input.QobuzId, central.QobuzId);
        var amazonId = FirstNonEmpty(input.AmazonId, central.AmazonId);

        var spotifyUrl = FirstNonEmpty(central.SpotifyUrl, BuildSpotifyUrl(spotifyId));
        var deezerUrl = FirstNonEmpty(central.DeezerUrl, BuildDeezerUrl(deezerId));
        var tidalUrl = FirstNonEmpty(central.TidalUrl, BuildTidalUrl(tidalId));
        var amazonUrl = central.AmazonUrl;
        var qobuzUrl = FirstNonEmpty(central.QobuzUrl, BuildQobuzUrl(qobuzId));
        appleUrl = FirstNonEmpty(appleUrl, BuildAppleUrl(appleId));
        if (IsFabricatedAppleIdentity(deezerId, appleId, appleUrl))
        {
            appleId = null;
            appleUrl = null;
            appleUnknown = false;
        }

        var spotify = IsAvailable(spotifyId, spotifyUrl, input.Url, "spotify");
        var deezer = IsAvailable(deezerId, deezerUrl, input.Url, "deezer");
        var tidal = IsAvailable(tidalId, tidalUrl, input.Url, "tidal");
        var amazon = IsAvailable(amazonId, amazonUrl, input.Url, "amazon");
        var qobuz = IsAvailable(qobuzId, qobuzUrl, input.Url, "qobuz");
        bool? apple = appleUnknown ? null : IsAvailable(appleId, appleUrl, input.Url, "apple");

        return new TrackAvailabilityResult
        {
            Available = spotify || deezer || tidal || amazon || qobuz || apple == true,
            Resolved = true,
            ResolverAttempted = central.Candidates.Count > 0,
            ResolverResolved = central.Candidates.Any(static candidate => candidate.Accepted),
            Spotify = spotify,
            SpotifyId = spotifyId,
            SpotifyUrl = spotifyUrl,
            Isrc = FirstNonEmpty(input.Isrc, central.Isrc),
            Deezer = deezer,
            DeezerId = deezerId,
            DeezerUrl = deezerUrl,
            Tidal = tidal,
            TidalId = tidalId,
            TidalUrl = tidalUrl,
            Amazon = amazon,
            AmazonId = amazonId,
            AmazonUrl = amazonUrl,
            Qobuz = qobuz,
            QobuzId = qobuzId,
            QobuzUrl = qobuzUrl,
            Apple = apple,
            AppleId = appleId,
            AppleUrl = appleUrl
        };
    }

    private async Task<TrackIdentityResolution> ResolveCentralAvailabilityAsync(
        TrackIdentityResolutionRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CentralResolverTimeout);
        try
        {
            return await _trackIdentityResolver.ResolveAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TrackIdentityResolution.Empty(request);
        }
    }

    private static IReadOnlyCollection<string> ResolveAvailabilityTargets(AvailabilityInput input)
    {
        var targets = new List<string>(capacity: 3);
        if (string.IsNullOrWhiteSpace(input.SpotifyId))
        {
            targets.Add("spotify");
        }
        if (string.IsNullOrWhiteSpace(input.NormalizedDeezerId))
        {
            targets.Add("deezer");
        }
        if (string.IsNullOrWhiteSpace(input.TidalId))
        {
            targets.Add("tidal");
        }

        return targets;
    }

    private static bool IsFabricatedAppleIdentity(
        string? deezerId,
        string? appleId,
        string? appleUrl)
    {
        return !string.IsNullOrWhiteSpace(deezerId)
            && !string.IsNullOrWhiteSpace(appleId)
            && string.Equals(appleId, deezerId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(appleUrl)
                || appleUrl.Contains("/song/", StringComparison.OrdinalIgnoreCase));
    }

    private static AvailabilityInput BuildInput(TrackAvailabilityRequest request)
    {
        var spotifyId = FirstNonEmpty(request.SpotifyId, ExtractSpotifyId(request.Url));
        var normalizedDeezerId = NormalizeDeezerId(request.DeezerId);
        if (string.IsNullOrWhiteSpace(normalizedDeezerId)
            && TryExtractDeezerId(request.Url, out var deezerIdFromUrl))
        {
            normalizedDeezerId = deezerIdFromUrl;
        }
        if (string.IsNullOrWhiteSpace(normalizedDeezerId)
            && LooksLikeSpotifyId(request.DeezerId)
            && string.IsNullOrWhiteSpace(spotifyId))
        {
            spotifyId = request.DeezerId;
        }

        return new AvailabilityInput
        {
            SpotifyId = spotifyId,
            Url = request.Url,
            Isrc = request.Isrc,
            NormalizedDeezerId = normalizedDeezerId,
            AppleId = FirstNonEmpty(request.AppleId, ExtractAppleId(request.Url)),
            TidalId = FirstNonEmpty(request.TidalId, ExtractTidalId(request.Url)),
            QobuzId = FirstNonEmpty(request.QobuzId, ExtractQobuzId(request.Url)),
            AmazonId = request.AmazonId,
            Title = request.Title,
            Artist = request.Artist,
            Album = request.Album,
            DurationMs = request.DurationMs
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? InferPlatformFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.Contains("spotify.com", StringComparison.Ordinal))
        {
            return "spotify";
        }

        if (host.Contains("deezer.com", StringComparison.Ordinal))
        {
            return "deezer";
        }

        if (host.Contains("music.apple.", StringComparison.Ordinal)
            || host.Contains("itunes.apple.", StringComparison.Ordinal))
        {
            return "apple";
        }

        if (host.Contains("tidal.com", StringComparison.Ordinal))
        {
            return "tidal";
        }

        if (host.Contains("qobuz.com", StringComparison.Ordinal))
        {
            return "qobuz";
        }

        if (host.Contains("amazon.", StringComparison.Ordinal))
        {
            return "amazon";
        }

        return null;
    }

    private static string? BuildSpotifyUrl(string? spotifyId)
        => LooksLikeSpotifyId(spotifyId) ? $"https://open.spotify.com/track/{spotifyId}" : null;

    private static string? BuildDeezerUrl(string? deezerId)
        => string.IsNullOrWhiteSpace(deezerId) ? null : $"https://www.deezer.com/track/{deezerId}";

    private static string? ExtractDeezerId(string? value)
        => TryExtractDeezerId(value, out var deezerId) ? deezerId : null;

    private static string? BuildTidalUrl(string? tidalId)
        => string.IsNullOrWhiteSpace(tidalId) ? null : $"https://listen.tidal.com/track/{tidalId}";

    private static string? BuildQobuzUrl(string? qobuzId)
        => string.IsNullOrWhiteSpace(qobuzId) ? null : $"https://open.qobuz.com/track/{qobuzId}";

    private static string? BuildAppleUrl(string? appleId)
        => string.IsNullOrWhiteSpace(appleId) ? null : $"https://music.apple.com/song/{appleId}?i={appleId}";

    private static bool IsAvailable(string? id, string? mappedUrl, string? sourceUrl, string platform)
    {
        return !string.IsNullOrWhiteSpace(id)
            || !string.IsNullOrWhiteSpace(mappedUrl)
            || IsSourceUrlForPlatform(sourceUrl, platform);
    }

    private static bool IsSourceUrlForPlatform(string? sourceUrl, string platform)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return false;
        }

        var normalized = sourceUrl.ToLowerInvariant();
        return platform switch
        {
            "spotify" => normalized.Contains("open.spotify.com/track/", StringComparison.Ordinal)
                         || normalized.StartsWith("spotify:track:", StringComparison.Ordinal),
            "deezer" => normalized.Contains("deezer.com/track/", StringComparison.Ordinal),
            "tidal" => normalized.Contains("tidal.com/track/", StringComparison.Ordinal)
                       || normalized.Contains("tidal.com/browse/track/", StringComparison.Ordinal),
            "qobuz" => normalized.Contains("qobuz.com/", StringComparison.Ordinal)
                       && normalized.Contains("/track/", StringComparison.Ordinal),
            "apple" => normalized.Contains("music.apple.com/", StringComparison.Ordinal)
                       && (normalized.Contains("/song/", StringComparison.Ordinal)
                           || normalized.Contains("?i=", StringComparison.Ordinal)),
            "amazon" => normalized.Contains("music.amazon.", StringComparison.Ordinal),
            _ => false
        };
    }

    private static string? ExtractSpotifyId(string? value)
        => ExtractIdByRegex(value, @"open\.spotify\.com\/track\/(?<id>[A-Za-z0-9]+)");

    private static bool TryExtractDeezerId(string? value, out string? deezerId)
    {
        deezerId = ExtractIdByRegex(value, @"deezer\.com\/(?:[a-z]{2}\/)?track\/(?<id>\d+)");
        return !string.IsNullOrWhiteSpace(deezerId);
    }

    private static string? ExtractAppleId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var queryMatch = System.Text.RegularExpressions.Regex.Match(
            value,
            @"[?&]i=(?<id>\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(250));
        if (queryMatch.Success)
        {
            return queryMatch.Groups["id"].Value;
        }

        var pathMatch = System.Text.RegularExpressions.Regex.Match(
            value,
            @"\/(?<id>\d+)(?:[/?#]|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(250));
        return pathMatch.Success ? pathMatch.Groups["id"].Value : null;
    }

    private static string? ExtractTidalId(string? value)
        => ExtractIdByRegex(value, @"tidal\.com\/(?:browse\/)?track\/(?<id>\d+)");

    private static string? ExtractQobuzId(string? value)
        => ExtractIdByRegex(value, @"qobuz\.com\/(?:[a-z]{2}\/[a-z]{2}\/)?track\/(?<id>\d+)");

    private static string? ExtractIdByRegex(string? value, string pattern)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            value,
            pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(250));
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static bool LooksLikeSpotifyId(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length == 22
           && value.All(char.IsAsciiLetterOrDigit);

    private static string? NormalizeDeezerId(string? value)
        => !string.IsNullOrWhiteSpace(value) && long.TryParse(value, out _) ? value : null;

    private sealed class AvailabilityInput
    {
        public string? SpotifyId { get; init; }
        public string? Url { get; init; }
        public string? Isrc { get; init; }
        public string? NormalizedDeezerId { get; init; }
        public string? AppleId { get; init; }
        public string? TidalId { get; init; }
        public string? QobuzId { get; init; }
        public string? AmazonId { get; init; }
        public string? Title { get; init; }
        public string? Artist { get; init; }
        public string? Album { get; init; }
        public int? DurationMs { get; init; }

        public bool HasLookupSignal =>
            !string.IsNullOrWhiteSpace(SpotifyId)
            || !string.IsNullOrWhiteSpace(Url)
            || !string.IsNullOrWhiteSpace(Isrc)
            || !string.IsNullOrWhiteSpace(NormalizedDeezerId)
            || !string.IsNullOrWhiteSpace(AppleId)
            || !string.IsNullOrWhiteSpace(TidalId)
            || !string.IsNullOrWhiteSpace(QobuzId)
            || !string.IsNullOrWhiteSpace(AmazonId);
    }

}

public sealed class TrackAvailabilityRequest
{
    public string? SpotifyId { get; set; }
    public string? Url { get; set; }
    public string? Isrc { get; set; }
    public string? DeezerId { get; set; }
    public string? AppleId { get; set; }
    public string? TidalId { get; set; }
    public string? QobuzId { get; set; }
    public string? AmazonId { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public int? DurationMs { get; set; }
}

public sealed class TrackAvailabilityResult
{
    public string? Error { get; init; }
    public bool Available { get; init; }
    public bool Resolved { get; init; }
    public bool ResolverAttempted { get; init; }
    public bool ResolverResolved { get; init; }
    public string? ResolverError { get; init; }
    public bool Spotify { get; init; }
    public string? SpotifyId { get; init; }
    public string? SpotifyUrl { get; init; }
    public string? Isrc { get; init; }
    public bool Deezer { get; init; }
    public string? DeezerId { get; init; }
    public string? DeezerUrl { get; init; }
    public bool Tidal { get; init; }
    public string? TidalId { get; init; }
    public string? TidalUrl { get; init; }
    public bool Amazon { get; init; }
    public string? AmazonId { get; init; }
    public string? AmazonUrl { get; init; }
    public bool Qobuz { get; init; }
    public string? QobuzId { get; init; }
    public string? QobuzUrl { get; init; }
    public bool? Apple { get; init; }
    public string? AppleId { get; init; }
    public string? AppleUrl { get; init; }

    public static TrackAvailabilityResult Failed(string error)
    {
        return new TrackAvailabilityResult { Error = error };
    }
}
