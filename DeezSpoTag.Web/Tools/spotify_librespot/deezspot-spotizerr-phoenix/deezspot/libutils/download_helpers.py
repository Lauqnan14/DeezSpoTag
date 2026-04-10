from __future__ import annotations

from typing import Any, Callable


def _normalize_bitrate(bitrate: Any) -> str | None:
    if not bitrate:
        return None
    digits = "".join(ch for ch in str(bitrate) if ch.isdigit())
    return f"{digits}k" if digits else None


def resolve_deezer_summary_media(
    *,
    quality_download: str | None,
    convert_to: Any,
    bitrate: Any,
) -> tuple[str | None, str | None]:
    if convert_to:
        return str(convert_to).lower(), _normalize_bitrate(bitrate)

    qkey = (quality_download or "").upper()
    if qkey.startswith("MP3"):
        bitrate_label = None
        try:
            bitrate_part = qkey.split("_", 1)[1]
            bitrate_label = f"{bitrate_part}k"
        except Exception:
            bitrate_label = None
        return "mp3", bitrate_label
    if qkey == "FLAC":
        return "flac", None
    return None, None


def resolve_spotify_summary_media(
    *,
    quality_download: str | None,
    convert_to: Any,
    bitrate: Any,
) -> tuple[str | None, str | None]:
    if convert_to:
        return str(convert_to).lower(), _normalize_bitrate(bitrate)

    bitrate_by_quality = {
        "NORMAL": "96k",
        "HIGH": "160k",
        "VERY_HIGH": "320k",
    }
    qkey = (quality_download or "NORMAL").upper()
    return "ogg", bitrate_by_quality.get(qkey)


def _playlist_title(preferences: Any) -> str:
    playlist_data = getattr(preferences, "json_data", None)
    if not playlist_data:
        return "unknown"
    if isinstance(playlist_data, dict):
        return playlist_data.get("name") or playlist_data.get("title") or "unknown"
    return getattr(playlist_data, "title", "unknown")


def _resolve_total_tracks(*, parent: str | None, preferences: Any, song_metadata: Any) -> int | None:
    if parent == "album":
        album = getattr(song_metadata, "album", None)
        total = getattr(album, "total_tracks", None)
        if isinstance(total, int) and total > 0:
            return total

    if parent == "playlist":
        playlist_data = getattr(preferences, "json_data", None)
        if isinstance(playlist_data, dict):
            total = playlist_data.get("tracks", {}).get("total")
            if isinstance(total, int) and total > 0:
                return total

    fallback_total = getattr(preferences, "total_tracks", None)
    if isinstance(fallback_total, int) and fallback_total > 0:
        return fallback_total
    return None


def _resolve_pad_width(*, parent: str | None, preferences: Any, song_metadata: Any) -> int | None:
    try:
        pad_width = getattr(preferences, "pad_number_width", None)
        if isinstance(pad_width, int) and pad_width >= 1:
            return pad_width
        if isinstance(pad_width, str) and pad_width.lower() == "auto":
            total_tracks = _resolve_total_tracks(parent=parent, preferences=preferences, song_metadata=song_metadata)
            if isinstance(total_tracks, int) and total_tracks > 0:
                return max(2, len(str(total_tracks)))
    except Exception:
        return None
    return None


def build_song_path_with_preferences(
    *,
    set_path_func: Callable[..., str],
    song_metadata_dict: dict,
    output_dir: str,
    song_quality: str,
    file_format: str,
    preferences: Any,
    parent: str | None,
    song_metadata: Any = None,
) -> str:
    song_metadata_dict["artist_separator"] = getattr(preferences, "artist_separator", "; ")
    if parent == "playlist":
        song_metadata_dict["playlist"] = _playlist_title(preferences)
        song_metadata_dict["playlistnum"] = getattr(preferences, "track_number", None) or 0

    return set_path_func(
        song_metadata_dict,
        output_dir,
        song_quality,
        file_format,
        custom_dir_format=getattr(preferences, "custom_dir_format", None),
        custom_track_format=getattr(preferences, "custom_track_format", None),
        pad_tracks=getattr(preferences, "pad_tracks", True),
        pad_number_width=_resolve_pad_width(parent=parent, preferences=preferences, song_metadata=song_metadata),
    )
