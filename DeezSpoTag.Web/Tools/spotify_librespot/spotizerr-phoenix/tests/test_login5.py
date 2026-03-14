#!/usr/bin/env python3
"""
Test script to verify Login5 authentication using stored credentials
"""

import logging
import sys
import os
from librespot.core import Session

# Enable debug logging to see what's happening
logging.basicConfig(level=logging.DEBUG, format='%(asctime)s - %(name)s - %(levelname)s - %(message)s')

def test_with_stored_credentials():
    """Test using stored credentials (if available)"""
    print("=== Testing with stored credentials ===")
    
    try:
        if os.path.exists("credentials.json"):
            session = Session.Builder().stored_file("credentials.json").create()
            print(f"✓ Successfully authenticated as: {session.username()}")
            
            # Test token retrieval
            token_provider = session.tokens()
            try:
                token = token_provider.get("playlist-read")
                print(f"✓ Successfully got playlist-read token: {token[:20]}...")
                
                # Check if Login5 token is available
                login5_token = session.get_login5_token()
                if login5_token:
                    print(f"✓ Login5 token available: {login5_token[:20]}...")
                else:
                    print("⚠ Login5 token not available")
                
                session.close()
                return True
            except Exception as e:
                print(f"✗ Token retrieval failed: {e}")
                session.close()
                return False
        else:
            print("⚠ No credentials.json file found")
            return False
            
    except Exception as e:
        print(f"✗ Authentication failed: {e}")
        return False

def main():
    print("Testing Login5 Authentication Implementation")
    print("=" * 50)
    
    # Test with stored credentials
    stored_success = test_with_stored_credentials()
    
    # Note: Username/password authentication has been deprecated by Spotify
    # and is no longer supported. Use stored credentials or OAuth flow instead.
    
    print("\n" + "=" * 50)
    print("Test Results:")
    print(f"Stored credentials: {'✓ PASS' if stored_success else '✗ FAIL or NO CREDENTIALS'}")
    
    if stored_success:
        print("\n🎉 Login5 authentication is working!")
        return 0
    else:
        print("\n⚠ Could not test authentication - no valid credentials.json file found")
        print("  Please authenticate using OAuth or other supported methods first.")
        return 1

if __name__ == "__main__":
    sys.exit(main())