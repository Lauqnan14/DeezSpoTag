using Microsoft.Playwright;

namespace DeezSpoTag.Web.Services;

public sealed record BoomplayChallengeSolveResult(
    string ChallengeCookiePair,
    string UserAgent);

public interface IBoomplayChallengeSolver
{
    Task<BoomplayChallengeSolveResult?> SolveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Solves Boomplay's Cloudflare managed challenge with a real headless Chromium: navigates the
/// Boomplay origin, waits for cf_clearance to be issued, and returns the cookie pair bound to
/// the browser's own User-Agent (Cloudflare binds cf_clearance to both, so the pair must travel
/// together into the HttpClient session).
/// </summary>
public sealed class PlaywrightBoomplayChallengeSolver : IBoomplayChallengeSolver
{
    private const string BoomplayOrigin = "https://www.boomplay.com/";
    private static readonly TimeSpan SolveTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CookiePollInterval = TimeSpan.FromSeconds(2);
    private static int _browserInstallAttempted;

    private readonly string _profileDirectory;
    private readonly string _browsersPath;
    private readonly ILogger<PlaywrightBoomplayChallengeSolver> _logger;

    public PlaywrightBoomplayChallengeSolver(
        IWebHostEnvironment environment,
        ILogger<PlaywrightBoomplayChallengeSolver> logger)
    {
        var dataRoot = AppDataPaths.GetDataRoot(environment);
        _profileDirectory = Path.Join(dataRoot, "boomplay-browser-profile");
        _browsersPath = Path.Join(dataRoot, "ms-playwright");
        _logger = logger;
    }

    public async Task<BoomplayChallengeSolveResult?> SolveAsync(CancellationToken cancellationToken)
    {
        // The harvested cookie is only valid for the browser fingerprint that earned it, so
        // Playwright's own Chromium user agent is adopted by the HttpClient session afterwards.
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", _browsersPath);
        EnsureBrowserInstalled();
        Directory.CreateDirectory(_profileDirectory);

        var playwright = await Playwright.CreateAsync();
        try
        {
            await using var context = await playwright.Chromium.LaunchPersistentContextAsync(
                _profileDirectory,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = true,
                    Timeout = (float)SolveTimeout.TotalMilliseconds
                });
            var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
            await page.GotoAsync(
                BoomplayOrigin,
                new PageGotoOptions
                {
                    Timeout = (float)SolveTimeout.TotalMilliseconds,
                    WaitUntil = WaitUntilState.DOMContentLoaded
                });

            var deadline = DateTimeOffset.UtcNow + SolveTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cookies = await context.CookiesAsync(BoomplayOrigin);
                var clearance = cookies.FirstOrDefault(cookie => cookie.Name == "cf_clearance");
                if (clearance is not null)
                {
                    var userAgent = await page.EvaluateAsync<string>("navigator.userAgent");
                    _logger.LogInformation(
                        "Boomplay Cloudflare challenge solved; cf_clearance harvested (userAgentLength={UserAgentLength}).",
                        userAgent.Length);
                    return new BoomplayChallengeSolveResult($"cf_clearance={clearance.Value}", userAgent);
                }

                await Task.Delay(CookiePollInterval, cancellationToken);
            }

            _logger.LogWarning("Boomplay Cloudflare challenge did not clear within the solve window.");
            return null;
        }
        finally
        {
            playwright.Dispose();
        }
    }

    private void EnsureBrowserInstalled()
    {
        if (Interlocked.CompareExchange(ref _browserInstallAttempted, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (Directory.Exists(_browsersPath) && Directory.EnumerateDirectories(_browsersPath).Any())
            {
                return;
            }

            Directory.CreateDirectory(_browsersPath);
            _logger.LogInformation(
                "Installing Playwright Chromium for Boomplay session recovery (browsersPath={BrowsersPath}).",
                _browsersPath);
            var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Playwright Chromium install failed with exit code {exitCode}.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Allow a later attempt to retry the install instead of poisoning the once-guard.
            Interlocked.Exchange(ref _browserInstallAttempted, 0);
            throw;
        }
    }
}
