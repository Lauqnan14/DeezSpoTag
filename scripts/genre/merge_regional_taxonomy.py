#!/usr/bin/env python3
"""Merge a regional research workbook into the shipped taxonomy without deleting it.

The workbook is the source of truth for the terms it verifies. A term the workbook
does not mention is left unchanged, including Asia, Oceania and any other region
this pass did not research. An unverified row does not add a term and does not
remove one.

The run also derives the researched geography: the closed country vocabulary the
Member Countries columns name, and the place (city/town/area) vocabulary the
Location prose resolves to. Both ship as one geo resource, and every Location
clause is written to a review artifact so the city-vs-country mapping stays
auditable instead of heuristic.
"""

from __future__ import annotations

import hashlib
import json
import sys
import unicodedata
from collections import defaultdict
from pathlib import Path

# parse_era lives in the tracked builder so the merger runs from the committed
# tree alone; the research scratch file it used to live in is ignored.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from build_genre_taxonomy import parse_era


def load_workbook(path, read_only=False, data_only=False):
    """openpyxl is imported only when spreadsheet processing actually runs.

    The builder imports it the same lazy way inside its reading functions. A
    module-level import would make even importing this module fail in an
    environment without openpyxl, such as CI, where only the era parser is
    needed.
    """
    try:
        from openpyxl import load_workbook as openpyxl_load_workbook
    except ImportError:
        raise SystemExit("openpyxl is required: pip install openpyxl") from None
    return openpyxl_load_workbook(path, read_only=read_only, data_only=data_only)

VERSION = "researched-master-2026-11-10-v8"
SHEETS = (
    "Global Core Taxonomy",
    "Africa Core Taxonomy",
    "North America Core Taxonomy",
    "Latin America Core Taxonomy",
    "Europe Core Taxonomy",
)
MUSICAL = {"Genre", "Style"}
HELD_KINDS = {"Context", "Language", "Scene"}
COVERED_REGIONS = {"africa", "north-america", "latin-america-caribbean", "europe"}
TOKEN_SLUGS = {
    "african": ("africa",),
    "west-african": ("africa",),
    "east-african": ("africa",),
    "southern-african": ("africa",),
    "central-african": ("africa",),
    "horn-of-africa": ("africa",),
    "sahel": ("africa",),
    "indian-ocean-islands": ("africa",),
    "maghreb": ("africa", "mena"),
    "north-african": ("africa", "mena"),
    "latin-america-and-caribbean": ("latin-america-caribbean",),
    "south-american": ("latin-america-caribbean",),
    "caribbean": ("latin-america-caribbean", "north-america"),
    "central-american": ("latin-america-caribbean", "north-america"),
    "north-american": ("north-america",),
    "northern-american": ("north-america",),
    "european": ("europe",),
    "northern-european": ("europe",),
    "southern-european": ("europe",),
    "western-european": ("europe",),
    "eastern-european": ("europe",),
    "western-asian": ("mena",),
}

# ---------------------------------------------------------------------------
# Geography derivation
# ---------------------------------------------------------------------------

# Display names the slug-to-title rule cannot produce on its own.
COUNTRY_DISPLAY_OVERRIDES = {
    "aland-islands": "Åland Islands",
    "curacao": "Curaçao",
    "cote-d-ivoire": "Côte d'Ivoire",
    "drc": "DR Congo",
    "reunion": "Réunion",
    "sao-tome-and-principe": "São Tomé and Príncipe",
    "us-virgin-islands": "U.S. Virgin Islands",
}

# Extra spellings the Location prose is allowed to name a country by. Keys are
# word tuples in fold form; they are matched longest-first alongside the display
# names, so nothing here can shadow a different country.
COUNTRY_ALIASES = {
    "united-states": [("usa",), ("u.s.a",)],
    "united-kingdom": [("u.k.",), ("uk",)],
    "cote-d-ivoire": [("ivory", "coast")],
    "cape-verde": [("cabo", "verde")],
    "drc": [("democratic", "republic", "of", "the", "congo"), ("drc",)],
}
# Country names the Location prose uses that the Member Countries column never
# spells as a slug. They are countries, so they must not fall through into the
# place vocabulary. They do not replace a column value.
PROSE_COUNTRIES = {
    "england": "England",
    "scotland": "Scotland",
    "wales": "Wales",
    "azerbaijan": "Azerbaijan",
    "kazakhstan": "Kazakhstan",
}

