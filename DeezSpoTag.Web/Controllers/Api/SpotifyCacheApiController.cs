using DeezSpoTag.Integrations.Jellyfin;
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
public class SpotifyCacheApiController : ControllerBase
{
    private const string SpotifySource = "spotify";
    private const string ArtistType = "artist";
    private const string LibraryArtistImagesPath = "library-artist-images";

    private readonly IServiceProvider _serviceProvider;
    private readonly LibraryRepository _libraryRepository;
    private readonly LibraryConfigStore _configStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<SpotifyCacheApiController> _logger;

    public SpotifyCacheApiController(
        IServiceProvider serviceProvider,
        LibraryRepository libraryRepository,
        LibraryConfigStore configStore,
        IHttpClientFactory httpClientFactory,
        IWebHostEnvironment environment,
        ILogger<SpotifyCacheApiController> logger)
    {
        _serviceProvider = serviceProvider;
        _libraryRepository = libraryRepository;
        _configStore = configStore;
        _httpClientFactory = httpClientFactory;
        _environment = environment;
        _logger = logger;
    }

    private SpotifyArtistService SpotifyArtistService => _serviceProvider.GetRequiredService<SpotifyArtistService>();
    private ArtistPageCacheRepository ArtistPageCache => _serviceProvider.GetRequiredService<ArtistPageCacheRepository>();
    private SpotifyMetadataCacheRepository SpotifyMetadataCache => _serviceProvider.GetRequiredService<SpotifyMetadataCacheRepository>();
    private PlatformAuthService PlatformAuthService => _serviceProvider.GetRequiredService<PlatformAuthService>();
    private PlexApiClient PlexClient => _serviceProvider.GetRequiredService<PlexApiClient>();
    private JellyfinApiClient JellyfinClient => _serviceProvider.GetRequiredService<JellyfinApiClient>();
    private ArtistMetadataUpdaterService MetadataUpdaterService => _serviceProvider.GetRequiredService<ArtistMetadataUpdaterService>();

