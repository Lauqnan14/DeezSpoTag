using DeezSpoTag.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using System.IO;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Jpeg;
using System.ComponentModel.DataAnnotations;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/account")]
[Authorize]
public sealed class AccountApiController : ControllerBase
{
    private const string MustChangePasswordClaim = "must_change_password";
    private const string AvatarPathClaim = "avatar_path";
    private readonly UserManager<AppUser> _userManager;
    private readonly SignInManager<AppUser> _signInManager;
    private readonly IConfiguration _configuration;
    private readonly bool _isSingleUserMode;

    public AccountApiController(
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        IConfiguration configuration)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _configuration = configuration;
        _isSingleUserMode = configuration.GetValue<bool>("IsSingleUser", true);
    }

    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            var claimName = User.FindFirstValue(ClaimTypes.Name);
            if (!string.IsNullOrWhiteSpace(claimName))
            {
                return Ok(new
                {
                    username = claimName,
                    avatarUrl = string.Empty
                });
            }
            return Unauthorized();
        }

        var claims = await _userManager.GetClaimsAsync(user);
        var avatarClaim = claims.FirstOrDefault(c => c.Type == AvatarPathClaim);
        var avatarUrl = avatarClaim != null ? "/api/account/avatar" : string.Empty;

        return Ok(new
        {
            username = user.UserName ?? string.Empty,
            avatarUrl
        });
    }

    [HttpGet("avatar")]
    public async Task<IActionResult> GetAvatar()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return Unauthorized();
        }

        var claims = await _userManager.GetClaimsAsync(user);
        var avatarClaim = claims.FirstOrDefault(c => c.Type == AvatarPathClaim);
        if (avatarClaim == null || string.IsNullOrWhiteSpace(avatarClaim.Value) || !System.IO.File.Exists(avatarClaim.Value))
        {
            return NotFound();
        }

        var contentType = avatarClaim.Value.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? "image/png"
            : "image/jpeg";

        return PhysicalFile(avatarClaim.Value, contentType);
    }

    [HttpPost("avatar")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> UploadAvatar([FromForm, Required] IFormFile avatar)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return Unauthorized();
        }

        await using var uploadStream = avatar.OpenReadStream();
        await using var buffer = new MemoryStream();
        await uploadStream.CopyToAsync(buffer);
        buffer.Position = 0;

        var detectedFormat = default(SixLabors.ImageSharp.Formats.IImageFormat);
        try
        {
            detectedFormat = await Image.DetectFormatAsync(buffer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return BadRequest(new { message = "Invalid image file." });
        }

        var isPng = string.Equals(detectedFormat?.Name, "PNG", StringComparison.OrdinalIgnoreCase);
        var isJpeg = string.Equals(detectedFormat?.Name, "JPEG", StringComparison.OrdinalIgnoreCase);
        if (!isPng && !isJpeg)
        {
            return BadRequest(new { message = "Only PNG or JPEG images are allowed." });
        }

        var dataDir = _configuration["DataDirectory"] ?? "Data";
        var avatarDir = Path.Join(dataDir, "avatars", user.Id);
        Directory.CreateDirectory(avatarDir);
        var extension = isPng ? ".png" : ".jpg";
        var avatarPath = Path.Join(avatarDir, "avatar" + extension);

        buffer.Position = 0;
        using (var image = await Image.LoadAsync(buffer))
        {
            image.Metadata.ExifProfile = null;
            image.Metadata.IccProfile = null;
            image.Metadata.XmpProfile = null;

            await using var output = System.IO.File.Create(avatarPath);
            if (isPng)
            {
                await image.SaveAsPngAsync(output, new PngEncoder());
            }
            else
            {
                await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = 90 });
            }
        }

        var claims = await _userManager.GetClaimsAsync(user);
        var existing = claims.FirstOrDefault(c => c.Type == AvatarPathClaim);
        if (existing != null)
        {
            await _userManager.RemoveClaimAsync(user, existing);
        }
        await _userManager.AddClaimAsync(user, new Claim(AvatarPathClaim, avatarPath));
        await _signInManager.RefreshSignInAsync(user);

        return Ok(new { message = "Avatar updated." });
    }

    [HttpPost("change-credentials")]
    public async Task<IActionResult> ChangeCredentials([FromBody] ChangeCredentialsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NewUsername) ||
            string.IsNullOrWhiteSpace(request.NewPassword) ||
            string.IsNullOrWhiteSpace(request.CurrentPassword))
        {
            return BadRequest(new { message = "All fields are required." });
        }

        return await ExecuteWithUpdatedPasswordAsync(
            request.CurrentPassword,
            request.NewPassword,
            async user =>
            {
                var usernameUpdateError = await TryUpdateUserNameAsync(user, request.NewUsername);
                if (usernameUpdateError != null)
                {
                    return usernameUpdateError;
                }

                var claims = await _userManager.GetClaimsAsync(user);
                var mustChangeClaims = claims.Where(c => c.Type == MustChangePasswordClaim).ToList();
                foreach (var mustChange in mustChangeClaims)
                {
                    await _userManager.RemoveClaimAsync(user, mustChange);
                }

                return await CompleteCredentialsUpdateAsync(user, "Credentials updated.");
            });
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CurrentPassword) ||
            string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new { message = "All fields are required." });
        }

        return await ExecuteWithUpdatedPasswordAsync(
            request.CurrentPassword,
            request.NewPassword,
            user => CompleteCredentialsUpdateAsync(user, "Password updated."));
    }

    [HttpPost("change-username")]
    public async Task<IActionResult> ChangeUsername([FromBody] ChangeUsernameRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CurrentPassword) ||
            string.IsNullOrWhiteSpace(request.NewUsername))
        {
            return BadRequest(new { message = "All fields are required." });
        }

        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return Unauthorized();
        }

        var passwordValid = await _userManager.CheckPasswordAsync(user, request.CurrentPassword);
        if (!passwordValid)
        {
            return BadRequest(new { message = "Current password is incorrect." });
        }

        var usernameUpdateError = await TryUpdateUserNameAsync(user, request.NewUsername);
        if (usernameUpdateError != null)
        {
            return usernameUpdateError;
        }

        await DeleteNonCanonicalAccountsAsync(user.Id);
        await _signInManager.RefreshSignInAsync(user);
        return Ok(new { message = "Username updated." });
    }

    private async Task DeleteNonCanonicalAccountsAsync(string canonicalUserId)
    {
        if (!_isSingleUserMode || string.IsNullOrWhiteSpace(canonicalUserId))
        {
            return;
        }

        var otherUsers = await _userManager.Users
            .Where(u => u.Id != canonicalUserId)
            .ToListAsync();
        foreach (var other in otherUsers)
        {
            await _userManager.DeleteAsync(other);
        }
    }

    private async Task<IActionResult?> TryChangePasswordAsync(AppUser user, string currentPassword, string newPassword)
    {
        var passwordResult = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (passwordResult.Succeeded)
        {
            return null;
        }

        return BadRequest(new { message = string.Join("; ", passwordResult.Errors.Select(e => e.Description)) });
    }

    private async Task<IActionResult?> TryUpdateUserNameAsync(AppUser user, string newUsername)
    {
        if (string.Equals(user.UserName, newUsername, StringComparison.Ordinal))
        {
            return null;
        }

        var setUserNameResult = await _userManager.SetUserNameAsync(user, newUsername);
        if (setUserNameResult.Succeeded)
        {
            return null;
        }

        return BadRequest(new { message = string.Join("; ", setUserNameResult.Errors.Select(e => e.Description)) });
    }

    private async Task<(AppUser? User, IActionResult? Error)> TryGetUserWithUpdatedPasswordAsync(string currentPassword, string newPassword)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return (null, Unauthorized());
        }

        var passwordUpdateError = await TryChangePasswordAsync(user, currentPassword, newPassword);
        return passwordUpdateError is null
            ? (user, null)
            : (null, passwordUpdateError);
    }

    private async Task<IActionResult> ExecuteWithUpdatedPasswordAsync(
        string currentPassword,
        string newPassword,
        Func<AppUser, Task<IActionResult>> onSuccess)
    {
        var (user, passwordUpdateError) = await TryGetUserWithUpdatedPasswordAsync(currentPassword, newPassword);
        if (passwordUpdateError is not null)
        {
            return passwordUpdateError;
        }

        if (user is null)
        {
            return Unauthorized();
        }

        return await onSuccess(user);
    }

    private async Task<IActionResult> CompleteCredentialsUpdateAsync(AppUser user, string message)
    {
        await DeleteNonCanonicalAccountsAsync(user.Id);
        await _signInManager.RefreshSignInAsync(user);
        return Ok(new { message });
    }

    public sealed class ChangeCredentialsRequest
    {
        public string CurrentPassword { get; set; } = string.Empty;
        public string NewUsername { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }

    public sealed class ChangePasswordRequest
    {
        public string CurrentPassword { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }

    public sealed class ChangeUsernameRequest
    {
        public string CurrentPassword { get; set; } = string.Empty;
        public string NewUsername { get; set; } = string.Empty;
    }
}
