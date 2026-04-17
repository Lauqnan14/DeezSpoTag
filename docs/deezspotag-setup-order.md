# Best Order To Configure DeezSpoTag

Here is the best order, in simple practical terms.

If you set the app up in this order, downloads are much less likely to behave strangely.

## 1. Start With Login

Go to `Login` first.

Connect the services you actually plan to use:

- Deezer
- Spotify
- Apple Music - Requires active subscription to fetch audio files and lyrics
- Plex or Jellyfin if you use a media server
- any metadata-related services you care about

Why first:

- many features depend on these connections
- if login is missing, later settings may look correct but still not work properly

Simple rule:

- connect accounts before touching anything else

## 2. Set The Main Download Rules In Settings

Next go to `Settings`.

Set the global things first:

- `Download Source` / engine
- download location

This page should answer:

- where audio should come from
- where raw downloads should go

Important:

- this is the main app-wide behavior


## 3. Go To AutoTag And Create Profiles

After global settings are stable, go to `AutoTag`.

Start with `Profiles`.

Make profiles for the kinds of behavior you want.

Examples:

- streaming sources profile
- specific genre profile
- vintage & rare profile
- general profile

Think of a profile as:

- "when a file goes to this kind of place, use these tagging and organization rules"


## 4. Set Up Your Library Destinations

Now go to `AutoTag > Folders`.

In this tab, make sure your library paths and server/library-related settings are correct.

Examples:

- music library path
- video/podcast path if used
- Plex/Jellyfin libraries if applicable

Why this matters:

- if the destinations are wrong, downloads may finish but land in the wrong place
- later AutoTag folder assignments depend on real destination structure making sense


- quality - This is different from download quality. This is mainly used in the statistics page to highlight the number of files that fall below your desired quality for that particular desired quality
- naming templates
- artist and album folders creation behavior
- lyrics/artwork defaults
- conversion options if you use them


Match real destination folders to the right profile.

This is extremely important.

Why:

- this is how the app knows which AutoTag rules to apply after a download
- if a folder is not assigned properly, downloads will not trigger to that folder

- However, Videos and Podcasts do not require tagging, so those are downloaded to the Library folder directly

Simple rule:

- every important destination folder should have the correct AutoTag profile attached. They can all use the same profile. 

Make sure the folder is enabled for it to be among the download destination options.


## 5. Configure AutoTag Download-Stage Metadata Carefully

Now go to `AutoTag > Download`.

This is where many people get confused.

Remember:

- `Settings > Download Source` = where the audio comes from
- `AutoTag > Download Tag Metadata Source` = where the tags written during download come from

Best default:

- use `Follow download engine`

That keeps things aligned and reduces surprises.

Only choose an override like Deezer or Spotify if you specifically want:

- audio from one place, but tags from another place

Simple advice:

- if you want stable behavior, start with `Follow download engine`

## 6. Configure Enrichment And Enhancement After That

Then go through:

- `AutoTag > Enrichment`
- `AutoTag > Enhancement`
- `AutoTag > Technical`

This is where you fine-tune:

- missing metadata filling
- artwork improvements
- lyrics behavior
- cleanup rules
- library reorganization behavior

Why now:

- these are second-layer refinements
- they work best after engine choice, folders, and profiles are already correct

If you do these too early, you may be polishing the wrong setup.


## 7. Save Preferences And Test With One Or Two Downloads

Before doing a big batch, test with a small sample.

Try:

- one track
- one album
- maybe one playlist item

Then check:

- did it download from the expected source?
- were tags written correctly?
- did it land in the right folder?
- did artwork and lyrics behave the way you expected?
- did AutoTag use the right profile?

Do not start with 500 downloads.
Start small.

## 8. Watch Activities While Testing

Open `Activities` while doing those small tests.

Check:

- `Downloads`
- `AutoTag Status`
- `History`
- `Logs`

This tells you:

- whether the queue is behaving properly
- whether AutoTag is applying
- whether hidden errors are happening

If something is wrong, fix it now before scaling up.

## 9. Only After That, Use Media Management Automation

Once basic downloading works correctly, then move to `Media Management`.

That includes things like:

- watchlists
- auto playlists
- monitored artists/playlists
- soundtrack and recommendation flows

Why last:

- automation multiplies mistakes
- if your base download and tagging setup is wrong, automated jobs will repeat the wrong behavior over and over

So first make one download behave correctly.
Then automate.

## Best Order In One Short List

1. `Login`
2. `Settings` download engine and basic download rules
4. `AutoTag > Profiles`
5. `AutoTag > Folders`
6. `AutoTag > Download`
7. `AutoTag > Enrichment / Enhancement / Technical`
8. test a few downloads
9. verify in `Activities`
10. then enable `Media Management` automation

## Best Beginner Default

If you want the safest starting point:

- connect your accounts
- choose one main download engine in `Settings`
- set your download folder and naming rules
- create one main AutoTag profile
- assign your main music folder to that profile
- set AutoTag download metadata source to `Follow download engine`
- test with one song

That is the cleanest setup.

## The Main Mistake To Avoid

Do not configure the app in this wrong order:

- first set complicated AutoTag rules
- then later change Settings download engine and paths
- then turn on watchlists

That creates mismatches.

The right way is:

- global rules first
- folder/profile matching second
- automation last
