using DeezSpoTag.Services.Download.Utils;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyLyricsAuthProvider : ISpotifyLyricsAuthProvider
{
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36";

    private readonly SpotifyUserAuthStore _userAuthStore;
    private readonly PlatformAuthService _platformAuthService;
    private readonly ISpotifyUserContextAccessor _userContext;
    private readonly SpotifyBlobService _blobService;
    private readonly ILogger<SpotifyLyricsAuthProvider> _logger;

    public SpotifyLyricsAuthProvider(
        SpotifyUserAuthStore userAuthStore,
        PlatformAuthService platformAuthService,
        ISpotifyUserContextAccessor userContext,
        SpotifyBlobService blobService,
        ILogger<SpotifyLyricsAuthProvider> logger)
    {
        _userAuthStore = userAuthStore;
        _platformAuthService = platformAuthService;
        _userContext = userContext;
        _blobService = blobService;
        _logger = logger;
    }

    public async Task<SpotifyLyricsAuthToken?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var userId = _userContext.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("Spotify lyrics authentication has no active user context.");
            return null;
        }

        var state = await _userAuthStore.LoadAuthoritativeAsync(userId, _platformAuthService);
        var account = SpotifyUserAuthStore.ResolveActiveAccount(state);
        if (account is null)
        {
            _logger.LogWarning("Spotify lyrics authentication has no active authenticated account.");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(account.WebPlayerBlobPath))
        {
            var token = await _blobService.GetWebPlayerTokenInfoAsync(
                account.WebPlayerBlobPath,
                cancellationToken);
            return string.IsNullOrWhiteSpace(token?.AccessToken)
                ? null
                : new SpotifyLyricsAuthToken(
                    token.AccessToken,
                    token.Country,
                    DefaultUserAgent);
        }

        var librespotBlobPath = SpotifyUserAuthStore.ResolveActiveLibrespotBlobPath(state);
        if (string.IsNullOrWhiteSpace(librespotBlobPath))
        {
            _logger.LogWarning(
                "Spotify lyrics authentication account {Account} has no usable authenticated blob.",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(account.Name));
            return null;
        }

        var librespotToken = await _blobService.GetWebApiAccessTokenAsync(
            librespotBlobPath,
            allowRetries: false,
            cancellationToken);
        return string.IsNullOrWhiteSpace(librespotToken.AccessToken)
            ? null
            : new SpotifyLyricsAuthToken(
                librespotToken.AccessToken,
                Market: null,
                DefaultUserAgent);
    }
}
