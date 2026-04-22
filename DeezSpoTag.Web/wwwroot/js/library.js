const libraryState = {
    folders: [],
    libraries: [],
    aliases: new Map(),
    aliasesLoaded: false,
    downloadLocation: null,
    atmosDestinationFolderId: null,
    globalMultiQualityEnabled: false,
    videoDownloadLocation: null,
    podcastDownloadLocation: null,
    unavailableAlbums: new Map(),
    imageCacheKey: null,
    spotifyResolveCache: new Map(),
    spotifyResolveInFlight: new Map(),
    autotagProfiles: [],
    autotagDefaults: {
        defaultFileProfile: null,
        librarySchedules: {}
    },
    previewTrackId: null,
    previewButton: null,
    deezerAlbumIndex: new Map(),
    localArtistIndex: new Map(),
    allArtists: [],
    filteredArtists: [],
    artistFolders: new Map(),
    artistFolderScopeId: null,
    artistSearchQuery: '',
    artistSortKey: 'name-asc',
    artistSearchTimer: null,
    artistLoadRequestId: 0,
    artistLoadAbortController: null,
    artistVirtualizationCleanup: null,
    artistVirtualizationKey: '',
    scanArtistsRefreshInFlight: false,
    lastActiveScanRefreshKey: '',
    lastArtistRefreshAtMs: 0,
    wasScanRunning: false,
    scanStatusPollTimer: 0,
    scanStatusPolling: false,
    analysisPollTimer: 0,
    runtimeRefreshPolicy: {
        scanStatusActiveMs: 5000,
        scanStatusIdleMs: 15000,
        analysisMs: 15000,
        minArtistRefreshMs: 10000
    },
    runtimeStatusFailureCount: 0,
    albumTracksById: new Map(),
    currentSpotifyArtist: null,
    currentLocalArtistName: '',
    artistVisuals: {
        artistId: null,
        headerImageUrl: null,
        gallery: [],
        cacheImages: [],
        spotifyImages: [],
        appleImages: [],
        deezerImages: [],
        preferredAvatarPath: null,
        preferredBackgroundPath: null,
        backgroundApplyId: 0,
        externalTerm: '',
        externalLoading: false
    },
    localAlbums: [],
    spotifyAlbums: [],
    discography: [],
    discographyTypeFilter: 'popular',
    discographyTypeTouched: false,
    discographyAvailabilityFilter: 'all',
    artistDataReady: { local: false, spotify: false },
    appleExtras: {
        term: '',
        appleArtistId: null,
        storedAppleId: null,
        atmos: [],
        localTrackVariantIndex: null,
        localTrackVariantArtistId: null,
        localTrackVariantIndexPromise: null,
        videos: [],
        videoOffset: 0,
        hasMoreVideos: false,
        loadingVideos: false,
        selectedVideoKeys: new Set(),
        showSelectedOnly: false,
        initialized: false,
        scrollBound: false
    },
    localTopSongTrackIndex: null,
    localTopSongTrackArtistId: null,
    localTopSongTrackIndexPromise: null,
    folderSaveInProgress: false,
    unmatchedArtistResolver: {
        items: [],
        suggestions: new Map(),
        loading: false,
        initialized: false,
        filter: ''
    }
};

const libraryTrackSummaryCache = new Map();
let spotifyTopTrackMatchRequestId = 0;
let spotifyTopTrackPreviewWarmupTimer = 0;
let spotifyTopTrackMatchPollTimer = 0;
let spotifyTopTrackMatchToken = '';
let spotifyTopTrackMatchStartPromise = null;
const spotifyTopTrackMatchButtonsByIndex = new Map();
const spotifyTopTrackMatchButtonsBySpotifyId = new Map();
const LIBRARY_VIEW_SESSION_KEY = 'libraryViewFolderId';
const LIBRARY_RETURN_STATE_SESSION_KEY = 'library:return:state';
const SOUNDTRACK_RETURN_STATE_SESSION_KEY = 'soundtrack:return:state';
const ALPHA_JUMP_LETTERS = Object.freeze([
    '#',
    'A', 'B', 'C', 'D', 'E', 'F', 'G',
    'H', 'I', 'J', 'K', 'L', 'M', 'N',
    'O', 'P', 'Q', 'R', 'S', 'T', 'U',
    'V', 'W', 'X', 'Y', 'Z'
]);
let pendingLibraryReturnState = null;
const libraryTopSongsPreviewState = {
    queueButtons: [],
    queueIndex: -1
};
const libraryTopTrackDeezerPlaybackContextCache = new Map();
const libraryTopTrackDeezerPlaybackContextRequests = new Map();

const analysisState = {
    running: false,
    lastRunUtc: null
};

const cleanupState = {
    running: false,
    timerId: 0,
    startedAtMs: 0,
    labelElement: null,
    originalLabel: 'Cleanup Missing'
};

const soundtrackState = {
    category: 'movie',
    selectedServerType: '',
    selectedLibraryId: '',
    configuration: null,
    initialized: false,
    lastItemsRequestId: 0,
    selectedTvShow: null,
    selectedTvSeasonId: '',
    backgroundRefreshTimer: 0,
    backgroundRefreshAttempt: 0,
    syncStatusTimer: 0,
    syncStatusSnapshot: '',
    syncRunning: false
};

const librarySpotifyArtistCacheTtlMs = 2 * 60 * 60 * 1000;
let activeFolderQualityDropdown = null;
let activeFolderQualityPanel = null;
let activeFolderQualitySummary = null;
let folderQualityOverlayHandlersBound = false;
const FOLDER_CONVERSION_FORMAT_VALUES = Object.freeze(['mp3', 'aac', 'alac', 'ogg', 'opus', 'flac', 'wav']);
const FOLDER_CONVERSION_BITRATE_VALUES = Object.freeze(['AUTO', '64', '96', '128', '160', '192', '256', '320']);

function normalizeFolderConvertFormatValue(value) {
    const text = String(value ?? '').trim().toLowerCase();
    if (!text) {
        return null;
    }
    let normalized = text;
    if (text === 'm4a' || text === 'm4a-aac') {
        normalized = 'aac';
    } else if (text === 'm4a-alac') {
        normalized = 'alac';
    }
    return FOLDER_CONVERSION_FORMAT_VALUES.includes(normalized) ? normalized : null;
}

function normalizeFolderConvertBitrateValue(value) {
    const raw = String(value ?? '').trim();
    if (!raw) {
        return null;
    }
    let compact = raw.toLowerCase().replaceAll(/\s+/g, '');
    if (compact === 'auto') {
        return 'AUTO';
    }
    if (compact.endsWith('kbps')) {
        compact = compact.slice(0, -4);
    } else if (compact.endsWith('kb/s')) {
        compact = compact.slice(0, -4);
    } else if (compact.endsWith('k')) {
        compact = compact.slice(0, -1);
    }
    if (!/^\d+$/.test(compact)) {
        return null;
    }
    const normalized = String(Number.parseInt(compact, 10));
    return FOLDER_CONVERSION_BITRATE_VALUES.includes(normalized) ? normalized : null;
}

function normalizeFolderConversionState(folder) {
    if (!folder || typeof folder !== 'object') {
        return folder;
    }
    const convertEnabled = folder.convertEnabled === true;
    const convertFormat = convertEnabled ? normalizeFolderConvertFormatValue(folder.convertFormat) : null;
    const convertBitrate = convertEnabled ? normalizeFolderConvertBitrateValue(folder.convertBitrate) : null;
    return {
        ...folder,
        convertEnabled,
        convertFormat,
        convertBitrate
    };
}

function isFolderEnabledFlag(value) {
    const normalized = String(value ?? '').trim().toLowerCase();
    if (!normalized) {
        return true;
    }
    if (typeof value === 'boolean') {
        return value;
    }
    if (typeof value === 'number') {
        return value !== 0;
    }
    return !/^(false|0|no|off|disabled)$/i.test(normalized);
}

function resolveFolderContentMode(folder) {
    const normalized = String(folder?.desiredQuality ?? '').trim().toLowerCase();
    if (!normalized) {
        return 'music';
    }
    if (normalized.includes('video')) {
        return 'video';
    }
    if (normalized.includes('podcast')) {
        return 'podcast';
    }
    if (normalized.includes('atmos') || normalized === '5') {
        return 'atmos';
    }
    return 'music';
}

function isMusicRecommendationEligibleFolder(folder) {
    if (!folder || !isFolderEnabledFlag(folder.enabled)) {
        return false;
    }

    const contentMode = resolveFolderContentMode(folder);
    return contentMode !== 'video' && contentMode !== 'podcast';
}

function positionActiveFolderQualityDropdown() {
    if (!activeFolderQualityDropdown || !activeFolderQualityPanel || !activeFolderQualitySummary || !activeFolderQualityDropdown.classList.contains('is-open')) {
        return;
    }

    const viewportWidth = globalThis.innerWidth || document.documentElement.clientWidth || 1280;
    const viewportHeight = globalThis.innerHeight || document.documentElement.clientHeight || 720;
    const margin = 12;
    const anchorRect = activeFolderQualitySummary.getBoundingClientRect();

    activeFolderQualityPanel.style.left = '0px';
    activeFolderQualityPanel.style.top = '0px';
    activeFolderQualityPanel.style.transform = 'none';
    const panelRect = activeFolderQualityPanel.getBoundingClientRect();
    const panelWidth = Math.min(panelRect.width || 640, Math.max(320, viewportWidth - (margin * 2)));
    const panelHeight = panelRect.height || 420;

    let left = anchorRect.left;
    if (left + panelWidth > viewportWidth - margin) {
        left = viewportWidth - margin - panelWidth;
    }
    if (left < margin) {
        left = margin;
    }

    let top = anchorRect.bottom + 8;
    if (top + panelHeight > viewportHeight - margin) {
        const aboveTop = anchorRect.top - panelHeight - 8;
        if (aboveTop >= margin) {
            top = aboveTop;
        } else {
            top = Math.max(margin, viewportHeight - margin - panelHeight);
        }
    }

    activeFolderQualityPanel.style.left = `${Math.round(left)}px`;
    activeFolderQualityPanel.style.top = `${Math.round(top)}px`;
}

function clearActiveFolderQualityDropdown() {
    activeFolderQualityDropdown = null;
    activeFolderQualityPanel = null;
    activeFolderQualitySummary = null;
}

function closeActiveFolderQualityDropdown() {
    if (activeFolderQualityDropdown) {
        activeFolderQualityDropdown.classList.remove('is-open');
    }
    if (activeFolderQualitySummary) {
        activeFolderQualitySummary.setAttribute('aria-expanded', 'false');
    }
    if (activeFolderQualityPanel) {
        activeFolderQualityPanel.hidden = true;
    }
    clearActiveFolderQualityDropdown();
}

function openFolderQualityDropdown(dropdown, panel, summary) {
    if (!dropdown || !panel || !summary) {
        return;
    }
    if (activeFolderQualityDropdown && activeFolderQualityDropdown !== dropdown) {
        closeActiveFolderQualityDropdown();
    }

    activeFolderQualityDropdown = dropdown;
    activeFolderQualityPanel = panel;
    activeFolderQualitySummary = summary;
    dropdown.classList.add('is-open');
    summary.setAttribute('aria-expanded', 'true');
    panel.hidden = false;
    positionActiveFolderQualityDropdown();
}

function folderQualityClickIsInsideActiveDropdown(event, targetElement) {
    if (targetElement) {
        if (targetElement.closest('[data-folder-quality-dropdown]')
            || targetElement.closest('[data-folder-quality-panel]')
            || targetElement.closest('[data-folder-quality-summary]')
            || activeFolderQualityDropdown.contains(targetElement)) {
            return true;
        }
    }

    const composedPath = typeof event.composedPath === 'function'
        ? event.composedPath()
        : [];
    return Array.isArray(composedPath) && (
        composedPath.includes(activeFolderQualityDropdown)
        || composedPath.includes(activeFolderQualityPanel)
        || composedPath.includes(activeFolderQualitySummary)
    );
}

function isPointerWithinElementRect(event, element) {
    if (!element) {
        return false;
    }

    const rect = element.getBoundingClientRect();
    return event.clientX >= rect.left
        && event.clientX <= rect.right
        && event.clientY >= rect.top
        && event.clientY <= rect.bottom;
}

function shouldCloseActiveFolderQualityDropdown(event) {
    if (!activeFolderQualityDropdown?.classList.contains('is-open')) {
        return false;
    }

    const targetElement = event.target instanceof Element ? event.target : null;
    if (folderQualityClickIsInsideActiveDropdown(event, targetElement)) {
        return false;
    }

    return !isPointerWithinElementRect(event, activeFolderQualityPanel)
        && !isPointerWithinElementRect(event, activeFolderQualitySummary);
}

function ensureFolderQualityOverlayHandlers() {
    if (folderQualityOverlayHandlersBound) {
        return;
    }
    folderQualityOverlayHandlersBound = true;

    document.addEventListener('click', (event) => {
        if (shouldCloseActiveFolderQualityDropdown(event)) {
            closeActiveFolderQualityDropdown();
        }
    });

    document.addEventListener('keydown', (event) => {
        if (event.key === 'Escape') {
            closeActiveFolderQualityDropdown();
        }
    });

    globalThis.addEventListener('resize', () => {
        positionActiveFolderQualityDropdown();
    });

    globalThis.addEventListener('scroll', () => {
        positionActiveFolderQualityDropdown();
    }, true);
}

function getLibrarySpotifyArtistCacheKey(artistId) {
    if (!artistId) {
        return '';
    }
    return `library-artist-spotify:${artistId}`;
}

function loadLibrarySpotifyArtistCache(artistId) {
    const key = getLibrarySpotifyArtistCacheKey(artistId);
    if (!key) {
        return null;
    }
    try {
        const raw = localStorage.getItem(key);
        if (!raw) {
            return null;
        }
        const parsed = JSON.parse(raw);
        if (!parsed?.savedAtUtc || !parsed?.payload) {
            return null;
        }
        const ageMs = Date.now() - Number(parsed.savedAtUtc);
        if (!Number.isFinite(ageMs) || ageMs < 0 || ageMs > librarySpotifyArtistCacheTtlMs) {
            localStorage.removeItem(key);
            return null;
        }
        return parsed.payload;
    } catch {
        return null;
    }
}

function saveLibrarySpotifyArtistCache(artistId, payload) {
    const key = getLibrarySpotifyArtistCacheKey(artistId);
    if (!key || !payload || typeof payload !== 'object') {
        return;
    }
    try {
        localStorage.setItem(key, JSON.stringify({
            savedAtUtc: Date.now(),
            payload
        }));
    } catch {
    }
}

function getLibrarySpotifyArtistIdHintKey(artistId) {
    if (!artistId) {
        return '';
    }
    return `library-artist-spotify-id:${artistId}`;
}

function loadLibrarySpotifyArtistIdHint(artistId) {
    const key = getLibrarySpotifyArtistIdHintKey(artistId);
    if (!key) {
        return '';
    }
    try {
        return (localStorage.getItem(key) || '').trim();
    } catch {
        return '';
    }
}

function saveLibrarySpotifyArtistIdHint(artistId, spotifyId) {
    const key = getLibrarySpotifyArtistIdHintKey(artistId);
    const value = (spotifyId || '').toString().trim();
    if (!key || !value) {
        return;
    }
    try {
        localStorage.setItem(key, value);
    } catch {
    }
}

function clearLibrarySpotifyArtistState(artistId) {
    const cacheKey = getLibrarySpotifyArtistCacheKey(artistId);
    const hintKey = getLibrarySpotifyArtistIdHintKey(artistId);
    try {
        if (cacheKey) {
            localStorage.removeItem(cacheKey);
        }
        if (hintKey) {
            localStorage.removeItem(hintKey);
        }
    } catch {
    }
}

function countArtistPageReleases(payload) {
    const releases = payload?.releases;
    if (!releases || typeof releases !== 'object') {
        return 0;
    }
    return Object.values(releases).reduce((count, value) => count + (Array.isArray(value) ? value.length : 0), 0);
}

function mergeArtistPagePayload(primary, fallback) {
    if (!primary || typeof primary !== 'object') {
        return fallback || primary;
    }
    if (!fallback || typeof fallback !== 'object') {
        return primary;
    }

    const merged = { ...primary };
    const releaseCounts = [countArtistPageReleases(primary), countArtistPageReleases(fallback)];
    if (releaseCounts[0] === 0 && releaseCounts[1] > 0) {
        merged.releases = fallback.releases;
    }

    [
        ['top_tracks', Array.isArray(primary.top_tracks) ? primary.top_tracks : [], Array.isArray(fallback.top_tracks) ? fallback.top_tracks : []],
        ['related', Array.isArray(primary.related) ? primary.related : [], Array.isArray(fallback.related) ? fallback.related : []]
    ].forEach(([key, currentItems, fallbackItems]) => {
        if (currentItems.length === 0 && fallbackItems.length > 0) {
            merged[key] = fallbackItems;
        }
    });

    ['picture_xl', 'picture_big', 'picture_medium'].forEach((key) => {
        if (!merged[key] && fallback[key]) {
            merged[key] = fallback[key];
        }
    });

    return merged;
}

function mergeSpotifyArtistPayload(primary, fallback) {
    if (!primary || typeof primary !== 'object') {
        return fallback || primary;
    }
    if (!fallback || typeof fallback !== 'object') {
        return primary;
    }

    const merged = { ...primary };
    merged.artistPage = mergeArtistPagePayload(primary.artistPage, fallback.artistPage);
    merged.albums = mergeSpotifyAlbums(
        Array.isArray(primary.albums) ? primary.albums : [],
        Array.isArray(fallback.albums) ? fallback.albums : []
    );
    merged.topTracks = mergeSpotifyTopTracks(
        Array.isArray(primary.topTracks) ? primary.topTracks : [],
        Array.isArray(fallback.topTracks) ? fallback.topTracks : []
    );
    if ((!Array.isArray(primary.relatedArtists) || primary.relatedArtists.length === 0) && Array.isArray(fallback.relatedArtists) && fallback.relatedArtists.length > 0) {
        merged.relatedArtists = fallback.relatedArtists;
    }
    if ((!Array.isArray(primary.appearsOn) || primary.appearsOn.length === 0) && Array.isArray(fallback.appearsOn) && fallback.appearsOn.length > 0) {
        merged.appearsOn = fallback.appearsOn;
    }

    merged.artist = mergeSpotifyArtistProfile(merged.artist, fallback.artist);

    return merged;
}

function mergeSpotifyArtistProfile(primaryArtist, fallbackArtist) {
    if ((!primaryArtist || typeof primaryArtist !== 'object') && fallbackArtist && typeof fallbackArtist === 'object') {
        return fallbackArtist;
    }
    if (!primaryArtist || typeof primaryArtist !== 'object' || !fallbackArtist || typeof fallbackArtist !== 'object') {
        return primaryArtist;
    }

    const artist = { ...primaryArtist };
    [
        ['biography', fallbackArtist.biography],
        ['headerImageUrl', fallbackArtist.headerImageUrl]
    ].forEach(([key, fallbackValue]) => {
        if (!artist[key] && fallbackValue) {
            artist[key] = fallbackValue;
        }
    });

    [
        ['gallery', fallbackArtist.gallery],
        ['images', fallbackArtist.images],
        ['genres', fallbackArtist.genres]
    ].forEach(([key, fallbackValue]) => {
        if ((!Array.isArray(artist[key]) || artist[key].length === 0) && Array.isArray(fallbackValue) && fallbackValue.length > 0) {
            artist[key] = fallbackValue;
        }
    });

    return artist;
}

function buildSpotifyAlbumMergeKey(album) {
    const id = (album?.id || '').toString().trim();
    if (id) {
        return `id:${id}`;
    }
    const titleKey = normalizeAlbumTitle(album?.name || album?.title || '');
    return titleKey ? `name:${titleKey}` : '';
}

function mergeSpotifyAlbums(primaryAlbums, fallbackAlbums) {
    if (!Array.isArray(primaryAlbums) || primaryAlbums.length === 0) {
        return Array.isArray(fallbackAlbums) ? [...fallbackAlbums] : [];
    }
    if (!Array.isArray(fallbackAlbums) || fallbackAlbums.length === 0) {
        return [...primaryAlbums];
    }

    const fallbackByKey = new Map();
    fallbackAlbums.forEach((album) => {
        const key = buildSpotifyAlbumMergeKey(album);
        if (key && !fallbackByKey.has(key)) {
            fallbackByKey.set(key, album);
        }
    });

    const seen = new Set();
    const merged = primaryAlbums.map((album) => {
        const key = buildSpotifyAlbumMergeKey(album);
        if (!key) {
            return album;
        }
        seen.add(key);
        const fallback = fallbackByKey.get(key);
        if (!fallback) {
            return album;
        }

        return {
            ...album,
            releaseDate: album.releaseDate || fallback.releaseDate || '',
            albumGroup: album.albumGroup || fallback.albumGroup || '',
            releaseType: album.releaseType || fallback.releaseType || '',
            totalTracks: Number(album.totalTracks) > 0 ? album.totalTracks : (fallback.totalTracks || 0),
            images: Array.isArray(album.images) && album.images.length ? album.images : (fallback.images || []),
            sourceUrl: album.sourceUrl || fallback.sourceUrl || '',
            discographySection: album.discographySection || fallback.discographySection || '',
            isPopular: Boolean(album.isPopular) || Boolean(fallback.isPopular),
            deezerId: album.deezerId || fallback.deezerId || null,
            deezerUrl: album.deezerUrl || fallback.deezerUrl || null
        };
    });

    fallbackAlbums.forEach((album) => {
        const key = buildSpotifyAlbumMergeKey(album);
        if (!key || seen.has(key)) {
            return;
        }
        merged.push(album);
    });

    merged.sort((a, b) => {
        const aDate = (a?.releaseDate || '').toString();
        const bDate = (b?.releaseDate || '').toString();
        return bDate.localeCompare(aDate);
    });

    return merged;
}

function mergeSpotifyTopTracks(primaryTracks, fallbackTracks) {
    if (!Array.isArray(primaryTracks) || primaryTracks.length === 0) {
        return Array.isArray(fallbackTracks) ? [...fallbackTracks] : [];
    }
    if (!Array.isArray(fallbackTracks) || fallbackTracks.length === 0) {
        return [...primaryTracks];
    }

    const fallbackById = new Map();
    fallbackTracks.forEach((track) => {
        const id = (track?.id || '').toString().trim();
        if (id && !fallbackById.has(id)) {
            fallbackById.set(id, track);
        }
    });

    return primaryTracks.map((track) => {
        const id = (track?.id || '').toString().trim();
        if (!id || !fallbackById.has(id)) {
            return track;
        }

        const fallback = fallbackById.get(id);
        return {
            ...track,
            releaseDate: track.releaseDate || fallback.releaseDate || '',
            albumName: track.albumName || fallback.albumName || '',
            albumId: track.albumId || fallback.albumId || '',
            albumGroup: track.albumGroup || fallback.albumGroup || '',
            releaseType: track.releaseType || fallback.releaseType || '',
            albumTrackTotal: Number(track.albumTrackTotal) > 0 ? track.albumTrackTotal : (fallback.albumTrackTotal || null)
        };
    });
}

function getImageCacheKey() {
    if (!libraryState.imageCacheKey) {
        let key = localStorage.getItem('libraryImageCacheKey');
        if (!key) {
            key = Date.now().toString();
            localStorage.setItem('libraryImageCacheKey', key);
        }
        libraryState.imageCacheKey = key;
    }
    return libraryState.imageCacheKey;
}

function bumpImageCacheKey() {
    const key = Date.now().toString();
    localStorage.setItem('libraryImageCacheKey', key);
    libraryState.imageCacheKey = key;
    return key;
}

function appendCacheKey(url) {
    const key = getImageCacheKey();
    if (!key) {
        return url;
    }
    const joiner = url.includes('?') ? '&' : '?';
    return `${url}${joiner}v=${encodeURIComponent(key)}`;
}

function toSafeHttpUrl(rawUrl) {
    if (typeof rawUrl !== 'string') {
        return '';
    }

    const trimmed = rawUrl.trim();
    if (!trimmed) {
        return '';
    }

    try {
        const parsed = new URL(trimmed, globalThis.location.origin);
        const protocol = parsed.protocol.toLowerCase();
        if (protocol !== 'http:' && protocol !== 'https:') {
            return '';
        }
        return parsed.toString();
    } catch {
        return '';
    }
}

function escapeHtml(text) {
    if (text === null || text === undefined) {
        return '';
    }
    return String(text)
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;')
        .replaceAll("'", '&#39;');
}

function buildLibraryImageUrl(path, size) {
    const value = (path || '').toString().trim();
    if (!value) {
        return '';
    }
    const params = new URLSearchParams();
    params.set('path', value);
    if (Number.isFinite(size) && size > 0) {
        params.set('size', String(Math.round(size)));
    }
    return `/api/library/image?${params.toString()}`;
}

async function fetchJson(url, options) {
    const response = await fetch(url, {
        cache: options?.cache ?? 'no-store',
        ...options
    });
    return parseJsonResponse(response, url);
}

async function fetchJsonOptional(url, options) {
    const response = await fetch(url, {
        cache: options?.cache ?? 'no-store',
        ...options
    });
    if (response.status === 404) {
        return null;
    }
    return parseJsonResponse(response, url);
}

async function fetchJsonOptionalWithTimeout(url, timeoutMs = 12000, options = null) {
    const controller = new AbortController();
    const timer = globalThis.setTimeout(() => {
        controller.abort();
    }, Math.max(1, Number.parseInt(String(timeoutMs ?? 0), 10) || 12000));

    try {
        const requestOptions = options
            ? { ...options, signal: controller.signal }
            : { signal: controller.signal };
        return await fetchJsonOptional(url, requestOptions);
    } catch (error) {
        if (error?.name === 'AbortError') {
            console.warn(`Request timed out while loading optional JSON: ${url}`);
            return null;
        }
        throw error;
    } finally {
        globalThis.clearTimeout(timer);
    }
}

async function parseJsonResponse(response, url) {
    if (isAuthHtmlResponse(response)) {
        const error = new Error('Session expired or invalid security token. Refresh the page and sign in again.');
        error.libraryUrl = url;
        throw error;
    }

    if (!response.ok) {
        const raw = await response.text();
        const message = parseLibraryErrorMessage(raw);
        const error = new Error(message || `Request failed (${response.status})`);
        error.libraryUrl = url;
        throw error;
    }

    return await parseLibrarySuccessResponse(response, url);
}

function parseLibraryErrorMessage(raw) {
    const trimmed = String(raw || '').trim();
    if (!trimmed) {
        return '';
    }

    if (isHtmlPayload(trimmed)) {
        return 'Session expired or invalid security token. Refresh the page and sign in again.';
    }

    try {
        const parsed = JSON.parse(trimmed);
        const parsedMessage = typeof parsed?.message === 'string' ? parsed.message.trim() : '';
        if (parsedMessage && !/^nothing queued\.?$/i.test(parsedMessage)) {
            return parsedMessage;
        }
        const parsedError = typeof parsed?.error === 'string' ? parsed.error.trim() : '';
        if (parsedError) {
            return parsedError;
        }
        const reasonCodes = Array.isArray(parsed?.reasonCodes)
            ? parsed.reasonCodes.map((value) => String(value || '').trim()).filter(Boolean)
            : [];
        if (reasonCodes.length > 0) {
            return reasonCodes[0].replaceAll('_', ' ');
        }
    } catch {
    }

    return trimmed;
}

async function parseLibrarySuccessResponse(response, url) {
    const contentType = response.headers.get('content-type') || '';
    const raw = await response.text();
    const trimmed = raw.trim();
    if (isHtmlPayload(trimmed) || contentType.toLowerCase().includes('text/html')) {
        const error = new Error('Session expired or invalid security token. Refresh the page and sign in again.');
        error.libraryUrl = url;
        throw error;
    }
    if (!trimmed || trimmed === 'undefined') {
        const error = new Error(`Invalid JSON response from ${url}: ${trimmed || '<empty>'}`);
        error.libraryUrl = url;
        throw error;
    }

    return parseLibraryJsonBody(raw, trimmed, url, contentType);
}

function parseLibraryJsonBody(raw, trimmed, url, contentType) {
    try {
        return JSON.parse(raw);
    } catch {
        const error = new Error('Unexpected response from server. Refresh the page and try again.');
        error.libraryUrl = url;
        throw error;
    }
}

function isHtmlPayload(text) {
    const sample = String(text || '').trimStart().slice(0, 64).toLowerCase();
    return sample.startsWith('<!doctype html') || sample.startsWith('<html');
}

function isAuthHtmlResponse(response) {
    if (!response) {
        return false;
    }

    if (response.status === 401 || response.status === 403) {
        return true;
    }

    if (!response.redirected) {
        return false;
    }

    try {
        const redirectedUrl = new URL(response.url, globalThis.location.origin);
        const path = redirectedUrl.pathname.toLowerCase();
        return path.startsWith('/identity/account/login');
    } catch {
        return false;
    }
}

function getLibraryPlaybackFacade() {
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

const spotifyUrlHelpers = globalThis.SpotifyUrlHelpers;

function normalizeSpotifyTrackSourceUrl(url, fallbackTrackId = '') {
    const initial = spotifyUrlHelpers.buildSpotifyWebUrl(url);
    const fallbackId = String(fallbackTrackId || '').trim();
    if (!initial) {
        return fallbackId ? `https://open.spotify.com/track/${encodeURIComponent(fallbackId)}` : '';
    }

    const normalized = String(initial)
        .replace(/open\.spotify\.com\/intl-[^/]+\//i, 'open.spotify.com/')
        .trim();

    if (normalized) {
        return normalized;
    }

    return fallbackId ? `https://open.spotify.com/track/${encodeURIComponent(fallbackId)}` : '';
}

async function resolveSpotifyUrlToDeezer(url) {
    const request = typeof url === 'string' ? { link: url } : url;
    const link = normalizeSpotifyTrackSourceUrl(String(request?.link || '').trim());
    if (!link) {
        return null;
    }

    const metadataKey = JSON.stringify([
        link,
        String(request?.title || '').trim(),
        String(request?.artist || '').trim(),
        String(request?.album || '').trim(),
        String(request?.isrc || '').trim(),
        Number.isFinite(Number(request?.durationMs)) ? Number(request.durationMs) : 0
    ]);

    if (libraryState.spotifyResolveCache.has(metadataKey)) {
        return libraryState.spotifyResolveCache.get(metadataKey);
    }
    if (libraryState.spotifyResolveInFlight.has(metadataKey)) {
        return await libraryState.spotifyResolveInFlight.get(metadataKey);
    }

    const resolvePromise = (async () => {
        const facade = getLibraryPlaybackFacade();
        if (facade && typeof facade.resolveTrackBySpotifyRequest === 'function') {
            try {
                const resolved = await facade.resolveTrackBySpotifyRequest({
                    link,
                    title: String(request?.title || '').trim(),
                    artist: String(request?.artist || '').trim(),
                    album: String(request?.album || '').trim(),
                    isrc: String(request?.isrc || '').trim(),
                    durationMs: Number.isFinite(Number(request?.durationMs)) ? Number(request.durationMs) : 0
                });
                if (resolved?.type === 'track' && resolved?.deezerId) {
                    libraryState.spotifyResolveCache.set(metadataKey, resolved);
                    return resolved;
                }
            } catch {
                // Fallback to API resolver below.
            }
        }

        try {
            const resolved = await fetchJsonOptional(`/api/spotify/resolve-deezer?url=${encodeURIComponent(link)}`);
            const normalized = resolved?.type === 'track' && resolved?.deezerId ? resolved : null;
            libraryState.spotifyResolveCache.set(metadataKey, normalized);
            return normalized;
        } catch {
            libraryState.spotifyResolveCache.set(metadataKey, null);
            return null;
        }
    })();

    libraryState.spotifyResolveInFlight.set(metadataKey, resolvePromise);
    try {
        return await resolvePromise;
    } finally {
        libraryState.spotifyResolveInFlight.delete(metadataKey);
    }
}

function normalizeAlbumTitle(value) {
    if (value === null || value === undefined) {
        return '';
    }
    const text = String(value);
    if (!text.trim()) {
        return '';
    }
    let normalized = text
        .toLowerCase()
        // Keep edition qualifiers like "Deluxe" by removing bracket chars, not bracket content.
        .replaceAll(/[()[\]{}]/g, ' ')
        .replaceAll(/\bfeat\.?\b|\bft\.?\b/gi, '')
        .replaceAll(/[^a-z0-9]+/g, ' ')
        .trim();

    // Normalize storefront suffixes so "Title" and "Title - Single/EP" map together.
    normalized = normalized
        .replaceAll(/\b(single|ep)\s+version$/g, '')
        .replaceAll(/\b(single|ep)$/g, '')
        .trim();

    return normalized;
}

function getNavigationType() {
    try {
        const navEntries = performance?.getEntriesByType?.('navigation');
        if (Array.isArray(navEntries) && navEntries.length > 0) {
            const type = (navEntries[0]?.type || '').toString().trim().toLowerCase();
            if (type) {
                return type;
            }
        }
    } catch {
    }
    return '';
}

function isNavigationRestoreCandidate(referrerPathFragment) {
    const navigationType = getNavigationType();
    if (navigationType === 'back_forward') {
        return true;
    }

    const fragment = String(referrerPathFragment || '').trim();
    if (!fragment) {
        return false;
    }

    try {
        if (!document.referrer) {
            return false;
        }
        const referrer = new URL(document.referrer, globalThis.location.origin);
        return referrer.pathname.includes(fragment);
    } catch {
        return false;
    }
}

function setSessionJsonValue(key, payload) {
    try {
        sessionStorage.setItem(key, JSON.stringify(payload));
    } catch {
        // Ignore session storage failures.
    }
}

function getSessionJsonValue(key) {
    try {
        const raw = sessionStorage.getItem(key);
        if (!raw) {
            return null;
        }
        return JSON.parse(raw);
    } catch {
        return null;
    }
}

function removeSessionValue(key) {
    try {
        sessionStorage.removeItem(key);
    } catch {
        // Ignore session storage failures.
    }
}

function getAlphaJumpLetter(value) {
    const trimmed = String(value || '').trim();
    if (!trimmed) {
        return '#';
    }

    const firstCodePoint = Array.from(trimmed)[0] || '';
    const upper = firstCodePoint.toLocaleUpperCase();
    return /^[A-Z]$/.test(upper) ? upper : '#';
}

function buildAlphaJumpAnchorId(prefix, letter) {
    const safePrefix = String(prefix || 'alpha').trim().toLowerCase() || 'alpha';
    const safeLetter = letter === '#'
        ? 'num'
        : String(letter || '').trim().toLowerCase();
    return `${safePrefix}-alpha-anchor-${safeLetter}`;
}

function bindAlphaJumpNavigation(navElement) {
    if (!navElement || navElement.dataset.alphaJumpBound === 'true') {
        return;
    }

    navElement.addEventListener('click', event => {
        const button = event.target.closest('[data-alpha-anchor-id]');
        if (!button || button.disabled) {
            return;
        }

        const anchorId = String(button.dataset.alphaAnchorId || '').trim();
        if (!anchorId) {
            return;
        }

        const anchor = document.getElementById(anchorId);
        if (!anchor) {
            return;
        }

        const offset = 88;
        const destinationTop = Math.max(0, Math.floor(anchor.getBoundingClientRect().top + globalThis.scrollY - offset));
        globalThis.scrollTo({ top: destinationTop, behavior: 'smooth' });
    });

    navElement.dataset.alphaJumpBound = 'true';
}

function resolveAlphaJumpVisibilityTarget(navElement) {
    if (!navElement) {
        return null;
    }

    if (navElement.id === 'libraryLetterNav') {
        return document.getElementById('artistsGrid');
    }
    if (navElement.id === 'soundtrackLetterNav') {
        return document.getElementById('soundtrackGrid');
    }
    return null;
}

function updateAlphaJumpVisibility(navElement) {
    if (!navElement || navElement.hidden) {
        return;
    }

    const target = resolveAlphaJumpVisibilityTarget(navElement);
    if (!target) {
        navElement.classList.add('is-visible');
        return;
    }

    const revealOffset = 170;
    const targetRect = target.getBoundingClientRect();
    const shouldShow = targetRect.top <= revealOffset && targetRect.bottom > 120;
    navElement.classList.toggle('is-visible', shouldShow);
}

function bindAlphaJumpAutoVisibility(navElement) {
    if (!navElement || navElement.dataset.alphaJumpVisibilityBound === 'true') {
        return;
    }

    let scheduled = false;
    const scheduleUpdate = () => {
        if (scheduled) {
            return;
        }
        scheduled = true;
        globalThis.requestAnimationFrame(() => {
            scheduled = false;
            updateAlphaJumpVisibility(navElement);
        });
    };

    globalThis.addEventListener('scroll', scheduleUpdate, { passive: true });
    globalThis.addEventListener('resize', scheduleUpdate, { passive: true });
    document.addEventListener('visibilitychange', scheduleUpdate);

    const soundtrackTabButton = document.getElementById('soundtracks-tab');
    if (soundtrackTabButton && navElement.id === 'soundtrackLetterNav') {
        soundtrackTabButton.addEventListener('shown.bs.tab', scheduleUpdate);
    }

    navElement.dataset.alphaJumpVisibilityBound = 'true';
}

function renderAlphaJumpNavigation(navElement, anchorIdsByLetter) {
    if (!navElement) {
        return;
    }

    bindAlphaJumpNavigation(navElement);
    const anchors = anchorIdsByLetter instanceof Map ? anchorIdsByLetter : new Map();
    if (anchors.size === 0) {
        navElement.hidden = true;
        navElement.classList.remove('is-visible');
        navElement.innerHTML = '';
        return;
    }

    const buttons = ALPHA_JUMP_LETTERS.map(letter => {
        const anchorId = anchors.get(letter) || '';
        const disabled = !anchorId;
        return `<button type="button" class="alpha-jump-nav__button${disabled ? ' is-disabled' : ''}" data-alpha-anchor-id="${escapeHtml(anchorId)}" aria-label="Jump to ${escapeHtml(letter)}" ${disabled ? 'disabled' : ''}>${escapeHtml(letter)}</button>`;
    });
    navElement.innerHTML = buttons.join('');
    navElement.hidden = false;
    bindAlphaJumpAutoVisibility(navElement);
    updateAlphaJumpVisibility(navElement);
}

function maybeApplyPendingScrollRestore(getState, clearState, allowDeferred = true) {
    const state = typeof getState === 'function' ? getState() : null;
    if (!state) {
        return false;
    }

    const desiredScroll = Math.max(0, Number.parseInt(String(state.scrollY ?? 0), 10) || 0);
    const maxScroll = Math.max(0, document.documentElement.scrollHeight - globalThis.innerHeight);
    if (allowDeferred && maxScroll + 20 < desiredScroll) {
        return false;
    }

    globalThis.scrollTo({
        top: Math.min(desiredScroll, maxScroll),
        behavior: 'auto'
    });
    if (typeof clearState === 'function') {
        clearState();
    }
    return true;
}

function persistLibraryReturnState() {
    if (!document.getElementById('artistsGrid')) {
        return;
    }

    const viewSelect = document.getElementById('libraryViewSelect');
    setSessionJsonValue(LIBRARY_RETURN_STATE_SESSION_KEY, {
        scrollY: globalThis.scrollY,
        viewSelection: String(viewSelect?.value || getStoredLibraryViewSelection() || 'main'),
        searchQuery: String(libraryState.artistSearchQuery || ''),
        sortKey: String(libraryState.artistSortKey || 'name-asc'),
        capturedAtUtc: new Date().toISOString()
    });
}

function clearPendingLibraryReturnState() {
    pendingLibraryReturnState = null;
    removeSessionValue(LIBRARY_RETURN_STATE_SESSION_KEY);
}

function primePendingLibraryReturnState() {
    const payload = getSessionJsonValue(LIBRARY_RETURN_STATE_SESSION_KEY);
    if (!payload) {
        return;
    }

    if (!isNavigationRestoreCandidate('/Library/Artist/')) {
        clearPendingLibraryReturnState();
        return;
    }

    pendingLibraryReturnState = payload;
    const locationScopedSelection = getLibraryScopeSelectionFromLocation();
    const hasExplicitLocationScope = locationScopedSelection !== '';
    const viewSelection = String(payload.viewSelection || '').trim();
    // URL scope is explicit user intent and must not be overridden by stale return-state.
    if (viewSelection && !hasExplicitLocationScope) {
        setStoredLibraryViewSelection(viewSelection);
    }

    libraryState.artistSearchQuery = String(payload.searchQuery || '');
    const savedSortKey = String(payload.sortKey || '').trim().toLowerCase();
    libraryState.artistSortKey = savedSortKey === 'name-desc' ? 'name-desc' : 'name-asc';
}

function applyPendingLibraryScrollRestore() {
    maybeApplyPendingScrollRestore(() => pendingLibraryReturnState, clearPendingLibraryReturnState, false);
}

function getDiscographyFilterStorageKey() {
    const artistId = getCurrentLibraryArtistId();
    if (!artistId) {
        return '';
    }
    return `library:artist:discography-filters:${artistId}`;
}

function saveDiscographyFilterState() {
    const key = getDiscographyFilterStorageKey();
    if (!key) {
        return;
    }

    const type = (libraryState.discographyTypeFilter || 'popular').toString();
    const availability = (libraryState.discographyAvailabilityFilter || 'all').toString();
    const normalizedType = ['popular', 'albums', 'singles-eps', 'all'].includes(type) ? type : 'popular';
    const normalizedAvailability = ['all', 'in-library', 'missing'].includes(availability) ? availability : 'all';

    try {
        sessionStorage.setItem(key, JSON.stringify({
            type: normalizedType,
            availability: normalizedAvailability
        }));
    } catch {
    }
}

function loadDiscographyFilterState() {
    const key = getDiscographyFilterStorageKey();
    if (!key) {
        return;
    }

    try {
        const raw = sessionStorage.getItem(key);
        if (!raw) {
            return;
        }
        const parsed = JSON.parse(raw);
        const availability = (parsed?.availability || '').toString();

        if (['all', 'in-library', 'missing'].includes(availability)) {
            libraryState.discographyAvailabilityFilter = availability;
        }
    } catch {
    }
}

function resetDiscographyFilterState() {
    const key = getDiscographyFilterStorageKey();
    if (!key) {
        return;
    }
    try {
        sessionStorage.removeItem(key);
    } catch {
    }
}

function initializeDiscographyFilterState() {
    libraryState.discographyTypeFilter = 'popular';
    libraryState.discographyTypeTouched = false;
    loadDiscographyFilterState();
}

function safeNormalizeLibrarySpotifyPayload(payload, contextLabel = '') {
    if (!payload || typeof payload !== 'object') {
        return payload;
    }
    try {
        return normalizeLibrarySpotifyPayload(payload);
    } catch (error) {
        console.warn('Spotify payload normalization failed; using raw payload.', contextLabel, error);
        return payload;
    }
}

function getSpotifyPayloadField(payload, camelName, pascalName) {
    if (!payload || typeof payload !== 'object') {
        return undefined;
    }
    if (Object.hasOwn(payload, camelName)) {
        return payload[camelName];
    }
    if (Object.hasOwn(payload, pascalName)) {
        return payload[pascalName];
    }
    return undefined;
}

function getArrayField(value, primaryKey, fallbackKey) {
    if (!value || typeof value !== 'object') {
        return [];
    }
    if (Array.isArray(value[primaryKey])) {
        return value[primaryKey];
    }
    if (Array.isArray(value[fallbackKey])) {
        return value[fallbackKey];
    }
    return [];
}

function normalizeSpotifyImageItem(image) {
    if (!image || typeof image !== 'object') {
        return null;
    }
    const url = (image.url || image.Url || '').toString().trim();
    if (!url) {
        return null;
    }
    return {
        url,
        width: image.width ?? image.Width ?? null,
        height: image.height ?? image.Height ?? null
    };
}

function normalizeSpotifyAlbumItem(album) {
    if (!album || typeof album !== 'object') {
        return null;
    }
    const imagesRaw = getArrayField(album, 'images', 'Images');
    const images = imagesRaw
        .map(normalizeSpotifyImageItem)
        .filter(Boolean);
    return {
        id: (album.id || album.Id || '').toString(),
        name: (album.name || album.Name || album.title || album.Title || '').toString(),
        title: (album.title || album.Title || album.name || album.Name || '').toString(),
        releaseDate: (album.releaseDate || album.ReleaseDate || '').toString(),
        albumGroup: (album.albumGroup || album.AlbumGroup || '').toString(),
        releaseType: (album.releaseType || album.ReleaseType || '').toString(),
        totalTracks: Number(album.totalTracks ?? album.TotalTracks ?? 0) || 0,
        images,
        sourceUrl: (album.sourceUrl || album.SourceUrl || '').toString(),
        deezerId: (album.deezerId || album.DeezerId || '').toString() || null,
        deezerUrl: (album.deezerUrl || album.DeezerUrl || '').toString() || null,
        discographySection: (album.discographySection || album.DiscographySection || '').toString(),
        isPopular: Boolean(album.isPopular ?? album.IsPopular)
    };
}

function normalizeSpotifyTrackItem(track) {
    if (!track || typeof track !== 'object') {
        return null;
    }
    const albumImagesRaw = getArrayField(track, 'albumImages', 'AlbumImages');
    const albumImages = albumImagesRaw
        .map(normalizeSpotifyImageItem)
        .filter(Boolean);
    const fallbackArtist = (track.artistName || track.ArtistName || track.artist || track.Artist || '').toString().trim();
    const artistsRaw = getArrayField(track, 'artists', 'Artists');
    const artistsText = artistsRaw
        .map((artist) => {
            if (!artist) {
                return '';
            }
            if (typeof artist === 'string') {
                return artist.trim();
            }
            return (artist.name || artist.Name || '').toString().trim();
        })
        .filter(Boolean)
        .join(', ');
    const artistName = artistsText || fallbackArtist;
    const normalizedSourceUrl = normalizeSpotifyTrackSourceUrl(track.sourceUrl || track.SourceUrl || '', track.id || track.Id || '');

    return {
        id: (track.id || track.Id || '').toString(),
        name: (track.name || track.Name || '').toString(),
        durationMs: Number(track.durationMs ?? track.DurationMs ?? 0) || 0,
        popularity: Number(track.popularity ?? track.Popularity ?? 0) || 0,
        previewUrl: (track.previewUrl || track.PreviewUrl || '').toString() || null,
        sourceUrl: normalizedSourceUrl || '',
        albumImages,
        albumName: (track.albumName || track.AlbumName || '').toString(),
        releaseDate: (track.releaseDate || track.ReleaseDate || '').toString(),
        deezerId: (track.deezerId || track.DeezerId || '').toString() || null,
        deezerUrl: (track.deezerUrl || track.DeezerUrl || '').toString() || null,
        albumGroup: (track.albumGroup || track.AlbumGroup || '').toString(),
        releaseType: (track.releaseType || track.ReleaseType || '').toString(),
        albumTrackTotal: track.albumTrackTotal ?? track.AlbumTrackTotal ?? null,
        albumId: (track.albumId || track.AlbumId || '').toString(),
        isrc: (track.isrc || track.Isrc || '').toString() || null,
        artistName
    };
}

function normalizeSpotifyRelatedArtistItem(artist) {
    if (!artist || typeof artist !== 'object') {
        return null;
    }
    const imagesRaw = getArrayField(artist, 'images', 'Images');
    const images = imagesRaw
        .map(normalizeSpotifyImageItem)
        .filter(Boolean);
    return {
        id: (artist.id || artist.Id || '').toString(),
        name: (artist.name || artist.Name || '').toString(),
        images,
        sourceUrl: (artist.sourceUrl || artist.SourceUrl || '').toString(),
        deezerId: (artist.deezerId || artist.DeezerId || '').toString() || null,
        deezerUrl: (artist.deezerUrl || artist.DeezerUrl || '').toString() || null
    };
}

function normalizeSpotifyArtistProfileObject(artist) {
    if (!artist || typeof artist !== 'object') {
        return null;
    }
    const imagesRaw = getArrayField(artist, 'images', 'Images');
    const images = imagesRaw
        .map(normalizeSpotifyImageItem)
        .filter(Boolean);
    const genres = getArrayField(artist, 'genres', 'Genres');
    const galleryRaw = getArrayField(artist, 'gallery', 'Gallery');
    const gallery = galleryRaw
        .map((item) => {
            if (!item) {
                return '';
            }
            if (typeof item === 'string') {
                return item.trim();
            }
            if (typeof item === 'object') {
                return (item.url || item.Url || item.src || item.Src || '').toString().trim();
            }
            return '';
        })
        .filter(Boolean);
    return {
        id: (artist.id || artist.Id || '').toString(),
        name: (artist.name || artist.Name || '').toString(),
        images,
        genres,
        followers: Number(artist.followers ?? artist.Followers ?? 0) || 0,
        popularity: Number(artist.popularity ?? artist.Popularity ?? 0) || 0,
        sourceUrl: (artist.sourceUrl || artist.SourceUrl || '').toString(),
        biography: (artist.biography || artist.Biography || '').toString() || null,
        verified: artist.verified ?? artist.Verified ?? null,
        monthlyListeners: artist.monthlyListeners ?? artist.MonthlyListeners ?? null,
        rank: artist.rank ?? artist.Rank ?? null,
        headerImageUrl: (artist.headerImageUrl || artist.HeaderImageUrl || '').toString() || null,
        gallery,
        discographyType: (artist.discographyType || artist.DiscographyType || '').toString() || null,
        totalAlbums: artist.totalAlbums ?? artist.TotalAlbums ?? null
    };
}

function isSpotifyPayloadUnavailable(payload) {
    if (!payload || typeof payload !== 'object') {
        return true;
    }
    const available = getSpotifyPayloadField(payload, 'available', 'Available');
    return available === false;
}

function normalizeArtistPageReleaseTabKey(key) {
    const lowered = (key || '').toString().trim().toLowerCase();
    if (!lowered) {
        return '';
    }
    if (lowered === 'popular' || lowered === 'popular releases' || lowered === 'popular_releases') {
        return 'popular';
    }
    if (lowered === 'album' || lowered === 'albums') {
        return 'albums';
    }
    if (lowered === 'single' || lowered === 'singles' || lowered === 'singles_eps' || lowered === 'singles and eps' || lowered === 'singles & eps') {
        return 'singles_eps';
    }
    if (lowered === 'ep') {
        return 'singles_eps';
    }
    if (lowered === 'featured') {
        return 'featured';
    }
    if (lowered === 'compile' || lowered === 'compilation' || lowered === 'compilations') {
        return 'albums';
    }
    return lowered;
}

function mapReleaseTypeMetadata(recordType, section, totalTracks) {
    const normalizedRecord = (recordType || '').toString().trim().toLowerCase();
    if (normalizedRecord === 'single') {
        return { albumGroup: 'single', releaseType: 'SINGLE' };
    }
    if (normalizedRecord === 'ep') {
        return { albumGroup: 'ep', releaseType: 'EP' };
    }
    if (normalizedRecord === 'compile' || normalizedRecord === 'compilation') {
        return { albumGroup: 'compilation', releaseType: 'COMPILATION' };
    }
    if (normalizedRecord === 'album') {
        return { albumGroup: 'album', releaseType: 'ALBUM' };
    }

    if (section === 'singles_eps') {
        if (Number(totalTracks) === 1) {
            return { albumGroup: 'single', releaseType: 'SINGLE' };
        }
        return { albumGroup: 'ep', releaseType: 'EP' };
    }

    return { albumGroup: 'album', releaseType: 'ALBUM' };
}

function buildSpotifyAlbumsFromArtistPage(artistPage) {
    const regularByKey = new Map();
    const featuredByKey = new Map();
    const releaseMetaByAlbumId = new Map();
    const releaseMetaByAlbumTitle = new Map();
    const releases = artistPage?.releases;

    if (!releases || typeof releases !== 'object') {
        return {
            albums: [],
            appearsOn: [],
            releaseMetaByAlbumId,
            releaseMetaByAlbumTitle
        };
    }

    Object.entries(releases).forEach(([rawTabKey, rawItems]) => {
        const tabKey = normalizeArtistPageReleaseTabKey(rawTabKey);
        if (!tabKey || !Array.isArray(rawItems)) {
            return;
        }

        rawItems.forEach((release) => {
            if (!release || typeof release !== 'object') {
                return;
            }
            const title = (release.title || '').toString().trim();
            if (!title) {
                return;
            }

            const releaseDate = (release.release_date || '').toString().trim();
            const totalTracks = Number(release.nb_tracks || 0);
            const releaseMeta = mapReleaseTypeMetadata(release.record_type, tabKey, totalTracks);
            const cover = (release.cover_medium || release.cover_big || release.cover || '').toString().trim();
            const album = {
                id: (release.id || '').toString().trim(),
                name: title,
                releaseDate,
                albumGroup: releaseMeta.albumGroup,
                releaseType: releaseMeta.releaseType,
                totalTracks,
                images: cover ? [{ url: cover, width: null, height: null }] : [],
                sourceUrl: (release.link || '').toString().trim(),
                deezerId: null,
                deezerUrl: null,
                discographySection: tabKey,
                isPopular: tabKey === 'popular'
            };

            const mergeKey = buildSpotifyAlbumMergeKey(album);
            if (!mergeKey) {
                return;
            }

            const target = tabKey === 'featured' ? featuredByKey : regularByKey;
            const existing = target.get(mergeKey);
            target.set(mergeKey, pickPreferredSpotifyDiscographyAlbum(existing, album));

            const meta = {
                releaseDate,
                releaseType: releaseMeta.releaseType,
                albumGroup: releaseMeta.albumGroup,
                totalTracks
            };
            if (album.id && releaseDate && !releaseMetaByAlbumId.has(album.id)) {
                releaseMetaByAlbumId.set(album.id, meta);
            }
            const titleKey = normalizeAlbumTitle(title);
            if (titleKey && releaseDate && !releaseMetaByAlbumTitle.has(titleKey)) {
                releaseMetaByAlbumTitle.set(titleKey, meta);
            }
        });
    });

    return {
        albums: Array.from(regularByKey.values()),
        appearsOn: Array.from(featuredByKey.values()),
        releaseMetaByAlbumId,
        releaseMetaByAlbumTitle
    };
}

function mapTopTracksFromArtistPage(topTracks) {
    if (!Array.isArray(topTracks)) {
        return [];
    }

    return topTracks
        .filter(track => track && typeof track === 'object')
        .map(track => {
            const album = track.album && typeof track.album === 'object' ? track.album : {};
            const albumType = (album.type || '').toString().trim().toLowerCase();
            let albumGroup = '';
            if (albumType === 'single' || albumType === 'ep' || albumType === 'album') {
                albumGroup = albumType;
            } else if (albumType === 'compile' || albumType === 'compilation') {
                albumGroup = 'compilation';
            }

            const cover = (album.cover_medium || album.cover_big || '').toString().trim();
            return {
                id: (track.id || '').toString().trim(),
                name: (track.title || '').toString(),
                durationMs: Math.max(0, Number(track.duration || 0)) * 1000,
                popularity: Math.max(0, Math.round(Number(track.rank || 0) / 10000)),
                previewUrl: null,
                sourceUrl: (track.link || '').toString().trim(),
                albumImages: cover ? [{ url: cover, width: null, height: null }] : [],
                albumName: (album.title || '').toString(),
                releaseDate: (track.release_date || album.release_date || '').toString(),
                deezerId: null,
                deezerUrl: null,
                albumGroup,
                releaseType: albumType ? albumType.toUpperCase() : '',
                albumTrackTotal: null,
                albumId: (album.id || '').toString().trim()
            };
        })
        .filter(track => track.id || track.name);
}

function mapRelatedArtistsFromArtistPage(related) {
    if (!Array.isArray(related)) {
        return [];
    }
    return related
        .filter(artist => artist && typeof artist === 'object')
        .map(artist => ({
            id: (artist.id || '').toString().trim(),
            name: (artist.name || '').toString(),
            images: (artist.picture_medium || artist.picture)
                ? [{ url: (artist.picture_medium || artist.picture).toString(), width: null, height: null }]
                : [],
            sourceUrl: (artist.link || '').toString().trim(),
            deezerId: null,
            deezerUrl: null
        }))
        .filter(artist => artist.id || artist.name);
}

function mapArtistProfileFromArtistPage(artistPage) {
    if (!artistPage || typeof artistPage !== 'object') {
        return null;
    }
    const artistId = (artistPage.id || '').toString().trim();
    const artistName = (artistPage.name || '').toString().trim();
    if (!artistId && !artistName) {
        return null;
    }

    const images = [];
    const pushImage = (url) => {
        const value = (url || '').toString().trim();
        if (!value || images.some(image => image.url === value)) {
            return;
        }
        images.push({ url: value, width: null, height: null });
    };
    pushImage(artistPage.picture_xl);
    pushImage(artistPage.picture_big);
    pushImage(artistPage.picture_medium);

    return {
        id: artistId,
        name: artistName,
        images,
        genres: Array.isArray(artistPage.genres) ? artistPage.genres : [],
        followers: Number(artistPage.nb_fan || 0),
        popularity: 0,
        sourceUrl: (artistPage.download_link || '').toString().trim(),
        biography: null,
        verified: null,
        monthlyListeners: null,
        rank: null,
        headerImageUrl: (artistPage.picture_xl || '').toString().trim(),
        gallery: []
    };
}

function hydrateTopTracksWithReleaseMetadata(tracks, releaseMetaByAlbumId, releaseMetaByAlbumTitle) {
    if (!Array.isArray(tracks) || tracks.length === 0) {
        return [];
    }

    return tracks.map((track) => {
        const albumId = (track?.albumId || '').toString().trim();
        const titleKey = normalizeAlbumTitle(track?.albumName || '');
        const meta = (albumId && releaseMetaByAlbumId.get(albumId))
            || (titleKey && releaseMetaByAlbumTitle.get(titleKey))
            || null;
        if (!meta) {
            return track;
        }

        const currentReleaseDate = (track?.releaseDate || '').toString();
        const releaseDate = extractReleaseYear(currentReleaseDate)
            ? currentReleaseDate
            : (meta.releaseDate || currentReleaseDate);
        const releaseType = (track?.releaseType || '').toString().trim() || (meta.releaseType || '');
        const albumGroup = (track?.albumGroup || '').toString().trim() || (meta.albumGroup || '');
        const fallbackTotalTracks = Number(meta.totalTracks || 0) > 0
            ? Number(meta.totalTracks)
            : track?.albumTrackTotal;
        const albumTrackTotal = Number(track?.albumTrackTotal || 0) > 0
            ? track.albumTrackTotal
            : fallbackTotalTracks;

        return {
            ...track,
            releaseDate,
            releaseType,
            albumGroup,
            albumTrackTotal
        };
    });
}

function normalizeLibrarySpotifyPayload(payload) {
    if (!payload || typeof payload !== 'object') {
        return payload;
    }

    const artistPageRaw = getSpotifyPayloadField(payload, 'artistPage', 'ArtistPage');
    const artistPage = artistPageRaw && typeof artistPageRaw === 'object'
        ? artistPageRaw
        : null;
    const artistPageDerived = buildSpotifyAlbumsFromArtistPage(artistPage);

    const rawAlbumsField = getSpotifyPayloadField(payload, 'albums', 'Albums');
    const rawAppearsOnField = getSpotifyPayloadField(payload, 'appearsOn', 'AppearsOn');
    const rawTopTracksField = getSpotifyPayloadField(payload, 'topTracks', 'TopTracks');
    const rawRelatedField = getSpotifyPayloadField(payload, 'relatedArtists', 'RelatedArtists');
    const rawArtistField = getSpotifyPayloadField(payload, 'artist', 'Artist');

    const rawAlbums = Array.isArray(rawAlbumsField)
        ? rawAlbumsField.map(normalizeSpotifyAlbumItem).filter(Boolean)
        : [];
    const rawAppearsOn = Array.isArray(rawAppearsOnField)
        ? rawAppearsOnField.map(normalizeSpotifyAlbumItem).filter(Boolean)
        : [];
    const rawTopTracks = Array.isArray(rawTopTracksField)
        ? rawTopTracksField.map(normalizeSpotifyTrackItem).filter(Boolean)
        : [];
    const fallbackTopTracks = mapTopTracksFromArtistPage(artistPage?.top_tracks);
    const fallbackRelated = mapRelatedArtistsFromArtistPage(artistPage?.related);
    const fallbackArtist = mapArtistProfileFromArtistPage(artistPage);

    const albums = mergeSpotifyAlbums(rawAlbums, artistPageDerived.albums);
    const appearsOn = mergeSpotifyAlbums(rawAppearsOn, artistPageDerived.appearsOn);
    let topTracks = rawTopTracks.length > 0 ? rawTopTracks : fallbackTopTracks;
    topTracks = hydrateTopTracksWithReleaseMetadata(
        topTracks,
        artistPageDerived.releaseMetaByAlbumId,
        artistPageDerived.releaseMetaByAlbumTitle
    );
    topTracks = hydrateTopTracksWithAlbumDates(topTracks, albums);

    const normalizedRelated = Array.isArray(rawRelatedField)
        ? rawRelatedField.map(normalizeSpotifyRelatedArtistItem).filter(Boolean)
        : [];
    const relatedArtists = normalizedRelated.length > 0
        ? normalizedRelated
        : fallbackRelated;

    const artist = mergeNormalizedSpotifyArtistProfile(
        normalizeSpotifyArtistProfileObject(rawArtistField),
        fallbackArtist
    );

    return {
        ...payload,
        artist,
        albums,
        appearsOn,
        topTracks,
        relatedArtists,
        artistPage,
        available: getSpotifyPayloadField(payload, 'available', 'Available') ?? payload.available
    };
}

function mergeNormalizedSpotifyArtistProfile(primaryArtist, fallbackArtist) {
    if (!primaryArtist) {
        return fallbackArtist || null;
    }
    if (!fallbackArtist) {
        return primaryArtist;
    }

    return {
        ...primaryArtist,
        id: primaryArtist.id || fallbackArtist.id || '',
        name: primaryArtist.name || fallbackArtist.name || '',
        images: Array.isArray(primaryArtist.images) && primaryArtist.images.length > 0 ? primaryArtist.images : fallbackArtist.images,
        genres: Array.isArray(primaryArtist.genres) && primaryArtist.genres.length > 0 ? primaryArtist.genres : fallbackArtist.genres,
        followers: Number(primaryArtist.followers || 0) > 0 ? primaryArtist.followers : fallbackArtist.followers,
        sourceUrl: primaryArtist.sourceUrl || fallbackArtist.sourceUrl || ''
    };
}

function indexDeezerAlbums(albums) {
    if (!Array.isArray(albums)) {
        return;
    }
    albums.forEach(album => {
        const title = normalizeAlbumTitle(album.title || album.name);
        if (!title) {
            return;
        }
        if (!libraryState.deezerAlbumIndex.has(title)) {
            libraryState.deezerAlbumIndex.set(title, {
                id: album.id,
                title: album.title || album.name
            });
        }
    });
}

function resolveLocalAlbumIdByTitle(title) {
    const normalized = normalizeAlbumTitle(title);
    if (!normalized) {
        return null;
    }
    return libraryState.deezerAlbumIndex.get(normalized)?.id ?? null;
}

function stripBracketedSegments(text) {
    const closingFor = {
        "(": ")",
        "[": "]",
        "{": "}"
    };
    const stack = [];
    let output = "";

    for (const ch of text) {
        if (ch === "(" || ch === "[" || ch === "{") {
            stack.push(closingFor[ch]);
            continue;
        }

        if (stack.length > 0) {
            if (ch === stack.at(-1)) {
                stack.pop();
            } else if (ch === "(" || ch === "[" || ch === "{") {
                stack.push(closingFor[ch]);
            }
            continue;
        }

        output += ch;
    }

    return output;
}

function normalizeArtistName(value) {
    if (value === null || value === undefined) {
        return '';
    }
    const text = String(value);
    if (!text.trim()) {
        return '';
    }
    return stripBracketedSegments(text.toLowerCase())
        .replaceAll(/[^a-z0-9]+/g, ' ')
        .trim();
}

async function ensureLocalArtistIndex() {
    if (libraryState.localArtistIndex.size) {
        return;
    }
    try {
        const artists = await fetchJson('/api/library/artists?availability=local');
        (artists || []).forEach(artist => {
            const normalized = normalizeArtistName(artist.name);
            if (normalized && !libraryState.localArtistIndex.has(normalized)) {
                libraryState.localArtistIndex.set(normalized, artist.id);
            }
        });
    } catch {
        // Local artist lookup is optional for related artist links.
    }
}

async function handleSpotifyRedirect(url, metadata = {}) {
    const parsed = spotifyUrlHelpers.parseSpotifyUrl(url);
    if (!parsed?.type || !parsed.id) {
        const safeUrl = toSafeHttpUrl(url);
        if (!safeUrl) {
            showToast('Invalid Spotify URL.', true);
            return;
        }
        globalThis.open(safeUrl, '_blank', 'noopener');
        return;
    }

    if (parsed.type === 'artist') {
        globalThis.location.href = `/Artist?id=${encodeURIComponent(parsed.id)}&source=spotify`;
        return;
    }

    globalThis.location.href = `/Tracklist?id=${encodeURIComponent(parsed.id)}&type=${encodeURIComponent(parsed.type || 'track')}&source=spotify`;
}

function clearPreviewPlayingMarkers(exceptButton = null) {
    document.querySelectorAll('.track-action.track-play').forEach((activeButton) => {
        if (exceptButton && activeButton === exceptButton) {
            return;
        }

        activeButton.classList.remove('is-playing', 'is-starting');
        delete activeButton.dataset.playbackState;
        const row = activeButton.closest('.top-song-item, .home-top-song-item');
        if (row) {
            row.classList.remove('is-playing');
        }
        const numberCell = activeButton.closest('.track-number');
        if (numberCell) {
            numberCell.classList.remove('is-playing');
        }
        const materialIcon = activeButton.querySelector('.material-icons');
        if (materialIcon) {
            materialIcon.textContent = 'play_arrow';
        }
    });
}

function isLibraryTopSongPlayButton(button) {
    return Boolean(button?.closest?.('#spotifyTopTracksList'));
}

function clearSpotifyTopSongPlayingMarkers(exceptButton = null) {
    const list = document.getElementById('spotifyTopTracksList');
    if (!list) {
        return;
    }
    list.querySelectorAll('.top-song-item__play.track-play').forEach((activeButton) => {
        if (exceptButton && activeButton === exceptButton) {
            return;
        }
        activeButton.classList.remove('is-playing', 'is-starting');
        delete activeButton.dataset.playbackState;
        const row = activeButton.closest('.top-song-item');
        if (row) {
            row.classList.remove('is-playing');
        }
    });
}

function setPreviewButtonState(button, isPlaying) {
    if (!button) {
        if (!isPlaying) {
            clearPreviewPlayingMarkers(null);
        }
        return;
    }
    if (isPlaying) {
        clearPreviewPlayingMarkers(button);
    }

    button.classList.toggle('is-playing', isPlaying);
    const topSongRow = button.closest('.top-song-item, .home-top-song-item');
    if (topSongRow) {
        topSongRow.classList.toggle('is-playing', isPlaying);
    }
    const numberCell = button.closest('.track-number');
    if (numberCell) {
        numberCell.classList.toggle('is-playing', isPlaying);
    }
    const materialIcon = button.querySelector('.material-icons');
    if (materialIcon) {
        materialIcon.textContent = isPlaying ? 'pause' : 'play_arrow';
    }

}

function setSpotifyTopSongButtonState(button, isPlaying) {
    if (!button) {
        if (!isPlaying) {
            clearSpotifyTopSongPlayingMarkers(null);
        }
        return;
    }

    if (isPlaying) {
        clearSpotifyTopSongPlayingMarkers(button);
    }

    button.classList.toggle('is-playing', isPlaying);
    const topSongRow = button.closest('.top-song-item');
    if (topSongRow) {
        topSongRow.classList.toggle('is-playing', isPlaying);
    }
}

const libraryPlaybackState = globalThis.DeezerPlaybackState;

function setLibraryPlaybackState(button, state) {
    const normalizedState = String(state || '').toLowerCase();
    const isTopSongButton = isLibraryTopSongPlayButton(button);

    if (libraryPlaybackState && typeof libraryPlaybackState.transitionButtonState === 'function') {
        libraryPlaybackState.transitionButtonState(button, state, {
            setPlaying: isTopSongButton ? setSpotifyTopSongButtonState : setPreviewButtonState,
            clear: isTopSongButton
                ? () => clearSpotifyTopSongPlayingMarkers(null)
                : () => clearPreviewPlayingMarkers(null),
            onTransition: (targetButton, nextState) => {
                if (nextState === 'requested') {
                    if (isLibraryTopSongPlayButton(targetButton)) {
                        setSpotifyTopSongButtonState(targetButton, true);
                    } else {
                        setPreviewButtonState(targetButton, true);
                    }
                }
            }
        });
        return;
    }

    const isPlaying = normalizedState === 'requested' || normalizedState === 'playing';
    if (isTopSongButton) {
        setSpotifyTopSongButtonState(button, isPlaying);
        return;
    }
    setPreviewButtonState(button, isPlaying);
}

function getLibraryPlaybackSession() {
    const player = globalThis.DeezerUnifiedPlayback;
    if (!player || typeof player.getSession !== 'function') {
        return null;
    }
    return player.getSession();
}

function findLibraryTopSongButtonFromSession(session) {
    if (!session) {
        return null;
    }

    const list = document.getElementById('spotifyTopTracksList');
    if (!list) {
        return null;
    }

    const sessionButton = session.button;
    if (sessionButton instanceof HTMLElement
        && sessionButton.isConnected
        && isLibraryTopSongPlayButton(sessionButton)) {
        return sessionButton;
    }

    const buttons = Array.from(list.querySelectorAll('.top-song-item__play.track-play'));
    if (!buttons.length) {
        return null;
    }

    const deezerId = String(session.deezerId || '').trim();
    if (deezerId) {
        const deezerButton = buttons.find((button) => String(button.dataset?.deezerId || '').trim() === deezerId);
        if (deezerButton) {
            return deezerButton;
        }
    }

    const sourceKey = String(session.sourceKey || '').trim();
    if (sourceKey) {
        const sourceButton = buttons.find((button) =>
            String(button.dataset?.spotifyUrl || '').trim() === sourceKey
            || String(button.dataset?.previewUrl || '').trim() === sourceKey
            || String(button.dataset?.deezerId || '').trim() === sourceKey);
        if (sourceButton) {
            return sourceButton;
        }
    }

    return null;
}

function syncLibraryTopSongsPlaybackFromSession() {
    const session = getLibraryPlaybackSession();
    const isLibrarySession = String(session?.page || '').trim().toLowerCase() === 'library';
    if (!isLibrarySession) {
        clearSpotifyTopSongPlayingMarkers(null);
        resetSpotifyTopTrackQueue();
        if (isLibraryTopSongPlayButton(libraryState.previewButton)) {
            libraryState.previewButton = null;
            libraryState.previewTrackId = null;
        }
        return;
    }

    const targetButton = findLibraryTopSongButtonFromSession(session);
    if (!targetButton) {
        clearSpotifyTopSongPlayingMarkers(null);
        resetSpotifyTopTrackQueue();
        if (isLibraryTopSongPlayButton(libraryState.previewButton)) {
            libraryState.previewButton = null;
            libraryState.previewTrackId = null;
        }
        return;
    }

    const state = session?.playing ? 'playing' : 'requested';
    setLibraryPlaybackState(targetButton, state);
    libraryState.previewButton = targetButton;
    libraryState.previewTrackId = String(session?.key || session?.sourceKey || '').trim() || null;
    seedSpotifyTopTrackQueue(targetButton);
}

async function playDirectPreviewInApp(previewUrl, button) {
    const normalizedPreviewUrl = String(previewUrl || '').trim();
    if (!normalizedPreviewUrl) {
        showToast('Preview unavailable.', true);
        return;
    }

    const player = globalThis.DeezerUnifiedPlayback;
    if (!player || typeof player.play !== 'function') {
        return;
    }

    await player.play({
        page: 'library',
        button,
        previewUrl: normalizedPreviewUrl,
        sourceKey: normalizedPreviewUrl,
        unavailableMessage: 'Preview unavailable.',
        startFailedMessage: 'Unable to start playback.',
        interruptedMessage: 'Playback interrupted.',
        onStateChange: buildLibraryPlaybackStateHandler(button, normalizedPreviewUrl)
    });
}

function getNextSpotifyTopTrackButton(currentButton = null) {
    const list = document.getElementById('spotifyTopTracksList');
    const activeButton = currentButton || libraryState.previewButton;
    if (!list || !activeButton) {
        return null;
    }
    const buttons = Array.from(list.querySelectorAll('button.track-play'));
    const index = buttons.indexOf(activeButton);
    return index === -1 ? null : buttons[index + 1] || null;
}

function buildSpotifyTopTrackPlaybackQueue(startButton) {
    const list = document.getElementById('spotifyTopTracksList');
    if (!list) {
        return [];
    }

    return Array.from(list.querySelectorAll('.top-song-item__play.track-play'))
        .filter((button) => {
            if (!(button instanceof HTMLElement)) {
                return false;
            }
            if (!button.isConnected) {
                return false;
            }
            const deezerId = String(button.dataset?.deezerId || '').trim();
            const spotifyUrl = String(button.dataset?.spotifyUrl || '').trim();
            const previewUrl = String(button.dataset?.previewUrl || '').trim();
            return Boolean(deezerId || spotifyUrl || previewUrl);
        });
}

function resetSpotifyTopTrackQueue() {
    libraryTopSongsPreviewState.queueButtons = [];
    libraryTopSongsPreviewState.queueIndex = -1;
}

function seedSpotifyTopTrackQueue(startButton) {
    const queue = buildSpotifyTopTrackPlaybackQueue(startButton);
    if (!queue.length) {
        resetSpotifyTopTrackQueue();
        return;
    }
    libraryTopSongsPreviewState.queueButtons = queue;
    const index = queue.indexOf(startButton);
    libraryTopSongsPreviewState.queueIndex = Math.max(index, 0);
}

function getNextSpotifyTopTrackQueueButton() {
    const queue = libraryTopSongsPreviewState.queueButtons;
    if (!Array.isArray(queue) || queue.length === 0) {
        return null;
    }

    for (let index = libraryTopSongsPreviewState.queueIndex + 1; index < queue.length; index++) {
        const candidate = queue[index];
        if (candidate?.isConnected) {
            libraryTopSongsPreviewState.queueIndex = index;
            return candidate;
        }
    }

    resetSpotifyTopTrackQueue();
    return null;
}

function buildLibraryPlaybackStateHandler(button, fallbackKey) {
    return (state, context) => {
        setLibraryPlaybackState(button, state);
        if (state === 'requested' || state === 'playing') {
            libraryState.previewButton = button;
            libraryState.previewTrackId = context?.session?.key || fallbackKey;
            return;
        }
        if (libraryState.previewButton === button) {
            libraryState.previewButton = null;
        }
        const contextSessionKey = String(context?.session?.key || '').trim();
        if (contextSessionKey
            ? libraryState.previewTrackId === contextSessionKey
            : libraryState.previewButton === null) {
            libraryState.previewTrackId = null;
        }
    };
}

async function ensureLibrarySpotifyButtonReadyForPlayback(button, url, options = {}) {
    if (!button) {
        return false;
    }

    const notifyOnUnmatched = options?.notifyOnUnmatched !== false;
    const existingDeezerId = String(button.dataset.deezerId || '').trim();
    const hasValidExistingDeezerId = /^\d+$/.test(existingDeezerId);
    if (existingDeezerId && hasValidExistingDeezerId) {
        return true;
    }
    if (existingDeezerId && !hasValidExistingDeezerId) {
        button.dataset.deezerId = '';
    }

    const spotifyUrl = normalizeSpotifyTrackSourceUrl(String(url || button.dataset.spotifyUrl || '').trim());
    const previewUrl = String(button.dataset.previewUrl || '').trim();
    if (!spotifyUrl) {
        if (!previewUrl && notifyOnUnmatched) {
            showToast('No Deezer match available yet for this track.', true);
        }
        return Boolean(previewUrl);
    }

    if (notifyOnUnmatched) {
        showToast('No Deezer match available yet for this track.', true);
    }
    return false;
}

async function getNextLibraryPlayableSpotifyButton(currentButton) {
    let candidate = isLibraryTopSongPlayButton(currentButton)
        ? getNextSpotifyTopTrackQueueButton()
        : getNextSpotifyTopTrackButton(currentButton);

    while (candidate) {
        const ready = await ensureLibrarySpotifyButtonReadyForPlayback(
            candidate,
            candidate.dataset.spotifyUrl || '',
            { notifyOnUnmatched: false }
        );
        if (ready) {
            return candidate;
        }

        candidate = isLibraryTopSongPlayButton(currentButton)
            ? getNextSpotifyTopTrackQueueButton()
            : getNextSpotifyTopTrackButton(candidate);
    }

    return null;
}

function buildLibrarySpotifyPlaybackRequest(url, button) {
    if (!button) {
        return null;
    }

    const deezerIdRaw = String(button.dataset.deezerId || '').trim();
    const deezerId = /^\d+$/.test(deezerIdRaw) ? deezerIdRaw : '';
    const spotifyUrl = normalizeSpotifyTrackSourceUrl(String(url || button.dataset.spotifyUrl || '').trim());
    const previewUrlRaw = String(button.dataset.previewUrl || '').trim();
    // For Spotify top-song cards, follow Deezer-matched playback behavior.
    const previewUrl = spotifyUrl ? '' : previewUrlRaw;
    const spotifyMetadata = buildSpotifyTrackResolveRequestFromButton(button);
    const fallbackKey = deezerId ? `deezer:${deezerId}` : (previewUrl || spotifyUrl);

    return {
        page: 'library',
        button,
        deezerId,
        spotifyUrl,
        previewUrl,
        spotifyMetadata,
        sourceKey: previewUrl || deezerId || spotifyUrl,
        cache: libraryTopTrackDeezerPlaybackContextCache,
        requests: libraryTopTrackDeezerPlaybackContextRequests,
        unavailableMessage: previewUrl ? 'Preview unavailable.' : 'Track not available for streaming.',
        startFailedMessage: 'Unable to start playback.',
        interruptedMessage: 'Playback interrupted.',
        onStateChange: buildLibraryPlaybackStateHandler(button, fallbackKey),
        getNextRequest: async () => {
            const nextButton = await getNextLibraryPlayableSpotifyButton(button);
            if (!nextButton) {
                if (isLibraryTopSongPlayButton(button)) {
                    resetSpotifyTopTrackQueue();
                }
                return null;
            }
            return buildLibrarySpotifyPlaybackRequest(nextButton.dataset.spotifyUrl || '', nextButton);
        }
    };
}

async function playSpotifyTrackInApp(url, button) {
    if (isLibraryTopSongPlayButton(button)) {
        if (typeof button?.blur === 'function') {
            button.blur();
        }
        seedSpotifyTopTrackQueue(button);
    }

    const player = globalThis.DeezerUnifiedPlayback;
    if (!player || typeof player.play !== 'function') {
        return;
    }

    const ready = await ensureLibrarySpotifyButtonReadyForPlayback(button, url, {
        notifyOnUnmatched: true
    });
    if (!ready) {
        if (isLibraryTopSongPlayButton(button)) {
            resetSpotifyTopTrackQueue();
        }
        return;
    }

    const request = buildLibrarySpotifyPlaybackRequest(url, button);
    if (!request) {
        return;
    }
    await player.play(request);
}

async function playLocalLibraryTrackInApp(trackId, button, preferredPath) {
    if (!trackId) {
        return;
    }

    const normalizedPreferredPath = typeof preferredPath === 'string' ? preferredPath.trim() : '';
    const previewKey = `library:${trackId}:${normalizedPreferredPath}`;
    const previewUrl = normalizedPreferredPath
        ? `/api/library/analysis/track/${encodeURIComponent(trackId)}/audio?filePath=${encodeURIComponent(normalizedPreferredPath)}`
        : `/api/library/analysis/track/${encodeURIComponent(trackId)}/audio`;
    const player = globalThis.DeezerUnifiedPlayback;
    if (!player || typeof player.play !== 'function') {
        return;
    }

    await player.play({
        page: 'library',
        button,
        previewUrl,
        sourceKey: previewKey,
        unavailableMessage: 'Preview unavailable.',
        startFailedMessage: 'Unable to start playback.',
        interruptedMessage: 'Playback interrupted.',
        onStateChange: buildLibraryPlaybackStateHandler(button, previewKey)
    });
}


async function logLibraryActivity(message, level = 'info') {
    try {
        await fetchJson('/api/library/scan/logs', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ level, message })
        });
    } catch {
        // Logging should never block UI.
    }
}

async function loadLibrarySettings() {
    const settings = await fetchJson('/api/library/settings');
    const livePreviewIngest = document.getElementById('livePreviewIngest');
    const enableSignalAnalysis = document.getElementById('enableSignalAnalysis');
    if (livePreviewIngest) {
        livePreviewIngest.checked = settings.livePreviewIngest ?? settings.LivePreviewIngest ?? false;
    }
    if (enableSignalAnalysis) {
        enableSignalAnalysis.checked = settings.enableSignalAnalysis ?? settings.EnableSignalAnalysis ?? false;
    }
}

function getLibraryScanStatusElements() {
    return {
        lastScanEl: document.getElementById('libraryLastScan'),
        artistEl: document.getElementById('libraryArtistCount'),
        albumEl: document.getElementById('libraryAlbumCount'),
        trackEl: document.getElementById('libraryTrackCount'),
        cancelButton: document.getElementById('cancelLibraryScan'),
        indicator: document.getElementById('libraryScanIndicator')
    };
}

function clampRefreshInterval(rawValue, fallbackValue, minValue, maxValue) {
    const numeric = Number(rawValue);
    if (!Number.isFinite(numeric)) {
        return fallbackValue;
    }
    return Math.min(maxValue, Math.max(minValue, Math.trunc(numeric)));
}

function normalizeLibraryRefreshPolicy(policy) {
    const source = policy && typeof policy === 'object' ? policy : {};
    return {
        scanStatusActiveMs: clampRefreshInterval(source.scanStatusActiveMs, 5000, 1000, 120000),
        scanStatusIdleMs: clampRefreshInterval(source.scanStatusIdleMs, 15000, 2000, 300000),
        analysisMs: clampRefreshInterval(source.analysisMs, 15000, 5000, 300000),
        minArtistRefreshMs: clampRefreshInterval(source.minArtistRefreshMs, 10000, 1000, 120000)
    };
}

function getLibraryRefreshPolicy() {
    libraryState.runtimeRefreshPolicy = normalizeLibraryRefreshPolicy(libraryState.runtimeRefreshPolicy);
    return libraryState.runtimeRefreshPolicy;
}

function resolveLibraryScanCount(runningValue, totalsValue, lastCountValue, running) {
    if (running && Number.isFinite(Number(runningValue))) {
        return Number(runningValue);
    }
    if (Number.isFinite(Number(totalsValue))) {
        return Number(totalsValue);
    }
    return lastCountValue ?? 0;
}

function applyLibraryScanStatusSuccess(elements, status, stats, selectedFolderId) {
    const { lastScanEl, artistEl, albumEl, trackEl, cancelButton, indicator } = elements;
    if (cancelButton) {
        cancelButton.disabled = !status?.running;
    }
    if (lastScanEl) {
        if (status?.running) {
            const processed = status?.progress?.processedFiles ?? 0;
            const total = status?.progress?.totalFiles ?? 0;
            let runningLabel = 'Running...';
            if (total > 0) {
                runningLabel = `Running ${processed.toLocaleString()}/${total.toLocaleString()}`;
            } else if (processed > 0) {
                runningLabel = `Running ${processed.toLocaleString()} files`;
            }

            lastScanEl.textContent = runningLabel;
        } else {
            lastScanEl.textContent = formatTimestamp(status?.lastRunUtc);
        }
    }
    const totals = stats?.totals || null;
    const running = !!status?.running;
    const useRunningProgressCounts = running && selectedFolderId === null;
    const progress = status?.progress || null;
    const counts = {
        artists: resolveLibraryScanCount(progress?.artistsDetected, totals?.artists, status?.lastCounts?.artists, useRunningProgressCounts),
        albums: resolveLibraryScanCount(progress?.albumsDetected, totals?.albums, status?.lastCounts?.albums, useRunningProgressCounts),
        tracks: resolveLibraryScanCount(progress?.tracksDetected, totals?.tracks, status?.lastCounts?.tracks, useRunningProgressCounts)
    };
    if (artistEl) {
        artistEl.textContent = counts.artists.toLocaleString();
    }
    if (albumEl) {
        albumEl.textContent = counts.albums.toLocaleString();
    }
    if (trackEl) {
        trackEl.textContent = counts.tracks.toLocaleString();
    }
    if (indicator) {
        indicator.classList.toggle('is-running', !!status?.running);
    }
}

function applyLibraryScanStatusFailure(elements) {
    const { lastScanEl, artistEl, albumEl, trackEl, cancelButton, indicator } = elements;
    if (lastScanEl) {
        lastScanEl.textContent = 'Unknown';
    }
    if (cancelButton) {
        cancelButton.disabled = true;
    }
    [artistEl, albumEl, trackEl].forEach((node) => {
        if (node) {
            node.textContent = '—';
        }
    });
    if (indicator) {
        indicator.classList.remove('is-running');
    }
}

function getLibrarySpotifyBrowserCacheState(artistId, forceRefresh, forceRematch) {
    const allowBrowserCache = !forceRefresh && !forceRematch;
    const browserCachedRaw = allowBrowserCache ? loadLibrarySpotifyArtistCache(artistId) : null;
    const browserCached = safeNormalizeLibrarySpotifyPayload(browserCachedRaw, 'browser-cache');
    return {
        browserCached,
        hasBrowserCached: canUseSpotifyBrowserCache(browserCached)
    };
}

function handleUnavailableSpotifyArtistResponse(hasBrowserCached, cacheOnly) {
    if (hasBrowserCached) {
        setSpotifyCacheStatus('Spotify refresh: using cached');
        return;
    }
    if (cacheOnly) {
        setSpotifyCacheStatus('Spotify refresh: cache miss');
        return;
    }
    setSpotifyCacheStatus('Spotify refresh: no data');
    renderSpotifyArtistUnavailable('Spotify artist data unavailable.');
}

function handleSpotifyArtistRequestFailure(error, hasBrowserCached, cacheOnly) {
    console.warn('Spotify artist fetch failed.', error);
    if (hasBrowserCached) {
        setSpotifyCacheStatus('Spotify refresh: fallback cache');
        return;
    }
    if (cacheOnly) {
        setSpotifyCacheStatus('Spotify refresh: cache unavailable');
        return;
    }
    setSpotifyCacheStatus('Spotify refresh: failed');
    renderSpotifyArtistUnavailable('Spotify artist data failed to load.');
}

function setAppleArtistExtrasLoading(term, atmosContainer, videoContainer) {
    const loadingMarkup = `<div class="apple-loading">Searching Apple Music for ${escapeHtml(term)}…</div>`;
    if (atmosContainer) {
        atmosContainer.innerHTML = loadingMarkup;
    }
    if (videoContainer) {
        videoContainer.innerHTML = loadingMarkup;
    }
}

function buildAppleAtmosList(payload, normalizedTerm) {
    const matchesArtist = (name) => {
        const normalizedName = normalizeArtistName(name || '');
        return normalizedName === normalizedTerm || normalizedName.includes(normalizedTerm);
    };
    const tracks = (payload?.tracks || [])
        .filter((track) => track.hasAtmos && matchesArtist(track.artist))
        .map((track) => ({ ...track, __kind: 'track' }));
    const albums = (payload?.albums || [])
        .filter((album) => album.hasAtmos && matchesArtist(album.artist))
        .map((album) => ({ ...album, __kind: 'album' }));
    return [...tracks, ...albums];
}

async function loadLibraryScanStatus() {
    const elements = getLibraryScanStatusElements();
    if (!elements.lastScanEl && !elements.artistEl && !elements.albumEl && !elements.trackEl) {
        return null;
    }
    try {
        const selectedFolderId = getSelectedLibraryViewFolderId();
        const runtimeUrl = selectedFolderId === null
            ? '/api/library/runtime'
            : `/api/library/runtime?folderId=${encodeURIComponent(String(selectedFolderId))}`;
        let runtime = null;
        try {
            runtime = await fetchJsonOptional(runtimeUrl);
        } catch (runtimeError) {
            console.warn('Library runtime snapshot fetch failed, falling back to legacy status endpoints.', runtimeError);
        }
        const status = runtime?.scanStatus
            ?? await fetchJson('/api/library/scan/status');
        const stats = runtime?.stats
            ?? await fetchJsonOptional(selectedFolderId === null
                ? '/api/library/stats'
                : `/api/library/stats?folderId=${encodeURIComponent(String(selectedFolderId))}`);
        if (runtime?.refreshPolicy) {
            libraryState.runtimeRefreshPolicy = normalizeLibraryRefreshPolicy(runtime.refreshPolicy);
        }
        applyLibraryScanStatusSuccess(elements, status, stats, selectedFolderId);
        await refreshArtistsDuringActiveScan(status);
        return status;
    } catch (error) {
        applyLibraryScanStatusFailure(elements);
        libraryState.wasScanRunning = false;
        console.warn('Library scan status failed.', error);
        return null;
    }
}

async function refreshArtistsDuringActiveScan(status) {
    const hasArtistsGrid = !!document.getElementById('artistsGrid');
    const running = !!status?.running;
    const policy = getLibraryRefreshPolicy();

    if (!hasArtistsGrid) {
        libraryState.wasScanRunning = running;
        return;
    }

    if (!running) {
        const shouldRefreshAfterCompletion = libraryState.wasScanRunning;
        libraryState.wasScanRunning = false;
        libraryState.lastActiveScanRefreshKey = '';
        if (shouldRefreshAfterCompletion && !libraryState.scanArtistsRefreshInFlight) {
            libraryState.scanArtistsRefreshInFlight = true;
            try {
                await loadArtists();
                libraryState.lastArtistRefreshAtMs = Date.now();
            } finally {
                libraryState.scanArtistsRefreshInFlight = false;
            }
        }
        return;
    }

    libraryState.wasScanRunning = true;
    const progress = status?.progress || {};
    const refreshKey = [
        progress?.processedFiles ?? 0,
        progress?.artistsDetected ?? 0,
        progress?.albumsDetected ?? 0,
        progress?.tracksDetected ?? 0
    ].join(':');

    if (libraryState.scanArtistsRefreshInFlight || libraryState.lastActiveScanRefreshKey === refreshKey) {
        return;
    }

    const now = Date.now();
    if ((now - libraryState.lastArtistRefreshAtMs) < policy.minArtistRefreshMs) {
        console.debug('Library artist refresh skipped due to throttle window.', {
            minArtistRefreshMs: policy.minArtistRefreshMs,
            elapsedMs: now - libraryState.lastArtistRefreshAtMs
        });
        return;
    }

    libraryState.scanArtistsRefreshInFlight = true;
    try {
        await loadArtists();
        libraryState.lastActiveScanRefreshKey = refreshKey;
        libraryState.lastArtistRefreshAtMs = Date.now();
    } finally {
        libraryState.scanArtistsRefreshInFlight = false;
    }
}

async function saveLibrarySettings() {
    const livePreviewIngest = document.getElementById('livePreviewIngest');
    const enableSignalAnalysis = document.getElementById('enableSignalAnalysis');
    const payload = {};
    if (livePreviewIngest) {
        payload.livePreviewIngest = livePreviewIngest.checked;
    }
    if (enableSignalAnalysis) {
        payload.enableSignalAnalysis = enableSignalAnalysis.checked;
    }
    await fetchJson('/api/library/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
    });
    showToast('Settings saved.');
}

async function loadAnalysisStatus() {
    const statusEl = document.getElementById('analysisStatus');
    const lastRunEl = document.getElementById('analysisLastRun');
    if (!statusEl) {
        return;
    }
    try {
        const status = await fetchJson('/api/library/analysis/status');
        const pending = status?.pendingTracks ?? 0;
        const analyzed = status?.analyzedTracks ?? 0;
        const total = status?.totalTracks ?? 0;
        statusEl.textContent = `${analyzed.toLocaleString()}/${total.toLocaleString()} analyzed`;
        if (pending > 0) {
            statusEl.textContent += ` • ${pending.toLocaleString()} pending`;
        }
        if (analysisState.running) {
            statusEl.textContent += ' • Running';
        }
        const lastRunValue = analysisState.lastRunUtc || status?.lastRunUtc;
        if (lastRunEl) {
            lastRunEl.textContent = formatTimestamp(lastRunValue);
        }
    } catch (error) {
        statusEl.textContent = 'Status unavailable';
        if (lastRunEl) {
            lastRunEl.textContent = 'Unknown';
        }
        console.warn('Analysis status failed.', error);
    }
}

async function loadAnalysisActivity() {
    try {
        const logs = await fetchJson('/api/library/scan/logs?limit=200');
        const analysisLogs = Array.isArray(logs)
            ? logs.filter(entry => (entry.message || '').toLowerCase().includes('vibe analysis'))
            : [];
        if (!analysisLogs.length) {
            analysisState.running = false;
            return;
        }
        const started = findLatestLog(analysisLogs, 'started');
        const completed = findLatestLog(analysisLogs, 'completed');
        analysisState.running = Boolean(started && (!completed || started.timestampUtc > completed.timestampUtc));
        if (completed?.timestampUtc) {
            analysisState.lastRunUtc = completed.timestampUtc;
        }
    } catch (error) {
        console.warn('Analysis activity log fetch failed.', error);
    }
}

function findLatestLog(logs, keyword) {
    const lower = keyword.toLowerCase();
    return logs
        .filter(entry => (entry.message || '').toLowerCase().includes(lower))
        .sort((a, b) => new Date(b.timestampUtc || 0) - new Date(a.timestampUtc || 0))[0];
}

function formatTimestamp(value) {
    if (!value) {
        return 'Never';
    }
    const parsed = new Date(value);
    if (Number.isNaN(parsed.getTime())) {
        return 'Unknown';
    }
    return parsed.toLocaleString();
}

async function runAnalysis() {
    const button = document.getElementById('runAnalysis');
    if (!button) {
        return;
    }
    button.disabled = true;
    try {
        await fetchJson('/api/library/analysis/run', { method: 'POST' });
        showToast('Vibe analysis started.');
        await loadAnalysisActivity();
        await loadAnalysisStatus();
    } catch (error) {
        showToast(`Analysis failed: ${error.message}`, true);
    } finally {
        button.disabled = false;
    }
}

function normalizeAutoTagDefaults(defaults) {
    const source = defaults && typeof defaults === 'object' ? defaults : {};
    const parsedRecentWindow = Number.parseInt(String(source.recentDownloadWindowHours ?? ''), 10);
    const recentDownloadWindowHours = Number.isFinite(parsedRecentWindow) && parsedRecentWindow >= 0
        ? parsedRecentWindow
        : 24;
    return {
        defaultFileProfile: typeof source.defaultFileProfile === 'string' && source.defaultFileProfile.trim()
            ? source.defaultFileProfile.trim()
            : null,
        librarySchedules: source.librarySchedules && typeof source.librarySchedules === 'object'
            ? { ...source.librarySchedules }
            : {},
        recentDownloadWindowHours
    };
}

function normalizeProfileReference(reference) {
    return (reference || '').toString().trim().toLowerCase();
}

function findAutoTagProfileByReference(reference) {
    const normalized = normalizeProfileReference(reference);
    if (!normalized) {
        return null;
    }

    const profiles = Array.isArray(libraryState.autotagProfiles) ? libraryState.autotagProfiles : [];
    return profiles.find((profile) =>
        normalizeProfileReference(profile?.id) === normalized
        || normalizeProfileReference(profile?.name) === normalized) || null;
}

function resolveFolderProfileReference(folder) {
    const directReference = (folder?.autoTagProfileId || '').trim();
    const directMatch = findAutoTagProfileByReference(directReference);
    if (directMatch?.id) {
        return directMatch.id;
    }
    return directReference;
}

async function loadAutoTagFolderDefaults() {
    try {
        const [profilesResult, defaultsResult] = await Promise.allSettled([
            fetchJsonOptionalWithTimeout('/api/tagging/profiles', 12000),
            fetchJsonOptionalWithTimeout('/api/autotag/defaults', 12000)
        ]);
        const profiles = profilesResult.status === 'fulfilled' ? profilesResult.value : null;
        const defaults = defaultsResult.status === 'fulfilled' ? defaultsResult.value : null;
        libraryState.autotagProfiles = Array.isArray(profiles)
            ? profiles
                .map((profile) => ({
                    id: (profile?.id || '').toString().trim(),
                    name: (profile?.name || '').toString().trim()
                }))
                .filter((profile) => profile.id && profile.name)
            : [];
        libraryState.autotagDefaults = normalizeAutoTagDefaults(defaults);
    } catch (error) {
        console.warn('Failed to load AutoTag defaults for folder table', error);
        libraryState.autotagProfiles = [];
        libraryState.autotagDefaults = normalizeAutoTagDefaults(null);
    }
}

let autoTagProfileSettingsChangeMuteDepth = 0;

function emitAutoTagProfileLibrarySettingsChanged(reason = 'updated') {
    if (autoTagProfileSettingsChangeMuteDepth > 0) {
        return;
    }

    document.dispatchEvent(new CustomEvent('autotag:library-profile-settings-changed', {
        detail: {
            reason: String(reason || 'updated').trim() || 'updated',
            updatedAtUtc: new Date().toISOString()
        }
    }));
}

async function saveAutoTagFolderDefault(folderId, profileId, schedule) {
    const defaults = normalizeAutoTagDefaults(libraryState.autotagDefaults);
    const idKey = String(folderId);
    const nextSchedule = typeof schedule === 'string'
        ? schedule.trim()
        : '';
    if (nextSchedule) {
        defaults.librarySchedules[idKey] = nextSchedule;
    } else {
        delete defaults.librarySchedules[idKey];
    }

    const saved = await fetchJson('/api/autotag/defaults', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            defaultFileProfile: defaults.defaultFileProfile,
            librarySchedules: defaults.librarySchedules,
            recentDownloadWindowHours: defaults.recentDownloadWindowHours
        })
    });
    libraryState.autotagDefaults = normalizeAutoTagDefaults(saved);
    emitAutoTagProfileLibrarySettingsChanged('folder-defaults');
}

function buildAutoTagProfileLibrarySettingsSnapshot() {
    const folders = Array.isArray(libraryState.folders)
        ? libraryState.folders
        : [];
    const defaults = normalizeAutoTagDefaults(libraryState.autotagDefaults);
    const schedules = defaults?.librarySchedules && typeof defaults.librarySchedules === 'object'
        ? defaults.librarySchedules
        : {};
    const entries = folders
        .filter((folder) => String(folder?.rootPath || '').trim().length > 0)
        .map((folder) => {
            const normalizedFolder = normalizeFolderConversionState(folder);
            return {
                rootPath: String(normalizedFolder?.rootPath || '').trim(),
                displayName: String(normalizedFolder?.displayName || normalizedFolder?.libraryName || '').trim(),
                enabled: isFolderEnabledFlag(normalizedFolder?.enabled),
                desiredQuality: String(normalizedFolder?.desiredQuality || '27').trim() || '27',
                convertEnabled: normalizedFolder?.convertEnabled === true,
                convertFormat: normalizedFolder?.convertEnabled === true
                    ? normalizeFolderConvertFormatValue(normalizedFolder?.convertFormat)
                    : null,
                convertBitrate: normalizedFolder?.convertEnabled === true
                    ? normalizeFolderConvertBitrateValue(normalizedFolder?.convertBitrate)
                    : null,
                autoTagEnabled: normalizedFolder?.autoTagEnabled !== false,
                autoTagProfileId: resolveFolderProfileReference(normalizedFolder) || null,
                enhancementSchedule: String(schedules[String(normalizedFolder?.id ?? '')] || '').trim() || null
            };
        });
    const atmosFolder = folders.find((folder) =>
        Number.parseInt(String(folder?.id ?? ''), 10) === Number.parseInt(String(libraryState.atmosDestinationFolderId ?? ''), 10));

    return {
        version: 1,
        folders: entries,
        defaultFileProfile: defaults.defaultFileProfile || null,
        recentDownloadWindowHours: Number.isFinite(Number(defaults.recentDownloadWindowHours))
            ? Number(defaults.recentDownloadWindowHours)
            : null,
        destinations: {
            atmosRootPath: String(atmosFolder?.rootPath || '').trim() || null,
            videoRootPath: String(libraryState.videoDownloadLocation || '').trim() || null,
            podcastRootPath: String(libraryState.podcastDownloadLocation || '').trim() || null
        }
    };
}

function normalizeAutoTagProfileLibrarySettingsSnapshot(snapshot) {
    if (!snapshot || typeof snapshot !== 'object') {
        return null;
    }

    const rawFolders = Array.isArray(snapshot.folders) ? snapshot.folders : [];
    const dedupedByPath = new Map();
    rawFolders.forEach((item) => {
        const rootPath = String(item?.rootPath || '').trim();
        if (!rootPath) {
            return;
        }
        const pathKey = normalizePath(rootPath);
        if (!pathKey || dedupedByPath.has(pathKey)) {
            return;
        }

        dedupedByPath.set(pathKey, {
            rootPath,
            pathKey,
            displayName: String(item?.displayName || '').trim(),
            enabled: isFolderEnabledFlag(item?.enabled),
            desiredQuality: String(item?.desiredQuality || '27').trim() || '27',
            convertEnabled: item?.convertEnabled === true,
            convertFormat: item?.convertEnabled === true
                ? normalizeFolderConvertFormatValue(item?.convertFormat)
                : null,
            convertBitrate: item?.convertEnabled === true
                ? normalizeFolderConvertBitrateValue(item?.convertBitrate)
                : null,
            hasAutoTagEnabled: Object.hasOwn(item, 'autoTagEnabled'),
            autoTagEnabled: item?.autoTagEnabled === true,
            hasAutoTagProfileId: Object.hasOwn(item, 'autoTagProfileId'),
            autoTagProfileId: String(item?.autoTagProfileId || '').trim() || null,
            enhancementSchedule: String(item?.enhancementSchedule || '').trim() || null
        });
    });

    const hasDefaultFileProfile = Object.hasOwn(snapshot, 'defaultFileProfile');
    const hasRecentDownloadWindowHours = Object.hasOwn(snapshot, 'recentDownloadWindowHours');
    const defaultFileProfile = hasDefaultFileProfile
        ? (String(snapshot.defaultFileProfile || '').trim() || null)
        : null;
    const parsedRecentDownloadWindowHours = Number.parseInt(String(snapshot.recentDownloadWindowHours ?? ''), 10);
    const recentDownloadWindowHours = hasRecentDownloadWindowHours && Number.isFinite(parsedRecentDownloadWindowHours)
        ? Math.max(0, parsedRecentDownloadWindowHours)
        : null;
    const destinations = snapshot.destinations && typeof snapshot.destinations === 'object'
        ? {
            hasAtmosRootPath: Object.hasOwn(snapshot.destinations, 'atmosRootPath'),
            atmosRootPath: String(snapshot.destinations.atmosRootPath || '').trim() || null,
            hasVideoRootPath: Object.hasOwn(snapshot.destinations, 'videoRootPath'),
            videoRootPath: String(snapshot.destinations.videoRootPath || '').trim() || null,
            hasPodcastRootPath: Object.hasOwn(snapshot.destinations, 'podcastRootPath'),
            podcastRootPath: String(snapshot.destinations.podcastRootPath || '').trim() || null
        }
        : {
            hasAtmosRootPath: false,
            atmosRootPath: null,
            hasVideoRootPath: false,
            videoRootPath: null,
            hasPodcastRootPath: false,
            podcastRootPath: null
        };

    return {
        folders: Array.from(dedupedByPath.values()),
        hasDefaultFileProfile,
        defaultFileProfile,
        hasRecentDownloadWindowHours,
        recentDownloadWindowHours,
        destinations
    };
}

function buildExistingFoldersByPath(existingFoldersRaw) {
    const existingFolders = Array.isArray(existingFoldersRaw)
        ? existingFoldersRaw.map(normalizeFolderConversionState)
        : [];
    const existingByPath = new Map();
    existingFolders.forEach((folder) => {
        const rootPath = String(folder?.rootPath || '').trim();
        const pathKey = normalizePath(rootPath);
        if (!rootPath || !pathKey || existingByPath.has(pathKey)) {
            return;
        }
        existingByPath.set(pathKey, folder);
    });
    return existingByPath;
}

async function upsertFolderFromSnapshotEntry(entry, existingByPath) {
    const existing = existingByPath.get(entry.pathKey) || null;
    const payload = {
        rootPath: entry.rootPath,
        displayName: entry.displayName || deriveFolderDisplayName(entry.rootPath),
        enabled: entry.enabled,
        libraryName: existing?.libraryName ?? null,
        desiredQuality: entry.desiredQuality,
        convertEnabled: entry.convertEnabled,
        convertFormat: entry.convertEnabled ? entry.convertFormat : null,
        convertBitrate: entry.convertEnabled ? entry.convertBitrate : null
    };
    const savedFolder = await fetchJson(existing ? `/api/library/folders/${existing.id}` : '/api/library/folders', {
        method: existing ? 'PATCH' : 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
    });
    const savedId = Number.parseInt(String(savedFolder?.id ?? existing?.id ?? ''), 10);
    const savedRootPath = String(savedFolder?.rootPath || payload.rootPath || '').trim();
    const savedPathKey = normalizePath(savedRootPath);
    if (!(Number.isFinite(savedId) && savedId > 0 && savedRootPath && savedPathKey)) {
        return null;
    }

    if (entry.hasAutoTagProfileId) {
        await setFolderAutoTagProfile(savedId, entry.autoTagProfileId || '');
    }
    if (entry.hasAutoTagEnabled) {
        await setFolderAutoTagEnabled(savedId, entry.autoTagEnabled === true);
    }

    return { id: savedId, rootPath: savedRootPath, pathKey: savedPathKey };
}

function resolvePathBySnapshot(snapshotPath, resolvedByPath, existingByPath) {
    const normalizedPath = normalizePath(snapshotPath);
    if (!normalizedPath) {
        return null;
    }
    const resolved = resolvedByPath.get(normalizedPath);
    if (resolved?.rootPath) {
        return resolved.rootPath;
    }
    const existing = existingByPath.get(normalizedPath);
    return existing?.rootPath ? String(existing.rootPath).trim() : null;
}

function resolveIdBySnapshot(snapshotPath, resolvedByPath, existingByPath) {
    const normalizedPath = normalizePath(snapshotPath);
    if (!normalizedPath) {
        return null;
    }
    const resolved = resolvedByPath.get(normalizedPath);
    if (resolved?.id) {
        return resolved.id;
    }
    const existing = existingByPath.get(normalizedPath);
    const id = Number.parseInt(String(existing?.id ?? ''), 10);
    return Number.isFinite(id) && id > 0 ? id : null;
}

function buildLibrarySchedulesUpdate(normalizedFolders, resolvedByPath, currentDefaults) {
    const nextSchedules = currentDefaults.librarySchedules && typeof currentDefaults.librarySchedules === 'object'
        ? { ...currentDefaults.librarySchedules }
        : {};
    normalizedFolders.forEach((entry) => {
        const resolved = resolvedByPath.get(entry.pathKey);
        if (!resolved) {
            return;
        }
        const idKey = String(resolved.id);
        if (entry.enhancementSchedule) {
            nextSchedules[idKey] = entry.enhancementSchedule;
        } else {
            delete nextSchedules[idKey];
        }
    });
    return nextSchedules;
}

function buildDestinationUpdate(destinations, resolvedByPath, existingByPath) {
    const destinationUpdate = {};
    if (destinations.hasAtmosRootPath) {
        destinationUpdate.atmosFolderId = resolveIdBySnapshot(destinations.atmosRootPath, resolvedByPath, existingByPath);
    }
    if (destinations.hasVideoRootPath) {
        destinationUpdate.videoFolderPath = resolvePathBySnapshot(destinations.videoRootPath, resolvedByPath, existingByPath) || '';
    }
    if (destinations.hasPodcastRootPath) {
        destinationUpdate.podcastFolderPath = resolvePathBySnapshot(destinations.podcastRootPath, resolvedByPath, existingByPath) || '';
    }
    return destinationUpdate;
}

async function applyAutoTagProfileLibrarySettingsSnapshot(snapshot, options = {}) {
    const normalized = normalizeAutoTagProfileLibrarySettingsSnapshot(snapshot);
    if (!normalized) {
        return false;
    }

    const { silent = true } = options || {};
    autoTagProfileSettingsChangeMuteDepth += 1;
    let applied = false;
    try {
        const existingFoldersRaw = await fetchJson('/api/library/folders?includeDisabled=true');
        const existingByPath = buildExistingFoldersByPath(existingFoldersRaw);

        const resolvedByPath = new Map();
        for (const entry of normalized.folders) {
            const saved = await upsertFolderFromSnapshotEntry(entry, existingByPath);
            if (saved) {
                resolvedByPath.set(saved.pathKey, {
                    id: saved.id,
                    rootPath: saved.rootPath
                });
            }
        }

        const currentDefaults = normalizeAutoTagDefaults(libraryState.autotagDefaults);
        const nextSchedules = buildLibrarySchedulesUpdate(normalized.folders, resolvedByPath, currentDefaults);

        const nextDefaultsPayload = {
            defaultFileProfile: normalized.hasDefaultFileProfile
                ? normalized.defaultFileProfile
                : currentDefaults.defaultFileProfile,
            librarySchedules: nextSchedules,
            recentDownloadWindowHours: normalized.hasRecentDownloadWindowHours
                ? normalized.recentDownloadWindowHours
                : currentDefaults.recentDownloadWindowHours,
            renameSpotifyArtistFolders: currentDefaults.renameSpotifyArtistFolders
        };
        const savedDefaults = await fetchJson('/api/autotag/defaults', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(nextDefaultsPayload)
        });
        libraryState.autotagDefaults = normalizeAutoTagDefaults(savedDefaults);

        const destinationUpdate = buildDestinationUpdate(normalized.destinations, resolvedByPath, existingByPath);
        if (Object.keys(destinationUpdate).length > 0) {
            await updateDownloadDestinations(destinationUpdate);
        }

        await loadFolders();
        applied = true;
    } finally {
        autoTagProfileSettingsChangeMuteDepth = Math.max(0, autoTagProfileSettingsChangeMuteDepth - 1);
    }

    if (applied && !silent) {
        showToast('Applied profile library folder settings.');
        emitAutoTagProfileLibrarySettingsChanged('profile-library-apply');
    }

    return applied;
}

globalThis.collectAutoTagProfileLibrarySettings = function collectAutoTagProfileLibrarySettings() {
    return buildAutoTagProfileLibrarySettingsSnapshot();
};

globalThis.applyAutoTagProfileLibrarySettings = async function applyAutoTagProfileLibrarySettings(snapshot, options) {
    return applyAutoTagProfileLibrarySettingsSnapshot(snapshot, options);
};

async function startFolderEnhancement(folder, profileReference) {
    const rootPath = (folder?.rootPath || '').trim();
    if (!rootPath) {
        throw new Error('Folder path is missing.');
    }

    const desiredQuality = String(folder?.desiredQuality || '').trim().toUpperCase();
    if (desiredQuality === 'VIDEO' || desiredQuality === 'PODCAST') {
        throw new Error('Podcasts and videos do not use AutoTag enhancement profiles.');
    }

    const resolvedProfileReference = (profileReference || '').trim();
    if (!resolvedProfileReference) {
        throw new Error('Select an AutoTag profile for this folder first.');
    }

    const profiles = await fetchJson('/api/tagging/profiles');
    const list = Array.isArray(profiles) ? profiles : [];
    const profile = list.find((item) => {
        const name = (item?.name || '').trim();
        const id = (item?.id || '').trim();
        return normalizeProfileReference(id) === normalizeProfileReference(resolvedProfileReference)
            || normalizeProfileReference(name) === normalizeProfileReference(resolvedProfileReference);
    });

    if (!profile) {
        throw new Error(`Profile "${resolvedProfileReference}" was not found.`);
    }

    const resolvedProfileName = (profile?.name || resolvedProfileReference).trim();
    const baseConfig = profile?.autoTag?.data || profile?.autoTag;
    if (!baseConfig || typeof baseConfig !== 'object') {
        throw new Error(`Profile "${resolvedProfileName}" has no AutoTag settings.`);
    }

    const gapFillTags = Array.isArray(baseConfig.gapFillTags)
        ? baseConfig.gapFillTags.filter((tag) => typeof tag === 'string' && tag.trim().length > 0)
        : [];

    if (!gapFillTags.length) {
        throw new Error(`Profile "${resolvedProfileName}" has no Library Enhancement tags.`);
    }

    const response = await fetchJson('/api/autotag/start', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            path: rootPath,
            profileId: String(profile?.id || resolvedProfileReference || '').trim() || null,
            runIntent: 'enhancement_only'
        })
    });

    const jobId = response?.jobId ? String(response.jobId) : '';
    if (!jobId) {
        throw new Error('AutoTag did not return a job id.');
    }

    return { jobId, profileName: resolvedProfileName };
}

async function loadFolders() {
    const includeDisabled = !!document.getElementById('foldersContainer');
    const folders = await fetchJson(includeDisabled
        ? '/api/library/folders?includeDisabled=true'
        : '/api/library/folders');
    libraryState.folders = Array.isArray(folders)
        ? folders.map(normalizeFolderConversionState)
        : [];
    libraryState.aliasesLoaded = false;
    libraryState.aliases.clear();
    if (document.getElementById('foldersContainer')) {
        renderFolders();
        void loadAutoTagFolderDefaults().then(() => {
            renderFolders();
        });
    }
    await loadViewAliases();
    updateLibraryViewOptions();
    populateAlbumDestinationOptions();
    if (document.getElementById('artistsGrid') && Array.isArray(libraryState.allArtists) && libraryState.allArtists.length > 0) {
        await applyLibraryViewFilter();
    }
}

globalThis.refreshAutoTagFolderDefaults = async function refreshAutoTagFolderDefaults() {
    if (!document.getElementById('foldersContainer')) {
        return;
    }
    await loadAutoTagFolderDefaults();
    renderFolders();
};

async function loadArtists() {
    updateLibraryResultsMeta(-1, -1);
    const requestId = ++libraryState.artistLoadRequestId;
    if (libraryState.artistLoadAbortController) {
        libraryState.artistLoadAbortController.abort();
    }
    const abortController = new AbortController();
    libraryState.artistLoadAbortController = abortController;
    const params = new URLSearchParams();
    const selectedFolderId = getSelectedLibraryViewFolderId();
    params.set('availability', 'local');
    if (selectedFolderId !== null) {
        params.set('folderId', String(selectedFolderId));
    }
    const normalizedQuery = (libraryState.artistSearchQuery || '').trim();
    if (normalizedQuery) {
        params.set('search', normalizedQuery);
    }
    params.set('sort', libraryState.artistSortKey || 'name-asc');
    const incrementalRenderEnabled = !((libraryState.artistSearchQuery || '').trim())
        && (libraryState.artistSortKey || 'name-asc') === 'name-asc';
    const pageSize = 400;
    const pagedResult = await fetchArtistsPaged({
        baseParams: params,
        pageSize,
        abortController,
        requestId,
        incrementalRenderEnabled
    });
    if (!pagedResult) {
        return;
    }
    const collected = pagedResult.collected;

    if (requestId !== libraryState.artistLoadRequestId) {
        return;
    }

    libraryState.allArtists = collected;
    libraryState.filteredArtists = collected;
    libraryState.artistFolders.clear();
    libraryState.artistFolderScopeId = selectedFolderId;
    updateLibraryResultsMeta(collected.length, collected.length);
    renderArtistGrid(collected);
}

async function fetchArtistsPagePayload(baseParams, page, pageSize, abortController) {
    const pageParams = new URLSearchParams(baseParams);
    pageParams.set('page', String(page));
    pageParams.set('pageSize', String(pageSize));
    try {
        return await fetchJson(`/api/library/artists?${pageParams.toString()}`, { signal: abortController.signal });
    } catch (error) {
        if (error?.name === 'AbortError') {
            return null;
        }
        throw error;
    }
}

function normalizePagedArtistsResponse(payload, baseParams, page, pageSize) {
    if (Array.isArray(payload)) {
        const normalizedSearch = String(baseParams?.get('search') || '').trim().toLocaleLowerCase();
        const normalizedSort = String(baseParams?.get('sort') || 'name-asc').trim().toLowerCase() === 'name-desc'
            ? 'name-desc'
            : 'name-asc';
        const filtered = payload
            .filter((artist) => artist && typeof artist === 'object')
            .filter((artist) => !normalizedSearch || String(artist?.name || '').toLocaleLowerCase().includes(normalizedSearch))
            .sort((first, second) => compareArtistsBySort(first, second, normalizedSort));
        const totalCount = filtered.length;
        const offset = Math.max(0, (Math.max(1, page) - 1) * Math.max(1, pageSize));
        const items = filtered.slice(offset, offset + Math.max(1, pageSize));
        return {
            items,
            totalCount,
            hasMore: offset + Math.max(1, pageSize) < totalCount
        };
    }

    let rawItems = [];
    if (Array.isArray(payload?.items)) {
        rawItems = payload.items;
    } else if (Array.isArray(payload?.Items)) {
        rawItems = payload.Items;
    }
    const totalCountValue = payload?.totalCount ?? payload?.TotalCount;
    const totalCount = Number.isFinite(Number(totalCountValue))
        ? Number(totalCountValue)
        : rawItems.length;
    const hasMoreValue = payload?.hasMore ?? payload?.HasMore;
    const inferredHasMore = (Math.max(1, page) * Math.max(1, pageSize)) < totalCount;
    return {
        items: rawItems,
        totalCount,
        hasMore: hasMoreValue === true || (hasMoreValue == null && inferredHasMore)
    };
}

function updateArtistsIncrementalView(collected, totalCount, incrementalRenderEnabled) {
    libraryState.allArtists = [...collected];
    updateLibraryLoadProgressMeta(collected.length, totalCount);
    if (incrementalRenderEnabled && document.getElementById('artistsGrid')) {
        libraryState.filteredArtists = [...collected];
        const shouldForceWindowing = Number.isFinite(totalCount) && totalCount >= 600;
        renderArtistGrid(collected, { forceWindowing: shouldForceWindowing });
    }
}

async function fetchArtistsPaged(options) {
    const { baseParams, pageSize, abortController, requestId, incrementalRenderEnabled } = options;
    let page = 1;
    let totalCount = -1;
    const collected = [];

    while (true) {
        const payload = await fetchArtistsPagePayload(baseParams, page, pageSize, abortController);
        if (!payload) {
            return null;
        }
        if (requestId !== libraryState.artistLoadRequestId) {
            return null;
        }

        const normalizedPayload = normalizePagedArtistsResponse(payload, baseParams, page, pageSize);
        const items = Array.isArray(normalizedPayload?.items) ? normalizedPayload.items : [];
        totalCount = Number.isFinite(Number(normalizedPayload?.totalCount))
            ? Number(normalizedPayload.totalCount)
            : totalCount;
        if (items.length === 0) {
            break;
        }

        collected.push(...items);
        updateArtistsIncrementalView(collected, totalCount, incrementalRenderEnabled);

        const hasMore = normalizedPayload?.hasMore === true;
        if (!hasMore || (totalCount > 0 && collected.length >= totalCount)) {
            break;
        }

        page += 1;
    }

    return { collected, totalCount };
}

function getStoredLibraryViewSelection() {
    try {
        return sessionStorage.getItem(LIBRARY_VIEW_SESSION_KEY) || '';
    } catch {
        return '';
    }
}

function setStoredLibraryViewSelection(value) {
    try {
        if (!value || value === 'main') {
            sessionStorage.removeItem(LIBRARY_VIEW_SESSION_KEY);
            return;
        }

        sessionStorage.setItem(LIBRARY_VIEW_SESSION_KEY, value);
    } catch {
        // Ignore session storage failures.
    }
}

function getSelectedLibraryViewFolderId() {
    const viewSelect = document.getElementById('libraryViewSelect');
    const selected = (viewSelect?.value || getStoredLibraryViewSelection() || 'main').trim();
    if (!selected || selected === 'main') {
        return null;
    }

    const folderId = Number.parseInt(selected, 10);
    return Number.isFinite(folderId) ? folderId : null;
}

function getSelectedLibraryViewFolder() {
    const selectedFolderId = getSelectedLibraryViewFolderId();
    if (selectedFolderId === null) {
        return null;
    }

    return (libraryState.folders || []).find(folder => Number(folder?.id) === selectedFolderId) || null;
}

function getLibraryScopeFolderIdFromLocation() {
    try {
        const params = new URLSearchParams(globalThis.location.search);
        const raw = String(params.get('folderId') || '').trim();
        if (!raw || raw.toLowerCase() === 'main') {
            return null;
        }
        const parsed = Number.parseInt(raw, 10);
        return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
    } catch {
        return null;
    }
}

function getLibraryScopeSelectionFromLocation() {
    try {
        const params = new URLSearchParams(globalThis.location.search);
        if (!params.has('folderId')) {
            return '';
        }
        const raw = String(params.get('folderId') || '').trim();
        if (!raw || raw.toLowerCase() === 'main') {
            return 'main';
        }
        const parsed = Number.parseInt(raw, 10);
        return Number.isFinite(parsed) && parsed > 0 ? String(parsed) : 'main';
    } catch {
        return '';
    }
}

function applyLibraryScopeSelectionFromLocation() {
    const selection = getLibraryScopeSelectionFromLocation();
    if (!selection) {
        return;
    }
    setStoredLibraryViewSelection(selection);
}

function syncLibraryScopeInLocationBar(selectionValue) {
    try {
        const path = trimTrailingSlashes(String(globalThis.location.pathname || '').toLowerCase());
        if (path !== '/library') {
            return;
        }
        const normalizedSelection = String(selectionValue || '').trim().toLowerCase();
        const params = new URLSearchParams(globalThis.location.search);
        if (!normalizedSelection || normalizedSelection === 'main') {
            params.delete('folderId');
        } else {
            params.set('folderId', normalizedSelection);
        }
        const nextQuery = params.toString();
        const queryPrefix = nextQuery ? '?' : '';
        const nextUrl = `${globalThis.location.pathname}${queryPrefix}${nextQuery}${globalThis.location.hash || ''}`;
        globalThis.history.replaceState(globalThis.history.state, '', nextUrl);
    } catch {
        // Ignore history/location update failures.
    }
}

function trimTrailingSlashes(value) {
    let index = value.length;
    while (index > 1 && value.codePointAt(index - 1) === 47) {
        index--;
    }
    return value.slice(0, index);
}

function resolveLibraryScopeFolderIdForNavigation() {
    const selectedFolderId = getSelectedLibraryViewFolderId();
    if (selectedFolderId !== null) {
        return selectedFolderId;
    }

    return getLibraryScopeFolderIdFromLocation();
}

function buildLibraryScopedUrl(path, folderId = resolveLibraryScopeFolderIdForNavigation()) {
    if (!path || folderId === null) {
        return path;
    }

    const suffix = path.includes('?') ? '&' : '?';
    return `${path}${suffix}folderId=${encodeURIComponent(String(folderId))}`;
}

async function runLocalScan(refreshImages = false, reset = false) {
    try {
        const url = buildLibraryScanUrl(refreshImages, reset);
        const scanStartedAt = Date.now();
        showToast('Library scan started.');
        await fetchJson(url, { method: 'POST', keepalive: true });
        waitForScanCompletion(scanStartedAt);
        showToast(resolveLibraryScanSuccessMessage(refreshImages, reset));
    } catch (error) {
        showToast(`Refresh failed: ${error.message}`, true);
    }
}

function buildLibraryScanUrl(refreshImages, reset) {
    const params = new URLSearchParams();
    if (refreshImages) {
        params.set('refreshImages', 'true');
    }
    if (reset) {
        params.set('reset', 'true');
    }
    const scopedFolderId = getSelectedLibraryViewFolderId();
    if (scopedFolderId !== null) {
        params.set('folderId', String(scopedFolderId));
    }

    const suffix = params.toString();
    return suffix ? `/api/library/scan?${suffix}` : '/api/library/scan';
}

function resolveLibraryScanSuccessMessage(refreshImages, reset) {
    if (reset && refreshImages) {
        return 'Library data reset and images refreshed.';
    }
    if (reset) {
        return 'Library data reset and refreshed.';
    }

    return refreshImages ? 'Images refreshed.' : 'Library refreshed.';
}

async function waitForScanCompletion(startedAtMs) {
    const deadline = startedAtMs + (15 * 60 * 1000);
    const policy = getLibraryRefreshPolicy();
    const refreshIntervalMs = Math.max(3000, policy.scanStatusActiveMs);
    let lastRefreshAt = 0;
    const refreshViews = async () => {
        try {
            await loadLibraryScanStatus();
        } catch {
        }
        if (document.getElementById('artistsGrid')) {
            try {
                await loadArtists();
            } catch {
            }
        }
    };

    while (Date.now() < deadline) {
        const now = Date.now();
        if ((now - lastRefreshAt) >= refreshIntervalMs) {
            await refreshViews();
            lastRefreshAt = now;
        }

        try {
            const logs = await fetchJson('/api/library/scan/logs?limit=200');
            const recent = Array.isArray(logs)
                ? logs.filter(entry => entry.timestampUtc && new Date(entry.timestampUtc).getTime() >= startedAtMs - 1000)
                : [];
            const failed = recent.find(entry => (entry.message || '').includes('Library scan failed'));
            if (failed) {
                showToast('Library scan failed.', true);
                await refreshViews();
                return;
            }
            const completed = recent.find(entry => (entry.message || '').includes('Library scan completed'));
            if (completed) {
                showToast('Library scan completed.');
                await refreshViews();
                return;
            }
        } catch {
            // Ignore polling errors; try again.
        }
        await new Promise(resolve => setTimeout(resolve, refreshIntervalMs));
    }
    showToast('Library scan still running.', true);
    await refreshViews();
}

function clearArtistGridVirtualization() {
    if (typeof libraryState.artistVirtualizationCleanup === 'function') {
        libraryState.artistVirtualizationCleanup();
    }
    libraryState.artistVirtualizationCleanup = null;
    libraryState.artistVirtualizationKey = '';
}

function createArtistCardElement(artist, anchorId = '') {
    const card = document.createElement('div');
    card.className = 'artist-card ds-tile';
    if (anchorId) {
        card.id = anchorId;
    }
    const initials = (artist?.name || '')
        .split(' ')
        .map(part => part[0])
        .slice(0, 2)
        .join('')
        .toUpperCase();
    const coverPath = artist?.preferredImagePath
        ? `/api/library/image?path=${encodeURIComponent(artist.preferredImagePath)}&size=240`
        : '';
    const coverUrl = coverPath ? appendCacheKey(coverPath) : '';
    const imageMarkup = artist?.preferredImagePath
        ? `<img src="${coverUrl}" alt="${escapeHtml(artist?.name || 'Artist')}" loading="lazy" decoding="async" />`
        : `<div class="artist-initials">${initials || '?'}</div>`;
    card.innerHTML = `
        ${imageMarkup}
        <strong>${escapeHtml(artist?.name || 'Unknown Artist')}</strong>
    `;
    card.addEventListener('click', () => {
        persistLibraryReturnState();
        globalThis.location.href = buildLibraryScopedUrl(`/Library/Artist/${artist.id}`);
    });
    return card;
}

function measureArtistGridLengthPx(container, cssExpression, fallback) {
    const probe = document.createElement('span');
    probe.style.position = 'absolute';
    probe.style.visibility = 'hidden';
    probe.style.pointerEvents = 'none';
    probe.style.height = '0';
    probe.style.overflow = 'hidden';
    probe.style.width = cssExpression;
    container.appendChild(probe);
    const measured = probe.getBoundingClientRect().width;
    probe.remove();
    if (Number.isFinite(measured) && measured > 0) {
        return measured;
    }
    return fallback;
}

function getArtistGridWindowMetrics(container) {
    const computed = getComputedStyle(container);
    const gapRaw = computed.columnGap || computed.gap || '';
    const parsedGap = Number.parseFloat(gapRaw);
    const gap = Number.isFinite(parsedGap) && parsedGap >= 0 ? parsedGap : 16;
    const templateColumns = (computed.gridTemplateColumns || '').trim();
    const columnsFromCss = templateColumns && templateColumns !== 'none'
        ? templateColumns.split(/\s+/).filter(Boolean).length
        : 0;

    let columns = columnsFromCss;
    let cardSize = 0;
    if (columns > 0) {
        const totalGap = gap * Math.max(0, columns - 1);
        cardSize = (container.clientWidth - totalGap) / columns;
    }

    if (!Number.isFinite(cardSize) || cardSize <= 0) {
        cardSize = measureArtistGridLengthPx(container, 'var(--art-card-size)', 180);
    }
    cardSize = Math.max(120, Math.round(cardSize));

    if (!Number.isFinite(columns) || columns < 1) {
        const minColumnWidth = cardSize + gap;
        columns = Math.max(1, Math.floor((container.clientWidth + gap) / Math.max(1, minColumnWidth)));
    }

    return { columns, cardSize, gap };
}

function renderArtistGridWindowed(container, artists, letterNav) {
    const renderKey = artists.map(item => `${item.id}:${item.name || ''}`).join('|');
    if (libraryState.artistVirtualizationKey !== renderKey) {
        clearArtistGridVirtualization();
    }
    libraryState.artistVirtualizationKey = renderKey;

    container.innerHTML = '';
    const metrics = getArtistGridWindowMetrics(container);
    const columns = metrics.columns;
    const cardSize = metrics.cardSize;
    const gap = metrics.gap;
    container.classList.add('artist-grid-windowed');
    container.style.position = 'relative';
    const rowHeight = cardSize + 48;
    const totalRows = Math.max(1, Math.ceil(artists.length / columns));
    const totalHeight = totalRows * rowHeight;
    container.style.height = `${totalHeight}px`;

    const anchorIdsByLetter = new Map();
    const firstIndexByLetter = new Map();
    artists.forEach((artist, index) => {
        const letter = getAlphaJumpLetter(artist?.name);
        if (!firstIndexByLetter.has(letter)) {
            firstIndexByLetter.set(letter, index);
            const anchorId = buildAlphaJumpAnchorId('library', letter);
            anchorIdsByLetter.set(letter, anchorId);
            const anchor = document.createElement('span');
            anchor.id = anchorId;
            anchor.className = 'artist-grid-anchor';
            anchor.style.top = `${Math.floor(index / columns) * rowHeight}px`;
            container.appendChild(anchor);
        }
    });

    const content = document.createElement('div');
    content.className = 'artist-grid-windowed-content';
    content.style.height = `${totalHeight}px`;
    container.appendChild(content);

    const overscanRows = 4;
    let lastRangeKey = '';
    const renderVisibleRange = () => {
        const containerRect = container.getBoundingClientRect();
        const viewportHeight = globalThis.innerHeight || document.documentElement.clientHeight || 900;
        if (containerRect.bottom < -200 || containerRect.top > viewportHeight + 200) {
            return;
        }
        const absoluteTop = containerRect.top + globalThis.scrollY;
        const viewportTop = globalThis.scrollY;
        const viewportBottom = viewportTop + viewportHeight;
        const visibleTopPx = Math.max(0, viewportTop - absoluteTop);
        const visibleBottomPx = Math.min(totalHeight, viewportBottom - absoluteTop);
        const startRow = Math.max(0, Math.floor(visibleTopPx / rowHeight) - overscanRows);
        const endRow = Math.min(totalRows, Math.ceil(visibleBottomPx / rowHeight) + overscanRows);
        const startIndex = Math.max(0, startRow * columns);
        const endIndex = Math.min(artists.length, endRow * columns);
        const rangeKey = `${startIndex}:${endIndex}:${columns}`;
        if (rangeKey === lastRangeKey) {
            return;
        }
        lastRangeKey = rangeKey;

        content.innerHTML = '';
        for (let index = startIndex; index < endIndex; index++) {
            const artist = artists[index];
            if (!artist) {
                continue;
            }
            const letter = getAlphaJumpLetter(artist?.name);
            const anchorId = firstIndexByLetter.get(letter) === index
                ? anchorIdsByLetter.get(letter) || ''
                : '';
            const card = createArtistCardElement(artist, anchorId);
            const row = Math.floor(index / columns);
            const col = index % columns;
            card.style.position = 'absolute';
            card.style.top = `${row * rowHeight}px`;
            card.style.left = `${col * (cardSize + gap)}px`;
            card.style.width = `${cardSize}px`;
            content.appendChild(card);
        }
    };

    const onScroll = () => {
        globalThis.requestAnimationFrame(renderVisibleRange);
    };
    const onResize = () => {
        clearArtistGridVirtualization();
        renderArtistGrid(artists);
    };

    globalThis.addEventListener('scroll', onScroll, { passive: true });
    globalThis.addEventListener('resize', onResize);
    libraryState.artistVirtualizationCleanup = () => {
        globalThis.removeEventListener('scroll', onScroll);
        globalThis.removeEventListener('resize', onResize);
    };

    renderVisibleRange();
    renderAlphaJumpNavigation(letterNav, anchorIdsByLetter);
    applyPendingLibraryScrollRestore();
}

function renderArtistGrid(artists, options = {}) {
    const container = document.getElementById('artistsGrid');
    const letterNav = document.getElementById('libraryLetterNav');
    if (!container) {
        return;
    }
    clearArtistGridVirtualization();
    container.classList.remove('artist-grid-windowed');
    container.style.position = '';
    container.style.height = '';
    container.innerHTML = '';
    if (!artists.length) {
        const hasFilter = !!(libraryState.artistSearchQuery || '').trim();
        container.innerHTML = hasFilter
            ? '<p class="library-empty-note">No artists match your filter.</p>'
            : '<p class="library-empty-note">No local artists yet.</p>';
        renderAlphaJumpNavigation(letterNav, new Map());
        applyPendingLibraryScrollRestore();
        return;
    }

    const shouldUseWindowing = options.forceWindowing === true || artists.length >= 600;
    if (shouldUseWindowing) {
        renderArtistGridWindowed(container, artists, letterNav);
        return;
    }

    const anchorIdsByLetter = new Map();
    artists.forEach(artist => {
        const letter = getAlphaJumpLetter(artist?.name);
        let anchorId = '';
        if (!anchorIdsByLetter.has(letter)) {
            anchorId = buildAlphaJumpAnchorId('library', letter);
        }
        if (anchorId) {
            anchorIdsByLetter.set(letter, anchorId);
        }
        const card = createArtistCardElement(artist, anchorId);
        container.appendChild(card);
    });

    renderAlphaJumpNavigation(letterNav, anchorIdsByLetter);
    applyPendingLibraryScrollRestore();
}

async function loadAlbums(artistId) {
    const artistIdValue = artistId || document.querySelector('[data-artist-id]')?.dataset.artistId;
    if (!artistIdValue) {
        return;
    }
    libraryState.deezerAlbumIndex.clear();
    libraryState.artistDataReady.local = false;
    libraryState.localTopSongTrackIndex = null;
    libraryState.localTopSongTrackArtistId = null;
    libraryState.localTopSongTrackIndexPromise = null;
    libraryState.appleExtras.localTrackVariantIndex = null;
    libraryState.appleExtras.localTrackVariantArtistId = null;
    libraryState.appleExtras.localTrackVariantIndexPromise = null;
    const cachedUnavailable = libraryState.unavailableAlbums.get(artistIdValue);
    const [artist, albums, appleIdData] = await Promise.all([
        fetchJsonOptional(`/api/library/artists/${artistIdValue}`),
        fetchJson(buildLibraryScopedUrl(`/api/library/artists/${artistIdValue}/albums`)),
        fetchJsonOptional(`/api/library/artists/${artistIdValue}/apple-id`)
    ]);

    let resolvedArtist = artist;
    if (!resolvedArtist) {
        showToast('Artist details missing. Showing albums only.', true);
    }

    libraryState.artistVisuals.preferredAvatarPath = (resolvedArtist?.preferredImagePath || '').toString().trim() || null;
    libraryState.artistVisuals.preferredBackgroundPath = (resolvedArtist?.preferredBackgroundPath || '').toString().trim() || null;

    const artistName = document.getElementById('artistName');
    if (artistName && resolvedArtist) {
        artistName.textContent = resolvedArtist.name;
    }
    libraryState.currentLocalArtistName = (resolvedArtist?.name || '').trim();
    loadExternalArtistVisuals(libraryState.currentLocalArtistName, artistIdValue, appleIdData?.appleId || null);
    const spotifyIdEl = document.getElementById('artistSpotifyId');
    if (spotifyIdEl && resolvedArtist) {
        spotifyIdEl.removeAttribute('href');
        spotifyIdEl.style.display = 'none';
    }

    const avatarEl = document.getElementById('artistAvatar');
    if (avatarEl && resolvedArtist?.preferredImagePath) {
        const avatarPath = appendCacheKey(`/api/library/image?path=${encodeURIComponent(resolvedArtist.preferredImagePath)}&size=320`);
        setArtistAvatarImageElement(avatarEl, avatarPath, resolvedArtist.name || 'Artist', true);
    }

    const bgEl = document.querySelector('.artist-page');
    if (bgEl && libraryState.artistVisuals.preferredBackgroundPath) {
        const backgroundUrl = appendCacheKey(buildLibraryImageUrl(libraryState.artistVisuals.preferredBackgroundPath));
        applyArtistHeroBackgroundImage(backgroundUrl, true);
    }

    const available = albums.filter(album => (album.localFolders || []).length > 0);
    indexDeezerAlbums(available);

    libraryState.localAlbums = available;
    libraryState.artistDataReady.local = true;
    tryRenderDiscography();

    if (!cachedUnavailable) {
        fetchJsonOptional(`/api/library/artists/${artistIdValue}/unavailable`)
            .then(unavailableAlbums => {
                const unavailable = Array.isArray(unavailableAlbums) ? unavailableAlbums : [];
                libraryState.unavailableAlbums.set(artistIdValue, unavailable);
            })
            .catch(() => {});
    }

    initSpotifyAvatarFetch(artistIdValue);
    initSpotifyCacheControls(artistIdValue);
    const storedAppleId = appleIdData?.appleId || null;
    initAppleLazyLoad(resolvedArtist?.name, storedAppleId);
    initAppleIdEditor(artistIdValue);
}

function renderSpotifyArtistUnavailable(reason = 'Spotify artist data unavailable.') {
    const message = reason;
    libraryState.currentSpotifyArtist = null;
    updateTopSongsTracklistLink(null);
    const bioPanel = document.getElementById('spotifyBioPanel');
    const bio = document.getElementById('artistBiography');
    if (bioPanel) {
        bioPanel.style.display = 'block';
    }
    if (bio) {
        bio.textContent = message;
    }
    if (typeof globalThis.refreshArtistBiographyClamp === 'function') {
        globalThis.refreshArtistBiographyClamp(true);
    }

    const topTracks = document.getElementById('spotifyTopTracksList');
    if (topTracks) {
        topTracks.innerHTML = '<div class="empty-card">Top songs unavailable.</div>';
    }

    const related = document.getElementById('relatedArtists');
    if (related) {
        related.innerHTML = '<div class="empty-card">Related artists unavailable.</div>';
    }

    const appearsGrid = document.querySelector('#spotifyAppearsPanel .album-grid');
    if (appearsGrid) {
        appearsGrid.innerHTML = '<div class="empty-card">Appears on unavailable.</div>';
    }
}

function updateTopSongsTracklistLink(artist) {
    const link = document.getElementById('topSongsTracklistLink');
    if (!link) {
        return;
    }

    const spotifyId = (artist?.id || libraryState.currentSpotifyArtist?.id || '').toString().trim();
    if (!spotifyId) {
        link.setAttribute('href', '#');
        link.setAttribute('aria-disabled', 'true');
        link.classList.add('is-disabled');
        return;
    }

    const qs = new URLSearchParams();
    qs.set('id', spotifyId);
    qs.set('type', 'artist');
    qs.set('source', 'spotify');
    link.setAttribute('href', `/Tracklist?${qs.toString()}`);
    link.setAttribute('aria-disabled', 'false');
    link.classList.remove('is-disabled');
}

function getSpotifyCacheStatusElement() {
    return document.getElementById('spotify-cache-status');
}

function setSpotifyCacheStatus(message) {
    const statusEl = getSpotifyCacheStatusElement();
    if (statusEl) {
        statusEl.textContent = message;
    }
}

function canUseSpotifyBrowserCache(browserCached) {
    return !!(browserCached && typeof browserCached === 'object' && !isSpotifyPayloadUnavailable(browserCached));
}

function renderCachedSpotifyArtistPayload(browserCached) {
    const cachedAlbums = Array.isArray(browserCached?.albums) ? browserCached.albums : [];
    const cachedTopTracks = Array.isArray(browserCached?.topTracks) ? browserCached.topTracks : [];
    const hasExistingAlbums = Array.isArray(libraryState.spotifyAlbums) && libraryState.spotifyAlbums.length > 0;

    if (cachedAlbums.length > 0 || !hasExistingAlbums) {
        libraryState.spotifyAlbums = cachedAlbums;
    }
    if (browserCached?.artist) {
        applySpotifyArtistProfile(browserCached.artist);
    }
    renderSpotifyRelatedArtists(browserCached?.relatedArtists || []);
    renderSpotifyTopTracks(cachedTopTracks, browserCached?.artist || null);
    renderSpotifyAppearsOn(browserCached?.appearsOn || []);
    renderSpotifyRecentReleases(cachedAlbums);
    libraryState.artistDataReady.spotify = true;
    tryRenderDiscography();
}

function buildSpotifyArtistRequestQuery(artistId, options) {
    const params = new URLSearchParams();
    if (options.cacheOnly) {
        params.set('cacheOnly', 'true');
    }
    if (options.forceRefresh) {
        params.set('refresh', 'true');
    }
    if (options.forceRematch) {
        params.set('rematch', 'true');
    }

    const fallbackSpotifyId = options.forceRematch
        ? ''
        : (((new URLSearchParams(globalThis.location.search).get('spotifyId') || '')
            || options.browserCached?.artist?.id
            || options.browserCached?.artistPage?.id
            || libraryState.currentSpotifyArtist?.id
            || loadLibrarySpotifyArtistIdHint(artistId)
            || '')
            .toString()
            .trim());
    if (fallbackSpotifyId) {
        params.set('spotifyId', fallbackSpotifyId);
    }

    const fallbackArtistName = (options.browserCached?.artist?.name
        || libraryState.currentSpotifyArtist?.name
        || libraryState.currentLocalArtistName
        || document.getElementById('artistName')?.textContent
        || '')
        .toString()
        .trim();
    if (fallbackArtistName) {
        params.set('artistName', fallbackArtistName);
    }

    return params.toString();
}

function selectSpotifyPayloadForRender(spotifyData, browserCached) {
    const mergedPayload = mergeSpotifyArtistPayload(spotifyData, browserCached);
    const effectivePayload = safeNormalizeLibrarySpotifyPayload(mergedPayload, 'merged');
    if (effectivePayload && typeof effectivePayload === 'object') {
        return effectivePayload;
    }
    if (spotifyData && typeof spotifyData === 'object') {
        return spotifyData;
    }
    return browserCached;
}

function prepareSpotifyArtistRenderPayload(payloadForRender) {
    if (!payloadForRender || typeof payloadForRender !== 'object') {
        return null;
    }

    const incomingAlbums = Array.isArray(payloadForRender.albums) ? payloadForRender.albums : [];
    const hasExistingAlbums = Array.isArray(libraryState.spotifyAlbums) && libraryState.spotifyAlbums.length > 0;
    const preserveExistingAlbums = incomingAlbums.length === 0 && hasExistingAlbums;
    const effectiveAlbums = preserveExistingAlbums ? libraryState.spotifyAlbums : incomingAlbums;
    const topTracks = hydrateTopTracksWithAlbumDates(
        Array.isArray(payloadForRender.topTracks) ? payloadForRender.topTracks : [],
        effectiveAlbums
    );

    return {
        payloadForRender,
        effectiveAlbums,
        topTracks,
        preserveExistingAlbums
    };
}

function renderSpotifyArtistPayload(artistId, payloadForRender, effectiveAlbums, topTracks, preserveExistingAlbums) {
    setSpotifyCacheStatus(preserveExistingAlbums ? 'Spotify refresh: loaded (preserved)' : 'Spotify refresh: loaded');
    libraryState.spotifyAlbums = effectiveAlbums;

    if (payloadForRender.artist) {
        applySpotifyArtistProfile(payloadForRender.artist);
        if (payloadForRender.artist.id) {
            saveLibrarySpotifyArtistIdHint(artistId, payloadForRender.artist.id);
        }
    }

    renderSpotifyRelatedArtists(payloadForRender.relatedArtists || []);
    renderSpotifyTopTracks(topTracks, payloadForRender.artist || null);
    renderSpotifyAppearsOn(payloadForRender.appearsOn || []);
    renderSpotifyRecentReleases(effectiveAlbums);

    libraryState.artistDataReady.spotify = true;
    tryRenderDiscography();
    saveLibrarySpotifyArtistCache(artistId, {
        ...payloadForRender,
        albums: effectiveAlbums,
        topTracks
    });
}

function scheduleSpotifyArtistSupplementaryFetches(payloadForRender) {
    const spotifyArtistId = payloadForRender?.artist?.id;
    if (!spotifyArtistId) {
        return;
    }

    if (!payloadForRender.relatedArtists || payloadForRender.relatedArtists.length === 0) {
        fetchJsonOptional(`/api/spotify/artist/${encodeURIComponent(spotifyArtistId)}/related`)
            .then(result => {
                const related = result?.relatedArtists || [];
                if (related.length) {
                    renderSpotifyRelatedArtists(related);
                }
            })
            .catch(() => {});
    }

    if (!payloadForRender.appearsOn || payloadForRender.appearsOn.length === 0) {
        fetchJsonOptional(`/api/spotify/artist/${encodeURIComponent(spotifyArtistId)}/appears-on`)
            .then(result => {
                const appears = result?.appearsOn || [];
                if (appears.length) {
                    renderSpotifyAppearsOn(appears);
                }
            })
            .catch(() => {});
    }
}

async function loadSpotifyArtist(artistId, forceRefresh = false, forceRematch = false, cacheOnly = false) {
    if (forceRematch) {
        clearLibrarySpotifyArtistState(artistId);
    }
    const { browserCached, hasBrowserCached } = getLibrarySpotifyBrowserCacheState(artistId, forceRefresh, forceRematch);

    try {
        await ensureLocalArtistIndex();
        setSpotifyCacheStatus(forceRefresh ? 'Spotify refresh: requested' : 'Spotify refresh: cache');

        if (hasBrowserCached) {
            try {
                renderCachedSpotifyArtistPayload(browserCached);
                setSpotifyCacheStatus('Spotify refresh: loaded (cached)');
            } catch (cachedRenderError) {
                console.warn('Cached Spotify payload render failed; continuing with API payload.', cachedRenderError);
            }
        }

        const query = buildSpotifyArtistRequestQuery(artistId, {
            cacheOnly,
            forceRefresh,
            forceRematch,
            browserCached
        });
        const spotifyApiPath = query
            ? `/api/library/artists/${artistId}/spotify?${query}`
            : `/api/library/artists/${artistId}/spotify`;
        const spotifyDataRaw = await fetchJsonOptional(spotifyApiPath);
        if (!spotifyDataRaw || isSpotifyPayloadUnavailable(spotifyDataRaw)) {
            handleUnavailableSpotifyArtistResponse(hasBrowserCached, cacheOnly);
            return;
        }

        const spotifyData = safeNormalizeLibrarySpotifyPayload(spotifyDataRaw, 'api-response');
        const payloadForRender = selectSpotifyPayloadForRender(spotifyData, browserCached);
        if (cacheOnly && (!Array.isArray(payloadForRender?.albums) || payloadForRender.albums.length === 0)) {
            setSpotifyCacheStatus('Spotify refresh: cache warmup');
            return;
        }

        const renderState = prepareSpotifyArtistRenderPayload(payloadForRender);
        if (!renderState) {
            setSpotifyCacheStatus('Spotify refresh: no renderable payload');
            renderSpotifyArtistUnavailable('Spotify artist data unavailable.');
            return;
        }

        renderSpotifyArtistPayload(
            artistId,
            renderState.payloadForRender,
            renderState.effectiveAlbums,
            renderState.topTracks,
            renderState.preserveExistingAlbums
        );
        scheduleSpotifyArtistSupplementaryFetches(renderState.payloadForRender);
    } catch (error) {
        handleSpotifyArtistRequestFailure(error, hasBrowserCached, cacheOnly);
    }
}

function initSpotifyAvatarFetch(artistId) {
    const avatar = document.getElementById('artistAvatar');
    if (!avatar) {
        return;
    }
    avatar.style.cursor = 'pointer';
    avatar.addEventListener('click', async () => {
        await loadSpotifyArtist(artistId, true, true);
    });
}

async function loadSpotifyCacheImages(artistId) {
    try {
        const images = await fetchJsonOptional(`/api/library/spotify-cache/images?artistId=${encodeURIComponent(artistId)}`);
        libraryState.artistVisuals.artistId = artistId;
        libraryState.artistVisuals.cacheImages = Array.isArray(images) ? images : [];
        renderArtistVisualPicker(artistId);
    } catch (error) {
        console.warn('Failed to load cached Spotify images.', error);
    }
}

function initSpotifyCacheControls(artistId) {
    const refreshButton = document.getElementById('spotify-cache-refresh-button');
    const resetMatchButton = document.getElementById('spotify-match-reset-button');
    const pushButton = document.getElementById('spotify-cache-push-button');
    const targetSelect = document.getElementById('spotify-cache-push-target');
    const cachePanel = document.getElementById('spotify-cache-panel');

    if (!refreshButton || !resetMatchButton || !pushButton || !targetSelect || !cachePanel) {
        return;
    }

    if (refreshButton.dataset.bound === 'true') {
        return;
    }
    refreshButton.dataset.bound = 'true';
    resetMatchButton.dataset.bound = 'true';
    pushButton.dataset.bound = 'true';

    refreshButton.addEventListener('click', async () => {
        cachePanel.classList.add('is-open');
        const statusEl = document.getElementById('spotify-cache-status');
        try {
            await fetchJson(`/api/library/spotify-cache/refresh?artistId=${encodeURIComponent(artistId)}`, { method: 'POST' });
            showToast('Spotify cache refresh queued.', false);
            if (statusEl) {
                statusEl.textContent = 'Spotify refresh: queued';
            }
            // Keep rendering deterministic: read current payload, refresh in background.
            await loadSpotifyArtist(artistId, false, false);
            setTimeout(() => { void loadSpotifyArtist(artistId, false, false); }, 3500);
            setTimeout(() => loadSpotifyCacheImages(artistId), 2000);
        } catch (error) {
            showToast(`Spotify cache refresh failed: ${error?.message || error}`, true);
            if (statusEl) {
                statusEl.textContent = 'Spotify refresh: failed';
            }
        }
    });

    resetMatchButton.addEventListener('click', async () => {
        cachePanel.classList.add('is-open');
        const statusEl = document.getElementById('spotify-cache-status');
        try {
            await fetchJson(`/api/library/artists/${artistId}/spotify-reset`, { method: 'POST' });
            clearLibrarySpotifyArtistState(artistId);
            libraryState.currentSpotifyArtist = null;
            if (statusEl) {
                statusEl.textContent = 'Spotify refresh: rematching';
            }
            await loadSpotifyArtist(artistId, true, true);
            setTimeout(() => loadSpotifyCacheImages(artistId), 2000);
            showToast('Spotify artist match reset.');
        } catch (error) {
            showToast(`Spotify match reset failed: ${error?.message || error}`, true);
            if (statusEl) {
                statusEl.textContent = 'Spotify refresh: reset failed';
            }
        }
    });

    pushButton.addEventListener('click', async () => {
        const selection = getSpotifySyncSelectionState(artistId);
        const missingItems = getSpotifySyncMissingItems(selection);
        if (!validateSpotifySyncSelection(selection, missingItems)) {
            return;
        }

        if (missingItems.length > 0) {
            showToast(`${missingItems.join(' and ')} not set in app visuals, pushing remaining items only.`, false);
        }
        if (selection.includeBio && !selection.biography) {
            showToast('Background info is empty, so background info push will be skipped.', false);
        }

        const { payload } = buildSpotifySyncPayload(
            artistId,
            targetSelect.value,
            selection
        );

        try {
            console.debug('[push] payload', payload);

            const result = await fetchJson('/api/library/spotify-cache/push', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });

            console.debug('[push] result', result);
            showSpotifySyncUpdateResult(result);
        } catch (error) {
            console.error('[push] failed', error);
            showToast(`Push failed: ${error?.message || error}`, true);
        }
    });

    loadSpotifyCacheImages(artistId);
}

function renderSpotifyRelatedArtists(artists) {
    const container = document.getElementById('relatedArtists');
    if (!container) {
        return;
    }

    const safeArtists = Array.isArray(artists)
        ? artists.filter(artist => artist && typeof artist === 'object')
        : [];
    if (safeArtists.length === 0) {
        container.innerHTML = '<div class="empty-card">Related artists not available yet.</div>';
        return;
    }

    container.innerHTML = safeArtists.map(artist => {
        const image = toSafeHttpUrl(selectImage(artist.images, 'medium') || selectImage(artist.images, 'small'));
        const coverMarkup = image
            ? `<img src="${escapeHtml(image)}" alt="${escapeHtml(artist.name || '')}" loading="lazy" decoding="async" />`
            : `<div class="artist-initials">${escapeHtml(getInitials(artist.name))}</div>`;
        const normalized = normalizeArtistName(artist.name);
        const localId = normalized ? libraryState.localArtistIndex.get(normalized) : null;
        if (localId) {
            return `
                <div class="related-artist-card">
                    <a href="${buildLibraryScopedUrl(`/Library/Artist/${localId}`)}">
                        <div class="related-artist-card__avatar">${coverMarkup}</div>
                        <div class="related-artist-card__name">${escapeHtml(artist.name || '')}</div>
                    </a>
                </div>
            `;
        }
        const spotifyUrl = toSafeHttpUrl(artist.sourceUrl || '');
        const deezerId = String(artist.deezerId || '');
        const spotifyId = String(artist.id || '');
        return `
            <div class="related-artist-card">
                <button class="related-artist-link" type="button" data-spotify-url="${escapeHtml(spotifyUrl)}" data-deezer-id="${escapeHtml(deezerId)}" data-spotify-id="${escapeHtml(spotifyId)}">
                    <div class="related-artist-card__avatar">${coverMarkup}</div>
                    <div class="related-artist-card__name">${escapeHtml(artist.name || '')}</div>
                </button>
            </div>
        `;
    }).join('');

    container.querySelectorAll('.related-artist-link').forEach((button) => {
        button.addEventListener('click', async () => {
            const deezerId = button.dataset.deezerId;
            if (deezerId) {
                globalThis.location.href = `/Artist?id=${encodeURIComponent(deezerId)}&source=deezer`;
                return;
            }

            const spotifyId = button.dataset.spotifyId;
            if (spotifyId) {
                globalThis.location.href = `/Artist?id=${encodeURIComponent(spotifyId)}&source=spotify`;
                return;
            }

            const spotifyUrl = button.dataset.spotifyUrl;
            const parsed = spotifyUrlHelpers.parseSpotifyUrl(spotifyUrl || '');
            if (parsed?.type === 'artist' && parsed.id) {
                globalThis.location.href = `/Artist?id=${encodeURIComponent(parsed.id)}&source=spotify`;
                return;
            }

            showToast('Artist not available in the local library yet.', true);
        });
    });
}

function renderSpotifyTopTracks(tracks, _artistProfile = null) {
    const panel = document.getElementById('spotifyTopTracksPanel');
    const list = document.getElementById('spotifyTopTracksList');
    const safeTracks = Array.isArray(tracks)
        ? tracks.filter(track => track && typeof track === 'object')
        : [];
    if (!safeTracks.length || !panel || !list) {
        return;
    }

    list.innerHTML = `
        <div class="top-songs-grid">
            ${safeTracks.slice(0, 9).map((track) => {
        const image = toSafeHttpUrl(selectImage(track.albumImages, 'small'));
        const cover = image ? `<img src="${escapeHtml(image)}" alt="${escapeHtml(track.name || '')}" loading="lazy" decoding="async" />` : '<div class="top-song-item__placeholder"></div>';
        const spotifyUrl = normalizeSpotifyTrackSourceUrl(track.sourceUrl || '', track.id || '');
        const safeSpotifyUrl = spotifyUrl ? escapeHtml(spotifyUrl) : '';
        const dataAttrs = safeSpotifyUrl ? `data-spotify-url="${safeSpotifyUrl}"` : '';
        const deezerId = String(track.deezerId || '').trim();
        const deezerAttr = deezerId ? ` data-deezer-id="${escapeHtml(deezerId)}"` : '';
        const previewUrl = (track.previewUrl || track.preview || '').toString().trim();
        const previewAttr = previewUrl ? ` data-preview-url="${escapeHtml(previewUrl)}"` : '';
        const titleAttr = track.name ? ` data-track-title="${escapeHtml(track.name)}"` : '';
        const fallbackArtistName = String(_artistProfile?.name || libraryState.currentSpotifyArtist?.name || '').trim();
        const artistName = String(track.artistName || track.artist || fallbackArtistName).trim();
        const artistAttr = artistName ? ` data-track-artist="${escapeHtml(artistName)}"` : '';
        const albumName = String(track.albumName || '').trim();
        const albumAttr = albumName ? ` data-track-album="${escapeHtml(albumName)}"` : '';
        const isrc = String(track.isrc || '').trim();
        const isrcAttr = isrc ? ` data-track-isrc="${escapeHtml(isrc)}"` : '';
        const durationMs = Number(track.durationMs || 0) > 0 ? Math.trunc(Number(track.durationMs)) : 0;
        const durationAttr = durationMs > 0 ? ` data-track-duration-ms="${durationMs}"` : '';
        const canPlay = Boolean(deezerId || spotifyUrl || previewUrl);
        const playButtonSpotifyAttr = safeSpotifyUrl
            ? ` data-spotify-url="${safeSpotifyUrl}"`
            : '';
        const playButton = canPlay
            ? `<button class="top-song-item__play track-action track-play" type="button"${playButtonSpotifyAttr}${deezerAttr}${previewAttr}${titleAttr}${artistAttr}${albumAttr}${isrcAttr}${durationAttr} aria-label="Play ${escapeHtml(track.name || 'track')} preview">
                    <span class="playback-glyph" aria-hidden="true">
                        <svg class="playback-icon playback-icon--play" viewBox="0 0 24 24" focusable="false">
                            <path d="M8 5v14l11-7z"></path>
                        </svg>
                        <svg class="playback-icon playback-icon--pause" viewBox="0 0 24 24" focusable="false">
                            <path d="M7 5h4v14H7zM13 5h4v14h-4z"></path>
                        </svg>
                    </span>
               </button>`
            : '';
        const subtitle = buildTopSongSubtitle(track);
        const subtitleMarkup = subtitle
            ? `<div class="top-song-item__album">${escapeHtml(subtitle)}</div>`
            : '';
        const trackId = escapeHtml((track.id || '').toString());
        return `
            <div class="top-song-item" data-top-track-id="${trackId}" ${dataAttrs}>
                <div class="top-song-item__thumb">
                    ${cover}
                    ${playButton}
                </div>
                <div class="top-song-item__info">
                    <div class="top-song-item__title-row">
                        <div class="top-song-item__title">${escapeHtml(track.name || '')}</div>
                        <span class="top-song-item__library-badge" hidden>In Library</span>
                    </div>
                    ${subtitleMarkup}
                </div>
            </div>
        `;
            }).join('')}
        </div>
    `;

    markSpotifyTopTracksInLibrary(safeTracks.slice(0, 9), _artistProfile)
        .catch((error) => {
            console.warn('Failed to mark Spotify top tracks in library', error);
        });
    scheduleSpotifyTopTrackPreviewWarmup();
    syncLibraryTopSongsPlaybackFromSession();
}

async function markSpotifyTopTracksInLibrary(tracks, artistProfile = null) {
    const safeTracks = Array.isArray(tracks)
        ? tracks.filter(track => track && typeof track === 'object' && (track.id || track.name))
        : [];
    const list = document.getElementById('spotifyTopTracksList');
    if (!list || safeTracks.length === 0) {
        return;
    }

    const artistId = getCurrentLibraryArtistId();
    const requestId = ++spotifyTopTrackMatchRequestId;

    try {
        const localTrackIndex = await ensureLocalTopSongTrackIndexForArtistAsync(artistId);

        if (requestId !== spotifyTopTrackMatchRequestId) {
            return;
        }

        safeTracks.forEach((track, index) => {
            const trackId = (track.id || `top-track-${index}`).toString();
            const row = list.querySelector(`.top-song-item[data-top-track-id="${CSS.escape(trackId)}"]`);
            if (!row) {
                return;
            }

            const inLibrary = isSpotifyTopSongInLocalArtistIndex(track, localTrackIndex);
            row.classList.toggle('in-library', inLibrary);
            const badge = row.querySelector('.top-song-item__library-badge');
            if (badge) {
                badge.hidden = !inLibrary;
            }
        });
    } catch (error) {
        console.warn('Failed to mark top songs in library.', error);
    }
}

async function ensureLocalTopSongTrackIndexForArtistAsync(artistId) {
    const normalizedArtistId = String(artistId || '').trim();
    if (!normalizedArtistId) {
        return new Map();
    }

    if (libraryState.localTopSongTrackArtistId === normalizedArtistId && libraryState.localTopSongTrackIndex instanceof Map) {
        return libraryState.localTopSongTrackIndex;
    }

    if (libraryState.localTopSongTrackArtistId === normalizedArtistId && libraryState.localTopSongTrackIndexPromise) {
        return libraryState.localTopSongTrackIndexPromise;
    }

    libraryState.localTopSongTrackArtistId = normalizedArtistId;
    libraryState.localTopSongTrackIndex = null;
    libraryState.localTopSongTrackIndexPromise = (async () => {
        const index = new Map();
        const localAlbums = Array.isArray(libraryState.localAlbums) ? libraryState.localAlbums : [];
        const albumCandidates = localAlbums
            .filter(album => album && Number(album.id) > 0)
            .map(album => Number(album.id));

        for (const albumId of albumCandidates) {
            let tracks;
            try {
                tracks = await fetchJsonOptional(`/api/library/albums/${albumId}/tracks`);
            } catch {
                continue;
            }

            if (!Array.isArray(tracks) || tracks.length === 0) {
                continue;
            }

            tracks.forEach(track => {
                if (!toBoolish(track?.availableLocally)) {
                    return;
                }

                const titleKey = normalizeTopSongMatchTitle(track?.title || '');
                if (!titleKey) {
                    return;
                }

                const bucket = index.get(titleKey) || [];
                bucket.push({
                    durationMs: Number(track?.durationMs || 0) > 0 ? Number(track.durationMs) : null
                });
                index.set(titleKey, bucket);
            });
        }

        libraryState.localTopSongTrackIndex = index;
        return index;
    })();

    try {
        return await libraryState.localTopSongTrackIndexPromise;
    } finally {
        libraryState.localTopSongTrackIndexPromise = null;
    }
}

function isSpotifyTopSongInLocalArtistIndex(track, index) {
    if (!(index instanceof Map) || index.size === 0) {
        return false;
    }

    const titleKey = normalizeTopSongMatchTitle(track?.name || '');
    if (!titleKey) {
        return false;
    }

    const candidates = index.get(titleKey);
    if (!Array.isArray(candidates) || candidates.length === 0) {
        return false;
    }

    const targetDurationMs = Number(track?.durationMs || 0) > 0 ? Number(track.durationMs) : null;
    if (!targetDurationMs) {
        return true;
    }

    return candidates.some(candidate => {
        const candidateDurationMs = Number(candidate?.durationMs || 0) > 0 ? Number(candidate.durationMs) : null;
        if (!candidateDurationMs) {
            return true;
        }
        return Math.abs(candidateDurationMs - targetDurationMs) <= 2000;
    });
}

function normalizeTopSongMatchTitle(value) {
    return normalizeAlbumTitle(value || '');
}

function scheduleSpotifyTopTrackPreviewWarmup() {
    if (spotifyTopTrackPreviewWarmupTimer) {
        globalThis.clearTimeout(spotifyTopTrackPreviewWarmupTimer);
    }

    primeSpotifyTopTrackPlaybackContexts({ visibleFirst: true, limit: 24 });
    void primeSpotifyTrackPreviews({ visibleFirst: true, visibleFirstOnly: true, concurrency: 10, limit: 16 });
    spotifyTopTrackPreviewWarmupTimer = globalThis.setTimeout(() => {
        spotifyTopTrackPreviewWarmupTimer = 0;
        primeSpotifyTopTrackPlaybackContexts({ visibleFirst: true, limit: 64 });
        void primeSpotifyTrackPreviews({ visibleFirst: true, concurrency: 4 });
    }, 220);
}

function extractSpotifyTopTrackIdFromUrl(url) {
    const parsed = spotifyUrlHelpers.parseSpotifyUrl(url);
    if (parsed?.type !== 'track') {
        return '';
    }
    return String(parsed.id || '').trim();
}

function resetSpotifyTopTrackMatchMaps() {
    spotifyTopTrackMatchButtonsByIndex.clear();
    spotifyTopTrackMatchButtonsBySpotifyId.clear();
}

function applySpotifyTopTrackPlaylistMatches(matches) {
    if (!Array.isArray(matches) || matches.length === 0) {
        return;
    }

    matches.forEach((match) => {
        const deezerId = String(match?.deezerId || '').trim();
        const spotifyId = String(match?.spotifyId || '').trim();
        const status = String(match?.status || '').trim().toLowerCase();
        const index = Number.isFinite(Number(match?.index)) ? Number(match.index) : -1;
        const button = (spotifyId && spotifyTopTrackMatchButtonsBySpotifyId.get(spotifyId))
            || (index >= 0 ? spotifyTopTrackMatchButtonsByIndex.get(index) : null);
        if (!(button instanceof HTMLElement)) {
            return;
        }

        if (/^\d+$/.test(deezerId)) {
            button.dataset.deezerId = deezerId;
            button.dataset.mappingState = 'mapped';
            const playbackContext = globalThis.DeezerPlaybackContext;
            if (playbackContext && typeof playbackContext.fetchContext === 'function') {
                playbackContext.fetchContext(deezerId, {
                    cache: libraryTopTrackDeezerPlaybackContextCache,
                    requests: libraryTopTrackDeezerPlaybackContextRequests
                }).then((context) => {
                    if (context && typeof playbackContext.applyContextToElement === 'function') {
                        playbackContext.applyContextToElement(button, context);
                        button.dataset.mappingState = 'context-ready';
                    }
                }).catch(() => {
                    // Best-effort context warmup only.
                });
            }
            return;
        }

        if (status === 'unmatched_final' || status === 'hard_mismatch') {
            button.dataset.mappingState = 'unmapped';
        }
    });
}

async function pollSpotifyTopTrackPlaylistMatches(token) {
    if (!token) {
        return;
    }
    try {
        const payload = await fetchJsonOptional(`/api/spotify/tracklist/matches?token=${encodeURIComponent(token)}`);
        if (payload?.available !== true) {
            return;
        }
        applySpotifyTopTrackPlaylistMatches(payload.matches);
        const pending = Number(payload.pending || 0);
        if (pending <= 0 && token === spotifyTopTrackMatchToken) {
            if (spotifyTopTrackMatchPollTimer) {
                globalThis.clearInterval(spotifyTopTrackMatchPollTimer);
                spotifyTopTrackMatchPollTimer = 0;
            }
        }
    } catch {
        // Best-effort polling only.
    }
}

async function startSpotifyTopTrackPlaylistStyleMatching(pendingQueue) {
    if (!Array.isArray(pendingQueue) || pendingQueue.length === 0) {
        return;
    }
    if (spotifyTopTrackMatchStartPromise) {
        await spotifyTopTrackMatchStartPromise;
        return;
    }

    const startPromise = (async () => {
        resetSpotifyTopTrackMatchMaps();
        const tracks = [];
        const touchedButtons = [];
        for (let index = 0; index < pendingQueue.length; index += 1) {
            const current = pendingQueue[index];
            const request = buildSpotifyTrackResolveRequestFromButton(current.el) || { link: current.url };
            const link = normalizeSpotifyTrackSourceUrl(String(request?.link || current.url || '').trim());
            if (!link) {
                continue;
            }

            current.el.dataset.mappingState = 'mapping';
            touchedButtons.push(current.el);
            spotifyTopTrackMatchButtonsByIndex.set(index, current.el);
            const spotifyId = extractSpotifyTopTrackIdFromUrl(link);
            if (spotifyId && !spotifyTopTrackMatchButtonsBySpotifyId.has(spotifyId)) {
                spotifyTopTrackMatchButtonsBySpotifyId.set(spotifyId, current.el);
            }

            tracks.push(spotifyUrlHelpers.buildSpotifyTrackMatchPayload(link, request));
        }

        if (tracks.length === 0) {
            return;
        }

        try {
            const payload = await fetchJson('/api/spotify/tracklist/section/match', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    sectionKey: `library-top-tracks:${getCurrentLibraryArtistId() || 'unknown'}`,
                    tracks
                })
            });
            if (payload?.available !== true) {
                touchedButtons.forEach((button) => {
                    if (!button.dataset.deezerId) {
                        button.dataset.mappingState = 'unmapped';
                    }
                });
                return;
            }

            applySpotifyTopTrackPlaylistMatches(payload.matches);
            const token = String(payload?.matching?.token || '').trim();
            const pendingCount = Number(payload?.matching?.pending || 0);
            if (!token || pendingCount <= 0) {
                return;
            }

            spotifyTopTrackMatchToken = token;
            if (spotifyTopTrackMatchPollTimer) {
                globalThis.clearInterval(spotifyTopTrackMatchPollTimer);
                spotifyTopTrackMatchPollTimer = 0;
            }

            await pollSpotifyTopTrackPlaylistMatches(token);
            spotifyTopTrackMatchPollTimer = globalThis.setInterval(() => {
                void pollSpotifyTopTrackPlaylistMatches(token);
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

    spotifyTopTrackMatchStartPromise = startPromise;
    try {
        await startPromise;
    } finally {
        spotifyTopTrackMatchStartPromise = null;
    }
}

function buildTopSongSubtitle(track) {
    const parts = [];
    const albumName = (track?.albumName ?? '').toString().trim();
    const releaseTag = getTopSongReleaseTypeLabel(track);
    if (albumName && releaseTag) {
        parts.push(`${albumName} - ${releaseTag}`);
    } else if (albumName) {
        parts.push(albumName);
    } else if (releaseTag) {
        parts.push(releaseTag);
    }
    const year = resolveTopSongReleaseYear(track);
    if (year) {
        parts.push(year);
    }
    return parts.join(' • ');
}

function getTopSongReleaseTypeLabel(track) {
    const normalizedType = (track?.releaseType || '').toString().trim().toUpperCase();
    if (normalizedType === 'SINGLE') {
        return 'Single';
    }
    if (normalizedType === 'EP') {
        return 'EP';
    }
    if (normalizedType === 'COMPILATION') {
        return 'Compilation';
    }
    if (normalizedType === 'ALBUM') {
        return 'Album';
    }

    const normalizedGroup = (track?.albumGroup || '').toString().trim().toLowerCase();
    if (normalizedGroup === 'single') {
        return 'Single';
    }
    if (normalizedGroup === 'ep') {
        return 'EP';
    }
    if (normalizedGroup === 'compilation') {
        return 'Compilation';
    }
    if (normalizedGroup === 'album') {
        return 'Album';
    }

    const totalTracks = Number(track?.albumTrackTotal || 0);
    if (Number.isFinite(totalTracks) && totalTracks === 1) {
        return 'Single';
    }

    return 'Album';
}

function resolveTopSongReleaseYear(track) {
    const direct = extractReleaseYear(track?.releaseDate);
    if (direct) {
        return direct;
    }

    const albumName = (track?.albumName ?? '').toString().trim();
    if (!albumName) {
        return '';
    }

    const normalizedAlbum = normalizeAlbumTitle(albumName);
    if (!normalizedAlbum) {
        return '';
    }

    const match = (libraryState.spotifyAlbums || []).find((album) => {
        const candidate = normalizeAlbumTitle(album?.name || album?.title || '');
        return candidate && candidate === normalizedAlbum;
    });

    return extractReleaseYear(match?.releaseDate);
}

function hydrateTopTracksWithAlbumDates(tracks, albums) {
    if (!Array.isArray(tracks) || tracks.length === 0) {
        return [];
    }

    const byId = new Map();
    const byTitle = new Map();
    (albums || []).forEach(album => {
        const date = album?.releaseDate || '';
        if (!date) {
            return;
        }
        const id = (album?.id || '').toString().trim();
        if (id) {
            byId.set(id, date);
        }
        const titleKey = normalizeAlbumTitle(album?.name || album?.title || '');
        if (titleKey) {
            byTitle.set(titleKey, date);
        }
    });

    return tracks.map(track => {
        if (extractReleaseYear(track?.releaseDate)) {
            return track;
        }

        const albumId = (track?.albumId || '').toString().trim();
        const albumTitleKey = normalizeAlbumTitle(track?.albumName || '');
        const fallbackDate = (albumId && byId.get(albumId))
            || (albumTitleKey && byTitle.get(albumTitleKey))
            || '';

        if (!fallbackDate) {
            return track;
        }

        return {
            ...track,
            releaseDate: fallbackDate
        };
    });
}

function extractReleaseYear(value) {
    if (!value) {
        return '';
    }
    const text = value.toString();
    const match = text.match(/^(\d{4})/);
    if (match) {
        return match[1];
    }
    const parsed = new Date(text);
    if (!Number.isNaN(parsed.getTime())) {
        return String(parsed.getFullYear());
    }
    return '';
}

function normalizeDiscographyCategory(albumGroup, releaseType, totalTracks) {
    const normalizedGroup = (albumGroup || '').toString().trim().toLowerCase();
    if (normalizedGroup === 'single') {
        return 'single';
    }
    if (normalizedGroup === 'ep') {
        return 'ep';
    }
    if (normalizedGroup === 'album' || normalizedGroup === 'compilation') {
        return 'album';
    }

    const normalizedType = (releaseType || '').toString().trim().toUpperCase();
    if (normalizedType === 'SINGLE') {
        return 'single';
    }
    if (normalizedType === 'EP') {
        return 'ep';
    }
    if (normalizedType === 'ALBUM' || normalizedType === 'COMPILATION') {
        return 'album';
    }

    return 'album';
}

function normalizeDiscographySection(section, albumGroup, releaseType, totalTracks) {
    const normalizedSection = (section || '').toString().trim().toLowerCase();
    if (normalizedSection === 'popular' || normalizedSection === 'albums' || normalizedSection === 'singles_eps') {
        return normalizedSection;
    }

    const category = normalizeDiscographyCategory(albumGroup, releaseType, totalTracks);
    if (category === 'single' || category === 'ep') {
        return 'singles_eps';
    }
    return 'albums';
}

function getReleaseTagLabel(albumGroup, releaseType, totalTracks) {
    const category = normalizeDiscographyCategory(albumGroup, releaseType, totalTracks);
    if (category === 'single') {
        return 'Single';
    }
    if (category === 'ep') {
        return 'EP';
    }
    return '';
}

function primeSpotifyTopTrackPlaybackContexts(options = {}) {
    if (!globalThis.DeezerPlaybackContext || typeof globalThis.DeezerPlaybackContext.primePlaybackTargets !== 'function') {
        return;
    }

    const requestedLimit = Number(options?.limit || 0);
    const limit = Number.isFinite(requestedLimit) && requestedLimit > 0
        ? Math.max(1, Math.trunc(requestedLimit))
        : 24;
    const visibleFirst = options?.visibleFirst !== false;

    const buttons = Array.from(
        document.querySelectorAll('#spotifyTopTracksList .top-song-item__play.track-play[data-deezer-id]')
    ).filter((button) => button instanceof HTMLElement);
    if (!buttons.length) {
        return;
    }

    if (visibleFirst) {
        buttons.sort((left, right) => Number(isSpotifyTopTrackButtonVisible(right)) - Number(isSpotifyTopTrackButtonVisible(left)));
    }

    const targets = buttons.slice(0, limit);
    globalThis.DeezerPlaybackContext.primePlaybackTargets(targets, {
        includeSession: true,
        cache: libraryTopTrackDeezerPlaybackContextCache,
        requests: libraryTopTrackDeezerPlaybackContextRequests
    }).catch(() => {
        // Best-effort warmup only.
    });
}

async function processSpotifyPreviewQueueEntry(current) {
    try {
        current.el.dataset.mappingState = 'mapping';
        const resolved = await resolveSpotifyUrlToDeezer(
            buildSpotifyTrackResolveRequestFromButton(current.el)
        );
        if (resolved?.type === 'track' && resolved?.deezerId) {
            const deezerId = resolved.deezerId.toString();
            current.el.dataset.deezerId = deezerId;
            current.el.dataset.mappingState = 'mapped';
            const playbackContext = globalThis.DeezerPlaybackContext;
            if (playbackContext && typeof playbackContext.fetchContext === 'function') {
                const context = await playbackContext.fetchContext(deezerId, {
                    cache: libraryTopTrackDeezerPlaybackContextCache,
                    requests: libraryTopTrackDeezerPlaybackContextRequests
                });
                if (context && typeof playbackContext.applyContextToElement === 'function') {
                    playbackContext.applyContextToElement(current.el, context);
                    current.el.dataset.mappingState = 'context-ready';
                }
            }
        } else {
            current.el.dataset.mappingState = 'unmapped';
        }
    } catch {
        current.el.dataset.mappingState = 'unmapped';
        // Best-effort prefetch; playback will still resolve on demand.
    }
}

function buildSpotifyPreviewPendingQueue(list, options = {}) {
    const limit = Number(options?.limit || 0);
    const visibleFirst = options?.visibleFirst !== false;
    const visibleFirstOnly = options?.visibleFirstOnly === true;
    const playButtons = Array.from(list.querySelectorAll('button.track-play[data-spotify-url]'));
    const elements = playButtons.length > 0
        ? playButtons
        : Array.from(list.querySelectorAll('[data-spotify-url]'));
    if (elements.length === 0) {
        return [];
    }

    const queue = elements
        .map(el => ({
            el,
            url: normalizeSpotifyTrackSourceUrl(String(el.dataset.spotifyUrl || '').trim()),
            isVisible: isSpotifyTopTrackButtonVisible(el)
        }))
        .filter(entry => entry.url && !entry.el.dataset.deezerId && entry.el.dataset.mappingState !== 'mapping');

    if (visibleFirst) {
        queue.sort((left, right) => Number(right.isVisible) - Number(left.isVisible));
    }

    let pendingQueue = queue;
    if (visibleFirstOnly) {
        pendingQueue = pendingQueue.filter(entry => entry.isVisible);
    }
    const visibleCount = pendingQueue.reduce((count, entry) => count + (entry.isVisible ? 1 : 0), 0);
    const adaptiveVisibleLimit = Math.max(16, Math.min(24, visibleCount + 4));
    let effectiveLimit = 0;
    if (visibleFirstOnly) {
        effectiveLimit = limit > 0 ? Math.max(Math.trunc(limit), adaptiveVisibleLimit) : adaptiveVisibleLimit;
    } else {
        effectiveLimit = limit > 0 ? Math.max(1, Math.trunc(limit)) : 0;
    }
    if (effectiveLimit > 0) {
        pendingQueue = pendingQueue.slice(0, effectiveLimit);
    }
    return pendingQueue;
}

async function primeSpotifyTrackPreviews(options = {}) {
    const list = document.getElementById('spotifyTopTracksList');
    if (!list) {
        return;
    }
    const pendingQueue = buildSpotifyPreviewPendingQueue(list, options);
    if (pendingQueue.length === 0) {
        return;
    }

    await startSpotifyTopTrackPlaylistStyleMatching(pendingQueue);
}

function isSpotifyTopTrackButtonVisible(button) {
    if (!(button instanceof HTMLElement)) {
        return false;
    }
    const rect = button.getBoundingClientRect();
    const viewportHeight = globalThis.innerHeight || document.documentElement.clientHeight || 0;
    return rect.bottom >= -120 && rect.top <= viewportHeight + 200;
}

function buildSpotifyTrackResolveRequestFromButton(button) {
    if (!button?.dataset) {
        return null;
    }
    const link = normalizeSpotifyTrackSourceUrl(String(button.dataset.spotifyUrl || '').trim());
    if (!link) {
        return null;
    }
    const durationRaw = Number.parseInt(String(button.dataset.trackDurationMs || '0'), 10);
    return {
        link,
        title: String(button.dataset.trackTitle || '').trim(),
        artist: String(button.dataset.trackArtist || '').trim(),
        album: String(button.dataset.trackAlbum || '').trim(),
        isrc: String(button.dataset.trackIsrc || '').trim(),
        durationMs: Number.isFinite(durationRaw) && durationRaw > 0 ? durationRaw : 0
    };
}

function renderSpotifyAppearsOn(appearsOn) {
    const appearsPanel = document.getElementById('spotifyAppearsPanel');
    if (appearsPanel) {
        renderSpotifyAlbumPanel(appearsPanel, 'spotifyAppearsPanel', 'Appears On', appearsOn);
    }
}

function renderSpotifyAlbumPanel(parent, panelId, title, albums) {
    const safeAlbums = Array.isArray(albums)
        ? albums.filter(album => album && typeof album === 'object')
        : [];
    if (!safeAlbums.length) {
        return;
    }

    let panel = document.getElementById(panelId);
    if (!panel) {
        panel = document.createElement('div');
        panel.className = 'artist-neo-panel';
        panel.id = panelId;
        const panelHeader = document.createElement('div');
        panelHeader.className = 'panel-header';
        const heading = document.createElement('h2');
        heading.textContent = title;
        panelHeader.appendChild(heading);
        const albumGrid = document.createElement('div');
        albumGrid.className = 'album-grid neo-grid';
        panel.append(panelHeader, albumGrid);
        parent.appendChild(panel);
    }
    const panelTitle = panel.querySelector('h2');
    if (panelTitle) {
        panelTitle.textContent = title;
    }

    const grid = panel.querySelector('.album-grid');
    if (!grid) {
        return;
    }

    grid.innerHTML = safeAlbums.map(album => {
        const cover = toSafeHttpUrl(selectImage(album.images, 'small'));
        const coverMarkup = cover
            ? `<img src="${escapeHtml(cover)}" alt="${escapeHtml(album.name || '')}" loading="lazy" decoding="async" />`
            : '<div class="artist-initials">AL</div>';
        const titleMarkup = `<div class="album-title"><span class="artist-marquee">${escapeHtml(album.name || '')}</span></div>`;
        const year = album.releaseDate ? new Date(album.releaseDate).getFullYear() : '';
        const subtitleText = year ? `${year}` : '';
        const subtitleMarkup = subtitleText ? `<div class="album-subtitle">${escapeHtml(subtitleText)}</div>` : '';
        const safeTitle = escapeHtml(album.name || '');
        const safeSpotifyUrl = toSafeHttpUrl(album.sourceUrl || '');
        return `
            <div class="album-card ds-tile"${safeSpotifyUrl ? ` data-spotify-url="${escapeHtml(safeSpotifyUrl)}" data-spotify-title="${safeTitle}"` : ''}>
                ${coverMarkup}
                ${titleMarkup}
                ${subtitleMarkup}
            </div>
        `;
    }).join('');
}

function renderSpotifyRecentReleases(albums) {
    const latestContainer = document.getElementById('latestReleaseCard');
    const safeAlbums = Array.isArray(albums)
        ? albums.filter(album => album && typeof album === 'object')
        : [];
    if (!latestContainer || !safeAlbums.length) {
        return;
    }

    const sorted = [...safeAlbums].sort((a, b) => {
        const aDate = a.releaseDate || '';
        const bDate = b.releaseDate || '';
        return bDate.localeCompare(aDate);
    });

    const latest = sorted[0];
    if (!latest) return;
    const trackCount = Number.isFinite(Number(latest.totalTracks))
        ? Math.max(0, Number(latest.totalTracks))
        : 0;

    const cover = toSafeHttpUrl(selectImage(latest.images, 'medium'));
    const coverMarkup = cover
        ? `<img src="${escapeHtml(cover)}" alt="${escapeHtml(latest.name || '')}" loading="lazy" decoding="async" />`
        : '<div class="artist-initials">AL</div>';

    const formattedDate = formatSpotifyReleaseDate(latest.releaseDate);
    const typeLabel = getSpotifyReleaseTypeLabel(latest.albumGroup);
    applyLatestReleaseInteractivity(latestContainer, latest);

    latestContainer.innerHTML = `
        <div class="latest-release__artwork">${coverMarkup}</div>
        <div class="latest-release__meta">
            ${formattedDate ? `<div class="latest-release__date">${escapeHtml(formattedDate)}</div>` : ''}
            <div class="latest-release__type">${escapeHtml(typeLabel)}</div>
            <div class="latest-release__title">${escapeHtml(latest.name || '')}</div>
            <div class="latest-release__subtitle">${trackCount} ${trackCount === 1 ? 'song' : 'songs'}</div>
        </div>
    `;
}

function formatSpotifyReleaseDate(releaseDate) {
    if (!releaseDate) {
        return '';
    }
    const date = new Date(releaseDate);
    if (Number.isNaN(date.getTime())) {
        return releaseDate;
    }
    return date.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' }).toUpperCase();
}

function getSpotifyReleaseTypeLabel(albumGroup) {
    const group = String(albumGroup || '').toLowerCase();
    if (group === 'ep') {
        return 'EP';
    }
    if (group === 'single') {
        return 'Single';
    }
    if (group === 'compilation') {
        return 'Compilation';
    }
    return 'Album';
}

function applyLatestReleaseInteractivity(container, album) {
    const safeSpotifyUrl = toSafeHttpUrl(album?.sourceUrl || '');
    if (safeSpotifyUrl) {
        container.dataset.spotifyUrl = safeSpotifyUrl;
        container.dataset.spotifyTitle = album.name || '';
        container.classList.add('latest-release--interactive');
        return;
    }
    delete container.dataset.spotifyUrl;
    delete container.dataset.spotifyTitle;
    container.classList.remove('latest-release--interactive');
}

function selectImage(images, sizeHint) {
    if (!Array.isArray(images) || images.length === 0) {
        return null;
    }

    return images[0]?.url || null;
}

function formatCompactNumber(value) {
    if (!Number.isFinite(value)) {
        return '—';
    }
    return Intl.NumberFormat(undefined, {
        notation: 'compact',
        maximumFractionDigits: 1
    }).format(value);
}

function getInitials(name) {
    if (name === null || name === undefined) {
        return 'AL';
    }
    const text = String(name).trim();
    if (!text) {
        return 'AL';
    }
    return text
        .split(' ')
        .filter(Boolean)
        .slice(0, 2)
        .map(part => part[0].toUpperCase())
        .join('');
}

function resolveArtistIdFromPath(pathname) {
    const pathText = String(pathname || '');
    const artistMatch = /\/Library\/Artist\/(\d+)/i.exec(pathText);
    if (artistMatch?.[1]) {
        return artistMatch[1];
    }
    const albumMatch = /\/Library\/Albums\/(\d+)/i.exec(pathText);
    return albumMatch?.[1] || '';
}

async function initSpotifyIdEditor() {
    const editButton = document.getElementById('spotifyIdEdit');
    const spotifyIdEl = document.getElementById('artistSpotifyId');
    const artistIdValue = document.querySelector('[data-artist-id]')?.dataset.artistId
        || resolveArtistIdFromPath(globalThis.location.pathname);
    if (!editButton || !spotifyIdEl || !artistIdValue) {
        return;
    }

    editButton.addEventListener('click', async () => {
        const currentText = spotifyIdEl.textContent?.trim() || '';
        const current = spotifyIdEl.dataset.spotifyId
            || (/^[A-Za-z0-9]{22}$/.test(currentText) ? currentText : '');
        const updated = await DeezSpoTag.ui.prompt('Spotify artist ID (22 chars)', {
            title: 'Update Spotify ID',
            value: current,
            placeholder: '22-character Spotify ID'
        });
        if (updated === null) {
            return;
        }
        const trimmed = updated.trim();
        if (!trimmed) {
            showToast('Spotify ID is required.', true);
            return;
        }
        if (!/^[A-Za-z0-9]{22}$/.test(trimmed)) {
            showToast('Spotify ID should be 22 letters/numbers.', true);
            return;
        }

        editButton.disabled = true;
        try {
            await fetchJson(`/api/library/artists/${artistIdValue}/spotify-id`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ spotifyId: trimmed })
            });
            spotifyIdEl.dataset.spotifyId = trimmed;
            spotifyIdEl.href = `https://open.spotify.com/artist/${trimmed}`;
            spotifyIdEl.style.display = 'inline-flex';
            if (!spotifyIdEl.dataset.defaultLabel) {
                spotifyIdEl.dataset.defaultLabel = spotifyIdEl.innerHTML;
            }
            if (spotifyIdEl.dataset.defaultLabel) {
                spotifyIdEl.innerHTML = spotifyIdEl.dataset.defaultLabel;
            }
            localStorage.removeItem(getArtistVisualStorageKey(artistIdValue));
            const avatarEl = document.getElementById('artistAvatar');
            if (avatarEl) {
                delete avatarEl.dataset.localAvatar;
            }
            await loadSpotifyArtist(artistIdValue, true, false);
            showToast('Spotify ID updated.');
        } catch (error) {
            showToast(`Spotify ID update failed: ${error.message}`, true);
        } finally {
            editButton.disabled = false;
        }
    });
}

function initAppleIdEditor(artistIdValue) {
    const editButton = document.getElementById('appleIdEdit');
    if (!editButton || !artistIdValue) return;

    editButton.addEventListener('click', async () => {
        const current = libraryState.appleExtras.storedAppleId || '';
        const updated = await DeezSpoTag.ui.prompt('Apple Music artist ID (numeric)', {
            title: 'Update Apple Music Artist ID',
            value: current,
            placeholder: 'e.g. 1234567890'
        });
        if (updated === null) return;
        const trimmed = updated.trim();
        if (!trimmed) {
            showToast('Apple Music artist ID is required.', true);
            return;
        }
        if (!/^\d+$/.test(trimmed)) {
            showToast('Apple Music artist ID should be numeric.', true);
            return;
        }

        editButton.disabled = true;
        try {
            await fetchJson(`/api/library/artists/${artistIdValue}/apple-id`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ appleId: trimmed })
            });
            libraryState.appleExtras.storedAppleId = trimmed;
            libraryState.appleExtras.appleArtistId = trimmed;
            // Re-fetch Apple content with the new ID
            libraryState.appleExtras.initialized = false;
            initAppleArtistExtras(libraryState.appleExtras.term, trimmed);
            // Refresh the visual picker with the correct artist image
            libraryState.artistVisuals.appleImages = [];
            libraryState.artistVisuals.externalTerm = '';
            loadAppleArtistVisuals(libraryState.appleExtras.term, artistIdValue, trimmed);
            showToast('Apple Music artist ID updated.');
        } catch (error) {
            showToast(`Apple ID update failed: ${error.message}`, true);
        } finally {
            editButton.disabled = false;
        }
    });
}

function getArtistVisualStorageKey(artistId) {
    return `artist-visuals:${artistId || 'unknown'}`;
}

function loadArtistVisualPrefs(artistId) {
    if (!artistId) {
        return null;
    }
    try {
        const raw = localStorage.getItem(getArtistVisualStorageKey(artistId));
        return raw ? JSON.parse(raw) : null;
    } catch {
        return null;
    }
}

function saveArtistVisualPrefs(artistId, prefs) {
    if (!artistId) {
        return;
    }
    localStorage.setItem(getArtistVisualStorageKey(artistId), JSON.stringify(prefs));
}

async function persistArtistVisualSelection(artistId, action, url, path) {
    if (!artistId || !action || !url) {
        return;
    }

    const payload = {
        artistId: Number(artistId)
    };

    if (action === 'avatar') {
        payload.avatarImagePath = path || null;
        payload.avatarVisualUrl = url;
    } else if (action === 'background') {
        payload.backgroundImagePath = path || null;
        payload.backgroundVisualUrl = url;
    } else {
        return;
    }

    try {
        const result = await fetchJson('/api/library/spotify-cache/visuals', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });

        const prefs = loadArtistVisualPrefs(artistId) || {};
        if (applyPersistedArtistVisualResult(artistId, result, prefs)) {
            bumpImageCacheKey();
            saveArtistVisualPrefs(artistId, prefs);
            applyStoredArtistVisuals(artistId);
            if (document.getElementById('artistsGrid')) {
                await applyLibraryViewFilter();
            }
        }
    } catch (error) {
        console.warn('Failed to persist artist visuals.', error);
    }
}

function findArtistEntryById(artistId) {
    const numericArtistId = Number(artistId);
    if (!Number.isFinite(numericArtistId) || numericArtistId <= 0) {
        return null;
    }

    return libraryState.allArtists.find(item => Number(item?.id) === numericArtistId) || null;
}

function applyPersistedArtistAvatarResult(artistEntry, result, prefs) {
    if (!result?.avatarPath) {
        return false;
    }

    prefs.avatarPath = result.avatarPath;
    prefs.avatarUrl = buildLibraryImageUrl(result.avatarPath, 320);
    libraryState.artistVisuals.preferredAvatarPath = result.avatarPath;
    if (artistEntry) {
        artistEntry.preferredImagePath = result.avatarPath;
    }
    return true;
}

function applyPersistedArtistBackgroundResult(artistEntry, result, prefs) {
    if (!result?.backgroundPath) {
        return false;
    }

    prefs.backgroundPath = result.backgroundPath;
    prefs.backgroundUrl = buildLibraryImageUrl(result.backgroundPath);
    libraryState.artistVisuals.preferredBackgroundPath = result.backgroundPath;
    if (artistEntry) {
        artistEntry.preferredBackgroundPath = result.backgroundPath;
    }
    return true;
}

function applyPersistedArtistVisualResult(artistId, result, prefs) {
    const artistEntry = findArtistEntryById(artistId);
    const avatarUpdated = applyPersistedArtistAvatarResult(artistEntry, result, prefs);
    const backgroundUpdated = applyPersistedArtistBackgroundResult(artistEntry, result, prefs);
    return avatarUpdated || backgroundUpdated;
}

function getSpotifySyncSelectionState(artistId) {
    const requestedAvatar = document.getElementById('sync-include-avatar')?.checked ?? true;
    const requestedBackground = document.getElementById('sync-include-background')?.checked ?? true;
    const includeBio = document.getElementById('sync-include-bio')?.checked ?? true;
    const prefs = loadArtistVisualPrefs(artistId) || {};
    const serverAvatarPath = (libraryState.artistVisuals?.preferredAvatarPath || '').toString().trim();
    const serverBackgroundPath = (libraryState.artistVisuals?.preferredBackgroundPath || '').toString().trim();
    const avatarImagePath = requestedAvatar
        ? resolveManagedArtistVisualPath(artistId, prefs.avatarPath, serverAvatarPath)
        : null;
    const backgroundImagePath = requestedBackground
        ? resolveManagedArtistVisualPath(artistId, prefs.backgroundPath, serverBackgroundPath)
        : null;
    const serverAvatarUrl = serverAvatarPath ? buildLibraryImageUrl(serverAvatarPath, 320) : '';
    const serverBackgroundUrl = serverBackgroundPath ? buildLibraryImageUrl(serverBackgroundPath) : '';
    let avatarVisualUrl = null;
    if (requestedAvatar) {
        const avatarPathUrl = avatarImagePath ? buildLibraryImageUrl(avatarImagePath, 320) : '';
        avatarVisualUrl = normalizeArtistVisualUrl(
            prefs.avatarUrl
            || avatarPathUrl
            || serverAvatarUrl
        );
    }

    let backgroundVisualUrl = null;
    if (requestedBackground) {
        const backgroundPathUrl = backgroundImagePath ? buildLibraryImageUrl(backgroundImagePath) : '';
        backgroundVisualUrl = normalizeArtistVisualUrl(
            prefs.backgroundUrl
            || backgroundPathUrl
            || serverBackgroundUrl
            || libraryState.artistVisuals.headerImageUrl
            || selectImage(libraryState.currentSpotifyArtist?.images, 'large')
        );
    }

    return {
        requestedAvatar,
        requestedBackground,
        includeBio,
        avatarImagePath,
        avatarVisualUrl: avatarVisualUrl || null,
        backgroundImagePath,
        backgroundVisualUrl: backgroundVisualUrl || null,
        biography: includeBio
            ? (libraryState.currentSpotifyArtist?.biography || '').toString().trim() || null
            : null
    };
}

function getSpotifySyncMissingItems(selection) {
    const missingItems = [];
    if (selection.requestedAvatar && !selection.avatarImagePath && !selection.avatarVisualUrl) {
        missingItems.push('avatar');
    }
    if (selection.requestedBackground && !selection.backgroundImagePath && !selection.backgroundVisualUrl) {
        missingItems.push('background art');
    }
    return missingItems;
}

function validateSpotifySyncSelection(selection, missingItems) {
    if (!selection.requestedAvatar && !selection.requestedBackground && !selection.includeBio) {
        showToast('No sync items selected. Sending a no-op push request.', false);
    }
    return true;
}

function buildSpotifySyncPayload(artistId, target, selection) {
    const includeAvatar = selection.requestedAvatar && (!!selection.avatarImagePath || !!selection.avatarVisualUrl);
    const includeBackground = selection.requestedBackground && (!!selection.backgroundImagePath || !!selection.backgroundVisualUrl);
    return {
        includeAvatar,
        includeBackground,
        includeBio: selection.includeBio,
        payload: {
            artistId: Number(artistId),
            includeAvatar,
            includeBackground,
            includeBio: selection.includeBio,
            avatarImagePath: selection.avatarImagePath || null,
            avatarVisualUrl: includeAvatar ? (selection.avatarVisualUrl || null) : null,
            backgroundImagePath: selection.backgroundImagePath || null,
            backgroundVisualUrl: includeBackground ? (selection.backgroundVisualUrl || null) : null,
            biography: selection.biography || null,
            target: target || 'plex'
        }
    };
}

function showSpotifySyncWarnings(result) {
    const warnings = Array.isArray(result?.warnings) ? result.warnings.filter(Boolean) : [];
    warnings.forEach((warning) => showToast(warning, !isSpotifySyncInfoWarning(warning)));
}

function isSpotifySyncInfoWarning(warning) {
    const value = String(warning || '').trim().toLowerCase();
    if (!value) {
        return true;
    }

    return value.includes('nothing to sync yet')
        || value.includes('not set in app visuals')
        || value.includes('background info is empty')
        || value.includes('pushing remaining items only')
        || value.includes('used remote source where possible');
}

function showSpotifySyncUpdateResult(result) {
    const updatedParts = [];
    if (result?.avatarUpdated) updatedParts.push('avatar');
    if (result?.backgroundUpdated) updatedParts.push('background art');
    if (result?.bioUpdated) updatedParts.push('background info');

    if (updatedParts.length > 0) {
        showToast(`Server updated: ${updatedParts.join(' + ')}.`, false);
    } else if (result?.noOp) {
        showToast('No sync changes were available yet. Update visuals/background info and push again when ready.', false);
    } else {
        showToast('Push incomplete — no items were updated on the media server.', true);
    }

    showSpotifySyncWarnings(result);
}

function normalizeArtistVisualUrl(url) {
    if (!url) {
        return '';
    }
    return String(url).replaceAll(/([?&])v=[^&]+/g, '$1').replace(/[?&]$/, '');
}

function isAppManagedArtistVisualPath(path, artistId) {
    const value = (path || '').toString().trim();
    const numericArtistId = Number(artistId);
    if (!value || !Number.isFinite(numericArtistId) || numericArtistId <= 0) {
        return false;
    }

    const normalized = value.replaceAll('\\', '/').toLowerCase();
    return normalized.includes(`/library-artist-images/spotify/artists/${numericArtistId}/`);
}

function resolveManagedArtistVisualPath(artistId, preferredPath, serverPath) {
    const localPreferred = (preferredPath || '').toString().trim();
    if (isAppManagedArtistVisualPath(localPreferred, artistId)) {
        return localPreferred;
    }

    const localServer = (serverPath || '').toString().trim();
    if (isAppManagedArtistVisualPath(localServer, artistId)) {
        return localServer;
    }

    return null;
}

function applyStoredArtistVisuals(artistId) {
    const prefs = loadArtistVisualPrefs(artistId) || {};
    const serverAvatarPath = (libraryState.artistVisuals.preferredAvatarPath || '').toString().trim();
    const serverBackgroundPath = (libraryState.artistVisuals.preferredBackgroundPath || '').toString().trim();

    const avatarUrl = prefs.avatarUrl
        || (prefs.avatarPath ? buildLibraryImageUrl(prefs.avatarPath, 320) : '')
        || (serverAvatarPath ? buildLibraryImageUrl(serverAvatarPath, 320) : '');
    const backgroundUrl = prefs.backgroundUrl
        || (prefs.backgroundPath ? buildLibraryImageUrl(prefs.backgroundPath) : '')
        || (serverBackgroundPath ? buildLibraryImageUrl(serverBackgroundPath) : '');

    if (!avatarUrl && !backgroundUrl) {
        return;
    }

    if (avatarUrl) {
        const avatarEl = document.getElementById('artistAvatar');
        if (avatarEl) {
            const src = appendCacheKey(avatarUrl);
            setArtistAvatarImageElement(avatarEl, src, 'Artist avatar', true);
        }
    }

    if (backgroundUrl) {
        const bgEl = document.querySelector('.artist-page');
        if (bgEl) {
            const savedUrl = appendCacheKey(backgroundUrl);
            const applyId = (libraryState.artistVisuals.backgroundApplyId || 0) + 1;
            libraryState.artistVisuals.backgroundApplyId = applyId;

            const applyBackground = (url) => {
                if (libraryState.artistVisuals.backgroundApplyId !== applyId) {
                    return;
                }
                applyArtistHeroBackgroundImage(url, true);
            };

            // Apply immediately so async profile refreshes do not overwrite the new image while it is loading.
            applyBackground(savedUrl);
            const probe = new Image();
            probe.onload = () => {
                applyBackground(savedUrl);
            };
            probe.onerror = () => {
                if (libraryState.artistVisuals.backgroundApplyId !== applyId) {
                    return;
                }
                const fallback = libraryState.artistVisuals.headerImageUrl
                    || selectImage(libraryState.currentSpotifyArtist?.images, 'large');
                if (fallback) {
                    applyArtistHeroBackgroundImage(fallback, false);
                }
                const hasSavedPath = !!((prefs.backgroundPath || '').toString().trim() || serverBackgroundPath);
                if (!hasSavedPath && prefs.backgroundUrl) {
                    const nextPrefs = loadArtistVisualPrefs(artistId) || {};
                    if (nextPrefs.backgroundUrl) {
                        delete nextPrefs.backgroundUrl;
                        saveArtistVisualPrefs(artistId, nextPrefs);
                    }
                }
            };
            probe.src = savedUrl;
        }
    }
}

function pickArtistVisualCandidates(items, term) {
    if (!Array.isArray(items) || items.length === 0) {
        return [];
    }
    const normalizedTerm = normalizeArtistName(term);
    if (!normalizedTerm) {
        return items.slice(0, 6);
    }

    const exact = items.filter(item => normalizeArtistName(item?.name) === normalizedTerm);
    if (exact.length > 0) {
        return exact.slice(0, 6);
    }
    const contains = items.filter(item => normalizeArtistName(item?.name).includes(normalizedTerm));
    if (contains.length > 0) {
        return contains.slice(0, 6);
    }
    return items.slice(0, 6);
}

async function loadAppleArtistVisuals(artistName, artistId, storedAppleId) {
    // If we have a stored Apple ID, fetch that specific artist directly — no guessing
    if (storedAppleId) {
        try {
            const data = await fetchJsonOptional(`/api/apple/artist?id=${encodeURIComponent(storedAppleId)}`);
            if (data?.image) {
                libraryState.artistVisuals.appleImages = [{
                    url: data.image,
                    label: `Apple Music • ${data.name || artistName}`,
                    source: 'apple'
                }];
                renderArtistVisualPicker(artistId);
                return;
            }
        } catch (error) {
            console.warn('Apple artist visuals by ID failed.', error);
        }
    }

    // Fallback: name search with library album cross-reference
    const term = (artistName || '').trim();
    if (!term) {
        return;
    }
    try {
        const payload = await fetchJsonOptional(`/api/apple/search?term=${encodeURIComponent(term)}&limit=10&types=artists`);
        const artists = Array.isArray(payload?.artists) ? payload.artists : [];
        let candidates = pickArtistVisualCandidates(artists, term);

        // When multiple candidates share the same name, rank by library album overlap
        const localTitles = buildLocalAlbumTitleSet();
        if (candidates.length > 1 && localTitles.size > 0) {
            const scored = await Promise.all(candidates.map(async c => {
                if (!c?.appleId) return { c, score: 0 };
                try {
                    const albumData = await fetchJsonOptional(`/api/apple/artist/albums?id=${encodeURIComponent(c.appleId)}&limit=50`);
                    let score = 0;
                    for (const album of (albumData?.albums || [])) {
                        const key = normalizeAlbumTitle(album?.name || '');
                        if (key && localTitles.has(key)) score++;
                    }
                    return { c, score };
                } catch {
                    return { c, score: 0 };
                }
            }));
            scored.sort((a, b) => b.score - a.score);
            candidates = scored.map(s => s.c);
        }

        const images = candidates
            .map(item => ({
                url: item?.image || '',
                label: item?.name ? `Apple Music • ${item.name}` : 'Apple Music',
                source: 'apple'
            }))
            .filter(item => item.url);
        libraryState.artistVisuals.appleImages = images;
        renderArtistVisualPicker(artistId);
    } catch (error) {
        console.warn('Apple artist visuals failed.', error);
    }
}

async function loadDeezerArtistVisuals(artistName, artistId) {
    const term = (artistName || '').trim();
    if (!term) {
        return;
    }
    try {
        const payload = await fetchJsonOptional(`/api/deezer/search/type?query=${encodeURIComponent(term)}&type=artist&limit=10`);
        const artists = Array.isArray(payload?.items) ? payload.items : [];
        const candidates = pickArtistVisualCandidates(artists, term);
        const images = candidates
            .map(item => {
                const url = item?.picture_xl || item?.picture_big || item?.picture_medium || item?.picture || item?.picture_small || '';
                return {
                    url,
                    label: item?.name ? `Deezer • ${item.name}` : 'Deezer',
                    source: 'deezer'
                };
            })
            .filter(item => item.url);
        libraryState.artistVisuals.deezerImages = images;
        renderArtistVisualPicker(artistId);
    } catch (error) {
        console.warn('Deezer artist visuals failed.', error);
    }
}

function loadExternalArtistVisuals(artistName, artistId, storedAppleId) {
    const term = (artistName || '').trim();
    if (!term) {
        return;
    }
    const normalized = normalizeArtistName(term);
    const visuals = libraryState.artistVisuals;
    if (visuals.externalTerm === normalized) {
        if (visuals.externalLoading) {
            return;
        }
        if (visuals.appleImages.length > 0 || visuals.deezerImages.length > 0) {
            renderArtistVisualPicker(artistId);
            return;
        }
    }

    visuals.externalTerm = normalized;
    visuals.externalLoading = true;
    visuals.appleImages = [];
    visuals.deezerImages = [];
    renderArtistVisualPicker(artistId);

    Promise.allSettled([
        loadAppleArtistVisuals(term, artistId, storedAppleId || null),
        loadDeezerArtistVisuals(term, artistId)
    ]).finally(() => {
        visuals.externalLoading = false;
    });
}

function renderArtistVisualPicker(artistId) {
    const grid = document.getElementById('artist-visuals-grid');
    if (!grid || !artistId) {
        return;
    }

    const cacheImages = Array.isArray(libraryState.artistVisuals.cacheImages)
        ? libraryState.artistVisuals.cacheImages
        : [];
    const spotifyImages = Array.isArray(libraryState.artistVisuals.spotifyImages)
        ? libraryState.artistVisuals.spotifyImages
        : [];
    const appleImages = Array.isArray(libraryState.artistVisuals.appleImages)
        ? libraryState.artistVisuals.appleImages
        : [];
    const deezerImages = Array.isArray(libraryState.artistVisuals.deezerImages)
        ? libraryState.artistVisuals.deezerImages
        : [];

    const items = [];
    cacheImages.forEach(image => {
        if (!image?.path) {
            return;
        }
        const rawUrl = `/api/library/image?path=${encodeURIComponent(image.path)}&size=640`;
        items.push({
            url: rawUrl,
            label: image.name ? `Spotify cache • ${image.name}` : 'Spotify cache',
            source: 'spotify-cache',
            path: image.path
        });
    });
    spotifyImages.forEach(item => {
        if (!item?.url) {
            return;
        }
        items.push({
            url: item.url,
            label: item.label || 'Spotify',
            source: item.source || 'spotify'
        });
    });
    appleImages.forEach(item => {
        if (!item?.url) {
            return;
        }
        items.push({
            url: item.url,
            label: item.label || 'Apple Music',
            source: item.source || 'apple'
        });
    });
    deezerImages.forEach(item => {
        if (!item?.url) {
            return;
        }
        items.push({
            url: item.url,
            label: item.label || 'Deezer',
            source: item.source || 'deezer'
        });
    });

    const seen = new Set();
    const deduped = items.filter(item => {
        const key = normalizeArtistVisualUrl(item.url);
        if (!key || seen.has(key)) {
            return false;
        }
        seen.add(key);
        return true;
    });

    if (deduped.length === 0) {
        grid.innerHTML = '<div class="empty-card">No visuals available yet.</div>';
        return;
    }

    grid.innerHTML = deduped.map(item => {
        const src = toSafeHttpUrl(appendCacheKey(item.url));
        const safeUrl = escapeHtml(item.url || '');
        const safePath = escapeHtml((item.path || '').toString());
        const safeSource = escapeHtml((item.source || '').toString());
        const safeLabel = escapeHtml(item.label || 'Artist visual');
        return `
            <div class="visual-tile" title="${safeLabel}">
                <img src="${escapeHtml(src)}" alt="${safeLabel}" loading="lazy" decoding="async" />
                <div class="visual-tile__actions">
                    <button class="action-btn action-btn-sm" type="button" data-visual-action="avatar" data-visual-url="${safeUrl}" data-visual-path="${safePath}" data-visual-source="${safeSource}">Set avatar</button>
                    <button class="action-btn action-btn-sm btn-secondary" type="button" data-visual-action="background" data-visual-url="${safeUrl}" data-visual-path="${safePath}" data-visual-source="${safeSource}">Set background</button>
                </div>
            </div>
        `;
    }).join('');

    grid.querySelectorAll('[data-visual-action]').forEach(button => {
        button.addEventListener('click', (event) => {
            event.stopPropagation();
            const action = button.dataset.visualAction;
            const url = button.dataset.visualUrl;
            const path = (button.dataset.visualPath || '').trim();
            if (!action || !url) {
                return;
            }
            const prefs = loadArtistVisualPrefs(artistId) || {};
            const normalized = normalizeArtistVisualUrl(url);
            if (action === 'avatar') {
                prefs.avatarUrl = normalized;
                if (path) {
                    prefs.avatarPath = path;
                } else {
                    delete prefs.avatarPath;
                }
            } else if (action === 'background') {
                prefs.backgroundUrl = normalized;
                if (path) {
                    prefs.backgroundPath = path;
                } else {
                    delete prefs.backgroundPath;
                }
            }
            saveArtistVisualPrefs(artistId, prefs);
            applyStoredArtistVisuals(artistId);
            void persistArtistVisualSelection(artistId, action, normalized, path);
        });
    });

    const resetButton = document.getElementById('artist-visuals-reset');
    if (resetButton && !resetButton.dataset.bound) {
        resetButton.dataset.bound = 'true';
        resetButton.addEventListener('click', () => {
            localStorage.removeItem(getArtistVisualStorageKey(artistId));
            const avatarEl = document.getElementById('artistAvatar');
            if (avatarEl) {
                delete avatarEl.dataset.localAvatar;
            }
            const bgEl = document.querySelector('.artist-page');
            if (bgEl) {
                libraryState.artistVisuals.backgroundApplyId = (libraryState.artistVisuals.backgroundApplyId || 0) + 1;
                delete bgEl.dataset.localBackground;
            }
            const artist = libraryState.currentSpotifyArtist;
            if (artist) {
                applySpotifyArtistProfile(artist);
            }
        });
    }
}

function renderAlbumCards(albums, options = {}) {
    const { linkToAlbum = false, linkToTracklist = false } = options;
    if (!albums.length) {
        const message = linkToTracklist ? 'All albums are available locally.' : 'No locally available albums yet.';
        return `<p class="text-muted">${message}</p>`;
    }
    return albums.map(album => {
        const folderPills = (album.localFolders || []).map(folder => `<span class="folder-pill">${escapeHtml(folder)}</span>`).join('');
        const showFolderPills = !document.querySelector('.artist-page');
        const folderMarkup = showFolderPills && folderPills ? `<div class="folder-pill-group">${folderPills}</div>` : '';
        const coverUrl = album.preferredCoverPath
            ? appendCacheKey(`/api/library/image?path=${encodeURIComponent(album.preferredCoverPath)}&size=240`)
            : (album.coverUrl || null);
        const safeCoverUrl = toSafeHttpUrl(coverUrl || '');
        const coverMarkup = safeCoverUrl
            ? `<img src="${escapeHtml(safeCoverUrl)}" alt="${escapeHtml(album.title || '')}" loading="lazy" decoding="async" />`
            : '<div class="artist-initials">AL</div>';
        let cardAttrs = '';
        if (linkToAlbum) {
            cardAttrs = ` data-album-id="${escapeHtml(String(album.id || ''))}"`;
        } else if (linkToTracklist) {
            cardAttrs = ` data-tracklist-id="${escapeHtml(String(album.id || ''))}"`;
        }
        const folderName = (album.localFolders || [])[0] || '';
        const year = album.releaseDate ? new Date(album.releaseDate).getFullYear() : '';
        const subtitleText = folderName || (year ? `${year}` : '');
        const subtitleMarkup = subtitleText ? `<div class="album-subtitle">${escapeHtml(subtitleText)}</div>` : '';
        return `
            <div class="album-card ds-tile"${cardAttrs}>
                ${coverMarkup}
                <div class="album-title"><span class="artist-marquee">${escapeHtml(album.title || '')}</span></div>
                ${subtitleMarkup}
                ${folderMarkup}
            </div>
        `;
    }).join('');
}

function scoreSpotifyDiscographyAlbum(album) {
    if (!album || typeof album !== 'object') {
        return -1;
    }

    let score = 0;
    if (album.isPopular || album.discographySection === 'popular') {
        score += 8;
    }
    if (album.releaseDate) {
        score += 4;
    }
    if (Number(album.totalTracks) > 0) {
        score += 2;
    }
    if (album.deezerId) {
        score += 2;
    }
    if (album.sourceUrl) {
        score += 1;
    }
    return score;
}

function pickPreferredSpotifyDiscographyAlbum(existing, candidate) {
    if (!existing) {
        return candidate;
    }
    if (!candidate) {
        return existing;
    }

    const existingScore = scoreSpotifyDiscographyAlbum(existing);
    const candidateScore = scoreSpotifyDiscographyAlbum(candidate);
    if (candidateScore > existingScore) {
        return candidate;
    }
    if (candidateScore < existingScore) {
        return existing;
    }

    const existingDate = (existing.releaseDate || '').toString();
    const candidateDate = (candidate.releaseDate || '').toString();
    return candidateDate.localeCompare(existingDate) > 0 ? candidate : existing;
}

function getDiscographyMergeKey(album) {
    const titleKey = normalizeAlbumTitle(album?.title || album?.name || '');
    if (titleKey) {
        return `title:${titleKey}`;
    }

    const id = (album?.deezerId || album?.id || '').toString().trim();
    if (id) {
        return `id:${id}`;
    }

    const sourceUrl = (album?.sourceUrl || '').toString().trim();
    return sourceUrl ? `url:${sourceUrl}` : '';
}

function isDiscographyAlbumMissing(album) {
    if (!album || typeof album !== 'object') {
        return true;
    }

    if (typeof album.missingForDownload === 'boolean') {
        return album.missingForDownload;
    }

    const inLibrary = toBoolish(album.inLibrary);
    const totalTrackCount = Number(album.totalTracks || 0);
    const localStereoTrackCount = Number(album.localStereoTrackCount || album.localTrackCount || 0);
    const isPartiallyDownloaded = inLibrary && totalTrackCount > 0 && localStereoTrackCount < totalTrackCount;
    return !inLibrary || isPartiallyDownloaded;
}

function matchesDiscographyAvailability(album, availabilityFilter) {
    if (availabilityFilter === 'in-library') {
        return toBoolish(album?.inLibrary);
    }
    if (availabilityFilter === 'missing') {
        return isDiscographyAlbumMissing(album);
    }
    return true;
}

function preserveDiscographyMissingEntries(nextDiscography) {
    const current = Array.isArray(nextDiscography) ? nextDiscography : [];
    const previous = Array.isArray(libraryState.discography) ? libraryState.discography : [];
    if (previous.length === 0 || current.length === 0) {
        return current;
    }

    const previousMissing = previous.filter(item => isDiscographyAlbumMissing(item));
    if (previousMissing.length === 0) {
        return current;
    }

    const seen = new Set(current.map(getDiscographyMergeKey).filter(Boolean));
    previousMissing.forEach(item => {
        const key = getDiscographyMergeKey(item);
        if (!key || seen.has(key)) {
            return;
        }
        seen.add(key);
        current.push({
            ...item,
            missingForDownload: true
        });
    });

    return current;
}

function buildDiscography(localAlbums, spotifyAlbums) {
    const merged = [];
    const seen = new Set();
    const spotifyByTitle = new Map();

    (spotifyAlbums || []).forEach(album => {
        const key = normalizeAlbumTitle(album.name || album.title);
        if (!key) {
            return;
        }
        const existing = spotifyByTitle.get(key);
        spotifyByTitle.set(key, pickPreferredSpotifyDiscographyAlbum(existing, album));
    });

    (localAlbums || []).forEach(album => {
        const key = normalizeAlbumTitle(album.title || album.name);
        if (key) {
            seen.add(key);
        }
        const spotifyMatch = key ? spotifyByTitle.get(key) : null;
        const spotifyCoverUrl = spotifyMatch
            ? ((spotifyMatch.coverUrl || selectImage(spotifyMatch.images, 'small') || '').toString().trim() || null)
            : null;
        const onDiskCoverUrl = album.preferredCoverPath
            ? appendCacheKey(`/api/library/image?path=${encodeURIComponent(album.preferredCoverPath)}&size=240`)
            : null;
        const albumGroup = spotifyMatch?.albumGroup || '';
        const releaseType = spotifyMatch?.releaseType || '';
        const totalTracks = album.totalTracks || spotifyMatch?.totalTracks || 0;
        const localTrackCount = Number(album.localTrackCount || album.localTracks || 0);
        const hasStereoVariant = toBoolish(album?.hasStereoVariant);
        const hasAtmosVariant = toBoolish(album?.hasAtmosVariant);
        const localStereoTrackCountRaw = Number(album.localStereoTrackCount || 0);
        const localAtmosTrackCountRaw = Number(album.localAtmosTrackCount || 0);
        const stereoFallbackCount = hasStereoVariant ? localTrackCount : 0;
        const atmosFallbackCount = hasAtmosVariant ? localTrackCount : 0;
        const localStereoTrackCount = localStereoTrackCountRaw > 0
            ? localStereoTrackCountRaw
            : stereoFallbackCount;
        const localAtmosTrackCount = localAtmosTrackCountRaw > 0
            ? localAtmosTrackCountRaw
            : atmosFallbackCount;
        const isStereoPartiallyDownloaded = hasStereoVariant
            && totalTracks > 0
            && localStereoTrackCount < totalTracks;
        const isStereoComplete = hasStereoVariant
            && (!isStereoPartiallyDownloaded);
        const sourceUrl = (spotifyMatch?.sourceUrl || album.sourceUrl || '').toString().trim();
        const deezerAlbumId = (spotifyMatch?.deezerId || '').toString().trim() || null;
        const discographySection = normalizeDiscographySection(
            spotifyMatch?.discographySection,
            albumGroup,
            releaseType,
            totalTracks
        );
        const isPopular = Boolean(spotifyMatch?.isPopular) || (spotifyMatch?.discographySection === 'popular');
        const coverUrl = spotifyCoverUrl || (spotifyMatch ? null : onDiskCoverUrl);

        merged.push({
            id: album.id,
            title: album.title || album.name || '',
            name: album.title || album.name || '',
            releaseDate: album.releaseDate || spotifyMatch?.releaseDate || '',
            images: album.images || [],
            coverUrl,
            sourceUrl: sourceUrl,
            deezerId: deezerAlbumId,
            totalTracks,
            albumGroup,
            releaseType,
            discographySection,
            category: normalizeDiscographyCategory(albumGroup, releaseType, totalTracks),
            isPopular,
            // Discography represents stereo availability. Atmos-only local files must not block stereo downloads.
            inLibrary: hasStereoVariant,
            hasStereoVariant,
            hasAtmosVariant,
            stereoInLibrary: hasStereoVariant,
            atmosInLibrary: hasAtmosVariant,
            isStereoComplete,
            missingForDownload: !isStereoComplete,
            localId: album.id,
            localTrackCount: localStereoTrackCount,
            localStereoTrackCount,
            localAtmosTrackCount
        });
    });

    (spotifyAlbums || []).forEach(album => {
        const key = normalizeAlbumTitle(album.name || album.title);
        if (key && seen.has(key)) {
            return;
        }
        const localId = resolveLocalAlbumIdByTitle(album.name || album.title);
        if (localId) {
            if (key) {
                seen.add(key);
            }
            return;
        }
        if (key) {
            seen.add(key);
        }
        const discographySection = normalizeDiscographySection(album.discographySection, album.albumGroup, album.releaseType, album.totalTracks || 0);
        const isPopular = Boolean(album.isPopular) || (album.discographySection === 'popular');
        merged.push({
            id: album.id,
            title: album.name || album.title || '',
            name: album.name || album.title || '',
            releaseDate: album.releaseDate || '',
            images: album.images || [],
            coverUrl: null,
            sourceUrl: album.sourceUrl || '',
            deezerId: album.deezerId || null,
            totalTracks: album.totalTracks || 0,
            albumGroup: album.albumGroup || '',
            releaseType: album.releaseType || '',
            discographySection,
            category: normalizeDiscographyCategory(album.albumGroup, album.releaseType, album.totalTracks || 0),
            isPopular,
            inLibrary: false,
            hasStereoVariant: false,
            hasAtmosVariant: false,
            stereoInLibrary: false,
            atmosInLibrary: false,
            isStereoComplete: false,
            missingForDownload: true,
            localId: null,
            localTrackCount: 0,
            localStereoTrackCount: 0,
            localAtmosTrackCount: 0
        });
    });

    const stabilized = preserveDiscographyMissingEntries(merged);
    stabilized.sort((a, b) => {
        const aDate = a.releaseDate || '';
        const bDate = b.releaseDate || '';
        return bDate.localeCompare(aDate);
    });

    libraryState.discography = stabilized;
}

function getDiscographyTypeCounts(albums, availabilityFilter) {
    const counts = {
        popular: 0,
        albums: 0,
        'singles-eps': 0
    };

    (albums || []).forEach((album) => {
        if (!album) {
            return;
        }
        const matchesAvailability = matchesDiscographyAvailability(album, availabilityFilter);
        if (!matchesAvailability) {
            return;
        }

        const section = normalizeDiscographySection(album.discographySection, album.albumGroup, album.releaseType, album.totalTracks || 0);
        const isPopular = Boolean(album.isPopular) || section === 'popular';
        if (isPopular) {
            counts.popular += 1;
        }
        if (section === 'albums') {
            counts.albums += 1;
        }
        if (section === 'singles_eps') {
            counts['singles-eps'] += 1;
        }
    });

    return counts;
}

function resolveDiscographyTypeFilter(albums, currentTypeFilter, availabilityFilter) {
    const counts = getDiscographyTypeCounts(albums, availabilityFilter);
    if (!libraryState.discographyTypeTouched && counts.popular > 0) {
        return 'popular';
    }
    if (currentTypeFilter === 'all') {
        return 'all';
    }

    if ((currentTypeFilter === 'popular' && counts.popular > 0)
        || (currentTypeFilter === 'albums' && counts.albums > 0)
        || (currentTypeFilter === 'singles-eps' && counts['singles-eps'] > 0)) {
        return currentTypeFilter;
    }

    if (counts.popular > 0) {
        return 'popular';
    }
    if (counts.albums > 0) {
        return 'albums';
    }
    if (counts['singles-eps'] > 0) {
        return 'singles-eps';
    }

    return currentTypeFilter || 'popular';
}

function renderDiscography(filter) {
    if (typeof filter === 'string') {
        // Backward compatibility with previous single-filter calls.
        libraryState.discographyTypeFilter = filter;
    }
    const grid = document.getElementById('discographyGrid');
    if (!grid) {
        return;
    }

    const availabilityFilter = libraryState.discographyAvailabilityFilter || 'all';
    const albums = libraryState.discography || [];
    const typeFilter = resolveDiscographyTypeFilter(albums, libraryState.discographyTypeFilter || 'popular', availabilityFilter);
    libraryState.discographyTypeFilter = typeFilter;
    const filtered = albums.filter((album) => {
        const section = normalizeDiscographySection(album.discographySection, album.albumGroup, album.releaseType, album.totalTracks || 0);
        const isPopular = Boolean(album.isPopular) || section === 'popular';
        const matchesType = typeFilter === 'all'
            || (typeFilter === 'popular' && isPopular)
            || (typeFilter === 'albums' && section === 'albums')
            || (typeFilter === 'singles-eps' && section === 'singles_eps');
        const matchesAvailability = matchesDiscographyAvailability(album, availabilityFilter);
        return matchesType && matchesAvailability;
    });

    if (!filtered.length) {
        const msg = (typeFilter === 'all' || typeFilter === 'popular') && availabilityFilter === 'all'
            ? 'No releases found yet.'
            : 'No releases match the selected filters.';
        grid.innerHTML = `<div class="empty-card">${msg}</div>`;
        return;
    }

    grid.innerHTML = filtered
        .map((album) => renderDiscographyAlbumCard(album, availabilityFilter))
        .join('');

    grid.querySelectorAll('[data-album-id]').forEach(card => {
        card.addEventListener('click', () => {
            const albumId = card.dataset.albumId;
            if (!albumId) {
                return;
            }
            globalThis.location.href = buildLibraryScopedUrl(`/Library/Album/${encodeURIComponent(albumId)}`);
        });
    });

    grid.querySelectorAll('[data-tracklist-id]').forEach(card => {
        if (card.dataset.albumId !== undefined) {
            return;
        }
        card.addEventListener('click', () => {
            const id = card.dataset.tracklistId;
            if (id) {
                const params = new URLSearchParams({ id, type: 'album' });
                globalThis.location.href = `/Tracklist?${params.toString()}`;
            }
        });
    });

    const filterContainer = document.getElementById('discographyFilters');
    if (filterContainer) {
        filterContainer.querySelectorAll('.filter-chip').forEach(chip => {
            const group = chip.dataset.filterGroup || 'type';
            const value = chip.dataset.filter || 'all';
            const isActive = group === 'availability'
                ? value === availabilityFilter
                : value === typeFilter;
            chip.classList.toggle('active', isActive);
        });
    }

    saveDiscographyFilterState();
}

function buildDiscographyCardDataAttrs(album, availabilityFilter, isPartiallyDownloaded) {
    const canNavigateToLocalAlbum = album.inLibrary
        && Boolean(album.localId)
        && (
            availabilityFilter === 'in-library'
            || !isPartiallyDownloaded
        );
    if (canNavigateToLocalAlbum) {
        return ` data-album-id="${escapeHtml(String(album.localId || ''))}"`;
    }
    if (album.deezerId) {
        return ` data-tracklist-id="${escapeHtml(String(album.deezerId || ''))}"`;
    }
    const safeSpotifyUrl = toSafeHttpUrl(album.sourceUrl || '');
    if (safeSpotifyUrl) {
        return ` data-spotify-url="${escapeHtml(safeSpotifyUrl)}"`;
    }
    return '';
}

function renderDiscographyAlbumCard(album, availabilityFilter) {
    const cover = toSafeHttpUrl(album.coverUrl || selectImage(album.images, 'small'));
    const coverMarkup = cover
        ? `<img src="${escapeHtml(cover)}" alt="${escapeHtml(album.title)}" loading="lazy" decoding="async" />`
        : '<div class="artist-initials">AL</div>';
    const localTrackCount = Number(album.localStereoTrackCount || album.localTrackCount || 0);
    const totalTrackCount = Number(album.totalTracks || 0);
    const isPartiallyDownloaded = album.inLibrary
        && totalTrackCount > 0
        && localTrackCount < totalTrackCount;
    let badgeMarkup = '';
    if (album.inLibrary) {
        if (isPartiallyDownloaded) {
            const partialCountLabel = `${localTrackCount}/${totalTrackCount}`;
            badgeMarkup = `<div class="library-badge library-badge--partial" title="Partially downloaded: ${partialCountLabel} tracks">${escapeHtml(partialCountLabel)}</div>`;
        } else {
            badgeMarkup = '<div class="library-badge"><svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><path d="M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41L9 16.17z"/></svg></div>';
        }
    }
    const titleMarkup = `<div class="album-title"><span class="artist-marquee">${escapeHtml(album.title)}</span></div>`;
    const year = album.releaseDate ? new Date(album.releaseDate).getFullYear() : '';
    const releaseLabel = getReleaseTagLabel(album.albumGroup, album.releaseType, album.totalTracks);
    const subtitleParts = [releaseLabel, year ? String(year) : ''].filter(Boolean);
    const subtitleMarkup = subtitleParts.length ? `<div class="album-subtitle">${escapeHtml(subtitleParts.join(' • '))}</div>` : '';
    const dataAttrs = buildDiscographyCardDataAttrs(album, availabilityFilter, isPartiallyDownloaded);
    return `
        <div class="album-card ds-tile${album.inLibrary ? ' in-library' : ''}"${dataAttrs}>
            <div class="album-card__artwork">
                ${coverMarkup}
                ${badgeMarkup}
            </div>
            ${titleMarkup}
            ${subtitleMarkup}
        </div>
    `;
}

function tryRenderDiscography() {
    if (libraryState.artistDataReady.local) {
        buildDiscography(libraryState.localAlbums, libraryState.spotifyAlbums || []);
        renderDiscography();
        renderAppleAtmos();
    }
}

function initDiscographyFilters() {
    const container = document.getElementById('discographyFilters');
    if (!container || container.dataset.bound) {
        return;
    }
    container.dataset.bound = 'true';
    container.addEventListener('click', (e) => {
        const chip = e.target.closest('.filter-chip');
        if (!chip) {
            return;
        }
        const group = chip.dataset.filterGroup || 'type';
        const value = chip.dataset.filter || 'all';
        if (group === 'availability') {
            libraryState.discographyAvailabilityFilter = value;
        } else {
            libraryState.discographyTypeFilter = value;
            libraryState.discographyTypeTouched = true;
        }
        renderDiscography();
        renderAppleAtmos();
    });
}

function initAppleLazyLoad(artistName, storedAppleId) {
    const sections = document.querySelectorAll('[data-lazy-apple]');
    if (!sections.length) {
        return;
    }
    const observer = new IntersectionObserver((entries) => {
        entries.forEach(entry => {
            if (entry.isIntersecting) {
                observer.disconnect();
                initAppleArtistExtras(artistName, storedAppleId);
            }
        });
    }, { rootMargin: '200px' });
    sections.forEach(section => observer.observe(section));
}

function getTrackSourceForTrack(track) {
    if (!track) {
        return null;
    }

    if (track.deezerUrl) {
        return { service: 'deezer', url: track.deezerUrl, label: 'Deezer' };
    }
    if (track.deezerTrackId) {
        return { service: 'deezer', url: `https://www.deezer.com/track/${track.deezerTrackId}`, label: 'Deezer' };
    }
    if (track.spotifyUrl) {
        return { service: 'spotify', url: track.spotifyUrl, label: 'Spotify' };
    }
    if (track.spotifyTrackId) {
        return { service: 'spotify', url: `https://open.spotify.com/track/${track.spotifyTrackId}`, label: 'Spotify' };
    }
    if (track.appleUrl) {
        return { service: 'apple', url: track.appleUrl, label: 'Apple' };
    }
    return null;
}

async function queueAlbumDownloads(tracks) {
    if (!Array.isArray(tracks) || tracks.length === 0) {
        showToast('No downloadable tracks selected.', true);
        return;
    }

    const destinationRaw = document.getElementById('downloadDestinationAlbum')?.value ?? '';
    const destinationFolderId = destinationRaw ? Number(destinationRaw) : null;

    const { intents, linkedTrackCount } = collectAlbumQueueIntents(tracks);
    const counts = await requestAlbumQueueIntents(intents, destinationFolderId);
    const unsupported = Math.max(0, tracks.length - linkedTrackCount);
    const summary = buildAlbumQueueSummary(counts, unsupported);

    if (!summary) {
        showToast('No tracks were queued.', true);
        return;
    }
    showToast(summary);
}

function collectAlbumQueueIntents(tracks) {
    const intents = [];
    let linkedTrackCount = 0;
    tracks.forEach((track) => {
        const source = getTrackSourceForTrack(track);
        const built = buildQueueIntentsForTrack(track, source);
        if (!built.linked) {
            return;
        }
        linkedTrackCount += 1;
        intents.push(...built.intents);
    });
    return { intents, linkedTrackCount };
}

async function requestAlbumQueueIntents(intents, destinationFolderId) {
    if (!Array.isArray(intents) || intents.length === 0) {
        return { queuedCount: 0, deferredCount: 0, skippedCount: 0 };
    }
    const result = await fetchJson('/api/download/intent', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            resolveImmediately: true,
            destinationFolderId: Number.isFinite(destinationFolderId) ? destinationFolderId : null,
            intents
        })
    });
    return {
        queuedCount: Array.isArray(result?.queued) ? result.queued.length : 0,
        deferredCount: Number.isFinite(result?.deferredCount) ? result.deferredCount : 0,
        skippedCount: Number.isFinite(result?.skipped) ? result.skipped : 0
    };
}

function buildAlbumQueueSummary({ queuedCount, deferredCount, skippedCount }, unsupported) {
    return [
        queuedCount > 0 ? `Queued ${queuedCount}` : null,
        deferredCount > 0 ? `deferred ${deferredCount}` : null,
        skippedCount > 0 ? `skipped ${skippedCount}` : null,
        unsupported > 0 ? `unlinked ${unsupported}` : null
    ].filter(Boolean).join(' | ');
}

function buildAlbumTrackBaseIntent(track) {
    return {
        title: track?.title || '',
        artist: track?.artistName || track?.artist || '',
        album: track?.albumTitle || track?.album || '',
        albumArtist: track?.artistName || track?.artist || '',
        isrc: track?.isrc || '',
        cover: track?.coverUrl || track?.image || '',
        durationMs: Number(track?.durationMs || track?.duration || 0) || 0,
        position: Number(track?.trackNo || 0) || 0,
        allowQualityUpgrade: true
    };
}

function buildAppleQueueIntents(track, source, baseIntent) {
    const hasStereoVariant = track?.hasStereoVariant === true || track?.hasStereoVariant === 'true';
    const hasAtmosVariant = track?.hasAtmosVariant === true || track?.hasAtmosVariant === 'true';
    const hasAtmosCapability = track?.hasAtmos === true
        || track?.hasAtmos === 'true'
        || track?.appleHasAtmos === true
        || track?.appleHasAtmos === 'true'
        || hasAtmosVariant
        || String(track?.audioVariant || '').toLowerCase() === 'atmos';
    const intents = [];
    if (hasAtmosCapability && !hasAtmosVariant) {
        intents.push({
            sourceService: 'apple',
            sourceUrl: source.url,
            contentType: 'atmos',
            hasAtmos: true,
            ...baseIntent
        });
    }
    if (!hasStereoVariant) {
        intents.push({
            sourceService: 'apple',
            sourceUrl: source.url,
            contentType: 'stereo',
            hasAtmos: hasAtmosCapability,
            ...baseIntent
        });
    }
    return intents;
}

function buildQueueIntentsForTrack(track, source) {
    if (!source?.url) {
        return { linked: false, intents: [] };
    }
    const baseIntent = buildAlbumTrackBaseIntent(track);
    if (source.service === 'apple') {
        return { linked: true, intents: buildAppleQueueIntents(track, source, baseIntent) };
    }
    if (source.service === 'spotify') {
        const spotifyId = track?.spotifyTrackId
            || (source.url.match(/(?:spotify:track:|open\.spotify\.com\/track\/)([a-z0-9]+)/i) || [])[1]
            || '';
        return {
            linked: true,
            intents: [{
                sourceService: 'spotify',
                sourceUrl: source.url,
                spotifyId,
                deezerId: track?.deezerTrackId || track?.deezerId || undefined,
                ...baseIntent
            }]
        };
    }
    return {
        linked: true,
        intents: [{
            sourceService: source.service,
            sourceUrl: source.url,
            deezerId: track?.deezerTrackId || track?.deezerId || undefined,
            ...baseIntent
        }]
    };
}

async function loadAlbum(albumId) {
    const albumIdValue = albumId || document.querySelector('[data-album-id]')?.dataset.albumId;
    if (!albumIdValue) {
        return;
    }

    const [album, tracks] = await Promise.all([
        fetchJsonOptional(`/api/library/albums/${albumIdValue}`),
        fetchJson(`/api/library/albums/${albumIdValue}/tracks`)
    ]);

    if (!album) {
        showToast('Album not found. Refresh the library and try again.', true);
        return;
    }

    await populateAlbumHero(album);

    const trackContainer = document.getElementById('albumTracks');
    if (!trackContainer) {
        return;
    }

    libraryState.albumTracksById.clear();
    if (!tracks.length) {
        trackContainer.innerHTML = '<p>No tracks found for this album.</p>';
        return;
    }

    const summaryTracks = getUniqueAlbumTracks(tracks);
    updateAlbumTrackSummary(summaryTracks);
    renderAlbumTrackRows(trackContainer, tracks);
    scheduleAlbumTrackMetrics(tracks);
    toggleAlbumHeroActions(tracks);
}

async function populateAlbumHero(album) {
    populateAlbumTitle(album);
    populateAlbumArtwork(album);
    await populateAlbumArtistLinks(album);
    populateAlbumFolders(album);
}

function populateAlbumTitle(album) {
    const titleEl = document.getElementById('albumTitle');
    if (titleEl) {
        titleEl.textContent = album.title;
    }

    const breadcrumbTitle = document.getElementById('albumBreadcrumbTitle');
    if (breadcrumbTitle) {
        breadcrumbTitle.textContent = album.title || 'Album';
    }
}

function populateAlbumArtwork(album) {
    const artworkEl = document.getElementById('albumArtwork');
    if (artworkEl && album.preferredCoverPath) {
        const albumArtworkPath = `/api/library/image?path=${encodeURIComponent(album.preferredCoverPath)}&size=560`;
        const albumArtworkUrl = toSafeHttpUrl(appendCacheKey(albumArtworkPath));
        if (!albumArtworkUrl) {
            return;
        }
        artworkEl.innerHTML = `<img src="${escapeHtml(albumArtworkUrl)}" alt="${escapeHtml(album.title || 'Album')}" loading="eager" />`;
    }
}

async function populateAlbumArtistLinks(album) {
    if (!album.artistId) {
        return;
    }

    const artist = await fetchJsonOptional(`/api/library/artists/${album.artistId}`);
    if (!artist) {
        return;
    }

    const artistUrl = buildLibraryScopedUrl(`/Library/Artist/${album.artistId}`);
    const artistLink = document.getElementById('albumArtistLink');
    const artistNameEl = document.getElementById('albumArtistName');
    if (artistLink) {
        artistLink.href = artistUrl;
        artistLink.textContent = artist.name || 'Artist';
    }
    if (artistNameEl) {
        artistNameEl.href = artistUrl;
        artistNameEl.textContent = artist.name || 'Artist';
    }
}

function populateAlbumFolders(album) {
    const foldersEl = document.getElementById('albumFolders');
    if (!foldersEl) {
        return;
    }

    const folders = album.localFolders || [];
    foldersEl.innerHTML = folders.length
        ? folders.map(f => `<span class="folder-pill">${escapeHtml(f)}</span>`).join('')
        : '';
}

function getUniqueAlbumTracks(tracks) {
    const uniqueTracks = new Map();
    tracks.forEach(track => {
        const key = String(track?.id ?? '');
        if (key && !uniqueTracks.has(key)) {
            uniqueTracks.set(key, track);
        }
    });
    return Array.from(uniqueTracks.values());
}

function updateAlbumTrackSummary(summaryTracks) {
    const trackCountEl = document.getElementById('albumTrackCount');
    if (trackCountEl) {
        trackCountEl.textContent = `${summaryTracks.length} ${summaryTracks.length === 1 ? 'track' : 'tracks'}`;
    }

    const totalMs = summaryTracks.reduce((sum, t) => sum + (t.durationMs || 0), 0);
    const totalDurationEl = document.getElementById('albumDuration');
    if (!totalDurationEl || totalMs <= 0) {
        return;
    }

    const totalMin = Math.floor(totalMs / 60000);
    const totalSec = Math.floor((totalMs % 60000) / 1000);
    totalDurationEl.textContent = totalMin >= 60
        ? `${Math.floor(totalMin / 60)} hr ${totalMin % 60} min`
        : `${totalMin} min ${totalSec} sec`;
}

function buildAlbumTrackNumberCell(track, trackIndexText, rowFilePath, playLabel) {
    if (!track.availableLocally) {
        return `<span class="track-number__index">${escapeHtml(trackIndexText)}</span>`;
    }

    return `<span class="track-number__index">${escapeHtml(trackIndexText)}</span>
               <button class="library-track-play track-action track-play" type="button" data-library-play-track="${escapeHtml(String(track.id || ''))}" data-library-play-path="${escapeHtml(rowFilePath)}" aria-label="Play ${playLabel}">
                    <span class="material-icons preview-controls" aria-hidden="true">play_arrow</span>
               </button>`;
}

function buildAlbumTrackUrls(track, rowFilePath) {
    return {
        spectrogramUrl: rowFilePath
            ? `/api/library/analysis/track/${encodeURIComponent(track.id)}/spectrogram?filePath=${encodeURIComponent(rowFilePath)}`
            : `/api/library/analysis/track/${encodeURIComponent(track.id)}/spectrogram`,
        lrcEditorUrl: `/Lrc?trackId=${encodeURIComponent(track.id)}`,
        tagEditorUrl: `/AutoTag/QuickTag?trackId=${encodeURIComponent(track.id)}`
    };
}

function buildAlbumTrackRow(track) {
    const trackNum = track.trackNo || '';
    const rowFilePath = track.filePath || '';
    const rowKey = String(track.variantKey || `${track.id}:0`);
    const playLabel = escapeHtml(track.title || 'track');
    const trackIndexText = trackNum || '—';
    const { spectrogramUrl, lrcEditorUrl, tagEditorUrl } = buildAlbumTrackUrls(track, rowFilePath);
    const numberCellContent = buildAlbumTrackNumberCell(track, trackIndexText, rowFilePath, playLabel);
    const variantLabel = formatTrackVariantLabel(track);
    const variantClass = formatTrackVariantClass(track);
    const audioLabel = formatTrackAudioLabel(track);
    const qualityLabel = formatTrackQualityLabel(track);
    const qualityClass = formatTrackQualityClass(track);
    const lyricsLabel = formatTrackLyricsLabel(track);
    const lyricsClass = formatTrackLyricsClass(track);
    const sampleRateLabel = formatTrackSampleRate(track);
    const bitDepthLabel = formatTrackBitDepth(track);
    const channelsLabel = formatMetricChannels(track?.channels);
    const nyquistLabel = Number(track?.sampleRateHz) > 0
        ? formatMetricNyquist(Number(track.sampleRateHz) / 2)
        : '—';

    return `
            <div class="track-row${track.availableLocally ? ' track-row--local' : ''}" data-track-id="${escapeHtml(String(track.id || ''))}" data-track-variant-key="${escapeHtml(rowKey)}" data-track-primary="${track.isPrimaryVariant ? 'true' : 'false'}">
                <div class="track-number">
                    ${numberCellContent}
                </div>
                <div class="track-meta">
                    <strong>${escapeHtml(track.title || 'Unknown')}</strong>
                </div>
                <div class="track-audio">
                    <span class="track-variant-pill ${variantClass}">${escapeHtml(variantLabel)}</span>
                    <span class="track-audio-label">${escapeHtml(audioLabel)}</span>
                </div>
                <div class="track-quality">
                    <span class="quality-pill ${qualityClass}">${escapeHtml(qualityLabel)}</span>
                </div>
                <div class="track-lyrics">
                    <span class="lyrics-pill ${lyricsClass}">${escapeHtml(lyricsLabel)}</span>
                </div>
                <span class="track-duration" data-track-cell="duration">${formatDuration(track.durationMs)}</span>
                <span class="track-metric track-metric--sample-rate" data-track-cell="sample-rate">${escapeHtml(sampleRateLabel)}</span>
                <span class="track-metric track-metric--bit-depth" data-track-cell="bit-depth">${escapeHtml(bitDepthLabel)}</span>
                <span class="track-metric track-metric--channels" data-track-cell="channels">${escapeHtml(channelsLabel)}</span>
                <span class="track-metric track-metric--nyquist" data-track-cell="nyquist">${escapeHtml(nyquistLabel)}</span>
                <span class="track-metric track-metric--dynamic-range" data-track-cell="dynamic-range">—</span>
                <span class="track-metric track-metric--peak" data-track-cell="peak">—</span>
                <span class="track-metric track-metric--rms" data-track-cell="rms">—</span>
                <span class="track-metric track-metric--samples" data-track-cell="samples">—</span>
                <div class="track-actions">
                    <details class="track-actions-menu">
                        <summary title="Track actions" aria-label="Track actions">⋯</summary>
                        <div class="track-actions-menu__list">
                            <a href="${escapeHtml(spectrogramUrl)}"
                               data-library-spectrogram-url="${escapeHtml(spectrogramUrl)}"
                               data-library-spectrogram-title="${escapeHtml(track.title || 'Track')}"
                               data-library-track-id="${escapeHtml(String(track.id || ''))}"
                               data-library-track-file-path="${escapeHtml(rowFilePath)}"
                            >View Spectrogram</a>
                            <a href="${escapeHtml(lrcEditorUrl)}">Open LRC Editor</a>
                            <a href="${escapeHtml(tagEditorUrl)}">Open Tag Editor</a>
                        </div>
                    </details>
                </div>
            </div>
        `;
}

function renderAlbumTrackRows(trackContainer, tracks) {
    trackContainer.innerHTML = tracks.map(track => {
        if (!libraryState.albumTracksById.has(String(track.id))) {
            libraryState.albumTracksById.set(String(track.id), track);
        }
        return buildAlbumTrackRow(track);
    }).join('');
}

function scheduleAlbumTrackMetrics(tracks) {
    tracks.forEach(track => {
        const key = String(track?.id ?? '');
        if (key) {
            scheduleTrackMetricsUpdate(track.id, track.filePath || '', track.variantKey || `${track.id}:0`);
        }
    });
}

function toggleAlbumHeroActions(tracks) {
    const allLocal = tracks.every(t => t.availableLocally);
    const heroActions = document.querySelector('.album-hero__actions');
    if (heroActions) {
        heroActions.style.display = allLocal ? 'none' : '';
    }
}

function formatDuration(durationMs) {
    if (!durationMs) {
        return '--:--';
    }
    const totalSeconds = Math.floor(durationMs / 1000);
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return `${minutes}:${seconds.toString().padStart(2, '0')}`;
}

function formatAnalysisClock(secondsValue) {
    const totalSeconds = Math.max(0, Math.round(Number(secondsValue || 0)));
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return `${minutes}:${seconds.toString().padStart(2, '0')}`;
}

function formatAnalysisFileSize(bytesValue) {
    const bytes = Number(bytesValue || 0);
    if (!Number.isFinite(bytes) || bytes <= 0) {
        return '—';
    }

    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let value = bytes;
    let index = 0;
    while (value >= 1024 && index < units.length - 1) {
        value /= 1024;
        index++;
    }

    const precision = value >= 10 ? 1 : 2;
    return `${value.toFixed(precision)} ${units[index]}`;
}

function formatAnalysisNumber(value, decimals = 2, suffix = '') {
    const number = Number(value);
    if (!Number.isFinite(number)) {
        return '—';
    }
    return `${number.toFixed(decimals)}${suffix}`;
}

function renderAnalysisStat(label, value) {
    return `<span class="track-analysis-stat"><span class="track-analysis-stat__label">${escapeHtml(label)}:</span> <span class="track-analysis-stat__value">${escapeHtml(value)}</span></span>`;
}

function updateTrackAnalysisHeader(titleEl, subtitleEl, summary) {
    if (titleEl) {
        titleEl.textContent = summary?.title || 'Track Analysis';
    }
    if (subtitleEl) {
        const artist = summary?.artist || 'Unknown Artist';
        const album = summary?.album || 'Unknown Album';
        subtitleEl.textContent = `${artist} • ${album}`;
    }
}

function formatChannelLabel(channelsValue) {
    if (channelsValue <= 0) {
        return '—';
    }
    if (channelsValue === 2) {
        return 'Stereo';
    }
    if (channelsValue === 1) {
        return 'Mono';
    }
    return `${channelsValue}`;
}

function buildPrimaryTrackAnalysisStats(summary) {
    const sampleRateText = Number(summary?.sampleRateHz) > 0
        ? `${(Number(summary.sampleRateHz) / 1000).toFixed(1)} kHz`
        : '—';
    const bitDepthText = Number(summary?.bitsPerSample) > 0
        ? `${Number(summary.bitsPerSample)}-bit`
        : '—';
    const channelsValue = Number(summary?.channels || 0);
    const channelsText = formatChannelLabel(channelsValue);
    const durationText = Number(summary?.durationSeconds) > 0
        ? formatAnalysisClock(summary.durationSeconds)
        : '--:--';
    const nyquistText = Number(summary?.nyquistHz) > 0
        ? `${(Number(summary.nyquistHz) / 1000).toFixed(1)} kHz`
        : '—';
    const fileSizeText = formatAnalysisFileSize(summary?.fileSize);

    return [
        renderAnalysisStat('Sample Rate', sampleRateText),
        renderAnalysisStat('Bit Depth', bitDepthText),
        renderAnalysisStat('Channels', channelsText),
        renderAnalysisStat('Duration', durationText),
        renderAnalysisStat('Nyquist', nyquistText),
        renderAnalysisStat('Size', fileSizeText)
    ].join('');
}

function buildSecondaryTrackAnalysisStats(summary) {
    const dynamicRangeText = formatAnalysisNumber(summary?.dynamicRangeDb, 2, ' dB');
    const peakText = formatAnalysisNumber(summary?.peakAmplitudeDb, 2, ' dB');
    const rmsText = formatAnalysisNumber(summary?.rmsLevelDb, 2, ' dB');
    const sampleCount = Number(summary?.totalSamples);
    const sampleCountText = Number.isFinite(sampleCount) && sampleCount > 0
        ? sampleCount.toLocaleString()
        : '—';

    return [
        renderAnalysisStat('Dynamic Range', dynamicRangeText),
        renderAnalysisStat('Peak', peakText),
        renderAnalysisStat('RMS', rmsText),
        renderAnalysisStat('Samples', sampleCountText)
    ].join('');
}

function buildTrackAnalysisSpectrogramUrl(trackId, summary) {
    const spectrogramSeconds = Math.max(10, Math.min(600, Number(summary?.spectrogramSeconds || 120)));
    const spectrogramWidth = Math.max(320, Math.min(4096, Number(summary?.spectrogramWidth || 1600)));
    const spectrogramHeight = Math.max(180, Math.min(2160, Number(summary?.spectrogramHeight || 720)));
    return appendCacheKey(
        `/api/library/analysis/track/${encodeURIComponent(trackId)}/spectrogram?width=${spectrogramWidth}&height=${spectrogramHeight}&seconds=${spectrogramSeconds}`
    );
}

function renderTrackAnalysisSpectrogram(trackId, summary, plotStatusEl, spectrogramEl) {
    spectrogramEl.onload = () => {
        plotStatusEl.style.display = 'none';
        spectrogramEl.style.display = 'block';
    };
    spectrogramEl.onerror = () => {
        plotStatusEl.style.display = '';
        plotStatusEl.textContent = 'Unable to load spectrogram image.';
        spectrogramEl.style.display = 'none';
    };
    spectrogramEl.src = buildTrackAnalysisSpectrogramUrl(trackId, summary);
}

function getTrackRowPlayPath(row) {
    return String(row.querySelector('[data-library-play-path]')?.dataset.libraryPlayPath || '')
        .trim()
        .toLowerCase();
}

async function loadTrackAnalysisPage() {
    const page = document.querySelector('.library-track-analysis-page[data-track-id]');
    if (!page) {
        return;
    }

    const trackId = page.dataset.trackId;
    if (!trackId) {
        return;
    }

    const titleEl = document.getElementById('trackAnalysisTitle');
    const subtitleEl = document.getElementById('trackAnalysisSubtitle');
    const primaryEl = document.getElementById('trackAnalysisPrimary');
    const secondaryEl = document.getElementById('trackAnalysisSecondary');
    const pathEl = document.getElementById('trackAnalysisPath');
    const plotStatusEl = document.getElementById('trackAnalysisPlotStatus');
    const spectrogramEl = document.getElementById('trackAnalysisSpectrogram');

    if (!primaryEl || !secondaryEl || !pathEl || !plotStatusEl || !spectrogramEl) {
        return;
    }

    try {
        const summary = await fetchJson(`/api/library/analysis/track/${encodeURIComponent(trackId)}/summary`);
        updateTrackAnalysisHeader(titleEl, subtitleEl, summary);
        primaryEl.innerHTML = buildPrimaryTrackAnalysisStats(summary);
        secondaryEl.innerHTML = buildSecondaryTrackAnalysisStats(summary);
        pathEl.textContent = summary?.filePath || '';
        renderTrackAnalysisSpectrogram(trackId, summary, plotStatusEl, spectrogramEl);

        if (summary?.analysisWarning) {
            showToast(`Analysis warning: ${summary.analysisWarning}`, true);
        }
    } catch (error) {
        if (titleEl) {
            titleEl.textContent = 'Track Analysis';
        }
        if (subtitleEl) {
            subtitleEl.textContent = 'Failed to load track analysis.';
        }
        plotStatusEl.style.display = '';
        plotStatusEl.textContent = error?.message ? `Failed to load analysis: ${error.message}` : 'Failed to load analysis.';
        spectrogramEl.style.display = 'none';
    }
}

let librarySpectrogramModalRefs = null;
let inlineAnalysisRefs = null;

function ensureLibrarySpectrogramModal() {
    if (librarySpectrogramModalRefs) {
        return librarySpectrogramModalRefs;
    }

    const root = document.createElement('div');
    root.className = 'library-spectrogram-modal';
    root.innerHTML = `
        <div class="library-spectrogram-modal__backdrop" data-library-spectrogram-close></div>
        <div class="library-spectrogram-modal__panel" role="dialog" aria-modal="true" aria-label="Spectrogram viewer">
            <div class="library-spectrogram-modal__header">
                <h3 id="librarySpectrogramModalTitle">Spectrogram</h3>
                <button type="button" class="library-spectrogram-modal__close" data-library-spectrogram-close aria-label="Close spectrogram viewer">✕</button>
            </div>
            <div class="library-spectrogram-modal__status" id="librarySpectrogramModalStatus">Loading spectrogram...</div>
            <img id="librarySpectrogramModalImage" alt="Track spectrogram" />
        </div>
    `;
    document.body.appendChild(root);

    const title = root.querySelector('#librarySpectrogramModalTitle');
    const status = root.querySelector('#librarySpectrogramModalStatus');
    const image = root.querySelector('#librarySpectrogramModalImage');
    const closeTargets = root.querySelectorAll('[data-library-spectrogram-close]');

    closeTargets.forEach(target => {
        target.addEventListener('click', () => {
            root.classList.remove('is-open');
        });
    });

    document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && root.classList.contains('is-open')) {
            root.classList.remove('is-open');
        }
    });

    librarySpectrogramModalRefs = { root, title, status, image };
    return librarySpectrogramModalRefs;
}

function ensureInlineAnalysisPanel() {
    if (inlineAnalysisRefs) {
        return inlineAnalysisRefs;
    }

    const panel = document.getElementById('trackAnalysisInline');
    if (!panel) {
        return null;
    }

    inlineAnalysisRefs = {
        title: panel.querySelector('.track-analysis-inline__title'),
        subtitle: panel.querySelector('.track-analysis-inline__subtitle'),
        sampleRate: document.getElementById('inlineSampleRate'),
        bitDepth: document.getElementById('inlineBitDepth'),
        channels: document.getElementById('inlineChannels'),
        duration: document.getElementById('inlineDuration'),
        nyquist: document.getElementById('inlineNyquist'),
        size: document.getElementById('inlineSize'),
        dynamicRange: document.getElementById('inlineDynamicRange'),
        peak: document.getElementById('inlinePeak'),
        rms: document.getElementById('inlineRms'),
        samples: document.getElementById('inlineSamples')
    };
    return inlineAnalysisRefs;
}

function updateInlineAnalysis(summary) {
    const refs = ensureInlineAnalysisPanel();
    if (!refs) {
        return;
    }

    refs.sampleRate.textContent = Number(summary?.sampleRateHz) > 0
        ? `${(Number(summary.sampleRateHz) / 1000).toFixed(1)} kHz`
        : '—';
    refs.bitDepth.textContent = Number(summary?.bitsPerSample) > 0
        ? `${Number(summary.bitsPerSample)}-bit`
        : '—';
    const channelsValue = Number(summary?.channels || 0);
    refs.channels.textContent = formatChannelLabel(channelsValue);
    refs.duration.textContent = Number(summary?.durationSeconds) > 0
        ? formatAnalysisClock(summary.durationSeconds)
        : '--:--';
    refs.nyquist.textContent = Number(summary?.nyquistHz) > 0
        ? `${(Number(summary.nyquistHz) / 1000).toFixed(1)} kHz`
        : '—';
    refs.size.textContent = formatAnalysisFileSize(summary?.fileSize);
    refs.dynamicRange.textContent = formatAnalysisNumber(summary?.dynamicRangeDb, 2, ' dB');
    refs.peak.textContent = formatAnalysisNumber(summary?.peakAmplitudeDb, 2, ' dB');
    refs.rms.textContent = formatAnalysisNumber(summary?.rmsLevelDb, 2, ' dB');
    const sampleCount = Number(summary?.totalSamples);
    refs.samples.textContent = Number.isFinite(sampleCount) && sampleCount > 0
        ? sampleCount.toLocaleString()
        : '—';

    if (refs.subtitle) {
        const artist = summary?.artist || 'Unknown Artist';
        const album = summary?.album || 'Unknown Album';
        refs.subtitle.textContent = `${artist} • ${album}`;
    }
}

async function loadTrackSummaryForInline(trackId, filePath) {
    try {
        const queryPath = typeof filePath === 'string' ? filePath.trim() : '';
        const url = queryPath
            ? `/api/library/analysis/track/${encodeURIComponent(trackId)}/summary?filePath=${encodeURIComponent(queryPath)}`
            : `/api/library/analysis/track/${encodeURIComponent(trackId)}/summary`;
        const summary = await fetchJson(url);
        updateInlineAnalysis(summary);
    } catch {
        // ignore; panel remains unchanged
    }
}

function openLibrarySpectrogramModal(spectrogramUrl, trackTitle) {
    const trackId = arguments[2];
    const filePath = arguments[3];
    if (!spectrogramUrl) {
        return;
    }

    const modal = ensureLibrarySpectrogramModal();
    if (!modal?.root || !modal?.status || !modal?.image || !modal?.title) {
        return;
    }

    modal.title.textContent = trackTitle ? `Spectrogram • ${trackTitle}` : 'Spectrogram';
    modal.status.textContent = 'Loading spectrogram...';
    modal.status.style.display = '';
    modal.image.style.display = 'none';
    modal.root.classList.add('is-open');

    modal.image.onload = () => {
        modal.status.style.display = 'none';
        modal.image.style.display = 'block';
    };
    modal.image.onerror = () => {
        modal.status.style.display = '';
        modal.status.textContent = 'Unable to load spectrogram image.';
        modal.image.style.display = 'none';
    };
    const safeSpectrogramUrl = toSafeHttpUrl(appendCacheKey(spectrogramUrl));
    if (!safeSpectrogramUrl) {
        modal.status.style.display = '';
        modal.status.textContent = 'Invalid spectrogram URL.';
        modal.image.style.display = 'none';
        return;
    }
    modal.image.src = safeSpectrogramUrl;

    if (trackId) {
        loadTrackSummaryForInline(trackId, filePath);
    }
}

function formatTrackFormat(track) {
    const extension = String(track?.extension || '').trim();
    const codec = String(track?.codec || '').trim();
    if (extension && codec) {
        return `${extension.replace(/^\./, '').toUpperCase()} / ${codec}`;
    }
    if (extension) {
        return extension.replace(/^\./, '').toUpperCase();
    }
    if (codec) {
        return codec;
    }
    return 'Unknown';
}

function formatTrackBitDepth(track) {
    const bits = Number(track?.bitsPerSample || 0);
    return bits > 0 ? `${bits}-bit` : '—';
}

function formatTrackSampleRate(track) {
    const hz = Number(track?.sampleRateHz || 0);
    if (hz <= 0) {
        return '—';
    }
    const khz = hz / 1000;
    const precision = Number.isInteger(khz) ? 0 : 1;
    return `${khz.toFixed(precision)} kHz`;
}

function formatTrackBitrate(track) {
    const kbps = Number(track?.bitrateKbps || 0);
    return kbps > 0 ? `${kbps} kbps` : '—';
}

function formatTrackVariantLabel(track) {
    const variant = String(track?.audioVariant || '').trim().toLowerCase();
    if (variant === 'atmos') {
        return 'Atmos';
    }
    if (variant === 'surround') {
        return 'Surround';
    }
    if (variant === 'stereo') {
        return 'Stereo';
    }
    return 'Stereo';
}

function formatTrackVariantClass(track) {
    const variant = String(track?.audioVariant || '').trim().toLowerCase();
    if (variant === 'atmos') {
        return 'track-variant-pill--atmos';
    }
    if (variant === 'surround') {
        return 'track-variant-pill--surround';
    }
    return 'track-variant-pill--stereo';
}

function formatTrackAudioLabel(track) {
    const format = formatTrackFormat(track);
    const bitrate = formatTrackBitrate(track);

    const parts = [format, bitrate].filter(part => part && part !== '—' && part !== 'Unknown');
    if (parts.length === 0) {
        return 'Unknown';
    }

    return parts.join(' · ');
}

function formatTrackQualityLabel(track) {
    const rank = Number(track?.qualityRank || 0);
    switch (rank) {
        case 4:
            return 'Hi-Res';
        case 3:
            return 'Lossless';
        case 2:
            return 'High';
        case 1:
            return 'Standard';
        default:
            return 'Unknown';
    }
}

function formatTrackQualityClass(track) {
    const rank = Number(track?.qualityRank || 0);
    if (rank >= 4) return 'quality-pill--hires';
    if (rank === 3) return 'quality-pill--lossless';
    if (rank === 2) return 'quality-pill--high';
    if (rank === 1) return 'quality-pill--standard';
    return 'quality-pill--unknown';
}

function formatTrackLyricsLabel(track) {
    const status = String(track?.lyricsStatus || '').trim();
    if (!status) {
        return 'None';
    }

    const normalized = status.toLowerCase().replaceAll(/\s+/g, '_');
    if (normalized === 'ttml_lrc_txt' || normalized === 'ttml_synced_unsynced') return 'TTML+LRC+TXT';
    if (normalized === 'ttml_lrc' || normalized === 'ttml_synced') return 'TTML+LRC';
    if (normalized === 'ttml_txt' || normalized === 'ttml_unsynced') return 'TTML+TXT';
    if (normalized === 'ttml') return 'TTML';
    if (normalized === 'lrc' || normalized === 'synced') return 'LRC';
    if (normalized === 'txt' || normalized === 'unsynced') return 'TXT';
    if (normalized === 'lrc_txt' || normalized === 'both') return 'LRC+TXT';
    if (normalized === 'embedded') return 'Embedded';
    if (normalized === 'missing') return 'None';
    if (normalized === 'none') return 'None';
    if (normalized === 'error') return 'Error';
    return status;
}

function formatTrackLyricsClass(track) {
    const normalized = String(track?.lyricsStatus || '').trim().toLowerCase().replaceAll(/\s+/g, '_');
    if (
        normalized === 'lrc'
        || normalized === 'synced'
        || normalized === 'both'
        || normalized === 'lrc_txt'
        || normalized === 'embedded'
        || normalized === 'ttml'
        || normalized === 'ttml_lrc'
        || normalized === 'ttml_lrc_txt'
        || normalized === 'ttml_synced'
        || normalized === 'ttml_synced_unsynced'
    ) {
        return 'lyrics-pill--available';
    }
    if (
        normalized === 'txt'
        || normalized === 'unsynced'
        || normalized === 'ttml_txt'
        || normalized === 'ttml_unsynced'
    ) {
        return 'lyrics-pill--partial';
    }
    if (normalized === 'missing' || normalized === 'none') {
        return 'lyrics-pill--missing';
    }
    if (normalized === 'error') {
        return 'lyrics-pill--error';
    }
    return 'lyrics-pill--unknown';
}

async function scheduleTrackMetricsUpdate(trackId, filePath, variantKey) {
    if (!trackId) {
        return;
    }

    try {
        const summary = await getTrackSummaryData(trackId, filePath);
        if (summary) {
            updateTrackRowMetrics(trackId, summary, variantKey, filePath);
        }
    } catch {
        // ignore
    }
}

function getTrackSummaryCacheKey(trackId, filePath) {
    const path = typeof filePath === 'string' ? filePath.trim().toLowerCase() : '';
    return `${trackId}|${path}`;
}

async function getTrackSummaryData(trackId, filePath) {
    const cacheKey = getTrackSummaryCacheKey(trackId, filePath);
    if (libraryTrackSummaryCache.has(cacheKey)) {
        return libraryTrackSummaryCache.get(cacheKey);
    }

    const queryPath = typeof filePath === 'string' ? filePath.trim() : '';
    const url = queryPath
        ? `/api/library/analysis/track/${encodeURIComponent(trackId)}/summary?filePath=${encodeURIComponent(queryPath)}`
        : `/api/library/analysis/track/${encodeURIComponent(trackId)}/summary`;
    const summary = await fetchJson(url);
    if (summary) {
        libraryTrackSummaryCache.set(cacheKey, summary);
    }
    return summary;
}

function updateTrackRowMetrics(trackId, summary, variantKey, filePath) {
    const normalizedRequestedPath = typeof filePath === 'string' ? filePath.trim().toLowerCase() : '';
    const normalizedSummaryPath = typeof summary?.filePath === 'string' ? summary.filePath.trim().toLowerCase() : '';

    let rowEl = null;
    if (variantKey) {
        const variantKeyText = String(variantKey);
        const safeVariantKey = globalThis.CSS && typeof globalThis.CSS.escape === 'function'
            ? globalThis.CSS.escape(variantKeyText)
            : variantKeyText.replaceAll('\\', String.raw`\\`).replaceAll('"', String.raw`\"`);
        rowEl = document.querySelector(`.track-row[data-track-id="${trackId}"][data-track-variant-key="${safeVariantKey}"]`);
    }

    if (!rowEl && normalizedRequestedPath) {
        rowEl = Array.from(document.querySelectorAll(`.track-row[data-track-id="${trackId}"]`))
            .find(el => {
                const rowPath = getTrackRowPlayPath(el);
                return rowPath === normalizedRequestedPath;
            }) || null;
    }

    if (!rowEl && normalizedSummaryPath) {
        rowEl = Array.from(document.querySelectorAll(`.track-row[data-track-id="${trackId}"]`))
            .find(el => {
                const rowPath = getTrackRowPlayPath(el);
                return rowPath === normalizedSummaryPath;
            }) || null;
    }

    if (!rowEl) {
        rowEl = document.querySelector(`.track-row[data-track-id="${trackId}"][data-track-primary="true"]`)
            || document.querySelector(`.track-row[data-track-id="${trackId}"]`);
    }
    if (!rowEl || !summary) {
        return;
    }

    const setMetric = (key, value) => {
        const target = rowEl.querySelector(`[data-track-cell="${key}"]`);
        if (target) {
            target.textContent = value;
        }
    };

    setMetric('sample-rate', formatMetricSampleRate(summary?.sampleRateHz));
    setMetric('bit-depth', formatMetricBitDepth(summary?.bitsPerSample));
    setMetric('channels', formatMetricChannels(summary?.channels));
    setMetric('duration', formatAnalysisClock(summary?.durationSeconds));
    setMetric('nyquist', formatMetricNyquist(summary?.nyquistHz));
    setMetric('dynamic-range', formatAnalysisNumber(summary?.dynamicRangeDb, 2, ' dB'));
    setMetric('peak', formatAnalysisNumber(summary?.peakAmplitudeDb, 2, ' dB'));
    setMetric('rms', formatAnalysisNumber(summary?.rmsLevelDb, 2, ' dB'));
    setMetric('samples', formatMetricSamples(summary?.totalSamples));
    const durationSeconds = Number(summary?.durationSeconds);
    if (Number.isFinite(durationSeconds) && durationSeconds > 0) {
        setMetric('duration', formatAnalysisClock(durationSeconds));
    }
}

function formatMetricSampleRate(value) {
    if (!Number.isFinite(Number(value)) || Number(value) <= 0) {
        return '—';
    }
    return `${(Number(value) / 1000).toFixed(1)} kHz`;
}

function formatMetricBitDepth(value) {
    return Number.isFinite(Number(value)) && Number(value) > 0 ? `${Number(value)}-bit` : '—';
}

function formatMetricChannels(value) {
    const number = Number(value);
    if (!Number.isFinite(number) || number <= 0) {
        return '—';
    }
    if (number === 2) {
        return 'Stereo';
    }
    if (number === 1) {
        return 'Mono';
    }
    return `${number}`;
}

function formatMetricNyquist(value) {
    return formatMetricSampleRate(value);
}

function formatMetricSamples(value) {
    const number = Number(value);
    if (!Number.isFinite(number) || number <= 0) {
        return '—';
    }
    return number.toLocaleString();
}

function setFavoritesStatus(prefix, message, state) {
    const status = document.getElementById(`${prefix}FavoritesStatus`);
    if (!status) {
        return;
    }
    status.textContent = message || 'Unavailable';
    status.classList.remove('is-connected', 'is-error');
    if (state === 'connected') {
        status.classList.add('is-connected');
    } else if (state === 'error') {
        status.classList.add('is-error');
    }
}

function renderFavoritesList(items, listId, emptyId) {
    const container = document.getElementById(listId);
    const empty = document.getElementById(emptyId);
    if (!container) {
        return false;
    }

    const subsection = container.closest('.favorites-subsection');
    container.innerHTML = '';
    const hasItems = Array.isArray(items) && items.length > 0;
    if (!hasItems) {
        if (subsection) {
            subsection.hidden = true;
        }
        if (empty) {
            empty.classList.remove('is-visible');
            empty.hidden = true;
        }
        return false;
    }
    if (subsection) {
        subsection.hidden = false;
    }
    if (empty) {
        empty.classList.remove('is-visible');
        empty.hidden = true;
    }
    items.forEach(item => {
        const card = document.createElement('div');
        card.className = 'favorites-card';
        card.setAttribute('role', 'button');
        card.tabIndex = 0;
        const cardLink = resolveFavoritesLink(item);
        if (cardLink) {
            card.dataset.link = cardLink;
            card.addEventListener('click', () => {
                globalThis.location.href = cardLink;
            });
            card.addEventListener('keydown', (event) => {
                if (event.key === 'Enter') {
                    globalThis.location.href = cardLink;
                }
            });
        }

        const cover = document.createElement('div');
        cover.className = 'favorites-cover';
        const safeImageUrl = toSafeHttpUrl(item.imageUrl || '');
        if (safeImageUrl) {
            const img = document.createElement('img');
            img.src = safeImageUrl;
            img.alt = item.name || '';
            cover.appendChild(img);
        } else {
            cover.textContent = item.type ? item.type.toUpperCase() : 'ITEM';
        }

        const body = document.createElement('div');
        body.className = 'favorites-body';

        const title = document.createElement('div');
        title.className = 'favorites-title';
        title.textContent = item.name || 'Untitled';

        body.appendChild(title);

        if (item.subtitle) {
            const subtitle = document.createElement('div');
            subtitle.className = 'favorites-subtitle';
            subtitle.textContent = item.subtitle;
            body.appendChild(subtitle);
        }

        const meta = document.createElement('div');
        meta.className = 'favorites-meta';
        if (item.type) {
            meta.textContent = item.type.toUpperCase();
        }
        if (item.durationMs) {
            const duration = formatDuration(item.durationMs);
            meta.textContent = meta.textContent ? `${meta.textContent} • ${duration}` : duration;
        }
        if (meta.textContent) {
            body.appendChild(meta);
        }

        card.appendChild(cover);
        card.appendChild(body);
        container.appendChild(card);
    });

    return true;
}

function setFavoriteProviderVisibility(sectionId, hasAnyContent) {
    const section = document.getElementById(sectionId);
    if (!section) {
        return;
    }

    section.hidden = !hasAnyContent;
}

function isSpotifySourceUrl(url) {
    if (typeof url !== 'string' || !url.trim()) {
        return false;
    }

    try {
        const parsed = new URL(url, globalThis.location.origin);
        const host = parsed.hostname.toLowerCase();
        if (host.includes('spotify.com') || host.includes('scdn.co')) {
            return true;
        }
    } catch {
        // Fall through to string-based check below.
    }

    const normalized = url.toLowerCase();
    return normalized.includes('spotify.com');
}

function resolveFavoritesLink(item) {
    if (!item?.id || !item.type) {
        return '';
    }

    const type = String(item.type).toLowerCase();
    const id = encodeURIComponent(item.id);
    const url = item.sourceUrl || '';
    const isSpotify = isSpotifySourceUrl(url);

    if (type === 'artist') {
        return `/Artist?id=${id}&source=${isSpotify ? 'spotify' : 'deezer'}`;
    }

    if (type === 'playlist' || type === 'album' || type === 'track') {
        if (isSpotify) {
            return `/Tracklist?id=${id}&type=${type}&source=spotify`;
        }
        return `/Tracklist?id=${id}&type=${type}&source=deezer`;
    }

    return '';
}

async function loadFavorites() {
    const root = document.getElementById('favoritesContainer');
    if (!root) {
        return;
    }
    setFavoritesStatus('spotify', 'Loading...', '');
    setFavoritesStatus('deezer', 'Loading...', '');
    try {
        const response = await fetchJson('/api/favorites?limit=50');
        const spotify = response?.spotify;
        const deezer = response?.deezer;

        if (spotify?.available) {
            setFavoritesStatus('spotify', 'Connected', 'connected');
        } else {
            setFavoritesStatus('spotify', spotify?.message || 'Not connected', 'error');
        }
        if (deezer?.available) {
            setFavoritesStatus('deezer', 'Connected', 'connected');
        } else {
            setFavoritesStatus('deezer', deezer?.message || 'Not connected', 'error');
        }

        const spotifyHasPlaylists = renderFavoritesList(spotify?.playlists, 'spotifyFavoritePlaylists', 'spotifyFavoritePlaylistsEmpty');
        const spotifyHasTracks = renderFavoritesList(spotify?.tracks, 'spotifyFavoriteTracks', 'spotifyFavoriteTracksEmpty');
        const deezerHasAlbums = renderFavoritesList(deezer?.albums, 'deezerFavoriteAlbums', 'deezerFavoriteAlbumsEmpty');
        const deezerHasPlaylists = renderFavoritesList(deezer?.playlists, 'deezerFavoritePlaylists', 'deezerFavoritePlaylistsEmpty');
        const deezerHasTracks = renderFavoritesList(deezer?.tracks, 'deezerFavoriteTracks', 'deezerFavoriteTracksEmpty');

        setFavoriteProviderVisibility('spotifyFavoritesSection', spotifyHasPlaylists || spotifyHasTracks);
        setFavoriteProviderVisibility('deezerFavoritesSection', deezerHasAlbums || deezerHasPlaylists || deezerHasTracks);
    } catch (error) {
        setFavoritesStatus('spotify', 'Unavailable', 'error');
        setFavoritesStatus('deezer', 'Unavailable', 'error');
        setFavoriteProviderVisibility('spotifyFavoritesSection', false);
        setFavoriteProviderVisibility('deezerFavoritesSection', false);
        console.error('Failed to load favorites.', error);
    }
}

function resetFolderModal() {
    const title = document.getElementById('folderModalTitle');
    if (title) {
        title.textContent = 'Add Library Folder';
    }
    const editId = document.getElementById('folderEditId');
    if (editId) {
        editId.value = '';
    }
    resetFolderModalFields();
    syncFolderConversionFieldsState();
    updateSaveFolderState();
}

function resetFolderModalFields() {
    const pathInput = document.getElementById('folderPath');
    if (pathInput) {
        pathInput.value = '';
    }
    const nameInput = document.getElementById('folderName');
    if (nameInput) {
        nameInput.value = '';
    }
    const enabledField = document.getElementById('folderEnabled');
    if (enabledField) {
        enabledField.checked = true;
    }
    const atmosCheckbox = document.getElementById('folderAtmosDestination');
    if (atmosCheckbox) {
        atmosCheckbox.checked = false;
    }
    const videoCheckbox = document.getElementById('folderVideoDestination');
    if (videoCheckbox) {
        videoCheckbox.checked = false;
    }
    const podcastCheckbox = document.getElementById('folderPodcastDestination');
    if (podcastCheckbox) {
        podcastCheckbox.checked = false;
    }
    const qualityField = document.getElementById('folderQuality');
    if (qualityField) {
        qualityField.value = '27';
    }
    const convertEnabledField = document.getElementById('folderConvertEnabled');
    if (convertEnabledField) {
        convertEnabledField.checked = false;
    }
    const convertFormatField = document.getElementById('folderConvertFormat');
    if (convertFormatField) {
        convertFormatField.value = '';
    }
    const convertBitrateField = document.getElementById('folderConvertBitrate');
    if (convertBitrateField) {
        convertBitrateField.value = '';
    }
}

function syncFolderConversionFieldsState() {
    const enabled = document.getElementById('folderConvertEnabled')?.checked === true;
    const formatField = document.getElementById('folderConvertFormat');
    const bitrateField = document.getElementById('folderConvertBitrate');
    if (formatField) {
        formatField.disabled = !enabled;
    }
    if (bitrateField) {
        bitrateField.disabled = !enabled;
    }
}

function syncAppModalOpenState() {
    const hasOpenModal = Array.from(document.querySelectorAll('.app-modal'))
        .some((modal) => !modal.classList.contains('hidden'));
    document.body.classList.toggle('app-modal-open', hasOpenModal);
    document.documentElement.classList.toggle('app-modal-open', hasOpenModal);
}

function openFolderModal(folder = null) {
    const modal = document.getElementById('folderModal');
    if (!modal) {
        return;
    }

    resetFolderModal();

    if (folder && typeof folder === 'object') {
        populateFolderModal(folder);
        syncFolderConversionFieldsState();
        updateSaveFolderState();
    }

    modal.classList.remove('hidden');
    modal.setAttribute('aria-hidden', 'false');
    syncAppModalOpenState();
}

function resolveFolderDestinationFlags(folder) {
    const desiredQuality = String(folder.desiredQuality || '27');
    const normalizedPath = normalizePath(folder.rootPath || '');
    const currentVideoPath = normalizePath(libraryState.videoDownloadLocation || '');
    const currentPodcastPath = normalizePath(libraryState.podcastDownloadLocation || '');
    const isAtmosDestination = folder.id === libraryState.atmosDestinationFolderId;
    const isVideoDestination = normalizedPath.length > 0
        && normalizedPath === currentVideoPath;
    const isPodcastDestination = normalizedPath.length > 0
        && normalizedPath === currentPodcastPath;

    return { desiredQuality, isAtmosDestination, isVideoDestination, isPodcastDestination };
}

function populateFolderModalCoreFields(folder) {
    const title = document.getElementById('folderModalTitle');
    if (title) {
        title.textContent = 'Edit Library Folder';
    }
    const editId = document.getElementById('folderEditId');
    if (editId) {
        editId.value = String(folder.id || '');
    }
    const pathInput = document.getElementById('folderPath');
    if (pathInput) {
        pathInput.value = String(folder.rootPath || '');
    }
    const nameInput = document.getElementById('folderName');
    if (nameInput) {
        nameInput.value = String(folder.displayName || '');
    }
    const enabledField = document.getElementById('folderEnabled');
    if (enabledField) {
        enabledField.checked = isFolderEnabledFlag(folder.enabled);
    }
}

function populateFolderModalDestinationFields(folder, flags) {
    const atmosCheckbox = document.getElementById('folderAtmosDestination');
    if (atmosCheckbox) {
        atmosCheckbox.checked = flags.isAtmosDestination;
    }
    const videoCheckbox = document.getElementById('folderVideoDestination');
    if (videoCheckbox) {
        videoCheckbox.checked = !flags.isAtmosDestination && flags.isVideoDestination;
    }
    const podcastCheckbox = document.getElementById('folderPodcastDestination');
    if (podcastCheckbox) {
        podcastCheckbox.checked = !flags.isAtmosDestination && !flags.isVideoDestination && flags.isPodcastDestination;
    }

    const qualityField = document.getElementById('folderQuality');
    if (qualityField) {
        const desiredQuality = String(flags.desiredQuality || '').trim();
        const qualityOptions = Array.from(qualityField.options || []);
        const exactMatch = qualityOptions.find((option) => option.value === desiredQuality);
        const caseInsensitiveMatch = exactMatch
            ? null
            : qualityOptions.find((option) => option.value.localeCompare(desiredQuality, undefined, { sensitivity: 'accent' }) === 0);
        qualityField.value = (exactMatch || caseInsensitiveMatch)?.value || '27';
    }
}

function populateFolderModalConversionFields(folder) {
    const convertEnabledField = document.getElementById('folderConvertEnabled');
    const convertFormatField = document.getElementById('folderConvertFormat');
    const convertBitrateField = document.getElementById('folderConvertBitrate');
    const normalizedConvertFormat = normalizeFolderConvertFormatValue(folder.convertFormat);
    const normalizedConvertBitrate = normalizeFolderConvertBitrateValue(folder.convertBitrate);

    if (convertEnabledField) {
        convertEnabledField.checked = folder.convertEnabled === true;
    }
    if (convertFormatField) {
        convertFormatField.value = normalizedConvertFormat || '';
    }
    if (convertBitrateField) {
        convertBitrateField.value = normalizedConvertBitrate || '';
    }
}

function populateFolderModal(folder) {
    const flags = resolveFolderDestinationFlags(folder);
    populateFolderModalCoreFields(folder);
    populateFolderModalDestinationFields(folder, flags);
    populateFolderModalConversionFields(folder);
}

function closeFolderModal() {
    const modal = document.getElementById('folderModal');
    if (!modal) {
        return;
    }
    modal.classList.add('hidden');
    modal.setAttribute('aria-hidden', 'true');
    syncAppModalOpenState();
}

function wireExclusiveFolderDestinationRoles() {
    const roleIds = ['folderAtmosDestination', 'folderVideoDestination', 'folderPodcastDestination'];
    const roleToggles = roleIds
        .map((id) => document.getElementById(id))
        .filter((element) => element instanceof HTMLInputElement);

    roleToggles.forEach((toggle) => {
        if (toggle.dataset.boundExclusiveRole === 'true') {
            return;
        }

        toggle.dataset.boundExclusiveRole = 'true';
        toggle.addEventListener('change', () => {
            if (!toggle.checked) {
                return;
            }

            roleToggles.forEach((other) => {
                if (other !== toggle) {
                    other.checked = false;
                }
            });
        });
    });
}

async function saveFolder() {
    if (libraryState.folderSaveInProgress) {
        return;
    }

    const folderInput = readFolderModalInput();
    if (!folderInput.rootPath) {
        showToast('Folder path is required.', true);
        return;
    }
    const folderConflictMessage = getFolderPathConflictMessage(folderInput.rootPath);
    if (folderConflictMessage) {
        showToast(folderConflictMessage, true);
        return;
    }

    libraryState.folderSaveInProgress = true;
    updateSaveFolderState();
    try {
        const finalQuality = resolveFolderDestinationQuality(folderInput);
        const folder = await fetchJson(folderInput.isEdit ? `/api/library/folders/${folderInput.editId}` : '/api/library/folders', {
            method: folderInput.isEdit ? 'PATCH' : 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                rootPath: folderInput.rootPath,
                displayName: folderInput.displayName,
                enabled: folderInput.enabled,
                libraryName: folderInput.existingFolder?.libraryName ?? null,
                desiredQuality: finalQuality,
                convertEnabled: folderInput.convertEnabled,
                convertFormat: folderInput.convertFormat,
                convertBitrate: folderInput.convertBitrate
            })
        });
        await persistFolderDestinationRole(folder, folderInput);
        resetFolderModalFields();
        syncFolderConversionFieldsState();
        await loadFolders();
        updateSaveFolderState();
        closeFolderModal();
        showToast(folderInput.isEdit ? 'Folder updated.' : 'Folder saved.');
        emitAutoTagProfileLibrarySettingsChanged('folder-save');
    } catch (error) {
        showToast(`Failed to save folder: ${error.message}`, true);
    } finally {
        libraryState.folderSaveInProgress = false;
        updateSaveFolderState();
    }
}

function readFolderModalInput() {
    const editIdRaw = document.getElementById('folderEditId')?.value || '';
    const editId = Number.parseInt(String(editIdRaw).trim(), 10);
    const isEdit = Number.isFinite(editId) && editId > 0;
    const existingFolder = isEdit
        ? libraryState.folders.find((item) => item.id === editId) || null
        : null;
    const rootPath = document.getElementById('folderPath').value.trim();
    const convertEnabled = document.getElementById('folderConvertEnabled')?.checked === true;
    let enabled = document.getElementById('folderEnabled')?.checked !== false;
    if (!document.getElementById('folderEnabled') && isEdit && existingFolder) {
        enabled = isFolderEnabledFlag(existingFolder.enabled);
    }
    return {
        editId,
        isEdit,
        existingFolder,
        rootPath,
        displayName: document.getElementById('folderName').value.trim() || deriveFolderDisplayName(rootPath),
        enabled,
        desiredQuality: document.getElementById('folderQuality')?.value || '27',
        useAtmosDestination: document.getElementById('folderAtmosDestination')?.checked === true,
        useVideoDestination: document.getElementById('folderVideoDestination')?.checked === true,
        usePodcastDestination: document.getElementById('folderPodcastDestination')?.checked === true,
        convertEnabled,
        convertFormat: convertEnabled ? normalizeFolderConvertFormatValue(document.getElementById('folderConvertFormat')?.value || '') : null,
        convertBitrate: convertEnabled ? normalizeFolderConvertBitrateValue(document.getElementById('folderConvertBitrate')?.value || '') : null
    };
}

function deriveFolderDisplayName(rootPath) {
    const normalized = trimTrailingPathSeparators(String(rootPath || '').trim());
    if (!normalized) {
        return '';
    }

    const normalizedWithForwardSlashes = normalized.replaceAll('\\', '/');
    const lastSeparatorIndex = normalizedWithForwardSlashes.lastIndexOf('/');
    if (lastSeparatorIndex < 0) {
        return normalizedWithForwardSlashes;
    }

    return normalizedWithForwardSlashes.slice(lastSeparatorIndex + 1) || normalized;
}

function trimTrailingPathSeparators(path) {
    let end = path.length;
    while (end > 0) {
        const char = path.charAt(end - 1);
        if (char === '/' || char === '\\') {
            end -= 1;
            continue;
        }
        break;
    }

    return end === path.length ? path : path.slice(0, end);
}

function getFolderPathConflictMessage(rootPath) {
    if (!libraryState.downloadLocation) {
        return '';
    }

    return normalizePath(rootPath) === normalizePath(libraryState.downloadLocation)
        ? 'Library folders cannot match the download folder.'
        : '';
}

function resolveFolderDestinationQuality(folderInput) {
    if (folderInput.useAtmosDestination) {
        return 'ATMOS';
    }
    if (folderInput.useVideoDestination) {
        return 'VIDEO';
    }
    if (folderInput.usePodcastDestination) {
        return 'PODCAST';
    }
    return folderInput.desiredQuality;
}

async function persistFolderDestinationRole(folder, folderInput) {
    if (!folderInput.useAtmosDestination && !folderInput.useVideoDestination && !folderInput.usePodcastDestination) {
        return;
    }

    await updateDownloadDestinations({
        atmosFolderId: folderInput.useAtmosDestination ? folder.id : null,
        videoFolderPath: folderInput.useVideoDestination ? folder.rootPath : null,
        podcastFolderPath: folderInput.usePodcastDestination ? folder.rootPath : null
    });
}

function updateSaveFolderState() {
    const saveButton = document.getElementById('saveFolder');
    const pathInput = document.getElementById('folderPath');
    if (!saveButton || !pathInput) {
        return;
    }

    const rootPath = pathInput.value.trim();
    const hasPath = rootPath.length > 0;
    const conflictMessage = getFolderPathConflictMessage(rootPath);
    const isBusy = libraryState.folderSaveInProgress === true;

    saveButton.disabled = !hasPath || isBusy;
    saveButton.setAttribute('aria-disabled', saveButton.disabled ? 'true' : 'false');

    if (isBusy) {
        saveButton.title = 'Saving folder...';
        return;
    }

    if (conflictMessage) {
        saveButton.title = `${conflictMessage} Click Save to view details.`;
        return;
    }

    saveButton.removeAttribute('title');
}

async function loadDownloadLocation() {
    try {
        const data = await fetchJson('/api/getSettings');
        const settings = data?.settings || {};
        const multiQuality = settings?.multiQuality || {};
        libraryState.downloadLocation = settings.downloadLocation || null;
        libraryState.atmosDestinationFolderId = multiQuality.secondaryDestinationFolderId ?? null;
        libraryState.globalMultiQualityEnabled = multiQuality.enabled === true || multiQuality.secondaryEnabled === true;
        libraryState.videoDownloadLocation = settings?.video?.videoDownloadLocation || null;
        libraryState.podcastDownloadLocation = settings?.podcast?.downloadLocation || null;
        const hint = document.getElementById('downloadLocationHint');
        if (hint && libraryState.downloadLocation) {
            hint.textContent = `Current download folder: ${libraryState.downloadLocation}`;
        }
        updateSaveFolderState();
        populateAlbumDestinationOptions();
    } catch (error) {
        console.warn('Failed to load download settings.', error);
    }
}

function normalizePath(path) {
    let normalized = String(path || '').trim();
    while (normalized.endsWith('/') || normalized.endsWith('\\')) {
        normalized = normalized.slice(0, -1);
    }
    return normalized.toLowerCase();
}

function bindLibraryAction(button, action) {
    if (!button) {
        return;
    }
    button.addEventListener('click', action);
}

function bindFolderPathInput(input, action) {
    if (!input) {
        return;
    }
    input.addEventListener('input', action);
    input.addEventListener('change', action);
    input.addEventListener('paste', () => {
        globalThis.setTimeout(action, 0);
    });
}

function bindFolderChangeInput(input, action) {
    if (!input) {
        return;
    }
    input.addEventListener('change', action);
}

function getLibraryBootstrapElements() {
    const folderModal = document.getElementById('folderModal');
    const unmatchedModal = document.getElementById('unmatchedArtistsModal');
    return {
        refreshButton: document.getElementById('refreshLibrary'),
        scanButton: document.getElementById('scanLibrary'),
        cancelScanButton: document.getElementById('cancelLibraryScan'),
        refreshImagesButton: document.getElementById('refreshImages'),
        cleanupButton: document.getElementById('cleanupLibrary'),
        clearButton: document.getElementById('clearLibrary'),
        resolveUnmatchedArtistsButton: document.getElementById('resolveUnmatchedArtists'),
        saveButton: document.getElementById('saveSettings'),
        chooseFolderButton: document.getElementById('chooseFolder'),
        saveFolderButton: document.getElementById('saveFolder'),
        addFolderButton: document.getElementById('addFolderBtn'),
        folderModalCloseButton: document.getElementById('folderModalClose'),
        folderModalCancelButton: document.getElementById('folderModalCancel'),
        folderModal,
        folderModalBackdrop: folderModal?.querySelector('.folder-modal-backdrop'),
        unmatchedModal,
        unmatchedModalCloseButton: document.getElementById('closeUnmatchedArtistsModal'),
        unmatchedModalBackdrop: unmatchedModal?.querySelector('[data-close-unmatched-modal]'),
        unmatchedModalList: document.getElementById('unmatchedArtistsList'),
        unmatchedModalStatus: document.getElementById('unmatchedArtistsStatus'),
        unmatchedModalRefreshButton: document.getElementById('refreshUnmatchedArtists'),
        unmatchedModalApplyHighConfidenceButton: document.getElementById('applyHighConfidenceUnmatchedArtists'),
        unmatchedModalSearchInput: document.getElementById('unmatchedArtistsSearch'),
        browseLibraryFolderPathButton: document.getElementById('browseLibraryFolderPath'),
        folderPathInput: document.getElementById('folderPath'),
        folderConvertEnabledInput: document.getElementById('folderConvertEnabled'),
        folderConvertFormatInput: document.getElementById('folderConvertFormat'),
        folderConvertBitrateInput: document.getElementById('folderConvertBitrate')
    };
}

function bindFolderModalActions(elements) {
    bindLibraryAction(elements.chooseFolderButton, async () => {
        if (elements.browseLibraryFolderPathButton) {
            elements.browseLibraryFolderPathButton.click();
        }
    });
    bindLibraryAction(elements.saveFolderButton, saveFolder);
    bindLibraryAction(elements.addFolderButton, openFolderModal);
    bindLibraryAction(elements.folderModalCloseButton, closeFolderModal);
    bindLibraryAction(elements.folderModalCancelButton, closeFolderModal);
    bindLibraryAction(elements.folderModalBackdrop, closeFolderModal);
}

function bindFolderPathBrowser(elements) {
    bindLibraryAction(elements.browseLibraryFolderPathButton, async () => {
        const selected = await DeezSpoTag.ui.browseServerFolder({
            title: 'Library Folder Path',
            startPath: elements.folderPathInput?.value || '',
            apiPath: '/api/library/folders/browse',
            selectText: 'Use This Folder'
        });
        if (!selected || !elements.folderPathInput) {
            return;
        }

        elements.folderPathInput.value = selected;
        const folderNameInput = document.getElementById('folderName');
        if (folderNameInput && !folderNameInput.value.trim()) {
            folderNameInput.value = deriveFolderDisplayName(selected) || selected;
        }
        updateSaveFolderState();
    });
}

async function updateDownloadDestinations(destinationUpdate) {
    const update = destinationUpdate || {};
    const { atmosFolderId, videoFolderPath, podcastFolderPath } = update;
    const hasAtmosFolderId = Object.hasOwn(update, 'atmosFolderId');
    const hasVideoFolderPath = Object.hasOwn(update, 'videoFolderPath');
    const hasPodcastFolderPath = Object.hasOwn(update, 'podcastFolderPath');
    const data = await fetchJson('/api/getSettings');
    const settings = data?.settings;
    if (!settings) {
        throw new Error('Unable to load settings.');
    }

    settings.multiQuality = settings.multiQuality || {};
    if (hasAtmosFolderId) {
        if (typeof atmosFolderId === 'number') {
            settings.multiQuality.secondaryDestinationFolderId = atmosFolderId;
            settings.multiQuality.enabled = true;
            settings.multiQuality.secondaryEnabled = true;
        } else {
            settings.multiQuality.secondaryDestinationFolderId = null;
        }
    }

    settings.video = settings.video || {};
    if (hasVideoFolderPath) {
        settings.video.videoDownloadLocation = typeof videoFolderPath === 'string'
            ? videoFolderPath
            : '';
    }

    settings.podcast = settings.podcast || {};
    if (hasPodcastFolderPath) {
        settings.podcast.downloadLocation = typeof podcastFolderPath === 'string'
            ? podcastFolderPath
            : '';
    }

    const patch = buildDownloadDestinationPatch(settings, {
        hasAtmosFolderId,
        hasVideoFolderPath,
        hasPodcastFolderPath
    });

    await fetchJson('/api/saveSettings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(patch)
    });

    libraryState.atmosDestinationFolderId = settings.multiQuality?.secondaryDestinationFolderId ?? null;
    libraryState.globalMultiQualityEnabled = settings.multiQuality?.enabled === true || settings.multiQuality?.secondaryEnabled === true;
    libraryState.videoDownloadLocation = settings.video?.videoDownloadLocation || null;
    libraryState.podcastDownloadLocation = settings.podcast?.downloadLocation || null;
    emitAutoTagProfileLibrarySettingsChanged('download-destinations');
}

function buildDownloadDestinationPatch(settings, flags) {
    const patch = {};
    if (flags.hasAtmosFolderId) {
        patch.multiQuality = {
            secondaryDestinationFolderId: settings.multiQuality.secondaryDestinationFolderId,
            enabled: settings.multiQuality.enabled,
            secondaryEnabled: settings.multiQuality.secondaryEnabled
        };
    }
    if (flags.hasVideoFolderPath) {
        patch.video = {
            videoDownloadLocation: settings.video.videoDownloadLocation
        };
    }
    if (flags.hasPodcastFolderPath) {
        patch.podcast = {
            downloadLocation: settings.podcast.downloadLocation
        };
    }
    return patch;
}

async function updateFolder(id) {
    const folder = libraryState.folders.find(item => item.id === id);
    if (!folder) {
        return;
    }
    openFolderModal(folder);
}

async function deleteFolder(id) {
    if (!await DeezSpoTag.ui.confirm('Remove this folder?', { title: 'Remove Folder' })) {
        return;
    }
    await fetchJson(`/api/library/folders/${id}`, { method: 'DELETE' });
    await loadFolders();
    showToast('Folder removed.');
    emitAutoTagProfileLibrarySettingsChanged('folder-delete');
}

async function toggleAliases(id, container) {
    if (container.dataset.open === 'true') {
        container.innerHTML = '';
        container.dataset.open = 'false';
        return;
    }

    const aliases = await fetchJson(`/api/library/folders/${id}/aliases`);
    libraryState.aliases.set(id, aliases);
    container.dataset.open = 'true';
    renderAliases(id, container);
}

function renderAliases(folderId, container) {
    const aliases = libraryState.aliases.get(folderId) || [];
    const listItems = aliases.map(alias => `
        <li>
            <span>${escapeHtml(alias.aliasName)}</span>
            <button class="btn-danger action-btn action-btn-sm" data-alias-id="${escapeHtml(String(alias.id || ''))}">Remove</button>
        </li>
    `).join('');

    container.innerHTML = `
        <div class="alias-panel">
            <ul>${listItems || '<li>No aliases yet.</li>'}</ul>
            <div class="form-row">
                <label>New alias</label>
                <input type="text" placeholder="Studio Archive" data-alias-input />
            </div>
            <div class="form-actions">
                <button class="btn-primary action-btn action-btn-sm" data-alias-add>Add Alias</button>
            </div>
        </div>
    `;

    container.querySelectorAll('[data-alias-id]').forEach(button => {
        button.addEventListener('click', async () => {
            const aliasId = button.dataset.aliasId;
            await fetchJson(`/api/library/folders/aliases/${aliasId}`, { method: 'DELETE' });
            await toggleAliases(folderId, container);
            showToast('Alias removed.');
        });
    });

    const addButton = container.querySelector('[data-alias-add]');
    const input = container.querySelector('[data-alias-input]');
    addButton.addEventListener('click', async () => {
        const aliasName = input.value.trim();
        if (!aliasName) {
            await DeezSpoTag.ui.alert('Alias name required.', { title: 'Missing Alias' });
            return;
        }
        await fetchJson(`/api/library/folders/${folderId}/aliases`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ aliasName })
        });
        input.value = '';
        await toggleAliases(folderId, container);
        showToast('Alias added.');
    });
}

function showToast(message, isError = false) {
    const type = isError ? 'error' : 'info';
    const notifier = globalThis.DeezSpoTag?.showNotification;
    if (typeof notifier === 'function') {
        notifier.call(globalThis.DeezSpoTag, String(message || ''), type);
        return;
    }

    // Keep a minimal fallback when the shared notifier is unavailable.
    const alertClass = isError ? 'alert-danger' : 'alert-info';
    const notification = document.createElement('div');
    notification.className = `alert ${alertClass} alert-dismissible fade show position-fixed deezspot-notification`;
    notification.style.top = '20px';
    notification.style.right = '20px';
    notification.style.zIndex = '1060';
    notification.style.maxWidth = '400px';
    const textNode = document.createElement('span');
    textNode.textContent = String(message || '');
    notification.appendChild(textNode);

    const closeButton = document.createElement('button');
    closeButton.type = 'button';
    closeButton.className = 'btn-close';
    closeButton.setAttribute('aria-label', 'Close');
    notification.appendChild(closeButton);
    closeButton?.addEventListener('click', () => notification.remove());
    document.body.appendChild(notification);
    setTimeout(() => notification.remove(), 5000);
}

function revealCheckedConversionOption(checkboxes) {
    const selected = checkboxes.find((checkbox) => checkbox.checked);
    const optionRow = selected?.closest('.folder-conversion-option');
    if (optionRow) {
        optionRow.scrollIntoView({ block: 'nearest' });
    }
}

function getCurrentFolderDestinationState(folder) {
    const folderPathNormalized = normalizePath(folder.rootPath || '');
    const currentVideoPath = normalizePath(libraryState.videoDownloadLocation || '');
    const currentPodcastPath = normalizePath(libraryState.podcastDownloadLocation || '');
    return {
        isCurrentAtmosDestination: folder.id === libraryState.atmosDestinationFolderId,
        isCurrentVideoDestination: folderPathNormalized.length > 0 && folderPathNormalized === currentVideoPath,
        isCurrentPodcastDestination: folderPathNormalized.length > 0 && folderPathNormalized === currentPodcastPath
    };
}

function buildDestinationClearUpdate(destinationState) {
    const clearDestinations = {};
    if (destinationState.isCurrentAtmosDestination) {
        clearDestinations.atmosFolderId = null;
    }
    if (destinationState.isCurrentVideoDestination) {
        clearDestinations.videoFolderPath = null;
    }
    if (destinationState.isCurrentPodcastDestination) {
        clearDestinations.podcastFolderPath = null;
    }
    return clearDestinations;
}

function hasDestinationAssignments(destinationState) {
    return destinationState.isCurrentAtmosDestination
        || destinationState.isCurrentVideoDestination
        || destinationState.isCurrentPodcastDestination;
}

async function clearFolderDestinationAssignments(folder) {
    const destinationState = getCurrentFolderDestinationState(folder);
    if (!hasDestinationAssignments(destinationState)) {
        return;
    }
    await updateDownloadDestinations(buildDestinationClearUpdate(destinationState));
}

async function applyFolderModeAtmos(folder, disableConversionIfEnabled, hasAutoTagProfileSelection) {
    await disableConversionIfEnabled();
    const destinationState = getCurrentFolderDestinationState(folder);
    const destinationUpdate = { atmosFolderId: folder.id };
    if (destinationState.isCurrentVideoDestination) {
        destinationUpdate.videoFolderPath = null;
    }
    if (destinationState.isCurrentPodcastDestination) {
        destinationUpdate.podcastFolderPath = null;
    }
    await updateDownloadDestinations(destinationUpdate);
    await setFolderDesiredQuality(folder.id, 'ATMOS');
    if (hasAutoTagProfileSelection()) {
        await setFolderAutoTagEnabled(folder.id, true);
        folder.autoTagEnabled = true;
        showToast('Atmos destination updated.');
        return;
    }
    showToast('Atmos destination updated. Assign a profile to enable AutoTag for this music folder.');
}

async function applyFolderModeVideo(folder, disableConversionIfEnabled) {
    await disableConversionIfEnabled();
    const destinationState = getCurrentFolderDestinationState(folder);
    const destinationUpdate = { videoFolderPath: folder.rootPath };
    if (destinationState.isCurrentAtmosDestination) {
        destinationUpdate.atmosFolderId = null;
    }
    if (destinationState.isCurrentPodcastDestination) {
        destinationUpdate.podcastFolderPath = null;
    }
    await updateDownloadDestinations(destinationUpdate);
    await setFolderDesiredQuality(folder.id, 'VIDEO');
    showToast('Video downloads folder updated.');
}

async function applyFolderModePodcast(folder, disableConversionIfEnabled) {
    await disableConversionIfEnabled();
    const destinationState = getCurrentFolderDestinationState(folder);
    const destinationUpdate = { podcastFolderPath: folder.rootPath };
    if (destinationState.isCurrentAtmosDestination) {
        destinationUpdate.atmosFolderId = null;
    }
    if (destinationState.isCurrentVideoDestination) {
        destinationUpdate.videoFolderPath = null;
    }
    await updateDownloadDestinations(destinationUpdate);
    await setFolderDesiredQuality(folder.id, 'PODCAST');
    showToast('Podcast downloads folder updated.');
}

async function applyFolderModeQuality(folder, newValue, disableConversionIfEnabled, hasAutoTagProfileSelection, qualityLabel) {
    await disableConversionIfEnabled();
    await clearFolderDestinationAssignments(folder);
    await setFolderDesiredQuality(folder.id, newValue);
    if (hasAutoTagProfileSelection()) {
        await setFolderAutoTagEnabled(folder.id, true);
        folder.autoTagEnabled = true;
        showToast(`Folder quality set to ${qualityLabel(newValue)}.`);
        return;
    }
    showToast('Folder quality saved. Assign a profile to enable AutoTag for this music folder.');
}

async function applyFolderModeSelection(folder, newValue, disableConversionIfEnabled, hasAutoTagProfileSelection, qualityLabel) {
    if (newValue === 'dest:atmos') {
        await applyFolderModeAtmos(folder, disableConversionIfEnabled, hasAutoTagProfileSelection);
        return;
    }
    if (newValue === 'dest:video') {
        await applyFolderModeVideo(folder, disableConversionIfEnabled);
        return;
    }
    if (newValue === 'dest:podcast') {
        await applyFolderModePodcast(folder, disableConversionIfEnabled);
        return;
    }
    await applyFolderModeQuality(folder, newValue, disableConversionIfEnabled, hasAutoTagProfileSelection, qualityLabel);
}

function bindFolderCombinedToggle(wrapper, folder, canEnableAutoTag) {
    const enabledToggle = wrapper.querySelector('[data-folder-enabled]');
    if (!enabledToggle) {
        return;
    }

    const statusText = wrapper.querySelector('[data-folder-enabled-text]');
    const setStatusText = (enabled) => {
        if (statusText) {
            statusText.textContent = enabled ? 'On' : 'Off';
        }
    };

    enabledToggle.addEventListener('change', async () => {
        const enabled = enabledToggle.checked;
        const previousLibraryEnabled = isFolderEnabledFlag(folder.enabled);
        const previousAutoTagEnabled = folder.autoTagEnabled !== false;
        const previousCombinedEnabled = previousLibraryEnabled && previousAutoTagEnabled;

        if (enabled && !canEnableAutoTag) {
            enabledToggle.checked = previousCombinedEnabled;
            setStatusText(previousCombinedEnabled);
            showToast('Assign an AutoTag profile before enabling this music folder.', true);
            return;
        }

        enabledToggle.disabled = true;
        try {
            await applyCombinedFolderToggleState(folder, enabled, previousLibraryEnabled);

            await Promise.all([loadFolders(), loadArtists(), loadLibraryScanStatus()]);
        } catch (error) {
            folder.enabled = previousLibraryEnabled;
            folder.autoTagEnabled = previousAutoTagEnabled;
            enabledToggle.checked = previousCombinedEnabled;
            setStatusText(previousCombinedEnabled);
            showToast(`Failed to update folder: ${error?.message || error}`, true);
        } finally {
            enabledToggle.disabled = false;
        }
    });
}

async function applyCombinedFolderToggleState(folder, enabled, previousLibraryEnabled) {
    if (enabled) {
        await enableCombinedFolderToggleState(folder, previousLibraryEnabled);
        showToast('Folder enabled for Library + AutoTag.');
        return;
    }

    await disableCombinedFolderToggleState(folder, previousLibraryEnabled);
    showToast('Folder disabled for Library + AutoTag.');
}

async function enableCombinedFolderToggleState(folder, previousLibraryEnabled) {
    if (!previousLibraryEnabled) {
        await setFolderEnabled(folder.id, true, {
            reload: false,
            refreshArtists: false,
            refreshScanStatus: false
        });
    }

    try {
        const updatedAutoTag = await setFolderAutoTagEnabled(folder.id, true);
        folder.autoTagEnabled = typeof updatedAutoTag?.autoTagEnabled === 'boolean'
            ? updatedAutoTag.autoTagEnabled
            : true;
    } catch (error) {
        if (!previousLibraryEnabled) {
            await setFolderEnabled(folder.id, false, {
                reload: false,
                refreshArtists: false,
                refreshScanStatus: false
            });
        }
        throw error;
    }

    folder.enabled = true;
}

async function disableCombinedFolderToggleState(folder, previousLibraryEnabled) {
    await setFolderAutoTagEnabled(folder.id, false);
    if (previousLibraryEnabled) {
        await setFolderEnabled(folder.id, false, {
            reload: false,
            refreshArtists: false,
            refreshScanStatus: false
        });
    }
    folder.enabled = false;
    folder.autoTagEnabled = false;
}

function bindFolderProfileSelection(wrapper, folder, getCurrentProfile, setCurrentProfile, currentSchedule) {
    const profileSelect = wrapper.querySelector('[data-folder-profile]');
    if (!profileSelect) {
        return;
    }
    profileSelect.value = getCurrentProfile();
    profileSelect.addEventListener('change', async () => {
        const previous = getCurrentProfile();
        const durationSelect = wrapper.querySelector('[data-folder-duration]');
        profileSelect.disabled = true;
        if (durationSelect) {
            durationSelect.disabled = true;
        }
        try {
            const updated = await setFolderAutoTagProfile(folder.id, profileSelect.value);
            folder.autoTagProfileId = (updated?.autoTagProfileId || profileSelect.value || '').trim() || null;
            folder.autoTagEnabled = typeof updated?.autoTagEnabled === 'boolean'
                ? updated.autoTagEnabled
                : folder.autoTagEnabled;
            await saveAutoTagFolderDefault(folder.id, folder.autoTagProfileId || '', durationSelect ? durationSelect.value : currentSchedule);
            setCurrentProfile(folder.autoTagProfileId || '');
            showToast('Folder AutoTag profile updated.');
            renderFolders();
        } catch (error) {
            profileSelect.value = previous;
            showToast(`Failed to save folder profile: ${error?.message || error}`, true);
        } finally {
            profileSelect.disabled = false;
            if (durationSelect) {
                durationSelect.disabled = false;
            }
        }
    });
}

function bindFolderDurationSelection(wrapper, folder, folderIdKey, getCurrentProfile) {
    const durationSelect = wrapper.querySelector('[data-folder-duration]');
    if (!durationSelect) {
        return;
    }
    const currentSchedule = libraryState.autotagDefaults?.librarySchedules?.[folderIdKey] || '';
    durationSelect.value = currentSchedule;
    durationSelect.addEventListener('change', async () => {
        const previous = currentSchedule;
        const profileSelectRef = wrapper.querySelector('[data-folder-profile]');
        const selectedProfile = profileSelectRef ? profileSelectRef.value : getCurrentProfile();
        durationSelect.disabled = true;
        if (profileSelectRef) {
            profileSelectRef.disabled = true;
        }
        try {
            await saveAutoTagFolderDefault(folder.id, selectedProfile, durationSelect.value);
            showToast('Folder AutoTag duration updated.');
        } catch (error) {
            durationSelect.value = previous;
            showToast(`Failed to save folder duration: ${error?.message || error}`, true);
        } finally {
            durationSelect.disabled = false;
            if (profileSelectRef) {
                profileSelectRef.disabled = false;
            }
        }
    });
}

function bindFolderEnhanceAction(enhanceButton, wrapper, folder) {
    if (!enhanceButton) {
        return;
    }
    enhanceButton.addEventListener('click', async () => {
        const profileSelectRef = wrapper.querySelector('[data-folder-profile]');
        const selectedProfile = profileSelectRef ? profileSelectRef.value : '';
        enhanceButton.disabled = true;
        try {
            const started = await startFolderEnhancement(folder, selectedProfile);
            showToast(`Enhancement started for ${folder.displayName} (${started.profileName}). Job ${started.jobId}.`);
        } catch (error) {
            showToast(`Failed to start enhancement: ${error?.message || error}`, true);
        } finally {
            enhanceButton.disabled = false;
        }
    });
}

function getFolderDestinationModeValue(destinationFlags) {
    if (destinationFlags.isAtmosDestination) {
        return 'dest:atmos';
    }
    if (destinationFlags.isVideoDestination) {
        return 'dest:video';
    }
    if (destinationFlags.isPodcastDestination) {
        return 'dest:podcast';
    }
    return '';
}

function buildFolderProfileColumnMarkup(profileOptionsSource, profileOptions, showProfileSelector) {
    if (!showProfileSelector) {
        return '<span class="folder-profile-static" aria-label="Profile not applicable">-</span>';
    }
    const disabledAttr = profileOptionsSource.length ? '' : 'disabled';
    return `<select class="folder-profile-select" data-folder-profile ${disabledAttr}>
                        ${profileOptions}
                    </select>`;
}

function computeFolderRowViewModel(folder, context) {
    const combinedEnabledId = `folder-enabled-${folder.id}`;
    const currentLibraryEnabled = isFolderEnabledFlag(folder.enabled);
    const currentQuality = String(folder.desiredQuality ?? '27');
    const folderIdKey = String(folder.id);
    const currentProfile = resolveFolderProfileReference(folder);
    const currentSchedule = context.folderSchedules[folderIdKey] || '';
    const profileOptions = context.profileOptionsSource.length
        ? ['<option value="">No profile</option>', ...context.profileOptionsSource.map((profile) => `<option value="${escapeHtml(profile.id)}">${escapeHtml(profile.name)}</option>`)].join('')
        : '<option value="">No profiles</option>';
    const scheduleOptions = context.scheduleChoices
        .map((choice) => `<option value="${escapeHtml(choice.value)}" ${choice.value === currentSchedule ? 'selected' : ''}>${escapeHtml(choice.label)}</option>`)
        .join('');
    const destinationFlags = context.getFolderDestinationFlags(folder);
    const { isVideoDestination, isPodcastDestination } = destinationFlags;
    const requiresProfileForAutoTag = !isVideoDestination && !isPodcastDestination;
    const showProfileSelector = requiresProfileForAutoTag;
    const hasAssignedProfile = currentProfile.trim().length > 0;
    const canEnableAutoTag = !requiresProfileForAutoTag || hasAssignedProfile;
    const currentAutoTagEnabled = folder.autoTagEnabled !== false;
    const currentCombinedEnabled = currentLibraryEnabled && currentAutoTagEnabled;
    let combinedToggleTitle = 'Assign an AutoTag profile first (music folders only).';
    if (currentCombinedEnabled) {
        combinedToggleTitle = 'Disable this folder for Library indexing and AutoTag.';
    } else if (canEnableAutoTag) {
        combinedToggleTitle = 'Enable this folder for Library indexing and AutoTag.';
    }
    const convertEnabled = folder.convertEnabled === true;
    const convertFormat = convertEnabled ? normalizeFolderConvertFormatValue(folder.convertFormat) : null;
    const convertBitrate = convertEnabled ? normalizeFolderConvertBitrateValue(folder.convertBitrate) : null;
    const selectedDestinationValue = getFolderDestinationModeValue(destinationFlags);
    const selectedQualityValue = convertEnabled
        ? context.conversionModeValue
        : (selectedDestinationValue || currentQuality);
    const formatSummary = context.getConversionOptionLabel(context.conversionFormatOptions, convertFormat, 'Not selected');
    const bitrateSummary = context.getConversionOptionLabel(context.conversionBitrateOptions, convertBitrate, 'Not selected');
    const conversionFormatCheckboxes = context.buildConversionCheckboxOptions(
        context.conversionFormatOptions,
        convertFormat,
        'data-folder-convert-format-option');
    const conversionBitrateCheckboxes = context.buildConversionCheckboxOptions(
        context.conversionBitrateOptions,
        convertBitrate,
        'data-folder-convert-bitrate-option');
    const qualityChoicesMarkup = context.qualityOptions
        .map((option) => `
                <button type="button" class="folder-quality-choice ${selectedQualityValue === option.value ? 'is-selected' : ''}" data-folder-mode-option="${escapeHtml(option.value)}">
                    ${escapeHtml(option.label)}
                </button>`)
        .join('');
    const destinationChoicesMarkup = [
        { value: 'dest:atmos', label: 'Atmos' },
        { value: 'dest:video', label: 'Video' },
        { value: 'dest:podcast', label: 'Podcast' }
    ].map((option) => `
            <button type="button" class="folder-quality-choice ${selectedQualityValue === option.value ? 'is-selected' : ''}" data-folder-mode-option="${escapeHtml(option.value)}">
                ${escapeHtml(option.label)}
            </button>`).join('');
    const profileColumnMarkup = buildFolderProfileColumnMarkup(
        context.profileOptionsSource,
        profileOptions,
        showProfileSelector
    );
    return {
        combinedEnabledId,
        currentLibraryEnabled,
        folderIdKey,
        currentProfile,
        currentSchedule,
        canEnableAutoTag,
        currentAutoTagEnabled,
        currentCombinedEnabled,
        combinedToggleTitle,
        selectedQualityValue,
        formatSummary,
        bitrateSummary,
        conversionFormatCheckboxes,
        conversionBitrateCheckboxes,
        qualityChoicesMarkup,
        destinationChoicesMarkup,
        profileColumnMarkup,
        scheduleOptions
    };
}

function buildFolderRowMarkup(folder, viewModel, conversionModeValue) {
    return `
            <div class="table-row">
                <span class="folder-label">
                    ${escapeHtml(folder.displayName)}
                    ${viewModel.currentLibraryEnabled ? '' : '<span class="folder-library-disabled-badge">Hidden</span>'}
                </span>
                <span>${escapeHtml(folder.rootPath)}</span>
                <span class="folder-quality-cell">
                    <div class="folder-quality-dropdown" data-folder-quality-dropdown>
                        <button type="button" class="folder-quality-summary" data-folder-quality-summary aria-haspopup="true" aria-expanded="false"></button>
                        <div class="folder-quality-panel" data-folder-quality-panel hidden>
                            <div class="folder-quality-group">
                                <div class="folder-quality-group-title">Quality</div>
                                ${viewModel.qualityChoicesMarkup}
                            </div>
                            <div class="folder-quality-group">
                                <div class="folder-quality-group-title">Destinations</div>
                                ${viewModel.destinationChoicesMarkup}
                            </div>
                            <div class="folder-quality-group">
                                <div class="folder-quality-group-title">Conversion</div>
                                <button type="button" class="folder-quality-choice ${viewModel.selectedQualityValue === conversionModeValue ? 'is-selected' : ''}" data-folder-mode-option="${conversionModeValue}">
                                    Conversion override
                                </button>
                            </div>
                            <div class="folder-quality-group folder-conversion-options ${viewModel.selectedQualityValue === conversionModeValue ? '' : 'is-disabled'}" data-folder-conversion-options>
                                <div class="folder-quality-group-title">Conversion Format (Select One)</div>
                                <div class="folder-conversion-checklist">
                                    ${viewModel.conversionFormatCheckboxes}
                                </div>
                                <div class="folder-quality-group-title mt-2">Conversion Bitrate (Select One)</div>
                                <div class="folder-conversion-checklist">
                                    ${viewModel.conversionBitrateCheckboxes}
                                </div>
                                <div class="helper small mb-0">
                                    Format: <span data-folder-convert-format-summary>${escapeHtml(viewModel.formatSummary)}</span> |
                                    Bitrate: <span data-folder-convert-bitrate-summary>${escapeHtml(viewModel.bitrateSummary)}</span>
                                </div>
                                <button type="button" class="folder-conversion-apply" data-folder-conversion-apply>
                                    Apply Conversion
                                </button>
                            </div>
                        </div>
                    </div>
                </span>
                <span>
                    ${viewModel.profileColumnMarkup}
                </span>
                <span>
                    <select class="folder-duration-select" data-folder-duration ${viewModel.currentLibraryEnabled && viewModel.currentAutoTagEnabled ? '' : 'disabled'}>
                        ${viewModel.scheduleOptions}
                    </select>
                </span>
                <span>
                    <label class="switch folder-library-toggle-label" title="${escapeHtml(viewModel.combinedToggleTitle)}">
                        <input id="${viewModel.combinedEnabledId}" type="checkbox" ${viewModel.currentCombinedEnabled ? 'checked' : ''} data-folder-enabled />
                        <span class="slider"></span>
                        <span class="folder-library-toggle-text" data-folder-enabled-text>${viewModel.currentCombinedEnabled ? 'On' : 'Off'}</span>
                    </label>
                </span>
                <span class="actions">
                    <button class="action-btn action-btn-sm folder-action" data-enhance title="Run enhancement now" ${viewModel.currentLibraryEnabled && viewModel.currentAutoTagEnabled ? '' : 'disabled'}>
                        <i class="fas fa-magic" aria-hidden="true"></i>
                        <span class="visually-hidden">Enhance</span>
                    </button>
                    <button class="action-btn action-btn-sm folder-action" data-edit title="Edit folder">
                        <i class="fas fa-pen" aria-hidden="true"></i>
                        <span class="visually-hidden">Edit</span>
                    </button>
                    <button class="action-btn action-btn-sm folder-action" data-aliases title="Manage aliases">
                        <i class="fas fa-link" aria-hidden="true"></i>
                        <span class="visually-hidden">Aliases</span>
                    </button>
                    <button class="btn-danger action-btn action-btn-sm folder-action" data-delete title="Delete folder">
                        <i class="fas fa-trash" aria-hidden="true"></i>
                        <span class="visually-hidden">Delete</span>
                    </button>
                </span>
            </div>
            <div class="alias-container" data-open="false"></div>
        `;
}

function renderFolders() {
    const container = document.getElementById('foldersContainer');
    const emptyState = document.getElementById('foldersEmptyState');
    ensureFolderQualityOverlayHandlers();
    closeActiveFolderQualityDropdown();
    container.innerHTML = '';

    if (!libraryState.folders.length) {
        if (emptyState) {
            emptyState.style.display = 'flex';
        }
        return;
    }
    if (emptyState) {
        emptyState.style.display = 'none';
    }

    const getQualityOptions = () => {
        const options = globalThis.LibraryFolderQualityOptions;
        if (Array.isArray(options) && options.length > 0) {
            return options
                .map((option) => ({
                    value: String(option.value ?? ''),
                    label: String(option.label ?? option.value ?? '')
                }))
                .filter((option) => option.value.length > 0);
        }

        return [
            { value: 'ATMOS', label: 'Atmos' },
            { value: 'VIDEO', label: 'Video' },
            { value: 'PODCAST', label: 'Podcast' },
            { value: '27', label: 'Hi-Res (24-bit/96kHz+)' },
            { value: 'HI_RES_LOSSLESS', label: 'Hi-Res Lossless (24-bit/48kHz+)' },
            { value: '7', label: 'FLAC 24-bit' },
            { value: '6', label: 'FLAC 16-bit (CD)' },
            { value: 'LOSSLESS', label: 'Lossless (16-bit/CD)' },
            { value: '9', label: 'FLAC' },
            { value: '3', label: 'MP3 320kbps' },
            { value: '1', label: 'MP3 128kbps' }
        ];
    };

    const rawQualityOptions = getQualityOptions();
    const qualityOptions = rawQualityOptions.filter((option) => !["ATMOS", "VIDEO", "PODCAST"].includes(option.value));
    const conversionModeValue = 'conv:on';
    const conversionFormatOptions = [
        { value: 'mp3', label: 'MP3' },
        { value: 'aac', label: 'AAC (M4A)' },
        { value: 'alac', label: 'ALAC (M4A)' },
        { value: 'ogg', label: 'OGG' },
        { value: 'opus', label: 'OPUS' },
        { value: 'flac', label: 'FLAC' },
        { value: 'wav', label: 'WAV' }
    ];
    const conversionBitrateOptions = [
        { value: 'AUTO', label: 'Auto' },
        { value: '64', label: '64 kbps' },
        { value: '96', label: '96 kbps' },
        { value: '128', label: '128 kbps' },
        { value: '160', label: '160 kbps' },
        { value: '192', label: '192 kbps' },
        { value: '256', label: '256 kbps' },
        { value: '320', label: '320 kbps' }
    ];
    const normalizeOptionalTextValue = (value) => {
        const text = String(value ?? '').trim();
        return text.length > 0 ? text : null;
    };
    const normalizeConversionOptionValue = (options, value) => {
        if (options === conversionFormatOptions) {
            return normalizeFolderConvertFormatValue(value);
        }
        if (options === conversionBitrateOptions) {
            return normalizeFolderConvertBitrateValue(value);
        }
        return normalizeOptionalTextValue(value);
    };
    const getConversionOptionLabel = (options, value, fallback) => {
        const normalized = normalizeConversionOptionValue(options, value);
        if (!normalized) {
            return fallback;
        }
        const match = options.find((option) => option.value === normalized);
        return match ? match.label : fallback;
    };
    const buildConversionCheckboxOptions = (options, selectedValue, dataAttributeName) => {
        const normalized = normalizeConversionOptionValue(options, selectedValue);
        return options
            .map((option) => `
                <label class="folder-conversion-option">
                    <input type="checkbox" ${dataAttributeName} value="${escapeHtml(option.value)}" ${option.value === normalized ? 'checked' : ''} />
                    <span>${escapeHtml(option.label)}</span>
                </label>`)
            .join('');
    };
    const qualityLabel = (qualityValue) => {
        const desired = String(qualityValue ?? '');
        const match = qualityOptions.find((option) => option.value === desired);
        return match ? match.label : (desired || 'Unknown');
    };

    const profileOptionsSource = Array.isArray(libraryState.autotagProfiles) ? libraryState.autotagProfiles : [];
    const folderSchedules = libraryState.autotagDefaults?.librarySchedules || {};
    const scheduleChoices = [
        { value: '', label: 'No automatic run' },
        { value: '7d', label: '1 week' },
        { value: '14d', label: '2 weeks' },
        { value: '30d', label: '1 month' },
        { value: '90d', label: '3 months' },
        { value: '180d', label: '6 months' }
    ];
    const getFolderDestinationFlags = (folder) => {
        const normalizedPath = normalizePath(folder?.rootPath);
        const isAtmosDestination = folder?.id === libraryState.atmosDestinationFolderId;
        const isVideoDestination = normalizedPath.length > 0
            && normalizedPath === normalizePath(libraryState.videoDownloadLocation || '');
        const isPodcastDestination = normalizedPath.length > 0
            && normalizedPath === normalizePath(libraryState.podcastDownloadLocation || '');
        return { isAtmosDestination, isVideoDestination, isPodcastDestination };
    };
    const foldersForDisplay = (libraryState.folders || [])
        .map((folder, index) => {
            const { isVideoDestination, isPodcastDestination } = getFolderDestinationFlags(folder);
            const pushToBottom = isVideoDestination || isPodcastDestination;
            return { folder, index, pushToBottom };
        })
        .sort((a, b) => {
            if (a.pushToBottom !== b.pushToBottom) {
                return a.pushToBottom ? 1 : -1;
            }
            return a.index - b.index;
        })
        .map((entry) => entry.folder);

    const folderRowContext = {
        profileOptionsSource,
        folderSchedules,
        scheduleChoices,
        getFolderDestinationFlags,
        conversionModeValue,
        getConversionOptionLabel,
        conversionFormatOptions,
        conversionBitrateOptions,
        buildConversionCheckboxOptions,
        qualityOptions
    };

    foldersForDisplay.forEach(folder => {
        const wrapper = document.createElement('div');
        wrapper.className = 'folder-row';
        if (!isFolderEnabledFlag(folder.enabled)) {
            wrapper.classList.add('is-disabled');
        }
        const viewModel = computeFolderRowViewModel(folder, folderRowContext);
        let currentProfile = viewModel.currentProfile;
        const currentSchedule = viewModel.currentSchedule;
        const folderIdKey = viewModel.folderIdKey;
        const selectedQualityValue = viewModel.selectedQualityValue;
        wrapper.innerHTML = buildFolderRowMarkup(folder, viewModel, conversionModeValue);

        bindFolderCombinedToggle(wrapper, folder, viewModel.canEnableAutoTag);
        const aliasContainer = wrapper.querySelector('.alias-container');
        const enhanceButton = wrapper.querySelector('[data-enhance]');
        wrapper.querySelector('[data-edit]').addEventListener('click', () => updateFolder(folder.id));
        wrapper.querySelector('[data-delete]').addEventListener('click', () => deleteFolder(folder.id));
        wrapper.querySelector('[data-aliases]').addEventListener('click', () => toggleAliases(folder.id, aliasContainer));

        const qualityDropdown = wrapper.querySelector('[data-folder-quality-dropdown]');
        const qualityPanelEl = wrapper.querySelector('[data-folder-quality-panel]');
        const qualitySummaryEl = wrapper.querySelector('[data-folder-quality-summary]');
        const modeButtons = Array.from(wrapper.querySelectorAll('[data-folder-mode-option]'));
        const conversionOptionsGroup = wrapper.querySelector('[data-folder-conversion-options]');
        const conversionApplyButton = wrapper.querySelector('[data-folder-conversion-apply]');
        const formatSummaryEl = wrapper.querySelector('[data-folder-convert-format-summary]');
        const bitrateSummaryEl = wrapper.querySelector('[data-folder-convert-bitrate-summary]');
        const formatCheckboxes = Array.from(wrapper.querySelectorAll('[data-folder-convert-format-option]'));
        const bitrateCheckboxes = Array.from(wrapper.querySelectorAll('[data-folder-convert-bitrate-option]'));
        let selectedMode = selectedQualityValue;
        let selectedConvertFormat = normalizeFolderConvertFormatValue(folder.convertFormat);
        let selectedConvertBitrate = normalizeFolderConvertBitrateValue(folder.convertBitrate);
        let conversionSaving = false;
        let conversionAutoPersistTimer = null;
        let conversionAutoPersistInFlight = false;
        qualityPanelEl?.addEventListener('pointerdown', (event) => {
            event.stopPropagation();
        });
        qualityPanelEl?.addEventListener('click', (event) => {
            event.stopPropagation();
        });
        const closeThisQualityDropdown = () => {
            if (!qualityDropdown || !qualityPanelEl || !qualitySummaryEl) {
                return;
            }
            if (activeFolderQualityDropdown === qualityDropdown) {
                closeActiveFolderQualityDropdown();
                return;
            }
            qualityDropdown.classList.remove('is-open');
            qualitySummaryEl.setAttribute('aria-expanded', 'false');
            qualityPanelEl.hidden = true;
        };
        const hasAutoTagProfileSelection = () => {
            const profileSelectRef = wrapper.querySelector('[data-folder-profile]');
            const reference = profileSelectRef ? profileSelectRef.value : currentProfile;
            return (reference || '').trim().length > 0;
        };
        const modeLabel = (modeValue) => {
            if (modeValue === conversionModeValue) {
                const formatLabel = getConversionOptionLabel(conversionFormatOptions, selectedConvertFormat, 'missing format');
                const bitrateLabel = getConversionOptionLabel(conversionBitrateOptions, selectedConvertBitrate, 'missing bitrate');
                return `${formatLabel}, ${bitrateLabel}`;
            }
            if (modeValue === 'dest:atmos') {
                return 'Atmos';
            }
            if (modeValue === 'dest:video') {
                return 'Video';
            }
            if (modeValue === 'dest:podcast') {
                return 'Podcast';
            }
            return qualityLabel(modeValue);
        };
        const updateQualitySummary = () => {
            if (qualitySummaryEl) {
                qualitySummaryEl.textContent = modeLabel(selectedMode);
            }
        };
        const updateModeButtonSelection = () => {
            modeButtons.forEach((button) => {
                button.classList.toggle('is-selected', (button.dataset.folderModeOption || '') === selectedMode);
            });
        };
        const setSingleCheckboxSelection = (checkboxes, value, normalizer = normalizeOptionalTextValue) => {
            const normalized = normalizer(value);
            let matched = false;
            checkboxes.forEach((checkbox) => {
                const checkboxValue = normalizer(checkbox.value);
                const isSelected = normalized !== null && checkboxValue === normalized;
                checkbox.checked = isSelected;
                matched = matched || isSelected;
            });
            if (!matched && normalized === null) {
                checkboxes.forEach((checkbox) => {
                    checkbox.checked = false;
                });
            }
        };
        const updateConversionSummaries = () => {
            if (formatSummaryEl) {
                const formatLabel = getConversionOptionLabel(conversionFormatOptions, selectedConvertFormat, 'Not selected');
                formatSummaryEl.textContent = formatLabel;
            }
            if (bitrateSummaryEl) {
                const bitrateLabel = getConversionOptionLabel(conversionBitrateOptions, selectedConvertBitrate, 'Not selected');
                bitrateSummaryEl.textContent = bitrateLabel;
            }
        };
        const revealSelectedConversionOptions = () => {
            revealCheckedConversionOption(formatCheckboxes);
            revealCheckedConversionOption(bitrateCheckboxes);
        };
        const syncConversionPickerState = () => {
            const conversionSelected = selectedMode === conversionModeValue;
            if (conversionOptionsGroup) {
                conversionOptionsGroup.classList.toggle('is-disabled', !conversionSelected);
            }
            const disableInputs = conversionSaving || !conversionSelected;
            formatCheckboxes.forEach((checkbox) => {
                checkbox.disabled = disableInputs;
            });
            bitrateCheckboxes.forEach((checkbox) => {
                checkbox.disabled = disableInputs;
            });
            modeButtons.forEach((button) => {
                button.disabled = conversionSaving;
            });
            if (qualityDropdown) {
                qualityDropdown.classList.toggle('is-busy', conversionSaving);
            }
            updateModeButtonSelection();
            updateQualitySummary();
        };
        const disableConversionIfEnabled = async (clearSelections = false) => {
            if (folder.convertEnabled !== true) {
                return;
            }
            const updated = await setFolderConversionSettings(folder.id, false, null, null);
            folder.convertEnabled = updated?.convertEnabled === true;
            folder.convertFormat = null;
            folder.convertBitrate = null;
            if (clearSelections) {
                selectedConvertFormat = null;
                selectedConvertBitrate = null;
                setSingleCheckboxSelection(formatCheckboxes, null, normalizeFolderConvertFormatValue);
                setSingleCheckboxSelection(bitrateCheckboxes, null, normalizeFolderConvertBitrateValue);
            }
            updateConversionSummaries();
        };
        const persistConversionIfReady = async (successMessage) => {
            if (!selectedConvertFormat || !selectedConvertBitrate) {
                showToast('Select one conversion format and one conversion bitrate.', true);
                return false;
            }
            const updated = await setFolderConversionSettings(
                folder.id,
                true,
                selectedConvertFormat,
                selectedConvertBitrate);
            folder.convertEnabled = updated?.convertEnabled === true;
            folder.convertFormat = folder.convertEnabled
                ? normalizeFolderConvertFormatValue(updated?.convertFormat ?? selectedConvertFormat)
                : null;
            folder.convertBitrate = folder.convertEnabled
                ? normalizeFolderConvertBitrateValue(updated?.convertBitrate ?? selectedConvertBitrate)
                : null;
            selectedConvertFormat = normalizeFolderConvertFormatValue(folder.convertFormat);
            selectedConvertBitrate = normalizeFolderConvertBitrateValue(folder.convertBitrate);
            setSingleCheckboxSelection(formatCheckboxes, selectedConvertFormat, normalizeFolderConvertFormatValue);
            setSingleCheckboxSelection(bitrateCheckboxes, selectedConvertBitrate, normalizeFolderConvertBitrateValue);
            updateConversionSummaries();
            if (successMessage) {
                showToast(successMessage);
            }
            return true;
        };
        const clearConversionAutoPersistTimer = () => {
            if (conversionAutoPersistTimer !== null) {
                globalThis.clearTimeout(conversionAutoPersistTimer);
                conversionAutoPersistTimer = null;
            }
        };
        const hasPersistedConversionSelection = () => {
            if (folder.convertEnabled !== true) {
                return false;
            }
            const persistedFormat = normalizeFolderConvertFormatValue(folder.convertFormat);
            const persistedBitrate = normalizeFolderConvertBitrateValue(folder.convertBitrate);
            return persistedFormat === selectedConvertFormat && persistedBitrate === selectedConvertBitrate;
        };
        const persistConversionSelectionWithoutClosing = async () => {
            if (conversionSaving || conversionAutoPersistInFlight || selectedMode !== conversionModeValue) {
                return;
            }
            if (!selectedConvertFormat || !selectedConvertBitrate || hasPersistedConversionSelection()) {
                return;
            }
            const formatToPersist = selectedConvertFormat;
            const bitrateToPersist = selectedConvertBitrate;

            conversionAutoPersistInFlight = true;
            try {
                const updated = await setFolderConversionSettings(
                    folder.id,
                    true,
                    formatToPersist,
                    bitrateToPersist);
                folder.convertEnabled = updated?.convertEnabled === true;
                folder.convertFormat = folder.convertEnabled
                    ? normalizeFolderConvertFormatValue(updated?.convertFormat ?? formatToPersist)
                    : null;
                folder.convertBitrate = folder.convertEnabled
                    ? normalizeFolderConvertBitrateValue(updated?.convertBitrate ?? bitrateToPersist)
                    : null;
                if (selectedConvertFormat === formatToPersist) {
                    selectedConvertFormat = normalizeFolderConvertFormatValue(folder.convertFormat);
                }
                if (selectedConvertBitrate === bitrateToPersist) {
                    selectedConvertBitrate = normalizeFolderConvertBitrateValue(folder.convertBitrate);
                }
                setSingleCheckboxSelection(formatCheckboxes, selectedConvertFormat, normalizeFolderConvertFormatValue);
                setSingleCheckboxSelection(bitrateCheckboxes, selectedConvertBitrate, normalizeFolderConvertBitrateValue);
                updateConversionSummaries();
                await clearFolderDestinationAssignments(folder);
                if (hasAutoTagProfileSelection()) {
                    await setFolderAutoTagEnabled(folder.id, true);
                    folder.autoTagEnabled = true;
                }
            } catch (error) {
                showToast(`Failed to update folder conversion: ${error?.message || error}`, true);
                if (selectedConvertFormat === formatToPersist) {
                    selectedConvertFormat = normalizeFolderConvertFormatValue(folder.convertFormat);
                }
                if (selectedConvertBitrate === bitrateToPersist) {
                    selectedConvertBitrate = normalizeFolderConvertBitrateValue(folder.convertBitrate);
                }
                setSingleCheckboxSelection(formatCheckboxes, selectedConvertFormat, normalizeFolderConvertFormatValue);
                setSingleCheckboxSelection(bitrateCheckboxes, selectedConvertBitrate, normalizeFolderConvertBitrateValue);
                updateConversionSummaries();
            } finally {
                conversionAutoPersistInFlight = false;
                if (!conversionSaving && selectedMode === conversionModeValue && selectedConvertFormat && selectedConvertBitrate && !hasPersistedConversionSelection()) {
                    scheduleConversionAutoPersist();
                }
            }
        };
        const scheduleConversionAutoPersist = () => {
            clearConversionAutoPersistTimer();
            if (selectedMode !== conversionModeValue || !selectedConvertFormat || !selectedConvertBitrate) {
                return;
            }
            conversionAutoPersistTimer = globalThis.setTimeout(async () => {
                conversionAutoPersistTimer = null;
                await persistConversionSelectionWithoutClosing();
            }, 220);
        };
        const handleSingleSelection = (checkboxes, changedCheckbox, normalizer = normalizeOptionalTextValue) => {
            if (changedCheckbox.checked) {
                checkboxes.forEach((checkbox) => {
                    if (checkbox !== changedCheckbox) {
                        checkbox.checked = false;
                    }
                });
            } else {
                changedCheckbox.checked = true;
            }
            const selected = checkboxes.find((checkbox) => checkbox.checked);
            return selected ? normalizer(selected.value) : null;
        };
        setSingleCheckboxSelection(formatCheckboxes, selectedConvertFormat, normalizeFolderConvertFormatValue);
        setSingleCheckboxSelection(bitrateCheckboxes, selectedConvertBitrate, normalizeFolderConvertBitrateValue);
        updateConversionSummaries();
        updateModeButtonSelection();
        updateQualitySummary();
        modeButtons.forEach((modeButton) => {
            modeButton.addEventListener('click', async () => {
                const newValue = (modeButton.dataset.folderModeOption || '').trim();
                if (!newValue) {
                    return;
                }
                const previousMode = selectedMode;
                if (newValue === previousMode && newValue !== conversionModeValue) {
                    return;
                }
                if (newValue === conversionModeValue) {
                    selectedMode = conversionModeValue;
                    syncConversionPickerState();
                    showToast('Select one conversion format and one conversion bitrate, then click Apply Conversion.');
                    scheduleConversionAutoPersist();
                    return;
                }

                clearConversionAutoPersistTimer();
                const previousConvertEnabled = folder.convertEnabled === true;
                const previousConvertFormat = previousConvertEnabled ? normalizeFolderConvertFormatValue(folder.convertFormat) : null;
                const previousConvertBitrate = previousConvertEnabled ? normalizeFolderConvertBitrateValue(folder.convertBitrate) : null;
                selectedMode = newValue;
                conversionSaving = true;
                syncConversionPickerState();

                try {
                    await applyFolderModeSelection(
                        folder,
                        newValue,
                        disableConversionIfEnabled,
                        hasAutoTagProfileSelection,
                        qualityLabel
                    );
                    closeThisQualityDropdown();
                    await loadDownloadLocation();
                    renderFolders();
                } catch (error) {
                    selectedMode = previousMode;
                    folder.convertEnabled = previousConvertEnabled;
                    folder.convertFormat = previousConvertEnabled ? previousConvertFormat : null;
                    folder.convertBitrate = previousConvertEnabled ? previousConvertBitrate : null;
                    selectedConvertFormat = previousConvertEnabled ? previousConvertFormat : selectedConvertFormat;
                    selectedConvertBitrate = previousConvertEnabled ? previousConvertBitrate : selectedConvertBitrate;
                    setSingleCheckboxSelection(formatCheckboxes, selectedConvertFormat, normalizeFolderConvertFormatValue);
                    setSingleCheckboxSelection(bitrateCheckboxes, selectedConvertBitrate, normalizeFolderConvertBitrateValue);
                    updateConversionSummaries();
                    showToast(`Failed to update folder: ${error?.message || error}`, true);
                } finally {
                    conversionSaving = false;
                    syncConversionPickerState();
                }
            });
        });
        formatCheckboxes.forEach((checkbox) => {
            checkbox.addEventListener('change', async () => {
                selectedConvertFormat = handleSingleSelection(formatCheckboxes, checkbox, normalizeFolderConvertFormatValue);
                updateConversionSummaries();
                scheduleConversionAutoPersist();
            });
        });
        bitrateCheckboxes.forEach((checkbox) => {
            checkbox.addEventListener('change', async () => {
                selectedConvertBitrate = handleSingleSelection(bitrateCheckboxes, checkbox, normalizeFolderConvertBitrateValue);
                updateConversionSummaries();
                scheduleConversionAutoPersist();
            });
        });
        conversionApplyButton?.addEventListener('click', async () => {
            clearConversionAutoPersistTimer();
            if (selectedMode !== conversionModeValue) {
                selectedMode = conversionModeValue;
            }
            if (!selectedConvertFormat || !selectedConvertBitrate) {
                showToast('Select one conversion format and one conversion bitrate.', true);
                syncConversionPickerState();
                return;
            }

            conversionSaving = true;
            syncConversionPickerState();
            try {
                const saved = await persistConversionIfReady('Folder conversion settings saved.');
                if (!saved) {
                    return;
                }
                await clearFolderDestinationAssignments(folder);
                if (hasAutoTagProfileSelection()) {
                    await setFolderAutoTagEnabled(folder.id, true);
                    folder.autoTagEnabled = true;
                } else {
                    showToast('Conversion settings saved. Assign a profile to enable AutoTag for this music folder.');
                }
                closeThisQualityDropdown();
                await loadDownloadLocation();
                renderFolders();
            } catch (error) {
                showToast(`Failed to update folder conversion: ${error?.message || error}`, true);
                selectedConvertFormat = normalizeFolderConvertFormatValue(folder.convertFormat);
                selectedConvertBitrate = normalizeFolderConvertBitrateValue(folder.convertBitrate);
                setSingleCheckboxSelection(formatCheckboxes, selectedConvertFormat, normalizeFolderConvertFormatValue);
                setSingleCheckboxSelection(bitrateCheckboxes, selectedConvertBitrate, normalizeFolderConvertBitrateValue);
                updateConversionSummaries();
            } finally {
                conversionSaving = false;
                syncConversionPickerState();
            }
        });
        syncConversionPickerState();
        qualitySummaryEl?.addEventListener('click', (event) => {
            event.preventDefault();
            event.stopPropagation();
            const isOpen = qualityDropdown?.classList.contains('is-open') === true;
            if (isOpen) {
                closeThisQualityDropdown();
            } else {
                openFolderQualityDropdown(qualityDropdown, qualityPanelEl, qualitySummaryEl);
                syncConversionPickerState();
                requestAnimationFrame(() => {
                    revealSelectedConversionOptions();
                });
            }
        });

        bindFolderProfileSelection(
            wrapper,
            folder,
            () => currentProfile,
            (nextProfile) => { currentProfile = nextProfile; },
            currentSchedule
        );
        bindFolderDurationSelection(wrapper, folder, folderIdKey, () => currentProfile);
        bindFolderEnhanceAction(enhanceButton, wrapper, folder);

        container.appendChild(wrapper);
    });
}

async function setFolderAutoTagEnabled(id, enabled) {
    const updated = await fetchJson(`/api/library/folders/${id}/autotag-enabled`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled })
    });
    if (globalThis.DeezSpoTag?.Download) {
        globalThis.DeezSpoTag.Download.destinationFolders = null;
    }
    emitAutoTagProfileLibrarySettingsChanged('folder-autotag-enabled');
    return updated;
}

async function setFolderAutoTagProfile(id, profileId) {
    const normalizedProfileId = (profileId || '').trim();
    const updated = await fetchJson(`/api/library/folders/${id}/profile`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ profileId: normalizedProfileId || null })
    });
    if (globalThis.DeezSpoTag?.Download) {
        globalThis.DeezSpoTag.Download.destinationFolders = null;
    }
    emitAutoTagProfileLibrarySettingsChanged('folder-autotag-profile');
    return updated;
}

async function setFolderEnabled(id, enabled, options = {}) {
    const folder = libraryState.folders.find(item => item.id === id);
    if (!folder) {
        throw new Error('Folder not found.');
    }

    const shouldReload = options.reload !== false;
    const shouldRefreshArtists = options.refreshArtists !== false;
    const shouldRefreshScanStatus = options.refreshScanStatus !== false;

    await fetchJson(`/api/library/folders/${id}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            rootPath: folder.rootPath,
            displayName: folder.displayName,
            enabled,
            libraryName: folder.libraryName ?? null,
            desiredQuality: folder.desiredQuality ?? '27',
            convertEnabled: folder.convertEnabled === true,
            convertFormat: folder.convertFormat ?? null,
            convertBitrate: folder.convertBitrate ?? null
        })
    });
    const refreshTasks = [];
    if (shouldReload) {
        refreshTasks.push(loadFolders());
    }
    if (shouldRefreshArtists) {
        refreshTasks.push(loadArtists());
    }
    if (shouldRefreshScanStatus) {
        refreshTasks.push(loadLibraryScanStatus());
    }
    if (refreshTasks.length > 0) {
        await Promise.all(refreshTasks);
    }
    emitAutoTagProfileLibrarySettingsChanged('folder-enabled');
}

async function setFolderDesiredQuality(id, desiredQuality) {
    const folder = libraryState.folders.find(item => item.id === id);
    if (!folder) {
        throw new Error('Folder not found.');
    }

    await fetchJson(`/api/library/folders/${id}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            rootPath: folder.rootPath,
            displayName: folder.displayName,
            enabled: isFolderEnabledFlag(folder.enabled),
            libraryName: folder.libraryName ?? null,
            desiredQuality: desiredQuality || '27',
            convertEnabled: folder.convertEnabled === true,
            convertFormat: folder.convertFormat ?? null,
            convertBitrate: folder.convertBitrate ?? null
        })
    });
    await loadFolders();
    emitAutoTagProfileLibrarySettingsChanged('folder-quality');
}

async function setFolderConversionSettings(id, convertEnabled, convertFormat, convertBitrate) {
    const folder = libraryState.folders.find(item => item.id === id);
    if (!folder) {
        throw new Error('Folder not found.');
    }

    const normalizedEnabled = convertEnabled === true;
    const normalizedFormat = normalizedEnabled ? normalizeFolderConvertFormatValue(convertFormat) : null;
    const normalizedBitrate = normalizedEnabled ? normalizeFolderConvertBitrateValue(convertBitrate) : null;

    const updated = await fetchJson(`/api/library/folders/${id}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            rootPath: folder.rootPath,
            displayName: folder.displayName,
            enabled: isFolderEnabledFlag(folder.enabled),
            libraryName: folder.libraryName ?? null,
            desiredQuality: folder.desiredQuality ?? '27',
            convertEnabled: normalizedEnabled,
            convertFormat: normalizedFormat,
            convertBitrate: normalizedBitrate
        })
    });
    const normalizedUpdated = updated && typeof updated === 'object'
        ? normalizeFolderConversionState(updated)
        : updated;

    if (normalizedUpdated && typeof normalizedUpdated === 'object') {
        Object.assign(folder, normalizedUpdated);
    } else {
        folder.convertEnabled = normalizedEnabled;
        folder.convertFormat = normalizedEnabled ? normalizedFormat : null;
        folder.convertBitrate = normalizedEnabled ? normalizedBitrate : null;
    }
    emitAutoTagProfileLibrarySettingsChanged('folder-conversion');
    return normalizedUpdated;
}

async function loadViewAliases() {
    const viewSelect = document.getElementById('libraryViewSelect');
    if (!viewSelect || libraryState.aliasesLoaded) {
        return;
    }
    const folders = libraryState.folders || [];
    await Promise.all(folders.map(async folder => {
        try {
            const aliases = await fetchJson(`/api/library/folders/${folder.id}/aliases`);
            libraryState.aliases.set(folder.id, aliases);
        } catch {
            libraryState.aliases.set(folder.id, []);
        }
    }));
    libraryState.aliasesLoaded = true;
}

function getFolderLabel(folder) {
    const aliases = libraryState.aliases.get(folder.id) || [];
    const firstAlias = aliases.find(alias => (alias.aliasName || '').trim());
    return firstAlias ? firstAlias.aliasName : folder.displayName;
}

function updateLibraryViewOptions() {
    const viewSelect = document.getElementById('libraryViewSelect');
    if (!viewSelect) {
        return;
    }
    const currentValue = String(viewSelect.value || '').trim();
    const storedValue = String(getStoredLibraryViewSelection() || '').trim();
    const requestedValue = (
        currentValue && currentValue !== 'main'
            ? currentValue
            : (storedValue || currentValue || 'main')
    ).trim();
    viewSelect.innerHTML = '';
    const mainOption = document.createElement('option');
    mainOption.value = 'main';
    mainOption.textContent = 'Main';
    viewSelect.appendChild(mainOption);
    (libraryState.folders || [])
        .filter(folder => isFolderEnabledFlag(folder.enabled))
        .forEach(folder => {
            const option = document.createElement('option');
            option.value = String(folder.id);
            option.textContent = getFolderLabel(folder);
            viewSelect.appendChild(option);
        });
    const selectedExists = requestedValue === 'main'
        || (libraryState.folders || []).some(folder => isFolderEnabledFlag(folder.enabled) && String(folder.id) === requestedValue);
    viewSelect.value = selectedExists ? requestedValue : 'main';
    setStoredLibraryViewSelection(viewSelect.value || 'main');
    syncLibraryScopeInLocationBar(viewSelect.value || 'main');
}

function ensureLibraryViewDefaultOption() {
    const viewSelect = document.getElementById('libraryViewSelect');
    if (!viewSelect || viewSelect.options.length > 0) {
        return;
    }

    const mainOption = document.createElement('option');
    mainOption.value = 'main';
    mainOption.textContent = 'Main';
    viewSelect.appendChild(mainOption);
    viewSelect.value = 'main';
}

function populateAlbumDestinationOptions() {
    const destinationSelect = document.getElementById('downloadDestinationAlbum');
    if (!destinationSelect) {
        return;
    }

    const enabledFolders = (libraryState.folders || []).filter(folder => isFolderEnabledFlag(folder.enabled));
    const remembered = localStorage.getItem('libraryAlbumDestinationFolderId') || '';
    destinationSelect.innerHTML = '<option value="">Default destination</option>';

    enabledFolders.forEach(folder => {
        const option = document.createElement('option');
        option.value = String(folder.id);
        option.textContent = folder.displayName;
        destinationSelect.appendChild(option);
    });

    if (remembered && enabledFolders.some(folder => String(folder.id) === remembered)) {
        destinationSelect.value = remembered;
    }

    if (!destinationSelect.dataset.bound) {
        destinationSelect.dataset.bound = 'true';
        destinationSelect.addEventListener('change', () => {
            const current = destinationSelect.value || '';
            if (current) {
                localStorage.setItem('libraryAlbumDestinationFolderId', current);
                if (globalThis.UserPrefs) globalThis.UserPrefs.set('libraryAlbumDestinationFolderId', current);
            } else {
                localStorage.removeItem('libraryAlbumDestinationFolderId');
                if (globalThis.UserPrefs) globalThis.UserPrefs.set('libraryAlbumDestinationFolderId', null);
            }
        });
    }
}

async function getArtistFolders(artistId) {
    if (libraryState.artistFolders.has(artistId)) {
        return libraryState.artistFolders.get(artistId);
    }
    try {
        const albums = await fetchJson(buildLibraryScopedUrl(`/api/library/artists/${artistId}/albums`));
        const folderSet = new Set();
        (albums || []).forEach(album => {
            (album.localFolders || []).forEach(folderName => {
                const normalizedName = String(folderName || '').trim().toLocaleLowerCase();
                if (!normalizedName) {
                    return;
                }
                (libraryState.folders || [])
                    .filter(folder => String(folder.displayName || '').trim().toLocaleLowerCase() === normalizedName)
                    .forEach(folder => folderSet.add(String(folder.id)));
            });
        });
        libraryState.artistFolders.set(artistId, folderSet);
        return folderSet;
    } catch {
        const empty = new Set();
        libraryState.artistFolders.set(artistId, empty);
        return empty;
    }
}

function updateLibraryResultsMeta(totalCount, filteredCount) {
    const meta = document.getElementById('libraryResultsMeta');
    if (!meta) {
        return;
    }
    if (!Number.isFinite(totalCount) || totalCount < 0) {
        meta.textContent = 'Loading artists...';
        return;
    }
    if (totalCount === 0) {
        meta.textContent = 'No artists in library';
        return;
    }
    if (filteredCount === totalCount) {
        meta.textContent = `${filteredCount} artist${filteredCount === 1 ? '' : 's'}`;
        return;
    }
    meta.textContent = `${filteredCount} of ${totalCount} artists`;
}

function updateLibraryLoadProgressMeta(loadedCount, totalCount) {
    const meta = document.getElementById('libraryResultsMeta');
    if (!meta) {
        return;
    }

    if (!Number.isFinite(totalCount) || totalCount <= 0) {
        meta.textContent = `Loading artists... ${loadedCount.toLocaleString()}`;
        return;
    }

    meta.textContent = `Loading artists... ${loadedCount.toLocaleString()} of ${totalCount.toLocaleString()}`;
}

function compareArtistsBySort(a, b, sortKey) {
    const first = (a?.name || '').toLocaleLowerCase();
    const second = (b?.name || '').toLocaleLowerCase();
    if (sortKey === 'name-desc') {
        return second.localeCompare(first);
    }
    return first.localeCompare(second);
}

async function applyLibraryViewFilter(requestId = null) {
    const viewSelect = document.getElementById('libraryViewSelect');
    const allArtists = Array.isArray(libraryState.allArtists) ? libraryState.allArtists : [];
    const normalizedQuery = (libraryState.artistSearchQuery || '').trim().toLocaleLowerCase();
    const sortKey = libraryState.artistSortKey || 'name-asc';
    let filteredByView = allArtists;

    const isStaleRequest = () => requestId !== null && requestId !== libraryState.artistLoadRequestId;

    if (!viewSelect) {
        if (isStaleRequest()) {
            return;
        }
        const filtered = allArtists
            .filter(artist => !normalizedQuery || (artist.name || '').toLocaleLowerCase().includes(normalizedQuery))
            .sort((a, b) => compareArtistsBySort(a, b, sortKey));
        libraryState.filteredArtists = filtered;
        updateLibraryResultsMeta(allArtists.length, filtered.length);
        renderArtistGrid(filtered);
        return;
    }

    const selected = (viewSelect.value || '').trim();
    const selectedFolderId = getSelectedLibraryViewFolderId();
    const selectedMatchesLoadedScope = String(libraryState.artistFolderScopeId ?? '') === String(selectedFolderId ?? '');
    if (selected && selected !== 'main' && !selectedMatchesLoadedScope) {
        const folderLookups = await Promise.all(allArtists.map(async artist => {
            const folders = await getArtistFolders(artist.id);
            return { artist, folders };
        }));
        if (isStaleRequest()) {
            return;
        }
        filteredByView = folderLookups
            .filter(entry => entry.folders?.has(selected))
            .map(entry => entry.artist);
    }

    if (isStaleRequest()) {
        return;
    }
    const filtered = filteredByView
        .filter(artist => !normalizedQuery || (artist.name || '').toLocaleLowerCase().includes(normalizedQuery))
        .sort((a, b) => compareArtistsBySort(a, b, sortKey));

    libraryState.filteredArtists = filtered;
    updateLibraryResultsMeta(allArtists.length, filtered.length);
    renderArtistGrid(filtered);
}

function formatRelativeTime(dateStr) {
    if (!dateStr) return null;
    const diff = Date.now() - new Date(dateStr).getTime();
    const mins = Math.floor(diff / 60000);
    if (mins < 2) return 'Just now';
    if (mins < 60) return `${mins}m ago`;
    const hrs = Math.floor(mins / 60);
    if (hrs < 24) return `${hrs}h ago`;
    const days = Math.floor(hrs / 24);
    if (days < 30) return `${days}d ago`;
    return `${Math.floor(days / 30)}mo ago`;
}

function getLibraryLoadTargets() {
    const shouldLoadFolders = document.getElementById('foldersContainer');
    const shouldLoadViewFolders = document.getElementById('libraryViewSelect');
    const shouldLoadAlbumDestination = document.getElementById('downloadDestinationAlbum');
    return {
        shouldLoadSettings: document.getElementById('livePreviewIngest') || document.getElementById('enableSignalAnalysis'),
        shouldLoadFolders,
        shouldLoadViewFolders,
        shouldLoadArtists: document.getElementById('artistsGrid'),
        shouldLoadScanStatus: document.getElementById('libraryLastScan') || document.getElementById('libraryTrackCount'),
        // Folder tabs still need download-location settings even when the hint text is removed.
        shouldLoadDownload: document.getElementById('downloadLocationHint') || shouldLoadFolders,
        shouldLoadArtistAlbums: document.getElementById('discographyGrid'),
        shouldLoadAlbumTracks: document.getElementById('albumTracks'),
        shouldLoadTrackAnalysis: document.getElementById('trackAnalysisCard'),
        shouldLoadAlbumDestination,
        downloadAlbumButton: document.getElementById('downloadAlbum'),
        viewSelect: shouldLoadViewFolders,
        searchInput: document.getElementById('librarySearchInput'),
        sortSelect: document.getElementById('librarySortSelect'),
        shouldLoadWatchlist: document.getElementById('watchlistContainer'),
        shouldLoadPlaylistWatchlist: document.getElementById('playlistWatchlistContainer'),
        shouldLoadPlaylistBlockedRules: document.getElementById('blockedWatchlistContainer'),
        shouldLoadSoundtracks: document.getElementById('soundtrackGrid'),
        shouldLoadAnalysis: document.getElementById('analysisStatus'),
        analysisButton: document.getElementById('runAnalysis'),
        shouldLoadFavorites: document.getElementById('favoritesContainer'),
        shouldDeferViewFolderLoad: !!(shouldLoadViewFolders && !shouldLoadFolders && !shouldLoadAlbumDestination)
    };
}

function shouldInitializeLibraryForCurrentPage(targets) {
    if (!targets || typeof targets !== 'object') {
        return false;
    }

    return Boolean(
        targets.shouldLoadSettings
        || targets.shouldLoadFolders
        || targets.shouldLoadViewFolders
        || targets.shouldLoadArtists
        || targets.shouldLoadScanStatus
        || targets.shouldLoadDownload
        || targets.shouldLoadArtistAlbums
        || targets.shouldLoadAlbumTracks
        || targets.shouldLoadTrackAnalysis
        || targets.shouldLoadWatchlist
        || targets.shouldLoadPlaylistWatchlist
        || targets.shouldLoadPlaylistBlockedRules
        || targets.shouldLoadSoundtracks
        || targets.shouldLoadAnalysis
        || targets.shouldLoadFavorites
        || targets.downloadAlbumButton
    );
}

function bindIndexActionsDropdown() {
    globalThis.DeezSpoTagLibraryInteractions?.bindIndexActionsDropdown?.();
}

function bindGlobalLibraryInteractionHandlers() {
    globalThis.DeezSpoTagLibraryInteractions?.bindGlobalLibraryInteractionHandlers?.({
        playSpotifyTrackInApp,
        handleSpotifyRedirect,
        openLibrarySpectrogramModal,
        playLocalLibraryTrackInApp
    });
}

function queueStandardInitialLoadTasks(targets, tasks) {
    if (targets.shouldLoadArtists) {
        tasks.push(loadArtists());
    }
    if (targets.shouldLoadScanStatus) {
        tasks.push(loadLibraryScanStatus());
    }
    if (targets.shouldLoadArtistAlbums) {
        tasks.push(loadAlbums());
    }
    if (targets.shouldLoadWatchlist) {
        tasks.push(loadWatchlist());
    }
    if (targets.shouldLoadPlaylistWatchlist) {
        tasks.push(loadPlaylistWatchlist());
    }
    if (targets.shouldLoadPlaylistBlockedRules) {
        tasks.push(loadPlaylistBlockedRules());
    }
    if (targets.shouldLoadAlbumTracks) {
        tasks.push(loadAlbum());
    }
    if (targets.shouldLoadTrackAnalysis) {
        tasks.push(loadTrackAnalysisPage());
    }
    if (targets.shouldLoadAnalysis) {
        tasks.push(loadAnalysisActivity().then(loadAnalysisStatus));
    }
    if (targets.shouldLoadFavorites) {
        tasks.push(loadFavorites());
    }
}

function bindDeferredSoundtrackInitialization(shouldLoadSoundtracks) {
    if (!shouldLoadSoundtracks) {
        return;
    }

    const soundtrackTabButton = document.getElementById('soundtracks-tab');
    const soundtrackTabPane = document.getElementById('soundtracks-content');
    if (!soundtrackTabPane || soundtrackTabPane.dataset.soundtracksDeferredBound === 'true') {
        return;
    }

    let initializationTask = null;
    const ensureInitialized = () => {
        if (!initializationTask) {
            initializationTask = initializeSoundtracksTab();
        }
        return initializationTask;
    };

    if (soundtrackTabButton) {
        soundtrackTabButton.addEventListener('shown.bs.tab', () => {
            ensureInitialized().then(() => {
                applyPendingSoundtrackScrollRestore();
            }).catch(() => {
                // Initializer failures are handled by the tab loader UI.
            });
        });
    }

    const soundtrackTabActive = soundtrackTabPane.classList.contains('active')
        || soundtrackTabPane.classList.contains('show')
        || soundtrackTabButton?.classList.contains('active') === true;
    if (soundtrackTabActive) {
        ensureInitialized().then(() => {
            applyPendingSoundtrackScrollRestore();
        }).catch(() => {
            // Initializer failures are handled by the tab loader UI.
        });
    }

    soundtrackTabPane.dataset.soundtracksDeferredBound = 'true';
}

function queueFolderAndDownloadLoadTasks(targets, tasks) {
    const shouldLoadFolderData = targets.shouldLoadFolders || targets.shouldLoadViewFolders || targets.shouldLoadAlbumDestination;
    const shouldLoadDownloadData = targets.shouldLoadDownload || targets.shouldLoadAlbumDestination;
    if (shouldLoadFolderData && shouldLoadDownloadData) {
        tasks.push(
            loadFolders(),
            loadDownloadLocation().then(() => {
                if (targets.shouldLoadFolders && document.getElementById('foldersContainer')) {
                    renderFolders();
                }
            }));
        return;
    }
    if (shouldLoadFolderData) {
        if (targets.shouldDeferViewFolderLoad) {
            void loadFolders().catch(error => {
                console.warn('Deferred library folder load failed.', error);
            });
        } else {
            tasks.push(loadFolders());
        }
    }
    if (shouldLoadDownloadData) {
        tasks.push(loadDownloadLocation());
    }
}

async function runInitialLibraryLoads(targets) {
    const tasks = [];
    if (targets.shouldLoadSettings) {
        tasks.push(loadLibrarySettings());
    }
    queueFolderAndDownloadLoadTasks(targets, tasks);
    queueStandardInitialLoadTasks(targets, tasks);
    if (tasks.length) {
        await Promise.all(tasks);
    }
}

function bindSavedPreferenceButtons() {
    const saveArtistButton = document.getElementById('saveArtistPreferences');
    if (saveArtistButton) {
        saveArtistButton.addEventListener('click', saveArtistWatchlistPreferences);
    }
    const savePlaylistButton = document.getElementById('savePlaylistPreferences');
    if (savePlaylistButton) {
        savePlaylistButton.addEventListener('click', savePlaylistWatchlistPreferences);
    }
}

async function initializeArtistAlbumsPage(shouldLoadArtistAlbums) {
    if (!shouldLoadArtistAlbums) {
        return;
    }
    const artistIdValue = document.querySelector('[data-artist-id]')?.dataset.artistId;
    const artistNameValue = document.getElementById('artistName')?.textContent?.trim();
    if (artistIdValue) {
        await loadSpotifyArtist(artistIdValue, false, false, false);
    }
    if (artistIdValue && artistNameValue) {
        await logLibraryActivity(`Artist page initialized for ${artistIdValue} (${artistNameValue}).`);
    }
}

function bindLibraryFilterEvents(viewSelect, searchInput, sortSelect) {
    if (viewSelect) {
        viewSelect.addEventListener('change', async () => {
            setStoredLibraryViewSelection(viewSelect.value || 'main');
            syncLibraryScopeInLocationBar(viewSelect.value || 'main');
            await loadArtists();
        });
    }
    if (searchInput) {
        searchInput.addEventListener('input', () => {
            if (libraryState.artistSearchTimer) {
                clearTimeout(libraryState.artistSearchTimer);
            }
            libraryState.artistSearchTimer = setTimeout(async () => {
                libraryState.artistSearchQuery = searchInput.value || '';
                await loadArtists();
            }, 180);
        });
    }
    if (sortSelect) {
        sortSelect.addEventListener('change', async () => {
            libraryState.artistSortKey = sortSelect.value || 'name-asc';
            await loadArtists();
        });
    }
}

function bindAlbumDownloadButton(downloadAlbumButton) {
    if (!downloadAlbumButton) {
        return;
    }
    downloadAlbumButton.addEventListener('click', async () => {
        const tracks = Array.from(libraryState.albumTracksById.values())
            .filter(track => {
                const source = getTrackSourceForTrack(track);
                if (!source) {
                    return false;
                }
                if (source.service !== 'apple') {
                    return !track.availableLocally;
                }
                const hasStereoVariant = track?.hasStereoVariant === true || track?.hasStereoVariant === 'true';
                const hasAtmosVariant = track?.hasAtmosVariant === true || track?.hasAtmosVariant === 'true';
                const hasAtmosCapability = track?.hasAtmos === true
                    || track?.hasAtmos === 'true'
                    || track?.appleHasAtmos === true
                    || track?.appleHasAtmos === 'true'
                    || hasAtmosVariant
                    || String(track?.audioVariant || '').toLowerCase() === 'atmos';
                const needsStereo = !hasStereoVariant;
                const needsAtmos = hasAtmosCapability && !hasAtmosVariant;
                return needsStereo || needsAtmos;
            });
        if (!tracks.length) {
            showToast('All tracks are already available locally.');
            return;
        }
        await queueAlbumDownloads(tracks);
    });
}

function startLibraryRefreshIntervals(shouldLoadAnalysis, shouldLoadScanStatus) {
    const policy = getLibraryRefreshPolicy();

    const scheduleAnalysisPoll = (delayMs) => {
        if (libraryState.analysisPollTimer) {
            globalThis.clearTimeout(libraryState.analysisPollTimer);
        }
        libraryState.analysisPollTimer = globalThis.setTimeout(async () => {
            try {
                await loadAnalysisActivity();
                await loadAnalysisStatus();
            } finally {
                const refreshedPolicy = getLibraryRefreshPolicy();
                scheduleAnalysisPoll(refreshedPolicy.analysisMs);
            }
        }, Math.max(0, delayMs));
    };

    const scheduleScanStatusPoll = (delayMs) => {
        if (libraryState.scanStatusPollTimer) {
            globalThis.clearTimeout(libraryState.scanStatusPollTimer);
        }
        libraryState.scanStatusPollTimer = globalThis.setTimeout(async () => {
            if (libraryState.scanStatusPolling) {
                const refreshedPolicy = getLibraryRefreshPolicy();
                scheduleScanStatusPoll(refreshedPolicy.scanStatusActiveMs);
                return;
            }

            libraryState.scanStatusPolling = true;
            let status = null;
            try {
                status = await loadLibraryScanStatus();
            } finally {
                libraryState.scanStatusPolling = false;
                const refreshedPolicy = getLibraryRefreshPolicy();
                if (status === null) {
                    libraryState.runtimeStatusFailureCount += 1;
                } else {
                    libraryState.runtimeStatusFailureCount = 0;
                }
                const failureBackoffFactor = Math.min(8, Math.max(1, 2 ** libraryState.runtimeStatusFailureCount));
                const baseDelay = status?.running
                    ? refreshedPolicy.scanStatusActiveMs
                    : refreshedPolicy.scanStatusIdleMs;
                const nextDelay = Math.min(120000, baseDelay * failureBackoffFactor);
                scheduleScanStatusPoll(nextDelay);
            }
        }, Math.max(0, delayMs));
    };

    if (shouldLoadAnalysis) {
        scheduleAnalysisPoll(policy.analysisMs);
    }
    if (shouldLoadScanStatus) {
        scheduleScanStatusPoll(policy.scanStatusActiveMs);
    }
}
