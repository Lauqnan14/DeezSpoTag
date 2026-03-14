#!/usr/bin/python3

from base64 import b64encode
import mutagen
from mutagen.flac import FLAC, Picture as FLACPicture
from mutagen.oggvorbis import OggVorbis
from mutagen.oggopus import OggOpus
from mutagen.mp4 import MP4, MP4Cover
from mutagen.id3 import (
	ID3NoHeaderError, ID3,
	APIC, COMM, SYLT, TALB, TCOM, TCON, TCOP, TDRC, TEXT, TIT2, TLEN,
	TPE1, TPE2, TPOS, TPUB, TRCK, TSRC, TXXX, USLT, TYER
)
from deezspot.models.download import Track, Episode
import requests
import logging
import os
import traceback
import time
import unicodedata

logger = logging.getLogger("deezspot.taggers")

# Helper: wait for a file to appear on disk (and preferably become non-empty)
def _wait_for_file(path: str, timeout: float = 3.0, interval: float = 0.1) -> bool:
	end_time = time.time() + timeout
	while time.time() < end_time:
		try:
			if os.path.exists(path):
				try:
					# Consider file ready if size can be read and > 0
					if os.path.getsize(path) > 0:
						return True
				except OSError:
					# If stat fails intermittently, but path exists, allow another cycle
					pass
		except Exception:
			pass
		time.sleep(interval)
	# One last check
	return os.path.exists(path)

# Helper: safe directory snapshot for diagnostics
def _safe_dir_snapshot(path: str, limit: int = 25):
	try:
		dirname = os.path.dirname(path) or "."
		entries = os.listdir(dirname)
		return entries[:limit]
	except Exception as e:
		return [f"<listdir failed: {e}>"]


def request(url):
    response = requests.get(url)
    response.raise_for_status()
    return response

# Helper to safely get image bytes
def _get_image_bytes(image_data_or_url):
	if isinstance(image_data_or_url, bytes):
		return image_data_or_url
	elif isinstance(image_data_or_url, str): # Assuming it's a URL
		try:
			response = requests.get(image_data_or_url, timeout=10)
			response.raise_for_status()
			return response.content
		except requests.RequestException as e:
			logger.warning(f"Failed to download image from URL {image_data_or_url}: {e}")
			return None
	return None

def _format_year_for_id3(year_obj):
	if not year_obj or not hasattr(year_obj, 'year'):
		return None
	return str(year_obj.year)

def _format_date_for_vorbis(year_obj):
	if not year_obj or not hasattr(year_obj, 'strftime'):
		return None
	return year_obj.strftime('%Y-%m-%d')

def _format_date_for_mp4(year_obj):
	if not year_obj or not hasattr(year_obj, 'year'): # MP4 ©day can be just year or full date
		return None
	# For simplicity, just using year, but full date like YYYY-MM-DD is also valid
	return str(year_obj.year) 

