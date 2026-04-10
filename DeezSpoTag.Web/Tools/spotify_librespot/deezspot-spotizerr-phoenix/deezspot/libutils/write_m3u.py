#!/usr/bin/python3

import os
from typing import List
from deezspot.libutils.utils import sanitize_name
from deezspot.libutils.logging_utils import logger
from deezspot.models.download import Track

M3U_HEADER = "#EXTM3U\n"
UNKNOWN_ARTIST = "Unknown Artist"
UNKNOWN_TITLE = "Unknown Title"


def create_m3u_file(output_dir: str, playlist_name: str) -> str:
    """
    Creates an m3u playlist file with the proper header.
    Returns full path to the m3u file.
    """
    playlist_m3u_dir = os.path.join(output_dir, "playlists")
    os.makedirs(playlist_m3u_dir, exist_ok=True)
    playlist_name_sanitized = sanitize_name(playlist_name)
    m3u_path = os.path.join(playlist_m3u_dir, f"{playlist_name_sanitized}.m3u")
    # Always ensure header exists (idempotent)
    if not os.path.exists(m3u_path):
        with open(m3u_path, "w", encoding="utf-8") as m3u_file:
            m3u_file.write(M3U_HEADER)
        logger.debug(f"Created m3u playlist file: {m3u_path}")
    return m3u_path


def ensure_m3u_header(m3u_path: str) -> None:
    """Ensure an existing m3u has the header; create if missing."""
    if not os.path.exists(m3u_path):
        os.makedirs(os.path.dirname(m3u_path), exist_ok=True)
        with open(m3u_path, "w", encoding="utf-8") as m3u_file:
            m3u_file.write(M3U_HEADER)


# Prefer the actual file that exists on disk; if the stored path doesn't exist,
# attempt to find the same basename with a different extension (e.g., due to conversion).
_AUDIO_EXTS_TRY = [
    ".flac", ".mp3", ".m4a", ".aac", ".alac", ".ogg", ".opus", ".wav", ".aiff"
]


def _resolve_existing_song_path(song_path: str) -> str | None:
    if not song_path:
        return None
    if os.path.exists(song_path):
        return song_path
    base, _ = os.path.splitext(song_path)
    for ext in _AUDIO_EXTS_TRY:
        candidate = base + ext
        if os.path.exists(candidate):
            return candidate
    return None


def _get_track_duration_seconds(track: Track) -> int:
    try:
        if hasattr(track, 'tags') and track.tags:
            if 'duration' in track.tags:
                return int(float(track.tags['duration']))
            elif 'length' in track.tags:
                return int(float(track.tags['length']))
        if hasattr(track, 'song_metadata') and hasattr(track.song_metadata, 'duration_ms'):
            return int(track.song_metadata.duration_ms / 1000)
        return 0
    except (ValueError, AttributeError, TypeError):
        return 0


def _get_track_info(track: Track) -> tuple:
    try:
        if hasattr(track, 'tags') and track.tags:
            artist = track.tags.get('artist', UNKNOWN_ARTIST)
            title = track.tags.get('music', track.tags.get('title', UNKNOWN_TITLE))
            return artist, title
        elif hasattr(track, 'song_metadata'):
            sep = ", "
            if hasattr(track, 'tags') and track.tags:
                sep = track.tags.get('artist_separator', sep)
            if hasattr(track.song_metadata, 'artists') and track.song_metadata.artists:
                artist = sep.join([a.name for a in track.song_metadata.artists])
            else:
                artist = UNKNOWN_ARTIST
            title = getattr(track.song_metadata, 'title', UNKNOWN_TITLE)
            return artist, title
        else:
            return UNKNOWN_ARTIST, UNKNOWN_TITLE
    except (AttributeError, TypeError):
        return UNKNOWN_ARTIST, UNKNOWN_TITLE


def _read_m3u_entries(m3u_path: str) -> List[tuple]:
    """Parse existing m3u into a list of (extinf_line, path_line) entries after header."""
    entries: List[tuple] = []
    if not os.path.exists(m3u_path):
        return entries
    try:
        with open(m3u_path, "r", encoding="utf-8") as f:
            lines = [line.rstrip('\n') for line in f]
    except OSError:
        return entries
    # Skip header if present
    idx = 0
    if idx < len(lines) and lines[idx].strip() == "#EXTM3U":
        idx += 1
    while idx < len(lines):
        line = lines[idx]
        if not line:
            idx += 1
            continue
        if line.startswith("#EXTINF:"):
            extinf = line
            path = ""
            if idx + 1 < len(lines):
                path = lines[idx + 1]
            entries.append((extinf, path))
            idx += 2
        else:
            # Path-only entry
            entries.append(("", line))
            idx += 1
    return entries


