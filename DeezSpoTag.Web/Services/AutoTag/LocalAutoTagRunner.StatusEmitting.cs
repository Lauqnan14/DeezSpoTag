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

    private static void HandleRejectedManualRelease(
        AutoTagFileRunContext context,
        AutoTagAudioInfo source,
        AutoTagTrack candidate,
        string reason,
        bool usedShazam,
        ProviderTagPlan tagPlan,
        AutoTagMatchResult match)
    {
        if (IsLastPlatform(context) && !WasTaggedByAnyPlatform(context))
        {
            EmitReviewStatus(
                context,
                reason,
                usedShazam,
                AutoTagReviewMetadata.FromMatch(source, candidate),
                "rejected",
                tagPlan,
                match);
            context.Plan.ReviewedFiles.Add(context.File);
            return;
        }

        EmitSkippedStatus(context, reason, usedShazam, "rejected", tagPlan, match);
    }

    private static void EmitSkippedStatus(
        AutoTagFileRunContext context,
        string message,
        bool usedShazam = false,
        string? outcome = null,
        ProviderTagPlan? tagPlan = null,
        AutoTagMatchResult? match = null)
    {
        EmitStatus(context, "skipped", message, null, usedShazam, outcome: outcome, tagPlan: tagPlan, match: match);
    }

    private static void EmitErrorStatus(
        AutoTagFileRunContext context,
        string message,
        bool usedShazam,
        string? outcome = null,
        ProviderTagPlan? tagPlan = null,
        AutoTagMatchResult? match = null)
    {
        EmitStatus(context, "error", message, null, usedShazam, outcome: outcome, tagPlan: tagPlan, match: match);
    }

    private static void EmitReviewStatus(
        AutoTagFileRunContext context,
        string message,
        bool usedShazam,
        AutoTagReviewMetadata? review,
        string? outcome = null,
        ProviderTagPlan? tagPlan = null,
        AutoTagMatchResult? match = null)
    {
        EmitStatus(context, "review", message, null, usedShazam, review, outcome, tagPlan, match);
    }

    private static void EmitTaggingStatus(AutoTagFileRunContext context, double? accuracy, bool usedShazam)
    {
        EmitStatus(context, "tagging", null, accuracy, usedShazam);
    }

    private static void EmitTaggedStatus(
        AutoTagFileRunContext context,
        double? accuracy,
        bool usedShazam,
        string? outcome = null,
        ProviderTagPlan? tagPlan = null,
        AutoTagMatchResult? match = null,
        IReadOnlyCollection<SupportedTag>? returnedTags = null,
        IReadOnlyCollection<SupportedTag>? writtenTags = null,
        IReadOnlyCollection<SupportedTag>? missingTags = null)
    {
        EmitStatus(
            context,
            "tagged",
            outcome == "matched_no_changes" ? "provider matched but supplied no new eligible values" : null,
            accuracy,
            usedShazam,
            outcome: outcome,
            tagPlan: tagPlan,
            match: match,
            returnedTags: returnedTags,
            writtenTags: writtenTags,
            missingTags: missingTags);
    }

    private static void EmitStatus(
        AutoTagFileRunContext context,
        string status,
        string? message,
        double? accuracy,
        bool usedShazam,
        AutoTagReviewMetadata? review = null,
        string? outcome = null,
        ProviderTagPlan? tagPlan = null,
        AutoTagMatchResult? match = null,
        IReadOnlyCollection<SupportedTag>? returnedTags = null,
        IReadOnlyCollection<SupportedTag>? writtenTags = null,
        IReadOnlyCollection<SupportedTag>? missingTags = null)
    {
        var isLyricsPlatform = string.Equals(context.Platform, LyricsPlatform, StringComparison.OrdinalIgnoreCase);
        context.StatusCallback(new TaggingStatusWrap
        {
            Platform = context.Platform,
            Progress = context.Progress,
            PlatformIndex = context.PlatformIndex,
            PlatformCount = context.Plan.PlatformCount,
            FileIndex = context.FileIndex,
            FileCount = context.Plan.FileCount,
            NextPlatformIndex = context.NextPlatformIndex,
            NextFileIndex = context.NextFileIndex,
            BatchNumber = context.BatchNumber,
            BatchCount = context.BatchCount,
            BatchSize = context.BatchSize,
            BatchProcessed = context.BatchProcessed,
            Status = new TaggingStatus
            {
                Status = status,
                Path = context.File,
                Message = message,
                Accuracy = accuracy,
                UsedShazam = usedShazam,
                Outcome = outcome,
                RecognitionStrategy = ResolveRecognitionStrategy(match),
                RequestedTags = (tagPlan?.Requested.AsEnumerable() ?? Enumerable.Empty<SupportedTag>()).Select(ToTagKey).OrderBy(tag => tag, StringComparer.Ordinal).ToList(),
                ReturnedTags = (returnedTags ?? Array.Empty<SupportedTag>()).Select(ToTagKey).OrderBy(tag => tag, StringComparer.Ordinal).ToList(),
                WrittenTags = (writtenTags ?? Array.Empty<SupportedTag>()).Select(ToTagKey).OrderBy(tag => tag, StringComparer.Ordinal).ToList(),
                RetainedTags = (tagPlan?.Retained.AsEnumerable() ?? Enumerable.Empty<SupportedTag>()).Select(ToTagKey).OrderBy(tag => tag, StringComparer.Ordinal).ToList(),
                MissingTags = (missingTags?.AsEnumerable() ?? tagPlan?.Eligible.AsEnumerable() ?? Enumerable.Empty<SupportedTag>()).Select(ToTagKey).OrderBy(tag => tag, StringComparer.Ordinal).ToList(),
                ReviewReason = review?.Reason ?? message,
                LyricsBadges = isLyricsPlatform
                    ? ResolveLyricsTimingBadges(context.File, context.Plan.Config, context.Plan.Settings)
                    : new List<string>(),
                ArtworkBadges = ResolveAnimatedArtworkBadges(context.File, context.Plan.Settings),
                LyricsCoverUrl = isLyricsPlatform ? ResolveLyricsRowCoverUrl(context.File) : null,
                SourceTitle = isLyricsPlatform
                    ? (match?.Track.Title ?? review?.SourceTitle)
                    : review?.SourceTitle,
                SourceArtist = isLyricsPlatform
                    ? (match?.Track.Artists.FirstOrDefault() ?? review?.SourceArtist)
                    : review?.SourceArtist,
                SourceIsrc = review?.SourceIsrc,
                SourceDurationSeconds = review?.SourceDurationSeconds,
                CandidateTitle = review?.CandidateTitle,
                CandidateArtist = review?.CandidateArtist,
                CandidateIsrc = review?.CandidateIsrc,
                CandidateDurationSeconds = review?.CandidateDurationSeconds
            }
        });
    }
}
