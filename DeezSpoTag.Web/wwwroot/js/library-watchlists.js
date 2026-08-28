// Extracted from library.js: watchlist and playlist feature module

function escapeHtml(text) {
    const shared = globalThis.DeezSpoTagLibraryPageCommon?.escapeHtml;
    if (typeof shared === 'function') {
        return shared(text);
    }

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

function emitActivitiesLiveUpdate(source, context = {}) {
    if (typeof globalThis.dispatchEvent !== 'function') {
        return;
    }

    globalThis.dispatchEvent(new CustomEvent('deezspotag:activities-live-update', {
        detail: {
            source: String(source || 'watchlist'),
            ...context
        }
    }));
}

function normalizeWatchlistHistoryEntries(historyRaw) {
    if (Array.isArray(historyRaw)) {
        return historyRaw;
    }

    if (historyRaw && Array.isArray(historyRaw.entries)) {
        return historyRaw.entries;
    }

    return [];
}

async function confirmWithAppUi(message, options = {}) {
    if (typeof globalThis.DeezSpoTag?.ui?.showModal !== 'function') {
        showToast('Confirmation modal is unavailable right now.', true);
        return false;
    }

    const result = await globalThis.DeezSpoTag.ui.showModal({
        title: options.title || 'Confirm',
        message,
        buttons: [
            { label: options.cancelText || 'Cancel', value: false },
            { label: options.okText || 'OK', value: true, primary: true }
        ]
    });
    return result?.value === true;
}

async function loadWatchlist() {
    const container = document.getElementById('watchlistContainer');
    if (!container) {
        return;
    }
    try {
        if (!Array.isArray(libraryState.folders) || !libraryState.folders.length) {
            try {
                const folders = await fetchJson('/api/library/folders?downloadOnly=true&contentType=music');
                libraryState.folders = Array.isArray(folders)
                    ? folders.map(normalizeFolderConversionState)
                    : [];
            } catch {
                libraryState.folders = [];
            }
        }
        const [items, historyRaw] = await Promise.all([
            fetchJson('/api/library/watchlist'),
            fetchJson('/api/history/watchlist?limit=500&offset=0').catch(() => ({ entries: [] }))
        ]);

        if (!Array.isArray(items) || items.length === 0) {
            container.innerHTML = '<div class="watchlist-empty-state">No monitored artists yet.</div>';
            return;
        }

        // Build detected-count map from history
        const detectedByItemKey = {};
        const historyEntries = normalizeWatchlistHistoryEntries(historyRaw);
        for (const h of historyEntries) {
            if (h.watchType === 'artist' && h.itemKey) {
                const key = String(h.itemKey).toLowerCase();
                detectedByItemKey[key] = (detectedByItemKey[key] || 0) + 1;
            }
        }

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

            const detectedCount = detectedByItemKey[`artist:${item.artistId}`] || 0;
            const lastChecked = formatRelativeTime(item.lastCheckedUtc);
            const statsHtml = [
                detectedCount > 0 ? `<span class="watchlist-card-stat">${detectedCount} detected</span>` : '',
                lastChecked ? `<span class="watchlist-card-stat">Checked ${lastChecked}</span>` : ''
            ].filter(Boolean).join('');

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
                <div class="watchlist-action-menu watchlist-action-menu--hover">
                    <button class="watchlist-kebab-btn" type="button" title="Actions" data-artist-menu-toggle="${escapeHtml(String(item.artistId || ''))}" aria-expanded="false">
                        <i class="fa-solid fa-ellipsis-vertical"></i>
                    </button>
                    <div class="watchlist-action-dropdown watchlist-action-dropdown--hover" data-artist-menu="${escapeHtml(String(item.artistId || ''))}" hidden>
                        <button class="dropdown-item" data-artist-action="settings" data-artist-id="${escapeHtml(String(item.artistId || ''))}" data-artist-name="${escapeHtml(item.artistName)}" data-artist-folder="${escapeHtml(item.destinationFolderId == null ? '' : String(item.destinationFolderId))}" data-artist-groups="${escapeHtml(JSON.stringify(item.watchedAlbumGroups || []))}" data-artist-top-songs="${escapeHtml(item.topSongsEnabled == null ? '' : String(item.topSongsEnabled))}" data-artist-latest="${escapeHtml(item.latestReleasesOnly == null ? '' : String(item.latestReleasesOnly))}" data-artist-engine="${escapeHtml(item.preferredEngine || '')}" data-artist-routing-rules="${escapeHtml(JSON.stringify(item.routingRules || []))}" data-artist-atmos-folder="${escapeHtml(item.atmosDestinationFolderId == null ? '' : String(item.atmosDestinationFolderId))}" data-artist-download-mode="${escapeHtml(item.downloadVariantMode || 'standard')}" data-artist-top-songs-sync="${escapeHtml(item.topSongsSyncMode || 'mirror')}" data-artist-discography="${escapeHtml(item.downloadDiscographyEnabled == null ? '' : String(item.downloadDiscographyEnabled))}" data-artist-block-rules="${escapeHtml(JSON.stringify(item.ignoreRules || []))}" type="button">
                            <i class="fa-solid fa-gear"></i>
                            <span>Settings</span>
                        </button>
                        <button class="dropdown-item danger" data-watchlist-remove="${escapeHtml(String(item.artistId || ''))}" type="button">
                            <i class="fa-solid fa-trash"></i>
                            <span>Unmonitor</span>
                        </button>
                    </div>
                </div>
                <div class="watchlist-card-strip">
                    <div class="watchlist-card-name">${escapeHtml(item.artistName)}</div>
                </div>
            </div>`;
        }).join('');

        const closeArtistActionMenus = () => {
            container.querySelectorAll('[data-artist-menu]').forEach(menu => {
                menu.hidden = true;
            });
            container.querySelectorAll('[data-artist-menu-toggle]').forEach(toggle => {
                toggle.setAttribute('aria-expanded', 'false');
            });
        };

        container.querySelectorAll('[data-artist-menu-toggle]').forEach(button => {
            button.addEventListener('click', (event) => {
                event.stopPropagation();
                const artistId = button.dataset.artistMenuToggle;
                const menu = artistId
                    ? container.querySelector(`[data-artist-menu="${artistId}"]`)
                    : null;
                const shouldOpen = Boolean(menu?.hidden);
                closeArtistActionMenus();
                if (menu && shouldOpen) {
                    menu.hidden = false;
                    button.setAttribute('aria-expanded', 'true');
                }
            });
        });

        container.querySelectorAll('[data-artist-menu]').forEach(menu => {
            menu.addEventListener('click', event => event.stopPropagation());
        });

        if (container.dataset.artistMenuBound !== 'true') {
            document.addEventListener('click', () => {
                closeArtistActionMenus();
            });
            container.dataset.artistMenuBound = 'true';
        }

        container.querySelectorAll('[data-artist-action="settings"]').forEach(button => {
            button.addEventListener('click', async () => {
                const artistId = button.dataset.artistId;
                if (!artistId) {
                    return;
                }

                closeArtistActionMenus();
                try {
                    const savedSettings = await openArtistSettingsPanel({
                        artistId,
                        artistName: button.dataset.artistName || 'Artist',
                        currentFolderId: button.dataset.artistFolder || '',
                        currentGroups: parseArtistSettingsGroups(button.dataset.artistGroups),
                        currentTopSongs: button.dataset.artistTopSongs || '',
                        currentLatestOnly: button.dataset.artistLatest || '',
                        currentPreferredEngine: button.dataset.artistEngine || '',
                        currentRoutingRules: parseArtistRoutingRules(button.dataset.artistRoutingRules),
                        currentAtmosFolderId: button.dataset.artistAtmosFolder || '',
                        currentDownloadMode: button.dataset.artistDownloadMode || 'standard',
                        currentTopSongsSyncMode: button.dataset.artistTopSongsSync || 'mirror',
                        currentDiscography: button.dataset.artistDiscography || '',
                        currentBlockRules: parseArtistRoutingRules(button.dataset.artistBlockRules)
                    });
                    if (savedSettings !== null) {
                        button.dataset.artistFolder = savedSettings.folderId;
                        button.dataset.artistGroups = JSON.stringify(savedSettings.groups);
                        button.dataset.artistTopSongs = String(savedSettings.topSongs);
                        button.dataset.artistLatest = String(savedSettings.latest);
                        button.dataset.artistEngine = savedSettings.preferredEngine;
                        button.dataset.artistRoutingRules = JSON.stringify(savedSettings.routingRules);
                        button.dataset.artistAtmosFolder = savedSettings.atmosFolderId;
                        button.dataset.artistDownloadMode = savedSettings.downloadVariantMode;
                        button.dataset.artistTopSongsSync = savedSettings.topSongsSyncMode;
                        button.dataset.artistDiscography = String(savedSettings.downloadDiscography);
                        button.dataset.artistBlockRules = JSON.stringify(savedSettings.blockRules);
                    }
                } catch (error) {
                    showToast(`Artist settings failed: ${error.message}`, true);
                }
            });
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
                    emitActivitiesLiveUpdate('watchlist', { action: 'remove', artistId });
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
                const ignoredRaw = await fetchJson(`/api/library/playlists/${encodeURIComponent(item.source)}/${encodeURIComponent(item.sourceId)}/ignore-details`).catch(() => []);
                const ignoredRows = Array.isArray(ignoredRaw) ? ignoredRaw : [];
                return ignoredRows
                    .map(entry => {
                        const trackSourceId = String(entry?.trackSourceId || '').trim();
                        if (!trackSourceId) {
                            return null;
                        }

                        return {
                            source: item.source,
                            sourceId: item.sourceId,
                            playlistName: item.name || 'Playlist',
                            trackSourceId,
                            title: String(entry?.title || trackSourceId).trim(),
                            artist: String(entry?.artist || '').trim(),
                            album: String(entry?.album || '').trim(),
                            isrc: String(entry?.isrc || '').trim()
                        };
                    })
                    .filter(Boolean);
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
            container.dataset.stale = 'false';
            container.dataset.loadState = 'ready';
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
                try {
                    await openPlaylistSettingsPanel(source, sourceId, playlistName, playlistPrefs);
                    await loadPlaylistBlockedRules();
                } catch (error) {
                    showToast(`Playlist settings failed to load: ${error?.message || 'Unknown error'}`, true);
                }
            });
        });
        container.dataset.stale = 'false';
        container.dataset.loadState = 'ready';
    } catch (error) {
        container.dataset.loadState = 'error';
        container.innerHTML = `<div class="watchlist-empty-state">Failed to load blocked items: ${escapeHtml(error?.message || 'Unknown error')}</div>`;
    }
}

function renderSharedPlaylistActions(options = {}) {
    const renderer = globalThis.renderSharedPlaylistActionButtons;
    if (typeof renderer !== 'function') {
        return '';
    }
    return renderer(options);
}

function parseArtistSettingsGroups(rawValue) {
    try {
        const parsed = JSON.parse(String(rawValue || '[]'));
        return Array.isArray(parsed) ? parsed.map(value => String(value || '').toLowerCase()) : [];
    } catch {
        return [];
    }
}

function parseArtistRoutingRules(rawValue) {
    try {
        const parsed = JSON.parse(String(rawValue || '[]'));
        return Array.isArray(parsed) ? parsed : [];
    } catch {
        return [];
    }
}

function createArtistWatchOption(id, label, checked) {
    const row = document.createElement('label');
    row.className = 'checkbox-group';
    row.innerHTML = `
        <input type="checkbox" id="${id}" ${checked ? 'checked' : ''} />
        <span>${label}</span>
    `;
    return row;
}

function resolveArtistWatchOptionChecked(id, value, selectedGroups, topSongsEnabled, latestOnly) {
    if (value) {
        return selectedGroups.includes(value);
    }

    if (id === 'artist-watch-top-songs') {
        return topSongsEnabled;
    }

    return latestOnly;
}

function resolveRoutingOperatorOptions(field) {
    if (field === 'explicit') {
        return [['is_true', 'explicit only'], ['is_false', 'clean only']];
    }

    if (field === 'year') {
        return [['equals', 'equals'], ['gte', 'at least'], ['lte', 'at most']];
    }

    return [['contains', 'contains'], ['equals', 'equals'], ['starts_with', 'starts with']];
}

function resolveArtistSettingsDefaults(currentGroups, currentTopSongs, currentLatestOnly, globalSettings) {
    const selectedGroups = Array.isArray(currentGroups) && currentGroups.length > 0
        ? currentGroups
        : ['album', 'single'];
    const topSongsEnabled = currentTopSongs === ''
        ? false
        : currentTopSongs === 'true';
    const latestOnly = currentLatestOnly === ''
        ? false
        : currentLatestOnly === 'true';

    return { selectedGroups, topSongsEnabled, latestOnly };
}

function appendArtistSettingsFolderOptions(select, folders) {
    folders.forEach((folder) => {
        const option = document.createElement('option');
        option.value = String(folder.id ?? '');
        option.textContent = String(folder.displayName || 'Folder');
        select.appendChild(option);
    });
}

function createArtistWatchOptionsSection(currentDiscography, latestOnly, selectedGroups, topSongsEnabled) {
    const artistOptionsSection = document.createElement('div');
    artistOptionsSection.className = 'playlist-settings-section';
    const artistOptionsTitle = document.createElement('div');
    artistOptionsTitle.className = 'playlist-settings-section-title';
    artistOptionsTitle.textContent = 'Artist watch options';
    const artistOptionsGrid = document.createElement('div');
    artistOptionsGrid.className = 'artist-watch-options-grid';
    const downloadDiscography = currentDiscography === ''
        ? latestOnly !== true
        : currentDiscography === 'true';
    const latestOnlyChecked = downloadDiscography ? false : latestOnly;
    const discographyOption = createArtistWatchOption('artist-watch-discography', 'Discography', downloadDiscography);
    discographyOption.classList.add('artist-watch-option-discography');
    artistOptionsGrid.appendChild(discographyOption);
    const coveredOptions = [
        ['artist-watch-album', 'Album', 'album'],
        ['artist-watch-single', 'Single', 'single'],
        ['artist-watch-compilation', 'Compilation', 'compilation'],
        ['artist-watch-appears-on', 'Appears On', 'appears_on'],
        ['artist-watch-top-songs', "Spotify's Top Songs", null],
        ['artist-watch-latest-releases', 'Latest Releases', null]
    ];
    coveredOptions.forEach(([id, label, value]) => {
        const checked = resolveArtistWatchOptionChecked(id, value, selectedGroups, topSongsEnabled, latestOnlyChecked);
        artistOptionsGrid.appendChild(createArtistWatchOption(id, label, checked));
    });
    const latestOnlyInput = artistOptionsGrid.querySelector('#artist-watch-latest-releases');
    const discographyInput = artistOptionsGrid.querySelector('#artist-watch-discography');
    const coveredInputs = [
        'artist-watch-album',
        'artist-watch-single',
        'artist-watch-compilation',
        'artist-watch-appears-on',
        'artist-watch-top-songs',
        'artist-watch-latest-releases'
    ]
        .map(id => artistOptionsGrid.querySelector(`#${id}`))
        .filter(Boolean);
    const syncDiscographyCoveredOptions = () => {
        const discographySelected = discographyInput?.checked === true;
        coveredInputs.forEach(input => {
            input.disabled = discographySelected;
            input.closest('.checkbox-group')?.classList.toggle('is-disabled', discographySelected);
        });
    };
    latestOnlyInput?.addEventListener('change', () => {
        if (latestOnlyInput.checked && discographyInput) {
            discographyInput.checked = false;
            syncDiscographyCoveredOptions();
        }
    });
    discographyInput?.addEventListener('change', () => {
        if (discographyInput.checked && latestOnlyInput) {
            latestOnlyInput.checked = false;
        }
        syncDiscographyCoveredOptions();
    });
    syncDiscographyCoveredOptions();
    artistOptionsSection.appendChild(artistOptionsTitle);
    artistOptionsSection.appendChild(artistOptionsGrid);

    return artistOptionsSection;
}

const playlistSyncTargetOptions = [
    { value: 'plex', label: 'Plex' },
    { value: 'jellyfin', label: 'Jellyfin' },
    { value: 'navidrome', label: 'Navidrome' }
];

let watchlistDownloadSourceCatalogPromise = null;

async function getWatchlistDownloadSourceCatalog() {
    if (!watchlistDownloadSourceCatalogPromise) {
        watchlistDownloadSourceCatalogPromise = fetchJson('/api/download-sources')
            .then(payload => {
                const options = Array.isArray(payload?.watchlist) ? payload.watchlist : [];
                const defaultDownloadEngineOrder = Array.isArray(payload?.defaultDownloadEngineOrder)
                    ? payload.defaultDownloadEngineOrder
                    : [];
                if (options.length === 0) {
                    throw new Error('Download source options unavailable.');
                }

                return {
                    options: [
                        ...options.map(option => ({
                            value: String(option.value || '').trim(),
                            label: String(option.label || option.value || '').trim()
                        })).filter(option => option.value && option.label)
                    ],
                    defaultDownloadEngineOrder
                };
            });
    }

    return watchlistDownloadSourceCatalogPromise;
}

function normalizeWatchlistDownloadEngineOrderConfig(config, defaults) {
    const defaultEngines = Array.isArray(defaults) ? defaults : [];
    const incoming = config && typeof config === 'object' ? config : {};
    const incomingEngines = Array.isArray(incoming.engines) ? incoming.engines : [];
    const byEngine = new Map(defaultEngines.map(engine => [String(engine.engine || '').toLowerCase(), engine]));
    const normalized = { enabled: true, engines: [] };
    const seenEngines = new Set();

    incomingEngines.forEach(engineConfig => {
        const engineKey = String(engineConfig?.engine || '').trim().toLowerCase();
        const defaultEngine = byEngine.get(engineKey);
        if (!defaultEngine || seenEngines.has(engineKey)) {
            return;
        }

        seenEngines.add(engineKey);
        normalized.engines.push(normalizeWatchlistDownloadEngineOrderItem(engineConfig, defaultEngine));
    });

    defaultEngines.forEach(defaultEngine => {
        const engineKey = String(defaultEngine.engine || '').trim().toLowerCase();
        if (!engineKey || seenEngines.has(engineKey)) {
            return;
        }

        normalized.engines.push(normalizeWatchlistDownloadEngineOrderItem(defaultEngine, defaultEngine));
    });

    return normalized;
}

function normalizeWatchlistDownloadEngineOrderItem(engineConfig, defaultEngine) {
    const defaultQualities = Array.isArray(defaultEngine.qualities) ? defaultEngine.qualities : [];
    const incomingQualities = Array.isArray(engineConfig?.qualities) ? engineConfig.qualities : [];
    const incomingByQuality = new Map();

    incomingQualities.forEach(qualityConfig => {
        const qualityKey = String(qualityConfig?.quality || '').trim().toLowerCase();
        if (!qualityKey || incomingByQuality.has(qualityKey)) {
            return;
        }

        incomingByQuality.set(qualityKey, qualityConfig);
    });

    const qualities = defaultQualities.map(defaultQuality => {
        const qualityKey = String(defaultQuality.quality || '').trim().toLowerCase();
        const incomingQuality = incomingByQuality.get(qualityKey);
        return {
            quality: defaultQuality.quality,
            label: defaultQuality.label,
            enabled: incomingQuality ? incomingQuality.enabled !== false : defaultQuality.enabled !== false
        };
    });

    return {
        engine: defaultEngine.engine,
        label: defaultEngine.label,
        enabled: engineConfig?.enabled !== false,
        qualities
    };
}

function createWatchlistDownloadEngineOrderSection(config, defaults) {
    const section = document.createElement('div');
    section.className = 'playlist-settings-section watchlist-engine-order-section';
    const toggle = document.createElement('div');
    toggle.className = 'watchlist-engine-order-toggle';
    const title = document.createElement('div');
    title.className = 'playlist-settings-section-title';
    title.textContent = 'Custom download sources';
    const editButton = document.createElement('button');
    editButton.type = 'button';
    editButton.className = 'btn btn-secondary action-btn btn-sm watchlist-engine-order-edit';
    editButton.textContent = 'Edit selection';
    editButton.setAttribute('aria-expanded', 'false');
    toggle.appendChild(title);
    toggle.appendChild(editButton);
    const summary = document.createElement('div');
    summary.className = 'watchlist-engine-order-summary';
    const panel = document.createElement('div');
    panel.className = 'watchlist-engine-order-panel';
    panel.hidden = true;
    const list = document.createElement('div');
    list.className = 'watchlist-engine-order-list';
    const footer = document.createElement('div');
    footer.className = 'watchlist-engine-order-footer';
    footer.textContent = 'Choose which sources and qualities Custom mode may use. Highest available quality remains the fixed priority.';
    panel.appendChild(list);
    panel.appendChild(footer);
    section.appendChild(toggle);
    section.appendChild(summary);
    section.appendChild(panel);
    renderWatchlistDownloadEngineOrder(list, config, defaults, () => {
        updateWatchlistDownloadEngineOrderSummary(list, summary);
    });
    updateWatchlistDownloadEngineOrderSummary(list, summary);
    editButton.addEventListener('click', () => {
        panel.hidden = !panel.hidden;
        const expanded = !panel.hidden;
        editButton.textContent = expanded ? 'Done' : 'Edit selection';
        editButton.setAttribute('aria-expanded', expanded ? 'true' : 'false');
    });
    return { section, list, panel, summary, editButton };
}

function renderWatchlistDownloadEngineOrder(list, config, defaults, onChange) {
    const normalized = normalizeWatchlistDownloadEngineOrderConfig(config, defaults);
    list.innerHTML = '';
    normalized.engines.forEach(engine => {
        const row = document.createElement('div');
        row.className = `watchlist-engine-order-item${engine.enabled ? '' : ' is-disabled'}`;
        row.dataset.engine = engine.engine;
        row.innerHTML = `
            <div class="watchlist-engine-order-header">
                <input type="checkbox" class="watchlist-engine-order-enabled" ${engine.enabled ? 'checked' : ''} aria-label="Enable ${escapeHtml(engine.label)}">
                <span class="watchlist-engine-order-title">${escapeHtml(engine.label)}</span>
                <span class="watchlist-engine-order-status">${engine.enabled ? 'enabled' : 'disabled'}</span>
            </div>
            <div class="watchlist-engine-quality-list">
                ${engine.qualities.map(quality => `
                    <label class="checkbox-group" data-quality="${escapeHtml(quality.quality)}">
                        <input type="checkbox" class="watchlist-engine-quality-enabled" ${quality.enabled ? 'checked' : ''} aria-label="Enable ${escapeHtml(engine.label)} ${escapeHtml(quality.label)}">
                        <span>${escapeHtml(quality.label)}</span>
                    </label>
                `).join('')}
            </div>
        `;
        list.appendChild(row);
    });
    bindWatchlistDownloadEngineOrderControls(list, onChange);
}

function bindWatchlistDownloadEngineOrderControls(list, onChange) {
    list.querySelectorAll('.watchlist-engine-order-item').forEach(item => {
        item.querySelector('.watchlist-engine-order-enabled')?.addEventListener('change', event => {
            const checked = event.target?.checked === true;
            item.classList.toggle('is-disabled', !checked);
            const status = item.querySelector('.watchlist-engine-order-status');
            if (status) {
                status.textContent = checked ? 'enabled' : 'disabled';
            }
            onChange?.();
        });
    });

    list.querySelectorAll('.watchlist-engine-quality-enabled').forEach(input => {
        input.addEventListener('change', () => {
            onChange?.();
        });
    });
}

function updateWatchlistDownloadEngineOrderSummary(list, summary) {
    const engineRows = Array.from(list.querySelectorAll('.watchlist-engine-order-item'));
    const enabledEngines = engineRows.filter(row => row.querySelector('.watchlist-engine-order-enabled')?.checked === true);
    const enabledQualityCount = enabledEngines.reduce((total, row) => (
        total + Array.from(row.querySelectorAll('.watchlist-engine-quality-enabled')).filter(input => input.checked === true).length
    ), 0);
    const engineLabels = enabledEngines
        .map(row => row.querySelector('.watchlist-engine-order-title')?.textContent?.trim())
        .filter(Boolean);
    const engineSummary = engineLabels.length > 0
        ? engineLabels.join(', ')
        : 'No enabled sources';
    summary.textContent = `${enabledEngines.length} enabled source${enabledEngines.length === 1 ? '' : 's'}, ${enabledQualityCount} enabled qualit${enabledQualityCount === 1 ? 'y' : 'ies'}: ${engineSummary}.`;
}

function collectWatchlistDownloadEngineOrder(panel) {
    const engines = Array.from(panel.querySelectorAll('.watchlist-engine-order-list .watchlist-engine-order-item'))
        .map(item => ({
            engine: item.dataset.engine || '',
            enabled: item.querySelector('.watchlist-engine-order-enabled')?.checked === true,
            qualities: Array.from(item.querySelectorAll('.watchlist-engine-quality-list .checkbox-group'))
                .map(qualityRow => ({
                    quality: qualityRow.dataset.quality || '',
                    enabled: qualityRow.querySelector('.watchlist-engine-quality-enabled')?.checked === true
                }))
        }))
        .filter(engine => engine.engine);

    return { enabled: true, engines };
}

const playlistDownloadModeOptions = [
    { value: 'standard', label: 'Standard only' },
    { value: 'dual_quality', label: 'Dual quality (standard + Atmos)' },
    { value: 'atmos_only', label: 'Atmos only' }
];

function createPlaylistSettingsSelectSection({
    title,
    selectClass,
    selectId,
    options,
    value,
    helpText
}) {
    const section = document.createElement('div');
    section.className = 'playlist-settings-section';
    const titleElement = document.createElement('div');
    titleElement.className = 'playlist-settings-section-title';
    titleElement.textContent = title;
    const select = document.createElement('select');
    select.className = `form-select ${selectClass}`;
    if (selectId) {
        select.id = selectId;
    }

    options.forEach(optionConfig => {
        const valueText = Array.isArray(optionConfig) ? optionConfig[0] : optionConfig.value;
        const labelText = Array.isArray(optionConfig) ? optionConfig[1] : optionConfig.label;
        select.appendChild(new Option(labelText, valueText));
    });
    select.value = value;
    section.appendChild(titleElement);
    section.appendChild(select);
    if (helpText) {
        const hint = document.createElement('div');
        hint.className = 'playlist-settings-help';
        hint.textContent = helpText;
        section.appendChild(hint);
    }

    return { section, select };
}

function normalizePlaylistSyncTargets(targets, fallbackService = null) {
    const values = Array.isArray(targets)
        ? targets
        : (fallbackService ? [fallbackService] : ['plex']);
    const normalized = [];
    values.forEach(value => {
        const target = String(value || '').trim().toLowerCase();
        if (!target || target === 'none' || normalized.includes(target)) {
            return;
        }

        if (playlistSyncTargetOptions.some(option => option.value === target)) {
            normalized.push(target);
        }
    });
    return normalized;
}

function getPrimaryPlaylistSyncTarget(syncTargets) {
    const targets = normalizePlaylistSyncTargets(syncTargets);
    return targets.length ? targets[0] : 'none';
}

function createPlaylistSyncTargetsSection(source, sourceId, selectedTargets) {
    const section = document.createElement('div');
    section.className = 'playlist-settings-section playlist-sync-targets-section';
    const title = document.createElement('div');
    title.className = 'playlist-settings-section-title';
    title.textContent = 'Server';
    const grid = document.createElement('div');
    grid.className = 'playlist-sync-target-grid';
    const selected = new Set(normalizePlaylistSyncTargets(selectedTargets));

    playlistSyncTargetOptions.forEach(option => {
        const row = document.createElement('label');
        row.className = 'checkbox-group playlist-sync-target-option';
        const input = document.createElement('input');
        input.type = 'checkbox';
        input.className = 'ps-sync-target-checkbox';
        input.value = option.value;
        input.checked = selected.has(option.value);
        input.dataset.playlistSyncTarget = source;
        input.dataset.playlistId = sourceId;
        const label = document.createElement('span');
        label.textContent = option.label;
        row.appendChild(input);
        row.appendChild(label);
        grid.appendChild(row);
    });

    const hint = document.createElement('div');
    hint.className = 'playlist-settings-help';
    hint.textContent = 'Leave all unchecked to keep downloads without recreating server playlists.';
    section.appendChild(title);
    section.appendChild(grid);
    section.appendChild(hint);
    return section;
}

function collectPlaylistSyncTargets(container, source, sourceId) {
    return Array.from(container.querySelectorAll(`[data-playlist-sync-target="${source}"][data-playlist-id="${sourceId}"]`))
        .filter(input => input.checked)
        .map(input => String(input.value || '').trim().toLowerCase())
        .filter(Boolean);
}

function collectPlaylistSyncTargetsFromPanel(panel) {
    return Array.from(panel.querySelectorAll('.ps-sync-target-checkbox'))
        .filter(input => input.checked)
        .map(input => String(input.value || '').trim().toLowerCase())
        .filter(Boolean);
}

async function openArtistSettingsPanel({
    artistId,
    artistName,
    currentFolderId,
    currentGroups,
    currentTopSongs,
    currentLatestOnly,
    currentPreferredEngine,
    currentRoutingRules,
    currentAtmosFolderId,
    currentDownloadMode,
    currentTopSongsSyncMode,
    currentDiscography,
    currentBlockRules
}) {
    if (!globalThis.DeezSpoTag?.ui?.showModal) {
        throw new Error('Settings panel unavailable.');
    }

    await ensurePlaylistSettingsFoldersLoaded();
    const globalSettingsResponse = await fetchJson('/api/getSettings').catch(() => null);
    const downloadSourceCatalog = await getWatchlistDownloadSourceCatalog();
    const downloadSourceOptions = downloadSourceCatalog.options
        .filter(option => String(option.value || '').trim().toLowerCase() !== 'custom');
    const globalSettings = globalSettingsResponse?.settings || {};
    const { selectedGroups, topSongsEnabled, latestOnly } = resolveArtistSettingsDefaults(
        currentGroups,
        currentTopSongs,
        currentLatestOnly,
        globalSettings);
    const enabledFolders = (libraryState.folders || []).filter(isMusicRecommendationEligibleFolder);
    const panel = document.createElement('div');
    panel.className = 'playlist-settings-panel watchlist-playlist-settings';

    const intro = document.createElement('div');
    intro.className = 'playlist-settings-intro';
    intro.textContent = 'Configure where monitored artist releases and Spotify top songs are downloaded.';
    panel.appendChild(intro);

    const folderSection = document.createElement('div');
    folderSection.className = 'playlist-settings-section';
    const folderTitle = document.createElement('div');
    folderTitle.className = 'playlist-settings-section-title';
    folderTitle.textContent = 'Destination folder';
    const folderSelect = document.createElement('select');
    folderSelect.className = 'form-select ps-folder-select';
    const noFolderOption = document.createElement('option');
    noFolderOption.value = '';
    noFolderOption.textContent = 'No folder';
    folderSelect.appendChild(noFolderOption);
    appendArtistSettingsFolderOptions(folderSelect, enabledFolders);
    folderSelect.value = currentFolderId ? String(currentFolderId) : '';
    const folderHint = document.createElement('div');
    folderHint.className = 'playlist-settings-help';
    folderHint.textContent = 'Used by artist watchlist downloads, including latest releases and Spotify top songs.';
    folderSection.appendChild(folderTitle);
    folderSection.appendChild(folderSelect);
    folderSection.appendChild(folderHint);
    panel.appendChild(folderSection);

    const atmosFolderSection = document.createElement('div');
    atmosFolderSection.className = 'playlist-settings-section';
    const atmosFolderTitle = document.createElement('div');
    atmosFolderTitle.className = 'playlist-settings-section-title';
    atmosFolderTitle.textContent = 'Atmos destination folder';
    const atmosFolderSelect = document.createElement('select');
    atmosFolderSelect.className = 'form-select ps-atmos-folder-select';
    atmosFolderSelect.appendChild(new Option('Use global Atmos folder', ''));
    appendArtistSettingsFolderOptions(atmosFolderSelect, enabledFolders);
    atmosFolderSelect.value = currentAtmosFolderId ? String(currentAtmosFolderId) : '';
    const atmosFolderHint = document.createElement('div');
    atmosFolderHint.className = 'playlist-settings-help';
    atmosFolderHint.textContent = 'Used when artist watchlist download mode includes Atmos.';
    atmosFolderSection.appendChild(atmosFolderTitle);
    atmosFolderSection.appendChild(atmosFolderSelect);
    atmosFolderSection.appendChild(atmosFolderHint);
    panel.appendChild(atmosFolderSection);

    const artistEngine = createPlaylistSettingsSelectSection({
        title: 'Download engine',
        selectClass: 'ps-engine-select',
        options: downloadSourceOptions,
        value: String(currentPreferredEngine || 'auto').toLowerCase(),
        helpText: 'Selects the download source for this watched artist.'
    });
    const engineSelect = artistEngine.select;
    panel.appendChild(artistEngine.section);

    const artistDownloadMode = createPlaylistSettingsSelectSection({
        title: 'Download mode',
        selectClass: 'ps-download-mode-select',
        options: [
        ['standard', 'Standard only'],
        ['dual_quality', 'Dual quality (standard + Atmos)'],
        ['atmos_only', 'Atmos only']
        ],
        value: String(currentDownloadMode || 'standard').toLowerCase()
    });
    const downloadModeSection = artistDownloadMode.section;
    const downloadModeSelect = artistDownloadMode.select;
    const syncAtmosFolderVisibility = () => {
        const selectedMode = String(downloadModeSelect.value || 'standard').toLowerCase();
        const shouldShowAtmosFolder = selectedMode === 'dual_quality' || selectedMode === 'atmos_only';
        atmosFolderSection.hidden = !shouldShowAtmosFolder;
        atmosFolderSelect.disabled = !shouldShowAtmosFolder;
    };
    downloadModeSelect.addEventListener('change', syncAtmosFolderVisibility);
    downloadModeSection.appendChild(downloadModeTitle);
    downloadModeSection.appendChild(downloadModeSelect);
    panel.appendChild(downloadModeSection);
    syncAtmosFolderVisibility();

    panel.appendChild(createArtistWatchOptionsSection(currentDiscography, latestOnly, selectedGroups, topSongsEnabled));

    const syncModeSection = document.createElement('div');
    syncModeSection.className = 'playlist-settings-section';
    const syncModeTitle = document.createElement('div');
    syncModeTitle.className = 'playlist-settings-section-title';
    syncModeTitle.textContent = 'Spotify top songs sync behavior';
    const topSongsSyncSelect = document.createElement('select');
    topSongsSyncSelect.className = 'form-select ps-sync-mode-select';
    [
        ['mirror', 'Mirror Spotify top songs'],
        ['append', 'Append new top songs only']
    ].forEach(([value, label]) => {
        topSongsSyncSelect.appendChild(new Option(label, value));
    });
    topSongsSyncSelect.value = String(currentTopSongsSyncMode || 'mirror').toLowerCase() === 'append'
        ? 'append'
        : 'mirror';
    const syncModeHint = document.createElement('div');
    syncModeHint.className = 'playlist-settings-help';
    syncModeHint.textContent = 'Applies only to Spotify top songs for this artist.';
    syncModeSection.appendChild(syncModeTitle);
    syncModeSection.appendChild(topSongsSyncSelect);
    syncModeSection.appendChild(syncModeHint);
    panel.appendChild(syncModeSection);

    const routingSection = document.createElement('div');
    routingSection.className = 'playlist-settings-section';
    const routingTitle = document.createElement('div');
    routingTitle.className = 'playlist-settings-section-title';
    routingTitle.textContent = 'Routing rules';
    const routingHelp = document.createElement('div');
    routingHelp.className = 'playlist-settings-help';
    routingHelp.textContent = 'Send matching artist watchlist downloads to a specific destination folder.';
    const rulesList = document.createElement('div');
    rulesList.className = 'playlist-routing-rules';
    const addRuleButton = document.createElement('button');
    addRuleButton.type = 'button';
    addRuleButton.className = 'btn btn-secondary action-btn btn-sm';
    addRuleButton.textContent = 'Add routing rule';

    const createRuleRow = (rule = {}) => {
        const row = document.createElement('div');
        row.className = 'routing-rule-row';
        row.innerHTML = `
            <select class="rr-field" aria-label="Rule field">
                <option value="artist">Artist</option>
                <option value="title">Title</option>
                <option value="album">Album</option>
                <option value="genre">Genre</option>
                <option value="year">Year</option>
                <option value="explicit">Explicit</option>
            </select>
            <select class="rr-operator" aria-label="Rule operator"></select>
            <div class="rr-value-wrap">
                <input class="rr-value rr-value-choice" type="text" aria-label="Rule value" />
                <select class="rr-value rr-value-explicit" aria-label="Explicit value">
                    <option value="is_true">Explicit tracks only</option>
                    <option value="is_false">Clean/non-explicit tracks only</option>
                </select>
            </div>
            <select class="rr-folder" aria-label="Destination folder">
                <option value="">No folder</option>
            </select>
            <button class="routing-rule-remove" type="button" title="Remove rule"><i class="fa-solid fa-xmark"></i></button>`;

        const supportedFields = ['artist', 'title', 'album', 'genre', 'year', 'explicit'];
        const fieldSelect = row.querySelector('.rr-field');
        const operatorSelect = row.querySelector('.rr-operator');
        const valueInput = row.querySelector('.rr-value-choice');
        const explicitSelect = row.querySelector('.rr-value-explicit');
        const folderSelect = row.querySelector('.rr-folder');
        const normalizedField = supportedFields.includes(String(rule.conditionField || '').toLowerCase())
            ? String(rule.conditionField).toLowerCase()
            : 'artist';

        enabledFolders.forEach(folder => {
            folderSelect.appendChild(new Option(String(folder.displayName || 'Folder'), String(folder.id ?? '')));
        });

        const refreshOperatorOptions = () => {
            const field = fieldSelect.value;
            const operators = resolveRoutingOperatorOptions(field);
            operatorSelect.innerHTML = '';
            operators.forEach(([value, label]) => {
                operatorSelect.appendChild(new Option(label, value));
            });
            const requestedOperator = String(rule.conditionOperator || '').toLowerCase();
            operatorSelect.value = operators.some(([value]) => value === requestedOperator)
                ? requestedOperator
                : operators[0][0];
            const explicit = field === 'explicit';
            valueInput.hidden = explicit;
            explicitSelect.hidden = !explicit;
        };

        fieldSelect.value = normalizedField;
        valueInput.value = String(rule.conditionValue || '');
        explicitSelect.value = String(rule.conditionOperator || '').toLowerCase() === 'is_false' ? 'is_false' : 'is_true';
        folderSelect.value = rule.destinationFolderId == null ? '' : String(rule.destinationFolderId);
        refreshOperatorOptions();
        fieldSelect.addEventListener('change', refreshOperatorOptions);
        row.querySelector('.routing-rule-remove')?.addEventListener('click', () => row.remove());
        return row;
    };

    (Array.isArray(currentRoutingRules) ? currentRoutingRules : []).forEach(rule => {
        rulesList.appendChild(createRuleRow(rule));
    });
    addRuleButton.addEventListener('click', () => {
        rulesList.appendChild(createRuleRow());
    });
    routingSection.appendChild(routingTitle);
    routingSection.appendChild(routingHelp);
    routingSection.appendChild(rulesList);
    routingSection.appendChild(addRuleButton);
    panel.appendChild(routingSection);

    const blockSection = document.createElement('div');
    blockSection.className = 'playlist-settings-section playlist-rule-section';
    const blockHeader = document.createElement('div');
    blockHeader.className = 'playlist-settings-title-row';
    const blockTitle = document.createElement('div');
    blockTitle.className = 'playlist-settings-section-title';
    blockTitle.textContent = 'Blocked track rules';
    const blockCount = document.createElement('span');
    blockCount.className = 'playlist-settings-rule-count';
    blockHeader.appendChild(blockTitle);
    blockHeader.appendChild(blockCount);
    const blockHelp = document.createElement('div');
    blockHelp.className = 'playlist-settings-help';
    blockHelp.textContent = 'Skip matching artist watchlist tracks before download.';
    const blockColumns = document.createElement('div');
    blockColumns.className = 'routing-rule-columns blocked-rule-columns';
    blockColumns.innerHTML = `
        <span>Field</span>
        <span>Match</span>
        <span>Value</span>
        <span></span>
    `;
    const blockRulesList = document.createElement('div');
    blockRulesList.className = 'routing-rules-list';
    const blockEmpty = document.createElement('div');
    blockEmpty.className = 'routing-rules-empty';
    blockEmpty.textContent = 'No blocked-track rules yet.';
    const refreshBlockState = () => {
        const count = blockRulesList.querySelectorAll('.block-rule-row').length;
        blockCount.textContent = count === 1 ? '1 rule' : `${count} rules`;
        blockEmpty.hidden = count > 0;
    };
    const createBlockRow = (rule = {}) => {
        const row = createRuleRow(rule);
        row.classList.add('block-rule-row');
        row.querySelector('.rr-field')?.classList.replace('rr-field', 'br-field');
        row.querySelector('.rr-operator')?.classList.replace('rr-operator', 'br-operator');
        row.querySelector('.rr-value-choice')?.classList.replace('rr-value-choice', 'br-value-choice');
        row.querySelector('.rr-value-explicit')?.classList.replace('rr-value-explicit', 'br-value-explicit');
        row.querySelector('.rr-folder')?.remove();
        row.querySelector('.routing-rule-remove')?.addEventListener('click', refreshBlockState);
        return row;
    };
    (Array.isArray(currentBlockRules) ? currentBlockRules : []).forEach(rule => {
        blockRulesList.appendChild(createBlockRow(rule));
    });
    const addBlockRuleButton = document.createElement('button');
    addBlockRuleButton.type = 'button';
    addBlockRuleButton.className = 'btn btn-secondary action-btn btn-sm routing-rules-add-btn';
    addBlockRuleButton.textContent = 'Add block rule';
    addBlockRuleButton.addEventListener('click', () => {
        blockRulesList.appendChild(createBlockRow());
        refreshBlockState();
    });
    blockSection.appendChild(blockHeader);
    blockSection.appendChild(blockHelp);
    blockSection.appendChild(blockColumns);
    blockSection.appendChild(blockRulesList);
    blockSection.appendChild(blockEmpty);
    blockSection.appendChild(addBlockRuleButton);
    panel.appendChild(blockSection);
    refreshBlockState();

    const confirmed = await globalThis.DeezSpoTag.ui.showModal({
        title: `Settings — ${artistName}`,
        message: '',
        allowHtml: false,
        dialogClass: 'is-resizable playlist-settings-modal',
        contentElement: panel,
        buttons: [
            { label: 'Save', value: 'save', primary: true },
            { label: 'Cancel', value: 'cancel' }
        ]
    });

    if (confirmed?.value !== 'save') {
        return null;
    }

    const destinationFolderId = folderSelect.value ? Number(folderSelect.value) : null;
    const collectGroup = (id, value) => panel.querySelector(`#${id}`)?.checked ? value : null;
    const downloadDiscographyEnabled = panel.querySelector('#artist-watch-discography')?.checked === true;
    const watchedArtistAlbumGroup = downloadDiscographyEnabled
        ? ['album', 'single', 'compilation', 'appears_on']
        : [
            collectGroup('artist-watch-album', 'album'),
            collectGroup('artist-watch-single', 'single'),
            collectGroup('artist-watch-compilation', 'compilation'),
            collectGroup('artist-watch-appears-on', 'appears_on')
        ].filter(Boolean);
    const watchArtistTopSongsEnabled = !downloadDiscographyEnabled
        && panel.querySelector('#artist-watch-top-songs')?.checked === true;
    const watchArtistLatestReleasesOnly = !downloadDiscographyEnabled
        && panel.querySelector('#artist-watch-latest-releases')?.checked === true;
    const preferredEngine = engineSelect.value || 'auto';
    const routingRules = collectPlaylistRoutingRules(rulesList);
    const blockRules = collectPlaylistBlockRules(blockRulesList);
    const downloadVariantMode = downloadModeSelect.value || 'standard';
    const atmosDestinationFolderId = atmosFolderSelect.value ? Number(atmosFolderSelect.value) : null;
    const topSongsSyncMode = topSongsSyncSelect.value || 'mirror';
    if (watchedArtistAlbumGroup.length === 0 && !watchArtistTopSongsEnabled) {
        throw new Error('Select at least one artist watch option.');
    }

    await fetchJson(`/api/library/watchlist/${encodeURIComponent(artistId)}/preferences`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            destinationFolderId,
            watchedArtistAlbumGroup,
            watchArtistTopSongsEnabled,
            watchArtistLatestReleasesOnly,
            preferredEngine,
            routingRules,
            atmosDestinationFolderId,
            downloadVariantMode,
            topSongsSyncMode,
            downloadDiscographyEnabled,
            blockRules
        })
    });
    showToast('Artist settings saved.');
    return {
        folderId: folderSelect.value || '',
        groups: watchedArtistAlbumGroup,
        topSongs: watchArtistTopSongsEnabled,
        latest: watchArtistLatestReleasesOnly,
        preferredEngine: preferredEngine || 'auto',
        routingRules,
        atmosFolderId: atmosFolderSelect.value || '',
        downloadVariantMode,
        topSongsSyncMode,
        downloadDiscography: downloadDiscographyEnabled,
        blockRules
    };
}

