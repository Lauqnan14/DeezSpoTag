#!/usr/bin/python3

import requests
from requests import get as req_get
from deezspot.exceptions import NoDataApi
from deezspot.libutils.logging_utils import logger
from .__dee_api__ import tracking, tracking_album, tracking_playlist

class API:
	__api_link = "https://api.deezer.com/"
	__cover = "https://e-cdns-images.dzcdn.net/images/cover/%s/{}-000000-80-0-0.jpg"
	__album_cache = {}
	headers = {
		"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36"
	}

	@classmethod
	def __get_api(cls, url, params=None):
		try:
			response = req_get(url, headers=cls.headers, params=params)
			response.raise_for_status()
			data = response.json()
			if data.get("error"):
				logger.error(f"Deezer API error for url {url}: {data['error']}")
			return data
		except requests.exceptions.RequestException as e:
			logger.error(f"Failed to get API data from {url}: {str(e)}")
			raise

	@classmethod
	def get_track(cls, track_id):
		url = f"{cls.__api_link}track/{track_id}"
		infos = cls.__get_api(url)
		
		if infos and infos.get('album') and infos.get('album', {}).get('id'):
			album_id = infos['album']['id']
			full_album_json = cls.__album_cache.get(album_id)

			if not full_album_json:
				try:
					album_url = f"{cls.__api_link}album/{album_id}"
					full_album_json = cls.__get_api(album_url)
					if full_album_json:
						cls.__album_cache[album_id] = full_album_json
				except Exception as e:
					logger.warning(f"Could not fetch full album details for album {album_id}: {e}")
					full_album_json = None
			
			if full_album_json:
				album_data = infos.setdefault('album', {})
				if 'genres' in full_album_json:
					album_data['genres'] = full_album_json.get('genres')
					infos['genres'] = full_album_json.get('genres')
				if 'nb_tracks' in full_album_json:
					album_data['nb_tracks'] = full_album_json.get('nb_tracks')
				if 'record_type' in full_album_json:
					album_data['record_type'] = full_album_json.get('record_type')
				if 'contributors' in full_album_json:
					album_data['contributors'] = full_album_json.get('contributors')
					# If track doesn't have contributors but album does, use album contributors
					if 'contributors' not in infos:
						infos['contributors'] = full_album_json.get('contributors')

		return tracking(infos)

	@classmethod
	def get_track_json(cls, track_id_or_isrc: str) -> dict:
		"""Return raw Deezer track JSON. Accepts numeric id or 'isrc:CODE'."""
		url = f"{cls.__api_link}track/{track_id_or_isrc}"
		return cls.__get_api(url)

	@classmethod
	def search_tracks_raw(cls, query: str, limit: int = 25) -> list[dict]:
		"""Return raw track objects from search for more complete fields (readable, rank, etc.)."""
		url = f"{cls.__api_link}search/track"
		params = {"q": query, "limit": limit}
		infos = cls.__get_api(url, params=params)
		if infos.get('total', 0) == 0:
			raise NoDataApi(query)
		return infos.get('data', [])

	@classmethod
	def search_albums_raw(cls, query: str, limit: int = 25) -> list[dict]:
		"""Return raw album objects from search to allow title similarity checks."""
		url = f"{cls.__api_link}search/album"
		params = {"q": query, "limit": limit}
		infos = cls.__get_api(url, params=params)
		if infos.get('total', 0) == 0:
			raise NoDataApi(query)
		return infos.get('data', [])

	@classmethod
	def get_album_json(cls, album_id_or_upc: str) -> dict:
		"""Return raw album JSON. Accepts numeric id or 'upc:CODE'."""
		url = f"{cls.__api_link}album/{album_id_or_upc}"
		return cls.__get_api(url)

	@classmethod
	def get_album(cls, album_id):
		url = f"{cls.__api_link}album/{album_id}"
		infos = cls.__get_api(url)

		if infos.get("error"):
			logger.error(f"Deezer API error when fetching album {album_id}: {infos.get('error')}")
			return tracking_album(infos)

		# After fetching with UPC, we get the numeric album ID in the response.
		numeric_album_id = infos.get('id')
		if not numeric_album_id:
			logger.error(f"Could not get numeric album ID for {album_id}")
			return tracking_album(infos)
		
		# Get detailed track information from the dedicated tracks endpoint
		tracks_url = f"{cls.__api_link}album/{numeric_album_id}/tracks?limit=100"
		detailed_tracks = []
		
		try:
			tracks_response = cls.__get_api(tracks_url)
			if tracks_response and 'data' in tracks_response:
				detailed_tracks = tracks_response['data']
				
				# Handle pagination for albums with more than 100 tracks
				next_url = tracks_response.get('next')
				while next_url:
					try:
						next_data = cls.__get_api(next_url)
						if 'data' in next_data:
							detailed_tracks.extend(next_data['data'])
							next_url = next_data.get('next')
						else:
							break
					except Exception as e:
						logger.error(f"Error fetching next page for album tracks: {str(e)}")
						break
				
				# Replace the simplified track data in album response with detailed track data
				if 'tracks' in infos:
					infos['tracks']['data'] = detailed_tracks
					
				logger.info(f"Fetched {len(detailed_tracks)} detailed tracks for album {numeric_album_id}")
		except Exception as e:
			logger.warning(f"Failed to fetch detailed tracks for album {numeric_album_id}: {e}")
			# Continue with regular album tracks if detailed fetch fails
			
			# Handle pagination for regular album endpoint if detailed fetch failed
			if infos.get('nb_tracks', 0) > 25 and 'tracks' in infos and 'next' in infos['tracks']:
				all_tracks = infos['tracks']['data']
				next_url = infos['tracks']['next']
				while next_url:
					try:
						next_data = cls.__get_api(next_url)
						if 'data' in next_data:
							all_tracks.extend(next_data['data'])
							next_url = next_data.get('next')
						else:
							break
					except Exception as e:
						logger.error(f"Error fetching next page for album tracks: {str(e)}")
						break
				infos['tracks']['data'] = all_tracks
		
		return tracking_album(infos)

	@classmethod
	def get_playlist(cls, playlist_id):
		url = f"{cls.__api_link}playlist/{playlist_id}"
		infos = cls.__get_api(url)
		if 'tracks' in infos and 'next' in infos['tracks']:
			all_tracks = infos['tracks']['data']
			next_url = infos['tracks']['next']
			while next_url:
				try:
					next_data = cls.__get_api(next_url)
					if 'data' in next_data:
						all_tracks.extend(next_data['data'])
						next_url = next_data.get('next')
					else:
						break
				except Exception as e:
					logger.error(f"Error fetching next page for playlist tracks: {str(e)}")
					break
			infos['tracks']['data'] = all_tracks
		return tracking_playlist(infos)

	@classmethod
	def get_episode(cls, episode_id):
		url = f"{cls.__api_link}episode/{episode_id}"
		infos = cls.__get_api(url)
		return infos

	@classmethod
	def get_artist(cls, ids):
		url = f"{cls.__api_link}artist/{ids}"
		infos = cls.__get_api(url)
		return infos

	@classmethod
	def get_artist_top_tracks(cls, ids, limit = 25):
		url = f"{cls.__api_link}artist/{ids}/top?limit={limit}"
		infos = cls.__get_api(url)
		return infos

	@classmethod
	def search(cls, query, limit=25, search_type="track"):
		url = f"{cls.__api_link}search/{search_type}"
		params = {
			"q": query,
			"limit": limit
		}
		infos = cls.__get_api(url, params=params)

		if infos['total'] == 0:
			raise NoDataApi(query)
		
		if search_type == "track":
			return [tracking(t) for t in infos.get('data', []) if t]
		elif search_type == "album":
			return [tracking_album(a) for a in infos.get('data', []) if a]
		elif search_type == "playlist":
			return [tracking_playlist(p) for p in infos.get('data', []) if p]
		
		return infos.get('data', [])

	@classmethod
	def get_img_url(cls, md5_image, size = "1200x1200"):
		cover = cls.__cover.format(size)
		image_url = cover % md5_image
		return image_url

	@classmethod
	def choose_img(cls, md5_image, size = "1200x1200"):
		image_url = cls.get_img_url(md5_image, size)
		image = req_get(image_url).content
		if len(image) == 13:
			image_url = cls.get_img_url("", size)
			image = req_get(image_url).content
		return image
