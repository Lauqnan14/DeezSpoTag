#!/usr/bin/python3

class Track:
	def __init__(
		self,
		tags: dict,
		song_path: str,
		file_format: str,
		quality: str,
		link: str,
		ids: int
) -> None:

		self.tags = tags
		self.__set_tags()

		music_display = getattr(self, 'music', getattr(self, 'name', "Unknown Track"))
		artist_display = getattr(self, 'artist', "Unknown Artist")
		self.song_name = f"{music_display} - {artist_display}"

		self.song_path = song_path
		self.file_format = file_format
		self.quality = quality
		self.link = link
		self.ids = ids
		self.md5_image = None
		self.success = True
		self.__set_track_md5()

	def __set_tags(self):
		for tag, value in self.tags.items():
			setattr(
				self, tag, value
			)

	def __set_track_md5(self):
		self.track_md5 = f"track/{self.ids}"

	def set_fallback_ids(self, fallback_ids):
		self.fallback_ids = fallback_ids
		self.fallback_track_md5 = f"track/{self.fallback_ids}"