#!/usr/bin/python3

"""
Callback data models for the music metadata schema.
"""

from .common import IDs, ReleaseDate
from .artist import ArtistObject, AlbumArtistObject
from .album import AlbumObject, TrackAlbumObject, ArtistAlbumObject
from .track import TrackObject, ArtistTrackObject, AlbumTrackObject, PlaylistTrackObject
from .playlist import PlaylistObject, TrackPlaylistObject, AlbumTrackPlaylistObject, ArtistTrackPlaylistObject 
from .callbacks import (
    BaseStatusObject, 
    InitializingObject, 
    SkippedObject, 
    RetryingObject, 
    RealTimeObject, 
    ErrorObject, 
    DoneObject,
    SummaryObject,
    FailedTrackObject,
    TrackCallbackObject, 
    AlbumCallbackObject, 
    PlaylistCallbackObject
) 
from .user import UserObject