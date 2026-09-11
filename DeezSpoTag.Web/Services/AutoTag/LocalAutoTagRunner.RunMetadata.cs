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

public partial class LocalAutoTagRunner
{

    private readonly record struct ArtistAliasOverwriteDecision(
        bool ForcedArtist,
        bool ForcedAlbumArtist,
        bool ForcedTitle,
        string? RewrittenTitle)
    {
        public static ArtistAliasOverwriteDecision None { get; } = new(false, false, false, null);
    }

    /// <summary>
    /// When on-disk artist / album artist / featured credits still use an alias,
    /// force those tags to the user's preferred spelling and keep write enabled.
    /// Richer-credit preservation must not keep the alias on disk: Navidrome
    /// organizes from tags, so main artist, artist, and featured credits have
    /// to use the same preferred name.
    /// </summary>
    private static ArtistAliasOverwriteDecision ApplyPreferredArtistAliasToExistingCredits(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        List<string> existingArtists,
        List<string> existingAlbumArtists,
        string? existingTitle,
        Func<string?, string>? rewriteCredit = null)
    {
        rewriteCredit ??= static value => DeezSpoTag.Services.Library.ArtistAliasGateway.ResolveCredit(value);
        var originalArtists = existingArtists.ToList();
        var originalAlbumArtists = existingAlbumArtists.ToList();
        var rewrittenArtists = RewriteCreditList(originalArtists, rewriteCredit);
        var rewrittenAlbumArtists = RewriteCreditList(originalAlbumArtists, rewriteCredit);
        var rewrittenTitle = string.IsNullOrWhiteSpace(existingTitle)
            ? existingTitle
            : rewriteCredit(existingTitle)?.Trim();

        var forcedArtist = rewrittenArtists.Count > 0
            && !AreArtistCreditsEquivalent(originalArtists, rewrittenArtists);
        var forcedAlbumArtist = rewrittenAlbumArtists.Count > 0
            && !AreArtistCreditsEquivalent(originalAlbumArtists, rewrittenAlbumArtists);
        var forcedTitle = !string.IsNullOrWhiteSpace(rewrittenTitle)
            && !string.Equals(existingTitle?.Trim(), rewrittenTitle, StringComparison.Ordinal);

        if (!forcedArtist && !forcedAlbumArtist && !forcedTitle)
        {
            return ArtistAliasOverwriteDecision.None;
        }

        if (forcedArtist)
        {
            existingArtists.Clear();
            existingArtists.AddRange(rewrittenArtists);
            sourceTrack.Artists = rewrittenArtists.ToList();
            effectiveTagSettings.Artist = true;
            effectiveTagSettings.Artists = true;
        }

        if (forcedAlbumArtist)
        {
            existingAlbumArtists.Clear();
            existingAlbumArtists.AddRange(rewrittenAlbumArtists);
            sourceTrack.AlbumArtists = rewrittenAlbumArtists.ToList();
            effectiveTagSettings.AlbumArtist = true;
        }
        else if (forcedArtist && sourceTrack.AlbumArtists.Count == 0 && rewrittenArtists.Count > 0)
        {
            sourceTrack.AlbumArtists = new List<string> { rewrittenArtists[0] };
            effectiveTagSettings.AlbumArtist = true;
        }

        if (forcedTitle && !string.IsNullOrWhiteSpace(rewrittenTitle))
        {
            sourceTrack.Title = rewrittenTitle;
            effectiveTagSettings.Title = true;
        }

        return new ArtistAliasOverwriteDecision(forcedArtist, forcedAlbumArtist, forcedTitle, rewrittenTitle);
    }

    private static void PreserveRicherCreditsWithoutBlockingPreferredWrite(
        AutoTagTrack sourceTrack,
        List<string> existingArtists,
        List<string> existingAlbumArtists)
    {
        var existing = SplitArtistCredits(existingArtists);
        var incoming = SplitArtistCredits(sourceTrack.Artists);
        if (existing.Count > 0
            && ShouldPreferSourceArtistCredits(existing, incoming))
        {
            sourceTrack.Artists = existing.ToList();
        }

        var existingAlbum = SplitArtistCredits(existingAlbumArtists);
        var incomingAlbum = SplitArtistCredits(sourceTrack.AlbumArtists);
        if (existingAlbum.Count > 0
            && ShouldPreferSourceArtistCredits(existingAlbum, incomingAlbum))
        {
            sourceTrack.AlbumArtists = existingAlbum.ToList();
        }
    }

