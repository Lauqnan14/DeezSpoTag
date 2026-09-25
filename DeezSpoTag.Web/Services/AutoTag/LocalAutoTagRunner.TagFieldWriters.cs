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

    private static readonly ProviderIdentityField[] IdentityReturnedFields =
    [
        ProviderIdentityField.TrackId,
        ProviderIdentityField.AlbumId,
        ProviderIdentityField.ReleaseId,
        ProviderIdentityField.ArtistId,
        ProviderIdentityField.AlbumArtistId,
        ProviderIdentityField.Url
    ];

    private static HashSet<SupportedTag> ResolveReturnedEligibleTags(
        AutoTagTrack track,
        ProviderTagPlan plan,
        ProviderIdentityPayload? providerIdentity)
    {
        var identityTags = IdentityReturnedFields
            .Select(field => AutoTagIdentityTags.ResolveFamily("unknown", field).SupportedTag)
            .ToHashSet();

        var returned = CollectAutoTagTags(track)
            .Select(tag => SupportedTagMap.TryGetValue(tag, out var mapped) ? (SupportedTag?)mapped : null)
            .Where(tag => tag.HasValue && plan.Eligible.Contains(tag.Value))
            .Select(tag => tag!.Value)
            // Provider identity is decided by the immutable provider payload, never by
            // a mutated track value or by whichever alias happens to exist on disk.
            .Where(tag => !identityTags.Contains(tag) && tag != SupportedTag.RecordingId)
            .ToHashSet();

        if (providerIdentity is { IsNativeProviderResult: true })
        {
            foreach (var field in IdentityReturnedFields)
            {
                if (string.IsNullOrWhiteSpace(providerIdentity.ValueFor(field)))
                {
                    continue;
                }

                var family = AutoTagIdentityTags.ResolveFamily(providerIdentity.ProviderId, field);
                if (plan.Eligible.Contains(family.SupportedTag))
                {
                    returned.Add(family.SupportedTag);
                }
            }
        }

        return returned;
    }

    private static string? TryResolveProspectiveAlbumDirectory(AutoTagFileRunContext context, AutoTagTrack track)
    {
        if (context.Plan.Config.MaterializeToTemplatePath != true)
        {
            return null;
        }

        try
        {
            var separator = ResolveArtistSeparator(context.Plan.Config, context.File);
            var coreTrack = BuildCoreTrack(
                track,
                separator,
                context.Plan.TagSettings.SingleAlbumArtist,
                context.Plan.Settings);
            var pathInfo = BuildTemplatePathInfo(coreTrack, context.Plan.Settings);
            var candidate = pathInfo.CoverPath ?? pathInfo.FilePath;
            return ResolveAlbumRootDirectory(context.File, candidate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static List<string> RewriteCreditList(
        IEnumerable<string>? credits,
        Func<string?, string>? rewriteCredit = null)
    {
        rewriteCredit ??= static value => DeezSpoTag.Services.Library.ArtistAliasGateway.ResolveCredit(value);
        return (credits ?? Array.Empty<string>())
            .Select(credit => rewriteCredit(credit) ?? string.Empty)
            .Select(static credit => credit.Trim())
            .Where(static credit => credit.Length > 0)
            .ToList();
    }

    private static List<string> ResolveArtistValues(Track coreTrack, TagSettings tagSettings)
    {
        var artists = coreTrack.Artists
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (artists.Count == 0)
        {
            return new List<string>();
        }

        if (tagSettings.SingleAlbumArtist)
        {
            var primary = coreTrack.MainArtist?.Name;
            return string.IsNullOrWhiteSpace(primary)
                ? new List<string> { artists[0] }
                : new List<string> { primary.Trim() };
        }

        if (string.Equals(tagSettings.MultiArtistSeparator, MultiArtistSeparatorDefault, StringComparison.OrdinalIgnoreCase))
        {
            return artists;
        }

        if (string.Equals(tagSettings.MultiArtistSeparator, MultiArtistSeparatorNothing, StringComparison.OrdinalIgnoreCase))
        {
            var primary = coreTrack.MainArtist?.Name;
            return string.IsNullOrWhiteSpace(primary)
                ? new List<string> { artists[0] }
                : new List<string> { primary.Trim() };
        }

        var joined = string.IsNullOrWhiteSpace(coreTrack.ArtistsString)
            ? string.Join(", ", artists)
            : coreTrack.ArtistsString;
        return new List<string> { joined };
    }

    private static void WriteTitleTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(TitleTag) || !context.EffectiveTagSettings.Title)
        {
            return;
        }

        var titleValue = context.CoreTrack.Title;
        if (!context.Config.ShortTitle
            && !string.IsNullOrWhiteSpace(context.SourceTrack.Version)
            && !titleValue.Contains(context.SourceTrack.Version, StringComparison.OrdinalIgnoreCase))
        {
            titleValue = $"{titleValue} ({context.SourceTrack.Version})";
        }

        SetField(tagWriteContext, new TagFieldBinding("TIT2", TitleUpperTag, "©nam", SupportedTag.Title), new List<string> { titleValue });
    }

    private static void WriteVersionTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(VersionTag) || string.IsNullOrWhiteSpace(context.SourceTrack.Version))
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TIT3", "SUBTITLE", "desc", SupportedTag.Version), new List<string> { context.SourceTrack.Version });
    }

    private static void WriteArtistTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ArtistTag) || !context.EffectiveTagSettings.Artist)
        {
            return;
        }

        var artistValues = ResolveArtistValues(context.CoreTrack, context.EffectiveTagSettings);
        if (artistValues.Count == 0)
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TPE1", ArtistUpperTag, "©ART", SupportedTag.Artist), artistValues);
    }

    private static void WriteArtistsTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ArtistsTag) || !context.EffectiveTagSettings.Artists)
        {
            return;
        }

        if (string.Equals(
                context.EffectiveTagSettings.MultiArtistSeparator,
                MultiArtistSeparatorDefault,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var artists = context.CoreTrack.Artists
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (artists.Count == 0)
        {
            return;
        }

        var value = context.EffectiveTagSettings.MultiArtistSeparator switch
        {
            MultiArtistSeparatorNothing => context.CoreTrack.MainArtist?.Name ?? artists[0],
            MultiArtistSeparatorDefault => string.Join(", ", artists),
            _ when !string.IsNullOrWhiteSpace(context.CoreTrack.ArtistsString) => context.CoreTrack.ArtistsString,
            _ => string.Join(", ", artists)
        };
        SetRawIfAllowed(tagWriteContext, ArtistsTag, "ARTISTS", new List<string> { value });
    }

    private static void WriteAlbumTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(AlbumTag) || !context.EffectiveTagSettings.Album || context.CoreTrack.Album == null)
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TALB", AlbumUpperTag, "©alb", SupportedTag.Album), new List<string> { context.CoreTrack.Album.Title });
    }

    private static void WriteKeyTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("key") || string.IsNullOrWhiteSpace(context.SourceTrack.Key))
        {
            return;
        }

        var keyValue = context.Config.Camelot ? ToCamelot(context.SourceTrack.Key) : context.SourceTrack.Key;
        SetField(tagWriteContext, new TagFieldBinding("TKEY", "INITIALKEY", InitialKeyRawTag, SupportedTag.Key), new List<string> { keyValue });
    }

    private static void WriteBpmTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("bpm") || !context.SourceTrack.Bpm.HasValue)
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TBPM", "BPM", "tmpo", SupportedTag.BPM), new List<string> { context.SourceTrack.Bpm.Value.ToString() });
    }

    private static void WriteLabelTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(LabelTag) || string.IsNullOrWhiteSpace(context.SourceTrack.Label))
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TPUB", LabelUpperTag, LabelUpperTag, SupportedTag.Label), new List<string> { context.SourceTrack.Label });
    }

    private static void WriteAudioFeatureTag(
        TagWriteContext tagWriteContext,
        TagWriteExecutionContext context,
        string enabledTag,
        string rawTag,
        SupportedTag supportedTag,
        double? value)
    {
        if (!context.EnabledTags.Contains(enabledTag) || !value.HasValue)
        {
            return;
        }

        SetRaw(tagWriteContext, rawTag, supportedTag, new List<string> { FormatAudioFeature(value.Value) });
    }

    /// <summary>
    /// True when the final genre list equals the file's current genres exactly
    /// (same values, same casing, order-insensitive). Casing-only changes such as
    /// R&b → R&B are real repairs and must be written.
    /// </summary>
    private static bool GenreWriteAddsNothing(List<string> genres, List<string> existingGenres)
    {
        if (genres.Count != existingGenres.Count)
        {
            return false;
        }

        return genres
            .Select(value => value?.Trim() ?? string.Empty)
            .OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(
                existingGenres.Select(value => value?.Trim() ?? string.Empty).OrderBy(value => value, StringComparer.Ordinal),
                StringComparer.Ordinal);
    }

    private static List<string> NormalizeStyleValues(IEnumerable<string> values, string separator)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var effectiveSeparator = string.IsNullOrEmpty(separator) ? "," : separator;
            var parts = value.Split(
                effectiveSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0 && seen.Add(trimmed))
                {
                    normalized.Add(trimmed);
                }
            }
        }

        return normalized;
    }

    private static void WriteReleaseDateTag(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ReleaseDateTag) || !context.SourceTrack.ReleaseDate.HasValue)
        {
            return;
        }

        WriteDate(
            file,
            context.Extension,
            ReleaseDateTag,
            context.SourceTrack.ReleaseDate.Value,
            SupportedTag.ReleaseDate,
            context.Config,
            context.EffectiveTagSettings.UseNullSeparator);
        MarkAttemptedIfPresent(context, file, SupportedTag.ReleaseDate);
    }

    private static void WritePublishDateTag(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(PublishDateTag) || !context.SourceTrack.PublishDate.HasValue)
        {
            return;
        }

        WriteDate(
            file,
            context.Extension,
            PublishDateTag,
            context.SourceTrack.PublishDate.Value,
            SupportedTag.PublishDate,
            context.Config,
            context.EffectiveTagSettings.UseNullSeparator);
        MarkAttemptedIfPresent(context, file, SupportedTag.PublishDate);
    }

    private static void WriteRemixerTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(RemixerTag) || context.SourceTrack.Remixers.Count == 0)
        {
            return;
        }

        SetField(
            tagWriteContext,
            new TagFieldBinding("TPE4", RemixerUpperTag, RemixerUpperTag, SupportedTag.Remixer),
            context.SourceTrack.Remixers.ToList());
    }

    private static void WriteMoodTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("mood") || string.IsNullOrWhiteSpace(context.SourceTrack.Mood))
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TMOO", "MOOD", "MOOD", SupportedTag.Mood), new List<string> { context.SourceTrack.Mood });
    }

    private static void WriteActivityTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("activity") || string.IsNullOrWhiteSpace(context.SourceTrack.Activity))
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("ACTIVITY", "ACTIVITY", "ACTIVITY", SupportedTag.Activity), new List<string> { context.SourceTrack.Activity });
    }

    private static void WriteDiscNumberTag(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(DiscNumberTag)
            || !context.EffectiveTagSettings.DiscNumber
            || !context.SourceTrack.DiscNumber.HasValue)
        {
            return;
        }

        SetTrackNumber(
            file,
            context,
            context.SourceTrack.DiscNumber.Value,
            ResolveFirstPositiveInt(context.SourceTrack, DiscTotalTag, DiscTotalRawTag),
            SupportedTag.DiscNumber,
            isDisc: true);
        MarkAttemptedIfPresent(context, file, SupportedTag.DiscNumber);
        MarkAttemptedIfPresent(context, file, SupportedTag.DiscTotal);
    }

    private static void WriteDiscTotalTag(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(DiscTotalTag)
            || !context.EffectiveTagSettings.DiscTotal)
        {
            return;
        }

        var total = context.SourceTrack.DiscTotal is > 0
            ? context.SourceTrack.DiscTotal
            : ResolveFirstPositiveInt(context.SourceTrack, DiscTotalTag, DiscTotalRawTag);
        if (!total.HasValue)
        {
            return;
        }

        SetDiscTotal(file, context, total.Value);
        MarkAttemptedIfPresent(context, file, SupportedTag.DiscTotal);
    }

    private static void WriteTrackNumberTag(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(TrackNumberTag)
            || !context.EffectiveTagSettings.TrackNumber
            || !context.SourceTrack.TrackNumber.HasValue)
        {
            return;
        }

        var total = context.EnabledTags.Contains(TrackTotalTag)
            && context.EffectiveTagSettings.TrackTotal
            && context.SourceTrack.TrackTotal is > 0
            ? context.SourceTrack.TrackTotal
            : null;
        SetTrackNumber(
            file,
            context,
            context.SourceTrack.TrackNumber.Value,
            total,
            SupportedTag.TrackNumber,
            isDisc: false);
        MarkAttemptedIfPresent(context, file, SupportedTag.TrackNumber);
        MarkAttemptedIfPresent(context, file, SupportedTag.TrackTotal);
    }

    private static void WriteBarcodeTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(BarcodeTag) || !context.EffectiveTagSettings.Barcode)
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, BarcodeTag, "upc", BarcodeRawTag);
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, BarcodeTag, BarcodeRawTag, values);
    }

    private static void WriteReplayGainTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ReplayGainTag) || !context.EffectiveTagSettings.ReplayGain)
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, ReplayGainTag, ReplayGainRawTag, "gain");
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, ReplayGainTag, ReplayGainRawTag, values);
    }

    private static void WriteCopyrightTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(CopyrightTag) || !context.EffectiveTagSettings.Copyright)
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, CopyrightTag, CopyrightRawTag);
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, CopyrightTag, CopyrightRawTag, values);
    }

    private static void WriteComposerTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ComposerTag) || !context.EffectiveTagSettings.Composer)
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, ComposerTag, ComposerUpperTag, "TCOM");
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, ComposerTag, ResolveComposerRawName(context.Extension), values);
    }

    private static void WriteInvolvedPeopleTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(InvolvedPeopleTag) || !context.EffectiveTagSettings.InvolvedPeople)
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, InvolvedPeopleTag, InvolvedPeopleRawTag);
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, InvolvedPeopleTag, InvolvedPeopleRawTag, values);
    }

    private static void WritePublisherTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(PublisherTag) || !context.EffectiveTagSettings.Publisher)
        {
            return;
        }

        var values = ResolveFirstClassOrOtherValues(context.SourceTrack.Publisher, context.SourceTrack, PublisherTag, PublisherRawTag);
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, PublisherTag, PublisherRawTag, values);
    }

    private static void WriteDescriptionTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(DescriptionTag) || !context.EffectiveTagSettings.Description)
        {
            return;
        }

        var values = ResolveFirstClassOrOtherValues(context.SourceTrack.Description, context.SourceTrack, DescriptionTag, DescriptionRawTag, CommentRawTag);
        if (values.Count == 0)
        {
            return;
        }

        var rawName = IsMp4Family(context.Extension) ? "ldes" : DescriptionRawTag;
        SetRawIfAllowed(tagWriteContext, DescriptionTag, rawName, values);
    }

    private static void WriteSourceTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(SourceTag) || !context.EffectiveTagSettings.Source)
        {
            return;
        }

        if (string.Equals(context.PlatformId, ShazamPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var sourceValues = ResolveOtherValues(context.SourceTrack, SourceTag);
        if (sourceValues.Count == 0)
        {
            sourceValues.Add(context.PlatformId.ToUpperInvariant());
        }

        SetRawIfAllowed(tagWriteContext, SourceTag, SourceRawTag, sourceValues);

        var sourceIdValues = ResolveOtherValues(context.SourceTrack, "sourceId", "SOURCE_ID", SourceIdRawTag);
        if (sourceIdValues.Count == 0 && !string.IsNullOrWhiteSpace(context.SourceTrack.TrackId))
        {
            sourceIdValues.Add(context.SourceTrack.TrackId);
        }
        if (sourceIdValues.Count > 0)
        {
            SetRawIfAllowed(tagWriteContext, SourceTag, SourceIdRawTag, sourceIdValues);
        }
    }

    private static void WriteRatingTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(RatingTag) || !context.EffectiveTagSettings.Rating)
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, RatingTag, RatingRawTag);
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, RatingTag, RatingRawTag, values);
    }

    private static void WriteLanguageTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(LanguageTag))
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, LanguageTag, LanguageRawTag);
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, LanguageTag, LanguageRawTag, values);
    }

    private static void WriteExplicitTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ExplicitTag) || !context.SourceTrack.Explicit.HasValue)
        {
            return;
        }

        SetRaw(
            tagWriteContext,
            ItunesAdvisoryTag,
            SupportedTag.Explicit,
            new List<string> { context.SourceTrack.Explicit.Value ? "1" : "0" });
    }

    private static void WriteOtherTags(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(OtherTagsTag)
            && !context.EnabledTags.Contains(ReleaseTypeTag))
        {
            return;
        }

        if (context.EnabledTags.Contains(OtherTagsTag))
        {
            foreach (var rawName in context.SourceTrack.RawTagsToRemove)
            {
                RemoveRawTagValues(tagWriteContext, rawName);
            }
        }

        if (context.SourceTrack.Other.Count == 0)
        {
            return;
        }

        foreach (var kvp in context.SourceTrack.Other)
        {
            if (IsNonPersistedOtherRawKey(kvp.Key))
            {
                continue;
            }

            var isReleaseType = kvp.Key.Equals(ReleaseTypeRawTag, StringComparison.OrdinalIgnoreCase);
            if (isReleaseType && !HasReleaseTypeTagEnabled(context.EnabledTags))
            {
                continue;
            }

            if (!isReleaseType && !context.EnabledTags.Contains(OtherTagsTag))
            {
                continue;
            }

            if (!ShouldAllowLyricsOtherTagKey(
                    kvp.Key,
                    context.AllowsLyricsBySettings,
                    context.AllowsSyncedType,
                    context.AllowsUnsyncedType,
                    context.AllowsTtmlByFormat,
                    !context.ShouldSkipEmbeddedLyrics))
            {
                continue;
            }

            SetRaw(tagWriteContext, kvp.Key, isReleaseType ? SupportedTag.ReleaseType : SupportedTag.OtherTags, kvp.Value.ToList());
        }
    }

    private static void WriteMetaTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(MetaTagsTag))
        {
            return;
        }

        SetRaw(tagWriteContext, TaggedDateTag, SupportedTag.MetaTags, new List<string> { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}_AT" });
    }

    private static string ResolveTemplateMaterializationDestination(
        string sourcePath,
        string destinationPath,
        DeezSpoTagSettings settings)
    {
        if (!IOFile.Exists(destinationPath) || ShouldOverwriteMaterializedFile(settings))
        {
            return destinationPath;
        }

        var directory = Path.GetDirectoryName(destinationPath) ?? "";
        var filename = Path.GetFileNameWithoutExtension(destinationPath);
        var extension = Path.GetExtension(destinationPath);
        for (var index = 1; index < 10_000; index++)
        {
            var candidate = Path.Join(directory, $"{filename} ({index}){extension}");
            if (!IOFile.Exists(candidate) && !PathsReferToSameFile(sourcePath, candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not allocate a unique manual enrichment destination for {destinationPath}.");
    }

    private static string ResolveStylesTagName(AutoTagRunnerConfig config, string extension)
    {
        if (config.StylesCustomTag == null)
        {
            return StyleUpperTag;
        }

        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(config.StylesCustomTag.Id3) ? StyleUpperTag : config.StylesCustomTag.Id3;
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(config.StylesCustomTag.Vorbis) ? StyleUpperTag : config.StylesCustomTag.Vorbis;
        }

        if (IsMp4Family(extension))
        {
            return string.IsNullOrWhiteSpace(config.StylesCustomTag.Mp4) ? StyleUpperTag : config.StylesCustomTag.Mp4;
        }

        return StyleUpperTag;
    }

    private static string ResolveSeparatorForFormat(AutoTagRunnerConfig config, string extension)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return config.Separators?.Id3 ?? ", ";
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            return config.Separators?.Vorbis ?? "";
        }

        if (IsMp4Family(extension))
        {
            return config.Separators?.Mp4 ?? ", ";
        }

        return ", ";
    }

    private static string FormatAudioFeature(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string ResolveFormatName(string extension)
    {
        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase)) return VorbisFormat;
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)) return "id3";
        return "mp4";
    }

    private static void WriteDate(
        TagLib.File file,
        string extension,
        string kind,
        DateTime date,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        bool useNullSeparator,
        bool forceOverwrite = false)
    {
        var useYearOnly = IsYearOnlyDateFormat(config.Technical?.DateFormat);
        var payload = new DateWritePayload(
            Date: date,
            UseYearOnly: useYearOnly,
            Year: date.Year.ToString(CultureInfo.InvariantCulture),
            DateString: useYearOnly
                ? date.Year.ToString(CultureInfo.InvariantCulture)
                : date.ToString(IsoDateFormat, CultureInfo.InvariantCulture));

        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            WriteId3Date(file, kind, tag, config, payload, useNullSeparator, forceOverwrite);
            return;
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            WriteVorbisDate(file, kind, tag, config, payload.DateString, forceOverwrite);
            return;
        }

        WriteMp4Date(file, extension, kind, tag, config, payload.DateString, forceOverwrite);
    }

    private static void WriteId3Date(
        TagLib.File file,
        string kind,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        DateWritePayload payload,
        bool useNullSeparator,
        bool forceOverwrite)
    {
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
        if (kind == ReleaseDateTag)
        {
            if (!forceOverwrite && ShouldSkipId3ReleaseDate(config, tag, id3, payload.UseYearOnly))
            {
                return;
            }

            if (config.Id3v24)
            {
                SetId3Raw(id3, "TDRC", new List<string> { payload.DateString }, ", ", useNullSeparator);
                return;
            }

            SetId3Raw(id3, "TYER", new List<string> { payload.Year }, ", ", useNullSeparator);
            if (!payload.UseYearOnly)
            {
                SetId3Raw(id3, "TDAT", new List<string> { payload.Date.ToString("ddMM", CultureInfo.InvariantCulture) }, ", ", useNullSeparator);
            }
            return;
        }

        if (!ShouldOverwriteTag(config, tag) && TagRawProbe.HasId3Raw(id3, "TDRL"))
        {
            return;
        }

        SetId3Raw(id3, "TDRL", new List<string> { payload.DateString }, ", ", useNullSeparator);
    }

    private static void WriteVorbisDate(
        TagLib.File file,
        string kind,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        string dateString,
        bool forceOverwrite)
    {
        var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
        var field = kind == ReleaseDateTag ? "DATE" : OriginalDateUpperTag;
        if (!forceOverwrite && !ShouldOverwriteTag(config, tag) && TagRawProbe.HasVorbisRaw(vorbis, field))
        {
            return;
        }

        SetVorbisRaw(vorbis, field, new List<string> { dateString }, "");
    }

    private static void WriteMp4Date(
        TagLib.File file,
        string extension,
        string kind,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        string dateString,
        bool forceOverwrite)
    {
        if (!IsMp4Family(extension))
        {
            return;
        }

        if (kind == ReleaseDateTag)
        {
            if (!forceOverwrite && !ShouldOverwriteTag(config, tag)
                && (Mp4TagHelper.HasRaw(file, "©day")
                    || Mp4TagHelper.HasRaw(file, "DATE")))
            {
                return;
            }

            Mp4TagHelper.SetDate(file, dateString);
            var appleRelease = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
            TrySetAppleDashBox(appleRelease, "DATE", new[] { dateString });
            return;
        }

        if (kind != PublishDateTag)
        {
            return;
        }

        if (!ShouldOverwriteTag(config, tag) && Mp4TagHelper.HasRaw(file, OriginalDateUpperTag))
        {
            return;
        }

        var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
        TrySetAppleDashBox(apple, "ORIGINALDATE", new[] { dateString });
    }

    private static void WriteId3TrackNumber(
        TagLib.File file,
        string numberText,
        int? total,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        bool useNullSeparator,
        bool isDisc)
    {
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
        if (!ShouldOverwriteTag(config, tag) && (isDisc ? id3.Disc > 0 : id3.Track > 0))
        {
            return;
        }

        var value = total.HasValue ? $"{numberText}/{total.Value}" : numberText;
        var frame = TagLib.Id3v2.TextInformationFrame.Get(id3, isDisc ? "TPOS" : "TRCK", true);
        if (useNullSeparator)
        {
            frame.TextEncoding = TagLib.StringType.UTF16;
        }
        frame.Text = new[] { value };
    }

    private static void WriteVorbisTrackNumber(
        TagLib.File file,
        string numberText,
        int? total,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        bool isDisc)
    {
        var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
        var field = isDisc ? "DISCNUMBER" : TrackNumberUpperTag;
        if (!ShouldOverwriteTag(config, tag) && TagRawProbe.HasVorbisRaw(vorbis, field))
        {
            return;
        }

        SetVorbisRaw(vorbis, field, new List<string> { numberText }, "");
        if (isDisc
            && total.HasValue
            && (ShouldOverwriteTag(config, SupportedTag.DiscTotal) || !TagRawProbe.HasVorbisRaw(vorbis, DiscTotalRawTag)))
        {
            SetVorbisRaw(vorbis, DiscTotalRawTag, new List<string> { total.Value.ToString(CultureInfo.InvariantCulture) }, "");
        }
        if (!isDisc
            && total.HasValue
            && (ShouldOverwriteTag(config, SupportedTag.TrackTotal) || !TagRawProbe.HasVorbisRaw(vorbis, TrackTotalRawTag)))
        {
            SetVorbisRaw(vorbis, TrackTotalRawTag, new List<string> { total.Value.ToString(CultureInfo.InvariantCulture) }, "");
        }
    }

    private static void WriteMp4TrackNumber(
        TagLib.File file,
        int number,
        int? total,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        bool isDisc,
        string extension)
    {
        if (!IsMp4Family(extension))
        {
            return;
        }

        if (!ShouldOverwriteTag(config, tag) && (isDisc ? file.Tag.Disc > 0 : file.Tag.Track > 0))
        {
            return;
        }

        if (!isDisc)
        {
            file.Tag.Track = (uint)number;
            if (total.HasValue
                && (ShouldOverwriteTag(config, SupportedTag.TrackTotal) || file.Tag.TrackCount == 0))
            {
                file.Tag.TrackCount = (uint)total.Value;
            }
            return;
        }

        file.Tag.Disc = (uint)number;
        if (total.HasValue)
        {
            file.Tag.DiscCount = (uint)total.Value;
        }
    }

    internal static string CapitalizeGenre(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var chars = words[i].ToCharArray();
            if (chars.Length == 0)
            {
                continue;
            }

            // Capitalize word starts without flattening the remaining casing:
            // R&B, HipHop and EDM must survive capitalization untouched.
            chars[0] = char.ToUpperInvariant(chars[0]);
            for (var c = 1; c < chars.Length; c++)
            {
                if (chars[c - 1] == '&' && char.IsLetter(chars[c]))
                {
                    chars[c] = char.ToUpperInvariant(chars[c]);
                }
            }

            words[i] = new string(chars);
        }

        return string.Join(' ', words);
    }

    private static bool IsGenreRawTag(string rawName)
    {
        var normalized = rawName.Trim();
        var mp4Normalized = Mp4RawTagNameNormalizer.Normalize(normalized);
        return normalized.Equals("TCON", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(Mp4GenreTag, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("©gen", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals($"----:com.apple.iTunes:{Mp4GenreTag}", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals($"iTunes:{Mp4GenreTag}", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals($"com.apple.iTunes:{Mp4GenreTag}", StringComparison.OrdinalIgnoreCase)
            || mp4Normalized.Equals(Mp4GenreTag, StringComparison.OrdinalIgnoreCase)
            || mp4Normalized.Equals("©gen", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Keeps the file's existing genre order when the sanitized values are the same
    /// set (case-insensitive) as the tags already on the file. Platform payloads
    /// reorder genres between runs, and with genre in overwriteTags every run rewrote
    /// them in that platform's order — producing order-only diffs (HipHop, Rap →
    /// Rap, HipHop) with no content change. The returned list keeps the file's order
    /// while adopting the sanitized values' casing; any real set change keeps the
    /// platform order.
    /// </summary>
    internal static List<string> PreserveGenreOrderWhenSetEqual(List<string> values, IEnumerable<string?>? existingGenres)
    {
        if (values.Count == 0 || existingGenres == null)
        {
            return values;
        }

        var existingList = existingGenres
            .Select(value => value?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .ToList();
        if (existingList.Count != values.Count)
        {
            return values;
        }

        var sanitizedByNormalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var key = value?.Trim() ?? string.Empty;
            if (key.Length == 0 || !sanitizedByNormalized.TryAdd(key, value ?? string.Empty))
            {
                return values;
            }
        }

        var ordered = new List<string>(values.Count);
        foreach (var existing in existingList)
        {
            if (!sanitizedByNormalized.TryGetValue(existing, out var sanitized))
            {
                return values;
            }

            ordered.Add(sanitized);
        }

        return ordered;
    }

    private static string ResolveAtlAdditionalValue(Dictionary<string, string> additional, string key)
    {
        return additional.TryGetValue(key, out var value)
            ? value
            : string.Empty;
    }

    private static List<string> ResolveOtherValues(AutoTagTrack track, params string[] keys)
    {
        var values = new List<string>();
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key) || !track.Other.TryGetValue(key, out var keyValues))
            {
                continue;
            }

            values = values
                .Concat(keyValues.SelectMany(SplitCompositeRawValues))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return values;
    }

    private static List<string> ResolveFirstClassOrOtherValues(string? firstClassValue, AutoTagTrack track, params string[] keys)
    {
        var values = ResolveOtherValues(track, keys);
        if (!string.IsNullOrWhiteSpace(firstClassValue))
        {
            values.Insert(0, firstClassValue.Trim());
        }

        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int? ResolveFirstPositiveInt(AutoTagTrack track, params string[] keys)
    {
        return ResolveOtherValues(track, keys)
            .Select(raw => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : (int?)null)
            .FirstOrDefault(parsed => parsed > 0);
    }

    private static string ResolveComposerRawName(string extension)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return "TCOM";
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            return ComposerUpperTag;
        }

        return "©wrt";
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
}
