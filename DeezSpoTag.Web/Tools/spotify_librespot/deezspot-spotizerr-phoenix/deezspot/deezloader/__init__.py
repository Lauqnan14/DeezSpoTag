#!/usr/bin/python3
import os
import json
import logging
import re
from deezspot.deezloader.dee_api import API
from deezspot.easy_spoty import Spo
from deezspot.deezloader.deegw_api import API_GW
from deezspot.deezloader.deezer_settings import stock_quality
from deezspot.models.download import (
    Track,
    Album,
    Playlist,
    Preferences,
    Smart,
    Episode,
)
from deezspot.deezloader.__download__ import (
    DW_TRACK,
    DW_ALBUM,
    DW_PLAYLIST,
    DW_EPISODE,
    Download_JOB,
)
from deezspot.exceptions import (
    InvalidLink,
    TrackNotFound,
    NoDataApi,
    AlbumNotFound,
    MarketAvailabilityError,
)
from deezspot.libutils.utils import (
    create_zip,
    get_ids,
    link_is_valid,
    what_kind,
    sanitize_name
)
from deezspot.libutils.others_settings import (
    stock_output,
    stock_recursive_quality,
    stock_recursive_download,
    stock_not_interface,
    stock_zip,
    stock_save_cover,
    stock_market
)
from deezspot.libutils.logging_utils import ProgressReporter, logger, report_progress
import requests
from librespot.core import Session

from deezspot.models.callback.callbacks import (
    TrackCallbackObject,
    AlbumCallbackObject,
    PlaylistCallbackObject,
    ErrorObject,
    SummaryObject,
    FailedTrackObject,
    InitializingObject,
    DoneObject,
)
from deezspot.models.callback.track import TrackObject as trackCbObject, ArtistTrackObject
from deezspot.models.callback.album import AlbumObject as albumCbObject
from deezspot.models.callback.playlist import PlaylistObject as playlistCbObject
from deezspot.models.callback.common import IDs
from deezspot.models.callback.user import UserObject
from rapidfuzz import fuzz

def _sim(a: str, b: str) -> float:
    a = (a or '').strip().lower()
    b = (b or '').strip().lower()
    if not a or not b:
        return 0.0
    return fuzz.partial_ratio(a, b) / 100

# Clean for searching on Deezer
def _remove_parentheses(string: str) -> str:
    # remove () and [] and {}, as well as anything inside
    return re.sub(r'\{[^)]*\}', '', re.sub(r'\[[^)]*\]', '', re.sub(r'\([^)]*\)', '', string)))

API()

# Create a logger for the deezspot library
logger = logging.getLogger('deezspot')

