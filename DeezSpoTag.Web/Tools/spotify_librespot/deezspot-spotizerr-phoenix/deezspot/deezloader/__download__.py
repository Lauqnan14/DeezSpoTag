import os
import json
import requests
import time
from os.path import isfile
from copy import deepcopy
from deezspot.libutils.audio_converter import convert_audio
from deezspot.deezloader.dee_api import API
from deezspot.deezloader.deegw_api import API_GW
from deezspot.deezloader.deezer_settings import qualities
from deezspot.libutils.others_settings import answers
from deezspot.deezloader.__download_utils__ import decryptfile, gen_song_hash
from deezspot.exceptions import (
    TrackNotFound,
    NoRightOnMedia,
    QualityNotFound,
)
from deezspot.models.download import (
    Track,
    Album,
    Playlist,
    Preferences,
    Episode,
)
from deezspot.deezloader.__utils__ import (
    check_track_ids,
    check_track_token,
    check_track_md5,
)
from deezspot.libutils.utils import (
    set_path,
    trasform_sync_lyric,
    create_zip,
    sanitize_name,
    save_cover_image,
    __get_dir as get_album_directory,
)
from deezspot.libutils.write_m3u import create_m3u_file, append_track_to_m3u
from deezspot.libutils.metadata_converter import track_object_to_dict, album_object_to_dict
from deezspot.libutils.download_helpers import (
    build_song_path_with_preferences,
    resolve_deezer_summary_media,
)
from deezspot.libutils.progress_reporter import (
    report_track_initializing, report_track_skipped, report_track_retrying,
    report_track_realtime_progress, report_track_error, report_track_done,
    report_album_initializing, report_album_done, report_playlist_initializing, report_playlist_done
)
from deezspot.libutils.taggers import (
    enhance_metadata_with_image, add_deezer_enhanced_metadata, add_spotify_enhanced_metadata, process_and_tag_track,
    save_cover_image_for_track
)
from mutagen.flac import FLAC
from mutagen.mp3 import MP3
from mutagen.id3 import ID3
from mutagen.mp4 import MP4
from mutagen import File
from deezspot.libutils.logging_utils import logger, ProgressReporter, report_progress
from deezspot.libutils.skip_detection import check_track_exists
from deezspot.libutils.cleanup_utils import register_active_download, unregister_active_download
from deezspot.libutils.audio_converter import AUDIO_FORMATS # Added for parse_format_string
from deezspot.models.callback.callbacks import (
    TrackCallbackObject,
    AlbumCallbackObject,
    PlaylistCallbackObject,
    InitializingObject,
    SkippedObject,
    RetryingObject,
    RealTimeObject,
    ErrorObject,
    DoneObject,
    SummaryObject,
    FailedTrackObject,
)
from deezspot.models.callback.track import TrackObject as trackCbObject, AlbumTrackObject, ArtistTrackObject, PlaylistTrackObject
from deezspot.models.callback.album import AlbumObject as albumCbObject
from deezspot.models.callback.playlist import PlaylistObject as playlistCbObject
from deezspot.models.callback.common import IDs, Service
from deezspot.models.callback.user import UserObject

UNKNOWN_ARTIST_NAME = "Unknown Artist"
UNKNOWN_EPISODE_TITLE = "Unknown Episode"
REQUEST_TIMEOUT_SECONDS = 20

# Use unified metadata converter
def _track_object_to_dict(track_obj: trackCbObject) -> dict:
    """
    Convert a track object to a dictionary format for tagging.
    Similar to spotloader's approach for consistent metadata handling.
    """
    return track_object_to_dict(track_obj, source_type='deezer')

# Use unified metadata converter  
def _album_object_to_dict(album_obj: albumCbObject) -> dict:
    """
    Convert an album object to a dictionary format for tagging.
    Similar to spotloader's approach for consistent metadata handling.
    """
    return album_object_to_dict(album_obj, source_type='deezer')

class DownloadJob:
    progress_reporter = None
    
    @classmethod
    def set_progress_reporter(cls, reporter):
        cls.progress_reporter = reporter
        
    @classmethod
    def __get_url(cls, c_track: Track, quality_download: str) -> dict:
        if c_track.get('__TYPE__') == 'episode':
            return {
                "media": [{
                    "sources": [{
                        "url": c_track.get('EPISODE_DIRECT_STREAM_URL')
                    }]
                }]
            }
        else:
            # Get track IDs and check which encryption method is available
            track_info = check_track_ids(c_track)
            encryption_type = track_info.get('encryption_type', 'blowfish')
            
            # If AES encryption is available (MEDIA_KEY and MEDIA_NONCE present)
            if encryption_type == 'aes':
                # Use track token to get media URL from API
                track_token = check_track_token(c_track)
                medias = API_GW.get_medias_url([track_token], quality_download)
                return medias[0]
            
            # Use Blowfish encryption (legacy method)
            else:
                md5_origin = track_info.get('md5_origin')
                media_version = track_info.get('media_version', '1')
                track_id = track_info.get('track_id')
                
                if not md5_origin:
                    raise ValueError("MD5_ORIGIN is missing")
                if not track_id:
                    raise ValueError("Track ID is missing")
                
                # Create the song hash using the correct parameter order
                # Note: For legacy Deezer API, the order is: MD5 + Media Version + Track ID
                c_song_hash = gen_song_hash(track_id, md5_origin, media_version)
                
                # Log the hash generation parameters for debugging
                logger.debug(f"Generating song hash with: track_id={track_id}, md5_origin={md5_origin}, media_version={media_version}")
                
                c_media_url = API_GW.get_song_url(md5_origin[0], c_song_hash)
                
                return {
                    "media": [
                        {
                            "sources": [
                                {
                                    "url": c_media_url
                                }
                            ]
                        }
                    ]
                }

    @staticmethod
    def _chunk_list(items: list, chunk_size: int):
        for start in range(0, len(items), chunk_size):
            yield items[start:start + chunk_size]

    @staticmethod
    def _normalize_chunk_media(chunk_medias: list) -> list:
        normalized = []
        for media_entry in chunk_medias:
            if "errors" in media_entry:
                normalized.append({"media": []})
                continue
            media_list = media_entry.get('media') if isinstance(media_entry, dict) else None
            normalized.append(media_entry if media_list else {"media": []})
        return normalized

    @classmethod
    def _episode_medias(cls, infos_dw: list, quality_download: str) -> list:
        episode_medias = []
        for track in infos_dw:
            if track.get('__TYPE__') == 'episode':
                episode_medias.append(cls.__get_url(track, quality_download))
        return episode_medias

    @classmethod
    def _non_episode_medias(cls, non_episode_tracks: list, quality_download: str) -> list:
        from deezspot.deezloader.deegw_api import API_GW

        tokens = [check_track_token(track) for track in non_episode_tracks]
        medias: list = []
        for token_chunk in cls._chunk_list(tokens, 25):
            try:
                chunk_medias = API_GW.get_medias_url(token_chunk, quality_download)
                medias.extend(cls._normalize_chunk_media(chunk_medias))
            except NoRightOnMedia:
                for _ in token_chunk:
                    track_index = len(medias)
                    medias.append(cls.__get_url(non_episode_tracks[track_index], quality_download))
        return medias

    @staticmethod
    def _merge_episode_and_track_medias(infos_dw: list, episode_medias: list, non_episode_medias: list) -> list:
        merged_medias = []
        episode_idx = 0
        non_episode_idx = 0
        for track in infos_dw:
            if track.get('__TYPE__') == 'episode':
                merged_medias.append(episode_medias[episode_idx])
                episode_idx += 1
                continue
            merged_medias.append(non_episode_medias[non_episode_idx])
            non_episode_idx += 1
        return merged_medias
     
    @classmethod
    def check_sources(
        cls,
        infos_dw: list,
        quality_download: str  
    ) -> list:
        episode_medias = cls._episode_medias(infos_dw, quality_download)
        non_episode_tracks = [track for track in infos_dw if track.get('__TYPE__') != 'episode']
        non_episode_medias = cls._non_episode_medias(non_episode_tracks, quality_download)
        return cls._merge_episode_and_track_medias(infos_dw, episode_medias, non_episode_medias)

