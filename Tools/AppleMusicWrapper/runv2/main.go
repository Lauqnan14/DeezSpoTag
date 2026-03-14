package main

import (
	"flag"
	"fmt"
	"os"
)

type Config struct {
	DecryptM3u8Port string
	MaxMemoryLimit  int
}

func main() {
	var adamId string
	var playlistUrl string
	var outputPath string
	var decryptPort string
	var maxMemory int

	flag.StringVar(&adamId, "adam-id", "", "Apple Music adam id")
	flag.StringVar(&playlistUrl, "playlist-url", "", "Media playlist URL")
	flag.StringVar(&outputPath, "output", "", "Output file path")
	flag.StringVar(&decryptPort, "decrypt-port", "127.0.0.1:10020", "Decrypt M3U8 port")
	flag.IntVar(&maxMemory, "max-memory-mb", 512, "Max memory buffer in MB")
	flag.Parse()

	if adamId == "" || playlistUrl == "" || outputPath == "" {
		fmt.Fprintln(os.Stderr, "adam-id, playlist-url, and output are required")
		os.Exit(2)
	}

	cfg := Config{
		DecryptM3u8Port: decryptPort,
		MaxMemoryLimit:  maxMemory,
	}

	if err := Run(adamId, playlistUrl, outputPath, cfg); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