async function openSharedPlaylistArtworkPickerViaShared(source, sourceId, playlistName, options = {}) {
    const picker = globalThis.openSharedPlaylistArtworkPicker;
    if (typeof picker !== 'function') {
        showToast('Artwork picker unavailable.', true);
        return false;
    }
    return picker(source, sourceId, playlistName, options);
}

function toNonNegativeCount(value) {
    const parsed = Number(value);
    return Number.isFinite(parsed) && parsed > 0 ? Math.floor(parsed) : 0;
}

function resolvePlaylistWatchlistPresentation(item) {
    const sourceTrackCount = toNonNegativeCount(item.trackCount);
    const ignoredBlockedTrackCount = toNonNegativeCount(item.ignoredBlockedTrackCount);
    const totalTrackCount = Math.max(0, sourceTrackCount - ignoredBlockedTrackCount);
    const syncedTrackCount = Math.min(toNonNegativeCount(item.syncedTrackCount), totalTrackCount);
    const hasAuthoritativeIncompleteCount = item.incompleteTrackCount !== null
        && item.incompleteTrackCount !== undefined;
    const incompleteTrackCount = hasAuthoritativeIncompleteCount
        ? Math.min(toNonNegativeCount(item.incompleteTrackCount), totalTrackCount)
        : Math.max(0, totalTrackCount - syncedTrackCount);
    return {
        totalTrackCount,
        syncedTrackCount,
        ignoredBlockedTrackCount,
        incompleteTrackCount,
        reroutedTrackCount: toNonNegativeCount(item.reroutedTrackCount),
        reviewTrackCount: toNonNegativeCount(item.reviewTrackCount),
        missingTrackCount: toNonNegativeCount(item.missingTrackCount),
        mappingRetryCount: toNonNegativeCount(item.mappingRetryCount),
        blockedTrackCount: toNonNegativeCount(item.blockedTrackCount),
        failedTrackCount: toNonNegativeCount(item.failedTrackCount),
        hasIncompleteSync: incompleteTrackCount > 0 && totalTrackCount > syncedTrackCount
    };
}

