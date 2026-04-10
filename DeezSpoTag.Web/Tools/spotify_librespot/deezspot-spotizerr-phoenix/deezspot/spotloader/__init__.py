#!/usr/bin/python3
import traceback
from dataclasses import dataclass
from os.path import isfile
from deezspot.easy_spoty import Spo
from librespot.core import Session
from deezspot.exceptions import InvalidLink, MarketAvailabilityError
from deezspot.spotloader.__spo_api__ import tracking, tracking_album, tracking_episode
from deezspot.spotloader.spotify_settings import stock_quality, stock_market
from deezspot.libutils.utils import (
	get_ids,
	link_is_valid,
	what_kind,
)
from deezspot.models.download import (
	Track,
	Album,
	Playlist,
	Preferences,
	Smart,
	Episode
)
from deezspot.models.callback import TrackCallbackObject, ErrorObject
from deezspot.spotloader.__download__ import (
	DW_TRACK,
	DW_ALBUM,
	DW_PLAYLIST,
	DW_EPISODE,
	Download_JOB,
)
from deezspot.libutils.others_settings import (
	stock_output,
	stock_recursive_quality,
	stock_recursive_download,
	stock_not_interface,
	stock_zip,
	stock_save_cover,
	stock_real_time_dl,
	stock_market,
	stock_real_time_multiplier
)
from deezspot.libutils.logging_utils import logger, ProgressReporter, report_progress

SPOTIFY_DOMAIN = "spotify.com"


@dataclass(frozen=True)
class CollectionPreferencesOptions:
	recursive_quality: object
	recursive_download: object
	not_interface: object
	make_zip: object
	custom_dir_format: object
	custom_track_format: object
	pad_tracks: object
	initial_retry_delay: object
	retry_delay_increase: object
	max_retries: object
	convert_to: object
	bitrate: object
	save_cover: object
	market: list[str] | None
	artist_separator: str
	pad_number_width: int | str


