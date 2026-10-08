#!/usr/bin/env python3
"""
Generate the Genre Intelligence runtime taxonomy catalog from the researched master.

The research master is a spreadsheet. The application must not depend on a
spreadsheet, and hand-writing thousands of C# records would be unmaintainable and
unauditable, so this script derives a versioned JSON catalog from it.

The script is the only place that reads the spreadsheet. It is reproducible: the
source SHA-256 is pinned into the output, so a regenerated catalog either matches
the committed one byte for byte or the difference is visible.

Usage:
    python3 scripts/genre/build_genre_taxonomy.py <master.xlsx> <output.json>

Rules that the output must satisfy, and why:

* Canonical names outrank aliases. An alias whose key already resolves to a
  canonical term is dropped, because the canonical wins and adding the alias would
  let a spelling variant override a researched term.

* An alias may never point at two different canonical terms. That is a corruption
  in the source, not something to resolve arbitrarily, so the script fails.

* An alias may never shadow a canonical with a different target. The researched
  canonical stands and the two stay distinct, because merging them would assert a
  relationship the research did not establish for those terms.

* Ampersand variants are only aliased where the source research established the
  equivalence. The shared normalization algorithm is not modified, so a term such
  as "Drum & Bass" is not automatically equivalent to "drum and bass"; it is only
  aliased if a source row says so. The residual unmatched set is reported.

* AllMusic's "Parent" column is deliberately NOT imported. Those values are source
  facets such as "international" and "pop/rock", not researched canonical parent
  relationships, and using them for derivation would invent relationships.
"""

from __future__ import annotations

import hashlib
import json
import sys
from collections import Counter, defaultdict

CATALOG_VERSION = "researched-master-2026-10-06-v1"

# The one canonical-key collision in the source. Both spellings are researched
# Style/Global terms, so the collision is resolved by dropping the run-together
# spelling as an alias of the spaced spelling, which is the form the rest of the
# research uses. This is recorded in the output rather than resolved silently.
RESOLVED_CANONICAL_COLLISIONS = {
    "hypertechno": "hyper techno",
}

# Regions in the source are multi-valued and joined with "; ". They map onto the
# application region slugs one for one, and "Global" means the term is unscoped.
REGION_SLUGS = {
    "Global": None,
    "North America": "north-america",
    "Latin America & Caribbean": "latin-america-caribbean",
    "Africa": "africa",
    "Europe": "europe",
    "MENA": "mena",
    "Central Asia": "central-asia",
    "South Asia": "south-asia",
    "East Asia": "east-asia",
    "Southeast Asia": "southeast-asia",
    "Oceania/Pacific": "oceania-pacific",
}

VALID_KINDS = {"Genre", "Style"}


def normalize(value: str) -> str:
    """The shared lookup key: strip everything that is not a letter or digit, lowercase.

    Mirrors DeezSpoTag.Core.Utils.GenreTagAliasNormalizer.ToLookupKey and
    DeezSpoTag.Services.Genre.PersonalGenreTaxonomy.Normalize.
    """
    return "".join(ch.lower() for ch in str(value).strip() if ch.isalnum())


def slugify(value: str) -> str:
    """A stable id, matching the style of the existing built-in ids such as bongo-flava."""
    out = []
    previous_dash = True
    for ch in str(value).strip().lower():
        if ch.isalnum():
            out.append(ch)
            previous_dash = False
        elif ch in " '&/,-":
            if not previous_dash:
                out.append("-")
                previous_dash = True
    return "".join(out).strip("-")


class CatalogError(RuntimeError):
    """Raised when the source catalog is corrupt. Never resolved silently."""


def read_sheet(workbook, name: str):
    sheet = workbook[name]
    rows = sheet.iter_rows(values_only=True)
    header = next(rows)
    return header, [row for row in rows if not all(cell is None for cell in row)]


def parse_regions(raw: str) -> list[str]:
    slugs = []
    for part in str(raw or "").split(";"):
        part = part.strip()
        if not part:
            continue
        if part not in REGION_SLUGS:
            raise CatalogError(f"Unknown region {part!r}")
        slug = REGION_SLUGS[part]
        if slug and slug not in slugs:
            slugs.append(slug)
    return slugs


