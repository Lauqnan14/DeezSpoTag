using DeezSpoTag.Services.Authentication;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Extensions;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.CoverPort;
using DeezSpoTag.Web.Data;
using DeezSpoTag.Web.Models;
using DeezSpoTag.Integrations.Qobuz;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using DeezSpoTag.Web.Configuration;
using Microsoft.Net.Http.Headers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using System.Threading.RateLimiting;
using System.Reflection;

namespace DeezSpoTag.Web;

public partial class Program
{
protected Program()
{
}

private const string UnknownValue = "unknown";
private const string MissingValue = "missing";
    [GeneratedRegex(
        @"^v?(?<core>\d+\.\d+\.\d+\.\d+)(?:[-+][0-9A-Za-z][0-9A-Za-z.\-]*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex BuildVersionPatternRegex();
public static async Task Main(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);
    var libraryDataDir = ConfigureDataDirectories();
    ConfigureDataProtection(builder.Services, libraryDataDir);
    ConfigureTlsRuntime(builder.Configuration);
    var identityConnectionString = ConfigureDatabaseConnections(builder, libraryDataDir);
    ConfigureBindUrls(builder);

    ConfigureCoreServices(builder.Services);
    ConfigureIdentityServices(builder.Services, identityConnectionString);
    builder.Services.Configure<LoginConfiguration>(builder.Configuration.GetSection("LoginConfiguration"));
    builder.Services.Configure<QobuzApiConfig>(builder.Configuration.GetSection("Qobuz"));
    RegisterApplicationServices(builder.Services, builder.Configuration);

    var app = builder.Build();
    LogBuildDetails(app);
    await InitializeApplicationAsync(app, builder.Configuration);
    ConfigurePipeline(app, builder.Configuration);
    MapApplicationEndpoints(app);

    Console.WriteLine("🚀 DeezSpoTag is running!");
    Console.WriteLine("🌐 Access the application at: http://localhost:8668");
    await app.RunAsync();
}

static string ConfigureDataDirectories()
{
    var workersDataDir = AppDataPathResolver.GetDefaultWorkersDataDir();
    var configuredConfigDir = AppDataPathResolver.NormalizeConfiguredDataRoot(Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR"));
    var configuredDataDir = AppDataPathResolver.NormalizeConfiguredDataRoot(Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR"));
    const string webDataMarker = "/DeezSpoTag.Web/Data";

    if (!string.IsNullOrWhiteSpace(configuredConfigDir)
        && configuredConfigDir.Replace('\\', '/').Contains(webDataMarker, StringComparison.OrdinalIgnoreCase))
    {
        configuredConfigDir = null;
    }

    if (!string.IsNullOrWhiteSpace(configuredDataDir)
        && configuredDataDir.Replace('\\', '/').Contains(webDataMarker, StringComparison.OrdinalIgnoreCase))
    {
        configuredDataDir = null;
    }

    // Keep the host .NET app aligned with the canonical Workers runtime root.
    // Legacy ../DeezSpoTag.Workers/Data causes shared-wrapper path drift locally.
    if (AppDataPathResolver.IsLegacyWorkersDataDir(configuredConfigDir))
    {
        configuredConfigDir = null;
    }

    if (AppDataPathResolver.IsLegacyWorkersDataDir(configuredDataDir))
    {
        configuredDataDir = null;
    }

    Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", string.IsNullOrWhiteSpace(configuredConfigDir) ? workersDataDir : configuredConfigDir);
    var dataDir = string.IsNullOrWhiteSpace(configuredDataDir) ? workersDataDir : configuredDataDir;
    Environment.SetEnvironmentVariable("DEEZSPOTAG_DATA_DIR", dataDir);
    Directory.CreateDirectory(dataDir);
    return dataDir;
}

static void ConfigureDataProtection(IServiceCollection services, string dataDir)
{
    var keyDirectory = Path.GetFullPath(Path.Combine(dataDir, "security", "data-protection-keys"));
    Directory.CreateDirectory(keyDirectory);

    services
        .AddDataProtection()
        .SetApplicationName("DeezSpoTag")
        .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
}

static void ConfigureTlsRuntime(IConfiguration configuration)
{
    var allowInsecureTls = DeezSpoTag.Services.Utils.TlsPolicy.AllowInsecure(configuration);
    var allowLegacyTls = DeezSpoTag.Services.Utils.TlsPolicy.AllowLegacy(configuration);

    if (allowInsecureTls || allowLegacyTls)
    {
        Console.WriteLine("TLS security override requested, but only TLS 1.2+ with certificate validation is allowed.");
    }

    ServicePointManager.ServerCertificateValidationCallback = null;
    ServicePointManager.CheckCertificateRevocationList = true;
    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
    ServicePointManager.Expect100Continue = false;
    ServicePointManager.UseNagleAlgorithm = false;
    ServicePointManager.DefaultConnectionLimit = 10;
}

static string ConfigureDatabaseConnections(WebApplicationBuilder builder, string libraryDataDir)
{
    var libraryDbPath = AppDataPathResolver.ResolveDbPathStrict(libraryDataDir, "library", "deezspotag.db");
    var libraryConnectionString = $"Data Source={libraryDbPath}";
    builder.Configuration["ConnectionStrings:Library"] = libraryConnectionString;
    Environment.SetEnvironmentVariable("LIBRARY_DB", libraryConnectionString);

    var queueDbPath = AppDataPathResolver.ResolveDbPathStrict(libraryDataDir, "queue", "queue.db");
    var queueConnectionString = $"Data Source={queueDbPath}";
    builder.Configuration["ConnectionStrings:Queue"] = queueConnectionString;
    Environment.SetEnvironmentVariable("QUEUE_DB", queueConnectionString);
    builder.Configuration["DataDirectory"] = libraryDataDir;

    var identityDbPath = AppDataPathResolver.ResolveDbPathStrict(libraryDataDir, "identity", "deezspotag-identity.db");
    var identityConnectionString = $"Data Source={identityDbPath}";
    builder.Configuration["ConnectionStrings:Identity"] = identityConnectionString;
    Environment.SetEnvironmentVariable("IDENTITY_DB", identityConnectionString);

    Console.WriteLine($"Library DB: {libraryDbPath}");
    Console.WriteLine($"Queue DB: {queueDbPath}");
    Console.WriteLine($"Data Dir: {Path.GetFullPath(libraryDataDir)}");
    Console.WriteLine($"Identity DB: {identityDbPath}");
    return identityConnectionString;
}

static void ConfigureBindUrls(WebApplicationBuilder builder)
{
    var bindUrls = builder.Configuration["Web:BindUrls"];
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
        && string.IsNullOrWhiteSpace(bindUrls))
    {
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(8668));
        return;
    }

    if (!string.IsNullOrWhiteSpace(bindUrls))
    {
        builder.WebHost.UseUrls(bindUrls);
    }
}

static void ConfigureCoreServices(IServiceCollection services)
{
    services.AddControllersWithViews();
    services.AddRazorPages();
    services.AddHttpClient();
    services.AddAntiforgery(options =>
    {
        options.HeaderName = "X-CSRF-TOKEN";
        options.Cookie.Name = "deezspotag.csrf";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
    services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy("AuthEndpoints", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? UnknownValue,
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 8,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
        options.AddPolicy("SensitiveWrites", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? UnknownValue,
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
    });
    services.AddCoverPortingServices();
    services.AddMemoryCache();
    services.AddSingleton<QuickTagService>();
    services.AddSingleton<QuickTagTagSourceService>();
    services.AddHttpClient<DeezSpoTag.Web.Services.PlaylistCoverService>();
    services.AddHttpClient<DeezSpoTag.Integrations.Plex.PlexApiClient>();
    services.AddHttpClient<DeezSpoTag.Integrations.Jellyfin.JellyfinApiClient>();
    services.AddHttpClient<DeezSpoTag.Integrations.Discogs.DiscogsApiClient>();
    services.AddSignalR();
    services.AddDeezSpoTagQueue();
    services.AddHostedService<DeezSpoTag.Services.Download.Shared.DeezSpoTagQueueBackgroundService>();
}

static void ConfigureIdentityServices(IServiceCollection services, string identityConnectionString)
{
    services.AddDbContext<AppIdentityDbContext>(options => options.UseSqlite(identityConnectionString));
    services.AddHttpContextAccessor();
    services.AddDefaultIdentity<AppUser>(options =>
        {
            options.SignIn.RequireConfirmedAccount = false;
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Password.RequireUppercase = true;
            options.Password.RequiredLength = 12;
            options.Password.RequiredUniqueChars = 2;
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        })
        .AddEntityFrameworkStores<AppIdentityDbContext>();
    services.ConfigureApplicationCookie(options =>
    {
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Events ??= new Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationEvents();
        options.Events.OnRedirectToLogin = context => HandleCookieRedirectAsync(context, StatusCodes.Status401Unauthorized);
        options.Events.OnRedirectToAccessDenied = context => HandleCookieRedirectAsync(context, StatusCodes.Status403Forbidden);
        options.Events.OnValidatePrincipal = ValidateIdentityPrincipalAsync;
    });
    services.AddAuthorizationBuilder()
        .SetFallbackPolicy(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build());
}

static Task HandleCookieRedirectAsync(
    Microsoft.AspNetCore.Authentication.RedirectContext<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> context,
    int statusCode)
{
    if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || context.Request.Path.StartsWithSegments("/deezerQueueHub", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = statusCode;
        return Task.CompletedTask;
    }

    context.Response.Redirect(context.RedirectUri);
    return Task.CompletedTask;
}

static async Task ValidateIdentityPrincipalAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieValidatePrincipalContext context)
{
    if (context.Principal is null)
    {
        return;
    }

    var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<AppUser>>();
    var signInManager = context.HttpContext.RequestServices.GetRequiredService<SignInManager<AppUser>>();
    var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
    var userIdRaw = userManager.GetUserId(context.Principal);
    var claimUserId = context.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userIdRaw) && !string.IsNullOrWhiteSpace(claimUserId))
    {
        userIdRaw = claimUserId;
    }

    var userIdNormalized = userIdRaw?.Trim().Trim('{', '}');
    var userName = context.Principal.FindFirstValue(ClaimTypes.Name);
    var userNameNormalized = userName?.Trim().ToUpperInvariant();
    var user = await userManager.GetUserAsync(context.Principal);
    if (user is null)
    {
        user = await TryResolvePrincipalUserAsync(userManager, userIdNormalized, userName, userNameNormalized);
        if (user is not null)
        {
            var refreshedPrincipal = await signInManager.CreateUserPrincipalAsync(user);
            context.ReplacePrincipal(refreshedPrincipal);
            context.ShouldRenew = true;
        }
    }

    if (user is null)
    {
        logger.LogWarning(
            "Auth cookie rejected: user not found. userId={UserId} userName={UserName}",
            userIdNormalized ?? MissingValue,
            userName ?? MissingValue);
        await RejectAndSignOutAsync(context);
        return;
    }

    if (await userManager.IsLockedOutAsync(user))
    {
        logger.LogWarning(
            "Auth cookie rejected: locked out user {UserName} ({UserId}).",
            user.UserName ?? UnknownValue,
            user.Id);
        await RejectAndSignOutAsync(context);
    }
}

static async Task<AppUser?> TryResolvePrincipalUserAsync(
    UserManager<AppUser> userManager,
    string? userIdNormalized,
    string? userName,
    string? userNameNormalized)
{
    try
    {
        if (!string.IsNullOrWhiteSpace(userIdNormalized))
        {
            var byId = await userManager.FindByIdAsync(userIdNormalized);
            if (byId != null)
            {
                return byId;
            }
        }

        if (!string.IsNullOrWhiteSpace(userName))
        {
            var byName = await userManager.FindByNameAsync(userName);
            if (byName != null)
            {
                return byName;
            }
        }

        if (!string.IsNullOrWhiteSpace(userNameNormalized))
        {
            return await userManager.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == userNameNormalized);
        }
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // Best-effort only.
    }

