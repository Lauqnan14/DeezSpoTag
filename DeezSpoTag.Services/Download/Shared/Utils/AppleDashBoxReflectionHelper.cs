using System.Reflection;

namespace DeezSpoTag.Services.Download.Shared.Utils;

public static class AppleDashBoxReflectionHelper
{
    private const string MeanValue = "com.apple.iTunes";

    public static List<string> ReadValues(TagLib.Mpeg4.AppleTag? tag, string name)
    {
        if (tag == null)
        {
            return new List<string>();
        }

        try
        {
            foreach (var method in EnumerateMatchingMethods(tag, "GetDashBoxes", 2))
            {
                var parameters = method.GetParameters();
                if (parameters[0].ParameterType != typeof(string)
                    || parameters[1].ParameterType != typeof(string))
                {
                    continue;
                }

                var result = method.Invoke(tag, new object[] { MeanValue, name });
                if (result is string[] arrayResult)
                {
                    return arrayResult.Where(static value => !string.IsNullOrWhiteSpace(value)).ToList();
                }

                if (result is IEnumerable<string> enumerableResult)
                {
                    return enumerableResult.Where(static value => !string.IsNullOrWhiteSpace(value)).ToList();
                }
            }

            foreach (var method in EnumerateMatchingMethods(tag, "GetDashBox", 2))
            {
                var parameters = method.GetParameters();
                if (parameters[0].ParameterType != typeof(string)
                    || parameters[1].ParameterType != typeof(string))
                {
                    continue;
                }

                var result = method.Invoke(tag, new object[] { MeanValue, name });
                if (result is string stringResult)
                {
                    return string.IsNullOrWhiteSpace(stringResult)
                        ? new List<string>()
                        : new List<string> { stringResult };
                }
            }
        }
        catch
        {
            return new List<string>();
        }

        return new List<string>();
    }

    public static bool TrySetValues(TagLib.Mpeg4.AppleTag? tag, string name, string[] values)
    {
        if (tag == null || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalizedValues = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedValues.Length == 0)
        {
            return false;
        }

        try
        {
            // Some TagLib versions append dash-box values instead of replacing them.
            // Clearing first keeps MP4 tags stable across repeated enrichment runs.
            TryClearValues(tag, name);

            foreach (var method in EnumerateMatchingMethods(tag, "SetDashBoxes", 3))
            {
                var parameters = method.GetParameters();
                if (parameters[0].ParameterType != typeof(string)
                    || parameters[1].ParameterType != typeof(string))
                {
                    continue;
                }

                if (!TryBuildThirdArgument(parameters[2].ParameterType, normalizedValues, out var argument))
                {
                    continue;
                }

                method.Invoke(tag, new[] { MeanValue, name, argument });
                return true;
            }

            foreach (var method in EnumerateMatchingMethods(tag, "SetDashBox", 3))
            {
                var parameters = method.GetParameters();
                if (parameters[0].ParameterType != typeof(string)
                    || parameters[1].ParameterType != typeof(string))
                {
                    continue;
                }

                var valueToUse = normalizedValues[0];
                if (parameters[2].ParameterType == typeof(string))
                {
                    method.Invoke(tag, new object[] { MeanValue, name, valueToUse });
                    return true;
                }

                if (TryBuildThirdArgument(parameters[2].ParameterType, normalizedValues, out var argument))
                {
                    method.Invoke(tag, new[] { MeanValue, name, argument });
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static void TryClearValues(TagLib.Mpeg4.AppleTag tag, string name)
    {
        try
        {
            foreach (var method in EnumerateMatchingMethods(tag, "SetDashBoxes", 3))
            {
                var parameters = method.GetParameters();
                if (parameters[0].ParameterType != typeof(string)
                    || parameters[1].ParameterType != typeof(string))
                {
                    continue;
                }

                if (!TryBuildThirdArgument(parameters[2].ParameterType, Array.Empty<string>(), out var argument))
                {
                    continue;
                }

                method.Invoke(tag, new[] { MeanValue, name, argument });
            }

            foreach (var method in EnumerateMatchingMethods(tag, "SetDashBox", 3))
            {
                var parameters = method.GetParameters();
                if (parameters[0].ParameterType != typeof(string)
                    || parameters[1].ParameterType != typeof(string))
                {
                    continue;
                }

                if (parameters[2].ParameterType == typeof(string))
                {
                    method.Invoke(tag, new object[] { MeanValue, name, string.Empty });
                    continue;
                }

                if (TryBuildThirdArgument(parameters[2].ParameterType, Array.Empty<string>(), out var argument))
                {
                    method.Invoke(tag, new[] { MeanValue, name, argument });
                }
            }
        }
        catch
        {
            // best effort only
        }
    }

    private static IEnumerable<MethodInfo> EnumerateMatchingMethods(
        TagLib.Mpeg4.AppleTag tag,
        string methodName,
        int parameterCount)
    {
        return tag
            .GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method =>
                string.Equals(method.Name, methodName, StringComparison.Ordinal)
                && method.GetParameters().Length == parameterCount);
    }

    private static bool TryBuildThirdArgument(Type parameterType, string[] values, out object argument)
    {
        if (parameterType == typeof(string[]))
        {
            argument = values;
            return true;
        }

        if (parameterType == typeof(string))
        {
            argument = values.FirstOrDefault() ?? string.Empty;
            return true;
        }

        if (parameterType.IsAssignableFrom(typeof(List<string>)))
        {
            argument = values.ToList();
            return true;
        }

        if (parameterType.IsAssignableFrom(typeof(IEnumerable<string>)))
        {
            argument = values;
            return true;
        }

        argument = string.Empty;
        return false;
    }
}
