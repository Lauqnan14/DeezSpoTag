#!/usr/bin/python3

import re
from unicodedata import normalize
from os import makedirs
from datetime import datetime
from urllib.parse import urlparse
from requests import get as req_get
from zipfile import ZipFile, ZIP_DEFLATED
from deezspot.models.download.track import Track
from deezspot.exceptions import InvalidLink
from deezspot.libutils.others_settings import supported_link, header
from deezspot.libutils.logging_utils import ProgressReporter, logger

from os.path import (
    isdir, basename,
    join, isfile, dirname
)

def link_is_valid(link):
    netloc = urlparse(link).netloc

    if not any(
        c_link == netloc
        for c_link in supported_link
    ):
        raise InvalidLink(link)

def get_ids(link):
    parsed = urlparse(link)
    path = parsed.path
    ids = path.split("/")[-1]

    return ids

def request(url):
    thing = req_get(url, headers=header)
    return thing

def __check_dir(directory):
    if not isdir(directory):
        makedirs(directory)

def sanitize_name(string, max_length=200):
    """Sanitize a string for use as a filename or directory name.
    
    This version maps filesystem-conflicting ASCII characters to Unicode
    lookalikes (mostly fullwidth forms) rather than dropping or replacing
    with ASCII fallbacks. This preserves readability while avoiding
    path-separator or Windows-invalid characters.
    
    Args:
        string: The string to sanitize
        max_length: Maximum length for the resulting string
        
    Returns:
        A sanitized string safe for use in file paths
    """
    if string is None:
        return "Unknown"
        
    # Convert to string if not already
    string = str(string)

    # Map invalid/reserved characters to Unicode fullwidth or similar lookalikes
    # to avoid filesystem conflicts while keeping readability.
    # Windows-invalid: < > : " / \ | ? * and control chars
    replacements = {
        "\\": "＼",  # U+FF3C FULLWIDTH REVERSE SOLIDUS
        "/": "／",   # U+FF0F FULLWIDTH SOLIDUS
        ":": "：",   # U+FF1A FULLWIDTH COLON
        "*": "＊",   # U+FF0A FULLWIDTH ASTERISK
        "?": "？",   # U+FF1F FULLWIDTH QUESTION MARK
        "\"": "＂",  # U+FF02 FULLWIDTH QUOTATION MARK
        "<": "＜",   # U+FF1C FULLWIDTH LESS-THAN SIGN
        ">": "＞",   # U+FF1E FULLWIDTH GREATER-THAN SIGN
        "|": "｜",   # U+FF5C FULLWIDTH VERTICAL LINE
        "&": "＆",   # U+FF06 FULLWIDTH AMPERSAND
        "$": "＄",   # U+FF04 FULLWIDTH DOLLAR SIGN
        ";": "；",   # U+FF1B FULLWIDTH SEMICOLON
        "\t": " ",  # Tab to space
        "\n": " ",  # Newline to space
        "\r": " ",  # Carriage return to space
        "\0": "",   # Null byte removed
    }
    
    for old, new in replacements.items():
        string = string.replace(old, new)

    # Remove any other non-printable characters
    string = ''.join(char for char in string if char.isprintable())

    # Remove leading/trailing whitespace
    string = string.strip()

    # Replace multiple spaces with a single space
    string = re.sub(r'\s+', ' ', string)

    # Truncate if too long
    if len(string) > max_length:
        string = string[:max_length]
        
    # Ensure we don't end with a dot or space (can cause issues in some filesystems)
    string = string.rstrip('. ')

    # Provide a fallback for empty strings
    if not string:
        string = "Unknown"

    # Normalize to NFC to keep composed characters stable but avoid
    # compatibility decomposition that might revert fullwidth mappings.
    string = normalize('NFC', string)
        
    return string

# Keep the original function name for backward compatibility
def var_excape(string):
    """Legacy function name for backward compatibility."""
    return sanitize_name(string)

def convert_to_date(date: str):
    if date == "0000-00-00":
        date = "0001-01-01"
    elif date.isdigit():
        date = f"{date}-01-01"
    date = datetime.strptime(date, "%Y-%m-%d")
    return date

def what_kind(link):
    url = request(link).url
    if url.endswith("/"):
        url = url[:-1]
    return url

def __get_tronc(string):
    return string[:len(string) - 1]

