using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Task 1 contracts: the shared credit-role registry, the legacy
/// combined-credit (`involvedPeople`) profile migration to independent
/// role keys, and the runner wiring that resolves those keys everywhere
/// whitelist semantics already resolve existing tag keys.
/// </summary>
public sealed class AutoTagCreditRoleTest
{
    // ------------------------------------------------------------------
    // Section A — shared registry
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Producer", "producer")]
    [InlineData("producer", "producer")]
    [InlineData("Mixed by", "mixingEngineer")]
    [InlineData("Mixed By", "mixingEngineer")]
    [InlineData("mixing-engineer", "mixingEngineer")]
    [InlineData("Written-By", "songwriter")]
    [InlineData("Written by", "songwriter")]
    [InlineData("Produced by", "producer")]
    [InlineData("Performed by", "performer")]
    [InlineData("DJ Mixer", "djMixer")]
    [InlineData("Recorded by", "recordingEngineer")]
    [InlineData("Remix", "remixer")]
    [InlineData("Published by", "publisher")]
    [InlineData("Directed by", "director")]
    [InlineData("mastered by", "masteringEngineer")]
    [InlineData("Executive Producer", "executiveProducer")]
    [InlineData("Co-Producer", "coProducer")]
    [InlineData("composers", "composer")]
    [InlineData("SONGWRITER", "songwriter")]
    public void NormalizeRoles_ResolvesAliasesCaseAndSeparatorInsensitively(string label, string expectedKey)
    {
        var roles = SupportedTagCreditMappings.NormalizeRoles(label);

        var role = Assert.Single(roles);
        Assert.Equal(expectedKey, role.Key);
    }

    [Fact]
    public void NormalizeRoles_SplitsTopLevelCommasIntoMultipleRoles()
    {
        var roles = SupportedTagCreditMappings.NormalizeRoles("Producer, Mixing Engineer");

        Assert.Equal(2, roles.Count);
        Assert.Equal("producer", roles[0].Key);
        Assert.Equal("mixingEngineer", roles[1].Key);
    }

    [Fact]
    public void NormalizeRoles_CompoundEngineerBracketExpandsToBothRoles()
    {
        var roles = SupportedTagCreditMappings.NormalizeRoles("Engineer [Recording Engineer, Mixing Engineer]");

        Assert.Equal(2, roles.Count);
        var keys = roles.Select(role => role.Key).ToArray();
        Assert.Contains("recordingEngineer", keys);
        Assert.Contains("mixingEngineer", keys);
        // The original compound label must survive losslessly as provenance.
        Assert.All(roles, role => Assert.Equal("Engineer [Recording Engineer, Mixing Engineer]", role.OriginalLabel));
    }

    [Fact]
    public void NormalizeRoles_UnrecognizedLabelStaysLegacyWithFullLabel()
    {
        var roles = SupportedTagCreditMappings.NormalizeRoles("Loudness Mastering");

        var role = Assert.Single(roles);
        Assert.Equal(string.Empty, role.Key);
        Assert.Equal("Loudness Mastering", role.OriginalLabel);
    }

    [Fact]
    public void NormalizeRoles_RecognizedMainWithUnrecognizedQualifierStaysSingle()
    {
        var roles = SupportedTagCreditMappings.NormalizeRoles("Engineer [Loudness Mastering]");

        var role = Assert.Single(roles);
        Assert.Equal("engineer", role.Key);
        Assert.Equal("Engineer [Loudness Mastering]", role.OriginalLabel);
    }

    [Fact]
    public void NormalizeRoles_QualifierDoesNotInventAnAdditionalRole()
    {
        var role = Assert.Single(SupportedTagCreditMappings.NormalizeRoles("Producer [Remix]"));
        Assert.Equal("producer", role.Key);
        Assert.Equal("Producer [Remix]", role.OriginalLabel);
    }

    [Theory]
    [InlineData("Assistant")]
    [InlineData("DJ")]
    [InlineData("Co-Executive Producer")]
    [InlineData("Word By")]
    public void NormalizeRoles_AmbiguousLabelsRemainUnclassified(string label)
    {
        var role = Assert.Single(SupportedTagCreditMappings.NormalizeRoles(label));
        Assert.Equal(string.Empty, role.Key);
        Assert.Equal(label, role.OriginalLabel);
    }

