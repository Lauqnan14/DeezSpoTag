const melodayUnsafeMethods = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

function melodayReadCsrfToken() {
    const tokenMeta = document.querySelector('meta[name="deezspotag-csrf-token"]');
    const token = tokenMeta?.getAttribute('content');
    return typeof token === 'string' ? token.trim() : '';
}

function buildMelodayFetchOptions(options) {
    const requestOptions = options ? { ...options } : {};
    const method = String(requestOptions.method || 'GET').toUpperCase();
    if (!melodayUnsafeMethods.has(method)) {
        return requestOptions;
    }

    const headers = new Headers(requestOptions.headers || {});
    if (!headers.has('X-CSRF-TOKEN')) {
        const csrfToken = melodayReadCsrfToken();
        if (csrfToken) {
            headers.set('X-CSRF-TOKEN', csrfToken);
        }
    }

    requestOptions.headers = headers;
    if (!requestOptions.credentials) {
        requestOptions.credentials = 'same-origin';
    }

    return requestOptions;
}

function melodayFetch(url, options) {
    if (typeof globalThis.activityFetch === 'function') {
        return globalThis.activityFetch(url, options);
    }

    return fetch(url, buildMelodayFetchOptions(options));
}

async function melodayFetchJson(url, options) {
    const response = await melodayFetch(url, options);
    if (!response.ok) {
        const text = await response.text();
        let message = text;
        try {
            const payload = text ? JSON.parse(text) : null;
            message = payload?.message || payload?.error || payload?.title || text;
        } catch {
            message = text;
        }
        throw new Error(message || `Request failed: ${response.status}`);
    }
    return response.json();
}

function melodayFormatTimestamp(value) {
    if (!value) {
        return 'Never';
    }
    const parsed = new Date(value);
    if (Number.isNaN(parsed.getTime())) {
        return 'Unknown';
    }
    return parsed.toLocaleString();
}

const melodayState = {
    enabled: true,
    settings: null,
    libraries: []
};

const melodayDefaults = {
    maxTracks: 50,
    historyLookbackDays: 30,
    excludePlayedDays: 4,
    maxActivePlaylists: 4,
    missedRunGraceMinutes: 60
};

const melodaySlotModes = ['sonic', 'direct', 'both'];

function melodayLog(level, message, timestamp) {
    const logger = globalThis.DeezSpoTag?.DownloadLogger;
    logger?.[level]?.(message, { engine: 'meloday', timestamp });
}

function updateMelodayStatusPill() {
    const statusPill = document.getElementById('melodayStatusPill');
    if (!statusPill) {
        return;
    }
    statusPill.textContent = melodayState.enabled ? 'Active' : 'Inactive';
    statusPill.classList.toggle('is-active', Boolean(melodayState.enabled));
}

async function loadMelodayStatus() {
    const lastRunEl = document.getElementById('melodayLastRun');
    const periodEl = document.getElementById('melodayPeriod');
    const lastMessageEl = document.getElementById('melodayLastMessage');
    const historySourcesEl = document.getElementById('melodayHistorySources');
    const settingsSummaryEl = document.getElementById('melodaySettingsSummary');
    try {
        const status = await melodayFetchJson('/api/meloday/status');
        melodayState.enabled = Boolean(status.enabled);
        updateMelodayStatusPill();
        if (lastRunEl) {
            lastRunEl.textContent = melodayFormatTimestamp(status.lastRunUtc);
        }
        if (periodEl) {
            periodEl.textContent = status.nextSlot || '--';
        }
        if (lastMessageEl) {
            lastMessageEl.textContent = status.lastMessage || '—';
        }
        if (historySourcesEl) {
            const sources = Array.isArray(status.historySources) ? status.historySources : [];
            historySourcesEl.textContent = sources.length > 0
	                ? sources.map(source => {
	                    const service = String(source.service || 'server');
	                    const endpointStatus = String(source.endpointStatus || source.status || 'unknown');
	                    const mappingStatus = String(source.mappingStatus || source.status || 'unknown');
	                    const resolved = Number(source.resolved || 0);
	                    const fetched = Number(source.fetched || 0);
	                    return `${service}: endpoint ${endpointStatus}, mapping ${mappingStatus} (${resolved}/${fetched} resolved)`;
	                }).join(' • ')
                : 'Not checked';
        }
        if (settingsSummaryEl) {
            const tracks = status.maxTracks ?? melodayDefaults.maxTracks;
            const lookback = status.historyLookbackDays ?? melodayDefaults.historyLookbackDays;
            const exclude = status.excludePlayedDays ?? melodayDefaults.excludePlayedDays;
            const grace = status.missedRunGraceMinutes ?? melodayDefaults.missedRunGraceMinutes;
            settingsSummaryEl.textContent = `Tracks: ${tracks} • Lookback: ${lookback}d • Exclude: ${exclude}d • Grace: ${grace}m`;
        }
        if (status.lastRunUtc && status.lastRunUtc !== melodayLastLogRun) {
            melodayLog('info', `Meloday run at ${melodayFormatTimestamp(status.lastRunUtc)}`, status.lastRunUtc);
            melodayLastLogRun = status.lastRunUtc;
        }
    } catch (error) {
        updateMelodayStatusPill();
        if (lastRunEl) {
            lastRunEl.textContent = 'Unknown';
        }
        if (periodEl) {
            periodEl.textContent = '--';
        }
        if (lastMessageEl) {
            lastMessageEl.textContent = '—';
        }
        if (historySourcesEl) {
            historySourcesEl.textContent = 'Unavailable';
        }
        if (settingsSummaryEl) {
            settingsSummaryEl.textContent = '—';
        }
        console.warn('Meloday status failed.', error);
    }
}

