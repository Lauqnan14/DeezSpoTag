using System.Text.Json.Serialization;

namespace DeezSpoTag.Services.Download.Shared.Models;

public abstract class DeezSpoTagDownloadObject
{
    [JsonPropertyName("uuid")]
    public string UUID { get; set; } = string.Empty;
    
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
    
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    
    [JsonPropertyName("bitrate")]
    public int Bitrate { get; set; }
    
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
    
    [JsonPropertyName("artist")]
    public string Artist { get; set; } = string.Empty;
    
    [JsonPropertyName("cover")]
    public string Cover { get; set; } = string.Empty;
    
    [JsonPropertyName("explicit")]
    public bool Explicit { get; set; }
    
    [JsonPropertyName("size")]
    public int Size { get; set; }
    
    [JsonPropertyName("isCanceled")]
    public bool IsCanceled { get; set; }
    
    [JsonPropertyName("downloaded")]
    public int Downloaded { get; set; }
    
    [JsonPropertyName("failed")]
    public int Failed { get; set; }
    
    [JsonPropertyName("extrasPath")]
    public string ExtrasPath { get; set; } = string.Empty;

    [JsonPropertyName("destinationFolderId")]
    public long? DestinationFolderId { get; set; }
    
    [JsonPropertyName("progress")]
    public double Progress { get; set; }
    
    [JsonPropertyName("progressNext")]
    public double ProgressNext { get; set; }
    
    [JsonPropertyName("errors")]
    public List<object> Errors { get; set; } = new();
    
    [JsonPropertyName("files")]
    public List<Dictionary<string, object>> Files { get; set; } = new();

    public abstract Dictionary<string, object> GetEssentialDict();
    public abstract Dictionary<string, object> GetSlimmedDict();
    public abstract Dictionary<string, object> ToDict();
    protected abstract double TrackProgressIncrement { get; }
    protected abstract string ObjectType { get; }

    public void UpdateProgress(IDeezSpoTagListener? listener)
    {
        var next = Math.Clamp(ProgressNext, 0d, 100d);
        var delta = Math.Abs(next - Progress);
        if (delta < 0.25 && next < 100d && next > 0d)
        {
            return;
        }

        Progress = next;
        listener?.Send("updateQueue", new
        {
            uuid = UUID,
            title = Title,
            progress = Progress
        });
    }

    public void CompleteTrackProgress(IDeezSpoTagListener? listener)
    {
        ProgressNext += TrackProgressIncrement;
        UpdateProgress(listener);
    }

    public void RemoveTrackProgress(IDeezSpoTagListener? listener)
    {
        ProgressNext -= TrackProgressIncrement;
        UpdateProgress(listener);
    }

    protected Dictionary<string, object> BuildEssentialDict()
    {
        return new Dictionary<string, object>
        {
            ["type"] = Type,
            ["id"] = Id,
            ["bitrate"] = Bitrate,
            ["uuid"] = UUID,
            ["title"] = Title,
            ["artist"] = Artist,
            ["cover"] = Cover,
            ["explicit"] = Explicit,
            ["size"] = Size,
            ["extrasPath"] = ExtrasPath,
            ["destinationFolderId"] = DestinationFolderId!
        };
    }

    protected Dictionary<string, object> BuildSlimmedDict()
    {
        var light = ToDict();
        light.Remove("single");
        light.Remove("collection");
        return light;
    }

    protected Dictionary<string, object> BuildBaseDict(string payloadKey, Dictionary<string, object> payload)
    {
        return new Dictionary<string, object>
        {
            ["__type__"] = ObjectType,
            ["uuid"] = UUID,
            ["type"] = Type,
            ["id"] = Id,
            ["bitrate"] = Bitrate,
            ["title"] = Title,
            ["artist"] = Artist,
            ["cover"] = Cover,
            ["explicit"] = Explicit,
            ["size"] = Size,
            ["downloaded"] = Downloaded,
            ["failed"] = Failed,
            ["progress"] = Progress,
            ["errors"] = Errors,
            ["files"] = Files,
            ["extrasPath"] = ExtrasPath,
            ["destinationFolderId"] = DestinationFolderId!,
            [payloadKey] = payload
        };
    }
}

public class DeezSpoTagSingle : DeezSpoTagDownloadObject
{
    protected override double TrackProgressIncrement => 100d;
    protected override string ObjectType => "Single";

    [JsonPropertyName("__type__")]
    public string __Type__ { get; set; } = "Single";
    
    [JsonPropertyName("single")]
    public SingleData Single { get; set; } = new();

    public override Dictionary<string, object> GetEssentialDict() => BuildEssentialDict();

    public override Dictionary<string, object> GetSlimmedDict() => BuildSlimmedDict();

    public override Dictionary<string, object> ToDict()
    {
        var singleDict = new Dictionary<string, object>
        {
            ["trackAPI"] = Single.TrackAPI
        };

        if (Single.AlbumAPI != null)
        {
            singleDict["albumAPI"] = Single.AlbumAPI;
        }

        return BuildBaseDict("single", singleDict);
    }
}

public class DeezSpoTagCollection : DeezSpoTagDownloadObject
{
    protected override double TrackProgressIncrement => Size > 0 ? (1.0 / Size) * 100d : 0d;
    protected override string ObjectType => "Collection";

    [JsonPropertyName("__type__")]
    public string __Type__ { get; set; } = "Collection";
    
    [JsonPropertyName("collection")]
    public CollectionData Collection { get; set; } = new();

    public override Dictionary<string, object> GetEssentialDict() => BuildEssentialDict();

    public override Dictionary<string, object> GetSlimmedDict() => BuildSlimmedDict();

    public override Dictionary<string, object> ToDict()
    {
        var collectionDict = new Dictionary<string, object>
        {
            ["tracks"] = Collection.Tracks
        };

        if (Collection.AlbumAPI != null)
        {
            collectionDict["albumAPI"] = Collection.AlbumAPI;
        }

        if (Collection.PlaylistAPI != null)
        {
            collectionDict["playlistAPI"] = Collection.PlaylistAPI;
        }

        return BuildBaseDict("collection", collectionDict);
    }
}

public class SingleData
{
    [JsonPropertyName("trackAPI")]
    public Dictionary<string, object> TrackAPI { get; set; } = new();
    
    [JsonPropertyName("albumAPI")]
    public Dictionary<string, object>? AlbumAPI { get; set; }
}

public class CollectionData
{
    [JsonPropertyName("tracks")]
    public List<Dictionary<string, object>> Tracks { get; set; } = new();
    
    [JsonPropertyName("albumAPI")]
    public Dictionary<string, object>? AlbumAPI { get; set; }
    
    [JsonPropertyName("playlistAPI")]
    public Dictionary<string, object>? PlaylistAPI { get; set; }
}
