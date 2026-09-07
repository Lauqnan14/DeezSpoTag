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
    slots: [],
    libraries: [],
    view: 'grid'
};

const melodayDefaults = {
    maxTracks: 50,
    historyLookbackDays: 30,
    excludePlayedDays: 4,
    maxActivePlaylists: 4,
    missedRunGraceMinutes: 60
};

const melodayCanonicalSlots = [
    ['early-morning', 'Early Morning', '05:30', 'wb_twilight'],
    ['morning', 'Morning', '08:30', 'wb_sunny'],
    ['midday', 'Midday', '11:00', 'light_mode'],
    ['noon', 'Noon', '13:00', 'flare'],
    ['afternoon', 'Afternoon', '16:00', 'wb_cloudy'],
    ['evening', 'Evening', '19:00', 'nights_stay'],
    ['late-evening', 'Late Evening', '22:30', 'bedtime']
];

const melodaySlotModes = ['direct', 'sonic', 'both'];

function melodayLog(level, message, timestamp) {
    const logger = globalThis.DeezSpoTag?.DownloadLogger;
    logger?.[level]?.(message, { engine: 'meloday', timestamp });
}

function melodayNotify(message, isError) {
    if (typeof notifyActivity === 'function') {
        notifyActivity(message, isError ? 'error' : undefined);
    } else if (typeof showToast === 'function') {
        showToast(message, Boolean(isError));
    }
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

/* ------------------------------------------------------------------ *
 * Rule-based playlist naming (mirrors MelodayScheduleSlots server side)
 * ------------------------------------------------------------------ */
function melodayPlaylistName(libraryName, slotName, mode) {
    const library = String(libraryName || 'Library').trim() || 'Library';
    return melodayNormalizeMode(mode) === 'sonic'
        ? `${slotName} Sonic Playlist for ${library}`
        : `${slotName} Playlist for ${library}`;
}

function melodayPlaylistNamesForMode(libraryName, slotName, mode) {
    if (melodayNormalizeMode(mode) === 'both') {
        return [
            melodayPlaylistName(libraryName, slotName, 'direct'),
            melodayPlaylistName(libraryName, slotName, 'sonic')
        ];
    }
    return [melodayPlaylistName(libraryName, slotName, mode)];
}

function melodayCountPlaylists(library) {
    return (library?.slotIds || []).length * (melodayNormalizeMode(library?.mode) === 'both' ? 2 : 1);
}

/* ------------------------------------------------------------------ *
 * Activities status card
 * ------------------------------------------------------------------ */
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
    } catch (error) {
        updateMelodayStatusPill();
        if (lastRunEl) lastRunEl.textContent = 'Unknown';
        if (periodEl) periodEl.textContent = '--';
        if (lastMessageEl) lastMessageEl.textContent = '—';
        if (historySourcesEl) historySourcesEl.textContent = 'Unavailable';
        console.warn('Meloday status failed.', error);
    }
}

/* ------------------------------------------------------------------ *
 * Configuration page state
 * ------------------------------------------------------------------ */
function melodayNormalizeSlots(values) {
    const stored = new Map();
    (Array.isArray(values) ? values : []).forEach((slot) => {
        if (slot?.id) {
            stored.set(String(slot.id), slot);
        }
    });

    return melodayCanonicalSlots.map(([id, name, defaultTime, icon]) => {
        const saved = stored.get(id) || {};
        const time = typeof saved.generateAt === 'string' && /^\d{2}:\d{2}$/.test(saved.generateAt)
            ? saved.generateAt
            : defaultTime;
        return { id, name, generateAt: time, icon };
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
        const slotIds = [];
        (Array.isArray(saved.slotIds) ? saved.slotIds : []).forEach((slotId) => {
            const normalized = String(slotId || '').trim().toLowerCase();
            if (normalized && !slotIds.includes(normalized)) {
                slotIds.push(normalized);
            }
        });

        return {
            libraryId: id,
            name: catalogLibrary?.name || `Library ${id}`,
            trackCount: Number(catalogLibrary?.trackCount || 0),
            maxActivePlaylists: Math.max(1, Math.min(7, Number(saved.maxActivePlaylists) || melodayDefaults.maxActivePlaylists)),
            mode: melodayNormalizeMode(saved.mode),
            slotIds
        };
    });
}

