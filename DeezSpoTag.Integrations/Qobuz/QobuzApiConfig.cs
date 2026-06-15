namespace DeezSpoTag.Integrations.Qobuz;

public sealed class QobuzApiConfig
{
    public string AppId { get; set; } = "712109809";
    public string AuthToken { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;
    public string DownloadSecret { get; set; } = string.Empty;
    public string ApiBase { get; set; } = "https://www.qobuz.com/api.json/0.2/";
    public string SquidCfClearance { get; set; } = string.Empty;
    public string SquidUserAgent { get; set; } = string.Empty;
    public string DefaultStore { get; set; } = "us-en";
    public int PageSize { get; set; } = 500;
    public string BaseUrl { get; set; } = "https://www.qobuz.com";
    public int CookieCacheMinutes { get; set; } = 60;
    public int CacheDurationMinutes { get; set; } = 60;
    public bool EnableHiResSearch { get; set; } = true;
    public List<string> PreferredStores { get; set; } = new() { "us-en" };
    public bool StrictMatchFallback { get; set; } = false;
}

public readonly record struct QobuzOfficialCredentials(
    string AppId,
    string AuthToken,
    string AppSecret);

public interface IQobuzCredentialProvider
{
    Task<QobuzOfficialCredentials> GetCredentialsAsync(CancellationToken cancellationToken);
}

public sealed class OptionsQobuzCredentialProvider : IQobuzCredentialProvider
{
    private readonly QobuzApiConfig _config;

    public OptionsQobuzCredentialProvider(Microsoft.Extensions.Options.IOptions<QobuzApiConfig> options)
    {
        _config = options.Value;
    }

    public Task<QobuzOfficialCredentials> GetCredentialsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new QobuzOfficialCredentials(
            _config.AppId,
            _config.AuthToken,
            string.IsNullOrWhiteSpace(_config.AppSecret) ? _config.DownloadSecret : _config.AppSecret));
    }
}
