#!/usr/bin/python3

from typing import Optional, List, Dict, Any

from deezspot.models.callback.common import IDs
from deezspot.models.callback.track import (
    TrackObject,
    AlbumTrackObject,
    ArtistTrackObject,
    ArtistAlbumTrackObject,
)
from deezspot.models.callback.album import (
    AlbumObject,
    TrackAlbumObject,
    ArtistAlbumObject,
    ArtistTrackAlbumObject,
)
from deezspot.models.callback.playlist import (
    PlaylistObject,
    TrackPlaylistObject,
    AlbumTrackPlaylistObject,
    ArtistTrackPlaylistObject,
    ArtistAlbumTrackPlaylistObject,
)
from deezspot.models.callback.user import UserObject
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


def _json_to_artist_track_object(artist_json: dict) -> ArtistTrackObject:
    return ArtistTrackObject(
        name=artist_json.get('name'),
        ids=IDs(deezer=artist_json.get('id'))
    )

def _json_to_album_track_object(album_json: dict) -> AlbumTrackObject:
    artists = []
    
    # Check for contributors first - they're more detailed
    if "contributors" in album_json:
        # Look for main artists
        main_artists = [c for c in album_json['contributors'] if c.get('role') == 'Main']
        if main_artists:
            artists = [ArtistAlbumTrackObject(
                name=c.get('name'),
                ids=IDs(deezer=c.get('id'))
            ) for c in main_artists]
        else:
            # If no main artists specified, use all contributors
            artists = [ArtistAlbumTrackObject(
                name=c.get('name'),
                ids=IDs(deezer=c.get('id'))
            ) for c in album_json['contributors']]
    
    # If no contributors found, use the artist field
    if not artists and "artist" in album_json:
        artists.append(ArtistAlbumTrackObject(
            name=album_json['artist'].get('name'),
            ids=IDs(deezer=album_json['artist'].get('id'))
        ))

    return AlbumTrackObject(
        album_type=album_json.get('record_type', ''),
        title=album_json.get('title'),
        ids=IDs(deezer=album_json.get('id')),
        images=_get_images_from_cover(album_json),
        release_date=_parse_release_date(album_json.get('release_date')),
        artists=artists,
        total_tracks=album_json.get('nb_tracks', 0),
        genres=[g['name'] for g in album_json.get('genres', {}).get('data', [])]
    )

def tracking(track_json: dict) -> Optional[TrackObject]:
    """
    Convert raw Deezer API track response to a standardized TrackObject.
    
    Args:
        track_json: Raw track data from Deezer API
        
    Returns:
        A standardized TrackObject or None if input is invalid
    """
    if not track_json or 'id' not in track_json:
        return None
        
    return create_standardized_track(track_json)

