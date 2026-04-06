(function (global) {
    'use strict';

    const resolveCache = new Map();
    const resolveInFlight = new Map();

    function normalizeMetadata(metadata) {
        if (!metadata || typeof metadata !== 'object') {
            return null;
        }

        const title = String(metadata.title || '').trim();
        const artist = String(metadata.artist || '').trim();
        const album = String(metadata.album || '').trim();
        const isrc = String(metadata.isrc || '').trim();
        const durationMsRaw = Number.parseInt(String(metadata.durationMs || '0'), 10);
        const durationMs = Number.isFinite(durationMsRaw) && durationMsRaw > 0 ? durationMsRaw : 0;

        if (!title && !artist && !album && !isrc && durationMs <= 0) {
            return null;
        }

        return {
            title,
            artist,
            album,
            isrc,
            durationMs
        };
    }

    function buildResolveCacheKey(url, metadata) {
        if (!url) {
            return '';
        }
        const normalized = normalizeMetadata(metadata);
        if (!normalized) {
            return String(url);
        }
        return JSON.stringify([String(url), normalized.title, normalized.artist, normalized.album, normalized.isrc, normalized.durationMs]);
    }

    async function fetchResolveDeezerByMetadata(url, metadata) {
        if (!url) {
            return null;
        }

        const params = new URLSearchParams();
        params.set('url', String(url));
        const normalized = normalizeMetadata(metadata);
        if (normalized?.title) {
            params.set('title', normalized.title);
        }
        if (normalized?.artist) {
            params.set('artist', normalized.artist);
        }
        if (normalized?.album) {
            params.set('album', normalized.album);
        }
        if (normalized?.isrc) {
            params.set('isrc', normalized.isrc);
        }
        if (normalized?.durationMs && normalized.durationMs > 0) {
            params.set('durationMs', String(normalized.durationMs));
        }

        const response = await fetch('/api/resolve/deezer?' + params.toString());
        if (!response.ok) {
            return null;
        }
        const payload = await response.json();
        const deezerId = String(payload?.deezerId || '').trim();
        if (!deezerId || deezerId === '0') {
            return null;
        }
        return {
            available: true,
            type: 'track',
            deezerId,
            deezerUrl: 'https://www.deezer.com/track/' + deezerId,
            resolvedBy: 'metadata-fallback'
        };
    }

    async function resolveTrackBySpotifyRequest(request, options = {}) {
        if (!request || typeof request !== 'object') {
            return null;
        }

        const url = String(request.link || request.url || '').trim();
        if (!url) {
            return null;
        }

        return await resolveTrackBySpotifyUrl(url, {
            ...options,
            metadata: {
                title: request.title || '',
                artist: request.artist || '',
                album: request.album || '',
                isrc: request.isrc || '',
                durationMs: request.durationMs || 0
            },
        });
    }

    async function resolveTrackBySpotifyUrl(url, options = {}) {
        const normalizedUrl = String(url || '').trim();
        if (!normalizedUrl) {
            return null;
        }

        const metadata = normalizeMetadata(options.metadata || null);
        const key = buildResolveCacheKey(normalizedUrl, metadata);
        if (key && resolveCache.has(key)) {
            return resolveCache.get(key);
        }
        if (key && resolveInFlight.has(key)) {
            return await resolveInFlight.get(key);
        }

        const request = (async function () {
            try {
                let resolved = null;
                if (global.DeezerResolver && typeof global.DeezerResolver.resolveTrack === 'function') {
                    resolved = await global.DeezerResolver.resolveTrack(
                        {
                            source: 'spotify',
                            url: normalizedUrl,
                            title: metadata?.title || '',
                            artist: metadata?.artist || '',
                            album: metadata?.album || '',
                            isrc: metadata?.isrc || '',
                            durationMs: metadata?.durationMs || 0
                        },
                        {
                            attempts: 2,
                            baseDelayMs: 250,
                            timeoutMs: 3000,
                            spotifyResolverFirst: true
                        }
                    );
                } else {
                    const response = await fetch('/api/spotify/resolve-deezer?url=' + encodeURIComponent(normalizedUrl));
                    if (response.ok) {
                        resolved = await response.json();
                    }
                }

                if (resolved?.type === 'track' && resolved?.available === true && resolved?.deezerId) {
                    if (key) {
                        resolveCache.set(key, resolved);
                    }
                    return resolved;
                }

                const fallbackResolved = await fetchResolveDeezerByMetadata(normalizedUrl, metadata);
                if (fallbackResolved?.deezerId) {
                    if (key) {
                        resolveCache.set(key, fallbackResolved);
                    }
                    return fallbackResolved;
                }

                return null;
            } catch {
                return null;
            } finally {
                if (key) {
                    resolveInFlight.delete(key);
                }
            }
        })();

        if (key) {
            resolveInFlight.set(key, request);
        }
        return await request;
    }

    async function resolvePlayableStreamUrl(deezerId, options = {}) {
        const normalizedId = String(deezerId || '').trim();
        if (!normalizedId) {
            return '';
        }

        if (global.DeezerResolver && typeof global.DeezerResolver.resolvePlayableStreamUrl === 'function') {
            return await global.DeezerResolver.resolvePlayableStreamUrl(
                { deezerId: normalizedId, element: options.element },
                {
                    cache: options.cache,
                    requests: options.requests,
                    fetchContext: options.fetchContext === true,
                    type: options.type || ''
                }
            );
        }

        if (global.DeezerPlaybackContext && typeof global.DeezerPlaybackContext.resolveStreamUrl === 'function') {
            return await global.DeezerPlaybackContext.resolveStreamUrl(normalizedId, {
                element: options.element,
                cache: options.cache,
                requests: options.requests,
                fetchContext: options.fetchContext === true,
                type: options.type || ''
            });
        }

        return '/api/deezer/stream/' + encodeURIComponent(normalizedId);
    }

    function parsePlayablePreviewUrl(previewUrl) {
        const normalizedPreviewUrl = String(previewUrl || '').trim();
        if (!normalizedPreviewUrl) {
            return null;
        }

        try {
            const url = new URL(normalizedPreviewUrl, global.location?.origin || 'http://localhost');
            const match = url.pathname.match(/^\/api\/deezer\/stream\/(\d+)$/i);
            if (!match) {
                return null;
            }

            return {
                deezerId: match[1],
                hasContext: /[?&](trackToken|streamTrackId)=/i.test(url.search)
            };
        } catch {
            const path = normalizedPreviewUrl.split(/[?#]/, 1)[0];
            const match = path.match(/^\/api\/deezer\/stream\/(\d+)$/i);
            if (!match) {
                return null;
            }

            return {
                deezerId: match[1],
                hasContext: /[?&](trackToken|streamTrackId)=/i.test(normalizedPreviewUrl)
            };
        }
    }

    async function resolvePlayablePreviewUrl(previewUrl, options = {}) {
        const normalizedPreviewUrl = String(previewUrl || '').trim();
        if (!normalizedPreviewUrl) {
            return '';
        }

        const parsed = parsePlayablePreviewUrl(normalizedPreviewUrl);
        if (!parsed) {
            return normalizedPreviewUrl;
        }

        if (parsed.hasContext) {
            return normalizedPreviewUrl;
        }

        const resolvedStreamUrl = await resolvePlayableStreamUrl(parsed.deezerId, {
            ...options,
            fetchContext: true
        });
        if (!resolvedStreamUrl) {
            return '';
        }

        const resolvedPreview = parsePlayablePreviewUrl(resolvedStreamUrl);
        if (resolvedPreview && !resolvedPreview.hasContext) {
            return '';
        }

        return resolvedStreamUrl;
    }

    function clearCaches() {
        resolveCache.clear();
        resolveInFlight.clear();
    }

    global.DeezerPlaybackFacade = {
        resolveTrackBySpotifyUrl: resolveTrackBySpotifyUrl,
        resolveTrackBySpotifyRequest: resolveTrackBySpotifyRequest,
        resolvePlayableStreamUrl: resolvePlayableStreamUrl,
        resolvePlayablePreviewUrl: resolvePlayablePreviewUrl,
        clearCaches: clearCaches
    };
})(globalThis);