class EasyDw:
    progress_reporter = None
    
    @classmethod
    def set_progress_reporter(cls, reporter):
        cls.progress_reporter = reporter

    def _episode_song_metadata(self) -> dict:
        return {
            'music': self.__infos_dw.get('EPISODE_TITLE', ''),
            'artist': self.__infos_dw.get('SHOW_NAME', ''),
            'album': self.__infos_dw.get('SHOW_NAME', ''),
            'date': self.__infos_dw.get('EPISODE_PUBLISHED_TIMESTAMP', '').split()[0],
            'genre': 'Podcast',
            'explicit': self.__infos_dw.get('SHOW_IS_EXPLICIT', '2'),
            'disc': 1,
            'track': 1,
            'duration': int(self.__infos_dw.get('DURATION', 0)),
            'isrc': None,
        }

    def _build_track_song_metadata(self, preferences: Preferences) -> None:
        self.__track_obj: trackCbObject = preferences.song_metadata
        if getattr(preferences, 'spotify_metadata', False) and getattr(preferences, 'spotify_track_obj', None):
            self.__track_obj = preferences.spotify_track_obj

        artist_separator = getattr(preferences, 'artist_separator', '; ')
        use_spotify = getattr(preferences, 'spotify_metadata', False)
        source_type = 'spotify' if use_spotify else 'deezer'
        self.__song_metadata_dict = track_object_to_dict(
            self.__track_obj,
            source_type=source_type,
            artist_separator=artist_separator,
        )
        self.__song_metadata = self.__song_metadata_dict
        self._backfill_spotify_album_fields(preferences, use_spotify, artist_separator)

    def _backfill_spotify_album_fields(self, preferences: Preferences, use_spotify: bool, artist_separator: str) -> None:
        try:
            if not use_spotify:
                return
            if 'album' in self.__song_metadata_dict:
                return
            spotify_album = getattr(preferences, 'spotify_album_obj', None)
            if not spotify_album:
                return
            self.__song_metadata_dict['album'] = getattr(spotify_album, 'title', '')
            if getattr(spotify_album, 'artists', None):
                album_artists = [getattr(artist, 'name', '') for artist in spotify_album.artists]
                self.__song_metadata_dict['ar_album'] = artist_separator.join(album_artists)
            self.__song_metadata_dict['nb_tracks'] = getattr(spotify_album, 'total_tracks', 0)
            if hasattr(spotify_album, 'total_discs') and getattr(spotify_album, 'total_discs'):
                self.__song_metadata_dict['nb_discs'] = getattr(spotify_album, 'total_discs')

            from deezspot.libutils.metadata_converter import _format_release_date, _get_best_image_url

            self.__song_metadata_dict['year'] = _format_release_date(getattr(spotify_album, 'release_date', None), 'spotify')
            if getattr(spotify_album, 'ids', None):
                self.__song_metadata_dict['upc'] = getattr(spotify_album.ids, 'upc', None)
                self.__song_metadata_dict['album_id'] = getattr(spotify_album.ids, 'spotify', None)
            if getattr(spotify_album, 'images', None):
                image_url = _get_best_image_url(spotify_album.images, 'spotify')
                if image_url:
                    self.__song_metadata_dict['image'] = image_url
        except Exception as exc:
            logger.debug("Unable to backfill Spotify album fields: %s", exc)

    def _initialize_song_metadata(self, preferences: Preferences) -> None:
        if self.__infos_dw.get('__TYPE__') == 'episode':
            self.__song_metadata = self._episode_song_metadata()
            return
        self._build_track_song_metadata(preferences)
        
    def __init__(
        self,
        infos_dw: dict,
        preferences: Preferences,
        parent: str = None  # Can be 'album', 'playlist', or None for individual track
    ) -> None:
        
        self.__preferences = preferences
        self.__parent = parent  # Store the parent type
        
        self.__infos_dw = infos_dw
        self.__ids = preferences.ids
        self.__link = preferences.link
        self.__output_dir = preferences.output_dir
        self.__quality_download = preferences.quality_download
        self.__recursive_quality = preferences.recursive_quality
        self.__convert_to = getattr(preferences, 'convert_to', None)
        self.__bitrate = getattr(preferences, 'bitrate', None) # Added for consistency

        self._initialize_song_metadata(preferences)

        self.__c_quality = qualities[self.__quality_download]
        self.__fallback_ids = self.__ids

        self.__set_quality()
        self.__write_track()

    def _get_parent_context(self):
        parent_obj = None
        current_track_val = None
        total_tracks_val = None

        if self.__parent == "playlist" and hasattr(self.__preferences, "json_data") and self.__preferences.json_data:
            playlist_data = self.__preferences.json_data
            
            if isinstance(playlist_data, dict):
                # Spotify raw dict
                parent_obj = PlaylistTrackObject(
                    title=playlist_data.get('name', 'unknown'),
                    description=playlist_data.get('description', ''),
                    owner=UserObject(name=playlist_data.get('owner', {}).get('display_name', 'unknown')),
                    ids=IDs(spotify=playlist_data.get('id', ''))
                )
            else:
                # Deezer PlaylistObject
                playlist_data_obj: playlistCbObject = playlist_data
                parent_obj = PlaylistTrackObject(
                    title=playlist_data_obj.title,
                    description=playlist_data_obj.description,
                    owner=playlist_data_obj.owner,
                    ids=playlist_data_obj.ids
                )
            
            total_tracks_val = getattr(self.__preferences, 'total_tracks', 0)
            current_track_val = getattr(self.__preferences, 'track_number', 0)

        elif self.__parent == "album" and hasattr(self.__preferences, "json_data") and self.__preferences.json_data:
            album_data = self.__preferences.json_data
            album_id = album_data.ids.deezer
            parent_obj = albumCbObject(
                title=album_data.title,
                artists=[ArtistTrackObject(name=artist.name) for artist in album_data.artists],
                ids=IDs(deezer=album_id)
            )
            total_tracks_val = getattr(self.__preferences, 'total_tracks', 0)
            current_track_val = getattr(self.__preferences, 'track_number', 0)

        return parent_obj, current_track_val, total_tracks_val

    def _track_object_to_dict(self, track_obj: any) -> dict:
        """
        Helper to convert a track object (of any kind) to the dict format 
        expected by legacy tagging and path functions.
        It intelligently finds the album information based on the download context.
        """
        # Use the unified metadata converter
        artist_separator = getattr(self.__preferences, 'artist_separator', '; ')
        metadata_dict = track_object_to_dict(track_obj, source_type='deezer', artist_separator=artist_separator)
        
        # Check for track_position and disk_number in the original API data
        # These might be directly available in the infos_dw dictionary for Deezer tracks
        if self.__infos_dw:
            if 'track_position' in self.__infos_dw:
                metadata_dict['tracknum'] = self.__infos_dw['track_position']
            if 'disk_number' in self.__infos_dw:
                metadata_dict['discnum'] = self.__infos_dw['disk_number']

        return metadata_dict

    def __set_quality(self) -> None:
        self.__file_format = self.__c_quality['f_format']
        self.__song_quality = self.__c_quality['s_quality']

    def __set_song_path(self) -> None:
        self.__song_path = build_song_path_with_preferences(
            set_path_func=set_path,
            song_metadata_dict=self.__song_metadata_dict,
            output_dir=self.__output_dir,
            song_quality=self.__song_quality,
            file_format=self.__file_format,
            preferences=self.__preferences,
            parent=self.__parent,
            song_metadata=self.__track_obj,
        )
    
    def __set_episode_path(self) -> None:
        custom_dir_format = getattr(self.__preferences, 'custom_dir_format', None)
        custom_track_format = getattr(self.__preferences, 'custom_track_format', None)
        pad_tracks = getattr(self.__preferences, 'pad_tracks', True)
        self.__song_path = set_path(
            self.__song_metadata_dict,
            self.__output_dir,
            self.__song_quality,
            self.__file_format,
            is_episode=True,
            custom_dir_format=custom_dir_format,
            custom_track_format=custom_track_format,
            pad_tracks=pad_tracks
        )

    def __write_track(self) -> None:
        self.__set_song_path()

        self.__c_track = Track(
            self.__song_metadata, self.__song_path,
            self.__file_format, self.__song_quality,
            self.__link, self.__ids
        )

        self.__c_track.set_fallback_ids(self.__fallback_ids)
    
    def __write_episode(self) -> None:
        self.__set_episode_path()

        self.__c_episode = Episode(
            self.__song_metadata, self.__song_path,
            self.__file_format, self.__song_quality,
            self.__link, self.__ids
        )

        self.__c_episode.md5_image = self.__ids
        self.__c_episode.set_fallback_ids(self.__fallback_ids)

    def _prepare_image_metadata(self):
        pic = None
        if self.__infos_dw.get('__TYPE__') == 'episode':
            pic = self.__infos_dw.get('EPISODE_IMAGE_MD5', '')
            image = API.choose_img(pic)
        elif (
            getattr(self.__preferences, 'spotify_metadata', False)
            and hasattr(self.__track_obj, 'album')
            and getattr(self.__track_obj.album, 'images', None)
        ):
            from deezspot.libutils.metadata_converter import _get_best_image_url
            image = _get_best_image_url(self.__track_obj.album.images, 'spotify')
        else:
            pic = self.__infos_dw['ALB_PICTURE']
            image = API.choose_img(pic)
        self.__song_metadata['image'] = image
        self.__song_metadata = enhance_metadata_with_image(self.__song_metadata)
        return pic

    def _skipped_track_if_exists(self):
        current_title = self.__song_metadata['music']
        current_album = self.__song_metadata['album']
        current_artist = self.__song_metadata.get('artist')
        exists, existing_file_path = check_track_exists(
            original_song_path=self.__song_path,
            title=current_title,
            album=current_album,
            convert_to=self.__convert_to,
            logger=logger,
        )
        if not (exists and existing_file_path):
            return None

        logger.info(f"Track '{current_title}' by '{current_artist}' already exists at '{existing_file_path}'. Skipping download.")
        self.__c_track.song_path = existing_file_path
        _, new_ext = os.path.splitext(existing_file_path)
        self.__c_track.file_format = new_ext.lower()
        self.__c_track.success = True
        self.__c_track.was_skipped = True
        parent_obj, current_track_val, total_tracks_val = self._get_parent_context()
        report_track_skipped(
            track_obj=self.__track_obj,
            reason=f"Track already exists in desired format at {existing_file_path}",
            preferences=self.__preferences,
            parent_obj=parent_obj,
            current_track=current_track_val,
            total_tracks=total_tracks_val,
        )
        skipped_item = Track(
            self.__song_metadata,
            existing_file_path,
            self.__c_track.file_format,
            self.__song_quality,
            self.__link,
            self.__ids,
        )
        skipped_item.success = True
        skipped_item.was_skipped = True
        self.__c_track = skipped_item
        return self.__c_track

    def _mark_item_pending(self):
        if self.__infos_dw.get('__TYPE__') == 'episode':
            if hasattr(self, '_EasyDw__c_episode') and self.__c_episode:
                self.__c_episode.success = False
            return
        if hasattr(self, '_EasyDw__c_track') and self.__c_track:
            self.__c_track.success = False

    def _report_track_done(self):
        if not self.__c_track.success:
            return
        parent_obj, current_track_val, total_tracks_val = self._get_parent_context()
        final_path_val = getattr(self.__c_track, 'song_path', None)
        download_quality_val = self.__quality_download
        if not download_quality_val:
            file_format = getattr(self.__c_track, 'file_format', None)
            if file_format:
                download_quality_val = file_format.upper().lstrip('.')

        done_status = DoneObject(
            ids=self.__track_obj.ids,
            convert_to=self.__convert_to,
            final_path=final_path_val,
            download_quality=download_quality_val,
        )
        if self.__parent is None:
            summary = SummaryObject(
                successful_tracks=[self.__track_obj],
                total_successful=1,
                service=Service.DEEZER,
            )
            summary.final_path = final_path_val
            summary.download_quality = download_quality_val
            quality_val, bitrate_val = resolve_deezer_summary_media(
                quality_download=self.__quality_download,
                convert_to=self.__convert_to,
                bitrate=self.__bitrate,
            )
            summary.quality = quality_val
            summary.bitrate = bitrate_val
            done_status.summary = summary

        callback_obj = TrackCallbackObject(
            track=self.__track_obj,
            status_info=done_status,
            parent=parent_obj,
            current_track=current_track_val,
            total_tracks=total_tracks_val,
        )
        report_progress(reporter=Download_JOB.progress_reporter, callback_obj=callback_obj)

    def _run_download_flow(self):
        if self.__infos_dw.get('__TYPE__') == 'episode':
            self.download_episode_try()
            return
        self.download_try()
        self._report_track_done()

    def _raise_easy_dw_failure(self, error: Exception):
        item_type = "Episode" if self.__infos_dw.get('__TYPE__') == 'episode' else "Track"
        item_name = self.__song_metadata.get('music', f'Unknown {item_type}')
        artist_name = self.__song_metadata.get('artist', UNKNOWN_ARTIST_NAME)
        error_message = f"Download process failed for {item_type.lower()} '{item_name}' by '{artist_name}' (URL: {self.__link}). Error: {str(error)}"
        logger.error(error_message)
        current_item = self.__c_episode if self.__infos_dw.get('__TYPE__') == 'episode' else self.__c_track
        if current_item:
            current_item.success = False
            current_item.error_message = error_message
        if item_type == "Track":
            parent_obj, current_track_val, total_tracks_val = self._get_parent_context()
            error_obj = ErrorObject(ids=self.__track_obj.ids, error=error_message)
            callback_obj = TrackCallbackObject(
                track=self.__track_obj,
                status_info=error_obj,
                parent=parent_obj,
                current_track=current_track_val,
                total_tracks=total_tracks_val,
            )
            report_progress(reporter=Download_JOB.progress_reporter, callback_obj=callback_obj)
        raise TrackNotFound(message=error_message, url=self.__link) from error

    def _current_item(self):
        return self.__c_episode if self.__infos_dw.get('__TYPE__') == 'episode' else self.__c_track

    def _validate_current_item(self, current_item):
        if current_item is None:
            raise TrackNotFound(message="Download did not produce a track/episode object.", url=self.__link)
        if getattr(current_item, 'was_skipped', False) or current_item.success:
            return current_item
        item_type_str = "episode" if self.__infos_dw.get('__TYPE__') == 'episode' else "track"
        item_name = self.__song_metadata.get('music', f'Unknown {item_type_str.capitalize()}')
        artist_name = self.__song_metadata.get('artist', UNKNOWN_ARTIST_NAME)
        original_error_msg = getattr(
            current_item,
            'error_message',
            f"Download failed for an unspecified reason after {item_type_str} processing attempt.",
        )
        final_error_msg = "Cannot download {type} '{title}' by '{artist}'. Reason: {reason}".format(
            type=item_type_str,
            title=item_name,
            artist=artist_name,
            reason=original_error_msg,
        )
        current_link = current_item.link if hasattr(current_item, 'link') and current_item.link else self.__link
        logger.error(f"{final_error_msg} (URL: {current_link})")
        current_item.error_message = final_error_msg
        raise TrackNotFound(message=final_error_msg, url=current_link)

    def _tag_current_item(self, current_item, pic):
        if self.__infos_dw.get('__TYPE__') != 'episode' and pic:
            current_item.md5_image = pic
        from deezspot.deezloader.deegw_api import API_GW
        if getattr(self.__preferences, 'spotify_metadata', False):
            enhanced_metadata = add_spotify_enhanced_metadata(self.__song_metadata, self.__track_obj)
            process_and_tag_track(track=current_item, metadata_dict=enhanced_metadata, source_type='spotify')
            return
        enhanced_metadata = add_deezer_enhanced_metadata(
            self.__song_metadata,
            self.__infos_dw,
            self.__ids,
            API_GW,
        )
        process_and_tag_track(track=current_item, metadata_dict=enhanced_metadata, source_type='deezer')

    def easy_dw(self) -> Track:
        pic = self._prepare_image_metadata()
        skipped_item = self._skipped_track_if_exists()
        if skipped_item:
            return skipped_item

        self._mark_item_pending()
        try:
            self._run_download_flow()
        except Exception as error:
            self._raise_easy_dw_failure(error)

        current_item = self._current_item()
        current_item = self._validate_current_item(current_item)
        if getattr(current_item, 'was_skipped', False):
            return current_item
        self._tag_current_item(current_item, pic)
        return current_item

    def _song_and_artist(self) -> tuple[str, str]:
        return self.__song_metadata['music'], self.__song_metadata['artist']

    def _switch_to_mp3_320(self) -> None:
        self.__quality_download = 'MP3_320'
        self.__file_format = '.mp3'
        self.__song_path = self.__song_path.rsplit('.', 1)[0] + '.mp3'

    def _ensure_flac_has_media(self) -> None:
        if self.__file_format != '.flac':
            return
        filesize_str = self.__infos_dw.get('FILESIZE_FLAC', '0')
        try:
            filesize = int(filesize_str)
        except ValueError:
            filesize = 0
        if filesize != 0:
            return
        song, artist = self._song_and_artist()
        if not self.__recursive_quality:
            raise QualityNotFound(f"FLAC not available for {song} - {artist} and recursive quality search is disabled.")
        self._switch_to_mp3_320()
        media = Download_JOB.check_sources([self.__infos_dw], 'MP3_320')
        if media:
            self.__infos_dw['media_url'] = media[0]
            return
        raise TrackNotFound(f"Track {song} - {artist} not available in MP3 format after FLAC attempt failed (filesize was 0).")

    @staticmethod
    def _find_available_stream(media_list: list, api_gw):
        crypted_audio = None
        last_error = None
        for media_entry in media_list:
            sources = media_entry.get('sources') or []
            for source in sources:
                song_link = source.get('url')
                if not song_link:
                    continue
                try:
                    crypted_audio = api_gw.song_exist(song_link)
                    if crypted_audio:
                        return crypted_audio, None
                except Exception as source_error:
                    last_error = source_error
        return None, last_error

    def _find_stream_for_quality(self, quality: str, api_gw):
        media = Download_JOB.check_sources([self.__infos_dw], quality)
        if not media:
            return None, None
        self.__infos_dw['media_url'] = media[0]
        media_list = self.__infos_dw['media_url']['media']
        return self._find_available_stream(media_list, api_gw)

    def _resolve_stream_with_fallbacks(self, api_gw):
        media_list = self.__infos_dw['media_url']['media']
        crypted_audio, last_error = self._find_available_stream(media_list, api_gw)
        if crypted_audio:
            return crypted_audio, last_error

        song, artist = self._song_and_artist()
        if self.__file_format == '.flac':
            if not self.__recursive_quality:
                raise QualityNotFound(f"FLAC not available for {song} - {artist} and recursive quality search is disabled.")
            logger.warning(f"\n⚠ {song} - {artist} is not available in FLAC format. Trying MP3...")
            self._switch_to_mp3_320()
            crypted_audio, last_error = self._find_stream_for_quality('MP3_320', api_gw)
            if crypted_audio:
                return crypted_audio, last_error
            raise TrackNotFound(
                f"Track {song} - {artist} not available in MP3 after FLAC attempt failed. Last error: {last_error}"
            )

        if not self.__recursive_quality:
            raise QualityNotFound(
                f"Quality {self.__quality_download} not found for {song} - {artist} and recursive quality search is disabled."
            )
        for quality in qualities:
            if self.__quality_download == quality:
                continue
            crypted_audio, last_error = self._find_stream_for_quality(quality, api_gw)
            if not crypted_audio:
                continue
            self.__c_quality = qualities[quality]
            self.__set_quality()
            return crypted_audio, last_error
        raise TrackNotFound(f"Error with {song} - {artist}. All available qualities failed. Last error: {last_error}. Link: {self.__link}")

    def _apply_download_tags(self, save_cover: bool = False) -> None:
        from deezspot.deezloader.deegw_api import API_GW
        use_spotify = getattr(self.__preferences, 'spotify_metadata', False)
        if use_spotify:
            enhanced_metadata = add_spotify_enhanced_metadata(self.__song_metadata, self.__track_obj)
            process_and_tag_track(
                track=self.__c_track,
                metadata_dict=enhanced_metadata,
                source_type='spotify',
                save_cover=save_cover,
            )
            return
        enhanced_metadata = add_deezer_enhanced_metadata(
            self.__song_metadata,
            self.__infos_dw,
            self.__ids,
            API_GW,
        )
        process_and_tag_track(
            track=self.__c_track,
            metadata_dict=enhanced_metadata,
            source_type='deezer',
            save_cover=save_cover,
        )

    def _convert_track_if_needed(self) -> None:
        if not self.__convert_to:
            return
        format_name, bitrate = self._parse_format_string(self.__convert_to)
        if not format_name:
            return
        path_before_conversion = self.__song_path
        try:
            converted_path = convert_audio(
                path_before_conversion,
                format_name,
                bitrate if bitrate else self.__bitrate,
                register_active_download,
                unregister_active_download,
            )
        except Exception as conv_error:
            logger.error(f"Audio conversion error: {str(conv_error)}. Proceeding with original format.")
            register_active_download(path_before_conversion)
            return
        if converted_path == path_before_conversion:
            return
        self.__song_path = converted_path
        self.__c_track.song_path = converted_path
        _, new_ext = os.path.splitext(converted_path)
        self.__file_format = new_ext.lower()
        self.__c_track.file_format = new_ext.lower()

    def _handle_decrypt_failure(self, decrypt_error, parent_obj, current_track_val, total_tracks_val):
        unregister_active_download(self.__song_path)
        if isfile(self.__song_path):
            try:
                os.remove(self.__song_path)
            except OSError:
                logger.warning(f"Could not remove partially downloaded file: {self.__song_path}")
        self.__c_track.success = False
        self.__c_track.error_message = f"Decryption failed: {str(decrypt_error)}"
        error_status = ErrorObject(
            ids=self.__track_obj.ids,
            error=f"Decryption failed: {str(decrypt_error)}",
            convert_to=self.__convert_to,
        )
        error_callback_obj = TrackCallbackObject(
            track=self.__track_obj,
            status_info=error_status,
            parent=parent_obj,
            current_track=current_track_val,
            total_tracks=total_tracks_val,
        )
        report_progress(reporter=Download_JOB.progress_reporter, callback_obj=error_callback_obj)
        raise TrackNotFound(f"Failed to process {self.__song_path}. Error: {str(decrypt_error)}") from decrypt_error

    @staticmethod
    def _normalize_processing_error(error: Exception) -> str:
        error_msg = str(error)
        if "Data must be padded" in error_msg:
            return "Decryption error (padding issue) - Try a different quality setting or download format"
        if isinstance(error, ConnectionError) or "Connection" in error_msg:
            return "Connection error - Check your internet connection"
        if "timeout" in error_msg.lower():
            return "Request timed out - Server may be busy"
        if "403" in error_msg or "Forbidden" in error_msg:
            return "Access denied - Track might be region-restricted or premium-only"
        if "404" in error_msg or "Not Found" in error_msg:
            return "Track not found - It might have been removed"
        return error_msg

    def _handle_processing_failure(self, error, parent_obj, current_track_val, total_tracks_val):
        unregister_active_download(self.__song_path)
        if isfile(self.__song_path):
            try:
                os.remove(self.__song_path)
            except OSError:
                logger.warning(f"Could not remove file on error: {self.__song_path}")
        error_msg = self._normalize_processing_error(error)
        error_status = ErrorObject(ids=self.__track_obj.ids, error=error_msg, convert_to=self.__convert_to)
        callback_obj = TrackCallbackObject(
            track=self.__track_obj,
            status_info=error_status,
            parent=parent_obj,
            current_track=current_track_val,
            total_tracks=total_tracks_val,
        )
        report_progress(reporter=Download_JOB.progress_reporter, callback_obj=callback_obj)
        logger.error(f"Failed to process track: {error_msg}")
        self.__c_track.success = False
        self.__c_track.error_message = error_msg
        raise TrackNotFound(f"Failed to process {self.__song_path}. Error: {error_msg}. Original Exception: {str(error)}")

    def download_try(self) -> Track:
        from deezspot.deezloader.deegw_api import API_GW
        try:
            self._ensure_flac_has_media()
            crypted_audio, _ = self._resolve_stream_with_fallbacks(API_GW)
            c_crypted_audio = crypted_audio.iter_content(2048)
            self.__fallback_ids = check_track_ids(self.__infos_dw)
            encryption_type = self.__fallback_ids.get('encryption_type', 'unknown')
            logger.debug(f"Using encryption type: {encryption_type}")
            parent_obj, current_track_val, total_tracks_val = self._get_parent_context()
            try:
                self.__write_track()
                report_track_initializing(
                    track_obj=self.__track_obj,
                    preferences=self.__preferences,
                    parent_obj=parent_obj,
                    current_track=current_track_val,
                    total_tracks=total_tracks_val
                )
                register_active_download(self.__song_path)
                try:
                    decryptfile(c_crypted_audio, self.__fallback_ids, self.__song_path)
                    logger.debug(f"Successfully decrypted track using {encryption_type} encryption")
                except Exception as e_decrypt:
                    self._handle_decrypt_failure(e_decrypt, parent_obj, current_track_val, total_tracks_val)

                self._apply_download_tags(save_cover=getattr(self.__preferences, 'save_cover', False))
                self._convert_track_if_needed()
                self._apply_download_tags(save_cover=False)
                self.__c_track.success = True
                unregister_active_download(self.__song_path)
            except Exception as e:
                self._handle_processing_failure(e, parent_obj, current_track_val, total_tracks_val)
            return self.__c_track

        except Exception as e:
            song_title = self.__song_metadata.get('music', 'Unknown Song')
            artist_name = self.__song_metadata.get('artist', UNKNOWN_ARTIST_NAME)
            error_message = f"Download failed for '{song_title}' by '{artist_name}' (Link: {self.__link}). Error: {str(e)}"
            logger.error(error_message)
            unregister_active_download(self.__song_path)
            if hasattr(self, '_EasyDw__c_track') and self.__c_track:
                self.__c_track.success = False
                self.__c_track.error_message = str(e)
            raise TrackNotFound(message=error_message, url=self.__link) from e

    def download_episode_try(self) -> Episode:
        try:
            direct_url = self.__infos_dw.get('EPISODE_DIRECT_STREAM_URL')
            if not direct_url:
                raise TrackNotFound("No direct stream URL found")

            os.makedirs(os.path.dirname(self.__song_path), exist_ok=True)
            
            register_active_download(self.__song_path)
            try:
                response = requests.get(
                    direct_url,
                    stream=True,
                    timeout=REQUEST_TIMEOUT_SECONDS,
                )
                response.raise_for_status()

                with open(self.__song_path, 'wb') as f:
                    for chunk in response.iter_content(chunk_size=8192):
                        if chunk:
                            f.write(chunk)
                            
                            # Download progress reporting could be added here
                
                # If download successful, unregister the initially downloaded file before potential conversion
                unregister_active_download(self.__song_path)


                # Build episode progress report
                progress_data = {
                    "type": "episode",
                    "song": self.__song_metadata.get('music', UNKNOWN_EPISODE_TITLE),
                    "artist": self.__song_metadata.get('artist', 'Unknown Show'),
                    "status": "done"
                }
                
                # Use Spotify URL if available (for downloadspo functions), otherwise use Deezer link
                spotify_url = getattr(self.__preferences, 'spotify_url', None)
                progress_data["url"] = spotify_url if spotify_url else self.__link
                
                Download_JOB.progress_reporter.report(progress_data)
                
                self.__c_track.success = True
                self.__write_episode()
                # Apply tags using unified utility with Deezer enhancements
                from deezspot.deezloader.deegw_api import API_GW
                enhanced_metadata = add_deezer_enhanced_metadata(
                    self.__song_metadata,
                    self.__infos_dw,
                    self.__ids,
                    API_GW
                )
                process_and_tag_track(
                    track=self.__c_track,
                    metadata_dict=enhanced_metadata,
                    source_type='deezer'
                )
            
                return self.__c_track

            except Exception as e_dw_ep: # Catches errors from requests.get, file writing
                unregister_active_download(self.__song_path) # Unregister if download part failed
                if isfile(self.__song_path):
                    try:
                        os.remove(self.__song_path)
                    except OSError:
                        logger.warning(f"Could not remove episode file on error: {self.__song_path}")
                self.__c_track.success = False # Mark as failed
                episode_title = self.__preferences.song_metadata.get('music', UNKNOWN_EPISODE_TITLE)
                err_msg = f"Episode download failed for '{episode_title}' (URL: {self.__link}). Error: {str(e_dw_ep)}"
                logger.error(err_msg)
                self.__c_track.error_message = str(e_dw_ep)
                raise TrackNotFound(message=err_msg, url=self.__link) from e_dw_ep
        
        except Exception as e:
            if isfile(self.__song_path):
                os.remove(self.__song_path)
            self.__c_track.success = False
            episode_title = self.__preferences.song_metadata.get('music', UNKNOWN_EPISODE_TITLE)
            err_msg = f"Episode download failed for '{episode_title}' (URL: {self.__link}). Error: {str(e)}"
            logger.error(err_msg)
            # Store error on track object
            self.__c_track.error_message = str(e)
            raise TrackNotFound(message=err_msg, url=self.__link) from e

    def _parse_format_string(self, format_str: str) -> tuple[str | None, str | None]:
        """Helper to parse format string like 'MP3_320K' into format and bitrate."""
        if not format_str:
            return None, None
        
        parts = format_str.upper().split('_', 1)
        format_name = parts[0]
        bitrate = parts[1] if len(parts) > 1 else None

        if format_name not in AUDIO_FORMATS:
            logger.warning(f"Unsupported format {format_name} in format string '{format_str}'. Will not convert.")
            return None, None

        if bitrate:
            # Ensure bitrate ends with 'K' for consistency if it's a number followed by K
            if bitrate[:-1].isdigit() and not bitrate.endswith('K'):
                bitrate += 'K'
            
            valid_bitrates = AUDIO_FORMATS[format_name].get("bitrates", [])
            if valid_bitrates and bitrate not in valid_bitrates:
                default_br = AUDIO_FORMATS[format_name].get("default_bitrate")
                logger.warning(f"Unsupported bitrate {bitrate} for {format_name}. Using default {default_br if default_br else 'as available'}.")
                bitrate = default_br # Fallback to default, or None if no specific default for lossless
            elif not valid_bitrates and AUDIO_FORMATS[format_name].get("default_bitrate") is None: # Lossless format
                logger.info(f"Bitrate {bitrate} specified for lossless format {format_name}. Bitrate will be ignored by converter.")
                # Keep bitrate as is, convert_audio will handle ignoring it for lossless.
        
        return format_name, bitrate

    # Removed __add_more_tags() - now handled by unified libutils/taggers.py