async function loadMelodayPageConfig() {
    const [settings, libraries] = await Promise.all([
        melodayFetchJson('/api/meloday/settings'),
        melodayFetchJson('/api/meloday/settings/libraries')
    ]);

    melodayState.settings = settings;
    melodayState.enabled = settings.enabled ?? true;
    melodayState.slots = melodayNormalizeSlots(settings.slots);
    melodayState.libraries = melodayNormalizeLibraries(settings.libraries, libraries);

    const enabledEl = document.getElementById('meloday-enabled');
    if (enabledEl) {
        enabledEl.checked = melodayState.enabled;
    }
    melodaySetTargetServers(settings.targetServers);
    const maxTracks = document.getElementById('meloday-max-tracks');
    const lookback = document.getElementById('meloday-lookback-days');
    const exclude = document.getElementById('meloday-exclude-days');
    const similarityDistance = document.getElementById('meloday-similarity-distance');
    const similarLimit = document.getElementById('meloday-similar-limit');
    const historicalRatio = document.getElementById('meloday-historical-ratio');
    const grace = document.getElementById('meloday-grace-minutes');
    if (maxTracks) maxTracks.value = settings.maxTracks ?? 50;
    if (lookback) lookback.value = settings.historyLookbackDays ?? 30;
    if (exclude) exclude.value = settings.excludePlayedDays ?? 4;
    if (similarityDistance) similarityDistance.value = settings.sonicSimilarityDistance ?? 0.35;
    if (similarLimit) similarLimit.value = settings.sonicSimilarLimit ?? 8;
    if (historicalRatio) historicalRatio.value = settings.historicalRatio ?? 0.3;
    if (grace) grace.value = settings.missedRunGraceMinutes ?? 60;

    renderMelodayScheduleSlots();
    renderMelodayLibraryCards();
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

/* ------------------------------------------------------------------ *
 * Rendering: schedule slots
 * ------------------------------------------------------------------ */
function renderMelodayScheduleSlots() {
    const container = document.getElementById('meloday-schedule-slots');
    if (!container) {
        return;
    }
    container.innerHTML = '';
    melodayState.slots.forEach((slot) => {
        const card = document.createElement('div');
        card.className = 'meloday-time-slot';

        const icon = document.createElement('span');
        icon.className = 'meloday-time-slot-icon';
        const iconGlyph = document.createElement('span');
        iconGlyph.className = 'material-icons';
        iconGlyph.textContent = slot.icon;
        icon.appendChild(iconGlyph);

        const name = document.createElement('span');
        name.className = 'meloday-time-slot-name';
        name.textContent = slot.name;

        const field = document.createElement('label');
        field.className = 'meloday-time-field';
        const clockIcon = document.createElement('span');
        clockIcon.className = 'material-icons';
        clockIcon.textContent = 'access_time';
        const time = document.createElement('input');
        time.type = 'time';
        time.value = slot.generateAt;
        time.setAttribute('data-meloday-slot-time', slot.id);
        time.setAttribute('aria-label', `${slot.name} generation time`);
        time.addEventListener('change', () => {
            if (/^\d{2}:\d{2}$/.test(time.value)) {
                slot.generateAt = time.value;
                renderMelodayLibraryCards();
            } else {
                time.value = slot.generateAt;
            }
        });
        field.append(clockIcon, time);

        card.append(icon, name, field);
        container.appendChild(card);
    });
}

function melodayResetScheduleToDefault() {
    melodayState.slots.forEach((slot) => {
        const canonical = melodayCanonicalSlots.find(entry => entry[0] === slot.id);
        if (canonical) {
            slot.generateAt = canonical[2];
        }
    });
    renderMelodayScheduleSlots();
    renderMelodayLibraryCards();
}

/* ------------------------------------------------------------------ *
 * Rendering: library cards
 * ------------------------------------------------------------------ */
function renderMelodayLibraryCards() {
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
        container.appendChild(buildMelodayLibraryCard(library));
    });
}