function formatPlaylistWatchlistRunStatus(item) {
    const runStatus = String(item.lastRunStatus || '').trim().toLowerCase();
    if (!runStatus) {
        return 'never checked yet';
    }

    return runStatus.replaceAll('_', ' ');
}

function renderPlaylistWatchlistPresentationBadges(item) {
    const {
        totalTrackCount,
        syncedTrackCount,
        ignoredBlockedTrackCount,
        reroutedTrackCount,
        reviewTrackCount,
        missingTrackCount,
        mappingRetryCount,
        blockedTrackCount,
        failedTrackCount,
        hasIncompleteSync
    } = resolvePlaylistWatchlistPresentation(item);
    const syncBadge = hasIncompleteSync ? renderPlaylistWatchlistSyncBadge(syncedTrackCount, totalTrackCount) : '';
    const stateBadges = [
        renderPlaylistWatchlistStateBadge(reviewTrackCount, 'review', 'track requiring review', 'fa-magnifying-glass'),
        renderPlaylistWatchlistStateBadge(missingTrackCount, 'missing', 'missing downloadable track', 'fa-download'),
        renderPlaylistWatchlistStateBadge(mappingRetryCount, 'mapping-retry', 'track waiting for mapping', 'fa-link-slash'),
        renderPlaylistWatchlistStateBadge(blockedTrackCount, 'blocked', 'blocked track', 'fa-ban'),
        renderPlaylistWatchlistStateBadge(Math.max(0, ignoredBlockedTrackCount - blockedTrackCount), 'ignored', 'ignored track', 'fa-eye-slash'),
        renderPlaylistWatchlistStateBadge(failedTrackCount, 'failed', 'failed track', 'fa-triangle-exclamation'),
        renderPlaylistWatchlistStateBadge(reroutedTrackCount, 'rerouted', 'rerouted track', 'fa-route')
    ].filter(Boolean).join('');

    if (!syncBadge && !stateBadges) {
        return '';
    }

    const stateBadgeGroup = stateBadges ? `<div class="playlist-watchlist-state-badges">${stateBadges}</div>` : '';
    return `${syncBadge}${stateBadgeGroup}`;
}

