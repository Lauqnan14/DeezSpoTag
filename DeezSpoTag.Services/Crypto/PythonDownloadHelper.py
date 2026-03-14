#!/usr/bin/env python3
"""
Python download helper that bypasses SSL verification completely
Equivalent to deezspotag's approach with requests and urllib3.disable_warnings()
"""

import sys
import requests
import urllib3
from urllib3.exceptions import InsecureRequestWarning
import os
import tempfile
import json

# Disable SSL warnings completely like deezspotag
urllib3.disable_warnings(InsecureRequestWarning)

def download_track(url, output_path, track_id):
    """
    Download track using Python requests with SSL verification disabled
    Equivalent to deezspotag's download approach
    """
    try:
        # Create session with secure default TLS validation.
        # Compatibility mode can still be enabled explicitly via env var.
        session = requests.Session()
        allow_insecure_tls = os.environ.get("DEEZSPOTAG_ALLOW_INSECURE_TLS", "").strip().lower() in ("1", "true", "yes")
        session.verify = not allow_insecure_tls
        
        # Set headers similar to deezspotag
        headers = {
            'User-Agent': 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/79.0.3945.130 Safari/537.36',
            'Accept': '*/*',
            'Accept-Encoding': 'gzip, deflate, br',
            'Accept-Language': 'en-US,en;q=0.9',
            'Connection': 'keep-alive'
        }
        
        # Download with streaming to handle large files
        response = session.get(url, headers=headers, stream=True, timeout=300)
        response.raise_for_status()
        
        # Write to temporary file first, then move to final location
        temp_path = output_path + '.tmp'
        
        with open(temp_path, 'wb') as f:
            for chunk in response.iter_content(chunk_size=8192):
                if chunk:
                    f.write(chunk)
        
        # Move temp file to final location
        os.rename(temp_path, output_path)
        
        return {
            'success': True,
            'track_id': track_id,
            'path': output_path,
            'size': os.path.getsize(output_path)
        }
        
    except Exception as e:
        # Clean up temp file if it exists
        temp_path = output_path + '.tmp'
        if os.path.exists(temp_path):
            os.remove(temp_path)
            
        return {
            'success': False,
            'error': str(e),
            'track_id': track_id,
            'path': output_path
        }

if __name__ == '__main__':
    if len(sys.argv) != 4:
        print(json.dumps({'success': False, 'error': 'Usage: python3 PythonDownloadHelper.py <url> <output_path> <track_id>'}))
        sys.exit(1)
    
    url = sys.argv[1]
    output_path = sys.argv[2]
    track_id = sys.argv[3]
    
    result = download_track(url, output_path, track_id)
    print(json.dumps(result))
