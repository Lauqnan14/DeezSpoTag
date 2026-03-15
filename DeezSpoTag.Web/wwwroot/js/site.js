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

    function readCookie(name) {
        const encodedName = `${encodeURIComponent(name)}=`;
        const parts = document.cookie ? document.cookie.split(';') : [];
        for (const part of parts) {
            const trimmed = part.trim();
            if (trimmed.startsWith(encodedName)) {
                return decodeURIComponent(trimmed.slice(encodedName.length));
            }
        }
        return '';
    }

    function resolveUrl(resource) {
        if (resource instanceof Request) {
            return resource.url;
        }
        return String(resource || '');
    }

    globalThis.fetch = (resource, init) => {
        const method = (init?.method || (resource instanceof Request ? resource.method : 'GET') || 'GET').toUpperCase();
        if (!unsafeMethods.has(method)) {
            return originalFetch(resource, init);
        }

        const urlText = resolveUrl(resource);
        let url;
        try {
            url = new URL(urlText, globalThis.location.href);
        } catch {
            return originalFetch(resource, init);
        }

        if (url.origin !== globalThis.location.origin) {
            return originalFetch(resource, init);
        }

        const csrfToken = readCookie('deezspotag.csrf.request');
        if (!csrfToken) {
            return originalFetch(resource, init);
        }

        const headers = new Headers(init?.headers || (resource instanceof Request ? resource.headers : undefined));
        if (!headers.has('X-CSRF-TOKEN')) {
            headers.set('X-CSRF-TOKEN', csrfToken);
        }

        return originalFetch(resource, {
            ...init,
            headers,
            credentials: init?.credentials ?? 'same-origin'
        });
    };
})();

