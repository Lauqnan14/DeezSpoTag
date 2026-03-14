#!/usr/bin/python3

sources = [
	"dee", "spo"
]

header = {
	"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:88.0) Gecko/20100101 Firefox/88.0",
	"Accept-Language": "en-US;q=0.5,en;q=0.3"
}

supported_link = [
	"www.deezer.com", "open.spotify.com",
	"deezer.com", "spotify.com",
	"deezer.page.link", "www.spotify.com"
]

answers = ["Y", "y", "Yes", "YES"]
stock_output = "Songs"
stock_recursive_quality = False
stock_recursive_download = False
stock_not_interface = False
stock_zip = False
stock_real_time_dl = False
# New: default real-time multiplier (0-10). 1 means real-time, 0 disables pacing.
stock_real_time_multiplier = 1
stock_save_cover = False # Default for saving cover image
stock_market = None
