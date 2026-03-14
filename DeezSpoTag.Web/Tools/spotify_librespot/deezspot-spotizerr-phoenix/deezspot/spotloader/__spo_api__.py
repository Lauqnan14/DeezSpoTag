#!/usr/bin/python3

from deezspot.easy_spoty import Spo
import traceback
from deezspot.libutils.logging_utils import logger
from deezspot.exceptions import MarketAvailabilityError
from typing import List, Optional, Dict, Any

from deezspot.models.callback.album import albumObject, artistAlbumObject, trackAlbumObject as CbTrackAlbumObject, artistTrackAlbumObject
from deezspot.models.callback.artist import artistObject
from deezspot.models.callback.common import IDs
from deezspot.models.callback.playlist import playlistObject, trackPlaylistObject, albumTrackPlaylistObject, artistTrackPlaylistObject, artistAlbumTrackPlaylistObject
from deezspot.models.callback.track import trackObject, artistTrackObject, albumTrackObject, artistAlbumTrackObject
from deezspot.models.callback.user import userObject


def _check_market_availability(item_name: str, item_type: str, api_available_markets: list[str] | None, user_markets: list[str] | None):
    """Checks if an item is available in any of the user-specified markets."""
    if user_markets and api_available_markets is not None:
        is_available_in_any_user_market = any(m in api_available_markets for m in user_markets)
        if not is_available_in_any_user_market:
            markets_str = ", ".join(user_markets)
            raise MarketAvailabilityError(f"{item_type} '{item_name}' not available in provided market(s): {markets_str}")
    elif user_markets and api_available_markets is None:
        logger.warning(
            f"Market availability check for {item_type} '{item_name}' skipped: "
            "API response did not include 'available_markets' field. Assuming availability."
        )

def _parse_release_date(date_str: Optional[str], precision: Optional[str]) -> Dict[str, Any]:
    if not date_str:
        return {}
    
    parts = date_str.split('-')
    data = {}
    
    if len(parts) >= 1 and parts[0]:
        data['year'] = int(parts[0])
    if precision in ['month', 'day'] and len(parts) >= 2 and parts[1]:
        data['month'] = int(parts[1])
    if precision == 'day' and len(parts) >= 3 and parts[2]:
        data['day'] = int(parts[2])
        
    return data

def _json_to_ids(item_json: dict) -> IDs:
    external_ids = item_json.get('external_ids', {})
    return IDs(
        spotify=item_json.get('id'),
        isrc=external_ids.get('isrc'),
        upc=external_ids.get('upc')
    )

def _json_to_artist_track_object(artist_json: dict) -> artistTrackObject:
    return artistTrackObject(
        name=artist_json.get('name', ''),
        ids=_json_to_ids(artist_json)
    )

def _json_to_artist_album_track_object(artist_json: dict) -> artistAlbumTrackObject:
    return artistAlbumTrackObject(
        name=artist_json.get('name', ''),
        ids=_json_to_ids(artist_json)
    )

def _json_to_album_track_object(album_json: dict) -> albumTrackObject:
    return albumTrackObject(
        album_type=album_json.get('album_type', 'album'),
        title=album_json.get('name', ''),
        release_date=_parse_release_date(album_json.get('release_date'), album_json.get('release_date_precision')),
        total_tracks=album_json.get('total_tracks', 0),
        genres=album_json.get('genres', []),
        images=album_json.get('images', []),
        ids=_json_to_ids(album_json),
        artists=[_json_to_artist_album_track_object(artist) for artist in album_json.get('artists', [])]
    )

