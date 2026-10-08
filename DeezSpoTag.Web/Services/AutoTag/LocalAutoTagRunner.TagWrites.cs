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
using GenreSemanticSnapshot = DeezSpoTag.Services.Genre.GenreSemanticSnapshot;
using TagLib;
using IOFile = System.IO.File;
using DownloadLyricsService = DeezSpoTag.Services.Download.Utils.LyricsService;
using LyricsProviderRegistry = DeezSpoTag.Services.Download.Utils.LyricsProviderRegistry;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed partial class LocalAutoTagRunner : IAutoTagRunner
{
    /// <summary>
    /// Records the file's own semantic tags before any AutoTag platform runs.
    ///
    /// This is not the resolution input — the post-platform read is. It exists so
    /// a value the user wrote themselves cannot be destroyed by a rewrite, and so
    /// history can show the file's true starting state. A resumed run past the
    /// first platform cannot reconstruct it, because the file has already been
    /// overwritten, so that case stops the file rather than guessing.
    /// </summary>
    private async Task CapturePreAutoTagSemanticSnapshotAsync(AutoTagFileRunContext context)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<DeezSpoTag.Services.Genre.PersonalGenreStore>();
        // A file already marked unusable stays unusable for the whole job. A
        // retry must not re-read the file and quietly start trusting it again,
        // because by then the file may have been partially written.
        if (await HasSnapshotErrorMarkerAsync(context))
        {
            throw new InvalidOperationException(
                "Genre Intelligence has an incomplete semantic snapshot; existing metadata was preserved.");
        }

        if (await store.GetSnapshotAsync(
                context.Plan.JobId, context.File, DeezSpoTag.Services.Genre.GenreSnapshotStage.PreAutoTag, context.Token) is not null)
        {
            return;
        }

        if (context.Plan.IsResumedRun && context.PlatformIndex > 0)
        {
            await store.MarkAutoTagSnapshotErrorAsync(context.Plan.JobId, context.File, CancellationToken.None);
            throw new InvalidOperationException(
                "Genre Intelligence cannot resume without the pre-AutoTag file snapshot; the file's original " +
                "semantic values have already been overwritten and cannot be reconstructed.");
        }

        var extension = Path.GetExtension(context.File);
        GenreSemanticSnapshot snapshot;
        try
        {
            using var file = TagLib.File.Create(context.File);
            snapshot = GenreSemanticTagIo.ReadSnapshot(
                file, extension, ResolveStylesTagName(context.Plan.Config, extension));
        }
        catch
        {
            // The file could not be read, so the stage must not guess what it
            // contained. Marking it keeps any later attempt for this job from
            // treating the file as safe to rewrite.
            await store.MarkAutoTagSnapshotErrorAsync(context.Plan.JobId, context.File, CancellationToken.None);
            throw;
        }

        var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        var trackId = await repository.GetTrackIdForFilePathAsync(context.File, context.Token);
        await store.SaveSnapshotAsync(
            context.Plan.JobId,
            context.File,
            DeezSpoTag.Services.Genre.GenreSnapshotStage.PreAutoTag,
            snapshot,
            trackId,
            context.Token);
    }

    private async Task<bool> HasSnapshotErrorMarkerAsync(AutoTagFileRunContext context)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeezSpoTag.Services.Genre.PersonalGenreStore>()
            .HasSnapshotErrorMarkerAsync(context.Plan.JobId, context.File, CancellationToken.None);
    }

    /// <summary>
    /// The final AutoTag stage: classify the tags the platforms just wrote, then
    /// rewrite the file consistently.
    /// </summary>
    private async Task ProcessGenreIntelligenceAsync(AutoTagFileRunContext context)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<DeezSpoTag.Services.Genre.PersonalGenreStore>();
        try
        {
            context.Token.ThrowIfCancellationRequested();
            EmitTaggingStatus(context, null, false);
            if (!await store.IsAutoTagSnapshotValidAsync(context.Plan.JobId, context.File, context.Token))
            {
                throw new InvalidOperationException(
                    "Genre Intelligence has an incomplete semantic snapshot; existing metadata was preserved.");
            }

            var extension = Path.GetExtension(context.File);
            if (!GenreSemanticTagIo.IsSupportedExtension(extension))
            {
                throw new NotSupportedException(
                    "Genre Intelligence semantic write-back supports MP3, FLAC and MP4-family files.");
            }

            // §6: the file as it stands after every ordinary platform has run. This
            // is the state Genre Intelligence is being asked to clean up, and it is
            // the primary input.
            var stylesTagName = ResolveStylesTagName(context.Plan.Config, extension);
            var postSnapshot = PersonalGenreService.ReadFileSnapshot(context.File, stylesTagName);
            var preSnapshot = await store.GetSnapshotAsync(
                context.Plan.JobId, context.File, DeezSpoTag.Services.Genre.GenreSnapshotStage.PreAutoTag, context.Token);

            // §25: the post-AutoTag state is recorded as its own snapshot. It is
            // the file as the provider left it, which is not the same as the
            // resolver's input once values have been carried forward.
            await store.SaveSnapshotAsync(
                context.Plan.JobId,
                context.File,
                DeezSpoTag.Services.Genre.GenreSnapshotStage.PostAutoTag,
                postSnapshot,
                null,
                context.Token);

            var service = scope.ServiceProvider.GetRequiredService<PersonalGenreService>();
            var knownTrackId = await store.GetAutoTagTrackIdAsync(context.Plan.JobId, context.File, context.Token)
                ?? await scope.ServiceProvider.GetRequiredService<LibraryRepository>()
                    .GetTrackIdForFilePathAsync(context.File, context.Token);
            var resolved = await service.ResolveFileAsync(
                knownTrackId,
                context.File,
                postSnapshot,
                preSnapshot,
                context.Plan.Config.GenreIntelligence,
                context.Token);
            var result = resolved.Result;
            var resolution = result.Resolution;

            // §27: the complete final payload is built and the decision record made
            // durable before any tag is touched, so a failure mid-write leaves a
            // full record of what was found rather than a half-written file.
            var plan = GenreSemanticTagIo.PlanFields(resolution, context.Plan.Config.CapitalizeGenres);

            // The proposed transformation is computed from the same plan the writer
            // consumes, and it is computed before the write. Describing the moves
            // afterwards would only ever report what already happened, which cannot
            // be reviewed before it happens.
            var moves = GenreSemanticTagIo.DescribeFieldMoves(result.PostAutoTagSnapshot!.Observations, plan);
            // The removals come from the resolution context rather than being
            // recomputed, so the blocked values this stage reports are exactly the
            // ones the resolver was told about — the same list the cleanup preview
            // endpoint shows for this file.
            var preview = GenreCleanupPreviewBuilder.Build(
                resolution, postSnapshot.Observations, resolved.Context.RemovedByNormalization);

            await store.SaveAutoTagResultAsync(
                context.Plan.JobId, context.File, knownTrackId, resolution, "resolved", context.Token);
            context.Token.ThrowIfCancellationRequested();

            var written = WriteGenreIntelligenceTags(context.File, context.Plan.Config, resolution, plan);
            context.Token.ThrowIfCancellationRequested();

            // §27: confirm the write by reading the file back, so a silently
            // ignored field is reported as a failure rather than a success.
            var verify = GenreSemanticTagIo.VerifyWrittenFields(
                context.File,
                extension,
                stylesTagName,
                plan,
                context.Plan.Config.GenreIntelligence,
                BuildConfiguredTagSet(context.Plan.Config.Tags));
            if (verify.Count > 0)
            {
                await store.SaveAutoTagResultAsync(
                    context.Plan.JobId, context.File, knownTrackId, resolution, "write_failed", context.Token);
                throw new IOException(
                    "Genre Intelligence could not write these semantic fields: " + string.Join("; ", verify) +
                    ". The file may be partially updated.");
            }

            await store.SaveAutoTagResultAsync(
                context.Plan.JobId, context.File, knownTrackId, resolution, "written:" + string.Join(",", written), context.Token);

            var message = resolution.Genres.Count == 0 ? "no canonical genre" : resolution.PrimaryGenre;
            context.LogCallback(
                $"Genre Intelligence: read {postSnapshot.Observations.Count} file tag values; " +
                $"{resolution.Classifications.Count} classifications; " +
                $"{resolution.Preserved.Count} preserved unmapped; " +
                $"{preview.Removed.Count} removed; " +
                $"{preview.Moved.Count} moved; " +
                $"{preview.Canonicalized.Count} canonicalized; " +
                $"{preview.PreservedUnknown.Count} kept as unknown; " +
                $"vocabulary {preview.CatalogVersion}; " +
                $"{(resolution.Classifications.Any(item => item.UserLocked) ? "user lock applied" : "no user lock")}; " +
                $"tags written: {(written.Count == 0 ? "none" : string.Join(", ", written))}." +
                (moves.Count == 0 ? string.Empty : " Corrections: " + string.Join("; ", moves) + "."));
            EmitStatus(context, written.Count == 0 ? "skipped" : "tagged", message, null, false,
                outcome: written.Count == 0
                    ? (resolution.Genres.Count == 0 ? "no_canonical_result" : "no_selected_semantic_values")
                    : "genre_intelligence_written");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // §28: the file is left as it is. A failure here is recoverable for the
            // rest of the run and is surfaced rather than swallowed.
            context.LogCallback($"Genre Intelligence failed for {Path.GetFileName(context.File)}: {ex.Message}");
            EmitErrorStatus(context, ex.Message, false, "genre_intelligence_error");
        }
    }

    /// <summary>
    /// Writes one raw semantic tag, used by the Genre Intelligence tests to build
    /// a fixture through the same encoder a real run uses. A second parser in the
    /// test would drift from the real one and hide encoding bugs.
    /// </summary>
    internal static void SetSemanticRawTagForTest(string path, string rawName, string[] values)
    {
        var config = new AutoTagRunnerConfig();
        var extension = Path.GetExtension(path);
        using var file = TagLib.File.Create(path);
        var context = new TagWriteContext(
            file,
            extension,
            config,
            ResolveSeparatorForFormat(config, extension),
            "genre-intelligence",
            config.Technical?.UseNullSeparator == true,
            new Dictionary<string, string>(),
            Array.Empty<string>(),
            false,
            new HashSet<SupportedTag>());
        SetRaw(context, rawName, SupportedTag.OtherTags, values.ToList(), force: true);
        file.Save();
    }

    /// <summary>
    /// Writes the final semantic state.
    ///
    /// Each enabled dimension is replaced wholesale rather than merged into what
    /// the providers left, because the whole point of this stage is to leave a
    /// coherent classification instead of a provider's opinion plus corrections.
    /// Every write is forced, so a dimension the run did not select for AutoTag is
    /// still finalised here, and an existing value is never left in front of the
    /// resolved one.
    ///
    /// StylesOptions is deliberately not applied. Those transformations belong to
    /// the provider stage, and re-running them over an already-classified result
    /// would undo the classification.
    /// </summary>
    private static IReadOnlyList<string> WriteGenreIntelligenceTags(
        string path,
        AutoTagRunnerConfig config,
        DeezSpoTag.Services.Genre.PersonalGenreResolution resolution,
        IReadOnlyDictionary<DeezSpoTag.Services.Genre.PersonalGenreTaxonKind, List<string>> plan)
    {
        var extension = Path.GetExtension(path);
        if (!GenreSemanticTagIo.IsSupportedExtension(extension))
        {
            throw new NotSupportedException("Genre Intelligence semantic write-back supports MP3, FLAC and MP4-family files.");
        }

        var writeConfig = JsonSerializer.Deserialize<AutoTagRunnerConfig>(JsonSerializer.Serialize(config))!;
        writeConfig.OverwriteTags = [GenreTag, StyleTag, LanguageTag];
        var selected = BuildConfiguredTagSet(config.Tags);
        var stylesTagName = ResolveStylesTagName(config, extension);
        var written = new List<string>();

        using var file = TagLib.File.Create(path);
        var context = new TagWriteContext(file, extension, writeConfig,
            ResolveSeparatorForFormat(config, extension), "genre-intelligence",
            config.Technical?.UseNullSeparator == true,
            new Dictionary<string, string>(), Array.Empty<string>(), false, new HashSet<SupportedTag>());

        Write(DeezSpoTag.Services.Genre.PersonalGenreTaxonKind.Genre, selected.Contains(GenreTag),
            values => SetField(context, new TagFieldBinding("TCON", Mp4GenreTag, "©gen", SupportedTag.Genre), values));
        Write(DeezSpoTag.Services.Genre.PersonalGenreTaxonKind.Style, selected.Contains(StyleTag),
            values => SetRaw(context, stylesTagName, SupportedTag.Style, values, force: true));
        Write(DeezSpoTag.Services.Genre.PersonalGenreTaxonKind.Language, selected.Contains(LanguageTag),
            values => SetRaw(context, LanguageRawTag, SupportedTag.Language, values, force: true));

        // The extra dimensions are opt-in: a user who never asked for a SUBSTYLE
        // tag should not start getting one.
        Write(DeezSpoTag.Services.Genre.PersonalGenreTaxonKind.Substyle, config.GenreIntelligence.WriteSubstyle,
            values => SetRaw(context, "SUBSTYLE", SupportedTag.OtherTags, values, force: true));
        Write(DeezSpoTag.Services.Genre.PersonalGenreTaxonKind.Context, config.GenreIntelligence.WriteContext,
            values => SetRaw(context, "CONTEXT", SupportedTag.OtherTags, values, force: true));
        Write(DeezSpoTag.Services.Genre.PersonalGenreTaxonKind.Scene, config.GenreIntelligence.WriteScene,
            values => SetRaw(context, "SCENE", SupportedTag.OtherTags, values, force: true));

        if (written.Count > 0)
        {
            file.Save();
        }

        return written;

        void Write(DeezSpoTag.Services.Genre.PersonalGenreTaxonKind field, bool enabled, Action<List<string>> writer)
        {
            if (!enabled || !plan.TryGetValue(field, out var values) || values.Count == 0)
            {
                return;
            }

            writer(values);
            written.Add(field.ToString());
        }
    }

    private static ProviderTagPlan BuildProviderTagPlan(AutoTagFileRunContext context)
    {
        var configured = context.Plan.Config.Tags
            .Select(tag => tag?.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => SupportedTagMap.TryGetValue(tag!, out var mapped) ? (SupportedTag?)mapped : null)
            .Where(tag => tag.HasValue)
            .Select(tag => tag!.Value)
            .ToHashSet();

        if (context.Plan.PlatformSupportedTags.TryGetValue(context.Platform, out var supported))
        {
            var common = configured.Where(tag => tag is SupportedTag.ArtistCountry or SupportedTag.ArtistCity
                or SupportedTag.ArtistRegion or SupportedTag.ArtistLanguage).ToArray();
            configured.IntersectWith(supported);
            configured.UnionWith(common);
        }

        var retained = new HashSet<SupportedTag>();
        var eligible = new HashSet<SupportedTag>();
        try
        {
            using var file = TagLib.File.Create(context.File);
            var extension = Path.GetExtension(context.File);
            foreach (var tag in configured)
            {
                if (!ShouldOverwriteTag(context.Plan.Config, tag)
                    && HasTag(file, extension, tag, context.Plan.Config, context.Platform))
                {
                    retained.Add(tag);
                }
                else
                {
                    eligible.Add(tag);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            eligible.UnionWith(configured);
        }

        return new ProviderTagPlan(configured, eligible, retained);
    }

    private static void ApplyTechnicalOverrides(DeezSpoTagSettings settings, TechnicalTagSettings? technical)
    {
        if (technical == null)
        {
            return;
        }

        TechnicalLyricsSettingsApplier.Apply(settings, technical);
        settings.Tags ??= new TagSettings();

        settings.DateFormat = technical.DateFormat;
        settings.AlbumVariousArtists = technical.AlbumVariousArtists;
        settings.RemoveAlbumVersion = technical.RemoveAlbumVersion;
        settings.RemoveFeaturedFromAlbumTitle = technical.RemoveFeaturedFromAlbumTitle;
        settings.RemoveDuplicateArtists = technical.RemoveDuplicateArtists;
        settings.FeaturedToTitle = technical.FeaturedToTitle;
        settings.TitleCasing = technical.TitleCasing;
        settings.ArtistCasing = technical.ArtistCasing;

        settings.Tags.SavePlaylistAsCompilation = technical.SavePlaylistAsCompilation;
        settings.Tags.UseNullSeparator = technical.UseNullSeparator;
        settings.Tags.SaveID3v1 = technical.SaveID3v1;
        settings.Tags.MultiArtistSeparator = technical.MultiArtistSeparator;
        settings.Tags.SingleAlbumArtist = technical.SingleAlbumArtist;
        settings.Tags.CoverDescriptionUTF8 = technical.CoverDescriptionUTF8;
    }

    private static void ApplyFolderStructureOverrides(DeezSpoTagSettings settings, FolderStructureSettings? folderStructure)
    {
        if (folderStructure == null)
        {
            return;
        }

        settings.CreateArtistFolder = folderStructure.CreateArtistFolder;
        settings.CreateAlbumFolder = folderStructure.CreateAlbumFolder;
        settings.CreateCDFolder = folderStructure.CreateCDFolder;
        settings.CreateStructurePlaylist = folderStructure.CreateStructurePlaylist;
        settings.CreateSingleFolder = folderStructure.CreateSingleFolder;
        settings.CreatePlaylistFolder = folderStructure.CreatePlaylistFolder;

        if (!string.IsNullOrWhiteSpace(folderStructure.ArtistNameTemplate))
        {
            settings.ArtistNameTemplate = folderStructure.ArtistNameTemplate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(folderStructure.AlbumNameTemplate))
        {
            settings.AlbumNameTemplate = folderStructure.AlbumNameTemplate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(folderStructure.PlaylistNameTemplate))
        {
            settings.PlaylistNameTemplate = folderStructure.PlaylistNameTemplate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(folderStructure.IllegalCharacterReplacer))
        {
            settings.IllegalCharacterReplacer = folderStructure.IllegalCharacterReplacer.Trim();
        }
    }

    private static string? NormalizeManualReleasePreference(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            AutoTagReleaseCategory.Album => AutoTagReleaseCategory.Album,
            AutoTagReleaseCategory.Single => AutoTagReleaseCategory.Single,
            _ => null
        };

    private static int? ParsePositiveInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        return int.TryParse(trimmed, out var value) && value > 0 ? value : null;
    }

    private async Task<TagFileWriteResult> TagFileAsync(
        string filePath,
        AutoTagTrack track,
        TagSettings tagSettings,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        string platformId,
        ProviderIdentityPayload? providerIdentity,
        CancellationToken token)
    {
        EnsureReleaseCategory(track);
        var separator = ResolveSeparatorForFormat(config, Path.GetExtension(filePath));
        ApplyArtistAliasPreference(track);
        var effectiveTagSettings = ApplyOverwriteRules(filePath, tagSettings, config, platformId, track, settings);
        NormalizeTrackArtistsForTagging(track, effectiveTagSettings.SingleAlbumArtist);
        var coreTrack = BuildCoreTrack(track, separator, effectiveTagSettings.SingleAlbumArtist, settings);
        string? tempCoverPath = null;
        var shouldPrepareTemplateArtworkSidecar = ShouldPrepareTemplateArtworkSidecar(config);

        if ((effectiveTagSettings.Cover || shouldPrepareTemplateArtworkSidecar) && !string.IsNullOrWhiteSpace(track.Art))
        {
            tempCoverPath = TryResolveExistingCoverSidecar(filePath, track, coreTrack, config, settings)
                ?? await DownloadCoverAsync(track.Art, token);
        }

        if (effectiveTagSettings.Cover &&
            string.IsNullOrWhiteSpace(tempCoverPath) &&
            !TrackHasEmbeddedArtwork(filePath, config, platformId))
        {
            tempCoverPath = TryResolveFolderArtworkPath(filePath);
        }

        var embeddedCoverPath = effectiveTagSettings.Cover && !string.IsNullOrWhiteSpace(tempCoverPath)
            ? await PrepareEmbeddedArtworkFileAsync(tempCoverPath, filePath, settings, token)
            : null;

        var writeResult = await WriteTagsOnetaggerStyleAsync(
            new TagWriteRequest
            {
                FilePath = filePath,
                SourceTrack = track,
                CoreTrack = coreTrack,
                EffectiveTagSettings = effectiveTagSettings,
                Config = config,
                Settings = settings,
                PlatformId = platformId,
                Separator = separator,
                TempCoverPath = embeddedCoverPath
            },
            token);
        await EnsureTemplateFoldersAndArtworkSidecarAsync(
            track,
            coreTrack,
            config,
            settings,
            filePath,
            tempCoverPath,
            token);
        if (!IsMp4Family(Path.GetExtension(filePath)))
        {
            writeResult.AttemptedTags.UnionWith(await ApplyCustomTagsAsync(
                filePath,
                track,
                config,
                platformId,
                effectiveTagSettings.UseNullSeparator));
        }

        // Provider identity is written once, through the backend that owns this
        // container, after every ordinary write has been saved.
        var identityWriteResult = await WriteProviderIdentityAsync(
            filePath,
            providerIdentity,
            config,
            ResolveEnabledProviderIdentityFields(config),
            token);
        writeResult.AttemptedTags.UnionWith(identityWriteResult.AttemptedTags);
        if (identityWriteResult.Failures.Count > 0)
        {
            var failure = identityWriteResult.Failures[0];
            throw new IOException(
                $"Provider identity persistence failed: format={failure.Format}, provider={failure.Provider}, field={failure.Field}, reason={failure.Reason}.");
        }

        if (!string.IsNullOrWhiteSpace(embeddedCoverPath))
        {
            IOFile.Delete(embeddedCoverPath);
        }
        if (!string.IsNullOrWhiteSpace(tempCoverPath) && !string.Equals(Path.GetDirectoryName(tempCoverPath), Path.GetDirectoryName(filePath), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                IOFile.Delete(tempCoverPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // best effort
            }
        }

        return writeResult;
    }

    /// <summary>
    /// Rewrites the track's artist credits, album artists, featured names, and
    /// "feat." strings inside track/album titles so every user-defined alias
    /// becomes its preferred name before tags are written. Applies to every
    /// tagging run — enrichment, enhancement, and merge-triggered runs alike.
    /// </summary>
    private static void ApplyArtistAliasPreference(AutoTagTrack track)
    {
        try
        {
            track.Artists = RewriteCreditList(track.Artists);
            track.AlbumArtists = RewriteCreditList(track.AlbumArtists);
            if (!string.IsNullOrWhiteSpace(track.Title))
            {
                track.Title = DeezSpoTag.Services.Library.ArtistAliasGateway.ResolveCredit(track.Title);
            }

            if (!string.IsNullOrWhiteSpace(track.Album))
            {
                track.Album = DeezSpoTag.Services.Library.ArtistAliasGateway.ResolveCredit(track.Album);
            }

            foreach (var key in track.Other.Keys.ToList())
            {
                track.Other[key] = RewriteCreditList(track.Other[key]);
            }
        }
        catch
        {
            // Alias resolution must never break tagging.
        }
    }

    internal async Task<TagFileWriteResult> WriteTagsOnetaggerStyleAsync(
        TagWriteRequest request,
        CancellationToken token)
    {
        var context = BuildTagWriteExecutionContext(request);
        var chapterSnapshot = AtlTagHelper.CaptureChapters(context.FilePath, context.Extension, _logger);

        using var file = TagLib.File.Create(context.FilePath);
        PrepareId3Version(file, context);

        var tagWriteContext = new TagWriteContext(
            file,
            context.Extension,
            context.Config,
            context.Separator,
            context.PlatformId,
            context.EffectiveTagSettings.UseNullSeparator,
            context.GenreAliasMap,
            context.GenreBlockList,
            context.SplitCompositeGenres,
            context.AttemptedTags);
        ApplyPrimaryTagWrites(tagWriteContext, context);
        ApplyAudioFeatureTagWrites(tagWriteContext, context);
        ApplyGenreAndStyleTagWrites(file, tagWriteContext, context);
        ApplyReleaseAndMetadataTagWrites(file, tagWriteContext, context);
        ApplyTrackAndLyricsTagWrites(file, tagWriteContext, context);
        ApplyAlbumArtTagWrite(file, context);
        file.Save();
        RemoveId3v1TagIfDisabled(file, context);
        file.Dispose();

        if (IsMp4Family(context.Extension)
            && context.AttemptedTags.Contains(SupportedTag.AlbumArt)
            && !string.IsNullOrWhiteSpace(context.TempCoverPath))
        {
            var bytes = await IOFile.ReadAllBytesAsync(context.TempCoverPath, token);
            using var image = SixLabors.ImageSharp.Image.Load(bytes);
            EmbeddedArtworkWriter.WriteAndVerify(
                context.FilePath,
                new ArtworkVariant(
                    bytes,
                    Path.GetExtension(context.TempCoverPath).TrimStart('.').ToLowerInvariant(),
                    CoverArtMimeTypeResolver.Resolve(context.TempCoverPath, bytes),
                    image.Width,
                    image.Height));
        }

        AtlTagHelper.RestoreChapters(context.FilePath, chapterSnapshot, _logger);

        var sidecarWriteResult = await WriteLyricsSidecarsAsync(context, token);
        CleanupUpgradedTxtSidecar(context, sidecarWriteResult);

        if (sidecarWriteResult.WroteTtmlSidecar)
        {
            context.AttemptedTags.Add(SupportedTag.TtmlLyrics);
        }

        return new TagFileWriteResult(context.AttemptedTags);
    }

    internal static TagWriteExecutionContext BuildTagWriteExecutionContext(TagWriteRequest request)
    {
        var extension = Path.GetExtension(request.FilePath);
        var enabledTags = BuildConfiguredTagSet(request.Config.Tags);
        // Genre Intelligence owns the genre-spelling preferences. The provider
        // stage still applies them, as it did before, because a downloaded or
        // provider-supplied value is normalized on the way in. The terminal
        // Genre Intelligence stage does not re-apply them: its output is already
        // classified, and normalizing it again would undo that.
        var genreNormalization = request.Settings.GenreNormalization;
        var normalizeGenreTags = genreNormalization.Enabled;
        var genreAliasMap = genreNormalization.AliasMap;
        var genreBlockList = genreNormalization.BlockList;
        var allowsSyncedByToggle = request.Settings.SyncedLyrics;
        var allowsUnsyncedByToggle = request.Settings.SaveLyrics;
        var allowsLyricsBySettings = allowsSyncedByToggle || allowsUnsyncedByToggle;
        var selectedLyricsTypes = ParseLyricsTypeSelection(request.Settings.LrcType);
        var allowsSyncedType = allowsSyncedByToggle
            && (selectedLyricsTypes.Contains(LyricsTag) || selectedLyricsTypes.Contains(SyllableLyricsType));
        var allowsUnsyncedType = allowsUnsyncedByToggle && selectedLyricsTypes.Contains(UnsyncedLyricsType);
        var allowsTtmlByFormat = allowsSyncedByToggle
            && selectedLyricsTypes.Contains(TtmlLyricsType)
            && ParseLyricsFormatSelection(request.Settings.LrcFormat).Contains("ttml");
        var allowsLrcByFormat = allowsSyncedByToggle
            && ParseLyricsFormatSelection(request.Settings.LrcFormat).Contains("lrc");
        var sidecarState = GetLyricsSidecarState(request.FilePath);

        return new TagWriteExecutionContext
        {
            FilePath = request.FilePath,
            SourceTrack = request.SourceTrack,
            CoreTrack = request.CoreTrack,
            EffectiveTagSettings = request.EffectiveTagSettings,
            Config = request.Config,
            Settings = request.Settings,
            PlatformId = request.PlatformId,
            Separator = request.Separator,
            TempCoverPath = request.TempCoverPath,
            Extension = extension,
            EnabledTags = enabledTags,
            GenreAliasMap = genreAliasMap,
            GenreBlockList = genreBlockList,
            SplitCompositeGenres = normalizeGenreTags,
            AllowsLyricsBySettings = allowsLyricsBySettings,
            AllowsSyncedType = allowsSyncedType,
            AllowsUnsyncedType = allowsUnsyncedType,
            AllowsLrcByFormat = allowsLrcByFormat,
            AllowsTtmlByFormat = allowsTtmlByFormat,
            SidecarState = sidecarState,
            ShouldSkipEmbeddedLyrics = sidecarState.HasAny
        };
    }

    private static void ApplyPrimaryTagWrites(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        WriteTitleTag(tagWriteContext, context);
        WriteVersionTag(tagWriteContext, context);
        WriteArtistTag(tagWriteContext, context);
        WriteArtistsTag(tagWriteContext, context);
        WriteAlbumArtistTag(tagWriteContext, context);
        WriteAlbumTag(tagWriteContext, context);
        WriteKeyTag(tagWriteContext, context);
        WriteBpmTag(tagWriteContext, context);
        WriteLabelTag(tagWriteContext, context);
    }

    private static void ApplyAudioFeatureTagWrites(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        WriteAudioFeatureTag(tagWriteContext, context, "danceability", DanceabilityTag, SupportedTag.Danceability, context.SourceTrack.Danceability);
        WriteAudioFeatureTag(tagWriteContext, context, "energy", EnergyTag, SupportedTag.Energy, context.SourceTrack.Energy);
        WriteAudioFeatureTag(tagWriteContext, context, "valence", ValenceTag, SupportedTag.Valence, context.SourceTrack.Valence);
        WriteAudioFeatureTag(tagWriteContext, context, "acousticness", AcousticnessTag, SupportedTag.Acousticness, context.SourceTrack.Acousticness);
        WriteAudioFeatureTag(tagWriteContext, context, "instrumentalness", InstrumentalnessTag, SupportedTag.Instrumentalness, context.SourceTrack.Instrumentalness);
        WriteAudioFeatureTag(tagWriteContext, context, "speechiness", SpeechinessTag, SupportedTag.Speechiness, context.SourceTrack.Speechiness);
        WriteAudioFeatureTag(tagWriteContext, context, "loudness", LoudnessTag, SupportedTag.Loudness, context.SourceTrack.Loudness);
        WriteAudioFeatureTag(tagWriteContext, context, "tempo", TempoTag, SupportedTag.Tempo, context.SourceTrack.Tempo);
        WriteAudioFeatureTag(tagWriteContext, context, "liveness", LivenessTag, SupportedTag.Liveness, context.SourceTrack.Liveness);

        if (context.EnabledTags.Contains("timeSignature") && context.SourceTrack.TimeSignature.HasValue)
        {
            SetRaw(
                tagWriteContext,
                TimeSignatureTag,
                SupportedTag.TimeSignature,
                new List<string> { context.SourceTrack.TimeSignature.Value.ToString(CultureInfo.InvariantCulture) });
        }
    }

    private static void ApplyGenreAndStyleTagWrites(
        TagLib.File file,
        TagWriteContext tagWriteContext,
        TagWriteExecutionContext context)
    {
        var genres = SanitizeGenres(context.CoreTrack.Album?.Genre ?? new List<string>(), context.GenreAliasMap, context.GenreBlockList, context.SplitCompositeGenres);
        var styles = NormalizeStyleValues(context.SourceTrack.Styles, context.Separator);
        (genres, styles) = ApplyStylesOptions(genres, styles, context.Config.StylesOptions);

        if (context.EnabledTags.Contains(GenreTag) && context.EffectiveTagSettings.Genre && genres.Count > 0)
        {
            var existingGenres = ReadExistingGenre(context.FilePath);
            if (context.Config.MergeGenres)
            {
                var existing = SanitizeGenres(existingGenres, context.GenreAliasMap, context.GenreBlockList, context.SplitCompositeGenres);
                var genreSet = new HashSet<string>(genres, StringComparer.OrdinalIgnoreCase);
                genres.AddRange(existing.Where(genreSet.Add));
            }

            genres = SanitizeGenres(genres, context.GenreAliasMap, context.GenreBlockList, context.SplitCompositeGenres);
            if (context.Config.CapitalizeGenres)
            {
                genres = genres.Select(CapitalizeGenre).ToList();
            }
            genres = GenreTagAliasNormalizer.DedupeValues(genres, context.GenreBlockList);

            // Strict no-op: when the merge (after alias/split/capitalize) produced the
            // exact set already on the file — same values, same casing, any order —
            // the genre write is skipped entirely instead of touching the tag block.
            if (!GenreWriteAddsNothing(genres, existingGenres))
            {
                SetField(tagWriteContext, new TagFieldBinding("TCON", Mp4GenreTag, "©gen", SupportedTag.Genre), genres);
            }
        }

        if (!context.EnabledTags.Contains(StyleTag) || styles.Count == 0)
        {
            return;
        }

        var styleTagName = ResolveStylesTagName(context.Config, context.Extension);
        var styleValues = styles;
        if (context.Config.MergeGenres)
        {
            var existingStyles = NormalizeStyleValues(
                ReadExistingRawTag(file, context.Extension, styleTagName),
                context.Separator);
            var existingStyleSet = new HashSet<string>(existingStyles, StringComparer.OrdinalIgnoreCase);
            existingStyles.AddRange(styleValues.Where(existingStyleSet.Add));
            styleValues = existingStyles;
        }

        if (context.Config.CapitalizeGenres)
        {
            styleValues = styleValues.Select(CapitalizeGenre).ToList();
        }

        var rawName = context.Config.StylesOptions.Equals("customTag", StringComparison.OrdinalIgnoreCase)
            ? styleTagName
            : ResolveFieldRawName(SupportedTag.Style, ResolveFormatName(context.Extension), context.Config);
        SetRaw(tagWriteContext, rawName, SupportedTag.Style, styleValues);
    }

    private static (List<string> Genres, List<string> Styles) ApplyStylesOptions(
        List<string> genres,
        List<string> styles,
        string stylesOption)
    {
        switch (stylesOption.ToLowerInvariant())
        {
            case "onlygenres":
                styles = new List<string>();
                break;
            case "onlystyles":
                genres = new List<string>();
                break;
            case "mergetogenres":
                var genreSet = new HashSet<string>(genres, StringComparer.OrdinalIgnoreCase);
                genres.AddRange(styles.Where(genreSet.Add));
                break;
            case "mergetostyles":
                var styleSet = new HashSet<string>(styles, StringComparer.OrdinalIgnoreCase);
                styles.AddRange(genres.Where(styleSet.Add));
                break;
            case "stylestogenre":
                genres = styles.ToList();
                break;
            case "genrestostyle":
                styles = genres.ToList();
                break;
        }

        return (genres, styles);
    }

    private static void ApplyReleaseAndMetadataTagWrites(
        TagLib.File file,
        TagWriteContext tagWriteContext,
        TagWriteExecutionContext context)
    {
        WriteReleaseDateTag(file, context);
        WritePublishDateTag(file, context);
        // Provider identity tags are owned exclusively by WriteProviderIdentityAsync.
        WriteCatalogNumberTag(tagWriteContext, context);
        WriteDurationTag(tagWriteContext, context);
        WriteRemixerTag(tagWriteContext, context);
        WriteIsrcTag(tagWriteContext, context);
        WriteMoodTag(tagWriteContext, context);
        WriteActivityTag(tagWriteContext, context);
    }

    private static void MarkAttemptedIfPresent(TagWriteExecutionContext context, TagLib.File file, SupportedTag tag)
    {
        if (HasTag(file, context.Extension, tag, context.Config, context.PlatformId))
        {
            context.AttemptedTags.Add(tag);
        }
    }

    private Task<HashSet<SupportedTag>> ApplyCustomTagsAsync(
        string filePath,
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        string platformId,
        bool useNullSeparator)
    {
        if (config.Tags.Count == 0)
        {
            return Task.FromResult(new HashSet<SupportedTag>());
        }

        var attemptedTags = new HashSet<SupportedTag>();
        try
        {
            var extension = Path.GetExtension(filePath);
            var chapterSnapshot = AtlTagHelper.CaptureChapters(filePath, extension, _logger);
            using var file = TagLib.File.Create(filePath);
            var enabledTags = BuildConfiguredTagSet(config.Tags);
            var separator = ResolveArtistSeparator(config, filePath);
            var writes = BuildCustomTagWrites(track, config, platformId, extension, file);

            if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
                ApplyId3CustomTags(id3, writes, config, separator, useNullSeparator, enabledTags, attemptedTags);
            }
            else if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
            {
                var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
                ApplyVorbisCustomTags(vorbis, writes, config, separator, enabledTags, attemptedTags);
            }
            else if (IsMp4Family(extension))
            {
                var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
                ApplyAppleCustomTags(apple, writes, config, separator, enabledTags, attemptedTags);
            }

            file.Save();
            AtlTagHelper.RestoreChapters(filePath, chapterSnapshot, _logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed applying custom tags for {File}", SanitizeLogValue(filePath));
        }

        return Task.FromResult(attemptedTags);
    }

    private static string[] ApplySeparator(List<string> values, string separator, bool useNullSeparator = false)
    {
        if (values.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (useNullSeparator)
        {
            return values.ToArray();
        }

        if (string.IsNullOrEmpty(separator))
        {
            return values.ToArray();
        }

        return new[] { string.Join(separator, values) };
    }

    private static List<CustomTagWrite> BuildCustomTagWrites(AutoTagTrack track, AutoTagRunnerConfig config, string platformId, string extension, TagLib.File file)
    {
        var writes = new List<CustomTagWrite>();
        var styleTagName = ResolveStylesTagName(config, extension);
        var format = ResolveFormatName(extension);

        if (track.Styles.Count > 0)
        {
            var separator = ResolveSeparatorForFormat(config, extension);
            var styleValues = NormalizeStyleValues(track.Styles, separator);
            if (config.MergeGenres)
            {
                var existing = NormalizeStyleValues(
                    ReadExistingRawTag(file, extension, styleTagName),
                    separator);
                var existingStyleSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
                existing.AddRange(styleValues.Where(existingStyleSet.Add));
                styleValues = existing;
            }

            if (config.CapitalizeGenres)
            {
                styleValues = styleValues.Select(CapitalizeGenre).ToList();
            }

            var styleRaw = config.StylesOptions.Equals("customTag", StringComparison.OrdinalIgnoreCase)
                ? styleTagName
                : ResolveFieldRawName(SupportedTag.Style, format, config);

            writes.Add(new CustomTagWrite(StyleTag, SupportedTag.Style, styleRaw, styleValues));
        }

        AddSingleValueCustomTagWrite(
            writes,
            "mood",
            SupportedTag.Mood,
            ResolveFieldRawName(SupportedTag.Mood, format, config),
            track.Mood);
        AddSingleValueCustomTagWrite(
            writes,
            "key",
            SupportedTag.Key,
            ResolveFieldRawName(SupportedTag.Key, format, config),
            track.Key);
        AddSingleValueCustomTagWrite(
            writes,
            VersionTag,
            SupportedTag.Version,
            ResolveFieldRawName(SupportedTag.Version, format, config),
            track.Version);

        if (track.Remixers.Count > 0)
        {
            writes.Add(new CustomTagWrite(RemixerTag, SupportedTag.Remixer, ResolveFieldRawName(SupportedTag.Remixer, format, config), track.Remixers.ToList()));
        }

        AddSingleValueCustomTagWrite(
            writes,
            CatalogNumberTag,
            SupportedTag.CatalogNumber,
            ResolveFieldRawName(SupportedTag.CatalogNumber, format, config),
            track.CatalogNumber);
        // Provider identity tags (track/album/release/artist/album-artist id and URL)
        // are written by the single provider identity writer, never here.
        AddSingleValueCustomTagWrite(writes, ReleaseGroupIdTag, SupportedTag.ReleaseGroupId, ReleaseGroupIdRawTag, track.ReleaseGroupId);
        if (string.Equals(platformId, "musicbrainz", StringComparison.OrdinalIgnoreCase))
        {
            AddSingleValueCustomTagWrite(writes, ReleaseGroupIdTag, SupportedTag.ReleaseGroupId, "MUSICBRAINZ_RELEASEGROUPID", track.ReleaseGroupId);
        }
        AddSingleValueCustomTagWrite(writes, ReleaseStatusTag, SupportedTag.ReleaseStatus, ReleaseStatusRawTag, track.ReleaseStatus);
        AddSingleValueCustomTagWrite(writes, ReleaseCountryTag, SupportedTag.ReleaseCountry, ReleaseCountryRawTag, track.ReleaseCountry);
        AddSingleValueCustomTagWrite(writes, BarcodeTag, SupportedTag.Barcode, BarcodeRawTag, track.Barcode);
        AddSingleValueCustomTagWrite(writes, LyricistTag, SupportedTag.Lyricist, ResolveFieldRawName(SupportedTag.Lyricist, format, config), track.Lyricist);
        AddSingleValueCustomTagWrite(writes, PublisherTag, SupportedTag.Publisher, ResolveFieldRawName(SupportedTag.Publisher, format, config), track.Publisher);
        AddSingleValueCustomTagWrite(writes, DescriptionTag, SupportedTag.Description, ResolveFieldRawName(SupportedTag.Description, format, config), track.Description);
        if (track.Media.Count > 0)
        {
            writes.Add(new CustomTagWrite(MediaTag, SupportedTag.Media, MediaRawTag, track.Media.ToList()));
        }
        AddOtherTagWrites(writes, track.Other);
        AddMetaTagWrite(writes, config);

        return writes;
    }

    private static void AddSingleValueCustomTagWrite(
        List<CustomTagWrite> writes,
        string tagKey,
        SupportedTag supportedTag,
        string rawTagName,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        writes.Add(new CustomTagWrite(tagKey, supportedTag, rawTagName, new List<string> { value }));
    }

    private static void ApplyId3CustomTags(
        TagLib.Id3v2.Tag tag,
        List<CustomTagWrite> writes,
        AutoTagRunnerConfig config,
        string separator,
        bool useNullSeparator,
        HashSet<string> enabledTags,
        HashSet<SupportedTag> attemptedTags)
    {
        foreach (var write in writes)
        {
            if (!enabledTags.Contains(write.TagKey) || write.Values.Count == 0)
            {
                continue;
            }

            if (!ShouldOverwriteTag(config, write.SupportedTag) && TagRawProbe.HasId3Raw(tag, write.RawTagName))
            {
                attemptedTags.Add(write.SupportedTag);
                continue;
            }

            SetId3Raw(tag, write.RawTagName, write.Values, separator, useNullSeparator);
            attemptedTags.Add(write.SupportedTag);
        }
    }

    private static void ApplyVorbisCustomTags(TagLib.Ogg.XiphComment tag, List<CustomTagWrite> writes, AutoTagRunnerConfig config, string separator, HashSet<string> enabledTags, HashSet<SupportedTag> attemptedTags)
    {
        foreach (var write in writes)
        {
            if (!enabledTags.Contains(write.TagKey) || write.Values.Count == 0)
            {
                continue;
            }

            if (!ShouldOverwriteTag(config, write.SupportedTag) && TagRawProbe.HasVorbisRaw(tag, write.RawTagName))
            {
                attemptedTags.Add(write.SupportedTag);
                continue;
            }

            SetVorbisRaw(tag, write.RawTagName, write.Values, separator);
            attemptedTags.Add(write.SupportedTag);
        }
    }
}
