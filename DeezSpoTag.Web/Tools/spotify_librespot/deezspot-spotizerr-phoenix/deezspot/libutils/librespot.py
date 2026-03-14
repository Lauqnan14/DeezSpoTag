from __future__ import annotations

import base64
import datetime
import time
from concurrent.futures import ThreadPoolExecutor
from typing import Any, Dict, List, Optional, Union

from google.protobuf.descriptor import FieldDescriptor
from google.protobuf.message import Message

from librespot.core import Session, SearchManager
from librespot.metadata import AlbumId, ArtistId, PlaylistId, TrackId
from librespot import util
from librespot.proto import Metadata_pb2 as Metadata
from librespot.proto import Playlist4External_pb2 as P4


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

    def get_album(self, album: Union[str, AlbumId], include_tracks: bool = False) -> Dict[str, Any]:
        album_id = self._ensure_album_id(album)
        album_proto = self._session.api().get_metadata_4_album(album_id)
        return self._album_proto_to_object(album_proto, include_tracks=include_tracks, for_embed=False)

    def get_track(self, track: Union[str, TrackId]) -> Dict[str, Any]:
        track_id = self._ensure_track_id(track)
        track_proto = self._session.api().get_metadata_4_track(track_id)
        return self._track_proto_to_object(track_proto)

    def get_artist(self, artist: Union[str, ArtistId]) -> Dict[str, Any]:
        artist_id = self._ensure_artist_id(artist)
        artist_proto = self._session.api().get_metadata_4_artist(artist_id)
        return self._proto_to_full_json(artist_proto)

    def get_playlist(self, playlist: Union[str, PlaylistId], expand_items: bool = False) -> Dict[str, Any]:
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
    def parse_input_id(kind: str, value: str) -> Union[TrackId, AlbumId, ArtistId, PlaylistId]:
        s = value.strip()
        if kind == "track":
            if s.startswith("spotify:track:"):
                return TrackId.from_uri(s)
            if "open.spotify.com/track/" in s:
                base = s.split("open.spotify.com/track/")[-1].split("?")[0].split("#")[0].strip("/")
                return TrackId.from_base62(base)
            return TrackId.from_base62(s)
        if kind == "album":
            if s.startswith("spotify:album:"):
                return AlbumId.from_uri(s)
            if "open.spotify.com/album/" in s:
                base = s.split("open.spotify.com/album/")[-1].split("?")[0].split("#")[0].strip("/")
                return AlbumId.from_base62(base)
            return AlbumId.from_base62(s)
        if kind == "artist":
            if s.startswith("spotify:artist:"):
                return ArtistId.from_uri(s)
            if "open.spotify.com/artist/" in s:
                base = s.split("open.spotify.com/artist/")[-1].split("?")[0].split("#")[0].strip("/")
                return ArtistId.from_base62(base)
            return ArtistId.from_base62(s)
        if kind == "playlist":
            if s.startswith("spotify:playlist:"):
                return PlaylistId.from_uri(s)
            if "open.spotify.com/playlist/" in s:
                base = s.split("open.spotify.com/playlist/")[-1].split("?")[0].split("#")[0].strip("/")
                return PlaylistId(base)
            return PlaylistId(s)
        raise RuntimeError(f"Unknown kind: {kind}")

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

    def _ensure_track_id(self, v: Union[str, TrackId]) -> TrackId:
        if isinstance(v, TrackId):
            return v
        return self.parse_input_id("track", v)  # type: ignore[return-value]

    def _ensure_album_id(self, v: Union[str, AlbumId]) -> AlbumId:
        if isinstance(v, AlbumId):
            return v
        return self.parse_input_id("album", v)  # type: ignore[return-value]

    def _ensure_artist_id(self, v: Union[str, ArtistId]) -> ArtistId:
        if isinstance(v, ArtistId):
            return v
        return self.parse_input_id("artist", v)  # type: ignore[return-value]

    def _ensure_playlist_id(self, v: Union[str, PlaylistId]) -> PlaylistId:
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

    def _proto_to_full_json(self, msg: Any) -> Any:
        if isinstance(msg, Message):
            msg_name = msg.DESCRIPTOR.name if hasattr(msg, "DESCRIPTOR") else ""
            if msg_name == "Image":
                url = self._image_url_from_file_id(getattr(msg, "file_id", b""))
                width = getattr(msg, "width", 0) or 0
                height = getattr(msg, "height", 0) or 0
                return self._prune_empty({"url": url, "width": width, "height": height})

            if msg_name == "TopTracks":
                country = getattr(msg, "country", "") or ""
                ids: List[str] = []
                try:
                    for t in getattr(msg, "track", []):
                        ids.append(self._bytes_to_base62(getattr(t, "gid", b"")))
                except Exception:
                    pass
                return self._prune_empty({"country": country or None, "track": ids})

            if hasattr(msg, "album"):
                try:
                    albums = getattr(msg, "album", [])
                    ids: List[str] = []
                    for a in albums:
                        ids.append(self._bytes_to_base62(getattr(a, "gid", b"")))
                    return {"album": ids}
                except Exception:
                    pass

            out: Dict[str, Any] = {}
            for field, value in msg.ListFields():
                name = field.name
                if name in ("album_group", "single_group", "compilation_group", "appears_on_group"):
                    try:
                        ids = []
                        for ag in value:  # repeated AlbumGroup
                            for a in getattr(ag, "album", []):
                                ids.append(self._bytes_to_base62(getattr(a, "gid", b"")))
                        out[name] = ids
                        continue
                    except Exception:
                        pass
                name_out = "id" if name == "gid" else name
                if field.type == FieldDescriptor.TYPE_BYTES:
                    if field.label == FieldDescriptor.LABEL_REPEATED:
                        out[name_out] = [self._bytes_to_base62(v) for v in value]
                    else:
                        out[name_out] = self._bytes_to_base62(value)
                elif field.type == FieldDescriptor.TYPE_MESSAGE:
                    if field.label == FieldDescriptor.LABEL_REPEATED:
                        out[name_out] = [self._proto_to_full_json(v) for v in value]
                    else:
                        out[name_out] = self._proto_to_full_json(value)
                else:
                    if field.label == FieldDescriptor.LABEL_REPEATED:
                        out[name_out] = list(value)
                    else:
                        out[name_out] = value
            return out
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

    def _album_proto_to_object(
        self,
        a: Metadata.Album,
        include_tracks: bool = False,
        for_embed: bool = False,
    ) -> Dict[str, Any]:
        gid = getattr(a, "gid", b"")
        hex_id = util.bytes_to_hex(gid) if gid else ""
        uri = ""
        base62 = ""
        if hex_id:
            try:
                aid = AlbumId.from_hex(hex_id)
                uri = aid.to_spotify_uri()
                base62 = uri.split(":")[-1]
            except Exception:
                pass

        available = self._restrictions_to_available_markets(getattr(a, "restriction", []))
        release_date, release_precision = self._date_to_release_fields(getattr(a, "date", None))

        artists = [self._artist_ref_to_object(ar) for ar in getattr(a, "artist", [])]

        track_ids: List[str] = []
        if not for_embed:
            for d in getattr(a, "disc", []):
                for t in getattr(d, "track", []):
                    tid_hex = util.bytes_to_hex(getattr(t, "gid", b"")) if getattr(t, "gid", b"") else ""
                    if not tid_hex:
                        continue
                    try:
                        tid = TrackId.from_hex(tid_hex)
                        t_uri = tid.to_spotify_uri()
                        t_base62 = t_uri.split(":")[-1]
                        if t_base62:
                            track_ids.append(t_base62)
                    except Exception:
                        pass

        track_list_value: Optional[List[Any]] = None
        if not for_embed:
            if include_tracks and self._session is not None and track_ids:
                fetched = self._fetch_track_objects(track_ids)
                expanded: List[Dict[str, Any]] = []
                for b62 in track_ids:
                    obj = fetched.get(b62)
                    if obj is not None:
                        expanded.append(obj)
                    else:
                        expanded.append({
                            "id": b62,
                            "uri": f"spotify:track:{b62}",
                            "type": "track",
                            "external_urls": {"spotify": f"https://open.spotify.com/track/{b62}"},
                        })
                track_list_value = expanded
            else:
                track_list_value = track_ids

        images = self._images_from_group(getattr(a, "cover_group", None), getattr(a, "cover", []))

        result: Dict[str, Any] = {
            "album_type": self._album_type_to_str(a) or None,
            **({"total_tracks": sum(len(getattr(d, "track", [])) for d in getattr(a, "disc", []))} if not for_embed else {}),
            "available_markets": available,
            "external_urls": {"spotify": f"https://open.spotify.com/album/{base62}"} if base62 else {},
            "id": base62 or None,
            "images": images or None,
            "name": getattr(a, "name", "") or None,
            "release_date": release_date or None,
            "release_date_precision": release_precision or None,
            "type": "album",
            "uri": uri or None,
            "artists": artists or None,
            **({"tracks": track_list_value} if (not for_embed and track_list_value is not None) else {}),
            "copyrights": [{
                "text": getattr(c, "text", ""),
                "type": str(getattr(c, "type", "")),
            } for c in getattr(a, "copyright", [])],
            "external_ids": self._external_ids_to_dict(getattr(a, "external_id", [])) or None,
            "label": getattr(a, "label", "") or None,
            "popularity": getattr(a, "popularity", 0) or 0,
        }
        return self._prune_empty(result)

    def _track_proto_to_object(self, t: Metadata.Track) -> Dict[str, Any]:
        tid_hex = util.bytes_to_hex(getattr(t, "gid", b"")) if getattr(t, "gid", b"") else ""
        uri = ""
        base62 = ""
        if tid_hex:
            try:
                tid = TrackId.from_hex(tid_hex)
                uri = tid.to_spotify_uri()
                base62 = uri.split(":")[-1]
            except Exception:
                pass

        album_obj = self._album_proto_to_object(getattr(t, "album", None), include_tracks=False, for_embed=True) if getattr(t, "album", None) else None

        preview_url = None
        previews = getattr(t, "preview", [])
        if previews:
            pf = previews[0]
            pf_id = getattr(pf, "file_id", b"")
            if pf_id:
                try:
                    preview_url = f"https://p.scdn.co/mp3-preview/{util.bytes_to_hex(pf_id)}"
                except Exception:
                    preview_url = None

        licensor_uuid = None
        licensor = getattr(t, "licensor", None)
        if licensor is not None:
            licensor_uuid = util.bytes_to_hex(getattr(licensor, "uuid", b"")) if getattr(licensor, "uuid", b"") else None

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

    def _playlist_proto_to_object(self, p: P4.SelectedListContent, include_track_objects: bool) -> Dict[str, Any]:
        attrs = getattr(p, "attributes", None)
        name = getattr(attrs, "name", "") if attrs else ""
        description = getattr(attrs, "description", "") if attrs else ""
        collaborative = bool(getattr(attrs, "collaborative", False)) if attrs else False
        picture_bytes = getattr(attrs, "picture", b"") if attrs else b""

        images: List[Dict[str, Any]] = []
        picture_url: Optional[str] = None
        # Derive picture URL from attributes.picture with header-aware parsing
        pic_url = self._get_playlist_picture_url(attrs)
        if pic_url:
            picture_url = pic_url
            images.append({"url": pic_url, "width": 0, "height": 0})

        owner_username = getattr(p, "owner_username", "") or ""

        items: List[Dict[str, Any]] = []
        contents = getattr(p, "contents", None)

        fetched_tracks: Dict[str, Optional[Dict[str, Any]]] = {}
        # Collect all track ids to fetch durations for length computation and, if requested, for expansion
        to_fetch: List[str] = []
        if contents is not None:
            for it in getattr(contents, "items", []):
                uri = getattr(it, "uri", "") or ""
                if uri.startswith("spotify:track:"):
                    b62 = uri.split(":")[-1]
                    to_fetch.append(b62)
        if to_fetch and self._session is not None:
            fetched_tracks = self._fetch_track_objects(to_fetch)

        if contents is not None:
            for it in getattr(contents, "items", []):
                uri = getattr(it, "uri", "") or ""
                attrs_it = getattr(it, "attributes", None)
                added_by = getattr(attrs_it, "added_by", "") if attrs_it else ""
                ts_ms = getattr(attrs_it, "timestamp", 0) if attrs_it else 0
                item_id_bytes = getattr(attrs_it, "item_id", b"") if attrs_it else b""
                added_at_iso = None
                if isinstance(ts_ms, int) and ts_ms > 0:
                    try:
                        added_at_iso = datetime.datetime.utcfromtimestamp(ts_ms / 1000.0).isoformat() + "Z"
                    except Exception:
                        added_at_iso = None
                track_obj: Optional[Dict[str, Any]] = None
                if include_track_objects and uri.startswith("spotify:track:"):
                    b62 = uri.split(":")[-1]
                    obj = fetched_tracks.get(b62)
                    if obj is not None:
                        track_obj = obj
                    else:
                        track_obj = {
                            "id": b62,
                            "uri": uri,
                            "type": "track",
                            "external_urls": {"spotify": f"https://open.spotify.com/track/{b62}"},
                        }
                else:
                    if uri.startswith("spotify:track:"):
                        b62 = uri.split(":")[-1]
                        track_obj = {
                            "id": b62,
                            "uri": uri,
                            "type": "track",
                            "external_urls": {"spotify": f"https://open.spotify.com/track/{b62}"},
                        }
                item_obj: Dict[str, Any] = {
                    "added_at": added_at_iso,
                    "added_by": {
                        "id": added_by,
                        "type": "user",
                        "uri": f"spotify:user:{added_by}" if added_by else "",
                        "external_urls": {"spotify": f"https://open.spotify.com/user/{added_by}"} if added_by else {},
                        "display_name": added_by or None,
                    },
                    "is_local": False,
                    "track": track_obj,
                }
                if isinstance(item_id_bytes, (bytes, bytearray)) and item_id_bytes:
                    item_obj["item_id"] = util.bytes_to_hex(item_id_bytes)
                items.append(self._prune_empty(item_obj))

        tracks_obj = self._prune_empty({
            "offset": 0,
            "total": len(items),
            "items": items,
        })

        # Compute playlist length (in seconds) by summing track durations
        length_seconds: Optional[int] = None
        try:
            if to_fetch and fetched_tracks:
                total_ms = 0
                for b62 in to_fetch:
                    obj = fetched_tracks.get(b62)
                    if obj is None:
                        continue
                    dur = obj.get("duration_ms")
                    if isinstance(dur, int) and dur > 0:
                        total_ms += dur
                length_seconds = (total_ms // 1000) if total_ms > 0 else 0
        except Exception:
            length_seconds = None

        rev_bytes = getattr(p, "revision", b"") if hasattr(p, "revision") else b""
        snapshot_b64 = base64.b64encode(rev_bytes).decode("ascii") if rev_bytes else None

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