using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using TagLib;
using IOFile = System.IO.File;

namespace DeezSpoTag.Web.Services;

public sealed class QuickTagService
{
    private const string FlacExtension = ".flac";
    private const string AiffExtension = ".aiff";
    private const string OpusExtension = ".opus";
    private const string PathRequiredMessage = "Path is required.";
    private const string FileNotFoundMessage = "File not found.";
    private const string AlbumTag = "ALBUM";
    private const string AlbumArtistTag = "ALBUMARTIST";
    private const string ArtistTag = "ARTIST";
    private const string ComposerTag = "COMPOSER";
    private const string GenreTag = "GENRE";
    private const string TitleTag = "TITLE";
    private const string CommentTag = "COMMENT";
    private const string LyricsTag = "LYRICS";
    private const string RatingTag = "RATING";
    private const string ITunesIsrcTag = "iTunes:ISRC";
    private const string AppleDashMean = "com.apple.iTunes";
    private static readonly HashSet<string> BlockedGenres = new(StringComparer.OrdinalIgnoreCase)
    {
        "other",
        "others"
    };
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", FlacExtension, ".m4a", ".mp4", ".m4b", ".wav", ".aif", AiffExtension, ".ogg", OpusExtension
    };

    private static readonly HashSet<string> Id3FamilyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".aif", AiffExtension
    };

    private static readonly HashSet<string> VorbisFamilyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        FlacExtension, ".ogg", OpusExtension
    };

    private static readonly HashSet<string> Mp4FamilyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m4a", ".mp4", ".m4b"
    };

    private static readonly HashSet<string> PlaylistExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m3u", ".m3u8", ".pls", ".xspf", ".txt"
    };

    private static readonly HashSet<string> LyricsSidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lrc", ".ttml", ".srt", ".vtt"
    };

    private static readonly string[] LyricsFileNameTokens =
    {
        "lyrics", "lyric", "karaoke", "synced", "unsynced"
    };

    private static readonly string[] AnimatedArtworkTokens =
    {
        "animated_artwork", "square_animated_artwork", "tall_animated_artwork"
    };

    private static readonly string[] Mp4DashProbeKeys =
    {
        "ISRC", TitleTag, ArtistTag, AlbumTag, AlbumArtistTag, GenreTag, "DATE", "YEAR", ComposerTag,
        "PUBLISHER", "LYRICIST", "CONDUCTOR", "LANGUAGE", "KEY", "BPM", "TRACK", "DISC",
        "MOOD", "STYLE", CommentTag, LyricsTag, RatingTag, "ENERGY", "VIBE", "SITUATION", "INSTRUMENTS"
    };

    private readonly ILogger<QuickTagService> _logger;
    private readonly LibraryRepository _libraryRepository;
    private readonly LibraryConfigStore _configStore;
    private readonly DeezSpoTagSettingsService _settingsService;

    public QuickTagService(
        ILogger<QuickTagService> logger,
        LibraryRepository libraryRepository,
        LibraryConfigStore configStore,
        DeezSpoTagSettingsService settingsService)
    {
        _logger = logger;
        _libraryRepository = libraryRepository;
        _configStore = configStore;
        _settingsService = settingsService;
    }

    public QuickTagFolderResult GetFolder(string? inputPath, string? subdir)
    {
        var allowedRoots = ResolveAllowedLibraryRoots();
        var resolved = ResolveBrowsePath(inputPath, subdir, allowedRoots);
        var items = Directory.Exists(resolved)
            ? EnumerateFolderEntries(resolved, allowedRoots)
            : new List<QuickTagFolderEntry>();

        return new QuickTagFolderResult
        {
            Path = resolved,
            Files = items
        };
    }

    private static List<QuickTagFolderEntry> EnumerateFolderEntries(string rootPath, List<string> allowedRoots)
    {
        var items = new List<QuickTagFolderEntry>();
        AddBrowsableDirectories(items, rootPath, allowedRoots);
        AddBrowsableFiles(items, rootPath, allowedRoots);

        return items
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddBrowsableDirectories(List<QuickTagFolderEntry> items, string rootPath, List<string> allowedRoots)
    {
        foreach (var dir in Directory.EnumerateDirectories(rootPath))
        {
            if (!IsPathAllowedForBrowsing(dir, allowedRoots))
            {
                continue;
            }

            var name = Path.GetFileName(dir);
            if (IsHiddenOrInvalidEntryName(name))
            {
                continue;
            }

            items.Add(new QuickTagFolderEntry
            {
                Path = dir,
                Filename = name,
                Dir = true,
                Playlist = false
            });
        }
    }

    private static void AddBrowsableFiles(List<QuickTagFolderEntry> items, string rootPath, List<string> allowedRoots)
    {
        foreach (var file in Directory.EnumerateFiles(rootPath))
        {
            if (!IsPathInAllowedRoots(file, allowedRoots))
            {
                continue;
            }

            var name = Path.GetFileName(file);
            if (IsHiddenOrInvalidEntryName(name))
            {
                continue;
            }

            var extension = Path.GetExtension(file);
            if (ShouldIgnoreTagEditorFile(file, extension))
            {
                continue;
            }

            var playlist = PlaylistExtensions.Contains(extension);
            var audio = AudioExtensions.Contains(extension);
            if (!playlist && !audio)
            {
                continue;
            }

            items.Add(new QuickTagFolderEntry
            {
                Path = file,
                Filename = name,
                Dir = false,
                Playlist = playlist
            });
        }
    }

    private static bool IsHiddenOrInvalidEntryName(string? name)
    {
        return string.IsNullOrWhiteSpace(name) || name.StartsWith('.');
    }

    public QuickTagLoadResult Load(QuickTagLoadRequest request)
    {
        var allowedRoots = ResolveAllowedLibraryRoots();
        var path = request.Path?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            path = allowedRoots[0];
        }
        else
        {
            var normalized = Path.GetFullPath(path);
            if (!IsPathInAllowedRoots(normalized, allowedRoots))
            {
                path = allowedRoots[0];
            }
        }

        var files = ResolveLoadFiles(path!, request.Recursive == true, allowedRoots);
        var output = new List<QuickTagFileDto>();
        var failed = new List<QuickTagFailedDto>();

        foreach (var filePath in files)
        {
            try
            {
                var dto = LoadFile(filePath, request.Separators ?? QuickTagSeparators.Default);
                output.Add(dto);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add(new QuickTagFailedDto
                {
                    Path = filePath,
                    Error = ex.Message
                });

                _logger.LogDebug(ex, "QuickTag failed to load file Path");
            }
        }

        return new QuickTagLoadResult
        {
            Files = output,
            Failed = failed
        };
    }

    public QuickTagFileDto Save(QuickTagSaveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            throw new InvalidOperationException(PathRequiredMessage);
        }

        var allowedRoots = ResolveAllowedLibraryRoots();
        var fullPath = Path.GetFullPath(request.Path);
        EnsurePathAllowed(fullPath, allowedRoots);

        if (!IOFile.Exists(fullPath))
        {
            throw new FileNotFoundException(FileNotFoundMessage, fullPath);
        }

        var extension = Path.GetExtension(fullPath);
        if (!AudioExtensions.Contains(extension) || ShouldIgnoreTagEditorFile(fullPath, extension))
        {
            throw new InvalidOperationException("Unsupported audio file type.");
        }

        var separators = request.Separators ?? QuickTagSeparators.Default;
        var (genreAliasMap, splitCompositeGenres) = ResolveGenreNormalization();
        var rawTagWriteOptions = new RawTagWriteOptions(separators, request.Id3CommLang, genreAliasMap, splitCompositeGenres);
        var chapterSnapshot = AtlTagHelper.CaptureChapters(fullPath, extension, _logger);

        using (var file = TagLib.File.Create(fullPath))
        {
            foreach (var change in request.Changes)
            {
                ApplyChange(file, extension, change, rawTagWriteOptions);
            }

            file.Save();
        }

        ApplyMp4AtlFallbackIfNeeded(fullPath, extension, request, separators, genreAliasMap, splitCompositeGenres);

        AtlTagHelper.RestoreChapters(fullPath, chapterSnapshot, _logger);

        return LoadFile(fullPath, separators);
    }

    public QuickTagCloneResult CloneAllTags(
        string sourcePath,
        string destinationPath,
        bool enforceLibraryPathCheck = true)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
        {
            return QuickTagCloneResult.Fail("Source and destination paths are required.");
        }

        try
        {
            var sourceFullPath = Path.GetFullPath(sourcePath);
            var destinationFullPath = Path.GetFullPath(destinationPath);

            if (enforceLibraryPathCheck)
            {
                var allowedRoots = ResolveAllowedLibraryRoots();
                EnsurePathAllowed(sourceFullPath, allowedRoots);
                EnsurePathAllowed(destinationFullPath, allowedRoots);
            }

            if (!IOFile.Exists(sourceFullPath))
            {
                return QuickTagCloneResult.Fail($"Source file not found: {sourceFullPath}");
            }

            if (!IOFile.Exists(destinationFullPath))
            {
                return QuickTagCloneResult.Fail($"Destination file not found: {destinationFullPath}");
            }

            var sourceExtension = Path.GetExtension(sourceFullPath);
            var destinationExtension = Path.GetExtension(destinationFullPath);
            if (!AudioExtensions.Contains(sourceExtension) || ShouldIgnoreTagEditorFile(sourceFullPath, sourceExtension))
            {
                return QuickTagCloneResult.Fail("Unsupported source audio file type.");
            }

            if (!AudioExtensions.Contains(destinationExtension) || ShouldIgnoreTagEditorFile(destinationFullPath, destinationExtension))
            {
                return QuickTagCloneResult.Fail("Unsupported destination audio file type.");
            }

            var separators = QuickTagSeparators.Default;
            var chapterSnapshot = AtlTagHelper.CaptureChapters(sourceFullPath, sourceExtension, _logger);
            Dictionary<string, List<string>> rawTags;
            string? title;
            string[] performers;
            string? album;
            string[] albumArtists;
            string[] composers;
            string[] genres;
            uint year;
            uint beatsPerMinute;
            uint track;
            uint trackCount;
            uint disc;
            uint discCount;
            string? comment;
            string? lyrics;
            string? copyright;
            string? initialKey;
            string? isrc;
            string? grouping;
            string? conductor;
            IPicture[] pictures;

            using (var sourceFile = TagLib.File.Create(sourceFullPath))
            {
                rawTags = ReadAllTags(sourceFile, sourceExtension, separators);
                title = sourceFile.Tag.Title;
                performers = (sourceFile.Tag.Performers ?? Array.Empty<string>()).ToArray();
                album = sourceFile.Tag.Album;
                albumArtists = (sourceFile.Tag.AlbumArtists ?? Array.Empty<string>()).ToArray();
                composers = (sourceFile.Tag.Composers ?? Array.Empty<string>()).ToArray();
                genres = (sourceFile.Tag.Genres ?? Array.Empty<string>()).ToArray();
                year = sourceFile.Tag.Year;
                beatsPerMinute = sourceFile.Tag.BeatsPerMinute;
                track = sourceFile.Tag.Track;
                trackCount = sourceFile.Tag.TrackCount;
                disc = sourceFile.Tag.Disc;
                discCount = sourceFile.Tag.DiscCount;
                comment = sourceFile.Tag.Comment;
                lyrics = sourceFile.Tag.Lyrics;
                copyright = sourceFile.Tag.Copyright;
                initialKey = sourceFile.Tag.InitialKey;
                isrc = sourceFile.Tag.ISRC;
                grouping = sourceFile.Tag.Grouping;
                conductor = sourceFile.Tag.Conductor;
                pictures = ClonePictures(sourceFile.Tag.Pictures);
            }

            var (genreAliasMap, splitCompositeGenres) = ResolveGenreNormalization();
            var cloneRawTagWriteOptions = new RawTagWriteOptions(separators, null, genreAliasMap, splitCompositeGenres);
            using (var destinationFile = TagLib.File.Create(destinationFullPath))
            {
                destinationFile.Tag.Title = title;
                destinationFile.Tag.Performers = performers;
                destinationFile.Tag.Album = album;
                destinationFile.Tag.AlbumArtists = albumArtists;
                destinationFile.Tag.Composers = composers;
                destinationFile.Tag.Genres = FilterBlockedGenres(genres, genreAliasMap, splitCompositeGenres).ToArray();
                destinationFile.Tag.Year = year;
                destinationFile.Tag.BeatsPerMinute = beatsPerMinute;
                destinationFile.Tag.Track = track;
                destinationFile.Tag.TrackCount = trackCount;
                destinationFile.Tag.Disc = disc;
                destinationFile.Tag.DiscCount = discCount;
                destinationFile.Tag.Comment = comment;
                destinationFile.Tag.Lyrics = lyrics;
                destinationFile.Tag.Copyright = copyright;
                destinationFile.Tag.InitialKey = initialKey;
                destinationFile.Tag.ISRC = isrc;
                destinationFile.Tag.Grouping = grouping;
                destinationFile.Tag.Conductor = conductor;
                destinationFile.Tag.Pictures = pictures;

                foreach (var pair in rawTags)
                {
                    if (pair.Value.Count == 0)
                    {
                        continue;
                    }

                    var rawName = NormalizeRawTagNameForDestination(pair.Key, destinationExtension);
                    try
                    {
                        SetRawTag(destinationFile, destinationExtension, rawName, pair.Value, cloneRawTagWriteOptions);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(
                            ex,
                            "QuickTag clone skipped raw tag {Tag} for destination {Path}",
                            rawName,
                            destinationFullPath);
                    }
                }

                destinationFile.Save();
            }

            AtlTagHelper.RestoreChapters(destinationFullPath, chapterSnapshot, _logger);
            return QuickTagCloneResult.Ok(rawTags.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "QuickTag clone failed for {Source} -> {Destination}", sourcePath, destinationPath);
            return QuickTagCloneResult.Fail(ex.Message);
        }
    }

    private void ApplyMp4AtlFallbackIfNeeded(
        string fullPath,
        string extension,
        QuickTagSaveRequest request,
        QuickTagSeparators separators,
        IReadOnlyDictionary<string, string> genreAliasMap,
        bool splitCompositeGenres)
    {
        if (!AtlTagHelper.IsMp4Family(extension) || request.Changes.Count == 0)
        {
            return;
        }

        var hasAppleTags = false;
        try
        {
            using var verify = TagLib.File.Create(fullPath);
            hasAppleTags = (verify.TagTypesOnDisk & TagTypes.Apple) != 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "QuickTag failed to verify MP4 tag state before ATL fallback for Path");
        }

        if (hasAppleTags)
        {
            return;
        }

        try
        {
            var track = new ATL.Track(fullPath);
            var additional = track.AdditionalFields != null
                ? new Dictionary<string, string>(track.AdditionalFields, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var change in request.Changes)
            {
                ApplyMp4AtlChange(track, additional, change, separators, genreAliasMap, splitCompositeGenres);
            }

            track.AdditionalFields = additional;
            var saved = track.Save();
            if (saved)
            {
                _logger.LogInformation("QuickTag used ATL MP4 fallback writer for Path");
            }
            else
            {
                _logger.LogWarning("QuickTag ATL MP4 fallback did not report a successful save for Path");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "QuickTag ATL MP4 fallback failed for Path");
        }
    }

    private readonly record struct Mp4AtlChangeContext(
        ATL.Track Track,
        Dictionary<string, string> Additional,
        QuickTagChangeRequest Change,
        QuickTagSeparators Separators,
        IReadOnlyDictionary<string, string> GenreAliasMap,
        bool SplitCompositeGenres);

    private static readonly Dictionary<string, Action<Mp4AtlChangeContext>> Mp4AtlChangeHandlers = new(StringComparer.Ordinal)
    {
        ["title"] = static context => context.Track.Title = ParseSingleString(context.Change.Value) ?? string.Empty,
        ["artist"] = static context => context.Track.Artist = JoinValues(ParseStringValues(context.Change.Value), context.Separators.Mp4 ?? ", "),
        ["album"] = static context => context.Track.Album = ParseSingleString(context.Change.Value) ?? string.Empty,
        ["albumartist"] = static context => context.Track.AlbumArtist = JoinValues(ParseStringValues(context.Change.Value), context.Separators.Mp4 ?? ", "),
        ["composer"] = static context => context.Track.Composer = JoinValues(ParseStringValues(context.Change.Value), context.Separators.Mp4 ?? ", "),
        ["genre"] = static context => ApplyMp4AtlGenre(context),
        ["year"] = static context => ApplyMp4AtlYear(context),
        ["bpm"] = static context => context.Track.BPM = (int)(ParseOptionalUInt(context.Change.Value) ?? 0u),
        ["key"] = static context => ApplyMp4AtlKey(context),
        ["track"] = static context => ApplyMp4AtlPosition(context.Track, ParsePosition(context.Change.Value), isTrack: true),
        ["disc"] = static context => ApplyMp4AtlPosition(context.Track, ParsePosition(context.Change.Value), isTrack: false),
        ["lyrics"] = static context => ApplyMp4AtlLyrics(context),
        ["rating"] = static context => SetAtlAdditionalField(context.Additional, BuildAtlDashFieldName(RatingTag), (ParseRating(context.Change.Value) * 20).ToString(CultureInfo.InvariantCulture)),
        ["isrc"] = static context => ApplyMp4AtlIsrc(context),
        ["raw"] = static context => ApplyMp4AtlRawChange(context)
    };

    private static void ApplyMp4AtlChange(
        ATL.Track track,
        Dictionary<string, string> additional,
        QuickTagChangeRequest change,
        QuickTagSeparators separators,
        IReadOnlyDictionary<string, string> genreAliasMap,
        bool splitCompositeGenres)
    {
        var type = (change.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (!Mp4AtlChangeHandlers.TryGetValue(type, out var handler))
        {
            return;
        }

        handler(new Mp4AtlChangeContext(track, additional, change, separators, genreAliasMap, splitCompositeGenres));
    }

    private static void ApplyMp4AtlGenre(Mp4AtlChangeContext context)
    {
        var values = FilterBlockedGenres(ParseStringValues(context.Change.Value), context.GenreAliasMap, context.SplitCompositeGenres);
        context.Track.Genre = JoinValues(values, context.Separators.Mp4 ?? ", ");
    }

    private static void ApplyMp4AtlYear(Mp4AtlChangeContext context)
    {
        var year = ParseOptionalUInt(context.Change.Value);
        if (year.HasValue && year.Value > 0 && year.Value <= 9999)
        {
            context.Track.Date = new DateTime((int)year.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SetAtlAdditionalField(context.Additional, "DATE", year.Value.ToString(CultureInfo.InvariantCulture));
            return;
        }

        SetAtlAdditionalField(context.Additional, "DATE", null);
    }

    private static void ApplyMp4AtlKey(Mp4AtlChangeContext context)
    {
        var key = ParseSingleString(context.Change.Value);
        SetAtlAdditionalField(context.Additional, BuildAtlDashFieldName("KEY"), key);
        SetAtlAdditionalField(context.Additional, "KEY", key);
    }

    private static void ApplyMp4AtlPosition(ATL.Track track, (uint? Number, uint? Total) position, bool isTrack)
    {
        var number = position.Number.HasValue ? (int)position.Number.Value : 0;
        var total = position.Total.HasValue ? (int)position.Total.Value : 0;
        if (isTrack)
        {
            track.TrackNumber = number;
            track.TrackTotal = total;
            return;
        }

        track.DiscNumber = number;
        track.DiscTotal = total;
    }

    private static void ApplyMp4AtlLyrics(Mp4AtlChangeContext context)
    {
        var lyrics = ParseSingleString(context.Change.Value);
        SetAtlAdditionalField(context.Additional, LyricsTag, lyrics);
        SetAtlAdditionalField(context.Additional, BuildAtlDashFieldName(LyricsTag), lyrics);
    }

    private static void ApplyMp4AtlIsrc(Mp4AtlChangeContext context)
    {
        var isrcValue = ParseSingleString(context.Change.Value);
        context.Track.ISRC = isrcValue ?? string.Empty;
        SetAtlAdditionalField(context.Additional, "ISRC", isrcValue);
        SetAtlAdditionalField(context.Additional, BuildAtlDashFieldName("ISRC"), isrcValue);
    }

    private static void ApplyMp4AtlRawChange(Mp4AtlChangeContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Change.Tag))
        {
            return;
        }

        var rawTag = context.Change.Tag;
        var normalized = NormalizeAtlMp4FieldName(rawTag);
        var values = ParseStringValues(context.Change.Value);
        if (IsGenreTag(rawTag) || normalized.Equals(GenreTag, StringComparison.OrdinalIgnoreCase))
        {
            values = FilterBlockedGenres(values, context.GenreAliasMap, context.SplitCompositeGenres);
        }

        var joined = JoinValues(values, context.Separators.Mp4 ?? ", ");
        if (TryApplyMp4AtlRawTextTags(context.Track, context.Additional, rawTag, joined))
        {
            return;
        }

        ApplyMp4AtlRawKnownField(context.Track, normalized, joined);
        SetAtlAdditionalField(context.Additional, normalized, joined);
        SetAtlAdditionalField(context.Additional, BuildAtlDashFieldName(normalized), joined);
    }

    private static bool TryApplyMp4AtlRawTextTags(ATL.Track track, Dictionary<string, string> additional, string rawTag, string joined)
    {
        if (IsCommentTag(rawTag))
        {
            track.Comment = joined;
            SetAtlAdditionalField(additional, BuildAtlDashFieldName(CommentTag), joined);
            SetAtlAdditionalField(additional, CommentTag, joined);
            return true;
        }

        if (IsLyricsTag(rawTag))
        {
            SetAtlAdditionalField(additional, BuildAtlDashFieldName(LyricsTag), joined);
            SetAtlAdditionalField(additional, LyricsTag, joined);
            return true;
        }

        return false;
    }

    private static void ApplyMp4AtlRawKnownField(ATL.Track track, string normalized, string joined)
    {
        switch (normalized.ToUpperInvariant())
        {
            case TitleTag:
                track.Title = joined;
                return;
            case ArtistTag:
            case "ARTISTS":
                track.Artist = joined;
                return;
            case AlbumTag:
                track.Album = joined;
                return;
            case AlbumArtistTag:
            case "ALBUM ARTIST":
                track.AlbumArtist = joined;
                return;
            case ComposerTag:
                track.Composer = joined;
                return;
            case GenreTag:
                track.Genre = joined;
                return;
            case "COPYRIGHT":
                track.Copyright = joined;
                return;
            case "ISRC":
                track.ISRC = joined;
                return;
            case "DATE":
            case "YEAR":
                if (TryParseYear(joined, out var parsedYear))
                {
                    track.Date = new DateTime(parsedYear, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                }

                return;
        }
    }

    private static void SetAtlAdditionalField(Dictionary<string, string> fields, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            fields.Remove(key);
            return;
        }

        fields[key] = value.Trim();
    }

    private static IPicture[] ClonePictures(IPicture[]? pictures)
    {
        if (pictures == null || pictures.Length == 0)
        {
            return Array.Empty<IPicture>();
        }

        var output = new List<IPicture>(pictures.Length);
        foreach (var picture in pictures)
        {
            if (picture?.Data == null || picture.Data.Count == 0)
            {
                continue;
            }

            output.Add(new TagLib.Picture(new ByteVector(picture.Data))
            {
                Type = picture.Type,
                Description = picture.Description ?? string.Empty,
                MimeType = picture.MimeType ?? "image/jpeg"
            });
        }

        return output.ToArray();
    }

    private static string BuildAtlDashFieldName(string name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? string.Empty
            : $"----:com.apple.iTunes:{name.Trim()}";
    }

    private static string NormalizeAtlMp4FieldName(string rawName)
    {
        var normalized = Mp4RawTagNameNormalizer.Normalize(rawName).Trim();
        return normalized.ToLowerInvariant() switch
        {
            "©nam" => TitleTag,
            "©art" => ArtistTag,
            "©alb" => AlbumTag,
            "aart" => AlbumArtistTag,
            "©wrt" => ComposerTag,
            "©gen" => GenreTag,
            "©day" => "DATE",
            "tmpo" => "BPM",
            "trkn" => "TRACK",
            "disk" => "DISC",
            "©cmt" => CommentTag,
            "©lyr" => LyricsTag,
            "cprt" => "COPYRIGHT",
            _ => normalized
        };
    }

    private static string NormalizeRawTagNameForDestination(string rawName, string destinationExtension)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return rawName;
        }

        if (destinationExtension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || destinationExtension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase)
            || destinationExtension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            || destinationExtension.Equals(OpusExtension, StringComparison.OrdinalIgnoreCase))
        {
            return Mp4RawTagNameNormalizer.Normalize(rawName).Trim();
        }

        return rawName;
    }

    private static bool TryParseYear(string input, out int year)
    {
        year = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        if (trimmed.Length >= 4 && int.TryParse(trimmed[..4], out var parsed) && parsed is >= 1 and <= 9999)
        {
            year = parsed;
            return true;
        }

        return false;
    }

    public QuickTagDumpResult Dump(string path, bool includeArtworkData = false, bool enforceLibraryPathCheck = true)
    {
        var fullPath = ResolveValidatedAudioPath(path, enforceLibraryPathCheck);
        var extension = Path.GetExtension(fullPath);
        EnsureAudioFileTypeSupported(fullPath, extension);

        using var file = TagLib.File.Create(fullPath);
        var meta = BuildFileDto(file, fullPath, QuickTagSeparators.Default);
        var props = file.Properties;

        var audio = new QuickTagDumpAudioInfo
        {
            DurationMs = (long)Math.Round(props.Duration.TotalMilliseconds),
            BitrateKbps = props.AudioBitrate,
            SampleRate = props.AudioSampleRate,
            BitsPerSample = props.BitsPerSample,
            Channels = props.AudioChannels,
            ChannelLayout = DescribeChannels(props.AudioChannels),
            Description = props.Description ?? string.Empty,
            MimeType = file.MimeType ?? string.Empty
        };

        var pictures = BuildDumpPictures(file.Tag.Pictures, includeArtworkData);
        var isrc = string.IsNullOrWhiteSpace(file.Tag.ISRC)
            ? FirstTagValue(meta.Tags, "ISRC", "TSRC", ITunesIsrcTag)
            : file.Tag.ISRC;

        return new QuickTagDumpResult
        {
            Path = fullPath,
            Extension = extension,
            Format = meta.Format,
            Audio = audio,
            Meta = BuildDumpMeta(meta, isrc),
            Tags = meta.Tags,
            Pictures = pictures
        };
    }

    private string ResolveValidatedAudioPath(string path, bool enforceLibraryPathCheck)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(PathRequiredMessage);
        }

        var fullPath = Path.GetFullPath(path);
        if (enforceLibraryPathCheck)
        {
            var allowedRoots = ResolveAllowedLibraryRoots();
            EnsurePathAllowed(fullPath, allowedRoots);
        }

        if (!IOFile.Exists(fullPath))
        {
            throw new FileNotFoundException(FileNotFoundMessage, fullPath);
        }

        return fullPath;
    }

    private static void EnsureAudioFileTypeSupported(string fullPath, string extension)
    {
        if (AudioExtensions.Contains(extension) && !ShouldIgnoreTagEditorFile(fullPath, extension))
        {
            return;
        }

        throw new InvalidOperationException("Unsupported audio file type.");
    }

    private static List<QuickTagDumpPictureInfo> BuildDumpPictures(IPicture[]? pictures, bool includeArtworkData)
    {
        if (pictures == null || pictures.Length == 0)
        {
            return new List<QuickTagDumpPictureInfo>();
        }

        var output = new List<QuickTagDumpPictureInfo>();
        foreach (var picture in pictures)
        {
            if (picture?.Data == null || picture.Data.Count == 0)
            {
                continue;
            }

            output.Add(new QuickTagDumpPictureInfo
            {
                MimeType = picture.MimeType ?? string.Empty,
                Type = (int)picture.Type,
                Description = picture.Description ?? string.Empty,
                Size = picture.Data.Count,
                Data = includeArtworkData ? Convert.ToBase64String(picture.Data.Data) : null
            });
        }

        return output;
    }

    private static QuickTagDumpMeta BuildDumpMeta(QuickTagFileDto meta, string? isrc)
    {
        return new QuickTagDumpMeta
        {
            Title = meta.Title,
            Artists = meta.Artists,
            Album = meta.Album,
            AlbumArtists = meta.AlbumArtists,
            Composers = meta.Composers,
            TrackNumber = meta.TrackNumber,
            TrackTotal = meta.TrackTotal,
            DiscNumber = meta.DiscNumber,
            DiscTotal = meta.DiscTotal,
            Genres = meta.Genres,
            Bpm = meta.Bpm,
            Rating = meta.Rating,
            Year = meta.Year,
            Key = meta.Key,
            Isrc = string.IsNullOrWhiteSpace(isrc) ? null : isrc,
            HasArtwork = meta.HasArtwork,
            ArtworkDescription = meta.ArtworkDescription,
            ArtworkType = meta.ArtworkType
        };
    }

    public byte[] LoadThumbnail(string path, int size = 50, bool crop = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(PathRequiredMessage);
        }

        size = Math.Clamp(size, 24, 1200);

        var allowedRoots = ResolveAllowedLibraryRoots();
        var fullPath = Path.GetFullPath(path);
        EnsurePathAllowed(fullPath, allowedRoots);

        if (!IOFile.Exists(fullPath))
        {
            throw new FileNotFoundException(FileNotFoundMessage, fullPath);
        }

        using var file = TagLib.File.Create(fullPath);
        var picture = file.Tag.Pictures?.FirstOrDefault();
        if (picture == null || picture.Data == null || picture.Data.Count == 0)
        {
            throw new InvalidOperationException("Album art not found.");
        }

        using var image = Image.Load(picture.Data.Data);
        image.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Mode = crop ? ResizeMode.Crop : ResizeMode.Max,
            Position = AnchorPositionMode.Center,
            Size = new Size(size, size)
        }));

        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder
        {
            Quality = 95
        });
        return ms.ToArray();
    }

    private static string DescribeChannels(int channels)
    {
        return channels switch
        {
            1 => "Mono",
            2 => "Stereo",
            6 => "5.1",
            8 => "7.1",
            _ => channels > 0 ? $"{channels} channels" : string.Empty
        };
    }

    public QuickTagAudioFile OpenAudio(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(PathRequiredMessage);
        }

        var allowedRoots = ResolveAllowedLibraryRoots();
        var fullPath = Path.GetFullPath(path);
        EnsurePathAllowed(fullPath, allowedRoots);

        if (!IOFile.Exists(fullPath))
        {
            throw new FileNotFoundException(FileNotFoundMessage, fullPath);
        }

        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (!AudioExtensions.Contains(extension))
        {
            throw new InvalidOperationException("Unsupported audio file type.");
        }

        var contentType = extension switch
        {
            ".mp3" => "audio/mpeg",
            FlacExtension => "audio/flac",
            ".m4a" => "audio/mp4",
            ".mp4" => "audio/mp4",
            ".m4b" => "audio/mp4",
            ".wav" => "audio/wav",
            ".aif" => "audio/aiff",
            AiffExtension => "audio/aiff",
            ".ogg" => "audio/ogg",
            OpusExtension => "audio/ogg",
            _ => "application/octet-stream"
        };

        return new QuickTagAudioFile
        {
            Path = fullPath,
            ContentType = contentType,
            Stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        };
    }

    private List<string> ResolveAllowedLibraryRoots()
    {
        IReadOnlyList<FolderDto> folders;

        try
        {
            folders = _libraryRepository.IsConfigured
                ? _libraryRepository.GetFoldersAsync().GetAwaiter().GetResult()
                : _configStore.GetFolders();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "QuickTag failed to load library folders from repository. Falling back to local config store.");
            folders = _configStore.GetFolders();
        }

        return LibraryFolderRootResolver.ResolveAccessibleRoots(folders, throwWhenNone: true).ToList();
    }

    private static void EnsurePathAllowed(string path, IReadOnlyList<string> allowedRoots)
    {
        if (!IsPathInAllowedRoots(path, allowedRoots))
        {
            throw new InvalidOperationException("Path is outside configured library folders.");
        }
    }

    private static bool IsPathInAllowedRoots(string path, IReadOnlyList<string> allowedRoots)
    {
        return allowedRoots.Any(root => IsPathUnderRoot(path, root));
    }

    private static bool IsPathAllowedForBrowsing(string path, IReadOnlyList<string> allowedRoots)
    {
        if (IsPathInAllowedRoots(path, allowedRoots))
        {
            return true;
        }

        var normalized = Path.GetFullPath(path);
        return allowedRoots.Any(root => IsPathUnderRoot(root, normalized));
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSlash = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveBrowsePath(string? inputPath, string? subdir, IReadOnlyList<string> allowedRoots)
    {
        var defaultRoot = allowedRoots[0];
        var basePath = string.IsNullOrWhiteSpace(inputPath)
            ? defaultRoot
            : Path.GetFullPath(inputPath!);

        var sub = subdir?.Trim() ?? string.Empty;

        if (IOFile.Exists(basePath))
        {
            basePath = Path.GetDirectoryName(basePath) ?? defaultRoot;
        }

        if (!IsPathAllowedForBrowsing(basePath, allowedRoots))
        {
            basePath = defaultRoot;
        }

        if (string.IsNullOrWhiteSpace(sub))
        {
            return basePath;
        }

        var combined = Path.IsPathRooted(sub)
            ? sub
            : Path.Join(basePath, sub);

        var resolved = Path.GetFullPath(combined);
        if (!IsPathAllowedForBrowsing(resolved, allowedRoots))
        {
            return basePath;
        }

        if (Directory.Exists(resolved))
        {
            return resolved;
        }

        if (IOFile.Exists(resolved))
        {
            if (IsPathInAllowedRoots(resolved, allowedRoots))
            {
                return Path.GetDirectoryName(resolved) ?? basePath;
            }

            return basePath;
        }

        return basePath;
    }

    private static IReadOnlyList<string> ResolveLoadFiles(string path, bool recursive, IReadOnlyList<string> allowedRoots)
    {
        var fullPath = Path.GetFullPath(path);
        EnsurePathAllowed(fullPath, allowedRoots);

        if (Directory.Exists(fullPath))
        {
            var search = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return Directory.EnumerateFiles(fullPath, "*", search)
                .Where(file => AudioExtensions.Contains(Path.GetExtension(file)))
                .Where(file => !ShouldIgnoreTagEditorFile(file))
                .Where(file => IsPathInAllowedRoots(file, allowedRoots))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (!IOFile.Exists(fullPath))
        {
            return Array.Empty<string>();
        }

        var extension = Path.GetExtension(fullPath);
        if (PlaylistExtensions.Contains(extension))
        {
            return ParsePlaylist(fullPath)
                .Where(file => AudioExtensions.Contains(Path.GetExtension(file)) && IOFile.Exists(file))
                .Where(file => !ShouldIgnoreTagEditorFile(file))
                .Where(file => IsPathInAllowedRoots(file, allowedRoots))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (AudioExtensions.Contains(extension) && !ShouldIgnoreTagEditorFile(fullPath, extension))
        {
            return new[] { fullPath };
        }

        return Array.Empty<string>();
    }

    private static List<string> ParsePlaylist(string playlistPath)
    {
        var extension = Path.GetExtension(playlistPath).ToLowerInvariant();
        var directory = Path.GetDirectoryName(playlistPath) ?? string.Empty;
        var files = new List<string>();

        foreach (var line in EnumeratePlaylistContentLines(playlistPath))
        {
            var candidate = TryResolvePlaylistCandidate(line, extension);

            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var normalized = NormalizePlaylistCandidatePath(candidate, directory);
            if (ShouldIgnoreTagEditorFile(normalized))
            {
                continue;
            }

            files.Add(normalized);
        }

        return files;
    }

    private static IEnumerable<string> EnumeratePlaylistContentLines(string playlistPath)
    {
        return IOFile.ReadAllLines(playlistPath)
            .Select(rawLine => rawLine.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'));
    }

    private static string? TryResolvePlaylistCandidate(string line, string extension)
    {
        if (!extension.Equals(".pls", StringComparison.OrdinalIgnoreCase))
        {
            return line;
        }

        var idx = line.IndexOf('=');
        if (idx <= 0)
        {
            return null;
        }

        var key = line[..idx].Trim();
        if (!key.StartsWith("File", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return line[(idx + 1)..].Trim();
    }

    private static string NormalizePlaylistCandidatePath(string candidate, string directory)
    {
        return Path.IsPathRooted(candidate)
            ? candidate
            : Path.GetFullPath(Path.Join(directory, candidate));
    }

    private static bool ShouldIgnoreTagEditorFile(string path, string? extensionOverride = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = (extensionOverride ?? Path.GetExtension(path)).Trim();
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        if (LyricsSidecarExtensions.Contains(extension))
        {
            return true;
        }

        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            && LyricsFileNameTokens.Any(token => fileNameWithoutExtension.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webm", StringComparison.OrdinalIgnoreCase))
        {
            return AnimatedArtworkTokens.Any(token => fileNameWithoutExtension.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private QuickTagFileDto LoadFile(string filePath, QuickTagSeparators separators)
    {
        using var file = TagLib.File.Create(filePath);
        return BuildFileDto(file, filePath, separators);
    }

    private QuickTagFileDto BuildFileDto(TagLib.File file, string filePath, QuickTagSeparators separators)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var tags = ReadAllTags(file, extension, separators);
        AtlTagHelper.AppendChapterTags(tags, filePath, extension, _logger);

        var title = ResolveFileTitle(filePath, file, tags);
        var artists = ResolveFileArtists(file, tags);
        var genres = ResolveFileGenres(file, tags);
        var bpm = ResolveFileBpm(file, tags);
        var year = ResolveFileYear(file, tags);
        var album = ResolveFileAlbum(file, tags);
        var albumArtists = ResolveFileAlbumArtists(file, tags);
        var composers = ResolveFileComposers(file, tags);
        var key = ResolveFileKey(file, tags);

        var (trackNumber, trackTotal) = ReadPositionValues(
            file.Tag.Track,
            file.Tag.TrackCount,
            FirstTagValue(tags, "TRCK", "TRACKNUMBER", "TRACK", "trkn", "iTunes:TRACK"));
        var (discNumber, discTotal) = ReadPositionValues(
            file.Tag.Disc,
            file.Tag.DiscCount,
            FirstTagValue(tags, "TPOS", "DISCNUMBER", "DISC", "disk", "iTunes:DISC"));

        var publisher = FirstTagValue(tags, "TPUB", "PUBLISHER", "iTunes:PUBLISHER");
        var copyright = FirstTagValue(tags, "TCOP", "COPYRIGHT", "cprt", "iTunes:COPYRIGHT");
        var language = FirstTagValue(tags, "TLAN", "LANGUAGE", "iTunes:LANGUAGE");
        var lyricist = FirstTagValue(tags, "TEXT", "LYRICIST", "iTunes:LYRICIST");
        var conductor = FirstTagValue(tags, "TPE3", "CONDUCTOR", "iTunes:CONDUCTOR");

        var isrc = file.Tag.ISRC;
        if (string.IsNullOrWhiteSpace(isrc))
        {
            isrc = FirstTagValue(tags, "TSRC", "ISRC", ITunesIsrcTag);
        }

        var rating = ReadRating(file, extension, tags);
        var hasArtwork = file.Tag.Pictures?.Any(pic => pic?.Data != null && pic.Data.Count > 0) == true;
        var firstArtwork = file.Tag.Pictures?.FirstOrDefault(pic => pic?.Data != null && pic.Data.Count > 0);
        var artworkVersion = IOFile.GetLastWriteTimeUtc(filePath).Ticks;

        var props = file.Properties;
        long fileSizeBytes = 0;
        try
        {
            fileSizeBytes = new FileInfo(filePath).Length;
        }
        catch (IOException)
        {
            // Ignore missing/inaccessible files when building preview metadata.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore unreadable files when building preview metadata.
        }

        return new QuickTagFileDto
        {
            Path = filePath,
            Format = ResolveFormatName(extension),
            Title = title ?? string.Empty,
            Artists = artists,
            Album = string.IsNullOrWhiteSpace(album) ? null : album,
            AlbumArtists = albumArtists,
            Composers = composers,
            TrackNumber = trackNumber,
            TrackTotal = trackTotal,
            DiscNumber = discNumber,
            DiscTotal = discTotal,
            Genres = genres,
            Bpm = bpm,
            Rating = rating,
            Tags = tags,
            Year = year,
            Key = string.IsNullOrWhiteSpace(key) ? null : key,
            Publisher = string.IsNullOrWhiteSpace(publisher) ? null : publisher,
            Copyright = string.IsNullOrWhiteSpace(copyright) ? null : copyright,
            Language = string.IsNullOrWhiteSpace(language) ? null : language,
            Lyricist = string.IsNullOrWhiteSpace(lyricist) ? null : lyricist,
            Conductor = string.IsNullOrWhiteSpace(conductor) ? null : conductor,
            Isrc = string.IsNullOrWhiteSpace(isrc) ? null : isrc,
            FileName = Path.GetFileName(filePath),
            BitrateKbps = props?.AudioBitrate ?? 0,
            DurationMs = (long)Math.Round(props?.Duration.TotalMilliseconds ?? 0),
            FileSizeBytes = fileSizeBytes,
            HasArtwork = hasArtwork,
            ArtworkVersion = artworkVersion,
            ArtworkDescription = firstArtwork?.Description ?? string.Empty,
            ArtworkType = firstArtwork != null ? (int)firstArtwork.Type : 3
        };
    }

    private static string ResolveFileTitle(string filePath, TagLib.File file, Dictionary<string, List<string>> tags)
    {
        var title = file.Tag.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = FirstTagValue(tags, "TIT2", TitleTag, "©nam", "iTunes:TITLE");
        }

        return string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(filePath)
            : title;
    }

    private static List<string> ResolveFileArtists(TagLib.File file, Dictionary<string, List<string>> tags)
    {
        var artists = CleanValues(file.Tag.Performers);
        return artists.Count > 0
            ? artists
            : CleanValues(ReadTagValues(tags, "TPE1", ArtistTag, "©ART", "iTunes:ARTIST", "iTunes:ARTISTS"));
    }

    private static List<string> ResolveFileGenres(TagLib.File file, Dictionary<string, List<string>> tags)
    {
        var genres = CleanValues(file.Tag.Genres);
        return genres.Count > 0
            ? genres
            : CleanValues(ReadTagValues(tags, "TCON", GenreTag, "©gen", "iTunes:GENRE"));
    }

    private static int? ResolveFileBpm(TagLib.File file, Dictionary<string, List<string>> tags)
    {
        if (file.Tag.BeatsPerMinute > 0)
        {
            return (int)file.Tag.BeatsPerMinute;
        }

        if (!TryGetTagValues(tags, out var bpmValues, "BPM", "TBPM", "tmpo", "iTunes:BPM"))
        {
            return null;
        }

        return int.TryParse(bpmValues.FirstOrDefault(), out var parsedBpm)
            ? parsedBpm
            : null;
    }

    private static int? ResolveFileYear(TagLib.File file, Dictionary<string, List<string>> tags) =>
        file.Tag.Year > 0
            ? (int)file.Tag.Year
            : ParseNullableInt(FirstTagValue(tags, "TDRC", "TYER", "DATE", "YEAR", "©day", "iTunes:DATE", "iTunes:YEAR"));

    private static string? ResolveFileAlbum(TagLib.File file, Dictionary<string, List<string>> tags) =>
        string.IsNullOrWhiteSpace(file.Tag.Album)
            ? FirstTagValue(tags, "TALB", AlbumTag, "©alb", "iTunes:ALBUM")
            : file.Tag.Album;

    private static List<string> ResolveFileAlbumArtists(TagLib.File file, Dictionary<string, List<string>> tags)
    {
        var albumArtists = CleanValues(file.Tag.AlbumArtists);
        return albumArtists.Count > 0
            ? albumArtists
            : CleanValues(ReadTagValues(tags, "TPE2", AlbumArtistTag, "ALBUM ARTIST", "aART", "iTunes:ALBUMARTIST", "iTunes:ALBUM ARTIST"));
    }

    private static List<string> ResolveFileComposers(TagLib.File file, Dictionary<string, List<string>> tags)
    {
        var composers = CleanValues(file.Tag.Composers);
        return composers.Count > 0
            ? composers
            : CleanValues(ReadTagValues(tags, "TCOM", ComposerTag, "©wrt", "iTunes:COMPOSER"));
    }

    private static string? ResolveFileKey(TagLib.File file, Dictionary<string, List<string>> tags)
    {
        if (!string.IsNullOrWhiteSpace(file.Tag.InitialKey))
        {
            return file.Tag.InitialKey;
        }

        if (TryGetTagValues(tags, out var keyValues, "TKEY", "INITIALKEY", "KEY"))
        {
            return keyValues.FirstOrDefault();
        }

        return FirstTagValue(tags, "iTunes:KEY");
    }

    private static bool TryGetTagValues(
        Dictionary<string, List<string>> tags,
        out List<string> values,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (tags.TryGetValue(key, out values!))
            {
                return true;
            }
        }

        values = new List<string>();
        return false;
    }

    private static Dictionary<string, List<string>> ReadAllTags(TagLib.File file, string extension, QuickTagSeparators separators)
    {
        var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        switch (extension)
        {
            case ".mp3":
            case ".wav":
            case ".aif":
            case AiffExtension:
                ReadId3Tags(tags, file, extension, separators);
                break;

            case FlacExtension:
            case ".ogg":
            case OpusExtension:
                ReadVorbisTags(tags, file, separators);
                break;

            case ".m4a":
            case ".mp4":
            case ".m4b":
                ReadMp4Tags(tags, file, separators);
                break;
        }

        return tags;
    }

    private static void ReadId3Tags(
        Dictionary<string, List<string>> tags,
        TagLib.File file,
        string extension,
        QuickTagSeparators separators)
    {
        var id3 = (TagLib.Id3v2.Tag?)file.GetTag(TagTypes.Id3v2, false);
        if (id3 != null)
        {
            foreach (var frame in id3.GetFrames())
            {
                switch (frame)
                {
                    case TagLib.Id3v2.UserTextInformationFrame userFrame:
                        var key = string.IsNullOrWhiteSpace(userFrame.Description)
                            ? "TXXX"
                            : userFrame.Description;
                        MergeTagValues(tags, key, userFrame.Text, separators.Id3);
                        break;
                    case TagLib.Id3v2.TextInformationFrame textFrame:
                        MergeTagValues(tags, textFrame.FrameId.ToString(), textFrame.Text, separators.Id3);
                        break;
                    case TagLib.Id3v2.CommentsFrame commentsFrame:
                        MergeTagValues(tags, "COMM", new[] { commentsFrame.Text }, separators.Id3);
                        break;
                    case TagLib.Id3v2.UnsynchronisedLyricsFrame lyricsFrame:
                        MergeTagValues(tags, "USLT", new[] { lyricsFrame.Text }, separators.Id3);
                        break;
                }
            }
        }

        MergeTagValues(tags, "COMM", new[] { file.Tag.Comment }, separators.Id3);
        MergeTagValues(tags, "USLT", new[] { file.Tag.Lyrics }, separators.Id3);
        MergeTagValues(tags, "TMOO", ReadRawTagValues(file, extension, "TMOO"), separators.Id3);
    }

    private static void ReadVorbisTags(
        Dictionary<string, List<string>> tags,
        TagLib.File file,
        QuickTagSeparators separators)
    {
        var vorbis = (TagLib.Ogg.XiphComment?)file.GetTag(TagTypes.Xiph, false);
        if (vorbis != null)
        {
            foreach (var pair in EnumerateVorbisFields(vorbis))
            {
                MergeTagValues(tags, pair.Key, pair.Value, separators.Vorbis);
            }
        }

        MergeTagValues(tags, CommentTag, new[] { file.Tag.Comment }, separators.Vorbis);
        MergeTagValues(tags, LyricsTag, new[] { file.Tag.Lyrics }, separators.Vorbis);
    }

    private static void ReadMp4Tags(
        Dictionary<string, List<string>> tags,
        TagLib.File file,
        QuickTagSeparators separators)
    {
        MergeTagValues(tags, "©nam", new[] { file.Tag.Title }, separators.Mp4);
        MergeTagValues(tags, "©ART", file.Tag.Performers, separators.Mp4);
        MergeTagValues(tags, "©alb", new[] { file.Tag.Album }, separators.Mp4);
        MergeTagValues(tags, "aART", file.Tag.AlbumArtists, separators.Mp4);
        MergeTagValues(tags, "©wrt", file.Tag.Composers, separators.Mp4);
        MergeTagValues(tags, "©gen", file.Tag.Genres, separators.Mp4);
        MergeTagValues(tags, "cprt", new[] { file.Tag.Copyright }, separators.Mp4);
        MergeTagValues(tags, ITunesIsrcTag, new[] { file.Tag.ISRC }, separators.Mp4);
        MergeTagValues(tags, "iTunes:KEY", new[] { file.Tag.InitialKey }, separators.Mp4);

        if (file.Tag.Year > 0)
        {
            MergeTagValues(tags, "©day", new[] { file.Tag.Year.ToString() }, separators.Mp4);
        }

        var trackPosition = FormatMp4Position(file.Tag.Track, file.Tag.TrackCount);
        if (!string.IsNullOrWhiteSpace(trackPosition))
        {
            MergeTagValues(tags, "trkn", new[] { trackPosition }, separators.Mp4);
        }

        var discPosition = FormatMp4Position(file.Tag.Disc, file.Tag.DiscCount);
        if (!string.IsNullOrWhiteSpace(discPosition))
        {
            MergeTagValues(tags, "disk", new[] { discPosition }, separators.Mp4);
        }

        if (file.Tag.BeatsPerMinute > 0)
        {
            var bpmValue = ((int)file.Tag.BeatsPerMinute).ToString();
            MergeTagValues(tags, "tmpo", new[] { bpmValue }, separators.Mp4);
        }

        MergeTagValues(tags, "©cmt", new[] { file.Tag.Comment }, separators.Mp4);
        MergeTagValues(tags, "©lyr", new[] { file.Tag.Lyrics }, separators.Mp4);

        var apple = (TagLib.Mpeg4.AppleTag?)file.GetTag(TagTypes.Apple, false);
        if (apple == null)
        {
            return;
        }

        foreach (var key in Mp4DashProbeKeys)
        {
            var values = ReadAppleDashBox(apple, key);
            if (values.Count > 0)
            {
                MergeTagValues(tags, $"iTunes:{key}", values, separators.Mp4);
            }
        }
    }

    private static void MergeTagValues(Dictionary<string, List<string>> target, string key, IEnumerable<string?> values, string? separator)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var split = SplitBySeparator(values, separator);
        if (split.Count == 0)
        {
            return;
        }

        if (!target.TryGetValue(key, out var existing))
        {
            existing = new List<string>();
            target[key] = existing;
        }

        foreach (var value in split.Where(value => !existing.Contains(value, StringComparer.OrdinalIgnoreCase)))
        {
            existing.Add(value);
        }
    }

    private static List<string> SplitBySeparator(IEnumerable<string?> values, string? separator)
    {
        var output = new List<string>();

        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var cleaned = raw.Replace("\0", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                continue;
            }

            if (string.IsNullOrEmpty(separator))
            {
                output.Add(cleaned);
                continue;
            }

            var parts = cleaned.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                output.Add(cleaned);
                continue;
            }

            output.AddRange(parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        return output;
    }

    private static List<string> CleanValues(IEnumerable<string?>? values)
    {
        if (values == null)
        {
            return new List<string>();
        }

        return values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveFormatName(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            FlacExtension => "flac",
            ".aif" => "aiff",
            AiffExtension => "aiff",
            ".m4a" => "mp4",
            ".mp4" => "mp4",
            ".m4b" => "mp4",
            ".wav" => "wav",
            ".ogg" => "ogg",
            OpusExtension => "ogg",
            _ => "mp3"
        };
    }

    private readonly record struct RawTagWriteOptions(
        QuickTagSeparators Separators,
        string? Id3CommLang,
        IReadOnlyDictionary<string, string> GenreAliasMap,
        bool SplitCompositeGenres);

    private static void ApplyChange(
        TagLib.File file,
        string extension,
        QuickTagChangeRequest change,
        RawTagWriteOptions rawTagWriteOptions)
    {
        var type = (change.Type ?? string.Empty).Trim().ToLowerInvariant();
        switch (type)
        {
            case "genre":
                {
                    var values = FilterBlockedGenres(
                        ParseStringValues(change.Value),
                        rawTagWriteOptions.GenreAliasMap,
                        rawTagWriteOptions.SplitCompositeGenres);
                    file.Tag.Genres = values.ToArray();
                    break;
                }

            case "title":
                {
                    file.Tag.Title = ParseSingleString(change.Value);
                    break;
                }

            case "artist":
                {
                    var values = ParseStringValues(change.Value);
                    file.Tag.Performers = values.ToArray();
                    break;
                }

            case "album":
                {
                    file.Tag.Album = ParseSingleString(change.Value);
                    break;
                }

            case "albumartist":
                {
                    var values = ParseStringValues(change.Value);
                    file.Tag.AlbumArtists = values.ToArray();
                    break;
                }

            case "composer":
                {
                    var values = ParseStringValues(change.Value);
                    file.Tag.Composers = values.ToArray();
                    break;
                }

            case "year":
                {
                    var year = ParseOptionalUInt(change.Value);
                    file.Tag.Year = year ?? 0u;
                    break;
                }

            case "bpm":
                {
                    var bpm = ParseOptionalUInt(change.Value);
                    file.Tag.BeatsPerMinute = bpm ?? 0u;
                    break;
                }

            case "key":
                {
                    var key = ParseSingleString(change.Value);
                    file.Tag.InitialKey = string.IsNullOrWhiteSpace(key) ? null : key;
                    break;
                }

            case "track":
                {
                    var (number, total) = ParsePosition(change.Value);
                    file.Tag.Track = number ?? 0u;
                    file.Tag.TrackCount = total ?? (number.HasValue ? file.Tag.TrackCount : 0u);
                    break;
                }

            case "disc":
                {
                    var (number, total) = ParsePosition(change.Value);
                    file.Tag.Disc = number ?? 0u;
                    file.Tag.DiscCount = total ?? (number.HasValue ? file.Tag.DiscCount : 0u);
                    break;
                }

            case "artwork":
                {
                    var artwork = ParseArtworkChange(change.Value);
                    if (artwork.Clear)
                    {
                        file.Tag.Pictures = Array.Empty<IPicture>();
                        break;
                    }

                    var picture = new TagLib.Picture(new ByteVector(artwork.Data))
                    {
                        Type = artwork.ImageType,
                        MimeType = artwork.MimeType,
                        Description = artwork.Description
                    };
                    file.Tag.Pictures = new IPicture[] { picture };
                    break;
                }

            case "lyrics":
                {
                    file.Tag.Lyrics = ParseSingleString(change.Value) ?? string.Empty;
                    break;
                }

            case "rating":
                {
                    var rating = ParseRating(change.Value);
                    WriteRating(file, extension, rating);
                    break;
                }

            case "isrc":
                {
                    var isrcValue = ParseSingleString(change.Value);
                    SetRawTag(file, extension, ResolveIsrcTagName(extension),
                        string.IsNullOrWhiteSpace(isrcValue) ? new List<string>() : new List<string> { isrcValue },
                        rawTagWriteOptions);
                    break;
                }

            case "raw":
                {
                    if (string.IsNullOrWhiteSpace(change.Tag))
                    {
                        return;
                    }

                    var values = ParseStringValues(change.Value);
                    if (IsGenreTag(change.Tag))
                    {
                        values = FilterBlockedGenres(
                            values,
                            rawTagWriteOptions.GenreAliasMap,
                            rawTagWriteOptions.SplitCompositeGenres);
                    }

                    SetRawTag(file, extension, change.Tag, values, rawTagWriteOptions);
                    break;
                }
        }
    }

    private static List<string> ParseStringValues(JsonElement value)
    {
        var output = new List<string>();

        if (value.ValueKind == JsonValueKind.Array)
        {
            output.AddRange(
                value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()?.Trim())
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Cast<string>());

            return output;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                output.Add(text.Trim());
            }
        }

        return output;
    }

    private static string? ParseSingleString(JsonElement value)
    {
        var values = ParseStringValues(value);
        return values.FirstOrDefault();
    }

    private static uint? ParseOptionalUInt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var direct))
        {
            return direct;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var signed) && signed > 0)
        {
            return (uint)signed;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (uint.TryParse(text, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static (uint? Number, uint? Total) ParsePosition(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var number = value.TryGetProperty("number", out var numberElement)
                ? ParseOptionalUInt(numberElement)
                : null;
            var total = value.TryGetProperty("total", out var totalElement)
                ? ParseOptionalUInt(totalElement)
                : null;
            return (number, total);
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            var number = ParseOptionalUInt(value);
            return (number, null);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return ParsePositionText(value.GetString());
        }

        return (null, null);
    }

    private static QuickTagArtworkChange ParseArtworkChange(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Artwork payload is invalid.");
        }

        var mode = ParseArtworkMode(value);
        if (mode == "clear" || mode == "remove")
        {
            return QuickTagArtworkChange.ForClear();
        }

        if (mode != "set")
        {
            throw new InvalidOperationException("Artwork mode is not supported.");
        }

        var mimeType = ParseArtworkMimeType(value);
        var description = ParseArtworkDescription(value);
        var imageType = ParseArtworkType(value);
        var bytes = ParseArtworkBytes(value);
        return QuickTagArtworkChange.ForSet(bytes, mimeType, description, imageType);
    }

    private static string ParseArtworkMode(JsonElement value)
    {
        if (value.TryGetProperty("mode", out var modeElement) && modeElement.ValueKind == JsonValueKind.String)
        {
            return (modeElement.GetString() ?? "set").Trim().ToLowerInvariant();
        }

        return "set";
    }

    private static string ParseArtworkMimeType(JsonElement value)
    {
        if (value.TryGetProperty("mimeType", out var mimeElement) && mimeElement.ValueKind == JsonValueKind.String)
        {
            var mimeType = mimeElement.GetString();
            return string.IsNullOrWhiteSpace(mimeType) ? "image/jpeg" : mimeType.Trim();
        }

        return "image/jpeg";
    }

    private static string ParseArtworkDescription(JsonElement value)
    {
        if (value.TryGetProperty("description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String)
        {
            return descriptionElement.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static PictureType ParseArtworkType(JsonElement value)
    {
        if (!value.TryGetProperty("imageType", out var imageTypeElement))
        {
            return PictureType.FrontCover;
        }

        var parsedImageType = ParseOptionalUInt(imageTypeElement);
        return parsedImageType.HasValue && parsedImageType.Value <= 20u
            ? (PictureType)parsedImageType.Value
            : PictureType.FrontCover;
    }

    private static byte[] ParseArtworkBytes(JsonElement value)
    {
        var rawBase64 = ResolveArtworkBase64(value);
        if (string.IsNullOrWhiteSpace(rawBase64))
        {
            throw new InvalidOperationException("Artwork image data is missing.");
        }

        try
        {
            var bytes = Convert.FromBase64String(rawBase64.Trim());
            if (bytes.Length == 0)
            {
                throw new InvalidOperationException("Artwork image data is empty.");
            }

            return bytes;
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Artwork image data is invalid.");
        }
    }

    private static string? ResolveArtworkBase64(JsonElement value)
    {
        if (value.TryGetProperty("dataBase64", out var base64Element) && base64Element.ValueKind == JsonValueKind.String)
        {
            return base64Element.GetString();
        }

        if (value.TryGetProperty("data", out var legacyDataElement) && legacyDataElement.ValueKind == JsonValueKind.String)
        {
            return legacyDataElement.GetString();
        }

        return null;
    }

    private static int? ParseNullableInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var raw = value.Trim();
        if (int.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        if (raw.Length >= 4 &&
            int.TryParse(raw[..4], out var yearPrefix) &&
            yearPrefix > 0)
        {
            return yearPrefix;
        }

        return null;
    }

    private static string? FirstTagValue(Dictionary<string, List<string>> tags, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!tags.TryGetValue(key, out var values))
            {
                continue;
            }

            var first = values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first;
            }
        }

        return null;
    }

    private static List<string> ReadTagValues(Dictionary<string, List<string>> tags, params string[] keys)
    {
        var values = new List<string>();
        foreach (var key in keys)
        {
            if (!tags.TryGetValue(key, out var entries))
            {
                continue;
            }

            values.AddRange(entries.Where(entry => !string.IsNullOrWhiteSpace(entry)));
        }

        return values;
    }

    private static (uint? Number, uint? Total) ReadPositionValues(uint number, uint total, string? fallback)
    {
        if (number > 0 || total > 0)
        {
            return (number > 0 ? number : null, total > 0 ? total : null);
        }

        return ParsePositionText(fallback);
    }

    private static string FormatMp4Position(uint number, uint total)
    {
        if (number == 0)
        {
            return string.Empty;
        }

        return total > 0 ? $"{number}/{total}" : number.ToString();
    }

    private static (uint? Number, uint? Total) ParsePositionText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var text = value.Trim();
        if (text.Contains('/', StringComparison.Ordinal))
        {
            var split = text.Split('/', 2, StringSplitOptions.TrimEntries);
            var number = split.Length > 0 && uint.TryParse(split[0], out var parsedNumber) && parsedNumber > 0
                ? parsedNumber
                : (uint?)null;
            var total = split.Length > 1 && uint.TryParse(split[1], out var parsedTotal) && parsedTotal > 0
                ? parsedTotal
                : (uint?)null;
            return (number, total);
        }

        return uint.TryParse(text, out var plain) && plain > 0
            ? (plain, null)
            : (null, null);
    }

    private static int ParseRating(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var direct))
        {
            return Math.Clamp(direct, 0, 5);
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
        {
            return Math.Clamp(parsed, 0, 5);
        }

        return 0;
    }

    private static int ReadRating(TagLib.File file, string extension, Dictionary<string, List<string>> tags)
    {
        var normalizedExtension = extension.ToLowerInvariant();
        if (Id3FamilyExtensions.Contains(normalizedExtension))
        {
            return ReadId3Rating(file);
        }

        if (VorbisFamilyExtensions.Contains(normalizedExtension))
        {
            return ReadScaledRatingFromTag(tags, RatingTag);
        }

        if (Mp4FamilyExtensions.Contains(normalizedExtension))
        {
            return ReadScaledRatingFromTag(tags, "iTunes:RATING");
        }

        return 0;
    }

    private static int ReadId3Rating(TagLib.File file)
    {
        var id3 = (TagLib.Id3v2.Tag?)file.GetTag(TagTypes.Id3v2, false);
        if (id3 == null)
        {
            return 0;
        }

        var popm = TagLib.Id3v2.PopularimeterFrame.Get(id3, "Windows Media Player 9 Series", false)
                   ?? TagLib.Id3v2.PopularimeterFrame.Get(id3, "", false);
        if (popm == null)
        {
            return 0;
        }

        var normalized = (int)Math.Round(popm.Rating / 51d, MidpointRounding.AwayFromZero);
        return Math.Clamp(normalized, 0, 5);
    }

    private static int ReadScaledRatingFromTag(Dictionary<string, List<string>> tags, string tagName)
    {
        if (!tags.TryGetValue(tagName, out var values) || !int.TryParse(values.FirstOrDefault(), out var raw))
        {
            return 0;
        }

        if (raw <= 5)
        {
            return Math.Clamp(raw, 0, 5);
        }

        return Math.Clamp((int)Math.Round(raw / 20d, MidpointRounding.AwayFromZero), 0, 5);
    }

    private static void WriteRating(TagLib.File file, string extension, int rating)
    {
        rating = Math.Clamp(rating, 0, 5);

        switch (extension.ToLowerInvariant())
        {
            case ".mp3":
            case ".wav":
            case ".aif":
            case AiffExtension:
                {
                    var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
                    var popm = TagLib.Id3v2.PopularimeterFrame.Get(id3, "Windows Media Player 9 Series", true);
                    popm.Rating = (byte)(rating * 51);
                    break;
                }

            case FlacExtension:
            case ".ogg":
            case OpusExtension:
                {
                    var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
                    vorbis.SetField(RatingTag, (rating * 20).ToString());
                    break;
                }

            case ".m4a":
            case ".mp4":
            case ".m4b":
                {
                    var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
                    TrySetAppleDashBox(apple, RatingTag, new[] { (rating * 20).ToString() });
                    break;
                }
        }
    }

    private static void SetRawTag(
        TagLib.File file,
        string extension,
        string rawName,
        List<string> values,
        RawTagWriteOptions rawTagWriteOptions)
    {
        rawName = rawName.Trim();

        var isComment = IsCommentTag(rawName);
        var isLyrics = IsLyricsTag(rawName);
        if (IsGenreTag(rawName))
        {
            values = FilterBlockedGenres(
                values,
                rawTagWriteOptions.GenreAliasMap,
                rawTagWriteOptions.SplitCompositeGenres);
        }

        if (isComment)
        {
            var joined = JoinValues(values, ResolveSeparatorForExtension(extension, rawTagWriteOptions.Separators));
            file.Tag.Comment = joined;
            if (Id3FamilyExtensions.Contains(extension))
            {
                var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
                // Ensure COMM is explicit so quicktag-style note/custom values round-trip reliably.
                SetId3Comment(id3, joined, rawTagWriteOptions.Id3CommLang);
            }

            return;
        }

        if (isLyrics)
        {
            var joined = JoinValues(values, ResolveSeparatorForExtension(extension, rawTagWriteOptions.Separators));
            file.Tag.Lyrics = joined;
            return;
        }

        if (Id3FamilyExtensions.Contains(extension))
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
            SetId3Raw(id3, rawName, values, rawTagWriteOptions.Separators.Id3);
            return;
        }

        if (VorbisFamilyExtensions.Contains(extension))
        {
            var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
            SetVorbisRaw(vorbis, rawName, values, rawTagWriteOptions.Separators.Vorbis);
            return;
        }

        if (Mp4FamilyExtensions.Contains(extension))
        {
            var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
            var normalized = Mp4RawTagNameNormalizer.Normalize(rawName);
            TrySetAppleDashBox(apple, normalized, ApplySeparator(values, rawTagWriteOptions.Separators.Mp4));
        }
    }

    private static string ResolveIsrcTagName(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".mp3" or ".wav" or ".aif" or AiffExtension => "TSRC",
            FlacExtension or ".ogg" or OpusExtension => "ISRC",
            ".m4a" or ".mp4" or ".m4b" => ITunesIsrcTag,
            _ => "TSRC"
        };
    }

    private static bool IsCommentTag(string rawName)
    {
        var normalized = rawName.Trim();
        return normalized.Equals("COMM", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(CommentTag, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("©cmt", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("iTunes:COMMENT", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("com.apple.iTunes:COMMENT", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLyricsTag(string rawName)
    {
        var normalized = rawName.Trim();
        return normalized.Equals("USLT", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(LyricsTag, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("©lyr", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("iTunes:LYRICS", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("com.apple.iTunes:LYRICS", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenreTag(string rawName)
    {
        var normalized = rawName.Trim();
        var mp4Normalized = Mp4RawTagNameNormalizer.Normalize(normalized);
        return normalized.Equals("TCON", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(GenreTag, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("©gen", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("----:com.apple.iTunes:GENRE", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("iTunes:GENRE", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("com.apple.iTunes:GENRE", StringComparison.OrdinalIgnoreCase)
            || mp4Normalized.Equals(GenreTag, StringComparison.OrdinalIgnoreCase)
            || mp4Normalized.Equals("©gen", StringComparison.OrdinalIgnoreCase);
    }

    private (IReadOnlyDictionary<string, string> AliasMap, bool SplitCompositeGenres) ResolveGenreNormalization()
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            return settings.NormalizeGenreTags
                ? (GenreTagAliasNormalizer.BuildAliasMap(settings.GenreTagAliasRules), true)
                : (new Dictionary<string, string>(StringComparer.Ordinal), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load genre alias rules for QuickTag save; continuing without alias normalization.");
            return (new Dictionary<string, string>(StringComparer.Ordinal), false);
        }
    }

    private static List<string> FilterBlockedGenres(
        IEnumerable<string> values,
        IReadOnlyDictionary<string, string> genreAliasMap,
        bool splitCompositeGenres)
    {
        return GenreTagAliasNormalizer.NormalizeAndExpandValues(values, genreAliasMap, splitCompositeGenres)
            .Where(v => !BlockedGenres.Contains(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveSeparatorForExtension(string extension, QuickTagSeparators separators)
    {
        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(OpusExtension, StringComparison.OrdinalIgnoreCase))
        {
            return separators.Vorbis ?? string.Empty;
        }

        if (extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".m4b", StringComparison.OrdinalIgnoreCase))
        {
            return separators.Mp4 ?? ", ";
        }

        return separators.Id3 ?? ", ";
    }

    private static string JoinValues(List<string> values, string separator)
    {
        var cleaned = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToList();
        if (cleaned.Count == 0)
        {
            return string.Empty;
        }

        if (string.IsNullOrEmpty(separator))
        {
            return string.Join(string.Empty, cleaned);
        }

        return string.Join(separator, cleaned);
    }

    private static string[] ApplySeparator(List<string> values, string? separator)
    {
        var cleaned = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToList();

        if (cleaned.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (string.IsNullOrEmpty(separator))
        {
            return cleaned.ToArray();
        }

        return new[] { string.Join(separator, cleaned) };
    }

    private static void SetId3Raw(TagLib.Id3v2.Tag tag, string name, List<string> values, string? separator)
    {
        var output = ApplySeparator(values, separator);
        if (name.Equals("TXXX", StringComparison.OrdinalIgnoreCase))
        {
            var userDefault = TagLib.Id3v2.UserTextInformationFrame.Get(tag, string.Empty, true);
            userDefault.Text = output;
            return;
        }

        if (name.Length == 4)
        {
            var frame = TagLib.Id3v2.TextInformationFrame.Get(tag, name, true);
            frame.Text = output;
            return;
        }

        var user = TagLib.Id3v2.UserTextInformationFrame.Get(tag, name, true);
        user.Text = output;
    }

    private static void SetId3Comment(TagLib.Id3v2.Tag tag, string comment, string? id3CommLang)
    {
        var language = string.IsNullOrWhiteSpace(id3CommLang) ? "eng" : id3CommLang.Trim().ToLowerInvariant();
        var frames = tag.GetFrames<TagLib.Id3v2.CommentsFrame>("COMM").ToList();
        foreach (var frame in frames)
        {
            tag.RemoveFrame(frame);
        }

        var commentsFrame = new TagLib.Id3v2.CommentsFrame(language, string.Empty)
        {
            Text = comment
        };
        tag.AddFrame(commentsFrame);
    }

    private static void SetVorbisRaw(TagLib.Ogg.XiphComment tag, string name, List<string> values, string? separator)
    {
        var output = ApplySeparator(values, separator);
        tag.SetField(name, output);
    }

    private static List<string> ReadRawTagValues(TagLib.File file, string extension, string name)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".aif", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(AiffExtension, StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag?)file.GetTag(TagTypes.Id3v2, false);
            if (id3 == null)
            {
                return new List<string>();
            }

            if (name.Length == 4)
            {
                var frame = TagLib.Id3v2.TextInformationFrame.Get(id3, name, false);
                return frame?.Text?.ToList() ?? new List<string>();
            }

            var user = TagLib.Id3v2.UserTextInformationFrame.Get(id3, name, false);
            return user?.Text?.ToList() ?? new List<string>();
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(OpusExtension, StringComparison.OrdinalIgnoreCase))
        {
            var vorbis = (TagLib.Ogg.XiphComment?)file.GetTag(TagTypes.Xiph, false);
            return vorbis?.GetField(name).ToList() ?? new List<string>();
        }

        if (extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".m4b", StringComparison.OrdinalIgnoreCase))
        {
            var apple = (TagLib.Mpeg4.AppleTag?)file.GetTag(TagTypes.Apple, false);
            if (apple == null)
            {
                return new List<string>();
            }

            return ReadAppleDashBox(apple, Mp4RawTagNameNormalizer.Normalize(name));
        }

        return new List<string>();
    }

    private static List<KeyValuePair<string, string[]>> EnumerateVorbisFields(TagLib.Ogg.XiphComment vorbis)
    {
        var output = new List<KeyValuePair<string, string[]>>();
        foreach (var key in vorbis)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var values = vorbis.GetField(key)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            output.Add(new KeyValuePair<string, string[]>(key, values));
        }

        return output;
    }

    private static List<string> ReadAppleDashBox(TagLib.Mpeg4.AppleTag tag, string name)
    {
        try
        {
            foreach (var method in EnumerateAppleDashReadMethods(tag.GetType()))
            {
                var values = ConvertAppleDashReadResult(method.Invoke(tag, new object[] { AppleDashMean, name }));
                if (values.Count > 0)
                {
                    return values;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new List<string>();
        }

        return new List<string>();
    }

    private static IEnumerable<MethodInfo> EnumerateAppleDashReadMethods(Type tagType)
    {
        return tagType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(IsAppleDashReadMethod);
    }

    private static bool IsAppleDashReadMethod(MethodInfo method)
    {
        if (!string.Equals(method.Name, "GetDashBox", StringComparison.Ordinal) &&
            !string.Equals(method.Name, "GetDashBoxes", StringComparison.Ordinal))
        {
            return false;
        }

        var parameters = method.GetParameters();
        return parameters.Length == 2 &&
               parameters[0].ParameterType == typeof(string) &&
               parameters[1].ParameterType == typeof(string);
    }

    private static List<string> ConvertAppleDashReadResult(object? result)
    {
        if (result is string str)
        {
            return string.IsNullOrWhiteSpace(str)
                ? new List<string>()
                : new List<string> { str };
        }

        if (result is string[] arr)
        {
            return arr.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        }

        if (result is not IEnumerable enumerable)
        {
            return new List<string>();
        }

        var values = new List<string>();
        foreach (var item in enumerable)
        {
            var text = item?.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                values.Add(text);
            }
        }

        return values;
    }

    private static void TrySetAppleDashBox(TagLib.Mpeg4.AppleTag tag, string name, string[] values)
    {
        try
        {
            var tagType = tag.GetType();
            var methods = tagType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
            foreach (var method in methods)
            {
                if (!string.Equals(method.Name, "SetDashBox", StringComparison.Ordinal))
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != 3)
                {
                    continue;
                }

                var oneValue = values.Length == 1 ? values[0] : string.Join(", ", values);

                if (parameters[0].ParameterType == typeof(string) &&
                    parameters[1].ParameterType == typeof(string) &&
                    parameters[2].ParameterType == typeof(string))
                {
                    method.Invoke(tag, new object[] { AppleDashMean, name, oneValue });
                    return;
                }

                if (parameters[0].ParameterType == typeof(string) &&
                    parameters[1].ParameterType == typeof(string) &&
                    parameters[2].ParameterType == typeof(string[]))
                {
                    method.Invoke(tag, new object[] { AppleDashMean, name, values });
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            // Ignore: quick tag save should continue even when a specific atom cannot be set.
        }
    }
}

public sealed class QuickTagArtworkChange
{
    private QuickTagArtworkChange(bool clear, byte[] data, string mimeType, string description, PictureType imageType)
    {
        Clear = clear;
        Data = data;
        MimeType = mimeType;
        Description = description;
        ImageType = imageType;
    }

    public bool Clear { get; }
    public byte[] Data { get; }
    public string MimeType { get; }
    public string Description { get; }
    public PictureType ImageType { get; }

    public static QuickTagArtworkChange ForClear()
    {
        return new QuickTagArtworkChange(true, Array.Empty<byte>(), "image/jpeg", string.Empty, PictureType.FrontCover);
    }

    public static QuickTagArtworkChange ForSet(byte[] data, string mimeType, string description, PictureType imageType)
    {
        return new QuickTagArtworkChange(false, data, mimeType, description, imageType);
    }
}

public sealed class QuickTagAudioFile
{
    public required string Path { get; init; }
    public required string ContentType { get; init; }
    public required Stream Stream { get; init; }
}

public sealed class QuickTagCloneResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public int RawTagKeysCloned { get; init; }

    public static QuickTagCloneResult Ok(int rawTagKeysCloned)
        => new() { Success = true, RawTagKeysCloned = rawTagKeysCloned };

    public static QuickTagCloneResult Fail(string error)
        => new() { Success = false, Error = error ?? "Tag clone failed." };
}

public sealed class QuickTagFolderResult
{
    public string Path { get; init; } = string.Empty;
    public List<QuickTagFolderEntry> Files { get; init; } = new();
}

public sealed class QuickTagFolderEntry
{
    public string Path { get; init; } = string.Empty;
    public string Filename { get; init; } = string.Empty;
    public bool Dir { get; init; }
    public bool Playlist { get; init; }
}

public sealed class QuickTagLoadRequest
{
    public string? Path { get; set; }
    public bool? Recursive { get; set; }
    public QuickTagSeparators? Separators { get; set; }
}

public sealed class QuickTagSaveRequest
{
    public string Path { get; set; } = string.Empty;
    public string? Format { get; set; }
    public bool? Id3v24 { get; set; }
    public string? Id3CommLang { get; set; }
    public QuickTagSeparators? Separators { get; set; }
    public List<QuickTagChangeRequest> Changes { get; set; } = new();
}

public sealed class QuickTagChangeRequest
{
    public string Type { get; set; } = string.Empty;
    public string? Tag { get; set; }
    public JsonElement Value { get; set; }
}

public sealed class QuickTagLoadResult
{
    public List<QuickTagFileDto> Files { get; init; } = new();
    public List<QuickTagFailedDto> Failed { get; init; } = new();
}

public sealed class QuickTagFailedDto
{
    public string Path { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
}

public abstract class QuickTagSharedMeta
{
    public string Title { get; init; } = string.Empty;
    public List<string> Artists { get; init; } = new();
    public string? Album { get; init; }
    public List<string> AlbumArtists { get; init; } = new();
    public List<string> Composers { get; init; } = new();
    public uint? TrackNumber { get; init; }
    public uint? TrackTotal { get; init; }
    public uint? DiscNumber { get; init; }
    public uint? DiscTotal { get; init; }
    public List<string> Genres { get; init; } = new();
    public int? Bpm { get; init; }
    public int Rating { get; init; }
    public int? Year { get; init; }
    public string? Key { get; init; }
    public string? Isrc { get; init; }
    public bool HasArtwork { get; init; }
    public string ArtworkDescription { get; init; } = string.Empty;
    public int ArtworkType { get; init; } = 3;
}

public sealed class QuickTagFileDto : QuickTagSharedMeta
{
    public string Path { get; init; } = string.Empty;
    public string Format { get; init; } = "mp3";
    public Dictionary<string, List<string>> Tags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Publisher { get; init; }
    public string? Copyright { get; init; }
    public string? Language { get; init; }
    public string? Lyricist { get; init; }
    public string? Conductor { get; init; }
    public string FileName { get; init; } = string.Empty;
    public int BitrateKbps { get; init; }
    public long DurationMs { get; init; }
    public long FileSizeBytes { get; init; }
    public long ArtworkVersion { get; init; }
}

public sealed class QuickTagDumpResult
{
    public string Path { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public QuickTagDumpAudioInfo Audio { get; init; } = new();
    public QuickTagDumpMeta Meta { get; init; } = new();
    public Dictionary<string, List<string>> Tags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<QuickTagDumpPictureInfo> Pictures { get; init; } = new();
}

public sealed class QuickTagDumpAudioInfo
{
    public long DurationMs { get; init; }
    public int BitrateKbps { get; init; }
    public int SampleRate { get; init; }
    public int BitsPerSample { get; init; }
    public int Channels { get; init; }
    public string ChannelLayout { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string MimeType { get; init; } = string.Empty;
}

public sealed class QuickTagDumpMeta : QuickTagSharedMeta
{
}

public sealed class QuickTagDumpPictureInfo
{
    public string MimeType { get; init; } = string.Empty;
    public int Type { get; init; }
    public string Description { get; init; } = string.Empty;
    public int Size { get; init; }
    public string? Data { get; init; }
}

public sealed class QuickTagSeparators
{
    public static readonly QuickTagSeparators Default = new();

    public string Id3 { get; set; } = ", ";
    public string? Vorbis { get; set; }
    public string Mp4 { get; set; } = ", ";
}
