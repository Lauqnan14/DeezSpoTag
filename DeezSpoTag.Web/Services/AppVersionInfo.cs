using System.Reflection;
using System.Text.RegularExpressions;
using DeezSpoTag.Web.Services.Updates;
using Microsoft.Extensions.Options;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// The version of DeezSpoTag this instance is actually running, plus the repository coordinates
/// the version is reported against.
/// </summary>
/// <remarks>
/// Resolution order is unchanged from the original in-program helper: an injected
/// <c>DEEZSPOTAG_BUILD_VERSION</c> wins, then the assembly informational version, then the assembly
/// name version. A build whose version is not a four-part number is reported verbatim so an
/// unrecognised value is visible rather than silently rewritten.
/// </remarks>
public sealed partial class AppVersionInfo
{
    /// <summary>The placeholder used when no version can be resolved at all.</summary>
    public const string UnknownVersion = "unknown";

    private readonly ILogger<AppVersionInfo> _logger;

    public AppVersionInfo(IOptions<AppVersionOptions> options, ILogger<AppVersionInfo> logger)
    {
        _logger = logger;
        var settings = options.Value;

        Branch = string.IsNullOrWhiteSpace(settings.Branch) ? "main" : settings.Branch.Trim();
        RepositoryOwner = string.IsNullOrWhiteSpace(settings.Owner) ? "Lauqnan14" : settings.Owner.Trim();
        RepositoryName = string.IsNullOrWhiteSpace(settings.Repository) ? "DeezSpoTag" : settings.Repository.Trim();

        CurrentVersion = ResolveBuildDisplayVersion(typeof(AppVersionInfo).Assembly, ResolveFallbackVersion());
        RepositoryUrl = $"https://github.com/{RepositoryOwner}/{RepositoryName}";
        BranchUrl = $"{RepositoryUrl}/tree/{Uri.EscapeDataString(Branch)}";

        if (!AppVersionOptions.TryResolveChannel(Branch, out _))
        {
            _logger.LogWarning(
                "AppVersion:Branch '{Branch}' is not a recognised release branch; update checks are disabled for this instance.",
                Branch);
        }
    }

    /// <summary>The running build version, normalized to a leading <c>v</c> (for example <c>v0.1.27.5</c>).</summary>
    public string CurrentVersion { get; }

    /// <summary>The branch this build is reported against. Update checks only ever consider this branch.</summary>
    public string Branch { get; }

    public string RepositoryOwner { get; }

    public string RepositoryName { get; }

    /// <summary>The public repository URL.</summary>
    public string RepositoryUrl { get; }

    /// <summary>The URL of <see cref="Branch"/> within the repository.</summary>
    public string BranchUrl { get; }

    private static string ResolveFallbackVersion()
        => typeof(AppVersionInfo).Assembly.GetName().Version?.ToString() ?? UnknownVersion;

    private static string ResolveBuildDisplayVersion(Assembly entryAssembly, string fallbackVersion)
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

    private static string NormalizeBuildDisplayVersion(string? candidate)
    {
        var value = (candidate ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return UnknownVersion;
        }

        if (string.Equals(value, UnknownVersion, StringComparison.OrdinalIgnoreCase))
        {
            return UnknownVersion;
        }

        var match = BuildVersionPatternRegex().Match(value);
        if (!match.Success)
        {
            return value;
        }

        var core = match.Groups["core"].Value;
        return $"v{core}";
    }

    [GeneratedRegex(
        @"^v?(?<core>\d+\.\d+\.\d+\.\d+)(?:[-+][0-9A-Za-z][0-9A-Za-z.\-]*)?$",
        RegexOptions.CultureInvariant,
        250)]
    private static partial Regex BuildVersionPatternRegex();
}
