import traceback
import json
import os
import time
from copy import deepcopy
from os.path import isfile, dirname
from librespot.core import Session
from deezspot.exceptions import TrackNotFound
from librespot.metadata import TrackId, EpisodeId
from deezspot.spotloader.spotify_settings import qualities
from deezspot.libutils.others_settings import answers
from deezspot.libutils.write_tags import check_track
from librespot.audio.decoders import AudioQuality, VorbisOnlyAudioQuality
from deezspot.libutils.audio_converter import convert_audio, AUDIO_FORMATS, get_output_path
from os import (
    remove,
    system,
    replace as os_replace,
)
import subprocess
import shutil
from deezspot.models.download import (
    Track,
    Album,
    Playlist,
    Preferences,
    Episode,
)
from deezspot.libutils.utils import (
    set_path,
    create_zip,
    request,
    sanitize_name,
    save_cover_image,
    __get_dir as get_album_directory,
)
from deezspot.libutils.write_m3u import create_m3u_file, append_track_to_m3u
from deezspot.libutils.metadata_converter import track_object_to_dict, album_object_to_dict
from deezspot.libutils.progress_reporter import (
    report_track_initializing, report_track_skipped, report_track_retrying,
    report_track_realtime_progress, report_track_error, report_track_done,
    report_album_initializing, report_album_done, report_playlist_initializing, report_playlist_done
)
from deezspot.libutils.taggers import (
    enhance_metadata_with_image, process_and_tag_track, process_and_tag_episode,
    save_cover_image_for_track
)
from deezspot.libutils.logging_utils import logger, report_progress
from deezspot.libutils.cleanup_utils import (
    register_active_download,
    unregister_active_download,
)
from deezspot.libutils.skip_detection import check_track_exists
from deezspot.models.callback import (
    trackObject, albumTrackObject, playlistTrackObject, artistTrackObject,
    trackCallbackObject, albumCallbackObject, playlistCallbackObject,
    initializingObject, skippedObject, retryingObject, realTimeObject, errorObject, doneObject,
    failedTrackObject, summaryObject,
    albumObject, artistAlbumObject,
    playlistObject,
    userObject,
    IDs
)
from deezspot.spotloader.__spo_api__ import tracking, json_to_track_playlist_object
from deezspot.models.callback.common import Service

UNKNOWN_TRACK_TITLE = "Unknown Track"

# --- Global retry counter variables ---
GLOBAL_RETRY_COUNT = 0
GLOBAL_MAX_RETRIES = 100  # Adjust this value as needed

# --- Global tracking of active downloads ---
# Moved to deezspot.libutils.cleanup_utils

# Use unified metadata converter
def _track_object_to_dict(track_obj: trackObject) -> dict:
    """Converts a trackObject into a dictionary for legacy functions like taggers."""
    return track_object_to_dict(track_obj, source_type='spotify')

# Use unified metadata converter
def _album_object_to_dict(album_obj: albumObject) -> dict:
    """Converts an albumObject into a dictionary for legacy functions."""
    return album_object_to_dict(album_obj, source_type='spotify')

class DownloadJob:
    session = None
    progress_reporter = None

    @classmethod
    def __init__(cls, session: Session) -> None:
        cls.session = session

    @classmethod
    def set_progress_reporter(cls, reporter):
        cls.progress_reporter = reporter

