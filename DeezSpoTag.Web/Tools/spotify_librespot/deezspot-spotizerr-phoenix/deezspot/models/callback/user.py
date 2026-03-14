#!/usr/bin/python3

from dataclasses import dataclass, field
from typing import Optional

from .common import IDs


@dataclass
class userObject:
    """A user object representation."""
    name: str = ""
    type: str = "user"
    ids: IDs = field(default_factory=IDs)