# Words that never carry a place on their own. A residual phrase made only of
# these is recorded as a leftover instead of becoming a place.
DIRECTIONAL_FOLDS = {
    "south", "southern", "southwest", "southwestern", "southeast", "southeastern",
    "north", "northern", "northwest", "northwestern", "northeast", "northeastern",
    "east", "eastern", "west", "western", "central", "coastal", "sub", "interior",
    "areas", "area", "region", "regions", "parts", "part",
}
STOP_FOLDS = {"of", "and", "the", "in", "near", "or", "a", "an", "de", "da", "di", "del", "la", "le", "el", "y"}
# A parenthesis that contains one of these is a citation, not a list of cities.
CITATION_FOLDS = {
    "wikipedia", "wiki", "ptwikipedia", "guardian", "udiscover", "fania", "scielo",
    "allmusic", "archive", "http", "https", "www", "enciclopedia", "encyclopedia",
    "itau", "monitor", "globe", "muglobe", "repertoire", "standard", "independent",
}
# A segment that starts with one of these is a qualifier ("also the South"),
# not a city. A segment that contains a junk token is a scene description
# ("club circuit") or a citation that escaped the parentheses.
QUALIFIER_START = {
    "also", "especially", "including", "incl", "variants", "formerly", "via",
    "see", "cf", "both", "popular", "origin", "origins", "now", "across", "throughout",
}
JUNK_TOKEN = {
    "circuit", "circuits", "wikipedia", "wiki", "monitor", "globe", "repertoire",
    "encyclopedia", "popularisation", "popularization", "usage", "source", "sources",
    "home", "studio", "studios", "venue", "venues", "tradition", "liturgical",
    "diaspora", "continent", "sourced", "describing",
}
CONTINENT_SLUGS = {"africa", "europe", "asia", "americas", "oceania"}
# A one-token alias that is also an English word. Only the dotted or all-caps
# form may name the country, so a stray "us" never becomes the United States.
DOTTED_COUNTRY = {
    "us": "united-states",
    "usa": "united-states",
    "uk": "united-kingdom",
}


# Letters NFKD does not decompose. A slug has to stay ASCII so the loader and
# any later rule can share one spelling.
TRANSLIT = str.maketrans({
    "ŋ": "ng", "Ŋ": "ng",
    "ø": "o", "Ø": "o",
    "ł": "l", "Ł": "l",
    "ð": "d", "Ð": "d",
    "þ": "th", "Þ": "th",
    "æ": "ae", "Æ": "ae",
    "œ": "oe", "Œ": "oe",
    "ß": "ss",
    "ı": "i",
    "ʻ": "", "ʼ": "", "′": "", "’": "",
})


def fold_word(word: str) -> str:
    """ASCII-fold a word to its lowercase alphanumeric lookup form.

    Accents must not change which country a clause names: Côte and Cote are the
    same lookup, and a hyphen is a word boundary rather than part of the word.
    """
    letters = unicodedata.normalize("NFKD", str(word or "").translate(TRANSLIT))
    stripped = "".join(ch for ch in letters if not unicodedata.combining(ch))
    return "".join(ch for ch in stripped.lower() if ch.isalnum())


def title_country(slug: str) -> str:
    words = []
    for index, part in enumerate(slug.split("-")):
        if index and part in {"and", "of", "the"}:
            words.append(part)
        else:
            words.append(part[:1].upper() + part[1:] if part else part)
    return " ".join(words)


def tokenize(text: str) -> list[dict]:
    """Words of a Location value, with clause, parenthesis and hyphen structure.

    ';' outside parentheses starts a new clause, so "Lagos, Nigeria; Accra, Ghana"
    cannot hand Accra's country to Lagos. A hyphen joins a compound ("Saint-Pierre",
    "Guinea-Bissau") without starting a new place. Parentheses keep an id so a
    citation can be dropped while a city list inside them is kept.
    """
    tokens: list[dict] = []
    depth = 0
    paren_serial = 0
    clause = 0
    current = ""
    brk = False
    hyphen = False
    semi_in_paren = False

    def flush() -> None:
        nonlocal current, brk, hyphen
        raw = current.strip(" '")
        if raw.endswith("'s"):
            raw = raw[:-2]
        if raw:
            folded = fold_word(raw)
            if folded:
                tokens.append({
                    "i": len(tokens),
                    "raw": raw,
                    "fold": folded,
                    "paren": paren_serial if depth > 0 else 0,
                    "clause": clause,
                    "brk": brk,
                    "hy": hyphen,
                    "semi": semi_in_paren and depth > 0,
                })
            # The boundary belongs to the next word. An empty flush (the space
            # after a comma, or a trailing hyphen) must not consume it.
            brk = False
            hyphen = False
        current = ""

    for ch in str(text or ""):
        if ch == "(":
            flush()
            depth += 1
            brk = True
            if depth == 1:
                paren_serial += 1
                semi_in_paren = False
        elif ch == ")":
            flush()
            depth = max(0, depth - 1)
            brk = True
        elif ch == ";" and depth == 0:
            flush()
            clause += 1
            brk = True
        elif ch == ";" and depth > 0:
            flush()
            semi_in_paren = True
            brk = True
        elif ch in ",:/&+":
            flush()
            brk = True
        elif ch == "-":
            flush()
            hyphen = True
        elif ch.isspace() or ch in "–—":
            flush()
        else:
            current += ch
    flush()
    return tokens


