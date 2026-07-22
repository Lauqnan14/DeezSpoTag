using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Navidrome;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/library/spotify-cache")]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public class SpotifyCacheApiController : ControllerBase
{
    private const string SpotifySource = "spotify";
    private const string LibraryArtistImagesPath = "library-artist-images";

    private readonly IServiceProvider _serviceProvider;
    private readonly LibraryRepository _libraryRepository;
    private readonly LibraryConfigStore _configStore;
    private readonly ArtistVisualSelectionService _artistVisualSelectionService;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<SpotifyCacheApiController> _logger;

    public SpotifyCacheApiController(
        IServiceProvider serviceProvider,
        LibraryRepository libraryRepository,
        LibraryConfigStore configStore,
        ArtistVisualSelectionService artistVisualSelectionService,
        IWebHostEnvironment environment,
        ILogger<SpotifyCacheApiController> logger)
    {
        _serviceProvider = serviceProvider;
        _libraryRepository = libraryRepository;
        _configStore = configStore;
        _artistVisualSelectionService = artistVisualSelectionService;
        _environment = environment;
        _logger = logger;
    }

    private PlatformAuthService PlatformAuthService => _serviceProvider.GetRequiredService<PlatformAuthService>();
    private PlexApiClient PlexClient => _serviceProvider.GetRequiredService<PlexApiClient>();
    private JellyfinApiClient JellyfinClient => _serviceProvider.GetRequiredService<JellyfinApiClient>();
    private NavidromeApiClient NavidromeClient => _serviceProvider.GetRequiredService<NavidromeApiClient>();
    private ArtistMetadataUpdaterService MetadataUpdaterService => _serviceProvider.GetRequiredService<ArtistMetadataUpdaterService>();
    private ArtistPopularSongsSyncService ArtistPopularSongsSyncService => _serviceProvider.GetRequiredService<ArtistPopularSongsSyncService>();

    [HttpPost("push")]
    public async Task<IActionResult> Push([FromBody] SpotifyCachePushRequest request, CancellationToken cancellationToken)
    {
        var parsed = ParsePushRequest(request);
        if (!string.IsNullOrWhiteSpace(parsed.Error))
        {
            return BadRequest(parsed.Error);
        }

        var push = parsed.Push!;
        var artist = await _libraryRepository.GetArtistAsync(push.ArtistId, cancellationToken);
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
        {
            return NotFound("Artist not found.");
        }
        push = await ApplyDefaultPushVisualsAsync(push, artist, cancellationToken);
        var warnings = BuildPushWarnings(push);

        if (!HasPushPayload(push))
        {
            warnings.Add("Nothing to sync yet. Configure visuals and/or background info, then push again.");
            await TryRegisterManualPushAsync(push, artist.Name, warnings, cancellationToken);
            return Ok(new
            {
                noOp = true,
                updated = false,
                avatarUpdated = false,
                backgroundUpdated = false,
                bioUpdated = false,
                warnings
            });
        }

        var visuals = await MaterializePushVisualsAsync(push, warnings, cancellationToken);

        await PersistArtistVisualPathsAsync(push.ArtistId, visuals, cancellationToken);
        var auth = await PlatformAuthService.LoadAsync();
        var target = ResolvePushTarget(push.Targets, push.Target);
        var updates = new PushUpdateState();
        var context = new PushExecutionContext(artist.Name, visuals, push.Biography);

        await PushToPlexAsync(target.IncludePlex, auth.Plex, context, updates, warnings, cancellationToken);
        await PushToJellyfinAsync(target.IncludeJellyfin, auth.Jellyfin, context, updates, warnings, cancellationToken);
        await PushToNavidromeAsync(target.IncludeNavidrome, auth.Navidrome, context, updates, warnings, cancellationToken);
        await TryRegisterManualPushAsync(push, artist.Name, warnings, cancellationToken);

        return Ok(new
        {
            updated = updates.Updated,
            avatarUpdated = updates.AvatarUpdated,
            backgroundUpdated = updates.BackgroundUpdated,
            bioUpdated = updates.BioUpdated,
            warnings
        });
    }

    private async Task<PreparedPushRequest> ApplyDefaultPushVisualsAsync(
        PreparedPushRequest push,
        ArtistDetailDto artist,
        CancellationToken cancellationToken)
    {
        var avatarVisual = push.AvatarVisual;
        if (push.IncludeAvatar && avatarVisual is null)
        {
            avatarVisual = await ResolveDefaultPushVisualAsync(
                artist.Id,
                "avatar",
                artist.PreferredImagePath,
                cancellationToken);
        }

        var backgroundVisual = push.BackgroundVisual;
        if (push.IncludeBackground && backgroundVisual is null)
        {
            backgroundVisual = await ResolveDefaultPushVisualAsync(
                artist.Id,
                "background",
                artist.PreferredBackgroundPath,
                cancellationToken);
        }

        return push with
        {
            AvatarVisual = avatarVisual,
            BackgroundVisual = backgroundVisual
        };
    }

    private async Task<ResolvedArtistVisualSelection?> ResolveDefaultPushVisualAsync(
        long artistId,
        string slot,
        string? preferredPath,
        CancellationToken cancellationToken)
    {
        var preferredLocalPath = NormalizeExistingFilePath(preferredPath);
        if (!string.IsNullOrWhiteSpace(preferredLocalPath))
        {
            return new ResolvedArtistVisualSelection(preferredLocalPath, null);
        }

        var slotPath = ResolveManagedSlotPath(artistId, slot);
        if (!string.IsNullOrWhiteSpace(slotPath))
        {
            return new ResolvedArtistVisualSelection(slotPath, null);
        }

        var spotifySourceId = await _libraryRepository.GetArtistSourceIdAsync(artistId, SpotifySource, cancellationToken);
        if (string.IsNullOrWhiteSpace(spotifySourceId))
        {
            return null;
        }

        var cacheRoot = Path.Join(AppDataPaths.GetDataRoot(_environment), LibraryArtistImagesPath, SpotifySource);
        if (!Directory.Exists(cacheRoot))
        {
            return null;
        }

        try
        {
            var cacheMatch = Directory.GetFiles(cacheRoot, $"*{spotifySourceId}.*", SearchOption.TopDirectoryOnly)
                .Where(System.IO.File.Exists)
                .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(cacheMatch))
            {
                return null;
            }

            return new ResolvedArtistVisualSelection(Path.GetFullPath(cacheMatch), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to resolve default {Slot} visual for artist {ArtistId}", slot, artistId);
            }
            return null;
        }
    }

    private string? ResolveManagedSlotPath(long artistId, string slot)
    {
        var visualDir = Path.Join(
            AppDataPaths.GetDataRoot(_environment),
            LibraryArtistImagesPath,
            SpotifySource,
            "artists",
            artistId.ToString());
        if (!Directory.Exists(visualDir))
        {
            return null;
        }

        return Directory.GetFiles(visualDir, $"{slot}.*", SearchOption.TopDirectoryOnly)
            .Where(System.IO.File.Exists)
            .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc)
            .Select(Path.GetFullPath)
            .FirstOrDefault();
    }

    private static string? NormalizeExistingFilePath(string? candidatePath)
    {
        var value = (candidatePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(value);
            return System.IO.File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private (PreparedPushRequest? Push, string? Error) ParsePushRequest(SpotifyCachePushRequest? request)
    {
        if (request is null || !request.ArtistId.HasValue || request.ArtistId.Value <= 0)
        {
            return (null, "ArtistId is required.");
        }

        var artistId = request.ArtistId.Value;
        var includeAvatar = request.IncludeAvatar ?? true;
        var includeBackground = request.IncludeBackground ?? true;
        var includeBio = request.IncludeBio == true;
        var managedVisualRoot = Path.GetFullPath(Path.Join(
            AppDataPaths.GetDataRoot(_environment),
            LibraryArtistImagesPath,
            SpotifySource));

        var avatarVisual = includeAvatar
            ? ArtistVisualSelectionService.ResolveVisualSelection(managedVisualRoot, request.AvatarImagePath, request.AvatarVisualUrl)
            : null;
        var backgroundVisual = includeBackground
            ? ArtistVisualSelectionService.ResolveVisualSelection(managedVisualRoot, request.BackgroundImagePath, request.BackgroundVisualUrl)
            : null;
        var biography = includeBio ? (request.Biography ?? string.Empty).Trim() : null;

        return (
            new PreparedPushRequest(
                artistId,
                includeAvatar,
                includeBackground,
                includeBio,
                request.Target,
                request.Targets,
                request.RenewIntervalDays,
                biography,
                avatarVisual,
                backgroundVisual),
            null);
    }

    private static List<string> BuildPushWarnings(PreparedPushRequest push)
    {
        var warnings = new List<string>();
        if (push.IncludeAvatar && push.AvatarVisual is null)
        {
            warnings.Add("Avatar is not set in app visuals, so avatar push was skipped.");
        }

        if (push.IncludeBackground && push.BackgroundVisual is null)
        {
            warnings.Add("Background art is not set in app visuals, so background push was skipped.");
        }

        if (push.IncludeBio && string.IsNullOrWhiteSpace(push.Biography))
        {
            warnings.Add("Background info is empty, so background info push was skipped.");
        }

        return warnings;
    }

    private static bool HasPushPayload(PreparedPushRequest push)
        => push.AvatarVisual is not null
           || push.BackgroundVisual is not null
           || !string.IsNullOrWhiteSpace(push.Biography);

    private async Task<MaterializedPushVisuals> MaterializePushVisualsAsync(
        PreparedPushRequest push,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var avatarVisual = await MaterializeVisualAsync(push.ArtistId, "avatar", push.AvatarVisual, warnings, cancellationToken);
        var backgroundVisual = await MaterializeVisualAsync(push.ArtistId, "background", push.BackgroundVisual, warnings, cancellationToken);
        return new MaterializedPushVisuals(avatarVisual, backgroundVisual);
    }

    private async Task<ResolvedArtistVisualSelection?> MaterializeVisualAsync(
        long artistId,
        string slot,
        ResolvedArtistVisualSelection? visual,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (visual is null)
        {
            return null;
        }

        var materialized = await _artistVisualSelectionService.StoreVisualAsync(artistId, SpotifySource, slot, visual, cancellationToken);
        if (!string.IsNullOrWhiteSpace(materialized.Warning))
        {
            warnings.Add(materialized.Warning);
        }

        return materialized.Selection;
    }

    private async Task PersistArtistVisualPathsAsync(long artistId, MaterializedPushVisuals visuals, CancellationToken cancellationToken)
    {
        var artistIds = await ResolveLinkedArtistIdsAsync(artistId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(visuals.AvatarVisual?.LocalPath))
        {
            foreach (var linkedArtistId in artistIds)
            {
                await _libraryRepository.UpdateArtistImagePathAsync(linkedArtistId, visuals.AvatarVisual.LocalPath!, cancellationToken);
            }
        }

        if (!string.IsNullOrWhiteSpace(visuals.BackgroundVisual?.LocalPath))
        {
            foreach (var linkedArtistId in artistIds)
            {
                await _libraryRepository.UpdateArtistBackgroundPathAsync(linkedArtistId, visuals.BackgroundVisual.LocalPath!, cancellationToken);
            }
        }
    }

    private async Task<IReadOnlyCollection<long>> ResolveLinkedArtistIdsAsync(long artistId, CancellationToken cancellationToken)
    {
        var artistIds = new HashSet<long> { artistId };
        foreach (var source in new[] { "spotify", "deezer", "apple" })
        {
            var sourceId = await _libraryRepository.GetArtistSourceIdAsync(artistId, source, cancellationToken);
            if (string.IsNullOrWhiteSpace(sourceId))
            {
                continue;
            }

            var linkedIds = await _libraryRepository.GetArtistIdsBySourceIdAsync(source, sourceId, cancellationToken);
            foreach (var linkedId in linkedIds)
            {
                artistIds.Add(linkedId);
            }
        }

        return artistIds;
    }

    private static PushTarget ResolvePushTarget(IReadOnlyList<string>? targets, string? target)
    {
        var values = NormalizeTargets(targets, target);
        return new PushTarget(
            IncludePlex: values.Contains("plex", StringComparer.OrdinalIgnoreCase),
            IncludeJellyfin: values.Contains("jellyfin", StringComparer.OrdinalIgnoreCase),
            IncludeNavidrome: values.Contains("navidrome", StringComparer.OrdinalIgnoreCase),
            Targets: values);
    }

    private static IReadOnlyList<string> NormalizeTargets(IReadOnlyList<string>? targets, string? legacyTarget)
    {
        var normalized = new List<string>();
        if (targets is not null)
        {
            foreach (var target in targets)
            {
                AddNormalizedTarget(normalized, target);
            }
        }

        if (normalized.Count == 0)
        {
            AddNormalizedTarget(normalized, legacyTarget);
        }

        return normalized.Count == 0 ? new[] { "plex" } : normalized;
    }

    private static void AddNormalizedTarget(List<string> targets, string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized == "both")
        {
            AddTargetIfMissing(targets, "plex");
            AddTargetIfMissing(targets, "jellyfin");
            return;
        }

        if (normalized is "plex" or "jellyfin" or "navidrome")
        {
            AddTargetIfMissing(targets, normalized);
        }
    }

    private static void AddTargetIfMissing(List<string> targets, string target)
    {
        if (!targets.Contains(target, StringComparer.OrdinalIgnoreCase))
        {
            targets.Add(target);
        }
    }

    private async Task PushToPlexAsync(
        bool includePlex,
        PlexAuth? plex,
        PushExecutionContext context,
        PushUpdateState updates,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!includePlex)
        {
            return;
        }

        if (plex is null || string.IsNullOrWhiteSpace(plex.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            warnings.Add("Plex is not configured.");
            return;
        }

        try
        {
            var plexLocations = await PlexClient.FindArtistLocationsAsync(plex.Url, plex.Token, context.ArtistName, cancellationToken);
            if (plexLocations.Count == 0)
            {
                warnings.Add("Plex artist not found.");
                return;
            }

            foreach (var plexLocation in plexLocations)
            {
                var avatarUpdated = await PushPlexAvatarAsync(plex, plexLocation, context, cancellationToken);
                updates.AvatarUpdated = avatarUpdated || updates.AvatarUpdated;

                var backgroundUpdated = await PushPlexBackgroundAsync(plex, plexLocation, context, cancellationToken);
                updates.BackgroundUpdated = backgroundUpdated || updates.BackgroundUpdated;

                if (avatarUpdated || backgroundUpdated)
                {
                    var locked = await PlexClient.LockArtistArtworkAsync(
                        plex.Url!,
                        plex.Token!,
                        plexLocation.SectionKey,
                        plexLocation.RatingKey,
                        lockPoster: avatarUpdated,
                        lockBackground: backgroundUpdated,
                        cancellationToken);
                    if (!locked)
                    {
                        warnings.Add("Plex artwork lock failed; Plex may revert avatar/background on refresh.");
                    }
                }

                updates.BioUpdated = await PushPlexBiographyAsync(plex, plexLocation, context, cancellationToken) || updates.BioUpdated;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to push artist data to Plex for {Artist}", context.ArtistName);
            warnings.Add("Plex update failed.");
        }
    }

    private async Task<bool> PushPlexAvatarAsync(
        PlexAuth plex,
        PlexArtistLocation plexLocation,
        PushExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (context.Visuals.AvatarVisual is null)
        {
            return false;
        }

        var avatar = context.Visuals.AvatarVisual;
        if (!string.IsNullOrWhiteSpace(avatar.LocalPath))
        {
            return await PlexClient.UpdateArtistPosterFromFileAsync(
                plex.Url!,
                plex.Token!,
                plexLocation.RatingKey,
                avatar.LocalPath!,
                cancellationToken);
        }

        var posterUrl = ResolvePlexImageUrl(avatar, true);
        return !string.IsNullOrWhiteSpace(posterUrl)
               && await PlexClient.UpdateArtistPosterAsync(plex.Url!, plex.Token!, plexLocation.RatingKey, posterUrl, cancellationToken);
    }

    private async Task<bool> PushPlexBackgroundAsync(
        PlexAuth plex,
        PlexArtistLocation plexLocation,
        PushExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (context.Visuals.BackgroundVisual is null)
        {
            return false;
        }

        var background = context.Visuals.BackgroundVisual;
        if (!string.IsNullOrWhiteSpace(background.LocalPath))
        {
            return await PlexClient.UpdateArtistArtFromFileAsync(
                plex.Url!,
                plex.Token!,
                plexLocation.RatingKey,
                background.LocalPath!,
                cancellationToken);
        }

        var artUrl = ResolvePlexImageUrl(background, false);
        return !string.IsNullOrWhiteSpace(artUrl)
               && await PlexClient.UpdateArtistArtAsync(plex.Url!, plex.Token!, plexLocation.RatingKey, artUrl, cancellationToken);
    }

    private async Task<bool> PushPlexBiographyAsync(
        PlexAuth plex,
        PlexArtistLocation plexLocation,
        PushExecutionContext context,
        CancellationToken cancellationToken)
    {
        return !string.IsNullOrWhiteSpace(context.Biography)
               && await PlexClient.UpdateArtistBiographyAsync(
                   plex.Url!,
                   plex.Token!,
                   plexLocation.SectionKey,
                   plexLocation.RatingKey,
                   context.Biography,
                   cancellationToken);
    }

    private async Task PushToJellyfinAsync(
        bool includeJellyfin,
        JellyfinAuth? jellyfin,
        PushExecutionContext context,
        PushUpdateState updates,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!includeJellyfin)
        {
            return;
        }

        if (jellyfin is null || string.IsNullOrWhiteSpace(jellyfin.Url) || string.IsNullOrWhiteSpace(jellyfin.ApiKey))
        {
            warnings.Add("Jellyfin is not configured.");
            return;
        }

        try
        {
            var jellyfinIds = await JellyfinClient.FindArtistIdsAsync(jellyfin.Url, jellyfin.ApiKey, context.ArtistName, cancellationToken);
            if (jellyfinIds.Count == 0)
            {
                warnings.Add("Jellyfin artist not found.");
                return;
            }

            foreach (var jellyfinId in jellyfinIds)
            {
                updates.AvatarUpdated = await PushJellyfinAvatarAsync(jellyfin, jellyfinId, context, warnings, cancellationToken) || updates.AvatarUpdated;
                updates.BackgroundUpdated = await PushJellyfinBackgroundAsync(jellyfin, jellyfinId, context, warnings, cancellationToken) || updates.BackgroundUpdated;
                updates.BioUpdated = await PushJellyfinBiographyAsync(jellyfin, jellyfinId, context, cancellationToken) || updates.BioUpdated;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to push artist data to Jellyfin for {Artist}", context.ArtistName);
            warnings.Add("Jellyfin update failed.");
        }
    }

    private async Task<bool> PushJellyfinAvatarAsync(
        JellyfinAuth jellyfin,
        string jellyfinId,
        PushExecutionContext context,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var avatar = context.Visuals.AvatarVisual;
        if (avatar is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(avatar.LocalPath))
        {
            warnings.Add("Avatar is not a local cached image, so Jellyfin avatar was skipped.");
            return false;
        }

        return await JellyfinClient.UpdateArtistImageAsync(jellyfin.Url!, jellyfin.ApiKey!, jellyfinId, avatar.LocalPath, cancellationToken);
    }

    private async Task<bool> PushJellyfinBackgroundAsync(
        JellyfinAuth jellyfin,
        string jellyfinId,
        PushExecutionContext context,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var background = context.Visuals.BackgroundVisual;
        if (background is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(background.LocalPath))
        {
            warnings.Add("Background is not a local cached image, so Jellyfin background was skipped.");
            return false;
        }

        return await JellyfinClient.UpdateArtistBackdropAsync(jellyfin.Url!, jellyfin.ApiKey!, jellyfinId, background.LocalPath, cancellationToken);
    }

    private async Task<bool> PushJellyfinBiographyAsync(
        JellyfinAuth jellyfin,
        string jellyfinId,
        PushExecutionContext context,
        CancellationToken cancellationToken)
    {
        return !string.IsNullOrWhiteSpace(context.Biography)
               && await JellyfinClient.UpdateArtistOverviewAsync(
                   jellyfin.Url!,
                   jellyfin.ApiKey!,
                   jellyfinId,
                   context.Biography,
                   cancellationToken);
    }

    private async Task PushToNavidromeAsync(
        bool includeNavidrome,
        NavidromeAuth? navidrome,
        PushExecutionContext context,
        PushUpdateState updates,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!includeNavidrome)
        {
            return;
        }

        if (navidrome is null
            || string.IsNullOrWhiteSpace(navidrome.Url)
            || string.IsNullOrWhiteSpace(navidrome.Username)
            || string.IsNullOrWhiteSpace(navidrome.Password))
        {
            warnings.Add("Navidrome is not configured.");
            return;
        }

        try
        {
            var artistIds = await NavidromeClient.FindArtistIdsAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                context.ArtistName,
                cancellationToken);
            if (artistIds.Count == 0)
            {
                warnings.Add("Navidrome artist not found.");
                return;
            }

            var navidromeImage = context.Visuals.AvatarVisual is { LocalPath: { Length: > 0 } }
                ? context.Visuals.AvatarVisual
                : context.Visuals.BackgroundVisual is { LocalPath: { Length: > 0 } }
                    ? context.Visuals.BackgroundVisual
                    : null;

            if (navidromeImage is not null)
            {
                foreach (var artistId in artistIds)
                {
                    updates.AvatarUpdated = await NavidromeClient.UpdateArtistImageFromFileAsync(
                        navidrome.Url,
                        navidrome.Username,
                        navidrome.Password,
                        artistId,
                        navidromeImage.LocalPath!,
                        null,
                        cancellationToken) || updates.AvatarUpdated;
                }
            }

            var navidromeBiographyAvailable = false;
            var navidromeBackgroundAvailable = false;
            foreach (var artistId in artistIds)
            {
                var artistInfo = await NavidromeClient.GetArtistInfoAsync(
                    navidrome.Url,
                    navidrome.Username,
                    navidrome.Password,
                    artistId,
                    cancellationToken);
                navidromeBiographyAvailable = navidromeBiographyAvailable
                    || !string.IsNullOrWhiteSpace(artistInfo?.Biography);
                navidromeBackgroundAvailable = navidromeBackgroundAvailable
                    || !string.IsNullOrWhiteSpace(artistInfo?.LargeImageUrl);
            }
            updates.BioUpdated = navidromeBiographyAvailable || updates.BioUpdated;
            updates.BackgroundUpdated = navidromeBackgroundAvailable || updates.BackgroundUpdated;

            var scanStarted = await NavidromeClient.StartScanAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                cancellationToken);
            updates.NavidromeScanTriggered = scanStarted || updates.NavidromeScanTriggered;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to request Navidrome scan for {Artist}", context.ArtistName);
            warnings.Add("Navidrome scan request failed.");
        }
    }

    private async Task TryRegisterManualPushAsync(
        PreparedPushRequest push,
        string artistName,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            await MetadataUpdaterService.RegisterFromManualPushAsync(
                new ManualPushRegistrationRequest
                {
                    ArtistId = push.ArtistId,
                    ArtistName = artistName,
                    Target = push.Target,
                    Targets = push.Targets?.ToList(),
                    IncludeAvatar = push.IncludeAvatar,
                    IncludeBackground = push.IncludeBackground,
                    IncludeBio = push.IncludeBio,
                    IntervalDays = push.RenewIntervalDays
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to register artist {ArtistId} in metadata updater.", push.ArtistId);
            warnings.Add("Metadata updater registration failed.");
        }
    }

    [HttpGet("artist-metadata/capabilities")]
    public IActionResult ArtistMetadataCapabilities()
    {
        return Ok(new[]
        {
            new
            {
                server = "plex",
                canAuditArtist = true,
                canUpdateAvatar = true,
                canUpdateBiography = true,
                canUpdateBackground = true,
                canTriggerLibraryScan = true,
                limitationReason = (string?)null
            },
            new
            {
                server = "jellyfin",
                canAuditArtist = true,
                canUpdateAvatar = true,
                canUpdateBiography = true,
                canUpdateBackground = true,
                canTriggerLibraryScan = true,
                limitationReason = (string?)null
            },
            new
            {
                server = "navidrome",
                canAuditArtist = true,
                canUpdateAvatar = true,
                canUpdateBiography = false,
                canUpdateBackground = true,
                canTriggerLibraryScan = true,
                limitationReason = (string?)"Navidrome exposes one artist image slot; the large artist image is used as the background-equivalent. Biography can be refreshed/read through getArtistInfo2, but Navidrome does not expose an HTTP biography write endpoint."
            }
        });
    }

    [HttpGet("artist-metadata/audit")]
    public async Task<IActionResult> ArtistMetadataAudit(CancellationToken cancellationToken)
    {
        var auth = await PlatformAuthService.LoadAsync();
        var artists = await _libraryRepository.GetArtistsAsync("all", cancellationToken);
        var results = new List<object>();
        foreach (var artist in artists.Where(static artist => artist.Id > 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasAvatar = HasExistingFile(artist.PreferredImagePath);
            var hasBackground = HasExistingFile(artist.PreferredBackgroundPath);
            var hasBiography = !string.IsNullOrWhiteSpace(artist.AppleBiography);
            results.Add(new
            {
                artistId = artist.Id,
                artistName = artist.Name,
                cacheHasAvatar = hasAvatar,
                cacheHasBiography = hasBiography,
                cacheHasBackground = hasBackground,
                missingAvatar = !hasAvatar,
                missingBiography = !hasBiography,
                missingBackground = !hasBackground,
                servers = new
                {
                    plex = await AuditPlexArtistAsync(auth.Plex, artist.Name, cancellationToken),
                    jellyfin = await AuditJellyfinArtistAsync(auth.Jellyfin, artist.Name, cancellationToken),
                    navidrome = await AuditNavidromeArtistAsync(auth.Navidrome, artist.Name, cancellationToken)
                }
            });
        }

        return Ok(new
        {
            totalArtists = results.Count,
            missingAvatar = artists.Count(artist => !HasExistingFile(artist.PreferredImagePath)),
            missingBiography = artists.Count(artist => string.IsNullOrWhiteSpace(artist.AppleBiography)),
            missingBackground = artists.Count(artist => !HasExistingFile(artist.PreferredBackgroundPath)),
            artists = results
        });
    }

    private async Task<object> AuditPlexArtistAsync(PlexAuth? plex, string artistName, CancellationToken cancellationToken)
    {
        if (plex is null || string.IsNullOrWhiteSpace(plex.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            return new
            {
                capabilitySupported = true,
                canAuditArtist = false,
                canUpdateAvatar = true,
                canUpdateBiography = true,
                canUpdateBackground = true,
                hasAvatar = (bool?)null,
                hasBiography = (bool?)null,
                hasBackground = (bool?)null,
                message = "Plex is not configured."
            };
        }

        var locations = await PlexClient.FindArtistLocationsAsync(plex.Url, plex.Token, artistName, cancellationToken);
        if (locations.Count == 0)
        {
            return new
            {
                capabilitySupported = true,
                canAuditArtist = true,
                canUpdateAvatar = true,
                canUpdateBiography = true,
                canUpdateBackground = true,
                hasAvatar = (bool?)false,
                hasBiography = (bool?)false,
                hasBackground = (bool?)false,
                message = "Plex artist not found."
            };
        }

        var metadata = await PlexClient.GetArtistMetadataAsync(plex.Url, plex.Token, locations[0].RatingKey, cancellationToken);
        return new
        {
            capabilitySupported = true,
            canAuditArtist = metadata is not null,
            canUpdateAvatar = true,
            canUpdateBiography = true,
            canUpdateBackground = true,
            hasAvatar = metadata is null ? (bool?)null : !string.IsNullOrWhiteSpace(metadata.Thumb),
            hasBiography = metadata is null ? (bool?)null : !string.IsNullOrWhiteSpace(metadata.Summary),
            hasBackground = metadata is null ? (bool?)null : !string.IsNullOrWhiteSpace(metadata.Art),
            message = metadata is null ? "Plex artist metadata could not be read." : null
        };
    }

    private async Task<object> AuditJellyfinArtistAsync(JellyfinAuth? jellyfin, string artistName, CancellationToken cancellationToken)
    {
        if (jellyfin is null
            || string.IsNullOrWhiteSpace(jellyfin.Url)
            || string.IsNullOrWhiteSpace(jellyfin.ApiKey)
            || string.IsNullOrWhiteSpace(jellyfin.UserId))
        {
            return new
            {
                capabilitySupported = true,
                canAuditArtist = false,
                canUpdateAvatar = true,
                canUpdateBiography = true,
                canUpdateBackground = true,
                hasAvatar = (bool?)null,
                hasBiography = (bool?)null,
                hasBackground = (bool?)null,
                message = "Jellyfin is not configured."
            };
        }

        var artistIds = await JellyfinClient.FindArtistIdsAsync(jellyfin.Url, jellyfin.ApiKey, artistName, cancellationToken);
        if (artistIds.Count == 0)
        {
            return new
            {
                capabilitySupported = true,
                canAuditArtist = true,
                canUpdateAvatar = true,
                canUpdateBiography = true,
                canUpdateBackground = true,
                hasAvatar = (bool?)false,
                hasBiography = (bool?)false,
                hasBackground = (bool?)false,
                message = "Jellyfin artist not found."
            };
        }

        var item = await JellyfinClient.GetItemAsync(jellyfin.Url, jellyfin.ApiKey, jellyfin.UserId, artistIds[0], cancellationToken);
        return new
        {
            capabilitySupported = true,
            canAuditArtist = item is not null,
            canUpdateAvatar = true,
            canUpdateBiography = true,
            canUpdateBackground = true,
            hasAvatar = item is null ? (bool?)null : item.ImageTags?.ContainsKey("Primary") == true,
            hasBiography = item is null ? (bool?)null : !string.IsNullOrWhiteSpace(item.Overview),
            hasBackground = item is null ? (bool?)null : item.BackdropImageTags?.Count > 0,
            message = item is null ? "Jellyfin artist metadata could not be read." : null
        };
    }

    private async Task<object> AuditNavidromeArtistAsync(NavidromeAuth? navidrome, string artistName, CancellationToken cancellationToken)
    {
        if (navidrome is null
            || string.IsNullOrWhiteSpace(navidrome.Url)
            || string.IsNullOrWhiteSpace(navidrome.Username)
            || string.IsNullOrWhiteSpace(navidrome.Password))
        {
            return new
            {
                capabilitySupported = true,
                canAuditArtist = false,
                canUpdateAvatar = true,
                canUpdateBiography = false,
                canUpdateBackground = true,
                hasAvatar = (bool?)null,
                hasBiography = (bool?)null,
                hasBackground = (bool?)null,
                message = "Navidrome is not configured."
            };
        }

        var artists = await NavidromeClient.SearchArtistsAsync(
            navidrome.Url,
            navidrome.Username,
            navidrome.Password,
            artistName,
            cancellationToken);
        var match = artists.FirstOrDefault(artist => string.Equals(artist.Name.Trim(), artistName.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? artists.FirstOrDefault();
        var info = match is null
            ? null
            : await NavidromeClient.GetArtistInfoAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                match.Id,
                cancellationToken);
        return new
        {
            capabilitySupported = true,
            canAuditArtist = match is not null,
            canUpdateAvatar = true,
            canUpdateBiography = false,
            canUpdateBackground = true,
            hasAvatar = match is null ? (bool?)false : !string.IsNullOrWhiteSpace(match.CoverArt),
            hasBiography = match is null ? (bool?)false : !string.IsNullOrWhiteSpace(info?.Biography),
            hasBackground = match is null ? (bool?)false : !string.IsNullOrWhiteSpace(info?.LargeImageUrl),
            message = match is null
                ? "Navidrome artist not found."
                : "Navidrome uses one artist image for avatar and large/background display; biography is read from getArtistInfo2 and depends on Navidrome artist-info providers."
        };
    }

    private static bool HasExistingFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return System.IO.File.Exists(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    [HttpGet("artists/{artistId:long}/metadata-policy")]
    public async Task<IActionResult> GetArtistMetadataPolicy(long artistId, CancellationToken cancellationToken)
    {
        var artist = await _libraryRepository.GetArtistAsync(artistId, cancellationToken);
        if (artist is null)
        {
            return NotFound("Artist not found.");
        }

        return Ok(await _libraryRepository.GetArtistMetadataPolicyAsync(artistId, cancellationToken));
    }

    [HttpPost("artists/{artistId:long}/metadata-policy/sync-block")]
    public async Task<IActionResult> SetArtistSyncBlocked(
        long artistId,
        [FromBody] ArtistSyncBlockRequest? request,
        CancellationToken cancellationToken)
    {
        var artist = await _libraryRepository.GetArtistAsync(artistId, cancellationToken);
        if (artist is null)
        {
            return NotFound("Artist not found.");
        }

        await _libraryRepository.SetArtistMetadataSyncBlockedAsync(artistId, request?.Blocked == true, cancellationToken);
        return Ok(await _libraryRepository.GetArtistMetadataPolicyAsync(artistId, cancellationToken));
    }

    [HttpPost("artists/{artistId:long}/artwork/block")]
    public async Task<IActionResult> SetArtistArtworkBlocked(
        long artistId,
        [FromBody] ArtistArtworkBlockRequest? request,
        CancellationToken cancellationToken)
    {
        var artist = await _libraryRepository.GetArtistAsync(artistId, cancellationToken);
        if (artist is null)
        {
            return NotFound("Artist not found.");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Role) || string.IsNullOrWhiteSpace(request.Identity))
        {
            return BadRequest("Role and identity are required.");
        }

        await _libraryRepository.SetArtistArtworkBlockedAsync(
            artistId,
            request.Role.Trim().ToLowerInvariant(),
            request.Identity.Trim(),
            request.Blocked,
            cancellationToken);
        var normalizedRole = request.Role.Trim().ToLowerInvariant();
        foreach (var alias in ResolveArtistArtworkBlockAliases(normalizedRole, request))
        {
            await _libraryRepository.SetArtistArtworkBlockedAsync(
                artistId,
                normalizedRole,
                alias,
                request.Blocked,
                cancellationToken);
        }

        return Ok(new { artistId, request.Role, request.Identity, request.Blocked });
    }

    private static IReadOnlyList<string> ResolveArtistArtworkBlockAliases(
        string role,
        ArtistArtworkBlockRequest request)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(request.VisualUrl))
        {
            aliases.Add($"{ResolveArtistArtworkBlockSourcePrefix(request.VisualSource)}:{request.VisualUrl.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(request.LocalPath))
        {
            try
            {
                var localPath = Path.GetFullPath(request.LocalPath);
                aliases.Add($"file:{localPath}");
                aliases.Add($"slot:{role}:{localPath}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Ignore malformed client-side path aliases; the original identity is still persisted.
            }
        }

        aliases.RemoveWhere(static alias => string.IsNullOrWhiteSpace(alias));
        aliases.Remove(request.Identity?.Trim() ?? string.Empty);
        return aliases.ToList();
    }

    private static string ResolveArtistArtworkBlockSourcePrefix(string? source)
        => string.IsNullOrWhiteSpace(source) ? "visual" : source.Trim().ToLowerInvariant();

    [HttpPost("artists/{artistId:long}/popular-songs/sync")]
    public async Task<IActionResult> SyncArtistPopularSongs(
        long artistId,
        [FromBody] ArtistPopularSongsSyncRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await ArtistPopularSongsSyncService.SyncAsync(
            artistId,
            NormalizeTargets(request?.Targets, request?.Target),
            cancellationToken);
        if (!result.Success)
        {
            return BadRequest(new
            {
                result.Success,
                result.Message,
                result.Targets
            });
        }

        return Ok(result);
    }

    [HttpPost("visuals")]
    public async Task<IActionResult> SaveVisuals([FromBody] SpotifyCacheVisualRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !request.ArtistId.HasValue || request.ArtistId.Value <= 0)
        {
            return BadRequest("ArtistId is required.");
        }

        var result = await _artistVisualSelectionService.SaveAsync(
            request.ArtistId.Value,
            new ArtistVisualSelectionRequest
            {
                AvatarImagePath = request.AvatarImagePath,
                AvatarVisualUrl = request.AvatarVisualUrl,
                BackgroundImagePath = request.BackgroundImagePath,
                BackgroundVisualUrl = request.BackgroundVisualUrl
            },
            cancellationToken);

        if (!result.Success)
        {
            return StatusCode(result.StatusCode, result.Error);
        }

        return Ok(new
        {
            stored = true,
            avatarPath = result.AvatarPath,
            backgroundPath = result.BackgroundPath,
            warnings = result.Warnings
        });
    }

    private string? ResolvePlexImageUrl(ResolvedArtistVisualSelection visual, bool asPoster)
    {
        if (!string.IsNullOrWhiteSpace(visual.RemoteUrl))
        {
            return visual.RemoteUrl;
        }

        if (!string.IsNullOrWhiteSpace(visual.LocalPath))
        {
            return $"{Request.Scheme}://{Request.Host}/api/library/image?path={Uri.EscapeDataString(visual.LocalPath!)}{(asPoster ? "&size=512" : string.Empty)}";
        }

        return null;
    }

    private sealed record PreparedPushRequest(
        long ArtistId,
        bool IncludeAvatar,
        bool IncludeBackground,
        bool IncludeBio,
        string? Target,
        IReadOnlyList<string>? Targets,
        int? RenewIntervalDays,
        string? Biography,
        ResolvedArtistVisualSelection? AvatarVisual,
        ResolvedArtistVisualSelection? BackgroundVisual);
    private sealed record MaterializedPushVisuals(
        ResolvedArtistVisualSelection? AvatarVisual,
        ResolvedArtistVisualSelection? BackgroundVisual);
    private sealed record PushTarget(bool IncludePlex, bool IncludeJellyfin, bool IncludeNavidrome, IReadOnlyList<string> Targets);
    private sealed record PushExecutionContext(
        string ArtistName,
        MaterializedPushVisuals Visuals,
        string? Biography);

    private sealed class PushUpdateState
    {
        public bool AvatarUpdated { get; set; }
        public bool BackgroundUpdated { get; set; }
        public bool BioUpdated { get; set; }
        public bool NavidromeScanTriggered { get; set; }
        public bool Updated => AvatarUpdated || BackgroundUpdated || BioUpdated || NavidromeScanTriggered;
    }
}

public sealed class SpotifyCachePushRequest
{
    public long? ArtistId { get; set; }
    public string? AvatarImagePath { get; set; }
    public string? AvatarVisualUrl { get; set; }
    public string? BackgroundImagePath { get; set; }
    public string? BackgroundVisualUrl { get; set; }
    public string? ImagePath { get; set; }
    public string? Biography { get; set; }
    public bool? IncludeAvatar { get; set; }
    public bool? IncludeBackground { get; set; }
    public bool? IncludeBio { get; set; }
    public string? Target { get; set; }
    public List<string>? Targets { get; set; }
    public int? RenewIntervalDays { get; set; }
}

public sealed class ArtistPopularSongsSyncRequest
{
    public string? Target { get; set; }
    public List<string>? Targets { get; set; }
}

public sealed class ArtistSyncBlockRequest
{
    public bool Blocked { get; set; }
}

public sealed class ArtistArtworkBlockRequest
{
    public string? Role { get; set; }
    public string? Identity { get; set; }
    public string? VisualUrl { get; set; }
    public string? VisualSource { get; set; }
    public string? LocalPath { get; set; }
    public bool Blocked { get; set; }
}

public sealed class SpotifyCacheVisualRequest
{
    public long? ArtistId { get; set; }
    public string? AvatarImagePath { get; set; }
    public string? AvatarVisualUrl { get; set; }
    public string? BackgroundImagePath { get; set; }
    public string? BackgroundVisualUrl { get; set; }
}
