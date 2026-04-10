#!/usr/bin/env python3
import argparse
import json
import pathlib
import time
from spotify_librespot_common import ensure_vendor_paths
from spotify_librespot_common import resolve_credentials


def _write_result(ok, access_token=None, expires_at_unix_ms=None, error=None):
    payload = {"ok": ok}
    if access_token:
        payload["access_token"] = access_token
    if expires_at_unix_ms is not None:
        payload["expires_at_unix_ms"] = int(expires_at_unix_ms)
    if error:
        payload["error"] = error
    print(json.dumps(payload))


def _load_librespot():
    ensure_vendor_paths(include_deezspot=False, include_crypto=False)
    try:
        from librespot.core import Session  # type: ignore
    except Exception as exc:
        raise RuntimeError("spotizerr-phoenix librespot vendor is not available") from exc
    return Session


def _create_session(session_cls, credentials_path, max_retries=3):
    """Create librespot session with retry logic for connection failures."""
    conf = (
        session_cls.Configuration.Builder()
        .set_stored_credential_file(str(credentials_path))
        .build()
    )

    last_error = None
    for attempt in range(max_retries):
        try:
            builder = session_cls.Builder(conf)
            builder.stored_file(str(credentials_path))
            session = builder.create()
            # Verify session is valid
            if hasattr(session, 'is_valid') and not session.is_valid():
                raise RuntimeError("Session created but not valid")
            return session
        except Exception as e:
            last_error = e
            if attempt < max_retries - 1:
                delay = (attempt + 1) * 2  # 2s, 4s
                time.sleep(delay)
            continue

    raise RuntimeError(f"Failed to create session after {max_retries} attempts: {last_error}")


def main():
    parser = argparse.ArgumentParser(description="Fetch Spotify access token via librespot credentials.")
    parser.add_argument("--credentials", required=True, help="Path to librespot credentials.json")
    parser.add_argument("--scopes", nargs="+", default=["playlist-read"], help="Token scopes")
    args = parser.parse_args()

    credentials_path = resolve_credentials(args.credentials)
    if credentials_path is None:
        _write_result(False, error="credentials_not_found")
        return 1

    try:
        session_cls = _load_librespot()
    except Exception as exc:
        _write_result(False, error=str(exc))
        return 1

    try:
        session = _create_session(session_cls, credentials_path)
    except Exception as exc:
        _write_result(False, error=f"librespot_session_error: {exc}")
        return 1

    try:
        # Token retrieval with retry logic
        max_token_retries = 3
        last_token_error = None

        for token_attempt in range(max_token_retries):
            try:
                provider = session.tokens()
                token_obj = None
                access_token = None
                expires_at_ms = None

                if hasattr(provider, "get_token"):
                    token_obj = provider.get_token(*args.scopes)
                    access_token = getattr(token_obj, "access_token", None)
                    expires_in = getattr(token_obj, "expires_in", None)
                    if expires_in is None:
                        expires_in = getattr(token_obj, "expires_in_s", None)
                    if expires_in is not None:
                        expires_at_ms = int((time.time() + float(expires_in)) * 1000)
                if access_token is None and hasattr(provider, "get"):
                    access_token = provider.get(args.scopes[0])

                if access_token:
                    _write_result(True, access_token=access_token, expires_at_unix_ms=expires_at_ms)
                    return 0

                # No token received, but no exception - try reconnecting session
                if token_attempt < max_token_retries - 1 and hasattr(session, "reconnect"):
                    session.reconnect()
                    time.sleep(2)
                    continue

            except Exception as exc:
                last_token_error = exc
                if token_attempt < max_token_retries - 1:
                    # Try to reconnect session on error
                    try:
                        if hasattr(session, "reconnect"):
                            session.reconnect()
                    except Exception:
                        pass
                    time.sleep(2)
                    continue

        error_msg = f"librespot_token_error: {last_token_error}" if last_token_error else "librespot_token_unavailable"
        _write_result(False, error=error_msg)
        return 1
    finally:
        try:
            session.close()
        except Exception:
            pass


if __name__ == "__main__":
    raise SystemExit(main())
