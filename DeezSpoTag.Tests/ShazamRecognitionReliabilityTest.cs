using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ShazamRecognitionReliabilityTest
{
    [Fact]
    public void RuntimeTimeout_PreservesSuccessAndSchedulesPromptRecheck()
    {
        using var env = new EnvironmentFixture();
        using var service = CreateService(env);
        ApplyProbe(service, true, false, null);
        ApplyProbe(service, false, true, "Import timed out.");
        Assert.True(service.IsAvailable);
        Assert.Null(service.AvailabilityError);
        Assert.False(service.IsRuntimeProbeStale(DateTimeOffset.UtcNow));
        Assert.True(service.IsRuntimeProbeStale(DateTimeOffset.UtcNow.AddSeconds(31)));
        ApplyProbe(service, false, false, "ModuleNotFoundError: shazamio");
        Assert.False(service.IsAvailable);
        Assert.Contains("ModuleNotFoundError", service.AvailabilityError);
        ApplyProbe(service, false, true, "Import timed out.");
        Assert.False(service.IsAvailable); // A timeout cannot erase an explicit failure.
        ApplyProbe(service, true, false, null);
        Assert.True(service.IsAvailable);
        Assert.False(service.IsRuntimeProbeStale(DateTimeOffset.UtcNow.AddSeconds(31)));
    }

    [Fact]
    public async Task RecognitionProcesses_AreSerializedAndCancellationReleasesTheSlot()
    {
        using var env = new EnvironmentFixture();
        var script = Path.Combine(env.ContentRootPath, "Tools", "shazam_port", "recognize.py");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, """
            import os,time,json
            lock='recognition-active'
            fd=os.open(lock,os.O_CREAT|os.O_EXCL|os.O_WRONLY)
            try:
                time.sleep(.35)
                print(json.dumps({'ok':True,'matched':False}))
            finally:
                os.close(fd)
                os.unlink(lock)
            """);
        using var service = CreateService(env);
        var first = Run(service, CancellationToken.None);
        var second = Run(service, CancellationToken.None);
        await Task.WhenAll(first, second);
        Assert.Equal("NoMatch", State(await first));
        Assert.Equal("NoMatch", State(await second));
        var holding = Run(service, CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        canceled.CancelAfter(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(service, canceled.Token));
        Assert.Equal("NoMatch", State(await holding));
        Assert.Equal("NoMatch", State(await Run(service, CancellationToken.None)));
    }

    [Fact]
    public async Task SlowStartup_DoesNotConsumeFingerprintOrRequestBudget()
    {
        using var env = new EnvironmentFixture();
        var script = Path.Combine(env.ContentRootPath, "Tools", "shazam_port", "recognize.py");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, """
            import time,json,sys
            time.sleep(16)
            for stage in ['fingerprint','request','complete']:
                print(json.dumps({'shazamStage':stage}),file=sys.stderr,flush=True)
                time.sleep(.02)
            print(json.dumps({'ok':True,'matched':False}))
            """);
        using var service = CreateService(env);
        Assert.Equal("NoMatch", State(await Run(service, CancellationToken.None)));
    }

    [Fact]
    public async Task ProbeProcess_DistinguishesTimeoutFromMissingDependency()
    {
        using var env = new EnvironmentFixture();
        using var service = CreateService(env);
        var method = typeof(ShazamRecognitionService).GetMethod("RunProbeProcessAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        async Task<object> Probe(string arguments)
        {
            var task = (Task)method.Invoke(service, ["python3", arguments, true, CancellationToken.None])!;
            await task;
            return task.GetType().GetProperty("Result")!.GetValue(task)!;
        }
        var timeout = await Probe("-c \"import time; time.sleep(11)\"");
        Assert.False((bool)timeout.GetType().GetProperty("IsAvailable")!.GetValue(timeout)!);
        Assert.True((bool)timeout.GetType().GetProperty("TimedOut")!.GetValue(timeout)!);
        var missing = await Probe("-c \"raise ModuleNotFoundError('shazamio')\"");
        Assert.False((bool)missing.GetType().GetProperty("IsAvailable")!.GetValue(missing)!);
        Assert.False((bool)missing.GetType().GetProperty("TimedOut")!.GetValue(missing)!);
    }

    [Theory]
    [InlineData("success", true, "complete")]
    [InlineData("fingerprint", false, "fingerprint")]
    [InlineData("request", false, "request")]
    [InlineData("startup", false, "startup")]
    public async Task PythonRecognition_UsesSeparateStageDeadlinesAndReportsTimings(string scenario, bool ok, string stage)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var script = Path.Combine(root, "DeezSpoTag.Web", "Tools", "shazam_port", "recognize.py");
        var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("""
            import asyncio,importlib.util,sys,types
            scenario=sys.argv[2]
            retry=types.ModuleType('aiohttp_retry')
            retry.ExponentialRetry=lambda **kw:kw
            sys.modules['aiohttp_retry']=retry
            fake=types.ModuleType('shazamio')
            fake.HTTPClient=lambda **kw:kw
            fake.SearchParams=lambda **kw:kw
            class Core:
                async def recognize_path(self,value,options):
                    assert value=='audio.flac' and options['segment_duration_seconds']==12
                    if scenario=='fingerprint': await asyncio.sleep(1)
                    return 'verified-signature'
            class Shazam:
                def __init__(self,**kw):
                    assert kw['language']=='en-US' and kw['endpoint_country']=='US'
                    self.core_recognizer=Core()
                async def send_recognize_request_v2(self,sig):
                    assert sig=='verified-signature'
                    if scenario=='request': await asyncio.sleep(1)
                    return {'matches':[{}],'track':{'key':'123','title':'Song','subtitle':'Artist'}}
            fake.Shazam=Shazam
            sys.modules['shazamio']=None if scenario=='startup' else fake
            spec=importlib.util.spec_from_file_location('recognize',sys.argv[1])
            module=importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            sys.argv=['recognize.py','audio.flac','--signature-seconds','12','--fingerprint-timeout','.05','--timeout','.05']
            sys.exit(module.main())
            """);
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(scenario);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var diagnostics = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await output;
        var stderr = await diagnostics;
        Assert.False(string.IsNullOrWhiteSpace(stdout), stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal(ok, json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(stage, json.RootElement.GetProperty("stage").GetString());
        Assert.True(json.RootElement.GetProperty("timings").GetProperty("startup").GetDouble() >= 0);
        if (scenario != "startup") Assert.Contains("\"stage\": \"fingerprint\"", stderr);
        if (scenario is "success" or "request") Assert.Contains("\"stage\": \"request\"", stderr);
        if (ok) Assert.Equal("123", json.RootElement.GetProperty("summary").GetProperty("trackId").GetString());
    }

    [Fact]
    public async Task Cancellation_TerminatesActiveRecognitionAndAllowsNextFile()
    {
        using var env = new EnvironmentFixture();
        var script = Path.Combine(env.ContentRootPath, "Tools", "shazam_port", "recognize.py");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "import time; time.sleep(30)");
        using var service = CreateService(env);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(service, cancellation.Token));
        var active = typeof(ShazamRecognitionService).GetField("_activeRecognizerProcesses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        Assert.Equal(0, active.GetType().GetProperty("Count")!.GetValue(active));
        File.WriteAllText(script, "print('{\"ok\":true,\"matched\":false}')");
        Assert.Equal("NoMatch", State(await Run(service, CancellationToken.None)));
    }

    private static ShazamRecognitionService CreateService(EnvironmentFixture env) => new(env, null!, NullLogger<ShazamRecognitionService>.Instance);
    private static void ApplyProbe(ShazamRecognitionService service, bool available, bool timedOut, string? error)
    {
        var method = typeof(ShazamRecognitionService).GetMethod("RecordRuntimeProbeResult", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(service, [available, timedOut, error, DateTimeOffset.UtcNow]);
    }
    private static async Task<object> Run(ShazamRecognitionService service, CancellationToken token)
    {
        var method = typeof(ShazamRecognitionService).GetMethod("RunPortedRecognizerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(service, ["audio.flac", null, token])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
    private static string State(object result) => result.GetType().GetProperty("State")!.GetValue(result)!.ToString()!;
    private sealed class EnvironmentFixture : IWebHostEnvironment, IDisposable
    {
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "shazam-reliability-" + Guid.NewGuid().ToString("N"));
        public string WebRootPath { get; set; } = "";
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Development";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public void Dispose() { if (Directory.Exists(ContentRootPath)) Directory.Delete(ContentRootPath, true); }
    }
}