    [Fact]
    public void NormalizeRoles_QuallifyingSpecializationsSubsumeTheGeneric()
    {
        var roles = SupportedTagCreditMappings.NormalizeRoles("Producer [Executive Producer, Vocal Producer]");

        Assert.Equal(2, roles.Count);
        var keys = roles.Select(role => role.Key).ToArray();
        Assert.Contains("executiveProducer", keys);
        Assert.Contains("vocalProducer", keys);
        Assert.DoesNotContain("producer", keys);
    }

    [Fact]
    public void CollectAutoTagTagsRecognizesCreditsFromLegacyProviderBuckets()
    {
        var track = new AutoTagTrack();
        track.Other["writer"] = ["Writer Person"];
        track.Other["involvedPeople"] = ["Producer: Producer Person", "Mixer: Mixer Person", "Performer (guitar): Player Person", "Producer, Written-By: Multi Role Person"];
        var collect = typeof(LocalAutoTagRunner).GetMethod("CollectAutoTagTags", BindingFlags.NonPublic | BindingFlags.Static)!;
        var tags = (List<string>)collect.Invoke(null, [track])!;
        Assert.Contains("songwriter", tags);
        Assert.Contains("producer", tags);
        Assert.Contains("mixingEngineer", tags);
        Assert.Contains("performer", tags);
        Assert.DoesNotContain("composer", tags);
        Assert.Equal(["Writer Person", "Multi Role Person"], track.Other["songwriter"]);
        Assert.Contains("Multi Role Person", track.Other["producer"]);
        Assert.Contains("guitar", Assert.Single(track.Other["performer"]));
    }

    [Fact]
    public void NormalizeRoles_BlankAndNullProduceNoRoles()
    {
        Assert.Empty(SupportedTagCreditMappings.NormalizeRoles(null));
        Assert.Empty(SupportedTagCreditMappings.NormalizeRoles("   "));
    }

    [Fact]
    public void LegacyCombinedCreditRoles_PreservesDeezerWritingAndLyrics()
    {
        var legacy = SupportedTagCreditMappings.LegacyCombinedCreditRoles;

        Assert.Contains("songwriter", legacy);
        Assert.Contains("lyricist", legacy);
        Assert.DoesNotContain("composer", legacy);
        Assert.DoesNotContain("remixer", legacy);
        Assert.DoesNotContain("publisher", legacy);

        // Every remaining covered role participates exactly once and is also
        // a registered canonical role.
        Assert.Equal(18, legacy.Count);
        Assert.Equal(legacy.Count, legacy.Distinct().Count());
        Assert.All(legacy, key =>
            Assert.True(SupportedTagCreditMappings.TryGetRole(key, out _), key));
    }

    [Theory]
    [InlineData("Producer: Tomas Bangalter", "producer", "Tomas Bangalter")]
    [InlineData("producer:Guy-Manuel", "producer", "Guy-Manuel")]
    [InlineData("Instrument: Piano", "performer", "Piano")]
    [InlineData("Written By: Nile Rodgers", "songwriter", "Nile Rodgers")]
    public void TryParseCombinedCreditEntry_ParsesRecognizedEntries(
        string entry, string expectedKey, string expectedName)
    {
        Assert.True(SupportedTagCreditMappings.TryParseCombinedCreditEntry(entry, out var key, out var name));
        Assert.Equal(expectedKey, key);
        Assert.Equal(expectedName, name);
    }

    [Theory]
    [InlineData("Chef: Antoine")]
    [InlineData("Producer:")]
    [InlineData("Tomas Bangalter")]
    [InlineData(null)]
    public void TryParseCombinedCreditEntry_RejectsUnrecognizedOrMalformed(string? entry)
    {
        Assert.False(SupportedTagCreditMappings.TryParseCombinedCreditEntry(entry, out _, out _));
    }

    [Fact]
    public void TryGetRawTagFor_BindsRolesAndMelodicnessToPhysicalTags()
    {
        Assert.True(SupportedTagCreditMappings.TryGetRawTagFor(SupportedTag.Songwriter, out var songwriter));
        Assert.Equal("SONGWRITER", songwriter);
        Assert.True(SupportedTagCreditMappings.TryGetRawTagFor(SupportedTag.RecordingEngineer, out var recording));
        Assert.Equal("RECORDINGENGINEER", recording);
        Assert.True(SupportedTagCreditMappings.TryGetRawTagFor(SupportedTag.DjMixer, out var dj));
        Assert.Equal("DJMIXER", dj);
        Assert.True(SupportedTagCreditMappings.TryGetRawTagFor(SupportedTag.Melodicness, out var melodicness));
        Assert.Equal("MELODICNESS", melodicness);
        Assert.False(SupportedTagCreditMappings.TryGetRawTagFor(SupportedTag.Title, out _));
    }

