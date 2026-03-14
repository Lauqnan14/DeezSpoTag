#!/usr/bin/python3

import json
import os
from deezspot.libutils.logging_utils import logger

def artist_sort(array: list):
	if len(array) > 1:
		for a in array:
			for b in array:
				if a in b and a != b:
					array.remove(b)

	array = list(
		dict.fromkeys(array)
	)

	artists = "; ".join(array)

	return artists

def check_track_token(infos_dw):
    """
    Check and extract track token from the Deezer API response.
    
    Args:
        infos_dw: Deezer API response data
        
    Returns:
        str: Track token
    """
    try:
        token = infos_dw.get('TRACK_TOKEN')
        if not token:
            logger.error("Missing TRACK_TOKEN in API response")
            raise ValueError("Missing TRACK_TOKEN")
            
        return token
        
    except Exception as e:
        logger.error(f"Failed to check track token: {str(e)}")
        raise

def check_track_ids(infos_dw):
    """
    Check and extract track IDs from the Deezer API response.
    
    Args:
        infos_dw: Deezer API response data
        
    Returns:
        dict: Track IDs and encryption info
    """
    try:
        # Extract required IDs
        track_id = infos_dw.get('SNG_ID')
        if not track_id:
            logger.error("Missing SNG_ID in API response")
            raise ValueError("Missing SNG_ID")
            
        # Initialize result dictionary
        result = {'track_id': track_id}
        
        # Check for AES encryption info (MEDIA_KEY and MEDIA_NONCE)
        key = infos_dw.get('MEDIA_KEY')
        nonce = infos_dw.get('MEDIA_NONCE')
        
        if key and nonce:
            # AES encryption is available
            result['encryption_type'] = 'aes'
            result['key'] = key
            result['nonce'] = nonce
        else:
            # Fallback to Blowfish encryption
            md5_origin = infos_dw.get('MD5_ORIGIN')
            track_token = infos_dw.get('TRACK_TOKEN')
            media_version = infos_dw.get('MEDIA_VERSION', '1')
            
            if not md5_origin or not track_token:
                logger.error("Missing Blowfish encryption info (MD5_ORIGIN or TRACK_TOKEN) in API response")
                raise ValueError("Missing encryption info")
                
            result['encryption_type'] = 'blowfish'
            result['md5_origin'] = md5_origin
            result['track_token'] = track_token
            result['media_version'] = media_version
            
        return result
        
    except Exception as e:
        logger.error(f"Failed to check track IDs: {str(e)}")
        raise

def check_track_md5(infos_dw):
    """
    Check and extract track MD5 and media version from the Deezer API response.
    
    Args:
        infos_dw: Deezer API response data
        
    Returns:
        tuple: (Track MD5 hash, Media version)
    """
    try:
        md5 = infos_dw.get('MD5_ORIGIN')
        if not md5:
            logger.error("Missing MD5_ORIGIN in API response")
            raise ValueError("Missing MD5_ORIGIN")
            
        media_version = infos_dw.get('MEDIA_VERSION', '1')
        
        return md5, media_version
        
    except Exception as e:
        logger.error(f"Failed to check track MD5: {str(e)}")
        raise

def trasform_sync_lyric(lyrics):
    """
    Transform synchronized lyrics into a standard format.
    
    Args:
        lyrics: Raw lyrics data
        
    Returns:
        str: Formatted lyrics
    """
    try:
        if not lyrics:
            return ""
            
        # Parse lyrics data
        data = json.loads(lyrics)
        
        # Format each line with timestamp
        formatted = []
        for line in data:
            timestamp = line.get('timestamp', 0)
            text = line.get('text', '')
            if text:
                formatted.append(f"[{timestamp}]{text}")
                
        return "\n".join(formatted)
        
    except Exception as e:
        logger.error(f"Failed to transform lyrics: {str(e)}")
        return ""