# Provider Identity Boundaries Design

## Purpose

Prevent AutoTag providers from contaminating one another's identifiers and URLs. Every AutoTag execution path must be able to retain independently sourced identity tags from every provider on the same file, while overwrite affects only the selected provider and identity field.

This design covers provider-owned album IDs, release IDs, artist IDs, album-artist IDs, track IDs, and URLs across every AutoTag run type and every audio format accepted by AutoTag. It does not change ordinary descriptive metadata such as titles, artists, albums, genres, dates, artwork, or lyrics.

## Scope

The boundary is part of the shared AutoTag matching, writing, reconciliation, and verification pipeline. It applies equally to:

- standard and independent provider AutoTag runs;
- enhancement runs containing any combination of sections;
- manual enrichment;
- AutoTag invoked after downloads;
- batch-scoped and library-wide processing;
- resumed and recovered jobs.

No caller may opt out or use a parallel identity writer.

The same behavior is required for every extension currently accepted by `AutoTagService`:

- `.flac`, `.wav`, `.aiff`, `.aif`, `.alac`;
- `.m4a`, `.m4b`, `.mp4`, `.aac`;
- `.mp3`, `.mp2`, `.mp1`;
- `.wma`;
- `.ogg`, `.oga`, `.opus`;
- `.ape`, `.wv`, `.tta`;
- `.dsf`, `.dff`, `.mka`.

An accepted AutoTag format must never silently skip provider identity persistence or report success without reading the value back successfully.

## Required invariants

1. Every provider owns a separate namespace for each supported identity field:
   - `<PROVIDER>_TRACK_ID`
   - `<PROVIDER>_ALBUM_ID`
   - `<PROVIDER>_RELEASE_ID`
   - `<PROVIDER>_ARTIST_ID`
   - `<PROVIDER>_ALBUM_ARTIST_ID`
   - `<PROVIDER>_URL`
2. Provider-specific aliases remain inside the same provider and field family. Apple/iTunes compatibility aliases, for example, may map to one Apple-owned family but never to Spotify, Deezer, MusicBrainz, or another provider.
3. Overwrite is scoped to one provider and one field. Replacing `SPOTIFY_RELEASE_ID` cannot alter Spotify's other identity fields or any other provider's fields.
4. A provider value is writable only when captured authoritatively from that provider's response. Values inherited from the source file, album consensus, another provider, or a fallback match are not authoritative provider values.
5. A provider match that supplies no authoritative value does not delete or replace an existing provider value.
6. A fallback cannot claim the fallback provider's identity as the current platform's identity. A Shazam stage resolved through Deezer, for example, cannot create `SHAZAM_*` identity tags from Deezer values.
7. The generic compatibility fields `ALBUMID`, `ARTISTID`, `ALBUMARTISTID`, `RECORDINGID`, `URL`, and `WWWAUDIOFILE` are never populated, cleared, or overwritten by any provider AutoTag pass. Existing values remain untouched even when overwrite is selected.
8. A provider release ID and album ID are separate fields. They may contain the same value only when that provider's own response and documented model identify the same upstream entity; the application must not copy one into the other merely because the other is absent.
9. Provider-native album and release identities may be reconciled across files in the same edition-aware album. Track IDs and track URLs are always file-specific. Artist IDs must not be blindly propagated across featured or differing artists.
10. Sidecars may reuse only provider-native identities whose provider and field provenance has been confirmed.

## Current implementation

Provider identity currently has competing write paths:

- The primary tag writer includes guarded release-ID handling and provider alias-family cleanup.
- A later legacy custom-tag pass writes `${PLATFORM}_RELEASE_ID`, `${PLATFORM}_TRACK_ID`, `ALBUMID`, `ARTISTID`, `RECORDINGID`, and related URL fields independently.
- Album identity consensus and batch reconciliation retain provider release IDs, but other provider identity families are not represented with the same authority and provenance rules.
- URL handling writes the generic `WWWAUDIOFILE` field for provider results and treats Spotify as a special additional case.
- MusicBrainz release-ID aliases currently include generic `ALBUMID`.
- Presence and ID-shape checks cannot establish ownership. Numeric IDs from Deezer, iTunes, Shazam, Audiomack, Discogs, and other providers are indistinguishable by shape alone.
- AutoTag accepts 22 audio extensions, but its current raw provider-tag writer has explicit persistence branches only for MP3, FLAC, and the MP4 family. Other accepted formats can therefore pass through the run without equivalent provider-identity writes and verification.

