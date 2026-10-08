#!/usr/bin/env python3
import argparse
import json
from datetime import datetime, timezone
from pathlib import Path


def _validate_day(date_key, payload):
    if not isinstance(date_key, str) or len(date_key) != 10:
        raise ValueError(f"Invalid day key: {date_key!r}")
    if not isinstance(payload, dict):
        raise ValueError(f"Invalid day payload for {date_key}")
    count = payload.get("count")
    uniques = payload.get("uniques")
    if not isinstance(count, int) or count < 0:
        raise ValueError(f"Invalid clone count for {date_key}: {count!r}")
    if not isinstance(uniques, int) or uniques < 0:
        raise ValueError(f"Invalid unique clone count for {date_key}: {uniques!r}")
    return {"count": count, "uniques": uniques}


def merge_history(existing, traffic, now=None):
    if not isinstance(existing, dict):
        raise ValueError("Existing history must be a JSON object")
    if not isinstance(traffic, dict) or not isinstance(traffic.get("clones"), list):
        raise ValueError("Traffic payload must contain a clones array")

    now = now or datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")

    old_days = {}
    for date_key, payload in (existing.get("days") or {}).items():
        old_days[date_key] = _validate_day(date_key, payload)

    merged_days = dict(old_days)
    for item in traffic["clones"]:
        if not isinstance(item, dict):
            raise ValueError("Each traffic clone entry must be an object")
        timestamp = item.get("timestamp")
        if not isinstance(timestamp, str) or len(timestamp) < 10:
            raise ValueError(f"Invalid clone timestamp: {timestamp!r}")
        date_key = timestamp[:10]
        merged_days[date_key] = _validate_day(
            date_key,
            {"count": item.get("count"), "uniques": item.get("uniques")},
        )

    merged_days = dict(sorted(merged_days.items()))
    changed = merged_days != dict(sorted(old_days.items()))

    if existing.get("started_at"):
        started_at = existing["started_at"]
    elif merged_days:
        started_at = next(iter(merged_days))
    else:
        started_at = now[:10]

    if changed or not existing.get("updated_at"):
        updated_at = now
    else:
        updated_at = existing["updated_at"]

    return {
        "schema_version": 1,
        "started_at": started_at,
        "updated_at": updated_at,
        "days": merged_days,
        "total_clones": sum(day["count"] for day in merged_days.values()),
    }


def make_badge(total_clones):
    if not isinstance(total_clones, int) or total_clones < 0:
        raise ValueError("total_clones must be a non-negative integer")
    return {
        "schemaVersion": 1,
        "label": "All-time clones",
        "message": f"{total_clones:,}",
        "color": "22c55e",
        "labelColor": "black",
        "namedLogo": "github",
        "logoColor": "white",
    }


def main():
    parser = argparse.ArgumentParser(description="Merge GitHub clone traffic into persistent all-time history")
    parser.add_argument("--traffic", required=True, help="GitHub traffic/clones JSON response")
    parser.add_argument("--history", required=True, help="Existing clone history JSON")
    parser.add_argument("--history-out", required=True, help="Path for merged history JSON")
    parser.add_argument("--badge-out", required=True, help="Path for Shields endpoint JSON")
    args = parser.parse_args()

    traffic = json.loads(Path(args.traffic).read_text(encoding="utf-8"))
    history_path = Path(args.history)
    if history_path.exists():
        existing = json.loads(history_path.read_text(encoding="utf-8"))
    else:
        existing = {"schema_version": 1, "days": {}}

    merged = merge_history(existing, traffic)
    badge = make_badge(merged["total_clones"])

    Path(args.history_out).write_text(json.dumps(merged, indent=2, sort_keys=False) + "\n", encoding="utf-8")
    Path(args.badge_out).write_text(json.dumps(badge, indent=2, sort_keys=False) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
