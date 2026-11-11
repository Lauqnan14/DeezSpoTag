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
    python3 scripts/genre/build_genre_taxonomy.py --apply-parentage \\
        <base-catalog.json> <parentage.xlsx> <output.json> <diff.json>

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
import os
import re
import sys
from collections import Counter, defaultdict

# Era prose parsing lives here, tracked, rather than in the ignored research
# scratch file, because the regional merger imports it and the merger must be
# runnable from the committed tree alone. Behavior is identical to the parser
# that produced the shipped ranges: same regexes, same bounds, same named ranges.

# A 2-digit decade inherits the century of the previous 4-digit decade in the
# same clause. With no neighbour, 20-99 are the 1900s and 00-19 are the 2000s.
ERA_TOKEN = re.compile(
    r"(?P<mod>early|mid|late)?[\s-]*"
    r"(?:"
    r"(?P<decade>\d{4}|\d{2})s"
    r"|(?P<century>\d{1,2})(?:st|nd|rd|th)(?:[\s-]*century|[\s]*c\.?|(?=\s*[–—-]|$))"
    r"|(?:c\.|circa\s+)?(?P<year>\d{3,4})"
    r")",
    re.I,
)
ERA_OPEN = re.compile(r"[–—-]|\bpresent\b|\bonward\b|\btoday\b", re.I)
ERA_NAMED = {
    "interwar": (1918, 1939),
    "wwi": (1914, 1918),
    "world war i": (1914, 1918),
    "wwii": (1939, 1945),
    "world war ii": (1939, 1945),
    "post-wwii": (1945, None),
    "post wwii": (1945, None),
    "post-world war ii": (1945, None),
}


def _era_decade_bounds(year: int, modifier: str) -> tuple[int, int]:
    start = year - (year % 10)
    if modifier == "early":
        return start, start + 3
    if modifier == "mid":
        return start + 4, start + 6
    if modifier == "late":
        return start + 7, start + 9
    return start, start + 9


def _era_century_bounds(number: int, modifier: str) -> tuple[int, int]:
    start = (number - 1) * 100 + 1
    end = number * 100
    if modifier == "early":
        return start, start + 29
    if modifier == "mid":
        return start + 30, end - 30
    if modifier == "late":
        return end - 29, end
    return start, end


