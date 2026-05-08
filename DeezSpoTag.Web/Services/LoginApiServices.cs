using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Web.Services;

public sealed class LoginApiServices
{
    public LoginApiServices(
        IConfiguration configuration,
        DeezSpoTagSettingsService settings,
        DeezerAuthUtils auth,
        AppleMusicWrapperService appleWrapper,
        DeezerLoginCoordinator deezerLogin)
    {
        Configuration = configuration;
        Settings = settings;
        Auth = auth;
        AppleWrapper = appleWrapper;
        DeezerLogin = deezerLogin;
    }

    public IConfiguration Configuration { get; }

    public DeezSpoTagSettingsService Settings { get; }

    public DeezerAuthUtils Auth { get; }

    public AppleMusicWrapperService AppleWrapper { get; }

    public DeezerLoginCoordinator DeezerLogin { get; }
}
