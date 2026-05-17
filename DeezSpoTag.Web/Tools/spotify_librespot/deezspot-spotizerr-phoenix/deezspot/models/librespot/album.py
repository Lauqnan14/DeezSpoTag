#!/usr/bin/python3

from dataclasses import dataclass, field
from typing import Dict, Any, List

from .types import ExternalUrls, Image, ArtistRef, _str, _int
from .track import Track as TrackModel


@dataclass
class Album:
	id: str | None = None
	name: str | None = None
	uri: str | None = None
	type: str = "album"
	album_type: str | None = None
	release_date: str | None = None
	release_date_precision: str | None = None
	total_tracks: int | None = None
	label: str | None = None
	popularity: int | None = None
	external_urls: ExternalUrls = field(default_factory=ExternalUrls)
	external_ids: Dict[str, str] = field(default_factory=dict)
	available_markets: List[str] | None = None
	images: List[Image] | None = None
	artists: List[ArtistRef] = field(default_factory=list)
	tracks: List[str | TrackModel] | None = None
	copyrights: List[Dict[str, Any]] | None = None

	@staticmethod
	def _parse_images(obj: Dict[str, Any]) -> List[Image] | None:
		imgs: List[Image] = []
		for im in obj.get("images", []) or []:
			im_obj = Image.from_dict(im)
			if im_obj:
				imgs.append(im_obj)
		return imgs or None

	@staticmethod
	def _parse_artists(obj: Dict[str, Any]) -> List[ArtistRef]:
		artists: List[ArtistRef] = []
		for artist_item in obj.get("artists", []) or []:
			artists.append(ArtistRef.from_dict(artist_item))
		return artists

	@staticmethod
	def _parse_tracks(obj: Dict[str, Any]) -> List[str | TrackModel] | None:
		if not isinstance(obj.get("tracks"), list):
			return None
		tracks_in: List[str | TrackModel] = []
		for track_item in obj.get("tracks"):
			if isinstance(track_item, dict):
				tracks_in.append(TrackModel.from_dict(track_item))
				continue
			track_string = _str(track_item)
			if track_string:
				tracks_in.append(track_string)
		return tracks_in or None

	@staticmethod
	def from_dict(obj: Any) -> "Album":
		if not isinstance(obj, dict):
			return Album()

		imgs = Album._parse_images(obj)
		artists = Album._parse_artists(obj)
		tracks_in = Album._parse_tracks(obj)

		return Album(
			id=_str(obj.get("id")),
			name=_str(obj.get("name")),
			uri=_str(obj.get("uri")),
			type=_str(obj.get("type")) or "album",
			album_type=_str(obj.get("album_type")),
			release_date=_str(obj.get("release_date")),
			release_date_precision=_str(obj.get("release_date_precision")),
			total_tracks=_int(obj.get("total_tracks")),
			label=_str(obj.get("label")),
			popularity=_int(obj.get("popularity")),
			external_urls=ExternalUrls.from_dict(obj.get("external_urls", {})),
			external_ids=dict(obj.get("external_ids", {}) or {}),
			available_markets=list(obj.get("available_markets", []) or []),
			images=imgs,
			artists=artists,
			tracks=tracks_in,
			copyrights=list(obj.get("copyrights", []) or []),
		)

	def to_dict(self) -> Dict[str, Any]:
		out = {
			"id": self.id,
			"name": self.name,
			"uri": self.uri,
			"type": self.type,
			"album_type": self.album_type,
			"release_date": self.release_date,
			"release_date_precision": self.release_date_precision,
			"total_tracks": self.total_tracks,
			"label": self.label,
			"popularity": self.popularity,
			"external_urls": self.external_urls.to_dict(),
			"external_ids": self.external_ids or {},
			"available_markets": self.available_markets or [],
			"images": [im.to_dict() for im in (self.images or [])],
			"artists": [a.to_dict() for a in (self.artists or [])],
			"tracks": [t.to_dict() if isinstance(t, TrackModel) else t for t in (self.tracks or [])],
			"copyrights": self.copyrights or [],
		}
		return {k: v for k, v in out.items() if v not in (None, {}, [], "")} 