def apply_custom_format(format_str, metadata: dict, pad_tracks=True, pad_number_width: int | None = None) -> str:
    def replacer(match):
        full_key = match.group(1)  # e.g., "artist", "ar_album_1"

        # Allow custom artist/album-artist separator to be provided via metadata
        separator = metadata.get('artist_separator', ';')
        if not isinstance(separator, str) or separator == "":
            separator = ';'

        # Check for specific indexed placeholders: artist_INDEX or ar_album_INDEX
        # Allows %artist_1%, %ar_album_1%, etc.
        indexed_artist_match = re.fullmatch(r'(artist|ar_album)_(\d+)', full_key)

        if indexed_artist_match:
            base_key = indexed_artist_match.group(1)  # "artist" or "ar_album"
            try:
                index = int(indexed_artist_match.group(2))
            except ValueError: # Should not happen with \d+ but good practice
                return ""


            raw_value = metadata.get(base_key)  # Get the value of "artist" or "ar_album"
            items = []

            if isinstance(raw_value, str):
                # Split by provided separator and strip whitespace
                items = [item.strip() for item in raw_value.split(separator) if item.strip()]
            elif isinstance(raw_value, list):
                # Convert all items to string, strip whitespace
                items = [str(item).strip() for item in raw_value if str(item).strip()]
            # If raw_value is not string or list, items remains []

            if items:  # If we have a list of artists/ar_album
                if 1 <= index <= len(items):
                    return items[index - 1]
                elif items:  # Index out of bounds, but list is not empty
                    return items[0]  # Fallback to the first item
                # If items is empty after processing, fall through
            
            # Fallback if no items or base_key was not found or not list/string
            return ""

        else:
            # Original non-indexed placeholder logic (for %album%, %title%, %artist%, %ar_album%, etc.)
            value = metadata.get(full_key, '')

            # Handle None values safely
            if value is None:
                if full_key in ['tracknum', 'discnum', 'playlistnum']:
                    value = '1' if full_key == 'discnum' else '0'
                else:
                    value = ''

            if full_key == 'year' and value:
                if isinstance(value, datetime):
                    return str(value.year)
                # Fallback for string-based dates like "YYYY-MM-DD" or just "YYYY"
                return str(value).split('-')[0]

            if pad_tracks and full_key in ['tracknum', 'discnum', 'playlistnum']:
                str_value = str(value)
                if str_value.isdigit():
                    if isinstance(pad_number_width, int) and pad_number_width >= 1:
                        return str_value.zfill(pad_number_width)
                    # Default legacy behavior: pad single digits to width 2
                    if len(str_value) == 1:
                        return str_value.zfill(2)
                return str_value
            return str(value)

    return re.sub(r'%([^%]+)%', replacer, format_str)

def __get_dir(song_metadata, output_dir, custom_dir_format=None, pad_tracks=True, pad_number_width: int | None = None):
    # If custom_dir_format is explicitly empty or None, use output_dir directly
    if not custom_dir_format:
        # Ensure output_dir itself exists, as __check_dir won't be called on a subpath
        __check_dir(output_dir)
        return output_dir

    # Apply formatting per path component so only slashes from the format
    # create directories; slashes from data are sanitized inside components.
    format_parts = custom_dir_format.split("/")
    formatted_parts = [
        apply_custom_format(part, song_metadata, pad_tracks, pad_number_width) for part in format_parts
    ]
    sanitized_path_segment = "/".join(
        sanitize_name(part) for part in formatted_parts
    )

    # Join with the base output directory
    path = join(output_dir, sanitized_path_segment)
    
    # __check_dir will create the directory if it doesn't exist.
    __check_dir(path)
    return path

def set_path(
    song_metadata, output_dir,
    song_quality, file_format,
    is_episode=False,
    custom_dir_format=None,
    custom_track_format=None,
    pad_tracks=True,
    pad_number_width: int | None = None
):
    # Determine the directory for the song
    directory = __get_dir(
        song_metadata,
        output_dir,
        custom_dir_format=custom_dir_format,
        pad_tracks=pad_tracks,
        pad_number_width=pad_number_width
    )

    # Determine the filename for the song
    # Default track format if no custom one is provided
    if custom_track_format is None:
        if is_episode:
            # Default for episodes: %music%
            custom_track_format = "%music%"
        else:
            # Default for tracks: %artist% - %music%
            custom_track_format = "%artist% - %music%"
    
    # Prepare metadata for formatting, including quality if available
    effective_metadata = dict(song_metadata) # Create a mutable copy
    if song_quality:
        effective_metadata['quality'] = f"[{song_quality}]"
    # else: if song_quality is None or empty, 'quality' won't be in effective_metadata,
    # so %quality% placeholder will be replaced by an empty string by apply_custom_format.

    # Apply the custom format string for the track filename.
    # pad_tracks is passed along for track/disc numbers in filename.
    track_filename_base = apply_custom_format(custom_track_format, effective_metadata, pad_tracks, pad_number_width)
    track_filename_base = sanitize_name(track_filename_base)

    # Add file format (extension) to the filename
    if file_format:
        ext = file_format if file_format.startswith('.') else f".{file_format}"
        filename = f"{track_filename_base}{ext}"
    else: # No file_format provided (should not happen for standard audio, but handle defensively)
        filename = track_filename_base

    return join(directory, filename)

