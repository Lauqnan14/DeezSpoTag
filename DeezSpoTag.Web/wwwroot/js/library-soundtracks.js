// Extracted from library.js: soundtrack feature module
let pendingSoundtrackReturnState = null;

function persistSoundtrackReturnState() {
    if (!document.getElementById('soundtrackGrid')) {
        return;
    }

    const selectedShow = soundtrackState.selectedTvShow;
    setSessionJsonValue(SOUNDTRACK_RETURN_STATE_SESSION_KEY, {
        scrollY: globalThis.scrollY,
        category: normalizeSoundtrackCategory(soundtrackState.category),
        selectedServerType: String(soundtrackState.selectedServerType || '').trim().toLowerCase(),
        selectedLibraryId: String(soundtrackState.selectedLibraryId || '').trim(),
        selectedTvSeasonId: String(soundtrackState.selectedTvSeasonId || '').trim(),
        selectedTvShow: selectedShow
            ? {
                showId: String(selectedShow.showId || '').trim(),
                showTitle: String(selectedShow.showTitle || '').trim(),
                showImageUrl: String(selectedShow.showImageUrl || '').trim(),
                serverType: String(selectedShow.serverType || '').trim().toLowerCase(),
                libraryId: String(selectedShow.libraryId || '').trim(),
                libraryName: String(selectedShow.libraryName || '').trim(),
                year: Number.isFinite(selectedShow.year) ? selectedShow.year : null
            }
            : null,
        capturedAtUtc: new Date().toISOString()
    });
}

function clearPendingSoundtrackReturnState() {
    pendingSoundtrackReturnState = null;
    removeSessionValue(SOUNDTRACK_RETURN_STATE_SESSION_KEY);
}

function primePendingSoundtrackReturnState() {
    const payload = getSessionJsonValue(SOUNDTRACK_RETURN_STATE_SESSION_KEY);
    if (!payload) {
        return;
    }

    if (!isNavigationRestoreCandidate('/Tracklist')) {
        clearPendingSoundtrackReturnState();
        return;
    }

    pendingSoundtrackReturnState = payload;
    soundtrackState.category = normalizeSoundtrackCategory(payload.category);
    soundtrackState.selectedServerType = String(payload.selectedServerType || '').trim().toLowerCase();
    soundtrackState.selectedLibraryId = String(payload.selectedLibraryId || '').trim();
    soundtrackState.selectedTvSeasonId = String(payload.selectedTvSeasonId || '').trim();

    const selectedShow = payload.selectedTvShow;
    if (selectedShow && typeof selectedShow === 'object' && String(selectedShow.showId || '').trim()) {
        soundtrackState.selectedTvShow = {
            showId: String(selectedShow.showId || '').trim(),
            showTitle: String(selectedShow.showTitle || 'TV Show').trim(),
            showImageUrl: String(selectedShow.showImageUrl || '').trim(),
            serverType: String(selectedShow.serverType || '').trim().toLowerCase(),
            libraryId: String(selectedShow.libraryId || '').trim(),
            libraryName: String(selectedShow.libraryName || '').trim(),
            year: Number.isFinite(selectedShow.year) ? selectedShow.year : null,
            seasons: [],
            episodes: []
        };
    } else {
        soundtrackState.selectedTvShow = null;
    }
}

function isSoundtracksTabVisible() {
    const tabPane = document.getElementById('soundtracks-content');
    if (!tabPane) {
        return false;
    }
    if (tabPane.classList.contains('active') || tabPane.classList.contains('show')) {
        return true;
    }
    return document.getElementById('soundtracks-tab')?.classList.contains('active') === true;
}

function applyPendingSoundtrackScrollRestore() {
    if (!isSoundtracksTabVisible()) {
        return;
    }

    maybeApplyPendingScrollRestore(() => pendingSoundtrackReturnState, clearPendingSoundtrackReturnState);
}

function normalizeSoundtrackCategory(category) {
    return String(category || '').trim().toLowerCase() === 'tv_show' ? 'tv_show' : 'movie';
}

function isTabPreferenceEnabled() {
    const stored = localStorage.getItem('tabs-preference-enabled');
    if (stored === null || stored === '') {
        return true;
    }

    return stored === 'true';
}

function getSoundtrackTabPreferenceKey() {
    return `tabs:last:${globalThis.location.pathname}:soundtrackSubTabs`;
}

function mapSoundtrackCategoryToTabTarget(category) {
    return normalizeSoundtrackCategory(category) === 'tv_show'
        ? '#soundtrack-tv-tab'
        : '#soundtrack-movies-tab';
}

function mapSoundtrackTabTargetToCategory(tabTarget) {
    if (String(tabTarget || '').trim() === '#soundtrack-tv-tab') {
        return 'tv_show';
    }

    return 'movie';
}

function restoreSoundtrackCategoryPreference() {
    if (!isTabPreferenceEnabled()) {
        return null;
    }

    const storedTarget = localStorage.getItem(getSoundtrackTabPreferenceKey());
    if (!storedTarget) {
        return null;
    }

    return mapSoundtrackTabTargetToCategory(storedTarget);
}

function persistSoundtrackCategoryPreference(category) {
    if (!isTabPreferenceEnabled()) {
        return;
    }

    const storageKey = getSoundtrackTabPreferenceKey();
    const tabTarget = mapSoundtrackCategoryToTabTarget(category);
    localStorage.setItem(storageKey, tabTarget);
    if (globalThis.UserPrefs?.setTabSelection) {
        globalThis.UserPrefs.setTabSelection(storageKey, tabTarget);
    }
}

function getSoundtrackCategoryLabel(category) {
    return normalizeSoundtrackCategory(category) === 'tv_show' ? 'TV Shows' : 'Movies';
}

function getSoundtrackElements() {
    return {
        tab: document.getElementById('soundtracks-content'),
        categoryTabs: document.querySelectorAll('#soundtrackSubTabs [data-soundtrack-category]'),
        serverFilterGroup: document.getElementById('soundtrackServerFilterGroup'),
        serverFilterLabel: document.getElementById('soundtrackServerFilterLabel'),
        serverPills: document.getElementById('soundtrackServerPills'),
        libraryFilterGroup: document.getElementById('soundtrackLibraryFilterGroup'),
        libraryFilterLabel: document.getElementById('soundtrackLibraryFilterLabel'),
        libraryPills: document.getElementById('soundtrackLibraryPills'),
        refreshButton: document.getElementById('soundtrackRefreshItems'),
        syncButton: document.getElementById('soundtrackSyncLibraries'),
        status: document.getElementById('soundtrackStatus'),
        syncStatus: document.getElementById('soundtrackSyncStatus'),
        tvNav: document.getElementById('soundtrackTvNav'),
        tvBackButton: document.getElementById('soundtrackTvBack'),
        tvShowTitle: document.getElementById('soundtrackTvShowTitle'),
        tvSeasonFilterGroup: document.getElementById('soundtrackTvSeasonFilterGroup'),
        tvSeasonPills: document.getElementById('soundtrackTvSeasonPills'),
        letterNav: document.getElementById('soundtrackLetterNav'),
        grid: document.getElementById('soundtrackGrid'),
        empty: document.getElementById('soundtrackEmpty')
    };
}

function setActiveSoundtrackCategory(category) {
    const normalizedCategory = normalizeSoundtrackCategory(category);
    soundtrackState.category = normalizedCategory;
    getSoundtrackElements().categoryTabs.forEach(button => {
        const buttonCategory = normalizeSoundtrackCategory(button.dataset.soundtrackCategory);
        const active = buttonCategory === normalizedCategory;
        button.classList.toggle('active', active);
        button.setAttribute('aria-selected', active ? 'true' : 'false');
    });
}

