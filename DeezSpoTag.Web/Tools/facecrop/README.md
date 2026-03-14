# facecrop

This helper crops playlist background images around detected faces.

Requirements
- Go 1.21+
- Pigo face detection model file named `facefinder` in `Tools/facecrop/model/`

Build (example)
- `go build -o bin/facecrop` from this directory

Environment overrides
- `DEEZSPOTAG_FACE_CROP_TOOL` to point to the compiled binary
- `DEEZSPOTAG_FACE_CROP_MODEL` to point to the Pigo model file
