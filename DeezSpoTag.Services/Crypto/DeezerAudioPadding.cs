using System.Text;

namespace DeezSpoTag.Services.Crypto;

internal static class DeezerAudioPadding
{
    internal static byte[] RemoveLeadingNullPadding(byte[] data)
    {
        if (data.Length < 8)
        {
            return data;
        }

        if (data[0] == 0)
        {
            var ftypCheck = Encoding.ASCII.GetString(data, 4, Math.Min(4, data.Length - 4));
            if (ftypCheck != "ftyp")
            {
                var firstNonZero = 0;
                for (int i = 0; i < data.Length; i++)
                {
                    if (data[i] != 0)
                    {
                        firstNonZero = i;
                        break;
                    }
                }

                if (firstNonZero > 0)
                {
                    var result = new byte[data.Length - firstNonZero];
                    Array.Copy(data, firstNonZero, result, 0, result.Length);
                    return result;
                }
            }
        }

        return data;
    }
}
