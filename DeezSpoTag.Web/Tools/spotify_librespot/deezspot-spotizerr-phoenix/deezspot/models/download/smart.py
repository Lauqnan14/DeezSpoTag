#!/usr/bin/python3

from typing import Optional

from deezspot.models.download.track import Track
from deezspot.models.download.album import Album
from deezspot.models.download.playlist import Playlist

class Smart:
	def __init__(self) -> None:
		self.track: Optional[Track] = None
		self.album: Optional[Album] = None
		self.playlist: Optional[Playlist] = None
		self.type = None
		self.source = None
