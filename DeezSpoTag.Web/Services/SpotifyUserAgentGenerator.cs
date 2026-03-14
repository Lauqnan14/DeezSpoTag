namespace DeezSpoTag.Web.Services;

public static class SpotifyUserAgentGenerator
{
    public static string BuildRandom(Random random, object syncRoot)
    {
        lock (syncRoot)
        {
            var macMajor = random.Next(11, 16);
            var macMinor = random.Next(4, 10);
            var webkitMajor = random.Next(530, 538);
            var webkitMinor = random.Next(30, 38);
            var chromeMajor = random.Next(80, 106);
            var chromeBuild = random.Next(3000, 4501);
            var chromePatch = random.Next(60, 126);
            var safariMajor = random.Next(530, 538);
            var safariMinor = random.Next(30, 37);

            return $"Mozilla/5.0 (Macintosh; Intel Mac OS X 10_{macMajor}_{macMinor}) " +
                   $"AppleWebKit/{webkitMajor}.{webkitMinor} (KHTML, like Gecko) " +
                   $"Chrome/{chromeMajor}.0.{chromeBuild}.{chromePatch} Safari/{safariMajor}.{safariMinor}";
        }
    }
}
