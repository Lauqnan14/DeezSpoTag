# deezspotag-metadata

A Navidrome `MetadataAgent` plugin that serves **artist biographies** and **top songs**
from a DeezSpoTag instance.

## Why this exists

The Subsonic API has no metadata write path — no `setArtistInfo`, no way to push a
biography. DeezSpoTag already hit this wall; `ArtistMetadataUpdaterService` emits
`"Navidrome biography is read-only and was not updated."` and drops the text.

This plugin inverts the flow: instead of DeezSpoTag pushing, Navidrome pulls. The
plugin runs inside Navidrome, calls back into DeezSpoTag over HTTP, and returns the
data through the normal agent chain — so it surfaces in `getArtistInfo2` and
`getTopSongs` for every client.

## Scope

| Implemented | Why |
|---|---|
| `GetArtistBiography` | The gap this plugin exists to close. |
| `GetArtistTopSongs` | Promotes top songs from a sibling playlist to the native artist page. |
| `GetSimilarArtists` | Spotify related artists, resolved to local artist IDs where the library already knows them. |

## Background art

Not achievable, by anyone. There is no backdrop field in the Subsonic
`artistInfo2` response, no role/type discriminator on the plugin's `ImageInfo`
(only `URL` + `Size`), and no backdrop concept in Navidrome. Clients that show
artist backdrops (Symfonium, Psysonic) scrape fanart.tv themselves, keyed by
MusicBrainz ID — which does not help a library MusicBrainz has never heard of.

DeezSpoTag's `background` artwork slot is therefore correctly Plex/Jellyfin-only.

## Build

Requires **Go 1.25+** and **TinyGo** (the PDK is `//go:build wasip1`; the system Go
toolchain cannot build it alone).

```bash
make            # builds plugin.wasm and packages deezspotag-metadata.ndp
NAVIDROME_PLUGIN_DIR=/data/navidrome/plugins make install
```

Verified with Go 1.25.0 and TinyGo 0.41.1 (LLVM 20.1.1), producing a ~1.28 MB
`plugin.wasm` exporting `nd_get_artist_biography`, `nd_get_artist_top_songs` and
`nd_get_similar_artists`.
The PDK emits export wrappers for all eleven MetadataAgent methods; the eight this
plugin does not implement return `NotImplementedCode` (-2), which the host treats
as "skip this agent" rather than an error.

`go.mod` pins the PDK to a pseudo-version off navidrome `master`, since
`plugins/pdk/go` carries no tags of its own.

## Configure

Navidrome (`navidrome.toml`):

```toml
[Plugins]
Enabled = true
AutoReload = true

Agents = "deezspotag-metadata"
```

⚠️ **List this agent alone.** Last.fm and Deezer match on name and will confidently
return a biography and top-songs list for an unrelated same-named Western act.
For a catalog they do not cover, that is worse than empty. Navidrome's built-in
`local` agent always runs as the final fallback, so top songs degrade to your own
play counts rather than to fiction.

Then in the Navidrome UI, set the plugin config:

| Field | Notes |
|---|---|
| `baseUrl` | Must be reachable **from the Navidrome container** — not just your browser. |
| `apiToken` | Matches `ApiToken` in DeezSpoTag settings, or `DEEZSPOTAG_API_TOKEN`. |
| `preferredBiographySource` | `spotify` / `deezer` / `apple` / `lastfm`; blank = any. |
| `cacheTtlSeconds` | Default 86400. Misses are negative-cached for 1h. |

### Remote access

DeezSpoTag's bearer-token middleware only accepts tokens from trusted-local
addresses unless `DEEZSPOTAG_ALLOW_REMOTE_API_TOKEN=1` is set. If Navidrome runs
in a separate container, its source address is typically **not** trusted-local, so
you will need that variable — or a host-network / loopback arrangement.

## How matching works

`GetArtistTopSongs` returns `SongRef`s carrying **ISRC**. Navidrome's matcher
resolves them against the library by `ID > MBID > ISRC > fuzzy title`, and
DeezSpoTag writes ISRC into file tags (`AudioTagger` / `AudioTagWriter`), so the
ISRC rung hits reliably where MusicBrainz has nothing.

Artist identity uses Navidrome's own artist ID, which DeezSpoTag already records
in `artist_source` on every metadata push. Name matching only bootstraps artists
never pushed, and a successful name match persists the mapping — so each artist is
name-matched at most once.

## Endpoints consumed

Served by `MetadataAgentApiController` in DeezSpoTag. Both return **204** when there
is nothing to serve, which is what makes Navidrome fall through to the next agent
instead of caching an empty result.

```
GET /api/metadata-agent/artist/biography ?id=&name=&preferredSource=
GET /api/metadata-agent/artist/top-songs ?id=&name=&count=
GET /api/metadata-agent/artist/similar   ?id=&name=&count=
```

`artist/similar` currently serves **Spotify related artists only**, read from the
cached Spotify artist page. Each entry carries a `source` field so additional
providers can be merged in later without changing the response shape. Where a
related artist already exists locally and has a `navidrome` source id, that id is
returned so Navidrome links straight to it instead of name-matching.
