using DeezSpoTag.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeezSpoTag.Web.Areas.Identity.Pages.Account;

[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class LogoutModel : PageModel
{
    private readonly SignInManager<AppUser> _signInManager;
    private readonly ILogger<LogoutModel> _logger;

    public LogoutModel(SignInManager<AppUser> signInManager, ILogger<LogoutModel> logger)
    {
        _signInManager = signInManager;
        _logger = logger;
    }

    public Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        return SignOutAndRedirectAsync(returnUrl);
    }

    public Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        return SignOutAndRedirectAsync(returnUrl);
    }

    private async Task<IActionResult> SignOutAndRedirectAsync(string? returnUrl)
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            await _signInManager.SignOutAsync();
            _logger.LogInformation("User logged out.");
        }

        var target = ResolveReturnUrl(returnUrl);
        return LocalRedirect(target);
    }

    private string ResolveReturnUrl(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return returnUrl;
        }

        return "/Identity/Account/Login";
    }
}
