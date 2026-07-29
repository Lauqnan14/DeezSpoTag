# Internal candidate validation

DeezSpoTag changes are validated on the NAS through Gitea before they are
published to GitHub. This pipeline is independent of the production containers,
production Compose configuration, GHCR publishing, and GitHub Actions.

## Architecture

1. A change is committed locally and pushed to the Gitea `main` branch.
2. The Gitea runner builds a commit-specific candidate image.
3. The image is pushed through the NAS loopback interface to the private Gitea
   Container Registry at
   `127.0.0.1:3430/lauqnan14/deezspotag-candidate`. Loopback preserves the
   existing Docker registry policy without adding a LAN-wide insecure registry.
   Registry login retries the same loopback endpoint when the NAS token service
   temporarily exceeds Docker's client timeout; it never switches registries.
4. Trivy scans the exact pushed image. Any fixed HIGH or CRITICAL vulnerability
   fails the run before deployment.
5. A disposable container starts from that scanned image using:
   - the isolated loopback-only port `18668`;
   - a dedicated temporary data volume;
   - temporary download, review, and library filesystems;
   - no production configuration, media, database, or credentials.
6. The runner verifies the anonymous `/health` endpoint.
7. The test container, temporary data volume, and local candidate image are
   removed even when a preceding step fails.
8. GitHub publication remains a separate, explicit action after the Gitea run
   succeeds.

The jobs run sequentially in one workflow so the image that is tested is the
same image that was built, pushed, and scanned. Concurrent candidate runs are
disabled to prevent shared test-container or port collisions.

## Backup checkpoint

The initial pre-pipeline source checkpoint is the Gitea branch
`checkpoint-pre-gitea-validation-20260729` at commit
`88faf66c382b8085382137137d089d2e9f335b07`.

Rollback consists of stopping/removing the disposable candidate container and
resetting Gitea `main` to that checkpoint. Production containers are not part of
the candidate workflow and do not require rollback.

## Required Gitea secrets

The repository stores these encrypted Actions secrets:

- `REGISTRY_USERNAME`: the Gitea package owner;
- `REGISTRY_TOKEN`: a package-scoped Gitea access token.

Credentials must never be committed to this repository or embedded in an image.

## Promotion rule

A commit may be published to GitHub only after its Gitea candidate run has:

- built and pushed the candidate image;
- passed the enforced Trivy vulnerability gate;
- started with isolated state;
- returned a healthy response;
- completed cleanup.