    return null;
}

static async Task RejectAndSignOutAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieValidatePrincipalContext context)
{
    context.RejectPrincipal();
    await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
}

static void LogBuildDetails(WebApplication app)
{
    var entryAssembly = typeof(Program).Assembly;
    var assemblyVersion = entryAssembly.GetName().Version?.ToString() ?? UnknownValue;
    var displayVersion = ResolveBuildDisplayVersion(entryAssembly, assemblyVersion);
    var assemblyLocation = entryAssembly.Location;
    var buildTimestamp = (!string.IsNullOrWhiteSpace(assemblyLocation) && File.Exists(assemblyLocation))
        ? File.GetLastWriteTimeUtc(assemblyLocation).ToString("yyyy-MM-dd HH:mm:ss 'UTC'")
        : UnknownValue;
    app.Logger.LogInformation("DeezSpoTag.Web build: {Configuration} ({AssemblyVersion}) built {BuildTimestamp}",
#if DEBUG
        "Debug",
#else
        "Release",
#endif
        displayVersion,
        buildTimestamp);
}

static string ResolveBuildDisplayVersion(Assembly entryAssembly, string fallbackVersion)
{
    var injectedBuildVersion = Environment.GetEnvironmentVariable("DEEZSPOTAG_BUILD_VERSION");
    if (!string.IsNullOrWhiteSpace(injectedBuildVersion))
    {
        return NormalizeBuildDisplayVersion(injectedBuildVersion);
    }

    var informationalVersion = entryAssembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion;
    if (!string.IsNullOrWhiteSpace(informationalVersion))
    {
        return NormalizeBuildDisplayVersion(informationalVersion);
    }

    return NormalizeBuildDisplayVersion(fallbackVersion);
}

static string NormalizeBuildDisplayVersion(string? candidate)
{
    var value = (candidate ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(value))
    {
        return UnknownValue;
    }

    if (string.Equals(value, UnknownValue, StringComparison.OrdinalIgnoreCase))
    {
        return UnknownValue;
    }

    var match = BuildVersionPatternRegex().Match(value);
    if (!match.Success)
    {
        return value;
    }

    var core = match.Groups["core"].Value;
    return $"v{core}";
}

static void ConfigurePipeline(WebApplication app, IConfiguration configuration)
{
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Home/Error");
        app.UseHsts();
    }

    var enableHttpsRedirect = configuration.GetValue<bool?>("Web:EnableHttpsRedirection")
        ?? !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORT"));
    if (enableHttpsRedirect)
    {
        app.UseHttpsRedirection();
    }

    ConfigureSecurityHeadersMiddleware(app);
    ConfigureStaticFiles(app);
    app.UseRouting();
    app.UseRateLimiter();
    app.UseAuthentication();
    ConfigureCsrfCookieMiddleware(app);
    ConfigureApiTokenMiddleware(app);
    app.UseAuthorization();
    ConfigureApiAntiforgeryMiddleware(app);
    ConfigureLoginStatusCodePages(app);
    ConfigurePasswordChangeMiddleware(app);
    ConfigureIdentityRouteGuardsMiddleware(app);
}

static void ConfigureSecurityHeadersMiddleware(WebApplication app)
{
    app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.TryAdd("X-Frame-Options", "DENY");
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
            context.Response.Headers.TryAdd(
                "Content-Security-Policy",
                "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; object-src 'none'; form-action 'self'; " +
                "img-src 'self' data: blob: https:; media-src 'self' data: blob: https:; font-src 'self' data: https:; " +
                "style-src 'self' 'unsafe-inline' https:; script-src 'self' 'unsafe-inline' 'unsafe-eval' https://cdn.jsdelivr.net https://code.jquery.com https://cdnjs.cloudflare.com; connect-src 'self' https: ws: wss:;");
            return Task.CompletedTask;
        });
        await next();
    });
}

static void ConfigureStaticFiles(WebApplication app)
{
    var contentTypeProvider = new FileExtensionContentTypeProvider();
    contentTypeProvider.Mappings[".webmanifest"] = "application/manifest+json";
    app.UseStaticFiles(new StaticFileOptions
    {
        ContentTypeProvider = contentTypeProvider,
        OnPrepareResponse = context =>
        {
            var path = context.Context.Request.Path.Value ?? string.Empty;
            if (path.StartsWith("/images/icons/", StringComparison.OrdinalIgnoreCase))
            {
                context.Context.Response.Headers[HeaderNames.CacheControl] =
                    "public,max-age=604800,stale-while-revalidate=86400";
            }

            context.Context.Response.Headers.XContentTypeOptions = "nosniff";
        }
    });
}

static void ConfigureCsrfCookieMiddleware(WebApplication app)
{
    app.Use(async (context, next) =>
    {
        if (HttpMethods.IsGet(context.Request.Method)
            && !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
            antiforgery.GetAndStoreTokens(context);
        }

        await next();
    });
}

static void ConfigureApiTokenMiddleware(WebApplication app)
{
    app.Use(async (context, next) =>
    {
        if (ShouldAttemptApiTokenAuth(context))
        {
            if (!IsApiTokenScopeAllowed(context))
            {
                await next();
                return;
            }

            TryAuthenticateApiToken(context);
        }

        await next();
    });
}

static bool ShouldAttemptApiTokenAuth(HttpContext context)
{
    if (context.User?.Identity?.IsAuthenticated == true)
    {
        return false;
    }

    var path = context.Request.Path.Value ?? string.Empty;
    if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var bearer = context.Request.Headers.Authorization.ToString();
    return bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
}

static bool IsApiTokenScopeAllowed(HttpContext context)
{
    var allowRemoteApiToken = IsTrue(Environment.GetEnvironmentVariable("DEEZSPOTAG_ALLOW_REMOTE_API_TOKEN"));
    return allowRemoteApiToken
        || DeezSpoTag.Web.Controllers.Api.LocalApiAccess.IsTrustedLocal(context.Connection.RemoteIpAddress);
}

