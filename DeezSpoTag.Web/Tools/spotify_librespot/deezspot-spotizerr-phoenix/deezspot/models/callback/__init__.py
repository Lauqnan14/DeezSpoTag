#!/usr/bin/python3

"""
Callback data models for the music metadata schema.
"""

from .common import IDs, ReleaseDate
from .artist import artistObject, albumArtistObject
from .album import albumObject, trackAlbumObject, artistAlbumObject
from .track import trackObject, artistTrackObject, albumTrackObject, playlistTrackObject
from .playlist import playlistObject, trackPlaylistObject, albumTrackPlaylistObject, artistTrackPlaylistObject 
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
from .user import userObject