function getSoundtrackLibraries(configuration, category, serverTypeFilter) {
    if (!configuration || !Array.isArray(configuration.servers)) {
        return [];
    }

    const normalizedCategory = normalizeSoundtrackCategory(category);
    const normalizedServerFilter = String(serverTypeFilter || '').trim().toLowerCase();
    const libraries = [];

    configuration.servers.forEach(server => {
        const serverType = String(server?.serverType || '').trim().toLowerCase();
        if (!serverType) {
            return;
        }
        if (normalizedServerFilter && normalizedServerFilter !== serverType) {
            return;
        }

        (Array.isArray(server?.libraries) ? server.libraries : []).forEach(library => {
            if (!library || library.enabled === false || library.ignored === true) {
                return;
            }

            const libraryCategory = normalizeSoundtrackCategory(library.category);
            if (libraryCategory !== normalizedCategory) {
                return;
            }

            const libraryId = String(library.libraryId || '').trim();
            if (!libraryId) {
                return;
            }

            libraries.push({
                serverType,
                serverLabel: String(server.displayName || serverType),
                libraryId,
                libraryName: String(library.name || libraryId),
                connected: library.connected !== false
            });
        });
    });

    return libraries;
}

function renderSoundtrackFilters(configuration) {
    const elements = getSoundtrackElements();
    if (!elements.serverPills || !elements.libraryPills) {
        return;
    }

    const category = soundtrackState.category;
    const serverOptions = buildSoundtrackServerOptions(configuration, category);
    normalizeSelectedSoundtrackServer(serverOptions);
    const selectedServer = soundtrackState.selectedServerType;
    const selectedServerLabel = resolveSelectedSoundtrackServerLabel(serverOptions, selectedServer);
    const libraryOptions = buildSoundtrackLibraryOptions(configuration, category, selectedServer);
    normalizeSelectedSoundtrackLibrary(libraryOptions);

    if (elements.serverFilterLabel) {
        elements.serverFilterLabel.textContent = 'Servers';
    }
    if (elements.serverFilterGroup) {
        elements.serverFilterGroup.style.display = serverOptions.length >= 2 ? '' : 'none';
    }

    elements.serverPills.innerHTML = renderSoundtrackServerPills(serverOptions);

    if (elements.libraryFilterLabel) {
        elements.libraryFilterLabel.textContent = selectedServerLabel
            ? `${selectedServerLabel} Libraries`
            : 'Libraries';
    }
    if (elements.libraryFilterGroup) {
        elements.libraryFilterGroup.style.display = libraryOptions.length >= 2 ? '' : 'none';
    }

    elements.libraryPills.innerHTML = renderSoundtrackLibraryPills(libraryOptions);
}

function buildSoundtrackServerOptions(configuration, category) {
    const servers = Array.isArray(configuration?.servers) ? configuration.servers : [];
    const options = [];
    servers.forEach(server => {
        const serverType = String(server?.serverType || '').trim().toLowerCase();
        if (!serverType) {
            return;
        }
        if (getSoundtrackLibraries(configuration, category, serverType).length === 0) {
            return;
        }

        const label = String(server?.displayName || serverType).trim();
        options.push({ serverType, label: label || serverType });
    });
    return options;
}

function normalizeSelectedSoundtrackServer(serverOptions) {
    const validServerTypes = new Set(serverOptions.map(option => option.serverType));
    if (!validServerTypes.has(soundtrackState.selectedServerType)) {
        soundtrackState.selectedServerType = '';
    }
    if (serverOptions.length === 1) {
        soundtrackState.selectedServerType = serverOptions[0].serverType;
    }
}

function resolveSelectedSoundtrackServerLabel(serverOptions, selectedServer) {
    return serverOptions.find(option => option.serverType === selectedServer)?.label || '';
}

function buildSoundtrackLibraryOptions(configuration, category, selectedServer) {
    const libraries = getSoundtrackLibraries(configuration, category, selectedServer);
    return libraries.map(library => {
        const serverHint = selectedServer ? '' : ` (${library.serverLabel})`;
        return {
            libraryId: library.libraryId,
            label: library.libraryName + serverHint
        };
    });
}

function normalizeSelectedSoundtrackLibrary(libraryOptions) {
    const validLibraryIds = new Set(libraryOptions.map(option => option.libraryId));
    if (!validLibraryIds.has(soundtrackState.selectedLibraryId)) {
        soundtrackState.selectedLibraryId = '';
    }
}

function renderSoundtrackServerPills(serverOptions) {
    if (serverOptions.length >= 2) {
        const pills = [
            `<button type="button" class="soundtrack-pill ${soundtrackState.selectedServerType ? '' : 'is-active'}" data-soundtrack-server="">All servers</button>`,
            ...serverOptions.map(option => `<button type="button" class="soundtrack-pill ${soundtrackState.selectedServerType === option.serverType ? 'is-active' : ''}" data-soundtrack-server="${escapeHtml(option.serverType)}">${escapeHtml(option.label)}</button>`)
        ];
        return pills.join('');
    }
    if (serverOptions.length === 0) {
        return '<span class="soundtrack-pill is-empty">No servers available</span>';
    }
    return '';
}

function renderSoundtrackLibraryPills(libraryOptions) {
    if (libraryOptions.length >= 2) {
        const pills = [
            `<button type="button" class="soundtrack-pill ${soundtrackState.selectedLibraryId ? '' : 'is-active'}" data-soundtrack-library="">All libraries</button>`,
            ...libraryOptions.map(option => `<button type="button" class="soundtrack-pill ${soundtrackState.selectedLibraryId === option.libraryId ? 'is-active' : ''}" data-soundtrack-library="${escapeHtml(option.libraryId)}">${escapeHtml(option.label)}</button>`)
        ];
        return pills.join('');
    }
    if (libraryOptions.length === 0) {
        return '<span class="soundtrack-pill is-empty">No libraries available</span>';
    }
    return '';
}

function resetSoundtrackTvSelection() {
    soundtrackState.selectedTvShow = null;
    soundtrackState.selectedTvSeasonId = '';
}

function clearSoundtrackBackgroundRefreshTimer() {
    if (soundtrackState.backgroundRefreshTimer) {
        globalThis.clearTimeout(soundtrackState.backgroundRefreshTimer);
        soundtrackState.backgroundRefreshTimer = 0;
    }
}

function clearSoundtrackSyncStatusTimer() {
    if (soundtrackState.syncStatusTimer) {
        globalThis.clearInterval(soundtrackState.syncStatusTimer);
        soundtrackState.syncStatusTimer = 0;
    }
}

function renderSoundtrackSyncStatus(payload) {
    const elements = getSoundtrackElements();
    if (!elements.syncStatus) {
        return;
    }

    const libraries = Array.isArray(payload?.libraries) ? payload.libraries : [];
    const runningLibrary = libraries.find(entry => String(entry?.status || '').toLowerCase() === 'running');
    const erroredLibrary = libraries.find(entry => String(entry?.status || '').toLowerCase() === 'error');
    const pendingJobs = Number.isFinite(Number(payload?.pendingJobs)) ? Number(payload.pendingJobs) : 0;
    const syncRunning = payload?.syncRunning === true || pendingJobs > 0 || Boolean(runningLibrary);
    soundtrackState.syncRunning = syncRunning;

    if (runningLibrary) {
        const serverType = String(runningLibrary.serverType || '').trim().toLowerCase();
        const libraryId = String(runningLibrary.libraryId || '').trim();
        const label = [serverType || 'server', libraryId].filter(Boolean).join('/');
        const offset = Number.isFinite(Number(runningLibrary.lastOffset)) ? Number(runningLibrary.lastOffset) : 0;
        const processed = Number.isFinite(Number(runningLibrary.totalProcessed)) ? Number(runningLibrary.totalProcessed) : 0;
        elements.syncStatus.textContent = `Sync running: ${label} (offset ${offset}, processed ${processed}).`;
        return;
    }

    if (erroredLibrary) {
        const serverType = String(erroredLibrary.serverType || '').trim().toLowerCase();
        const libraryId = String(erroredLibrary.libraryId || '').trim();
        const label = [serverType || 'server', libraryId].filter(Boolean).join('/');
        const lastError = String(erroredLibrary.lastError || '').trim();
        const errorSuffix = lastError ? ` (${lastError})` : '';
        elements.syncStatus.textContent = `Sync error: ${label}${errorSuffix}`;
        return;
    }

    if (syncRunning) {
        elements.syncStatus.textContent = 'Sync queued...';
        return;
    }

    elements.syncStatus.textContent = '';
}

