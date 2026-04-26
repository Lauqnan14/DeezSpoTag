# DeezSpoTag UI Walkthrough


## How To Think About The App

This app has 3 main jobs:

- find music
- download and tag it
- keep your library organized

The left sidebar is your main map. The pages most people will use regularly are:

- `Home`
- `Library`
- `Media Management`
- `AutoTag`
- `Activities`
- `Settings`
- `Login`

There are also a few support pages like `Statistics` and `About`.

## 1. Home

This is the front door.

What you do here:

- search for songs, albums, artists, and playlists
- paste links from services like Spotify, Apple Music, Tidal, Qobuz, and others
- jump into discovery-style content the app loads on the home screen

Simple meaning:

- this is the "what do I want?" page

If you know what you want to download, you usually start here.

## 2. Search Results

After you search from Home, you land on the search results page.

What you do here:

- look through matching tracks, albums, artists, playlists, or videos
- choose the exact item you want
- send that item into the download flow

Simple meaning:

- this is the "pick the correct version" page

So Home is where you ask. Search Results is where you choose.

## 3. Login

This is where you connect your music and media accounts.

What you can connect:

- Deezer
- Spotify
- Apple Music
- Discogs
- Last.fm
- BPM Supreme
- Plex
- Jellyfin

Why this matters:

- some downloads, tagging features, artwork, lyrics, and library syncing need these accounts

Simple meaning:

- this is the "give the app permission to talk to your services" page

Without this page being set up properly, some other pages will only work partly.


## 4. Settings

This is the master control panel.

What you do here:

- choose the main download engine
- choose where downloads should go
- control how the app behaves in general

This is where you tell the app things like:

- "download from Qobuz"
- "save files in this folder"

Simple meaning:

- this is the "global rules for the whole app" page

Important:

- this page controls the main download behavior
- it does not replace AutoTag profile choices, it works alongside them

## 5. AutoTag

This is one of the most important pages in the whole project.

It controls how music files get cleaned up, tagged, enriched, and moved into the library.

Its tabs are basically the workflow:

- `Profiles`
  - save different rule sets for different situations
  - for example, one profile for singles, one for albums, one for a specific folder

- `Folders`
  - assign those profiles to real destination folders
  - this is how the app knows which rules to use for which library area

- `Platforms`
  - choose which metadata sources are allowed for enrichment

- `Download`
  - choose what tags get written during the download stage
  - this is where the tag-writing source lives
  - this is separate from Settings' download engine choice

- `Enrichment`
  - fill in missing metadata after download
  - improve genre, label, lyrics, artwork, and more

- `Enhancement`
  - clean up and standardize the finished files and folders

- `Technical`
  - advanced behavior and deeper file-handling rules
  - control naming rules for files and folders
  - set lyrics, artwork, video, conversion, and library settings

Simple meaning:

- this is the "make my files look correct and land in the right place" page

Very important distinction:

- `Settings` chooses where the audio comes from
- `AutoTag` chooses how the file is labeled and organized
- they are connected, but they are not the same thing

## 6. Activities

This is the live dashboard.

If you want to know "what is happening right now?", this is the page.

Its tabs are:

- `Downloads`
  - shows the queue
  - what is waiting, downloading, paused, completed, or failed

- `AutoTag Status`
  - shows what AutoTag is doing right now

- `History`
  - shows what already happened before

- `Media Operations`
  - shows file-processing actions and related tasks

- `Logs`
  - shows the running event log and errors

Simple meaning:

- this is the "control room" page

If something seems wrong, this is usually the first place to check.

## 7. Library

This is your finished collection.

What you do here:

- browse artists, albums, and tracks already saved
- scan the library
- refresh artwork and metadata views
- clean up missing files
- resolve unmatched artists

Simple meaning:

- this is the "what do I already own?" page

This page is less about downloading and more about maintaining the final collection.

## 8. Media Management

This page is for music organization and automation around playlists, watchlists, and related media.

Its major areas are:

- `Recommendations`
  - suggestions based on your library

- `Watchlist`
  - monitor playlists and artists so new music can be caught automatically

- `Soundtracks`
  - browse soundtrack-related media from connected libraries

- `Auto Playlists`
  - generated playlists

- `Library Playlists`
  - playlists already in Plex or Jellyfin

- `Favorites`
  - saved favorites from spotify and deezer

Simple meaning:

- this is the "automation and collection management" page

This page becomes more useful after your library and accounts are already set up.

## 9. Statistics

This is the numbers page.

What it shows:

- totals
- library breakdown
- counts of artists, albums, tracks, and other summary information

Simple meaning:

- this is the "how big and healthy is my library?" page

## 10. About

This is the reference page.

What it does:

- explains the project background
- shows credits and outside tools/projects it builds on


## 11. Smaller Support Pages

There are also a few more specific pages, like:

- `QuickTag / Tag Editor`
  - for manual tagging edits
- `Artist / Album / Track detail pages`
  - for drilling into library items
- `LRC`
  - lyrics-related view
- `Connect Test`
  - more of a technical/support page than a daily-use page

Most everyday users will not live in these pages, but they support the main workflow.

## The Easiest Way To Use The App Day To Day

For a normal person using the app, the flow is usually:

1. Go to `Login` and connect your services
2. Go to `Settings` and choose your general download behavior
3. Go to `AutoTag` and decide how files should be tagged and organized
4. Go to `Home` and search or paste a link
5. Add the music to the queue
6. Watch progress in `Activities`
7. Check the finished result in `Library`
8. Use `Media Management` if you want smarter playlist/watchlist automation

## In one simple sentence

- `Home` finds music
- `Settings` decides download rules
- `AutoTag` decides file quality and organization rules
- `Activities` shows live progress
- `Library` shows the finished collection
- `Media Management` automates ongoing music management
