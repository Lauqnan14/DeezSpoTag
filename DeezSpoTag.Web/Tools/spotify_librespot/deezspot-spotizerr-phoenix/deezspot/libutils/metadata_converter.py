#!/usr/bin/python3

from datetime import datetime
from typing import Dict, Any, Optional, Union


def _detect_source_type(obj: Any) -> str:
    """
    Auto-detect whether an object is from Spotify or Deezer based on its IDs.
    
    Args:
        obj: Track or album object with ids attribute
        
    Returns:
        str: 'spotify' or 'deezer'
    """
    if hasattr(obj, 'ids') and obj.ids:
        if hasattr(obj.ids, 'spotify') and obj.ids.spotify:
            return 'spotify'
        elif hasattr(obj.ids, 'deezer') and obj.ids.deezer:
            return 'deezer'
    return 'spotify'  # Default fallback


def _get_platform_id(ids_obj: Any, source_type: str) -> Optional[str]:
    """
    Get the appropriate platform ID based on source type.
    
    Args:
        ids_obj: IDs object containing platform-specific IDs
        source_type: 'spotify' or 'deezer'
        
    Returns:
        Platform-specific ID or None
    """
    if not ids_obj:
        return None
    
    if source_type == 'spotify':
        return getattr(ids_obj, 'spotify', None)
    else:  # deezer
        return getattr(ids_obj, 'deezer', None)


def _format_release_date(release_date: Any, source_type: str) -> Optional[datetime]:
    """
    Format release date based on source type and data structure.
    
    Args:
        release_date: Release date object/dict from API
        source_type: 'spotify' or 'deezer'
        
    Returns:
        datetime object or None
    """
    if not release_date:
        return None
        
    try:
        if source_type == 'spotify' and isinstance(release_date, dict):
            # Spotify format: create datetime object
            if 'year' in release_date:
                return datetime(
                    year=release_date['year'],
                    month=release_date.get('month', 1),
                    day=release_date.get('day', 1)
                )
        elif source_type == 'deezer':
            # Deezer format: extract year only
            if isinstance(release_date, dict) and 'year' in release_date:
                return datetime(year=release_date['year'], month=1, day=1)
            elif hasattr(release_date, 'year'):
                return datetime(year=release_date.year, month=1, day=1)
                
    except (ValueError, TypeError, AttributeError):
        pass
        
    return None


def _get_best_image_url(images: Any, source_type: str) -> Optional[str]:
    """
    Get the best image URL based on source type and format.
    
    Args:
        images: Images data from API
        source_type: 'spotify' or 'deezer'
        
    Returns:
        Best image URL or None
    """
    if not images:
        return None
        
    if source_type == 'spotify' and isinstance(images, list):
        # Spotify: find highest resolution image
        best_image = max(images, key=lambda i: i.get('height', 0) * i.get('width', 0), default=None)
        return best_image.get('url') if best_image else None
    elif source_type == 'deezer':
        # Deezer: images might be direct URL or object
        if isinstance(images, str):
            return images
        elif isinstance(images, list) and images:
            return images[0] if isinstance(images[0], str) else None
        elif hasattr(images, 'url'):
            return images.url
            
    return None


