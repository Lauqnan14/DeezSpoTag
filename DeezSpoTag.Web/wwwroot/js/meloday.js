async function melodayFetchJson(url, options) {
    const response = await fetch(url, options);
    if (!response.ok) {
        const message = await response.text();
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
    excludePlayedDays: 4
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
        if (settingsSummaryEl) {
            const tracks = status.maxTracks ?? melodayDefaults.maxTracks;
            const lookback = status.historyLookbackDays ?? melodayDefaults.historyLookbackDays;
            const exclude = status.excludePlayedDays ?? melodayDefaults.excludePlayedDays;
            settingsSummaryEl.textContent = `Tracks: ${tracks} • Lookback: ${lookback}d • Exclude: ${exclude}d`;
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
        const libraryName = document.getElementById('meloday-library-name');
        const playlistPrefix = document.getElementById('meloday-playlist-prefix');
        const maxTracks = document.getElementById('meloday-max-tracks');
        const lookback = document.getElementById('meloday-lookback-days');
        const exclude = document.getElementById('meloday-exclude-days');
        const updateMinutes = document.getElementById('meloday-update-minutes');
        const similarityDistance = document.getElementById('meloday-similarity-distance');
        const similarLimit = document.getElementById('meloday-similar-limit');
        const historicalRatio = document.getElementById('meloday-historical-ratio');

        if (libraryName) libraryName.value = settings.libraryName || '';
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
        libraryName: document.getElementById('meloday-library-name')?.value || '',
        playlistPrefix: document.getElementById('meloday-playlist-prefix')?.value || '',
        maxTracks: melodayParseNumber(document.getElementById('meloday-max-tracks')?.value, 50),
        historyLookbackDays: melodayParseNumber(document.getElementById('meloday-lookback-days')?.value, 30),
        excludePlayedDays: melodayParseNumber(document.getElementById('meloday-exclude-days')?.value, 4),
        updateIntervalMinutes: melodayParseNumber(document.getElementById('meloday-update-minutes')?.value, 30),
        sonicSimilarityDistance: melodayParseNumber(document.getElementById('meloday-similarity-distance')?.value, 0.35),
        sonicSimilarLimit: melodayParseNumber(document.getElementById('meloday-similar-limit')?.value, 8),
        historicalRatio: melodayParseNumber(document.getElementById('meloday-historical-ratio')?.value, 0.3)
    };
}

async function saveMelodaySettings() {
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
    if (!button) {
        return;
    }
    button.disabled = true;
    try {
        await melodayFetchJson('/api/meloday/run', { method: 'POST' });
        if (typeof notifyActivity === 'function') {
            notifyActivity('Meloday playlist updated.');
        } else if (typeof showToast === 'function') {
            showToast('Meloday playlist updated.');
        }
        melodayLog('info', 'Meloday playlist updated.');
        await loadMelodayStatus();
    } catch (error) {
        if (typeof notifyActivity === 'function') {
            notifyActivity(`Meloday failed: ${error.message}`, 'error');
        } else if (typeof showToast === 'function') {
            showToast(`Meloday failed: ${error.message}`, true);
        }
        melodayLog('error', `Meloday failed: ${error.message}`);
    } finally {
        button.disabled = false;
    }
}

document.addEventListener('DOMContentLoaded', () => {
    // Use the status pill as the presence check now that the text block is gone
    if (document.getElementById('melodayStatusPill')) {
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
});
