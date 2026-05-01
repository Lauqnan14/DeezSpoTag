function getTimeGreeting(date = new Date()) {
    const hour = date.getHours();
    if (hour < 5) return 'Good night';
    if (hour < 12) return 'Good morning';
    if (hour < 17) return 'Good afternoon';
    if (hour < 21) return 'Good evening';
    return 'Good night';
}

function setHomeGreeting() {
    const el = document.getElementById('home-greeting');
    if (!el) return;
    el.textContent = getTimeGreeting();
}
// Utility function to check if input is a valid URL (like deezspotag)
function isValidURL(string) {
    if (!string || typeof string !== 'string') {
        return false;
    }
    if (typeof URL.canParse === 'function' && !URL.canParse(string)) {
        return false;
    }
    if (!/^https?:\/\//i.test(string)) {
        return false;
    }
    const url = new URL(string);
    return url.protocol === 'http:' || url.protocol === 'https:';
}


const showToast = globalThis.HomeViewHelpers.showToast;

const HOME_TRENDING_SPOTIFY_SOURCE_ID = 'home-trending-songs';
const homeTrendingContextWarmupInFlight = new Map();
const spotifyHomeItemCache = new Map();
const homeTrendingWatchMetaCache = new Map();
let spotifyHomeItemSeq = 0;
let homeTrendingWatchSeq = 0;
let homeLoadRequestSeq = 0;
const homeTrendingPreviewState = {
    trackKey: null,
    button: null,
    queueButtons: [],
    queueIndex: -1
};
const homeDeezerPlaybackContextCache = new Map();
const homeDeezerPlaybackContextRequests = new Map();
const homeTrendingUnmappedCooldownUntil = new Map();
let homeTrendingMatchWarmupTimer = 0;
const HOME_TRENDING_UNMAPPED_RETRY_COOLDOWN_MS = 20000;
const HOME_TRENDING_MATCH_SECTION_KEY = 'home-trending-songs';
let homeTrendingMatchPollTimer = 0;
let homeTrendingMatchToken = '';
let homeTrendingMatchStartPromise = null;
const homeTrendingMatchButtonsByIndex = new Map();
const homeTrendingMatchButtonsBySpotifyId = new Map();

// Lazy image loading with IntersectionObserver for faster initial render
const lazyImageObserver = new IntersectionObserver((entries) => {
    entries.forEach(entry => {
        if (entry.isIntersecting) {
            const el = entry.target;
            const bgUrl = el.dataset.lazyBg;
            if (bgUrl) {
                el.style.backgroundImage = `url('${bgUrl}')`;
                delete el.dataset.lazyBg;
            }
            lazyImageObserver.unobserve(el);
        }
    });
}, { rootMargin: '100px' }); // Load images 100px before they enter viewport

function observeLazyImages(container) {
    if (!container) return;
    const lazyElements = container.querySelectorAll('[data-lazy-bg]');
    lazyElements.forEach(el => lazyImageObserver.observe(el));
}

function getBrowserTimeZone() {
    const timeZone = Intl.DateTimeFormat?.().resolvedOptions?.().timeZone;
    return timeZone || 'America/New_York';
}

function getSpotifyUrlHelpers() {
    const helpers = globalThis.SpotifyUrlHelpers;
    if (helpers && typeof helpers.buildSpotifyWebUrl === 'function' && typeof helpers.parseSpotifyUrl === 'function') {
        return helpers;
    }

    return {
        buildSpotifyWebUrl(uri) {
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
        },
        parseSpotifyUrl(url) {
            if (!url) {
                return null;
            }
            const trimmed = String(url).trim();
            if (trimmed.startsWith('spotify:')) {
                const parts = trimmed.split(':');
                if (parts.length >= 3 && parts[1] && parts[2]) {
                    return { type: parts[1].toLowerCase(), id: parts[2] };
                }
            }
            const directMatch = /open\.spotify\.com\/(?:intl-[a-z]+\/)?(album|playlist|track|show|episode|artist|station)\/([a-z0-9]+)/i.exec(trimmed);
            if (directMatch) {
                return { type: directMatch[1].toLowerCase(), id: directMatch[2] };
            }
            try {
                const parsed = new URL(trimmed);
                const segments = parsed.pathname.split('/').filter(Boolean);
                const kindIndex = segments.findIndex((segment) => /^(album|playlist|track|show|episode|artist|station)$/i.test(segment));
                if (kindIndex >= 0 && segments[kindIndex + 1]) {
                    return { type: segments[kindIndex].toLowerCase(), id: segments[kindIndex + 1] };
                }
            } catch {
                return null;
            }
            return null;
        }
    };
}

const spotifyUrlHelpers = getSpotifyUrlHelpers();

async function openSpotifyItem(item) {
    if (!item) {
        return;
    }
    const itemType = (item.type || '').toLowerCase();
    if (itemType === 'category') {
        await openSpotifyBrowseCategory(item);
        return;
    }
    const url = spotifyUrlHelpers.buildSpotifyWebUrl(item.uri || '');
    const parsed = spotifyUrlHelpers.parseSpotifyUrl(url);
    const fallbackId = item.id || '';
    const fallbackType = itemType || '';
    const resolvedType = parsed?.type || fallbackType;
    const resolvedId = parsed?.id || fallbackId;

    if (resolvedType === 'artist' && resolvedId) {
        openSpotifyArtist(resolvedId, encodeURIComponent(item.name || ''));
        return;
    }
    if (resolvedType && resolvedId) {
        globalThis.location.href = `/Tracklist?id=${encodeURIComponent(resolvedId)}&type=${encodeURIComponent(resolvedType)}&source=spotify`;
        return;
    }
    showToast('Spotify item unavailable in-app (missing id/type).', 'warning');
}

async function openSpotifyBrowseCategory(item) {
    const id = item.categoryId || item.id || '';
    const uri = item.uri || '';
    const title = item.name || '';
    if (!id && !uri) {
        return;
    }

    const params = new URLSearchParams();
    if (id) params.set('categoryId', id);
    if (uri) params.set('uri', uri);
    if (title) params.set('title', title);
    globalThis.location.href = `/Spotify/Browse?${params.toString()}`;
}

const setHomeTrendingPreviewButtonState = globalThis.HomeViewHelpers.setHomeTrendingPreviewButtonState;
const homePlaybackState = globalThis.DeezerPlaybackState;

function setHomeTrendingPlaybackState(button, state) {
    if (homePlaybackState && typeof homePlaybackState.transitionButtonState === 'function') {
        homePlaybackState.transitionButtonState(button, state, {
            setPlaying: setHomeTrendingPreviewButtonState,
            clear: () => {
                if (globalThis.HomeViewHelpers && typeof globalThis.HomeViewHelpers.clearHomeTrendingPlayingMarkers === 'function') {
                    globalThis.HomeViewHelpers.clearHomeTrendingPlayingMarkers(null);
                }
            },
            onTransition: (targetButton, nextState) => {
                if (nextState === 'requested') {
                    setHomeTrendingPreviewButtonState(targetButton, true);
                }
            }
        });
        return;
    }

    const isPlaying = String(state || '').toLowerCase() === 'playing';
    setHomeTrendingPreviewButtonState(button, isPlaying);
}

function clearHomeTrendingPreviewButton() {
    if (!homeTrendingPreviewState.button) {
        return;
    }
    setHomeTrendingPreviewButtonState(homeTrendingPreviewState.button, false);
    homeTrendingPreviewState.button = null;
}

function getHomePlaybackSession() {
    const player = globalThis.DeezerUnifiedPlayback;
    if (!player || typeof player.getSession !== 'function') {
        return null;
    }
    return player.getSession();
}

function getHomeTrendingPlaybackButtons() {
    return Array.from(
        document.querySelectorAll('#home-sections .home-top-song-item__play[data-home-trending-track="true"]')
    );
}

function isConnectedHomeTrendingPlaybackButton(button) {
    return button instanceof HTMLElement
        && button.isConnected
        && button.matches('.home-top-song-item__play[data-home-trending-track="true"]');
}

function findHomeTrendingButtonByDatasetValue(buttons, key, value) {
    const expected = String(value || '').trim();
    if (!expected) {
        return null;
    }

    return buttons.find((button) => String(button.dataset?.[key] || '').trim() === expected) || null;
}

function findHomeTrendingButtonBySourceKey(buttons, sourceKey) {
    const key = String(sourceKey || '').trim();
    if (!key) {
        return null;
    }

    const lowerKey = key.toLowerCase();
    if (lowerKey.startsWith('preview:')) {
        return findHomeTrendingButtonByDatasetValue(buttons, 'previewUrl', key.slice('preview:'.length));
    }

    if (lowerKey.startsWith('source:')) {
        return findHomeTrendingButtonByDatasetValue(buttons, 'spotifyUrl', key.slice('source:'.length));
    }

    return buttons.find((button) =>
        String(button.dataset?.spotifyUrl || '').trim() === key
        || String(button.dataset?.previewUrl || '').trim() === key
        || String(button.dataset?.deezerId || '').trim() === key) || null;
}

function findHomeTrendingPlaybackButtonFromSession(session) {
    if (!session) {
        return null;
    }

    const sessionButton = session.button;
    if (isConnectedHomeTrendingPlaybackButton(sessionButton)) {
        return sessionButton;
    }

    const buttons = getHomeTrendingPlaybackButtons();
    if (!buttons.length) {
        return null;
    }

    const deezerButton = findHomeTrendingButtonByDatasetValue(buttons, 'deezerId', session.deezerId);
    if (deezerButton) {
        return deezerButton;
    }

    return findHomeTrendingButtonBySourceKey(buttons, session.sourceKey);
}

function syncHomeTrendingPlaybackFromSession() {
    const session = getHomePlaybackSession();
    const isHomeSession = String(session?.page || '').trim().toLowerCase() === 'home';
    if (!isHomeSession) {
        clearHomeTrendingPreviewButton();
        resetHomeTrendingQueue();
        return;
    }

    const playButton = findHomeTrendingPlaybackButtonFromSession(session);
    if (!playButton) {
        clearHomeTrendingPreviewButton();
        resetHomeTrendingQueue();
        return;
    }

    const nextState = session?.playing ? 'playing' : 'requested';
    setHomeTrendingPlaybackState(playButton, nextState);
    homeTrendingPreviewState.button = playButton;
    homeTrendingPreviewState.trackKey = String(session?.key || '').trim() || null;
    seedHomeTrendingQueue(playButton);
}

function resolveHomeTrendingPlayButton(target) {
    if (!target) {
        return null;
    }
    if (target.classList?.contains('home-top-song-item__play')) {
        return target;
    }
    const nested = target.querySelector?.('.home-top-song-item__play');
    if (nested) {
        return nested;
    }
    const row = target.closest?.('.home-top-song-item');
    return row ? row.querySelector('.home-top-song-item__play') : null;
}

function buildHomeTrendingPlaybackQueue(startButton) {
    const section = startButton?.closest?.('.home-section');
    if (!section) {
        return [];
    }
    return Array.from(section.querySelectorAll('.home-top-song-item__play'))
        .filter((button) => {
            if (!(button instanceof HTMLElement)) {
                return false;
            }
            if (!button.isConnected) {
                return false;
            }
            const deezerId = (button.dataset?.deezerId || '').trim();
            const spotifyUrl = (button.dataset?.spotifyUrl || '').trim();
            const previewUrl = (button.dataset?.previewUrl || '').trim();
            return Boolean(deezerId || spotifyUrl || previewUrl);
        });
}

function resetHomeTrendingQueue() {
    homeTrendingPreviewState.queueButtons = [];
    homeTrendingPreviewState.queueIndex = -1;
}

function seedHomeTrendingQueue(startButton) {
    const queue = buildHomeTrendingPlaybackQueue(startButton);
    if (!queue.length) {
        resetHomeTrendingQueue();
        return;
    }
    homeTrendingPreviewState.queueButtons = queue;
    const index = queue.indexOf(startButton);
    homeTrendingPreviewState.queueIndex = Math.max(index, 0);
}

function getNextHomeTrendingQueueButton() {
    const queue = homeTrendingPreviewState.queueButtons;
    if (!Array.isArray(queue) || queue.length === 0) {
        return null;
    }

    for (let index = homeTrendingPreviewState.queueIndex + 1; index < queue.length; index++) {
        const candidate = queue[index];
        if (candidate?.isConnected) {
            homeTrendingPreviewState.queueIndex = index;
            return candidate;
        }
    }

    resetHomeTrendingQueue();
    return null;
}

function normalizeHomeDeezerPlaybackContext(payload) {
    return globalThis.HomeViewHelpers.normalizeHomeDeezerPlaybackContext(payload);
}

function hasHomeTrendingPlaybackContext(button) {
    if (!button?.dataset) {
        return false;
    }
    return Boolean(
        String(button.dataset.streamTrackId || '').trim()
        && String(button.dataset.trackToken || '').trim()
    );
}

async function warmHomeTrendingPlaybackContext(button, deezerId) {
    const normalizedId = String(deezerId || '').trim();
    if (!normalizedId || !button) {
        return null;
    }
    if (hasHomeTrendingPlaybackContext(button)) {
        button.dataset.mappingState = 'context-ready';
        return true;
    }

    if (homeTrendingContextWarmupInFlight.has(normalizedId)) {
        return await homeTrendingContextWarmupInFlight.get(normalizedId);
    }

    const warmupPromise = (async () => {
        try {
            const context = await fetchHomeDeezerPlaybackContext(normalizedId);
            if (context && globalThis.DeezerPlaybackContext && typeof globalThis.DeezerPlaybackContext.applyContextToElement === 'function') {
                globalThis.DeezerPlaybackContext.applyContextToElement(button, context);
                button.dataset.mappingState = 'context-ready';
                return true;
            }
        } catch {
            // Best-effort warmup.
        } finally {
            homeTrendingContextWarmupInFlight.delete(normalizedId);
        }
        if (button.dataset.mappingState !== 'context-ready') {
            button.dataset.mappingState = 'mapped';
        }
        return false;
    })();

    homeTrendingContextWarmupInFlight.set(normalizedId, warmupPromise);
    return await warmupPromise;
}

function buildHomeDeezerStreamUrl(deezerId, context) {
    return globalThis.HomeViewHelpers.buildHomeDeezerStreamUrl(deezerId, context);
}

