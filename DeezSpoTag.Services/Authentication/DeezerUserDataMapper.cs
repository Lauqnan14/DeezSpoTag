using DeezSpoTag.Core.Models.Deezer;

namespace DeezSpoTag.Services.Authentication;

public static class DeezerUserDataMapper
{
    public static UserData? ToLoginUserData(DeezerUser? user)
    {
        if (user == null)
        {
            return null;
        }

        return new UserData
        {
            Id = user.Id?.ToString() ?? "0",
            Name = user.Name ?? string.Empty,
            Picture = user.Picture ?? string.Empty,
            Country = user.Country ?? string.Empty,
            CanStreamLossless = user.CanStreamLossless,
            CanStreamHq = user.CanStreamHq,
            LicenseToken = user.LicenseToken,
            LovedTracks = user.LovedTracks?.ToString()
        };
    }
}