    [Fact]
    public void TryGetRoleFor_MapsEnumMembersToRegistryDefinitions()
    {
        Assert.True(SupportedTagCreditMappings.TryGetRoleFor(SupportedTag.Producer, out var producer));
        Assert.Equal("producer", producer.Key);
        Assert.True(SupportedTagCreditMappings.TryGetRoleFor(SupportedTag.Director, out var director));
        Assert.Equal("director", director.Key);
        Assert.False(SupportedTagCreditMappings.TryGetRoleFor(SupportedTag.Title, out _));
    }

    [Fact]
    public void AddSupportedTagMappings_RegistersEveryRolePlusMelodicness()
    {
        var map = new Dictionary<string, SupportedTag>(StringComparer.OrdinalIgnoreCase);

        SupportedTagCreditMappings.AddSupportedTagMappings(map);

        foreach (var key in SupportedTagCreditMappings.LegacyCombinedCreditRoles)
        {
            Assert.True(map.TryGetValue(key, out var tag), key);
            Assert.Equal(
                key,
                ToTagKey(SupportedTagCreditMappings.TryGetRoleFor(tag, out var role) ? role.Key : ""),
                ignoreCase: true);
        }

        Assert.True(map.ContainsKey("songwriter"));
        Assert.Equal(SupportedTag.Melodicness, map["melodicness"]);
    }

    private static string ToTagKey(string key) => key;

    // ------------------------------------------------------------------
    // Section B — legacy combined-credit migration
    // ------------------------------------------------------------------

    private static Dictionary<string, JsonElement> Data(params (string Key, object Value)[] entries)
    {
        var data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries)
        {
            data[key] = JsonSerializer.SerializeToElement(value);
        }

