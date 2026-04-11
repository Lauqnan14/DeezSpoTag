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
	def _get_album_id_from_track_payload(cls, infos: dict) -> int | None:
		album_data = infos.get('album') or {}
		return album_data.get('id')

	@classmethod
	def _fetch_full_album_json(cls, album_id: int) -> dict | None:
		full_album_json = cls.__album_cache.get(album_id)
		if full_album_json:
			return full_album_json
		try:
			album_url = f"{cls.__api_link}album/{album_id}"
			full_album_json = cls.__get_api(album_url)
			if full_album_json:
				cls.__album_cache[album_id] = full_album_json
			return full_album_json
		except Exception as e:
			logger.warning(f"Could not fetch full album details for album {album_id}: {e}")
			return None

	@classmethod
	def _merge_track_with_full_album(cls, infos: dict, full_album_json: dict) -> None:
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
			if 'contributors' not in infos:
				infos['contributors'] = full_album_json.get('contributors')

	@classmethod
	def get_track(cls, track_id):
		url = f"{cls.__api_link}track/{track_id}"
		infos = cls.__get_api(url)
		album_id = cls._get_album_id_from_track_payload(infos) if infos else None
		if album_id:
			full_album_json = cls._fetch_full_album_json(album_id)
			if full_album_json:
				cls._merge_track_with_full_album(infos, full_album_json)

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
	def _fetch_paginated_data(cls, first_page: dict, next_key: str = 'next') -> list[dict]:
		items = list(first_page.get('data', []))
		next_url = first_page.get(next_key)
		while next_url:
			try:
				next_data = cls.__get_api(next_url)
			except Exception as e:
				logger.error(f"Error fetching next page for album tracks: {str(e)}")
				break
			page_items = next_data.get('data')
			if page_items is None:
				break
			items.extend(page_items)
			next_url = next_data.get(next_key)
		return items

	@classmethod
	def _load_detailed_album_tracks(cls, numeric_album_id: int) -> list[dict]:
		tracks_url = f"{cls.__api_link}album/{numeric_album_id}/tracks?limit=100"
		tracks_response = cls.__get_api(tracks_url)
		if not tracks_response or 'data' not in tracks_response:
			return []
		return cls._fetch_paginated_data(tracks_response)

	@classmethod
	def _apply_fallback_album_tracks(cls, infos: dict) -> None:
		if infos.get('nb_tracks', 0) <= 25:
			return
		tracks_data = infos.get('tracks') or {}
		if 'next' not in tracks_data or 'data' not in tracks_data:
			return
		expanded_tracks = cls._fetch_paginated_data(tracks_data)
		infos['tracks']['data'] = expanded_tracks

	@classmethod
	def _populate_album_tracks(cls, infos: dict, numeric_album_id: int) -> None:
		try:
			detailed_tracks = cls._load_detailed_album_tracks(numeric_album_id)
			if detailed_tracks:
				if 'tracks' in infos:
					infos['tracks']['data'] = detailed_tracks
				logger.info(
					f"Fetched {len(detailed_tracks)} detailed tracks for album {numeric_album_id}"
				)
		except Exception as e:
			logger.warning(f"Failed to fetch detailed tracks for album {numeric_album_id}: {e}")
			cls._apply_fallback_album_tracks(infos)

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

		cls._populate_album_tracks(infos, numeric_album_id)
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