async function pollSoundtrackSyncStatusOnce() {
    try {
        const payload = await fetchJson('/api/media-server/soundtracks/status');
        const snapshot = JSON.stringify({
            syncRunning: payload?.syncRunning === true,
            pendingJobs: payload?.pendingJobs || 0,
            libraries: Array.isArray(payload?.libraries)
                ? payload.libraries.map(entry => ({
                    serverType: entry?.serverType || '',
                    libraryId: entry?.libraryId || '',
                    status: entry?.status || '',
                    lastOffset: entry?.lastOffset || 0,
                    totalProcessed: entry?.totalProcessed || 0,
                    lastError: entry?.lastError || ''
                }))
                : []
        });

        if (snapshot !== soundtrackState.syncStatusSnapshot) {
            soundtrackState.syncStatusSnapshot = snapshot;
            renderSoundtrackSyncStatus(payload);
        }
    } catch {
        // Non-blocking status telemetry; ignore poll failures.
    }
}

function startSoundtrackSyncStatusPolling() {
    if (soundtrackState.syncStatusTimer) {
        return;
    }

    pollSoundtrackSyncStatusOnce().catch(() => {
        // Non-blocking status telemetry; ignore poll failures.
    });
    soundtrackState.syncStatusTimer = globalThis.setInterval(() => {
        pollSoundtrackSyncStatusOnce().catch(() => {
            // Non-blocking status telemetry; ignore poll failures.
        });
    }, 3000);
}

function resolveSoundtrackCardArtVariant(category) {
    const normalizedCategory = normalizeSoundtrackCategory(category);
    if (normalizedCategory === 'movie') {
        return 'poster';
    }

    if (soundtrackState.selectedTvShow && soundtrackState.selectedTvSeasonId) {
        return 'landscape';
    }

    return 'poster';
}

function renderSoundtrackLoadingState(category) {
    const elements = getSoundtrackElements();
    if (!elements.grid) {
        return;
    }

    const variant = resolveSoundtrackCardArtVariant(category);
    const cardCount = 8;
    const cards = Array.from({ length: cardCount }, () => `<article class="soundtrack-card soundtrack-card--skeleton">
        <div class="soundtrack-card-art soundtrack-card-art--${variant} soundtrack-skeleton-block"></div>
        <div class="soundtrack-card-body">
            <div class="soundtrack-skeleton-line soundtrack-skeleton-line--title"></div>
            <div class="soundtrack-skeleton-line soundtrack-skeleton-line--meta"></div>
            <div class="soundtrack-skeleton-line soundtrack-skeleton-line--meta short"></div>
        </div>
    </article>`);

    elements.grid.innerHTML = cards.join('');
    if (elements.empty) {
        elements.empty.hidden = true;
    }
    if (elements.status) {
        elements.status.textContent = `Loading ${getSoundtrackCategoryLabel(category)}...`;
    }
}

function renderSoundtrackTvNavigation() {
    const elements = getSoundtrackElements();
    const isTvCategory = normalizeSoundtrackCategory(soundtrackState.category) === 'tv_show';
    const selectedShow = soundtrackState.selectedTvShow;
    const hasSelectedShow = isTvCategory && selectedShow && String(selectedShow.showId || '').trim();

    if (elements.tvNav) {
        elements.tvNav.hidden = !hasSelectedShow;
    }

    if (elements.tvBackButton) {
        elements.tvBackButton.textContent = soundtrackState.selectedTvSeasonId
            ? 'Back to Seasons'
            : 'Back to TV Shows';
    }

    if (!hasSelectedShow) {
        resetSoundtrackTvNavigation(elements);
        return;
    }

    if (elements.tvShowTitle) {
        elements.tvShowTitle.textContent = String(selectedShow.showTitle || 'TV Show');
    }

    // Keep legacy season-pill controls hidden. TV navigation is card-based:
    // Shows -> Seasons -> Episodes.
    hideSoundtrackSeasonPills(elements);
}

function resetSoundtrackTvNavigation(elements) {
    if (elements.tvShowTitle) {
        elements.tvShowTitle.textContent = '';
    }
    if (elements.tvSeasonPills) {
        elements.tvSeasonPills.innerHTML = '';
    }
    if (elements.tvSeasonFilterGroup) {
        elements.tvSeasonFilterGroup.style.display = 'none';
    }
}

function hideSoundtrackSeasonPills(elements) {
    elements.tvSeasonFilterGroup.style.display = 'none';
    elements.tvSeasonPills.innerHTML = '';
}

function buildSoundtrackSeasonPills(seasons) {
    const pills = [
        `<button type="button" class="soundtrack-pill ${soundtrackState.selectedTvSeasonId ? '' : 'is-active'}" data-soundtrack-season="">All seasons</button>`
    ];
    seasons.forEach(season => {
        const seasonId = String(season?.seasonId || '').trim();
        const seasonNumber = Number.isFinite(season?.seasonNumber) ? `S${season.seasonNumber}` : '';
        const seasonTitle = String(season?.title || '').trim();
        const label = seasonNumber || seasonTitle || 'Season';
        const active = soundtrackState.selectedTvSeasonId === seasonId;
        pills.push(`<button type="button" class="soundtrack-pill ${active ? 'is-active' : ''}" data-soundtrack-season="${escapeHtml(seasonId)}">${escapeHtml(label)}</button>`);
    });

    return pills.join('');
}

function resolveSoundtrackTracklistTarget(soundtrack) {
    const kind = String(soundtrack?.kind || '').trim().toLowerCase();
    const deezerId = String(soundtrack?.deezerId || '').trim();
    if (!deezerId) {
        const url = String(soundtrack?.url || '').trim();
        const spotifyTarget = parseSpotifyTracklistTargetFromUrl(url);
        if (!spotifyTarget) {
            return null;
        }

        return spotifyTarget;
    }

    if (kind === 'album' || kind === 'playlist' || kind === 'track') {
        return { source: 'deezer', type: kind, id: deezerId, externalUrl: '' };
    }

    return null;
}

function parseSpotifyTracklistTargetFromUrl(url) {
    const value = String(url || '').trim();
    if (!value) {
        return null;
    }

    const webMatch = /open\.spotify\.com\/(album|playlist|track)\/([a-z0-9]+)/i.exec(value);
    if (webMatch) {
        return {
            source: 'spotify',
            type: webMatch[1].toLowerCase(),
            id: webMatch[2],
            externalUrl: value
        };
    }

    const uriMatch = /^spotify:(album|playlist|track):([a-z0-9]+)$/i.exec(value);
    if (uriMatch) {
        const type = uriMatch[1].toLowerCase();
        const id = uriMatch[2];
        return {
            source: 'spotify',
            type,
            id,
            externalUrl: `https://open.spotify.com/${type}/${id}`
        };
    }

    return null;
}

function navigateToSoundtrackTracklist(target) {
    const normalizedSource = String(target?.source || 'deezer').trim().toLowerCase();
    const normalizedType = String(target?.type || '').trim().toLowerCase();
    const normalizedId = String(target?.id || '').trim();
    const normalizedExternalUrl = String(target?.externalUrl || '').trim();
    if (!normalizedId || !normalizedType) {
        return;
    }

    const query = new URLSearchParams();
    query.set('id', normalizedId);
    query.set('type', normalizedType);
    query.set('source', normalizedSource);
    if (normalizedExternalUrl) {
        query.set('externalUrl', normalizedExternalUrl);
    }
    persistSoundtrackReturnState();
    globalThis.location.href = `/Tracklist?${query.toString()}`;
}