function buildMelodayLibraryCard(library) {
    const card = document.createElement('article');
    card.className = 'meloday-library-card';
    card.setAttribute('data-meloday-library', String(library.libraryId));

    const head = document.createElement('header');
    head.className = 'meloday-library-head';

    const accentIndex = melodayState.libraries.indexOf(library) % 7;
    const tile = document.createElement('span');
    tile.className = `meloday-library-tile accent-${accentIndex}`;
    const tileIcon = document.createElement('span');
    tileIcon.className = 'material-icons';
    tileIcon.textContent = 'library_music';
    tile.appendChild(tileIcon);

    const identity = document.createElement('div');
    identity.className = 'meloday-library-id';
    const title = document.createElement('h3');
    title.textContent = library.name;
    const trackCount = document.createElement('p');
    trackCount.textContent = `${library.trackCount.toLocaleString()} track${library.trackCount === 1 ? '' : 's'}`;
    identity.append(title, trackCount);

    const maxWrap = document.createElement('div');
    maxWrap.className = 'meloday-library-max';
    const maxLabel = document.createElement('label');
    maxLabel.textContent = 'Maximum playlists';
    const maxSelect = document.createElement('select');
    maxSelect.setAttribute('data-meloday-library-max', String(library.libraryId));
    maxSelect.setAttribute('aria-label', `Maximum playlists for ${library.name}`);
    for (let value = 1; value <= 7; value++) {
        const option = document.createElement('option');
        option.value = String(value);
        option.textContent = String(value);
        option.selected = library.maxActivePlaylists === value;
        maxSelect.appendChild(option);
    }
    maxSelect.addEventListener('change', () => {
        library.maxActivePlaylists = Number(maxSelect.value) || melodayDefaults.maxActivePlaylists;
        updateMelodayLibraryFooter(library);
    });
    maxWrap.append(maxLabel, maxSelect);

    const modeWrap = document.createElement('div');
    modeWrap.className = 'meloday-library-mode';
    const modeLabel = document.createElement('label');
    modeLabel.textContent = 'Mode';
    const modeGroup = document.createElement('div');
    modeGroup.className = 'meloday-mode-segmented';
    modeGroup.setAttribute('role', 'radiogroup');
    modeGroup.setAttribute('data-meloday-library-mode', String(library.libraryId));
    modeGroup.setAttribute('aria-label', `Playlist mode for ${library.name}`);
    melodaySlotModes.forEach((mode) => {
        const button = document.createElement('button');
        button.type = 'button';
        button.setAttribute('data-mode', mode);
        button.textContent = melodayFormatMode(mode);
        button.classList.toggle('is-active', melodayNormalizeMode(library.mode) === mode);
        button.addEventListener('click', () => {
            library.mode = melodayNormalizeMode(mode);
            modeGroup.querySelectorAll('button').forEach(candidate => {
                candidate.classList.toggle('is-active', candidate === button);
            });
            updateMelodayLibraryFooter(library);
        });
        modeGroup.appendChild(button);
    });
    modeWrap.append(modeLabel, modeGroup);

    head.append(tile, identity, maxWrap, modeWrap);

    const chips = document.createElement('div');
    chips.className = 'meloday-library-chips';
    melodayState.slots.forEach((slot) => {
        chips.appendChild(buildMelodaySlotChip(library, slot));
    });

    const foot = document.createElement('footer');
    foot.className = 'meloday-library-foot';
    const example = document.createElement('div');
    example.className = 'meloday-library-example';
    example.setAttribute('data-meloday-library-example', String(library.libraryId));
    const meta = document.createElement('div');
    meta.className = 'meloday-library-meta';
    const counter = document.createElement('span');
    counter.className = 'meloday-library-counter';
    counter.setAttribute('data-meloday-library-counter', String(library.libraryId));
    const note = document.createElement('span');
    note.className = 'meloday-library-note';
    note.textContent = 'Names are generated automatically';
    meta.append(counter, note);
    foot.append(example, meta);

    card.append(head, chips, foot);
    updateMelodayLibraryFooter(library);
    return card;
}

