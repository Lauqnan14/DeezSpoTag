#!/usr/bin/python3
import traceback
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
from deezspot.models.callback import trackCallbackObject, errorObject
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

class SpoLogin:
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

	def download_track(
		self, link_track,
		output_dir=stock_output,
		quality_download=stock_quality,
		recursive_quality=stock_recursive_quality,
		recursive_download=stock_recursive_download,
		not_interface=stock_not_interface,
		real_time_dl=stock_real_time_dl,
		real_time_multiplier: int = stock_real_time_multiplier,
		custom_dir_format=None,
		custom_track_format=None,
		pad_tracks=True,
		initial_retry_delay=30,
		retry_delay_increase=30,
		max_retries=5,
		convert_to=None,
		bitrate=None,
		save_cover=stock_save_cover,
		market: list[str] | None = stock_market,
		artist_separator: str = "; ",
		pad_number_width: int | str = 'auto'
	) -> Track:
		song_metadata = None
		try:
			link_is_valid(link_track)
			ids = get_ids(link_track)
			song_metadata = tracking(ids, market=market)
			
			if song_metadata is None:
				raise Exception(f"Could not retrieve metadata for track {link_track}. It might not be available or an API error occurred.")

			logger.info(f"Starting download for track: {song_metadata.title} - {artist_separator.join([a.name for a in song_metadata.artists])}")

			preferences = Preferences()
			preferences.real_time_dl = real_time_dl
			preferences.real_time_multiplier = int(real_time_multiplier) if real_time_multiplier is not None else 1
			preferences.link = link_track
			preferences.song_metadata = song_metadata
			preferences.quality_download = quality_download
			preferences.output_dir = output_dir
			preferences.ids = ids
			preferences.recursive_quality = recursive_quality
			preferences.recursive_download = recursive_download
			preferences.not_interface = not_interface
			preferences.is_episode = False
			preferences.custom_dir_format = custom_dir_format
			preferences.custom_track_format = custom_track_format
			preferences.pad_tracks = pad_tracks
			preferences.initial_retry_delay = initial_retry_delay
			preferences.retry_delay_increase = retry_delay_increase
			preferences.max_retries = max_retries
			if convert_to is None:
				preferences.convert_to = None
				preferences.bitrate = None
			else:
				preferences.convert_to = convert_to
				preferences.bitrate = bitrate
			preferences.save_cover = save_cover
			preferences.market = market
			preferences.artist_separator = artist_separator
			preferences.pad_number_width = pad_number_width

			track = DW_TRACK(preferences).dw()

			return track
		except MarketAvailabilityError as e:
			logger.error(f"Track download failed due to market availability: {str(e)}")
			if song_metadata:
				status_obj = errorObject(ids=song_metadata.ids, error=str(e))
				callback_obj = trackCallbackObject(track=song_metadata, status_info=status_obj)
				report_progress(
					reporter=self.progress_reporter,
					callback_obj=callback_obj
				)
			raise
		except Exception as e:
			logger.error(f"Failed to download track: {str(e)}")
			traceback.print_exc()
			if song_metadata:
				status_obj = errorObject(ids=song_metadata.ids, error=str(e))
				callback_obj = trackCallbackObject(track=song_metadata, status_info=status_obj)
				report_progress(
					reporter=self.progress_reporter,
					callback_obj=callback_obj
				)
			raise e

	def download_album(
		self, link_album,
		output_dir=stock_output,
		quality_download=stock_quality,
		recursive_quality=stock_recursive_quality,
		recursive_download=stock_recursive_download,
		not_interface=stock_not_interface,
		make_zip=stock_zip,
		real_time_dl=stock_real_time_dl,
		real_time_multiplier: int = stock_real_time_multiplier,
		custom_dir_format=None,
		custom_track_format=None,
		pad_tracks=True,
		initial_retry_delay=30,
		retry_delay_increase=30,
		max_retries=5,
		convert_to=None,
		bitrate=None,
		save_cover=stock_save_cover,
		market: list[str] | None = stock_market,
		artist_separator: str = "; ",
		pad_number_width: int | str = 'auto'
	) -> Album:
		try:
			link_is_valid(link_album)
			ids = get_ids(link_album)
			album_json = Spo.get_album(ids)
			if not album_json:
				raise Exception(f"Could not retrieve album data for {link_album}.")
			
			song_metadata = tracking_album(album_json, market=market)
			if song_metadata is None:
				raise Exception(f"Could not process album metadata for {link_album}. It might not be available in the specified market(s) or an API error occurred.")

			logger.info(f"Starting download for album: {song_metadata.title} - {artist_separator.join([a.name for a in song_metadata.artists])}")

			preferences = Preferences()
			preferences.real_time_dl = real_time_dl
			preferences.real_time_multiplier = int(real_time_multiplier) if real_time_multiplier is not None else 1
			preferences.link = link_album
			preferences.song_metadata = song_metadata
			preferences.quality_download = quality_download
			preferences.output_dir = output_dir
			preferences.ids = ids
			preferences.json_data = album_json
			preferences.recursive_quality = recursive_quality
			preferences.recursive_download = recursive_download
			preferences.not_interface = not_interface
			preferences.make_zip = make_zip
			preferences.is_episode = False
			preferences.custom_dir_format = custom_dir_format
			preferences.custom_track_format = custom_track_format
			preferences.pad_tracks = pad_tracks
			preferences.initial_retry_delay = initial_retry_delay
			preferences.retry_delay_increase = retry_delay_increase
			preferences.max_retries = max_retries
			if convert_to is None:
				preferences.convert_to = None
				preferences.bitrate = None
			else:
				preferences.convert_to = convert_to
				preferences.bitrate = bitrate
			preferences.save_cover = save_cover
			preferences.market = market
			preferences.artist_separator = artist_separator
			preferences.pad_number_width = pad_number_width

			album = DW_ALBUM(preferences).dw()

			return album
		except MarketAvailabilityError as e:
			logger.error(f"Album download failed due to market availability: {str(e)}")
			raise
		except Exception as e:
			logger.error(f"Failed to download album: {str(e)}")
			traceback.print_exc()
			raise e

	def download_playlist(
		self, link_playlist,
		output_dir=stock_output,
		quality_download=stock_quality,
		recursive_quality=stock_recursive_quality,
		recursive_download=stock_recursive_download,
		not_interface=stock_not_interface,
		make_zip=stock_zip,
		real_time_dl=stock_real_time_dl,
		real_time_multiplier: int = stock_real_time_multiplier,
		custom_dir_format=None,
		custom_track_format=None,
		pad_tracks=True,
		initial_retry_delay=30,
		retry_delay_increase=30,
		max_retries=5,
		convert_to=None,
		bitrate=None,
		save_cover=stock_save_cover,
		market: list[str] | None = stock_market,
		artist_separator: str = "; ",
		pad_number_width: int | str = 'auto'
	) -> Playlist:
		try:
			link_is_valid(link_playlist)
			ids = get_ids(link_playlist)

			song_metadata = []
			playlist_json = Spo.get_playlist(ids)
			if not playlist_json:
				raise Exception(f"Could not retrieve playlist data for {link_playlist}.")
			
			logger.info(f"Starting download for playlist: {playlist_json.get('name', 'Unknown')}")

			playlist_tracks_data = playlist_json.get('tracks', {}).get('items', [])
			if not playlist_tracks_data:
				logger.warning(f"Playlist {link_playlist} has no tracks or could not be fetched.")
				# We can still proceed to create an empty playlist object for consistency
				
			song_metadata_list = []
			for item in playlist_tracks_data:
				if not item or 'track' not in item or not item['track']:
					# Log a warning for items that are not valid tracks (e.g., local files, etc.)
					logger.warning(f"Skipping an item in playlist {link_playlist} as it does not appear to be a valid track object.")
					song_metadata_list.append({'error_type': 'invalid_track_object', 'error_message': 'Playlist item was not a valid track object.', 'name': 'Unknown Skipped Item', 'ids': None})
					continue
				
				track_data = item['track']
				track_id = track_data.get('id')
				
				if not track_id:
					logger.warning(f"Skipping an item in playlist {link_playlist} because it has no track ID.")
					song_metadata_list.append({'error_type': 'missing_track_id', 'error_message': 'Playlist item is missing a track ID.', 'name': track_data.get('name', 'Unknown Track without ID'), 'ids': None})
					continue

				try:
					song_metadata = tracking(track_id, market=market)
					if song_metadata:
						song_metadata_list.append(song_metadata)
					else:
						# Create a placeholder for tracks that fail metadata fetching
						failed_track_info = {'error_type': 'metadata_fetch_failed', 'error_message': f"Failed to fetch metadata for track ID: {track_id}", 'name': track_data.get('name', f'Track ID {track_id}'), 'ids': track_id}
						song_metadata_list.append(failed_track_info)
						logger.warning(f"Could not retrieve metadata for track {track_id} in playlist {link_playlist}.")
				except MarketAvailabilityError as e:
					failed_track_info = {'error_type': 'market_availability_error', 'error_message': str(e), 'name': track_data.get('name', f'Track ID {track_id}'), 'ids': track_id}
					song_metadata_list.append(failed_track_info)
					logger.warning(str(e))

			preferences = Preferences()
			preferences.real_time_dl = real_time_dl
			preferences.real_time_multiplier = int(real_time_multiplier) if real_time_multiplier is not None else 1
			preferences.link = link_playlist
			preferences.song_metadata = song_metadata_list
			preferences.quality_download = quality_download
			preferences.output_dir = output_dir
			preferences.ids = ids
			preferences.json_data = playlist_json
			preferences.playlist_tracks_json = playlist_tracks_data
			preferences.recursive_quality = recursive_quality
			preferences.recursive_download = recursive_download
			preferences.not_interface = not_interface
			preferences.make_zip = make_zip
			preferences.is_episode = False
			preferences.custom_dir_format = custom_dir_format
			preferences.custom_track_format = custom_track_format
			preferences.pad_tracks = pad_tracks
			preferences.initial_retry_delay = initial_retry_delay
			preferences.retry_delay_increase = retry_delay_increase
			preferences.max_retries = max_retries
			if convert_to is None:
				preferences.convert_to = None
				preferences.bitrate = None
			else:
				preferences.convert_to = convert_to
				preferences.bitrate = bitrate
			preferences.save_cover = save_cover
			preferences.market = market
			preferences.artist_separator = artist_separator
			preferences.pad_number_width = pad_number_width
			
			playlist = DW_PLAYLIST(preferences).dw()

			return playlist
		except MarketAvailabilityError as e:
			logger.error(f"Playlist download failed due to market availability issues with one or more tracks: {str(e)}")
			raise
		except Exception as e:
			logger.error(f"Failed to download playlist: {str(e)}")
			traceback.print_exc()
			raise e

	def download_episode(
		self, link_episode,
		output_dir=stock_output,
		quality_download=stock_quality,
		recursive_quality=stock_recursive_quality,
		recursive_download=stock_recursive_download,
		not_interface=stock_not_interface,
		real_time_dl=stock_real_time_dl,
		real_time_multiplier: int = stock_real_time_multiplier,
		custom_dir_format=None,
		custom_track_format=None,
		pad_tracks=True,
		initial_retry_delay=30,
		retry_delay_increase=30,
		max_retries=5,
		convert_to=None,
		bitrate=None,
		save_cover=stock_save_cover,
		market: list[str] | None = stock_market,
		artist_separator: str = "; "
	) -> Episode:
		try:
			link_is_valid(link_episode)
			ids = get_ids(link_episode)
			episode_json = Spo.get_episode(ids)
			if not episode_json:
				raise Exception(f"Could not retrieve episode data for {link_episode} from API.")

			episode_metadata = tracking_episode(ids, market=market)
			if episode_metadata is None:
				raise Exception(f"Could not process episode metadata for {link_episode}. It might not be available in the specified market(s) or an API error occurred.")
			
			logger.info(f"Starting download for episode: {episode_metadata.title} - {episode_metadata.album.title}")

			preferences = Preferences()
			preferences.real_time_dl = real_time_dl
			preferences.real_time_multiplier = int(real_time_multiplier) if real_time_multiplier is not None else 1
			preferences.link = link_episode
			preferences.song_metadata = episode_metadata
			preferences.output_dir = output_dir
			preferences.ids = ids
			preferences.json_data = episode_json
			preferences.recursive_quality = recursive_quality
			preferences.recursive_download = recursive_download
			preferences.not_interface = not_interface
			preferences.is_episode = True
			preferences.custom_dir_format = custom_dir_format
			preferences.custom_track_format = custom_track_format
			preferences.pad_tracks = pad_tracks
			preferences.initial_retry_delay = initial_retry_delay
			preferences.retry_delay_increase = retry_delay_increase
			preferences.max_retries = max_retries
			if convert_to is None:
				preferences.convert_to = None
				preferences.bitrate = None
			else:
				preferences.convert_to = convert_to
				preferences.bitrate = bitrate
			preferences.save_cover = save_cover
			preferences.market = market
			preferences.artist_separator = artist_separator

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
		output_dir=stock_output,
		quality_download=stock_quality,
		recursive_quality=stock_recursive_quality,
		recursive_download=stock_recursive_download,
		not_interface=stock_not_interface,
		make_zip=stock_zip,
		real_time_dl=stock_real_time_dl,
		real_time_multiplier: int = stock_real_time_multiplier,
		custom_dir_format=None,
		custom_track_format=None,
		pad_tracks=True,
		initial_retry_delay=30,
		retry_delay_increase=30,
		max_retries=5,
		convert_to=None,
		bitrate=None,
		market: list[str] | None = stock_market,
		save_cover=stock_save_cover,
		artist_separator: str = "; "
	):
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
				raise Exception("No albums found for the provided artist.")
				
			logger.info(f"Starting download for artist discography: {discography.get('name', 'Unknown')}")
			
			downloaded_albums = []
			for album in albums:
				album_url = album.get('external_urls', {}).get('spotify')
				if not album_url:
					logger.warning(f"No URL found for album: {album.get('name', 'Unknown')}")
					continue
				downloaded_album = self.download_album(
					album_url,
					output_dir=output_dir,
					quality_download=quality_download,
					recursive_quality=recursive_quality,
					recursive_download=recursive_download,
					not_interface=not_interface,
					make_zip=make_zip,
					real_time_dl=real_time_dl,
					real_time_multiplier=real_time_multiplier,
					custom_dir_format=custom_dir_format,
					custom_track_format=custom_track_format,
					pad_tracks=pad_tracks,
					initial_retry_delay=initial_retry_delay,
					retry_delay_increase=retry_delay_increase,
					max_retries=max_retries,
					convert_to=convert_to,
					bitrate=bitrate,
					market=market,
					save_cover=save_cover,
					artist_separator=artist_separator
				)
				downloaded_albums.append(downloaded_album)
			return downloaded_albums
		except Exception as e:
			logger.error(f"Failed to download artist discography: {str(e)}")
			traceback.print_exc()
			raise e

	def download_smart(
		self, link,
		output_dir=stock_output,
		quality_download=stock_quality,
		recursive_quality=stock_recursive_quality,
		recursive_download=stock_recursive_download,
		not_interface=stock_not_interface,
		make_zip=stock_zip,
		real_time_dl=stock_real_time_dl,
		real_time_multiplier: int = stock_real_time_multiplier,
		custom_dir_format=None,
		custom_track_format=None,
		pad_tracks=True,
		initial_retry_delay=30,
		retry_delay_increase=30,
		max_retries=5,
		convert_to=None,
		bitrate=None,
		save_cover=stock_save_cover,
		market: list[str] | None = stock_market,
		artist_separator: str = "; "
	) -> Smart:
		try:
			link_is_valid(link)
			link = what_kind(link)
			smart = Smart()

			if "spotify.com" in link:
				source = "https://spotify.com"
			smart.source = source
			
			logger.info(f"Starting smart download for: {link}")

			if "track/" in link:
				if not "spotify.com" in link:
					raise InvalidLink(link)
				track = self.download_track(
					link,
					output_dir=output_dir,
					quality_download=quality_download,
					recursive_quality=recursive_quality,
					recursive_download=recursive_download,
					not_interface=not_interface,
					real_time_dl=real_time_dl,
					real_time_multiplier=real_time_multiplier,
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
					artist_separator=artist_separator
				)
				smart.type = "track"
				smart.track = track

			elif "album/" in link:
				if not "spotify.com" in link:
					raise InvalidLink(link)
				album = self.download_album(
					link,
					output_dir=output_dir,
					quality_download=quality_download,
					recursive_quality=recursive_quality,
					recursive_download=recursive_download,
					not_interface=not_interface,
					make_zip=make_zip,
					real_time_dl=real_time_dl,
					real_time_multiplier=real_time_multiplier,
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
					artist_separator=artist_separator
				)
				smart.type = "album"
				smart.album = album

			elif "playlist/" in link:
				if not "spotify.com" in link:
					raise InvalidLink(link)
				playlist = self.download_playlist(
					link,
					output_dir=output_dir,
					quality_download=quality_download,
					recursive_quality=recursive_quality,
					recursive_download=recursive_download,
					not_interface=not_interface,
					make_zip=make_zip,
					real_time_dl=real_time_dl,
					real_time_multiplier=real_time_multiplier,
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
					artist_separator=artist_separator
				)
				smart.type = "playlist"
				smart.playlist = playlist

			elif "episode/" in link:
				if not "spotify.com" in link:
					raise InvalidLink(link)
				episode = self.download_episode(
					link,
					output_dir=output_dir,
					quality_download=quality_download,
					recursive_quality=recursive_quality,
					recursive_download=recursive_download,
					not_interface=not_interface,
					real_time_dl=real_time_dl,
					real_time_multiplier=real_time_multiplier,
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
					artist_separator=artist_separator
				)
				smart.type = "episode"
				smart.episode = episode

			return smart
		except Exception as e:
			logger.error(f"Failed to perform smart download: {str(e)}")
			traceback.print_exc()
			raise e
