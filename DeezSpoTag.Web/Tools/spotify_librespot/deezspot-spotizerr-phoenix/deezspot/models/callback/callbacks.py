#!/usr/bin/python3

from dataclasses import dataclass, field
from typing import List, Optional

from .common import IDs, Service
from .track import TrackObject, AlbumTrackObject, PlaylistTrackObject
from .album import AlbumObject
from .playlist import PlaylistObject


@dataclass
class BaseStatusObject:
    """Base class for all status objects with common fields."""
    ids: Optional[IDs] = None
    convert_to: Optional[str] = None
    bitrate: Optional[str] = None


@dataclass
class InitializingObject(BaseStatusObject):
    """Status object for 'initializing' state."""
    status: str = "initializing"


@dataclass
class SkippedObject(BaseStatusObject):
    """Status object for 'skipped' state."""
    status: str = "skipped"
    reason: str = ""


@dataclass
class RetryingObject(BaseStatusObject):
    """Status object for 'retrying' state."""
    status: str = "retrying"
    retry_count: int = 0
    seconds_left: int = 0
    error: str = ""


@dataclass
class RealTimeObject(BaseStatusObject):
    """Status object for 'real-time' state."""
    status: str = "real-time"
    time_elapsed: int = 0
    progress: int = 0


@dataclass
class ErrorObject(BaseStatusObject):
    """Status object for 'error' state."""
    status: str = "error"
    error: str = ""


@dataclass
class FailedTrackObject:
    """Represents a failed track with a reason."""
    track: TrackObject = field(default_factory=TrackObject)
    reason: str = ""


@dataclass
class SummaryObject:
    """Summary of a download operation for an album or playlist."""
    successful_tracks: List[TrackObject] = field(default_factory=list)
    skipped_tracks: List[TrackObject] = field(default_factory=list)
    failed_tracks: List[FailedTrackObject] = field(default_factory=list)
    total_successful: int = 0
    total_skipped: int = 0
    total_failed: int = 0
    service: Optional[Service] = None
    # Extended info
    m3u_path: Optional[str] = None
    final_path: Optional[str] = None
    download_quality: Optional[str] = None
    # Final media characteristics
    quality: Optional[str] = None   # e.g., "mp3", "flac", "ogg"
    bitrate: Optional[str] = None   # e.g., "320k"


@dataclass
class DoneObject(BaseStatusObject):
    """Status object for 'done' state."""
    status: str = "done"
    summary: Optional[SummaryObject] = None
    # Extended info for final artifact
    final_path: Optional[str] = None
    download_quality: Optional[str] = None


@dataclass
class TrackCallbackObject:
    """
    Track callback object that combines TrackObject with status-specific fields.
    Used for progress reporting during track processing.
    """
    track: TrackObject = field(default_factory=TrackObject)
    status_info: (
        InitializingObject
        | SkippedObject
        | RetryingObject
        | RealTimeObject
        | ErrorObject
        | DoneObject
    ) = field(default_factory=InitializingObject)
    current_track: Optional[int] = None
    total_tracks: Optional[int] = None
    parent: Optional[AlbumTrackObject | PlaylistTrackObject] = None


@dataclass
class AlbumCallbackObject:
    """
    Album callback object that combines AlbumObject with status-specific fields.
    Used for progress reporting during album processing.
    """
    album: AlbumObject = field(default_factory=AlbumObject)
    status_info: (
        InitializingObject
        | SkippedObject
        | RetryingObject
        | RealTimeObject
        | ErrorObject
        | DoneObject
    ) = field(default_factory=InitializingObject)


@dataclass
class PlaylistCallbackObject:
    """
    Playlist callback object that combines PlaylistObject with status-specific fields.
    Used for progress reporting during playlist processing.
    """
    playlist: PlaylistObject = field(default_factory=PlaylistObject)
    status_info: (
        InitializingObject
        | SkippedObject
        | RetryingObject
        | RealTimeObject
        | ErrorObject
        | DoneObject
    ) = field(default_factory=InitializingObject)
