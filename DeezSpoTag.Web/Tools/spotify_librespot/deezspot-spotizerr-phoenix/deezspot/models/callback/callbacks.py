#!/usr/bin/python3

from dataclasses import dataclass, field
from typing import List, Optional, Dict, Any, Union

from .common import IDs, Service
from .track import trackObject, albumTrackObject, playlistTrackObject
from .album import albumObject
from .playlist import playlistObject


@dataclass
class BaseStatusObject:
    """Base class for all status objects with common fields."""
    ids: Optional[IDs] = None
    convert_to: Optional[str] = None
    bitrate: Optional[str] = None


@dataclass
class initializingObject(BaseStatusObject):
    """Status object for 'initializing' state."""
    status: str = "initializing"


@dataclass
class skippedObject(BaseStatusObject):
    """Status object for 'skipped' state."""
    status: str = "skipped"
    reason: str = ""


@dataclass
class retryingObject(BaseStatusObject):
    """Status object for 'retrying' state."""
    status: str = "retrying"
    retry_count: int = 0
    seconds_left: int = 0
    error: str = ""


@dataclass
class realTimeObject(BaseStatusObject):
    """Status object for 'real-time' state."""
    status: str = "real-time"
    time_elapsed: int = 0
    progress: int = 0


@dataclass
class errorObject(BaseStatusObject):
    """Status object for 'error' state."""
    status: str = "error"
    error: str = ""


@dataclass
class failedTrackObject:
    """Represents a failed track with a reason."""
    track: trackObject = field(default_factory=trackObject)
    reason: str = ""


@dataclass
class summaryObject:
    """Summary of a download operation for an album or playlist."""
    successful_tracks: List[trackObject] = field(default_factory=list)
    skipped_tracks: List[trackObject] = field(default_factory=list)
    failed_tracks: List[failedTrackObject] = field(default_factory=list)
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
class doneObject(BaseStatusObject):
    """Status object for 'done' state."""
    status: str = "done"
    summary: Optional[summaryObject] = None
    # Extended info for final artifact
    final_path: Optional[str] = None
    download_quality: Optional[str] = None


@dataclass
class trackCallbackObject:
    """
    Track callback object that combines trackObject with status-specific fields.
    Used for progress reporting during track processing.
    """
    track: trackObject = field(default_factory=trackObject)
    status_info: Union[
        initializingObject,
        skippedObject,
        retryingObject,
        realTimeObject,
        errorObject,
        doneObject
    ] = field(default_factory=initializingObject)
    current_track: Optional[int] = None
    total_tracks: Optional[int] = None
    parent: Optional[Union[albumTrackObject, playlistTrackObject]] = None


@dataclass
class albumCallbackObject:
    """
    Album callback object that combines albumObject with status-specific fields.
    Used for progress reporting during album processing.
    """
    album: albumObject = field(default_factory=albumObject)
    status_info: Union[
        initializingObject,
        skippedObject,
        retryingObject,
        realTimeObject,
        errorObject,
        doneObject
    ] = field(default_factory=initializingObject)


@dataclass
class playlistCallbackObject:
    """
    Playlist callback object that combines playlistObject with status-specific fields.
    Used for progress reporting during playlist processing.
    """
    playlist: playlistObject = field(default_factory=playlistObject)
    status_info: Union[
        initializingObject,
        skippedObject,
        retryingObject,
        realTimeObject,
        errorObject,
        doneObject
    ] = field(default_factory=initializingObject) 