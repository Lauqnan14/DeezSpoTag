#!/usr/bin/python3

from typing import Optional, Any, Dict, List
from deezspot.libutils.logging_utils import report_progress
from deezspot.models.callback import (
    trackCallbackObject, albumCallbackObject, playlistCallbackObject,
    initializingObject, skippedObject, retryingObject, realTimeObject, 
    errorObject, doneObject, summaryObject
)


def _get_reporter():
    """Get the active progress reporter from Download_JOB"""
    # Import here to avoid circular imports
    try:
        from deezspot.spotloader.__download__ import Download_JOB as SpotifyDJ
        if hasattr(SpotifyDJ, 'progress_reporter') and SpotifyDJ.progress_reporter:
            return SpotifyDJ.progress_reporter
    except ImportError:
        pass
    
    try:
        from deezspot.deezloader.__download__ import Download_JOB as DeezerDJ
        if hasattr(DeezerDJ, 'progress_reporter') and DeezerDJ.progress_reporter:
            return DeezerDJ.progress_reporter
    except ImportError:
        pass
    
    return None


def report_track_initializing(
    track_obj: Any,
    preferences: Any,
    parent_obj: Optional[Any] = None,
    current_track: Optional[int] = None,
    total_tracks: Optional[int] = None
) -> None:
    """
    Report track initialization status.
    
    Args:
        track_obj: Track object being initialized
        preferences: Preferences object with convert_to/bitrate info
        parent_obj: Parent object (album/playlist) if applicable
        current_track: Current track number for progress
        total_tracks: Total tracks for progress
    """
    status_obj = initializingObject(
        ids=getattr(track_obj, 'ids', None),
        convert_to=getattr(preferences, 'convert_to', None),
        bitrate=getattr(preferences, 'bitrate', None)
    )
    
    callback_obj = trackCallbackObject(
        track=track_obj,
        status_info=status_obj,
        current_track=current_track or getattr(preferences, 'track_number', None),
        total_tracks=total_tracks,
        parent=parent_obj
    )
    
    reporter = _get_reporter()
    if reporter:
        report_progress(reporter=reporter, callback_obj=callback_obj)


def report_track_skipped(
    track_obj: Any,
    reason: str,
    preferences: Any,
    parent_obj: Optional[Any] = None,
    current_track: Optional[int] = None,
    total_tracks: Optional[int] = None
) -> None:
    """
    Report track skipped status.
    
    Args:
        track_obj: Track object being skipped
        reason: Reason for skipping
        preferences: Preferences object with convert_to/bitrate info
        parent_obj: Parent object (album/playlist) if applicable
        current_track: Current track number for progress
        total_tracks: Total tracks for progress
    """
    status_obj = skippedObject(
        ids=getattr(track_obj, 'ids', None),
        reason=reason,
        convert_to=getattr(preferences, 'convert_to', None),
        bitrate=getattr(preferences, 'bitrate', None)
    )
    
    callback_obj = trackCallbackObject(
        track=track_obj,
        status_info=status_obj,
        current_track=current_track or getattr(preferences, 'track_number', None),
        total_tracks=total_tracks,
        parent=parent_obj
    )
    
    reporter = _get_reporter()
    if reporter:
        report_progress(reporter=reporter, callback_obj=callback_obj)


def report_track_retrying(
    track_obj: Any,
    retry_count: int,
    seconds_left: int,
    error: str,
    preferences: Any,
    parent_obj: Optional[Any] = None,
    current_track: Optional[int] = None,
    total_tracks: Optional[int] = None
) -> None:
    """
    Report track retry status.
    
    Args:
        track_obj: Track object being retried
        retry_count: Current retry attempt number
        seconds_left: Seconds until next retry
        error: Error that caused the retry
        preferences: Preferences object with convert_to/bitrate info
        parent_obj: Parent object (album/playlist) if applicable
        current_track: Current track number for progress
        total_tracks: Total tracks for progress
    """
    status_obj = retryingObject(
        ids=getattr(track_obj, 'ids', None),
        retry_count=retry_count,
        seconds_left=seconds_left,
        error=error,
        convert_to=getattr(preferences, 'convert_to', None),
        bitrate=getattr(preferences, 'bitrate', None)
    )
    
    callback_obj = trackCallbackObject(
        track=track_obj,
        status_info=status_obj,
        current_track=current_track or getattr(preferences, 'track_number', None),
        total_tracks=total_tracks,
        parent=parent_obj
    )
    
    reporter = _get_reporter()
    if reporter:
        report_progress(reporter=reporter, callback_obj=callback_obj)


