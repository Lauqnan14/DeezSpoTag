### LibrespotClient wrapper

A thin, high-level wrapper over the internal librespot API that returns Web API-like dictionaries for albums, tracks, artists, and playlists. Use this to standardize access to Spotify metadata throughout the codebase.

- Import path: `from deezspot.libutils import LibrespotClient`
- Backed by: `librespot` internal API (Session + Metadata/Playlist protos)
- Thread-safe for read operations; uses an internal in-memory cache for track object expansions


## Initialization

```python
from deezspot.libutils import LibrespotClient

# 1) Create with stored credentials file (recommended)
client = LibrespotClient(stored_credentials_path="/absolute/path/to/credentials.json")

# 2) Or reuse an existing librespot Session
# from librespot.core import Session
# session = ...
# client = LibrespotClient(session=session)
```

- **stored_credentials_path**: path to JSON created by librespot credential flow
- **session**: optional existing `librespot.core.Session` (if provided, `stored_credentials_path` is not required)
- **max_workers**: optional concurrency cap for track expansion (default 16, bounded [1, 32])

Always dispose when done:

```python
client.close()
```


## ID/URI inputs

All data-fetching methods accept either:
- A Spotify URI (e.g., `spotify:album:...`, `spotify:track:...`, etc)
- A base62 ID (e.g., `3KuXEGcqLcnEYWnn3OEGy0`)
- A public `open.spotify.com/...` URL (album/track/artist/playlist)
- Or the corresponding `librespot.metadata.*Id` class

You can also use the helper if needed:

```python
# kind in {"track", "album", "artist", "playlist"}
track_id = LibrespotClient.parse_input_id("track", "https://open.spotify.com/track/...")
```


## Public API

### get_album(album, include_tracks=False) -> dict
Fetches album metadata.

- **album**: URI/base62/URL or `AlbumId`
- **include_tracks**: when True, expands the album's tracks to full track objects using concurrent fetches; when False, returns `tracks` as an array of track base62 IDs

Return shape (subset):

```json
{
  "album_type": "album",
  "total_tracks": 10,
  "available_markets": ["US", "GB"],
  "external_urls": {"spotify": "https://open.spotify.com/album/{id}"},
  "id": "{base62}",
  "images": [{"url": "https://...", "width": 640, "height": 640}],
  "name": "...",
  "release_date": "2020-05-01",
  "release_date_precision": "day",
  "type": "album",
  "uri": "spotify:album:{base62}",
  "artists": [{"id": "...", "name": "...", "type": "artist", "uri": "..."}],
  "tracks": [
    // include_tracks=False -> ["trackBase62", ...]
    // include_tracks=True  -> [{ track object }, ...]
  ],
  "copyrights": [{"text": "...", "type": "..."}],
  "external_ids": {"upc": "..."},
  "label": "...",
  "popularity": 57
}
```

Usage:

```python
album = client.get_album("spotify:album:...", include_tracks=True)
```


### get_track(track) -> dict
Fetches track metadata.

- **track**: URI/base62/URL or `TrackId`

Return shape (subset):

```json
{
  "album": { /* embedded album (no tracks) */ },
  "artists": [{"id": "...", "name": "..."}],
  "available_markets": ["US", "GB"],
  "disc_number": 1,
  "duration_ms": 221000,
  "explicit": false,
  "external_ids": {"isrc": "..."},
  "external_urls": {"spotify": "https://open.spotify.com/track/{id}"},
  "id": "{base62}",
  "name": "...",
  "popularity": 65,
  "track_number": 1,
  "type": "track",
  "uri": "spotify:track:{base62}",
  "preview_url": "https://p.scdn.co/mp3-preview/{hex}",
  "has_lyrics": true,
  "earliest_live_timestamp": 0,
  "licensor_uuid": "{hex}" // when available
}
```

Usage:

```python
track = client.get_track("3KuXEGcqLcnEYWnn3OEGy0")
```


### get_artist(artist) -> dict
Fetches artist metadata and returns a full JSON-like mapping of the protobuf (pruned of empty fields).

- **artist**: URI/base62/URL or `ArtistId`

Usage:

```python
artist = client.get_artist("https://open.spotify.com/artist/...")
```


### get_playlist(playlist, expand_items=False) -> dict
Fetches playlist contents.

- **playlist**: URI/URL/ID or `PlaylistId` (Spotify uses non-base62 playlist IDs)
- **expand_items**: when True, playlist items containing tracks are expanded to full track objects (concurrent fetch with caching); otherwise, items contain minimal track stubs with `id`, `uri`, `type`, and `external_urls`

Return shape (subset):

```json
{
  "name": "My Playlist",
  "description": "...",
  "collaborative": false,
  "images": [{"url": "https://..."}],
  "owner": {
    "id": "username",
    "type": "user",
    "uri": "spotify:user:username",
    "external_urls": {"spotify": "https://open.spotify.com/user/username"},
    "display_name": "username"
  },
  "snapshot_id": "base64Revision==",
  "tracks": {
    "offset": 0,
    "total": 42,
    "items": [
      {
        "added_at": "2023-01-01T12:34:56Z",
        "added_by": {"id": "...", "type": "user", "uri": "...", "external_urls": {"spotify": "..."}, "display_name": "..."},
        "is_local": false,
        "track": {
          // expand_items=False -> {"id": "...", "uri": "spotify:track:...", "type": "track", "external_urls": {"spotify": "..."}}
          // expand_items=True  -> full track object
        },
        "item_id": "{hex}" // additional reference, not a Web API field
      }
    ]
  },
  "type": "playlist"
}
```

Usage:

```python
playlist = client.get_playlist("spotify:playlist:...")
playlist_expanded = client.get_playlist("spotify:playlist:...", expand_items=True)
```



## Concurrency and caching

- When expanding tracks for albums/playlists, the client concurrently fetches missing track objects using a `ThreadPoolExecutor` with up to `max_workers` threads (default 16).
- A per-instance in-memory cache stores fetched track objects keyed by base62 ID to avoid duplicate network calls in the same process.


## Error handling

- Underlying network/protobuf errors are not swallowed; wrap your calls if you need custom handling.
- Empty/missing fields are pruned from output structures where appropriate.

Example:

```python
try:
    data = client.get_album("spotify:album:...")
except Exception as exc:
    # handle failure (retry/backoff/logging)
    raise
```


## Migration guide (from direct librespot usage)

Before (direct protobuf access):

```python
album_id = AlbumId.from_base62(base62)
proto = session.api().get_metadata_4_album(album_id)
# manual traversal over `proto`...
```

After (wrapper):

```python
from deezspot.libutils import LibrespotClient

client = LibrespotClient(stored_credentials_path="/path/to/credentials.json")
try:
    album = client.get_album(base62, include_tracks=True)
finally:
    client.close()
```


## Notes

- Image URLs are derived from internal `file_id` bytes using the public Spotify image host.
- Playlist IDs are not base62; pass the raw ID, URI, or URL.
- For performance-critical paths, reuse a single `LibrespotClient` instance (and its cache) per worker. 