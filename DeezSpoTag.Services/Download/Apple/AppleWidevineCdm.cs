using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;

namespace DeezSpoTag.Services.Download.Apple;

public sealed class AppleWidevineCdm
{
    private AppleWidevineCdm()
    {
    }

    private const string DefaultPrivateKeyPemBase64 = "LS0tLS1CRUdJTiBSU0EgUFJJVkFURSBLRVktLS0tLQpNSUlFcEFJQkFBS0NBUUVBMmJPM3l2RndObklIc2JEbDNNVGpLZERzaUJXc3VaV09HVnhJbkZXQVZNcCtuZmZHCllscXVUS3BKdXJFcnk5NXlwcmNSQjNoWWh2QTVnaHNBQ2lkY1dQREVQVnFxUlo3WVhMZXZ5VUErU24ySnhwdnQKT2N3eUZIYlN3cnVOeHByV09rSENUNzc0TzRML3dKVXQ1eDJDNGlGQ3JKQnlqdzBvbU44dStFSGRhdnZIN1pQbgpiMy9FWnAvY3BaYTkvK0hPa3V0dkJIQnZhUHAxOEY4SlFoelVROU13THVERlRyK1FMREI1K1k1N0plMnROWURLCnhEMUsrRWQ1SmEwQTRPS2hQS0l3UHdQcmUwbnQ1c2NqTGJhM0xTQUt0S3hpR3FGdFdPNFU3VGYxWXJkakp2Mm8KOW84U2Y4cWNuYnB6dlE0S3dGcWVodUpuQjcrVzdtZEpKdzEyUFFJREFRQUJBb0lCQUNFMzJ3T01jNkxiSTNGcApuS2xqSVladjZxZVpKeEhxVUJSdWtHWEtaaHFLQzJmdk5zWXJNQTFpcm4xZUsyQ2dRTDVQa0xtakUxOERxTUxCCmUvQVFzWGFneGxEV1ZNVHF4L2pkem1UVytLcEZIWkRBbWlJSGxseXBCTi9SM29BL2dCRERsL0t6SVExem43S3oKRUo0RFVzVk9iZTRHM0hRWGZlcFZvOFVkeDd0YkI3WDZ3SGUya0VnRnlZM2xQZHZ1YmlrMEM0dDRpcFNENzl5NwpTZlc3WFZBNVhVUW1xTjRVMmtXTTB1U3d6ZDRCQTdocXlTY0pzeWdmNktncE1XUFMyeEZaRVpRUlVwWWNCSDQ4CkU3WXFOcnJsWVAzeWFRKzlKeDU2a0tTMG12djN2VVhTN0FmVWJVOENpSHdEOUkzQkd3c3dFVXVlT0dHVmVYYngKdEZGOHM4RUNnWUVBOTdCRGNML2J0K3IzcUpGMGR4dE1CNVpuZ0piRng5UmRzYmxZZXBWcGJscjJVZnhuRnR0TwpQb05TS2E0VzM2SHVEc3VuNDlka2FvQUJKV2R0WnMySHk2cSt4dkVnb3p2aE1hQlZFM3NwblduekNUMXlUTVlMCkcwMnVERWwwZFBpVGcxMTZiVkVsYXN3dHFNWHZubnBiT1RNVGU3SWc5c1dpVVcvR0g5Uk0rTjhDZ1lFQTRRSGIKK09BMEJmY3piVlFQOUIrcGx0NG1BdXU0QkRtNEdQd3ExeVhPV28zQ3Q4SWsrSGVZMWhxT09icGZ5UU1BemErRQplL2tQNlc4dlhwaUVsR3JtaVViVFhLNFJ6bWYreVllT3J2bDNEODBiRnE0R3RETkFJUUQzanBqNnpqbFQrR3p3Ckk1MDFnUng1aVBsNGZTY2NSU2Rwb2VyaTdGOUFOdGM2RUVHRnlHTUNnWUVBak16bldZWEhHa0w0N0J0YmtJVzAKNzY5QlFTajBYNGRLaDhnc0V1c3lsdWdnbERTZVNiRDdSckFTR2QxNzVUN0EvQ29yVTJyVEMzT2VzeXViVmxCSgovSzRnYXlrUmU1bURoMWwwWTNHbEUzWHlFWE9ic1NiM2sxclNNT3ZreHNXejNYNWJKUjkyM01JYXhwRldpTWxYCmFDbXZ6cVpROU5jZVVacnZqcEo1K3hNQ2dZQUphOEtDRVNFY2Z0VXdacXlrVkE4TnVnOXRYK0U4akE0aFBhMnQKaEcrM2F1Z1VPWlRDc244N3Q3RHN5ZGpvMmE5VzdWcG10bTdzSHpPa2lrNUN5SmNPZUdDeEtMaW1JOFNQTzVYRgp6YndtZFRnRkl4UTB4MUNRRVRKTVRpdHlKd1JWQ25xamd4bVNabGJRWFdHbUc5VWJNQ05FSEVtVURBanNRdWF6CmQ0cmFjUUtCZ1FEUjFZMmthbHZsZVlHcmh3Y0E4TFRuSWgwcllFZkF0OVl4Tm1UaTVxREtmNVFQdlVQMnYrV08KZlNCNWNvVXFSOExCd2VIRTVWOEpnRnQ3NGZkTEJxWlYvazJ6L2RJMHIrRVFXbXBaMnVQRUMwS2hrL1NiOWlSRApmSDdhdDNQTXVzcmt3WkNHWjhiZUZFQXI2aWNYY2xWMDhuUENOR0I2V2NrYWNmenBBajhBemc9PQotLS0tLUVORCBSU0EgUFJJVkFURSBLRVktLS0tLQ==";
    private static readonly string DefaultPrivateKeyPem = Encoding.UTF8.GetString(Convert.FromBase64String(DefaultPrivateKeyPemBase64));