static void TryAuthenticateApiToken(HttpContext context)
{
    var bearer = context.Request.Headers.Authorization.ToString();
    var token = bearer["Bearer ".Length..].Trim();
    var settingsService = context.RequestServices.GetService<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>();
    var configured = settingsService?.LoadSettings().ApiToken
                     ?? Environment.GetEnvironmentVariable("DEEZSPOTAG_API_TOKEN");
    if (string.IsNullOrWhiteSpace(configured))
    {
        return;
    }

    var tokenBytes = Encoding.UTF8.GetBytes(token);
    var configuredBytes = Encoding.UTF8.GetBytes(configured);
    if (tokenBytes.Length != configuredBytes.Length
        || !CryptographicOperations.FixedTimeEquals(tokenBytes, configuredBytes))
    {
        return;
    }

    var identity = new ClaimsIdentity("ApiToken");
    identity.AddClaim(new Claim(ClaimTypes.Name, "api-token"));
    context.User = new ClaimsPrincipal(identity);
}

static void ConfigureApiAntiforgeryMiddleware(WebApplication app)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            && context.User?.Identity?.IsAuthenticated == true
            && !string.Equals(context.User.Identity.AuthenticationType, "ApiToken", StringComparison.Ordinal)
            && !HttpMethods.IsGet(context.Request.Method)
            && !HttpMethods.IsHead(context.Request.Method)
            && !HttpMethods.IsOptions(context.Request.Method)
            && !HttpMethods.IsTrace(context.Request.Method))
        {
            var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "Invalid anti-forgery token." });
                return;
            }
        }

        await next();
    });
}

static void ConfigureLoginStatusCodePages(WebApplication app)
{
    app.UseStatusCodePages(context =>
    {
        var http = context.HttpContext;
        if (http.Response.StatusCode != StatusCodes.Status400BadRequest
            || !HttpMethods.IsPost(http.Request.Method)
            || !http.Request.Path.StartsWithSegments("/Identity/Account/Login", StringComparison.OrdinalIgnoreCase)
            || http.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        var targetUrl = QueryHelpers.AddQueryString("/Identity/Account/Login", "csrfError", "1");
        http.Response.Redirect(targetUrl);
        return Task.CompletedTask;
    });
}

static void ConfigurePasswordChangeMiddleware(WebApplication app)
{
    app.Use(async (context, next) =>
    {
        if (context.User?.Identity?.IsAuthenticated == true
            && context.User.HasClaim("must_change_password", "true"))
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (path.StartsWith("/api/account/change-credentials", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/Identity/Account/Logout", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/Identity/Account/Login", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/images/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/sw.js", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { message = "Password change required." });
                return;
            }
        }

        await next();
    });
}

static void ConfigureIdentityRouteGuardsMiddleware(WebApplication app)
{
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/Identity/Account/Register", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/Identity/Account/RegisterConfirmation", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (path.StartsWith("/Identity/Account/Manage", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Redirect("/");
            return;
        }

        await next();
    });
}

static void MapApplicationEndpoints(WebApplication app)
{
    app.MapControllers();
    app.MapRazorPages();
    app.MapHub<DeezSpoTag.Web.Hubs.DeezerQueueHub>("/deezerQueueHub");
    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}");
}

static bool IsTrue(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    return value.Equals("1", StringComparison.OrdinalIgnoreCase)
        || value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
}

static void RegisterApplicationServices(IServiceCollection services, IConfiguration configuration)
{
    services.AddSingleton<DeezSpoTag.Services.Settings.PlatformCapabilitiesStore>(sp =>
    {
        var env = sp.GetRequiredService<IWebHostEnvironment>();
        var logger = sp.GetService<ILogger<DeezSpoTag.Services.Settings.PlatformCapabilitiesStore>>();
        var dataRoot = DeezSpoTag.Web.Services.AppDataPaths.GetDataRoot(env);
        return new DeezSpoTag.Services.Settings.PlatformCapabilitiesStore(dataRoot, logger);
    });

    RegisterAutoTagServices(services);
    RegisterCoreApplicationServices(services, configuration);
    RegisterDeezerServices(services, configuration);

    services.AddDeezSpoTagServices();
    services.AddDeezSpoTagAuthentication();
    services.AddDownloadEngine();
    services.AddSingleton<DeezSpoTag.Services.Download.IActivityLogWriter, DeezSpoTag.Web.Services.ActivityLogWriter>();
    services.AddSingleton<DeezSpoTag.Services.Download.AuthenticatedDeezerService>();
    services.AddScoped<DeezSpoTag.Services.Metadata.IMetadataResolver, DeezSpoTag.Web.Services.SpotifyMetadataResolver>();
    services.AddSingleton<DeezSpoTag.Web.Services.AppleMusicWrapperService>();
    services.AddHostedService(sp => sp.GetRequiredService<DeezSpoTag.Web.Services.AppleMusicWrapperService>());
    services.AddSingleton<DeezSpoTag.Services.Download.Apple.IAppleWrapperStatusProvider>(
        sp => sp.GetRequiredService<DeezSpoTag.Web.Services.AppleMusicWrapperService>());
    services.AddSingleton<ILoginStorageService, LoginStorageService>();
    services.AddHostedService<StartupLoginService>();
    services.AddSingleton<DeezSpoTag.Services.Download.Shared.Models.IDeezSpoTagListener, DeezSpoTag.Web.Services.SignalRDeezSpoTagListener>();
}