def report_track_realtime_progress(
    track_obj: Any,
    time_elapsed: int,
    progress: int,
    preferences: Any,
    parent_obj: Optional[Any] = None,
    current_track: Optional[int] = None,
    total_tracks: Optional[int] = None
) -> None:
    """
    Report real-time track download progress.
    
    Args:
        track_obj: Track object being downloaded
        time_elapsed: Milliseconds elapsed
        progress: Progress percentage (0-100)
        preferences: Preferences object with convert_to/bitrate info
        parent_obj: Parent object (album/playlist) if applicable
        current_track: Current track number for progress
        total_tracks: Total tracks for progress
    """
    status_obj = realTimeObject(
        ids=getattr(track_obj, 'ids', None),
        time_elapsed=time_elapsed,
        progress=progress,
        convert_to=getattr(preferences, 'convert_to', None),
        bitrate=getattr(preferences, 'bitrate', None)
    )
    
    callback_obj = trackCallbackObject(
        track=track_obj,
        status_info=status_obj,
        current_track=current_track or getattr(preferences, 'track_number', None),
        total_tracks=total_tracks,
        parent=parent_obj
    )
    
    reporter = _get_reporter()
    if reporter:
        report_progress(reporter=reporter, callback_obj=callback_obj)


def report_track_error(
    track_obj: Any,
    error: str,
    preferences: Any,
    parent_obj: Optional[Any] = None,
    current_track: Optional[int] = None,
    total_tracks: Optional[int] = None
) -> None:
    """
    Report track error status.
    
    Args:
        track_obj: Track object that errored
        error: Error message
        preferences: Preferences object with convert_to/bitrate info
        parent_obj: Parent object (album/playlist) if applicable
        current_track: Current track number for progress
        total_tracks: Total tracks for progress
    """
    status_obj = errorObject(
        ids=getattr(track_obj, 'ids', None),
        error=error,
        convert_to=getattr(preferences, 'convert_to', None),
        bitrate=getattr(preferences, 'bitrate', None)
    )
    
    callback_obj = trackCallbackObject(
        track=track_obj,
        status_info=status_obj,
        current_track=current_track or getattr(preferences, 'track_number', None),
        total_tracks=total_tracks,
        parent=parent_obj
    )
    
    reporter = _get_reporter()
    if reporter:
        report_progress(reporter=reporter, callback_obj=callback_obj)


def report_track_done(
    track_obj: Any,
    preferences: Any,
    summary: Optional[summaryObject] = None,
    parent_obj: Optional[Any] = None,
    current_track: Optional[int] = None,
    total_tracks: Optional[int] = None,
    *,
    final_path: Optional[str] = None,
    download_quality: Optional[str] = None
) -> None:
    """
    Report track completion status.
    
    Args:
        track_obj: Track object that completed
        preferences: Preferences object with convert_to/bitrate info
        summary: Optional summary object for single track downloads
        parent_obj: Parent object (album/playlist) if applicable
        current_track: Current track number for progress
        total_tracks: Total tracks for progress
        final_path: Final filesystem path of the produced file
        download_quality: String label of the used download quality (e.g., OGG_160, OGG_320 or FLAC/MP3_320)
    """
    status_obj = doneObject(
        ids=getattr(track_obj, 'ids', None),
        summary=summary,
        convert_to=getattr(preferences, 'convert_to', None),
        bitrate=getattr(preferences, 'bitrate', None),
        final_path=final_path,
        download_quality=download_quality
    )
    
    callback_obj = trackCallbackObject(
        track=track_obj,
        status_info=status_obj,
        current_track=current_track or getattr(preferences, 'track_number', None),
        total_tracks=total_tracks,
        parent=parent_obj
    )
    
    reporter = _get_reporter()
    if reporter:
        report_progress(reporter=reporter, callback_obj=callback_obj)


