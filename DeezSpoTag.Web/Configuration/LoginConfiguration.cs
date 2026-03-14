namespace DeezSpoTag.Web.Configuration;

public sealed class LoginConfiguration
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool RequirePasswordChange { get; set; } = true;
    public bool EnableSeeding { get; set; }
    public bool ResetPasswordOnSeed { get; set; }
}