def build_country_phrases(countries: dict[str, str]) -> dict[tuple[str, ...], str]:
    phrases: dict[tuple[str, ...], str] = {}

    def add(words: list[str], slug: str) -> None:
        key = tuple(fold_word(word) for word in words if fold_word(word))
        if not key:
            return
        if key in phrases and phrases[key] != slug:
            raise SystemExit(f"Country phrases collide: {key} names both {phrases[key]} and {slug}.")
        phrases[key] = slug

    for slug, name in countries.items():
        add(name.split(), slug)
        add(slug.split("-"), slug)
        for alias in COUNTRY_ALIASES.get(slug, []):
            add(list(alias), slug)
    return phrases


def match_countries(tokens: list[dict], phrases: dict[tuple[str, ...], str]) -> list[tuple[str, int, int]]:
    """Longest-first country matches as (slug, start, end) token ranges."""
    longest = max(len(key) for key in phrases)
    hits: list[tuple[str, int, int]] = []
    index = 0
    while index < len(tokens):
        found = None
        for length in range(min(longest, len(tokens) - index), 0, -1):
            key = tuple(tokens[index + offset]["fold"] for offset in range(length))
            slug = phrases.get(key)
            if slug:
                found = (slug, length)
                break
        if found:
            hits.append((found[0], index, index + found[1]))
            index += found[1]
        else:
            index += 1
    return hits


def dotted_country(token: dict) -> str | None:
    """A short country alias that is also an English word, so case and dots matter."""
    slug = DOTTED_COUNTRY.get(token["fold"])
    if not slug:
        return None
    raw = token["raw"]
    if "." in raw or raw.isupper():
        return slug
    return None


def place_parens(tokens: list[dict]) -> set[int]:
    """Parentheses that name cities, as opposed to a citation or a qualifier."""
    groups: dict[int, list[dict]] = {}
    for token in tokens:
        if token["paren"]:
            groups.setdefault(token["paren"], []).append(token)
    kept = set()
    for paren_id, group in groups.items():
        if any(token["semi"] for token in group):
            continue
        if any(token["fold"] in CITATION_FOLDS for token in group):
            continue
        explanatory = [
            token for token in group
            if token["raw"][:1].islower() and token["fold"] not in STOP_FOLDS and token["fold"] not in DIRECTIONAL_FOLDS
        ]
        if explanatory:
            continue
        kept.add(paren_id)
    return kept


def display_name(segment: list[dict]) -> str:
    parts = [segment[0]["raw"]]
    for token in segment[1:]:
        parts.append(("-" if token["hy"] else " ") + token["raw"])
    return "".join(parts)


def analyse_location(text: str, phrases: dict[tuple[str, ...], str]) -> dict:
    """Resolve one Location value into places, countries and leftovers.

    Countries and places are different vocabularies. A place is linked only to
    the countries named in its own ';' clause, so a multi-country string cannot
    attach every country to every city. A country mention never becomes a place.
    """
    tokens = tokenize(text)
    # Dotted aliases are one token and must win over a later phrase match.
    for token in tokens:
        special = dotted_country(token)
        if special:
            token["country"] = special
    hits = match_countries(tokens, phrases)
    covered = {index for index, token in enumerate(tokens) if token.get("country")}
    countries_by_clause: dict[int, list[str]] = {}
    country_at: list[tuple[str, int, int]] = []
    countries: list[str] = []

    def add_country(slug: str, clause: int, at: int) -> None:
        bucket = countries_by_clause.setdefault(clause, [])
        if slug not in bucket:
            bucket.append(slug)
        country_at.append((slug, at, clause))
        if slug not in countries:
            countries.append(slug)

    for index, token in enumerate(tokens):
        if token.get("country"):
            add_country(token["country"], token["clause"], token["i"])
    for slug, start, end in hits:
        if any(index in covered for index in range(start, end)):
            continue
        covered.update(range(start, end))
        add_country(slug, tokens[start]["clause"], tokens[start]["i"])

    kept_parens = place_parens(tokens)
    places: list[dict] = []
    seen: set[str] = set()
    leftovers: list[str] = []
    segment: list[dict] = []
    segment_clause = 0

    def flush_segment() -> None:
        nonlocal segment_clause
        while segment and segment[0]["fold"] in STOP_FOLDS:
            segment.pop(0)
        while segment and segment[-1]["fold"] in STOP_FOLDS:
            segment.pop()
        if not segment:
            return
        if all(token["fold"] in DIRECTIONAL_FOLDS or token["fold"] in STOP_FOLDS for token in segment):
            leftovers.append(display_name(segment))
            segment.clear()
            return
        if segment[0]["fold"] in QUALIFIER_START or any(token["fold"] in JUNK_TOKEN for token in segment):
            leftovers.append(display_name(segment))
            segment.clear()
            return
        slug = "-".join(token["fold"] for token in segment)
        if len(slug) < 2:
            segment.clear()
            return
        if slug in phrases.values() or slug in CONTINENT_SLUGS or not slug:
            segment.clear()
            return
        if slug not in seen:
            seen.add(slug)
            places.append({
                "slug": slug,
                "name": display_name(segment),
                "clause": segment_clause,
                "at": segment[0].get("i", 0),
                "countries": [],
            })
        segment.clear()

    for index, token in enumerate(tokens):
        in_dropped_paren = token["paren"] and token["paren"] not in kept_parens
        if index in covered or in_dropped_paren:
            flush_segment()
            continue
        # A break belongs to the boundary before this token. Flush the previous
        # place, then keep this token: skipping it dropped every city that
        # followed a comma. "and" is the boundary word itself, so it is not a place.
        if token["brk"] or token["fold"] == "and":
            flush_segment()
            if token["fold"] == "and":
                continue
        if not segment:
            segment_clause = token["clause"]
        elif token["clause"] != segment_clause:
            flush_segment()
            segment_clause = token["clause"]
        segment.append(token)
    flush_segment()

    # One country in the clause belongs to every place there. Several countries
    # belong to the nearest place only, so Lagos does not inherit Accra's country
    # and Berlin is not assigned every country on a multi-country row.
    for place in places:
        clause_hits = [hit for hit in country_at if hit[2] == place["clause"]]
        unique = []
        for slug, _at, _clause in clause_hits:
            if slug not in unique:
                unique.append(slug)
        if len(unique) == 1:
            place["countries"] = unique
        elif clause_hits:
            slug, _at, _clause = min(clause_hits, key=lambda hit: abs(hit[1] - place["at"]))
            place["countries"] = [slug]

    return {"places": places, "countries": countries, "leftovers": leftovers}


