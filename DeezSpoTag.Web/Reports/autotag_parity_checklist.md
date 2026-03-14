# AutoTag Parity Checklist (OneTagger vs C#)

Scope: All platforms except Deezer, MusicBrainz, Spotify.

Status legend:
- `Done` = implemented to match OneTagger behavior
- `Verify` = needs log-based/manual confirmation

Global Matching Rules
- `Done` Clean title and artist logic matches OneTagger for all platforms using `OneTaggerMatching`.
- `Done` Duration matching and strictness align with OneTagger defaults.
- `Done` Multiple-matches sorting options align with OneTagger options.
- `Verify` Match-by-ID toggles behave correctly when `MatchById` is enabled and tag IDs exist in files.

Beatport
- `Done` Query uses `CleanArtistSearching + CleanTitle`.
- `Done` ISRC-first lookup path.
- `Done` ID match via `BEATPORT_TRACK_ID` only when `MatchById` is true.
- `Done` Restricted tracks fallback loop matches OneTagger behavior.
- `Done` Release extension only when `albumArtist` or `trackTotal` tags are enabled.
- `Verify` Track restricted handling in API still matches OneTagger on live data.

Discogs
- `Done` Query and fallback search (`title` + `artist`) match OneTagger.
- `Done` ID match via `DISCOGS_RELEASE_ID` only when `MatchById` is true.
- `Done` Track number exact match when ID is present.
- `Done` Label/Catalog fetch from main release only when `label` or `catalogNumber` tags are enabled.
- `Verify` Rate limit handling vs OneTagger default rate limit and token validation.

Traxsource
- `Done` Query uses `CleanTitle`.
- `Done` Extend track only when album/track meta tags are enabled.
- `Done` Album meta extension only when album-art/track-total/album-artist/track-number/catalog-number tags are enabled.
- `Verify` HTML parsing still matches Traxsource markup.

JunoDownload
- `Done` Query uses `CleanTitle`.
- `Done` Match rules are identical (title, artist, duration).
- `Verify` Rate limit handling (429 retry) matches OneTagger.

Bandcamp
- `Done` Query uses `CleanTitle` and artist.
- `Done` Match rules align with OneTagger (artist + title).
- `Done` Full track extension mirrors OneTagger flow.
- `Verify` Bandcamp page parse remains compatible with current HTML structure.

Beatsource
- `Done` Query uses `CleanTitle`.
- `Done` Track conversion uses the same fields as OneTagger.
- `Verify` OAuth token refresh behavior matches OneTagger in production.

BpmSupreme
- `Done` Title suffix stripping matches OneTagger regex.
- `Done` Query order matches OneTagger (`{clean_title} {clean_artist}`).
- `Verify` Login/session token handling and rate-limit delays.

iTunes
- `Done` Query uses `CleanTitle`.
- `Done` Rate limit logic matches OneTagger’s pace.
- `Verify` Search results mapping still aligns with current API responses.

Musixmatch
- `Done` Only runs when lyrics tags enabled.
- `Done` Richsync/subtitles/lyrics fallbacks align with OneTagger order.
- `Verify` Captcha retry behavior under real-world throttling.

Suggested Validation Steps
1. Enable `MatchById` and tag a file with `BEATPORT_TRACK_ID` and `DISCOGS_RELEASE_ID`. Confirm 1.0 accuracy match without search.
2. Enable only `albumArtist` or `trackTotal`, and verify Beatport release extension happens; disable and confirm it skips.
3. Disable `label` and `catalogNumber`, confirm Discogs main-release lookup is skipped.
4. For Traxsource, toggle `albumArt` and confirm extension happens only when enabled.
5. For Musixmatch, disable lyrics tags and confirm no request is made.