function melodayParseNumber(value, fallback) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : fallback;
}

function melodayNormalizeMode(value) {
    const normalized = String(value || '').trim().toLowerCase();
    if (normalized === 'direct' || normalized === 'both') {
        return normalized;
    }
    return 'sonic';
}

function melodayFormatMode(value) {
    const normalized = melodayNormalizeMode(value);
    if (normalized === 'direct') return 'Direct';
    if (normalized === 'both') return 'Both';
    return 'Sonic';
}

function melodayNormalizeTargetServers(values, defaultToAll = true) {
    const allowed = new Set(['plex', 'jellyfin', 'navidrome']);
    const normalized = [];
    (Array.isArray(values) ? values : []).forEach((value) => {
        const target = String(value || '').trim().toLowerCase();
        if (allowed.has(target) && !normalized.includes(target)) {
            normalized.push(target);
        }
    });
    return normalized.length > 0 || !defaultToAll
        ? normalized
        : [...allowed];
}

function melodaySetTargetServers(values) {
    const selected = melodayNormalizeTargetServers(values, true);
    document.querySelectorAll('[data-meloday-target-server]').forEach((input) => {
        const target = String(input.getAttribute('data-meloday-target-server') || '').trim().toLowerCase();
        input.checked = selected.includes(target);
    });
}

function melodayGetTargetServers() {
    return melodayNormalizeTargetServers(
        Array.from(document.querySelectorAll('[data-meloday-target-server]'))
            .filter(input => input && input.checked)
            .map(input => input.getAttribute('data-meloday-target-server')),
        false);
}

function melodayNormalizeSlots(values) {
    const stored = new Map();
    (Array.isArray(values) ? values : []).forEach((slot) => {
        if (slot?.id) {
            stored.set(String(slot.id), slot);
        }
    });

    // Canonical slot order is fixed; stored rows only override enabled + time.
    const canonical = [
        ['early-morning', 'Early Morning', '05:30'],
        ['morning', 'Morning', '08:30'],
        ['midday', 'Midday', '11:00'],
        ['noon', 'Noon', '13:00'],
        ['afternoon', 'Afternoon', '16:00'],
        ['evening', 'Evening', '19:00'],
        ['late-evening', 'Late Evening', '22:30']
    ];
    return canonical.map(([id, name, defaultTime], order) => {
        const saved = stored.get(id) || {};
        const time = typeof saved.generateAt === 'string' && /^\d{2}:\d{2}$/.test(saved.generateAt)
            ? saved.generateAt
            : defaultTime;
        return {
            id,
            name,
            generateAt: time,
            enabled: Boolean(saved.enabled),
            order
        };
    });
}