static void RegisterAutoTagServices(IServiceCollection services)
{
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTagService.AutoTagServiceCollaborators>(sp =>
        new DeezSpoTag.Web.Services.AutoTagService.AutoTagServiceCollaborators
        {
            ActivityLog = sp.GetRequiredService<DeezSpoTag.Web.Services.LibraryConfigStore>(),
            DeezerAuth = sp.GetRequiredService<DeezSpoTag.Services.Download.AuthenticatedDeezerService>(),
            MetadataService = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTagMetadataService>(),
            AutoTagRunner = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTag.IAutoTagRunner>(),
            LibraryOrganizer = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTagLibraryOrganizer>(),
            DownloadMoveService = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTagDownloadMoveService>(),
            QueueRepository = sp.GetRequiredService<DeezSpoTag.Services.Download.Queue.DownloadQueueRepository>(),
            QuickTagService = sp.GetRequiredService<DeezSpoTag.Web.Services.QuickTagService>(),
            PlatformAuthService = sp.GetRequiredService<DeezSpoTag.Web.Services.PlatformAuthService>(),
            PlexApiClient = sp.GetRequiredService<DeezSpoTag.Integrations.Plex.PlexApiClient>(),
            SpotifyBlobService = sp.GetRequiredService<DeezSpoTag.Web.Services.SpotifyBlobService>(),
            SettingsService = sp.GetRequiredService<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>(),
            LibraryRepository = sp.GetRequiredService<DeezSpoTag.Services.Library.LibraryRepository>(),
            LibraryScanRunner = sp.GetRequiredService<DeezSpoTag.Web.Services.LibraryScanRunner>(),
            QualityScannerService = sp.GetRequiredService<DeezSpoTag.Web.Services.QualityScannerService>(),
            DuplicateCleanerService = sp.GetRequiredService<DeezSpoTag.Web.Services.DuplicateCleanerService>(),
            LyricsRefreshQueueService = sp.GetRequiredService<DeezSpoTag.Web.Services.LyricsRefreshQueueService>(),
            CoverMaintenanceService = sp.GetRequiredService<DeezSpoTag.Web.Services.CoverPort.CoverLibraryMaintenanceService>()
        });
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner.LocalAutoTagRunnerCollaborators>(sp =>
        new DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner.LocalAutoTagRunnerCollaborators
        {
            Logger = sp.GetRequiredService<ILogger<DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner>>(),
            HttpClientFactory = sp.GetRequiredService<IHttpClientFactory>(),
            MusicBrainzMatcher = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTag.MusicBrainzMatcher>(),
            BeatportMatcher = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTag.BeatportMatcher>(),
            DiscogsMatcher = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTag.DiscogsMatcher>(),
            TraxsourceMatcher = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTag.TraxsourceMatcher>(),
            JunoDownloadMatcher = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTag.JunoDownloadMatcher>(),
            BandcampMatcher = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTag.BandcampMatcher>(),
            BeatsourceMatcher = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTag.BeatsourceMatcher>(),
            BpmSupremeMatcher = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTag.BpmSupremeMatcher>(),
            ItunesMatcher = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTag.ItunesMatcher>(),
            SpotifyMatcher = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTag.SpotifyMatcher>(),
            DeezerMatcher = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTag.DeezerMatcher>(),
            LastFmMatcher = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTag.LastFmMatcher>(),
            BoomplayMatcher = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTag.BoomplayMatcher>(),
            MusixmatchMatcher = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTag.MusixmatchMatcher>(),
            LrclibMatcher = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTag.LrclibMatcher>(),
            ShazamMatcher = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTag.ShazamMatcher>(),
            ShazamRecognitionService = sp.GetRequiredService<DeezSpoTag.Web.Services.ShazamRecognitionService>(),
            AppleLyricsService = sp.GetRequiredService<DeezSpoTag.Services.Apple.AppleLyricsService>(),
            AppleMusicCatalogService = sp.GetRequiredService<DeezSpoTag.Services.Apple.AppleMusicCatalogService>(),
            DownloadLyricsService = sp.GetRequiredService<DeezSpoTag.Services.Download.Utils.LyricsService>(),
            SettingsService = sp.GetRequiredService<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>(),
            CapabilitiesStore = sp.GetRequiredService<DeezSpoTag.Services.Settings.PlatformCapabilitiesStore>()
        });
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTagService>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.IAutoTagRunner, DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTagMetadataService>();
    services.AddSingleton<DeezSpoTag.Web.Services.ShazamRecognitionService>();
    services.AddHttpClient<DeezSpoTag.Web.Services.ShazamDiscoveryService>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.IAutoTagPlatform, DeezSpoTag.Web.Services.AutoTag.MusicBrainzPlatform>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.IAutoTagPlatform, DeezSpoTag.Web.Services.AutoTag.ShazamPlatform>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.IAutoTagPlatform, DeezSpoTag.Web.Services.AutoTag.BandcampPlatform>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.IAutoTagPlatform, DeezSpoTag.Web.Services.AutoTag.BeatsourcePlatform>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.IAutoTagPlatform, DeezSpoTag.Web.Services.AutoTag.BpmSupremePlatform>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.IAutoTagPlatform, DeezSpoTag.Web.Services.AutoTag.ItunesPlatform>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.IAutoTagPlatform, DeezSpoTag.Web.Services.AutoTag.MusixmatchPlatform>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.IAutoTagPlatform, DeezSpoTag.Web.Services.AutoTag.LrclibPlatform>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.IAutoTagPlatform, DeezSpoTag.Web.Services.AutoTag.SpotifyPlatform>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.IAutoTagPlatform, DeezSpoTag.Web.Services.AutoTag.LastFmPlatform>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.IAutoTagPlatform, DeezSpoTag.Web.Services.AutoTag.DeezerPlatform>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.IAutoTagPlatform, DeezSpoTag.Web.Services.AutoTag.BoomplayPlatform>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.IAutoTagPlatform, DeezSpoTag.Web.Services.AutoTag.BeatportPlatform>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.IAutoTagPlatform, DeezSpoTag.Web.Services.AutoTag.DiscogsPlatform>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.IAutoTagPlatform, DeezSpoTag.Web.Services.AutoTag.TraxsourcePlatform>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.IAutoTagPlatform, DeezSpoTag.Web.Services.AutoTag.JunoDownloadPlatform>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.PortedPlatformRegistry>();
    services.AddHttpClient<DeezSpoTag.Web.Services.AutoTag.MusicBrainzClient>()
        .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
        {
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
        });
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.MusicBrainzMatcher>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.ShazamMatcher>();
    services.AddHttpClient<DeezSpoTag.Web.Services.AutoTag.BandcampClient>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.BandcampMatcher>();
    services.AddHttpClient<DeezSpoTag.Web.Services.AutoTag.BeatsourceClient>();
    services.AddHttpClient<DeezSpoTag.Web.Services.AutoTag.BeatsourceTokenManager>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.BeatsourceMatcher>();
    services.AddHttpClient<DeezSpoTag.Web.Services.AutoTag.BpmSupremeClient>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.BpmSupremeMatcher>();
    services.AddHttpClient<DeezSpoTag.Web.Services.AutoTag.ItunesClient>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.ItunesMatcher>();
    services.AddHttpClient<DeezSpoTag.Web.Services.AutoTag.MusixmatchClient>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.MusixmatchMatcher>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.LrclibMatcher>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.SpotifyClient>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.SpotifyMatcher>();
    services.AddHttpClient<DeezSpoTag.Web.Services.AutoTag.DeezerClient>()
        .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        });
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.DeezerMatcher>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.LastFmMatcher>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.BoomplayMatcher>();
    services.AddHttpClient<DeezSpoTag.Web.Services.AutoTag.BeatportClient>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.BeatportMatcher>();
    services.AddHttpClient<DeezSpoTag.Web.Services.AutoTag.DiscogsClient>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.DiscogsMatcher>();
    services.AddHttpClient<DeezSpoTag.Web.Services.AutoTag.TraxsourceClient>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.TraxsourceMatcher>();
    services.AddHttpClient<DeezSpoTag.Web.Services.AutoTag.JunoDownloadClient>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.JunoDownloadMatcher>();
    services.AddSingleton<DeezSpoTag.Web.Services.TaggingProfileService>();
    services.AddSingleton<DeezSpoTag.Web.Services.ExternalFileImportService>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTagConfigBuilder>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTagProfileResolutionService>();
    services.AddSingleton<DeezSpoTag.Web.Services.DownloadTagSettingsConverter>();
    services.AddSingleton<DeezSpoTag.Services.Download.Shared.IDownloadTagSettingsResolver, DeezSpoTag.Web.Services.DownloadTagSettingsResolver>();
    services.AddSingleton<DeezSpoTag.Web.Services.TagSettingsMigrationService>();
    services.AddSingleton<DeezSpoTag.Web.Services.DownloadVerificationService>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTagDefaultsStore>();
    services.AddSingleton<DeezSpoTag.Web.Services.UserPreferencesStore>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTagLibraryOrganizer>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTagDownloadMoveService>();
}

