<p align="center">
  <img src="DeezSpoTag.Web/wwwroot/images/logo.svg" alt="DeezSpoTag Logo" width="640" />
</p>

# DeezSpoTag

Bridge streaming sources and local libraries with one web app. DeezSpoTag handles discovery, queueing, downloading, tagging, conversion, and organization workflows for self-hosted music stacks.

Support: [GitHub Issues](https://github.com/Lauqnan14/DeezSpoTag/issues)

---

## What It Does

DeezSpoTag automates music workflows end-to-end:

1. Collects tracks/albums/playlists from supported platforms.
2. Queues downloads with quality and fallback rules.
3. Processes files with multi-platform tagging, artwork and lyrics enrichment, Essentia tagging, quality upgrades, and multi-quality queueing (both stereo + Atmos for the same track), with video download support.
4. Supports conversion/transcoding flows where configured.
5. Organizes output into your library structure.
6. Allows manual tagging and lyrics editing

---

## Key Features

### Multi-Source Download Workflow

- Unified queueing across supported providers.
- Fallback logic for source/quality failures.
- Download path + library path separation.
- Large library mount support (read-only library scanning).
- Multi-folder support for different content types.
- Browse discography from your library into Spotify discography and Apple Music Atmos tracks.

### Metadata, Tagging, and File Handling

- Multi-platform tagging controls with format/size preferences.
- Built-in manual lyrics editor and lyrics creator.
- Essentia-based tagging support.
- Animated artwork support.
- Naming templates and folder structure customization.
- Optional post-download conversion controls.

### Recommendations and Automation

- Daily music recommendations based on the music in your library.
- Automated playlist generation based on listening patterns.
- Sync artist avatar, background art, and biography with Plex/Jellyfin (still in progress).

### Apple Music Wrapper Integration

- External `apple-wrapper` service orchestration.
- Login + 2FA helper flow via `apple-wrapperctl.sh`.
- Persistent wrapper state under your data folder.
- Health-checked wrapper ports on localhost.

---

## Run With Docker Compose

Compose-only install is the primary deployment mode.

```bash
mkdir deezspotag && cd deezspotag
curl -L -o docker-compose.yml https://raw.githubusercontent.com/Lauqnan14/DeezSpoTag/main/src/docker-compose.yml
curl -L -o .env https://raw.githubusercontent.com/Lauqnan14/DeezSpoTag/main/src/.env.example
```

Then configure `.env` and start:

```bash
docker compose pull
docker compose up -d
```

Open:

- `http://<your-server-ip>:8668`

If your host uses Compose v1, replace `docker compose` with `docker-compose`.

---

## Quick Setup

### Required `.env` Values

- `DEEZSPOTAG_DATA_PATH`
- `APPLE_WRAPPER_DATA_PATH`
- `APPLE_WRAPPER_SESSION_PATH`
- `DOWNLOADS_PATH`
- `LIBRARY_PATH`

### Current Runtime Model

- `deezspotag` and `apple-wrapper` run in Docker `network_mode: host` (Linux host networking).
- `deezspotag` binds directly on host port `8668`.
- `apple-wrapper` binds directly on host ports `10020/20020/30020`.
- `deezspotag` reaches `apple-wrapper` via `127.0.0.1` in host mode.
- App and wrapper state persist in host bind-mount paths.
- Compose auto-creates:
  - `DEEZSPOTAG_DATA_PATH`
  - `APPLE_WRAPPER_DATA_PATH`
  - `APPLE_WRAPPER_SESSION_PATH`
- Compose does not auto-create:
  - `DOWNLOADS_PATH`
  - `LIBRARY_PATH`

## Security Notes

- Host networking reduces network isolation between containers and the host.
- Keep the stack behind a reverse proxy and HTTPS when exposed externally.
- Restrict external access by network policy, IP rules, and/or upstream auth.
- Use strong credentials and rotate tokens regularly.

---

## Contributing

See `CONTRIBUTING.md`.

---

## Architecture

- `deezspotag`: main web app and orchestration runtime.
- `apple-wrapper`: external Apple helper runtime.
- Data roots:
  - App state and config: `/data`
  - Downloads: `/downloads`
  - Library scan mount: `/library/music` (read-only by default)

---

## Disclaimer

This app is still in development.
