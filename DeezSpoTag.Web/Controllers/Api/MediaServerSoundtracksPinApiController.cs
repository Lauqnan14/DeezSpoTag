using System.Security.Cryptography;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/media-server/soundtracks/pin")]
[Authorize]
public sealed class MediaServerSoundtracksPinApiController : ControllerBase
{
    private const int LibraryPinMinLength = 4;
    private const int LibraryPinSaltBytes = 16;
    private const int LibraryPinHashBytes = 32;
    private const int LibraryPinPbkdf2Iterations = 120_000;
    private readonly UserPreferencesStore _userPreferencesStore;

    public MediaServerSoundtracksPinApiController(UserPreferencesStore userPreferencesStore)
    {
        _userPreferencesStore = userPreferencesStore;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetLibraryPinStatus()
    {
        var prefs = await _userPreferencesStore.LoadAsync();
        return Ok(new MediaServerLibraryPinStatusDto
        {
            Configured = IsLibraryPinConfigured(prefs)
        });
    }

    [HttpPost("unlock")]
    public async Task<IActionResult> UnlockLibrariesWithPin([FromBody] MediaServerLibraryPinUnlockRequest request)
    {
        var pin = NormalizePin(request?.Pin);
        if (string.IsNullOrWhiteSpace(pin))
        {
            return BadRequest(new { error = "Enter a PIN first." });
        }

        var prefs = await _userPreferencesStore.LoadAsync();
        if (!IsLibraryPinConfigured(prefs))
        {
            var confirmation = NormalizePin(request?.ConfirmationPin);
            if (pin.Length < LibraryPinMinLength)
            {
                return BadRequest(new { error = $"PIN must be at least {LibraryPinMinLength} characters." });
            }

            if (string.IsNullOrWhiteSpace(confirmation))
            {
                return BadRequest(new { error = "Confirm your new PIN." });
            }

            if (!string.Equals(pin, confirmation, StringComparison.Ordinal))
            {
                return BadRequest(new { error = "PIN confirmation does not match." });
            }

            var salt = RandomNumberGenerator.GetBytes(LibraryPinSaltBytes);
            var hash = DeriveLibraryPinHash(pin, salt);
            prefs.MediaServerLibraryPinSalt = Convert.ToBase64String(salt);
            prefs.MediaServerLibraryPinHash = Convert.ToBase64String(hash);
            await _userPreferencesStore.SaveAsync(prefs);
            return Ok(new MediaServerLibraryPinUnlockResultDto
            {
                Unlocked = true,
                Created = true
            });
        }

        if (!ValidateLibraryPin(pin, prefs.MediaServerLibraryPinSalt, prefs.MediaServerLibraryPinHash))
        {
            return Unauthorized(new { error = "Invalid PIN." });
        }

        return Ok(new MediaServerLibraryPinUnlockResultDto
        {
            Unlocked = true,
            Created = false
        });
    }

    [HttpPost("change")]
    public async Task<IActionResult> ChangeLibraryPin([FromBody] MediaServerLibraryPinChangeRequest request)
    {
        var currentPin = NormalizePin(request?.CurrentPin);
        if (string.IsNullOrWhiteSpace(currentPin))
        {
            return BadRequest(new { error = "Enter current PIN first." });
        }

        var newPin = NormalizePin(request?.NewPin);
        if (newPin.Length < LibraryPinMinLength)
        {
            return BadRequest(new { error = $"PIN must be at least {LibraryPinMinLength} characters." });
        }

        var confirmation = NormalizePin(request?.ConfirmationPin);
        if (string.IsNullOrWhiteSpace(confirmation))
        {
            return BadRequest(new { error = "Confirm your new PIN." });
        }

        if (!string.Equals(newPin, confirmation, StringComparison.Ordinal))
        {
            return BadRequest(new { error = "PIN confirmation does not match." });
        }

        var prefs = await _userPreferencesStore.LoadAsync();
        if (!IsLibraryPinConfigured(prefs))
        {
            return BadRequest(new { error = "No PIN is configured yet." });
        }

        if (!ValidateLibraryPin(currentPin, prefs.MediaServerLibraryPinSalt, prefs.MediaServerLibraryPinHash))
        {
            return Unauthorized(new { error = "Invalid current PIN." });
        }

        var salt = RandomNumberGenerator.GetBytes(LibraryPinSaltBytes);
        var hash = DeriveLibraryPinHash(newPin, salt);
        prefs.MediaServerLibraryPinSalt = Convert.ToBase64String(salt);
        prefs.MediaServerLibraryPinHash = Convert.ToBase64String(hash);
        await _userPreferencesStore.SaveAsync(prefs);
        return Ok(new MediaServerLibraryPinStatusDto
        {
            Configured = true
        });
    }

    [HttpPost("reset")]
    public async Task<IActionResult> ResetLibraryPin([FromBody] MediaServerLibraryPinResetRequest request)
    {
        var currentPin = NormalizePin(request?.CurrentPin);
        if (string.IsNullOrWhiteSpace(currentPin))
        {
            return BadRequest(new { error = "Enter current PIN first." });
        }

        var prefs = await _userPreferencesStore.LoadAsync();
        if (!IsLibraryPinConfigured(prefs))
        {
            return Ok(new MediaServerLibraryPinStatusDto
            {
                Configured = false
            });
        }

        if (!ValidateLibraryPin(currentPin, prefs.MediaServerLibraryPinSalt, prefs.MediaServerLibraryPinHash))
        {
            return Unauthorized(new { error = "Invalid current PIN." });
        }

        prefs.MediaServerLibraryPinSalt = null;
        prefs.MediaServerLibraryPinHash = null;
        await _userPreferencesStore.SaveAsync(prefs);
        return Ok(new MediaServerLibraryPinStatusDto
        {
            Configured = false
        });
    }

    private static bool IsLibraryPinConfigured(UserPreferencesDto prefs)
    {
        return !string.IsNullOrWhiteSpace(prefs.MediaServerLibraryPinHash)
            && !string.IsNullOrWhiteSpace(prefs.MediaServerLibraryPinSalt);
    }

    private static string NormalizePin(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static bool ValidateLibraryPin(string pin, string? encodedSalt, string? encodedHash)
    {
        if (string.IsNullOrWhiteSpace(pin)
            || string.IsNullOrWhiteSpace(encodedSalt)
            || string.IsNullOrWhiteSpace(encodedHash))
        {
            return false;
        }

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(encodedSalt);
            expectedHash = Convert.FromBase64String(encodedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        var actualHash = DeriveLibraryPinHash(pin, salt);
        return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }

    private static byte[] DeriveLibraryPinHash(string pin, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            pin,
            salt,
            LibraryPinPbkdf2Iterations,
            HashAlgorithmName.SHA256,
            LibraryPinHashBytes);
    }
}
