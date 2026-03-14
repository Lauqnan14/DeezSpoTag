#!/usr/bin/python3

from hashlib import md5 as __md5

from binascii import (
	a2b_hex as __a2b_hex,
	b2a_hex as __b2a_hex
)

from Crypto.Cipher.Blowfish import (
	new as __newBlowfish,
	MODE_CBC as __MODE_CBC
)

from Crypto.Cipher import AES
from Crypto.Util import Counter
import os
from deezspot.libutils.logging_utils import logger

__secret_key = "g4el58wc0zvf9na1"
__secret_key2 = b"jo6aey6haid2Teih"
__idk = __a2b_hex("0001020304050607")

def md5hex(data: str):
	hashed = __md5(
		data.encode()
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
        hash_obj = hashlib.sha1()
        hash_obj.update(data.encode('utf-8'))
        return hash_obj.hexdigest()
        
    except Exception as e:
        logger.error(f"Failed to generate song hash: {str(e)}")
        raise

def __calcbfkey(songid):
	"""
	Calculate the Blowfish decrypt key for a given song ID.
	
	Args:
		songid: String song ID
		
	Returns:
		The Blowfish decryption key
	"""
	try:
		h = md5hex(songid)
		logger.debug(f"MD5 hash of song ID '{songid}': {h}")

		# Build the key through XOR operations as per Deezer's algorithm
		bfkey = "".join(
			chr(
				ord(h[i]) ^ ord(h[i + 16]) ^ ord(__secret_key[i])
			)
			for i in range(16)
		)

		# Log the generated key in hex format for debugging
		logger.debug(f"Generated Blowfish key: {bfkey.encode().hex()}")
		return bfkey
	except Exception as e:
		logger.error(f"Error calculating Blowfish key: {str(e)}")
		raise

def __blowfishDecrypt(data, key):
	"""
	Decrypt a single block of data using Blowfish in CBC mode.
	
	Args:
		data: The encrypted data block (must be a multiple of 8 bytes)
		key: The Blowfish key as a string
		
	Returns:
		The decrypted data
	"""
	try:
		# Ensure data is a multiple of Blowfish block size (8 bytes)
		if len(data) % 8 != 0:
			logger.warning(f"Data length {len(data)} is not a multiple of 8 bytes - Blowfish requires 8-byte blocks")
			# Pad data to a multiple of 8 if needed (though this should be avoided)
			padding = 8 - (len(data) % 8)
			data += b'\x00' * padding
			logger.warning(f"Padded data with {padding} null bytes")
		
		# Create Blowfish cipher in CBC mode with initialization vector
		c = __newBlowfish(
			key.encode(), __MODE_CBC, __idk	
		)
		
		# Decrypt the data
		decrypted = c.decrypt(data)
		logger.debug(f"Decrypted {len(data)} bytes of data")
		
		return decrypted
	except Exception as e:
		logger.error(f"Error in Blowfish decryption: {str(e)}")
		raise

def decrypt_blowfish_track(crypted_audio, song_id, md5_origin, song_path):
    """
    Decrypt the audio file using Blowfish encryption.
    
    Args:
        crypted_audio: The encrypted audio data
        song_id: The song ID for generating the key
        md5_origin: The MD5 hash of the track
        song_path: Path where to save the decrypted file
    """
    try:
        # Calculate the Blowfish key
        bf_key = __calcbfkey(song_id)
        
        # For debugging - log the key being used
        logger.debug(f"Using Blowfish key for decryption: {bf_key.encode().hex()}")
        
        # Prepare to process the file
        block_size = 2048  # Size of each block to process
        
        # We need to reconstruct the data from potentially variable-sized chunks into
        # fixed-size blocks for proper decryption
        buffer = bytearray()
        block_count = 0  # Count of completed blocks
        
        # Open the output file
        with open(song_path, 'wb') as output_file:
            # Process each incoming chunk of data
            for chunk in crypted_audio:
                if not chunk:
                    continue
                
                # Add current chunk to our buffer
                buffer.extend(chunk)
                
                # Process as many complete blocks as we can
                while len(buffer) >= block_size:
                    # Extract a block from buffer
                    block = buffer[:block_size]
                    buffer = buffer[block_size:]
                    
                    # Only decrypt every third block
                    is_encrypted = (block_count % 3 == 0)
                    
                    if is_encrypted:
                        # Ensure the block is a multiple of 8 bytes (Blowfish block size)
                        if len(block) == block_size and len(block) % 8 == 0:
                            try:
                                # Create a fresh cipher with the initialization vector for each block
                                # This is crucial - we need to reset the IV for each encrypted block
                                cipher = __newBlowfish(bf_key.encode(), __MODE_CBC, __idk)
                                
                                # Decrypt the block
                                block = cipher.decrypt(block)
                                logger.debug(f"Decrypted block {block_count} (size: {len(block)})")
                            except Exception as e:
                                logger.error(f"Failed to decrypt block {block_count}: {str(e)}")
                                # Continue with the encrypted block rather than failing completely
                    
                    # Write the block (decrypted or not) to the output file
                    output_file.write(block)
                    block_count += 1
            
            # Write any remaining data in the buffer (this won't be decrypted as it's a partial block)
            if buffer:
                logger.debug(f"Writing final partial block of size {len(buffer)}")
                output_file.write(buffer)
            
        logger.debug(f"Successfully decrypted and saved Blowfish-encrypted file to {song_path}")
        
    except Exception as e:
        logger.error(f"Failed to decrypt Blowfish file: {str(e)}")
        raise

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
        # Check if this is a FLAC file based on file extension
        is_flac = song_path.lower().endswith('.flac')
        
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
            # Customize Blowfish decryption based on file type
            if is_flac:
                logger.debug("Detected FLAC file - using special FLAC decryption handling")
                decrypt_blowfish_flac(
                    crypted_audio, 
                    str(ids['track_id']), 
                    ids['md5_origin'], 
                    song_path
                )
            else:
                # Use standard Blowfish decryption for MP3
                decrypt_blowfish_track(
                    crypted_audio, 
                    str(ids['track_id']), 
                    ids['md5_origin'], 
                    song_path
                )
        else:
            raise ValueError(f"Unknown encryption type: {encryption_type}")
            
    except Exception as e:
        logger.error(f"Failed to decrypt file: {str(e)}")
        raise

def decrypt_blowfish_flac(crypted_audio, song_id, md5_origin, song_path):
    """
    Special decryption function for FLAC files using Blowfish encryption.
    This implementation follows Deezer's encryption scheme exactly.
    
    In Deezer's encryption scheme:
    - Data is processed in 2048-byte blocks
    - Only every third block is encrypted (blocks 0, 3, 6, etc.)
    - Partial blocks at the end of the file are not encrypted
    - FLAC file structure must be preserved exactly
    - The initialization vector is reset for each encrypted block
    
    Args:
        crypted_audio: Iterator of the encrypted audio data chunks
        song_id: The song ID for generating the key
        md5_origin: The MD5 hash of the track
        song_path: Path where to save the decrypted file
    """
    try:
        # Calculate the Blowfish key
        bf_key = __calcbfkey(song_id)
        
        # For debugging - log the key being used
        logger.debug(f"Using Blowfish key for decryption: {bf_key.encode().hex()}")
        
        # Prepare to process the file
        block_size = 2048  # Size of each block to process
        
        # We need to reconstruct the data from potentially variable-sized chunks into
        # fixed-size blocks for proper decryption
        buffer = bytearray()
        block_count = 0  # Count of completed blocks
        
        # Open the output file
        with open(song_path, 'wb') as output_file:
            # Process each incoming chunk of data
            for chunk in crypted_audio:
                if not chunk:
                    continue
                
                # Add current chunk to our buffer
                buffer.extend(chunk)
                
                # Process as many complete blocks as we can
                while len(buffer) >= block_size:
                    # Extract a block from buffer
                    block = buffer[:block_size]
                    buffer = buffer[block_size:]
                    
                    # Determine if this block should be decrypted (every third block)
                    if block_count % 3 == 0:
                        # Ensure we have a complete block for decryption and it's a multiple of 8 bytes
                        if len(block) == block_size and len(block) % 8 == 0:
                            try:
                                # Create a fresh cipher with the initialization vector for each block
                                # This is crucial - we need to reset the IV for each encrypted block
                                cipher = __newBlowfish(bf_key.encode(), __MODE_CBC, __idk)
                                
                                # Decrypt the block
                                block = cipher.decrypt(block)
                                logger.debug(f"Decrypted block {block_count} (size: {len(block)})")
                            except Exception as e:
                                logger.error(f"Failed to decrypt block {block_count}: {str(e)}")
                                # Continue with the encrypted block rather than failing completely
                    
                    # Write the block (decrypted or not) to the output file
                    output_file.write(block)
                    block_count += 1
            
            # Write any remaining data in the buffer (this won't be decrypted as it's a partial block)
            if buffer:
                logger.debug(f"Writing final partial block of size {len(buffer)}")
                output_file.write(buffer)
        
        # Final validation
        if os.path.getsize(song_path) > 0:
            with open(song_path, 'rb') as f:
                if f.read(4) == b'fLaC':
                    logger.info(f"FLAC file header verification passed")
                else:
                    logger.warning("FLAC file doesn't begin with proper 'fLaC' signature")
            
            logger.info(f"Successfully decrypted FLAC file to {song_path} ({os.path.getsize(song_path)} bytes)")
            
            # Run the detailed analysis
            analysis = analyze_flac_file(song_path)
            if analysis.get("potential_issues"):
                logger.warning(f"Decryption completed but analysis found issues: {analysis['potential_issues']}")
            else:
                logger.info("FLAC analysis indicates the file structure is valid")
                
        else:
            logger.error("Decrypted file is empty - decryption likely failed")
            
    except Exception as e:
        logger.error(f"Failed to decrypt Blowfish FLAC file: {str(e)}")
        raise

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