    [HttpPost("refresh")]
    public IActionResult Refresh([FromQuery] long? artistId)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Spotify cache refresh requested: artistId={ArtistId}", artistId);
        }
        LogRefreshQueued(artistId);
        _ = Task.Run(() => RunRefreshAsync(artistId));

        return Ok(new { queued = true });
    }

    private void LogRefreshQueued(long? artistId)
    {
        var message = artistId.HasValue
            ? $"Spotify cache refresh queued for artist {artistId.Value}."
            : "Spotify cache refresh queued for all artists.";

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(DateTimeOffset.UtcNow, "info", message));
    }

    private async Task RunRefreshAsync(long? artistId)
    {
        try
        {
            if (artistId.HasValue)
            {
                await RefreshSingleArtistAsync(artistId.Value);
                return;
            }

            await RefreshAllArtistsAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "error",
                $"Spotify cache refresh failed: {ex.Message}"));
        }
    }

    private async Task RefreshSingleArtistAsync(long artistId)
    {
        var artist = await ResolveRefreshArtistAsync(artistId);
        if (artist is null)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warn",
                $"Spotify cache refresh skipped: artist {artistId} not found."));
            return;
        }

        await ClearArtistSpotifyCacheAsync(artist.Id);
        await SpotifyArtistService.GetArtistPageAsync(artist.Id, artist.Name, true, false, CancellationToken.None);
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Spotify cache refresh completed for {artist.Name}."));
    }

    private async Task RefreshAllArtistsAsync()
    {
        var artists = await GetRefreshArtistsAsync();
        foreach (var artist in artists)
        {
            await SpotifyArtistService.GetArtistPageAsync(artist.Id, artist.Name, true, false, CancellationToken.None);
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Spotify cache refresh completed ({artists.Count} artists)."));
    }

    private async Task<List<RefreshArtist>> GetRefreshArtistsAsync()
    {
        if (!_libraryRepository.IsConfigured || _configStore.HasLocalLibraryData())
        {
            return _configStore.GetLocalArtists()
                .Where(artist => !string.IsNullOrWhiteSpace(artist.Name))
                .Select(artist => new RefreshArtist(artist.Id, artist.Name))
                .ToList();
        }

        var artists = await _libraryRepository.GetArtistsAsync("all", CancellationToken.None);
        return artists
            .Where(artist => !string.IsNullOrWhiteSpace(artist.Name))
            .Select(artist => new RefreshArtist(artist.Id, artist.Name))
            .ToList();
    }

    private async Task<RefreshArtist?> ResolveRefreshArtistAsync(long artistId)
    {
        if (!_libraryRepository.IsConfigured || _configStore.HasLocalLibraryData())
        {
            var localArtist = _configStore.GetLocalArtists().FirstOrDefault(item => item.Id == artistId);
            if (localArtist is null || string.IsNullOrWhiteSpace(localArtist.Name))
            {
                return null;
            }

            return new RefreshArtist(localArtist.Id, localArtist.Name);
        }

        var artist = await _libraryRepository.GetArtistAsync(artistId, CancellationToken.None);
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
        {
            return null;
        }

        return new RefreshArtist(artist.Id, artist.Name);
    }

    private async Task ClearArtistSpotifyCacheAsync(long artistId)
    {
        var spotifyId = await _libraryRepository.GetArtistSourceIdAsync(artistId, SpotifySource, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return;
        }

        await ArtistPageCache.ClearEntryAsync(SpotifySource, spotifyId, CancellationToken.None);
        await SpotifyMetadataCache.ClearEntryAsync(ArtistType, spotifyId, CancellationToken.None);
    }

    [HttpGet("images")]
    public async Task<IActionResult> ListImages([FromQuery] long artistId, CancellationToken cancellationToken)
    {
        var artist = await _libraryRepository.GetArtistAsync(artistId, cancellationToken);
        if (artist is null)
        {
            return NotFound();
        }

        var spotifyId = await _libraryRepository.GetArtistSourceIdAsync(artistId, SpotifySource, cancellationToken);
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return Ok(Array.Empty<object>());
        }

        var cacheRoot = Path.Join(AppDataPaths.GetDataRoot(_environment), LibraryArtistImagesPath, SpotifySource);
        if (!Directory.Exists(cacheRoot))
        {
            return Ok(Array.Empty<object>());
        }

        var matches = Directory.GetFiles(cacheRoot, $"*{spotifyId}.*", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => new
            {
                path = info.FullName,
                name = info.Name,
                updatedUtc = info.LastWriteTimeUtc
            })
            .ToList();

        return Ok(matches);
    }

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
        var target = ResolvePushTarget(push.Target);
        var updates = new PushUpdateState();
        var context = new PushExecutionContext(artist.Name, visuals, push.Biography);

        await PushToPlexAsync(target.IncludePlex, auth.Plex, context, updates, warnings, cancellationToken);
        await PushToJellyfinAsync(target.IncludeJellyfin, auth.Jellyfin, context, updates, warnings, cancellationToken);
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

    private async Task<ResolvedVisualSelection?> ResolveDefaultPushVisualAsync(
        long artistId,
        string slot,
        string? preferredPath,
        CancellationToken cancellationToken)
    {
        var preferredLocalPath = NormalizeExistingFilePath(preferredPath);
        if (!string.IsNullOrWhiteSpace(preferredLocalPath))
        {
            return new ResolvedVisualSelection(preferredLocalPath, null);
        }

        var slotPath = ResolveManagedSlotPath(artistId, slot);
        if (!string.IsNullOrWhiteSpace(slotPath))
        {
            return new ResolvedVisualSelection(slotPath, null);
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

            return new ResolvedVisualSelection(Path.GetFullPath(cacheMatch), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve default {Slot} visual for artist {ArtistId}", slot, artistId);
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
            ? ResolveVisualSelection(managedVisualRoot, request.AvatarImagePath, request.AvatarVisualUrl)
            : null;
        var backgroundVisual = includeBackground
            ? ResolveVisualSelection(managedVisualRoot, request.BackgroundImagePath, request.BackgroundVisualUrl)
            : null;
        var biography = includeBio ? (request.Biography ?? string.Empty).Trim() : null;

        if (includeAvatar
            && avatarVisual is null
            && string.IsNullOrWhiteSpace(request.AvatarImagePath)
            && string.IsNullOrWhiteSpace(request.AvatarVisualUrl))
        {
            avatarVisual = ResolveVisualSelection(managedVisualRoot, request.ImagePath, null);
        }

        return (
            new PreparedPushRequest(
                artistId,
                includeAvatar,
                includeBackground,
                includeBio,
                request.Target,
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

    private async Task<ResolvedVisualSelection?> MaterializeVisualAsync(
        long artistId,
        string slot,
        ResolvedVisualSelection? visual,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (visual is null)
        {
            return null;
        }

        var materialized = await EnsureVisualStoredAsync(artistId, slot, visual, cancellationToken);
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

    private static PushTarget ResolvePushTarget(string? target)
    {
        var value = (target ?? "plex").Trim().ToLowerInvariant();
        return new PushTarget(
            IncludePlex: value is "plex" or "both",
            IncludeJellyfin: value is "jellyfin" or "both");
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

    [HttpGet("metadata-updater/status")]
    public IActionResult MetadataUpdaterStatus()
    {
        return Ok(MetadataUpdaterService.GetStatus());
    }

    [HttpPost("metadata-updater/run")]
    public async Task<IActionResult> RunMetadataUpdater([FromBody] MetadataUpdaterRunRequest? request, CancellationToken cancellationToken)
    {
        var queued = await MetadataUpdaterService.EnqueueRunAsync(request ?? new MetadataUpdaterRunRequest(), cancellationToken);
        return Ok(new
        {
            queued,
            status = MetadataUpdaterService.GetStatus()
        });
    }

    [HttpPost("visuals")]
    public async Task<IActionResult> SaveVisuals([FromBody] SpotifyCacheVisualRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !request.ArtistId.HasValue || request.ArtistId.Value <= 0)
        {
            return BadRequest("ArtistId is required.");
        }

        var artistId = request.ArtistId.Value;

        if (!_libraryRepository.IsConfigured)
        {
            return BadRequest("Library DB not configured.");
        }

        var cacheRoot = Path.GetFullPath(Path.Join(AppDataPaths.GetDataRoot(_environment), LibraryArtistImagesPath, SpotifySource));
        var avatarVisual = ResolveVisualSelection(cacheRoot, request.AvatarImagePath, request.AvatarVisualUrl);
        var backgroundVisual = ResolveVisualSelection(cacheRoot, request.BackgroundImagePath, request.BackgroundVisualUrl);

        if (avatarVisual is null && backgroundVisual is null)
        {
            return BadRequest("Set artist avatar or background first.");
        }

        var artist = await _libraryRepository.GetArtistAsync(artistId, cancellationToken);
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
        {
            return NotFound("Artist not found.");
        }

        var warnings = new List<string>();
        var avatarMaterialized = await EnsureVisualStoredAsync(artistId, "avatar", avatarVisual, cancellationToken);
        avatarVisual = avatarMaterialized.Selection;
        if (!string.IsNullOrWhiteSpace(avatarMaterialized.Warning))
        {
            warnings.Add(avatarMaterialized.Warning);
        }

        var backgroundMaterialized = await EnsureVisualStoredAsync(artistId, "background", backgroundVisual, cancellationToken);
        backgroundVisual = backgroundMaterialized.Selection;
        if (!string.IsNullOrWhiteSpace(backgroundMaterialized.Warning))
        {
            warnings.Add(backgroundMaterialized.Warning);
        }

        await PersistArtistVisualPathsAsync(
            artistId,
            new MaterializedPushVisuals(avatarVisual, backgroundVisual),
            cancellationToken);

        return Ok(new
        {
            stored = true,
            avatarPath = avatarVisual?.LocalPath,
            backgroundPath = backgroundVisual?.LocalPath,
            warnings
        });
    }

    private string? ResolvePlexImageUrl(ResolvedVisualSelection visual, bool asPoster)
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

    private static ResolvedVisualSelection? ResolveVisualSelection(string cacheRoot, string? explicitLocalPath, string? visualUrl)
    {
        var localPath = TryResolveLocalVisualPath(cacheRoot, explicitLocalPath, visualUrl);
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            return new ResolvedVisualSelection(localPath, null);
        }

        var urlValue = (visualUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(urlValue))
        {
            return null;
        }

        if (Uri.TryCreate(urlValue, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return new ResolvedVisualSelection(null, urlValue);
        }

        return null;
    }

    private static string? TryResolveLocalVisualPath(string allowedRoot, string? explicitLocalPath, string? visualUrl)
    {
        var localPath = ValidateCachePath(explicitLocalPath, allowedRoot);
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            return localPath;
        }

        var urlValue = (visualUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(urlValue))
        {
            return null;
        }

        var extractedPath = TryExtractPathFromLibraryImageUrl(urlValue);
        return ValidateCachePath(extractedPath, allowedRoot);
    }

    private static string? ValidateCachePath(string? candidatePath, string cacheRoot)
    {
        var value = (candidatePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(value);
            if (!IsPathWithinRoot(fullPath, cacheRoot))
            {
                return null;
            }

            return System.IO.File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);

        return !relative.StartsWith("..", StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }

    private static string? TryExtractPathFromLibraryImageUrl(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var queryIndex = trimmed.IndexOf('?');
        if (queryIndex < 0)
        {
            return null;
        }

        var endpoint = trimmed[..queryIndex];
        if (endpoint.IndexOf("/api/library/image", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }

        var query = trimmed[(queryIndex + 1)..];
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var segments = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var equalsIndex = segment.IndexOf('=');
            var rawKey = equalsIndex >= 0 ? segment[..equalsIndex] : segment;
            var key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
            if (!string.Equals(key, "path", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawValue = equalsIndex >= 0 ? segment[(equalsIndex + 1)..] : string.Empty;
            var decoded = Uri.UnescapeDataString(rawValue.Replace('+', ' ')).Trim();
            return string.IsNullOrWhiteSpace(decoded) ? null : decoded;
        }

        return null;
    }

    private async Task<MaterializedVisual> EnsureVisualStoredAsync(long artistId, string slot, ResolvedVisualSelection? selection, CancellationToken cancellationToken)
    {
        if (selection is null)
        {
            return new MaterializedVisual(null, null);
        }

        var visualDir = Path.Join(AppDataPaths.GetDataRoot(_environment), LibraryArtistImagesPath, SpotifySource, "artists", artistId.ToString());
        Directory.CreateDirectory(visualDir);

        if (!string.IsNullOrWhiteSpace(selection.LocalPath))
        {
            var sourcePath = selection.LocalPath!;
            if (System.IO.File.Exists(sourcePath))
            {
                var extension = ImageFileExtensionResolver.NormalizeStandardImageExtension(Path.GetExtension(sourcePath));
                var targetPath = Path.Join(visualDir, $"{slot}{extension}");
                DeleteVisualSlotFiles(visualDir, slot, targetPath);

                if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                {
                    System.IO.File.Copy(sourcePath, targetPath, true);
                }

                return new MaterializedVisual(new ResolvedVisualSelection(targetPath, null), null);
            }
        }

        if (!string.IsNullOrWhiteSpace(selection.RemoteUrl))
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                using var response = await client.GetAsync(selection.RemoteUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return new MaterializedVisual(
                        selection,
                        $"Failed to download {slot} visual ({(int)response.StatusCode}); used remote source where possible.");
                }

                var extension = ImageFileExtensionResolver.ResolveStandardImageExtension(
                    response.Content.Headers.ContentType?.MediaType,
                    selection.RemoteUrl);
                var targetPath = Path.Join(visualDir, $"{slot}{extension}");
                DeleteVisualSlotFiles(visualDir, slot, targetPath);

                await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var targetStream = System.IO.File.Create(targetPath);
                await sourceStream.CopyToAsync(targetStream, cancellationToken);

                return new MaterializedVisual(new ResolvedVisualSelection(targetPath, null), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to materialize {Slot} visual for artist {ArtistId}", slot, artistId);
                return new MaterializedVisual(selection, $"Failed to download {slot} visual; used remote source where possible.");
            }
        }

        return new MaterializedVisual(selection, null);
    }

    private static void DeleteVisualSlotFiles(string directory, string slot, string keepPath)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var files = Directory.GetFiles(directory, $"{slot}.*", SearchOption.TopDirectoryOnly);
        foreach (var file in files)
        {
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(keepPath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                System.IO.File.Delete(file);
            }
            catch (IOException ex)
            {
                System.Diagnostics.Trace.TraceWarning("Failed to remove stale {0} visual file '{1}': {2}", slot, file, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Trace.TraceWarning("Access denied removing stale {0} visual file '{1}': {2}", slot, file, ex.Message);
            }
        }
    }

    private sealed record RefreshArtist(long Id, string Name);
    private sealed record PreparedPushRequest(
        long ArtistId,
        bool IncludeAvatar,
        bool IncludeBackground,
        bool IncludeBio,
        string? Target,
        int? RenewIntervalDays,
        string? Biography,
        ResolvedVisualSelection? AvatarVisual,
        ResolvedVisualSelection? BackgroundVisual);
    private sealed record MaterializedPushVisuals(
        ResolvedVisualSelection? AvatarVisual,
        ResolvedVisualSelection? BackgroundVisual);
    private sealed record PushTarget(bool IncludePlex, bool IncludeJellyfin);
    private sealed record PushExecutionContext(
        string ArtistName,
        MaterializedPushVisuals Visuals,
        string? Biography);

    private sealed class PushUpdateState
    {
        public bool AvatarUpdated { get; set; }
        public bool BackgroundUpdated { get; set; }
        public bool BioUpdated { get; set; }
        public bool Updated => AvatarUpdated || BackgroundUpdated || BioUpdated;
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
    public int? RenewIntervalDays { get; set; }
}

public sealed class SpotifyCacheVisualRequest
{
    public long? ArtistId { get; set; }
    public string? AvatarImagePath { get; set; }
    public string? AvatarVisualUrl { get; set; }
    public string? BackgroundImagePath { get; set; }
    public string? BackgroundVisualUrl { get; set; }
}

internal sealed record ResolvedVisualSelection(string? LocalPath, string? RemoteUrl);
internal sealed record MaterializedVisual(ResolvedVisualSelection? Selection, string? Warning);
