import os
import sys
import signal
import atexit
from deezspot.libutils.logging_utils import logger

# --- Global tracking of active downloads ---
ACTIVE_DOWNLOADS = set()
CLEANUP_LOCK = False
CURRENT_DOWNLOAD = None

def register_active_download(file_path):
    """Register a file as being actively downloaded"""
    global CURRENT_DOWNLOAD
    ACTIVE_DOWNLOADS.add(file_path)
    CURRENT_DOWNLOAD = file_path

def unregister_active_download(file_path):
    """Remove a file from the active downloads list"""
    global CURRENT_DOWNLOAD
    if file_path in ACTIVE_DOWNLOADS:
        ACTIVE_DOWNLOADS.remove(file_path)
        if CURRENT_DOWNLOAD == file_path:
            CURRENT_DOWNLOAD = None

def cleanup_active_downloads():
    """Clean up any incomplete downloads during process termination"""
    global CLEANUP_LOCK, CURRENT_DOWNLOAD
    if CLEANUP_LOCK:
        return

    CLEANUP_LOCK = True
    # Only remove the file that was in progress when stopped
    if CURRENT_DOWNLOAD:
        try:
            if os.path.exists(CURRENT_DOWNLOAD):
                logger.info(f"Removing incomplete download: {CURRENT_DOWNLOAD}")
                os.remove(CURRENT_DOWNLOAD)
                # No need to call unregister_active_download here,
                # as the process is terminating.
        except Exception as e:
            logger.error(f"Error cleaning up file {CURRENT_DOWNLOAD}: {str(e)}")
    CLEANUP_LOCK = False

# Register the cleanup function to run on exit
atexit.register(cleanup_active_downloads)

# Set up signal handlers
def signal_handler(sig, frame):
    logger.info(f"Received termination signal {sig}. Cleaning up...")
    cleanup_active_downloads()
    if sig == signal.SIGINT:
        logger.info("CTRL+C received. Exiting...")
    sys.exit(0)

# Register signal handlers for common termination signals
signal.signal(signal.SIGINT, signal_handler)   # CTRL+C
signal.signal(signal.SIGTERM, signal_handler)  # Normal termination
try:
    # These may not be available on all platforms
    signal.signal(signal.SIGHUP, signal_handler)   # Terminal closed
    signal.signal(signal.SIGQUIT, signal_handler)  # CTRL+\
except AttributeError:
    pass 