def collect_canonical_terms(workbook) -> tuple[list[dict], dict[str, str]]:
    _, rows = read_sheet(workbook, "All Terms")
    terms: list[dict] = []
    seen_keys: dict[str, str] = {}
    seen_ids: dict[str, str] = {}
    collision_aliases: dict[str, str] = {}

    for raw_term, raw_kind, raw_region in rows:
        name = str(raw_term).strip()
        kind = str(raw_kind).strip()
        if not name:
            raise CatalogError("A canonical term has no name.")
        if kind not in VALID_KINDS:
            raise CatalogError(f"Term {name!r} has an invalid kind {kind!r}.")

        key = normalize(name)
        if not key:
            raise CatalogError(f"Term {name!r} has no usable characters.")
        if key in seen_keys and seen_keys[key] != name:
            winner = RESOLVED_CANONICAL_COLLISIONS.get(key)
            if winner is None:
                raise CatalogError(
                    f"Canonical key collision on {key!r}: {seen_keys[key]!r} and {name!r}."
                )
            # The canonical outranks the alias, so the run-together spelling becomes
            # an alias of the spaced one instead of being discarded. Dropping it
            # would lose a term the research published.
            collision_aliases[key] = seen_keys[key]
            continue
        seen_keys.setdefault(key, name)

        term_id = slugify(name)
        if not term_id:
            raise CatalogError(f"Term {name!r} does not produce a usable id.")
        if term_id in seen_ids:
            # Two different names slugging to the same id would make the id useless
            # as a stable reference for saved user locks and mappings.
            raise CatalogError(
                f"Id collision on {term_id!r}: {seen_ids[term_id]!r} and {name!r}."
            )
        seen_ids[term_id] = name

        terms.append(
            {
                "id": term_id,
                "name": name,
                "kind": kind,
                "regions": parse_regions(raw_region),
            }
        )

    return terms, collision_aliases


def collect_aliases(workbook, by_key: dict[str, dict]) -> tuple[dict[str, list[str]], dict]:
    """Build alias -> canonical id, and report what was dropped and why."""
    candidates: list[tuple[str, str, str, str]] = []  # label, canonical name, source, relationship

    _, rows = read_sheet(workbook, "RYM Matched")
    candidates += [
        (str(r[0]).strip(), str(r[1]).strip(), "RYM", str(r[4] or ""))
        for r in rows
        if "alias" in str(r[4] or "").lower()
    ]

    _, rows = read_sheet(workbook, "AllMusic Matched")
    candidates += [
        (str(r[0]).strip(), str(r[1]).strip(), "AllMusic", str(r[6] or ""))
        for r in rows
        if "alias" in str(r[6] or "").lower()
    ]

    _, rows = read_sheet(workbook, "Discogs Matched")
    candidates += [
        (str(r[0]).strip(), str(r[2]).strip(), "Discogs", str(r[5] or ""))
        for r in rows
        if "alias" in str(r[5] or "").lower()
    ]

    _, rows = read_sheet(workbook, "Last.fm Matched")
    candidates += [
        (str(r[0]).strip(), str(r[1]).strip(), "Last.fm", str(r[2] or ""))
        for r in rows
    ]

    # label key -> canonical id. Two labels reaching the same canonical is fine;
    # one label reaching two canonicals is corruption.
    aliases: dict[str, list[str]] = defaultdict(list)
    origin: dict[str, tuple[str, str, str]] = {}
    dropped_redundant: list[str] = []
    dropped_canonical_wins: list[tuple[str, str]] = []

    for label, canonical_name, source, relationship in candidates:
        label_key = normalize(label)
        if not label_key:
            raise CatalogError(f"Alias {label!r} from {source} has no usable characters.")

        target = by_key.get(normalize(canonical_name))
        if target is None:
            # A researched reconciliation pointing at a term that is not in the
            # master would silently do nothing, so it is reported rather than kept.
            dropped_canonical_wins.append((label, f"canonical {canonical_name!r} is not in the master"))
            continue

        existing = by_key.get(label_key)
        if existing is not None:
            if existing["id"] == target["id"]:
                # The spelling variant already resolves to the same term through the
                # shared key, so the alias would add nothing.
                dropped_redundant.append(label)
            else:
                # A researched canonical exists for this spelling and it is a
                # different term. The canonical stands; the two stay distinct.
                dropped_canonical_wins.append(
                    (label, f"{existing['name']!r} is its own researched canonical term")
                )
            continue

        target_id = target["id"]
        if aliases[label_key] and target_id not in aliases[label_key]:
            raise CatalogError(
                f"Alias {label!r} resolves to multiple canonical terms: "
                f"{aliases[label_key]} and {target_id}."
            )
        if target_id not in aliases[label_key]:
            aliases[label_key].append(target_id)
            origin.setdefault(label_key, (label, source, relationship))

    resolved: dict[str, str] = {}
    for key, value in aliases.items():
        if len(value) > 1:
            raise CatalogError(f"Alias key {key!r} resolves to multiple canonical terms: {value}.")
        resolved[key] = value[0]

    # Aliases must not collide with a canonical name or id.
    for key in list(aliases):
        if key in by_key:
            raise CatalogError(f"Alias key {key!r} collides with a canonical term.")

    report = {
        "sourceRows": len(candidates),
        "kept": len(aliases),
        "droppedRedundantBecauseCanonicalAlreadyMatches": sorted(set(dropped_redundant)),
        "droppedBecauseCanonicalTakesPrecedence": sorted(set(dropped_canonical_wins)),
    }
    return resolved, report