class DwTrack:
    def __init__(
        self,
        preferences: Preferences,
        parent: str = None
    ) -> None:

        self.__preferences = preferences
        self.__parent = parent
        self.__ids = self.__preferences.ids
        self.__quality_download = self.__preferences.quality_download

    def dw(self) -> Track:
        from deezspot.deezloader.deegw_api import API_GW
        infos_dw = API_GW.get_song_data(self.__ids)

        media = Download_JOB.check_sources(
            [infos_dw], self.__quality_download
        )

        infos_dw['media_url'] = media[0]
        track = EASY_DW(infos_dw, self.__preferences, parent=self.__parent).easy_dw()

        if not track.success and not getattr(track, 'was_skipped', False):
            error_msg = getattr(track, 'error_message', "An unknown error occurred during download.")
            raise TrackNotFound(message=error_msg, url=track.link or self.__preferences.link)

        return track

class DwAlbum:
    def _album_object_to_dict(self, album_obj: albumCbObject) -> dict:
        """Converts an AlbumObject to a dictionary for tagging and path generation."""
        # Use the unified metadata converter
        artist_separator = getattr(self.__preferences, 'artist_separator', '; ')
        return album_object_to_dict(album_obj, source_type='deezer', artist_separator=artist_separator)

    def _track_object_to_dict(self, track_obj: any, album_obj: albumCbObject) -> dict:
        """Converts a track object to a dictionary with album context."""
        # Check if track_obj is a TrackAlbumObject which doesn't have its own album attribute
        if hasattr(track_obj, 'type') and track_obj.type == 'trackAlbum':
            # Create a TrackObject with album reference from the provided album_obj
            from deezspot.models.callback.track import TrackObject
            full_track = TrackObject(
                title=track_obj.title,
                disc_number=track_obj.disc_number,
                track_number=track_obj.track_number,
                duration_ms=track_obj.duration_ms,
                explicit=track_obj.explicit,
                ids=track_obj.ids,
                artists=track_obj.artists,
                album=album_obj,  # Use the parent album
                genres=getattr(track_obj, 'genres', [])
            )
            # Use the unified metadata converter
            artist_separator = getattr(self.__preferences, 'artist_separator', '; ')
            return track_object_to_dict(full_track, source_type='deezer', artist_separator=artist_separator)
        else:
            # Use the unified metadata converter
            artist_separator = getattr(self.__preferences, 'artist_separator', '; ')
            return track_object_to_dict(track_obj, source_type='deezer', artist_separator=artist_separator)

    def _resolve_album_dict(self, album_obj: albumCbObject, image_bytes):
        artist_separator = getattr(self.__preferences, 'artist_separator', '; ')
        if self.__use_spotify and self.__spotify_album_obj:
            album_dict = album_object_to_dict(
                self.__spotify_album_obj,
                source_type='spotify',
                artist_separator=artist_separator,
            )
        else:
            album_dict = self._album_object_to_dict(album_obj)
        if self.__use_spotify and self.__spotify_album_obj and getattr(self.__spotify_album_obj, 'images', None):
            from deezspot.libutils.metadata_converter import _get_best_image_url
            spotify_image_url = _get_best_image_url(self.__spotify_album_obj.images, 'spotify')
            album_dict['image'] = spotify_image_url if spotify_image_url else image_bytes
        else:
            album_dict['image'] = image_bytes
        return album_dict

    @staticmethod
    def _resolve_album_image(album_dict: dict, md5_image: str):
        if isinstance(album_dict['image'], bytes):
            return album_dict['image']
        try:
            from deezspot.libutils.taggers import fetch_and_process_image
            return fetch_and_process_image(album_dict['image']) or API.choose_img(md5_image, size="1400x1400")
        except Exception:
            return API.choose_img(md5_image, size="1400x1400")

    def _spotify_track_maps(self):
        by_isrc = {}
        ordered = []
        if not (self.__use_spotify and self.__spotify_album_obj and getattr(self.__spotify_album_obj, 'tracks', None)):
            return by_isrc, ordered
        for spotify_track in self.__spotify_album_obj.tracks:
            ordered.append(spotify_track)
            spotify_ids = getattr(spotify_track, 'ids', None)
            isrc_val = getattr(spotify_ids, 'isrc', None) if spotify_ids else None
            if isrc_val:
                by_isrc[isrc_val.upper()] = spotify_track
        return by_isrc, ordered

    @staticmethod
    def _apply_album_track_position(track_obj, info_item: dict, index: int) -> None:
        if 'track_position' in info_item:
            track_obj.track_number = info_item['track_position']
        if 'disk_number' in info_item:
            track_obj.disc_number = info_item['disk_number']
        if track_obj.track_number is None:
            track_obj.track_number = index + 1
        if track_obj.disc_number is None:
            track_obj.disc_number = 1

    @staticmethod
    def _full_track_with_album(album_track_obj, album_obj):
        from deezspot.models.callback.track import TrackObject
        return TrackObject(
            title=album_track_obj.title,
            disc_number=album_track_obj.disc_number,
            track_number=album_track_obj.track_number,
            duration_ms=album_track_obj.duration_ms,
            explicit=album_track_obj.explicit,
            ids=album_track_obj.ids,
            artists=album_track_obj.artists,
            album=album_obj,
            genres=getattr(album_track_obj, 'genres', []),
        )

    def _assign_spotify_track_preference(self, c_preferences, info_item: dict, track_index: int, spotify_tracks_by_isrc: dict, spotify_tracks_in_order: list) -> None:
        if not (self.__use_spotify and spotify_tracks_in_order):
            return
        spotify_track = None
        deezer_isrc = info_item.get('ISRC') or info_item.get('isrc')
        deezer_isrc = deezer_isrc.upper() if isinstance(deezer_isrc, str) else None
        if deezer_isrc and deezer_isrc in spotify_tracks_by_isrc:
            spotify_track = spotify_tracks_by_isrc[deezer_isrc]
        elif track_index < len(spotify_tracks_in_order):
            spotify_track = spotify_tracks_in_order[track_index]
        if spotify_track:
            c_preferences.spotify_metadata = True
            c_preferences.spotify_track_obj = spotify_track

    def _download_album_track(
        self,
        info_item: dict,
        album_track_obj,
        album_obj: albumCbObject,
        track_index: int,
        total_tracks: int,
        spotify_tracks_by_isrc: dict,
        spotify_tracks_in_order: list,
    ):
        c_preferences = deepcopy(self.__preferences)
        full_track_obj = self._full_track_with_album(album_track_obj, album_obj)
        c_preferences.song_metadata = full_track_obj
        c_preferences.ids = full_track_obj.ids.deezer
        c_preferences.track_number = track_index + 1
        c_preferences.total_tracks = total_tracks
        c_preferences.link = f"https://deezer.com/track/{c_preferences.ids}"
        c_preferences.pad_number_width = getattr(self.__preferences, 'pad_number_width', 'auto')
        self._assign_spotify_track_preference(
            c_preferences,
            info_item,
            track_index,
            spotify_tracks_by_isrc,
            spotify_tracks_in_order,
        )
        try:
            return EASY_DW(info_item, c_preferences, parent='album').easy_dw()
        except Exception as e:
            logger.error(f"Track '{album_track_obj.title}' in album '{album_obj.title}' failed: {e}")
            track_metadata = self._track_object_to_dict(album_track_obj, album_obj)
            failed_track = Track(track_metadata, None, None, None, c_preferences.link, c_preferences.ids)
            failed_track.success = False
            failed_track.error_message = str(e)
            return failed_track

    @staticmethod
    def _collect_album_track_summary(tracks: list, album_tracks: list):
        successful_tracks_cb = []
        failed_tracks_cb = []
        skipped_tracks_cb = []
        for track, track_obj in zip(tracks, album_tracks):
            if getattr(track, 'was_skipped', False):
                skipped_tracks_cb.append(track_obj)
            elif track.success:
                successful_tracks_cb.append(track_obj)
            else:
                failed_tracks_cb.append(
                    FailedTrackObject(
                        track=track_obj,
                        reason=getattr(track, 'error_message', 'Unknown reason'),
                    )
                )
        return successful_tracks_cb, skipped_tracks_cb, failed_tracks_cb

    def __init__(
        self,
        preferences: Preferences
    ) -> None:

        self.__preferences = preferences
        self.__ids = self.__preferences.ids
        self.__make_zip = self.__preferences.make_zip
        self.__output_dir = self.__preferences.output_dir
        self.__quality_download = self.__preferences.quality_download
        album_obj: albumCbObject = self.__preferences.song_metadata
        self.__song_metadata = self._album_object_to_dict(album_obj)
        self.__song_metadata['artist_separator'] = getattr(self.__preferences, 'artist_separator', '; ')
        # New: Spotify metadata context for album-level tagging
        self.__use_spotify = getattr(self.__preferences, 'spotify_metadata', False)
        self.__spotify_album_obj = getattr(self.__preferences, 'spotify_album_obj', None)

    def dw(self) -> Album:
        from deezspot.deezloader.deegw_api import API_GW
        album_obj = self.__preferences.json_data
        report_album_initializing(album_obj)
        
        infos_dw = API_GW.get_album_data(self.__ids)['data']
        md5_image = infos_dw[0]['ALB_PICTURE']
        image_bytes = API.choose_img(md5_image, size="1400x1400")
        album_dict = self._resolve_album_dict(album_obj, image_bytes)
        
        album = Album(self.__ids)
        album.image = self._resolve_album_image(album_dict, md5_image)
        album.md5_image = md5_image
        album.nb_tracks = album_obj.total_tracks
        album.album_name = album_obj.title
        album.upc = album_obj.ids.upc
        album.tags = album_dict
        tracks = album.tracks
        
        medias = Download_JOB.check_sources(infos_dw, self.__quality_download)
        
        album_base_directory = get_album_directory(
            album.tags,
            self.__output_dir,
            custom_dir_format=self.__preferences.custom_dir_format,
            pad_tracks=self.__preferences.pad_tracks
        )
        
        # Save cover to album directory
        if self.__preferences.save_cover and album.image and album_base_directory:
            save_cover_image(album.image, album_base_directory, "cover.jpg")
            
        total_tracks = len(infos_dw)
        spotify_tracks_by_isrc, spotify_tracks_in_order = self._spotify_track_maps()
        
        for a, album_track_obj in enumerate(album_obj.tracks):
            c_infos_dw_item = infos_dw[a] 
            self._apply_album_track_position(album_track_obj, c_infos_dw_item, a)
            c_infos_dw_item['media_url'] = medias[a]
            current_track_object = self._download_album_track(
                c_infos_dw_item,
                album_track_obj,
                album_obj,
                a,
                total_tracks,
                spotify_tracks_by_isrc,
                spotify_tracks_in_order,
            )
            
            if current_track_object:
                tracks.append(current_track_object)

        if self.__make_zip:
            song_quality = tracks[0].quality if tracks else 'Unknown'
            custom_dir_format = getattr(self.__preferences, 'custom_dir_format', None)
            zip_name = create_zip(
                tracks,
                output_dir=self.__output_dir,
                song_metadata=album_dict,
                song_quality=song_quality,
                custom_dir_format=custom_dir_format
            )
            album.zip_path = zip_name

        successful_tracks_cb, skipped_tracks_cb, failed_tracks_cb = self._collect_album_track_summary(
            tracks,
            album_obj.tracks,
        )

        summary_obj = SummaryObject(
            successful_tracks=successful_tracks_cb,
            skipped_tracks=skipped_tracks_cb,
            failed_tracks=failed_tracks_cb,
            total_successful=len(successful_tracks_cb),
            total_skipped=len(skipped_tracks_cb),
            total_failed=len(failed_tracks_cb),
            service=Service.DEEZER
        )
        # Compute and attach final media characteristics
        quality_val, bitrate_val = resolve_deezer_summary_media(
            quality_download=self.__quality_download,
            convert_to=getattr(self.__preferences, 'convert_to', None),
            bitrate=getattr(self.__preferences, 'bitrate', None),
        )
        summary_obj.quality = quality_val
        summary_obj.bitrate = bitrate_val
        
        # Report album completion status
        report_album_done(album_obj, summary_obj)
        
        return album