class EasyDw:
    def __init__(
        self,
        preferences: Preferences,
        parent: str = None  # Can be 'album', 'playlist', or None for individual track
    ) -> None:
        
        self.__preferences = preferences
        self.__parent = parent  # Store the parent type

        self.__ids = preferences.ids
        self.__link = preferences.link
        self.__output_dir = preferences.output_dir
        self.__song_metadata = preferences.song_metadata
        # Convert song metadata to dict with configured artist separator
        artist_separator = getattr(preferences, 'artist_separator', '; ')
        self.__song_metadata_dict = track_object_to_dict(
            self.__song_metadata,
            source_type='spotify',
            artist_separator=artist_separator,
        )
        self.__quality_download = preferences.quality_download or "NORMAL"
        self.__type = "episode" if preferences.is_episode else "track"  # New type parameter
        self.__real_time_dl = preferences.real_time_dl
        self.__convert_to = getattr(preferences, 'convert_to', None)
        self.__bitrate = getattr(preferences, 'bitrate', None) # New bitrate attribute

        # Ensure if convert_to is None, bitrate is also None
        if self.__convert_to is None:
            self.__bitrate = None

        self.__c_quality = qualities[self.__quality_download]
        self.__fallback_ids = self.__ids

        self.__set_quality()
        if preferences.is_episode:
            self.__write_episode()
        else:
            self.__write_track()

    def __set_quality(self) -> None:
        self.__dw_quality = self.__c_quality['n_quality']
        self.__file_format = self.__c_quality['f_format']
        self.__song_quality = self.__c_quality['s_quality']

    def __set_song_path(self) -> None:
        # Retrieve custom formatting strings from preferences, if any.
        custom_dir_format = getattr(self.__preferences, 'custom_dir_format', None)
        custom_track_format = getattr(self.__preferences, 'custom_track_format', None)
        pad_tracks = getattr(self.__preferences, 'pad_tracks', True)
        # Ensure the separator is available to formatting utils for indexed placeholders
        self.__song_metadata_dict['artist_separator'] = getattr(self.__preferences, 'artist_separator', '; ')
        # Determine pad width (supports 'auto' mode)
        pad_number_width = None
        try:
            pnw = getattr(self.__preferences, 'pad_number_width', None)
            if isinstance(pnw, str) and pnw.lower() == 'auto':
                total = None
                if self.__parent == 'album' and hasattr(self.__song_metadata, 'album') and getattr(self.__song_metadata.album, 'total_tracks', None):
                    total = self.__song_metadata.album.total_tracks
                elif self.__parent == 'playlist' and hasattr(self.__preferences, 'json_data') and self.__preferences.json_data:
                    try:
                        total = self.__preferences.json_data.get('tracks', {}).get('total')
                    except Exception:
                        total = None
                if isinstance(total, int) and total and total > 0:
                    pad_number_width = max(2, len(str(total)))
            elif isinstance(pnw, int) and pnw >= 1:
                pad_number_width = pnw
        except Exception:
            pad_number_width = None
        # Inject playlist placeholders if in playlist context
        try:
            if self.__parent == 'playlist' and hasattr(self.__preferences, 'json_data') and self.__preferences.json_data:
                playlist_data = self.__preferences.json_data
                playlist_name = None
                if isinstance(playlist_data, dict):
                    playlist_name = playlist_data.get('name') or playlist_data.get('title')
                if not playlist_name and hasattr(playlist_data, 'title'):
                    playlist_name = getattr(playlist_data, 'title')
                self.__song_metadata_dict['playlist'] = playlist_name or 'unknown'
                self.__song_metadata_dict['playlistnum'] = getattr(self.__preferences, 'track_number', None) or 0
        except Exception:
            # If playlist info missing, skip silently
            pass
        self.__song_path = set_path(
            self.__song_metadata_dict,
            self.__output_dir,
            self.__song_quality,
            self.__file_format,
            custom_dir_format=custom_dir_format,
            custom_track_format=custom_track_format,
            pad_tracks=pad_tracks,
            pad_number_width=pad_number_width
        )

    def __set_episode_path(self) -> None:
        custom_dir_format = getattr(self.__preferences, 'custom_dir_format', None)
        custom_track_format = getattr(self.__preferences, 'custom_track_format', None)
        pad_tracks = getattr(self.__preferences, 'pad_tracks', True)
        self.__song_metadata_dict['artist_separator'] = getattr(self.__preferences, 'artist_separator', '; ')
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
            self.__song_metadata_dict, self.__song_path,
            self.__file_format, self.__song_quality,
            self.__link, self.__ids
        )
        self.__c_track.md5_image = self.__ids
        self.__c_track.set_fallback_ids(self.__fallback_ids)

    def __write_episode(self) -> None:
        self.__set_episode_path()
        self.__c_episode = Episode(
            self.__song_metadata_dict, self.__song_path,
            self.__file_format, self.__song_quality,
            self.__link, self.__ids
        )
        self.__c_episode.md5_image = self.__ids
        self.__c_episode.set_fallback_ids(self.__fallback_ids)

    def _get_parent_info(self):
        parent_info = None
        total_tracks_val = None
        if self.__parent == "playlist" and hasattr(self.__preferences, "json_data"):
            playlist_data = self.__preferences.json_data
            total_tracks_val = playlist_data.get('tracks', {}).get('total', 'unknown')
            parent_info = {
                "type": "playlist",
                "name": playlist_data.get('name', 'unknown'),
                "owner": playlist_data.get('owner', {}).get('display_name', 'unknown'),
                "total_tracks": total_tracks_val,
                "url": f"https://open.spotify.com/playlist/{playlist_data.get('id', '')}"
            }
        elif self.__parent == "album":
            album_meta = self.__song_metadata.album
            total_tracks_val = album_meta.total_tracks
            parent_info = {
                "type": "album",
                "title": album_meta.title,
                "artist": getattr(self.__preferences, 'artist_separator', '; ').join([a.name for a in album_meta.artists]),
                "total_tracks": total_tracks_val,
                "url": f"https://open.spotify.com/album/{album_meta.ids.spotify if album_meta.ids else ''}"
            }
        return parent_info, total_tracks_val

    def __convert_audio(self) -> None:
        # First, handle Spotify's OGG to standard format conversion (always needed)
        # self.__song_path is initially the path for the .ogg file (e.g., song.ogg)
        og_song_path_for_ogg_output = self.__song_path
        temp_filename = og_song_path_for_ogg_output.replace(".ogg", ".tmp")

        # Move original .ogg to .tmp
        os_replace(og_song_path_for_ogg_output, temp_filename)
        register_active_download(temp_filename)
        
        try:
            # Step 1: First convert the OGG file to standard format (copy operation)
            # Output is og_song_path_for_ogg_output
            # Resolve ffmpeg path explicitly to avoid PATH issues in distroless
            ffmpeg_path = shutil.which("ffmpeg") or "/usr/local/bin/ffmpeg"
            try:
                result = subprocess.run(
                    [
                        ffmpeg_path, "-y", "-hide_banner", "-loglevel", "error",
                        "-i", temp_filename, "-c:a", "copy", og_song_path_for_ogg_output
                    ],
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    text=True,
                    check=False
                )
                if result.returncode != 0 or not os.path.exists(og_song_path_for_ogg_output):
                    raise RuntimeError(f"ffmpeg remux failed (rc={result.returncode}). stderr: {result.stderr.strip()}")
            except FileNotFoundError as fnf:
                raise RuntimeError(f"ffmpeg not found: attempted '{ffmpeg_path}'. Ensure it is present in PATH.") from fnf
            
            # temp_filename has been processed. Unregister and remove it.
            # CURRENT_DOWNLOAD was temp_filename.
            unregister_active_download(temp_filename) # CURRENT_DOWNLOAD should become None.
            if os.path.exists(temp_filename):
                remove(temp_filename)
            
            # The primary file is now og_song_path_for_ogg_output. Register it.
            # Ensure self.__song_path reflects this, as it might be used by other parts of the class or returned.
            self.__song_path = og_song_path_for_ogg_output
            register_active_download(self.__song_path)
            
            # Step 2: Convert to requested format if specified (e.g., MP3, FLAC)
            conversion_to_another_format_occurred_and_cleared_state = False
            if self.__convert_to:
                format_name = self.__convert_to
                bitrate = self.__bitrate
                if format_name:
                    try:
                        path_before_final_conversion = self.__song_path # Current path, e.g., .ogg
                        converted_path = convert_audio(
                            path_before_final_conversion, 
                            format_name,
                            bitrate,
                            register_active_download,
                            unregister_active_download
                        )
                        if converted_path != path_before_final_conversion:
                            # Conversion to a new format happened and path changed
                            self.__song_path = converted_path # Update EASY_DW's current song path

                            current_object_path_attr_name = 'song_path' if self.__type == "track" else 'episode_path'
                            current_media_object = self.__c_track if self.__type == "track" else self.__c_episode
                            
                            if current_media_object:
                                setattr(current_media_object, current_object_path_attr_name, converted_path)
                                _, new_ext = os.path.splitext(converted_path)
                                if new_ext:
                                    current_media_object.file_format = new_ext.lower()
                                    # Also update EASY_DW's internal __file_format
                                    self.__file_format = new_ext.lower()
                        
                        conversion_to_another_format_occurred_and_cleared_state = True
                    except Exception as conv_error:
                        # Conversion to a different format failed.
                        # self.__song_path (the .ogg) is still the latest valid file and is registered.
                        # We want to keep it, so CURRENT_DOWNLOAD should remain set to this .ogg path.
                        logger.error(f"Audio conversion to {format_name} error: {str(conv_error)}")
                        # conversion_to_another_format_occurred_and_cleared_state remains False.
            
            # If no conversion to another format was requested, or if it was requested but didn't effectively run
            # (e.g. format_name was None), or if convert_audio failed to clear state (which would be its bug),
            # then self.__song_path (the .ogg from Step 1) is the final successfully processed file for this method's scope.
            # It is currently registered. Unregister it as its processing is complete.
            if not conversion_to_another_format_occurred_and_cleared_state:
                unregister_active_download(self.__song_path) # Clears CURRENT_DOWNLOAD if it was self.__song_path
                
        except Exception as e:
            # This outer try/except handles errors primarily from Step 1 (OGG copy)
            # or issues during the setup for Step 2 before convert_audio is deeply involved.
            # In case of failure, try to restore the original file from temp if Step 1 didn't complete.
            if os.path.exists(temp_filename) and not os.path.exists(og_song_path_for_ogg_output):
                os_replace(temp_filename, og_song_path_for_ogg_output)
            
            # Clean up temp_filename. unregister_active_download is safe:
            # it only clears CURRENT_DOWNLOAD if CURRENT_DOWNLOAD == temp_filename.
            if os.path.exists(temp_filename):
                unregister_active_download(temp_filename)
                remove(temp_filename)
                
            # Re-throw the exception. If a file (like og_song_path_for_ogg_output) was registered
            # and an error occurred, it remains registered for atexit cleanup, which is intended.
            raise e

    def get_no_dw_track(self) -> Track:
        return self.__c_track

    def easy_dw(self) -> Track:
        # Process image data using unified utility
        self.__song_metadata_dict = enhance_metadata_with_image(self.__song_metadata_dict)

        try:
            # Initialize success to False, it will be set to True if download_try is successful
            if hasattr(self, '_EasyDw__c_track') and self.__c_track:
                self.__c_track.success = False
            elif hasattr(self, '_EasyDw__c_episode') and self.__c_episode: # For episodes
                self.__c_episode.success = False
            
            self.download_try() # This should set self.__c_track.success = True if successful

        except Exception as e:
            song_title = self.__song_metadata.title
            artist_name = getattr(self.__preferences, 'artist_separator', '; ').join([a.name for a in self.__song_metadata.artists])
            error_message = f"Download failed for '{song_title}' by '{artist_name}' (URL: {self.__link}). Original error: {str(e)}"
            logger.error(error_message)
            traceback.print_exc()
            # Store the error message on the track object if it exists
            if hasattr(self, '_EasyDw__c_track') and self.__c_track:
                self.__c_track.success = False
                self.__c_track.error_message = error_message # Store the more detailed error message
            # Removed problematic elif for __c_episode here as easy_dw in spotloader is focused on tracks.
            # Episode-specific error handling should be within download_eps or its callers.
            raise TrackNotFound(message=error_message, url=self.__link) from e
        
        # If the track was skipped (e.g. file already exists), return it immediately.
        # download_try sets success=False and was_skipped=True in this case.
        if hasattr(self, '_EasyDw__c_track') and self.__c_track and getattr(self.__c_track, 'was_skipped', False):
            return self.__c_track

        # Final check for non-skipped tracks that might have failed after download_try returned.
        # This handles cases where download_try didn't raise an exception but self.__c_track.success is still False.
        if hasattr(self, '_EasyDw__c_track') and self.__c_track and not self.__c_track.success:
            song_title = self.__song_metadata.title
            artist_name = getattr(self.__preferences, 'artist_separator', '; ').join([a.name for a in self.__song_metadata.artists])
            original_error_msg = getattr(self.__c_track, 'error_message', "Download failed for an unspecified reason after attempt.")
            error_msg_template = "Cannot download '{title}' by '{artist}'. Reason: {reason}"
            final_error_msg = error_msg_template.format(title=song_title, artist=artist_name, reason=original_error_msg)
            current_link = self.__c_track.link if hasattr(self.__c_track, 'link') and self.__c_track.link else self.__link
            logger.error(f"{final_error_msg} (URL: {current_link})")
            self.__c_track.error_message = final_error_msg # Ensure the most specific error is on the object
            raise TrackNotFound(message=final_error_msg, url=current_link)
            
        # If we reach here, the track should be successful and not skipped.
        if hasattr(self, '_EasyDw__c_track') and self.__c_track and self.__c_track.success:
            # Apply tags using unified utility
            process_and_tag_track(
                track=self.__c_track,
                metadata_dict=self.__song_metadata_dict,
                source_type='spotify',
                save_cover=getattr(self.__preferences, 'save_cover', False)
            )
        
        # Unregister the final successful file path after all operations are done.
        # self.__c_track.song_path would have been updated by __convert_audio__ if conversion occurred.
        unregister_active_download(self.__c_track.song_path)
        
        return self.__c_track

    def download_try(self) -> Track:
        current_title = self.__song_metadata.title
        current_album = self.__song_metadata.album.title if self.__song_metadata.album else ''
        current_artist = getattr(self.__preferences, 'artist_separator', '; ').join([a.name for a in self.__song_metadata.artists])

        # Call the new check_track_exists function from skip_detection.py
        # It needs: original_song_path, title, album, convert_to, logger
        # self.__song_path is the original_song_path before any conversion attempts by this specific download operation.
        # self.__preferences.convert_to is the convert_to parameter.
        # logger is available as a global import in this module.
        exists, existing_file_path = check_track_exists(
            original_song_path=self.__song_path, 
            title=current_title, 
            album=current_album, 
            convert_to=self.__preferences.convert_to, 
            logger=logger # Pass the logger instance
        )

        if exists and existing_file_path:
            logger.info(f"Track '{current_title}' by '{current_artist}' already exists at '{existing_file_path}'. Skipping download and conversion.")
            # Update the track object to point to the existing file
            self.__c_track.song_path = existing_file_path
            _, new_ext = os.path.splitext(existing_file_path)
            self.__c_track.file_format = new_ext.lower() # Ensure it's just the extension like '.mp3'
            # self.__c_track.song_quality might need re-evaluation if we could determine quality of existing file
            # For now, assume if it exists in target format, its quality is acceptable.
            
            self.__c_track.success = True # Mark as success because the desired file is available
            self.__c_track.was_skipped = True

            parent_info, total_tracks_val = self._get_parent_info()
            
            # Build track object
            track_obj = self.__song_metadata

            # Build parent object
            parent_obj = None
            if self.__parent == "album":
                parent_obj = self.__song_metadata.album
            elif self.__parent == "playlist" and parent_info:
                parent_obj = playlistTrackObject(
                    title=parent_info.get("name"),
                    owner=userObject(name=parent_info.get("owner"))
                                )

            # Report track skipped status
            report_track_skipped(
                track_obj=track_obj,
                reason=f"Track already exists at '{existing_file_path}'",
                preferences=self.__preferences,
                parent_obj=parent_obj,
                total_tracks=total_tracks_val
            )
            return self.__c_track

        # Report initializing status for the track download
        parent_info, total_tracks_val = self._get_parent_info()
        
        # Build track object
        track_obj = self.__song_metadata

        # Build parent object
        parent_obj = None
        if self.__parent == "album":
            parent_obj = self.__song_metadata.album
        elif self.__parent == "playlist" and parent_info:
            parent_obj = playlistTrackObject(
                title=parent_info.get("name"),
                owner=userObject(name=parent_info.get("owner"))
            )

        # Report track initialization status
        report_track_initializing(
            track_obj=track_obj,
            preferences=self.__preferences,
            parent_obj=parent_obj,
            total_tracks=total_tracks_val
        )
        
        # If track does not exist in the desired final format, proceed with download/conversion
        retries = 0
        # Use the customizable retry parameters (reduced defaults for faster recovery from disconnections)
        retry_delay = getattr(self.__preferences, 'initial_retry_delay', 10)  # Default to 10 seconds (reduced from 30)
        retry_delay_increase = getattr(self.__preferences, 'retry_delay_increase', 15)  # Default to 15 seconds (reduced from 30)
        max_retries = getattr(self.__preferences, 'max_retries', 5)  # Default to 5 retries

        while True:
            try:
                # Validate session before attempting download, reconnect if needed
                if Download_JOB.session is not None:
                    if not Download_JOB.session.is_valid():
                        logger.warning("Session is invalid, attempting to reconnect...")
                        try:
                            Download_JOB.session.reconnect()
                            logger.info("Session reconnected successfully")
                        except Exception as reconnect_err:
                            logger.error(f"Failed to reconnect session: {reconnect_err}")
                            raise RuntimeError(f"Session reconnection failed: {reconnect_err}")

                track_id_obj = TrackId.from_base62(self.__ids)
                stream = Download_JOB.session.content_feeder().load_track(
                    track_id_obj,
                    VorbisOnlyAudioQuality(self.__dw_quality),
                    False,
                    None
                )
                c_stream = stream.input_stream.stream()
                total_size = stream.input_stream.size
                
                os.makedirs(dirname(self.__song_path), exist_ok=True)
                
                # Register this file as being actively downloaded
                register_active_download(self.__song_path)
                
                try:
                    with open(self.__song_path, "wb") as f:
                        if self.__real_time_dl and self.__song_metadata_dict.get("duration") and self.__song_metadata_dict.get("duration") > 0:
                            # Real-time download path
                            duration = self.__song_metadata_dict["duration"]
                            if duration > 0:
                                # Base rate limit in bytes per second to match real-time
                                base_rate_limit = total_size / duration
                                # Multiplier handling (0 disables pacing, 1=real-time, up to 10x)
                                m = getattr(self.__preferences, 'real_time_multiplier', 1)
                                try:
                                    m = int(m)
                                except Exception:
                                    m = 1
                                if m < 0:
                                    m = 0
                                if m > 10:
                                    m = 10
                                pacing_enabled = m > 0
                                rate_limit = base_rate_limit * m if pacing_enabled else None
                                chunk_size = 4096
                                bytes_written = 0
                                start_time = time.time()
                                
                                # Initialize tracking variable for percentage reporting
                                self._last_reported_percentage = -1
                                
                                while True:
                                    chunk = c_stream.read(chunk_size)
                                    if not chunk:
                                        break
                                    f.write(chunk)
                                    bytes_written += len(chunk)
                                    
                                    # Calculate current percentage (as integer)
                                    current_time = time.time()
                                    current_percentage = int((bytes_written / total_size) * 100)
                                    
                                    # Only report when percentage increases by at least 1 point
                                    if current_percentage > self._last_reported_percentage:
                                        self._last_reported_percentage = current_percentage
                                        
                                        # Report real-time progress
                                        report_track_realtime_progress(
                                            track_obj=track_obj,
                                            time_elapsed=int((current_time - start_time) * 1000),
                                            progress=current_percentage,
                                            preferences=self.__preferences,
                                            parent_obj=parent_obj,
                                            total_tracks=total_tracks_val
                                        )
                                        
                                    # Rate limiting (if pacing is enabled)
                                    if pacing_enabled and rate_limit:
                                        expected_time = bytes_written / rate_limit
                                        elapsed = time.time() - start_time
                                        if expected_time > elapsed:
                                            time.sleep(expected_time - elapsed)
                        else:
                            # Non real-time download path
                            data = c_stream.read(total_size)
                            f.write(data)
                    
                    # Close the stream after successful write
                    c_stream.close()
                    
                    # After successful download, unregister the file
                    unregister_active_download(self.__song_path)
                    break
                    
                except Exception as e:
                    # Handle any exceptions that might occur during download
                    error_msg = f"Error during download process: {str(e)}"
                    logger.error(error_msg)
                    
                    # Clean up resources
                    if 'c_stream' in locals():
                        try:
                            c_stream.close()
                        except Exception:
                            pass
                    
                    # Remove partial download if it exists
                    if os.path.exists(self.__song_path):
                        try:
                            os.remove(self.__song_path)
                        except Exception:
                            pass
                    
                    # Unregister the download
                    unregister_active_download(self.__song_path)
                    
                # After successful download, unregister the file (moved here from below)
                unregister_active_download(self.__song_path)
                break
                
            except Exception as e:
                # Handle retry logic
                global GLOBAL_RETRY_COUNT
                GLOBAL_RETRY_COUNT += 1
                retries += 1
                
                # Clean up any incomplete file
                if os.path.exists(self.__song_path):
                    os.remove(self.__song_path)
                unregister_active_download(self.__song_path)
                
                # Report retry status
                report_track_retrying(
                    track_obj=track_obj,
                    retry_count=retries,
                    seconds_left=retry_delay,
                    error=str(e),
                    preferences=self.__preferences,
                    parent_obj=parent_obj,
                    total_tracks=total_tracks_val
                )
                    
                if retries >= max_retries or GLOBAL_RETRY_COUNT >= GLOBAL_MAX_RETRIES:
                    # Final cleanup before giving up
                    if os.path.exists(self.__song_path):
                        os.remove(self.__song_path)
                    # Add track info to exception    
                    track_name = self.__song_metadata.title
                    artist_name = getattr(self.__preferences, 'artist_separator', '; ').join([a.name for a in self.__song_metadata.artists])
                    final_error_msg = f"Maximum retry limit reached for '{track_name}' by '{artist_name}' (local: {max_retries}, global: {GLOBAL_MAX_RETRIES}). Last error: {str(e)}"
                    # Store error on track object
                    if hasattr(self, '_EasyDw__c_track') and self.__c_track:
                        self.__c_track.success = False
                        self.__c_track.error_message = final_error_msg
                    raise TrackNotFound(message=final_error_msg, url=self.__link) from e
                time.sleep(retry_delay)
                retry_delay += retry_delay_increase  # Use the custom retry delay increase
                # Cap retry delay at 60 seconds to prevent session token expiration during long waits
                max_retry_delay = getattr(self.__preferences, 'max_retry_delay', 60)
                retry_delay = min(retry_delay, max_retry_delay)
                
        # Save cover image if requested, after successful download and before conversion
        if self.__preferences.save_cover and hasattr(self, '_EasyDw__song_path') and self.__song_path:
            save_cover_image_for_track(self.__song_metadata_dict, self.__song_path, self.__preferences.save_cover)

        try:
            self.__convert_audio()
        except Exception as e:
            # Improve error message formatting
            original_error_str = str(e)
            if "codec" in original_error_str.lower():
                error_msg = "Audio conversion error - Missing codec or unsupported format"
            elif "ffmpeg" in original_error_str.lower():
                error_msg = "FFmpeg error - Audio conversion failed"
            else:
                error_msg = f"Audio conversion failed: {original_error_str}"
            
            # Report error status
            report_track_error(
                track_obj=track_obj,
                error=error_msg,
                preferences=self.__preferences,
                parent_obj=parent_obj,
                total_tracks=total_tracks_val
            )
            logger.error(f"Audio conversion error: {error_msg}")
            
            # If conversion fails, clean up the .ogg file
            if os.path.exists(self.__song_path):
                os.remove(self.__song_path)
                
            # Try one more time
            time.sleep(retry_delay)
            retry_delay += retry_delay_increase
            try:
                self.__convert_audio()
            except Exception as conv_e:
                # If conversion fails twice, create a final error report
                error_msg_2 = f"Audio conversion failed after retry for '{self.__song_metadata.title}'. Original error: {str(conv_e)}"
                
                # Report error status
                report_track_error(
                    track_obj=track_obj,
                    error=error_msg_2,
                    preferences=self.__preferences,
                    parent_obj=parent_obj,
                    total_tracks=total_tracks_val
                )
                logger.error(error_msg)
                
                if os.path.exists(self.__song_path):
                    os.remove(self.__song_path)
                # Store error on track object
                if hasattr(self, '_EasyDw__c_track') and self.__c_track:
                    self.__c_track.success = False
                    self.__c_track.error_message = error_msg_2
                raise TrackNotFound(message=error_msg, url=self.__link) from conv_e

        if hasattr(self, '_EasyDw__c_track') and self.__c_track: 
            self.__c_track.success = True
            # Apply tags using unified utility
            process_and_tag_track(
                track=self.__c_track,
                metadata_dict=self.__song_metadata_dict,
                source_type='spotify'
            )
        
        # Create done status report
        parent_info, total_tracks_val = self._get_parent_info()
        current_track_val = getattr(self.__preferences, 'track_number', None)
        
        summary_obj = None
        if self.__parent is None:
            # Create a summary object for single-track downloads
            successful_track_list = [track_obj] if self.__c_track.success and not getattr(self.__c_track, 'was_skipped', False) else []
            skipped_track_list = [track_obj] if getattr(self.__c_track, 'was_skipped', False) else []
            
            summary_obj = summaryObject(
                successful_tracks=successful_track_list,
                skipped_tracks=skipped_track_list,
                failed_tracks=[],
                total_successful=len(successful_track_list),
                total_skipped=len(skipped_track_list),
                total_failed=0,
                service=Service.SPOTIFY
            )
            # Enrich summary with final path and quality
            try:
                final_path_val = getattr(self.__c_track, 'song_path', None)
            except Exception:
                final_path_val = None
            quality_key_single = self.__quality_download
            sp_quality_map_single = {
                'NORMAL': 'OGG_96',
                'HIGH': 'OGG_160',
                'VERY_HIGH': 'OGG_320'
            }
            summary_obj.final_path = final_path_val
            summary_obj.download_quality = sp_quality_map_single.get(quality_key_single, 'OGG')
            # Compute final quality/bitrate
            quality_val = None
            bitrate_val = None
            if self.__convert_to:
                # When converting, trust convert_to + bitrate
                fmt = self.__convert_to
                if fmt:
                    quality_val = fmt.lower()
                br_raw = self.__bitrate
                if br_raw:
                    digits = ''.join([c for c in str(br_raw) if c.isdigit()])
                    bitrate_val = f"{digits}k" if digits else None
            else:
                quality_val = 'ogg'
                if quality_key_single == 'NORMAL':
                    bitrate_val = '96k'
                elif quality_key_single == 'HIGH':
                    bitrate_val = '160k'
                elif quality_key_single == 'VERY_HIGH':
                    bitrate_val = '320k'
            summary_obj.quality = quality_val
            summary_obj.bitrate = bitrate_val

        # Report track done status
        # Compute final path and quality label
        final_path_val = getattr(self.__c_track, 'song_path', None)
        # Map Spotify quality to OGG bitrate label
        quality_key = self.__quality_download if hasattr(self, '_EasyDw__quality_download') else getattr(self, '_EasyDw__quality_download', None)
        quality_key = self.__quality_download if quality_key is None else quality_key
        sp_quality_map = {
            'NORMAL': 'OGG_96',
            'HIGH': 'OGG_160',
            'VERY_HIGH': 'OGG_320'
        }
        download_quality_val = sp_quality_map.get(quality_key, 'OGG')
        report_track_done(
            track_obj=track_obj,
            preferences=self.__preferences,
            summary=summary_obj,
            parent_obj=parent_obj,
            current_track=current_track_val,
            total_tracks=total_tracks_val,
            final_path=final_path_val,
            download_quality=download_quality_val
        )

        if hasattr(self, '_EasyDw__c_track') and self.__c_track and self.__c_track.success:
            # Unregister the final successful file path after all operations are done.
            unregister_active_download(self.__c_track.song_path)

        return self.__c_track

    def download_eps(self) -> Episode:
        # Use the customizable retry parameters
        retry_delay = getattr(self.__preferences, 'initial_retry_delay', 30)  # Default to 30 seconds
        retry_delay_increase = getattr(self.__preferences, 'retry_delay_increase', 30)  # Default to 30 seconds
        max_retries = getattr(self.__preferences, 'max_retries', 5)  # Default to 5 retries
        
        retries = 0
        # Initialize success to False for the episode, to be set True on completion
        if hasattr(self, '_EasyDw__c_episode') and self.__c_episode:
            self.__c_episode.success = False

        if isfile(self.__song_path) and check_track(self.__c_episode):
            ans = input(
                f"Episode \"{self.__song_path}\" already exists, do you want to redownload it?(y or n):"
            )
            if ans not in answers:
                 # If user chooses not to redownload, and file exists, consider it 'successful' for cleanup purposes if needed.
                 # However, the main .success might be for actual download processing.
                 # For now, just return. The file isn't in ACTIVE_DOWNLOADS from *this* run.
                return self.__c_episode
        episode_id = EpisodeId.from_base62(self.__ids)
        while True:
            try:
                stream = Download_JOB.session.content_feeder().load_episode(
                    episode_id,
                    AudioQuality(self.__dw_quality),
                    False,
                    None
                )
                # If load_episode is successful, break from retry loop
                break
            except Exception as e:
                global GLOBAL_RETRY_COUNT
                GLOBAL_RETRY_COUNT += 1
                
                track_obj = self.__song_metadata
                status_obj = retryingObject(
                    ids=track_obj.ids,
                    retry_count=retries,
                    seconds_left=retry_delay,
                    error=str(e),
                    convert_to=self.__convert_to,
                    bitrate=self.__bitrate
                )
                callback_obj = trackCallbackObject(track=track_obj, status_info=status_obj)
                # Log retry attempt with structured data
                report_progress(
                    reporter=Download_JOB.progress_reporter,
                    callback_obj=callback_obj
                )
                if retries >= max_retries or GLOBAL_RETRY_COUNT >= GLOBAL_MAX_RETRIES:
                    if os.path.exists(self.__song_path):
                        os.remove(self.__song_path) # Clean up partial file
                    track_name = self.__song_metadata.title
                    artist_name = getattr(self.__preferences, 'artist_separator', '; ').join([a.name for a in self.__song_metadata.artists])
                    final_error_msg = f"Maximum retry limit reached for '{track_name}' by '{artist_name}' (local: {max_retries}, global: {GLOBAL_MAX_RETRIES}). Last error: {str(e)}"
                    if hasattr(self, '_EasyDw__c_episode') and self.__c_episode:
                        self.__c_episode.success = False
                        self.__c_episode.error_message = final_error_msg
                    raise TrackNotFound(message=final_error_msg, url=self.__link) from e
                time.sleep(retry_delay)
                retry_delay += retry_delay_increase
        
        total_size = stream.input_stream.size
        os.makedirs(dirname(self.__song_path), exist_ok=True)
        
        register_active_download(self.__song_path) # Register before writing
        
        try:
            with open(self.__song_path, "wb") as f:
                c_stream = stream.input_stream.stream()
                if self.__real_time_dl and self.__song_metadata_dict.get("duration") and self.__song_metadata_dict["duration"] > 0:
                    # Restored Real-time download logic for episodes
                    duration = self.__song_metadata_dict["duration"]
                    # Base rate to match real-time
                    base_rate_limit = total_size / duration if duration > 0 else None
                    # Multiplier handling
                    m = getattr(self.__preferences, 'real_time_multiplier', 1)
                    try:
                        m = int(m)
                    except Exception:
                        m = 1
                    if m < 0:
                        m = 0
                    if m > 10:
                        m = 10
                    pacing_enabled = (base_rate_limit is not None) and m > 0
                    rate_limit = (base_rate_limit * m) if pacing_enabled else None
                    chunk_size = 4096
                    bytes_written = 0
                    start_time = time.time()
                    try:
                        while True:
                            chunk = c_stream.read(chunk_size)
                            if not chunk:
                                break
                            f.write(chunk)
                            bytes_written += len(chunk)
                            # Optional: Real-time progress reporting for episodes (can be added here if desired)
                            # Matching the style of download_try, no specific progress report inside this loop for episodes by default.
                            if pacing_enabled and rate_limit:
                                expected_time = bytes_written / rate_limit
                                elapsed_time = time.time() - start_time
                                if expected_time > elapsed_time:
                                    time.sleep(expected_time - elapsed_time)
                    except Exception as e_realtime:
                        # If any error occurs during real-time download, clean up
                        if not c_stream.closed:
                            try: 
                                c_stream.close()
                            except Exception:
                                pass
                        # f.close() is handled by with statement, but an explicit one might be here if not using with.
                        if os.path.exists(self.__song_path):
                            try: 
                                os.remove(self.__song_path) 
                            except Exception:
                                pass
                        unregister_active_download(self.__song_path)
                        episode_title = self.__song_metadata.title
                        artist_name = getattr(self.__preferences, 'artist_separator', '; ').join([a.name for a in self.__song_metadata.artists])
                        final_error_msg = f"Error during real-time download for episode '{episode_title}' by '{artist_name}' (URL: {self.__link}). Error: {str(e_realtime)}"
                        logger.error(final_error_msg)
                        if hasattr(self, '_EasyDw__c_episode') and self.__c_episode:
                            self.__c_episode.success = False
                            self.__c_episode.error_message = final_error_msg
                        raise TrackNotFound(message=final_error_msg, url=self.__link) from e_realtime
                else:
                    # Restored Non real-time download logic for episodes
                    try:
                        data = c_stream.read(total_size)
                        f.write(data)
                    except Exception as e_standard:
                         # If any error occurs during standard download, clean up
                        if not c_stream.closed:
                            try: 
                                c_stream.close()
                            except Exception:
                                pass
                        if os.path.exists(self.__song_path):
                            try: 
                                os.remove(self.__song_path) 
                            except Exception:
                                pass
                        unregister_active_download(self.__song_path)
                        episode_title = self.__song_metadata.title
                        artist_name = getattr(self.__preferences, 'artist_separator', '; ').join([a.name for a in self.__song_metadata.artists])
                        final_error_msg = f"Error during standard download for episode '{episode_title}' by '{artist_name}' (URL: {self.__link}). Error: {str(e_standard)}"
                        logger.error(final_error_msg)
                        if hasattr(self, '_EasyDw__c_episode') and self.__c_episode:
                            self.__c_episode.success = False
                            self.__c_episode.error_message = final_error_msg
                        raise TrackNotFound(message=final_error_msg, url=self.__link) from e_standard
                
                # If all went well with writing to file and reading stream:
                if not c_stream.closed: c_stream.close()

            # If with open completes without internal exceptions leading to TrackNotFound:
            unregister_active_download(self.__song_path) # Unregister after successful write of original file
        
        except TrackNotFound: # Re-raise if it was an internally handled download error
            raise
        except Exception as e_outer: # Catch other potential errors around file handling or unexpected issues
            # Cleanup for download part if an unexpected error occurs outside the inner try-excepts
            if 'c_stream' in locals() and hasattr(c_stream, 'closed') and not c_stream.closed:
                try: c_stream.close() 
                except Exception:
                    pass
            if os.path.exists(self.__song_path):
                try: os.remove(self.__song_path) 
                except Exception:
                    pass
            unregister_active_download(self.__song_path)
            episode_title = self.__song_metadata.title
            error_message = f"Failed to download episode '{episode_title}' (URL: {self.__link}) during file operations. Error: {str(e_outer)}"
            logger.error(error_message)
            if hasattr(self, '_EasyDw__c_episode') and self.__c_episode:
                self.__c_episode.success = False
                self.__c_episode.error_message = error_message
            raise TrackNotFound(message=error_message, url=self.__link) from e_outer
            
        # If download was successful, proceed to conversion and tagging
        try:
            self.__convert_audio() # This will update self.__c_episode.file_format and path if conversion occurs
                               # It also handles registration/unregistration of intermediate/final files during conversion.
        except Exception as conv_e:
            # Conversion failed. __convert_audio or underlying convert_audio should have cleaned up its own temps.
            # The original downloaded file (if __convert_audio started from it) might still exist or be the self.__song_path.
            # Or self.__song_path might be a partially converted file if convert_audio failed mid-way and didn't cleanup perfectly.
            episode_title = self.__song_metadata.title
            error_message = f"Audio conversion for episode '{episode_title}' failed. Original error: {str(conv_e)}"
            
            track_obj = self.__song_metadata
            status_obj = errorObject(
                ids=track_obj.ids,
                error=error_message,
                convert_to=self.__convert_to,
                bitrate=self.__bitrate
            )
            callback_obj = trackCallbackObject(track=track_obj, status_info=status_obj)
            report_progress(
                reporter=Download_JOB.progress_reporter,
                callback_obj=callback_obj
            )
            # Attempt to remove self.__song_path, which is the latest known path for this episode
            if os.path.exists(self.__song_path):
                os.remove(self.__song_path)
                unregister_active_download(self.__song_path) # Unregister it as it failed/was removed
            
            logger.error(error_message)
            if hasattr(self, '_EasyDw__c_episode') and self.__c_episode:
                self.__c_episode.success = False
                self.__c_episode.error_message = error_message
            raise TrackNotFound(message=error_message, url=self.__link) from conv_e
                
        # If we reach here, download and any conversion were successful.
        if hasattr(self, '_EasyDw__c_episode') and self.__c_episode:
            self.__c_episode.success = True 
            # Apply tags using unified utility
            process_and_tag_episode(
                episode=self.__c_episode,
                metadata_dict=self.__song_metadata_dict,
                source_type='spotify'
            )
            # Unregister the final successful file path for episodes, as it's now complete.
            # self.__c_episode.episode_path would have been updated by __convert_audio__ if conversion occurred.
            unregister_active_download(self.__c_episode.episode_path)
            
        return self.__c_episode

