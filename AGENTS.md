# Agent Instructions

Always assume the app uses the Workers data/config locations.

- Set `DEEZSPOTAG_DATA_DIR` to:
  - `DeezSpoTag.Workers/bin/Debug/net8.0/Data`
- Set `DEEZSPOTAG_CONFIG_DIR` to the same Workers data directory above.

Treat all config, DB, auth, and settings paths as living under that Workers Data folder by default.

Current DB location (queue):
- `Data/queue.db`
