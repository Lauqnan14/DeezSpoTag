namespace DeezSpoTag.Integrations.Qobuz;

public sealed class QobuzApiConfig
{
    public string AppId { get; set; } = "712109809";
    public string DefaultStore { get; set; } = "us-en";
    public int PageSize { get; set; } = 500;
    public string BaseUrl { get; set; } = "https://www.qobuz.com";
    public int CookieCacheMinutes { get; set; } = 60;
    public int CacheDurationMinutes { get; set; } = 60;
    public bool EnableHiResSearch { get; set; } = true;
    public List<string> PreferredStores { get; set; } = new() { "us-en" };
    public bool StrictMatchFallback { get; set; } = false;
}
