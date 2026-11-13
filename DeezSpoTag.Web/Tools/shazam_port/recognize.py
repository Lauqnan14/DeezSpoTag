#!/usr/bin/env python3
"""Recognize audio with the modern shazamio_core runtime."""

from __future__ import annotations

import argparse
import asyncio
import json
import sys
import time
from typing import Any, Dict, Optional
from urllib.parse import parse_qs, urlsplit

_stage = "startup"
_stage_started = time.monotonic()
_timings: Dict[str, float] = {}


def mark_stage(stage: str) -> None:
    global _stage, _stage_started
    now = time.monotonic()
    _timings[_stage] = now - _stage_started
    _stage, _stage_started = stage, now
    print(json.dumps({"shazamStage": stage, "stage": stage, "timings": _timings}), file=sys.stderr, flush=True)


def first_non_empty(*values: Optional[str]) -> Optional[str]:
    for value in values:
        if value is None:
            continue
        text = str(value).strip()
        if text:
            return text
    return None


def apple_music_url(value: Any) -> Optional[str]:
    # Preserve the association supplied by Shazam; never substitute the Shazam key
    # for Apple's separate numeric song namespace.
    if isinstance(value, str):
        try:
            parsed = urlsplit(value)
            if parsed.scheme == "https" and parsed.hostname == "music.apple.com":
                song_id = parse_qs(parsed.query).get("i", [""])[0]
                parts = parsed.path.strip("/").split("/")
                direct_song = any(part == "song" and index + 1 < len(parts)
                                  and parts[index + 1].isascii() and parts[index + 1].isdigit()
                                  for index, part in enumerate(parts))
                if (song_id.isascii() and song_id.isdigit()) or direct_song:
                    return value
        except ValueError:
            return None
    elif isinstance(value, dict):
        for child in value.values():
            found = apple_music_url(child)
            if found:
                return found
    elif isinstance(value, list):
        for child in value:
            found = apple_music_url(child)
            if found:
                return found
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
        "appleMusicUrl": apple_music_url(track),
        "url": first_non_empty(track.get("url"), share.get("href") if isinstance(share, dict) else None),
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Recognize audio with shazamio_core")
    parser.add_argument("audio_path", help="Path to an audio file")
    parser.add_argument("--language", default="en-US")
    parser.add_argument("--country", default="US")
    parser.add_argument("--timeout", type=float, default=25, help="Shazam HTTP request budget in seconds")
    parser.add_argument("--fingerprint-timeout", type=float, default=30)
    parser.add_argument(
        "--max-retries",
        type=int,
        default=6,
        help="HTTP retry attempts. Keep low for interactive capture so the whole "
             "recognition fits inside the caller's process timeout.",
    )
    parser.add_argument("--signature-seconds", type=int, default=10)
    return parser.parse_args()


async def recognize_async(args: argparse.Namespace) -> Dict[str, Any]:
    # Imports belong to startup, not the fingerprint or network request budget.
    from aiohttp_retry import ExponentialRetry
    from shazamio import HTTPClient, SearchParams, Shazam

    signature_seconds = max(3, min(20, int(args.signature_seconds)))
    timeout_seconds = max(.01, float(args.timeout))
    max_retries = max(1, min(12, int(args.max_retries)))

    shazam = Shazam(
        language=args.language,
        endpoint_country=args.country,
        http_client=HTTPClient(
            retry_options=ExponentialRetry(
                # Cap the backoff below the overall deadline: a sleep longer than the
                # budget can only ever end in a timeout, never in a result.
                attempts=max_retries,
                max_timeout=max(1, timeout_seconds // 2),
                statuses={429, 500, 502, 503, 504},
            ),
        ),
        segment_duration_seconds=signature_seconds,
    )

    mark_stage("fingerprint")
    try:
        signature = await asyncio.wait_for(
            shazam.core_recognizer.recognize_path(
                value=args.audio_path,
                options=SearchParams(segment_duration_seconds=signature_seconds),
            ),
            timeout=max(.01, float(args.fingerprint_timeout)),
        )
    except asyncio.TimeoutError as ex:
        raise RuntimeError(f"Shazam fingerprint timeout after {args.fingerprint_timeout:g}s") from ex

    mark_stage("request")
    try:
        response = await asyncio.wait_for(
            shazam.send_recognize_request_v2(sig=signature),
            timeout=timeout_seconds,
        )
    except asyncio.TimeoutError as ex:
        raise RuntimeError(f"Shazam request timeout after {timeout_seconds:g}s") from ex

    mark_stage("complete")

    matches = response.get("matches") if isinstance(response, dict) else None
    matched = isinstance(matches, list) and len(matches) > 0

    return {
        "ok": True,
        "stage": "complete",
        "timings": _timings,
        "matched": matched,
        "matchesCount": len(matches) if isinstance(matches, list) else 0,
        "summary": summarize_response(response),
    }


def main() -> int:
    args = parse_args()

    try:
        result = asyncio.run(recognize_async(args))
        print(json.dumps(result, ensure_ascii=False))
        return 0
    except Exception as ex:  # pragma: no cover - CLI error surface
        _timings[_stage] = time.monotonic() - _stage_started
        print(json.dumps({"ok": False, "error": f"Shazam {_stage} failed: {ex}", "stage": _stage, "timings": _timings}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