function buildMelodaySlotChip(library, slot) {
    const chip = document.createElement('label');
    chip.className = 'meloday-slot-chip';
    const selected = library.slotIds.includes(slot.id);
    chip.classList.toggle('is-selected', selected);

    const input = document.createElement('input');
    input.type = 'checkbox';
    input.checked = selected;
    input.setAttribute('data-meloday-library-slot', `${library.libraryId}:${slot.id}`);
    input.setAttribute('aria-label', `${slot.name} ${slot.generateAt} for ${library.name}`);
    input.addEventListener('change', () => {
        if (input.checked) {
            if (library.slotIds.length >= library.maxActivePlaylists) {
                input.checked = false;
                melodayNotify(
                    `${library.name}: maximum ${library.maxActivePlaylists} playlist slots — deselect one first.`,
                    true);
                melodayLog('error', `${library.name}: maximum ${library.maxActivePlaylists} playlist slots.`);
                return;
            }
            library.slotIds.push(slot.id);
        } else {
            library.slotIds = library.slotIds.filter(slotId => slotId !== slot.id);
        }
        updateMelodayLibraryCard(library);
    });

    const check = document.createElement('span');
    check.className = 'meloday-slot-chip-check';
    const checkIcon = document.createElement('span');
    checkIcon.className = 'material-icons';
    checkIcon.textContent = 'check';
    check.appendChild(checkIcon);

    const name = document.createElement('span');
    name.className = 'meloday-slot-chip-name';
    name.textContent = slot.name;

    const time = document.createElement('span');
    time.className = 'meloday-slot-chip-time';
    time.textContent = slot.generateAt;

    chip.append(input, check, name, time);
    return chip;
}

function updateMelodayLibraryCard(library) {
    const card = document.querySelector(`[data-meloday-library="${library.libraryId}"]`);
    if (!card) {
        return;
    }
    card.querySelectorAll('.meloday-slot-chip').forEach((chip) => {
        const input = chip.querySelector('input');
        const slotId = String(input?.getAttribute('data-meloday-library-slot') || '').split(':')[1];
        const selected = library.slotIds.includes(slotId);
        input.checked = selected;
        chip.classList.toggle('is-selected', selected);
    });
    updateMelodayLibraryFooter(library);
}

function updateMelodayLibraryFooter(library) {
    const counter = document.querySelector(`[data-meloday-library-counter="${library.libraryId}"]`);
    if (counter) {
        const selected = library.slotIds.length;
        const over = selected > library.maxActivePlaylists;
        counter.textContent = `${selected} of ${library.maxActivePlaylists} selected`;
        counter.classList.toggle('is-over-limit', over);
    }

    const example = document.querySelector(`[data-meloday-library-example="${library.libraryId}"]`);
    if (example) {
        example.innerHTML = '';
        const lastSlotId = melodayState.slots
            .filter(slot => library.slotIds.includes(slot.id))
            .sort((a, b) => melodaySlotOrder(b.id) - melodaySlotOrder(a.id))[0];
        if (lastSlotId) {
            const names = melodayPlaylistNamesForMode(library.name, lastSlotId.name, library.mode);
            const label = document.createElement('span');
            label.className = 'meloday-library-example-label';
            label.textContent = names.length > 1 ? 'Example names:' : 'Example name:';
            example.appendChild(label);
            names.forEach((name, index) => {
                const strong = document.createElement('strong');
                strong.textContent = names.length > 1 ? `${index + 1}) ${name}` : name;
                strong.title = name;
                example.appendChild(strong);
            });
        }
    }
}

