using TagLib;

namespace DeezSpoTag.Web.Services;

internal static class TagRawProbe
{
    public static bool HasId3Raw(TagLib.Id3v2.Tag tag, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name.Length == 4)
        {
            var frame = TagLib.Id3v2.TextInformationFrame.Get(tag, name, false);
            return frame?.Text != null && frame.Text.Any(t => !string.IsNullOrWhiteSpace(t));
        }

        var user = TagLib.Id3v2.UserTextInformationFrame.Get(tag, name, false);
        return user?.Text != null && user.Text.Any(t => !string.IsNullOrWhiteSpace(t));
    }

    public static bool HasVorbisRaw(TagLib.Ogg.XiphComment tag, string name)
    {
        return tag.GetField(name).Length > 0;
    }

    public static bool HasAppleDashBox(TagLib.Mpeg4.AppleTag tag, string name)
    {
        try
        {
            var tagType = tag.GetType();
            var methods = tagType.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            foreach (var method in methods)
            {
                if (!string.Equals(method.Name, "GetDashBox", StringComparison.Ordinal))
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != 2)
                {
                    continue;
                }

                var meanValue = "com.apple.iTunes";
                if (parameters[0].ParameterType == typeof(string) &&
                    parameters[1].ParameterType == typeof(string))
                {
                    var result = method.Invoke(tag, new object[] { meanValue, name });
                    if (result is string strResult)
                    {
                        return !string.IsNullOrWhiteSpace(strResult);
                    }

                    if (result is string[] arr)
                    {
                        return arr.Length > 0;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        return false;
    }
}