function renderPlaylistWatchlistSyncBadge(syncedTrackCount, totalTrackCount) {
    const label = `${syncedTrackCount}/${totalTrackCount}`;
    return `<div class="library-badge library-badge--partial playlist-watchlist-sync-badge" title="Target sync: ${label} tracks. Incomplete is not the same as missing downloads.">${escapeHtml(label)}</div>`;
}

function renderPlaylistWatchlistStateBadge(count, state, label, icon) {
    if (count <= 0) {
        return '';
    }

    const pluralSuffix = count === 1 ? '' : 's';
    return `<span class="playlist-watchlist-state-badge playlist-watchlist-state-badge--${state}" title="${count} ${label}${pluralSuffix}"><i class="fa-solid ${icon}"></i></span>`;
}

function renderPlaylistWatchlistPriorityBadge(priorityNumber) {
    return `<div class="playlist-watchlist-priority-badge" title="Sync priority ${priorityNumber}">${escapeHtml(String(priorityNumber))}</div>`;
}

function isPlaylistWatchlistPaneActiveForRefresh() {
    const watchlistPane = document.getElementById('watchlist-content');
    const playlistPane = document.getElementById('watchlist-playlists-content');
    return Boolean(
        playlistPane
        && (playlistPane.classList.contains('active') || playlistPane.classList.contains('show'))
        && (!watchlistPane || watchlistPane.classList.contains('active') || watchlistPane.classList.contains('show')));
}

function bindPlaylistWatchlistRealtimeRefresh() {
    if (globalThis.__deezspotagPlaylistWatchlistRealtimeRefreshBound === true) {
        return;
    }

    globalThis.__deezspotagPlaylistWatchlistRealtimeRefreshBound = true;
    globalThis.addEventListener('deezspotag:watchlist-updated', () => {
        const container = document.getElementById('playlistWatchlistContainer');
        if (!container) {
            return;
        }

        if (isPlaylistWatchlistPaneActiveForRefresh()) {
            void loadPlaylistWatchlist();
            return;
        }

        container.dataset.stale = 'true';
    });
}

function refreshPlaylistWatchlistPriorityBadges(container) {
    container.querySelectorAll('.watchlist-playlist-card-v2[data-playlist-source][data-playlist-id]').forEach((card, index) => {
        const badge = card.querySelector('.playlist-watchlist-priority-badge');
        const priorityNumber = index + 1;
        if (badge) {
            badge.textContent = String(priorityNumber);
            badge.title = `Sync priority ${priorityNumber}`;
        }
    });
}

function collectPlaylistWatchlistPriorityOrder(container) {
    return Array.from(container.querySelectorAll('.watchlist-playlist-card-v2[data-playlist-source][data-playlist-id]'))
        .map(card => ({
            source: card.dataset.playlistSource || '',
            sourceId: card.dataset.playlistId || ''
        }))
        .filter(item => item.source && item.sourceId);
}

async function savePlaylistWatchlistPriorityOrder(container) {
    const order = collectPlaylistWatchlistPriorityOrder(container);
    if (order.length === 0) {
        return;
    }

    await fetchJson('/api/library/playlists/priority-order', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(order)
    });
}

function bindPlaylistWatchlistDragOrdering(container) {
    let draggedCard = null;
    let dragMoved = false;

    container.querySelectorAll('.watchlist-playlist-card-v2[data-playlist-source][data-playlist-id]').forEach(card => {
        card.addEventListener('dragstart', (event) => {
            draggedCard = card;
            dragMoved = false;
            card.classList.add('is-dragging');
            if (event.dataTransfer) {
                event.dataTransfer.effectAllowed = 'move';
                event.dataTransfer.setData('text/plain', card.dataset.playlistId || '');
            }
        });

        card.addEventListener('dragend', async () => {
            const moved = dragMoved;
            card.classList.remove('is-dragging');
            draggedCard = null;
            dragMoved = false;
            if (!moved) {
                return;
            }

            refreshPlaylistWatchlistPriorityBadges(container);
            try {
                await savePlaylistWatchlistPriorityOrder(container);
                showToast('Playlist sync priority updated.');
            } catch (error) {
                showToast(`Priority update failed: ${error.message}`, true);
                await loadPlaylistWatchlist();
            }
        });
    });

    container.ondragover = (event) => {
        if (!draggedCard) {
            return;
        }

        const target = event.target.closest('.watchlist-playlist-card-v2[data-playlist-source][data-playlist-id]');
        if (!target || target === draggedCard || !container.contains(target)) {
            return;
        }

        event.preventDefault();
        const rect = target.getBoundingClientRect();
        const afterTarget = event.clientY > rect.top + rect.height / 2
            || (event.clientY >= rect.top && event.clientY <= rect.bottom && event.clientX > rect.left + rect.width / 2);
        container.insertBefore(draggedCard, afterTarget ? target.nextSibling : target);
        dragMoved = true;
    };
}