function melodayNormalizeLibraries(values, libraryCatalog) {
    const stored = new Map();
    (Array.isArray(values) ? values : []).forEach((library) => {
        const id = Number(library?.libraryId);
        if (Number.isFinite(id) && id > 0) {
            stored.set(id, library);
        }
    });

    return (Array.isArray(libraryCatalog) ? libraryCatalog : []).map((catalogLibrary) => {
        const id = Number(catalogLibrary?.id || 0);
        const saved = stored.get(id) || {};
        const assignments = new Map();
        (Array.isArray(saved.slots) ? saved.slots : []).forEach((assignment) => {
            if (assignment?.slotId) {
                assignments.set(String(assignment.slotId), melodayNormalizeMode(assignment.mode));
            }
        });

        const slots = [];
        if (Object.keys(saved).length > 0) {
            assignments.forEach((mode, slotId) => slots.push({ slotId, mode }));
        } else {
            // A library with no saved schedule starts with the first four slots at its maximum.
            melodayState.slots.slice(0, melodayDefaults.maxActivePlaylists).forEach((slot) => {
                slots.push({ slotId: slot.id, mode: 'sonic' });
            });
        }

        return {
            libraryId: id,
            name: catalogLibrary?.name || `Library ${id}`,
            trackCount: Number(catalogLibrary?.trackCount || 0),
            enabled: saved.enabled !== false,
            maxActivePlaylists: melodayParseNumber(saved.maxActivePlaylists, melodayDefaults.maxActivePlaylists),
            slots
        };
    });
}

function melodayCountPlaylists(library) {
    return (library?.slots || []).reduce((total, assignment) => (
        total + (melodayNormalizeMode(assignment.mode) === 'both' ? 2 : 1)
    ), 0);
}

function renderMelodayScheduleSlots() {
    const container = document.getElementById('meloday-schedule-slots');
    if (!container) {
        return;
    }
    container.innerHTML = '';
    melodayState.slots.forEach((slot) => {
        const row = document.createElement('div');
        row.className = 'meloday-schedule-row';

        const label = document.createElement('label');
        label.className = 'metadata-updater-option meloday-slot-toggle';
        const checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.setAttribute('data-meloday-slot-enabled', slot.id);
        checkbox.checked = slot.enabled;
        checkbox.addEventListener('change', () => {
            slot.enabled = checkbox.checked;
            renderMelodayLibrarySchedules();
        });
        const name = document.createElement('span');
        name.textContent = slot.name;
        label.append(checkbox, name);

        const time = document.createElement('input');
        time.type = 'time';
        time.className = 'form-control meloday-slot-time';
        time.value = slot.generateAt;
        time.setAttribute('data-meloday-slot-time', slot.id);
        time.setAttribute('aria-label', `${slot.name} generation time`);
        time.addEventListener('change', () => {
            if (/^\d{2}:\d{2}$/.test(time.value)) {
                slot.generateAt = time.value;
                renderMelodayLibrarySchedules();
            }
        });

        row.append(label, time);
        container.appendChild(row);
    });
}

