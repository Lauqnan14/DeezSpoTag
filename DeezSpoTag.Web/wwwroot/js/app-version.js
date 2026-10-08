/**
 * Renders the running build version and the newest release on the configured branch.
 *
 * The version and the branch link are already server-rendered, so this script only adds the
 * "latest release" line and the update indicator, and keeps them fresh. Every fetch goes through
 * the shared server-side cache, so re-running this never turns into repeated GitHub traffic.
 */
(function () {
    'use strict';

    const ENDPOINT = '/api/app-version';

    function get(id) {
        return document.getElementById(id);
    }

    function safeHref(href) {
        // Reuse the shared sanitizer so an untrusted href from the API can never become a
        // javascript: or data: URL.
        if (globalThis.DeezSpoTag && typeof globalThis.DeezSpoTag.sanitizeActionHref === 'function') {
            return globalThis.DeezSpoTag.sanitizeActionHref(href);
        }

        try {
            const url = new URL(href, globalThis.location.href);
            if (url.protocol !== 'http:' && url.protocol !== 'https:') {
                return '';
            }
            return url.toString();
        } catch {
            return '';
        }
    }

    function formatCheckedAt(value) {
        if (!value) {
            return '';
        }

        const date = new Date(value);
        if (Number.isNaN(date.getTime())) {
            return '';
        }

        return date.toLocaleString();
    }

    function render(payload) {
        const current = get('appVersionCurrent');
        const latest = get('appVersionLatest');
        const dot = get('appVersionDot');
        if (!current || !latest || !dot) {
            return;
        }

        const branch = typeof payload?.branch === 'string' ? payload.branch : '';
        const branchUrl = safeHref(payload?.branchUrl);
        if (branchUrl) {
            current.href = branchUrl;
            current.title = `View the ${branch || 'source'} branch on GitHub`;
        }

        if (typeof payload?.currentVersion === 'string' && payload.currentVersion) {
            current.textContent = payload.currentVersion;
        }

        const latestVersion = typeof payload?.latestVersion === 'string' ? payload.latestVersion : '';
        const updateAvailable = payload?.updateAvailable === true;

        latest.replaceChildren();
        if (!latestVersion) {
            latest.hidden = true;
        } else {
            latest.hidden = false;
            const label = updateAvailable
                ? `${latestVersion} available`
                : `Up to date (${latestVersion})`;
            const href = updateAvailable ? safeHref(payload?.latestUrl) : '';

            if (href) {
                const link = document.createElement('a');
                link.href = href;
                link.target = '_blank';
                link.rel = 'noopener noreferrer';
                link.className = 'app-version-latest-link';
                link.textContent = label;
                latest.appendChild(link);
            } else {
                latest.textContent = label;
            }
        }

        dot.hidden = !updateAvailable;
        get('appVersionBlock')?.classList.toggle('has-update', updateAvailable);
    }

    function renderStatus(text) {
        const status = get('appVersionStatus');
        if (!status) {
            return;
        }

        status.textContent = text || '';
        status.hidden = !text;
    }

    async function load() {
        try {
            const response = await fetch(ENDPOINT, { headers: { Accept: 'application/json' } });
            if (!response.ok) {
                return;
            }

            const payload = await response.json();
            render(payload);

            const checked = formatCheckedAt(payload?.checkedUtc);
            renderStatus(checked ? `Checked ${checked}` : '');
        } catch (error) {
            console.warn('Failed to load the app version', error);
        }
    }

    function init() {
        const checkButton = get('appVersionCheck');
        if (!checkButton) {
            return;
        }

        checkButton.addEventListener('click', async () => {
            checkButton.disabled = true;
            renderStatus('Checking...');
            try {
                await load();
            } finally {
                checkButton.disabled = false;
            }
        });

        void load();

        globalThis.DeezSpoTagAppVersion = {
            refresh: load
        };
    }

    document.addEventListener('DOMContentLoaded', init);
})();