function melodaySlotOrder(slotId) {
    const index = melodayCanonicalSlots.findIndex(entry => entry[0] === slotId);
    return index < 0 ? Number.MAX_SAFE_INTEGER : index;
}

/* ------------------------------------------------------------------ *
 * Payload, save, run, enable
 * ------------------------------------------------------------------ */
function buildMelodayLibrariesFromDom() {
    return melodayState.libraries.map(library => ({
        libraryId: library.libraryId,
        maxActivePlaylists: library.maxActivePlaylists,
        mode: melodayNormalizeMode(library.mode),
        slotIds: [...library.slotIds]
    }));
}

function melodayParseNumber(value, fallback) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : fallback;
}

function buildMelodayPayload(enabledOverride) {
    const enabledEl = document.getElementById('meloday-enabled');
    const enabled = enabledOverride ?? enabledEl?.checked ?? true;
    const targetServers = melodayGetTargetServers();
    if (enabled && targetServers.length === 0) {
        throw new Error('Select at least one Meloday target server.');
    }

    const libraries = buildMelodayLibrariesFromDom();
    if (enabled) {
        const targeted = libraries.filter(library => library.slotIds.length > 0);
        if (targeted.length === 0) {
            throw new Error('Select at least one time slot for at least one library.');
        }
        for (const library of targeted) {
            const state = melodayState.libraries.find(candidate => candidate.libraryId === library.libraryId);
            if (library.slotIds.length > library.maxActivePlaylists) {
                throw new Error(
                    `${state?.name || `Library ${library.libraryId}`}: ${library.slotIds.length} of ${library.maxActivePlaylists} playlist slots selected — raise the maximum or deselect slots.`);
            }
        }
    }

    return {
        enabled,
        maxTracks: melodayParseNumber(document.getElementById('meloday-max-tracks')?.value, 50),
        historyLookbackDays: melodayParseNumber(document.getElementById('meloday-lookback-days')?.value, 30),
        excludePlayedDays: melodayParseNumber(document.getElementById('meloday-exclude-days')?.value, 4),
        sonicSimilarityDistance: melodayParseNumber(document.getElementById('meloday-similarity-distance')?.value, 0.35),
        sonicSimilarLimit: melodayParseNumber(document.getElementById('meloday-similar-limit')?.value, 8),
        historicalRatio: melodayParseNumber(document.getElementById('meloday-historical-ratio')?.value, 0.3),
        missedRunGraceMinutes: melodayParseNumber(document.getElementById('meloday-grace-minutes')?.value, 60),
        slots: melodayState.slots.map(slot => ({
            id: slot.id,
            name: slot.name,
            generateAt: slot.generateAt
        })),
        libraries,
        targetServers
    };
}