class DwPlaylist:
    def __init__(
        self,
        preferences: Preferences
    ) -> None:

        self.__preferences = preferences
        self.__ids = self.__preferences.ids
        self.__make_zip = self.__preferences.make_zip
        self.__output_dir = self.__preferences.output_dir
        self.__quality_download = self.__preferences.quality_download

    def _track_object_to_dict(self, track_obj: any) -> dict:
        # Use the unified metadata converter
        artist_separator = getattr(self.__preferences, 'artist_separator', '; ')
        return track_object_to_dict(track_obj, source_type='deezer', artist_separator=artist_separator)

    @staticmethod
    def _is_valid_playlist_track(track_obj) -> bool:
        return bool(track_obj and track_obj.ids and track_obj.ids.deezer)

    def _build_invalid_playlist_track(self, reason: str) -> Track:
        failed_track_model = Track(
            tags={'music': 'Unknown Skipped Item', 'artist': 'Unknown'},
            song_path=None,
            file_format=None,
            quality=None,
            link=None,
            ids=None,
        )
        failed_track_model.success = False
        failed_track_model.error_message = reason
        return failed_track_model

    def _build_playlist_track_preferences(self, track_obj, idx: int, total_tracks: int):
        c_preferences = deepcopy(self.__preferences)
        c_preferences.ids = track_obj.ids.deezer
        c_preferences.song_metadata = track_obj
        c_preferences.track_number = idx + 1
        c_preferences.total_tracks = total_tracks
        c_preferences.json_data = self.__preferences.json_data
        c_preferences.link = f"https://deezer.com/track/{c_preferences.ids}"
        c_preferences.pad_number_width = getattr(self.__preferences, 'pad_number_width', 'auto')
        return c_preferences

    def _download_playlist_track(self, info_item: dict, track_obj, idx: int, total_tracks: int, playlist_title: str):
        preferences = self._build_playlist_track_preferences(track_obj, idx, total_tracks)
        try:
            track = EASY_DW(info_item, preferences, parent='playlist').easy_dw()
            return track, None
        except Exception as e:
            logger.error(f"Track '{track_obj.title}' in playlist '{playlist_title}' failed: {e}")
            failed_track = Track(self._track_object_to_dict(track_obj), None, None, None, preferences.link, preferences.ids)
            failed_track.success = False
            failed_track.error_message = str(e)
            return failed_track, str(e)

    @staticmethod
    def _collect_playlist_track_result(current_track_object, track_obj, successful_tracks_cb, skipped_tracks_cb, failed_tracks_cb):
        if getattr(current_track_object, 'was_skipped', False):
            skipped_tracks_cb.append(track_obj)
            return
        if current_track_object.success:
            successful_tracks_cb.append(track_obj)
            return
        failed_tracks_cb.append(
            FailedTrackObject(
                track=track_obj,
                reason=getattr(current_track_object, 'error_message', 'Unknown reason'),
            )
        )

    def dw(self) -> Playlist:
        playlist_obj: playlistCbObject = self.__preferences.json_data
        
        status_obj_init = InitializingObject(ids=playlist_obj.ids)
        callback_obj_init = PlaylistCallbackObject(playlist=playlist_obj, status_info=status_obj_init)
        report_progress(
            reporter=Download_JOB.progress_reporter,
            callback_obj=callback_obj_init
        )
        
        from deezspot.deezloader.deegw_api import API_GW
        infos_dw = API_GW.get_playlist_data(self.__ids)['data']
        
        playlist = Playlist()
        tracks = playlist.tracks

        m3u_path = create_m3u_file(self.__output_dir, playlist_obj.title)

        medias = Download_JOB.check_sources(infos_dw, self.__quality_download)

        successful_tracks_cb = []
        failed_tracks_cb = []
        skipped_tracks_cb = []
        
        total_tracks = len(infos_dw)

        for idx in range(total_tracks):
            c_infos_dw_item = infos_dw[idx]
            c_media = medias[idx]
            c_track_obj = playlist_obj.tracks[idx] if idx < len(playlist_obj.tracks) else None

            if not self._is_valid_playlist_track(c_track_obj):
                logger.warning(f"Skipping item {idx + 1} in playlist '{playlist_obj.title}' as it's not a valid track object.")
                from deezspot.models.callback.track import TrackObject as trackCbObject
                unknown_track = trackCbObject(title="Unknown Skipped Item")
                reason = "Playlist item was not a valid track object."
                
                failed_tracks_cb.append(FailedTrackObject(track=unknown_track, reason=reason))
                failed_track_model = self._build_invalid_playlist_track(reason)
                tracks.append(failed_track_model)
                continue

            c_infos_dw_item['media_url'] = c_media
            current_track_object, download_error = self._download_playlist_track(
                c_infos_dw_item,
                c_track_obj,
                idx,
                total_tracks,
                playlist_obj.title,
            )
            if download_error is not None:
                failed_tracks_cb.append(FailedTrackObject(track=c_track_obj, reason=download_error))
            else:
                self._collect_playlist_track_result(
                    current_track_object,
                    c_track_obj,
                    successful_tracks_cb,
                    skipped_tracks_cb,
                    failed_tracks_cb,
                )

            if current_track_object:
                tracks.append(current_track_object)
                if current_track_object.success and hasattr(current_track_object, 'song_path') and current_track_object.song_path:
                    append_track_to_m3u(m3u_path, current_track_object)

        if self.__make_zip:
            zip_name = f"{self.__output_dir}/{playlist_obj.title} [playlist {self.__ids}]"
            create_zip(tracks, zip_name=zip_name)
            playlist.zip_path = zip_name
 
        summary_obj = SummaryObject(
            successful_tracks=successful_tracks_cb,
            skipped_tracks=skipped_tracks_cb,
            failed_tracks=failed_tracks_cb,
            total_successful=len(successful_tracks_cb),
            total_skipped=len(skipped_tracks_cb),
            total_failed=len(failed_tracks_cb),
            service=Service.DEEZER
        )
        # Compute and attach final media characteristics
        quality_val, bitrate_val = resolve_deezer_summary_media(
            quality_download=self.__quality_download,
            convert_to=getattr(self.__preferences, 'convert_to', None),
            bitrate=getattr(self.__preferences, 'bitrate', None),
        )
        summary_obj.quality = quality_val
        summary_obj.bitrate = bitrate_val
        
        # Attach m3u path to summary
        summary_obj.m3u_path = m3u_path
        
        status_obj_done = DoneObject(ids=playlist_obj.ids, summary=summary_obj)
        callback_obj_done = PlaylistCallbackObject(playlist=playlist_obj, status_info=status_obj_done)
        report_progress(
            reporter=Download_JOB.progress_reporter,
            callback_obj=callback_obj_done
        )
        
        return playlist

