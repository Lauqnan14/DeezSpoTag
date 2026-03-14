#!/usr/bin/python3

from typing import Optional, List, Dict, Any

from deezspot.models.callback.common import IDs
from deezspot.models.callback.track import (
    trackObject,
    albumTrackObject,
    artistTrackObject,
    artistAlbumTrackObject,
)
from deezspot.models.callback.album import (
    albumObject,
    trackAlbumObject,
    artistAlbumObject,
    artistTrackAlbumObject,
)
from deezspot.models.callback.playlist import (
    playlistObject,
    trackPlaylistObject,
    albumTrackPlaylistObject,
    artistTrackPlaylistObject,
    artistAlbumTrackPlaylistObject,
)
from deezspot.models.callback.user import userObject
from deezspot.libutils.logging_utils import logger

def _parse_release_date(date_str: Optional[str]) -> Dict[str, Any]:
    if not date_str:
        return {"year": 0, "month": 0, "day": 0}
    
    parts = list(map(int, date_str.split('-')))
    return {
        "year": parts[0] if len(parts) > 0 else 0,
        "month": parts[1] if len(parts) > 1 else 0,
        "day": parts[2] if len(parts) > 2 else 0
    }

def _get_images_from_cover(item_json: dict) -> List[Dict[str, Any]]:
    images = []
    if item_json.get("cover_small"):
        images.append({"url": item_json["cover_small"], "height": 56, "width": 56})
    if item_json.get("cover_medium"):
        images.append({"url": item_json["cover_medium"], "height": 250, "width": 250})
    if item_json.get("cover_big"):
        images.append({"url": item_json["cover_big"], "height": 500, "width": 500})
    if item_json.get("cover_xl"):
        images.append({"url": item_json["cover_xl"], "height": 1000, "width": 1000})
    if item_json.get("picture_small"):
        images.append({"url": item_json["picture_small"], "height": 56, "width": 56})
    if item_json.get("picture_medium"):
        images.append({"url": item_json["picture_medium"], "height": 250, "width": 250})
    if item_json.get("picture_big"):
        images.append({"url": item_json["picture_big"], "height": 500, "width": 500})
    if item_json.get("picture_xl"):
        images.append({"url": item_json["picture_xl"], "height": 1000, "width": 1000})
    return images


def _json_to_artist_track_object(artist_json: dict) -> artistTrackObject:
    return artistTrackObject(
        name=artist_json.get('name'),
        ids=IDs(deezer=artist_json.get('id'))
    )

def _json_to_album_track_object(album_json: dict) -> albumTrackObject:
    artists = []
    
    # Check for contributors first - they're more detailed
    if "contributors" in album_json:
        # Look for main artists
        main_artists = [c for c in album_json['contributors'] if c.get('role') == 'Main']
        if main_artists:
            artists = [artistAlbumTrackObject(
                name=c.get('name'),
                ids=IDs(deezer=c.get('id'))
            ) for c in main_artists]
        else:
            # If no main artists specified, use all contributors
            artists = [artistAlbumTrackObject(
                name=c.get('name'),
                ids=IDs(deezer=c.get('id'))
            ) for c in album_json['contributors']]
    
    # If no contributors found, use the artist field
    if not artists and "artist" in album_json:
        artists.append(artistAlbumTrackObject(
            name=album_json['artist'].get('name'),
            ids=IDs(deezer=album_json['artist'].get('id'))
        ))

    return albumTrackObject(
        album_type=album_json.get('record_type', ''),
        title=album_json.get('title'),
        ids=IDs(deezer=album_json.get('id')),
        images=_get_images_from_cover(album_json),
        release_date=_parse_release_date(album_json.get('release_date')),
        artists=artists,
        total_tracks=album_json.get('nb_tracks', 0),
        genres=[g['name'] for g in album_json.get('genres', {}).get('data', [])]
    )

def tracking(track_json: dict) -> Optional[trackObject]:
    """
    Convert raw Deezer API track response to a standardized trackObject.
    
    Args:
        track_json: Raw track data from Deezer API
        
    Returns:
        A standardized trackObject or None if input is invalid
    """
    if not track_json or 'id' not in track_json:
        return None
        
    return create_standardized_track(track_json)

def _json_to_track_album_object(track_json: dict) -> trackAlbumObject:
    artists = []
    if "artist" in track_json:
        artists.append(artistTrackAlbumObject(
            name=track_json['artist'].get('name'),
            ids=IDs(deezer=track_json['artist'].get('id'))
        ))
    
    # If 'contributors' exists, add them as artists too
    if "contributors" in track_json:
        for contributor in track_json['contributors']:
            # Skip duplicates - don't add if name already exists
            if not any(artist.name == contributor.get('name') for artist in artists):
                artists.append(artistTrackAlbumObject(
                    name=contributor.get('name'),
                    ids=IDs(deezer=contributor.get('id'))
                ))
    
    # Ensure track position and disc number are properly extracted
    track_position = track_json.get('track_position')
    # Default to track_number if track_position isn't available
    if track_position is None:
        track_position = track_json.get('track_number')
    # Ensure we have a non-None value
    if track_position is None:
        track_position = 0
        
    disc_number = track_json.get('disk_number')
    # Default to disc_number if disk_number isn't available
    if disc_number is None:
        disc_number = track_json.get('disc_number')
    # Ensure we have a non-None value
    if disc_number is None:
        disc_number = 1
    
    return trackAlbumObject(
        title=track_json.get('title'),
        duration_ms=track_json.get('duration', 0) * 1000,
        explicit=track_json.get('explicit_lyrics', False),
        track_number=track_position,
        disc_number=disc_number,
        ids=IDs(deezer=track_json.get('id')),
        artists=artists
    )


