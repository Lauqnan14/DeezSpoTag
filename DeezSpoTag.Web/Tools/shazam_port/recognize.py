#!/usr/bin/env python3
"""Ported ShazamIO recognition flow: local signature generation + recognize request."""

from __future__ import annotations

import argparse
import json
import random
import subprocess
import sys
import time
import uuid
from pathlib import Path
from typing import Any, Dict, Optional
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

import numpy as np

SCRIPT_DIR = Path(__file__).resolve().parent
PARENT_DIR = SCRIPT_DIR.parent
if str(PARENT_DIR) not in sys.path:
    sys.path.insert(0, str(PARENT_DIR))

from shazam_port.algorithm import SignatureGenerator  # noqa: E402


SEARCH_FROM_FILE_URL = (
    "https://amp.shazam.com/discovery/v5/{language}/{endpoint_country}/{device}/-/tag/"
    "{uuid_1}/{uuid_2}?sync=true&webv3=true&sampling=true"
    "&connected=&shazamapiversion=v3&sharehub=true&hubv5minorversion=v5.1&hidelb=true&video=v3"
)


def first_non_empty(*values: Optional[str]) -> Optional[str]:
    for value in values:
        if value is None:
            continue
        text = str(value).strip()
        if text:
            return text
    return None


def decode_audio_samples(path: str, ffmpeg: str) -> np.ndarray:
    cmd = [
        ffmpeg,
        "-v",
        "error",
        "-i",
        path,
        "-f",
        "s16le",
        "-acodec",
        "pcm_s16le",
        "-ac",
        "1",
        "-ar",
        "16000",
        "-",
    ]

    process = subprocess.run(cmd, capture_output=True, check=False)
    if process.returncode != 0:
        stderr = process.stderr.decode("utf-8", errors="replace").strip()
        raise RuntimeError(f"ffmpeg failed: {stderr or 'unknown error'}")

    if not process.stdout:
        raise RuntimeError("ffmpeg produced empty audio output")

    return np.frombuffer(process.stdout, dtype="<i2")


def generate_signature_uri(samples: np.ndarray) -> Optional[Dict[str, Any]]:
    signature_generator = SignatureGenerator()
    signature_generator.feed_input(samples.tolist())
    signature_generator.MAX_TIME_SECONDS = 12

    duration_seconds = len(samples) / 16000.0
    if duration_seconds > 12 * 3:
        signature_generator.samples_processed += 16000 * (int(duration_seconds / 2) - 6)

    signature = signature_generator.get_next_signature()
    if signature is None:
        return None

    sample_ms = int(signature.number_samples / signature.sample_rate_hz * 1000)
    return {
        "uri": signature.encode_to_uri(),
        "samplems": sample_ms,
    }


def send_recognize_request(
    signature_payload: Dict[str, Any],
    language: str,
    endpoint_country: str,
    timezone: str,
    timeout_seconds: int,
) -> Dict[str, Any]:
    payload = {
        "timezone": timezone,
        "signature": signature_payload,
        "timestamp": int(time.time() * 1000),
        "context": {},
        "geolocation": {},
    }

    url = SEARCH_FROM_FILE_URL.format(
        language=language,
        endpoint_country=endpoint_country,
        device=random.choice(["iphone", "android", "web"]),
        uuid_1=str(uuid.uuid4()).upper(),
        uuid_2=str(uuid.uuid4()).upper(),
    )

    body = json.dumps(payload).encode("utf-8")
    headers = {
        "Content-Type": "application/json",
        "Accept": "*/*",
        "Accept-Language": language,
        "User-Agent": (
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 "
            "(KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"
        ),
        "X-Shazam-Platform": "IPHONE",
        "X-Shazam-AppVersion": "14.1.0",
    }

    request = Request(url=url, data=body, headers=headers, method="POST")

    try:
        with urlopen(request, timeout=timeout_seconds) as response:
            raw = response.read().decode("utf-8", errors="replace")
            return json.loads(raw)
    except HTTPError as ex:
        detail = ex.read().decode("utf-8", errors="replace").strip()
        raise RuntimeError(f"Shazam HTTP {ex.code}: {detail}") from ex
    except URLError as ex:
        raise RuntimeError(f"Shazam request failed: {ex.reason}") from ex


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
    parser = argparse.ArgumentParser(description="Recognize audio with ported Shazam reference implementation")
    parser.add_argument("audio_path", help="Path to an audio file")
    parser.add_argument("--language", default="en-US")
    parser.add_argument("--country", default="US")
    parser.add_argument("--timezone", default="Etc/UTC")
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--timeout", type=int, default=25)
    return parser.parse_args()


def main() -> int:
    args = parse_args()

    try:
        samples = decode_audio_samples(args.audio_path, args.ffmpeg)
        signature_payload = generate_signature_uri(samples)
        if signature_payload is None:
            print(json.dumps({"ok": True, "summary": {}, "response": {"matches": []}}))
            return 0

        response = send_recognize_request(
            signature_payload=signature_payload,
            language=args.language,
            endpoint_country=args.country,
            timezone=args.timezone,
            timeout_seconds=max(5, int(args.timeout)),
        )

        result = {
            "ok": True,
            "summary": summarize_response(response),
            "response": response,
        }
        print(json.dumps(result, ensure_ascii=False))
        return 0
    except Exception as ex:  # pragma: no cover - CLI error surface
        print(json.dumps({"ok": False, "error": str(ex)}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
