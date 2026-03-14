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
    try {
        const url = new URL(string);
        return url.protocol === 'http:' || url.protocol === 'https:';
    } catch (_) {
        return false;
    }
}


const showToast = window.HomeViewHelpers.showToast;

const HOME_TRENDING_SPOTIFY_SOURCE_ID = 'home-trending-songs';
const homeSpotifyResolveCache = new Map();
const spotifyHomeItemCache = new Map();
const homeTrendingWatchMetaCache = new Map();
let spotifyHomeItemSeq = 0;
let homeTrendingWatchSeq = 0;
let spotifyBrowseCategoriesCache = null;
let spotifyBrowseCategoriesLoading = false;
const homeTrendingPreviewState = {
    audio: null,
    trackKey: null,
    button: null,
    requestId: 0,
    pendingKey: null
};
const homeDeezerPlaybackContextCache = new Map();
const homeDeezerPlaybackContextRequests = new Map();

// Lazy image loading with IntersectionObserver for faster initial render
const lazyImageObserver = new IntersectionObserver((entries) => {
    entries.forEach(entry => {
        if (entry.isIntersecting) {
            const el = entry.target;
            const bgUrl = el.getAttribute('data-lazy-bg');
            if (bgUrl) {
                el.style.backgroundImage = `url('${bgUrl}')`;
                el.removeAttribute('data-lazy-bg');
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
    try {
        return Intl.DateTimeFormat().resolvedOptions().timeZone || 'America/New_York';
    } catch (error) {
        return 'America/New_York';
    }
}

function buildSpotifyWebUrl(uri) {
    if (!uri) {
        return '';
    }
    if (uri.startsWith('http://') || uri.startsWith('https://')) {
        return uri;
    }
    if (uri.startsWith('spotify:')) {
        const parts = uri.split(':');
        if (parts.length >= 3) {
            return `https://open.spotify.com/${parts[1]}/${parts[2]}`;
        }
    }
    return '';
}

async function openSpotifyItem(item) {
    if (!item) {
        return;
    }
    const itemType = (item.type || '').toLowerCase();
    if (itemType === 'category') {
        await openSpotifyBrowseCategory(item);
        return;
    }
    const url = buildSpotifyWebUrl(item.uri || '');
    const parsed = parseSpotifyUrl(url);
    const fallbackId = item.id || '';
    const fallbackType = itemType || '';
    const resolvedType = parsed?.type || fallbackType;
    const resolvedId = parsed?.id || fallbackId;

    if (resolvedType === 'artist' && resolvedId) {
        openSpotifyArtist(resolvedId, encodeURIComponent(item.name || ''));
        return;
    }
    if (resolvedType && resolvedId) {
        window.location.href = `/Tracklist?id=${encodeURIComponent(resolvedId)}&type=${encodeURIComponent(resolvedType || 'album')}&source=spotify`;
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
    window.location.href = `/Spotify/Browse?${params.toString()}`;
}

function parseSpotifyUrl(url) {
    if (!url) {
        return null;
    }
    if (url.startsWith('spotify:')) {
        const uriParts = url.split(':');
        if (uriParts.length >= 3 && uriParts[1] && uriParts[2]) {
            return { type: uriParts[1].toLowerCase(), id: uriParts[2] };
        }
    }
    const directMatch = url.match(/open\.spotify\.com\/(?:intl-[a-z-]+\/)?(album|playlist|track|show|episode|artist|station)\/([a-zA-Z0-9]+)/i);
    if (directMatch) {
        return { type: directMatch[1].toLowerCase(), id: directMatch[2] };
    }
    try {
        const parsed = new URL(url);
        const segments = parsed.pathname.split('/').filter(Boolean);
        const kindIndex = segments.findIndex(seg => /^(album|playlist|track|show|episode|artist|station)$/i.test(seg));
        if (kindIndex >= 0 && segments[kindIndex + 1]) {
            return { type: segments[kindIndex].toLowerCase(), id: segments[kindIndex + 1] };
        }
    } catch {
        return null;
    }
    return null;
}

const setHomeTrendingPreviewButtonState = window.HomeViewHelpers.setHomeTrendingPreviewButtonState;

function clearHomeTrendingPreviewButton() {
    if (!homeTrendingPreviewState.button) {
        return;
    }
    setHomeTrendingPreviewButtonState(homeTrendingPreviewState.button, false);
    homeTrendingPreviewState.button = null;
}

function normalizeHomeDeezerPlaybackContext(payload) {
    return window.HomeViewHelpers.normalizeHomeDeezerPlaybackContext(payload);
}

function buildHomeDeezerStreamUrl(deezerId, context) {
    return window.HomeViewHelpers.buildHomeDeezerStreamUrl(deezerId, context);
}

async function fetchHomeDeezerPlaybackContext(deezerId) {
    return await window.HomeViewHelpers.fetchHomeDeezerPlaybackContext(
        deezerId,
        homeDeezerPlaybackContextCache,
        homeDeezerPlaybackContextRequests
    );
}

async function resolveSpotifyUrlToDeezerHome(url) {
    if (!url) {
        return null;
    }
    if (homeSpotifyResolveCache.has(url)) {
        return homeSpotifyResolveCache.get(url);
    }
    try {
        const response = await fetch(`/api/spotify/resolve-deezer?url=${encodeURIComponent(url)}`);
        if (!response.ok) {
            return null;
        }
        const raw = await response.text();
        const trimmed = raw.trim();
        if (!trimmed || trimmed === 'undefined') {
            return null;
        }
        const resolved = JSON.parse(raw);
        homeSpotifyResolveCache.set(url, resolved);
        return resolved;
    } catch {
        return null;
    }
}

async function playHomeTrendingTrackInApp(button) {
    if (!button) {
        return;
    }

    const deezerId = (button.dataset.deezerId || '').trim();
    const spotifyUrl = (button.dataset.spotifyUrl || '').trim();
    const previewUrl = (button.dataset.previewUrl || '').trim();
    const intentKey = previewUrl
        ? `preview:${previewUrl}`
        : (deezerId ? `deezer:${deezerId}` : (spotifyUrl ? `spotify:${spotifyUrl}` : ''));
    if (intentKey && homeTrendingPreviewState.pendingKey === intentKey) {
        return;
    }
    const requestId = ++homeTrendingPreviewState.requestId;
    homeTrendingPreviewState.pendingKey = intentKey || null;
    const isStaleRequest = () => requestId !== homeTrendingPreviewState.requestId;

    let streamUrl = '';
    let trackKey = '';

    try {
        if (previewUrl) {
            streamUrl = previewUrl;
            trackKey = previewUrl;
        } else if (deezerId) {
            if (window.DeezerPlaybackContext && typeof window.DeezerPlaybackContext.resolveStreamUrl === 'function') {
                streamUrl = await window.DeezerPlaybackContext.resolveStreamUrl(deezerId, {
                    element: button,
                    cache: homeDeezerPlaybackContextCache,
                    requests: homeDeezerPlaybackContextRequests
                });
            } else {
                streamUrl = `/api/deezer/stream/${encodeURIComponent(deezerId)}`;
            }
            trackKey = `deezer:${deezerId}`;
        } else if (spotifyUrl) {
            const resolved = await resolveSpotifyUrlToDeezerHome(spotifyUrl);
            if (isStaleRequest()) {
                return;
            }
            if (!resolved || resolved.available === false || resolved.type !== 'track' || !resolved.deezerId) {
                showToast('Track not available for streaming.', 'warning');
                return;
            }
            const resolvedDeezerId = String(resolved.deezerId);
            button.dataset.deezerId = resolvedDeezerId;
            if (window.DeezerPlaybackContext && typeof window.DeezerPlaybackContext.resolveStreamUrl === 'function') {
                streamUrl = await window.DeezerPlaybackContext.resolveStreamUrl(resolvedDeezerId, {
                    element: button,
                    cache: homeDeezerPlaybackContextCache,
                    requests: homeDeezerPlaybackContextRequests
                });
            } else {
                streamUrl = `/api/deezer/stream/${encodeURIComponent(resolvedDeezerId)}`;
            }
            trackKey = `deezer:${resolvedDeezerId}`;
        } else {
            showToast('Preview unavailable.', 'warning');
            return;
        }

        if (!streamUrl || !trackKey || isStaleRequest()) {
            return;
        }

        const audio = homeTrendingPreviewState.audio ?? new Audio();

        if (homeTrendingPreviewState.trackKey === trackKey && !audio.paused) {
            audio.pause();
            clearHomeTrendingPreviewButton();
            homeTrendingPreviewState.trackKey = null;
            return;
        }

        if (homeTrendingPreviewState.button !== button) {
            clearHomeTrendingPreviewButton();
        }

        homeTrendingPreviewState.audio = audio;
        homeTrendingPreviewState.trackKey = trackKey;
        homeTrendingPreviewState.button = button;
        setHomeTrendingPreviewButtonState(button, true);

        // Ensure old request/stream is fully detached before setting a new source.
        audio.pause();
        audio.onended = null;
        audio.onerror = null;
        audio.src = streamUrl;
        audio.currentTime = 0;

        audio.onended = () => {
            if (isStaleRequest()) {
                return;
            }
            clearHomeTrendingPreviewButton();
            homeTrendingPreviewState.trackKey = null;
        };
        audio.onerror = () => {
            if (isStaleRequest()) {
                return;
            }
            clearHomeTrendingPreviewButton();
            homeTrendingPreviewState.trackKey = null;
            showToast('Playback interrupted.', 'warning');
        };

        try {
            await audio.play();
            if (isStaleRequest()) {
                audio.pause();
            }
        } catch {
            if (isStaleRequest()) {
                return;
            }
            setHomeTrendingPreviewButtonState(button, false);
            homeTrendingPreviewState.trackKey = null;
            if (homeTrendingPreviewState.button === button) {
                homeTrendingPreviewState.button = null;
            }
            showToast('Unable to start playback.', 'warning');
        }
    } finally {
        if (requestId === homeTrendingPreviewState.requestId) {
            homeTrendingPreviewState.pendingKey = null;
        }
    }
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

const SEARCH_LINK_PLACEHOLDER = 'Search Deezer tracks, albums, artists, playlists... or paste Spotify/Apple Music/YouTube/SoundCloud/Tidal/Boomplay/Qobuz/Bandcamp/Pandora links to map to Deezer';
const SUPPORTED_LINK_SOURCES = 'Spotify, Apple Music, YouTube, SoundCloud, Tidal, Boomplay, Deezer, Qobuz, Bandcamp, Pandora';

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
            return path.includes('/playlist/');
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

function extractExternalCollectionId(parsedUrl, source) {
    if (!parsedUrl || !(parsedUrl instanceof URL) || !source) {
        return '';
    }

    const pathSegments = (parsedUrl.pathname || '')
        .split('/')
        .filter(Boolean)
        .map(segment => segment.trim())
        .filter(Boolean);

    if (source === 'youtube') {
        return (parsedUrl.searchParams.get('list') || '').trim();
    }
    if (source === 'spotify') {
        const match = parsedUrl.pathname.match(/\/(?:intl-[a-z]{2}\/)?playlist\/([A-Za-z0-9]+)/i);
        return (match && match[1]) ? match[1] : '';
    }
    if (source === 'apple') {
        const withSlug = parsedUrl.pathname.match(/\/playlist\/[^\/]+\/([^\/?#]+)/i);
        const direct = parsedUrl.pathname.match(/\/playlist\/([^\/?#]+)/i);
        return (withSlug && withSlug[1]) || (direct && direct[1]) || '';
    }
    if (source === 'boomplay') {
        const match = parsedUrl.pathname.match(/\/playlists?\/([A-Za-z0-9]+)/i);
        return (match && match[1]) ? match[1] : '';
    }
    if (source === 'soundcloud') {
        const setsIndex = pathSegments.findIndex(segment => segment.toLowerCase() === 'sets');
        return setsIndex >= 0 && pathSegments[setsIndex + 1] ? pathSegments[setsIndex + 1] : '';
    }
    if (source === 'tidal') {
        const playlistIndex = pathSegments.findIndex(segment => segment.toLowerCase() === 'playlist');
        if (playlistIndex >= 0 && pathSegments[playlistIndex + 1]) {
            return pathSegments[playlistIndex + 1];
        }
        const mixIndex = pathSegments.findIndex(segment => segment.toLowerCase() === 'mix');
        return mixIndex >= 0 && pathSegments[mixIndex + 1] ? pathSegments[mixIndex + 1] : '';
    }
    if (source === 'qobuz') {
        const playlistIndex = pathSegments.findIndex(segment => segment.toLowerCase() === 'playlist');
        return playlistIndex >= 0 && pathSegments[playlistIndex + 1] ? pathSegments[playlistIndex + 1] : '';
    }
    if (source === 'pandora') {
        const playlistIndex = pathSegments.findIndex(segment => segment.toLowerCase() === 'playlist');
        if (playlistIndex >= 0) {
            return pathSegments[pathSegments.length - 1] || '';
        }
        return '';
    }
    if (source === 'bandcamp') {
        const albumIndex = pathSegments.findIndex(segment => segment.toLowerCase() === 'album');
        return albumIndex >= 0 && pathSegments[albumIndex + 1] ? pathSegments[albumIndex + 1] : '';
    }

    return '';
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

    if (source === 'youtube') {
        const listId = (parsedUrl.searchParams.get('list') || '').trim();
        if (/^[A-Za-z0-9_-]{10,}$/.test(listId)) {
            return `/Tracklist?id=${encodeURIComponent(listId)}&type=playlist&source=youtube`;
        }
        return '';
    }

    if (source === 'spotify') {
        const spotifyPlaylistMatch = parsedUrl.pathname.match(/\/(?:intl-[a-z]{2}\/)?playlist\/([A-Za-z0-9]+)/i);
        if (spotifyPlaylistMatch && spotifyPlaylistMatch[1]) {
            return `/Tracklist?id=${encodeURIComponent(spotifyPlaylistMatch[1])}&type=playlist&source=spotify`;
        }
        return '';
    }

    if (source === 'apple') {
        const applePlaylistWithSlug = parsedUrl.pathname.match(/\/playlist\/[^\/]+\/([^\/?#]+)/i);
        const applePlaylistDirect = parsedUrl.pathname.match(/\/playlist\/([^\/?#]+)/i);
        const applePlaylistId = (applePlaylistWithSlug && applePlaylistWithSlug[1])
            || (applePlaylistDirect && applePlaylistDirect[1])
            || '';
        if (applePlaylistId) {
            return `/Tracklist?id=${encodeURIComponent(applePlaylistId)}&type=playlist&source=apple&appleUrl=${encodeURIComponent(sourceUrl)}`;
        }
        return '';
    }

    if (source === 'boomplay') {
        const boomplayPlaylistMatch = parsedUrl.pathname.match(/\/playlists?\/([A-Za-z0-9]+)/i);
        if (boomplayPlaylistMatch && boomplayPlaylistMatch[1]) {
            return `/Tracklist?id=${encodeURIComponent(boomplayPlaylistMatch[1])}&type=playlist&source=boomplay`;
        }
        return '';
    }

    const genericPlaylistSources = new Set(['soundcloud', 'tidal', 'qobuz', 'bandcamp', 'pandora']);
    if (!genericPlaylistSources.has(source)) {
        return '';
    }

    const collectionId = extractExternalCollectionId(parsedUrl, source) || 'playlist';
    return `/Tracklist?id=${encodeURIComponent(collectionId)}&type=playlist&source=${encodeURIComponent(source)}&externalUrl=${encodeURIComponent(sourceUrl)}`;
}

function navigateToMappedDeezer(mapping) {
    if (!mapping || mapping.available !== true) {
        return false;
    }

    const deezerType = (mapping.deezerType || '').toString().trim().toLowerCase();
    const deezerId = (mapping.deezerId || '').toString().trim();
    const hasNumericDeezerId = /^\d+$/.test(deezerId);

    if (deezerType === 'artist' && hasNumericDeezerId) {
        window.location.href = `/Artist?id=${encodeURIComponent(deezerId)}&source=deezer`;
        return true;
    }

    if (hasNumericDeezerId) {
        const tracklistType = deezerType || 'track';
        window.location.href = `/Tracklist?id=${encodeURIComponent(deezerId)}&type=${encodeURIComponent(tracklistType)}&source=deezer`;
        return true;
    }

    return false;
}

async function performUnifiedSearch() {
    const input = document.getElementById('unified-search').value.trim();
    const type = 'track'; // Default to track search since dropdown is removed
    
    if (!input) {
        DeezSpoTag.ui.alert('Please enter a search term or URL', { title: 'Search' });
        return;
    }
    
    try {
        // Show loading state
        const searchBtn = document.getElementById('unified-search-btn');
        searchBtn.textContent = 'Searching...';
        searchBtn.disabled = true;
        
        // Check if input is a URL (like deezspotag)
        if (isValidURL(input)) {
            const parsedUrl = new URL(input);
            const externalPlaylistRoute = tryBuildExternalPlaylistRoute(parsedUrl, input);
            if (externalPlaylistRoute) {
                window.location.href = externalPlaylistRoute;
                return;
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
                    window.location.href = `/Artist?id=${encodeURIComponent(parsed.id)}&source=deezer`;
                } else {
                    const typeValue = deezerType || 'track';
                    window.location.href = `/Tracklist?id=${encodeURIComponent(parsed.id)}&type=${encodeURIComponent(typeValue)}&source=deezer`;
                }
                return;
            }

            const mapping = await mapInputLinkToDeezer(input);
            if (navigateToMappedDeezer(mapping)) {
                return;
            }

            const reason = (mapping?.reason || '').toString().trim();
            const sourceLabel = (mapping?.source || 'provided').toString();
            const message = reason
                ? `${reason} Supported link sources: ${SUPPORTED_LINK_SOURCES}.`
                : `No Deezer mapping found for ${sourceLabel} link. Supported link sources: ${SUPPORTED_LINK_SOURCES}.`;
            throw new Error(message);

            return;
        } else {
            console.log('Detected search term, redirecting to search results:', input);

            // Navigate to search results page
            const searchParams = new URLSearchParams({
                term: input,
                type: type
            });
            window.location.href = `/Search?${searchParams.toString()}`;
        }
        
        // Reset button state
        searchBtn.textContent = 'Search';
        searchBtn.disabled = false;
        
    } catch (error) {
        console.error('Search/Download error:', error);
        DeezSpoTag.ui.alert(`Operation failed: ${error.message}`, { title: 'Search' });
        
        // Reset button state
        const searchBtn = document.getElementById('unified-search-btn');
        searchBtn.textContent = 'Search';
        searchBtn.disabled = false;
    }
}

// Load popular content
async function loadHomeData() {
    try {
        const urlParams = new URLSearchParams(window.location.search);
        const channel = urlParams.get('channel');
        const refresh = urlParams.get('refresh');
        const refreshEnabled = refresh === '1' || refresh === 'true' || refresh === 'yes';

        console.log('Loading home data...');
        let spotifySectionsPromise = null;
        if (!channel) {
            const tz = encodeURIComponent(getBrowserTimeZone());
            spotifySectionsPromise = fetch(`/api/spotify/home-feed/sections?timeZone=${tz}`)
                .then(async (response) => {
                    if (!response.ok) {
                        return [];
                    }
                    const payload = await response.json();
                    return Array.isArray(payload?.sections) ? payload.sections : [];
                })
                .catch((error) => {
                    console.warn('Spotify home sections merge failed:', error);
                    return [];
                });
        }

        const baseUrl = channel ? `/api/home?channel=${encodeURIComponent(channel)}` : '/api/home';
        const response = await fetch(
            refreshEnabled ? `${baseUrl}${baseUrl.includes('?') ? '&' : '?'}refresh=1` : baseUrl,
            { cache: 'no-store' }
        );
        
        if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
        }
        
        const data = await response.json();
        console.log('Home data received:', data);

        let sections = data.sections || [];
        if (spotifySectionsPromise) {
            const spotifySections = (await spotifySectionsPromise)
                .map(section => ({ ...section, source: 'spotify' }));
            if (spotifySections.length > 0) {
                const insertAfter = 'discover';
                const insertIndex = sections.findIndex(sec => {
                    const title = (sec?.title || '').toString().trim().toLowerCase();
                    return title === insertAfter;
                });
                if (insertIndex >= 0) {
                    sections = [
                        ...sections.slice(0, insertIndex + 1),
                        ...spotifySections,
                        ...sections.slice(insertIndex + 1)
                    ];
                } else {
                    sections = [...sections, ...spotifySections];
                }
            }
        }

        renderHomeSections(sections);
        observeLazyImages(document.getElementById('home-sections'));

    } catch (error) {
        console.error('Error loading home data:', error);
        document.getElementById('home-sections').innerHTML = '<div class="empty-section">Failed to load home sections</div>';
    }
}

function renderHomeSections(sections) {
    const container = document.getElementById('home-sections');
    if (!sections || sections.length === 0) {
        container.innerHTML = '<div class="empty-section">No home sections available</div>';
        return;
    }

    const urlParams = new URLSearchParams(window.location.search);
    const isChannelPage = !!urlParams.get('channel');
    const maxItemsPerSection = isChannelPage ? 60 : 16;
    const normalizeTitle = (value) => (value || '').toString().trim().toLowerCase();
    const normalizeSectionTitle = (value) => normalizeTitle(value).replace(/\s+/g, ' ');
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
        const title = normalizeSectionTitle(section?.title);
        return title === 'new releases for you' || title === 'new release for you';
    };
    const isPopularRadioSection = (section) => {
        const title = normalizeSectionTitle(section?.title);
        return title === 'popular radio' || title === 'popular radios';
    };
    const isRecentlyPlayedSection = (section) => {
        const title = normalizeSectionTitle(section?.title);
        return title === 'recently played';
    };
    const normalizeArtistNameKey = (value) => {
        if (value === null || value === undefined) {
            return '';
        }
        return value
            .toString()
            .normalize('NFKD')
            .replace(/[\u0300-\u036f]/g, '')
            .replace(/['`´’]/g, '')
            .replace(/[._-]/g, ' ')
            .replace(/\s+/g, ' ')
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
    const dedupeMergedPopularArtistItems = (items) => {
        if (!Array.isArray(items) || items.length === 0) {
            return [];
        }

        // Pass 1: remove exact duplicates by stable platform identity (source+type+id).
        const seenExact = new Map();
        const stableItems = [];
        for (const item of items) {
            if (!item || typeof item !== 'object') {
                continue;
            }

            const source = getItemSource(item);
            const type = getItemType(item);
            const id = getItemId(item);
            if (source && type && id) {
                const key = `${source}:${type}:${id}`;
                if (seenExact.has(key)) {
                    const existingIndex = seenExact.get(key);
                    const existingItem = stableItems[existingIndex];
                    if (isArtistItem(existingItem) && isArtistItem(item)) {
                        stableItems[existingIndex] = choosePreferredArtistCard(existingItem, item);
                    }
                    continue;
                }
                seenExact.set(key, stableItems.length);
            }
            stableItems.push(item);
        }

        // Pass 2: collapse same artist shown from different sources by canonical exact name.
        const firstArtistByName = new Map();
        const deduped = [];
        for (const item of stableItems) {
            if (!isArtistItem(item)) {
                deduped.push(item);
                continue;
            }

            const nameKey = normalizeArtistNameKey(getArtistDisplayName(item));
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
            if (!isSameCrossSourceArtist(existingItem, item)) {
                deduped.push(item);
                continue;
            }

            deduped[existingIndex] = choosePreferredArtistCard(existingItem, item);
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
    sections = sections.filter(section => {
        if (!isRecentlyPlayedSection(section)) {
            return true;
        }
        const itemCount = Array.isArray(section?.items) ? section.items.length : 0;
        return itemCount >= 4;
    });
    const hasUnifiedSpotifySections = sections.some(section => isSpotifyHomeSection(section));
    if (!isChannelPage) {
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
                const categoriesIndex = sections.findIndex(section => section === categoriesSection);
                const targetIndex = categoriesIndex >= 0 && categoriesIndex <= trimmed.length ? categoriesIndex : trimmed.length;
                trimmed.splice(targetIndex, 0, alias);
            }
            sections = trimmed;
        } else if (topGenresSection) {
            topGenresSection.title = 'Categories';
        }

        const continueStreamingIndex = sections.findIndex(section => isContinueStreamingSection(section));
        const popularArtistsIndex = sections.findIndex(section => isPopularArtistsSection(section));
        if (continueStreamingIndex >= 0) {
            const continueSection = sections[continueStreamingIndex] || {};
            const continueArtistItems = extractArtistItems(continueSection);
            const hasExplicitPopularArtistsSection = popularArtistsIndex >= 0 && popularArtistsIndex !== continueStreamingIndex;
            const popularArtistsSection = hasExplicitPopularArtistsSection
                ? (sections[popularArtistsIndex] || {})
                : null;
            const spotifyArtistFallbackItems = hasExplicitPopularArtistsSection
                ? []
                : collectSpotifyArtistItemsForContinueMerge(sections, new Set([continueStreamingIndex]));
            const spotifyArtistItems = hasExplicitPopularArtistsSection
                ? extractArtistItems(popularArtistsSection)
                : spotifyArtistFallbackItems;

            if (continueArtistItems.length > 0 || spotifyArtistItems.length > 0) {
                const mergedArtistItems = dedupeMergedPopularArtistItems([
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

                if (hasExplicitPopularArtistsSection) {
                    const compactSections = sections.filter((_, index) => index !== continueStreamingIndex && index !== popularArtistsIndex);
                    const mergedInsertIndex = popularArtistsIndex - (continueStreamingIndex < popularArtistsIndex ? 1 : 0);
                    compactSections.splice(Math.max(0, mergedInsertIndex), 0, mergedSection);
                    sections = compactSections;
                } else {
                    sections[continueStreamingIndex] = mergedSection;
                }
            }
        }

        const recommendedTodayIndex = sections.findIndex(section => isRecommendedForTodaySection(section));
        const newReleasesForYouIndex = sections.findIndex(section => isNewReleasesForYouSection(section));
        if (recommendedTodayIndex >= 0 && newReleasesForYouIndex >= 0 && recommendedTodayIndex !== newReleasesForYouIndex) {
            const recommendedSection = sections[recommendedTodayIndex] || {};
            const newReleasesForYouSection = sections[newReleasesForYouIndex] || {};
            const mergeRecommendedAndNewReleases =
                isSpotifyHomeSection(recommendedSection) && isSpotifyHomeSection(newReleasesForYouSection);
            if (mergeRecommendedAndNewReleases) {
                const mergedSection = {
                    ...recommendedSection,
                    title: 'Recommended new releases for you',
                    __preserveAllItems: true,
                    items: [
                        ...(Array.isArray(recommendedSection.items) ? recommendedSection.items : []),
                        ...(Array.isArray(newReleasesForYouSection.items) ? newReleasesForYouSection.items : [])
                    ],
                    hasMore: (recommendedSection.hasMore === true) || (newReleasesForYouSection.hasMore === true),
                    pagePath: recommendedSection.pagePath || newReleasesForYouSection.pagePath || '',
                    related: recommendedSection.related || newReleasesForYouSection.related || null,
                    filter: recommendedSection.filter || newReleasesForYouSection.filter || null,
                    layout: recommendedSection.layout || newReleasesForYouSection.layout || 'row'
                };
                const compactSections = sections.filter((_, index) => index !== recommendedTodayIndex && index !== newReleasesForYouIndex);
                const popularRadioIndex = compactSections.findIndex(section => isPopularRadioSection(section));
                const fallbackInsertIndex = Math.min(recommendedTodayIndex, newReleasesForYouIndex);
                const insertIndex = popularRadioIndex >= 0
                    ? popularRadioIndex
                    : Math.min(fallbackInsertIndex, compactSections.length);
                compactSections.splice(insertIndex, 0, mergedSection);
                sections = compactSections;
            }
        }

        const recommendedNewReleasesIndex = sections.findIndex(
            section => normalizeSectionTitle(section?.title) === 'recommended new releases for you'
        );
        const categoriesIndex = sections.findIndex(
            section => normalizeTitle(section?.title) === 'categories'
        );
        if (recommendedNewReleasesIndex >= 0 && categoriesIndex >= 0) {
            const [categoriesSection] = sections.splice(categoriesIndex, 1);
            const refreshedRecommendedIndex = sections.findIndex(
                section => normalizeSectionTitle(section?.title) === 'recommended new releases for you'
            );
            const refreshedPopularRadioIndex = sections.findIndex(
                section => isPopularRadioSection(section)
            );
            const insertAfterRecommendedIndex = refreshedRecommendedIndex >= 0
                ? ((refreshedPopularRadioIndex > refreshedRecommendedIndex)
                    ? refreshedPopularRadioIndex + 1
                    : refreshedRecommendedIndex + 1)
                : sections.length;
            sections.splice(insertAfterRecommendedIndex, 0, categoriesSection);
        }
    }

    if (!sections.length) {
        container.innerHTML = '<div class="empty-section">No home sections available</div>';
        return;
    }

    const orderedSections = (() => {
        if (isChannelPage) {
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

        // Explicit homepage pin order requested by product design.
        pushPinned(section => isSpotifyHomeSection(section) && normalizeSectionTitle(section?.title) === 'trending songs');
        pushPinned(section => isSpotifyHomeSection(section) && isMadeForYouSection(section));
        pushPinned(section => normalizeSectionTitle(section?.title) === 'discover');
        pushPinned(section => normalizeSectionTitle(section?.title) === 'recommended new releases for you');
        pushPinned(section => isPopularRadioSection(section));
        pushPinned(section => normalizeTitle(section?.title) === 'categories' || normalizeTitle(section?.title) === 'your top genres');

        const remaining = normalizedSections.filter((_, index) => !used.has(index));
        return [...pinned, ...remaining];
    })();

    const minItemsToRenderSection = 4;
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

    const computeSectionRenderMeta = (section) => {
        const normalizedTitle = normalizeTitle(section?.title);
        const isTrendingSongs = normalizedTitle === 'trending songs';
        const preserveAllItems = section?.__preserveAllItems === true;
        const sectionLimit = preserveAllItems
            ? ((Array.isArray(section?.items) && section.items.length > 0) ? section.items.length : maxItemsPerSection)
            : (isChannelPage ? 60 : (isTrendingSongs ? 20 : maxItemsPerSection));
        const items = Array.isArray(section?.items) ? section.items.slice(0, sectionLimit) : [];
        const layoutRaw = (section?.layout || '').toString().toLowerCase();
        const filter = section?.filter || null;
        const hasFilter = layoutRaw === 'filterable-grid' && Array.isArray(filter?.options) && filter.options.length > 0;
        const isDiscover = normalizedTitle === 'discover';
        const isTopGenres = normalizedTitle === 'your top genres' || normalizedTitle === 'categories';
        const isChannelSection = items.length > 0 && items.every(item => (item?.type || '').toString().toLowerCase() === 'channel');
        const layoutClass = layoutRaw === 'grid' ? 'home-grid' : 'home-row';
        const rowClass = isDiscover
            ? 'discover-grid'
            : isTrendingSongs
            ? 'home-trending-grid'
            : (isTopGenres || isChannelSection) ? 'top-genres-grid' : layoutClass;
        const filteredItems = (isDiscover || isTopGenres || isTrendingSongs)
            ? items
            : items.filter(item => !isGhostHomeItem(item));
        const isSpaceAware = filteredItems.length > 0
            && filteredItems.length < spaceAwareThreshold
            && rowClass !== 'discover-grid';

        return {
            normalizedTitle,
            isTrendingSongs,
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
    };

    const sectionsToRender = orderedSections
        .map(section => ({ section, meta: computeSectionRenderMeta(section) }))
        .filter(entry => {
            const itemCount = entry.meta.filteredItems.length;
            if (itemCount >= minItemsToRenderSection) {
                return true;
            }
            return itemCount >= 1 && isSingleCardNewReleaseSection(entry.meta.normalizedTitle);
        });

    if (!sectionsToRender.length) {
        container.innerHTML = '<div class="empty-section">No home sections available</div>';
        return;
    }

    homeTrendingWatchMetaCache.clear();
    container.innerHTML = sectionsToRender.map((entry, index) => {
        const section = entry.section;
        const meta = entry.meta;
        const filterId = `home-filter-${index}`;
        const filtersHtml = meta.hasFilter
            ? `<div class="home-filters" data-filter-group="${filterId}">
                ${meta.filter.options.map(opt => `<button class="home-filter-btn" data-filter-id="${opt.id}">${escapeHtml(opt.label || opt.id)}</button>`).join('')}
               </div>`
            : '';
        const showMore = section.hasMore && section.pagePath
            ? `<div class="home-section-more"><button class="action-btn action-btn-sm" onclick="openHomeChannel('${section.pagePath}')">Show more</button></div>`
            : '';
        const maxTopGenresSlots = isChannelPage ? meta.items.length : 14;
        const baseTopGenresItems = meta.isTopGenres ? meta.items.slice(0, maxTopGenresSlots) : [];
        const topGenresCards = meta.isTopGenres
            ? (() => {
                const mergedItems = mergeSpotifyCategories(baseTopGenresItems, maxTopGenresSlots);
                const sliced = mergedItems.map(renderTopGenresItem).join('');
                const showMoreCard = !isChannelPage
                    ? `<a class="top-genres-card top-genres-card--more" href="/Categories">View all</a>`
                    : '';
                return `${sliced}${showMoreCard}`;
            })()
            : '';
        const sectionIdAttr = meta.isTrendingSongs ? ' id="home-trending"' : '';
        const trendingWatchKey = meta.isTrendingSongs
            ? cacheHomeTrendingWatchMeta(section, meta.filteredItems)
            : '';
        const titleHtml = trendingWatchKey
            ? `<button type="button" class="home-section-title home-section-title--action" data-home-trending-tracklist="${trendingWatchKey}">${escapeHtml(section.title || '')}</button>`
            : `<div class="home-section-title">${escapeHtml(section.title || '')}</div>`;
        const sectionItemsHtml = meta.isTopGenres
            ? topGenresCards
            : (meta.isTrendingSongs
                ? meta.filteredItems.map(item => renderHomeTrendingSongItem(item)).join('')
                : (meta.filteredItems.map(item => {
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
                    const rendered = meta.isDiscover ? renderDiscoveryItem(item) : meta.isTopGenres ? renderTopGenresItem(item) : renderHomeItem(item);
                    return `<div class="home-filtered-item"${dataAttr}>${rendered}</div>`;
                }).join('')));
        const rowClass = meta.isSpaceAware ? `${meta.rowClass} home-space-aware-row` : meta.rowClass;
        const rowSpaceAwareMin = meta.rowClass === 'home-grid'
            ? 'var(--art-grid-min)'
            : (meta.rowClass === 'home-row' ? 'var(--art-card-size)' : '0px');
        const rowStyle = meta.isSpaceAware
            ? `style="--home-space-aware-count:${meta.filteredItems.length}; --home-space-aware-min:${rowSpaceAwareMin};"`
            : '';
        return `
            <div class="home-section"${sectionIdAttr}>
                ${titleHtml}
                ${filtersHtml}
                <div class="${rowClass}" ${rowStyle} ${meta.isTopGenres ? `data-deezer-items="${encodeURIComponent(JSON.stringify(baseTopGenresItems))}"` : ''}>
                    ${sectionItemsHtml}
                </div>
                ${meta.isTopGenres ? '' : showMore}
            </div>
        `;
    }).join('');

    setupHomeFilters(sectionsToRender.map(entry => entry.section));
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
    const watchKey = button?.getAttribute('data-home-trending-tracklist') || '';
    const watchMeta = watchKey && homeTrendingWatchMetaCache.has(watchKey)
        ? homeTrendingWatchMetaCache.get(watchKey)
        : null;
    const sourceId = (watchMeta?.sourceId || HOME_TRENDING_SPOTIFY_SOURCE_ID).toString().trim() || HOME_TRENDING_SPOTIFY_SOURCE_ID;
    window.location.href = `/Tracklist?id=${encodeURIComponent(sourceId)}&type=playlist&source=spotify`;
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
                const ids = (el.getAttribute('data-filter-ids') || '').split(',').filter(Boolean);
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
        const normalized = trimmed.replace(/[^\d-]/g, '');
        if (!normalized || normalized === '-') {
            return null;
        }
        const parsed = parseInt(normalized, 10);
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
    const grouped = abs.toString().replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
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

    subtitle = subtitle.replace(/\bfollowers?\b/gi, 'fans');
    subtitle = subtitle.replace(/(\d[\d\s,._-]*)\s*(tracks?|songs?|fans?|followers?)/gi, (_, numberPart, labelPart) => {
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

function renderHomeItem(item) {
    if (!item) {
        return '';
    }
    const isSpotifyItem = (item.source || '').toString().toLowerCase() === 'spotify';
    let spotifyItemKey = '';
    if (isSpotifyItem) {
        spotifyItemKey = `spotify-item-${spotifyHomeItemSeq++}`;
        spotifyHomeItemCache.set(spotifyItemKey, item);
    }
    const imageType = item.picture_type || item.pictureType || item.imageType || 'cover';
    const thumbSize = item.type === 'artist' || item.type === 'channel' ? 140 : 250;
    const imageHash = item.md5_image || item.md5 || item.imageHash || item.PLAYLIST_PICTURE || item.playlistPicture || item.cover?.md5 || item.cover?.MD5 || item.background_image?.md5;
    const image = upgradeSpotifyImageUrl(extractFirstUrl(
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
    const logoHash = item.logo || item.logo_image?.md5 || item.logo_image?.MD5;
    const logoImage = logoHash ? buildDeezerLogoUrlWithSize(logoHash, 'misc', 52, 100) : '';
    const titleRaw = (item.title || item.name || '').toString().trim();
    if (!titleRaw) {
        return '';
    }
    const title = escapeHtml(titleRaw);
    const subtitleRaw = normalizeHomeSubtitle(item, item.subtitle || '');
    let subtitle = escapeHtml(subtitleRaw);
    let badges = [];
    if (item.type === 'playlist') {
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
        const statBadges = subtitleHasCounts
            ? []
            : [
                parsedPlaylistTracks !== null ? { label: `${formatHomeStatNumber(parsedPlaylistTracks)} tracks` } : null,
                parsedPlaylistFans !== null ? { label: `${formatHomeStatNumber(parsedPlaylistFans)} fans` } : null
            ].filter(Boolean);
        badges = [...statBadges, ...playlistFlags];
    }
    const fallbackIcon = item.type === 'artist' ? '👤' : item.type === 'playlist' ? '🎵' : item.type === 'album' ? '💿' : '✨';
    const click = buildHomeClick(item);

    const needsSquare = item.type === 'artist' || item.type === 'channel';
    const imageClass = needsSquare ? 'playlist-image square-art' : 'playlist-image';
    const cardClass = `playlist-card${item.type === 'playlist' ? ' playlist-card--playlist' : ''}`;
    const channelBg = item.background_color ? `background-color: ${item.background_color};` : '';
    const channelBgStyle = channelBg ? `style="${channelBg}"` : '';
    const imageStyle = `style="${channelBg}background-image: url('${image}'); background-size: cover; background-position: center;"`;
    const spotifyAttr = isSpotifyItem ? ` data-spotify-item="${spotifyItemKey}"` : '';
    const clickAttr = isSpotifyItem ? '' : ` onclick="${click}"`;

    if (item.type === 'channel') {
        const artStyle = image ? `style="background-image: url('${image}');"` : '';
        return `
            <div class="channel-card"${spotifyAttr}${clickAttr}>
                <div class="channel-card-title">${title}</div>
                <div class="channel-card-art" ${artStyle}>${!image ? fallbackIcon : ''}</div>
            </div>
        `;
    }
    if (item.type === 'show') {
        const artStyle = image ? `style="background-image: url('${image}');"` : '';
        return `
            <div class="show-card"${spotifyAttr}${clickAttr}>
                <div class="show-art" ${artStyle}>${!image ? fallbackIcon : ''}</div>
                <div class="show-title">${title}</div>
            </div>
        `;
    }

    return `
        <div class="${cardClass}"${spotifyAttr}${clickAttr}>
            <div class="${imageClass}" ${channelBgStyle} ${imageStyle}>
                ${logoImage ? `<img class="channel-logo" src="${logoImage}" alt="">` : (!image ? fallbackIcon : '')}
            </div>
            <div class="playlist-title"><span class="home-marquee">${title}</span></div>
            ${subtitle ? `<div class="playlist-meta"><span class="home-marquee">${subtitle}</span></div>` : ''}
            ${badges.length ? `<div class="playlist-badges">${badges.map(badge => {
                const label = escapeHtml(badge.label || '');
                const className = badge.className ? ` ${badge.className}` : '';
                return `<span class="playlist-badge${className}">${label}</span>`;
            }).join('')}</div>` : ''}
        </div>
    `;
}

function renderHomeTrendingSongItem(item) {
    if (!item) {
        return '';
    }

    const isSpotifyItem = (item.source || '').toString().toLowerCase() === 'spotify';
    let spotifyAttr = '';
    let clickAttr = '';
    let clickableAttr = '';
    if (isSpotifyItem) {
        const spotifyItemKey = `spotify-item-${spotifyHomeItemSeq++}`;
        spotifyHomeItemCache.set(spotifyItemKey, item);
        spotifyAttr = ` data-spotify-item="${spotifyItemKey}"`;
    } else {
        clickAttr = ` onclick="${buildHomeClick(item)}"`;
        clickableAttr = ' data-home-clickable="true"';
    }

    const imageType = item.picture_type || item.pictureType || item.imageType || 'cover';
    const imageHash = item.md5_image || item.md5 || item.imageHash || item.cover?.md5 || item.cover?.MD5;
    const cover = upgradeSpotifyImageUrl(extractFirstUrl(
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

    const titleRaw = (item.name || item.title || '').toString().trim();
    const title = escapeHtml(titleRaw || 'Untitled');
    const subtitle = escapeHtml(buildHomeTrendingSubtitle(item));
    const altText = escapeHtml(titleRaw || 'track');
    const itemSource = (item.source || '').toString().toLowerCase();
    const itemType = (item.type || '').toString().toLowerCase();
    const itemTarget = (item.target || '').toString();
    const targetTrackMatch = itemTarget.match(/\/track\/(\d+)/i);
    const deezerTrackId = (itemSource === 'deezer' && itemType === 'track' && item.id)
        ? String(item.id)
        : (targetTrackMatch ? targetTrackMatch[1] : '');
    let spotifyTrackUrl = '';
    if (itemSource === 'spotify') {
        spotifyTrackUrl = buildSpotifyWebUrl(item.uri || item.url || item.sourceUrl || '');
        if (!spotifyTrackUrl && itemType === 'track' && item.id) {
            spotifyTrackUrl = `https://open.spotify.com/track/${encodeURIComponent(String(item.id))}`;
        }
    }
    const canPlay = Boolean(deezerTrackId || spotifyTrackUrl);
    const coverMarkup = cover
        ? `<img src="${escapeHtml(cover)}" alt="${altText}" loading="lazy" decoding="async" />`
        : '<div class="home-top-song-item__placeholder"></div>';
    const deezerPlaybackContext = normalizeHomeDeezerPlaybackContext({
        streamTrackId: item.stream_track_id || item.streamTrackId || '',
        trackToken: item.track_token || item.trackToken || '',
        md5origin: item.md5_origin || item.md5Origin || item.md5origin || '',
        mv: item.media_version || item.mediaVersion || item.mv || ''
    });
    const playAttrs = [];
    if (deezerTrackId) {
        playAttrs.push(`data-deezer-id="${escapeHtml(deezerTrackId)}"`);
        if (deezerPlaybackContext) {
            playAttrs.push(`data-stream-track-id="${escapeHtml(deezerPlaybackContext.streamTrackId)}"`);
            playAttrs.push(`data-track-token="${escapeHtml(deezerPlaybackContext.trackToken)}"`);
            if (deezerPlaybackContext.md5origin) {
                playAttrs.push(`data-md5-origin="${escapeHtml(deezerPlaybackContext.md5origin)}"`);
            }
            if (deezerPlaybackContext.mv) {
                playAttrs.push(`data-media-version="${escapeHtml(deezerPlaybackContext.mv)}"`);
            }
        }
    }
    if (spotifyTrackUrl) {
        playAttrs.push(`data-spotify-url="${escapeHtml(spotifyTrackUrl)}"`);
    }
    const playButton = canPlay
        ? `
        <button class="home-top-song-item__play" type="button" ${playAttrs.join(' ')} aria-label="Play ${altText} preview" onclick="event.preventDefault(); event.stopPropagation(); event.stopImmediatePropagation(); playHomeTrendingTrackInApp(this); return false;">
            <span class="playback-glyph" aria-hidden="true">
                <svg class="playback-icon playback-icon--play" viewBox="0 0 24 24" focusable="false">
                    <path d="M8 5v14l11-7z"></path>
                </svg>
                <svg class="playback-icon playback-icon--pause" viewBox="0 0 24 24" focusable="false">
                    <path d="M7 5h4v14H7zM13 5h4v14h-4z"></path>
                </svg>
            </span>
        </button>
    `
        : '';

    return `
        <div class="home-top-song-item"${spotifyAttr}${clickAttr}${clickableAttr}>
            <div class="home-top-song-item__thumb">
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
    const click = buildHomeClick(item);
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

const renderTopGenresItem = window.HomeViewHelpers.renderTopGenresItem;
const buildSpotifyBrowseClick = window.HomeViewHelpers.buildSpotifyBrowseClick;
const buildHomeClick = window.HomeViewHelpers.buildHomeClick;

function normalizeSpotifyBrowseCategory(item) {
    if (!item) {
        return null;
    }
    const id = item.id || item.categoryId || '';
    const name = item.name || item.title || id;
    if (!id || !name) {
        return null;
    }
    return {
        id,
        name,
        title: name,
        coverUrl: item.image_url || item.imageUrl || item.coverUrl || item.image || item.picture || '',
        source: 'spotify',
        type: 'browse-category'
    };
}

function mergeSpotifyCategories(deezerItems, limit) {
    const baseItems = Array.isArray(deezerItems) ? deezerItems.slice() : [];
    const targetPerPlatform = 7;
    const deezerSlice = baseItems.slice(0, targetPerPlatform);
    if (!spotifyBrowseCategoriesCache || spotifyBrowseCategoriesCache.length === 0) {
        if (!spotifyBrowseCategoriesLoading) {
            loadSpotifyCategoriesForMerge();
        }
        return deezerSlice.slice(0, limit || deezerSlice.length);
    }
    const spotifyItems = spotifyBrowseCategoriesCache
        .map(normalizeSpotifyBrowseCategory)
        .filter(Boolean)
        .filter(item => !/podcast/i.test(item.name || item.title || ''))
        .slice(0, targetPerPlatform);
    const merged = deezerSlice.concat(spotifyItems);
    if (limit && merged.length > limit) {
        return merged.slice(0, limit);
    }
    return merged;
}

async function loadSpotifyCategoriesForMerge() {
    if (spotifyBrowseCategoriesLoading) {
        return;
    }
    spotifyBrowseCategoriesLoading = true;
    try {
        const response = await fetch('/api/spotify/home-feed/browse');
        const data = await response.json();
        if (response.ok && data?.success && Array.isArray(data.categories)) {
            spotifyBrowseCategoriesCache = data.categories;
            refreshCategoriesSection();
        }
    } catch (error) {
        console.warn('Failed to load Spotify categories for merge:', error);
    } finally {
        spotifyBrowseCategoriesLoading = false;
    }
}

function refreshCategoriesSection() {
    const sections = document.querySelectorAll('#home-sections .home-section');
    if (!sections.length) {
        return;
    }
    const normalizeTitle = (value) => (value || '').toString().trim().toLowerCase();
    const targetSection = Array.from(sections).find(section => {
        const titleEl = section.querySelector('.home-section-title');
        const title = normalizeTitle(titleEl?.textContent || '');
        return title === 'categories' || title === 'your top genres';
    });
    if (!targetSection) {
        return;
    }
    const row = targetSection.querySelector('.top-genres-grid');
    if (!row) {
        return;
    }
    const rawDeezerItems = row.getAttribute('data-deezer-items');
    const deezerItems = rawDeezerItems
        ? JSON.parse(decodeURIComponent(rawDeezerItems))
        : null;
    if (!Array.isArray(deezerItems)) {
        return;
    }
    const mergedItems = mergeSpotifyCategories(deezerItems, 14);
    row.innerHTML = mergedItems.map(renderTopGenresItem).join('')
        + `<a class="top-genres-card top-genres-card--more" href="/Categories">View all</a>`;
}

function openTracklist(id, type) {
    window.location.href = `/Tracklist?id=${id}&type=${encodeURIComponent(type)}`;
}

function openSpotifyTracklist(id, type) {
    window.location.href = `/Tracklist?id=${encodeURIComponent(id)}&type=${encodeURIComponent(type)}&source=spotify`;
}

window.openSpotifyArtistFallback = window.HomeViewHelpers.openSpotifyArtistFallback;
window.openSpotifyArtist = window.HomeViewHelpers.openSpotifyArtist;

function openArtist(id) {
    window.location.href = `/Artist?id=${id}&source=deezer`;
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
        window.location.href = url;
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
        if (data.autologin && data.singleUser && data.singleUser.hasStoredCredentials) {
            console.log('Auto-login required, attempting server-side auto-login...');
            await attemptAutoLogin();
        }
        
    } catch (error) {
        console.error('Error checking auto-login:', error);
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
        
    } catch (error) {
        console.error('Error during auto-login:', error);
    }
}

// Load data when page loads
document.addEventListener('DOMContentLoaded', async function() {
    const urlParams = new URLSearchParams(window.location.search);
    const isChannelPage = !!urlParams.get('channel');
    // EXACT PORT: Check auto-login first like deezspotag
    await checkAutoLogin();
    setHomeGreeting();
    // Then load home data
    try {
        await loadHomeData();
    } catch (error) {
        console.error('Home data load failed:', error);
    }
    if (!isChannelPage) {
        // Load Spotify home feed and browse categories in parallel for faster rendering
        loadSpotifyCategoriesForMerge().catch(err => console.warn('Spotify content load error:', err));
        scheduleSpotifyHomeFeedRefresh();
    }
    applySearchSourceState();
    document.body.addEventListener('click', (event) => {
        const card = event.target.closest('[data-spotify-item]');
        if (!card) {
            return;
        }
        const key = card.getAttribute('data-spotify-item');
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

function scheduleSpotifyHomeFeedRefresh() {
    const homeSections = document.getElementById('home-sections');
    const autoEnabled = homeSections?.dataset.spotifyHomeAutorefresh === 'true';
    const hours = parseInt(homeSections?.dataset.spotifyHomeRefreshHours || '2', 10);
    if (!autoEnabled || Number.isNaN(hours) || hours < 2) {
        return;
    }
    setInterval(() => {
        loadHomeData();
    }, hours * 60 * 60 * 1000);
}