        return data;
    }

    private static List<string> Strings(Dictionary<string, JsonElement> data, string key)
    {
        return data[key].EnumerateArray().Select(item => item.GetString()!).ToList();
    }

    [Fact]
    public void Migration_ExpandsLegacyKeyInAllWhitelistsAndStampsVersion()
    {
        var data = Data(
            ("downloadTags", new[] { "title", "involvedPeople" }),
            ("tags", new[] { "involvedPeople", "genre" }),
            ("gapFillTags", new[] { "involvedPeople" }),
            ("overwriteTags", new[] { "involvedPeople" }));

        var changed = TaggingProfileCanonicalizer.MigrateLegacyCreditControls(data);

        Assert.True(changed);
        foreach (var key in new[] { "downloadTags", "tags", "gapFillTags", "overwriteTags" })
        {
            var list = Strings(data, key);
            Assert.Contains("involvedPeople", list); // retained for rollback
            Assert.All(SupportedTagCreditMappings.LegacyCombinedCreditRoles, role => Assert.Contains(role, list));
            Assert.Contains("songwriter", list);
            Assert.Contains("lyricist", list);
            Assert.DoesNotContain("composer", list);
            Assert.DoesNotContain("remixer", list);
            Assert.DoesNotContain("publisher", list);
        }

        Assert.True(data.TryGetValue("creditControlsVersion", out var version));
        Assert.Equal(1, version.GetInt32());
    }

    [Fact]
    public void Migration_IsIdempotentThroughVersionStamp()
    {
        var data = Data(
            ("downloadTags", new[] { "involvedPeople" }),
            ("overwriteTags", new[] { "involvedPeople" }));

        Assert.True(TaggingProfileCanonicalizer.MigrateLegacyCreditControls(data));
        var afterFirst = JsonSerializer.Serialize(data);

        Assert.False(TaggingProfileCanonicalizer.MigrateLegacyCreditControls(data));
        Assert.Equal(afterFirst, JsonSerializer.Serialize(data));
    }

    [Fact]
    public void Migration_VersionedProfileWithAbsentRolesAddsNothing()
    {
        // A profile stamped by a previous migration pass: an absent role is an
        // explicit off, so migration must not re-add roles from presence of
        // anything.
        var data = Data(
            ("creditControlsVersion", 1),
            ("downloadTags", new[] { "title" }));

        Assert.False(TaggingProfileCanonicalizer.MigrateLegacyCreditControls(data));
        Assert.DoesNotContain("producer", Strings(data, "downloadTags"));
    }

    [Fact]
    public void ExplicitNewWritingRoleOffRemainsOffWithTheLegacyToggleStillSelected()
    {
        var data = Data(
            ("creditControlsVersion", 1),
            ("tags", new[] { "involvedPeople", "producer" }),
            ("overwriteTags", new[] { "involvedPeople" }));
        Assert.False(TaggingProfileCanonicalizer.MigrateLegacyCreditControls(data));
        Assert.DoesNotContain("songwriter", Strings(data, "tags"));
        Assert.DoesNotContain("lyricist", Strings(data, "tags"));
        Assert.DoesNotContain("songwriter", Strings(data, "overwriteTags"));
        Assert.DoesNotContain("lyricist", Strings(data, "overwriteTags"));
    }

    [Fact]
    public void Migration_RespectsRolesAlreadyNamedExplicitly()
    {
        var data = Data(
            ("downloadTags", new[] { "involvedPeople", "producer", "mixingEngineer" }));

        TaggingProfileCanonicalizer.MigrateLegacyCreditControls(data);

        var list = Strings(data, "downloadTags");
        Assert.Equal(1, list.Count(entry => entry == "producer"));
        Assert.Equal(1, list.Count(entry => entry == "mixingEngineer"));
        Assert.Contains("recordingEngineer", list);
        Assert.Equal(19, list.Count); // involvedPeople + 18 roles, no duplicates
    }

    [Fact]
    public void Migration_MatchesLegacyKeyCaseInsensitively()
    {
        var data = Data(("downloadTags", new[] { "InvolvedPeople" }));

        TaggingProfileCanonicalizer.MigrateLegacyCreditControls(data);

        var list = Strings(data, "downloadTags");
        Assert.Contains("involvedPeople", list);
        Assert.Contains("djMixer", list);
    }

    [Fact]
    public void Migration_ArrayWithoutLegacyKeyAddsNoRolesButStamps()
    {
        var data = Data(
            ("downloadTags", new[] { "title", "artist" }),
            ("overwriteTags", Array.Empty<string>()));

        var changed = TaggingProfileCanonicalizer.MigrateLegacyCreditControls(data);

        // Global overwrite choice stays untouched; migration only appends
        // roles where the legacy key was selected, and stamps the version so
        // repeated runs never reinterpret absence.
        Assert.DoesNotContain("overwrite", data.Keys);
        Assert.DoesNotContain("producer", Strings(data, "downloadTags"));
        Assert.DoesNotContain("songwriter", Strings(data, "downloadTags"));
        Assert.Empty(Strings(data, "overwriteTags"));
        Assert.True(data.TryGetValue("creditControlsVersion", out _));
    }

    [Fact]
    public void Migration_NormalizesLegacyAliasesWhileExpanding()
    {
        // "Involved People" is the legacy selected key in some older profiles;
        // canonical tag aliases must still be recognized through the registry
        // path (composer was the buggy misclassification for songwriting).
        var data = Data(
            ("downloadTags", new[] { "involvedPeople", "producer" }),
            ("overwriteTags", new[] { "involvedPeople" }));

        TaggingProfileCanonicalizer.MigrateLegacyCreditControls(data);

        // Deezer supplied writing credits through the legacy combined field.
        Assert.Contains("songwriter", Strings(data, "downloadTags"));
        Assert.Contains("songwriter", Strings(data, "overwriteTags"));
        Assert.DoesNotContain("composer", Strings(data, "downloadTags"));
    }

    [Fact]
    public void Migration_NullDataIsNoOp()
    {
        Assert.False(TaggingProfileCanonicalizer.MigrateLegacyCreditControls(null));
    }

    [Fact]
    public void TryParseCombinedCreditEntry_SamePersonInTwoRolesStaysDistinct()
    {
        // Duplicate name across roles: the person is the value, the role is
        // the key. The registry resolves role roles independently and never
        // merges the same person into one artist-identity entry.
        Assert.True(SupportedTagCreditMappings.TryParseCombinedCreditEntry("Producer: Tomas Bangalter", out var producer, out var producerName));
        Assert.True(SupportedTagCreditMappings.TryParseCombinedCreditEntry("Mixing Engineer: Tomas Bangalter", out var mixing, out var mixingName));
        Assert.Equal("producer", producer);
        Assert.Equal("mixingEngineer", mixing);
        Assert.Equal(producerName, mixingName);
    }

    [Fact]
    public void TryParseCombinedCreditEntry_SeveralPeoplePerRoleShareTheKey()
    {
        // Several people per role: every entry resolves to the same canonical
        // key; multi-value entries are the writer's concern.
        Assert.True(SupportedTagCreditMappings.TryParseCombinedCreditEntry("Producer: Guy-Manuel de Homem-Christo", out var first, out _));
        Assert.True(SupportedTagCreditMappings.TryParseCombinedCreditEntry("Producer: Tomas Bangalter", out var second, out _));
        Assert.Equal(first, second);
    }

    // ------------------------------------------------------------------
    // Section C — runner wiring (config-key resolution, whitelist entries)
    // ------------------------------------------------------------------

    private static readonly BindingFlags StaticPrivate =
        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    [Fact]
    public void TagSettingsAppliers_EnableRoleFlagsForEveryRoleKey()
    {
        var field = typeof(LocalAutoTagRunner).GetField("TagSettingsAppliers", StaticPrivate)
            ?? throw new InvalidOperationException("TagSettingsAppliers field not found");
        var appliers = ( IReadOnlyDictionary<string, Action<DeezSpoTag.Core.Models.Settings.TagSettings>> )field.GetValue(null)!;

        foreach (var (key, flagName) in RoleFlagNames())
        {
            var settings = new DeezSpoTag.Core.Models.Settings.TagSettings();
            Assert.True(appliers.TryGetValue(key, out var applier), key);
            applier(settings);
            var property = typeof(DeezSpoTag.Core.Models.Settings.TagSettings).GetProperty(flagName)
                ?? throw new InvalidOperationException(flagName);
            Assert.True((bool)property.GetValue(settings)!, $"{key} must enable {flagName}");
        }
    }

    private static IEnumerable<(string Key, string Flag)> RoleFlagNames()
    {
        yield return ("songwriter", "Songwriter");
        yield return ("producer", "Producer");
        yield return ("executiveProducer", "ExecutiveProducer");
        yield return ("coProducer", "CoProducer");
        yield return ("additionalProducer", "AdditionalProducer");
        yield return ("vocalProducer", "VocalProducer");
        yield return ("engineer", "Engineer");
        yield return ("recordingEngineer", "RecordingEngineer");
        yield return ("mixingEngineer", "MixingEngineer");
        yield return ("masteringEngineer", "MasteringEngineer");
        yield return ("assistantEngineer", "AssistantEngineer");
        yield return ("arranger", "Arranger");
        yield return ("orchestrator", "Orchestrator");
        yield return ("conductor", "Conductor");
        yield return ("performer", "Performer");
        yield return ("djMixer", "DjMixer");
        yield return ("director", "Director");
        yield return ("melodicness", "Melodicness");
    }

    [Fact]
    public void SupportedTagMap_ResolvesEveryRoleKeyToItsEnumMember()
    {
        var method = typeof(LocalAutoTagRunner).GetMethod("CreateSupportedTagMap", StaticPrivate)
            ?? throw new InvalidOperationException("CreateSupportedTagMap method not found");
        var map = (Dictionary<string, SupportedTag>)method.Invoke(null, null)!;

        foreach (var key in RoleFlagNames().Select(entry => entry.Key))
        {
            if (key == "melodicness")
            {
                Assert.Equal(SupportedTag.Melodicness, map[key]);
                continue;
            }

            Assert.True(map.TryGetValue(key, out var tag), key);
            Assert.True(SupportedTagCreditMappings.TryGetRoleFor(tag, out var role), $"{key} -> {tag}");
            Assert.Equal(key, role.Key);
        }
    }

    [Fact]
    public void FirstClassRawOtherTags_ContainsEveryRoleRawTag()
    {
        var field = typeof(LocalAutoTagRunner).GetField("FirstClassRawOtherTags", StaticPrivate)
            ?? throw new InvalidOperationException("FirstClassRawOtherTags field not found");
        var tags = ( IEnumerable<string> )field.GetValue(null)!;
        var set = tags.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in new[]
        {
            "SONGWRITER", "PRODUCER", "EXECUTIVEPRODUCER", "COPRODUCER",
            "ADDITIONALPRODUCER", "VOCALPRODUCER", "ENGINEER", "RECORDINGENGINEER",
            "MIXINGENGINEER", "MASTERINGENGINEER", "ASSISTANTENGINEER", "ARRANGER",
            "ORCHESTRATOR", "CONDUCTOR", "PERFORMER", "DJMIXER", "DIRECTOR",
            "MELODICNESS"
        })
        {
            Assert.True(set.Contains(raw), raw);
        }
    }

    [Fact]
    public void CloneTagSettings_CopiesMelodicnessAndRoleFlags()
    {
        var method = typeof(LocalAutoTagRunner).GetMethods(StaticPrivate)
            . Single(m => m.Name == "CloneTagSettings")
            ?? throw new InvalidOperationException("CloneTagSettings method not found");
        var source = new DeezSpoTag.Core.Models.Settings.TagSettings
        {
            Melodicness = true,
            Songwriter = true,
            DjMixer = true,
            Producer = false
        };

        var clone = (DeezSpoTag.Core.Models.Settings.TagSettings)method.Invoke(null, new object?[] { source })!;

        Assert.True(clone.Melodicness);
        Assert.True(clone.Songwriter);
        Assert.True(clone.DjMixer);
        Assert.False(clone.Producer);
    }

    // ------------------------------------------------------------------
    // Section D — download-path conversion (UnifiedTagConfig → TagSettings)
    // ------------------------------------------------------------------

    [Fact]
    public void DownloadTagSettingsConverter_FlowsRoleSourcesIntoFlags()
    {
        var converter = new DownloadTagSettingsConverter();
        var config = new UnifiedTagConfig
        {
            Songwriter = TagSource.DownloadSource,
            Producer = TagSource.Both,
            MixingEngineer = TagSource.DownloadSource,
            InvolvedPeople = TagSource.None,
            Melodicness = TagSource.DownloadSource
        };

        var settings = converter.ToTagSettings(config, new TechnicalTagSettings());

        Assert.True(settings.Songwriter);
        Assert.True(settings.Producer);
        Assert.True(settings.MixingEngineer);
        Assert.True(settings.Melodicness);
        Assert.False(settings.InvolvedPeople);
        // Untouched roles stay off: no invented selections.
        Assert.False(settings.ExecutiveProducer);
        Assert.False(settings.Director);
    }
    [Fact]
    public async System.Threading.Tasks.Task ActualClientSelectionsRoundTripWithoutReenablingUncheckedRoles()
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var script = """
            const fs = require('node:fs');
            const vm = require('node:vm');
            const assert = require('node:assert/strict');
            const source = fs.readFileSync(process.argv[1], 'utf8');
            const boundary = source.indexOf('\n    document.addEventListener("change"');
            assert(boundary > 0, 'AutoTag startup boundary not found');
            const selected = {tags:['involvedPeople','engineer'],gapFillTags:['involvedPeople','engineer'],downloadTags:[],overwriteTags:['producer']};
            const document = {
                getElementById: () => null,
                querySelectorAll: selector => (selected[/name="([^"]+)"/.exec(selector)?.[1]] || []).map(tag => ({dataset:{tag}}))
            };
            const context = {document, console, structuredClone, setTimeout, clearTimeout};
            vm.createContext(context);
            vm.runInContext(source.slice(0,boundary) + '\n globalThis.creditTest = {state,DEFAULT_CONFIG,readTagSelectionsFromUi,isTagSupported};})();', context);
            const {state, DEFAULT_CONFIG, readTagSelectionsFromUi, isTagSupported} = context.creditTest;
            assert.equal(DEFAULT_CONFIG.creditControlsVersion,1,'New client config must identify role-aware selections');
            state.config = JSON.parse(JSON.stringify(DEFAULT_CONFIG));
            state.platforms = [{id:'itunes',supportedTags:['title']}];
            state.config.platforms = ['itunes'];
            assert(isTagSupported('producer'), 'Shared credits must not require selecting Shazam');
            assert(isTagSupported('melodicness'));
            assert(isTagSupported('energy'));
            assert(!isTagSupported('liveness'), 'Unsupported analyses stay disabled');
            readTagSelectionsFromUi();
            assert(state.config.tags.includes('engineer'));
            assert(!state.config.tags.includes('producer'));
            assert(state.config.overwriteTags.includes('producer'));
            process.stdout.write(JSON.stringify(state.config));
            """;
        var start = new System.Diagnostics.ProcessStartInfo("node")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add(script);
        start.ArgumentList.Add(System.IO.Path.Combine(directory!.FullName, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stderr);
        var saved = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await stdout)!;
        Assert.False(TaggingProfileCanonicalizer.MigrateLegacyCreditControls(saved));
        var roles = saved["tags"].EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("engineer", roles);
        Assert.DoesNotContain("producer", roles);
        Assert.Contains("producer", saved["overwriteTags"].EnumerateArray().Select(value => value.GetString()));
    }

}