async function resolveMovieSoundtrackOnDemand(button) {
    const requestPayload = buildSoundtrackResolveRequestPayload(button);
    if (!requestPayload) {
        return null;
    }

    const payload = await resolveSoundtrackForCard(requestPayload, button);
    const target = resolveSoundtrackTracklistTarget(payload?.soundtrack);
    if (!target) {
        return null;
    }

    applySoundtrackTracklistTargetToButton(button, requestPayload.title, target);
    return target;
}

function buildSoundtrackResolveRequestPayload(button) {
    const itemId = String(button?.dataset?.soundtrackItemId || '').trim();
    const title = String(button?.dataset?.soundtrackTitle || '').trim();
    const serverType = String(button?.dataset?.soundtrackServer || '').trim().toLowerCase();
    const libraryId = String(button?.dataset?.soundtrackLibrary || '').trim();
    const libraryName = String(button?.dataset?.soundtrackLibraryName || '').trim();
    const category = String(button?.dataset?.soundtrackCategory || 'movie').trim().toLowerCase();
    const imageUrl = String(button?.dataset?.soundtrackImage || '').trim();
    const yearRaw = String(button?.dataset?.soundtrackYear || '').trim();
    const year = Number.isFinite(Number(yearRaw)) ? Number(yearRaw) : null;
    if (!itemId || !title || !serverType || !libraryId) {
        return null;
    }

    return {
        serverType,
        libraryId,
        libraryName,
        category,
        itemId,
        title,
        year,
        imageUrl
    };
}

async function resolveSoundtrackForCard(requestPayload, button) {
    button.disabled = true;
    button.dataset.soundtrackResolving = 'true';

    try {
        return await fetchJson('/api/media-server/soundtracks/resolve', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(requestPayload)
        });
    } finally {
        button.dataset.soundtrackResolving = 'false';
        button.disabled = false;
    }
}

function applySoundtrackTracklistTargetToButton(button, title, target) {
    button.dataset.soundtrackOpenTracklist = 'true';
    button.dataset.soundtrackTracklistSource = String(target.source || 'deezer');
    button.dataset.soundtrackTracklistType = target.type;
    button.dataset.soundtrackTracklistId = target.id;
    button.dataset.soundtrackTracklistExternalUrl = String(target.externalUrl || '');
    button.setAttribute('aria-label', `Open soundtrack tracklist for ${title}`);
}

async function resolveManualSoundtrackSearch(button) {
    const requestPayload = buildSoundtrackResolveRequestPayload(button);
    if (!requestPayload) {
        showToast('Manual soundtrack search needs a valid media item.', true);
        return;
    }

    const params = new URLSearchParams();
    params.set('term', String(requestPayload.title || '').trim());
    params.set('type', 'all');
    params.set('source', 'spotify');
    params.set('mode', 'soundtrack');
    params.set('contextType', String(requestPayload.category || '').trim().toLowerCase() || 'movie');
    params.set('contextServerType', String(requestPayload.serverType || '').trim().toLowerCase());
    params.set('contextLibraryId', String(requestPayload.libraryId || '').trim());
    params.set('contextItemId', String(requestPayload.itemId || '').trim());
    params.set('contextTitle', String(requestPayload.title || '').trim());
    if (Number.isFinite(requestPayload.year)) {
        params.set('contextYear', String(requestPayload.year));
    }

    persistSoundtrackReturnState();
    globalThis.location.href = `/Search?${params.toString()}`;
}

function setSoundtrackStatus(elements, text) {
    if (elements.status) {
        elements.status.textContent = text;
    }
}

function renderSoundtrackEmptyState(elements, normalizedCategory, category) {
    elements.grid.innerHTML = '';
    elements.empty.hidden = false;
    if (normalizedCategory === 'tv_show' && soundtrackState.selectedTvShow) {
        setSoundtrackStatus(elements, 'TV Shows: 0 episodes');
        return;
    }

    setSoundtrackStatus(elements, `${getSoundtrackCategoryLabel(category)}: 0 items`);
}

function sortSoundtrackSeasons(seasons) {
    return seasons
        .slice()
        .sort((left, right) => {
            const leftNum = Number.isFinite(left?.seasonNumber) ? left.seasonNumber : Number.MAX_SAFE_INTEGER;
            const rightNum = Number.isFinite(right?.seasonNumber) ? right.seasonNumber : Number.MAX_SAFE_INTEGER;
            if (leftNum !== rightNum) {
                return leftNum - rightNum;
            }
            return String(left?.title || '').localeCompare(String(right?.title || ''), undefined, { sensitivity: 'base' });
        });
}

function buildSoundtrackActionMenu(actionDataAttributes, options = {}) {
    if (!actionDataAttributes) {
        return '';
    }

    const cornerClass = options.corner === true ? ' soundtrack-card-actions--corner' : '';
    return `<div class="soundtrack-card-actions${cornerClass}">
        <button type="button" class="soundtrack-card-actions-toggle" aria-label="Soundtrack actions" title="Soundtrack actions">⋯</button>
        <div class="soundtrack-card-actions-menu" role="menu">
            <button type="button" class="soundtrack-card-actions-item" data-soundtrack-manual-search="true"${actionDataAttributes}>Manual Search</button>
        </div>
    </div>`;
}

function buildSoundtrackMovieCardMarkup(item) {
    const title = String(item?.title || '').trim() || 'Untitled';
    const year = Number.isFinite(item?.year) ? ` (${item.year})` : '';
    const libraryName = String(item?.libraryName || '').trim();
    const serverLabel = String(item?.serverLabel || item?.serverType || '').trim();
    const context = [libraryName, serverLabel].filter(Boolean).join(' • ');
    const image = String(item?.imageUrl || '').trim();
    const tracklistTarget = resolveSoundtrackTracklistTarget(item?.soundtrack);
    const openTracklistAttributes = tracklistTarget
        ? ` data-soundtrack-open-tracklist="true" data-soundtrack-tracklist-source="${escapeHtml(String(tracklistTarget.source || 'deezer'))}" data-soundtrack-tracklist-type="${escapeHtml(tracklistTarget.type)}" data-soundtrack-tracklist-id="${escapeHtml(tracklistTarget.id)}" data-soundtrack-tracklist-external-url="${escapeHtml(String(tracklistTarget.externalUrl || ''))}"`
        : '';
    const posterAriaLabel = tracklistTarget
        ? `Open soundtrack tracklist for ${title}`
        : `Resolve soundtrack for ${title}`;
    const movieDataAttributes = ` data-soundtrack-item-id="${escapeHtml(String(item?.itemId || '').trim())}" data-soundtrack-title="${escapeHtml(title)}" data-soundtrack-year="${escapeHtml(Number.isFinite(item?.year) ? String(item.year) : '')}" data-soundtrack-image="${escapeHtml(image)}" data-soundtrack-server="${escapeHtml(String(item?.serverType || '').trim())}" data-soundtrack-library="${escapeHtml(String(item?.libraryId || '').trim())}" data-soundtrack-library-name="${escapeHtml(libraryName)}" data-soundtrack-category="movie"`;

    return `<article class="soundtrack-card soundtrack-card--movie">
        <button type="button" class="soundtrack-card-poster-btn soundtrack-card-open-tracklist-btn"${openTracklistAttributes}${movieDataAttributes} aria-label="${escapeHtml(posterAriaLabel)}">
            ${image
        ? `<img class="soundtrack-card-art soundtrack-card-art--poster" src="${escapeHtml(image)}" alt="${escapeHtml(title)}">`
        : `<div class="soundtrack-card-art soundtrack-card-art--poster watchlist-card-art-placeholder"><i class="fa-solid fa-compact-disc"></i></div>`}
        </button>
        ${buildSoundtrackActionMenu(movieDataAttributes, { corner: true })}
        <div class="soundtrack-card-body">
            <h3 class="soundtrack-card-title">${escapeHtml(title)}${escapeHtml(year)}</h3>
            ${context ? `<p class="soundtrack-card-meta">${escapeHtml(context)}</p>` : ''}
        </div>
    </article>`;
}

