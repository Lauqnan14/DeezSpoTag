(() => {
    async function loadRuntimeScript() {
        if (typeof globalThis.loadTrackAnalysisPage === 'function') {
            return;
        }
        await new Promise((resolve, reject) => {
            const script = document.createElement('script');
            script.src = '/js/library.js';
            script.async = true;
            script.onload = resolve;
            script.onerror = () => reject(new Error('Failed to load track analysis runtime.'));
            document.head.appendChild(script);
        });
    }

    async function initializeTrackAnalysisPage() {
        const page = document.querySelector('.library-track-analysis-page[data-track-id]');
        if (!page) {
            return;
        }

        try {
            await loadRuntimeScript();
            if (typeof globalThis.loadTrackAnalysisPage === 'function') {
                await globalThis.loadTrackAnalysisPage();
            }
            await initializePersonalGenreTrack(page);
        } catch (error) {
            if (globalThis.DeezSpoTag?.showNotification) {
                globalThis.DeezSpoTag.showNotification(error?.message || 'Failed to load track analysis page.', 'error');
            } else {
                console.error(error);
            }
        }
    }

    async function initializePersonalGenreTrack(page) {
        const trackId = String(page.dataset.trackId || '').trim();
        if (!trackId || !document.getElementById('personalGenreTrackCard')) {
            return;
        }

        const state = {
            trackId: trackId,
            taxonomy: [],
            locks: [],
            result: null
        };

        const status = document.getElementById('pgTrackStatus');
        const classifications = document.getElementById('pgTrackClassifications');
        const lockList = document.getElementById('pgTrackLockList');
        const lockSelect = document.getElementById('pgTrackLockTaxon');
        const lockAdd = document.getElementById('pgTrackLockAdd');
        const resolveButton = document.getElementById('pgTrackResolve');

        if (!status || !classifications || !lockList || !lockSelect || !lockAdd || !resolveButton) {
            return;
        }

        async function loadAll() {
            status.textContent = 'Loading Personal Genre…';
            const taxonomyPayload = await requestJson('/api/personal-genre/taxonomy');
            state.taxonomy = Array.isArray(taxonomyPayload?.taxa) ? taxonomyPayload.taxa : [];
            state.locks = await requestJson('/api/personal-genre/tracks/' + encodeURIComponent(trackId) + '/locks');

            const existing = await requestJson(
                '/api/personal-genre/tracks/' + encodeURIComponent(trackId),
                {},
                true);
            if (existing === null) {
                state.result = await requestJson(
                    '/api/personal-genre/tracks/' + encodeURIComponent(trackId) + '/resolve',
                    { method: 'POST' },
                    true);
            } else {
                state.result = existing;
            }

            renderTaxonSelect();
            renderLocks();
            renderResult();
        }

        function renderTaxonSelect() {
            const previous = lockSelect.value;
            lockSelect.innerHTML = '';
            const lockedIds = new Set(
                (state.locks || [])
                    .filter(item => item.enabled !== false)
                    .map(item => String(item.taxonId || '').toLowerCase()));

            ['genre', 'style', 'substyle', 'context'].forEach(kind => {
                const items = state.taxonomy
                    .filter(item => String(item.kind || '').toLowerCase() === kind)
                    .filter(item => !lockedIds.has(String(item.id || '').toLowerCase()))
                    .sort((left, right) => String(left.name || '').localeCompare(String(right.name || '')));
                if (!items.length) {
                    return;
                }

                const group = document.createElement('optgroup');
                group.label = capitalize(kind);
                items.forEach(item => {
                    const option = document.createElement('option');
                    option.value = item.id;
                    option.textContent = item.name + ' · ' + item.id;
                    group.appendChild(option);
                });
                lockSelect.appendChild(group);
            });

            if (previous && Array.from(lockSelect.options).some(option => option.value === previous)) {
                lockSelect.value = previous;
            }
            lockAdd.disabled = lockSelect.options.length === 0;
        }

        function renderLocks() {
            lockList.innerHTML = '';
            const active = (state.locks || []).filter(item => item.enabled !== false);
            if (!active.length) {
                const empty = document.createElement('span');
                empty.className = 'pg-muted';
                empty.textContent = 'No user locks.';
                lockList.appendChild(empty);
                return;
            }

            active.forEach(item => {
                const taxon = state.taxonomy.find(entry =>
                    String(entry.id || '').toLowerCase() === String(item.taxonId || '').toLowerCase());
                const pill = document.createElement('span');
                pill.className = 'pg-track-lock-pill';

                const label = document.createElement('span');
                label.textContent = taxon?.name || item.taxonId;
                pill.appendChild(label);

                const remove = document.createElement('button');
                remove.type = 'button';
                remove.setAttribute('aria-label', 'Remove ' + (taxon?.name || item.taxonId) + ' lock');
                remove.textContent = '×';
                remove.addEventListener('click', async () => {
                    try {
                        state.result = await requestJson(
                            '/api/personal-genre/tracks/' + encodeURIComponent(trackId) +
                            '/locks/' + encodeURIComponent(item.taxonId),
                            { method: 'DELETE' });
                        state.locks = await requestJson(
                            '/api/personal-genre/tracks/' + encodeURIComponent(trackId) + '/locks');
                        renderLocks();
                        renderTaxonSelect();
                        renderResult();
                    } catch (error) {
                        status.textContent = error.message || 'Failed to remove Personal Genre lock.';
                    }
                });
                pill.appendChild(remove);
                lockList.appendChild(pill);
            });
        }

        function renderResult() {
            classifications.innerHTML = '';
            const result = state.result;
            const resolution = result?.resolution;
            const items = Array.isArray(resolution?.classifications) ? resolution.classifications : [];

            if (!result || !resolution) {
                status.textContent = 'No Personal Genre result is available for this track yet.';
                return;
            }

            const primary = resolution.primaryGenre || 'No final Genre';
            status.textContent = primary + ' · resolver ' + (resolution.resolverVersion || 'unknown');

            if (!items.length) {
                const empty = document.createElement('div');
                empty.className = 'pg-muted';
                empty.textContent = 'No canonical classifications were produced from the available evidence.';
                classifications.appendChild(empty);
                return;
            }

            items.forEach(item => {
                const row = document.createElement('div');
                row.className = 'pg-track-classification' + (item.userLocked ? ' is-locked' : '');

                const name = document.createElement('div');
                name.className = 'pg-track-classification__name';
                name.textContent = item.name || item.taxonId || 'Unknown';

                const kind = document.createElement('div');
                kind.textContent = formatKind(item.kind);

                const confidence = document.createElement('div');
                confidence.className = 'pg-track-classification__confidence';
                const confidenceValue = Number(item.confidence);
                confidence.textContent = Number.isFinite(confidenceValue)
                    ? Math.round(confidenceValue * 100) + '%'
                    : '—';

                const sources = document.createElement('div');
                sources.className = 'pg-track-classification__sources';
                sources.textContent = (item.sources || []).join(', ') || 'No source provenance';

                row.appendChild(name);
                row.appendChild(kind);
                row.appendChild(confidence);
                row.appendChild(sources);
                classifications.appendChild(row);
            });
        }

        lockAdd.addEventListener('click', async () => {
            const taxonId = lockSelect.value;
            if (!taxonId) {
                return;
            }

            try {
                state.result = await requestJson(
                    '/api/personal-genre/tracks/' + encodeURIComponent(trackId) + '/locks',
                    {
                        method: 'POST',
                        body: JSON.stringify({ taxonId: taxonId, enabled: true })
                    });
                state.locks = await requestJson(
                    '/api/personal-genre/tracks/' + encodeURIComponent(trackId) + '/locks');
                renderLocks();
                renderTaxonSelect();
                renderResult();
            } catch (error) {
                status.textContent = error.message || 'Failed to save Personal Genre lock.';
            }
        });

        resolveButton.addEventListener('click', async () => {
            resolveButton.disabled = true;
            status.textContent = 'Re-resolving Personal Genre…';
            try {
                state.result = await requestJson(
                    '/api/personal-genre/tracks/' + encodeURIComponent(trackId) + '/resolve',
                    { method: 'POST' },
                    true);
                renderResult();
            } catch (error) {
                status.textContent = error.message || 'Personal Genre resolution failed.';
            } finally {
                resolveButton.disabled = false;
            }
        });

        try {
            await loadAll();
        } catch (error) {
            console.error('Personal Genre track controls failed.', error);
            status.textContent = error.message || 'Failed to load Personal Genre for this track.';
        }
    }

    async function requestJson(url, options = {}, allowNotFound = false) {
        const headers = new Headers(options.headers || {});
        if (options.body != null && !headers.has('Content-Type')) {
            headers.set('Content-Type', 'application/json');
        }

        const response = await fetch(url, {
            ...options,
            headers: headers,
            credentials: 'same-origin',
            cache: 'no-store'
        });

        if (allowNotFound && response.status === 404) {
            return null;
        }

        if (response.status === 204) {
            return null;
        }

        const text = await response.text();
        let payload = null;
        if (text) {
            try {
                payload = JSON.parse(text);
            } catch {
                payload = text;
            }
        }

        if (!response.ok) {
            const message = payload && typeof payload === 'object'
                ? payload.error || payload.message
                : null;
            throw new Error(message || 'Request failed (' + response.status + ').');
        }

        return payload;
    }

    function formatKind(value) {
        if (typeof value === 'string') {
            return capitalize(value);
        }

        switch (Number(value)) {
            case 0: return 'Genre';
            case 1: return 'Style';
            case 2: return 'Substyle';
            case 3: return 'Context';
            default: return 'Unknown';
        }
    }

    function capitalize(value) {
        const text = String(value || '');
        return text ? text[0].toUpperCase() + text.slice(1) : text;
    }

    document.addEventListener('DOMContentLoaded', () => {
        void initializeTrackAnalysisPage();
    });
})();
