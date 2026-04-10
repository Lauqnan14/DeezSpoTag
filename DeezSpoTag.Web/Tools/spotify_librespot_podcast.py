#!/usr/bin/env python3
import argparse
from spotify_librespot_common import close_if_possible
from spotify_librespot_common import is_valid_spotify_id
from spotify_librespot_common import load_deezspot_librespot_client
from spotify_librespot_common import resolve_credentials
from spotify_librespot_common import write_result


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
    exit_code = 1

    try:
        librespot_client = load_deezspot_librespot_client()
    except Exception as exc:
        write_result(False, error=f"librespot client loader failed: {exc}")
        return exit_code

    credentials_path = resolve_credentials(args.credentials)
    if credentials_path is None:
        write_result(False, error="credentials_not_found")
        return exit_code

    spotify_id = args.id.strip()
    if not is_valid_spotify_id(spotify_id):
        write_result(False, error="invalid_spotify_id")
        return exit_code

    client = None
    try:
        client = librespot_client(stored_credentials_path=credentials_path.as_posix(), max_workers=2)
        if args.type == "show":
            payload = _fetch_show_payload(client, spotify_id)
        else:
            payload = _fetch_episode_payload(client, spotify_id)
        write_result(True, payload=payload)
        exit_code = 0
    except Exception as exc:
        write_result(False, error=f"librespot_{args.type}_error: {exc}")
    finally:
        try:
            close_if_possible(client)
        except Exception:
            pass
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
