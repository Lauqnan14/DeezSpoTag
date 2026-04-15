using DeezSpoTag.Integrations.Deezer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Shared;

/// <summary>
/// Service to manage download concurrency limits based on account type
/// Implements safety measures to prevent account bans
/// Ported from deezspotag concurrency management logic
/// </summary>
public class ConcurrencyLimitService
{
    private readonly ILogger<ConcurrencyLimitService> _logger;
    private readonly IServiceProvider _serviceProvider;

    // Concurrency limits to prevent account bans
    private const int SINGLE_USER_MAX_CONCURRENT = 1;
    private const int FAMILY_ACCOUNT_MAX_CONCURRENT = 6;
    private const int DEFAULT_MAX_CONCURRENT = 1; // Safe default

    private bool? _isFamilyAccount;
    private DateTime _lastAccountCheck = DateTime.MinValue;
    private readonly TimeSpan _accountCheckCacheTime = TimeSpan.FromMinutes(30);

    public ConcurrencyLimitService(
        ILogger<ConcurrencyLimitService> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Get the maximum allowed concurrent downloads for the current account
    /// </summary>
    public async Task<int> GetMaxConcurrentDownloadsAsync()
    {
        try
        {
            var isFamilyAccount = await IsFamilyAccountAsync();
            var maxConcurrent = isFamilyAccount ? FAMILY_ACCOUNT_MAX_CONCURRENT : SINGLE_USER_MAX_CONCURRENT;

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Account type: {AccountType}, Max concurrent downloads: {MaxConcurrent}",
                    isFamilyAccount ? "Family" : "Single User", maxConcurrent);            }

            return maxConcurrent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to determine account type, using safe default of {DefaultConcurrent}", DEFAULT_MAX_CONCURRENT);
            return DEFAULT_MAX_CONCURRENT;
        }
    }

    /// <summary>
    /// Check if the current account is a family account
    /// Uses caching to avoid excessive API calls
    /// </summary>
    public async Task<bool> IsFamilyAccountAsync()
    {
        // Use cached result if available and not expired
        if (_isFamilyAccount.HasValue && DateTime.UtcNow - _lastAccountCheck < _accountCheckCacheTime)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Using cached family account status: {IsFamilyAccount}", _isFamilyAccount.Value);            }
            return _isFamilyAccount.Value;
        }

        try
        {
            _logger.LogDebug("Checking account type via Deezer API");

            // Use service provider to get the scoped DeezerGatewayService
            using var scope = _serviceProvider.CreateScope();
            var deezerGatewayService = scope.ServiceProvider.GetRequiredService<DeezerGatewayService>();

            var userData = await deezerGatewayService.GetUserDataAsync();

            // Family account detection logic ported from deezspotag
            var multiAccount = userData.User?.MultiAccount;
            var isFamilyAccount = multiAccount?.Enabled == true &&
                                 !multiAccount.IsSubAccount;

            _isFamilyAccount = isFamilyAccount;
            _lastAccountCheck = DateTime.UtcNow;

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Account type determined: {AccountType} (MultiAccount.Enabled: {Enabled}, IsSubAccount: {IsSubAccount})",
                    isFamilyAccount ? "Family Account" : "Single User Account",
                    userData.User?.MultiAccount?.Enabled,
                    userData.User?.MultiAccount?.IsSubAccount);            }

            return isFamilyAccount;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to check account type, defaulting to single user");
            _isFamilyAccount = false;
            _lastAccountCheck = DateTime.UtcNow;
            return false;
        }
    }

    /// <summary>
    /// Get the recommended concurrency for track downloads within a collection (album/playlist)
    /// This is separate from the queue-level concurrency
    /// </summary>
    public async Task<int> GetTrackConcurrencyAsync()
    {
        try
        {
            var isFamilyAccount = await IsFamilyAccountAsync();

            // For track-level concurrency within collections, use more conservative limits
            // to avoid overwhelming the API even for family accounts
            var trackConcurrency = isFamilyAccount ? 3 : 1;

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Track-level concurrency: {TrackConcurrency} for {AccountType}",
                    trackConcurrency, isFamilyAccount ? "Family Account" : "Single User Account");            }

            return trackConcurrency;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to determine track concurrency, using safe default of 1");
            return 1;
        }
    }

    /// <summary>
    /// Validate and adjust user-configured concurrency settings
    /// Ensures they don't exceed safe limits for the account type
    /// </summary>
    public async Task<int> ValidateConcurrencySettingAsync(int userConfiguredConcurrency)
    {
        var maxAllowed = await GetMaxConcurrentDownloadsAsync();

        if (userConfiguredConcurrency > maxAllowed)
        {
            _logger.LogWarning("User configured concurrency ({UserConcurrency}) exceeds safe limit ({ConfiguredMaxAllowed}) for account type. Limiting to {AppliedMaxAllowed}",
                userConfiguredConcurrency, maxAllowed, maxAllowed);
            return maxAllowed;
        }

        if (userConfiguredConcurrency <= 0)
        {
            _logger.LogWarning("Invalid user configured concurrency ({UserConcurrency}), using default of {DefaultConcurrent}",
                userConfiguredConcurrency, DEFAULT_MAX_CONCURRENT);
            return DEFAULT_MAX_CONCURRENT;
        }

        return userConfiguredConcurrency;
    }

    /// <summary>
    /// Clear the cached account type (useful for testing or when account changes)
    /// </summary>
    public void ClearCache()
    {
        _isFamilyAccount = null;
        _lastAccountCheck = DateTime.MinValue;
        _logger.LogDebug("Cleared account type cache");
    }

    /// <summary>
    /// Get account type information for display purposes
    /// </summary>
    public async Task<AccountTypeInfo> GetAccountTypeInfoAsync()
    {
        try
        {
            var isFamilyAccount = await IsFamilyAccountAsync();
            var maxConcurrent = await GetMaxConcurrentDownloadsAsync();
            var trackConcurrency = await GetTrackConcurrencyAsync();

            return new AccountTypeInfo
            {
                IsFamilyAccount = isFamilyAccount,
                AccountType = isFamilyAccount ? "Family Account" : "Single User Account",
                MaxConcurrentDownloads = maxConcurrent,
                RecommendedTrackConcurrency = trackConcurrency,
                LastChecked = _lastAccountCheck
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to get account type info");
            return new AccountTypeInfo
            {
                IsFamilyAccount = false,
                AccountType = "Unknown (Safe Mode)",
                MaxConcurrentDownloads = DEFAULT_MAX_CONCURRENT,
                RecommendedTrackConcurrency = 1,
                LastChecked = DateTime.MinValue
            };
        }
    }
}

/// <summary>
/// Information about the account type and concurrency limits
/// </summary>
public class AccountTypeInfo
{
    public bool IsFamilyAccount { get; set; }
    public string AccountType { get; set; } = "";
    public int MaxConcurrentDownloads { get; set; }
    public int RecommendedTrackConcurrency { get; set; }
    public DateTime LastChecked { get; set; }
}
