#!/usr/bin/python3

class Preferences:
    def __init__(self) -> None:
        self.link = None
        self.song_metadata: dict = None
        self.quality_download = None
        self.output_dir = None
        self.ids = None
        self.json_data = None
        self.playlist_tracks_json = None
        self.recursive_quality = None
        self.recursive_download = None
        self.not_interface = None
        self.make_zip = None
        self.real_time_dl = None
        self.custom_dir_format = None
        self.custom_track_format = None
        self.pad_tracks = True  # Default to padded track numbers (01, 02, etc.)
        self.pad_number_width = 'auto'  # New: number of digits for padding; 'auto' uses total tracks
        self.initial_retry_delay = 30  # Default initial retry delay in seconds
        self.retry_delay_increase = 30  # Default increase in delay between retries in seconds
        self.max_retries = 5  # Default maximum number of retries per track
        self.save_cover: bool = False # Option to save a cover.jpg image
        # New: artist separator for joining multiple artists or album artists
        self.artist_separator: str = "; "
        # New: when True, use Spotify metadata for tagging in spo flows
        self.spotify_metadata: bool = False
        # New: optional Spotify trackObject to use when spotify_metadata is True
        self.spotify_track_obj = None
        # New: optional Spotify albumObject (from spotloader tracking_album) for album-level spotify_metadata
        self.spotify_album_obj = None
        # New: real-time throttling multiplier (0 disables pacing, 1=real-time, >1 speeds up up to 10x)
        self.real_time_multiplier: int = 1