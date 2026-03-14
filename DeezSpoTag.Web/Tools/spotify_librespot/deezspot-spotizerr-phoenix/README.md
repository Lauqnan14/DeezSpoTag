# DeezSpot

## Description

DeezSpot is a Python library designed for downloading music and episodes from Deezer and Spotify. It allows users to fetch tracks, albums, playlists, and podcast episodes, with support for various audio qualities and tagging.

## Features

- **Download from Deezer and Spotify:** Supports downloading content from both major streaming platforms.
- **Content Types:**
  - Tracks
  - Albums
  - Playlists
  - Podcast Episodes
- **Audio Quality:** Choose from multiple audio quality options (e.g., MP3_320, FLAC for Deezer; NORMAL, HIGH, VERY_HIGH for Spotify).
- **Audio Conversion:** Option to convert downloaded audio to different formats (e.g., mp3, flac, opus, m4a).
- **Metadata Tagging:** Automatically tags downloaded files with relevant metadata (title, artist, album, cover art, etc.).
- **Spotify to Deezer Link Conversion:** Convert Spotify track and album links to their Deezer equivalents to facilitate downloading via Deezer.
- **Login Management:** Securely handles login credentials for Deezer (ARL cookie or email/password) and Spotify (client ID/secret, credentials file).
- **Progress Reporting:** Provides callbacks for real-time progress updates during downloads.
- **Customizable Output:** Flexible options for naming and organizing downloaded files and directories.
- **Resilient Downloads:** Includes retry mechanisms for handling network issues.
- **M3U Playlist Generation:** Automatically creates M3U playlist files for downloaded playlists.
- **ZIP Archiving:** Option to create ZIP archives of downloaded albums and playlists.

## Installation

(Provide instructions on how to install the library, e.g., using pip)

```bash
pip install deezspot-spotizerr
```

## Usage

### Initialization

**For Deezer:**

```python
from deezspot import DeeLogin

# Using ARL cookie (recommended)
deezer_downloader = DeeLogin(arl="YOUR_DEEZER_ARL_COOKIE")

# Or using email and password (less secure, might be deprecated by Deezer)
# deezer_downloader = DeeLogin(email="your_email", password="your_password")
```

**For Spotify:**

```python
from deezspot.spotloader import SpoLogin # Corrected import path

# You need Spotify API credentials (client_id, client_secret)
# and a credentials.json file for librespot-spotizerr-phoenix.
spotify_downloader = SpoLogin(
    credentials_path="path/to/your/credentials.json",
    spotify_client_id="YOUR_SPOTIFY_CLIENT_ID",
    spotify_client_secret="YOUR_SPOTIFY_CLIENT_SECRET"
)
```

### Downloading Content

**Deezer Examples:**

```python
# Download a track from Deezer
track_link_dee = "https://www.deezer.com/track/TRACK_ID"
downloaded_track = deezer_downloader.download_trackdee(
    link_track=track_link_dee,
    output_dir="./music_downloads",
    quality_download="FLAC", # or "MP3_320"
    convert_to="mp3-320" # Optional: convert to mp3 at 320kbps
)
print(f"Downloaded Deezer track: {downloaded_track.song_path}")

# Download an album from Deezer
album_link_dee = "https://www.deezer.com/album/ALBUM_ID"
downloaded_album = deezer_downloader.download_albumdee(
    link_album=album_link_dee,
    output_dir="./music_downloads",
    make_zip=True
)
print(f"Downloaded Deezer album: {downloaded_album.album_name}")
if downloaded_album.zip_path:
    print(f"Album ZIP created at: {downloaded_album.zip_path}")


# Download a playlist from Deezer
playlist_link_dee = "https://www.deezer.com/playlist/PLAYLIST_ID"
downloaded_playlist = deezer_downloader.download_playlistdee(
    link_playlist=playlist_link_dee,
    output_dir="./music_downloads"
)
print(f"Downloaded Deezer playlist: {len(downloaded_playlist.tracks)} tracks")

# Download an episode from Deezer
episode_link_dee = "https://www.deezer.com/episode/EPISODE_ID"
downloaded_episode = deezer_downloader.download_episode(
    link_episode=episode_link_dee,
    output_dir="./podcast_downloads"
)
print(f"Downloaded Deezer episode: {downloaded_episode.episode_path}")
```

**Spotify Examples:**