def download_cli(preferences: Preferences) -> None:
    __link = preferences.link
    __output_dir = preferences.output_dir
    __not_interface = preferences.not_interface
    __quality_download = preferences.quality_download
    __recursive_download = preferences.recursive_download
    # Build argv list instead of shell string (distroless-safe)
    argv = ["deez-dw.py", "-so", "spo", "-l", __link]
    if __output_dir:
        argv += ["-o", str(__output_dir)]
    if __not_interface:
        argv += ["-g"]
    if __quality_download:
        argv += ["-q", str(__quality_download)]
    if __recursive_download:
        argv += ["-rd"]
    prog = shutil.which(argv[0])
    if not prog:
        logger.error("deez-dw.py CLI not found in PATH; cannot run download_cli in this environment.")
        return
    argv[0] = prog
    try:
        result = subprocess.run(argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        if result.returncode != 0:
            logger.error(f"deez-dw.py exited with {result.returncode}: {result.stderr.strip()}")
    except Exception as e:
        logger.error(f"Failed to execute deez-dw.py: {e}")

class DwTrack:
    def __init__(
        self,
        preferences: Preferences
    ) -> None:
        self.__preferences = preferences

    def dw(self) -> Track:
        track = EASY_DW(self.__preferences).easy_dw()
        # No error handling needed here - if track.success is False but was_skipped is True,
        # it's an intentional skip, not an error
        return track

class DwAlbum:
    def __init__(
        self,
        preferences: Preferences
    ) -> None:
        self.__preferences = preferences
        self.__ids = self.__preferences.ids
        self.__make_zip = self.__preferences.make_zip
        self.__output_dir = self.__preferences.output_dir
        self.__album_metadata = self.__preferences.song_metadata

    def dw(self) -> Album:
        # Report album initializing status
        album_obj = self.__album_metadata
        report_album_initializing(album_obj)
        
        pic_url = max(album_obj.images, key=lambda i: i.get('height', 0) * i.get('width', 0)).get('url') if album_obj.images else None
        image_bytes = request(pic_url).content if pic_url else None
        
        album = Album(self.__ids)
        album.image = image_bytes
        album.nb_tracks = album_obj.total_tracks
        album.album_name = album_obj.title
        album.upc = album_obj.ids.upc
        tracks = album.tracks
        album.md5_image = self.__ids
        album.tags = album_object_to_dict(self.__album_metadata, source_type='spotify', artist_separator=getattr(self.__preferences, 'artist_separator', '; ')) # For top-level album tags if needed
        album.tags['artist_separator'] = getattr(self.__preferences, 'artist_separator', '; ')
        
        album_base_directory = get_album_directory(
            album.tags,
            self.__output_dir,
            custom_dir_format=self.__preferences.custom_dir_format,
            pad_tracks=self.__preferences.pad_tracks
        )
        
        for a, track_in_album in enumerate(album_obj.tracks):
            
            c_preferences = deepcopy(self.__preferences)
            
            try:
                # Fetch full track object as album endpoint only provides simplified track objects
                full_track_obj = tracking(
                    track_in_album.ids.spotify,
                    album_data_for_track=self.__preferences.json_data, # pass raw album json
                    market=self.__preferences.market
                )

                if not full_track_obj:
                    raise TrackNotFound(f"Could not fetch metadata for track ID {track_in_album.ids.spotify}")

                c_preferences.song_metadata = full_track_obj
                c_preferences.ids = full_track_obj.ids.spotify
                c_preferences.link = f"https://open.spotify.com/track/{c_preferences.ids}"
                # Set album position for progress reporting (not for metadata - that comes from API)
                c_preferences.track_number = a + 1
                c_preferences.pad_number_width = getattr(self.__preferences, 'pad_number_width', 'auto')

                track = EASY_DW(c_preferences, parent='album').easy_dw()

            except Exception as e:
                # Create a failed track object for the summary
                song_tags = _track_object_to_dict(track_in_album) if isinstance(track_in_album, trackObject) else {'music': UNKNOWN_TRACK_TITLE}
                track = Track(tags=song_tags, song_path=None, file_format=None, quality=None, link=None, ids=track_in_album.ids)
                track.success = False
                track.error_message = str(e)
                logger.warning(f"Track '{song_tags.get('music')}' from album '{album.album_name}' failed to download. Reason: {track.error_message}")

            tracks.append(track)
        
        # Save album cover image
        if self.__preferences.save_cover and album.image and album_base_directory:
            save_cover_image(album.image, album_base_directory, "cover.jpg")
        
        if self.__make_zip:
            song_quality = tracks[0].quality if tracks and tracks[0].quality else 'HIGH' # Fallback quality
            zip_name = create_zip(
                tracks,
                output_dir=self.__output_dir,
                song_metadata=album.tags,
                song_quality=song_quality,
                custom_dir_format=self.__preferences.custom_dir_format
            )
            album.zip_path = zip_name
            
        successful_tracks = []
        failed_tracks = []
        skipped_tracks = []
        for track in tracks:
            # track.tags is a dict.
            track_obj_for_cb = trackObject(
                title=track.tags.get('music', UNKNOWN_TRACK_TITLE),
                artists=[artistTrackObject(name=artist.strip()) for artist in track.tags.get('artist', '').split(';')]
            )

            if getattr(track, 'was_skipped', False):
                skipped_tracks.append(track_obj_for_cb)
            elif track.success:
                successful_tracks.append(track_obj_for_cb)
            else:
                failed_tracks.append(failedTrackObject(
                    track=track_obj_for_cb,
                    reason=getattr(track, 'error_message', 'Unknown reason')
                ))

        summary_obj = summaryObject(
            successful_tracks=successful_tracks,
            skipped_tracks=skipped_tracks,
            failed_tracks=failed_tracks,
            total_successful=len(successful_tracks),
            total_skipped=len(skipped_tracks),
            total_failed=len(failed_tracks),
            service=Service.SPOTIFY
        )
        # Compute final quality/bitrate for album summary
        quality_val = None
        bitrate_val = None
        conv = getattr(self.__preferences, 'convert_to', None)
        if conv:
            quality_val = conv.lower()
            br_raw = getattr(self.__preferences, 'bitrate', None)
            if br_raw:
                digits = ''.join([c for c in str(br_raw) if c.isdigit()])
                bitrate_val = f"{digits}k" if digits else None
        else:
            quality_val = 'ogg'
            qkey = (getattr(self.__preferences, 'quality_download', None) or 'NORMAL').upper()
            if qkey == 'NORMAL':
                bitrate_val = '96k'
            elif qkey == 'HIGH':
                bitrate_val = '160k'
            elif qkey == 'VERY_HIGH':
                bitrate_val = '320k'
        summary_obj.quality = quality_val
        summary_obj.bitrate = bitrate_val
        
        report_album_done(album_obj, summary_obj)
        
        return album

class DwPlaylist:
    def __init__(
        self,
        preferences: Preferences
    ) -> None:
        self.__preferences = preferences
        self.__ids = self.__preferences.ids
        self.__json_data = preferences.json_data
        self.__make_zip = self.__preferences.make_zip
        self.__output_dir = self.__preferences.output_dir
        self.__song_metadata_list = self.__preferences.song_metadata
        self.__playlist_tracks_json = getattr(self.__preferences, 'playlist_tracks_json', None)

    def dw(self) -> Playlist:
        playlist_name = self.__json_data.get('name', 'unknown')
        playlist_owner_name = self.__json_data.get('owner', {}).get('display_name', 'Unknown Owner')
        playlist_id = self.__ids

        # --- Build playlistObject for callbacks ---
        playlist_tracks_for_cb = []
        if self.__playlist_tracks_json:
            for item in self.__playlist_tracks_json:
                track_data = item.get('track')
                if track_data:
                    track_playlist_obj = json_to_track_playlist_object(track_data)
                    if track_playlist_obj:
                        playlist_tracks_for_cb.append(track_playlist_obj)

        playlist_obj_for_cb = playlistObject(
            title=playlist_name,
            owner=userObject(name=playlist_owner_name, ids=IDs(spotify=self.__json_data.get('owner', {}).get('id'))),
            ids=IDs(spotify=playlist_id),
            images=self.__json_data.get('images', []),
            tracks=playlist_tracks_for_cb,
            description=self.__json_data.get('description')
        )
        # --- End build playlistObject ---

        # Report playlist initializing status
        report_playlist_initializing(playlist_obj_for_cb)
        
        # --- Prepare the m3u playlist file ---
        m3u_path = create_m3u_file(self.__output_dir, playlist_name)
        # -------------------------------------

        playlist = Playlist()
        tracks = playlist.tracks
        for idx, c_song_metadata in enumerate(self.__song_metadata_list):
            track = None

            if isinstance(c_song_metadata, dict) and 'error_type' in c_song_metadata:
                track_title = c_song_metadata.get('name', UNKNOWN_TRACK_TITLE)
                track_ids = c_song_metadata.get('ids')
                error_message = c_song_metadata.get('error_message', 'Unknown error during metadata retrieval.')
                logger.warning(f"Skipping download for track '{track_title}' (ID: {track_ids}) from playlist '{playlist_name}' due to error: {error_message}")
                
                track_tags = {'music': track_title, 'ids': track_ids}
                track = Track(tags=track_tags, song_path=None, file_format=None, quality=None, link=None, ids=track_ids)
                track.success = False
                track.error_message = error_message
                tracks.append(track)
                continue

            # c_song_metadata is a trackObject
            c_preferences = deepcopy(self.__preferences)
            c_preferences.ids = c_song_metadata.ids.spotify
            c_preferences.song_metadata = c_song_metadata
            c_preferences.json_data = self.__json_data
            c_preferences.track_number = idx + 1
            c_preferences.link = f"https://open.spotify.com/track/{c_preferences.ids}" if c_preferences.ids else None
            c_preferences.pad_number_width = getattr(self.__preferences, 'pad_number_width', 'auto')

            easy_dw_instance = EASY_DW(c_preferences, parent='playlist')

            try:
                track = easy_dw_instance.easy_dw()
            except Exception as e:
                track = easy_dw_instance.get_no_dw_track()
                if not isinstance(track, Track):
                    track = Track(_track_object_to_dict(c_song_metadata), None, None, None, c_preferences.link, c_preferences.ids)
                track.success = False
                track.error_message = str(e)
                logger.warning(f"Failed to download track '{c_song_metadata.title}' from playlist '{playlist_name}'. Reason: {track.error_message}")

            if track:
                tracks.append(track)

            # --- Append the final track to the m3u file with extended format ---
            if track and track.success and hasattr(track, 'song_path') and track.song_path:
                append_track_to_m3u(m3u_path, track)
            # ---------------------------------------------------------------------
        
        if self.__make_zip:
            playlist_title = self.__json_data['name']
            zip_name = f"{self.__output_dir}/{playlist_title} [playlist {self.__ids}]"
            create_zip(tracks, zip_name=zip_name)
            playlist.zip_path = zip_name
            
        # Report playlist done status
        successful_tracks_cb = []
        failed_tracks_cb = []
        skipped_tracks_cb = []
        for track in tracks:
            # Create a trackObject for the callback from the internal Track object's tags
            track_tags = track.tags
            track_obj_for_cb = trackObject(
                title=track_tags.get('music', UNKNOWN_TRACK_TITLE),
                artists=[artistTrackObject(name=artist.strip()) for artist in track_tags.get('artist', '').split(';')]
            )

            if getattr(track, 'was_skipped', False):
                skipped_tracks_cb.append(track_obj_for_cb)
            elif track.success:
                successful_tracks_cb.append(track_obj_for_cb)
            else:
                failed_tracks_cb.append(failedTrackObject(
                    track=track_obj_for_cb,
                    reason=getattr(track, 'error_message', 'Unknown reason')
                ))

        summary_obj = summaryObject(
            successful_tracks=successful_tracks_cb,
            skipped_tracks=skipped_tracks_cb,
            failed_tracks=failed_tracks_cb,
            total_successful=len(successful_tracks_cb),
            total_skipped=len(skipped_tracks_cb),
            total_failed=len(failed_tracks_cb),
            service=Service.SPOTIFY
        )
        # Compute final quality/bitrate for playlist summary
        quality_val = None
        bitrate_val = None
        conv = getattr(self.__preferences, 'convert_to', None)
        if conv:
            quality_val = conv.lower()
            br_raw = getattr(self.__preferences, 'bitrate', None)
            if br_raw:
                digits = ''.join([c for c in str(br_raw) if c.isdigit()])
                bitrate_val = f"{digits}k" if digits else None
        else:
            quality_val = 'ogg'
            qkey = (getattr(self.__preferences, 'quality_download', None) or 'NORMAL').upper()
            if qkey == 'NORMAL':
                bitrate_val = '96k'
            elif qkey == 'HIGH':
                bitrate_val = '160k'
            elif qkey == 'VERY_HIGH':
                bitrate_val = '320k'
        summary_obj.quality = quality_val
        summary_obj.bitrate = bitrate_val
        
        # Include m3u path in summary and callback
        report_playlist_done(playlist_obj_for_cb, summary_obj, m3u_path=m3u_path)
        
        return playlist

class DwEpisode:
    def __init__(
        self,
        preferences: Preferences
    ) -> None:
        self.__preferences = preferences

    def dw(self) -> Episode:
        episode_obj = self.__preferences.song_metadata # This is a trackObject
        
        # Build status object
        status_obj_init = initializingObject(ids=episode_obj.ids)
        callback_obj_init = trackCallbackObject(track=episode_obj, status_info=status_obj_init)
        report_progress(
            reporter=Download_JOB.progress_reporter,
            callback_obj=callback_obj_init
        )
        
        episode = EASY_DW(self.__preferences).download_eps()
        
        # Build status object
        status_obj_done = doneObject(ids=episode_obj.ids)
        callback_obj_done = trackCallbackObject(track=episode_obj, status_info=status_obj_done)
        report_progress(
            reporter=Download_JOB.progress_reporter,
            callback_obj=callback_obj_done
        )
        
        return episode


# Backward-compatible aliases for existing imports/usages.
Download_JOB = DownloadJob
EASY_DW = EasyDw
DW_TRACK = DwTrack
DW_ALBUM = DwAlbum
DW_PLAYLIST = DwPlaylist
DW_EPISODE = DwEpisode