def remember_place(places: dict[str, dict], place: dict) -> None:
    """Keep one display name and the owning countries for a place slug."""
    entry = places.setdefault(place["slug"], {"name": place["name"], "countries": []})
    if sum(ch.isupper() for ch in place["name"]) > sum(ch.isupper() for ch in entry["name"]):
        entry["name"] = place["name"]
    for slug in place.get("countries") or []:
        if slug not in entry["countries"]:
            entry["countries"].append(slug)


def derive_geo(workbook_path: Path) -> dict:
    """Countries, places and the per-clause review for the whole workbook.

    Countries come from the Member Countries columns. Places come from the
    Location prose of verified rows. The two vocabularies must stay disjoint: a
    value is either a country or a place, never both.
    """
    workbook = load_workbook(workbook_path, read_only=True, data_only=True)
    country_slugs: dict[str, list[str]] = {}
    for sheet in SHEETS:
        for row in read_rows(workbook, sheet):
            for part in clean(row.get("Member Countries")).split(";"):
                part = part.strip()
                if not part:
                    continue
                if part != part.lower() or not all(ch.isalnum() or ch == "-" for ch in part) or "--" in part:
                    raise SystemExit(f"Member Countries column holds a non-slug value: {part!r}")
                country_slugs.setdefault(part, [])
                term_id = clean(row.get("ID"))
                if term_id and term_id not in country_slugs[part]:
                    country_slugs[part].append(term_id)
    workbook.close()

    countries = {
        slug: COUNTRY_DISPLAY_OVERRIDES.get(slug, title_country(slug))
        for slug in sorted(country_slugs)
    }
    for slug, name in PROSE_COUNTRIES.items():
        countries.setdefault(slug, name)
    phrases = build_country_phrases(countries)

    places: dict[str, dict] = {}
    clauses: dict[str, dict] = {}
    workbook = load_workbook(workbook_path, read_only=True, data_only=True)
    for sheet in SHEETS:
        for row in read_rows(workbook, sheet):
            location = clean(row.get("Location"))
            if not location or location in clauses:
                continue
            analysis = analyse_location(location, phrases)
            column_parts = [
                part.strip() for part in clean(row.get("Member Countries")).split(";") if part.strip()
            ]
            extras = [slug for slug in analysis["countries"] if slug not in column_parts]
            missing = [slug for slug in column_parts if slug not in analysis["countries"]]
            clauses[location] = {
                "sheet": sheet,
                "places": [place["slug"] for place in analysis["places"]],
                "proseCountries": analysis["countries"],
                "columnCountries": column_parts,
                "proseOnlyCountries": extras,
                "columnOnlyCountries": missing,
                "leftovers": analysis["leftovers"],
            }
            for place in analysis["places"]:
                # A single column country may fill a clause the prose did not name.
                # Several column countries must not be copied onto every place:
                # that is the Lagos/Accra bug. Clause linking already did that work.
                linked = list(place["countries"])
                if not linked and len(column_parts) == 1:
                    linked = list(column_parts)
                place["countries"] = linked
                remember_place(places, place)
    workbook.close()

    overlap = sorted(set(places) & set(countries))
    if overlap:
        raise SystemExit(f"Geo vocabularies overlap; a value cannot be both place and country: {overlap}")

    return {
        "countries": countries,
        "places": places,
        "clauses": [dict(clause, location=location) for location, clause in sorted(clauses.items())],
    }


