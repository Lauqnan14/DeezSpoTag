#!/usr/bin/python3

from dataclasses import dataclass, field
from typing import Optional, Dict, Any, List

from .types import ExternalUrls, Image, ArtistRef, AlbumRef, _str, _int, _bool


@dataclass
class Track:
	id: Optional[str] = None
	name: Optional[str] = None
	uri: Optional[str] = None
	type: str = "track"
	duration_ms: Optional[int] = None
	explicit: Optional[bool] = None
	track_number: Optional[int] = None
	disc_number: Optional[int] = None
	popularity: Optional[int] = None
	preview_url: Optional[str] = None
	earliest_live_timestamp: Optional[int] = None
	has_lyrics: Optional[bool] = None
	licensor_uuid: Optional[str] = None
	external_urls: ExternalUrls = field(default_factory=ExternalUrls)
	external_ids: Dict[str, str] = field(default_factory=dict)
	available_markets: Optional[List[str]] = None
	artists: List[ArtistRef] = field(default_factory=list)
	album: Optional[AlbumRef] = None

	@staticmethod
	def from_dict(obj: Any) -> "Track":
		if not isinstance(obj, dict):
			return Track()
		artists: List[ArtistRef] = []
		for a in obj.get("artists", []) or []:
			artists.append(ArtistRef.from_dict(a))
		album_ref = None
		if isinstance(obj.get("album"), dict):
			album_ref = AlbumRef.from_dict(obj.get("album"))
		return Track(
			id=_str(obj.get("id")),
			name=_str(obj.get("name")),
			uri=_str(obj.get("uri")),
			type=_str(obj.get("type")) or "track",
			duration_ms=_int(obj.get("duration_ms")),
			explicit=_bool(obj.get("explicit")),
			track_number=_int(obj.get("track_number")),
			disc_number=_int(obj.get("disc_number")),
			popularity=_int(obj.get("popularity")),
			preview_url=_str(obj.get("preview_url")),
			earliest_live_timestamp=_int(obj.get("earliest_live_timestamp")),
			has_lyrics=_bool(obj.get("has_lyrics")),
			licensor_uuid=_str(obj.get("licensor_uuid")),
			external_urls=ExternalUrls.from_dict(obj.get("external_urls", {})),
			external_ids=dict(obj.get("external_ids", {}) or {}),
			available_markets=list(obj.get("available_markets", []) or []),
			artists=artists,
			album=album_ref,
		)

	def to_dict(self) -> Dict[str, Any]:
		out = {
			"id": self.id,
			"name": self.name,
			"uri": self.uri,
			"type": self.type,
			"duration_ms": self.duration_ms,
			"explicit": self.explicit,
			"track_number": self.track_number,
			"disc_number": self.disc_number,
			"popularity": self.popularity,
			"preview_url": self.preview_url,
			"earliest_live_timestamp": self.earliest_live_timestamp,
			"has_lyrics": self.has_lyrics,
			"licensor_uuid": self.licensor_uuid,
			"external_urls": self.external_urls.to_dict(),
			"external_ids": self.external_ids or {},
			"available_markets": self.available_markets or [],
			"artists": [a.to_dict() for a in (self.artists or [])],
			"album": self.album.to_dict() if self.album else None,
		}
		return {k: v for k, v in out.items() if v not in (None, {}, [], "")} 