def tracking(ids, album_data_for_track=None, market: list[str] | None = None) -> Optional[trackObject]:
    try:
        json_track = Spo.get_track(ids)
        if not json_track:
            logger.error(f"Failed to get track details for ID: {ids} from Spotify API.")
            return None

        track_name_for_check = json_track.get('name', f'Track ID {ids}')
        api_track_markets = json_track.get('available_markets')
        _check_market_availability(track_name_for_check, "Track", api_track_markets, market)

        album_to_process = None
        full_album_obj = None
        album_track_number = json_track.get('track_number', 1)  # Default to individual track number
        album_disc_number = json_track.get('disc_number', 1)    # Default to individual disc number
        
        if album_data_for_track:
            album_to_process = album_data_for_track
            # When we have album context, find the track's correct position within its disc
            simplified_tracks = album_data_for_track.get('tracks', {}).get('items', [])
            
            # Group tracks by disc and calculate track numbers relative to each disc
            disc_track_counts = {}
            for track_item in simplified_tracks:
                if track_item and track_item.get('id'):
                    disc_num = track_item.get('disc_number', 1)
                    if disc_num not in disc_track_counts:
                        disc_track_counts[disc_num] = 0
                    
                    if track_item.get('id') == ids:
                        disc_track_counts[disc_num] += 1
                        album_track_number = disc_track_counts[disc_num]  # Track number within disc
                        album_disc_number = disc_num  # Disc number from album context
                        break
                    else:
                        disc_track_counts[disc_num] += 1
        elif json_track.get('album'):
            album_id = json_track.get('album', {}).get('id')
            if album_id:
                # Try to get full album data with all tracks for proper disc counting
                full_album_data = Spo.get_album(album_id)
                if full_album_data:
                    album_to_process = full_album_data
                    # Also create a full album object to get total_discs
                    full_album_obj = tracking_album(full_album_data, market)
            if not album_to_process:
               album_to_process = json_track.get('album')

        album_for_track = _json_to_album_track_object(album_to_process) if album_to_process else albumTrackObject()
        
        # If we have a full album object with total_discs, use that information
        if full_album_obj and hasattr(full_album_obj, 'total_discs'):
            album_for_track.total_discs = full_album_obj.total_discs
        
        track_obj = trackObject(
            title=json_track.get('name', ''),
            disc_number=album_disc_number,
            track_number=album_track_number,
            duration_ms=json_track.get('duration_ms', 0),
            explicit=json_track.get('explicit', False),
            genres=album_for_track.genres,
            album=album_for_track,
            artists=[_json_to_artist_track_object(artist) for artist in json_track.get('artists', [])],
            ids=_json_to_ids(json_track)
        )
        logger.debug(f"Successfully tracked metadata for track {ids}")
        return track_obj
        
    except MarketAvailabilityError:
        raise
    except Exception as e:
        logger.error(f"Failed to track metadata for track {ids}: {str(e)}")
        logger.debug(traceback.format_exc())
        return None

def _json_to_artist_album_object(artist_json: dict) -> artistAlbumObject:
    return artistAlbumObject(
        name=artist_json.get('name', ''),
        ids=_json_to_ids(artist_json)
    )

def _json_to_track_album_object(track_json: dict) -> CbTrackAlbumObject:
    return CbTrackAlbumObject(
        title=track_json.get('name', ''),
        disc_number=track_json.get('disc_number', 1),
        track_number=track_json.get('track_number', 1),
        duration_ms=track_json.get('duration_ms', 0),
        explicit=track_json.get('explicit', False),
        ids=_json_to_ids(track_json),
        artists=[artistTrackAlbumObject(name=a.get('name'), ids=_json_to_ids(a)) for a in track_json.get('artists', [])]
    )

def tracking_album(album_json, market: list[str] | None = None) -> Optional[albumObject]:
    if not album_json:
        logger.error("tracking_album received None or empty album_json.")
        return None
        
    try:
        album_name_for_check = album_json.get('name', f"Album ID {album_json.get('id', 'Unknown')}")
        api_album_markets = album_json.get('available_markets')
        _check_market_availability(album_name_for_check, "Album", api_album_markets, market)

        album_artists = [_json_to_artist_album_object(a) for a in album_json.get('artists', [])]
        
        album_tracks = []
        simplified_tracks = album_json.get('tracks', {}).get('items', [])
        track_ids = [t['id'] for t in simplified_tracks if t and t.get('id')]
        
        full_tracks_data = []
        if track_ids:
            # Batch fetch full track objects. The get_tracks method should handle chunking if necessary.
            full_tracks_data = Spo.get_tracks(track_ids, market=','.join(market) if market else None)

        track_items_to_process = []
        if full_tracks_data and full_tracks_data.get('tracks'):
            # Create a mapping of track ID to full track data to preserve original album order
            full_tracks_by_id = {track['id']: track for track in full_tracks_data['tracks'] if track and track.get('id')}
            
            # Process tracks in the original album order using the full track data when available
            for simplified_track in simplified_tracks:
                if not simplified_track or not simplified_track.get('id'):
                    continue
                
                track_id = simplified_track['id']
                # Use full track data if available, otherwise fall back to simplified
                track_to_use = full_tracks_by_id.get(track_id, simplified_track)
                track_items_to_process.append(track_to_use)
        else: 
            # Fallback to simplified if batch fetch fails
            track_items_to_process = simplified_tracks

        # Group tracks by disc and assign correct track numbers within each disc
        disc_track_counts = {}
        
        for track_item in track_items_to_process:
            if not track_item or not track_item.get('id'):
                continue

            # Get disc number and increment track count for this disc
            disc_num = track_item.get('disc_number', 1)
            if disc_num not in disc_track_counts:
                disc_track_counts[disc_num] = 0
            disc_track_counts[disc_num] += 1

            # For album context, use the track position within its disc for correct numbering
            # This is important for compilation albums and multi-disc albums
            track_item_copy = dict(track_item)  # Create a copy to avoid modifying original
            track_item_copy['track_number'] = disc_track_counts[disc_num]  # Track number within disc
            track_item_copy['disc_number'] = disc_num  # Keep disc number
            
            # Simplified track object from album endpoint is enough for trackAlbumObject
            album_tracks.append(_json_to_track_album_object(track_item_copy))

        # Calculate total discs by finding the maximum disc number
        total_discs = 1
        if album_tracks:
            disc_numbers = [track.disc_number for track in album_tracks if hasattr(track, 'disc_number') and track.disc_number]
            total_discs = max(disc_numbers, default=1)

        album_obj = albumObject(
            album_type=album_json.get('album_type'),
            title=album_json.get('name'),
            release_date=_parse_release_date(album_json.get('release_date'), album_json.get('release_date_precision')),
            total_tracks=album_json.get('total_tracks'),
            total_discs=total_discs,  # Set the calculated total discs
            genres=album_json.get('genres', []),
            images=album_json.get('images', []),
            copyrights=album_json.get('copyrights', []),
            ids=_json_to_ids(album_json),
            tracks=album_tracks,
            artists=album_artists
        )

        logger.debug(f"Successfully tracked metadata for album {album_json.get('id', 'N/A')}")
        return album_obj
                    
    except MarketAvailabilityError:
        raise
    except Exception as e:
        logger.error(f"Failed to track album metadata for album ID {album_json.get('id', 'N/A') if album_json else 'N/A'}: {str(e)}")
        logger.debug(traceback.format_exc())
        return None

