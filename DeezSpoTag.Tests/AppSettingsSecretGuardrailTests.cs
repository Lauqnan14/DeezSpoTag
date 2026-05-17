using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AppSettingsSecretGuardrailTests
{
    [Fact]
    public void LoginConfigurationPassword_InTrackedAppSettingsFiles_MustBeEmpty()
    {
        var webRoot = Path.Combine(ResolveRepoRoot(), "DeezSpoTag.Web");
        var candidates = Directory
            .EnumerateFiles(webRoot, "appsettings*.json", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".template.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(candidates);

        foreach (var path in candidates)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("LoginConfiguration", out var login))
            {
                continue;
            }

            if (!login.TryGetProperty("Password", out var password))
            {
                continue;
            }

            Assert.True(
                password.ValueKind == JsonValueKind.String
                && string.IsNullOrEmpty(password.GetString()),
                $"LoginConfiguration:Password must be empty in committed settings file '{Path.GetFileName(path)}'.");
        }
    }

    private static string ResolveRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var candidate = Path.GetFullPath(Path.Combine(current, "..", "..", "..", ".."));
            if (Directory.Exists(Path.Combine(candidate, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Combine(candidate, "DeezSpoTag.Tests")))
            {
                return candidate;
            }

            var parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }

            current = parent.FullName;
        }

        throw new InvalidOperationException("Could not resolve repository root.");
    }
}