```python
# Download a track from Spotify
track_link_spo = "https://open.spotify.com/track/TRACK_ID"
downloaded_track_spo = spotify_downloader.download_track(
    link_track=track_link_spo,
    output_dir="./music_downloads_spotify",
    quality_download="VERY_HIGH", # or "HIGH", "NORMAL"
    convert_to="flac" # Optional: convert to FLAC
)
print(f"Downloaded Spotify track: {downloaded_track_spo.song_path}")

# Download an album from Spotify
album_link_spo = "https://open.spotify.com/album/ALBUM_ID"
downloaded_album_spo = spotify_downloader.download_album(
    link_album=album_link_spo,
    output_dir="./music_downloads_spotify",
    make_zip=True
)
print(f"Downloaded Spotify album: {downloaded_album_spo.album_name}")

# Download a playlist from Spotify
playlist_link_spo = "https://open.spotify.com/playlist/PLAYLIST_ID"
downloaded_playlist_spo = spotify_downloader.download_playlist(
    link_playlist=playlist_link_spo,
    output_dir="./music_downloads_spotify"
)
print(f"Downloaded Spotify playlist: {len(downloaded_playlist_spo.tracks)} tracks")

# Download an episode from Spotify
episode_link_spo = "https://open.spotify.com/episode/EPISODE_ID"
downloaded_episode_spo = spotify_downloader.download_episode(
    link_episode=episode_link_spo,
    output_dir="./podcast_downloads_spotify"
)
print(f"Downloaded Spotify episode: {downloaded_episode_spo.episode_path}")
```

**Smart Downloader (Deezer only for now):**

The smart downloader automatically detects the content type (track, album, playlist) from the URL.

```python
smart_link_dee = "https://www.deezer.com/album/ALBUM_ID_OR_TRACK_ID_OR_PLAYLIST_ID"
smart_download_result = deezer_downloader.download_smart(
    link=smart_link_dee,
    output_dir="./smart_downloads"
)
print(f"Smart download type: {smart_download_result.type}")
if smart_download_result.type == "track":
    print(f"Downloaded track: {smart_download_result.track.song_path}")
elif smart_download_result.type == "album":
    print(f"Downloaded album: {smart_download_result.album.album_name}")
# ... and so on for playlist
```

### Customizing Output Paths

You can customize the directory structure and track filenames using `custom_dir_format` and `custom_track_format` parameters available in most download methods. These parameters accept strings with placeholders that will be replaced with metadata.

**Available placeholders:**

- `%artist%`
- `%albumartist%`
- `%album%`
- `%title%`
- `%tracknumber%` (can be padded with `pad_tracks=True/False`)
- `%discnumber%`
- `%year%`
- `%genre%`
- `%isrc%`
- `%upc%` (for albums)
- `%quality%` (e.g., "FLAC", "320")
- `%explicit%` (e.g. "Explicit" or empty string)
- `%showname%` (for episodes)
- `%episodetitle%` (for episodes)
- `%podcastgenre%` (for episodes)
- `%playlist%` (for playlist downloads: playlist name)
- `%playlistnum%` (for playlist downloads: 1-based index of the track within the playlist; respects `pad_tracks`)

**Example:**

```python
# For Deezer
deezer_downloader.download_trackdee(
    link_track="some_track_link",
    output_dir="./custom_music",
    custom_dir_format="%artist%/%album% (%year%)",
    custom_track_format="%tracknumber%. %title% [%quality%]",
    pad_tracks=True # Results in "01. Song Title [FLAC]"
)

# For Spotify
spotify_downloader.download_track(
    link_track="some_spotify_track_link",
    output_dir="./custom_music_spotify",
    custom_dir_format="%artist% - %album%",
    custom_track_format="%title% - %artist%",
    pad_tracks=False
)
```

### Progress Reporting

You can provide a callback function during initialization to receive progress updates.

```python
def my_progress_callback(progress_data):
    # progress_data is a dictionary with information about the download progress
    # Example keys: "type", "status", "song", "artist", "progress", "total_tracks", "current_track", "parent", etc.
    print(f"Progress: {progress_data}")

# For Deezer
# deezer_downloader = DeeLogin(arl="YOUR_ARL", progress_callback=my_progress_callback)

# For Spotify
# spotify_downloader = SpoLogin(
#     credentials_path="path/to/credentials.json",
#     spotify_client_id="YOUR_ID",
#     spotify_client_secret="YOUR_SECRET",
#     progress_callback=my_progress_callback
# )
```

## Configuration

### Deezer

- **ARL Cookie:** The primary method for Deezer authentication. You can obtain this from your browser's developer tools when logged into Deezer.
- **Email/Password:** Can be used but is less secure and may have limitations.

### Spotify

- **Spotify API Credentials:** You'll need a Client ID and Client Secret from the Spotify Developer Dashboard.
- **`credentials.json`:** This file is used by the underlying `librespot-spotizerr-phoenix` library for session management. The `SpoLogin` class requires the path to this file. If it doesn't exist, `librespot-spotizerr-phoenix` might attempt to create it or guide you through an authentication flow (behavior depends on `librespot-spotizerr-phoenix`).

## Logging

The library uses Python's standard `logging` module. You can configure the logging level and output:

```python
import logging
from deezspot import set_log_level, enable_file_logging, disable_logging

# Set logging level (e.g., INFO, DEBUG, WARNING)
set_log_level(logging.DEBUG)

# Enable logging to a file
# enable_file_logging("deezspot.log", level=logging.INFO)

# Disable all logging
# disable_logging()
```
