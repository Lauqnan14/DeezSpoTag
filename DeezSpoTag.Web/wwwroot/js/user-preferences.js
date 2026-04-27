/**
 * UserPrefs — thin wrapper that keeps user preferences in sync with the server.
 *
 * Usage (in any JS file, after this script has loaded):
 *   UserPrefs.set('theme', 'purple');          // persists to server + localStorage
 *   UserPrefs.setRaw('theme', 'purple');        // same but value is already a string
 *
 * The server-side hydration in _Layout.cshtml already pre-populates localStorage
 * on every page load, so no changes to localStorage.getItem() calls are needed.
 */
globalThis.UserPrefs = (function () {
    // In-memory copy seeded from the server-rendered blob injected by _Layout.cshtml
    const _data = globalThis.__userPrefsData || {};
    let _saveTimer = null;
    let _dirty = false;

    // Map from localStorage string key → DTO camelCase field name
    const KEY_TO_FIELD = {
        'deezspotag-theme':                     'theme',
        'sidebarCollapsed':                      'sidebarCollapsed',
        'tabs-preference-enabled':               'tabsPreferenceEnabled',
        'pwa-prompt-dismissed':                  'pwaPromptDismissedAt',
        'autotag-selected-platforms':            'autoTagSelectedPlatforms',
        'autotag-preferences':                   'autoTagPreferences',
        'autotag-active-profile-id':             'autoTagActiveProfileId',
        'download-destination-folder':           'downloadDestinationFolderId',
        'download-destination-folder-stereo':    'downloadDestinationStereoFolderId',
        'download-destination-folder-atmos':     'downloadDestinationAtmosFolderId',
        'apple-download-notification-mode':      'appleDownloadNotificationMode',
        'deezspotag.quicktag.columns':           'quickTagColumns',
        'deezspotag.quicktag.columnPreset':      'quickTagColumnPreset',
        'deezspotag.quicktag.tagSourceProvider': 'quickTagTagSourceProvider',
        'deezspotag.quicktag.sourceTemplate':    'quickTagSourceTemplate',
        'quicktag_left_col':                     'quickTagPanelLeftWidth',
        'quicktag_right_col':                    'quickTagPanelRightWidth',
        'lrc-editor-merge':                      'lrcEditorMerge',
        'libraryAlbumDestinationFolderId':       'libraryAlbumDestinationFolderId',
        'previewVolume':                         'previewVolume',
        'deezspotag-spotify-cache-schedule':     'spotifyCacheSchedule',
        'deezspotag-spotify-cache-last-run':     'spotifyCacheLastRun',
        'multisource_recent_searches':           'spotiflacRecentSearches'
    };

    function _scheduleSave() {
        _dirty = true;
        clearTimeout(_saveTimer);
        _saveTimer = setTimeout(_flush, 1000);
    }

    async function _flush(useBeacon = false) {
        if (!_dirty) return;
        const payload = deepClone(_data);
        _dirty = false;
        try {
            const body = JSON.stringify(payload);
            if (useBeacon && globalThis.navigator && typeof globalThis.navigator.sendBeacon === 'function') {
                const blob = new Blob([body], { type: 'application/json' });
                if (globalThis.navigator.sendBeacon('/api/user-preferences', blob)) {
                    return;
                }
            }

            await fetch('/api/user-preferences', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body,
                credentials: 'same-origin',
                keepalive: useBeacon
            });
        } catch (error) {
            logPreferenceWarning('flush preferences', error);
            // Silent fail — localStorage was already updated, server will sync next load
            _dirty = true;
        }
    }

    /**
     * Set a preference by DTO field name and schedule a server save.
     * Also keeps localStorage in sync so existing code that reads localStorage continues to work.
     *
     * @param {string} field   - DTO camelCase field name (e.g. 'theme', 'previewVolume')
     * @param {*}      value   - The parsed value (string, number, boolean, object, array)
     */
    function set(field, value) {
        _data[field] = value;
        _scheduleSave();
    }

    function setTabSelection(storageKey, targetSelector) {
        if (!storageKey) return;
        if (!_data.tabSelections || typeof _data.tabSelections !== 'object') {
            _data.tabSelections = {};
        }

        if (targetSelector) {
            _data.tabSelections[storageKey] = targetSelector;
        } else {
            delete _data.tabSelections[storageKey];
        }

        _scheduleSave();
    }

    /**
     * Convenience wrapper: accept a raw localStorage string value for a known localStorage key,
     * parse it appropriately, and sync to server.
     *
     * @param {string} lsKey   - The localStorage key (e.g. 'deezspotag-theme')
     * @param {string} rawVal  - The raw string value written to localStorage
     */
    function syncFromLocalStorage(lsKey, rawVal) {
        const field = KEY_TO_FIELD[lsKey];
        if (!field) return;

        // Parse the raw localStorage string back to a proper typed value
        let parsed = rawVal;
        if (rawVal === 'true') parsed = true;
        else if (rawVal === 'false') parsed = false;
        else {
            const n = Number(rawVal);
            if (rawVal !== '' && !Number.isNaN(n)) {
                parsed = n;
            } else {
                try {
                    parsed = JSON.parse(rawVal);
                } catch (error) {
                    logPreferenceWarning('parse localStorage payload', error);
                }
            }
        }

        set(field, parsed);
    }

    function drainPendingPreferenceSync() {
        const pending = Array.isArray(globalThis.__deezspotPendingPrefSync)
            ? globalThis.__deezspotPendingPrefSync
            : [];
        if (pending.length === 0) {
            return;
        }

        globalThis.__deezspotPendingPrefSync = [];
        pending.forEach((entry) => {
            if (!entry || typeof entry.key !== 'string' || typeof entry.value !== 'string') {
                return;
            }

            syncFromLocalStorage(entry.key, entry.value);
        });
    }

    drainPendingPreferenceSync();

    return {
        /** Set by DTO field name */
        set: set,
        /** Persist a remembered tab selection keyed by its localStorage key */
        setTabSelection: setTabSelection,
        /** Sync a raw localStorage write to the server */
        syncFromLocalStorage: syncFromLocalStorage,
        /** Flush immediately (useful before page unload) */
        flush: _flush
    };

    function deepClone(value) {
        if (typeof globalThis.structuredClone === 'function') {
            return globalThis.structuredClone(value);
        }

        if (Array.isArray(value)) {
            return value.map((item) => deepClone(item));
        }

        if (value && typeof value === 'object') {
            const clone = {};
            Object.entries(value).forEach(([key, item]) => {
                clone[key] = deepClone(item);
            });
            return clone;
        }

        return value;
    }

    function logPreferenceWarning(action, error) {
        if (globalThis.console && typeof globalThis.console.debug === 'function') {
            globalThis.console.debug(`[UserPrefs] Failed to ${action}.`, error);
        }
    }
})();

globalThis.addEventListener('pagehide', () => {
    globalThis.UserPrefs?.flush(true);
});

globalThis.addEventListener('beforeunload', () => {
    globalThis.UserPrefs?.flush(true);
});

document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden') {
        globalThis.UserPrefs?.flush(true);
    }
});