// Global DeezSpoTag namespace
globalThis.DeezSpoTag = {
    // Notification management
    notifications: {
        active: [],
        baseTop: 20,
        spacing: 10
    },

    // Themed popup helpers
    ui: {
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

            if (!enabled) {
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

        showModal({ title, message, input, buttons, allowHtml, contentElement, dialogClass }) {
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
            messageEl.innerHTML = '';
            if (allowHtml) {
                messageEl.innerHTML = message || '';
            } else {
                messageEl.textContent = message || '';
            }
            // remove any prior injected content
            bodyEl.querySelectorAll('.app-modal-content').forEach(el => el.remove());
            footerEl.innerHTML = '';

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
                const cleanup = () => {
                    modal.classList.add('hidden');
                    delete modal.dataset.open;
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
                    action.addEventListener('click', () => {
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
                allowHtml: Boolean(options.allowHtml)
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

    // Initialize the application
    init() {
        this.bindGlobalEvents();
        this.initializeNavigation();
        this.initializeTabs();
        if (this.isLoginRoute()) {
            // Login route uses lightweight platform probes; run a one-shot refresh.
            globalThis.setTimeout(() => this.loadConnectedPlatforms(), 700);
        } else {
            this.loadConnectedPlatforms({ force: true });
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

    // Bootstrap tab fallback (ensures panes swap even if bootstrap JS misses it)
    initializeTabs() {
        const tabListSelector = '[role="tablist"], .nav-tabs, .nav-pills';
        const tabPreferenceEnabled = () => {
            const stored = localStorage.getItem('tabs-preference-enabled');
            if (stored === null || stored === '') {
                return true;
            }

            return stored === 'true';
        };
        const getTabList = (element) => {
            if (!element) {
                return null;
            }

            return element.closest(tabListSelector);
        };
        const ensureTabListIdentity = (tabList) => {
            if (!tabList) {
                return null;
            }

            if (tabList.dataset?.deezspotTablistKey) {
                return tabList.dataset.deezspotTablistKey;
            }

            const explicitId = tabList.id;
            if (explicitId) {
                tabList.dataset.deezspotTablistKey = explicitId;
                return explicitId;
            }

            const all = Array.from(document.querySelectorAll(tabListSelector));
            const index = all.indexOf(tabList);
            const ownerId = tabList.closest('[id]')?.id || 'page';
            const generated = `auto:${ownerId}:${Math.max(index, 0)}`;
            tabList.dataset.deezspotTablistKey = generated;
            return generated;
        };
        const getTabKey = (tabList) => {
            const listId = ensureTabListIdentity(tabList) || 'tabs';
            return `tabs:last:${globalThis.location.pathname}:${listId}`;
        };
        const persistTabPreference = (trigger, tabList) => {
            if (!trigger || !tabList || !tabPreferenceEnabled()) {
                return;
            }

            const targetSelector = trigger.dataset.bsTarget;
            if (!targetSelector) {
                return;
            }

            const storageKey = getTabKey(tabList);
            localStorage.setItem(storageKey, targetSelector);
            if (globalThis.UserPrefs?.setTabSelection) {
                globalThis.UserPrefs.setTabSelection(storageKey, targetSelector);
            }
        };
        const syncUrlTabParam = (trigger) => {
            if (!trigger) {
                return;
            }

            const targetSelector = trigger.dataset.bsTarget;
            if (!targetSelector || !targetSelector.startsWith('#')) {
                return;
            }

            const tabId = targetSelector.slice(1);
            if (!tabId) {
                return;
            }

            const url = new URL(globalThis.location.href);
            if (url.searchParams.get('tab') === tabId) {
                return;
            }

            url.searchParams.set('tab', tabId);
            const next = `${url.pathname}${url.search}${url.hash}`;
            globalThis.history.replaceState(globalThis.history.state, '', next);
        };
        const getTargetPane = (trigger) => {
            const targetSelector = trigger.dataset.bsTarget;
            if (!targetSelector) {
                return null;
            }
            return document.querySelector(targetSelector);
        };
        const activateTab = (trigger) => {
            const targetPane = getTargetPane(trigger);
            if (!targetPane) {
                return;
            }

            const tabList = trigger.closest('.nav');
            const previousTrigger = tabList?.querySelector('[data-bs-toggle="tab"].active') || null;
            const container = targetPane.closest('.tab-content');
            const previousPane = container?.querySelector('.tab-pane.show.active') || null;

            if (container) {
                container.querySelectorAll('.tab-pane').forEach((pane) => {
                    pane.classList.remove('show', 'active');
                });
                targetPane.classList.add('show', 'active');
            }

            if (tabList) {
                tabList.querySelectorAll('[data-bs-toggle="tab"]').forEach((tab) => {
                    tab.classList.remove('active');
                });
                trigger.classList.add('active');
            }

            if (previousTrigger && previousTrigger !== trigger) {
                const hideEvent = new Event('hide.bs.tab', { bubbles: true, cancelable: true });
                Object.defineProperty(hideEvent, 'relatedTarget', { value: trigger, enumerable: true });
                previousTrigger.dispatchEvent(hideEvent);
            }

            if (!previousTrigger || previousTrigger !== trigger) {
                const showEvent = new Event('show.bs.tab', { bubbles: true, cancelable: true });
                Object.defineProperty(showEvent, 'relatedTarget', { value: previousTrigger, enumerable: true });
                trigger.dispatchEvent(showEvent);

                if (previousTrigger) {
                    const hiddenEvent = new Event('hidden.bs.tab', { bubbles: true, cancelable: false });
                    Object.defineProperty(hiddenEvent, 'relatedTarget', { value: trigger, enumerable: true });
                    previousTrigger.dispatchEvent(hiddenEvent);
                }

                const shownEvent = new Event('shown.bs.tab', { bubbles: true, cancelable: false });
                Object.defineProperty(shownEvent, 'relatedTarget', { value: previousTrigger, enumerable: true });
                trigger.dispatchEvent(shownEvent);
            } else if (previousPane && previousPane !== targetPane) {
                // Ensure a no-op re-activation still notifies listeners if pane state was stale.
                const shownEvent = new Event('shown.bs.tab', { bubbles: true, cancelable: false });
                Object.defineProperty(shownEvent, 'relatedTarget', { value: previousTrigger, enumerable: true });
                trigger.dispatchEvent(shownEvent);
            }
        };

        document.addEventListener('click', (event) => {
            const trigger = event.target.closest('[data-bs-toggle="tab"]');
            if (!trigger) {
                return;
            }

            const tabList = getTabList(trigger);
            if (tabList?.dataset?.noGlobalTabFallback === 'true') {
                return;
            }

            event.preventDefault();
            activateTab(trigger);
        });

        document.addEventListener('shown.bs.tab', (event) => {
            const trigger = event.target?.closest?.('[data-bs-toggle="tab"]');
            if (!trigger) {
                return;
            }

            const tabList = getTabList(trigger);
            if (tabList?.dataset?.noGlobalTabFallback === 'true') {
                return;
            }

            persistTabPreference(trigger, tabList);
            syncUrlTabParam(trigger);
        });

        const urlTab = new URLSearchParams(globalThis.location.search).get('tab');

        const tabLists = Array.from(document.querySelectorAll(tabListSelector))
            .filter((tabList, index, all) => all.indexOf(tabList) === index);

        tabLists.forEach((tabList) => {
            if (tabList?.dataset?.noGlobalTabFallback === 'true') {
                return;
            }

            ensureTabListIdentity(tabList);

            const triggers = Array.from(tabList.querySelectorAll('[data-bs-toggle="tab"]'));
            if (!triggers.length) {
                return;
            }

            let preferredTrigger = null;
            if (urlTab) {
                const targetSelector = `#${urlTab}`;
                preferredTrigger = triggers.find((trigger) => trigger.dataset.bsTarget === targetSelector) || null;
                if (preferredTrigger) {
                    activateTab(preferredTrigger);
                    return;
                }
            }
            if (tabPreferenceEnabled()) {
                const storedTarget = localStorage.getItem(getTabKey(tabList));
                if (storedTarget) {
                    preferredTrigger = triggers.find((trigger) => trigger.dataset.bsTarget === storedTarget) || null;
                }
            }

            if (!preferredTrigger) {
                const activeByClass = triggers.find((trigger) => trigger.classList.contains('active'));
                if (activeByClass) {
                    preferredTrigger = activeByClass;
                } else {
                    const activeByPane = triggers.find((trigger) => {
                        const pane = getTargetPane(trigger);
                        return pane?.classList.contains('active');
                    });
                    preferredTrigger = activeByPane || triggers[0];
                }
            }

            activateTab(preferredTrigger);
        });
    },

    platformIconMap: {},

    authRequiredPlatforms: new Set(),

    platformDisplayOrder: [],

    connectedPlatformsRefreshIntervalMs: 15000,
    connectedPlatformsRefreshTimerId: null,
    connectedPlatformsRefreshInFlight: false,
    connectedPlatformsRefreshPending: false,
    connectedPlatformsFocusHandler: null,
    connectedPlatformsVisibilityHandler: null,
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
            states[id] = { active: false, reason: null };
        });

        return states;
    },

    setPlatformState(states, id, active, reason = null) {
        if (!states || !this.platformIconMap[id]) {
            return;
        }

        if (!states[id]) {
            states[id] = { active: false, reason: null };
        }

        states[id].active = Boolean(active);
        states[id].reason = reason;
    },

    normalizeConnectedPlatformStates(platformsOrStates) {
        const states = {};
        if (Array.isArray(platformsOrStates)) {
            const normalizedIds = platformsOrStates
                .map((id) => this.normalizePlatformId(id))
                .filter((id) => this.platformIconMap[id]);

            this.getPlatformDisplayOrder(normalizedIds).forEach((id) => {
                if (this.platformIconMap[id]) {
                    states[id] = { active: true, reason: null };
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
                        reason: null
                    };
                }
                if (value?.active === true) {
                    merged[id].active = true;
                }
                if (!merged[id].reason && value?.reason) {
                    merged[id].reason = value.reason;
                }
            });

            const ids = Object.keys(merged);
            this.getPlatformDisplayOrder(ids).forEach((id) => {
                const value = merged[id];
                states[id] = {
                    active: Boolean(value?.active),
                    reason: value?.reason || null
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
        });
        return baseline;
    },

    getConnectedPlatformsRenderSignature(entries) {
        return entries.map(([id, status]) => `${id}:${status?.active === true ? 1 : 0}`).join('|');
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
                    updatedAt: null
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
                updatedAt: parsed.updatedAt || null
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
                updatedAt: snapshot?.updatedAt || Date.now()
            };
            localStorage.setItem('connected-platforms-cache', JSON.stringify(payload));
        } catch {
            // Ignore storage errors (private mode, quota exceeded).
        }
    },

    isSpotifyStatusTransient(status) {
        const webError = String(status?.webPlayerError || '').toLowerCase();
        const librespotError = String(status?.librespotError || '').toLowerCase();
        const hardErrors = new Set([
            'missing_web_player_blob',
            'web_player_anonymous',
            'missing_librespot_blob',
            'credentials_not_found',
            'helper_not_found',
            'missing_blob'
        ]);

        if (hardErrors.has(webError) || hardErrors.has(librespotError)) {
            return false;
        }

        const merged = `${webError} ${librespotError}`;
        return merged.includes('token_failed')
            || merged.includes('all_retries_failed')
            || merged.includes('timeout')
            || merged.includes('timed out')
            || merged.includes('network')
            || merged.includes('429')
            || merged.includes('502')
            || merged.includes('503')
            || merged.includes('504');
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

    startConnectedPlatformsAutoRefresh() {
        if (this.connectedPlatformsRefreshTimerId !== null) {
            return;
        }

        this.connectedPlatformsRefreshTimerId = globalThis.setInterval(() => {
            this.loadConnectedPlatforms();
        }, this.connectedPlatformsRefreshIntervalMs);

        if (!this.connectedPlatformsFocusHandler) {
            this.connectedPlatformsFocusHandler = () => {
                this.loadConnectedPlatforms();
            };
            globalThis.addEventListener('focus', this.connectedPlatformsFocusHandler);
        }

        if (!this.connectedPlatformsVisibilityHandler) {
            this.connectedPlatformsVisibilityHandler = () => {
                if (document.visibilityState === 'visible') {
                    this.loadConnectedPlatforms();
                }
            };
            document.addEventListener('visibilitychange', this.connectedPlatformsVisibilityHandler);
        }
    },

    async loadConnectedPlatforms() {
        const container = document.getElementById('connectedPlatformsList');
        if (!container) {
            return;
        }

        await this.ensurePlatformRegistryLoaded();

        const selected = this.getAutoTagSelectedPlatforms();
        const initialStates = this.buildInitialPlatformStates(selected);
        const cached = this.getCachedConnectedPlatformsSnapshot();
        this.renderConnectedPlatformsFromSnapshot(cached, selected, initialStates);

        if (this.connectedPlatformsRefreshInFlight) {
            this.connectedPlatformsRefreshPending = true;
            return;
        }

        this.connectedPlatformsRefreshInFlight = true;

        const connected = new Set();
        const platformStates = this.buildInitialPlatformStates(selected);
        const cachedHadSpotify = cached?.statuses?.spotify?.active === true
            || (Array.isArray(cached?.platforms) && cached.platforms.includes('spotify'));

        this.seedSelectedConnectedPlatforms(selected, connected, platformStates);

        const fetchOptions = {
            cache: 'no-store',
            credentials: 'same-origin',
            headers: { Accept: 'application/json' }
        };
        const skipExpensiveChecks = this.isLoginRoute();

        try {
            const settledResponses = await this.fetchConnectedPlatformResponses(fetchOptions, skipExpensiveChecks);
            const authData = await this.applyAuthStatus(settledResponses.authResponse, settledResponses.authOk, connected, platformStates);
            const wrapperReady = await this.resolveWrapperReadiness(
                settledResponses.appleWrapperResponse,
                settledResponses.appleWrapperOk,
                authData);
            this.applyWrapperPlatformState(wrapperReady, connected, platformStates);
            await this.applySpotifyStatus(
                settledResponses.spotifyResponse,
                settledResponses.spotifyOk,
                skipExpensiveChecks,
                cachedHadSpotify,
                connected,
                platformStates);
            await this.applyDeezerStatus(settledResponses.deezerResponse, settledResponses.deezerOk, connected, platformStates);
            const resolved = Array.from(connected);
            const preserveIfEmpty = !(
                settledResponses.authCompleted
                && settledResponses.deezerCompleted
                && settledResponses.spotifyCompleted
                && settledResponses.appleWrapperCompleted);
            this.setCachedConnectedPlatforms({
                platforms: resolved,
                statuses: platformStates,
                updatedAt: Date.now()
            });
            this.renderConnectedPlatforms(platformStates, { preserveIfEmpty });
        } catch (error) {
            console.warn('Failed to refresh connected platform status', error);
            this.renderConnectedPlatforms(platformStates);
        } finally {
            this.connectedPlatformsRefreshInFlight = false;
            if (this.connectedPlatformsRefreshPending) {
                this.connectedPlatformsRefreshPending = false;
                globalThis.setTimeout(() => this.loadConnectedPlatforms(), 150);
            }
        }
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

    async fetchConnectedPlatformResponses(fetchOptions, skipExpensiveChecks) {
        const deezerRequest = fetch('/api/login/status', fetchOptions);
        const spotifyStatusRequest = skipExpensiveChecks
            ? fetch('/api/spotify-credentials/accounts', fetchOptions)
            : fetch('/api/spotify-credentials/status', fetchOptions);
        const appleWrapperStatusRequest = skipExpensiveChecks
            ? Promise.resolve(null)
            : fetch('/api/apple-music/wrapper-ref/status', fetchOptions);
        const [authResult, deezerResult, spotifyStatusResult, appleWrapperStatusResult] = await Promise.allSettled([
            fetch('/api/platform-auth', fetchOptions),
            deezerRequest,
            spotifyStatusRequest,
            appleWrapperStatusRequest
        ]);

        const authResponse = authResult.status === 'fulfilled' ? authResult.value : null;
        const deezerResponse = deezerResult.status === 'fulfilled' ? deezerResult.value : null;
        const spotifyResponse = spotifyStatusResult.status === 'fulfilled' ? spotifyStatusResult.value : null;
        const appleWrapperResponse = appleWrapperStatusResult.status === 'fulfilled' ? appleWrapperStatusResult.value : null;
        return {
            authResponse,
            deezerResponse,
            spotifyResponse,
            appleWrapperResponse,
            authCompleted: authResult.status === 'fulfilled',
            deezerCompleted: deezerResult.status === 'fulfilled',
            spotifyCompleted: spotifyStatusResult.status === 'fulfilled',
            appleWrapperCompleted: appleWrapperStatusResult.status === 'fulfilled',
            authOk: Boolean(authResponse?.ok),
            deezerOk: Boolean(deezerResponse?.ok),
            spotifyOk: Boolean(spotifyResponse?.ok),
            appleWrapperOk: Boolean(appleWrapperResponse?.ok)
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

        if (authData.discogs?.token) {
            connected.add('discogs');
            this.setPlatformState(platformStates, 'discogs', true, 'token');
        }

        const lastFmApiKey = typeof authData.lastFm?.apiKey === 'string'
            ? authData.lastFm.apiKey.trim()
            : '';
        if (authData.lastFm?.hasApiKey === true || lastFmApiKey.length > 0) {
            connected.add('lastfm');
            this.setPlatformState(platformStates, 'lastfm', true, 'api-key');
        }
        if (authData.bpmSupreme?.email && authData.bpmSupreme?.password) {
            connected.add('bpmsupreme');
            this.setPlatformState(platformStates, 'bpmsupreme', true, 'credentials');
        }
        if (authData.plex?.url && authData.plex?.token) {
            connected.add('plex');
            this.setPlatformState(platformStates, 'plex', true, 'credentials');
        }
        if (authData.jellyfin?.url && (authData.jellyfin?.apiKey || authData.jellyfin?.username)) {
            connected.add('jellyfin');
            this.setPlatformState(platformStates, 'jellyfin', true, 'credentials');
        }

        return authData;
    },

    async resolveWrapperReadiness(appleWrapperResponse, appleWrapperOk, authData) {
        if (appleWrapperOk) {
            const wrapperData = await this.parseJsonSafely(appleWrapperResponse, '/api/apple-music/wrapper-ref/status');
            if (wrapperData) {
                return wrapperData.wrapperReady === true;
            }
        }

        if (authData) {
            return authData.appleMusic?.wrapperReady === true;
        }

        return null;
    },

    applyWrapperPlatformState(wrapperReady, connected, platformStates) {
        if (wrapperReady === true) {
            connected.add('applemusic');
            this.setPlatformState(platformStates, 'applemusic', true, 'wrapper');
        } else if (wrapperReady === false) {
            this.setPlatformState(platformStates, 'applemusic', false, 'wrapper');
        }
    },

    async applySpotifyStatus(spotifyResponse, spotifyOk, skipExpensiveChecks, cachedHadSpotify, connected, platformStates) {
        if (!spotifyResponse) {
            return;
        }

        if (!spotifyOk) {
            this.setPlatformState(platformStates, 'spotify', false, 'status-request-failed');
            return;
        }

        if (skipExpensiveChecks) {
            const accountsData = await this.parseJsonSafely(spotifyResponse, '/api/spotify-credentials/accounts');
            this.applySpotifyStatusFromAccounts(accountsData, connected, platformStates);
            return;
        }

        const status = await this.parseJsonSafely(spotifyResponse, '/api/spotify-credentials/status');
        this.applySpotifyStatusFromCredentialState(status, cachedHadSpotify, connected, platformStates);
    },

    applySpotifyStatusFromAccounts(accountsData, connected, platformStates) {
        if (!accountsData) {
            this.setPlatformState(platformStates, 'spotify', false, 'status-unavailable');
            return;
        }

        const accounts = Array.isArray(accountsData.accounts) ? accountsData.accounts : [];
        const activeAccountName = typeof accountsData.activeAccount === 'string' ? accountsData.activeAccount : '';
        const activeAccount = accounts.find((account) =>
            typeof account?.name === 'string'
            && account.name.toLowerCase() === activeAccountName.toLowerCase());
        const hasBlob = Boolean(
            activeAccount?.blobPath
            || activeAccount?.librespotBlobPath
            || activeAccount?.webPlayerBlobPath);
        if (activeAccount && hasBlob) {
            connected.add('spotify');
            this.setPinnedMessage('spotify-auth', null);
            this.setPlatformState(platformStates, 'spotify', true, 'provisional');
            return;
        }

        this.setPlatformState(platformStates, 'spotify', false, 'no-active-account');
    },

    applySpotifyStatusFromCredentialState(status, cachedHadSpotify, connected, platformStates) {
        if (!status) {
            this.setPlatformState(platformStates, 'spotify', false, 'status-unavailable');
            return;
        }

        const webPlayerOk = status.webPlayerOk === true;
        const librespotOk = status.librespotOk === true;
        if (webPlayerOk || librespotOk) {
            connected.add('spotify');
            this.setPinnedMessage('spotify-auth', null);
            let reason = 'librespot';
            if (webPlayerOk && librespotOk) {
                reason = 'ok';
            } else if (webPlayerOk) {
                reason = 'web-player';
            }
            this.setPlatformState(platformStates, 'spotify', true, reason);
            return;
        }

        const transientFailure = this.isSpotifyStatusTransient(status);
        this.setPinnedMessage('spotify-auth', null);
        if (transientFailure && cachedHadSpotify) {
            connected.add('spotify');
            this.setPlatformState(platformStates, 'spotify', true, 'cached-transient');
            return;
        }

        this.setPlatformState(platformStates, 'spotify', false, 'missing');
    },

    async applyDeezerStatus(deezerResponse, deezerOk, connected, platformStates) {
        if (!deezerOk) {
            return;
        }

        const data = await this.parseJsonSafely(deezerResponse, '/api/login/status');
        const deezerConnected = Boolean(data && Number(data.status) > 0 && data.user);
        if (deezerConnected) {
            connected.add('deezer');
            this.setPlatformState(platformStates, 'deezer', true, 'auth');
        }
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
            const stateLabel = isActive ? 'Connected' : 'Not connected';
            const target = this.getPlatformNavigationTarget(id);
            const wrapper = document.createElement('a');
            wrapper.className = `connected-platform-icon ${isActive ? 'connected-platform-icon--active' : 'connected-platform-icon--inactive'}`;
            wrapper.classList.add(`connected-platform-icon--platform-${id}`);
            wrapper.href = target.href;
            wrapper.title = `${this.getPlatformDisplayName(id)} (${stateLabel})`;
            wrapper.setAttribute('aria-label', `${this.getPlatformDisplayName(id)} (${stateLabel})`);
            wrapper.addEventListener('click', () => {
                if (target.loginTabId) {
                    this.setLoginTabPreference(target.loginTabId);
                }
            });
            const img = document.createElement('img');
            img.src = icon;
            img.alt = this.getPlatformDisplayName(id);
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
        const actionHref = this.sanitizeActionHref(action?.href);
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
        DeezSpoTag.loadConnectedPlatforms();
    });
    globalThis.addEventListener('storage', (event) => {
        if (event.key === 'autotag-selected-platforms') {
            DeezSpoTag.loadConnectedPlatforms();
        }
    });
});
