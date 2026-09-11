using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Security;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Identity;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using TagLib;
using IOFile = System.IO.File;
using DownloadLyricsService = DeezSpoTag.Services.Download.Utils.LyricsService;
using LyricsProviderRegistry = DeezSpoTag.Services.Download.Utils.LyricsProviderRegistry;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed partial class LocalAutoTagRunner
{

    /// <summary>
    /// Harvests files that appeared after the run started. Files whose artist sorts
    /// after the runner's current position join the run (appended before the deferred
    /// wave); files at or before the current position are deferred to the very end so
    /// a new file never jumps ahead of the alphabetical flow.
    /// </summary>
    private sealed class EnhancementPickupScheduler
    {
        private const int PickupScanIntervalSeconds = 30;

        private readonly AutoTagRunPlan _plan;
        private readonly Action<string> _log;
        private readonly bool _enabled;
        private readonly HashSet<string> _knownFiles;
        private readonly List<string> _included = new();
        private readonly List<string> _deferred = new();
        private DateTimeOffset _lastScanUtc = DateTimeOffset.MinValue;

        public EnhancementPickupScheduler(AutoTagRunPlan plan, Action<string> log)
        {
            _plan = plan;
            _log = log;
            // Pickups apply to library-wide runs only; scoped target-file runs keep
            // their explicit scope.
            _enabled = plan.Config.TargetFiles is null or { Count: 0 };
            _knownFiles = new HashSet<string>(plan.Files, StringComparer.OrdinalIgnoreCase);
        }

        public void ScanIfDue(int currentFileIndex, CancellationToken token)
        {
            if (!_enabled || currentFileIndex < 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - _lastScanUtc < TimeSpan.FromSeconds(PickupScanIntervalSeconds))
            {
                return;
            }

            _lastScanUtc = now;
            var currentKey = ArtistKeyAt(currentFileIndex);
            var discovered = 0;
            foreach (var file in EnumerateAudioFiles(_plan.TargetPath, _plan.Config.IncludeSubfolders))
            {
                token.ThrowIfCancellationRequested();
                if (!_knownFiles.Add(file))
                {
                    continue;
                }

                discovered++;
                if (_plan.Config.SkipTagged && HasExistingTags(file))
                {
                    continue;
                }

                var meta = ReadArtistSortMeta(file);
                _plan.ArtistSortMeta[file] = meta;
                if (string.Compare(meta.ArtistKey, currentKey, StringComparison.Ordinal) <= 0)
                {
                    _deferred.Add(file);
                }
                else
                {
                    _included.Add(file);
                }
            }

            if (discovered > 0)
            {
                _log($"onetagger_autotag: {discovered} new file(s) detected mid-run "
                     + $"({_included.Count} join the run, {_deferred.Count} deferred to the end wave).");
            }
        }

        public bool BeginNextPass(AutoTagRunPlan plan, Action<string> log)
        {
            var source = _included.Count > 0 ? _included : _deferred;
            if (source.Count == 0)
            {
                return false;
            }

            var ordered = source
                .Select(file => (File: file, Meta: PlanMeta(plan, file)))
                .OrderBy(item => item.Meta.ArtistKey, StringComparer.Ordinal)
                .ThenBy(item => item.Meta.AlbumKey, StringComparer.Ordinal)
                .ThenBy(item => item.Meta.TrackNumber ?? int.MaxValue)
                .ThenBy(item => item.File, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.File)
                .ToList();
            var kind = ReferenceEquals(source, _included) ? "current-run" : "deferred";
            log($"onetagger_autotag: running {ordered.Count} mid-run pickup file(s) ({kind} wave).");
            source.Clear();
            plan.Files.AddRange(ordered);
            return true;
        }

        private string ArtistKeyAt(int fileIndex)
        {
            var index = Math.Clamp(fileIndex, 0, _plan.FileCount - 1);
            var file = _plan.Files[index];
            return _plan.ArtistSortMeta.TryGetValue(file, out var meta)
                ? meta.ArtistKey
                : string.Empty;
        }

        private static ArtistSortMeta PlanMeta(AutoTagRunPlan plan, string file) =>
            plan.ArtistSortMeta.TryGetValue(file, out var meta)
                ? meta
                : new ArtistSortMeta(string.Empty, string.Empty, null, true);
    }

    private sealed record ArtistSortMeta(string ArtistKey, string AlbumKey, int? TrackNumber, bool WeakIdentity);

    private sealed class PlatformMatchContext
    {
        public required string FilePath { get; init; }
        public required AutoTagRunnerConfig Config { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required AutoTagMatchingConfig MatchingConfig { get; init; }
        public required IDictionary<string, ShazamRecognitionInfo?> ShazamCache { get; init; }
        public required bool IsManualEnrichment { get; init; }
    }

    private sealed class AudioInfoDraft
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public List<string> Artists { get; set; } = new();
        public string? Album { get; set; }
        public string? Isrc { get; set; }
        public int? TrackNumber { get; set; }
        public bool HasEmbeddedTitle { get; set; }
        public bool HasEmbeddedArtist { get; set; }
        public Dictionary<string, List<string>> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record FolderAlbumIdentity(
        string AlbumTitle,
        string? AlbumArtist,
        AlbumIdentity Identity);

    private sealed class TagWriteRequest
    {
        public required string FilePath { get; init; }
        public required AutoTagTrack SourceTrack { get; init; }
        public required Track CoreTrack { get; init; }
        public required TagSettings EffectiveTagSettings { get; init; }
        public required AutoTagRunnerConfig Config { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required string PlatformId { get; init; }
        public required string Separator { get; init; }
        public string? TempCoverPath { get; init; }
    }

    private sealed class TagWriteExecutionContext
    {
        public required string FilePath { get; init; }
        public required AutoTagTrack SourceTrack { get; init; }
        public required Track CoreTrack { get; init; }
        public required TagSettings EffectiveTagSettings { get; init; }
        public required AutoTagRunnerConfig Config { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required string PlatformId { get; init; }
        public required string Separator { get; init; }
        public string? TempCoverPath { get; init; }
        public required string Extension { get; init; }
        public required HashSet<string> EnabledTags { get; init; }
        public required IReadOnlyDictionary<string, string> GenreAliasMap { get; init; }
        public required IReadOnlyList<string> GenreBlockList { get; init; }
        public required bool SplitCompositeGenres { get; init; }
        public required bool AllowsLyricsBySettings { get; init; }
        public required bool AllowsSyncedType { get; init; }
        public required bool AllowsUnsyncedType { get; init; }
        public required bool AllowsLrcByFormat { get; init; }
        public required bool AllowsTtmlByFormat { get; init; }
        public required (bool HasAny, bool HasLrc, bool HasTtml, bool HasTxt, string TxtPath) SidecarState { get; init; }
        public required bool ShouldSkipEmbeddedLyrics { get; init; }
        public HashSet<SupportedTag> AttemptedTags { get; } = new();
    }

    private sealed record TagFileWriteResult(HashSet<SupportedTag> AttemptedTags);

    public sealed class LocalAutoTagRunnerCollaborators
    {
        public required ILogger<LocalAutoTagRunner> Logger { get; init; }
        public required IHttpClientFactory HttpClientFactory { get; init; }
        public required MusicBrainzMatcher MusicBrainzMatcher { get; init; }
        public required BeatportMatcher BeatportMatcher { get; init; }
        public required DiscogsMatcher DiscogsMatcher { get; init; }
        public required TraxsourceMatcher TraxsourceMatcher { get; init; }
        public required BandcampMatcher BandcampMatcher { get; init; }
        public required BpmSupremeMatcher BpmSupremeMatcher { get; init; }
        public required ItunesMatcher ItunesMatcher { get; init; }
        public required SpotifyMatcher SpotifyMatcher { get; init; }
        public required DeezerMatcher DeezerMatcher { get; init; }
        public required LastFmMatcher LastFmMatcher { get; init; }
        public required BoomplayMatcher BoomplayMatcher { get; init; }
        public required AudiomackMatcher AudiomackMatcher { get; init; }
        public required ShazamMatcher ShazamMatcher { get; init; }
        public required ShazamRecognitionService ShazamRecognitionService { get; init; }
        public required AppleLyricsService AppleLyricsService { get; init; }
        public required AppleMusicCatalogService AppleMusicCatalogService { get; init; }
        public required DownloadLyricsService DownloadLyricsService { get; init; }
        public required DeezSpoTagSettingsService SettingsService { get; init; }
        public required IServiceScopeFactory ServiceScopeFactory { get; init; }
        public required ITrackIdentityResolver TrackIdentityResolver { get; init; }
        public PortedPlatformRegistry? PlatformRegistry { get; init; }

        /// <summary>Optional path of the cross-run album identity store; persistence is disabled when null.</summary>
        public string? AlbumIdentityStorePath { get; init; }
    }

    private readonly record struct TagWriteContext(
        TagLib.File File,
        string Extension,
        AutoTagRunnerConfig Config,
        string Separator,
        string PlatformId,
        bool UseNullSeparator,
        IReadOnlyDictionary<string, string> GenreAliasMap,
        IReadOnlyList<string> GenreBlockList,
        bool SplitCompositeGenres,
        HashSet<SupportedTag> AttemptedTags);

    private readonly record struct TagFieldBinding(
        string Id3Frame,
        string VorbisField,
        string Mp4Field,
        SupportedTag Tag);

    private readonly record struct DateWritePayload(
        DateTime Date,
        bool UseYearOnly,
        string Year,
        string DateString);

    private readonly record struct LyricsSidecarWriteResult(
        bool WroteLrcSidecar,
        bool WroteTtmlSidecar);

    private readonly record struct OverwriteRuleContext(
        HashSet<string> EnabledTags,
        AutoTagRunnerConfig Config,
        TagLib.File File,
        string Extension,
        string PlatformId);

    private sealed record CustomTagWrite(string TagKey, SupportedTag SupportedTag, string RawTagName, List<string> Values);

    private static class Mp4TagHelper
    {
        public static bool HasField(TagLib.File file, SupportedTag tag)
        {
            return tag switch
            {
                SupportedTag.Title => !string.IsNullOrWhiteSpace(file.Tag.Title),
                SupportedTag.Artist => file.Tag.Performers?.Length > 0,
                SupportedTag.AlbumArtist => file.Tag.AlbumArtists?.Length > 0,
                SupportedTag.Album => !string.IsNullOrWhiteSpace(file.Tag.Album),
                SupportedTag.Genre => file.Tag.Genres?.Length > 0,
                SupportedTag.BPM => file.Tag.BeatsPerMinute > 0,
                SupportedTag.TrackNumber => file.Tag.Track > 0,
                SupportedTag.TrackTotal => file.Tag.TrackCount > 0,
                SupportedTag.DiscNumber => file.Tag.Disc > 0,
                SupportedTag.DiscTotal => file.Tag.DiscCount > 0,
                SupportedTag.UnsyncedLyrics => !string.IsNullOrWhiteSpace(file.Tag.Lyrics),
                SupportedTag.AlbumArt => file.Tag.Pictures?.Length > 0,
                _ => false
            };
        }

        public static bool TrySetMp4Field(
            TagWriteContext context,
            SupportedTag tag,
            List<string> values)
        {
            if (!ShouldOverwriteTag(context.Config, tag) && HasTag(context.File, ".mp4", tag, context.Config, context.PlatformId))
            {
                return true;
            }

            switch (tag)
            {
                case SupportedTag.Title:
                    context.File.Tag.Title = values.FirstOrDefault() ?? "";
                    return true;
                case SupportedTag.Artist:
                    context.File.Tag.Performers = values.ToArray();
                    return true;
                case SupportedTag.AlbumArtist:
                    context.File.Tag.AlbumArtists = values.ToArray();
                    return true;
                case SupportedTag.Album:
                    context.File.Tag.Album = values.FirstOrDefault() ?? "";
                    return true;
                case SupportedTag.Genre:
                    context.File.Tag.Genres = SanitizeGenres(
                        values,
                        context.GenreAliasMap,
                        context.GenreBlockList,
                        context.SplitCompositeGenres).ToArray();
                    return true;
                case SupportedTag.BPM:
                    if (int.TryParse(values.FirstOrDefault(), out var bpm))
                    {
                        context.File.Tag.BeatsPerMinute = (uint)bpm;
                    }
                    return true;
                case SupportedTag.TrackNumber:
                    if (int.TryParse(values.FirstOrDefault(), out var track))
                    {
                        context.File.Tag.Track = (uint)track;
                    }
                    return true;
                case SupportedTag.TrackTotal:
                    if (int.TryParse(values.FirstOrDefault(), out var total))
                    {
                        context.File.Tag.TrackCount = (uint)total;
                    }
                    return true;
                case SupportedTag.DiscNumber:
                    if (int.TryParse(values.FirstOrDefault(), out var disc))
                    {
                        context.File.Tag.Disc = (uint)disc;
                    }
                    return true;
                case SupportedTag.DiscTotal:
                    if (int.TryParse(values.FirstOrDefault(), out var discTotal))
                    {
                        context.File.Tag.DiscCount = (uint)discTotal;
                    }
                    return true;
                default:
                    return false;
            }
        }

        public static void SetMp4Raw(
            TagLib.File file,
            string rawName,
            string[] values,
            IReadOnlyDictionary<string, string> genreAliasMap,
            IReadOnlyList<string> genreBlockList,
            bool splitCompositeGenres)
        {
            var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
            var normalized = Mp4RawTagNameNormalizer.Normalize(rawName);
            var output = IsGenreRawTag(normalized) || IsGenreRawTag(rawName)
                ? SanitizeGenres(values, genreAliasMap, genreBlockList, splitCompositeGenres).ToArray()
                : values;
            TrySetAppleDashBox(apple, normalized, output);
        }

        public static bool HasRaw(TagLib.File file, string rawName)
        {
            return HasMp4RawValue(file, rawName);
        }

        private static bool HasMp4RawValue(TagLib.File file, string rawName)
        {
            var normalized = Mp4RawTagNameNormalizer.Normalize(rawName);
            var apple = (TagLib.Mpeg4.AppleTag?)file.GetTag(TagTypes.Apple, false);
            if (apple != null && TagRawProbe.HasAppleDashBox(apple, normalized))
            {
                return true;
            }

            if (ReadMp4AtlRawValues(file.Name, normalized).Count > 0)
            {
                return true;
            }

            return normalized.ToUpperInvariant() switch
            {
                "©NAM" or TitleUpperTag => !string.IsNullOrWhiteSpace(file.Tag.Title),
                "©ART" or ArtistUpperTag or "ARTISTS" => file.Tag.Performers?.Any(value => !string.IsNullOrWhiteSpace(value)) == true,
                "AART" or AlbumArtistUpperTag => file.Tag.AlbumArtists?.Any(value => !string.IsNullOrWhiteSpace(value)) == true,
                "©ALB" or AlbumUpperTag => !string.IsNullOrWhiteSpace(file.Tag.Album),
                "ISRC" => !string.IsNullOrWhiteSpace(file.Tag.ISRC),
                "©GEN" or Mp4GenreTag => file.Tag.Genres?.Any(value => !string.IsNullOrWhiteSpace(value)) == true,
                "TRACK" or "TRKN" => file.Tag.Track > 0 || file.Tag.TrackCount > 0,
                "DISC" or "DISK" => file.Tag.Disc > 0 || file.Tag.DiscCount > 0,
                "LYRICS" or "©LYR" => !string.IsNullOrWhiteSpace(file.Tag.Lyrics),
                _ => false
            };
        }

        public static void SetDate(TagLib.File file, string dateString)
        {
            var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
            TrySetAppleDashBox(apple, "©day", new[] { dateString });
        }
    }

    private readonly record struct ArtistAliasOverwriteDecision(
        bool ForcedArtist,
        bool ForcedAlbumArtist,
        bool ForcedTitle,
        string? RewrittenTitle)
    {
        public static ArtistAliasOverwriteDecision None { get; } = new(false, false, false, null);
    }

    private sealed class AutoTagRunPlan
    {
        public required string JobId { get; init; }
        public required string ConfigPath { get; init; }
        public required AutoTagRunnerConfig Config { get; init; }
        public required string TargetPath { get; init; }
        public required AutoTagMatchingConfig MatchingConfig { get; init; }
        public required List<string> EffectivePlatforms { get; init; }
        public required Dictionary<string, HashSet<SupportedTag>> PlatformSupportedTags { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required TagSettings TagSettings { get; init; }
        public required List<string> Files { get; init; }
        public required Dictionary<string, ShazamRecognitionInfo?> ShazamCache { get; init; }
        public required bool EnableShazamFallback { get; init; }
        public required bool ForceShazamMatch { get; init; }
        public required bool ShazamConflictResolution { get; init; }
        public HashSet<string> PreSkippedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TaggedByAnyPlatform { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReviewedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> TaggedFileIndices { get; } = new();
        public HashSet<int> ShazamIdentifiedFiles { get; } = new();
        public Dictionary<int, AutoTagAudioInfo> OriginalManualInfo { get; } = new();
        public Dictionary<int, AutoTagAudioInfo> ResolvedManualInfo { get; } = new();
        public Dictionary<int, ManualReleaseIdentity> FrozenManualReleases { get; } = new();
        public AlbumIdentityRegistry AlbumIdentities { get; } = new();
        public HashSet<string> SeededAlbumIdentityKeys { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ArtistSortMeta> ArtistSortMeta { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, FolderAlbumIdentity> AlbumFolderIdentities { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, string> MaterializedManualPaths { get; } = new();
        public HashSet<string> AttemptedArtistArtworkPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> AttemptedAppleExtras { get; } = new();
        public int PlatformCount => EffectivePlatforms.Count;
        public int FileCount => Files.Count;
    }

    private sealed class ManualReleaseIdentity
    {
        public string Title { get; init; } = string.Empty;
        public List<string> Artists { get; init; } = new();
        public List<string> AlbumArtists { get; init; } = new();
        public string? Album { get; init; }
        public string? Isrc { get; init; }
        public string? ReleaseType { get; init; }
        public int? TrackTotal { get; init; }
        public string? Art { get; set; }

        public static ManualReleaseIdentity FromTrack(AutoTagTrack track)
            => new()
            {
                Title = track.Title,
                Artists = track.Artists.ToList(),
                AlbumArtists = track.AlbumArtists.ToList(),
                Album = track.Album,
                Isrc = track.Isrc,
                ReleaseType = track.ReleaseType,
                TrackTotal = track.TrackTotal,
                Art = track.Art
            };

        public AutoTagAudioInfo ToAudioInfo()
            => new()
            {
                Title = Title,
                Artist = Artists.FirstOrDefault() ?? string.Empty,
                Artists = Artists.ToList(),
                Album = Album,
                Isrc = Isrc
            };

        public void ApplyTo(AutoTagTrack track)
        {
            track.Title = Title;
            track.Artists = Artists.ToList();
            track.AlbumArtists = AlbumArtists.ToList();
            track.Album = Album;
            track.Isrc = Isrc;
            track.ReleaseType = ReleaseType;
            track.TrackTotal = TrackTotal;
            if (!string.IsNullOrWhiteSpace(Art))
            {
                track.Art = Art;
            }
        }
    }

    private sealed class AutoTagFileRunContext
    {
        public required AutoTagRunPlan Plan { get; init; }
        public required JobMatchCacheState JobMatchCache { get; init; }
        public required string Platform { get; init; }
        public required int PlatformIndex { get; init; }
        public required int FileIndex { get; init; }
        public required string File { get; set; }
        public required double Progress { get; init; }
        public required int NextPlatformIndex { get; init; }
        public required int NextFileIndex { get; init; }
        public required Action<TaggingStatusWrap> StatusCallback { get; init; }
        public required Action<string> LogCallback { get; init; }
        public required CancellationToken Token { get; init; }
        public string? MatchFailureOutcome { get; set; }
        public string? MatchFailureMessage { get; set; }

        // Actual album-boundary batch position; null when the run is not batched.
        public int? BatchNumber { get; init; }
        public int? BatchCount { get; init; }
        public int? BatchSize { get; init; }
        public int? BatchProcessed { get; init; }
    }

    private sealed class JobMatchCacheState
    {
        public object SyncRoot { get; } = new();
        public DateTimeOffset LastAccessUtc { get; set; } = DateTimeOffset.UtcNow;
        public Dictionary<string, MatchCacheEntry> Entries { get; } = new(StringComparer.Ordinal);
        public HashSet<string> UnavailablePlatforms { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed record MatchCacheEntry(AutoTagMatchResult? Match);
    private sealed record ProviderTagPlan(
        HashSet<SupportedTag> Requested,
        HashSet<SupportedTag> Eligible,
        HashSet<SupportedTag> Retained);
    private sealed record LyricsPopulationRequest(
        bool WantsSynced,
        bool WantsUnsynced,
        bool WantsTtml,
        bool HasSynced,
        bool HasUnsynced,
        bool HasTtml)
    {
        public bool ShouldFetch => WantsSynced || WantsUnsynced || WantsTtml;

        public bool HasAllRequestedLyrics()
        {
            if (WantsSynced && !HasSynced)
            {
                return false;
            }

            if (WantsUnsynced && !HasUnsynced)
            {
                return false;
            }

            return !WantsTtml || HasTtml;
        }
    }

    private readonly record struct LyricsRequestFlags(
        bool WantsSynced,
        bool WantsUnsynced,
        bool WantsTtml);

    private sealed class AutoTagRunnerConfig
    {
        public List<string> Platforms { get; set; } = new();
        public string? DownloadTagSource { get; set; }
        public string? Path { get; set; }
        public List<string>? TargetFiles { get; set; }

        /// <summary>Files the library DB flagged as missing core metadata; they run in priority wave 1.</summary>
        public List<string>? PriorityTargetFiles { get; set; }

        /// <summary>Flag a file for review instead of silently resolving an album edition conflict.</summary>
        public bool? EditionConflictReview { get; set; }
        public List<string> Tags { get; set; } = new();
        public List<string> OverwriteTags { get; set; } = new();
        public AutoTagSeparators? Separators { get; set; }
        public bool Overwrite { get; set; } = false;
        public bool MergeGenres { get; set; } = true;
        public bool Camelot { get; set; }
        public bool ShortTitle { get; set; }
        public double Strictness { get; set; } = 0.7;
        public bool MatchDuration { get; set; }
        public int MaxDurationDifference { get; set; } = 30;
        public bool MatchById { get; set; }
        public bool EnableShazam { get; set; } = true;
        public bool ForceShazam { get; set; }
        public bool EnhancementUntrustedTargets { get; set; }
        public string? ConflictResolution { get; set; }
        public bool SkipTagged { get; set; }
        public bool IncludeSubfolders { get; set; } = true;
        public bool ParseFilename { get; set; }
        public bool Id3v24 { get; set; } = true;
        public int TrackNumberLeadingZeroes { get; set; }
        public string StylesOptions { get; set; } = "default";
        public MultipleMatchesSort MultipleMatches { get; set; } = MultipleMatchesSort.Default;
        public string? TitleRegex { get; set; }
        public JsonObject? Custom { get; set; }
        public AutoTagStylesCustomTag? StylesCustomTag { get; set; }
        public string? Id3CommLang { get; set; }
        public bool CapitalizeGenres { get; set; }
        public string? TracknameTemplate { get; set; }
        public FolderStructureSettings? FolderStructure { get; set; }
        public bool? SaveArtwork { get; set; }
        public bool? DlAlbumcoverForPlaylist { get; set; }
        public bool? SaveArtworkArtist { get; set; }
        public bool? SaveAnimatedArtwork { get; set; }
        public bool? SaveSquareAnimatedArtwork { get; set; }
        public bool? SaveTallAnimatedArtwork { get; set; }
        public string? AnimatedArtworkFormats { get; set; }
        public string? CoverImageTemplate { get; set; }
        public string? AnimatedArtworkSquareFileName { get; set; }
        public string? AnimatedArtworkTallFileName { get; set; }
        public string? ArtistImageTemplate { get; set; }
        public string? LocalArtworkFormat { get; set; }
        public bool? MaterializeToTemplatePath { get; set; }
        public bool? OrganizeSidecarsIntoTemplateFolders { get; set; }
        public bool? EmbedMaxQualityCover { get; set; }
        public int? JpegImageQuality { get; set; }

        public int? AnimatedArtworkMaxSizeMb { get; set; }
        public TechnicalTagSettings? Technical { get; set; }
        public string? ProfileId { get; set; }
        public string? ProfileName { get; set; }
        public int? LibraryWideEnhancementBatchSize { get; set; }
        public string? ManualReleasePreference { get; set; }
        public long? ManualDestinationFolderId { get; set; }
    }

    private sealed record ShazamEnrichmentResult(bool UsedShazam, string? Error, bool IsFatal, ShazamFailureKind FailureKind = ShazamFailureKind.None);

    private enum ShazamFailureKind
    {
        None,
        NoMatch,
        Infrastructure
    }

    private sealed record AutoTagReviewMetadata(
        string? Reason,
        string? SourceTitle,
        string? SourceArtist,
        string? SourceIsrc,
        double? SourceDurationSeconds,
        string? CandidateTitle,
        string? CandidateArtist,
        string? CandidateIsrc,
        double? CandidateDurationSeconds)
    {
        public static AutoTagReviewMetadata FromSourceOnly(AutoTagAudioInfo source)
            => new(
                null,
                source.Title,
                source.Artist,
                source.Isrc,
                source.DurationSeconds,
                null,
                null,
                null,
                null);

        public static AutoTagReviewMetadata FromMatch(AutoTagAudioInfo source, AutoTagTrack? candidate)
            => new(
                null,
                source.Title,
                source.Artist,
                source.Isrc,
                source.DurationSeconds,
                candidate?.Title,
                candidate?.Artists.FirstOrDefault(),
                candidate?.Isrc,
                candidate?.Duration?.TotalSeconds);
    }

    private sealed class AutoTagSeparators
    {
        public string? Id3 { get; set; }
        public string? Vorbis { get; set; }
        public string? Mp4 { get; set; }
    }

    private sealed class AutoTagStylesCustomTag
    {
        public string? Id3 { get; set; }
        public string? Vorbis { get; set; }
        public string? Mp4 { get; set; }
    }
}
