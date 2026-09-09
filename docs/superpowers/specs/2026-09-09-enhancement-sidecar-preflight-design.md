# Enhancement Sidecar Preflight and Download-Style Activity Design

Date: 2026-09-09

## Objective

Make enhancement-side sidecar processing library-aware before it performs a provider lookup. Enhancement must reuse the established download-side sidecar activity wording, lifecycle, and rendering while retaining its own enhancement selections and eligibility rules.

Download behavior and download policy remain unchanged.

## Confirmed semantics

- Enhancement updates metadata independently from sidecar maintenance. Metadata platforms may have changed since download, or a newly enabled platform may now provide tags that were unavailable during download.
- A sidecar lookup is not required merely because a sidecar option is enabled. The existing library artifact must first be inspected.
- A satisfactory existing artifact causes no provider lookup and no `Fetching ...` activity.
- An existing artifact that is below the quality selected by the user creates an eligible lookup. The existing artifact is replaced only if the lookup returns a valid improvement permitted by the selected enhancement settings.
- A failed or unsuitable lookup preserves the existing artifact.
- `.elrc` is not a supported application artifact. The app must not create, detect, plan around, badge, migrate, rename, overwrite, or delete `.elrc` files.
- **Enhanced lyrics** means a word-synced `.lrc` file.
- **Synced lyrics** means a line-synced `.lrc` file.
- The application must identify both kinds by parsing LRC timing content, not by using a separate extension or trusting a filename.
- TTML is identified and labeled explicitly as TTML. It is not called enhanced lyrics or synced lyrics.

## Canonical lyrics terminology

The following display meanings apply consistently throughout the application wherever lyric type or timing quality is presented, including settings, enhancement decisions, Activities, library status, badges, result messages, and logs:

| Display term | Exact meaning |
| --- | --- |
| Enhanced lyrics | Word-synced `.lrc` |
| Synced lyrics | Line-synced `.lrc` |
| Unsynced lyrics | Untimed lyrics, normally `.txt` or embedded text |
| TTML lyrics | `.ttml`, with its TTML timing quality described separately when needed |

Internal provider tokens may remain compatible with persisted settings, but they must be mapped to these canonical meanings at application boundaries. A word-synced `.lrc` must never be reported as merely synced when enhanced timing has been detected. A line-synced `.lrc` must never receive an enhanced badge. TTML must never receive an enhanced-lyrics badge solely because it contains word timing.

## Existing implementation

### Download sidecars

`EngineAudioPostDownloadHelper` derives four ordered work categories from download settings:

1. album artwork;
2. animated artwork;
3. artist artwork;
4. lyrics.

`EngineQueueProcessorHelper` emits one `fetchingSidecars` event per queue file before the eligible artwork and lyrics lookups execute. `DescribePrefetchWork` formats the visible activity as `Fetching X`, or `Fetching X, Y and Z`. The Activities download card renders this text in a `task-activity` line beneath the title and artist.

### Enhancement sidecars

`AutoTagService.EnhancementWorkflows` currently starts lyrics and cover maintenance independently. It emits `Fetching lyrics` before `LyricsRefreshQueueService` has resolved the assigned profile or inspected the existing lyrics. Cover activity is similarly emitted before `CoverLibraryMaintenanceService` builds its actual album work plan.

The enhancement Activities client reconstructs combined activity by parsing message text and merging history records. This is not authoritative and can claim that work is being fetched when the maintenance service will skip it.

### Existing library awareness

- `LyricsService`, `LyricsSidecarTimingBadges`, `LrcContent`, and Apple lyrics helpers can inspect line-synced LRC, word-synced LRC, TTML, and unsynchronized lyric content.
- `CoverLibraryMaintenanceService` already inspects embedded covers, external covers, dimensions, recognized animated artwork, and legacy animated-artwork names.
- `LibraryArtistImageQueueService`, `ArtistArtworkCatalogService`, and `DownloadEngineArtworkHelper` already inspect artist artwork, provenance, preferred provider, dimensions, and overwrite protection.
- `IDownloadTagSettingsResolver` resolves the profile assigned to a library folder. Its profile contains technical lyrics settings and runtime artwork preferences.

## Approaches considered

### Enhancement-side preflight plan

Inspect the library first, create an explicit plan, and use the same plan to drive both activity and execution. Existing lyrics and artwork services remain responsible for provider resolution and writes.

This is the selected approach because the activity is truthful, unnecessary provider traffic is avoided, and enhancement policy remains independent from download policy.

### Synthetic download prefetch requests

Adapt existing library files into fake download queue items and run the download prefetch engine. This was rejected because the engine depends on queue UUIDs, download payloads, temporary paths, and post-download state. It would couple enhancement to download internals and could change download behavior.

### Frontend-only activity merging

Keep independent enhancement tasks and combine their messages in JavaScript. This was rejected because the browser cannot determine whether a lookup was actually required and would continue displaying premature activity.

## Architecture

### Shared sidecar activity formatter

Extract the existing ordered message construction into a small shared formatter that accepts the four sidecar work flags. Both callers supply their own eligibility decisions:

