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
            trackScope: null,
            locks: [],
            result: null,
            history: []
        };

        const status = document.getElementById('pgTrackStatus');
        const classifications = document.getElementById('pgTrackClassifications');
        const lockList = document.getElementById('pgTrackLockList');
        const lockSelect = document.getElementById('pgTrackLockTaxon');
        const lockAdd = document.getElementById('pgTrackLockAdd');
        const resolveButton = document.getElementById('pgTrackResolve');
        const decisions = document.getElementById('pgTrackDecisions');
        const history = document.getElementById('pgTrackHistory');

        if (!status || !classifications || !lockList || !lockSelect || !lockAdd || !resolveButton || !decisions || !history) {
            return;
        }

        async function loadAll() {
            status.textContent = 'Loading Personal Genre…';
            const taxonomyPayload = await requestJson('/api/personal-genre/taxonomy');
            state.taxonomy = Array.isArray(taxonomyPayload?.taxa) ? taxonomyPayload.taxa : [];
            state.trackScope = await requestJson(
                '/api/personal-genre/tracks/' + encodeURIComponent(trackId) + '/scope',
                {},
                true);
            await loadLocks();

            const existing = await requestJson(
                '/api/personal-genre/tracks/' + encodeURIComponent(trackId),
                {},
                true);
            if (existing === null) {
                await resolveCurrentTrack();
            } else {
                state.result = existing;
            }
            await loadHistory();

            renderTaxonSelect();
            renderLocks();
            renderResult();
            renderDecisions();
            renderHistory();
        }

        async function loadLocks() {
            const combined = [];
            const trackLocks = await requestJson(
                '/api/personal-genre/tracks/' + encodeURIComponent(trackId) + '/locks');
            (Array.isArray(trackLocks) ? trackLocks : []).forEach(item => {
                combined.push({ ...item, scopeType: 'track', scopeId: Number(trackId) });
            });

            const albumId = Number(state.trackScope?.albumId || 0);
            if (albumId > 0) {
                const albumLocks = await requestJson(
                    '/api/personal-genre/scopes/album/' + encodeURIComponent(albumId) + '/locks');
                (Array.isArray(albumLocks) ? albumLocks : []).forEach(item => {
                    combined.push({ ...item, scopeType: 'album', scopeId: albumId });
                });
            }

            const artistId = Number(state.trackScope?.artistId || 0);
            if (artistId > 0) {
                const artistLocks = await requestJson(
                    '/api/personal-genre/scopes/artist/' + encodeURIComponent(artistId) + '/locks');
                (Array.isArray(artistLocks) ? artistLocks : []).forEach(item => {
                    combined.push({ ...item, scopeType: 'artist', scopeId: artistId });
                });
            }

            state.locks = combined;
        }

        function selectedScopeTarget() {
            const scopeType = String(document.getElementById('pgTrackLockScope')?.value || 'track').toLowerCase();
            if (scopeType === 'album') {
                return { scopeType, scopeId: Number(state.trackScope?.albumId || 0) };
            }
            if (scopeType === 'artist') {
                return { scopeType, scopeId: Number(state.trackScope?.artistId || 0) };
            }
            return { scopeType: 'track', scopeId: Number(trackId) };
        }

        async function resolveCurrentTrack() {
            state.result = await requestJson(
                '/api/personal-genre/tracks/' + encodeURIComponent(trackId) + '/resolve',
                { method: 'POST' },
                true);
        }

        async function loadHistory() {
            const payload = await requestJson(
                '/api/personal-genre/tracks/' + encodeURIComponent(trackId) + '/history?limit=20');
            state.history = Array.isArray(payload) ? payload : [];
        }

        function renderTaxonSelect() {
            const previous = lockSelect.value;
            lockSelect.innerHTML = '';
            const selectedScope = selectedScopeTarget();
            const lockedIds = new Set(
                (state.locks || [])
                    .filter(item => item.enabled !== false)
                    .filter(item =>
                        String(item.scopeType || 'track').toLowerCase() === selectedScope.scopeType
                        && Number(item.scopeId || 0) === selectedScope.scopeId)
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
                const scopeLabel = capitalize(String(item.scopeType || 'track'));
                label.textContent = (taxon?.name || item.taxonId) + ' · ' + scopeLabel;
                pill.appendChild(label);

                const remove = document.createElement('button');
                remove.type = 'button';
                remove.setAttribute('aria-label', 'Remove ' + (taxon?.name || item.taxonId) + ' lock');
                remove.textContent = '×';
                remove.addEventListener('click', async () => {
                    try {
                        const scopeType = String(item.scopeType || 'track').toLowerCase();
                        if (scopeType === 'track') {
                            await requestJson(
                                '/api/personal-genre/tracks/' + encodeURIComponent(trackId) +
                                '/locks/' + encodeURIComponent(item.taxonId),
                                { method: 'DELETE' });
                        } else {
                            await requestJson(
                                '/api/personal-genre/scopes/' + encodeURIComponent(scopeType) + '/' +
                                encodeURIComponent(item.scopeId) + '/locks/' + encodeURIComponent(item.taxonId),
                                { method: 'DELETE' });
                        }
                        await resolveCurrentTrack();
                        await loadLocks();
                        await loadHistory();
                        renderLocks();
                        renderTaxonSelect();
                        renderResult();
                        renderDecisions();
                        renderHistory();
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

        function renderDecisions() {
            decisions.innerHTML = '';
            const items = Array.isArray(state.result?.resolution?.decisions)
                ? state.result.resolution.decisions
                : [];
            if (!items.length) {
                const empty = document.createElement('div');
                empty.className = 'pg-muted';
                empty.textContent = 'No evidence decisions recorded.';
                decisions.appendChild(empty);
                return;
            }

            items.forEach(item => {
                const row = document.createElement('div');
                row.className = 'pg-track-history-item';

                const heading = document.createElement('strong');
                heading.textContent =
                    (item.rawValue || item.canonicalValue || 'Evidence') +
                    ' · ' + String(item.outcome || 'unmapped').replaceAll('_', ' ');
                row.appendChild(heading);

                const meta = document.createElement('div');
                meta.className = 'pg-muted';
                const canonical = item.canonicalValue && item.canonicalValue !== item.rawValue
                    ? ' · canonical ' + item.canonicalValue
                    : '';
                const target = item.taxonId ? ' · taxon ' + item.taxonId : '';
                meta.textContent = (item.source || 'unknown') + canonical + target;
                row.appendChild(meta);

                if (item.reason) {
                    const reason = document.createElement('div');
                    reason.textContent = item.reason;
                    row.appendChild(reason);
                }

                decisions.appendChild(row);
            });
        }

        function renderHistory() {
            history.innerHTML = '';
            if (!state.history.length) {
                const empty = document.createElement('div');
                empty.className = 'pg-muted';
                empty.textContent = 'No previous Personal Genre resolutions.';
                history.appendChild(empty);
                return;
            }

            state.history.forEach(item => {
                const row = document.createElement('div');
                row.className = 'pg-track-history-item';

                const heading = document.createElement('strong');
                heading.textContent = item.primaryGenre || 'No final Genre';
                row.appendChild(heading);

                const meta = document.createElement('div');
                meta.className = 'pg-muted';
                const at = item.resolvedAtUtc ? new Date(item.resolvedAtUtc).toLocaleString() : 'Unknown time';
                meta.textContent =
                    at + ' · ' + Number(item.classificationCount || 0) + ' classifications · ' +
                    Number(item.evidenceCount || 0) + ' evidence items';
                row.appendChild(meta);

                const decisionItems = Array.isArray(item.decisions) ? item.decisions : [];
                if (decisionItems.length) {
                    const summary = document.createElement('div');
                    const counts = decisionItems.reduce((acc, decision) => {
                        const key = String(decision.outcome || 'unmapped');
                        acc[key] = (acc[key] || 0) + 1;
                        return acc;
                    }, {});
                    summary.textContent = Object.entries(counts)
                        .map(([key, count]) => key.replaceAll('_', ' ') + ': ' + count)
                        .join(' · ');
                    row.appendChild(summary);
                }

                history.appendChild(row);
            });
        }

        document.getElementById('pgTrackLockScope')?.addEventListener('change', () => {
            renderTaxonSelect();
        });

        lockAdd.addEventListener('click', async () => {
            const taxonId = lockSelect.value;
            if (!taxonId) {
                return;
            }

            const target = selectedScopeTarget();
            if (!target.scopeId) {
                status.textContent = 'The selected lock scope is not available for this track.';
                return;
            }

            try {
                if (target.scopeType === 'track') {
                    await requestJson(
                        '/api/personal-genre/tracks/' + encodeURIComponent(trackId) + '/locks',
                        {
                            method: 'POST',
                            body: JSON.stringify({ taxonId: taxonId, enabled: true })
                        });
                } else {
                    await requestJson(
                        '/api/personal-genre/scopes/' + encodeURIComponent(target.scopeType) + '/' +
                        encodeURIComponent(target.scopeId) + '/locks',
                        {
                            method: 'POST',
                            body: JSON.stringify({ taxonId: taxonId, enabled: true })
                        });
                }

                await resolveCurrentTrack();
                await loadLocks();
                await loadHistory();
                renderLocks();
                renderTaxonSelect();
                renderResult();
                renderDecisions();
                renderHistory();
            } catch (error) {
                status.textContent = error.message || 'Failed to save Personal Genre lock.';
            }
        });

        resolveButton.addEventListener('click', async () => {
            resolveButton.disabled = true;
            status.textContent = 'Re-resolving Personal Genre…';
            try {
                await resolveCurrentTrack();
                await loadHistory();
                renderResult();
                renderDecisions();
                renderHistory();
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
