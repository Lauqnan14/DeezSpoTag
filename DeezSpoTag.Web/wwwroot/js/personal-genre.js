(() => {
    'use strict';

    const apiBase = '/api/personal-genre';
    const state = {
        taxonomy: [], settings: null, mappings: [], rules: [], aliasRules: [],
        // Region is presentation state only. It is never sent to the resolver and it
        // never changes a classification; it only decides which slice of the one
        // taxonomy this table is currently showing.
        region: 'global',
        regionNames: {}
    };
    const byId = id => document.getElementById(id);

    document.addEventListener('DOMContentLoaded', () => {
        bindSettings();
        bindAliasRules();
        bindRegions();
        bindRegionMapLoading();
        bindTaxonomy();
        bindTaxonomyControls();
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
            // The region vocabulary comes from the same response as the taxonomy, so
            // the names are never written out a second time on the client.
            readRegionDirectory(taxonomy && taxonomy.regions);
            state.settings = results[1] || {};
            state.mappings = Array.isArray(results[2]) ? results[2] : [];
            state.rules = Array.isArray(results[3]) ? results[3] : [];
            renderSettings();
            renderAliasRules();
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

    function bindSettings() {
        const form = byId('pgSettingsForm');
        if (!form) return;
        form.addEventListener('submit', async event => {
            event.preventDefault();
            try {
                // The normalization fields are sent with every save so a resolver
                // change cannot silently drop them.
                await saveSettings(collectSettings());
                setStatus('Personal Genre settings saved.', 'success');
            } catch (error) {
                setStatus(error.message, 'error');
            }
        });

        byId('pgRebuildButton')?.addEventListener('click', () => {
            void rebuildExistingResults();
        });

        byId('pgAliasRuleAdd')?.addEventListener('click', () => {
            state.aliasRules = [...(state.aliasRules || []), { alias: '', canonical: '' }];
            renderAliasRules();
            const rows = byId('pgAliasRulesBody')?.querySelectorAll('input');
            rows?.[rows.length - 2]?.focus();
        });

        byId('pgSaveNormalization')?.addEventListener('click', async () => {
            try {
                await saveSettings(collectSettings());
                setStatus('Genre normalization saved.', 'success');
            } catch (error) {
                setStatus(error.message, 'error');
            }
        });
    }

    /// Builds the complete settings payload.
    ///
    /// The normalization fields are always included, never only when their own
    /// card is saved. The server treats an omitted alias list as "use the
    /// defaults", so a partial payload from the main settings form would silently
    /// discard the user's alias rules every time they changed something else.
    function collectSettings() {
        return {
            enabled: byId('pgEnabled').checked,
            maxGenres: clampInt(byId('pgMaxGenres').value, 1, 10, 3),
            preserveUnmappedTags: byId('pgPreserveUnmappedTags').checked,
            includeParentGenres: byId('pgIncludeParents').checked,
            normalizeGenreTags: byId('pgNormalizeGenreTags')?.checked === true,
            genreTagAliasRules: collectAliasRules(),
            genreTagBlockList: collectBlockList()
        };
    }

    async function saveSettings(payload) {
        state.settings = await requestJson(apiBase + '/settings', {
            method: 'POST',
            body: JSON.stringify(payload)
        });
        // Re-read the normalization fields from the response so the editor shows
        // exactly what was stored, including the values the server normalized.
        if (state.settings) {
            state.aliasRules = Array.isArray(state.settings.genreTagAliasRules)
                ? state.settings.genreTagAliasRules
                : [];
            renderSettings();
            renderAliasRules();
            if (byId('pgGenreBlockList')) {
                byId('pgGenreBlockList').value =
                    (state.settings.genreTagBlockList || []).join('\n');
            }
        }
        return state.settings;
    }

    /// Reads the alias editor back out of the DOM. A half-filled row is dropped
    /// rather than sent: an empty alias has no meaning to the normalizer.
    function collectAliasRules() {
        const rows = byId('pgAliasRulesBody')?.querySelectorAll('tr[data-pg-alias]');
        if (!rows) return [];
        const output = [];
        rows.forEach(row => {
            const alias = row.querySelector('.pg-alias-alias')?.value.trim() || '';
            const canonical = row.querySelector('.pg-alias-canonical')?.value.trim() || '';
            if (alias && canonical) {
                output.push({ alias, canonical });
            }
        });
        return output;
    }

    /// Splits the block list on newlines as well as commas, because the control
    /// invites one value per line.
    function collectBlockList() {
        return byId('pgGenreBlockList')?.value
            .split(/[\n,]/)
            .map(item => item.trim())
            .filter(Boolean)
            .filter((item, index, array) =>
                array.findIndex(candidate => candidate.toLowerCase() === item.toLowerCase()) === index) || [];
    }

    function renderAliasRules() {
        const body = byId('pgAliasRulesBody');
        if (!body) return;
        const rules = state.aliasRules || [];
        // The empty-state paragraph is a sibling of the table, so it is toggled
        // through its hidden attribute rather than the table's own markup.
        const empty = byId('pgAliasRulesEmpty');
        if (empty && typeof empty.toggleAttribute === 'function') {
            empty.toggleAttribute('hidden', rules.length > 0);
        }

        body.innerHTML = rules.map((rule, index) =>
            '<tr data-pg-alias>' +
            '<td><input class="pg-alias-alias" value="' + escapeHtml(rule.alias || '') + '" placeholder="Afro-Pop"></td>' +
            '<td><input class="pg-alias-canonical" value="' + escapeHtml(rule.canonical || '') + '" placeholder="Afropop"></td>' +
            '<td><div class="genre-intelligence-row-actions">' +
            '<button class="action-btn action-btn-sm btn-danger" type="button" data-pg-remove-alias="' + index + '">Remove</button>' +
            '</div></td></tr>'
        ).join('');
    }

    function bindAliasRules() {
        byId('pgAliasRulesBody')?.addEventListener('click', event => {
            const remove = event.target.closest('[data-pg-remove-alias]');
            if (!remove) return;
            const index = Number(remove.dataset.pgRemoveAlias);
            if (Number.isNaN(index)) return;
            // Capture first: removing a row re-renders the table, and an unsaved
            // edit in another row would otherwise be lost.
            state.aliasRules = collectAliasRules();
            state.aliasRules.splice(index, 1);
            renderAliasRules();
        });
    }

    async function rebuildExistingResults() {
        const button = byId('pgRebuildButton');
        const status = byId('pgRebuildStatus');
        if (!button || button.disabled) return;

        button.disabled = true;
        const originalText = button.textContent;
        let afterTrackId = 0;
        let processed = 0;
        let resolved = 0;
        let skipped = 0;

        try {
            do {
                const result = await requestJson(
                    apiBase + '/rebuild?afterTrackId=' + encodeURIComponent(afterTrackId) + '&batchSize=200',
                    { method: 'POST' });

                const batchProcessed = Number(result?.processed || 0);
                processed += batchProcessed;
                resolved += Number(result?.resolved || 0);
                skipped += Number(result?.skipped || 0);
                afterTrackId = Number(result?.lastTrackId || afterTrackId);

                if (status) {
                    status.textContent =
                        'Rebuild running · ' + processed + ' processed · ' +
                        resolved + ' resolved · ' + skipped + ' skipped';
                }

                if (!result?.hasMore || batchProcessed === 0) {
                    break;
                }
            } while (true);

            if (status) {
                status.textContent =
                    'Rebuild complete · ' + processed + ' processed · ' +
                    resolved + ' resolved · ' + skipped + ' skipped. No audio analysis or file writes were performed.';
            }
            setStatus('Personal Genre rebuild complete.', 'success');
        } catch (error) {
            if (status) {
                status.textContent = 'Rebuild stopped: ' + (error.message || error);
            }
            setStatus(error.message || 'Personal Genre rebuild failed.', 'error');
        } finally {
            button.disabled = false;
            button.textContent = originalText;
        }
    }

    function renderSettings() {
        const settings = state.settings || {};
        byId('pgEnabled').checked = settings.enabled !== false;
        byId('pgMaxGenres').value = clampInt(settings.maxGenres, 1, 10, 3);
        byId('pgPreserveUnmappedTags').checked = settings.preserveUnmappedTags !== false;
        byId('pgIncludeParents').checked = settings.includeParentGenres === true;
        if (byId('pgNormalizeGenreTags')) {
            byId('pgNormalizeGenreTags').checked = settings.normalizeGenreTags === true;
        }
        state.aliasRules = Array.isArray(settings.genreTagAliasRules)
            ? settings.genreTagAliasRules
            : [];
        if (byId('pgGenreBlockList')) {
            byId('pgGenreBlockList').value = (settings.genreTagBlockList || []).join('\n');
        }
    }

    function readRegionDirectory(regions) {
        const names = {};
        (Array.isArray(regions) ? regions : []).forEach(region => {
            if (region && region.slug) names[region.slug] = region.name || region.slug;
        });
        state.regionNames = names;
    }

    function regionName(slug) {
        return state.regionNames[slug] || slug;
    }

    /// Whether a term belongs in the region view currently on screen.
    ///
    /// A regional view shows that region's own terms together with the universal
    /// ones, because a taxonomy browser that hid Pop and Rock when you switched to
    /// Africa would be lying about the vocabulary. The two groups stay visually
    /// distinct, and neither is duplicated: this is one filter over one list.
    function matchesRegion(item) {
        const current = state.region;
        if (current === 'all') return true;
        const regions = Array.isArray(item.regions) ? item.regions : [];
        if (current === 'global') return regions.length === 0;
        return regions.indexOf(current) !== -1 || regions.length === 0;
    }

    function isUniversal(item) {
        return !Array.isArray(item.regions) || item.regions.length === 0;
    }

    // The world map is a separate, fairly large asset. It is fetched the first
    // time the Genre Intelligence tab is actually shown rather than being inlined,
    // so an AutoTag visit never pays for a decorative illustration.
    const regionMapUrl = '/images/world/world-regions.svg';
    let regionMapRequest = null;

    function ensureRegionMap() {
        const host = byId('pgRegionMap');
        if (!host || typeof fetch !== 'function') return;
        if (typeof host.querySelector !== 'function' || typeof host.insertAdjacentHTML !== 'function') return;

        if (host.querySelector('svg')) return;
        if (!regionMapRequest) {
            regionMapRequest = fetch(regionMapUrl, { credentials: 'same-origin', cache: 'force-cache' })
                .then(response => (response && response.ok ? response.text() : ''))
                .then(markup => {
                    // The map is decoration. If it cannot be fetched the feature is
                    // unaffected, so the failure is contained here.
                    if (!markup || !/<svg/i.test(markup)) return;
                    host.insertAdjacentHTML('afterbegin', markup);
                })
                .catch(() => { regionMapRequest = null; });
        }
    }

    function bindRegionMapLoading() {
        const host = byId('pgRegionMap');
        if (!host) return;

        const panel = byId('autotag-genre-intelligence-panel');
        if (panel && panel.classList && typeof panel.classList.contains === 'function'
            && panel.classList.contains('active')) {
            ensureRegionMap();
            return;
        }

        // The map belongs to the AutoTag Genre Intelligence tab, so wait for it.
        const tabs = byId('autotagTabs');
        if (tabs && typeof tabs.addEventListener === 'function') {
            tabs.addEventListener('shown.bs.tab', event => {
                const trigger = event.target;
                if (trigger && trigger.dataset && trigger.dataset.bsTarget === '#autotag-genre-intelligence-panel') {
                    ensureRegionMap();
                }
            });
        }
    }

    /// Mirrors the selected region onto the map.
    ///
    /// The attribute is the only thing that changes. Highlighting itself is CSS, so
    /// switching regions costs one attribute write and no per-country DOM work.
    function renderRegionMap() {
        const host = byId('pgRegionMap');
        if (host && host.dataset) host.dataset.activeRegion = state.region;
    }

    function bindRegions() {
        const tabs = byId('genreIntelligenceRegionTabs');
        if (!tabs || typeof tabs.addEventListener !== 'function') return;

        // A tab change is presentation only. It re-renders the same taxonomy and
        // touches nothing the resolver can observe.
        // Both handlers do the same idempotent work: shown.bs.tab does not always
        // fire for a tab whose pane has no visible content, so relying on it alone
        // left the table and the scope hint describing the previous region.
        const apply = event => {
            const trigger = event.target.closest('[data-pg-region]');
            if (!trigger) return;
            state.region = trigger.dataset.pgRegion || 'global';
            // A region change is a filter change, so the page returns to the start
            // rather than leaving the user on page 40 of the new region.
            taxonomyFilterChanged();
            renderRegionMap();
        };

        tabs.addEventListener('shown.bs.tab', apply);
        tabs.addEventListener('show.bs.tab', apply);
        tabs.addEventListener('click', apply);
    }

    /// Wires the vocabulary filters, sort and pager.
    ///
    /// Every filter goes through the same reset so that changing one can never leave
    /// the table showing an empty page beyond the end of a shorter result set.
    function bindTaxonomyControls() {
        ['pgTaxonomySearch', 'pgTaxonomyKindFilter', 'pgTaxonomyOriginFilter', 'pgTaxonomySort']
            .forEach(id => byId(id)?.addEventListener('input', taxonomyFilterChanged));

        byId('pgTaxonomyPageSize')?.addEventListener('change', event => {
            const requested = Number(event.target.value);
            taxonomyPage.size = TAXONOMY_PAGE_SIZES.includes(requested) ? requested : 100;
            taxonomyFilterChanged();
        });

        byId('pgTaxonomyPrev')?.addEventListener('click', () => {
            if (taxonomyPage.number <= 1) return;
            taxonomyPage.number -= 1;
            renderTaxonomy();
        });

        byId('pgTaxonomyNext')?.addEventListener('click', () => {
            taxonomyPage.number += 1;
            renderTaxonomy();
        });
    }

    function collectTaxonRegions() {
        const boxes = document.querySelectorAll('[data-pg-region-option]');
        const selected = [];
        (boxes || []).forEach(box => {
            if (box.checked && box.value) selected.push(box.value);
        });
        return selected;
    }

    function fillTaxonRegions(regions) {
        const wanted = new Set(Array.isArray(regions) ? regions : []);
        (document.querySelectorAll('[data-pg-region-option]') || []).forEach(box => {
            box.checked = wanted.has(box.value);
        });
    }

    function renderRegionScopeHint() {
        const hint = byId('pgRegionScopeHint');
        if (!hint) return;
        if (state.region === 'all') {
            hint.textContent = 'Showing every taxonomy term once. Pick a region to narrow the vocabulary.';
            return;
        }
        if (state.region === 'global') {
            hint.textContent = 'Showing global and universal terms. Pick a region to see the vocabulary filed under it.';
            return;
        }
        hint.textContent = 'Showing ' + regionName(state.region) +
            ' terms together with the universal ones. Global terms are never filed under a region.';
    }

    function bindTaxonomy() {
        byId('pgTaxonomySearch')?.addEventListener('input', renderTaxonomy);
        byId('pgTaxonomyKindFilter')?.addEventListener('change', renderTaxonomy);
        byId('pgTaxonReset')?.addEventListener('click', resetTaxonForm);
        byId('pgTaxonKind')?.addEventListener('change', event => {
            if (['context', 'scene', 'language'].includes(event.target.value)) {
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
                aliases: splitCsv(byId('pgTaxonAliases').value),
                regions: collectTaxonRegions()
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
            if (!id || !await confirmAction(
                'Delete custom Personal Genre taxon "' + id + '"?',
                'Delete custom taxon?')) return;

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
        readRegionDirectory(taxonomy && taxonomy.regions);
        renderTaxonomy();
        populateTaxonSelects();
        renderMappings();
        renderRules();
    }

    /// Page state for the vocabulary table.
    ///
    /// Held in the script only. Filtering and sorting run over the whole vocabulary
    /// first and paging is applied to that result, so a term on any page stays
    /// reachable by search and no page holds a slice of a different ordering.
    const taxonomyPage = { number: 1, size: 100 };

    const TAXONOMY_PAGE_SIZES = [50, 100, 250];

    /// Every filter that narrows the vocabulary, so a change can reset the page.
    function taxonomyFilterChanged() {
        taxonomyPage.number = 1;
        renderTaxonomy();
    }

    function renderTaxonomy() {
        const body = byId('pgTaxonomyBody');
        if (!body) return;
        const query = (byId('pgTaxonomySearch')?.value || '').trim().toLowerCase();
        const kind = (byId('pgTaxonomyKindFilter')?.value || '').trim().toLowerCase();
        const origin = (byId('pgTaxonomyOriginFilter')?.value || '').trim().toLowerCase();
        const sort = byId('pgTaxonomySort')?.value || 'name';

        renderRegionScopeHint();

        // Filters apply to the entire vocabulary, not to the current page.
        const filtered = state.taxonomy.filter(item => {
            if (!matchesRegion(item)) return false;
            if (kind && String(item.kind || '').toLowerCase() !== kind) return false;
            if (origin && taxonomyOrigin(item) !== origin) return false;
            if (!query) return true;
            const haystack = [
                item.id, item.name, item.kind
            ].concat(item.parentIds || [], item.aliases || []).join(' ').toLowerCase();
            return haystack.includes(query);
        });

        // Sorting also applies to the whole filtered set.
        const sorted = filtered.slice().sort(comparatorFor(sort));

        if (!sorted.length) {
            body.innerHTML = '<tr><td colspan="7" class="text-muted">No matching taxonomy terms.</td></tr>';
            renderTaxonomyPager(0, 0, 0);
            return;
        }

        // Paging is applied last, and only the current page reaches the DOM.
        const total = sorted.length;
        const pageCount = Math.max(1, Math.ceil(total / taxonomyPage.size));
        if (taxonomyPage.number > pageCount) {
            taxonomyPage.number = pageCount;
        }
        const start = (taxonomyPage.number - 1) * taxonomyPage.size;
        const page = sorted.slice(start, start + taxonomyPage.size);

        body.innerHTML = page.map(renderTaxonomyRow).join('');
        renderTaxonomyPager(start + 1, Math.min(start + page.length, total), total);
    }

    function comparatorFor(sort) {
        const byName = (left, right) => String(left.name || '').localeCompare(
            String(right.name || ''), undefined, { sensitivity: 'base' });
        if (sort === 'kind') {
            return (left, right) =>
                String(left.kind || '').localeCompare(String(right.kind || '')) || byName(left, right);
        }
        if (sort === 'origin') {
            return (left, right) =>
                taxonomyOrigin(left).localeCompare(taxonomyOrigin(right)) || byName(left, right);
        }
        if (sort === 'recent') {
            return (left, right) => String(right.id || '').localeCompare(String(left.id || ''));
        }
        return byName;
    }

    function renderTaxonomyRow(item) {
        const parents = (item.parentIds || []).map(escapeHtml).join(', ') || '<span class="text-muted">—</span>';
        const aliases = (item.aliases || []).map(escapeHtml).join(', ') || '<span class="text-muted">—</span>';
        const actions = item.editable === true
            ? '<div class="genre-intelligence-row-actions">' +
                '<button class="action-btn action-btn-sm" type="button" data-pg-edit-taxon="' + escapeAttribute(item.id) + '">Edit</button>' +
                '<button class="action-btn action-btn-sm btn-danger" type="button" data-pg-delete-taxon="' + escapeAttribute(item.id) + '">Delete</button>' +
                '</div>'
            : '';
        return '<tr' + (isUniversal(item) ? ' class="genre-intelligence-row-universal"' : '') + '>' +
            '<td><strong>' + escapeHtml(item.displayName ?? item.name) + '</strong><br><span class="text-muted">' + escapeHtml(item.id) + '</span></td>' +
            '<td><span class="genre-intelligence-badge genre-intelligence-badge-kind">' + escapeHtml(item.kind) + '</span>' +
                (item.contextOnly ? '<br><span class="text-muted">context-only</span>' : '') + '</td>' +
            '<td>' + parents + '</td>' +
            '<td>' + aliases + '</td>' +
            '<td>' + renderRegionsCell(item) + '</td>' +
            '<td>' + renderOriginCell(item) + '</td>' +
            '<td>' + actions + '</td>' +
            '</tr>';
    }

    function taxonomyOrigin(item) {
        const origin = String(item.origin || '').toLowerCase();
        return origin === 'core' || origin === 'researched' ? 'built-in' : 'custom';
    }

    function renderOriginCell(item) {
        const builtIn = taxonomyOrigin(item) === 'built-in';
        const label = builtIn ? 'In-built' : 'Custom';
        const modifier = builtIn ? 'built-in' : 'custom';
        return '<span class="genre-intelligence-badge genre-intelligence-badge-' + modifier + '">' + label + '</span>';
    }

    function renderTaxonomyPager(from, to, total) {
        const summary = byId('pgTaxonomyPagerSummary');
        if (summary) {
            summary.textContent = total === 0 ? '0 terms' : (from + '–' + to + ' of ' + total);
        }

        const pageCount = Math.max(1, Math.ceil(total / taxonomyPage.size));
        const position = byId('pgTaxonomyPagerPosition');
        if (position) {
            position.textContent = 'Page ' + taxonomyPage.number + ' of ' + pageCount;
        }

        const previous = byId('pgTaxonomyPrev');
        const next = byId('pgTaxonomyNext');
        if (previous) previous.disabled = taxonomyPage.number <= 1;
        if (next) next.disabled = taxonomyPage.number >= pageCount;
    }

    /// One cell listing every region a term is filed under, or "Global" when it is
    /// filed under none. A term in two regions is named twice in the same cell and
    /// still occupies exactly one row of the taxonomy.
    function renderRegionsCell(item) {
        const regions = Array.isArray(item.regions) ? item.regions : [];
        if (!regions.length) {
            return '<span class="genre-intelligence-badge genre-intelligence-badge-global">Global</span>';
        }
        return regions
            .map(slug => '<span class="genre-intelligence-badge genre-intelligence-badge-region">' +
                escapeHtml(regionName(slug)) + '</span>')
            .join(' ');
    }

    function fillTaxonForm(taxon) {
        byId('pgTaxonId').value = taxon.id || '';
        byId('pgTaxonId').readOnly = true;
        byId('pgTaxonName').value = taxon.name || '';
        byId('pgTaxonKind').value = String(taxon.kind || 'genre').toLowerCase();
        byId('pgTaxonParents').value = (taxon.parentIds || []).join(', ');
        byId('pgTaxonAliases').value = (taxon.aliases || []).join(', ');
        byId('pgTaxonContextOnly').checked = taxon.contextOnly === true;
        fillTaxonRegions(taxon.regions);
        const details = byId('pgTaxonForm')?.closest('details');
        if (details) details.open = true;
        byId('pgTaxonName')?.focus();
    }

    function resetTaxonForm() {
        byId('pgTaxonForm')?.reset();
        byId('pgTaxonId').readOnly = false;
        byId('pgTaxonKind').value = 'genre';
        byId('pgTaxonContextOnly').checked = false;
        fillTaxonRegions([]);
    }

    function bindMappings() {
        byId('pgMappingReset')?.addEventListener('click', resetMappingForm);
        byId('pgMappingForm')?.addEventListener('submit', async event => {
            event.preventDefault();
            const payload = {
                id: Number(byId('pgMappingId').value) || 0,
                matchValue: byId('pgMappingValue').value.trim(),
                targetTaxonId: byId('pgMappingTarget').value,
                inputField: nullableText(byId('pgMappingSource').value),
                priority: toInt(byId('pgMappingPriority').value, 100),
                action: toInt(byId('pgMappingAction').value, 0),
                enabled: byId('pgMappingEnabled').checked
            };

            try {
                await requestJson(apiBase + '/mappings', { method: 'POST', body: JSON.stringify(payload) });
                resetMappingForm();
                await reloadMappings();
                setStatus('Tag mapping saved.', 'success');
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
            if (!id || !await confirmAction('Delete this provider mapping?', 'Delete provider mapping?')) return;
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
            body.innerHTML = '<tr><td colspan="7" class="text-muted">No tag mappings yet.</td></tr>';
            return;
        }

        body.innerHTML = state.mappings.map(item =>
            '<tr>' +
            '<td><strong>' + escapeHtml(item.matchValue) + '</strong></td>' +
            '<td>' + formatInputField(item.inputField) + '</td>' +
            '<td>' + escapeHtml(formatMappingAction(item.action)) + '</td>' +
            '<td>' + formatMappingTarget(item) + '</td>' +
            '<td>' + escapeHtml(String(item.priority)) + '</td>' +
            '<td><span class="' + (item.enabled ? 'text-success' : '') + '">' + (item.enabled ? 'Enabled' : 'Disabled') + '</span></td>' +
            '<td><div class="genre-intelligence-row-actions">' +
            '<button class="action-btn action-btn-sm" type="button" data-pg-edit-mapping="' + item.id + '">Edit</button>' +
            '<button class="action-btn action-btn-sm btn-danger" type="button" data-pg-delete-mapping="' + item.id + '">Delete</button>' +
            '</div></td></tr>'
        ).join('');
    }

    function fillMappingForm(item) {
        byId('pgMappingId').value = item.id || 0;
        byId('pgMappingValue').value = item.matchValue || '';
        byId('pgMappingSource').value = item.inputField || '';
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
                inputField: nullableText(byId('pgRuleSource').value),
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
            if (!id || !await confirmAction('Delete this user rule?', 'Delete user rule?')) return;
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
            body.innerHTML = '<tr><td colspan="6" class="text-muted">No user rules yet.</td></tr>';
            return;
        }

        body.innerHTML = state.rules.map(item =>
            '<tr>' +
            '<td><strong>' + escapeHtml(item.matchValue) + '</strong></td>' +
            '<td>' + formatInputField(item.inputField) + '</td>' +
            '<td>' + formatTaxon(item.targetTaxonId) + '</td>' +
            '<td>' + escapeHtml(String(item.priority)) + '</td>' +
            '<td><span class="' + (item.enabled ? 'text-success' : '') + '">' + (item.enabled ? 'Enabled' : 'Disabled') + '</span></td>' +
            '<td><div class="genre-intelligence-row-actions">' +
            '<button class="action-btn action-btn-sm" type="button" data-pg-edit-rule="' + item.id + '">Edit</button>' +
            '<button class="action-btn action-btn-sm btn-danger" type="button" data-pg-delete-rule="' + item.id + '">Delete</button>' +
            '</div></td></tr>'
        ).join('');
    }

    function fillRuleForm(item) {
        byId('pgRuleId').value = item.id || 0;
        byId('pgRuleValue').value = item.matchValue || '';
        byId('pgRuleSource').value = item.inputField || '';
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

        const html = ['genre', 'style', 'substyle', 'context', 'scene', 'language']
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
            return '<span class="text-muted">Not used</span>';
        }
        return formatTaxon(item.targetTaxonId);
    }

    function formatTaxon(id) {
        const taxon = state.taxonomy.find(item => item.id === id);
        if (!taxon) return '<span class="text-muted">' + escapeHtml(id || 'Unknown') + '</span>';
        return escapeHtml(taxon.name) + ' <span class="text-muted">(' + escapeHtml(taxon.kind) + ')</span>';
    }

    function setStatus(message, kind = '') {
        const element = byId('pgStatus');
        if (!element) return;
        element.textContent = message || '';
        element.classList.toggle('text-danger', kind === 'error');
        element.classList.toggle('text-muted', kind === '');
        element.classList.toggle('text-success', kind === 'success');
    }

    async function confirmAction(message, title) {
        const appConfirm = globalThis.DeezSpoTag?.ui?.confirm;
        if (typeof appConfirm !== 'function') {
            setStatus('The app confirmation dialog is unavailable. Reload the page and try again.', 'error');
            return false;
        }

        return await appConfirm(message, {
            title,
            okText: 'Delete',
            cancelText: 'Cancel'
        });
    }

    function splitCsv(value) {
        const values = String(value || '').split(',').map(item => item.trim()).filter(Boolean);
        return values.filter((item, index, array) =>
            array.findIndex(candidate => candidate.toLowerCase() === item.toLowerCase()) === index);
    }

    function formatInputField(value) {
        return value
            ? escapeHtml(String(value))
            : '<span class="text-muted">Any field</span>';
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
            case 'scene': return 4;
            case 'language': return 5;
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
