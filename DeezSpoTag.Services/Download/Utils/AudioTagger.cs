using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Models.Download;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Crypto;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Diagnostics;
using TagLib;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Services.Download.Utils;

/// <summary>
/// Consolidated audio processing utilities merging AudioTagger and BitrateSelector
/// EXACT PORT from deezspotag tagger.ts and getPreferredBitrate.ts
/// </summary>
public class AudioTagger
{
    private const string DefaultMultiArtistSeparator = "default";
    private const string UnknownArtist = "Unknown Artist";
    private const string UnknownValue = "Unknown";
    private const string NothingSeparator = "nothing";
    private const string AppleDigitalMasterTag = "APPLE_DIGITAL_MASTER";
    private const string ComposerRole = "composer";
    private const string CoverDescription = "cover";
    private const string LyricsKey = "lyrics";
    private const string DeezerSource = "deezer";
    private const string SpotifySource = "spotify";
    private const string AppleSource = "apple";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly ILogger<AudioTagger> _logger;
    private readonly PlatformCapabilitiesStore _capabilitiesStore;
    private readonly DeezSpoTagSettingsService _settingsService;
    private IReadOnlyDictionary<string, string>? _genreAliasMap;
    private bool _genreTagNormalizationEnabled;

    private static readonly HashSet<string> SupportedContributorRoles = new(StringComparer.Ordinal)
    {
        "AUTHOR",
        "ENGINEER",
        "MIXER",
        "PRODUCER",
        "WRITER",
        "COMPOSER"
    };
    private static readonly HashSet<string> Id3SupportedContributorRoles = new(StringComparer.Ordinal)
    {
        "author",
        "engineer",
        "mixer",
        "producer",
        "writer"
    };
    private static readonly string[] CompilationEnabledValue = { "1" };

