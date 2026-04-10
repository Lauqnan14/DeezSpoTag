#!/usr/bin/env python3
import importlib.util
import json
import pathlib
import sys


def write_result(ok, payload=None, error=None):
    body = {"ok": ok}
    if payload is not None:
        body["payload"] = payload
    if error is not None:
        body["error"] = error
    print(json.dumps(body))


def resolve_credentials(credentials):
    credentials_path = pathlib.Path(credentials).expanduser().resolve()
    if not credentials_path.exists():
        return None
    return credentials_path


def _prepend_path(path):
    path_text = str(path)
    if path.is_dir() and path_text not in sys.path:
        sys.path.insert(0, path_text)


def _vendor_roots():
    script_path = pathlib.Path(__file__).resolve()
    vendor_root = script_path.parent / "spotify_librespot"
    return {
        "vendor_root": vendor_root,
        "librespot_root": vendor_root / "spotizerr-phoenix",
        "deezspot_root": vendor_root / "deezspot-spotizerr-phoenix",
        "crypto_shim_root": vendor_root / "crypto_shim",
    }


def ensure_vendor_paths(include_deezspot=True, include_crypto=True):
    roots = _vendor_roots()
    _prepend_path(roots["librespot_root"])
    if include_deezspot:
        _prepend_path(roots["deezspot_root"])
    if include_crypto:
        _prepend_path(roots["crypto_shim_root"])
    return roots


def load_deezspot_librespot_client():
    roots = ensure_vendor_paths(include_deezspot=True, include_crypto=True)
    librespot_module = roots["deezspot_root"] / "deezspot" / "libutils" / "librespot.py"
    if not librespot_module.is_file():
        raise RuntimeError(f"librespot client module not found at {librespot_module}")
    spec = importlib.util.spec_from_file_location("deezspot_librespot", librespot_module)
    if spec is None or spec.loader is None:
        raise RuntimeError("failed to load librespot client module spec")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return getattr(module, "LibrespotClient")


def close_if_possible(target):
    close = getattr(target, "close", None)
    if callable(close):
        close()


def parse_csv_values(text):
    if not text:
        return []
    return [item.strip() for item in text.split(",") if item.strip()]


def is_valid_spotify_id(value):
    if not value or len(value) != 22:
        return False
    return value.isalnum()
