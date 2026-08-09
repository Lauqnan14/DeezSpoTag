package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"net/url"
	"strconv"
	"strings"

	"github.com/navidrome/navidrome/plugins/pdk/go/host"
	"github.com/navidrome/navidrome/plugins/pdk/go/metadata"
	"github.com/navidrome/navidrome/plugins/pdk/go/types"
)

const (
	defaultCacheTTL  = int64(86400)
	negativeCacheTTL = int64(3600)
	requestTimeoutMs = int32(10000)
)

var errNotConfigured = errors.New("deezspotag: baseUrl and apiToken must be configured")

type plugin struct{}

func fetch(path string, query url.Values, out any) (bool, error) {
	base, _ := host.ConfigGet("baseUrl")
	token, _ := host.ConfigGet("apiToken")
	base = strings.TrimRight(strings.TrimSpace(base), "/")
	token = strings.TrimSpace(token)
	if base == "" || token == "" {
		return false, errNotConfigured
	}

	endpoint := base + path + "?" + query.Encode()
	if cached, ok, err := host.CacheGetString(endpoint); err == nil && ok {
		if cached == "" {
			return false, nil
		}
		return true, json.Unmarshal([]byte(cached), out)
	}

	resp, err := host.HTTPSend(host.HTTPRequest{
		Method: "GET",
		URL:    endpoint,
		Headers: map[string]string{
			"Authorization": "Bearer " + token,
			"Accept":        "application/json",
		},
		TimeoutMs: requestTimeoutMs,
	})
	if err != nil {
		return false, err
	}

	switch resp.StatusCode {
	case 204, 404:
		_ = host.CacheSetString(endpoint, "", negativeCacheTTL)
		return false, nil
	case 200:
		_ = host.CacheSetString(endpoint, string(resp.Body), cacheTTL())
		return true, json.Unmarshal(resp.Body, out)
	default:
		return false, fmt.Errorf("deezspotag: HTTP %d", resp.StatusCode)
	}
}

func cacheTTL() int64 {
	if v, ok := host.ConfigGetInt("cacheTtlSeconds"); ok && v > 0 {
		return v
	}
	return defaultCacheTTL
}

func artistQuery(id, name string) url.Values {
	q := url.Values{}
	if id != "" {
		q.Set("id", id)
	}
	q.Set("name", name)
	return q
}

func (p *plugin) GetArtistBiography(req metadata.ArtistRequest) (*metadata.ArtistBiographyResponse, error) {
	q := artistQuery(req.ID, req.Name)
	if src, ok := host.ConfigGet("preferredBiographySource"); ok && strings.TrimSpace(src) != "" {
		q.Set("preferredSource", strings.TrimSpace(src))
	}

	var out struct {
		Biography string `json:"biography"`
	}
	ok, err := fetch("/api/metadata-agent/artist/biography", q, &out)
	if err != nil || !ok || strings.TrimSpace(out.Biography) == "" {
		return nil, err
	}
	return &metadata.ArtistBiographyResponse{Biography: out.Biography}, nil
}

func (p *plugin) GetArtistTopSongs(req metadata.TopSongsRequest) (*metadata.TopSongsResponse, error) {
	q := artistQuery(req.ID, req.Name)
	q.Set("count", strconv.Itoa(int(req.Count)))

	var out struct {
		Songs []struct {
			Name       string `json:"name"`
			ISRC       string `json:"isrc"`
			Artist     string `json:"artist"`
			Album      string `json:"album"`
			DurationMs uint32 `json:"durationMs"`
		} `json:"songs"`
	}
	ok, err := fetch("/api/metadata-agent/artist/top-songs", q, &out)
	if err != nil || !ok || len(out.Songs) == 0 {
		return nil, err
	}

	songs := make([]types.SongRef, 0, len(out.Songs))
	for _, s := range out.Songs {
		songs = append(songs, types.SongRef{
			Name:       s.Name,
			ISRC:       s.ISRC,
			Artists:    []types.ArtistRef{{Name: s.Artist}},
			Album:      s.Album,
			DurationMs: s.DurationMs,
		})
	}
	return &metadata.TopSongsResponse{Songs: songs}, nil
}

func init() { metadata.Register(&plugin{}) }

func main() {}
