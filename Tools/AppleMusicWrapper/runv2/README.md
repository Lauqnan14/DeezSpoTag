Apple Music wrapper decrypt helper (runv2)

Build:
  go build -o apple-wrapper-runv2 .

Usage:
  ./apple-wrapper-runv2 --adam-id <id> --playlist-url <m3u8> --output <path> --decrypt-port 127.0.0.1:10020

This helper speaks the wrapper decrypt protocol used by WorldObservationLog.
