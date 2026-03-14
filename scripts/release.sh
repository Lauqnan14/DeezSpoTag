#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage:
  scripts/release.sh <change|revision|major|minor|patch|X.Y.Z|X.Y.Z.W> [--tag] [--push] [--dry-run]

Examples:
  scripts/release.sh change
  scripts/release.sh revision
  scripts/release.sh minor --tag
  scripts/release.sh 1.2.0.0 --tag --push

Behavior:
  - Updates VersionPrefix/VersionRevision in Directory.Build.props
  - change/revision: increments revision until .9, then rolls to next patch with .0
  - patch/minor/major: bumps patch/minor/major and resets revision to .0
  - Inserts a new version section in CHANGELOG.md under [Unreleased] if missing
  - Optionally creates a release commit + annotated git tag
  - Optionally pushes the commit + tag
EOF
}

SEMVER3_RE='^[0-9]+\.[0-9]+\.[0-9]+$'
SEMVER4_RE='^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
PROPS_FILE="${ROOT_DIR}/Directory.Build.props"
CHANGELOG_FILE="${ROOT_DIR}/CHANGELOG.md"

if [[ $# -lt 1 ]]; then
  usage
  exit 1
fi

BUMP_INPUT="$1"
shift

DO_TAG=false
DO_PUSH=false
DRY_RUN=false
git_root=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --tag)
      DO_TAG=true
      shift
      ;;
    --push)
      DO_PUSH=true
      DO_TAG=true
      shift
      ;;
    --dry-run)
      DRY_RUN=true
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage
      exit 1
      ;;
  esac
done

if [[ ! -f "$PROPS_FILE" ]]; then
  echo "Missing $PROPS_FILE" >&2
  exit 1
fi

if [[ ! -f "$CHANGELOG_FILE" ]]; then
  echo "Missing $CHANGELOG_FILE" >&2
  exit 1
fi

current_prefix="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$PROPS_FILE" | head -n1)"
if [[ -z "$current_prefix" ]]; then
  echo "Could not read <VersionPrefix> from $PROPS_FILE" >&2
  exit 1
fi

if [[ ! "$current_prefix" =~ $SEMVER3_RE ]]; then
  echo "Current VersionPrefix is not X.Y.Z: $current_prefix" >&2
  exit 1
fi

current_revision="$(sed -n 's:.*<VersionRevision>\([0-9]\+\)</VersionRevision>.*:\1:p' "$PROPS_FILE" | head -n1)"
if [[ -z "$current_revision" ]]; then
  current_revision=0
fi

if [[ ! "$current_revision" =~ ^[0-9]+$ ]]; then
  echo "Current VersionRevision is not numeric: $current_revision" >&2
  exit 1
fi

if (( current_revision < 0 || current_revision > 9 )); then
  echo "Current VersionRevision out of range (0-9): $current_revision" >&2
  exit 1
fi

next_prefix=""
next_revision=0

if [[ "$BUMP_INPUT" == "major" || "$BUMP_INPUT" == "minor" || "$BUMP_INPUT" == "patch" || "$BUMP_INPUT" == "change" || "$BUMP_INPUT" == "revision" ]]; then
  IFS='.' read -r major minor patch <<< "$current_prefix"
  case "$BUMP_INPUT" in
    change|revision)
      if (( current_revision < 9 )); then
        next_revision=$((current_revision + 1))
      else
        patch=$((patch + 1))
        next_revision=0
      fi
      ;;
    major)
      major=$((major + 1))
      minor=0
      patch=0
      next_revision=0
      ;;
    minor)
      minor=$((minor + 1))
      patch=0
      next_revision=0
      ;;
    patch)
      patch=$((patch + 1))
      next_revision=0
      ;;
  esac
  next_prefix="${major}.${minor}.${patch}"
else
  explicit_version="$BUMP_INPUT"
  if [[ "$explicit_version" =~ $SEMVER4_RE ]]; then
    IFS='.' read -r major minor patch next_revision <<< "$explicit_version"
    next_prefix="${major}.${minor}.${patch}"
  elif [[ "$explicit_version" =~ $SEMVER3_RE ]]; then
    next_prefix="$explicit_version"
    next_revision=0
  else
    echo "Version must be change|revision|major|minor|patch or explicit X.Y.Z / X.Y.Z.W" >&2
    exit 1
  fi
