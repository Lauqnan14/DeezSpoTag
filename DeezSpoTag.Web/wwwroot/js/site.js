/**
 * DeezSpoTag JavaScript
 * Core functionality for the application
 */

(() => {
    if (globalThis.__deezspotCsrfFetchShimInstalled || typeof globalThis.fetch !== 'function') {
        return;
    }

    globalThis.__deezspotCsrfFetchShimInstalled = true;
    const originalFetch = globalThis.fetch.bind(globalThis);
    const unsafeMethods = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);
    const csrfRefreshEndpoint = '/api/security/csrf-token';
    const clientIdHeaderName = 'X-DeezSpoTag-ClientId';
    const clientIdStorageKey = 'deezspotag-client-id';
    const clientId = resolveClientId();
    let csrfRefreshPromise = null;
    if (clientId) {
        globalThis.DeezSpoTagClientId = clientId;
    }

    function resolveClientId() {
        let existing = '';
        try {
            existing = String(globalThis.sessionStorage?.getItem(clientIdStorageKey) || '').trim();
        } catch {
            existing = '';
        }

        if (existing) {
            return existing;
        }

        const generated = generateClientId();
        try {
            globalThis.sessionStorage?.setItem(clientIdStorageKey, generated);
        } catch {
            // Ignore storage failures and keep runtime-only value.
        }

        return generated;
    }

    function generateClientId() {
        if (globalThis.crypto && typeof globalThis.crypto.randomUUID === 'function') {
            return globalThis.crypto.randomUUID();
        }

        let randomPart = '';
        if (globalThis.crypto && typeof globalThis.crypto.getRandomValues === 'function') {
            const values = new Uint32Array(1);
            globalThis.crypto.getRandomValues(values);
            randomPart = values[0].toString(36);
        } else {
            randomPart = `${Date.now().toString(36)}-${performance.now().toString(36).replace('.', '')}`;
        }

        return `client-${Date.now()}-${randomPart}`;
    }

    function readCsrfToken() {
        const tokenMeta = document.querySelector('meta[name="deezspotag-csrf-token"]');
        const token = tokenMeta?.getAttribute('content');
        return typeof token === 'string' ? token.trim() : '';
    }

    function writeCsrfToken(token) {
        const normalized = typeof token === 'string' ? token.trim() : '';
        if (!normalized) {
            return;
        }

        let tokenMeta = document.querySelector('meta[name="deezspotag-csrf-token"]');
        if (!tokenMeta) {
            tokenMeta = document.createElement('meta');
            tokenMeta.setAttribute('name', 'deezspotag-csrf-token');
            document.head?.appendChild(tokenMeta);
        }
        tokenMeta.setAttribute('content', normalized);
    }

    function resolveUrl(resource) {
        if (resource instanceof Request) {
            return resource.url;
        }
        return String(resource || '');
    }

    function resolveSameOriginUrl(resource) {
        const urlText = resolveUrl(resource);
        try {
            const url = new URL(urlText, globalThis.location.href);
            if (url.origin === globalThis.location.origin) {
                return url;
            }
        } catch {
            // Ignore parse errors and treat as non-same-origin.
        }

        return null;
    }

    function buildFetchInit(resource, init) {
        return {
            ...init,
            credentials: init?.credentials ?? (resource instanceof Request ? resource.credentials : 'same-origin')
        };
    }

    function isAntiforgeryFailureResponse(response) {
        if (!(response instanceof Response) || response.status !== 400) {
            return false;
        }

        const contentType = response.headers.get('content-type') || '';
        if (!contentType.toLowerCase().includes('application/json')) {
            return false;
        }

        return true;
    }

    async function responseContainsAntiforgeryError(response) {
        try {
            const payload = await response.clone().json();
            const errorText = String(payload?.error || payload?.message || '').toLowerCase();
            return errorText.includes('anti-forgery') || errorText.includes('antiforgery');
        } catch {
            return false;
        }
    }

    async function refreshCsrfToken() {
        if (csrfRefreshPromise) {
            return csrfRefreshPromise;
        }

        csrfRefreshPromise = (async () => {
            const response = await originalFetch(csrfRefreshEndpoint, {
                method: 'GET',
                credentials: 'same-origin',
                cache: 'no-store',
                headers: {
                    'X-Requested-With': 'XMLHttpRequest'
                }
            });
            if (!response.ok) {
                throw new Error(`Failed to refresh CSRF token (${response.status})`);
            }

            const payload = await response.json();
            const token = typeof payload?.requestToken === 'string' ? payload.requestToken.trim() : '';
            if (!token) {
                throw new Error('CSRF token refresh endpoint returned no token.');
            }

            writeCsrfToken(token);
            return token;
        })();

        try {
            return await csrfRefreshPromise;
        } finally {
            csrfRefreshPromise = null;
        }
    }

    globalThis.fetch = async (resource, init) => {
        const method = (init?.method || (resource instanceof Request ? resource.method : 'GET') || 'GET').toUpperCase();
        const requestInit = buildFetchInit(resource, init);
        const sameOriginUrl = resolveSameOriginUrl(resource);
        const headers = new Headers(init?.headers || (resource instanceof Request ? resource.headers : undefined));
        let hasHeaderChanges = false;

        if (sameOriginUrl && clientId && !headers.has(clientIdHeaderName)) {
            headers.set(clientIdHeaderName, clientId);
            hasHeaderChanges = true;
        }

        if (sameOriginUrl && unsafeMethods.has(method)) {
            const csrfToken = readCsrfToken();
            if (csrfToken && !headers.has('X-CSRF-TOKEN')) {
                headers.set('X-CSRF-TOKEN', csrfToken);
                hasHeaderChanges = true;
            }
        }

        if (hasHeaderChanges) {
            requestInit.headers = headers;
        }

        const response = await originalFetch(resource, requestInit);
        const shouldRetryForCsrf =
            sameOriginUrl
            && unsafeMethods.has(method)
            && !(resource instanceof Request)
            && sameOriginUrl.pathname !== csrfRefreshEndpoint
            && isAntiforgeryFailureResponse(response)
            && await responseContainsAntiforgeryError(response);

        if (!shouldRetryForCsrf) {
            return response;
        }

        try {
            const refreshedToken = await refreshCsrfToken();
            const retryHeaders = new Headers(requestInit.headers || {});
            retryHeaders.set('X-CSRF-TOKEN', refreshedToken);
            return await originalFetch(resource, { ...requestInit, headers: retryHeaders });
        } catch {
            return response;
        }
    };
})();

globalThis.DeezSpoTagRevealMaterialIconsWhenReady = function (selector) {
    if (!selector || typeof selector !== 'string') {
        return;
    }

    const page = document.querySelector(selector);
    if (!page) {
        return;
    }

    const reveal = () => page.classList.remove('material-icons-pending');
    if (!document.fonts || typeof document.fonts.load !== 'function') {
        reveal();
        return;
    }

    if (document.fonts.check('1em "Material Icons"')) {
        reveal();
        return;
    }

    document.fonts.load('1em "Material Icons"')
        .then(() => {
            if (document.fonts.check('1em "Material Icons"')) {
                reveal();
            }
        })
        .catch(() => {
            // Keep hidden rather than flashing icon-name fallback text.
        });
};

