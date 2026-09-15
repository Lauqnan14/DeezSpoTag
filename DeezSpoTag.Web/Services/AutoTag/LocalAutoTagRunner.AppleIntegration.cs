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

    private static void PreserveAtmosFileIsrc(string filePath, AutoTagAudioInfo source, AutoTagTrack? incoming)
        => PreserveAtmosFileIsrc(IsLocalAtmosFile(filePath), source, incoming);

    private static void PreserveAtmosFileIsrc(bool localFileIsAtmos, AutoTagAudioInfo source, AutoTagTrack? incoming)
    {
        if (incoming == null || !localFileIsAtmos)
        {
            return;
        }

        incoming.Isrc = source.Isrc;
    }

    private async Task PopulateAppleExtrasAsync(
        string platform,
        string filePath,
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        CancellationToken token,
        ProviderIdentityPayload? confirmedIdentity = null)
    {
        var itunesConfig = LoadConfig(config.Custom, ItunesPlatform, new ItunesMatchConfig());
        var saveAnimatedArtwork = itunesConfig.AnimatedArtwork ?? settings.SaveAnimatedArtwork;
        var wantsAnimatedArtwork = saveAnimatedArtwork
            && WantsArtworkFromSettings(config, settings);
        var wantsAppleLyrics = ShouldRequestAnyLyrics(config, settings);
        var wantsCatalogMetadata = string.Equals(platform, ItunesPlatform, StringComparison.OrdinalIgnoreCase)
            && HasAnyTags(
                config,
                GenreTag,
                IsrcTag,
                LabelTag,
                CopyrightTag,
                ComposerTag,
                InvolvedPeopleTag,
                OtherTagsTag,
                ExplicitTag);
        if (!wantsAnimatedArtwork && !wantsAppleLyrics && !wantsCatalogMetadata)
        {
            return;
        }

        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront)
            ? "us"
            : settings.AppleMusic.Storefront;
        var appleIdentity = await ResolveAppleIdentityForExtrasAsync(track, storefront, settings, token, confirmedIdentity);

        if (wantsCatalogMetadata && !string.IsNullOrWhiteSpace(appleIdentity?.AppleId))
        {
            await PopulateAppleCatalogMetadataAsync(
                track,
                config,
                IsLocalAtmosFile(filePath),
                appleIdentity.AppleId,
                storefront,
                settings.AppleMusic?.MediaUserToken,
                token);
        }

        if (wantsAppleLyrics)
        {
            await PopulatePlatformLyricsAsync(
                AppleProvider,
                filePath,
                track,
                config,
                settings,
                token,
                appleIdentity?.AppleId);
        }

        if (!wantsAnimatedArtwork)
        {
            return;
        }

        var outputDir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return;
        }

        var maxResolution = settings.Video?.AppleMusicVideoMaxResolution ?? 2160;
        var baseFileName = BuildAlbumArtworkBaseFileName(track, settings);

        await TryPopulateAppleAnimatedArtworkAsync(
            track,
            outputDir,
            storefront,
            maxResolution,
            baseFileName,
            appleIdentity,
            settings,
            token);
    }

    private async Task PopulateAppleCatalogMetadataAsync(
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        bool localFileIsAtmos,
        string appleTrackId,
        string storefront,
        string? mediaUserToken,
        CancellationToken token)
    {
        using var payload = await _appleMusicCatalogService.GetSongAsync(
            appleTrackId,
            storefront,
            "en-US",
            token,
            mediaUserToken);
        if (!payload.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() == 0
            || !data[0].TryGetProperty("attributes", out var attributes))
        {
            return;
        }

        ApplyAppleCatalogMetadata(track, config, attributes, localFileIsAtmos);
    }

    private static void ApplyAppleCatalogMetadata(
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        JsonElement attributes,
        bool localFileIsAtmos)
    {
        if (HasAnyTags(config, GenreTag)
            && attributes.TryGetProperty("genreNames", out var genreNames)
            && genreNames.ValueKind == JsonValueKind.Array)
        {
            var genres = genreNames.EnumerateArray()
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Where(value => !string.Equals(value, "Music", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (genres.Count > 0)
            {
                track.Genres = track.Genres
                    .Concat(genres)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        var isrc = TryGetJsonString(attributes, "isrc");
        if (HasAnyTags(config, IsrcTag) && !string.IsNullOrWhiteSpace(isrc) && !localFileIsAtmos)
        {
            track.Isrc = isrc;
        }

        var recordLabel = TryGetJsonString(attributes, "recordLabel");
        if (HasAnyTags(config, LabelTag) && !string.IsNullOrWhiteSpace(recordLabel))
        {
            track.Label = recordLabel;
        }

        var composer = TryGetJsonString(attributes, "composerName");
        if (HasAnyTags(config, ComposerTag) && !string.IsNullOrWhiteSpace(composer))
        {
            track.Other[ComposerTag] = SplitCompositeRawValues(composer).ToList();
        }
        if (HasAnyTags(config, InvolvedPeopleTag) && !string.IsNullOrWhiteSpace(composer))
        {
            track.Other[InvolvedPeopleTag] = [.. SplitCompositeRawValues(composer).Select(name => $"Composer: {name}")];
        }

        var copyright = TryGetJsonString(attributes, "copyright");
        if (HasAnyTags(config, CopyrightTag) && !string.IsNullOrWhiteSpace(copyright))
        {
            track.Other[CopyrightTag] = [copyright];
        }

        if (HasAnyTags(config, ExplicitTag)
            && attributes.TryGetProperty("contentRating", out var rating)
            && rating.ValueKind == JsonValueKind.String)
        {
            track.Explicit = string.Equals(rating.GetString(), "explicit", StringComparison.OrdinalIgnoreCase);
        }

        if (!HasAnyTags(config, OtherTagsTag))
        {
            return;
        }

        track.RawTagsToRemove.Add("APPLE_AUDIO_TRAITS");
        track.RawTagsToRemove.Add("APPLE_IS_ATMOS");
        if (!localFileIsAtmos)
        {
            return;
        }

        track.Other["APPLE_AUDIO_TRAITS"] = ["atmos"];
        track.Other["APPLE_IS_ATMOS"] = ["1"];
    }

    private static bool IsLocalAtmosFile(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var codec = file.Properties.Codecs == null
                ? string.Empty
                : string.Join(' ', file.Properties.Codecs.Select(value => value.Description ?? string.Empty));
            return AudioVariantResolver.IsAtmosVariant(
                file.Properties.AudioChannels,
                codec,
                Path.GetExtension(filePath),
                filePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task TryPopulateAppleAnimatedArtworkAsync(
        AutoTagTrack track,
        string outputDir,
        string storefront,
        int maxResolution,
        string baseFileName,
        TrackIdentityResolution? appleIdentity,
        DeezSpoTagSettings settings,
        CancellationToken token)
    {
        var artist = track.Artists.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(appleIdentity?.AppleId))
        {
            return;
        }

        try
        {
            var animatedResult = await AppleQueueHelpers.SaveAnimatedArtworkAsync(
                _appleMusicCatalogService,
                _httpClientFactory,
                new AppleQueueHelpers.AnimatedArtworkSaveRequest
                {
                    AppleId = appleIdentity?.AppleId,
                    Artist = appleIdentity?.AppleArtistName ?? artist,
                    Album = appleIdentity?.AppleAlbumName ?? track.Album,
                    SquareFileName = settings.AnimatedArtworkSquareFileName,
                    TallFileName = settings.AnimatedArtworkTallFileName,
                    Storefront = storefront,
                    MaxResolution = maxResolution,
                    OutputDir = outputDir,
                    Logger = _logger,
                    CollectionType = string.IsNullOrWhiteSpace(appleIdentity?.AppleAlbumId) ? null : "album",
                    CollectionId = appleIdentity?.AppleAlbumId,
                    OutputFormats = AppleQueueHelpers.ResolveAnimatedArtworkFormats(settings),
                    MaxSizeMb = AppleQueueHelpers.ResolveAnimatedArtworkMaxSizeMb(settings),
                    SaveSquareVariant = settings.SaveAnimatedArtwork && settings.SaveSquareAnimatedArtwork,
                    SaveTallVariant = settings.SaveAnimatedArtwork && settings.SaveTallAnimatedArtwork
                },
                token);

            if (animatedResult.Paths.Count > 0)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("AutoTag Apple animated artwork saved for {Title} in {OutputDir}", SanitizeLogValue(track.Title), SanitizeLogValue(outputDir));
                }
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "AutoTag Apple animated artwork {Status} for {Title}: {Message}",
                        animatedResult.Status,
                        SanitizeLogValue(track.Title),
                        animatedResult.Message);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple animated artwork resolution failed for {Title}.", SanitizeLogValue(track.Title));
            }
        }
    }

    private async Task<TrackIdentityResolution?> ResolveAppleIdentityForExtrasAsync(
        AutoTagTrack track,
        string storefront,
        DeezSpoTagSettings settings,
        CancellationToken token,
        ProviderIdentityPayload? confirmedIdentity = null)
    {
        try
        {
            var artist = track.Artists.FirstOrDefault();
            // Only an Apple-confirmed payload may preseed the Apple identity lookup: raw
            // tags and mutated track values are never trusted provenance.
            var appleConfirmed = confirmedIdentity is { IsNativeProviderResult: true }
                && AutoTagIdentityTags.NormalizeProviderId(confirmedIdentity.ProviderId) == "itunes"
                        ? confirmedIdentity
                        : null;
            var persistedAppleId = appleConfirmed?.TrackId;
            var identity = await _trackIdentityResolver.ResolveAsync(
                new TrackIdentityResolutionRequest(
                    SourcePlatform: null,
                    SourceUrl: appleConfirmed?.Url,
                    Title: track.Title,
                    Artist: artist,
                    Album: track.Album,
                    Isrc: track.Isrc,
                    DurationMs: track.Duration.HasValue ? (int)Math.Round(track.Duration.Value.TotalMilliseconds) : null,
                    AppleId: persistedAppleId,
                    TargetPlatforms: new[] { "apple" },
                    Storefront: storefront,
                    Language: "en-US",
                    MediaUserToken: settings.AppleMusic?.MediaUserToken),
                token);
            return string.IsNullOrWhiteSpace(identity.AppleId) ? null : identity;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Central Apple identity lookup failed for animated artwork {Isrc}.", SanitizeLogValue(track.Isrc));
            }
            return null;
        }
    }

    private static void ApplyAppleCustomTags(TagLib.Mpeg4.AppleTag tag, List<CustomTagWrite> writes, AutoTagRunnerConfig config, string separator, HashSet<string> enabledTags, HashSet<SupportedTag> attemptedTags)
    {
        foreach (var write in writes)
        {
            if (!enabledTags.Contains(write.TagKey) || write.Values.Count == 0)
            {
                continue;
            }

            var rawName = Mp4RawTagNameNormalizer.Normalize(write.RawTagName);
            if (!ShouldOverwriteTag(config, write.SupportedTag) && TagRawProbe.HasAppleDashBox(tag, rawName))
            {
                attemptedTags.Add(write.SupportedTag);
                continue;
            }

            TrySetAppleDashBox(tag, rawName, ApplySeparator(write.Values, separator));
            attemptedTags.Add(write.SupportedTag);
        }
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
}
