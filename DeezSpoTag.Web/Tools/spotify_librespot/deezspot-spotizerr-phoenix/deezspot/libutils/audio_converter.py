#!/usr/bin/python3

import os
import re
import subprocess
import logging
from os.path import exists, basename, dirname
from shutil import which

logger = logging.getLogger("deezspot")

# Define available audio formats and their properties
AUDIO_FORMATS = {
    "MP3": {
        "extension": ".mp3",
        "mime": "audio/mpeg",
        "ffmpeg_codec": "libmp3lame",
        "ffmpeg_format_flag": "mp3",
        "default_bitrate": "320k",
        "bitrates": ["32k", "64k", "96k", "128k", "192k", "256k", "320k"],
    },
    "AAC": {
        "extension": ".m4a",
        "mime": "audio/mp4",
        "ffmpeg_codec": "aac",
        "ffmpeg_format_flag": "ipod",
        "default_bitrate": "256k",
        "bitrates": ["32k", "64k", "96k", "128k", "192k", "256k"],
    },
    "OGG": {
        "extension": ".ogg",
        "mime": "audio/ogg",
        "ffmpeg_codec": "libvorbis",
        "ffmpeg_format_flag": "ogg",
        "default_bitrate": "256k",
        "bitrates": ["64k", "96k", "128k", "192k", "256k", "320k"],
    },
    "OPUS": {
        "extension": ".opus",
        "mime": "audio/opus",
        "ffmpeg_codec": "libopus",
        "ffmpeg_format_flag": "opus",
        "default_bitrate": "128k",
        "bitrates": ["32k", "64k", "96k", "128k", "192k", "256k"],
    },
    "FLAC": {
        "extension": ".flac",
        "mime": "audio/flac",
        "ffmpeg_codec": "flac",
        "ffmpeg_format_flag": "flac",
        "default_bitrate": None,  # Lossless, no bitrate needed
        "bitrates": [],
    },
    "WAV": {
        "extension": ".wav",
        "mime": "audio/wav",
        "ffmpeg_codec": "pcm_s16le",
        "ffmpeg_format_flag": "wav",
        "default_bitrate": None,  # Lossless, no bitrate needed
        "bitrates": [],
    },
    "ALAC": {
        "extension": ".m4a",
        "mime": "audio/mp4",
        "ffmpeg_codec": "alac",
        "ffmpeg_format_flag": "ipod",
        "default_bitrate": None,  # Lossless, no bitrate needed
        "bitrates": [],
    }
}


def get_output_path(input_path, format_name):
    """Get the output path with the new extension based on the format."""
    if not format_name or format_name not in AUDIO_FORMATS:
        return input_path
        
    dir_name = dirname(input_path)
    file_name = basename(input_path)
    
    # Find the position of the last period to replace extension
    dot_pos = file_name.rfind('.')
    if dot_pos > 0:
        new_file_name = file_name[:dot_pos] + AUDIO_FORMATS[format_name]["extension"]
    else:
        new_file_name = file_name + AUDIO_FORMATS[format_name]["extension"]
        
    return os.path.join(dir_name, new_file_name)


def register_active_download(path):
    """
    Register a file as being actively downloaded.
    This is a placeholder that both modules implement, so we declare it here
    to maintain the interface.
    """
    # This function is expected to be overridden by the module
    pass

def unregister_active_download(path):
    """
    Unregister a file from the active downloads list.
    This is a placeholder that both modules implement, so we declare it here
    to maintain the interface.
    """
    # This function is expected to be overridden by the module
    pass

def _configure_active_download_hooks(register_func=None, unregister_func=None):
    if register_func:
        global register_active_download
        register_active_download = register_func
    if unregister_func:
        global unregister_active_download
        unregister_active_download = unregister_func


def _resolve_format_details(format_name):
    if not format_name:
        return None, None
    format_name_upper = format_name.upper()
    if format_name_upper not in AUDIO_FORMATS:
        logger.warning(f"Unknown format: {format_name}. Using original format.")
        return None, None
    return format_name_upper, AUDIO_FORMATS[format_name_upper]


def _resolve_effective_bitrate(format_name_upper, format_details, bitrate):
    effective_bitrate = bitrate
    if format_details["default_bitrate"] is not None:
        if not effective_bitrate:
            return format_details["default_bitrate"]
        valid_bitrates = [b.lower() for b in format_details["bitrates"]]
        if effective_bitrate.lower() in valid_bitrates:
            return effective_bitrate
        logger.warning(
            f"Invalid bitrate {effective_bitrate} for {format_name_upper}. "
            f"Using default {format_details['default_bitrate']}."
        )
        return format_details["default_bitrate"]
    if effective_bitrate:
        logger.warning(f"Bitrate specified for lossless format {format_name_upper}. Ignoring bitrate.")
    return None