def create_zip(
    tracks: list[Track],
    output_dir=None,
    song_metadata=None, # Album/Playlist level metadata
    song_quality=None, # Overall quality for the zip, if applicable
    zip_name=None, # Specific name for the zip file
    custom_dir_format=None # To determine zip name if not provided, and for paths inside zip
):
    # Determine the zip file name and path
    if zip_name:
        # If zip_name is a full path, use it as is.
        # Otherwise, prepend output_dir.
        if not basename(zip_name) == zip_name: # Checks if it's just a filename
            actual_zip_path = zip_name
        else:
            # Ensure output_dir exists for placing the zip file
            if not output_dir:
                # Fallback to a default if output_dir is not provided with a relative zip_name
                output_dir = "."
                __check_dir(output_dir)
            actual_zip_path = join(output_dir, zip_name)
    elif song_metadata and output_dir: # Construct default name if song_metadata and output_dir exist
        # Use album/playlist name and quality for default zip name
        # Sanitize the album/playlist name part of the zip file
        name_part = sanitize_name(song_metadata.get('album', song_metadata.get('name', 'archive')))
        quality_part = f" [{song_quality}]" if song_quality else ""
        actual_zip_path = join(output_dir, f"{name_part}{quality_part}.zip")
    else:
        # Fallback zip name if not enough info
        actual_zip_path = join(output_dir if output_dir else ".", "archive.zip")

    # Ensure the directory for the zip file exists
    zip_dir = dirname(actual_zip_path)
    __check_dir(zip_dir)

    with ZipFile(actual_zip_path, 'w', ZIP_DEFLATED) as zf:
        for track in tracks:
            if track.success and isfile(track.song_path):
                # Determine path inside the zip
                # This uses the same logic as saving individual files,
                # but relative to the zip root.
                # We pass an empty string as base_output_dir to set_path essentially,
                # so it generates a relative path structure.
                path_in_zip = set_path(
                    track.tags, # Use individual track metadata for path inside zip
                    "",         # Base output dir (empty for relative paths in zip)
                    track.quality,
                    track.file_format,
                    custom_dir_format=custom_dir_format, # Use album/playlist custom dir format
                    custom_track_format=track.tags.get('custom_track_format'), # Use track specific if available
                    pad_tracks=track.tags.get('pad_tracks', True)
                )
                # Remove leading slash if any, to ensure it's relative inside zip
                path_in_zip = path_in_zip.lstrip('/').lstrip('\\')
                
                zf.write(track.song_path, arcname=path_in_zip)
    return actual_zip_path

def trasform_sync_lyric(lyric):
    sync_array = []
    for a in lyric:
        if "milliseconds" in a:
            arr = (a['line'], int(a['milliseconds']))
            sync_array.append(arr)
    return sync_array

def save_cover_image(image_data: bytes, directory_path: str, cover_filename: str = "cover.jpg"):
    if not image_data:
        logger.warning(f"No image data provided to save cover in {directory_path}.")
        return

    if not isdir(directory_path):
        # This case should ideally be handled by prior directory creation (e.g., __get_dir)
        # but as a fallback, we can try to create it or log a warning.
        logger.warning(f"Directory {directory_path} does not exist. Attempting to create it for cover image.")
        try:
            makedirs(directory_path, exist_ok=True)
            logger.info(f"Created directory {directory_path} for cover image.")
        except OSError as e:
            logger.error(f"Failed to create directory {directory_path} for cover: {e}")
            return

    cover_path = join(directory_path, cover_filename)
    try:
        with open(cover_path, "wb") as f:
            f.write(image_data)
        logger.info(f"Successfully saved cover image to {cover_path}")
    except OSError as e:
        logger.error(f"Failed to save cover image to {cover_path}: {e}")
