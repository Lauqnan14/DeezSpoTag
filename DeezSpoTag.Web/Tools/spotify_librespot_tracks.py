#!/usr/bin/env python3
import argparse
from concurrent.futures import ThreadPoolExecutor
from spotify_librespot_common import close_if_possible
from spotify_librespot_common import load_deezspot_librespot_client
from spotify_librespot_common import parse_csv_values
from spotify_librespot_common import resolve_credentials
from spotify_librespot_common import write_result


def main():
    parser = argparse.ArgumentParser(description="Fetch Spotify track metadata via librespot.")
    parser.add_argument("--credentials", required=True, help="Path to librespot credentials.json")
    parser.add_argument("--track-ids", required=True, help="Comma-separated Spotify track IDs")
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

    ids = parse_csv_values(args.track_ids)
    if not ids:
        write_result(False, error="missing_track_ids")
        return 1

    worker_count = min(5, len(ids))
    try:
        client = librespot_client(stored_credentials_path=credentials_path.as_posix(), max_workers=worker_count)
    except Exception as exc:
        write_result(False, error=f"librespot_client_error: {exc}")
        return 1

    def fetch_track(track_id):
        try:
            track = client.get_track(track_id)
            if not track:
                return {"id": track_id, "error": "librespot_track_empty"}
            return {"id": track_id, "track": track}
        except Exception as exc:
            return {"id": track_id, "error": f"librespot_track_error: {exc}"}

    with ThreadPoolExecutor(max_workers=worker_count) as executor:
        results = list(executor.map(fetch_track, ids))

    try:
        close_if_possible(client)
    except Exception:
        pass

    write_result(True, payload=results)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