def _should_skip_conversion(input_path, format_name_upper, format_details, effective_bitrate):
    if not input_path.lower().endswith(format_details["extension"].lower()):
        return False
    if format_details["default_bitrate"] is None:
        logger.info(
            f"File {input_path} is already in {format_name_upper} (lossless) format. Skipping conversion."
        )
        return True
    if not effective_bitrate:
        logger.info(
            f"File {input_path} is already in {format_name_upper} format with a suitable bitrate. Skipping conversion."
        )
        return True
    return False


def _build_ffmpeg_command(ffmpeg_path, input_path, format_name_upper, format_details, effective_bitrate, temp_output):
    cmd = [ffmpeg_path, "-y", "-hide_banner", "-loglevel", "error", "-i", input_path]
    if effective_bitrate and format_details["bitrates"]:
        cmd.extend(["-b:a", effective_bitrate])
    cmd.extend(["-c:a", format_details["ffmpeg_codec"]])
    if "ffmpeg_format_flag" in format_details:
        cmd.extend(["-f", format_details["ffmpeg_format_flag"]])
    if format_name_upper == "MP3":
        if not effective_bitrate or int(effective_bitrate.replace('k', '')) >= 256:
            cmd.extend(["-q:a", "0"])
    cmd.append(temp_output)
    return cmd


def _cleanup_temp_file(temp_output):
    if exists(temp_output):
        try:
            os.remove(temp_output)
        except Exception:
            pass
        unregister_active_download(temp_output)


def _finalize_conversion(input_path, output_path, temp_output):
    register_active_download(output_path)
    os.rename(temp_output, output_path)
    unregister_active_download(temp_output)
    if exists(output_path) and input_path != output_path and exists(input_path):
        os.remove(input_path)
        unregister_active_download(input_path)


def convert_audio(input_path, format_name=None, bitrate=None, register_func=None, unregister_func=None):
    """
    Convert audio file to the specified format and bitrate.
    
    Args:
        input_path: Path to the input audio file
        format_name: Target format name (e.g., 'MP3', 'OGG', 'FLAC')
        bitrate: Target bitrate (e.g., '320k', '128k'). If None, uses default for lossy formats.
        register_func: Function to register a file as being actively downloaded
        unregister_func: Function to unregister a file from the active downloads list
        
    Returns:
        Path to the converted file, or the original path if no conversion was done
    """
    _configure_active_download_hooks(register_func, unregister_func)

    # Resolve ffmpeg path explicitly (distroless-safe)
    ffmpeg_path = which("ffmpeg") or "/usr/local/bin/ffmpeg"
    if not os.path.exists(ffmpeg_path):
        logger.error(f"FFmpeg is not available (looked for '{ffmpeg_path}'). Audio conversion is unavailable.")
        return input_path

    format_name_upper, format_details = _resolve_format_details(format_name)
    if not format_name_upper or not format_details:
        return input_path

    effective_bitrate = _resolve_effective_bitrate(format_name_upper, format_details, bitrate)
    if _should_skip_conversion(input_path, format_name_upper, format_details, effective_bitrate):
        return input_path

    output_path = get_output_path(input_path, format_name_upper)
    temp_output = output_path + ".tmp"
    register_active_download(temp_output)
    
    try:
        cmd = _build_ffmpeg_command(
            ffmpeg_path, input_path, format_name_upper, format_details, effective_bitrate, temp_output
        )
        logger.info(f"Converting {input_path} to {format_name_upper}" + (f" at {effective_bitrate}" if effective_bitrate else ""))
        process = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        
        if process.returncode != 0:
            logger.error(f"Audio conversion failed: {process.stderr}")
            _cleanup_temp_file(temp_output)
            return input_path

        _finalize_conversion(input_path, output_path, temp_output)
        
        logger.info(f"Successfully converted to {format_name_upper}" + (f" at {effective_bitrate}" if effective_bitrate else ""))
        return output_path
        
    except FileNotFoundError:
        logger.error(f"FFmpeg executable not found at '{ffmpeg_path}'. Conversion aborted.")
        _cleanup_temp_file(temp_output)
        return input_path
    except Exception as e:
        logger.error(f"Error during audio conversion: {str(e)}")
        _cleanup_temp_file(temp_output)
        return input_path
