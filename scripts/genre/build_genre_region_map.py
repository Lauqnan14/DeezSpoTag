#!/usr/bin/env python3
"""Derive the Genre Intelligence region map from the shipped world map.

The source asset (``wwwroot/images/world/world.svg``) is a MapSVG world map whose
250 country paths each carry an ISO-3166 alpha-2 ``id``. It is left untouched.

This script writes ``world-regions.svg`` beside it, adding one ``data-regions``
attribute per country so the browser can highlight a region with plain CSS instead
of per-country JavaScript.

Membership is deliberately many-to-many, because DeezSpoTag's regions are not a
partition of the world. MENA overlaps Africa (Egypt, Morocco, Djibouti), Central
America belongs to both North America and Latin America & Caribbean, and Turkey and
Cyprus sit in both Europe and MENA. A country that belongs to two regions is
highlighted when either of them is browsed, which is exactly what the overlap means.

Usage:
    python3 scripts/genre/build_genre_region_map.py

Re-run it after replacing the source map. It is idempotent.
"""

from __future__ import annotations

import gzip
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SOURCE = REPO_ROOT / "DeezSpoTag.Web/wwwroot/images/world/world.svg"
OUTPUT = REPO_ROOT / "DeezSpoTag.Web/wwwroot/images/world/world-regions.svg"

AFRICA = "africa"
NORTH_AMERICA = "north-america"
LATIN_AMERICA = "latin-america-caribbean"
EUROPE = "europe"
MENA = "mena"
CENTRAL_ASIA = "central-asia"
SOUTH_ASIA = "south-asia"
EAST_ASIA = "east-asia"
SOUTHEAST_ASIA = "southeast-asia"
OCEANIA = "oceania-pacific"

# The nine slugs must match PersonalGenreRegions in
# DeezSpoTag.Services/Genre/PersonalGenreRegions.cs.
REGIONS = {
    AFRICA, NORTH_AMERICA, LATIN_AMERICA, EUROPE, MENA, CENTRAL_ASIA,
    SOUTH_ASIA, EAST_ASIA, SOUTHEAST_ASIA, OCEANIA,
}

# Deliberately unassigned countries, so that nothing is ever left off the map by
# accident. These are uninhabited or negligible territories: they have no music
# scene of their own, and picking a region for them would be a fiction. The
# generator fails if the computed neutral set ever differs from this one.
NEUTRAL = {
    "BM",  # Bermuda
    "BV",  # Bouvet Island
    "CC",  # Cocos (Keeling) Islands
    "CX",  # Christmas Island
    "FK",  # Falkland Islands
    "GL",  # Greenland
    "GO",  # Glorioso Islands
    "GS",  # South Georgia
    "HM",  # Heard and McDonald Islands
    "IO",  # British Indian Ocean Territory
    "JU",  # Juan Fernandez
    "MS",  # Montserrat
    "NF",  # Norfolk Island
    "PM",  # Saint Pierre and Miquelon
    "PN",  # Pitcairn Islands
    "SX",  # Sint Maarten
    "TF",  # French Southern Territories
    "TK",  # Tokelau
    "WF",  # Wallis and Futuna
}

SOLE = {
    # Africa, excluding the countries that also belong to MENA.
    AFRICA: """
        AO BJ BW BF BI CM CV CF TD KM CG CD CI ET GA GM GH GN GW KE LS LR MG MW
        ML MU MZ NA NE NG RW ST SN SC SL SZ TZ TG UG ZA ER GQ MR RE SH SS YT
        ZM ZW
    """,
    NORTH_AMERICA: "US CA",
    EUROPE: """
        AL AD AT BY BE BA BG HR CZ DK EE FO FI FR DE GI GR GG IS IE IM IT JE
        XK LV LI LT LU MT MD MC ME NL MK NO PL PT RO RU SM RS SK SI ES SE CH
        UA GB VA SJ AX HU
    """,
    # Central Asia is its own region, not a tail of South Asia. These five states
    # were briefly filed under South Asia because Central Asia was missing from the
    # original specification; they belong here.
    CENTRAL_ASIA: "KZ KG TJ TM UZ",
    SOUTH_ASIA: "IN PK BD LK NP BT MV AF",
    EAST_ASIA: "CN JP KR KP TW HK MO MN",
    SOUTHEAST_ASIA: "ID MY TH VN KH LA MM PH SG BN TL",
    OCEANIA: """
        AU NZ PG FJ SB VU WS TO TV KI FM MH PW NR NU CK PF NC GU MP AS
    """,
    LATIN_AMERICA: """
        CO VE EC PE BO BR PY UY AR CL GY SR GF
        CU JM HT DO PR TT BB BS AG DM GD KN LC VC AW CW BQ KY TC VG VI AI
        MF BL GP MQ
    """,
}

