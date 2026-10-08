using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Services.Library;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Tests that a host which registers the queue can build the Soulseek engine.
/// </summary>
/// <remarks>
///     <para>
///         The other engines are supplied their download services by each composition root - the workers host
///         registers Qobuz, Tidal, Amazon and Apple explicitly - so their processors are not resolvable from
///         the queue registration alone and that is expected.
///     </para>
///     <para>
///         Soulseek is different. Its processor is registered inside the shared <c>AddDeezSpoTagQueue</c> block,
///         so every host that calls it has to be able to satisfy it. It used not to: the services lived only in
///         the web host, and <c>DeezSpoTagApp</c> resolves the whole processor collection with
///         <c>GetServices&lt;IQueueEngineProcessor&gt;</c>, which constructs every registration. The workers host
///         therefore threw and lost the entire download queue, not just Soulseek.
///     </para>
/// </remarks>
public sealed class EngineProcessorResolutionTest
{
    /// <summary>
    ///     A collection with the queue registration plus the handful of services every host supplies itself.
    /// </summary>
    private static ServiceProvider BuildHostProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = "Data Source=:memory:",
                ["ConnectionStrings:Library"] = "Data Source=:memory:"
            })
            .Build();

        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton(NullLoggerFactory.Instance)
            .AddSingleton<LibraryRepository>(_ => new LibraryRepository(
                configuration,
                NullLogger<LibraryRepository>.Instance))
            .AddDeezSpoTagQueue()
            .BuildServiceProvider();
    }

    [Fact]
    public void TheSoulseekProcessorIsActivatableFromTheSharedQueueRegistration()
    {
        // This is the workers host's situation: no platform auth, no hub, no Soulseek services registered
        // outside the shared block.
        using var provider = BuildHostProvider();

        var processor = ActivatorUtilities.CreateInstance<SoulseekEngineProcessor>(provider);

        Assert.Equal("soulseek", processor.Engine);
    }

    [Fact]
    public void EverySoulseekServiceResolvesFromTheSharedQueueRegistration()
    {
        using var provider = BuildHostProvider();

        Assert.NotNull(provider.GetRequiredService<ISoulseekDownloadService>());
        Assert.NotNull(provider.GetRequiredService<ISoulseekSearchService>());
        Assert.NotNull(provider.GetRequiredService<ISoulseekTransferService>());
        Assert.NotNull(provider.GetRequiredService<ISoulseekShareService>());
        Assert.NotNull(provider.GetRequiredService<ISoulseekConnectionService>());
        Assert.NotNull(provider.GetRequiredService<ISoulseekPeerPolicyService>());
        Assert.NotNull(provider.GetRequiredService<ISoulseekResultScoringService>());
        Assert.NotNull(provider.GetRequiredService<SoulseekSettingsService>());
        Assert.NotNull(provider.GetRequiredService<SoulseekRepository>());
        Assert.NotNull(provider.GetRequiredService<ISoulseekRealtimePublisher>());
        Assert.NotNull(provider.GetRequiredService<ISoulseekCredentialProvider>());
    }

    [Fact]
    public void HousekeepingIsPickedUpByTheExistingQueueLoop()
    {
        using var provider = BuildHostProvider();

        var tasks = provider.GetServices<IQueueMaintenanceTask>().ToList();

        Assert.Contains(tasks, task => string.Equals(task.Engine, "soulseek", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AHostWithNoCredentialStoreResolvesTheEngineAndReportsItUnconfigured()
    {
        // The workers host has no encrypted credential store. A Soulseek item there must fail with the
        // ordinary "not configured" message rather than being unable to construct the graph at all.
        using var provider = BuildHostProvider();

        var credentials = provider.GetRequiredService<ISoulseekCredentialProvider>();
        var connection = provider.GetRequiredService<ISoulseekConnectionService>();

        Assert.Null(credentials.GetCredentialsAsync().GetAwaiter().GetResult());
        Assert.False(connection.IsAvailableAsync().GetAwaiter().GetResult());
    }

    [Fact]
    public void TheSharedRegistrationBlockIsTheSinglePlaceSoulseekIsWired()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "../../../../");

        var queueSource = File.ReadAllText(Path.Combine(
            root, "DeezSpoTag.Services", "Download", "Shared", "DeezSpoTagServiceExtensions.cs"));

        Assert.Contains("services.AddSoulseekDownloadEngine();", queueSource, StringComparison.Ordinal);
        Assert.Contains(
            "AddScoped<IQueueEngineProcessor, DeezSpoTag.Services.Download.Soulseek.SoulseekEngineProcessor>",
            queueSource,
            StringComparison.Ordinal);

        // The web host may only add what genuinely depends on web types, so the engine's own services must not
        // be re-declared there.
        var programSource = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web", "Program.cs"));

        Assert.DoesNotContain("ISoulseekDownloadService, ", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ISoulseekSearchService>(sp =>", programSource, StringComparison.Ordinal);
        Assert.Contains(
            "ISoulseekCredentialProvider, DeezSpoTag.Web.Services.PlatformAuthSoulseekCredentialProvider",
            programSource,
            StringComparison.Ordinal);
    }
}
