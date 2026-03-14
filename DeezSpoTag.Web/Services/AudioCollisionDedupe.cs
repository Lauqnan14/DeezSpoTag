using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TagLib;
using IOFile = System.IO.File;

namespace DeezSpoTag.Web.Services;

internal static class AudioCollisionDedupe
{
    private const int DurationToleranceMs = 2000;
    private const int ProbeBytes = 262_144;
    private const int AudioHashTimeoutMs = 15_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex AudioHashRegex = new(@"SHA256=([0-9A-Fa-f]{64})", RegexOptions.Compiled, RegexTimeout);
    private static readonly string? FfmpegExecutablePath = FfmpegPathResolver.ResolveExecutable();

    public static bool IsDuplicate(string existingPath, string incomingPath)
    {
        if (string.IsNullOrWhiteSpace(existingPath) || string.IsNullOrWhiteSpace(incomingPath))
        {
            return false;
        }

        var existingIo = existingPath;
        var incomingIo = incomingPath;
        if (string.IsNullOrWhiteSpace(existingIo) || string.IsNullOrWhiteSpace(incomingIo))
        {
            return false;
        }

        try
        {
            existingIo = Path.GetFullPath(existingIo);
            incomingIo = Path.GetFullPath(incomingIo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        if (string.Equals(existingIo, incomingIo, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IOFile.Exists(existingIo) || !IOFile.Exists(incomingIo))
        {
            return false;
        }

        if (TryReadIdentity(existingIo, out var existingIdentity)
            && TryReadIdentity(incomingIo, out var incomingIdentity)
            && MatchesByIdentity(existingIdentity, incomingIdentity))
        {
            return true;
        }

        if (MatchesByBinaryProbe(existingIo, incomingIo))
        {
            return true;
        }

        return MatchesByAudioHash(existingIo, incomingIo);
    }

    public static bool ShouldPreferIncoming(string existingPath, string incomingPath)
    {
        if (!TryReadIdentity(existingPath, out var existingIdentity))
        {
            return false;
        }

        if (!TryReadIdentity(incomingPath, out var incomingIdentity))
        {
            return false;
        }

        return ComputeMetadataScore(incomingIdentity) > ComputeMetadataScore(existingIdentity);
    }

    private static bool TryReadIdentity(string path, out AudioIdentity identity)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            var performers = (tag.Performers ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Normalize(value))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var albumArtists = (tag.AlbumArtists ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Normalize(value))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();

            identity = new AudioIdentity(
                Normalize(tag.ISRC),
                Normalize(tag.Title),
                Normalize(tag.Album),
                performers,
                albumArtists,
                tag.Track > 0 ? (int)tag.Track : null,
                tag.Disc > 0 ? (int)tag.Disc : null,
                file.Properties.Duration.TotalMilliseconds > 0
                    ? (int)Math.Round(file.Properties.Duration.TotalMilliseconds)
                    : null);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            identity = default;
            return false;
        }
    }

    private static bool MatchesByIdentity(AudioIdentity existing, AudioIdentity incoming)
    {
        var durationMatches = DurationMatches(existing.DurationMs, incoming.DurationMs);
        var isrcMatches = !string.IsNullOrWhiteSpace(existing.Isrc) &&
                          !string.IsNullOrWhiteSpace(incoming.Isrc) &&
                          string.Equals(existing.Isrc, incoming.Isrc, StringComparison.Ordinal);
        if (isrcMatches && durationMatches)
        {
            return true;
        }

        var titleMatches = !string.IsNullOrWhiteSpace(existing.Title) &&
                           !string.IsNullOrWhiteSpace(incoming.Title) &&
                           string.Equals(existing.Title, incoming.Title, StringComparison.Ordinal);
        if (!titleMatches || !durationMatches)
        {
            return false;
        }

        var performersMatch = SequenceEquals(existing.Performers, incoming.Performers);
        var albumArtistsMatch = SequenceEquals(existing.AlbumArtists, incoming.AlbumArtists);
        var artistMatch = performersMatch || albumArtistsMatch;
        if (!artistMatch)
        {
            return false;
        }

        var albumMatches = !string.IsNullOrWhiteSpace(existing.Album) &&
                           !string.IsNullOrWhiteSpace(incoming.Album) &&
                           string.Equals(existing.Album, incoming.Album, StringComparison.Ordinal);
        if (albumMatches)
        {
            return true;
        }

        if (existing.TrackNumber.HasValue
            && incoming.TrackNumber.HasValue
            && existing.TrackNumber.Value == incoming.TrackNumber.Value
            && (!existing.DiscNumber.HasValue
                || !incoming.DiscNumber.HasValue
                || existing.DiscNumber.Value == incoming.DiscNumber.Value))
        {
            return true;
        }

        return false;
    }

    private static bool DurationMatches(int? existingMs, int? incomingMs)
    {
        if (!existingMs.HasValue || !incomingMs.HasValue)
        {
            return true;
        }

        return Math.Abs(existingMs.Value - incomingMs.Value) <= DurationToleranceMs;
    }

    private static bool MatchesByBinaryProbe(string existingPath, string incomingPath)
    {
        try
        {
            var existingInfo = new FileInfo(existingPath);
            var incomingInfo = new FileInfo(incomingPath);
            if (existingInfo.Length != incomingInfo.Length)
            {
                return false;
            }

            var leftHash = ComputeProbeHash(existingPath, existingInfo.Length);
            var rightHash = ComputeProbeHash(incomingPath, incomingInfo.Length);
            return string.Equals(leftHash, rightHash, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static bool MatchesByAudioHash(string existingPath, string incomingPath)
    {
        if (!TryComputeAudioHash(existingPath, out var existingHash))
        {
            return false;
        }

        if (!TryComputeAudioHash(incomingPath, out var incomingHash))
        {
            return false;
        }

        return string.Equals(existingHash, incomingHash, StringComparison.Ordinal);
    }

    private static int ComputeMetadataScore(AudioIdentity identity)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(identity.Isrc))
        {
            score += 5;
        }
        if (!string.IsNullOrWhiteSpace(identity.Title))
        {
            score += 3;
        }
        if (!string.IsNullOrWhiteSpace(identity.Album))
        {
            score += 2;
        }
        if (identity.Performers.Count > 0 || identity.AlbumArtists.Count > 0)
        {
            score += 3;
        }
        if (identity.TrackNumber.HasValue)
        {
            score += 1;
        }
        if (identity.DiscNumber.HasValue)
        {
            score += 1;
        }
        if (identity.DurationMs.HasValue)
        {
            score += 1;
        }

        return score;
    }

    private static bool TryComputeAudioHash(string path, out string hash)
    {
        hash = string.Empty;
        if (string.IsNullOrWhiteSpace(FfmpegExecutablePath))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = FfmpegExecutablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(path);
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:a:0");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("hash");
            startInfo.ArgumentList.Add("-hash");
            startInfo.ArgumentList.Add("sha256");
            startInfo.ArgumentList.Add("-");

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(AudioHashTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Best effort: the process may already have exited.
                    Debug.WriteLine(ex);
                }
                return false;
            }

            var output = stdoutTask.GetAwaiter().GetResult();
            var errors = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                return false;
            }

            var combined = string.Concat(output, "\n", errors);
            var match = AudioHashRegex.Match(combined);
            if (!match.Success)
            {
                return false;
            }

            hash = match.Groups[1].Value.ToUpperInvariant();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static string ComputeProbeHash(string path, long length)
    {
        using var stream = IOFile.OpenRead(path);
        if (length <= ProbeBytes * 2L)
        {
            var allBytes = new byte[length];
            _ = stream.Read(allBytes, 0, allBytes.Length);
            return Convert.ToHexString(SHA256.HashData(allBytes));
        }

        var head = new byte[ProbeBytes];
        _ = stream.Read(head, 0, head.Length);

        stream.Seek(Math.Max(0, length - ProbeBytes), SeekOrigin.Begin);
        var tail = new byte[ProbeBytes];
        _ = stream.Read(tail, 0, tail.Length);

        var lengthBytes = BitConverter.GetBytes(length);
        var payload = new byte[head.Length + tail.Length + lengthBytes.Length];
        Buffer.BlockCopy(head, 0, payload, 0, head.Length);
        Buffer.BlockCopy(tail, 0, payload, head.Length, tail.Length);
        Buffer.BlockCopy(lengthBytes, 0, payload, head.Length + tail.Length, lengthBytes.Length);
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Regex.Replace(value.Trim(), @"\s+", " ", RegexOptions.None, RegexTimeout).ToLowerInvariant();
    }

    private static bool SequenceEquals(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return false;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct AudioIdentity(
        string Isrc,
        string Title,
        string Album,
        IReadOnlyList<string> Performers,
        IReadOnlyList<string> AlbumArtists,
        int? TrackNumber,
        int? DiscNumber,
        int? DurationMs);
}
