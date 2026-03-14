using System.Text.Json;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class MusixmatchMatcher
{
    private readonly MusixmatchClient _client;
    private readonly ILogger<MusixmatchMatcher> _logger;

    public MusixmatchMatcher(MusixmatchClient client, ILogger<MusixmatchMatcher> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(AutoTagAudioInfo info, CancellationToken cancellationToken)
    {
        var body = await _client.FetchLyricsAsync(info.Title, string.Join(",", info.Artists), cancellationToken);
        if (body == null)
        {
            return null;
        }

        if (TryGetRichsync(body, out var richsyncLines) && richsyncLines.Count > 0)
        {
            var track = ToAutoTagTrack(info);
            track.Other["syncedLyrics"] = BuildRichsyncLrc(richsyncLines);
            return new AutoTagMatchResult { Accuracy = 1.0, Track = track };
        }

        if (TryGetSubtitles(body, out var subtitleLines) && subtitleLines.Count > 0)
        {
            var track = ToAutoTagTrack(info);
            track.Other["syncedLyrics"] = BuildSubtitleLrc(subtitleLines);
            return new AutoTagMatchResult { Accuracy = 1.0, Track = track };
        }

        if (TryGetLyrics(body, out var lyricsBody) && !string.IsNullOrWhiteSpace(lyricsBody))
        {
            var track = ToAutoTagTrack(info);
            track.Other["unsyncedLyrics"] = lyricsBody
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            return new AutoTagMatchResult { Accuracy = 1.0, Track = track };
        }

        return null;
    }

    private bool TryGetRichsync(MusixmatchMacroCallsBody<MusixmatchBody> body, out List<MusixmatchRichsyncLine> lines)
    {
        lines = new List<MusixmatchRichsyncLine>();
        if (!body.MacroCalls.TryGetValue("track.richsync.get", out var response))
        {
            return false;
        }

        var richsync = response.Message.Body?.Richsync;
        if (richsync == null || string.IsNullOrWhiteSpace(richsync.RichsyncBody))
        {
            return false;
        }

        try
        {
            lines = JsonSerializer.Deserialize<List<MusixmatchRichsyncLine>>(richsync.RichsyncBody) ?? new List<MusixmatchRichsyncLine>();
            return lines.Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed parsing Musixmatch richsync payload.");
            return false;
        }
    }

    private static bool TryGetSubtitles(MusixmatchMacroCallsBody<MusixmatchBody> body, out List<MusixmatchSubtitleLine> lines)
    {
        lines = new List<MusixmatchSubtitleLine>();
        if (!body.MacroCalls.TryGetValue("track.subtitles.get", out var response))
        {
            return false;
        }

        var subtitle = response.Message.Body?.SubtitleList?.FirstOrDefault()?.Subtitle;
        if (subtitle == null || string.IsNullOrWhiteSpace(subtitle.SubtitleBody))
        {
            return false;
        }

        lines = ParseSubtitle(subtitle.SubtitleBody);
        return lines.Count > 0;
    }

    private static bool TryGetLyrics(MusixmatchMacroCallsBody<MusixmatchBody> body, out string? lyricsBody)
    {
        lyricsBody = null;
        if (!body.MacroCalls.TryGetValue("track.lyrics.get", out var response))
        {
            return false;
        }

        lyricsBody = response.Message.Body?.Lyrics?.LyricsBody;
        if (string.IsNullOrWhiteSpace(lyricsBody))
        {
            return false;
        }

        if (string.Equals(lyricsBody.Trim(), "instrumental", StringComparison.OrdinalIgnoreCase))
        {
            lyricsBody = null;
            return false;
        }

        return true;
    }

    private static AutoTagTrack ToAutoTagTrack(AutoTagAudioInfo info)
    {
        return new AutoTagTrack
        {
            Title = info.Title,
            Artists = info.Artists.ToList(),
            Duration = info.DurationSeconds.HasValue ? TimeSpan.FromSeconds(info.DurationSeconds.Value) : null,
            Isrc = info.Isrc
        };
    }

    private static List<MusixmatchSubtitleLine> ParseSubtitle(string subtitleBody)
    {
        var output = new List<MusixmatchSubtitleLine>();
        foreach (var line in subtitleBody.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length < 11 || !line.StartsWith('['))
            {
                continue;
            }

            var timestampRaw = line.Substring(1, 8);
            if (!TryParseLrcTimestamp(timestampRaw, out var timestamp))
            {
                continue;
            }

            var text = line.Length > 10 ? line[10..] : string.Empty;
            output.Add(new MusixmatchSubtitleLine { Timestamp = timestamp, Line = text });
        }

        return output;
    }

    private static List<string> BuildSubtitleLrc(List<MusixmatchSubtitleLine> lines)
    {
        return lines
            .Where(l => l.Timestamp >= TimeSpan.Zero)
            .Select(l => $"[{FormatTimestamp(l.Timestamp)}]{l.Line}")
            .ToList();
    }

    private static List<string> BuildRichsyncLrc(List<MusixmatchRichsyncLine> lines)
    {
        var output = new List<string>();
        foreach (var line in lines)
        {
            if (line.Ts < 0 || string.IsNullOrWhiteSpace(line.X))
            {
                continue;
            }
            var ts = TimeSpan.FromSeconds(line.Ts);
            output.Add($"[{FormatTimestamp(ts)}]{line.X.Trim()}");
        }
        return output;
    }

    private static string FormatTimestamp(TimeSpan ts)
    {
        var minutes = (int)ts.TotalMinutes;
        var seconds = ts.Seconds;
        var hundredths = ts.Milliseconds / 10;
        return $"{minutes:D2}:{seconds:D2}.{hundredths:D2}";
    }

    private static bool TryParseLrcTimestamp(string value, out TimeSpan timestamp)
    {
        timestamp = TimeSpan.Zero;
        var parts = value.Split(':');
        if (parts.Length != 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var minutes))
        {
            return false;
        }

        var secondsParts = parts[1].Split('.');
        if (secondsParts.Length != 2 || !int.TryParse(secondsParts[0], out var seconds) || !int.TryParse(secondsParts[1], out var centiseconds))
        {
            return false;
        }

        timestamp = new TimeSpan(0, 0, minutes, seconds, centiseconds * 10);
        return true;
    }
}
