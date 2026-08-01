#!/usr/bin/env python3
import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import re
import sys
import time

from spotify_librespot_common import close_if_possible
from spotify_librespot_common import is_valid_spotify_id
from spotify_librespot_common import load_deezspot_librespot_client
from spotify_librespot_common import resolve_credentials


SPOTIFY_TRACK_URI_RE = re.compile(r"spotify:track:([A-Za-z0-9]{22})")
SPOTIFY_TRACK_URL_RE = re.compile(r"open\.spotify\.com/track/([A-Za-z0-9]{22})")


def _write(message):
    print(json.dumps(message, separators=(",", ":")), flush=True)


def _first_text(value):
    if value is None:
        return ""
    if isinstance(value, str):
        return value
    if isinstance(value, (int, float)):
        return str(value)
    return ""


def _extract_track_id(track):
    if not isinstance(track, dict):
        return ""
    for key in ("id", "track_id", "gid"):
        value = _first_text(track.get(key))
        if is_valid_spotify_id(value):
            return value
    for key in ("uri", "link", "url", "href"):
        value = _first_text(track.get(key))
        match = SPOTIFY_TRACK_URI_RE.search(value) or SPOTIFY_TRACK_URL_RE.search(value)
        if match:
            return match.group(1)
    return ""


def _extract_artists(track):
    artists = track.get("artists") if isinstance(track, dict) else None
    if not isinstance(artists, list):
        artists = track.get("artist") if isinstance(track, dict) else None
    if isinstance(artists, dict):
        artists = [artists]
    if not isinstance(artists, list):
        return []
    result = []
    for artist in artists:
        name = _first_text(artist.get("name")) if isinstance(artist, dict) else _first_text(artist)
        if name:
            result.append({"name": name})
    return result


def _extract_album(track):
    album = track.get("album") if isinstance(track, dict) else None
    if not isinstance(album, dict):
        return {"name": _first_text(track.get("album_name") if isinstance(track, dict) else ""), "images": []}
    images = album.get("images")
    return {"name": _first_text(album.get("name")), "images": images if isinstance(images, list) else []}


def _extract_isrc(track):
    external_ids = track.get("external_ids") if isinstance(track, dict) else None
    if isinstance(external_ids, dict):
        isrc = _first_text(external_ids.get("isrc"))
        if isrc:
            return isrc
    return _first_text(track.get("isrc") if isinstance(track, dict) else "")


def _extract_duration_ms(track):
    if not isinstance(track, dict):
        return None
    for key in ("duration_ms", "duration"):
        value = track.get(key)
        if isinstance(value, int):
            return value if key == "duration_ms" or value > 10000 else value * 1000
        if isinstance(value, str) and value.isdigit():
            number = int(value)
            return number if key == "duration_ms" or number > 10000 else number * 1000
    return None


def _normalize_search_track(track):
    if not isinstance(track, dict):
        return None
    track_id = _extract_track_id(track)
    if not track_id:
        return None
    return {
        "id": track_id,
        "name": _first_text(track.get("name") or track.get("title")),
        "duration_ms": _extract_duration_ms(track),
        "external_ids": {"isrc": _extract_isrc(track)},
        "artists": _extract_artists(track),
        "album": _extract_album(track),
        "external_urls": {"spotify": f"https://open.spotify.com/track/{track_id}"},
    }


def _find_track_items(payload):
    if isinstance(payload, dict):
        tracks = payload.get("tracks")
        if isinstance(tracks, dict) and isinstance(tracks.get("items"), list):
            return tracks["items"]
        if isinstance(payload.get("items"), list):
            return payload["items"]
    return []


def _fetch_show(client, spotify_id):
    from librespot.metadata import ShowId  # type: ignore

    show = client._session.api().get_metadata_4_show(ShowId.from_base62(spotify_id))
    return client._proto_to_full_json(show)


def _fetch_episode(client, spotify_id):
    from librespot.proto.ExtensionKind_pb2 import ExtensionKind  # type: ignore
    from librespot.proto import Metadata_pb2 as Metadata  # type: ignore

    metadata_bytes = client._session.api().get_ext_metadata(
        ExtensionKind.EPISODE_V4,
        f"spotify:episode:{spotify_id}",
    )
    episode = Metadata.Episode()
    episode.ParseFromString(metadata_bytes)
    return client._proto_to_full_json(episode)