- download continues to use its existing prefetch requirements;
- enhancement supplies its library-aware preflight plan.

The formatter must preserve the current download wording and order exactly. Refactoring the download caller to use the formatter must not change its policy, event state, timing, or output.

### Enhancement work plan

An enhancement work plan represents the work that will actually be attempted for one active library item. It contains:

- stable track identity and current audio path;
- resolved destination folder and assigned profile;
- current lyric artifact state and timing quality;
- current album artwork state;
- current animated-artwork state;
- current artist-artwork state;
- flags for album artwork, animated artwork, artist artwork, and lyrics lookup;
- reasons for each planned or skipped category.

The plan is transient. It is not a new database entity and requires no migration.

The planner must call or expose the existing inspection rules from the lyrics and artwork services. It must not reimplement those policies in `AutoTagService`.

### Profile and enhancement inputs

Eligibility combines:

1. the enabled enhancement Sidecars workflow;
2. the specific enhancement lyrics and cover actions selected by the user;
3. the profile assigned to the file's destination library folder;
4. the profile's technical lyrics and runtime artwork preferences;
5. the current artifact state.

The planner does not import or alter download overwrite policy. General AutoTag tag-overwrite settings do not authorize sidecar replacement.

Existing sidecar-specific actions keep their meanings. Examples include missing embedded cover replacement, external-cover synchronization, low-resolution upgrades, animated-artwork completion, animated-artwork overwrite, and synced-to-enhanced lyrics upgrades.

### Lyrics classification and planning

The lyrics inventory recognizes these outputs:

- `.lrc`, classified by parsed content as synced lyrics (line-synced) or enhanced lyrics (word-synced);
- `.ttml`, classified by parsed content and accepted only according to the existing TTML quality rules;
- `.txt`, representing unsynchronized lyrics where the selected profile permits it;
- embedded lyrics where relevant to the existing save behavior.

`.elrc` is excluded from application-wide lyric inventory and from the enhancement decision matrix.

The planner compares the inventory with the assigned profile's requested lyric types, output formats, timing preference, provider order, and synthesis settings.

Representative decisions:

- Enhanced lyrics requested and a valid word-synced `.lrc` exists: skip lookup.
- Enhanced lyrics requested and only synced lyrics—a line-synced `.lrc`—exists: plan lyrics lookup.
- Prefer-enhanced-else-synced selected and only a valid line-synced `.lrc` exists: the existing synced lyrics satisfy the fallback; do not fetch solely to chase enhanced lyrics unless the enhancement rewrite/upgrade action is selected.
- Synced lyrics requested and a valid line-synced or word-synced `.lrc` exists: do not downgrade or refetch.
- TTML requested and satisfactory TTML exists: skip that output.
- Only unsynchronized output requested and satisfactory `.txt` exists: skip lookup.
- Required outputs are missing, or an explicitly selected quality upgrade is possible: plan lookup.
- Lyrics are disabled by the assigned profile: skip lookup.

When a lookup is planned, the existing provider-resolution path is used. The write step revalidates the returned timing quality. Synced lyrics cannot replace existing synced lyrics during an enhanced-lyrics upgrade attempt. Valid enhanced lyrics may replace the line-synced `.lrc` because the user explicitly selected that upgrade.

### Artwork classification and planning

Album and animated artwork reuse `CoverLibraryMaintenanceService` inspection and work-plan logic. A read-only planning entry point exposes the existing decision before execution so activity can be emitted only for actual lookup work.

Still artwork is planned only when the selected enhancement action and profile permit the required missing, synchronization, or low-resolution update. Animated artwork is planned only when the selected completion or overwrite action requires a provider lookup; local rename and removal actions are maintenance work but are not described as fetching.

Artist artwork reuses the existing library artist-artwork inspection, provider preference, provenance, dimension, and overwrite-protection rules. It is eligible only when the enhancement Sidecars workflow and applicable artwork selection are enabled and the assigned profile requests artist artwork. Existing satisfactory artist artwork produces no lookup.

### Unified execution

The enhancement coordinator consumes the plan. If at least one fetch flag is set, it records one structured fetching status immediately before starting the planned provider operations. The visible message comes from the shared formatter.

Independent planned operations may execute in parallel, following the download-side sidecar flow. The coordinator awaits all operations and records one completion state for the same stable item.

Album work is deduplicated per album directory and artist work per library artist. The first representative item that performs shared work owns the live fetch activity. Later tracks consume or re-inspect the shared result and must not claim a duplicate fetch.

A plan with only local maintenance work may record its completion result but does not emit a `Fetching ...` message. A plan with no work records an existing/retained or skipped result without provider access.

### Structured activity state

Enhancement status records carry an explicit sidecar activity state and the already formatted message. The client must not infer categories by searching message text.

The stable UI key is the library track ID, with normalized path as a fallback when a track ID is unavailable. A fetching status and its completion status update the same logical card.

### Activities rendering

The enhancement Sidecars view uses the download activity presentation contract:

