#!/usr/bin/python3

from requests import Session
from deezspot.deezloader.deezer_settings import qualities
from deezspot.deezloader.__download_utils__ import md5hex
from deezspot.exceptions import (
    BadCredentials,
    TrackNotFound,
    NoRightOnMedia,
)
from requests import (
    get as req_get,
    post as req_post,
)
from deezspot.libutils.logging_utils import logger
import re
from urllib.parse import urlparse, urlunparse

class API_GW:

    @classmethod
    def __init__(
        cls,
        arl = None,
        email = None,
        password = None
    ):
        cls.__req = Session()
        cls.__arl = arl
        cls.__email = email
        cls.__password = password
        cls.__token = "null"

        cls.__client_id = 172365
        cls.__client_secret = "fb0bec7ccc063dab0417eb7b0d847f34"
        cls.__try_link = "https://api.deezer.com/platform/generic/track/3135556"

        cls.__get_lyric = "song.getLyrics"
        cls.__get_song_data = "song.getData"
        cls.__get_user_getArl = "user.getArl"
        cls.__get_page_track = "deezer.pageTrack"
        cls.__get_user_data = "deezer.getUserData"
        cls.__get_album_data = "song.getListByAlbum"
        cls.__get_playlist_data = "playlist.getSongs"
        cls.__get_episode_data = "episode.getData"

        cls.__get_media_url = "https://media.deezer.com/v1/get_url"
        cls.__get_auth_token_url = "https://api.deezer.com/auth/token"
        cls.__private_api_link = "https://www.deezer.com/ajax/gw-light.php"
        cls.__song_server = "https://e-cdns-proxy-{}.dzcdn.net/mobile/1/{}"

        cls.__refresh_token()

    @classmethod
    def __login(cls):
        if (
            (not cls.__arl) and
            (not cls.__email) and
            (not cls.__password)
        ):
            msg = f"NO LOGIN STUFF INSERTED :)))"

            raise BadCredentials(msg = msg)

        if cls.__arl:
            cls.__req.cookies['arl'] = cls.__arl
        else:
            cls.__set_arl()

    @classmethod
    def __set_arl(cls):
        access_token = cls.__get_access_token()

        c_headers = {
            "Authorization": f"Bearer {access_token}"
        }

        cls.__req.get(cls.__try_link, headers = c_headers).json()
        cls.__arl = cls.__get_api(cls.__get_user_getArl)

    @classmethod
    def __get_access_token(cls):
        password = md5hex(cls.__password)

        to_hash = (
            f"{cls.__client_id}{cls.__email}{password}{cls.__client_secret}"
        )

        request_hash = md5hex(to_hash)

        params = {
            "app_id": cls.__client_id,
            "login": cls.__email,
            "password": password,
            "hash": request_hash
        }

        results = req_get(cls.__get_auth_token_url, params = params).json()

        if "error" in results:
            raise BadCredentials(
                email = cls.__email,
                password = cls.__password
            )

        access_token = results['access_token']

        return access_token

    def __cool_api(cls):
        guest_sid = cls.__req.cookies.get("sid")
        url = "https://api.deezer.com/1.0/gateway.php"

        params = {
            'api_key': "4VCYIJUCDLOUELGD1V8WBVYBNVDYOXEWSLLZDONGBBDFVXTZJRXPR29JRLQFO6ZE",
            'sid': guest_sid,
            'input': '3',
            'output': '3',
            'method': 'song_getData'
        }

        json = {'sng_id': 302127}

        json = req_post(url, params = params, json = json).json()
        print(json)

    @classmethod
    def __get_api(
        cls, method,
        json_data = None,
        repeats = 4
    ):
        params = {
            "api_version": "1.0",
            "api_token": cls.__token,
            "input": "3",
            "method": method
        }

        results = cls.__req.post(
            cls.__private_api_link,
            params = params,
            json = json_data
        ).json()['results']

        if not results and repeats != 0:
            cls.__refresh_token()

            cls.__get_api(
                method, json_data,
                repeats = repeats - 1
            )

        return results

    @classmethod
    def get_user(cls):
        data = cls.__get_api(cls.__get_user_data)

        return data

    @classmethod
    def __refresh_token(cls):
        cls.__req.cookies.clear_session_cookies()

        if not cls.amIlog():
            cls.__login()
            cls.am_I_log()

        data = cls.get_user()
        cls.__token = data['checkForm']
        cls.__license_token = cls.__get_license_token()

    @classmethod
    def __get_license_token(cls):
        data = cls.get_user()
        license_token = data['USER']['OPTIONS']['license_token']

        return license_token

    @classmethod
    def amIlog(cls):
        data = cls.get_user()
        user_id = data['USER']['USER_ID']
        is_logged = False

        if user_id != 0:
            is_logged = True

        return is_logged

    @classmethod
    def am_I_log(cls):
        if not cls.amIlog():
            raise BadCredentials(arl = cls.__arl)

    @classmethod
    def get_song_data(cls, ids):
        json_data = {
            "sng_id" : ids
        }

        infos = cls.__get_api(cls.__get_song_data, json_data)

        return infos

    @classmethod
    def get_album_data(cls, ids):
        json_data = {
            "alb_id": ids,
            "nb": -1
        }

        infos = cls.__get_api(cls.__get_album_data, json_data)

        return infos

    @classmethod
    def get_lyric(cls, ids):
        json_data = {
            "sng_id": ids
        }

        infos = cls.__get_api(cls.__get_lyric, json_data)

        return infos

    @classmethod
    def get_playlist_data(cls, ids):
        json_data = {
            "playlist_id": ids,
            "nb": -1
        }

        infos = cls.__get_api(cls.__get_playlist_data, json_data)

        return infos

    @classmethod
    def get_page_track(cls, ids):
        json_data = {
            "sng_id" : ids
        }

        infos = cls.__get_api(cls.__get_page_track, json_data)

        return infos

    @classmethod
    def get_episode_data(cls, ids):
        json_data = {
            "episode_id": ids
        }

        infos = cls.__get_api(cls.__get_episode_data, json_data)
        
        if infos:
            infos['MEDIA_VERSION'] = '1' 
            infos['SNG_ID'] = infos.get('EPISODE_ID') 
            if 'EPISODE_DIRECT_STREAM_URL' in infos:
                infos['MD5_ORIGIN'] = 'episode'
                
        return infos

    @classmethod
    def get_song_url(cls, n, song_hash):
        song_url = cls.__song_server.format(n, song_hash)

        return song_url

    @classmethod 
    def song_exist(cls, song_link):
        if song_link and 'spreaker.com' in song_link:
            return req_get(song_link, stream=True)
        
        try:
            crypted_audio = req_get(song_link, stream=True, timeout=15)
            if len(crypted_audio.content) == 0:
                raise TrackNotFound
            return crypted_audio
        except Exception as e:
            # DNS fallback across dzcdn proxy hosts (e-cdns-proxy-0..7)
            try:
                parsed = urlparse(song_link)
                host = parsed.netloc
                if re.search(r"e-cdns-proxy-\d+\.dzcdn\.net", host):
                    m = re.search(r"e-cdns-proxy-(\d+)\.dzcdn\.net", host)
                    original_idx = int(m.group(1)) if m else -1
                    for i in range(0, 8):
                        if i == original_idx:
                            continue
                        new_host = re.sub(r"e-cdns-proxy-\d+\.dzcdn\.net", f"e-cdns-proxy-{i}.dzcdn.net", host)
                        new_url = urlunparse((parsed.scheme, new_host, parsed.path, parsed.params, parsed.query, parsed.fragment))
                        try:
                            alt_resp = req_get(new_url, stream=True, timeout=15)
                            if len(alt_resp.content) == 0:
                                continue
                            return alt_resp
                        except Exception:
                            continue
            except Exception:
                pass
            # If all fallbacks failed, re-raise as TrackNotFound/Connection error
            raise

    @classmethod
    def get_medias_url(cls, tracks_token, quality):
        # Only request the specific desired quality to avoid unexpected fallbacks
        json_data = {
            "license_token": cls.__license_token,
            "media": [
                {
                    "type": "FULL",
                    "formats": [
                        {
                            "cipher": "BF_CBC_STRIPE",
                            "format": quality
                        }
                    ]
                }
            ],
            "track_tokens": tracks_token
        }

        infos = req_post(
            cls.__get_media_url,
            json = json_data
        ).json()

        if "errors" in infos:
            msg = infos['errors'][0]['message']

            raise NoRightOnMedia(msg)

        medias = infos['data']

        return medias