def track_object_to_dict(track_obj: Any, source_type: Optional[str] = None, artist_separator: str = "; ") -> Dict[str, Any]:
    """
    Convert a track object to a dictionary format for tagging.
    Supports both Spotify and Deezer track objects.
    
    Args:
        track_obj: Track object from Spotify or Deezer API
        source_type: Optional source type ('spotify' or 'deezer'). If None, auto-detected.
        artist_separator: Separator string for joining multiple artists
        
    Returns:
        Dictionary with standardized metadata tags
    """
    if not track_obj:
        return {}
    
    # Auto-detect source if not specified
    if source_type is None:
        source_type = _detect_source_type(track_obj)
    
    tags = {}
    
    # Basic track details
    tags['music'] = getattr(track_obj, 'title', '')
    tags['tracknum'] = getattr(track_obj, 'track_number', 0) if getattr(track_obj, 'track_number', None) is not None else 0
    tags['discnum'] = getattr(track_obj, 'disc_number', 1) if getattr(track_obj, 'disc_number', None) is not None else 1
    tags['duration'] = (getattr(track_obj, 'duration_ms', 0) // 1000) if getattr(track_obj, 'duration_ms', None) else 0
    
    # Platform-specific IDs
    if hasattr(track_obj, 'ids') and track_obj.ids:
        tags['ids'] = _get_platform_id(track_obj.ids, source_type)
        tags['isrc'] = getattr(track_obj.ids, 'isrc', None)
    
    # Artist information
    if hasattr(track_obj, 'artists') and track_obj.artists:
        tags['artist'] = artist_separator.join([getattr(artist, 'name', '') for artist in track_obj.artists])
    else:
        tags['artist'] = ''
    
    # Explicit flag (mainly for Deezer)
    if hasattr(track_obj, 'explicit'):
        tags['explicit'] = track_obj.explicit
    
    # Album details
    if hasattr(track_obj, 'album') and track_obj.album:
        album = track_obj.album
        tags['album'] = getattr(album, 'title', '')
        
        # Album artists
        if hasattr(album, 'artists') and album.artists:
            tags['ar_album'] = artist_separator.join([getattr(artist, 'name', '') for artist in album.artists])
        else:
            tags['ar_album'] = ''
            
        tags['nb_tracks'] = getattr(album, 'total_tracks', 0)
        
        # Use the album's total_discs field if available, otherwise calculate from tracks
        if hasattr(album, 'total_discs') and album.total_discs:
            tags['nb_discs'] = album.total_discs
        elif hasattr(album, 'tracks') and album.tracks:
            # Fallback: Calculate total discs from all tracks in album for proper metadata
            disc_numbers = [getattr(track, 'disc_number', 1) for track in album.tracks if hasattr(track, 'disc_number')]
            tags['nb_discs'] = max(disc_numbers, default=1)
        else:
            tags['nb_discs'] = 1
            
        # Release date handling
        release_date = getattr(album, 'release_date', None)
        tags['year'] = _format_release_date(release_date, source_type)
        
        # Platform-specific album IDs
        if hasattr(album, 'ids') and album.ids:
            tags['upc'] = getattr(album.ids, 'upc', None)
            tags['album_id'] = _get_platform_id(album.ids, source_type)
        
        # Image handling
        if hasattr(album, 'images'):
            tags['image'] = _get_best_image_url(album.images, source_type)
        else:
            tags['image'] = None
            
        # Additional album metadata
        tags['label'] = getattr(album, 'label', '')
        tags['copyright'] = getattr(album, 'copyright', '')
        
        # Genre handling (more common in Deezer)
        if hasattr(album, 'genres') and album.genres:
            tags['genre'] = "; ".join(album.genres) if isinstance(album.genres, list) else str(album.genres)
        else:
            tags['genre'] = ""
    
    # Default/Placeholder values for compatibility
    tags['bpm'] = tags.get('bpm', 'Unknown')
    tags['gain'] = tags.get('gain', 'Unknown')
    tags['lyric'] = tags.get('lyric', '')
    tags['author'] = tags.get('author', '')
    tags['composer'] = tags.get('composer', '')
    tags['lyricist'] = tags.get('lyricist', '')
    tags['version'] = tags.get('version', '')
    
    return tags


def album_object_to_dict(album_obj: Any, source_type: Optional[str] = None, artist_separator: str = "; ") -> Dict[str, Any]:
    """
    Convert an album object to a dictionary format for tagging.
    Supports both Spotify and Deezer album objects.
    
    Args:
        album_obj: Album object from Spotify or Deezer API
        source_type: Optional source type ('spotify' or 'deezer'). If None, auto-detected.
        artist_separator: Separator string for joining multiple album artists
        
    Returns:
        Dictionary with standardized metadata tags
    """
    if not album_obj:
        return {}
    
    # Auto-detect source if not specified
    if source_type is None:
        source_type = _detect_source_type(album_obj)
        
    tags = {}
    
    # Basic album details
    tags['album'] = getattr(album_obj, 'title', '')
    
    # Album artists
    if hasattr(album_obj, 'artists') and album_obj.artists:
        tags['ar_album'] = artist_separator.join([getattr(artist, 'name', '') for artist in album_obj.artists])
    else:
        tags['ar_album'] = ''
        
    tags['nb_tracks'] = getattr(album_obj, 'total_tracks', 0)
    
    # Release date handling
    release_date = getattr(album_obj, 'release_date', None)
    tags['year'] = _format_release_date(release_date, source_type)
    
    # Platform-specific album IDs
    if hasattr(album_obj, 'ids') and album_obj.ids:
        tags['upc'] = getattr(album_obj.ids, 'upc', None)
        tags['album_id'] = _get_platform_id(album_obj.ids, source_type)
    
    # Image handling
    if hasattr(album_obj, 'images'):
        tags['image'] = _get_best_image_url(album_obj.images, source_type)
    else:
        tags['image'] = None
    
    # Additional metadata
    tags['label'] = getattr(album_obj, 'label', '')
    tags['copyright'] = getattr(album_obj, 'copyright', '')
    
    # Genre handling (more common in Deezer)
    if hasattr(album_obj, 'genres') and album_obj.genres:
        tags['genre'] = "; ".join(album_obj.genres) if isinstance(album_obj.genres, list) else str(album_obj.genres)
    else:
        tags['genre'] = ""
        
    return tags


# Backward compatibility aliases for easy migration
def _track_object_to_dict(track_obj: Any, source_type: Optional[str] = None) -> Dict[str, Any]:
    """Legacy alias for track_object_to_dict"""
    return track_object_to_dict(track_obj, source_type)


def _album_object_to_dict(album_obj: Any, source_type: Optional[str] = None) -> Dict[str, Any]:
    """Legacy alias for album_object_to_dict"""
    return album_object_to_dict(album_obj, source_type) 