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

                if (result is string[] arrayResult)
                {
                    return arrayResult.Where(static value => !string.IsNullOrWhiteSpace(value)).ToList();
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
        if (tag == null || values.Length == 0)
        {
            return false;
        }

        try
        {
            foreach (var method in EnumerateMatchingMethods(tag, "SetDashBox", 3))
            {
                var parameters = method.GetParameters();
                if (parameters[0].ParameterType != typeof(string)
                    || parameters[1].ParameterType != typeof(string))
                {
                    continue;
                }

                var valueToUse = values.Length == 1 ? values[0] : string.Join(", ", values);
                if (parameters[2].ParameterType == typeof(string))
                {
                    method.Invoke(tag, new object[] { MeanValue, name, valueToUse });
                    return true;
                }

                if (parameters[2].ParameterType == typeof(string[]))
                {
                    method.Invoke(tag, new object[] { MeanValue, name, values });
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

    private static IEnumerable<MethodInfo> EnumerateMatchingMethods(
        TagLib.Mpeg4.AppleTag tag,
        string methodName,
        int parameterCount)
    {
        return tag
            .GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method =>
                string.Equals(method.Name, methodName, StringComparison.Ordinal)
                && method.GetParameters().Length == parameterCount);
    }
}
