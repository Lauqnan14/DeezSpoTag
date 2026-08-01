#!/usr/bin/env python3
import pathlib
import sys
import unittest


sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from spotify_librespot_worker import Worker  # noqa: E402


TRACK_ID = "1234567890123456789012"
SECOND_TRACK_ID = "abcdefghijklmnopqrstuv"


class FakeClient:
    instances = 0
    fail_playlist_once = False
    fail_first_instance_tracks = False

    def __init__(self, stored_credentials_path, max_workers):
        FakeClient.instances += 1
        self.instance_number = FakeClient.instances
        self.credentials_path = stored_credentials_path
        self.max_workers = max_workers
        self.closed = False

    def close(self):
        self.closed = True

    def get_track(self, track_id):
        if FakeClient.fail_first_instance_tracks and self.instance_number == 1:
            raise RuntimeError("expired session")
        if track_id == SECOND_TRACK_ID:
            raise RuntimeError("missing")
        return {"id": track_id, "name": "Resolved"}

    def get_playlist(self, playlist_id, expand_items=False):
        if FakeClient.fail_playlist_once:
            FakeClient.fail_playlist_once = False
            raise RuntimeError("expired session")
        return {
            "name": "Playlist",
            "snapshot_id": "snapshot-1",
            "tracks": {"items": [{"track": {"id": TRACK_ID}}], "total": 1},
        }


class SpotifyLibrespotWorkerTests(unittest.TestCase):
    def setUp(self):
        FakeClient.instances = 0
        FakeClient.fail_playlist_once = False
        FakeClient.fail_first_instance_tracks = False

    def test_reuses_one_client_across_requests(self):
        worker = Worker(FakeClient, "/tmp/credentials.json")
        first, first_failures = worker.execute("tracks", {"track_ids": [TRACK_ID]})
        second, second_failures = worker.execute("playlist", {"playlist_id": TRACK_ID})

        self.assertEqual(1, FakeClient.instances)
        self.assertEqual([], first_failures)
        self.assertEqual(TRACK_ID, first[0]["track"]["id"])
        self.assertEqual([], second_failures)
        self.assertEqual("snapshot-1", second["snapshot_id"])
        worker.close()

    def test_track_batch_preserves_successes_and_reports_failures(self):
        worker = Worker(FakeClient, "/tmp/credentials.json")
        payload, failures = worker.execute("tracks", {"track_ids": [TRACK_ID, SECOND_TRACK_ID]})

        self.assertEqual(2, len(payload))
        self.assertEqual(TRACK_ID, payload[0]["track"]["id"])
        self.assertEqual(SECOND_TRACK_ID, failures[0]["id"])
        worker.close()

    def test_reconnects_once_after_session_failure(self):
        worker = Worker(FakeClient, "/tmp/credentials.json")
        FakeClient.fail_playlist_once = True

        payload, failures = worker.execute("playlist", {"playlist_id": TRACK_ID})

        self.assertEqual(2, FakeClient.instances)
        self.assertEqual([], failures)
        self.assertEqual("Playlist", payload["name"])
        worker.close()

    def test_reconnects_when_an_expired_session_fails_the_whole_track_batch(self):
        worker = Worker(FakeClient, "/tmp/credentials.json")
        FakeClient.fail_first_instance_tracks = True

        payload, failures = worker.execute("tracks", {"track_ids": [TRACK_ID]})

        self.assertEqual(2, FakeClient.instances)
        self.assertEqual([], failures)
        self.assertEqual(TRACK_ID, payload[0]["track"]["id"])
        worker.close()


if __name__ == "__main__":
    unittest.main()
