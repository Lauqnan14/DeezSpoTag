(function initSpotifyUrlHelpers(globalObj) {
    'use strict';

    function buildSpotifyWebUrl(uri) {
        if (!uri) {
            return '';
        }
        const value = String(uri).trim();
        if (!value) {
            return '';
        }
        if (value.startsWith('http://') || value.startsWith('https://')) {
            return value;
        }
        if (value.startsWith('spotify:')) {
            const parts = value.split(':');
            if (parts.length >= 3 && parts[1] && parts[2]) {
                return `https://open.spotify.com/${parts[1]}/${parts[2]}`;
            }
        }
        return '';
    }

    function parseSpotifyUrl(url) {
        if (!url) {
            return null;
        }
        const trimmed = String(url).trim();
        if (trimmed.startsWith('spotify:')) {
            const uriParts = trimmed.split(':');
            if (uriParts.length >= 3 && uriParts[1] && uriParts[2]) {
                return { type: uriParts[1].toLowerCase(), id: uriParts[2] };
            }
        }

        const directMatch = /open\.spotify\.com\/(?:intl-[a-z]+\/)?(album|playlist|track|show|episode|artist|station)\/([a-z0-9]+)/i.exec(trimmed);
        if (directMatch) {
            return { type: directMatch[1].toLowerCase(), id: directMatch[2] };
        }

        try {
            const parsed = new URL(trimmed);
            const segments = parsed.pathname.split('/').filter(Boolean);
            const kindIndex = segments.findIndex((seg) => /^(album|playlist|track|show|episode|artist|station)$/i.test(seg));
            if (kindIndex >= 0 && segments[kindIndex + 1]) {
                return { type: segments[kindIndex].toLowerCase(), id: segments[kindIndex + 1] };
            }
        } catch {
            return null;
        }
        return null;
    }

    function buildSpotifyTrackMatchPayload(link, request) {
        return {
            link: String(link || '').trim(),
            title: String(request?.title || '').trim(),
            artist: String(request?.artist || '').trim(),
            album: String(request?.album || '').trim(),
            isrc: String(request?.isrc || '').trim(),
            durationMs: Number.isFinite(Number(request?.durationMs)) ? Number(request.durationMs) : 0
        };
    }

    function parseJson(raw) {
        try {
            return JSON.parse(String(raw || '{}'));
        } catch {
            return {};
        }
    }

    async function defaultFetchJson(url, options) {
        const response = await fetch(url, options);
        if (!response.ok) {
            return null;
        }
        return parseJson(await response.text());
    }

    function createDeezerSectionMatcher(options) {
        const state = {
            token: '',
            pollTimer: 0,
            startPromise: null,
            buttonsByIndex: new Map(),
            buttonsBySpotifyId: new Map()
        };

        const fetchJson = typeof options?.fetchJson === 'function'
            ? options.fetchJson
            : defaultFetchJson;
        const normalizeUrl = typeof options?.normalizeUrl === 'function'
            ? options.normalizeUrl
            : (value) => String(value || '').trim();
        const buildRequest = typeof options?.buildRequest === 'function'
            ? options.buildRequest
            : ((button, url) => ({ link: url || String(button?.dataset?.spotifyUrl || '').trim() }));

        function clearPollTimer() {
            if (state.pollTimer) {
                clearInterval(state.pollTimer);
                state.pollTimer = 0;
            }
        }

        function resetMaps() {
            state.buttonsByIndex.clear();
            state.buttonsBySpotifyId.clear();
        }

        function markTouchedUnavailable(buttons) {
            buttons.forEach((button) => {
                if (button instanceof HTMLElement && !button.dataset.deezerId) {
                    button.dataset.mappingState = 'unmapped';
                    if (typeof options?.onUnmatched === 'function') {
                        options.onUnmatched(button, null);
                    }
                }
            });
        }

        function applyMatches(matches) {
            if (!Array.isArray(matches) || matches.length === 0) {
                return;
            }

            matches.forEach((match) => {
                const deezerId = String(match?.deezerId || '').trim();
                const spotifyId = String(match?.spotifyId || '').trim();
                const status = String(match?.status || '').trim().toLowerCase();
                const index = Number.isFinite(Number(match?.index)) ? Number(match.index) : -1;
                const button = (spotifyId && state.buttonsBySpotifyId.get(spotifyId))
                    || (index >= 0 ? state.buttonsByIndex.get(index) : null);
                if (!(button instanceof HTMLElement)) {
                    return;
                }

                if (/^\d+$/.test(deezerId)) {
                    button.dataset.deezerId = deezerId;
                    button.dataset.mappingState = 'mapped';
                    if (typeof options?.onMatched === 'function') {
                        options.onMatched(button, deezerId, match);
                    }
                    return;
                }

                if (status === 'unmatched_final' || status === 'hard_mismatch') {
                    button.dataset.mappingState = 'unmapped';
                    if (typeof options?.onUnmatched === 'function') {
                        options.onUnmatched(button, match);
                    }
                }
            });
        }

        async function poll(token) {
            if (!token) {
                return;
            }

            try {
                const payload = await fetchJson(`/api/spotify/tracklist/matches?token=${encodeURIComponent(token)}`);
                if (payload?.available !== true) {
                    return;
                }

                applyMatches(payload.matches);
                const pending = Number(payload.pending || 0);
                if (pending <= 0 && token === state.token) {
                    clearPollTimer();
                }
            } catch {
                // Best-effort polling.
            }
        }

        async function start(entries) {
            if (!Array.isArray(entries) || entries.length === 0) {
                return;
            }

            if (state.startPromise) {
                await state.startPromise;
                return;
            }

            const startPromise = (async () => {
                resetMaps();
                const tracks = [];
                const touchedButtons = [];

                for (let index = 0; index < entries.length; index += 1) {
                    const entry = entries[index];
                    const button = entry?.button;
                    const request = buildRequest(button, entry?.url) || { link: entry?.url };
                    const link = normalizeUrl(String(request?.link || entry?.url || '').trim());
                    if (!link || !(button instanceof HTMLElement)) {
                        continue;
                    }

                    button.dataset.mappingState = 'mapping';
                    touchedButtons.push(button);
                    state.buttonsByIndex.set(index, button);

                    const parsedSpotify = parseSpotifyUrl(link);
                    const spotifyId = parsedSpotify?.type === 'track'
                        ? String(parsedSpotify.id || '').trim()
                        : '';
                    if (spotifyId && !state.buttonsBySpotifyId.has(spotifyId)) {
                        state.buttonsBySpotifyId.set(spotifyId, button);
                    }

                    tracks.push(buildSpotifyTrackMatchPayload(link, request));
                }

                if (tracks.length === 0) {
                    return;
                }

                try {
                    const sectionKey = typeof options?.sectionKey === 'function'
                        ? options.sectionKey()
                        : options?.sectionKey;
                    const payload = await fetchJson('/api/spotify/tracklist/section/match', {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json'
                        },
                        body: JSON.stringify({
                            sectionKey: String(sectionKey || 'spotify-section').trim(),
                            tracks
                        })
                    });

                    if (payload?.available !== true) {
                        markTouchedUnavailable(touchedButtons);
                        return;
                    }

                    applyMatches(payload.matches);
                    const token = String(payload?.matching?.token || '').trim();
                    const pendingCount = Number(payload?.matching?.pending || 0);
                    if (!token || pendingCount <= 0) {
                        return;
                    }

                    state.token = token;
                    clearPollTimer();
                    await poll(token);
                    state.pollTimer = setInterval(() => {
                        void poll(token);
                    }, Number(options?.pollIntervalMs || 1000));
                } catch {
                    markTouchedUnavailable(touchedButtons);
                }
            })();

            state.startPromise = startPromise;
            try {
                await startPromise;
            } finally {
                state.startPromise = null;
            }
        }

        return Object.freeze({
            start,
            applyMatches,
            waitForCurrent: async () => {
                if (state.startPromise) {
                    await state.startPromise;
                }
            },
            isRunning: () => Boolean(state.startPromise),
            stop: () => {
                clearPollTimer();
                state.token = '';
                resetMaps();
            }
        });
    }

    globalObj.SpotifyUrlHelpers = Object.freeze({
        buildSpotifyWebUrl,
        parseSpotifyUrl,
        buildSpotifyTrackMatchPayload,
        createDeezerSectionMatcher
    });
})(globalThis);
