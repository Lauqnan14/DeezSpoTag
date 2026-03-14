#!/usr/bin/python3

class Episode:
    def __init__(
        self,
        tags: dict,
        episode_path: str,
        file_format: str,
        quality: str,
        link: str,
        ids: int
    ) -> None:
        self.tags = tags
        self.__set_tags()
        self.episode_name = f"{self.name} - {self.show}"
        self.episode_path = episode_path
        self.file_format = file_format
        self.quality = quality
        self.link = link
        self.ids = ids
        self.md5_image = None
        self.success = True
        self.__set_episode_md5()

    def __set_tags(self):
        for tag, value in self.tags.items():
            setattr(self, tag, value)

    def __set_episode_md5(self):
        self.episode_md5 = f"episode/{self.ids}"

    def set_fallback_ids(self, fallback_ids):
        self.fallback_ids = fallback_ids
        self.fallback_episode_md5 = f"episode/{self.fallback_ids}"