static void RegisterCoreApplicationServices(IServiceCollection services, IConfiguration configuration)
{
    services.AddSingleton<DeezSpoTag.Web.Services.AppInstanceIdProvider>();
    services.AddSingleton<DeezSpoTag.Web.Services.PlatformAuthService>();
    services.AddSingleton<DeezSpoTag.Web.Services.SpotifyUserAuthStore>();
    services.AddSingleton<DeezSpoTag.Web.Services.ISpotifyUserContextAccessor, DeezSpoTag.Web.Services.SpotifyUserContextAccessor>();
    services.AddSingleton<DeezSpoTag.Web.Services.SpotifyUserStateProvider>();
    services.AddSingleton<DeezSpoTag.Services.Apple.AppleMusicCatalogService>();
    services.AddSingleton<DeezSpoTag.Services.Apple.AppleLyricsService>();
    services.AddSingleton<DeezSpoTag.Web.Services.SpotifyBlobService>();
    services.AddSingleton<DeezSpoTag.Web.Services.SpotifyAppTokenService>();
    services.AddSingleton<DeezSpoTag.Web.Services.SpotifyPathfinderMetadataClient>();
    services.AddSingleton<DeezSpoTag.Web.Services.SpotifyDeezerAlbumResolver>();
    services.AddSingleton<DeezSpoTag.Web.Services.SpotifyCentralMetadataService>();
    services.AddSingleton<DeezSpoTag.Web.Services.SpotifyArtistService>();
    services.AddSingleton<DeezSpoTag.Web.Services.SpotifySearchService>();
    services.AddSingleton<DeezSpoTag.Web.Services.AppleVideoAtmosCapabilityService>();
    services.AddSingleton<DeezSpoTag.Web.Services.AppleCatalogVideoAtmosEnricher>();
    services.AddSingleton<DeezSpoTag.Web.Services.DeezSpoTagSearchService>();
    services.AddSingleton<DeezSpoTag.Web.Services.SpotifyDesktopSearchService>();
    services.AddSingleton<DeezSpoTag.Web.Services.SpotifyMetadataService>();
    services.AddSingleton<DeezSpoTag.Web.Services.BoomplayMetadataService>();
    services.AddScoped<DeezSpoTag.Web.Services.LinkMapping.DeezerLinkMappingService>();
    services.AddScoped<DeezSpoTag.Services.Metadata.IMetadataResolver, DeezSpoTag.Web.Services.QobuzMetadataResolver>();
    services.AddScoped<DeezSpoTag.Services.Metadata.IMetadataResolver, DeezSpoTag.Web.Services.DeezerMetadataResolver>();
    services.AddSingleton<DeezSpoTag.Services.Download.ISpotifyArtworkResolver, DeezSpoTag.Web.Services.SpotifyArtworkResolver>();
    services.AddSingleton<DeezSpoTag.Web.Services.SpotifyTracklistService>();
    services.AddSingleton<DeezSpoTag.Web.Services.SpotifyRecommendationService>();
    services.AddSingleton<DeezSpoTag.Web.Services.ITidalAccessTokenProvider, DeezSpoTag.Web.Services.TidalAccessTokenProvider>();
    services.AddSingleton<DeezSpoTag.Web.Services.ISpotifyTracklistMatchQueue, DeezSpoTag.Web.Services.SpotifyTracklistMatchQueue>();
    services.AddSingleton<DeezSpoTag.Web.Services.ISpotifyTracklistMatchStore, DeezSpoTag.Web.Services.SpotifyTracklistMatchStore>();
    services.AddHostedService<DeezSpoTag.Web.Services.SpotifyTracklistMatchBackgroundService>();
    services.AddSingleton<DeezSpoTag.Web.Services.SpotifyDeezerLinkService>();
    services.AddSingleton<DeezSpoTag.Web.Services.SpotifyFavoritesService>();
    services.AddSingleton<DeezSpoTag.Web.Services.DeezerFavoritesService>();
    services.AddSingleton<DeezSpoTag.Web.Services.ArtistWatchPlatformDependencies>(sp =>
        new DeezSpoTag.Web.Services.ArtistWatchPlatformDependencies(
            sp.GetRequiredService<DeezSpoTag.Web.Services.SpotifyArtistService>(),
            sp.GetRequiredService<DeezSpoTag.Web.Services.SpotifyMetadataService>(),
            sp.GetRequiredService<DeezSpoTag.Services.Apple.AppleMusicCatalogService>(),
            sp.GetRequiredService<DeezSpoTag.Integrations.Deezer.DeezerClient>()));
    services.AddSingleton<DeezSpoTag.Web.Services.ArtistWatchService>();
    services.AddSingleton<DeezSpoTag.Web.Services.PlaylistSyncService>();
    services.AddSingleton<DeezSpoTag.Web.Services.PlaylistVisualService>();
    services.AddScoped<DeezSpoTag.Web.Services.SpotifyHomeFeedCollaborators>(sp =>
        new DeezSpoTag.Web.Services.SpotifyHomeFeedCollaborators
        {
            PathfinderClient = sp.GetRequiredService<DeezSpoTag.Web.Services.SpotifyPathfinderMetadataClient>(),
            SpotifyMetadataService = sp.GetRequiredService<DeezSpoTag.Web.Services.SpotifyMetadataService>(),
            SpotifyDeezerAlbumResolver = sp.GetRequiredService<DeezSpoTag.Web.Services.SpotifyDeezerAlbumResolver>(),
            SongLinkResolver = sp.GetRequiredService<DeezSpoTag.Services.Download.Utils.SongLinkResolver>(),
            DeezerClient = sp.GetRequiredService<DeezSpoTag.Integrations.Deezer.DeezerClient>(),
            SettingsService = sp.GetRequiredService<DeezSpoTag.Services.Settings.ISettingsService>(),
            BlobService = sp.GetRequiredService<DeezSpoTag.Web.Services.SpotifyBlobService>(),
            PlatformAuthService = sp.GetRequiredService<DeezSpoTag.Web.Services.PlatformAuthService>(),
            UserAuthStore = sp.GetRequiredService<DeezSpoTag.Web.Services.SpotifyUserAuthStore>(),
            UserContextAccessor = sp.GetRequiredService<DeezSpoTag.Web.Services.ISpotifyUserContextAccessor>()
        });
    services.AddScoped<DeezSpoTag.Web.Controllers.Api.SpotifyCredentialsApiControllerCore.SpotifyCredentialsCollaborators>(sp =>
        new DeezSpoTag.Web.Controllers.Api.SpotifyCredentialsApiControllerCore.SpotifyCredentialsCollaborators
        {
            PlatformAuthService = sp.GetRequiredService<DeezSpoTag.Web.Services.PlatformAuthService>(),
            UserAuthStore = sp.GetRequiredService<DeezSpoTag.Web.Services.SpotifyUserAuthStore>(),
            BlobService = sp.GetRequiredService<DeezSpoTag.Web.Services.SpotifyBlobService>(),
            PathfinderMetadataClient = sp.GetRequiredService<DeezSpoTag.Web.Services.SpotifyPathfinderMetadataClient>(),
            ConfigStore = sp.GetRequiredService<DeezSpoTag.Web.Services.LibraryConfigStore>(),
            Configuration = sp.GetRequiredService<IConfiguration>(),
            HttpClientFactory = sp.GetRequiredService<IHttpClientFactory>()
        });
    services.AddScoped<DeezSpoTag.Web.Controllers.Api.DeezerDownloadCollaborators>(sp =>
        new DeezSpoTag.Web.Controllers.Api.DeezerDownloadCollaborators(
            sp.GetRequiredService<DeezSpoTag.Web.Services.DownloadOrchestrationService>(),
            sp.GetRequiredService<DeezSpoTag.Services.Download.Queue.DownloadQueueRepository>(),
            sp.GetRequiredService<DeezSpoTag.Services.Download.Shared.DeezSpoTagApp>(),
            sp.GetRequiredService<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>(),
            sp.GetRequiredService<DeezSpoTag.Integrations.Deezer.DeezerGatewayService>(),
            sp.GetRequiredService<DeezSpoTag.Web.Services.BoomplayMetadataService>()));
    services.AddScoped<DeezSpoTag.Web.Controllers.ApiController.ApiControllerMusicServices>(sp =>
        new DeezSpoTag.Web.Controllers.ApiController.ApiControllerMusicServices(
            sp.GetRequiredService<DeezSpoTag.Services.Apple.AppleMusicCatalogService>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<DeezSpoTag.Services.Download.ISpotifyIdResolver>(),
            sp.GetRequiredService<DeezSpoTag.Services.Download.ISpotifyArtworkResolver>(),
            sp.GetRequiredService<DeezSpoTag.Web.Services.SpotifyArtistService>()));
    services.AddSingleton<DeezSpoTag.Web.Services.PlaylistWatchService.PlaylistWatchPlatformServices>(sp =>
        new DeezSpoTag.Web.Services.PlaylistWatchService.PlaylistWatchPlatformServices
        {
            SpotifyMetadataService = sp.GetRequiredService<DeezSpoTag.Web.Services.SpotifyMetadataService>(),
            SpotifyPathfinderMetadataClient = sp.GetRequiredService<DeezSpoTag.Web.Services.SpotifyPathfinderMetadataClient>(),
            SpotifyArtistService = sp.GetRequiredService<DeezSpoTag.Web.Services.SpotifyArtistService>(),
            DeezerClient = sp.GetRequiredService<DeezSpoTag.Integrations.Deezer.DeezerClient>(),
            DeezerGatewayService = sp.GetRequiredService<DeezSpoTag.Integrations.Deezer.DeezerGatewayService>(),
            AppleCatalogService = sp.GetRequiredService<DeezSpoTag.Services.Apple.AppleMusicCatalogService>(),
            BoomplayMetadataService = sp.GetRequiredService<DeezSpoTag.Web.Services.BoomplayMetadataService>(),
            LibraryRecommendationService = sp.GetRequiredService<DeezSpoTag.Web.Services.LibraryRecommendationService>()
        });
    services.AddSingleton<DeezSpoTag.Web.Services.PlaylistWatchService>();
    services.AddHostedService<DeezSpoTag.Web.Services.PlaylistWatchHostedService>();
    services.AddSingleton<DeezSpoTag.Web.Services.MediaServerSoundtrackStore>();
    services.AddSingleton<DeezSpoTag.Web.Services.MediaServerSoundtrackCacheRepository>();
    services.AddSingleton<DeezSpoTag.Web.Services.MediaServerSoundtrackService.Dependencies>(sp =>
        new DeezSpoTag.Web.Services.MediaServerSoundtrackService.Dependencies
        {
            PlatformAuthService = sp.GetRequiredService<DeezSpoTag.Web.Services.PlatformAuthService>(),
            PlexApiClient = sp.GetRequiredService<DeezSpoTag.Integrations.Plex.PlexApiClient>(),
            JellyfinApiClient = sp.GetRequiredService<DeezSpoTag.Integrations.Jellyfin.JellyfinApiClient>(),
            SpotifySearchService = sp.GetRequiredService<DeezSpoTag.Web.Services.SpotifySearchService>(),
            MusicBrainzClient = sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTag.MusicBrainzClient>(),
            Store = sp.GetRequiredService<DeezSpoTag.Web.Services.MediaServerSoundtrackStore>(),
            CacheRepository = sp.GetRequiredService<DeezSpoTag.Web.Services.MediaServerSoundtrackCacheRepository>(),
            HttpClientFactory = sp.GetRequiredService<IHttpClientFactory>()
        });
    services.AddSingleton<DeezSpoTag.Web.Services.MediaServerSoundtrackService>();
    services.AddHostedService<DeezSpoTag.Web.Services.MediaServerSoundtrackMonitorService>();
    services.AddSingleton<DeezSpoTag.Services.Matching.TrackMatchService>();
    services.AddSingleton<DeezSpoTag.Services.Download.Queue.DownloadCancellationRegistry>();
    services.AddSingleton<DeezSpoTag.Services.Download.Queue.DownloadRetryScheduler>(sp =>
        new DeezSpoTag.Services.Download.Queue.DownloadRetryScheduler(
            sp.GetRequiredService<DeezSpoTag.Services.Download.Queue.DownloadQueueRepository>(),
            sp.GetRequiredService<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>(),
            sp.GetRequiredService<DeezSpoTag.Services.Download.IActivityLogWriter>(),
            sp.GetRequiredService<DeezSpoTag.Services.Download.Shared.Models.IDeezSpoTagListener>(),
            sp.GetRequiredService<ILogger<DeezSpoTag.Services.Download.Queue.DownloadRetryScheduler>>(),
            sp.GetRequiredService<DeezSpoTag.Services.Download.Queue.DownloadCancellationRegistry>(),
            () => sp.GetRequiredService<DeezSpoTag.Web.Services.AutoTagService>().HasRunningJobs()));
    services.AddSingleton<DeezSpoTag.Web.Services.SystemStatsService>();
    services.AddSingleton<DeezSpoTag.Services.Download.Qobuz.IQobuzDownloadService, DeezSpoTag.Services.Download.Qobuz.QobuzDownloadService>();
    services.AddSingleton<DeezSpoTag.Services.Download.Qobuz.QobuzEngineProcessor>();
    services.AddSingleton<DeezSpoTag.Services.Download.Amazon.IAmazonDownloadService, DeezSpoTag.Services.Download.Amazon.AmazonDownloadService>();
    services.AddSingleton<DeezSpoTag.Services.Download.Amazon.AmazonEngineProcessor>();
    services.AddSingleton<DeezSpoTag.Services.Download.Tidal.TidalDownloadService>();
    services.AddSingleton<DeezSpoTag.Services.Download.Tidal.TidalEngineProcessor>();
    services.AddSingleton<DeezSpoTag.Services.Download.Apple.IAppleDownloadService, DeezSpoTag.Services.Download.Apple.AppleDownloadService>();
    services.AddSingleton<DeezSpoTag.Services.Download.Apple.AppleWebPlaybackClient>();
    services.AddSingleton<DeezSpoTag.Services.Download.Apple.AppleHlsDownloader>();
    services.AddSingleton<DeezSpoTag.Services.Download.Apple.AppleExternalToolRunner>();
    services.AddSingleton<DeezSpoTag.Services.Download.Apple.AppleWidevineLicenseClient>();
    services.AddSingleton<DeezSpoTag.Services.Download.Apple.AppleWrapperDecryptor>();
    services.AddSingleton<DeezSpoTag.Services.Download.Apple.AppleEngineProcessor>();
    services.AddSingleton<DeezSpoTag.Web.Services.IDownloadIntentBackgroundQueue, DeezSpoTag.Web.Services.DownloadIntentBackgroundQueue>();
    services.AddHostedService<DeezSpoTag.Web.Services.DownloadIntentBackgroundService>();
    services.AddScoped<DeezSpoTag.Web.Services.DownloadIntentService>();
    services.AddSingleton<DeezSpoTag.Services.Download.ISpotifyIdResolver, DeezSpoTag.Web.Services.SpotifyIdResolver>();
    services.AddHostedService<DeezSpoTag.Web.Services.SpotifyAuthWarmupService>();
    services.AddHostedService<DeezSpoTag.Web.Services.DeezerLoginWarmupService>();
    services.AddSingleton<LibraryDbService>();
    services.AddHostedService<DeezSpoTag.Web.Services.LibrarySchemaHostedService>();
    services.AddSingleton<DeezSpoTag.Services.Library.LibraryRepository>();
    services.AddSingleton<DeezSpoTag.Services.Library.AudioQualitySignalAnalyzer>();
    services.AddSingleton<DeezSpoTag.Web.Services.SpectrogramService>();
    services.AddSingleton<DeezSpoTag.Web.Services.LibraryConfigStore>();
    services.AddSingleton<DeezSpoTag.Web.Services.AutoTagFolderScopeDependencies>(sp =>
        new DeezSpoTag.Web.Services.AutoTagFolderScopeDependencies(
            sp.GetRequiredService<DeezSpoTag.Services.Library.LibraryRepository>(),
            sp.GetRequiredService<DeezSpoTag.Web.Services.LibraryConfigStore>()));
    services.AddSingleton<DeezSpoTag.Web.Services.LocalLibraryScanner>();
    services.AddSingleton<DeezSpoTag.Web.Services.LibraryScanRunner>();
    services.AddSingleton<DeezSpoTag.Web.Services.DeezerArtistImageService>();
    services.AddSingleton<DeezSpoTag.Web.Services.LibraryArtistImageQueueService>();
    services.AddHostedService(sp => sp.GetRequiredService<DeezSpoTag.Web.Services.LibraryArtistImageQueueService>());
    services.AddSingleton<DeezSpoTag.Web.Services.LyricsRefreshQueueService>();
    services.AddHostedService(sp => sp.GetRequiredService<DeezSpoTag.Web.Services.LyricsRefreshQueueService>());
    services.AddSingleton<DeezSpoTag.Web.Services.LibrarySpotifyArtistQueueService>();
    services.AddHostedService(sp => sp.GetRequiredService<DeezSpoTag.Web.Services.LibrarySpotifyArtistQueueService>());
    services.AddSingleton<DeezSpoTag.Web.Services.SpotifyArtistImageCacheService>();
    services.AddSingleton<DeezSpoTag.Web.Services.ArtistMetadataUpdaterService>();
    services.AddHostedService(sp => sp.GetRequiredService<DeezSpoTag.Web.Services.ArtistMetadataUpdaterService>());
    services.AddSingleton<DeezSpoTag.Services.Library.MixService>();
    services.AddSingleton<DeezSpoTag.Services.Library.RadioService>();
    services.AddSingleton<DeezSpoTag.Services.Library.DeezerTrackRecommendationService>();
    services.AddSingleton<DeezSpoTag.Web.Services.LibraryRecommendationService.LibraryRecommendationCollaborators>(sp =>
        new DeezSpoTag.Web.Services.LibraryRecommendationService.LibraryRecommendationCollaborators
        {
            DeezerRecommendations = sp.GetRequiredService<DeezSpoTag.Services.Library.DeezerTrackRecommendationService>(),
            Repository = sp.GetRequiredService<DeezSpoTag.Services.Library.LibraryRepository>(),
            ShazamRecognitionService = sp.GetRequiredService<DeezSpoTag.Web.Services.ShazamRecognitionService>(),
            ShazamDiscoveryService = sp.GetRequiredService<DeezSpoTag.Web.Services.ShazamDiscoveryService>(),
            DeezerClient = sp.GetRequiredService<DeezSpoTag.Integrations.Deezer.DeezerClient>(),
            DeezerGatewayService = sp.GetRequiredService<DeezSpoTag.Integrations.Deezer.DeezerGatewayService>(),
            SongLinkResolver = sp.GetRequiredService<DeezSpoTag.Services.Download.Utils.SongLinkResolver>()
        });
    services.AddSingleton<DeezSpoTag.Web.Services.LibraryRecommendationService>();
    services.AddHostedService<DeezSpoTag.Web.Services.LibraryRecommendationAutomationHostedService>();
    services.AddSingleton<DeezSpoTag.Web.Services.PlexHistoryImportService>();
    services.AddSingleton<DeezSpoTag.Web.Services.MixSyncService>();
    services.AddSingleton<DeezSpoTag.Web.Services.VibeAnalysisSettingsStore>();
    services.AddSingleton<DeezSpoTag.Web.Services.TrackAnalysisBackgroundService>();
    services.AddHostedService(sp => sp.GetRequiredService<DeezSpoTag.Web.Services.TrackAnalysisBackgroundService>());
    services.AddSingleton<DeezSpoTag.Web.Services.VibeMatchService>();
    services.AddSingleton<DeezSpoTag.Web.Services.LastFmTagService>();
    services.AddSingleton<DeezSpoTag.Web.Services.MoodMixPreferencesStore>();
    services.AddSingleton<DeezSpoTag.Web.Services.MoodBucketService>();
    services.AddSingleton<DeezSpoTag.Web.Services.MoodBucketBackgroundService>();
    services.AddHostedService(sp => sp.GetRequiredService<DeezSpoTag.Web.Services.MoodBucketBackgroundService>());
    services.AddSingleton<DeezSpoTag.Web.Services.MoodMixService>();
    services.AddSingleton<DeezSpoTag.Web.Services.DownloadOrchestrationService>();
    services.AddHostedService(sp => sp.GetRequiredService<DeezSpoTag.Web.Services.DownloadOrchestrationService>());
    services.AddSingleton<DeezSpoTag.Web.Services.DuplicateCleanerService>();
    services.AddSingleton<DeezSpoTag.Web.Services.QualityScannerService>();
    services.AddHostedService<DeezSpoTag.Web.Services.QualityScannerAutomationHostedService>();
    services.Configure<DeezSpoTag.Web.Services.MelodayOptions>(configuration.GetSection("Meloday"));
    services.AddSingleton<DeezSpoTag.Web.Services.MelodayCollaborators>();
    services.AddSingleton<DeezSpoTag.Web.Services.MelodayService>();
    services.AddSingleton<DeezSpoTag.Web.Services.MelodaySettingsStore>();
    services.AddHostedService<DeezSpoTag.Web.Services.MelodayHostedService>();
    services.AddHostedService<DeezSpoTag.Web.Services.PlexMetadataRefreshService>();
}