The later custom-tag pass can therefore bypass the guarded writer and overwrite a correct provider value with a stale or foreign value. This directly caused the persistence failures and on-disk contamination observed in enhancement job `4b38b20190c14bf897451f7c74bc5a07`.

## Recommended architecture

### Authoritative provider identity payload

Capture an immutable provider identity payload immediately after a provider returns a successful match and before folder consensus, metadata preservation, or other provider-derived mutations run. The payload records:

- provider ID;
- track ID;
- album ID;
- release ID;
- artist ID;
- album-artist ID;
- URL;
- authority/provenance for each field.

The matched `AutoTagTrack` remains the source for descriptive metadata. It is not the authority for provider identity after later processing has mutated or merged it.

Each provider adapter must populate only values returned by that provider. An inherited source-file value is not converted into an authoritative value. A fallback-backed match records the actual provenance and does not manufacture identity for the stage provider.

### Central provider tag contract

Extend the existing identity-tag mapping component into the single contract for provider and field families. For every supported provider and identity field, it defines:

- canonical write name;
- accepted compatibility aliases;
- aliases eligible for cleanup during overwrite;
- value validation where validation is meaningful;
- whether a provider legitimately uses one upstream value for both album and release identity.

Validation supplements provenance but never substitutes for it. A correctly shaped value is not considered provider-owned without an authoritative payload.

### Single shared writer

All provider identity fields are written through one format-independent operation used by every AutoTag caller. It delegates persistence to one deterministic adapter selected from the actual container/tag type, not from the run type.

For each authoritative non-empty field:

1. Resolve its exact provider-and-field alias family.
2. If overwrite is enabled for that configured field, remove only aliases in that family.
3. Write the canonical provider-specific aliases for that field.
4. Verify that the expected provider aliases contain the authoritative value.
5. Verify that no unrelated provider family was changed by the operation.

If a field has no authoritative value, the writer leaves its existing aliases untouched.

The legacy custom-tag pass will no longer build or write first-class identity fields or provider URLs. It remains responsible only for genuinely custom metadata that has no dedicated writer. This removes the competing authority rather than adding another guard around it.

### Audio-format persistence

Reuse and consolidate the project's existing tag mechanisms instead of creating a separate tagging subsystem:

- ID3-family persistence for formats carrying ID3 tags, including MP3 and the supported WAV/AIFF variants;
- Xiph/Vorbis persistence for FLAC, OGG, OGA, and Opus;
- MP4 free-form atoms for M4A, M4B, MP4, AAC/ALAC files whose detected container uses MP4 metadata;
- ATL native additional fields for accepted formats whose native metadata is not covered by the three direct paths, including WMA/ASF, APE-family, WavPack, TTA, DSD, and Matroska where supported by their detected tag type.

Format dispatch must inspect the actual supported tag/container type when an extension can represent more than one container. The same adapter must perform write, removal, presence checks, and read-back verification so those operations cannot disagree.

If a file extension is accepted by AutoTag but its actual container/tag type cannot persist a requested provider field, the item must report a precise unsupported-persistence error. It must not be reported as successfully tagged, and no alternate generic field may be used as a substitute.

### Generic compatibility fields

No provider AutoTag pass may write:

- `ALBUMID`
- `ARTISTID`
- `ALBUMARTISTID`
- `RECORDINGID`
- `URL`
- `WWWAUDIOFILE`

These fields are also excluded from provider cleanup families. Their existing logical values are preserved. MusicBrainz-specific fields such as `MUSICBRAINZ_ALBUMID` remain provider-owned, but generic `ALBUMID` is removed from the MusicBrainz release-ID family.

### Album reconciliation

Extend the existing edition-aware album identity state to retain confirmed provider album IDs and provider release IDs as separate maps. Reconciliation may apply those confirmed album-scoped values to every file in the same resolved album edition.

Reconciliation must not copy:

- track IDs;
- track URLs;
- track-specific artist and album-artist IDs;
- an album ID into a release-ID field;
- a release ID into an album-ID field;
- any value into another provider's namespace.

The existing folder key, edition normalization, persisted album identity store, batch boundaries, and album-coherent batching remain in use.

### Sidecar identity consumption

Sidecar lookup must prefer the confirmed provider identity state produced by matching and persisted album reconciliation. Raw provider tags found on disk are not automatically trusted merely because their spelling or shape looks valid. Existing unconfirmed tags may remain on disk, but they cannot seed a provider request until confirmed by that provider.

## Overwrite semantics

Overwrite is evaluated as a two-dimensional key: `(provider, identity field)`.

Examples:

- Spotify release overwrite may replace Spotify release aliases only.
- Spotify album overwrite may replace Spotify album aliases only.
- iTunes track overwrite may replace Apple/iTunes track aliases only.
- MusicBrainz album overwrite may replace MusicBrainz album aliases only and never generic `ALBUMID`.
- URL overwrite may replace only that provider's URL aliases and never `WWWAUDIOFILE`.

Unselected identity fields and all unrelated provider families remain unchanged.

## Existing components to reuse

- Provider matchers and their provider-native response models.
- `AutoTagIdentityTags` as the home for explicit identity aliases.
- Existing FLAC/Vorbis, MP3/ID3, and MP4 raw-tag helpers.
- Existing ATL native-tag and `AdditionalFields` handling for the remaining accepted formats.
- Existing overwrite configuration and supported-tag mapping.
- Edition-aware album keys, album identity registry/store, and batch reconciliation.
- Existing persistence verification and status reporting, extended to verify exact provider-and-field families.

## Code to remove or stop using

- Provider ID and URL entries generated by the legacy custom-tag builder.
- Provider writes to generic `ALBUMID`, `ARTISTID`, `ALBUMARTISTID`, `RECORDINGID`, `URL`, and `WWWAUDIOFILE`.
- Generic `ALBUMID` from the MusicBrainz release-ID write and cleanup families.
- Shape-only provider ownership decisions.
- Sidecar seeding from unconfirmed raw provider identifiers.
- Release-ID-only authority fields once all callers use the generalized provider identity payload.

Removal is limited to paths made obsolete by the centralized provider identity contract. Unrelated custom-tag behavior remains unchanged.

## Compatibility and migration

- No settings migration is required. Existing field-selection and overwrite choices continue to control whether a field is eligible across all AutoTag run types.
- Existing correctly namespaced provider tags remain readable.
- Existing generic compatibility fields remain untouched.
- Existing contaminated provider tags are replaced only when the corresponding provider returns an authoritative value and overwrite is enabled.
- A contaminated tag for which no authoritative replacement is returned remains on disk but is excluded from trusted identity and sidecar reuse.
- Existing Apple/iTunes aliases remain compatible within the Apple-owned mapping.

## Error handling

- Provider identity capture failures do not fail unrelated descriptive tagging; the missing identity field is left untouched and reported as not written.
- Persistence verification reports the exact provider and field family that failed.
- Album reconciliation rejects cross-provider and cross-field writes before opening the target file.
- A provider response whose identity value violates that provider's explicit validator is not authoritative and cannot overwrite existing tags.
- Unsupported persistence for an accepted audio container is reported explicitly for that file, provider, identity field, and format.

## Verification strategy

Tests must first reproduce the current failure and then cover:

1. A FLAC file retaining IDs and URLs from several providers simultaneously.
2. Provider-and-field overwrite isolation for all five identity families.
3. Preservation of unselected fields from the same provider.
4. Preservation of every unrelated provider family.
5. Preservation of existing `ALBUMID`, `ARTISTID`, `ALBUMARTISTID`, `RECORDINGID`, `URL`, and `WWWAUDIOFILE` values.
6. No creation of generic compatibility fields when they are absent.
7. Shazam-through-Deezer and equivalent fallback attribution.
8. Independent album-ID and release-ID values, including providers where those values differ.
9. Album-wide propagation of confirmed provider album/release IDs within one edition only.
10. No propagation of track IDs, track URLs, or featured-artist IDs.
11. Rejection of unconfirmed disk tags as sidecar lookup identities.
12. Equivalent provider-boundary behavior for every AutoTag run type, including standard, enhancement, manual, post-download, batch, library-wide, resumed, and recovered runs.
13. Write, overwrite, cleanup, and read-back behavior for every AutoTag-eligible audio extension and each detected tag/container family.
14. Explicit failure instead of silent success when an accepted file cannot persist a provider identity field.
15. Exact verification diagnostics identifying run, format, provider, and field.
16. The observed MusicBrainz/iTunes contamination scenario from enhancement job `4b38b20190c14bf897451f7c74bc5a07`, retained only as one regression example rather than the feature scope.

Completion requires the focused regression suite, the full test suite, a clean build, and a final diff audit against every invariant in this document.

## Out of scope

- Changes to descriptive metadata precedence.
- Changes to matching thresholds or provider search behavior except identity provenance capture.
- Changes to title, album-title, or edition wording.
- Changes to lyrics, artwork, folder templates, Folder Uniformity, download deduplication, or UI.
- Automatic deletion of contaminated provider tags without an authoritative replacement.
- Modification or deletion of generic compatibility fields.
- Expanding the set of audio extensions accepted by AutoTag; this work guarantees consistent behavior for the existing set.
