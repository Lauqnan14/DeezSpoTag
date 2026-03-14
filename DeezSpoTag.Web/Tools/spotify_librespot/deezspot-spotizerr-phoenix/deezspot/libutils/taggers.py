#!/usr/bin/python3

import os
from typing import Dict, Any, Optional, Union
from deezspot.libutils.utils import request
from deezspot.libutils.logging_utils import logger
from deezspot.libutils.write_tags import write_tags
from deezspot.models.download import Track, Episode


def fetch_and_process_image(image_url_or_bytes: Union[str, bytes, None]) -> Optional[bytes]:
    """
    Fetch and process image data from URL or return bytes directly.
    
    Args:
        image_url_or_bytes: Image URL string, bytes, or None
        
    Returns:
        Image bytes or None if failed/not available
    """
    if not image_url_or_bytes:
        return None
        
    if isinstance(image_url_or_bytes, bytes):
        return image_url_or_bytes
        
    if isinstance(image_url_or_bytes, str):
        try:
            response = request(image_url_or_bytes)
            return response.content
        except Exception as e:
            logger.warning(f"Failed to fetch image from URL {image_url_or_bytes}: {e}")
            return None
            
    return None


def enhance_metadata_with_image(metadata_dict: Dict[str, Any]) -> Dict[str, Any]:
    """
    Enhance metadata dictionary by fetching image data if image URL is present.
    
    Args:
        metadata_dict: Metadata dictionary potentially containing image URL
        
    Returns:
        Enhanced metadata dictionary with image bytes
    """
    image_url = metadata_dict.get('image')
    if image_url:
        image_bytes = fetch_and_process_image(image_url)
        if image_bytes:
            metadata_dict['image'] = image_bytes
    
    return metadata_dict


def add_deezer_enhanced_metadata(
    metadata_dict: Dict[str, Any], 
    infos_dw: Dict[str, Any], 
    track_ids: str,
    api_gw_instance: Any = None
) -> Dict[str, Any]:
    """
    Add Deezer-specific enhanced metadata including contributors, lyrics, and version info.
    
    Args:
        metadata_dict: Base metadata dictionary
        infos_dw: Deezer track information dictionary  
        track_ids: Track IDs for lyrics fetching
        api_gw_instance: API gateway instance for lyrics retrieval
        
    Returns:
        Enhanced metadata dictionary with Deezer-specific data
    """
    # Add contributor information
    contributors = infos_dw.get('SNG_CONTRIBUTORS', {})
    metadata_dict['author'] = "; ".join(contributors.get('author', []))
    metadata_dict['composer'] = "; ".join(contributors.get('composer', []))
    metadata_dict['lyricist'] = "; ".join(contributors.get('lyricist', []))
    
    # Handle composer+lyricist combination
    if contributors.get('composerlyricist'):
        metadata_dict['composer'] = "; ".join(contributors.get('composerlyricist', []))

    # Add version information
    metadata_dict['version'] = infos_dw.get('VERSION', '')
    
    # Initialize lyric fields
    metadata_dict['lyric'] = ""
    metadata_dict['copyright'] = ""
    metadata_dict['lyric_sync'] = []

    # Add lyrics if available and API instance provided
    if api_gw_instance and infos_dw.get('LYRICS_ID', 0) != 0:
        try:
            lyrics_data = api_gw_instance.get_lyric(track_ids)

            if lyrics_data and "LYRICS_TEXT" in lyrics_data:
                metadata_dict['lyric'] = lyrics_data["LYRICS_TEXT"]

            if lyrics_data and "LYRICS_SYNC_JSON" in lyrics_data:
                # Import here to avoid circular imports
                from deezspot.libutils.utils import trasform_sync_lyric
                metadata_dict['lyric_sync'] = trasform_sync_lyric(
                    lyrics_data['LYRICS_SYNC_JSON']
                )
        except Exception as e:
            logger.warning(f"Failed to retrieve lyrics: {str(e)}")
            
    # Extract album artist from contributors with 'Main' role
    if 'contributors' in infos_dw:
        main_artists = [c['name'] for c in infos_dw['contributors'] if c.get('role') == 'Main']
        if main_artists:
            metadata_dict['album_artist'] = "; ".join(main_artists)

    return metadata_dict


def add_spotify_enhanced_metadata(metadata_dict: Dict[str, Any], track_obj: Any) -> Dict[str, Any]:
    """
    Add Spotify-specific enhanced metadata.
    
    Args:
        metadata_dict: Base metadata dictionary
        track_obj: Spotify track object
        
    Returns:
        Enhanced metadata dictionary with Spotify-specific data
    """
    # Spotify tracks already have most metadata from the unified converter
    # Add any Spotify-specific enhancements here if needed in the future
    
    # Ensure image is processed
    return enhance_metadata_with_image(metadata_dict)


def prepare_track_metadata(
    metadata_dict: Dict[str, Any],
    source_type: str = 'unknown',
    enhanced_data: Optional[Dict[str, Any]] = None,
    api_instance: Any = None,
    track_ids: Optional[str] = None
) -> Dict[str, Any]:
    """
    Prepare and enhance track metadata for tagging based on source type.
    
    Args:
        metadata_dict: Base metadata dictionary
        source_type: Source type ('spotify', 'deezer', or 'unknown')
        enhanced_data: Additional source-specific data (infos_dw for Deezer)
        api_instance: API instance for additional data fetching
        track_ids: Track IDs for API calls
        
    Returns:
        Fully prepared metadata dictionary
    """
    # Always process images first
    metadata_dict = enhance_metadata_with_image(metadata_dict)
    
    if source_type == 'deezer' and enhanced_data:
        metadata_dict = add_deezer_enhanced_metadata(
            metadata_dict, 
            enhanced_data, 
            track_ids or '',
            api_instance
        )
    elif source_type == 'spotify':
        metadata_dict = add_spotify_enhanced_metadata(metadata_dict, enhanced_data)
    
    return metadata_dict