# ---------------------------------------------------------------------------
# Workbook plumbing (unchanged from the earlier passes)
# ---------------------------------------------------------------------------

def normalize(value: str) -> str:
    return "".join(ch.lower() for ch in str(value or "") if ch.isalnum())


def clean(value) -> str:
    return str(value or "").strip()


def parents_of(row: dict) -> tuple[str, ...]:
    found = []
    for index in range(1, 6):
        parent = clean(row.get(f"Parent ID {index}"))
        if parent and parent not in found:
            found.append(parent)
    return tuple(found)


def aliases_of(row: dict) -> list[str]:
    found = []
    for index in range(1, 5):
        alias = clean(row.get(f"Alias {index}"))
        if alias and alias not in found:
            found.append(alias)
    return found


def read_rows(workbook, name: str) -> list[dict]:
    sheet = workbook[name]
    rows = sheet.iter_rows(values_only=True)
    header = [clean(cell) for cell in next(rows)]
    parsed = []
    for raw in rows:
        if raw is None or all(cell is None for cell in raw):
            continue
        item = {header[index]: raw[index] if index < len(raw) else None for index in range(len(header))}
        if clean(item.get("ID") or item.get("Runtime ID")):
            item["_sheet"] = name
            parsed.append(item)
    return parsed


def map_regions(tokens: list[str]) -> list[str]:
    slugs = []
    unknown = [token for token in tokens if token not in TOKEN_SLUGS]
    if unknown:
        raise SystemExit(f"Region token has no fold onto the app's region tabs: {unknown}")
    for token in tokens:
        for slug in TOKEN_SLUGS[token]:
            if slug not in slugs:
                slugs.append(slug)
    return slugs


def geo_of(group: list[dict], phrases: dict[tuple[str, ...], str]) -> dict:
    """Geo fields for one term from its verified rows.

    The Member Countries column is authoritative when present. Location prose
    names countries only for rows the research left without a column value
    (Africa and North America), so it is used as the fallback there. Places
    always come from the Location prose.
    """
    column: list[str] = []
    locations: list[str] = []
    prose_countries: list[str] = []
    places: list[dict] = []
    seen_places: set[str] = set()
    for row in group:
        for part in clean(row.get("Member Countries")).split(";"):
            part = part.strip()
            if part and part not in column:
                column.append(part)
        location = clean(row.get("Location"))
        if location and location not in locations:
            locations.append(location)
            analysis = analyse_location(location, phrases)
            for slug in analysis["countries"]:
                if slug not in prose_countries:
                    prose_countries.append(slug)
            for place in analysis["places"]:
                if place["slug"] not in seen_places:
                    seen_places.add(place["slug"])
                    places.append(place)
    return {
        "places": places,
        "memberCountries": column or prose_countries,
    }


def research_of(group: list[dict], phrases: dict[tuple[str, ...], str]) -> dict:
    verified = [row for row in group if clean(row.get("Research Status")).lower() == "verified"] or group
    row = verified[0]
    tokens = []
    for item in group:
        for part in clean(item.get("Region")).split(";"):
            part = part.strip()
            if part and part not in tokens:
                tokens.append(part)
    geo = geo_of([item for item in group if clean(item.get("Research Status")).lower() == "verified"], phrases)
    research = {
        "status": clean(row.get("Research Status")) or "Verified",
        "label": clean(row.get("Label")) or None,
        "era": parse_era(clean(row.get("Era"))),
        "regionTokens": tokens,
        "researchedKind": clean(row.get("Runtime Kind")) or None,
    }
    if geo["places"]:
        research["places"] = [place["slug"] for place in geo["places"]]
    if geo["memberCountries"]:
        research["memberCountries"] = geo["memberCountries"]
    return research


def migrate_geo(research: dict, phrases: dict[tuple[str, ...], str]) -> list[dict]:
    """Bring one stored research record to the slug-list shape.

    Terms the workbook does not mention keep their earlier research, which still
    holds the prose location string. The same clause analysis runs over it so
    every shipped record ends up in exactly one shape and the app never sees a
    free-text location. The returned places must be registered in the geo
    catalog or the loader will reject the slugs.
    """
    location = clean(research.get("location"))
    column = research.get("memberCountries")
    parts: list[str] = []
    if isinstance(column, str):
        parts = [part.strip() for part in column.split(";") if part.strip()]
    elif isinstance(column, list):
        parts = [clean(part) for part in column if clean(part)]
    found: list[dict] = []
    if location:
        analysis = analyse_location(location, phrases)
        found = analysis["places"]
        if not parts:
            parts = analysis["countries"]
    research.pop("location", None)
    if parts:
        research["memberCountries"] = parts
    else:
        research.pop("memberCountries", None)
    if found:
        research["places"] = [place["slug"] for place in found]
    elif not isinstance(research.get("places"), list):
        research.pop("places", None)
    return found


