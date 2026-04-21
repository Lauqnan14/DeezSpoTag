(() => {
    if (globalThis.DeezSpoTagLibraryPageCommon) {
        return;
    }

    const LIBRARY_VIEW_SESSION_KEY = 'libraryViewFolderId';
    let imageCacheKey = null;

    function escapeHtml(text) {
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

    function toSafeHttpUrl(rawUrl) {
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
    }

    function getImageCacheKey() {
        if (imageCacheKey) {
            return imageCacheKey;
        }

        try {
            const existing = localStorage.getItem('libraryImageCacheKey');
            if (existing) {
                imageCacheKey = existing;
                return imageCacheKey;
            }
            imageCacheKey = Date.now().toString();
            localStorage.setItem('libraryImageCacheKey', imageCacheKey);
            return imageCacheKey;
        } catch {
            return '';
        }
    }

    function appendCacheKey(url) {
        const key = getImageCacheKey();
        if (!key || !url) {
            return url;
        }
        const joiner = url.includes('?') ? '&' : '?';
        return `${url}${joiner}v=${encodeURIComponent(key)}`;
    }

    function isHtmlPayload(text) {
        const sample = String(text || '').trimStart().slice(0, 64).toLowerCase();
        return sample.startsWith('<!doctype html') || sample.startsWith('<html');
    }

    function isAuthHtmlResponse(response) {
        if (!response) {
            return false;
        }
        if (response.status === 401 || response.status === 403) {
            return true;
        }
        if (!response.redirected) {
            return false;
        }

        try {
            const redirectedUrl = new URL(response.url, globalThis.location.origin);
            return redirectedUrl.pathname.toLowerCase().startsWith('/identity/account/login');
        } catch {
            return false;
        }
    }

    function parseLibraryErrorMessage(raw) {
        const trimmed = String(raw || '').trim();
        if (!trimmed) {
            return '';
        }
        if (isHtmlPayload(trimmed)) {
            return 'Session expired or invalid security token. Refresh the page and sign in again.';
        }

        try {
            const parsed = JSON.parse(trimmed);
            const parsedMessage = typeof parsed?.message === 'string' ? parsed.message.trim() : '';
            if (parsedMessage && !/^nothing queued\.?$/i.test(parsedMessage)) {
                return parsedMessage;
            }
            const parsedError = typeof parsed?.error === 'string' ? parsed.error.trim() : '';
            if (parsedError) {
                return parsedError;
            }
            const reasonCodes = Array.isArray(parsed?.reasonCodes)
                ? parsed.reasonCodes.map((value) => String(value || '').trim()).filter(Boolean)
                : [];
            if (reasonCodes.length > 0) {
                return reasonCodes[0].replaceAll('_', ' ');
            }
        } catch {
            // Fall through to raw text.
        }

        return trimmed;
    }

    async function parseLibrarySuccessResponse(response, url) {
        const contentType = response.headers.get('content-type') || '';
        const raw = await response.text();
        const trimmed = raw.trim();
        if (isHtmlPayload(trimmed) || contentType.toLowerCase().includes('text/html')) {
            const error = new Error('Session expired or invalid security token. Refresh the page and sign in again.');
            error.libraryUrl = url;
            throw error;
        }
        if (!trimmed || trimmed === 'undefined') {
            const error = new Error(`Invalid JSON response from ${url}: ${trimmed || '<empty>'}`);
            error.libraryUrl = url;
            throw error;
        }

        try {
            return JSON.parse(raw);
        } catch {
            const error = new Error('Unexpected response from server. Refresh the page and try again.');
            error.libraryUrl = url;
            throw error;
        }
    }

    async function parseJsonResponse(response, url) {
        if (isAuthHtmlResponse(response)) {
            const error = new Error('Session expired or invalid security token. Refresh the page and sign in again.');
            error.libraryUrl = url;
            throw error;
        }

        if (!response.ok) {
            const raw = await response.text();
            const message = parseLibraryErrorMessage(raw);
            const error = new Error(message || `Request failed (${response.status})`);
            error.libraryUrl = url;
            throw error;
        }

        return parseLibrarySuccessResponse(response, url);
    }

    async function fetchJson(url, options) {
        const response = await fetch(url, {
            cache: options?.cache ?? 'no-store',
            ...options
        });
        return parseJsonResponse(response, url);
    }

    async function fetchJsonOptional(url, options) {
        const response = await fetch(url, {
            cache: options?.cache ?? 'no-store',
            ...options
        });
        if (response.status === 404) {
            return null;
        }
        return parseJsonResponse(response, url);
    }

    function showToast(message, isError = false) {
        const type = isError ? 'error' : 'info';
        const notifier = globalThis.DeezSpoTag?.showNotification;
        if (typeof notifier === 'function') {
            notifier.call(globalThis.DeezSpoTag, String(message || ''), type);
            return;
        }

        const alertClass = isError ? 'alert-danger' : 'alert-info';
        const notification = document.createElement('div');
        notification.className = `alert ${alertClass} alert-dismissible fade show position-fixed deezspot-notification`;
        notification.style.top = '20px';
        notification.style.right = '20px';
        notification.style.zIndex = '1060';
        notification.style.maxWidth = '400px';

        const textNode = document.createElement('span');
        textNode.textContent = String(message || '');
        notification.appendChild(textNode);

        const closeButton = document.createElement('button');
        closeButton.type = 'button';
        closeButton.className = 'btn-close';
        closeButton.setAttribute('aria-label', 'Close');
        closeButton.addEventListener('click', () => notification.remove());
        notification.appendChild(closeButton);

        document.body.appendChild(notification);
        globalThis.setTimeout(() => notification.remove(), 5000);
    }

    function getStoredLibraryViewSelection() {
        try {
            return sessionStorage.getItem(LIBRARY_VIEW_SESSION_KEY) || '';
        } catch {
            return '';
        }
    }

    function setStoredLibraryViewSelection(value) {
        try {
            if (!value || value === 'main') {
                sessionStorage.removeItem(LIBRARY_VIEW_SESSION_KEY);
                return;
            }
            sessionStorage.setItem(LIBRARY_VIEW_SESSION_KEY, value);
        } catch {
            // Ignore session storage failures.
        }
    }

    function getLibraryScopeFolderIdFromLocation() {
        try {
            const params = new URLSearchParams(globalThis.location.search);
            const raw = String(params.get('folderId') || '').trim();
            if (!raw || raw.toLowerCase() === 'main') {
                return null;
            }
            const parsed = Number.parseInt(raw, 10);
            return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
        } catch {
            return null;
        }
    }

    function getLibraryScopeSelectionFromLocation() {
        try {
            const params = new URLSearchParams(globalThis.location.search);
            if (!params.has('folderId')) {
                return '';
            }
            const raw = String(params.get('folderId') || '').trim();
            if (!raw || raw.toLowerCase() === 'main') {
                return 'main';
            }
            const parsed = Number.parseInt(raw, 10);
            return Number.isFinite(parsed) && parsed > 0 ? String(parsed) : 'main';
        } catch {
            return '';
        }
    }

    function applyLibraryScopeSelectionFromLocation() {
        const selection = getLibraryScopeSelectionFromLocation();
        if (!selection) {
            return;
        }
        setStoredLibraryViewSelection(selection);
    }

    function getSelectedLibraryViewFolderId() {
        const selected = (getStoredLibraryViewSelection() || '').trim();
        if (!selected || selected === 'main') {
            return null;
        }
        const parsed = Number.parseInt(selected, 10);
        return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
    }

    function resolveLibraryScopeFolderIdForNavigation() {
        const selectedFolderId = getSelectedLibraryViewFolderId();
        if (selectedFolderId !== null) {
            return selectedFolderId;
        }
        return getLibraryScopeFolderIdFromLocation();
    }

    function buildLibraryScopedUrl(path, folderId = resolveLibraryScopeFolderIdForNavigation()) {
        if (!path || folderId === null) {
            return path;
        }
        const suffix = path.includes('?') ? '&' : '?';
        return `${path}${suffix}folderId=${encodeURIComponent(String(folderId))}`;
    }

    function buildLibraryPlaybackStateHandler(button, sourceKey) {
        return ({ status, activeSourceKey }) => {
            if (!button || sourceKey !== activeSourceKey) {
                return;
            }

            const isPlaying = status === 'playing';
            const icon = button.querySelector('.material-icons.preview-controls');
            if (icon) {
                icon.textContent = isPlaying ? 'pause' : 'play_arrow';
            }
            button.classList.toggle('is-playing', isPlaying);
            button.setAttribute('aria-pressed', isPlaying ? 'true' : 'false');
            button.setAttribute('title', isPlaying ? 'Pause preview' : 'Play preview');
        };
    }

    async function playLocalLibraryTrackInApp(trackId, button, preferredPath) {
        if (!trackId) {
            return;
        }

        const normalizedPreferredPath = typeof preferredPath === 'string' ? preferredPath.trim() : '';
        const previewKey = `library:${trackId}:${normalizedPreferredPath}`;
        const previewUrl = normalizedPreferredPath
            ? `/api/library/analysis/track/${encodeURIComponent(trackId)}/audio?filePath=${encodeURIComponent(normalizedPreferredPath)}`
            : `/api/library/analysis/track/${encodeURIComponent(trackId)}/audio`;
        const player = globalThis.DeezerUnifiedPlayback;
        if (!player || typeof player.play !== 'function') {
            return;
        }

        await player.play({
            page: 'library',
            button,
            previewUrl,
            sourceKey: previewKey,
            unavailableMessage: 'Preview unavailable.',
            startFailedMessage: 'Unable to start playback.',
            interruptedMessage: 'Playback interrupted.',
            onStateChange: buildLibraryPlaybackStateHandler(button, previewKey)
        });
    }

    applyLibraryScopeSelectionFromLocation();

    globalThis.DeezSpoTagLibraryPageCommon = {
        appendCacheKey,
        buildLibraryScopedUrl,
        escapeHtml,
        fetchJson,
        fetchJsonOptional,
        playLocalLibraryTrackInApp,
        showToast,
        toSafeHttpUrl
    };
})();