function renderMelodayLibrarySchedules() {
    const container = document.getElementById('meloday-library-schedules');
    if (!container) {
        return;
    }
    container.innerHTML = '';
    if (melodayState.libraries.length === 0) {
        container.textContent = 'No nonempty configured music libraries found.';
        return;
    }

    melodayState.libraries.forEach((library) => {
        const card = document.createElement('div');
        card.className = 'meloday-library-schedule';

        const header = document.createElement('div');
        header.className = 'meloday-library-schedule-header';

        const toggleLabel = document.createElement('label');
        toggleLabel.className = 'metadata-updater-option';
        const enabledInput = document.createElement('input');
        enabledInput.type = 'checkbox';
        enabledInput.setAttribute('data-meloday-library-enabled', String(library.libraryId));
        enabledInput.checked = library.enabled;
        enabledInput.addEventListener('change', () => {
            library.enabled = enabledInput.checked;
        });
        const title = document.createElement('span');
        title.textContent = `${library.name} (${library.trackCount} tracks)`;
        toggleLabel.append(enabledInput, title);

        const maxWrap = document.createElement('div');
        maxWrap.className = 'field-input meloday-library-max';
        const maxInput = document.createElement('input');
        maxInput.type = 'number';
        maxInput.min = '1';
        maxInput.max = '7';
        maxInput.className = 'form-control';
        maxInput.value = library.maxActivePlaylists;
        maxInput.setAttribute('aria-label', `Maximum Meloday playlists for ${library.name}`);
        maxInput.addEventListener('change', () => {
            library.maxActivePlaylists = Math.max(1, Math.min(7, melodayParseNumber(maxInput.value, melodayDefaults.maxActivePlaylists)));
            maxInput.value = library.maxActivePlaylists;
            updateMelodayLibraryCounter(library);
        });
        const maxLabel = document.createElement('span');
        maxLabel.className = 'meloday-library-max-label';
        maxLabel.textContent = 'Maximum playlists';
        maxWrap.append(maxInput, maxLabel);

        header.append(toggleLabel, maxWrap);

        const rows = document.createElement('div');
        rows.className = 'meloday-library-slot-rows';
        melodayState.slots.forEach((slot) => {
            const row = document.createElement('div');
            row.className = 'meloday-library-slot-row';

            const assignment = library.slots.find(candidate => candidate.slotId === slot.id) || null;
            const slotCheck = document.createElement('label');
            slotCheck.className = 'metadata-updater-option meloday-slot-toggle';
            const input = document.createElement('input');
            input.type = 'checkbox';
            input.setAttribute('data-meloday-library-slot', `${library.libraryId}:${slot.id}`);
            input.checked = Boolean(assignment);
            input.disabled = !slot.enabled;
            input.addEventListener('change', () => {
                if (input.checked) {
                    library.slots.push({ slotId: slot.id, mode: 'sonic' });
                } else {
                    library.slots = library.slots.filter(candidate => candidate.slotId !== slot.id);
                }
                updateMelodayLibraryCounter(library);
                renderMelodayLibrarySchedules();
            });
            const slotName = document.createElement('span');
            slotName.textContent = slot.enabled ? `${slot.name} · ${slot.generateAt}` : `${slot.name} (slot disabled)`;
            slotCheck.append(input, slotName);

            const modeSelect = document.createElement('select');
            modeSelect.className = 'form-control meloday-slot-mode';
            modeSelect.setAttribute('aria-label', `Playlist mode for ${slot.name} in ${library.name}`);
            modeSelect.disabled = !input.checked;
            melodaySlotModes.forEach((mode) => {
                const option = document.createElement('option');
                option.value = mode;
                option.textContent = melodayFormatMode(mode);
                option.selected = melodayNormalizeMode(assignment?.mode) === mode;
                modeSelect.appendChild(option);
            });
            modeSelect.addEventListener('change', () => {
                const current = library.slots.find(candidate => candidate.slotId === slot.id);
                if (current) {
                    current.mode = melodayNormalizeMode(modeSelect.value);
                }
                updateMelodayLibraryCounter(library);
            });

            row.append(slotCheck, modeSelect);
            rows.appendChild(row);
        });

        const counter = document.createElement('div');
        counter.className = 'meloday-library-counter';
        counter.setAttribute('data-meloday-library-counter', String(library.libraryId));

        card.append(header, rows, counter);
        container.appendChild(card);
        updateMelodayLibraryCounter(library);
    });
}

function updateMelodayLibraryCounter(library) {
    const counter = document.querySelector(`[data-meloday-library-counter="${library.libraryId}"]`);
    if (!counter) {
        return;
    }
    const count = melodayCountPlaylists(library);
    const over = count > library.maxActivePlaylists;
    counter.textContent = `${count} of ${library.maxActivePlaylists} playlists selected`;
    counter.classList.toggle('is-over-limit', over);
}

function readMelodaySlotsFromDom() {
    melodayState.slots.forEach((slot) => {
        const checkbox = document.querySelector(`[data-meloday-slot-enabled="${slot.id}"]`);
        if (checkbox) {
            slot.enabled = checkbox.checked;
        }
        const time = document.querySelector(`[data-meloday-slot-time="${slot.id}"]`);
        if (time && /^\d{2}:\d{2}$/.test(time.value)) {
            slot.generateAt = time.value;
        }
    });
    return melodayState.slots.map(slot => ({
        id: slot.id,
        name: slot.name,
        enabled: slot.enabled,
        generateAt: slot.generateAt,
        order: slot.order
    }));
}

