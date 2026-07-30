import socket
import threading
import unittest

from librespot.zeroconf import ZeroconfServer


def _read_fragmented(payload: bytes, split_at: int) -> bytes:
    server, client = socket.socketpair()
    try:
        def write_request():
            client.sendall(payload[:split_at])
            client.sendall(payload[split_at:])
            client.shutdown(socket.SHUT_WR)

        writer = threading.Thread(target=write_request)
        writer.start()
        request = ZeroconfServer.HttpRunner._HttpRunner__read_http_request(server)
        writer.join()
        return request.read()
    finally:
        server.close()
        client.close()


class ZeroconfHttpReaderTests(unittest.TestCase):
    def test_fragmented_add_user_request_is_read_in_full(self):
        body = b"action=addUser&blob=abc%2Bdef%3D%3D&clientKey=key"
        payload = (
            b"POST /?action=addUser HTTP/1.1\r\n"
            b"Content-Type: application/x-www-form-urlencoded\r\n"
            + f"Content-Length: {len(body)}\r\n\r\n".encode()
            + body
        )

        self.assertEqual(_read_fragmented(payload, 22), payload)

    def test_fragmented_get_info_request_is_read_in_full(self):
        payload = b"GET /?action=getInfo HTTP/1.1\r\nHost: localhost\r\n\r\n"

        self.assertEqual(_read_fragmented(payload, 7), payload)
