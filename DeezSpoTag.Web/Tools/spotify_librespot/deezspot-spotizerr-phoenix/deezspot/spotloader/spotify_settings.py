#!/usr/bin/python3

from librespot.audio.decoders import AudioQuality

stock_quality = "HIGH"
librespot_credentials = "credentials.json"

qualities = {
	"HIGH": {
		"n_quality": AudioQuality.HIGH,
		"f_format": ".ogg",
		"s_quality": "HIGH"
	},

	"VERY_HIGH": {
		"n_quality": AudioQuality.VERY_HIGH,
		"f_format": ".ogg",
		"s_quality": "VERY_HIGH"
	},

	"NORMAL": {
		"n_quality": AudioQuality.NORMAL,
		"f_format": ".ogg",
		"s_quality": "NORMAL"
	}
}

# Document the allowed bounds for real-time pacing speed-ups
REAL_TIME_MULTIPLIER_MIN = 0
REAL_TIME_MULTIPLIER_MAX = 10

stock_market = None