// NOSONAR - preserves end-to-end watchlist rendering/binding flow in one place to avoid UI wiring regressions.
async function loadPlaylistWatchlist() {
    const container = document.getElementById('playlistWatchlistContainer');
    if (!container) return;
    container.dataset.loadState = 'loading';
    container.ondragover = null;
    container.innerHTML = '<div class="watchlist-empty-state">Loading monitored playlists...</div>';
    const mergeButton = document.getElementById('mergePlaylistWatchlistBtn');
    const resetRuntimeButton = document.getElementById('resetPlaylistWatchlistRuntimeBtn');
    if (mergeButton) {
        mergeButton.disabled = true;
        mergeButton.onclick = null;
    }
    if (resetRuntimeButton) {
        resetRuntimeButton.disabled = true;
        resetRuntimeButton.onclick = null;
    }
    try {
        if (!Array.isArray(libraryState.folders) || !libraryState.folders.length) {
            try {
                const folders = await fetchJson('/api/library/folders?downloadOnly=true&contentType=music');
                libraryState.folders = Array.isArray(folders)
                    ? folders.map(normalizeFolderConversionState)
                    : [];
            } catch {
                libraryState.folders = [];
            }
        }
        const [itemsRaw, runtime, manualUnavailable] = await Promise.all([
            fetchJson('/api/library/playlists'),
            fetchJson('/api/library/playlists/watch-runtime').catch(() => null),
            fetchJson('/api/library/playlists/manual-unavailable').catch(() => ({ count: 0, tracks: [] }))
        ]);
        const items = Array.isArray(itemsRaw) ? itemsRaw : [];
        const manualUnavailableTracks = Array.isArray(manualUnavailable?.tracks) ? manualUnavailable.tracks : [];
        const hasManualUnavailable = manualUnavailableTracks.length > 0;
        if (items.length === 0 && !hasManualUnavailable) {
            container.innerHTML = '<div class="watchlist-empty-state">No monitored playlists yet.</div>';
            container.dataset.loadState = 'ready';
            return;
        }

        const activeSource = runtime?.scheduler?.activeSource
            ? String(runtime.scheduler.activeSource).trim().toLowerCase()
            : '';
        const activeSourceId = runtime?.scheduler?.activeSourceId
            ? String(runtime.scheduler.activeSourceId).trim()
            : '';
        const openCircuits = runtime?.circuits?.filter(circuit => circuit?.isOpen) ?? [];
        const circuitBySource = new Map(
            openCircuits
                .map(circuit => [String(circuit.source || '').trim().toLowerCase(), circuit])
                .filter(entry => Boolean(entry[0]))
        );
        const runtimeHealth = runtime?.runtime || {};
        const claimHealth = runtime?.claims || {};
        const syncJobHealth = runtime?.targetSyncJobs || {};
        const runtimeHealthParts = [];
        if (!runtime) {
            runtimeHealthParts.push('Watchlist runtime telemetry unavailable');
        } else if (runtimeHealth.isRunning) {
            runtimeHealthParts.push('Watchlist cycle running');
        } else if (runtimeHealth.lastCycleCompletedUtc) {
            runtimeHealthParts.push(`Last cycle ${formatRelativeTime(runtimeHealth.lastCycleCompletedUtc)}`);
        } else {
            runtimeHealthParts.push('Watchlist worker has not completed a cycle yet');
        }
        if (runtimeHealth.triggerPending) {
            runtimeHealthParts.push('trigger queued');
        }
        if (Number(runtimeHealth.pendingReconciliationRequests || 0) > 0) {
            runtimeHealthParts.push(`${runtimeHealth.pendingReconciliationRequests} reconciliation request(s)`);
        }
        if (runtimeHealth.lastAdmissionBlockReason) {
            runtimeHealthParts.push(runtimeHealth.lastAdmissionBlockReason);
        }
        if (Number(runtimeHealth.lastRecoveredClaimCount || 0) > 0) {
            runtimeHealthParts.push(`recovered ${runtimeHealth.lastRecoveredClaimCount} stale claim(s)`);
        }
        if (Number(claimHealth.orphanedPending || 0) > 0) {
            runtimeHealthParts.push(`${claimHealth.orphanedPending} orphaned pending claim(s)`);
        }
        const targetSyncWork = Number(syncJobHealth.due || 0)
            + Number(syncJobHealth.processing || 0)
            + Number(syncJobHealth.retryWaiting || 0)
            + Number(syncJobHealth.repairRequired || 0)
            + Number(syncJobHealth.blocked || 0);
        if (targetSyncWork > 0) {
            runtimeHealthParts.push(`${targetSyncWork} target sync job(s)`);
        }
        if (Number(syncJobHealth.expiredProcessing || 0) > 0) {
            runtimeHealthParts.push(`${syncJobHealth.expiredProcessing} expired target lease(s)`);
        }
        if (Number(syncJobHealth.repairRequired || 0) > 0) {
            runtimeHealthParts.push(`${syncJobHealth.repairRequired} target job(s) awaiting automatic repair`);
        }
        if (Number(syncJobHealth.blocked || 0) > 0) {
            runtimeHealthParts.push(`${syncJobHealth.blocked} blocked target job(s)`);
        }
        if (syncJobHealth.oldestPendingUtc) {
            runtimeHealthParts.push(`oldest target job ${formatRelativeTime(syncJobHealth.oldestPendingUtc)}`);
        }
        if (syncJobHealth.lastError) {
            runtimeHealthParts.push(syncJobHealth.lastError);
        }
        const presentation = runtime?.presentation || {};
        const presentationParts = [
            [presentation.review, 'review'],
            [presentation.missing, 'missing'],
            [presentation.mappingRetry, 'mapping retry'],
            [presentation.blocked, 'blocked'],
            [presentation.failed, 'failed'],
            [presentation.queued, 'queued'],
            [presentation.downloading, 'downloading']
        ];
        presentationParts.forEach(([count, label]) => {
            if (Number(count || 0) > 0) {
                runtimeHealthParts.push(`${count} ${label}`);
            }
        });
        const runtimeHealthHtml = `<div class="watchlist-runtime-health">${escapeHtml(runtimeHealthParts.join(' • '))}</div>`;
        if (mergeButton) {
            mergeButton.disabled = items.length < 2;
            mergeButton.onclick = async () => {
                await openPlaylistMergePanel(items);
            };
        }
        if (resetRuntimeButton) {
            resetRuntimeButton.disabled = items.length === 0;
            resetRuntimeButton.onclick = async (event) => {
                event?.preventDefault?.();
                event?.stopPropagation?.();
                try {
                    const confirmed = await confirmWithAppUi(
                        'Reset watchlist runtime state for all monitored playlists and trigger a fresh run?',
                        { title: 'Reset Watchlist Runtime', okText: 'Reset Runtime' });
                    if (!confirmed) {
                        return;
                    }
                    resetRuntimeButton.disabled = true;
                    try {
                        const result = await fetchJson('/api/library/playlists/reset-runtime', { method: 'POST' });
                        showToast(`Watchlist runtime reset (${result?.playlistsReset || 0} playlists).`);
                        await loadPlaylistWatchlist();
                    } catch (error) {
                        showToast(`Runtime reset failed: ${error.message}`, true);
                        resetRuntimeButton.disabled = false;
                    }
                } catch (error) {
                    showToast(`Failed to open confirmation dialog: ${error?.message || 'Unknown error'}`, true);
                }
            };
        }

        const playlistPrefsPromise = hydratePlaylistPreferences();

        const manualUnavailableCard = hasManualUnavailable
            ? renderManualUnavailablePlaylistCard(manualUnavailableTracks, manualUnavailable?.imageUrl)
            : '';

        container.innerHTML = runtimeHealthHtml + items.map((item, index) => {
            const imageUrl = toSafeHttpUrl(item.imageUrl || '');
            const artContent = imageUrl
                ? `<img src="${escapeHtml(imageUrl)}" alt="${escapeHtml(item.name)}" />`
                : `<div class="watchlist-card-art-placeholder"><i class="fa-solid fa-list-music"></i></div>`;
            const presentationBadges = renderPlaylistWatchlistPresentationBadges(item);
            const priorityBadge = renderPlaylistWatchlistPriorityBadge(index + 1);
            const trackCount = item.trackCount === null || item.trackCount === undefined
                ? ''
                : `${item.trackCount} tracks`;
            const source = String(item.source || '').trim().toLowerCase();
            const sourceId = String(item.sourceId || '').trim();
            const isActive = activeSource && activeSourceId && source === activeSource && sourceId === activeSourceId;
            const circuit = circuitBySource.get(source);
            const runMessage = String(item.lastRunMessage || '').trim();
            const consecutiveFailures = Number(item.consecutiveFailures || 0);
            const nextAttempt = item.nextAttemptUtc
                ? formatRelativeTime(item.nextAttemptUtc)
                : '';
            const presentationCounts = resolvePlaylistWatchlistPresentation(item);
            const statusLabel = formatPlaylistWatchlistRunStatus(item);
            const statusParts = [statusLabel];
            if (isActive) {
                statusParts.push('active');
            }
            if (circuit?.isOpen) {
                statusParts.push('source circuit open');
            }
            if (consecutiveFailures > 0) {
                statusParts.push(`failures ${consecutiveFailures}`);
            }
            if (nextAttempt) {
                statusParts.push(`next ${nextAttempt}`);
            }
            const statusMeta = statusParts.join(' • ');
            const circuitMeta = circuit?.isOpen
                ? [circuit.reason, circuit.openUntilUtc ? `until ${formatRelativeTime(circuit.openUntilUtc)}` : null]
                    .filter(Boolean)
                    .join(' • ')
                : '';
            return `<div class="watchlist-playlist-card-v2" draggable="true" data-playlist-source="${escapeHtml(item.source)}" data-playlist-id="${escapeHtml(item.sourceId)}" data-playlist-track-count="${escapeHtml(String(item.trackCount ?? ''))}">
                <button class="watchlist-card-art" type="button"
                    data-playlist-open="${escapeHtml(item.sourceId)}"
                    data-playlist-source="${escapeHtml(item.source)}">
                    ${artContent}
                    ${priorityBadge}
                    <div class="playlist-watchlist-presentation-slot">${presentationBadges}</div>
                </button>
                <div class="watchlist-action-menu watchlist-action-menu--hover">
                    <button class="watchlist-kebab-btn" type="button" title="Actions" data-playlist-menu-toggle="${escapeHtml(item.source)}" data-playlist-id="${escapeHtml(item.sourceId)}" aria-expanded="false">
                        <i class="fa-solid fa-ellipsis-vertical"></i>
                    </button>
                    <div class="watchlist-action-dropdown watchlist-action-dropdown--hover" data-playlist-menu="${escapeHtml(item.source)}" data-playlist-id="${escapeHtml(item.sourceId)}" hidden>
                        ${renderSharedPlaylistActions({
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
                    <div class="watchlist-card-meta">${escapeHtml(statusMeta)}</div>
                    ${runMessage ? `<div class="watchlist-card-meta">${escapeHtml(runMessage)}</div>` : ''}
                    ${circuitMeta ? `<div class="watchlist-card-meta">${escapeHtml(circuitMeta)}</div>` : ''}
                </div>
            </div>`;
        }).join('') + manualUnavailableCard;

        bindPlaylistWatchlistDragOrdering(container);

        container.querySelectorAll('[data-playlist-open]').forEach(button => {
            button.addEventListener('click', () => {
                const sourceId = button.dataset.playlistOpen;
                const source = button.dataset.playlistSource || 'deezer';
                navigateToPlaylistTracklist(source, sourceId);
            });
        });

        container.querySelectorAll('[data-manual-unavailable-open]').forEach(button => {
            button.addEventListener('click', () => {
                window.location.href = '/Tracklist?id=manual-unavailable&type=playlist&source=manual-unavailable';
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
                    const remainingCards = container.querySelectorAll('.watchlist-playlist-card-v2[data-playlist-source][data-playlist-id]').length;
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

        const bindPlaylistRuntimeAction = ({
            action,
            confirmMessage,
            confirmTitle,
            okText,
            endpointSuffix,
            successMessage,
            failurePrefix,
            resolveSuccessMessage
        }) => {
            container.querySelectorAll(`[data-playlist-action="${action}"]`).forEach(button => {
                button.addEventListener('click', async (event) => {
                    event.preventDefault();
                    event.stopPropagation();
                    const source = button.dataset.playlistSource;
                    const sourceId = button.dataset.playlistId;
                    if (!source || !sourceId) return;
                    try {
                        const confirmed = await confirmWithAppUi(
                            confirmMessage,
                            { title: confirmTitle, okText });
                        if (!confirmed) {
                            return;
                        }
                        button.disabled = true;
                        try {
                            const result = await fetchJson(`/api/library/playlists/${encodeURIComponent(source)}/${encodeURIComponent(sourceId)}/${endpointSuffix}`, { method: 'POST' });
                            showToast(resolveSuccessMessage ? resolveSuccessMessage(result) : successMessage);
                            await loadPlaylistWatchlist();
                        } catch (error) {
                            showToast(`${failurePrefix}: ${error.message}`, true);
                            button.disabled = false;
                        }
                    } catch (error) {
                        showToast(`Failed to open confirmation dialog: ${error?.message || 'Unknown error'}`, true);
                    }
                });
            });
        };

        bindPlaylistRuntimeAction({
            action: 'reset-runtime',
            confirmMessage: 'Reset runtime state for this playlist and trigger a fresh attempt?',
            confirmTitle: 'Reset Playlist Runtime',
            okText: 'Reset Runtime',
            endpointSuffix: 'reset-runtime',
            successMessage: 'Playlist runtime reset.',
            failurePrefix: 'Playlist runtime reset failed'
        });

        bindPlaylistRuntimeAction({
            action: 'reset-skip',
            confirmMessage: 'Reset this playlist and move scheduler focus to the next playlist?',
            confirmTitle: 'Reset and Skip',
            okText: 'Reset and Skip',
            endpointSuffix: 'reset-and-skip',
            failurePrefix: 'Reset and skip failed',
            resolveSuccessMessage: result => {
                if (result?.skipped && result?.nextSource && result?.nextSourceId) {
                    return `Reset done. Active moved to ${result.nextSource}:${result.nextSourceId}.`;
                }
                return 'Playlist reset done.';
            }
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
                const opened = await openSharedPlaylistArtworkPickerViaShared(source, sourceId, playlistName);
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
                button.disabled = true;
                try {
                    await openPlaylistSettingsPanel(source, sourceId, playlistName, playlistPrefsPromise);
                } catch (error) {
                    showToast(`Playlist settings failed to load: ${error?.message || 'Unknown error'}`, true);
                } finally {
                    button.disabled = false;
                }
            });
        });

        playlistPrefsPromise.then((playlistPrefs) => {
            tryOpenPendingPlaylistSettings(playlistPrefs);
        }).catch(() => {
            // Ignore preference hydration failures here; settings panel handles missing prefs.
        });
        container.dataset.stale = 'false';
        container.dataset.loadState = 'ready';
    } catch (error) {
        container.dataset.loadState = 'error';
        container.innerHTML = `<div class="watchlist-empty-state">Failed to load playlists: ${escapeHtml(error?.message || 'Unknown error')}</div>`;
    }
}

globalThis.loadPlaylistWatchlist = loadPlaylistWatchlist;

function renderManualUnavailablePlaylistCard(tracks, imageUrl) {
    const count = Array.isArray(tracks) ? tracks.length : 0;
    if (count <= 0) {
        return '';
    }

    const safeImageUrl = toSafeHttpUrl(imageUrl || '/images/unavailable/unavailable.jpg') || '/images/unavailable/unavailable.jpg';
    const trackLabel = `${count} unavailable track${count === 1 ? '' : 's'}`;
    return `<div class="watchlist-playlist-card-v2 watchlist-playlist-card-v2--manual-unavailable" data-manual-unavailable-card="true">
        <button class="watchlist-card-art" type="button" data-manual-unavailable-open="true">
            <img src="${escapeHtml(safeImageUrl)}" alt="Unavailable Tracks" />
            <div class="playlist-watchlist-presentation-slot">
                <span class="playlist-watchlist-state-badge playlist-watchlist-state-badge--unavailable" title="${escapeHtml(trackLabel)}"><i class="fa-solid fa-triangle-exclamation"></i></span>
            </div>
        </button>
        <div class="watchlist-card-strip">
            <div class="watchlist-card-name">Unavailable Tracks</div>
            <div class="watchlist-card-meta">${escapeHtml(trackLabel)}</div>
            <div class="watchlist-card-meta">Manual downloads only</div>
        </div>
    </div>`;
}

function bindPlaylistWatchlistTabHydration() {
    const watchlistTab = document.getElementById('watchlist-tab');
    const playlistSubTab = document.getElementById('watchlist-playlists-tab');
    const playlistPane = document.getElementById('watchlist-playlists-content');
    const blockedSubTab = document.getElementById('watchlist-blocked-tab');
    const blockedPane = document.getElementById('watchlist-blocked-content');
    if (!playlistPane || playlistPane.dataset.playlistHydrationBound === 'true') {
        return;
    }

    const isWatchlistParentActive = () => {
        const watchlistPane = document.getElementById('watchlist-content');
        return watchlistPane
            ? watchlistPane.classList.contains('active') || watchlistPane.classList.contains('show')
            : watchlistTab?.classList.contains('active') === true;
    };
    const isPaneActive = (pane) => pane
        && (pane.classList.contains('active') || pane.classList.contains('show'));
    const shouldLoadContainer = (container) => {
        if (!container || container.dataset.loadState === 'loading') {
            return false;
        }

        const hasRenderableContent = container.childElementCount > 0 || container.textContent.trim().length > 0;
        return container.dataset.loadState !== 'ready'
            || container.dataset.stale === 'true'
            || !hasRenderableContent;
    };
    const ensurePlaylistWatchlistLoaded = () => {
        if (!isWatchlistParentActive() || !isPaneActive(playlistPane)) {
            return;
        }

        const container = document.getElementById('playlistWatchlistContainer');
        if (!shouldLoadContainer(container)) {
            return;
        }

        container.dataset.stale = 'false';
        void loadPlaylistWatchlist();
    };
    const ensureBlockedWatchlistLoaded = () => {
        if (!isWatchlistParentActive() || !isPaneActive(blockedPane)) {
            return;
        }

        const container = document.getElementById('blockedWatchlistContainer');
        if (!shouldLoadContainer(container)) {
            return;
        }

        container.dataset.stale = 'false';
        void loadPlaylistBlockedRules();
    };

    const ensureActiveWatchlistSubTabLoaded = () => {
        ensurePlaylistWatchlistLoaded();
        ensureBlockedWatchlistLoaded();
    };

    watchlistTab?.addEventListener('shown.bs.tab', ensureActiveWatchlistSubTabLoaded);
    playlistSubTab?.addEventListener('shown.bs.tab', ensurePlaylistWatchlistLoaded);
    blockedSubTab?.addEventListener('shown.bs.tab', ensureBlockedWatchlistLoaded);

    ensureActiveWatchlistSubTabLoaded();

    playlistPane.dataset.playlistHydrationBound = 'true';
    if (blockedPane) {
        blockedPane.dataset.blockedHydrationBound = 'true';
    }
}

document.addEventListener('DOMContentLoaded', () => {
    bindPlaylistWatchlistRealtimeRefresh();
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
            openPlaylistSettingsPanel(pendingSource, pendingSourceId, pendingName, playlistPrefs)
                .catch(error => showToast(`Playlist settings failed to load: ${error?.message || 'Unknown error'}`, true));
        }, 0);
    } catch {
    }
}