async function fetchHomeDeezerPlaybackContext(deezerId) {
    return await globalThis.HomeViewHelpers.fetchHomeDeezerPlaybackContext(
        deezerId,
        homeDeezerPlaybackContextCache,
        homeDeezerPlaybackContextRequests
    );
}

function getDeezerPlaybackFacade() {
    const facade = globalThis.DeezerPlaybackFacade;
    if (!facade || typeof facade !== 'object') {
        return null;
    }
    if (typeof facade.resolveTrackBySpotifyUrl !== 'function') {
        return null;
    }
    if (typeof facade.resolvePlayableStreamUrl !== 'function') {
        return null;
    }
    if (typeof facade.resolvePlayablePreviewUrl !== 'function') {
        return null;
    }
    return facade;
}

function buildSpotifyTrackResolveRequestFromTrendingCard(button) {
    if (!button?.dataset) {
        return null;
    }

    const spotifyUrl = String(button.dataset.spotifyUrl || '').trim();
    const title = String(button.dataset.trackTitle || '').trim();
    const artist = String(button.dataset.trackArtist || '').trim();
    const album = String(button.dataset.trackAlbum || '').trim();
    const isrc = String(button.dataset.trackIsrc || '').trim();
    const durationMsRaw = Number.parseInt(String(button.dataset.trackDurationMs || '0'), 10);
    const durationMs = Number.isFinite(durationMsRaw) && durationMsRaw > 0 ? durationMsRaw : 0;

    if (!spotifyUrl || (!title && !artist && !album && !isrc && durationMs <= 0)) {
        return null;
    }

    return {
        link: spotifyUrl,
        title,
        artist,
        album,
        isrc,
        durationMs
    };
}

async function resolveHomeTrendingSpotifyTrackToDeezer(request) {
    const link = String(request?.link || '').trim();
    if (!link) {
        return null;
    }

    const payload = {
        link,
        title: String(request?.title || '').trim(),
        artist: String(request?.artist || '').trim(),
        album: String(request?.album || '').trim(),
        isrc: String(request?.isrc || '').trim(),
        durationMs: Number.isFinite(Number(request?.durationMs)) ? Number(request.durationMs) : 0
    };

    const facade = getDeezerPlaybackFacade();
    if (facade && typeof facade.resolveTrackBySpotifyRequest === 'function') {
        try {
            const resolved = await facade.resolveTrackBySpotifyRequest(payload);
            return resolved?.type === 'track' && resolved?.deezerId ? resolved : null;
        } catch {
            // Fall back to direct API resolve below.
        }
    }

    try {
        const response = await fetch(`/api/spotify/resolve-deezer?url=${encodeURIComponent(link)}`);
        if (!response.ok) {
            return null;
        }
        const raw = await response.text();
        const trimmed = raw.trim();
        if (!trimmed || trimmed === 'undefined') {
            return null;
        }
        const parsed = JSON.parse(raw);
        return parsed?.type === 'track' && parsed?.deezerId ? parsed : null;
    } catch {
        return null;
    }
}

function isHomeTrendingMappingButtonVisible(button) {
    if (!(button instanceof HTMLElement)) {
        return false;
    }
    const rect = button.getBoundingClientRect();
    const viewportHeight = globalThis.innerHeight || document.documentElement.clientHeight || 0;
    return rect.bottom >= -120 && rect.top <= viewportHeight + 200;
}

function getHomeTrendingMappingButtons() {
    return Array.from(
        document.querySelectorAll('#home-trending .home-top-song-item__play[data-home-trending-track="true"][data-spotify-url]')
    );
}

async function processHomeTrendingQueue(entries, concurrency, handler) {
    if (!entries.length) {
        return;
    }
    let cursor = 0;
    const workers = Array.from({ length: Math.min(concurrency, entries.length) }, async () => {
        while (cursor < entries.length) {
            const entry = entries[cursor++];
            await handler(entry);
        }
    });
    await Promise.all(workers);
}

function buildHomeTrendingResolveKey(button) {
    if (!button?.dataset) {
        return '';
    }
    return JSON.stringify([
        String(button.dataset.spotifyUrl || '').trim(),
        String(button.dataset.trackTitle || '').trim(),
        String(button.dataset.trackArtist || '').trim(),
        String(button.dataset.trackAlbum || '').trim(),
        String(button.dataset.trackIsrc || '').trim(),
        Number.parseInt(String(button.dataset.trackDurationMs || '0'), 10) || 0
    ]);
}

function isHomeTrendingResolveCoolingDown(resolveKey, nowMs) {
    if (!resolveKey) {
        return false;
    }
    const until = Number(homeTrendingUnmappedCooldownUntil.get(resolveKey) || 0);
    if (!Number.isFinite(until) || until <= 0) {
        return false;
    }
    if (until <= nowMs) {
        homeTrendingUnmappedCooldownUntil.delete(resolveKey);
        return false;
    }
    return true;
}

function computeWarmupVisibleLimit(queue, requestedLimit, defaults = {}) {
    const minBase = Number(defaults.minBase || 16);
    const maxCap = Number(defaults.maxCap || 24);
    const headroom = Number(defaults.headroom || 4);
    const visibleCount = queue.reduce((count, entry) => count + (entry.isVisible ? 1 : 0), 0);
    const adaptive = Math.max(minBase, visibleCount + headroom);
    const bounded = Math.max(1, Math.min(maxCap, adaptive));
    if (Number.isFinite(requestedLimit) && requestedLimit > 0) {
        return Math.max(1, Math.min(maxCap, Math.max(Math.trunc(requestedLimit), bounded)));
    }
    return bounded;
}

function extractHomeTrendingSpotifyTrackId(url) {
    const parsed = spotifyUrlHelpers.parseSpotifyUrl(url);
    if (parsed?.type !== 'track') {
        return '';
    }
    return String(parsed.id || '').trim();
}

function resetHomeTrendingMatchMaps() {
    homeTrendingMatchButtonsByIndex.clear();
    homeTrendingMatchButtonsBySpotifyId.clear();
}

function applyHomeTrendingPlaylistMatches(matches) {
    if (!Array.isArray(matches) || matches.length === 0) {
        return;
    }

    matches.forEach((match) => {
        const deezerId = String(match?.deezerId || '').trim();
        const spotifyId = String(match?.spotifyId || '').trim();
        const status = String(match?.status || '').trim().toLowerCase();
        const index = Number.isFinite(Number(match?.index)) ? Number(match.index) : -1;
        const button = (spotifyId && homeTrendingMatchButtonsBySpotifyId.get(spotifyId))
            || (index >= 0 ? homeTrendingMatchButtonsByIndex.get(index) : null);
        if (!(button instanceof HTMLElement)) {
            return;
        }

        if (/^\d+$/.test(deezerId)) {
            button.dataset.deezerId = deezerId;
            button.dataset.mappingState = 'mapped';
            const resolveKey = buildHomeTrendingResolveKey(button);
            if (resolveKey) {
                homeTrendingUnmappedCooldownUntil.delete(resolveKey);
            }
            void warmHomeTrendingPlaybackContext(button, deezerId);
            return;
        }

        if (status === 'unmatched_final' || status === 'hard_mismatch') {
            button.dataset.mappingState = 'unmapped';
            const resolveKey = buildHomeTrendingResolveKey(button);
            if (resolveKey) {
                homeTrendingUnmappedCooldownUntil.set(
                    resolveKey,
                    Date.now() + HOME_TRENDING_UNMAPPED_RETRY_COOLDOWN_MS
                );
            }
        }
    });
}

function computeHomeTrendingEffectiveLimit(visibleFirstOnly, limit, pending) {
    if (!visibleFirstOnly) {
        return limit > 0 ? Math.max(1, Math.trunc(limit)) : 0;
    }
    return computeWarmupVisibleLimit(pending, limit, { minBase: 16, maxCap: 24, headroom: 4 });
}

async function pollHomeTrendingPlaylistMatches(token) {
    if (!token) {
        return;
    }
    try {
        const response = await fetch(`/api/spotify/tracklist/matches?token=${encodeURIComponent(token)}`);
        if (!response.ok) {
            return;
        }
        const payload = JSON.parse(await response.text() || '{}');
        if (payload?.available !== true) {
            return;
        }
        applyHomeTrendingPlaylistMatches(payload.matches);
        const pending = Number(payload.pending || 0);
        if (pending <= 0 && token === homeTrendingMatchToken) {
            if (homeTrendingMatchPollTimer) {
                clearInterval(homeTrendingMatchPollTimer);
                homeTrendingMatchPollTimer = 0;
            }
        }
    } catch {
        // Best-effort polling.
    }
}

async function startHomeTrendingPlaylistStyleMatching(pending) {
    if (!Array.isArray(pending) || pending.length === 0) {
        return;
    }
    if (homeTrendingMatchStartPromise) {
        await homeTrendingMatchStartPromise;
        return;
    }

    const startPromise = (async () => {
        resetHomeTrendingMatchMaps();
        const tracks = [];
        const touchedButtons = [];
        for (let index = 0; index < pending.length; index += 1) {
            const entry = pending[index];
            const request = buildSpotifyTrackResolveRequestFromTrendingCard(entry.button) || { link: entry.url };
            const link = String(request?.link || entry.url || '').trim();
            if (!link) {
                continue;
            }

            entry.button.dataset.mappingState = 'mapping';
            touchedButtons.push(entry.button);
            homeTrendingMatchButtonsByIndex.set(index, entry.button);
            const spotifyId = extractHomeTrendingSpotifyTrackId(link);
            if (spotifyId && !homeTrendingMatchButtonsBySpotifyId.has(spotifyId)) {
                homeTrendingMatchButtonsBySpotifyId.set(spotifyId, entry.button);
            }

            tracks.push(spotifyUrlHelpers.buildSpotifyTrackMatchPayload(link, request));
        }

        if (tracks.length === 0) {
            return;
        }

        try {
            const response = await fetch('/api/spotify/tracklist/section/match', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    sectionKey: HOME_TRENDING_MATCH_SECTION_KEY,
                    tracks
                })
            });
            if (!response.ok) {
                return;
            }

            const payload = JSON.parse(await response.text() || '{}');
            if (payload?.available !== true) {
                touchedButtons.forEach((button) => {
                    if (!button.dataset.deezerId) {
                        button.dataset.mappingState = 'unmapped';
                    }
                });
                return;
            }

            applyHomeTrendingPlaylistMatches(payload.matches);
            const token = String(payload?.matching?.token || '').trim();
            const pendingCount = Number(payload?.matching?.pending || 0);
            if (!token || pendingCount <= 0) {
                return;
            }

            homeTrendingMatchToken = token;
            if (homeTrendingMatchPollTimer) {
                clearInterval(homeTrendingMatchPollTimer);
                homeTrendingMatchPollTimer = 0;
            }
            await pollHomeTrendingPlaylistMatches(token);
            homeTrendingMatchPollTimer = setInterval(() => {
                void pollHomeTrendingPlaylistMatches(token);
            }, 1000);
        } catch {
            touchedButtons.forEach((button) => {
                if (!button.dataset.deezerId) {
                    button.dataset.mappingState = 'unmapped';
                }
            });
            // Best-effort section matching.
        }
    })();

    homeTrendingMatchStartPromise = startPromise;
    try {
        await startPromise;
    } finally {
        homeTrendingMatchStartPromise = null;
    }
}

async function primeHomeTrendingTrackMappings(options = {}) {
    const limit = Number(options?.limit || 0);
    const visibleFirst = options?.visibleFirst !== false;
    const visibleFirstOnly = options?.visibleFirstOnly === true;

    const buttons = getHomeTrendingMappingButtons();
    if (buttons.length === 0) {
        return;
    }

    const nowMs = Date.now();
    const queue = buttons
        .map((button) => ({
            button,
            url: String(button.dataset.spotifyUrl || '').trim(),
            isVisible: isHomeTrendingMappingButtonVisible(button),
            resolveKey: buildHomeTrendingResolveKey(button)
        }))
        .filter((entry) => entry.url
            && !entry.button.dataset.deezerId
            && entry.button.dataset.mappingState !== 'mapping'
            && !isHomeTrendingResolveCoolingDown(entry.resolveKey, nowMs));

    if (queue.length === 0) {
        return;
    }

    if (visibleFirst) {
        queue.sort((left, right) => Number(right.isVisible) - Number(left.isVisible));
    }

    let pending = queue;
    if (visibleFirstOnly) {
        pending = pending.filter((entry) => entry.isVisible);
    }
    const effectiveLimit = computeHomeTrendingEffectiveLimit(visibleFirstOnly, limit, pending);
    if (effectiveLimit > 0) {
        pending = pending.slice(0, effectiveLimit);
    }

    await startHomeTrendingPlaylistStyleMatching(pending);
}

function scheduleHomeTrendingTrackMappingWarmup() {
    if (homeTrendingMatchWarmupTimer) {
        clearTimeout(homeTrendingMatchWarmupTimer);
    }

    void primeHomeTrendingTrackMappings({ visibleFirst: true, visibleFirstOnly: true, concurrency: 10, limit: 16 });
    homeTrendingMatchWarmupTimer = setTimeout(() => {
        homeTrendingMatchWarmupTimer = 0;
        void primeHomeTrendingTrackMappings({ visibleFirst: true, concurrency: 4 });
    }, 220);
}

