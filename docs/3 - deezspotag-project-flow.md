# DeezSpoTag Project Flow

This project is basically a music downloading and library-organizing machine.

In plain English, it does 4 big jobs:

1. It lets you choose what to download and how you want it saved.
2. It downloads the audio from a music service.
3. It writes tags and artwork into the file so the file "knows" what song it is.
4. It organizes the finished file into your music library so it ends up in the right folder with the right metadata.

Here is the full flow from beginning to end.

## Big Picture

Think of the app like a smart warehouse for music files.

- The **Settings page** is where you choose the main rules for downloading.
- The **Download system** is the worker that goes out and gets the music.
- The **AutoTag page** is the quality-control and labeling station.
- The **Library system** is the filing cabinet that stores everything neatly.
- The **Activities page** is the control room where you watch what is happening.

## Step 1: You set your preferences

Before downloading, you tell the app what kind of behavior you want.

Examples:

- Which **download engine** to use: Deezer, Qobuz, Apple, Tidal, Amazon, or Auto.
- Where downloaded files should go first.
- What naming style to use for songs, albums, and playlists.
- Whether to save artwork, animated album artwork where available, lyrics, synced lyrics, and other extras.

Important distinction:

- The **Settings page download source** decides **where the app will try to get the audio from**.
- The **AutoTag page download tag metadata source** decides **whose metadata should be written into the file while downloading**.
- Those are now treated as two separate things, but they can also work together if AutoTag is set to "follow download engine."

So:

- Settings answers: "Which shop do I buy the music from?"
- AutoTag answers: "Whose labels do I stick on the file?"

## Step 2: You add something to download

You usually give the app a link, search result, track, album, playlist, or artist item.

The app then figures out:

- what the item is,
- which music service it came from,
- whether it is a song, album, playlist, podcast, or video,
- what engine should handle it.

That item is then  **queued**.

## Step 3: The queue decides what happens next

The queue is the app's to-do list.

Each queued item carries details like:

- song title,
- artist,
- album,
- source link,
- chosen engine,
- destination folder,
- quality target,
- extra settings.

The app has the options to allow you, the user, to:

- start it,
- pause it,
- resume it,
- retry it,
- cancel it.

This is what you see in the **Downloads** tab on the **Activities page** .

## Step 4: The chosen engine downloads the file

Now the real work begins.

Depending on your Settings choice, the app will try to download through the right engine:

- Deezer
- Qobuz
- Apple
- Tidal
- Amazon
- or an automatic choice

That engine is responsible for actually fetching the audio.

At this point, the app is not just grabbing a file blindly. It is also checking things like:

- quality,
- availability,
- whether the song is already in the queue,
- whether the library already has it,
- whether fallback is needed.

## Step 5: Tags are written during download

As the file is being created, the app can write metadata into it.

Metadata such as:

- title,
- artist,
- album,
- track number,
- genre,
- year,
- label,
- lyrics,
- artwork,
- IDs like ISRC.

This is where the **AutoTag download metadata source** matters.

Example:

- You may download the audio using **Qobuz**
- but choose **Spotify** or **Deezer** as the metadata source for the tags
- or choose **Follow download engine**, which means the tagging metadata should come from the same source as the engine when possible

So the app can separate:

- "where the sound came from"
- from
- "where the labels and details came from"

That gives you more control.

## Step 6: AutoTag can enrich the file further

After the basic download is done, the project can run a second pass called **Enrichment**.

This is like a cleanup and improvement stage.

It can:

- fill in missing tags,
- improve genre/style info,
- add lyrics,
- improve artwork,
- apply technical metadata,
- standardize formatting,
- fix naming and folder layout.

This matters because downloads from different sources may not all come with equally complete metadata.

So Enrichment helps make your library consistent.

## Step 7: The file is moved into the library

Once the file is downloaded and tagged, the app places it into your library using your folder rules.

Examples:

- `Artist/Album/Track`
- playlist folders
- single-song folders
- special structure for CDs/discs
- custom naming templates

This is where the project becomes more than just a downloader. It is also a **library organizer**.

Its job is not only to fetch music, but to make sure your library stays clean and predictable.

## Step 8: The library can be monitored and maintained

After files land in the library, the project can keep working.

It can:

- detect duplicates,
- scan the library,
- update metadata,
- monitor watched folders,
- keep activity history,
- trigger more AutoTag actions later called **Enhancement**.

So it is not "download once and forget."
It is closer to a full music-management workflow.

## Step 9: You watch everything in Activities

The **Activities page** is where you monitor the whole system.

Its tabs roughly show:

- **Downloads**: what is queued, running, paused, completed, failed
- **AutoTag Status**: what AutoTag is doing right now
- **History**: what already happened
- **Media Operations**: file-processing actions
- **Logs**: the running record of events and errors

So if Settings is the rulebook, Activities is the live dashboard.

## In one sentence

The project takes a music link, decides how to download it, fetches the file, writes the right information into it, improves it with AutoTag, and files it into your library in the right place.

## The simplest mental model

You can think of it as this pipeline:

1. Choose rules in **Settings**
2. Choose tagging behavior in **AutoTag**
3. Add music to the **queue**
4. Download audio through the selected **engine**
5. Write tags and artwork into the file
6. Enrich and clean up with **AutoTag**
7. Move the file into the **library**
8. Watch progress in **Activities**