function buildMergeSourceSection(items) {
    const sourceSection = document.createElement('div');
    sourceSection.className = 'playlist-settings-section';
    sourceSection.innerHTML = '<div class="playlist-settings-section-title">Playlists to merge</div>';
    const sourceList = document.createElement('div');
    sourceList.className = 'routing-rules-list merge-source-list';
    items.forEach((item, index) => {
        const row = document.createElement('label');
        row.className = 'merge-source-row';
        const checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.className = 'form-check-input';
        checkbox.dataset.mergeSource = item.source || '';
        checkbox.dataset.mergeSourceId = item.sourceId || '';
        checkbox.checked = index < 2;
        const sourceLabelWrap = document.createElement('div');
        sourceLabelWrap.className = 'merge-source-label-wrap';
        const sourceName = document.createElement('div');
        sourceName.className = 'merge-source-name';
        sourceName.textContent = item.name || 'Playlist';
        const sourceMeta = document.createElement('div');
        sourceMeta.className = 'merge-source-meta';
        sourceMeta.textContent = String(item.source || '').toUpperCase();
        sourceLabelWrap.appendChild(sourceName);
        sourceLabelWrap.appendChild(sourceMeta);
        row.appendChild(checkbox);
        row.appendChild(sourceLabelWrap);
        sourceList.appendChild(row);
    });
    sourceSection.appendChild(sourceList);
    return { sourceSection, sourceList };
}

function buildMergeTextSection(title, element, hintText) {
    const section = document.createElement('div');
    section.className = 'playlist-settings-section';
    section.innerHTML = `<div class="playlist-settings-section-title">${title}</div>`;
    section.appendChild(element);
    if (hintText) {
        const hint = document.createElement('div');
        hint.className = 'playlist-settings-section-label';
        hint.textContent = hintText;
        section.appendChild(hint);
    }
    return section;
}

function buildMergeArtworkSection(items) {
    const section = document.createElement('div');
    section.className = 'playlist-settings-section merge-artwork-section';
    section.innerHTML = '<div class="playlist-settings-section-title">Merged playlist artwork</div>';

    const state = {
        selectedMode: 'source',
        selectedKey: '',
        uploadDataUrl: null,
        uploadFileName: '',
        options: new Map(),
        optionInputs: [],
        uploadInput: null
    };

    const sourceGrid = document.createElement('div');
    sourceGrid.className = 'merge-artwork-grid';

    items.forEach(item => {
        const imageUrl = String(item?.imageUrl || '').trim();
        if (!isManagedPlaylistVisualUrl(imageUrl)) {
            return;
        }

        const key = buildMergeArtworkKey(item);
        if (!key || state.options.has(key)) {
            return;
        }

        const option = {
            key,
            source: String(item.source || '').trim(),
            sourceId: String(item.sourceId || '').trim(),
            imageUrl,
            name: String(item.name || 'Playlist').trim()
        };
        state.options.set(key, option);

        const label = document.createElement('label');
        label.className = 'merge-artwork-option';
        label.dataset.mergeArtworkKey = key;

        const radio = document.createElement('input');
        radio.type = 'radio';
        radio.name = 'mergeArtworkChoice';
        radio.className = 'form-check-input';
        radio.value = key;
        radio.addEventListener('change', () => {
            if (radio.checked) {
                state.selectedMode = 'source';
                state.selectedKey = key;
            }
        });

        const image = document.createElement('img');
        image.src = imageUrl;
        image.alt = option.name;
        image.loading = 'lazy';
        image.decoding = 'async';

        const caption = document.createElement('span');
        caption.textContent = option.name;

        label.appendChild(radio);
        label.appendChild(image);
        label.appendChild(caption);
        sourceGrid.appendChild(label);
        state.optionInputs.push(radio);

        if (!state.selectedKey) {
            state.selectedKey = key;
            radio.checked = true;
        }
    });

    if (state.options.size > 0) {
        section.appendChild(sourceGrid);
    }

    const uploadWrap = document.createElement('div');
    uploadWrap.className = 'merge-artwork-upload';

    const uploadLabel = document.createElement('label');
    uploadLabel.className = 'merge-target-row';
    const uploadRadio = document.createElement('input');
    uploadRadio.type = 'radio';
    uploadRadio.name = 'mergeArtworkChoice';
    uploadRadio.className = 'form-check-input';
    uploadRadio.addEventListener('change', () => {
        if (uploadRadio.checked) {
            state.selectedMode = 'upload';
        }
    });
    const uploadText = document.createElement('div');
    uploadText.className = 'merge-target-label';
    uploadText.textContent = 'Use custom artwork from this device';
    uploadLabel.appendChild(uploadRadio);
    uploadLabel.appendChild(uploadText);

    const fileInput = document.createElement('input');
    fileInput.type = 'file';
    fileInput.accept = 'image/jpeg,image/png,image/webp,image/gif';
    fileInput.className = 'form-control';
    state.uploadInput = fileInput;

    const preview = document.createElement('div');
    preview.className = 'merge-artwork-upload-preview';
    preview.textContent = 'No custom artwork selected.';

    fileInput.addEventListener('change', () => {
        const file = fileInput.files && fileInput.files[0] ? fileInput.files[0] : null;
        if (!file) {
            state.uploadDataUrl = null;
            state.uploadFileName = '';
            preview.textContent = 'No custom artwork selected.';
            return;
        }

        if (!file.type || !file.type.startsWith('image/')) {
            showToast('Select a valid image file for merged playlist artwork.', true);
            fileInput.value = '';
            return;
        }

        if (file.size > 8 * 1024 * 1024) {
            showToast('Merged playlist artwork must be 8 MB or smaller.', true);
            fileInput.value = '';
            return;
        }

        const reader = new FileReader();
        reader.onload = () => {
            state.uploadDataUrl = typeof reader.result === 'string' ? reader.result : null;
            state.uploadFileName = file.name || '';
            state.selectedMode = 'upload';
            uploadRadio.checked = true;
            preview.innerHTML = state.uploadDataUrl
                ? `<img src="${escapeHtml(state.uploadDataUrl)}" alt="${escapeHtml(state.uploadFileName || 'Custom artwork')}"><span>${escapeHtml(state.uploadFileName || 'Custom artwork')}</span>`
                : 'No custom artwork selected.';
        };
        reader.onerror = () => {
            state.uploadDataUrl = null;
            state.uploadFileName = '';
            showToast('Failed to read selected artwork file.', true);
        };
        reader.readAsDataURL(file);
    });

    uploadWrap.appendChild(uploadLabel);
    uploadWrap.appendChild(fileInput);
    uploadWrap.appendChild(preview);
    section.appendChild(uploadWrap);

    if (state.options.size === 0) {
        state.selectedMode = 'upload';
        uploadRadio.checked = true;
    }

    return { artworkSection: section, artworkState: state };
}

function buildMergeArtworkKey(item) {
    const source = String(item?.source || '').trim();
    const sourceId = String(item?.sourceId || '').trim();
    return source && sourceId ? `${source}:${sourceId}` : '';
}

function isManagedPlaylistVisualUrl(value) {
    const url = String(value || '').trim().toLowerCase();
    return url.includes('/api/library/playlists/') && url.includes('/visual');
}

function syncMergeArtworkAvailability(sourceList, artworkState) {
    if (!artworkState || !sourceList) {
        return;
    }

    const selectedKeys = new Set(Array.from(sourceList.querySelectorAll('input[type="checkbox"]:checked'))
        .map(input => `${String(input.dataset.mergeSource || '').trim()}:${String(input.dataset.mergeSourceId || '').trim()}`));
    let firstEnabledInput = null;
    artworkState.optionInputs.forEach(input => {
        const enabled = selectedKeys.has(input.value);
        input.disabled = !enabled;
        input.closest('.merge-artwork-option')?.classList.toggle('is-disabled', !enabled);
        if (enabled && !firstEnabledInput) {
            firstEnabledInput = input;
        }
    });

    if (artworkState.selectedMode === 'source' && !selectedKeys.has(artworkState.selectedKey)) {
        if (firstEnabledInput) {
            firstEnabledInput.checked = true;
            artworkState.selectedKey = firstEnabledInput.value;
        } else {
            artworkState.selectedKey = '';
            artworkState.selectedMode = 'upload';
            if (artworkState.uploadInput) {
                const uploadRadio = artworkState.uploadInput
                    .closest('.merge-artwork-upload')
                    ?.querySelector('input[type="radio"]');
                if (uploadRadio) {
                    uploadRadio.checked = true;
                }
            }
        }
    }
}

function buildMergeTargetRow(label, checked) {
    const row = document.createElement('label');
    row.className = 'merge-target-row';
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.className = 'form-check-input';
    checkbox.checked = checked;
    const text = document.createElement('div');
    text.className = 'merge-target-label';
    text.textContent = label;
    row.appendChild(checkbox);
    row.appendChild(text);
    return { row, checkbox };
}

function createMergeTargetPlaylistLoader() {
    const cache = new Map();
    return async function loadTargetPlaylistOptions(target, selectElement) {
        const normalizedTarget = String(target || '').trim().toLowerCase();
        if (!normalizedTarget) {
            return;
        }

        if (!cache.has(normalizedTarget)) {
            const items = await fetchJson(`/api/library/playlists/merge-target-playlists?target=${encodeURIComponent(normalizedTarget)}`);
            cache.set(normalizedTarget, Array.isArray(items) ? items : []);
        }

        const options = cache.get(normalizedTarget) || [];
        const defaultLabel = `Select existing ${
            normalizedTarget === 'plex'
                ? 'Plex'
                : normalizedTarget === 'jellyfin'
                    ? 'Jellyfin'
                    : 'Navidrome'
        } playlist`;
        selectElement.innerHTML = `<option value="">${defaultLabel}</option>`;
        options.forEach(item => appendMergeTargetPlaylistOption(selectElement, item));
    };
}

function appendMergeTargetPlaylistOption(selectElement, item) {
    const option = document.createElement('option');
    option.value = String(item.id || '').trim();
    if (!option.value) {
        return;
    }

    const count = Number.isFinite(Number(item.trackCount)) ? ` (${Number(item.trackCount)} tracks)` : '';
    option.textContent = `${String(item.name || option.value)}${count}`;
    selectElement.appendChild(option);
}

