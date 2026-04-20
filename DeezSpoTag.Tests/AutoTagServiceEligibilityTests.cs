using System;
using System.IO;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagServiceEligibilityTests
{
    private static MethodInfo ServiceMethod(string name)
    {
        return typeof(AutoTagService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"AutoTagService.{name} not found.");
    }

    private static T InvokeStatic<T>(string methodName, params object?[] args)
    {
        var result = ServiceMethod(methodName).Invoke(null, args);
        if (result == null)
        {
            return default!;
        }

        return (T)result;
    }

    [Fact]
    public void NormalizeRootPath_ReturnsNullForMissingDirectory()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"autotag-missing-{Guid.NewGuid():N}");
        var normalized = InvokeStatic<string?>("NormalizeRootPath", missing);
        Assert.Null(normalized);
    }

    [Fact]
    public void HasEligibleInputFiles_ReturnsTrueWhenConfigJsonCannotBeParsed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"autotag-config-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var result = InvokeStatic<bool>("HasEligibleInputFiles", root, "{not-json");
            Assert.True(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HasEligibleInputFiles_ReturnsTrueWhenTargetFilesContainSupportedInScopeAudioFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"autotag-targets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var candidate = Path.Combine(root, "track.flac");
            var outside = Path.Combine(Path.GetTempPath(), $"autotag-outside-{Guid.NewGuid():N}.flac");
            File.WriteAllText(candidate, "audio");
            File.WriteAllText(outside, "audio");

            var configJson = $$"""
                {
                  "targetFiles": [
                    "{{candidate.Replace("\\", "\\\\")}}",
                    "{{outside.Replace("\\", "\\\\")}}"
                  ]
                }
                """;

            var result = InvokeStatic<bool>("HasEligibleInputFiles", root, configJson);
            Assert.True(result);

            File.Delete(outside);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HasEligibleInputFiles_ReturnsFalseWhenNoSupportedFilesExist()
    {
        var root = Path.Combine(Path.GetTempPath(), $"autotag-no-audio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "notes.txt"), "not audio");
            var configJson = """{"includeSubfolders": true}""";

            var result = InvokeStatic<bool>("HasEligibleInputFiles", root, configJson);

            Assert.False(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
