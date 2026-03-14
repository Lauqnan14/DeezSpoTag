const libraryState = {
    folders: [],
    libraries: [],
    aliases: new Map(),
    aliasesLoaded: false,
    downloadLocation: null,
    atmosDestinationFolderId: null,
    videoDownloadLocation: null,
    podcastDownloadLocation: null,
    unavailableAlbums: new Map(),
    imageCacheKey: null,
    spotifyResolveCache: new Map(),
    autotagProfiles: [],
    autotagDefaults: {
        defaultFileProfile: null,
        libraryProfiles: {},
        librarySchedules: {}
    },
    previewAudio: null,
    previewTrackId: null,
    previewButton: null,
    deezerAlbumIndex: new Map(),
    localArtistIndex: new Map(),
    allArtists: [],
    filteredArtists: [],
    artistFolders: new Map(),
    artistSearchQuery: '',
    artistSortKey: 'name-asc',
    artistSearchTimer: null,
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
    localTopSongTrackIndexPromise: null
};

const libraryTrackSummaryCache = new Map();
let spotifyTopTrackMatchRequestId = 0;
let spotifyTopTrackPreviewWarmupTimer = 0;

const analysisState = {
    running: false,
    lastRunUtc: null
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
    const response = await fetch(url, options);
    return parseJsonResponse(response, url);
}

async function fetchJsonOptional(url) {
    const response = await fetch(url);
    if (response.status === 404) {
        return null;
    }
    return parseJsonResponse(response, url);
}

async function parseJsonResponse(response, url) {
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
        const error = new Error(`Invalid JSON response from ${url}: ${trimmed.slice(0, 200)}`);
        error.libraryUrl = url;
        throw error;
    }
}

async function resolveSpotifyUrlToDeezer(url) {
    if (!url) {
        return null;
    }
    const cached = libraryState.spotifyResolveCache.get(url);
    if (cached) {
        return cached;
    }
    try {
        const resolved = await fetchJsonOptional(`/api/spotify/resolve-deezer?url=${encodeURIComponent(url)}`);
        if (resolved) {
            libraryState.spotifyResolveCache.set(url, resolved);
        }
        return resolved;
    } catch {
        return null;
    }
}

