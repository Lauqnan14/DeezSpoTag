#!/usr/bin/python3

import os
from mutagen import File
from mutagen.easyid3 import EasyID3
from mutagen.oggvorbis import OggVorbis
from mutagen.oggopus import OggOpus
from mutagen.flac import FLAC
from mutagen.mp3 import MP3 # Added for explicit MP3 type checking

# AUDIO_FORMATS and get_output_path will be imported from audio_converter
# We need to ensure this doesn't create circular dependencies.
# If audio_converter also imports something from libutils that might import this,
# it could be an issue. For now, proceeding with direct import.
from deezspot.libutils.audio_converter import AUDIO_FORMATS, get_output_path

# Logger instance will be passed as an argument to functions that need it.

def _metadata_matches(file_path, title, album, logger):
    existing_title, existing_album = read_metadata_from_file(file_path, logger)
    return existing_title == title and existing_album == album


def _read_mp3_metadata(audio, file_path, logger):
    title = None
    album = None
    if not audio.tags:
        logger.debug(f"No tags found in MP3 file: {file_path}")
        return title, album
    title_frame = audio.tags.get('TIT2')
    if title_frame:
        title = title_frame.text[0] if title_frame.text else None
    album_frame = audio.tags.get('TALB')
    if album_frame:
        album = album_frame.text[0] if album_frame.text else None
    return title, album


def _read_known_format_metadata(audio, file_path):
    if isinstance(audio, EasyID3):
        return audio.get('title', [None])[0], audio.get('album', [None])[0]
    if isinstance(audio, (OggVorbis, OggOpus, FLAC)):
        return audio.get('TITLE', [None])[0], audio.get('ALBUM', [None])[0]
    if file_path.lower().endswith('.m4a'):
        return audio.get('\xa9nam', [None])[0], audio.get('\xa9alb', [None])[0]
    return None, None


def read_metadata_from_file(file_path, logger):
    """Reads title and album metadata from an audio file."""
    try:
        if not os.path.isfile(file_path):
            logger.debug(f"File not found for metadata reading: {file_path}")
            return None, None
        
        audio = File(file_path, easy=False) # easy=False to access format-specific tags better
        if audio is None:
            logger.warning(f"Could not load audio file with mutagen: {file_path}")
            return None, None
        if isinstance(audio, MP3):
            return _read_mp3_metadata(audio, file_path, logger)

        title, album = _read_known_format_metadata(audio, file_path)
        if title is None and album is None and not isinstance(
            audio, (EasyID3, OggVorbis, OggOpus, FLAC)
        ) and not file_path.lower().endswith('.m4a'):
            logger.warning(f"Unsupported file type for metadata extraction by read_metadata_from_file: {file_path} (type: {type(audio)})")
            return None, None

        return title, album

    except Exception as e:
        logger.error(f"Error reading metadata from {file_path}: {str(e)}")
        return None, None


def _scan_for_matching_extension(scan_dir, extension, title, album, logger, skip_path=None):
    for file_in_dir in os.listdir(scan_dir):
        if not file_in_dir.lower().endswith(extension):
            continue
        file_path_to_check = os.path.join(scan_dir, file_in_dir)
        if skip_path and file_path_to_check == skip_path and os.path.exists(skip_path):
            continue
        if _metadata_matches(file_path_to_check, title, album, logger):
            return file_path_to_check
    return None


def _check_converted_target(original_song_path, title, album, convert_to, logger):
    if not convert_to:
        return None
    target_format_upper = convert_to.upper()
    if target_format_upper not in AUDIO_FORMATS:
        logger.warning(f"Invalid convert_to format: '{convert_to}'. Checking for original/general format.")
        return None

    final_expected_converted_path = get_output_path(original_song_path, target_format_upper)
    final_target_ext = AUDIO_FORMATS[target_format_upper]["extension"].lower()

    if os.path.exists(final_expected_converted_path) and _metadata_matches(
        final_expected_converted_path, title, album, logger
    ):
        logger.info(
            f"Found existing track (exact converted path match): {title} - {album} at {final_expected_converted_path}"
        )
        return final_expected_converted_path

    converted_match = _scan_for_matching_extension(
        scan_dir=os.path.dirname(original_song_path),
        extension=final_target_ext,
        title=title,
        album=album,
        logger=logger,
        skip_path=final_expected_converted_path,
    )
    if converted_match:
        logger.info(
            f"Found existing track (converted extension scan): {title} - {album} at {converted_match}"
        )
        return converted_match

    # conversion requested and format valid: only that target format should count as existing
    return False


def _scan_any_known_audio(scan_dir, original_song_path, original_ext_lower, title, album, logger):
    known_extensions = {
        fmt_details["extension"].lower() for fmt_details in AUDIO_FORMATS.values()
    }
    for file_in_dir in os.listdir(scan_dir):
        file_lower = file_in_dir.lower()
        if not any(file_lower.endswith(ext) for ext in known_extensions):
            continue
        file_path_to_check = os.path.join(scan_dir, file_in_dir)
        if os.path.exists(original_song_path) and file_path_to_check == original_song_path:
            continue
        if file_lower.endswith(original_ext_lower):
            # Already covered by dedicated original-extension scan.
            pass
        if _metadata_matches(file_path_to_check, title, album, logger):
            logger.info(
                f"Found existing track (general audio format scan): {title} - {album} at {file_path_to_check}"
            )
            return file_path_to_check
    return None


def check_track_exists(original_song_path, title, album, convert_to, logger):
    """Checks if a track exists, considering original and target converted formats.

    Args:
        original_song_path (str): The expected path for the song in its original download format.
        title (str): The title of the track to check.
        album (str): The album of the track to check.
        convert_to (str | None): The target format for conversion (e.g., 'MP3', 'FLAC'), or None.
        logger (logging.Logger): Logger instance.

    Returns:
        tuple[bool, str | None]: (True, path_to_existing_file) if exists, else (False, None).
    """
    scan_dir = os.path.dirname(original_song_path)

    if not os.path.exists(scan_dir):
        logger.debug(f"Scan directory {scan_dir} does not exist. Track cannot exist.")
        return False, None

    converted_match = _check_converted_target(
        original_song_path, title, album, convert_to, logger
    )
    if converted_match is False:
        return False, None
    if converted_match:
        return True, converted_match

    # Priority 2: Check if the file exists in its original download format
    original_ext_lower = os.path.splitext(original_song_path)[1].lower()

    if os.path.exists(original_song_path) and _metadata_matches(
        original_song_path, title, album, logger
    ):
        logger.info(
            f"Found existing track (exact original path match): {title} - {album} at {original_song_path}"
        )
        return True, original_song_path

    original_ext_match = _scan_for_matching_extension(
        scan_dir=scan_dir,
        extension=original_ext_lower,
        title=title,
        album=album,
        logger=logger,
        skip_path=original_song_path,
    )
    if original_ext_match:
        logger.info(
            f"Found existing track (original extension scan): {title} - {album} at {original_ext_match}"
        )
        return True, original_ext_match
    
    # Priority 3: General scan for any known audio format if no conversion was specified OR if convert_to was invalid
    # This part only runs if convert_to is None or was an invalid format string.
    if not convert_to or (convert_to and convert_to.upper() not in AUDIO_FORMATS):
        general_match = _scan_any_known_audio(
            scan_dir, original_song_path, original_ext_lower, title, album, logger
        )
        if general_match:
            return True, general_match
                    
    return False, None 
