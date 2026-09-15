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

    private static (int PlatformIndex, int FileIndex) ResolveResumeStartIndices(
        AutoTagRunPlan plan,
        AutoTagResumeCursor? resumeCursor,
        bool preferPathAnchor = false)
    {
        if (plan.PlatformCount == 0 || plan.FileCount == 0 || resumeCursor == null)
        {
            return (0, 0);
        }

        var platformIndex = Math.Clamp(resumeCursor.PlatformIndex, 0, plan.PlatformCount - 1);
        var fileIndex = Math.Clamp(resumeCursor.FileIndex, 0, plan.FileCount);
        if (preferPathAnchor
            && !string.IsNullOrWhiteSpace(resumeCursor.LastPath))
        {
            var anchoredFileIndex = plan.Files.FindIndex(file =>
                string.Equals(file, resumeCursor.LastPath, StringComparison.OrdinalIgnoreCase));
            if (anchoredFileIndex >= 0)
            {
                fileIndex = anchoredFileIndex + 1;
            }
        }

        if (fileIndex >= plan.FileCount)
        {
            fileIndex = 0;
            platformIndex += 1;
        }

        if (platformIndex >= plan.PlatformCount)
        {
            return (plan.PlatformCount, 0);
        }

        return (platformIndex, fileIndex);
    }

    private static string NormalizeOrderPath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return path.Trim();
        }
    }

    private static readonly SupportedTag[] ProviderIdentitySupportedTags =
    [
        SupportedTag.TrackId,
        SupportedTag.AlbumId,
        SupportedTag.ReleaseId,
        SupportedTag.ArtistId,
        SupportedTag.AlbumArtistId,
        SupportedTag.URL
    ];

    private static HashSet<SupportedTag> VerifyPersistedTags(
        string filePath,
        AutoTagRunnerConfig config,
        string platformId,
        AutoTagTrack track,
        ProviderIdentityPayload? providerIdentity,
        IEnumerable<SupportedTag> expectedTags)
    {
        var missing = new HashSet<SupportedTag>();
        var expected = expectedTags.ToHashSet();
        using var file = TagLib.File.Create(filePath);
        var extension = Path.GetExtension(filePath);
        if (expected.Contains(SupportedTag.Artist)
            && BuildConfiguredTagSet(config.Tags).Contains(ArtistsTag)
            && !string.Equals(
                config.Technical?.MultiArtistSeparator ?? MultiArtistSeparatorDefault,
                MultiArtistSeparatorDefault,
                StringComparison.OrdinalIgnoreCase)
            && track.Artists.Count > 0
            && !HasRawTag(file, extension, "ARTISTS"))
        {
            missing.Add(SupportedTag.Artist);
        }

        foreach (var tag in expected)
        {
            var persisted = tag switch
            {
                SupportedTag.OtherTags => VerifyOtherTagsPersisted(file, extension, track),
                SupportedTag.TtmlLyrics => IOFile.Exists(Path.ChangeExtension(filePath, TtmlExtension)),
                SupportedTag.ReleaseGroupId => HasTag(file, extension, tag, config, platformId)
                    && VerifyMusicBrainzAlbumIdentityAlias(file, extension, track.ReleaseGroupId, "MUSICBRAINZ_RELEASEGROUPID"),
                _ when ProviderIdentitySupportedTags.Contains(tag) =>
                    VerifyProviderIdentityFieldPersisted(filePath, providerIdentity, tag),
                _ => HasTag(file, extension, tag, config, platformId)
            };
            if (!persisted)
            {
                missing.Add(tag);
            }
        }

        return missing;
    }

    /// <summary>
    /// Verifies the exact provider+field family through the same backend that wrote it.
    /// Presence of any alias is never accepted as success: the canonical write name must
    /// read back the immutable payload value.
    /// </summary>
    private static bool VerifyProviderIdentityFieldPersisted(
        string filePath,
        ProviderIdentityPayload? providerIdentity,
        SupportedTag supportedTag)
    {
        if (providerIdentity is not { IsNativeProviderResult: true }
            || !TryMapProviderIdentityField(supportedTag, out var field))
        {
            return false;
        }

        var expectedValue = providerIdentity.ValueFor(field);
        if (string.IsNullOrWhiteSpace(expectedValue))
        {
            return false;
        }

        var family = AutoTagIdentityTags.ResolveFamily(providerIdentity.ProviderId, field);
        var actual = family.WriteNames
            .Select(name => ReadRawIdentityValue(filePath, name))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return string.Equals(actual, expectedValue, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryMapProviderIdentityField(SupportedTag supportedTag, out ProviderIdentityField field)
    {
        switch (supportedTag)
        {
            case SupportedTag.TrackId:
                field = ProviderIdentityField.TrackId;
                return true;
            case SupportedTag.AlbumId:
                field = ProviderIdentityField.AlbumId;
                return true;
            case SupportedTag.ReleaseId:
                field = ProviderIdentityField.ReleaseId;
                return true;
            case SupportedTag.ArtistId:
                field = ProviderIdentityField.ArtistId;
                return true;
            case SupportedTag.AlbumArtistId:
                field = ProviderIdentityField.AlbumArtistId;
                return true;
            case SupportedTag.URL:
                field = ProviderIdentityField.Url;
                return true;
            default:
                field = default;
                return false;
        }
    }

    private static bool VerifyMusicBrainzAlbumIdentityAlias(
        TagLib.File file,
        string extension,
        string? expectedValue,
        string rawName)
    {
        var musicBrainzId = ToMusicBrainzShapedId(expectedValue);
        return musicBrainzId == null
            || ReadRawTagValuesAny(file, extension, rawName)
                .Any(value => string.Equals(value, musicBrainzId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool VerifyOtherTagsPersisted(TagLib.File file, string extension, AutoTagTrack track)
    {
        var expectedRawTags = track.Other
            .Where(pair => pair.Value.Count > 0)
            .Where(pair => ShouldPersistOtherRawKey(pair.Key))
            .Select(pair => pair.Key)
            .ToList();
        return expectedRawTags.Count == 0
            || expectedRawTags.All(rawTag => HasRawTag(file, extension, rawTag));
    }

    private static bool TryHandlePreSkippedFile(AutoTagFileRunContext context)
    {
        if (context.Plan.PreSkippedFiles.Contains(context.File))
        {
            if (context.PlatformIndex == 0)
            {
                EmitSkippedStatus(context, "already tagged");
            }

            return true;
        }

        if (string.IsNullOrWhiteSpace(context.File) || !IOFile.Exists(context.File))
        {
            var path = context.File ?? string.Empty;
            if (!context.Plan.PreSkippedFiles.Contains(path)
                && !context.Plan.ReviewedFiles.Contains(path))
            {
                EmitSkippedStatus(context, "file is no longer at this path");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    context.Plan.PreSkippedFiles.Add(path);
                }
            }

            return true;
        }

        return false;
    }

    private static bool IsProviderNotConfigured(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is not InvalidOperationException)
            {
                continue;
            }

            var message = current.Message;
            if (message.Contains("not configured", StringComparison.OrdinalIgnoreCase)
                || message.Contains("must be connected", StringComparison.OrdinalIgnoreCase)
                || message.Contains("credentials are required", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task RunBoundedOptionalStepAsync(
        AutoTagFileRunContext context,
        string stepName,
        TimeSpan timeout,
        Func<CancellationToken, Task> action)
    {
        context.LogCallback($"onetagger_autotag: {context.Platform} {stepName} starting");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(context.Token);
        var stepTask = action(timeoutSource.Token);
        try
        {
            await stepTask.WaitAsync(timeout, context.Token);
            context.LogCallback($"onetagger_autotag: {context.Platform} {stepName} completed");
        }
        catch (TimeoutException)
        {
            timeoutSource.Cancel();
            ObserveBackgroundTask(stepTask);
            context.LogCallback(
                $"onetagger_autotag: {context.Platform} {stepName} timed out after {timeout.TotalSeconds:0}s; continuing");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Optional AutoTag step {Step} failed for {File} on {Platform}; continuing with provider metadata.",
                SanitizeLogValue(stepName),
                SanitizeLogValue(context.File),
                SanitizeLogValue(context.Platform));
            context.LogCallback(
                $"onetagger_autotag: {context.Platform} optional {stepName} failed; continuing with provider metadata");
        }
    }

    private static void ObserveBackgroundTask(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string? ResolveRecognitionStrategy(AutoTagMatchResult? match)
    {
        if (!string.IsNullOrWhiteSpace(match?.MatchStrategy))
        {
            return match.MatchStrategy;
        }

        return match?.Track.Other.TryGetValue("SHAZAM_MATCH_STRATEGY", out var strategies) == true
            ? strategies.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim().ToLowerInvariant()
            : null;
    }

    internal const int PlatformTimeoutCircuitThreshold = 3;

    private static bool IsPlatformUnavailable(JobMatchCacheState cache, string platform)
    {
        lock (cache.SyncRoot)
        {
            return cache.UnavailablePlatforms.Contains(platform);
        }
    }

    private static void NotePlatformMatchSuccess(JobMatchCacheState cache, string platform)
    {
        lock (cache.SyncRoot)
        {
            cache.LastAccessUtc = DateTimeOffset.UtcNow;
            cache.ConsecutiveTimeouts[platform] = 0;
        }
    }

    private static bool NotePlatformMatchTimeout(JobMatchCacheState cache, string platform)
    {
        lock (cache.SyncRoot)
        {
            cache.LastAccessUtc = DateTimeOffset.UtcNow;
            var next = cache.ConsecutiveTimeouts.TryGetValue(platform, out var current)
                ? current + 1
                : 1;
            cache.ConsecutiveTimeouts[platform] = next;
            if (next < PlatformTimeoutCircuitThreshold)
            {
                return false;
            }

            cache.UnavailablePlatforms.Add(platform);
            return true;
        }
    }

    public Task<bool> StopAsync(string jobId, CancellationToken cancellationToken)
    {
        if (_jobTokens.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    private DeezSpoTagSettings LoadRuntimeSettings(TechnicalTagSettings? technical, AutoTagRunnerConfig config)
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            ApplyTechnicalOverrides(settings, technical);
            ApplyRuntimeConfigOverrides(settings, config);
            return settings;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load runtime settings for AutoTag.");
            var fallback = DeezSpoTagSettingsService.GetStaticDefaultSettings();
            ApplyTechnicalOverrides(fallback, technical);
            ApplyRuntimeConfigOverrides(fallback, config);
            return fallback;
        }
    }

    private static string ReadFileOrEmpty(string path)
    {
        try
        {
            return IOFile.Exists(path) ? IOFile.ReadAllText(path) : string.Empty;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return string.Empty;
        }
    }

    private static IEnumerable<string> ResolveTargetFiles(string rootPath, AutoTagRunnerConfig config)
    {
        if (config.TargetFiles == null || config.TargetFiles.Count == 0)
        {
            return EnumerateAudioFiles(rootPath, config.IncludeSubfolders);
        }

        var normalizedRoot = NormalizeScopePath(DownloadPathResolver.ResolveIoPath(rootPath));
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in config.TargetFiles)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            var ioPath = DownloadPathResolver.ResolveIoPath(rawPath.Trim());
            if (string.IsNullOrWhiteSpace(ioPath))
            {
                continue;
            }

            var normalizedPath = NormalizeScopePath(ioPath);
            if (string.IsNullOrWhiteSpace(normalizedPath)
                || !IsPathWithinScope(normalizedPath, normalizedRoot)
                || !IOFile.Exists(normalizedPath)
                || !SupportedExtensions.Contains(Path.GetExtension(normalizedPath))
                || AnimatedArtworkFileNaming.IsAnimatedArtworkSidecar(normalizedPath))
            {
                continue;
            }

            selected.Add(normalizedPath);
        }

        return selected;
    }

    private static string NormalizeScopePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static bool IsPathWithinScope(string candidatePath, string scopePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(scopePath))
        {
            return false;
        }

        if (string.Equals(candidatePath, scopePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var scopeWithSeparator = scopePath.EndsWith(Path.DirectorySeparatorChar)
            || scopePath.EndsWith(Path.AltDirectorySeparatorChar)
            ? scopePath
            : scopePath + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(scopeWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCacheToken(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // best effort timeout cleanup
        }
    }

    /// <summary>
    /// Loads the cross-run album identity store so a track downloaded months after
    /// the rest of its album converges onto the same album id and release date as
    /// the earlier tracks.
    /// </summary>
    private void LoadPersistedAlbumIdentities()
    {
        var storePath = _albumIdentityStorePath;
        if (string.IsNullOrWhiteSpace(storePath))
        {
            return;
        }

        try
        {
            _albumIdentityStore = IOFile.Exists(storePath)
                ? AlbumIdentityStore.Load(storePath)
                : new AlbumIdentityStore();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load the album identity store.");
            _albumIdentityStore = new AlbumIdentityStore();
        }
    }

    /// <summary>Merges the run's established album identities back into the cross-run store.</summary>
    private void PersistAlbumIdentities(AutoTagRunPlan plan)
    {
        var storePath = _albumIdentityStorePath;
        if (string.IsNullOrWhiteSpace(storePath) || _albumIdentityStore == null || !plan.AlbumIdentities.IsDirty)
        {
            return;
        }

        try
        {
            _albumIdentityStore.Merge(plan.AlbumIdentities.Snapshot());
            _albumIdentityStore.Save(storePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to persist the album identity store.");
        }
    }

    private static void MoveAdjacentSidecars(string sourcePath, string destinationPath)
    {
        foreach (var extension in new[] { ".lrc", TtmlExtension, ".txt" })
        {
            var sourceSidecar = Path.ChangeExtension(sourcePath, extension);
            if (!IOFile.Exists(sourceSidecar))
            {
                continue;
            }

            var destinationSidecar = Path.ChangeExtension(destinationPath, extension);
            if (PathsReferToSameFile(sourceSidecar, destinationSidecar) || IOFile.Exists(destinationSidecar))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationSidecar) ?? "");
            FileMoveFallbackHelper.MoveWithFallback(sourceSidecar, destinationSidecar);
        }
    }

    private static bool PathsReferToSameFile(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void PersistManualMaterializedTargetPath(
        AutoTagRunPlan plan,
        string previousPath,
        string materializedPath)
    {
        if (PathsReferToSameFile(previousPath, materializedPath))
        {
            return;
        }

        var runtimeDirectory = Path.GetDirectoryName(plan.ConfigPath);
        if (string.IsNullOrWhiteSpace(runtimeDirectory) || !Directory.Exists(runtimeDirectory))
        {
            return;
        }

        var configPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { plan.ConfigPath };
        if (!string.IsNullOrWhiteSpace(plan.JobId))
        {
            foreach (var path in Directory.EnumerateFiles(runtimeDirectory, $"autotag-{plan.JobId}-*.json"))
            {
                configPaths.Add(path);
            }
        }

        foreach (var configPath in configPaths)
        {
            ReplaceTargetPathInRuntimeConfig(configPath, previousPath, materializedPath);
        }
    }

    private void CleanupUpgradedTxtSidecar(TagWriteExecutionContext context, LyricsSidecarWriteResult sidecarWriteResult)
    {
        if (!context.SidecarState.HasTxt
            || (!context.SidecarState.HasLrc
                && !context.SidecarState.HasTtml
                && !sidecarWriteResult.WroteLrcSidecar
                && !sidecarWriteResult.WroteTtmlSidecar))
        {
            return;
        }

        try
        {
            IOFile.Delete(context.SidecarState.TxtPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to remove upgraded TXT lyrics sidecar {Path}", SanitizeLogValue(context.SidecarState.TxtPath));
            }
        }
    }
}