// Global DeezSpoTag namespace
globalThis.DeezSpoTag = {
    // Notification management
    notifications: {
        active: [],
        baseTop: 20,
        spacing: 10
    },
    crossDeviceSyncConnection: null,
    crossDeviceTracklistReloadPending: false,

    // Themed popup helpers
    ui: {
        showToast(message, options = {}) {
            const normalizedOptions = typeof options === 'string'
                ? { type: options }
                : (options || {});
            const type = normalizedOptions.type || 'info';

            if (typeof globalThis.DeezSpoTag?.showNotification === 'function') {
                globalThis.DeezSpoTag.showNotification(message, type, normalizedOptions);
            }
        },

        setDialogResizable(dialogEl, enabled) {
            if (!dialogEl) {
                return;
            }

            if (typeof dialogEl._appResizeCleanup === 'function') {
                dialogEl._appResizeCleanup();
                dialogEl._appResizeCleanup = null;
            }

            dialogEl.style.removeProperty('left');
            dialogEl.style.removeProperty('top');
            dialogEl.style.removeProperty('width');
            dialogEl.style.removeProperty('height');
            dialogEl.style.removeProperty('transform');

            const mobileViewport = globalThis.matchMedia?.('(max-width: 768px)')?.matches
                ?? globalThis.innerWidth <= 768;
            if (!enabled || mobileViewport) {
                return;
            }

            const initialRect = dialogEl.getBoundingClientRect();
            const fallbackWidth = Math.min(980, Math.floor(globalThis.innerWidth * 0.94));
            const fallbackHeight = Math.min(820, Math.floor(globalThis.innerHeight * 0.92));
            const startWidth = initialRect.width > 80 ? initialRect.width : fallbackWidth;
            const startHeight = initialRect.height > 80 ? initialRect.height : fallbackHeight;
            const startLeft = Math.max(0, Math.floor((globalThis.innerWidth - startWidth) / 2));
            const startTop = Math.max(0, Math.floor((globalThis.innerHeight - startHeight) / 2));
            dialogEl.style.left = `${Math.round(startLeft)}px`;
            dialogEl.style.top = `${Math.round(startTop)}px`;
            dialogEl.style.width = `${Math.round(startWidth)}px`;
            dialogEl.style.height = `${Math.round(startHeight)}px`;
            dialogEl.style.transform = 'none';

            const computed = globalThis.getComputedStyle(dialogEl);
            const minWidth = Number.parseFloat(computed.minWidth) || 420;
            const minHeight = Number.parseFloat(computed.minHeight) || 280;

            const directions = ['n', 'e', 's', 'w', 'ne', 'nw', 'se', 'sw'];
            const handles = directions.map((dir) => {
                const handle = document.createElement('button');
                handle.type = 'button';
                handle.className = 'app-modal-resize-handle';
                handle.setAttribute('aria-hidden', 'true');
                handle.setAttribute('tabindex', '-1');
                handle.dataset.dir = dir;
                dialogEl.appendChild(handle);
                return handle;
            });

            const listeners = [];
            handles.forEach((handle) => {
                const onPointerDown = (event) => {
                    if (event.button !== 0) {
                        return;
                    }

                    event.preventDefault();
                    const dir = handle.dataset.dir || '';
                    const startX = event.clientX;
                    const startY = event.clientY;
                    const startWidth = dialogEl.offsetWidth;
                    const startHeight = dialogEl.offsetHeight;
                    const startLeft = Number.parseFloat(dialogEl.style.left) || 0;
                    const startTop = Number.parseFloat(dialogEl.style.top) || 0;
                    const maxWidth = Math.max(minWidth, Math.floor(globalThis.innerWidth * 0.96));
                    const maxHeight = Math.max(minHeight, Math.floor(globalThis.innerHeight * 0.92));

                    const onPointerMove = (moveEvent) => {
                        const dx = moveEvent.clientX - startX;
                        const dy = moveEvent.clientY - startY;

                        let width = startWidth;
                        let height = startHeight;
                        let left = startLeft;
                        let top = startTop;

                        if (dir.includes('e')) {
                            width = startWidth + dx;
                        }
                        if (dir.includes('s')) {
                            height = startHeight + dy;
                        }
                        if (dir.includes('w')) {
                            width = startWidth - dx;
                            left = startLeft + dx;
                        }
                        if (dir.includes('n')) {
                            height = startHeight - dy;
                            top = startTop + dy;
                        }

                        width = Math.min(Math.max(width, minWidth), maxWidth);
                        height = Math.min(Math.max(height, minHeight), maxHeight);
                        left = Math.min(Math.max(left, 0), Math.max(0, globalThis.innerWidth - width));
                        top = Math.min(Math.max(top, 0), Math.max(0, globalThis.innerHeight - height));

                        if (dir.includes('w')) {
                            left = startLeft + (startWidth - width);
                            left = Math.min(Math.max(left, 0), Math.max(0, globalThis.innerWidth - width));
                        }
                        if (dir.includes('n')) {
                            top = startTop + (startHeight - height);
                            top = Math.min(Math.max(top, 0), Math.max(0, globalThis.innerHeight - height));
                        }

                        dialogEl.style.left = `${Math.round(left)}px`;
                        dialogEl.style.top = `${Math.round(top)}px`;
                        dialogEl.style.width = `${Math.round(width)}px`;
                        dialogEl.style.height = `${Math.round(height)}px`;
                    };

                    const onPointerUp = () => {
                        globalThis.removeEventListener('pointermove', onPointerMove);
                        globalThis.removeEventListener('pointerup', onPointerUp);
                    };

                    globalThis.addEventListener('pointermove', onPointerMove);
                    globalThis.addEventListener('pointerup', onPointerUp);
                };

                handle.addEventListener('pointerdown', onPointerDown);
                listeners.push([handle, onPointerDown]);
            });

            dialogEl._appResizeCleanup = () => {
                listeners.forEach(([handle, listener]) => {
                    handle.removeEventListener('pointerdown', listener);
                });
                handles.forEach((handle) => handle.remove());
                dialogEl.style.removeProperty('left');
                dialogEl.style.removeProperty('top');
                dialogEl.style.removeProperty('width');
                dialogEl.style.removeProperty('height');
                dialogEl.style.removeProperty('transform');
            };
        },

        ensureModal() {
            let modal = document.getElementById('appModal');
            if (modal) {
                return modal;
            }

            modal = document.createElement('div');
            modal.id = 'appModal';
            modal.className = 'app-modal hidden';
            modal.innerHTML = `
                <div class="app-modal-backdrop" data-modal-close></div>
                <div class="app-modal-dialog" role="dialog" aria-modal="true">
                    <div class="app-modal-header">
                        <h3 class="app-modal-title"></h3>
                        <button class="app-modal-close" type="button" aria-label="Close" data-modal-close>
                            <span class="material-icons">close</span>
                        </button>
                    </div>
                    <div class="app-modal-body">
                        <p class="app-modal-message"></p>
                    </div>
                    <div class="app-modal-footer"></div>
                </div>
            `;
            document.body.appendChild(modal);
            return modal;
        },

        showModal({ title, message, input, buttons, contentElement, dialogClass }) {
            const modal = this.ensureModal();
            const dialogEl = modal.querySelector('.app-modal-dialog');
            const titleEl = modal.querySelector('.app-modal-title');
            const messageEl = modal.querySelector('.app-modal-message');
            const bodyEl = modal.querySelector('.app-modal-body');
            const footerEl = modal.querySelector('.app-modal-footer');

            const previousDialogClass = (modal.dataset.dialogClass || '')
                .split(' ')
                .map((value) => value.trim())
                .filter(Boolean);
            previousDialogClass.forEach((className) => {
                dialogEl?.classList.remove(className);
            });
            delete modal.dataset.dialogClass;

            const nextDialogClass = typeof dialogClass === 'string'
                ? dialogClass.split(' ').map((value) => value.trim()).filter(Boolean)
                : [];
            nextDialogClass.forEach((className) => {
                dialogEl?.classList.add(className);
            });
            if (nextDialogClass.length) {
                modal.dataset.dialogClass = nextDialogClass.join(' ');
            }
            this.setDialogResizable(dialogEl, false);

            titleEl.textContent = title || 'Notice';
            // reset body/message
            messageEl.replaceChildren();
            messageEl.textContent = message || '';
            // remove any prior injected content
            bodyEl.querySelectorAll('.app-modal-content').forEach(el => el.remove());
            footerEl.replaceChildren();

            let inputEl = null;
            if (input) {
                inputEl = document.createElement('input');
                inputEl.className = 'app-modal-input';
                inputEl.type = input.type || 'text';
                inputEl.placeholder = input.placeholder || '';
                inputEl.value = input.value || '';
                inputEl.autocomplete = input.autocomplete || 'off';
                bodyEl.appendChild(inputEl);
            }

            const resolveButtons = buttons?.length
                ? buttons
                : [{ label: 'OK', value: true, primary: true }];

            let contentHost = null;
            if (contentElement) {
                contentHost = document.createElement('div');
                contentHost.className = 'app-modal-content';
                contentHost.appendChild(contentElement);
                bodyEl.appendChild(contentHost);
                // hide the message paragraph when custom content is present
                messageEl.textContent = '';
            }

            return new Promise((resolve) => {
                let actionInFlight = false;
                const closeElements = Array.from(modal.querySelectorAll('[data-modal-close]'));

                const setActionBusy = (busy, activeAction = null, busyLabel = '') => {
                    actionInFlight = busy;
                    modal.toggleAttribute('aria-busy', busy);
                    footerEl.querySelectorAll('.app-modal-action').forEach((buttonElement) => {
                        buttonElement.disabled = busy;
                    });
                    closeElements.forEach((element) => {
                        if ('disabled' in element) {
                            element.disabled = busy;
                        }
                    });
                    if (activeAction) {
                        if (busy) {
                            activeAction.dataset.defaultLabel = activeAction.textContent || '';
                            activeAction.textContent = busyLabel || 'Working...';
                        } else {
                            activeAction.textContent = activeAction.dataset.defaultLabel || activeAction.textContent;
                            delete activeAction.dataset.defaultLabel;
                        }
                    }
                };

                const cleanup = () => {
                    modal.classList.add('hidden');
                    delete modal.dataset.open;
                    modal.removeAttribute('aria-busy');
                    document.body.classList.remove('app-modal-open');
                    document.documentElement.classList.remove('app-modal-open');
                    modal.querySelectorAll('[data-modal-close]').forEach((el) => {
                        el.removeEventListener('click', onCancel);
                    });
                    globalThis.removeEventListener('keydown', onKeydown);
                    nextDialogClass.forEach((className) => {
                        dialogEl?.classList.remove(className);
                    });
                    this.setDialogResizable(dialogEl, false);
                    delete modal.dataset.dialogClass;
                    if (inputEl) inputEl.remove();
                    if (contentHost) contentHost.remove();
                };

                const onCancel = () => {
                    if (actionInFlight) {
                        return;
                    }
                    resolve({ value: null, inputValue: inputEl ? inputEl.value : null });
                    cleanup();
                };

                const onKeydown = (event) => {
                    if (event.key === 'Escape') {
                        event.preventDefault();
                        onCancel();
                    }
                    if (event.key === 'Enter' && inputEl) {
                        event.preventDefault();
                        const primary = footerEl.querySelector('.app-modal-action.primary');
                        if (primary) {
                            primary.click();
                        }
                    }
                };

                resolveButtons.forEach((button) => {
                    const action = document.createElement('button');
                    action.type = 'button';
                    action.className = 'app-modal-action';
                    if (button.primary) {
                        action.classList.add('primary');
                    }
                    if (button.danger) {
                        action.classList.add('danger');
                    }
                    action.textContent = button.label;
                    action.addEventListener('click', async () => {
                        if (actionInFlight) {
                            return;
                        }

                        if (typeof button.onClick === 'function') {
                            setActionBusy(true, action, button.busyLabel);
                            let shouldClose = false;
                            try {
                                shouldClose = await button.onClick({
                                    inputValue: inputEl ? inputEl.value : null
                                }) !== false;
                            } catch (error) {
                                console.error('Modal action failed.', error);
                            }

                            if (!shouldClose) {
                                setActionBusy(false, action);
                                return;
                            }
                        }

                        resolve({ value: button.value, inputValue: inputEl ? inputEl.value : null });
                        cleanup();
                    });
                    footerEl.appendChild(action);
                });

                modal.querySelectorAll('[data-modal-close]').forEach((el) => {
                    el.addEventListener('click', onCancel);
                });

                globalThis.addEventListener('keydown', onKeydown);

                modal.classList.remove('hidden');
                modal.dataset.open = 'true';
                document.body.classList.add('app-modal-open');
                document.documentElement.classList.add('app-modal-open');
                globalThis.requestAnimationFrame(() => {
                    this.setDialogResizable(dialogEl, nextDialogClass.includes('is-resizable'));
                });

                setTimeout(() => {
                    if (inputEl) {
                        inputEl.focus();
                        return;
                    }
                    const primary = footerEl.querySelector('.app-modal-action.primary') || footerEl.querySelector('.app-modal-action');
                    if (primary) {
                        primary.focus();
                    }
                }, 0);
            });
        },

        alert(message, options = {}) {
            return this.showModal({
                title: options.title || 'Notice',
                message,
                buttons: [{ label: options.okText || 'OK', value: true, primary: true }],
                contentElement: options.contentElement || null
            });
        },

        confirm(message, options = {}) {
            return this.showModal({
                title: options.title || 'Confirm',
                message,
                buttons: [
                    { label: options.cancelText || 'Cancel', value: false },
                    { label: options.okText || 'OK', value: true, primary: true }
                ]
            }).then(result => Boolean(result.value));
        },

        prompt(message, options = {}) {
            return this.showModal({
                title: options.title || 'Input',
                message,
                input: {
                    type: options.type || 'text',
                    placeholder: options.placeholder || '',
                    value: options.value || '',
                    autocomplete: options.autocomplete || 'off'
                },
                buttons: [
                    { label: options.cancelText || 'Cancel', value: null },
                    { label: options.okText || 'OK', value: 'ok', primary: true }
                ]
            }).then(result => {
                if (result.value === null) {
                    return null;
                }
                return result.inputValue ?? '';
            });
        },

        browseServerFolder(options = {}) {
            const modal = this.ensureModal();
            const titleEl = modal.querySelector('.app-modal-title');
            const messageEl = modal.querySelector('.app-modal-message');
            const bodyEl = modal.querySelector('.app-modal-body');
            const footerEl = modal.querySelector('.app-modal-footer');

            titleEl.textContent = options.title || 'Browse Server Folder';
            messageEl.textContent = options.message || 'Browse folders visible to the server or container.';
            bodyEl.querySelectorAll('.app-modal-content').forEach((el) => el.remove());
            footerEl.innerHTML = '';

            const contentHost = document.createElement('div');
            contentHost.className = 'app-modal-content folder-browser-modal';
            contentHost.innerHTML = `
                <div class="folder-browser-toolbar">
                    <input class="app-modal-input folder-browser-path" type="text" autocomplete="off" />
                    <button type="button" class="app-modal-action folder-browser-go">Go</button>
                </div>
                <div class="folder-browser-current"></div>
                <div class="folder-browser-status"></div>
                <div class="folder-browser-list" role="listbox" aria-label="Server folders"></div>
            `;
            bodyEl.appendChild(contentHost);

            const pathInput = contentHost.querySelector('.folder-browser-path');
            const currentEl = contentHost.querySelector('.folder-browser-current');
            const statusEl = contentHost.querySelector('.folder-browser-status');
            const listEl = contentHost.querySelector('.folder-browser-list');
            const goButton = contentHost.querySelector('.folder-browser-go');

            const cancelButton = document.createElement('button');
            cancelButton.type = 'button';
            cancelButton.className = 'app-modal-action';
            cancelButton.textContent = options.cancelText || 'Cancel';

            const selectButton = document.createElement('button');
            selectButton.type = 'button';
            selectButton.className = 'app-modal-action primary';
            selectButton.textContent = options.selectText || 'Select Folder';
            selectButton.disabled = true;

            footerEl.appendChild(cancelButton);
            footerEl.appendChild(selectButton);

            const apiPath = options.apiPath || '/api/library/folders/browse';
            let currentPath = typeof options.startPath === 'string' ? options.startPath.trim() : '';
            let destroyed = false;
            let resolvePromise = () => {};
            const closeElements = Array.from(modal.querySelectorAll('[data-modal-close]'));

            const cleanup = () => {
                destroyed = true;
                modal.classList.add('hidden');
                delete modal.dataset.open;
                document.body.classList.remove('app-modal-open');
                document.documentElement.classList.remove('app-modal-open');
                contentHost.remove();
                cancelButton.removeEventListener('click', onCancel);
                selectButton.removeEventListener('click', onSelect);
                goButton.removeEventListener('click', onGo);
                pathInput.removeEventListener('keydown', onPathKeydown);
                globalThis.removeEventListener('keydown', onKeydown);
                closeElements.forEach((el) => {
                    el.removeEventListener('click', onCancel);
                });
            };

            const finish = (value) => {
                resolvePromise(value);
                cleanup();
            };

            const onCancel = () => finish(null);
            const onSelect = () => finish(currentPath || '');

            const renderEntries = (data) => {
                currentPath = typeof data.path === 'string' ? data.path : '';
                pathInput.value = currentPath;
                currentEl.textContent = currentPath || 'Select a root folder';
                selectButton.disabled = !currentPath;
                listEl.innerHTML = '';

                if (data.parentPath) {
                    const up = document.createElement('button');
                    up.type = 'button';
                    up.className = 'folder-browser-entry folder-browser-entry-up';
                    up.innerHTML = '<span class="folder-browser-entry-icon">..</span><span class="folder-browser-entry-name">Up</span>';
                    up.addEventListener('click', async () => {
                        await loadPath(data.parentPath);
                    });
                    listEl.appendChild(up);
                }

                const entries = Array.isArray(data.entries) ? data.entries : [];
                if (entries.length === 0) {
                    const empty = document.createElement('div');
                    empty.className = 'folder-browser-empty';
                    empty.textContent = 'No folders available here.';
                    listEl.appendChild(empty);
                    return;
                }

                entries.forEach((entry) => {
                    const row = document.createElement('button');
                    row.type = 'button';
                    row.className = 'folder-browser-entry';
                    row.innerHTML = '<span class="folder-browser-entry-icon"><i class="fas fa-folder"></i></span><span class="folder-browser-entry-name"></span>';
                    const nameEl = row.querySelector('.folder-browser-entry-name');
                    if (nameEl) {
                        nameEl.textContent = entry.name || entry.path || '';
                    }
                    row.addEventListener('click', async () => {
                        await loadPath(entry.path || '');
                    });
                    listEl.appendChild(row);
                });
            };

            const loadPath = async (requestedPath) => {
                statusEl.textContent = 'Loading...';
                const url = new URL(apiPath, globalThis.location.origin);
                if (requestedPath?.trim()) {
                    url.searchParams.set('path', requestedPath.trim());
                }

                try {
                    const response = await fetch(url.toString(), { credentials: 'same-origin' });
                    const data = await response.json().catch(() => ({}));
                    if (!response.ok) {
                        throw new Error(data?.error || `HTTP ${response.status}`);
                    }

                    if (destroyed) {
                        return;
                    }

                    renderEntries(data);
                    statusEl.textContent = '';
                } catch (error) {
                    statusEl.textContent = error?.message || 'Browse failed.';
                }
            };

            const onGo = async () => {
                await loadPath(pathInput.value || '');
            };

            const onPathKeydown = async (event) => {
                if (event.key === 'Enter') {
                    event.preventDefault();
                    await onGo();
                }
            };

            const onKeydown = (event) => {
                if (event.key === 'Escape') {
                    event.preventDefault();
                    onCancel();
                }
            };

            cancelButton.addEventListener('click', onCancel);
            selectButton.addEventListener('click', onSelect);
            goButton.addEventListener('click', onGo);
            pathInput.addEventListener('keydown', onPathKeydown);
            globalThis.addEventListener('keydown', onKeydown);

            closeElements.forEach((el) => {
                el.addEventListener('click', onCancel);
            });

            modal.classList.remove('hidden');
            modal.dataset.open = 'true';
            document.body.classList.add('app-modal-open');
            document.documentElement.classList.add('app-modal-open');

            return new Promise((resolve) => {
                resolvePromise = resolve;
                void loadPath(currentPath);
                globalThis.setTimeout(() => pathInput.focus(), 0);
            });
        }
    },

    getClientId() {
        const value = typeof globalThis.DeezSpoTagClientId === 'string'
            ? globalThis.DeezSpoTagClientId.trim()
            : '';
        return value;
    },

    initializeCrossDeviceSync() {
        if (this.isLoginRoute()) {
            return;
        }

        const signalR = globalThis.signalR;
        if (!signalR || typeof signalR.HubConnectionBuilder !== 'function') {
            return;
        }

        if (this.crossDeviceSyncConnection) {
            return;
        }

        const connection = new signalR.HubConnectionBuilder()
            .withUrl('/crossDeviceSyncHub')
            .withAutomaticReconnect()
            .build();

        connection.on('tracklistUpdated', (eventPayload) => {
            this.handleCrossDeviceTracklistUpdated(eventPayload);
        });
        connection.on('libraryUpdated', (eventPayload) => {
            this.handleLibraryUpdated(eventPayload);
        });
        connection.on('watchlistUpdated', (eventPayload) => {
            this.handleWatchlistUpdated(eventPayload);
        });
        connection.on('deezerConnectionStateChanged', () => {
            this.loadConnectedPlatforms({ force: true, reason: 'deezer-state-event' });
        });
        connection.on('publicDownloadSessionStateChanged', () => {
            this.loadConnectedPlatforms({ force: true, reason: 'public-download-session-event' });
        });

        connection.onreconnected(() => {
            this.loadConnectedPlatforms({ force: true, reason: 'signalr-reconnected' });
        });

        connection.start()
            .then(() => this.loadConnectedPlatforms({ force: true, reason: 'signalr-connected' }))
            .catch((error) => {
                console.debug('Cross-device sync unavailable.', error);
            });

        this.crossDeviceSyncConnection = connection;
    },

    handleLibraryUpdated(eventPayload) {
        globalThis.dispatchEvent(new CustomEvent('deezspotag:library-updated', {
            detail: eventPayload || {}
        }));
    },

    handleWatchlistUpdated(eventPayload) {
        globalThis.dispatchEvent(new CustomEvent('deezspotag:watchlist-updated', {
            detail: eventPayload || {}
        }));
    },

    handleCrossDeviceTracklistUpdated(eventPayload) {
        if (!this.matchesCurrentTracklist(eventPayload)) {
            return;
        }

        if (this.crossDeviceTracklistReloadPending) {
            return;
        }

        this.crossDeviceTracklistReloadPending = true;
        this.showNotification('Tracklist updated on another device. Reloading...', 'info');

        globalThis.setTimeout(() => {
            const url = new URL(globalThis.location.href);
            url.searchParams.set('refresh', '1');
            globalThis.location.replace(url.toString());
        }, 250);
    },

    matchesCurrentTracklist(eventPayload) {
        const current = this.resolveCurrentTracklistContext();
        if (!current) {
            return false;
        }

        const sourceClientId = String(eventPayload?.sourceClientId || '').trim();
        const localClientId = this.getClientId();
        if (sourceClientId && localClientId && sourceClientId === localClientId) {
            return false;
        }

        const eventType = String(eventPayload?.tracklistType || '').trim().toLowerCase();
        const eventId = String(eventPayload?.tracklistId || '').trim();
        if (!eventType || !eventId) {
            return false;
        }

        return current.type === eventType && current.id === eventId;
    },

    resolveCurrentTracklistContext() {
        const path = String(globalThis.location.pathname || '').trim().toLowerCase();
        if (path !== '/tracklist') {
            return null;
        }

        const searchParams = new URLSearchParams(globalThis.location.search || '');
        const source = String(searchParams.get('source') || 'deezer').trim().toLowerCase();
        if (source && source !== 'deezer') {
            return null;
        }

        const type = String(searchParams.get('type') || '').trim().toLowerCase();
        const id = String(searchParams.get('id') || '').trim();
        if (!type || !id) {
            return null;
        }

        return { type, id };
    },

    // Initialize the application
    init() {
        this.bindGlobalEvents();
        this.initializeNavigation();
        this.updateThemedPlatformIcons();
        this.initializeCrossDeviceSync();
        if (this.isLoginRoute()) {
            globalThis.setTimeout(() => this.loadConnectedPlatforms({ force: true, checkPublicApis: true, reason: 'login-init' }), 350);
        } else {
            this.loadConnectedPlatforms({ force: true, checkPublicApis: true, reason: 'init' });
            this.startConnectedPlatformsAutoRefresh();
        }
        this.initializePwaInstallPrompt();
        console.log('DeezSpoTag initialized');
    },

    isLoginRoute() {
        const path = (globalThis.location.pathname || '').toLowerCase();
        return path === '/login' || path.startsWith('/login/');
    },

    // Bind global event handlers
    bindGlobalEvents() {
        document.addEventListener('click', (e) => {
            const disabledButton = e.target.closest('button[disabled]');
            if (disabledButton) {
                e.preventDefault();
                if (disabledButton.dataset.notImplemented === 'true') {
                    this.showNotification('This feature is not yet implemented.', 'info');
                }
                return;
            }

            const navbarToggler = e.target.closest('.navbar-toggler');
            if (navbarToggler) {
                document.querySelectorAll('.navbar-collapse').forEach((element) => {
                    element.classList.toggle('show');
                });
            }
        });
    },

    initializePwaInstallPrompt() {
        const promptEl = document.getElementById('pwaInstallPrompt');
        if (!promptEl) {
            return;
        }

        const installButton = promptEl.querySelector('[data-pwa-install]');
        const dismissButton = promptEl.querySelector('[data-pwa-dismiss]');
        const messageEl = promptEl.querySelector('[data-pwa-message]');
        let deferredPrompt = null;

        if (this.isPwaStandalone()) {
            return;
        }

        if (this.hasRecentPwaDismissal()) {
            return;
        }

        const isIOS = /iPad|iPhone|iPod/.test(navigator.userAgent) && !globalThis.MSStream;
        if (isIOS) {
            this.prepareIosPwaPrompt(promptEl, messageEl, installButton);
        }

        const showPrompt = () => {
            if (!promptEl.classList.contains('hidden')) {
                return;
            }
            promptEl.classList.remove('hidden');
        };

        const handleBeforeInstallPrompt = (event) => {
            event.preventDefault();
            deferredPrompt = event;
            setTimeout(showPrompt, 1500);
        };

        globalThis.addEventListener('beforeinstallprompt', handleBeforeInstallPrompt);

        if (installButton) {
            installButton.addEventListener('click', async () => {
                if (!deferredPrompt) {
                    return;
                }
                deferredPrompt.prompt();
                try {
                    await deferredPrompt.userChoice;
                } finally {
                    deferredPrompt = null;
                    promptEl.classList.add('hidden');
                }
            });
        }

        if (dismissButton) {
            dismissButton.addEventListener('click', () => {
                promptEl.classList.add('hidden');
                const _pwaDismissedAt = Date.now();
                localStorage.setItem('pwa-prompt-dismissed', _pwaDismissedAt.toString());
                if (globalThis.UserPrefs) globalThis.UserPrefs.set('pwaPromptDismissedAt', _pwaDismissedAt);
            });
        }

        globalThis.addEventListener('appinstalled', () => {
            promptEl.classList.add('hidden');
        });
    },

    isPwaStandalone() {
        return globalThis.matchMedia('(display-mode: standalone)').matches || globalThis.navigator.standalone === true;
    },

    hasRecentPwaDismissal() {
        const dismissedAt = localStorage.getItem('pwa-prompt-dismissed');
        if (!dismissedAt) {
            return false;
        }

        const dismissedTime = Number.parseInt(dismissedAt, 10);
        if (Number.isNaN(dismissedTime)) {
            return false;
        }

        const sevenDays = 7 * 24 * 60 * 60 * 1000;
        return Date.now() - dismissedTime < sevenDays;
    },

    prepareIosPwaPrompt(promptEl, messageEl, installButton) {
        if (messageEl) {
            messageEl.textContent = 'Tap Share, then "Add to Home Screen" to install DeezSpoTag.';
        }
        if (installButton) {
            installButton.classList.add('hidden');
        }
        globalThis.setTimeout(() => {
            promptEl.classList.remove('hidden');
        }, 2500);
    },

    // Initialize navigation
    initializeNavigation() {
        // Set active nav item based on current page
        const currentPath = globalThis.location.pathname;
        // Scope to sidebar navigation only; tab controls also use .nav-link.
        document.querySelectorAll('.sidebar .menu-item').forEach((item) => {
            item.classList.remove('active');
        });
        document.querySelectorAll(`.sidebar .menu-item[href="${currentPath}"]`).forEach((item) => {
            item.classList.add('active');
        });
    },

    platformIconMap: {},

    authRequiredPlatforms: new Set(),

    platformDisplayOrder: [],

    connectedPlatformsRefreshIntervalMs: 180000,
    connectedPlatformsProbeMinIntervalMs: 30000,
    connectedPlatformsRefreshTimerId: null,
    connectedPlatformsRefreshInFlight: false,
    connectedPlatformsRefreshPending: null,
    connectedPlatformsFocusHandler: null,
    connectedPlatformsVisibilityHandler: null,
    connectedPlatformsLastProbeAt: 0,
    connectedPlatformsHasRendered: false,
    connectedPlatformsLastRenderSignature: null,
    platformRegistryLoaded: false,
    platformRegistryLoadPromise: null,

    platformDisplayNames: {},

    platformLoginTabMap: {},

    setLoginTabPreference(loginTabId) {
        if (!loginTabId) {
            return;
        }

        try {
            sessionStorage.setItem('deezspotag-login-active-tab', loginTabId);
        } catch {
            // Ignore private mode/session storage errors.
        }
    },

    getPlatformNavigationTarget(id) {
        const loginTabId = this.platformLoginTabMap[id];
        if (loginTabId) {
            return {
                href: `/Login?tab=${encodeURIComponent(loginTabId)}`,
                loginTabId
            };
        }

        return {
            href: `/AutoTag?tab=autotag-platforms-panel&platform=${encodeURIComponent(id)}`,
            loginTabId: null
        };
    },

    getAutoTagSelectedPlatforms() {
        try {
            const raw = localStorage.getItem('autotag-selected-platforms');
            const parsed = raw ? JSON.parse(raw) : [];
            if (!Array.isArray(parsed)) {
                return [];
            }

            const normalized = [];
            const seen = new Set();
            parsed.forEach((id) => {
                const key = this.normalizePlatformId(id);
                if (!key || seen.has(key) || !this.platformIconMap[key]) {
                    return;
                }
                seen.add(key);
                normalized.push(key);
            });
            return normalized;
        } catch {
            return [];
        }
    },

    normalizePlatformId(id) {
        return String(id || '').trim().toLowerCase();
    },

    getPlatformDisplayName(id) {
        if (this.platformDisplayNames[id]) {
            return this.platformDisplayNames[id];
        }
        return id;
    },

    resolvePlatformIcon(platformId, fallbackIcon = '') {
        const normalized = this.normalizePlatformId(platformId);
        if (normalized === 'tidal') {
            return this.isCurrentThemeLightSurface()
                ? '/images/icons/tidal-dark.png'
                : '/images/icons/tidal-light.png';
        }

        return fallbackIcon || this.platformIconMap[normalized] || '';
    },

    isCurrentThemeLightSurface() {
        const root = document.documentElement;
        const explicitTheme = String(root?.dataset?.theme || '').trim().toLowerCase();
        if (explicitTheme === 'amoled-inverse') {
            return true;
        }

        const styles = getComputedStyle(root);
        const bg = styles.getPropertyValue('--bg-quaternary').trim()
            || styles.getPropertyValue('--bg-secondary').trim();
        const rgb = this.parseCssColor(bg);
        if (!rgb) {
            return false;
        }

        const luminance = (0.2126 * rgb.r) + (0.7152 * rgb.g) + (0.0722 * rgb.b);
        return luminance > 180;
    },

    parseCssColor(value) {
        const normalized = String(value || '').trim();
        if (!normalized) {
            return null;
        }

        const hex = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(normalized);
        if (hex) {
            const raw = hex[1];
            const full = raw.length === 3
                ? raw.split('').map(char => `${char}${char}`).join('')
                : raw;
            return {
                r: Number.parseInt(full.slice(0, 2), 16),
                g: Number.parseInt(full.slice(2, 4), 16),
                b: Number.parseInt(full.slice(4, 6), 16)
            };
        }

        const rgb = /^rgba?\((\d+),\s*(\d+),\s*(\d+)/i.exec(normalized);
        if (!rgb) {
            return null;
        }

        return {
            r: Number(rgb[1]),
            g: Number(rgb[2]),
            b: Number(rgb[3])
        };
    },

    updateThemedPlatformIcons() {
        document.querySelectorAll('img[data-platform-icon]').forEach((img) => {
            const platformId = img.dataset.platformIcon;
            const src = this.resolvePlatformIcon(platformId, img.getAttribute('src') || '');
            if (src && img.getAttribute('src') !== src) {
                img.setAttribute('src', src);
            }
        });
    },

    getPlatformDisplayOrder(ids) {
        const uniqueIds = Array.from(new Set(ids));
        const ordered = this.platformDisplayOrder.filter((id) => uniqueIds.includes(id));
        const remaining = uniqueIds
            .filter((id) => !this.platformDisplayOrder.includes(id))
            .sort((a, b) => a.localeCompare(b));
        return ordered.concat(remaining);
    },

    buildInitialPlatformStates(selected = []) {
        const ids = new Set();
        this.authRequiredPlatforms.forEach((id) => {
            if (this.platformIconMap[id]) {
                ids.add(id);
            }
        });

        selected.forEach((id) => {
            if (this.platformIconMap[id]) {
                ids.add(id);
            }
        });

        const states = {};
        this.getPlatformDisplayOrder(Array.from(ids)).forEach((id) => {
            states[id] = {
                active: false,
                reason: null,
                publicApiStatus: ['qobuz', 'tidal', 'amazonmusic'].includes(id) ? 'unknown' : null,
                publicApiOnlineCount: null
            };
        });

        return states;
    },

    setPlatformState(states, id, active, reason = null) {
        if (!states || !this.platformIconMap[id]) {
            return;
        }

        if (!states[id]) {
            states[id] = { active: false, reason: null, publicApiStatus: null, publicApiOnlineCount: null };
        }

        states[id].active = Boolean(active);
        states[id].reason = reason;
    },

    setPlatformPublicApiStatus(states, id, status, onlineCount = null) {
        if (!states?.[id] || !['qobuz', 'tidal', 'amazonmusic'].includes(id)) {
            return;
        }

        const normalized = String(status || '').trim().toLowerCase();
        states[id].publicApiStatus = ['online', 'offline', 'unknown'].includes(normalized)
            ? normalized
            : 'unknown';
        const parsedCount = Number(onlineCount);
        states[id].publicApiOnlineCount = Number.isInteger(parsedCount) && parsedCount >= 0
            ? parsedCount
            : null;
    },

    normalizeConnectedPlatformStates(platformsOrStates) {
        const states = {};
        if (Array.isArray(platformsOrStates)) {
            const normalizedIds = platformsOrStates
                .map((id) => this.normalizePlatformId(id))
                .filter((id) => this.platformIconMap[id]);

            this.getPlatformDisplayOrder(normalizedIds).forEach((id) => {
                if (this.platformIconMap[id]) {
                    states[id] = { active: true, reason: null, publicApiStatus: null, publicApiOnlineCount: null };
                }
            });
            return states;
        }

        if (platformsOrStates && typeof platformsOrStates === 'object') {
            const merged = {};
            Object.keys(platformsOrStates).forEach((rawId) => {
                const id = this.normalizePlatformId(rawId);
                if (!this.platformIconMap[id]) {
                    return;
                }

                const value = platformsOrStates[rawId];
                if (!merged[id]) {
                    merged[id] = {
                        active: false,
                        reason: null,
                        publicApiStatus: null,
                        publicApiOnlineCount: null
                    };
                }
                if (value?.active === true) {
                    merged[id].active = true;
                }
                if (!merged[id].reason && value?.reason) {
                    merged[id].reason = value.reason;
                }
                const publicApiStatus = String(value?.publicApiStatus || '').trim().toLowerCase();
                if (['qobuz', 'tidal', 'amazonmusic'].includes(id) && ['online', 'offline', 'unknown'].includes(publicApiStatus)) {
                    merged[id].publicApiStatus = publicApiStatus;
                    const parsedCount = Number(value?.publicApiOnlineCount);
                    merged[id].publicApiOnlineCount = Number.isInteger(parsedCount) && parsedCount >= 0
                        ? parsedCount
                        : null;
                }
            });

            const ids = Object.keys(merged);
            this.getPlatformDisplayOrder(ids).forEach((id) => {
                const value = merged[id];
                states[id] = {
                    active: Boolean(value?.active),
                    reason: value?.reason || null,
                    publicApiStatus: value?.publicApiStatus || null,
                    publicApiOnlineCount: value?.publicApiOnlineCount ?? null
                };
            });
        }

        return states;
    },

    buildCachedPlatformStates(snapshotStates, selected = []) {
        const selectedSet = new Set((Array.isArray(selected) ? selected : []).map((id) => this.normalizePlatformId(id)));
        const baseline = this.buildInitialPlatformStates(Array.from(selectedSet));
        const normalized = this.normalizeConnectedPlatformStates(snapshotStates);
        Object.entries(normalized).forEach(([id, status]) => {
            if (!this.authRequiredPlatforms.has(id) && !selectedSet.has(id)) {
                return;
            }
            this.setPlatformState(baseline, id, status?.active === true, status?.reason || null);
            this.setPlatformPublicApiStatus(
                baseline,
                id,
                status?.publicApiStatus,
                status?.publicApiOnlineCount);
        });
        return baseline;
    },

    getConnectedPlatformsRenderSignature(entries) {
        return entries
            .map(([id, status]) => `${id}:${status?.active === true ? 1 : 0}:${status?.reason || ''}:${status?.publicApiStatus || ''}:${status?.publicApiOnlineCount ?? ''}`)
            .join('|');
    },

    getCachedConnectedPlatformsSnapshot() {
        try {
            const raw = localStorage.getItem('connected-platforms-cache');
            if (!raw) {
                return null;
            }

            const parsed = JSON.parse(raw);
            if (Array.isArray(parsed)) {
                return {
                    platforms: parsed,
                    statuses: null,
                    publicApiCheckedAt: null
                };
            }

            if (!parsed || !Array.isArray(parsed.platforms)) {
                return null;
            }

            return {
                platforms: parsed.platforms,
                statuses: parsed.statuses && typeof parsed.statuses === 'object'
                    ? parsed.statuses
                    : null,
                publicApiCheckedAt: Number(parsed.publicApiCheckedAt || 0) || null
            };
        } catch {
            return null;
        }
    },

    setCachedConnectedPlatforms(snapshot) {
        try {
            const payload = {
                platforms: Array.isArray(snapshot?.platforms) ? snapshot.platforms : [],
                statuses: snapshot?.statuses && typeof snapshot.statuses === 'object'
                    ? snapshot.statuses
                    : {},
                publicApiCheckedAt: snapshot?.publicApiCheckedAt || null
            };
            localStorage.setItem('connected-platforms-cache', JSON.stringify(payload));
        } catch {
            // Ignore storage errors (private mode, quota exceeded).
        }
    },

    getSpotifyErrorSummary(status) {
        const parts = [];
        const webError = status?.webPlayerError;
        const librespotError = status?.librespotError;
        if (webError) {
            parts.push(`web=${webError}`);
        }
        if (librespotError) {
            parts.push(`librespot=${librespotError}`);
        }
        return parts.join(', ');
    },

    async parseJsonSafely(response, endpointName) {
        try {
            return await response.json();
        } catch (error) {
            console.warn(`Failed to parse JSON from ${endpointName}`, error);
            return null;
        }
    },

    async ensurePlatformRegistryLoaded(force = false) {
        if (!force && this.platformRegistryLoaded) {
            return;
        }

        if (!force && this.platformRegistryLoadPromise) {
            await this.platformRegistryLoadPromise;
            return;
        }

        this.platformRegistryLoadPromise = (async () => {
            const nextIconMap = {};
            const nextNames = {};
            const nextOrder = [];
            const nextAuthRequired = new Set();
            const seen = new Set();
            const pushOrder = (id) => {
                const key = this.normalizePlatformId(id);
                if (!key || seen.has(key)) {
                    return;
                }
                seen.add(key);
                nextOrder.push(key);
            };

            try {
                const response = await fetch('/api/platform-registry', {
                    cache: 'no-store',
                    credentials: 'same-origin',
                    headers: { Accept: 'application/json' }
                });

                if (!response.ok) {
                    throw new Error(`HTTP ${response.status}`);
                }

                const data = await this.parseJsonSafely(response, '/api/platform-registry');
                const platforms = Array.isArray(data) ? data : [];
                const nextLoginTabMap = {};

                platforms.forEach((entry) => {
                    const id = this.normalizePlatformId(entry?.id);
                    if (!id) {
                        return;
                    }

                    const name = String(entry?.name || id).trim();
                    const icon = String(entry?.icon || "").trim();
                    const requiresAuth = entry?.requiresAuth === true;
                    const loginTabId = String(entry?.loginTabId || "").trim();

                    if (name) {
                        nextNames[id] = name;
                    }
                    if (icon) {
                        nextIconMap[id] = icon;
                    }
                    if (requiresAuth) {
                        nextAuthRequired.add(id);
                    }
                    if (loginTabId) {
                        nextLoginTabMap[id] = loginTabId;
                    }

                    pushOrder(id);
                });

                this.platformIconMap = nextIconMap;
                this.platformDisplayNames = nextNames;
                this.platformDisplayOrder = nextOrder;
                this.authRequiredPlatforms = nextAuthRequired;
                this.platformLoginTabMap = nextLoginTabMap;
            } catch (error) {
                console.error('Failed to load platform registry from /api/platform-registry.', error);
                this.platformIconMap = {};
                this.platformDisplayNames = {};
                this.platformDisplayOrder = [];
                this.authRequiredPlatforms = new Set();
                this.platformLoginTabMap = {};
            } finally {
                this.platformRegistryLoaded = true;
                this.platformRegistryLoadPromise = null;
            }
        })();

        await this.platformRegistryLoadPromise;
    },

    resolveConnectedPlatformsProbePlan(options = {}) {
        const now = Date.now();
        const force = options?.force === true;
        const shouldProbe = force
            || this.connectedPlatformsLastProbeAt === 0
            || (now - this.connectedPlatformsLastProbeAt) >= this.connectedPlatformsProbeMinIntervalMs;

        return {
            now,
            force,
            shouldProbe
        };
    },

    markConnectedPlatformsProbe(timestamp) {
        if (!timestamp) {
            return;
        }
        this.connectedPlatformsLastProbeAt = timestamp;
    },

    startConnectedPlatformsAutoRefresh() {
        if (this.connectedPlatformsRefreshTimerId !== null) {
            return;
        }

        this.connectedPlatformsRefreshTimerId = globalThis.setInterval(() => {
            if (document.visibilityState === 'visible') {
                this.loadConnectedPlatforms({ force: true, checkPublicApis: true, reason: 'timer' });
            }
        }, this.connectedPlatformsRefreshIntervalMs);

        if (!this.connectedPlatformsFocusHandler) {
            this.connectedPlatformsFocusHandler = () => {
                if (document.visibilityState === 'visible') {
                    this.loadConnectedPlatforms({ checkPublicApis: true, reason: 'focus' });
                }
            };
            globalThis.addEventListener('focus', this.connectedPlatformsFocusHandler);
        }

        if (!this.connectedPlatformsVisibilityHandler) {
            this.connectedPlatformsVisibilityHandler = () => {
                if (document.visibilityState === 'visible') {
                    this.loadConnectedPlatforms({ checkPublicApis: true, reason: 'visibility' });
                }
            };
            document.addEventListener('visibilitychange', this.connectedPlatformsVisibilityHandler);
        }
    },

    async loadConnectedPlatforms(options = {}) {
        if (document.visibilityState === 'hidden' && options.allowHidden !== true) {
            return;
        }
        const container = document.getElementById('connectedPlatformsList');
        if (!container) {
            return;
        }

        const probePlan = this.resolveConnectedPlatformsProbePlan(options);
        const registryPromise = this.ensurePlatformRegistryLoaded();
        const cached = this.getCachedConnectedPlatformsSnapshot();
        if (!probePlan.shouldProbe) {
            await registryPromise;
            const selected = this.getAutoTagSelectedPlatforms();
            const initialStates = this.buildInitialPlatformStates(selected);
            this.renderConnectedPlatformsFromSnapshot(cached, selected, initialStates);
            return;
        }

        if (this.connectedPlatformsRefreshInFlight) {
            this.queuePendingConnectedPlatformsRefresh(options);
            await registryPromise;
            const selected = this.getAutoTagSelectedPlatforms();
            const initialStates = this.buildInitialPlatformStates(selected);
            this.renderConnectedPlatformsFromSnapshot(cached, selected, initialStates);
            return;
        }

        this.connectedPlatformsRefreshInFlight = true;
        const fetchOptions = {
            cache: 'no-store',
            credentials: 'same-origin',
            headers: { Accept: 'application/json' }
        };
        const publicApiCheckDue = options?.checkPublicApis === true
            && (!cached?.publicApiCheckedAt
                || (Date.now() - cached.publicApiCheckedAt) >= this.connectedPlatformsRefreshIntervalMs);
        const statusResponsesPromise = this.fetchConnectedPlatformResponses(fetchOptions, {
            checkPublicApis: publicApiCheckDue
        });
        let platformStates = null;
        try {
            await registryPromise;
            const selected = this.getAutoTagSelectedPlatforms();
            const initialStates = this.buildInitialPlatformStates(selected);
            this.renderConnectedPlatformsFromSnapshot(cached, selected, initialStates);

            platformStates = this.resolveConnectedPlatformStates(cached, selected);
            const connected = new Set(
                Object.entries(platformStates)
                    .filter(([, status]) => status?.active === true)
                    .map(([id]) => id));
            this.seedSelectedConnectedPlatforms(selected, connected, platformStates);

            const settledResponses = await statusResponsesPromise;
            await this.applyAuthStatus(settledResponses.authResponse, settledResponses.authOk, connected, platformStates);
            await this.applyPublicApiStatus(
                settledResponses.publicApiResponse,
                settledResponses.publicApiOk,
                platformStates);
            const resolved = Array.from(connected);
            const requiredChecksCompleted = settledResponses.authCompleted || settledResponses.publicApiCompleted;
            const preserveIfEmpty = !requiredChecksCompleted;
            this.setCachedConnectedPlatforms({
                platforms: resolved,
                statuses: platformStates,
                publicApiCheckedAt: publicApiCheckDue && settledResponses.publicApiOk
                    ? Date.now()
                    : cached?.publicApiCheckedAt
            });
            this.renderConnectedPlatforms(platformStates, { preserveIfEmpty });
        } catch (error) {
            console.warn('Failed to refresh connected platform status', error);
            if (platformStates) {
                this.renderConnectedPlatforms(platformStates);
            }
        } finally {
            this.markConnectedPlatformsProbe(probePlan.now);
            this.connectedPlatformsRefreshInFlight = false;
            const pendingOptions = this.connectedPlatformsRefreshPending;
            this.connectedPlatformsRefreshPending = null;
            if (pendingOptions) {
                globalThis.setTimeout(() => this.loadConnectedPlatforms(pendingOptions), 150);
            }
        }
    },

    queuePendingConnectedPlatformsRefresh(options) {
        if (this.connectedPlatformsRefreshPending) {
            if (options?.force === true) {
                this.connectedPlatformsRefreshPending.force = true;
            }
            if (options?.checkPublicApis === true) {
                this.connectedPlatformsRefreshPending.checkPublicApis = true;
            }
            return;
        }

        this.connectedPlatformsRefreshPending = { ...options };
    },

    resolveConnectedPlatformStates(cached, selected) {
        if (cached?.statuses) {
            return this.buildCachedPlatformStates(cached.statuses, selected);
        }
        if (cached?.platforms?.length) {
            return this.buildCachedPlatformStates(cached.platforms, selected);
        }
        return this.buildInitialPlatformStates(selected);
    },

    renderConnectedPlatformsFromSnapshot(cached, selected, initialStates) {
        if (this.connectedPlatformsHasRendered) {
            return;
        }

        if (cached?.statuses && Object.keys(cached.statuses).length) {
            const cachedStates = this.buildCachedPlatformStates(cached.statuses, selected);
            this.renderConnectedPlatforms(cachedStates, { preserveIfEmpty: true });
            return;
        }

        if (cached?.platforms?.length) {
            const cachedStates = this.buildCachedPlatformStates(cached.platforms, selected);
            this.renderConnectedPlatforms(cachedStates, { preserveIfEmpty: true });
            return;
        }

        this.renderConnectedPlatforms(initialStates);
    },

    seedSelectedConnectedPlatforms(selected, connected, platformStates) {
        selected.forEach((id) => {
            if (!this.authRequiredPlatforms.has(id) && this.platformIconMap[id]) {
                connected.add(id);
                this.setPlatformState(platformStates, id, true, 'selected');
            }
        });
    },

    async fetchConnectedPlatformResponses(fetchOptions, options = {}) {
        const publicApiEndpoint = options?.checkPublicApis === true
            ? '/api/platform-auth/public-providers/status?check=true'
            : '/api/platform-auth/public-providers/status';
        const [authResult, publicApiResult] = await Promise.allSettled([
            fetch('/api/platform-auth', fetchOptions),
            fetch(publicApiEndpoint, fetchOptions)
        ]);

        const authResponse = authResult.status === 'fulfilled' ? authResult.value : null;
        const publicApiResponse = publicApiResult.status === 'fulfilled' ? publicApiResult.value : null;
        return {
            authResponse,
            authCompleted: authResult.status === 'fulfilled',
            authOk: Boolean(authResponse?.ok),
            publicApiResponse,
            publicApiCompleted: publicApiResult.status === 'fulfilled',
            publicApiOk: Boolean(publicApiResponse?.ok)
        };
    },

    async applyAuthStatus(authResponse, authOk, connected, platformStates) {
        if (!authOk) {
            return null;
        }

        const authData = await this.parseJsonSafely(authResponse, '/api/platform-auth');
        if (!authData) {
            return null;
        }

        this.applyCredentialPlatformStatus(authData, connected, platformStates);
        this.applyStreamingPlatformStatus(authData, connected, platformStates);

        return authData;
    },

    async applyPublicApiStatus(publicApiResponse, publicApiOk, platformStates) {
        if (!publicApiOk) {
            return null;
        }

        const data = await this.parseJsonSafely(
            publicApiResponse,
            '/api/platform-auth/public-providers/status');
        if (!data) {
            return null;
        }

        this.setPlatformPublicApiStatus(platformStates, 'qobuz', data.qobuz?.status, data.qobuz?.onlineCount);
        this.setPlatformPublicApiStatus(platformStates, 'tidal', data.tidal?.status, data.tidal?.onlineCount);
        this.setPlatformPublicApiStatus(
            platformStates,
            'amazonmusic',
            data.amazonMusic?.status,
            data.amazonMusic?.onlineCount);
        return data;
    },

    applyCredentialPlatformStatus(authData, connected, platformStates) {
        this.applySimpleCredentialState(
            authData.discogs?.tokenSaved === true,
            connected,
            platformStates,
            'discogs',
            'token');

        const lastFmApiKey = typeof authData.lastFm?.apiKey === 'string'
            ? authData.lastFm.apiKey.trim()
            : '';
        this.applySimpleCredentialState(
            authData.lastFm?.hasApiKey === true || lastFmApiKey.length > 0,
            connected,
            platformStates,
            'lastfm',
            'api-key');

        this.applySimpleCredentialState(
            authData.bpmSupreme?.email && authData.bpmSupreme?.passwordSaved === true,
            connected,
            platformStates,
            'bpmsupreme',
            'credentials');
        this.applySimpleCredentialState(
            authData.plex?.url && authData.plex?.tokenSaved === true,
            connected,
            platformStates,
            'plex',
            'credentials');
        this.applySimpleCredentialState(
            authData.jellyfin?.url && (authData.jellyfin?.apiKey || authData.jellyfin?.username),
            connected,
            platformStates,
            'jellyfin',
            'credentials');
        this.applySimpleCredentialState(
            authData.navidrome?.connected === true,
            connected,
            platformStates,
            'navidrome',
            'credentials');
        this.applySimpleCredentialState(
            authData.boomplay?.connected === true || authData.boomplay?.cookieSaved === true,
            connected,
            platformStates,
            'boomplay',
            'session');
    },

    applyStreamingPlatformStatus(authData, connected, platformStates) {
        const deezerState = ['disconnected', 'authenticating', 'connected', 'failed'].includes(authData.deezer?.state)
            ? authData.deezer.state
            : 'disconnected';
        const deezerConfigured = authData.deezer?.configured === true || authData.deezer?.live === true;
        this.applyConnectedFlagState(
            deezerConfigured,
            connected,
            platformStates,
            'deezer',
            deezerState,
            deezerState);
        this.applyConnectedFlagState(authData.spotifyConnected === true, connected, platformStates, 'spotify', 'librespot-blob', 'missing');
        this.applyConnectedFlagState(authData.appleMusic?.wrapperReady === true, connected, platformStates, 'applemusic', 'wrapper', 'wrapper');
        this.applyConnectedFlagState(authData.qobuz?.connected === true, connected, platformStates, 'qobuz', 'official-api', 'offline');
        this.applyConnectedFlagState(authData.tidal?.connected === true, connected, platformStates, 'tidal', 'official-api', 'offline');
        this.applyConnectedFlagState(authData.amazonMusic?.connected === true, connected, platformStates, 'amazonmusic', 'session', 'offline');
        this.applyConnectedFlagState(authData.beatport?.connected === true, connected, platformStates, 'beatport', 'oauth', 'missing');
        this.applyConnectedFlagState(
            authData.soulseek?.connected === true,
            connected,
            platformStates,
            'soulseek',
            'slskd',
            authData.soulseek?.status || 'offline');
    },

    applySimpleCredentialState(hasCredential, connected, platformStates, platform, detail) {
        if (hasCredential) {
            connected.add(platform);
            this.setPlatformState(platformStates, platform, true, detail);
        }
    },

    applyConnectedFlagState(isConnected, connected, platformStates, platform, connectedDetail, disconnectedDetail) {
        if (isConnected) {
            connected.add(platform);
            this.setPlatformState(platformStates, platform, true, connectedDetail);
            return;
        }

        this.setPlatformState(platformStates, platform, false, disconnectedDetail);
    },


    renderConnectedPlatforms(platformsOrStates, options = {}) {
        const container = document.getElementById('connectedPlatformsList');
        if (!container) {
            return;
        }

        const states = this.normalizeConnectedPlatformStates(platformsOrStates);
        const entries = Object.entries(states);
        if (entries.length === 0 && options.preserveIfEmpty) {
            return;
        }

        const signature = this.getConnectedPlatformsRenderSignature(entries);
        if (options.skipIfUnchanged !== false && signature === this.connectedPlatformsLastRenderSignature) {
            return;
        }

        this.connectedPlatformsLastRenderSignature = signature;
        this.connectedPlatformsHasRendered = true;
        container.innerHTML = '';

        if (entries.length === 0) {
            return;
        }

        entries.forEach(([id, status]) => {
            const icon = this.platformIconMap[id];
            if (!icon) {
                return;
            }
            const isActive = status?.active === true;
            const deezerStateLabels = {
                authenticating: 'Configured; connecting',
                connected: 'Connected',
                failed: 'Configured; temporarily unavailable',
                disconnected: 'Not connected'
            };
            const stateLabel = id === 'deezer' && deezerStateLabels[status?.reason]
                ? deezerStateLabels[status.reason]
                : isActive ? 'Connected' : 'Not connected';
            const publicApiStatus = ['qobuz', 'tidal', 'amazonmusic'].includes(id)
                && ['online', 'offline', 'unknown'].includes(status?.publicApiStatus)
                ? status.publicApiStatus
                : null;
            const publicApiOnlineCount = Number.isInteger(status?.publicApiOnlineCount)
                ? status.publicApiOnlineCount
                : null;
            let publicApiLabel = 'Public APIs: Not checked';
            if (publicApiStatus !== 'unknown' && publicApiOnlineCount !== null) {
                publicApiLabel = `Public APIs online: ${publicApiOnlineCount}`;
            } else if (publicApiStatus !== 'unknown') {
                const statusLabel = publicApiStatus === 'online' ? 'Online' : 'Offline';
                publicApiLabel = `Public APIs: ${statusLabel}`;
            }
            const target = this.getPlatformNavigationTarget(id);
            const wrapper = document.createElement('a');
            wrapper.className = `connected-platform-icon ${isActive ? 'connected-platform-icon--active' : 'connected-platform-icon--inactive'}`;
            wrapper.classList.add(`connected-platform-icon--platform-${id}`);
            if (publicApiStatus) {
                wrapper.classList.add(`connected-platform-icon--api-${publicApiStatus}`);
            }
            wrapper.href = target.href;
            const statusDescription = publicApiStatus
                ? `Account: ${stateLabel}; ${publicApiLabel}`
                : stateLabel;
            wrapper.title = `${this.getPlatformDisplayName(id)} (${statusDescription})`;
            wrapper.setAttribute('aria-label', `${this.getPlatformDisplayName(id)} (${statusDescription})`);
            wrapper.addEventListener('click', () => {
                if (target.loginTabId) {
                    this.setLoginTabPreference(target.loginTabId);
                }
            });
            const img = document.createElement('img');
            img.src = this.resolvePlatformIcon(id, icon);
            img.alt = this.getPlatformDisplayName(id);
            img.dataset.platformIcon = id;
            img.width = 16;
            img.height = 16;
            img.decoding = 'async';
            img.loading = 'lazy';
            img.onerror = null;
            wrapper.appendChild(img);
            container.appendChild(wrapper);
        });
    },

    setPinnedMessage(id, message, type = 'warning', options = {}) {
        const bannerId = `deezspot-pinned-${id}`;
        let banner = document.getElementById(bannerId);

        if (!message) {
            if (banner) {
                banner.remove();
            }
            return;
        }

        const alertClass = {
            'info': 'alert-info',
            'success': 'alert-success',
            'warning': 'alert-warning',
            'error': 'alert-danger'
        }[type] || 'alert-warning';

        if (banner) {
            banner.className = `alert ${alertClass} deezspot-pinned-banner`;
        } else {
            banner = document.createElement('div');
            banner.id = bannerId;
            banner.className = `alert ${alertClass} deezspot-pinned-banner`;
            banner.style.position = 'fixed';
            banner.style.top = '10px';
            banner.style.left = '50%';
            banner.style.transform = 'translateX(-50%)';
            banner.style.zIndex = '1070';
            banner.style.maxWidth = '90%';
            banner.style.minWidth = '280px';
            banner.style.padding = '10px 16px';
            banner.style.boxShadow = '0 4px 18px rgba(0, 0, 0, 0.15)';
            document.body.appendChild(banner);
        }

        banner.textContent = '';
        const messageSpan = document.createElement('span');
        messageSpan.textContent = message;
        banner.appendChild(messageSpan);

        const action = options?.action;
        const actionHref = this.sanitizeActionHref(action?.href);
        if (action?.label && actionHref) {
            const link = document.createElement('a');
            link.className = 'btn btn-sm btn-light ms-2';
            link.href = actionHref;
            link.textContent = action.label;
            banner.appendChild(link);
        }
    },

    sanitizeActionHref(href) {
        if (!href) {
            return '';
        }

        try {
            const url = new URL(href, globalThis.location.href);
            const protocol = url.protocol.toLowerCase();
            if (protocol !== 'http:' && protocol !== 'https:') {
                return '';
            }
            return url.toString();
        } catch {
            return '';
        }
    },

    normalizeNotificationActionHref(action) {
        const href = action?.href;
        if (!href || String(action?.label || '').trim().toLowerCase() !== 'view') {
            return href;
        }

        try {
            const url = new URL(href, globalThis.location.href);
            if (url.origin === globalThis.location.origin
                && url.pathname.replace(/\/+$/, '') === '/Activities'
                && !url.searchParams.has('tab')) {
                url.searchParams.set('tab', 'downloads-content');
                return url.toString();
            }
        } catch {
            return href;
        }

        return href;
    },

    // Show notification
    showNotification(message, type = 'info', options = {}) {
        const alertClass = {
            'info': 'alert-info',
            'success': 'alert-success',
            'warning': 'alert-warning',
            'error': 'alert-danger'
        }[type] || 'alert-info';

        // Calculate position based on existing notifications
        const topPosition = this.calculateNotificationPosition();

        const notificationElement = document.createElement('div');
        notificationElement.className = `alert ${alertClass} alert-dismissible fade show position-fixed deezspot-notification`;
        notificationElement.style.top = `${topPosition}px`;
        notificationElement.style.right = '20px';
        notificationElement.style.zIndex = '1060';
        notificationElement.style.maxWidth = '400px';
        notificationElement.style.transition = 'all 0.3s ease';

        const messageSpan = document.createElement('span');
        messageSpan.textContent = message;
        notificationElement.appendChild(messageSpan);

        const action = options?.action;
        const actionHref = this.sanitizeActionHref(this.normalizeNotificationActionHref(action));
        if (action?.label && actionHref) {
            const actionLink = document.createElement('a');
            actionLink.className = 'btn btn-sm btn-light ms-2';
            actionLink.href = actionHref;
            actionLink.textContent = action.label;
            notificationElement.appendChild(actionLink);
        }

        const dismissButton = document.createElement('button');
        dismissButton.type = 'button';
        dismissButton.className = 'btn-close';
        dismissButton.dataset.bsDismiss = 'alert';
        notificationElement.appendChild(dismissButton);

        // Add to active notifications
        this.notifications.active.push(notificationElement);

        document.body.appendChild(notificationElement);

        dismissButton.addEventListener('click', () => {
            notificationElement.remove();
            this.removeNotification(notificationElement);
        });

        // Auto-dismiss after 5 seconds
        setTimeout(() => {
            if (notificationElement.isConnected) {
                notificationElement.remove();
                this.removeNotification(notificationElement);
            }
        }, 5000);
    },

    // Calculate position for new notification
    calculateNotificationPosition() {
        let position = this.notifications.baseTop;
        
        this.notifications.active.forEach((notif) => {
            if (notif?.offsetHeight) {
                position += notif.offsetHeight + this.notifications.spacing;
            } else {
                // Fallback height if element not yet rendered
                position += 70 + this.notifications.spacing;
            }
        });

        return position;
    },

    // Remove notification and reposition others
    removeNotification(notificationElement) {
        const index = this.notifications.active.indexOf(notificationElement);
        if (index > -1) {
            this.notifications.active.splice(index, 1);
            this.repositionNotifications();
        }
    },

    // Reposition all active notifications
    repositionNotifications() {
        let position = this.notifications.baseTop;
        
        this.notifications.active.forEach((notif) => {
            if (notif?.style) {
                notif.style.top = position + 'px';
                position += (notif.offsetHeight || 70) + this.notifications.spacing;
            }
        });
    }
};

// Initialize when DOM is ready
document.addEventListener('DOMContentLoaded', () => {
    DeezSpoTag.init();
    globalThis.addEventListener('autotagPlatformsChanged', () => {
        DeezSpoTag.loadConnectedPlatforms({ force: true, reason: 'autotag-change' });
    });
    globalThis.addEventListener('themeChanged', () => {
        DeezSpoTag.updateThemedPlatformIcons();
    });
    globalThis.addEventListener('themeInitialized', () => {
        DeezSpoTag.updateThemedPlatformIcons();
    });
    globalThis.addEventListener('storage', (event) => {
        if (event.key === 'autotag-selected-platforms') {
            DeezSpoTag.loadConnectedPlatforms({ force: true, reason: 'storage-change' });
        }
    });
});

(function initNotificationsPanel() {
    const SEVERITY_CLASS = {
        info: 'notifications-item--info',
        warning: 'notifications-item--warning',
        actionrequired: 'notifications-item--actionrequired'
    };

    function escapeText(value) {
        const span = document.createElement('span');
        span.textContent = String(value ?? '');
        return span.innerHTML;
    }

    function renderNotifications(payload) {
        const list = document.getElementById('notificationsList');
        const badge = document.getElementById('notificationsBadge');
        if (!list) {
            return;
        }

        const unread = Number(payload?.unreadCount || 0);
        if (badge) {
            badge.hidden = unread <= 0;
            badge.textContent = unread > 99 ? '99+' : String(unread);
        }

        const items = Array.isArray(payload?.notifications) ? payload.notifications : [];
        if (items.length === 0) {
            list.innerHTML = '<div class="notifications-empty">Nothing to report.</div>';
            return;
        }

        list.innerHTML = items.map((item) => {
            const severity = String(item?.severity || 'info').toLowerCase();
            const classes = ['notifications-item'];
            if (!item?.isRead) {
                classes.push('notifications-item--unread');
            }
            classes.push(SEVERITY_CLASS[severity] || SEVERITY_CLASS.info);
            const count = Number(item?.occurrenceCount || 1);
            const suffix = count > 1 ? ` (${count}x)` : '';
            const actions = item?.isRead
                ? ''
                : `<button type="button" class="notifications-item-action" data-notification-read="${escapeText(item?.id)}">Mark read</button>`;
            const clear = `<button type="button" class="notifications-item-action" data-notification-clear="${escapeText(item?.id)}">Clear</button>`;
            const link = item?.link
                ? `<a class="notifications-item-action" href="${escapeText(item.link)}">Open</a>`
                : '';
            return `<div class="${classes.join(' ')}" data-notification-id="${escapeText(item?.id)}">
                <div class="notifications-item-title">${escapeText(item?.title)}${suffix}</div>
                <div class="notifications-item-body">${escapeText(item?.body)}</div>
                <div class="notifications-item-actions">${link}${actions}${clear}</div>
            </div>`;
        }).join('');
    }

    async function loadNotifications() {
        try {
            const response = await fetch('/api/notifications?limit=25');
            if (!response.ok) {
                return;
            }
            renderNotifications(await response.json());
        } catch (error) {
            console.warn('Failed to load notifications', error);
        }
    }

    async function markRead(id) {
        if (!id) {
            return;
        }
        try {
            await fetch('/api/notifications/read', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ ids: [id] })
            });
            await loadNotifications();
        } catch (error) {
            console.warn('Failed to mark notification read', error);
        }
    }

    async function clearNotifications(ids) {
        try {
            await fetch('/api/notifications/clear', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(ids ? { ids } : {})
            });
            await loadNotifications();
        } catch (error) {
            console.warn('Failed to clear notifications', error);
        }
    }

    async function markAllRead() {
        try {
            await fetch('/api/notifications/read-all', { method: 'POST' });
            await loadNotifications();
        } catch (error) {
            console.warn('Failed to mark notifications read', error);
        }
    }

    document.addEventListener('DOMContentLoaded', () => {
        const toggle = document.getElementById('notificationsToggle');
        const panel = document.getElementById('notificationsPanel');
        const markAll = document.getElementById('notificationsMarkAll');
        if (!toggle || !panel) {
            return;
        }

        toggle.addEventListener('click', async (event) => {
            event.preventDefault();
            const open = panel.hidden;
            panel.hidden = !open;
            toggle.setAttribute('aria-expanded', String(open));
            if (open) {
                await loadNotifications();
            }
        });

        if (markAll) {
            markAll.addEventListener('click', (event) => {
                event.preventDefault();
                void markAllRead();
            });
        }

        const list = document.getElementById('notificationsList');
        if (list) {
            list.addEventListener('click', (event) => {
                const readButton = event.target.closest('[data-notification-read]');
                if (readButton) {
                    event.preventDefault();
                    void markRead(readButton.dataset.notificationRead);
                    return;
                }

                const clearButton = event.target.closest('[data-notification-clear]');
                if (clearButton) {
                    event.preventDefault();
                    void clearNotifications([clearButton.dataset.notificationClear]);
                }
            });

        const clearAll = document.getElementById('notificationsClearAll');
        if (clearAll) {
            clearAll.addEventListener('click', (event) => {
                event.preventDefault();
                void clearNotifications(null);
            });
        }
        }

        void loadNotifications();
        window.DeezSpoTagNotifications = { refresh: loadNotifications };
    });
})();
