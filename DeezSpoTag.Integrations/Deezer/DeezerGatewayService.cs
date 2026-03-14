using DeezSpoTag.Core.Models.Deezer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Text;

namespace DeezSpoTag.Integrations.Deezer;

/// <summary>
/// Deezer Gateway service using centralized session management
/// </summary>
public sealed class DeezerGatewayService : IDisposable
{
    private const string SessionManagerNotSetMessage = "Session manager not set";
    private static readonly string[] HomeGridItemTypes =
    {
        "album",
        "artist",
        "channel",
        "flow",
        "playlist",
        "radio",
        "show",
        "smarttracklist",
        "track",
        "user"
    };

    private DeezerSessionManager? _sessionManager;
    private DeezerClient? _clientProxy;
    private bool _disposed;

    public DeezerGatewayService(ILogger<DeezerGatewayService> logger)
    {
    }

    public void SetSessionManager(DeezerSessionManager sessionManager)
    {
        _sessionManager = sessionManager;
        _clientProxy = new DeezerClient(NullLogger<DeezerClient>.Instance, sessionManager);
    }

    private DeezerSessionManager RequireSessionManager()
    {
        return _sessionManager ?? throw new InvalidOperationException(SessionManagerNotSetMessage);
    }

    private DeezerClient RequireClientProxy()
    {
        return _clientProxy ?? throw new InvalidOperationException(SessionManagerNotSetMessage);
    }

    private async Task<T> ApiCallAsync<T>(string method, object? args = null, Dictionary<string, object>? parameters = null) where T : class
    {
        return await RequireSessionManager().GatewayApiCallAsync<T>(method, args, parameters);
    }

    private string GetLanguage(string fallback = "en")
    {
        return _sessionManager?.CurrentUser?.Language ?? fallback;
    }

    private string GetCountry(string fallback = "US")
    {
        return _sessionManager?.CurrentUser?.Country ?? fallback;
    }

    // -----===== Core Methods =====-----

    public async Task<DeezerUserData> GetUserDataAsync()
    {
        return await RequireClientProxy().GetUserDataAsync();
    }

    /// <summary>
    /// Get child accounts for family accounts
    /// Ported from: /deezspotag/deezer-sdk/src/gw.ts get_child_accounts method
    /// </summary>
    public async Task<List<GwChildAccount>> GetChildAccountsAsync()
    {
        return await RequireClientProxy().GetChildAccountsAsync();
    }

    // -----===== Tracks =====-----

    public Task<GwTrack> GetTrackAsync(string trackId) => RequireClientProxy().GetGwTrackAsync(trackId);

    public async Task<Dictionary<string, object>?> GetTrackAsync(string trackId, CancellationToken cancellationToken)
    {
        return await RequireClientProxy().GetTrackAsync(trackId, cancellationToken);
    }

    public async Task<GwTrack> GetTrackWithFallbackAsync(string trackId)
    {
        return await RequireClientProxy().GetTrackWithFallbackAsync(trackId);
    }

    public async Task<List<GwTrack>> GetTracksAsync(List<string> trackIds)
    {
        return await RequireClientProxy().GetTracksAsync(trackIds);
    }

    // -----===== Albums =====-----

    public Task<GwAlbum> GetAlbumAsync(string albumId) => RequireClientProxy().GetGwAlbumAsync(albumId);

    public async Task<Dictionary<string, object>?> GetAlbumAsync(string albumId, CancellationToken cancellationToken)
    {
        return await RequireClientProxy().GetAlbumAsync(albumId, cancellationToken);
    }

    public async Task<GwAlbumPageResponse> GetAlbumPageAsync(string albumId)
    {
        return await RequireClientProxy().GetAlbumPageAsync(albumId);
    }

    public async Task<List<GwTrack>> GetAlbumTracksAsync(string albumId)
    {
        return await RequireClientProxy().GetAlbumTracksAsync(albumId);
    }

    // -----===== Artists =====-----

    public Task<GwArtist> GetArtistAsync(string artistId) => RequireClientProxy().GetGwArtistAsync(artistId);

    public async Task<Dictionary<string, object>?> GetArtistAsync(string artistId, CancellationToken cancellationToken)
    {
        return await RequireClientProxy().GetArtistAsync(artistId, cancellationToken);
    }

