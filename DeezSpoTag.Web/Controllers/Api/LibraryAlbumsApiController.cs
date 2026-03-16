using DeezSpoTag.Services.Library;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/albums")]
[ApiController]
[Authorize]
public class LibraryAlbumsApiController : ControllerBase
{
    private const string AtmosVariant = "atmos";
    private const string StereoVariant = "stereo";
    private readonly LibraryRepository _repository;
    private readonly DeezSpoTag.Web.Services.LibraryConfigStore _configStore;
    private static readonly System.Text.RegularExpressions.Regex SyncedLyricsRegex = new(
        @"\[\d{1,2}:\d{2}([.:]\d{1,2})?\]",
        System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    public LibraryAlbumsApiController(LibraryRepository repository, DeezSpoTag.Web.Services.LibraryConfigStore configStore)
    {
        _repository = repository;
        _configStore = configStore;
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetAlbum(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            var localAlbum = _configStore.GetLocalAlbum(id);
            if (localAlbum is null)
            {
                return NotFound();
            }
            return Ok(new
            {
                localAlbum.Id,
                localAlbum.ArtistId,
                localAlbum.Title,
                PreferredCoverPath = localAlbum.PreferredCoverPath,
                LocalFolders = localAlbum.LocalFolders
            });
        }

        var dbAlbum = await _repository.GetAlbumAsync(id, cancellationToken);
        if (dbAlbum is null)
        {
            return NotFound();
        }

        return Ok(dbAlbum);
    }

    [HttpGet("{id:long}/tracks")]
    public async Task<IActionResult> GetTracks(long id, CancellationToken cancellationToken)
    {
        var audioInfoByTrack = new Dictionary<long, AlbumTrackAudioInfoDto>();
        var audioVariantsByTrack = new Dictionary<long, IReadOnlyList<AlbumTrackAudioInfoDto>>();
        if (_repository.IsConfigured)
        {
            audioInfoByTrack = (await _repository.GetAlbumTrackAudioInfoAsync(id, cancellationToken))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value);
            audioVariantsByTrack = (await _repository.GetAlbumTrackAudioVariantsAsync(id, cancellationToken))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value);
        }

        if (!_repository.IsConfigured)
        {
            var localTracks = _configStore.GetLocalTracks(id);
            return Ok(BuildLocalTrackRows(localTracks, audioInfoByTrack, audioVariantsByTrack));
        }

