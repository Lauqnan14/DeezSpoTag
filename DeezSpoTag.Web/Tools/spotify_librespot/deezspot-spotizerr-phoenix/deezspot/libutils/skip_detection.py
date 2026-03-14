#!/usr/bin/python3

import os
from mutagen import File
from mutagen.easyid3 import EasyID3
from mutagen.oggvorbis import OggVorbis
from mutagen.oggopus import OggOpus
from mutagen.flac import FLAC
from mutagen.mp3 import MP3 # Added for explicit MP3 type checking
# from mutagen.mp4 import MP4 # MP4 is usually handled by File for .m4a

# AUDIO_FORMATS and get_output_path will be imported from audio_converter
# We need to ensure this doesn't create circular dependencies.
# If audio_converter also imports something from libutils that might import this,
# it could be an issue. For now, proceeding with direct import.
from deezspot.libutils.audio_converter import AUDIO_FORMATS, get_output_path

# Logger instance will be passed as an argument to functions that need it.

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

        title = None
        album = None

        if isinstance(audio, EasyID3): # This might occur if easy=True was used, but we use easy=False
            # This branch is less likely to be hit with current File(..., easy=False) usage for MP3s
            title = audio.get('title', [None])[0]
            album = audio.get('album', [None])[0]
        elif isinstance(audio, MP3): # Correctly handle MP3 objects when easy=False
            # For mutagen.mp3.MP3, tags are typically accessed via audio.tags (an ID3 object)
            # Common ID3 frames for title and album are TIT2 and TALB respectively.
            # The .text attribute of a frame object usually holds a list of strings.
            if audio.tags:
                title_frame = audio.tags.get('TIT2')
                if title_frame:
                    title = title_frame.text[0] if title_frame.text else None
                
                album_frame = audio.tags.get('TALB')
                if album_frame:
                    album = album_frame.text[0] if album_frame.text else None
            else:
                logger.debug(f"No tags found in MP3 file: {file_path}")
        elif isinstance(audio, OggVorbis): # OGG
            title = audio.get('TITLE', [None])[0] # Vorbis tags are case-insensitive but typically uppercase
            album = audio.get('ALBUM', [None])[0]
        elif isinstance(audio, OggOpus): # OPUS
            title = audio.get('TITLE', [None])[0] # Opus files use Vorbis comments, similar to OGG
            album = audio.get('ALBUM', [None])[0]
        elif isinstance(audio, FLAC): # FLAC
            title = audio.get('TITLE', [None])[0]
            album = audio.get('ALBUM', [None])[0]
        elif file_path.lower().endswith('.m4a'): # M4A (AAC/ALAC)
            # Mutagen's File(filepath) for .m4a returns an MP4 object
            title = audio.get('\xa9nam', [None])[0] # iTunes title tag
            album = audio.get('\xa9alb', [None])[0] # iTunes album tag
        else:
            logger.warning(f"Unsupported file type for metadata extraction by read_metadata_from_file: {file_path} (type: {type(audio)})")
            return None, None
            
        return title, album

    except Exception as e:
        logger.error(f"Error reading metadata from {file_path}: {str(e)}")
        return None, None

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

    # Priority 1: Check if the file exists in the target converted format
    if convert_to:
        target_format_upper = convert_to.upper()
        if target_format_upper in AUDIO_FORMATS:
            final_expected_converted_path = get_output_path(original_song_path, target_format_upper)
            final_target_ext = AUDIO_FORMATS[target_format_upper]["extension"].lower()

            # Check exact predicted path for converted file
            if os.path.exists(final_expected_converted_path):
                existing_title, existing_album = read_metadata_from_file(final_expected_converted_path, logger)
                if existing_title == title and existing_album == album:
                    logger.info(f"Found existing track (exact converted path match): {title} - {album} at {final_expected_converted_path}")
                    return True, final_expected_converted_path
            
            # Scan directory for other files with the target extension
            for file_in_dir in os.listdir(scan_dir):
                if file_in_dir.lower().endswith(final_target_ext):
                    file_path_to_check = os.path.join(scan_dir, file_in_dir)
                    # Skip if it's the same as the one we just checked (and it matched or didn't exist)
                    if file_path_to_check == final_expected_converted_path and os.path.exists(final_expected_converted_path):
                        continue 
                    existing_title, existing_album = read_metadata_from_file(file_path_to_check, logger)
                    if existing_title == title and existing_album == album:
                        logger.info(f"Found existing track (converted extension scan): {title} - {album} at {file_path_to_check}")
                        return True, file_path_to_check
            
            # If conversion is specified, and we didn't find the converted file, we should not report other formats as existing.
            # The intention is to get the file in the `convert_to` format.
            return False, None 
        else:
            logger.warning(f"Invalid convert_to format: '{convert_to}'. Checking for original/general format.")
            # Fall through to check original/general if convert_to was invalid

    # Priority 2: Check if the file exists in its original download format
    original_ext_lower = os.path.splitext(original_song_path)[1].lower()

    if os.path.exists(original_song_path):
        existing_title, existing_album = read_metadata_from_file(original_song_path, logger)
        if existing_title == title and existing_album == album:
            logger.info(f"Found existing track (exact original path match): {title} - {album} at {original_song_path}")
            return True, original_song_path

    # Scan directory for other files with the original extension (if no conversion target)
    for file_in_dir in os.listdir(scan_dir):
        if file_in_dir.lower().endswith(original_ext_lower):
            file_path_to_check = os.path.join(scan_dir, file_in_dir)
            if file_path_to_check == original_song_path: # Already checked this one
                continue
            existing_title, existing_album = read_metadata_from_file(file_path_to_check, logger)
            if existing_title == title and existing_album == album:
                logger.info(f"Found existing track (original extension scan): {title} - {album} at {file_path_to_check}")
                return True, file_path_to_check
    
    # Priority 3: General scan for any known audio format if no conversion was specified OR if convert_to was invalid
    # This part only runs if convert_to is None or was an invalid format string.
    if not convert_to or (convert_to and convert_to.upper() not in AUDIO_FORMATS):
        for file_in_dir in os.listdir(scan_dir):
            file_lower = file_in_dir.lower()
            # Check against all known audio format extensions
            is_known_audio_format = False
            for fmt_details in AUDIO_FORMATS.values():
                if file_lower.endswith(fmt_details["extension"].lower()):
                    is_known_audio_format = True
                    break
            
            if is_known_audio_format:
                # Skip if it's the original extension and we've already scanned for those
                if file_lower.endswith(original_ext_lower):
                    # We've already checked exact original_song_path and scanned for original_ext_lower
                    # so this specific file would have been caught unless it's the original_song_path itself,
                    # or another file with original_ext_lower that didn't match metadata.
                    # This avoids re-checking files already covered by Priority 2 logic more explicitly.
                    pass # Let it proceed to metadata check if it wasn't an exact match path-wise

                file_path_to_check = os.path.join(scan_dir, file_in_dir)
                # Avoid re-checking original_song_path if it exists, it was covered by Priority 2's exact match.
                if os.path.exists(original_song_path) and file_path_to_check == original_song_path:
                    continue
                
                existing_title, existing_album = read_metadata_from_file(file_path_to_check, logger)
                if existing_title == title and existing_album == album:
                    logger.info(f"Found existing track (general audio format scan): {title} - {album} at {file_path_to_check}")
                    return True, file_path_to_check
                    
    return False, None 