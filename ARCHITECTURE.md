# DeezSpoTag Architecture

## Purpose
This document defines the intended project layering and dependency boundaries for the `src` workspace.
All new code should follow these rules.

## Project Layers

### `DeezSpoTag.Core`
- Domain models, constants, shared primitives, low-level helpers.
- Must not depend on Web, API, Services, Data, or Integrations.

### `DeezSpoTag.Integrations`
- Provider clients and protocol adapters (Spotify, Deezer, Apple, Plex, Jellyfin, etc.).
- Depends on Core only.
- No Web/controller concerns.

### `DeezSpoTag.Data`
- Persistence models and storage infrastructure.
- Depends on Core (and integration-neutral libraries).
- No Web/controller concerns.

### `DeezSpoTag.Services`
- Application/domain orchestration: download pipeline, tagging pipeline, matching, queue coordination, library operations.
- Depends on Core, Integrations, and Data.
- Must not depend on Web controllers/views.

### `DeezSpoTag.Web`
- HTTP/UI host: controllers, Razor pages, request validation, auth/session, API surface.
- Orchestrates Services and host-only concerns.
- Web-specific helper services stay here only when they are presentation or host orchestration concerns.

### `DeezSpoTag.API`
- API-focused host surface.
- Uses Services and host concerns.
- No UI/Razor concerns.

### `DeezSpoTag.Tests`
- Unit/integration/guardrail tests across layers.
- Can reference multiple projects for coverage, but test setup should respect runtime boundaries.

## Dependency Direction
Allowed direction:
- `Core <- Integrations`
- `Core <- Data`
- `Core <- Services`
- `Integrations <- Services`
- `Data <- Services`
- `Services <- Web`
- `Services <- API`

Disallowed direction:
- `Web ->` referenced by `Services`, `Data`, `Integrations`, or `Core`
- `API ->` referenced by lower layers
- Any circular project references

## Service Placement Rules
- Put business rules, pipeline logic, and reusable orchestration in `DeezSpoTag.Services`.
- Put host-specific transport concerns in `DeezSpoTag.Web`/`DeezSpoTag.API`:
  - HTTP request/response shaping
  - endpoint-specific auth/rate-limit policies
  - Razor/view composition
- If a Web service is reused by non-Web hosts, move it into `Services` and keep a small Web adapter.

## Background Work Rules
- Long-running/background operations should use hosted services, queue services, or scheduler services.
- Avoid ad-hoc unmanaged fire-and-forget wrappers in request handlers.
- Ensure cancellation and shutdown paths are explicit for background jobs.

## Change Checklist
Before merging architectural changes:
- Confirm project reference graph has no cycles.
- Confirm moved logic still passes existing tests.
- Add/adjust tests around moved boundaries.
- Update this document when introducing a new layer or boundary rule.
