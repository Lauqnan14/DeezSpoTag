#!/usr/bin/env python3
import argparse
import json
import os
import pathlib
import random
import shutil
import string
import sys
import time
import logging
import traceback


logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s - %(message)s",
)


def _write_result(ok, output=None, username=None, error=None):
    payload = {"ok": ok}
    if output:
        payload["output"] = output
    if username:
        payload["username"] = username
    if error:
        payload["error"] = error
    print(json.dumps(payload))


def _load_librespot():
    script_path = pathlib.Path(__file__).resolve()
    librespot_root = script_path.parent / "spotify_librespot" / "spotizerr-phoenix"
    if librespot_root.is_dir():
        sys.path.insert(0, str(librespot_root))
    try:
        from librespot.zeroconf import ZeroconfServer  # type: ignore
    except Exception as exc:
        details = "".join(traceback.format_exception_only(type(exc), exc)).strip()
        raise RuntimeError(
            "spotizerr-phoenix librespot vendor is not available. "
            f"Import failure: {details}. "
            "Install its dependencies or ensure the vendored auth folder is present."
        ) from exc
    return ZeroconfServer


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Headless Spotify Connect credentials capture."
    )
    parser.add_argument("--output", required=True, help="Output credentials.json path")
    parser.add_argument("--device-name", default="DeezSpoTag", help="Spotify Connect device name")
    parser.add_argument("--timeout", type=int, default=90, help="Timeout in seconds")
    return parser


def _remove_existing_credentials_file(credential_file: pathlib.Path) -> str | None:
    if not credential_file.exists():
        return None
    try:
        credential_file.unlink()
    except OSError as exc:
        return f"Failed to remove existing credentials.json: {exc}"
    return None


def _create_zeroconf_server(zeroconf_server_cls, base_device_name: str):
    device_name = base_device_name
    for _ in range(5):
        try:
            server = zeroconf_server_cls.Builder().set_device_name(device_name).create()
            logging.info("Zeroconf server started with device name: %s", device_name)
            return server, None
        except Exception as exc:
            if "NonUniqueNameException" not in exc.__class__.__name__:
                return None, f"Failed to start Spotify Connect listener: {exc}"
            suffix = "".join(random.choices(string.ascii_uppercase + string.digits, k=4))
            device_name = f"{base_device_name}-{suffix}"
            time.sleep(0.2)
    return None, "Failed to allocate a unique Spotify Connect device name."


def _wait_for_credentials_file(server, credential_file: pathlib.Path, timeout_seconds: int) -> bool:
    deadline = time.time() + timeout_seconds
    session_seen_at = None
    while time.time() < deadline:
        session = getattr(server, "_ZeroconfServer__session", None)
        if session is not None:
            try:
                username = session.username()
            except Exception:
                username = "unknown"
            logging.info("Zeroconf session active for user: %s", username)
            if session_seen_at is None:
                session_seen_at = time.time()
                deadline = max(deadline, session_seen_at + 30)
        if credential_file.exists() and credential_file.stat().st_size > 0:
            return True
        time.sleep(0.5)
    return False


def _persist_credentials(
    credential_file: pathlib.Path, output_path: pathlib.Path
) -> tuple[str | None, str | None]:
    try:
        data = json.loads(credential_file.read_text(encoding="utf-8"))
        username = data.get("username")
        shutil.copy2(credential_file, output_path)
        return username, None
    except Exception as exc:
        return None, f"Failed to save credentials.json: {exc}"


def main():
    args = _build_parser().parse_args()

    output_path = pathlib.Path(args.output).expanduser().resolve()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    logging.info("Starting Spotify Zeroconf auth helper.")
    logging.info("Working directory: %s", pathlib.Path.cwd())
    logging.info("Output path: %s", output_path)

    credential_file = pathlib.Path.cwd() / "credentials.json"
    cleanup_error = _remove_existing_credentials_file(credential_file)
    if cleanup_error is not None:
        _write_result(False, error=cleanup_error)
        return 1

    try:
        zeroconf_server_cls = _load_librespot()
    except Exception as exc:
        _write_result(False, error=str(exc))
        return 1
    logging.getLogger("Librespot:ZeroconfServer").setLevel(logging.INFO)

    server, server_error = _create_zeroconf_server(zeroconf_server_cls, args.device_name)
    if server is None:
        _write_result(False, error=server_error)
        return 1

    try:
        if not _wait_for_credentials_file(server, credential_file, args.timeout):
            _write_result(False, error="Timeout waiting for Spotify Connect session.")
            return 1
        username, persist_error = _persist_credentials(credential_file, output_path)
        if persist_error is not None:
            _write_result(False, error=persist_error)
            return 1
    finally:
        server.close()

    _write_result(True, output=str(output_path), username=username)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
