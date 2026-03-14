using System.Text;
using DeezSpoTag.Services.Crypto;

namespace DeezSpoTag.Services.Authentication;

/// <summary>
/// Crypto service for authentication operations
/// Ported from: /deezspotag/deezspotag/src/utils/crypto.ts
/// </summary>
public interface ICryptoService
{
    /// <summary>
    /// Generate MD5 hash from string (exact port from deezspotag _md5 function)
    /// </summary>
    string MD5Hash(string input);
}

/// <summary>
/// Implementation of crypto service for deezspotag authentication
/// </summary>
public class CryptoService : ICryptoService
{
    /// <summary>
    /// Generate MD5 hash from string (exact port from deezspotag _md5 function)
    /// Ported from: /deezspotag/deezspotag/src/utils/crypto.ts _md5 function
    /// </summary>
    public string MD5Hash(string input)
    {
        // Protocol compatibility: deezspotag auth requires the legacy MD5 digest format.
        return LegacyMd5.ComputeHexLower(input, Encoding.UTF8);
    }
}
