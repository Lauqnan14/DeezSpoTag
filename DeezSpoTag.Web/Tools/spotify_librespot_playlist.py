#!/usr/bin/env python3
import argparse
from spotify_librespot_common import ensure_vendor_paths
from spotify_librespot_common import resolve_credentials
from spotify_librespot_common import write_result

PLAYLIST_URI_PREFIX = "spotify:playlist:"


def _load_librespot():
    ensure_vendor_paths(include_deezspot=False, include_crypto=False)

    try:
        from librespot.core import Session  # type: ignore
        from librespot.metadata import PlaylistId  # type: ignore
    except Exception as exc:
        raise RuntimeError("spotizerr-phoenix librespot vendor is not available") from exc

    return Session, PlaylistId


def _to_json_message(message):
    try:
        from google.protobuf.json_format import MessageToDict  # type: ignore
    except Exception:
        return None

    try:
        return MessageToDict(
            message,
            preserving_proto_field_name=True,
            including_default_value_fields=False,
            use_integers_for_enums=True,
        )
    except Exception:
        return None


def main():
    parser = argparse.ArgumentParser(description="Fetch Spotify playlist via librespot (spclient.wg.spotify.com).")
    parser.add_argument("--credentials", required=True, help="Path to librespot credentials.json")
    parser.add_argument("--playlist-id", required=True, help="Playlist id (base62) or spotify:playlist:... or https URL")
    args = parser.parse_args()

    try:
        session_cls, playlist_id_cls = _load_librespot()
    except Exception as exc:
        write_result(False, error=str(exc))
        return 1

    credentials_path = resolve_credentials(args.credentials)
    if credentials_path is None:
        write_result(False, error="credentials_not_found")
        return 1

    playlist_arg = args.playlist_id.strip()
    if PLAYLIST_URI_PREFIX in playlist_arg:
        playlist_uri = playlist_arg
    elif "open.spotify.com/playlist/" in playlist_arg:
        playlist_uri = PLAYLIST_URI_PREFIX + playlist_arg.split("/playlist/")[1].split("?")[0].strip()
    else:
        playlist_uri = PLAYLIST_URI_PREFIX + playlist_arg

    try:
        playlist_id = playlist_id_cls.from_uri(playlist_uri)
    except Exception as exc:
        write_result(False, error=f"invalid_playlist_id: {exc}")
        return 1

    try:
        session = session_cls.Builder().stored_file(credentials_path.as_posix()).create()
    except Exception as exc:
        write_result(False, error=f"librespot_session_error: {exc}")
        return 1

    try:
        playlist = session.api().get_playlist(playlist_id)
        payload = _to_json_message(playlist)
        if payload is None:
            write_result(False, error="protobuf_json_unavailable")
            return 1
        write_result(True, payload=payload)
        return 0
    except Exception as exc:
        write_result(False, error=f"librespot_playlist_error: {exc}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
