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
- Portable login + 2FA flow via shared wrapper control paths (no Docker socket requirement).
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
- `DEEZSPOTAG_APPLE_WRAPPER_CONTROL_MODE` (`shared` recommended)
- `DOWNLOADS_PATH`
- `LIBRARY_PATH`

### Release Channels

- `prerelease` channel is published continuously from `main`.
- `stable` channel is published only when manually promoted in GitHub Actions.
- Docker channel selection is tag-based:
  - prerelease: set
    - `DEEZSPOTAG_IMAGE=ghcr.io/<owner>/deezspotag:prerelease`
    - `APPLE_WRAPPER_IMAGE=ghcr.io/<owner>/deezspotag-apple-wrapper:prerelease`
  - stable: set
    - `DEEZSPOTAG_IMAGE=ghcr.io/<owner>/deezspotag:latest`
    - `APPLE_WRAPPER_IMAGE=ghcr.io/<owner>/deezspotag-apple-wrapper:latest`
- With `pull_policy: always`, `docker compose up -d` always pulls the selected channel tag.
- GitHub Releases behavior:
  - default (`push` to `main`): creates prerelease entries (`vX.Y.Z.W-pre`)
  - manual promote (`workflow_dispatch` with `release_channel=stable`): creates stable release (`vX.Y.Z.W`)
- Versioned image tags follow the same GitHub release format:
  - prerelease image version tag: `vX.Y.Z.W-pre`
  - stable image version tag: `vX.Y.Z.W`

### Current Runtime Model

- `deezspotag` and `apple-wrapper` run in Docker `network_mode: host` (Linux host networking).
- `deezspotag` runs the published app from the release image (`dotnet DeezSpoTag.Web.dll`).
- `deezspotag` app state/config in-container uses `/data`, backed by the host Workers data path bind mount.
- `apple-wrapper` binds directly on host ports `10020/20020/30020`.
- `deezspotag` reaches `apple-wrapper` via `127.0.0.1` in host mode.
- `deezspotag` and `apple-wrapper` share:
  - `${APPLE_WRAPPER_DATA_PATH}` -> `/apple-wrapper/data` (app) and `/opt/apple-wrapper/data` (wrapper)
  - `${APPLE_WRAPPER_SESSION_PATH}` -> `/apple-wrapper/session` (app) and `/opt/apple-wrapper/rootfs/data/data/com.apple.android.music` (wrapper)
- Apple auth orchestration uses shared files and wrapper HTTP ports, so Docker daemon access is not required in the app container.
- `apple-wrapper` image tracks upstream `WorldObservationLog/wrapper` runtime behavior; DeezSpoTag-specific logic is limited to container packaging and control-path integration.
- Compose maps host `/dev/urandom` and `/dev/random` into wrapper rootfs to avoid distro-specific `mknod` variance.
- The published wrapper image also bakes the minimal rootfs device nodes and timezone payload required by the upstream wrapper, so startup does not depend on NAS-specific `mknod` behavior.
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
export DEEZSPOTAG_APPLE_WRAPPER_CONTROL_MODE=shared
export DEEZSPOTAG_APPLE_WRAPPER_SHARED_DATA_DIR=/absolute/path/to/apple-wrapper/data
export DEEZSPOTAG_APPLE_WRAPPER_SHARED_SESSION_DIR=/absolute/path/to/apple-wrapper/session
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
- Apple wrapper image startup + shared-control compatibility
- application HTTP startup on localhost
- clean-start bootstrap with temporary writable app data

### Local Wrapper Image Build

Build the wrapper image that matches the supported `linux/amd64` release path:

```bash
./Tools/AppleMusicWrapper/build-image.sh
```

This builds `deezspotag-apple-wrapper:local-amd64` and runs the wrapper smoke audit:

- static runtime contract (`wrapper`, `main`, baked `/dev` nodes, timezone payload)
- idle startup without cached Apple session
- wrapper process launch with shared-control mounts and fake login path

Use this if you want to validate the wrapper image locally before publishing it.

## Security Notes

- Host networking reduces network isolation between containers and the host.
- Keep the stack behind a reverse proxy and HTTPS when exposed externally.
- Restrict external access by network policy, IP rules, and/or upstream auth.
- Use strong credentials and rotate tokens regularly.

---

## Contributing

See `CONTRIBUTING.md`.

---

## License Recommendation

Based on a folders-only scan of the reference projects under `References/` (no archive extraction), including AGPL-3.0, GPL-3.0, GPL-2.0-or-later, MIT, Apache-2.0, and ISC sources, the safest project license for DeezSpoTag is:

- **GNU Affero General Public License v3.0 or later (`AGPL-3.0-or-later`)**

Why this is the safest fit:

- It remains compatible with the strong-copyleft references already used during development.
- It covers network/server use (relevant for self-hosted web deployments).
- Permissive dependencies (MIT/Apache/ISC) remain compatible.
- GPL-2.0-or-later references (such as Picard) can be consumed in AGPLv3-compatible form.

Important note:

- Some reference folders do not expose a clear root license in-place (for example: `cinemagoria-main`, `meloday`, `Quality Scaanner/whatsmybitrate-main`).
- Treat unlicensed material as **all rights reserved** unless explicit permission is granted.
- If direct code was copied from any unlicensed source, replace it or obtain written permission before release.

---

## Acknowledgements

DeezSpoTag was informed by ideas, architecture patterns, and implementation approaches from many open-source projects. Thank you to the creators and maintainers behind these references:

- **Deemixrr** (AGPL-3.0) and contributors.
- **deemix** (GPL-3.0) by Bambanah and contributors.
- **Lidarr** (GPL-3.0) by Team Lidarr.
- **MusicMover** (GPL) and contributors.
- **ShazamIO** (MIT) by dotX12.
- **SoulSync** (MIT-style license text) and contributors.
- **SpotiFLAC** (MIT) by afkarxyz, zarzet, and contributors.
- **hifi-api** (MIT) by sachin senal.
- **Wolframe Spotify Canvas** (MIT) by the Wolframe Team.
- **boomplay-main** (MIT) by Okoya Usman.
- **idonthavespotify** (MIT) by Juan Rodriguez Donado.
- **lidify** (GPL-3.0) and contributors.
- **lrclib** (MIT) by tranxuanthang and contributors.
- **OneTagger** (GPL-3.0) and contributors.
- **MusicBrainz Picard** (GPL-2.0-or-later) by the MetaBrainz community.
- **puddletag** (GPL) and contributors.
- **qobuz-artist-discography** (MIT/ISC components) by Paweł Januszek and contributors.
- **refreezer** (GPL-3.0) and contributors.
- **spotizerr-phoenix** (GPL-3.0) and contributors.
- **ATL.NET** (MIT) by Zeugma440.
- **Cinemagoria** by Iván Luna and contributors.
- **Meloday** and its maintainer community.
- **WhatsMyBitrate** by oren-cohen and contributors.
- **syrics-web** (MIT) by Akash R. Chandran.
- **Apple Music API / tooling references** including work by Myp3a and Sendy McSenderson.

If any project listed above needs correction or more specific attribution, open an issue and it will be updated.

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