async function refreshExistingTargetPlaylistControls(controls, loadTargetPlaylistOptions) {
    const useExisting = controls.useExistingCheck.checked;
    const allowPlex = useExisting && controls.plexCheck.checked;
    const allowJellyfin = useExisting && controls.jellyfinCheck.checked;
    const allowNavidrome = useExisting && controls.navidromeCheck.checked;

    controls.plexExistingSelect.hidden = !allowPlex;
    controls.jellyfinExistingSelect.hidden = !allowJellyfin;
    controls.navidromeExistingSelect.hidden = !allowNavidrome;
    controls.plexExistingSelect.disabled = !allowPlex;
    controls.jellyfinExistingSelect.disabled = !allowJellyfin;
    controls.navidromeExistingSelect.disabled = !allowNavidrome;

    await refreshMergeTargetSelect('plex', allowPlex, controls.plexExistingSelect, loadTargetPlaylistOptions);
    await refreshMergeTargetSelect('jellyfin', allowJellyfin, controls.jellyfinExistingSelect, loadTargetPlaylistOptions);
    await refreshMergeTargetSelect('navidrome', allowNavidrome, controls.navidromeExistingSelect, loadTargetPlaylistOptions);
}

async function refreshMergeTargetSelect(target, enabled, selectElement, loadTargetPlaylistOptions) {
    if (enabled) {
        await loadTargetPlaylistOptions(target, selectElement);
        return;
    }

    selectElement.value = '';
}

function collectSelectedMergePlaylists(sourceList) {
    return Array.from(sourceList.querySelectorAll('input[type="checkbox"]:checked'))
        .map(input => ({
            source: String(input.dataset.mergeSource || '').trim(),
            sourceId: String(input.dataset.mergeSourceId || '').trim()
        }))
        .filter(item => item.source && item.sourceId);
}

function validateMergeSelection(selectedPlaylists, controls, artworkState) {
    if (selectedPlaylists.length < 2) {
        showToast('Select at least two monitored playlists to merge.', true);
        return false;
    }

    if (!controls.plexCheck.checked && !controls.jellyfinCheck.checked && !controls.navidromeCheck.checked) {
        showToast('Select at least one merge target (Plex, Jellyfin, or Navidrome).', true);
        return false;
    }

    if (!controls.useExistingCheck.checked) {
        return true;
    }

    if (controls.plexCheck.checked && !controls.plexExistingSelect.value) {
        showToast('Select an existing Plex playlist.', true);
        return false;
    }

    if (controls.jellyfinCheck.checked && !controls.jellyfinExistingSelect.value) {
        showToast('Select an existing Jellyfin playlist.', true);
        return false;
    }

    if (controls.navidromeCheck.checked && !controls.navidromeExistingSelect.value) {
        showToast('Select an existing Navidrome playlist.', true);
        return false;
    }

    if (artworkState?.selectedMode === 'upload' && !artworkState.uploadDataUrl) {
        showToast('Select a custom artwork image or choose one of the playlist covers.', true);
        return false;
    }

    return true;
}

function buildMergePayload(selectedPlaylists, inputs, controls, artworkState) {
    const selectedArtwork = artworkState?.selectedMode === 'source'
        ? artworkState.options.get(artworkState.selectedKey)
        : null;
    return {
        playlists: selectedPlaylists,
        name: String(inputs.nameInput.value || '').trim(),
        description: String(inputs.descriptionInput.value || '').trim(),
        artworkDataUrl: artworkState?.selectedMode === 'upload' ? artworkState.uploadDataUrl : null,
        artworkSource: selectedArtwork ? selectedArtwork.source : null,
        artworkSourceId: selectedArtwork ? selectedArtwork.sourceId : null,
        syncMode: inputs.syncModeSelect.value || 'mirror',
        syncToPlex: controls.plexCheck.checked,
        syncToJellyfin: controls.jellyfinCheck.checked,
        syncToNavidrome: controls.navidromeCheck.checked,
        existingPlexPlaylistId: controls.useExistingCheck.checked && controls.plexCheck.checked
            ? String(controls.plexExistingSelect.value || '').trim()
            : null,
        existingJellyfinPlaylistId: controls.useExistingCheck.checked && controls.jellyfinCheck.checked
            ? String(controls.jellyfinExistingSelect.value || '').trim()
            : null,
        existingNavidromePlaylistId: controls.useExistingCheck.checked && controls.navidromeCheck.checked
            ? String(controls.navidromeExistingSelect.value || '').trim()
            : null
    };
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
    panel.className = 'playlist-settings-panel watchlist-playlist-settings';

    const { sourceSection, sourceList } = buildMergeSourceSection(items);
    panel.appendChild(sourceSection);

    const { artworkSection, artworkState } = buildMergeArtworkSection(items);
    panel.appendChild(artworkSection);
    sourceList.addEventListener('change', () => syncMergeArtworkAvailability(sourceList, artworkState));
    syncMergeArtworkAvailability(sourceList, artworkState);

    const nameInput = document.createElement('input');
    nameInput.className = 'form-control';
    nameInput.type = 'text';
    nameInput.maxLength = 200;
    nameInput.value = 'Merged Monitored Playlist';
    panel.appendChild(buildMergeTextSection('Merged playlist name', nameInput));

    const descriptionInput = document.createElement('textarea');
    descriptionInput.className = 'form-control';
    descriptionInput.rows = 3;
    descriptionInput.placeholder = 'Write a custom description for the merged playlist.';
    panel.appendChild(buildMergeTextSection(
        'Description',
        descriptionInput,
        'Source attribution will include your DeezSpoTag username.'));

    const targetSection = document.createElement('div');
    targetSection.className = 'playlist-settings-section';
    targetSection.innerHTML = '<div class="playlist-settings-section-title">Sync targets</div>';
    const targetList = document.createElement('div');
    targetList.className = 'routing-rules-list merge-target-list';
    const { row: plexRow, checkbox: plexCheck } = buildMergeTargetRow('Plex', true);
    const { row: jellyfinRow, checkbox: jellyfinCheck } = buildMergeTargetRow('Jellyfin', false);
    const { row: navidromeRow, checkbox: navidromeCheck } = buildMergeTargetRow('Navidrome', false);
    targetList.appendChild(plexRow);
    targetList.appendChild(jellyfinRow);
    targetList.appendChild(navidromeRow);
    targetSection.appendChild(targetList);
    panel.appendChild(targetSection);

    const existingTargetSection = document.createElement('div');
    existingTargetSection.className = 'playlist-settings-section';
    existingTargetSection.innerHTML = '<div class="playlist-settings-section-title">Target playlist option</div>';
    const useExistingWrap = document.createElement('label');
    useExistingWrap.className = 'merge-target-row';
    const useExistingCheck = document.createElement('input');
    useExistingCheck.type = 'checkbox';
    useExistingCheck.className = 'form-check-input';
    const useExistingText = document.createElement('div');
    useExistingText.className = 'merge-target-label';
    useExistingText.textContent = 'Merge into existing playlist on target server';
    useExistingWrap.appendChild(useExistingCheck);
    useExistingWrap.appendChild(useExistingText);
    existingTargetSection.appendChild(useExistingWrap);

    const plexExistingSelect = document.createElement('select');
    plexExistingSelect.className = 'form-select';
    plexExistingSelect.disabled = true;
    plexExistingSelect.hidden = true;
    plexExistingSelect.innerHTML = '<option value="">Select existing Plex playlist</option>';
    existingTargetSection.appendChild(plexExistingSelect);

    const jellyfinExistingSelect = document.createElement('select');
    jellyfinExistingSelect.className = 'form-select';
    jellyfinExistingSelect.disabled = true;
    jellyfinExistingSelect.hidden = true;
    jellyfinExistingSelect.innerHTML = '<option value="">Select existing Jellyfin playlist</option>';
    existingTargetSection.appendChild(jellyfinExistingSelect);

    const navidromeExistingSelect = document.createElement('select');
    navidromeExistingSelect.className = 'form-select';
    navidromeExistingSelect.disabled = true;
    navidromeExistingSelect.hidden = true;
    navidromeExistingSelect.innerHTML = '<option value="">Select existing Navidrome playlist</option>';
    existingTargetSection.appendChild(navidromeExistingSelect);

    panel.appendChild(existingTargetSection);

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

    const mergeControls = {
        useExistingCheck,
        plexCheck,
        jellyfinCheck,
        navidromeCheck,
        plexExistingSelect,
        jellyfinExistingSelect,
        navidromeExistingSelect
    };
    const loadTargetPlaylistOptions = createMergeTargetPlaylistLoader();

    useExistingCheck.addEventListener('change', async () => {
        try {
            await refreshExistingTargetPlaylistControls(mergeControls, loadTargetPlaylistOptions);
        } catch (error) {
            showToast(`Failed to load target playlists: ${error?.message || 'Unknown error'}`, true);
            useExistingCheck.checked = false;
            await refreshExistingTargetPlaylistControls(mergeControls, loadTargetPlaylistOptions);
        }
    });
    plexCheck.addEventListener('change', () => {
        void refreshExistingTargetPlaylistControls(mergeControls, loadTargetPlaylistOptions);
    });
    jellyfinCheck.addEventListener('change', () => {
        void refreshExistingTargetPlaylistControls(mergeControls, loadTargetPlaylistOptions);
    });
    navidromeCheck.addEventListener('change', () => {
        void refreshExistingTargetPlaylistControls(mergeControls, loadTargetPlaylistOptions);
    });

    const confirmed = await globalThis.DeezSpoTag.ui.showModal({
        title: 'Merge Monitored Playlists',
        message: '',
        allowHtml: false,
        dialogClass: 'is-resizable playlist-settings-modal',
        contentElement: panel,
        buttons: [
            { label: 'Merge & Sync', value: 'merge', primary: true },
            { label: 'Cancel', value: 'cancel' }
        ]
    });
    if (confirmed?.value !== 'merge') {
        return;
    }

    const selectedPlaylists = collectSelectedMergePlaylists(sourceList);
    if (!validateMergeSelection(selectedPlaylists, mergeControls, artworkState)) {
        return;
    }

    const payload = buildMergePayload(
        selectedPlaylists,
        { nameInput, descriptionInput, syncModeSelect },
        mergeControls,
        artworkState);

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

async function ensurePlaylistSettingsFoldersLoaded() {
    if (!Array.isArray(libraryState.folders) || libraryState.folders.length === 0) {
        try {
            const folders = await fetchJson('/api/library/folders?downloadOnly=true&contentType=music');
            libraryState.folders = Array.isArray(folders)
                ? folders.map(normalizeFolderConversionState)
                : [];
        } catch {
            libraryState.folders = Array.isArray(libraryState.folders) ? libraryState.folders : [];
        }
    }
}

function buildPlaylistTrackCandidateIndexes(trackCandidatesResponse) {
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

    trackCandidates.forEach(candidate => {
        const normalizedCandidate = normalizePlaylistTrackCandidate(candidate);
        if (!normalizedCandidate || trackCandidateMap.has(normalizedCandidate.trackSourceId)) {
            return;
        }

        trackCandidateMap.set(normalizedCandidate.trackSourceId, normalizedCandidate);
        addRoutingValue(routingValueIndex, 'artist', normalizedCandidate.artist);
        addRoutingValue(routingValueIndex, 'title', normalizedCandidate.title);
        addRoutingValue(routingValueIndex, 'album', normalizedCandidate.album);
        if (Number.isInteger(normalizedCandidate.releaseYear)) {
            addRoutingValue(routingValueIndex, 'year', String(normalizedCandidate.releaseYear));
        }
        normalizedCandidate.genres.forEach(genre => addRoutingValue(routingValueIndex, 'genre', genre));

        if (normalizedCandidate.explicit === true) {
            explicitModesAvailable.add('is_true');
        } else if (normalizedCandidate.explicit === false) {
            explicitModesAvailable.add('is_false');
        }
    });

    return { trackCandidateMap, routingValueIndex, explicitModesAvailable };
}

function normalizePlaylistTrackCandidate(candidate) {
    const trackSourceId = String(candidate?.trackSourceId || '').trim();
    if (!trackSourceId) {
        return null;
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

    return {
        trackSourceId,
        isrc: String(candidate?.isrc || '').trim(),
        title: String(candidate?.title || '').trim(),
        artist: String(candidate?.artist || '').trim(),
        album: String(candidate?.album || '').trim(),
        releaseYear,
        explicit,
        genres: genresRaw
            .map(value => String(value || '').trim())
            .filter(Boolean)
    };
}

function addRoutingValue(routingValueIndex, field, rawValue) {
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

async function openPlaylistSettingsPanel(source, sourceId, playlistName, playlistPrefs) {
    await ensurePlaylistSettingsFoldersLoaded();
    const downloadSourceCatalog = await getWatchlistDownloadSourceCatalog();
    const downloadSourceOptions = [
        { value: '', label: 'Follow global download source' },
        ...downloadSourceCatalog.options
    ];

    const enabledFolders = (libraryState.folders || []).filter(isMusicRecommendationEligibleFolder);

    const prefKey = `${source}:${sourceId}`;
    const resolvedPlaylistPrefs = playlistPrefs && typeof playlistPrefs.then === 'function'
        ? await playlistPrefs
        : playlistPrefs;
    const serverPrefs = normalizePlaylistPreferenceMap(
        resolvedPlaylistPrefs && typeof resolvedPlaylistPrefs === 'object'
            ? resolvedPlaylistPrefs
            : await hydrateSinglePlaylistPreference(source, sourceId));
    const localPlaylistPrefs = getStoredPreferences('playlistWatchlist');
    const stored = {
        ...serverPrefs[prefKey],
        ...localPlaylistPrefs[prefKey]
    };

    const trackCandidatesPromise = fetchJson(
        `/api/library/playlists/${encodeURIComponent(source)}/${encodeURIComponent(sourceId)}/tracks`)
        .catch(() => null);
    const [existingRules, existingBlockRules] = await Promise.all([
        fetchJson(`/api/library/playlists/${encodeURIComponent(source)}/${encodeURIComponent(sourceId)}/routing-rules`).catch(() => []),
        fetchJson(`/api/library/playlists/${encodeURIComponent(source)}/${encodeURIComponent(sourceId)}/ignore-rules`).catch(() => [])
    ]);

    const panel = document.createElement('div');
    panel.className = 'playlist-settings-panel watchlist-playlist-settings';

    const panelIntro = document.createElement('div');
    panelIntro.className = 'playlist-settings-intro';
    panelIntro.textContent = 'Tune sync behavior, route matching tracks to folders, and block tracks you do not want synced.';
    panel.appendChild(panelIntro);

    const { routingValueIndex, explicitModesAvailable } =
        buildPlaylistTrackCandidateIndexes([]);

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

    panel.appendChild(createPlaylistSyncTargetsSection(source, sourceId, stored.syncTargets));

    const playlistEngine = createPlaylistSettingsSelectSection({
        title: 'Download engine',
        selectClass: 'ps-engine-select',
        selectId: `ps-engine-${source}-${sourceId}`,
        options: downloadSourceOptions,
        value: '',
        helpText: 'Exact-match watchlist mapping pins to Deezer when a Deezer ID/ISRC match is found.'
    });
    const engineSelect = playlistEngine.select;
    panel.appendChild(playlistEngine.section);
    const customEngineOrder = createWatchlistDownloadEngineOrderSection(
        stored.downloadEngineOrder,
        downloadSourceCatalog.defaultDownloadEngineOrder);
    panel.appendChild(customEngineOrder.section);

    const playlistDownloadMode = createPlaylistSettingsSelectSection({
        title: 'Download mode',
        selectClass: 'ps-download-mode-select',
        selectId: `ps-download-mode-${source}-${sourceId}`,
        options: playlistDownloadModeOptions,
        value: 'standard'
    });
    const downloadModeSection = playlistDownloadMode.section;
    const downloadModeSelect = playlistDownloadMode.select;
    panel.appendChild(downloadModeSection);

    const syncAtmosFolderVisibility = () => {
        const selectedMode = String(downloadModeSelect?.value || 'standard').trim().toLowerCase();
        const hasPlaylistAtmosMode = selectedMode === 'dual_quality' || selectedMode === 'atmos_only';
        const shouldShowAtmosFolder = hasPlaylistAtmosMode;

        atmosFolderSection.hidden = !shouldShowAtmosFolder;
        atmosFolderSelect.disabled = !shouldShowAtmosFolder;
        if (hasPlaylistAtmosMode) {
            atmosFolderHint.textContent = 'Used when monitored playlist download mode includes Atmos.';
        } else {
            atmosFolderHint.textContent = 'Used when download mode includes Atmos (Dual quality or Atmos only).';
        }
    };
    const syncCustomEngineOrderVisibility = () => {
        customEngineOrder.section.hidden = String(engineSelect?.value || '').trim().toLowerCase() !== 'custom';
    };
    engineSelect?.addEventListener('change', syncCustomEngineOrderVisibility);
    syncCustomEngineOrderVisibility();

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
    const applyGlobalBtn = document.createElement('button');
    applyGlobalBtn.className = 'btn btn-secondary action-btn btn-sm routing-rules-apply-global-btn';
    applyGlobalBtn.type = 'button';
    applyGlobalBtn.textContent = 'Apply globally';
    applyGlobalBtn.addEventListener('click', async () => {
        const rules = collectPlaylistRoutingRules(rulesList);
        try {
            applyGlobalBtn.disabled = true;
            const result = await fetchJson(`/api/library/playlists/${encodeURIComponent(source)}/${encodeURIComponent(sourceId)}/routing-rules/apply-globally`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(rules)
            });
            const updated = Number(result?.playlistsUpdated || 0);
            showToast(`Applied globally to ${updated} monitored playlist${updated === 1 ? '' : 's'}.`);
        } catch (error) {
            showToast(`Apply globally failed: ${error.message}`, true);
        } finally {
            applyGlobalBtn.disabled = false;
        }
    });

    const rulesActions = document.createElement('div');
    rulesActions.className = 'routing-rules-actions';
    rulesActions.appendChild(addRuleBtn);
    rulesActions.appendChild(applyGlobalBtn);

    rulesSection.appendChild(rulesHeader);
    rulesSection.appendChild(rulesHint);
    rulesSection.appendChild(rulesColumns);
    rulesSection.appendChild(rulesList);
    rulesSection.appendChild(rulesEmpty);
    rulesSection.appendChild(rulesActions);
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
        const operatorOpts = resolveRoutingOperatorOptions(currentField)
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
            getOps: resolveRoutingOperatorOptions,
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
        if (engineSel) engineSel.value = stored.preferredEngine || '';
        syncCustomEngineOrderVisibility();
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

    void trackCandidatesPromise.then(trackCandidatesResponse => {
        const hydratedIndexes = buildPlaylistTrackCandidateIndexes(trackCandidatesResponse);
        const hydratedValues = {
            artist: Array.from(hydratedIndexes.routingValueIndex.artist.values()).sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' })),
            title: Array.from(hydratedIndexes.routingValueIndex.title.values()).sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' })),
            album: Array.from(hydratedIndexes.routingValueIndex.album.values()).sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' })),
            genre: Array.from(hydratedIndexes.routingValueIndex.genre.values()).sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' })),
            year: Array.from(hydratedIndexes.routingValueIndex.year.values()).sort((a, b) => Number(b) - Number(a))
        };
        Object.entries(hydratedValues).forEach(([field, values]) => {
            routingFieldValues[field] = values;
        });
        explicitModesAvailable.clear();
        hydratedIndexes.explicitModesAvailable.forEach(mode => explicitModesAvailable.add(mode));
        panel.querySelectorAll('.rr-field, .br-field').forEach(fieldSelect => {
            fieldSelect.dispatchEvent(new Event('change'));
        });
    });

    const settingsResult = await globalThis.DeezSpoTag.ui.showModal({
        title: `Settings — ${playlistName}`,
        message: '',
        allowHtml: false,
        dialogClass: 'is-resizable playlist-settings-modal',
        contentElement: panel,
        buttons: [
            {
                label: 'Save',
                value: 'save',
                primary: true,
                busyLabel: 'Saving...',
                onClick: () => savePlaylistSettingsFromPanel({
                    panel,
                    source,
                    sourceId,
                    playlistPrefs: resolvedPlaylistPrefs,
                    prefKey,
                    rulesList,
                    blockRulesList
                })
            },
            { label: 'Cancel', value: 'cancel' }
        ]
    });

    if (settingsResult?.value === 'save') {
        void refreshPlaylistSettingsViewsAfterSave();
    }
}

async function refreshPlaylistSettingsViewsAfterSave() {
    const blockedPane = document.getElementById('watchlist-blocked-content');
    const blockedPaneVisible = blockedPane
        && (blockedPane.classList.contains('active') || blockedPane.classList.contains('show'));
    if (!blockedPaneVisible) {
        return;
    }

    try {
        await loadPlaylistBlockedRules();
    } catch (error) {
        console.warn('Failed to refresh monitored playlist blocked rules after settings save.', error);
    }
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
                syncTargets: values.syncTargets,
                preferredEngine: values.preferredEngine,
                downloadEngineOrder: values.downloadEngineOrder,
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
            syncTargets: values.syncTargets,
            preferredEngine: values.preferredEngine,
            downloadEngineOrder: values.downloadEngineOrder,
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
        return true;
    } catch (error) {
        showToast(`Save failed: ${error.message}`, true);
        return false;
    }
}

function collectPlaylistSettingsValues(panel) {
    const folderSel = panel.querySelector('.ps-folder-select');
    const atmosFolderSel = panel.querySelector('.ps-atmos-folder-select');
    const syncTargets = collectPlaylistSyncTargetsFromPanel(panel);
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
        service: getPrimaryPlaylistSyncTarget(syncTargets),
        syncTargets,
        preferredEngine: engineSel?.value || '',
        downloadEngineOrder: String(engineSel?.value || '').trim().toLowerCase() === 'custom'
            ? collectWatchlistDownloadEngineOrder(panel)
            : null,
        downloadVariantMode: downloadModeSel?.value || 'standard',
        syncMode: syncModeSel?.value || 'mirror',
        updateArtwork: normalizedArtwork.updateArtwork,
        reuseSavedArtwork: normalizedArtwork.reuseSavedArtwork
    };
}

