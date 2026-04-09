(function (global) {
    'use strict';

    const sharedContextCache = new Map();
    const sharedRequestCache = new Map();
    let sessionWarmRequest = null;
    let sessionWarmState = null;
    let startupWarmScheduled = false;

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

    async function warmSession() {
        if (typeof sessionWarmState === 'boolean') {
            return sessionWarmState;
        }

        if (sessionWarmRequest) {
            return await sessionWarmRequest;
        }

        sessionWarmRequest = (async function () {
            try {
                const response = await fetch('/api/deezer/stream/warmup/session');
                if (!response.ok) {
                    return false;
                }
                const payload = await response.json();
                sessionWarmState = payload?.authenticated === true;
                return sessionWarmState;
            } catch {
                sessionWarmState = false;
                return false;
            } finally {
                sessionWarmRequest = null;
            }
        })();

        return await sessionWarmRequest;
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
            try {
                const response = await fetch('/api/deezer/stream/context/' + encodeURIComponent(normalizedId));
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
                requestCache.delete(normalizedId);
            }
        })();

        requestCache.set(normalizedId, request);
        return await request;
    }

    async function fetchContexts(ids, options = {}) {
        const normalizedIds = Array.from(new Set(
            (Array.isArray(ids) ? ids : [])
                .map((id) => String(id || '').trim())
                .filter((id) => /^\d+$/.test(id))
        ));
        if (!normalizedIds.length) {
            return [];
        }

        const cache = getCache(options);
        const missingIds = normalizedIds.filter((id) => !cache.has(id));
        const resolvedItems = normalizedIds
            .map((id) => {
                const cached = normalize(cache.get(id));
                return cached ? { deezerId: id, context: cached, previewUrl: '' } : null;
            })
            .filter(Boolean);

        if (!missingIds.length) {
            return resolvedItems;
        }

        try {
            const response = await fetch('/api/deezer/stream/warmup/context', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ ids: missingIds })
            });
            if (!response.ok) {
                return resolvedItems;
            }

            const payload = await response.json();
            const items = Array.isArray(payload?.items) ? payload.items : [];
            for (const item of items) {
                const deezerId = String(item?.deezerId || '').trim();
                const context = warmContext(deezerId, item, options);
                if (!deezerId || !context) {
                    continue;
                }
                resolvedItems.push({
                    deezerId: deezerId,
                    context: context,
                    previewUrl: String(item?.previewUrl || '').trim()
                });
            }
        } catch {
            return resolvedItems;
        }

        return resolvedItems;
    }

    function getPlaybackTargetDetails(element) {
        if (!element) {
            return null;
        }

        if (element.classList?.contains('preview-controls')) {
            const row = element.closest('tr');
            const checkbox = row?.querySelector('.track-checkbox');
            const deezerId = String(
                row?.dataset?.availabilityDeezerId
                || checkbox?.dataset?.trackId
                || ''
            ).trim();
            return deezerId ? { deezerId: deezerId, target: element } : null;
        }

        const deezerId = String(
            element.dataset?.deezerId
            || element.dataset?.trackId
            || ''
        ).trim();
        return deezerId ? { deezerId: deezerId, target: element } : null;
    }

    function applyPreparedPreviewUrl(target, previewUrl) {
        const normalizedPreviewUrl = String(previewUrl || '').trim();
        if (!target || !normalizedPreviewUrl) {
            return;
        }

        if (target.dataset) {
            if (target.classList?.contains('preview-controls')) {
                target.dataset.preview = normalizedPreviewUrl;
                return;
            }
            target.dataset.previewUrl = normalizedPreviewUrl;
        }
    }

    function applyContextToPlaybackTarget(target, payload, previewUrl = '') {
        const context = applyContextToElement(target, payload);
        if (!context) {
            return null;
        }

        applyPreparedPreviewUrl(target, previewUrl);

        if (target?.classList?.contains('preview-controls')) {
            const row = target.closest('tr');
            const checkbox = row?.querySelector('.track-checkbox');
            applyContextToElement(row, context);
            applyContextToElement(checkbox, context);
            applyPreparedPreviewUrl(row, previewUrl);
            applyPreparedPreviewUrl(checkbox, previewUrl);
        }

        return context;
    }

    async function primePlaybackTargets(elements, options = {}) {
        const normalizedElements = Array.from(new Set(
            (Array.isArray(elements) ? elements : []).filter(Boolean)
        ));
        if (!normalizedElements.length) {
            return [];
        }

        if (options.includeSession !== false) {
            await warmSession();
        }

        const details = normalizedElements
            .map((element) => getPlaybackTargetDetails(element))
            .filter(Boolean);
        if (!details.length) {
            return [];
        }

        const ids = details.map((entry) => entry.deezerId);
        const items = await fetchContexts(ids, options);
        const byId = new Map(items.map((item) => [item.deezerId, item]));

        for (const entry of details) {
            const item = byId.get(entry.deezerId);
            if (!item?.context) {
                continue;
            }
            applyContextToPlaybackTarget(entry.target, item.context, item.previewUrl || '');
        }

        return items;
    }

    function runStartupWarmup() {
        void warmSession();
    }

    function scheduleStartupWarmup() {
        if (startupWarmScheduled) {
            return;
        }
        startupWarmScheduled = true;

        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', runStartupWarmup, { once: true });
            return;
        }

        runStartupWarmup();
    }

    function bindPlaybackHoverWarmup() {
        const selector = '.preview-controls, button.track-play, [data-home-trending-track="true"][data-deezer-id]';
        const handle = function (event) {
            const target = event.target?.closest?.(selector);
            if (!target) {
                return;
            }
            void primePlaybackTargets([target], { includeSession: true });
        };

        document.addEventListener('pointerover', handle, true);
        document.addEventListener('focusin', handle, true);
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
        warmSession: warmSession,
        fetchContext: fetchContext,
        fetchContexts: fetchContexts,
        resolveStreamUrl: resolveStreamUrl,
        readContextFromElement: readContextFromElement,
        applyContextToElement: applyContextToElement,
        warmContext: warmContext,
        primePlaybackTargets: primePlaybackTargets
    };

    scheduleStartupWarmup();
    bindPlaybackHoverWarmup();
})(globalThis);
