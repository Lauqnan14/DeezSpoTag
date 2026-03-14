#!/usr/bin/env python3
import argparse
import json
import os
from pathlib import Path


def iter_pathfinder_payloads(har_data):
    entries = har_data.get("log", {}).get("entries", [])
    for entry in entries:
        request = entry.get("request", {})
        url = request.get("url", "")
        if "api-partner.spotify.com/pathfinder/v2/query" not in url:
            continue
        post = request.get("postData", {})
        text = post.get("text")
        if text:
            try:
                payload = json.loads(text)
            except json.JSONDecodeError:
                continue
        else:
            params = post.get("params", [])
            if not params:
                continue
            payload = {p.get("name"): p.get("value") for p in params}
            if "text" in payload:
                try:
                    payload = json.loads(payload["text"])
                except json.JSONDecodeError:
                    continue
        if isinstance(payload, list):
            for item in payload:
                if isinstance(item, dict):
                    yield item
        elif isinstance(payload, dict):
            yield payload


def extract_operations(har_data):
    operations = {}
    for payload in iter_pathfinder_payloads(har_data):
        operation = payload.get("operationName")
        persisted = payload.get("extensions", {}).get("persistedQuery", {})
        sha = persisted.get("sha256Hash")
        version = persisted.get("version", 1)
        if not operation or not sha:
            continue
        entry = {
            "version": int(version),
            "sha256Hash": sha
        }
        variables = payload.get("variables")
        if isinstance(variables, dict):
            entry["variables"] = variables
        operations[operation] = entry
    return operations


def resolve_output_path(explicit_path):
    if explicit_path:
        return Path(explicit_path)
    config_dir = os.getenv("DEEZSPOTAG_CONFIG_DIR") or os.getenv("DEEZSPOTAG_DATA_DIR")
    if not config_dir:
        raise SystemExit("DEEZSPOTAG_CONFIG_DIR or DEEZSPOTAG_DATA_DIR is not set.")
    return Path(config_dir) / "spotify" / "pathfinder-hashes.json"


def main():
    parser = argparse.ArgumentParser(description="Extract Spotify Pathfinder persisted query hashes from a HAR file.")
    parser.add_argument("har", help="Path to HAR file exported from browser DevTools.")
    parser.add_argument("--out", help="Output JSON path. Defaults to $DEEZSPOTAG_CONFIG_DIR/spotify/pathfinder-hashes.json")
    args = parser.parse_args()

    with open(args.har, "r", encoding="utf-8") as f:
        har_data = json.load(f)

    ops = extract_operations(har_data)
    if not ops:
        raise SystemExit("No Pathfinder operations found in HAR.")

    out_path = resolve_output_path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)

    existing = {}
    if out_path.exists():
        try:
            with open(out_path, "r", encoding="utf-8") as f:
                existing = json.load(f)
        except json.JSONDecodeError:
            existing = {}

    existing_ops = existing.get("operations", {}) if isinstance(existing, dict) else {}
    merged_ops = {**existing_ops, **ops}

    payload = {"operations": merged_ops}
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(payload, f, indent=2, sort_keys=True)
        f.write("\n")

    print(f"Wrote {len(ops)} operations to {out_path}")


if __name__ == "__main__":
    main()