    public async Task<List<GwTrack>> GetArtistTopTracksAsync(string artistId, int limit = 100)
    {
        return await RequireClientProxy().GetArtistTopTracksAsync(artistId, limit);
    }

    public async Task<GwDiscographyResponse> GetArtistDiscographyAsync(string artistId, int index = 0, int limit = 25)
    {
        return await RequireClientProxy().GetArtistDiscographyAsync(artistId, index, limit);
    }

    /// <summary>
    /// Get artist discography tabs exactly like deezspotag does
    /// </summary>
    public async Task<Dictionary<string, List<object>>> GetArtistDiscographyTabsAsync(string artistId, int limit = 100)
    {
        return await RequireClientProxy().GetArtistDiscographyTabsAsync(artistId, limit);
    }

    // -----===== Playlists =====-----

    public async Task<GwPlaylistPageResponse> GetPlaylistAsync(string playlistId)
    {
        return await RequireClientProxy().GetPlaylistPageAsync(playlistId);
    }

    public async Task<GwPlaylistPageResponse> GetPlaylistPageAsync(string playlistId)
    {
        return await RequireClientProxy().GetPlaylistPageAsync(playlistId);
    }

    public async Task<GwPlaylistPageResponse> GetPlaylistPageWithSongsAsync(string playlistId, int nb = 200, int start = 0)
    {
        return await ApiCallAsync<GwPlaylistPageResponse>("deezer.pagePlaylist", new
        {
            playlist_id = playlistId,
            lang = GetLanguage(),
            nb,
            tags = true,
            start
        });
    }

    public async Task<GwAlbumPageResponse> GetAlbumPageWithSongsAsync(string albumId)
    {
        return await ApiCallAsync<GwAlbumPageResponse>("deezer.pageAlbum", new
        {
            alb_id = albumId,
            lang = GetLanguage(),
            header = true
        });
    }

    public async Task<List<GwTrack>> GetPlaylistTracksAsync(string playlistId)
    {
        return await RequireClientProxy().GetPlaylistTracksAsync(playlistId);
    }

    public async Task<JObject> GetSmartTracklistPageAsync(string smartTracklistId)
    {
        return await ApiCallAsync<JObject>("deezer.pageSmartTracklist", new
        {
            smarttracklist_id = smartTracklistId
        });
    }