def _extract_token(client, scopes):
    provider = client._session.tokens()
    token_obj = provider.get_token(*scopes) if hasattr(provider, "get_token") else None
    access_token = getattr(token_obj, "access_token", None)
    expires_in = getattr(token_obj, "expires_in", None)
    if expires_in is None:
        expires_in = getattr(token_obj, "expires_in_s", None)
    if not access_token and hasattr(provider, "get"):
        access_token = provider.get(scopes[0])
    expires_at = int((time.time() + float(expires_in)) * 1000) if expires_in is not None else None
    if not access_token:
        raise RuntimeError("librespot_token_unavailable")
    return {"access_token": access_token, "expires_at_unix_ms": expires_at}


class Worker:
    def __init__(self, client_class, credentials_path):
        self._client_class = client_class
        self._credentials_path = credentials_path
        self._client = self._create_client()

    def _create_client(self):
        return self._client_class(stored_credentials_path=self._credentials_path, max_workers=5)

    def close(self):
        close_if_possible(self._client)

    def _reconnect(self):
        close_if_possible(self._client)
        self._client = self._create_client()

    def execute(self, operation, arguments):
        try:
            return self._execute(operation, arguments)
        except (KeyError, ValueError):
            raise
        except Exception:
            self._reconnect()
            return self._execute(operation, arguments)

    def _execute(self, operation, arguments):
        if operation == "tracks":
            track_ids = [value for value in arguments.get("track_ids", []) if is_valid_spotify_id(value)]
            if not track_ids:
                raise ValueError("missing_track_ids")

            def fetch(track_id):
                try:
                    track = self._client.get_track(track_id)
                    return {"id": track_id, "track": track} if track else {"id": track_id, "error": "librespot_track_empty"}
                except Exception as exc:
                    return {"id": track_id, "error": f"librespot_track_error: {exc}"}

            with ThreadPoolExecutor(max_workers=min(5, len(track_ids))) as executor:
                payload = list(executor.map(fetch, track_ids))
            failures = [{"id": item["id"], "error": item["error"]} for item in payload if "error" in item]
            if failures and len(failures) == len(payload):
                raise RuntimeError("librespot_track_batch_failed")
            return payload, failures

        if operation == "playlist":
            return self._client.get_playlist(arguments["playlist_id"], expand_items=False), []
        if operation == "album":
            return self._client.get_album(arguments["album_id"], include_tracks=bool(arguments.get("include_tracks"))), []
        if operation == "artist":
            return self._client.get_artist(arguments["artist_id"]), []
        if operation == "search":
            raw = self._client.search(
                arguments["query"],
                limit=max(1, min(int(arguments.get("limit", 10)), 50)),
                country=arguments.get("country"),
            )
            tracks = [track for item in _find_track_items(raw) if (track := _normalize_search_track(item)) is not None]
            return {"tracks": {"items": tracks, "total": len(tracks)}}, []
        if operation == "show":
            return _fetch_show(self._client, arguments["spotify_id"]), []
        if operation == "episode":
            return _fetch_episode(self._client, arguments["spotify_id"]), []
        if operation == "token":
            return _extract_token(self._client, arguments.get("scopes") or ["playlist-read"]), []
        raise ValueError(f"unsupported_operation:{operation}")


def main():
    parser = argparse.ArgumentParser(description="Persistent Spotify metadata worker backed by one librespot session.")
    parser.add_argument("--credentials", required=True)
    args = parser.parse_args()

    credentials_path = resolve_credentials(args.credentials)
    if credentials_path is None:
        _write({"ready": False, "error": "credentials_not_found"})
        return 1
    try:
        client_class = load_deezspot_librespot_client()
        worker = Worker(client_class, credentials_path.as_posix())
    except Exception as exc:
        _write({"ready": False, "error": f"librespot_session_error: {exc}"})
        return 1

    _write({"ready": True})
    try:
        for line in sys.stdin:
            request = None
            try:
                request = json.loads(line)
                request_id = request.get("id")
                payload, failures = worker.execute(request.get("operation", ""), request.get("arguments") or {})
                _write({
                    "id": request_id,
                    "ok": len(failures) == 0,
                    "partial": bool(failures) and len(failures) < len(payload) if isinstance(payload, list) else False,
                    "payload": payload,
                    "failures": failures,
                    "error": "partial_track_metadata" if failures else None,
                })
            except Exception as exc:
                _write({"id": request.get("id") if isinstance(request, dict) else None, "ok": False, "error": str(exc)})
    finally:
        worker.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