# --- MP3 (ID3 Tags) ---
def __write_mp3(filepath, data):
	try:
		tags = ID3(filepath)
	except ID3NoHeaderError:
		tags = ID3()
	tags.delete(filepath, delete_v1=True, delete_v2=True) # Clear existing tags
	tags = ID3() # Re-initialize

	if data.get('music'): tags.add(TIT2(encoding=3, text=str(data['music'])))
	if data.get('artist'): tags.add(TPE1(encoding=3, text=str(data['artist'])))
	if data.get('album'): tags.add(TALB(encoding=3, text=str(data['album'])))
	if data.get('ar_album'): tags.add(TPE2(encoding=3, text=str(data['ar_album']))) # Album Artist

	track_num_str = str(data.get('tracknum', ''))
	tracks_total_str = str(data.get('nb_tracks', ''))
	if track_num_str:
		tags.add(TRCK(encoding=3, text=f"{track_num_str}{f'/{tracks_total_str}' if tracks_total_str else ''}"))

	disc_num_str = str(data.get('discnum', ''))
	discs_total_str = str(data.get('nb_discs', '')) # Assuming 'nb_discs' if available
	if disc_num_str:
		tags.add(TPOS(encoding=3, text=f"{disc_num_str}{f'/{discs_total_str}' if discs_total_str else ''}"))

	if data.get('genre'): tags.add(TCON(encoding=3, text=str(data['genre'])))
	
	year_str = _format_year_for_id3(data.get('year'))
	if year_str: tags.add(TYER(encoding=3, text=year_str))

	comment_text = data.get('comment', 'Downloaded by DeezSpot')
	tags.add(COMM(encoding=3, lang='eng', desc='', text=comment_text))
		
	if data.get('composer'): tags.add(TCOM(encoding=3, text=str(data['composer'])))
	if data.get('copyright'): tags.add(TCOP(encoding=3, text=str(data['copyright'])))
	if data.get('label'): tags.add(TPUB(encoding=3, text=str(data['label']))) # Publisher/Label
	if data.get('isrc'): tags.add(TSRC(encoding=3, text=str(data['isrc'])))
	
	duration_sec = data.get('duration')
	if isinstance(duration_sec, (int, float)) and duration_sec > 0:
		tags.add(TLEN(encoding=3, text=str(int(duration_sec * 1000))))

	if data.get('lyric'): tags.add(USLT(encoding=3, lang='eng', desc='', text=str(data['lyric'])))
	# SYLT for synced lyrics would need specific format for its text field

	img_bytes = _get_image_bytes(data.get('image'))
	if img_bytes:
		tags.add(APIC(encoding=3, mime='image/jpeg', type=3, desc='Cover', data=img_bytes))
	
	if data.get('bpm') and str(data.get('bpm', '')).isdigit(): 
		tags.add(TXXX(encoding=3, desc='BPM', text=str(data['bpm'])))
	if data.get('author'): # Lyricist
		tags.add(TXXX(encoding=3, desc='LYRICIST', text=str(data['author'])))

	tags.save(filepath, v2_version=3)

# --- M4A (AAC/ALAC in MP4 Container) ---
def __write_m4a(filepath, data):
	try:
		mp4 = MP4(filepath)
		tags = mp4.tags
	except Exception as e:
		logger.warning(f"Could not open M4A file {filepath} for tagging, trying to create new: {e}")
		try:
			mp4 = MP4() # Create a new MP4 object if loading fails
			tags = mp4.tags # Get its tags attribute (will be empty or None)
		except Exception as e_create:
			logger.error(f"Failed to initialize MP4 tags for {filepath}: {e_create}")
			return

	# Atom names (ensure they are bytes for mutagen for older versions, strings for newer)
	# Mutagen generally handles this; use strings for keys for clarity.
	TAG_MAP = {
		'music': '\xa9nam', 'artist': '\xa9ART', 'album': '\xa9alb', 'ar_album': 'aART',
		'genre': '\xa9gen', 'composer': '\xa9wrt', 'copyright': 'cprt',
		'comment': '\xa9cmt', 'label': '\xa9pub' # Using a common atom for publisher
	}

	for data_key, atom_key in TAG_MAP.items():
		if data.get(data_key) is not None:
			tags[atom_key] = [str(data[data_key])]
		else:
			if atom_key in tags: del tags[atom_key]

	mp4_date = _format_date_for_mp4(data.get('year'))
	if mp4_date: tags['\xa9day'] = [mp4_date]
	else: 
		if '\xa9day' in tags: del tags['\xa9day']

	track_num = data.get('tracknum')
	tracks_total = data.get('nb_tracks', 0)
	if track_num is not None:
		tags['trkn'] = [[int(track_num), int(tracks_total)]]
	else:
		if 'trkn' in tags: del tags['trkn']

	disc_num = data.get('discnum')
	discs_total = data.get('nb_discs', 0) # Assuming 'nb_discs' if available
	if disc_num is not None:
		tags['disk'] = [[int(disc_num), int(discs_total)]]
	else:
		if 'disk' in tags: del tags['disk']
		
	if data.get('bpm') and str(data.get('bpm','')).isdigit():
		tags['tmpo'] = [int(data['bpm'])]
	elif 'tmpo' in tags: del tags['tmpo']

	if data.get('lyric'):
		tags['\xa9lyr'] = [str(data['lyric'])]
	elif '\xa9lyr' in tags: del tags['\xa9lyr']

	img_bytes = _get_image_bytes(data.get('image'))
	if img_bytes:
		img_format = MP4Cover.FORMAT_JPEG if img_bytes.startswith(b'\xff\xd8') else MP4Cover.FORMAT_PNG
		tags['covr'] = [MP4Cover(img_bytes, imageformat=img_format)]
	elif 'covr' in tags: del tags['covr']
	
	# For ISRC - often stored in a custom way
	if data.get('isrc'):
		tags['----:com.apple.iTunes:ISRC'] = bytes(str(data['isrc']), 'utf-8')
	elif '----:com.apple.iTunes:ISRC' in tags: del tags['----:com.apple.iTunes:ISRC']

	try:
		mp4.save(filepath) # Use the MP4 object's save method
	except Exception as e:
		logger.error(f"Failed to save M4A tags for {filepath}: {e}")