    public async Task<JObject> GetContextualTrackMixAsync(IReadOnlyList<string> songIds)
    {
        var filtered = (songIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (filtered.Length == 0)
        {
            throw new ArgumentException("At least one song id is required.", nameof(songIds));
        }

        return await ApiCallAsync<JObject>("song.getContextualTrackMix", new
        {
            sng_ids = filtered
        });
    }

    public async Task<JObject> GetShowPageAsync(string showId, string language = "en", int nb = 1000, int start = 0)
    {
        var userId = _sessionManager?.CurrentUser?.Id ?? "0";
        var country = GetCountry();
        var lang = GetLanguage(language);
        return await ApiCallAsync<JObject>("deezer.pageShow", new
        {
            country,
            lang,
            nb,
            show_id = showId,
            start,
            user_id = userId
        });
    }

    public async Task<JObject> GetEpisodePageAsync(string episodeId, string language = "en")
    {
        var userId = _sessionManager?.CurrentUser?.Id ?? "0";
        var country = GetCountry();
        var lang = GetLanguage(language);
        try
        {
            return await ApiCallAsync<JObject>("deezer.pageEpisode", new
            {
                country,
                lang,
                episode_id = episodeId,
                user_id = userId
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return await ApiCallAsync<JObject>("episode.getData", new
            {
                EPISODE_ID = episodeId
            });
        }
    }

    // -----===== Search =====-----

    public async Task<GwSearchResponse> SearchAsync(string query, int index = 0, int limit = 10, 
        bool suggest = true, bool artistSuggest = true, bool topTracks = true)
    {
        return await RequireClientProxy().GwSearchAsync(query, index, limit, suggest, artistSuggest, topTracks);
    }

    public async Task<JObject> GetHomePageAsync(string language = "en")
    {
        language = GetLanguage(language);
        var payload = BuildHomePageGatewayPayload("home", language);

        var gatewayInput = JsonConvert.SerializeObject(payload);
        var parameters = new Dictionary<string, object>
        {
            ["gateway_input"] = gatewayInput
        };

        return await ApiCallAsync<JObject>("page.get", new { }, parameters);
    }

    public async Task<JObject> GetChannelPageAsync(string target, string language = "en")
    {
        language = GetLanguage(language);
        var payload = BuildHomePageGatewayPayload(target, language);

        var gatewayInput = JsonConvert.SerializeObject(payload);
        var parameters = new Dictionary<string, object>
        {
            ["gateway_input"] = gatewayInput
        };

        return await ApiCallAsync<JObject>("page.get", new { }, parameters);
    }

    private static object BuildHomePageGatewayPayload(string page, string language)
    {
        return new
        {
            PAGE = page,
            VERSION = "2.5",
            SUPPORT = new Dictionary<string, object>
            {
                ["filterable-grid"] = new[] { "flow" },
                ["grid"] = HomeGridItemTypes,
                ["horizontal-grid"] = HomeGridItemTypes,
                ["item-highlight"] = new[] { "radio" },
                ["large-card"] = new[] { "album", "playlist", "show", "video-link" },
                ["ads"] = Array.Empty<string>()
            },
            LANG = language,
            OPTIONS = Array.Empty<object>()
        };
    }

    public async Task<List<string>> GetSearchSuggestionsAsync(string query)
    {
        var token = await ApiCallAsync<JToken>("search_getSuggestedQueries", new
        {
            QUERY = query
        });

        return ExtractSuggestions(token);
    }

    private static List<string> ExtractSuggestions(JToken token)
    {
        var suggestions = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddSuggestion(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
            {
                suggestions.Add(trimmed);
            }
        }

        if (token == null)
        {
            return suggestions;
        }

        var items = EnumerateSuggestionItems(token);

        foreach (var item in items)
        {
            if (item.Type == JTokenType.String)
            {
                AddSuggestion(item.Value<string>());
                continue;
            }

            if (item.Type == JTokenType.Object)
            {
                AddObjectSuggestions((JObject)item, AddSuggestion);
                continue;
            }

            if (item.Type == JTokenType.Array)
            {
                foreach (var inner in item.Children().Where(static inner => inner.Type == JTokenType.String))
                {
                    AddSuggestion(inner.Value<string>());
                }
            }
        }

        return suggestions;
    }

    private static IEnumerable<JToken> EnumerateSuggestionItems(JToken token)
    {
        if (token.Type == JTokenType.Array)
        {
            return token.Children();
        }

        if (token.Type != JTokenType.Object)
        {
            return Enumerable.Empty<JToken>();
        }

        var obj = (JObject)token;
        return TryGetSuggestionContainer(obj, "data")
            ?? TryGetSuggestionContainer(obj, "suggestions")
            ?? TryGetSuggestionContainer(obj, "queries")
            ?? obj.Properties().Select(p => p.Value);
    }

    private static IEnumerable<JToken>? TryGetSuggestionContainer(JObject obj, string propertyName)
    {
        if (!obj.TryGetValue(propertyName, out var token) || token == null)
        {
            return null;
        }

        return token.Type == JTokenType.Array ? token.Children() : new[] { token };
    }

    private static void AddObjectSuggestions(JObject obj, Action<string?> addSuggestion)
    {
        addSuggestion(obj.Value<string>("QUERY"));
        addSuggestion(obj.Value<string>("query"));
        addSuggestion(obj.Value<string>("value"));
        addSuggestion(obj.Value<string>("text"));
    }

    // -----===== Lyrics =====-----

    public async Task<Dictionary<string, object>?> GetLyricsAsync(string trackId, CancellationToken cancellationToken = default)
    {
        return await RequireClientProxy().GetLyricsAsync(trackId, cancellationToken);
    }

    /// <summary>
    /// Enhanced lyrics fetching using track ID (for new lyrics service integration)
    /// </summary>
    public async Task<Dictionary<string, object>?> GetLyricsByTrackIdAsync(string trackId, CancellationToken cancellationToken = default)
    {
        return await RequireClientProxy().GetLyricsByTrackIdAsync(trackId, cancellationToken);
    }

    /// <summary>
    /// Get track page data including lyrics (for fallback lyrics fetching)
    /// </summary>
    public async Task<Dictionary<string, object>?> GetTrackPageDataAsync(string trackId, CancellationToken cancellationToken = default)
    {
        return await RequireClientProxy().GetTrackPageDataAsync(trackId, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

}

// Gateway response models (moved from DeezerSessionManager to avoid duplication)
public class DeezerUserData
{
    public string? CheckForm { get; set; }
    public GwUser? User { get; set; }
}

public class GwUser
{
    [JsonProperty("USER_ID")]
    public long UserId { get; set; }
    
    [JsonProperty("BLOG_NAME")]
    public string? BlogName { get; set; }
    
    [JsonProperty("USER_PICTURE")]
    public string? UserPicture { get; set; }
    
    [JsonProperty("OPTIONS")]
    public GwUserOptions? Options { get; set; }
    
    [JsonProperty("MULTI_ACCOUNT")]
    [JsonConverter(typeof(MultiAccountConverter))]
    public GwMultiAccount? MultiAccount { get; set; }
    
    [JsonProperty("SETTING")]
    public GwUserSetting? Setting { get; set; }
}

public class GwMultiAccount
{
    [JsonProperty("ENABLED")]
    public bool Enabled { get; set; }
    
    [JsonProperty("IS_SUB_ACCOUNT")]
    public bool IsSubAccount { get; set; }
}

public class GwUserSetting
{
    [JsonProperty("global")]
    public GwGlobalSetting? Global { get; set; }
}

public class GwGlobalSetting
{
    [JsonProperty("language")]
    public string? Language { get; set; }
}

public class GwUserOptions
{
    [JsonProperty("license_country")]
    public string? LicenseCountry { get; set; }
    
    [JsonProperty("license_token")]
    public string? LicenseToken { get; set; }
    
    [JsonProperty("web_lossless")]
    public bool WebLossless { get; set; }
    
    [JsonProperty("mobile_lossless")]
    public bool MobileLossless { get; set; }
    
    [JsonProperty("web_hq")]
    public bool WebHq { get; set; }
    
    [JsonProperty("mobile_hq")]
    public bool MobileHq { get; set; }
}

public class GwAlbum
{
    [JsonProperty("ALB_ID")]
    public string AlbId { get; set; } = "";
    
    [JsonProperty("ALB_TITLE")]
    public string AlbTitle { get; set; } = "";
    
    [JsonProperty("ALB_PICTURE")]
    public string AlbPicture { get; set; } = "";
    
    [JsonProperty("ART_ID")]
    public long ArtId { get; set; }
    
    [JsonProperty("ART_NAME")]
    public string ArtName { get; set; } = "";
}

public class GwArtist
{
    [JsonProperty("ART_ID")]
    public long ArtId { get; set; }
    
    [JsonProperty("ART_NAME")]
    public string ArtName { get; set; } = "";
    
    [JsonProperty("ART_PICTURE")]
    public string ArtPicture { get; set; } = "";
}

// Response wrapper classes
public class GwTracksResponse
{
    public List<GwTrack> Data { get; set; } = new();
}

public class GwAlbumTracksResponse
{
    public List<GwTrack> Data { get; set; } = new();
}

public class GwPlaylistTracksResponse
{
    public List<GwTrack> Data { get; set; } = new();
}

public class GwArtistTopResponse
{
    public List<GwTrack> Data { get; set; } = new();
}

public class GwDiscographyResponse
{
    public List<GwAlbumRelease> Data { get; set; } = new();
    public int Total { get; set; }
}

public class GwAlbumRelease
{
    [JsonProperty("ALB_ID")]
    public string AlbId { get; set; } = "";
    
    [JsonProperty("ALB_TITLE")]
    public string? AlbTitle { get; set; }
    
    [JsonProperty("ALB_PICTURE")]
    public string? AlbPicture { get; set; }
    
    [JsonProperty("ART_ID")]
    public long ArtId { get; set; }
    
    [JsonProperty("ART_NAME")]
    public string? ArtName { get; set; }
    
    [JsonProperty("ROLE_ID")]
    public int RoleId { get; set; }
    
    [JsonProperty("ARTISTS_ALBUMS_IS_OFFICIAL")]
    public bool ArtistsAlbumsIsOfficial { get; set; }
    
    [JsonProperty("TYPE")]
    public int Type { get; set; }
    
    [JsonProperty("GENRE_ID")]
    public int GenreId { get; set; }
    
    [JsonProperty("NUMBER_TRACK")]
    public int NumberTrack { get; set; }
    
    [JsonProperty("NUMBER_DISK")]
    public int NumberDisk { get; set; }
    
    [JsonProperty("RANK")]
    public int Rank { get; set; }
    
    [JsonProperty("PHYSICAL_RELEASE_DATE")]
    public string? PhysicalReleaseDate { get; set; }
    
    [JsonProperty("DIGITAL_RELEASE_DATE")]
    public string? DigitalReleaseDate { get; set; }
    
    [JsonProperty("ORIGINAL_RELEASE_DATE")]
    public string? OriginalReleaseDate { get; set; }
    
    [JsonProperty("COPYRIGHT")]
    public string? Copyright { get; set; }
    
    [JsonProperty("EXPLICIT_LYRICS")]
    public bool ExplicitLyrics { get; set; }
    
    [JsonProperty("EXPLICIT_ALBUM_CONTENT")]
    public GwExplicitContent? ExplicitAlbumContent { get; set; }
}

public class GwExplicitContent
{
    [JsonProperty("EXPLICIT_LYRICS_STATUS")]
    public int ExplicitLyricsStatus { get; set; }
    
    [JsonProperty("EXPLICIT_COVER_STATUS")]
    public int ExplicitCoverStatus { get; set; }
}

public class GwTrackPageResponse
{
    public GwTrack Data { get; set; } = new();
    public string? Lyrics { get; set; }
    public object? Isrc { get; set; }
}

public class GwAlbumPageResponse
{
    public GwAlbum Data { get; set; } = new();
    public GwSongsData Songs { get; set; } = new();
}

public class GwPlaylistPageResponse
{
    public GwPlaylist Data { get; set; } = new();
    public GwSongsData Songs { get; set; } = new();
}

public class GwPlaylist
{
    [JsonProperty("PLAYLIST_ID")]
    public string PlaylistId { get; set; } = "";
    
    [JsonProperty("TITLE")]
    public string Title { get; set; } = "";
    
    [JsonProperty("DESCRIPTION")]
    public string Description { get; set; } = "";
    
    [JsonProperty("PLAYLIST_PICTURE")]
    public string PlaylistPicture { get; set; } = "";
    
    [JsonProperty("NB_SONG")]
    public int NbSong { get; set; }
    
    [JsonProperty("DURATION")]
    public int Duration { get; set; }

    [JsonProperty("STATUS")]
    public int Status { get; set; }

    [JsonProperty("IS_PUBLIC")]
    public bool IsPublic { get; set; }

    [JsonProperty("COLLABORATIVE")]
    public bool Collaborative { get; set; }

    [JsonProperty("CHECKSUM")]
    public string Checksum { get; set; } = "";

    [JsonProperty("CREATION_DATE")]
    public string CreationDate { get; set; } = "";

    [JsonProperty("IS_LOVED_TRACK")]
    public bool IsLovedTrack { get; set; }

    [JsonProperty("NB_FAN")]
    public int Fans { get; set; }

    [JsonProperty("PARENT_USER_ID")]
    public string OwnerId { get; set; } = "";

    [JsonProperty("PARENT_USERNAME")]
    public string OwnerName { get; set; } = "";

    [JsonProperty("PARENT_USER_PICTURE")]
    public string OwnerPicture { get; set; } = "";

    [JsonProperty("USER_ID")]
    public string UserId { get; set; } = "";

    [JsonProperty("USER_NAME")]
    public string UserName { get; set; } = "";

    [JsonProperty("USER_PICTURE")]
    public string UserPicture { get; set; } = "";
}

public class GwSongsData
{
    public List<GwTrack> Data { get; set; } = new();
    public int Count { get; set; }
    public int Total { get; set; }
}

public class GwSearchResponse
{
    [JsonProperty("ORDER")]
    public List<string>? Order { get; set; }
    
    [JsonProperty("TOP_RESULT")]
    public List<GwTopResult>? TopResult { get; set; }
    
    [JsonProperty("ARTIST")]
    public GwSearchSection? Artist { get; set; }
    
    [JsonProperty("ALBUM")]
    public GwSearchSection? Album { get; set; }
    
    [JsonProperty("TRACK")]
    public GwSearchSection? Track { get; set; }
    
    [JsonProperty("PLAYLIST")]
    public GwSearchSection? Playlist { get; set; }
}

public class GwSearchSection
{
    [JsonProperty("data")]
    public object[]? Data { get; set; }
    
    [JsonProperty("count")]
    public int Count { get; set; }
    
    [JsonProperty("total")]
    public int Total { get; set; }
    
    [JsonProperty("filtered_count")]
    public int FilteredCount { get; set; }
    
    [JsonProperty("filtered_items")]
    public object[]? FilteredItems { get; set; }
    
    [JsonProperty("next")]
    public int Next { get; set; }
}

public class GwTopResult
{
    [JsonProperty("__TYPE__")]
    public string? Type { get; set; }
    
    // Artist fields
    [JsonProperty("ART_ID")]
    public string? ArtistId { get; set; }
    
    [JsonProperty("ART_PICTURE")]
    public string? ArtistPicture { get; set; }
    
    [JsonProperty("ART_NAME")]
    public string? ArtistName { get; set; }
    
    [JsonProperty("NB_FAN")]
    public int? NbFan { get; set; }
    
    // Album fields
    [JsonProperty("ALB_ID")]
    public string? AlbumId { get; set; }
    
    [JsonProperty("ALB_PICTURE")]
    public string? AlbumPicture { get; set; }
    
    [JsonProperty("ALB_TITLE")]
    public string? AlbumTitle { get; set; }
    
    [JsonProperty("NUMBER_TRACK")]
    public int? NumberTrack { get; set; }
    
    // Playlist fields
    [JsonProperty("PLAYLIST_ID")]
    public string? PlaylistId { get; set; }
    
    [JsonProperty("PLAYLIST_PICTURE")]
    public string? PlaylistPicture { get; set; }
    
    [JsonProperty("PICTURE_TYPE")]
    public string? PictureType { get; set; }
    
    [JsonProperty("TITLE")]
    public string? Title { get; set; }
    
    [JsonProperty("PARENT_USERNAME")]
    public string? ParentUsername { get; set; }
    
    [JsonProperty("NB_SONG")]
    public int? NbSong { get; set; }
}

public class DeezerGatewayException : Exception
{
    public DeezerGatewayException(string message) : base(message) { }
    public DeezerGatewayException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Custom converter to handle MULTI_ACCOUNT field that can be either an array or an object
/// When it's an array (like []), it means no multi-account, so we return null
/// When it's an object, we deserialize it normally
/// </summary>
public class MultiAccountConverter : JsonConverter<GwMultiAccount?>
{
    public override GwMultiAccount? ReadJson(JsonReader reader, Type objectType, GwMultiAccount? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.StartArray)
        {
            // Skip the array - it means no multi-account
            serializer.Deserialize<object[]>(reader);
            return null;
        }
        else if (reader.TokenType == JsonToken.StartObject)
        {
            // Deserialize as normal object
            return serializer.Deserialize<GwMultiAccount>(reader);
        }
        else if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }
        
        throw new JsonSerializationException($"Unexpected token type for MULTI_ACCOUNT: {reader.TokenType}");
    }

    public override void WriteJson(JsonWriter writer, GwMultiAccount? value, JsonSerializer serializer)
    {
        if (value == null)
        {
            writer.WriteStartArray();
            writer.WriteEndArray();
        }
        else
        {
            serializer.Serialize(writer, value);
        }
    }
}

/// <summary>
/// Child account model for family accounts
/// Ported from deezspotag child account structure
/// </summary>
public class GwChildAccount
{
    [JsonProperty("USER_ID")]
    public long UserId { get; set; }
    
    [JsonProperty("BLOG_NAME")]
    public string? BlogName { get; set; }
    
    [JsonProperty("USER_PICTURE")]
    public string? UserPicture { get; set; }
    
    [JsonProperty("LOVEDTRACKS_ID")]
    public string? LovedTracksId { get; set; }
    
    [JsonProperty("EXTRA_FAMILY")]
    public GwExtraFamily? ExtraFamily { get; set; }
}

public class GwExtraFamily
{
    [JsonProperty("IS_LOGGABLE_AS")]
    public bool IsLoggableAs { get; set; }
}