function collectPlaylistRoutingRules(rulesList) {
    return collectPlaylistRuleRows(rulesList, {
        rowSelector: '.routing-rule-row',
        fieldSelector: '.rr-field',
        operatorSelector: '.rr-operator',
        explicitSelector: '.rr-value-explicit',
        valueSelector: '.rr-value-choice',
        folderSelector: '.rr-folder',
        requireFolder: true
    });
}

function collectPlaylistBlockRules(blockRulesList) {
    return collectPlaylistRuleRows(blockRulesList, {
        rowSelector: '.block-rule-row',
        fieldSelector: '.br-field',
        operatorSelector: '.br-operator',
        explicitSelector: '.br-value-explicit',
        valueSelector: '.br-value-choice',
        requireFolder: false
    });
}

function collectPlaylistRuleRows(container, options) {
    const rules = [];
    container.querySelectorAll(options.rowSelector).forEach((row, idx) => {
        const field = row.querySelector(options.fieldSelector)?.value || 'artist';
        const explicitValue = row.querySelector(options.explicitSelector)?.value || 'is_true';
        let operator = row.querySelector(options.operatorSelector)?.value || 'contains';
        if (field === 'explicit') {
            operator = explicitValue === 'is_false' ? 'is_false' : 'is_true';
        }

        const value = field === 'explicit'
            ? ''
            : (row.querySelector(options.valueSelector)?.value || '').trim();
        const ruleFolder = options.folderSelector
            ? row.querySelector(options.folderSelector)?.value
            : '';
        if ((options.requireFolder && !ruleFolder) || (field !== 'explicit' && !value)) {
            return;
        }

        const rule = {
            conditionField: field,
            conditionOperator: operator,
            conditionValue: value,
            order: idx
        };
        if (options.requireFolder) {
            rule.destinationFolderId = Number(ruleFolder);
        }

        rules.push(rule);
    });
    return rules;
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

async function fetchSinglePlaylistPreference(source, sourceId) {
    const normalizedSource = String(source || '').trim();
    const normalizedSourceId = String(sourceId || '').trim();
    if (!normalizedSource || !normalizedSourceId) {
        return null;
    }

    return fetchJson(`/api/library/playlists/preferences/${encodeURIComponent(normalizedSource)}/${encodeURIComponent(normalizedSourceId)}`);
}

function normalizePlaylistPreferenceMap(rawPrefs) {
    if (!rawPrefs || typeof rawPrefs !== 'object') {
        return {};
    }

    if (!Array.isArray(rawPrefs)) {
        return rawPrefs;
    }

    const mapped = {};
    rawPrefs.forEach(item => {
        if (!item?.source || !item.sourceId) {
            return;
        }

        const key = `${item.source}:${item.sourceId}`;
        const normalizedArtwork = normalizePlaylistArtworkPreference(
            item.updateArtwork !== false,
            item.reuseSavedArtwork === true);
        mapped[key] = {
            folderId: item.destinationFolderId == null ? '' : String(item.destinationFolderId),
            atmosFolderId: item.atmosDestinationFolderId == null ? '' : String(item.atmosDestinationFolderId),
            syncTargets: normalizePlaylistSyncTargets(item.syncTargets, item.service),
            service: getPrimaryPlaylistSyncTarget(item.syncTargets || [item.service]),
            preferredEngine: item.preferredEngine || '',
            downloadEngineOrder: item.downloadEngineOrder || null,
            downloadVariantMode: item.downloadVariantMode || 'standard',
            syncMode: item.syncMode || 'mirror',
            updateArtwork: normalizedArtwork.updateArtwork,
            reuseSavedArtwork: normalizedArtwork.reuseSavedArtwork
        };
    });
    return mapped;
}

async function hydrateSinglePlaylistPreference(source, sourceId) {
    const item = await fetchSinglePlaylistPreference(source, sourceId);
    if (!item?.source || !item.sourceId) {
        return {};
    }

    return normalizePlaylistPreferenceMap([item]);
}

async function hydratePlaylistPreferences() {
    const serverPrefs = await fetchPlaylistPreferences();
    return normalizePlaylistPreferenceMap(serverPrefs);
}

function storePlaylistPreference(source, sourceId, updates) {
    const key = `${source}:${sourceId}`;
    const prefs = getStoredPreferences('playlistWatchlist');
    prefs[key] = { ...prefs[key], ...updates };
    storePreferences('playlistWatchlist', prefs);
}

async function persistPlaylistPreference(container, source, sourceId) {
    const folderSelect = container.querySelector(`[data-playlist-folder="${source}"][data-playlist-id="${sourceId}"]`);
    const atmosFolderSelect = container.querySelector(`[data-playlist-atmos-folder="${source}"][data-playlist-id="${sourceId}"]`);
    const syncTargets = collectPlaylistSyncTargets(container, source, sourceId);
    const engineSelect = container.querySelector(`[data-playlist-engine="${source}"][data-playlist-id="${sourceId}"]`);
    const downloadModeSelect = container.querySelector(`[data-playlist-download-mode="${source}"][data-playlist-id="${sourceId}"]`);
    const folderId = folderSelect?.value || null;
    const atmosFolderId = atmosFolderSelect?.value || '';
    const service = getPrimaryPlaylistSyncTarget(syncTargets);
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
        syncTargets,
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
        syncTargets,
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
    const syncTargetsByKey = {};
    document.querySelectorAll('[data-playlist-sync-target]').forEach(input => {
        const source = input.dataset.playlistSyncTarget;
        const sourceId = input.dataset.playlistId;
        const key = source && sourceId ? `${source}:${sourceId}` : '';
        if (!key) {
            return;
        }

        syncTargetsByKey[key] = syncTargetsByKey[key] || [];
        if (input.checked) {
            syncTargetsByKey[key].push(String(input.value || '').trim().toLowerCase());
        }
    });
    Object.entries(syncTargetsByKey).forEach(([key, syncTargets]) => {
        prefs[key] = {
            ...prefs[key],
            syncTargets,
            service: getPrimaryPlaylistSyncTarget(syncTargets)
        };
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
                service: value?.service || getPrimaryPlaylistSyncTarget(value?.syncTargets || []),
                syncTargets: normalizePlaylistSyncTargets(value?.syncTargets, value?.service),
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
                    emitActivitiesLiveUpdate('watchlist', { action: 'remove', artistId: artistIdValue });
                } else {
                    const artistName = await resolveWatchlistArtistName(artistIdValue);
                    await fetchJson('/api/library/watchlist', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ artistId: Number(artistIdValue), artistName })
                    });
                    showToast('Artist added to watchlist.');
                    emitActivitiesLiveUpdate('watchlist', { action: 'add', artistId: artistIdValue, artistName });
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