function buildMelodayLibrariesFromDom() {
    return melodayState.libraries.map(library => ({
        libraryId: library.libraryId,
        enabled: library.enabled,
        maxActivePlaylists: library.maxActivePlaylists,
        slots: library.slots.map(assignment => ({
            slotId: assignment.slotId,
            mode: melodayNormalizeMode(assignment.mode)
        }))
    }));
}

function buildMelodayPayload(enabledOverride) {
    const enabledEl = document.getElementById('meloday-enabled');
    const enabled = enabledOverride ?? enabledEl?.checked ?? true;
    const targetServers = melodayGetTargetServers();
    if (enabled && targetServers.length === 0) {
        throw new Error('Select at least one Meloday target server.');
    }

    const slots = readMelodaySlotsFromDom();
    const libraries = buildMelodayLibrariesFromDom();
    if (enabled) {
        const enabledLibraries = libraries.filter(library => library.enabled);
        if (enabledLibraries.length === 0) {
            throw new Error('Select at least one Meloday target library.');
        }
        for (const library of enabledLibraries) {
            const state = melodayState.libraries.find(candidate => candidate.libraryId === library.libraryId);
            const playlistCount = state ? melodayCountPlaylists(state) : 0;
            if (library.slots.length === 0) {
                throw new Error(`${state?.name || `Library ${library.libraryId}`}: select at least one scheduled slot.`);
            }
            if (playlistCount > library.maxActivePlaylists) {
                throw new Error(
                    `${state?.name || `Library ${library.libraryId}`}: ${playlistCount} of ${library.maxActivePlaylists} Meloday playlists selected — raise the maximum or disable slots.`);
            }
        }
    }

    return {
        enabled,
        playlistPrefix: document.getElementById('meloday-playlist-prefix')?.value || '',
        maxTracks: melodayParseNumber(document.getElementById('meloday-max-tracks')?.value, 50),
        historyLookbackDays: melodayParseNumber(document.getElementById('meloday-lookback-days')?.value, 30),
        excludePlayedDays: melodayParseNumber(document.getElementById('meloday-exclude-days')?.value, 4),
        sonicSimilarityDistance: melodayParseNumber(document.getElementById('meloday-similarity-distance')?.value, 0.35),
        sonicSimilarLimit: melodayParseNumber(document.getElementById('meloday-similar-limit')?.value, 8),
        historicalRatio: melodayParseNumber(document.getElementById('meloday-historical-ratio')?.value, 0.3),
        missedRunGraceMinutes: melodayParseNumber(document.getElementById('meloday-grace-minutes')?.value, 60),
        slots,
        libraries,
        targetServers
    };
}

async function loadMelodaySettings() {
    const enabledEl = document.getElementById('meloday-enabled');
    if (!enabledEl) {
        return;
    }
    try {
        const [settings, libraries] = await Promise.all([
            melodayFetchJson('/api/meloday/settings'),
            melodayFetchJson('/api/meloday/settings/libraries')
        ]);
        enabledEl.checked = settings.enabled ?? true;
        melodayState.enabled = enabledEl.checked;
        melodayState.settings = { ...settings, enabled: enabledEl.checked };
        melodayState.slots = melodayNormalizeSlots(settings.slots);
        melodayState.libraries = melodayNormalizeLibraries(settings.libraries, libraries);
        updateMelodayStatusPill();
        melodaySetTargetServers(settings.targetServers);
        const playlistPrefix = document.getElementById('meloday-playlist-prefix');
        const maxTracks = document.getElementById('meloday-max-tracks');
        const lookback = document.getElementById('meloday-lookback-days');
        const exclude = document.getElementById('meloday-exclude-days');
        const similarityDistance = document.getElementById('meloday-similarity-distance');
        const similarLimit = document.getElementById('meloday-similar-limit');
        const historicalRatio = document.getElementById('meloday-historical-ratio');
        const grace = document.getElementById('meloday-grace-minutes');
        if (playlistPrefix) playlistPrefix.value = settings.playlistPrefix || '';
        if (maxTracks) maxTracks.value = settings.maxTracks ?? 50;
        if (lookback) lookback.value = settings.historyLookbackDays ?? 30;
        if (exclude) exclude.value = settings.excludePlayedDays ?? 4;
        if (similarityDistance) similarityDistance.value = settings.sonicSimilarityDistance ?? 0.35;
        if (similarLimit) similarLimit.value = settings.sonicSimilarLimit ?? 8;
        if (historicalRatio) historicalRatio.value = settings.historicalRatio ?? 0.3;
        if (grace) grace.value = settings.missedRunGraceMinutes ?? 60;
        renderMelodayScheduleSlots();
        renderMelodayLibrarySchedules();
        await loadMelodayArtwork();
    } catch (error) {
        console.warn('Meloday settings failed to load.', error);
    }
}