def tracking_album(album_json: dict) -> Optional[albumObject]:
    if not album_json or 'id' not in album_json:
        return None

    # Determine album artists from contributors or artist field
    album_artists = []
    if 'contributors' in album_json:
        main_artists = [c for c in album_json['contributors'] if c.get('role') == 'Main']
        if main_artists:
            album_artists = [artistAlbumObject(
                name=c.get('name', ''),
                ids=IDs(deezer=c.get('id'))
            ) for c in main_artists]
        else:
            # Fallback to all contributors if no main artist is specified
            album_artists = [artistAlbumObject(
                name=c.get('name', ''),
                ids=IDs(deezer=c.get('id'))
            ) for c in album_json['contributors']]
    elif 'artist' in album_json:
        album_artists.append(artistAlbumObject(
            name=album_json['artist'].get('name', ''),
            ids=IDs(deezer=album_json['artist'].get('id'))
        ))

    # Extract album metadata
    album_obj = albumObject(
        album_type=album_json.get('record_type', ''),
        title=album_json.get('title', ''),
        ids=IDs(deezer=album_json.get('id'), upc=album_json.get('upc')),
        images=_get_images_from_cover(album_json),
        release_date=_parse_release_date(album_json.get('release_date')),
        total_tracks=album_json.get('nb_tracks', 0),
        genres=[g['name'] for g in album_json.get('genres', {}).get('data', [])] if album_json.get('genres') else [],
        artists=album_artists
    )
    
    # Process tracks
    album_tracks = []
    tracks_data = album_json.get('tracks', {}).get('data', [])
    
    for track_data in tracks_data:
        # Ensure we have detailed track information
        # The /album/{id}/tracks endpoint provides ISRC, explicit flags, etc.
        
        # Create track artists with main artist
        track_artists = []
        if "artist" in track_data:
            track_artists.append(artistTrackAlbumObject(
                name=track_data['artist'].get('name'),
                ids=IDs(deezer=track_data['artist'].get('id'))
            ))
        
        # Ensure track position and disc number are properly extracted
        track_position = track_data.get('track_position')
        if track_position is None:
            track_position = track_data.get('track_number', 0)
        
        disc_number = track_data.get('disk_number')
        if disc_number is None:
            disc_number = track_data.get('disc_number', 1)
        
        # Create the track object with enhanced metadata
        track = trackAlbumObject(
            title=track_data.get('title'),
            duration_ms=track_data.get('duration', 0) * 1000,
            explicit=track_data.get('explicit_lyrics', False),
            track_number=track_position,
            disc_number=disc_number,
            ids=IDs(deezer=track_data.get('id'), isrc=track_data.get('isrc')),
            artists=track_artists
        )
        album_tracks.append(track)

    # Calculate total discs by finding the maximum disc number
    total_discs = 1
    if album_tracks:
        disc_numbers = [track.disc_number for track in album_tracks if hasattr(track, 'disc_number') and track.disc_number]
        total_discs = max(disc_numbers, default=1)
    
    # Update album object with tracks and total discs
    album_obj.tracks = album_tracks
    album_obj.total_discs = total_discs
    
    return album_obj

def _json_to_track_playlist_object(track_json: dict) -> Optional[trackPlaylistObject]:
    if not track_json or not track_json.get('id'):
        return None

    # Create artists with proper type
    artists = []
    if "artist" in track_json:
        artists.append(artistTrackPlaylistObject(
            name=track_json['artist'].get('name'),
            ids=IDs(deezer=track_json['artist'].get('id'))
        ))
    
    # If 'contributors' exists, add them as artists too
    if "contributors" in track_json:
        for contributor in track_json['contributors']:
            # Skip duplicates - don't add if name already exists
            if not any(artist.name == contributor.get('name') for artist in artists):
                artists.append(artistTrackPlaylistObject(
                    name=contributor.get('name'),
                    ids=IDs(deezer=contributor.get('id'))
                ))

    # Process album
    album_data = track_json.get('album', {})
    
    # Process album artists
    album_artists = []
    if "artist" in album_data:
        album_artists.append(artistAlbumTrackPlaylistObject(
            name=album_data['artist'].get('name'),
            ids=IDs(deezer=album_data['artist'].get('id'))
        ))
    
    album = albumTrackPlaylistObject(
        title=album_data.get('title'),
        ids=IDs(deezer=album_data.get('id')),
        images=_get_images_from_cover(album_data),
        artists=album_artists,
        album_type=album_data.get('record_type', ''),
        release_date=_parse_release_date(album_data.get('release_date')),
        total_tracks=album_data.get('nb_tracks', 0)
    )

    return trackPlaylistObject(
        title=track_json.get('title'),
        duration_ms=track_json.get('duration', 0) * 1000,
        ids=IDs(deezer=track_json.get('id'), isrc=track_json.get('isrc')),
        artists=artists,
        album=album,
        explicit=track_json.get('explicit_lyrics', False),
        disc_number=track_json.get('disk_number') or track_json.get('disc_number', 1),
        track_number=track_json.get('track_position') or track_json.get('track_number', 0)
    )