def tracking_episode(ids, market: list[str] | None = None) -> Optional[trackObject]:
    try:
        json_episode = Spo.get_episode(ids)
        if not json_episode:
            logger.error(f"Failed to get episode details for ID: {ids} from Spotify API.")
            return None

        episode_name_for_check = json_episode.get('name', f'Episode ID {ids}')
        api_episode_markets = json_episode.get('available_markets')
        _check_market_availability(episode_name_for_check, "Episode", api_episode_markets, market)
        
        show_data = json_episode.get('show', {})
        
        album_for_episode = albumTrackObject(
            album_type='show',
            title=show_data.get('name', 'Unknown Show'),
            total_tracks=show_data.get('total_episodes', 0),
            genres=show_data.get('genres', []),
            images=json_episode.get('images', []),
            ids=IDs(spotify=show_data.get('id')),
            artists=[artistTrackAlbumObject(name=show_data.get('publisher', ''))]
        )

        episode_as_track = trackObject(
            title=json_episode.get('name', 'Unknown Episode'),
            duration_ms=json_episode.get('duration_ms', 0),
            explicit=json_episode.get('explicit', False),
            album=album_for_episode,
            artists=[artistTrackObject(name=show_data.get('publisher', ''))],
            ids=_json_to_ids(json_episode)
        )
        
        logger.debug(f"Successfully tracked metadata for episode {ids}")
        return episode_as_track
        
    except MarketAvailabilityError:
        raise
    except Exception as e:
        logger.error(f"Failed to track episode metadata for ID {ids}: {str(e)}")
        logger.debug(traceback.format_exc())
        return None

def json_to_artist_album_track_playlist_object(artist_json: dict) -> artistAlbumTrackPlaylistObject:
    """Converts a JSON dict to an artistAlbumTrackPlaylistObject."""
    return artistAlbumTrackPlaylistObject(
        name=artist_json.get('name', ''),
        ids=_json_to_ids(artist_json)
    )

def json_to_artist_track_playlist_object(artist_json: dict) -> artistTrackPlaylistObject:
    """Converts a JSON dict to an artistTrackPlaylistObject."""
    return artistTrackPlaylistObject(
        name=artist_json.get('name', ''),
        ids=_json_to_ids(artist_json)
    )

def json_to_album_track_playlist_object(album_json: dict) -> albumTrackPlaylistObject:
    """Converts a JSON dict to an albumTrackPlaylistObject."""
    return albumTrackPlaylistObject(
        album_type=album_json.get('album_type', ''),
        title=album_json.get('name', ''),
        total_tracks=album_json.get('total_tracks', 0),
        release_date=_parse_release_date(album_json.get('release_date'), album_json.get('release_date_precision')),
        images=album_json.get('images', []),
        ids=_json_to_ids(album_json),
        artists=[json_to_artist_album_track_playlist_object(a) for a in album_json.get('artists', [])]
    )

def json_to_track_playlist_object(track_json: dict) -> Optional[trackPlaylistObject]:
    """Converts a JSON dict from a playlist item to a trackPlaylistObject."""
    if not track_json:
        return None
    album_data = track_json.get('album', {})
    return trackPlaylistObject(
        title=track_json.get('name', ''),
        disc_number=track_json.get('disc_number', 1),
        track_number=track_json.get('track_number', 1),
        duration_ms=track_json.get('duration_ms', 0),
        ids=_json_to_ids(track_json),
        album=json_to_album_track_playlist_object(album_data),
        artists=[json_to_artist_track_playlist_object(a) for a in track_json.get('artists', [])]
    )