    private const string DefaultClientIdBase64 = "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";

    public static AppleWidevineRequest BuildRequest(byte[] widevineHeaderBytes)
    {
        var requestId = BuildSessionId();
        var clientIdBytes = Convert.FromBase64String(DefaultClientIdBase64);
        var licenseRequestMsg = AppleWidevineProto.BuildLicenseRequest(clientIdBytes, widevineHeaderBytes, requestId);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(DefaultPrivateKeyPem);
        // Widevine legacy contract requires SHA-1 for request signing and OAEP mode.
        var hash = ComputeSha1Digest(licenseRequestMsg);
        var signature = rsa.SignHash(hash, HashAlgorithmName.SHA1, RSASignaturePadding.Pss);

        var signedRequest = AppleWidevineProto.BuildSignedLicenseRequest(licenseRequestMsg, signature);
        return new AppleWidevineRequest(licenseRequestMsg, signedRequest);
    }

    public static byte[] ExtractContentKey(byte[] licenseResponse, byte[] licenseRequestMsg)
    {
        var signed = AppleWidevineProto.ParseSignedLicense(licenseResponse);
        if (signed.SessionKey.Length == 0 || signed.LicenseMessage.Length == 0)
        {
            return Array.Empty<byte>();
        }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(DefaultPrivateKeyPem);
        var sessionKey = rsa.Decrypt(signed.SessionKey, RSAEncryptionPadding.OaepSHA1);

        var encryptionKey = BuildEncryptionKey(sessionKey, licenseRequestMsg);
        if (encryptionKey.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var keys = AppleWidevineProto.ParseLicenseKeys(signed.LicenseMessage);
        var contentKey = keys
            .Where(static key => key.Type == 2)
            .Select(key => DecryptKey(encryptionKey, key.Iv, key.Key))
            .FirstOrDefault();
        return contentKey ?? Array.Empty<byte>();
    }

    private static byte[] BuildEncryptionKey(byte[] sessionKey, byte[] licenseRequestMsg)
    {
        var input = new List<byte>();
        input.Add(1);
        input.AddRange(Encoding.ASCII.GetBytes("ENCRYPTION"));
        input.Add(0);
        input.AddRange(licenseRequestMsg);
        input.AddRange(new byte[] { 0, 0, 0, 0x80 });

        var mac = new CMac(new Org.BouncyCastle.Crypto.Engines.AesEngine());
        mac.Init(new KeyParameter(sessionKey));
        mac.BlockUpdate(input.ToArray(), 0, input.Count);
        var output = new byte[mac.GetMacSize()];
        mac.DoFinal(output, 0);
        return output;
    }

    private static byte[] ComputeSha1Digest(byte[] payload)
    {
        var digest = new Sha1Digest();
        digest.BlockUpdate(payload, 0, payload.Length);
        var output = new byte[digest.GetDigestSize()];
        digest.DoFinal(output, 0);
        return output;
    }

    private static byte[] DecryptKey(byte[] encryptionKey, byte[] iv, byte[] encryptedKey)
    {
        using var aes = Aes.Create();
        aes.Key = encryptionKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(encryptedKey, 0, encryptedKey.Length);
        return Unpad(decrypted);
    }

    private static byte[] Unpad(byte[] data)
    {
        if (data.Length == 0)
        {
            return data;
        }

        var pad = data[^1];
        if (pad <= 0 || pad > data.Length)
        {
            return data;
        }

        return data[..^pad];
    }

    private static byte[] BuildSessionId()
    {
        var session = new byte[32];
        var chars = Encoding.ASCII.GetBytes("ABCDEF0123456789");
        for (var i = 0; i < 16; i++)
        {
            session[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        }

        session[16] = (byte)'0';
        session[17] = (byte)'1';
        for (var i = 18; i < 32; i++)
        {
            session[i] = (byte)'0';
        }

        return session;
    }
}

public sealed record AppleWidevineRequest(byte[] LicenseRequestMsg, byte[] SignedRequest);
