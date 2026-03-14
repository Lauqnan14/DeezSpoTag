#!/usr/bin/python3

from deezspot.models.download.track import Track

class Album:
	def __init__(self, ids: int) -> None:
		self.tracks: list[Track] = []
		self.zip_path = None
		self.image = None
		self.album_quality = None
		self.md5_image = None
		self.ids = ids
		self.nb_tracks = None
		self.album_name = None
		self.upc = None
		self.tags = None
		self.__set_album_md5()

	def __set_album_md5(self):
		self.album_md5 = f"album/{self.ids}"