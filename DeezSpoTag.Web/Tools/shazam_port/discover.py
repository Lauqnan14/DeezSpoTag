#!/usr/bin/env python3
from __future__ import annotations

import argparse
import asyncio
import json
from typing import Any, Dict

from shazamio import Shazam

# shazamio's ShazamUrl.ABOUT_TRACK points the track lookup at
# www.shazam.com/discovery/v5/{lang}/{country}/web/-/track/{id}, which answers 404 with
# the single-page-app shell, so track_about() fails JSON decoding on every call and the
# matched track never resolves. This is not fixed in any published release: 0.8.0 and
# 0.8.1 are byte-identical on PyPI, and the repair only exists in an unreleased checkout
# whose pyproject still reads 0.8.1. Until that ships, call the route directly. The host,
# path and device segment match that checkout's ABOUT_TRACK, which documents why `iphone`
# and not the others: `web` omits hub.providers (the Spotify and Deezer links) from every
# track it answers with, and `android` returns apple_music_url as an unopenable intent://
# deep link. `web` is the segment the installed RELATED_SONGS still uses.
TRACK_LOOKUP_URL = (
    "https://amp.shazam.com/discovery/v5/{language}/{country}/{device}/-/track"
    "/{track_id}?shazamapiversion=v3&video=v3"
)
TRACK_LOOKUP_DEVICE = "iphone"


async def fetch_track(shazam: Shazam, track_id: int) -> Dict[str, Any]:
    """Stand-in for Shazam.track_about until the published URL is corrected."""
    url = TRACK_LOOKUP_URL.format(
        language=shazam.language,
        country=shazam.endpoint_country,
        device=TRACK_LOOKUP_DEVICE,
        track_id=track_id,
    )
    return await shazam.http_client.request("GET", url, headers=shazam.headers())


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Shazam discovery bridge")
    parser.add_argument("mode", choices=["track", "related", "search"])
    parser.add_argument("--track-id", default="")
    parser.add_argument("--query", default="")
    parser.add_argument("--limit", type=int, default=20)
    parser.add_argument("--offset", type=int, default=0)
    parser.add_argument("--language", default="en-US")
    parser.add_argument("--country", default="US")
    parser.add_argument("--timeout", type=int, default=20)
    return parser.parse_args()


async def run_async(args: argparse.Namespace) -> Dict[str, Any]:
    shazam = Shazam(language=args.language, endpoint_country=args.country)
    timeout = max(5, int(args.timeout))
    limit = max(1, min(50, int(args.limit)))
    offset = max(0, int(args.offset))

    if args.mode == "track":
        if not str(args.track_id).strip():
            return {"ok": True, "track": None}
        payload = await asyncio.wait_for(
            fetch_track(shazam, int(str(args.track_id).strip())), timeout=timeout
        )
        return {"ok": True, "track": payload}

    if args.mode == "related":
        if not str(args.track_id).strip():
            return {"ok": True, "tracks": []}
        payload = await asyncio.wait_for(
            shazam.related_tracks(track_id=int(str(args.track_id).strip()), limit=limit, offset=offset),
            timeout=timeout,
        )
        tracks = payload.get("tracks") if isinstance(payload, dict) else []
        return {"ok": True, "tracks": tracks if isinstance(tracks, list) else []}

    if not str(args.query).strip():
        return {"ok": True, "tracks": []}

    try:
        payload = await asyncio.wait_for(
            shazam.search_track(query=str(args.query).strip(), limit=limit, offset=offset),
            timeout=timeout,
        )
        tracks = payload.get("tracks") if isinstance(payload, dict) else []
        return {"ok": True, "tracks": tracks if isinstance(tracks, list) else []}
    except Exception:
        return {"ok": True, "tracks": []}


def main() -> int:
    args = parse_args()
    try:
        result = asyncio.run(run_async(args))
        print(json.dumps(result, ensure_ascii=False))
        return 0
    except Exception as ex:
        print(json.dumps({"ok": False, "error": str(ex)}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
