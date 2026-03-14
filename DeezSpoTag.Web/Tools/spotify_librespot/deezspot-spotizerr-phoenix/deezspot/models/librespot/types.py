#!/usr/bin/python3

from dataclasses import dataclass, field
from typing import Optional, Dict, Any, List


def _str(v: Any) -> Optional[str]:
	if v is None:
		return None
	try:
		s = str(v)
		return s
	except Exception:
		return None


def _int(v: Any) -> Optional[int]:
	try:
		if v is None:
			return None
		return int(v)
	except Exception:
		return None


def _bool(v: Any) -> Optional[bool]:
	if isinstance(v, bool):
		return v
	if v in ("true", "True", "1", 1):
		return True
	if v in ("false", "False", "0", 0):
		return False
	return None


def _list_str(v: Any) -> Optional[List[str]]:
	if v is None:
		return []
	if isinstance(v, list):
		out: List[str] = []
		for it in v:
			s = _str(it)
			if s is not None:
				out.append(s)
		return out
	return []


@dataclass
class ExternalUrls:
	spotify: Optional[str] = None

	@staticmethod
	def from_dict(obj: Any) -> "ExternalUrls":
		if not isinstance(obj, dict):
			return ExternalUrls()
		return ExternalUrls(
			spotify=_str(obj.get("spotify"))
		)

	def to_dict(self) -> Dict[str, Any]:
		return {"spotify": self.spotify} if self.spotify else {}


@dataclass
class Image:
	url: str
	width: int = 0
	height: int = 0

	@staticmethod
	def from_dict(obj: Any) -> Optional["Image"]:
		if not isinstance(obj, dict):
			return None
		url = _str(obj.get("url"))
		if not url:
			return None
		w = _int(obj.get("width")) or 0
		h = _int(obj.get("height")) or 0
		return Image(url=url, width=w, height=h)

	def to_dict(self) -> Dict[str, Any]:
		return {"url": self.url, "width": self.width, "height": self.height}


@dataclass
class ArtistRef:
	id: Optional[str] = None
	name: str = ""
	type: str = "artist"
	uri: Optional[str] = None
	external_urls: ExternalUrls = field(default_factory=ExternalUrls)

	@staticmethod
	def from_dict(obj: Any) -> "ArtistRef":
		if not isinstance(obj, dict):
			return ArtistRef()
		return ArtistRef(
			id=_str(obj.get("id")),
			name=_str(obj.get("name")) or "",
			type=_str(obj.get("type")) or "artist",
			uri=_str(obj.get("uri")),
			external_urls=ExternalUrls.from_dict(obj.get("external_urls", {})),
		)

	def to_dict(self) -> Dict[str, Any]:
		out = {
			"id": self.id,
			"name": self.name,
			"type": self.type,
			"uri": self.uri,
			"external_urls": self.external_urls.to_dict()
		}
		return {k: v for k, v in out.items() if v not in (None, {}, [], "")}


@dataclass
class AlbumRef:
	id: Optional[str] = None
	name: Optional[str] = None
	type: str = "album"
	uri: Optional[str] = None
	external_urls: ExternalUrls = field(default_factory=ExternalUrls)
	images: Optional[List[Image]] = None

	@staticmethod
	def from_dict(obj: Any) -> "AlbumRef":
		if not isinstance(obj, dict):
			return AlbumRef()
		imgs: List[Image] = []
		for im in obj.get("images", []) or []:
			im_obj = Image.from_dict(im)
			if im_obj:
				imgs.append(im_obj)
		return AlbumRef(
			id=_str(obj.get("id")),
			name=_str(obj.get("name")),
			type=_str(obj.get("type")) or "album",
			uri=_str(obj.get("uri")),
			external_urls=ExternalUrls.from_dict(obj.get("external_urls", {})),
			images=imgs or None,
		)

	def to_dict(self) -> Dict[str, Any]:
		out = {
			"id": self.id,
			"name": self.name,
			"type": self.type,
			"uri": self.uri,
			"external_urls": self.external_urls.to_dict(),
			"images": [im.to_dict() for im in (self.images or [])]
		}
		return {k: v for k, v in out.items() if v not in (None, {}, [], "")} 