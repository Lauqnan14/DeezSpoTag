#!/usr/bin/python3

from dataclasses import dataclass, field
from typing import Optional, Dict, Any, List, Union

from .types import ExternalUrls, Image, ArtistRef, _str, _int
from .track import Track as TrackModel


@dataclass
class Album:
	id: Optional[str] = None
	name: Optional[str] = None
	uri: Optional[str] = None
	type: str = "album"
	album_type: Optional[str] = None
	release_date: Optional[str] = None
	release_date_precision: Optional[str] = None
	total_tracks: Optional[int] = None
	label: Optional[str] = None
	popularity: Optional[int] = None
	external_urls: ExternalUrls = field(default_factory=ExternalUrls)
	external_ids: Dict[str, str] = field(default_factory=dict)
	available_markets: Optional[List[str]] = None
	images: Optional[List[Image]] = None
	artists: List[ArtistRef] = field(default_factory=list)
	tracks: Optional[List[Union[str, TrackModel]]] = None
	copyrights: Optional[List[Dict[str, Any]]] = None

	@staticmethod
	def from_dict(obj: Any) -> "Album":
		if not isinstance(obj, dict):
			return Album()
		imgs: List[Image] = []
		for im in obj.get("images", []) or []:
			im_obj = Image.from_dict(im)
			if im_obj:
				imgs.append(im_obj)
		artists: List[ArtistRef] = []
		for a in obj.get("artists", []) or []:
			artists.append(ArtistRef.from_dict(a))
		# Tracks can be base62 strings or full track dicts
		tracks_in: List[Union[str, TrackModel]] = []
		if isinstance(obj.get("tracks"), list):
			for t in obj.get("tracks"):
				if isinstance(t, dict):
					tracks_in.append(TrackModel.from_dict(t))
				else:
					ts = _str(t)
					if ts:
						tracks_in.append(ts)
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
			images=imgs or None,
			artists=artists,
			tracks=tracks_in or None,
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