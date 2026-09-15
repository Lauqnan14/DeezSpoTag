PLUGIN := deezspotag-metadata
OUT    := $(PLUGIN).ndp

.PHONY: all build package clean install

all: package

build:
	tinygo build -o plugin.wasm -target wasip1 -buildmode=c-shared .

package: build
	zip -j $(OUT) manifest.json plugin.wasm

# NAVIDROME_PLUGIN_DIR must point at the folder configured as [Plugins].Folder
install: package
	@test -n "$(NAVIDROME_PLUGIN_DIR)" || (echo "set NAVIDROME_PLUGIN_DIR" && exit 1)
	cp $(OUT) "$(NAVIDROME_PLUGIN_DIR)/"

clean:
	rm -f plugin.wasm $(OUT)
