(() => {
    const common = globalThis.DeezSpoTagLibraryPageCommon;
    const escapeHtml = common?.escapeHtml ?? ((text) => {
        if (text === null || text === undefined) {
            return '';
        }
        return String(text)
            .replaceAll('&', '&amp;')
            .replaceAll('<', '&lt;')
            .replaceAll('>', '&gt;')
            .replaceAll('"', '&quot;')
            .replaceAll("'", '&#39;');
    });
    const toSafeHttpUrl = common?.toSafeHttpUrl ?? ((rawUrl) => {
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
    });
    const showToast = common?.showToast ?? ((message, isError = false) => {
        if (globalThis.DeezSpoTag?.ui?.showToast) {
            globalThis.DeezSpoTag.ui.showToast(message, { type: isError ? 'error' : 'info' });
            return;
        }
        if (isError) {
            console.error(message);
            return;
        }
        console.log(message);
    });

    function resolveParsedErrorMessage(rawMessage) {
        const message = rawMessage.trim();
        if (!message) {
            return '';
        }
        try {
            const parsed = JSON.parse(message);
            if (typeof parsed?.message === 'string' && parsed.message) {
                return parsed.message;
            }
            if (typeof parsed?.error === 'string' && parsed.error) {
                return parsed.error;
            }
        } catch {
            // Keep original message text.
        }
        return message;
    }

    async function parseJsonResponse(response) {
        if (response.ok) {
            return response.json();
        }
        const raw = await response.text();
        const message = resolveParsedErrorMessage(raw);
        throw new Error(message || `Request failed (${response.status})`);
    }

    const fetchJson = common?.fetchJson ?? (async (url, options) => {
        const response = await fetch(url, {
            cache: options?.cache ?? 'no-store',
            ...options
        });
        return parseJsonResponse(response);
    });

    function renderSharedPlaylistActionButtons({
        actionAttribute = 'data-playlist-action',
        source,
        sourceId,
        name
    }) {
        const safeSource = escapeHtml(String(source || ''));
        const safeSourceId = escapeHtml(String(sourceId || ''));
        const safeName = escapeHtml(String(name || 'Playlist'));
        const withData = ` data-playlist-source="${safeSource}" data-playlist-id="${safeSourceId}" data-playlist-name="${safeName}"`;
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
                const isActive = fileName && activeFileName ? fileName === activeFileName : item?.isActive === true;
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
            buttons: [{ label: 'Close', value: 'close', primary: true }]
        });
        return true;
    }

    globalThis.renderSharedPlaylistActionButtons = renderSharedPlaylistActionButtons;
    globalThis.openSharedPlaylistArtworkPicker = openSharedPlaylistArtworkPicker;
})();
