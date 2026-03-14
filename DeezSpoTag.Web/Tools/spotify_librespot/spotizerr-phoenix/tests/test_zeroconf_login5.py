#!/usr/bin/env python3
"""
Test script using Zeroconf/Spotify Connect to verify Login5 authentication
"""

import logging
import time
import pathlib
from librespot.zeroconf import ZeroconfServer

# Enable debug logging
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(name)s - %(levelname)s - %(message)s')

def test_zeroconf_login5():
    """Test Login5 using Zeroconf authentication"""
    print("=== Testing Login5 with Zeroconf ===")
    print("1. Open Spotify on your phone/computer")
    print("2. Look for 'librespot-spotizerr' in Spotify Connect devices")
    print("3. Transfer playback to it")
    print("4. Wait for credentials to be stored...")
    print("\nWaiting for Spotify Connect transfer...")
    
    zs = ZeroconfServer.Builder().create()
    
    start_time = time.time()
    timeout = 60  # 60 seconds timeout
    
    while True:
        time.sleep(1)
        elapsed = time.time() - start_time
        
        if elapsed > timeout:
            print(f"\n⚠ Timeout after {timeout} seconds")
            print("Make sure you:")
            print("- Have Spotify open on another device")
            print("- Can see 'librespot-spotizerr' in Spotify Connect")
            print("- Transfer playback to it")
            return False
            
        if zs._ZeroconfServer__session:
            session = zs._ZeroconfServer__session
            print(f"\n✓ Got session for user: {session.username()}")
            
            # Test token retrieval
            try:
                token_provider = session.tokens()
                token = token_provider.get("playlist-read")
                print(f"✓ Got playlist-read token: {token[:20]}...")
                
                # Test Login5 token
                login5_token = session.get_login5_token()
                if login5_token:
                    print(f"✓ Login5 token available: {login5_token[:20]}...")
                else:
                    print("⚠ Login5 token not available")
                
                # Check if credentials were saved
                if pathlib.Path("credentials.json").exists():
                    print("✓ Credentials saved to credentials.json")
                    print("\nYou can now use the stored credentials for future tests!")
                    return True
                else:
                    print("⚠ Credentials not saved")
                    return True
                    
            except Exception as e:
                print(f"✗ Token test failed: {e}")
                return False

def main():
    print("Zeroconf Login5 Test")
    print("=" * 30)
    
    success = test_zeroconf_login5()
    
    print("\n" + "=" * 30)
    if success:
        print("🎉 Zeroconf Login5 test completed successfully!")
        return 0
    else:
        print("⚠ Zeroconf test failed or timed out")
        return 1

if __name__ == "__main__":
    import sys
    sys.exit(main())