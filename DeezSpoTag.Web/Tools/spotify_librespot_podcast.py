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
    vendor_root = script_path.parent / "spotify_librespot"
    librespot_root = vendor_root / "spotizerr-phoenix"
    deezspot_root = vendor_root / "deezspot-spotizerr-phoenix"
    crypto_shim_root = vendor_root / "crypto_shim"
    if librespot_root.is_dir():
        sys.path.insert(0, str(librespot_root))
    if deezspot_root.is_dir():
        sys.path.insert(0, str(deezspot_root))
    if crypto_shim_root.is_dir():
        sys.path.insert(0, str(crypto_shim_root))

    try:
        librespot_module = deezspot_root / "deezspot" / "libutils" / "librespot.py"
        if not librespot_module.is_file():
            raise FileNotFoundError(f"librespot client module not found at {librespot_module}")
        import importlib.util

        spec = importlib.util.spec_from_file_location("deezspot_librespot", librespot_module)
        if spec is None or spec.loader is None:
            raise RuntimeError("failed to load librespot client module spec")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return getattr(module, "LibrespotClient")
    except Exception as exc:
        raise RuntimeError(f"librespot client loader failed: {exc}") from exc


def _is_valid_spotify_id(value):
    if not value or len(value) != 22:
        return False
    return value.isalnum()


def _fetch_show_payload(client, show_id):
    from librespot.metadata import ShowId  # type: ignore

    show = client._session.api().get_metadata_4_show(ShowId.from_base62(show_id))
    return client._proto_to_full_json(show)


def _fetch_episode_payload(client, episode_id):
    # Work around upstream EpisodeId URI casing bug by querying ext metadata with a lowercase URI.
    from librespot.proto.ExtensionKind_pb2 import ExtensionKind  # type: ignore
    from librespot.proto import Metadata_pb2 as Metadata  # type: ignore

    metadata_bytes = client._session.api().get_ext_metadata(
        ExtensionKind.EPISODE_V4,
        f"spotify:episode:{episode_id}",
    )
    episode = Metadata.Episode()
    episode.ParseFromString(metadata_bytes)
    return client._proto_to_full_json(episode)


def main():
    parser = argparse.ArgumentParser(description="Fetch Spotify show/episode metadata via librespot burst endpoints.")
    parser.add_argument("--credentials", required=True, help="Path to librespot credentials.json")
    parser.add_argument("--type", required=True, choices=["show", "episode"], help="Metadata type")
    parser.add_argument("--id", required=True, help="Spotify show/episode ID (base62)")
    args = parser.parse_args()

    try:
        LibrespotClient = _load_librespot()
    except Exception as exc:
        _write_result(False, error=str(exc))
        return 1

    credentials_path = pathlib.Path(args.credentials).expanduser().resolve()
    if not credentials_path.exists():
        _write_result(False, error="credentials_not_found")
        return 1

    spotify_id = args.id.strip()
    if not _is_valid_spotify_id(spotify_id):
        _write_result(False, error="invalid_spotify_id")
        return 1

    client = None
    try:
        client = LibrespotClient(stored_credentials_path=credentials_path.as_posix(), max_workers=2)
        if args.type == "show":
            payload = _fetch_show_payload(client, spotify_id)
        else:
            payload = _fetch_episode_payload(client, spotify_id)
        _write_result(True, payload=payload)
        return 0
    except Exception as exc:
        _write_result(False, error=f"librespot_{args.type}_error: {exc}")
        return 1
    finally:
        try:
            close = getattr(client, "close", None)
            if callable(close):
                close()
        except Exception:
            pass


if __name__ == "__main__":
    raise SystemExit(main())
