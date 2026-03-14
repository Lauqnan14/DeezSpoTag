#!/usr/bin/python3

from dataclasses import dataclass
from typing import Optional
from enum import Enum


@dataclass
class IDs:
    """IDs used across objects; isrc only required on items supporting it."""
    spotify: Optional[str] = None
    deezer: Optional[str] = None
    isrc: Optional[str] = None
    upc: Optional[str] = None


@dataclass
class ReleaseDate:
    """Mandatory release date structure."""
    year: int
    month: Optional[int] = None  # null if unknown
    day: Optional[int] = None    # null if unknown


class Service(str, Enum):
    """Supported source services for downloads."""
    SPOTIFY = "spotify"
    DEEZER = "deezer" 