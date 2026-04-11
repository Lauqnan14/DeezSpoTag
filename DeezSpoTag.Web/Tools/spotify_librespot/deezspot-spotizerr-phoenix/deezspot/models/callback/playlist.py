#!/usr/bin/python3

from dataclasses import dataclass, field
from typing import List, Optional, Dict, Any

from .common import IDs
from .user import UserObject

@dataclass
class ArtistAlbumTrackPlaylistObject:
    """Artist when nested inside a track in a playlist context."""
    type: str = "artistAlbumTrackPlaylist"
    name: str = ""
    ids: IDs = field(default_factory=IDs)

@dataclass
class AlbumTrackPlaylistObject:
    """Album when nested inside a track in a playlist context."""
    type: str = "albumTrackPlaylist"
    album_type: str = ""  # "album" | "single" | "compilation"
    title: str = ""
    release_date: Dict[str, Any] = field(default_factory=dict)  # ReleaseDate as dict
    total_tracks: int = 0
    total_discs: int = 1  # New field for multi-disc album support
    images: List[Dict[str, Any]] = field(default_factory=list)
    ids: IDs = field(default_factory=IDs)
    artists: List[ArtistAlbumTrackPlaylistObject] = field(default_factory=list)


@dataclass
class ArtistTrackPlaylistObject:
    """Artist when nested inside a track in a playlist context."""
    type: str = "artistTrackPlaylist"
    name: str = ""
    ids: IDs = field(default_factory=IDs)


@dataclass
class TrackPlaylistObject:
    """Track when nested inside a playlist context."""
    type: str = "trackPlaylist"
    title: str = ""
    position: int = 0  # Position in the playlist
    duration_ms: int = 0  # mandatory
    artists: List[ArtistTrackPlaylistObject] = field(default_factory=list)
    album: AlbumTrackPlaylistObject = field(default_factory=AlbumTrackPlaylistObject)
    ids: IDs = field(default_factory=IDs)
    disc_number: int = 1
    track_number: int = 1
    explicit: bool = False


@dataclass
class PlaylistObject:
    """A user‑curated playlist, nesting TrackPlaylistObject[]."""
    type: str = "playlist"
    title: str = ""
    description: Optional[str] = None
    owner: UserObject = field(default_factory=UserObject)
    tracks: List[TrackPlaylistObject] = field(default_factory=list)
    images: List[Dict[str, Any]] = field(default_factory=list)
    ids: IDs = field(default_factory=IDs) 