class SpoLogin:
	_TRACK_OPTION_ORDER = [
		"output_dir", "quality_download", "recursive_quality", "recursive_download",
		"not_interface", "real_time_dl", "real_time_multiplier", "custom_dir_format",
		"custom_track_format", "pad_tracks", "initial_retry_delay", "retry_delay_increase",
		"max_retries", "convert_to", "bitrate", "save_cover", "market", "artist_separator",
		"pad_number_width",
	]
	_TRACK_OPTION_DEFAULTS = {
		"output_dir": stock_output,
		"quality_download": stock_quality,
		"recursive_quality": stock_recursive_quality,
		"recursive_download": stock_recursive_download,
		"not_interface": stock_not_interface,
		"real_time_dl": stock_real_time_dl,
		"real_time_multiplier": stock_real_time_multiplier,
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
	_COLLECTION_OPTION_ORDER = [
		"output_dir", "quality_download", "recursive_quality", "recursive_download",
		"not_interface", "make_zip", "real_time_dl", "real_time_multiplier",
		"custom_dir_format", "custom_track_format", "pad_tracks", "initial_retry_delay",
		"retry_delay_increase", "max_retries", "convert_to", "bitrate", "save_cover",
		"market", "artist_separator", "pad_number_width",
	]
	_COLLECTION_OPTION_DEFAULTS = {
		"output_dir": stock_output,
		"quality_download": stock_quality,
		"recursive_quality": stock_recursive_quality,
		"recursive_download": stock_recursive_download,
		"not_interface": stock_not_interface,
		"make_zip": stock_zip,
		"real_time_dl": stock_real_time_dl,
		"real_time_multiplier": stock_real_time_multiplier,
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
	_EPISODE_OPTION_ORDER = [
		"output_dir", "quality_download", "recursive_quality", "recursive_download",
		"not_interface", "real_time_dl", "real_time_multiplier", "custom_dir_format",
		"custom_track_format", "pad_tracks", "initial_retry_delay", "retry_delay_increase",
		"max_retries", "convert_to", "bitrate", "save_cover", "market", "artist_separator",
	]
	_EPISODE_OPTION_DEFAULTS = {
		"output_dir": stock_output,
		"quality_download": stock_quality,
		"recursive_quality": stock_recursive_quality,
		"recursive_download": stock_recursive_download,
		"not_interface": stock_not_interface,
		"real_time_dl": stock_real_time_dl,
		"real_time_multiplier": stock_real_time_multiplier,
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
	_ARTIST_OPTION_ORDER = [
		"output_dir", "quality_download", "recursive_quality", "recursive_download",
		"not_interface", "make_zip", "real_time_dl", "real_time_multiplier",
		"custom_dir_format", "custom_track_format", "pad_tracks", "initial_retry_delay",
		"retry_delay_increase", "max_retries", "convert_to", "bitrate", "market",
		"save_cover", "artist_separator",
	]
	_ARTIST_OPTION_DEFAULTS = {
		"output_dir": stock_output,
		"quality_download": stock_quality,
		"recursive_quality": stock_recursive_quality,
		"recursive_download": stock_recursive_download,
		"not_interface": stock_not_interface,
		"make_zip": stock_zip,
		"real_time_dl": stock_real_time_dl,
		"real_time_multiplier": stock_real_time_multiplier,
		"custom_dir_format": None,
		"custom_track_format": None,
		"pad_tracks": True,
		"initial_retry_delay": 30,
		"retry_delay_increase": 30,
		"max_retries": 5,
		"convert_to": None,
		"bitrate": None,
		"market": stock_market,
		"save_cover": stock_save_cover,
		"artist_separator": "; ",
	}
	_SMART_OPTION_ORDER = [
		"output_dir", "quality_download", "recursive_quality", "recursive_download",
		"not_interface", "make_zip", "real_time_dl", "real_time_multiplier",
		"custom_dir_format", "custom_track_format", "pad_tracks", "initial_retry_delay",
		"retry_delay_increase", "max_retries", "convert_to", "bitrate", "save_cover",
		"market", "artist_separator",
	]
	_SMART_OPTION_DEFAULTS = {
		"output_dir": stock_output,
		"quality_download": stock_quality,
		"recursive_quality": stock_recursive_quality,
		"recursive_download": stock_recursive_download,
		"not_interface": stock_not_interface,
		"make_zip": stock_zip,
		"real_time_dl": stock_real_time_dl,
		"real_time_multiplier": stock_real_time_multiplier,
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
		credentials_path: str,
		spotify_client_id: str = None,
		spotify_client_secret: str = None,
		progress_callback = None,
		silent: bool = False
	) -> None:
		self.credentials_path = credentials_path
		self.spotify_client_id = spotify_client_id
		self.spotify_client_secret = spotify_client_secret
		
		# Initialize Spotify API with credentials if provided (kept no-op for compatibility)
		if spotify_client_id and spotify_client_secret:
			Spo.__init__(client_id=spotify_client_id, client_secret=spotify_client_secret)
			logger.info("Initialized Spotify API compatibility shim (librespot-backed)")
			
		# Configure progress reporting
		self.progress_reporter = ProgressReporter(callback=progress_callback, silent=silent)
		
		self.__initialize_session()

	def __initialize_session(self) -> None:
		try:
			session_builder = Session.Builder()
			session_builder.conf.stored_credentials_file = self.credentials_path

			if isfile(self.credentials_path):
				session = session_builder.stored_file().create()
				logger.info("Successfully initialized Spotify session")
			else:
				logger.error("Credentials file not found")
				raise FileNotFoundError("Please fill your credentials.json location!")

			Download_JOB(session)
			Download_JOB.set_progress_reporter(self.progress_reporter)
			# Wire the session into Spo shim for metadata/search
			Spo.set_session(session)
		except Exception as e:
			logger.error(f"Failed to initialize Spotify session: {str(e)}")
			raise

	@staticmethod
	def _apply_collection_preferences(
		preferences: Preferences,
		options: CollectionPreferencesOptions
	) -> None:
		preferences.recursive_quality = options.recursive_quality
		preferences.recursive_download = options.recursive_download
		preferences.not_interface = options.not_interface
		preferences.make_zip = options.make_zip
		preferences.is_episode = False
		preferences.custom_dir_format = options.custom_dir_format
		preferences.custom_track_format = options.custom_track_format
		preferences.pad_tracks = options.pad_tracks
		preferences.initial_retry_delay = options.initial_retry_delay
		preferences.retry_delay_increase = options.retry_delay_increase
		preferences.max_retries = options.max_retries
		if options.convert_to is None:
			preferences.convert_to = None
			preferences.bitrate = None
		else:
			preferences.convert_to = options.convert_to
			preferences.bitrate = options.bitrate
		preferences.save_cover = options.save_cover
		preferences.market = options.market
		preferences.artist_separator = options.artist_separator
		preferences.pad_number_width = options.pad_number_width

	@staticmethod
	def _playlist_failure_info(
		error_type: str,
		error_message: str,
		name: str,
		track_id: str | None,
	) -> dict:
		return {
			'error_type': error_type,
			'error_message': error_message,
			'name': name,
			'ids': track_id,
		}

	def _extract_playlist_item_metadata(
		self,
		item: dict | None,
		link_playlist: str,
		market: list[str] | None,
	):
		if not item or 'track' not in item or not item['track']:
			logger.warning(
				f"Skipping an item in playlist {link_playlist} as it does not appear to be a valid track object."
			)
			return self._playlist_failure_info(
				error_type='invalid_track_object',
				error_message='Playlist item was not a valid track object.',
				name='Unknown Skipped Item',
				track_id=None,
			)

		track_data = item['track']
		track_id = track_data.get('id')
		track_name = track_data.get('name', 'Unknown Track without ID')
		if not track_id:
			logger.warning(f"Skipping an item in playlist {link_playlist} because it has no track ID.")
			return self._playlist_failure_info(
				error_type='missing_track_id',
				error_message='Playlist item is missing a track ID.',
				name=track_name,
				track_id=None,
			)

		try:
			song_metadata = tracking(track_id, market=market)
			if song_metadata:
				return song_metadata
			logger.warning(f"Could not retrieve metadata for track {track_id} in playlist {link_playlist}.")
			return self._playlist_failure_info(
				error_type='metadata_fetch_failed',
				error_message=f"Failed to fetch metadata for track ID: {track_id}",
				name=track_data.get('name', f'Track ID {track_id}'),
				track_id=track_id,
			)
		except MarketAvailabilityError as e:
			logger.warning(str(e))
			return self._playlist_failure_info(
				error_type='market_availability_error',
				error_message=str(e),
				name=track_data.get('name', f'Track ID {track_id}'),
				track_id=track_id,
			)

	@staticmethod
	def _merge_options(
		*,
		legacy_args: tuple,
		kwargs: dict,
		param_order: list[str],
		defaults: dict
	) -> dict:
		if len(legacy_args) > len(param_order):
			raise TypeError(
				f"Expected at most {len(param_order)} positional option(s), got {len(legacy_args)}."
			)

		positional_values = dict(zip(param_order, legacy_args))
		unexpected = sorted(set(kwargs).difference(defaults))
		if unexpected:
			raise TypeError(f"Unexpected option(s): {', '.join(sorted(unexpected))}")

		duplicates = sorted(set(kwargs).intersection(positional_values))
		if duplicates:
			raise TypeError(f"Multiple values for option(s): {', '.join(sorted(duplicates))}")

		return {**defaults, **positional_values, **kwargs}

	def _resolve_track_options(self, legacy_args: tuple, kwargs: dict) -> dict:
		return self._merge_options(
			legacy_args=legacy_args,
			kwargs=kwargs,
			param_order=self._TRACK_OPTION_ORDER,
			defaults=self._TRACK_OPTION_DEFAULTS,
		)

	def _resolve_collection_options(self, legacy_args: tuple, kwargs: dict) -> dict:
		return self._merge_options(
			legacy_args=legacy_args,
			kwargs=kwargs,
			param_order=self._COLLECTION_OPTION_ORDER,
			defaults=self._COLLECTION_OPTION_DEFAULTS,
		)

	def _resolve_episode_options(self, legacy_args: tuple, kwargs: dict) -> dict:
		return self._merge_options(
			legacy_args=legacy_args,
			kwargs=kwargs,
			param_order=self._EPISODE_OPTION_ORDER,
			defaults=self._EPISODE_OPTION_DEFAULTS,
		)

	def _resolve_artist_options(self, legacy_args: tuple, kwargs: dict) -> dict:
		return self._merge_options(
			legacy_args=legacy_args,
			kwargs=kwargs,
			param_order=self._ARTIST_OPTION_ORDER,
			defaults=self._ARTIST_OPTION_DEFAULTS,
		)

	def _resolve_smart_options(self, legacy_args: tuple, kwargs: dict) -> dict:
		return self._merge_options(
			legacy_args=legacy_args,
			kwargs=kwargs,
			param_order=self._SMART_OPTION_ORDER,
			defaults=self._SMART_OPTION_DEFAULTS,
		)

	@staticmethod
	def _build_track_download_options(options: dict) -> dict:
		return {
			"output_dir": options["output_dir"],
			"quality_download": options["quality_download"],
			"recursive_quality": options["recursive_quality"],
			"recursive_download": options["recursive_download"],
			"not_interface": options["not_interface"],
			"real_time_dl": options["real_time_dl"],
			"real_time_multiplier": options["real_time_multiplier"],
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
		}

	@staticmethod
	def _build_collection_download_options(options: dict) -> dict:
		return {
			**SpoLogin._build_track_download_options(options),
			"make_zip": options["make_zip"],
		}

	@staticmethod
	def _require_spotify_domain(link: str) -> None:
		if SPOTIFY_DOMAIN not in link:
			raise InvalidLink(link)

	@staticmethod
	def _build_collection_preferences_options(options: dict) -> CollectionPreferencesOptions:
		return CollectionPreferencesOptions(
			recursive_quality=options["recursive_quality"],
			recursive_download=options["recursive_download"],
			not_interface=options["not_interface"],
			make_zip=options["make_zip"],
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
			artist_separator=options["artist_separator"],
			pad_number_width=options["pad_number_width"],
		)

	@staticmethod
	def _apply_download_preferences(
		preferences: Preferences,
		*,
		link: str,
		metadata: object,
		ids: str,
		options: dict,
		is_episode: bool,
		include_pad_number_width: bool,
	) -> None:
		preferences.real_time_dl = options["real_time_dl"]
		preferences.real_time_multiplier = int(options["real_time_multiplier"]) if options["real_time_multiplier"] is not None else 1
		preferences.link = link
		preferences.song_metadata = metadata
		preferences.quality_download = options["quality_download"]
		preferences.output_dir = options["output_dir"]
		preferences.ids = ids
		preferences.recursive_quality = options["recursive_quality"]
		preferences.recursive_download = options["recursive_download"]
		preferences.not_interface = options["not_interface"]
		preferences.is_episode = is_episode
		preferences.custom_dir_format = options["custom_dir_format"]
		preferences.custom_track_format = options["custom_track_format"]
		preferences.pad_tracks = options["pad_tracks"]
		preferences.initial_retry_delay = options["initial_retry_delay"]
		preferences.retry_delay_increase = options["retry_delay_increase"]
		preferences.max_retries = options["max_retries"]
		if options["convert_to"] is None:
			preferences.convert_to = None
			preferences.bitrate = None
		else:
			preferences.convert_to = options["convert_to"]
			preferences.bitrate = options["bitrate"]
		preferences.save_cover = options["save_cover"]
		preferences.market = options["market"]
		preferences.artist_separator = options["artist_separator"]
		if include_pad_number_width:
			preferences.pad_number_width = options["pad_number_width"]

	def download_track(self, link_track, *args, **kwargs) -> Track:
		options = self._resolve_track_options(args, kwargs)
		song_metadata = None
		try:
			link_is_valid(link_track)
			ids = get_ids(link_track)
			song_metadata = tracking(ids, market=options["market"])
			
			if song_metadata is None:
				raise RuntimeError(f"Could not retrieve metadata for track {link_track}. It might not be available or an API error occurred.")

			logger.info(f"Starting download for track: {song_metadata.title} - {options['artist_separator'].join([a.name for a in song_metadata.artists])}")

			preferences = Preferences()
			self._apply_download_preferences(
				preferences,
				link=link_track,
				metadata=song_metadata,
				ids=ids,
				options=options,
				is_episode=False,
				include_pad_number_width=True,
			)

			track = DW_TRACK(preferences).dw()

			return track
		except MarketAvailabilityError as e:
			logger.error(f"Track download failed due to market availability: {str(e)}")
			if song_metadata:
				status_obj = ErrorObject(ids=song_metadata.ids, error=str(e))
				callback_obj = TrackCallbackObject(track=song_metadata, status_info=status_obj)
				report_progress(
					reporter=self.progress_reporter,
					callback_obj=callback_obj
				)
			raise
		except Exception as e:
			logger.error(f"Failed to download track: {str(e)}")
			traceback.print_exc()
			if song_metadata:
				status_obj = ErrorObject(ids=song_metadata.ids, error=str(e))
				callback_obj = TrackCallbackObject(track=song_metadata, status_info=status_obj)
				report_progress(
					reporter=self.progress_reporter,
					callback_obj=callback_obj
				)
			raise e

	def download_album(self, link_album, *args, **kwargs) -> Album:
		options = self._resolve_collection_options(args, kwargs)
		try:
			link_is_valid(link_album)
			ids = get_ids(link_album)
			album_json = Spo.get_album(ids)
			if not album_json:
				raise RuntimeError(f"Could not retrieve album data for {link_album}.")
			
			song_metadata = tracking_album(album_json, market=options["market"])
			if song_metadata is None:
				raise RuntimeError(f"Could not process album metadata for {link_album}. It might not be available in the specified market(s) or an API error occurred.")

			logger.info(f"Starting download for album: {song_metadata.title} - {options['artist_separator'].join([a.name for a in song_metadata.artists])}")

			preferences = Preferences()
			preferences.real_time_dl = options["real_time_dl"]
			preferences.real_time_multiplier = int(options["real_time_multiplier"]) if options["real_time_multiplier"] is not None else 1
			preferences.link = link_album
			preferences.song_metadata = song_metadata
			preferences.quality_download = options["quality_download"]
			preferences.output_dir = options["output_dir"]
			preferences.ids = ids
			preferences.json_data = album_json
			collection_options = self._build_collection_preferences_options(options)
			self._apply_collection_preferences(
				preferences,
				collection_options,
			)

			album = DW_ALBUM(preferences).dw()

			return album
		except MarketAvailabilityError as e:
			logger.error(f"Album download failed due to market availability: {str(e)}")
			raise
		except Exception as e:
			logger.error(f"Failed to download album: {str(e)}")
			traceback.print_exc()
			raise e

	def download_playlist(self, link_playlist, *args, **kwargs) -> Playlist:
		options = self._resolve_collection_options(args, kwargs)
		try:
			link_is_valid(link_playlist)
			ids = get_ids(link_playlist)

			playlist_json = Spo.get_playlist(ids)
			if not playlist_json:
				raise RuntimeError(f"Could not retrieve playlist data for {link_playlist}.")
			
			logger.info(f"Starting download for playlist: {playlist_json.get('name', 'Unknown')}")

			playlist_tracks_data = playlist_json.get('tracks', {}).get('items', [])
			if not playlist_tracks_data:
				logger.warning(f"Playlist {link_playlist} has no tracks or could not be fetched.")
				# We can still proceed to create an empty playlist object for consistency
				
			song_metadata_list = [
				self._extract_playlist_item_metadata(item, link_playlist, options["market"])
				for item in playlist_tracks_data
			]

			preferences = Preferences()
			preferences.real_time_dl = options["real_time_dl"]
			preferences.real_time_multiplier = int(options["real_time_multiplier"]) if options["real_time_multiplier"] is not None else 1
			preferences.link = link_playlist
			preferences.song_metadata = song_metadata_list
			preferences.quality_download = options["quality_download"]
			preferences.output_dir = options["output_dir"]
			preferences.ids = ids
			preferences.json_data = playlist_json
			preferences.playlist_tracks_json = playlist_tracks_data
			collection_options = self._build_collection_preferences_options(options)
			self._apply_collection_preferences(
				preferences,
				collection_options,
			)
			
			playlist = DW_PLAYLIST(preferences).dw()

			return playlist
		except MarketAvailabilityError as e:
			logger.error(f"Playlist download failed due to market availability issues with one or more tracks: {str(e)}")
			raise
		except Exception as e:
			logger.error(f"Failed to download playlist: {str(e)}")
			traceback.print_exc()
			raise e

	def download_episode(self, link_episode, *args, **kwargs) -> Episode:
		options = self._resolve_episode_options(args, kwargs)
		try:
			link_is_valid(link_episode)
			ids = get_ids(link_episode)
			episode_json = Spo.get_episode(ids)
			if not episode_json:
				raise RuntimeError(f"Could not retrieve episode data for {link_episode} from API.")

			episode_metadata = tracking_episode(ids, market=options["market"])
			if episode_metadata is None:
				raise RuntimeError(f"Could not process episode metadata for {link_episode}. It might not be available in the specified market(s) or an API error occurred.")
			
			logger.info(f"Starting download for episode: {episode_metadata.title} - {episode_metadata.album.title}")

			preferences = Preferences()
			self._apply_download_preferences(
				preferences,
				link=link_episode,
				metadata=episode_metadata,
				ids=ids,
				options=options,
				is_episode=True,
				include_pad_number_width=False,
			)
			preferences.json_data = episode_json

			episode = DW_EPISODE(preferences).dw()

			return episode
		except MarketAvailabilityError as e:
			logger.error(f"Episode download failed due to market availability: {str(e)}")
			raise
		except Exception as e:
			logger.error(f"Failed to download episode: {str(e)}")
			traceback.print_exc()
			raise e

	def download_artist(
		self, link_artist,
		album_type: str = 'album,single,compilation,appears_on',
		limit: int = 50,
		*args,
		**kwargs
	):
		options = self._resolve_artist_options(args, kwargs)
		"""
		Download all albums (or a subset based on album_type and limit) from an artist.
		"""
		try:
			link_is_valid(link_artist)
			ids = get_ids(link_artist)
			discography = Spo.get_artist(ids, album_type=album_type, limit=limit)
			albums = discography.get('items', [])
			if not albums:
				logger.warning("No albums found for the provided artist")
				raise RuntimeError("No albums found for the provided artist.")
				
			logger.info(f"Starting download for artist discography: {discography.get('name', 'Unknown')}")
			
			downloaded_albums = []
			for album in albums:
				album_url = album.get('external_urls', {}).get('spotify')
				if not album_url:
					logger.warning(f"No URL found for album: {album.get('name', 'Unknown')}")
					continue
				downloaded_album = self.download_album(
					album_url,
					output_dir=options["output_dir"],
					quality_download=options["quality_download"],
					recursive_quality=options["recursive_quality"],
					recursive_download=options["recursive_download"],
					not_interface=options["not_interface"],
					make_zip=options["make_zip"],
					real_time_dl=options["real_time_dl"],
					real_time_multiplier=options["real_time_multiplier"],
					custom_dir_format=options["custom_dir_format"],
					custom_track_format=options["custom_track_format"],
					pad_tracks=options["pad_tracks"],
					initial_retry_delay=options["initial_retry_delay"],
					retry_delay_increase=options["retry_delay_increase"],
					max_retries=options["max_retries"],
					convert_to=options["convert_to"],
					bitrate=options["bitrate"],
					market=options["market"],
					save_cover=options["save_cover"],
					artist_separator=options["artist_separator"]
				)
				downloaded_albums.append(downloaded_album)
			return downloaded_albums
		except Exception as e:
			logger.error(f"Failed to download artist discography: {str(e)}")
			traceback.print_exc()
			raise e

	def _download_smart_resource(self, link: str, options: dict) -> tuple[str | None, object | None]:
		resource_handlers = [
			("track/", "track", self.download_track, self._build_track_download_options),
			("album/", "album", self.download_album, self._build_collection_download_options),
			("playlist/", "playlist", self.download_playlist, self._build_collection_download_options),
			("episode/", "episode", self.download_episode, self._build_track_download_options),
		]
		for marker, resource_type, downloader, option_builder in resource_handlers:
			if marker not in link:
				continue
			self._require_spotify_domain(link)
			resource = downloader(link, **option_builder(options))
			return resource_type, resource
		return None, None

	def download_smart(self, link, *args, **kwargs) -> Smart:
		options = self._resolve_smart_options(args, kwargs)
		try:
			link_is_valid(link)
			link = what_kind(link)
			smart = Smart()

			source = link
			if SPOTIFY_DOMAIN in link:
				source = f"https://{SPOTIFY_DOMAIN}"
			smart.source = source
			
			logger.info(f"Starting smart download for: {link}")

			resource_type, resource = self._download_smart_resource(link, options)
			if resource_type and resource is not None:
				smart.type = resource_type
				setattr(smart, resource_type, resource)

			return smart
		except Exception as e:
			logger.error(f"Failed to perform smart download: {str(e)}")
			traceback.print_exc()
			raise e