- the same `task-activity` class and shared styling;
- the exact shared message beneath title and artist;
- no fetching badge or separate placeholder row;
- activity visible only while that item is active;
- activity removed when the completion state arrives;
- retained and newly created sidecar badges shown on the completed card.

Run-level batch heartbeats remain separate and never render as file activity. The current message-parsing and guessed category-merging code becomes dead code and is removed.

## Data flow

1. Enhancement selects its current batch of library files.
2. Each file resolves to its library track, destination folder, and assigned profile.
3. The planner inventories lyrics, album artwork, animated artwork, and artist artwork using existing inspection helpers.
4. The planner applies enhancement selections and profile preferences to produce actual fetch flags and local-maintenance flags.
5. Album and artist plans are deduplicated by their natural scope.
6. If fetching is required, the coordinator persists a structured fetching status whose message comes from the shared formatter.
7. Existing services perform only the planned lookups and writes.
8. Writers validate filesystem state and returned artifact quality before replacement.
9. The coordinator persists a completion, retained, skipped, or error result for the same stable item.
10. Activities renders the latest state on one card without parsing the message.

## Error handling and consistency

- A provider miss or lower-quality result retains the existing artifact.
- Failure in one sidecar category does not erase successful results from another category.
- Files that disappear are skipped explicitly.
- Files without a resolvable library track, folder, or assigned profile are skipped explicitly.
- Cancellation flows through the existing enhancement cancellation token.
- Before any write, the service rechecks the target artifact so a sidecar created or changed after planning is not improperly overwritten.
- Result details distinguish updated, retained, unavailable, skipped, and failed work.

## Scope boundaries

The implementation must not change:

- download eligibility, settings, overwrite policy, queue behavior, or event lifecycle;
- metadata/tag enhancement selection or execution;
- provider priority or fallback behavior;
- sidecar names or formats other than removing stale `.elrc` participation throughout the application;
- folder organization;
- database schema;
- existing enhancement preference meanings.

No new fallback, preference, database entity, or background queue is introduced.

## Testing strategy

### Shared formatting

- Verify every one-, two-, three-, and four-category message and the exact category order.
- Verify the download caller produces the same strings and `fetchingSidecars` state as before.

### Lyrics planning and execution

- Word-synced `.lrc` is detected from content, labeled enhanced lyrics, and satisfies an enhanced-lyrics request.
- Line-synced `.lrc` is detected from content, labeled synced lyrics, and is not misclassified as enhanced.
- A line-synced `.lrc` plus an enabled enhanced-lyrics upgrade plans `Fetching lyrics`.
- A valid word-synced result replaces the line-synced `.lrc`.
- A line-synced, invalid, or missing upgrade result retains the existing `.lrc`.
- Settings, library status, Activities, badges, result messages, and logs use the canonical terms consistently.
- Word-timed TTML is labeled TTML rather than enhanced lyrics.
- A satisfactory `.lrc`, `.ttml`, or `.txt` suppresses unnecessary lookup according to selected outputs.
- `.elrc` neither satisfies nor triggers any decision and is not created, badged, changed, or deleted.
- Disabled profile outputs do not fetch.

### Artwork planning and execution

- Satisfactory embedded and external artwork suppress lookup.
- Missing, synchronization, and low-resolution selections plan only their eligible work.
- Existing satisfactory animated artwork suppresses lookup.
- Local animated rename/removal does not produce a fetching message.
- Artist artwork follows profile preference, provenance, quality, and protection rules.
- Album and artist provider lookups are deduplicated.

### Coordination and UI

- Combined work uses the exact shared ordering and wording.
- A no-work plan emits no fetching status.
- Fetching and completion records share a stable item key.
- One Activities card updates in place and removes activity at completion.
- The client uses structured activity state and contains no sidecar-message parsing fallback.
- Batch heartbeat records never appear as sidecar item cards.

### Regression verification

- Run focused download-side sidecar tests to prove unchanged behavior.
- Run focused enhancement, lyrics, cover maintenance, artist artwork, Activities contract, and configuration tests.
- Build the full solution.
- Inspect the final diff for unrelated changes and compare every implementation item with the approved plan.

## Acceptance criteria

- Enhancement performs no sidecar provider lookup when existing artifacts satisfy the selected preferences.
- An explicitly eligible synced-to-enhanced LRC upgrade displays `Fetching lyrics` and replaces the file only with a verified word-synced `.lrc`.
- Enhanced lyrics are reliably identified as word-synced `.lrc` content.
- Synced lyrics are reliably identified as line-synced `.lrc` content.
- The canonical enhanced, synced, unsynced, and TTML terminology is used consistently throughout the application.
- `.elrc` has no role in application lyric or sidecar behavior.
- Every visible fetching message describes only work that is actually attempted.
- Combined messages exactly match download wording and ordering.
- Enhancement sidecar activity renders with the download activity style on one stable per-item card.
- Album and artist lookups are not repeated for every track.
- Existing artifacts survive provider misses, unsuitable results, and disallowed replacement.
- Download policy and behavior remain unchanged.
