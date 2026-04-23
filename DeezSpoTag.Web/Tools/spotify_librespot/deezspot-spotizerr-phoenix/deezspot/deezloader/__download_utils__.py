#!/usr/bin/python3

import hashlib

from Cryptodome.Cipher import AES
from Cryptodome.Util import Counter
import os
from deezspot.libutils.logging_utils import logger

class InsecureCipherError(RuntimeError):
    """Raised when legacy insecure ciphers are requested."""

def md5hex(data: str):
	hashed = hashlib.md5(
		data.encode(),
		usedforsecurity=False,
	).hexdigest()

	return hashed

def gen_song_hash(song_id, song_md5, media_version):
    """
    Generate a hash for the song using its ID, MD5 and media version.
    
    Args:
        song_id: The song's ID
        song_md5: The song's MD5 hash
        media_version: The media version
        
    Returns:
        str: The generated hash
    """
    try:
        # Combine the song data
        data = f"{song_md5}{media_version}{song_id}"
        
        # Generate hash using SHA1
        import hashlib
        hash_obj = hashlib.sha1(usedforsecurity=False)
        hash_obj.update(data.encode('utf-8'))
        return hash_obj.hexdigest()
        
    except Exception as e:
        logger.error(f"Failed to generate song hash: {str(e)}")
        raise

def _raise_insecure_blowfish_error(song_path: str) -> None:
    message = (
        "Blocked insecure Blowfish/CBC media decryption for "
        f"{song_path}. Re-download using AES-encrypted source."
    )
    logger.error(message)
    raise InsecureCipherError(message)

def decrypt_blowfish_track(_crypted_audio, _song_id, _md5_origin, song_path):
    _raise_insecure_blowfish_error(song_path)

def decryptfile(crypted_audio, ids, song_path):
    """
    Decrypt the audio file using either AES or Blowfish encryption.
    
    Args:
        crypted_audio: The encrypted audio data
        ids: The track IDs containing encryption info
        song_path: Path where to save the decrypted file
    """
    try:
        # Check encryption type
        encryption_type = ids.get('encryption_type', 'aes')
        if encryption_type == 'aes':
            # Get the AES encryption key and nonce
            key = bytes.fromhex(ids['key'])
            nonce = bytes.fromhex(ids['nonce'])
            
            # For AES-CTR, we can decrypt chunk by chunk
            counter = Counter.new(128, initial_value=int.from_bytes(nonce, byteorder='big'))
            cipher = AES.new(key, AES.MODE_CTR, counter=counter)
            
            # Open the output file
            with open(song_path, 'wb') as f:
                # Process the data in chunks
                for chunk in crypted_audio:
                    if chunk:
                        # Decrypt the chunk and write to file
                        decrypted_chunk = cipher.decrypt(chunk)
                        f.write(decrypted_chunk)
                
            logger.debug(f"Successfully decrypted and saved AES-encrypted file to {song_path}")
            
        elif encryption_type == 'blowfish':
            _raise_insecure_blowfish_error(song_path)
        else:
            raise ValueError(f"Unknown encryption type: {encryption_type}")
            
    except Exception as e:
        logger.error(f"Failed to decrypt file: {str(e)}")
        raise

def decrypt_blowfish_flac(_crypted_audio, _song_id, _md5_origin, song_path):
    _raise_insecure_blowfish_error(song_path)

def analyze_flac_file(file_path, limit=100):
    """
    Analyze a FLAC file at the binary level for debugging purposes.
    This function helps identify issues with file structure that might cause
    playback problems.
    
    Args:
        file_path: Path to the FLAC file
        limit: Maximum number of blocks to analyze
        
    Returns:
        A dictionary with analysis results
    """
    try:
        results = {
            "file_size": 0,
            "has_flac_signature": False,
            "block_structure": [],
            "metadata_blocks": 0,
            "potential_issues": []
        }
        
        if not os.path.exists(file_path):
            results["potential_issues"].append("File does not exist")
            return results
            
        # Get file size
        file_size = os.path.getsize(file_path)
        results["file_size"] = file_size
        
        if file_size < 8:
            results["potential_issues"].append("File too small to be a valid FLAC")
            return results
            
        with open(file_path, 'rb') as f:
            # Check FLAC signature (first 4 bytes should be 'fLaC')
            header = f.read(4)
            results["has_flac_signature"] = (header == b'fLaC')
            
            if not results["has_flac_signature"]:
                results["potential_issues"].append(f"Missing FLAC signature. Found: {header}")
                
            # Read and analyze metadata blocks
            # FLAC format: https://xiph.org/flac/format.html
            try:
                # Go back to position after signature
                f.seek(4)
                
                # Read metadata blocks
                last_block = False
                block_count = 0
                
                while not last_block and block_count < limit:
                    block_header = f.read(4)
                    if len(block_header) < 4:
                        break
                        
                    # First bit of first byte indicates if this is the last metadata block
                    last_block = (block_header[0] & 0x80) != 0
                    # Last 7 bits of first byte indicate block type
                    block_type = block_header[0] & 0x7F
                    # Next 3 bytes indicate length of block data
                    block_length = (block_header[1] << 16) | (block_header[2] << 8) | block_header[3]
                    
                    # Record block info
                    block_info = {
                        "position": f.tell() - 4,
                        "type": block_type,
                        "length": block_length,
                        "is_last": last_block
                    }
                    
                    results["block_structure"].append(block_info)
                    
                    # Skip to next block
                    f.seek(block_length, os.SEEK_CUR)
                    block_count += 1
                
                results["metadata_blocks"] = block_count
                
                # Check for common issues
                if block_count == 0:
                    results["potential_issues"].append("No metadata blocks found")
                
                # Check for STREAMINFO block (type 0) which should be present
                has_streaminfo = any(block["type"] == 0 for block in results["block_structure"])
                if not has_streaminfo:
                    results["potential_issues"].append("Missing STREAMINFO block")
                
            except Exception as e:
                results["potential_issues"].append(f"Error analyzing metadata: {str(e)}")
            
        return results
        
    except Exception as e:
        logger.error(f"Error analyzing FLAC file: {str(e)}")
        return {"error": str(e)}
