using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Caching.Memory;

namespace DeezSpoTag.Web.Services;

/// <summary>Resolves audio streaming URLs only. Never used by download identity or download engines.</summary>
public sealed class YouTubePlaybackResolver(DeezSpoTagSettingsService settings, IMemoryCache cache)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public sealed record StreamInfo(string Url, string ContentType, DateTimeOffset ExpiresAt);

    public static void ValidateVideoId(string id)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250))) throw new ArgumentException("Invalid YouTube video ID.");
    }

    public static bool IsMediaUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo) && uri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase);

    public static ProcessStartInfo BuildProcess(string executable, string id)
    {
        ValidateVideoId(id);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--no-playlist", "--skip-download", "--simulate", "--dump-single-json", "--no-warnings", "--socket-timeout", "15", "--retries", "0", "--js-runtimes", "node", "-f", "bestaudio[ext=m4a]/bestaudio[ext=webm]", "--", "https://music.youtube.com/watch?v=" + id }) start.ArgumentList.Add(argument);
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
            using var process = new Process { StartInfo = BuildProcess(executable, id) };
            try { process.Start(); }
            catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("YouTube streaming is unavailable."); }
            using var registration = deadline.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            // Drain both pipes concurrently and cap output so an extractor cannot fill server memory.
            var output = ReadBoundedAsync(process.StandardOutput, 2_000_000, deadline.Token);
            var errors = ReadBoundedAsync(process.StandardError, 100_000, deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                var json = await output;
                await errors; // Errors may contain signed URLs; never log or return them.
                if (process.ExitCode != 0) throw new InvalidOperationException("YouTube streaming resolution failed. Check the installed yt-dlp version and runtime support.");
                var result = ParseStream(json, id, DateTimeOffset.UtcNow);
                cache.Set(key, result, result.ExpiresAt);
                return result;
            }
            catch (JsonException) { throw new InvalidOperationException("YouTube returned invalid streaming metadata."); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        finally { gate.Release(); }
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
