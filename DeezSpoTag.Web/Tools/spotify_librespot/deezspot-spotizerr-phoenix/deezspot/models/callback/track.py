#!/usr/bin/python3

from dataclasses import dataclass, field
from typing import List, Optional, Dict, Any

from .common import IDs
from .user import userObject

@dataclass
class artistAlbumTrackObject:
    """Artist when nested inside a track in an album context."""
    type: str = "artistAlbumTrack"
    name: str = ""
    ids: IDs = field(default_factory=IDs) 

@dataclass
class artistTrackObject:
    """
    An artist when nested inside a track context.
    No genres, no albums—just identifying info.
    """
    type: str = "artistTrack"
    name: str = ""
    ids: IDs = field(default_factory=IDs)

@dataclass
class albumTrackObject:
    """Album when nested inside a track context."""
    type: str = "albumTrack"
    album_type: str = ""  # "album" | "single" | "compilation"
    title: str = ""
    release_date: Dict[str, Any] = field(default_factory=dict)  # ReleaseDate as dict
    total_tracks: int = 0
    total_discs: int = 1  # New field for multi-disc album support
    genres: List[str] = field(default_factory=list)
    images: List[Dict[str, Any]] = field(default_factory=list)
    ids: IDs = field(default_factory=IDs)
    artists: List[artistAlbumTrackObject] = field(default_factory=list)

@dataclass
class playlistTrackObject:
    """Playlist when nested inside a track context."""
    type: str = "playlistTrack"
    title: str = ""
    description: Optional[str] = None
    owner: userObject = field(default_factory=userObject)
    ids: IDs = field(default_factory=IDs)

@dataclass
class trackObject:
    """A full track record, nesting albumTrackObject and artistTrackObject."""
    type: str = "track"
    title: str = ""
    disc_number: int = 1
    track_number: int = 1
    duration_ms: int = 0  # mandatory
    explicit: bool = False
    genres: List[str] = field(default_factory=list)
    album: albumTrackObject = field(default_factory=albumTrackObject)
    artists: List[artistTrackObject] = field(default_factory=list)
    ids: IDs = field(default_factory=IDs)