function applyMelodaySettingsResponse(settings) {
    melodayState.settings = settings;
    melodayState.enabled = settings.enabled ?? melodayState.enabled;
    melodayState.slots = melodayNormalizeSlots(settings.slots);
    const namesById = new Map(melodayState.libraries.map(library => [Number(library.libraryId), library]));
    melodayState.libraries = melodayNormalizeLibraries(settings.libraries, melodayState.libraries.map(library => ({
        id: library.libraryId,
        name: library.name,
        trackCount: library.trackCount
    })));
    const enabledEl = document.getElementById('meloday-enabled');
    if (enabledEl) {
        enabledEl.checked = melodayState.enabled;
    }
    renderMelodayScheduleSlots();
    renderMelodayLibraryCards();
    void namesById;
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
        const saved = await melodayFetchJson('/api/meloday/settings', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        applyMelodaySettingsResponse(saved);
        updateMelodayStatusPill();
        melodayNotify('Meloday settings saved.');
        melodayLog('info', 'Meloday settings saved.');
    } catch (error) {
        melodayNotify(`Failed to save Meloday settings: ${error.message}`, true);
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
        const saved = await melodayFetchJson('/api/meloday/settings', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        applyMelodaySettingsResponse(saved);
        updateMelodayStatusPill();
        melodayNotify(enabled ? 'Meloday enabled.' : 'Meloday disabled.');
    } catch (error) {
        if (enabledEl) {
            enabledEl.checked = previous;
        }
        melodayNotify(`Failed to update Meloday: ${error.message}`, true);
        melodayLog('error', `Failed to update Meloday: ${error.message}`);
    }
}

async function runMeloday() {
    const button = document.getElementById('runMeloday');
    if (!button) {
        return;
    }
    button.disabled = true;
    try {
        const payload = buildMelodayPayload();
        const saved = await melodayFetchJson('/api/meloday/settings', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        applyMelodaySettingsResponse(saved);

        const result = await melodayFetchJson('/api/meloday/run', { method: 'POST' });
        melodayNotify(result?.message || 'Meloday playlists updated.');
        melodayLog('info', result?.message || 'Meloday playlists updated.');
        await loadMelodayStatus();
    } catch (error) {
        melodayNotify(`Meloday failed: ${error.message}`, true);
        melodayLog('error', `Meloday failed: ${error.message}`);
    } finally {
        button.disabled = false;
    }
}

/* ------------------------------------------------------------------ *
 * Artwork
 * ------------------------------------------------------------------ */
function initializeMelodayPage() {
    if (!document.getElementById('meloday-config')) {
        return;
    }

    loadMelodayPageConfig().catch(error => {
        console.warn('Meloday settings failed to load.', error);
        melodayNotify(`Failed to load Meloday settings: ${error.message}`, true);
    });

    const saveButton = document.getElementById('saveMelodaySettings');
    if (saveButton) {
        saveButton.addEventListener('click', saveMelodaySettings);
    }
    const runButton = document.getElementById('runMeloday');
    if (runButton) {
        runButton.addEventListener('click', runMeloday);
    }
    const enabledEl = document.getElementById('meloday-enabled');
    if (enabledEl) {
        enabledEl.addEventListener('change', () => saveMelodayEnabled(enabledEl.checked));
    }
    const resetButton = document.getElementById('meloday-schedule-reset');
    if (resetButton) {
        resetButton.addEventListener('click', melodayResetScheduleToDefault);
    }
    const advancedToggle = document.getElementById('meloday-advanced-toggle');
    const advancedPanel = document.getElementById('meloday-advanced-panel');
    if (advancedToggle && advancedPanel) {
        advancedToggle.addEventListener('click', () => {
            const expanded = advancedToggle.getAttribute('aria-expanded') === 'true';
            advancedToggle.setAttribute('aria-expanded', expanded ? 'false' : 'true');
            advancedPanel.hidden = expanded;
        });
    }
    document.querySelectorAll('[data-meloday-view]').forEach((button) => {
        button.addEventListener('click', () => {
            const view = String(button.getAttribute('data-meloday-view') || 'grid');
            melodayState.view = view;
            document.querySelectorAll('[data-meloday-view]').forEach(candidate => {
                candidate.classList.toggle('is-active', candidate === button);
            });
            const config = document.getElementById('meloday-config');
            config?.classList.toggle('is-list', view === 'list');
        });
    });

}

function initializeMelodayStatusCard() {
    if (!document.getElementById('melodayStatusPill')) {
        return;
    }
    globalThis.DeezSpoTagMeloday = {
        refresh: async () => {
            await loadMelodayStatus();
        }
    };
    loadMelodayStatus();
}

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => {
        initializeMelodayPage();
        initializeMelodayStatusCard();
    });
} else {
    initializeMelodayPage();
    initializeMelodayStatusCard();
}