static void RegisterDeezerServices(IServiceCollection services, IConfiguration configuration)
{
    services.AddSingleton<DeezSpoTag.Integrations.Deezer.DeezerClient>();
    services.AddSingleton<DeezSpoTag.Integrations.Deezer.DeezerSessionManager>(sp =>
        new DeezSpoTag.Integrations.Deezer.DeezerSessionManager(
            sp.GetRequiredService<ILogger<DeezSpoTag.Integrations.Deezer.DeezerSessionManager>>(),
            () => sp.GetRequiredService<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>().LoadSettings()));
    services.AddSingleton<DeezSpoTag.Integrations.Deezer.DeezerGatewayService>(sp =>
    {
        var service = new DeezSpoTag.Integrations.Deezer.DeezerGatewayService(
            sp.GetRequiredService<ILogger<DeezSpoTag.Integrations.Deezer.DeezerGatewayService>>());
        service.SetSessionManager(sp.GetRequiredService<DeezSpoTag.Integrations.Deezer.DeezerSessionManager>());
        return service;
    });
    services.AddSingleton<DeezSpoTag.Integrations.Deezer.DeezerApiService>(sp =>
    {
        var service = new DeezSpoTag.Integrations.Deezer.DeezerApiService(
            sp.GetRequiredService<ILogger<DeezSpoTag.Integrations.Deezer.DeezerApiService>>());
        service.SetSessionManager(sp.GetRequiredService<DeezSpoTag.Integrations.Deezer.DeezerSessionManager>());
        return service;
    });

    services.AddHttpClient("DeezerClient")
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                SslProtocols = DeezSpoTag.Services.Utils.TlsPolicy.GetSslProtocols(configuration),
                ClientCertificateOptions = ClientCertificateOption.Manual,
                PreAuthenticate = false,
                UseDefaultCredentials = false,
                AutomaticDecompression = DecompressionMethods.GZip |
                                         DecompressionMethods.Deflate |
                                         DecompressionMethods.Brotli,
                MaxConnectionsPerServer = 100
            };
            DeezSpoTag.Services.Utils.TlsPolicy.ApplyIfAllowed(handler, configuration);
            return handler;
        })
        .ConfigureHttpClient(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(30);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/79.0.3945.130 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "*/*");
            client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            client.DefaultRequestHeaders.Add("Connection", "keep-alive");
        });

    services.AddHttpClient("DeezerDownload")
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                SslProtocols = DeezSpoTag.Services.Utils.TlsPolicy.GetSslProtocols(configuration),
                MaxConnectionsPerServer = 10
            };
            DeezSpoTag.Services.Utils.TlsPolicy.ApplyIfAllowed(handler, configuration);
            return handler;
        });
}