function buildSoundtrackTvSeasonCardMarkup(season) {
    const title = String(season?.title || '').trim() || 'Season';
    const seasonId = String(season?.seasonId || '').trim();
    const seasonNumber = Number.isFinite(season?.seasonNumber) ? `Season ${season.seasonNumber}` : '';
    let episodeCount = '';
    if (Number.isFinite(season?.episodeCount)) {
        const suffix = season.episodeCount === 1 ? '' : 's';
        episodeCount = `${season.episodeCount} episode${suffix}`;
    }
    const context = [seasonNumber, episodeCount].filter(Boolean).join(' • ');
    const image = String(season?.imageUrl || '').trim();
    const openSeasonLabel = seasonId
        ? `Open episodes for ${title}`
        : `Season ${title}`;

    return `<article class="soundtrack-card soundtrack-card--tv-season">
        ${seasonId
        ? `<button type="button" class="soundtrack-card-media-btn" data-soundtrack-open-season="${escapeHtml(seasonId)}" aria-label="${escapeHtml(openSeasonLabel)}">`
        : '<div>'}
        ${image
        ? `<img class="soundtrack-card-art soundtrack-card-art--poster" src="${escapeHtml(image)}" alt="${escapeHtml(title)}">`
        : `<div class="soundtrack-card-art soundtrack-card-art--poster watchlist-card-art-placeholder"><i class="fa-solid fa-photo-film"></i></div>`}
        ${seasonId ? '</button>' : '</div>'}
        <div class="soundtrack-card-body">
            <h3 class="soundtrack-card-title">${escapeHtml(title)}</h3>
            ${context ? `<p class="soundtrack-card-meta">${escapeHtml(context)}</p>` : ''}
        </div>
    </article>`;
}

function buildSoundtrackTvEpisodeCardMarkup(item) {
    const title = String(item?.title || '').trim() || 'Untitled';
    const seasonNumber = Number.isFinite(item?.seasonNumber) ? `S${item.seasonNumber}` : '';
    const episodeNumber = Number.isFinite(item?.episodeNumber) ? `E${item.episodeNumber}` : '';
    const seasonLabel = [seasonNumber, episodeNumber].filter(Boolean).join(' • ');
    const seasonTitle = String(item?.seasonTitle || '').trim();
    const context = [seasonTitle, seasonLabel].filter(Boolean).join(' • ');
    const image = String(item?.imageUrl || '').trim();
    const selectedShow = soundtrackState.selectedTvShow || {};
    const resolveServerType = String(selectedShow.serverType || soundtrackState.selectedServerType || '').trim().toLowerCase();
    const resolveLibraryId = String(selectedShow.libraryId || soundtrackState.selectedLibraryId || '').trim();
    const resolveLibraryName = String(selectedShow.libraryName || '').trim();
    const resolveItemId = String(selectedShow.showId || '').trim();
    const resolveTitle = String(selectedShow.showTitle || title).trim();
    const resolveImage = String(selectedShow.showImageUrl || image).trim();
    const resolveYear = Number.isFinite(selectedShow.year) ? String(selectedShow.year) : '';
    const episodeManualAttributes = resolveServerType && resolveLibraryId && resolveItemId
        ? ` data-soundtrack-item-id="${escapeHtml(resolveItemId)}" data-soundtrack-title="${escapeHtml(resolveTitle)}" data-soundtrack-year="${escapeHtml(resolveYear)}" data-soundtrack-image="${escapeHtml(resolveImage)}" data-soundtrack-server="${escapeHtml(resolveServerType)}" data-soundtrack-library="${escapeHtml(resolveLibraryId)}" data-soundtrack-library-name="${escapeHtml(resolveLibraryName)}" data-soundtrack-category="tv_show"`
        : '';
    const tracklistTarget = resolveSoundtrackTracklistTarget(item?.soundtrack);
    const openTracklistAttributes = tracklistTarget
        ? ` data-soundtrack-open-tracklist="true" data-soundtrack-tracklist-source="${escapeHtml(String(tracklistTarget.source || 'deezer'))}" data-soundtrack-tracklist-type="${escapeHtml(tracklistTarget.type)}" data-soundtrack-tracklist-id="${escapeHtml(tracklistTarget.id)}" data-soundtrack-tracklist-external-url="${escapeHtml(String(tracklistTarget.externalUrl || ''))}"`
        : '';
    const episodeAriaLabel = tracklistTarget
        ? `Open soundtrack tracklist for ${title}`
        : `Resolve soundtrack for ${title}`;

    return `<article class="soundtrack-card soundtrack-card--tv-episode">
        <button type="button" class="soundtrack-card-media-btn soundtrack-card-open-tracklist-btn"${openTracklistAttributes}${episodeManualAttributes} aria-label="${escapeHtml(episodeAriaLabel)}">
        ${image
        ? `<img class="soundtrack-card-art soundtrack-card-art--landscape" src="${escapeHtml(image)}" alt="${escapeHtml(title)}">`
        : `<div class="soundtrack-card-art soundtrack-card-art--landscape watchlist-card-art-placeholder"><i class="fa-solid fa-tv"></i></div>`}
        </button>
        ${buildSoundtrackActionMenu(episodeManualAttributes, { corner: true })}
        <div class="soundtrack-card-body">
            <h3 class="soundtrack-card-title">${escapeHtml(title)}</h3>
            ${context ? `<p class="soundtrack-card-meta">${escapeHtml(context)}</p>` : ''}
        </div>
    </article>`;
}

function buildSoundtrackTvShowCardMarkup(item) {
    const title = String(item?.title || '').trim() || 'Untitled';
    const year = Number.isFinite(item?.year) ? ` (${item.year})` : '';
    const libraryName = String(item?.libraryName || '').trim();
    const serverLabel = String(item?.serverLabel || item?.serverType || '').trim();
    const context = [libraryName, serverLabel].filter(Boolean).join(' • ');
    const image = String(item?.imageUrl || '').trim();
    const showId = String(item?.itemId || '').trim();
    const itemServerType = String(item?.serverType || '').trim().toLowerCase();
    const itemLibraryId = String(item?.libraryId || '').trim();
    const showYearValue = Number.isFinite(item?.year) ? String(item.year) : '';
    const showOpenAttributes = showId
        ? ` data-soundtrack-open-show="${escapeHtml(showId)}" data-soundtrack-show-title="${escapeHtml(title)}" data-soundtrack-server="${escapeHtml(itemServerType)}" data-soundtrack-library="${escapeHtml(itemLibraryId)}" data-soundtrack-show-library-name="${escapeHtml(libraryName)}" data-soundtrack-show-image="${escapeHtml(image)}" data-soundtrack-year="${escapeHtml(showYearValue)}"`
        : '';
    const showAriaLabel = showId
        ? `Browse seasons for ${title}`
        : `TV show ${title}`;

    return `<article class="soundtrack-card soundtrack-card--tv-show">
        ${showId
        ? `<button type="button" class="soundtrack-card-media-btn"${showOpenAttributes} aria-label="${escapeHtml(showAriaLabel)}">`
        : '<div>'}
        ${image
        ? `<img class="soundtrack-card-art soundtrack-card-art--poster" src="${escapeHtml(image)}" alt="${escapeHtml(title)}">`
        : `<div class="soundtrack-card-art soundtrack-card-art--poster watchlist-card-art-placeholder"><i class="fa-solid fa-tv"></i></div>`}
        ${showId ? '</button>' : '</div>'}
        <div class="soundtrack-card-body">
            <h3 class="soundtrack-card-title">${escapeHtml(title)}${escapeHtml(year)}</h3>
            ${context ? `<p class="soundtrack-card-meta">${escapeHtml(context)}</p>` : ''}
        </div>
    </article>`;
}

