using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;
public sealed class ShazamRelatedRuntimeTest
{
    [Fact]
    public async Task ConcurrentCallsShareProcessAndCancellationDoesNotCancelOtherCallers()
    {
        using var env = new TestEnvironment();
        await using var runtime = new ShazamRelatedRuntimeService(env, NullLogger<ShazamRelatedRuntimeService>.Instance);
        using var cancel = new CancellationTokenSource(40);
        var cancelled = runtime.GetRelatedAsync("123", "en-US", "US", cancel.Token);
        var calls = Enumerable.Range(0, 4).Select(_ => runtime.GetRelatedAsync("456", "en-US", "US", CancellationToken.None)).ToArray();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var docs = await Task.WhenAll(calls);
        var pids = docs.Select(d => d.RootElement.GetProperty("pid").GetInt32()).Distinct().ToArray();
        Assert.Single(pids);
        foreach (var doc in docs) doc.Dispose();
        await runtime.StopAsync(CancellationToken.None);
    }
    [Fact]
    public async Task RejectsInvalidIdsBeforeStartingProcess()
    {
        using var env = new TestEnvironment();
        await using var runtime = new ShazamRelatedRuntimeService(env, NullLogger<ShazamRelatedRuntimeService>.Instance);
        foreach (var id in new[] { "", "-1", "+1", "a", new string('1', 21) })
            await Assert.ThrowsAsync<ArgumentException>(() => runtime.GetRelatedAsync(id, "en-US", "US", CancellationToken.None));
    }
    [Fact]
    public async Task HelperCrashFailsPendingRequestAndNextCallStartsFreshProcess()
    {
        using var env = new TestEnvironment();
        File.WriteAllText(Path.Join(env.ContentRootPath, "Tools", "shazam_related", "runtime.mjs"),
            "import {createInterface} from 'node:readline';createInterface({input:process.stdin}).on('line',l=>{const q=JSON.parse(l);if(q.shazamId==='111')process.exit(2);console.log(JSON.stringify({requestId:q.requestId,ok:true,tracks:[],pid:process.pid}));});");
        await using var runtime = new ShazamRelatedRuntimeService(env, NullLogger<ShazamRelatedRuntimeService>.Instance);
        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => runtime.GetRelatedAsync("111", "en-US", "US", CancellationToken.None));
        using var result = await runtime.GetRelatedAsync("222", "en-US", "US", CancellationToken.None);
        Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
    }
    internal sealed class TestEnvironment : IWebHostEnvironment, IDisposable
    {
        public TestEnvironment()
        {
            ContentRootPath = Path.Join(Path.GetTempPath(), "shazam-helper-" + Guid.NewGuid().ToString("N"));
            var dir = Path.Join(ContentRootPath, "Tools", "shazam_related"); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Join(dir, "runtime.mjs"), "import { createInterface } from 'node:readline';const r=createInterface({input:process.stdin});r.on('line',l=>{const q=JSON.parse(l);setTimeout(()=>console.log(JSON.stringify({requestId:q.requestId,ok:true,tracks:[],pid:process.pid})),150);});");
        }
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public void Dispose() => Directory.Delete(ContentRootPath, true);
    }
}