static async Task InitializeApplicationAsync(WebApplication app, IConfiguration configuration)
{
    using var scope = app.Services.CreateScope();
    var dbService = scope.ServiceProvider.GetRequiredService<DeezSpoTag.Services.Library.LibraryDbService>();
    await dbService.EnsureSchemaAsync();
    await RunStartupMigrationsAsync(scope.ServiceProvider, app.Logger);

    var configStore = scope.ServiceProvider.GetRequiredService<DeezSpoTag.Web.Services.LibraryConfigStore>();
    configStore.ClearLogs();

    var playlistCoverService = scope.ServiceProvider.GetRequiredService<DeezSpoTag.Web.Services.PlaylistCoverService>();
    await playlistCoverService.LogStartupStatusAsync();

    var identityDb = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
    await identityDb.Database.EnsureCreatedAsync();

    await EnforceIdentityStartupStateAsync(scope.ServiceProvider);
}

static async Task RunStartupMigrationsAsync(IServiceProvider services, ILogger logger)
{
    try
    {
        var migrationService = services.GetRequiredService<DeezSpoTag.Web.Services.TagSettingsMigrationService>();
        await migrationService.MigrateAsync();
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogWarning(ex, "Tagging settings migration failed.");
    }
}

static async Task EnforceIdentityStartupStateAsync(IServiceProvider services)
{
    var loginConfig = services.GetRequiredService<IOptions<LoginConfiguration>>().Value;
    var userManager = services.GetRequiredService<UserManager<AppUser>>();
    var logger = services.GetRequiredService<ILogger<Program>>();
    const bool isSingleUserMode = true;
    var bootstrapUserFromEnvironment = Environment.GetEnvironmentVariable("DEEZSPOTAG_BOOTSTRAP_USER");
    var bootstrapPassFromEnvironment = Environment.GetEnvironmentVariable("DEEZSPOTAG_BOOTSTRAP_PASS");
    var seedUsername = loginConfig.Username;
    var seedPassword = loginConfig.Password;
    var seedEnabled = loginConfig.EnableSeeding;

    if (string.IsNullOrWhiteSpace(seedUsername) || string.IsNullOrWhiteSpace(seedPassword))
    {
        seedUsername = bootstrapUserFromEnvironment ?? seedUsername;
        seedPassword = bootstrapPassFromEnvironment ?? seedPassword;
        if (!string.IsNullOrWhiteSpace(seedUsername) && !string.IsNullOrWhiteSpace(seedPassword))
        {
            seedEnabled = true;
        }
    }

    // Do not auto-reset passwords just because bootstrap env vars are present.
    // Bootstrap creds are for first-run seeding; explicit reset must be opt-in.
    var resetPasswordOnSeed = loginConfig.ResetPasswordOnSeed;

    if (seedEnabled &&
        !string.IsNullOrWhiteSpace(seedUsername) &&
        !string.IsNullOrWhiteSpace(seedPassword))
    {
        await EnsureSeedUserAsync(
            userManager,
            logger,
            loginConfig,
            isSingleUserMode,
            seedUsername,
            seedPassword,
            resetPasswordOnSeed);
    }

    await RemoveDuplicatePasswordClaimsAsync(userManager, logger, seedUsername, seedPassword);

    if (isSingleUserMode)
    {
        var hasBootstrapCredentials = !string.IsNullOrWhiteSpace(seedUsername) &&
                                      !string.IsNullOrWhiteSpace(seedPassword);
        if (!hasBootstrapCredentials && !await userManager.Users.AnyAsync())
        {
            throw new InvalidOperationException(
                "Single-user mode requires an initial account. Configure LoginConfiguration Username/Password " +
                "or set DEEZSPOTAG_BOOTSTRAP_USER and DEEZSPOTAG_BOOTSTRAP_PASS before startup.");
        }

        await EnforceSingleUserModeAsync(userManager, logger, loginConfig, bootstrapUserFromEnvironment);
    }
}

static async Task EnsureSeedUserAsync(
    UserManager<AppUser> userManager,
    ILogger logger,
    LoginConfiguration loginConfig,
    bool isSingleUserMode,
    string seedUsername,
    string seedPassword,
    bool resetPasswordOnSeed)
{
    var normalizedSeedUserName = userManager.NormalizeName(seedUsername);
    var existing = await userManager.Users
        .OrderBy(u => u.Id)
        .FirstOrDefaultAsync(u => u.NormalizedUserName == normalizedSeedUserName);
    if (existing != null)
    {
        if (resetPasswordOnSeed)
        {
            await EnsureSeedUserPasswordAsync(userManager, logger, existing, seedPassword);
        }

        return;
    }

    if (isSingleUserMode && await userManager.Users.AnyAsync())
    {
        logger.LogWarning(
            "Single-user mode skipped bootstrap user creation for '{SeedUser}' because an ASP.NET Identity account already exists.",
            seedUsername);
        return;
    }

    existing = new AppUser { UserName = seedUsername, EmailConfirmed = true };
    var result = await userManager.CreateAsync(existing, seedPassword);
    if (result.Succeeded && loginConfig.RequirePasswordChange)
    {
        await userManager.AddClaimAsync(existing, new Claim("must_change_password", "true"));
    }

    if (!result.Succeeded)
    {
        return;
    }

    await userManager.SetLockoutEnabledAsync(existing, true);
    await userManager.SetLockoutEndDateAsync(existing, null);
    await userManager.ResetAccessFailedCountAsync(existing);
    logger.LogInformation("Bootstrap user '{User}' created and lockout cleared.", seedUsername);
}

static async Task EnsureSeedUserPasswordAsync(
    UserManager<AppUser> userManager,
    ILogger logger,
    AppUser user,
    string seedPassword)
{
    if (string.IsNullOrWhiteSpace(seedPassword))
    {
        return;
    }

    var hasPassword = await userManager.HasPasswordAsync(user);
    if (hasPassword && await userManager.CheckPasswordAsync(user, seedPassword))
    {
        await userManager.SetLockoutEnabledAsync(user, true);
        await userManager.SetLockoutEndDateAsync(user, null);
        await userManager.ResetAccessFailedCountAsync(user);
        return;
    }

    IdentityResult result;
    if (hasPassword)
    {
        var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
        result = await userManager.ResetPasswordAsync(user, resetToken, seedPassword);
    }
    else
    {
        result = await userManager.AddPasswordAsync(user, seedPassword);
    }

    if (!result.Succeeded)
    {
        logger.LogWarning(
            "Bootstrap credential password reset failed for '{UserName}' ({UserId}): {Errors}",
            user.UserName ?? UnknownValue,
            user.Id,
            string.Join("; ", result.Errors.Select(e => e.Description)));
        return;
    }

    await userManager.SetLockoutEnabledAsync(user, true);
    await userManager.SetLockoutEndDateAsync(user, null);
    await userManager.ResetAccessFailedCountAsync(user);
    logger.LogInformation(
        "Bootstrap credential password enforced for '{UserName}' ({UserId}).",
        user.UserName ?? UnknownValue,
        user.Id);
}

