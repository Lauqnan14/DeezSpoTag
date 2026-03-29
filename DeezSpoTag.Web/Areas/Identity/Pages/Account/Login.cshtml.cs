using DeezSpoTag.Web.Configuration;
using DeezSpoTag.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using DeezSpoTag.Web.Data;

namespace DeezSpoTag.Web.Areas.Identity.Pages.Account;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly SignInManager<AppUser> _signInManager;
    private readonly ILogger<LoginModel> _logger;
    private readonly UserManager<AppUser> _userManager;
    private readonly LoginConfiguration _loginConfig;
    private readonly AppIdentityDbContext _identityDb;
    private readonly bool _isSingleUserMode;
    private const string MustChangePasswordClaim = "must_change_password";
    private const string UnknownValue = "unknown";
    private const string InvalidSignInSessionMessage = "Sign-in session expired or invalid. Refresh this page and try again.";

    public LoginModel(
        SignInManager<AppUser> signInManager,
        UserManager<AppUser> userManager,
        IOptions<LoginConfiguration> loginOptions,
        IConfiguration configuration,
        ILogger<LoginModel> logger,
        AppIdentityDbContext identityDb)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _loginConfig = loginOptions.Value;
        _isSingleUserMode = configuration.GetValue<bool>("IsSingleUser", true);
        _logger = logger;
        _identityDb = identityDb;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IList<AuthenticationScheme> ExternalLogins { get; set; } = new List<AuthenticationScheme>();

    public string ReturnUrl { get; set; } = "/";

    public sealed class InputModel
    {
        [Required]
        [Display(Prompt = "Username")]
        public string Username { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [Display(Prompt = "Password")]
        public string Password { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        await EnsureSingleUserSeededAsync();
        ReturnUrl = returnUrl ?? Url.Content("~/");
        if (IsCsrfErrorQueryFlagEnabled())
        {
            ModelState.AddModelError(string.Empty, InvalidSignInSessionMessage);
        }

        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        ExternalLogins = (await _signInManager.GetExternalAuthenticationSchemesAsync()).ToList();

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        ReturnUrl = returnUrl ?? Url.Content("~/");

        await EnsureSingleUserSeededAsync();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var attemptedUsername = Input.Username?.Trim() ?? string.Empty;
        var attemptedUser = await _userManager.FindByNameAsync(attemptedUsername);
        if (attemptedUser == null)
        {
            ModelState.AddModelError(string.Empty, "No account found for that username.");
            return Page();
        }

        var result = await _signInManager.PasswordSignInAsync(attemptedUsername, Input.Password, true, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            return await CompleteSuccessfulSignInAsync(attemptedUser);
        }

        if (result.IsLockedOut)
        {
            attemptedUser = await _userManager.FindByNameAsync(attemptedUsername) ?? attemptedUser;

            if (_isSingleUserMode &&
                await TryRecoverSingleUserPolicyLockoutAsync(attemptedUser, Input.Password))
            {
                return await CompleteSuccessfulSignInAsync(attemptedUser);
            }

            _logger.LogWarning("User account locked out.");
            var lockoutEndUtc = attemptedUser.LockoutEnd?.UtcDateTime;
            if (lockoutEndUtc.HasValue && lockoutEndUtc.Value > DateTime.UtcNow)
            {
                ModelState.AddModelError(
                    string.Empty,
                    $"Account locked due to too many failed attempts. Try again after {lockoutEndUtc.Value:yyyy-MM-dd HH:mm:ss} UTC.");
            }
            else
            {
                ModelState.AddModelError(string.Empty, "Account locked due to too many failed attempts.");
            }
            return Page();
        }

        if (result.RequiresTwoFactor)
        {
            ModelState.AddModelError(string.Empty, "Two-factor authentication is required for this account.");
            return Page();
        }

        if (result.IsNotAllowed)
        {
            ModelState.AddModelError(string.Empty, "This account is not allowed to sign in.");
            return Page();
        }

        if (!await _userManager.CheckPasswordAsync(attemptedUser, Input.Password))
        {
            ModelState.AddModelError(string.Empty, "Incorrect password.");
            return Page();
        }

        ModelState.AddModelError(string.Empty, "Login failed for an unknown reason.");
        return Page();
    }

    private bool IsCsrfErrorQueryFlagEnabled()
    {
        return string.Equals(
            Request.Query["csrfError"],
            "1",
            StringComparison.Ordinal);
    }

    private async Task<IActionResult> CompleteSuccessfulSignInAsync(AppUser? user)
    {
        if (user != null)
        {
            if (_isSingleUserMode)
            {
                var canonicalUser = await ResolveCanonicalSingleUserAsync(user);
                if (canonicalUser != null &&
                    !string.Equals(canonicalUser.Id, user.Id, StringComparison.Ordinal))
                {
                    await _signInManager.SignOutAsync();
                    _logger.LogWarning(
                        "Blocked login for non-canonical account in single-user mode. attempted={AttemptedUser}({AttemptedId}) canonical={CanonicalUser}({CanonicalId})",
                        user.UserName ?? Input.Username,
                        user.Id,
                        canonicalUser.UserName ?? UnknownValue,
                        canonicalUser.Id);
                    ModelState.AddModelError(string.Empty, "Single-user mode only allows one account.");
                    return Page();
                }

                await EnforceSingleUserCanonicalAsync(user);
            }

            try
            {
                var connection = _identityDb.Database.GetDbConnection().ConnectionString;
                _logger.LogInformation("Login succeeded for user {UserName} id={UserId} identityDb={IdentityDb}",
                    user.UserName, user.Id, string.IsNullOrWhiteSpace(connection) ? UnknownValue : connection);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                _logger.LogInformation(ex, "Login succeeded for user {UserName} id={UserId}",
                    user.UserName, user.Id);
            }
        }

        _logger.LogInformation("User logged in.");
        return LocalRedirect(ReturnUrl);
    }

    private async Task<bool> TryRecoverSingleUserPolicyLockoutAsync(AppUser attemptedUser, string password)
    {
        if (!IsPolicyLockout(attemptedUser))
        {
            return false;
        }

        var preferredUserName = ResolvePreferredSingleUserName();
        if (!string.IsNullOrWhiteSpace(preferredUserName))
        {
            // Explicit canonical user configured: do not bypass lockout policy.
            return false;
        }

        var isPasswordValid = await _userManager.CheckPasswordAsync(attemptedUser, password);
        if (!isPasswordValid)
        {
            return false;
        }

        await _userManager.SetLockoutEnabledAsync(attemptedUser, true);
        await _userManager.SetLockoutEndDateAsync(attemptedUser, null);
        await _userManager.ResetAccessFailedCountAsync(attemptedUser);
        await _signInManager.SignInAsync(attemptedUser, isPersistent: true);
        _logger.LogInformation(
            "Recovered single-user policy lockout for {UserName} ({UserId}) after valid credential check.",
            attemptedUser.UserName ?? UnknownValue,
            attemptedUser.Id);
        return true;
    }

    private async Task EnsureSingleUserSeededAsync()
    {
        var seed = ResolveSeedCredentials();
        if (!seed.Enabled)
        {
            return;
        }

        var user = await FindSeedUserAsync(seed.Username);
        if (user == null && await IsSeedCreationBlockedAsync(seed.Username))
        {
            return;
        }

        var createdSeedUser = false;
        if (user == null)
        {
            user = await CreateSeedUserAsync(seed.Username, seed.Password);
            if (user == null)
            {
                return;
            }

            createdSeedUser = true;
        }

        // Do not overwrite credentials for an existing account.
        // In single-user mode, changed username/password must remain the source of truth.
        if (createdSeedUser && _loginConfig.RequirePasswordChange)
        {
            await EnsureMustChangePasswordClaimAsync(user);
        }
    }

    private (bool Enabled, string Username, string Password) ResolveSeedCredentials()
    {
        var configuredUsername = _loginConfig.Username;
        var configuredPassword = _loginConfig.Password;
        var hasConfiguredCredentials = !string.IsNullOrWhiteSpace(configuredUsername) &&
                                       !string.IsNullOrWhiteSpace(configuredPassword);
        if (hasConfiguredCredentials)
        {
            return (_loginConfig.EnableSeeding, configuredUsername!, configuredPassword!);
        }

        var envUsername = Environment.GetEnvironmentVariable("DEEZSPOTAG_BOOTSTRAP_USER");
        var envPassword = Environment.GetEnvironmentVariable("DEEZSPOTAG_BOOTSTRAP_PASS");
        var hasEnvironmentSeed = !string.IsNullOrWhiteSpace(envUsername) && !string.IsNullOrWhiteSpace(envPassword);
        if (!hasEnvironmentSeed)
        {
            return (false, string.Empty, string.Empty);
        }

        return (true, envUsername!, envPassword!);
    }

    private async Task<AppUser?> FindSeedUserAsync(string seedUsername)
    {
        var normalizedSeedUserName = _userManager.NormalizeName(seedUsername);
        return await _userManager.Users
            .OrderBy(u => u.Id)
            .FirstOrDefaultAsync(u => u.NormalizedUserName == normalizedSeedUserName);
    }

    private async Task<bool> IsSeedCreationBlockedAsync(string seedUsername)
    {
        if (!_isSingleUserMode || !await _userManager.Users.AnyAsync())
        {
            return false;
        }

        _logger.LogWarning(
            "Single-user mode skipped seeded user creation for '{SeedUser}' because an ASP.NET Identity account already exists.",
            seedUsername);
        return true;
    }

    private async Task<AppUser?> CreateSeedUserAsync(string seedUsername, string seedPassword)
    {
        var user = new AppUser { UserName = seedUsername, EmailConfirmed = true };
        var createResult = await _userManager.CreateAsync(user, seedPassword);
        if (createResult.Succeeded)
        {
            return user;
        }

        var errors = string.Join("; ", createResult.Errors.Select(e => e.Description));
        _logger.LogError("Failed to seed login user: {Errors}", errors);
        return null;
    }

    private async Task EnsureMustChangePasswordClaimAsync(AppUser user)
    {
        var existingClaims = await _userManager.GetClaimsAsync(user);
        if (existingClaims.Any(c =>
                c.Type == MustChangePasswordClaim &&
                string.Equals(c.Value, "true", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        await _userManager.AddClaimAsync(user, new System.Security.Claims.Claim(MustChangePasswordClaim, "true"));
    }

    private async Task<AppUser?> ResolveCanonicalSingleUserAsync(AppUser signedInUser)
    {
        var preferredUserName = ResolvePreferredSingleUserName();
        if (!string.IsNullOrWhiteSpace(preferredUserName))
        {
            var normalizedPreferred = _userManager.NormalizeName(preferredUserName);
            var configuredUser = await _userManager.Users
                .OrderBy(u => u.Id)
                .FirstOrDefaultAsync(u => u.NormalizedUserName == normalizedPreferred);
            if (configuredUser != null)
            {
                return configuredUser;
            }
        }

        var users = await _userManager.Users
            .OrderBy(u => u.Id)
            .ToListAsync();
        if (users.Count == 0)
        {
            return signedInUser;
        }

        var activeUsers = users.Where(IsUserSignInEnabled).ToList();
        if (activeUsers.Count == 1)
        {
            return activeUsers[0];
        }

        // No explicit canonical user exists yet; let the successful login establish it.
        return signedInUser;
    }

    private async Task EnforceSingleUserCanonicalAsync(AppUser canonicalUser)
    {
        await _userManager.SetLockoutEnabledAsync(canonicalUser, true);
        await _userManager.SetLockoutEndDateAsync(canonicalUser, null);
        await _userManager.ResetAccessFailedCountAsync(canonicalUser);

        var otherUsers = await _userManager.Users
            .Where(u => u.Id != canonicalUser.Id)
            .ToListAsync();
        var deletedUsers = 0;
        foreach (var other in otherUsers)
        {
            var deleteResult = await _userManager.DeleteAsync(other);
            if (deleteResult.Succeeded)
            {
                deletedUsers++;
            }
            else
            {
                _logger.LogWarning(
                    "Failed to delete non-canonical account {UserName} ({UserId}): {Errors}",
                    other.UserName ?? UnknownValue,
                    other.Id,
                    string.Join("; ", deleteResult.Errors.Select(e => e.Description)));
            }
        }

        if (deletedUsers > 0)
        {
            _logger.LogWarning(
                "Single-user mode deleted {Count} non-canonical account(s). Canonical user: {UserName} ({UserId})",
                deletedUsers,
                canonicalUser.UserName ?? UnknownValue,
                canonicalUser.Id);
        }
    }

    private static bool IsUserSignInEnabled(AppUser user)
    {
        if (!user.LockoutEnabled)
        {
            return true;
        }

        if (!user.LockoutEnd.HasValue)
        {
            return true;
        }

        return user.LockoutEnd.Value <= DateTimeOffset.UtcNow;
    }

    private static bool IsPolicyLockout(AppUser user)
    {
        if (!user.LockoutEnabled || !user.LockoutEnd.HasValue)
        {
            return false;
        }

        return user.LockoutEnd.Value > DateTimeOffset.UtcNow.AddYears(50);
    }

    private string? ResolvePreferredSingleUserName()
    {
        var environmentBootstrapUser = Environment.GetEnvironmentVariable("DEEZSPOTAG_BOOTSTRAP_USER");
        if (!string.IsNullOrWhiteSpace(environmentBootstrapUser))
        {
            return environmentBootstrapUser;
        }

        if (_loginConfig.EnableSeeding && !string.IsNullOrWhiteSpace(_loginConfig.Username))
        {
            return _loginConfig.Username;
        }

        return null;
    }
}
