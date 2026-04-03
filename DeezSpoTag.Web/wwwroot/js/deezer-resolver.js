(function (global) {
    'use strict';

    const TRANSIENT_STATUS_CODES = new Set([408, 425, 429, 500, 502, 503, 504]);
    const DEFAULT_ATTEMPTS = 3;
    const DEFAULT_BASE_DELAY_MS = 250;
    const DEFAULT_TIMEOUT_MS = 8000;
    const MAX_DELAY_MS = 2000;

    function delay(ms) {
        return new Promise((resolve) => setTimeout(resolve, ms));
    }

    function parseJsonText(raw) {
        const text = String(raw || '').trim();
        if (!text || text === 'undefined') {
            return null;
        }
        try {
            return JSON.parse(text);
        } catch {
            return null;
        }
    }

    function buildTimeoutSignal(timeoutMs) {
        if (!Number.isFinite(timeoutMs) || timeoutMs <= 0 || typeof AbortController === 'undefined') {
            return { signal: undefined, clear: () => {} };
        }

        const controller = new AbortController();
        const timer = setTimeout(() => {
            controller.abort(new DOMException('Request timeout', 'AbortError'));
        }, timeoutMs);

        return {
            signal: controller.signal,
            clear: () => clearTimeout(timer)
        };
    }

    async function fetchJsonWithRetry(url, options = {}) {
        const attempts = Number.isFinite(options.attempts)
            ? Math.max(1, Math.trunc(options.attempts))
            : DEFAULT_ATTEMPTS;
        const baseDelayMs = Number.isFinite(options.baseDelayMs)
            ? Math.max(0, Math.trunc(options.baseDelayMs))
            : DEFAULT_BASE_DELAY_MS;
        const timeoutMs = Number.isFinite(options.timeoutMs)
            ? Math.max(1, Math.trunc(options.timeoutMs))
            : DEFAULT_TIMEOUT_MS;
        const fetchOptions = options.fetchOptions || {};

        let lastResponse = null;
        for (let attempt = 1; attempt <= attempts; attempt += 1) {
            const timeout = buildTimeoutSignal(timeoutMs);
            try {
                const response = await fetch(url, {
                    ...fetchOptions,
                    signal: fetchOptions.signal || timeout.signal
                });
                lastResponse = response;
                if (response.ok) {
                    const payload = parseJsonText(await response.text());
                    return {
                        ok: true,
                        status: response.status,
                        payload,
                        transientFailure: false
                    };
                }

                const isTransient = TRANSIENT_STATUS_CODES.has(response.status);
                if (!isTransient || attempt >= attempts) {
                    const payload = parseJsonText(await response.text());
                    return {
                        ok: false,
                        status: response.status,
                        payload,
                        transientFailure: isTransient
                    };
                }
            } catch (error) {
                if (attempt >= attempts) {
                    return {
                        ok: false,
                        status: 0,
                        payload: null,
                        transientFailure: true,
                        error
                    };
                }
            } finally {
                timeout.clear();
            }

            await delay(Math.min(MAX_DELAY_MS, baseDelayMs * attempt));
        }

        return {
            ok: false,
            status: lastResponse?.status || 0,
            payload: null,
            transientFailure: true
        };
    }

    function normalizeResolvePayload(payload, fallbackReasonCode = 'no_match') {
        if (!payload || typeof payload !== 'object') {
            return {
                available: false,
                deezerId: '',
                type: '',
                reasonCode: fallbackReasonCode
            };
        }

        const deezerId = String(payload.deezerId || '').trim();
        const available = payload.available === true && /^\d+$/.test(deezerId);
        return {
            ...payload,
            available,
            deezerId: available ? deezerId : deezerId,
            reasonCode: String(payload.reasonCode || payload.reason || (available ? '' : fallbackReasonCode)).trim()
        };
    }

    async function resolveSpotifyTrackToDeezer(url, options = {}) {
        const response = await fetchJsonWithRetry(
            `/api/spotify/resolve-deezer?url=${encodeURIComponent(url)}`,
            options
        );

        if (!response.ok) {
            return {
                available: false,
                deezerId: '',
                type: 'track',
                reasonCode: response.transientFailure ? 'transient_upstream' : 'resolver_unavailable'
            };
        }

        return normalizeResolvePayload(response.payload, 'no_match');
    }

    function buildGenericResolveQuery(input) {
        const qs = new URLSearchParams();
        qs.set('url', input.url);
        if (input.title) {
            qs.set('title', input.title);
        }
        if (input.artist) {
            qs.set('artist', input.artist);
        }
        if (input.album) {
            qs.set('album', input.album);
        }
        if (input.isrc) {
            qs.set('isrc', input.isrc);
        }
        if (Number.isFinite(input.durationMs) && input.durationMs > 0) {
            qs.set('durationMs', String(Math.round(input.durationMs)));
        }
        if (input.includeMeta === true) {
            qs.set('includeMeta', 'true');
        }
        return qs.toString();
    }

    async function resolveGenericToDeezer(input, options = {}) {
        const query = buildGenericResolveQuery(input);
        const response = await fetchJsonWithRetry(`/api/resolve/deezer?${query}`, options);
        if (!response.ok) {
            return {
                available: false,
                deezerId: '',
                reasonCode: response.transientFailure ? 'transient_upstream' : 'resolver_unavailable'
            };
        }
        return normalizeResolvePayload(response.payload, 'no_match');
    }

    async function resolveTrack(input, options = {}) {
        const normalizedUrl = String(input?.url || '').trim();
        if (!normalizedUrl) {
            return {
                available: false,
                deezerId: '',
                reasonCode: 'missing_url'
            };
        }

        const normalizedSource = String(input?.source || '').trim().toLowerCase();
        const retryOptions = {
            attempts: options.attempts,
            baseDelayMs: options.baseDelayMs,
            fetchOptions: options.fetchOptions
        };

        if (normalizedSource === 'spotify' && options.spotifyResolverFirst !== false) {
            const spotifyResolved = await resolveSpotifyTrackToDeezer(normalizedUrl, retryOptions);
            if (spotifyResolved.available) {
                return spotifyResolved;
            }
        }

        return await resolveGenericToDeezer({
            url: normalizedUrl,
            title: input?.title || '',
            artist: input?.artist || '',
            album: input?.album || '',
            isrc: input?.isrc || '',
            durationMs: input?.durationMs || 0,
            includeMeta: input?.includeMeta === true
        }, retryOptions);
    }

    async function resolvePlayableStreamUrl(input, options = {}) {
        const deezerId = String(input?.deezerId || '').trim();
        if (!deezerId) {
            return '';
        }

        if (global.DeezerPlaybackContext && typeof global.DeezerPlaybackContext.resolveStreamUrl === 'function') {
            return await global.DeezerPlaybackContext.resolveStreamUrl(deezerId, {
                element: input?.element || null,
                context: input?.context || null,
                type: input?.type || '',
                cache: options.cache,
                requests: options.requests,
                fetchContext: options.fetchContext !== false
            });
        }

        return `/api/deezer/stream/${encodeURIComponent(deezerId)}`;
    }

    global.DeezerResolver = {
        fetchJsonWithRetry,
        resolveTrack,
        resolveSpotifyTrackToDeezer,
        resolveGenericToDeezer,
        resolvePlayableStreamUrl
    };
})(globalThis);