def _era_token_bounds(match: re.Match[str], anchor: int | None) -> tuple[int, int, int]:
    modifier = (match.group("mod") or "").lower()
    if match.group("decade"):
        raw = int(match.group("decade"))
        if raw < 100:
            century = (anchor // 100) * 100 if anchor else (1900 if raw >= 20 else 2000)
            raw = century + raw
        start, end = _era_decade_bounds(raw, modifier)
        return start, end, raw
    if match.group("century"):
        start, end = _era_century_bounds(int(match.group("century")), modifier)
        return start, end, start
    year = int(match.group("year"))
    return year, year, year


def _era_clauses(text: str) -> list[str]:
    parts: list[str] = []
    depth = 0
    current: list[str] = []
    for ch in text:
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth = max(0, depth - 1)
        if ch == ";" and depth == 0:
            parts.append("".join(current))
            current = []
            continue
        current.append(ch)
    parts.append("".join(current))
    return [part.strip() for part in parts if part.strip()]


def _era_named_range(text: str) -> tuple[int, int | None] | None:
    folded = re.sub(r"\s+", " ", text.lower()).strip(" .–—-")
    for name, bounds in ERA_NAMED.items():
        if name in folded:
            start, end = bounds
            if ERA_OPEN.search(text[text.lower().find(name) + len(name):]):
                return start, None
            return start, end
    return None


def parse_era(text: str) -> dict | None:
    """Parse one Era value into a label plus year ranges. None means nothing to store.

    The label is kept. The ranges are the only thing a file update may use. A
    named period with no stated year, such as "medieval" or "colonial", gets no
    range: guessing a start year would update the wrong files.
    """
    label = str(text or "").strip()
    if not label:
        return None
    ranges: list[dict] = []
    seen: set[tuple[int, int | None]] = set()

    def add(start: int, end: int | None) -> None:
        if end is not None and end < start:
            start, end = end, start
        key = (start, end)
        if key in seen:
            return
        seen.add(key)
        ranges.append({"start": start, "end": end})

    for clause in _era_clauses(label):
        matches = list(ERA_TOKEN.finditer(clause))
        if not matches:
            named = _era_named_range(clause)
            if named:
                add(*named)
            continue
        anchor = None
        index = 0
        built: list[tuple[int, int | None]] = []
        while index < len(matches):
            start, end, anchor = _era_token_bounds(matches[index], anchor)
            if index + 1 < len(matches):
                between = clause[matches[index].end(): matches[index + 1].start()]
                if re.search(r"[–—-]", between):
                    _start2, end2, anchor = _era_token_bounds(matches[index + 1], anchor)
                    built.append((start, end2))
                    index += 2
                    continue
            built.append((start, end))
            index += 1
        tail = clause[matches[-1].end():]
        if built and ERA_OPEN.search(tail):
            built[-1] = (built[-1][0], None)
        for start, end in built:
            add(start, end)

    return {"label": label, "ranges": ranges}


CATALOG_VERSION = "researched-master-2026-10-06-v1"
PARENTAGE_VERSION = "researched-master-2026-10-13-v6"

# Statuses that must not become parent edges. Matching is on the uppercased
# status text. VERIFIED is required as well: a PASS gate alone is not approval.
UNAPPROVED_STATUS_MARKERS = (
    "HOLD",
    "PROVISIONAL",
    "CONFLICT",
    "AMBIGU",
    "UNRESOLVED",
    "RECHECK",
    "NO SAFE",
    "INSUFFICIENT",
)

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


def fold_lookup(value: str) -> str:
    """Accent-folded lookup key. Used only when the plain key is not unique."""
    import unicodedata

    text = unicodedata.normalize("NFKD", str(value or ""))
    text = "".join(ch for ch in text if not unicodedata.combining(ch))
    return "".join(ch.lower() for ch in text if ch.isalnum())


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


def preserve_overloaded_musical_identities(terms, by_key, owner_of_key, ambiguous, aliases) -> None:
    """Keep a researched Genre or Style when the same word also has another meaning.

    A language, industry, or scene does not make the musical row incorrect.
    Bollywood stays a Style. Swahili stays a Style. OPM stays a Genre whose
    visible label is the acronym. Original Pilipino Music is an alias of that
    genre. Runtime code disambiguates with the input field. A future regen
    must not drop these rows to make a lookup key unique.
    """

    for key in ("bollywood", "opm", "swahili"):
        ambiguous.pop(key, None)

    opm = next((term for term in terms if term["id"] == "opm"), None)
    if opm is None:
        return
    opm["name"] = "OPM"
    opm["kind"] = "Genre"
    name_key = normalize(opm["name"])
    by_key[name_key] = opm
    owner_of_key[name_key] = opm["id"]
    expansion = normalize("Original Pilipino Music")
    if expansion in by_key or expansion in aliases:
        raise CatalogError(
            "Original Pilipino Music collides with an existing canonical term or alias."
        )
    aliases[expansion] = opm["id"]


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


def parentage_approved(status: str, gate: str) -> bool:
    """True only for an implementation-approved parent edge.

    HOLD, RECHECK, provisional, conflict, ambiguous, and no-safe rows stay out.
    A verified identity with an unapproved parent is not given an invented one.
    """
    if str(gate or "").strip().upper() != "PASS":
        return False
    text = str(status or "").upper()
    if "VERIFIED" not in text:
        return False
    return not any(marker in text for marker in UNAPPROVED_STATUS_MARKERS)


def load_builtin_musical_ids(taxonomy_path: str) -> dict[str, str]:
    """Built-in Genre and Style ids mapped to their display names.

    The parentage workbook cites runtime ids such as ``rnb``. The researched
    catalog may know that same identity as ``r-b``. The generator resolves the
    runtime id to the researched canonical id here, and the runtime catalog
    maps it back when the built-in id is the one that has to survive.
    """
    import re

    text = open(taxonomy_path, encoding="utf-8").read()
    pattern = re.compile(
        r'new\("([^"]+)", "((?:\\.|[^"\\])*)", PersonalGenreTaxonKind\.(Genre|Style)'
    )
    return {match.group(1): match.group(2).replace('\\"', '"') for match in pattern.finditer(text)}


def resolve_parent_id(parent_id: str, by_id: dict[str, dict], aliases: dict, builtins: dict[str, str]) -> str:
    """Return the researched canonical id for a parent reference, or raise."""
    if parent_id in by_id:
        return parent_id
    folded = {term_id.lower(): term_id for term_id in by_id}
    if parent_id.lower() in folded:
        return folded[parent_id.lower()]
    if parent_id in aliases or normalize(parent_id) in aliases:
        raise CatalogError(
            f"Parent {parent_id!r} is an alias. Resolve it to the canonical id before writing parentIds."
        )
    builtin_name = builtins.get(parent_id)
    if builtin_name is None:
        raise CatalogError(f"Parent {parent_id!r} is not a canonical taxonomy id.")
    key = normalize(builtin_name)
    matches = [
        term for term in by_id.values()
        if normalize(term["name"]) == key or normalize(term["id"]) == key
    ]
    distinct = {term["id"]: term for term in matches}
    if len(distinct) != 1:
        raise CatalogError(
            f"Parent {parent_id!r} ({builtin_name!r}) does not resolve to exactly one researched identity "
            f"({sorted(distinct)})."
        )
    return next(iter(distinct))


def resolve_child_id(child_id: str, by_id: dict[str, dict], aliases: dict, builtins: dict[str, str]) -> str:
    """Map a workbook id onto one researched term.

    Compatibility ids such as ``afropop`` stay on the researched term ``afro-pop``.
    An alias or an ambiguous spelling is not given a second identity.
    """
    try:
        return resolve_parent_id(child_id, by_id, aliases, builtins)
    except CatalogError as error:
        if "is an alias" in str(error):
            raise CatalogError(
                "Child id is not a researched canonical id. "
                "An alias or colliding spelling is not given a second identity or a parent edge."
            ) from error
    key = normalize(child_id)
    matches = [
        term for term in by_id.values()
        if normalize(term["id"]) == key or normalize(term["name"]) == key
    ]
    distinct = {term["id"]: term for term in matches}
    if len(distinct) == 1:
        return next(iter(distinct))
    # Workbook ids sometimes drop accents (rock-en-espanol, reggae-panameno).
    # Fold only when that spelling is still one researched term. kaseko and
    # kasékò stay distinct because the fold is not unique.
    folded = fold_lookup(child_id)
    folded_matches = [
        term for term in by_id.values()
        if fold_lookup(term["id"]) == folded or fold_lookup(term["name"]) == folded
    ]
    folded_distinct = {term["id"]: term for term in folded_matches}
    if len(folded_distinct) != 1:
        raise CatalogError(
            "Child id is not a researched canonical id. "
            "An alias or colliding spelling is not given a second identity or a parent edge."
        )
    return next(iter(folded_distinct))


def genre_parents_from_research_column(
    raw: str,
    child_id: str,
    by_id: dict[str, dict],
    aliases: dict,
    builtins: dict[str, str],
) -> list[str] | None:
    """Return researched parents only when every token is one canonical Genre.

    Regional labels such as "West African Music" do not resolve, and that
    column is then ignored. A row whose researched parents are all Genres
    keeps those Genres even when the completion column listed only some of them.
    """
    tokens = [part.strip() for part in str(raw or "").replace(",", ";").split(";") if part.strip()]
    if not tokens:
        return None
    resolved: list[str] = []
    for token in tokens:
        try:
            canonical = resolve_child_id(token, by_id, aliases, builtins)
        except CatalogError:
            return None
        if canonical == child_id or by_id[canonical].get("kind") != "Genre":
            return None
        if canonical not in resolved:
            resolved.append(canonical)
    return resolved


def find_cycles(edges: dict[str, list[str]]) -> list[str] | None:
    state: dict[str, int] = {}
    stack: list[str] = []

    def visit(node: str) -> list[str] | None:
        mark = state.get(node, 0)
        if mark == 1:
            start = stack.index(node)
            return stack[start:] + [node]
        if mark == 2:
            return None
        state[node] = 1
        stack.append(node)
        for parent in edges.get(node, []):
            cycle = visit(parent)
            if cycle:
                return cycle
        stack.pop()
        state[node] = 2
        return None

    for node in edges:
        cycle = visit(node)
        if cycle:
            return cycle
    return None


def apply_parentage(base_path: str, workbook_path: str, output_path: str, diff_path: str) -> int:
    """Add verified Style parent edges to an existing researched catalog.

    The base catalog stays the vocabulary. This does not regenerate terms from
    the original spreadsheet and does not drop a term the parentage batch omits.
    Existing parent edges are kept. A later workbook may add a parent. It may
    not remove or replace one: that conflict stops the import.
    Genre rows are recorded and skipped. This tranche is verified Styles only.
    """
    try:
        import openpyxl
    except ImportError:
        print("openpyxl is required: pip install openpyxl", file=sys.stderr)
        return 3

    with open(base_path, encoding="utf-8") as handle:
        catalog = json.load(handle)
    with open(workbook_path, "rb") as handle:
        parentage_hash = hashlib.sha256(handle.read()).hexdigest()

    previous_version = catalog.get("version")
    terms = catalog["terms"]
    by_id = {term["id"]: term for term in terms}
    aliases = {key: entry["target"] for key, entry in catalog.get("aliases", {}).items()}
    taxonomy_path = os.path.join(
        os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
        "DeezSpoTag.Services",
        "Genre",
        "PersonalGenreTaxonomy.cs",
    )
    builtins = load_builtin_musical_ids(taxonomy_path)

    workbook = openpyxl.load_workbook(workbook_path, read_only=True, data_only=True)
    sheet_name = next(
        name for name in (
            "Global Parentage Research",
            "Africa Parentage Research",
            "LAC Parentage Research",
        )
        if name in workbook.sheetnames
    )
    _, rows = read_sheet(workbook, sheet_name)
    workbook.close()

    parents_by_child: dict[str, list[str]] = {}
    excluded: list[dict] = []
    applied_rows = 0

    for row in rows:
        child_id = str(row[0] or "").strip()
        name = str(row[1] or "").strip()
        status = str(row[3] or "").strip()
        gate = str(row[14] or "").strip()
        raw_parents = str(row[6] or "")
        if not child_id:
            continue
        if not parentage_approved(status, gate):
            excluded.append({
                "id": child_id,
                "name": name,
                "status": status,
                "gate": gate,
                "reason": "Parent edge is not implementation-approved.",
            })
            continue
        try:
            child_id = resolve_child_id(child_id, by_id, aliases, builtins)
        except CatalogError as error:
            excluded.append({
                "id": str(row[0] or "").strip(),
                "name": name,
                "status": status,
                "gate": gate,
                "reason": str(error),
            })
            continue
        if by_id[child_id].get("kind") != "Style":
            excluded.append({
                "id": child_id,
                "name": name,
                "status": status,
                "gate": gate,
                "reason": (
                    "Verified Styles only. Genre parentage is deferred and was not written."
                ),
            })
            continue
        parsed: list[str] = []
        seen: set[str] = set()
        try:
            for part in raw_parents.replace(",", ";").split(";"):
                token = part.strip()
                if not token:
                    continue
                canonical = resolve_parent_id(token, by_id, aliases, builtins)
                if canonical == child_id:
                    raise CatalogError(f"{child_id} parents itself.")
                if canonical not in seen:
                    seen.add(canonical)
                    parsed.append(canonical)
        except CatalogError as error:
            excluded.append({
                "id": child_id,
                "name": name,
                "status": status,
                "gate": gate,
                "reason": str(error),
            })
            continue
        # The researched-parent column is the regional chain. It is not an
        # auto-write list. The two Africa corrections that say "do not reduce"
        # name every constitutive Genre there, including one the completion
        # column left blank.
        notes = str(row[10] or "")
        researched_genres = (
            genre_parents_from_research_column(
                str(row[4] or ""), child_id, by_id, aliases, builtins)
            if "do not reduce" in notes.lower()
            else None
        )
        if researched_genres:
            for parent in researched_genres:
                if parent not in parsed:
                    parsed.append(parent)
        if not parsed:
            excluded.append({
                "id": child_id,
                "name": name,
                "status": status,
                "gate": gate,
                "reason": "Approved row has no resolvable parent id. No parent was invented.",
            })
            continue
        parents_by_child[child_id] = parsed
        applied_rows += 1

    previous_parents = {
        term["id"]: list(term.get("parentIds") or [])
        for term in terms
    }
    # A restated edge is not a change. An omitted existing edge is a conflict,
    # not a deletion. New parents are appended after the parents already stored.
    updates: dict[str, list[str]] = {}
    conflicts: list[str] = []
    for child_id, proposed in parents_by_child.items():
        existing = previous_parents.get(child_id) or []
        if not existing:
            updates[child_id] = proposed
            continue
        removed = [parent for parent in existing if parent not in proposed]
        if removed:
            conflicts.append(
                f"{child_id}: workbook would remove {removed}; stored {existing}; workbook {proposed}"
            )
            continue
        if set(proposed) == set(existing):
            continue
        merged = list(existing)
        for parent in proposed:
            if parent not in merged:
                merged.append(parent)
        updates[child_id] = merged

    if conflicts:
        print(
            "catalog validation failed: parentage conflicts with an earlier verified edge:\n"
            + "\n".join(conflicts[:30]),
            file=sys.stderr,
        )
        return 4

    merged_edges = {child: list(parents) for child, parents in previous_parents.items() if parents}
    merged_edges.update(updates)
    cycle = find_cycles(merged_edges)
    if cycle:
        print("catalog validation failed: parent cycle " + " -> ".join(cycle), file=sys.stderr)
        return 4

    identity_changes = []
    for term in terms:
        before = (
            term.get("name"),
            term.get("kind"),
            list(term.get("regions") or []),
            list(term.get("aliases") or []),
            term.get("id"),
        )
        parents = updates.get(term["id"])
        if parents:
            term["parentIds"] = parents
        after = (
            term.get("name"),
            term.get("kind"),
            list(term.get("regions") or []),
            list(term.get("aliases") or []),
            term.get("id"),
        )
        if before != after:
            identity_changes.append(term["id"])

    if identity_changes:
        print(
            "catalog validation failed: parentage import changed identity fields: "
            + ", ".join(identity_changes[:20]),
            file=sys.stderr,
        )
        return 4

    def edge_set(mapping: dict[str, list[str]]) -> set[tuple[str, str]]:
        return {(child, parent) for child, parents in mapping.items() for parent in parents}

    previous_edges = edge_set(previous_parents)
    current_edges = edge_set({term["id"]: list(term.get("parentIds") or []) for term in terms})
    added_edges = sorted(current_edges - previous_edges)
    removed_edges = sorted(previous_edges - current_edges)
    if removed_edges:
        print(
            "catalog validation failed: import removed parent edges: "
            + ", ".join(f"{child}->{parent}" for child, parent in removed_edges[:20]),
            file=sys.stderr,
        )
        return 4
    previous_multi = {child for child, parents in previous_parents.items() if len(parents) > 1}
    multi_parent_total = sorted(child for child, parents in merged_edges.items() if len(parents) > 1)
    multi_parent_added = [child for child in multi_parent_total if child not in previous_multi]

    kind_counts = Counter(term["kind"] for term in terms)
    region_slugs = sorted({slug for term in terms for slug in term.get("regions") or []})
    alias_count = sum(len(term.get("aliases") or []) for term in terms)
    parent_edges = sum(len(term.get("parentIds") or []) for term in terms)

    source = catalog.setdefault("source", {})
    inputs = list(source.get("inputs") or [])
    if not inputs and source.get("sha256"):
        inputs.append({
            "role": "vocabulary",
            "file": source.get("file"),
            "sha256": source.get("sha256"),
        })
    parentage_file = os.path.basename(workbook_path)
    if not any(
        item.get("role") == "global-parentage"
        and item.get("file") == parentage_file
        and item.get("sha256") == parentage_hash
        for item in inputs
    ):
        inputs.append({
            "role": "global-parentage",
            "file": parentage_file,
            "sha256": parentage_hash,
        })
    source["inputs"] = inputs
    source["note"] = (
        "Vocabulary rows come from the pinned 2026-10-06 researched master. "
        "Parent edges are additive and, in the current tranche, verified Styles only. "
        "Each sha256 covers only the artifact it names. "
        "A later parentage batch must not remove or replace an earlier verified edge."
    )
    catalog["version"] = PARENTAGE_VERSION
    catalog["counts"] = {
        "genres": kind_counts["Genre"],
        "styles": kind_counts["Style"],
        "terms": len(terms),
        "aliases": alias_count,
        "regions": len(region_slugs),
        "exclusions": len(catalog.get("exclusions") or {}),
        "ambiguous": len(catalog.get("ambiguous") or {}),
        "parentEdges": parent_edges,
        "multiParentTerms": len(multi_parent_total),
    }
    catalog["importDiff"] = {
        "previousVersion": previous_version,
        "termsAdded": 0,
        "termsRemoved": 0,
        "termsChanged": len(updates),
        "genresAdded": 0,
        "stylesAdded": 0,
        "parentEdgesAdded": len(added_edges),
        "parentEdgesRemoved": len(removed_edges),
        "multiParentTermsAdded": len(multi_parent_added),
        "aliasesAdded": 0,
        "aliasesRemoved": 0,
        "kindChanges": 0,
        "idChanges": 0,
        "regionChanges": 0,
        "excludedRows": len(excluded),
    }

    # Keep the historical key order: version, source, counts, then the rest,
    # with importDiff placed after counts so provenance is visible up front.
    ordered = {}
    for key in ("version", "source", "counts", "importDiff"):
        ordered[key] = catalog[key]
    for key, value in catalog.items():
        if key not in ordered:
            ordered[key] = value

    diff = {
        "previousVersion": previous_version,
        "version": PARENTAGE_VERSION,
        "previousTerms": len(terms),
        "newTerms": [],
        "removedTerms": [],
        "changedTerms": sorted(updates),
        "genresAdded": 0,
        "stylesAdded": 0,
        "parentEdgesAdded": [{"child": child, "parent": parent} for child, parent in added_edges],
        "parentEdgesRemoved": [{"child": child, "parent": parent} for child, parent in removed_edges],
        "aliasesAdded": [],
        "aliasesRemoved": [],
        "kindChanges": [],
        "idChanges": [],
        "regionChanges": [],
        "newCollisions": [],
        "resolvedCollisions": [],
        "newAmbiguities": [],
        "newExclusions": [],
        "multiParentTermsAdded": multi_parent_added,
        "excluded": sorted(excluded, key=lambda item: (item["id"], item["status"])),
        "appliedRows": applied_rows,
    }

    write_json(output_path, ordered)
    write_json(diff_path, diff)
    print(f"catalog      : {output_path}")
    print(f"diff         : {diff_path}")
    print(f"version      : {PARENTAGE_VERSION}")
    print(f"parentage sha: {parentage_hash}")
    print(f"applied rows : {applied_rows}")
    print(f"parent edges : {parent_edges}")
    print(f"multi-parent : {len(multi_parent_total)} added {len(multi_parent_added)}")
    print(f"excluded     : {len(excluded)}")
    print(f"edges added  : {len(added_edges)} removed {len(removed_edges)}")
    return 0


def write_json(path: str, payload: dict) -> None:
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=1)
        handle.write("\n")


def main() -> int:
    if len(sys.argv) >= 2 and sys.argv[1] == "--apply-parentage":
        if len(sys.argv) != 6:
            print(__doc__)
            return 2
        return apply_parentage(sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5])

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
    preserve_overloaded_musical_identities(terms, by_key, owner_of_key, ambiguous, aliases)
    # An exclusion that shares a lookup key with a canonical term must not hide
    # that term. The canonical identity wins and the collision is recorded.
    # Known case: "Greek Pop" was excluded because Last.fm warns that the tag is
    # often misapplied instead of modern laika. Its key is the Style Greek-Pop,
    # so the exclusion is recorded and not published.
    resolved_exclusion_collisions = {}
    for key in list(exclusions):
        if key in owner_of_key:
            resolved_exclusion_collisions[key] = owner_of_key[key]
            del exclusions[key]
    for key in ambiguous:
        if key in owner_of_key or key in aliases:
            print(
                f"catalog validation failed: ambiguity {key!r} collides with a canonical term",
                file=sys.stderr,
            )
            return 4

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
    alias_labels.setdefault(normalize("Original Pilipino Music"), "Original Pilipino Music")

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
        "resolvedExclusionCollisions": dict(sorted(resolved_exclusion_collisions.items())),
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
