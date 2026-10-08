using System.Security.Cryptography;
using System.Text;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Integrations.Soulseek;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>Projects a raw peer-directory file through the user's current Soulseek download policy.</summary>
public static class SoulseekBrowseFilePolicy
{
    private static readonly HashSet<string> KnownNonAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".cue", ".gif", ".jpeg", ".jpg", ".log", ".m3u", ".m3u8",
        ".nfo", ".pdf", ".png", ".sfv", ".txt", ".webp"
    };

    public static SoulseekBrowseFile Evaluate(
        string username,
        SlskdFile file,
        SoulseekDownloadSettings settings,
        IReadOnlyCollection<string> enabledQualities,
        string? browseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(enabledQualities);

        var normalizedUsername = (username ?? string.Empty).Trim();
        var normalizedFilename = ResolveRemotePath(file.Filename, browseDirectory);
        var displayFilename = SoulseekRemotePath.GetLeaf(normalizedFilename);
        var remoteDirectory = SoulseekRemotePath.GetDirectory(normalizedFilename);
        var facts = SoulseekFilenameParser.Parse(file.Filename, file.Length);
        var quality = SoulseekQuality.Normalize(
            file.Filename,
            file.Extension,
            file.BitRate,
            file.BitDepth,
            file.SampleRate);

        string? rejectedBecause = null;
        if (SoulseekPeerPolicyService.IsBlockedUser(normalizedUsername, settings))
        {
            rejectedBecause = "blocked_user";
        }
        else if (SoulseekPeerPolicyService.IsBlockedFilename(file.Filename, settings))
        {
            rejectedBecause = "blocked_filename_pattern";
        }
        else if (file.IsLocked)
        {
            rejectedBecause = "file_locked";
        }
        else if (facts.IsJunk || KnownNonAudioExtensions.Contains(GetExtension(file)))
        {
            rejectedBecause = "non_audio_file";
        }
        else if (quality.IsUnknown && !settings.AllowUnknownQuality)
        {
            rejectedBecause = "unknown_quality";
        }
        else if (!enabledQualities.Contains(quality.Code, StringComparer.OrdinalIgnoreCase))
        {
            rejectedBecause = "quality_not_allowed";
        }

        // A file that is not audio is still worth naming. A folder's cover, cue sheet and lyrics are the only
        // artwork and lyrics that will ever exist for a release no streaming service carries, and rejecting
        // them all with one undifferentiated reason is what threw that away.
        var sidecarRole = SoulseekSidecarPolicy.Classify(file.Filename, file.Extension);

        return new SoulseekBrowseFile(
            BuildId(normalizedUsername, normalizedFilename),
            normalizedUsername,
            remoteDirectory,

            // The resolved full path, not the leaf slskd happened to send. This is the file's identity: the
            // batch queue pins it and the engine asks slskd for it by this exact string, so reporting the
            // bare leaf here while reporting a real RemoteDirectory beside it left the two contradicting
            // each other and made every listed file fail the exact-directory check.
            normalizedFilename,
            displayFilename,
            facts.Title,
            facts.Artist,
            facts.Album,
            facts.TrackNumber,
            file.Size,
            file.Length,
            file.BitRate,
            file.BitDepth,
            file.SampleRate,
            quality.Code,
            quality.Label,
            rejectedBecause is null,
            rejectedBecause,
            sidecarRole);
    }

    /// <summary>
    ///     The peer's full path for a listed file, given the directory that was browsed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         slskd answers a directory listing with names <em>relative to the folder that was asked
    ///         for</em>, so a listed file arrives as a bare leaf and carries no directory of its own. The
    ///         browsed folder is the missing half of its identity, and the full path is not decoration: it is
    ///         what the download queue pins and what slskd has to be asked for in order to fetch exactly that
    ///         file. Without it every listed file is rejected as "outside the selected directory", which is
    ///         what made an album's tracks unqueueable the moment a browse actually succeeded.
    ///     </para>
    ///     <para>
    ///         A filename that already carries a directory is left exactly as it is: a full path from a peer
    ///         is authoritative, and rewriting it against the browsed folder would only risk disagreeing with
    ///         the peer about a path that was stated outright.
    ///     </para>
    /// </remarks>
    private static string ResolveRemotePath(string? filename, string? browseDirectory)
    {
        var normalized = SoulseekRemotePath.Normalize(filename);
        if (SoulseekRemotePath.GetDirectory(normalized).Length > 0)
        {
            return normalized;
        }

        var directory = SoulseekRemotePath.Normalize(browseDirectory);

        return directory.Length == 0
            ? normalized
            : $"{directory}/{SoulseekRemotePath.GetLeaf(normalized)}";
    }

    private static string GetExtension(SlskdFile file)
    {
        var extension = NormalizeExtension(file.Extension);
        return extension.Length > 0
            ? extension
            : NormalizeExtension(Path.GetExtension(SoulseekRemotePath.GetLeaf(file.Filename)));
    }

    private static string NormalizeExtension(string? extension)
    {
        var normalized = (extension ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return normalized.StartsWith('.') ? normalized : $".{normalized}";
    }

    private static string BuildId(string username, string filename)
    {
        var identity = $"{username.ToUpperInvariant()}\0{filename.ToUpperInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }
}
