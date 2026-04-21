// Extracted from library.js: Apple artist extras and unmatched resolver module

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
        const previewButton = e.target.closest('[data-apple-preview-url]');
        if (previewButton) {
            e.stopPropagation();
            void playDirectPreviewInApp(previewButton.dataset.applePreviewUrl, previewButton);
            return;
        }
        const card = e.target.closest('.apple-card');
        if (!card) return;
        const localAlbumId = (card.dataset.localAlbumId || '').trim();
        const availabilityFilter = (libraryState.discographyAvailabilityFilter || 'all').toLowerCase();
        if (availabilityFilter === 'in-library' && localAlbumId) {
            globalThis.location.href = buildLibraryScopedUrl(`/Library/Album/${encodeURIComponent(localAlbumId)}`);
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
    const previewUrl = isAlbum ? '' : String(item.previewUrl || item.preview || '').trim();
    const libraryBadge = inLibrary
        ? '<div class="library-badge"><svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><path d="M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41L9 16.17z"/></svg></div>'
        : '';
    const previewButton = previewUrl
        ? `<button class="video-overlay-btn video-overlay-btn--play apple-audio-preview-btn" data-apple-preview-url="${escapeHtml(previewUrl)}" type="button" aria-label="Play preview">
               <svg width="24" height="24" viewBox="0 0 24 24" fill="currentColor"><path d="M8 5v14l11-7z"/></svg>
           </button>`
        : '';
    const localAlbumAttr = localAlbumId ? ` data-local-album-id="${escapeHtml(localAlbumId)}"` : '';
    return `
        <div class="apple-card${inLibrary ? ' in-library' : ''}" data-kind="${kind}" data-url="${escapeHtml(item.appleUrl || '')}" data-id="${escapeHtml(idVal)}"${localAlbumAttr}>
            <div class="apple-thumb">${image ? `<img src="${escapeHtml(image)}" alt="${escapeHtml(title)}" loading="lazy" decoding="async" />` : ''}${previewButton}${libraryBadge}</div>
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

function resolveCleanupScopeContext() {
    const selectedFolder = getSelectedLibraryViewFolder();
    const selectedFolderId = selectedFolder ? Number(selectedFolder.id) : null;
    const scopeLabel = selectedFolder?.displayName || 'Library';
    const runLabel = selectedFolder
        ? `Cleanup ${scopeLabel}`
        : 'Cleanup Missing';
    const confirmMessage = selectedFolder
        ? `Remove missing-file entries only for ${scopeLabel}?`
        : 'Remove entries for files that no longer exist on disk?';

    return {
        selectedFolder,
        selectedFolderId,
        scopeLabel,
        runLabel,
        confirmMessage
    };
}

function setCleanupMissingRunningState(cleanupButton, runLabel) {
    const cleanupLabel = cleanupButton?.querySelector?.('span') || null;
    cleanupState.running = true;
    cleanupState.labelElement = cleanupLabel;
    cleanupState.originalLabel = (cleanupLabel?.textContent || 'Cleanup Missing').trim() || 'Cleanup Missing';
    cleanupState.startedAtMs = Date.now();

    if (cleanupButton instanceof HTMLButtonElement) {
        cleanupButton.disabled = true;
        cleanupButton.setAttribute('aria-busy', 'true');
    }

    if (cleanupState.labelElement) {
        cleanupState.labelElement.textContent = `${runLabel} (0s)`;
    }

    cleanupState.timerId = globalThis.setInterval(() => {
        if (!cleanupState.running) {
            return;
        }
        const elapsedSeconds = Math.max(0, Math.floor((Date.now() - cleanupState.startedAtMs) / 1000));
        if (cleanupState.labelElement) {
            cleanupState.labelElement.textContent = `${runLabel} (${elapsedSeconds}s)`;
        }
    }, 1000);
}

function resetCleanupMissingRunningState(cleanupButton) {
    cleanupState.running = false;
    if (cleanupState.timerId) {
        globalThis.clearInterval(cleanupState.timerId);
        cleanupState.timerId = 0;
    }

    if (cleanupButton instanceof HTMLButtonElement) {
        cleanupButton.disabled = false;
        cleanupButton.removeAttribute('aria-busy');
    }

    if (cleanupState.labelElement) {
        cleanupState.labelElement.textContent = cleanupState.originalLabel;
    }
}

function buildCleanupMissingUrl(selectedFolderId) {
    const params = new URLSearchParams();
    if (selectedFolderId !== null) {
        params.set('folderId', String(selectedFolderId));
    }
    const suffix = params.toString();
    return suffix
        ? `/api/library/maintenance/cleanup-missing?${suffix}`
        : '/api/library/maintenance/cleanup-missing';
}

async function cleanupMissingLibraryFiles() {
    if (cleanupState.running) {
        showToast('Cleanup Missing is already running.');
        return;
    }

    const {
        selectedFolder,
        selectedFolderId,
        scopeLabel,
        runLabel,
        confirmMessage
    } = resolveCleanupScopeContext();
    const isConfirmed = await DeezSpoTag.ui.confirm(
        confirmMessage,
        { title: selectedFolder ? `Cleanup ${scopeLabel}` : 'Cleanup Missing Files' }
    );
    if (!isConfirmed) {
        return;
    }

    const cleanupButton = document.getElementById('cleanupLibrary');
    setCleanupMissingRunningState(cleanupButton, runLabel);

    showToast(selectedFolder
        ? `${runLabel} started...`
        : 'Cleanup Missing started...');

    try {
        const url = buildCleanupMissingUrl(selectedFolderId);
        const result = await fetchJson(url, { method: 'POST' });
        if (result?.ok === false) {
            showToast('Library DB not configured.', true);
            return;
        }
        const removed = result?.removed ?? 0;
        showToast(selectedFolder
            ? `Removed ${removed.toLocaleString()} missing files from ${scopeLabel}.`
            : `Removed ${removed.toLocaleString()} missing files.`);
        await Promise.all([loadLibraryScanStatus(), loadArtists()]);
    } catch (error) {
        showToast(`Cleanup failed: ${error.message}`, true);
    } finally {
        resetCleanupMissingRunningState(cleanupButton);
    }
}

async function clearLibraryData() {
    const selectedFolder = getSelectedLibraryViewFolder();
    const selectedFolderId = selectedFolder ? Number(selectedFolder.id) : null;
    const scopeLabel = selectedFolder?.displayName || 'Library';
    try {
        const confirmed = await confirmClearLibraryData(selectedFolder, scopeLabel);
        if (!confirmed) {
            return;
        }
        showToast(selectedFolder ? `Clearing ${scopeLabel} metadata...` : 'Clearing library metadata...');
        const clearButton = document.getElementById('clearLibrary');
        if (clearButton instanceof HTMLButtonElement) {
            clearButton.disabled = true;
        }
        const params = new URLSearchParams();
        if (selectedFolderId !== null) {
            params.set('folderId', String(selectedFolderId));
        }
        const suffix = params.toString();
        const url = suffix
            ? `/api/library/maintenance/clear?${suffix}`
            : '/api/library/maintenance/clear';
        const result = await fetchJson(url, { method: 'POST' });
        if (result?.ok === false) {
            showToast('Library DB not configured.', true);
            return;
        }
        const counts = getClearLibraryCounts(result);
        showToast(buildClearLibrarySummaryMessage(selectedFolder, scopeLabel, counts));
        if (globalThis.DeezSpoTag?.ui?.alert) {
            await DeezSpoTag.ui.alert(
                buildClearLibraryAlertMessage(selectedFolder, scopeLabel, counts),
                { title: selectedFolder ? `${scopeLabel} Cleared` : 'Library Cleared' }
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

function getClearLibraryConfirmMessage(selectedFolder, scopeLabel) {
    return selectedFolder
        ? `Clear indexed metadata only for ${scopeLabel}? Your music files will not be deleted.`
        : 'Clear all library metadata? Your music files will not be deleted.';
}

async function confirmClearLibraryData(selectedFolder, scopeLabel) {
    const message = getClearLibraryConfirmMessage(selectedFolder, scopeLabel);
    try {
        if (globalThis.DeezSpoTag?.ui?.confirm) {
            return await DeezSpoTag.ui.confirm(message, {
                title: selectedFolder ? `Clear ${scopeLabel}` : 'Clear Library',
                okText: selectedFolder ? `Clear ${scopeLabel}` : 'Clear Library',
                cancelText: 'Cancel'
            });
        }
        return globalThis.confirm(message);
    } catch (dialogError) {
        console.error('Clear library confirm dialog failed:', dialogError);
        return globalThis.confirm(message);
    }
}

function getClearLibraryCounts(result) {
    return {
        artistsRemoved: Number(result?.artistsRemoved || 0),
        albumsRemoved: Number(result?.albumsRemoved || 0),
        tracksRemoved: Number(result?.tracksRemoved || 0)
    };
}

function buildClearLibrarySummaryMessage(selectedFolder, scopeLabel, counts) {
    if (selectedFolder) {
        return `${scopeLabel} cleared: ${counts.artistsRemoved.toLocaleString()} artists, ${counts.albumsRemoved.toLocaleString()} albums, ${counts.tracksRemoved.toLocaleString()} tracks removed.`;
    }
    return `Library cleared: ${counts.artistsRemoved.toLocaleString()} artists, ${counts.albumsRemoved.toLocaleString()} albums, ${counts.tracksRemoved.toLocaleString()} tracks removed.`;
}

function buildClearLibraryAlertMessage(selectedFolder, scopeLabel, counts) {
    if (selectedFolder) {
        return `Removed ${counts.artistsRemoved.toLocaleString()} artists, ${counts.albumsRemoved.toLocaleString()} albums, and ${counts.tracksRemoved.toLocaleString()} tracks from ${scopeLabel}.`;
    }
    return `Removed ${counts.artistsRemoved.toLocaleString()} artists, ${counts.albumsRemoved.toLocaleString()} albums, and ${counts.tracksRemoved.toLocaleString()} tracks.`;
}

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
    bindLibraryAction(elements.clearButton, clearLibraryData);
    bindUnmatchedArtistResolverActions(elements);
    bindLibraryAction(elements.saveButton, saveLibrarySettings);
}

function bindUnmatchedArtistResolverActions(elements) {
    bindLibraryAction(elements.resolveUnmatchedArtistsButton, async () => {
        await openUnmatchedArtistsModal(elements);
    });
    bindLibraryAction(elements.unmatchedModalCloseButton, () => closeUnmatchedArtistsModal(elements));
    bindLibraryAction(elements.unmatchedModalBackdrop, () => closeUnmatchedArtistsModal(elements));
    bindLibraryAction(elements.unmatchedModalRefreshButton, async () => {
        await loadUnmatchedArtistsForResolver(elements, true);
    });
    bindLibraryAction(elements.unmatchedModalApplyHighConfidenceButton, async () => {
        await applyBestSuggestionsForHighConfidence(elements);
    });
    if (elements.unmatchedModalSearchInput && !elements.unmatchedModalSearchInput.dataset.bound) {
        elements.unmatchedModalSearchInput.dataset.bound = 'true';
        elements.unmatchedModalSearchInput.addEventListener('input', () => {
            libraryState.unmatchedArtistResolver.filter = elements.unmatchedModalSearchInput?.value?.trim() || '';
            renderUnmatchedArtistsResolverList(elements);
        });
    }
}

async function openUnmatchedArtistsModal(elements) {
    if (!elements.unmatchedModal) {
        return;
    }

    elements.unmatchedModal.classList.remove('hidden');
    elements.unmatchedModal.dataset.open = 'true';
    document.body.classList.add('app-modal-open');
    document.documentElement.classList.add('app-modal-open');
    await loadUnmatchedArtistsForResolver(elements, false);
}

function closeUnmatchedArtistsModal(elements) {
    if (!elements.unmatchedModal) {
        return;
    }

    elements.unmatchedModal.classList.add('hidden');
    delete elements.unmatchedModal.dataset.open;
    document.body.classList.remove('app-modal-open');
    document.documentElement.classList.remove('app-modal-open');
}

async function loadUnmatchedArtistsForResolver(elements, forceRefresh) {
    const state = libraryState.unmatchedArtistResolver;
    if (state.loading) {
        return;
    }

    if (state.initialized && !forceRefresh) {
        renderUnmatchedArtistsResolverList(elements);
        return;
    }

    state.loading = true;
    if (elements.unmatchedModalStatus) {
        elements.unmatchedModalStatus.textContent = 'Loading unmatched artists...';
    }
    if (elements.unmatchedModalList) {
        elements.unmatchedModalList.innerHTML = '';
    }

    try {
        const payload = await fetchJson('/api/library/artists/unmatched-spotify?limit=200');
        const items = Array.isArray(payload) ? payload : [];
        state.items = items.map(item => ({
            artistId: Number(item.artistId ?? item.id ?? 0),
            artistName: String(item.artistName ?? item.name ?? '').trim()
        })).filter(item => item.artistId > 0 && item.artistName);
        state.suggestions.clear();
        state.initialized = true;
        renderUnmatchedArtistsResolverList(elements);
    } catch (error) {
        if (elements.unmatchedModalStatus) {
            elements.unmatchedModalStatus.textContent = `Failed to load unmatched artists: ${error.message}`;
        }
    } finally {
        state.loading = false;
    }
}

function renderUnmatchedArtistsResolverList(elements) {
    const state = libraryState.unmatchedArtistResolver;
    if (!elements.unmatchedModalList || !elements.unmatchedModalStatus) {
        return;
    }

    const filter = (state.filter || '').trim().toLowerCase();
    const rows = state.items
        .filter(item => !filter || item.artistName.toLowerCase().includes(filter));

    if (rows.length === 0) {
        elements.unmatchedModalStatus.textContent = state.items.length === 0
            ? 'No unmatched artists found.'
            : 'No artists match your filter.';
        elements.unmatchedModalList.innerHTML = '';
        return;
    }

    elements.unmatchedModalStatus.textContent = `${rows.length.toLocaleString()} unmatched artist(s).`;
    elements.unmatchedModalList.innerHTML = rows.map(item => {
        const suggestions = state.suggestions.get(item.artistId) || [];
        const options = suggestions.length > 0
            ? suggestions.map(suggestion => {
                const overlapText = Number(suggestion.localAlbumOverlap || 0) > 0
                    ? ` • overlap ${suggestion.localAlbumOverlap}`
                    : '';
                const catalogText = Number(suggestion.totalAlbums || 0) > 0
                    ? ` • ${Number(suggestion.totalAlbums || 0)} albums / ${Number(suggestion.totalTracks || 0)} tracks`
                    : '';
                const verifiedText = suggestion.verified ? ' • verified' : '';
                return `<option value="${escapeHtml(suggestion.spotifyId)}">${escapeHtml(suggestion.name)}${overlapText}${catalogText}${verifiedText}</option>`;
            }).join('')
            : '<option value="">Load suggestions first</option>';

        return `
            <div class="unmatched-artist-row" data-artist-id="${escapeHtml(String(item.artistId || ''))}">
                <div class="unmatched-artist-row__name">${escapeHtml(item.artistName)}</div>
                <div class="unmatched-artist-row__suggestions">
                    <select data-suggestion-select ${suggestions.length === 0 ? 'disabled' : ''}>${options}</select>
                </div>
                <div class="unmatched-artist-row__actions">
                    <button type="button" class="btn-secondary action-btn action-btn-sm" data-load-suggestions>Suggestions</button>
                    <button type="button" class="btn-primary action-btn action-btn-sm" data-apply-suggestion ${suggestions.length === 0 ? 'disabled' : ''}>Apply</button>
                </div>
            </div>`;
    }).join('');

    elements.unmatchedModalList.querySelectorAll('[data-load-suggestions]').forEach(button => {
        button.addEventListener('click', async event => {
            const row = event.currentTarget.closest('.unmatched-artist-row');
            const artistId = Number(row?.dataset.artistId || 0);
            if (!Number.isFinite(artistId) || artistId <= 0) {
                return;
            }
            await loadSuggestionsForUnmatchedArtist(elements, artistId);
        });
    });

    elements.unmatchedModalList.querySelectorAll('[data-apply-suggestion]').forEach(button => {
        button.addEventListener('click', async event => {
            const row = event.currentTarget.closest('.unmatched-artist-row');
            const artistId = Number(row?.dataset.artistId || 0);
            const select = row?.querySelector('[data-suggestion-select]');
            const spotifyId = String(select?.value || '').trim();
            if (!artistId || !spotifyId) {
                return;
            }
            await applyUnmatchedArtistSuggestion(elements, artistId, spotifyId);
        });
    });
}

async function loadSuggestionsForUnmatchedArtist(elements, artistId) {
    try {
        const suggestions = await fetchUnmatchedArtistSuggestions(artistId);
        libraryState.unmatchedArtistResolver.suggestions.set(artistId, suggestions);
        renderUnmatchedArtistsResolverList(elements);
    } catch (error) {
        showToast(`Suggestion load failed: ${error.message}`, true);
    }
}

async function applyUnmatchedArtistSuggestion(elements, artistId, spotifyId) {
    try {
        await fetchJson(`/api/library/artists/${artistId}/spotify-id`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ spotifyId })
        });
        const state = libraryState.unmatchedArtistResolver;
        state.items = state.items.filter(item => item.artistId !== artistId);
        state.suggestions.delete(artistId);
        renderUnmatchedArtistsResolverList(elements);
        showToast('Spotify artist match updated.');
        if (document.getElementById('artistsGrid')) {
            await loadArtists();
        }
    } catch (error) {
        showToast(`Failed to apply match: ${error.message}`, true);
    }
}

async function fetchUnmatchedArtistSuggestions(artistId) {
    const suggestions = await fetchJson(`/api/library/artists/${artistId}/spotify-suggestions?limit=8`);
    return (Array.isArray(suggestions) ? suggestions : []).map(item => ({
        spotifyId: String(item.spotifyId || '').trim(),
        name: String(item.name || '').trim(),
        verified: item.verified === true,
        localAlbumOverlap: Number(item.localAlbumOverlap || 0),
        totalAlbums: Number(item.totalAlbums || 0),
        totalTracks: Number(item.totalTracks || 0),
        score: Number(item.score || 0),
        nameMatchesAlias: item.nameMatchesAlias === true
    })).filter(item => item.spotifyId && item.name);
}

function getFilteredUnmatchedArtistItems() {
    const state = libraryState.unmatchedArtistResolver;
    const filter = (state.filter || '').trim().toLowerCase();
    return state.items.filter(item => !filter || item.artistName.toLowerCase().includes(filter));
}

function isHighConfidenceUnmatchedSuggestion(suggestion) {
    if (!suggestion?.spotifyId) {
        return false;
    }

    if (!suggestion.nameMatchesAlias) {
        return false;
    }

    if (suggestion.localAlbumOverlap > 0) {
        return true;
    }
    const totalAlbums = Number(suggestion.totalAlbums || 0);
    const totalTracks = Number(suggestion.totalTracks || 0);
    if (totalAlbums >= 3 && totalTracks >= 25 && suggestion.score >= 100_000) {
        return true;
    }

    return suggestion.verified === true
        && totalAlbums >= 2
        && totalTracks >= 15
        && suggestion.score >= 100_000;
}

async function ensureUnmatchedSuggestionsForItem(state, artistId) {
    let suggestions = state.suggestions.get(artistId);
    if (Array.isArray(suggestions) && suggestions.length > 0) {
        return suggestions;
    }

    suggestions = await fetchUnmatchedArtistSuggestions(artistId);
    state.suggestions.set(artistId, suggestions);
    return suggestions;
}

async function applyHighConfidenceSuggestionForItem(state, item) {
    let suggestions;
    try {
        suggestions = await ensureUnmatchedSuggestionsForItem(state, item.artistId);
    } catch {
        return { status: 'failed' };
    }

    const best = suggestions[0];
    if (!isHighConfidenceUnmatchedSuggestion(best)) {
        return { status: 'skipped' };
    }

    try {
        await fetchJson(`/api/library/artists/${item.artistId}/spotify-id`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ spotifyId: best.spotifyId })
        });
        state.suggestions.delete(item.artistId);
        return { status: 'applied' };
    } catch {
        return { status: 'failed' };
    }
}

async function applyBestSuggestionsForHighConfidence(elements) {
    const button = elements.unmatchedModalApplyHighConfidenceButton;
    if (button) {
        button.disabled = true;
    }

    try {
        const visibleItems = getFilteredUnmatchedArtistItems();
        if (visibleItems.length === 0) {
            showToast('No unmatched artists available in current filter.', true);
            return;
        }

        let applied = 0;
        let skipped = 0;
        let failed = 0;
        const appliedArtistIds = new Set();
        const state = libraryState.unmatchedArtistResolver;

        for (const item of visibleItems) {
            const result = await applyHighConfidenceSuggestionForItem(state, item);
            if (result.status === 'applied') {
                applied += 1;
                appliedArtistIds.add(item.artistId);
            } else if (result.status === 'skipped') {
                skipped += 1;
            } else {
                failed += 1;
            }
        }

        if (appliedArtistIds.size > 0) {
            state.items = state.items.filter(item => !appliedArtistIds.has(item.artistId));
            renderUnmatchedArtistsResolverList(elements);
            if (document.getElementById('artistsGrid')) {
                await loadArtists();
            }
        }

        showToast(`Applied ${applied} high-confidence match(es). Skipped ${skipped}. Failed ${failed}.`, failed > 0);
    } finally {
        if (button) {
            button.disabled = false;
        }
    }
}