    private static readonly HashSet<string> Id3LikeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".aif", ".aiff"
    };

    private static readonly HashSet<string> XiphLikeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".ogg", ".opus", ".oga"
    };

    private static readonly HashSet<string> Mp4LikeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4a", ".m4b"
    };
    private static readonly HashSet<string> BlockedGenres = new(StringComparer.OrdinalIgnoreCase)
    {
        "other",
        "others"
    };

    private static readonly Regex LrcLineRegex = new(
        @"^\s*\[(\d{1,2}):(\d{2})(?:[.:](\d{1,3}))?\](.*)$",
        RegexOptions.Compiled,
        RegexTimeout);
    private static readonly Lazy<string?> FfmpegPath = new(ResolveFfmpegPath);

    public AudioTagger(
        ILogger<AudioTagger> logger,
        PlatformCapabilitiesStore capabilitiesStore,
        DeezSpoTagSettingsService settingsService)
    {
        _logger = logger;
        _capabilitiesStore = capabilitiesStore;
        _settingsService = settingsService;
    }

    private static string ResolvePrimaryAlbumArtist(DeezSpoTag.Core.Models.Track track)
    {
        if (!string.IsNullOrWhiteSpace(track.Album?.MainArtist?.Name))
        {
            return track.Album.MainArtist.Name;
        }

        var mainArtists = track.Artist.GetValueOrDefault("Main", new List<string>());
        if (mainArtists.Count > 0 && !string.IsNullOrWhiteSpace(mainArtists[0]))
        {
            return mainArtists[0];
        }

        if (!string.IsNullOrWhiteSpace(track.MainArtist?.Name))
        {
            return track.MainArtist.Name;
        }

        return UnknownArtist;
    }

    private string[] SanitizeGenres(IEnumerable<string>? values)
    {
        var aliasMap = GetGenreAliasMap();
        var splitComposite = IsGenreTagNormalizationEnabled();
        return GenreTagAliasNormalizer.NormalizeAndExpandValues(values, aliasMap, splitComposite)
            .Where(value => !BlockedGenres.Contains(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ;
    }

    private bool IsGenreTagNormalizationEnabled()
    {
        _ = GetGenreAliasMap();
        return _genreTagNormalizationEnabled;
    }

    private IReadOnlyDictionary<string, string> GetGenreAliasMap()
    {
        if (_genreAliasMap != null)
        {
            return _genreAliasMap;
        }

        try
        {
            var settings = _settingsService.LoadSettings();
            _genreTagNormalizationEnabled = settings.NormalizeGenreTags;
            _genreAliasMap = settings.NormalizeGenreTags
                ? GenreTagAliasNormalizer.BuildAliasMap(settings.GenreTagAliasRules)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load genre alias map; continuing without genre alias normalization.");
            _genreTagNormalizationEnabled = false;
            _genreAliasMap = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return _genreAliasMap;
    }

    #region Audio Tagging (from AudioTagger)

    /// <summary>
    /// Tag track with metadata (exact port from deezspotag tagTrack function)
    /// </summary>
    public async Task TagTrackAsync(string extension, string writePath, DeezSpoTag.Core.Models.Track track, TagSettings tags)
    {
        var normalizedExtension = string.IsNullOrWhiteSpace(extension)
            ? Path.GetExtension(writePath).ToLowerInvariant()
            : extension.Trim().ToLowerInvariant();

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Tagging track: {FilePath} with extension: {Extension}", writePath, normalizedExtension);        }
        UpdateDownloadCapabilities(track);

        const int maxAttempts = 5;
        const int delayMs = 200;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (Id3LikeExtensions.Contains(normalizedExtension))
                {
                    await TagMP3Async(writePath, track, tags);
                }
                else if (XiphLikeExtensions.Contains(normalizedExtension))
                {
                    await TagFLACAsync(writePath, track, tags);
                }
                else if (Mp4LikeExtensions.Contains(normalizedExtension))
                {
                    await TagMP4Async(writePath, track, tags);
                }
                else
                {
                    await TagGenericAsync(writePath, track, tags);
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Successfully tagged track: {FilePath}", writePath);                }
                return;
            }
            catch (IOException ex) when (attempt < maxAttempts)
            {
                _logger.LogWarning(ex, "Tagging failed (attempt {Attempt}/{MaxAttempts}) for {FilePath}, retrying", attempt, maxAttempts, writePath);
                await Task.Delay(delayMs);
            }
        }

        throw new IOException($"Tagging failed after {maxAttempts} attempts for {writePath}");
    }

    /// <summary>
    /// Tag track with metadata (Downloader compatibility method)
    /// </summary>
    public async Task TagTrackAsync(string writePath, DeezSpoTag.Core.Models.Track track, DeezSpoTagSettings settings)
    {
        // Determine extension from file path
        var extension = Path.GetExtension(writePath);

        // Use the existing method with the settings tags
        await TagTrackAsync(extension, writePath, track, settings.Tags);
    }

    /// <summary>
    /// Tag MP3 file (exact port from deezspotag tagID3 function)
    /// </summary>
    private async Task TagMP3Async(string path, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.GetTag(TagTypes.Id3v2, true);

            // Clear existing tags
            tag.Clear();

            ApplyMp3CoreMetadata(tag, track, save);
            ApplyMp3AdditionalMetadata(tag, track, save);
            ApplyMp3LyricsMetadata(tag, track, save);
            ApplyMp3ContributorMetadata(tag, track, save);
            ApplyMp3OwnershipAndCompilationMetadata(tag, track, save);
            ApplyMp3SourceMetadata(tag, track, save);

            if (save.Cover)
            {
                await AttachCoverArtAsync(tag, track.Album?.EmbeddedCoverPath, save);
            }

            file.Save();
            RemoveId3v1WhenDisabled(file, save);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Successfully tagged MP3 file: {Path}", path);            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Failed to tag MP3 file: {path}", ex);
        }
    }

    private void ApplyMp3CoreMetadata(Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (save.Title)
        {
            tag.Title = track.Title;
        }

        ApplyMp3PerformerTags(tag, track, save);

        if (save.Album)
        {
            tag.Album = track.Album?.Title;
        }

        if (save.AlbumArtist)
        {
            tag.AlbumArtists = new[] { ResolvePrimaryAlbumArtist(track) };
        }

        ApplyMp3TrackAndDiscMetadata(tag, track, save);
        ApplyMp3GenreYearAndDateMetadata(tag, track, save);
    }

    private void ApplyMp3PerformerTags(Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (!save.Artist || track.Artists.Count == 0)
        {
            return;
        }

        if (save.MultiArtistSeparator == DefaultMultiArtistSeparator)
        {
            tag.Performers = track.Artists.ToArray();
            if (save.UseNullSeparator && tag is TagLib.Id3v2.Tag id3Tag)
            {
                var frame = TagLib.Id3v2.TextInformationFrame.Get(id3Tag, "TPE1", true);
                frame.TextEncoding = TagLib.StringType.UTF16;
                frame.Text = track.Artists.ToArray();
            }
            return;
        }

        tag.Performers = save.MultiArtistSeparator == NothingSeparator
            ? new[] { track.MainArtist?.Name ?? UnknownValue }
            : new[] { track.ArtistsString };

        if (save.Artists)
        {
            SetCustomFrame(tag, "TXXX", "ARTISTS", ResolveMultiArtistValue(track, save), save);
        }
    }

    private static void ApplyMp3TrackAndDiscMetadata(Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (save.TrackNumber)
        {
            tag.Track = (uint)track.TrackNumber;
            if (save.TrackTotal && track.Album != null)
            {
                tag.TrackCount = (uint)track.Album.TrackTotal;
            }
        }

        if (save.DiscNumber)
        {
            tag.Disc = (uint)track.DiscNumber;
            var discTotal = track.Album?.DiscTotal;
            if (save.DiscTotal && discTotal.HasValue)
            {
                tag.DiscCount = (uint)discTotal.Value;
            }
        }
    }

    private void ApplyMp3GenreYearAndDateMetadata(Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (save.Genre && track.Album?.Genre is { Count: > 0 })
        {
            tag.Genres = SanitizeGenres(track.Album.Genre);
        }

        if (save.Year && !string.IsNullOrEmpty(track.Date.Year) && uint.TryParse(track.Date.Year, out var year))
        {
            tag.Year = year;
        }

        if (save.Date)
        {
            var dateString = $"{track.Date.Day}{track.Date.Month}";
            SetCustomFrame(tag, "TDAT", "", dateString, save);
        }
    }

    private void ApplyMp3AdditionalMetadata(Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        SetId3FrameIf(tag, save.Length, "TLEN", "", (track.Duration * 1000).ToString(CultureInfo.InvariantCulture), save);
        SetId3FrameIf(tag, save.Bpm && track.Bpm > 0, "TBPM", "", track.Bpm.ToString(CultureInfo.InvariantCulture), save);
        SetId3FrameIf(tag, save.Key, "TKEY", "", track.Key, save);

        WriteMp3FeatureFrame(tag, save.Danceability, "DANCEABILITY", track.Danceability, save);
        WriteMp3FeatureFrame(tag, save.Energy, "ENERGY", track.Energy, save);
        WriteMp3FeatureFrame(tag, save.Valence, "VALENCE", track.Valence, save);
        WriteMp3FeatureFrame(tag, save.Acousticness, "ACOUSTICNESS", track.Acousticness, save);
        WriteMp3FeatureFrame(tag, save.Instrumentalness, "INSTRUMENTALNESS", track.Instrumentalness, save);
        WriteMp3FeatureFrame(tag, save.Speechiness, "SPEECHINESS", track.Speechiness, save);
        WriteMp3FeatureFrame(tag, save.Loudness, "LOUDNESS", track.Loudness, save);
        WriteMp3FeatureFrame(tag, save.Tempo, "TEMPO", track.Tempo, save);
        WriteMp3FeatureFrame(tag, save.Liveness, "LIVENESS", track.Liveness, save);

        SetId3FrameIf(
            tag,
            save.TimeSignature && track.TimeSignature.HasValue,
            "TXXX",
            "TIME_SIGNATURE",
            track.TimeSignature?.ToString(CultureInfo.InvariantCulture),
            save);
        SetId3FrameIf(tag, save.Label, "TPUB", "", track.Album?.Label, save);
        SetId3FrameIf(tag, save.Isrc, "TSRC", "", track.ISRC, save);
        SetId3FrameIf(tag, save.Barcode, "TXXX", "BARCODE", track.Album?.Barcode, save);
        SetId3FrameIf(tag, save.Explicit, "TXXX", "ITUNESADVISORY", track.Explicit ? "1" : "0", save);
        SetId3FrameIf(tag, save.ReplayGain, "TXXX", "REPLAYGAIN_TRACK_GAIN", track.ReplayGain, save);

        if (TryGetAppleDigitalMasterMarker(track, out var appleDigitalMasterMarker))
        {
            SetCustomFrame(tag, "TXXX", AppleDigitalMasterTag, appleDigitalMasterMarker, save);
        }
    }

    private void ApplyMp3LyricsMetadata(Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (save.Lyrics && !string.IsNullOrEmpty(track.Lyrics?.Unsync))
        {
            SetLyricsFrame(tag, track.Lyrics.Unsync);
        }

        if (save.SyncedLyrics && track.Lyrics != null)
        {
            SetSyncedLyricsFrame(tag, track.Lyrics);
        }
    }

    private void ApplyMp3ContributorMetadata(Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (!save.InvolvedPeople || track.Contributors.Count == 0)
        {
            return;
        }

        var involvedPeople = new List<string>();
        foreach (var contributor in track.Contributors)
        {
            var role = contributor.Key;
            if (Id3SupportedContributorRoles.Contains(role) && contributor.Value is List<string> people)
            {
                foreach (var person in people)
                {
                    involvedPeople.Add($"{role}:{person}");
                }

                continue;
            }

            if (role == ComposerRole && save.Composer && contributor.Value is List<string> composers)
            {
                SetCustomFrame(tag, "TCOM", "", string.Join(", ", composers), save);
            }
        }

        if (involvedPeople.Count > 0)
        {
            SetCustomFrame(tag, "TXXX", "INVOLVEDPEOPLE", string.Join("; ", involvedPeople), save);
        }
    }

    private void ApplyMp3OwnershipAndCompilationMetadata(Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        SetId3FrameIf(tag, save.Copyright, "TCOP", "", track.Copyright, save);

        if ((save.SavePlaylistAsCompilation && track.Playlist != null) || track.Album?.RecordType == "compile")
        {
            SetCustomFrame(tag, "TCMP", "", "1", save);
        }
    }

    private void ApplyMp3SourceMetadata(Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        var sourceId = ResolveSourceId(track);
        if (save.Source)
        {
            SetCustomFrame(tag, "TXXX", "SOURCE", ResolveSourceName(track), save);
            if (!string.IsNullOrWhiteSpace(sourceId))
            {
                SetCustomFrame(tag, "TXXX", "SOURCEID", sourceId, save);
            }
        }

        SetId3FrameIf(tag, save.Url, "TXXX", "WWWAUDIOFILE", ResolveTrackUrl(track), save);

        if (save.TrackId)
        {
            if (!string.IsNullOrWhiteSpace(sourceId))
            {
                SetCustomFrame(tag, "TXXX", $"{ResolveSourceTagPrefix(track)}_TRACK_ID", sourceId, save);
            }

            SetCustomFrameIfPresent(tag, "TXXX", "SPOTIFY_TRACK_ID", ResolveSpotifyTrackId(track), save);
            SetCustomFrameIfPresent(tag, "TXXX", "DEEZER_TRACK_ID", ResolveDeezerTrackId(track), save);
            SetCustomFrameIfPresent(tag, "TXXX", "APPLE_TRACK_ID", ResolveAppleTrackId(track), save);
        }

        if (save.ReleaseId)
        {
            var releaseId = ResolveReleaseId(track);
            if (!string.IsNullOrWhiteSpace(releaseId))
            {
                SetCustomFrame(tag, "TXXX", $"{ResolveSourceTagPrefix(track)}_RELEASE_ID", releaseId, save);
            }
        }
    }

    private static void RemoveId3v1WhenDisabled(TagLib.File file, TagSettings save)
    {
        if (save.SaveID3v1)
        {
            return;
        }

        file.RemoveTags(TagTypes.Id3v1);
        file.Save();
    }

    private void WriteMp3FeatureFrame(Tag tag, bool enabled, string description, double? value, TagSettings save)
    {
        if (!enabled || !value.HasValue)
        {
            return;
        }

        SetCustomFrame(tag, "TXXX", description, FormatAudioFeature(value.Value), save);
    }

    private void SetId3FrameIf(
        Tag tag,
        bool condition,
        string frameId,
        string description,
        string? value,
        TagSettings save)
    {
        if (condition && !string.IsNullOrWhiteSpace(value))
        {
            SetCustomFrame(tag, frameId, description, value, save);
        }
    }

    /// <summary>
    /// Tag FLAC file (exact port from deezspotag tagFLAC function)
    /// </summary>
    private async Task TagFLACAsync(string path, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.GetTag(TagTypes.Xiph, true);

            // Clear existing tags
            tag.Clear();

            ApplyFlacCoreMetadata(tag, track, save);
            ApplyFlacAdditionalMetadata(tag, track, save);
            ApplyFlacContributorMetadata(tag, track, save);
            ApplyFlacOwnershipAndCompilationMetadata(tag, track, save);
            ApplyFlacSourceMetadata(tag, track, save);
            ApplyFlacRatingMetadata(tag, track, save);

            if (save.Cover)
            {
                await AttachCoverArtAsync(tag, track.Album?.EmbeddedCoverPath, save);
            }

            file.Save();
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Successfully tagged FLAC file: {Path}", path);            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Failed to tag FLAC file: {path}", ex);
        }
    }

    /// <summary>
    /// Tag MP4 file (implementation for MP4/M4A files)
    /// </summary>
    private async Task TagMP4Async(string path, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        var ffmpegPath = FfmpegPath.Value;
        var isAtmos = IsAtmosCodecMp4(path);
        var isFragmented = FragmentedMp4DurationReader.IsFragmentedMp4(path);

        if (isAtmos || isFragmented)
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath))
            {
                var reason = isAtmos ? "Atmos" : "fragmented";
                throw new IOException($"ffmpeg is required for {reason} MP4 tagging: {path}");
            }

            var taggedWithFfmpeg = await TryTagMP4WithFfmpegAsync(ffmpegPath, path, track, save);
            if (!taggedWithFfmpeg)
            {
                var reason = isAtmos ? "Atmos" : "fragmented";
                throw new IOException($"Failed to persist {reason} MP4 tags with ffmpeg for {path}");
            }

            if (!VerifyMp4TagPersistence(path, track, save))
            {
                var reason = isAtmos ? "Atmos" : "fragmented";
                throw new IOException($"{reason} MP4 tag verification failed for {path}");
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                if (isAtmos)
                {
                    _logger.LogInformation("Successfully tagged Atmos MP4 file with ffmpeg: {Path}", path);
                }
                else
                {
                    _logger.LogInformation("Successfully tagged fragmented MP4 file with ffmpeg: {Path}", path);
                }
            }
            return;
        }

        await TagMP4WithTagLibAsync(path, track, save);
        if (VerifyMp4TagPersistence(path, track, save))
        {
            return;
        }

        _logger.LogWarning("TagLib MP4 tagging completed but expected metadata is missing for {Path}", path);
        throw new IOException($"Failed to persist MP4 tags for {path}");
    }

    private async Task TagMP4WithTagLibAsync(string path, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.GetTag(TagTypes.Apple, true);
            var appleTag = tag as TagLib.Mpeg4.AppleTag;

            // Clear existing tags
            tag.Clear();

            ApplyMp4CoreMetadata(tag, appleTag, track, save);
            ApplyMp4AdditionalMetadata(tag, appleTag, track, save);
            ApplyMp4ContributorMetadata(appleTag, track, save);
            ApplyMp4OwnershipAndCompilationMetadata(tag, appleTag, track, save);
            ApplyMp4SourceMetadata(appleTag, track, save);
            ApplyMp4RatingMetadata(appleTag, track, save);

            if (save.Cover)
            {
                await AttachCoverArtAsync(tag, track.Album?.EmbeddedCoverPath, save);
            }

            file.Save();
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Successfully tagged MP4 file: {Path}", path);            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Failed to tag MP4 file: {path}", ex);
        }
    }

    private void ApplyFlacCoreMetadata(Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (save.Title)
        {
            tag.Title = track.Title;
        }

        ApplyFlacOrMp4PerformerTags(tag, track, save, values => SetVorbisComment(tag, "ARTISTS", values));

        if (save.Album)
        {
            tag.Album = track.Album?.Title;
        }

        ApplyCommonAlbumArtistAndSequenceTags(tag, track, save);

        if (save.Genre && track.Album?.Genre is { Count: > 0 })
        {
            tag.Genres = SanitizeGenres(track.Album.Genre);
        }

        if (save.Date)
        {
            SetVorbisComment(tag, "DATE", new[] { track.DateString });
        }
        else
        {
            SetVorbisCommentIf(tag, save.Year, "DATE", track.Date?.Year);
        }
    }

    private void ApplyFlacAdditionalMetadata(Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        SetVorbisCommentIf(tag, save.Length, "LENGTH", (track.Duration * 1000).ToString(CultureInfo.InvariantCulture));
        SetVorbisCommentIf(tag, save.Bpm && track.Bpm > 0, "BPM", track.Bpm.ToString(CultureInfo.InvariantCulture));
        SetVorbisCommentIf(tag, save.Key, "INITIALKEY", track.Key);
        SetVorbisCommentIf(tag, save.Danceability && track.Danceability.HasValue, "DANCEABILITY", FormatAudioFeature(track.Danceability ?? 0));
        SetVorbisCommentIf(tag, save.Energy && track.Energy.HasValue, "ENERGY", FormatAudioFeature(track.Energy ?? 0));
        SetVorbisCommentIf(tag, save.Valence && track.Valence.HasValue, "VALENCE", FormatAudioFeature(track.Valence ?? 0));
        SetVorbisCommentIf(tag, save.Acousticness && track.Acousticness.HasValue, "ACOUSTICNESS", FormatAudioFeature(track.Acousticness ?? 0));
        SetVorbisCommentIf(tag, save.Instrumentalness && track.Instrumentalness.HasValue, "INSTRUMENTALNESS", FormatAudioFeature(track.Instrumentalness ?? 0));
        SetVorbisCommentIf(tag, save.Speechiness && track.Speechiness.HasValue, "SPEECHINESS", FormatAudioFeature(track.Speechiness ?? 0));
        SetVorbisCommentIf(tag, save.Loudness && track.Loudness.HasValue, "LOUDNESS", FormatAudioFeature(track.Loudness ?? 0));
        SetVorbisCommentIf(tag, save.Tempo && track.Tempo.HasValue, "TEMPO", FormatAudioFeature(track.Tempo ?? 0));
        SetVorbisCommentIf(
            tag,
            save.TimeSignature && track.TimeSignature.HasValue,
            "TIME_SIGNATURE",
            track.TimeSignature?.ToString(CultureInfo.InvariantCulture));
        SetVorbisCommentIf(tag, save.Liveness && track.Liveness.HasValue, "LIVENESS", FormatAudioFeature(track.Liveness ?? 0));
        SetVorbisCommentIf(tag, save.Label, "PUBLISHER", track.Album?.Label);
        SetVorbisCommentIf(tag, save.Isrc, "ISRC", track.ISRC);
        SetVorbisCommentIf(tag, save.Barcode, "BARCODE", track.Album?.Barcode);
        SetVorbisCommentIf(tag, save.Explicit, "ITUNESADVISORY", track.Explicit ? "1" : "0");
        SetVorbisCommentIf(tag, save.ReplayGain, "REPLAYGAIN_TRACK_GAIN", track.ReplayGain);

        if (TryGetAppleDigitalMasterMarker(track, out var appleDigitalMasterMarker))
        {
            SetVorbisComment(tag, AppleDigitalMasterTag, new[] { appleDigitalMasterMarker });
        }

        SetVorbisCommentIf(tag, save.Lyrics, "LYRICS", track.Lyrics?.Unsync);

        if (save.SyncedLyrics && TryGetSyncedLyricsText(track.Lyrics, out var syncedLyricsText))
        {
            SetVorbisComment(tag, "LYRICS_SYNCED", new[] { syncedLyricsText });
        }
    }

    private void ApplyFlacContributorMetadata(Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (track.Contributors.Count == 0)
        {
            return;
        }

        foreach (var contributor in track.Contributors)
        {
            var role = contributor.Key.ToUpperInvariant();
            if (SupportedContributorRoles.Contains(role)
                && ShouldWriteContributorRole(save, role)
                && contributor.Value is List<string> people)
            {
                SetVorbisComment(tag, role, people);
                continue;
            }

            if (role == "MUSICPUBLISHER"
                && save.InvolvedPeople
                && contributor.Value is List<string> publishers)
            {
                SetVorbisComment(tag, "ORGANIZATION", publishers);
            }
        }
    }

    private void ApplyFlacOwnershipAndCompilationMetadata(Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        SetVorbisCommentIf(tag, save.Copyright, "COPYRIGHT", track.Copyright);

        if ((save.SavePlaylistAsCompilation && track.Playlist != null) || track.Album?.RecordType == "compile")
        {
            SetVorbisComment(tag, "COMPILATION", CompilationEnabledValue);
        }
    }

    private void ApplyFlacSourceMetadata(Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        var sourceId = ResolveSourceId(track);
        if (save.Source)
        {
            SetVorbisComment(tag, "SOURCE", new[] { ResolveSourceName(track) });
            if (!string.IsNullOrWhiteSpace(sourceId))
            {
                SetVorbisComment(tag, "SOURCEID", new[] { sourceId });
            }
        }

        SetVorbisCommentIf(tag, save.Url, "WWWAUDIOFILE", ResolveTrackUrl(track));

        if (save.TrackId)
        {
            if (!string.IsNullOrWhiteSpace(sourceId))
            {
                SetVorbisComment(tag, $"{ResolveSourceTagPrefix(track)}_TRACK_ID", new[] { sourceId });
            }

            SetVorbisCommentIf(tag, true, "SPOTIFY_TRACK_ID", ResolveSpotifyTrackId(track));
            SetVorbisCommentIf(tag, true, "DEEZER_TRACK_ID", ResolveDeezerTrackId(track));
            SetVorbisCommentIf(tag, true, "APPLE_TRACK_ID", ResolveAppleTrackId(track));
        }

        if (save.ReleaseId)
        {
            var releaseId = ResolveReleaseId(track);
            if (!string.IsNullOrWhiteSpace(releaseId))
            {
                SetVorbisComment(tag, $"{ResolveSourceTagPrefix(track)}_RELEASE_ID", new[] { releaseId });
            }
        }
    }

    private void ApplyFlacRatingMetadata(Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (!save.Rating || track.Rank <= 0)
        {
            return;
        }

        var rank = Math.Round(track.Rank / 10000.0);
        SetVorbisComment(tag, "RATING", new[] { rank.ToString(CultureInfo.InvariantCulture) });
    }

    private void ApplyMp4CoreMetadata(
        Tag tag,
        TagLib.Mpeg4.AppleTag? appleTag,
        DeezSpoTag.Core.Models.Track track,
        TagSettings save)
    {
        if (save.Title)
        {
            tag.Title = track.Title;
        }

        ApplyFlacOrMp4PerformerTags(tag, track, save, values => TrySetAppleDashBox(appleTag, "ARTISTS", values));

        if (save.Album)
        {
            tag.Album = track.Album?.Title;
        }

        ApplyCommonAlbumArtistAndSequenceTags(tag, track, save);

        if (save.Genre && track.Album?.Genre is { Count: > 0 })
        {
            tag.Genres = SanitizeGenres(track.Album.Genre);
        }

        if (save.Year && !string.IsNullOrEmpty(track.Date?.Year) && uint.TryParse(track.Date.Year, out var year))
        {
            tag.Year = year;
        }
    }

    private void ApplyMp4AdditionalMetadata(
        Tag tag,
        TagLib.Mpeg4.AppleTag? appleTag,
        DeezSpoTag.Core.Models.Track track,
        TagSettings save)
    {
        ApplyMp4FeatureMetadata(appleTag, track, save);
        ApplyMp4IdentityMetadata(tag, appleTag, track, save);
    }

    private void ApplyMp4FeatureMetadata(
        TagLib.Mpeg4.AppleTag? appleTag,
        DeezSpoTag.Core.Models.Track track,
        TagSettings save)
    {
        TrySetAppleDashBoxIf(appleTag, save.Length, "LENGTH", (track.Duration * 1000).ToString(CultureInfo.InvariantCulture));
        TrySetAppleDashBoxIf(appleTag, save.Bpm && track.Bpm > 0, "BPM", track.Bpm.ToString(CultureInfo.InvariantCulture));
        TrySetAppleDashBoxIf(appleTag, save.Key, "initialkey", track.Key);
        TrySetAppleDashBoxIf(appleTag, save.Danceability && track.Danceability.HasValue, "DANCEABILITY", FormatAudioFeature(track.Danceability ?? 0));
        TrySetAppleDashBoxIf(appleTag, save.Energy && track.Energy.HasValue, "ENERGY", FormatAudioFeature(track.Energy ?? 0));
        TrySetAppleDashBoxIf(appleTag, save.Valence && track.Valence.HasValue, "VALENCE", FormatAudioFeature(track.Valence ?? 0));
        TrySetAppleDashBoxIf(appleTag, save.Acousticness && track.Acousticness.HasValue, "ACOUSTICNESS", FormatAudioFeature(track.Acousticness ?? 0));
        TrySetAppleDashBoxIf(appleTag, save.Instrumentalness && track.Instrumentalness.HasValue, "INSTRUMENTALNESS", FormatAudioFeature(track.Instrumentalness ?? 0));
        TrySetAppleDashBoxIf(appleTag, save.Speechiness && track.Speechiness.HasValue, "SPEECHINESS", FormatAudioFeature(track.Speechiness ?? 0));
        TrySetAppleDashBoxIf(appleTag, save.Loudness && track.Loudness.HasValue, "LOUDNESS", FormatAudioFeature(track.Loudness ?? 0));
        TrySetAppleDashBoxIf(appleTag, save.Tempo && track.Tempo.HasValue, "TEMPO", FormatAudioFeature(track.Tempo ?? 0));
        TrySetAppleDashBoxIf(
            appleTag,
            save.TimeSignature && track.TimeSignature.HasValue,
            "TIME_SIGNATURE",
            track.TimeSignature?.ToString(CultureInfo.InvariantCulture));
        TrySetAppleDashBoxIf(appleTag, save.Liveness && track.Liveness.HasValue, "LIVENESS", FormatAudioFeature(track.Liveness ?? 0));
    }

    private void ApplyMp4IdentityMetadata(
        Tag tag,
        TagLib.Mpeg4.AppleTag? appleTag,
        DeezSpoTag.Core.Models.Track track,
        TagSettings save)
    {
        TrySetAppleDashBoxIf(appleTag, save.Label, "PUBLISHER", track.Album?.Label);
        TrySetAppleDashBoxIf(appleTag, save.Barcode, "BARCODE", track.Album?.Barcode);
        TrySetAppleDashBoxIf(appleTag, save.Explicit, "ITUNESADVISORY", track.Explicit ? "1" : "0");
        TrySetAppleDashBoxIf(appleTag, save.ReplayGain, "REPLAYGAIN_TRACK_GAIN", track.ReplayGain);

        if (save.Isrc && !string.IsNullOrEmpty(track.ISRC))
        {
            tag.ISRC = track.ISRC;
            TrySetAppleDashBox(appleTag, "ISRC", new[] { track.ISRC });
        }

        if (TryGetAppleDigitalMasterMarker(track, out var appleDigitalMasterMarker))
        {
            TrySetAppleDashBox(appleTag, AppleDigitalMasterTag, new[] { appleDigitalMasterMarker });
        }

        TrySetAppleDashBoxIf(appleTag, save.Lyrics, "LYRICS", track.Lyrics?.Unsync);

        if (save.SyncedLyrics && TryGetSyncedLyricsText(track.Lyrics, out var syncedLyricsText))
        {
            TrySetAppleDashBox(appleTag, "LYRICS_SYNCED", new[] { syncedLyricsText });
        }
    }

    private void ApplyMp4ContributorMetadata(
        TagLib.Mpeg4.AppleTag? appleTag,
        DeezSpoTag.Core.Models.Track track,
        TagSettings save)
    {
        if (track.Contributors.Count == 0)
        {
            return;
        }

        foreach (var contributor in track.Contributors)
        {
            var role = contributor.Key.ToUpperInvariant();
            if (SupportedContributorRoles.Contains(role)
                && ShouldWriteContributorRole(save, role)
                && contributor.Value is List<string> people)
            {
                TrySetAppleDashBox(appleTag, role, people.ToArray());
                continue;
            }

            if (role == "MUSICPUBLISHER"
                && save.InvolvedPeople
                && contributor.Value is List<string> publishers)
            {
                TrySetAppleDashBox(appleTag, "ORGANIZATION", publishers.ToArray());
            }
        }
    }

    private void ApplyMp4OwnershipAndCompilationMetadata(
        Tag tag,
        TagLib.Mpeg4.AppleTag? appleTag,
        DeezSpoTag.Core.Models.Track track,
        TagSettings save)
    {
        TrySetAppleDashBoxIf(appleTag, save.Copyright, "COPYRIGHT", track.Copyright);

        if ((save.SavePlaylistAsCompilation && track.Playlist != null) || track.Album?.RecordType == "compile")
        {
            TrySetTagBoolProperty(tag, "IsCompilation", true);
            TrySetAppleDashBox(appleTag, "COMPILATION", CompilationEnabledValue);
        }
    }

    private void ApplyMp4SourceMetadata(
        TagLib.Mpeg4.AppleTag? appleTag,
        DeezSpoTag.Core.Models.Track track,
        TagSettings save)
    {
        var sourceId = ResolveSourceId(track);
        if (save.Source)
        {
            TrySetAppleDashBox(appleTag, "SOURCE", new[] { ResolveSourceName(track) });
            if (!string.IsNullOrWhiteSpace(sourceId))
            {
                TrySetAppleDashBox(appleTag, "SOURCEID", new[] { sourceId });
            }
        }

        TrySetAppleDashBoxIf(appleTag, save.Url, "WWWAUDIOFILE", ResolveTrackUrl(track));

        if (save.TrackId)
        {
            if (!string.IsNullOrWhiteSpace(sourceId))
            {
                TrySetAppleDashBox(appleTag, $"{ResolveSourceTagPrefix(track)}_TRACK_ID", new[] { sourceId });
            }

            TrySetAppleDashBoxIf(appleTag, true, "SPOTIFY_TRACK_ID", ResolveSpotifyTrackId(track));
            TrySetAppleDashBoxIf(appleTag, true, "DEEZER_TRACK_ID", ResolveDeezerTrackId(track));
            TrySetAppleDashBoxIf(appleTag, true, "APPLE_TRACK_ID", ResolveAppleTrackId(track));
        }

        if (save.ReleaseId)
        {
            var releaseId = ResolveReleaseId(track);
            if (!string.IsNullOrWhiteSpace(releaseId))
            {
                TrySetAppleDashBox(appleTag, $"{ResolveSourceTagPrefix(track)}_RELEASE_ID", new[] { releaseId });
            }
        }
    }

    private void ApplyMp4RatingMetadata(
        TagLib.Mpeg4.AppleTag? appleTag,
        DeezSpoTag.Core.Models.Track track,
        TagSettings save)
    {
        if (!save.Rating || track.Rank <= 0)
        {
            return;
        }

        var rank = Math.Round(track.Rank / 10000.0);
        TrySetAppleDashBox(appleTag, "RATING", new[] { rank.ToString(CultureInfo.InvariantCulture) });
    }

    private static void ApplyCommonAlbumArtistAndSequenceTags(Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (save.AlbumArtist)
        {
            tag.AlbumArtists = new[] { ResolvePrimaryAlbumArtist(track) };
        }

        if (save.TrackNumber)
        {
            tag.Track = (uint)track.TrackNumber;
        }

        if (save.TrackTotal && track.Album != null)
        {
            tag.TrackCount = (uint)track.Album.TrackTotal;
        }

        if (save.DiscNumber)
        {
            tag.Disc = (uint)track.DiscNumber;
        }

        if (save.DiscTotal)
        {
            var discTotal = track.Album?.DiscTotal;
            if (discTotal.HasValue)
            {
                tag.DiscCount = (uint)discTotal.Value;
            }
        }
    }

    private static void ApplyFlacOrMp4PerformerTags(
        Tag tag,
        DeezSpoTag.Core.Models.Track track,
        TagSettings save,
        Action<string[]> writeArtistsTag)
    {
        if (!save.Artist || track.Artists.Count == 0)
        {
            return;
        }

        if (save.MultiArtistSeparator == DefaultMultiArtistSeparator)
        {
            tag.Performers = track.Artists.ToArray();
            return;
        }

        tag.Performers = save.MultiArtistSeparator == NothingSeparator
            ? new[] { track.MainArtist?.Name ?? UnknownValue }
            : new[] { track.ArtistsString };

        if (save.Artists)
        {
            writeArtistsTag(new[] { ResolveMultiArtistValue(track, save) });
        }
    }

    private void SetVorbisCommentIf(Tag tag, bool condition, string field, string? value)
    {
        if (condition && !string.IsNullOrWhiteSpace(value))
        {
            SetVorbisComment(tag, field, new[] { value });
        }
    }

    private void TrySetAppleDashBoxIf(TagLib.Mpeg4.AppleTag? tag, bool condition, string name, string? value)
    {
        if (condition && !string.IsNullOrWhiteSpace(value))
        {
            TrySetAppleDashBox(tag, name, new[] { value });
        }
    }

    private static bool ShouldWriteContributorRole(TagSettings save, string role)
    {
        return role == "COMPOSER"
            ? save.Composer
            : save.InvolvedPeople;
    }

    private async Task<bool> TryTagMP4WithFfmpegAsync(string ffmpegPath, string path, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        var coverPath = save.Cover ? track.Album?.EmbeddedCoverPath : null;
        var hasCover = !string.IsNullOrWhiteSpace(coverPath) && System.IO.File.Exists(coverPath);
        var metadata = BuildMP4FfmpegMetadata(track, save);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "ffmpeg MP4 metadata map for {Path}: {Keys}",
                path,
                string.Join(", ", metadata.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)));
        }

        if (!hasCover && metadata.Count == 0)
        {
            return true;
        }

        var directory = Path.GetDirectoryName(path) ?? Path.GetTempPath();
        var extension = Path.GetExtension(path);
        var tempPath = Path.Join(
            directory,
            $"{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}.tag{extension}");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(path);

            if (hasCover)
            {
                startInfo.ArgumentList.Add("-i");
                startInfo.ArgumentList.Add(coverPath!);
            }

            startInfo.ArgumentList.Add("-map_metadata");
            startInfo.ArgumentList.Add("-1");
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:a:0");

            if (hasCover)
            {
                startInfo.ArgumentList.Add("-map");
                startInfo.ArgumentList.Add("1:v:0");
            }

            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("copy");

            if (hasCover)
            {
                startInfo.ArgumentList.Add("-c:v");
                startInfo.ArgumentList.Add("mjpeg");
                startInfo.ArgumentList.Add("-disposition:v:0");
                startInfo.ArgumentList.Add("attached_pic");
            }
            else
            {
                startInfo.ArgumentList.Add("-vn");
            }

            foreach (var (key, value) in metadata)
            {
                startInfo.ArgumentList.Add("-metadata");
                startInfo.ArgumentList.Add($"{key}={value}");
            }

            // Persist arbitrary metadata keys (SOURCEID/DEEZER_TRACK_ID/etc.) in MP4 containers.
            startInfo.ArgumentList.Add("-movflags");
            startInfo.ArgumentList.Add("use_metadata_tags");

            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("mp4");
            startInfo.ArgumentList.Add(tempPath);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }

            await process.WaitForExitAsync();
            var stderr = await process.StandardError.ReadToEndAsync();

            if (process.ExitCode != 0 || !System.IO.File.Exists(tempPath))
            {
                _logger.LogWarning(
                    "ffmpeg MP4 tagging failed for {Path} (exit {ExitCode}): {Error}",
                    path,
                    process.ExitCode,
                    stderr);
                TryDeleteTempFile(tempPath);
                return false;
            }

            if (!HasSafeTaggedDuration(path, tempPath, out var sourceDurationSeconds, out var taggedDurationSeconds))
            {
                _logger.LogWarning(
                    "ffmpeg MP4 tagging rejected for {Path} due to duration delta (source={SourceDuration:F3}s tagged={TaggedDuration:F3}s).",
                    path,
                    sourceDurationSeconds,
                    taggedDurationSeconds);
                TryDeleteTempFile(tempPath);
                return false;
            }

            System.IO.File.Move(tempPath, path, true);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ffmpeg MP4 tagging threw for {Path}", path);
            TryDeleteTempFile(tempPath);
            return false;
        }
    }

    private bool VerifyMp4TagPersistence(string path, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        var validators = BuildMp4PersistenceValidators(track, save);
        if (validators.Count == 0)
        {
            return true;
        }

        try
        {
            using var file = TagLib.File.Create(path);
            if ((file.TagTypesOnDisk & TagTypes.Apple) == 0)
            {
                return false;
            }

            return validators.Any(validator => validator(file.Tag));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "MP4 tag verification failed for {Path}", path);            }
            return false;
        }
    }

    private static List<Func<TagLib.Tag, bool>> BuildMp4PersistenceValidators(DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        var validators = new List<Func<TagLib.Tag, bool>>();
        if (save.Title && !string.IsNullOrWhiteSpace(track.Title))
        {
            validators.Add(static tag => !string.IsNullOrWhiteSpace(tag.Title));
        }

        if (save.Artist && (track.Artists.Count > 0 || !string.IsNullOrWhiteSpace(track.MainArtist?.Name)))
        {
            validators.Add(HasAnyPerformers);
        }

        if (save.Album && !string.IsNullOrWhiteSpace(track.Album?.Title))
        {
            validators.Add(static tag => !string.IsNullOrWhiteSpace(tag.Album));
        }

        if (save.Year && !string.IsNullOrWhiteSpace(track.Date?.Year))
        {
            validators.Add(static tag => tag.Year > 0);
        }

        if (save.Isrc && !string.IsNullOrWhiteSpace(track.ISRC))
        {
            validators.Add(static tag => !string.IsNullOrWhiteSpace(tag.ISRC));
        }

        var expectsCover = save.Cover
            && !string.IsNullOrWhiteSpace(track.Album?.EmbeddedCoverPath)
            && System.IO.File.Exists(track.Album.EmbeddedCoverPath);
        if (expectsCover)
        {
            validators.Add(static tag => tag.Pictures?.Length > 0);
        }

        return validators;
    }

    private static bool HasAnyPerformers(TagLib.Tag tag) =>
        tag.Performers?.Any(value => !string.IsNullOrWhiteSpace(value)) == true;

    private bool HasSafeTaggedDuration(
        string sourcePath,
        string taggedPath,
        out double sourceDurationSeconds,
        out double taggedDurationSeconds)
    {
        sourceDurationSeconds = 0;
        taggedDurationSeconds = 0;

        try
        {
            using var sourceFile = TagLib.File.Create(sourcePath);
            using var taggedFile = TagLib.File.Create(taggedPath);
            sourceDurationSeconds = sourceFile.Properties.Duration.TotalSeconds;
            taggedDurationSeconds = taggedFile.Properties.Duration.TotalSeconds;

            if (sourceDurationSeconds <= 0 || taggedDurationSeconds <= 0)
            {
                return true;
            }

            var delta = Math.Abs(sourceDurationSeconds - taggedDurationSeconds);
            var allowedDelta = Math.Max(2d, sourceDurationSeconds * 0.05d);
            return delta <= allowedDelta;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Unable to compare MP4 durations for {SourcePath} and {TaggedPath}", sourcePath, taggedPath);            }
            return true;
        }
    }

    private Dictionary<string, string> BuildMP4FfmpegMetadata(DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddPrimaryMp4Metadata(metadata, track, save);
        AddTrackNumberMp4Metadata(metadata, track, save);
        AddAlbumAndDateMp4Metadata(metadata, track, save);
        AddAdditionalMp4Metadata(metadata, track, save);
        AddSourceMp4Metadata(metadata, track, save);
        return metadata;
    }

    private static void AddMetadataValue(Dictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            metadata[key] = value.Trim();
        }
    }

    private static void AddPrimaryMp4Metadata(Dictionary<string, string> metadata, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (save.Title)
        {
            AddMetadataValue(metadata, "title", track.Title);
        }

        if (save.Artist)
        {
            AddMetadataValue(metadata, "artist", track.Artists.Count > 0 ? track.ArtistsString : track.MainArtist?.Name);
        }

        if (save.Album)
        {
            AddMetadataValue(metadata, "album", track.Album?.Title);
        }

        if (save.AlbumArtist)
        {
            AddMetadataValue(metadata, "album_artist", ResolvePrimaryAlbumArtist(track));
        }
    }

    private static void AddTrackNumberMp4Metadata(Dictionary<string, string> metadata, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (save.TrackNumber && track.TrackNumber > 0)
        {
            var trackValue = save.TrackTotal && (track.Album?.TrackTotal ?? 0) > 0
                ? $"{track.TrackNumber}/{track.Album!.TrackTotal}"
                : track.TrackNumber.ToString(CultureInfo.InvariantCulture);
            AddMetadataValue(metadata, "track", trackValue);
        }

        if (save.DiscNumber && track.DiscNumber > 0)
        {
            var discTotal = track.Album?.DiscTotal;
            var discValue = save.DiscTotal && discTotal.HasValue && discTotal.Value > 0
                ? $"{track.DiscNumber}/{discTotal.Value}"
                : track.DiscNumber.ToString(CultureInfo.InvariantCulture);
            AddMetadataValue(metadata, "disc", discValue);
        }
    }

    private void AddAlbumAndDateMp4Metadata(Dictionary<string, string> metadata, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (save.Genre && track.Album?.Genre is { Count: > 0 })
        {
            var genres = SanitizeGenres(track.Album.Genre);
            if (genres.Length > 0)
            {
                AddMetadataValue(metadata, "genre", string.Join("; ", genres));
            }
        }

        if (save.Date && track.Date != null && track.Date.IsValid())
        {
            AddMetadataValue(metadata, "date", track.DateString);
        }
        else if (save.Year && track.Date != null && !string.IsNullOrWhiteSpace(track.Date.Year))
        {
            AddMetadataValue(metadata, "date", track.Date.Year);
        }
    }

    private static void AddAdditionalMp4Metadata(Dictionary<string, string> metadata, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (save.Isrc)
        {
            AddMetadataValue(metadata, "isrc", track.ISRC);
        }

        if (save.Bpm && track.Bpm > 0)
        {
            AddMetadataValue(metadata, "bpm", track.Bpm.ToString(CultureInfo.InvariantCulture));
        }

        if (save.Label)
        {
            AddMetadataValue(metadata, "publisher", track.Album?.Label);
        }

        if (save.Composer
            && track.Contributors.TryGetValue(ComposerRole, out var composersObj)
            && composersObj is List<string> composers
            && composers.Count > 0)
        {
            AddMetadataValue(metadata, ComposerRole, string.Join(", ", composers.Where(x => !string.IsNullOrWhiteSpace(x))));
        }

        if (save.Copyright)
        {
            AddMetadataValue(metadata, "copyright", track.Copyright);
        }

        if (save.Lyrics && !string.IsNullOrWhiteSpace(track.Lyrics?.Unsync))
        {
            AddMetadataValue(metadata, LyricsKey, track.Lyrics.Unsync);
        }

        if (save.Url)
        {
            AddMetadataValue(metadata, "purl", ResolveTrackUrl(track));
            AddMetadataValue(metadata, "WWWAUDIOFILE", ResolveTrackUrl(track));
        }

        if (save.Explicit)
        {
            AddMetadataValue(metadata, "ITUNESADVISORY", track.Explicit ? "1" : "0");
        }
    }

    private static void AddSourceMp4Metadata(Dictionary<string, string> metadata, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        var sourceId = ResolveSourceId(track);
        if (save.Source)
        {
            AddMetadataValue(metadata, "SOURCE", ResolveSourceName(track));
            AddMetadataValue(metadata, "SOURCEID", sourceId);
        }

        if (save.TrackId)
        {
            AddMetadataValue(metadata, $"{ResolveSourceTagPrefix(track)}_TRACK_ID", sourceId);
            AddMetadataValue(metadata, "SPOTIFY_TRACK_ID", ResolveSpotifyTrackId(track));
            AddMetadataValue(metadata, "DEEZER_TRACK_ID", ResolveDeezerTrackId(track));
            AddMetadataValue(metadata, "APPLE_TRACK_ID", ResolveAppleTrackId(track));
        }

        if (save.ReleaseId)
        {
            AddMetadataValue(metadata, $"{ResolveSourceTagPrefix(track)}_RELEASE_ID", ResolveReleaseId(track));
        }
    }

    private static bool IsAtmosCodecMp4(string path)
    {
        try
        {
            var ffprobePath = ResolveFfprobePath();
            if (string.IsNullOrWhiteSpace(ffprobePath))
            {
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-select_streams");
            startInfo.ArgumentList.Add("a:0");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("stream=codec_name");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
            startInfo.ArgumentList.Add(path);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }

            process.WaitForExit(3000);
            var stdout = process.StandardOutput.ReadToEnd();
            var codec = stdout.Trim();
            return codec.Equals("eac3", StringComparison.OrdinalIgnoreCase)
                   || codec.Equals("ec-3", StringComparison.OrdinalIgnoreCase)
                   || codec.Equals("ac3", StringComparison.OrdinalIgnoreCase)
                   || codec.Equals("ac-3", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static void TryDeleteTempFile(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _ = ex;
        }
    }

    #endregion

    #region Helper Methods
    private static string ResolveSourceName(DeezSpoTag.Core.Models.Track track)
    {
        var source = string.IsNullOrWhiteSpace(track.Source) ? DeezerSource : track.Source.Trim();
        return source;
    }

    private static string ResolveSourceId(DeezSpoTag.Core.Models.Track track)
    {
        if (!string.IsNullOrWhiteSpace(track.SourceId))
        {
            return track.SourceId.Trim();
        }

        var source = ResolveSourceName(track).Trim().ToLowerInvariant();
        if (source == DeezerSource && !string.IsNullOrWhiteSpace(track.Id) && track.Id.All(char.IsDigit))
        {
            return track.Id.Trim();
        }

        return string.Empty;
    }

    private static string ResolveSourceTagPrefix(DeezSpoTag.Core.Models.Track track)
    {
        return ResolveSourceName(track).ToUpperInvariant();
    }

    private static string? ResolveReleaseId(DeezSpoTag.Core.Models.Track track)
    {
        if (!string.IsNullOrWhiteSpace(track.Album?.Id))
        {
            return track.Album.Id;
        }

        return null;
    }

    private static string? ResolveTrackUrl(DeezSpoTag.Core.Models.Track track)
    {
        if (!string.IsNullOrWhiteSpace(track.DownloadURL))
        {
            return track.DownloadURL;
        }

        var sourceId = ResolveSourceId(track);
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return null;
        }

        var source = ResolveSourceName(track).Trim().ToLowerInvariant();
        return source switch
        {
            SpotifySource => $"https://open.spotify.com/track/{sourceId}",
            DeezerSource => $"https://www.deezer.com/track/{sourceId}",
            _ => null
        };
    }

    private static string FormatAudioFeature(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void SetCustomFrameIfPresent(
        Tag tag,
        string frameId,
        string description,
        string? value,
        TagSettings save)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        SetCustomFrame(tag, frameId, description, value, save);
    }

    private static string? ResolveSpotifyTrackId(DeezSpoTag.Core.Models.Track track)
    {
        return TrackIdNormalization.TryResolveSpotifyTrackId(track, out var spotifyTrackId)
            ? spotifyTrackId
            : null;
    }

    private static string? ResolveDeezerTrackId(DeezSpoTag.Core.Models.Track track)
    {
        return ResolveTrackIdFromUrls(
            track,
            directKey: "deezer_track_id",
            fallbackUrlKey: DeezerSource,
            marker: "/track/");
    }

    private static string? ResolveAppleTrackId(DeezSpoTag.Core.Models.Track track)
    {
        return ResolveTrackIdFromUrls(
            track,
            directKey: "apple_track_id",
            fallbackUrlKey: AppleSource,
            marker: "?i=");
    }

    private static string? ResolveTrackIdFromUrls(
        DeezSpoTag.Core.Models.Track track,
        string directKey,
        string fallbackUrlKey,
        string marker)
    {
        if (track.Urls.TryGetValue(directKey, out var direct) && !string.IsNullOrWhiteSpace(direct))
        {
            return direct.Trim();
        }

        if (!track.Urls.TryGetValue(fallbackUrlKey, out var url) || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return ParseTrackIdFromUrl(url, marker);
    }

    private static string? ParseTrackIdFromUrl(string url, string marker)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var index = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = index + marker.Length;
        if (start >= url.Length)
        {
            return null;
        }

        var end = url.IndexOfAny(['&', '?', '/', '#'], start);
        var id = end >= 0 ? url[start..end] : url[start..];
        var trimmed = id.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string? ResolveFfmpegPath() => ExternalToolResolver.ResolveFfmpegPath();

    private static string? ResolveFfprobePath() => ExternalToolResolver.ResolveFfprobePath();

    private static async Task AttachCoverArtAsync(TagLib.Tag tag, string? embeddedCoverPath, TagSettings save)
    {
        if (string.IsNullOrEmpty(embeddedCoverPath) || !System.IO.File.Exists(embeddedCoverPath))
        {
            return;
        }

        var coverData = await System.IO.File.ReadAllBytesAsync(embeddedCoverPath);
        if (coverData.Length == 0)
        {
            return;
        }

        if (tag is TagLib.Id3v2.Tag id3Tag)
        {
            SetId3CoverFrame(id3Tag, coverData, save.CoverDescriptionUTF8);
            return;
        }

        tag.Pictures = new[]
        {
            new TagLib.Picture(coverData)
            {
                Type = PictureType.FrontCover,
                Description = CoverDescription
            }
        };
    }

    /// <summary>
    /// Set custom ID3v2 frame
    /// </summary>
    private void SetCustomFrame(Tag tag, string frameId, string description, string value, TagSettings save)
    {
        try
        {
            if (tag is TagLib.Id3v2.Tag id3Tag)
            {
                if (frameId == "TXXX")
                {
                    var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3Tag, description ?? string.Empty, true);
                    if (save.UseNullSeparator)
                    {
                        frame.TextEncoding = TagLib.StringType.UTF16;
                    }
                    frame.Text = BuildId3TextValues(value, save.UseNullSeparator);
                }
                else
                {
                    var frame = TagLib.Id3v2.TextInformationFrame.Get(id3Tag, frameId, true);
                    if (save.UseNullSeparator)
                    {
                        frame.TextEncoding = TagLib.StringType.UTF16;
                    }
                    frame.Text = BuildId3TextValues(value, save.UseNullSeparator);
                }
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Skipping custom frame {FrameId}; ID3v2 tag not available", frameId);                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to set custom frame {FrameId}", frameId);
        }
    }

    private static void SetId3CoverFrame(TagLib.Id3v2.Tag id3Tag, byte[] coverData, bool coverDescriptionUtf8)
    {
        var picture = new TagLib.Picture(coverData)
        {
            Type = PictureType.FrontCover,
            Description = CoverDescription
        };
        id3Tag.RemoveFrames("APIC");
        var apic = new TagLib.Id3v2.AttachmentFrame(picture)
        {
            TextEncoding = coverDescriptionUtf8 ? TagLib.StringType.UTF8 : TagLib.StringType.Latin1
        };
        id3Tag.AddFrame(apic);
    }

    private static string[] BuildId3TextValues(string value, bool useNullSeparator)
    {
        if (!useNullSeparator)
        {
            return new[] { value };
        }

        var split = value
            .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return split.Length > 0 ? split : new[] { value };
    }

    /// <summary>
    /// Set lyrics frame
    /// </summary>
    private void SetLyricsFrame(Tag tag, string lyrics)
    {
        try
        {
            if (tag is TagLib.Id3v2.Tag id3Tag)
            {
                var frame = TagLib.Id3v2.UnsynchronisedLyricsFrame.Get(id3Tag, "eng", string.Empty, true);
                frame.Text = lyrics;
            }
            else
            {
                _logger.LogDebug("Skipping lyrics frame; ID3v2 tag not available");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to set lyrics frame");
        }
    }

    /// <summary>
    /// Set synchronized lyrics frame (SYLT) for ID3v2 tags.
    /// </summary>
    private void SetSyncedLyricsFrame(Tag tag, DeezSpoTag.Core.Models.Lyrics lyrics)
    {
        try
        {
            if (tag is not TagLib.Id3v2.Tag id3Tag)
            {
                _logger.LogDebug("Skipping synced lyrics frame; ID3v2 tag not available");
                return;
            }

            var syncedText = BuildSynchedTextEntries(lyrics);
            if (syncedText.Count == 0)
            {
                return;
            }

            var frame = new TagLib.Id3v2.SynchronisedLyricsFrame(string.Empty, "eng", TagLib.Id3v2.SynchedTextType.Lyrics)
            {
                Format = TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds,
                Text = syncedText.ToArray()
            };
            id3Tag.AddFrame(frame);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to set synchronized lyrics frame");
        }
    }

    /// <summary>
    /// Set Vorbis comment for FLAC files
    /// </summary>
    private void SetVorbisComment(Tag tag, string field, IEnumerable<string> values)
    {
        try
        {
            if (tag is TagLib.Ogg.XiphComment xiph)
            {
                xiph.SetField(field, values.ToArray());
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Skipping Vorbis comment {Field}; Xiph tag not available", field);                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to set Vorbis comment {Field}", field);
        }
    }

    private void TrySetAppleDashBox(TagLib.Mpeg4.AppleTag? tag, string name, string[] values)
    {
        if (!AppleDashBoxReflectionHelper.TrySetValues(tag, name, values))
        {
            _logger.LogWarning("Failed to set MP4 dash box {Name}", name);
        }
    }

    private static bool TryGetAppleDigitalMasterMarker(DeezSpoTag.Core.Models.Track track, out string marker)
    {
        marker = string.Empty;
        if (track?.Urls == null)
        {
            return false;
        }

        if (!track.Urls.TryGetValue("apple_digital_master", out var value)
            && !track.Urls.TryGetValue(AppleDigitalMasterTag, out value))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        marker = value.Trim();
        return true;
    }

    private static string ResolveMultiArtistValue(DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (save.MultiArtistSeparator == NothingSeparator)
        {
            return track.MainArtist?.Name ?? UnknownValue;
        }

        if (save.MultiArtistSeparator == DefaultMultiArtistSeparator)
        {
            return string.Join(", ", track.Artists);
        }

        return track.ArtistsString;
    }

    private static bool TryGetSyncedLyricsText(DeezSpoTag.Core.Models.Lyrics? lyrics, out string syncedText)
    {
        syncedText = string.Empty;
        if (lyrics == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(lyrics.Sync))
        {
            syncedText = lyrics.Sync;
            return true;
        }

        if (lyrics.SyncID3 == null || lyrics.SyncID3.Count == 0)
        {
            return false;
        }

        var lines = new List<string>();
        foreach (var line in lyrics.SyncID3)
        {
            if (line == null || line.Timestamp < 0)
            {
                continue;
            }

            var minutes = line.Timestamp / 60000;
            var seconds = (line.Timestamp % 60000) / 1000;
            var centiseconds = (line.Timestamp % 1000) / 10;
            lines.Add($"[{minutes:D2}:{seconds:D2}.{centiseconds:D2}]{line.Text ?? string.Empty}");
        }

        if (lines.Count == 0)
        {
            return false;
        }

        syncedText = string.Join('\n', lines);
        return true;
    }

    private static List<TagLib.Id3v2.SynchedText> BuildSynchedTextEntries(DeezSpoTag.Core.Models.Lyrics lyrics)
    {
        var entries = new List<TagLib.Id3v2.SynchedText>();
        if (TryAddSynchedTextFromSyncId3(lyrics, entries))
        {
            return entries;
        }

        if (string.IsNullOrWhiteSpace(lyrics.Sync))
        {
            return entries;
        }

        AddSynchedTextFromLrc(lyrics.Sync, entries);
        return entries;
    }

    private static bool TryAddSynchedTextFromSyncId3(
        DeezSpoTag.Core.Models.Lyrics lyrics,
        List<TagLib.Id3v2.SynchedText> entries)
    {
        if (lyrics.SyncID3?.Count is not > 0)
        {
            return false;
        }

        foreach (var line in lyrics.SyncID3)
        {
            if (line == null || line.Timestamp < 0)
            {
                continue;
            }

            entries.Add(new TagLib.Id3v2.SynchedText(line.Timestamp, line.Text ?? string.Empty));
        }

        return entries.Count > 0;
    }

    private static void AddSynchedTextFromLrc(string lrcText, List<TagLib.Id3v2.SynchedText> entries)
    {
        foreach (var match in lrcText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(rawLine => LrcLineRegex.Match(rawLine))
            .Where(static match => match.Success)
            .Select(static match => match.Groups))
        {
            if (!int.TryParse(match[1].Value, out var minutes) ||
                !int.TryParse(match[2].Value, out var seconds))
            {
                continue;
            }

            var timestampMs = (minutes * 60 * 1000L) + (seconds * 1000L) + ParseLrcFractionMs(match[3].Value);
            entries.Add(new TagLib.Id3v2.SynchedText(timestampMs, match[4].Value.Trim()));
        }
    }

    private static int ParseLrcFractionMs(string fraction)
    {
        if (string.IsNullOrWhiteSpace(fraction) || !int.TryParse(fraction, out var parsedFraction))
        {
            return 0;
        }

        return fraction.Length switch
        {
            1 => parsedFraction * 100,
            2 => parsedFraction * 10,
            _ => int.Parse(fraction[..3])
        };
    }

    private async Task TagGenericAsync(string path, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            if (await TryTagWithKnownTagTypeAsync(file.TagTypesOnDisk, path, track, save))
            {
                return;
            }

            var tag = file.Tag;
            ApplyGenericFallbackTagValues(tag, track, save);
            await AttachGenericFallbackCoverAsync(tag, track, save);
            file.Save();
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Successfully tagged file via generic fallback: {Path}", path);            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Generic tagging fallback failed for {path}", ex);
        }
    }

    private async Task<bool> TryTagWithKnownTagTypeAsync(
        TagTypes tagTypesOnDisk,
        string path,
        DeezSpoTag.Core.Models.Track track,
        TagSettings save)
    {
        if ((tagTypesOnDisk & TagTypes.Id3v2) != 0)
        {
            await TagMP3Async(path, track, save);
            return true;
        }

        if ((tagTypesOnDisk & TagTypes.Xiph) != 0)
        {
            await TagFLACAsync(path, track, save);
            return true;
        }

        if ((tagTypesOnDisk & TagTypes.Apple) != 0)
        {
            await TagMP4Async(path, track, save);
            return true;
        }

        return false;
    }

    private void ApplyGenericFallbackTagValues(TagLib.Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        ApplyGenericFallbackCoreTagValues(tag, track, save);
        ApplyGenericFallbackLyrics(tag, track, save);
    }

    private void ApplyGenericFallbackCoreTagValues(TagLib.Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        ApplyGenericFallbackIdentityTags(tag, track, save);
        ApplyGenericFallbackSequenceTags(tag, track, save);
        ApplyGenericFallbackCatalogTags(tag, track, save);
    }

    private static void ApplyGenericFallbackIdentityTags(TagLib.Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (save.Title) tag.Title = track.Title;
        if (save.Artist && track.Artists.Count > 0) tag.Performers = track.Artists.ToArray();
        if (save.Album) tag.Album = track.Album?.Title;
        if (save.AlbumArtist) tag.AlbumArtists = new[] { ResolvePrimaryAlbumArtist(track) };
    }

    private static void ApplyGenericFallbackSequenceTags(TagLib.Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (save.TrackNumber) tag.Track = (uint)track.TrackNumber;
        if (save.TrackTotal && track.Album != null) tag.TrackCount = (uint)track.Album.TrackTotal;
        if (save.DiscNumber) tag.Disc = (uint)track.DiscNumber;
        if (save.DiscTotal && track.Album?.DiscTotal.HasValue == true) tag.DiscCount = (uint)track.Album.DiscTotal.Value;
    }

    private void ApplyGenericFallbackCatalogTags(TagLib.Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (save.Genre && track.Album?.Genre is { Count: > 0 }) tag.Genres = SanitizeGenres(track.Album.Genre);
        if (save.Year && uint.TryParse(track.Date.Year, out var year)) tag.Year = year;
        if (save.Isrc && !string.IsNullOrWhiteSpace(track.ISRC)) tag.ISRC = track.ISRC;
    }

    private static void ApplyGenericFallbackLyrics(TagLib.Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (save.Lyrics && !string.IsNullOrWhiteSpace(track.Lyrics?.Unsync))
        {
            tag.Lyrics = track.Lyrics.Unsync;
        }
        else if (save.SyncedLyrics && TryGetSyncedLyricsText(track.Lyrics, out var syncedText))
        {
            tag.Lyrics = syncedText;
        }
    }

    private static async Task AttachGenericFallbackCoverAsync(TagLib.Tag tag, DeezSpoTag.Core.Models.Track track, TagSettings save)
    {
        if (!save.Cover
            || string.IsNullOrEmpty(track.Album?.EmbeddedCoverPath)
            || !System.IO.File.Exists(track.Album.EmbeddedCoverPath))
        {
            return;
        }

        var coverData = await System.IO.File.ReadAllBytesAsync(track.Album.EmbeddedCoverPath);
        if (coverData.Length == 0)
        {
            return;
        }

        if (tag is TagLib.Id3v2.Tag id3Tag)
        {
            SetId3CoverFrame(id3Tag, coverData, save.CoverDescriptionUTF8);
            return;
        }

        var picture = new TagLib.Picture(coverData)
        {
            Type = PictureType.FrontCover,
            Description = CoverDescription
        };
        tag.Pictures = new[] { picture };
    }

    private void UpdateDownloadCapabilities(DeezSpoTag.Core.Models.Track track)
    {
        try
        {
            var platform = ResolveSourceName(track);
            var tags = CollectDownloadTags(track);
            if (tags.Count == 0)
            {
                return;
            }
            _capabilitiesStore.RecordDownloadTags(platform, tags);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed updating download tag capabilities.");
        }
    }

    private static List<string> CollectDownloadTags(DeezSpoTag.Core.Models.Track track)
    {
        var tags = new List<string>();
        void Add(string tag, bool condition)
        {
            if (!condition || tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
            tags.Add(tag);
        }

        Add("title", !string.IsNullOrWhiteSpace(track.Title));
        Add("artist", track.Artists.Count > 0 || !string.IsNullOrWhiteSpace(track.MainArtist?.Name));
        Add("artists", track.Artists.Count > 0);
        Add("album", !string.IsNullOrWhiteSpace(track.Album?.Title));
        Add("albumArtist", !string.IsNullOrWhiteSpace(track.Album?.MainArtist?.Name) || (track.Album?.Artists?.Count ?? 0) > 0);
        Add("trackNumber", track.TrackNumber > 0);
        Add("trackTotal", (track.Album?.TrackTotal ?? 0) > 0);
        Add("discNumber", track.DiscNumber > 0);
        Add("discTotal", (track.Album?.DiscTotal ?? 0) > 0);
        Add("genre", (track.Album?.Genre?.Count ?? 0) > 0);
        Add("year", track.Date != null && !string.IsNullOrWhiteSpace(track.Date.Year) && track.Date.Year != "0000");
        Add("date", track.Date != null && track.Date.IsValid());
        Add("explicit", track.Album?.Explicit.HasValue == true || track.Explicit);
        Add("isrc", !string.IsNullOrWhiteSpace(track.ISRC));
        Add("length", track.Duration > 0);
        Add("barcode", !string.IsNullOrWhiteSpace(track.Album?.Barcode) || !string.IsNullOrWhiteSpace(track.Album?.UPC));
        Add("bpm", track.Bpm > 0);
        Add("key", !string.IsNullOrWhiteSpace(track.Key));
        foreach (var (tag, hasValue) in GetFeatureCapabilityTags(track))
        {
            Add(tag, hasValue);
        }
        Add("replayGain", !string.IsNullOrWhiteSpace(track.ReplayGain));
        Add("label", !string.IsNullOrWhiteSpace(track.Album?.Label));
        Add(LyricsKey, !string.IsNullOrWhiteSpace(track.Lyrics?.Unsync));
        Add("syncedLyrics", !string.IsNullOrWhiteSpace(track.Lyrics?.Sync) || (track.Lyrics?.SyncID3?.Count ?? 0) > 0);
        Add("copyright", !string.IsNullOrWhiteSpace(track.Copyright) || !string.IsNullOrWhiteSpace(track.Album?.Copyright));
        Add(ComposerRole, track.Contributors?.ContainsKey(ComposerRole) == true);
        Add("involvedPeople", track.Contributors?.Count > 0);
        Add(CoverDescription, !string.IsNullOrWhiteSpace(track.Album?.EmbeddedCoverPath) && System.IO.File.Exists(track.Album.EmbeddedCoverPath));
        Add("source", !string.IsNullOrWhiteSpace(track.Source) || !string.IsNullOrWhiteSpace(track.SourceId));
        Add("url", !string.IsNullOrWhiteSpace(track.DownloadURL) || (track.Urls?.Count ?? 0) > 0);
        Add("trackId", !string.IsNullOrWhiteSpace(ResolveSourceId(track)));
        Add("releaseId", !string.IsNullOrWhiteSpace(ResolveReleaseId(track)));

        return tags;
    }

    private static IEnumerable<(string Tag, bool HasValue)> GetFeatureCapabilityTags(DeezSpoTag.Core.Models.Track track)
    {
        yield return ("danceability", track.Danceability.HasValue);
        yield return ("energy", track.Energy.HasValue);
        yield return ("valence", track.Valence.HasValue);
        yield return ("acousticness", track.Acousticness.HasValue);
        yield return ("instrumentalness", track.Instrumentalness.HasValue);
        yield return ("speechiness", track.Speechiness.HasValue);
        yield return ("loudness", track.Loudness.HasValue);
        yield return ("tempo", track.Tempo.HasValue);
        yield return ("timeSignature", track.TimeSignature.HasValue);
        yield return ("liveness", track.Liveness.HasValue);
    }

    private void TrySetTagBoolProperty(Tag tag, string propertyName, bool value)
    {
        try
        {
            var property = tag.GetType().GetProperty(propertyName);
            if (property?.CanWrite == true && property.PropertyType == typeof(bool))
            {
                property.SetValue(tag, value);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to set tag property {Property}", propertyName);
        }
    }

    #endregion
}
