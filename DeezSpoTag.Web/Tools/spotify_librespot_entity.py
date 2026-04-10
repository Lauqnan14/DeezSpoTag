#!/usr/bin/env python3
import argparse

from spotify_librespot_common import close_if_possible
from spotify_librespot_common import load_deezspot_librespot_client
from spotify_librespot_common import resolve_credentials
from spotify_librespot_common import write_result


def run_entity_query(*, description, id_argument, id_help, fetch_payload, error_prefix, add_arguments=None):
    parser = argparse.ArgumentParser(description=description)
    parser.add_argument("--credentials", required=True, help="Path to librespot credentials.json")
    parser.add_argument(f"--{id_argument}", required=True, help=id_help)
    if add_arguments is not None:
        add_arguments(parser)
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
    except Exception as exc:
        write_result(False, error=f"librespot_client_error: {exc}")
        return 1

    try:
        payload = fetch_payload(client, args)
    except Exception as exc:
        write_result(False, error=f"{error_prefix}: {exc}")
        try:
            close_if_possible(client)
        except Exception:
            pass
        return 1

    try:
        close_if_possible(client)
    except Exception:
        pass

    write_result(True, payload=payload)
    return 0
