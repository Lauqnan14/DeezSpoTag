using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagFileOutcomeTests
{
    [Fact]
    public void NoEligibleTags_IsCompletedWithoutChangesForFinalLibraryMove()
    {
        var serviceType = typeof(AutoTagService);
        var outcomeType = serviceType.GetNestedType("FileTagOutcome", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("FileTagOutcome was not found.");
        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), outcomeType);
        var outcomes = Activator.CreateInstance(dictionaryType)
            ?? throw new InvalidOperationException("Outcome dictionary could not be created.");
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "already-complete.flac"));
        var status = new TaggingStatusWrap
        {
            Status = new TaggingStatus
            {
                Path = path,
                Status = "skipped",
                Outcome = "no_eligible_tags"
            }
        };

        InvokePrivateStaticVoid(serviceType, "TrackFileOutcome", outcomes, status);
        var result = InvokePrivateStatic(serviceType, "BuildMoveFileSets", outcomes);
        var taggedFiles = (IEnumerable)(result.GetType().GetField("Item1")?.GetValue(result)
            ?? throw new InvalidOperationException("TaggedFiles result was not found."));
        var failedFiles = (IEnumerable)(result.GetType().GetField("Item2")?.GetValue(result)
            ?? throw new InvalidOperationException("FailedFiles result was not found."));

        Assert.Contains(path, taggedFiles.Cast<string>(), StringComparer.OrdinalIgnoreCase);
        Assert.Empty(failedFiles.Cast<string>());
    }

    private static object InvokePrivateStatic(Type type, string name, params object[] arguments)
    {
        var method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{name} was not found.");
        return method.Invoke(null, arguments)
            ?? throw new InvalidOperationException($"{name} returned no result.");
    }

    private static void InvokePrivateStaticVoid(Type type, string name, params object[] arguments)
    {
        var method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{name} was not found.");
        method.Invoke(null, arguments);
    }
}