def apply_tags_to_track(track: Track, metadata_dict: Dict[str, Any]) -> None:
    """
    Apply metadata tags to a track object and write them to the file.
    
    Args:
        track: Track object to tag
        metadata_dict: Metadata dictionary containing tag information
    """
    if not track or not metadata_dict:
        return
        
    try:
        track.tags = metadata_dict
        try:
            import os
            path = getattr(track, 'song_path', None)
            logger.debug(f"Pre-tagging: path={repr(path)}, exists={os.path.exists(path) if path else None}")
        except Exception:
            pass
        write_tags(track)
        logger.debug(f"Successfully applied tags to track: {metadata_dict.get('music', 'Unknown')}")
    except Exception as e:
        logger.error(f"Failed to apply tags to track: {e}")


def apply_tags_to_episode(episode: Episode, metadata_dict: Dict[str, Any]) -> None:
    """
    Apply metadata tags to an episode object and write them to the file.
    
    Args:
        episode: Episode object to tag  
        metadata_dict: Metadata dictionary containing tag information
    """
    if not episode or not metadata_dict:
        return
        
    try:
        episode.tags = metadata_dict
        write_tags(episode)
        logger.debug(f"Successfully applied tags to episode: {metadata_dict.get('music', 'Unknown')}")
    except Exception as e:
        logger.error(f"Failed to apply tags to episode: {e}")


def save_cover_image_for_track(
    metadata_dict: Dict[str, Any], 
    track_path: str, 
    save_cover: bool = False,
    cover_filename: str = "cover.jpg"
) -> None:
    """
    Save cover image for a track if requested and image data is available.
    
    Args:
        metadata_dict: Metadata dictionary potentially containing image data
        track_path: Path to the track file  
        save_cover: Whether to save cover image
        cover_filename: Filename for the cover image
    """
    if not save_cover or not metadata_dict.get('image'):
        return
        
    try:
        from deezspot.libutils.utils import save_cover_image
        track_directory = os.path.dirname(track_path)
        
        # Handle both URL and bytes
        image_data = metadata_dict['image']
        if isinstance(image_data, str):
            image_bytes = fetch_and_process_image(image_data)
        else:
            image_bytes = image_data
            
        if image_bytes:
            save_cover_image(image_bytes, track_directory, cover_filename)
            logger.info(f"Saved cover image for track in {track_directory}")
    except Exception as e:
        logger.warning(f"Failed to save cover image for track: {e}")


# Convenience function that combines metadata preparation and tagging
def process_and_tag_track(
    track: Track,
    metadata_dict: Dict[str, Any],
    source_type: str = 'unknown',
    enhanced_data: Optional[Dict[str, Any]] = None,
    api_instance: Any = None,
    track_ids: Optional[str] = None,
    save_cover: bool = False
) -> None:
    """
    Complete metadata processing and tagging workflow for a track.
    
    Args:
        track: Track object to process
        metadata_dict: Base metadata dictionary
        source_type: Source type ('spotify', 'deezer', or 'unknown') 
        enhanced_data: Additional source-specific data
        api_instance: API instance for additional data fetching
        track_ids: Track IDs for API calls
        save_cover: Whether to save cover image
    """
    # Prepare enhanced metadata
    prepared_metadata = prepare_track_metadata(
        metadata_dict, 
        source_type, 
        enhanced_data, 
        api_instance, 
        track_ids
    )
    
    # Apply tags to track
    apply_tags_to_track(track, prepared_metadata)
    
    # Save cover image if requested
    if hasattr(track, 'song_path') and track.song_path:
        save_cover_image_for_track(prepared_metadata, track.song_path, save_cover)


def process_and_tag_episode(
    episode: Episode,
    metadata_dict: Dict[str, Any],
    source_type: str = 'unknown',
    enhanced_data: Optional[Dict[str, Any]] = None,
    api_instance: Any = None,
    track_ids: Optional[str] = None,
    save_cover: bool = False
) -> None:
    """
    Complete metadata processing and tagging workflow for an episode.
    
    Args:
        episode: Episode object to process
        metadata_dict: Base metadata dictionary
        source_type: Source type ('spotify', 'deezer', or 'unknown')
        enhanced_data: Additional source-specific data  
        api_instance: API instance for additional data fetching
        track_ids: Track IDs for API calls
        save_cover: Whether to save cover image
    """
    # Prepare enhanced metadata
    prepared_metadata = prepare_track_metadata(
        metadata_dict, 
        source_type, 
        enhanced_data, 
        api_instance, 
        track_ids
    )
    
    # Apply tags to episode
    apply_tags_to_episode(episode, prepared_metadata)
    
    # Save cover image if requested  
    if hasattr(episode, 'episode_path') and episode.episode_path:
        save_cover_image_for_track(prepared_metadata, episode.episode_path, save_cover) 