def report_album_initializing(album_obj: Any) -> None:
    """
    Report album initialization status.
    
    Args:
        album_obj: Album object being initialized
    """
    status_obj = initializingObject(ids=getattr(album_obj, 'ids', None))
    callback_obj = albumCallbackObject(album=album_obj, status_info=status_obj)
    
    reporter = _get_reporter()
    if reporter:
        report_progress(reporter=reporter, callback_obj=callback_obj)


def report_album_done(album_obj: Any, summary: summaryObject) -> None:
    """
    Report album completion status.
    
    Args:
        album_obj: Album object that completed
        summary: Summary of track download results
    """
    status_obj = doneObject(ids=getattr(album_obj, 'ids', None), summary=summary)
    callback_obj = albumCallbackObject(album=album_obj, status_info=status_obj)
    
    reporter = _get_reporter()
    if reporter:
        report_progress(reporter=reporter, callback_obj=callback_obj)


def report_playlist_initializing(playlist_obj: Any) -> None:
    """
    Report playlist initialization status.
    
    Args:
        playlist_obj: Playlist object being initialized
    """
    status_obj = initializingObject(ids=getattr(playlist_obj, 'ids', None))
    callback_obj = playlistCallbackObject(playlist=playlist_obj, status_info=status_obj)
    
    reporter = _get_reporter()
    if reporter:
        report_progress(reporter=reporter, callback_obj=callback_obj)


def report_playlist_done(playlist_obj: Any, summary: summaryObject, *, m3u_path: Optional[str] = None) -> None:
    """
    Report playlist completion status.
    
    Args:
        playlist_obj: Playlist object that completed
        summary: Summary of track download results
        m3u_path: Final path of the generated m3u file, if any
    """
    if m3u_path:
        summary.m3u_path = m3u_path
    status_obj = doneObject(ids=getattr(playlist_obj, 'ids', None), summary=summary)
    callback_obj = playlistCallbackObject(playlist=playlist_obj, status_info=status_obj)
    
    reporter = _get_reporter()
    if reporter:
        report_progress(reporter=reporter, callback_obj=callback_obj)


# Convenience function for generic track reporting
def report_track_status(
    status_type: str,
    track_obj: Any,
    preferences: Any,
    parent_obj: Optional[Any] = None,
    current_track: Optional[int] = None,
    total_tracks: Optional[int] = None,
    **kwargs
) -> None:
    """
    Generic track status reporting function.
    
    Args:
        status_type: Type of status ('initializing', 'skipped', 'retrying', 'error', 'done', 'realtime')
        track_obj: Track object
        preferences: Preferences object
        parent_obj: Parent object if applicable
        current_track: Current track number
        total_tracks: Total tracks
        **kwargs: Additional parameters specific to status type
    """
    if status_type == 'initializing':
        report_track_initializing(track_obj, preferences, parent_obj, current_track, total_tracks)
    elif status_type == 'skipped':
        report_track_skipped(track_obj, kwargs.get('reason', 'Unknown'), preferences, parent_obj, current_track, total_tracks)
    elif status_type == 'retrying':
        report_track_retrying(track_obj, kwargs.get('retry_count', 0), kwargs.get('seconds_left', 0), 
                            kwargs.get('error', 'Unknown'), preferences, parent_obj, current_track, total_tracks)
    elif status_type == 'error':
        report_track_error(track_obj, kwargs.get('error', 'Unknown'), preferences, parent_obj, current_track, total_tracks)
    elif status_type == 'done':
        report_track_done(
            track_obj,
            preferences,
            kwargs.get('summary'),
            parent_obj,
            current_track,
            total_tracks,
            final_path=kwargs.get('final_path'),
            download_quality=kwargs.get('download_quality')
        )
    elif status_type == 'realtime':
        report_track_realtime_progress(track_obj, kwargs.get('time_elapsed', 0), kwargs.get('progress', 0),
                                     preferences, parent_obj, current_track, total_tracks) 