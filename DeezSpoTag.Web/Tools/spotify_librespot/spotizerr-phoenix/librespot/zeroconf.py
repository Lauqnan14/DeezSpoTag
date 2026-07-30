from __future__ import annotations
from Cryptodome.Cipher import AES
from Cryptodome.Hash import HMAC, SHA1
from Cryptodome.Util import Counter
from librespot import util, Version
from librespot.core import Session
from librespot.crypto import DiffieHellman
from librespot.proto import Connect_pb2 as Connect
from librespot.structure import Closeable, Runnable, SessionListener
import base64
import concurrent.futures
import copy
import io
import json
import hmac as stdlib_hmac
import logging
import socket
import threading
import time
import typing
import urllib.parse
import zeroconf


class ZeroconfServer(Closeable):
    logger = logging.getLogger("Librespot:ZeroconfServer")
    logger.propagate = False
    if not logger.handlers:
        _handler = logging.StreamHandler()
        _handler.setFormatter(logging.Formatter("%(asctime)s [%(levelname)s] %(name)s - %(message)s"))
        logger.addHandler(_handler)
    logger.setLevel(logging.INFO)
    
    service = "_spotify-connect._tcp.local."
    __connecting_username: str | None = None
    __connection_lock = threading.Condition()
    __default_get_info_fields = {
        "status": 101,
        "statusString": "OK",
        "spotifyError": 0,
        "version": "2.9.0",
        "libraryVersion": Version.version_name,
        "accountReq": "FREE",
        "brandDisplayName": "Spotizerr",
        "modelDisplayName": "librespot-spotizerr",
        "voiceSupport": "NO",
        "availability": "1",
        "productID": 0,
        "tokenType": "default",
        "groupStatus": "NONE",
        "resolverVersion": "1",
        "scope": "streaming,client-authorization-universal",
        "clientID": "65b708073fc0480ea92a077233ca87bd"
    }
    __default_successful_add_user = {
        "status": 101,
        "spotifyError": 0,
        "statusString": "OK",
    }
    __eol = b"\r\n"
    __runner: HttpRunner
    __service_info: zeroconf.ServiceInfo
    __session: Session | None = None
    __session_listeners: typing.List[SessionListener] = []
    __zeroconf: zeroconf.Zeroconf

    def __init__(self, inner: Inner, listen_port):
        self.__inner = inner
        self.__keys = DiffieHellman()
        if listen_port <= 0 or listen_port > 65535:
            raise ValueError("A valid stable Spotify Connect listener port is required.")
        self.__runner = ZeroconfServer.HttpRunner(self, listen_port)
        threading.Thread(target=self.__runner.run,
                         name="zeroconf-http-server",
                         daemon=True).start()
                         
        advertised_ip_str = self._get_local_ip()
        self.__zeroconf = self._create_zeroconf()
        
        server_hostname = socket.gethostname()
        if not server_hostname or server_hostname == "localhost":
            self.logger.warning(
                f"Machine hostname is '{server_hostname}', which is not ideal for mDNS. "
                f"Consider setting a unique hostname for this machine. "
                f"Using device name '{inner.device_name}' as part of the service instance name, "
                f"but relying on zeroconf library to handle server resolution for IP {advertised_ip_str}."
            )

        service_addresses = None
        if advertised_ip_str:
            try:
                service_addresses = [socket.inet_aton(advertised_ip_str)]
            except socket.error: # Catches errors like invalid IP string format
                self.logger.error(f"Failed to convert IP string '{advertised_ip_str}' to packed address. Zeroconf will attempt to determine addresses from hostname.")
        
        self.__service_info = zeroconf.ServiceInfo(
            ZeroconfServer.service,  # type, e.g., "_spotify-connect._tcp.local."
            f"{inner.device_name}.{ZeroconfServer.service}",  # name, e.g., "MyDevice._spotify-connect._tcp.local."
            listen_port,
            0,  # weight
            0,  # priority
            {   # properties
                "CPath": "/",
                "VERSION": "1.0",
                "STACK": "SP",
            },
            server=f"{server_hostname}.local.", # server FQDN
            addresses=service_addresses # Pass resolved IP, or None if all-interfaces address or conversion failed
        )
        
        self.__zeroconf.register_service(self.__service_info)
        self.logger.info(
            "Registered Zeroconf service: name=%s port=%s server=%s addresses=%s",
            self.__service_info.name,
            listen_port,
            self.__service_info.server,
            self.__service_info.addresses,
        )

    def _create_zeroconf(self) -> zeroconf.Zeroconf:
        self.logger.info("Starting Zeroconf on active local interfaces")
        return zeroconf.Zeroconf(interfaces=zeroconf.InterfaceChoice.Default)

    def _get_local_ip(self) -> str:
        interface_name = self._get_default_route_interface()
        if interface_name:
            interface_address = self._get_interface_ipv4(interface_name)
            if interface_address:
                self.logger.info(
                    "Using default-route Spotify Connect LAN address %s on %s",
                    interface_address,
                    interface_name,
                )
                return interface_address

        addresses = sorted({
            address[4][0]
            for address in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET)
            if not address[4][0].startswith("127.")
        })
        private_addresses = [
            address for address in addresses
            if address.startswith("10.")
            or address.startswith("192.168.")
            or address.startswith("172.") and 16 <= int(address.split(".")[1]) <= 31
        ]
        if not private_addresses:
            raise RuntimeError(
                "Spotify Connect could not determine a local LAN IPv4 address."
            )
        self.logger.info("Using detected Spotify Connect LAN address: %s", private_addresses[0])
        return private_addresses[0]

    @staticmethod
    def _get_default_route_interface() -> str | None:
        try:
            with open("/proc/net/route", encoding="ascii") as routes:
                next(routes, None)
                for line in routes:
                    fields = line.split()
                    if len(fields) >= 4 and fields[1] == "00000000" and int(fields[3], 16) & 2:
                        return fields[0]
        except (OSError, ValueError):
            return None
        return None

    @staticmethod
    def _get_interface_ipv4(interface_name: str) -> str | None:
        try:
            import fcntl
            import struct

            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
                request = struct.pack("256s", interface_name.encode("utf-8")[:15])
                response = fcntl.ioctl(probe.fileno(), 0x8915, request)
                address = socket.inet_ntoa(response[20:24])
                return None if address.startswith("127.") else address
        except (ImportError, OSError):
            return None

    def add_session_listener(self, listener: ZeroconfServer):
        self.__session_listeners.append(listener)

    def close(self) -> None:
        self.__zeroconf.close()
        self.__runner.close()

    def close_session(self) -> None:
        if self.__session is None:
            return
        for session_listener in self.__session_listeners:
            session_listener.session_closing(self.__session)
        self.__session.close()
        self.__session = None

    def _send_status_response(self, client_socket: socket.socket, http_version: str,
                              status_line: bytes) -> None:
        client_socket.send(http_version.encode())
        client_socket.send(status_line)
        client_socket.send(self.__eol)
        client_socket.send(self.__eol)

    def _send_json_response(self, client_socket: socket.socket, http_version: str,
                            status_line: bytes, payload: dict) -> None:
        response = json.dumps(payload)
        client_socket.send(http_version.encode())
        client_socket.send(status_line)
        client_socket.send(self.__eol)
        client_socket.send(b"Content-Length: ")
        client_socket.send(str(len(response)).encode())
        client_socket.send(self.__eol)
        client_socket.send(self.__eol)
        client_socket.send(response.encode())

    @staticmethod
    def _is_transient_connect_error(err: Exception) -> bool:
        if isinstance(err, (ConnectionResetError, TimeoutError, socket.timeout, ConnectionAbortedError)):
            return True
        return isinstance(err, OSError) and err.errno in (104, 110, 111, 113)

    def _decrypt_blob_payload(self, client_key_str: str, blob_str: str) -> bytes | None:
        shared_key = util.int_to_bytes(
            self.__keys.compute_shared_key(base64.b64decode(client_key_str.encode()))
        )
        blob_bytes = base64.b64decode(blob_str)
        iv = blob_bytes[:16]
        encrypted = blob_bytes[16:len(blob_bytes) - 20]
        checksum = blob_bytes[len(blob_bytes) - 20:]

        sha1 = SHA1.new()
        sha1.update(shared_key)
        base_key = sha1.digest()[:16]
        hmac = HMAC.new(base_key, digestmod=SHA1)
        hmac.update(b"checksum")
        checksum_key = hmac.digest()
        hmac = HMAC.new(base_key, digestmod=SHA1)
        hmac.update(b"encryption")
        encryption_key = hmac.digest()
        hmac = HMAC.new(checksum_key, digestmod=SHA1)
        hmac.update(encrypted)
        mac = hmac.digest()
        # Constant-time comparison avoids timing side-channels on MAC validation.
        # The SHA-1 based derivation itself is protocol-compatibility behavior.
        if not stdlib_hmac.compare_digest(mac, checksum):
            return None

        aes = AES.new(
            encryption_key[:16],
            AES.MODE_CTR,
            counter=Counter.new(128, initial_value=int.from_bytes(iv, "big")),
        )
        return aes.decrypt(encrypted)

    def _create_session_with_retry(self, username: str, decrypted: bytes) -> None:
        retries = 3
        backoff_seconds = 0.5
        self.__session = None
        for attempt in range(1, retries + 1):
            try:
                self.logger.info(
                    "Creating librespot session for user: %s (attempt %d/%d)",
                    username,
                    attempt,
                    retries,
                )
                self.__session = Session.Builder(self.__inner.conf) \
                    .set_device_id(self.__inner.device_id) \
                    .set_device_name(self.__inner.device_name) \
                    .set_device_type(self.__inner.device_type) \
                    .set_preferred_locale(self.__inner.preferred_locale) \
                    .blob(username, decrypted) \
                    .create()
                self.logger.info(
                    "Librespot session created. username=%s stored_credentials_file=%s",
                    self.__session.username(),
                    getattr(self.__inner.conf, "stored_credentials_file", None),
                )
                return
            except Exception as exc:
                self.__session = None
                if self._is_transient_connect_error(exc) and attempt < retries:
                    self.logger.warning(
                        "Transient librespot session error: %s (attempt %d/%d). Retrying in %.1fs.",
                        exc,
                        attempt,
                        retries,
                        backoff_seconds,
                    )
                    time.sleep(backoff_seconds)
                    backoff_seconds *= 2
                    continue
                self.logger.exception("Failed to create librespot session: %s", exc)
                return

    def handle_add_user(self, __socket: socket.socket, params: dict[str, str],
                        http_version: str) -> None:
        username = params.get("userName")
        if not username:
            self.logger.error("Missing userName!")
            return
        blob_str = params.get("blob")
        if not blob_str:
            self.logger.error("Missing blob!")
            return
        client_key_str = params.get("clientKey")
        if not client_key_str:
            self.logger.error("Missing clientKey!")
            return
        with self.__connection_lock:
            if username == self.__connecting_username:
                self.logger.info(
                    "{} is already trying to connect.".format(username))
                self._send_status_response(__socket, http_version, b" 403 Forbidden")
                return
            self.__connecting_username = username
            self.logger.info("Beginning login handshake for user: %s", username)
        try:
            decrypted = self._decrypt_blob_payload(client_key_str, blob_str)
            if decrypted is None:
                self.logger.error("Mac and checksum don't match!")
                self._send_status_response(__socket, http_version, b" 400 Bad Request")
                return

            self.close_session()
            self.logger.info("Accepted new user from {}. [deviceId: {}]".format(
                params.get("deviceName"), self.__inner.device_id))
            self._send_json_response(
                __socket,
                http_version,
                b" 200 OK",
                self.__default_successful_add_user,
            )
            self._create_session_with_retry(username, decrypted)
        except Exception as exc:
            self.logger.exception("Failed to process addUser request: %s", exc)
            self._send_status_response(__socket, http_version, b" 400 Bad Request")
        finally:
            with self.__connection_lock:
                self.__connecting_username = None
        for session_listener in self.__session_listeners:
            session_listener.session_changed(self.__session)

    def handle_get_info(self, __socket: socket.socket,
                        http_version: str) -> None:
        info = copy.deepcopy(self.__default_get_info_fields)
        info["deviceID"] = self.__inner.device_id
        info["remoteName"] = self.__inner.device_name
        info["publicKey"] = base64.b64encode(
            self.__keys.public_key_bytes()).decode()
        device_type_name = Connect.DeviceType.Name(self.__inner.device_type)
        info["deviceType"] = device_type_name.title()
        with self.__connection_lock:
            active_user = ""
            if self.__connecting_username is not None:
                active_user = self.__connecting_username
            elif self.has_valid_session():
                active_user = self.__session.username()
            info["activeUser"] = active_user
        __socket.send(http_version.encode())
        __socket.send(b" 200 OK")
        __socket.send(self.__eol)
        __socket.send(b"Content-Type: application/json")
        __socket.send(self.__eol)
        __socket.send(self.__eol)
        __socket.send(json.dumps(info).encode())

    def has_valid_session(self) -> bool:
        valid = self.__session and self.__session.is_valid()
        if not valid:
            self.__session = None
        return valid

    def parse_path(self, path: str) -> dict[str, str]:
        url = "https://host" + path
        parsed = {}
        params = urllib.parse.parse_qs(urllib.parse.urlparse(url).query)
        for key, values in params.items():
            for value in values:
                parsed[key] = value
        return parsed

    def remove_session_listener(self, listener: SessionListener):
        self.__session_listeners.remove(listener)

    class Builder(Session.Builder):
        listen_port: int = -1

        def set_listen_port(self, listen_port: int):
            self.listen_port = listen_port
            return self

        def create(self) -> ZeroconfServer:
            return ZeroconfServer(
                ZeroconfServer.Inner(self.device_type, self.device_name,
                                     self.device_id, self.preferred_locale,
                                     self.conf), self.listen_port)

    class HttpRunner(Closeable, Runnable):
        __should_stop = False
        __socket: socket.socket
        __worker = concurrent.futures.ThreadPoolExecutor()
        __zeroconf_server: ZeroconfServer

        def __init__(self, zeroconf_server: ZeroconfServer, port: int):
            self.__socket = socket.socket()
            self.__socket.bind((".".join(["0"] * 4), port))
            self.__socket.listen(5)
            self.__zeroconf_server = zeroconf_server
            self.__zeroconf_server.logger.info(
                "Zeroconf HTTP server started successfully on port {}!".format(
                    port))

        def close(self) -> None:
            self.__should_stop = True
            try:
                self.__socket.close()
            except OSError:
                pass
            self.__worker.shutdown(wait=False)

        def run(self):
            while not self.__should_stop:
                try:
                    __socket, _ = self.__socket.accept()
                except OSError:
                    if self.__should_stop:
                        return
                    raise

                def anonymous(client_socket=__socket):
                    self.__handle(client_socket)
                    client_socket.close()

                self.__worker.submit(anonymous)

        def __handle(self, __socket: socket.socket) -> None:
            __socket.settimeout(15)
            request = self.__read_http_request(__socket)
            if request is None:
                return
            request_line = request.readline().strip().split(b" ")
            if len(request_line) != 3:
                self.__zeroconf_server.logger.warning(
                    "Unexpected request line: {}".format(request_line))
            method = request_line[0].decode()
            path = request_line[1].decode()
            http_version = request_line[2].decode()
            headers = {}
            while True:
                header = request.readline().strip()
                if not header:
                    break
                key, value = header.split(b":", 1)
                headers[key.decode()] = value.strip().decode()
            if not self.__zeroconf_server.has_valid_session():
                self.__zeroconf_server.logger.debug(
                    "Handling request: {}, {}, {}, headers: {}".format(
                        method, path, http_version, headers))
            params = {}
            if method == "POST":
                content_type = headers.get("Content-Type")
                if content_type != "application/x-www-form-urlencoded":
                    self.__zeroconf_server.logger.error(
                        "Bad Content-Type: {}".format(content_type))
                    return
                content_length_str = headers.get("Content-Length")
                if content_length_str is None:
                    self.__zeroconf_server.logger.error(
                        "Missing Content-Length header!")
                    return
                content_length = int(content_length_str)
                body = request.read(content_length).decode()
                params = {
                    key: values[-1]
                    for key, values in urllib.parse.parse_qs(body, keep_blank_values=True).items()
                }
            else:
                params = self.__zeroconf_server.parse_path(path)
            action = params.get("action")
            if action is None:
                self.__zeroconf_server.logger.debug(
                    "Request is missing action.")
                return
            self.handle_request(__socket, http_version, action, params)

        @staticmethod
        def __read_http_request(client_socket: socket.socket) -> io.BytesIO | None:
            buffer = bytearray()
            while b"\r\n\r\n" not in buffer:
                chunk = client_socket.recv(8192)
                if not chunk:
                    return None
                buffer.extend(chunk)
                if len(buffer) > 1024 * 1024:
                    raise ValueError("Spotify Connect request headers exceed the allowed size.")

            header_end = buffer.index(b"\r\n\r\n") + 4
            headers = buffer[:header_end].decode("iso-8859-1")
            content_length = 0
            for header in headers.split("\r\n")[1:]:
                if header.lower().startswith("content-length:"):
                    content_length = int(header.split(":", 1)[1].strip())
                    break
            if content_length < 0 or content_length > 1024 * 1024:
                raise ValueError("Spotify Connect request body exceeds the allowed size.")
            required = header_end + content_length
            while len(buffer) < required:
                chunk = client_socket.recv(min(8192, required - len(buffer)))
                if not chunk:
                    return None
                buffer.extend(chunk)
            return io.BytesIO(bytes(buffer[:required]))

        def handle_request(self, __socket: socket.socket, http_version: str,
                           action: str, params: dict[str, str]) -> None:
            if action == "addUser":
                if params is None:
                    raise RuntimeError
                self.__zeroconf_server.handle_add_user(__socket, params,
                                                       http_version)
            elif action == "getInfo":
                self.__zeroconf_server.handle_get_info(__socket, http_version)
            else:
                self.__zeroconf_server.logger.warning(
                    "Unknown action: {}".format(action))

    class Inner:
        conf: typing.Final[Session.Configuration]
        device_name: typing.Final[str]
        device_id: typing.Final[str]
        device_type: typing.Final[Connect.DeviceType]
        preferred_locale: typing.Final[str]

        def __init__(self, device_type: Connect.DeviceType, device_name: str,
                     device_id: str, preferred_locale: str,
                     conf: Session.Configuration):
            self.conf = conf
            self.device_name = device_name
            self.device_id = util.random_hex_string(
                40).lower() if not device_id else device_id
            self.device_type = device_type
            self.preferred_locale = preferred_locale