def _json_to_track_album_object(track_json: dict) -> TrackAlbumObject:
    artists = []
    if "artist" in track_json:
        artists.append(ArtistTrackAlbumObject(
            name=track_json['artist'].get('name'),
            ids=IDs(deezer=track_json['artist'].get('id'))
        ))
    
    # If 'contributors' exists, add them as artists too
    if "contributors" in track_json:
        for contributor in track_json['contributors']:
            # Skip duplicates - don't add if name already exists
            if not any(artist.name == contributor.get('name') for artist in artists):
                artists.append(ArtistTrackAlbumObject(
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
    
    return TrackAlbumObject(
        title=track_json.get('title'),
        duration_ms=track_json.get('duration', 0) * 1000,
        explicit=track_json.get('explicit_lyrics', False),
        track_number=track_position,
        disc_number=disc_number,
        ids=IDs(deezer=track_json.get('id')),
        artists=artists
    )


def _extract_album_artists(album_json: dict) -> list[ArtistAlbumObject]:
    contributors = album_json.get('contributors')
    if contributors:
        main_artists = [c for c in contributors if c.get('role') == 'Main']
        source_artists = main_artists or contributors
        return [
            ArtistAlbumObject(
                name=c.get('name', ''),
                ids=IDs(deezer=c.get('id'))
            )
            for c in source_artists
        ]

    album_artist = album_json.get('artist')
    if not album_artist:
        return []

    return [
        ArtistAlbumObject(
            name=album_artist.get('name', ''),
            ids=IDs(deezer=album_artist.get('id'))
        )
    ]


def _create_album_track(track_data: dict) -> TrackAlbumObject:
    track_artists = []
    track_artist = track_data.get('artist')
    if track_artist:
        track_artists.append(ArtistTrackAlbumObject(
            name=track_artist.get('name'),
            ids=IDs(deezer=track_artist.get('id'))
        ))

    track_position = track_data.get('track_position')
    if track_position is None:
        track_position = track_data.get('track_number', 0)

    disc_number = track_data.get('disk_number')
    if disc_number is None:
        disc_number = track_data.get('disc_number', 1)

    return TrackAlbumObject(
        title=track_data.get('title'),
        duration_ms=track_data.get('duration', 0) * 1000,
        explicit=track_data.get('explicit_lyrics', False),
        track_number=track_position,
        disc_number=disc_number,
        ids=IDs(deezer=track_data.get('id'), isrc=track_data.get('isrc')),
        artists=track_artists
    )


def tracking_album(album_json: dict) -> Optional[AlbumObject]:
    if not album_json or 'id' not in album_json:
        return None

    album_artists = _extract_album_artists(album_json)

    # Extract album metadata
    album_obj = AlbumObject(
        album_type=album_json.get('record_type', ''),
        title=album_json.get('title', ''),
        ids=IDs(deezer=album_json.get('id'), upc=album_json.get('upc')),
        images=_get_images_from_cover(album_json),
        release_date=_parse_release_date(album_json.get('release_date')),
        total_tracks=album_json.get('nb_tracks', 0),
        genres=[g['name'] for g in album_json.get('genres', {}).get('data', [])] if album_json.get('genres') else [],
        artists=album_artists
    )
    
    tracks_data = album_json.get('tracks', {}).get('data', [])
    album_tracks = [_create_album_track(track_data) for track_data in tracks_data]

    # Calculate total discs by finding the maximum disc number
    total_discs = 1
    if album_tracks:
        disc_numbers = [track.disc_number for track in album_tracks if hasattr(track, 'disc_number') and track.disc_number]
        total_discs = max(disc_numbers, default=1)
    
    # Update album object with tracks and total discs
    album_obj.tracks = album_tracks
    album_obj.total_discs = total_discs
    
    return album_obj

def _json_to_track_playlist_object(track_json: dict) -> Optional[TrackPlaylistObject]:
    if not track_json or not track_json.get('id'):
        return None

    # Create artists with proper type
    artists = []
    if "artist" in track_json:
        artists.append(ArtistTrackPlaylistObject(
            name=track_json['artist'].get('name'),
            ids=IDs(deezer=track_json['artist'].get('id'))
        ))
    
    # If 'contributors' exists, add them as artists too
    if "contributors" in track_json:
        for contributor in track_json['contributors']:
            # Skip duplicates - don't add if name already exists
            if not any(artist.name == contributor.get('name') for artist in artists):
                artists.append(ArtistTrackPlaylistObject(
                    name=contributor.get('name'),
                    ids=IDs(deezer=contributor.get('id'))
                ))

    # Process album
    album_data = track_json.get('album', {})
    
    # Process album artists
    album_artists = []
    if "artist" in album_data:
        album_artists.append(ArtistAlbumTrackPlaylistObject(
            name=album_data['artist'].get('name'),
            ids=IDs(deezer=album_data['artist'].get('id'))
        ))
    
    album = AlbumTrackPlaylistObject(
        title=album_data.get('title'),
        ids=IDs(deezer=album_data.get('id')),
        images=_get_images_from_cover(album_data),
        artists=album_artists,
        album_type=album_data.get('record_type', ''),
        release_date=_parse_release_date(album_data.get('release_date')),
        total_tracks=album_data.get('nb_tracks', 0)
    )

    return TrackPlaylistObject(
        title=track_json.get('title'),
        duration_ms=track_json.get('duration', 0) * 1000,
        ids=IDs(deezer=track_json.get('id'), isrc=track_json.get('isrc')),
        artists=artists,
        album=album,
        explicit=track_json.get('explicit_lyrics', False),
        disc_number=track_json.get('disk_number') or track_json.get('disc_number', 1),
        track_number=track_json.get('track_position') or track_json.get('track_number', 0)
    )

def tracking_playlist(playlist_json: dict) -> Optional[PlaylistObject]:
    if not playlist_json or 'id' not in playlist_json:
        return None
        
    creator = playlist_json.get('creator', {})
    owner = UserObject(
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

    playlist_obj = PlaylistObject(
        title=playlist_json.get('title'),
        description=description,
        ids=IDs(deezer=playlist_json.get('id')),
        images=images,
        owner=owner,
        tracks=tracks,
    )
    
    return playlist_obj

def _build_track_artists(track_json: dict) -> list[ArtistTrackObject]:
    artists: list[ArtistTrackObject] = []
    main_artist = track_json.get("artist")
    if isinstance(main_artist, dict):
        artists.append(
            ArtistTrackObject(
                name=main_artist.get('name', ''),
                ids=IDs(deezer=main_artist.get('id')),
            )
        )

    existing_names = {artist.name for artist in artists}
    for contributor in track_json.get("contributors", []):
        contributor_name = contributor.get('name')
        if contributor_name in existing_names:
            continue
        artists.append(
            ArtistTrackObject(
                name=contributor_name or '',
                ids=IDs(deezer=contributor.get('id')),
            )
        )
        if contributor_name:
            existing_names.add(contributor_name)
    return artists

def _build_album_artists(track_json: dict) -> list[ArtistAlbumTrackObject]:
    album_artists: list[ArtistAlbumTrackObject] = []
    contributors = track_json.get('contributors', [])
    main_contributors = [c for c in contributors if c.get('role') == 'Main']
    for contributor in main_contributors:
        album_artists.append(
            ArtistAlbumTrackObject(
                name=contributor.get('name', ''),
                ids=IDs(deezer=contributor.get('id')),
            )
        )
    if album_artists:
        return album_artists

    album_artist = track_json.get("album", {}).get("artist", {})
    if isinstance(album_artist, dict):
        album_artists.append(
            ArtistAlbumTrackObject(
                name=album_artist.get('name', ''),
                ids=IDs(deezer=album_artist.get('id')),
            )
        )
    return album_artists

def _fetch_total_discs(album_id) -> int:
    if not album_id:
        return 1
    try:
        from deezspot.deezloader.dee_api import API
        full_album_obj = API.get_album(album_id)
        if full_album_obj and hasattr(full_album_obj, 'total_discs'):
            return full_album_obj.total_discs
    except Exception as e:
        logger.debug(f"Could not fetch full album data for total_discs calculation: {e}")
    return 1

def _build_track_album_object(track_json: dict) -> Optional[AlbumTrackObject]:
    album_payload = track_json.get("album")
    if not isinstance(album_payload, dict):
        return None

    album_artists = _build_album_artists(track_json)
    total_discs = _fetch_total_discs(album_payload.get('id'))
    return AlbumTrackObject(
        album_type=album_payload.get('record_type', ''),
        title=album_payload.get('title', ''),
        ids=IDs(deezer=album_payload.get('id')),
        images=_get_images_from_cover(album_payload),
        release_date=_parse_release_date(album_payload.get('release_date')),
        artists=album_artists,
        total_tracks=album_payload.get('nb_tracks', 0),
        total_discs=total_discs,
        genres=[g['name'] for g in album_payload.get('genres', {}).get('data', [])],
    )

def create_standardized_track(track_json: dict) -> TrackObject:
    """
    Create a standardized TrackObject directly from Deezer API response.
    This makes metadata handling more consistent with spotloader's approach.
    
    Args:
        track_json: Raw track data from Deezer API
        
    Returns:
        A standardized TrackObject
    """
    artists = _build_track_artists(track_json)
    album_data = _build_track_album_object(track_json)
    
    # Create track object
    track_obj = TrackObject(
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