def tracking_playlist(playlist_json: dict) -> Optional[playlistObject]:
    if not playlist_json or 'id' not in playlist_json:
        return None
        
    creator = playlist_json.get('creator', {})
    owner = userObject(
        name=creator.get('name'),
        ids=IDs(deezer=creator.get('id'))
    )

    tracks_data = playlist_json.get('tracks', {}).get('data', [])
    tracks = []
    for track_data in tracks_data:
        track = _json_to_track_playlist_object(track_data)
        if track:
            tracks.append(track)

    # Extract playlist images
    images = _get_images_from_cover(playlist_json)
    
    # Add picture of the first track as playlist image if no images found
    if not images and tracks and tracks[0].album and tracks[0].album.images:
        images = tracks[0].album.images

    description = playlist_json.get('description') or ""

    playlist_obj = playlistObject(
        title=playlist_json.get('title'),
        description=description,
        ids=IDs(deezer=playlist_json.get('id')),
        images=images,
        owner=owner,
        tracks=tracks,
    )
    
    return playlist_obj

def create_standardized_track(track_json: dict) -> trackObject:
    """
    Create a standardized trackObject directly from Deezer API response.
    This makes metadata handling more consistent with spotloader's approach.
    
    Args:
        track_json: Raw track data from Deezer API
        
    Returns:
        A standardized trackObject
    """
    # Extract artist information
    artists = []
    if "artist" in track_json:
        artists.append(artistTrackObject(
            name=track_json['artist'].get('name', ''),
            ids=IDs(deezer=track_json['artist'].get('id'))
        ))
        
    # Add additional artists from contributors
    if "contributors" in track_json:
        for contributor in track_json['contributors']:
            # Skip if already added
            if not any(artist.name == contributor.get('name') for artist in artists):
                artists.append(artistTrackObject(
                    name=contributor.get('name', ''),
                    ids=IDs(deezer=contributor.get('id'))
                ))
    
    # Extract album information
    album_data = None
    if "album" in track_json:
        album_artists = []
        
        # First check for main contributors if available
        if "contributors" in track_json:
            main_artists = [c for c in track_json['contributors'] if c.get('role') == 'Main']
            if main_artists:
                album_artists = [artistAlbumTrackObject(
                    name=c.get('name', ''),
                    ids=IDs(deezer=c.get('id'))
                ) for c in main_artists]
        
        # If no main contributors found and album has its own artist field
        if not album_artists and "artist" in track_json["album"]:
            album_artists.append(artistAlbumTrackObject(
                name=track_json["album"]["artist"].get('name', ''),
                ids=IDs(deezer=track_json["album"]["artist"].get('id'))
            ))
        
        # Try to get full album information for accurate total_discs
        total_discs = 1
        album_id = track_json["album"].get('id')
        if album_id:
            try:
                # Import here to avoid circular imports
                from deezspot.deezloader.dee_api import API
                full_album_obj = API.get_album(album_id)
                if full_album_obj and hasattr(full_album_obj, 'total_discs'):
                    total_discs = full_album_obj.total_discs
            except Exception as e:
                # If album fetching fails, fall back to default
                logger.debug(f"Could not fetch full album data for total_discs calculation: {e}")
                total_discs = 1
            
        album_data = albumTrackObject(
            album_type=track_json["album"].get('record_type', ''),
            title=track_json["album"].get('title', ''),
            ids=IDs(deezer=track_json["album"].get('id')),
            images=_get_images_from_cover(track_json["album"]),
            release_date=_parse_release_date(track_json["album"].get('release_date')),
            artists=album_artists,
            total_tracks=track_json["album"].get('nb_tracks', 0),
            total_discs=total_discs,  # Set the calculated or fetched total discs
            genres=[g['name'] for g in track_json["album"].get('genres', {}).get('data', [])]
        )
    
    # Create track object
    track_obj = trackObject(
        title=track_json.get('title', ''),
        duration_ms=track_json.get('duration', 0) * 1000,
        explicit=track_json.get('explicit_lyrics', False),
        track_number=track_json.get('track_position') or track_json.get('track_number', 0),
        disc_number=track_json.get('disk_number') or track_json.get('disc_number', 1),
        ids=IDs(deezer=track_json.get('id'), isrc=track_json.get('isrc')),
        artists=artists,
        album=album_data,
        genres=[g['name'] for g in track_json.get('genres', {}).get('data', [])],
    )
    
    return track_obj 