using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace DeezSpoTag.Services.Security;

public sealed class ProtectedCredentialFileStore
{
    private const string ProtectedMarker = "deezspotag-protected-credential";
    private readonly IDataProtector _protector;
    private readonly string _purpose;

    public ProtectedCredentialFileStore(IDataProtectionProvider provider, string purpose)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(purpose))
        {
            throw new ArgumentException("Protection purpose is required.", nameof(purpose));
        }

        _purpose = purpose.Trim();
        _protector = provider.CreateProtector(_purpose);
    }

    public string Purpose => _purpose;

    public async Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var stored = await File.ReadAllTextAsync(path, cancellationToken);
        return TryUnprotect(stored, out var plaintext) ? plaintext : stored;
    }

    public async Task<string?> ReadTextAndMigrateAsync(string path, CancellationToken cancellationToken = default)
    {
        var stored = await ReadStoredTextAsync(path, cancellationToken);
        if (stored == null)
        {
            return null;
        }

        if (TryUnprotect(stored, out var plaintext))
        {
            return plaintext;
        }

        await WriteTextAsync(path, stored, cancellationToken);
        return stored;
    }

    public async Task WriteTextAsync(string path, string plaintext, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Credential file path is required.", nameof(path));
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var protectedText = _protector.Protect(plaintext ?? string.Empty);
        var envelope = JsonSerializer.Serialize(
            new ProtectedCredentialEnvelope(ProtectedMarker, _purpose, protectedText));
        await WriteTextAtomicallyAsync(path, envelope, cancellationToken);
    }

    public static bool IsProtectedText(string? stored)
        => TryReadEnvelope(stored, out _);

    public bool IsProtectedForPurpose(string? stored)
        => TryReadEnvelope(stored, out var envelope)
           && string.Equals(envelope.Purpose, _purpose, StringComparison.Ordinal);

    private static async Task<string?> ReadStoredTextAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    private bool TryUnprotect(string? stored, out string plaintext)
    {
        plaintext = string.Empty;
        if (!TryReadEnvelope(stored, out var envelope))
        {
            return false;
        }
        if (!string.Equals(envelope.Purpose, _purpose, StringComparison.Ordinal))
        {
            return false;
        }

        plaintext = _protector.Unprotect(envelope.Payload);
        return true;
    }

    private static bool TryReadEnvelope(string? stored, out ProtectedCredentialEnvelope envelope)
    {
        envelope = null!;
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ProtectedCredentialEnvelope>(stored);
            if (parsed is not null
                && string.Equals(parsed.Format, ProtectedMarker, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(parsed.Payload))
            {
                envelope = parsed;
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static async Task WriteTextAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var tempPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private sealed record ProtectedCredentialEnvelope(string Format, string Purpose, string Payload);
}
