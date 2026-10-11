using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyAuthStageTest
{
    [Fact]
    public async Task Stage_ReturnsResultAndPropagatesCancellationWithStageIdentity()
    {
        var type = typeof(SpotifyPathfinderMetadataClient);
        var client = (SpotifyPathfinderMetadataClient)RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, NullLogger<SpotifyPathfinderMetadataClient>.Instance);
        var stateType = type.GetNestedType("BlobAuthState", BindingFlags.NonPublic)!;
        var state = Activator.CreateInstance(stateType, nonPublic: true)!;
        var method = type.GetMethod("RunAuthStageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(string));
        var result = (Task<string>)method.Invoke(client, [state, "session_config", (Func<CancellationToken, Task<string>>)(_ => Task.FromResult("config")), CancellationToken.None])!;
        Assert.Equal("config", await result);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var task = (Task<string>)method.Invoke(client, [state, "client_token", (Func<CancellationToken, Task<string>>)(token => Task.FromCanceled<string>(token)), canceled.Token])!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.Equal("client_token", stateType.GetProperty("ActiveStage")!.GetValue(state));
    }
}
