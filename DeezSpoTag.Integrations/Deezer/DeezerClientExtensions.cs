using DeezSpoTag.Core.Models.Deezer;

namespace DeezSpoTag.Integrations.Deezer;

/// <summary>
/// Extensions for DeezerClient to support authentication service
/// </summary>
public static class DeezerClientExtensions
{
    /// <summary>
    /// Login using ARL (Account Request Login) token (alias for compatibility)
    /// </summary>
    public static async Task<bool> LoginWithArlAsync(this DeezerClient client, string arl, int? child = null)
    {
        return await client.LoginViaArlAsync(arl, child ?? 0);
    }

    /// <summary>
    /// Get user data (for authentication service)
    /// </summary>
    public static async Task<DeezerUser?> GetUserDataAsync(this DeezerClient client)
    {
        await Task.CompletedTask;
        return client.CurrentUser;
    }

    /// <summary>
    /// Logout and clear session
    /// </summary>
    public static async Task LogoutAsync(this DeezerClient client)
    {
        // Reset login state
        var loggedInField = typeof(DeezerClient).GetProperty("LoggedIn");
        loggedInField?.SetValue(client, false);

        var currentUserField = typeof(DeezerClient).GetProperty("CurrentUser");
        currentUserField?.SetValue(client, null);

        var childrenField = typeof(DeezerClient).GetProperty("Children");
        if (childrenField?.GetValue(client) is List<DeezerUser> children)
        {
            children.Clear();
        }

        var selectedAccountField = typeof(DeezerClient).GetProperty("SelectedAccount");
        selectedAccountField?.SetValue(client, 0);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Get lyrics for a track
    /// </summary>
    public static async Task<Dictionary<string, object>?> GetLyricsAsync(this DeezerClient client, string lyricsId)
    {
        if (!client.LoggedIn)
        {
            throw new InvalidOperationException("Must be logged in to get lyrics");
        }

        return await client.GetLyricsAsync(lyricsId);
    }
}