def would_cycle(by_id: dict, child: str, parent: str) -> bool:
    seen = set()
    stack = [parent]
    while stack:
        current = stack.pop()
        if current == child:
            return True
        if current in seen:
            continue
        seen.add(current)
        stack.extend(by_id.get(current, {}).get("parentIds") or [])
    return False


def merge(catalog_path: Path, workbook_path: Path, output_path: Path, geo_path: Path) -> dict:
    catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
    previous_counts = dict(catalog.get("counts") or {})
    previous_version = catalog.get("version")

    geo = derive_geo(workbook_path)
    phrases = build_country_phrases(geo["countries"])

    workbook = load_workbook(workbook_path, read_only=True, data_only=True)
    grouped = defaultdict(list)
    for sheet in SHEETS:
        for row in read_rows(workbook, sheet):
            grouped[clean(row.get("ID"))].append(row)
    workbook.close()

    by_id = {term["id"]: term for term in catalog["terms"]}
    kept_ids = set(by_id)
    report = {
        "updated": 0,
        "added": 0,
        "heldNonMusical": 0,
        "parentDisagreements": [],
        "droppedAliases": [],
        "droppedParents": [],
        "unchangedBecauseAbsent": len(kept_ids - set(grouped)),
    }

    for term_id, group in grouped.items():
        verified = [row for row in group if clean(row.get("Research Status")).lower() == "verified"]
        if not verified:
            continue
        kinds = {clean(row.get("Runtime Kind")) for row in verified}
        musical = [row for row in verified if clean(row.get("Runtime Kind")) in MUSICAL]
        research = research_of(verified, phrases)
        if not musical:
            if term_id in by_id:
                by_id[term_id]["research"] = research
                report["heldNonMusical"] += 1
            else:
                report["heldNonMusical"] += 1
            continue

        parent_sets = {parents_of(row) for row in musical}
        if len(parent_sets) == 1:
            requested_parents = list(next(iter(parent_sets)))
        else:
            requested_parents = list(by_id.get(term_id, {}).get("parentIds") or [])
            report["parentDisagreements"].append(term_id)

        name = clean(musical[0].get("Name"))
        kind = clean(musical[0].get("Runtime Kind"))
        aliases = []
        for row in musical:
            for alias in aliases_of(row):
                if alias not in aliases and normalize(alias) != normalize(name):
                    aliases.append(alias)
        tokens = []
        for row in musical:
            for part in clean(row.get("Region")).split(";"):
                part = part.strip()
                if part and part not in tokens:
                    tokens.append(part)
        mapped = map_regions(tokens)
        existing = by_id.get(term_id)
        if existing is None:
            regions = mapped
            term = {
                "id": term_id,
                "name": name,
                "kind": kind,
                "regions": regions,
                "aliases": aliases,
                "parentIds": [],
                "research": research,
            }
            by_id[term_id] = term
            report["added"] += 1
        else:
            # An empty Region column must not strip a region the catalog already
            # has. The Global sheet has no Region column; only a row that actually
            # names region tokens may replace the covered ones.
            if mapped:
                preserved = [slug for slug in existing.get("regions") or [] if slug not in COVERED_REGIONS]
                regions = preserved + [slug for slug in mapped if slug not in preserved]
            else:
                regions = list(existing.get("regions") or [])
            if normalize(existing.get("name")) != normalize(name) and not any(
                normalize(alias) == normalize(existing.get("name")) for alias in aliases
            ):
                aliases.append(existing["name"])
            # A repeat import must keep aliases the previous pass preserved. The
            # workbook only lists its own aliases, so replacing the list outright
            # would drop the previous canonical spelling on the second run.
            for alias in existing.get("aliases") or []:
                if alias not in aliases and normalize(alias) != normalize(name):
                    aliases.append(alias)
            # A sheet with no Location and no Member Countries (the Global sheet)
            # must not wipe geography an earlier regional pass already stored.
            new_has_geo = any(clean(row.get("Location")) or clean(row.get("Member Countries")) for row in verified)
            if not new_has_geo and existing.get("research"):
                kept = dict(existing["research"])
                for place in migrate_geo(kept, phrases):
                    remember_place(geo["places"], place)
                if kept.get("places") and "places" not in research:
                    research["places"] = kept["places"]
                if kept.get("memberCountries") and "memberCountries" not in research:
                    research["memberCountries"] = kept["memberCountries"]
            existing.update({
                "name": name,
                "kind": kind,
                "regions": regions,
                "aliases": aliases,
                "research": research,
            })
            report["updated"] += 1

        accepted = []
        for parent in requested_parents:
            if parent == term_id or parent not in by_id and parent not in grouped:
                report["droppedParents"].append({"term": term_id, "parent": parent, "reason": "unknown"})
                continue
            if parent not in by_id:
                report["droppedParents"].append({"term": term_id, "parent": parent, "reason": "not-yet-present"})
                continue
            if would_cycle(by_id, term_id, parent):
                report["droppedParents"].append({"term": term_id, "parent": parent, "reason": "cycle"})
                continue
            if parent not in accepted:
                accepted.append(parent)
        by_id[term_id]["parentIds"] = accepted

    # A second pass picks up parents whose target was added later in the same merge.
    for term_id, group in grouped.items():
        verified = [row for row in group if clean(row.get("Research Status")).lower() == "verified"]
        musical = [row for row in verified if clean(row.get("Runtime Kind")) in MUSICAL]
        if not musical or term_id not in by_id:
            continue
        parent_sets = {parents_of(row) for row in musical}
        if len(parent_sets) != 1:
            continue
        accepted = list(by_id[term_id].get("parentIds") or [])
        for parent in parent_sets.pop():
            if parent in accepted or parent == term_id or parent not in by_id:
                continue
            if would_cycle(by_id, term_id, parent):
                continue
            accepted.append(parent)
        by_id[term_id]["parentIds"] = accepted

    # Terms the workbook does not mention keep their research; migrate those
    # records to the slug-list shape so the shipped catalog holds one format.
    migrated = 0
    for term in by_id.values():
        research = term.get("research")
        if research and ("location" in research or isinstance(research.get("memberCountries"), str)):
            for place in migrate_geo(research, phrases):
                remember_place(geo["places"], place)
            migrated += 1

    # Two canonical terms must not share a lookup key. A workbook id wins over an
    # older id the workbook does not name; the older name is kept as an alias.
    by_name = {}
    for term in list(by_id.values()):
        by_name.setdefault(normalize(term.get("name")), []).append(term)
    for group in by_name.values():
        if len(group) < 2:
            continue
        in_book = [term for term in group if term["id"] in grouped]
        keeper = in_book[0] if len(in_book) == 1 else max(
            group, key=lambda term: (bool((term.get("research") or {}).get("researchedKind")), len(term.get("regions") or []))
        )
        for other in group:
            if other["id"] == keeper["id"]:
                continue
            if normalize(other.get("name")) != normalize(keeper.get("name")):
                keeper.setdefault("aliases", []).append(other["name"])
            for alias in other.get("aliases") or []:
                if alias not in keeper.get("aliases", []):
                    keeper.setdefault("aliases", []).append(alias)
            for region in other.get("regions") or []:
                if region not in keeper.get("regions", []):
                    keeper.setdefault("regions", []).append(region)
            del by_id[other["id"]]
            if other["id"] not in kept_ids:
                report["added"] -= 1
            for term in by_id.values():
                rewritten = []
                for parent in term.get("parentIds") or []:
                    parent = keeper["id"] if parent == other["id"] else parent
                    if parent != term["id"] and parent not in rewritten:
                        rewritten.append(parent)
                term["parentIds"] = rewritten

    names = {}
    for term in by_id.values():
        key = normalize(term.get("name"))
        names.setdefault(key, set()).add(term["id"])
    for term in by_id.values():
        kept = []
        for alias in term.get("aliases") or []:
            key = normalize(alias)
            owners = names.get(key, set())
            if key and owners and owners != {term["id"]}:
                report["droppedAliases"].append({"term": term["id"], "alias": alias, "reason": "shadows another term"})
                continue
            if any(normalize(other) == key and other != alias for other in kept):
                continue
            kept.append(alias)
        term["aliases"] = kept

    alias_map = {}
    for term in by_id.values():
        for alias in term.get("aliases") or []:
            key = normalize(alias)
            if not key or key in names:
                continue
            alias_map[key] = {"value": alias, "target": term["id"]}

    terms = sorted(by_id.values(), key=lambda term: term["id"])
    genres = sum(1 for term in terms if term.get("kind") == "Genre")
    styles = sum(1 for term in terms if term.get("kind") == "Style")
    parent_edges = sum(len(term.get("parentIds") or []) for term in terms)
    multi_parent = sum(1 for term in terms if len(term.get("parentIds") or []) > 1)
    source_hash = hashlib.sha256(workbook_path.read_bytes()).hexdigest()
    catalog["version"] = VERSION
    catalog["source"]["note"] = (
        "Vocabulary rows the 2026-11-09 audited regional workbook verifies replace the matching "
        "term, and its Europe sheet is the first audited Europe pass. Terms the workbook does "
        "not mention are unchanged. Unverified rows are not imported. Research locations are "
        "carried as validated place and country slugs, never as prose."
    )
    # The earlier regional workbook stays in the manifest. This pass adds its
    # own row; a repeat run replaces that row instead of stacking copies.
    inputs = catalog["source"].setdefault("inputs", [])
    inputs[:] = [
        entry for entry in inputs
        if not (entry.get("role") == "regional-vocabulary" and entry.get("file") == workbook_path.name)
    ]
    inputs.append({
        "role": "regional-vocabulary",
        "file": workbook_path.name,
        "sha256": source_hash,
    })
    catalog["counts"] = {
        "genres": genres,
        "styles": styles,
        "terms": len(terms),
        "aliases": len(alias_map),
        "regions": catalog["counts"].get("regions", 10),
        "exclusions": len(catalog.get("exclusions") or {}),
        "ambiguous": len(catalog.get("ambiguous") or {}),
        "parentEdges": parent_edges,
        "multiParentTerms": multi_parent,
    }
    catalog["importDiff"] = {
        "previousVersion": previous_version,
        "termsAdded": report["added"],
        "termsRemoved": 0,
        "termsChanged": report["updated"],
        "genresAdded": genres - previous_counts.get("genres", 0),
        "stylesAdded": styles - previous_counts.get("styles", 0),
        "parentEdgesAdded": parent_edges - previous_counts.get("parentEdges", 0),
        "parentEdgesRemoved": 0,
        "multiParentTermsAdded": multi_parent - previous_counts.get("multiParentTerms", 0),
        "aliasesAdded": len(alias_map) - previous_counts.get("aliases", 0),
        "aliasesRemoved": 0,
        "kindChanges": report["updated"],
        "idChanges": 0,
        "regionChanges": report["updated"],
        "geoMigrations": migrated,
        "excludedRows": report["heldNonMusical"],
        "unchangedBecauseAbsent": report["unchangedBecauseAbsent"],
    }
    catalog["aliases"] = dict(sorted(alias_map.items()))
    catalog["terms"] = terms
    output_path.write_text(json.dumps(catalog, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")

    used_countries = set()
    used_places = set()
    for term in by_id.values():
        research = term.get("research") or {}
        used_countries.update(research.get("memberCountries") or [])
        used_places.update(research.get("places") or [])
    missing_countries = sorted(used_countries - set(geo["countries"]))
    if missing_countries:
        # A stored country slug has to resolve. These are reported, not guessed
        # into a different country; the display name is the slug's own title.
        print("countries stored but absent from the workbook vocabulary:", missing_countries)
        for slug in missing_countries:
            geo["countries"][slug] = COUNTRY_DISPLAY_OVERRIDES.get(slug, title_country(slug))
    missing_places = sorted(slug for slug in used_places if slug not in geo["places"])
    if missing_places:
        raise SystemExit(f"Places stored without a catalog entry: {missing_places[:30]}")
    overlap = sorted(set(geo["places"]) & set(geo["countries"]))
    if overlap:
        raise SystemExit(f"Geo vocabularies overlap; a value cannot be both place and country: {overlap}")

    geo_document = {
        "version": VERSION,
        "source": {"file": workbook_path.name, "sha256": source_hash},
        "countries": dict(sorted(geo["countries"].items())),
        "places": {
            slug: {"name": place["name"], "countries": sorted(place["countries"])}
            for slug, place in sorted(geo["places"].items())
        },
    }
    geo_path.write_text(json.dumps(geo_document, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")

    review_path = Path(__file__).resolve().parent / "research-geo-review.json"
    review_path.write_text(json.dumps({
        "version": VERSION,
        "source": {"file": workbook_path.name, "sha256": source_hash},
        "countries": geo_document["countries"],
        "places": geo_document["places"],
        "clauses": geo["clauses"],
    }, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")

    report["counts"] = catalog["counts"]
    report["version"] = VERSION
    report["sha256"] = source_hash
    report["geo"] = {
        "countries": len(geo["countries"]),
        "places": len(geo["places"]),
        "clauses": len(geo["clauses"]),
        "migrated": migrated,
    }
    return report


def main() -> int:
    root = Path(__file__).resolve().parents[2]
    catalog = root / "DeezSpoTag.Services/Genre/Data/genre-intelligence-taxonomy.json"
    if len(sys.argv) < 3 or len(sys.argv) > 4:
        print(
            "usage: python3 scripts/genre/merge_regional_taxonomy.py"
            " <master.xlsx> <output.json> [geo.json]",
            file=sys.stderr,
        )
        return 1
    geo = Path(sys.argv[3]) if len(sys.argv) > 3 else root / "DeezSpoTag.Services/Genre/Data/genre-research-geo.json"
    workbook = Path(sys.argv[1])
    output = Path(sys.argv[2])
    report = merge(catalog, workbook, output, geo)
    print(json.dumps({key: report[key] for key in ("version", "sha256", "counts", "added", "updated", "heldNonMusical", "unchangedBecauseAbsent", "geo")}, indent=2))
    print("parent disagreements", len(report["parentDisagreements"]))
    print("dropped aliases", len(report["droppedAliases"]))
    print("dropped parents", len(report["droppedParents"]))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