async function saveMelodaySettings() {
    const saveButton = document.getElementById('saveMelodaySettings');
    if (saveButton?.disabled) {
        return;
    }
    if (saveButton) {
        saveButton.disabled = true;
    }
    try {
        const payload = buildMelodayPayload();
        await melodayFetchJson('/api/meloday/settings', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        melodayState.settings = { ...payload };
        if (typeof notifyActivity === 'function') {
            notifyActivity('Meloday settings saved.');
        } else if (typeof showToast === 'function') {
            showToast('Meloday settings saved.');
        }
        await loadMelodayStatus();
    } catch (error) {
        if (typeof notifyActivity === 'function') {
            notifyActivity(`Failed to save Meloday settings: ${error.message}`, 'error');
        } else if (typeof showToast === 'function') {
            showToast(`Failed to save Meloday settings: ${error.message}`, true);
        }
        melodayLog('error', `Failed to save Meloday settings: ${error.message}`);
    } finally {
        if (saveButton) {
            saveButton.disabled = false;
        }
    }
}

async function saveMelodayEnabled(enabled) {
    const enabledEl = document.getElementById('meloday-enabled');
    const previous = enabledEl?.checked;
    if (enabledEl) {
        enabledEl.checked = enabled;
    }
    try {
        const payload = buildMelodayPayload(enabled);
        await melodayFetchJson('/api/meloday/settings', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        melodayState.enabled = enabled;
        melodayState.settings = { ...payload };
        updateMelodayStatusPill();
        const message = enabled ? 'Meloday enabled.' : 'Meloday disabled.';
        if (typeof notifyActivity === 'function') {
            notifyActivity(message);
        } else if (typeof showToast === 'function') {
            showToast(message);
        }
        await loadMelodayStatus();
    } catch (error) {
        if (enabledEl) {
            enabledEl.checked = previous;
        }
        if (typeof notifyActivity === 'function') {
            notifyActivity(`Failed to update Meloday: ${error.message}`, 'error');
        } else if (typeof showToast === 'function') {
            showToast(`Failed to update Meloday: ${error.message}`, true);
        }
        melodayLog('error', `Failed to update Meloday: ${error.message}`);
    }
}

async function runMeloday() {
    const button = document.getElementById('runMeloday');
    const lastMessageEl = document.getElementById('melodayLastMessage');
    if (!button) {
        return;
    }
    button.disabled = true;
    const originalText = button.textContent;
    button.textContent = 'Running...';
    if (lastMessageEl) {
        lastMessageEl.textContent = 'Saving settings and running Meloday...';
    }
    try {
        const payload = buildMelodayPayload();
        await melodayFetchJson('/api/meloday/settings', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        melodayState.settings = { ...payload };

        const result = await melodayFetchJson('/api/meloday/run', { method: 'POST' });
        if (typeof notifyActivity === 'function') {
            notifyActivity(result?.message || 'Meloday playlists updated.');
        } else if (typeof showToast === 'function') {
            showToast(result?.message || 'Meloday playlists updated.');
        }
        melodayLog('info', result?.message || 'Meloday playlists updated.');
        await loadMelodayStatus();
    } catch (error) {
        if (typeof notifyActivity === 'function') {
            notifyActivity(`Meloday failed: ${error.message}`, 'error');
        } else if (typeof showToast === 'function') {
            showToast(`Meloday failed: ${error.message}`, true);
        }
        if (lastMessageEl) {
            lastMessageEl.textContent = error.message || 'Meloday failed.';
        }
        melodayLog('error', `Meloday failed: ${error.message}`);
    } finally {
        button.textContent = originalText || 'Run Meloday';
        button.disabled = false;
    }
}

async function loadMelodayArtwork() {
    const countEl = document.getElementById('meloday-artwork-count');
    const assignmentsEl = document.getElementById('meloday-artwork-assignments');
    if (!countEl && !assignmentsEl) {
        return;
    }
    try {
        const artwork = await melodayFetchJson('/api/meloday/artwork');
        if (countEl) {
            const count = Number(artwork?.count || 0);
            countEl.textContent = `${count} image${count === 1 ? '' : 's'} available`;
        }
        if (assignmentsEl) {
            const assignments = Array.isArray(artwork?.assignments) ? artwork.assignments : [];
            const libraryNames = new Map(melodayState.libraries.map(library => [Number(library.libraryId), library.name]));
            assignmentsEl.innerHTML = '';
            if (assignments.length === 0) {
                assignmentsEl.textContent = 'No playlists own artwork yet.';
                return;
            }
            assignments.forEach((assignment) => {
                const row = document.createElement('div');
                row.className = 'meloday-artwork-assignment';
                const libraryName = libraryNames.get(Number(assignment.libraryId)) || `Library ${assignment.libraryId}`;
                row.textContent = `${libraryName} — ${melodaySlotDisplayName(assignment.slotId)} (${melodayFormatMode(assignment.mode)}) → ${assignment.imageId}`;
                assignmentsEl.appendChild(row);
            });
        }
    } catch (error) {
        if (countEl) {
            countEl.textContent = '—';
        }
        if (assignmentsEl) {
            assignmentsEl.textContent = 'Artwork unavailable.';
        }
        console.warn('Meloday artwork failed to load.', error);
    }
}

function melodaySlotDisplayName(slotId) {
    const slot = (melodayState.slots || []).find(candidate => candidate.id === String(slotId || '').toLowerCase());
    return slot ? slot.name : String(slotId || '');
}

async function uploadMelodayArtwork() {
    const input = document.getElementById('meloday-artwork-upload');
    const add = document.getElementById('meloday-artwork-add');
    if (!input || !add || input.files.length === 0) {
        return;
    }
    add.disabled = true;
    try {
        const form = new FormData();
        Array.from(input.files).forEach(file => form.append('files', file));
        const result = await melodayFetchJson('/api/meloday/artwork', { method: 'POST', body: form });
        input.value = '';
        const message = `Added ${result?.added || 0} Meloday image(s); ${result?.count ?? 0} available.`;
        if (typeof notifyActivity === 'function') {
            notifyActivity(message);
        } else if (typeof showToast === 'function') {
            showToast(message);
        }
        await loadMelodayArtwork();
    } catch (error) {
        if (typeof notifyActivity === 'function') {
            notifyActivity(`Failed to upload Meloday artwork: ${error.message}`, 'error');
        } else if (typeof showToast === 'function') {
            showToast(`Failed to upload Meloday artwork: ${error.message}`, true);
        }
        melodayLog('error', `Failed to upload Meloday artwork: ${error.message}`);
    } finally {
        add.disabled = false;
    }
}

function initializeMelodayCard() {
    // Use the status pill as the presence check now that the text block is gone
    if (document.getElementById('melodayStatusPill')) {
        globalThis.DeezSpoTagMeloday = {
            refresh: async () => {
                await loadMelodayStatus();
                await loadMelodaySettings();
            }
        };
        loadMelodayStatus();
        loadMelodaySettings();
        const button = document.getElementById('runMeloday');
        if (button) {
            button.addEventListener('click', runMeloday);
        }
        const saveButton = document.getElementById('saveMelodaySettings');
        if (saveButton) {
            saveButton.addEventListener('click', saveMelodaySettings);
        }
        const enabledEl = document.getElementById('meloday-enabled');
        if (enabledEl) {
            enabledEl.addEventListener('change', async () => {
                await saveMelodayEnabled(enabledEl.checked);
            });
        }
        const artworkAdd = document.getElementById('meloday-artwork-add');
        const artworkUpload = document.getElementById('meloday-artwork-upload');
        if (artworkAdd && artworkUpload) {
            artworkAdd.addEventListener('click', () => artworkUpload.click());
            artworkUpload.addEventListener('change', uploadMelodayArtwork);
        }
        loadMelodayArtwork();
    }
}

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initializeMelodayCard);
} else {
    initializeMelodayCard();
}
