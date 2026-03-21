#!/usr/bin/env python3
"""Recognize audio with the modern shazamio_core runtime."""

from __future__ import annotations

import argparse
import asyncio
import json
from typing import Any, Dict, Optional

from aiohttp_retry import ExponentialRetry
from shazamio import HTTPClient, SearchParams, Shazam


def first_non_empty(*values: Optional[str]) -> Optional[str]:
    for value in values:
        if value is None:
            continue
        text = str(value).strip()
        if text:
            return text
    return None


def summarize_response(response: Dict[str, Any]) -> Dict[str, Any]:
    track = response.get("track") if isinstance(response, dict) else None
    if not isinstance(track, dict):
        return {
            "trackId": None,
            "title": None,
            "artist": None,
            "isrc": None,
            "url": None,
        }

    share = track.get("share") if isinstance(track.get("share"), dict) else {}
    return {
        "trackId": first_non_empty(track.get("key"), track.get("id"), track.get("track_id"), track.get("trackId")),
        "title": first_non_empty(track.get("title"), track.get("name")),
        "artist": first_non_empty(track.get("subtitle"), track.get("artist")),
        "isrc": first_non_empty(track.get("isrc")),
        "url": first_non_empty(track.get("url"), share.get("href") if isinstance(share, dict) else None),
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Recognize audio with shazamio_core")
    parser.add_argument("audio_path", help="Path to an audio file")
    parser.add_argument("--language", default="en-US")
    parser.add_argument("--country", default="US")
    parser.add_argument("--timeout", type=int, default=25)
    parser.add_argument("--signature-seconds", type=int, default=12)
    return parser.parse_args()


async def recognize_async(args: argparse.Namespace) -> Dict[str, Any]:
    signature_seconds = max(3, min(20, int(args.signature_seconds)))
    timeout_seconds = max(5, int(args.timeout))

    shazam = Shazam(
        language=args.language,
        endpoint_country=args.country,
        http_client=HTTPClient(
            retry_options=ExponentialRetry(
                attempts=12,
                max_timeout=min(timeout_seconds * 2, 120),
                statuses={429, 500, 502, 503, 504},
            ),
        ),
        segment_duration_seconds=signature_seconds,
    )

    response = await shazam.recognize(
        args.audio_path,
        options=SearchParams(segment_duration_seconds=signature_seconds),
    )

    return {
        "ok": True,
        "summary": summarize_response(response),
        "response": response,
    }


def main() -> int:
    args = parse_args()

    try:
        result = asyncio.run(recognize_async(args))
        print(json.dumps(result, ensure_ascii=False))
        return 0
    except Exception as ex:  # pragma: no cover - CLI error surface
        print(json.dumps({"ok": False, "error": str(ex)}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