function parseSpotifyUrl(url) {
    if (!url) {
        return null;
    }
    const trimmed = url.trim();
    const uriMatch = trimmed.match(/open\.spotify\.com\/(track|album|artist)\/([a-z0-9]+)/i);
    if (uriMatch) {
        return { type: uriMatch[1].toLowerCase(), id: uriMatch[2] };
    }
    const uriAltMatch = trimmed.match(/spotify:(track|album|artist):([a-z0-9]+)/i);
    if (uriAltMatch) {
        return { type: uriAltMatch[1].toLowerCase(), id: uriAltMatch[2] };
    }
    return null;
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
    return {
        id: (track.id || track.Id || '').toString(),
        name: (track.name || track.Name || '').toString(),
        durationMs: Number(track.durationMs ?? track.DurationMs ?? 0) || 0,
        popularity: Number(track.popularity ?? track.Popularity ?? 0) || 0,
        previewUrl: (track.previewUrl || track.PreviewUrl || '').toString() || null,
        sourceUrl: (track.sourceUrl || track.SourceUrl || '').toString() || '',
        albumImages,
        albumName: (track.albumName || track.AlbumName || '').toString(),
        releaseDate: (track.releaseDate || track.ReleaseDate || '').toString(),
        deezerId: (track.deezerId || track.DeezerId || '').toString() || null,
        deezerUrl: (track.deezerUrl || track.DeezerUrl || '').toString() || null,
        albumGroup: (track.albumGroup || track.AlbumGroup || '').toString(),
        releaseType: (track.releaseType || track.ReleaseType || '').toString(),
        albumTrackTotal: track.albumTrackTotal ?? track.AlbumTrackTotal ?? null,
        albumId: (track.albumId || track.AlbumId || '').toString(),
        isrc: (track.isrc || track.Isrc || '').toString() || null
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
    const parsed = parseSpotifyUrl(url);
    if (!parsed?.type || !parsed.id) {
        globalThis.open(url, '_blank', 'noopener');
        return;
    }

    if (parsed.type === 'artist') {
        globalThis.location.href = `/Artist?id=${encodeURIComponent(parsed.id)}&source=spotify`;
        return;
    }

    globalThis.location.href = `/Tracklist?id=${encodeURIComponent(parsed.id)}&type=${encodeURIComponent(parsed.type || 'track')}&source=spotify`;
}

function setPreviewButtonState(button, isPlaying) {
    if (!button) {
        return;
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

function clearActivePreviewButton() {
    if (!libraryState.previewButton) {
        return;
    }

    setPreviewButtonState(libraryState.previewButton, false);
    libraryState.previewButton = null;
}

function resetPreviewState(button) {
    setPreviewButtonState(button, false);
    libraryState.previewTrackId = null;
    if (libraryState.previewButton === button) {
        libraryState.previewButton = null;
    }
}

function configurePreviewAudio(audio, previewKey, button, sourceUrl, onEnded) {
    if (libraryState.previewButton !== button) {
        clearActivePreviewButton();
    }

    libraryState.previewAudio = audio;
    libraryState.previewTrackId = previewKey;
    libraryState.previewButton = button;
    setPreviewButtonState(button, true);
    audio.pause();
    audio.src = sourceUrl;
    audio.currentTime = 0;
    audio.onended = onEnded;
}

async function startPreviewPlayback(audio, button, message) {
    try {
        await audio.play();
        return true;
    } catch {
        resetPreviewState(button);
        showToast(message, true);
        return false;
    }
}

function getNextSpotifyTopTrackButton() {
    const list = document.getElementById('spotifyTopTracksList');
    if (!list || !libraryState.previewButton) {
        return null;
    }
    const buttons = Array.from(list.querySelectorAll('button.track-play'));
    const index = buttons.indexOf(libraryState.previewButton);
    return index === -1 ? null : buttons[index + 1] || null;
}

async function resolvePlayableSpotifyTrack(url, button) {
    const cachedDeezerId = button?.dataset?.deezerId;
    if (cachedDeezerId) {
        return { deezerId: cachedDeezerId, type: 'track', available: true };
    }

    return resolveSpotifyUrlToDeezer(url);
}

async function playSpotifyTrackInApp(url, button) {
    const previewUrl = String(button?.dataset?.previewUrl || '').trim();
    if (previewUrl) {
        const audio = libraryState.previewAudio ?? new Audio();

        if (libraryState.previewTrackId === previewUrl && !audio.paused) {
            audio.pause();
            clearActivePreviewButton();
            return;
        }

        configurePreviewAudio(audio, previewUrl, button, previewUrl, () => {
            clearActivePreviewButton();
            libraryState.previewTrackId = null;
        });
        await startPreviewPlayback(audio, button, 'Unable to start playback.');
        return;
    }

    const resolved = await resolvePlayableSpotifyTrack(url, button);
    if (!resolved || resolved.available === false || resolved.type !== 'track' || !resolved.deezerId) {
        showToast('Track not available for streaming.', true);
        return;
    }

    const trackId = resolved.deezerId.toString();
    const previewKey = `deezer:${trackId}`;
    const playbackContext = globalThis.DeezerPlaybackContext;
    const streamUrl = (playbackContext && typeof playbackContext.resolveStreamUrl === 'function')
        ? await playbackContext.resolveStreamUrl(trackId, { element: button })
        : `/api/deezer/stream/${encodeURIComponent(trackId)}`;
    const audio = libraryState.previewAudio ?? new Audio();

    if (libraryState.previewTrackId === previewKey && !audio.paused) {
        audio.pause();
        clearActivePreviewButton();
        return;
    }

    configurePreviewAudio(audio, previewKey, button, streamUrl, () => {
        const nextButton = getNextSpotifyTopTrackButton();
        if (nextButton?.dataset?.spotifyUrl) {
            playSpotifyTrackInApp(nextButton.dataset.spotifyUrl, nextButton);
            return;
        }
        clearActivePreviewButton();
        libraryState.previewTrackId = null;
    });
    await startPreviewPlayback(audio, button, 'Unable to start playback.');
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
    const audio = libraryState.previewAudio ?? new Audio();

    if (libraryState.previewTrackId === previewKey && !audio.paused) {
        audio.pause();
        clearActivePreviewButton();
        return;
    }

    configurePreviewAudio(audio, previewKey, button, previewUrl, () => {
        clearActivePreviewButton();
        libraryState.previewTrackId = null;
    });
    await startPreviewPlayback(audio, button, 'Unable to start playback.');
}


async function logLibraryActivity(message, level = 'info') {
    try {
        await fetch('/api/library/scan/logs', {
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
    document.getElementById('fuzzyThreshold').value = settings.fuzzyThreshold ?? settings.FuzzyThreshold ?? 0.85;
    document.getElementById('includeAllFolders').checked = settings.includeAllFolders ?? settings.IncludeAllFolders ?? true;
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

function resolveLibraryScanCount(runningValue, totalsValue, lastCountValue, running) {
    if (running && Number.isFinite(Number(runningValue))) {
        return Number(runningValue);
    }
    if (Number.isFinite(Number(totalsValue))) {
        return Number(totalsValue);
    }
    return lastCountValue ?? 0;
}

function applyLibraryScanStatusSuccess(elements, status, stats) {
    const { lastScanEl, artistEl, albumEl, trackEl, cancelButton, indicator } = elements;
    if (cancelButton) {
        cancelButton.disabled = !status?.running;
    }
    if (lastScanEl) {
        if (status?.running) {
            const processed = status?.progress?.processedFiles ?? 0;
            const total = status?.progress?.totalFiles ?? 0;
            lastScanEl.textContent = total > 0
                ? `Running ${processed.toLocaleString()}/${total.toLocaleString()}`
                : 'Running...';
        } else {
            lastScanEl.textContent = formatTimestamp(status?.lastRunUtc);
        }
    }
    const totals = stats?.totals || null;
    const running = !!status?.running;
    const progress = status?.progress || null;
    const counts = {
        artists: resolveLibraryScanCount(progress?.artistsDetected, totals?.artists, status?.lastCounts?.artists, running),
        albums: resolveLibraryScanCount(progress?.albumsDetected, totals?.albums, status?.lastCounts?.albums, running),
        tracks: resolveLibraryScanCount(progress?.tracksDetected, totals?.tracks, status?.lastCounts?.tracks, running)
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
        return;
    }
    try {
        const [status, stats] = await Promise.all([
            fetchJson('/api/library/scan/status'),
            fetchJsonOptional('/api/library/stats')
        ]);
        applyLibraryScanStatusSuccess(elements, status, stats);
    } catch (error) {
        applyLibraryScanStatusFailure(elements);
        console.warn('Library scan status failed.', error);
    }
}

async function saveLibrarySettings() {
    const threshold = Number.parseFloat(document.getElementById('fuzzyThreshold').value || '0.85');
    const includeAll = document.getElementById('includeAllFolders').checked;
    await fetchJson('/api/library/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            fuzzyThreshold: threshold,
            includeAllFolders: includeAll
        })
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
    return {
        defaultFileProfile: typeof source.defaultFileProfile === 'string' && source.defaultFileProfile.trim()
            ? source.defaultFileProfile.trim()
            : null,
        libraryProfiles: source.libraryProfiles && typeof source.libraryProfiles === 'object'
            ? { ...source.libraryProfiles }
            : {},
        librarySchedules: source.librarySchedules && typeof source.librarySchedules === 'object'
            ? { ...source.librarySchedules }
            : {}
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
        const [profiles, defaults] = await Promise.all([
            fetchJsonOptional('/api/tagging/profiles'),
            fetchJsonOptional('/api/autotag/defaults')
        ]);
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

async function saveAutoTagFolderDefault(folderId, profileId, schedule) {
    const defaults = normalizeAutoTagDefaults(libraryState.autotagDefaults);
    const idKey = String(folderId);
    const nextProfile = (profileId || '').trim();
    const nextSchedule = (schedule || '7d').trim();

    if (nextProfile) {
        defaults.libraryProfiles[idKey] = nextProfile;
    } else {
        delete defaults.libraryProfiles[idKey];
    }
    if (nextSchedule) {
        defaults.librarySchedules[idKey] = nextSchedule;
    } else {
        delete defaults.librarySchedules[idKey];
    }

    await fetchJson('/api/autotag/defaults', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(defaults)
    });
    libraryState.autotagDefaults = defaults;
}

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

    const config = structuredClone(baseConfig);
    const gapFillTags = Array.isArray(config.gapFillTags)
        ? config.gapFillTags.filter((tag) => typeof tag === 'string' && tag.trim().length > 0)
        : [];

    if (!gapFillTags.length) {
        throw new Error(`Profile "${resolvedProfileName}" has no Library Enhancement tags.`);
    }

    config.downloadTags = [];
    config.tags = [];
    config.gapFillTags = [...new Set(gapFillTags)];

    const response = await fetchJson('/api/autotag/start', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            path: rootPath,
            config,
            profileId: String(profile?.id || resolvedProfileReference || '').trim() || null
        })
    });

    const jobId = response?.jobId ? String(response.jobId) : '';
    if (!jobId) {
        throw new Error('AutoTag did not return a job id.');
    }

    return { jobId, profileName: resolvedProfileName };
}

async function loadFolders() {
    const folders = await fetchJson('/api/library/folders');
    libraryState.folders = Array.isArray(folders)
        ? folders.map(normalizeFolderConversionState)
        : [];
    libraryState.aliasesLoaded = false;
    libraryState.aliases.clear();
    if (document.getElementById('foldersContainer')) {
        await loadAutoTagFolderDefaults();
        renderFolders();
    }
    await loadViewAliases();
    updateLibraryViewOptions();
    populateAlbumDestinationOptions();
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
    const artists = await fetchJson('/api/library/artists?availability=local');
    libraryState.allArtists = Array.isArray(artists) ? artists : [];
    libraryState.artistFolders.clear();
    await applyLibraryViewFilter();
}

async function runLocalScan(refreshImages = false, reset = false) {
    try {
        const params = new URLSearchParams();
        if (refreshImages) {
            params.set('refreshImages', 'true');
        }
        if (reset) {
            params.set('reset', 'true');
        }
        const suffix = params.toString();
        const url = suffix ? `/api/library/scan?${suffix}` : '/api/library/scan';
        const scanStartedAt = Date.now();
        showToast('Library scan started.');
        await fetchJson(url, { method: 'POST', keepalive: true });
        waitForScanCompletion(scanStartedAt);
        if (reset && refreshImages) {
            showToast('Library data reset and images refreshed.');
        } else if (reset) {
            showToast('Library data reset and refreshed.');
        } else {
            showToast(refreshImages ? 'Images refreshed.' : 'Library refreshed.');
        }
    } catch (error) {
        showToast(`Refresh failed: ${error.message}`, true);
    }
}

async function waitForScanCompletion(startedAtMs) {
    const deadline = startedAtMs + (15 * 60 * 1000);
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
        await new Promise(resolve => setTimeout(resolve, 3000));
    }
    showToast('Library scan still running.', true);
    await refreshViews();
}

function renderArtistGrid(artists) {
    const container = document.getElementById('artistsGrid');
    if (!container) {
        return;
    }
    container.innerHTML = '';
    if (!artists.length) {
        const hasFilter = !!(libraryState.artistSearchQuery || '').trim();
        container.innerHTML = hasFilter
            ? '<p class="library-empty-note">No artists match your filter.</p>'
            : '<p class="library-empty-note">No local artists yet.</p>';
        return;
    }

    artists.forEach(artist => {
        const card = document.createElement('div');
        card.className = 'artist-card ds-tile';
        const initials = artist.name.split(' ').map(part => part[0]).slice(0, 2).join('').toUpperCase();
        const coverPath = artist.preferredImagePath
            ? `/api/library/image?path=${encodeURIComponent(artist.preferredImagePath)}&size=240`
            : '';
        const coverUrl = coverPath ? appendCacheKey(coverPath) : '';
        const imageMarkup = artist.preferredImagePath
            ? `<img src="${coverUrl}" alt="${escapeHtml(artist.name || 'Artist')}" loading="lazy" decoding="async" />`
            : `<div class="artist-initials">${initials || '?'}</div>`;
        card.innerHTML = `
            ${imageMarkup}
            <strong>${escapeHtml(artist.name || 'Unknown Artist')}</strong>
        `;
        card.addEventListener('click', () => {
            globalThis.location.href = `/Library/Artist/${artist.id}`;
        });
        container.appendChild(card);
    });
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
        fetchJson(`/api/library/artists/${artistIdValue}/albums`),
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
        avatarEl.innerHTML = `<img src="${avatarPath}" alt="${resolvedArtist.name || 'Artist'}" loading="lazy" decoding="async" />`;
        avatarEl.dataset.localAvatar = 'true';
    }

    const bgEl = document.querySelector('.artist-page');
    if (bgEl && libraryState.artistVisuals.preferredBackgroundPath) {
        const backgroundUrl = appendCacheKey(buildLibraryImageUrl(libraryState.artistVisuals.preferredBackgroundPath));
        bgEl.style.setProperty('--hero-url', `url('${backgroundUrl}')`);
        bgEl.dataset.localBackground = 'true';
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
            showToast(`${missingItems.join(' and ')} not set in app visuals, pushing remaining items only.`, true);
        }

        const { includeAvatar, includeBackground, includeBio, payload } = buildSpotifySyncPayload(
            artistId,
            targetSelect.value,
            selection
        );
        if (!includeAvatar && !includeBackground && !includeBio) {
            showToast('Nothing to sync. Set avatar/background in Visuals or enable Background Info.', true);
            return;
        }

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

function normalizeSpotifyBiographyText(biography) {
    const biographyRaw = typeof biography === 'string' ? biography.trim() : '';
    if (!biographyRaw) {
        return '';
    }

    const temp = document.createElement('div');
    temp.innerHTML = biographyRaw;
    return (temp.textContent || '').replaceAll(/[\s\u00A0]+/g, ' ').trim();
}

function setSpotifyArtistLinkState(artist) {
    const spotifyIdEl = document.getElementById('artistSpotifyId');
    const spotifyActionEl = document.getElementById('artistSpotifyAction');
    if (spotifyIdEl && artist.id) {
        spotifyIdEl.href = `https://open.spotify.com/artist/${artist.id}`;
        spotifyIdEl.style.display = 'inline-flex';
        spotifyIdEl.dataset.spotifyId = artist.id;
        if (!spotifyIdEl.dataset.defaultLabel) {
            spotifyIdEl.dataset.defaultLabel = spotifyIdEl.innerHTML;
        }
        if (spotifyIdEl.dataset.defaultLabel) {
            spotifyIdEl.innerHTML = spotifyIdEl.dataset.defaultLabel;
        }
        spotifyIdEl.title = `Spotify ID: ${artist.id}`;
    }
    if (spotifyActionEl && artist.id) {
        spotifyActionEl.href = `https://open.spotify.com/artist/${artist.id}`;
        spotifyActionEl.style.display = 'flex';
    }
}

function setSpotifyArtistGenres(genres) {
    const genresEl = document.getElementById('artistGenres');
    if (!genresEl) {
        return;
    }

    const genreList = Array.isArray(genres) ? genres : [];
    genresEl.innerHTML = genreList.length
        ? genreList.slice(0, 6).map((genre) => `<span class="genre-tag">${genre}</span>`).join('')
        : '';
}

function setSpotifyArtistStats(artist) {
    [
        ['artistFollowers', typeof artist.followers === 'number' ? formatCompactNumber(artist.followers) : null],
        ['artistPopularity', typeof artist.popularity === 'number' ? artist.popularity.toString() : null],
        ['artistMonthlyListeners', typeof artist.monthlyListeners === 'number' ? formatCompactNumber(artist.monthlyListeners) : '—'],
        ['artistRank', typeof artist.rank === 'number' ? `#${artist.rank.toLocaleString()}` : '—'],
        ['artistTotalAlbums', typeof artist.totalAlbums === 'number' ? artist.totalAlbums.toString() : '—']
    ].forEach(([id, value]) => {
        const element = document.getElementById(id);
        if (element && value !== null) {
            element.textContent = value;
        }
    });

    const verifiedBadgeEl = document.getElementById('artistVerifiedBadge');
    if (verifiedBadgeEl) {
        verifiedBadgeEl.style.display = artist.verified === true ? 'flex' : 'none';
    }
}

function setSpotifyArtistBiography(biography) {
    const bioEl = document.getElementById('artistBiography');
    if (!bioEl) {
        return;
    }

    const biographyText = normalizeSpotifyBiographyText(biography);
    const hasBiography = biographyText && !/^n\/?a$/i.test(biographyText);
    bioEl.textContent = hasBiography ? biographyText : 'Biography unavailable from Spotify.';
    const bioPanel = document.getElementById('spotifyBioPanel');
    if (bioPanel) {
        bioPanel.style.display = 'block';
    }
}

function applySpotifyArtistProfile(artist) {
    if (!artist) {
        return;
    }
    libraryState.currentSpotifyArtist = artist;
    updateTopSongsTracklistLink(artist);
    setSpotifyArtistLinkState(artist);
    setSpotifyArtistGenres(artist.genres);
    setSpotifyArtistStats(artist);
    setSpotifyArtistBiography(artist.biography);

    const bestImage = selectImage(artist.images, 'large');
    const avatarImage = selectImage(artist.images, 'medium') || bestImage;

    const avatarEl = document.getElementById('artistAvatar');
    if (avatarEl && avatarImage && avatarEl.dataset.localAvatar !== 'true') {
        avatarEl.innerHTML = `<img src="${avatarImage}" alt="${artist.name || 'Artist'}" loading="lazy" decoding="async" />`;
    }

    libraryState.artistVisuals.headerImageUrl = artist.headerImageUrl || null;
    const spotifyImages = [];
    const pushSpotifyVisual = (url, label) => {
        const value = (url || '').toString().trim();
        if (!value) {
            return;
        }
        spotifyImages.push({
            url: value,
            label,
            source: 'spotify'
        });
    };
    pushSpotifyVisual(bestImage, artist.name ? `Spotify • ${artist.name}` : 'Spotify');
    pushSpotifyVisual(artist.headerImageUrl, artist.name ? `Spotify • ${artist.name} header` : 'Spotify header');
    const galleryRaw = Array.isArray(artist.gallery) ? artist.gallery : [];
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
    libraryState.artistVisuals.gallery = gallery;
    gallery.forEach((url, index) => {
        pushSpotifyVisual(url, artist.name
            ? `Spotify • ${artist.name} gallery ${index + 1}`
            : `Spotify gallery ${index + 1}`);
    });
    libraryState.artistVisuals.spotifyImages = spotifyImages;

    const bgEl = document.querySelector('.artist-page');
    const heroImage = artist.headerImageUrl || bestImage;
    if (bgEl && heroImage && bgEl.dataset.localBackground !== 'true') {
        bgEl.style.setProperty('--hero-url', `url('${heroImage}')`);
    }

    const artistIdValue = document.querySelector('.artist-page')?.dataset?.artistId || '';
    renderArtistVisualPicker(artistIdValue);
    applyStoredArtistVisuals(artistIdValue);
    loadExternalArtistVisuals(artist?.name || '', artistIdValue);
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
        const image = selectImage(artist.images, 'medium') || selectImage(artist.images, 'small');
        const coverMarkup = image
            ? `<img src="${image}" alt="${artist.name}" loading="lazy" decoding="async" />`
            : `<div class="artist-initials">${getInitials(artist.name)}</div>`;
        const normalized = normalizeArtistName(artist.name);
        const localId = normalized ? libraryState.localArtistIndex.get(normalized) : null;
        if (localId) {
            return `
                <div class="related-artist-card">
                    <a href="/Library/Artist/${localId}">
                        <div class="related-artist-card__avatar">${coverMarkup}</div>
                        <div class="related-artist-card__name">${artist.name}</div>
                    </a>
                </div>
            `;
        }
        const spotifyUrl = artist.sourceUrl || '';
        const deezerId = artist.deezerId || '';
        const spotifyId = artist.id || '';
        return `
            <div class="related-artist-card">
                <button class="related-artist-link" type="button" data-spotify-url="${spotifyUrl}" data-deezer-id="${deezerId}" data-spotify-id="${spotifyId}">
                    <div class="related-artist-card__avatar">${coverMarkup}</div>
                    <div class="related-artist-card__name">${artist.name}</div>
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
            const parsed = parseSpotifyUrl(spotifyUrl || '');
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
        const image = selectImage(track.albumImages, 'small');
        const cover = image ? `<img src="${image}" alt="${escapeHtml(track.name || '')}" loading="lazy" decoding="async" />` : '<div class="top-song-item__placeholder"></div>';
        const dataAttrs = track.sourceUrl ? `data-spotify-url="${track.sourceUrl}"` : '';
        const previewUrl = (track.previewUrl || track.preview || '').toString().trim();
        const previewAttr = previewUrl ? ` data-preview-url="${escapeHtml(previewUrl)}"` : '';
        const playButton = track.sourceUrl
            ? `<button class="top-song-item__play track-action track-play" type="button" data-spotify-url="${track.sourceUrl}"${previewAttr} aria-label="Play ${escapeHtml(track.name || 'track')} preview">
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

    void markSpotifyTopTracksInLibrary(safeTracks.slice(0, 9), _artistProfile);
    scheduleSpotifyTopTrackPreviewWarmup();
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

    const runWarmup = () => {
        spotifyTopTrackPreviewWarmupTimer = 0;
        void primeSpotifyTrackPreviews();
    };

    if (typeof globalThis.requestIdleCallback === 'function') {
        spotifyTopTrackPreviewWarmupTimer = globalThis.setTimeout(() => {
            globalThis.requestIdleCallback(runWarmup, { timeout: 2000 });
        }, 900);
        return;
    }

    spotifyTopTrackPreviewWarmupTimer = globalThis.setTimeout(runWarmup, 1200);
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

async function primeSpotifyTrackPreviews() {
    const list = document.getElementById('spotifyTopTracksList');
    if (!list) {
        return;
    }
    const playButtons = Array.from(list.querySelectorAll('button.track-play[data-spotify-url]'));
    const elements = playButtons.length > 0
        ? playButtons
        : Array.from(list.querySelectorAll('[data-spotify-url]'));
    if (elements.length === 0) {
        return;
    }

    const queue = elements
        .map(el => ({ el, url: el.dataset.spotifyUrl }))
        .filter(entry => entry.url && !entry.el.dataset.deezerId);

    if (queue.length === 0) {
        return;
    }

    const concurrency = 2;
    let cursor = 0;
    const workers = Array.from({ length: Math.min(concurrency, queue.length) }, async () => {
        while (cursor < queue.length) {
            const current = queue[cursor++];
            try {
                const resolved = await resolveSpotifyUrlToDeezer(current.url);
                if (resolved?.type === 'track' && resolved?.deezerId) {
                    const deezerId = resolved.deezerId.toString();
                    current.el.dataset.deezerId = deezerId;
                    const playbackContext = globalThis.DeezerPlaybackContext;
                    if (playbackContext && typeof playbackContext.fetchContext === 'function') {
                        const context = await playbackContext.fetchContext(deezerId);
                        if (context && typeof playbackContext.applyContextToElement === 'function') {
                            playbackContext.applyContextToElement(current.el, context);
                        }
                    }
                }
            } catch {
                // Best-effort prefetch; playback will still resolve on demand.
            }
        }
    });

    await Promise.all(workers);
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
        panel.innerHTML = `
            <div class="panel-header">
                <h2>${title}</h2>
            </div>
            <div class="album-grid neo-grid"></div>
        `;
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
        const cover = selectImage(album.images, 'small');
        const coverMarkup = cover
            ? `<img src="${cover}" alt="${album.name}" loading="lazy" decoding="async" />`
            : '<div class="artist-initials">AL</div>';
        const titleMarkup = `<div class="album-title"><span class="artist-marquee">${album.name}</span></div>`;
        const year = album.releaseDate ? new Date(album.releaseDate).getFullYear() : '';
        const subtitleText = year ? `${year}` : '';
        const subtitleMarkup = subtitleText ? `<div class="album-subtitle">${escapeHtml(subtitleText)}</div>` : '';
        const safeTitle = (album.name || '').replaceAll('"', '&quot;');
        return `
            <div class="album-card ds-tile"${album.sourceUrl ? ` data-spotify-url="${album.sourceUrl}" data-spotify-title="${safeTitle}"` : ''}>
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

    const cover = selectImage(latest.images, 'medium');
    const coverMarkup = cover
        ? `<img src="${cover}" alt="${escapeHtml(latest.name || '')}" loading="lazy" decoding="async" />`
        : '<div class="artist-initials">AL</div>';

    const formattedDate = formatSpotifyReleaseDate(latest.releaseDate);
    const typeLabel = getSpotifyReleaseTypeLabel(latest.albumGroup);
    applyLatestReleaseInteractivity(latestContainer, latest);

    latestContainer.innerHTML = `
        <div class="latest-release__artwork">${coverMarkup}</div>
        <div class="latest-release__meta">
            ${formattedDate ? `<div class="latest-release__date">${formattedDate}</div>` : ''}
            <div class="latest-release__type">${typeLabel}</div>
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
    if (album?.sourceUrl) {
        container.dataset.spotifyUrl = album.sourceUrl;
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

function initArtistActionsDropdown() {
    const toggleButton = document.getElementById('artistActionsToggle');
    const dropdown = document.getElementById('artistActionsDropdown');
    const cacheRefreshButton = document.getElementById('spotify-cache-refresh-button');
    const cachePanel = document.getElementById('spotify-cache-panel');

    if (!toggleButton || !dropdown) {
        return;
    }

    toggleButton.addEventListener('click', (event) => {
        event.stopPropagation();
        const isOpen = dropdown.classList.toggle('is-open');
        toggleButton.setAttribute('aria-expanded', isOpen);
        if (isOpen && cachePanel) {
            cachePanel.classList.add('is-open');
        }
    });

    document.addEventListener('click', (event) => {
        if (!dropdown.contains(event.target) && !toggleButton.contains(event.target)) {
            dropdown.classList.remove('is-open');
            toggleButton.setAttribute('aria-expanded', 'false');
        }
    });

    if (cacheRefreshButton && cachePanel) {
        cacheRefreshButton.addEventListener('click', () => {
            cachePanel.classList.add('is-open');
        });
    }
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
    const includeBio = document.getElementById('sync-include-bio')?.checked ?? false;
    const prefs = loadArtistVisualPrefs(artistId) || {};
    const serverAvatarPath = (libraryState.artistVisuals?.preferredAvatarPath || '').toString().trim();
    const serverBackgroundPath = (libraryState.artistVisuals?.preferredBackgroundPath || '').toString().trim();

    return {
        requestedAvatar,
        requestedBackground,
        includeBio,
        avatarImagePath: requestedAvatar
            ? resolveManagedArtistVisualPath(artistId, prefs.avatarPath, serverAvatarPath)
            : null,
        backgroundImagePath: requestedBackground
            ? resolveManagedArtistVisualPath(artistId, prefs.backgroundPath, serverBackgroundPath)
            : null,
        biography: includeBio
            ? (libraryState.currentSpotifyArtist?.biography || '').toString().trim() || null
            : null
    };
}

function getSpotifySyncMissingItems(selection) {
    const missingItems = [];
    if (selection.requestedAvatar && !selection.avatarImagePath) {
        missingItems.push('avatar');
    }
    if (selection.requestedBackground && !selection.backgroundImagePath) {
        missingItems.push('background art');
    }
    return missingItems;
}

function validateSpotifySyncSelection(selection, missingItems) {
    if (!selection.requestedAvatar && !selection.requestedBackground && !selection.includeBio) {
        showToast('Select at least one item to sync.', true);
        return false;
    }
    if (selection.includeBio && !selection.biography) {
        showToast('No biography loaded yet — load the artist page fully first.', true);
        return false;
    }
    if (missingItems.length > 0 && !selection.includeBio) {
        showToast(`Set ${missingItems.join(' and ')} in Visuals first, then push again.`, true);
        return false;
    }
    return true;
}

function buildSpotifySyncPayload(artistId, target, selection) {
    const includeAvatar = selection.requestedAvatar && !!selection.avatarImagePath;
    const includeBackground = selection.requestedBackground && !!selection.backgroundImagePath;
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
            avatarVisualUrl: includeAvatar ? buildLibraryImageUrl(selection.avatarImagePath, 320) : null,
            backgroundImagePath: selection.backgroundImagePath || null,
            backgroundVisualUrl: includeBackground ? buildLibraryImageUrl(selection.backgroundImagePath) : null,
            biography: selection.biography || null,
            target: target || 'plex'
        }
    };
}

function showSpotifySyncWarnings(result) {
    const warnings = Array.isArray(result?.warnings) ? result.warnings.filter(Boolean) : [];
    warnings.forEach((warning) => showToast(warning, true));
}

function showSpotifySyncUpdateResult(result) {
    const updatedParts = [];
    if (result?.avatarUpdated) updatedParts.push('avatar');
    if (result?.backgroundUpdated) updatedParts.push('background art');
    if (result?.bioUpdated) updatedParts.push('background info');

    if (updatedParts.length > 0) {
        showToast(`Server updated: ${updatedParts.join(' + ')}.`, false);
    } else {
        showToast('Push completed — no items were updated on the media server.', true);
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
            avatarEl.dataset.localAvatar = 'true';
            avatarEl.innerHTML = `<img src="${src}" alt="Artist avatar" loading="lazy" decoding="async" />`;
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
                bgEl.style.setProperty('--hero-url', `url('${url}')`);
                bgEl.dataset.localBackground = 'true';
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
                    bgEl.style.setProperty('--hero-url', `url('${fallback}')`);
                }
                delete bgEl.dataset.localBackground;
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
        const src = appendCacheKey(item.url);
        const safeUrl = item.url.replaceAll('"', '&quot;');
        const safePath = (item.path || '').toString().replaceAll('"', '&quot;');
        const safeSource = (item.source || '').toString().replaceAll('"', '&quot;');
        const safeLabel = escapeHtml(item.label || 'Artist visual');
        return `
            <div class="visual-tile" title="${safeLabel}">
                <img src="${src}" alt="${safeLabel}" loading="lazy" decoding="async" />
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
        const folderPills = (album.localFolders || []).map(folder => `<span class="folder-pill">${folder}</span>`).join('');
        const showFolderPills = !document.querySelector('.artist-page');
        const folderMarkup = showFolderPills && folderPills ? `<div class="folder-pill-group">${folderPills}</div>` : '';
        const coverUrl = album.preferredCoverPath
            ? appendCacheKey(`/api/library/image?path=${encodeURIComponent(album.preferredCoverPath)}&size=240`)
            : (album.coverUrl || null);
        const coverMarkup = coverUrl
            ? `<img src="${coverUrl}" alt="${album.title}" loading="lazy" decoding="async" />`
            : '<div class="artist-initials">AL</div>';
        let cardAttrs = '';
        if (linkToAlbum) {
            cardAttrs = ` data-album-id="${album.id}"`;
        } else if (linkToTracklist) {
            cardAttrs = ` data-tracklist-id="${album.id}"`;
        }
        const folderName = (album.localFolders || [])[0] || '';
        const year = album.releaseDate ? new Date(album.releaseDate).getFullYear() : '';
        const subtitleText = folderName || (year ? `${year}` : '');
        const subtitleMarkup = subtitleText ? `<div class="album-subtitle">${escapeHtml(subtitleText)}</div>` : '';
        return `
            <div class="album-card ds-tile"${cardAttrs}>
                ${coverMarkup}
                <div class="album-title"><span class="artist-marquee">${album.title}</span></div>
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
        merged.push({
            id: album.id,
            title: album.title || album.name || '',
            name: album.title || album.name || '',
            releaseDate: album.releaseDate || spotifyMatch?.releaseDate || '',
            images: album.images || [],
            coverUrl: album.preferredCoverPath
                ? appendCacheKey(`/api/library/image?path=${encodeURIComponent(album.preferredCoverPath)}&size=240`)
                : (album.coverUrl || null),
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
            globalThis.location.href = `/Library/Album/${encodeURIComponent(albumId)}`;
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
        return ` data-album-id="${album.localId}"`;
    }
    if (album.deezerId) {
        return ` data-tracklist-id="${album.deezerId}"`;
    }
    if (album.sourceUrl) {
        return ` data-spotify-url="${album.sourceUrl}"`;
    }
    return '';
}

function renderDiscographyAlbumCard(album, availabilityFilter) {
    const cover = album.coverUrl || selectImage(album.images, 'small');
    const coverMarkup = cover
        ? `<img src="${cover}" alt="${escapeHtml(album.title)}" loading="lazy" decoding="async" />`
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
        const albumArtworkUrl = appendCacheKey(albumArtworkPath);
        artworkEl.innerHTML = `<img src="${albumArtworkUrl}" alt="${escapeHtml(album.title || 'Album')}" loading="eager" />`;
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

    const artistUrl = `/Library/Artist/${album.artistId}`;
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
               <button class="library-track-play track-action track-play" type="button" data-library-play-track="${track.id}" data-library-play-path="${escapeHtml(rowFilePath)}" aria-label="Play ${playLabel}">
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
            <div class="track-row${track.availableLocally ? ' track-row--local' : ''}" data-track-id="${track.id}" data-track-variant-key="${escapeHtml(rowKey)}" data-track-primary="${track.isPrimaryVariant ? 'true' : 'false'}">
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
                            <a href="${spectrogramUrl}"
                               data-library-spectrogram-url="${spectrogramUrl}"
                               data-library-spectrogram-title="${escapeHtml(track.title || 'Track')}"
                               data-library-track-id="${track.id}"
                               data-library-track-file-path="${escapeHtml(rowFilePath)}"
                            >View Spectrogram</a>
                            <a href="${lrcEditorUrl}">Open LRC Editor</a>
                            <a href="${tagEditorUrl}">Open Tag Editor</a>
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
        if (item.imageUrl) {
            const img = document.createElement('img');
            img.src = item.imageUrl;
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

    modal.style.display = 'block';
}

function resolveFolderDestinationFlags(folder) {
    const desiredQuality = String(folder.desiredQuality || '27');
    const normalizedPath = normalizePath(folder.rootPath || '');
    const currentVideoPath = normalizePath(libraryState.videoDownloadLocation || '');
    const currentPodcastPath = normalizePath(libraryState.podcastDownloadLocation || '');
    const isAtmosDestination = folder.id === libraryState.atmosDestinationFolderId || desiredQuality.toUpperCase() === 'ATMOS';
    const isVideoDestination = normalizedPath.length > 0
        && (normalizedPath === currentVideoPath || desiredQuality.toUpperCase() === 'VIDEO');
    const isPodcastDestination = normalizedPath.length > 0
        && (normalizedPath === currentPodcastPath || desiredQuality.toUpperCase() === 'PODCAST');

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
        qualityField.value = (flags.isAtmosDestination || flags.isVideoDestination || flags.isPodcastDestination)
            ? '27'
            : flags.desiredQuality;
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
    modal.style.display = 'none';
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
    } catch (error) {
        showToast(`Failed to save folder: ${error.message}`, true);
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
    let enabled = true;
    if (isEdit && existingFolder) {
        enabled = isFolderEnabledFlag(existingFolder.enabled);
    }
    return {
        editId,
        isEdit,
        existingFolder,
        rootPath,
        displayName: document.getElementById('folderName').value.trim() || rootPath.split('/').pop(),
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
    let isBlocked = false;

    if (libraryState.downloadLocation && rootPath.length > 0) {
        const normalizedRoot = normalizePath(rootPath);
        const normalizedDownload = normalizePath(libraryState.downloadLocation);
        isBlocked = normalizedRoot === normalizedDownload;
    }

    saveButton.disabled = rootPath.length === 0 || isBlocked;
}

async function loadDownloadLocation() {
    try {
        const data = await fetchJson('/api/getSettings');
        const settings = data?.settings || {};
        libraryState.downloadLocation = settings.downloadLocation || null;
        libraryState.atmosDestinationFolderId = settings?.multiQuality?.secondaryDestinationFolderId ?? null;
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
}

function bindFolderChangeInput(input, action) {
    if (!input) {
        return;
    }
    input.addEventListener('change', action);
}

function getLibraryBootstrapElements() {
    const folderModal = document.getElementById('folderModal');
    return {
        refreshButton: document.getElementById('refreshLibrary'),
        scanButton: document.getElementById('scanLibrary'),
        cancelScanButton: document.getElementById('cancelLibraryScan'),
        refreshImagesButton: document.getElementById('refreshImages'),
        cleanupButton: document.getElementById('cleanupLibrary'),
        saveButton: document.getElementById('saveSettings'),
        chooseFolderButton: document.getElementById('chooseFolder'),
        saveFolderButton: document.getElementById('saveFolder'),
        addFolderButton: document.getElementById('addFolderBtn'),
        folderModalCloseButton: document.getElementById('folderModalClose'),
        folderModalCancelButton: document.getElementById('folderModalCancel'),
        folderModal,
        folderModalBackdrop: folderModal?.querySelector('.folder-modal-backdrop'),
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
            const parts = selected.split(/[\\/]+/).filter(Boolean);
            folderNameInput.value = parts[parts.length - 1] || selected;
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
    libraryState.videoDownloadLocation = settings.video?.videoDownloadLocation || null;
    libraryState.podcastDownloadLocation = settings.podcast?.downloadLocation || null;
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
            <span>${alias.aliasName}</span>
            <button class="btn-danger action-btn action-btn-sm" data-alias-id="${alias.id}">Remove</button>
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

function bindFolderAutoTagToggle(wrapper, folder, canEnableAutoTag) {
    const enabledToggle = wrapper.querySelector('[data-folder-enabled]');
    if (!enabledToggle) {
        return;
    }
    enabledToggle.addEventListener('change', async () => {
        const enabled = enabledToggle.checked;
        if (enabled && !canEnableAutoTag) {
            enabledToggle.checked = false;
            showToast('Assign an AutoTag profile before enabling AutoTag for music folders.', true);
            return;
        }
        enabledToggle.disabled = true;
        try {
            const updated = await setFolderAutoTagEnabled(folder.id, enabled);
            const persistedEnabled = typeof updated?.autoTagEnabled === 'boolean'
                ? updated.autoTagEnabled
                : enabled;
            enabledToggle.checked = persistedEnabled;
            folder.autoTagEnabled = persistedEnabled;
            showToast(`Folder AutoTag ${persistedEnabled ? 'enabled' : 'disabled'}.`);
        } catch (error) {
            enabledToggle.checked = !enabled;
            showToast(`Failed to update folder: ${error?.message || error}`, true);
        } finally {
            enabledToggle.disabled = !canEnableAutoTag;
        }
    });
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
    const currentSchedule = libraryState.autotagDefaults?.librarySchedules?.[folderIdKey] || '7d';
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
    const enabledId = `folder-enabled-${folder.id}`;
    const currentQuality = String(folder.desiredQuality ?? '27');
    const folderIdKey = String(folder.id);
    const currentProfile = resolveFolderProfileReference(folder);
    const currentSchedule = context.folderSchedules[folderIdKey] || '7d';
    const profileOptions = context.profileOptionsSource.length
        ? ['<option value="">No profile</option>', ...context.profileOptionsSource.map((profile) => `<option value="${escapeHtml(profile.id)}">${escapeHtml(profile.name)}</option>`)].join('')
        : '<option value="">No profiles</option>';
    const scheduleOptions = context.scheduleChoices
        .map((choice) => `<option value="${choice.value}" ${choice.value === currentSchedule ? 'selected' : ''}>${choice.label}</option>`)
        .join('');
    const destinationFlags = context.getFolderDestinationFlags(folder);
    const { isVideoDestination, isPodcastDestination } = destinationFlags;
    const requiresProfileForAutoTag = !isVideoDestination && !isPodcastDestination;
    const showProfileSelector = requiresProfileForAutoTag;
    const hasAssignedProfile = currentProfile.trim().length > 0;
    const canEnableAutoTag = !requiresProfileForAutoTag || hasAssignedProfile;
    const currentAutoTagEnabled = canEnableAutoTag && folder.autoTagEnabled !== false;
    const autoTagToggleTitle = canEnableAutoTag
        ? 'Enable/disable AutoTag for this folder'
        : 'Assign an AutoTag profile first (music folders only).';
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
        enabledId,
        folderIdKey,
        currentProfile,
        currentSchedule,
        canEnableAutoTag,
        currentAutoTagEnabled,
        autoTagToggleTitle,
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
                <span class="folder-label">${folder.displayName}</span>
                <span>${folder.rootPath}</span>
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
                    <select class="folder-duration-select" data-folder-duration ${viewModel.currentAutoTagEnabled ? '' : 'disabled'}>
                        ${viewModel.scheduleOptions}
                    </select>
                </span>
                <span>
                    <label class="switch" title="${escapeHtml(viewModel.autoTagToggleTitle)}">
                        <input id="${viewModel.enabledId}" type="checkbox" ${viewModel.currentAutoTagEnabled ? 'checked' : ''} ${viewModel.canEnableAutoTag ? '' : 'disabled'} data-folder-enabled />
                        <span class="slider"></span>
                    </label>
                </span>
                <span class="actions">
                    <button class="action-btn action-btn-sm folder-action" data-enhance title="Run enhancement now" ${viewModel.currentAutoTagEnabled ? '' : 'disabled'}>
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
        { value: '7d', label: '1 week' },
        { value: '14d', label: '2 weeks' },
        { value: '30d', label: '1 month' },
        { value: '90d', label: '3 months' },
        { value: '180d', label: '6 months' }
    ];
    const getFolderDestinationFlags = (folder) => {
        const currentQuality = String(folder?.desiredQuality ?? '27').toUpperCase();
        const isAtmosDestination = folder?.id === libraryState.atmosDestinationFolderId || currentQuality === 'ATMOS';
        const isVideoDestination = normalizePath(folder?.rootPath) === normalizePath(libraryState.videoDownloadLocation || '')
            || currentQuality === 'VIDEO';
        const isPodcastDestination = normalizePath(folder?.rootPath) === normalizePath(libraryState.podcastDownloadLocation || '')
            || currentQuality === 'PODCAST';
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
        const viewModel = computeFolderRowViewModel(folder, folderRowContext);
        let currentProfile = viewModel.currentProfile;
        const currentSchedule = viewModel.currentSchedule;
        const folderIdKey = viewModel.folderIdKey;
        const canEnableAutoTag = viewModel.canEnableAutoTag;
        const selectedQualityValue = viewModel.selectedQualityValue;
        wrapper.innerHTML = buildFolderRowMarkup(folder, viewModel, conversionModeValue);

        const aliasContainer = wrapper.querySelector('.alias-container');
        const enhanceButton = wrapper.querySelector('[data-enhance]');
        wrapper.querySelector('[data-edit]').addEventListener('click', () => updateFolder(folder.id));
        wrapper.querySelector('[data-delete]').addEventListener('click', () => deleteFolder(folder.id));
        wrapper.querySelector('[data-aliases]').addEventListener('click', () => toggleAliases(folder.id, aliasContainer));
        bindFolderAutoTagToggle(wrapper, folder, canEnableAutoTag);

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
    return updated;
}

async function setFolderEnabled(id, enabled) {
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
            enabled,
            libraryName: folder.libraryName ?? null,
            desiredQuality: folder.desiredQuality ?? '27',
            convertEnabled: folder.convertEnabled === true,
            convertFormat: folder.convertFormat ?? null,
            convertBitrate: folder.convertBitrate ?? null
        })
    });
    await loadFolders();
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
    const currentValue = viewSelect.value || 'main';
    viewSelect.innerHTML = '';
    const mainOption = document.createElement('option');
    mainOption.value = 'main';
    mainOption.textContent = 'Main';
    viewSelect.appendChild(mainOption);
    (libraryState.folders || [])
        .filter(folder => isFolderEnabledFlag(folder.enabled))
        .forEach(folder => {
            const option = document.createElement('option');
            option.value = folder.displayName;
            option.textContent = getFolderLabel(folder);
            viewSelect.appendChild(option);
        });
    viewSelect.value = currentValue;
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
        const albums = await fetchJson(`/api/library/artists/${artistId}/albums`);
        const folderSet = new Set();
        (albums || []).forEach(album => {
            (album.localFolders || []).forEach(folderName => {
                if (folderName) {
                    folderSet.add(folderName);
                }
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

function compareArtistsBySort(a, b, sortKey) {
    const first = (a?.name || '').toLocaleLowerCase();
    const second = (b?.name || '').toLocaleLowerCase();
    if (sortKey === 'name-desc') {
        return second.localeCompare(first);
    }
    return first.localeCompare(second);
}

async function applyLibraryViewFilter() {
    const viewSelect = document.getElementById('libraryViewSelect');
    const allArtists = Array.isArray(libraryState.allArtists) ? libraryState.allArtists : [];
    const normalizedQuery = (libraryState.artistSearchQuery || '').trim().toLocaleLowerCase();
    const sortKey = libraryState.artistSortKey || 'name-asc';
    let filteredByView = allArtists;

    if (!viewSelect) {
        const filtered = allArtists
            .filter(artist => !normalizedQuery || (artist.name || '').toLocaleLowerCase().includes(normalizedQuery))
            .sort((a, b) => compareArtistsBySort(a, b, sortKey));
        libraryState.filteredArtists = filtered;
        updateLibraryResultsMeta(allArtists.length, filtered.length);
        renderArtistGrid(filtered);
        return;
    }

    const selected = (viewSelect.value || '').trim();
    if (selected && selected !== 'main') {
        const folderLookups = await Promise.all(allArtists.map(async artist => {
            const folders = await getArtistFolders(artist.id);
            return { artist, folders };
        }));
        filteredByView = folderLookups
            .filter(entry => entry.folders?.has(selected))
            .map(entry => entry.artist);
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

async function loadWatchlist() {
    const container = document.getElementById('watchlistContainer');
    if (!container) {
        return;
    }
    try {
        if (!Array.isArray(libraryState.folders) || !libraryState.folders.length) {
            try {
                const folders = await fetchJson('/api/library/folders');
                libraryState.folders = Array.isArray(folders)
                    ? folders.map(normalizeFolderConversionState)
                    : [];
            } catch {
                libraryState.folders = [];
            }
        }
        const [items, historyRaw] = await Promise.all([
            fetchJson('/api/library/watchlist'),
            fetchJson('/api/history/watchlist?limit=500&offset=0').catch(() => [])
        ]);

        if (!Array.isArray(items) || items.length === 0) {
            container.innerHTML = '<div class="watchlist-empty-state">No monitored artists yet.</div>';
            return;
        }

        // Build detected-count map from history
        const detectedByName = {};
        if (Array.isArray(historyRaw)) {
            for (const h of historyRaw) {
                if (h.watchType === 'artist' && h.artistName) {
                    const key = h.artistName.toLowerCase();
                    detectedByName[key] = (detectedByName[key] || 0) + 1;
                }
            }
        }

        const enabledFolders = (libraryState.folders || []).filter(isMusicRecommendationEligibleFolder);
        const folderOptions = enabledFolders
            .map(folder => `<option value="${folder.id}">${folder.displayName || 'Folder'}</option>`)
            .join('');
        const artistPrefs = getStoredPreferences('artistWatchlist');

        container.innerHTML = items.map(item => {
            const cover = item.artistImagePath
                ? appendCacheKey(`/api/library/image?path=${encodeURIComponent(item.artistImagePath)}&size=300`)
                : '';
            const artContent = cover
                ? `<img src="${cover}" alt="${escapeHtml(item.artistName)}" />`
                : `<div class="watchlist-card-art-placeholder"><i class="fa-solid fa-music"></i></div>`;

            const badges = [
                item.spotifyId ? `<span class="watchlist-card-badge" title="Spotify"><i class="fab fa-spotify"></i></span>` : '',
                item.deezerId ? `<span class="watchlist-card-badge" title="Deezer"><i class="fa-solid fa-music"></i></span>` : '',
                item.appleId ? `<span class="watchlist-card-badge" title="Apple Music"><i class="fab fa-apple"></i></span>` : ''
            ].filter(Boolean).join('');

            const detectedCount = detectedByName[item.artistName?.toLowerCase()] || 0;
            const lastChecked = formatRelativeTime(item.lastCheckedUtc);
            const statsHtml = [
                detectedCount > 0 ? `<span class="watchlist-card-stat">${detectedCount} detected</span>` : '',
                lastChecked ? `<span class="watchlist-card-stat">Checked ${lastChecked}</span>` : ''
            ].filter(Boolean).join('');

            const folderSelect = folderOptions
                ? `<select class="watchlist-card-folder-select form-select" data-watchlist-folder="${item.artistId}">
                       <option value="">No folder</option>
                       ${folderOptions}
                   </select>`
                : `<div class="watchlist-folder-empty">No folders configured.</div>`;

            const deezerId = item.deezerId || '';
            const spotifyId = item.spotifyId || '';

            return `<div class="watchlist-artist-card">
                <button class="watchlist-card-art" type="button"
                    data-watchlist-open="${item.artistId}"
                    data-watchlist-deezer="${escapeHtml(deezerId)}"
                    data-watchlist-spotify="${escapeHtml(spotifyId)}">
                    ${artContent}
                    ${badges ? `<div class="watchlist-card-badges">${badges}</div>` : ''}
                    ${statsHtml ? `<div class="watchlist-card-stats">${statsHtml}</div>` : ''}
                </button>
                <div class="watchlist-card-strip">
                    <div class="watchlist-card-name">${escapeHtml(item.artistName)}</div>
                    ${folderSelect}
                    <button class="btn btn-danger action-btn btn-sm watchlist-card-unmonitor" data-watchlist-remove="${item.artistId}" type="button">Unmonitor</button>
                </div>
            </div>`;
        }).join('');

        container.querySelectorAll('[data-watchlist-folder]').forEach(select => {
            const artistId = select.dataset.watchlistFolder;
            const stored = artistId ? artistPrefs[artistId] : '';
            if (stored) {
                select.value = stored;
            }
        });

        container.querySelectorAll('[data-watchlist-remove]').forEach(button => {
            button.addEventListener('click', async () => {
                const artistId = button.dataset.watchlistRemove;
                if (!artistId) return;
                try {
                    await fetchJson(`/api/library/watchlist/${artistId}`, { method: 'DELETE' });
                    await loadWatchlist();
                } catch (error) {
                    showToast(`Watchlist remove failed: ${error.message}`, true);
                }
            });
        });

        container.querySelectorAll('[data-watchlist-open]').forEach(button => {
            button.addEventListener('click', () => {
                const deezerId = button.dataset.watchlistDeezer || '';
                const spotifyId = button.dataset.watchlistSpotify || '';
                const fallbackId = button.dataset.watchlistOpen || '';
                if (deezerId) { globalThis.location.href = `/Artist?id=${encodeURIComponent(deezerId)}&source=deezer`; return; }
                if (spotifyId) { globalThis.location.href = `/Artist?id=${encodeURIComponent(spotifyId)}&source=spotify`; return; }
                if (fallbackId) { globalThis.location.href = `/Artist?id=${encodeURIComponent(fallbackId)}&source=deezer`; }
            });
        });
    } catch (error) {
        container.innerHTML = `<div class="watchlist-empty-state">Failed to load watchlist: ${escapeHtml(error?.message || 'Unknown error')}</div>`;
    }
}

async function loadPlaylistWatchlist() {
    const container = document.getElementById('playlistWatchlistContainer');
    if (!container) return;
    try {
        if (!Array.isArray(libraryState.folders) || !libraryState.folders.length) {
            try {
                const folders = await fetchJson('/api/library/folders');
                libraryState.folders = Array.isArray(folders)
                    ? folders.map(normalizeFolderConversionState)
                    : [];
            } catch {
                libraryState.folders = [];
            }
        }
        const items = await fetchJson('/api/library/playlists');
        if (!Array.isArray(items) || items.length === 0) {
            container.innerHTML = '<div class="watchlist-empty-state">No monitored playlists yet.</div>';
            return;
        }

        const playlistPrefs = await hydratePlaylistPreferences();

        container.innerHTML = items.map(item => {
            const artContent = item.imageUrl
                ? `<img src="${item.imageUrl}" alt="${escapeHtml(item.name)}" />`
                : `<div class="watchlist-card-art-placeholder"><i class="fa-solid fa-list-music"></i></div>`;
            const trackCountStr = item.trackCount === null || item.trackCount === undefined
                ? ''
                : `${item.trackCount} tracks`;
            return `<div class="watchlist-playlist-card-v2">
                <button class="watchlist-card-art" type="button"
                    data-playlist-open="${escapeHtml(item.sourceId)}"
                    data-playlist-source="${escapeHtml(item.source)}">
                    ${artContent}
                    ${trackCountStr ? `<div class="watchlist-card-stats"><span class="watchlist-card-stat">${escapeHtml(trackCountStr)}</span></div>` : ''}
                </button>
                <div class="watchlist-card-strip">
                    <div class="watchlist-card-name">${escapeHtml(item.name)}</div>
                    <div class="watchlist-playlist-action-row">
                        <div class="watchlist-action-menu">
                            <button class="btn-icon" type="button" title="Actions" data-playlist-menu-toggle="${escapeHtml(item.source)}" data-playlist-id="${escapeHtml(item.sourceId)}" aria-expanded="false">
                                <i class="fa-solid fa-gear"></i>
                            </button>
                            <div class="watchlist-action-dropdown" data-playlist-menu="${escapeHtml(item.source)}" data-playlist-id="${escapeHtml(item.sourceId)}" hidden>
                                <button class="dropdown-item" type="button" data-playlist-action="settings" data-playlist-source="${escapeHtml(item.source)}" data-playlist-id="${escapeHtml(item.sourceId)}" data-playlist-name="${escapeHtml(item.name)}">
                                    <i class="fa-solid fa-sliders"></i><span>Settings</span>
                                </button>
                                <button class="dropdown-item" type="button" data-playlist-action="sync" data-playlist-source="${escapeHtml(item.source)}" data-playlist-id="${escapeHtml(item.sourceId)}">
                                    <i class="fa-solid fa-rotate"></i><span>Sync now</span>
                                </button>
                                <button class="dropdown-item" type="button" data-playlist-action="refresh-artwork" data-playlist-source="${escapeHtml(item.source)}" data-playlist-id="${escapeHtml(item.sourceId)}">
                                    <i class="fa-solid fa-image"></i><span>Refresh artwork</span>
                                </button>
                                <button class="dropdown-item danger" type="button" data-playlist-action="remove" data-playlist-source="${escapeHtml(item.source)}" data-playlist-id="${escapeHtml(item.sourceId)}">
                                    <i class="fa-solid fa-xmark"></i><span>Unmonitor</span>
                                </button>
                            </div>
                        </div>
                    </div>
                </div>
            </div>`;
        }).join('');

        container.querySelectorAll('[data-playlist-open]').forEach(button => {
            button.addEventListener('click', () => {
                const sourceId = button.dataset.playlistOpen;
                const source = button.dataset.playlistSource || 'deezer';
                if (sourceId) {
                    const normalizedSource = String(source || '').toLowerCase();
                    let type = 'playlist';
                    if (normalizedSource === 'smarttracklist') {
                        type = 'smarttracklist';
                    } else if (normalizedSource === 'recommendations') {
                        type = 'recommendation';
                    }
                    const query = new URLSearchParams({
                        id: String(sourceId),
                        type
                    });
                    if (normalizedSource && normalizedSource !== 'deezer') {
                        query.set('source', normalizedSource);
                    }
                    if (normalizedSource === 'recommendations') {
                        const stationMatch = /^daily-rotation:l(\d+):f\d+$/i.exec(String(sourceId));
                        if (stationMatch?.[1]) {
                            query.set('libraryId', stationMatch[1]);
                        }
                    }
                    globalThis.location.href = `/Tracklist?${query.toString()}`;
                }
            });
        });

        const closePlaylistActionMenus = () => {
            container.querySelectorAll('[data-playlist-menu]').forEach(menu => {
                menu.hidden = true;
            });
            container.querySelectorAll('[data-playlist-menu-toggle]').forEach(toggle => {
                toggle.setAttribute('aria-expanded', 'false');
            });
        };

        container.querySelectorAll('[data-playlist-menu-toggle]').forEach(button => {
            button.addEventListener('click', (event) => {
                event.stopPropagation();
                const source = button.dataset.playlistMenuToggle;
                const sourceId = button.dataset.playlistId;
                const menu = source && sourceId
                    ? container.querySelector(`[data-playlist-menu="${source}"][data-playlist-id="${sourceId}"]`)
                    : null;
                const shouldOpen = Boolean(menu?.hidden);
                closePlaylistActionMenus();
                if (menu && shouldOpen) {
                    menu.hidden = false;
                    button.setAttribute('aria-expanded', 'true');
                }
            });
        });

        if (container.dataset.playlistMenuBound !== 'true') {
            document.addEventListener('click', () => {
                closePlaylistActionMenus();
            });
            container.dataset.playlistMenuBound = 'true';
        }

        container.querySelectorAll('[data-playlist-action="remove"]').forEach(button => {
            button.addEventListener('click', async () => {
                const source = button.dataset.playlistSource;
                const sourceId = button.dataset.playlistId;
                if (!source || !sourceId) return;
                try {
                    await fetchJson(`/api/library/playlists/${encodeURIComponent(source)}/${encodeURIComponent(sourceId)}`, { method: 'DELETE' });
                    await loadPlaylistWatchlist();
                } catch (error) {
                    showToast(`Playlist remove failed: ${error.message}`, true);
                }
            });
        });

        container.querySelectorAll('[data-playlist-action="sync"]').forEach(button => {
            button.addEventListener('click', async () => {
                const source = button.dataset.playlistSource;
                const sourceId = button.dataset.playlistId;
                if (!source || !sourceId) return;
                try {
                    const result = await fetchJson(`/api/library/playlists/${encodeURIComponent(source)}/${encodeURIComponent(sourceId)}/sync`, { method: 'POST' });
                    showToast(result?.message || 'Playlist sync scheduled.');
                } catch (error) {
                    showToast(`Playlist sync failed: ${error.message}`, true);
                }
            });
        });

        container.querySelectorAll('[data-playlist-action="refresh-artwork"]').forEach(button => {
            button.addEventListener('click', async () => {
                const source = button.dataset.playlistSource;
                const sourceId = button.dataset.playlistId;
                if (!source || !sourceId) return;
                try {
                    await fetchJson(`/api/library/playlists/${encodeURIComponent(source)}/${encodeURIComponent(sourceId)}/refresh-artwork`, { method: 'POST' });
                    await loadPlaylistWatchlist();
                    showToast('Playlist artwork refreshed.');
                } catch (error) {
                    showToast(`Artwork refresh failed: ${error.message}`, true);
                }
            });
        });

        container.querySelectorAll('[data-playlist-action="settings"]').forEach(button => {
            button.addEventListener('click', async () => {
                const source = button.dataset.playlistSource;
                const sourceId = button.dataset.playlistId;
                const playlistName = button.dataset.playlistName || 'Playlist';
                if (!source || !sourceId) return;
                await openPlaylistSettingsPanel(source, sourceId, playlistName, playlistPrefs);
            });
        });

        try {
            const pendingSettings = sessionStorage.getItem('playlist-watchlist-open-settings');
            if (pendingSettings) {
                sessionStorage.removeItem('playlist-watchlist-open-settings');
                const parsed = JSON.parse(pendingSettings);
                const pendingSource = String(parsed?.source || '').trim();
                const pendingSourceId = String(parsed?.sourceId || '').trim();
                const pendingName = String(parsed?.name || 'Playlist').trim() || 'Playlist';
                if (pendingSource && pendingSourceId) {
                    setTimeout(() => {
                        openPlaylistSettingsPanel(pendingSource, pendingSourceId, pendingName, playlistPrefs);
                    }, 0);
                }
            }
        } catch {
        }
    } catch (error) {
        container.innerHTML = `<div class="watchlist-empty-state">Failed to load playlists: ${escapeHtml(error?.message || 'Unknown error')}</div>`;
    }
}

async function openPlaylistSettingsPanel(source, sourceId, playlistName, playlistPrefs) {
    const enabledFolders = (libraryState.folders || []).filter(isMusicRecommendationEligibleFolder);

    const prefKey = `${source}:${sourceId}`;
    const localPlaylistPrefs = getStoredPreferences('playlistWatchlist');
    const stored = {
        ...playlistPrefs[prefKey],
        ...localPlaylistPrefs[prefKey]
    };

    // Load routing rules, blocked track rules, and available playlist tracks in parallel.
    const [existingRules, existingBlockRules, trackCandidatesResponse] = await Promise.all([
        fetchJson(`/api/library/playlists/${encodeURIComponent(source)}/${encodeURIComponent(sourceId)}/routing-rules`).catch(() => []),
        fetchJson(`/api/library/playlists/${encodeURIComponent(source)}/${encodeURIComponent(sourceId)}/ignore-rules`).catch(() => []),
        fetchJson(`/api/library/playlists/${encodeURIComponent(source)}/${encodeURIComponent(sourceId)}/tracks`).catch(() => null)
    ]);

    const panel = document.createElement('div');
    panel.className = 'playlist-settings-panel';

    const trackCandidates = Array.isArray(trackCandidatesResponse) ? trackCandidatesResponse : [];
    const trackCandidateMap = new Map();
    const routingValueIndex = {
        artist: new Map(),
        title: new Map(),
        album: new Map(),
        genre: new Map(),
        year: new Map()
    };
    const explicitModesAvailable = new Set();

    function addRoutingValue(field, rawValue) {
        const value = String(rawValue || '').trim();
        if (!value) {
            return;
        }

        const normalized = value.toLowerCase();
        const bucket = routingValueIndex[field];
        if (bucket && !bucket.has(normalized)) {
            bucket.set(normalized, value);
        }
    }

    trackCandidates.forEach(candidate => {
        const trackSourceId = String(candidate?.trackSourceId || '').trim();
        if (!trackSourceId || trackCandidateMap.has(trackSourceId)) {
            return;
        }

        const releaseYearRaw = candidate?.releaseYear;
        const releaseYear = Number.isFinite(Number(releaseYearRaw)) ? Number(releaseYearRaw) : null;
        const explicitRaw = candidate?.explicit;
        let explicit = null;
        if (explicitRaw === true) {
            explicit = true;
        } else if (explicitRaw === false) {
            explicit = false;
        }
        const genresRaw = Array.isArray(candidate?.genres) ? candidate.genres : [];
        const genres = genresRaw
            .map(value => String(value || '').trim())
            .filter(Boolean);

        const normalizedCandidate = {
            trackSourceId,
            isrc: String(candidate?.isrc || '').trim(),
            title: String(candidate?.title || '').trim(),
            artist: String(candidate?.artist || '').trim(),
            album: String(candidate?.album || '').trim(),
            releaseYear,
            explicit,
            genres
        };

        trackCandidateMap.set(trackSourceId, normalizedCandidate);

        addRoutingValue('artist', normalizedCandidate.artist);
        addRoutingValue('title', normalizedCandidate.title);
        addRoutingValue('album', normalizedCandidate.album);
        if (Number.isInteger(normalizedCandidate.releaseYear)) {
            addRoutingValue('year', String(normalizedCandidate.releaseYear));
        }
        normalizedCandidate.genres.forEach(genre => addRoutingValue('genre', genre));

        if (normalizedCandidate.explicit === true) {
            explicitModesAvailable.add('is_true');
        } else if (normalizedCandidate.explicit === false) {
            explicitModesAvailable.add('is_false');
        }
    });

    const routingFieldValues = {
        artist: Array.from(routingValueIndex.artist.values()).sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' })),
        title: Array.from(routingValueIndex.title.values()).sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' })),
        album: Array.from(routingValueIndex.album.values()).sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' })),
        genre: Array.from(routingValueIndex.genre.values()).sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' })),
        year: Array.from(routingValueIndex.year.values()).sort((a, b) => Number(b) - Number(a))
    };

    // Section: Destination folder
    const folderSection = document.createElement('div');
    folderSection.className = 'playlist-settings-section';
    const folderTitle = document.createElement('div');
    folderTitle.className = 'playlist-settings-section-title';
    folderTitle.textContent = 'Destination folder';
    const folderSelect = document.createElement('select');
    folderSelect.className = 'form-select ps-folder-select';
    folderSelect.id = `ps-folder-${source}-${sourceId}`;
    const noFolderOption = document.createElement('option');
    noFolderOption.value = '';
    noFolderOption.textContent = 'No folder';
    folderSelect.appendChild(noFolderOption);
    enabledFolders.forEach((folder) => {
        const option = document.createElement('option');
        option.value = String(folder.id ?? '');
        option.textContent = String(folder.displayName || 'Folder');
        folderSelect.appendChild(option);
    });
    folderSection.appendChild(folderTitle);
    folderSection.appendChild(folderSelect);
    panel.appendChild(folderSection);

    // Section: Server
    const serverSection = document.createElement('div');
    serverSection.className = 'playlist-settings-section';
    const serverTitle = document.createElement('div');
    serverTitle.className = 'playlist-settings-section-title';
    serverTitle.textContent = 'Server';
    const serverSelect = document.createElement('select');
    serverSelect.className = 'form-select ps-service-select';
    serverSelect.id = `ps-service-${source}-${sourceId}`;
    [
        { value: 'plex', label: 'Plex' },
        { value: 'jellyfin', label: 'Jellyfin' }
    ].forEach(({ value, label }) => {
        const option = document.createElement('option');
        option.value = value;
        option.textContent = label;
        serverSelect.appendChild(option);
    });
    serverSection.appendChild(serverTitle);
    serverSection.appendChild(serverSelect);
    panel.appendChild(serverSection);

    // Section: Download engine
    const engineSection = document.createElement('div');
    engineSection.className = 'playlist-settings-section';
    const engineTitle = document.createElement('div');
    engineTitle.className = 'playlist-settings-section-title';
    engineTitle.textContent = 'Download engine';
    const engineSelect = document.createElement('select');
    engineSelect.className = 'form-select ps-engine-select';
    engineSelect.id = `ps-engine-${source}-${sourceId}`;
    [
        { value: '', label: 'Follow global download source' },
        { value: 'auto', label: 'Auto (cross-engine fallback)' },
        { value: 'amazon', label: 'Amazon Music' },
        { value: 'apple', label: 'Apple Music' },
        { value: 'deezer', label: 'Deezer' },
        { value: 'qobuz', label: 'Qobuz' },
        { value: 'tidal', label: 'Tidal' }
    ].forEach(({ value, label }) => {
        const option = document.createElement('option');
        option.value = value;
        option.textContent = label;
        engineSelect.appendChild(option);
    });
    engineSection.appendChild(engineTitle);
    engineSection.appendChild(engineSelect);
    panel.appendChild(engineSection);

    const downloadModeSection = document.createElement('div');
    downloadModeSection.className = 'playlist-settings-section';
    const downloadModeTitle = document.createElement('div');
    downloadModeTitle.className = 'playlist-settings-section-title';
    downloadModeTitle.textContent = 'Download mode';
    const downloadModeSelect = document.createElement('select');
    downloadModeSelect.className = 'form-select ps-download-mode-select';
    downloadModeSelect.id = `ps-download-mode-${source}-${sourceId}`;
    [
        { value: 'standard', label: 'Standard only' },
        { value: 'dual_quality', label: 'Dual quality (standard + Atmos)' },
        { value: 'atmos_only', label: 'Atmos only' }
    ].forEach(({ value, label }) => {
        const option = document.createElement('option');
        option.value = value;
        option.textContent = label;
        downloadModeSelect.appendChild(option);
    });
    downloadModeSection.appendChild(downloadModeTitle);
    downloadModeSection.appendChild(downloadModeSelect);
    panel.appendChild(downloadModeSection);

    const artworkSection = document.createElement('div');
    artworkSection.className = 'playlist-settings-section';
    const artworkTitle = document.createElement('div');
    artworkTitle.className = 'playlist-settings-section-title';
    artworkTitle.textContent = 'Playlist artwork';
    const artworkToggleRow = document.createElement('label');
    artworkToggleRow.className = 'checkbox-group';
    artworkToggleRow.innerHTML = `
        <input type="checkbox" class="ps-update-artwork" checked />
        <span>Update playlist artwork during sync</span>
    `;
    artworkSection.appendChild(artworkTitle);
    artworkSection.appendChild(artworkToggleRow);
    const artworkReuseRow = document.createElement('label');
    artworkReuseRow.className = 'checkbox-group';
    artworkReuseRow.innerHTML = `
        <input type="checkbox" class="ps-reuse-saved-artwork" />
        <span>Keep and reuse saved artwork when source art changes</span>
    `;
    artworkSection.appendChild(artworkReuseRow);
    panel.appendChild(artworkSection);

    // Section: Routing rules
    const rulesSection = document.createElement('div');
    rulesSection.className = 'playlist-settings-section';
    rulesSection.innerHTML = `<div class="playlist-settings-section-title">Track routing rules</div>`;
    const rulesList = document.createElement('div');
    rulesList.className = 'routing-rules-list';
    const syncExplicitOptionAvailability = (explicitSelect) => {
        const options = Array.from(explicitSelect.options);
        if (explicitModesAvailable.size <= 0) {
            options.forEach((option) => {
                option.disabled = false;
            });
            return;
        }
        options.forEach((option) => {
            option.disabled = !explicitModesAvailable.has(option.value);
        });
        if (explicitSelect.selectedOptions[0]?.disabled) {
            const firstEnabled = options.find((option) => !option.disabled);
            if (firstEnabled) {
                explicitSelect.value = firstEnabled.value;
            }
        }
    };
    const applyRoutingFieldPresentation = ({
        field,
        normalizeField,
        getOps,
        operatorSelect,
        choiceSelect,
        explicitSelect,
        defaultChoiceValue,
        populateValueChoice
    }) => {
        const normalizedField = normalizeField(field);
        const previousOperator = operatorSelect.value;
        const ops = getOps(normalizedField);
        operatorSelect.innerHTML = ops.map(([value, label]) => `<option value="${value}">${label}</option>`).join('');
        operatorSelect.value = ops.some(([value]) => value === previousOperator)
            ? previousOperator
            : ops[0][0];

        const isExplicit = normalizedField === 'explicit';
        choiceSelect.hidden = isExplicit;
        choiceSelect.disabled = isExplicit;
        explicitSelect.hidden = !isExplicit;
        explicitSelect.disabled = !isExplicit;
        operatorSelect.disabled = isExplicit;
        operatorSelect.style.opacity = isExplicit ? '0.5' : '';

        if (!isExplicit) {
            populateValueChoice(normalizedField, choiceSelect.value || defaultChoiceValue);
            explicitSelect.value = operatorSelect.value === 'is_false' ? 'is_false' : 'is_true';
            return;
        }

        syncExplicitOptionAvailability(explicitSelect);
        operatorSelect.value = explicitSelect.value === 'is_false' ? 'is_false' : 'is_true';
    };
    const bindRoutingFieldPresentationHandlers = ({
        fieldSelect,
        operatorSelect,
        choiceSelect,
        explicitSelect,
        currentField,
        normalizeField,
        getOps,
        defaultChoiceValue,
        populateValueChoice
    }) => {
        const applyFieldPresentation = (field) => {
            applyRoutingFieldPresentation({
                field,
                normalizeField,
                getOps,
                operatorSelect,
                choiceSelect,
                explicitSelect,
                defaultChoiceValue,
                populateValueChoice
            });
        };

        fieldSelect.addEventListener('change', function() {
            applyFieldPresentation(this.value);
        });

        explicitSelect.addEventListener('change', function() {
            if (fieldSelect.value === 'explicit') {
                operatorSelect.value = this.value === 'is_false' ? 'is_false' : 'is_true';
            }
        });

        applyFieldPresentation(currentField);
    };

    function buildRuleRow(rule) {
        const supportedFields = ['artist', 'title', 'album', 'genre', 'year', 'explicit'];
        const fieldLabels = {
            artist: 'Artist',
            title: 'Title',
            album: 'Album',
            genre: 'Genre',
            year: 'Year',
            explicit: 'Explicit'
        };
        const normalizeField = (value) => {
            const normalized = String(value || '').trim().toLowerCase();
            return supportedFields.includes(normalized) ? normalized : 'artist';
        };
        const getOps = (field) => {
            switch (field) {
                case 'explicit':
                    return [['is_true', 'explicit only'], ['is_false', 'clean only']];
                case 'year':
                    return [['equals', 'equals'], ['gte', 'at least'], ['lte', 'at most']];
                default:
                    return [['contains', 'contains'], ['equals', 'equals'], ['starts_with', 'starts with']];
            }
        };
        const getFieldValues = (field, currentValue) => {
            const baseValues = Array.isArray(routingFieldValues[field]) ? routingFieldValues[field] : [];
            const normalizedCurrentValue = String(currentValue || '').trim();
            if (!normalizedCurrentValue) {
                return [...baseValues];
            }

            const exists = baseValues.some(value => value.localeCompare(normalizedCurrentValue, undefined, { sensitivity: 'base' }) === 0);
            return exists ? [...baseValues] : [normalizedCurrentValue, ...baseValues];
        };

        const currentField = normalizeField(rule?.conditionField);
        const conditionFieldOpts = supportedFields
            .map(f => `<option value="${f}" ${currentField === f ? 'selected' : ''}>${fieldLabels[f] || f}</option>`)
            .join('');
        const operatorOpts = getOps(currentField)
            .map(([v, l]) => `<option value="${v}" ${rule?.conditionOperator === v ? 'selected' : ''}>${l}</option>`)
            .join('');
        const folderRuleOpts = enabledFolders
            .map(f => `<option value="${f.id}" ${rule?.destinationFolderId == f.id ? 'selected' : ''}>${escapeHtml(f.displayName || 'Folder')}</option>`)
            .join('');
        const normalizedValue = String(rule?.conditionValue || '').trim();
        const normalizedOperator = String(rule?.conditionOperator || '').trim().toLowerCase();

        const row = document.createElement('div');
        row.className = 'routing-rule-row';
        row.innerHTML = `
            <select class="rr-field">
                ${conditionFieldOpts}
            </select>
            <select class="rr-operator">
                ${operatorOpts}
            </select>
            <div class="rr-value-wrap">
                <select class="rr-value rr-value-choice"></select>
                <select class="rr-value rr-value-explicit">
                    <option value="is_true" ${normalizedOperator === 'is_true' ? 'selected' : ''}>Explicit tracks only</option>
                    <option value="is_false" ${normalizedOperator === 'is_false' ? 'selected' : ''}>Clean/non-explicit tracks only</option>
                </select>
            </div>
            <select class="rr-folder">
                <option value="">No folder</option>
                ${folderRuleOpts}
            </select>
            <button class="routing-rule-remove" type="button" title="Remove rule"><i class="fa-solid fa-xmark"></i></button>`;

        const fieldSelect = row.querySelector('.rr-field');
        const operatorSelect = row.querySelector('.rr-operator');
        const choiceSelect = row.querySelector('.rr-value-choice');
        const explicitSelect = row.querySelector('.rr-value-explicit');

        function populateValueChoice(field, currentValue) {
            const values = getFieldValues(field, currentValue);
            const selectedValue = values.includes(currentValue) ? currentValue : values[0] || '';
            choiceSelect.innerHTML = '';

            if (values.length === 0) {
                choiceSelect.add(new Option('No playlist metadata values', ''));
                choiceSelect.value = '';
                choiceSelect.disabled = true;
                return;
            }

            const fragment = document.createDocumentFragment();
            for (const value of values) {
                fragment.appendChild(new Option(value, value));
            }
            choiceSelect.appendChild(fragment);
            choiceSelect.disabled = false;
            choiceSelect.value = selectedValue;
        }

        bindRoutingFieldPresentationHandlers({
            fieldSelect,
            operatorSelect,
            choiceSelect,
            explicitSelect,
            currentField,
            normalizeField,
            getOps,
            defaultChoiceValue: normalizedValue,
            populateValueChoice
        });

        row.querySelector('.routing-rule-remove').addEventListener('click', () => row.remove());
        return row;
    }

    (Array.isArray(existingRules) ? existingRules : []).forEach(rule => rulesList.appendChild(buildRuleRow(rule)));

    const addRuleBtn = document.createElement('button');
    addRuleBtn.className = 'btn btn-secondary action-btn btn-sm routing-rules-add-btn';
    addRuleBtn.type = 'button';
    addRuleBtn.textContent = '+ Add rule';
    addRuleBtn.addEventListener('click', () => rulesList.appendChild(buildRuleRow(null)));

    rulesSection.appendChild(rulesList);
    rulesSection.appendChild(addRuleBtn);
    panel.appendChild(rulesSection);

    // Section: Blocked track rules
    const blockedSection = document.createElement('div');
    blockedSection.className = 'playlist-settings-section';
    blockedSection.innerHTML = `<div class="playlist-settings-section-title">Blocked track rules</div>`;

    function buildBlockRuleRow(rule) {
        const supportedFields = ['artist', 'title', 'album', 'genre', 'year', 'explicit'];
        const fieldLabels = {
            artist: 'Artist',
            title: 'Title',
            album: 'Album',
            genre: 'Genre',
            year: 'Year',
            explicit: 'Explicit'
        };
        const normalizeField = (value) => {
            const normalized = String(value || '').trim().toLowerCase();
            return supportedFields.includes(normalized) ? normalized : 'artist';
        };
        const getOps = (field) => {
            if (field === 'explicit') {
                return [['is_true', 'explicit only'], ['is_false', 'clean only']];
            }
            if (field === 'year') {
                return [['equals', 'equals'], ['gte', 'at least'], ['lte', 'at most']];
            }
            return [['contains', 'contains'], ['equals', 'equals'], ['starts_with', 'starts with']];
        };
        const getFieldValues = (field, currentValue) => {
            const values = Array.isArray(routingFieldValues[field]) ? [...routingFieldValues[field]] : [];
            const normalizedCurrentValue = String(currentValue || '').trim();
            if (normalizedCurrentValue) {
                const exists = values.some(value => value.localeCompare(normalizedCurrentValue, undefined, { sensitivity: 'base' }) === 0);
                if (!exists) {
                    values.unshift(normalizedCurrentValue);
                }
            }
            return values;
        };

        const currentField = normalizeField(rule?.conditionField);
        const conditionFieldOpts = supportedFields
            .map(f => `<option value="${f}" ${currentField === f ? 'selected' : ''}>${fieldLabels[f] || f}</option>`)
            .join('');
        const operatorOpts = getOps(currentField)
            .map(([v, l]) => `<option value="${v}" ${rule?.conditionOperator === v ? 'selected' : ''}>${l}</option>`)
            .join('');
        const normalizedValue = String(rule?.conditionValue || '').trim();
        const normalizedOperator = String(rule?.conditionOperator || '').trim().toLowerCase();

        const row = document.createElement('div');
        row.className = 'routing-rule-row block-rule-row';
        row.innerHTML = `
            <select class="br-field">
                ${conditionFieldOpts}
            </select>
            <select class="br-operator">
                ${operatorOpts}
            </select>
            <div class="rr-value-wrap">
                <select class="rr-value br-value-choice"></select>
                <select class="rr-value br-value-explicit">
                    <option value="is_true" ${normalizedOperator === 'is_true' ? 'selected' : ''}>Explicit tracks only</option>
                    <option value="is_false" ${normalizedOperator === 'is_false' ? 'selected' : ''}>Clean/non-explicit tracks only</option>
                </select>
            </div>
            <button class="routing-rule-remove" type="button" title="Remove rule"><i class="fa-solid fa-xmark"></i></button>`;

        const fieldSelect = row.querySelector('.br-field');
        const operatorSelect = row.querySelector('.br-operator');
        const choiceSelect = row.querySelector('.br-value-choice');
        const explicitSelect = row.querySelector('.br-value-explicit');

        function populateValueChoice(field, currentValue) {
            const values = getFieldValues(field, currentValue);
            choiceSelect.innerHTML = '';
            if (values.length === 0) {
                const option = document.createElement('option');
                option.value = '';
                option.textContent = 'No playlist metadata values';
                choiceSelect.appendChild(option);
                choiceSelect.value = '';
                choiceSelect.disabled = true;
                return;
            }

            values.forEach(value => {
                const option = document.createElement('option');
                option.value = value;
                option.textContent = value;
                choiceSelect.appendChild(option);
            });
            choiceSelect.disabled = false;
            choiceSelect.value = values.includes(currentValue) ? currentValue : values[0];
        }

        bindRoutingFieldPresentationHandlers({
            fieldSelect,
            operatorSelect,
            choiceSelect,
            explicitSelect,
            currentField,
            normalizeField,
            getOps,
            defaultChoiceValue: normalizedValue,
            populateValueChoice
        });
        row.querySelector('.routing-rule-remove').addEventListener('click', () => row.remove());
        return row;
    }

    const blockRulesList = document.createElement('div');
    blockRulesList.className = 'routing-rules-list';
    (Array.isArray(existingBlockRules) ? existingBlockRules : []).forEach(rule => blockRulesList.appendChild(buildBlockRuleRow(rule)));

    const addBlockRuleBtn = document.createElement('button');
    addBlockRuleBtn.className = 'btn btn-secondary action-btn btn-sm routing-rules-add-btn';
    addBlockRuleBtn.type = 'button';
    addBlockRuleBtn.textContent = '+ Add block rule';
    addBlockRuleBtn.addEventListener('click', () => blockRulesList.appendChild(buildBlockRuleRow(null)));

    blockedSection.appendChild(blockRulesList);
    blockedSection.appendChild(addBlockRuleBtn);
    panel.appendChild(blockedSection);

    // Show modal
    if (!globalThis.DeezSpoTag?.ui?.showModal) {
        showToast('Settings panel unavailable', true);
        return;
    }

    // Pre-fill saved values after DOM is in the modal
    setTimeout(() => {
        const folderSel = panel.querySelector('.ps-folder-select');
        const serviceSel = panel.querySelector('.ps-service-select');
        const engineSel = panel.querySelector('.ps-engine-select');
        const downloadModeSel = panel.querySelector('.ps-download-mode-select');
        const artworkToggle = panel.querySelector('.ps-update-artwork');
        const artworkReuseToggle = panel.querySelector('.ps-reuse-saved-artwork');
        if (folderSel && stored.folderId) folderSel.value = String(stored.folderId);
        if (serviceSel && stored.service) serviceSel.value = stored.service;
        if (engineSel) engineSel.value = stored.preferredEngine || '';
        if (downloadModeSel) downloadModeSel.value = stored.downloadVariantMode || 'standard';
        if (artworkToggle) artworkToggle.checked = stored.updateArtwork !== false;
        if (artworkReuseToggle) artworkReuseToggle.checked = stored.reuseSavedArtwork === true;
    }, 0);

    const confirmed = await globalThis.DeezSpoTag.ui.showModal({
        title: `Settings — ${playlistName}`,
        message: '',
        allowHtml: false,
        contentElement: panel,
        buttons: [
            { label: 'Save', value: 'save', primary: true },
            { label: 'Cancel', value: 'cancel' }
        ]
    });

    if (confirmed?.value !== 'save') return;

    // Collect values and save
    const folderSel = panel.querySelector('.ps-folder-select');
    const serviceSel = panel.querySelector('.ps-service-select');
    const engineSel = panel.querySelector('.ps-engine-select');
    const downloadModeSel = panel.querySelector('.ps-download-mode-select');
    const artworkToggle = panel.querySelector('.ps-update-artwork');
    const artworkReuseToggle = panel.querySelector('.ps-reuse-saved-artwork');
    const folderId = folderSel?.value ? Number(folderSel.value) : null;
    const service = serviceSel?.value || 'plex';
    const preferredEngine = engineSel?.value || '';
    const downloadVariantMode = downloadModeSel?.value || 'standard';
    const updateArtwork = artworkToggle?.checked !== false;
    const reuseSavedArtwork = artworkReuseToggle?.checked === true;

    // Collect routing rules
    const rules = [];
    rulesList.querySelectorAll('.routing-rule-row').forEach((row, idx) => {
        const field = row.querySelector('.rr-field')?.value || 'artist';
        const explicitValue = row.querySelector('.rr-value-explicit')?.value || 'is_true';
        let operator = row.querySelector('.rr-operator')?.value || 'contains';
        if (field === 'explicit') {
            operator = explicitValue === 'is_false' ? 'is_false' : 'is_true';
        }
        const value = field === 'explicit'
            ? ''
            : (row.querySelector('.rr-value-choice')?.value || '').trim();
        const ruleFolder = row.querySelector('.rr-folder')?.value;
        if (!ruleFolder) return;
        if (field !== 'explicit' && !value) return;
        rules.push({
            conditionField: field,
            conditionOperator: operator,
            conditionValue: value,
            destinationFolderId: Number(ruleFolder),
            order: idx
        });
    });

    // Collect block rules
    const blockRules = [];
    blockRulesList.querySelectorAll('.block-rule-row').forEach((row, idx) => {
        const field = row.querySelector('.br-field')?.value || 'artist';
        const explicitValue = row.querySelector('.br-value-explicit')?.value || 'is_true';
        let operator = row.querySelector('.br-operator')?.value || 'contains';
        if (field === 'explicit') {
            operator = explicitValue === 'is_false' ? 'is_false' : 'is_true';
        }
        const value = field === 'explicit'
            ? ''
            : (row.querySelector('.br-value-choice')?.value || '').trim();
        if (field !== 'explicit' && !value) return;

        blockRules.push({
            conditionField: field,
            conditionOperator: operator,
            conditionValue: value,
            order: idx
        });
    });

    try {
        // Save preferences
        await fetchJson('/api/library/playlists/preferences', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify([{ source, sourceId, folderId, service, preferredEngine, downloadVariantMode, updateArtwork, reuseSavedArtwork }])
        });
        // Save routing rules
        await fetchJson(`/api/library/playlists/${encodeURIComponent(source)}/${encodeURIComponent(sourceId)}/routing-rules`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(rules)
        });
        // Save blocked-track rules
        await fetchJson(`/api/library/playlists/${encodeURIComponent(source)}/${encodeURIComponent(sourceId)}/ignore-rules`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(blockRules)
        });
        // Update local prefs
        const updatedPref = { folderId: folderId ? String(folderId) : '', service, preferredEngine, downloadVariantMode, updateArtwork, reuseSavedArtwork };
        storePlaylistPreference(source, sourceId, updatedPref);
        if (playlistPrefs && typeof playlistPrefs === 'object') {
            playlistPrefs[prefKey] = {
                ...playlistPrefs[prefKey],
                ...updatedPref
            };
        }
        showToast('Playlist settings saved.');
    } catch (error) {
        showToast(`Save failed: ${error.message}`, true);
    }
}

function getStoredPreferences(key) {
    try {
        const raw = localStorage.getItem(key);
        return raw ? JSON.parse(raw) : {};
    } catch {
        return {};
    }
}

function storePreferences(key, payload) {
    try {
        localStorage.setItem(key, JSON.stringify(payload));
    } catch {
        // Ignore storage failures.
    }
}

async function fetchPlaylistPreferences() {
    try {
        const items = await fetchJson('/api/library/playlists/preferences');
        return Array.isArray(items) ? items : [];
    } catch {
        return [];
    }
}

async function hydratePlaylistPreferences() {
    const localPrefs = getStoredPreferences('playlistWatchlist');
    const serverPrefs = await fetchPlaylistPreferences();
    const merged = { ...localPrefs };
    serverPrefs.forEach(item => {
        if (!item?.source || !item.sourceId) {
            return;
        }
        const key = `${item.source}:${item.sourceId}`;
        merged[key] = {
            ...merged[key],
            folderId: item.destinationFolderId || '',
            service: item.service || merged[key]?.service || 'plex',
            preferredEngine: item.preferredEngine || merged[key]?.preferredEngine || '',
            downloadVariantMode: item.downloadVariantMode || merged[key]?.downloadVariantMode || 'standard',
            updateArtwork: item.updateArtwork !== false,
            reuseSavedArtwork: item.reuseSavedArtwork === true
        };
    });
    return merged;
}

function storePlaylistPreference(source, sourceId, updates) {
    const key = `${source}:${sourceId}`;
    const prefs = getStoredPreferences('playlistWatchlist');
    prefs[key] = { ...prefs[key], ...updates };
    storePreferences('playlistWatchlist', prefs);
}

async function persistPlaylistPreference(container, source, sourceId) {
    const folderSelect = container.querySelector(`[data-playlist-folder="${source}"][data-playlist-id="${sourceId}"]`);
    const serviceSelect = container.querySelector(`[data-playlist-service="${source}"][data-playlist-id="${sourceId}"]`);
    const engineSelect = container.querySelector(`[data-playlist-engine="${source}"][data-playlist-id="${sourceId}"]`);
    const downloadModeSelect = container.querySelector(`[data-playlist-download-mode="${source}"][data-playlist-id="${sourceId}"]`);
    const folderId = folderSelect?.value || null;
    const service = serviceSelect?.value || 'plex';
    const preferredEngine = engineSelect?.value || '';
    const downloadVariantMode = downloadModeSelect?.value || 'standard';
    const updateArtwork = container.querySelector(`[data-playlist-update-artwork="${source}"][data-playlist-id="${sourceId}"]`)?.checked !== false;
    const reuseSavedArtwork = container.querySelector(`[data-playlist-reuse-artwork="${source}"][data-playlist-id="${sourceId}"]`)?.checked === true;
    storePlaylistPreference(source, sourceId, {
        folderId: folderId || '',
        service,
        preferredEngine,
        downloadVariantMode,
        updateArtwork,
        reuseSavedArtwork
    });
    const payload = [{
        source,
        sourceId,
        folderId: folderId ? Number(folderId) : null,
        service,
        preferredEngine,
        downloadVariantMode,
        updateArtwork,
        reuseSavedArtwork
    }];
    await fetchJson('/api/library/playlists/preferences', {
        method: 'POST',
        body: JSON.stringify(payload),
        headers: { 'Content-Type': 'application/json' }
    });
}

function saveArtistWatchlistPreferences() {
    const prefs = {};
    document.querySelectorAll('[data-watchlist-folder]').forEach(select => {
        const artistId = select.dataset.watchlistFolder;
        if (artistId) {
            prefs[artistId] = select.value || '';
        }
    });
    storePreferences('artistWatchlist', prefs);
    showToast('Artist preferences saved.');
}

function savePlaylistWatchlistPreferences() {
    const prefs = getStoredPreferences('playlistWatchlist');
    document.querySelectorAll('[data-playlist-folder]').forEach(select => {
        const source = select.dataset.playlistFolder;
        const sourceId = select.dataset.playlistId;
        const key = source && sourceId ? `${source}:${sourceId}` : '';
        if (key) {
            prefs[key] = { ...prefs[key], folderId: select.value || '' };
        }
    });
    document.querySelectorAll('[data-playlist-service]').forEach(select => {
        const source = select.dataset.playlistService;
        const sourceId = select.dataset.playlistId;
        const key = source && sourceId ? `${source}:${sourceId}` : '';
        if (key) {
            prefs[key] = { ...prefs[key], service: select.value || 'plex' };
        }
    });
    document.querySelectorAll('[data-playlist-engine]').forEach(select => {
        const source = select.dataset.playlistEngine;
        const sourceId = select.dataset.playlistId;
        const key = source && sourceId ? `${source}:${sourceId}` : '';
        if (key) {
            prefs[key] = { ...prefs[key], preferredEngine: select.value || '' };
        }
    });
    document.querySelectorAll('[data-playlist-update-artwork]').forEach(input => {
        const source = input.dataset.playlistUpdateArtwork;
        const sourceId = input.dataset.playlistId;
        const key = source && sourceId ? `${source}:${sourceId}` : '';
        if (key) {
            prefs[key] = { ...prefs[key], updateArtwork: input.checked !== false };
        }
    });
    document.querySelectorAll('[data-playlist-reuse-artwork]').forEach(input => {
        const source = input.dataset.playlistReuseArtwork;
        const sourceId = input.dataset.playlistId;
        const key = source && sourceId ? `${source}:${sourceId}` : '';
        if (key) {
            prefs[key] = { ...prefs[key], reuseSavedArtwork: input.checked === true };
        }
    });
    storePreferences('playlistWatchlist', prefs);
    savePlaylistPreferencesToServer(prefs);
}

async function savePlaylistPreferencesToServer(prefs) {
    const payload = Object.entries(prefs || {})
        .map(([key, value]) => {
            const parts = key.split(':');
            if (parts.length < 2) {
                return null;
            }
            return {
                source: parts[0],
                sourceId: parts.slice(1).join(':'),
                folderId: value?.folderId ? Number(value.folderId) : null,
                service: value?.service || null,
                preferredEngine: value?.preferredEngine || null,
                updateArtwork: value?.updateArtwork !== false,
                reuseSavedArtwork: value?.reuseSavedArtwork === true
            };
        })
        .filter(Boolean);

    if (!payload.length) {
        showToast('Playlist preferences saved.');
        return;
    }

    try {
        await fetchJson('/api/library/playlists/preferences', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        showToast('Playlist preferences saved.');
    } catch (error) {
        showToast(`Playlist preferences save failed: ${error.message}`, true);
    }
}
async function refreshWatchlistToggle(button, artistIdValue) {
    let watching = false;
    try {
        const status = await fetchJson(`/api/library/watchlist/${artistIdValue}`);
        watching = status?.watching === true;
    } catch {
        // Allow toggling even if status lookup fails.
    }
    button.textContent = watching ? 'Monitoring Artist' : 'Monitor Artist';
    button.classList.toggle('btn-secondary', watching);
    button.classList.toggle('btn-primary', !watching);
    button.dataset.watching = watching ? 'true' : 'false';
}

async function resolveWatchlistArtistName(artistIdValue) {
    const currentName = document.getElementById('artistName')?.textContent?.trim() || '';
    if (currentName && currentName !== 'Albums') {
        return currentName;
    }
    try {
        const artist = await fetchJsonOptional(`/api/library/artists/${artistIdValue}`);
        return artist?.name || currentName;
    } catch {
        return currentName;
    }
}

async function initWatchlistToggle() {
    const button = document.getElementById('watchlistToggle');
    const artistIdValue = document.querySelector('[data-artist-id]')?.dataset.artistId
        || resolveArtistIdFromPath(globalThis.location.pathname);
    if (!button || !artistIdValue) {
        return;
    }

    globalThis.DeezSpoTag = globalThis.DeezSpoTag || {};
    const toggle = async () => {
            button.disabled = true;
            try {
                const status = await fetchJson(`/api/library/watchlist/${artistIdValue}`);
                const watching = status?.watching === true;
                if (watching) {
                    await fetchJson(`/api/library/watchlist/${artistIdValue}`, { method: 'DELETE' });
                    showToast('Artist removed from watchlist.');
                } else {
                    const artistName = await resolveWatchlistArtistName(artistIdValue);
                    await fetchJson('/api/library/watchlist', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ artistId: Number(artistIdValue), artistName })
                    });
                    showToast('Artist added to watchlist.');
                }
                await refreshWatchlistToggle(button, artistIdValue);
            } catch (error) {
                showToast(`Watchlist update failed: ${error.message}`, true);
            } finally {
                button.disabled = false;
            }
        };
    globalThis.DeezSpoTag.LibraryWatchlist = { toggle };

    button.style.cursor = 'pointer';
    button.disabled = false;
    button.addEventListener('click', toggle);
    await refreshWatchlistToggle(button, artistIdValue);
}

// --- Apple artist extras (Atmos + Videos) ---
function setPanelSectionVisible(panelId, visible) {
    const panel = document.getElementById(panelId);
    const section = panel?.closest('.apple-section');
    if (!section) {
        return;
    }
    section.style.display = visible ? '' : 'none';
}

function updateAppleExtrasPanelVisibility() {
    const atmosCount = Array.isArray(libraryState.appleExtras.atmos) ? libraryState.appleExtras.atmos.length : 0;
    const videoCount = Array.isArray(libraryState.appleExtras.videos) ? libraryState.appleExtras.videos.length : 0;
    setPanelSectionVisible('appleAtmosPanel', atmosCount > 0);
    setPanelSectionVisible('appleVideosPanel', videoCount > 0);
}

function initAppleArtistExtras(artistName, storedAppleId) {
    const name = (artistName || '').trim();
    if (!name) return;
    const state = libraryState.appleExtras;
    const sameTerm = state.term.toLowerCase() === name.toLowerCase();
    state.term = name;
    state.storedAppleId = storedAppleId || null;
    if (!state.initialized || !sameTerm) {
        state.initialized = true;
        state.atmos = [];
        state.videos = [];
        state.videoOffset = 0;
        state.hasMoreVideos = false;
        state.loadingVideos = false;
        state.appleArtistId = storedAppleId || null;
        state.selectedVideoKeys.clear();
        state.showSelectedOnly = false;
        updateAppleExtrasPanelVisibility();
        bindArtistVideoSelectionControls();
        fetchAppleArtistExtras();
        bindArtistVideoInfiniteScroll();
    }
}

function bindArtistVideoSelectionControls() {
    const viewBtn = document.getElementById('artist-view-selected');
    const clearBtn = document.getElementById('artist-clear-selected');
    if (viewBtn && !viewBtn.dataset.bound) {
        viewBtn.dataset.bound = 'true';
        viewBtn.addEventListener('click', () => {
            const state = libraryState.appleExtras;
            state.showSelectedOnly = !state.showSelectedOnly;
            viewBtn.textContent = state.showSelectedOnly ? 'View all' : 'View selected only';
            renderAppleVideos();
        });
    }
    if (clearBtn && !clearBtn.dataset.bound) {
        clearBtn.dataset.bound = 'true';
        clearBtn.addEventListener('click', () => {
            const state = libraryState.appleExtras;
            state.selectedVideoKeys.clear();
            state.showSelectedOnly = false;
            updateArtistVideoSelectionBar();
            renderAppleVideos();
        });
    }
}

function bindArtistVideoInfiniteScroll() {
    if (libraryState.appleExtras.scrollBound) return;
    libraryState.appleExtras.scrollBound = true;
    let ticking = false;
    globalThis.addEventListener('scroll', () => {
        if (ticking) return;
        ticking = true;
        requestAnimationFrame(() => {
            ticking = false;
            const state = libraryState.appleExtras;
            if (!state.hasMoreVideos || state.loadingVideos) return;
            const nearBottom = (globalThis.innerHeight + globalThis.scrollY) >= (document.body.offsetHeight - 500);
            if (!nearBottom) return;
            loadMoreArtistVideos();
        });
    });
}

async function fetchAppleArtistSearch(termParam, normalizedTerm) {
    try {
        const data = await fetchJsonOptional(`/api/apple/search?term=${termParam}&limit=50`);
        if (!data || data.available === false) {
            throw new Error(data?.error || 'Apple search unavailable');
        }

        return {
            data,
            artistCandidates: Array.isArray(data.artists) ? data.artists : [],
            atmos: buildAppleAtmosList(data, normalizedTerm)
        };
    } catch (err) {
        console.warn('Apple artist mixed search failed', err);
        return {
            data: null,
            artistCandidates: [],
            atmos: []
        };
    }
}

async function ensureAppleArtistCandidates(termParam, artistCandidates) {
    if (artistCandidates.length > 0) {
        return artistCandidates;
    }

    try {
        const artistsOnly = await fetchJsonOptional(`/api/apple/search?term=${termParam}&limit=10&types=artists`);
        if (artistsOnly && artistsOnly.available !== false) {
            return Array.isArray(artistsOnly.artists) ? artistsOnly.artists : [];
        }
    } catch (err) {
        console.warn('Apple artist-id lookup failed', err);
    }

    return [];
}

async function backfillAppleAtmos(termParam, normalizedTerm) {
    try {
        const atmosOnly = await fetchJsonOptional(`/api/apple/search?term=${termParam}&limit=30&types=songs,albums`);
        if (atmosOnly && atmosOnly.available !== false) {
            return buildAppleAtmosList(atmosOnly, normalizedTerm);
        }
    } catch (err) {
        console.warn('Apple artist Atmos lookup failed', err);
    }

    return libraryState.appleExtras.atmos;
}

async function loadAppleArtistVideos(appleArtistId, data, term, termParam) {
    let videos = [];
    let hasMoreVideos = false;

    if (appleArtistId) {
        try {
            const artistVideosData = await fetchJsonOptional(`/api/apple/artist/videos?id=${encodeURIComponent(appleArtistId)}&limit=25&offset=0`);
            if (artistVideosData) {
                videos = artistVideosData.videos || [];
                hasMoreVideos = Boolean(artistVideosData.hasMoreVideos);
            }
        } catch (err) {
            console.warn('Apple artist videos failed', err);
        }
    }

    if (videos.length === 0 && data) {
        return {
            videos: filterAppleVideosByArtist(data.videos || [], term),
            hasMoreVideos: Boolean(data.hasMoreVideos)
        };
    }

    if (videos.length === 0 && !data) {
        try {
            const videosOnly = await fetchJsonOptional(`/api/apple/search?term=${termParam}&limit=25&types=music-videos`);
            if (videosOnly && videosOnly.available !== false) {
                return {
                    videos: filterAppleVideosByArtist(videosOnly.videos || [], term),
                    hasMoreVideos: Boolean(videosOnly.hasMoreVideos)
                };
            }
        } catch (err) {
            console.warn('Apple artist videos fallback search failed', err);
        }
    }

    return { videos, hasMoreVideos };
}

async function fetchAppleArtistExtras() {
    const term = libraryState.appleExtras.term;
    if (!term) return;
    const storedId = libraryState.appleExtras.storedAppleId;
    const atmosContainer = document.getElementById('appleAtmosGrid');
    const videoContainer = document.getElementById('appleVideosGrid');
    setAppleArtistExtrasLoading(term, atmosContainer, videoContainer);

    const normalizedTerm = normalizeArtistName(term);
    const termParam = encodeURIComponent(term);
    let appleArtistId = storedId || null;
    const searchResult = await fetchAppleArtistSearch(termParam, normalizedTerm);
    let { data, artistCandidates } = searchResult;
    libraryState.appleExtras.atmos = searchResult.atmos;

    if (!appleArtistId) {
        artistCandidates = await ensureAppleArtistCandidates(termParam, artistCandidates);
        if (artistCandidates.length > 0) {
            appleArtistId = await resolveAppleArtistIdWithLibrary(artistCandidates, term);
        }
    }

    if (!data) {
        libraryState.appleExtras.atmos = await backfillAppleAtmos(termParam, normalizedTerm);
    }

    libraryState.appleExtras.appleArtistId = appleArtistId;
    const { videos, hasMoreVideos } = await loadAppleArtistVideos(appleArtistId, data, term, termParam);
    libraryState.appleExtras.videos = videos;
    libraryState.appleExtras.videoOffset = videos.length;
    libraryState.appleExtras.hasMoreVideos = hasMoreVideos;

    updateAppleExtrasPanelVisibility();
    renderAppleAtmos();
    renderAppleVideos();
}

function resolveAppleArtistId(artists, term) {
    if (!Array.isArray(artists) || artists.length === 0) {
        return null;
    }
    const normalizedTerm = (term || '').trim().toLowerCase();
    if (!normalizedTerm) {
        return artists[0]?.appleId || null;
    }
    const exact = artists.find(a => (a?.name || '').trim().toLowerCase() === normalizedTerm);
    if (exact?.appleId) return exact.appleId;
    const contains = artists.find(a => (a?.name || '').trim().toLowerCase().includes(normalizedTerm));
    return contains?.appleId || artists[0]?.appleId || null;
}

// Resolves the correct Apple Music artist ID by cross-referencing candidates'
// album catalogues against the local library. Falls back to name matching when
// only one candidate exists or when the library has no usable titles.
async function resolveAppleArtistIdWithLibrary(artists, term) {
    if (!Array.isArray(artists) || artists.length === 0) return null;

    const normalizedTerm = (term || '').trim().toLowerCase();

    // Collect all exact name matches first, then contains matches as fallback.
    let candidates = artists.filter(a => (a?.name || '').trim().toLowerCase() === normalizedTerm);
    if (candidates.length === 0) {
        candidates = artists.filter(a => (a?.name || '').trim().toLowerCase().includes(normalizedTerm));
    }
    if (candidates.length === 0) candidates = artists.slice(0, 3);

    // Only one match — no disambiguation needed.
    if (candidates.length === 1) return candidates[0]?.appleId || null;

    // Without library data we can't score, fall back to first candidate.
    const localTitles = buildLocalAlbumTitleSet();
    if (localTitles.size === 0) return candidates[0]?.appleId || null;

    // Fetch up to 3 candidates' albums and score by local library overlap.
    const toScore = candidates.slice(0, 3).filter(c => c?.appleId);
    const scores = await Promise.all(toScore.map(async candidate => {
        try {
            const resp = await fetch(`/api/apple/artist/albums?id=${encodeURIComponent(candidate.appleId)}&limit=100&offset=0`);
            if (!resp.ok) return { candidate, score: 0 };
            const data = await resp.json();
            let score = 0;
            for (const album of (data?.albums || [])) {
                const key = normalizeAlbumTitle(album?.name || '');
                if (key && localTitles.has(key)) score++;
            }
            return { candidate, score };
        } catch {
            return { candidate, score: 0 };
        }
    }));

    scores.sort((a, b) => b.score - a.score);
    return scores[0]?.candidate?.appleId || candidates[0]?.appleId || null;
}

function filterAppleVideosByArtist(videos, term) {
    if (!Array.isArray(videos) || videos.length === 0) {
        return [];
    }
    const normalizedTerm = (term || '').trim().toLowerCase();
    if (!normalizedTerm) {
        return videos;
    }
    return videos.filter(video => {
        const artistName = (video?.artist || '').toLowerCase();
        if (!artistName) {
            return false;
        }
        return artistName === normalizedTerm || artistName.includes(normalizedTerm);
    });
}

async function loadMoreArtistVideos() {
    const state = libraryState.appleExtras;
    if (!state.term || state.loadingVideos || !state.hasMoreVideos) return;
    state.loadingVideos = true;
    const spinner = document.getElementById('appleVideosLoading');
    if (spinner) spinner.style.display = 'block';
    try {
        const url = state.appleArtistId
            ? `/api/apple/artist/videos?id=${encodeURIComponent(state.appleArtistId)}&limit=25&offset=${state.videoOffset}`
            : `/api/apple/search?term=${encodeURIComponent(state.term)}&limit=25&offset=${state.videoOffset}&types=music-videos`;
        const resp = await fetch(url);
        if (!resp.ok) throw new Error('Load more failed');
        const data = await resp.json();
        const incoming = data?.videos || [];
        const newVideos = state.appleArtistId ? incoming : filterAppleVideosByArtist(incoming, state.term);
        // Append to existing array without copying (push is O(n) for new items only)
        for (const v of newVideos) state.videos.push(v);
        state.videoOffset = state.videos.length;
        state.hasMoreVideos = Boolean(data?.hasMoreVideos);
        // Append only the new cards to the DOM
        const container = document.getElementById('appleVideosGrid');
        if (container && newVideos.length > 0) {
            container.insertAdjacentHTML('beforeend', newVideos.map(v => renderVideoCard(v)).join(''));
        }
    } catch (err) {
        console.warn('Load more Apple videos failed', err);
    } finally {
        state.loadingVideos = false;
        if (spinner) spinner.style.display = 'none';
    }
}

function bindAppleAtmosDelegation() {
    const container = document.getElementById('appleAtmosGrid');
    if (!container || container.dataset.delegated) return;
    container.dataset.delegated = 'true';
    container.addEventListener('click', (e) => {
        const card = e.target.closest('.apple-card');
        if (!card) return;
        const localAlbumId = (card.dataset.localAlbumId || '').trim();
        const availabilityFilter = (libraryState.discographyAvailabilityFilter || 'all').toLowerCase();
        if (availabilityFilter === 'in-library' && localAlbumId) {
            globalThis.location.href = `/Library/Album/${encodeURIComponent(localAlbumId)}`;
            return;
        }
        const type = card.dataset.kind;
        const appleUrl = card.dataset.url || '';
        const appleId = card.dataset.id || '';
        openAppleTracklist(type === 'album' ? 'album' : 'track', appleId, appleUrl, 'atmos');
    });
}

function renderAppleAtmos() {
    const container = document.getElementById('appleAtmosGrid');
    if (!container) return;
    bindAppleAtmosDelegation();
    const list = libraryState.appleExtras.atmos || [];
    if (!list.length) {
        container.innerHTML = '<div class="empty-card">No Atmos releases found.</div>';
        return;
    }
    const localAlbumIndex = buildLocalAlbumIndex();
    const currentArtistId = getCurrentLibraryArtistId();
    let localTrackVariantIndex = getLocalTrackVariantIndexForArtist(currentArtistId);
    if (!localTrackVariantIndex && currentArtistId && !libraryState.appleExtras.localTrackVariantIndexPromise) {
        void ensureLocalTrackVariantIndexForArtistAsync(currentArtistId)
            .then(() => renderAppleAtmos())
            .catch(() => {});
    }

    const availabilityFilter = libraryState.discographyAvailabilityFilter || 'all';
    const filteredList = list.filter(item => {
        if (availabilityFilter === 'all') {
            return true;
        }
        const isAlbum = item?.__kind === 'album';
        const inLibrary = isAppleAtmosItemInLibrary(item, isAlbum, localAlbumIndex, localTrackVariantIndex);
        return availabilityFilter === 'in-library' ? inLibrary : !inLibrary;
    });

    if (!filteredList.length) {
        const message = availabilityFilter === 'all'
            ? 'No Atmos releases found.'
            : 'No Atmos releases match the selected library filter.';
        container.innerHTML = `<div class="empty-card">${message}</div>`;
        return;
    }

    container.innerHTML = filteredList
        .map(item => renderAppleCard(item, item.__kind === 'album', localAlbumIndex, localTrackVariantIndex))
        .join('');
}

function bindAppleVideosDelegation() {
    const container = document.getElementById('appleVideosGrid');
    if (!container || container.dataset.delegated) return;
    container.dataset.delegated = 'true';
    container.addEventListener('click', (e) => {
        const previewBtn = e.target.closest('[data-preview]');
        if (previewBtn) {
            e.stopPropagation();
            playVideoPreview(previewBtn.dataset.preview);
            return;
        }
        const downloadBtn = e.target.closest('[data-download]');
        if (downloadBtn) {
            e.stopPropagation();
            downloadAppleVideo(downloadBtn.dataset.download, {
                hasAtmos: toBoolish(downloadBtn.dataset.hasAtmos)
            });
            return;
        }
        const card = e.target.closest('.apple-card.video');
        if (!card) return;
        const key = card.dataset.key;
        toggleVideoSelection(key);
        card.classList.toggle('selected', libraryState.appleExtras.selectedVideoKeys.has(key));
        updateArtistVideoSelectionBar();
    });
}

function renderAppleVideos() {
    const container = document.getElementById('appleVideosGrid');
    if (!container) return;
    bindAppleVideosDelegation();
    let list = libraryState.appleExtras.videos || [];
    const selected = libraryState.appleExtras.selectedVideoKeys;
    if (libraryState.appleExtras.showSelectedOnly && selected.size > 0) {
        list = list.filter(v => selected.has(getVideoKey(v)));
    }
    if (!list.length) {
        updateArtistVideoSelectionBar();
        return;
    }
    container.innerHTML = list.map(video => renderVideoCard(video)).join('');
    updateArtistVideoSelectionBar();
}

function renderAppleCard(item, isAlbum, localAlbumIndex = null, localTrackVariantIndex = null) {
    const image = item.image || '';
    const title = item.name || '';
    let sub = item.artist || '';
    if (!isAlbum && item.album) {
        sub = `${item.artist || ''} • ${item.album}`;
    }
    const kind = isAlbum ? 'album' : 'track';
    const idVal = item.appleId || extractAppleIdFromUrl(item.appleUrl || '');
    const inLibrary = isAppleAtmosItemInLibrary(item, isAlbum, localAlbumIndex, localTrackVariantIndex);
    const localAlbumId = getLocalAlbumIdForAppleAtmosItem(item, isAlbum, localAlbumIndex, localTrackVariantIndex);
    const libraryBadge = inLibrary
        ? '<div class="library-badge"><svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><path d="M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41L9 16.17z"/></svg></div>'
        : '';
    const localAlbumAttr = localAlbumId ? ` data-local-album-id="${escapeHtml(localAlbumId)}"` : '';
    return `
        <div class="apple-card${inLibrary ? ' in-library' : ''}" data-kind="${kind}" data-url="${escapeHtml(item.appleUrl || '')}" data-id="${escapeHtml(idVal)}"${localAlbumAttr}>
            <div class="apple-thumb">${image ? `<img src="${escapeHtml(image)}" alt="${escapeHtml(title)}" loading="lazy" decoding="async" />` : ''}${libraryBadge}</div>
            <div class="apple-title">${escapeHtml(title)}</div>
            <div class="apple-sub">${escapeHtml(sub)}</div>
        </div>
    `;
}

function buildLocalAlbumIndex() {
    const map = new Map();
    const localAlbums = Array.isArray(libraryState.localAlbums) ? libraryState.localAlbums : [];
    localAlbums.forEach(album => {
        const key = normalizeAlbumTitle(album?.title || album?.name || '');
        const id = album?.id ?? album?.localId;
        if (!key || id === null || id === undefined) {
            return;
        }

        const nextStereo = toBoolish(album?.hasStereoVariant);
        const nextAtmos = toBoolish(album?.hasAtmosVariant);
        const existing = map.get(key);
        if (!existing) {
            map.set(key, {
                id: String(id),
                hasStereoVariant: nextStereo,
                hasAtmosVariant: nextAtmos
            });
            return;
        }

        // Merge duplicates by title so one variant does not mask the other.
        const mergedHasStereo = existing.hasStereoVariant || nextStereo;
        const mergedHasAtmos = existing.hasAtmosVariant || nextAtmos;
        let mergedId = existing.id || String(id);
        if (nextAtmos) {
            mergedId = String(id);
        }

        map.set(key, {
            id: mergedId,
            hasStereoVariant: mergedHasStereo,
            hasAtmosVariant: mergedHasAtmos
        });
    });
    return map;
}

function buildLocalAlbumTitleSet() {
    const set = new Set();
    const localAlbums = Array.isArray(libraryState.localAlbums) ? libraryState.localAlbums : [];
    for (const album of localAlbums) {
        const key = normalizeAlbumTitle(album?.title || album?.name || '');
        if (key) set.add(key);
    }
    return set;
}

function getCurrentLibraryArtistId() {
    return String(document.querySelector('[data-artist-id]')?.dataset.artistId || '').trim();
}

function getLocalTrackVariantIndexForArtist(artistId) {
    const normalizedArtistId = String(artistId || '').trim();
    const state = libraryState.appleExtras;
    if (!normalizedArtistId
        || state?.localTrackVariantArtistId !== normalizedArtistId
        || !(state?.localTrackVariantIndex instanceof Map)) {
        return null;
    }
    return state.localTrackVariantIndex;
}

async function ensureLocalTrackVariantIndexForArtistAsync(artistId) {
    const normalizedArtistId = String(artistId || '').trim();
    const state = libraryState.appleExtras;
    if (!normalizedArtistId || !state) {
        return null;
    }

    if (state.localTrackVariantArtistId === normalizedArtistId && state.localTrackVariantIndex instanceof Map) {
        return state.localTrackVariantIndex;
    }

    if (state.localTrackVariantArtistId === normalizedArtistId && state.localTrackVariantIndexPromise) {
        return state.localTrackVariantIndexPromise;
    }

    state.localTrackVariantArtistId = normalizedArtistId;
    state.localTrackVariantIndex = null;
    state.localTrackVariantIndexPromise = (async () => {
        const index = new Map();
        const localAlbums = Array.isArray(libraryState.localAlbums) ? libraryState.localAlbums : [];
        const candidates = localAlbums
            .filter(album => album && Number(album.id) > 0)
            .map(album => ({
                albumId: Number(album.id),
                albumKey: normalizeAlbumTitle(album.title || album.name || ''),
                localAlbumId: String(album.id)
            }))
            .filter(item => item.albumKey);

        for (const candidate of candidates) {
            let tracks;
            try {
                tracks = await fetchJsonOptional(`/api/library/albums/${candidate.albumId}/tracks`);
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

                const titleKey = normalizeAlbumTitle(track?.title || '');
                if (!titleKey) {
                    return;
                }

                const key = `${candidate.albumKey}::${titleKey}`;
                const existing = index.get(key) || {
                    localAlbumId: candidate.localAlbumId,
                    hasStereoVariant: false,
                    hasAtmosVariant: false
                };

                const variant = String(track?.audioVariant || '').trim().toLowerCase();
                const rowHasAtmos = variant === 'atmos'
                    || (variant === '' && toBoolish(track?.hasAtmosVariant));
                const rowHasStereo = variant === 'stereo'
                    || (variant === '' && toBoolish(track?.hasStereoVariant))
                    || (!rowHasAtmos && variant === '');

                existing.hasAtmosVariant = existing.hasAtmosVariant || rowHasAtmos;
                existing.hasStereoVariant = existing.hasStereoVariant || rowHasStereo;
                if (!existing.localAlbumId) {
                    existing.localAlbumId = candidate.localAlbumId;
                }

                index.set(key, existing);
            });
        }

        state.localTrackVariantIndex = index;
        return index;
    })();

    try {
        return await state.localTrackVariantIndexPromise;
    } finally {
        state.localTrackVariantIndexPromise = null;
    }
}

function getLocalAlbumMatchForAppleAtmosItem(item, isAlbum, localAlbumIndex) {
    const index = localAlbumIndex instanceof Map ? localAlbumIndex : buildLocalAlbumIndex();
    if (index.size === 0) {
        return null;
    }

    if (isAlbum) {
        const albumKey = normalizeAlbumTitle(item?.name || item?.title || '');
        return albumKey ? (index.get(albumKey) || null) : null;
    }

    const trackAlbumKey = normalizeAlbumTitle(item?.album || '');
    if (trackAlbumKey && index.has(trackAlbumKey)) {
        return index.get(trackAlbumKey) || null;
    }

    return null;
}

function getLocalTrackVariantMatchForAppleAtmosItem(item, localTrackVariantIndex) {
    const index = localTrackVariantIndex instanceof Map ? localTrackVariantIndex : null;
    if (!index || index.size === 0) {
        return null;
    }

    const albumKey = normalizeAlbumTitle(item?.album || '');
    const titleKey = normalizeAlbumTitle(item?.name || item?.title || '');
    if (!albumKey || !titleKey) {
        return null;
    }

    return index.get(`${albumKey}::${titleKey}`) || null;
}

function isAppleAtmosItemInLibrary(item, isAlbum, localAlbumIndex, localTrackVariantIndex = null) {
    if (!isAlbum) {
        const trackMatch = getLocalTrackVariantMatchForAppleAtmosItem(item, localTrackVariantIndex);
        if (!trackMatch) {
            return false;
        }
        return trackMatch.hasAtmosVariant === true;
    }

    const match = getLocalAlbumMatchForAppleAtmosItem(item, isAlbum, localAlbumIndex);
    // Apple Atmos panel should only be marked in-library when an Atmos local variant exists.
    return match?.hasAtmosVariant === true;
}

function getLocalAlbumIdForAppleAtmosItem(item, isAlbum, localAlbumIndex, localTrackVariantIndex = null) {
    if (!isAlbum) {
        const trackMatch = getLocalTrackVariantMatchForAppleAtmosItem(item, localTrackVariantIndex);
        if (trackMatch?.localAlbumId) {
            return trackMatch.localAlbumId;
        }
    }

    const albumMatch = getLocalAlbumMatchForAppleAtmosItem(item, isAlbum, localAlbumIndex);
    return albumMatch?.id || '';
}

function renderVideoCard(video) {
    const image = video.image || '';
    const title = video.name || '';
    const artist = video.artist || '';
    const key = getVideoKey(video);
    const selectedClass = libraryState.appleExtras.selectedVideoKeys.has(key) ? 'selected' : '';
    const year = video.releaseDate ? new Date(video.releaseDate).getFullYear() : '';
    const subText = year ? `${artist} · ${year}` : artist;
    const hasManifestAtmos = video?.hasAtmosDownloadable === true;
    const hasCatalogAtmos = video?.hasAtmosDownloadable == null && video?.hasAtmos === true;
    const hasStereoOnly = video?.hasAtmosDownloadable === false;
    let capabilityBadge = '';
    if (hasManifestAtmos || hasCatalogAtmos) {
        capabilityBadge = '<div class="artist-video-capabilities"><span class="artist-video-badge atmos-verified">Atmos</span></div>';
    } else if (hasStereoOnly) {
        capabilityBadge = '<div class="artist-video-capabilities"><span class="artist-video-badge stereo-only">Stereo</span></div>';
    }
    const playBtn = video.previewUrl
        ? `<button class="video-overlay-btn video-overlay-btn--play" data-preview="${escapeHtml(video.previewUrl)}" type="button" aria-label="Play preview">
               <svg width="24" height="24" viewBox="0 0 24 24" fill="currentColor"><path d="M8 5v14l11-7z"/></svg>
           </button>`
        : '';
    const hasAtmosFlag = hasManifestAtmos || hasCatalogAtmos ? 'true' : 'false';
    const downloadBtn = video.appleUrl
        ? `<button class="video-overlay-btn video-overlay-btn--download" data-download="${escapeHtml(video.appleUrl)}" data-has-atmos="${hasAtmosFlag}" type="button" aria-label="Download">
               <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>
           </button>`
        : '';
    return `
        <div class="apple-card video ${selectedClass}" data-key="${escapeHtml(key)}">
            <div class="apple-thumb">
                ${image ? `<img src="${escapeHtml(image)}" alt="${escapeHtml(title)}" loading="lazy" decoding="async" />` : ''}
                ${playBtn}
                ${downloadBtn}
            </div>
            <div class="apple-title">${escapeHtml(title)}</div>
            <div class="apple-sub">${escapeHtml(subText)}</div>
            ${capabilityBadge}
        </div>
    `;
}

function toBoolish(value) {
    if (value === true || value === false) {
        return value;
    }
    if (typeof value === 'number') {
        return value !== 0;
    }
    if (typeof value === 'string') {
        const normalized = value.trim().toLowerCase();
        return normalized === 'true' || normalized === '1' || normalized === 'yes';
    }
    return false;
}

function toggleVideoSelection(key) {
    if (!key) return;
    const set = libraryState.appleExtras.selectedVideoKeys;
    if (set.has(key)) {
        set.delete(key);
    } else {
        set.add(key);
    }
}

function updateArtistVideoSelectionBar() {
    const bar = document.getElementById('artist-video-selection-bar');
    const countEl = document.getElementById('artist-selection-count');
    if (!bar || !countEl) return;
    const count = libraryState.appleExtras.selectedVideoKeys.size;
    countEl.textContent = `${count} selected`;
    if (count > 0) {
        bar.classList.add('active');
    } else {
        bar.classList.remove('active');
    }
}

function getVideoKey(video) {
    if (!video) return '';
    return video.appleUrl || video.appleId || video.name || '';
}

function extractAppleIdFromUrl(url) {
    if (!url) return '';
    const raw = String(url).trim();
    if (!raw) return '';

    // Apple track links commonly use album URLs with ?i=<trackId>; prefer this first.
    const queryIdMatch = /[?&]i=(\d+)/i.exec(raw);
    if (queryIdMatch?.[1]) {
        return queryIdMatch[1];
    }

    try {
        const parsed = new URL(raw);
        const iParam = parsed.searchParams.get('i');
        if (iParam && /^\d+$/.test(iParam)) return iParam;
        const parts = parsed.pathname.split('/').filter(Boolean);
        // pick last numeric-ish segment
        for (let i = parts.length - 1; i >= 0; i--) {
            const seg = parts[i];
            if (/^\d+$/.test(seg)) return seg;
        }
    } catch {
        // ignore
    }
    return '';
}

function openAppleTracklist(type, appleId, appleUrl, audioVariant) {
    const idVal = appleId || extractAppleIdFromUrl(appleUrl || '');
    if (!idVal) {
        showToast('Missing Apple ID for this item.', true);
        return;
    }
    const targetType = type === 'track' ? 'track' : 'album';
    const qs = new URLSearchParams();
    qs.set('source', 'apple');
    qs.set('type', targetType);
    qs.set('id', idVal);
    if (appleUrl) {
        qs.set('appleUrl', appleUrl);
        qs.set('appleId', idVal);
    }
    const normalizedVariant = String(audioVariant || '').trim().toLowerCase();
    if (normalizedVariant === 'atmos' || normalizedVariant === 'stereo') {
        qs.set('audioVariant', normalizedVariant);
    }
    globalThis.location.href = `/Tracklist?${qs.toString()}`;
}

function escapeHtml(text) {
    if (text === null || text === undefined) return '';
    const div = document.createElement('div');
    div.textContent = String(text);
    return div.innerHTML;
}

function isSpotifySourceUrl(url) {
    if (typeof url !== 'string') {
        return false;
    }

    const trimmed = url.trim();
    if (!trimmed) {
        return false;
    }

    if (trimmed.toLowerCase().startsWith('spotify:')) {
        return true;
    }

    try {
        const parsed = new URL(trimmed, globalThis.location.origin);
        const host = parsed.hostname.toLowerCase();
        return host === 'spotify.com' || host.endsWith('.spotify.com');
    } catch {
        return false;
    }
}

function playVideoPreview(url) {
    if (!url) return;
    const safeUrl = toSafeHttpUrl(url);
    if (!safeUrl) {
        showToast('Invalid preview URL.', true);
        return;
    }
    const video = document.createElement('video');
    video.src = safeUrl;
    video.controls = true;
    video.autoplay = true;
    video.style.width = '100%';
    video.style.maxHeight = '70vh';

    if (globalThis.DeezSpoTag?.ui?.showModal) {
        globalThis.DeezSpoTag.ui.showModal({
            title: 'Preview',
            message: '',
            allowHtml: true,
            contentElement: video,
            buttons: [{ label: 'Close', value: true, primary: true }]
        });
    } else {
        const w = globalThis.open('', '_blank', 'noopener,width=640,height=360');
        if (w) {
            w.document.title = 'Preview';
            w.document.body.appendChild(video);
        }
    }
}

async function downloadAppleVideo(appleUrl, options = null) {
    if (!appleUrl) return;
    const hasAtmos = options?.hasAtmos === true || options?.hasAtmos === 'true';
    const videoMetadata = {
        isVideo: true,
        collectionType: 'music-video',
        contentType: 'video',
        hasAtmos: Boolean(hasAtmos)
    };
    try {
        if (globalThis.DeezSpoTagDownload?.addToQueue) {
            await globalThis.DeezSpoTagDownload.addToQueue(appleUrl, 0, null, { metadata: videoMetadata });
            return;
        }
        const resp = await fetch('/api/apple/videos/download', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                appleUrl,
                metadata: videoMetadata
            })
        });
        const data = await resp.json();
        if (!resp.ok || !data?.success) {
            throw new Error(data?.error || 'Download failed');
        }
        if (globalThis.DeezSpoTag?.ui?.showToast) {
            globalThis.DeezSpoTag.ui.showToast('Video download started');
        }
    } catch (err) {
        console.error('Video download failed', err);
        if (globalThis.DeezSpoTag?.ui?.showToast) {
            globalThis.DeezSpoTag.ui.showToast(err.message || 'Download failed', { type: 'error' });
        }
    }
}

async function cleanupMissingLibraryFiles() {
    if (!await DeezSpoTag.ui.confirm('Remove entries for files that no longer exist on disk?', { title: 'Cleanup Missing Files' })) {
        return;
    }
    try {
        const result = await fetchJson('/api/library/maintenance/cleanup-missing', { method: 'POST' });
        if (result?.ok === false) {
            showToast('Library DB not configured.', true);
            return;
        }
        const removed = result?.removed ?? 0;
        showToast(`Removed ${removed.toLocaleString()} missing files.`);
        await Promise.all([loadLibraryScanStatus(), loadArtists()]);
    } catch (error) {
        showToast(`Cleanup failed: ${error.message}`, true);
    }
}

async function clearLibraryData() {
    try {
        let confirmed = false;
        try {
            if (globalThis.DeezSpoTag?.ui?.confirm) {
                confirmed = await DeezSpoTag.ui.confirm(
                    'Clear all library metadata? Your music files will not be deleted.',
                    { title: 'Clear Library', okText: 'Clear Library', cancelText: 'Cancel' }
                );
            } else {
                confirmed = globalThis.confirm('Clear all library metadata? Your music files will not be deleted.');
            }
        } catch (dialogError) {
            console.error('Clear library confirm dialog failed:', dialogError);
            confirmed = globalThis.confirm('Clear all library metadata? Your music files will not be deleted.');
        }
        if (!confirmed) {
            return;
        }
        showToast('Clearing library metadata...');
        const clearButton = document.getElementById('clearLibrary');
        if (clearButton instanceof HTMLButtonElement) {
            clearButton.disabled = true;
        }
        const result = await fetchJson('/api/library/maintenance/clear', { method: 'POST' });
        if (result?.ok === false) {
            showToast('Library DB not configured.', true);
            return;
        }
        const artistsRemoved = Number(result?.artistsRemoved || 0);
        const albumsRemoved = Number(result?.albumsRemoved || 0);
        const tracksRemoved = Number(result?.tracksRemoved || 0);
        showToast(
            `Library cleared: ${artistsRemoved.toLocaleString()} artists, ${albumsRemoved.toLocaleString()} albums, ${tracksRemoved.toLocaleString()} tracks removed.`
        );
        if (globalThis.DeezSpoTag?.ui?.alert) {
            await DeezSpoTag.ui.alert(
                `Removed ${artistsRemoved.toLocaleString()} artists, ${albumsRemoved.toLocaleString()} albums, and ${tracksRemoved.toLocaleString()} tracks.`,
                { title: 'Library Cleared' }
            );
        }
        await Promise.all([loadLibraryScanStatus(), loadArtists()]);
        setTimeout(() => {
            void runLocalScan(false, false);
        }, 1200);
    } catch (error) {
        console.error('clearLibraryData error:', error);
        showToast(`Clear failed: ${error.message}`, true);
    } finally {
        const clearButton = document.getElementById('clearLibrary');
        if (clearButton instanceof HTMLButtonElement) {
            clearButton.disabled = false;
        }
    }
}

// clearLibraryData is called via inline onclick on the button in Index.cshtml

async function cancelLibraryScan() {
    try {
        const result = await fetchJson('/api/library/scan/cancel', { method: 'POST' });
        if (result?.cancelled) {
            showToast('Library scan cancelled.');
        } else {
            showToast('No scan running.', true);
        }
        await loadLibraryScanStatus();
    } catch (error) {
        showToast(`Cancel failed: ${error.message}`, true);
    }
}

function bindBootstrapScanActions(elements) {
    bindLibraryAction(elements.refreshButton, async () => {
        await runLocalScan(false, true);
        await Promise.all([loadLibrarySettings(), loadFolders(), loadArtists(), loadLibraryScanStatus()]);
    });
    bindLibraryAction(elements.scanButton, async () => {
        await runLocalScan(false, false);
        await Promise.all([loadLibrarySettings(), loadFolders(), loadArtists(), loadLibraryScanStatus()]);
    });
    bindLibraryAction(elements.cancelScanButton, cancelLibraryScan);
    bindLibraryAction(elements.refreshImagesButton, async () => {
        if (!await DeezSpoTag.ui.confirm('Rebuild all cached thumbnails? This may take a moment.', { title: 'Refresh Images' })) {
            return;
        }
        bumpImageCacheKey();
        await runLocalScan(true);
        await Promise.all([loadLibrarySettings(), loadFolders(), loadArtists(), loadLibraryScanStatus()]);
    });
    bindLibraryAction(elements.cleanupButton, cleanupMissingLibraryFiles);
    bindLibraryAction(elements.saveButton, saveLibrarySettings);
}

function getLibraryLoadTargets() {
    const shouldLoadFolders = document.getElementById('foldersContainer');
    const shouldLoadViewFolders = document.getElementById('libraryViewSelect');
    const shouldLoadAlbumDestination = document.getElementById('downloadDestinationAlbum');
    return {
        shouldLoadSettings: document.getElementById('fuzzyThreshold') && document.getElementById('includeAllFolders'),
        shouldLoadFolders,
        shouldLoadViewFolders,
        shouldLoadArtists: document.getElementById('artistsGrid'),
        shouldLoadScanStatus: document.getElementById('libraryLastScan') || document.getElementById('libraryTrackCount'),
        shouldLoadDownload: document.getElementById('downloadLocationHint'),
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
        shouldLoadAnalysis: document.getElementById('analysisStatus'),
        analysisButton: document.getElementById('runAnalysis'),
        shouldLoadFavorites: document.getElementById('favoritesContainer'),
        shouldDeferViewFolderLoad: !!(shouldLoadViewFolders && !shouldLoadFolders && !shouldLoadAlbumDestination)
    };
}

function bindIndexActionsDropdown() {
    const indexToggle = document.getElementById('indexActionsToggle');
    const indexDropdown = document.getElementById('indexActionsDropdown');
    if (!indexToggle || !indexDropdown) {
        return;
    }
    indexToggle.addEventListener('click', (event) => {
        event.stopPropagation();
        const isOpen = indexDropdown.classList.contains('is-open');
        indexDropdown.classList.toggle('is-open', !isOpen);
        indexToggle.setAttribute('aria-expanded', String(!isOpen));
    });
    document.addEventListener('click', (event) => {
        if (!indexDropdown.contains(event.target) && event.target !== indexToggle) {
            indexDropdown.classList.remove('is-open');
            indexToggle.setAttribute('aria-expanded', 'false');
        }
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

function queueFolderAndDownloadLoadTasks(targets, tasks) {
    const shouldLoadFolderData = targets.shouldLoadFolders || targets.shouldLoadViewFolders || targets.shouldLoadAlbumDestination;
    const shouldLoadDownloadData = targets.shouldLoadDownload || targets.shouldLoadAlbumDestination;
    if (shouldLoadFolderData && shouldLoadDownloadData) {
        tasks.push((async () => {
            await loadDownloadLocation();
            await loadFolders();
        })());
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
            await applyLibraryViewFilter();
        });
    }
    if (searchInput) {
        searchInput.addEventListener('input', () => {
            if (libraryState.artistSearchTimer) {
                clearTimeout(libraryState.artistSearchTimer);
            }
            libraryState.artistSearchTimer = setTimeout(async () => {
                libraryState.artistSearchQuery = searchInput.value || '';
                await applyLibraryViewFilter();
            }, 180);
        });
    }
    if (sortSelect) {
        sortSelect.addEventListener('change', async () => {
            libraryState.artistSortKey = sortSelect.value || 'name-asc';
            await applyLibraryViewFilter();
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
    if (shouldLoadAnalysis) {
        setInterval(async () => {
            await loadAnalysisActivity();
            await loadAnalysisStatus();
        }, 15000);
    }
    if (shouldLoadScanStatus) {
        setInterval(loadLibraryScanStatus, 30000);
    }
}

document.addEventListener('DOMContentLoaded', async () => {
    try {
        updateTopSongsTracklistLink(null);
        const elements = getLibraryBootstrapElements();
        bindBootstrapScanActions(elements);
        wireExclusiveFolderDestinationRoles();
        bindFolderModalActions(elements);
        bindFolderPathBrowser(elements);
        bindFolderPathInput(elements.folderPathInput, updateSaveFolderState);
        bindFolderChangeInput(elements.folderConvertEnabledInput, syncFolderConversionFieldsState);
        bindFolderChangeInput(elements.folderConvertFormatInput, syncFolderConversionFieldsState);
        bindFolderChangeInput(elements.folderConvertBitrateInput, syncFolderConversionFieldsState);
        updateSaveFolderState();
        syncFolderConversionFieldsState();

        const targets = getLibraryLoadTargets();
        if (targets.shouldLoadArtistAlbums) {
            initializeDiscographyFilterState();
        }

        if (targets.shouldDeferViewFolderLoad) {
            ensureLibraryViewDefaultOption();
        }

        if (targets.searchInput) {
            targets.searchInput.value = libraryState.artistSearchQuery;
        }
        if (targets.sortSelect) {
            targets.sortSelect.value = libraryState.artistSortKey;
        }

        bindIndexActionsDropdown();
        await runInitialLibraryLoads(targets);
        bindSavedPreferenceButtons();
        await initializeArtistAlbumsPage(targets.shouldLoadArtistAlbums);

        await initWatchlistToggle();
        await initSpotifyIdEditor();
        initArtistActionsDropdown();
        initDiscographyFilters();

        bindLibraryFilterEvents(targets.viewSelect, targets.searchInput, targets.sortSelect);
        bindAlbumDownloadButton(targets.downloadAlbumButton);
        if (targets.analysisButton) {
            targets.analysisButton.addEventListener('click', runAnalysis);
        }
        startLibraryRefreshIntervals(targets.shouldLoadAnalysis, targets.shouldLoadScanStatus);
    } catch (error) {
        const urlHint = error?.libraryUrl ? ` (${error.libraryUrl})` : '';
        DeezSpoTag.ui.alert(`Library error: ${error.message}${urlHint}`, { title: 'Library Error' });
        console.error('Library initialization failed.', error);
    }
});

document.addEventListener('click', event => {
    const target = event.target.closest('[data-spotify-url]');
    if (!target) {
        return;
    }
    event.preventDefault();
    event.stopPropagation();
    const url = target.dataset.spotifyUrl;
    if (!url) {
        return;
    }
    if (target.classList.contains('track-action')) {
        playSpotifyTrackInApp(url, target);
        return;
    }
    handleSpotifyRedirect(url, {
        title: target.dataset.spotifyTitle || '',
        artist: target.dataset.spotifyArtist || ''
    });
});

document.addEventListener('click', event => {
    const target = event.target.closest('[data-library-spectrogram-url]');
    if (!target) {
        return;
    }

    event.preventDefault();
    event.stopPropagation();
    const spectrogramUrl = target.dataset.librarySpectrogramUrl;
    const spectrogramTitle = target.dataset.librarySpectrogramTitle || 'Track';
    const trackId = target.dataset.libraryTrackId || '';
    const trackFilePath = target.dataset.libraryTrackFilePath || '';
    if (!spectrogramUrl) {
        return;
    }

    const menu = target.closest('details.track-actions-menu');
    if (menu) {
        menu.removeAttribute('open');
    }

    openLibrarySpectrogramModal(spectrogramUrl, spectrogramTitle, trackId, trackFilePath);
});

document.addEventListener('click', event => {
    const button = event.target.closest('[data-library-play-track]');
    if (!button) {
        return;
    }

    event.preventDefault();
    event.stopPropagation();
    const trackId = button.dataset.libraryPlayTrack;
    const preferredPath = button.dataset.libraryPlayPath || '';
    if (!trackId) {
        return;
    }

    playLocalLibraryTrackInApp(trackId, button, preferredPath);
});