function buildHomeTrendingPlaybackRequest(playButton) {
    if (!playButton) {
        return null;
    }

    const deezerIdRaw = (playButton.dataset.deezerId || '').trim();
    const deezerId = /^\d+$/.test(deezerIdRaw) ? deezerIdRaw : '';
    const spotifyUrl = (playButton.dataset.spotifyUrl || '').trim();
    const previewUrlRaw = (playButton.dataset.previewUrl || '').trim();
    // For Spotify-sourced cards we should follow Deezer-matched playback behavior.
    const previewUrl = spotifyUrl ? '' : previewUrlRaw;
    const spotifyMetadata = buildSpotifyTrackResolveRequestFromTrendingCard(playButton);

    return {
        page: 'home',
        button: playButton,
        deezerId,
        spotifyUrl,
        previewUrl,
        spotifyMetadata,
        sourceKey: previewUrl || deezerId || spotifyUrl,
        cache: homeDeezerPlaybackContextCache,
        requests: homeDeezerPlaybackContextRequests,
        unavailableMessage: previewUrl ? 'Preview unavailable.' : 'Track not available for streaming.',
        startFailedMessage: 'Unable to start playback.',
        interruptedMessage: 'Playback interrupted.',
        onStateChange: (state, context) => {
            setHomeTrendingPlaybackState(playButton, state);
            if (state === 'requested' || state === 'playing') {
                homeTrendingPreviewState.button = playButton;
                homeTrendingPreviewState.trackKey = context?.session?.key || null;
                return;
            }
            if (homeTrendingPreviewState.button === playButton) {
                homeTrendingPreviewState.button = null;
            }
            const contextSessionKey = String(context?.session?.key || '').trim();
            if (contextSessionKey
                ? homeTrendingPreviewState.trackKey === contextSessionKey
                : homeTrendingPreviewState.button === null) {
                homeTrendingPreviewState.trackKey = null;
            }
        },
        getNextRequest: async () => {
            while (true) {
                const nextButton = getNextHomeTrendingQueueButton();
                if (!nextButton) {
                    resetHomeTrendingQueue();
                    return null;
                }
                const ready = await ensureHomeTrendingButtonReadyForPlayback(nextButton, {
                    notifyOnUnmatched: false
                });
                if (ready) {
                    return buildHomeTrendingPlaybackRequest(nextButton);
                }
            }
        }
    };
}

function showHomeTrendingPlaybackMessage(message) {
    showToast(message, 'warning');
}

async function ensureHomeTrendingButtonReadyForPlayback(playButton, options = {}) {
    if (!playButton) {
        return false;
    }

    const notifyOnUnmatched = options?.notifyOnUnmatched !== false;
    const spotifyUrl = String(playButton.dataset.spotifyUrl || '').trim();
    const previewUrl = String(playButton.dataset.previewUrl || '').trim();
    const existingDeezerId = String(playButton.dataset.deezerId || '').trim();
    const hasValidExistingDeezerId = /^\d+$/.test(existingDeezerId);
    if (existingDeezerId && hasValidExistingDeezerId) {
        return true;
    }
    if (existingDeezerId && !hasValidExistingDeezerId) {
        playButton.dataset.deezerId = '';
    }

    if (!spotifyUrl) {
        if (!previewUrl && notifyOnUnmatched) {
            showHomeTrendingPlaybackMessage('No Deezer match available yet for this track.');
        }
        // Keep non-spotify, preview-only rows playable as a fallback.
        return Boolean(previewUrl);
    }

    if (notifyOnUnmatched) {
        showHomeTrendingPlaybackMessage('No Deezer match available yet for this track.');
    }
    return false;
}

async function playHomeTrendingTrackInApp(button) {
    const playButton = resolveHomeTrendingPlayButton(button);
    if (!playButton) {
        return;
    }

    const player = globalThis.DeezerUnifiedPlayback;
    if (!player || typeof player.play !== 'function') {
        return;
    }

    const ready = await ensureHomeTrendingButtonReadyForPlayback(playButton, {
        notifyOnUnmatched: true
    });
    if (!ready) {
        resetHomeTrendingQueue();
        return;
    }
    seedHomeTrendingQueue(playButton);

    const request = buildHomeTrendingPlaybackRequest(playButton);
    if (!request) {
        return;
    }

    await player.play(request);
}

function isMadeForYouSection(section) {
    if (!section) return false;
    const title = (section?.title || '').toString().trim().toLowerCase();
    if (title.includes('made for you')) {
        return true;
    }
    const candidates = [
        section?.id,
        section?.sectionId,
        section?.section_id,
        section?.key,
        section?.uri,
        section?.categoryId,
        section?.category_id
    ]
        .filter(Boolean)
        .map(val => String(val).toLowerCase());
    return candidates.some(val =>
        val.includes('made_for_you') ||
        val.includes('made-for-you') ||
        val.includes('madeforyou')
    );
}

// Unified search functionality (like deezspotag)
document.getElementById('unified-search-btn').addEventListener('click', function() {
    performUnifiedSearch();
});

// Enter key search
document.getElementById('unified-search').addEventListener('keypress', function(e) {
    if (e.key === 'Enter') {
        performUnifiedSearch();
    }
});

const searchInput = document.getElementById('unified-search');

const SEARCH_LINK_PLACEHOLDER = 'Search Deezer, Spotify and Apple Music tracks, albums, artists,... or paste Spotify/Apple Music/Boomplay/Tidal/Qobuz/Bandcamp links';
const SUPPORTED_LINK_SOURCES = 'Spotify, Apple Music, Boomplay, Tidal, Qobuz, Bandcamp, Deezer';

function applySearchSourceState() {
    if (searchInput) {
        searchInput.placeholder = SEARCH_LINK_PLACEHOLDER;
    }
}

async function parseDeezerLink(input) {
    const response = await fetch(`/api/deezer/parse-link?url=${encodeURIComponent(input)}`);
    if (!response.ok) {
        throw new Error('Link parsing failed.');
    }
    const data = await response.json();
    if (data?.error) {
        throw new Error(data.error);
    }
    return data;
}

async function mapInputLinkToDeezer(input) {
    const response = await fetch(`/api/link-map/deezer?url=${encodeURIComponent(input)}`);
    if (!response.ok) {
        throw new Error(`Link mapping failed (${response.status}).`);
    }

    const payload = await response.json();
    if (!payload || typeof payload !== 'object') {
        throw new Error('Link mapping response was invalid.');
    }

    return payload;
}

function classifyExternalSource(parsedUrl) {
    if (!parsedUrl || !(parsedUrl instanceof URL)) {
        return '';
    }

    const hostname = (parsedUrl.hostname || '').toLowerCase();
    if (!hostname) {
        return '';
    }

    if (hostname === 'open.spotify.com' || hostname === 'spotify.com' || hostname.endsWith('.spotify.com')) {
        return 'spotify';
    }
    if (hostname === 'music.apple.com' || hostname === 'itunes.apple.com') {
        return 'apple';
    }
    if (hostname === 'youtube.com'
        || hostname === 'www.youtube.com'
        || hostname === 'music.youtube.com'
        || hostname === 'youtu.be'
        || hostname.endsWith('.youtube.com')) {
        return 'youtube';
    }
    if (hostname === 'boomplay.com' || hostname === 'www.boomplay.com' || hostname === 'm.boomplay.com' || hostname.endsWith('.boomplay.com')) {
        return 'boomplay';
    }
    if (hostname === 'soundcloud.com' || hostname.endsWith('.soundcloud.com') || hostname === 'on.soundcloud.com') {
        return 'soundcloud';
    }
    if (hostname === 'tidal.com' || hostname === 'listen.tidal.com' || hostname.endsWith('.tidal.com')) {
        return 'tidal';
    }
    if (hostname === 'qobuz.com' || hostname === 'www.qobuz.com' || hostname === 'open.qobuz.com' || hostname === 'play.qobuz.com' || hostname.endsWith('.qobuz.com')) {
        return 'qobuz';
    }
    if (hostname === 'pandora.com' || hostname === 'www.pandora.com' || hostname.endsWith('.pandora.com')) {
        return 'pandora';
    }
    if (hostname === 'bandcamp.com' || hostname.endsWith('.bandcamp.com')) {
        return 'bandcamp';
    }
    if (hostname === 'deezer.com' || hostname === 'www.deezer.com' || hostname.endsWith('.deezer.com') || hostname === 'deezer.page.link') {
        return 'deezer';
    }

    return '';
}

function isPlaylistLikeExternalUrl(parsedUrl, source) {
    if (!parsedUrl || !(parsedUrl instanceof URL) || !source) {
        return false;
    }

    const path = (parsedUrl.pathname || '').toLowerCase();
    switch (source) {
        case 'youtube':
            return path.includes('/playlist') || parsedUrl.searchParams.has('list');
        case 'spotify':
            return path.includes('/playlist/');
        case 'apple':
            return path.includes('/playlist/');
        case 'boomplay':
            return /\/playlists?\//i.test(path);
        case 'soundcloud':
            return path.includes('/sets/');
        case 'tidal':
            return path.includes('/playlist/') || path.includes('/mix/');
        case 'qobuz':
            return path.includes('/playlist/') || path.includes('/playlists/');
        case 'bandcamp':
            return path.includes('/album/');
        case 'pandora':
            return path.includes('/playlist/');
        case 'deezer':
            return path.includes('/playlist/');
        default:
            return false;
    }
}

