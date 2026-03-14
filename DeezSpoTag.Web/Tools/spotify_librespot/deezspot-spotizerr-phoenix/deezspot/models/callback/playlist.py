#!/usr/bin/python3

from dataclasses import dataclass, field
from typing import List, Optional, Dict, Any

from .common import IDs
from .user import userObject

@dataclass
class artistAlbumTrackPlaylistObject:
    """Artist when nested inside a track in a playlist context."""
    type: str = "artistAlbumTrackPlaylist"
    name: str = ""
    ids: IDs = field(default_factory=IDs)

@dataclass
class albumTrackPlaylistObject:
    """Album when nested inside a track in a playlist context."""
    type: str = "albumTrackPlaylist"
    album_type: str = ""  # "album" | "single" | "compilation"
    title: str = ""
    release_date: Dict[str, Any] = field(default_factory=dict)  # ReleaseDate as dict
    total_tracks: int = 0
    total_discs: int = 1  # New field for multi-disc album support
    images: List[Dict[str, Any]] = field(default_factory=list)
    ids: IDs = field(default_factory=IDs)
    artists: List[artistAlbumTrackPlaylistObject] = field(default_factory=list)


@dataclass
class artistTrackPlaylistObject:
    """Artist when nested inside a track in a playlist context."""
    type: str = "artistTrackPlaylist"
    name: str = ""
    ids: IDs = field(default_factory=IDs)


@dataclass
class trackPlaylistObject:
    """Track when nested inside a playlist context."""
    type: str = "trackPlaylist"
    title: str = ""
    position: int = 0  # Position in the playlist
    duration_ms: int = 0  # mandatory
    artists: List[artistTrackPlaylistObject] = field(default_factory=list)
    album: albumTrackPlaylistObject = field(default_factory=albumTrackPlaylistObject)
    ids: IDs = field(default_factory=IDs)
    disc_number: int = 1
    track_number: int = 1
    explicit: bool = False


@dataclass
class playlistObject:
    """A user‑curated playlist, nesting trackPlaylistObject[]."""
    type: str = "playlist"
    title: str = ""
    description: Optional[str] = None
    owner: userObject = field(default_factory=userObject)
    tracks: List[trackPlaylistObject] = field(default_factory=list)
    images: List[Dict[str, Any]] = field(default_factory=list)
    ids: IDs = field(default_factory=IDs) 