    private static void ApplyAlbumLossyOverwriteGuard(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        string? existingAlbum)
    {
        var incomingAlbum = sourceTrack.Album?.Trim();
        var currentAlbum = existingAlbum?.Trim();
        if (!effectiveTagSettings.Album
            || string.IsNullOrWhiteSpace(currentAlbum)
            || string.IsNullOrWhiteSpace(incomingAlbum))
        {
            return;
        }

        var similarity = AutoTagSimilarity.ComputeScore(
            AutoTagSimilarity.NormalizeText(currentAlbum),
            AutoTagSimilarity.NormalizeText(incomingAlbum));
        if (similarity >= 0.90d)
        {
            return;
        }

        sourceTrack.Album = currentAlbum;
        effectiveTagSettings.Album = false;
    }

    private static void ApplyPlatformOverwriteGuards(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        TagLib.File file,
        string platformId,
        ArtistAliasOverwriteDecision aliasOverwrite = default)
    {
        if (!string.Equals(platformId, BoomplayPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var existingTitle = file.Tag.Title?.Trim();
        if (effectiveTagSettings.Title
            && !aliasOverwrite.ForcedTitle
            && !string.IsNullOrWhiteSpace(existingTitle)
            && !string.IsNullOrWhiteSpace(sourceTrack.Title))
        {
            var normalizedExistingTitle = AutoTagSimilarity.NormalizeText(existingTitle);
            var normalizedIncomingTitle = AutoTagSimilarity.NormalizeText(sourceTrack.Title);
            var titleSimilarity = AutoTagSimilarity.ComputeScore(normalizedExistingTitle, normalizedIncomingTitle);
            if (!TrackTitleMatcher.HasCompatibleTitleIdentity(existingTitle, sourceTrack.Title)
                || titleSimilarity < 0.90d)
            {
                sourceTrack.Title = existingTitle;
                effectiveTagSettings.Title = false;
            }
        }

        var existingArtists = SplitArtistCredits(file.Tag.Performers?.Where(value => !IsWeakMetadataValue(value) && !IsVariousArtistsValue(value)).ToList()
            ?? new List<string>());
        var incomingArtists = SplitArtistCredits(sourceTrack.Artists);
        if (effectiveTagSettings.Artist
            && !aliasOverwrite.ForcedArtist
            && existingArtists.Count > 0
            && incomingArtists.Count > 0
            && !AreArtistCreditsEquivalent(existingArtists, incomingArtists))
        {
            sourceTrack.Artists = existingArtists;
            effectiveTagSettings.Artist = false;
        }

        var existingAlbumArtists = SplitArtistCredits(file.Tag.AlbumArtists?.Where(value => !IsWeakMetadataValue(value) && !IsVariousArtistsValue(value)).ToList()
            ?? new List<string>());
        var incomingAlbumArtists = SplitArtistCredits(sourceTrack.AlbumArtists);
        if (effectiveTagSettings.AlbumArtist
            && !aliasOverwrite.ForcedAlbumArtist
            && existingAlbumArtists.Count > 0
            && incomingAlbumArtists.Count > 0
            && !AreArtistCreditsEquivalent(existingAlbumArtists, incomingAlbumArtists))
        {
            sourceTrack.AlbumArtists = existingAlbumArtists;
            effectiveTagSettings.AlbumArtist = false;
        }

        var existingAlbum = file.Tag.Album?.Trim();
        var incomingAlbum = sourceTrack.Album?.Trim();
        if (effectiveTagSettings.Album
            && !string.IsNullOrWhiteSpace(existingAlbum)
            && !string.IsNullOrWhiteSpace(incomingAlbum))
        {
            var normalizedExistingAlbum = AutoTagSimilarity.NormalizeText(existingAlbum);
            var normalizedIncomingAlbum = AutoTagSimilarity.NormalizeText(incomingAlbum);
            var albumSimilarity = AutoTagSimilarity.ComputeScore(normalizedExistingAlbum, normalizedIncomingAlbum);
            if (albumSimilarity < 0.90d)
            {
                sourceTrack.Album = existingAlbum;
                effectiveTagSettings.Album = false;
            }
        }
    }

    private static void ApplyPreferenceAwareArtistGuards(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        DeezSpoTagSettings runtimeSettings,
        List<string> existingArtists,
        List<string> existingAlbumArtists,
        string? existingTitle)
    {
        if ((!effectiveTagSettings.Artist && !effectiveTagSettings.AlbumArtist && !effectiveTagSettings.Title)
            || existingArtists.Count == 0)
        {
            return;
        }

        var normalizedExistingArtists = SplitArtistCredits(existingArtists);
        if (normalizedExistingArtists.Count == 0)
        {
            return;
        }

        var normalizedIncomingArtists = SplitArtistCredits(sourceTrack.Artists);
        if (normalizedIncomingArtists.Count == 0)
        {
            normalizedIncomingArtists = SplitArtistCredits(sourceTrack.AlbumArtists);
        }

        if (normalizedIncomingArtists.Count == 0)
        {
            return;
        }

        var multiArtistSeparator = runtimeSettings.Tags?.MultiArtistSeparator ?? MultiArtistSeparatorDefault;
        var keepSingleArtistOnly = string.Equals(multiArtistSeparator, MultiArtistSeparatorNothing, StringComparison.OrdinalIgnoreCase);
        var artistsMatchOrPreferred = AreArtistCreditsEquivalent(normalizedExistingArtists, normalizedIncomingArtists)
            || (!keepSingleArtistOnly && ShouldPreferSourceArtistCredits(normalizedExistingArtists, normalizedIncomingArtists));
        if (!artistsMatchOrPreferred)
        {
            effectiveTagSettings.Artist = false;
            effectiveTagSettings.AlbumArtist = false;
            return;
        }

        sourceTrack.Artists = normalizedExistingArtists.ToList();
        if (effectiveTagSettings.Artist)
        {
            effectiveTagSettings.Artist = false;
        }

        ApplyAlbumArtistGuards(
            effectiveTagSettings,
            sourceTrack,
            runtimeSettings,
            normalizedExistingArtists,
            existingAlbumArtists,
            keepSingleArtistOnly);
        ApplyTitleFeaturedGuard(effectiveTagSettings, sourceTrack, runtimeSettings, normalizedExistingArtists, existingTitle);
    }

    private static void ApplyAlbumArtistGuards(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        DeezSpoTagSettings runtimeSettings,
        List<string> normalizedExistingArtists,
        List<string> existingAlbumArtists,
        bool keepSingleArtistOnly)
    {
        var singleAlbumArtist = runtimeSettings.Tags?.SingleAlbumArtist ?? true;
        var normalizedExistingAlbumArtists = SplitArtistCredits(existingAlbumArtists);
        var normalizedIncomingAlbumArtists = SplitArtistCredits(sourceTrack.AlbumArtists);
        if (singleAlbumArtist)
        {
            ApplySingleAlbumArtistGuard(
                effectiveTagSettings,
                sourceTrack,
                normalizedExistingArtists,
                normalizedExistingAlbumArtists);
            return;
        }

        var albumArtistsMatchOrPreferred = normalizedExistingAlbumArtists.Count > 0
            && (AreArtistCreditsEquivalent(normalizedExistingAlbumArtists, normalizedIncomingAlbumArtists)
                || (!keepSingleArtistOnly
                    && ShouldPreferSourceArtistCredits(normalizedExistingAlbumArtists, normalizedIncomingAlbumArtists)));
        if (!albumArtistsMatchOrPreferred)
        {
            return;
        }

        sourceTrack.AlbumArtists = normalizedExistingAlbumArtists.ToList();
        if (effectiveTagSettings.AlbumArtist)
        {
            effectiveTagSettings.AlbumArtist = false;
        }
    }

    private static void ApplySingleAlbumArtistGuard(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        List<string> normalizedExistingArtists,
        List<string> normalizedExistingAlbumArtists)
    {
        string? preferredAlbumArtist = null;
        for (var i = 0; i < normalizedExistingAlbumArtists.Count; i++)
        {
            var candidate = normalizedExistingAlbumArtists[i];
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                preferredAlbumArtist = candidate;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(preferredAlbumArtist) && normalizedExistingArtists.Count > 0)
        {
            preferredAlbumArtist = normalizedExistingArtists[0];
        }
        if (string.IsNullOrWhiteSpace(preferredAlbumArtist))
        {
            return;
        }

        sourceTrack.AlbumArtists = new List<string> { preferredAlbumArtist };
        if (effectiveTagSettings.AlbumArtist
            && normalizedExistingAlbumArtists.Count > 0
            && AreArtistPrimaryCompatible(normalizedExistingAlbumArtists[0], preferredAlbumArtist))
        {
            effectiveTagSettings.AlbumArtist = false;
        }
    }

    private static void ApplyTitleFeaturedGuard(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        DeezSpoTagSettings runtimeSettings,
        List<string> normalizedExistingArtists,
        string? existingTitle)
    {
        if (!effectiveTagSettings.Title
            || !string.Equals(runtimeSettings.FeaturedToTitle, "2", StringComparison.OrdinalIgnoreCase)
            || normalizedExistingArtists.Count <= 1
            || string.IsNullOrWhiteSpace(existingTitle)
            || !HasFeaturedMarker(existingTitle))
        {
            return;
        }

        sourceTrack.Title = existingTitle.Trim();
        effectiveTagSettings.Title = false;
    }

    private static void ApplyTitleLossyOverwriteGuard(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        string? existingTitle,
        string platformId)
    {
        _ = platformId;
        if (!effectiveTagSettings.Title
            || string.IsNullOrWhiteSpace(existingTitle)
            || string.IsNullOrWhiteSpace(sourceTrack.Title))
        {
            return;
        }

        var existing = existingTitle.Trim();
        var incoming = sourceTrack.Title.Trim();
        if (string.Equals(existing, incoming, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!ShouldKeepExistingTitle(existing, incoming))
        {
            return;
        }

        sourceTrack.Title = existing;
        effectiveTagSettings.Title = false;
    }

    private static bool ShouldKeepExistingTitle(string existingTitle, string incomingTitle)
    {
        if (TrackIdentityTrust.IsWeakMetadataValue(existingTitle))
        {
            return false;
        }

        if (IsNearMissAlternativeTitle(existingTitle, incomingTitle))
        {
            return true;
        }

        var existingNormalized = NormalizeLooseTitle(existingTitle);
        var incomingNormalized = NormalizeLooseTitle(incomingTitle);
        if (string.IsNullOrWhiteSpace(existingNormalized) || string.IsNullOrWhiteSpace(incomingNormalized))
        {
            return false;
        }

        if (string.Equals(existingNormalized, incomingNormalized, StringComparison.Ordinal))
        {
            return false;
        }

        var existingHasDetails = HasDetailedTitleMarkers(existingTitle);
        if (!existingHasDetails)
        {
            return false;
        }

        if (HasDetailedTitleMarkers(incomingTitle))
        {
            return false;
        }

        if (existingTitle.Length <= incomingTitle.Length + 2)
        {
            return false;
        }

        return existingNormalized.Contains(incomingNormalized, StringComparison.Ordinal);
    }

    private static bool HasDetailedTitleMarkers(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        return HasFeaturedMarker(title)
            || TitleQualifierRegex.IsMatch(title)
            || BracketedTitleDetailRegex.IsMatch(title)
            || VariantSuffixRegex.IsMatch(title.Trim());
    }

    private static bool IsNearMissAlternativeTitle(string existingTitle, string incomingTitle)
    {
        if (TrackTitleMatcher.HasCompatibleTitleIdentity(existingTitle, incomingTitle))
        {
            return false;
        }

        var existingNormalized = AutoTagSimilarity.NormalizeText(OneTaggerMatching.CleanTitleMatching(existingTitle));
        var incomingNormalized = AutoTagSimilarity.NormalizeText(OneTaggerMatching.CleanTitleMatching(incomingTitle));
        if (string.IsNullOrWhiteSpace(existingNormalized) || string.IsNullOrWhiteSpace(incomingNormalized))
        {
            return false;
        }

        // Old MusicBrainz/OneTagger fuzzy thresholds treated scores around 0.86 as the same work.
        return AutoTagSimilarity.ComputeScore(existingNormalized, incomingNormalized) >= 0.80d;
    }

    private static string NormalizeLooseTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return LooseTitleNormalizationRegex.Replace(value.ToLowerInvariant(), string.Empty);
    }

    private static bool AreArtistCreditsEquivalent(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var normalizedLeft = SplitArtistCredits(left);
        var normalizedRight = SplitArtistCredits(right);
        if (normalizedLeft.Count != normalizedRight.Count)
        {
            return false;
        }

        for (var i = 0; i < normalizedLeft.Count; i++)
        {
            if (!string.Equals(normalizedLeft[i], normalizedRight[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreArtistPrimaryCompatible(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var leftTrimmed = left.Trim();
        var rightTrimmed = right.Trim();
        if (string.Equals(leftTrimmed, rightTrimmed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return leftTrimmed.Contains(rightTrimmed, StringComparison.OrdinalIgnoreCase)
            || rightTrimmed.Contains(leftTrimmed, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasFeaturedMarker(string title)
    {
        return title.Contains("(feat", StringComparison.OrdinalIgnoreCase)
            || title.Contains(" feat.", StringComparison.OrdinalIgnoreCase)
            || title.Contains(" ft.", StringComparison.OrdinalIgnoreCase)
            || title.Contains(" featuring ", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveArtistSeparator(AutoTagRunnerConfig config, string filePath)
    {
        if (config.Separators == null)
        {
            return "";
        }

        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return config.Separators.Id3 ?? "";
        }

        if (IsMp4Family(extension))
        {
            return config.Separators.Mp4 ?? "";
        }

        return config.Separators.Vorbis ?? "";
    }

    private static List<string> ReadAppleDashBox(TagLib.Mpeg4.AppleTag tag, string name)
    {
        return AppleDashBoxReflectionHelper.ReadValues(tag, name);
    }

    private static void TrySetAppleDashBox(TagLib.Mpeg4.AppleTag? tag, string name, string[] values)
    {
        if (!AppleDashBoxReflectionHelper.TrySetValues(tag, name, values))
        {
            throw new InvalidOperationException($"Failed to set MP4 dash box {name}.");
        }
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

    private static string SanitizeLogValue(string? value)
    {
        return LogSanitizer.OneLine(value);
    }

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
