(function (global) {
    'use strict';

    const sharedContextCache = new Map();
    const sharedRequestCache = new Map();
    const DEFAULT_CONTEXT_TIMEOUT_MS = 5000;

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

    function normalize(payload) {
        if (!payload || typeof payload !== 'object') {
            return null;
        }

        const streamTrackId = String(payload.streamTrackId || payload.stream_track_id || '').trim();
        const trackToken = String(payload.trackToken || payload.track_token || '').trim();
        if (!streamTrackId || !trackToken) {
            return null;
        }

        const context = { streamTrackId: streamTrackId, trackToken: trackToken };
        const md5origin = String(payload.md5origin || payload.md5_origin || '').trim();
        const mv = String(payload.mv || payload.mediaVersion || payload.media_version || '').trim();
        if (md5origin) {
            context.md5origin = md5origin;
        }
        if (mv) {
            context.mv = mv;
        }
        return context;
    }

    function resolveOptions(optionsOrContext) {
        if (optionsOrContext && typeof optionsOrContext === 'object'
            && (Object.hasOwn(optionsOrContext, 'context')
                || Object.hasOwn(optionsOrContext, 'type'))) {
            return optionsOrContext;
        }
        return { context: optionsOrContext };
    }

    function buildStreamUrl(deezerId, optionsOrContext) {
        const normalizedId = String(deezerId || '').trim();
        if (!normalizedId) {
            return '';
        }

        const options = resolveOptions(optionsOrContext);
        const context = normalize(options.context || null);
        const typeHint = String(options.type || '').trim().toLowerCase();
        const qs = new URLSearchParams();

        if (typeHint === 'episode') {
            qs.set('type', 'episode');
        }

        if (context) {
            qs.set('streamTrackId', context.streamTrackId);
            qs.set('trackToken', context.trackToken);
            if (context.md5origin) {
                qs.set('md5origin', context.md5origin);
            }
            if (context.mv) {
                qs.set('mv', context.mv);
            }
        }

        const query = qs.toString();
        return query
            ? '/api/deezer/stream/' + encodeURIComponent(normalizedId) + '?' + query
            : '/api/deezer/stream/' + encodeURIComponent(normalizedId);
    }

    function getCache(options) {
        return options && options.cache instanceof Map ? options.cache : sharedContextCache;
    }

    function getRequestCache(options) {
        return options && options.requests instanceof Map ? options.requests : sharedRequestCache;
    }

    function readContextFromElement(element) {
        if (!element?.dataset) {
            return null;
        }

        return normalize({
            streamTrackId: element.dataset.streamTrackId || '',
            trackToken: element.dataset.trackToken || '',
            md5origin: element.dataset.md5Origin || element.dataset.md5origin || '',
            mv: element.dataset.mediaVersion || element.dataset.mv || ''
        });
    }

    function applyContextToElement(element, payload) {
        const context = normalize(payload);
        if (!element?.dataset || !context) {
            return null;
        }

        element.dataset.streamTrackId = context.streamTrackId;
        element.dataset.trackToken = context.trackToken;
        element.dataset.md5Origin = context.md5origin || '';
        element.dataset.mediaVersion = context.mv || '';
        return context;
    }

    function warmContext(deezerId, payload, options) {
        const normalizedId = String(deezerId || '').trim();
        if (!normalizedId) {
            return null;
        }

        const context = normalize(payload);
        if (!context) {
            return null;
        }

        getCache(options).set(normalizedId, context);
        return context;
    }

    async function fetchContext(deezerId, options = {}) {
        const normalizedId = String(deezerId || '').trim();
        if (!normalizedId) {
            return null;
        }

        const cache = getCache(options);
        const requestCache = getRequestCache(options);

        const cached = cache.get(normalizedId);
        if (cached) {
            return normalize(cached);
        }

        if (requestCache.has(normalizedId)) {
            return await requestCache.get(normalizedId);
        }

        const request = (async function () {
            const timeout = buildTimeoutSignal(
                Number.isFinite(options.timeoutMs)
                    ? Number(options.timeoutMs)
                    : DEFAULT_CONTEXT_TIMEOUT_MS
            );
            try {
                const response = await fetch('/api/deezer/stream/context/' + encodeURIComponent(normalizedId), {
                    signal: timeout.signal
                });
                if (!response.ok) {
                    return null;
                }
                const payload = await response.json();
                if (payload?.available !== true) {
                    return null;
                }

                const context = normalize(payload);
                if (context) {
                    cache.set(normalizedId, context);
                }
                return context;
            } catch {
                return null;
            } finally {
                timeout.clear();
                requestCache.delete(normalizedId);
            }
        })();

        requestCache.set(normalizedId, request);
        return await request;
    }

    async function resolveStreamUrl(deezerId, options = {}) {
        const normalizedId = String(deezerId || '').trim();
        if (!normalizedId) {
            return '';
        }

        const hinted = normalize(options.context || readContextFromElement(options.element));
        if (hinted) {
            warmContext(normalizedId, hinted, options);
            if (options.element) {
                applyContextToElement(options.element, hinted);
            }
            return buildStreamUrl(normalizedId, { context: hinted, type: options.type });
        }

        const shouldFetchContext = options.fetchContext === true;
        if (!shouldFetchContext) {
            return buildStreamUrl(normalizedId, { type: options.type });
        }

        const context = await fetchContext(normalizedId, options);
        if (context) {
            if (options.element) {
                applyContextToElement(options.element, context);
            }
            return buildStreamUrl(normalizedId, { context: context, type: options.type });
        }

        return buildStreamUrl(normalizedId, { type: options.type });
    }

    global.DeezerPlaybackContext = {
        normalize: normalize,
        buildStreamUrl: buildStreamUrl,
        fetchContext: fetchContext,
        resolveStreamUrl: resolveStreamUrl,
        readContextFromElement: readContextFromElement,
        applyContextToElement: applyContextToElement,
        warmContext: warmContext
    };
})(globalThis);