        var dbTracks = await _repository.GetAlbumTracksAsync(id, cancellationToken);
        var sourceLinks = await _repository.GetAlbumTrackSourceLinksAsync(id, cancellationToken);
        return Ok(BuildDatabaseTrackRows(dbTracks, sourceLinks, audioInfoByTrack, audioVariantsByTrack));
    }

    private static List<object> BuildLocalTrackRows(
        IReadOnlyList<DeezSpoTag.Web.Services.LibraryConfigStore.LibraryTrack> localTracks,
        Dictionary<long, AlbumTrackAudioInfoDto> audioInfoByTrack,
        Dictionary<long, IReadOnlyList<AlbumTrackAudioInfoDto>> audioVariantsByTrack)
    {
        var rows = new List<object>();
        foreach (var track in localTracks)
        {
            audioInfoByTrack.TryGetValue(track.Id, out var audio);
            audioVariantsByTrack.TryGetValue(track.Id, out var variants);
            var orderedVariants = OrderTrackVariants(variants);
            if (orderedVariants.Count == 0)
            {
                rows.Add(BuildTrackRow(track, audio, audio, track.Scan.FilePath, track.Scan.LyricsStatus, 0, true));
                continue;
            }

            var primaryAudioFilePath = GetPrimaryAudioFilePath(audio);
            for (var index = 0; index < orderedVariants.Count; index++)
            {
                var variant = orderedVariants[index];
                var isPrimary = IsPrimaryVariant(primaryAudioFilePath, variant.FilePath, index);
                rows.Add(BuildTrackRow(track, variant, audio, variant.FilePath, track.Scan.LyricsStatus, index + 1, isPrimary));
            }
        }

        return rows;
    }

    private static List<object> BuildDatabaseTrackRows(
        IReadOnlyList<TrackDto> dbTracks,
        IReadOnlyDictionary<long, TrackSourceLinksDto> sourceLinks,
        Dictionary<long, AlbumTrackAudioInfoDto> audioInfoByTrack,
        Dictionary<long, IReadOnlyList<AlbumTrackAudioInfoDto>> audioVariantsByTrack)
    {
        var rows = new List<object>();
        foreach (var track in dbTracks)
        {
            sourceLinks.TryGetValue(track.Id, out var links);
            audioInfoByTrack.TryGetValue(track.Id, out var audio);
            audioVariantsByTrack.TryGetValue(track.Id, out var variants);
            var orderedVariants = OrderTrackVariants(variants);
            if (orderedVariants.Count == 0)
            {
                rows.Add(BuildDatabaseTrackRow(track, links, audio));
                continue;
            }

            var primaryAudioFilePath = GetPrimaryAudioFilePath(audio);
            for (var index = 0; index < orderedVariants.Count; index++)
            {
                var variant = orderedVariants[index];
                rows.Add(BuildDatabaseTrackVariantRow(track, links, audio, variant, index + 1, primaryAudioFilePath));
            }
        }

        return rows;
    }

    private static object BuildDatabaseTrackRow(TrackDto track, TrackSourceLinksDto? links, AlbumTrackAudioInfoDto? audio)
    {
        return new
        {
            track.Id,
            track.AlbumId,
            track.Title,
            track.DurationMs,
            track.Disc,
            track.TrackNo,
            track.AvailableLocally,
            LyricsStatus = ResolveLyricsType(track.LyricsStatus, audio?.FilePath),
            Codec = audio?.Codec,
            Extension = audio?.Extension,
            BitrateKbps = audio?.BitrateKbps,
            SampleRateHz = audio?.SampleRateHz,
            BitsPerSample = audio?.BitsPerSample,
            Channels = audio?.Channels,
            QualityRank = audio?.QualityRank,
            HasStereoVariant = audio?.HasStereoVariant == true,
            HasAtmosVariant = audio?.HasAtmosVariant == true,
            AudioVariant = ResolveAudioVariant(audio, audio?.FilePath),
            VariantKey = BuildVariantKey(track.Id, audio?.AudioFileId, 0),
            FilePath = audio?.FilePath,
            IsPrimaryVariant = true,
            DeezerTrackId = links?.DeezerTrackId,
            SpotifyTrackId = links?.SpotifyTrackId,
            AppleTrackId = links?.AppleTrackId,
            DeezerUrl = links?.DeezerUrl,
            SpotifyUrl = links?.SpotifyUrl,
            AppleUrl = links?.AppleUrl
        };
    }

    private static object BuildDatabaseTrackVariantRow(
        TrackDto track,
        TrackSourceLinksDto? links,
        AlbumTrackAudioInfoDto? primaryAudio,
        AlbumTrackAudioInfoDto variant,
        int variantIndex,
        string? primaryAudioFilePath)
    {
        return new
        {
            track.Id,
            track.AlbumId,
            track.Title,
            track.DurationMs,
            track.Disc,
            track.TrackNo,
            track.AvailableLocally,
            LyricsStatus = ResolveLyricsType(track.LyricsStatus, variant.FilePath),
            Codec = variant.Codec ?? primaryAudio?.Codec,
            Extension = variant.Extension ?? primaryAudio?.Extension,
            BitrateKbps = variant.BitrateKbps ?? primaryAudio?.BitrateKbps,
            SampleRateHz = variant.SampleRateHz ?? primaryAudio?.SampleRateHz,
            BitsPerSample = variant.BitsPerSample ?? primaryAudio?.BitsPerSample,
            Channels = variant.Channels ?? primaryAudio?.Channels,
            QualityRank = variant.QualityRank ?? primaryAudio?.QualityRank,
            HasStereoVariant = variant.HasStereoVariant || primaryAudio?.HasStereoVariant == true,
            HasAtmosVariant = variant.HasAtmosVariant || primaryAudio?.HasAtmosVariant == true,
            AudioVariant = ResolveAudioVariant(
                variant,
                variant.FilePath,
                variant.AudioVariant,
                variant.Codec ?? primaryAudio?.Codec,
                variant.Extension ?? primaryAudio?.Extension,
                variant.Channels ?? primaryAudio?.Channels),
            VariantKey = BuildVariantKey(track.Id, variant.AudioFileId, variantIndex),
            FilePath = variant.FilePath,
            IsPrimaryVariant = IsPrimaryVariant(primaryAudioFilePath, variant.FilePath, variantIndex - 1),
            DeezerTrackId = links?.DeezerTrackId,
            SpotifyTrackId = links?.SpotifyTrackId,
            AppleTrackId = links?.AppleTrackId,
            DeezerUrl = links?.DeezerUrl,
            SpotifyUrl = links?.SpotifyUrl,
            AppleUrl = links?.AppleUrl
        };
    }

    private static List<AlbumTrackAudioInfoDto> OrderTrackVariants(IReadOnlyList<AlbumTrackAudioInfoDto>? variants)
    {
        return variants?
            .Where(variant => !string.IsNullOrWhiteSpace(variant.FilePath))
            .OrderBy(variant => GetAudioVariantSortOrder(ResolveAudioVariant(variant, variant.FilePath)))
            .ThenByDescending(variant => variant.QualityRank ?? int.MinValue)
            .ToList() ?? new List<AlbumTrackAudioInfoDto>();
    }

    private static string? GetPrimaryAudioFilePath(AlbumTrackAudioInfoDto? audio)
    {
        return string.IsNullOrWhiteSpace(audio?.FilePath) ? null : audio.FilePath;
    }

    private static bool IsPrimaryVariant(string? primaryAudioFilePath, string? variantFilePath, int index)
    {
        return primaryAudioFilePath is null
            ? index == 0
            : string.Equals(variantFilePath, primaryAudioFilePath, StringComparison.OrdinalIgnoreCase);
    }

    private static object BuildTrackRow(
        DeezSpoTag.Web.Services.LibraryConfigStore.LibraryTrack track,
        AlbumTrackAudioInfoDto? variantAudio,
        AlbumTrackAudioInfoDto? primaryAudio,
        string? filePath,
        string? rawLyricsStatus,
        int variantIndex,
        bool isPrimaryVariant)
    {
        var selectedAudio = variantAudio ?? primaryAudio;
        var scan = track.Scan;
        return new
        {
            track.Id,
            track.AlbumId,
            Title = scan.Title,
            DurationMs = scan.DurationMs,
            Disc = scan.Disc,
            TrackNo = scan.TrackNo,
            track.AvailableLocally,
            LyricsStatus = ResolveLyricsType(rawLyricsStatus, filePath),
            Codec = scan.Codec ?? selectedAudio?.Codec,
            Extension = selectedAudio?.Extension,
            BitrateKbps = scan.BitrateKbps ?? selectedAudio?.BitrateKbps,
            SampleRateHz = scan.SampleRateHz ?? selectedAudio?.SampleRateHz,
            BitsPerSample = scan.BitsPerSample ?? selectedAudio?.BitsPerSample,
            Channels = scan.Channels ?? selectedAudio?.Channels,
            QualityRank = scan.QualityRank ?? selectedAudio?.QualityRank,
            HasStereoVariant = selectedAudio?.HasStereoVariant == true,
            HasAtmosVariant = selectedAudio?.HasAtmosVariant == true,
            AudioVariant = ResolveAudioVariant(
                selectedAudio,
                filePath,
                scan.AudioVariant,
                scan.Codec ?? selectedAudio?.Codec,
                selectedAudio?.Extension,
                scan.Channels ?? selectedAudio?.Channels),
            VariantKey = BuildVariantKey(track.Id, selectedAudio?.AudioFileId, variantIndex),
            FilePath = filePath,
            IsPrimaryVariant = isPrimaryVariant,
            DeezerTrackId = scan.DeezerTrackId,
            SpotifyTrackId = scan.SpotifyTrackId,
            AppleTrackId = scan.AppleTrackId,
            DeezerUrl = !string.IsNullOrWhiteSpace(scan.DeezerTrackId) ? $"https://www.deezer.com/track/{scan.DeezerTrackId}" : null,
            SpotifyUrl = !string.IsNullOrWhiteSpace(scan.SpotifyTrackId) ? $"https://open.spotify.com/track/{scan.SpotifyTrackId}" : null,
            AppleUrl = scan.TagUrl
        };
    }

    private static string ResolveAudioVariant(
        AlbumTrackAudioInfoDto? audio,
        string? filePath,
        string? variantOverride = null,
        string? codecOverride = null,
        string? extensionOverride = null,
        int? channelsOverride = null)
    {
        var explicitVariant = NormalizeAudioVariant(variantOverride) ?? NormalizeAudioVariant(audio?.AudioVariant);
        if (!string.IsNullOrWhiteSpace(explicitVariant))
        {
            return explicitVariant;
        }

        var channels = channelsOverride ?? audio?.Channels;
        var codec = codecOverride ?? audio?.Codec;
        var extension = extensionOverride ?? audio?.Extension;

        if (AudioVariantResolver.IsAtmosVariant(channels, codec, extension, filePath))
        {
            return AtmosVariant;
        }

        if (audio?.HasAtmosVariant == true && !audio.HasStereoVariant)
        {
            return AtmosVariant;
        }

        if (audio?.HasStereoVariant == true && !audio.HasAtmosVariant)
        {
            return StereoVariant;
        }

        if (!channels.HasValue)
        {
            return StereoVariant;
        }

        return StereoVariant;
    }

    private static string? NormalizeAudioVariant(string? value)
        => AudioVariantResolver.NormalizeAudioVariant(value);

    private static string BuildVariantKey(long trackId, long? audioFileId, int fallbackIndex)
    {
        return audioFileId.HasValue && audioFileId.Value > 0
            ? $"{trackId}:{audioFileId.Value}"
            : $"{trackId}:{fallbackIndex}";
    }

    private static int GetAudioVariantSortOrder(string? variant)
    {
        var normalized = (variant ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            StereoVariant => 0,
            AtmosVariant => 1,
            _ => 2
        };
    }

    private static string ResolveLyricsType(string? rawStatus, string? filePath)
    {
        var state = ParseLyricsPresenceFromStatus(rawStatus);
        ApplySidecarLyricsPresence(filePath, state);
        DetectEmbeddedLyrics(filePath, state);
        return MapLyricsPresenceToStatus(state);
    }

    private static LyricsPresenceState ParseLyricsPresenceFromStatus(string? rawStatus)
    {
        var state = new LyricsPresenceState();
        var normalized = (rawStatus ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return state;
        }

        state.HasTtml = normalized.Contains("ttml", StringComparison.Ordinal);

        var hasBothMarkers = normalized.Contains("both", StringComparison.Ordinal)
            || normalized.Contains("embedded", StringComparison.Ordinal);
        if (hasBothMarkers)
        {
            state.HasSynced = true;
            state.HasUnsynced = true;
        }

        if (normalized.Contains("unsynced", StringComparison.Ordinal))
        {
            state.HasUnsynced = true;
        }

        if (normalized.Contains("synced", StringComparison.Ordinal) && !normalized.Contains("unsynced", StringComparison.Ordinal))
        {
            state.HasSynced = true;
        }

        if (normalized.Contains("lrc", StringComparison.Ordinal))
        {
            state.HasSynced = true;
        }

        return state;
    }

    private static void ApplySidecarLyricsPresence(string? filePath, LyricsPresenceState state)
    {
        state.HasTtml = state.HasTtml || HasSidecarFile(filePath, ".ttml");
        state.HasSynced = state.HasSynced || HasSidecarFile(filePath, ".lrc");
        state.HasUnsynced = state.HasUnsynced || HasSidecarFile(filePath, ".txt");
    }

    private static string MapLyricsPresenceToStatus(LyricsPresenceState state)
    {
        return (state.HasTtml, state.HasSynced, state.HasUnsynced) switch
        {
            (true, true, true) => "ttml_lrc_txt",
            (true, true, false) => "ttml_lrc",
            (true, false, true) => "ttml_txt",
            (true, false, false) => "ttml",
            (false, true, true) => "lrc_txt",
            (false, true, false) => "lrc",
            (false, false, true) => "txt",
            _ => "none"
        };
    }

    private static void DetectEmbeddedLyrics(string? audioFilePath, LyricsPresenceState state)
    {
        if (state == null || string.IsNullOrWhiteSpace(audioFilePath) || !System.IO.File.Exists(audioFilePath))
        {
            return;
        }

        try
        {
            using var tagFile = TagLib.File.Create(audioFilePath);
            var embeddedLyrics = tagFile.Tag.Lyrics;
            if (string.IsNullOrWhiteSpace(embeddedLyrics))
            {
                return;
            }

            var lyrics = embeddedLyrics.Trim();
            if (lyrics.Contains("<tt", StringComparison.OrdinalIgnoreCase) || lyrics.Contains("</tt>", StringComparison.OrdinalIgnoreCase))
            {
                state.HasTtml = true;
                return;
            }

            if (SyncedLyricsRegex.IsMatch(lyrics))
            {
                state.HasSynced = true;
                return;
            }

            state.HasUnsynced = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            // Fall back to persisted status + sidecar checks.
        }
    }

    private static bool HasSidecarFile(string? audioFilePath, string extension)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(audioFilePath);
        var stem = Path.GetFileNameWithoutExtension(audioFilePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(stem) || !Directory.Exists(directory))
        {
            return false;
        }

        var directPath = Path.Join(directory, $"{stem}{extension}");
        if (System.IO.File.Exists(directPath))
        {
            return true;
        }

        try
        {
            if (Directory
                .EnumerateFiles(directory, $"{stem}.*")
                .Any(candidate => string.Equals(Path.GetExtension(candidate), extension, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return false;
        }

        return false;
    }

    private sealed class LyricsPresenceState
    {
        public bool HasSynced { get; set; }
        public bool HasUnsynced { get; set; }
        public bool HasTtml { get; set; }
    }
}