# --- Vorbis Comments (FLAC, OGG, OPUS) ---
def __write_vorbis(filepath, data, audio_format_class):
	# Mitigate races with late renames or slow mounts
	if not _wait_for_file(filepath, timeout=3.0, interval=0.1):
		logger.error(f"Vorbis tagging aborted: file not found after wait: {filepath}")
		return
	try:
		tags = audio_format_class(filepath)
	except Exception as e:
		logger.warning(f"Could not open {filepath} for Vorbis tagging ({audio_format_class.__name__}): {e}")
		# Do not attempt to create a new OGG/Opus/FLAC container via mutagen; abort cleanly.
		return

	tags.delete() # Clear existing tags before adding new ones

	VORBIS_MAP = {
		'music': 'TITLE', 'artist': 'ARTIST', 'album': 'ALBUM', 'ar_album': 'ALBUMARTIST',
		'genre': 'GENRE', 'composer': 'COMPOSER', 'copyright': 'COPYRIGHT',
		'label': 'ORGANIZATION', 'isrc': 'ISRC', 'comment': 'COMMENT',
		'lyric': 'LYRICS', 'author': 'LYRICIST', 'version': 'VERSION'
	}

	for data_key, vorbis_key in VORBIS_MAP.items():
		if data.get(data_key) is not None: tags[vorbis_key] = str(data[data_key])

	vorbis_date = _format_date_for_vorbis(data.get('year'))
	if vorbis_date: tags['DATE'] = vorbis_date

	if data.get('tracknum') is not None: tags['TRACKNUMBER'] = str(data['tracknum'])
	if data.get('nb_tracks') is not None: tags['TRACKTOTAL'] = str(data['nb_tracks'])
	if data.get('discnum') is not None: tags['DISCNUMBER'] = str(data['discnum'])
	if data.get('nb_discs') is not None: tags['DISCTOTAL'] = str(data['nb_discs'])

	if data.get('bpm') and str(data.get('bpm','')).isdigit():
		tags['BPM'] = str(data['bpm'])
	
	duration_sec = data.get('duration')
	if isinstance(duration_sec, (int, float)) and duration_sec > 0:
		tags['LENGTH'] = str(duration_sec) # Store as seconds string

	img_bytes = _get_image_bytes(data.get('image'))
	if img_bytes:
		if audio_format_class == FLAC:
			pic = FLACPicture()
			pic.type = 3
			pic.mime = 'image/jpeg' if img_bytes.startswith(b'\xff\xd8') else 'image/png'
			pic.data = img_bytes
			tags.clear_pictures()
			tags.add_picture(pic)
		elif audio_format_class in [OggVorbis, OggOpus]:
			try:
				# For OGG/Opus, METADATA_BLOCK_PICTURE is a base64 encoded FLAC Picture block
				pic_for_ogg = FLACPicture() # Use FLACPicture structure
				pic_for_ogg.type = 3
				pic_for_ogg.mime = 'image/jpeg' if img_bytes.startswith(b'\xff\xd8') else 'image/png'
				pic_for_ogg.data = img_bytes
				tags['METADATA_BLOCK_PICTURE'] = [b64encode(pic_for_ogg.write()).decode('ascii')]
			except Exception as e_ogg_pic:
				logger.warning(f"Could not prepare/embed cover art for OGG/Opus in {filepath}: {e_ogg_pic}")
	try:
		tags.save()
	except Exception as e:
		logger.error(f"Failed to save Vorbis tags for {filepath} ({audio_format_class.__name__}): {e}")