class DwEpisode:
    def __init__(
        self,
        preferences: Preferences
    ) -> None:
        self.__preferences = preferences
        self.__ids = preferences.ids
        self.__output_dir = preferences.output_dir
        self.__quality_download = preferences.quality_download
        
    def dw(self) -> Track:
        from deezspot.deezloader.deegw_api import API_GW
        infos_dw = API_GW.get_episode_data(self.__ids)
        infos_dw['__TYPE__'] = 'episode'
        
        self.__preferences.song_metadata = {
            'music': infos_dw.get('EPISODE_TITLE', ''),
            'artist': infos_dw.get('SHOW_NAME', ''),
            'album': infos_dw.get('SHOW_NAME', ''),
            'date': infos_dw.get('EPISODE_PUBLISHED_TIMESTAMP', '').split()[0],
            'genre': 'Podcast',
            'explicit': infos_dw.get('SHOW_IS_EXPLICIT', '2'),
            'duration': int(infos_dw.get('DURATION', 0)),
        }
        
        try:
            direct_url = infos_dw.get('EPISODE_DIRECT_STREAM_URL')
            if not direct_url:
                raise TrackNotFound("No direct URL found")
            
            from pathlib import Path
            safe_filename = sanitize_name(self.__preferences.song_metadata['music'])
            Path(self.__output_dir).mkdir(parents=True, exist_ok=True)
            output_path = os.path.join(self.__output_dir, f"{safe_filename}.mp3")
            
            response = requests.get(
                direct_url,
                stream=True,
                timeout=REQUEST_TIMEOUT_SECONDS,
            )
            response.raise_for_status()

            # Send initial progress status
            callback_track = trackCbObject(
                title=self.__preferences.song_metadata.get('music', UNKNOWN_EPISODE_TITLE),
                artists=[ArtistTrackObject(name=self.__preferences.song_metadata.get('artist', 'Unknown Show'))],
                ids=IDs(deezer=self.__ids),
            )
            report_progress(
                reporter=Download_JOB.progress_reporter,
                callback_obj=TrackCallbackObject(
                    track=callback_track,
                    status_info=InitializingObject(ids=callback_track.ids),
                ),
            )
            
            with open(output_path, 'wb') as f:
                for chunk in response.iter_content(chunk_size=8192):
                    if chunk:
                        f.write(chunk)
            
            episode = Track(
                self.__preferences.song_metadata,
                output_path,
                '.mp3',
                self.__quality_download, 
                f"https://www.deezer.com/episode/{self.__ids}",
                self.__ids
            )
            episode.success = True
            
            # Send completion status
            report_progress(
                reporter=Download_JOB.progress_reporter,
                callback_obj=TrackCallbackObject(
                    track=callback_track,
                    status_info=DoneObject(ids=callback_track.ids),
                ),
            )
            
            # Save cover image for the episode
            if self.__preferences.save_cover:
                episode_image_md5 = infos_dw.get('EPISODE_IMAGE_MD5', '')
                episode_image_data = None
                if episode_image_md5:
                    episode_image_data = API.choose_img(episode_image_md5, size="1200x1200")
                
                if episode_image_data:
                    episode_directory = os.path.dirname(output_path)
                    save_cover_image(episode_image_data, episode_directory, "cover.jpg")

            return episode
            
        except Exception as e:
            if 'output_path' in locals() and os.path.exists(output_path):
                os.remove(output_path)
            episode_title = self.__preferences.song_metadata.get('music', UNKNOWN_EPISODE_TITLE)
            err_msg = f"Episode download failed for '{episode_title}' (URL: {self.__preferences.link}). Error: {str(e)}"
            logger.error(err_msg)
            # Add original error to exception
            raise TrackNotFound(message=err_msg, url=self.__preferences.link) from e


# Backward-compatible aliases for existing imports/usages.
Download_JOB = DownloadJob
EASY_DW = EasyDw
DW_TRACK = DwTrack
DW_ALBUM = DwAlbum
DW_PLAYLIST = DwPlaylist
DW_EPISODE = DwEpisode
