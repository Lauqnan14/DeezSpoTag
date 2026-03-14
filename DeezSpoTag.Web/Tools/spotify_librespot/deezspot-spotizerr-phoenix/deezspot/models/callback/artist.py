#!/usr/bin/python3

from dataclasses import dataclass, field
from typing import List, Optional, Dict, Any

from .common import IDs


@dataclass
class albumArtistObject:
    """Album when nested inside an artist context."""
    type: str = "albumArtist"
    album_type: str = ""  # "album" | "single" | "compilation"
    title: str = ""
    release_date: dict = field(default_factory=dict)  # ReleaseDate as dict
    total_tracks: int = 0
    ids: IDs = field(default_factory=IDs)


@dataclass
class artistObject:
    """A full artist record, with nested albumArtistObject[] for a discography."""
    type: str = "artist"
    name: str = ""
    genres: List[str] = field(default_factory=list)
    images: List[Dict[str, Any]] = field(default_factory=list)
    ids: IDs = field(default_factory=IDs)
    albums: List[albumArtistObject] = field(default_factory=list) 