# --- WAV (ID3 Tags) ---
def __write_wav(filepath, data):
	# WAV files can store ID3 tags. This is more versatile than RIFF INFO.
	__write_mp3(filepath, data) # Reuse MP3/ID3 logic


# --- Main Dispatcher ---
def write_tags(media):
	if isinstance(media, Track):
		filepath = media.song_path
	elif isinstance(media, Episode):
		filepath = getattr(media, 'episode_path', getattr(media, 'song_path', None)) # Episode model might vary
	else:
		logger.error(f"Unsupported media type for tagging: {type(media)}")
		return

	if not filepath:
		logger.error(f"Filepath is missing for tagging media object: {media}")
		return

	song_metadata = getattr(media, 'tags', None)
	if not song_metadata:
		logger.warning(f"No metadata (tags) found for {filepath}. Skipping tagging.")
		return
		
	# Diagnostics: probe existence and normalization
	try:
		dirname = os.path.dirname(filepath)
		basename = os.path.basename(filepath)
		nfc = unicodedata.normalize('NFC', basename)
		nfkc = unicodedata.normalize('NFKC', basename)
		logger.debug(f"Tagging probe: path={repr(filepath)}, exists={os.path.exists(filepath)}, dir={repr(dirname)}")
		logger.debug(f"Tagging probe: basename={repr(basename)}, NFC==basename? {nfc==basename}, NFKC==basename? {nfkc==basename}")
		logger.debug(f"Directory sample: {_safe_dir_snapshot(filepath, limit=25)}")
	except Exception:
		pass

	# If the file isn't visible yet, wait briefly to avoid races
	if not _wait_for_file(filepath, timeout=3.0, interval=0.1):
		logger.error(f"write_tags: File not found after wait, skipping tagging: {filepath}")
		return
		
	file_ext = getattr(media, 'file_format', None) 
	if not file_ext:
		logger.warning(f"File format not specified in media object for {filepath}. Attempting to guess from filepath.")
		_, file_ext = os.path.splitext(filepath)
		if not file_ext:
			logger.error(f"Could not determine file format for {filepath}. Skipping tagging.")
			return

	file_ext = file_ext.lower()
	logger.info(f"Writing tags for: {filepath} (Format: {file_ext})")

	try:
		if file_ext == ".mp3":
			__write_mp3(filepath, song_metadata)
		elif file_ext == ".flac":
			__write_vorbis(filepath, song_metadata, FLAC)
		elif file_ext == ".ogg":
			__write_vorbis(filepath, song_metadata, OggVorbis)
		elif file_ext == ".opus":
			__write_vorbis(filepath, song_metadata, OggOpus)
		elif file_ext == ".m4a": # Handles AAC and ALAC
			__write_m4a(filepath, song_metadata)
		elif file_ext == ".wav":
			__write_wav(filepath, song_metadata)
		else:
			logger.warning(f"Unsupported file format for tagging: {file_ext} for file {filepath}")
	except Exception as e:
		logger.error(f"General error during tagging for {filepath}: {e}")
		logger.debug(traceback.format_exc())

# Placeholder - purpose seems to be for checking if tags were written correctly or file integrity.
# Actual implementation would depend on specific needs.
def check_track(media):
	if isinstance(media, Track):
		filepath = media.song_path
	elif isinstance(media, Episode):
		filepath = getattr(media, 'episode_path', getattr(media, 'song_path', None))
	else:
		logger.warning(f"check_track called with unsupported media type: {type(media)}")
		return False

	if not filepath or not os.path.exists(filepath):
		logger.warning(f"check_track: Filepath missing or file does not exist: {filepath}")
		return False

	try:
		audio = mutagen.File(filepath, easy=True) # Try loading with easy tags
		if audio is None or not audio.tags:
			logger.info(f"check_track: No tags found or file not recognized by mutagen for {filepath}")
			return False
		# Add more specific checks here if needed, e.g., check for a title tag
		if audio.get('title') or audio.get('TIT2') or audio.get('\xa9nam'):
			logger.info(f"check_track: Basic tags appear to be present for {filepath}")
			return True
		else:
			logger.info(f"check_track: Essential tags (like title) seem to be missing in {filepath}")
			return False
	except Exception as e:
		logger.error(f"check_track: Error loading file {filepath} with mutagen: {e}")
		return False