# Countries that belong to more than one region, with the reason each overlap is real.
MULTI = {
    # North Africa and the Horn are simultaneously African and MENA.
    "DZ": [AFRICA, MENA], "EG": [AFRICA, MENA], "LY": [AFRICA, MENA],
    "MA": [AFRICA, MENA], "TN": [AFRICA, MENA], "SD": [AFRICA, MENA],
    "EH": [AFRICA, MENA], "DJ": [AFRICA, MENA], "SO": [AFRICA, MENA],
    # The Levant, the Gulf and the peninsula.
    MENA: """
        BH IQ IR JO KW LB OM PS QA SA SY TR AE YE AM AZ GE IL
    """,
    # Turkey and Cyprus are European and MENA at once.
    "TR": [EUROPE, MENA], "CY": [EUROPE, MENA],
    # Mexico and Central America are North American geographically and Latin
    # American culturally, which is the same split Tejano straddles.
    "MX": [NORTH_AMERICA, LATIN_AMERICA], "GT": [NORTH_AMERICA, LATIN_AMERICA],
    "BZ": [NORTH_AMERICA, LATIN_AMERICA], "HN": [NORTH_AMERICA, LATIN_AMERICA],
    "SV": [NORTH_AMERICA, LATIN_AMERICA], "NI": [NORTH_AMERICA, LATIN_AMERICA],
    "CR": [NORTH_AMERICA, LATIN_AMERICA], "PA": [NORTH_AMERICA, LATIN_AMERICA],
}


def build_membership() -> dict[str, list[str]]:
    """Country ISO code -> region slugs, ordered by the registry's display order."""
    membership: dict[str, list[str]] = {}

    def add(code: str, slug: str) -> None:
        membership.setdefault(code, [])
        if slug not in membership[code]:
            membership[code].append(slug)

    for slug, codes in SOLE.items():
        for code in codes.split():
            add(code, slug)

    for key, slugs in MULTI.items():
        if key in REGIONS:
            # A region given as a block of countries that all share extra membership.
            for code in slugs.split():
                add(code, key)
            continue
        # A single country that belongs to several regions.
        for slug in slugs:
            add(key, slug)

    order = [
        NORTH_AMERICA, LATIN_AMERICA, AFRICA, EUROPE, MENA, CENTRAL_ASIA,
        SOUTH_ASIA, EAST_ASIA, SOUTHEAST_ASIA, OCEANIA,
    ]
    for code, slugs in membership.items():
        membership[code] = sorted(set(slugs), key=order.index)
    return membership


def main() -> int:
    if not SOURCE.exists():
        print(f"source map not found: {SOURCE}", file=sys.stderr)
        return 1

    source = SOURCE.read_text(encoding="utf-8")
    paths = re.findall(
        r'<path\s+d="([^"]+)"\s+title="([^"]*)"\s+id="([A-Za-z]{2})"\s*/>', source)
    if not paths:
        print("no country paths found in the source map", file=sys.stderr)
        return 1

    membership = build_membership()

    # The viewBox keeps the source's own scale.
    width = re.search(r'width="([\d.]+)"', source).group(1)
    height = re.search(r'height="([\d.]+)"', source).group(1)
    view_box = f"0 0 {width} {height}"

    out = [
        '<?xml version="1.0" encoding="UTF-8"?>\n',
        "<!-- Generated by scripts/genre/build_genre_region_map.py from world.svg.\n",
        "     Do not edit by hand. Each country carries data-regions so a region can be\n",
        "     highlighted in CSS, and membership is many-to-many by design.\n",
        "\n",
        "     Path data is copied from the source byte for byte. Do not round or\n",
        "     simplify it: the coastlines are long runs of implicit lineto pairs made of\n",
        "     sub-unit deltas, so quantising the numbers flattens real geometry.\n",
        "\n",
        "     data-country is the ISO 3166-1 alpha-2 code, kept so a country's region\n",
        "     membership is verifiable from the asset rather than only from this script. -->\n",
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{view_box}"\n',
        '     preserveAspectRatio="xMidYMid meet" aria-hidden="true" focusable="false"\n',
        '     role="img">',
    ]
    unassigned = []
    for d, _title, code in paths:
        slugs = membership.get(code.upper())
        if slugs:
            out.append(
                f'<path d="{d}" data-country="{code.upper()}"'
                f' data-regions="{" ".join(slugs)}"/>')
        else:
            out.append(f'<path d="{d}" data-country="{code.upper()}"/>')
            unassigned.append(code.upper())
    out.append("</svg>")

    # Nothing may be dropped from the map by accident.
    unexpected = sorted(set(unassigned) - NEUTRAL)
    missing = sorted(NEUTRAL - set(unassigned))
    if unexpected or missing:
        print(
            "the neutral country set changed; update NEUTRAL deliberately.\n"
            f"  newly unassigned: {' '.join(unexpected) or 'none'}\n"
            f"  no longer unassigned: {' '.join(missing) or 'none'}",
            file=sys.stderr,
        )
        return 1

    markup = "".join(out)

    # The geometry is copied verbatim, so prove it. A quiet rounding pass once
    # flattened these coastlines and the map was unusable; this makes that loud.
    for d, _title, code in paths:
        if f'<path d="{d}"' not in markup:
            print(f"path data for {code} was altered; copy it verbatim", file=sys.stderr)
            return 1
    OUTPUT.write_text(markup, encoding="utf-8")
    print(f"wrote {OUTPUT.relative_to(REPO_ROOT)}")
    print(f"  countries:  {len(paths)}")
    print(f"  assigned:   {len(paths) - len(unassigned)}")
    print(f"  neutral:    {len(unassigned)} ({' '.join(sorted(unassigned))})")
    print(f"  size:       {len(markup):,} bytes raw, "
          f"{len(gzip.compress(markup.encode('utf-8'), 9)):,} gzipped")
    for slug in sorted(REGIONS):
        count = sum(1 for slugs in membership.values() if slug in slugs)
        print(f"  {slug:<26} {count} countries")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
