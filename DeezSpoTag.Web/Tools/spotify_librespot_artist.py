#!/usr/bin/env python3
from spotify_librespot_entity import run_entity_query


def _fetch_payload(client, args):
    return client.get_artist(args.artist_id)


def main():
    return run_entity_query(
        description="Fetch Spotify artist metadata via librespot.",
        id_argument="artist-id",
        id_help="Spotify artist ID/URI/URL",
        fetch_payload=_fetch_payload,
        error_prefix="librespot_artist_error",
    )


if __name__ == "__main__":
    raise SystemExit(main())
