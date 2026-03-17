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
docker compose up -d
```

Open:

- `http://<your-server-ip>:8668`

If your host uses Compose v1, replace `docker compose` with `docker-compose`.

---

## Quick Setup

### Common `.env` Values

- `DEEZSPOTAG_DATA_PATH`
- `APPLE_WRAPPER_DATA_PATH`
- `APPLE_WRAPPER_SESSION_PATH`
- `DOWNLOADS_PATH`
- `LIBRARY_PATH`

### Current Runtime Model

- `deezspotag` and `apple-wrapper` run in Docker `network_mode: host` (Linux host networking).
- `deezspotag` runs the published app from the release image (`dotnet DeezSpoTag.Web.dll`).
- `deezspotag` app state/config in-container uses `/data`, backed by the host Workers data path bind mount.
- `apple-wrapper` binds directly on host ports `10020/20020/30020`.
- `deezspotag` reaches `apple-wrapper` via `127.0.0.1` in host mode.
- App and wrapper state persist in host bind-mount paths.
- Compose auto-creates:
  - `DEEZSPOTAG_DATA_PATH`
  - `APPLE_WRAPPER_DATA_PATH`
  - `APPLE_WRAPPER_SESSION_PATH`
  - `DOWNLOADS_PATH`
  - `LIBRARY_PATH`

### Host `.NET` + Docker Apple Wrapper (Parity Gate)

Use this flow when you want to run the web app locally via `dotnet run` but keep Apple wrapper in Docker:

```bash
docker compose stop deezspotag
docker compose up -d apple-wrapper
```

Then start the app locally with Workers data paths:

```bash
export DEEZSPOTAG_DATA_DIR=DeezSpoTag.Workers/Data
export DEEZSPOTAG_CONFIG_DIR=DeezSpoTag.Workers/Data
export DEEZSPOTAG_APPLE_WRAPPER_HOST=127.0.0.1
dotnet run --project DeezSpoTag.Web/DeezSpoTag.Web.csproj -c Debug
```

Expected wrapper endpoints from host:

- `127.0.0.1:10020`
- `127.0.0.1:20020`
- `127.0.0.1:30020`

### Parity Smoke Test

Validate that the release Docker image contains the same critical feature stack used by debug workflows:

```bash
./scripts/docker-dev.sh parity
```

This checks:

- media tools (`ffmpeg`, `mp4box`, `mp4decrypt`)
- Python + Essentia import
- analyzer assets (`Tools/vibe_analyzer.py`, `Tools/models`)
- bundled Apple wrapper helper (`apple-wrapper-runv2`)
- application HTTP startup on localhost
- clean-start bootstrap with temporary writable app data

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
  - App state and config in container: `/data` (mapped to host Workers data directory)
  - Downloads: `/downloads`
  - Library scan mount: `/library`

---

## Disclaimer

This app is still in development.
