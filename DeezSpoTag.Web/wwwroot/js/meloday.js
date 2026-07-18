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
    settings: null
};

let melodayLastLogRun = null;
const melodayDefaults = {
    maxTracks: 50,
    historyLookbackDays: 30,
    excludePlayedDays: 4,
    mode: 'sonic'
};

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
            periodEl.textContent = status.currentPeriod || '--';
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
            const mode = melodayFormatMode(status.mode || melodayDefaults.mode);
            settingsSummaryEl.textContent = `Mode: ${mode} • Tracks: ${tracks} • Lookback: ${lookback}d • Exclude: ${exclude}d`;
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

function melodaySetMode(value) {
    const normalized = melodayNormalizeMode(value);
    document.querySelectorAll('input[name="meloday-mode"]').forEach((input) => {
        input.checked = input.value === normalized;
    });
}

function melodayGetMode() {
    return melodayNormalizeMode(document.querySelector('input[name="meloday-mode"]:checked')?.value || melodayDefaults.mode);
}

async function loadMelodaySettings() {
    const enabledEl = document.getElementById('meloday-enabled');
    if (!enabledEl) {
        return;
    }
    try {
        const settings = await melodayFetchJson('/api/meloday/settings');
        enabledEl.checked = settings.enabled ?? true;
        melodayState.enabled = enabledEl.checked;
        melodayState.settings = { ...settings, enabled: enabledEl.checked };
        updateMelodayStatusPill();
        const playlistPrefix = document.getElementById('meloday-playlist-prefix');
        const maxTracks = document.getElementById('meloday-max-tracks');
        const lookback = document.getElementById('meloday-lookback-days');
        const exclude = document.getElementById('meloday-exclude-days');
        const updateMinutes = document.getElementById('meloday-update-minutes');
        const similarityDistance = document.getElementById('meloday-similarity-distance');
        const similarLimit = document.getElementById('meloday-similar-limit');
        const historicalRatio = document.getElementById('meloday-historical-ratio');
        melodaySetMode(settings.mode || melodayDefaults.mode);
        if (playlistPrefix) playlistPrefix.value = settings.playlistPrefix || '';
        if (maxTracks) maxTracks.value = settings.maxTracks ?? 50;
        if (lookback) lookback.value = settings.historyLookbackDays ?? 30;
        if (exclude) exclude.value = settings.excludePlayedDays ?? 4;
        if (updateMinutes) updateMinutes.value = settings.updateIntervalMinutes ?? 30;
        if (similarityDistance) similarityDistance.value = settings.sonicSimilarityDistance ?? 0.35;
        if (similarLimit) similarLimit.value = settings.sonicSimilarLimit ?? 8;
        if (historicalRatio) historicalRatio.value = settings.historicalRatio ?? 0.3;
    } catch (error) {
        console.warn('Meloday settings failed to load.', error);
    }
}

function buildMelodayPayload(enabledOverride) {
    const enabledEl = document.getElementById('meloday-enabled');
    return {
        enabled: enabledOverride ?? enabledEl?.checked ?? true,
        playlistPrefix: document.getElementById('meloday-playlist-prefix')?.value || '',
        maxTracks: melodayParseNumber(document.getElementById('meloday-max-tracks')?.value, 50),
        historyLookbackDays: melodayParseNumber(document.getElementById('meloday-lookback-days')?.value, 30),
        excludePlayedDays: melodayParseNumber(document.getElementById('meloday-exclude-days')?.value, 4),
        updateIntervalMinutes: melodayParseNumber(document.getElementById('meloday-update-minutes')?.value, 30),
        sonicSimilarityDistance: melodayParseNumber(document.getElementById('meloday-similarity-distance')?.value, 0.35),
        sonicSimilarLimit: melodayParseNumber(document.getElementById('meloday-similar-limit')?.value, 8),
        historicalRatio: melodayParseNumber(document.getElementById('meloday-historical-ratio')?.value, 0.3),
        mode: melodayGetMode()
    };
}

async function saveMelodaySettings() {
    const saveButton = document.getElementById('saveMelodaySettings');
    if (saveButton?.disabled) {
        return;
    }
    if (saveButton) {
        saveButton.disabled = true;
    }
    const payload = buildMelodayPayload();
    try {
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

function buildMelodayTogglePayload(enabled) {
    if (melodayState.settings) {
        return { ...melodayState.settings, enabled };
    }
    // Fallback to current form/defaults if settings have not been loaded yet
    return buildMelodayPayload(enabled);
}

async function saveMelodayEnabled(enabled) {
    const enabledEl = document.getElementById('meloday-enabled');
    const previous = enabledEl?.checked;
    if (enabledEl) {
        enabledEl.checked = enabled;
    }
    const payload = buildMelodayTogglePayload(enabled);
    try {
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
            notifyActivity(result?.message || 'Meloday playlist updated.');
        } else if (typeof showToast === 'function') {
            showToast(result?.message || 'Meloday playlist updated.');
        }
        melodayLog('info', result?.message || 'Meloday playlist updated.');
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
    }
}

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initializeMelodayCard);
} else {
    initializeMelodayCard();
}