function getPluralSuffix(count) {
    return count === 1 ? '' : 's';
}

function getSoundtrackSeasonsForSelectedShow() {
    const selectedShow = soundtrackState.selectedTvShow;
    if (!selectedShow) {
        return [];
    }

    if (Array.isArray(selectedShow.seasons) && selectedShow.seasons.length > 0) {
        return sortSoundtrackSeasons(selectedShow.seasons);
    }

    return sortSoundtrackSeasons(buildSeasonsFromEpisodes(selectedShow.episodes));
}

function buildSeasonsFromEpisodes(episodes) {
    const rows = Array.isArray(episodes) ? episodes : [];
    const bySeason = new Map();
    rows.forEach(episode => {
        const seasonId = String(episode?.seasonId || '').trim();
        if (!seasonId) {
            return;
        }

        if (!bySeason.has(seasonId)) {
            bySeason.set(seasonId, {
                seasonId,
                title: String(episode?.seasonTitle || '').trim(),
                seasonNumber: Number.isFinite(episode?.seasonNumber) ? episode.seasonNumber : null,
                imageUrl: String(episode?.imageUrl || '').trim(),
                episodeCount: 0
            });
        }

        const season = bySeason.get(seasonId);
        season.episodeCount += 1;
        if (!season.title) {
            season.title = String(episode?.seasonTitle || '').trim();
        }
        if (!season.imageUrl) {
            season.imageUrl = String(episode?.imageUrl || '').trim();
        }
    });

    return Array.from(bySeason.values());
}

function setMovieSoundtrackStatus(elements, count) {
    setSoundtrackStatus(elements, `Movies: ${count} item${getPluralSuffix(count)}`);
}

function setTvSoundtrackStatus(elements, label, count) {
    setSoundtrackStatus(elements, `TV Shows: ${count} ${label}${getPluralSuffix(count)}`);
}

function renderSoundtrackAlphaJumpNavigation(elements, rows) {
    if (!elements?.letterNav || !elements?.grid) {
        return;
    }

    const cards = Array.from(elements.grid.querySelectorAll('.soundtrack-card'));
    if (!Array.isArray(rows) || rows.length === 0 || cards.length === 0) {
        renderAlphaJumpNavigation(elements.letterNav, new Map());
        return;
    }

    const anchorIdsByLetter = new Map();
    cards.forEach(card => {
        const titleValue = card.querySelector('.soundtrack-card-title')?.textContent || '';
        const letter = getAlphaJumpLetter(titleValue);
        if (anchorIdsByLetter.has(letter)) {
            return;
        }

        const anchorId = buildAlphaJumpAnchorId('soundtrack', letter);
        anchorIdsByLetter.set(letter, anchorId);
        card.id = anchorId;
    });

    renderAlphaJumpNavigation(elements.letterNav, anchorIdsByLetter);
}

function renderMovieSoundtrackRows(elements, rows) {
    elements.grid.innerHTML = rows.map(item => buildSoundtrackMovieCardMarkup(item)).join('');
    renderSoundtrackAlphaJumpNavigation(elements, rows);
    setMovieSoundtrackStatus(elements, rows.length);
}

function shouldRenderSelectedTvShowSeasons() {
    return String(soundtrackState.selectedTvSeasonId || '').trim().length === 0;
}

function renderSelectedTvShowSeasons(elements) {
    const seasons = getSoundtrackSeasonsForSelectedShow();
    if (seasons.length === 0) {
        return false;
    }

    elements.grid.innerHTML = seasons.map(season => buildSoundtrackTvSeasonCardMarkup(season)).join('');
    renderSoundtrackAlphaJumpNavigation(elements, seasons);
    setTvSoundtrackStatus(elements, 'season', seasons.length);
    return true;
}

function renderSelectedTvShowEpisodes(elements, rows) {
    elements.grid.innerHTML = rows.map(item => buildSoundtrackTvEpisodeCardMarkup(item)).join('');
    renderSoundtrackAlphaJumpNavigation(elements, rows);
    setTvSoundtrackStatus(elements, 'episode', rows.length);
}

function renderTvShowSoundtrackRows(elements, rows) {
    elements.grid.innerHTML = rows.map(item => buildSoundtrackTvShowCardMarkup(item)).join('');
    renderSoundtrackAlphaJumpNavigation(elements, rows);
    setTvSoundtrackStatus(elements, 'show', rows.length);
}

function renderSelectedTvShowSoundtrackRows(elements, rows) {
    if (!soundtrackState.selectedTvShow) {
        return false;
    }

    if (shouldRenderSelectedTvShowSeasons() && renderSelectedTvShowSeasons(elements)) {
        return true;
    }

    renderSelectedTvShowEpisodes(elements, rows);
    return true;
}

function renderSoundtrackItems(items, category) {
    const elements = getSoundtrackElements();
    if (!elements.grid || !elements.empty) {
        return;
    }

    const rows = Array.isArray(items) ? items : [];
    const normalizedCategory = normalizeSoundtrackCategory(category);
    if (rows.length === 0) {
        renderSoundtrackEmptyState(elements, normalizedCategory, category);
        renderAlphaJumpNavigation(elements.letterNav, new Map());
        applyPendingSoundtrackScrollRestore();
        return;
    }

    elements.empty.hidden = true;
    if (normalizedCategory === 'movie') {
        renderMovieSoundtrackRows(elements, rows);
        applyPendingSoundtrackScrollRestore();
        return;
    }

    if (renderSelectedTvShowSoundtrackRows(elements, rows)) {
        applyPendingSoundtrackScrollRestore();
        return;
    }

    renderTvShowSoundtrackRows(elements, rows);
    applyPendingSoundtrackScrollRestore();
}

function hasPendingSoundtrackMatches(rows) {
    if (!Array.isArray(rows) || rows.length === 0) {
        return false;
    }

    return rows.some(row => {
        const kind = String(row?.soundtrack?.kind || '').trim().toLowerCase();
        const deezerId = String(row?.soundtrack?.deezerId || '').trim();
        return !deezerId || kind === 'search';
    });
}

function scheduleSoundtrackBackgroundRefresh(rows) {
    // Background matching warmup is handled server-side. Avoid client-side repaint polling,
    // which causes visible poster flicker from repeated full-grid rerenders.
    if (!Array.isArray(rows)) {
        return;
    }
}

async function loadSoundtrackEpisodes(options = {}) {
    const { showLoadingSkeleton = true } = options;
    const elements = getSoundtrackElements();
    const selectedShow = soundtrackState.selectedTvShow;
    if (!selectedShow || !elements.grid) {
        return;
    }

    if (showLoadingSkeleton) {
        soundtrackState.backgroundRefreshAttempt = 0;
        renderSoundtrackLoadingState('tv_show');
    }

    if (elements.status) {
        const stageLabel = soundtrackState.selectedTvSeasonId ? 'episodes' : 'seasons';
        elements.status.textContent = `Loading ${stageLabel} for ${selectedShow.showTitle || 'TV Show'}...`;
    }

    const params = new URLSearchParams();
    params.set('serverType', String(selectedShow.serverType || '').trim());
    params.set('libraryId', String(selectedShow.libraryId || '').trim());
    params.set('showId', String(selectedShow.showId || '').trim());
    if (soundtrackState.selectedTvSeasonId) {
        params.set('seasonId', soundtrackState.selectedTvSeasonId);
    }
    params.set('limit', '1200');

    try {
        const payload = await fetchJson(`/api/media-server/soundtracks/episodes?${params.toString()}`);
        soundtrackState.selectedTvShow = {
            showId: String(payload?.showId || selectedShow.showId || '').trim(),
            showTitle: String(payload?.showTitle || selectedShow.showTitle || 'TV Show').trim(),
            showImageUrl: String(payload?.showImageUrl || selectedShow.showImageUrl || '').trim(),
            serverType: String(payload?.serverType || selectedShow.serverType || '').trim().toLowerCase(),
            libraryId: String(payload?.libraryId || selectedShow.libraryId || '').trim(),
            libraryName: String(payload?.libraryName || selectedShow.libraryName || '').trim(),
            year: Number.isFinite(selectedShow.year) ? selectedShow.year : null,
            seasons: Array.isArray(payload?.seasons) ? payload.seasons : [],
            episodes: Array.isArray(payload?.episodes) ? payload.episodes : []
        };

        renderSoundtrackTvNavigation();
        renderSoundtrackItems(soundtrackState.selectedTvShow.episodes, 'tv_show');
        scheduleSoundtrackBackgroundRefresh(soundtrackState.selectedTvShow.episodes);
    } catch (error) {
        elements.grid.innerHTML = '';
        if (elements.empty) {
            elements.empty.hidden = false;
        }
        if (elements.status) {
            elements.status.textContent = 'Failed to load TV show episodes.';
        }
        showToast(`TV episode load failed: ${error?.message || 'Unknown error'}`, true);
    }
}