const SPOTIFY_PLAYLIST_PATH_REGEX = /\/(?:intl-[a-z]{2}\/)?playlist\/([a-z0-9]+)/i;
const APPLE_PLAYLIST_WITH_SLUG_REGEX = /\/playlist\/[^/]+\/([^/?#]+)/i;
const APPLE_PLAYLIST_DIRECT_REGEX = /\/playlist\/([^/?#]+)/i;
const BOOMPLAY_PLAYLIST_REGEX = /\/playlists?\/([a-z0-9]+)/i;

function extractRegexGroup(input, regex) {
    return regex.exec(input)?.[1] ?? '';
}

function findPathSegmentAfter(pathSegments, segmentName) {
    const index = pathSegments.indexOf(segmentName);
    if (index < 0) {
        return '';
    }
    return pathSegments[index + 1] || '';
}

function normalizePathSegments(parsedUrl) {
    return (parsedUrl.pathname || '')
        .split('/')
        .filter(Boolean)
        .map(segment => segment.trim().toLowerCase())
        .filter(Boolean);
}

function extractExternalCollectionId(parsedUrl, source) {
    if (!parsedUrl || !(parsedUrl instanceof URL) || !source) {
        return '';
    }

    const pathSegments = normalizePathSegments(parsedUrl);

    if (source === 'youtube') {
        return (parsedUrl.searchParams.get('list') || '').trim();
    }
    if (source === 'spotify') {
        return extractRegexGroup(parsedUrl.pathname, SPOTIFY_PLAYLIST_PATH_REGEX);
    }
    if (source === 'apple') {
        return extractRegexGroup(parsedUrl.pathname, APPLE_PLAYLIST_WITH_SLUG_REGEX)
            || extractRegexGroup(parsedUrl.pathname, APPLE_PLAYLIST_DIRECT_REGEX);
    }
    if (source === 'boomplay') {
        return extractRegexGroup(parsedUrl.pathname, BOOMPLAY_PLAYLIST_REGEX);
    }
    if (source === 'soundcloud') {
        return findPathSegmentAfter(pathSegments, 'sets');
    }
    if (source === 'tidal') {
        return findPathSegmentAfter(pathSegments, 'playlist')
            || findPathSegmentAfter(pathSegments, 'mix');
    }
    if (source === 'qobuz') {
        return findPathSegmentAfter(pathSegments, 'playlist')
            || findPathSegmentAfter(pathSegments, 'playlists');
    }
    if (source === 'pandora') {
        if (pathSegments.includes('playlist')) {
            return pathSegments.at(-1) || '';
        }
        return '';
    }
    if (source === 'bandcamp') {
        return findPathSegmentAfter(pathSegments, 'album');
    }

    return '';
}

function buildExternalPlaylistRouteBySource(source, parsedUrl, sourceUrl) {
    if (source === 'youtube') {
        const listId = (parsedUrl.searchParams.get('list') || '').trim();
        if (!/^[A-Za-z0-9_-]{10,}$/.test(listId)) {
            return '';
        }
        return `/Tracklist?id=${encodeURIComponent(listId)}&type=playlist&source=youtube`;
    }

    if (source === 'spotify') {
        const playlistId = extractRegexGroup(parsedUrl.pathname, SPOTIFY_PLAYLIST_PATH_REGEX);
        if (!playlistId) {
            return '';
        }
        return `/Tracklist?id=${encodeURIComponent(playlistId)}&type=playlist&source=spotify`;
    }

    if (source === 'apple') {
        const playlistId = extractRegexGroup(parsedUrl.pathname, APPLE_PLAYLIST_WITH_SLUG_REGEX)
            || extractRegexGroup(parsedUrl.pathname, APPLE_PLAYLIST_DIRECT_REGEX);
        if (!playlistId) {
            return '';
        }
        return `/Tracklist?id=${encodeURIComponent(playlistId)}&type=playlist&source=apple&appleUrl=${encodeURIComponent(sourceUrl)}`;
    }

    if (source === 'boomplay') {
        const playlistId = extractRegexGroup(parsedUrl.pathname, BOOMPLAY_PLAYLIST_REGEX);
        if (!playlistId) {
            return '';
        }
        return `/Tracklist?id=${encodeURIComponent(playlistId)}&type=playlist&source=boomplay`;
    }

    const genericPlaylistSources = new Set(['soundcloud', 'tidal', 'qobuz', 'bandcamp', 'pandora']);
    if (!genericPlaylistSources.has(source)) {
        return '';
    }

    const collectionId = extractExternalCollectionId(parsedUrl, source) || 'playlist';
    return `/Tracklist?id=${encodeURIComponent(collectionId)}&type=playlist&source=${encodeURIComponent(source)}&externalUrl=${encodeURIComponent(sourceUrl)}`;
}

function tryBuildExternalPlaylistRoute(parsedUrl, originalInput) {
    if (!parsedUrl || !(parsedUrl instanceof URL)) {
        return '';
    }

    const source = classifyExternalSource(parsedUrl);
    const sourceUrl = (originalInput || parsedUrl.toString() || '').trim();
    if (!source || !sourceUrl || !isPlaylistLikeExternalUrl(parsedUrl, source)) {
        return '';
    }
    return buildExternalPlaylistRouteBySource(source, parsedUrl, sourceUrl);
}

function navigateToMappedDeezer(mapping) {
    if (mapping?.available !== true) {
        return false;
    }

    const deezerType = (mapping.deezerType || '').toString().trim().toLowerCase();
    const deezerId = (mapping.deezerId || '').toString().trim();
    const hasNumericDeezerId = /^\d+$/.test(deezerId);

    if (deezerType === 'artist' && hasNumericDeezerId) {
        globalThis.location.href = `/Artist?id=${encodeURIComponent(deezerId)}&source=deezer`;
        return true;
    }

    if (hasNumericDeezerId) {
        const tracklistType = deezerType || 'track';
        globalThis.location.href = `/Tracklist?id=${encodeURIComponent(deezerId)}&type=${encodeURIComponent(tracklistType)}&source=deezer`;
        return true;
    }

    return false;
}

function setUnifiedSearchButtonState(isLoading) {
    const searchBtn = document.getElementById('unified-search-btn');
    if (!searchBtn) {
        return;
    }
    searchBtn.textContent = isLoading ? 'Searching...' : 'Search';
    searchBtn.disabled = isLoading;
}

async function handleUnifiedSearchUrlInput(input, parsedUrl) {
    const source = classifyExternalSource(parsedUrl);

    if (source === 'spotify') {
        const parsedSpotify = spotifyUrlHelpers.parseSpotifyUrl(input) || spotifyUrlHelpers.parseSpotifyUrl(parsedUrl.toString());
        if (!parsedSpotify?.type || !parsedSpotify?.id) {
            throw new Error('Invalid Spotify link.');
        }
        globalThis.location.href = `/Tracklist?id=${encodeURIComponent(parsedSpotify.id)}&type=${encodeURIComponent(parsedSpotify.type)}&source=spotify`;
        return true;
    }

    const externalPlaylistRoute = tryBuildExternalPlaylistRoute(parsedUrl, input);
    if (externalPlaylistRoute) {
        globalThis.location.href = externalPlaylistRoute;
        return true;
    }

    const hostname = (parsedUrl.hostname || '').toLowerCase();
    const isDeezerHost = hostname === 'deezer.com'
        || hostname === 'www.deezer.com'
        || hostname.endsWith('.deezer.com')
        || hostname === 'deezer.page.link';

    if (isDeezerHost) {
        const parsed = await parseDeezerLink(input);
        const deezerType = (parsed.type || '').toString().trim().toLowerCase();
        if (deezerType === 'artist') {
            globalThis.location.href = `/Artist?id=${encodeURIComponent(parsed.id)}&source=deezer`;
        } else {
            const typeValue = deezerType || 'track';
            globalThis.location.href = `/Tracklist?id=${encodeURIComponent(parsed.id)}&type=${encodeURIComponent(typeValue)}&source=deezer`;
        }
        return true;
    }

    const mapping = await mapInputLinkToDeezer(input);
    if (navigateToMappedDeezer(mapping)) {
        return true;
    }

    const sourceLabel = source || (mapping?.source || 'provided').toString();
    const message = `No in-app route found for ${sourceLabel} link. Supported link sources: ${SUPPORTED_LINK_SOURCES}.`;
    throw new Error(message);
}

async function performUnifiedSearch() {
    const input = document.getElementById('unified-search').value.trim();
    const type = 'track'; // Default to track search since dropdown is removed
    
    if (!input) {
        DeezSpoTag.ui.alert('Please enter a search term or URL', { title: 'Search' });
        return;
    }
    
    try {
        setUnifiedSearchButtonState(true);

        // Check if input is a URL (like deezspotag)
        if (isValidURL(input)) {
            const parsedUrl = new URL(input);
            const handled = await handleUnifiedSearchUrlInput(input, parsedUrl);
            if (handled) {
                return;
            }
            return;
        }

        console.log('Detected search term, redirecting to search results:', input);
        const searchParams = new URLSearchParams({
            term: input,
            type: type,
            source: 'spotify'
        });
        globalThis.location.href = `/Search?${searchParams.toString()}`;
    } catch (error) {
        console.error('Search/Download error:', error);
        DeezSpoTag.ui.alert(`Operation failed: ${error.message}`, { title: 'Search' });
    } finally {
        setUnifiedSearchButtonState(false);
    }
}

// Load popular content
function mergeSpotifySectionsIntoHomeSections(baseSections, spotifySections) {
    if (!Array.isArray(baseSections) || baseSections.length === 0) {
        return Array.isArray(spotifySections) ? [...spotifySections] : [];
    }
    if (!Array.isArray(spotifySections) || spotifySections.length === 0) {
        return [...baseSections];
    }

    const insertAfter = 'discover';
    const insertIndex = baseSections.findIndex((section) => {
        const title = (section?.title || '').toString().trim().toLowerCase();
        return title === insertAfter;
    });
    if (insertIndex >= 0) {
        return [
            ...baseSections.slice(0, insertIndex + 1),
            ...spotifySections,
            ...baseSections.slice(insertIndex + 1)
        ];
    }

    return [...baseSections, ...spotifySections];
}

function buildHomeRequestUrl(channel, refreshEnabled) {
    const baseUrl = channel ? `/api/home?channel=${encodeURIComponent(channel)}` : '/api/home';
    const requestUrl = new URL(baseUrl, globalThis.location.origin);
    if (refreshEnabled) {
        requestUrl.searchParams.set('refresh', '1');
    }
    if (!channel) {
        requestUrl.searchParams.set('timeZone', getBrowserTimeZone());
    }
    return requestUrl.toString();
}

async function fetchHomeSections(channel, refreshEnabled) {
    const requestUrl = buildHomeRequestUrl(channel, refreshEnabled);
    const response = await fetch(requestUrl, { cache: 'no-store' });
    if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`);
    }
    const data = await response.json();
    return Array.isArray(data?.sections) ? data.sections : [];
}

function renderHomeSectionsWithLazyImages(sections) {
    renderHomeSections(sections);
    observeLazyImages(document.getElementById('home-sections'));
    syncHomeTrendingPlaybackFromSession();
}

async function loadHomeData() {
    try {
        const requestId = ++homeLoadRequestSeq;
        const urlParams = new URLSearchParams(globalThis.location.search);
        const channel = urlParams.get('channel');
        const refresh = urlParams.get('refresh');
        const refreshEnabled = refresh === '1' || refresh === 'true' || refresh === 'yes';

        const sections = await fetchHomeSections(channel, refreshEnabled);
        if (requestId !== homeLoadRequestSeq) {
            return;
        }
        console.log('Home data received:', { sectionsCount: sections.length });

        renderHomeSectionsWithLazyImages(sections);
    } catch (error) {
        console.error('Error loading home data:', error);
        const container = document.getElementById('home-sections');
        if (container && !container.querySelector('.home-section')) {
            container.innerHTML = '<div class="empty-section">Failed to load home sections</div>';
        }
    }
}

function dedupeExactHomeArtistItems(items, helpers) {
    const seenExact = new Map();
    const stableItems = [];
    for (const item of items) {
        if (!item || typeof item !== 'object') {
            continue;
        }

        const source = helpers.getItemSource(item);
        const type = helpers.getItemType(item);
        const id = helpers.getItemId(item);
        if (source && type && id) {
            const key = `${source}:${type}:${id}`;
            if (seenExact.has(key)) {
                const existingIndex = seenExact.get(key);
                const existingItem = stableItems[existingIndex];
                if (helpers.isArtistItem(existingItem) && helpers.isArtistItem(item)) {
                    stableItems[existingIndex] = helpers.choosePreferredArtistCard(existingItem, item);
                }
                continue;
            }
            seenExact.set(key, stableItems.length);
        }
        stableItems.push(item);
    }

    return stableItems;
}

function dedupeCrossSourceHomeArtistItems(items, helpers) {
    const firstArtistByName = new Map();
    const deduped = [];
    for (const item of items) {
        if (!helpers.isArtistItem(item)) {
            deduped.push(item);
            continue;
        }

        const nameKey = helpers.normalizeArtistNameKey(helpers.getArtistDisplayName(item));
        if (!nameKey) {
            deduped.push(item);
            continue;
        }

        if (!firstArtistByName.has(nameKey)) {
            firstArtistByName.set(nameKey, deduped.length);
            deduped.push(item);
            continue;
        }

        const existingIndex = firstArtistByName.get(nameKey);
        const existingItem = deduped[existingIndex];
        if (!helpers.isSameCrossSourceArtist(existingItem, item)) {
            deduped.push(item);
            continue;
        }

        deduped[existingIndex] = helpers.choosePreferredArtistCard(existingItem, item);
    }

    return deduped;
}

function dedupeMergedHomePopularArtistItems(items, helpers) {
    if (!Array.isArray(items) || items.length === 0) {
        return [];
    }
    return dedupeCrossSourceHomeArtistItems(dedupeExactHomeArtistItems(items, helpers), helpers);
}

function resolveHomeSectionRowClass(layoutRaw, isDiscover, isTrendingSongs, isTopGenres, isChannelSection) {
    if (isDiscover) {
        return 'discover-grid';
    }
    if (isTrendingSongs) {
        return 'home-trending-grid';
    }
    if (isTopGenres || isChannelSection) {
        return 'top-genres-grid';
    }
    return layoutRaw === 'grid' ? 'home-grid' : 'home-row';
}

function resolveHomeSectionLimit(section, options) {
    const hasSectionItems = Array.isArray(section?.items) && section.items.length > 0;
    const preserveAllItems = section?.__preserveAllItems === true || section?.preserveAllItems === true;
    if (preserveAllItems && hasSectionItems) {
        return section.items.length;
    }
    if (options.isChannelPage) {
        return 60;
    }
    if (options.isTrendingSongs) {
        return 20;
    }
    return options.maxItemsPerSection;
}

function filterHomeSectionItems(items, options) {
    if (options.isDiscover || options.isTopGenres || options.isTrendingSongs) {
        return items;
    }
    return items.filter(item => !options.isGhostHomeItem(item));
}

function applyPopularRadioPreviewItems(items, options) {
    if (options.isChannelPage || !options.isPopularRadio || items.length <= options.popularRadioPreviewCount) {
        return items;
    }
    return [
        ...items.slice(0, options.popularRadioPreviewCount),
        { __homeAction: 'popular-radio-see-more' }
    ];
}

function computeHomeSectionRenderMeta(section, options) {
    const normalizedTitle = options.normalizeTitle(section?.title);
    const isTrendingSongs = normalizedTitle === 'trending songs';
    const isPopularRadio = options.isPopularRadioSection(section);
    const sectionLimit = resolveHomeSectionLimit(section, {
        isChannelPage: options.isChannelPage,
        isTrendingSongs,
        maxItemsPerSection: options.maxItemsPerSection
    });
    const items = Array.isArray(section?.items) ? section.items.slice(0, sectionLimit) : [];
    const layoutRaw = (section?.layout || '').toString().toLowerCase();
    const filter = section?.filter || null;
    const hasFilter = layoutRaw === 'filterable-grid' && Array.isArray(filter?.options) && filter.options.length > 0;
    const isDiscover = normalizedTitle === 'discover';
    const isTopGenres = normalizedTitle === 'your top genres' || normalizedTitle === 'categories';
    const isChannelSection = items.length > 0 && items.every(item => (item?.type || '').toString().toLowerCase() === 'channel');
    const rowClass = resolveHomeSectionRowClass(layoutRaw, isDiscover, isTrendingSongs, isTopGenres, isChannelSection);
    const filteredItems = applyPopularRadioPreviewItems(
        filterHomeSectionItems(items, {
            isDiscover,
            isTopGenres,
            isTrendingSongs,
            isGhostHomeItem: options.isGhostHomeItem
        }),
        {
            isChannelPage: options.isChannelPage,
            isPopularRadio,
            popularRadioPreviewCount: options.popularRadioPreviewCount
        }
    );
    const isSpaceAware = filteredItems.length > 0
        && filteredItems.length < options.spaceAwareThreshold
        && rowClass !== 'discover-grid';

    return {
        normalizedTitle,
        isTrendingSongs,
        isPopularRadio,
        layoutRaw,
        filter,
        hasFilter,
        isDiscover,
        isTopGenres,
        rowClass,
        items,
        filteredItems,
        isSpaceAware
    };
}

function renderHomeSectionCardItem(item, meta) {
    if (item?.__homeAction === 'popular-radio-see-more') {
        return renderPopularRadioSeeMoreCard();
    }
    if (!meta.hasFilter) {
        if (meta.isDiscover) {
            return renderDiscoveryItem(item);
        }
        if (meta.isTopGenres) {
            return renderTopGenresItem(item);
        }
        return renderHomeItem(item);
    }

    const filterIds = (item?.filter_option_ids || []).map(String);
    const dataAttr = filterIds.length ? ` data-filter-ids="${filterIds.join(',')}"` : '';
    let rendered = renderHomeItem(item);
    if (meta.isDiscover) {
        rendered = renderDiscoveryItem(item);
    } else if (meta.isTopGenres) {
        rendered = renderTopGenresItem(item);
    }
    return `<div class="home-filtered-item"${dataAttr}>${rendered}</div>`;
}

function renderHomeTopGenresCards(meta, isChannelPage) {
    const maxTopGenresSlots = isChannelPage ? meta.items.length : 14;
    const baseTopGenresItems = meta.items.slice(0, maxTopGenresSlots);
    const mergedItems = mergeSpotifyCategories(baseTopGenresItems, maxTopGenresSlots);
    const sliced = mergedItems.map((item) => renderTopGenresItem(item)).join('');
    const showMoreCard = isChannelPage
        ? ''
        : `<a class="top-genres-card top-genres-card--more" href="/Categories">See more</a>`;
    return {
        cards: `${sliced}${showMoreCard}`,
        deezerItemsAttr: `data-deezer-items="${encodeURIComponent(JSON.stringify(baseTopGenresItems))}"`
    };
}

function resolveHomeSectionRowSpaceAwareMin(rowClass) {
    if (rowClass === 'home-grid') {
        return 'var(--art-grid-min)';
    }
    if (rowClass === 'home-row') {
        return 'var(--art-card-size)';
    }
    return '0px';
}

function renderHomeSectionEntry(entry, index, isChannelPage) {
    const section = entry.section;
    const meta = entry.meta;
    const filterId = `home-filter-${index}`;
    const filtersHtml = meta.hasFilter
        ? `<div class="home-filters" data-filter-group="${filterId}">
            ${meta.filter.options.map(opt => `<button class="home-filter-btn" data-filter-id="${opt.id}">${escapeHtml(opt.label || opt.id)}</button>`).join('')}
           </div>`
        : '';
    const showMore = !meta.isPopularRadio && section.hasMore && section.pagePath
        ? `<div class="home-section-more"><button class="action-btn action-btn-sm" onclick="openHomeChannel('${section.pagePath}')">Show more</button></div>`
        : '';
    const sectionIdAttr = meta.isTrendingSongs ? ' id="home-trending"' : '';
    const trendingWatchKey = meta.isTrendingSongs
        ? cacheHomeTrendingWatchMeta(section, meta.filteredItems)
        : '';
    const titleHtml = trendingWatchKey
        ? `<button type="button" class="home-section-title home-section-title--action" data-home-trending-tracklist="${trendingWatchKey}">${escapeHtml(section.title || '')}</button>`
        : `<div class="home-section-title">${escapeHtml(section.title || '')}</div>`;
    const topGenres = meta.isTopGenres ? renderHomeTopGenresCards(meta, isChannelPage) : null;
    let sectionItemsHtml = meta.filteredItems.map(item => renderHomeSectionCardItem(item, meta)).join('');
    if (meta.isTopGenres && topGenres) {
        sectionItemsHtml = topGenres.cards;
    } else if (meta.isTrendingSongs) {
        sectionItemsHtml = meta.filteredItems.map(item => renderHomeTrendingSongItem(item)).join('');
    }
    const rowClass = meta.isSpaceAware ? `${meta.rowClass} home-space-aware-row` : meta.rowClass;
    const rowStyle = meta.isSpaceAware
        ? `style="--home-space-aware-count:${meta.filteredItems.length}; --home-space-aware-min:${resolveHomeSectionRowSpaceAwareMin(meta.rowClass)};"`
        : '';
    const deezerItemsAttr = topGenres ? topGenres.deezerItemsAttr : '';

    return `
        <div class="home-section"${sectionIdAttr}>
            ${titleHtml}
            ${filtersHtml}
            <div class="${rowClass}" ${rowStyle} ${deezerItemsAttr}>
                ${sectionItemsHtml}
            </div>
            ${meta.isTopGenres ? '' : showMore}
        </div>
    `;
}

function filterHomeSectionsForRender(sections, options) {
    return sections.filter(section => {
        if (options.isEpisodesYouMightLikeSection(section)) {
            return false;
        }
        if (!options.isRecentlyPlayedSection(section)) {
            return true;
        }
        const itemCount = Array.isArray(section?.items) ? section.items.length : 0;
        return itemCount >= 4;
    });
}

function normalizeHomeCategoriesSection(sections, normalizeTitle) {
    const categoriesSection = sections.find(section => normalizeTitle(section?.title) === 'categories');
    const topGenresSection = sections.find(section => normalizeTitle(section?.title) === 'your top genres');
    if (categoriesSection) {
        const alias = { ...categoriesSection, title: 'Categories', __aliasTopGenres: true };
        const trimmed = sections.filter(section => {
            const normalized = normalizeTitle(section?.title);
            return normalized !== 'your top genres' && normalized !== 'categories' && section !== categoriesSection;
        });
        const insertIndex = sections.findIndex(section => normalizeTitle(section?.title) === 'your top genres');
        if (insertIndex >= 0 && insertIndex <= trimmed.length) {
            trimmed.splice(insertIndex, 0, alias);
        } else {
            const categoriesIndex = sections.indexOf(categoriesSection);
            const targetIndex = categoriesIndex >= 0 && categoriesIndex <= trimmed.length ? categoriesIndex : trimmed.length;
            trimmed.splice(targetIndex, 0, alias);
        }
        return trimmed;
    }

    if (topGenresSection) {
        topGenresSection.title = 'Categories';
    }
    return sections;
}

function mergeHomeContinuePopularArtistsSections(sections, options) {
    const continueStreamingIndex = sections.findIndex(section => options.isContinueStreamingSection(section));
    const popularArtistsIndex = sections.findIndex(section => options.isPopularArtistsSection(section));
    if (continueStreamingIndex < 0) {
        return sections;
    }

    const continueSection = sections[continueStreamingIndex] || {};
    const continueArtistItems = options.extractArtistItems(continueSection);
    const hasExplicitPopularArtistsSection = popularArtistsIndex >= 0 && popularArtistsIndex !== continueStreamingIndex;
    const popularArtistsSection = hasExplicitPopularArtistsSection ? (sections[popularArtistsIndex] || {}) : null;
    const spotifyArtistItems = hasExplicitPopularArtistsSection
        ? options.extractArtistItems(popularArtistsSection)
        : options.collectSpotifyArtistItemsForContinueMerge(sections, new Set([continueStreamingIndex]));
    if (continueArtistItems.length === 0 && spotifyArtistItems.length === 0) {
        return sections;
    }

    const mergedArtistItems = options.dedupeMergedPopularArtistItems([
        ...continueArtistItems,
        ...spotifyArtistItems
    ]);
    const mergedSectionSource = popularArtistsSection || continueSection;
    const mergedSection = {
        ...mergedSectionSource,
        title: 'Continue Streaming Popular Artists',
        __preserveAllItems: true,
        items: mergedArtistItems,
        hasMore: (popularArtistsSection?.hasMore === true) || (continueSection.hasMore === true),
        pagePath: (popularArtistsSection?.pagePath || continueSection.pagePath || ''),
        related: popularArtistsSection?.related || continueSection.related || null,
        filter: popularArtistsSection?.filter || continueSection.filter || null,
        layout: popularArtistsSection?.layout || continueSection.layout || 'row'
    };

    if (!hasExplicitPopularArtistsSection) {
        const next = sections.slice();
        next[continueStreamingIndex] = mergedSection;
        return next;
    }

    const compactSections = sections.filter((_, index) => index !== continueStreamingIndex && index !== popularArtistsIndex);
    const mergedInsertIndex = popularArtistsIndex - (continueStreamingIndex < popularArtistsIndex ? 1 : 0);
    compactSections.splice(Math.max(0, mergedInsertIndex), 0, mergedSection);
    return compactSections;
}

function mergeHomeRecommendedNewReleaseSections(sections, options) {
    const recommendedIndexes = sections
        .map((section, index) => options.isRecommendedForTodaySection(section) ? index : -1)
        .filter(index => index >= 0);
    const newReleaseIndexes = sections
        .map((section, index) => options.isNewReleasesForYouSection(section) ? index : -1)
        .filter(index => index >= 0);
    if (recommendedIndexes.length === 0 || newReleaseIndexes.length === 0) {
        return sections;
    }

    const mergeIndexes = new Set([...recommendedIndexes, ...newReleaseIndexes]);
    const mergeSections = [...mergeIndexes]
        .sort((left, right) => left - right)
        .map(index => sections[index])
        .filter(Boolean);
    const preferredBaseSection = mergeSections.find(section => options.isSpotifyHomeSection(section))
        || mergeSections[0]
        || {};
    const mergedItems = options.dedupeSectionItems(
        mergeSections.flatMap(section => Array.isArray(section?.items) ? section.items : [])
    );
    const mergedSection = {
        ...preferredBaseSection,
        title: 'Recommended new releases for you today',
        __preserveAllItems: true,
        items: mergedItems,
        hasMore: mergeSections.some(section => section?.hasMore === true),
        pagePath: mergeSections
            .map(section => (section?.pagePath || '').toString().trim())
            .find(value => value.length > 0) || '',
        related: mergeSections.map(section => section?.related).find(Boolean) || null,
        filter: mergeSections.map(section => section?.filter).find(Boolean) || null,
        layout: mergeSections
            .map(section => (section?.layout || '').toString().trim())
            .find(value => value.length > 0) || 'row'
    };
    const compactSections = sections.filter((_, index) => !mergeIndexes.has(index));
    const popularRadioIndex = compactSections.findIndex(section => options.isPopularRadioSection(section));
    const fallbackInsertIndex = Math.min(...mergeIndexes);
    const insertIndex = popularRadioIndex >= 0
        ? popularRadioIndex + 1
        : Math.min(fallbackInsertIndex, compactSections.length);
    compactSections.splice(insertIndex, 0, mergedSection);
    return compactSections;
}

function repositionHomeCategoriesAfterRecommended(sections, options) {
    const recommendedNewReleasesIndex = sections.findIndex(section => options.isRecommendedNewReleasesCombinedSection(section));
    const categoriesIndex = sections.findIndex(section => options.normalizeTitle(section?.title) === 'categories');
    if (recommendedNewReleasesIndex < 0 || categoriesIndex < 0) {
        return sections;
    }

    const next = sections.slice();
    const [categoriesSection] = next.splice(categoriesIndex, 1);
    const refreshedRecommendedIndex = next.findIndex(section => options.isRecommendedNewReleasesCombinedSection(section));
    const refreshedPopularRadioIndex = next.findIndex(section => options.isPopularRadioSection(section));
    let insertAfterRecommendedIndex = next.length;
    if (refreshedRecommendedIndex >= 0) {
        if (refreshedPopularRadioIndex > refreshedRecommendedIndex) {
            insertAfterRecommendedIndex = refreshedPopularRadioIndex + 1;
        } else {
            insertAfterRecommendedIndex = refreshedRecommendedIndex + 1;
        }
    }
    next.splice(insertAfterRecommendedIndex, 0, categoriesSection);
    return next;
}

function preprocessHomeSectionsForRender(sections, options) {
    let next = filterHomeSectionsForRender(sections, options);
    if (options.isChannelPage) {
        return next;
    }

    next = normalizeHomeCategoriesSection(next, options.normalizeTitle);
    next = mergeHomeContinuePopularArtistsSections(next, options);
    next = mergeHomeRecommendedNewReleaseSections(next, options);
    next = repositionHomeCategoriesAfterRecommended(next, options);
    return next;
}

function orderHomeSectionsForRender(sections, options) {
    if (options.isChannelPage) {
        return sections;
    }

    const normalizedSections = sections.slice();
    const pinned = [];
    const used = new Set();
    const pushPinned = (predicate) => {
        const index = normalizedSections.findIndex((section, sectionIndex) => !used.has(sectionIndex) && predicate(section));
        if (index >= 0) {
            used.add(index);
            pinned.push(normalizedSections[index]);
        }
    };

    pushPinned(section => options.isSpotifyHomeSection(section) && options.normalizeSectionTitle(section?.title) === 'trending songs');
    pushPinned(section => options.isSpotifyHomeSection(section) && options.isMadeForYouSection(section));
    pushPinned(section => options.normalizeSectionTitle(section?.title) === 'discover');
    pushPinned(section => options.isPopularRadioSection(section));
    pushPinned(section => options.isRecommendedNewReleasesCombinedSection(section));
    pushPinned(section => options.normalizeTitle(section?.title) === 'categories' || options.normalizeTitle(section?.title) === 'your top genres');

    const remaining = normalizedSections.filter((_, index) => !used.has(index));
    return [...pinned, ...remaining];
}

function buildHomeSectionsToRender(orderedSections, computeSectionRenderMeta, isSingleCardNewReleaseSection) {
    return orderedSections
        .map(section => ({ section, meta: computeSectionRenderMeta(section) }))
        .filter(entry => {
            const itemCount = entry.meta.filteredItems.length;
            if (itemCount >= 4) {
                return true;
            }
            return itemCount >= 1 && isSingleCardNewReleaseSection(entry.meta.normalizedTitle);
        });
}

function renderHomeSections(sections) {
    const container = document.getElementById('home-sections');
    if (!sections || sections.length === 0) {
        container.innerHTML = '<div class="empty-section">No home sections available</div>';
        return;
    }

    const urlParams = new URLSearchParams(globalThis.location.search);
    const isChannelPage = !!urlParams.get('channel');
    const maxItemsPerSection = isChannelPage ? 60 : 16;
    const popularRadioPreviewCount = 13;
    const normalizeTitle = (value) => (value || '').toString().trim().toLowerCase();
    const normalizeSectionTitle = (value) => normalizeTitle(value).replaceAll(/\s+/g, ' ');
    const isContinueStreamingSection = (section) => {
        const title = normalizeSectionTitle(section?.title);
        return title === 'continue streaming' || title === 'continue listening';
    };
    const isPopularArtistsSection = (section) => {
        const title = normalizeSectionTitle(section?.title);
        return title === 'popular artists' || title === 'popular artist';
    };
    const getSectionItems = (section) => Array.isArray(section?.items) ? section.items : [];
    const extractArtistItems = (section) => getSectionItems(section).filter(isArtistItem);
    const collectSpotifyArtistItemsForContinueMerge = (allSections, skipIndexes = new Set()) => {
        const artists = [];
        if (!Array.isArray(allSections) || allSections.length === 0) {
            return artists;
        }

        for (let index = 0; index < allSections.length; index++) {
            if (skipIndexes.has(index)) {
                continue;
            }
            const section = allSections[index];
            if (!isSpotifyHomeSection(section)) {
                continue;
            }
            const sectionArtists = extractArtistItems(section);
            if (sectionArtists.length === 0) {
                continue;
            }
            artists.push(...sectionArtists);
        }

        return artists;
    };
    const isRecommendedForTodaySection = (section) => {
        return normalizeSectionTitle(section?.title) === 'recommended for today';
    };
    const isNewReleasesForYouSection = (section) => {
        return normalizeSectionTitle(section?.title) === 'new releases for you';
    };
    const isRecommendedNewReleasesCombinedSection = (section) => {
        return normalizeSectionTitle(section?.title) === 'recommended new releases for you today';
    };
    const isPopularRadioSection = (section) => {
        const title = normalizeSectionTitle(section?.title);
        return title === 'popular radio' || title === 'popular radios';
    };
    const isRecentlyPlayedSection = (section) => {
        const title = normalizeSectionTitle(section?.title);
        return title === 'recently played';
    };
    const isEpisodesYouMightLikeSection = (section) => {
        const title = normalizeSectionTitle(section?.title);
        return title === 'episodes you might like' || title === 'episode you might like';
    };
    const normalizeArtistNameKey = (value) => {
        if (value === null || value === undefined) {
            return '';
        }
        return value
            .toString()
            .normalize('NFKD')
            .replaceAll(/[\u0300-\u036f]/g, '')
            .replaceAll(/['`´’]/g, '')
            .replaceAll(/[._-]/g, ' ')
            .replaceAll(/\s+/g, ' ')
            .trim()
            .toLowerCase();
    };
    const getItemSource = (item) => (item?.source || '').toString().trim().toLowerCase();
    const getItemType = (item) => (item?.type || '').toString().trim().toLowerCase();
    const getItemId = (item) => (item?.id || '').toString().trim();
    const getArtistDisplayName = (item) => (item?.title || item?.name || '').toString().trim();
    const getArtistFansCount = (item) => toHomeStatNumber(item?.fans ?? item?.followers);
    const isArtistItem = (item) => getItemType(item) === 'artist';
    const hasUsableImage = (item) => {
        const imageHash = item?.md5_image
            || item?.md5
            || item?.imageHash
            || item?.PLAYLIST_PICTURE
            || item?.playlistPicture
            || item?.cover?.md5
            || item?.cover?.MD5
            || item?.background_image?.md5;
        const imageUrl = extractFirstUrl(
            item?.image?.fullUrl
            || item?.image?.full
            || item?.image?.url
            || item?.imageUrl
            || item?.coverUrl
            || item?.image
            || item?.picture
            || item?.cover
            || item?.artwork
            || item?.image?.thumbUrl
            || item?.image?.thumb
            || ''
        );
        return !!(imageHash || imageUrl);
    };
    const sourcePriority = (source) => {
        if (source === 'deezer') return 0;
        if (source === 'spotify') return 1;
        return 2;
    };
    const scoreArtistCard = (item) => {
        let score = 0;
        if (hasUsableImage(item)) score += 4;
        if (getArtistFansCount(item) !== null) score += 3;
        if (getItemId(item)) score += 1;
        if ((item?.target || item?.link || item?.uri || '').toString().trim()) score += 1;
        return score;
    };
    const choosePreferredArtistCard = (existingItem, incomingItem) => {
        const existingScore = scoreArtistCard(existingItem);
        const incomingScore = scoreArtistCard(incomingItem);
        if (incomingScore > existingScore) {
            return incomingItem;
        }
        if (incomingScore < existingScore) {
            return existingItem;
        }

        const existingSource = getItemSource(existingItem);
        const incomingSource = getItemSource(incomingItem);
        const existingPriority = sourcePriority(existingSource);
        const incomingPriority = sourcePriority(incomingSource);
        if (incomingPriority < existingPriority) {
            return incomingItem;
        }
        return existingItem;
    };
    const isSameCrossSourceArtist = (existingItem, incomingItem) => {
        if (!isArtistItem(existingItem) || !isArtistItem(incomingItem)) {
            return false;
        }

        const existingSource = getItemSource(existingItem);
        const incomingSource = getItemSource(incomingItem);
        if (!existingSource || !incomingSource || existingSource === incomingSource) {
            return false;
        }

        const existingNameKey = normalizeArtistNameKey(getArtistDisplayName(existingItem));
        const incomingNameKey = normalizeArtistNameKey(getArtistDisplayName(incomingItem));
        if (!existingNameKey || existingNameKey !== incomingNameKey) {
            return false;
        }

        const existingFans = getArtistFansCount(existingItem);
        const incomingFans = getArtistFansCount(incomingItem);
        if (existingFans !== null && incomingFans !== null) {
            const maxFans = Math.max(Math.abs(existingFans), Math.abs(incomingFans));
            const minFans = Math.max(1, Math.min(Math.abs(existingFans), Math.abs(incomingFans)));
            if ((maxFans / minFans) > 100) {
                return false;
            }
        }

        return true;
    };
    const dedupeMergedPopularArtistItems = (items) => dedupeMergedHomePopularArtistItems(items, {
        getItemSource,
        getItemType,
        getItemId,
        isArtistItem,
        choosePreferredArtistCard,
        normalizeArtistNameKey,
        getArtistDisplayName,
        isSameCrossSourceArtist
    });
    const dedupeSectionItems = (items) => {
        if (!Array.isArray(items) || items.length === 0) {
            return [];
        }

        const normalizeKey = (value) => normalizeArtistNameKey(value);
        const seen = new Set();
        const deduped = [];

        for (const item of items) {
            if (!item || typeof item !== 'object') {
                continue;
            }

            const source = (item.source || '').toString().trim().toLowerCase();
            const type = (item.type || '').toString().trim().toLowerCase();
            const id = (item.id || '').toString().trim();
            const uri = (item.uri || '').toString().trim().toLowerCase();
            const target = (item.target || item.link || '').toString().trim().toLowerCase();
            const title = normalizeKey(item.title || item.name || '');
            const subtitle = normalizeKey(item.subtitle || item.artist || item.artists || item.description || '');

            const candidateKeys = [];
            if (source && type && id) {
                candidateKeys.push(`sid:${source}:${type}:${id}`);
            }
            if (uri) {
                candidateKeys.push(`uri:${uri}`);
            }
            if (target) {
                candidateKeys.push(`target:${target}`);
            }
            if (title) {
                candidateKeys.push(`title:${type}:${title}:${subtitle}`);
            }

            const isDuplicate = candidateKeys.some((key) => seen.has(key));
            if (isDuplicate) {
                continue;
            }

            candidateKeys.forEach((key) => seen.add(key));
            deduped.push(item);
        }

        return deduped;
    };
    const isSpotifyHomeSection = (section) => {
        const source = (section?.source || '').toString().toLowerCase();
        if (source === 'spotify') return true;
        const uri = (section?.uri || '').toString().toLowerCase();
        if (uri.startsWith('spotify:section:')) return true;
        const items = Array.isArray(section?.items) ? section.items : [];
        if (items.some(item => (item?.uri || '').toString().toLowerCase().startsWith('spotify:'))) return true;
        return false;
    };
    sections = preprocessHomeSectionsForRender(sections, {
        isChannelPage,
        normalizeTitle,
        isContinueStreamingSection,
        isPopularArtistsSection,
        extractArtistItems,
        collectSpotifyArtistItemsForContinueMerge,
        dedupeMergedPopularArtistItems,
        isRecommendedForTodaySection,
        isNewReleasesForYouSection,
        isSpotifyHomeSection,
        dedupeSectionItems,
        isRecommendedNewReleasesCombinedSection,
        isPopularRadioSection,
        isEpisodesYouMightLikeSection,
        isRecentlyPlayedSection
    });

    if (!sections.length) {
        container.innerHTML = '<div class="empty-section">No home sections available</div>';
        return;
    }

    const orderedSections = orderHomeSectionsForRender(sections, {
        isChannelPage,
        isSpotifyHomeSection,
        normalizeSectionTitle,
        isMadeForYouSection,
        isPopularRadioSection,
        isRecommendedNewReleasesCombinedSection,
        normalizeTitle
    });

    const spaceAwareThreshold = 8;
    const isSingleCardNewReleaseSection = (normalizedTitle) => {
        if (!normalizedTitle) {
            return false;
        }
        return normalizedTitle.startsWith('new release')
            || normalizedTitle.startsWith('new releases')
            || normalizedTitle.includes('nouveaut')
            || normalizedTitle.includes('novedad');
    };

    const computeSectionRenderMeta = (section) => computeHomeSectionRenderMeta(section, {
        normalizeTitle,
        isPopularRadioSection,
        isGhostHomeItem,
        isChannelPage,
        maxItemsPerSection,
        popularRadioPreviewCount,
        spaceAwareThreshold
    });

    const sectionsToRender = buildHomeSectionsToRender(
        orderedSections,
        computeSectionRenderMeta,
        isSingleCardNewReleaseSection
    );

    if (!sectionsToRender.length) {
        container.innerHTML = '<div class="empty-section">No home sections available</div>';
        return;
    }

    homeTrendingWatchMetaCache.clear();
    container.innerHTML = sectionsToRender
        .map((entry, index) => renderHomeSectionEntry(entry, index, isChannelPage))
        .join('');

    setupHomeFilters(sectionsToRender.map(entry => entry.section));
    scheduleHomeTrendingTrackMappingWarmup();
}

function renderPopularRadioSeeMoreCard() {
    return `
        <a class="playlist-card playlist-card--playlist playlist-card--see-more" href="/Spotify/PopularRadio">
            <div class="playlist-image playlist-image--see-more">
                <span class="playlist-image--see-more-label">See more</span>
            </div>
            <div class="playlist-title"><span class="home-marquee">Popular Radio</span></div>
            <div class="playlist-meta"><span class="home-marquee">Open full list</span></div>
        </a>
    `;
}

function cacheHomeTrendingWatchMeta(section, items) {
    const title = (section?.title || '').toString().trim();
    if (!title) {
        return '';
    }
    const normalizedTitle = title.toLowerCase();
    if (normalizedTitle !== 'trending songs' && normalizedTitle !== 'top songs') {
        return '';
    }

    const sectionSource = (section?.source || '').toString().trim().toLowerCase();
    const hasSpotifyItems = Array.isArray(items)
        && items.some(item => (item?.source || '').toString().trim().toLowerCase() === 'spotify');
    if (sectionSource !== 'spotify' && !hasSpotifyItems) {
        return '';
    }

    const sourceId = HOME_TRENDING_SPOTIFY_SOURCE_ID;
    const key = `home-trending-watch-${homeTrendingWatchSeq++}`;
    homeTrendingWatchMetaCache.set(key, {
        source: 'spotify',
        sourceId,
        name: title,
        imageUrl: extractHomeTrendingSectionImageUrl(items),
        description: 'Spotify home feed trending songs',
        trackCount: Array.isArray(items) ? items.length : null
    });
    return key;
}

function extractHomeTrendingSectionImageUrl(items) {
    if (!Array.isArray(items) || items.length === 0) {
        return '';
    }

    for (const item of items) {
        const candidate = upgradeSpotifyImageUrl(extractFirstUrl(
            extractSpotifyCoverUrl(item)
            || item.image?.fullUrl
            || item.image?.full
            || item.image?.url
            || item.imageUrl
            || item.coverUrl
            || item.image
            || item.picture
            || item.cover
            || item.artwork
            || item.image?.thumbUrl
            || item.image?.thumb
            || ''
        ));
        if (candidate) {
            return candidate;
        }
    }

    return '';
}

function openHomeTrendingTracklist(button) {
    const watchKey = button?.dataset?.homeTrendingTracklist || '';
    const watchMeta = watchKey && homeTrendingWatchMetaCache.has(watchKey)
        ? homeTrendingWatchMetaCache.get(watchKey)
        : null;
    const sourceId = (watchMeta?.sourceId || HOME_TRENDING_SPOTIFY_SOURCE_ID).toString().trim() || HOME_TRENDING_SPOTIFY_SOURCE_ID;
    globalThis.location.href = `/Tracklist?id=${encodeURIComponent(sourceId)}&type=playlist&source=spotify`;
}

function setupHomeFilters(sections) {
    sections.forEach((section, index) => {
        const layoutRaw = (section.layout || '').toString().toLowerCase();
        const filter = section.filter || null;
        if (layoutRaw !== 'filterable-grid' || !Array.isArray(filter?.options)) {
            return;
        }
        const defaultId = filter.default_option_id || filter.options[0]?.id || '';
        const container = document.querySelector(`.home-filters[data-filter-group="home-filter-${index}"]`);
        const sectionEl = container?.closest('.home-section');
        if (!container || !sectionEl) return;
        const buttons = Array.from(container.querySelectorAll('.home-filter-btn'));
        const items = Array.from(sectionEl.querySelectorAll('.home-filtered-item'));
        const applyFilter = (id) => {
            buttons.forEach(btn => btn.classList.toggle('active', btn.dataset.filterId === id));
            items.forEach(el => {
                const ids = (el.dataset.filterIds || '').split(',').filter(Boolean);
                const visible = !ids.length || ids.includes(id);
                el.classList.toggle('is-visible', visible);
            });
        };
        buttons.forEach(btn => btn.addEventListener('click', () => applyFilter(btn.dataset.filterId || '')));
        applyFilter(defaultId);
    });
}

function buildDeezerImageUrl(imageHash, type = 'cover') {
    if (!imageHash) {
        return '';
    }
    if (typeof imageHash === 'string' && (imageHash.startsWith('http://') || imageHash.startsWith('https://'))) {
        return imageHash;
    }
    const safeHash = String(imageHash).trim();
    if (!safeHash) {
        return '';
    }
    return `https://e-cdns-images.dzcdn.net/images/${type}/${safeHash}/1000x1000-000000-80-0-0.jpg`;
}

function buildDeezerImageUrlWithSize(imageHash, type = 'cover', size = 1000, quality = 80) {
    if (!imageHash) {
        return '';
    }
    if (typeof imageHash === 'string' && (imageHash.startsWith('http://') || imageHash.startsWith('https://'))) {
        return imageHash;
    }
    const safeHash = String(imageHash).trim();
    if (!safeHash) {
        return '';
    }
    return `https://e-cdns-images.dzcdn.net/images/${type}/${safeHash}/${size}x${size}-000000-${quality}-0-0.jpg`;
}

function buildDeezerLogoUrlWithSize(imageHash, type = 'misc', height = 208, quality = 100) {
    if (!imageHash) {
        return '';
    }
    const safeHash = String(imageHash).trim();
    if (!safeHash) {
        return '';
    }
    return `https://e-cdns-images.dzcdn.net/images/${type}/${safeHash}/${height}x0-none-${quality}-0-0.png`;
}

function upgradeSpotifyImageUrl(url) {
    if (!url || typeof url !== 'string') {
        return url || '';
    }
    if (!/scdn\.co\/image\//i.test(url)) {
        return url;
    }
    if (url.includes('ab6765630000f68d')) {
        return url.replace('ab6765630000f68d', 'ab6765630000ba8a');
    }
    return url;
}

function extractSpotifyCoverUrl(item) {
    if (!item) {
        return '';
    }
    return extractFirstUrl(
        item.coverUrl
        || item.imageUrl
        || item.image
        || item.picture
        || item.images
        || item.artwork
        || item.cover
        || item.image?.images
        || item.cover?.images
        || item.images?.items
        || item.images?.sources
        || ''
    );
}

function extractFirstUrl(value) {
    if (!value) {
        return '';
    }
    if (typeof value === 'string') {
        return value;
    }
    if (Array.isArray(value)) {
        for (const entry of value) {
            const found = extractFirstUrl(entry);
            if (found) {
                return found;
            }
        }
        return '';
    }
    if (typeof value === 'object') {
        return extractFirstUrl(
            value.url
            || value.fullUrl
            || value.full
            || value.thumbUrl
            || value.thumb
            || value.image
            || value.cover
            || value.artwork
            || value.sources
            || value.items
            || ''
        );
    }
    return '';
}

function toHomeStatNumber(value) {
    if (typeof value === 'number' && Number.isFinite(value)) {
        return Math.trunc(value);
    }
    if (typeof value === 'string') {
        const trimmed = value.trim();
        if (!trimmed) {
            return null;
        }
        const normalized = trimmed.replaceAll(/[^\d-]/g, '');
        if (!normalized || normalized === '-') {
            return null;
        }
        const parsed = Number.parseInt(normalized, 10);
        return Number.isFinite(parsed) ? parsed : null;
    }
    return null;
}

function formatHomeStatNumber(value) {
    const numeric = toHomeStatNumber(value);
    if (numeric === null) {
        return '';
    }
    const abs = Math.abs(numeric);
    const grouped = abs.toLocaleString('en-US').replaceAll(',', ' ');
    return numeric < 0 ? `-${grouped}` : grouped;
}

function normalizeHomeStatLabel(label) {
    const normalized = (label || '').toString().trim().toLowerCase();
    if (!normalized) {
        return '';
    }
    if (normalized.startsWith('follow') || normalized.startsWith('fan')) {
        return 'fans';
    }
    if (normalized.startsWith('track') || normalized.startsWith('song')) {
        return 'tracks';
    }
    return normalized;
}

function normalizeHomeSubtitle(item, subtitleValue) {
    const itemType = (item?.type || '').toString().trim().toLowerCase();
    const directFans = toHomeStatNumber(item?.fans ?? item?.followers);
    if (itemType === 'artist' && directFans !== null) {
        return `${formatHomeStatNumber(directFans)} fans`;
    }

    let subtitle = (subtitleValue || '').toString().trim();
    if (!subtitle) {
        return '';
    }

    subtitle = subtitle.replaceAll(/\bfollowers?\b/gi, 'fans');
    subtitle = subtitle.replaceAll(/(\d[\d\s,._-]{0,64})\s*(tracks?|songs?|fans?|followers?)/gi, (_, numberPart, labelPart) => {
        const formatted = formatHomeStatNumber(numberPart);
        const label = normalizeHomeStatLabel(labelPart);
        if (!formatted) {
            return `${String(numberPart).trim()} ${label}`.trim();
        }
        return `${formatted} ${label}`.trim();
    });

    if (itemType === 'artist') {
        const parsedFromSubtitle = toHomeStatNumber(subtitle);
        if (parsedFromSubtitle !== null) {
            return `${formatHomeStatNumber(parsedFromSubtitle)} fans`;
        }
    }

    return subtitle;
}

function isGhostHomeItem(item) {
    if (!item) {
        return true;
    }

    const type = (item.type || '').toString().trim().toLowerCase();
    const isCardType = type === 'playlist'
        || type === 'album'
        || type === 'artist'
        || type === 'show'
        || type === 'episode'
        || type === 'track'
        || type === 'smarttracklist'
        || type === 'station'
        || type === '';
    if (!isCardType) {
        return false;
    }

    const titleRaw = (item.title || item.name || '').toString().trim();
    if (titleRaw) {
        return false;
    }

    const trackCount = toHomeStatNumber(item.nb_tracks ?? item.trackCount ?? item.track_count);
    const fanCount = toHomeStatNumber(item.fans ?? item.followers);
    if (trackCount === 0 && fanCount === 0) {
        return true;
    }

    const subtitleRaw = normalizeHomeSubtitle(item, item.subtitle || item.description || '');
    if (/\b0\s*tracks?\b/i.test(subtitleRaw) && /\b0\s*fans?\b/i.test(subtitleRaw)) {
        return true;
    }

    const hasRoutingTarget = !!(
        (item.id || '').toString().trim()
        || (item.target || '').toString().trim()
        || (item.link || '').toString().trim()
        || (item.uri || '').toString().trim()
    );
    return !hasRoutingTarget;
}

function getHomeItemType(item) {
    return (item?.type || '').toString().toLowerCase();
}

function registerHomeSpotifyCard(item) {
    const isSpotifyItem = (item?.source || '').toString().toLowerCase() === 'spotify';
    if (!isSpotifyItem) {
        return {
            isSpotifyItem: false,
            spotifyAttr: '',
            clickAttr: ` onclick="${buildHomeClick(item)}"`
        };
    }

    const spotifyItemKey = `spotify-item-${spotifyHomeItemSeq++}`;
    spotifyHomeItemCache.set(spotifyItemKey, item);
    return {
        isSpotifyItem: true,
        spotifyAttr: ` data-spotify-item="${spotifyItemKey}"`,
        clickAttr: ''
    };
}

function resolveHomeItemImage(item, thumbSize) {
    const imageType = item.picture_type || item.pictureType || item.imageType || 'cover';
    const imageHash = item.md5_image || item.md5 || item.imageHash || item.PLAYLIST_PICTURE || item.playlistPicture || item.cover?.md5 || item.cover?.MD5 || item.background_image?.md5;
    return upgradeSpotifyImageUrl(extractFirstUrl(
        item.image?.fullUrl
        || item.image?.full
        || item.image?.url
        || item.imageUrl
        || item.coverUrl
        || item.image
        || item.picture
        || item.cover
        || item.artwork
        || item.image?.thumbUrl
        || item.image?.thumb
        || buildDeezerImageUrlWithSize(imageHash, imageType, thumbSize, 80)
        || buildDeezerImageUrl(imageHash, imageType)
        || ''
    ));
}

function resolveHomeFallbackIcon(itemType) {
    if (itemType === 'artist') {
        return '👤';
    }
    if (itemType === 'playlist') {
        return '🎵';
    }
    if (itemType === 'album') {
        return '💿';
    }
    return '✨';
}

function buildHomePlaylistBadges(item, subtitleRaw) {
    if (getHomeItemType(item) !== 'playlist') {
        return [];
    }

    const playlistTracks = item.nb_tracks ?? item.trackCount ?? item.track_count;
    const playlistFans = item.fans ?? item.followers;
    const subtitleHasCounts = /track|fan/i.test(subtitleRaw);
    const parsedPlaylistTracks = toHomeStatNumber(playlistTracks);
    const parsedPlaylistFans = toHomeStatNumber(playlistFans);
    const playlistFlags = [];
    if (typeof item.public === 'boolean') {
        playlistFlags.push({
            label: item.public ? 'Public' : 'Private',
            className: item.public ? 'playlist-badge--public' : 'playlist-badge--private'
        });
    }
    if (item.collaborative === true) {
        playlistFlags.push({
            label: 'Collaborative',
            className: 'playlist-badge--collab'
        });
    }

    if (subtitleHasCounts) {
        return playlistFlags;
    }

    const statBadges = [
        parsedPlaylistTracks === null ? null : { label: `${formatHomeStatNumber(parsedPlaylistTracks)} tracks` },
        parsedPlaylistFans === null ? null : { label: `${formatHomeStatNumber(parsedPlaylistFans)} fans` }
    ].filter(Boolean);
    return [...statBadges, ...playlistFlags];
}

function renderHomePlaylistBadgesMarkup(badges) {
    if (!Array.isArray(badges) || badges.length === 0) {
        return '';
    }

    const badgeItems = badges.map(badge => {
        const label = escapeHtml(badge.label || '');
        const className = badge.className ? ` ${badge.className}` : '';
        return `<span class="playlist-badge${className}">${label}</span>`;
    }).join('');
    return `<div class="playlist-badges">${badgeItems}</div>`;
}

function renderHomeChannelCard(item, title, image, fallbackIcon, spotifyAttr, clickAttr) {
    const artStyle = image ? `style="background-image: url('${image}');"` : '';
    return `
        <div class="channel-card"${spotifyAttr}${clickAttr}>
            <div class="channel-card-title">${title}</div>
            <div class="channel-card-art" ${artStyle}>${image ? '' : fallbackIcon}</div>
        </div>
    `;
}

function renderHomeShowCard(title, image, fallbackIcon, spotifyAttr, clickAttr) {
    const artStyle = image ? `style="background-image: url('${image}');"` : '';
    return `
        <div class="show-card"${spotifyAttr}${clickAttr}>
            <div class="show-art" ${artStyle}>${image ? '' : fallbackIcon}</div>
            <div class="show-title">${title}</div>
        </div>
    `;
}

function renderHomeDefaultCard(card) {
    const {
        item,
        title,
        subtitle,
        badges,
        image,
        logoImage,
        fallbackIcon,
        spotifyAttr,
        clickAttr
    } = card;
    const itemType = getHomeItemType(item);
    const needsSquare = itemType === 'artist' || itemType === 'channel';
    const imageClass = needsSquare ? 'playlist-image square-art' : 'playlist-image';
    const cardClass = `playlist-card${itemType === 'playlist' ? ' playlist-card--playlist' : ''}`;
    const channelBg = item.background_color ? `background-color: ${item.background_color};` : '';
    const channelBgStyle = channelBg ? `style="${channelBg}"` : '';
    const imageStyle = `style="${channelBg}background-image: url('${image}'); background-size: cover; background-position: center;"`;
    let logoMarkup = '';
    if (logoImage) {
        logoMarkup = `<img class="channel-logo" src="${logoImage}" alt="">`;
    } else if (!image) {
        logoMarkup = fallbackIcon;
    }

    return `
        <div class="${cardClass}"${spotifyAttr}${clickAttr}>
            <div class="${imageClass}" ${channelBgStyle} ${imageStyle}>
                ${logoMarkup}
            </div>
            <div class="playlist-title"><span class="home-marquee">${title}</span></div>
            ${subtitle ? `<div class="playlist-meta"><span class="home-marquee">${subtitle}</span></div>` : ''}
            ${renderHomePlaylistBadgesMarkup(badges)}
        </div>
    `;
}

function renderHomeItem(item) {
    if (!item) {
        return '';
    }

    const itemType = getHomeItemType(item);
    const { spotifyAttr, clickAttr } = registerHomeSpotifyCard(item);
    const thumbSize = itemType === 'artist' || itemType === 'channel' ? 140 : 250;
    const image = resolveHomeItemImage(item, thumbSize);
    const logoHash = item.logo || item.logo_image?.md5 || item.logo_image?.MD5;
    const logoImage = logoHash ? buildDeezerLogoUrlWithSize(logoHash, 'misc', 52, 100) : '';
    const titleRaw = (item.title || item.name || '').toString().trim();
    if (!titleRaw) {
        return '';
    }
    const title = escapeHtml(titleRaw);
    const subtitleRaw = normalizeHomeSubtitle(item, item.subtitle || '');
    const subtitle = escapeHtml(subtitleRaw);
    const badges = buildHomePlaylistBadges(item, subtitleRaw);
    const fallbackIcon = resolveHomeFallbackIcon(itemType);

    if (itemType === 'channel') {
        return renderHomeChannelCard(item, title, image, fallbackIcon, spotifyAttr, clickAttr);
    }
    if (itemType === 'show') {
        return renderHomeShowCard(title, image, fallbackIcon, spotifyAttr, clickAttr);
    }

    return renderHomeDefaultCard({
        item,
        title,
        subtitle,
        badges,
        image,
        logoImage,
        fallbackIcon,
        spotifyAttr,
        clickAttr
    });
}

function buildHomeTrendingCardInteractivity(item) {
    const isSpotifyItem = (item?.source || '').toString().toLowerCase() === 'spotify';
    if (isSpotifyItem) {
        const spotifyItemKey = `spotify-item-${spotifyHomeItemSeq++}`;
        spotifyHomeItemCache.set(spotifyItemKey, item);
        return {
            spotifyAttr: ` data-spotify-item="${spotifyItemKey}"`,
            clickAttr: '',
            clickableAttr: ''
        };
    }

    return {
        spotifyAttr: '',
        clickAttr: ` onclick="${buildHomeClick(item)}"`,
        clickableAttr: ' data-home-clickable="true"'
    };
}

function resolveHomeTrendingCover(item) {
    const imageType = item.picture_type || item.pictureType || item.imageType || 'cover';
    const imageHash = item.md5_image || item.md5 || item.imageHash || item.cover?.md5 || item.cover?.MD5;
    return upgradeSpotifyImageUrl(extractFirstUrl(
        extractSpotifyCoverUrl(item)
        || item.image?.fullUrl
        || item.image?.full
        || item.image?.url
        || item.imageUrl
        || item.coverUrl
        || item.image
        || item.picture
        || item.cover
        || item.artwork
        || item.image?.thumbUrl
        || item.image?.thumb
        || buildDeezerImageUrlWithSize(imageHash, imageType, 250, 80)
        || buildDeezerImageUrl(imageHash, imageType)
        || ''
    ));
}

function resolveHomeTrendingDeezerTrackId(item, itemSource, itemType) {
    if (itemSource === 'deezer' && itemType === 'track' && item.id) {
        return String(item.id);
    }

    const itemTarget = (item?.target || '').toString();
    const targetTrackMatch = itemTarget.match(/\/track\/(\d+)/i);
    return targetTrackMatch ? targetTrackMatch[1] : '';
}

function resolveHomeTrendingSpotifyTrackUrl(item, itemSource, itemType) {
    if (itemSource !== 'spotify') {
        return '';
    }

    const existing = spotifyUrlHelpers.buildSpotifyWebUrl(item.uri || item.url || item.sourceUrl || '');
    if (existing) {
        return existing;
    }
    if (itemType === 'track' && item.id) {
        return `https://open.spotify.com/track/${encodeURIComponent(String(item.id))}`;
    }
    return '';
}

function buildHomeTrendingPlayAttributes(item, deezerTrackId, spotifyTrackUrl, previewUrl) {
    const deezerPlaybackContext = normalizeHomeDeezerPlaybackContext({
        streamTrackId: item.stream_track_id || item.streamTrackId || '',
        trackToken: item.track_token || item.trackToken || '',
        md5origin: item.md5_origin || item.md5Origin || item.md5origin || '',
        mv: item.media_version || item.mediaVersion || item.mv || ''
    });
    const playAttrs = [];
    playAttrs.push('data-home-trending-track="true"');

    if (deezerTrackId) {
        playAttrs.push(`data-deezer-id="${escapeHtml(deezerTrackId)}"`);
        appendHomeTrendingDeezerContextAttributes(playAttrs, deezerPlaybackContext);
    }
    appendHomeTrendingUrlAttributes(playAttrs, spotifyTrackUrl, previewUrl);
    appendHomeTrendingTrackMetadataAttributes(playAttrs, item);

    return playAttrs;
}

function appendHomeTrendingDeezerContextAttributes(playAttrs, deezerPlaybackContext) {
    if (!deezerPlaybackContext) {
        return;
    }

    playAttrs.push(
        `data-stream-track-id="${escapeHtml(deezerPlaybackContext.streamTrackId)}"`,
        `data-track-token="${escapeHtml(deezerPlaybackContext.trackToken)}"`
    );
    if (deezerPlaybackContext.md5origin) {
        playAttrs.push(`data-md5-origin="${escapeHtml(deezerPlaybackContext.md5origin)}"`);
    }
    if (deezerPlaybackContext.mv) {
        playAttrs.push(`data-media-version="${escapeHtml(deezerPlaybackContext.mv)}"`);
    }
}

function appendHomeTrendingUrlAttributes(playAttrs, spotifyTrackUrl, previewUrl) {
    if (spotifyTrackUrl) {
        playAttrs.push(`data-spotify-url="${escapeHtml(spotifyTrackUrl)}"`);
    }
    if (previewUrl) {
        playAttrs.push(`data-preview-url="${escapeHtml(previewUrl)}"`);
    }
}

function appendHomeTrendingTrackMetadataAttributes(playAttrs, item) {
    const metadata = extractHomeTrendingTrackMetadata(item);
    if (metadata.title) {
        playAttrs.push(`data-track-title="${escapeHtml(metadata.title)}"`);
    }
    if (metadata.artist) {
        playAttrs.push(`data-track-artist="${escapeHtml(metadata.artist)}"`);
    }
    if (metadata.album) {
        playAttrs.push(`data-track-album="${escapeHtml(metadata.album)}"`);
    }
    if (metadata.isrc) {
        playAttrs.push(`data-track-isrc="${escapeHtml(metadata.isrc)}"`);
    }
    if (metadata.durationMs > 0) {
        playAttrs.push(`data-track-duration-ms="${metadata.durationMs}"`);
    }
}

function extractHomeTrendingTrackMetadata(item) {
    const title = (item.name || item.title || '').toString().trim();
    const artist = (item.artists || item.artist || item.ownerName || item.owner || '').toString().trim();
    const album = (item.albumName || item.album || item.releaseName || '').toString().trim();
    const isrc = (item.isrc || item.ISRC || item.trackIsrc || '').toString().trim();
    const durationMsRaw = Number.parseInt(
        String(item.duration_ms || item.durationMs || (Number.isFinite(item.duration) ? Math.round(Number(item.duration) * 1000) : 0)),
        10
    );
    const durationMs = Number.isFinite(durationMsRaw) && durationMsRaw > 0 ? durationMsRaw : 0;

    return {
        title,
        artist,
        album,
        isrc,
        durationMs
    };
}

function buildHomeTrendingPlayButton(canPlay, playAttributes, altText) {
    if (!canPlay) {
        return '';
    }

    return `
        <button class="home-top-song-item__play" type="button" ${playAttributes.join(' ')} aria-label="Play ${altText} preview" onclick="event.preventDefault(); event.stopPropagation(); event.stopImmediatePropagation(); playHomeTrendingTrackInApp(this); return false;">
            <span class="playback-glyph" aria-hidden="true">
                <svg class="playback-icon playback-icon--play" viewBox="0 0 24 24" focusable="false">
                    <path d="M8 5v14l11-7z"></path>
                </svg>
                <svg class="playback-icon playback-icon--pause" viewBox="0 0 24 24" focusable="false">
                    <path d="M7 5h4v14H7zM13 5h4v14h-4z"></path>
                </svg>
            </span>
        </button>
    `;
}

function renderHomeTrendingSongItem(item) {
    if (!item) {
        return '';
    }

    const { spotifyAttr, clickAttr, clickableAttr } = buildHomeTrendingCardInteractivity(item);
    const cover = resolveHomeTrendingCover(item);

    const titleRaw = (item.name || item.title || '').toString().trim();
    const title = escapeHtml(titleRaw || 'Untitled');
    const subtitle = escapeHtml(buildHomeTrendingSubtitle(item));
    const altText = escapeHtml(titleRaw || 'track');
    const itemSource = (item.source || '').toString().toLowerCase();
    const itemType = (item.type || '').toString().toLowerCase();
    const deezerTrackId = resolveHomeTrendingDeezerTrackId(item, itemSource, itemType);
    const previewUrl = (item.preview_url || item.previewUrl || item.preview || '').toString().trim();
    const spotifyTrackUrl = resolveHomeTrendingSpotifyTrackUrl(item, itemSource, itemType);
    const canPlay = Boolean(deezerTrackId || spotifyTrackUrl || previewUrl);
    const coverMarkup = cover
        ? `<img src="${escapeHtml(cover)}" alt="${altText}" loading="lazy" decoding="async" />`
        : '<div class="home-top-song-item__placeholder"></div>';
    const playAttrs = buildHomeTrendingPlayAttributes(item, deezerTrackId, spotifyTrackUrl, previewUrl);
    const thumbClickAttrs = canPlay
        ? `data-home-play-thumb="true" ${playAttrs.join(' ')} onclick="event.preventDefault(); event.stopPropagation(); event.stopImmediatePropagation(); playHomeTrendingTrackInApp(this); return false;"`
        : '';
    const playButton = buildHomeTrendingPlayButton(canPlay, playAttrs, altText);

    return `
        <div class="home-top-song-item"${spotifyAttr}${clickAttr}${clickableAttr}>
            <div class="home-top-song-item__thumb" ${thumbClickAttrs}>
                ${coverMarkup}
                ${playButton}
            </div>
            <div class="home-top-song-item__info">
                <div class="home-top-song-item__title">${title}</div>
                ${subtitle ? `<div class="home-top-song-item__album">${subtitle}</div>` : ''}
            </div>
        </div>
    `;
}

function buildHomeTrendingSubtitle(item) {
    if (!item) {
        return '';
    }

    const explicit = (item.subtitle || item.description || '').toString().trim();
    const album = (item.albumName || item.album || item.releaseName || '').toString().trim();
    if (explicit) {
        if (album && !explicit.toLowerCase().includes(album.toLowerCase())) {
            return `${explicit} • ${album}`;
        }
        return explicit;
    }

    const artists = (item.artists || item.artist || item.ownerName || item.owner || '').toString().trim();
    const meta = [];
    if (artists) {
        meta.push(artists);
    }
    if (album && album.toLowerCase() !== artists.toLowerCase()) {
        meta.push(album);
    }
    return meta.join(' • ');
}

function renderDiscoveryItem(item) {
    if (!item) {
        return '';
    }
    const imageType = item.picture_type || item.pictureType || item.imageType || 'cover';
    const imageHash = item.md5_image || item.md5 || item.imageHash || item.cover?.md5 || item.cover?.MD5;
    const image = buildDeezerImageUrlWithSize(imageHash, imageType, 500, 90);
    const pictures = Array.isArray(item.pictures) ? item.pictures.slice(0, 2) : [];
    const collagePictures = pictures.length === 1 ? [pictures[0], pictures[0]] : pictures;
    const collage = collagePictures.length >= 1
        ? `<div class="discover-collage">
            ${collagePictures.map(pic => {
                const url = buildDeezerImageUrlWithSize(pic.md5, pic.type || 'artist', 500, 90);
                return `<div style="background-image: url('${url}')"></div>`;
            }).join('')}
           </div>`
        : '';
    const subtitle = escapeHtml(item.subtitle || '');
    const coverTitle = escapeHtml(item.cover_title || item.caption || item.label || item.title || '');
    const avatarImage = collagePictures[0]?.md5
        ? buildDeezerImageUrlWithSize(collagePictures[0].md5, collagePictures[0].type || 'artist', 1000, 90)
        : image;
    const heroImage = collagePictures[1]?.md5
        ? buildDeezerImageUrlWithSize(collagePictures[1].md5, collagePictures[1].type || 'artist', 1000, 90)
        : (avatarImage || image);
    const click = buildDiscoverClick(item, avatarImage, heroImage);
    const coverStyle = collage ? '' : `background-image: url('${image}'); background-size: cover; background-position: center;`;
    return `
        <div class="discover-card" onclick="${click}">
            <div class="discover-cover" style="${coverStyle}">
                ${collage}
                ${coverTitle ? `<div class="discover-banner">${coverTitle}</div>` : ''}
            </div>
            ${subtitle ? `<div class="discover-subtitle">${subtitle}</div>` : ''}
        </div>
    `;
}

const renderTopGenresItem = globalThis.HomeViewHelpers.renderTopGenresItem;
const buildSpotifyBrowseClick = globalThis.HomeViewHelpers.buildSpotifyBrowseClick;
const buildHomeClick = globalThis.HomeViewHelpers.buildHomeClick;

function buildDiscoverClick(item, avatarImage, heroImage) {
    if (!item) {
        return 'void(0)';
    }

    const source = (item.source || '').toString().toLowerCase();
    const type = (item.type || '').toString().toLowerCase();
    const extraParams = new URLSearchParams();
    if (avatarImage) {
        extraParams.set('discoverAvatar', avatarImage);
    }
    if (heroImage) {
        extraParams.set('discoverHero', heroImage);
    }

    const tracklistPrefixes = new Map([
        ['/playlist/', 'playlist'],
        ['/album/', 'album'],
        ['/smarttracklist/', 'smarttracklist']
    ]);
    const target = (item.target || '').toString();
    for (const [prefix, targetType] of tracklistPrefixes.entries()) {
        if (target.startsWith(prefix)) {
            const id = target.replace(prefix, '');
            const qs = new URLSearchParams();
            qs.set('id', id);
            qs.set('type', targetType);
            extraParams.forEach((value, key) => qs.set(key, value));
            return `globalThis.location.href='/Tracklist?${qs.toString()}'`;
        }
    }

    const tracklistTypes = new Set(['playlist', 'album', 'show', 'smarttracklist']);
    if (source === 'spotify') {
        return buildHomeClick(item);
    }
    if (tracklistTypes.has(type)) {
        const qs = new URLSearchParams();
        qs.set('id', item.id || '');
        qs.set('type', type);
        extraParams.forEach((value, key) => qs.set(key, value));
        return `globalThis.location.href='/Tracklist?${qs.toString()}'`;
    }

    return buildHomeClick(item);
}

function mergeSpotifyCategories(deezerItems, limit) {
    const baseItems = Array.isArray(deezerItems) ? deezerItems.slice() : [];
    return limit ? baseItems.slice(0, limit) : baseItems;
}

function openTracklist(id, type) {
    globalThis.location.href = `/Tracklist?id=${id}&type=${encodeURIComponent(type)}`;
}

function openSpotifyTracklist(id, type) {
    globalThis.location.href = `/Tracklist?id=${encodeURIComponent(id)}&type=${encodeURIComponent(type)}&source=spotify`;
}

globalThis.openSpotifyArtistFallback = globalThis.HomeViewHelpers.openSpotifyArtistFallback;
globalThis.openSpotifyArtist = globalThis.HomeViewHelpers.openSpotifyArtist;

function openArtist(id) {
    globalThis.location.href = `/Artist?id=${id}&source=deezer`;
}

function buildHomeChannelUrl(target, layout) {
    if (!target) {
        return '#';
    }
    const normalized = target.toString().replace(/^\//, '');
    const params = new URLSearchParams();
    params.set('channel', normalized);
    if (layout) {
        params.set('layout', layout);
    }
    return `/Home?${params.toString()}`;
}

function openHomeChannel(target, layout) {
    const url = buildHomeChannelUrl(target, layout);
    if (url && url !== '#') {
        globalThis.location.href = url;
    }
}

function escapeHtml(text) {
    if (!text) return '';
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

// Load data when page loads

// EXACT PORT: Auto-login functionality like deezspotag main.ts
async function checkAutoLogin() {
    try {
        console.log('Checking for auto-login...');
        
        const response = await fetch('/api/connect');
        const data = await response.json();
        
        console.log('Connect response:', data);
        
        // EXACT PORT: Handle auto-login like deezspotag main.ts
        if (data.autologin && data.singleUser?.hasStoredCredentials) {
            console.log('Auto-login required, attempting server-side auto-login...');
            const result = await attemptAutoLogin();
            return result?.status === 1 || result?.status === 2 || result?.status === 3;
        }
        return false;
    } catch (error) {
        console.error('Error checking auto-login:', error);
        return false;
    }
}

// EXACT PORT: Auto-login function like deezspotag main.ts
async function attemptAutoLogin() {
    try {
        console.log('Attempting stored auto-login...');
        
        const response = await fetch('/api/login/auto-login', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            }
        });
        
        const result = await response.json();
        console.log('Auto-login result:', result);
        
        if (result.status === 1 || result.status === 3) {
            // Login successful
            console.log('Auto-login successful');
        } else if (result.status === 2) {
            // Already logged in
            console.log('Already logged in');
        } else if (result.status === 0) {
            // Login failed
            console.log('Auto-login failed:', result);
        } else if (result.status === -1) {
            // Deezer not available
            console.log('Deezer not available:', result);
        } else {
            // Unknown status
            console.log('Auto-login failed with unknown status:', result);
        }
        return result;
    } catch (error) {
        console.error('Error during auto-login:', error);
        return null;
    }
}

// Load data when page loads
document.addEventListener('DOMContentLoaded', async function() {
    setHomeGreeting();
    // Start home render immediately; avoid blocking first paint on connect/auth checks.
    try {
        await loadHomeData();
    } catch (error) {
        console.error('Home data load failed:', error);
    }
    // Run auto-login in background and refresh home once if credentials were hydrated.
    void checkAutoLogin().then((wasAuthenticated) => {
        if (wasAuthenticated) {
            void loadHomeData();
        }
    });
    applySearchSourceState();
    document.body.addEventListener('click', (event) => {
        const card = event.target.closest('[data-spotify-item]');
        if (!card) {
            return;
        }
        const key = card.dataset.spotifyItem;
        if (!key || !spotifyHomeItemCache.has(key)) {
            return;
        }
        openSpotifyItem(spotifyHomeItemCache.get(key));
    });
    document.body.addEventListener('click', (event) => {
        const trigger = event.target.closest('[data-home-trending-tracklist]');
        if (!trigger) {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        openHomeTrendingTracklist(trigger);
    });
});
