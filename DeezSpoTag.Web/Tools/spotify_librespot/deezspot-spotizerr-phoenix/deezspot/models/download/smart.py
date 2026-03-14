#!/usr/bin/python3

from deezspot.models.download.track import Track
from deezspot.models.download.album import Album
from deezspot.models.download.playlist import Playlist

class Smart:
	def __init__(self) -> None:
		self.track: Track = None
		self.album: Album = None
		self.playlist: Playlist = None
		self.type = None
		self.source = None