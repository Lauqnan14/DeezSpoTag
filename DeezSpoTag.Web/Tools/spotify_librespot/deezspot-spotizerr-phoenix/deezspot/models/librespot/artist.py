#!/usr/bin/python3

from dataclasses import dataclass, field
from typing import Optional, Dict, Any, List

from .types import ExternalUrls, Image, _str, _int


@dataclass
class Artist:
	id: Optional[str] = None
	name: Optional[str] = None
	uri: Optional[str] = None
	type: str = "artist"
	genres: List[str] = field(default_factory=list)
	images: Optional[List[Image]] = None
	popularity: Optional[int] = None
	external_urls: ExternalUrls = field(default_factory=ExternalUrls)
	album_group: List[str] = field(default_factory=list)
	single_group: List[str] = field(default_factory=list)
	compilation_group: List[str] = field(default_factory=list)
	appears_on_group: List[str] = field(default_factory=list)

	@staticmethod
	def from_dict(obj: Any) -> "Artist":
		if not isinstance(obj, dict):
			return Artist()
		imgs: List[Image] = []
		for im in obj.get("images", []) or []:
			im_obj = Image.from_dict(im)
			if im_obj:
				imgs.append(im_obj)
		return Artist(
			id=_str(obj.get("id")),
			name=_str(obj.get("name")),
			uri=_str(obj.get("uri")),
			type=_str(obj.get("type")) or "artist",
			genres=list(obj.get("genres", []) or []),
			images=imgs or None,
			popularity=_int(obj.get("popularity")),
			external_urls=ExternalUrls.from_dict(obj.get("external_urls", {})),
			album_group=list(obj.get("album_group", []) or []),
			single_group=list(obj.get("single_group", []) or []),
			compilation_group=list(obj.get("compilation_group", []) or []),
			appears_on_group=list(obj.get("appears_on_group", []) or []),
		)

	def to_dict(self) -> Dict[str, Any]:
		out = {
			"id": self.id,
			"name": self.name,
			"uri": self.uri,
			"type": self.type,
			"genres": self.genres or [],
			"images": [im.to_dict() for im in (self.images or [])],
			"popularity": self.popularity,
			"external_urls": self.external_urls.to_dict(),
			"album_group": self.album_group or [],
			"single_group": self.single_group or [],
			"compilation_group": self.compilation_group or [],
			"appears_on_group": self.appears_on_group or [],
		}
		return {k: v for k, v in out.items() if v not in (None, {}, [], "")} 