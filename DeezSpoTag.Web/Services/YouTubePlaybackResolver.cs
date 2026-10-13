using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Caching.Memory;

namespace DeezSpoTag.Web.Services;

/// <summary>Resolves audio streaming URLs only. Never used by download identity or download engines.</summary>
public sealed class YouTubePlaybackResolver(DeezSpoTagSettingsService settings, IMemoryCache cache,
    IHttpClientFactory httpFactory, string dataRoot, string runtimePath)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string sessionPath = Path.Join(dataRoot, "cache", "youtube-playback", "session.json");
    private GuestSession? session;
    private DateTimeOffset throttledUntil;
    private sealed record GuestSession(string VisitorData, string Country, string Language, DateTimeOffset CreatedAt);
    internal sealed class PlaybackFailure(string category, string message) : InvalidOperationException(message)
    {
        public string Category { get; } = category;
    }

    public void Invalidate(string id)
    {
        ValidateVideoId(id);
        cache.Remove($"youtube:stream:{settings.LoadSettings().YouTubeExtractorPath}:{id}");
    }

    internal static PlaybackFailure ClassifyFailure(string diagnostics)
    {
        if (diagnostics.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase))
            return new("throttled", "YouTube playback is throttled. Please try again shortly.");
        if (diagnostics.Contains("not available in your country", StringComparison.OrdinalIgnoreCase) || diagnostics.Contains("geo-restricted", StringComparison.OrdinalIgnoreCase))
            return new("geographic", "This YouTube recording is unavailable in your region.");
        if (diagnostics.Contains("Sign in", StringComparison.OrdinalIgnoreCase) || diagnostics.Contains("not a bot", StringComparison.OrdinalIgnoreCase) || diagnostics.Contains("LOGIN_REQUIRED", StringComparison.Ordinal))
            return new("session", "YouTube rejected the guest playback session.");
        if (diagnostics.Contains("PO Token", StringComparison.OrdinalIgnoreCase) || diagnostics.Contains("bgutil", StringComparison.OrdinalIgnoreCase))
            return new("provider", "YouTube proof-token generation is unavailable.");
        if (diagnostics.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || diagnostics.Contains("private video", StringComparison.OrdinalIgnoreCase))
            return new("unavailable", "This YouTube recording is unavailable.");
        return new("extractor", "YouTube streaming resolution failed.");
    }
    public sealed record StreamInfo(string Url, string ContentType, DateTimeOffset ExpiresAt);

    public static void ValidateVideoId(string id)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250))) throw new ArgumentException("Invalid YouTube video ID.");
    }

    public static bool IsMediaUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo) && uri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase);

    public static ProcessStartInfo BuildProcess(string executable, string id, string? runtimePath = null, string? visitorData = null, string? country = null, string? language = null)
    {
        ValidateVideoId(id);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--no-playlist", "--skip-download", "--simulate", "--dump-single-json", "--no-warnings", "--socket-timeout", "15", "--retries", "0", "--no-js-runtimes", "--js-runtimes", "node", "-f", "bestaudio[ext=m4a]/bestaudio[ext=webm]" }) start.ArgumentList.Add(argument);
        if (runtimePath is not null)
        {
            foreach (var argument in new[] { "--plugin-dirs", Path.Join(runtimePath, "plugins"), "--extractor-args",
                "youtubepot-bgutilscript:server_home=" + Path.Join(runtimePath, "server"), "--extractor-args",
                "youtube:player_client=mweb;visitor_data=" + visitorData, "--extractor-args", "youtube:lang=" + language,
                "--geo-bypass-country", country! }) start.ArgumentList.Add(argument);
        }
        start.ArgumentList.Add("--");
        start.ArgumentList.Add("https://music.youtube.com/watch?v=" + id);
        return start;
    }

    public static StreamInfo ParseStream(string json, string id, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("id").GetString() != id || root.GetProperty("vcodec").GetString() != "none" || root.GetProperty("acodec").GetString() == "none") throw new InvalidOperationException("No supported YouTube audio stream.");
        var url = root.GetProperty("url").GetString();
        if (!IsMediaUrl(url)) throw new InvalidOperationException("Unsupported YouTube media host.");
        var expires = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(url!).Query).TryGetValue("expire", out var expiry) && long.TryParse(expiry, out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix).AddMinutes(-2) : now.AddMinutes(2);
        if (expires <= now) throw new InvalidOperationException("YouTube stream expired.");
        var extension = root.GetProperty("ext").GetString();
        if (extension is not "m4a" and not "webm") throw new InvalidOperationException("Unsupported audio format.");
        return new(url!, extension == "m4a" ? "audio/mp4" : "audio/webm", expires);
    }

    public async Task<StreamInfo> ResolveAsync(string id, CancellationToken cancellationToken)
    {
        ValidateVideoId(id);
        var executable = settings.LoadSettings().YouTubeExtractorPath;
        var key = $"youtube:stream:{executable}:{id}";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        await gate.WaitAsync(deadline.Token);
        try
        {
            if (cache.TryGetValue(key, out StreamInfo? found) && found is not null && found.ExpiresAt > DateTimeOffset.UtcNow) return found;
            if (throttledUntil > DateTimeOffset.UtcNow) throw ClassifyFailure("HTTP Error 429");
            if (cache.TryGetValue(key + ":failure", out PlaybackFailure? recent) && recent is not null) throw recent;
            if (!File.Exists(Path.Join(runtimePath, "server", "build", "generate_once.js"))
                || !File.Exists(Path.Join(runtimePath, "plugins", "bgutil-ytdlp-pot-provider.zip")))
                throw new PlaybackFailure("provider", "YouTube proof-token runtime is missing. Run the playback runtime setup.");
            var preferences = settings.LoadSettings();
            var country = string.IsNullOrWhiteSpace(preferences.YouTubeMusicCountry) ? preferences.DeezerCountry : preferences.YouTubeMusicCountry;
            var language = preferences.DeezerLanguage;
            if (!Regex.IsMatch(country, "^[a-zA-Z]{2}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250))
                || !Regex.IsMatch(language, "^[a-zA-Z]{2,3}(?:-[a-zA-Z]{2,4})?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))
                throw new PlaybackFailure("session", "Set a valid YouTube Music country and content language.");
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var identity = await GetSessionAsync(country, language, attempt > 0, deadline.Token);
                try
                {
                    var result = await ExtractAsync(executable, id, identity, deadline.Token);
                    cache.Set(key, result, result.ExpiresAt);
                    return result;
                }
                catch (PlaybackFailure error)
                {
                    if (error.Category == "throttled") throttledUntil = DateTimeOffset.UtcNow.AddSeconds(30);
                    if (error.Category == "session" && attempt == 0) continue;
                    cache.Set(key + ":failure", error, TimeSpan.FromSeconds(30));
                    throw;
                }
            }
            throw new PlaybackFailure("session", "YouTube rejected the guest playback session.");
        }
        catch (PlaybackFailure error) { throw new InvalidOperationException(error.Message); }
        catch (HttpRequestException) { throw new InvalidOperationException("YouTube guest session bootstrap is unavailable."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("YouTube playback session storage is unavailable."); }
        finally { gate.Release(); }
    }

    private async Task<GuestSession> GetSessionAsync(string country, string language, bool refresh, CancellationToken token)
    {
        if (!refresh && session is null && File.Exists(sessionPath) && new FileInfo(sessionPath).Length <= 16_384)
        {
            try { session = JsonSerializer.Deserialize<GuestSession>(await File.ReadAllTextAsync(sessionPath, token)); }
            catch (JsonException) { session = null; }
        }
        if (!refresh && session is { } stored && ValidVisitor(stored.VisitorData)
            && stored.Country == country && stored.Language == language
            && stored.CreatedAt <= DateTimeOffset.UtcNow && stored.CreatedAt > DateTimeOffset.UtcNow.AddHours(-24)) return stored;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://music.youtube.com/sw.js_data?hl=" + Uri.EscapeDataString(language) + "&gl=" + Uri.EscapeDataString(country));
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        request.Headers.TryAddWithoutValidation("Accept-Language", language);
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        using var http = httpFactory.CreateClient("YouTubePlaybackBootstrap");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if ((int)response.StatusCode == 429)
        {
            throttledUntil = DateTimeOffset.UtcNow.AddSeconds(30);
            throw ClassifyFailure("HTTP Error 429");
        }
        if (!response.IsSuccessStatusCode) throw new PlaybackFailure("session", "YouTube guest session bootstrap failed.");
        await using var body = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(body);
        var json = await ReadBoundedAsync(reader, 1_000_000, token);
        string? visitor;
        try
        {
            using var doc = JsonDocument.Parse(json.StartsWith(")]}'", StringComparison.Ordinal) ? json[5..] : json);
            visitor = doc.RootElement[0][2][0][0][13].GetString();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        { throw new PlaybackFailure("session", "YouTube guest session bootstrap returned invalid data."); }
        if (!ValidVisitor(visitor)) throw new PlaybackFailure("session", "YouTube guest session bootstrap returned invalid data.");
        var identity = new GuestSession(visitor!, country, language, DateTimeOffset.UtcNow);
        var directory = Path.GetDirectoryName(sessionPath)!;
        Directory.CreateDirectory(directory);
        var temporary = sessionPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(identity), token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, sessionPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        session = identity;
        return identity;
    }

    private static bool ValidVisitor(string? value) => value is { Length: >= 10 and <= 2048 }
        && Regex.IsMatch(value, "^[A-Za-z0-9_%=-]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private async Task<StreamInfo> ExtractAsync(string executable, string id, GuestSession identity, CancellationToken token)
    {
        using var process = new Process { StartInfo = BuildProcess(executable, id, runtimePath, identity.VisitorData, identity.Country, identity.Language) };
        // Some token contexts bind to a recording rather than the visitor. Keep
        // those caches isolated too, so a refreshed guest cannot reuse old tokens.
        var identityKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity.VisitorData)));
        process.StartInfo.Environment["XDG_CACHE_HOME"] = Path.Join(
            Path.GetDirectoryName(sessionPath)!, "proof-tokens", identityKey);
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { throw new PlaybackFailure("tools", "YouTube playback extractor is missing or cannot start."); }
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException)
            {
                // Process exit won the race with cancellation; nothing remains to kill.
            }
        });
        var output = ReadBoundedAsync(process.StandardOutput, 2_000_000, token);
        var errors = ReadBoundedAsync(process.StandardError, 100_000, token);
        try
        {
            await process.WaitForExitAsync(token);
            var json = await output;
            var diagnostics = await errors;
            if (process.ExitCode != 0) throw ClassifyFailure(diagnostics);
            try { return ParseStream(json, id, DateTimeOffset.UtcNow); }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or ArgumentOutOfRangeException)
            { throw new PlaybackFailure("metadata", "YouTube returned invalid streaming metadata."); }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(output, errors); }
            catch (Exception error) when (error is OperationCanceledException or InvalidOperationException)
            {
                // Observe both drain failures while preserving the original resolution failure.
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int length;
        while ((length = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (result.Length + length > limit) throw new InvalidOperationException("YouTube extractor response exceeded its limit.");
            result.Append(buffer, 0, length);
        }
        return result.ToString();
    }
}
