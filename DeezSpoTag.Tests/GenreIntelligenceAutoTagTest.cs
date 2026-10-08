using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using DeezSpoTag.Services.Genre;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class GenreIntelligenceAutoTagTest
{
    [Theory]
    [InlineData(true, "Alternative CCM", "Album Rock")]
    [InlineData(false, "alternative ccm", "album rock")]
    public void ProviderStylePathsRespectCapitalizationAndCustomTag(bool enabled, string expectedStyle, string expectedGenre)
    {
        var directory = Path.Combine(Path.GetTempPath(), "genre-cap-provider-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "test.flac");
        try
        {
            CreateSilentAudio(path);
            SetRawTag(path, "flac", "PERSONAL_STYLE", ["existing ccm"]);
            using var file = TagLib.File.Create(path);
            var config = JsonSerializer.Deserialize<LocalAutoTagRunner.AutoTagRunnerConfig>("""
                {"Tags":["genre","style"],"OverwriteTags":["genre","style"],"MergeGenres":true,
                 "StylesOptions":"customTag","StylesCustomTag":{"Vorbis":"PERSONAL_STYLE"}}
                """)!;
            config.CapitalizeGenres = enabled;
            var track = new AutoTagTrack { Styles = ["alternative ccm"] };
            var customWrites = (System.Collections.IEnumerable)typeof(LocalAutoTagRunner)
                .GetMethod("BuildCustomTagWrites", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [track, config, "test", ".flac", file])!;
            var styleWrite = customWrites.Cast<object>().Single(item =>
                (string)item.GetType().GetProperty("TagKey")!.GetValue(item)! == "style");
            Assert.Equal("PERSONAL_STYLE", styleWrite.GetType().GetProperty("RawTagName")!.GetValue(styleWrite));
            var values = (List<string>)styleWrite.GetType().GetProperty("Values")!.GetValue(styleWrite)!;
            Assert.Equal(new[] { enabled ? "Existing CCM" : "existing ccm", expectedStyle }, values);

            var execution = (LocalAutoTagRunner.TagWriteExecutionContext)Activator.CreateInstance(typeof(LocalAutoTagRunner.TagWriteExecutionContext))!;
            void Set(string name, object value) => execution.GetType().GetProperty(name)!.SetValue(execution, value);
            Set("FilePath", path); Set("SourceTrack", track);
            Set("CoreTrack", new DeezSpoTag.Core.Models.Track { Album = new DeezSpoTag.Core.Models.Album("Test Album") { Genre = ["album rock"] } });
            Set("EffectiveTagSettings", new DeezSpoTag.Core.Models.Settings.TagSettings { Genre = true });
            Set("Config", config); Set("Extension", ".flac"); Set("Separator", "; ");
            Set("EnabledTags", new HashSet<string> { "genre", "style" });
            Set("GenreAliasMap", new Dictionary<string, string>()); Set("GenreBlockList", Array.Empty<string>());
            var writeContext = new LocalAutoTagRunner.TagWriteContext(file, ".flac", config, "; ", "test", false,
                new Dictionary<string, string>(), Array.Empty<string>(), false, new HashSet<SupportedTag>());
            typeof(LocalAutoTagRunner).GetMethod("ApplyGenreAndStyleTagWrites", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [file, writeContext, execution]);
            file.Save();
            Assert.Equal(expectedGenre, Assert.Single(file.Tag.Genres));
            Assert.Equal(string.Join("; ", values), Assert.Single(ReadRawTag(path, "flac", "PERSONAL_STYLE")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(true, "Alternative CCM")]
    [InlineData(false, "alternative ccm")]
    public async Task FinalGenreIntelligenceRespectsCapitalization(bool enabled, string expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "genre-cap-final-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "test.flac");
        try
        {
            CreateSilentAudio(path);
            SetRawTag(path, "flac", "STYLE", ["alternative ccm"]);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(directory, "library.db") }).Build();
            await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            var store = new PersonalGenreStore(configuration);
            using var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLogging()
                .AddSingleton(store).AddSingleton<LibraryRepository>().AddSingleton<PersonalGenreService>().BuildServiceProvider();
            var outcome = await RunAutoTagAsync(services, path, directory, true, false, false, _ => { }, _ => { },
                capitalizeGenres: enabled);
            Assert.True(outcome.Success, outcome.Error);
            Assert.Equal(expected, Assert.Single(ReadRawTag(path, "flac", "STYLE")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(10, true, true)]
    public async Task AutoTagResolutionUsesProfileSettingsInsteadOfSharedSettings(int maxGenres, bool preserve, bool parents)
    {
        var directory = Path.Combine(Path.GetTempPath(), "genre-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(directory, "library.db") }).Build();
            await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            var store = new PersonalGenreStore(configuration);
            var shared = new PersonalGenreSettings(true, maxGenres == 1 ? 10 : 1, !preserve, !parents);
            await store.SaveSettingsAsync(shared);
            await store.UpsertCustomTaxonAsync(new PersonalGenreTaxon("profile-parent-test", "Profile Parent Test",
                PersonalGenreTaxonKind.Style, ParentIds: ["hip-hop"]));
            using var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLogging()
                .AddSingleton(store).AddSingleton<LibraryRepository>().AddSingleton<PersonalGenreService>().BuildServiceProvider();
            var snapshot = new GenreSemanticSnapshot([
                new GenreTagObservation("Rock", PersonalGenreTaxonKind.Genre, 0),
                new GenreTagObservation("Jazz", PersonalGenreTaxonKind.Genre, 1),
                new GenreTagObservation("My Personal Genre", PersonalGenreTaxonKind.Genre, 2),
                new GenreTagObservation("Profile Parent Test", PersonalGenreTaxonKind.Style, 3)
            ], DateTimeOffset.UtcNow);
            var result = await services.GetRequiredService<PersonalGenreService>().ResolveFileAsync(
                null, Path.Combine(directory, "track.flac"), snapshot, null,
                new DeezSpoTag.Core.Models.Settings.AutoTagGenreIntelligenceSettings {
                    Enabled = true, MaxGenres = maxGenres, PreserveUnmappedTags = preserve, IncludeParentGenres = parents
                }, CancellationToken.None);
            Assert.Equal(new PersonalGenreSettings(true, maxGenres, preserve, parents), result.Context.Settings);
            Assert.Equal(preserve, result.Result.Resolution.Preserved.Any(value => value.Value == "My Personal Genre"));
            Assert.True(result.Result.Resolution.Genres.Count <= maxGenres);
            if (maxGenres == 1) Assert.Single(result.Result.Resolution.Genres);
            else Assert.True(result.Result.Resolution.Genres.Count > 1);
            Assert.Equal(parents, result.Result.Resolution.Classifications.Any(value => value.Status == "derived_parent"));
            var stored = await store.GetSettingsAsync();
            Assert.Equal(shared.MaxGenres, stored.MaxGenres);
            Assert.Equal(shared.PreserveUnmappedTags, stored.PreserveUnmappedTags);
            Assert.Equal(shared.IncludeParentGenres, stored.IncludeParentGenres);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void GenreTabUsesTheSameCardAndSectionHierarchyAsOtherAutoTagTabs()
    {
        var root = XDocument.Parse(LoadGenreMarkup()).Root!;

        Assert.Equal("personalGenrePage", root.Attribute("id")?.Value);
        Assert.DoesNotContain(root.Descendants(), node => HasClass(node, "playlist-settings-section"));
        // The tab follows the AutoTag card idiom, so it must not leak the
        // per-track page's "pg-" utility classes onto the card or section elements.
        // `pg-muted` is the one permitted helper class, and only as inline text.
        Assert.DoesNotContain(root.Descendants(), node =>
            (node.Attribute("class")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(value => value.StartsWith("pg-", StringComparison.Ordinal) && value != "pg-muted"));

        var cards = root.Elements("article").ToList();
        Assert.Equal(7, cards.Count);
        Assert.All(cards, card =>
        {
            Assert.True(HasClass(card, "card"));
            Assert.Contains(card.Elements("div"), node => HasClass(node, "card-header"));
            var body = Assert.Single(card.Elements("div"), node => HasClass(node, "card-body"));
            Assert.NotEmpty(body.Descendants().Where(node => HasClass(node, "download-section")));
        });

        var checkboxGroups = root.Descendants().Where(node => HasClass(node, "checkbox-group")).ToList();
        Assert.NotEmpty(checkboxGroups);
        Assert.All(checkboxGroups, checkbox =>
            Assert.Contains(checkbox.AncestorsAndSelf(), node => HasClass(node, "genre-intelligence-checkbox-grid")));

        var actionRows = root.Descendants().Where(node => HasClass(node, "autotag-actions")).ToList();
        Assert.NotEmpty(actionRows);
        Assert.All(actionRows, row => Assert.True(HasClass(row, "genre-intelligence-actions")));

        static bool HasClass(XElement element, string expected) =>
            (element.Attribute("class")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(expected);
    }

    /// <summary>
    /// The provider authority model and its vocabulary must not survive anywhere
    /// in the Genre Intelligence surface. The UI is where a user would otherwise
    /// still be told the stage votes on provider labels.
    /// </summary>
    [Fact]
    public void GenreManagementUiHasNoProviderAuthorityVocabulary()
    {
        var markup = LoadGenreMarkup();
        var script = File.ReadAllText(Path.Combine(ResolveRoot(), "DeezSpoTag.Web/wwwroot/js/personal-genre.js"));

        foreach (var banned in new[]
        {
            "Authority Model", "authority", "Source</th>", "confidence",
            "provider fallback", "single_source", "agreement"
        })
        {
            Assert.DoesNotContain(banned, markup, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("item.source", script, StringComparison.Ordinal);
        Assert.DoesNotContain("preserveProviderFallback", script, StringComparison.Ordinal);
        Assert.Contains("inputField", script, StringComparison.Ordinal);

        // The architecture the UI describes must be the file.
        Assert.Contains("audio files", markup, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"Tag Mappings", markup);
    }

    /// <summary>
    /// The genre-normalization settings moved here from the general Settings page.
    /// There must be exactly one place to edit them, and it must be this one.
    /// </summary>
    [Fact]
    public void GenreNormalizationLivesOnlyInTheGenreIntelligenceTab()
    {
        var genreTab = LoadGenreMarkup();
        var settingsPage = File.ReadAllText(Path.Combine(ResolveRoot(), "DeezSpoTag.Web/Views/Settings/Index.cshtml"));
        var settingsScript = Path.Combine(ResolveRoot(), "DeezSpoTag.Web/wwwroot/js/settings.js");
        var settingsJs = File.Exists(settingsScript) ? File.ReadAllText(settingsScript) : string.Empty;

        // Present in the Genre Intelligence tab.
        Assert.Contains("id=\"pgNormalizeGenreTags\"", genreTab);
        Assert.Contains("id=\"pgAliasRulesBody\"", genreTab);
        Assert.Contains("id=\"pgAliasRuleAdd\"", genreTab);
        Assert.Contains("id=\"pgGenreBlockList\"", genreTab);
        Assert.Contains("id=\"pgSaveNormalization\"", genreTab);
        Assert.Contains("Genre Normalization", genreTab);

        // Absent from the general Settings page, markup and script alike.
        foreach (var gone in new[]
                 {
                     "genre-normalization-settings", "id=\"normalizeGenreTags\"",
                     "genreTagAliasRows", "genreTagBlockList", "addGenreTagAliasRule",
                     "renderGenreTagAliasRules", "collectGenreTagAliasRules",
                     "renderGenreTagBlockList", "collectGenreTagBlockList"
                 })
        {
            Assert.DoesNotContain(gone, settingsPage, StringComparison.Ordinal);
            Assert.DoesNotContain(gone, settingsJs, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GenreManagementLoadsEvidenceEditorsWithAutoTagActions()
    {
        var scriptPath = Path.Combine(ResolveRoot(), "DeezSpoTag.Web/wwwroot/js/personal-genre.js");
        var script = """
            const fs = require('fs'), vm = require('vm'), assert = require('assert');
            const nodes = new Map();
            const get = id => {
                if (!nodes.has(id)) nodes.set(id, {value:'', checked:false, innerHTML:'', textContent:'', listeners:{},
                    dataset:{}, querySelector(){return null;}, insertAdjacentHTML(){},
                    addEventListener(type, handler){this.listeners[type]=handler;}, classList:{toggle(){}, contains(){return false;}}});
                return nodes.get(id);
            };
            let start;
            const confirmations = [];
            const fixtures = {
                // The endpoint reports both the three-way origin and the plain editable
                // flag, and the row actions follow editable rather than builtIn.
                taxonomy: {taxa:[{id:'custom',name:'Custom & Genre',kind:'genre',parentIds:[],
                    aliases:[],builtIn:false,origin:'custom',editable:true}]},
                settings: {enabled:true,maxGenres:2,preserveUnmappedTags:false,includeParentGenres:true},
                mappings: [{id:1,matchValue:'Raw & value',inputField:'Style',targetTaxonId:'custom',action:0,priority:100,enabled:true}],
                rules: [{id:2,matchValue:'Rule',targetTaxonId:'custom',priority:1000,enabled:true}]
            };
            const context = {console,Headers,
                DeezSpoTag:{ui:{confirm:async(message, options)=>{confirmations.push({message,options}); return false;}}},
                document:{getElementById:get,querySelectorAll:()=>[],addEventListener:(_,fn)=>{start=fn;}},
                fetch:async url=>({ok:true,status:200,text:async()=>JSON.stringify(fixtures[url.split('/').pop()])})};
            vm.runInNewContext(fs.readFileSync(process.argv[1],'utf8'), context);
            start();
            setImmediate(async()=>{
                assert.match(get('pgStatus').textContent,/Ready/);
                assert.equal(get('pgMaxGenres').value,2);
                assert.equal(get('pgIncludeParents').checked,true);
                // The settings round-trip must use the file-centric name.
                assert.equal(get('pgPreserveUnmappedTags').checked,false);
                for (const id of ['pgTaxonomyBody','pgMappingsBody','pgRulesBody']) {
                    assert.match(get(id).innerHTML,/class="action-btn action-btn-sm/);
                    assert.match(get(id).innerHTML,/genre-intelligence-row-actions/);
                    assert.doesNotMatch(get(id).innerHTML,/pg-btn/);
                }
                assert.match(get('pgTaxonomyBody').innerHTML,/Custom &amp; Genre/);
                assert.match(get('pgTaxonomyBody').innerHTML,/genre-intelligence-badge/);
                assert.doesNotMatch(get('pgTaxonomyBody').innerHTML,/text-bg-secondary/);
                assert.match(get('pgMappingsBody').innerHTML,/Raw &amp; value/);
                assert.match(get('pgMappingsBody').innerHTML,/Style/);

                await get('pgTaxonomyBody').listeners.click({target:{closest(selector){
                    return selector === '[data-pg-delete-taxon]' ? {dataset:{pgDeleteTaxon:'custom'}} : null;
                }}});
                assert.equal(confirmations.length,1);
                assert.equal(confirmations[0].options.title,'Delete custom taxon?');
                assert.equal(confirmations[0].options.okText,'Delete');
            });
            """;
        var start = new ProcessStartInfo("node") { RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add(script); start.ArgumentList.Add(scriptPath);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public void ProfileConfigBuilderRetainsGenreRuntimeSettings()
    {
        var profile = new DeezSpoTag.Core.Models.Settings.TaggingProfile
        {
            AutoTag = new DeezSpoTag.Core.Models.Settings.AutoTagSettings
            {
                Data = new Dictionary<string, JsonElement> { ["genreIntelligence"] = JsonSerializer.SerializeToElement(new { enabled = true, maxGenres = 2, writeContext = true }) }
            }
        };
        using var config = JsonDocument.Parse(new AutoTagConfigBuilder().BuildConfigJson(profile)!);
        var options = config.RootElement.GetProperty("genreIntelligence");
        Assert.True(options.GetProperty("enabled").GetBoolean());
        Assert.Equal(2, options.GetProperty("maxGenres").GetInt32());
        Assert.True(options.GetProperty("writeContext").GetBoolean());
    }

    /// <summary>
    /// The end-to-end proof the phase requires: a real file is tagged, a real
    /// AutoTag run executes the terminal stage, and then the file is opened again
    /// from disk and every semantic field is read back and checked against what
    /// Genre Intelligence decided.
    ///
    /// The fixture deliberately mixes a canonical genre, a term that belongs in
    /// another dimension, an umbrella term that must become Context, and a purely
    /// personal value that must survive in the field it was read from. The
    /// provider stage is simulated by writing the file first, because AutoTag
    /// cannot call real metadata services from a test.
    /// </summary>
    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public async Task TerminalStageRewritesTheFileAndRereadConfirmsTheFinalSemanticState(string extension)
    {
        var directory = Path.Combine(Path.GetTempPath(), "genre-verify-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "track." + extension);
            CreateSilentAudio(path);
            using (var file = TagLib.File.Create(path))
            {
                file.Tag.Title = "Original Title";
                file.Tag.Album = "Original Album";
                file.Tag.Performers = ["Original Artist"];
                file.Tag.Year = 1999;
                file.Tag.Comment = "Original Comment";
                // What the user had before AutoTag: a personal value the taxonomy
                // has never heard of, plus a term that will be replaced.
                // "Jazz" is a term the taxonomy knows, and the provider replaces
                // it; "My Personal Genre" is one it does not, and the provider
                // drops it. One is a correction, the other is a loss to prevent.
                file.Tag.Genres = ["My Personal Genre", "Jazz"];
                file.Save();
            }
                SetRawTag(path, extension, "STYLE", ["Southern Hip-Hop"]);

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(directory, "library.db")
            }).Build();
            await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            var store = new PersonalGenreStore(configuration);
            var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLogging()
                .AddSingleton(store).AddSingleton<LibraryRepository>().AddSingleton<PersonalGenreService>().BuildServiceProvider();
            using (services)
            {
                // The pre-AutoTag snapshot is the user's file as it stands now. It
                // is recorded before the run starts, exactly as a real run records
                // it before its first provider.
                await store.SaveSnapshotAsync(
                    "job",
                    path,
                    GenreSnapshotStage.PreAutoTag,
                    PersonalGenreService.ReadFileSnapshot(path),
                    null);

                // Now the provider pass. A test cannot reach real metadata
                // services, so its effect on the file is written directly: it
                // overwrites GENRE with its own opinion, dropping the personal
                // value, and leaves STYLE alone.
                using (var file = TagLib.File.Create(path))
                {
                    file.Tag.Genres = ["Hip-Hop", "Trap", "Afrosounds"];
                    file.Save();
                }

                var before = await RunTerminalStageAsync(services, path, directory, writeSubstyle: true, writeContext: true);

                // ── The decision record, read from the store ──
                Assert.Equal("Hip-Hop", before.Resolution.PrimaryGenre);
                // Trap is a Style and Afrosounds is Context, so neither is a Genre
                // even though the provider wrote both into GENRE.
                Assert.Equal(new[] { "Hip-Hop" }, before.Resolution.Genres);
                // "Southern Hip-Hop" was, until the researched master was adopted, a
                // value the 156-term taxonomy did not know, so it was preserved back
                // into STYLE untouched. The researched vocabulary contains it as a
                // Style, so it is now classified and written under its canonical
                // researched spelling. That is the improvement this change exists for,
                // so the old expectation is intentionally replaced.
                Assert.Equal("Trap", before.Resolution.Styles[0]);
                Assert.Contains("southern hip hop", before.Resolution.Styles);
                Assert.Contains("Afrosounds", before.Resolution.Contexts);
                // The provider overwrote GENRE and dropped "My Personal Genre", so
                // that value is gone from the post-AutoTag read. The run restores
                // it: uninterpretable, replaced by nothing, preservation on.
                var personal = Assert.Single(before.Resolution.Preserved, item => item.Value == "My Personal Genre");
                Assert.Equal(PersonalGenreTaxonKind.Genre, personal.InputField);
                Assert.Equal(GenreObservationOrigin.PreservedOriginal, personal.Origin);
                // Only the personal value is preserved now. A value the researched
                // vocabulary recognises is classified rather than left in place, so
                // the STYLE value must no longer appear as unmapped.
                Assert.DoesNotContain(before.Resolution.Preserved, item => item.Value == "Southern Hip-Hop");
                Assert.Contains(
                    before.Resolution.Decisions,
                    d => d.RawValue == "Southern Hip-Hop"
                         && d.Outcome == "canonical_match"
                         && d.CanonicalValue == "southern hip hop");

                // Every observation has an inspectable outcome.
                Assert.All(before.Resolution.Decisions, decision => Assert.False(string.IsNullOrWhiteSpace(decision.Outcome)));
                Assert.Contains(before.Resolution.Decisions, d => d.RawValue == "Trap" && d.Outcome == "canonical_match");
                Assert.Contains(before.Resolution.Decisions, d => d.RawValue == "My Personal Genre" && d.Outcome == "preserved_unmapped");
                Assert.Contains(before.Resolution.Decisions, d => d.RawValue == "Afrosounds" && d.Outcome == "context_only");

                // §25: both snapshots are retained and neither overwrote the
                // other. The pre-snapshot is the user's file before the provider
                // pass; the post-snapshot is what the provider left.
                Assert.NotNull(before.PreAutoTagSnapshot);
                Assert.NotNull(before.PostAutoTagSnapshot);
                Assert.Contains(before.PreAutoTagSnapshot!.Observations, o => o.RawValue == "My Personal Genre");
                Assert.Contains(before.PreAutoTagSnapshot!.Observations, o => o.RawValue == "Jazz");
                Assert.DoesNotContain(before.PreAutoTagSnapshot!.Observations, o => o.RawValue == "Hip-Hop");
                Assert.Contains(before.PostAutoTagSnapshot!.Observations, o => o.RawValue == "Hip-Hop");
                Assert.DoesNotContain(before.PostAutoTagSnapshot!.Observations, o => o.RawValue == "My Personal Genre");

                // ── The file, reopened from disk ──
                using (var verify = TagLib.File.Create(path))
                {
                    var genres = ReadGenres(verify, extension);
                    // §17: the personal value returns to GENRE, and §16: the
                    // provider's Genre mess is replaced, not appended to. "Trap"
                    // and "Afrosounds" are gone from GENRE because they were
                    // reclassified to other dimensions.
                    Assert.Equal(new[] { "Hip-Hop", "My Personal Genre" }, genres);
                    // Non-genre metadata is untouched.
                    Assert.Equal("Original Title", verify.Tag.Title);
                    Assert.Equal("Original Album", verify.Tag.Album);
                    Assert.Equal(new[] { "Original Artist" }, verify.Tag.Performers);
                    Assert.Equal(1999u, verify.Tag.Year);
                    Assert.Equal("Original Comment", verify.Tag.Comment);
                }

                // STYLE holds the classified Trap and the classified researched
                // Style, each under its display spelling. "southern hip hop" is the
                // researched master's spelling; what reaches the file is the formatted
                // "Southern Hip Hop", because presentation is applied at the write
                // boundary rather than baked into the vocabulary.
                var styles = ReadRawTag(path, extension, "STYLE");
                Assert.Contains("Trap", styles);
                Assert.Contains("Southern Hip Hop", styles);
                // Afrosounds moved out of GENRE into its own dimension (§12).
                Assert.Contains("Afrosounds", ReadRawTag(path, extension, "CONTEXT"));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// §15: a disabled dimension must not be written, and must not be reported as
    /// missing afterwards either.
    /// </summary>
    [Fact]
    public async Task DisabledDimensionsAreNeitherWrittenNorDemanded()
    {
        var directory = Path.Combine(Path.GetTempPath(), "genre-disabled-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "track.flac");
            CreateSilentAudio(path);
            using (var file = TagLib.File.Create(path))
            {
                file.Tag.Genres = ["Hip-Hop", "Afrosounds", "Zilizopendwa"];
                file.Save();
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(directory, "library.db")
            }).Build();
            await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            var store = new PersonalGenreStore(configuration);
            var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLogging()
                .AddSingleton(store).AddSingleton<LibraryRepository>().AddSingleton<PersonalGenreService>().BuildServiceProvider();
            using (services)
            {
                // Every optional dimension is off.
                var result = await RunTerminalStageAsync(
                    services, path, directory, writeSubstyle: false, writeContext: false, writeScene: false);

                Assert.Contains("Afrosounds", result.Resolution.Contexts);
                Assert.Contains("Zilizopendwa", result.Resolution.Scenes);
                // The write succeeded even though those dimensions were not written,
                // which is the point: a disabled dimension is not a failed write.
                Assert.Equal("Hip-Hop", result.Resolution.PrimaryGenre);
                Assert.DoesNotContain("Afrosounds", ReadGenres(TagLib.File.Create(path), "flac"));
                Assert.Empty(ReadRawTag(path, "flac", "CONTEXT"));
                Assert.Empty(ReadRawTag(path, "flac", "SCENE"));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// §18: a value present in both snapshots is written once, and the provider's
    /// superseded value is gone.
    /// </summary>
    [Fact]
    public async Task SupersededProviderValueIsReplacedRatherThanKept()
    {
        var directory = Path.Combine(Path.GetTempPath(), "genre-dedupe-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "track.flac");
            CreateSilentAudio(path);
            // Rap is a provider-era value the platform replaced; Hip-Hop is what
            // it replaced it with. Both existed in the pre-AutoTag snapshot.
            using (var file = TagLib.File.Create(path))
            {
                file.Tag.Genres = ["Rap", "hip hop"];
                file.Save();
            }
            using (var file = TagLib.File.Create(path))
            {
                file.Tag.Genres = ["Hip-Hop"];
                file.Save();
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(directory, "library.db")
            }).Build();
            await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            var store = new PersonalGenreStore(configuration);
            var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLogging()
                .AddSingleton(store).AddSingleton<LibraryRepository>().AddSingleton<PersonalGenreService>().BuildServiceProvider();
            using (services)
            {
                var result = await RunTerminalStageAsync(services, path, directory, writeSubstyle: false, writeContext: false);

                // "Rap" is a built-in term the platform deliberately replaced, so
                // it is not resurrected. "hip hop" is an alias of Hip-Hop, and it
                // was also replaced, so it is not written a second time.
                var genres = ReadGenres(TagLib.File.Create(path), "flac");
                Assert.Equal(new[] { "Hip-Hop" }, genres);
                Assert.DoesNotContain("Rap", genres);
                Assert.DoesNotContain("hip hop", genres);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// §3: with Genre Intelligence disabled the run must behave exactly as it does
    /// without it — no snapshot, no resolution, no rewrite, so the provider's own
    /// Genre value is left alone.
    /// </summary>
    [Fact]
    public async Task DisabledGenreIntelligenceLeavesTheFileUntouched()
    {
        var directory = Path.Combine(Path.GetTempPath(), "genre-optin-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "track.flac");
            CreateSilentAudio(path);
            using (var file = TagLib.File.Create(path))
            {
                file.Tag.Genres = ["Afrosounds", "Pop"];
                file.Save();
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(directory, "library.db")
            }).Build();
            await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            var store = new PersonalGenreStore(configuration);
            var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLogging()
                .AddSingleton(store).AddSingleton<LibraryRepository>().AddSingleton<PersonalGenreService>().BuildServiceProvider();
            using (services)
            {
                var statuses = new List<TaggingStatusWrap>();
                var logs = new List<string>();
                var result = await RunAutoTagAsync(
                    services, path, directory, enabled: false, writeSubstyle: false, writeContext: false,
                    statuses.Add, logs.Add);
                Assert.True(result.Success, result.Error);

                Assert.DoesNotContain(statuses, status => status.Platform == "genre-intelligence");
                Assert.Null(await store.GetSnapshotAsync("job", path, GenreSnapshotStage.PreAutoTag));
                using (var verify = TagLib.File.Create(path))
                {
                    Assert.Equal(new[] { "Afrosounds", "Pop" }, ReadGenres(verify, "flac"));
                }
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>Runs the real AutoTag pipeline and returns the stored resolution.</summary>
    private static async Task<PersonalGenreTrackResult> RunTerminalStageAsync(
        IServiceProvider services,
        string path,
        string directory,
        bool writeSubstyle,
        bool writeContext,
        bool writeScene = true)
    {
        PersonalGenreTrackResult? stored = null;
        var logs = new List<string>();
        var result = await RunAutoTagAsync(
            services, path, directory, enabled: true, writeSubstyle, writeContext,
            _ => { },
            logs.Add,
            trackId => stored = trackId,
            writeScene);
        Assert.True(result.Success, result.Error);
        return stored ?? throw new InvalidOperationException(
            "Genre Intelligence stored no result. Logs: " + string.Join(" | ", logs));
    }

    private static async Task<(bool Success, string? Error)> RunAutoTagAsync(
        IServiceProvider services,
        string path,
        string directory,
        bool enabled,
        bool writeSubstyle,
        bool writeContext,
        Action<TaggingStatusWrap> onStatus,
        Action<string> onLog,
        Action<PersonalGenreTrackResult>? capture = null,
        bool writeScene = true,
        bool capitalizeGenres = true)
    {
        var collaborators = (LocalAutoTagRunner.LocalAutoTagRunnerCollaborators)Activator.CreateInstance(typeof(LocalAutoTagRunner.LocalAutoTagRunnerCollaborators))!;
        void Set(string name, object value) => collaborators.GetType().GetProperty(name)!.SetValue(collaborators, value);
        Set("Logger", NullLogger<LocalAutoTagRunner>.Instance);
        Set("ServiceScopeFactory", services.GetRequiredService<IServiceScopeFactory>());
        Set("ShazamRecognitionService", new ShazamRecognitionService(null!, null!, NullLogger<ShazamRecognitionService>.Instance));
        var runner = new LocalAutoTagRunner(collaborators);

        var configPath = Path.Combine(directory, "config.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            platforms = Array.Empty<string>(),
            tags = new[] { "genre", "style" },
            targetFiles = new[] { path },
            capitalizeGenres,
            enableShazam = false,
            parseFilename = false,
            genreIntelligence = new
            {
                enabled,
                writeSubstyle,
                writeContext,
                writeScene
            }
        }));

        var outcome = await runner.RunAsync(
            "job", directory, configPath, onStatus, onLog,
            (_, _) => Task.CompletedTask, new AutoTagResumeCursor(0, 0, 1, 1), CancellationToken.None);

        if (capture is not null)
        {
            var store = services.GetRequiredService<PersonalGenreStore>();
            var record = await store.GetLatestAutoTagResultAsync(0, path);
            if (record is not null)
            {
                capture(record);
            }
        }

        return (outcome.Success, outcome.Error);
    }

    /// <summary>
    /// Writes a raw semantic tag using the application's own writer, so the test
    /// fixture is encoded exactly as a real run would encode it.
    /// </summary>
    private static void SetRawTag(string path, string extension, string name, string[] values)
    {
        _ = extension;
        var setter = typeof(LocalAutoTagRunner)
            .GetMethod("SetSemanticRawTagForTest", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(setter);
        setter!.Invoke(null, [path, name, values]);
    }

    /// <summary>
    /// Reads a raw semantic tag the way the application reads it.
    ///
    /// An MP3 stores a multi-value frame as one separator-joined string, so the
    /// raw read is split exactly as the production reader splits it. Asserting
    /// against the unsplit value would test the container's storage quirk rather
    /// than the tag's content.
    /// </summary>
    private static IReadOnlyList<string> ReadRawTag(string path, string extension, string name)
    {
        using var file = TagLib.File.Create(path);
        var read = typeof(LocalAutoTagRunner).GetMethod("ReadRawTagValues", BindingFlags.NonPublic | BindingFlags.Static)!;
        var raw = (List<string>)read.Invoke(null, [file, "." + extension, name])!;
        return raw
            .SelectMany(value => GenreSemanticTagIo.SplitComposite(value, "." + extension))
            .ToList();
    }

    /// <summary>
    /// §12: the input field is not the output classification. A value the user
    /// placed in GENRE that the taxonomy calls a Style must be written to STYLE,
    /// and the reverse must also work.
    /// </summary>
    [Theory]
    [InlineData("Trap", PersonalGenreTaxonKind.Genre, "STYLE")]
    [InlineData("Bongo Flava", PersonalGenreTaxonKind.Style, "GENRE")]
    public async Task ValueMovesBetweenFieldsWhenTheTaxonomySaysSo(string value, PersonalGenreTaxonKind inputField, string expectedTarget)
    {
        var directory = Path.Combine(Path.GetTempPath(), "genre-move-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "track.flac");
            CreateSilentAudio(path);
            if (inputField == PersonalGenreTaxonKind.Genre)
            {
                using var file = TagLib.File.Create(path);
                file.Tag.Genres = [value];
                file.Save();
            }
            else
            {
                SetRawTag(path, "flac", "STYLE", [value]);
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(directory, "library.db")
            }).Build();
            await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            var store = new PersonalGenreStore(configuration);
            var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLogging()
                .AddSingleton(store).AddSingleton<LibraryRepository>().AddSingleton<PersonalGenreService>().BuildServiceProvider();
            using (services)
            {
                await store.SaveSnapshotAsync(
                    "job", path, GenreSnapshotStage.PreAutoTag, PersonalGenreService.ReadFileSnapshot(path), null);
                var result = await RunTerminalStageAsync(
                    services, path, directory, writeSubstyle: true, writeContext: true);

                // The decision names the field it was read from, so the move is
                // visible rather than implied.
                var decision = Assert.Single(result.Resolution.Decisions, d => d.RawValue == value);
                Assert.Equal(inputField, decision.InputField);

                var written = expectedTarget == "GENRE"
                    ? ReadGenres(TagLib.File.Create(path), "flac")
                    : ReadRawTag(path, "flac", "STYLE");
                Assert.Contains(value, written);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// §26: a personal alias and an explicit rule must each be recognisable in the
    /// decision trail, and the display spelling must be the term's own.
    /// </summary>
    [Fact]
    public async Task AliasAndRuleAreVisibleInTheDecisionTrailAndUseTheDisplaySpelling()
    {
        var directory = Path.Combine(Path.GetTempPath(), "genre-alias-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "track.flac");
            CreateSilentAudio(path);
            using (var file = TagLib.File.Create(path))
            {
                file.Tag.Genres = ["R and B", "Sunday Chill"];
                file.Save();
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(directory, "library.db")
            }).Build();
            await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            var store = new PersonalGenreStore(configuration);
            // "Sunday Chill" is an alias of a term the user defines.
            await store.UpsertCustomTaxonAsync(
                new PersonalGenreTaxon("sunday-chill", "Sunday Chill", PersonalGenreTaxonKind.Style));
            await store.UpsertRuleAsync(new PersonalGenreRule(0, "Sunday Chill", "worship", Priority: 10));

            var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLogging()
                .AddSingleton(store).AddSingleton<LibraryRepository>().AddSingleton<PersonalGenreService>().BuildServiceProvider();
            using (services)
            {
                await store.SaveSnapshotAsync(
                    "job", path, GenreSnapshotStage.PreAutoTag, PersonalGenreService.ReadFileSnapshot(path), null);
                var result = await RunTerminalStageAsync(
                    services, path, directory, writeSubstyle: true, writeContext: true);

                // "R and B" is a built-in alias of the R&B term. It is reported as
                // an alias match, distinct from a direct name match, and the file
                // receives the term's own display spelling.
                var alias = Assert.Single(result.Resolution.Decisions, d => d.RawValue == "R and B");
                Assert.Equal("alias_match", alias.Outcome);
                Assert.Equal("rnb", alias.TaxonId);
                Assert.Contains("alias", alias.Reason, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("R&B", result.Resolution.Genres);
                Assert.DoesNotContain("R and B", result.Resolution.Genres);

                // The rule outranks the custom term and is named.
                var ruled = Assert.Single(result.Resolution.Decisions, d => d.RawValue == "Sunday Chill");
                Assert.Equal("rule_applied", ruled.Outcome);
                Assert.Equal("worship", ruled.TaxonId);
                Assert.Contains("1", result.Resolution.AppliedRuleIds);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>§29: nothing to classify is a valid outcome, and nothing is invented.</summary>
    [Fact]
    public async Task NoCanonicalGenreIsNotAnErrorAndInventsNothing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "genre-empty-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "track.flac");
            CreateSilentAudio(path);
            using (var file = TagLib.File.Create(path)) { file.Tag.Genres = ["Afrosounds"]; file.Save(); }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(directory, "library.db")
            }).Build();
            await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            var store = new PersonalGenreStore(configuration);
            var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLogging()
                .AddSingleton(store).AddSingleton<LibraryRepository>().AddSingleton<PersonalGenreService>().BuildServiceProvider();
            using (services)
            {
                await store.SaveSnapshotAsync(
                    "job", path, GenreSnapshotStage.PreAutoTag, PersonalGenreService.ReadFileSnapshot(path), null);
                var result = await RunTerminalStageAsync(
                    services, path, directory, writeSubstyle: true, writeContext: true);

                Assert.Null(result.Resolution.PrimaryGenre);
                Assert.Empty(result.Resolution.Genres);
                Assert.Contains("Afrosounds", result.Resolution.Contexts);
                // Nothing is invented for GENRE. The provider's own value is also
                // left alone, because Genre Intelligence produces no Genre to
                // replace it with — replacing it with nothing would be a loss.
                using var file = TagLib.File.Create(path);
                Assert.Equal(new[] { "Afrosounds" }, ReadGenres(file, "flac"));
                // The Context dimension does hold it, under the enabled flag.
                Assert.Contains("Afrosounds", ReadRawTag(path, "flac", "CONTEXT"));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// §31: a run that pauses before the terminal stage and resumes must produce
    /// the same result as one that never paused, because the pre-AutoTag snapshot
    /// is durable.
    /// </summary>
    [Fact]
    public async Task ResumeAfterAPauseProducesTheSameResult()
    {
        var directory = Path.Combine(Path.GetTempPath(), "genre-resume-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var build = async (string suffix) =>
            {
                var path = Path.Combine(directory, "track" + suffix + ".flac");
                CreateSilentAudio(path);
                using (var file = TagLib.File.Create(path))
                {
                    file.Tag.Genres = ["My Personal Genre", "Jazz"];
                    file.Save();
                }
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(directory, suffix + ".db")
                }).Build();
                await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
                var store = new PersonalGenreStore(configuration);
                var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLogging()
                    .AddSingleton(store).AddSingleton<LibraryRepository>().AddSingleton<PersonalGenreService>().BuildServiceProvider();
                await store.SaveSnapshotAsync(
                    "job", path, GenreSnapshotStage.PreAutoTag, PersonalGenreService.ReadFileSnapshot(path), null);
                // The provider pass, then the terminal stage.
                using (var file = TagLib.File.Create(path))
                {
                    file.Tag.Genres = ["Hip-Hop"];
                    file.Save();
                }
                var result = await RunTerminalStageAsync(
                    services, path, directory, writeSubstyle: true, writeContext: true);
                return (Genres: ReadGenres(TagLib.File.Create(path), "flac"), Result: result, Store: store, Services: services);
            };

            var uninterrupted = await build("-a");
            // The resumed run starts from the same durable snapshot; the only
            // difference is that the job id is reused, as a real resume does.
            var resumed = await build("-b");

            Assert.Equal(uninterrupted.Genres, resumed.Genres);
            Assert.Equal(uninterrupted.Result.Resolution.PrimaryGenre, resumed.Result.Resolution.PrimaryGenre);
            Assert.Equal(
                uninterrupted.Result.Resolution.Preserved.Select(item => item.Value),
                resumed.Result.Resolution.Preserved.Select(item => item.Value));
            // The snapshot survived: it is still readable after the run.
            Assert.NotNull(await resumed.Store.GetSnapshotAsync(
                "job",
                Path.Combine(directory, "track-b.flac"),
                GenreSnapshotStage.PreAutoTag));

            uninterrupted.Services.Dispose();
            resumed.Services.Dispose();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// The terminal stage preprocesses through the shared Genre Normalization
    /// authority, so a user's alias preference is applied to the file's values
    /// before classification sees them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test previously asserted the opposite and was named
    /// <c>TerminalWriteIsNotAlteredByTheNormalizationSettings</c>. Its old
    /// expectation was that the terminal write is constructed with an empty alias
    /// map and block list so the user's preferences could never touch its output,
    /// and that a stored alias of Hip-Hop to something else entirely would leave
    /// the file holding Hip-Hop.
    /// </para>
    /// <para>
    /// That behaviour was declared incorrect: Genre Intelligence must preprocess
    /// through the same authority AutoTag, QuickTag and the downloads use, and a
    /// second rule set could disagree with the user's own configuration. The new
    /// expectation is that the preference is honoured, the resulting value is
    /// classified on its merits, and a value the taxonomy does not know is still
    /// preserved rather than deleted.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TerminalWritePreprocessesThroughTheSharedNormalizationAuthority()
    {
        var directory = Path.Combine(Path.GetTempPath(), "genre-nofight-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "track.flac");
            CreateSilentAudio(path);
            using (var file = TagLib.File.Create(path)) { file.Tag.Genres = ["Hip-Hop"]; file.Save(); }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(directory, "library.db")
            }).Build();
            await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            var store = new PersonalGenreStore(configuration);
            // The user's saved spelling preference, plus a block list that does not
            // touch this value, so the alias is the only thing that can change it.
            await store.SaveSettingsAsync(new PersonalGenreSettings(
                Enabled: true,
                NormalizeGenreTags: true,
                GenreTagAliasRules: [new PersonalGenreAliasRule("Hip-Hop", "Something Else Entirely")],
                GenreTagBlockList: ["Afrosounds"]));

            var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLogging()
                .AddSingleton(store).AddSingleton<LibraryRepository>()
                .AddSingleton<DeezSpoTag.Services.Genre.GenreNormalizationProvider>()
                .AddSingleton<PersonalGenreService>().BuildServiceProvider();
            using (services)
            {
                await store.SaveSnapshotAsync(
                    "job", path, GenreSnapshotStage.PreAutoTag, PersonalGenreService.ReadFileSnapshot(path), null);
                var result = await RunTerminalStageAsync(
                    services, path, directory, writeSubstyle: false, writeContext: false);

                // The saved preference was applied to the value read from the file.
                // The taxonomy does not know the rewritten spelling, so it is
                // preserved instead of being classified or dropped.
                using var verify = TagLib.File.Create(path);
                var genres = ReadGenres(verify, "flac");
                Assert.Contains("Something Else Entirely", genres);
                Assert.DoesNotContain("Hip-Hop", genres);

                // The decision trail records both the file's spelling and the
                // normalized one, so the change is inspectable rather than silent.
                var decision = Assert.Single(
                    result.Resolution.Decisions.Where(item => item.Outcome == "preserved_unmapped"));
                Assert.Equal("Hip-Hop", decision.RawValue);
                Assert.Equal("Something Else Entirely", decision.NormalizedValue);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// The terminal stage removes a blocked value and preserves a personal one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test previously asserted the opposite. Its old expectation was that
    /// "Worldwide" survives the terminal stage because the block list is applied
    /// only where a provider writes genres, and that both file values are therefore
    /// preserved unrecognised. Its comment said the terminal classifier has no
    /// provider value to strip.
    /// </para>
    /// <para>
    /// That was declared incorrect: a value the user's block list forbids must not
    /// survive merely because the stage that would have removed it is the terminal
    /// one. The new expectation is that the blocked value is removed and reported
    /// as such, while a value the user wrote themselves is still preserved, because
    /// unknown is not the same as forbidden.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ABlockedValueIsRemovedAndAPersonalValueIsPreserved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "genre-blocked-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "track.flac");
            CreateSilentAudio(path);
            using (var file = TagLib.File.Create(path)) { file.Tag.Genres = ["Worldwide", "My Personal Genre"]; file.Save(); }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(directory, "library.db")
            }).Build();
            await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            var store = new PersonalGenreStore(configuration);
            await store.SaveSettingsAsync(new PersonalGenreSettings(
                Enabled: true, NormalizeGenreTags: true, GenreTagBlockList: ["other", "others", "Worldwide"]));

            var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLogging()
                .AddSingleton(store).AddSingleton<LibraryRepository>()
                .AddSingleton<DeezSpoTag.Services.Genre.GenreNormalizationProvider>()
                .AddSingleton<PersonalGenreService>().BuildServiceProvider();
            using (services)
            {
                await store.SaveSnapshotAsync(
                    "job", path, GenreSnapshotStage.PreAutoTag, PersonalGenreService.ReadFileSnapshot(path), null);
                var result = await RunTerminalStageAsync(
                    services, path, directory, writeSubstyle: false, writeContext: false);

                // "My Personal Genre" is unknown to the taxonomy, so it is preserved
                // rather than classified or deleted. Unknown is not forbidden: the
                // user's own tagging has to survive the stage that cleans up.
                var preserved = result.Resolution.Preserved.ToList();
                var preservedValue = Assert.Single(preserved);
                Assert.Equal("My Personal Genre", preservedValue.Value);
                using var verify = TagLib.File.Create(path);
                var genres = ReadGenres(verify, "flac");
                Assert.Contains("My Personal Genre", genres);

                // "Worldwide" is on the saved block list, so the shared authority
                // removes it before classification. It must not reach the file, and
                // it must not be resurrected by carry-forward either, which is why
                // the pre-AutoTag snapshot holding the same value does not bring it
                // back.
                Assert.DoesNotContain("Worldwide", genres);
                var blocked = Assert.Single(
                    result.Resolution.Decisions.Where(item => item.Outcome == "blocked"));
                Assert.Equal("Worldwide", blocked.RawValue);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RealTerminalPassReadsTheFileItJustWroteAndPreservesPersonalTags(bool batched, bool breakSnapshot)
    {
        var directory = Path.Combine(Path.GetTempPath(), "genre-run-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "track.flac");
            CreateSilentAudio(path);
            // A personal tag the taxonomy has never heard of, plus one it knows.
            // The unknown one must survive the whole run.
            using (var file = TagLib.File.Create(path))
            {
                file.Tag.Title = "Original Title";
                file.Tag.Performers = ["Artist"];
                file.Tag.Album = "Album";
                file.Tag.Genres = ["My Personal Genre", "Pop"];
                file.Save();
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(directory, "library.db")
            }).Build();
            await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            var store = new PersonalGenreStore(configuration);
            var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLogging()
                .AddSingleton(store).AddSingleton<LibraryRepository>().AddSingleton<PersonalGenreService>().BuildServiceProvider();
            using (services)
            {
                var collaborators = (LocalAutoTagRunner.LocalAutoTagRunnerCollaborators)Activator.CreateInstance(typeof(LocalAutoTagRunner.LocalAutoTagRunnerCollaborators))!;
                void Set(string name, object value) => collaborators.GetType().GetProperty(name)!.SetValue(collaborators, value);
                Set("Logger", NullLogger<LocalAutoTagRunner>.Instance);
                Set("ServiceScopeFactory", services.GetRequiredService<IServiceScopeFactory>());
                Set("ShazamRecognitionService", new ShazamRecognitionService(null!, null!, NullLogger<ShazamRecognitionService>.Instance));
                var runner = new LocalAutoTagRunner(collaborators);
                var configPath = Path.Combine(directory, "config.json");
                await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
                {
                    platforms = Array.Empty<string>(),
                    tags = new[] { "genre" },
                    targetFiles = new[] { path },
                    enableShazam = false,
                    parseFilename = false,
                    libraryWideEnhancementBatchSize = batched ? 1 : 0,
                    genreIntelligence = new { enabled = true }
                }));

                var prepare = typeof(LocalAutoTagRunner).GetMethod("PrepareAutoTagRunPlanAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var prepareTask = (Task)prepare.Invoke(runner, ["job", directory, configPath, CancellationToken.None])!;
                await prepareTask;
                var tuple = prepareTask.GetType().GetProperty("Result")!.GetValue(prepareTask)!;
                var plan = tuple.GetType().GetField("Item1")!.GetValue(tuple)!;
                var contextType = typeof(LocalAutoTagRunner).GetNestedType("AutoTagFileRunContext", BindingFlags.NonPublic)!;

                // A future first-platform file that was never modified may still
                // establish its own snapshot after a resume.
                var futurePath = Path.Combine(directory, "future.flac");
                File.Copy(path, futurePath);
                var future = Activator.CreateInstance(contextType)!;
                contextType.GetProperty("Plan")!.SetValue(future, plan);
                contextType.GetProperty("File")!.SetValue(future, futurePath);
                contextType.GetProperty("PlatformIndex")!.SetValue(future, 0);
                plan.GetType().GetProperty("IsResumedRun")!.SetValue(plan, true);
                var capture = typeof(LocalAutoTagRunner).GetMethod("CapturePreAutoTagSemanticSnapshotAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
                await (Task)capture.Invoke(runner, [future])!;
                var pre = await store.GetSnapshotAsync("job", futurePath, GenreSnapshotStage.PreAutoTag);
                Assert.Contains(pre!.Observations, item => item.RawValue == "My Personal Genre" && item.InputField == PersonalGenreTaxonKind.Genre);
                File.Delete(futurePath);

                if (breakSnapshot)
                {
                    // Simulate a file whose snapshot could never be taken: the
                    // stage must refuse to classify and leave the file untouched.
                    await store.MarkAutoTagSnapshotErrorAsync("job", path);
                }

                var statuses = new List<TaggingStatusWrap>(); var logs = new List<string>(); var batches = 0;
                var result = await runner.RunAsync("job", directory, configPath, statuses.Add, logs.Add,
                    (_, _) => { batches++; return Task.CompletedTask; }, new AutoTagResumeCursor(0, 0, 1, 1), CancellationToken.None);
                Assert.True(result.Success, result.Error);
                Assert.Contains(statuses, status => status.Platform == "genre-intelligence");

                if (breakSnapshot)
                {
                    // The stage must have refused and said why. Asserting on the
                    // reported outcome rather than only the file contents proves
                    // the refusal came from the snapshot guard rather than from
                    // something else failing first, and that the user was told.
                    var failure = Assert.Single(
                        statuses.Where(status =>
                            status.Status?.Status == "error"
                            && status.Platform == "genre-intelligence"));
                    Assert.Equal("genre_intelligence_checkpoint_error", failure.Status!.Outcome);
                    Assert.Contains("incomplete semantic snapshot", failure.Status.Message ?? string.Empty, StringComparison.Ordinal);

                    // The file is exactly as the user left it.
                    using var preserved = TagLib.File.Create(path);
                    Assert.Equal(new[] { "My Personal Genre", "Pop" }, preserved.Tag.Genres);
                    return;
                }

                if (batched)
                {
                    Assert.Equal(1, batches);
                }

                // Pop is canonical; the personal value is kept in the field it was
                // read from rather than deleted.
                using var tagged = TagLib.File.Create(path);
                Assert.Contains("Pop", tagged.Tag.Genres);
                Assert.Contains("My Personal Genre", tagged.Tag.Genres);
                Assert.Equal("Original Title", tagged.Tag.Title);

                // The observed snapshot is recorded before the rewrite.
                var record = await store.GetLatestAutoTagResultAsync(0, path);
                Assert.NotNull(record);
                Assert.Equal("My Personal Genre", record!.Resolution.Preserved.Single().Value);
                Assert.Equal(PersonalGenreTaxonKind.Genre, record.Resolution.Preserved.Single().InputField);
                var observed = record.Resolution.Observations;
                Assert.Contains(observed, item => item.RawValue == "My Personal Genre" && item.InputField == PersonalGenreTaxonKind.Genre);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// The defining reconciliation case: a platform wrote its own genre and dropped
    /// the user's personal value, and the value came back anyway.
    /// </summary>
    [Fact]
    public async Task PersonalTagDroppedByAPlatformIsReconciledBackIntoTheFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "genre-reconcile-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "track.flac");
            CreateSilentAudio(path);
            using (var file = TagLib.File.Create(path))
            {
                file.Tag.Genres = ["My Personal Genre", "Pop"];
                file.Save();
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(directory, "library.db")
            }).Build();
            await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            var store = new PersonalGenreStore(configuration);
            var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLogging()
                .AddSingleton(store).AddSingleton<LibraryRepository>().AddSingleton<PersonalGenreService>().BuildServiceProvider();
            using (services)
            {
                // Stand in for the pre-AutoTag capture: the file as the user left it.
                var pre = PersonalGenreService.ReadFileSnapshot(path);
                await store.SaveSnapshotAsync("job", path, GenreSnapshotStage.PreAutoTag, pre, null);

                // Now a platform overwrites the file with its own opinion,
                // discarding the personal value.
                using (var file = TagLib.File.Create(path))
                {
                    file.Tag.Genres = ["Bongo Flava"];
                    file.Save();
                }

                var collaborators = (LocalAutoTagRunner.LocalAutoTagRunnerCollaborators)Activator.CreateInstance(typeof(LocalAutoTagRunner.LocalAutoTagRunnerCollaborators))!;
                void Set(string name, object value) => collaborators.GetType().GetProperty(name)!.SetValue(collaborators, value);
                Set("Logger", NullLogger<LocalAutoTagRunner>.Instance);
                Set("ServiceScopeFactory", services.GetRequiredService<IServiceScopeFactory>());
                Set("ShazamRecognitionService", new ShazamRecognitionService(null!, null!, NullLogger<ShazamRecognitionService>.Instance));
                var runner = new LocalAutoTagRunner(collaborators);
                var configPath = Path.Combine(directory, "config.json");
                await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
                {
                    platforms = Array.Empty<string>(),
                    tags = new[] { "genre" },
                    targetFiles = new[] { path },
                    enableShazam = false,
                    parseFilename = false,
                    genreIntelligence = new { enabled = true }
                }));

                var statuses = new List<TaggingStatusWrap>();
                var result = await runner.RunAsync("job", directory, configPath, statuses.Add, _ => { },
                    (_, _) => Task.CompletedTask, new AutoTagResumeCursor(0, 0, 1, 1), CancellationToken.None);
                Assert.True(result.Success, result.Error);

                using var tagged = TagLib.File.Create(path);
                Assert.Contains("Bongo Flava", tagged.Tag.Genres);
                Assert.Contains("My Personal Genre", tagged.Tag.Genres);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AutoTagHostsGenreIntelligenceBetweenEnhancementAndTechnical()
    {
        var root = ResolveRoot();
        var view = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web/Views/AutoTag/Index.cshtml"));
        var enhancement = view.IndexOf("id=\"autotag-stage3-tab\"", StringComparison.Ordinal);
        var genre = view.IndexOf("id=\"autotag-genre-intelligence-tab\"", StringComparison.Ordinal);
        var technical = view.IndexOf("id=\"autotag-technical-tab\"", StringComparison.Ordinal);
        Assert.True(enhancement < genre && genre < technical);
        Assert.Contains("@await Html.PartialAsync(\"_GenreIntelligence\")", view);
        Assert.DoesNotContain("<partial name=\"_GenreIntelligence\"", view);
        Assert.Contains("<div class=\"card bg-darker border-secondary mb-4 autotag-quality-background\">\n                <div class=\"card-header\"><h5 class=\"mb-0\"><i class=\"fas fa-sliders me-2\"></i>Profile runtime settings</h5></div>", view);
        var layout = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web/Views/Shared/_Layout.cshtml"));
        Assert.DoesNotContain("class=\"menu-item genre-intelligence\"", layout);
        Assert.Contains("id=\"autotagGenreIntelligenceEnabled\"", view);
        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectResult>(new DeezSpoTag.Web.Controllers.GenreIntelligenceController().Index());
        Assert.Equal("/AutoTag?tab=autotag-genre-intelligence-panel", result.Url);
    }

    [Fact]
    public async Task FileSnapshotsSurviveStoreRecreationAndFileMoves()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = "Data Source=" + path
            }).Build();
            var store = new PersonalGenreStore(configuration);
            var snapshot = new GenreSemanticSnapshot(
            [
                new GenreTagObservation("My Personal Genre", PersonalGenreTaxonKind.Genre, 0),
                new GenreTagObservation("Bongo Flava", PersonalGenreTaxonKind.Genre, 1),
                new GenreTagObservation("Southern Hip-Hop", PersonalGenreTaxonKind.Style, 2)
            ],
                DateTimeOffset.UtcNow);
            await store.SaveSnapshotAsync("job-1", "/music/track.flac", GenreSnapshotStage.PreAutoTag, snapshot, null);

            var recreated = new PersonalGenreStore(configuration);
            var loaded = await recreated.GetSnapshotAsync("job-1", "/music/track.flac", GenreSnapshotStage.PreAutoTag);
            Assert.Equal(snapshot.Observations, loaded!.Observations);
            Assert.Null(await recreated.GetSnapshotAsync("job-2", "/music/track.flac", GenreSnapshotStage.PreAutoTag));
            // Stage is part of the key: the pre- and post-AutoTag states are distinct.
            Assert.Null(await recreated.GetSnapshotAsync("job-1", "/music/track.flac", GenreSnapshotStage.PostAutoTag));

            var resolution = PersonalGenreResolver.Resolve(snapshot.Observations);
            await store.SaveAutoTagResultAsync("job-1", "/music/track.flac", null, resolution, "pending");
            Assert.Null(await recreated.GetLatestAutoTagResultAsync(42, "/music/track.flac"));
            await store.SaveAutoTagResultAsync("job-1", "/music/track.flac", null, resolution, "written:Genre");
            var stored = await recreated.GetLatestAutoTagResultAsync(42, "/music/track.flac");
            Assert.Equal(42, stored!.TrackId);
            Assert.Equal("Bongo Flava", stored.Resolution.PrimaryGenre);
            Assert.Equal(snapshot.Observations, stored.Resolution.Observations);

            await store.RememberAutoTagTrackIdAsync("job-1", "/music/track.flac", 42);
            await store.CopyAutoTagCheckpointAsync("job-1", "/music/track.flac", "/music/moved.flac");
            var moved = await recreated.GetSnapshotAsync("job-1", "/music/moved.flac", GenreSnapshotStage.PreAutoTag);
            Assert.Equal(snapshot.Observations, moved!.Observations);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    /// <summary>
    /// An installation that already has a provider-era database must upgrade in
    /// place. The obsolete columns stay, nothing new writes them, and the user's
    /// existing settings and personal taxonomy survive.
    /// </summary>
    [Fact]
    public async Task ProviderEraDatabaseMigratesInPlaceWithoutLosingPersonalConfiguration()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = "Data Source=" + path
            }).Build();
            var store = new PersonalGenreStore(configuration);
            await store.SaveSettingsAsync(new PersonalGenreSettings(true, 5, false, true));
            await store.UpsertCustomTaxonAsync(
                new PersonalGenreTaxon("my-afro-mix", "My Afro Mix", PersonalGenreTaxonKind.Style));
            await store.UpsertRuleAsync(
                new PersonalGenreRule(0, "Sunday Chill", "my-afro-mix", Priority: 10));

            // Wipe the file to the old shape and re-run schema creation, as an
            // existing installation would present it.
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(configuration.GetConnectionString("Library")))
            {
                await connection.OpenAsync();
                await using (var drop = connection.CreateCommand())
                {
                    drop.CommandText = "DROP TABLE personal_genre_snapshot; DROP TABLE personal_genre_mapping; DROP TABLE personal_genre_rule; DROP TABLE personal_genre_settings;";
                    await drop.ExecuteNonQueryAsync();
                }

                await using (var legacy = connection.CreateCommand())
                {
                    legacy.CommandText = """
                        CREATE TABLE personal_genre_settings (
                            id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                            enabled INTEGER NOT NULL DEFAULT 1,
                            max_genres INTEGER NOT NULL DEFAULT 3,
                            preserve_provider_fallback INTEGER NOT NULL DEFAULT 1,
                            include_parent_genres INTEGER NOT NULL DEFAULT 0,
                            updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
                        CREATE TABLE personal_genre_mapping (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            match_value TEXT NOT NULL,
                            target_taxon_id TEXT NOT NULL,
                            source TEXT,
                            priority INTEGER NOT NULL DEFAULT 100,
                            enabled INTEGER NOT NULL DEFAULT 1,
                            action TEXT NOT NULL DEFAULT 'map',
                            created_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                            updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
                        CREATE TABLE personal_genre_rule (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            match_value TEXT NOT NULL,
                            target_taxon_id TEXT NOT NULL,
                            source TEXT,
                            priority INTEGER NOT NULL DEFAULT 1000,
                            enabled INTEGER NOT NULL DEFAULT 1,
                            created_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                            updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
                        INSERT INTO personal_genre_settings (id, enabled, max_genres, preserve_provider_fallback, include_parent_genres)
                            VALUES (1, 1, 5, 0, 1);
                        INSERT INTO personal_genre_mapping (match_value, target_taxon_id, source, priority, enabled, action)
                            VALUES ('Afrosounds', 'bongo-flava', 'audiomack', 500, 1, 'map');
                        INSERT INTO personal_genre_rule (match_value, target_taxon_id, source, priority, enabled)
                            VALUES ('Latin Urban', 'reggaeton', 'spotify', 900, 1);
                        """;
                    await legacy.ExecuteNonQueryAsync();
                }
            }

            var migrated = new PersonalGenreStore(configuration);

            // Settings carried across, including the preservation intent.
            var settings = await migrated.GetSettingsAsync();
            Assert.Equal(5, settings.MaxGenres);
            Assert.True(settings.IncludeParentGenres);
            Assert.False(settings.PreserveUnmappedTags);

            // A provider-era source is not a file field, so it is dropped rather
            // than silently never matching.
            var mapping = Assert.Single(await migrated.GetMappingsAsync());
            Assert.Equal("Afrosounds", mapping.MatchValue);
            Assert.Null(mapping.InputField);

            var rule = Assert.Single(await migrated.GetRulesAsync());
            Assert.Equal("Latin Urban", rule.MatchValue);
            Assert.Null(rule.InputField);

            // The user's own taxonomy is untouched by the migration.
            Assert.Equal("My Afro Mix", Assert.Single(await migrated.GetCustomTaxaAsync()).Name);

            // The old tables are still there, and the new columns exist.
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(configuration.GetConnectionString("Library")))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT source FROM personal_genre_mapping WHERE id = 1;";
                Assert.Equal("audiomack", (string)(await command.ExecuteScalarAsync())!);
                await using var probe = connection.CreateCommand();
                probe.CommandText = "PRAGMA table_info(personal_genre_settings);";
                await using var reader = await probe.ExecuteReaderAsync();
                var columns = new List<string>();
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(1));
                }

                Assert.Contains("preserve_provider_fallback", columns);
                Assert.Contains("preserve_unmapped_tags", columns);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public void TerminalWriteReplacesSelectedSemanticsAndPreservesIdentity(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "." + extension);
        try
        {
            CreateSilentAudio(path);
            using (var file = TagLib.File.Create(path))
            {
                file.Tag.Title = "Original Title"; file.Tag.Album = "Original Album";
                file.Tag.Performers = ["Original Artist"];
                file.Tag.Genres = ["My Personal Genre", "Afrosounds", "Pop"];
                file.Save();
            }

            var type = typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunnerConfig", BindingFlags.NonPublic)!;
            var config = JsonSerializer.Deserialize("{\"Tags\":[\"genre\",\"style\",\"language\"],\"StylesOptions\":\"customTag\",\"StylesCustomTag\":{\"Id3\":\"PERSONAL_STYLE\",\"Vorbis\":\"PERSONAL_STYLE\",\"Mp4\":\"PERSONAL_STYLE\"},\"GenreIntelligence\":{\"Enabled\":true,\"WriteSubstyle\":true,\"WriteContext\":true,\"WriteScene\":true}}", type)!;

            // The file's actual tags, including the personal value the taxonomy
            // has never heard of.
            var resolution = PersonalGenreResolver.Resolve(
            [
                new GenreTagObservation("Bongo Flava", PersonalGenreTaxonKind.Genre, 0),
                new GenreTagObservation("Afrosounds", PersonalGenreTaxonKind.Genre, 1),
                new GenreTagObservation("My Personal Genre", PersonalGenreTaxonKind.Genre, 2)
            ]);
            resolution = resolution with
            {
                Styles = ["Bongo Flava Rap"],
                Substyles = ["Alternative"],
                Scenes = ["Zilizopendwa"],
                Languages = ["Swahili"]
            };
            var write = typeof(LocalAutoTagRunner).GetMethod("WriteGenreIntelligenceTags", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(write);
            // The writer takes the prebuilt plan, exactly as the terminal stage
            // does, so the payload under test is the one that was validated.
            Write(write, path, config, resolution);

            using var result = TagLib.File.Create(path);
            Assert.Equal(new[] { "Bongo Flava", "My Personal Genre" }, ReadGenres(result, extension));
            Assert.Equal("Original Title", result.Tag.Title);
            Assert.Equal("Original Album", result.Tag.Album);
            Assert.Equal(new[] { "Original Artist" }, result.Tag.Performers);

            var read = typeof(LocalAutoTagRunner).GetMethod("ReadRawTagValues", BindingFlags.NonPublic | BindingFlags.Static)!;
            var contexts = (List<string>)read.Invoke(null, [result, "." + extension, "CONTEXT"])!;
            Assert.Contains("Afrosounds", contexts);
            foreach (var (name, expected) in new[]
                     {
                         ("PERSONAL_STYLE", "Bongo Flava Rap"), ("SUBSTYLE", "Alternative"),
                         ("SCENE", "Zilizopendwa"), ("LANGUAGE", "Swahili")
                     })
            {
                var values = (List<string>)read.Invoke(null, [result, "." + extension, name])!;
                Assert.Contains(expected, values);
            }

            // An empty resolution must not clear the file.
            Write(write, path, config, PersonalGenreResolver.Resolve(Array.Empty<GenreTagObservation>()));
            using var unchanged = TagLib.File.Create(path);
            Assert.Equal(new[] { "Bongo Flava", "My Personal Genre" }, ReadGenres(unchanged, extension));

            // A lock still decides the final genre, and the personal value is
            // still preserved alongside it.
            var locked = PersonalGenreResolver.Resolve(resolution.Observations, locks:
            [
                new PersonalGenreLock(1, "pop", ScopeType: "artist"),
                new PersonalGenreLock(1, "bongo-flava", ScopeType: "album"),
                new PersonalGenreLock(1, "reggae", ScopeType: "track")
            ]);
            Assert.Equal("Reggae", locked.PrimaryGenre);

            // Opting out of the write leaves the file alone.
            type.GetProperty("Tags")!.SetValue(config, new List<string>());
            Write(write, path, config, locked);
            using (var disabled = TagLib.File.Create(path))
            {
                Assert.Equal(new[] { "Bongo Flava", "My Personal Genre" }, ReadGenres(disabled, extension));
            }

            type.GetProperty("Tags")!.SetValue(config, new List<string> { "genre" });
            Write(write, path, config, locked);
            using var final = TagLib.File.Create(path);
            Assert.Equal(new[] { "Reggae", "My Personal Genre" }, ReadGenres(final, extension));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerminalPlatformIsOptInAndCannotBeReordered(bool enabled)
    {
        var type = typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunnerConfig", BindingFlags.NonPublic)!;
        var config = JsonSerializer.Deserialize("{\"Platforms\":[\"genre-intelligence\",\"spotify\",\"lastfm\",\"genre-intelligence\"],\"GenreIntelligence\":{\"Enabled\":" + enabled.ToString().ToLowerInvariant() + "}}", type)!;
        var method = typeof(LocalAutoTagRunner).GetMethod("BuildEffectivePlatforms", BindingFlags.NonPublic | BindingFlags.Static)!;
        config = typeof(LocalAutoTagRunner).GetMethod("NormalizeConfig", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [config])!;
        var actual = (System.Collections.Generic.List<string>)method.Invoke(null, new object?[] { config, null })!;
        Assert.Equal(enabled ? new[] { "spotify", "lastfm", "genre-intelligence" } : new[] { "spotify", "lastfm" }, actual);
    }

    [Fact]
    public void OldProfilesDefaultToDisabled()
    {
        var type = typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunnerConfig", BindingFlags.NonPublic)!;
        var config = JsonSerializer.Deserialize("{\"Platforms\":[\"spotify\"]}", type)!;
        var settings = type.GetProperty("GenreIntelligence")!.GetValue(config)!;
        Assert.False((bool)settings.GetType().GetProperty("Enabled")!.GetValue(settings)!);
        // Unrecognised file values are preserved by default, so enabling the stage
        // can never delete a tag the user wrote themselves.
        Assert.True((bool)settings.GetType().GetProperty("PreserveUnmappedTags")!.GetValue(settings)!);
    }

    /// <summary>
    /// Invokes the terminal writer with the plan it would build in a real run.
    /// </summary>
    private static void Write(MethodInfo writer, string path, object config, PersonalGenreResolution resolution)
    {
        var plan = GenreSemanticTagIo.PlanFields(resolution);
        writer.Invoke(null, [path, config, resolution, plan]);
    }

    /// <summary>
    /// Reads the genre values back the way the resolver reads them.
    ///
    /// An MP3 stores TCON as one separator-joined string, so TagLib returns
    /// "Bongo Flava, My Personal Genre" as a single value while FLAC and MP4
    /// return two. Splitting on the separator is what the AutoTag reader already
    /// does, so this keeps the assertion about the values rather than the
    /// container's storage quirk.
    /// </summary>
    private static IReadOnlyList<string> ReadGenres(TagLib.File file, string extension)
    {
        var values = new List<string>();
        foreach (var value in file.Tag.Genres)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (extension.TrimStart('.').Equals("mp3", StringComparison.OrdinalIgnoreCase))
            {
                values.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim()));
            }
            else
            {
                values.Add(value.Trim());
            }
        }

        return values;
    }

    private static void CreateSilentAudio(string path)
    {
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (var argument in new[]
                 {
                     "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "0.1", path
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    private static string LoadGenreMarkup()
    {
        var markup = File.ReadAllText(Path.Combine(ResolveRoot(), "DeezSpoTag.Web/Views/Shared/_GenreIntelligence.cshtml"));
        // Razor's own attributes are rewritten so the file parses as plain XML.
        return Regex.Replace(markup, @"\s(required|checked)(?=\s|/?>)", match => $" {match.Groups[1].Value}=\"{match.Groups[1].Value}\"");
    }

    private static string ResolveRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeezSpoTag.Web/DeezSpoTag.Web.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