fi

next_version="${next_prefix}.${next_revision}"

release_date="$(date +%F)"

if [[ "$DO_TAG" == true ]]; then
  if ! command -v git >/dev/null 2>&1; then
    echo "git is required for --tag/--push." >&2
    exit 1
  fi

  if ! git -C "$ROOT_DIR" rev-parse --show-toplevel >/dev/null 2>&1; then
    echo "Could not find a git repository from $ROOT_DIR (required for --tag/--push)." >&2
    exit 1
  fi

  git_root="$(git -C "$ROOT_DIR" rev-parse --show-toplevel)"
  if [[ -n "$(git -C "$git_root" status --porcelain)" ]]; then
    echo "Git working tree is not clean. Commit or stash changes before --tag." >&2
    exit 1
  fi
fi

insert_changelog_section() {
  if grep -q "^## \[${next_version}\]" "$CHANGELOG_FILE"; then
    return 0
  fi

  local tmp
  tmp="$(mktemp)"
  awk -v version="$next_version" -v today="$release_date" '
    BEGIN { inserted = 0 }
    {
      print
      if (!inserted && $0 ~ /^## \[Unreleased\]/) {
        print ""
        print "## [" version "] - " today
        print "### Added"
        print "- _TBD_"
        print ""
        print "### Changed"
        print "- _TBD_"
        print ""
        print "### Fixed"
        print "- _TBD_"
        print ""
        print "### Security"
        print "- _TBD_"
        print ""
        inserted = 1
      }
    }
    END {
      if (!inserted) {
        print ""
        print "## [" version "] - " today
        print "### Added"
        print "- _TBD_"
        print ""
        print "### Changed"
        print "- _TBD_"
        print ""
        print "### Fixed"
        print "- _TBD_"
        print ""
        print "### Security"
        print "- _TBD_"
      }
    }
  ' "$CHANGELOG_FILE" > "$tmp"
  mv "$tmp" "$CHANGELOG_FILE"
}

echo "Current version : ${current_prefix}.${current_revision}"
echo "Next version    : $next_version"

if [[ "$DRY_RUN" == true ]]; then
  echo "[dry-run] Would update $PROPS_FILE"
  echo "[dry-run] Would update $CHANGELOG_FILE"
  if [[ "$DO_TAG" == true ]]; then
    echo "[dry-run] Would create commit + tag v$next_version"
  fi
  if [[ "$DO_PUSH" == true ]]; then
    echo "[dry-run] Would push commit + tag"
  fi
  exit 0
fi

sed -E -i "s|(<VersionPrefix>)[^<]+(</VersionPrefix>)|\1${next_prefix}\2|" "$PROPS_FILE"
if grep -q "<VersionRevision>" "$PROPS_FILE"; then
  sed -E -i "s|(<VersionRevision>)[0-9]+(</VersionRevision>)|\1${next_revision}\2|" "$PROPS_FILE"
else
  tmp_props="$(mktemp)"
  awk -v rev="$next_revision" '
    {
      print
      if (!inserted && $0 ~ /<VersionPrefix>/) {
        print "    <VersionRevision>" rev "</VersionRevision>"
        inserted = 1
      }
    }
  ' "$PROPS_FILE" > "$tmp_props"
  mv "$tmp_props" "$PROPS_FILE"
fi
insert_changelog_section

echo "Updated version files."

if [[ "$DO_TAG" == true ]]; then
  git -C "$git_root" add "$PROPS_FILE" "$CHANGELOG_FILE"
  git -C "$git_root" commit -m "chore(release): v${next_version}"
  git -C "$git_root" tag -a "v${next_version}" -m "Release v${next_version}"
  echo "Created commit + tag v${next_version}"

  if [[ "$DO_PUSH" == true ]]; then
    git -C "$git_root" push
    git -C "$git_root" push origin "v${next_version}"
    echo "Pushed commit and tag."
  fi
fi

echo "Done."
