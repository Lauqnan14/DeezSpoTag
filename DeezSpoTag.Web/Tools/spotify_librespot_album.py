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


def main():
    parser = argparse.ArgumentParser(description="Fetch Spotify album metadata via librespot.")
    parser.add_argument("--credentials", required=True, help="Path to librespot credentials.json")
    parser.add_argument("--album-id", required=True, help="Spotify album ID/URI/URL")
    parser.add_argument("--include-tracks", action="store_true", help="Expand album tracks")
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

    try:
        client = LibrespotClient(stored_credentials_path=credentials_path.as_posix(), max_workers=2)
    except Exception as exc:
        _write_result(False, error=f"librespot_client_error: {exc}")
        return 1

    try:
        payload = client.get_album(args.album_id, include_tracks=args.include_tracks)
    except Exception as exc:
        _write_result(False, error=f"librespot_album_error: {exc}")
        try:
            close = getattr(client, "close", None)
            if callable(close):
                close()
        except Exception:
            pass
        return 1

    try:
        close = getattr(client, "close", None)
        if callable(close):
            close()
    except Exception:
        pass

    _write_result(True, payload=payload)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
