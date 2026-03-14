#!/usr/bin/env python3
import argparse
import json
import pathlib
import sys


def _write_result(ok, payload=None, error=None):
    body = {"ok": ok}
    if payload is not None:
        body["payload"] = payload
    if error is not None:
        body["error"] = error
    print(json.dumps(body))


def _load_librespot():
    script_path = pathlib.Path(__file__).resolve()
    librespot_root = script_path.parent / "spotify_librespot" / "spotizerr-phoenix"
    if librespot_root.is_dir():
        sys.path.insert(0, str(librespot_root))

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
        Session, PlaylistId = _load_librespot()
    except Exception as exc:
        _write_result(False, error=str(exc))
        return 1

    credentials_path = pathlib.Path(args.credentials).expanduser().resolve()
    if not credentials_path.exists():
        _write_result(False, error="credentials_not_found")
        return 1

    playlist_arg = args.playlist_id.strip()
    if "spotify:playlist:" in playlist_arg:
        playlist_uri = playlist_arg
    elif "open.spotify.com/playlist/" in playlist_arg:
        playlist_uri = "spotify:playlist:" + playlist_arg.split("/playlist/")[1].split("?")[0].strip()
    else:
        playlist_uri = "spotify:playlist:" + playlist_arg

    try:
        playlist_id = PlaylistId.from_uri(playlist_uri)
    except Exception as exc:
        _write_result(False, error=f"invalid_playlist_id: {exc}")
        return 1

    try:
        session = Session.Builder().stored_file(credentials_path.as_posix()).create()
    except Exception as exc:
        _write_result(False, error=f"librespot_session_error: {exc}")
        return 1

    try:
        playlist = session.api().get_playlist(playlist_id)
        payload = _to_json_message(playlist)
        if payload is None:
            _write_result(False, error="protobuf_json_unavailable")
            return 1
        _write_result(True, payload=payload)
        return 0
    except Exception as exc:
        _write_result(False, error=f"librespot_playlist_error: {exc}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
