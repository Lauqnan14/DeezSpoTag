#!/usr/bin/env python3
import argparse
import re
from spotify_librespot_common import close_if_possible
from spotify_librespot_common import load_deezspot_librespot_client
from spotify_librespot_common import resolve_credentials
from spotify_librespot_common import write_result


SPOTIFY_TRACK_URI_RE = re.compile(r"spotify:track:([A-Za-z0-9]{22})")
SPOTIFY_TRACK_URL_RE = re.compile(r"open\.spotify\.com/track/([A-Za-z0-9]{22})")


def first_text(value):
    if value is None:
        return ""
    if isinstance(value, str):
        return value
    if isinstance(value, (int, float)):
        return str(value)
    return ""


def extract_track_id(track):
    for key in ("id", "track_id", "gid"):
        value = first_text(track.get(key) if isinstance(track, dict) else "")
        if len(value) == 22 and value.isalnum():
            return value
    for key in ("uri", "link", "url", "href"):
        value = first_text(track.get(key) if isinstance(track, dict) else "")
        match = SPOTIFY_TRACK_URI_RE.search(value) or SPOTIFY_TRACK_URL_RE.search(value)
        if match:
            return match.group(1)
    return ""


def extract_artists(track):
    artists = track.get("artists") if isinstance(track, dict) else None
    if not isinstance(artists, list):
        artists = track.get("artist") if isinstance(track, dict) else None
    if isinstance(artists, dict):
        artists = [artists]
    if not isinstance(artists, list):
        return []
    result = []
    for artist in artists:
        if isinstance(artist, dict):
            name = first_text(artist.get("name"))
        else:
            name = first_text(artist)
        if name:
            result.append({"name": name})
    return result


def extract_album(track):
    album = track.get("album") if isinstance(track, dict) else None
    if not isinstance(album, dict):
        album_name = first_text(track.get("album_name") if isinstance(track, dict) else "")
        return {"name": album_name, "images": []}
    images = album.get("images")
    if not isinstance(images, list):
        images = []
    return {"name": first_text(album.get("name")), "images": images}


def extract_isrc(track):
    external_ids = track.get("external_ids") if isinstance(track, dict) else None
    if isinstance(external_ids, dict):
        isrc = first_text(external_ids.get("isrc"))
        if isrc:
            return isrc
    return first_text(track.get("isrc") if isinstance(track, dict) else "")


def extract_duration_ms(track):
    for key in ("duration_ms", "duration"):
        value = track.get(key) if isinstance(track, dict) else None
        if isinstance(value, int):
            return value if key == "duration_ms" or value > 10000 else value * 1000
        if isinstance(value, str) and value.isdigit():
            number = int(value)
            return number if key == "duration_ms" or number > 10000 else number * 1000
    return None


def normalize_track(track):
    if not isinstance(track, dict):
        return None
    track_id = extract_track_id(track)
    if not track_id:
        return None
    name = first_text(track.get("name") or track.get("title"))
    return {
        "id": track_id,
        "name": name,
        "duration_ms": extract_duration_ms(track),
        "external_ids": {"isrc": extract_isrc(track)},
        "artists": extract_artists(track),
        "album": extract_album(track),
        "external_urls": {"spotify": f"https://open.spotify.com/track/{track_id}"},
    }


def find_track_items(payload):
    if isinstance(payload, dict):
        tracks = payload.get("tracks")
        if isinstance(tracks, dict):
            items = tracks.get("items")
            if isinstance(items, list):
                return items
        items = payload.get("items")
        if isinstance(items, list):
            return items
    return []


def main():
    parser = argparse.ArgumentParser(description="Search Spotify tracks via librespot.")
    parser.add_argument("--credentials", required=True, help="Path to librespot credentials.json")
    parser.add_argument("--query", required=True, help="Spotify search query")
    parser.add_argument("--limit", type=int, default=10, help="Maximum number of results")
    parser.add_argument("--country", default=None, help="Optional country code")
    args = parser.parse_args()

    try:
        librespot_client = load_deezspot_librespot_client()
    except Exception as exc:
        write_result(False, error=f"librespot client loader failed: {exc}")
        return 1

    credentials_path = resolve_credentials(args.credentials)
    if credentials_path is None:
        write_result(False, error="credentials_not_found")
        return 1

    try:
        client = librespot_client(stored_credentials_path=credentials_path.as_posix(), max_workers=2)
        raw = client.search(args.query, limit=max(1, min(args.limit, 50)), country=args.country)
        tracks = []
        for item in find_track_items(raw):
            normalized = normalize_track(item)
            if normalized is not None:
                tracks.append(normalized)
        write_result(True, payload={"tracks": {"items": tracks, "total": len(tracks)}})
        return 0
    except Exception as exc:
        write_result(False, error=f"librespot_search_error: {exc}")
        return 1
    finally:
        try:
            close_if_possible(locals().get("client"))
        except Exception:
            pass


if __name__ == "__main__":
    raise SystemExit(main())
