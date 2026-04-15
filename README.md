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

<p align="center">
  <img src="Screenshots/screenshot.gif" alt="DeezSpoTag Interface Demo" />
</p>

## Key Features

### Multi-Source Download Workflow

- Unified queueing across supported providers.
- Fallback logic for source/quality failures.
- Download path + library path separation.
- Multiple library support
- Multi-folder support for different content types.
- Browse discography from your library into Spotify discography and Apple Music Atmos tracks.

### Metadata, Tagging, and File Handling

- Multi-platform tagging controls.
- Built-in manual lyrics editor and lyrics creator.
- Essentia-based tagging support.
- Animated artwork support.
- Naming templates and folder structure customization.
- Optional post-download conversion controls.
- Multi-lyrics (.ttml and .lrc) support.
  
### Recommendations and Automation

- Daily music recommendations based on the music in your library.
- Automated playlist generation based on listening patterns.
- Sync artist avatar, background art, and biography with Plex/Jellyfin (still in progress).

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


### Common `.env` Values

- `DEEZSPOTAG_DATA_PATH`
- `DEEZSPOTAG_DATA_PROTECTION_KEYS_DIR` (for multi-instance deployments, this must point to the same shared directory in every app instance)
- `APPLE_WRAPPER_DATA_PATH`
- `APPLE_WRAPPER_SESSION_PATH`
- `DEEZSPOTAG_APPLE_WRAPPER_CONTROL_MODE` (`shared` recommended)
- `DOWNLOADS_PATH`
- `LIBRARY_PATH`


## Security Notes

- Host networking reduces network isolation between containers and the host.
- Keep the stack behind a reverse proxy and HTTPS when exposed externally.
- Restrict external access by network policy, IP rules, and/or upstream auth.
- Use strong credentials and rotate tokens regularly.

## Acknowledgements

DeezSpoTag was informed by ideas, architecture patterns, and implementation approaches from many open-source projects. Thanks to the creators and maintainers behind these projects:

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
- **apmyx** and contributors.
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
- **wrapper** by WorldObservationLog.
- **Apple Music API / tooling references** including work by Myp3a and Sendy McSenderson.

If any project listed above needs correction or more specific attribution, open an issue and it will be updated.

---

