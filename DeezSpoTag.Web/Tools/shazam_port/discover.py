#!/usr/bin/env python3
from __future__ import annotations

import argparse
import asyncio
import json
from typing import Any, Dict

from shazamio import Shazam


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
        payload = await asyncio.wait_for(shazam.track_about(track_id=int(str(args.track_id).strip())), timeout=timeout)
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
