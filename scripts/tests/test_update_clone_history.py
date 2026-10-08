import unittest
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from update_clone_history import merge_history, make_badge


class CloneHistoryTests(unittest.TestCase):
    def test_overlapping_api_days_are_upserted_without_double_counting(self):
        existing = {
            "schema_version": 1,
            "started_at": "2026-09-25",
            "updated_at": "2026-10-07T03:17:00Z",
            "days": {
                "2026-09-25": {"count": 4, "uniques": 3},
                "2026-10-07": {"count": 5, "uniques": 4},
            },
            "total_clones": 9,
        }
        traffic = {
            "count": 14,
            "uniques": 9,
            "clones": [
                {"timestamp": "2026-10-07T00:00:00Z", "count": 8, "uniques": 6},
                {"timestamp": "2026-10-08T00:00:00Z", "count": 6, "uniques": 4},
            ],
        }

        merged = merge_history(existing, traffic, now="2026-10-09T03:17:00Z")

        self.assertEqual(merged["days"]["2026-09-25"]["count"], 4)
        self.assertEqual(merged["days"]["2026-10-07"]["count"], 8)
        self.assertEqual(merged["days"]["2026-10-08"]["count"], 6)
        self.assertEqual(merged["total_clones"], 18)
        self.assertEqual(merged["updated_at"], "2026-10-09T03:17:00Z")

    def test_same_data_keeps_previous_updated_at(self):
        existing = {
            "schema_version": 1,
            "started_at": "2026-10-08",
            "updated_at": "2026-10-08T03:17:00Z",
            "days": {"2026-10-08": {"count": 6, "uniques": 4}},
            "total_clones": 6,
        }
        traffic = {
            "count": 6,
            "uniques": 4,
            "clones": [
                {"timestamp": "2026-10-08T00:00:00Z", "count": 6, "uniques": 4}
            ],
        }

        merged = merge_history(existing, traffic, now="2026-10-09T03:17:00Z")
        self.assertEqual(merged["updated_at"], "2026-10-08T03:17:00Z")

    def test_badge_is_shields_endpoint_payload(self):
        badge = make_badge(1234)
        self.assertEqual(badge["schemaVersion"], 1)
        self.assertEqual(badge["label"], "All-time clones")
        self.assertEqual(badge["message"], "1,234")
        self.assertEqual(badge["labelColor"], "black")
        self.assertEqual(badge["namedLogo"], "github")


if __name__ == "__main__":
    unittest.main()
