#!/usr/bin/python3

from librespot.core import Session
from librespot.metadata import TrackId, AlbumId, ArtistId, EpisodeId, ShowId, PlaylistId
from deezspot.exceptions import InvalidLink
from typing import Any, Dict, List, Optional

# Note: Search is handled via spotipy (Web API). Other metadata (tracks/albums/...)
# still use librespot via LibrespotClient.

from deezspot.libutils import LibrespotClient

class Spo:
    # Class-level references
    __session: Optional[Session] = None
    __client: Optional[LibrespotClient] = None
    __initialized = False

    @classmethod
    def set_session(cls, session: Session):
        """Attach an active librespot Session for metadata/search operations.
        Also initializes the LibrespotClient wrapper used for metadata fetches.
        """
        cls.__session = session
        try:
            cls.__client = LibrespotClient(session=session)
        except Exception:
            # Fallback: allow partial functionality (episode/search) via raw session
            cls.__client = None
        cls.__initialized = True

    @classmethod
    def __init__(cls, client_id=None, client_secret=None):
        """Kept for compatibility; no longer used (librespot session is used)."""
        cls.__initialized = True

    @classmethod
    def __check_initialized(cls):
        if not cls.__initialized or (cls.__session is None and cls.__client is None):
            raise ValueError("Spotify session/client not initialized. Ensure SpoLogin created a librespot Session and called Spo.set_session(session).")

    # ------------------------- helpers -------------------------
    @staticmethod
    def __base62_from_gid(gid_bytes: bytes, kind: str) -> Optional[str]:
        if not gid_bytes:
            return None
        hex_id = gid_bytes.hex()
        try:
            if kind == 'track':
                obj = TrackId.from_hex(hex_id)
            elif kind == 'album':
                obj = AlbumId.from_hex(hex_id)
            elif kind == 'artist':
                obj = ArtistId.from_hex(hex_id)
            elif kind == 'episode':
                obj = EpisodeId.from_hex(hex_id)
            elif kind == 'show':
                obj = ShowId.from_hex(hex_id)
            elif kind == 'playlist':
                # PlaylistId typically not hex-backed in same way, avoid for playlists here
                return None
            else:
                return None
            uri = obj.to_spotify_uri()
            return uri.split(":")[-1]
        except Exception:
            return None

    @staticmethod
    def __images_from_album_obj(album_obj: Dict[str, Any]) -> List[Dict[str, Any]]:
        imgs = album_obj.get('images')
        return imgs if isinstance(imgs, list) else []

    @classmethod
    def __album_images_from_proto(cls, album_proto: Any) -> List[Dict[str, Any]]:
        images: List[Dict[str, Any]] = []
        cover_group = getattr(album_proto, 'cover_group', None)
        cover_group_images = getattr(cover_group, 'image', []) if cover_group else []
        for im in cover_group_images or []:
            file_id = getattr(im, 'file_id', None)
            if not file_id:
                continue
            images.append({
                'url': f"https://i.scdn.co/image/{file_id.hex()}",
                'width': getattr(im, 'width', 0),
                'height': getattr(im, 'height', 0),
            })
        if images:
            return images
        for im in getattr(album_proto, 'cover', []) or []:
            file_id = getattr(im, 'file_id', None)
            if not file_id:
                continue
            images.append({
                'url': f"https://i.scdn.co/image/{file_id.hex()}",
                'width': getattr(im, 'width', 0),
                'height': getattr(im, 'height', 0),
            })
        return images

    @classmethod
    def __album_context_from_track_proto(cls, t_proto: Any) -> Optional[Dict[str, Any]]:
        album_proto = getattr(t_proto, 'album', None)
        if album_proto is None:
            return None
        return {
            'id': cls.__base62_from_gid(getattr(album_proto, 'gid', None), 'album'),
            'name': getattr(album_proto, 'name', ''),
            'images': cls.__album_images_from_proto(album_proto),
            'genres': [],
            'available_markets': None,
        }

    @classmethod
    def __track_artists_from_proto(cls, t_proto: Any) -> List[Dict[str, Any]]:
        artists: List[Dict[str, Any]] = []
        for artist in getattr(t_proto, 'artist', []) or []:
            artists.append({
                'id': cls.__base62_from_gid(getattr(artist, 'gid', None), 'artist'),
                'name': getattr(artist, 'name', ''),
            })
        return artists

    @staticmethod
    def __track_external_ids_from_proto(t_proto: Any) -> Dict[str, str]:
        external_ids_map: Dict[str, str] = {}
        for external in getattr(t_proto, 'external_id', []) or []:
            ext_type = getattr(external, 'type', None)
            ext_value = getattr(external, 'id', None)
            if ext_type and ext_value:
                external_ids_map[str(ext_type).lower()] = ext_value
        return external_ids_map

    @classmethod
    def __track_from_proto(cls, t_proto: Any) -> Dict[str, Any]:
        return {
            'id': cls.__base62_from_gid(getattr(t_proto, 'gid', None), 'track'),
            'name': getattr(t_proto, 'name', ''),
            'duration_ms': getattr(t_proto, 'duration', 0),
            'explicit': getattr(t_proto, 'explicit', False),
            'track_number': getattr(t_proto, 'number', 1),
            'disc_number': getattr(t_proto, 'disc_number', 1),
            'artists': cls.__track_artists_from_proto(t_proto),
            'external_ids': cls.__track_external_ids_from_proto(t_proto),
            'available_markets': None,
            'album': cls.__album_context_from_track_proto(t_proto),
        }

    @classmethod
    def __album_track_items_from_proto(cls, a_proto: Any) -> List[Dict[str, Any]]:
        items: List[Dict[str, Any]] = []
        for disc in getattr(a_proto, 'disc', []) or []:
            disc_number = getattr(disc, 'number', 1)
            for track in getattr(disc, 'track', []) or []:
                track_id = cls.__base62_from_gid(getattr(track, 'gid', None), 'track')
                if not track_id:
                    continue
                item = cls.get_track(track_id)
                if not isinstance(item, dict):
                    continue
                item['disc_number'] = disc_number
                if not item.get('track_number'):
                    item['track_number'] = getattr(track, 'number', 1)
                items.append(item)
        return items

    @classmethod
    def __album_from_proto(cls, a_proto: Any) -> Dict[str, Any]:
        items = cls.__album_track_items_from_proto(a_proto)
        return {
            'id': cls.__base62_from_gid(getattr(a_proto, 'gid', None), 'album'),
            'name': getattr(a_proto, 'name', ''),
            'images': cls.__album_images_from_proto(a_proto),
            'tracks': {
                'items': items,
                'total': len(items),
                'limit': len(items),
                'offset': 0,
                'next': None,
                'previous': None,
            },
        }

    @classmethod
    def __album_from_client(cls, album_obj: Dict[str, Any]) -> Dict[str, Any]:
        items = [track for track in album_obj.get('tracks', []) or [] if isinstance(track, dict)]
        return {
            'id': album_obj.get('id'),
            'name': album_obj.get('name'),
            'album_type': album_obj.get('album_type'),
            'release_date': album_obj.get('release_date'),
            'release_date_precision': album_obj.get('release_date_precision'),
            'total_tracks': album_obj.get('total_tracks') or len(items),
            'genres': album_obj.get('genres') or [],
            'images': cls.__images_from_album_obj(album_obj),
            'available_markets': album_obj.get('available_markets'),
            'external_ids': album_obj.get('external_ids') or {},
            'artists': album_obj.get('artists') or [],
            'tracks': {
                'items': items,
                'total': len(items),
                'limit': len(items),
                'offset': 0,
                'next': None,
                'previous': None,
            },
        }

    @classmethod
    def __playlist_items_from_proto(cls, p_proto: Any) -> List[Dict[str, Any]]:
        items: List[Dict[str, Any]] = []
        contents = getattr(p_proto, 'contents', None)
        for item in getattr(contents, 'items', []) or []:
            track_ref = getattr(item, 'track', None)
            gid = getattr(track_ref, 'gid', None) if track_ref else None
            base62 = cls.__base62_from_gid(gid, 'track') if gid else None
            if base62:
                items.append({'track': {'id': base62}})
        return items

    @classmethod
    def __playlist_from_proto(cls, p_proto: Any) -> Dict[str, Any]:
        attrs = getattr(p_proto, 'attributes', None)
        name = getattr(attrs, 'name', None) if attrs else None
        owner_name = getattr(p_proto, 'owner_username', None) or 'Unknown Owner'
        items = cls.__playlist_items_from_proto(p_proto)
        return {
            'name': name or 'Unknown Playlist',
            'owner': {'display_name': owner_name},
            'images': [],
            'tracks': {'items': items, 'total': len(items)},
        }

    @staticmethod
    def __playlist_items_from_client(pl_obj: Dict[str, Any]) -> List[Dict[str, Any]]:
        items: List[Dict[str, Any]] = []
        tracks = pl_obj.get('tracks', {}).get('items', [])
        for item in tracks:
            track = item.get('track') if isinstance(item, dict) else None
            if not isinstance(track, dict):
                continue
            track_id = track.get('id')
            if track_id:
                items.append({'track': {'id': track_id}})
        return items

    @classmethod
    def __playlist_from_client(cls, pl_obj: Dict[str, Any]) -> Dict[str, Any]:
        items = cls.__playlist_items_from_client(pl_obj)
        owner = pl_obj.get('owner', {}) if isinstance(pl_obj.get('owner', {}), dict) else {}
        return {
            'name': pl_obj.get('name') or 'Unknown Playlist',
            'owner': {'display_name': owner.get('display_name') or 'Unknown Owner'},
            'images': pl_obj.get('images') or [],
            'tracks': {'items': items, 'total': len(items)},
        }

    @staticmethod
    def __requested_album_types(album_type: str) -> List[str]:
        return [item.strip().lower() for item in str(album_type).split(',') if item.strip()]

    @staticmethod
    def __limit_reached(limit: int, items: List[Dict[str, Any]]) -> bool:
        return bool(limit) and len(items) >= int(limit)

    @staticmethod
    def __selected_artist_groups(requested: List[str]) -> List[str]:
        order = ['album', 'single', 'compilation', 'appears_on']
        if not requested:
            return order
        return [group_name for group_name in order if group_name in requested]

    @classmethod
    def __proto_group_items(cls, ar_proto: Any, group_name: str) -> List[Dict[str, Any]]:
        group = getattr(ar_proto, f"{group_name}_group", None)
        if not group:
            return []
        items: List[Dict[str, Any]] = []
        for album_group in group:
            albums = getattr(album_group, 'album', []) or []
            for album in albums:
                album_id = cls.__base62_from_gid(getattr(album, 'gid', None), 'album')
                if not album_id:
                    continue
                items.append({
                    'name': getattr(album, 'name', ''),
                    'external_urls': {'spotify': f"https://open.spotify.com/album/{album_id}"},
                })
        return items

    @staticmethod
    def __client_group_items(artist_obj: Dict[str, Any], group_name: str) -> List[Dict[str, Any]]:
        albums = artist_obj.get(f"{group_name}_group") if isinstance(artist_obj, dict) else None
        if not albums:
            return []
        items: List[Dict[str, Any]] = []
        for album_id in albums:
            if not album_id:
                continue
            items.append({
                'name': None,
                'external_urls': {'spotify': f"https://open.spotify.com/album/{album_id}"},
            })
        return items

    @classmethod
    def __collect_artist_items_from_proto(cls, ar_proto: Any, requested: List[str], limit: int) -> List[Dict[str, Any]]:
        items: List[Dict[str, Any]] = []
        for group_name in cls.__selected_artist_groups(requested):
            for item in cls.__proto_group_items(ar_proto, group_name):
                items.append(item)
                if cls.__limit_reached(limit, items):
                    return items
        return items

    @classmethod
    def __collect_artist_items_from_client(cls, artist_obj: Dict[str, Any], requested: List[str], limit: int) -> List[Dict[str, Any]]:
        items: List[Dict[str, Any]] = []
        for group_name in cls.__selected_artist_groups(requested):
            group_items = cls.__client_group_items(artist_obj, group_name)
            for item in group_items:
                items.append(item)
                if cls.__limit_reached(limit, items):
                    return items
        return items

    # ------------------------- public API -------------------------
    @classmethod
    def get_track(cls, ids, client_id=None, client_secret=None):
        cls.__check_initialized()
        try:
            if cls.__client is None:
                t_id = TrackId.from_base62(ids)
                t_proto = cls.__session.api().get_metadata_4_track(t_id)  # type: ignore[union-attr]
                if not t_proto:
                    raise InvalidLink(ids)
                return cls.__track_from_proto(t_proto)
            return cls.__client.get_track(ids)
        except InvalidLink:
            raise
        except Exception:
            raise InvalidLink(ids)

    @classmethod
    def get_tracks(cls, ids: list, market: str = None, client_id=None, client_secret=None):
        if not ids:
            return {'tracks': []}
        cls.__check_initialized()
        tracks: List[Dict[str, Any]] = []
        for tid in ids:
            try:
                tracks.append(cls.get_track(tid))
            except Exception:
                tracks.append(None)
        return {'tracks': tracks}

    @classmethod
    def get_album(cls, ids, client_id=None, client_secret=None):
        cls.__check_initialized()
        try:
            if cls.__client is None:
                a_id = AlbumId.from_base62(ids)
                a_proto = cls.__session.api().get_metadata_4_album(a_id)  # type: ignore[union-attr]
                if not a_proto:
                    raise InvalidLink(ids)
                return cls.__album_from_proto(a_proto)
            album_obj = cls.__client.get_album(ids, include_tracks=True)
            return cls.__album_from_client(album_obj)
        except InvalidLink:
            raise
        except Exception:
            raise InvalidLink(ids)

    @classmethod
    def get_playlist(cls, ids, client_id=None, client_secret=None):
        cls.__check_initialized()
        try:
            if cls.__client is None:
                p_id = PlaylistId(ids)
                p_proto = cls.__session.api().get_playlist(p_id)  # type: ignore[union-attr]
                if not p_proto:
                    raise InvalidLink(ids)
                return cls.__playlist_from_proto(p_proto)
            pl_obj = cls.__client.get_playlist(ids, expand_items=False)
            return cls.__playlist_from_client(pl_obj)
        except InvalidLink:
            raise
        except Exception:
            raise InvalidLink(ids)

    @classmethod
    def get_episode(cls, ids, client_id=None, client_secret=None):
        cls.__check_initialized()
        try:
            # Episodes not supported by LibrespotClient wrapper yet; use raw session
            e_id = EpisodeId.from_base62(ids)
            e_proto = cls.__session.api().get_metadata_4_episode(e_id)  # type: ignore[union-attr]
            if not e_proto:
                raise InvalidLink(ids)
            show_proto = getattr(e_proto, 'show', None)
            show_id = None
            show_name = ''
            publisher = ''
            try:
                sgid = getattr(show_proto, 'gid', None) if show_proto else None
                show_id = cls.__base62_from_gid(sgid, 'show') if sgid else None
                show_name = getattr(show_proto, 'name', '') if show_proto else ''
                publisher = getattr(show_proto, 'publisher', '') if show_proto else ''
            except Exception:
                pass
            images: List[Dict[str, Any]] = []
            try:
                # cover_image is an ImageGroup
                cg = getattr(e_proto, 'cover_image', None)
                for im in getattr(cg, 'image', []) or []:
                    fid = getattr(im, 'file_id', None)
                    if fid:
                        images.append({
                            'url': f"https://i.scdn.co/image/{fid.hex()}",
                            'width': getattr(im, 'width', 0),
                            'height': getattr(im, 'height', 0)
                        })
            except Exception:
                images = []
            return {
                'id': cls.__base62_from_gid(getattr(e_proto, 'gid', None), 'episode'),
                'name': getattr(e_proto, 'name', ''),
                'duration_ms': getattr(e_proto, 'duration', 0),
                'explicit': getattr(e_proto, 'explicit', False),
                'images': images,
                'available_markets': None,
                'show': {
                    'id': show_id,
                    'name': show_name,
                    'publisher': publisher
                }
            }
        except InvalidLink:
            raise
        except Exception:
            raise InvalidLink(ids)

    @classmethod
    def get_artist(cls, ids, album_type='album,single,compilation,appears_on', limit: int = 50, client_id=None, client_secret=None):
        """Return a dict with artist name and an 'items' list of albums matching album_type.
        Each item contains an external_urls.spotify link, minimally enough for download_artist."""
        cls.__check_initialized()
        try:
            requested = cls.__requested_album_types(album_type)
            if cls.__client is None:
                ar_id = ArtistId.from_base62(ids)
                ar_proto = cls.__session.api().get_metadata_4_artist(ar_id)  # type: ignore[union-attr]
                if not ar_proto:
                    raise InvalidLink(ids)
                items = cls.__collect_artist_items_from_proto(ar_proto, requested, limit)
                return {
                    'id': cls.__base62_from_gid(getattr(ar_proto, 'gid', None), 'artist'),
                    'name': getattr(ar_proto, 'name', ''),
                    'items': items,
                }
            artist_obj = cls.__client.get_artist(ids)
            items = cls.__collect_artist_items_from_client(artist_obj, requested, limit)
            return {
                'id': artist_obj.get('id') if isinstance(artist_obj, dict) else None,
                'name': artist_obj.get('name') if isinstance(artist_obj, dict) else '',
                'items': items,
            }
        except InvalidLink:
            raise
        except Exception:
            raise InvalidLink(ids)

    # ------------------------- search (optional) -------------------------
    @classmethod
    def search(cls, query, search_type='track', limit=10, country: Optional[str] = None, locale: Optional[str] = None, catalogue: Optional[str] = None, image_size: Optional[str] = None, client_id=None, client_secret=None):
        # Reverted: use spotipy Web API search; librespot search is not supported here.
        try:
            import spotipy  # type: ignore
            from spotipy.oauth2 import SpotifyClientCredentials  # type: ignore
        except Exception as e:
            raise RuntimeError("spotipy is required for search; please install spotipy") from e
        try:
            if client_id or client_secret:
                auth_manager = SpotifyClientCredentials(client_id=client_id, client_secret=client_secret)
            else:
                auth_manager = SpotifyClientCredentials()
            sp = spotipy.Spotify(auth_manager=auth_manager)
            type_param = ','.join([t.strip() for t in str(search_type or 'track').split(',') if t.strip()]) or 'track'
            market = country or None
            res = sp.search(q=query, type=type_param, market=market, limit=int(limit) if limit is not None else 10)
            return res
        except Exception as e:
            # Surface a concise error to callers
            raise RuntimeError(f"Spotify search failed: {e}")
