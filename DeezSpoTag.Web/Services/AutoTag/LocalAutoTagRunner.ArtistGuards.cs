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

public sealed partial class LocalAutoTagRunner : IAutoTagRunner
{

    private static bool AreArtistIdentitiesCompatibleForOverwrite(
        IReadOnlyList<string> sourceArtists,
        IReadOnlyList<string> incomingArtists,
        double strictness)
    {
        var sourceCredits = SplitArtistCredits(sourceArtists);
        var incomingCredits = SplitArtistCredits(incomingArtists);
        if (HasDottedInitialArtistCollapse(sourceCredits, incomingCredits))
        {
            return false;
        }

        var normalizedSource = sourceCredits
            .Select(NormalizeArtistIdentity)
            .Where(artist => !string.IsNullOrWhiteSpace(artist))
            .ToList();
        var normalizedIncoming = incomingCredits
            .Select(NormalizeArtistIdentity)
            .Where(artist => !string.IsNullOrWhiteSpace(artist))
            .ToList();

        if (normalizedSource.Count == 0 || normalizedIncoming.Count == 0)
        {
            return true;
        }

        if (normalizedSource.Any(source => normalizedIncoming.Contains(source, StringComparer.Ordinal)))
        {
            return true;
        }

        var sourceJoined = string.Join(" ", normalizedSource);
        var incomingJoined = string.Join(" ", normalizedIncoming);
        var similarity = AutoTagSimilarity.ComputeScore(sourceJoined, incomingJoined);
        return similarity >= Math.Clamp(strictness + 0.15d, 0.80d, 0.98d);
    }

    private static bool HasDottedInitialArtistCollapse(IReadOnlyList<string> sourceArtists, IReadOnlyList<string> incomingArtists)
    {
        foreach (var sourceArtist in sourceArtists)
        {
            foreach (var incomingArtist in incomingArtists)
            {
                if (!string.Equals(
                        NormalizeArtistIdentity(sourceArtist),
                        NormalizeArtistIdentity(incomingArtist),
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (IsDottedInitialArtist(sourceArtist) != IsDottedInitialArtist(incomingArtist))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsDottedInitialArtist(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var compact = Regex.Replace(value.Trim(), @"\s+", string.Empty, RegexOptions.None, RegexTimeout);
        return Regex.IsMatch(compact, @"^\p{L}(?:\.\p{L})+\.?$", RegexOptions.None, RegexTimeout);
    }

    private static string NormalizeArtistIdentity(string value)
    {
        return AutoTagSimilarity.NormalizeText(value);
    }

    private static string ApplyTitleRegexFilter(string title, string? titleRegex)
    {
        if (string.IsNullOrWhiteSpace(titleRegex) || string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        try
        {
            var regex = new Regex(titleRegex, RegexOptions.IgnoreCase, RegexTimeout);
            return regex.Replace(title, string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return title;
        }
    }

    private static List<string> SplitArtistCredits(IEnumerable<string> rawCredits)
    {
        return ArtistNameNormalizer.ExpandArtistNames(rawCredits);
    }

    private static void PreserveSourceTitleWording(AutoTagAudioInfo sourceInfo, AutoTagTrack track)
    {
        if (track == null || string.IsNullOrWhiteSpace(sourceInfo?.Title))
        {
            return;
        }

        var incomingFullTitle = OneTaggerMatching.FullTitle(track.Title, track.Version);
        if (!TrackTitleMatcher.ShouldPreserveSourceTitleWording(sourceInfo.Title, incomingFullTitle))
        {
            return;
        }

        // The source title already carries its own variant wording, so the incoming
        // version fragment must be dropped — WriteTitleTag re-appends Version in
        // parentheses when ShortTitle is off, which would duplicate the variant.
        track.Title = sourceInfo.Title.Trim();
        track.Version = null;
    }

    private static void PreserveRicherArtistCreditsFromSource(
        AutoTagAudioInfo sourceInfo,
        AutoTagTrack track,
        DeezSpoTagSettings settings)
    {
        IEnumerable<string> sourceArtistValues = sourceInfo.Artists.Count > 0
            ? sourceInfo.Artists
            : Array.Empty<string>();
        if (sourceInfo.Artists.Count == 0 && !string.IsNullOrWhiteSpace(sourceInfo.Artist))
        {
            sourceArtistValues = new[] { sourceInfo.Artist };
        }

        var sourceArtists = SplitArtistCredits(sourceArtistValues);
        var matchedArtists = SplitArtistCredits(track.Artists);

        if (ShouldPreferSourceArtistCredits(sourceArtists, matchedArtists))
        {
            track.Artists = sourceArtists;
        }
        else if (matchedArtists.Count > 0)
        {
            track.Artists = matchedArtists;
        }

        var normalizedAlbumArtists = SplitArtistCredits(track.AlbumArtists);
        if (normalizedAlbumArtists.Count == 0 && track.Artists.Count > 0)
        {
            normalizedAlbumArtists = track.Artists.ToList();
        }

        var singleAlbumArtist = settings.Tags?.SingleAlbumArtist ?? true;
        if (singleAlbumArtist && normalizedAlbumArtists.Count > 1)
        {
            normalizedAlbumArtists = new List<string> { normalizedAlbumArtists[0] };
        }

        track.AlbumArtists = normalizedAlbumArtists;
    }

    private static bool ShouldPreferSourceArtistCredits(List<string> sourceArtists, List<string> matchedArtists)
    {
        if (sourceArtists.Count == 0)
        {
            return false;
        }

        if (matchedArtists.Count == 0)
        {
            return true;
        }

        if (sourceArtists.Count <= matchedArtists.Count)
        {
            return false;
        }

        var sourcePrimary = sourceArtists[0];
        var matchedPrimary = matchedArtists[0];
        if (string.IsNullOrWhiteSpace(sourcePrimary) || string.IsNullOrWhiteSpace(matchedPrimary))
        {
            return true;
        }

        if (string.Equals(sourcePrimary, matchedPrimary, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return sourcePrimary.Contains(matchedPrimary, StringComparison.OrdinalIgnoreCase)
            || matchedPrimary.Contains(sourcePrimary, StringComparison.OrdinalIgnoreCase);
    }

    private static void NormalizeTrackArtistsForTagging(AutoTagTrack track, bool singleAlbumArtist)
    {
        var normalizedArtists = SplitArtistCredits(track.Artists);
        var normalizedAlbumArtists = SplitArtistCredits(track.AlbumArtists);
        if (normalizedArtists.Count > 0 && normalizedArtists.All(IsWeakMetadataValue))
        {
            normalizedArtists.Clear();
        }

        if (normalizedAlbumArtists.Count > 0 && normalizedAlbumArtists.All(IsWeakMetadataValue))
        {
            normalizedAlbumArtists.Clear();
        }

        if (normalizedArtists.Count == 0)
        {
            normalizedArtists = normalizedAlbumArtists.ToList();
        }

        if (normalizedArtists.Count == 0)
        {
            normalizedArtists.Add(UnknownArtist);
        }

        if (normalizedAlbumArtists.Count == 0)
        {
            normalizedAlbumArtists = normalizedArtists.ToList();
        }

        if (singleAlbumArtist && normalizedAlbumArtists.Count > 1)
        {
            normalizedAlbumArtists = new List<string> { normalizedAlbumArtists[0] };
        }

        track.Artists = normalizedArtists;
        track.AlbumArtists = normalizedAlbumArtists;
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
}
