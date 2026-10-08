using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Genre;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class StyleConstructionPreviewTest
{
    [Fact]
    public async Task PreviewReadsCurrentCanonicalFactsAndLocksWithoutWritingFilesOrResolutionRows()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Store.SaveScopedLockAsync(new("artist", 1, "asakaa"));
        await fixture.Store.SaveLockAsync(new(42, "kenyan-drill"));
        await fixture.Store.SaveTrackResultAsync(new(42, PersonalGenreResolver.Resolve([new("Rock", PersonalGenreTaxonKind.Genre)]), DateTimeOffset.UnixEpoch));
        var bytes = await File.ReadAllBytesAsync(fixture.AudioPath);
        var database = await fixture.DatabaseStateAsync();
        var controller = new PersonalGenreApiController(fixture.Service);
        for (var i = 0; i < 2; i++)
        {
            var response = Assert.IsType<OkObjectResult>(await controller.GetStyleConstructionPreview(42, CancellationToken.None));
            var result = Assert.IsType<StyleConstructionPreview>(response.Value);
            Assert.Equal("Hip-Hop", Assert.Single(result.Snapshot.SemanticFacts).CanonicalValue);
            Assert.Equal("kenyan-drill", Assert.Single(result.Snapshot.UserAuthority.StyleLocks).TaxonId);
            Assert.Equal(3, result.Decisions.Count);
            Assert.All(result.Decisions, d =>
            {
                Assert.Equal(StyleConstructionOutcome.RecognitionOnly, d.Outcome);
                Assert.Empty(d.MissingPredicates);
                Assert.Empty(d.SatisfiedPredicates);
                Assert.Empty(d.EvidenceUsed);
            });
        }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.AudioPath));
        Assert.Equal(database, await fixture.DatabaseStateAsync());
        Assert.Equal("Rock", (await fixture.Store.GetTrackResultAsync(42))!.Resolution.PrimaryGenre);
        Assert.Single(await fixture.Store.GetTrackHistoryAsync(42));
    }

    [Fact]
    public async Task MissingFileAndDisabledGenreIntelligenceReturnUnavailable()
    {
        using var fixture = await Fixture.CreateAsync();
        var controller = new PersonalGenreApiController(fixture.Service);
        Assert.IsType<NotFoundResult>(await controller.GetStyleConstructionPreview(999, CancellationToken.None));
        await fixture.Store.SaveSettingsAsync(new(Enabled: false));
        Assert.Null(await fixture.Service.GetStyleConstructionPreviewAsync(42));
        Assert.IsType<NotFoundResult>(await controller.GetStyleConstructionPreview(42, CancellationToken.None));
        await fixture.Store.SaveSettingsAsync(new());
        File.Delete(fixture.AudioPath);
        Assert.Null(await fixture.Service.GetStyleConstructionPreviewAsync(42));
    }

    [Fact]
    public async Task UnreadableFileDoesNotPersistAPartialPreview()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.AudioPath, "not an audio file");
        var before = await fixture.DatabaseStateAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.GetStyleConstructionPreviewAsync(42));
        Assert.Equal(before, await fixture.DatabaseStateAsync());
    }

    [Fact]
    public void EndpointAndExistingTrackCardExposeTheSeparateReadOnlyPreview()
    {
        var method = typeof(PersonalGenreApiController).GetMethod("GetStyleConstructionPreview")!;
        Assert.Equal("tracks/{trackId:long}/construction-preview", method.GetCustomAttribute<HttpGetAttribute>()!.Template);
        var markup = File.ReadAllText(Path.Combine(Root(), "DeezSpoTag.Web/Views/Library/TrackAnalysis.cshtml"));
        Assert.Contains("id=\"pgTrackConstruction\"", markup);
        Assert.Contains("id=\"pgTrackCleanup\"", markup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewRenderingUsesTextAndHandlesItsOwnRequestFailure(bool failPreview)
    {
        // Run the existing page initializer against a minimal DOM and controlled HTTP responses.
        // A preview error must leave cleanup and existing resolution visible; hostile text stays text.
        const string script = """
        const fs=require('fs'),vm=require('vm'),assert=require('assert');
        const fail=process.argv[2]==='true', elements=new Map(), calls=[];
        function element(){return {children:[],options:[],value:'',dataset:{},listeners:{},
          appendChild(x){this.children.push(x);return x;},addEventListener(k,f){this.listeners[k]=f;},
          set innerHTML(v){assert.equal(v,'');this.children=[];},get innerHTML(){return '';},
          textContent:''};}
        function get(id){if(!elements.has(id))elements.set(id,element());return elements.get(id);}
        let loaded;
        global.document={querySelector:()=>({dataset:{trackId:'42'}}),getElementById:get,
          createElement:element,addEventListener:(event,cb)=>{loaded=cb;}};
        global.loadTrackAnalysisPage=async()=>{};
        global.fetch=async(url,options)=>{
          calls.push({url,method:options.method||'GET'});
          let data=[];
          if(url.endsWith('/taxonomy'))data={taxa:[]};
          else if(url.endsWith('/scope'))data=null;
          else if(url.endsWith('/tracks/42'))data={resolution:{primaryGenre:'Hip-Hop',classifications:[],resolverVersion:'existing'}};
          else if(url.endsWith('/cleanup-preview'))data={moved:[],canonicalized:[],removed:[],preservedUnknown:[]};
          else if(url.endsWith('/construction-preview'))data={decisions:[{targetStyleName:'<img src=x onerror=bad()>',ruleId:'ASAKAA-001',ruleVersion:'1',outcome:'RecognitionOnly',explanation:'No sufficient condition',satisfiedPredicates:[],missingPredicates:[],evidenceUsed:[],conflictingFacts:[],rejectedAlternatives:[],research:{researchGaps:['Scene evidence unavailable']}}]};
          const bad=fail&&url.endsWith('/construction-preview');
          return {ok:!bad,status:bad?500:200,text:async()=>JSON.stringify(bad?{error:'Preview failed'}:data)};
        };
        vm.runInThisContext(fs.readFileSync(process.argv[1],'utf8'));
        function texts(node){return [node.textContent,...node.children.flatMap(texts)].join('\n');}
        (async()=>{
          loaded();
          for(let i=0;i<100&&!calls.some(c=>c.url.endsWith('/construction-preview'));i++)await new Promise(r=>setTimeout(r,10));
          await new Promise(r=>setTimeout(r,20));
          assert(calls.some(c=>c.url.endsWith('/construction-preview')));
          assert(calls.findIndex(c=>c.url.endsWith('/cleanup-preview'))<calls.findIndex(c=>c.url.endsWith('/construction-preview')));
          assert(calls.every(c=>c.method==='GET'));
          assert.match(get('pgTrackStatus').textContent,/Hip-Hop/);
          const text=texts(get('pgTrackConstruction'));
          if(fail)assert.match(text,/Preview failed/);
          else {assert.match(text,/<img src=x onerror=bad\(\)>/);assert.match(text,/Recognition only/);assert.match(text,/Scene evidence unavailable/);}
        })().catch(error=>{console.error(error);process.exitCode=1;});
        """;
        var start = new ProcessStartInfo("node") { RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add(script);
        start.ArgumentList.Add(Path.Combine(Root(), "DeezSpoTag.Web/wwwroot/js/library-track-analysis-page.js"));
        start.ArgumentList.Add(failPreview ? "true" : "false");
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeezSpoTag.Web/DeezSpoTag.Web.csproj"))) directory = directory.Parent;
        return directory!.FullName;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        private readonly string _connectionString;
        public string AudioPath { get; }
        public PersonalGenreStore Store { get; }
        public PersonalGenreService Service { get; }

        private Fixture(string directory)
        {
            // Production constructors prefer this environment variable; never let a fixture touch it.
            Assert.Null(Environment.GetEnvironmentVariable("LIBRARY_DB"));
            _directory = directory;
            AudioPath = Path.Combine(directory, "track.flac");
            _connectionString = "Data Source=" + Path.Combine(directory, "library.db") + ";Pooling=False";
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Library"] = _connectionString }).Build();
            Store = new(configuration);
            Service = new(Store, new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance), NullLogger<PersonalGenreService>.Instance);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "sc1-preview-" + Guid.NewGuid());
            Directory.CreateDirectory(directory);
            var fixture = new Fixture(directory);
            try
            {
                var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
                foreach (var argument in new[] { "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "0.1", fixture.AudioPath }) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, error);
                using (var file = TagLib.File.Create(fixture.AudioPath)) { file.Tag.Genres = ["Hip Hop", "Unqualified Vibe value"]; file.Save(); }
                await using var connection = new SqliteConnection(fixture._connectionString);
                await connection.OpenAsync();
                using var schema = typeof(LibraryRepository).Assembly.GetManifestResourceStream("DeezSpoTag.Services.Library.Schema.library.sql")!;
                using var reader = new StreamReader(schema);
                await using var command = connection.CreateCommand();
                command.CommandText = await reader.ReadToEndAsync();
                await command.ExecuteNonQueryAsync();
                command.CommandText = """
                INSERT INTO artist(id,name) VALUES(1,'Album Artist');
                INSERT INTO album(id,artist_id,title) VALUES(1,1,'Album');
                INSERT INTO track(id,album_id,title) VALUES(42,1,'Track');
                INSERT INTO folder(id,root_path,display_name) VALUES(1,@directory,'Fixture');
                INSERT INTO audio_file(id,path,folder_id) VALUES(1,@path,1);
                INSERT INTO track_local(track_id,audio_file_id) VALUES(42,1);
                """;
                command.Parameters.AddWithValue("directory", directory); command.Parameters.AddWithValue("path", fixture.AudioPath);
                await command.ExecuteNonQueryAsync();
                await fixture.Store.SaveSettingsAsync(new());
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }

        public async Task<string> DatabaseStateAsync()
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var tables = connection.CreateCommand();
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'personal_genre_%' ORDER BY name";
            var names = new List<string>();
            await using (var reader = await tables.ExecuteReaderAsync()) while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            var result = new Dictionary<string, List<string[]>>();
            foreach (var name in names)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\" ORDER BY rowid";
                var rows = new List<string[]>();
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToString()!).ToArray());
                result.Add(name, rows);
            }
            return JsonSerializer.Serialize(result);
        }

        public void Dispose() => Directory.Delete(_directory, true);
    }
}