async function loadSoundtrackConfiguration(refresh = false) {
    const query = refresh ? '?refresh=true' : '';
    const configuration = await fetchJson(`/api/media-server/soundtracks/configuration${query}`);
    soundtrackState.configuration = configuration;
    renderSoundtrackFilters(configuration);
    return configuration;
}

function buildSoundtrackItemKey(item) {
    const serverType = String(item?.serverType || '').trim().toLowerCase();
    const libraryId = String(item?.libraryId || '').trim();
    const itemId = String(item?.itemId || '').trim();
    return `${serverType}::${libraryId}::${itemId}`;
}

function resolveSoundtrackLibraryTargets(configuration, category) {
    const serverType = String(soundtrackState.selectedServerType || '').trim();
    const selectedLibraryId = String(soundtrackState.selectedLibraryId || '').trim();
    const allLibraries = getSoundtrackLibraries(configuration, category, serverType);
    if (!selectedLibraryId) {
        return allLibraries;
    }

    return allLibraries.filter(library =>
        String(library?.libraryId || '').trim() === selectedLibraryId
        && String(library?.serverType || '').trim().toLowerCase() === String(serverType || library?.serverType || '').trim().toLowerCase());
}

function buildSoundtrackItemsRequestParams(category, target, offset, requestLimit, forceServerRefresh) {
    const params = new URLSearchParams();
    params.set('category', category);
    params.set('serverType', String(target?.serverType || '').trim());
    params.set('libraryId', String(target?.libraryId || '').trim());
    params.set('offset', String(offset));
    params.set('limit', String(requestLimit));
    if (forceServerRefresh) {
        params.set('refresh', 'true');
    }
    return params;
}

function mergeSoundtrackRows(merged, rows) {
    rows.forEach(row => {
        const key = buildSoundtrackItemKey(row);
        if (key !== '::') {
            merged.set(key, row);
        }
    });
}

async function loadSoundtrackLibraryItemsLazy(target, options) {
    let offset = 0;
    let loadedForLibrary = 0;

    while (loadedForLibrary < options.perLibraryCap) {
        if (options.isStale()) {
            return true;
        }

        const requestLimit = Math.min(options.batchSize, options.perLibraryCap - loadedForLibrary);
        const params = buildSoundtrackItemsRequestParams(
            options.category,
            target,
            offset,
            requestLimit,
            options.forceServerRefresh
        );
        const payload = await fetchJson(`/api/media-server/soundtracks/items?${params.toString()}`);
        if (options.isStale()) {
            return true;
        }

        const rows = Array.isArray(payload?.items) ? payload.items : [];
        if (rows.length === 0) {
            break;
        }

        mergeSoundtrackRows(options.merged, rows);
        offset += rows.length;
        loadedForLibrary += rows.length;
        options.onProgress();

        if (rows.length < requestLimit) {
            break;
        }
    }

    return false;
}

function buildSoundtrackLazyLoadState(elements, category, requestId, targets) {
    return {
        elements,
        category,
        requestId,
        targets,
        merged: new Map(),
        completedLibraries: 0,
        batchSize: 100,
        perLibraryCap: Number.MAX_SAFE_INTEGER
    };
}

function isSoundtrackLazyLoadStale(state) {
    return state.requestId !== soundtrackState.lastItemsRequestId;
}

function updateSoundtrackLazyLoadStatus(state) {
    if (!state.elements.status) {
        return;
    }

    const itemCount = state.merged.size;
    state.elements.status.textContent = `${getSoundtrackCategoryLabel(state.category)}: ${itemCount} item${getPluralSuffix(itemCount)} • ${state.completedLibraries}/${state.targets.length} libraries loaded`;
}

function renderSoundtrackLazyLoadProgress(state) {
    renderSoundtrackItems(Array.from(state.merged.values()), state.category);
    updateSoundtrackLazyLoadStatus(state);
}

async function loadSoundtrackTargetItemsLazy(target, state, forceServerRefresh) {
    const stale = await loadSoundtrackLibraryItemsLazy(target, {
        category: state.category,
        batchSize: state.batchSize,
        perLibraryCap: state.perLibraryCap,
        forceServerRefresh,
        merged: state.merged,
        isStale: () => isSoundtrackLazyLoadStale(state),
        onProgress: () => renderSoundtrackLazyLoadProgress(state)
    });
    if (stale) {
        return true;
    }

    state.completedLibraries += 1;
    updateSoundtrackLazyLoadStatus(state);
    return false;
}

async function loadSoundtrackItemsLazy(configuration, category, requestId, forceServerRefresh = false) {
    const elements = getSoundtrackElements();
    const targets = resolveSoundtrackLibraryTargets(configuration, category);
    if (targets.length === 0) {
        renderSoundtrackItems([], category);
        return;
    }

    const state = buildSoundtrackLazyLoadState(elements, category, requestId, targets);
    updateSoundtrackLazyLoadStatus(state);

    for (const target of targets) {
        const stale = await loadSoundtrackTargetItemsLazy(target, state, forceServerRefresh);
        if (stale) {
            return;
        }
    }

    const finalRows = Array.from(state.merged.values());
    renderSoundtrackItems(finalRows, category);
    return finalRows;
}

async function loadSoundtrackItems(options = {}) {
    const { refreshConfiguration = false, showLoadingSkeleton = true, forceServerRefresh = false } = options;
    const elements = getSoundtrackElements();
    if (!elements.grid) {
        return;
    }

    const requestId = ++soundtrackState.lastItemsRequestId;
    clearSoundtrackBackgroundRefreshTimer();
    if (showLoadingSkeleton) {
        soundtrackState.backgroundRefreshAttempt = 0;
        renderSoundtrackLoadingState(soundtrackState.category);
    }
    const category = soundtrackState.category;
    if (elements.status) {
        elements.status.textContent = `Loading ${getSoundtrackCategoryLabel(category)}...`;
    }

    try {
        const configuration = refreshConfiguration || !soundtrackState.configuration
            ? await loadSoundtrackConfiguration(refreshConfiguration)
            : soundtrackState.configuration;
        if (requestId !== soundtrackState.lastItemsRequestId) {
            return;
        }

        renderSoundtrackFilters(configuration);

        if (category === 'tv_show' && soundtrackState.selectedTvShow) {
            await loadSoundtrackEpisodes({ showLoadingSkeleton });
            return;
        }

        const finalRows = await loadSoundtrackItemsLazy(configuration, category, requestId, forceServerRefresh);
        scheduleSoundtrackBackgroundRefresh(finalRows);
        renderSoundtrackTvNavigation();
    } catch (error) {
        if (requestId !== soundtrackState.lastItemsRequestId) {
            return;
        }
        elements.grid.innerHTML = '';
        if (elements.empty) {
            elements.empty.hidden = false;
        }
        if (elements.status) {
            elements.status.textContent = `Failed to load ${getSoundtrackCategoryLabel(category)}.`;
        }
        renderSoundtrackTvNavigation();
        showToast(`Soundtracks load failed: ${error?.message || 'Unknown error'}`, true);
    }
}

