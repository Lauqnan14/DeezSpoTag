# Regression-Safe Playback/MATCH Refactor Plan (Phase 1+)

## Goal
Stabilize playback and matching by moving from duplicated page logic to one shared client pipeline, without behavior regressions.

## Non-Negotiable Rules
1. No large rewrites.
2. One migration axis per phase.
3. Compatibility wrappers first, behavior switch later.
4. Delete legacy code only after parity window.

## Current Risk Hotspots
1. `DeezSpoTag.Web/wwwroot/js/home-index.js`
2. `DeezSpoTag.Web/wwwroot/js/library.js`
3. `DeezSpoTag.Web/Views/Tracklist/Index.cshtml`
4. Resolver split:
   - `/api/spotify/resolve-deezer`
   - `/api/resolve/deezer`
5. Page-local playback state machines with different transition timing.

## Phase 1 (Scaffold, No Behavior Change)
### Deliverables
1. Shared facade:
   - `DeezSpoTag.Web/wwwroot/js/deezer-playback-facade.js`
2. Global script registration:
   - `DeezSpoTag.Web/Views/Shared/_CommonFooterScripts.cshtml`

### Purpose
Create one canonical entrypoint for:
1. Spotify URL -> Deezer track resolve (+ metadata fallback)
2. Deezer stream URL resolve
3. Cache control

### Commit Boundary
Single commit containing only:
1. New facade file
2. Shared script include
3. No page behavior switch

## Phase 2 (Trending Move to Facade)
### Target Files
1. `DeezSpoTag.Web/wwwroot/js/home-index.js`

### Scope
1. Replace direct resolver calls with `DeezerPlaybackFacade`.
2. Preserve existing UI behavior (no new states, no styling changes).
3. Keep queue logic unchanged.

### Commit Boundary
Single commit limited to `home-index.js`.

## Phase 3 (Tracklist Move to Facade)
### Target Files
1. `DeezSpoTag.Web/Views/Tracklist/Index.cshtml`

### Scope
1. Replace row-level resolver split with facade.
2. Keep existing `playPausePreview` state machine unchanged.
3. Remove duplicate direct endpoint wiring from tracklist script.

### Commit Boundary
Single commit limited to tracklist view script.

## Phase 4 (Library/Artist/Shazam Move to Facade)
### Target Files
1. `DeezSpoTag.Web/wwwroot/js/library.js`
2. `DeezSpoTag.Web/Views/Artist/Index.cshtml`
3. `DeezSpoTag.Web/Views/Shazam/Results.cshtml`

### Scope
1. Replace local resolver calls with facade.
2. Keep existing UI states unchanged in each page.

### Commit Boundary
1 commit per file group (max 2 files/commit).

## Phase 5 (Single Playback State Contract)
### Scope
1. Define canonical transitions:
   - idle -> requested -> playing -> ended/error
2. Apply shared helper only after all pages use facade.
3. Remove per-page divergent transitions.

### Commit Boundary
One helper commit + one adoption commit per page.

## Phase 6 (Cleanup Legacy Paths)
### Scope
1. Remove dead resolver utilities.
2. Remove duplicate endpoint invocation patterns in client scripts.
3. Keep one documented fallback path.

### Commit Boundary
Small cleanup commits only after parity validation window.

## Operational Checklist Per Phase
1. Verify build.
2. Manual parity check:
   - Trending play
   - Tracklist play
   - Search->Tracklist play
3. No extra UI changes in the same commit.
4. No endpoint contract change in facade adoption commits.

## Rollback Strategy
Every phase is isolated so rollback is one commit revert:
1. Revert phase commit.
2. Keep prior phases intact.
3. No schema/data migration dependency.
