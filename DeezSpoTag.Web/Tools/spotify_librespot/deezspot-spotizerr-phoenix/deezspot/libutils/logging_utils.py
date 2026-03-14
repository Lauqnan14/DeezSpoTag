#!/usr/bin/python3

import logging
import sys
from typing import Optional, Callable, Dict, Any, Union
import json
from dataclasses import asdict

from deezspot.models.callback.callbacks import (
    BaseStatusObject, 
    initializingObject, 
    skippedObject, 
    retryingObject, 
    realTimeObject, 
    errorObject, 
    doneObject,
    summaryObject,
    failedTrackObject,
    trackCallbackObject, 
    albumCallbackObject, 
    playlistCallbackObject
)
from deezspot.models.callback.track import trackObject, albumTrackObject, playlistTrackObject, artistTrackObject
from deezspot.models.callback.album import albumObject
from deezspot.models.callback.playlist import playlistObject
from deezspot.models.callback.user import userObject

# Create the main library logger
logger = logging.getLogger('deezspot')

def configure_logger(
    level: int = logging.INFO,
    to_file: Optional[str] = None,
    to_console: bool = True,
    format_string: str = '%(asctime)s - %(name)s - %(levelname)s - %(message)s'
) -> None:
    """
    Configure the deezspot logger with the specified settings.

    Args:
        level: Logging level (e.g., logging.INFO, logging.DEBUG)
        to_file: Optional file path to write logs
        to_console: Whether to output logs to console
        format_string: Log message format
    """
    # Clear existing handlers to avoid duplicates
    logger.handlers = []
    logger.setLevel(level)

    formatter = logging.Formatter(format_string)

    if to_console:
        console_handler = logging.StreamHandler(sys.stdout)
        console_handler.setFormatter(formatter)
        logger.addHandler(console_handler)

    if to_file:
        file_handler = logging.FileHandler(to_file)
        file_handler.setFormatter(formatter)
        logger.addHandler(file_handler)

class ProgressReporter:
    """
    Handles progress reporting for the deezspot library.
    Supports both logging and custom callback functions.
    """
    def __init__(
        self, 
        callback: Optional[Callable[[Dict[str, Any]], None]] = None,
        silent: bool = False,
        log_level: int = logging.INFO
    ):
        self.callback = callback
        self.silent = silent
        self.log_level = log_level

    def report(self, progress_data: Dict[str, Any]) -> None:
        """
        Report progress using the configured method.
        
        Args:
            progress_data: Dictionary containing progress information
        """
        if self.callback:
            # Call the custom callback function if provided
            self.callback(progress_data)
        elif not self.silent:
            # Log using JSON format
            logger.log(self.log_level, json.dumps(progress_data))

# --- Standardized Progress Report Format ---
# The report_progress function generates a standardized dictionary (JSON object)
# to provide detailed feedback about the download process.
#
# Base Structure:
# {
#   "type": "track" | "album" | "playlist" | "episode",
#   "status": "initializing" | "skipped" | "retrying" | "real-time" | "error" | "done"
#   ... other fields based on type and status
# }
#
# --- Field Definitions ---
#
# [ General Fields ]
#   - url: (str) The URL of the item being processed.
#   - convert_to: (str) Target audio format for conversion (e.g., "mp3").
#   - bitrate: (str) Target bitrate for conversion (e.g., "320").
#
# [ Type: "track" ]
#   - song: (str) The name of the track.
#   - artists: (str) The artist of the track.
#   - album: (str, optional) The album of the track.
#   - parent: (dict, optional) Information about the container (album/playlist).
#     { "type": "album"|"playlist", "name": str, "owner": str, "artist": str, ... }
#   - current_track: (int, optional) The track number in the context of a parent.
#   - total_tracks: (int, optional) The total tracks in the context of a parent.
#
#   [ Status: "skipped" ]
#     - reason: (str) The reason for skipping (e.g., "Track already exists...").
#
#   [ Status: "retrying" ]
#     - retry_count: (int) The current retry attempt number.
#     - seconds_left: (int) The time in seconds until the next retry attempt.
#     - error: (str) The error message that caused the retry.
#
#   [ Status: "real-time" ]
#     - time_elapsed: (int) Time in milliseconds since the download started.
#     - progress: (int) Download percentage (0-100).
#
#   [ Status: "error" ]
#     - error: (str) The detailed error message.
#
#   [ Status: "done" (for single track downloads) ]
#     - summary: (dict) A summary of the operation.
#
# [ Type: "album" | "playlist" ]
#   - title / name: (str) The title of the album or name of the playlist.
#   - artist / owner: (str) The artist of the album or owner of the playlist.
#   - total_tracks: (int) The total number of tracks.
#
#   [ Status: "done" ]
#     - summary: (dict) A detailed summary of the entire download operation.
#       {
#         "successful_tracks": [str],
#         "skipped_tracks": [str],
#         "failed_tracks": [{"track": str, "reason": str}],
#         "total_successful": int,
#         "total_skipped": int,
#         "total_failed": int
#       }
#

def _remove_nulls(data):
    """
    Recursively remove null values from dictionaries and lists.
    
    Args:
        data: Any Python data structure (dict, list, etc.)
        
    Returns:
        The same structure with null values removed
    """
    if isinstance(data, dict):
        return {k: _remove_nulls(v) for k, v in data.items() if v is not None}
    elif isinstance(data, list):
        return [_remove_nulls(item) for item in data if item is not None]
    return data

def report_progress(
    reporter: Optional["ProgressReporter"],
    callback_obj: Union[trackCallbackObject, albumCallbackObject, playlistCallbackObject]
):
    """
    Reports progress using a standardized callback object.
    
    Args:
        reporter: The ProgressReporter to use for reporting
        callback_obj: A callback object of type trackCallbackObject, albumCallbackObject, or playlistCallbackObject
    """
    # Validate the callback object type
    if not isinstance(callback_obj, (trackCallbackObject, albumCallbackObject, playlistCallbackObject)):
        raise TypeError(f"callback_obj must be of type trackCallbackObject, albumCallbackObject, or playlistCallbackObject, got {type(callback_obj)}")
    
    # Convert the callback object to a dictionary and filter out null values
    report_dict = _remove_nulls(asdict(callback_obj))
    
    if reporter:
        reporter.report(report_dict)
    else:
        logger.info(json.dumps(report_dict)) 