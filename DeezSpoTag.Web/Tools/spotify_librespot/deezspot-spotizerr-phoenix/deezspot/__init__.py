#!/usr/bin/python3

"""
Deezspot - A Deezer/Spotify downloading library with proper logging support.
"""

import logging
from deezspot.libutils.logging_utils import configure_logger, logger

# Export key functionality
from deezspot.deezloader import DeeLogin
from deezspot.models.download import Track, Album, Playlist, Smart, Episode

__version__ = "1.2.0"

# Configure default logging (silent by default)
configure_logger(level=logging.WARNING, to_console=False)

def set_log_level(level):
    """
    Set the logging level for the deezspot library.
    
    Args:
        level: Logging level (e.g., logging.INFO, logging.DEBUG, logging.WARNING)
    """
    configure_logger(level=level, to_console=True)
    
def disable_logging():
    """Disable all logging output."""
    configure_logger(level=logging.CRITICAL, to_console=False)
    
def enable_file_logging(filepath, level=logging.INFO):
    """
    Enable logging to a file.
    
    Args:
        filepath: Path to the log file
        level: Logging level (defaults to INFO)
    """
    configure_logger(level=level, to_file=filepath, to_console=True)