class DeeLogin:
    _TRACK_OPTION_ORDER = [
        "output_dir", "quality_download", "recursive_quality", "recursive_download",
        "not_interface", "custom_dir_format", "custom_track_format", "pad_tracks",
        "initial_retry_delay", "retry_delay_increase", "max_retries", "convert_to",
        "bitrate", "save_cover", "market", "playlist_context", "artist_separator",
        "spotify_metadata", "pad_number_width",
    ]
    _TRACK_OPTION_DEFAULTS = {
        "output_dir": stock_output,
        "quality_download": stock_quality,
        "recursive_quality": stock_recursive_quality,
        "recursive_download": stock_recursive_download,
        "not_interface": stock_not_interface,
        "custom_dir_format": None,
        "custom_track_format": None,
        "pad_tracks": True,
        "initial_retry_delay": 30,
        "retry_delay_increase": 30,
        "max_retries": 5,
        "convert_to": None,
        "bitrate": None,
        "save_cover": stock_save_cover,
        "market": stock_market,
        "playlist_context": None,
        "artist_separator": "; ",
        "spotify_metadata": False,
        "pad_number_width": "auto",
    }
    _ALBUM_OPTION_ORDER = [
        "output_dir", "quality_download", "recursive_quality", "recursive_download",
        "not_interface", "make_zip", "custom_dir_format", "custom_track_format",
        "pad_tracks", "initial_retry_delay", "retry_delay_increase", "max_retries",
        "convert_to", "bitrate", "save_cover", "market", "playlist_context",
        "artist_separator", "spotify_metadata", "spotify_album_obj", "pad_number_width",
    ]
    _ALBUM_OPTION_DEFAULTS = {
        "output_dir": stock_output,
        "quality_download": stock_quality,
        "recursive_quality": stock_recursive_quality,
        "recursive_download": stock_recursive_download,
        "not_interface": stock_not_interface,
        "make_zip": stock_zip,
        "custom_dir_format": None,
        "custom_track_format": None,
        "pad_tracks": True,
        "initial_retry_delay": 30,
        "retry_delay_increase": 30,
        "max_retries": 5,
        "convert_to": None,
        "bitrate": None,
        "save_cover": stock_save_cover,
        "market": stock_market,
        "playlist_context": None,
        "artist_separator": "; ",
        "spotify_metadata": False,
        "spotify_album_obj": None,
        "pad_number_width": "auto",
    }
    _PLAYLIST_OPTION_ORDER = [
        "output_dir", "quality_download", "recursive_quality", "recursive_download",
        "not_interface", "make_zip", "custom_dir_format", "custom_track_format",
        "pad_tracks", "initial_retry_delay", "retry_delay_increase", "max_retries",
        "convert_to", "bitrate", "save_cover", "market", "artist_separator",
        "pad_number_width",
    ]
    _PLAYLIST_OPTION_DEFAULTS = {
        "output_dir": stock_output,
        "quality_download": stock_quality,
        "recursive_quality": stock_recursive_quality,
        "recursive_download": stock_recursive_download,
        "not_interface": stock_not_interface,
        "make_zip": stock_zip,
        "custom_dir_format": None,
        "custom_track_format": None,
        "pad_tracks": True,
        "initial_retry_delay": 30,
        "retry_delay_increase": 30,
        "max_retries": 5,
        "convert_to": None,
        "bitrate": None,
        "save_cover": stock_save_cover,
        "market": stock_market,
        "artist_separator": "; ",
        "pad_number_width": "auto",
    }
    _SPOTIFY_PLAYLIST_OPTION_ORDER = [
        "output_dir", "quality_download", "recursive_quality", "recursive_download",
        "not_interface", "make_zip", "custom_dir_format", "custom_track_format",
        "pad_tracks", "initial_retry_delay", "retry_delay_increase", "max_retries",
        "convert_to", "bitrate", "save_cover", "market", "artist_separator",
        "spotify_metadata", "pad_number_width",
    ]
    _SPOTIFY_PLAYLIST_OPTION_DEFAULTS = {
        **_PLAYLIST_OPTION_DEFAULTS,
        "spotify_metadata": False,
    }
    _SMART_OPTION_ORDER = [
        "output_dir", "quality_download", "recursive_quality", "recursive_download",
        "not_interface", "make_zip", "custom_dir_format", "custom_track_format",
        "pad_tracks", "initial_retry_delay", "retry_delay_increase", "max_retries",
        "convert_to", "bitrate", "save_cover", "market", "artist_separator",
    ]
    _SMART_OPTION_DEFAULTS = {
        "output_dir": stock_output,
        "quality_download": stock_quality,
        "recursive_quality": stock_recursive_quality,
        "recursive_download": stock_recursive_download,
        "not_interface": stock_not_interface,
        "make_zip": stock_zip,
        "custom_dir_format": None,
        "custom_track_format": None,
        "pad_tracks": True,
        "initial_retry_delay": 30,
        "retry_delay_increase": 30,
        "max_retries": 5,
        "convert_to": None,
        "bitrate": None,
        "save_cover": stock_save_cover,
        "market": stock_market,
        "artist_separator": "; ",
    }

    def __init__(
        self,
        arl=None,
        email=None,
        password=None,
        spotify_client_id=None,
        spotify_client_secret=None,
        spotify_credentials_path=None,
        progress_callback=None,
        silent=False
    ) -> None:

        # Store Spotify credentials
        self.spotify_client_id = spotify_client_id
        self.spotify_client_secret = spotify_client_secret
        # Optional path to Spotify credentials.json (explicit param > env override > CWD default)
        self.spotify_credentials_path = (
            spotify_credentials_path
            or os.environ.get("SPOTIFY_CREDENTIALS_PATH")
            or os.path.join(os.getcwd(), "credentials.json")
        )
        
        # Initialize Spotify API if credentials are provided
        if spotify_client_id and spotify_client_secret:
            Spo.__init__(client_id=spotify_client_id, client_secret=spotify_client_secret)

        # Initialize Deezer API
        if arl:
            self.__gw_api = API_GW(arl=arl)
        else:
            self.__gw_api = API_GW(
                email=email,
                password=password
            )
            
        # Reference to the Spotify search functionality
        self.__spo = Spo
        
        # Configure progress reporting
        self.progress_reporter = ProgressReporter(callback=progress_callback, silent=silent)
        
        # Set the progress reporter for Download_JOB
        Download_JOB.set_progress_reporter(self.progress_reporter)

    def _ensure_spotify_session(self) -> None:
        """Ensure Spo has an attached librespot Session. Used only by spo->dee flows."""
        has_session = False
        try:
            # Check if Spo already has a session (accessing private attr is ok internally)
            has_session = getattr(Spo, f"_{Spo.__name__}__session", None) is not None
        except Exception as exc:
            logger.debug("Unable to inspect current Spotify session state: %s", exc)

        if has_session:
            return

        cred_path = self.spotify_credentials_path
        if not os.path.isfile(cred_path):
            raise FileNotFoundError(
                f"Spotify session not initialized. Missing credentials.json at '{cred_path}'. "
                "Set SPOTIFY_CREDENTIALS_PATH or place credentials.json in the working directory."
            )
        builder = Session.Builder()
        builder.conf.stored_credentials_file = cred_path
        session = builder.stored_file().create()
        Spo.set_session(session)

    @staticmethod
    def _merge_options(
        *,
        legacy_args: tuple,
        kwargs: dict,
        param_order: list[str],
        defaults: dict
    ) -> dict:
        """Merge legacy positional/keyword options while preserving old call semantics."""
        if len(legacy_args) > len(param_order):
            raise TypeError(
                f"Expected at most {len(param_order)} positional option(s), got {len(legacy_args)}."
            )

        merged = dict(defaults)
        assigned_from_args = set()
        for key, value in zip(param_order, legacy_args):
            merged[key] = value
            assigned_from_args.add(key)

        unexpected = [key for key in kwargs if key not in defaults]
        if unexpected:
            raise TypeError(f"Unexpected option(s): {', '.join(sorted(unexpected))}")

        duplicates = [key for key in kwargs if key in assigned_from_args]
        if duplicates:
            raise TypeError(f"Multiple values for option(s): {', '.join(sorted(duplicates))}")

        merged.update(kwargs)
        return merged

    def _resolve_options(
        self,
        *,
        legacy_args: tuple,
        kwargs: dict,
        param_order: list[str],
        defaults: dict,
        bool_keys: tuple[str, ...] = (),
    ) -> dict:
        options = self._merge_options(
            legacy_args=legacy_args,
            kwargs=kwargs,
            param_order=param_order,
            defaults=defaults,
        )
        for key in bool_keys:
            options[key] = bool(options.get(key))
        return options

    def _resolve_track_options(self, legacy_args: tuple, kwargs: dict) -> dict:
        return self._resolve_options(
            legacy_args=legacy_args,
            kwargs=kwargs,
            param_order=self._TRACK_OPTION_ORDER,
            defaults=self._TRACK_OPTION_DEFAULTS,
            bool_keys=("spotify_metadata",),
        )

    def _resolve_album_options(self, legacy_args: tuple, kwargs: dict) -> dict:
        return self._resolve_options(
            legacy_args=legacy_args,
            kwargs=kwargs,
            param_order=self._ALBUM_OPTION_ORDER,
            defaults=self._ALBUM_OPTION_DEFAULTS,
            bool_keys=("spotify_metadata",),
        )

    def _resolve_playlist_options(self, legacy_args: tuple, kwargs: dict) -> dict:
        return self._resolve_options(
            legacy_args=legacy_args,
            kwargs=kwargs,
            param_order=self._PLAYLIST_OPTION_ORDER,
            defaults=self._PLAYLIST_OPTION_DEFAULTS,
        )

    def _resolve_spotify_playlist_options(self, legacy_args: tuple, kwargs: dict) -> dict:
        return self._resolve_options(
            legacy_args=legacy_args,
            kwargs=kwargs,
            param_order=self._SPOTIFY_PLAYLIST_OPTION_ORDER,
            defaults=self._SPOTIFY_PLAYLIST_OPTION_DEFAULTS,
            bool_keys=("spotify_metadata",),
        )

    def _resolve_smart_options(self, legacy_args: tuple, kwargs: dict) -> dict:
        return self._resolve_options(
            legacy_args=legacy_args,
            kwargs=kwargs,
            param_order=self._SMART_OPTION_ORDER,
            defaults=self._SMART_OPTION_DEFAULTS,
        )

    def _report_track_download_error(self, error: Exception, current_ids) -> None:
        error_status = ErrorObject(ids=IDs(deezer=current_ids), error=str(error))
        summary = SummaryObject(
            failed_tracks=[FailedTrackObject(track=trackCbObject(title=f"Track ID {current_ids}"), reason=str(error))],
            total_failed=1
        )
        error_status.summary = summary
        callback_obj = TrackCallbackObject(
            track=trackCbObject(title=f"Track ID {current_ids}", ids=IDs(deezer=current_ids)),
            status_info=error_status
        )
        report_progress(reporter=self.progress_reporter, callback_obj=callback_obj)

    def _get_track_with_fallback(self, track_id, link_track):
        try:
            return API.get_track(track_id), track_id
        except (NoDataApi, MarketAvailabilityError) as error:
            infos = self.__gw_api.get_song_data(track_id)
            if "FALLBACK" not in infos:
                self._report_track_download_error(error, track_id)
                raise TrackNotFound(link_track) from error

            fallback_id = infos['FALLBACK']['SNG_ID']
            try:
                track_obj = API.get_track(fallback_id)
                if not track_obj or not track_obj.available:
                    raise MarketAvailabilityError(f"Fallback track {fallback_id} not available.")
                return track_obj, fallback_id
            except (NoDataApi, MarketAvailabilityError) as fallback_error:
                self._report_track_download_error(fallback_error, fallback_id)
                raise TrackNotFound(url=link_track, message=str(fallback_error)) from fallback_error

    @staticmethod
    def _track_title_match(source_title: str, candidate: dict) -> float:
        return max(
            _sim(_remove_parentheses(source_title), _remove_parentheses(candidate.get('title', ''))),
            _sim(_remove_parentheses(source_title), _remove_parentheses(candidate.get('title_short', '')))
        )

    def _find_deezer_track_via_isrc(
        self,
        *,
        spo_isrc: str,
        spo_title: str,
        spo_album_title: str,
        spo_tracknum: int,
    ) -> str | None:
        if not spo_isrc:
            return None

        try:
            dz = API.get_track_json(f"isrc:{spo_isrc}")
        except Exception:
            return None

        if not dz or not dz.get('id'):
            return None

        tn = (dz.get('track_position') or dz.get('track_number') or 0)
        title_match = self._track_title_match(spo_title, dz)
        album_match = _sim(spo_album_title, (dz.get('album') or {}).get('title', ''))
        t_isrc = (dz.get('isrc') or '').upper()

        if t_isrc and t_isrc == spo_isrc and title_match >= 0.90 and album_match >= 0.90 and tn == spo_tracknum:
            return str(dz.get('id'))
        return None

    def _find_deezer_track_via_search(
        self,
        *,
        spo_title: str,
        spo_main_artist: str,
        spo_album_title: str,
        spo_tracknum: int,
        spo_isrc: str,
    ) -> str | None:
        query = f'"track:\'{spo_title}\' artist:\'{spo_main_artist}\' album:\'{spo_album_title}\'"'
        try:
            candidates = API.search_tracks_raw(query, limit=5)
        except Exception:
            return None

        for cand in candidates:
            if self._track_title_match(spo_title, cand) < 0.90:
                continue
            c_id = cand.get('id')
            if not c_id:
                continue
            dzc = None
            try:
                dzc = API.get_track_json(str(c_id))
            except Exception as exc:
                logger.debug("Could not fetch Deezer track candidate %s: %s", c_id, exc)
            if not isinstance(dzc, dict):
                continue

            tn = (dzc.get('track_position') or dzc.get('track_number') or 0)
            t_isrc = (dzc.get('isrc') or '').upper()
            if tn == spo_tracknum and spo_isrc and t_isrc == spo_isrc:
                return str(c_id)
        return None

    def _find_deezer_album_via_upc(self, *, spo_upc: str, spo_album_title: str) -> str | None:
        if not spo_upc:
            return None

        try:
            dz_album = API.get_album_json(f"upc:{spo_upc}")
        except Exception:
            return None

        if dz_album.get('id') and _sim(spo_album_title, dz_album.get('title', '')) >= 0.90:
            return str(dz_album.get('id'))
        return None

    def _find_deezer_album_via_search(
        self,
        *,
        spo_album_title: str,
        spo_main_artist: str,
        spo_upc: str,
    ) -> str | None:
        query = f'"{spo_album_title}" {spo_main_artist}'.strip()
        try:
            candidates = API.search_albums_raw(query, limit=5)
        except Exception:
            return None

        for cand in candidates:
            if _sim(spo_album_title, cand.get('title', '')) < 0.90:
                continue
            c_id = cand.get('id')
            if not c_id:
                continue
            dzc = None
            try:
                dzc = API.get_album_json(str(c_id))
            except Exception as exc:
                logger.debug("Could not fetch Deezer album candidate %s: %s", c_id, exc)
            if not isinstance(dzc, dict):
                continue
            upc = str(dzc.get('upc') or '').strip().lstrip('0')
            if spo_upc and upc and spo_upc != upc:
                continue
            return str(c_id)
        return None

    def download_trackdee(self, link_track, *args, **kwargs) -> Track:
        options = self._resolve_track_options(args, kwargs)
        output_dir = options["output_dir"]
        quality_download = options["quality_download"]
        recursive_quality = options["recursive_quality"]
        recursive_download = options["recursive_download"]
        not_interface = options["not_interface"]
        custom_dir_format = options["custom_dir_format"]
        custom_track_format = options["custom_track_format"]
        pad_tracks = options["pad_tracks"]
        initial_retry_delay = options["initial_retry_delay"]
        retry_delay_increase = options["retry_delay_increase"]
        max_retries = options["max_retries"]
        convert_to = options["convert_to"]
        bitrate = options["bitrate"]
        save_cover = options["save_cover"]
        market = options["market"]
        playlist_context = options["playlist_context"]
        artist_separator = options["artist_separator"]
        spotify_metadata = options["spotify_metadata"]
        pad_number_width = options["pad_number_width"]

        link_is_valid(link_track)
        ids = get_ids(link_track)
        track_obj, ids = self._get_track_with_fallback(ids, link_track)
        
        if not track_obj:
            e = TrackNotFound(f"Could not retrieve track metadata for {link_track}")
            self._report_track_download_error(e, ids)
            raise e

        # If requested and provided via context, override with Spotify metadata for tagging
        if spotify_metadata and playlist_context and playlist_context.get('spotify_track_obj'):
            track_obj_for_tagging = playlist_context.get('spotify_track_obj')
        else:
            track_obj_for_tagging = track_obj

        # Set up download preferences
        preferences = Preferences()
        preferences.link = link_track
        preferences.song_metadata = track_obj_for_tagging  # Use selected track object (Spotify or Deezer) for tagging
        preferences.quality_download = quality_download
        preferences.output_dir = output_dir
        preferences.ids = ids
        preferences.recursive_quality = recursive_quality
        preferences.recursive_download = recursive_download
        preferences.not_interface = not_interface
        preferences.custom_dir_format = custom_dir_format
        preferences.custom_track_format = custom_track_format
        preferences.pad_tracks = pad_tracks
        preferences.initial_retry_delay = initial_retry_delay
        preferences.retry_delay_increase = retry_delay_increase
        preferences.max_retries = max_retries
        preferences.convert_to = convert_to
        preferences.bitrate = bitrate
        preferences.save_cover = save_cover
        preferences.market = market
        preferences.artist_separator = artist_separator
        preferences.spotify_metadata = bool(spotify_metadata)
        preferences.spotify_track_obj = playlist_context.get('spotify_track_obj') if (playlist_context and playlist_context.get('spotify_track_obj')) else None
        preferences.pad_number_width = pad_number_width

        if playlist_context:
            preferences.json_data = playlist_context.get('json_data')
            preferences.track_number = playlist_context.get('track_number')
            preferences.total_tracks = playlist_context.get('total_tracks')
            preferences.spotify_url = playlist_context.get('spotify_url')

        try:
            parent = 'playlist' if (playlist_context and playlist_context.get('json_data')) else None
            track = DW_TRACK(preferences, parent=parent).dw()
            return track
        except Exception as e:
            logger.error(f"Failed to download track: {str(e)}")
            self._report_track_download_error(e, ids)
            raise e

    def download_albumdee(self, link_album, *args, **kwargs) -> Album:
        options = self._resolve_album_options(args, kwargs)
        output_dir = options["output_dir"]
        quality_download = options["quality_download"]
        recursive_quality = options["recursive_quality"]
        recursive_download = options["recursive_download"]
        not_interface = options["not_interface"]
        make_zip = options["make_zip"]
        custom_dir_format = options["custom_dir_format"]
        custom_track_format = options["custom_track_format"]
        pad_tracks = options["pad_tracks"]
        initial_retry_delay = options["initial_retry_delay"]
        retry_delay_increase = options["retry_delay_increase"]
        max_retries = options["max_retries"]
        convert_to = options["convert_to"]
        bitrate = options["bitrate"]
        save_cover = options["save_cover"]
        market = options["market"]
        playlist_context = options["playlist_context"]
        artist_separator = options["artist_separator"]
        spotify_metadata = options["spotify_metadata"]
        spotify_album_obj = options["spotify_album_obj"]
        pad_number_width = options["pad_number_width"]

        link_is_valid(link_album)
        ids = get_ids(link_album)

        def report_error(e, current_ids, _url):
            error_status = ErrorObject(ids=IDs(deezer=current_ids), error=str(e))
            callback_obj = AlbumCallbackObject(
                album=albumCbObject(title=f"Album ID {current_ids}", ids=IDs(deezer=current_ids)),
                status_info=error_status
            )
            report_progress(reporter=self.progress_reporter, callback_obj=callback_obj)

        try:
            # Get standardized album object
            album_obj = API.get_album(ids)
            if not album_obj:
                e = AlbumNotFound(f"Could not retrieve album metadata for {link_album}")
                report_error(e, ids, link_album)
                raise e
        except NoDataApi as e:
            report_error(e, ids, link_album)
            raise AlbumNotFound(link_album) from e

        # Set up download preferences
        preferences = Preferences()
        preferences.link = link_album
        preferences.song_metadata = album_obj  # Using the standardized album object
        preferences.quality_download = quality_download
        preferences.output_dir = output_dir
        preferences.ids = ids
        preferences.json_data = album_obj  # Pass the complete album object
        preferences.recursive_quality = recursive_quality
        preferences.recursive_download = recursive_download
        preferences.not_interface = not_interface
        preferences.make_zip = make_zip
        preferences.custom_dir_format = custom_dir_format
        preferences.custom_track_format = custom_track_format
        preferences.pad_tracks = pad_tracks
        preferences.initial_retry_delay = initial_retry_delay
        preferences.retry_delay_increase = retry_delay_increase
        preferences.max_retries = max_retries
        preferences.convert_to = convert_to
        preferences.bitrate = bitrate
        preferences.save_cover = save_cover
        preferences.market = market
        preferences.artist_separator = artist_separator
        preferences.spotify_metadata = bool(spotify_metadata)
        preferences.spotify_album_obj = spotify_album_obj
        preferences.pad_number_width = pad_number_width

        if playlist_context:
            preferences.json_data = playlist_context['json_data']
            preferences.track_number = playlist_context['track_number']
            preferences.total_tracks = playlist_context['total_tracks']
            preferences.spotify_url = playlist_context['spotify_url']

        try:
            album = DW_ALBUM(preferences).dw()
            return album
        except Exception as e:
            logger.error(f"Failed to download album: {str(e)}")
            report_error(e, ids, link_album)
            raise e

    def download_playlistdee(self, link_playlist, *args, **kwargs) -> Playlist:
        options = self._resolve_playlist_options(args, kwargs)
        output_dir = options["output_dir"]
        quality_download = options["quality_download"]
        recursive_quality = options["recursive_quality"]
        recursive_download = options["recursive_download"]
        not_interface = options["not_interface"]
        make_zip = options["make_zip"]
        custom_dir_format = options["custom_dir_format"]
        custom_track_format = options["custom_track_format"]
        pad_tracks = options["pad_tracks"]
        initial_retry_delay = options["initial_retry_delay"]
        retry_delay_increase = options["retry_delay_increase"]
        max_retries = options["max_retries"]
        convert_to = options["convert_to"]
        bitrate = options["bitrate"]
        save_cover = options["save_cover"]
        market = options["market"]
        artist_separator = options["artist_separator"]
        pad_number_width = options["pad_number_width"]

        link_is_valid(link_playlist)
        ids = get_ids(link_playlist)

        playlist_obj = API.get_playlist(ids)
        if not playlist_obj:
            raise NoDataApi(f"Playlist {ids} not found.")

        # This part of fetching metadata track by track is now handled in __download__.py
        # The logic here is simplified to pass the full playlist object.

        preferences = Preferences()
        preferences.link = link_playlist
        # preferences.song_metadata is not needed here, DW_PLAYLIST will use json_data
        preferences.quality_download = quality_download
        preferences.output_dir = output_dir
        preferences.ids = ids
        preferences.json_data = playlist_obj
        preferences.recursive_quality = recursive_quality
        preferences.recursive_download = recursive_download
        preferences.not_interface = not_interface
        preferences.make_zip = make_zip
        preferences.custom_dir_format = custom_dir_format
        preferences.custom_track_format = custom_track_format
        preferences.pad_tracks = pad_tracks
        preferences.initial_retry_delay = initial_retry_delay
        preferences.retry_delay_increase = retry_delay_increase
        preferences.max_retries = max_retries
        preferences.convert_to = convert_to
        preferences.bitrate = bitrate
        preferences.save_cover = save_cover
        preferences.market = market
        preferences.artist_separator = artist_separator
        preferences.pad_number_width = pad_number_width

        playlist = DW_PLAYLIST(preferences).dw()

        return playlist

    def download_artisttopdee(self, link_artist, *args, **kwargs) -> list[Track]:
        options = self._merge_options(
            legacy_args=args,
            kwargs=kwargs,
            param_order=[
                "output_dir", "quality_download", "recursive_quality", "recursive_download",
                "not_interface", "custom_dir_format", "custom_track_format", "pad_tracks",
                "convert_to", "bitrate", "save_cover", "market", "pad_number_width"
            ],
            defaults={
                "output_dir": stock_output,
                "quality_download": stock_quality,
                "recursive_quality": stock_recursive_quality,
                "recursive_download": stock_recursive_download,
                "not_interface": stock_not_interface,
                "custom_dir_format": None,
                "custom_track_format": None,
                "pad_tracks": True,
                "convert_to": None,
                "bitrate": None,
                "save_cover": stock_save_cover,
                "market": stock_market,
                "pad_number_width": "auto",
            },
        )
        output_dir = options["output_dir"]
        quality_download = options["quality_download"]
        recursive_quality = options["recursive_quality"]
        recursive_download = options["recursive_download"]
        not_interface = options["not_interface"]
        custom_dir_format = options["custom_dir_format"]
        custom_track_format = options["custom_track_format"]
        pad_tracks = options["pad_tracks"]
        convert_to = options["convert_to"]
        bitrate = options["bitrate"]
        save_cover = options["save_cover"]
        market = options["market"]
        pad_number_width = options["pad_number_width"]

        link_is_valid(link_artist)
        ids = get_ids(link_artist)

        # Assuming get_artist_top_tracks returns a list of track-like dicts with a 'link'
        top_tracks_json = API.get_artist_top_tracks(ids)['data']

        names = [
            self.download_trackdee(
                track['link'], output_dir,
                quality_download, recursive_quality,
                recursive_download, not_interface,
                custom_dir_format=custom_dir_format,
                custom_track_format=custom_track_format,
                pad_tracks=pad_tracks,
                convert_to=convert_to,
                bitrate=bitrate,
                save_cover=save_cover,
                market=market,
                pad_number_width=pad_number_width
            )
            for track in top_tracks_json
        ]

        return names

    def convert_spoty_to_dee_link_track(self, link_track):
        # Ensure Spotify session only when using spo->dee conversion
        self._ensure_spotify_session()
        link_is_valid(link_track)
        ids = get_ids(link_track)
        
        # Attempt via ISRC first
        track_json = Spo.get_track(ids)
        if not track_json:
            raise TrackNotFound(url=link_track, message="Spotify track metadata fetch failed.")
        external_ids = track_json.get('external_ids') or {}
        spo_isrc = (external_ids.get('isrc') or '').upper()
        spo_title = track_json.get('name', '')
        spo_album_title = (track_json.get('album') or {}).get('name', '')
        spo_tracknum = int(track_json.get('track_number') or 0)
        spo_artists = track_json.get('artists') or []
        spo_main_artist = (spo_artists[0].get('name') if spo_artists else '') or ''
        deezer_track_id = self._find_deezer_track_via_isrc(
            spo_isrc=spo_isrc,
            spo_title=spo_title,
            spo_album_title=spo_album_title,
            spo_tracknum=spo_tracknum,
        )
        if not deezer_track_id:
            deezer_track_id = self._find_deezer_track_via_search(
                spo_title=spo_title,
                spo_main_artist=spo_main_artist,
                spo_album_title=spo_album_title,
                spo_tracknum=spo_tracknum,
                spo_isrc=spo_isrc,
            )
        if deezer_track_id:
            return f"https://www.deezer.com/track/{deezer_track_id}"
        
        raise TrackNotFound(url=link_track, message=f"Failed to find Deezer equivalent for ISRC {spo_isrc} from Spotify track {link_track}")

    def convert_isrc_to_dee_link_track(self, isrc_code: str) -> str:
        if not isinstance(isrc_code, str) or not isrc_code:
            raise ValueError("ISRC code must be a non-empty string.")

        isrc_query = f"isrc:{isrc_code}"
        logger.debug(f"Attempting Deezer track search with ISRC query: {isrc_query}")

        try:
            track_obj = API.get_track(isrc_query)
        except NoDataApi:
            msg = f"⚠ The track with ISRC '{isrc_code}' can't be found on Deezer :( ⚠"
            logger.warning(msg)
            raise TrackNotFound(url=f"isrc:{isrc_code}", message=msg)
        
        if not track_obj or not track_obj.type or not track_obj.ids or not track_obj.ids.deezer:
            msg = f"⚠ Deezer API returned no link for ISRC '{isrc_code}' :( ⚠"
            logger.warning(msg)
            raise TrackNotFound(url=f"isrc:{isrc_code}", message=msg)

        track_link_dee = f"https://www.deezer.com/{track_obj.type}/{track_obj.ids.deezer}"
        logger.info(f"Successfully converted ISRC {isrc_code} to Deezer link: {track_link_dee}")
        return track_link_dee

    def convert_spoty_to_dee_link_album(self, link_album):
        # Ensure Spotify session only when using spo->dee conversion
        self._ensure_spotify_session()
        link_is_valid(link_album)
        ids = get_ids(link_album)
        
        spotify_album_data = Spo.get_album(ids)
        if not spotify_album_data:
            raise AlbumNotFound(f"Failed to fetch Spotify album metadata for {link_album}")
        
        spo_album_title = spotify_album_data.get('name', '')
        spo_artists = spotify_album_data.get('artists') or []
        spo_main_artist = (spo_artists[0].get('name') if spo_artists else '') or ''
        external_ids = spotify_album_data.get('external_ids') or {}
        spo_upc = str(external_ids.get('upc') or '').strip().lstrip('0')

        deezer_album_id = self._find_deezer_album_via_upc(spo_upc=spo_upc, spo_album_title=spo_album_title)
        if not deezer_album_id:
            deezer_album_id = self._find_deezer_album_via_search(
                spo_album_title=spo_album_title,
                spo_main_artist=spo_main_artist,
                spo_upc=spo_upc,
            )
        if deezer_album_id:
            return f"https://www.deezer.com/album/{deezer_album_id}"

        raise AlbumNotFound(f"Failed to convert Spotify album link {link_album} to a Deezer link after all attempts.")

    def download_trackspo(self, link_track, *args, **kwargs) -> Track:
        options = self._resolve_track_options(args, kwargs)
        playlist_context = options["playlist_context"]
        spotify_metadata = options["spotify_metadata"]

        link_dee = self.convert_spoty_to_dee_link_track(link_track)

        # If requested, prepare Spotify track object for tagging in preferences via playlist_context
        if spotify_metadata:
            try:
                from deezspot.spotloader.__spo_api__ import tracking as spo_tracking
                spo_ids = get_ids(link_track)
                spo_track_obj = spo_tracking(spo_ids)
                if spo_track_obj:
                    if playlist_context is None:
                        playlist_context = {}
                    playlist_context = dict(playlist_context)
                    playlist_context['spotify_track_obj'] = spo_track_obj
            except Exception as exc:
                logger.debug("Unable to fetch Spotify track metadata for tagging: %s", exc)

        dee_track_options = dict(options)
        dee_track_options["playlist_context"] = playlist_context
        return self.download_trackdee(link_dee, **dee_track_options)

    def download_albumspo(self, link_album, *args, **kwargs) -> Album:
        options = self._resolve_album_options(args, kwargs)
        spotify_metadata = options["spotify_metadata"]
        resolved_spotify_album_obj = options["spotify_album_obj"]

        link_dee = self.convert_spoty_to_dee_link_album(link_album)

        if spotify_metadata:
            # Only initialize Spotify session when we actually need Spotify metadata
            self._ensure_spotify_session()
            try:
                # Fetch full Spotify album with tracks once and convert to AlbumObject
                from deezspot.spotloader.__spo_api__ import tracking_album as spo_tracking_album
                spo_ids = get_ids(link_album)
                spotify_album_json = Spo.get_album(spo_ids)
                if spotify_album_json:
                    resolved_spotify_album_obj = spo_tracking_album(spotify_album_json)
            except Exception:
                resolved_spotify_album_obj = None

        dee_album_options = dict(options)
        dee_album_options["spotify_album_obj"] = resolved_spotify_album_obj
        return self.download_albumdee(link_dee, **dee_album_options)

    def _enrich_playlist_items_with_full_tracks(self, playlist_json: dict) -> None:
        try:
            items = playlist_json.get('tracks', {}).get('items', []) or []
            track_ids = [it.get('track', {}).get('id') for it in items if it.get('track') and it['track'].get('id')]
            full = Spo.get_tracks(track_ids) if track_ids else {'tracks': []}
            full_list = full.get('tracks') or []
            full_by_id = {t.get('id'): t for t in full_list if t and t.get('id')}
            playlist_json['tracks']['items'] = [
                {'track': full_by_id.get((it.get('track') or {}).get('id'))}
                if full_by_id.get((it.get('track') or {}).get('id'))
                else it
                for it in items
            ]
        except Exception:
            # If enrichment fails, continue with minimal ids
            return

    def _build_playlist_track_callback(self, track_info: dict):
        from deezspot.models.callback.playlist import (
            ArtistTrackPlaylistObject,
            AlbumTrackPlaylistObject,
            ArtistAlbumTrackPlaylistObject,
            TrackPlaylistObject
        )

        track_artists = [
            ArtistTrackPlaylistObject(name=artist['name'], ids=IDs(spotify=artist.get('id')))
            for artist in track_info.get('artists', [])
        ]

        album_info = track_info.get('album', {})
        album_images = [
            {"url": img.get('url'), "height": img.get('height'), "width": img.get('width')}
            for img in album_info.get('images', [])
        ]
        album_artists = [
            ArtistAlbumTrackPlaylistObject(name=artist.get('name'), ids=IDs(spotify=artist.get('id')))
            for artist in album_info.get('artists', [])
        ]

        release_date = album_info.get('release_date')
        release_parts = release_date.split('-') if release_date else []
        release_date_obj = {
            "year": int(release_parts[0]) if len(release_parts) > 0 else 0,
            "month": int(release_parts[1]) if len(release_parts) > 1 else 0,
            "day": int(release_parts[2]) if len(release_parts) > 2 else 0,
        }

        album_obj = AlbumTrackPlaylistObject(
            title=album_info.get('name', 'Unknown Album'),
            ids=IDs(spotify=album_info.get('id')),
            images=album_images,
            artists=album_artists,
            album_type=album_info.get('album_type', ''),
            release_date=release_date_obj,
            total_tracks=album_info.get('total_tracks', 0)
        )

        return TrackPlaylistObject(
            title=track_info.get('name', 'Unknown Track'),
            artists=track_artists,
            album=album_obj,
            duration_ms=track_info.get('duration_ms', 0),
            explicit=track_info.get('explicit', False),
            ids=IDs(
                spotify=track_info.get('id'),
                isrc=track_info.get('external_ids', {}).get('isrc')
            ),
            disc_number=track_info.get('disc_number', 1),
            track_number=track_info.get('track_number', 0)
        )

    def _build_playlist_callback_tracks(self, playlist_json: dict) -> list:
        tracks = []
        for item in playlist_json.get('tracks', {}).get('items', []):
            track_info = item.get('track')
            if not track_info:
                continue
            tracks.append(self._build_playlist_track_callback(track_info))
        return tracks

    @staticmethod
    def _playlist_track_for_callback(playlist_obj: playlistCbObject, index: int, fallback_title: str, fallback_artist: str):
        if index - 1 < len(playlist_obj.tracks):
            return playlist_obj.tracks[index - 1]
        return trackCbObject(title=fallback_title, artists=[ArtistTrackObject(name=fallback_artist)])

    def _process_spotify_playlist_item(
        self,
        *,
        item: dict,
        index: int,
        total_tracks: int,
        playlist_json: dict,
        playlist_obj: playlistCbObject,
        options: dict
    ):
        is_track = item.get('track')
        if not is_track:
            reason = "Playlist item was not a valid track object or is not available in your region."
            unknown_track = trackCbObject(title="Unknown Skipped Item", artists=[ArtistTrackObject(name="")])
            failed_track = Track(
                tags={'music': 'Unknown Skipped Item', 'artist': 'Unknown'},
                song_path=None, file_format=None, quality=None, link=None, ids=None
            )
            failed_track.success = False
            failed_track.error_message = reason
            return failed_track, 'failed', unknown_track, reason

        track_info = is_track
        track_name = track_info.get('name', 'Unknown Track')
        artist_name = track_info['artists'][0]['name'] if track_info.get('artists') else 'Unknown Artist'
        link_track = track_info.get('external_urls', {}).get('spotify')
        if not link_track:
            track_id = track_info.get('id')
            if track_id:
                link_track = f"https://open.spotify.com/track/{track_id}"

        if not link_track:
            logger.warning(f"The track \"{track_name}\" is not available on Spotify :(")
            return None, 'ignored', None, None

        callback_track = self._playlist_track_for_callback(playlist_obj, index, track_name, artist_name)

        try:
            playlist_ctx = {
                'json_data': playlist_json,
                'track_number': index,
                'total_tracks': total_tracks,
                'spotify_url': link_track
            }
            downloaded_track = self.download_trackspo(
                link_track,
                output_dir=options["output_dir"],
                quality_download=options["quality_download"],
                recursive_quality=options["recursive_quality"],
                recursive_download=options["recursive_download"],
                not_interface=options["not_interface"],
                custom_dir_format=options["custom_dir_format"],
                custom_track_format=options["custom_track_format"],
                pad_tracks=options["pad_tracks"],
                initial_retry_delay=options["initial_retry_delay"],
                retry_delay_increase=options["retry_delay_increase"],
                max_retries=options["max_retries"],
                convert_to=options["convert_to"],
                bitrate=options["bitrate"],
                save_cover=options["save_cover"],
                market=options["market"],
                playlist_context=playlist_ctx,
                artist_separator=options["artist_separator"],
                spotify_metadata=False,
                pad_number_width=options["pad_number_width"]
            )

            if getattr(downloaded_track, 'was_skipped', False):
                return downloaded_track, 'skipped', callback_track, None
            if downloaded_track.success:
                return downloaded_track, 'success', callback_track, None
            return downloaded_track, 'failed', callback_track, getattr(downloaded_track, 'error_message', 'Unknown reason')
        except Exception as error:
            logger.error(f"Track '{track_name}' in playlist '{playlist_obj.title}' failed: {error}")
            current_track_object = Track({'music': track_name, 'artist': artist_name}, None, None, None, link_track, None)
            current_track_object.success = False
            current_track_object.error_message = str(error)
            return current_track_object, 'failed', callback_track, str(error)

    @staticmethod
    def _append_playlist_status(
        *,
        status: str,
        callback_track,
        reason: str | None,
        successful_tracks_cb: list,
        skipped_tracks_cb: list,
        failed_tracks_cb: list
    ) -> None:
        if status == 'success':
            successful_tracks_cb.append(callback_track)
        elif status == 'skipped':
            skipped_tracks_cb.append(callback_track)
        elif status == 'failed' and callback_track is not None:
            failed_tracks_cb.append(FailedTrackObject(track=callback_track, reason=reason or 'Unknown reason'))

    @staticmethod
    def _warn_if_playlist_count_mismatch(playlist_title: str, total_from_spotify: int, processed_count: int) -> None:
        if total_from_spotify != processed_count:
            logger.warning(
                f"Playlist '{playlist_title}' metadata reports {total_from_spotify} tracks, "
                f"but only {processed_count} were processed. This might indicate that not all pages of tracks were retrieved from Spotify."
            )

    def download_playlistspo(self, link_playlist, *args, **kwargs) -> Playlist:
        options = self._resolve_spotify_playlist_options(args, kwargs)

        link_is_valid(link_playlist)
        ids = get_ids(link_playlist)

        # Ensure Spotify session for fetching playlist and tracks
        self._ensure_spotify_session()

        playlist_json = Spo.get_playlist(ids)
        # Ensure we keep the playlist ID for callbacks
        if 'id' not in playlist_json:
            playlist_json['id'] = ids

        self._enrich_playlist_items_with_full_tracks(playlist_json)
        playlist_tracks_for_callback = self._build_playlist_callback_tracks(playlist_json)
        
        playlist_obj = playlistCbObject(
            title=playlist_json['name'],
            owner=UserObject(name=playlist_json.get('owner', {}).get('display_name', 'Unknown Owner')),
            ids=IDs(spotify=playlist_json.get('id', ids)),
            tracks=playlist_tracks_for_callback  # Populate tracks array with track objects
        )

        status_obj_init = InitializingObject(ids=playlist_obj.ids)
        callback_obj_init = PlaylistCallbackObject(playlist=playlist_obj, status_info=status_obj_init)
        report_progress(reporter=self.progress_reporter, callback_obj=callback_obj_init)

        total_tracks = playlist_json['tracks']['total']
        playlist_tracks = playlist_json['tracks']['items']
        playlist = Playlist()
        tracks = playlist.tracks

        successful_tracks_cb = []
        failed_tracks_cb = []
        skipped_tracks_cb = []

        download_options = {
            "output_dir": options["output_dir"],
            "quality_download": options["quality_download"],
            "recursive_quality": options["recursive_quality"],
            "recursive_download": options["recursive_download"],
            "not_interface": options["not_interface"],
            "custom_dir_format": options["custom_dir_format"],
            "custom_track_format": options["custom_track_format"],
            "pad_tracks": options["pad_tracks"],
            "initial_retry_delay": options["initial_retry_delay"],
            "retry_delay_increase": options["retry_delay_increase"],
            "max_retries": options["max_retries"],
            "convert_to": options["convert_to"],
            "bitrate": options["bitrate"],
            "save_cover": options["save_cover"],
            "market": options["market"],
            "artist_separator": options["artist_separator"],
            "pad_number_width": options["pad_number_width"],
        }

        for index, item in enumerate(playlist_tracks, 1):
            downloaded_track, status, callback_track, reason = self._process_spotify_playlist_item(
                item=item,
                index=index,
                total_tracks=total_tracks,
                playlist_json=playlist_json,
                playlist_obj=playlist_obj,
                options=download_options
            )

            if downloaded_track is not None:
                tracks.append(downloaded_track)

            if status == 'ignored':
                continue
            self._append_playlist_status(
                status=status,
                callback_track=callback_track,
                reason=reason,
                successful_tracks_cb=successful_tracks_cb,
                skipped_tracks_cb=skipped_tracks_cb,
                failed_tracks_cb=failed_tracks_cb,
            )

        # Finalize summary and callbacks (existing logic continues below in file)...

        total_from_spotify = playlist_json['tracks']['total']
        processed_count = len(successful_tracks_cb) + len(skipped_tracks_cb) + len(failed_tracks_cb)
        self._warn_if_playlist_count_mismatch(playlist_obj.title, total_from_spotify, processed_count)

        from deezspot.libutils.write_m3u import write_tracks_to_m3u
        m3u_path = write_tracks_to_m3u(options["output_dir"], playlist_obj.title, tracks)

        summary_obj = SummaryObject(
            successful_tracks=successful_tracks_cb,
            skipped_tracks=skipped_tracks_cb,
            failed_tracks=failed_tracks_cb,
            total_successful=len(successful_tracks_cb),
            total_skipped=len(skipped_tracks_cb),
            total_failed=len(failed_tracks_cb)
        )
        # Include m3u path in summary and callback
        summary_obj.m3u_path = m3u_path
        status_obj_done = DoneObject(ids=playlist_obj.ids, summary=summary_obj)
        callback_obj_done = PlaylistCallbackObject(playlist=playlist_obj, status_info=status_obj_done)
        report_progress(reporter=self.progress_reporter, callback_obj=callback_obj_done)

        if options["make_zip"]:
            zip_name = f"{options['output_dir']}/playlist_{sanitize_name(playlist_obj.title)}.zip"
            create_zip(tracks, zip_name=zip_name)
            playlist.zip_path = zip_name

        return playlist

    def download_name(self, artist, song, *args, **kwargs) -> Track:
        options = self._merge_options(
            legacy_args=args,
            kwargs=kwargs,
            param_order=[
                "output_dir", "quality_download", "recursive_quality", "recursive_download",
                "not_interface", "custom_dir_format", "custom_track_format", "initial_retry_delay",
                "retry_delay_increase", "max_retries", "pad_tracks", "convert_to", "bitrate",
                "save_cover", "market", "artist_separator", "pad_number_width"
            ],
            defaults={
                "output_dir": stock_output,
                "quality_download": stock_quality,
                "recursive_quality": stock_recursive_quality,
                "recursive_download": stock_recursive_download,
                "not_interface": stock_not_interface,
                "custom_dir_format": None,
                "custom_track_format": None,
                "initial_retry_delay": 30,
                "retry_delay_increase": 30,
                "max_retries": 5,
                "pad_tracks": True,
                "convert_to": None,
                "bitrate": None,
                "save_cover": stock_save_cover,
                "market": stock_market,
                "artist_separator": "; ",
                "pad_number_width": "auto",
            },
        )
        output_dir = options["output_dir"]
        quality_download = options["quality_download"]
        recursive_quality = options["recursive_quality"]
        recursive_download = options["recursive_download"]
        not_interface = options["not_interface"]
        custom_dir_format = options["custom_dir_format"]
        custom_track_format = options["custom_track_format"]
        initial_retry_delay = options["initial_retry_delay"]
        retry_delay_increase = options["retry_delay_increase"]
        max_retries = options["max_retries"]
        pad_tracks = options["pad_tracks"]
        convert_to = options["convert_to"]
        bitrate = options["bitrate"]
        save_cover = options["save_cover"]
        market = options["market"]
        artist_separator = options["artist_separator"]
        pad_number_width = options["pad_number_width"]

        query = f"track:{song} artist:{artist}"
        search = self.__spo.search(query)
        
        items = search['tracks']['items']

        if len(items) == 0:
            msg = f"No result for {query} :("
            raise TrackNotFound(message=msg)

        link_track = items[0]['external_urls']['spotify']

        track = self.download_trackspo(
            link_track,
            output_dir=output_dir,
            quality_download=quality_download,
            recursive_quality=recursive_quality,
            recursive_download=recursive_download,
            not_interface=not_interface,
            custom_dir_format=custom_dir_format,
            custom_track_format=custom_track_format,
            pad_tracks=pad_tracks,
            initial_retry_delay=initial_retry_delay,
            retry_delay_increase=retry_delay_increase,
            max_retries=max_retries,
            convert_to=convert_to,
            bitrate=bitrate,
            save_cover=save_cover,
            market=market,
            artist_separator=artist_separator,
            pad_number_width=pad_number_width
        )

        return track

    def download_episode(self, link_episode, *args, **kwargs) -> Episode:
        options = self._merge_options(
            legacy_args=args,
            kwargs=kwargs,
            param_order=[
                "output_dir", "quality_download", "recursive_quality", "recursive_download",
                "not_interface", "custom_dir_format", "custom_track_format", "pad_tracks",
                "initial_retry_delay", "retry_delay_increase", "max_retries", "convert_to",
                "bitrate", "save_cover", "market", "artist_separator"
            ],
            defaults={
                "output_dir": stock_output,
                "quality_download": stock_quality,
                "recursive_quality": stock_recursive_quality,
                "recursive_download": stock_recursive_download,
                "not_interface": stock_not_interface,
                "custom_dir_format": None,
                "custom_track_format": None,
                "pad_tracks": True,
                "initial_retry_delay": 30,
                "retry_delay_increase": 30,
                "max_retries": 5,
                "convert_to": None,
                "bitrate": None,
                "save_cover": stock_save_cover,
                "market": stock_market,
                "artist_separator": "; ",
            },
        )
        output_dir = options["output_dir"]
        quality_download = options["quality_download"]
        recursive_quality = options["recursive_quality"]
        recursive_download = options["recursive_download"]
        not_interface = options["not_interface"]
        custom_dir_format = options["custom_dir_format"]
        custom_track_format = options["custom_track_format"]
        pad_tracks = options["pad_tracks"]
        initial_retry_delay = options["initial_retry_delay"]
        retry_delay_increase = options["retry_delay_increase"]
        max_retries = options["max_retries"]
        convert_to = options["convert_to"]
        bitrate = options["bitrate"]
        save_cover = options["save_cover"]
        market = options["market"]
        artist_separator = options["artist_separator"]
        
        logger.warning("Episode download logic is not fully refactored and might not work as expected with new reporting.")
        _ = (
            custom_dir_format,
            custom_track_format,
            pad_tracks,
            initial_retry_delay,
            retry_delay_increase,
        )
        link_is_valid(link_episode)
        ids = get_ids(link_episode)
        
        try:
            # This will likely fail as API.tracking is gone.
            episode_metadata = API.get_episode(ids)
        except (NoDataApi, MarketAvailabilityError) as e:
            raise TrackNotFound(url=link_episode, message=f"Episode not available: {e}") from e
        except Exception:
            # Fallback to GW API if public API fails for any reason
            infos = self.__gw_api.get_episode_data(ids)
            if not infos:
                raise TrackNotFound(f"Episode {ids} not found")
            episode_metadata = {
                'music': infos.get('EPISODE_TITLE', ''), 'artist': infos.get('SHOW_NAME', ''),
                'album': infos.get('SHOW_NAME', ''), 'date': infos.get('EPISODE_PUBLISHED_TIMESTAMP', '').split()[0],
                'genre': 'Podcast', 'explicit': infos.get('SHOW_IS_EXPLICIT', '2'),
                'disc': 1, 'track': 1, 'duration': int(infos.get('DURATION', 0)), 'isrc': None,
                'image': infos.get('EPISODE_IMAGE_MD5', '')
            }

        preferences = Preferences()
        preferences.link = link_episode
        preferences.song_metadata = episode_metadata
        preferences.quality_download = quality_download
        preferences.output_dir = output_dir
        preferences.ids = ids
        preferences.recursive_quality = recursive_quality
        preferences.recursive_download = recursive_download
        preferences.not_interface = not_interface
        preferences.max_retries = max_retries
        preferences.convert_to = convert_to
        preferences.bitrate = bitrate
        preferences.save_cover = save_cover
        preferences.is_episode = True
        preferences.market = market
        preferences.artist_separator = artist_separator

        episode = DW_EPISODE(preferences).dw()

        return episode
        
    def download_smart(self, link, *args, **kwargs) -> Smart:
        options = self._resolve_smart_options(args, kwargs)

        link_is_valid(link)
        link = what_kind(link)
        smart = Smart()

        if "spotify.com" in link:
            source = "spotify"
        elif "deezer.com" in link:
            source = "deezer"
        else:
            raise InvalidLink(link)

        smart.source = source
        
        # Smart download reporting can be enhanced later if needed
        # For now, the individual download functions will do the reporting.

        if "track/" in link:
            track_options = {
                key: options[key]
                for key in (
                    "output_dir",
                    "quality_download",
                    "recursive_quality",
                    "recursive_download",
                    "not_interface",
                    "custom_dir_format",
                    "custom_track_format",
                    "pad_tracks",
                    "initial_retry_delay",
                    "retry_delay_increase",
                    "max_retries",
                    "convert_to",
                    "bitrate",
                    "save_cover",
                    "market",
                    "artist_separator",
                )
            }
            func = self.download_trackspo if source == 'spotify' else self.download_trackdee
            track = func(link, **track_options)
            smart.type = "track"
            smart.track = track

        elif "album/" in link:
            collection_options = {
                key: options[key]
                for key in (
                    "output_dir",
                    "quality_download",
                    "recursive_quality",
                    "recursive_download",
                    "not_interface",
                    "make_zip",
                    "custom_dir_format",
                    "custom_track_format",
                    "pad_tracks",
                    "initial_retry_delay",
                    "retry_delay_increase",
                    "max_retries",
                    "convert_to",
                    "bitrate",
                    "save_cover",
                    "market",
                    "artist_separator",
                )
            }
            func = self.download_albumspo if source == 'spotify' else self.download_albumdee
            album = func(link, **collection_options)
            smart.type = "album"
            smart.album = album

        elif "playlist/" in link:
            collection_options = {
                key: options[key]
                for key in (
                    "output_dir",
                    "quality_download",
                    "recursive_quality",
                    "recursive_download",
                    "not_interface",
                    "make_zip",
                    "custom_dir_format",
                    "custom_track_format",
                    "pad_tracks",
                    "initial_retry_delay",
                    "retry_delay_increase",
                    "max_retries",
                    "convert_to",
                    "bitrate",
                    "save_cover",
                    "market",
                    "artist_separator",
                )
            }
            func = self.download_playlistspo if source == 'spotify' else self.download_playlistdee
            playlist = func(link, **collection_options)
            smart.type = "playlist"
            smart.playlist = playlist

        return smart
