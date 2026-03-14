#!/usr/bin/python3

from dataclasses import dataclass, field
from typing import List, Optional, Dict, Any

from .common import IDs, ReleaseDate

@dataclass
class artistTrackAlbumObject:
    """Artist representation for a track in an album context."""
    type: str = "artistTrackAlbum"
    name: str = ""
    ids: IDs = field(default_factory=IDs)


@dataclass
class artistAlbumObject:
    """Artist representation for an album."""
    type: str = "artistAlbum"
    name: str = ""
    genres: List[str] = field(default_factory=list)
    ids: IDs = field(default_factory=IDs) 
    
@dataclass
class trackAlbumObject:
    """Track when nested inside an album context."""
    type: str = "trackAlbum"
    title: str = ""
    disc_number: int = 1
    track_number: int = 1
    duration_ms: int = 0
    explicit: bool = False
    genres: List[str] = field(default_factory=list)
    ids: IDs = field(default_factory=IDs)
    artists: List[artistTrackAlbumObject] = field(default_factory=list)


@dataclass
class albumObject:
    """A standalone album/single/compilation, with nested trackAlbumObject[] for its tracks."""
    type: str = "album"
    album_type: str = ""  # "album" | "single" | "compilation"
    title: str = ""
    release_date: Dict[str, Any] = field(default_factory=dict)
    total_tracks: int = 0
    total_discs: int = 1  # New field for multi-disc album support
    genres: List[str] = field(default_factory=list)
    images: List[Dict[str, Any]] = field(default_factory=list)
    copyrights: List[Dict[str, str]] = field(default_factory=list)
    ids: IDs = field(default_factory=IDs)
    tracks: List[trackAlbumObject] = field(default_factory=list)
    artists: List[artistAlbumObject] = field(default_factory=list)