globalThis.DeezSpoTag = globalThis.DeezSpoTag || {};

(() => {
    const MAX_LOGS_IN_MEMORY = 3000;
    const MAX_LOGS_PERSISTED = 1000;
    const STORAGE_KEY = 'deezspotag.downloadLogs';
    const INSTANCE_KEY = 'deezspotag.appInstanceId';
    const PERSIST_DEBOUNCE_MS = 1500;

    class DownloadLogger {
        constructor() {
            this.logs = [];
            this.listeners = new Set();
            this.persistTimer = null;
            this.notifyFrame = null;
            this.restore();
        }

        add(level, message, context = {}) {
            const rawTimestamp = context.timestamp;
            let resolvedTimestamp = new Date();
            if (rawTimestamp instanceof Date) {
                resolvedTimestamp = rawTimestamp;
            } else if (rawTimestamp) {
                resolvedTimestamp = new Date(rawTimestamp);
            }
            const entry = {
                timestamp: resolvedTimestamp,
                level,
                message: String(message || ''),
                engine: context.engine || ''
            };
            this.logs.push(entry);
            if (this.logs.length > MAX_LOGS_IN_MEMORY) {
                this.logs.splice(0, this.logs.length - MAX_LOGS_IN_MEMORY);
            }
            this.schedulePersist();
            this.scheduleNotify();
        }

        info(message, context) {
            this.add('info', message, context);
        }

        success(message, context) {
            this.add('success', message, context);
        }

        warning(message, context) {
            this.add('warning', message, context);
        }

        error(message, context) {
            this.add('error', message, context);
        }

        debug(message, context) {
            this.add('debug', message, context);
        }

        getLogs() {
            return [...this.logs];
        }

        clear() {
            this.logs = [];
            this.persistNow();
            this.notifyNow();
        }

        subscribe(listener) {
            this.listeners.add(listener);
            return () => this.listeners.delete(listener);
        }

        notifyNow() {
            this.listeners.forEach((listener) => listener());
        }

        scheduleNotify() {
            if (this.notifyFrame) {
                return;
            }
            this.notifyFrame = requestAnimationFrame(() => {
                this.notifyFrame = null;
                this.notifyNow();
            });
        }

        schedulePersist() {
            if (this.persistTimer) {
                return;
            }
            this.persistTimer = globalThis.setTimeout(() => {
                this.persistTimer = null;
                this.persistNow();
            }, PERSIST_DEBOUNCE_MS);
        }

        persistNow() {
            try {
                const instanceId = String(globalThis.DeezSpoTagAppInstanceId || '');
                const logsToPersist = this.logs.length > MAX_LOGS_PERSISTED
                    ? this.logs.slice(-MAX_LOGS_PERSISTED)
                    : this.logs;
                const payload = {
                    instanceId,
                    logs: logsToPersist.map((entry) => ({
                        timestamp: entry.timestamp instanceof Date ? entry.timestamp.toISOString() : entry.timestamp,
                        level: entry.level,
                        message: entry.message,
                        engine: entry.engine
                    }))
                };
                localStorage.setItem(STORAGE_KEY, JSON.stringify(payload));
                if (instanceId) {
                    localStorage.setItem(INSTANCE_KEY, instanceId);
                }
            } catch (e) {
                this.reportStorageError('persist logs', e);
            }
        }

        restore() {
            try {
                const instanceId = String(globalThis.DeezSpoTagAppInstanceId || '');
                const storedInstance = localStorage.getItem(INSTANCE_KEY) || '';
                if (instanceId && storedInstance && storedInstance !== instanceId) {
                    localStorage.removeItem(STORAGE_KEY);
                    localStorage.setItem(INSTANCE_KEY, instanceId);
                    return;
                }

                const raw = localStorage.getItem(STORAGE_KEY);
                if (!raw) {
                    if (instanceId) {
                        localStorage.setItem(INSTANCE_KEY, instanceId);
                    }
                    return;
                }

                const payload = JSON.parse(raw);
                if (instanceId && payload?.instanceId && payload.instanceId !== instanceId) {
                    localStorage.removeItem(STORAGE_KEY);
                    localStorage.setItem(INSTANCE_KEY, instanceId);
                    return;
                }

                const restored = Array.isArray(payload?.logs) ? payload.logs : [];
                this.logs = restored.map((entry) => ({
                    timestamp: entry.timestamp ? new Date(entry.timestamp) : new Date(),
                    level: entry.level || 'info',
                    message: entry.message || '',
                    engine: entry.engine || ''
                }));
                if (this.logs.length > MAX_LOGS_IN_MEMORY) {
                    this.logs.splice(0, this.logs.length - MAX_LOGS_IN_MEMORY);
                }
            } catch (e) {
                this.reportStorageError('restore logs', e);
            }
        }

        reportStorageError(action, error) {
            if (globalThis.console && typeof globalThis.console.debug === 'function') {
                globalThis.console.debug(`[DownloadLogger] Failed to ${action}.`, error);
            }
        }
    }

    DeezSpoTag.DownloadLogger = new DownloadLogger();

    // Library activity log ingestion is owned by the Activities page.
    // Keeping it in one place prevents duplicate appends and timestamp drift.
})();
