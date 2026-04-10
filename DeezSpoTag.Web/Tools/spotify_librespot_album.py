#!/usr/bin/env python3
from spotify_librespot_entity import run_entity_query


def _add_arguments(parser):
    parser.add_argument("--include-tracks", action="store_true", help="Expand album tracks")


def _fetch_payload(client, args):
    return client.get_album(args.album_id, include_tracks=args.include_tracks)


def main():
    return run_entity_query(
        description="Fetch Spotify album metadata via librespot.",
        id_argument="album-id",
        id_help="Spotify album ID/URI/URL",
        fetch_payload=_fetch_payload,
        error_prefix="librespot_album_error",
        add_arguments=_add_arguments,
    )


if __name__ == "__main__":
    raise SystemExit(main())