function bindSoundtrackTabHandlers() {
    const elements = getSoundtrackElements();
    if (!elements.tab || elements.tab.dataset.soundtracksBound === 'true') {
        return;
    }

    const rememberedCategory = restoreSoundtrackCategoryPreference();
    if (rememberedCategory) {
        soundtrackState.category = rememberedCategory;
    }
    if (pendingSoundtrackReturnState?.category) {
        soundtrackState.category = normalizeSoundtrackCategory(pendingSoundtrackReturnState.category);
    }

    setActiveSoundtrackCategory(soundtrackState.category);

    elements.categoryTabs.forEach(button => {
        button.addEventListener('click', async () => {
            const category = normalizeSoundtrackCategory(button.dataset.soundtrackCategory);
            if (category === soundtrackState.category) {
                return;
            }
            setActiveSoundtrackCategory(category);
            persistSoundtrackCategoryPreference(category);
            resetSoundtrackTvSelection();
            renderSoundtrackTvNavigation();
            renderSoundtrackLoadingState(category);
            await loadSoundtrackItems();
        });
    });

    if (elements.serverPills) {
        elements.serverPills.addEventListener('click', async event => {
            const button = event.target.closest('[data-soundtrack-server]');
            if (!button) {
                return;
            }
            const selectedServerType = String(button.dataset.soundtrackServer || '').trim().toLowerCase();
            if (selectedServerType === soundtrackState.selectedServerType) {
                return;
            }
            soundtrackState.selectedServerType = selectedServerType;
            soundtrackState.selectedLibraryId = '';
            resetSoundtrackTvSelection();
            renderSoundtrackTvNavigation();
            await loadSoundtrackItems();
        });
    }

    if (elements.libraryPills) {
        elements.libraryPills.addEventListener('click', async event => {
            const button = event.target.closest('[data-soundtrack-library]');
            if (!button) {
                return;
            }
            const selectedLibraryId = String(button.dataset.soundtrackLibrary || '').trim();
            if (selectedLibraryId === soundtrackState.selectedLibraryId) {
                return;
            }
            soundtrackState.selectedLibraryId = selectedLibraryId;
            resetSoundtrackTvSelection();
            renderSoundtrackTvNavigation();
            await loadSoundtrackItems();
        });
    }

    if (elements.grid) {
        elements.grid.addEventListener('click', async event => {
            const manualSearchButton = event.target.closest('[data-soundtrack-manual-search]');
            if (manualSearchButton) {
                await resolveManualSoundtrackSearch(manualSearchButton);
                return;
            }

            const openTracklistButton = event.target.closest('.soundtrack-card-open-tracklist-btn');
            if (openTracklistButton) {
                let source = String(openTracklistButton.dataset.soundtrackTracklistSource || 'deezer').trim().toLowerCase();
                let type = String(openTracklistButton.dataset.soundtrackTracklistType || '').trim().toLowerCase();
                let id = String(openTracklistButton.dataset.soundtrackTracklistId || '').trim();
                let externalUrl = String(openTracklistButton.dataset.soundtrackTracklistExternalUrl || '').trim();
                if (!type || !id) {
                    const resolvedTarget = await resolveMovieSoundtrackOnDemand(openTracklistButton);
                    if (!resolvedTarget) {
                        const title = String(openTracklistButton.dataset.soundtrackTitle || 'this title').trim();
                        showToast(`No soundtrack match found yet for ${title}.`, true);
                        return;
                    }

                    source = String(resolvedTarget.source || 'deezer').trim().toLowerCase();
                    type = resolvedTarget.type;
                    id = resolvedTarget.id;
                    externalUrl = String(resolvedTarget.externalUrl || '').trim();
                }

                if (!type || !id) {
                    return;
                }

                navigateToSoundtrackTracklist({
                    source,
                    type,
                    id,
                    externalUrl
                });
                return;
            }

            const seasonButton = event.target.closest('[data-soundtrack-open-season]');
            if (seasonButton) {
                const seasonId = String(seasonButton.dataset.soundtrackOpenSeason || '').trim();
                if (!seasonId || seasonId === soundtrackState.selectedTvSeasonId) {
                    return;
                }

                soundtrackState.selectedTvSeasonId = seasonId;
                renderSoundtrackTvNavigation();
                await loadSoundtrackEpisodes();
                return;
            }

            const button = event.target.closest('[data-soundtrack-open-show]');
            if (!button) {
                return;
            }

            const showId = String(button.dataset.soundtrackOpenShow || '').trim();
            const serverType = String(button.dataset.soundtrackServer || soundtrackState.selectedServerType || '').trim().toLowerCase();
            const libraryId = String(button.dataset.soundtrackLibrary || soundtrackState.selectedLibraryId || '').trim();
            if (!showId || !serverType || !libraryId) {
                return;
            }

            soundtrackState.selectedTvShow = {
                showId,
                showTitle: String(button.dataset.soundtrackShowTitle || 'TV Show').trim(),
                showImageUrl: String(button.dataset.soundtrackShowImage || '').trim(),
                serverType,
                libraryId,
                libraryName: String(button.dataset.soundtrackShowLibraryName || '').trim(),
                year: Number.isFinite(Number(button.dataset.soundtrackYear))
                    ? Number(button.dataset.soundtrackYear)
                    : null,
                seasons: [],
                episodes: []
            };
            soundtrackState.selectedTvSeasonId = '';
            renderSoundtrackTvNavigation();
            await loadSoundtrackEpisodes();
        });
    }

    if (elements.tvBackButton) {
        elements.tvBackButton.addEventListener('click', async () => {
            if (soundtrackState.selectedTvShow && soundtrackState.selectedTvSeasonId) {
                soundtrackState.selectedTvSeasonId = '';
                renderSoundtrackTvNavigation();
                renderSoundtrackItems(soundtrackState.selectedTvShow.episodes, 'tv_show');
                return;
            }

            resetSoundtrackTvSelection();
            renderSoundtrackTvNavigation();
            await loadSoundtrackItems();
        });
    }

    // Legacy season pill filtering intentionally disabled.

    if (elements.refreshButton) {
        elements.refreshButton.addEventListener('click', async () => {
            await loadSoundtrackItems({ forceServerRefresh: true });
        });
    }

    if (elements.syncButton) {
        elements.syncButton.addEventListener('click', async () => {
            try {
                if (elements.status) {
                    elements.status.textContent = 'Syncing media server libraries...';
                }
                const configuration = await fetchJson('/api/media-server/soundtracks/sync', { method: 'POST' });
                soundtrackState.configuration = configuration;
                renderSoundtrackFilters(configuration);
                resetSoundtrackTvSelection();
                renderSoundtrackTvNavigation();
                await loadSoundtrackItems();
                await pollSoundtrackSyncStatusOnce();
                showToast('Media server soundtrack sync started.');
            } catch (error) {
                showToast(`Soundtrack sync failed: ${error?.message || 'Unknown error'}`, true);
            }
        });
    }

    elements.tab.dataset.soundtracksBound = 'true';
}

async function initializeSoundtracksTab() {
    const elements = getSoundtrackElements();
    if (!elements.grid) {
        return;
    }

    bindSoundtrackTabHandlers();
    renderSoundtrackTvNavigation();
    startSoundtrackSyncStatusPolling();
    if (soundtrackState.initialized) {
        await loadSoundtrackItems();
        return;
    }

    soundtrackState.initialized = true;
    await loadSoundtrackItems();
}
