(function (global) {
    'use strict';

    const audio = new Audio();
    let currentSession = null;
    let requestSequence = 0;
    const externalResolutionCache = new Map();

    function trim(value) {
        return String(value || '').trim();
    }

    function normalizeState(state) {
        const normalized = trim(state).toLowerCase();
        if (normalized === 'requested' || normalized === 'playing' || normalized === 'ended' || normalized === 'error') {
            return normalized;
        }
        return 'idle';
    }

    function getFacade() {
        const facade = global.DeezerPlaybackFacade;
        return facade && typeof facade === 'object' ? facade : null;
    }

    function getResolver() {
        const resolver = global.DeezerResolver;
        return resolver && typeof resolver === 'object' ? resolver : null;
    }

    function getPlaybackContext() {
        const context = global.DeezerPlaybackContext;
        return context && typeof context === 'object' ? context : null;
    }

    function notify(message, type = 'warning') {
        const normalizedMessage = trim(message);
        if (!normalizedMessage) {
            return;
        }
        if (global.DeezSpoTag?.ui?.showToast) {
            global.DeezSpoTag.ui.showToast(normalizedMessage, { type });
            return;
        }
        console.warn(normalizedMessage);
    }

    function emitState(request, state, details = {}) {
        if (!request || typeof request !== 'object') {
            return;
        }

        const normalized = normalizeState(state);
        if (typeof request.onStateChange === 'function') {
            request.onStateChange(normalized, {
                audio,
                request,
                details,
                session: getSession()
            });
            return;
        }

        if (request.button && typeof request.setButtonState === 'function') {
            request.setButtonState(request.button, normalized, details);
        }
    }

    function buildDefaultKey(request) {
        const explicitKey = trim(request?.key || request?.trackKey);
        if (explicitKey) {
            return explicitKey;
        }

        const deezerId = trim(request?.deezerId);
        if (deezerId) {
            return `deezer:${deezerId}`;
        }

        const previewUrl = trim(request?.previewUrl);
        if (previewUrl) {
            return `preview:${previewUrl}`;
        }

        const spotifyUrl = trim(request?.spotifyUrl || request?.resolveUrl);
        if (spotifyUrl) {
            return `source:${spotifyUrl}`;
        }

        return `request:${Date.now()}:${Math.random().toString(16).slice(2)}`;
    }

    function buildResultKey(request, result) {
        const explicitKey = trim(result?.key);
        if (explicitKey) {
            return explicitKey;
        }

        const previewUrl = trim(result?.url);
        if (previewUrl && !trim(result?.deezerId)) {
            return `preview:${previewUrl}`;
        }

        const deezerId = trim(result?.deezerId);
        if (deezerId) {
            return `deezer:${deezerId}`;
        }

        return buildDefaultKey(request);
    }

    function configureAudioSource(sourceUrl, request) {
        const normalizedSourceUrl = trim(sourceUrl);
        if (!normalizedSourceUrl) {
            return false;
        }

        const facade = getFacade();
        if (facade && typeof facade.configurePreviewAudioSource === 'function') {
            facade.configurePreviewAudioSource(audio, normalizedSourceUrl, {
                volume: Number.isFinite(request?.volume) ? Number(request.volume) : undefined
            });
            return true;
        }

        audio.pause();
        audio.src = normalizedSourceUrl;
        if (typeof audio.load === 'function') {
            audio.load();
        }
        audio.volume = Number.isFinite(request?.volume) ? Number(request.volume) : 0.8;
        audio.currentTime = 0;
        return true;
    }

    async function resolveSpotifyTrack(request) {
        const spotifyUrl = trim(request.spotifyUrl);
        if (!spotifyUrl) {
            return null;
        }

        const facade = getFacade();
        try {
            if (facade) {
                const metadata = request.spotifyMetadata || request.metadata || null;
                if (metadata && typeof facade.resolveTrackBySpotifyRequest === 'function') {
                    return await facade.resolveTrackBySpotifyRequest({
                        link: spotifyUrl,
                        title: metadata.title || '',
                        artist: metadata.artist || '',
                        album: metadata.album || '',
                        isrc: metadata.isrc || '',
                        durationMs: metadata.durationMs || 0
                    });
                }
                if (typeof facade.resolveTrackBySpotifyUrl === 'function') {
                    return await facade.resolveTrackBySpotifyUrl(spotifyUrl, {
                        metadata: metadata || undefined
                    });
                }
            }

            const response = await fetch('/api/spotify/resolve-deezer?url=' + encodeURIComponent(spotifyUrl));
            if (!response.ok) {
                return null;
            }
            return await response.json();
        } catch {
            return null;
        }
    }

    function buildExternalResolutionCacheKey(request) {
        const resolverSource = trim(request?.resolverSource).toLowerCase();
        const resolveUrl = trim(request?.resolveUrl);
        if (!resolverSource || !resolveUrl) {
            return '';
        }
        return resolverSource + ':' + resolveUrl;
    }

    async function resolveExternalTrack(request) {
        const resolver = getResolver();
        const resolveUrl = trim(request.resolveUrl);
        const resolverSource = trim(request.resolverSource).toLowerCase();
        if (!resolver || typeof resolver.resolveTrack !== 'function' || !resolveUrl || !resolverSource) {
            return null;
        }

        const cacheKey = buildExternalResolutionCacheKey(request);
        if (cacheKey && externalResolutionCache.has(cacheKey)) {
            return externalResolutionCache.get(cacheKey);
        }

        try {
            const resolved = await resolver.resolveTrack(
                {
                    source: resolverSource,
                    url: resolveUrl,
                    title: trim(request.metadata?.title || ''),
                    artist: trim(request.metadata?.artist || ''),
                    album: trim(request.metadata?.album || ''),
                    isrc: trim(request.metadata?.isrc || ''),
                    durationMs: Number.parseInt(String(request.metadata?.durationMs || '0'), 10) || 0
                },
                {
                    attempts: 3,
                    baseDelayMs: 300,
                    spotifyResolverFirst: resolverSource === 'spotify'
                }
            );
            if (cacheKey) {
                externalResolutionCache.set(cacheKey, resolved || null);
            }
            return resolved;
        } catch {
            return null;
        }
    }

    async function resolvePlayableStreamUrl(deezerId, request) {
        const normalizedId = trim(deezerId);
        if (!normalizedId) {
            return '';
        }

        const facade = getFacade();
        if (facade && typeof facade.resolvePlayableStreamUrl === 'function') {
            return await facade.resolvePlayableStreamUrl(normalizedId, {
                element: request.element || request.button || null,
                cache: request.cache,
                requests: request.requests,
                fetchContext: request.fetchContext === true,
                type: request.type || ''
            });
        }

        const context = getPlaybackContext();
        if (context && typeof context.resolveStreamUrl === 'function') {
            return await context.resolveStreamUrl(normalizedId, {
                element: request.element || request.button || null,
                cache: request.cache,
                requests: request.requests,
                fetchContext: request.fetchContext === true,
                type: request.type || ''
            });
        }

        return '/api/deezer/stream/' + encodeURIComponent(normalizedId);
    }

    async function resolvePlayablePreviewUrl(previewUrl, request) {
        const normalizedPreviewUrl = trim(previewUrl);
        if (!normalizedPreviewUrl) {
            return '';
        }

        const facade = getFacade();
        if (facade && typeof facade.resolvePlayablePreviewUrl === 'function') {
            return await facade.resolvePlayablePreviewUrl(normalizedPreviewUrl, {
                element: request.element || request.button || null,
                cache: request.cache,
                requests: request.requests,
                fetchContext: request.fetchContext === true,
                type: request.type || ''
            });
        }

        return normalizedPreviewUrl;
    }

    async function resolveRequest(request) {
        if (typeof request.resolve === 'function') {
            const custom = await request.resolve({
                audio,
                request,
                session: getSession()
            });
            return custom && typeof custom === 'object'
                ? custom
                : { url: trim(custom) };
        }

        const previewUrl = trim(request.previewUrl);
        if (previewUrl) {
            return {
                url: await resolvePlayablePreviewUrl(previewUrl, request),
                deezerId: trim(request.deezerId),
                key: buildDefaultKey(request)
            };
        }

        let deezerId = trim(request.deezerId);
        if (!deezerId) {
            const externalResolved = await resolveExternalTrack(request);
            deezerId = trim(externalResolved?.deezerId);
        }
        if (!deezerId) {
            const spotifyResolved = await resolveSpotifyTrack(request);
            deezerId = trim(spotifyResolved?.deezerId);
        }
        if (!deezerId) {
            return { url: '', silentUnavailable: false };
        }

        const url = await resolvePlayableStreamUrl(deezerId, request);
        return {
            url,
            deezerId,
            key: `deezer:${deezerId}`
        };
    }

    function clearAudioSource() {
        audio.pause();
        audio.onended = null;
        audio.onerror = null;
        try {
            audio.removeAttribute('src');
            if (typeof audio.load === 'function') {
                audio.load();
            }
        } catch {
            // Best effort.
        }
    }

    async function stop(reason = 'idle') {
        if (!currentSession) {
            clearAudioSource();
            return false;
        }

        const previous = currentSession;
        currentSession = null;
        clearAudioSource();
        emitState(previous.request, reason, { reason });
        return true;
    }

    async function handleTerminalState(state, sessionId) {
        if (!currentSession || currentSession.id !== sessionId) {
            return;
        }

        const finished = currentSession;
        const nextFactory = typeof finished.request.getNextRequest === 'function'
            ? finished.request.getNextRequest
            : null;

        emitState(finished.request, state, { reason: state });
        currentSession = null;

        let nextRequest = null;
        if (nextFactory) {
            try {
                nextRequest = await nextFactory({
                    audio,
                    request: finished.request,
                    session: {
                        id: finished.id,
                        key: finished.key,
                        deezerId: finished.deezerId,
                        sourceKey: finished.sourceKey || '',
                        page: finished.page || '',
                        state
                    }
                });
            } catch (error) {
                console.warn('Failed to resolve next playback request:', error);
            }
        }

        emitState(finished.request, 'idle', { reason: state });

        if (nextRequest) {
            await play(nextRequest, { triggeredBy: state });
            return;
        }

        if (state === 'error') {
            notify(trim(finished.request.interruptedMessage || finished.request.startFailedMessage || 'Playback interrupted.'), 'warning');
        }
    }

    function getSession() {
        if (!currentSession) {
            return null;
        }

        return {
            id: currentSession.id,
            key: currentSession.key,
            deezerId: currentSession.deezerId,
            sourceKey: currentSession.sourceKey || '',
            page: currentSession.page || '',
            button: currentSession.request?.button || null,
            playing: !audio.paused,
            paused: audio.paused,
            currentTime: Number(audio.currentTime || 0),
            src: trim(audio.currentSrc || audio.src || '')
        };
    }

    async function play(request) {
        if (!request || typeof request !== 'object') {
            return false;
        }

        const requestId = ++requestSequence;
        const requestKey = buildDefaultKey(request);

        if (currentSession && currentSession.key === requestKey) {
            if (!audio.paused) {
                await stop('idle');
                return false;
            }

            const activeSession = currentSession;
            try {
                await audio.play();
                if (currentSession !== activeSession) {
                    return false;
                }
                emitState(currentSession.request, 'playing', { resumed: true });
                return true;
            } catch {
                notify(trim(request.startFailedMessage || 'Unable to start playback.'), 'warning');
                emitState(currentSession.request, 'error', { resumed: true });
                await stop('idle');
                return false;
            }
        }

        if (currentSession) {
            const previous = currentSession;
            currentSession = null;
            clearAudioSource();
            emitState(previous.request, 'idle', { reason: 'switch' });
        }

        const session = {
            id: requestId,
            key: requestKey,
            deezerId: trim(request.deezerId),
            sourceKey: trim(request.sourceKey),
            page: trim(request.page),
            request
        };
        currentSession = session;
        emitState(request, 'requested', { reason: 'start' });

        let resolved;
        try {
            resolved = await resolveRequest(request);
        } catch (error) {
            if (currentSession?.id !== requestId) {
                return false;
            }
            console.warn('Playback request resolution failed:', error);
            emitState(request, 'error', { phase: 'resolve' });
            currentSession = null;
            notify(trim(request.unavailableMessage || 'Preview unavailable.'), 'warning');
            return false;
        }

        if (currentSession?.id !== requestId) {
            return false;
        }

        if (!trim(resolved?.url)) {
            emitState(request, 'idle', { phase: 'resolve' });
            currentSession = null;
            if (!resolved?.silentUnavailable) {
                notify(trim(request.unavailableMessage || 'Preview unavailable.'), 'warning');
            }
            return false;
        }

        session.key = buildResultKey(request, resolved);
        session.deezerId = trim(resolved?.deezerId || session.deezerId);
        session.sourceKey = trim(request.sourceKey || resolved?.sourceKey || session.key);

        if (!configureAudioSource(resolved.url, request)) {
            emitState(request, 'idle', { phase: 'configure' });
            currentSession = null;
            notify(trim(request.unavailableMessage || 'Preview unavailable.'), 'warning');
            return false;
        }

        audio.onended = () => {
            void handleTerminalState('ended', requestId);
        };
        audio.onerror = () => {
            void handleTerminalState('error', requestId);
        };

        try {
            await audio.play();
            if (currentSession?.id !== requestId) {
                audio.pause();
                return false;
            }
            emitState(request, 'playing', { url: resolved.url });
            return true;
        } catch {
            if (currentSession?.id !== requestId) {
                return false;
            }
            emitState(request, 'error', { phase: 'play' });
            currentSession = null;
            notify(trim(request.startFailedMessage || 'Unable to start playback.'), 'warning');
            clearAudioSource();
            return false;
        }
    }

    global.DeezerUnifiedPlayback = {
        play,
        stop,
        getSession
    };
})(globalThis);
