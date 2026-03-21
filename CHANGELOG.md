# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.2.8] - 2026-03-21
### Added
- _TBD_

### Changed
- _TBD_

### Fixed
- _TBD_

### Security
- _TBD_


## [0.1.2.7] - 2026-03-21
### Added
- GitHub prerelease publication now tracks the amd64 publish path used for NAS-targeted parity.

### Changed
- Docker publish workflow audits and publishes `linux/amd64` images for both `deezspotag` and `deezspotag-apple-wrapper`.

### Fixed
- Apple wrapper login now retries transient Apple-side failures that surface as "system busy" or response type `4`.
- Wrapper runtime state tracking now records transient login failures and response types for cleaner retry handling.

### Security
- _TBD_

## [0.1.2.5] - 2026-03-20
### Added
- _TBD_

### Changed
- _TBD_

### Fixed
- _TBD_

### Security
- _TBD_

## [0.1.2.4] - 2026-03-20
### Added
- _TBD_

### Changed
- _TBD_

### Fixed
- _TBD_

### Security
- _TBD_


## [0.1.2.3] - 2026-03-20
### Added
- _TBD_

### Changed
- _TBD_

### Fixed
- _TBD_

### Security
- _TBD_


## [0.1.2.2] - 2026-03-20
### Added
- _TBD_

### Changed
- _TBD_

### Fixed
- _TBD_

### Security
- _TBD_


## [0.1.2.1] - 2026-03-20
### Added
- _TBD_

### Changed
- _TBD_

### Fixed
- _TBD_

### Security
- _TBD_


## [0.1.2.0] - 2026-03-20
### Added
- _TBD_

### Changed
- _TBD_

### Fixed
- _TBD_

### Security
- _TBD_


## [0.1.1.9] - 2026-03-20
### Added
- _TBD_

### Changed
- _TBD_

### Fixed
- _TBD_

### Security
- _TBD_


## [0.1.1.8] - 2026-03-20
### Added
- _TBD_

### Changed
- _TBD_

### Fixed
- _TBD_

### Security
- _TBD_


## [0.1.1.7] - 2026-03-20
### Added
- _TBD_

### Changed
- _TBD_

### Fixed
- _TBD_

### Security
- _TBD_


## [0.1.1.2] - 2026-03-05
### Added
- _TBD_

### Changed
- _TBD_

### Fixed
- _TBD_

### Security
- _TBD_


## [0.1.1.1] - 2026-03-04
### Added
- Added burst/librespot-based Spotify show/episode metadata path for podcast/episode tracklists.
- Added hidden username fields to password forms for browser accessibility/password-manager compatibility.

### Changed
- Home page now hides any section with fewer than 4 items.
- Home page rows with fewer than 7 items now expand to use available width (space-aware layout).
- Categories section is now placed immediately below "Recommended new releases for you".
- Startup log now includes build timestamp in addition to configuration and assembly version.
- Release automation now supports 4-part app versioning with revision rollover:
  - `0.1.1.1` ... `0.1.1.9`
  - next change rolls to `0.1.2.0`

### Fixed
- Wrapped password fields in real forms to remove DOM password-in-form warnings.
- Removed ghost home cards by filtering empty/invalid items (including `0 tracks / 0 fans` artifacts).

### Security
- _TBD_

## [0.1.1] - 2026-03-03
### Added
- _TBD_

### Changed
- _TBD_

### Fixed
- _TBD_

### Security
- _TBD_

## [0.1.0] - 2026-02-24
### Added
- Centralized project versioning in `Directory.Build.props`.
- Added repository-level changelog tracking.
- Added `scripts/release.sh` for semver/changelog/tag workflow.