def collect_exclusions(workbook) -> dict[str, dict]:
    """Researched knowledge that a value is not a Genre or a Style.

    This is deliberately not the same as unknown. An exclusion is a researched
    decision, so the value is excluded from Genre and Style and the decision is
    recorded, whereas an unknown value is preserved.
    """
    exclusions: dict[str, dict] = {}

    _, rows = read_sheet(workbook, "RYM Excluded")
    for term, reason, _url in rows:
        key = normalize(term)
        if key:
            exclusions[key] = {
                "value": str(term).strip(),
                "reason": str(reason).strip(),
                "source": "RYM",
            }

    _, rows = read_sheet(workbook, "Last.fm Reviewed")
    for candidate, disposition, _canonical, _kind, _region, url, note in rows:
        if str(disposition).strip() != "Excluded":
            continue
        key = normalize(candidate)
        if key:
            exclusions[key] = {
                "value": str(candidate).strip(),
                "reason": str(note).strip() or "Excluded by the researched Last.fm audit.",
                "source": "Last.fm",
                "url": str(url or ""),
            }

    return exclusions


def collect_ambiguous(workbook) -> dict[str, dict]:
    """Researched terms that are not known to be wrong, but are not safe to classify."""
    ambiguous: dict[str, dict] = {}
    _, rows = read_sheet(workbook, "Last.fm Reviewed")
    for candidate, disposition, _canonical, _kind, _region, url, note in rows:
        if str(disposition).strip() != "Hold":
            continue
        key = normalize(candidate)
        if key:
            ambiguous[key] = {
                "value": str(candidate).strip(),
                "reason": str(note).strip() or "Held: insufficient evidence to classify.",
                "source": "Last.fm",
                "url": str(url or ""),
            }
    return ambiguous


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 2

    master_path, output_path = sys.argv[1], sys.argv[2]

    with open(master_path, "rb") as handle:
        source_hash = hashlib.sha256(handle.read()).hexdigest()

    try:
        import openpyxl
    except ImportError:
        print("openpyxl is required: pip install openpyxl", file=sys.stderr)
        return 3

    workbook = openpyxl.load_workbook(master_path, read_only=True, data_only=True)

    try:
        terms, collision_aliases = collect_canonical_terms(workbook)
    except CatalogError as error:
        print(f"catalog validation failed: {error}", file=sys.stderr)
        return 4

    by_key = {}
    owner_of_key = {}
    for term in terms:
        key = normalize(term["name"])
        if key in owner_of_key:
            raise CatalogError(f"Duplicate canonical key {key!r}")
        by_key[key] = term
        owner_of_key[key] = term["id"]

        # The id is also a lookup key, so a term stays reachable by the id a saved
        # user mapping or lock refers to. A term whose id happens to equal its own
        # name is fine; an id that would resolve to a different term is not.
        id_key = normalize(term["id"])
        if id_key in by_key and owner_of_key[id_key] != term["id"]:
            raise CatalogError(
                f"Term id {term['id']!r} resolves to a different canonical term "
                f"({by_key[id_key]['name']!r})."
            )
        if id_key not in by_key:
            by_key[id_key] = term
            owner_of_key[id_key] = term["id"]

    aliases, alias_report = collect_aliases(workbook, by_key)
    # A researched spelling collision is resolved as an alias, so every published
    # term stays reachable and none is discarded.
    for key, canonical_name in collision_aliases.items():
        target = by_key.get(normalize(canonical_name))
        if target is None:
            print(
                f"catalog validation failed: collision winner {canonical_name!r} is unknown",
                file=sys.stderr,
            )
            return 4
        if key not in by_key and key not in aliases:
            aliases[key] = target["id"]
    exclusions = collect_exclusions(workbook)
    ambiguous = collect_ambiguous(workbook)

    # An alias may not point at a term that does not exist.
    known_ids = {term["id"] for term in terms}
    for key, target in aliases.items():
        if target not in known_ids:
            print(f"catalog validation failed: alias {key!r} targets unknown id {target!r}", file=sys.stderr)
            return 4

    # Build the human-readable alias label for each alias key, preferring the
    # Last.fm spelling because that research recorded each reconciliation as an
    # explicit label, and falling back to the provider sheets otherwise.
    alias_labels = {}
    for sheet in ("RYM Matched", "AllMusic Matched", "Discogs Matched"):
        _, source_rows = read_sheet(workbook, sheet)
        for row in source_rows:
            alias_labels.setdefault(normalize(str(row[0]).strip()), str(row[0]).strip())
    _, source_rows = read_sheet(workbook, "Last.fm Matched")
    for row in source_rows:
        alias_labels[normalize(str(row[0]).strip())] = str(row[0]).strip()
    for key, canonical_name in collision_aliases.items():
        alias_labels.setdefault(key, RESOLVED_CANONICAL_COLLISIONS.get(key, key))

    for term in terms:
        term["aliases"] = sorted(
            {
                alias_labels[key]
                for key, target in aliases.items()
                if target == term["id"] and key in alias_labels
            }
        )

    for term in terms:
        for alias in term["aliases"]:
            if normalize(alias) == normalize(term["name"]) or normalize(alias) == normalize(term["id"]):
                raise CatalogError(f"Term {term['name']!r} lists its own spelling as an alias.")

    kind_counts = Counter(term["kind"] for term in terms)
    region_slugs = sorted({slug for term in terms for slug in term["regions"]})
    alias_count = sum(len(term["aliases"]) for term in terms)

    catalog = {
        "version": CATALOG_VERSION,
        "source": {
            "file": "DeezSpoTag_MusicBrainz_AllMusic_Discogs_RYM_LastFM_3746_Genre_Style_Regions.xlsx",
            "sha256": source_hash,
            "note": "The spreadsheet is research input only. It is not a runtime dependency.",
        },
        "counts": {
            "genres": kind_counts["Genre"],
            "styles": kind_counts["Style"],
            "terms": len(terms),
            "aliases": alias_count,
            "regions": len(region_slugs),
            "exclusions": len(exclusions),
            "ambiguous": len(ambiguous),
        },
        "regions": region_slugs,
        "aliases": {
            key: {"value": alias_labels.get(key, key), "target": target}
            for key, target in sorted(aliases.items())
        },
        "exclusions": dict(sorted(exclusions.items())),
        "ambiguous": dict(sorted(ambiguous.items())),
        "resolvedCanonicalCollisions": RESOLVED_CANONICAL_COLLISIONS,
        "terms": terms,
    }

    with open(output_path, "w", encoding="utf-8") as handle:
        json.dump(catalog, handle, ensure_ascii=False, indent=1, sort_keys=False)
        handle.write("\n")

    print(f"catalog      : {output_path}")
    print(f"version      : {CATALOG_VERSION}")
    print(f"source sha256: {source_hash}")
    print(f"terms        : {len(terms)}  (Genre {kind_counts['Genre']}, Style {kind_counts['Style']})")
    print(f"aliases      : {alias_count} kept of {alias_report['sourceRows']} source rows")
    print(f"  redundant (canonical already matches): {len(alias_report['droppedRedundantBecauseCanonicalAlreadyMatches'])}")
    print(f"  canonical takes precedence           : {len(alias_report['droppedBecauseCanonicalTakesPrecedence'])}")
    print(f"regions      : {len(region_slugs)} -> {', '.join(region_slugs)}")
    print(f"exclusions   : {len(exclusions)}")
    print(f"ambiguous    : {len(ambiguous)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
