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
from deezspot.libutils.download_helpers import (
    build_song_path_with_preferences,
    resolve_spotify_summary_media,
)
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
    TrackObject, AlbumTrackObject, PlaylistTrackObject, ArtistTrackObject,
    TrackCallbackObject, AlbumCallbackObject, PlaylistCallbackObject,
    InitializingObject, SkippedObject, RetryingObject, RealTimeObject, ErrorObject, DoneObject,
    FailedTrackObject, SummaryObject,
    AlbumObject, ArtistAlbumObject,
    PlaylistObject,
    UserObject,
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
def _track_object_to_dict(track_obj: TrackObject) -> dict:
    """Converts a TrackObject into a dictionary for legacy functions like taggers."""
    return track_object_to_dict(track_obj, source_type='spotify')

# Use unified metadata converter
def _album_object_to_dict(album_obj: AlbumObject) -> dict:
    """Converts an AlbumObject into a dictionary for legacy functions."""
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
        self.__song_path = build_song_path_with_preferences(
            set_path_func=set_path,
            song_metadata_dict=self.__song_metadata_dict,
            output_dir=self.__output_dir,
            song_quality=self.__song_quality,
            file_format=self.__file_format,
            preferences=self.__preferences,
            parent=self.__parent,
            song_metadata=self.__song_metadata,
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

    @staticmethod
    def _run_ffmpeg_remux(temp_filename: str, output_path: str) -> None:
        ffmpeg_path = shutil.which("ffmpeg") or "/usr/local/bin/ffmpeg"
        try:
            result = subprocess.run(
                [
                    ffmpeg_path,
                    "-y",
                    "-hide_banner",
                    "-loglevel",
                    "error",
                    "-i",
                    temp_filename,
                    "-c:a",
                    "copy",
                    output_path,
                ],
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                check=False,
            )
        except FileNotFoundError as fnf:
            raise RuntimeError(f"ffmpeg not found: attempted '{ffmpeg_path}'. Ensure it is present in PATH.") from fnf
        if result.returncode != 0 or not os.path.exists(output_path):
            raise RuntimeError(f"ffmpeg remux failed (rc={result.returncode}). stderr: {result.stderr.strip()}")

    def _update_media_path_after_conversion(self, converted_path: str) -> None:
        self.__song_path = converted_path
        current_attr_name = 'song_path' if self.__type == "track" else 'episode_path'
        current_media = self.__c_track if self.__type == "track" else self.__c_episode
        if current_media:
            setattr(current_media, current_attr_name, converted_path)
            _, new_ext = os.path.splitext(converted_path)
            if new_ext:
                current_media.file_format = new_ext.lower()
                self.__file_format = new_ext.lower()

    def _run_requested_conversion(self) -> bool:
        if not self.__convert_to:
            return False
        format_name = self.__convert_to
        bitrate = self.__bitrate
        if not format_name:
            return False
        path_before_final_conversion = self.__song_path
        try:
            converted_path = convert_audio(
                path_before_final_conversion,
                format_name,
                bitrate,
                register_active_download,
                unregister_active_download,
            )
        except Exception as conv_error:
            logger.error(f"Audio conversion to {format_name} error: {str(conv_error)}")
            return False
        if converted_path != path_before_final_conversion:
            self._update_media_path_after_conversion(converted_path)
        return True

    def __convert_audio(self) -> None:
        og_song_path_for_ogg_output = self.__song_path
        temp_filename = og_song_path_for_ogg_output.replace(".ogg", ".tmp")
        os_replace(og_song_path_for_ogg_output, temp_filename)
        register_active_download(temp_filename)
        
        try:
            self._run_ffmpeg_remux(temp_filename, og_song_path_for_ogg_output)
            unregister_active_download(temp_filename)
            if os.path.exists(temp_filename):
                remove(temp_filename)
            
            self.__song_path = og_song_path_for_ogg_output
            register_active_download(self.__song_path)
            
            conversion_finished = self._run_requested_conversion()
            if not conversion_finished:
                unregister_active_download(self.__song_path)
                
        except Exception as e:
            if os.path.exists(temp_filename) and not os.path.exists(og_song_path_for_ogg_output):
                os_replace(temp_filename, og_song_path_for_ogg_output)
            
            if os.path.exists(temp_filename):
                unregister_active_download(temp_filename)
                remove(temp_filename)
            raise e

    def get_no_dw_track(self) -> Track:
        return self.__c_track

    def _current_track(self) -> Track | None:
        if hasattr(self, "_EasyDw__c_track") and self.__c_track:
            return self.__c_track
        return None

    def _mark_active_media_as_pending(self) -> None:
        track = self._current_track()
        if track is not None:
            track.success = False
            return
        if hasattr(self, "_EasyDw__c_episode") and self.__c_episode:
            self.__c_episode.success = False

    def _track_identity(self) -> tuple[str, str]:
        song_title = self.__song_metadata.title
        artist_name = getattr(self.__preferences, "artist_separator", "; ").join(
            [a.name for a in self.__song_metadata.artists]
        )
        return song_title, artist_name

    def _raise_download_failure(self, error: Exception) -> None:
        song_title, artist_name = self._track_identity()
        error_message = (
            f"Download failed for '{song_title}' by '{artist_name}' "
            f"(URL: {self.__link}). Original error: {str(error)}"
        )
        logger.error(error_message)
        traceback.print_exc()
        track = self._current_track()
        if track is not None:
            track.success = False
            track.error_message = error_message
        raise TrackNotFound(message=error_message, url=self.__link) from error

    def _raise_post_attempt_failure(self, track: Track) -> None:
        song_title, artist_name = self._track_identity()
        original_error_msg = getattr(
            track, "error_message", "Download failed for an unspecified reason after attempt."
        )
        final_error_msg = (
            "Cannot download '{title}' by '{artist}'. Reason: {reason}".format(
                title=song_title, artist=artist_name, reason=original_error_msg
            )
        )
        current_link = track.link if hasattr(track, "link") and track.link else self.__link
        logger.error(f"{final_error_msg} (URL: {current_link})")
        track.error_message = final_error_msg
        raise TrackNotFound(message=final_error_msg, url=current_link)

    def easy_dw(self) -> Track:
        # Process image data using unified utility
        self.__song_metadata_dict = enhance_metadata_with_image(self.__song_metadata_dict)

        try:
            # Initialize success to False; download_try sets success=True on completion.
            self._mark_active_media_as_pending()
            self.download_try() # This should set self.__c_track.success = True if successful

        except Exception as e:
            self._raise_download_failure(e)

        track = self._current_track()
        if track is None:
            return self.__c_track

        # If the track was skipped (e.g. file already exists), return it immediately.
        # download_try sets success=False and was_skipped=True in this case.
        if getattr(track, "was_skipped", False):
            return track

        # Final check for non-skipped tracks that might have failed after download_try returned.
        # This handles cases where download_try didn't raise an exception but success is still False.
        if not track.success:
            self._raise_post_attempt_failure(track)
            
        # If we reach here, the track should be successful and not skipped.
        process_and_tag_track(
            track=track,
            metadata_dict=self.__song_metadata_dict,
            source_type='spotify',
            save_cover=getattr(self.__preferences, 'save_cover', False)
        )
        
        # Unregister the final successful file path after all operations are done.
        # track.song_path would have been updated by __convert_audio__ if conversion occurred.
        unregister_active_download(track.song_path)
        
        return track

    def _track_context(self):
        parent_info, total_tracks_val = self._get_parent_info()
        parent_obj = None
        if self.__parent == "album":
            parent_obj = self.__song_metadata.album
        elif self.__parent == "playlist" and parent_info:
            parent_obj = PlaylistTrackObject(
                title=parent_info.get("name"),
                owner=UserObject(name=parent_info.get("owner")),
            )
        return self.__song_metadata, parent_obj, total_tracks_val

    def _skip_existing_track_if_present(self, track_obj, parent_obj, total_tracks_val):
        current_title = self.__song_metadata.title
        current_album = self.__song_metadata.album.title if self.__song_metadata.album else ""
        current_artist = getattr(self.__preferences, "artist_separator", "; ").join(
            [a.name for a in self.__song_metadata.artists]
        )
        exists, existing_file_path = check_track_exists(
            original_song_path=self.__song_path,
            title=current_title,
            album=current_album,
            convert_to=self.__preferences.convert_to,
            logger=logger,
        )
        if not (exists and existing_file_path):
            return None

        logger.info(
            f"Track '{current_title}' by '{current_artist}' already exists at '{existing_file_path}'. "
            "Skipping download and conversion."
        )
        self.__c_track.song_path = existing_file_path
        _, new_ext = os.path.splitext(existing_file_path)
        self.__c_track.file_format = new_ext.lower()
        self.__c_track.success = True
        self.__c_track.was_skipped = True
        report_track_skipped(
            track_obj=track_obj,
            reason=f"Track already exists at '{existing_file_path}'",
            preferences=self.__preferences,
            parent_obj=parent_obj,
            total_tracks=total_tracks_val,
        )
        return self.__c_track

    @staticmethod
    def _close_stream(stream_obj) -> None:
        if stream_obj is None:
            return
        try:
            stream_obj.close()
        except Exception:
            pass

    @staticmethod
    def _safe_remove_file(path: str) -> None:
        if not os.path.exists(path):
            return
        try:
            os.remove(path)
        except Exception:
            pass

    def _ensure_session_ready(self) -> None:
        if Download_JOB.session is None:
            raise RuntimeError("Spotify session is not initialized.")
        if Download_JOB.session.is_valid():
            return

        logger.warning("Session is invalid, attempting to reconnect...")
        try:
            Download_JOB.session.reconnect()
            logger.info("Session reconnected successfully")
        except Exception as reconnect_err:
            logger.error(f"Failed to reconnect session: {reconnect_err}")
            raise RuntimeError(f"Session reconnection failed: {reconnect_err}") from reconnect_err

    @staticmethod
    def _bounded_realtime_multiplier(raw_multiplier) -> int:
        try:
            multiplier = int(raw_multiplier)
        except Exception:
            multiplier = 1
        return max(0, min(10, multiplier))

    def _should_use_realtime_download(self) -> bool:
        duration = self.__song_metadata_dict.get("duration")
        return bool(self.__real_time_dl and duration and duration > 0)

    def _resolve_rate_limit(self, total_size: int):
        duration = self.__song_metadata_dict.get("duration")
        if not duration or duration <= 0:
            return False, None
        multiplier = self._bounded_realtime_multiplier(
            getattr(self.__preferences, "real_time_multiplier", 1)
        )
        if multiplier <= 0:
            return False, None
        return True, (total_size / duration) * multiplier

    @staticmethod
    def _write_standard_stream(c_stream, output_file, total_size: int) -> None:
        data = c_stream.read(total_size)
        output_file.write(data)

    def _write_track_stream_realtime(
        self,
        c_stream,
        output_file,
        total_size: int,
        track_obj,
        parent_obj,
        total_tracks_val,
    ) -> None:
        pacing_enabled, rate_limit = self._resolve_rate_limit(total_size)
        bytes_written = 0
        start_time = time.time()
        chunk_size = 4096
        self._last_reported_percentage = -1

        while True:
            chunk = c_stream.read(chunk_size)
            if not chunk:
                break
            output_file.write(chunk)
            bytes_written += len(chunk)
            current_percentage = int((bytes_written / total_size) * 100) if total_size > 0 else 100

            if current_percentage > self._last_reported_percentage:
                self._last_reported_percentage = current_percentage
                report_track_realtime_progress(
                    track_obj=track_obj,
                    time_elapsed=int((time.time() - start_time) * 1000),
                    progress=current_percentage,
                    preferences=self.__preferences,
                    parent_obj=parent_obj,
                    total_tracks=total_tracks_val,
                )

            if pacing_enabled and rate_limit:
                expected_time = bytes_written / rate_limit
                elapsed = time.time() - start_time
                if expected_time > elapsed:
                    time.sleep(expected_time - elapsed)

    def _download_track_once(self, track_obj, parent_obj, total_tracks_val) -> None:
        self._ensure_session_ready()
        track_id_obj = TrackId.from_base62(self.__ids)
        stream = Download_JOB.session.content_feeder().load_track(
            track_id_obj,
            VorbisOnlyAudioQuality(self.__dw_quality),
            False,
            None,
        )
        c_stream = stream.input_stream.stream()
        total_size = stream.input_stream.size
        os.makedirs(dirname(self.__song_path), exist_ok=True)
        register_active_download(self.__song_path)
        try:
            with open(self.__song_path, "wb") as output_file:
                if self._should_use_realtime_download():
                    self._write_track_stream_realtime(
                        c_stream,
                        output_file,
                        total_size,
                        track_obj,
                        parent_obj,
                        total_tracks_val,
                    )
                else:
                    self._write_standard_stream(c_stream, output_file, total_size)
        finally:
            self._close_stream(c_stream)
            unregister_active_download(self.__song_path)

    def _report_track_retry(self, track_obj, retry_count: int, delay_seconds: int, error: Exception, parent_obj, total_tracks_val) -> None:
        report_track_retrying(
            track_obj=track_obj,
            retry_count=retry_count,
            seconds_left=delay_seconds,
            error=str(error),
            preferences=self.__preferences,
            parent_obj=parent_obj,
            total_tracks=total_tracks_val,
        )

    def _raise_track_retry_limit(self, max_retries: int, last_error: Exception) -> None:
        track_name = self.__song_metadata.title
        artist_name = getattr(self.__preferences, "artist_separator", "; ").join(
            [a.name for a in self.__song_metadata.artists]
        )
        final_error_msg = (
            f"Maximum retry limit reached for '{track_name}' by '{artist_name}' "
            f"(local: {max_retries}, global: {GLOBAL_MAX_RETRIES}). Last error: {str(last_error)}"
        )
        if hasattr(self, "_EasyDw__c_track") and self.__c_track:
            self.__c_track.success = False
            self.__c_track.error_message = final_error_msg
        raise TrackNotFound(message=final_error_msg, url=self.__link) from last_error

    def _download_track_with_retries(self, track_obj, parent_obj, total_tracks_val, retry_delay: int, retry_delay_increase: int, max_retries: int) -> int:
        retries = 0
        current_retry_delay = retry_delay
        max_retry_delay = getattr(self.__preferences, "max_retry_delay", 60)

        while True:
            try:
                self._download_track_once(track_obj, parent_obj, total_tracks_val)
                return current_retry_delay
            except Exception as error:
                global GLOBAL_RETRY_COUNT
                GLOBAL_RETRY_COUNT += 1
                retries += 1
                self._safe_remove_file(self.__song_path)
                unregister_active_download(self.__song_path)
                self._report_track_retry(
                    track_obj,
                    retries,
                    current_retry_delay,
                    error,
                    parent_obj,
                    total_tracks_val,
                )
                if retries >= max_retries or GLOBAL_RETRY_COUNT >= GLOBAL_MAX_RETRIES:
                    self._raise_track_retry_limit(max_retries, error)
                time.sleep(current_retry_delay)
                current_retry_delay = min(current_retry_delay + retry_delay_increase, max_retry_delay)

    @staticmethod
    def _format_conversion_error(error: Exception) -> str:
        original_error = str(error)
        lowered_error = original_error.lower()
        if "codec" in lowered_error:
            return "Audio conversion error - Missing codec or unsupported format"
        if "ffmpeg" in lowered_error:
            return "FFmpeg error - Audio conversion failed"
        return f"Audio conversion failed: {original_error}"

    def _convert_track_with_retry(self, track_obj, parent_obj, total_tracks_val, retry_delay: int) -> None:
        try:
            self.__convert_audio()
            return
        except Exception as conversion_error:
            first_error_msg = self._format_conversion_error(conversion_error)
            report_track_error(
                track_obj=track_obj,
                error=first_error_msg,
                preferences=self.__preferences,
                parent_obj=parent_obj,
                total_tracks=total_tracks_val,
            )
            logger.error(f"Audio conversion error: {first_error_msg}")
            self._safe_remove_file(self.__song_path)

        time.sleep(retry_delay)
        try:
            self.__convert_audio()
        except Exception as conversion_retry_error:
            final_error_msg = (
                f"Audio conversion failed after retry for '{self.__song_metadata.title}'. "
                f"Original error: {str(conversion_retry_error)}"
            )
            report_track_error(
                track_obj=track_obj,
                error=final_error_msg,
                preferences=self.__preferences,
                parent_obj=parent_obj,
                total_tracks=total_tracks_val,
            )
            logger.error(final_error_msg)
            self._safe_remove_file(self.__song_path)
            if hasattr(self, "_EasyDw__c_track") and self.__c_track:
                self.__c_track.success = False
                self.__c_track.error_message = final_error_msg
            raise TrackNotFound(message=final_error_msg, url=self.__link) from conversion_retry_error

    def _spotify_download_quality_label(self) -> str:
        quality_map = {
            "NORMAL": "OGG_96",
            "HIGH": "OGG_160",
            "VERY_HIGH": "OGG_320",
        }
        return quality_map.get(self.__quality_download, "OGG")

    def _single_track_summary(self, track_obj):
        if self.__parent is not None:
            return None
        successful_tracks = [track_obj] if self.__c_track.success and not getattr(self.__c_track, "was_skipped", False) else []
        skipped_tracks = [track_obj] if getattr(self.__c_track, "was_skipped", False) else []
        summary_obj = SummaryObject(
            successful_tracks=successful_tracks,
            skipped_tracks=skipped_tracks,
            failed_tracks=[],
            total_successful=len(successful_tracks),
            total_skipped=len(skipped_tracks),
            total_failed=0,
            service=Service.SPOTIFY,
        )
        summary_obj.final_path = getattr(self.__c_track, "song_path", None)
        summary_obj.download_quality = self._spotify_download_quality_label()
        quality_val, bitrate_val = resolve_spotify_summary_media(
            quality_download=self.__quality_download,
            convert_to=self.__convert_to,
            bitrate=self.__bitrate,
        )
        summary_obj.quality = quality_val
        summary_obj.bitrate = bitrate_val
        return summary_obj

    def _report_track_done_status(self, track_obj, parent_obj, total_tracks_val) -> None:
        report_track_done(
            track_obj=track_obj,
            preferences=self.__preferences,
            summary=self._single_track_summary(track_obj),
            parent_obj=parent_obj,
            current_track=getattr(self.__preferences, "track_number", None),
            total_tracks=total_tracks_val,
            final_path=getattr(self.__c_track, "song_path", None),
            download_quality=self._spotify_download_quality_label(),
        )

    def download_try(self) -> Track:
        track_obj, parent_obj, total_tracks_val = self._track_context()
        skipped_track = self._skip_existing_track_if_present(track_obj, parent_obj, total_tracks_val)
        if skipped_track is not None:
            return skipped_track

        report_track_initializing(
            track_obj=track_obj,
            preferences=self.__preferences,
            parent_obj=parent_obj,
            total_tracks=total_tracks_val,
        )

        retry_delay = getattr(self.__preferences, "initial_retry_delay", 10)
        retry_delay_increase = getattr(self.__preferences, "retry_delay_increase", 15)
        max_retries = getattr(self.__preferences, "max_retries", 5)
        conversion_retry_delay = self._download_track_with_retries(
            track_obj,
            parent_obj,
            total_tracks_val,
            retry_delay,
            retry_delay_increase,
            max_retries,
        )

        if self.__preferences.save_cover and self.__song_path:
            save_cover_image_for_track(
                self.__song_metadata_dict,
                self.__song_path,
                self.__preferences.save_cover,
            )
        self._convert_track_with_retry(
            track_obj,
            parent_obj,
            total_tracks_val,
            conversion_retry_delay,
        )

        if hasattr(self, "_EasyDw__c_track") and self.__c_track:
            self.__c_track.success = True
            process_and_tag_track(
                track=self.__c_track,
                metadata_dict=self.__song_metadata_dict,
                source_type="spotify",
            )

        self._report_track_done_status(track_obj, parent_obj, total_tracks_val)
        if hasattr(self, "_EasyDw__c_track") and self.__c_track and self.__c_track.success:
            unregister_active_download(self.__c_track.song_path)
        return self.__c_track

    def _episode_retry_settings(self) -> tuple[int, int, int]:
        return (
            getattr(self.__preferences, "initial_retry_delay", 30),
            getattr(self.__preferences, "retry_delay_increase", 30),
            getattr(self.__preferences, "max_retries", 5),
        )

    def _set_episode_error(self, message: str) -> None:
        if hasattr(self, "_EasyDw__c_episode") and self.__c_episode:
            self.__c_episode.success = False
            self.__c_episode.error_message = message

    def _episode_track_artist(self) -> tuple[str, str]:
        episode_title = self.__song_metadata.title
        artist_name = getattr(self.__preferences, "artist_separator", "; ").join(
            [a.name for a in self.__song_metadata.artists]
        )
        return episode_title, artist_name

    def _episode_download_error(self, stage: str, error: Exception) -> TrackNotFound:
        episode_title, artist_name = self._episode_track_artist()
        error_message = (
            f"Error during {stage} download for episode '{episode_title}' by '{artist_name}' "
            f"(URL: {self.__link}). Error: {str(error)}"
        )
        logger.error(error_message)
        self._set_episode_error(error_message)
        return TrackNotFound(message=error_message, url=self.__link)

    def _report_episode_retry(self, track_obj, retry_count: int, delay_seconds: int, error: Exception) -> None:
        status_obj = RetryingObject(
            ids=track_obj.ids,
            retry_count=retry_count,
            seconds_left=delay_seconds,
            error=str(error),
            convert_to=self.__convert_to,
            bitrate=self.__bitrate,
        )
        callback_obj = TrackCallbackObject(track=track_obj, status_info=status_obj)
        report_progress(reporter=Download_JOB.progress_reporter, callback_obj=callback_obj)

    def _raise_episode_retry_limit(self, max_retries: int, last_error: Exception) -> None:
        self._safe_remove_file(self.__song_path)
        episode_title, artist_name = self._episode_track_artist()
        final_error_msg = (
            f"Maximum retry limit reached for '{episode_title}' by '{artist_name}' "
            f"(local: {max_retries}, global: {GLOBAL_MAX_RETRIES}). Last error: {str(last_error)}"
        )
        self._set_episode_error(final_error_msg)
        raise TrackNotFound(message=final_error_msg, url=self.__link) from last_error

    def _load_episode_stream_with_retries(self):
        retry_delay, retry_delay_increase, max_retries = self._episode_retry_settings()
        retries = 0
        track_obj = self.__song_metadata
        episode_id = EpisodeId.from_base62(self.__ids)
        while True:
            try:
                self._ensure_session_ready()
                stream = Download_JOB.session.content_feeder().load_episode(
                    episode_id,
                    AudioQuality(self.__dw_quality),
                    False,
                    None,
                )
                return stream
            except Exception as error:
                global GLOBAL_RETRY_COUNT
                GLOBAL_RETRY_COUNT += 1
                retries += 1
                self._report_episode_retry(track_obj, retries, retry_delay, error)
                if retries >= max_retries or GLOBAL_RETRY_COUNT >= GLOBAL_MAX_RETRIES:
                    self._raise_episode_retry_limit(max_retries, error)
                time.sleep(retry_delay)
                retry_delay += retry_delay_increase

    def _write_episode_stream(self, c_stream, output_file, total_size: int) -> None:
        if not self._should_use_realtime_download():
            self._write_standard_stream(c_stream, output_file, total_size)
            return
        pacing_enabled, rate_limit = self._resolve_rate_limit(total_size)
        bytes_written = 0
        start_time = time.time()
        chunk_size = 4096
        while True:
            chunk = c_stream.read(chunk_size)
            if not chunk:
                break
            output_file.write(chunk)
            bytes_written += len(chunk)
            if pacing_enabled and rate_limit:
                expected_time = bytes_written / rate_limit
                elapsed_time = time.time() - start_time
                if expected_time > elapsed_time:
                    time.sleep(expected_time - elapsed_time)

    def _download_episode_file(self, stream) -> None:
        total_size = stream.input_stream.size
        c_stream = stream.input_stream.stream()
        os.makedirs(dirname(self.__song_path), exist_ok=True)
        register_active_download(self.__song_path)
        try:
            with open(self.__song_path, "wb") as output_file:
                self._write_episode_stream(c_stream, output_file, total_size)
        except Exception as stream_error:
            self._safe_remove_file(self.__song_path)
            raise self._episode_download_error("stream", stream_error) from stream_error
        finally:
            self._close_stream(c_stream)
            unregister_active_download(self.__song_path)

    def _raise_episode_conversion_error(self, conversion_error: Exception) -> None:
        episode_title = self.__song_metadata.title
        error_message = f"Audio conversion for episode '{episode_title}' failed. Original error: {str(conversion_error)}"
        status_obj = ErrorObject(
            ids=self.__song_metadata.ids,
            error=error_message,
            convert_to=self.__convert_to,
            bitrate=self.__bitrate,
        )
        callback_obj = TrackCallbackObject(track=self.__song_metadata, status_info=status_obj)
        report_progress(reporter=Download_JOB.progress_reporter, callback_obj=callback_obj)
        self._safe_remove_file(self.__song_path)
        unregister_active_download(self.__song_path)
        logger.error(error_message)
        self._set_episode_error(error_message)
        raise TrackNotFound(message=error_message, url=self.__link) from conversion_error

    def download_eps(self) -> Episode:
        if hasattr(self, "_EasyDw__c_episode") and self.__c_episode:
            self.__c_episode.success = False
        if isfile(self.__song_path) and check_track(self.__c_episode):
            answer = input(
                f"Episode \"{self.__song_path}\" already exists, do you want to redownload it?(y or n):"
            )
            if answer not in answers:
                return self.__c_episode

        stream = self._load_episode_stream_with_retries()
        self._download_episode_file(stream)
        try:
            self.__convert_audio()
        except Exception as conversion_error:
            self._raise_episode_conversion_error(conversion_error)

        if hasattr(self, "_EasyDw__c_episode") and self.__c_episode:
            self.__c_episode.success = True
            process_and_tag_episode(
                episode=self.__c_episode,
                metadata_dict=self.__song_metadata_dict,
                source_type="spotify",
            )
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

    @staticmethod
    def _best_album_image_bytes(album_obj):
        pic_url = (
            max(album_obj.images, key=lambda i: i.get('height', 0) * i.get('width', 0)).get('url')
            if album_obj.images
            else None
        )
        return request(pic_url).content if pic_url else None

    def _download_album_track(self, track_in_album, index: int, album_obj):
        c_preferences = deepcopy(self.__preferences)
        try:
            full_track_obj = tracking(
                track_in_album.ids.spotify,
                album_data_for_track=self.__preferences.json_data,
                market=self.__preferences.market,
            )
            if not full_track_obj:
                raise TrackNotFound(f"Could not fetch metadata for track ID {track_in_album.ids.spotify}")

            c_preferences.song_metadata = full_track_obj
            c_preferences.ids = full_track_obj.ids.spotify
            c_preferences.link = f"https://open.spotify.com/track/{c_preferences.ids}"
            c_preferences.track_number = index + 1
            c_preferences.pad_number_width = getattr(self.__preferences, 'pad_number_width', 'auto')
            return EASY_DW(c_preferences, parent='album').easy_dw()
        except Exception as e:
            song_tags = _track_object_to_dict(track_in_album) if isinstance(track_in_album, TrackObject) else {'music': UNKNOWN_TRACK_TITLE}
            failed_track = Track(tags=song_tags, song_path=None, file_format=None, quality=None, link=None, ids=track_in_album.ids)
            failed_track.success = False
            failed_track.error_message = str(e)
            logger.warning(
                f"Track '{song_tags.get('music')}' from album '{album_obj.title}' failed to download. Reason: {failed_track.error_message}"
            )
            return failed_track

    @staticmethod
    def _album_summary_groups(tracks: list):
        successful_tracks = []
        failed_tracks = []
        skipped_tracks = []
        for track in tracks:
            track_obj_for_cb = TrackObject(
                title=track.tags.get('music', UNKNOWN_TRACK_TITLE),
                artists=[ArtistTrackObject(name=artist.strip()) for artist in track.tags.get('artist', '').split(';')],
            )
            if getattr(track, 'was_skipped', False):
                skipped_tracks.append(track_obj_for_cb)
            elif track.success:
                successful_tracks.append(track_obj_for_cb)
            else:
                failed_tracks.append(
                    FailedTrackObject(
                        track=track_obj_for_cb,
                        reason=getattr(track, 'error_message', 'Unknown reason'),
                    )
                )
        return successful_tracks, skipped_tracks, failed_tracks

    def dw(self) -> Album:
        album_obj = self.__album_metadata
        report_album_initializing(album_obj)
        
        image_bytes = self._best_album_image_bytes(album_obj)
        
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
            track = self._download_album_track(track_in_album, a, album_obj)
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
            
        successful_tracks, skipped_tracks, failed_tracks = self._album_summary_groups(tracks)

        summary_obj = SummaryObject(
            successful_tracks=successful_tracks,
            skipped_tracks=skipped_tracks,
            failed_tracks=failed_tracks,
            total_successful=len(successful_tracks),
            total_skipped=len(skipped_tracks),
            total_failed=len(failed_tracks),
            service=Service.SPOTIFY
        )
        # Compute final quality/bitrate for album summary
        quality_val, bitrate_val = resolve_spotify_summary_media(
            quality_download=getattr(self.__preferences, 'quality_download', None),
            convert_to=getattr(self.__preferences, 'convert_to', None),
            bitrate=getattr(self.__preferences, 'bitrate', None),
        )
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

    def _build_playlist_callback_object(self, playlist_name: str, playlist_owner_name: str, playlist_id: str) -> PlaylistObject:
        playlist_tracks_for_cb = []
        if self.__playlist_tracks_json:
            for item in self.__playlist_tracks_json:
                track_data = item.get('track')
                if not track_data:
                    continue
                track_playlist_obj = json_to_track_playlist_object(track_data)
                if track_playlist_obj:
                    playlist_tracks_for_cb.append(track_playlist_obj)
        return PlaylistObject(
            title=playlist_name,
            owner=UserObject(name=playlist_owner_name, ids=IDs(spotify=self.__json_data.get('owner', {}).get('id'))),
            ids=IDs(spotify=playlist_id),
            images=self.__json_data.get('images', []),
            tracks=playlist_tracks_for_cb,
            description=self.__json_data.get('description'),
        )

    @staticmethod
    def _metadata_error_track(track_metadata: dict, playlist_name: str):
        track_title = track_metadata.get('name', UNKNOWN_TRACK_TITLE)
        track_ids = track_metadata.get('ids')
        error_message = track_metadata.get('error_message', 'Unknown error during metadata retrieval.')
        logger.warning(
            f"Skipping download for track '{track_title}' (ID: {track_ids}) from playlist '{playlist_name}' due to error: {error_message}"
        )
        track_tags = {'music': track_title, 'ids': track_ids}
        track = Track(tags=track_tags, song_path=None, file_format=None, quality=None, link=None, ids=track_ids)
        track.success = False
        track.error_message = error_message
        return track

    def _build_playlist_track_preferences(self, song_metadata, idx: int):
        c_preferences = deepcopy(self.__preferences)
        c_preferences.ids = song_metadata.ids.spotify
        c_preferences.song_metadata = song_metadata
        c_preferences.json_data = self.__json_data
        c_preferences.track_number = idx + 1
        c_preferences.link = f"https://open.spotify.com/track/{c_preferences.ids}" if c_preferences.ids else None
        c_preferences.pad_number_width = getattr(self.__preferences, 'pad_number_width', 'auto')
        return c_preferences

    def _download_playlist_track(self, song_metadata, idx: int, playlist_name: str):
        c_preferences = self._build_playlist_track_preferences(song_metadata, idx)
        easy_dw_instance = EASY_DW(c_preferences, parent='playlist')
        try:
            return easy_dw_instance.easy_dw()
        except Exception as e:
            track = easy_dw_instance.get_no_dw_track()
            if not isinstance(track, Track):
                track = Track(_track_object_to_dict(song_metadata), None, None, None, c_preferences.link, c_preferences.ids)
            track.success = False
            track.error_message = str(e)
            logger.warning(f"Failed to download track '{song_metadata.title}' from playlist '{playlist_name}'. Reason: {track.error_message}")
            return track

    @staticmethod
    def _playlist_summary_groups(tracks: list):
        successful_tracks_cb = []
        failed_tracks_cb = []
        skipped_tracks_cb = []
        for track in tracks:
            track_tags = track.tags
            track_obj_for_cb = TrackObject(
                title=track_tags.get('music', UNKNOWN_TRACK_TITLE),
                artists=[ArtistTrackObject(name=artist.strip()) for artist in track_tags.get('artist', '').split(';')],
            )
            if getattr(track, 'was_skipped', False):
                skipped_tracks_cb.append(track_obj_for_cb)
            elif track.success:
                successful_tracks_cb.append(track_obj_for_cb)
            else:
                failed_tracks_cb.append(
                    FailedTrackObject(
                        track=track_obj_for_cb,
                        reason=getattr(track, 'error_message', 'Unknown reason'),
                    )
                )
        return successful_tracks_cb, skipped_tracks_cb, failed_tracks_cb

    def dw(self) -> Playlist:
        playlist_name = self.__json_data.get('name', 'unknown')
        playlist_owner_name = self.__json_data.get('owner', {}).get('display_name', 'Unknown Owner')
        playlist_id = self.__ids

        playlist_obj_for_cb = self._build_playlist_callback_object(playlist_name, playlist_owner_name, playlist_id)
        report_playlist_initializing(playlist_obj_for_cb)
        m3u_path = create_m3u_file(self.__output_dir, playlist_name)

        playlist = Playlist()
        tracks = playlist.tracks
        for idx, c_song_metadata in enumerate(self.__song_metadata_list):
            if isinstance(c_song_metadata, dict) and 'error_type' in c_song_metadata:
                track = self._metadata_error_track(c_song_metadata, playlist_name)
                tracks.append(track)
                continue

            track = self._download_playlist_track(c_song_metadata, idx, playlist_name)

            if track:
                tracks.append(track)
            if track and track.success and hasattr(track, 'song_path') and track.song_path:
                append_track_to_m3u(m3u_path, track)
        
        if self.__make_zip:
            playlist_title = self.__json_data['name']
            zip_name = f"{self.__output_dir}/{playlist_title} [playlist {self.__ids}]"
            create_zip(tracks, zip_name=zip_name)
            playlist.zip_path = zip_name
            
        successful_tracks_cb, skipped_tracks_cb, failed_tracks_cb = self._playlist_summary_groups(tracks)

        summary_obj = SummaryObject(
            successful_tracks=successful_tracks_cb,
            skipped_tracks=skipped_tracks_cb,
            failed_tracks=failed_tracks_cb,
            total_successful=len(successful_tracks_cb),
            total_skipped=len(skipped_tracks_cb),
            total_failed=len(failed_tracks_cb),
            service=Service.SPOTIFY
        )
        # Compute final quality/bitrate for playlist summary
        quality_val, bitrate_val = resolve_spotify_summary_media(
            quality_download=getattr(self.__preferences, 'quality_download', None),
            convert_to=getattr(self.__preferences, 'convert_to', None),
            bitrate=getattr(self.__preferences, 'bitrate', None),
        )
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
        episode_obj = self.__preferences.song_metadata # This is a TrackObject
        
        # Build status object
        status_obj_init = InitializingObject(ids=episode_obj.ids)
        callback_obj_init = TrackCallbackObject(track=episode_obj, status_info=status_obj_init)
        report_progress(
            reporter=Download_JOB.progress_reporter,
            callback_obj=callback_obj_init
        )
        
        episode = EASY_DW(self.__preferences).download_eps()
        
        # Build status object
        status_obj_done = DoneObject(ids=episode_obj.ids)
        callback_obj_done = TrackCallbackObject(track=episode_obj, status_info=status_obj_done)
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
