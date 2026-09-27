(() => {
    'use strict';

    const apiBase = '/api/personal-genre';
    const state = { taxonomy: [], settings: null, mappings: [], rules: [] };
    const byId = id => document.getElementById(id);

    document.addEventListener('DOMContentLoaded', () => {
        bindTabs();
        bindSettings();
        bindTaxonomy();
        bindMappings();
        bindRules();
        bindPortability();
        void loadAll();
    });

    async function loadAll() {
        setStatus('Loading Personal Genre…');
        try {
            const results = await Promise.all([
                requestJson(apiBase + '/taxonomy'),
                requestJson(apiBase + '/settings'),
                requestJson(apiBase + '/mappings'),
                requestJson(apiBase + '/rules')
            ]);
            const taxonomy = results[0];
            state.taxonomy = Array.isArray(taxonomy && taxonomy.taxa) ? taxonomy.taxa : [];
            state.settings = results[1] || {};
            state.mappings = Array.isArray(results[2]) ? results[2] : [];
            state.rules = Array.isArray(results[3]) ? results[3] : [];
            renderSettings();
            renderTaxonomy();
            populateTaxonSelects();
            renderMappings();
            renderRules();
            setStatus(
                'Ready · ' + state.taxonomy.length + ' taxonomy terms · ' +
                state.mappings.length + ' mappings · ' + state.rules.length + ' rules',
                'success');
        } catch (error) {
            console.error('Personal Genre load failed.', error);
            setStatus(error.message || 'Failed to load Personal Genre.', 'error');
        }
    }

    function bindTabs() {
        document.querySelectorAll('[data-pg-tab]').forEach(button => {
            button.addEventListener('click', () => {
                const target = button.dataset.pgTab;
                document.querySelectorAll('[data-pg-tab]').forEach(item => {
                    item.classList.toggle('is-active', item === button);
                });
                document.querySelectorAll('[data-pg-panel]').forEach(panel => {
                    panel.classList.toggle('is-active', panel.dataset.pgPanel === target);
                });
            });
        });
    }

    function bindSettings() {
        const form = byId('pgSettingsForm');
        if (!form) return;
        form.addEventListener('submit', async event => {
            event.preventDefault();
            try {
                const payload = {
                    enabled: byId('pgEnabled').checked,
                    maxGenres: clampInt(byId('pgMaxGenres').value, 1, 10, 3),
                    preserveProviderFallback: byId('pgProviderFallback').checked,
                    includeParentGenres: byId('pgIncludeParents').checked
                };
                state.settings = await requestJson(apiBase + '/settings', {
                    method: 'POST',
                    body: JSON.stringify(payload)
                });
                renderSettings();
                setStatus('Personal Genre settings saved.', 'success');
            } catch (error) {
                setStatus(error.message, 'error');
            }
        });
    }

    function renderSettings() {
        const settings = state.settings || {};
        byId('pgEnabled').checked = settings.enabled !== false;
        byId('pgMaxGenres').value = clampInt(settings.maxGenres, 1, 10, 3);
        byId('pgProviderFallback').checked = settings.preserveProviderFallback !== false;
        byId('pgIncludeParents').checked = settings.includeParentGenres === true;
    }

    function bindTaxonomy() {
        byId('pgTaxonomySearch')?.addEventListener('input', renderTaxonomy);
        byId('pgTaxonomyKindFilter')?.addEventListener('change', renderTaxonomy);
        byId('pgTaxonReset')?.addEventListener('click', resetTaxonForm);
        byId('pgTaxonKind')?.addEventListener('change', event => {
            if (event.target.value === 'context') {
                byId('pgTaxonContextOnly').checked = true;
            }
        });

        byId('pgTaxonForm')?.addEventListener('submit', async event => {
            event.preventDefault();
            const id = byId('pgTaxonId').value.trim();
            const name = byId('pgTaxonName').value.trim();
            if (!id || !name) {
                setStatus('Custom taxonomy ID and name are required.', 'error');
                return;
            }

            const payload = {
                id: id,
                name: name,
                kind: kindToNumber(byId('pgTaxonKind').value),
                parentIds: splitCsv(byId('pgTaxonParents').value),
                contextOnly: byId('pgTaxonContextOnly').checked,
                aliases: splitCsv(byId('pgTaxonAliases').value)
            };

            try {
                await requestJson(apiBase + '/taxonomy/custom', {
                    method: 'POST',
                    body: JSON.stringify(payload)
                });
                resetTaxonForm();
                await reloadTaxonomy();
                setStatus('Custom taxon "' + name + '" saved.', 'success');
            } catch (error) {
                setStatus(error.message, 'error');
            }
        });

        byId('pgTaxonomyBody')?.addEventListener('click', async event => {
            const edit = event.target.closest('[data-pg-edit-taxon]');
            if (edit) {
                const taxon = state.taxonomy.find(item => item.id === edit.dataset.pgEditTaxon);
                if (taxon && !taxon.builtIn) fillTaxonForm(taxon);
                return;
            }

            const remove = event.target.closest('[data-pg-delete-taxon]');
            if (!remove) return;
            const id = remove.dataset.pgDeleteTaxon;
            if (!id || !confirm('Delete custom Personal Genre taxon "' + id + '"?')) return;

            try {
                await requestJson(apiBase + '/taxonomy/custom/' + encodeURIComponent(id), { method: 'DELETE' });
                await reloadTaxonomy();
                setStatus('Custom taxon "' + id + '" deleted.', 'success');
            } catch (error) {
                setStatus(error.message, 'error');
            }
        });
    }

    async function reloadTaxonomy() {
        const taxonomy = await requestJson(apiBase + '/taxonomy');
        state.taxonomy = Array.isArray(taxonomy && taxonomy.taxa) ? taxonomy.taxa : [];
        renderTaxonomy();
        populateTaxonSelects();
        renderMappings();
        renderRules();
    }

    function renderTaxonomy() {
        const body = byId('pgTaxonomyBody');
        if (!body) return;
        const query = (byId('pgTaxonomySearch')?.value || '').trim().toLowerCase();
        const kind = (byId('pgTaxonomyKindFilter')?.value || '').trim().toLowerCase();

        const items = state.taxonomy.filter(item => {
            if (kind && String(item.kind || '').toLowerCase() !== kind) return false;
            if (!query) return true;
            const haystack = [
                item.id, item.name, item.kind
            ].concat(item.parentIds || [], item.aliases || []).join(' ').toLowerCase();
            return haystack.includes(query);
        });

        if (!items.length) {
            body.innerHTML = '<tr><td colspan="6" class="pg-muted">No matching taxonomy terms.</td></tr>';
            return;
        }

        body.innerHTML = items.map(item => {
            const parents = (item.parentIds || []).map(escapeHtml).join(', ') || '<span class="pg-muted">—</span>';
            const aliases = (item.aliases || []).map(escapeHtml).join(', ') || '<span class="pg-muted">—</span>';
            const origin = item.builtIn
                ? '<span class="pg-origin">Built-in</span>'
                : '<span class="pg-origin pg-origin--custom">Custom</span>';
            const actions = item.builtIn ? '' :
                '<div class="pg-row-actions">' +
                '<button class="pg-btn pg-btn--small" type="button" data-pg-edit-taxon="' + escapeAttribute(item.id) + '">Edit</button>' +
                '<button class="pg-btn pg-btn--small pg-btn--danger" type="button" data-pg-delete-taxon="' + escapeAttribute(item.id) + '">Delete</button>' +
                '</div>';
            return '<tr>' +
                '<td><strong>' + escapeHtml(item.name) + '</strong><br><span class="pg-muted">' + escapeHtml(item.id) + '</span></td>' +
                '<td><span class="pg-kind">' + escapeHtml(item.kind) + '</span>' +
                    (item.contextOnly ? '<br><span class="pg-muted">context-only</span>' : '') + '</td>' +
                '<td>' + parents + '</td>' +
                '<td>' + aliases + '</td>' +
                '<td>' + origin + '</td>' +
                '<td>' + actions + '</td>' +
                '</tr>';
        }).join('');
    }

    function fillTaxonForm(taxon) {
        byId('pgTaxonId').value = taxon.id || '';
        byId('pgTaxonId').readOnly = true;
        byId('pgTaxonName').value = taxon.name || '';
        byId('pgTaxonKind').value = String(taxon.kind || 'genre').toLowerCase();
        byId('pgTaxonParents').value = (taxon.parentIds || []).join(', ');
        byId('pgTaxonAliases').value = (taxon.aliases || []).join(', ');
        byId('pgTaxonContextOnly').checked = taxon.contextOnly === true;
        const details = byId('pgTaxonForm')?.closest('details');
        if (details) details.open = true;
        byId('pgTaxonName')?.focus();
    }

    function resetTaxonForm() {
        byId('pgTaxonForm')?.reset();
        byId('pgTaxonId').readOnly = false;
        byId('pgTaxonKind').value = 'genre';
        byId('pgTaxonContextOnly').checked = false;
    }

    function bindMappings() {
        byId('pgMappingReset')?.addEventListener('click', resetMappingForm);
        byId('pgMappingForm')?.addEventListener('submit', async event => {
            event.preventDefault();
            const payload = {
                id: Number(byId('pgMappingId').value) || 0,
                matchValue: byId('pgMappingValue').value.trim(),
                targetTaxonId: byId('pgMappingTarget').value,
                source: nullableText(byId('pgMappingSource').value),
                priority: toInt(byId('pgMappingPriority').value, 100),
                action: toInt(byId('pgMappingAction').value, 0),
                enabled: byId('pgMappingEnabled').checked
            };

            try {
                await requestJson(apiBase + '/mappings', { method: 'POST', body: JSON.stringify(payload) });
                resetMappingForm();
                await reloadMappings();
                setStatus('Provider mapping saved.', 'success');
            } catch (error) {
                setStatus(error.message, 'error');
            }
        });

        byId('pgMappingsBody')?.addEventListener('click', async event => {
            const edit = event.target.closest('[data-pg-edit-mapping]');
            if (edit) {
                const item = state.mappings.find(mapping => String(mapping.id) === edit.dataset.pgEditMapping);
                if (item) fillMappingForm(item);
                return;
            }

            const remove = event.target.closest('[data-pg-delete-mapping]');
            if (!remove) return;
            const id = Number(remove.dataset.pgDeleteMapping);
            if (!id || !confirm('Delete this provider mapping?')) return;
            try {
                await requestJson(apiBase + '/mappings/' + id, { method: 'DELETE' });
                await reloadMappings();
                setStatus('Provider mapping deleted.', 'success');
            } catch (error) {
                setStatus(error.message, 'error');
            }
        });
    }

    async function reloadMappings() {
        state.mappings = await requestJson(apiBase + '/mappings');
        renderMappings();
    }

    function renderMappings() {
        const body = byId('pgMappingsBody');
        if (!body) return;
        if (!state.mappings.length) {
            body.innerHTML = '<tr><td colspan="7" class="pg-muted">No provider mappings yet.</td></tr>';
            return;
        }

        body.innerHTML = state.mappings.map(item =>
            '<tr>' +
            '<td><strong>' + escapeHtml(item.matchValue) + '</strong></td>' +
            '<td>' + (item.source ? escapeHtml(item.source) : '<span class="pg-muted">Any source</span>') + '</td>' +
            '<td>' + escapeHtml(formatMappingAction(item.action)) + '</td>' +
            '<td>' + formatMappingTarget(item) + '</td>' +
            '<td>' + escapeHtml(String(item.priority)) + '</td>' +
            '<td><span class="pg-state ' + (item.enabled ? 'pg-state--enabled' : '') + '">' + (item.enabled ? 'Enabled' : 'Disabled') + '</span></td>' +
            '<td><div class="pg-row-actions">' +
            '<button class="pg-btn pg-btn--small" type="button" data-pg-edit-mapping="' + item.id + '">Edit</button>' +
            '<button class="pg-btn pg-btn--small pg-btn--danger" type="button" data-pg-delete-mapping="' + item.id + '">Delete</button>' +
            '</div></td></tr>'
        ).join('');
    }

    function fillMappingForm(item) {
        byId('pgMappingId').value = item.id || 0;
        byId('pgMappingValue').value = item.matchValue || '';
        byId('pgMappingSource').value = item.source || '';
        byId('pgMappingAction').value = String(normalizeMappingActionValue(item.action));
        byId('pgMappingTarget').value = item.targetTaxonId || '';
        byId('pgMappingPriority').value = item.priority ?? 100;
        byId('pgMappingEnabled').checked = item.enabled !== false;
        byId('pgMappingValue').focus();
    }

    function resetMappingForm() {
        byId('pgMappingForm')?.reset();
        byId('pgMappingId').value = 0;
        byId('pgMappingPriority').value = 100;
        byId('pgMappingAction').value = '0';
        byId('pgMappingEnabled').checked = true;
    }

    function bindRules() {
        byId('pgRuleReset')?.addEventListener('click', resetRuleForm);
        byId('pgRuleForm')?.addEventListener('submit', async event => {
            event.preventDefault();
            const payload = {
                id: Number(byId('pgRuleId').value) || 0,
                matchValue: byId('pgRuleValue').value.trim(),
                targetTaxonId: byId('pgRuleTarget').value,
                source: nullableText(byId('pgRuleSource').value),
                priority: toInt(byId('pgRulePriority').value, 1000),
                enabled: byId('pgRuleEnabled').checked
            };

            try {
                await requestJson(apiBase + '/rules', { method: 'POST', body: JSON.stringify(payload) });
                resetRuleForm();
                await reloadRules();
                setStatus('User rule saved.', 'success');
            } catch (error) {
                setStatus(error.message, 'error');
            }
        });

        byId('pgRulesBody')?.addEventListener('click', async event => {
            const edit = event.target.closest('[data-pg-edit-rule]');
            if (edit) {
                const item = state.rules.find(rule => String(rule.id) === edit.dataset.pgEditRule);
                if (item) fillRuleForm(item);
                return;
            }

            const remove = event.target.closest('[data-pg-delete-rule]');
            if (!remove) return;
            const id = Number(remove.dataset.pgDeleteRule);
            if (!id || !confirm('Delete this user rule?')) return;
            try {
                await requestJson(apiBase + '/rules/' + id, { method: 'DELETE' });
                await reloadRules();
                setStatus('User rule deleted.', 'success');
            } catch (error) {
                setStatus(error.message, 'error');
            }
        });
    }

    async function reloadRules() {
        state.rules = await requestJson(apiBase + '/rules');
        renderRules();
    }

    function renderRules() {
        const body = byId('pgRulesBody');
        if (!body) return;
        if (!state.rules.length) {
            body.innerHTML = '<tr><td colspan="6" class="pg-muted">No user rules yet.</td></tr>';
            return;
        }

        body.innerHTML = state.rules.map(item =>
            '<tr>' +
            '<td><strong>' + escapeHtml(item.matchValue) + '</strong></td>' +
            '<td>' + (item.source ? escapeHtml(item.source) : '<span class="pg-muted">Any source</span>') + '</td>' +
            '<td>' + formatTaxon(item.targetTaxonId) + '</td>' +
            '<td>' + escapeHtml(String(item.priority)) + '</td>' +
            '<td><span class="pg-state ' + (item.enabled ? 'pg-state--enabled' : '') + '">' + (item.enabled ? 'Enabled' : 'Disabled') + '</span></td>' +
            '<td><div class="pg-row-actions">' +
            '<button class="pg-btn pg-btn--small" type="button" data-pg-edit-rule="' + item.id + '">Edit</button>' +
            '<button class="pg-btn pg-btn--small pg-btn--danger" type="button" data-pg-delete-rule="' + item.id + '">Delete</button>' +
            '</div></td></tr>'
        ).join('');
    }

    function fillRuleForm(item) {
        byId('pgRuleId').value = item.id || 0;
        byId('pgRuleValue').value = item.matchValue || '';
        byId('pgRuleSource').value = item.source || '';
        byId('pgRuleTarget').value = item.targetTaxonId || '';
        byId('pgRulePriority').value = item.priority ?? 1000;
        byId('pgRuleEnabled').checked = item.enabled !== false;
        byId('pgRuleValue').focus();
    }

    function resetRuleForm() {
        byId('pgRuleForm')?.reset();
        byId('pgRuleId').value = 0;
        byId('pgRulePriority').value = 1000;
        byId('pgRuleEnabled').checked = true;
    }

    function populateTaxonSelects() {
        const grouped = new Map();
        state.taxonomy.forEach(item => {
            const key = String(item.kind || 'other');
            if (!grouped.has(key)) grouped.set(key, []);
            grouped.get(key).push(item);
        });

        const html = ['genre', 'style', 'substyle', 'context']
            .filter(kind => grouped.has(kind))
            .map(kind => {
                const options = grouped.get(kind)
                    .slice()
                    .sort((a, b) => String(a.name).localeCompare(String(b.name)))
                    .map(item =>
                        '<option value="' + escapeAttribute(item.id) + '">' +
                        escapeHtml(item.name) + ' · ' + escapeHtml(item.id) +
                        '</option>')
                    .join('');
                return '<optgroup label="' + capitalize(kind) + '">' + options + '</optgroup>';
            }).join('');

        ['pgMappingTarget', 'pgRuleTarget'].forEach(id => {
            const select = byId(id);
            if (!select) return;
            const previous = select.value;
            select.innerHTML = html;
            if (previous && state.taxonomy.some(item => item.id === previous)) {
                select.value = previous;
            }
        });
    }

    function bindPortability() {
        byId('pgExportButton')?.addEventListener('click', async () => {
            try {
                const payload = await requestJson(apiBase + '/configuration/export');
                const blob = new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' });
                const url = URL.createObjectURL(blob);
                const link = document.createElement('a');
                const date = new Date().toISOString().slice(0, 10);
                link.href = url;
                link.download = 'deezspotag-personal-genre-' + date + '.json';
                document.body.appendChild(link);
                link.click();
                link.remove();
                URL.revokeObjectURL(url);
                setStatus('Personal Genre configuration exported.', 'success');
            } catch (error) {
                setStatus(error.message, 'error');
            }
        });

        byId('pgImportButton')?.addEventListener('click', async () => {
            const file = byId('pgImportFile')?.files?.[0];
            if (!file) {
                setStatus('Choose a Personal Genre JSON file first.', 'error');
                return;
            }

            try {
                const text = await file.text();
                const payload = JSON.parse(text);
                const result = await requestJson(apiBase + '/configuration/import', {
                    method: 'POST',
                    body: JSON.stringify(payload)
                });
                await loadAll();
                setStatus(
                    'Import complete · ' + result.customTaxaImported + ' custom taxa · ' +
                    result.mappingsImported + ' mappings · ' +
                    result.rulesImported + ' rules · ' +
                    result.duplicatesSkipped + ' duplicates skipped',
                    'success');
            } catch (error) {
                setStatus(error.message || 'Import failed.', 'error');
            }
        });
    }

    async function requestJson(url, options = {}) {
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

        if (response.status === 204) return null;
        const text = await response.text();
        let payload = null;
        if (text) {
            try { payload = JSON.parse(text); } catch { payload = text; }
        }

        if (!response.ok) {
            const message = payload && typeof payload === 'object'
                ? payload.error || payload.message
                : null;
            throw new Error(message || 'Request failed (' + response.status + ').');
        }

        return payload;
    }

    function normalizeMappingActionValue(value) {
        if (typeof value === 'number') return value;
        switch (String(value || '').toLowerCase().replaceAll('_', '')) {
            case 'contextonly': return 1;
            case 'ignore': return 2;
            case 'ambiguous': return 3;
            default: return 0;
        }
    }

    function formatMappingAction(value) {
        switch (normalizeMappingActionValue(value)) {
            case 1: return 'Context only';
            case 2: return 'Ignore';
            case 3: return 'Ambiguous';
            default: return 'Map';
        }
    }

    function formatMappingTarget(item) {
        const action = normalizeMappingActionValue(item.action);
        if (action === 2 || action === 3) {
            return '<span class="pg-muted">Not used</span>';
        }
        return formatTaxon(item.targetTaxonId);
    }

    function formatTaxon(id) {
        const taxon = state.taxonomy.find(item => item.id === id);
        if (!taxon) return '<span class="pg-muted">' + escapeHtml(id || 'Unknown') + '</span>';
        return escapeHtml(taxon.name) + ' <span class="pg-muted">(' + escapeHtml(taxon.kind) + ')</span>';
    }

    function setStatus(message, kind = '') {
        const element = byId('pgStatus');
        if (!element) return;
        element.textContent = message || '';
        element.classList.toggle('is-error', kind === 'error');
        element.classList.toggle('is-success', kind === 'success');
    }

    function splitCsv(value) {
        const values = String(value || '').split(',').map(item => item.trim()).filter(Boolean);
        return values.filter((item, index, array) =>
            array.findIndex(candidate => candidate.toLowerCase() === item.toLowerCase()) === index);
    }

    function nullableText(value) {
        const text = String(value || '').trim();
        return text || null;
    }

    function kindToNumber(kind) {
        switch (String(kind || '').toLowerCase()) {
            case 'genre': return 0;
            case 'style': return 1;
            case 'substyle': return 2;
            case 'context': return 3;
            default: return 0;
        }
    }

    function clampInt(value, min, max, fallback) {
        const parsed = Number.parseInt(value, 10);
        if (!Number.isFinite(parsed)) return fallback;
        return Math.min(max, Math.max(min, parsed));
    }

    function toInt(value, fallback) {
        const parsed = Number.parseInt(value, 10);
        return Number.isFinite(parsed) ? parsed : fallback;
    }

    function capitalize(value) {
        const text = String(value || '');
        return text ? text[0].toUpperCase() + text.slice(1) : text;
    }

    function escapeHtml(value) {
        return String(value ?? '')
            .replaceAll('&', '&amp;')
            .replaceAll('<', '&lt;')
            .replaceAll('>', '&gt;')
            .replaceAll('"', '&quot;')
            .replaceAll("'", '&#39;');
    }

    function escapeAttribute(value) {
        return escapeHtml(value);
    }
})();
