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
        const observed = document.getElementById('pgTrackObserved');
        const preserved = document.getElementById('pgTrackPreserved');

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
            await loadCleanupPreview();
            await loadConstructionPreview();
        }

        async function loadConstructionPreview() {
            const container = document.getElementById('pgTrackConstruction');
            if (!container) return;
            container.innerHTML = '';
            const appendText = function (parent, text, tag = 'div') {
                const element = document.createElement(tag);
                element.textContent = text;
                parent.appendChild(element);
                return element;
            };
            appendText(container, 'Checking Style Construction evidence…');
            try {
                const preview = await requestJson(
                    '/api/personal-genre/tracks/' + encodeURIComponent(trackId) + '/construction-preview', {}, true);
                container.innerHTML = '';
                if (!preview) {
                    appendText(container, 'No Style Construction preview is available for this file.');
                    return;
                }
                const labels = {
                    Qualified: 'Qualified — preview only',
                    MissingEvidence: 'Not qualified — required evidence is unavailable',
                    Conflicted: 'Not qualified — conflicting evidence',
                    SuppressedByUserAuthority: 'Suppressed by your Style lock',
                    RecognitionOnly: 'Recognition only — no sufficient construction rule'
                };
                (preview.decisions || []).forEach(function (decision) {
                    const row = document.createElement('div');
                    row.className = 'pg-track-history-item';
                    appendText(row, decision.targetStyleName || decision.targetStyleId, 'strong');
                    appendText(row, 'Rule: ' + decision.ruleId + ' v' + decision.ruleVersion);
                    appendText(row, labels[decision.outcome] || decision.outcome);
                    appendText(row, decision.explanation || '');
                    (decision.satisfiedPredicates || []).forEach(id => appendText(row, '✓ ' + id));
                    (decision.missingPredicates || []).forEach(id => appendText(row, '✗ Required: ' + id));
                    (decision.evidenceUsed || []).forEach(fact => appendText(row,
                        'Evidence: ' + fact.canonicalValue + ' · ' + fact.source + ' · ' + fact.provenanceReference));
                    (decision.conflictingFacts || []).forEach(fact => appendText(row,
                        'Conflict: ' + fact.canonicalValue + ' · ' + fact.provenanceReference));
                    (decision.rejectedAlternatives || []).forEach(item => appendText(row,
                        'Rejected: ' + item.predicateId + ' / ' + item.canonicalId + ' · ' + item.reason));
                    (decision.research?.researchGaps || []).forEach(gap => appendText(row, 'Research limit: ' + gap));
                    container.appendChild(row);
                });
            } catch (error) {
                container.innerHTML = '';
                appendText(container, error.message || 'Failed to load Style Construction preview.');
            }
        }

        /// Shows what cleanup would change, without changing anything.
        ///
        /// The endpoint only reads the file, so this can be shown before a write
        /// rather than described after one. It is deliberately a summary rather than
        /// the whole vocabulary: the point is to make the change inspectable, not to
        /// turn this page into a taxonomy browser.
        async function loadCleanupPreview() {
            const container = document.getElementById('pgTrackCleanup');
            if (!container) {
                return;
            }

            container.innerHTML = '';
            const loading = document.createElement('p');
            loading.className = 'pg-muted';
            loading.textContent = 'Checking what cleanup would change…';
            container.appendChild(loading);

            const preview = await requestJson(
                '/api/personal-genre/tracks/' + encodeURIComponent(trackId) + '/cleanup-preview',
                {},
                true);
            container.innerHTML = '';

            if (preview === null) {
                const empty = document.createElement('p');
                empty.className = 'pg-muted';
                empty.textContent = 'No cleanup preview is available for this file.';
                container.appendChild(empty);
                return;
            }

            const sections = [
                ['Would move to another tag', preview.moved],
                ['Would be normalized', preview.canonicalized],
                ['Would be removed', preview.removed],
                ['Kept because the vocabulary does not know them', preview.preservedUnknown]
            ];

            let listed = 0;
            sections.forEach(function (section) {
                const title = section[0];
                const items = section[1];
                if (!Array.isArray(items) || items.length === 0) {
                    return;
                }

                listed += items.length;
                const heading = document.createElement('p');
                heading.className = 'pg-track-history-item__decision';
                heading.textContent = title;
                container.appendChild(heading);

                items.forEach(function (item) {
                    const row = document.createElement('div');
                    row.className = 'pg-track-history-item';
                    row.textContent = String(item);
                    container.appendChild(row);
                });
            });

            if (listed === 0) {
                const none = document.createElement('p');
                none.className = 'pg-muted';
                none.textContent = 'Nothing to change. Genre and Style tags already match the vocabulary.';
                container.appendChild(none);
                return;
            }

            const suffix = preview.hasChanges
                ? ' An enabled AutoTag run writes these changes.'
                : '';
            const summary = document.createElement('p');
            summary.className = 'pg-muted';
            summary.textContent = 'Vocabulary ' + String(preview.catalogVersion || '') + '.' + suffix;
            container.insertBefore(summary, container.firstChild);
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

            ['genre', 'style', 'substyle', 'context', 'scene', 'language'].forEach(kind => {
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
            renderObserved(result);

            if (!items.length) {
                const empty = document.createElement('div');
                empty.className = 'pg-muted';
                empty.textContent = 'No classifications were produced from the tags in this file.';
                classifications.appendChild(empty);
            } else {
                items.forEach(item => {
                    const row = document.createElement('div');
                    row.className = 'pg-track-classification' + (item.userLocked ? ' is-locked' : '');

                    const name = document.createElement('div');
                    name.className = 'pg-track-classification__name';
                    name.textContent = item.name || item.taxonId || 'Unknown';

                    const kind = document.createElement('div');
                    kind.textContent = formatKind(item.kind);

                    // Which file field(s) this term was read from. A term that was
                    // found in GENRE and classified as a Style is a correction, and
                    // this is what makes that visible.
                    const origin = document.createElement('div');
                    origin.className = 'pg-track-classification__sources';
                    const fields = Array.isArray(item.originFields) ? item.originFields : [];
                    origin.textContent = fields.length
                        ? 'found in ' + fields.map(formatKind).join(', ')
                        : 'no file field recorded';

                    row.appendChild(name);
                    row.appendChild(kind);
                    row.appendChild(origin);
                    classifications.appendChild(row);
                });
            }

            renderPreserved(resolution);
        }

        /// Shows what the file actually contained before anything was rewritten.
        function renderObserved(result) {
            if (!observed) return;
            observed.innerHTML = '';

            const pre = result?.preAutoTagSnapshot?.observations;
            const post = result?.postAutoTagSnapshot?.observations;
            renderStage('Before AutoTag', pre);
            if (post) {
                renderStage('After AutoTag, before classification', post);
            }
        }

        function renderStage(label, values) {
            if (!Array.isArray(values) || !values.length) {
                const empty = document.createElement('div');
                empty.className = 'pg-muted';
                empty.textContent = label + ': no semantic tags were present.';
                observed.appendChild(empty);
                return;
            }

            const title = document.createElement('strong');
            title.textContent = label;
            observed.appendChild(title);

            values.forEach(item => {
                const row = document.createElement('div');
                row.className = 'pg-track-history-item';
                const heading = document.createElement('strong');
                heading.textContent = formatKind(item.inputField) + ': ' + (item.rawValue || '');
                row.appendChild(heading);
                if (item.origin === 'PreservedOriginal') {
                    const note = document.createElement('div');
                    note.className = 'pg-muted';
                    note.textContent = 'Present before AutoTag ran and no platform replaced it, so it was kept.';
                    row.appendChild(note);
                }
                observed.appendChild(row);
            });
        }

        /// Shows values the taxonomy does not know, so the user can teach it.
        function renderPreserved(resolution) {
            if (!preserved) return;
            preserved.innerHTML = '';
            const items = Array.isArray(resolution?.preserved) ? resolution.preserved : [];
            if (!items.length) {
                const empty = document.createElement('div');
                empty.className = 'pg-muted';
                empty.textContent = 'Every tag in this file was recognised.';
                preserved.appendChild(empty);
                return;
            }

            const lead = document.createElement('div');
            lead.className = 'pg-muted';
            lead.textContent = 'These values were left exactly as they were. Add a term, alias, mapping or rule to classify them.';
            preserved.appendChild(lead);

            items.forEach(item => {
                const row = document.createElement('div');
                row.className = 'pg-track-history-item';
                const heading = document.createElement('strong');
                heading.textContent = formatKind(item.inputField) + ': ' + (item.value || '');
                row.appendChild(heading);
                if (item.origin === 'PreservedOriginal') {
                    const note = document.createElement('div');
                    note.className = 'pg-muted';
                    note.textContent = 'Restored: AutoTag overwrote the field and would otherwise have lost it.';
                    row.appendChild(note);
                }
                preserved.appendChild(row);
            });
        }

        /// Human wording for each decision outcome. The vocabulary describes what
        /// Genre Intelligence decided about a value it read from the file, not
        /// where the value came from.
        const outcomeLabels = {
            canonical_match: 'canonical term',
            personal_taxonomy_match: 'your term',
            alias_match: 'alias',
            rule_applied: 'your rule',
            mapping_applied: 'your mapping',
            context_only: 'non-genre dimension',
            preserved_unmapped: 'kept unchanged',
            ignored: 'ignored by your rule',
            ambiguous: 'ambiguous',
            unmapped: 'not recognised'
        };

        function renderDecisions() {
            decisions.innerHTML = '';
            const items = Array.isArray(state.result?.resolution?.decisions)
                ? state.result.resolution.decisions
                : [];
            if (!items.length) {
                const empty = document.createElement('div');
                empty.className = 'pg-muted';
                empty.textContent = 'No file tags were read, so nothing was classified.';
                decisions.appendChild(empty);
                return;
            }

            items.forEach(item => {
                const row = document.createElement('div');
                row.className = 'pg-track-history-item';

                const outcome = String(item.outcome || 'unmapped');
                const heading = document.createElement('strong');
                heading.textContent = (item.rawValue || 'Value') + ' · ' + (outcomeLabels[outcome] || outcome.replaceAll('_', ' '));
                row.appendChild(heading);

                const meta = document.createElement('div');
                meta.className = 'pg-muted';
                const from = item.inputField ? 'read from ' + formatKind(item.inputField) : '';
                const target = item.taxonId ? ' → ' + item.taxonId : '';
                meta.textContent = (from + target).trim();
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
                    Number(item.observationCount || 0) + ' file tag values';
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
                        .map(([key, count]) => (outcomeLabels[key] || key.replaceAll('_', ' ')) + ': ' + count)
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
            case 4: return 'Scene';
            case 5: return 'Language';
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
