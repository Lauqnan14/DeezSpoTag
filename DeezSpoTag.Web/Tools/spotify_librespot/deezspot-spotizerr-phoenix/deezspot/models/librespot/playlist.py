#!/usr/bin/python3

from dataclasses import dataclass, field
from typing import Optional, Dict, Any, List, Union

from .types import ExternalUrls, _str, _int
from .track import Track as TrackModel


@dataclass
class UserMini:
	id: Optional[str] = None
	type: str = "user"
	uri: Optional[str] = None
	display_name: Optional[str] = None
	external_urls: ExternalUrls = field(default_factory=ExternalUrls)

	@staticmethod
	def from_dict(obj: Any) -> "UserMini":
		if not isinstance(obj, dict):
			return UserMini()
		return UserMini(
			id=_str(obj.get("id")),
			type=_str(obj.get("type")) or "user",
			uri=_str(obj.get("uri")),
			display_name=_str(obj.get("display_name")),
			external_urls=ExternalUrls.from_dict(obj.get("external_urls", {})),
		)

	def to_dict(self) -> Dict[str, Any]:
		out = {
			"id": self.id,
			"type": self.type,
			"uri": self.uri,
			"display_name": self.display_name,
			"external_urls": self.external_urls.to_dict(),
		}
		return {k: v for k, v in out.items() if v not in (None, {}, [], "")}


@dataclass
class TrackStub:
	id: Optional[str] = None
	type: str = "track"
	uri: Optional[str] = None
	external_urls: ExternalUrls = field(default_factory=ExternalUrls)

	@staticmethod
	def from_dict(obj: Any) -> "TrackStub":
		if not isinstance(obj, dict):
			return TrackStub()
		return TrackStub(
			id=_str(obj.get("id")),
			type=_str(obj.get("type")) or "track",
			uri=_str(obj.get("uri")),
			external_urls=ExternalUrls.from_dict(obj.get("external_urls", {})),
		)

	def to_dict(self) -> Dict[str, Any]:
		out = {
			"id": self.id,
			"type": self.type,
			"uri": self.uri,
			"external_urls": self.external_urls.to_dict(),
		}
		return {k: v for k, v in out.items() if v not in (None, {}, [], "")}


@dataclass
class PlaylistItem:
	added_at: Optional[str] = None
	added_by: UserMini = field(default_factory=UserMini)
	is_local: bool = False
	track: Optional[Union[TrackModel, TrackStub]] = None
	item_id: Optional[str] = None

	@staticmethod
	def from_dict(obj: Any) -> "PlaylistItem":
		if not isinstance(obj, dict):
			return PlaylistItem()
		track_obj = None
		trk = obj.get("track")
		if isinstance(trk, dict):
			if trk.get("duration_ms") is not None:
				track_obj = TrackModel.from_dict(trk)
			else:
				track_obj = TrackStub.from_dict(trk)
		return PlaylistItem(
			added_at=_str(obj.get("added_at")),
			added_by=UserMini.from_dict(obj.get("added_by", {})),
			is_local=bool(obj.get("is_local", False)),
			track=track_obj,
			item_id=_str(obj.get("item_id")),
		)

	def to_dict(self) -> Dict[str, Any]:
		out = {
			"added_at": self.added_at,
			"added_by": self.added_by.to_dict(),
			"is_local": self.is_local,
			"track": self.track.to_dict() if hasattr(self.track, 'to_dict') and self.track else None,
			"item_id": self.item_id,
		}
		return {k: v for k, v in out.items() if v not in (None, {}, [], "")}


@dataclass
class TracksPage:
	offset: int = 0
	total: int = 0
	items: List[PlaylistItem] = field(default_factory=list)

	@staticmethod
	def from_dict(obj: Any) -> "TracksPage":
		if not isinstance(obj, dict):
			return TracksPage(items=[])
		items: List[PlaylistItem] = []
		for it in obj.get("items", []) or []:
			items.append(PlaylistItem.from_dict(it))
		return TracksPage(
			offset=_int(obj.get("offset")) or 0,
			total=_int(obj.get("total")) or len(items),
			items=items,
		)

	def to_dict(self) -> Dict[str, Any]:
		return {
			"offset": self.offset,
			"total": self.total,
			"items": [it.to_dict() for it in (self.items or [])]
		}


@dataclass
class Owner:
	id: Optional[str] = None
	type: str = "user"
	uri: Optional[str] = None
	display_name: Optional[str] = None
	external_urls: ExternalUrls = field(default_factory=ExternalUrls)

	@staticmethod
	def from_dict(obj: Any) -> "Owner":
		if not isinstance(obj, dict):
			return Owner()
		return Owner(
			id=_str(obj.get("id")),
			type=_str(obj.get("type")) or "user",
			uri=_str(obj.get("uri")),
			display_name=_str(obj.get("display_name")),
			external_urls=ExternalUrls.from_dict(obj.get("external_urls", {})),
		)

	def to_dict(self) -> Dict[str, Any]:
		out = {
			"id": self.id,
			"type": self.type,
			"uri": self.uri,
			"display_name": self.display_name,
			"external_urls": self.external_urls.to_dict(),
		}
		return {k: v for k, v in out.items() if v not in (None, {}, [], "")}


@dataclass
class Playlist:
	name: Optional[str] = None
	description: Optional[str] = None
	collaborative: Optional[bool] = None
	picture: Optional[str] = None
	owner: Owner = field(default_factory=Owner)
	snapshot_id: Optional[str] = None
	length: Optional[int] = None
	tracks: TracksPage = field(default_factory=lambda: TracksPage(items=[]))
	type: str = "playlist"

	@staticmethod
	def from_dict(obj: Any) -> "Playlist":
		if not isinstance(obj, dict):
			return Playlist(tracks=TracksPage(items=[]))
		return Playlist(
			name=_str(obj.get("name")),
			description=_str(obj.get("description")),
			collaborative=bool(obj.get("collaborative")) if obj.get("collaborative") is not None else None,
			picture=_str(obj.get("picture")),
			owner=Owner.from_dict(obj.get("owner", {})),
			snapshot_id=_str(obj.get("snapshot_id")),
			length=_int(obj.get("length")),
			tracks=TracksPage.from_dict(obj.get("tracks", {})),
			type=_str(obj.get("type")) or "playlist",
		)

	def to_dict(self) -> Dict[str, Any]:
		out = {
			"name": self.name,
			"description": self.description,
			"collaborative": self.collaborative,
			"picture": self.picture,
			"owner": self.owner.to_dict(),
			"snapshot_id": self.snapshot_id,
			"length": self.length,
			"tracks": self.tracks.to_dict(),
			"type": self.type,
		}
		return {k: v for k, v in out.items() if v not in (None, {}, [], "")} 