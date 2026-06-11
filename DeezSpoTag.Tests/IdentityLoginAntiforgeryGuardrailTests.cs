using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class IdentityLoginAntiforgeryGuardrailTests
{
    [Fact]
    public void IdentityLoginForm_EmitsExplicitAntiforgeryToken()
    {
        var root = ResolveRepoRoot();
        var loginPagePath = Path.Join(root, "DeezSpoTag.Web", "Areas", "Identity", "Pages", "Account", "Login.cshtml");
        var viewImportsPath = Path.Join(root, "DeezSpoTag.Web", "Areas", "Identity", "Pages", "_ViewImports.cshtml");

        Assert.True(File.Exists(loginPagePath), $"Missing login page: {loginPagePath}");
        Assert.True(File.Exists(viewImportsPath), $"Missing Identity view imports: {viewImportsPath}");

        var loginPageSource = File.ReadAllText(loginPagePath);
        var viewImportsSource = File.ReadAllText(viewImportsPath);

        Assert.Contains("@removeTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers", viewImportsSource, StringComparison.Ordinal);
        Assert.Contains("method=\"post\"", loginPageSource, StringComparison.Ordinal);
        Assert.Contains("@Html.AntiForgeryToken()", loginPageSource, StringComparison.Ordinal);
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test output path.");
    }
}
