// Extracted from library.js: watchlist and playlist feature module

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
            .map(folder => `<option value="${escapeHtml(String(folder.id || ''))}">${escapeHtml(folder.displayName || 'Folder')}</option>`)
            .join('');
        const artistPrefs = getStoredPreferences('artistWatchlist');

        container.innerHTML = items.map(item => {
            const cover = item.artistImagePath
                ? appendCacheKey(`/api/library/image?path=${encodeURIComponent(item.artistImagePath)}&size=300`)
                : '';
            const artContent = cover
                ? `<img src="${escapeHtml(cover)}" alt="${escapeHtml(item.artistName)}" />`
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
                ? `<select class="watchlist-card-folder-select form-select" data-watchlist-folder="${escapeHtml(String(item.artistId || ''))}">
                       <option value="">No folder</option>
                       ${folderOptions}
                   </select>`
                : `<div class="watchlist-folder-empty">No folders configured.</div>`;

            const deezerId = item.deezerId || '';
            const spotifyId = item.spotifyId || '';

            return `<div class="watchlist-artist-card">
                <button class="watchlist-card-art" type="button"
                    data-watchlist-open="${escapeHtml(String(item.artistId || ''))}"
                    data-watchlist-deezer="${escapeHtml(deezerId)}"
                    data-watchlist-spotify="${escapeHtml(spotifyId)}">
                    ${artContent}
                    ${badges ? `<div class="watchlist-card-badges">${badges}</div>` : ''}
                    ${statsHtml ? `<div class="watchlist-card-stats">${statsHtml}</div>` : ''}
                </button>
                <div class="watchlist-card-strip">
                    <div class="watchlist-card-name">${escapeHtml(item.artistName)}</div>
                    ${folderSelect}
                    <button class="btn btn-danger action-btn btn-sm watchlist-card-unmonitor" data-watchlist-remove="${escapeHtml(String(item.artistId || ''))}" type="button">Unmonitor</button>
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
                const card = button.closest('.watchlist-artist-card');
                const strip = button.closest('.watchlist-card-strip');
                const previousOpacity = card ? card.style.opacity : '';
                button.disabled = true;
                if (card) {
                    card.style.opacity = '0.45';
                }
                try {
                    await fetchJson(`/api/library/watchlist/${artistId}`, { method: 'DELETE' });
                    if (card) {
                        card.remove();
                    }
                    if (!container.querySelector('.watchlist-artist-card')) {
                        container.innerHTML = '<div class="watchlist-empty-state">No monitored artists yet.</div>';
                    }
                } catch (error) {
                    button.disabled = false;
                    if (card) {
                        card.style.opacity = previousOpacity;
                    }
                    if (strip) {
                        strip.classList.remove('is-busy');
                    }
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

function navigateToPlaylistTracklist(source, sourceId) {
    if (!sourceId) {
        return;
    }
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

function normalizeBlocklistField(field) {
    const normalized = String(field || '').trim().toLowerCase();
    if (normalized === 'track' || normalized === 'artist' || normalized === 'album') {
        return normalized;
    }
    return '';
}

async function loadPlaylistBlockedRules() {
    const container = document.getElementById('blockedWatchlistContainer');
    if (!container) {
        return;
    }
    try {
        const [itemsRaw, blocklistRaw, playlistPrefs] = await Promise.all([
            fetchJson('/api/library/playlists').catch(() => []),
            fetchJson('/api/library/blocklist').catch(() => []),
            hydratePlaylistPreferences().catch(() => ({}))
        ]);

        const items = Array.isArray(itemsRaw) ? itemsRaw : [];
        const trackRows = [];
        if (items.length > 0) {
            const blockedTrackResults = await Promise.all(items.map(async item => {
                const [ignoredRaw, candidatesRaw] = await Promise.all([
                    fetchJson(`/api/library/playlists/${encodeURIComponent(item.source)}/${encodeURIComponent(item.sourceId)}/ignore`).catch(() => []),
                    fetchJson(`/api/library/playlists/${encodeURIComponent(item.source)}/${encodeURIComponent(item.sourceId)}/tracks`).catch(() => [])
                ]);

                const ignoredTrackIds = Array.isArray(ignoredRaw)
                    ? ignoredRaw
                        .map(trackSourceId => String(trackSourceId || '').trim())
                        .filter(Boolean)
                    : [];

                const candidateMap = new Map();
                if (Array.isArray(candidatesRaw)) {
                    candidatesRaw.forEach(candidate => {
                        const trackSourceId = String(candidate?.trackSourceId || '').trim();
                        if (trackSourceId) {
                            candidateMap.set(trackSourceId, candidate);
                        }
                    });
                }

                return ignoredTrackIds.map(trackSourceId => {
                    const candidate = candidateMap.get(trackSourceId);
                    return {
                        source: item.source,
                        sourceId: item.sourceId,
                        playlistName: item.name || 'Playlist',
                        trackSourceId,
                        title: String(candidate?.title || trackSourceId).trim(),
                        artist: String(candidate?.artist || '').trim(),
                        album: String(candidate?.album || '').trim(),
                        isrc: String(candidate?.isrc || '').trim()
                    };
                });
            }));

            blockedTrackResults.forEach(rows => {
                rows.forEach(row => {
                    trackRows.push(row);
                });
            });
        }

        const blocklistEntries = Array.isArray(blocklistRaw) ? blocklistRaw : [];
        const uniqueByField = {
            artist: new Set(),
            album: new Set(),
            track: new Set()
        };
        const globalArtists = [];
        const globalAlbums = [];
        const globalTracks = [];

        blocklistEntries.forEach(entry => {
            if (!entry || entry.enabled === false) {
                return;
            }

            const field = normalizeBlocklistField(entry.field);
            const value = String(entry.value || '').trim();
            if (!field || !value) {
                return;
            }

            const key = value.toLowerCase();
            if (uniqueByField[field].has(key)) {
                return;
            }
            uniqueByField[field].add(key);

            if (field === 'artist') {
                globalArtists.push(value);
                return;
            }
            if (field === 'album') {
                globalAlbums.push(value);
                return;
            }
            globalTracks.push(value);
        });

        if (trackRows.length === 0 && globalArtists.length === 0 && globalAlbums.length === 0 && globalTracks.length === 0) {
            container.innerHTML = '<div class="watchlist-empty-state">No blocked items configured yet.</div>';
            return;
        }

        const trackItems = [
            ...trackRows.map(row => ({
                kind: 'playlist',
                label: row.title,
                detail: [row.artist, row.album].filter(Boolean).join(' • '),
                context: `Playlist: ${row.playlistName} (${row.source})`,
                source: row.source,
                sourceId: row.sourceId,
                playlistName: row.playlistName,
                trackSourceId: row.trackSourceId,
                isrc: row.isrc
            })),
            ...globalTracks.map(value => ({
                kind: 'global',
                label: value,
                detail: '',
                context: 'Source: global blocklist'
            }))
        ];

        const dedupedTrackItems = [];
        const seenTrackItems = new Set();
        trackItems.forEach(item => {
            const dedupeKey = [
                String(item.kind || '').trim().toLowerCase(),
                String(item.trackSourceId || '').trim().toLowerCase(),
                String(item.isrc || '').trim().toLowerCase(),
                String(item.label || '').trim().toLowerCase(),
                String(item.detail || '').trim().toLowerCase(),
                String(item.context || '').trim().toLowerCase()
            ].join('\u001F');
            if (seenTrackItems.has(dedupeKey)) {
                return;
            }
            seenTrackItems.add(dedupeKey);
            dedupedTrackItems.push(item);
        });

        const renderTrackItems = dedupedTrackItems.length
            ? dedupedTrackItems.map(item => {
                const identity = [item.trackSourceId ? `Track ID: ${item.trackSourceId}` : '', item.isrc ? `ISRC: ${item.isrc}` : '']
                    .filter(Boolean)
                    .join(' • ');
                const manageButtons = item.kind === 'playlist'
                    ? `<div class="watchlist-blocked-actions">
                            <button class="btn btn-secondary action-btn btn-sm" type="button"
                                data-blocked-open="${escapeHtml(item.sourceId)}"
                                data-blocked-source="${escapeHtml(item.source)}">Open Playlist</button>
                            <button class="btn btn-secondary action-btn btn-sm" type="button"
                                data-blocked-manage="${escapeHtml(item.sourceId)}"
                                data-blocked-source="${escapeHtml(item.source)}"
                                data-blocked-name="${escapeHtml(item.playlistName)}">Manage</button>
                        </div>`
                    : '';
                return `<div class="watchlist-blocked-item">
                    <div class="watchlist-blocked-item-main">
                        <div class="watchlist-blocked-item-title">${escapeHtml(item.label)}</div>
                        ${item.detail ? `<div class="watchlist-blocked-item-meta">${escapeHtml(item.detail)}</div>` : ''}
                        <div class="watchlist-blocked-item-meta">${escapeHtml(item.context)}</div>
                        ${identity ? `<div class="watchlist-blocked-item-meta">${escapeHtml(identity)}</div>` : ''}
                    </div>
                    ${manageButtons}
                </div>`;
            }).join('')
            : '<div class="watchlist-empty-state">No blocked tracks.</div>';

        const renderValues = values => values.length
            ? values.map(value => `<div class="watchlist-blocked-item"><div class="watchlist-blocked-item-main"><div class="watchlist-blocked-item-title">${escapeHtml(value)}</div></div></div>`).join('')
            : '<div class="watchlist-empty-state">None.</div>';

        container.innerHTML = `<div class="watchlist-blocked-sections">
            <section class="watchlist-blocked-section">
                <h3>Tracks</h3>
                <div class="watchlist-blocked-list">${renderTrackItems}</div>
            </section>
            <section class="watchlist-blocked-section">
                <h3>Artists</h3>
                <div class="watchlist-blocked-list">${renderValues(globalArtists)}</div>
            </section>
            <section class="watchlist-blocked-section">
                <h3>Albums</h3>
                <div class="watchlist-blocked-list">${renderValues(globalAlbums)}</div>
            </section>
        </div>`;

        container.querySelectorAll('[data-blocked-open]').forEach(button => {
            button.addEventListener('click', () => {
                const sourceId = button.dataset.blockedOpen;
                const source = button.dataset.blockedSource || 'deezer';
                navigateToPlaylistTracklist(source, sourceId);
            });
        });

        container.querySelectorAll('[data-blocked-manage]').forEach(button => {
            button.addEventListener('click', async () => {
                const source = button.dataset.blockedSource;
                const sourceId = button.dataset.blockedManage;
                const playlistName = button.dataset.blockedName || 'Playlist';
                if (!source || !sourceId) {
                    return;
                }
                await openPlaylistSettingsPanel(source, sourceId, playlistName, playlistPrefs);
                await loadPlaylistBlockedRules();
            });
        });
    } catch (error) {
        container.innerHTML = `<div class="watchlist-empty-state">Failed to load blocked items: ${escapeHtml(error?.message || 'Unknown error')}</div>`;
    }
}

function renderSharedPlaylistActionButtons({
    actionAttribute = 'data-playlist-action',
    source = '',
    sourceId = '',
    name = '',
    includeDataAttributes = true
} = {}) {
    const safeSource = escapeHtml(String(source || ''));
    const safeSourceId = escapeHtml(String(sourceId || ''));
    const safeName = escapeHtml(String(name || 'Playlist'));
    const withData = includeDataAttributes
        ? ` data-playlist-source="${safeSource}" data-playlist-id="${safeSourceId}" data-playlist-name="${safeName}"`
        : '';

    return `
        <button class="dropdown-item" type="button" ${actionAttribute}="settings"${withData}>
            <i class="fa-solid fa-sliders"></i><span>Settings</span>
        </button>
        <button class="dropdown-item" type="button" ${actionAttribute}="sync"${withData}>
            <i class="fa-solid fa-rotate"></i><span>Sync now</span>
        </button>
        <button class="dropdown-item" type="button" ${actionAttribute}="choose-artwork"${withData}>
            <i class="fa-solid fa-images"></i><span>Choose artwork</span>
        </button>
        <button class="dropdown-item" type="button" ${actionAttribute}="refresh-artwork"${withData}>
            <i class="fa-solid fa-image"></i><span>Refresh artwork</span>
        </button>
        <button class="dropdown-item danger" type="button" ${actionAttribute}="remove"${withData}>
            <i class="fa-solid fa-xmark"></i><span>Unmonitor</span>
        </button>
    `;
}

globalThis.renderSharedPlaylistActionButtons = renderSharedPlaylistActionButtons;

async function openSharedPlaylistArtworkPicker(source, sourceId, playlistName, options = {}) {
    const normalizedSource = String(source || '').trim();
    const normalizedSourceId = String(sourceId || '').trim();
    if (!normalizedSource || !normalizedSourceId) {
        return false;
    }

    if (!globalThis.DeezSpoTag?.ui?.showModal) {
        showToast('Artwork picker unavailable.', true);
        return false;
    }

    let visuals = [];
    try {
        const response = await fetchJson(`/api/library/playlists/${encodeURIComponent(normalizedSource)}/${encodeURIComponent(normalizedSourceId)}/visuals`);
        visuals = Array.isArray(response) ? response : [];
    } catch (error) {
        showToast(`Failed to load artwork options: ${error.message}`, true);
        return false;
    }

    if (visuals.length === 0) {
        showToast('No saved artwork history is available yet. Use Refresh artwork first.', true);
        return false;
    }

    const panel = document.createElement('div');
    panel.className = 'playlist-settings-panel';
    const section = document.createElement('div');
    section.className = 'playlist-settings-section';
    section.innerHTML = '<div class="playlist-settings-section-title">Saved artwork history</div>';
    const grid = document.createElement('div');
    grid.className = 'dropdown-visuals-grid';
    const renderGrid = (activeFileName = null) => {
        grid.innerHTML = visuals.map(item => {
            const fileName = String(item?.fileName || '');
            const url = toSafeHttpUrl(String(item?.url || ''));
            const isActive = fileName && activeFileName
                ? fileName === activeFileName
                : item?.isActive === true;
            return `
                <div class="visual-tile" title="Saved artwork">
                    <img src="${escapeHtml(url)}" alt="Saved playlist artwork" loading="lazy" decoding="async" />
                    <div class="visual-tile__actions">
                        <button class="action-btn action-btn-sm ${isActive ? 'btn-secondary' : ''}" type="button"
                            data-playlist-visual-select="${escapeHtml(fileName)}"
                            data-playlist-visual-url="${escapeHtml(url)}">
                            ${isActive ? 'Active' : 'Use this art'}
                        </button>
                    </div>
                </div>
            `;
        }).join('');
    };

    renderGrid();
    section.appendChild(grid);
    panel.appendChild(section);

    grid.addEventListener('click', async (event) => {
        const target = event.target;
        if (!(target instanceof Element)) {
            return;
        }
        const button = target.closest('[data-playlist-visual-select]');
        if (!(button instanceof HTMLElement)) {
            return;
        }

        const fileName = button.dataset.playlistVisualSelect || '';
        const url = button.dataset.playlistVisualUrl || '';
        if (!fileName) {
            return;
        }

        try {
            await fetchJson(`/api/library/playlists/${encodeURIComponent(normalizedSource)}/${encodeURIComponent(normalizedSourceId)}/visuals/select`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ fileName })
            });
            renderGrid(fileName);
            if (typeof options.onApplied === 'function' && url) {
                options.onApplied(url);
            }
            if (options.silent !== true) {
                showToast('Playlist artwork updated.');
            }
        } catch (error) {
            showToast(`Failed to apply artwork: ${error.message}`, true);
        }
    });

    await globalThis.DeezSpoTag.ui.showModal({
        title: `Artwork - ${playlistName || 'Playlist'}`,
        contentElement: panel,
        buttons: [
            { label: 'Close', value: 'close', primary: true }
        ]
    });
    return true;
}

globalThis.openSharedPlaylistArtworkPicker = openSharedPlaylistArtworkPicker;

async function loadPlaylistWatchlist() {
    const container = document.getElementById('playlistWatchlistContainer');
    if (!container) return;
    container.dataset.loadState = 'loading';
    container.innerHTML = '<div class="watchlist-empty-state">Loading monitored playlists...</div>';
    const mergeButton = document.getElementById('mergePlaylistWatchlistBtn');
    if (mergeButton) {
        mergeButton.disabled = true;
        mergeButton.onclick = null;
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
        const items = await fetchJson('/api/library/playlists');
        if (!Array.isArray(items) || items.length === 0) {
            container.innerHTML = '<div class="watchlist-empty-state">No monitored playlists yet.</div>';
            container.dataset.loadState = 'ready';
            return;
        }

        if (mergeButton) {
            mergeButton.disabled = items.length < 2;
            mergeButton.onclick = async () => {
                await openPlaylistMergePanel(items);
            };
        }

        const playlistPrefsPromise = hydratePlaylistPreferences();

        container.innerHTML = items.map((item) => {
            const imageUrl = toSafeHttpUrl(item.imageUrl || '');
            const artContent = imageUrl
                ? `<img src="${escapeHtml(imageUrl)}" alt="${escapeHtml(item.name)}" />`
                : `<div class="watchlist-card-art-placeholder"><i class="fa-solid fa-list-music"></i></div>`;
            const trackCount = item.trackCount === null || item.trackCount === undefined
                ? ''
                : `${item.trackCount} tracks`;
            return `<div class="watchlist-playlist-card-v2">
                <button class="watchlist-card-art" type="button"
                    data-playlist-open="${escapeHtml(item.sourceId)}"
                    data-playlist-source="${escapeHtml(item.source)}">
                    ${artContent}
                </button>
                <div class="watchlist-action-menu watchlist-action-menu--hover">
                    <button class="watchlist-kebab-btn" type="button" title="Actions" data-playlist-menu-toggle="${escapeHtml(item.source)}" data-playlist-id="${escapeHtml(item.sourceId)}" aria-expanded="false">
                        <i class="fa-solid fa-ellipsis-vertical"></i>
                    </button>
                    <div class="watchlist-action-dropdown watchlist-action-dropdown--hover" data-playlist-menu="${escapeHtml(item.source)}" data-playlist-id="${escapeHtml(item.sourceId)}" hidden>
                        ${renderSharedPlaylistActionButtons({
                            actionAttribute: 'data-playlist-action',
                            source: item.source,
                            sourceId: item.sourceId,
                            name: item.name
                        })}
                    </div>
                </div>
                <div class="watchlist-card-strip">
                    <div class="watchlist-card-name">${escapeHtml(item.name)}</div>
                    ${trackCount ? `<div class="watchlist-card-meta">${escapeHtml(trackCount)}</div>` : ''}
                </div>
            </div>`;
        }).join('');

        container.querySelectorAll('[data-playlist-open]').forEach(button => {
            button.addEventListener('click', () => {
                const sourceId = button.dataset.playlistOpen;
                const source = button.dataset.playlistSource || 'deezer';
                navigateToPlaylistTracklist(source, sourceId);
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
                const card = button.closest('.watchlist-playlist-card-v2');
                const previousOpacity = card ? card.style.opacity : '';
                button.disabled = true;
                if (card) {
                    card.style.opacity = '0.45';
                }
                try {
                    await fetchJson(`/api/library/playlists/${encodeURIComponent(source)}/${encodeURIComponent(sourceId)}`, { method: 'DELETE' });
                    if (card) {
                        card.remove();
                    }
                    const remainingCards = container.querySelectorAll('.watchlist-playlist-card-v2').length;
                    if (remainingCards === 0) {
                        container.innerHTML = '<div class="watchlist-empty-state">No monitored playlists yet.</div>';
                    }
                    if (mergeButton) {
                        mergeButton.disabled = remainingCards < 2;
                    }
                    await loadPlaylistBlockedRules();
                } catch (error) {
                    button.disabled = false;
                    if (card) {
                        card.style.opacity = previousOpacity;
                    }
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
                    await loadPlaylistBlockedRules();
                    showToast('Playlist artwork refreshed.');
                } catch (error) {
                    showToast(`Artwork refresh failed: ${error.message}`, true);
                }
            });
        });

        container.querySelectorAll('[data-playlist-action="choose-artwork"]').forEach(button => {
            button.addEventListener('click', async () => {
                const source = button.dataset.playlistSource;
                const sourceId = button.dataset.playlistId;
                const playlistName = button.dataset.playlistName || 'Playlist';
                if (!source || !sourceId) return;
                const opened = await openSharedPlaylistArtworkPicker(source, sourceId, playlistName);
                if (opened) {
                    await loadPlaylistWatchlist();
                    await loadPlaylistBlockedRules();
                }
            });
        });

        container.querySelectorAll('[data-playlist-action="settings"]').forEach(button => {
            button.addEventListener('click', async () => {
                const source = button.dataset.playlistSource;
                const sourceId = button.dataset.playlistId;
                const playlistName = button.dataset.playlistName || 'Playlist';
                if (!source || !sourceId) return;
                const playlistPrefs = await playlistPrefsPromise;
                await openPlaylistSettingsPanel(source, sourceId, playlistName, playlistPrefs);
            });
        });

        playlistPrefsPromise.then((playlistPrefs) => {
            tryOpenPendingPlaylistSettings(playlistPrefs);
        }).catch(() => {
            // Ignore preference hydration failures here; settings panel handles missing prefs.
        });
        container.dataset.loadState = 'ready';
    } catch (error) {
        container.dataset.loadState = 'error';
        container.innerHTML = `<div class="watchlist-empty-state">Failed to load playlists: ${escapeHtml(error?.message || 'Unknown error')}</div>`;
    }
}

function bindPlaylistWatchlistTabHydration() {
    const watchlistTab = document.getElementById('watchlist-tab');
    const playlistSubTab = document.getElementById('watchlist-playlists-tab');
    const playlistPane = document.getElementById('watchlist-playlists-content');
    if (!playlistPane || playlistPane.dataset.playlistHydrationBound === 'true') {
        return;
    }

    const ensurePlaylistWatchlistLoaded = () => {
        const container = document.getElementById('playlistWatchlistContainer');
        if (!container) {
            return;
        }
        if (container.dataset.loadState === 'loading') {
            return;
        }
        const hasRenderableContent = container.childElementCount > 0 || container.textContent.trim().length > 0;
        if (container.dataset.loadState !== 'ready' || !hasRenderableContent) {
            void loadPlaylistWatchlist();
        }
    };

    watchlistTab?.addEventListener('shown.bs.tab', ensurePlaylistWatchlistLoaded);
    playlistSubTab?.addEventListener('shown.bs.tab', ensurePlaylistWatchlistLoaded);

    const watchlistTabActive = watchlistTab?.classList.contains('active') === true;
    const playlistTabActive = playlistSubTab?.classList.contains('active') === true;
    const playlistPaneActive = playlistPane.classList.contains('active') || playlistPane.classList.contains('show');
    if ((watchlistTabActive && playlistTabActive) || playlistPaneActive) {
        ensurePlaylistWatchlistLoaded();
    }

    playlistPane.dataset.playlistHydrationBound = 'true';
}

document.addEventListener('DOMContentLoaded', () => {
    bindPlaylistWatchlistTabHydration();
});

function tryOpenPendingPlaylistSettings(playlistPrefs) {
    try {
        const pendingSettings = sessionStorage.getItem('playlist-watchlist-open-settings');
        if (!pendingSettings) {
            return;
        }

        sessionStorage.removeItem('playlist-watchlist-open-settings');
        const parsed = JSON.parse(pendingSettings);
        const pendingSource = String(parsed?.source || '').trim();
        const pendingSourceId = String(parsed?.sourceId || '').trim();
        const pendingName = String(parsed?.name || 'Playlist').trim() || 'Playlist';
        if (!pendingSource || !pendingSourceId) {
            return;
        }

        setTimeout(() => {
            openPlaylistSettingsPanel(pendingSource, pendingSourceId, pendingName, playlistPrefs);
        }, 0);
    } catch {
    }
}

async function openPlaylistMergePanel(items) {
    if (!Array.isArray(items) || items.length < 2) {
        showToast('Add at least two monitored playlists before merging.', true);
        return;
    }

    if (!globalThis.DeezSpoTag?.ui?.showModal) {
        showToast('Merge panel unavailable.', true);
        return;
    }

    const panel = document.createElement('div');
    panel.className = 'playlist-settings-panel';

    const sourceSection = document.createElement('div');
    sourceSection.className = 'playlist-settings-section';
    sourceSection.innerHTML = '<div class="playlist-settings-section-title">Playlists to merge</div>';
    const sourceList = document.createElement('div');
    sourceList.className = 'routing-rules-list';
    items.forEach((item, index) => {
        const row = document.createElement('label');
        row.className = 'routing-rule-row';
        const checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.className = 'form-check-input';
        checkbox.dataset.mergeSource = item.source || '';
        checkbox.dataset.mergeSourceId = item.sourceId || '';
        checkbox.checked = index < 2;
        const sourceLabel = document.createElement('div');
        sourceLabel.className = 'playlist-settings-section-label';
        sourceLabel.textContent = `${item.name || 'Playlist'} · ${String(item.source || '').toUpperCase()}`;
        row.appendChild(checkbox);
        row.appendChild(sourceLabel);
        sourceList.appendChild(row);
    });
    sourceSection.appendChild(sourceList);
    panel.appendChild(sourceSection);

    const nameSection = document.createElement('div');
    nameSection.className = 'playlist-settings-section';
    nameSection.innerHTML = '<div class="playlist-settings-section-title">Merged playlist name</div>';
    const nameInput = document.createElement('input');
    nameInput.className = 'form-control';
    nameInput.type = 'text';
    nameInput.maxLength = 200;
    nameInput.value = 'Merged Monitored Playlist';
    nameSection.appendChild(nameInput);
    panel.appendChild(nameSection);

    const descriptionSection = document.createElement('div');
    descriptionSection.className = 'playlist-settings-section';
    descriptionSection.innerHTML = '<div class="playlist-settings-section-title">Description</div>';
    const descriptionInput = document.createElement('textarea');
    descriptionInput.className = 'form-control';
    descriptionInput.rows = 3;
    descriptionInput.placeholder = 'Write a custom description for the merged playlist.';
    descriptionSection.appendChild(descriptionInput);
    const descriptionHint = document.createElement('div');
    descriptionHint.className = 'playlist-settings-section-label';
    descriptionHint.textContent = 'Source attribution will include your DeezSpoTag username.';
    descriptionSection.appendChild(descriptionHint);
    panel.appendChild(descriptionSection);

    const targetSection = document.createElement('div');
    targetSection.className = 'playlist-settings-section';
    targetSection.innerHTML = '<div class="playlist-settings-section-title">Sync targets</div>';
    const targetList = document.createElement('div');
    targetList.className = 'routing-rules-list';
    const plexRow = document.createElement('label');
    plexRow.className = 'routing-rule-row';
    const plexCheck = document.createElement('input');
    plexCheck.type = 'checkbox';
    plexCheck.className = 'form-check-input';
    plexCheck.checked = true;
    const plexText = document.createElement('div');
    plexText.className = 'playlist-settings-section-label';
    plexText.textContent = 'Plex';
    plexRow.appendChild(plexCheck);
    plexRow.appendChild(plexText);
    targetList.appendChild(plexRow);
    const jellyfinRow = document.createElement('label');
    jellyfinRow.className = 'routing-rule-row';
    const jellyfinCheck = document.createElement('input');
    jellyfinCheck.type = 'checkbox';
    jellyfinCheck.className = 'form-check-input';
    jellyfinCheck.checked = false;
    const jellyfinText = document.createElement('div');
    jellyfinText.className = 'playlist-settings-section-label';
    jellyfinText.textContent = 'Jellyfin';
    jellyfinRow.appendChild(jellyfinCheck);
    jellyfinRow.appendChild(jellyfinText);
    targetList.appendChild(jellyfinRow);
    targetSection.appendChild(targetList);
    panel.appendChild(targetSection);

    const syncModeSection = document.createElement('div');
    syncModeSection.className = 'playlist-settings-section';
    syncModeSection.innerHTML = '<div class="playlist-settings-section-title">Sync behavior</div>';
    const syncModeSelect = document.createElement('select');
    syncModeSelect.className = 'form-select';
    [
        { value: 'mirror', label: 'Mirror source playlist (replace tracks)' },
        { value: 'append', label: 'Append new tracks only (keep existing)' }
    ].forEach(({ value, label }) => {
        const option = document.createElement('option');
        option.value = value;
        option.textContent = label;
        syncModeSelect.appendChild(option);
    });
    syncModeSection.appendChild(syncModeSelect);
    panel.appendChild(syncModeSection);

    const confirmed = await globalThis.DeezSpoTag.ui.showModal({
        title: 'Merge Monitored Playlists',
        message: '',
        allowHtml: false,
        contentElement: panel,
        buttons: [
            { label: 'Merge & Sync', value: 'merge', primary: true },
            { label: 'Cancel', value: 'cancel' }
        ]
    });
    if (confirmed?.value !== 'merge') {
        return;
    }

    const selectedPlaylists = Array.from(sourceList.querySelectorAll('input[type="checkbox"]:checked'))
        .map(input => ({
            source: String(input.dataset.mergeSource || '').trim(),
            sourceId: String(input.dataset.mergeSourceId || '').trim()
        }))
        .filter(item => item.source && item.sourceId);
    if (selectedPlaylists.length < 2) {
        showToast('Select at least two monitored playlists to merge.', true);
        return;
    }

    if (!plexCheck.checked && !jellyfinCheck.checked) {
        showToast('Select at least one merge target (Plex or Jellyfin).', true);
        return;
    }

    const payload = {
        playlists: selectedPlaylists,
        name: String(nameInput.value || '').trim(),
        description: String(descriptionInput.value || '').trim(),
        syncMode: syncModeSelect.value || 'mirror',
        syncToPlex: plexCheck.checked,
        syncToJellyfin: jellyfinCheck.checked
    };

    try {
        const result = await fetchJson('/api/library/playlists/merge-sync', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        const targetSummary = Array.isArray(result?.targets)
            ? result.targets
                .map(target => `${String(target.target || '').toUpperCase()}: ${target.success ? 'ok' : 'failed'} (${target.syncedTracks || 0})`)
                .join(' | ')
            : '';
        const statusMessage = result?.message || 'Merge sync completed.';
        const summarySuffix = targetSummary ? ` ${targetSummary}` : '';
        showToast(`${statusMessage}${summarySuffix}`);
    } catch (error) {
        showToast(`Playlist merge failed: ${error?.message || 'Unknown error'}`, true);
    }
}

async function openPlaylistSettingsPanel(source, sourceId, playlistName, playlistPrefs) {
    if (!Array.isArray(libraryState.folders) || libraryState.folders.length === 0) {
        try {
            const folders = await fetchJson('/api/library/folders');
            libraryState.folders = Array.isArray(folders)
                ? folders.map(normalizeFolderConversionState)
                : [];
        } catch {
            libraryState.folders = Array.isArray(libraryState.folders) ? libraryState.folders : [];
        }
    }

    const enabledFolders = (libraryState.folders || []).filter(isMusicRecommendationEligibleFolder);

    const prefKey = `${source}:${sourceId}`;
    const localPlaylistPrefs = getStoredPreferences('playlistWatchlist');
    const stored = {
        ...playlistPrefs[prefKey],
        ...localPlaylistPrefs[prefKey]
    };

    // Load routing rules, blocked track rules, available playlist tracks, and latest global download settings in parallel.
    const [existingRules, existingBlockRules, trackCandidatesResponse, globalSettingsResponse] = await Promise.all([
        fetchJson(`/api/library/playlists/${encodeURIComponent(source)}/${encodeURIComponent(sourceId)}/routing-rules`).catch(() => []),
        fetchJson(`/api/library/playlists/${encodeURIComponent(source)}/${encodeURIComponent(sourceId)}/ignore-rules`).catch(() => []),
        fetchJson(`/api/library/playlists/${encodeURIComponent(source)}/${encodeURIComponent(sourceId)}/tracks`).catch(() => null),
        fetchJson('/api/getSettings').catch(() => null)
    ]);
    const panelMultiQuality = globalSettingsResponse?.settings?.multiQuality;
    const panelGlobalMultiQualityEnabled = panelMultiQuality
        ? (panelMultiQuality.enabled === true || panelMultiQuality.secondaryEnabled === true)
        : (libraryState.globalMultiQualityEnabled === true);
    libraryState.globalMultiQualityEnabled = panelGlobalMultiQualityEnabled;

    const panel = document.createElement('div');
    panel.className = 'playlist-settings-panel watchlist-playlist-settings';

    const panelIntro = document.createElement('div');
    panelIntro.className = 'playlist-settings-intro';
    panelIntro.textContent = 'Tune sync behavior, route matching tracks to folders, and block tracks you do not want synced.';
    panel.appendChild(panelIntro);

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

    // Section: Atmos destination folder
    const atmosFolderSection = document.createElement('div');
    atmosFolderSection.className = 'playlist-settings-section';
    const atmosFolderTitle = document.createElement('div');
    atmosFolderTitle.className = 'playlist-settings-section-title';
    atmosFolderTitle.textContent = 'Atmos destination folder';
    const atmosFolderSelect = document.createElement('select');
    atmosFolderSelect.className = 'form-select ps-atmos-folder-select';
    atmosFolderSelect.id = `ps-atmos-folder-${source}-${sourceId}`;
    const globalAtmosOption = document.createElement('option');
    globalAtmosOption.value = '';
    globalAtmosOption.textContent = 'Use global Atmos folder';
    atmosFolderSelect.appendChild(globalAtmosOption);
    enabledFolders.forEach((folder) => {
        const option = document.createElement('option');
        option.value = String(folder.id ?? '');
        option.textContent = String(folder.displayName || 'Folder');
        atmosFolderSelect.appendChild(option);
    });
    const atmosFolderHint = document.createElement('div');
    atmosFolderHint.className = 'playlist-settings-help';
    atmosFolderHint.textContent = 'Used when download mode includes Atmos (Dual quality or Atmos only).';
    atmosFolderSection.appendChild(atmosFolderTitle);
    atmosFolderSection.appendChild(atmosFolderSelect);
    atmosFolderSection.appendChild(atmosFolderHint);
    panel.appendChild(atmosFolderSection);

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
        { value: 'jellyfin', label: 'Jellyfin' },
        { value: 'none', label: 'No media server (download only)' }
    ].forEach(({ value, label }) => {
        const option = document.createElement('option');
        option.value = value;
        option.textContent = label;
        serverSelect.appendChild(option);
    });
    const serverHint = document.createElement('div');
    serverHint.className = 'playlist-settings-help';
    serverHint.textContent = 'Choose "No media server" to keep downloads without recreating server playlists.';
    serverSection.appendChild(serverTitle);
    serverSection.appendChild(serverSelect);
    serverSection.appendChild(serverHint);
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
    const engineHint = document.createElement('div');
    engineHint.className = 'playlist-settings-help';
    engineHint.textContent = 'Exact-match watchlist mapping pins to Deezer when a Deezer ID/ISRC match is found.';
    engineSection.appendChild(engineTitle);
    engineSection.appendChild(engineSelect);
    engineSection.appendChild(engineHint);
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

    const syncAtmosFolderVisibility = () => {
        const selectedMode = String(downloadModeSelect?.value || 'standard').trim().toLowerCase();
        const hasPlaylistAtmosMode = selectedMode === 'dual_quality' || selectedMode === 'atmos_only';
        const followsGlobalDownloadSource = !String(engineSelect?.value || '').trim();
        const canUseGlobalAtmos = followsGlobalDownloadSource && panelGlobalMultiQualityEnabled === true;
        const shouldShowAtmosFolder = hasPlaylistAtmosMode || canUseGlobalAtmos;

        atmosFolderSection.hidden = !shouldShowAtmosFolder;
        atmosFolderSelect.disabled = !shouldShowAtmosFolder;
        if (hasPlaylistAtmosMode) {
            atmosFolderHint.textContent = 'Used when monitored playlist download mode includes Atmos.';
        } else if (canUseGlobalAtmos) {
            atmosFolderHint.textContent = 'Global multi-quality is enabled, so Atmos may be downloaded when following global download source.';
        } else {
            atmosFolderHint.textContent = 'Used when download mode includes Atmos (Dual quality or Atmos only).';
        }
    };

    downloadModeSelect.addEventListener('change', syncAtmosFolderVisibility);
    engineSelect.addEventListener('change', syncAtmosFolderVisibility);
    syncAtmosFolderVisibility();

    const syncModeSection = document.createElement('div');
    syncModeSection.className = 'playlist-settings-section';
    const syncModeTitle = document.createElement('div');
    syncModeTitle.className = 'playlist-settings-section-title';
    syncModeTitle.textContent = 'Sync behavior';
    const syncModeSelect = document.createElement('select');
    syncModeSelect.className = 'form-select ps-sync-mode-select';
    syncModeSelect.id = `ps-sync-mode-${source}-${sourceId}`;
    [
        { value: 'mirror', label: 'Mirror source playlist (replace tracks)' },
        { value: 'append', label: 'Append new tracks only (keep existing)' }
    ].forEach(({ value, label }) => {
        const option = document.createElement('option');
        option.value = value;
        option.textContent = label;
        syncModeSelect.appendChild(option);
    });
    syncModeSection.appendChild(syncModeTitle);
    syncModeSection.appendChild(syncModeSelect);
    panel.appendChild(syncModeSection);

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
    rulesSection.className = 'playlist-settings-section playlist-rule-section';
    const rulesHeader = document.createElement('div');
    rulesHeader.className = 'playlist-settings-title-row';
    const rulesTitle = document.createElement('div');
    rulesTitle.className = 'playlist-settings-section-title';
    rulesTitle.textContent = 'Track routing rules';
    const rulesCount = document.createElement('span');
    rulesCount.className = 'playlist-settings-rule-count';
    rulesHeader.appendChild(rulesTitle);
    rulesHeader.appendChild(rulesCount);
    const rulesHint = document.createElement('div');
    rulesHint.className = 'playlist-settings-help';
    rulesHint.textContent = 'Send matching tracks to a specific destination folder.';
    const rulesColumns = document.createElement('div');
    rulesColumns.className = 'routing-rule-columns';
    rulesColumns.innerHTML = `
        <span>Field</span>
        <span>Match</span>
        <span>Value</span>
        <span>Destination</span>
        <span></span>
    `;
    const rulesList = document.createElement('div');
    rulesList.className = 'routing-rules-list';
    const rulesEmpty = document.createElement('div');
    rulesEmpty.className = 'routing-rules-empty';
    rulesEmpty.textContent = 'No routing rules yet.';

    const refreshRoutingRuleState = () => {
        const count = rulesList.querySelectorAll('.routing-rule-row').length;
        rulesCount.textContent = count === 1 ? '1 rule' : `${count} rules`;
        rulesEmpty.hidden = count > 0;
    };
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
        operatorSelect.innerHTML = ops.map(([value, label]) => `<option value="${escapeHtml(value)}">${escapeHtml(label)}</option>`).join('');
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
            .map(f => `<option value="${escapeHtml(f)}" ${currentField === f ? 'selected' : ''}>${escapeHtml(fieldLabels[f] || f)}</option>`)
            .join('');
        const operatorOpts = getOps(currentField)
            .map(([v, l]) => `<option value="${escapeHtml(v)}" ${rule?.conditionOperator === v ? 'selected' : ''}>${escapeHtml(l)}</option>`)
            .join('');
        const folderRuleOpts = enabledFolders
            .map(f => `<option value="${escapeHtml(String(f.id || ''))}" ${rule?.destinationFolderId == f.id ? 'selected' : ''}>${escapeHtml(f.displayName || 'Folder')}</option>`)
            .join('');
        const normalizedValue = String(rule?.conditionValue || '').trim();
        const normalizedOperator = String(rule?.conditionOperator || '').trim().toLowerCase();

        const row = document.createElement('div');
        row.className = 'routing-rule-row';
        row.innerHTML = `
            <select class="rr-field" aria-label="Rule field">
                ${conditionFieldOpts}
            </select>
            <select class="rr-operator" aria-label="Rule operator">
                ${operatorOpts}
            </select>
            <div class="rr-value-wrap">
                <select class="rr-value rr-value-choice" aria-label="Rule value"></select>
                <select class="rr-value rr-value-explicit" aria-label="Explicit value">
                    <option value="is_true" ${normalizedOperator === 'is_true' ? 'selected' : ''}>Explicit tracks only</option>
                    <option value="is_false" ${normalizedOperator === 'is_false' ? 'selected' : ''}>Clean/non-explicit tracks only</option>
                </select>
            </div>
            <select class="rr-folder" aria-label="Destination folder">
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

        row.querySelector('.routing-rule-remove').addEventListener('click', () => {
            row.remove();
            refreshRoutingRuleState();
        });
        return row;
    }

    (Array.isArray(existingRules) ? existingRules : []).forEach(rule => rulesList.appendChild(buildRuleRow(rule)));

    const addRuleBtn = document.createElement('button');
    addRuleBtn.className = 'btn btn-secondary action-btn btn-sm routing-rules-add-btn';
    addRuleBtn.type = 'button';
    addRuleBtn.textContent = 'Add routing rule';
    addRuleBtn.addEventListener('click', () => {
        rulesList.appendChild(buildRuleRow(null));
        refreshRoutingRuleState();
    });

    rulesSection.appendChild(rulesHeader);
    rulesSection.appendChild(rulesHint);
    rulesSection.appendChild(rulesColumns);
    rulesSection.appendChild(rulesList);
    rulesSection.appendChild(rulesEmpty);
    rulesSection.appendChild(addRuleBtn);
    refreshRoutingRuleState();
    panel.appendChild(rulesSection);

    // Section: Blocked track rules
    const blockedSection = document.createElement('div');
    blockedSection.className = 'playlist-settings-section playlist-rule-section';
    const blockedHeader = document.createElement('div');
    blockedHeader.className = 'playlist-settings-title-row';
    const blockedTitle = document.createElement('div');
    blockedTitle.className = 'playlist-settings-section-title';
    blockedTitle.textContent = 'Blocked track rules';
    const blockedCount = document.createElement('span');
    blockedCount.className = 'playlist-settings-rule-count';
    blockedHeader.appendChild(blockedTitle);
    blockedHeader.appendChild(blockedCount);
    const blockedHint = document.createElement('div');
    blockedHint.className = 'playlist-settings-help';
    blockedHint.textContent = 'Skip matching tracks before sync or download.';
    const blockedColumns = document.createElement('div');
    blockedColumns.className = 'routing-rule-columns blocked-rule-columns';
    blockedColumns.innerHTML = `
        <span>Field</span>
        <span>Match</span>
        <span>Value</span>
        <span></span>
    `;

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
            .map(f => `<option value="${escapeHtml(f)}" ${currentField === f ? 'selected' : ''}>${escapeHtml(fieldLabels[f] || f)}</option>`)
            .join('');
        const operatorOpts = getOps(currentField)
            .map(([v, l]) => `<option value="${escapeHtml(v)}" ${rule?.conditionOperator === v ? 'selected' : ''}>${escapeHtml(l)}</option>`)
            .join('');
        const normalizedValue = String(rule?.conditionValue || '').trim();
        const normalizedOperator = String(rule?.conditionOperator || '').trim().toLowerCase();

        const row = document.createElement('div');
        row.className = 'routing-rule-row block-rule-row';
        row.innerHTML = `
            <select class="br-field" aria-label="Block rule field">
                ${conditionFieldOpts}
            </select>
            <select class="br-operator" aria-label="Block rule operator">
                ${operatorOpts}
            </select>
            <div class="rr-value-wrap">
                <select class="rr-value br-value-choice" aria-label="Block rule value"></select>
                <select class="rr-value br-value-explicit" aria-label="Block explicit value">
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
        row.querySelector('.routing-rule-remove').addEventListener('click', () => {
            row.remove();
            refreshBlockRuleState();
        });
        return row;
    }

    const blockRulesList = document.createElement('div');
    blockRulesList.className = 'routing-rules-list';
    const blockRulesEmpty = document.createElement('div');
    blockRulesEmpty.className = 'routing-rules-empty';
    blockRulesEmpty.textContent = 'No blocked-track rules yet.';

    const refreshBlockRuleState = () => {
        const count = blockRulesList.querySelectorAll('.block-rule-row').length;
        blockedCount.textContent = count === 1 ? '1 rule' : `${count} rules`;
        blockRulesEmpty.hidden = count > 0;
    };

    (Array.isArray(existingBlockRules) ? existingBlockRules : []).forEach(rule => blockRulesList.appendChild(buildBlockRuleRow(rule)));

    const addBlockRuleBtn = document.createElement('button');
    addBlockRuleBtn.className = 'btn btn-secondary action-btn btn-sm routing-rules-add-btn';
    addBlockRuleBtn.type = 'button';
    addBlockRuleBtn.textContent = 'Add block rule';
    addBlockRuleBtn.addEventListener('click', () => {
        blockRulesList.appendChild(buildBlockRuleRow(null));
        refreshBlockRuleState();
    });

    blockedSection.appendChild(blockedHeader);
    blockedSection.appendChild(blockedHint);
    blockedSection.appendChild(blockedColumns);
    blockedSection.appendChild(blockRulesList);
    blockedSection.appendChild(blockRulesEmpty);
    blockedSection.appendChild(addBlockRuleBtn);
    refreshBlockRuleState();
    panel.appendChild(blockedSection);

    // Show modal
    if (!globalThis.DeezSpoTag?.ui?.showModal) {
        showToast('Settings panel unavailable', true);
        return;
    }

    // Pre-fill saved values after DOM is in the modal
    setTimeout(() => {
        const folderSel = panel.querySelector('.ps-folder-select');
        const atmosFolderSel = panel.querySelector('.ps-atmos-folder-select');
        const serviceSel = panel.querySelector('.ps-service-select');
        const engineSel = panel.querySelector('.ps-engine-select');
        const downloadModeSel = panel.querySelector('.ps-download-mode-select');
        const syncModeSel = panel.querySelector('.ps-sync-mode-select');
        const artworkToggle = panel.querySelector('.ps-update-artwork');
        const artworkReuseToggle = panel.querySelector('.ps-reuse-saved-artwork');
        const syncArtworkToggles = (changedBy = null) => {
            if (!artworkToggle || !artworkReuseToggle) {
                return;
            }

            if (changedBy === 'update' && artworkToggle.checked) {
                artworkReuseToggle.checked = false;
            } else if (changedBy === 'reuse' && artworkReuseToggle.checked) {
                artworkToggle.checked = false;
            } else if (artworkToggle.checked && artworkReuseToggle.checked) {
                if (changedBy === 'reuse') {
                    artworkToggle.checked = false;
                } else {
                    artworkReuseToggle.checked = false;
                }
            }

            if (!artworkToggle.checked && !artworkReuseToggle.checked) {
                artworkToggle.checked = true;
            }
        };
        if (folderSel && stored.folderId) folderSel.value = String(stored.folderId);
        if (atmosFolderSel) atmosFolderSel.value = stored.atmosFolderId ? String(stored.atmosFolderId) : '';
        if (serviceSel) {
            const normalizedService = String(stored.service || '').trim().toLowerCase();
            serviceSel.value = normalizedService || 'plex';
        }
        if (engineSel) engineSel.value = stored.preferredEngine || '';
        if (downloadModeSel) downloadModeSel.value = stored.downloadVariantMode || 'standard';
        syncAtmosFolderVisibility();
        if (syncModeSel) syncModeSel.value = stored.syncMode || 'mirror';
        if (artworkToggle) artworkToggle.checked = stored.updateArtwork !== false;
        if (artworkReuseToggle) artworkReuseToggle.checked = stored.reuseSavedArtwork === true;
        if (artworkToggle && artworkReuseToggle) {
            artworkToggle.addEventListener('change', () => syncArtworkToggles('update'));
            artworkReuseToggle.addEventListener('change', () => syncArtworkToggles('reuse'));
            syncArtworkToggles(stored.reuseSavedArtwork === true ? 'reuse' : null);
        }
    }, 0);

    const confirmed = await globalThis.DeezSpoTag.ui.showModal({
        title: `Settings — ${playlistName}`,
        message: '',
        allowHtml: false,
        dialogClass: 'is-resizable playlist-settings-modal',
        contentElement: panel,
        buttons: [
            { label: 'Save', value: 'save', primary: true },
            { label: 'Cancel', value: 'cancel' }
        ]
    });

    if (confirmed?.value !== 'save') return;
    await savePlaylistSettingsFromPanel({
        panel,
        source,
        sourceId,
        playlistPrefs,
        prefKey,
        rulesList,
        blockRulesList
    });
}

async function savePlaylistSettingsFromPanel({
    panel,
    source,
    sourceId,
    playlistPrefs,
    prefKey,
    rulesList,
    blockRulesList
}) {
    const values = collectPlaylistSettingsValues(panel);
    const rules = collectPlaylistRoutingRules(rulesList);
    const blockRules = collectPlaylistBlockRules(blockRulesList);
    try {
        // Save preferences
        await fetchJson('/api/library/playlists/preferences', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify([{
                source,
                sourceId,
                folderId: values.folderId,
                atmosFolderId: values.atmosFolderId,
                service: values.service,
                preferredEngine: values.preferredEngine,
                downloadVariantMode: values.downloadVariantMode,
                syncMode: values.syncMode,
                updateArtwork: values.updateArtwork,
                reuseSavedArtwork: values.reuseSavedArtwork
            }])
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
        const updatedPref = {
            folderId: values.folderId ? String(values.folderId) : '',
            atmosFolderId: values.atmosFolderId ? String(values.atmosFolderId) : '',
            service: values.service,
            preferredEngine: values.preferredEngine,
            downloadVariantMode: values.downloadVariantMode,
            syncMode: values.syncMode,
            updateArtwork: values.updateArtwork,
            reuseSavedArtwork: values.reuseSavedArtwork
        };
        storePlaylistPreference(source, sourceId, updatedPref);
        if (playlistPrefs && typeof playlistPrefs === 'object') {
            playlistPrefs[prefKey] = {
                ...playlistPrefs[prefKey],
                ...updatedPref
            };
        }
        showToast('Playlist settings saved.');
        await loadPlaylistBlockedRules();
    } catch (error) {
        showToast(`Save failed: ${error.message}`, true);
    }
}

function collectPlaylistSettingsValues(panel) {
    const folderSel = panel.querySelector('.ps-folder-select');
    const atmosFolderSel = panel.querySelector('.ps-atmos-folder-select');
    const serviceSel = panel.querySelector('.ps-service-select');
    const engineSel = panel.querySelector('.ps-engine-select');
    const downloadModeSel = panel.querySelector('.ps-download-mode-select');
    const syncModeSel = panel.querySelector('.ps-sync-mode-select');
    const artworkToggle = panel.querySelector('.ps-update-artwork');
    const artworkReuseToggle = panel.querySelector('.ps-reuse-saved-artwork');
    const normalizedArtwork = normalizePlaylistArtworkPreference(
        artworkToggle?.checked !== false,
        artworkReuseToggle?.checked === true);
    return {
        folderId: folderSel?.value ? Number(folderSel.value) : null,
        atmosFolderId: atmosFolderSel?.value ? Number(atmosFolderSel.value) : null,
        service: serviceSel?.value || 'plex',
        preferredEngine: engineSel?.value || '',
        downloadVariantMode: downloadModeSel?.value || 'standard',
        syncMode: syncModeSel?.value || 'mirror',
        updateArtwork: normalizedArtwork.updateArtwork,
        reuseSavedArtwork: normalizedArtwork.reuseSavedArtwork
    };
}

function collectPlaylistRoutingRules(rulesList) {
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
        if (!ruleFolder || (field !== 'explicit' && !value)) {
            return;
        }

        rules.push({
            conditionField: field,
            conditionOperator: operator,
            conditionValue: value,
            destinationFolderId: Number(ruleFolder),
            order: idx
        });
    });
    return rules;
}

function collectPlaylistBlockRules(blockRulesList) {
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
        if (field !== 'explicit' && !value) {
            return;
        }

        blockRules.push({
            conditionField: field,
            conditionOperator: operator,
            conditionValue: value,
            order: idx
        });
    });
    return blockRules;
}

function getStoredPreferences(key) {
    try {
        const raw = localStorage.getItem(key);
        return raw ? JSON.parse(raw) : {};
    } catch {
        return {};
    }
}

function normalizePlaylistArtworkPreference(updateArtwork, reuseSavedArtwork) {
    if (reuseSavedArtwork === true) {
        return {
            updateArtwork: false,
            reuseSavedArtwork: true
        };
    }

    return {
        updateArtwork: true,
        reuseSavedArtwork: false
    };
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
        const normalizedArtwork = normalizePlaylistArtworkPreference(
            item.updateArtwork !== false,
            item.reuseSavedArtwork === true);
        merged[key] = {
            ...merged[key],
            folderId: item.destinationFolderId || '',
            atmosFolderId: item.atmosDestinationFolderId == null
                ? (merged[key]?.atmosFolderId || '')
                : String(item.atmosDestinationFolderId),
            service: item.service || merged[key]?.service || 'plex',
            preferredEngine: item.preferredEngine || merged[key]?.preferredEngine || '',
            downloadVariantMode: item.downloadVariantMode || merged[key]?.downloadVariantMode || 'standard',
            syncMode: item.syncMode || merged[key]?.syncMode || 'mirror',
            updateArtwork: normalizedArtwork.updateArtwork,
            reuseSavedArtwork: normalizedArtwork.reuseSavedArtwork
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
    const key = `${source}:${sourceId}`;
    const existingPrefs = getStoredPreferences('playlistWatchlist');
    const folderSelect = container.querySelector(`[data-playlist-folder="${source}"][data-playlist-id="${sourceId}"]`);
    const atmosFolderSelect = container.querySelector(`[data-playlist-atmos-folder="${source}"][data-playlist-id="${sourceId}"]`);
    const serviceSelect = container.querySelector(`[data-playlist-service="${source}"][data-playlist-id="${sourceId}"]`);
    const engineSelect = container.querySelector(`[data-playlist-engine="${source}"][data-playlist-id="${sourceId}"]`);
    const downloadModeSelect = container.querySelector(`[data-playlist-download-mode="${source}"][data-playlist-id="${sourceId}"]`);
    const folderId = folderSelect?.value || null;
    const atmosFolderId = atmosFolderSelect?.value ?? (existingPrefs[key]?.atmosFolderId || '');
    const service = serviceSelect?.value || 'plex';
    const preferredEngine = engineSelect?.value || '';
    const downloadVariantMode = downloadModeSelect?.value || 'standard';
    const syncMode = container.querySelector(`[data-playlist-sync-mode="${source}"][data-playlist-id="${sourceId}"]`)?.value || 'mirror';
    const normalizedArtwork = normalizePlaylistArtworkPreference(
        container.querySelector(`[data-playlist-update-artwork="${source}"][data-playlist-id="${sourceId}"]`)?.checked !== false,
        container.querySelector(`[data-playlist-reuse-artwork="${source}"][data-playlist-id="${sourceId}"]`)?.checked === true);
    const updateArtwork = normalizedArtwork.updateArtwork;
    const reuseSavedArtwork = normalizedArtwork.reuseSavedArtwork;
    storePlaylistPreference(source, sourceId, {
        folderId: folderId || '',
        atmosFolderId: atmosFolderId || '',
        service,
        preferredEngine,
        downloadVariantMode,
        syncMode,
        updateArtwork,
        reuseSavedArtwork
    });
    const payload = [{
        source,
        sourceId,
        folderId: folderId ? Number(folderId) : null,
        atmosFolderId: atmosFolderId ? Number(atmosFolderId) : null,
        service,
        preferredEngine,
        downloadVariantMode,
        syncMode,
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
    Object.keys(prefs).forEach((key) => {
        const normalizedArtwork = normalizePlaylistArtworkPreference(
            prefs[key]?.updateArtwork !== false,
            prefs[key]?.reuseSavedArtwork === true);
        prefs[key] = {
            ...prefs[key],
            updateArtwork: normalizedArtwork.updateArtwork,
            reuseSavedArtwork: normalizedArtwork.reuseSavedArtwork
        };
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
            const normalizedArtwork = normalizePlaylistArtworkPreference(
                value?.updateArtwork !== false,
                value?.reuseSavedArtwork === true);
            return {
                source: parts[0],
                sourceId: parts.slice(1).join(':'),
                folderId: value?.folderId ? Number(value.folderId) : null,
                atmosFolderId: value?.atmosFolderId ? Number(value.atmosFolderId) : null,
                service: value?.service || null,
                preferredEngine: value?.preferredEngine || null,
                updateArtwork: normalizedArtwork.updateArtwork,
                reuseSavedArtwork: normalizedArtwork.reuseSavedArtwork
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
    applyWatchlistToggleState(button, watching);
}

function applyWatchlistToggleState(button, watching, pending = false) {
    if (!button) {
        return;
    }

    button.textContent = watching ? 'Monitoring Artist' : 'Monitor Artist';
    button.classList.toggle('btn-secondary', watching);
    button.classList.toggle('btn-primary', !watching);
    button.classList.toggle('is-busy', pending);
    button.dataset.watching = watching ? 'true' : 'false';
    button.disabled = pending;
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
            const currentlyWatching = button.dataset.watching === 'true';
            const nextWatching = !currentlyWatching;
            applyWatchlistToggleState(button, nextWatching, true);
            try {
                if (currentlyWatching) {
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
                applyWatchlistToggleState(button, nextWatching, false);
            } catch (error) {
                applyWatchlistToggleState(button, currentlyWatching, false);
                showToast(`Watchlist update failed: ${error.message}`, true);
            }
        };
    globalThis.DeezSpoTag.LibraryWatchlist = { toggle };

    button.style.cursor = 'pointer';
    applyWatchlistToggleState(button, false, true);
    button.addEventListener('click', toggle);
    await refreshWatchlistToggle(button, artistIdValue);
}
