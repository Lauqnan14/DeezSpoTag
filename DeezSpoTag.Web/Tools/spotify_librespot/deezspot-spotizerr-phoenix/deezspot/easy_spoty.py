#!/usr/bin/python3

from librespot.core import Session
from librespot.metadata import TrackId, AlbumId, ArtistId, EpisodeId, ShowId, PlaylistId
from deezspot.exceptions import InvalidLink
from typing import Any, Dict, List, Optional

# Note: Search is handled via spotipy (Web API). Other metadata (tracks/albums/...)
# still use librespot via LibrespotClient.

from deezspot.libutils import LibrespotClient

class Spo:
    __error_codes = [404, 400]

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

    # ------------------------- public API -------------------------
    @classmethod
    def get_track(cls, ids, client_id=None, client_secret=None):
        cls.__check_initialized()
        try:
            if cls.__client is None:
                # Fallback to previous proto logic if client is unavailable
                t_id = TrackId.from_base62(ids)
                t_proto = cls.__session.api().get_metadata_4_track(t_id)  # type: ignore[union-attr]
                if not t_proto:
                    raise InvalidLink(ids)
                # Minimal album context from nested album proto if present
                album_proto = getattr(t_proto, 'album', None)
                album_ctx = None
                try:
                    if album_proto is not None:
                        agid = getattr(album_proto, 'gid', None)
                        images: List[Dict[str, Any]] = []
                        try:
                            cg = getattr(album_proto, 'cover_group', None)
                            if cg:
                                # Map image group
                                for im in getattr(cg, 'image', []) or []:
                                    fid = getattr(im, 'file_id', None)
                                    if fid:
                                        images.append({
                                            'url': f"https://i.scdn.co/image/{fid.hex()}",
                                            'width': getattr(im, 'width', 0),
                                            'height': getattr(im, 'height', 0)
                                        })
                            if not images:
                                for im in getattr(album_proto, 'cover', []) or []:
                                    fid = getattr(im, 'file_id', None)
                                    if fid:
                                        images.append({
                                            'url': f"https://i.scdn.co/image/{fid.hex()}",
                                            'width': getattr(im, 'width', 0),
                                            'height': getattr(im, 'height', 0)
                                        })
                        except Exception:
                            images = []
                        album_ctx = {
                            'id': cls.__base62_from_gid(agid, 'album'),
                            'name': getattr(album_proto, 'name', ''),
                            'images': images,
                            'genres': [],
                            'available_markets': None
                        }
                except Exception:
                    album_ctx = None
                # Build track dict
                artists = []
                try:
                    for a in getattr(t_proto, 'artist', []) or []:
                        artists.append({'id': cls.__base62_from_gid(getattr(a, 'gid', None), 'artist'), 'name': getattr(a, 'name', '')})
                except Exception:
                    pass
                external_ids_map: Dict[str, str] = {}
                try:
                    for ext in getattr(t_proto, 'external_id', []) or []:
                        t = getattr(ext, 'type', None)
                        v = getattr(ext, 'id', None)
                        if t and v:
                            external_ids_map[str(t).lower()] = v
                except Exception:
                    pass
                return {
                    'id': cls.__base62_from_gid(getattr(t_proto, 'gid', None), 'track'),
                    'name': getattr(t_proto, 'name', ''),
                    'duration_ms': getattr(t_proto, 'duration', 0),
                    'explicit': getattr(t_proto, 'explicit', False),
                    'track_number': getattr(t_proto, 'number', 1),
                    'disc_number': getattr(t_proto, 'disc_number', 1),
                    'artists': artists,
                    'external_ids': external_ids_map,
                    'available_markets': None,
                    'album': album_ctx
                }
            # Preferred: LibrespotClient
            obj = cls.__client.get_track(ids)
            return obj
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
                # Fallback to previous behavior using proto mapping
                a_id = AlbumId.from_base62(ids)
                a_proto = cls.__session.api().get_metadata_4_album(a_id)  # type: ignore[union-attr]
                if not a_proto:
                    raise InvalidLink(ids)
                # Reuse existing private mapper for proto shape
                # NOTE: import annotations above provided earlier methods; to avoid duplication, call through get_track for items
                # Basic fields
                title = getattr(a_proto, 'name', '')
                # Images
                images: List[Dict[str, Any]] = []
                try:
                    cg = getattr(a_proto, 'cover_group', None)
                    if cg:
                        for im in getattr(cg, 'image', []) or []:
                            fid = getattr(im, 'file_id', None)
                            if fid:
                                images.append({'url': f"https://i.scdn.co/image/{fid.hex()}", 'width': getattr(im, 'width', 0), 'height': getattr(im, 'height', 0)})
                    if not images:
                        for im in getattr(a_proto, 'cover', []) or []:
                            fid = getattr(im, 'file_id', None)
                            if fid:
                                images.append({'url': f"https://i.scdn.co/image/{fid.hex()}", 'width': getattr(im, 'width', 0), 'height': getattr(im, 'height', 0)})
                except Exception:
                    images = []
                # Build simplified tracks list by disc order
                items: List[Dict[str, Any]] = []
                total_tracks = 0
                try:
                    for disc in getattr(a_proto, 'disc', []) or []:
                        disc_number = getattr(disc, 'number', 1)
                        for t in getattr(disc, 'track', []) or []:
                            total_tracks += 1
                            setattr(t, 'disc_number', disc_number)
                            item = cls.get_track(cls.__base62_from_gid(getattr(t, 'gid', None), 'track') or "")
                            if isinstance(item, dict):
                                # Ensure numbering
                                item['disc_number'] = disc_number
                                if not item.get('track_number'):
                                    item['track_number'] = getattr(t, 'number', 1)
                                items.append(item)
                except Exception:
                    items = []
                return {
                    'id': cls.__base62_from_gid(getattr(a_proto, 'gid', None), 'album'),
                    'name': title,
                    'images': images,
                    'tracks': {
                        'items': items,
                        'total': len(items),
                        'limit': len(items),
                        'offset': 0,
                        'next': None,
                        'previous': None
                    }
                }
            # Preferred: LibrespotClient, then reshape to Spo-compatible album dict
            album_obj = cls.__client.get_album(ids, include_tracks=True)
            # album_obj['tracks'] is a list of full track objects; convert to Spo shape
            items = []
            for tr in album_obj.get('tracks', []) or []:
                if isinstance(tr, dict):
                    items.append(tr)
            result = {
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
                    'previous': None
                }
            }
            return result
        except InvalidLink:
            raise
        except Exception:
            raise InvalidLink(ids)

    @classmethod
    def get_playlist(cls, ids, client_id=None, client_secret=None):
        cls.__check_initialized()
        try:
            if cls.__client is None:
                # Fallback to previous behavior (proto mapping)
                p_id = PlaylistId(ids)
                p_proto = cls.__session.api().get_playlist(p_id)  # type: ignore[union-attr]
                if not p_proto:
                    raise InvalidLink(ids)
                name = None
                try:
                    attrs = getattr(p_proto, 'attributes', None)
                    name = getattr(attrs, 'name', None) if attrs else None
                except Exception:
                    name = None
                owner_name = getattr(p_proto, 'owner_username', None) or 'Unknown Owner'
                items = []
                try:
                    contents = getattr(p_proto, 'contents', None)
                    for it in getattr(contents, 'items', []) or []:
                        tref = getattr(it, 'track', None)
                        gid = getattr(tref, 'gid', None) if tref else None
                        base62 = cls.__base62_from_gid(gid, 'track') if gid else None
                        if base62:
                            items.append({'track': {'id': base62}})
                except Exception:
                    items = []
                return {
                    'name': name or 'Unknown Playlist',
                    'owner': {'display_name': owner_name},
                    'images': [],
                    'tracks': {'items': items, 'total': len(items)}
                }
            # Preferred: LibrespotClient, reshape minimally to prior output
            pl_obj = cls.__client.get_playlist(ids, expand_items=False)
            items = []
            try:
                # pl_obj['tracks']['items'] have 'track' possibly as stub dict already
                trks = pl_obj.get('tracks', {}).get('items', [])
                for it in trks:
                    tr = it.get('track') if isinstance(it, dict) else None
                    if isinstance(tr, dict):
                        tid = tr.get('id')
                        if tid:
                            items.append({'track': {'id': tid}})
            except Exception:
                items = []
            return {
                'name': pl_obj.get('name') or 'Unknown Playlist',
                'owner': {'display_name': pl_obj.get('owner', {}).get('display_name') or 'Unknown Owner'},
                'images': pl_obj.get('images') or [],
                'tracks': {'items': items, 'total': len(items)}
            }
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
            if cls.__client is None:
                ar_id = ArtistId.from_base62(ids)
                ar_proto = cls.__session.api().get_metadata_4_artist(ar_id)  # type: ignore[union-attr]
                if not ar_proto:
                    raise InvalidLink(ids)
                requested = [s.strip().lower() for s in str(album_type).split(',') if s.strip()]
                order = ['album', 'single', 'compilation', 'appears_on']
                items: List[Dict[str, Any]] = []
                for group_name in order:
                    if requested and group_name not in requested:
                        continue
                    attr = f"{group_name}_group"
                    grp = getattr(ar_proto, attr, None)
                    if not grp:
                        continue
                    try:
                        for ag in grp:
                            albums = getattr(ag, 'album', []) or []
                            for a in albums:
                                gid = getattr(a, 'gid', None)
                                base62 = cls.__base62_from_gid(gid, 'album') if gid else None
                                name = getattr(a, 'name', '')
                                if base62:
                                    items.append({
                                        'name': name,
                                        'external_urls': {'spotify': f"https://open.spotify.com/album/{base62}"}
                                    })
                                if limit and len(items) >= int(limit):
                                    break
                            if limit and len(items) >= int(limit):
                                break
                    except Exception:
                        continue
                    if limit and len(items) >= int(limit):
                        break
                return {
                    'id': cls.__base62_from_gid(getattr(ar_proto, 'gid', None), 'artist'),
                    'name': getattr(ar_proto, 'name', ''),
                    'items': items
                }
            # Preferred: LibrespotClient then map to legacy items shape
            artist_obj = cls.__client.get_artist(ids)
            requested = [s.strip().lower() for s in str(album_type).split(',') if s.strip()]
            order = ['album', 'single', 'compilation', 'appears_on']
            items: List[Dict[str, Any]] = []
            for group_name in order:
                if requested and group_name not in requested:
                    continue
                key = f"{group_name}_group"
                grp = artist_obj.get(key) if isinstance(artist_obj, dict) else None
                if not grp:
                    continue
                try:
                    # LibrespotClient flattens to arrays of album ids for groups
                    # We only need external_urls.spotify links
                    for album_id in grp:
                        if not album_id:
                            continue
                        items.append({
                            'name': None,
                            'external_urls': {'spotify': f"https://open.spotify.com/album/{album_id}"}
                        })
                        if limit and len(items) >= int(limit):
                            break
                except Exception:
                    continue
                if limit and len(items) >= int(limit):
                    break
            return {
                'id': artist_obj.get('id') if isinstance(artist_obj, dict) else None,
                'name': artist_obj.get('name') if isinstance(artist_obj, dict) else '',
                'items': items
            }
        except InvalidLink:
            raise
        except Exception:
            raise InvalidLink(ids)

    # ------------------------- search (optional) -------------------------
    @classmethod
    def __get_session_country_code(cls) -> str:
        try:
            if cls.__session is None:
                return ""
            cc = getattr(cls.__session, "_Session__country_code", None)
            if isinstance(cc, str) and len(cc) == 2:
                return cc
            cc2 = getattr(cls.__session, "country_code", None)
            if isinstance(cc2, str) and len(cc2) == 2:
                return cc2
        except Exception:
            pass
        return ""

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
