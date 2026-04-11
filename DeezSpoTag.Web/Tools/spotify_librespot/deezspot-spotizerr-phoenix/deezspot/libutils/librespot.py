from __future__ import annotations

import base64
import datetime
import time
from concurrent.futures import ThreadPoolExecutor
from typing import Any, Dict, List, Optional

from google.protobuf.descriptor import FieldDescriptor
from google.protobuf.message import Message

from librespot.core import Session, SearchManager
from librespot.metadata import AlbumId, ArtistId, PlaylistId, TrackId
from librespot import util
from librespot.proto import Metadata_pb2 as Metadata
from librespot.proto import Playlist4External_pb2 as P4

SPOTIFY_TRACK_URI_PREFIX = "spotify:track:"


class LibrespotClient:
    """
    Thin wrapper around the internal librespot API, exposing convenient helpers that
    return Web API-like dictionaries for albums, tracks, artists, and playlists.

    Typical usage:

        client = LibrespotClient(stored_credentials_path="/path/to/credentials.json")
        album = client.get_album("spotify:album:...", include_tracks=True)
        track = client.get_track("...base62...")
        playlist = client.get_playlist("spotify:playlist:...", expand_items=True)
        client.close()
    """

    def __init__(
        self,
        stored_credentials_path: Optional[str] = None,
        session: Optional[Session] = None,
        max_workers: int = 16,
    ) -> None:
        self._session: Session = session if session is not None else self._create_session(stored_credentials_path)
        self._max_workers: int = max(1, min(32, max_workers))
        self._track_object_cache: Dict[str, Optional[Dict[str, Any]]] = {}

    # ---------- Public API ----------

    def close(self) -> None:
        if hasattr(self, "_session") and self._session is not None:
            try:
                self._session.close()
            except Exception:
                pass

    def get_album(self, album: str | AlbumId, include_tracks: bool = False) -> Dict[str, Any]:
        album_id = self._ensure_album_id(album)
        album_proto = self._session.api().get_metadata_4_album(album_id)
        return self._album_proto_to_object(album_proto, include_tracks=include_tracks, for_embed=False)

    def get_track(self, track: str | TrackId) -> Dict[str, Any]:
        track_id = self._ensure_track_id(track)
        track_proto = self._session.api().get_metadata_4_track(track_id)
        return self._track_proto_to_object(track_proto)

    def get_artist(self, artist: str | ArtistId) -> Dict[str, Any]:
        artist_id = self._ensure_artist_id(artist)
        artist_proto = self._session.api().get_metadata_4_artist(artist_id)
        return self._proto_to_full_json(artist_proto)

    def get_playlist(self, playlist: str | PlaylistId, expand_items: bool = False) -> Dict[str, Any]:
        playlist_id = self._ensure_playlist_id(playlist)
        playlist_proto = self._session.api().get_playlist(playlist_id)
        return self._playlist_proto_to_object(playlist_proto, include_track_objects=expand_items)

    def search(
        self,
        query: str,
        limit: int = 10,
        country: Optional[str] = None,
        locale: Optional[str] = None,
        catalogue: Optional[str] = None,
        image_size: Optional[str] = None,
    ) -> Dict[str, Any]:
        """Perform a full-featured search using librespot's SearchManager.

        - country precedence: explicit country > session country code > unset
        - returns the raw JSON-like mapping response provided by librespot
        """
        req = SearchManager.SearchRequest(query).set_limit(limit)
        # Country precedence
        cc = country or self._get_session_country_code()
        if cc:
            req.set_country(cc)
        if locale:
            req.set_locale(locale)
        if catalogue:
            req.set_catalogue(catalogue)
        if image_size:
            req.set_image_size(image_size)
        res = self._session.search().request(req)
        return res

    # ---------- ID parsing helpers ----------

    @staticmethod
    def _extract_open_spotify_id(value: str, segment: str) -> str:
        token = f"open.spotify.com/{segment}/"
        if token not in value:
            return ""
        return value.split(token)[-1].split("?")[0].split("#")[0].strip("/")

    @staticmethod
    def parse_input_id(kind: str, value: str) -> TrackId | AlbumId | ArtistId | PlaylistId:
        s = value.strip()

        parser_map = {
            "track": (SPOTIFY_TRACK_URI_PREFIX, "track", TrackId.from_uri, TrackId.from_base62),
            "album": ("spotify:album:", "album", AlbumId.from_uri, AlbumId.from_base62),
            "artist": ("spotify:artist:", "artist", ArtistId.from_uri, ArtistId.from_base62),
            "playlist": ("spotify:playlist:", "playlist", PlaylistId.from_uri, PlaylistId),
        }

        cfg = parser_map.get(kind)
        if cfg is None:
            raise RuntimeError(f"Unknown kind: {kind}")

        uri_prefix, segment, uri_parser, base_parser = cfg
        if s.startswith(uri_prefix):
            return uri_parser(s)

        extracted = LibrespotClient._extract_open_spotify_id(s, segment)
        if extracted:
            return base_parser(extracted)

        return base_parser(s)

    # ---------- Private: session ----------

    @staticmethod
    def _create_session(stored_credentials_path: Optional[str]) -> Session:
        if not stored_credentials_path:
            raise ValueError("stored_credentials_path is required when no Session is provided")
        conf = (
            Session.Configuration.Builder()
            .set_stored_credential_file(stored_credentials_path)
            .build()
        )
        builder = Session.Builder(conf)
        builder.stored_file(stored_credentials_path)
        last_exc: Optional[Exception] = None
        for attempt in range(1, 4):
            try:
                return builder.create()
            except Exception as exc:
                last_exc = exc
                if attempt < 3:
                    time.sleep(3)
                else:
                    raise last_exc

    def _get_session_country_code(self) -> str:
        try:
            cc = getattr(self._session, "_Session__country_code", None)
            if isinstance(cc, str) and len(cc) == 2:
                return cc
            cc2 = getattr(self._session, "country_code", None)
            if isinstance(cc2, str) and len(cc2) == 2:
                return cc2
        except Exception:
            pass
        return ""

    # ---------- Private: ID coercion ----------

    def _ensure_track_id(self, v: str | TrackId) -> TrackId:
        if isinstance(v, TrackId):
            return v
        return self.parse_input_id("track", v)  # type: ignore[return-value]

    def _ensure_album_id(self, v: str | AlbumId) -> AlbumId:
        if isinstance(v, AlbumId):
            return v
        return self.parse_input_id("album", v)  # type: ignore[return-value]

    def _ensure_artist_id(self, v: str | ArtistId) -> ArtistId:
        if isinstance(v, ArtistId):
            return v
        return self.parse_input_id("artist", v)  # type: ignore[return-value]

    def _ensure_playlist_id(self, v: str | PlaylistId) -> PlaylistId:
        if isinstance(v, PlaylistId):
            return v
        return self.parse_input_id("playlist", v)  # type: ignore[return-value]

    # ---------- Private: conversions ----------

    @staticmethod
    def _bytes_to_base62(b: bytes) -> str:
        try:
            return TrackId.base62.encode(b).decode("ascii")
        except Exception:
            return ""

    def _special_message_json(self, msg: Message, msg_name: str) -> Optional[Any]:
        if msg_name == "Image":
            return self._prune_empty({
                "url": self._image_url_from_file_id(getattr(msg, "file_id", b"")),
                "width": getattr(msg, "width", 0) or 0,
                "height": getattr(msg, "height", 0) or 0,
            })
        if msg_name == "TopTracks":
            track_ids = [self._bytes_to_base62(getattr(track, "gid", b"")) for track in getattr(msg, "track", [])]
            country = getattr(msg, "country", "") or ""
            return self._prune_empty({"country": country or None, "track": track_ids})
        if not hasattr(msg, "album"):
            return None
        albums = getattr(msg, "album", [])
        album_ids = [self._bytes_to_base62(getattr(album, "gid", b"")) for album in albums]
        return {"album": album_ids}

    def _album_group_field_ids(self, value: Any) -> List[str]:
        ids: List[str] = []
        for album_group in value:
            for album in getattr(album_group, "album", []):
                ids.append(self._bytes_to_base62(getattr(album, "gid", b"")))
        return ids

    def _convert_message_field(self, field: Any, value: Any) -> Any:
        if field.type == FieldDescriptor.TYPE_BYTES:
            if field.label == FieldDescriptor.LABEL_REPEATED:
                return [self._bytes_to_base62(v) for v in value]
            return self._bytes_to_base62(value)
        if field.type == FieldDescriptor.TYPE_MESSAGE:
            if field.label == FieldDescriptor.LABEL_REPEATED:
                return [self._proto_to_full_json(v) for v in value]
            return self._proto_to_full_json(value)
        if field.label == FieldDescriptor.LABEL_REPEATED:
            return list(value)
        return value

    def _convert_message_fields(self, msg: Message) -> Dict[str, Any]:
        out: Dict[str, Any] = {}
        group_field_names = ("album_group", "single_group", "compilation_group", "appears_on_group")
        for field, value in msg.ListFields():
            name = field.name
            if name in group_field_names:
                out[name] = self._album_group_field_ids(value)
                continue
            field_name = "id" if name == "gid" else name
            out[field_name] = self._convert_message_field(field, value)
        return out

    def _proto_to_full_json(self, msg: Any) -> Any:
        if isinstance(msg, Message):
            msg_name = msg.DESCRIPTOR.name if hasattr(msg, "DESCRIPTOR") else ""
            special_json = self._special_message_json(msg, msg_name)
            if special_json is not None:
                return special_json
            return self._convert_message_fields(msg)
        if isinstance(msg, (bytes, bytearray)):
            return self._bytes_to_base62(bytes(msg))
        if isinstance(msg, (list, tuple)):
            return [self._proto_to_full_json(v) for v in msg]
        return msg

    @staticmethod
    def _prune_empty(obj: Any) -> Any:
        if isinstance(obj, dict):
            return {k: LibrespotClient._prune_empty(v) for k, v in obj.items() if v not in (None, "", [], {})}
        if isinstance(obj, list):
            return [LibrespotClient._prune_empty(v) for v in obj if v not in (None, "", [], {})]
        return obj

    @staticmethod
    def _image_url_from_file_id(file_id: bytes) -> Optional[str]:
        if not file_id:
            return None
        return f"https://i.scdn.co/image/{util.bytes_to_hex(file_id)}"

    def _get_playlist_picture_url(self, attrs: Any) -> Optional[str]:
        pic = getattr(attrs, "picture", b"") if attrs else b""
        try:
            if not pic:
                return None
            data = bytes(pic)
            image_id: Optional[bytes] = None
            if len(data) >= 26 and data[0] == 0xAB and data[1:4] == b"gpl" and data[4:6] == b"\x00\x00":
                image_id = data[6:26]
            elif len(data) >= 20:
                image_id = data[:20]
            if image_id:
                return f"https://i.scdn.co/image/{util.bytes_to_hex(image_id)}"
        except Exception:
            pass
        return None

    @staticmethod
    def _split_countries(countries: str) -> List[str]:
        if not countries:
            return []
        s = countries.strip()
        if " " in s:
            return [c for c in s.split(" ") if c]
        return [s[i : i + 2] for i in range(0, len(s), 2) if len(s[i : i + 2]) == 2]

    def _restrictions_to_available_markets(self, restrictions: List[Metadata.Restriction]) -> List[str]:
        for r in restrictions:
            allowed = getattr(r, "countries_allowed", "")
            if isinstance(allowed, str) and allowed:
                return self._split_countries(allowed)
        return []

    @staticmethod
    def _external_ids_to_dict(ext_ids: List[Metadata.ExternalId]) -> Dict[str, str]:
        out: Dict[str, str] = {}
        for e in ext_ids:
            t = getattr(e, "type", "").lower()
            v = getattr(e, "id", "")
            if t and v and t not in out:
                out[t] = v
        return out

    @staticmethod
    def _album_type_to_str(a: Metadata.Album) -> str:
        type_str = getattr(a, "type_str", "")
        if isinstance(type_str, str) and type_str:
            return type_str.lower()
        t = getattr(a, "type", None)
        if t is None:
            return "album"
        try:
            mapping = {
                Metadata.Album.ALBUM: "album",
                Metadata.Album.SINGLE: "single",
                Metadata.Album.COMPILATION: "compilation",
                Metadata.Album.EP: "ep",
            }
            return mapping.get(t, "album")
        except Exception:
            return "album"

    @staticmethod
    def _date_to_release_fields(d: Optional[Metadata.Date]) -> (str, str):
        if d is None:
            return "", "day"
        y = getattr(d, "year", 0)
        m = getattr(d, "month", 0)
        day = getattr(d, "day", 0)
        if y and m and day:
            return f"{y:04d}-{m:02d}-{day:02d}", "day"
        if y and m:
            return f"{y:04d}-{m:02d}", "month"
        if y:
            return f"{y:04d}", "year"
        return "", "day"

    def _images_from_group(
        self,
        group: Optional[Metadata.ImageGroup],
        fallback_images: Optional[List[Metadata.Image]] = None,
    ) -> List[Dict[str, Any]]:
        images: List[Dict[str, Any]] = []
        seq = []
        if group is not None:
            seq = getattr(group, "image", [])
        elif fallback_images is not None:
            seq = fallback_images
        for im in seq:
            url = self._image_url_from_file_id(getattr(im, "file_id", b""))
            if not url:
                continue
            width = getattr(im, "width", 0) or 0
            height = getattr(im, "height", 0) or 0
            images.append({"url": url, "width": width, "height": height})
        seen = set()
        uniq: List[Dict[str, Any]] = []
        for it in images:
            u = it.get("url")
            if u in seen:
                continue
            seen.add(u)
            uniq.append(it)
        return uniq

    def _artist_ref_to_object(self, a: Metadata.Artist) -> Dict[str, Any]:
        gid = getattr(a, "gid", b"")
        hex_id = util.bytes_to_hex(gid) if gid else ""
        uri = ""
        base62 = ""
        if hex_id:
            try:
                aid = ArtistId.from_hex(hex_id)
                uri = aid.to_spotify_uri()
                base62 = uri.split(":")[-1]
            except Exception:
                pass
        return {
            "external_urls": {"spotify": f"https://open.spotify.com/artist/{base62}" if base62 else ""},
            "id": base62,
            "name": getattr(a, "name", "") or "",
            "type": "artist",
            "uri": uri or "",
        }

    @staticmethod
    def _album_identity(a: Metadata.Album) -> tuple[str, str]:
        gid = getattr(a, "gid", b"")
        hex_id = util.bytes_to_hex(gid) if gid else ""
        if not hex_id:
            return "", ""
        try:
            album_id = AlbumId.from_hex(hex_id)
            uri = album_id.to_spotify_uri()
            return uri.split(":")[-1], uri
        except Exception:
            return "", ""

    def _album_track_ids(self, a: Metadata.Album) -> List[str]:
        track_ids: List[str] = []
        for disc in getattr(a, "disc", []):
            for track in getattr(disc, "track", []):
                track_gid = getattr(track, "gid", b"")
                if not track_gid:
                    continue
                try:
                    track_id = TrackId.from_hex(util.bytes_to_hex(track_gid))
                    track_uri = track_id.to_spotify_uri()
                    base62 = track_uri.split(":")[-1]
                    if base62:
                        track_ids.append(base62)
                except Exception:
                    continue
        return track_ids

    @staticmethod
    def _track_stub(base62_id: str) -> Dict[str, Any]:
        return {
            "id": base62_id,
            "uri": f"{SPOTIFY_TRACK_URI_PREFIX}{base62_id}",
            "type": "track",
            "external_urls": {"spotify": f"https://open.spotify.com/track/{base62_id}"},
        }

    def _album_tracks_payload(self, track_ids: List[str], include_tracks: bool, for_embed: bool) -> Optional[List[Any]]:
        if for_embed:
            return None
        if not include_tracks or self._session is None or not track_ids:
            return track_ids

        fetched = self._fetch_track_objects(track_ids)
        payload: List[Any] = []
        for base62_id in track_ids:
            track_obj = fetched.get(base62_id)
            payload.append(track_obj if track_obj is not None else self._track_stub(base62_id))
        return payload

    @staticmethod
    def _album_copyrights(a: Metadata.Album) -> List[Dict[str, Any]]:
        return [
            {
                "text": getattr(copyright_item, "text", ""),
                "type": str(getattr(copyright_item, "type", "")),
            }
            for copyright_item in getattr(a, "copyright", [])
        ]

    @staticmethod
    def _album_total_tracks(a: Metadata.Album) -> int:
        return sum(len(getattr(disc, "track", [])) for disc in getattr(a, "disc", []))

    @staticmethod
    def _album_external_urls(base62: str) -> Dict[str, str]:
        if not base62:
            return {}
        return {"spotify": f"https://open.spotify.com/album/{base62}"}

    @staticmethod
    def _track_identity(t: Metadata.Track) -> tuple[str, str]:
        track_gid = getattr(t, "gid", b"")
        if not track_gid:
            return "", ""
        try:
            track_id = TrackId.from_hex(util.bytes_to_hex(track_gid))
            uri = track_id.to_spotify_uri()
            return uri.split(":")[-1], uri
        except Exception:
            return "", ""

    @staticmethod
    def _preview_url_from_track(t: Metadata.Track) -> Optional[str]:
        previews = getattr(t, "preview", [])
        if not previews:
            return None
        preview = previews[0]
        file_id = getattr(preview, "file_id", b"")
        if not file_id:
            return None
        try:
            return f"https://p.scdn.co/mp3-preview/{util.bytes_to_hex(file_id)}"
        except Exception:
            return None

    @staticmethod
    def _licensor_uuid_from_track(t: Metadata.Track) -> Optional[str]:
        licensor = getattr(t, "licensor", None)
        if licensor is None:
            return None
        licensor_uuid = getattr(licensor, "uuid", b"")
        if not licensor_uuid:
            return None
        return util.bytes_to_hex(licensor_uuid)

    def _album_proto_to_object(
        self,
        a: Metadata.Album,
        include_tracks: bool = False,
        for_embed: bool = False,
    ) -> Dict[str, Any]:
        base62, uri = self._album_identity(a)
        available = self._restrictions_to_available_markets(getattr(a, "restriction", []))
        release_date, release_precision = self._date_to_release_fields(getattr(a, "date", None))
        artists = [self._artist_ref_to_object(ar) for ar in getattr(a, "artist", [])]
        track_ids: List[str] = []
        track_list_value: Optional[List[Any]] = None
        if not for_embed:
            track_ids = self._album_track_ids(a)
            track_list_value = self._album_tracks_payload(track_ids, include_tracks, for_embed)
        images = self._images_from_group(getattr(a, "cover_group", None), getattr(a, "cover", []))

        result: Dict[str, Any] = {
            "album_type": self._album_type_to_str(a) or None,
            "available_markets": available,
            "external_urls": self._album_external_urls(base62),
            "id": base62 or None,
            "images": images or None,
            "name": getattr(a, "name", "") or None,
            "release_date": release_date or None,
            "release_date_precision": release_precision or None,
            "type": "album",
            "uri": uri or None,
            "artists": artists or None,
            "copyrights": self._album_copyrights(a),
            "external_ids": self._external_ids_to_dict(getattr(a, "external_id", [])) or None,
            "label": getattr(a, "label", "") or None,
            "popularity": getattr(a, "popularity", 0) or 0,
        }
        if not for_embed:
            result["total_tracks"] = self._album_total_tracks(a)
            if track_list_value is not None:
                result["tracks"] = track_list_value
        return self._prune_empty(result)

    def _track_proto_to_object(self, t: Metadata.Track) -> Dict[str, Any]:
        base62, uri = self._track_identity(t)
        album_proto = getattr(t, "album", None)
        album_obj = (
            self._album_proto_to_object(album_proto, include_tracks=False, for_embed=True)
            if album_proto
            else None
        )
        preview_url = self._preview_url_from_track(t)
        licensor_uuid = self._licensor_uuid_from_track(t)

        result = {
            "album": album_obj,
            "artists": [self._artist_ref_to_object(a) for a in getattr(t, "artist", [])],
            "available_markets": self._restrictions_to_available_markets(getattr(t, "restriction", [])),
            "disc_number": getattr(t, "disc_number", 0) or None,
            "duration_ms": getattr(t, "duration", 0) or None,
            "explicit": bool(getattr(t, "explicit", False)) or None,
            "external_ids": self._external_ids_to_dict(getattr(t, "external_id", [])) or None,
            "external_urls": {"spotify": f"https://open.spotify.com/track/{base62}"} if base62 else {},
            "id": base62 or None,
            "name": getattr(t, "name", "") or None,
            "popularity": getattr(t, "popularity", 0) or None,
            "track_number": getattr(t, "number", 0) or None,
            "type": "track",
            "uri": uri or None,
            "preview_url": preview_url,
            "earliest_live_timestamp": getattr(t, "earliest_live_timestamp", 0) or None,
            "has_lyrics": bool(getattr(t, "has_lyrics", False)) or None,
            "licensor_uuid": licensor_uuid,
        }
        return self._prune_empty(result)

    @staticmethod
    def _playlist_track_ids(contents: Any) -> List[str]:
        if contents is None:
            return []
        track_ids: List[str] = []
        for item in getattr(contents, "items", []):
            uri = getattr(item, "uri", "") or ""
            if uri.startswith(SPOTIFY_TRACK_URI_PREFIX):
                track_ids.append(uri.split(":")[-1])
        return track_ids

    @staticmethod
    def _playlist_added_at_iso(timestamp_ms: Any) -> Optional[str]:
        if not isinstance(timestamp_ms, int) or timestamp_ms <= 0:
            return None
        try:
            return datetime.datetime.fromtimestamp(
                timestamp_ms / 1000.0,
                tz=datetime.UTC,
            ).isoformat().replace("+00:00", "Z")
        except Exception:
            return None

    def _playlist_track_object(
        self,
        uri: str,
        include_track_objects: bool,
        fetched_tracks: Dict[str, Optional[Dict[str, Any]]],
    ) -> Optional[Dict[str, Any]]:
        if not uri.startswith(SPOTIFY_TRACK_URI_PREFIX):
            return None
        base62_id = uri.split(":")[-1]
        if include_track_objects:
            fetched_obj = fetched_tracks.get(base62_id)
            if fetched_obj is not None:
                return fetched_obj
        return self._track_stub(base62_id)

    def _playlist_item_object(
        self,
        item: Any,
        include_track_objects: bool,
        fetched_tracks: Dict[str, Optional[Dict[str, Any]]],
    ) -> Dict[str, Any]:
        uri = getattr(item, "uri", "") or ""
        attrs = getattr(item, "attributes", None)
        added_by = getattr(attrs, "added_by", "") if attrs else ""
        timestamp_ms = getattr(attrs, "timestamp", 0) if attrs else 0
        item_id_bytes = getattr(attrs, "item_id", b"") if attrs else b""
        payload: Dict[str, Any] = {
            "added_at": self._playlist_added_at_iso(timestamp_ms),
            "added_by": {
                "id": added_by,
                "type": "user",
                "uri": f"spotify:user:{added_by}" if added_by else "",
                "external_urls": {"spotify": f"https://open.spotify.com/user/{added_by}"} if added_by else {},
                "display_name": added_by or None,
            },
            "is_local": False,
            "track": self._playlist_track_object(uri, include_track_objects, fetched_tracks),
        }
        if isinstance(item_id_bytes, (bytes, bytearray)) and item_id_bytes:
            payload["item_id"] = util.bytes_to_hex(item_id_bytes)
        return self._prune_empty(payload)

    def _playlist_items(
        self,
        contents: Any,
        include_track_objects: bool,
        fetched_tracks: Dict[str, Optional[Dict[str, Any]]],
    ) -> List[Dict[str, Any]]:
        if contents is None:
            return []
        return [
            self._playlist_item_object(item, include_track_objects, fetched_tracks)
            for item in getattr(contents, "items", [])
        ]

    @staticmethod
    def _playlist_length_seconds(track_ids: List[str], fetched_tracks: Dict[str, Optional[Dict[str, Any]]]) -> Optional[int]:
        if not track_ids or not fetched_tracks:
            return None
        total_ms = 0
        for base62_id in track_ids:
            track_obj = fetched_tracks.get(base62_id)
            if track_obj is None:
                continue
            duration_ms = track_obj.get("duration_ms")
            if isinstance(duration_ms, int) and duration_ms > 0:
                total_ms += duration_ms
        return (total_ms // 1000) if total_ms > 0 else 0

    @staticmethod
    def _playlist_snapshot_id(p: P4.SelectedListContent) -> Optional[str]:
        revision_bytes = getattr(p, "revision", b"") if hasattr(p, "revision") else b""
        return base64.b64encode(revision_bytes).decode("ascii") if revision_bytes else None

    def _playlist_proto_to_object(self, p: P4.SelectedListContent, include_track_objects: bool) -> Dict[str, Any]:
        attrs = getattr(p, "attributes", None)
        name = getattr(attrs, "name", "") if attrs else ""
        description = getattr(attrs, "description", "") if attrs else ""
        collaborative = bool(getattr(attrs, "collaborative", False)) if attrs else False
        images: List[Dict[str, Any]] = []
        picture_url: Optional[str] = None
        pic_url = self._get_playlist_picture_url(attrs)
        if pic_url:
            picture_url = pic_url
            images.append({"url": pic_url, "width": 0, "height": 0})

        owner_username = getattr(p, "owner_username", "") or ""
        contents = getattr(p, "contents", None)
        to_fetch = self._playlist_track_ids(contents)
        fetched_tracks: Dict[str, Optional[Dict[str, Any]]] = {}
        if to_fetch and self._session is not None:
            fetched_tracks = self._fetch_track_objects(to_fetch)
        items = self._playlist_items(contents, include_track_objects, fetched_tracks)

        tracks_obj = self._prune_empty(
            {
            "offset": 0,
            "total": len(items),
            "items": items,
            }
        )
        length_seconds = self._playlist_length_seconds(to_fetch, fetched_tracks)
        snapshot_b64 = self._playlist_snapshot_id(p)

        result = {
            "name": name or None,
            "description": description or None,
            "collaborative": collaborative or None,
            "picture": picture_url or None,
            "owner": self._prune_empty({
                "id": owner_username,
                "type": "user",
                "uri": f"spotify:user:{owner_username}" if owner_username else "",
                "external_urls": {"spotify": f"https://open.spotify.com/user/{owner_username}"} if owner_username else {},
                "display_name": owner_username or None,
            }),
            "snapshot_id": snapshot_b64,
            "length": length_seconds,
            "tracks": tracks_obj,
            "type": "playlist",
        }
        return self._prune_empty(result)

    # ---------- Private: fetching ----------

    def _fetch_single_track_object(self, base62_id: str) -> None:
        try:
            tid = TrackId.from_base62(base62_id)
            t_proto = self._session.api().get_metadata_4_track(tid)
            self._track_object_cache[base62_id] = self._track_proto_to_object(t_proto)
        except Exception:
            self._track_object_cache[base62_id] = None

    def _fetch_track_objects(self, base62_ids: List[str]) -> Dict[str, Optional[Dict[str, Any]]]:
        seen = set()
        unique: List[str] = []
        for b in base62_ids:
            if not b:
                continue
            if b not in seen:
                seen.add(b)
                if b not in self._track_object_cache:
                    unique.append(b)
        if unique:
            max_workers = min(self._max_workers, max(1, len(unique)))
            with ThreadPoolExecutor(max_workers=max_workers) as executor:
                for b in unique:
                    executor.submit(self._fetch_single_track_object, b)
        return {b: self._track_object_cache.get(b) for b in base62_ids if b}


__all__ = ["LibrespotClient"] 