def _write_m3u_entries(m3u_path: str, entries: List[tuple]) -> None:
    """Write header and provided entries back to file."""
    # Ensure folder exists
    os.makedirs(os.path.dirname(m3u_path), exist_ok=True)
    with open(m3u_path, "w", encoding="utf-8") as m3u_file:
        m3u_file.write(M3U_HEADER)
        for extinf, path in entries:
            # Skip empty placeholders
            if not path and not extinf:
                continue
            if extinf:
                m3u_file.write(f"{extinf}\n")
            if path:
                m3u_file.write(f"{path}\n")

def _remove_entries_for_path(entries: List[tuple], relative_path: str) -> List[tuple]:
    return [(extinf, path) for (extinf, path) in entries if path != relative_path]

def _resolve_relative_path(playlist_m3u_dir: str, song_path: str) -> str | None:
    resolved = _resolve_existing_song_path(song_path)
    if not resolved:
        return None
    return os.path.relpath(resolved, start=playlist_m3u_dir)

def _extract_playlist_position(track: Track) -> int:
    if not (hasattr(track, 'tags') and track.tags):
        return 0
    raw_pos = track.tags.get('playlistnum')
    if raw_pos is None:
        return 0
    try:
        return int(raw_pos)
    except (ValueError, TypeError):
        return 0

def _upsert_entry(entries: List[tuple], new_entry: tuple, position: int) -> tuple[List[tuple], bool]:
    if position >= 1 and position - 1 < len(entries):
        if entries[position - 1] == new_entry:
            return entries, False
        updated = list(entries)
        updated[position - 1] = new_entry
        return updated, True
    if any(entry == new_entry for entry in entries):
        return entries, False
    updated = list(entries)
    updated.append(new_entry)
    return updated, True

def _is_valid_track(track: str | Track) -> bool:
    return isinstance(track, Track) and track.success and hasattr(track, 'song_path')

def _build_track_entry(track: Track, playlist_m3u_dir: str) -> tuple[tuple, str] | None:
    relative_path = _resolve_relative_path(playlist_m3u_dir, track.song_path)
    if not relative_path:
        return None
    duration = _get_track_duration_seconds(track)
    artist, title = _get_track_info(track)
    extinf_line = f"#EXTINF:{duration},{artist} - {title}"
    return (extinf_line, relative_path), relative_path


def append_track_to_m3u(m3u_path: str, track: str | Track) -> None:
    """Append a single track to m3u with EXTINF and a resolved path.
    Idempotent behavior: if entry for same path exists, it is updated/moved to the desired position; if an entry exists at the desired position but differs, it is overwritten.
    """
    ensure_m3u_header(m3u_path)
    # Prepare entries and base dir
    playlist_m3u_dir = os.path.dirname(m3u_path)
    entries = _read_m3u_entries(m3u_path)

    # Handle simple string path case: dedupe by path, append if new
    if isinstance(track, str):
        relative_path = _resolve_relative_path(playlist_m3u_dir, track)
        if not relative_path:
            return
        new_entries = _remove_entries_for_path(entries, relative_path)
        new_entries.append(("", relative_path))
        _write_m3u_entries(m3u_path, new_entries)
        return

    # Validate Track object
    if not _is_valid_track(track):
        return

    track_entry = _build_track_entry(track, playlist_m3u_dir)
    if not track_entry:
        return
    new_entry, relative_path = track_entry

    # Remove duplicates by path first (to avoid multiple occurrences upon re-download)
    entries = _remove_entries_for_path(entries, relative_path)
    entries, changed = _upsert_entry(entries, new_entry, _extract_playlist_position(track))
    if not changed:
        return

    _write_m3u_entries(m3u_path, entries)


def write_tracks_to_m3u(output_dir: str, playlist_name: str, tracks: List[Track]) -> str:
    """
    Legacy batch method. Creates an m3u and writes provided tracks.
    Prefer progressive usage: create_m3u_file(...) once, then append_track_to_m3u(...) per track.
    """
    playlist_m3u_dir = os.path.join(output_dir, "playlists")
    os.makedirs(playlist_m3u_dir, exist_ok=True)
    m3u_path = os.path.join(playlist_m3u_dir, f"{sanitize_name(playlist_name)}.m3u")
    ensure_m3u_header(m3u_path)
    for track in tracks:
        append_track_to_m3u(m3u_path, track)
    logger.info(f"Created m3u playlist file at: {m3u_path}")
    return m3u_path


def get_m3u_path(output_dir: str, playlist_name: str) -> str:
    playlist_m3u_dir = os.path.join(output_dir, "playlists")
    return os.path.join(playlist_m3u_dir, f"{sanitize_name(playlist_name)}.m3u") 
