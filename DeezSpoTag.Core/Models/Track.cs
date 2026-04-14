using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Core.Enums;
using DeezSpoTag.Core.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;

namespace DeezSpoTag.Core.Models;

/// <summary>
/// Track model (ported from deezspotag Track.ts)
/// </summary>
public class Track : AudioFeaturesBase
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? MD5 { get; set; }
    public string? MediaVersion { get; set; }
    public string TrackToken { get; set; } = "";
    public int? TrackTokenExpiration { get; set; }
    public int TrackTokenExpire { get; set; }
    public int Duration { get; set; }
    public int FallbackID { get; set; }
    public int FallbackId { get; set; } // Make writable for compatibility
    public List<string> AlbumsFallback { get; set; } = new();
    public Dictionary<string, object> Filesizes { get; set; } = new();
    public Dictionary<string, int> FileSizes { get; set; } = new();
    public int Rank { get; set; }
    public bool Local { get; set; }
    public bool IsLocal => Local;
    
    // Artists
    public Artist? MainArtist { get; set; }
    public Dictionary<string, List<string>> Artist { get; set; } = new();
    public List<string> Artists { get; set; } = new();
    
    // Album and playlist
    public Album? Album { get; set; }
    public Playlist? Playlist { get; set; }
    
    // Track metadata
    public int TrackNumber { get; set; }
    public int DiscNumber { get; set; }
    public int DiskNumber { get; set; } // Make writable for compatibility
    public CustomDate Date { get; set; } = new();
    public Lyrics? Lyrics { get; set; }
    public double Bpm { get; set; }
    public int BPM => (int)Math.Round(Bpm); // Alias for compatibility
    public string? Key { get; set; }
    public Dictionary<string, object> Contributors { get; set; } = new();
    public string Copyright { get; set; } = "";
    public bool Explicit { get; set; }
    public string ISRC { get; set; } = "";
    public string ReplayGain { get; set; } = "";
    public double Gain { get; set; }
    public string? LyricsId { get; set; }
    public string? PhysicalReleaseDate { get; set; }
    public int? Position { get; set; }
    public bool Searched { get; set; }
    public string? Source { get; set; }
    public string? SourceId { get; set; }

    // Qobuz metadata
    public string? QobuzId { get; set; }
    public string? QobuzAlbumId { get; set; }
    public string? QobuzArtistId { get; set; }
    public QobuzQualityInfo? QobuzQuality { get; set; }
    
    // Download related
    public int Bitrate { get; set; }
    public Dictionary<string, string> Urls { get; set; } = new();
    public string DownloadURL { get; set; } = "";
    public string DownloadUrl => DownloadURL;
    
    // Generated strings
    public string DateString { get; set; } = "";
    public string ArtistString { get; set; } = ""; // Singular - used for default separator
    public string ArtistsString { get; set; } = ""; // Plural - used for custom separators
    public string MainArtistsString { get; set; } = "";
    public string FeatArtistsString { get; set; } = "";
    public string FullArtistsString { get; set; } = "";
    
    public Track()
    {
        Artist["Main"] = new List<string>();
    }

    /// <summary>
    /// Parse essential track data from enriched API track
    /// </summary>
    public void ParseEssentialData(EnrichedApiTrack trackApi)
    {
    Id = trackApi.Id.ToString();
    Duration = trackApi.Duration;
    TrackToken = trackApi.TrackToken;
    TrackTokenExpiration = trackApi.TrackTokenExpire;
    TrackTokenExpire = trackApi.TrackTokenExpire;
    MD5 = trackApi.Md5Origin;
    MediaVersion = trackApi.MediaVersion.ToString();
    Filesizes = trackApi.Filesizes ?? new Dictionary<string, object>();
    
    // CRITICAL FIX: Convert Filesizes to FileSizes for compatibility
    FileSizes = new Dictionary<string, int>();
    foreach (var kvp in Filesizes)
    {
    if (kvp.Value == null)
    {
    continue;
    }

    var normalizedKey = kvp.Key?.ToLowerInvariant();
    if (string.IsNullOrEmpty(normalizedKey))
    {
    continue;
    }

    if (kvp.Value is long longSize && longSize <= int.MaxValue)
    {
    FileSizes[normalizedKey] = (int)longSize;
    }
    else if (int.TryParse(kvp.Value.ToString(), out var size))
    {
    FileSizes[normalizedKey] = size;
    }
    }
    
    FallbackID = trackApi.FallbackId ?? 0;
    FallbackId = FallbackID;
    Local = long.TryParse(trackApi.Id, out long idVal) && idVal < 0;
    Urls = new Dictionary<string, string>();
    }

    
    /// <summary>
    /// Parse track data from API track
    /// </summary>
    public void ParseTrack(ApiTrack trackApi)
    {
        ApplyTrackBasics(trackApi);
        ApplyReplayGain(trackApi.Gain);
        Lyrics = new Lyrics(trackApi.LyricsId ?? "0");
        MainArtist = new Artist(trackApi.Artist.Id.ToString(), trackApi.Artist.Name ?? "", "Main", trackApi.Artist.Md5Image);
        ApplyTrackReleaseDate(trackApi);
        ParseTrackContributors(trackApi);
        ParseAlternativeAlbums(trackApi);
        EnsureTrackArtistsInitialized();
        GenerateMainFeatStrings();
    }

    /// <summary>
    /// Apply settings to track (ported from applySettings method)
    /// </summary>
    public void ApplySettings(DeezSpoTagSettings settings)
    {
        ApplyPlaylistCompilationSettings(settings);
        ApplyDateFormatting(settings);
        ApplyGenreTagNormalization(settings);
        ApplyVariousArtists(settings);
        ApplyAlbumMainArtistSaveFlag(settings);
        ApplyArtistDeduplication(settings);
        ApplyFeaturedTitleMode(settings);
        RemoveAlbumVersionSuffix(settings);
        ApplyTrackCasing(settings);
        ApplyArtistTagFormatting(settings);
    }

    private void ApplyTrackBasics(ApiTrack trackApi)
    {
        Title = trackApi.Title;
        DiscNumber = trackApi.DiskNumber;
        Explicit = trackApi.ExplicitLyrics;
        Copyright = trackApi.Copyright ?? "";
        ISRC = trackApi.Isrc;
        TrackNumber = trackApi.TrackPosition;
        Contributors = new Dictionary<string, object>();
        Bpm = trackApi.Bpm;
        Gain = trackApi.Gain;
        Rank = trackApi.Rank;
    }

    private void ApplyReplayGain(double gain)
    {
        if (Math.Abs(gain) > double.Epsilon)
        {
            ReplayGain = GenerateReplayGainString(gain);
        }
    }

    private void ApplyTrackReleaseDate(ApiTrack trackApi)
    {
        var releaseSource = !string.IsNullOrEmpty(trackApi.PhysicalReleaseDate)
            ? trackApi.PhysicalReleaseDate
            : trackApi.ReleaseDate;
        if (!string.IsNullOrEmpty(releaseSource)
            && DateTime.TryParse(releaseSource, CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseDate))
        {
            Date.Day = releaseDate.Day.ToString("D2");
            Date.Month = releaseDate.Month.ToString("D2");
            Date.Year = releaseDate.Year.ToString();
            Date.FixDayMonth();
        }
    }

    private void ParseTrackContributors(ApiTrack trackApi)
    {
        if (trackApi.Contributors == null)
        {
            return;
        }

        foreach (var contributor in trackApi.Contributors)
        {
            if (!Artists.Contains(contributor.Name))
            {
                Artists.Add(contributor.Name);
            }

            AddTrackContributorToRole(contributor.Role, contributor.Name);
        }
    }

    private void AddTrackContributorToRole(string? role, string contributorName)
    {
        if (role != "Main" && Artist["Main"].Contains(contributorName))
        {
            return;
        }

        var normalizedRole = role ?? "Main";
        if (!Artist.TryGetValue(normalizedRole, out _))
        {
            Artist[normalizedRole] = new List<string>();
        }

        Artist[normalizedRole].Add(contributorName);
    }

    private void ParseAlternativeAlbums(ApiTrack trackApi)
    {
        if (trackApi.AlternativeAlbums?.Data == null)
        {
            return;
        }

        foreach (var album in trackApi.AlternativeAlbums.Data)
        {
            AlbumsFallback.Add(album.Id.ToString());
        }
    }

    private void EnsureTrackArtistsInitialized()
    {
        if (Artist["Main"].Count == 0)
        {
            Artist["Main"] = new List<string> { MainArtist?.Name ?? "Unknown Artist" };
        }

        if (Artists.Count == 0 && MainArtist != null)
        {
            Artists.Add(MainArtist.Name);
        }

        foreach (var artistRole in Artist.Values)
        {
            foreach (var artistName in artistRole.Where(artistName => !Artists.Contains(artistName)))
            {
                Artists.Add(artistName);
            }
        }
    }

    private void ApplyPlaylistCompilationSettings(DeezSpoTagSettings settings)
    {
        if (settings.Tags.SavePlaylistAsCompilation && Playlist != null)
        {
            TrackNumber = Position ?? 0;
            DiscNumber = 1;
            Album?.MakePlaylistCompilation(Playlist);
        }
        else if (Album?.Date != null)
        {
            Date = Album.Date;
        }
    }

    private void ApplyDateFormatting(DeezSpoTagSettings settings)
    {
        DateString = Date.Format(settings.DateFormat);
        if (Album != null)
        {
            Album.DateString = Album.Date.Format(settings.DateFormat);
        }

        if (Playlist != null)
        {
            Playlist.DateString = Playlist.Date.Format(settings.DateFormat);
        }
    }

    private void ApplyVariousArtists(DeezSpoTagSettings settings)
    {
        if (!settings.AlbumVariousArtists || Album?.VariousArtists == null)
        {
            return;
        }

        var artist = Album.VariousArtists;
        if (!Album.Artists.Contains(artist.Name))
        {
            Album.Artists.Add(artist.Name);
        }

        var role = artist.Role ?? "Main";
        if (role != "Main" && Album.Artist["Main"].Contains(artist.Name))
        {
            return;
        }

        if (!Album.Artist.TryGetValue(role, out _))
        {
            Album.Artist[role] = new List<string>();
        }

        Album.Artist[role].Add(artist.Name);
    }

    private void ApplyAlbumMainArtistSaveFlag(DeezSpoTagSettings settings)
    {
        if (Album?.MainArtist != null)
        {
            Album.MainArtist.Save = !Album.MainArtist.IsVariousArtists()
                || (settings.AlbumVariousArtists && Album.MainArtist.IsVariousArtists());
        }
    }

    private void ApplyArtistDeduplication(DeezSpoTagSettings settings)
    {
        if (!settings.RemoveDuplicateArtists)
        {
            return;
        }

        RemoveDuplicateArtists();
        GenerateMainFeatStrings();
    }

    private void ApplyFeaturedTitleMode(DeezSpoTagSettings settings)
    {
        switch (settings.FeaturedToTitle)
        {
            case "0":
                Title = GetCleanTitle();
                if (Album != null)
                {
                    Album.Title = Album.GetCleanTitle();
                }
                break;
            case "1":
                Title = GetCleanTitle();
                break;
            case "2":
                Title = GetFeatTitle();
                break;
            case "3":
                Title = GetCleanTitle();
                if (Album != null)
                {
                    Album.Title = Album.GetCleanTitle();
                }
                break;
        }
    }

    private void RemoveAlbumVersionSuffix(DeezSpoTagSettings settings)
    {
        if (!settings.RemoveAlbumVersion || !Title.Contains("Album Version", StringComparison.Ordinal))
        {
            return;
        }

        Title = System.Text.RegularExpressions.Regex.Replace(
            Title,
            @" ?\(Album Version\)",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            RegexTimeout).Trim();
    }

    private void ApplyTrackCasing(DeezSpoTagSettings settings)
    {
        if (settings.TitleCasing != "nothing")
        {
            Title = ChangeCase(Title, settings.TitleCasing);
        }

        if (settings.ArtistCasing == "nothing")
        {
            return;
        }

        if (MainArtist != null)
        {
            MainArtist.Name = ChangeCase(MainArtist.Name, settings.ArtistCasing);
        }

        for (int i = 0; i < Artists.Count; i++)
        {
            Artists[i] = ChangeCase(Artists[i], settings.ArtistCasing);
        }

        foreach (var artType in Artist.Keys.ToList())
        {
            for (int i = 0; i < Artist[artType].Count; i++)
            {
                Artist[artType][i] = ChangeCase(Artist[artType][i], settings.ArtistCasing);
            }
        }

        GenerateMainFeatStrings();
    }

    private void ApplyArtistTagFormatting(DeezSpoTagSettings settings)
    {
        if (settings.Tags.MultiArtistSeparator == "default")
        {
            ApplyDefaultArtistSeparator(settings);
            return;
        }

        if (settings.Tags.MultiArtistSeparator == "andFeat")
        {
            ApplyAndFeatArtistSeparator(settings);
            return;
        }

        var separator = settings.Tags.MultiArtistSeparator;
        ArtistsString = settings.FeaturedToTitle == "2"
            ? string.Join(separator, Artist.GetValueOrDefault("Main", new List<string>()))
            : string.Join(separator, Artists);
    }

    private void ApplyDefaultArtistSeparator(DeezSpoTagSettings settings)
    {
        if (settings.FeaturedToTitle == "2")
        {
            ArtistsString = string.Join(", ", Artist.GetValueOrDefault("Main", new List<string>()));
            return;
        }

        ArtistString = string.Join(", ", Artists);
        ArtistsString = ArtistString;
    }

    private void ApplyAndFeatArtistSeparator(DeezSpoTagSettings settings)
    {
        ArtistsString = MainArtistsString;
        if (!string.IsNullOrEmpty(FeatArtistsString) && settings.FeaturedToTitle != "2")
        {
            ArtistsString += $" {FeatArtistsString}";
        }
    }

    private void ApplyGenreTagNormalization(DeezSpoTagSettings settings)
    {
        if (Album?.Genre == null || Album.Genre.Count == 0)
        {
            return;
        }

        var aliasMap = settings.NormalizeGenreTags
            ? GenreTagAliasNormalizer.BuildAliasMap(settings.GenreTagAliasRules)
            : null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();

        foreach (var value in GenreTagAliasNormalizer.NormalizeAndExpandValues(
                     Album.Genre,
                     aliasMap,
                     settings.NormalizeGenreTags))
        {
            if (value.Equals("other", StringComparison.OrdinalIgnoreCase)
                || value.Equals("others", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(value))
            {
                normalized.Add(value);
            }
        }

        Album.Genre = normalized;
    }

    /// <summary>
    /// Generate main and featured artist strings
    /// </summary>
    public void GenerateMainFeatStrings()
    {
        MainArtistsString = string.Join(", ", Artist.GetValueOrDefault("Main", new List<string>()));
        FullArtistsString = MainArtistsString;
        FeatArtistsString = "";
        
        if (Artist.TryGetValue("Featured", out var featuredArtists) && featuredArtists.Count > 0)
        {
            FeatArtistsString = $"feat. {string.Join(", ", featuredArtists)}";
            FullArtistsString += $" {FeatArtistsString}";
        }
    }

    /// <summary>
    /// Get clean title without features
    /// </summary>
    public string GetCleanTitle()
    {
        return RemoveFeatures(Title);
    }

    /// <summary>
    /// Get title with featured artists
    /// </summary>
    public string GetFeatTitle()
    {
        if (!string.IsNullOrEmpty(FeatArtistsString) && !Title.Contains("feat.", StringComparison.OrdinalIgnoreCase))
        {
            return $"{Title} ({FeatArtistsString})";
        }
        return Title;
    }

    /// <summary>
    /// Remove duplicate artists
    /// </summary>
    public void RemoveDuplicateArtists()
    {
        // Simplified version - remove duplicates from artists list
        Artists = Artists.Distinct().ToList();
        
        // Remove duplicates from artist roles
        foreach (var role in Artist.Keys.ToList())
        {
            Artist[role] = Artist[role].Distinct().ToList();
        }
    }

    // Helper methods (simplified versions)
    private static string GenerateReplayGainString(double gain)
    {
        return $"{gain:F2} dB";
    }

    private static string RemoveFeatures(string title)
    {
        // Remove trailing collaboration suffixes from title variants.
        var patterns = new[]
        {
            @"\s*\((feat|ft|featuring)\.?\s+.*?\)",
            @"\s*\[(feat|ft|featuring)\.?\s+.*?\]",
            @"\s*(feat|ft|featuring)\.?\s+.*$"
        };
        foreach (var pattern in patterns)
        {
            title = System.Text.RegularExpressions.Regex.Replace(
                title,
                pattern,
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                RegexTimeout);
        }
        return title.Trim();
    }

    private static string ChangeCase(string text, string casing)
    {
        return casing.ToLower() switch
        {
            "lower" => text.ToLower(),
            "upper" => text.ToUpper(),
            "title" => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLower()),
            "sentence" => char.ToUpper(text[0]) + text[1..].ToLower(),
            _ => text
        };
    }

    /// <summary>
    /// EXACT PORT from deezspotag Track.ts parseData method
    /// </summary>
    public async Task ParseData(dynamic dz, string id, ApiTrack existingTrack, ApiAlbum? albumAPI, ApiPlaylist? playlistAPI, bool refetch = true)
    {
        existingTrack = await HydrateTrackDataAsync(dz, id, existingTrack, refetch);
        existingTrack = await RefreshTrackBpmAsync(dz, existingTrack);
        ParseResolvedTrack(existingTrack);
        await PopulateAlbumDataAsync(dz, existingTrack, albumAPI);
        Title = System.Text.RegularExpressions.Regex.Replace(Title, @"\s\s+", " ", System.Text.RegularExpressions.RegexOptions.None, RegexTimeout);
        if (Artist["Main"].Count == 0)
        {
            Artist["Main"] = new List<string> { MainArtist?.Name ?? "" };
        }
        Position = existingTrack?.Position ?? Position;
        if (playlistAPI != null && (playlistAPI.Id != 0 || !string.IsNullOrWhiteSpace(playlistAPI.Title)))
        {
            Playlist = new Playlist(JsonConvert.DeserializeObject<Dictionary<string, object>>(JsonConvert.SerializeObject(playlistAPI)) ?? new Dictionary<string, object>());
        }
        GenerateMainFeatStrings();
    }

    private async Task<ApiTrack> HydrateTrackDataAsync(dynamic dz, string id, ApiTrack? existingTrack, bool refetch)
    {
        if (string.IsNullOrEmpty(id) || !refetch)
        {
            return existingTrack ?? throw new InvalidOperationException("No data to parse.");
        }

        var refreshedTrack = BuildEnrichedTrack(await dz.Gw.GetTrackWithFallbackAsync(id));
        ParseEssentialData(refreshedTrack);
        return existingTrack == null ? refreshedTrack : MergeExistingTrack(existingTrack, refreshedTrack);
    }

    private static EnrichedApiTrack BuildEnrichedTrack(dynamic gwTrack)
    {
        var track = new EnrichedApiTrack
        {
            Id = gwTrack.SngId.ToString(),
            Duration = gwTrack.Duration,
            TrackToken = gwTrack.TrackToken ?? "",
            TrackTokenExpire = gwTrack.TrackTokenExpire,
            Md5Origin = ((object?)gwTrack.Md5Origin)?.ToString() ?? "0",
            MediaVersion = gwTrack.MediaVersion,
            FallbackId = gwTrack.FallbackId,
            Filesizes = new Dictionary<string, object>()
        };

        AddFileSize(track, "MP3_128", gwTrack.FilesizeMp3128);
        AddFileSize(track, "MP3_320", gwTrack.FilesizeMp3320);
        AddFileSize(track, "FLAC", gwTrack.FilesizeFlac);
        AddFileSize(track, "MP4_RA1", gwTrack.FilesizeMp4Ra1);
        AddFileSize(track, "MP4_RA2", gwTrack.FilesizeMp4Ra2);
        AddFileSize(track, "MP4_RA3", gwTrack.FilesizeMp4Ra3);
        return track;
    }

    private static void AddFileSize(EnrichedApiTrack track, string key, long? size)
    {
        if (size != null && size > 0)
        {
            track.Filesizes[key] = size;
        }
    }

    private static EnrichedApiTrack MergeExistingTrack(ApiTrack existingTrack, EnrichedApiTrack refreshedTrack)
    {
        var mergedTrack = existingTrack as EnrichedApiTrack ?? new EnrichedApiTrack
        {
            Id = existingTrack.Id,
            Title = existingTrack.Title,
            Duration = existingTrack.Duration,
            TrackToken = existingTrack.TrackToken,
            Artist = existingTrack.Artist,
            Album = existingTrack.Album,
            Bpm = existingTrack.Bpm,
            Gain = existingTrack.Gain,
            ExplicitLyrics = existingTrack.ExplicitLyrics,
            Copyright = existingTrack.Copyright,
            Isrc = existingTrack.Isrc,
            TrackPosition = existingTrack.TrackPosition,
            DiskNumber = existingTrack.DiskNumber,
            LyricsId = existingTrack.LyricsId,
            PhysicalReleaseDate = existingTrack.PhysicalReleaseDate,
            Contributors = existingTrack.Contributors,
            AlternativeAlbums = existingTrack.AlternativeAlbums,
            Position = existingTrack.Position,
            ReleaseDate = existingTrack.ReleaseDate,
            Genres = existingTrack.Genres,
            Lyrics = existingTrack.Lyrics,
            Md5Image = existingTrack.Md5Image
        };

        mergedTrack.TrackToken = refreshedTrack.TrackToken;
        mergedTrack.TrackTokenExpire = refreshedTrack.TrackTokenExpire;
        mergedTrack.Md5Origin = refreshedTrack.Md5Origin;
        mergedTrack.MediaVersion = refreshedTrack.MediaVersion;
        mergedTrack.Filesizes = refreshedTrack.Filesizes;
        mergedTrack.FallbackId = refreshedTrack.FallbackId;
        return mergedTrack;
    }

    private async Task<ApiTrack> RefreshTrackBpmAsync(dynamic dz, ApiTrack existingTrack)
    {
        if (Math.Abs(existingTrack.Bpm) > double.Epsilon || Local)
        {
            return existingTrack;
        }

        try
        {
            var trackApiNewObj = await dz.Api.GetTrackAsync(existingTrack.Id.ToString());
            if (trackApiNewObj is ApiTrack trackApiNew)
            {
                trackApiNew.ReleaseDate = existingTrack.ReleaseDate;
                return trackApiNew;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        return existingTrack;
    }

    private void ParseResolvedTrack(ApiTrack existingTrack)
    {
        if (Local)
        {
            ParseLocalTrackData(existingTrack);
        }
        else
        {
            ParseTrack(existingTrack);
        }
    }

    private async Task PopulateAlbumDataAsync(dynamic dz, ApiTrack existingTrack, ApiAlbum? albumAPI)
    {
        if (Local)
        {
            return;
        }

        await PopulateLyricsAsync(dz, existingTrack);
        InitializeAlbum(existingTrack);
        var resolvedAlbumApi = await ResolveAlbumApiAsync(dz, albumAPI);
        Album!.ParseAlbum(resolvedAlbumApi);
        ApplyAlbumFallbacks(existingTrack);
        await PopulateAlbumArtistImageAsync(dz);
        MergeAlbumGenres(existingTrack);
    }

    private async Task PopulateLyricsAsync(dynamic dz, ApiTrack existingTrack)
    {
        if (existingTrack.Lyrics == null && Lyrics?.Id != "0")
        {
            try
            {
                var lyricsPayload = await dz.Gw.GetLyricsAsync(Id);
                if (lyricsPayload != null)
                {
                    existingTrack.Lyrics = JsonConvert.SerializeObject(lyricsPayload);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (Lyrics != null)
                {
                    Lyrics.Id = "0";
                }
            }
        }

        if (Lyrics?.Id != "0" && Lyrics != null)
        {
            Lyrics.ParseLyrics(ParseLyricsPayload(existingTrack.Lyrics));
        }
    }

    private void InitializeAlbum(ApiTrack existingTrack)
    {
        var sourceAlbum = existingTrack.Album;
        Album = new Album(
            sourceAlbum != null ? sourceAlbum.Id.ToString() : "0",
            sourceAlbum?.Title ?? string.Empty,
            sourceAlbum?.Md5Image ?? string.Empty);
    }

    private async Task<ApiAlbum> ResolveAlbumApiAsync(dynamic dz, ApiAlbum? albumAPI)
    {
        if (albumAPI == null)
        {
            try
            {
                albumAPI = await dz.Api.GetAlbumAsync(Album!.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                albumAPI = new ApiAlbum();
            }
        }

        if (albumAPI == null || albumAPI.NbDisk == null)
        {
            GwAlbumPageResponse albumApiGw;
            try
            {
                albumApiGw = await dz.Gw.GetAlbumPageAsync(Album!.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                albumApiGw = new GwAlbumPageResponse();
            }

            albumAPI ??= new ApiAlbum();
            albumAPI = JsonConvert.DeserializeObject<ApiAlbum>(JsonConvert.SerializeObject(albumApiGw)) ?? albumAPI;
        }

        return albumAPI ?? throw new InvalidOperationException("Album data is unavailable.");
    }

    private void ApplyAlbumFallbacks(ApiTrack existingTrack)
    {
        if ((Album!.MainArtist == null
             || string.IsNullOrWhiteSpace(Album.MainArtist.Name)
             || Album.MainArtist.Name == "Unknown"
             || Album.MainArtist.Name == "Unknown Artist")
            && MainArtist != null
            && !string.IsNullOrWhiteSpace(MainArtist.Name))
        {
            Album.MainArtist = MainArtist;
        }

        Album.Pic ??= new Picture("", "cover");
        if (string.IsNullOrEmpty(Album.Pic.Md5))
        {
            Album.Pic.Md5 = existingTrack.Album?.Md5Image ?? existingTrack.Md5Image ?? string.Empty;
        }

        if (string.IsNullOrEmpty(Album.Pic.Type))
        {
            Album.Pic.Type = "cover";
        }

        if (Album.Date != null && Date == null)
        {
            Date = Album.Date;
        }
    }

    private async Task PopulateAlbumArtistImageAsync(dynamic dz)
    {
        if (!string.IsNullOrEmpty(Album?.MainArtist?.Pic?.Md5) || Album?.MainArtist == null)
        {
            EnsureAlbumArtistPictureType();
            return;
        }

        try
        {
            var artistApi = await dz.Api.GetArtistAsync(Album.MainArtist.Id.ToString());
            Album.MainArtist.Pic.Md5 = !string.IsNullOrEmpty(artistApi.Md5Image)
                ? artistApi.Md5Image
                : ExtractArtistPictureMd5(artistApi.PictureSmall);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        EnsureAlbumArtistPictureType();
    }

    private void EnsureAlbumArtistPictureType()
    {
        if (Album?.MainArtist?.Pic != null
            && !string.IsNullOrEmpty(Album.MainArtist.Pic.Md5)
            && string.IsNullOrEmpty(Album.MainArtist.Pic.Type))
        {
            Album.MainArtist.Pic.Type = "artist";
        }
    }

    private static string ExtractArtistPictureMd5(string? pictureSmall)
    {
        var picture = pictureSmall ?? string.Empty;
        var artistIndex = picture.IndexOf("artist/", StringComparison.Ordinal);
        return artistIndex >= 0 && picture.Length >= artistIndex + 31
            ? picture.Substring(artistIndex + 7, picture.Length - (artistIndex + 7) - 24)
            : string.Empty;
    }

    private void MergeAlbumGenres(ApiTrack existingTrack)
    {
        if (existingTrack.Genres == null || Album == null)
        {
            return;
        }

        foreach (var genre in existingTrack.Genres.Where(genre => !Album.Genre.Contains(genre)))
        {
            Album.Genre.Add(genre);
        }
    }

    private static Dictionary<string, object>? ParseLyricsPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            var token = JToken.Parse(payload);
            if (token is JObject obj)
            {
                return obj.ToObject<Dictionary<string, object>>();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ignore malformed payloads and fallback to no parsed lyrics.
        }

        return null;
    }

    /// <summary>
    /// EXACT PORT of parseLocalTrackData from deezspotag Track.ts
    /// </summary>
    private void ParseLocalTrackData(ApiTrack trackAPI)
    {
        Title = trackAPI.Title;
        Album = new Album("0", trackAPI.Album?.Title ?? "", "");
        Album.Pic = new Picture(trackAPI.Md5Image ?? "", "cover");
        MainArtist = new Artist(0, trackAPI.Artist?.Name ?? "", "Main");
        Artists = new List<string> { trackAPI.Artist?.Name ?? "" };
        Artist = new Dictionary<string, List<string>>
        {
            ["Main"] = new List<string> { trackAPI.Artist?.Name ?? "" }
        };
        Album.Artist = Artist;
        Album.Artists = Artists;
        Album.Date = Date;
        Album.MainArtist = MainArtist;
    }

    /// <summary>
    /// EXACT PORT of checkAndRenewTrackToken from deezspotag Track.ts
    /// </summary>
    public async Task CheckAndRenewTrackToken(dynamic dz)
    {
        var now = DateTime.Now;
        var expiration = DateTimeOffset.FromUnixTimeSeconds(TrackTokenExpiration ?? 0).DateTime;
        if (now > expiration)
        {
            var newTrack = await dz.Gw.GetTrackWithFallbackAsync(Id);
            TrackToken = newTrack.TrackToken;
            TrackTokenExpiration = newTrack.TrackTokenExpire;
        }
    }
}
