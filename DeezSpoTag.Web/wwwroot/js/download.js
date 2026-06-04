/**
 * DeezSpoTag Download Engine Integration
 * Connects UI to the Deezer download engine
 */

globalThis.DeezSpoTag = globalThis.DeezSpoTag || {};

DeezSpoTag.Download = {
    APPLE_NOTIFICATION_MODE_KEY: 'apple-download-notification-mode',
    DOWNLOAD_SOURCE_SETTING_KEY: 'deezspotag-download-source-updated',
    csrfUnsafeMethods: new Set(['POST', 'PUT', 'PATCH', 'DELETE']),
    // Tracklist/search pages can enqueue downloads, but Activities owns all queue UI.
    queue: {
        isProcessing: false,
        resumeNotified: false
    },
    engineById: {},
    inFlightLockTimeoutMs: 45000,
    inFlightSequence: 0,
    inFlightByUrl: {},
    settings: null,
    settingsPromise: null,
    appleNotifications: {
        mode: 'detailed',
        started: 0,
        completed: 0,
        failed: 0,
        timer: null
    },

    // Initialize download functionality
    init() {
        this.activitiesPage = this.isActivitiesPage();
        this.bindEvents();
        this.refreshAppleNotificationMode();
        this.ensureDestinationSelects();
        this.ensureSettingsLoaded();
        this.logger = DeezSpoTag.DownloadLogger || null;
        globalThis.addEventListener('deezspotag:settings-updated', (event) => {
            this.applyUpdatedSettings(event?.detail?.settings || null);
        });
        globalThis.addEventListener('storage', (event) => {
            if (event.key === this.APPLE_NOTIFICATION_MODE_KEY) {
                this.refreshAppleNotificationMode();
            } else if (event.key === this.DOWNLOAD_SOURCE_SETTING_KEY) {
                this.applyUpdatedDownloadSource(event.newValue);
            }
        });
        this.resumePendingQueue();
        console.log('DeezSpoTag Download Engine initialized');
    },
    isActivitiesPage() {
        return /^\/activities(?:\/|$)/i.test(globalThis.location?.pathname || '');
    },

    // Bind download-related events
    bindEvents() {
        document.addEventListener('click', (e) => {
            const trigger = e.target.closest('[data-download-url]');
            if (!trigger) {
                return;
            }

            e.preventDefault();
            const url = trigger.dataset.downloadUrl || '';
            const bitrate = Number(trigger.dataset.bitrate || 0) || 0;
            this.addToQueue(url, bitrate);
        });

        document.addEventListener('submit', (e) => {
            const form = e.target.closest('.download-form');
            if (!form) {
                return;
            }

            e.preventDefault();
            const url = form.querySelector('[name="url"]')?.value || '';
            const bitrate = Number(form.querySelector('[name="bitrate"]')?.value || 0) || 0;
            this.addToQueue(url, bitrate);
        });
    },
    readCsrfRequestToken() {
        const tokenMeta = document.querySelector('meta[name="deezspotag-csrf-token"]');
        const token = tokenMeta?.getAttribute('content');
        return typeof token === 'string' ? token.trim() : '';
    },
    buildCsrfFetchOptions(options) {
        const requestOptions = options ? { ...options } : {};
        const method = String(requestOptions.method || 'GET').toUpperCase();
        if (!this.csrfUnsafeMethods.has(method)) {
            return requestOptions;
        }

        const headers = new Headers(requestOptions.headers || {});
        if (!headers.has('X-CSRF-TOKEN')) {
            const csrfToken = this.readCsrfRequestToken();
            if (csrfToken) {
                headers.set('X-CSRF-TOKEN', csrfToken);
            }
        }

        requestOptions.headers = headers;
        if (!requestOptions.credentials) {
            requestOptions.credentials = 'same-origin';
        }
        return requestOptions;
    },
    apiFetch(resource, options) {
        return globalThis.fetch(resource, this.buildCsrfFetchOptions(options));
    },
    normalizeDestinationContentMode(contentMode) {
        const normalized = String(contentMode || '').trim().toLowerCase();
        switch (normalized) {
            case 'music':
            case 'audio':
            case 'stereo':
            case 'track':
                return 'music';
            case 'atmos':
                return 'atmos';
            case 'video':
            case 'music-video':
            case 'music_videos':
                return 'video';
            case 'podcast':
            case 'show':
            case 'episode':
                return 'podcast';
            default:
                return 'all';
        }
    },
    isFolderEnabled(folder) {
        const value = folder?.enabled;
        const normalized = String(value ?? '').trim().toLowerCase();
        if (!normalized) {
            return true;
        }
        if (typeof value === 'boolean') {
            return value;
        }
        if (typeof value === 'number') {
            return value !== 0;
        }
        return !/^(false|0|no|off|disabled)$/i.test(normalized);
    },
    isFolderAutoTagActivated(folder) {
        const profileId = String(folder?.autoTagProfileId ?? '').trim();
        return profileId.length > 0;
    },
    isDestinationFolderEligible(folder, contentMode = 'all') {
        if (!this.isFolderEnabled(folder)) {
            return false;
        }

        const mode = this.normalizeDestinationContentMode(contentMode);
        if (mode === 'music' || mode === 'all') {
            const desiredQuality = String(folder?.desiredQuality ?? '').trim().toLowerCase();
            const isVideoFolder = desiredQuality === 'video';
            const isPodcastFolder = desiredQuality === 'podcast';
            if (!isVideoFolder && !isPodcastFolder) {
                return this.isFolderAutoTagActivated(folder);
            }
        }

        return true;
    },
    getPreferredDestinationSelect() {
        const selects = Array.from(document.querySelectorAll('.download-destination-select'));
        if (!selects.length) {
            return null;
        }

        const visible = selects.find((select) => {
            if (!(select instanceof HTMLSelectElement)) {
                return false;
            }

            if (select.disabled) {
                return false;
            }

            const style = globalThis.getComputedStyle ? globalThis.getComputedStyle(select) : null;
            if (style && (style.display === 'none' || style.visibility === 'hidden')) {
                return false;
            }

            if (select.closest('[hidden], .d-none, [aria-hidden=\"true\"]')) {
                return false;
            }

            return true;
        });

        return visible || selects[0] || null;
    },
    persistDestinationPreference(storageKey, value) {
        localStorage.setItem(storageKey, value);
        if (!globalThis.UserPrefs || !value) {
            return;
        }

        if (storageKey === 'download-destination-folder-stereo') {
            globalThis.UserPrefs.set('downloadDestinationStereoFolderId', value);
            return;
        }
        if (storageKey === 'download-destination-folder-atmos') {
            globalThis.UserPrefs.set('downloadDestinationAtmosFolderId', value);
            return;
        }
        globalThis.UserPrefs.set('downloadDestinationFolderId', value);
    },
    appendDestinationPlaceholder(select, hasFolders) {
        const placeholder = document.createElement('option');
        placeholder.value = '';
        placeholder.textContent = hasFolders ? 'Select destination folder' : 'No destination folders configured';
        select.appendChild(placeholder);
    },
    appendDestinationFolderOptions(select, folders) {
        folders.forEach((folder) => {
            const option = document.createElement('option');
            option.value = String(folder.id);
            option.textContent = folder.displayName || folder.rootPath || `Folder ${folder.id}`;
            select.appendChild(option);
        });
    },
    getStoredDestinationValue(storageKey) {
        const explicitValue = localStorage.getItem(storageKey);
        if (explicitValue) {
            return explicitValue;
        }

        if (storageKey === 'download-destination-folder') {
            return '';
        }

        return localStorage.getItem('download-destination-folder') || '';
    },
    resolveInitialDestinationValue(select, folders, stored) {
        const hasStoredOption = stored
            && Array.from(select.options || []).some((option) => String(option.value) === String(stored));
        if (hasStoredOption) {
            return String(stored);
        }
        if (folders.length === 1) {
            return String(folders[0].id);
        }
        return '';
    },
    bindDestinationSelectChange(select, storageKey) {
        if (select.dataset.destinationChangeBound === 'true') {
            return;
        }
        select.dataset.destinationChangeBound = 'true';
        select.addEventListener('change', () => {
            const value = select.value || '';
            this.persistDestinationPreference(storageKey, value);
        });
    },
    async configureDestinationSelect(select) {
        const contentMode = this.normalizeDestinationContentMode(select.dataset.destinationContentMode);
        if (select.dataset.destinationLoadedForMode === contentMode) {
            return;
        }

        const folders = await this.loadDestinationFolders(contentMode);
        select.dataset.destinationLoaded = 'true';
        select.dataset.destinationLoadedForMode = contentMode;
        select.innerHTML = '';

        const storageKey = select.dataset.destinationStorageKey || 'download-destination-folder';
        const stored = this.getStoredDestinationValue(storageKey);
        this.appendDestinationPlaceholder(select, folders.length > 0);
        this.appendDestinationFolderOptions(select, folders);

        const selectedValue = this.resolveInitialDestinationValue(select, folders, stored);
        if (selectedValue) {
            select.value = selectedValue;
            this.persistDestinationPreference(storageKey, selectedValue);
        }
        this.bindDestinationSelectChange(select, storageKey);
    },
    async ensureDestinationSelects() {
        const selects = Array.from(document.querySelectorAll('.download-destination-select'));
        if (!selects.length) {
            return;
        }

        await Promise.all(selects.map(async (select) => {
            try {
                await this.configureDestinationSelect(select);
            } catch (error) {
                console.warn('Failed to load destination folders', error);
            }
        }));
    },

    async loadDestinationFolders(contentMode = 'all') {
        const normalizedMode = this.normalizeDestinationContentMode(contentMode);
        this.destinationFoldersByMode = this.destinationFoldersByMode || {};
        if (this.destinationFoldersByMode[normalizedMode]) {
            return this.destinationFoldersByMode[normalizedMode];
        }

        try {
            const params = new URLSearchParams();
            params.set('downloadOnly', 'true');
            if (normalizedMode !== 'all') {
                params.set('contentType', normalizedMode);
            }
            const response = await this.apiFetch(`/api/library/folders?${params.toString()}`);
            if (!response.ok) {
                throw new Error(`Failed to load folders (${response.status})`);
            }
            const folders = await response.json();
            this.destinationFoldersByMode[normalizedMode] = Array.isArray(folders)
                ? folders.filter((folder) => this.isDestinationFolderEligible(folder, normalizedMode))
                : [];
        } catch (error) {
            console.warn('Failed to load destination folders', error);
            this.destinationFoldersByMode[normalizedMode] = [];
        }

        return this.destinationFoldersByMode[normalizedMode];
    },
    async ensureSettingsLoaded() {
        if (this.settings) {
            return this.settings;
        }
        if (this.settingsPromise) {
            return this.settingsPromise;
        }

        this.settingsPromise = (async () => {
            try {
                const response = await this.apiFetch('/api/settings');
                if (!response.ok) {
                    throw new Error(`Failed to load settings (${response.status})`);
                }
                const payload = await response.json();
                this.settings = payload?.settings || payload?.settings?.settings || payload || null;
                return this.settings;
            } catch (error) {
                console.warn('Failed to load settings', error);
                this.settings = null;
                return null;
            }
        })();

        return this.settingsPromise;
    },
    applyUpdatedSettings(settings) {
        const nextSettings = settings && typeof settings === 'object'
            ? this.cloneSettingsPayload(settings)
            : null;
        this.settings = nextSettings;
        this.settingsPromise = nextSettings
            ? Promise.resolve(nextSettings)
            : null;
    },
    applyUpdatedDownloadSource(serialized) {
        let payload = null;
        try {
            payload = serialized ? JSON.parse(serialized) : null;
        } catch {
            payload = null;
        }

        const service = String(payload?.service || '').trim().toLowerCase();
        if (!service) {
            this.settings = null;
            this.settingsPromise = null;
            return;
        }

        const nextSettings = this.settings && typeof this.settings === 'object'
            ? this.cloneSettingsPayload(this.settings)
            : {};
        nextSettings.service = service;
        this.settings = nextSettings;
        this.settingsPromise = Promise.resolve(nextSettings);
    },
    cloneSettingsPayload(value) {
        if (typeof structuredClone === 'function') {
            return structuredClone(value);
        }
        if (Array.isArray(value)) {
            return value.map((entry) => this.cloneSettingsPayload(entry));
        }
        if (value && typeof value === 'object') {
            return Object.fromEntries(
                Object.entries(value).map(([key, entry]) => [key, this.cloneSettingsPayload(entry)])
            );
        }
        return value;
    },
    normalizeAppleNotificationMode(mode) {
        const normalized = String(mode || '').trim().toLowerCase();
        if (normalized === 'off' || normalized === 'merged' || normalized === 'detailed') {
            return normalized;
        }
        return 'detailed';
    },
    refreshAppleNotificationMode() {
        const stored = localStorage.getItem(this.APPLE_NOTIFICATION_MODE_KEY);
        const nextMode = this.normalizeAppleNotificationMode(stored);
        if (this.appleNotifications.mode === 'merged' && nextMode !== 'merged') {
            this.flushAppleNotificationSummary();
        }
        this.appleNotifications.mode = nextMode;
    },
    getAppleNotificationMode() {
        return this.normalizeAppleNotificationMode(this.appleNotifications.mode);
    },
    queueAppleNotification(kind) {
        if (kind === 'started') {
            this.appleNotifications.started += 1;
        } else if (kind === 'completed') {
            this.appleNotifications.completed += 1;
        } else if (kind === 'failed') {
            this.appleNotifications.failed += 1;
        }

        if (this.appleNotifications.timer) {
            return;
        }

        this.appleNotifications.timer = setTimeout(() => {
            this.flushAppleNotificationSummary();
        }, 1500);
    },
    flushAppleNotificationSummary() {
        const pending = this.appleNotifications;
        if (pending.timer) {
            clearTimeout(pending.timer);
            pending.timer = null;
        }

        const parts = [];
        if (pending.started > 0) {
            parts.push(`${pending.started} started`);
        }
        if (pending.completed > 0) {
            parts.push(`${pending.completed} completed`);
        }
        if (pending.failed > 0) {
            parts.push(`${pending.failed} failed`);
        }

        if (parts.length) {
            let type = 'info';
            if (pending.failed > 0) {
                type = 'error';
            } else if (pending.completed > 0) {
                type = 'success';
            }
            this.showNotification(`Downloads: ${parts.join(', ')}`, type);
        }

        pending.started = 0;
        pending.completed = 0;
        pending.failed = 0;
    },

    getDestinationFolderId(requireSelection = false) {
        const select = this.getPreferredDestinationSelect();
        if (!select) {
            return null;
        }

        const value = (select.value || '').trim();
        if (!value && requireSelection) {
            this.showNotification('Select a destination folder before downloading.', 'warning');
            return null;
        }

        if (!value) {
            return null;
        }

        return /^\d+$/.test(value) ? Number(value) : value;
    },

    getAtmosDestinationFolderId() {
        const raw = this.settings?.multiQuality?.secondaryDestinationFolderId;
        if (raw === null || raw === undefined) {
            return null;
        }

        const value = String(raw).trim();
        if (!value) {
            return null;
        }

        return value;
    },

    async preloadAtmosDestinationFolder(shouldApply = false) {
        if (!shouldApply) {
            return null;
        }

        await this.ensureSettingsLoaded();
        const atmosFolderId = this.getAtmosDestinationFolderId();
        if (atmosFolderId === null || atmosFolderId === undefined || atmosFolderId === '') {
            return null;
        }

        await this.ensureDestinationSelects();
        const selects = Array.from(document.querySelectorAll('.download-destination-select'));
        if (!selects.length) {
            return null;
        }

        const targetValue = String(atmosFolderId);
        let applied = false;
        selects.forEach((select) => {
            const hasOption = Array.from(select.options || []).some((option) => String(option.value) === targetValue);
            if (!hasOption) {
                return;
            }

            select.value = targetValue;
            applied = true;
        });

        if (!applied) {
            return null;
        }

        localStorage.setItem('download-destination-folder', targetValue);
        if (globalThis.UserPrefs) globalThis.UserPrefs.set('downloadDestinationFolderId', targetValue);
        return atmosFolderId;
    },
    clearPendingDownload(url, bitrate, destinationFolderId, options) {
        if (!options?.skipPending) {
            this.removePendingQueueItem({ url, bitrate, destinationFolderId });
        }
    },
    handleAlreadyQueuedApiError(response, apiError, {
        url,
        bitrate,
        destinationFolderId,
        options,
        message = 'Item already queued',
        notificationType = 'warning',
        linkType = '',
        returnsSuccess = true
    }) {
        if (response.status !== 400 || !apiError?.alreadyQueued) {
            return null;
        }

        const resolvedMessage = apiError.message || message;
        this.showNotification(resolvedMessage, notificationType);
        this.logDownloadEvent('info', resolvedMessage);
        this.clearPendingDownload(url, bitrate, destinationFolderId, options);
        let reasonCodes = [];
        if (Array.isArray(apiError?.reasonCodes)) {
            reasonCodes = apiError.reasonCodes;
        } else if (apiError?.reasonCode) {
            reasonCodes = [apiError.reasonCode];
        }
        return returnsSuccess
            ? { success: true, alreadyQueued: true, errorMessage: resolvedMessage, linkType, reasonCodes }
            : { success: false, errorMessage: resolvedMessage, linkType, reasonCodes };
    },
    handleAlreadyQueuedResult(result, {
        url,
        bitrate,
        destinationFolderId,
        options,
        message = 'Item already queued',
        notificationType = 'warning',
        linkType = ''
    }) {
        if (!result?.alreadyQueued) {
            return null;
        }

        const resolvedMessage = result.message || message;
        this.showNotification(resolvedMessage, notificationType);
        this.logDownloadEvent('info', resolvedMessage);
        this.clearPendingDownload(url, bitrate, destinationFolderId, options);
        let reasonCodes = [];
        if (Array.isArray(result?.reasonCodes)) {
            reasonCodes = result.reasonCodes;
        } else if (result?.reasonCode) {
            reasonCodes = [result.reasonCode];
        } else {
            reasonCodes = Object.keys(result?.skippedReasons || {});
        }
        return { success: true, alreadyQueued: true, linkType, reasonCodes };
    },
    handleQueuedResult(result, {
        url,
        bitrate,
        destinationFolderId,
        options,
        queueType,
        engine,
        linkType,
        logLabel
    }) {
        if (!Array.isArray(result?.queued) || result.queued.length === 0) {
            return null;
        }

        this.showNotification(`Added ${result.queued.length} item(s) to the queue`, 'success');
        this.logDownloadEvent('success', `added to queue (${logLabel})`);
        this.clearPendingDownload(url, bitrate, destinationFolderId, options);
        return {
            success: true,
            downloadId: result.queued[0],
            downloadIds: result.queued,
            linkType
        };
    },
    handleDeferredResult(result, {
        url,
        bitrate,
        destinationFolderId,
        options,
        linkType,
        message,
        logMessage = 'queued for background intent resolution'
    }) {
        if (!result?.deferred) {
            return null;
        }

        const deferredCount = Number.isFinite(result.deferredCount) ? result.deferredCount : 1;
        const resolvedMessage = result.message || message || `Queued ${deferredCount} item(s) for background processing.`;
        this.showNotification(resolvedMessage, 'info');
        this.logDownloadEvent('info', logMessage);
        this.clearPendingDownload(url, bitrate, destinationFolderId, options);
        return { success: true, deferred: true, linkType };
    },
    async enqueueStandardDownloadRequest({
        endpoint,
        body,
        url,
        bitrate,
        destinationFolderId,
        options,
        linkType,
        queueType,
        engine,
        logLabel,
        alreadyQueuedReturnsSuccess = false
    }) {
        const response = await this.apiFetch(endpoint, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
            },
            keepalive: true,
            body: JSON.stringify(body)
        });

        if (!response.ok) {
            const apiError = await this.buildApiError(response);
            const alreadyQueuedError = this.handleAlreadyQueuedApiError(response, apiError, {
                url,
                bitrate,
                destinationFolderId,
                options,
                linkType,
                returnsSuccess: alreadyQueuedReturnsSuccess
            });
            if (alreadyQueuedError) {
                return alreadyQueuedError;
            }
            throw apiError;
        }

        const result = await response.json();
        const alreadyQueuedResult = this.handleAlreadyQueuedResult(result, {
            url,
            bitrate,
            destinationFolderId,
            options,
            linkType
        });
        if (alreadyQueuedResult) {
            return alreadyQueuedResult;
        }

        const queuedResult = this.handleQueuedResult(result, {
            url,
            bitrate,
            destinationFolderId,
            options,
            queueType,
            engine,
            linkType,
            logLabel
        });
        if (queuedResult) {
            return queuedResult;
        }

        throw this.buildResultError(result, 'Failed to add to queue');
    },

    createQueueNotifier(options = {}) {
        return {
            notify: (message, type = 'info', notificationOptions = {}) => {
                if (!options.silent) {
                    this.showNotification(message, type, notificationOptions);
                }
            },
            notifyQueue: (message, type = 'success') => {
                if (!options.silent) {
                    this.showQueueToast(message, type);
                }
            }
        };
    },
    cleanupStaleInFlightLocks() {
        const now = Date.now();
        const timeoutMs = Number(this.inFlightLockTimeoutMs) || 45000;
        Object.keys(this.inFlightByUrl || {}).forEach((url) => {
            const lock = this.inFlightByUrl[url];
            const startedAt = Number(lock?.startedAt || 0);
            if (!startedAt || (now - startedAt) > timeoutMs) {
                delete this.inFlightByUrl[url];
            }
        });
    },
    getActiveInFlightLock(normalizedUrl) {
        this.cleanupStaleInFlightLocks();
        const lock = this.inFlightByUrl[normalizedUrl];
        return lock && typeof lock === 'object' ? lock : null;
    },
    acquireInFlightLock(normalizedUrl, destinationId) {
        const lockId = `${Date.now()}-${++this.inFlightSequence}`;
        this.inFlightByUrl[normalizedUrl] = {
            id: lockId,
            startedAt: Date.now(),
            destinationId: destinationId ?? null
        };
        return lockId;
    },
    releaseInFlightLock(normalizedUrl, lockId) {
        const activeLock = this.inFlightByUrl[normalizedUrl];
        if (!activeLock) {
            return;
        }
        if (lockId && activeLock.id && activeLock.id !== lockId) {
            return;
        }
        delete this.inFlightByUrl[normalizedUrl];
    },
    validateQueueInput(url, normalizedUrl, destinationFolderId, destinationId, notify) {
        if (!url) {
            const errorMessage = 'Please provide a valid URL';
            notify(errorMessage, 'error');
            return { success: false, errorMessage };
        }
        const activeLock = this.getActiveInFlightLock(normalizedUrl);
        if (activeLock) {
            const errorMessage = 'Download already queued or in progress';
            notify(errorMessage, 'warning');
            this.logDownloadEvent('info', errorMessage);
            return { success: false, errorMessage };
        }
        if (destinationFolderId === null && destinationId === null && document.querySelector('.download-destination-select')) {
            return { success: false, errorMessage: 'Destination folder required' };
        }
        return null;
    },
    addPendingQueueIfNeeded(url, bitrate, destinationId, options = {}) {
        if (!options.skipPending && !options.pendingPrequeued) {
            this.addPendingQueueItem({
                url,
                bitrate,
                destinationFolderId: destinationId,
                metadata: options?.metadata
            });
        }
    },
    removePendingQueueIfNeeded(url, bitrate, destinationId, options = {}) {
        if (!options.skipPending) {
            this.removePendingQueueItem({ url, bitrate, destinationFolderId: destinationId });
        }
    },
    buildIntentContext(options = {}) {
        const allowQualityUpgrade = options?.allowQualityUpgrade === true
            || options?.metadata?.allowQualityUpgrade === true;
        const preferredEngine = this.getPreferredEngineFromUi();
        const preferredQuality = this.getPreferredQualityForEngine(preferredEngine);
        return {
            allowQualityUpgrade,
            preferredEngine,
            preferredQuality,
            shouldUseIntentRouting: (sourceService) => {
                const configured = String(preferredEngine || '').trim().toLowerCase();
                if (!configured) return false;
                if (configured === 'auto') return true;
                return configured !== String(sourceService || '').trim().toLowerCase();
            }
        };
    },
    async enqueueIntentWithPreference({ sourceService, sourceUrl, isrc, url, bitrate, destinationId, options, intentContext, notify, notifyQueue, resolveImmediately = true }) {
        const metadata = options?.metadata && typeof options.metadata === 'object'
            ? options.metadata
            : null;
        const response = await this.apiFetch('/api/download/intent', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
            },
            keepalive: true,
            body: JSON.stringify({
                resolveImmediately,
                intents: [{
                    sourceService,
                    sourceUrl: sourceUrl || undefined,
                    isrc: isrc || metadata?.isrc || undefined,
                    preferredEngine: intentContext.preferredEngine || undefined,
                    quality: intentContext.preferredQuality || undefined,
                    contentType: metadata?.contentType || undefined,
                    hasAtmos: metadata?.hasAtmos === true || metadata?.hasAtmos === 'true',
                    title: metadata?.title || undefined,
                    artist: metadata?.artist || undefined,
                    album: metadata?.album || undefined,
                    albumArtist: metadata?.albumArtist || undefined,
                    cover: metadata?.cover || undefined,
                    durationMs: Number(metadata?.durationMs || 0) || undefined,
                    position: Number(metadata?.position || 0) || undefined,
                    allowQualityUpgrade: intentContext.allowQualityUpgrade
                }],
                destinationFolderId: destinationId
            })
        });

        if (!response.ok) {
            throw await this.buildApiError(response);
        }

        const result = await response.json();
        if (Array.isArray(result.queued) && result.queued.length > 0) {
            notifyQueue(`Added ${result.queued.length} item(s) to the queue`, 'success');
            const resolvedEngine = this.resolveEngine(result.engine, sourceService);
            this.logDownloadEvent('success', `added to queue (${resolvedEngine || 'intent'})`);
            this.removePendingQueueIfNeeded(url, bitrate, destinationId, options);
            return {
                success: true,
                downloadId: result.queued[0],
                downloadIds: result.queued,
                linkType: result.engine || intentContext.preferredEngine || sourceService
            };
        }

        if (result?.deferred) {
            const deferredCount = Number.isFinite(result.deferredCount) ? result.deferredCount : 1;
            const message = result.message || `Queued ${deferredCount} item(s) for background processing.`;
            notifyQueue(message, 'info');
            this.logDownloadEvent('info', 'queued for background intent resolution');
            this.removePendingQueueIfNeeded(url, bitrate, destinationId, options);
            return {
                success: true,
                deferred: true,
                linkType: result.engine || intentContext.preferredEngine || sourceService
            };
        }

        const reasonCodes = this.getReasonCodes(result);
        const message = this.resolveApiMessage(result, 'Item already queued');
        if (this.isSkipReason(reasonCodes, message)) {
            notify(message, 'info');
            this.logDownloadEvent('info', message);
            this.removePendingQueueIfNeeded(url, bitrate, destinationId, options);
            return {
                success: true,
                alreadyQueued: true,
                reasonCodes,
                linkType: result.engine || intentContext.preferredEngine || sourceService
            };
        }

        throw this.buildResultError(result, 'Failed to add to queue');
    },
    async tryHandleQobuzIsrcDownload(context) {
        const qobuzIsrc = this.extractIsrcToken(context.url);
        if (!qobuzIsrc) {
            return null;
        }
        if (context.intentContext.shouldUseIntentRouting('qobuz')) {
            return this.enqueueIntentWithPreference({
                sourceService: 'qobuz',
                sourceUrl: '',
                isrc: qobuzIsrc,
                ...context
            });
        }
        return this.enqueueStandardDownloadRequest({
            endpoint: '/api/qobuz/download',
            body: {
                tracks: [{ isrc: qobuzIsrc }],
                destinationFolderId: context.destinationId,
                quality: context.intentContext.preferredQuality || this.settings?.qobuzQuality || '27'
            },
            url: context.url,
            bitrate: context.bitrate,
            destinationFolderId: context.destinationId,
            options: context.options,
            linkType: 'qobuz',
            queueType: 'qobuz',
            engine: 'qobuz',
            logLabel: 'qobuz isrc',
            alreadyQueuedReturnsSuccess: false
        });
    },
    async tryHandleSimpleServiceDownload(context, {
        service,
        endpoint,
        logLabel,
        urlPredicate,
        buildBody
    }) {
        if (!urlPredicate.call(this, context.url)) {
            return null;
        }
        if (context.intentContext.shouldUseIntentRouting(service)) {
            return this.enqueueIntentWithPreference({
                sourceService: service,
                sourceUrl: context.url,
                ...context
            });
        }
        return this.enqueueStandardDownloadRequest({
            endpoint,
            body: buildBody.call(this, context),
            url: context.url,
            bitrate: context.bitrate,
            destinationFolderId: context.destinationId,
            options: context.options,
            linkType: service,
            queueType: service,
            engine: service,
            logLabel,
            alreadyQueuedReturnsSuccess: false
        });
    },
    async tryHandleAmazonDownload(context) {
        return this.tryHandleSimpleServiceDownload(context, {
            service: 'amazon',
            endpoint: '/api/amazon/download',
            logLabel: 'amazon',
            urlPredicate: this.isAmazonUrl,
            buildBody: (ctx) => ({
                tracks: [{ sourceUrl: ctx.url }],
                destinationFolderId: ctx.destinationId
            })
        });
    },
    async tryHandleQobuzDownload(context) {
        if (!this.isQobuzUrl(context.url)) {
            return null;
        }
        if (context.intentContext.shouldUseIntentRouting('qobuz')) {
            return this.enqueueIntentWithPreference({
                sourceService: 'qobuz',
                sourceUrl: context.url,
                ...context
            });
        }
        return this.enqueueStandardDownloadRequest({
            endpoint: '/api/qobuz/download',
            body: {
                tracks: [{ sourceUrl: context.url }],
                destinationFolderId: context.destinationId,
                quality: context.intentContext.preferredQuality || this.settings?.qobuzQuality || '27'
            },
            url: context.url,
            bitrate: context.bitrate,
            destinationFolderId: context.destinationId,
            options: context.options,
            linkType: 'qobuz',
            queueType: 'qobuz',
            engine: 'qobuz',
            logLabel: 'qobuz',
            alreadyQueuedReturnsSuccess: false
        });
    },
    async tryHandleTidalDownload(context) {
        return this.tryHandleSimpleServiceDownload(context, {
            service: 'tidal',
            endpoint: '/api/tidal/download',
            logLabel: 'tidal',
            urlPredicate: this.isTidalUrl,
            buildBody: (ctx) => ({
                tracks: [{ sourceUrl: ctx.url }],
                destinationFolderId: ctx.destinationId
            })
        });
    },
    buildAppleTrackPayload(options = {}) {
        const metadata = options?.metadata && typeof options.metadata === 'object'
            ? { ...options.metadata }
            : null;
        const hasAtmos = metadata?.hasAtmos === true || metadata?.hasAtmos === 'true';
        if (metadata) {
            metadata.hasAtmos = Boolean(hasAtmos);
        }
        const trackPayload = { appleUrl: options.url };
        if (metadata) {
            trackPayload.metadata = metadata;
        }
        return { trackPayload, metadata, hasAtmos };
    },
    isAppleVideoDownload(url, metadata) {
        return this.isAppleVideoUrl(url)
            || metadata?.isVideo === true
            || ['music-video', 'music-videos', 'video'].includes(String(metadata?.collectionType || '').toLowerCase())
            || String(metadata?.contentType || '').toLowerCase() === 'video';
    },
    async enqueueAppleVideoDownload({
        trackPayload,
        url,
        bitrate,
        destinationId,
        secondaryDestinationFolderId,
        options,
        allowQualityUpgrade
    }) {
        const response = await this.apiFetch('/api/apple/videos/download', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
            },
            keepalive: true,
            body: JSON.stringify({
                tracks: [trackPayload],
                destinationFolderId: destinationId,
                secondaryDestinationFolderId,
                allowQualityUpgrade
            })
        });

        if (!response.ok) {
            const apiError = await this.buildApiError(response);
            const alreadyQueuedError = this.handleAlreadyQueuedApiError(response, apiError, {
                url,
                bitrate,
                destinationFolderId: destinationId,
                options,
                notificationType: 'info',
                linkType: 'apple-video'
            });
            if (alreadyQueuedError) return alreadyQueuedError;
            throw apiError;
        }

        const result = await response.json();
        const alreadyQueuedResult = this.handleAlreadyQueuedResult(result, {
            url,
            bitrate,
            destinationFolderId: destinationId,
            options,
            notificationType: 'info',
            linkType: 'apple-video'
        });
        if (alreadyQueuedResult) return alreadyQueuedResult;

        const queuedResult = this.handleQueuedResult(result, {
            url,
            bitrate,
            destinationFolderId: destinationId,
            options,
            queueType: 'apple-video',
            engine: 'apple',
            linkType: 'apple-video',
            logLabel: 'apple-video'
        });
        if (queuedResult) return queuedResult;
        throw this.buildResultError(result, 'Failed to add to queue');
    },
    resolveAppleIntentQuality(metadata, preferredQuality) {
        const requestedContentType = String(metadata?.contentType || '').trim().toLowerCase();
        const normalizedPreferredQuality = String(preferredQuality || '').trim().toLowerCase();
        let intentQuality = preferredQuality || undefined;
        if (requestedContentType === 'atmos') {
            intentQuality = 'atmos';
        } else if (requestedContentType === 'stereo' && normalizedPreferredQuality.includes('atmos')) {
            intentQuality = undefined;
        }
        return {
            requestedContentType,
            intentQuality
        };
    },
    buildAppleIntentPayload({
        url,
        metadata,
        hasAtmos,
        intentContext,
        destinationId,
        secondaryDestinationFolderId
    }) {
        const { requestedContentType, intentQuality } = this.resolveAppleIntentQuality(
            metadata,
            intentContext.preferredQuality
        );
        return {
            resolveImmediately: true,
            intents: [{
                sourceService: 'apple',
                sourceUrl: url,
                preferredEngine: intentContext.preferredEngine || undefined,
                quality: intentQuality,
                contentType: requestedContentType || undefined,
                hasAtmos: Boolean(hasAtmos),
                title: metadata?.title || undefined,
                artist: metadata?.artist || undefined,
                album: metadata?.album || undefined,
                albumArtist: metadata?.albumArtist || undefined,
                isrc: metadata?.isrc || undefined,
                cover: metadata?.cover || undefined,
                durationMs: Number(metadata?.durationMs || 0) || undefined,
                position: Number(metadata?.position || 0) || undefined,
                allowQualityUpgrade: intentContext.allowQualityUpgrade
            }],
            destinationFolderId: destinationId,
            secondaryDestinationFolderId
        };
    },
    async postDownloadIntent(payload) {
        const response = await this.apiFetch('/api/download/intent', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
            },
            keepalive: true,
            body: JSON.stringify(payload)
        });
        if (!response.ok) {
            throw await this.buildApiError(response);
        }
        return response.json();
    },
    handleAppleIntentResult(result, context, metadata) {
        const resolvedEngine = this.resolveEngine(result.engine, metadata?.sourceService || 'apple');
        const preferredLinkType = result.engine || context.intentContext.preferredEngine || 'apple';
        const queuedResult = this.handleQueuedResult(result, {
            url: context.url,
            bitrate: context.bitrate,
            destinationFolderId: context.destinationId,
            options: context.options,
            queueType: resolvedEngine || 'apple',
            engine: resolvedEngine || '',
            linkType: preferredLinkType,
            logLabel: resolvedEngine || 'intent'
        });
        if (queuedResult) return queuedResult;

        const deferredResult = this.handleDeferredResult(result, {
            url: context.url,
            bitrate: context.bitrate,
            destinationFolderId: context.destinationId,
            options: context.options,
            linkType: preferredLinkType
        });
        if (deferredResult) return deferredResult;

        const reasonCodes = this.getReasonCodes(result);
        const message = this.resolveApiMessage(result, 'Item already queued');
        if (this.isSkipReason(reasonCodes, message)) {
            const skipResult = this.handleAlreadyQueuedResult(
                { alreadyQueued: true, message },
                {
                    url: context.url,
                    bitrate: context.bitrate,
                    destinationFolderId: context.destinationId,
                    options: context.options,
                    notificationType: 'info',
                    linkType: preferredLinkType
                });
            return { ...skipResult, reasonCodes };
        }

        throw this.buildResultError(result, 'Failed to add to queue');
    },
    async tryHandleAppleDownload(context) {
        if (!this.isAppleUrl(context.url)) {
            return null;
        }

        const { trackPayload, metadata, hasAtmos } = this.buildAppleTrackPayload({
            url: context.url,
            metadata: context.options?.metadata
        });
        const isVideo = this.isAppleVideoDownload(context.url, metadata);
        const secondaryDestinationFolderId = context.options?.secondaryDestinationFolderId ?? null;

        if (isVideo) {
            return this.enqueueAppleVideoDownload({
                trackPayload,
                url: context.url,
                bitrate: context.bitrate,
                destinationId: context.destinationId,
                secondaryDestinationFolderId,
                options: context.options,
                allowQualityUpgrade: context.intentContext.allowQualityUpgrade
            });
        }

        const intentPayload = this.buildAppleIntentPayload({
            url: context.url,
            metadata,
            hasAtmos,
            intentContext: context.intentContext,
            destinationId: context.destinationId,
            secondaryDestinationFolderId
        });
        const result = await this.postDownloadIntent(intentPayload);
        return this.handleAppleIntentResult(result, context, metadata);
    },
    buildDeezerMetadata(context) {
        const deezerMetadata = context.options?.metadata && typeof context.options.metadata === 'object'
            ? { ...context.options.metadata }
            : {};
        if (context.intentContext.allowQualityUpgrade) {
            deezerMetadata.allowQualityUpgrade = true;
        }
        if (!deezerMetadata.preferredEngine && context.intentContext.preferredEngine) {
            deezerMetadata.preferredEngine = context.intentContext.preferredEngine;
        }
        if (!deezerMetadata.quality && context.intentContext.preferredQuality) {
            deezerMetadata.quality = context.intentContext.preferredQuality;
        }
        return deezerMetadata;
    },
    async postDeezerAddWithSettings({ url, bitrate, destinationId, metadata }) {
        return this.apiFetch('/api/deezer/download/add-with-settings', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
            },
            keepalive: true,
            body: JSON.stringify({
                url,
                bitrate,
                userId: this.getCurrentUserId(),
                destinationFolderId: destinationId,
                queueInBackground: true,
                metadata: Object.keys(metadata).length > 0
                    ? metadata
                    : undefined
            })
        });
    },
    async handleDeezerApiError(response, context) {
        const apiError = await this.buildApiError(response);
        const alreadyQueuedError = this.handleAlreadyQueuedApiError(response, apiError, {
            url: context.url,
            bitrate: context.bitrate,
            destinationFolderId: context.destinationId,
            options: context.options,
            returnsSuccess: false
        });
        if (alreadyQueuedError) return alreadyQueuedError;
        throw apiError;
    },
    handleDeezerCompletedResult(result, context) {
        const hasIds = result.downloadId || (Array.isArray(result.downloadIds) && result.downloadIds.length > 0);
        if (!hasIds) {
            return null;
        }

        this.logDownloadEvent('success', `added to queue (${result.linkType || 'download'})`);
        const addedCount = Array.isArray(result.downloadIds) && result.downloadIds.length > 0
            ? result.downloadIds.length
            : 1;
        context.notifyQueue(`Added ${addedCount} item(s) to the queue`, 'success');
        this.removePendingQueueIfNeeded(context.url, context.bitrate, context.destinationId, context.options);
        return {
            success: true,
            downloadId: result.downloadId,
            downloadIds: result.downloadIds || [],
            linkType: result.linkType
        };
    },
    async handleDeezerDownload(context) {
        const deezerMetadata = this.buildDeezerMetadata(context);
        const response = await this.postDeezerAddWithSettings({
            url: context.url,
            bitrate: context.bitrate,
            destinationId: context.destinationId,
            metadata: deezerMetadata
        });

        if (!response.ok) {
            return this.handleDeezerApiError(response, context);
        }

        const result = await response.json();
        const alreadyQueuedResult = this.handleAlreadyQueuedResult(result, {
            url: context.url,
            bitrate: context.bitrate,
            destinationFolderId: context.destinationId,
            options: context.options,
            linkType: result.linkType || ''
        });
        if (alreadyQueuedResult) return alreadyQueuedResult;

        const deferredResult = this.handleDeferredResult(result, {
            url: context.url,
            bitrate: context.bitrate,
            destinationFolderId: context.destinationId,
            options: context.options,
            linkType: result.linkType || '',
            message: 'Queued items for background matching',
            logMessage: result.message || 'Queued items for background matching'
        });
        if (deferredResult) return deferredResult;

        const completedResult = this.handleDeezerCompletedResult(result, context);
        if (completedResult) return completedResult;

        throw this.buildResultError(result, 'Failed to add to queue');
    },
    async tryHandleSpotifyDownload(context) {
        if (!this.isSpotifyUrl(context.url)) {
            return null;
        }
        const metadata = context.options?.metadata && typeof context.options.metadata === 'object'
            ? context.options.metadata
            : null;
        const result = await this.postDownloadIntent({
            resolveImmediately: true,
            intents: [{
                sourceService: 'spotify',
                sourceUrl: context.url,
                spotifyId: this.extractSpotifyId(context.url),
                deezerId: metadata?.deezerId || undefined,
                preferredEngine: context.intentContext.preferredEngine || undefined,
                quality: context.intentContext.preferredQuality || undefined,
                title: metadata?.title || undefined,
                artist: metadata?.artist || undefined,
                album: metadata?.album || undefined,
                albumArtist: metadata?.albumArtist || undefined,
                isrc: metadata?.isrc || undefined,
                cover: metadata?.cover || undefined,
                durationMs: Number(metadata?.durationMs || 0) || undefined,
                position: Number(metadata?.position || 0) || undefined,
                allowQualityUpgrade: context.intentContext.allowQualityUpgrade
            }],
            destinationFolderId: context.destinationId
        });

        const queuedResult = this.handleQueuedResult(result, {
            url: context.url,
            bitrate: context.bitrate,
            destinationFolderId: context.destinationId,
            options: context.options,
            queueType: 'spotify',
            engine: result.engine || '',
            linkType: result.engine || 'spotify',
            logLabel: 'spotify intent'
        });
        if (queuedResult) return queuedResult;

        const deferredResult = this.handleDeferredResult(result, {
            url: context.url,
            bitrate: context.bitrate,
            destinationFolderId: context.destinationId,
            options: context.options,
            linkType: 'spotify'
        });
        if (deferredResult) return deferredResult;

        const reasonCodes = this.getReasonCodes(result);
        const message = this.resolveApiMessage(result, 'Item already queued');
        if (this.isSkipReason(reasonCodes, message)) {
            const skipResult = this.handleAlreadyQueuedResult(
                { alreadyQueued: true, message },
                {
                    url: context.url,
                    bitrate: context.bitrate,
                    destinationFolderId: context.destinationId,
                    options: context.options,
                    notificationType: 'info',
                    linkType: result.engine || 'spotify'
                });
            return { ...skipResult, reasonCodes };
        }

        throw this.buildResultError(result, 'Failed to add to queue');
    },
    async executeAddToQueueBySource(context) {
        const backgroundIntentResult = await this.tryHandleBackgroundIntent(context);
        if (backgroundIntentResult) {
            return backgroundIntentResult;
        }

        const handlers = [
            this.tryHandleQobuzIsrcDownload,
            this.tryHandleAmazonDownload,
            this.tryHandleQobuzDownload,
            this.tryHandleTidalDownload,
            this.tryHandleAppleDownload,
            this.tryHandleSpotifyDownload
        ];

        for (const handler of handlers) {
            const result = await handler.call(this, context);
            if (result) {
                return result;
            }
        }

        return this.handleDeezerDownload(context);
    },
    resolveIntentSourceService(url) {
        if (this.isQobuzUrl(url)) return 'qobuz';
        if (this.isAmazonUrl(url)) return 'amazon';
        if (this.isTidalUrl(url)) return 'tidal';
        if (this.isAppleUrl(url)) return 'apple';
        if (this.isSpotifyUrl(url)) return 'spotify';

        const parsed = this.tryParseAbsoluteUrl(url);
        if (!parsed) return null;
        if (this.hostMatches(parsed.hostname, ['boomplay.com'])) return 'boomplay';
        if (this.hostMatches(parsed.hostname, ['deezer.com'])) return 'deezer';
        return null;
    },
    async tryHandleBackgroundIntent(context) {
        const isrcToken = this.extractIsrcToken(context.url);
        if (isrcToken) {
            return null;
        }

        const sourceService = this.resolveIntentSourceService(context.url);
        if (!sourceService) {
            return null;
        }

        return this.enqueueIntentWithPreference({
            sourceService,
            sourceUrl: context.url,
            isrc: '',
            url: context.url,
            bitrate: context.bitrate,
            destinationId: context.destinationId,
            options: context.options,
            intentContext: context.intentContext,
            notify: context.notify,
            notifyQueue: context.notifyQueue,
            resolveImmediately: false
        });
    },
    handleAddToQueueError(error, { url, bitrate, destinationId, options, notify }) {
        const errorMessage = error?.message || 'Unknown error';
        console.error('Error adding to download queue:', error);
        const isSkip = error?.skipLike === true;
        if (isSkip) {
            notify(errorMessage, 'warning');
            this.logDownloadEvent('warning', `skipped: ${errorMessage}`);
        } else {
            notify(`Failed to add to queue: ${errorMessage}`, 'error');
            this.logDownloadEvent('error', `failed to add: ${errorMessage}`);
        }
        const shouldKeepPending = error?.name === 'AbortError'
            || Number(error?.status || 0) >= 500
            || Number(error?.status || 0) === 408
            || Number(error?.status || 0) === 429
            || /network|failed to fetch|load failed|the network connection/i.test(String(errorMessage));
        if (!options.skipPending && !shouldKeepPending) {
            this.removePendingQueueItem({ url, bitrate, destinationFolderId: destinationId });
        }
        return {
            success: false,
            errorMessage,
            reasonCodes: Array.isArray(error?.reasonCodes) ? error.reasonCodes : []
        };
    },
    // Add URL to download queue
    async addToQueue(url, bitrate = 0, destinationFolderId = null, options = {}) {
        const { notify, notifyQueue } = this.createQueueNotifier(options);
        const normalizedUrl = String(url || '').trim();
        const destinationId = destinationFolderId ?? this.getDestinationFolderId(true);
        const validationError = this.validateQueueInput(url, normalizedUrl, destinationFolderId, destinationId, notify);
        if (validationError) {
            return validationError;
        }

        notify('Adding to download queue...', 'info');
        this.addPendingQueueIfNeeded(url, bitrate, destinationId, options);
        const lockId = this.acquireInFlightLock(normalizedUrl, destinationId);

        try {
            await this.ensureSettingsLoaded();
            const intentContext = this.buildIntentContext(options);
            return await this.executeAddToQueueBySource({
                url,
                bitrate,
                destinationId,
                options,
                notify,
                notifyQueue,
                intentContext
            });
        } catch (error) {
            return this.handleAddToQueueError(error, {
                url,
                bitrate,
                destinationId,
                options,
                notify
            });
        } finally {
            this.releaseInFlightLock(normalizedUrl, lockId);
        }
    },

    // Add multiple URLs to download queue
    async addMultipleToQueue(urls, bitrate = 0) {
        if (!urls || urls.length === 0) {
            this.showNotification('No URLs provided', 'error');
            return;
        }

        const destinationId = this.getDestinationFolderId(true);
        if (destinationId === null && document.querySelector('.download-destination-select')) {
            return;
        }

        const dedupedUrls = [];
        const seenUrls = new Set();
        let inputDuplicates = 0;
        for (const rawUrl of urls) {
            const normalizedUrl = String(rawUrl || '').trim();
            if (!normalizedUrl) {
                continue;
            }
            if (seenUrls.has(normalizedUrl)) {
                inputDuplicates++;
                continue;
            }
            seenUrls.add(normalizedUrl);
            dedupedUrls.push(normalizedUrl);
        }

        if (dedupedUrls.length === 0) {
            const duplicateOnlyMessage = inputDuplicates > 0
                ? `No new URLs to add (${inputDuplicates} duplicate input(s) removed).`
                : 'No valid URLs to add.';
            this.showNotification(duplicateOnlyMessage, inputDuplicates > 0 ? 'warning' : 'error');
            return;
        }

        const results = {
            success: 0,
            deferred: 0,
            failed: 0,
            alreadyQueued: 0,
            inputDuplicates,
            reasonCounts: {},
            errors: []
        };

        const inputSummary = inputDuplicates > 0
            ? ` (+${inputDuplicates} duplicates removed)`
            : '';
        this.showNotification(`Adding ${dedupedUrls.length}${inputSummary} items to download queue...`, 'info');

        // Persist the full bulk selection up front so queue submission survives page navigation.
        this.enqueuePendingQueueItems(dedupedUrls, bitrate, destinationId);

        const chunkSize = 20;
        const concurrency = 4;
        for (let index = 0; index < dedupedUrls.length; index += chunkSize) {
            const chunk = dedupedUrls.slice(index, index + chunkSize);
            await this.enqueueBatchChunk(chunk, bitrate, destinationId, results, concurrency);
        }

        this.showBatchQueueSummary(results);
    },
    async enqueueBatchChunk(urls, bitrate, destinationId, results, concurrency) {
        const queue = [...urls];
        const workers = Array.from({ length: Math.max(1, Math.min(concurrency, queue.length)) }, async () => {
            while (queue.length > 0) {
                const url = queue.shift();
                if (!url) {
                    continue;
                }

                try {
                    const outcome = await this.addToQueue(url, bitrate, destinationId, {
                        skipPending: false,
                        pendingPrequeued: true,
                        silent: true
                    });
                    this.recordBatchQueueOutcome(results, outcome, url, bitrate, destinationId);
                } catch (error) {
                    this.recordBatchQueueError(results, url, error);
                }
            }
        });

        await Promise.all(workers);
    },
    recordBatchQueueOutcome(results, outcome, url, bitrate, destinationId) {
        if (outcome?.alreadyQueued) {
            results.alreadyQueued++;
            this.mergeBatchReasonCounts(results, outcome);
            return;
        }
        if (outcome?.deferred) {
            results.deferred++;
            return;
        }
        if (outcome?.success) {
            results.success++;
            return;
        }
        results.failed++;
        results.errors.push(`${url}: ${outcome?.errorMessage || 'Unknown error'}`);
        this.mergeBatchReasonCounts(results, outcome);
    },
    recordBatchQueueError(results, url, error) {
        results.failed++;
        results.errors.push(`${url}: ${error?.message || 'Unknown error'}`);
    },
    mergeBatchReasonCounts(results, outcome) {
        let reasonCodes = [];
        if (Array.isArray(outcome?.reasonCodes) && outcome.reasonCodes.length > 0) {
            reasonCodes = outcome.reasonCodes;
        } else if (outcome?.reasonCode) {
            reasonCodes = [outcome.reasonCode];
        }
        reasonCodes.forEach((reasonCode) => {
            const normalized = String(reasonCode || '').trim();
            if (!normalized) {
                return;
            }
            results.reasonCounts[normalized] = (results.reasonCounts[normalized] || 0) + 1;
        });
    },
    showBatchQueueSummary(results) {
        if (results.success > 0) {
            this.showQueueToast(
                this.buildSuccessfulBatchQueueMessage(results),
                this.getBatchQueueToastType(results)
            );
            return;
        }

        if (results.deferred > 0 && results.failed === 0) {
            this.showQueueToast(this.buildDeferredBatchQueueMessage(results), 'info');
            return;
        }

        if (results.alreadyQueued > 0 && results.failed === 0) {
            this.showNotification(this.buildAlreadyQueuedBatchMessage(results), 'warning');
            return;
        }

        this.showNotification(this.buildFailedBatchQueueMessage(results), 'error');
    },
    getBatchQueueToastType(results) {
        return results.failed > 0 || results.alreadyQueued > 0 || results.deferred > 0 ? 'warning' : 'success';
    },
    buildSuccessfulBatchQueueMessage(results) {
        const duplicateSummary = this.buildDuplicateInputSummary(results.inputDuplicates);
        const deferredSummary = results.deferred > 0 ? ` (${results.deferred} deferred)` : '';
        const alreadyQueuedSummary = results.alreadyQueued > 0 ? ` (${results.alreadyQueued} already queued)` : '';
        const failedSummary = results.failed > 0 ? ` (${results.failed} failed)` : '';
        return `Successfully added ${results.success} items to queue${duplicateSummary}${deferredSummary}${alreadyQueuedSummary}${failedSummary}`;
    },
    buildDeferredBatchQueueMessage(results) {
        return `Queued ${results.deferred} item(s) for background intent resolution. They will appear after matching.`;
    },
    buildAlreadyQueuedBatchMessage(results) {
        const reasonSummary = this.formatBatchReasonSummary(results.reasonCounts);
        const duplicateSummary = results.inputDuplicates > 0 ? ` and ${results.inputDuplicates} duplicate input(s)` : '';
        return `All ${results.alreadyQueued} items were already queued${duplicateSummary}${reasonSummary}`;
    },
    buildFailedBatchQueueMessage(results) {
        const reasonSummary = this.formatBatchReasonSummary(results.reasonCounts);
        const duplicateSummary = this.buildDuplicateInputSummary(results.inputDuplicates);
        return `Failed to add any items to queue${duplicateSummary}${reasonSummary}`;
    },
    buildDuplicateInputSummary(duplicateCount) {
        return duplicateCount > 0 ? ` (${duplicateCount} duplicate input(s) removed)` : '';
    },
    formatBatchReasonSummary(reasonCounts) {
        const entries = Object.entries(reasonCounts || {});
        if (entries.length === 0) {
            return '';
        }

        const sortedEntries = [...entries];
        sortedEntries.sort((a, b) => b[1] - a[1]);
        const summary = sortedEntries
            .slice(0, 3)
            .map(([reason, count]) => `${reason} (${count})`)
            .join(', ');
        return summary ? ` [${summary}]` : '';
    },
    loadPendingQueue() {
        try {
            const raw = localStorage.getItem('download-pending-queue');
            const parsed = raw ? JSON.parse(raw) : [];
            return Array.isArray(parsed) ? parsed : [];
        } catch (error) {
            console.warn('Failed to load pending download queue', error);
            return [];
        }
    },
    savePendingQueue(items) {
        if (!items || items.length === 0) {
            localStorage.removeItem('download-pending-queue');
            return;
        }
        localStorage.setItem('download-pending-queue', JSON.stringify(items));
    },
    enqueuePendingQueueItems(urls, bitrate, destinationFolderId) {
        const pending = this.loadPendingQueue();
        urls.forEach((url) => {
            pending.push({
                url,
                bitrate,
                destinationFolderId: destinationFolderId || null
            });
        });
        this.savePendingQueue(pending);
    },
    addPendingQueueItem(item) {
        const pending = this.loadPendingQueue();
        const entry = {
            url: item.url,
            bitrate: item.bitrate,
            destinationFolderId: item.destinationFolderId ?? null
        };
        if (item?.metadata && typeof item.metadata === 'object') {
            entry.metadata = item.metadata;
        }
        pending.push(entry);
        this.savePendingQueue(pending);
    },
    removePendingQueueItem(item) {
        const pending = this.loadPendingQueue();
        const index = pending.findIndex((entry) => entry.url === item.url &&
            Number(entry.bitrate) === Number(item.bitrate) &&
            String(entry.destinationFolderId || '') === String(item.destinationFolderId || ''));
        if (index !== -1) {
            pending.splice(index, 1);
            this.savePendingQueue(pending);
        }
    },
    async processPendingQueue(options = {}) {
        if (this.queue.isProcessing) {
            return { resumed: 0 };
        }
        const notifyResumeStart = typeof options?.notifyResumeStart === 'function'
            ? options.notifyResumeStart
            : null;
        const pending = this.loadPendingQueue();
        if (!pending.length) {
            return { resumed: 0 };
        }
        this.queue.isProcessing = true;
        let resumed = 0;
        try {
            while (pending.length > 0) {
                const next = pending.shift();
                this.savePendingQueue(pending);
                if (!next?.url) {
                    continue;
                }
                const outcome = await this.addToQueue(next.url, next.bitrate || 0, next.destinationFolderId ?? null, {
                    skipPending: true,
                    silent: true,
                    metadata: next?.metadata && typeof next.metadata === 'object' ? next.metadata : undefined
                });
                if (outcome?.success || outcome?.deferred || outcome?.alreadyQueued) {
                    resumed++;
                    if (resumed === 1 && notifyResumeStart) {
                        notifyResumeStart();
                    }
                }
            }
            return { resumed };
        } finally {
            this.queue.isProcessing = false;
        }
    },
    resumePendingQueue() {
        if (document.visibilityState === 'hidden') {
            document.addEventListener('visibilitychange', () => {
                if (document.visibilityState === 'visible') {
                    this.resumePendingQueue();
                }
            }, { once: true });
            return;
        }
        this.processPendingQueue({
            notifyResumeStart: () => {
                if (!this.queue.resumeNotified) {
                    this.queue.resumeNotified = true;
                    this.showNotification('Resuming downloads...', 'info');
                }
            }
        });
    },

    // Parse URL to get information
    async parseUrl(url) {
        try {
            const response = await this.apiFetch('/api/deezer/download/parse', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify({ url: url })
            });

            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }

            return await response.json();
        } catch (error) {
            console.error('Error parsing URL:', error);
            throw error;
        }
    },

    normalizeEngine(type) {
        if (!type) return 'deezer';
        const raw = String(type).toLowerCase();
        if (raw.includes('apple')) return 'apple';
        if (raw.includes('tidal')) return 'tidal';
        if (raw.includes('amazon')) return 'amazon';
        if (raw.includes('qobuz')) return 'qobuz';
        if (raw.includes('deezer')) return 'deezer';
        return raw;
    },
    resolveEngine(engine, sourceService) {
        if (sourceService) return sourceService;
        if (engine && String(engine).toLowerCase() === 'multisource') {
            return 'deezer';
        }
        return engine || '';
    },
    getPreferredEngineFromUi() {
        const source = document.getElementById('downloadSource');
        const value = source?.value || this.settings?.service || null;
        if (!value) {
            return null;
        }
        return String(value).toLowerCase();
    },
    getPreferredQualityForEngine(engine) {
        if (!engine) {
            return null;
        }
        const normalized = String(engine).toLowerCase();
        if (normalized === 'apple') {
            return document.getElementById('applePreferredAudioProfile')?.value
                || this.settings?.appleMusic?.preferredAudioProfile
                || null;
        }
        if (normalized === 'qobuz') {
            return document.getElementById('multisource-qobuzQuality')?.value
                || this.settings?.qobuzQuality
                || null;
        }
        if (normalized === 'tidal') {
            return document.getElementById('multisource-tidalQuality')?.value
                || this.settings?.tidalQuality
                || null;
        }
        if (normalized === 'deezer') {
            return document.getElementById('maxBitrate')?.value
                || this.settings?.maxBitrate
                || null;
        }
        return null;
    },
    tryParseAbsoluteUrl(url) {
        if (typeof url !== 'string') {
            return null;
        }

        const trimmed = url.trim();
        if (!trimmed) {
            return null;
        }

        try {
            const parsed = new URL(trimmed);
            const protocol = parsed.protocol.toLowerCase();
            if (protocol !== 'http:' && protocol !== 'https:') {
                return null;
            }
            return parsed;
        } catch {
            return null;
        }
    },
    hostMatches(host, allowedHosts) {
        const normalizedHost = String(host || '').toLowerCase();
        return allowedHosts.some((allowedHost) =>
            normalizedHost === allowedHost || normalizedHost.endsWith(`.${allowedHost}`));
    },
    isQobuzUrl(url) {
        const parsed = this.tryParseAbsoluteUrl(url);
        return !!parsed && this.hostMatches(parsed.hostname, ['qobuz.com']);
    },
    extractIsrcToken(url) {
        if (typeof url !== 'string') {
            return '';
        }
        const match = /^isrc:([A-Za-z0-9-]+)$/.exec(url.trim());
        return match ? match[1] : '';
    },
    isAmazonUrl(url) {
        const parsed = this.tryParseAbsoluteUrl(url);
        if (!parsed) {
            return false;
        }

        const host = parsed.hostname.toLowerCase();
        const path = parsed.pathname.toLowerCase();
        if (host === 'music.amazon.com' || host.endsWith('.music.amazon.com') || host.startsWith('music.amazon.')) {
            return true;
        }

        const isAmazonDomain = host === 'amazon.com' || host.endsWith('.amazon.com') || host.startsWith('amazon.');
        return isAmazonDomain && path.startsWith('/music');
    },
    isTidalUrl(url) {
        const parsed = this.tryParseAbsoluteUrl(url);
        return !!parsed && this.hostMatches(parsed.hostname, ['tidal.com']);
    },
    isAppleUrl(url) {
        const parsed = this.tryParseAbsoluteUrl(url);
        return !!parsed && this.hostMatches(parsed.hostname, ['music.apple.com', 'itunes.apple.com']);
    },
    isAppleVideoUrl(url) {
        const lower = typeof url === 'string' ? url.toLowerCase() : '';
        return lower.includes('/music-video/')
            || lower.includes('/music-videos/')
            || lower.includes('/video/');
    },
    isSpotifyUrl(url) {
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

        const parsed = this.tryParseAbsoluteUrl(trimmed);
        return !!parsed && this.hostMatches(parsed.hostname, ['spotify.com']);
    },
    extractSpotifyId(url) {
        if (!url) return '';
        const match = /(?:spotify:track:|open\.spotify\.com\/track\/)([A-Za-z0-9]+)/.exec(String(url));
        return match ? match[1] : '';
    },
    async readApiBody(response) {
        const contentType = response?.headers?.get('content-type') || '';
        const text = await response.text();
        const trimmed = String(text || '').trim();
        if (!trimmed) {
            return { payload: null, rawText: '' };
        }

        if (contentType.includes('application/json')) {
            try {
                return { payload: JSON.parse(trimmed), rawText: trimmed };
            } catch {
                return { payload: null, rawText: trimmed };
            }
        }

        try {
            return { payload: JSON.parse(trimmed), rawText: trimmed };
        } catch {
            return { payload: null, rawText: trimmed };
        }
    },
    getReasonCodes(payload) {
        if (!payload || typeof payload !== 'object' || !Array.isArray(payload.reasonCodes)) {
            return [];
        }
        return payload.reasonCodes
            .map((value) => String(value || '').trim())
            .filter(Boolean);
    },
    reasonCodeToMessage(reasonCode) {
        const map = {
            library_duplicate: 'Matching file already exists in your library.',
            library_quality_not_higher: 'Requested quality is not higher than your local file.',
            queue_duplicate: 'Matching track is already in the download queue.',
            queue_recently_downloaded: 'Track was downloaded recently and is still in cooldown.',
            queue_quality_not_higher: 'Queue already has this track at same or higher quality.',
            queue_upgrade_in_progress: 'Matching track is currently downloading; cancel it before upgrading.',
            queue_insert_ignored: 'Track was skipped because a matching queued item already exists.',
            destination_invalid: 'Destination folder is invalid.',
            invalid_payload: 'Download payload is invalid.'
        };
        return map[String(reasonCode || '').toLowerCase()] || '';
    },
    resolveStringPayloadMessage(payload) {
        if (typeof payload !== 'string') {
            return '';
        }
        return payload.trim();
    },
    resolveObjectPayloadMessage(payload) {
        if (!payload || typeof payload !== 'object') {
            return '';
        }
        const message = typeof payload.message === 'string' ? payload.message.trim() : '';
        if (message && !/^nothing queued\.?$/i.test(message)) {
            return message;
        }
        return typeof payload.error === 'string' ? payload.error.trim() : '';
    },
    resolveReasonCodeMessage(payload) {
        const reasonCodes = this.getReasonCodes(payload);
        if (reasonCodes.length === 0) {
            return '';
        }
        const mapped = this.reasonCodeToMessage(reasonCodes[0]);
        return mapped || reasonCodes[0].replaceAll('_', ' ');
    },
    resolveApiMessage(payload, fallbackMessage) {
        return this.resolveStringPayloadMessage(payload)
            || this.resolveObjectPayloadMessage(payload)
            || this.resolveReasonCodeMessage(payload)
            || fallbackMessage;
    },
    isAlreadyQueuedMessage(message) {
        return /already\s+queued|already\s+in\s+queue|matching\s+track\s+is\s+already\s+in\s+queue/i.test(String(message || ''));
    },
    isSkipReason(reasonCodes, message) {
        const skipCodes = new Set([
            'library_duplicate',
            'library_quality_not_higher',
            'queue_duplicate',
            'queue_recently_downloaded',
            'queue_quality_not_higher',
            'queue_upgrade_in_progress',
            'queue_insert_ignored'
        ]);
        return reasonCodes.some((code) => skipCodes.has(String(code || '').toLowerCase()))
            || /(^|\s)skipped:|not higher than|already exists in library|already in queue|currently downloading/i.test(String(message || ''));
    },
    buildResultError(result, fallbackMessage) {
        const reasonCodes = this.getReasonCodes(result);
        const message = this.resolveApiMessage(result, fallbackMessage);
        const error = new Error(message);
        error.reasonCodes = reasonCodes;
        error.alreadyQueued = Boolean(result?.alreadyQueued) || this.isAlreadyQueuedMessage(message);
        error.skipLike = this.isSkipReason(reasonCodes, message);
        return error;
    },
    async buildApiError(response, fallbackMessage = '') {
        const status = Number(response?.status || 0);
        const { payload, rawText } = await this.readApiBody(response);
        const reasonCodes = this.getReasonCodes(payload);
        const htmlResponse = /^\s*</.test(String(rawText || ''));
        const defaultMessage = fallbackMessage
            || (htmlResponse ? `Download API returned an HTML error page (${status || 'unknown status'}).` : rawText)
            || `HTTP error! status: ${status}`;
        const messageSource = htmlResponse ? payload : (payload ?? rawText);
        const message = this.resolveApiMessage(messageSource, defaultMessage);
        const error = new Error(message);
        error.status = status;
        error.reasonCodes = reasonCodes;
        error.alreadyQueued = Boolean(payload?.alreadyQueued) || this.isAlreadyQueuedMessage(message);
        error.skipLike = this.isSkipReason(reasonCodes, message);
        return error;
    },
    logDownloadEvent(level, message, downloadId, engineOverride) {
        if (!this.logger) return;
        const engine = engineOverride || (downloadId ? this.engineById[downloadId] : '') || 'deezer';
        const prefix = engine ? `[${engine}] ` : '';
        this.logger[level]?.(`${prefix}${message}`, { engine });
    },

    // Utility functions
    getCurrentUserId() {
        // Get current user ID from session/auth
        return 'anonymous'; // Placeholder
    },

    showNotification(message, type = 'info', options = {}) {
        // Use existing notification system or create one
        if (globalThis.DeezSpoTag?.showNotification) {
            globalThis.DeezSpoTag.showNotification(message, type, options);
            return;
        }

        const toast = document.createElement('div');
        const safeType = ['success', 'error', 'warning', 'info'].includes(type) ? type : 'info';
        toast.className = `toast toast-${safeType}`;
        toast.textContent = message;
        document.body.appendChild(toast);
        setTimeout(() => toast.remove(), 3000);
    },

    showQueueToast(message, type = 'success') {
        this.showNotification(message, type, {
            action: { label: 'View', href: '/Activities?tab=downloads-content' }
        });
    },

    // Helper functions for different content types
    downloadTrack(trackId, bitrate = 0) {
        const url = `https://www.deezer.com/track/${trackId}`;
        return this.addToQueue(url, bitrate);
    },

    downloadAlbum(albumId, bitrate = 0) {
        const url = `https://www.deezer.com/album/${albumId}`;
        return this.addToQueue(url, bitrate);
    },

    downloadPlaylist(playlistId, bitrate = 0) {
        const url = `https://www.deezer.com/playlist/${playlistId}`;
        return this.addToQueue(url, bitrate);
    },

    downloadArtist(artistId, bitrate = 0) {
        const url = `https://www.deezer.com/artist/${artistId}`;
        return this.addToQueue(url, bitrate);
    },

    downloadArtistDiscography(artistId, bitrate = 0) {
        const url = `https://www.deezer.com/artist/${artistId}/discography`;
        return this.addToQueue(url, bitrate);
    },

    // Batch download functions
    downloadSelectedTracks(trackIds, bitrate = 0) {
        const urls = trackIds.map(id => `https://www.deezer.com/track/${id}`);
        return this.addMultipleToQueue(urls, bitrate);
    },

    downloadTracksFromTable() {
        const trackIds = Array.from(document.querySelectorAll('.track-checkbox[data-track-id]:checked'))
            .map((element) => element.dataset.trackId || '')
            .filter(Boolean);
        
        if (trackIds.length === 0) {
            this.showNotification('Please select tracks to download', 'warning');
            return;
        }

        return this.downloadSelectedTracks(trackIds);
    },

    downloadAllTracksFromTable() {
        const trackIds = Array.from(document.querySelectorAll('.track-checkbox[data-track-id]'))
            .map((element) => element.dataset.trackId || '')
            .filter(Boolean);
        
        if (trackIds.length === 0) {
            this.showNotification('No tracks available to download', 'warning');
            return;
        }

        return this.downloadSelectedTracks(trackIds);
    }
};

// Initialize when DOM is ready
document.addEventListener('DOMContentLoaded', () => {
    DeezSpoTag.Download.init();
});

// Export for global access
globalThis.DeezSpoTagDownload = DeezSpoTag.Download;