static async Task RemoveDuplicatePasswordClaimsAsync(
    UserManager<AppUser> userManager,
    ILogger logger,
    string? seedUsername,
    string? seedPassword)
{
    var hasSeedCredentials = !string.IsNullOrWhiteSpace(seedUsername) &&
                             !string.IsNullOrWhiteSpace(seedPassword);
    var normalizedSeedUserNameForCleanup = hasSeedCredentials
        ? userManager.NormalizeName(seedUsername)
        : null;

    var allIdentityUsers = await userManager.Users
        .OrderBy(u => u.Id)
        .ToListAsync();
    foreach (var identityUser in allIdentityUsers)
    {
        var claims = await userManager.GetClaimsAsync(identityUser);
        var mustChangeClaims = claims
            .Where(c => c.Type == "must_change_password")
            .ToList();
        var duplicateMustChangeClaims = mustChangeClaims
            .Skip(1)
            .ToList();
        foreach (var duplicateClaim in duplicateMustChangeClaims)
        {
            await userManager.RemoveClaimAsync(identityUser, duplicateClaim);
        }

        if (duplicateMustChangeClaims.Count > 0)
        {
            logger.LogWarning(
                "Removed {DuplicateCount} duplicate must_change_password claims for '{UserName}'.",
                duplicateMustChangeClaims.Count,
                identityUser.UserName ?? identityUser.Id);
        }

        if (mustChangeClaims.Count == 0 || !hasSeedCredentials)
        {
            continue;
        }

        var hasSeedUserName = string.Equals(
            identityUser.NormalizedUserName,
            normalizedSeedUserNameForCleanup,
            StringComparison.Ordinal);
        var hasSeedPassword = await userManager.CheckPasswordAsync(identityUser, seedPassword!);

        if (hasSeedUserName && hasSeedPassword)
        {
            continue;
        }

        foreach (var mustChangeClaim in mustChangeClaims)
        {
            await userManager.RemoveClaimAsync(identityUser, mustChangeClaim);
        }

        logger.LogInformation(
            "Removed stale must_change_password claim(s) for '{UserName}' because credentials no longer match seeded defaults.",
            identityUser.UserName ?? identityUser.Id);
    }
}

static async Task EnforceSingleUserModeAsync(
    UserManager<AppUser> userManager,
    ILogger logger,
    LoginConfiguration loginConfig,
    string? bootstrapUserFromEnvironment)
{
    var explicitCanonicalUserName = ResolveExplicitCanonicalUserName(loginConfig, bootstrapUserFromEnvironment);
    var orderedUsers = await LoadOrderedUsersAsync(userManager);
    var canonicalUser = ResolveExplicitCanonicalUser(userManager, orderedUsers, logger, explicitCanonicalUserName);
    if (canonicalUser == null && string.IsNullOrWhiteSpace(explicitCanonicalUserName))
    {
        orderedUsers = await ClearPolicyLockoutsAsync(userManager, orderedUsers, logger);
    }

    canonicalUser ??= ResolveCanonicalFallbackUser(orderedUsers);
    if (canonicalUser == null)
    {
        LogDeferredSingleUserMode(logger, orderedUsers.Count);
        return;
    }

    await EnsureCanonicalUserSignInEnabledAsync(userManager, canonicalUser);
    var deletedUsers = await DeleteNonCanonicalUsersAsync(userManager, canonicalUser, logger);
    LogSingleUserModeEnforcement(logger, canonicalUser, deletedUsers);
}

static string? ResolveExplicitCanonicalUserName(LoginConfiguration loginConfig, string? bootstrapUserFromEnvironment)
{
    if (!string.IsNullOrWhiteSpace(bootstrapUserFromEnvironment))
    {
        return bootstrapUserFromEnvironment;
    }

    return loginConfig.EnableSeeding ? loginConfig.Username : null;
}

static async Task<List<AppUser>> LoadOrderedUsersAsync(UserManager<AppUser> userManager)
    => await userManager.Users
        .OrderBy(u => u.Id)
        .ToListAsync();

static AppUser? ResolveExplicitCanonicalUser(
    UserManager<AppUser> userManager,
    List<AppUser> orderedUsers,
    ILogger logger,
    string? explicitCanonicalUserName)
{
    if (string.IsNullOrWhiteSpace(explicitCanonicalUserName))
    {
        return null;
    }

    var normalizedCanonicalUserName = userManager.NormalizeName(explicitCanonicalUserName);
    var canonicalUser = orderedUsers.FirstOrDefault(u => u.NormalizedUserName == normalizedCanonicalUserName);
    if (canonicalUser == null)
    {
        logger.LogWarning(
            "Single-user identity mode has explicit canonical username '{CanonicalUserName}', but that account does not exist.",
            explicitCanonicalUserName);
    }

    return canonicalUser;
}

static async Task<List<AppUser>> ClearPolicyLockoutsAsync(
    UserManager<AppUser> userManager,
    List<AppUser> orderedUsers,
    ILogger logger)
{
    if (orderedUsers.Count <= 1)
    {
        return orderedUsers;
    }

    var policyLockThreshold = DateTimeOffset.UtcNow.AddYears(50);
    var policyLockedUsers = orderedUsers
        .Where(u => u.LockoutEnabled &&
                    u.LockoutEnd.HasValue &&
                    u.LockoutEnd.Value > policyLockThreshold)
        .ToList();
    if (policyLockedUsers.Count == 0)
    {
        return orderedUsers;
    }

    foreach (var policyLocked in policyLockedUsers)
    {
        await EnsureCanonicalUserSignInEnabledAsync(userManager, policyLocked);
    }

    logger.LogWarning(
        "Single-user identity mode cleared {Count} long-duration lockout(s) because no explicit canonical username is configured.",
        policyLockedUsers.Count);

    return await LoadOrderedUsersAsync(userManager);
}

static AppUser? ResolveCanonicalFallbackUser(List<AppUser> orderedUsers)
{
    static bool IsSignInEnabled(AppUser user)
        => !user.LockoutEnabled ||
           !user.LockoutEnd.HasValue ||
           user.LockoutEnd.Value <= DateTimeOffset.UtcNow;

    var activeUsers = orderedUsers.Where(IsSignInEnabled).ToList();
    if (activeUsers.Count > 0)
    {
        return activeUsers[0];
    }

    return orderedUsers.Count > 0 ? orderedUsers[0] : null;
}

static void LogDeferredSingleUserMode(ILogger logger, int userCount)
{
    if (userCount == 0)
    {
        logger.LogInformation("Single-user identity mode is enabled but no ASP.NET Identity account exists yet.");
        return;
    }

    logger.LogWarning(
        "Single-user identity mode found {UserCount} ASP.NET Identity account(s) with no explicit canonical username. Lockout enforcement is deferred until the next successful login.",
        userCount);
}

static async Task EnsureCanonicalUserSignInEnabledAsync(UserManager<AppUser> userManager, AppUser user)
{
    await userManager.SetLockoutEnabledAsync(user, true);
    await userManager.SetLockoutEndDateAsync(user, null);
    await userManager.ResetAccessFailedCountAsync(user);
}

static async Task<int> DeleteNonCanonicalUsersAsync(
    UserManager<AppUser> userManager,
    AppUser canonicalUser,
    ILogger logger)
{
    var otherUsers = await userManager.Users
        .Where(u => u.Id != canonicalUser.Id)
        .ToListAsync();
    var deletedUsers = 0;
    foreach (var other in otherUsers)
    {
        var deleteResult = await userManager.DeleteAsync(other);
        if (deleteResult.Succeeded)
        {
            deletedUsers++;
            continue;
        }

        logger.LogWarning(
            "Failed to delete non-canonical ASP.NET Identity account '{UserName}' ({UserId}): {Errors}",
            other.UserName ?? UnknownValue,
            other.Id,
            string.Join("; ", deleteResult.Errors.Select(e => e.Description)));
    }

    return deletedUsers;
}

static void LogSingleUserModeEnforcement(ILogger logger, AppUser canonicalUser, int deletedUsers)
{
    if (deletedUsers > 0)
    {
        logger.LogWarning(
            "Single-user identity mode active. Canonical account is '{CanonicalUser}' ({CanonicalId}); deleted {DeletedCount} additional ASP.NET Identity account(s).",
            canonicalUser.UserName ?? UnknownValue,
            canonicalUser.Id,
            deletedUsers);
        return;
    }

    logger.LogInformation(
        "Single-user identity mode active. Canonical account is '{CanonicalUser}' ({CanonicalId}) and no additional ASP.NET Identity accounts were found to delete.",
        canonicalUser.UserName ?? UnknownValue,
        canonicalUser.Id);
}

}
