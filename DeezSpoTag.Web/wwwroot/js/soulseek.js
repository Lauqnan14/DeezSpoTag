/*
 * Soulseek client surface.
 *
 * Everything here talks to the Soulseek API and the Soulseek SignalR hub. The integration never speaks the
 * Soulseek protocol itself: it drives slskd over HTTP, so "search" and "download" are just API calls here.
 *
 * Peer-supplied strings (usernames and remote filenames) are untrusted input that originates outside this
 * application, so every value that reaches the DOM goes through escapeHtml first.
 */
(function (global) {
    'use strict';

    const API_BASE = '/api/v1/soulseek';
    const HUB_URL = '/hubs/soulseek';

    const SEARCH_STAGE_LABELS = {
        queued: 'Queued',
        searching: 'Searching',
        completed: 'Complete',
        timedout: 'Timed out',
        failed: 'Failed'
    };

    const state = {
        connection: null,
        searchTerm: '',
        artist: '',
        title: '',
        album: '',
        isrc: '',
        durationMs: 0,
        coverUrl: '',
        searchId: null,
        generation: 0,
        phase: 'idle',
        byId: new Map(),

        // The quality groups the user has enabled for Soulseek in Download, in ladder order, as sent by the
        // server. This is the single source of truth: the tab never invents a grouping the downloader would
        // not walk.
        groups: [],
        activeGroup: null,
        selectedId: null,
        expandedGroups: new Set(),

        // Which of the side panel's views is showing. Best match is the default because that is the answer
        // a search is for; Album and Selected are the two things a search cannot answer on its own.
        panelView: 'best',

        // One exact peer directory can be expanded in the result table at a time. Checkbox state is kept by
        // release key so collapsing and reopening the same directory does not discard the user's work, and
        // so tracks ticked in one folder stay ticked while another folder is read.
        albumDrawer: null,
        albumSelections: new Map(),
        acceptedOnly: false,
        resultFilter: '',
        resultSort: 'match',
        batchBusy: false,
        batchOutcome: null,

        // Where a queued file should land. A Soulseek candidate is not a library track until the reader says
        // where it goes, and the destinations are the app's own stereo folders rather than a path typed here,
        // so the choice is a folder id and nothing else.
        destinations: [],
        destinationsLoaded: false,
        destinationFolderId: null,

        // Newly observed peer responses in elapsed one-second buckets, shared by poll and hub updates.
        arrivals: [],
        // Files the peers have offered so far. slskd counts them during a search but only hands over the
        // file list when it finalizes, so this is the only honest measure of how much is there to list.
        fileCount: 0,
        startedAt: 0,
        elapsedMs: 0,
        responseCount: 0,
        peerCount: 0,

        busy: false
    };

    let hub = null;
    let hubStarting = false;
    let progressTimer = null;

    /* ---------------------------------------------------------------- helpers */

    /**
     * What an empty result set actually meant. Keyed by the discriminator the API returns, so the wording
     * and the server's classification cannot drift apart silently.
     */
    const OUTCOME_MESSAGES = {
        no_network_responses: 'No peer on Soulseek answered this search. The query may be too specific, or the network may be busy. Nothing was rejected, because nothing came back.',
        all_candidates_rejected: 'Peers answered, but every file they offered was rejected. Relax the Soulseek quality or peer filters to see them.',
        no_files_returned: 'Peers answered the search but offered no files for it.',
        // A search whose results could not be read is a failure, not an answer. It gets its own key so the
        // wording cannot be reached by accident through the peer-count classification, which would describe it
        // as "no peer answered" for a search that had peers answering right up to the read.
        response_retrieval_failed: 'Soulseek search results could not be retrieved. Try the search again.'
    };

    /**
     * Whether a search payload reports a failure to retrieve its results.
     *
     * <p>
     *     Read from the explicit fields rather than inferred from an empty candidate list: a search that failed
     *     and a search that legitimately matched nothing both arrive with no rows, and they need opposite
     *     treatment - one stops polling and says so, the other is a real answer.
     * </p>
     */
    function isSearchFailed(payload) {
        return Boolean(payload)
            && (payload.failed === true || typeof payload.errorCode === 'string' && payload.errorCode.length > 0);
    }

    /**
     * What to say about a failed search, preferring the server's own message.
     *
     * <p>
     *     The server sends a user-safe sentence for exactly this case, so it is used when present and the local
     *     wording is only a fallback for an older payload that carries the code but no message.
     * </p>
     */
    function searchFailureMessage(payload) {
        const provided = typeof payload?.error === 'string' ? payload.error.trim() : '';
        if (provided.length > 0) {
            return provided;
        }

        const code = typeof payload?.errorCode === 'string' ? payload.errorCode : '';
        return OUTCOME_MESSAGES[code] || OUTCOME_MESSAGES.response_retrieval_failed;
    }

    /**
     * The side panel's views.
     *
     * <p>
     *     A search answers one question — which file is best — so that is what the panel opens on. Two more
     *     things a peer search can show are not answers to it: the album the chosen file belongs to, and the
     *     file the user is actually looking at. Both are views of the same result set rather than new data,
     *     so they cost nothing but the listing they need.
     * </p>
     */
    const PANEL_VIEWS = [
        { code: 'best', label: 'Best match' },
        { code: 'album', label: 'Album' },
        { code: 'selected', label: 'Selected' }
    ];


    function escapeHtml(value) {
        if (value === null || value === undefined) {
            return '';
        }
        return String(value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function csrfToken() {
        return document.querySelector('meta[name="deezspotag-csrf-token"]')?.getAttribute('content')?.trim() || '';
    }

    function formatBytes(bytes) {
        const value = Number(bytes);
        if (!Number.isFinite(value) || value <= 0) {
            return '';
        }
        const units = ['B', 'KB', 'MB', 'GB'];
        let index = 0;
        let scaled = value;
        while (scaled >= 1024 && index < units.length - 1) {
            scaled /= 1024;
            index += 1;
        }
        return `${scaled.toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
    }

    function formatSpeed(bytesPerSecond) {
        const formatted = formatBytes(bytesPerSecond);
        return formatted ? `${formatted}/s` : '';
    }

    function byId(id) {
        return document.getElementById(id);
    }

    function setText(id, value) {
        const element = byId(id);
        if (element) {
            element.textContent = value === null || value === undefined ? '' : String(value);
        }
    }

    function setVisible(id, visible) {
        const element = byId(id);
        if (element) {
            element.hidden = !visible;
        }
    }

    async function request(path, options) {
        const settings = Object.assign({ headers: {} }, options || {});
        settings.headers = Object.assign({}, settings.headers);
        if (settings.method && settings.method.toUpperCase() !== 'GET') {
            settings.headers['RequestVerificationToken'] = csrfToken();
            settings.headers['X-CSRF-TOKEN'] = csrfToken();
            if (settings.body !== undefined && typeof settings.body !== 'string') {
                settings.headers['Content-Type'] = 'application/json';
                settings.body = JSON.stringify(settings.body);
            }
        }
        settings.credentials = 'same-origin';

        const response = await fetch(`${API_BASE}${path}`, settings);
        const text = await response.text();
        let payload = null;
        if (text) {
            try {
                payload = JSON.parse(text);
            } catch (error) {
                payload = null;
            }
        }

        if (!response.ok) {
            // The API is not uniform about the error key: the search endpoints use "error" while the queue
            // endpoint uses "message". Read all of them, otherwise the user sees a bare status code instead
            // of the one sentence that tells them what to fix.
            const detail = payload && (payload.error || payload.message || payload.detail || payload.title);
            const message = detail
                ? String(detail)
                : `Soulseek request failed (${response.status}).`;
            const failure = new Error(message);
            failure.status = response.status;
            failure.payload = payload;
            throw failure;
        }

        return payload;
    }

    /* ---------------------------------------------------------------- connection */

    async function loadConnection() {
        try {
            const connection = await request('/connection');
            state.connection = connection;
            renderConnection();
            return connection;
        } catch (error) {
            state.connection = { state: 'unknown', message: error.message, usable: false };
            renderConnection();
            return state.connection;
        }
    }

    function renderConnection() {
        // The tab stays reachable while Soulseek is inactive - hiding it would leave a reader who has just
        // logged out with no explanation and no way to find the login page. What changes is that the tab says
        // why it is inactive and the actions behind it stop being available.
        //
        // It deliberately does not touch the results: a connection probe can land mid-search and must never
        // clear rows the user is reading. An already-running search keeps reporting; only new work is gated,
        // which matches the server, where the same rule applies.
        renderAvailability();
        renderPhase();
        renderQueueAvailability();
    }

    /**
     * Whether Soulseek may be used for new work right now.
     *
     * Read from the same `usable` value the server admits on, so the tab cannot offer an action the API will
     * refuse. A probe that has not answered yet is treated as inactive: the reader is told to log in rather
     * than being allowed to click into a request that is about to come back 503.
     */
    function isSoulseekActive() {
        const connection = state.connection || {};
        return connection.usable === true;
    }

    function renderAvailability() {
        const active = isSoulseekActive();
        const tab = byId('tab-soulseek');
        const notice = byId('soulseek-login-required');

        if (tab) {
            // Not `disabled`: a disabled tab cannot be opened to read the explanation, and cannot carry the
            // link to the login page either.
            tab.setAttribute('aria-disabled', active ? 'false' : 'true');
            tab.classList.toggle('is-inactive', !active);
        }

        if (notice) {
            notice.hidden = active;
            if (!active) {
                const reason = (state.connection && state.connection.message) || '';
                const reasonNode = byId('soulseek-login-required-reason');
                if (reasonNode) {
                    reasonNode.textContent = reason;
                }
            }
        }

        // The result filter stays usable while inactive. It only narrows rows already on screen, so blocking it
        // would take away the reader's ability to inspect what a previous session left visible for no gain.
        // There is no Soulseek-specific term field: the term comes from the shared search box, so the notice
        // above is where the reason belongs.
    }

    /**
     * Keeps the queue controls honest about whether they would be accepted.
     *
     * Every one of these is a new transfer, so none of them may look available while the source is inactive.
     */
    function renderQueueAvailability() {
        const active = isSoulseekActive();
        document.querySelectorAll('[data-soulseek-queue], [data-soulseek-file-download], #soulseek-best-queue')
            .forEach((element) => {
                // Only the elements in the inactive state are ever taken away. When the source recovers, any
                // element this module disabled is handed back, so the controls track the server rather than
                // memorising a transition that has already passed.
                if (active) {
                    element.removeAttribute('aria-disabled');
                    element.classList.remove('is-inactive');
                    return;
                }
                element.setAttribute('aria-disabled', 'true');
                element.classList.add('is-inactive');
            });
    }

    /* ---------------------------------------------------------------- search */

    function renderStage(container, text) {
        const target = byId(container);
        if (target) {
            target.textContent = text;
        }
    }

    function renderStatus() {
        const status = byId('soulseek-search-status');
        if (status) {
            status.textContent = state.statusText || '';
        }
    }

    function setBusy(busy) {
        state.busy = busy;
    }

    async function runSearch() {
        if (state.busy || (state.phase === 'searching' && state.searchTerm === state.title)) {
            return null;
        }

        if (!state.title) {
            state.statusText = 'Search for something first, then open the Soulseek tab.';
            renderStatus();
            return null;
        }

        // This term already has a search behind it, and that search answered. Re-querying would throw the
        // answer away and replace it with whatever the peer network happens to be offering a minute later:
        // the same query legitimately returns a different set each time, which is what made the list look
        // unreliable. A search that came back empty is retried, and a POST that never produced an id left
        // state.searchId null, so both still get a fresh attempt.
        if (state.searchId && state.searchTerm === state.title && state.byId.size > 0) {
            return null;
        }

        // Checked before the phase is allowed to say "searching". Setting phase first and refusing second
        // left the spinner, the Stop button and the histogram title reading as if a search were running while
        // the notice said the opposite, which is exactly the confusion the gate is meant to remove.
        if (!isSoulseekActive()) {
            // Not "Connecting…". Nothing is being connected: a search does not log slskd in, so telling the
            // reader it is about to connect is a promise the app will not keep.
            state.phase = 'idle';
            state.statusText = 'Log in to enable Soulseek.';
            resetResults();
            renderPhase();
            renderStatus();
            renderAvailability();
            return null;
        }

        state.generation += 1;
        state.generationAtStart = state.generation;
        resetResults();
        state.searchTerm = state.title;
        state.phase = 'searching';
        // The clock starts here, not at the POST, so the histogram and the elapsed readout cover the same
        // window the user is actually watching.
        state.startedAt = Date.now();
        state.elapsedMs = 0;
        renderPhase();

        const connection = state.connection || {};

        try {
            setBusy(true);
            const result = await request('/searches', {
                method: 'POST',
                body: {
                    artist: state.artist,
                    title: state.title,
                    album: state.album || null,
                    isrc: state.isrc || null,
                    durationMs: state.durationMs || null,
                    automated: false
                }
            });

            state.searchId = result && result.searchId;
            renderPhase();
            applySearchCover(result);
            renderStatus();

            // Polling reconciles missed hub updates, including events sent before the POST returns.
            void pollResults(state.searchId, state.generationAtStart);

            return result;
        } catch (error) {
            state.phase = 'failed';
            renderPhase();
            state.statusText = error.message;
            renderStatus();
            return null;
        } finally {
            setBusy(false);
        }
    }

    /**
     * Renders where the search is in its lifecycle.
     *
     * <p>
     *     While a search runs the indicator is shown and keeps running even once results are visible, because
     *     peers are still arriving. Only completion, timeout or cancellation ends it. The empty block doubles
     *     as the waiting state during a search and only claims "no results" once a finished search really did
     *     return nothing.
     * </p>
     */
    function renderPhase() {
        const searching = state.phase === 'searching';
        if (searching) {
            if (progressTimer === null) {
                progressTimer = window.setInterval(() => {
                    if (state.phase !== 'searching') {
                        stopProgressTimer();
                        return;
                    }
                    state.elapsedMs = Math.max(state.elapsedMs, Date.now() - state.startedAt);
                    renderStats();
                    renderHistogram();
                }, 250);
            }
        } else {
            stopProgressTimer();
        }

        const stop = byId('soulseek-stop');
        if (stop) stop.disabled = !state.searchId;
        setVisible('soulseek-spinner', searching);
        setVisible('soulseek-stop', searching);
        setVisible('soulseek-hist', searching || state.arrivals.length > 0);

        const title = byId('soulseek-search-title');
        if (title) {
            title.textContent = searching
                ? 'Searching Soulseek'
                : ({ done: 'Search complete', stopped: 'Search stopped', failed: 'Search failed', timedout: 'Search timed out' }[state.phase] || 'Search Soulseek');
        }

        setText('soulseek-search-query', state.title ? `Query for ${state.title}` : '');

        const hasResults = state.byId.size > 0;
        setVisible('soulseek-results-toolbar', hasResults);
        setVisible('soulseek-result-count', hasResults || !searching);
        setText('soulseek-results-badge', searching ? (hasResults ? 'Collecting responses' : 'Awaiting results') :
            ({ done: 'Complete', stopped: 'Stopped', failed: 'Failed', timedout: 'Timed out' }[state.phase] || 'Ready'));

        const empty = byId('soulseek-candidates-empty');
        if (empty) {
            empty.hidden = hasResults;
            if (!hasResults) {
                if (searching) {
                    empty.innerHTML = '<h3>Waiting for files from peers</h3>'
                        + '<p>Peer and file counts update above. Available files will appear here when Soulseek finishes collecting responses.</p>';
                } else if (state.phase === 'stopped') {
                    empty.innerHTML = '<h3>Search stopped</h3><p>No files were returned before the search stopped.</p>';
                } else if (state.phase === 'failed') {
                    empty.innerHTML = '<h3>Search could not finish</h3><p>See the search error above. You can try the search again.</p>';
                } else if (state.phase === 'timedout') {
                    empty.innerHTML = '<h3>Search timed out</h3><p>No usable files were returned before the search reached its time limit.</p>';
                } else if (state.phase === 'done') {
                    empty.innerHTML = '<h3>No results found</h3>'
                        + '<p>No peer returned a usable file for this search.</p>';
                } else {
                    empty.innerHTML = '<h3>No Soulseek Results Yet</h3>'
                        + '<p>Peers are scored, so the best copy is listed first.</p>';
                }
            }
        }

        renderStats();
        renderHistogram();
    }

    function renderStats() {
        const target = byId('soulseek-search-stats');
        if (!target) {
            return;
        }

        const found = state.byId.size;
        const files = Number(state.fileCount || 0);
        const releases = countReleases();
        target.innerHTML = `
            <div class="soulseek-stat"><span>Peers answered</span><b>${escapeHtml(String(state.responseCount || 0))}</b></div>
            <div class="soulseek-stat"><span>Files reported</span><b>${escapeHtml(String(files))}</b></div>
            <div class="soulseek-stat"><span>Elapsed</span><b>${escapeHtml(formatElapsed(state.elapsedMs))}</b></div>
            ${releases > 0 ? `<div class="soulseek-search-release-summary">${escapeHtml(String(releases))} peer release${releases === 1 ? '' : 's'} from ${escapeHtml(String(found))} file${found === 1 ? '' : 's'}</div>` : ''}`;
    }

    /**
     * How many distinct peer folders the results so far span.
     *
     * <p>
     *     Counted over every candidate the download could take, across every quality group, because that is
     *     what a search actually found: several peers each holding a copy of the same release, and one peer
     *     holding several releases. Deliberately not the candidate count, which is a file count.
     * </p>
     */
    function countReleases() {
        const keys = new Set();
        for (const candidate of state.byId.values()) {
            if (candidate.accepted === false) {
                continue;
            }
            keys.add(releaseKey(candidate.username, folderOf(candidate.filename)));
        }
        return keys.size;
    }

    function formatElapsed(ms) {
        const total = Math.max(0, Math.floor(Number(ms) / 1000));
        const minutes = String(Math.floor(total / 60)).padStart(2, '0');
        const seconds = String(total % 60).padStart(2, '0');
        return `${minutes}:${seconds}`;
    }

    function stopProgressTimer() {
        if (progressTimer !== null) {
            if (state.startedAt > 0) {
                state.elapsedMs = Math.max(state.elapsedMs, Date.now() - state.startedAt);
            }
            window.clearInterval(progressTimer);
            progressTimer = null;
        }
    }

    // Poll and SignalR can repeat or reorder cumulative counts. Only a new high-water mark adds arrivals.
    function recordPeerArrivals(responseCount) {
        const count = Number(responseCount);
        const next = Number.isFinite(count) ? Math.max(state.peerCount, count) : state.peerCount;
        if (state.phase === 'searching' && state.startedAt > 0) {
            state.elapsedMs = Math.max(state.elapsedMs, Date.now() - state.startedAt);
            const second = Math.floor(state.elapsedMs / 1000);
            while (state.arrivals.length <= second) state.arrivals.push(0);
            state.arrivals[second] += next - state.peerCount;
        }
        state.peerCount = next;
        state.responseCount = next;
    }

    // Fixed seconds and a fixed 0–20 scale keep completed history still when later responses arrive.
    function renderHistogram() {
        const bars = byId('soulseek-hist-bars');
        if (!bars) return;

        const seconds = Math.max(30, Math.floor(state.elapsedMs / 1000) + 1, state.arrivals.length);
        while (bars.children.length < seconds) {
            bars.appendChild(document.createElement('i'));
        }
        for (let index = 0; index < seconds; index += 1) {
            const count = state.arrivals[index] || 0;
            const bar = bars.children[index];
            bar.style.height = `${Math.min(20, count) / 20 * 100}%`;
            bar.className = count > 20 ? 'soulseek-hist-overflow' : '';
            bar.title = `${index}–${index + 1}s: ${count} peers`;
            bar.setAttribute('aria-label', bar.title);
            bar.setAttribute('role', 'img');
            bar.textContent = count > 20 ? String(count) : '';
        }

        const axis = byId('soulseek-hist-axis');
        if (axis) {
            axis.innerHTML = '';
            for (let second = 0; second <= seconds; second += 1) {
                if (second % 10 !== 0 && second !== seconds) continue;
                // Do not place the last tick beside a ten-second tick if their labels would overlap.
                if (second !== seconds && second !== 0 && seconds - second < 3) continue;
                const label = document.createElement('span');
                label.textContent = `${second}s`;
                label.style.left = `${second / 30 * 100}%`;
                label.className = second === seconds ? 'soulseek-hist-axis-end' : '';
                axis.appendChild(label);
            }
        }
        const cursor = byId('soulseek-hist-cursor');
        if (cursor) cursor.style.left = `${state.elapsedMs / 30000 * 100}%`;
        setText('soulseek-hist-phase', state.phase === 'searching' ? 'searching\u2026' :
            ({ stopped: 'stopped', failed: 'failed', timedout: 'timed out' }[state.phase] || 'done'));
    }

    function resetResults() {
        stopProgressTimer();
        state.statusText = '';
        state.searchId = null;
        state.byId = new Map();
        state.groups = [];
        state.activeGroup = null;
        state.selectedId = null;
        state.expandedGroups = new Set();
        // A new search is a new question, so the panel opens on its answer again rather than on whatever
        // the previous search was left showing.
        state.panelView = 'best';
        state.albumDrawer = null;
        state.albumSelections = new Map();

        // The rows belong to the search that produced them, so a new search must not leave the previous
        // search's rows reachable: a drawer opened from a stale row would browse a folder this search never
        // asked about.
        releaseRowById.clear();
        state.batchBusy = false;
        state.batchOutcome = null;
        // The previous search's artwork belongs to the previous search. Leaving it up would caption a new
        // release with the old one's cover, which is worse than showing the placeholder.
        state.coverUrl = '';
        state.arrivals = [];
        state.responseCount = 0;
        state.fileCount = 0;
        state.peerCount = 0;
        state.startedAt = 0;
        state.elapsedMs = 0;

        for (const id of ['soulseek-groups', 'soulseek-qtabs', 'soulseek-hist-bars']) {
            const element = byId(id);
            if (element) {
                element.innerHTML = '';
            }
        }
        setVisible('soulseek-qtabs', false);
        setVisible('soulseek-hist', false);
        setText('soulseek-batch-outcome', '');
        renderPanel();
    }

    /**
     * Takes the search-level catalogue cover from a server payload.
     *
     * <p>
     *     One search is one track offered by many peers, so the server resolves the cover once and sends it
     *     with the search rather than per candidate. The value is only ever taken when there is one: a
     *     payload that simply does not mention artwork - the live poll, which runs every 750ms and has
     *     nothing new to say about the cover - must never clear a cover the search already supplied, or the
     *     card would flicker back to its placeholder mid-search.
     * </p>
     */
    function applySearchCover(payload) {
        if (!payload) {
            return;
        }

        const url = typeof payload.coverUrl === 'string' ? payload.coverUrl.trim() : '';
        if (url.length > 0) {
            state.coverUrl = url;
        }
    }

    /**
     * Applies the quality groups the server sent.
     *
     * <p>
     *     A group with no results is kept out of the tab strip, because a group the user can click but which
     *     is always empty is noise. The order is the ladder's and is never re-sorted by score: this is the
     *     order the download will actually be attempted in.
     * </p>
     */
    function applyGroups(groups) {
        if (!Array.isArray(groups)) {
            return;
        }

        state.groups = groups
            .filter((group) => group && group.code)
            .map((group) => ({ code: String(group.code), label: String(group.label || group.code) }));

        // A group the server has stopped sending cannot stay selected, and the undetermined quality is not
        // special: it is sent like any other rung when it is allowed, and simply absent when it is not.
        if (!state.groups.some((group) => group.code === state.activeGroup)) {
            state.activeGroup = null;
        }
    }

    /**
     * Maps a Soulseek quality code onto the band styles used by the group header.
     *
     * <p>
     *     The band drives border weight and dash pattern rather than a colour, so the grouping survives a
     *     colour-blind reader. It follows the same three resolution bands the ladder uses.
     * </p>
     */
    function bandForCode(code) {
        const upper = String(code || '').toUpperCase();
        if (upper === 'FLAC_HI_RES_LOSSLESS') return 'hi-res-lossless';
        if (upper === 'FLAC_HI_RES') return 'hi-res';
        if (upper === 'FLAC' || upper === 'LOSSLESS') return 'cd';
        if (upper === 'MP3_320' || upper === 'MP3_256' || upper === 'MP3_192' || upper === 'MP3_128') return 'lossy';
        return 'unknown';
    }

    function bandLabel(code) {
        const found = state.groups.find((group) => group.code === code);
        return found ? found.label : String(code || 'Unknown');
    }

    async function pollResults(searchId, generation) {
        if (!searchId) {
            return;
        }

        for (let attempt = 0; attempt < 200; attempt += 1) {
            if (generation !== state.generation || state.phase === 'stopped') {
                return;
            }

            try {
                const result = await request(`/searches/${encodeURIComponent(searchId)}/results`);
                if (generation !== state.generation || searchId !== state.searchId || state.phase === 'stopped') return;
                // slskd counts the files a search has found while it is still running, but it only hands
                // the file list over once the search finalizes. The count is the only thing the panel can
                // honestly show until then, so it is taken here rather than from the hub event, which does
                // not carry it. A search that has been recorded does not report a count at all, which is why
                // this only replaces what is there when the endpoint sends a number.
                if (result && Number.isFinite(Number(result.fileCount))) {
                    state.fileCount = Number(result.fileCount);
                }
                if (result) {
                    applySearchCover(result);

                    // A retrieval failure ends the poll on its own terms. It is not a completed or timed-out
                    // search, so it is tested before either of those and never by pretending to be one - setting
                    // completed to make the loop stop would report a broken read as a finished search.
                    if (isSearchFailed(result)) {
                        renderSearchFailure(result);
                        return;
                    }

                    mergeResults(result.results || result.candidates || [], result.completed === true, result.timedOut === true, result.responseCount || 0, result.qualityGroups, result.outcome);
                    if (result.completed === true || result.timedOut === true) {
                        return;
                    }
                }
            } catch (error) {
                if (generation !== state.generation || searchId !== state.searchId || state.phase === 'stopped') return;
                // 404 while the search is running means the results are not persisted yet, not that the
                // search is gone. Ending the poll on it is what previously left the tab spinning with no
                // rows and an error on screen.
                if (error.status === 404 && state.phase === 'searching') {
                    await new Promise(resolve => window.setTimeout(resolve, 750));
                    continue;
                }

                state.statusText = error.message;
                renderStatus();
                state.phase = 'failed';
                renderPhase();
                return;
            }

            await new Promise(resolve => window.setTimeout(resolve, 750));
        }
    }

    function renderSummary(result) {
        if (isSearchFailed(result)) {
            renderSearchFailure(result);
            return;
        }

        const total = Number(result?.candidateCount ?? 0) || 0;
        const timedOut = result?.timedOut === true;
        const completed = result?.completed === true;

        const bits = [];
        if (completed) {
            bits.push('Search complete');
        }
        if (timedOut) {
            bits.push('stopped at the timeout');
        }
        bits.push(`${Number(result?.responseCount || 0)} peer response(s)`);
        bits.push(`${total} candidate(s)`);

        state.statusText = bits.join(' · ');
        renderStatus();
    }

    /**
     * Puts a search whose results could not be read on screen and stops it.
     *
     * <p>
     *     The existing `failed` phase is reused rather than added: a transport error, an unreachable slskd and
     *     this both end the same way for the reader - the search is over and will not produce rows on its own.
     *     The phase is not left as `stopped`, so a retry that follows is a fresh search rather than a resume.
     * </p>
     */
    function renderSearchFailure(payload) {
        if (state.phase === 'stopped') return;
        state.phase = 'failed';
        if (Number.isFinite(Number(payload?.responseCount))) state.responseCount = Number(payload.responseCount);
        if (Number.isFinite(Number(payload?.fileCount))) state.fileCount = Number(payload.fileCount);

        state.statusText = searchFailureMessage(payload);
        renderStatus();
        renderPhase();
    }

    function formatDuration(seconds) {
        const total = Math.max(0, Math.floor(Number(seconds) / 60) || 0);
        const mins = total;
        const secs = Math.max(0, Math.floor(Number(seconds) || 0) % 60);
        if (!Number.isFinite(Number(seconds)) || Number(seconds) <= 0) {
            return '';
        }
        return `${mins}:${String(secs).padStart(2, '0')}`;
    }

    /**
     * Reduces a peer's remote path to the name of the file it points at.
     *
     * <p>
     *     slskd reports a candidate as the whole path inside the peer's share, for example
     *     "@@share/Music/Artist/Album/01 - Title.flac". That is where the file lives, not what the file is
     *     called, so the Filename column shows the leaf. Windows and POSIX separators both have to be
     *     handled: a peer's operating system is not the one this app runs on. The full path stays available
     *     as the cell's tooltip, because the folder is still worth knowing when judging a release.
     * </p>
     */
    function fileNameOf(path) {
        const value = String(path || '');
        const segments = value.split(/[\\/]/).filter((segment) => segment.length > 0);
        return segments.length > 0 ? segments[segments.length - 1] : value;
    }

    /**
     * A DOM id for the drawer that belongs to one result row.
     *
     * <p>
     *     The expansion control and the drawer it opens are connected by id, so `aria-controls` points at
     *     something that exists. The candidate id is peer-influenced and may hold anything, so it is folded
     *     into a fixed alphabet instead of being trusted as an id: the same release must produce the same id
     *     on every render, or the chevron's expanded state would flicker on each tick.
     * </p>
     */
    function drawerDomId(anchorId) {
        const value = String(anchorId || '');
        let hash = 2166136261;
        for (let index = 0; index < value.length; index += 1) {
            hash ^= value.charCodeAt(index);
            hash = Math.imul(hash, 16777619);
        }
        return `soulseek-album-drawer-${(hash >>> 0).toString(36)}`;
    }

    /**
     * Whether the drawer for this candidate is the one currently open.
     *
     * <p>
     *     The chevron and `aria-expanded` are read from the same state the drawer renders from, so a control
     *     can never claim to be open while the listing is collapsed, nor be drawn closed while it is open.
     * </p>
     */
    function isDrawerOpenFor(anchor) {
        const drawer = state.albumDrawer;
        if (!drawer || !drawer.expanded) {
            return false;
        }

        // Matched on the release key rather than the anchor's id. The anchor of a release row is whichever
        // candidate the drawer was opened from, and the same release reached from a different track is the
        // same open listing.
        return releaseKey(drawer.username, drawer.remoteDirectory)
            === releaseKey(anchor.username, anchor.remoteDirectory ?? folderOf(anchor.filename));
    }

    /**
     * The catalogue artwork to show, or null when there is none.
     *
     * <p>
     *     Only the catalogue cover is ever shown. A Soulseek peer offers a file and nothing else, and the
     *     tagging pipeline sources its own artwork from the user's own preferences, so nothing here may
     *     influence what ends up written to disk.
     * </p>
     */
    function coverImage(options) {
        const url = String(options.url || '').trim();
        const alt = String(options.alt || '').trim();
        const className = String(options.className || 'soulseek-cover');

        if (!url) {
            return `<div class="${escapeHtml(className)} ${escapeHtml(className)}--placeholder" role="img" aria-label="${escapeHtml(alt || 'No cover art available')}">♪</div>`;
        }

        // The alt text names the release so a screen reader says what the image is. The error handler swaps in
        // the same deliberate placeholder as a missing cover, because a broken catalogue URL is not something
        // the reader can act on and must not leave a broken glyph in the card.
        return `<img class="${escapeHtml(className)}" src="${escapeHtml(url)}" alt="${escapeHtml(alt)}" loading="lazy" decoding="async" onerror="this.outerHTML='<div class=&quot;${escapeHtml(className)} ${escapeHtml(className)}--placeholder&quot; role=&quot;img&quot; aria-label=&quot;${escapeHtml(alt || 'No cover art available')}&quot;>♪</div>'">`;
    }

    /**
     * The candidate panel's and the album drawer's alt text for the search's artwork.
     */
    function coverAltText(album, artist) {
        const release = String(album || '').trim();
        const performer = String(artist || '').trim();
        if (release && performer) {
            return `Cover art for ${release} by ${performer}`;
        }

        return release ? `Cover art for ${release}` : 'Cover art for the selected release';
    }

    /**
     * Renders one peer row.
     *
     * <p>
     *     Every peer-supplied value is escaped at this boundary and then only the escaped locals are
     *     interpolated. A username and a remote filename are attacker-controlled text from the Soulseek
     *     network, so they must never reach a template unescaped.
     * </p>
     */
    function resultRow(entry, index) {
        // Every peer-supplied value is escaped here, once, when it is bound to a local, and only the locals
        // are ever interpolated. That is the invariant the row renderer has always held and the guardrail
        // checks: it means a new column cannot start leaking a raw peer field, because the raw field has no
        // name to reach the template by. The labelled variants exist because a title wants readable fallbacks
        // where a cell wants empty ones, and composing the title from the raw fields and escaping the result
        // would break the rule for no gain.
        const peer = escapeHtml(entry.username || '');
        const peerLabel = escapeHtml(entry.username || 'this peer');
        const remotePath = escapeHtml(entry.remoteDirectory || '');
        const remoteLabel = escapeHtml(entry.remoteDirectory || 'no folder');
        const folderLabel = escapeHtml(entry.folderName || 'release');
        const releaseName = escapeHtml(entry.album || entry.folderName || 'Unnamed release');
        const size = escapeHtml(formatBytes(entry.totalSize));
        const score = Number.isFinite(Number(entry.bestScore)) ? Number(entry.bestScore) * 100 : 0;
        const scoreText = escapeHtml(`${score.toFixed(0)}%`);

        // slskd only reports peers that answered, so a responder was online at response time. A candidate
        // restored from history has no such proof, and says so rather than claiming to be online.
        const online = entry.online === true;
        const dotClass = online ? 'soulseek-dot--on' : 'soulseek-dot--unknown';

        const drawerId = drawerDomId(entry.id);
        const open = isDrawerOpenFor(entry);
        const selection = releaseSelection(entry);
        const releaseTitle = `${peerLabel} · ${remoteLabel}`;
        const browseTitle = open
            ? `Hide the ${folderLabel} file list`
            : `Browse the ${folderLabel} files on ${peerLabel}`;

        // Two different numbers live in this row and they must never be printed under each other's label.
        //
        // Once the folder has been read, both figures come from the peer's own listing and count files.
        // Before that, the only thing known is how much of the folder matched the search, and that measures
        // the query, not the download: a peer who names the tracks "01 - Angel.flac" matched one file in
        // eleven because only "09 - Mezzanine.flac" contains a word of the query, and printing that as
        // "1 of 11 files the download can take" contradicted the drawer it had just been expanded from, which
        // listed all eleven as eligible.
        const browsed = selection.source === 'browse';
        const eligibleCount = selection.takeableCount;
        const listed = selection.listed;
        const complete = eligibleCount > 0 && eligibleCount === listed;
        const coverageLabel = browsed
            ? `${eligibleCount} of ${listed} file${listed === 1 ? '' : 's'} in this folder can be taken`
            : `${entry.eligibleCount} of ${entry.matched} file${entry.matched === 1 ? '' : 's'} match your search`;
        const coverageTitle = browsed
            ? `Counted from the folder's own listing: ${eligibleCount} of ${listed} files can be taken.`
            : `Counted from the search results, so it is how much of the folder matched your search and not `
              + `what can be taken: peers name tracks "01 - Angel.flac", which contains no word of the `
              + `query. Open the folder to see the files it really holds.`;
        const coverage = `<span class="soulseek-coverage" title="${escapeHtml(coverageTitle)}">
                <span class="soulseek-coverage-n">${escapeHtml(String(eligibleCount))}/${escapeHtml(String(listed))}</span>
                <span class="soulseek-coverage-bar${complete ? '' : ' is-partial'}"><i style="width:${listed > 0 ? Math.round((eligibleCount / listed) * 100) : 0}%"></i></span>
            </span>`;

        // A partial selection made inside the drawer is reported back here, so it survives collapsing the
        // drawer. The row itself carries no checkbox: every row in a group is a different peer's copy of the
        // same release, so a column of them asked the same question nineteen times, and choosing which files
        // to take is a decision about tracks, which is made where the tracks are listed.
        const facts = selection.selected > 0
            ? `<span class="soulseek-release-facts is-selected">${escapeHtml(String(selection.selected))} of ${escapeHtml(String(selection.takeableCount))} selected</span>`
            : (entry.folderName
                ? `<span class="soulseek-release-facts">${escapeHtml(coverageLabel)}</span>`
                : `<span class="soulseek-release-facts">${escapeHtml(coverageLabel)} · no folder name given</span>`);

        const selected = entry.id === state.selectedId ? ' soulseek-row-selected' : '';
        const openClass = open ? ' is-open' : '';
        const rejectedClass = eligibleCount === 0 ? ' soulseek-row-rejected' : '';

        return `
            <tr class="soulseek-row soulseek-release${selected}${openClass}${rejectedClass}" data-soulseek-id="${escapeHtml(encodeURIComponent(entry.id))}" data-soulseek-release="${escapeHtml(encodeURIComponent(entry.key))}">
                <td class="soulseek-cell-rank">${escapeHtml(String(index + 1))}</td>
                <td class="soulseek-cell-user" title="${peer}"><span class="soulseek-dot ${dotClass}"></span>${peer}</td>
                <td class="soulseek-cell-release" title="${releaseTitle}"><span class="soulseek-release-name">${releaseName}</span>${facts}</td>
                <td class="soulseek-cell-coverage">${coverage}</td>
                <td class="soulseek-cell-num">${size}</td>
                <td class="soulseek-cell-match">
                    <span class="soulseek-meter">
                        <span class="soulseek-meter-bar"><i style="width:${Math.max(0, Math.min(100, score)).toFixed(0)}%"></i></span>
                        <span class="soulseek-meter-val">${scoreText}</span>
                    </span>
                </td>
                <td class="soulseek-cell-browse">
                    <button type="button" class="action-btn action-btn-sm soulseek-browse${open ? ' is-open' : ''}" data-soulseek-album="${escapeHtml(encodeURIComponent(entry.id))}" aria-expanded="${open ? 'true' : 'false'}" aria-controls="${escapeHtml(drawerId)}" title="${browseTitle}"><span class="soulseek-album-chevron" aria-hidden="true">${open ? '▾' : '▸'}</span>${open ? 'Hide' : 'Browse'}</button>
                </td>
            </tr>`;
    }

    /**
     * Merges a batch of live results in.
     *
     * <p>
     *     Rows already on screen never move. A result that is new is inserted at its ranked position among
     *     the unseen-so-far, so the list stays ordered for new arrivals without shifting anything the user
     *     may be reading or about to click.
     * </p>
     */
    function mergeResults(results, completed, timedOut, responseCount, groups, outcome) {
        if (Array.isArray(groups)) {
            applyGroups(groups);
        }
        if (!Array.isArray(results)) {
            return;
        }

        for (const raw of results) {
            const id = JSON.stringify([raw.username || '', raw.filename || '']);
            if (state.byId.has(id)) {
                continue;
            }
            state.byId.set(id, Object.assign({}, raw, {
                id,
                // The live payload names the code `quality` and the persisted one `qualityCode`, so a row
                // restored after the search finalizes must be normalized here or it would have no group at
                // all and would land in Other with the genuinely undetermined qualities.
                quality: raw.quality ?? raw.qualityCode ?? 'UNKNOWN',
                // A live response is proof the peer was online when it answered. A persisted candidate
                // sends `online: false` because the record carries no liveness evidence, so it reports
                // Unknown rather than claiming to be online.
                online: raw.online === undefined ? true : raw.online === true
            }));
        }

        recordPeerArrivals(responseCount);

        renderGroups();
        renderPanel();

        if (state.byId.size > 0) {
            // The tab strip appears when there is anything to tab between: a rendered group, or the Other
            // bucket on its own. A Custom selection that ticks only Unknown quality has no group at all but
            // must still be able to show what it found.
            setVisible('soulseek-qtabs', state.groups.length > 0);
        }

        // A finished search with nothing in it must say which of the very different "nothing" it was. slskd
        // reports every failure the same way otherwise, and "no results" hides whether the network answered
        // nobody or answered with files that were all rejected.
        if ((completed || timedOut) && state.byId.size === 0 && outcome
            && state.phase !== 'stopped' && state.phase !== 'failed') {
            state.statusText = OUTCOME_MESSAGES[outcome] || OUTCOME_MESSAGES.no_files_returned;
            if (state.phase !== 'stopped' && state.phase !== 'failed') state.phase = timedOut ? 'timedout' : 'done';
            renderStatus();
            renderPhase();
            return;
        }

        if (state.phase !== 'stopped' && state.phase !== 'failed') {
            state.statusText = timedOut ? 'Search reached its time limit.' : '';
        }
        renderStatus();

        if (completed || timedOut) {
            if (state.phase !== 'stopped' && state.phase !== 'failed') state.phase = timedOut ? 'timedout' : 'done';
        }
        renderPhase();
    }

    /**
     * The quality code a candidate groups under.
     *
     * <p>
     *     The live payload names it `quality` and the persisted one names it `qualityCode`, so both are read
     *     here or a row restored after the search finalizes has no code at all and falls into Other.
     *     Undeterminable quality answers `UNKNOWN`, which is what places a candidate in the trailing Other
     *     bucket rather than in a tier.
     * </p>
     */
    function groupCodeOf(candidate) {
        const raw = candidate.quality ?? candidate.qualityCode;
        const code = String(raw ?? '').trim().toUpperCase();
        return code || 'UNKNOWN';
    }

    function isCandidateEnabled(candidate) {
        const code = groupCodeOf(candidate);
        return state.groups.some((group) => group.code.toUpperCase() === code);
    }

    /**
     * Builds the ordered list of candidates for a group.
     *
     * <p>
     *     A group is whatever the server sent, in the server's order. The tab invents nothing: a candidate
     *     whose quality matches no rendered group is invisible here and is left out of the All Results count
     *     too, because it is a quality the download would refuse anyway, and offering it would advertise a
     *     step the ladder will not take. The undetermined quality used to be a client-invented "Other" bucket
     *     that appeared whether or not the download was allowed to take it; it is now the last rung of the
     *     ladder the server sends, so switching Unknown quality off removes the group as well.
     * </p>
     */
    function candidatesFor(code) {
        const wanted = String(code || '').toUpperCase();
        const query = state.resultFilter.trim().toLowerCase();
        // Rejected candidates stay in their group so the user can see why a peer was passed over. Only the
        // best-candidate panel filters them out, because it drives a Queue action.
        return Array.from(state.byId.values())
            .filter((candidate) => groupCodeOf(candidate) === wanted)
            .filter((candidate) => !state.acceptedOnly || candidate.accepted !== false)
            .filter((candidate) => !query || [candidate.username, candidate.filename, candidate.title, candidate.album]
                .some((value) => String(value || '').toLowerCase().includes(query)))
            .sort((a, b) => {
                // An accepted candidate always outranks a rejected one within the same tier, so the best copy
                // a user could actually take is at the top of its group.
                if (a.accepted !== b.accepted) {
                    return a.accepted === false ? 1 : -1;
                }

                switch (state.resultSort) {
                    case 'health':
                        return peerHealth(b).score - peerHealth(a).score;
                    case 'queue':
                        return (Number(a.peerQueueLength) || 0) - (Number(b.peerQueueLength) || 0);
                    case 'size':
                        return (Number(b.size) || 0) - (Number(a.size) || 0);
                    case 'duration':
                        return (Number(b.durationSeconds) || 0) - (Number(a.durationSeconds) || 0);
                    default:
                        return (Number(b.score) || 0) - (Number(a.score) || 0);
                }
            });
    }

    /**
     * One peer's copy of one release, folded out of the candidate files.
     *
     * <p>
     *     A Soulseek release is a folder, and a search for an album matches many files inside the same
     *     folder. Rendering one row per matched file therefore listed the same album eleven times, each row
     *     separately expandable into a drawer showing that same album. A real search measured against this
     *     app produced 916 candidate rows that fold into 89 peer-folders, so nine rows in ten were a
     *     different track of something already listed above them.
     *     <br /><br />
     *     The grouping key is the one the drawer and the panel already use, <c>releaseKey</c>: peer plus exact
     *     remote directory. Two candidates from the same peer and folder are the same release by definition,
     *     so no new notion of identity is introduced here.
     * </p>
     */
    function releaseEntries(code) {
        const entries = new Map();
        for (const candidate of candidatesFor(code)) {
            const folder = folderOf(candidate.filename);
            const key = releaseKey(candidate.username, folder);
            let entry = entries.get(key);
            if (!entry) {
                entry = {
                    key,

                    // The row's own identity is its release key, not the id of whichever candidate happened to
                    // arrive first. A candidate id is "peer + one file's path", so a row built from it could not
                    // be found again from any of its other files, which is how the drawer ended up looking for
                    // a row under an id that named a single track.
                    id: key,
                    username: candidate.username,
                    remoteDirectory: folder,
                    folderName: fileNameOf(folder),
                    // A Soulseek release is a folder, so the folder names it. A parsed album is
                    // not used as the release name: it is derived per file, and for "10 - Exchange.flac"
                    // the parser yields "Exchange", so trusting it named the whole album after one of its
                    // own tracks. The parsed album is still carried, for the drawer to use per file.
                    album: '',
                    candidates: []
                };
                entries.set(key, entry);
            }

            // A parsed album on any file in the folder is the release's name; the first one seen wins.
            entry.candidates.push(candidate);
        }

        return Array.from(entries.values())
            .map((entry) => {
                const sorted = entry.candidates.slice().sort(compareTrackOrder);
                const eligible = sorted.filter((candidate) => candidate.accepted !== false);
                const totalSize = eligible.reduce((sum, candidate) => sum + (Number(candidate.size) || 0), 0);
                const bestScore = sorted.reduce((best, candidate) => Math.max(best, Number(candidate.score) || 0), 0);
                const online = sorted.some((candidate) => candidate.online === true);
                const knownDuration = eligible.reduce((sum, candidate) => sum + (Number(candidate.durationSeconds) || 0), 0);

                // The peer's own facts, as slskd reports them for a search response. Taken from the candidate
                // the row leads with, because they describe the peer answering the search rather than the
                // folder: every candidate here came from the same peer's one reply.
                const lead = sorted[0] || {};

                return {
                    ...entry,

                    files: sorted,
                    eligible,
                    totalSize,
                    bestScore,
                    online,
                    knownDuration,
                    // The files a peer folder holds that the search matched. The album's own track count is
                    // not knowable without reading the directory, so this reports what is actually known:
                    // how many of this peer's matched files the download would take.
                    matched: sorted.length,
                    eligibleCount: eligible.length,
                    peerUploadSpeed: Number(lead.peerUploadSpeed) || 0,
                    peerQueueLength: Number(lead.peerQueueLength) || 0,
                    peerHasFreeUploadSlot: lead.peerHasFreeUploadSlot === true
                };
            })
            .sort((left, right) => {
                // A complete copy of the release is a better answer than a partial one, so completeness
                // leads and match breaks the tie. Peers offering the same album therefore sit together,
                // which is the comparison the reader is actually making.
                if (left.eligibleCount !== right.eligibleCount) {
                    return right.eligibleCount - left.eligibleCount;
                }
                return right.bestScore - left.bestScore;
            });
    }

    function groupRows(code) {
        const rows = releaseEntries(code);
        // A long tail of peers is noise, so a group opens at three and expands on request.
        return state.expandedGroups.has(code) ? rows : rows.slice(0, 3);
    }

    function renderGroups() {
        const host = byId('soulseek-groups');
        const tabs = byId('soulseek-qtabs');
        if (!host) {
            return;
        }

        // Exactly the groups the server sent, in the server's order, and no others. A group the server did
        // not send is a rung the download will not walk, so the tab must not offer it.
        const configured = state.groups
            .map((group) => ({ code: group.code, label: group.label, rows: candidatesFor(group.code) }))
            .filter((group) => group.rows.length > 0);

        if (tabs) {
            const total = configured.reduce((sum, group) => sum + group.rows.length, 0);
            tabs.innerHTML = [`<button type="button" class="soulseek-qtab${state.activeGroup === null ? ' is-active' : ''}" data-soulseek-group="">All Results <span class="soulseek-qtab-n">${escapeHtml(String(total))}</span></button>`]
                .concat(configured.map((group) =>
                    `<button type="button" class="soulseek-qtab${state.activeGroup === group.code ? ' is-active' : ''}" data-soulseek-group="${escapeHtml(group.code)}">${escapeHtml(group.label)} <span class="soulseek-qtab-n">${escapeHtml(String(group.rows.length))}</span></button>`))
                .join('');
        }

        const visible = state.activeGroup === null
            ? configured
            : configured.filter((group) => group.code === state.activeGroup);

        if (visible.length === 0) {
            host.innerHTML = '';
            const counter = byId('soulseek-result-count');
            if (counter) {
                counter.textContent = '0 results';
            }
            return;
        }

        host.innerHTML = visible.map(renderGroup).join('');
        syncDrawerIndeterminate();

        const counter = byId('soulseek-result-count');
        if (counter) {
            const shown = visible.reduce((sum, group) => sum + group.rows.length, 0);
            counter.textContent = `${shown} peer release${shown === 1 ? '' : 's'} in ${visible.length === 1 ? 'this group' : 'these groups'}`;
        }
    }

    function renderGroup(group) {
        const rows = groupRows(group.code);
        for (const row of rows) {
            releaseRowById.set(row.id, row);
        }

        const total = group.rows.length;
        const best = group.rows.length > 0 ? Math.max(...group.rows.map((row) => Number(row.score) || 0)) : 0;
        const hidden = Math.max(0, total - rows.length);

        const more = hidden > 0
            ? `<div class="soulseek-more"><button type="button" data-soulseek-expand="${escapeHtml(group.code)}">Show ${escapeHtml(String(hidden))} more release${hidden === 1 ? '' : 's'}</button></div>`
            : '';

        return `
            <div class="soulseek-group" data-band="${escapeHtml(bandForCode(group.code))}" data-soulseek-group-body="${escapeHtml(group.code)}">
                <div class="soulseek-group-head">
                    <span class="soulseek-group-tier">${escapeHtml(group.label)}</span>
                    <span class="soulseek-group-src">${escapeHtml(String(total))} peer release${total === 1 ? '' : 's'}</span>
                    <div class="soulseek-spacer"></div>
                    <span class="soulseek-group-best">Best match <b>${escapeHtml(`${(best * 100).toFixed(0)}%`)}</b></span>
                </div>
                <table class="soulseek-table">
                    <thead>
                        <tr>
                            <th class="soulseek-cell-rank">#</th>
                            <th class="soulseek-cell-user">Peer / User</th>
                            <th class="soulseek-cell-release">Album</th>
                            <th class="soulseek-cell-coverage">Files</th>
                            <th class="soulseek-cell-num">Size</th>
                            <th class="soulseek-cell-match">Match</th>
                            <th class="soulseek-cell-browse"><span class="soulseek-visually-hidden">Browse</span></th>
                        </tr>
                    </thead>
                    <tbody>${rows.map((row, index) => resultRow(row, index) + renderAlbumDrawer(row)).join('')}</tbody>
                </table>
                ${more}
            </div>`;
    }

    /**
     * Renders whichever view of the result set the panel is showing.
     *
     * <p>
     *     The switcher and the card are rendered together because the Queue button and the rank chip describe
     *     the focused candidate, which differs per view: the best match, the best copy of the album being
     *     looked at, or the file the user picked.
     * </p>
     */
    function renderPanel() {
        const host = byId('soulseek-best');
        if (!host) {
            return;
        }

        const ranked = acceptedRanking();
        if (state.panelView === 'best') {
            // The card and the table highlight are the same file until the user picks another, so the default
            // view never leaves the table pointing somewhere the panel is not.
            state.selectedId = ranked.length > 0 ? ranked[0].id : null;
        }

        renderViewSwitcher();
        renderSelectionSummary();
        if (state.panelView === 'album') {
            renderAlbumView(host);
            return;
        }

        renderBest();
    }

    /**
     * The candidates the download ladder could actually take, best first.
     */
    function acceptedRanking() {
        return Array.from(state.byId.values())
            .filter((candidate) => candidate.accepted !== false && isCandidateEnabled(candidate))
            .sort((a, b) => (Number(b.score) || 0) - (Number(a.score) || 0));
    }

    /**
     * The candidate the panel is acting on, which is the file the Queue button would queue.
     */
    function focusedCandidate() {
        const ranked = acceptedRanking();
        if (state.panelView === 'selected') {
            // The file the user is looking at, even when it was passed over. Refusing to describe a rejected
            // candidate is what made clicking one appear to do nothing.
            const chosen = state.byId.get(state.selectedId);
            return chosen && isCandidateEnabled(chosen) ? chosen : ranked[0] || null;
        }

        if (state.panelView === 'album') {
            return albumTracks()[0] || ranked[0] || null;
        }

        return ranked[0] || null;
    }

    function renderViewSwitcher() {
        const host = byId('soulseek-views');
        if (!host) {
            return;
        }

        host.innerHTML = PANEL_VIEWS.map((view) =>
            `<button type="button" class="soulseek-view-btn${state.panelView === view.code ? ' is-active' : ''}" data-soulseek-view="${escapeHtml(view.code)}">${escapeHtml(view.label)}</button>`).join('');
    }

    /**
     * Points the rank chip and the Queue button at the candidate a view is focused on.
     */
    function updatePanelActions(selected, ranked) {
        const queueButton = byId('soulseek-best-queue');
        const rankChip = byId('soulseek-best-rank');
        const batchFiles = selectedAlbumFiles();

        if (rankChip) {
            const rank = selected ? ranked.indexOf(selected) + 1 : 0;
            rankChip.hidden = rank <= 0;
            setText('soulseek-best-rank', rank > 0 ? `RANK #${rank}` : '');
        }

        if (queueButton) {
            // A rejected candidate is not offerable, so the action is withheld rather than left to fail. A
            // file also has nowhere to go until the reader has chosen a destination folder, so the two are
            // required together rather than one of them.
            const offerable = Boolean(selected) && selected.accepted !== false;
            const destinationChosen = Number(state.destinationFolderId) > 0;
            const batchSidecars = selectedReleases().reduce((sum, release) => sum + (release.sidecars || []).length, 0);
            const batchSelected = batchFiles.length > 0 || batchSidecars > 0;
            queueButton.disabled = state.batchBusy || !destinationChosen || (!batchSelected && !offerable);
            queueButton.dataset.soulseekQueue = !batchSelected && offerable ? encodeURIComponent(selected.id) : '';
            // Sidecars are not download items, so the count stays tracks and a ticked cover or lyrics file is
            // called out rather than inflating it.
            queueButton.textContent = batchFiles.length > 0
                ? `Queue ${batchFiles.length} Download${batchFiles.length === 1 ? '' : 's'}`
                : batchSidecars > 0 ? `Take ${batchSidecars} file${batchSidecars === 1 ? '' : 's'}` : 'Queue Download';
            queueButton.title = batchSidecars > 0 && batchFiles.length > 0
                ? `Also takes ${batchSidecars} peer file${batchSidecars === 1 ? '' : 's'}.`
                : '';
            queueButton.title = (batchSelected || offerable) && !destinationChosen
                ? 'Choose where this file should go first.'
                : '';
        }
    }

    function renderSelectionSummary() {
        const releases = selectedReleases();
        const files = releases.flatMap((release) => release.files);
        const sidecars = releases.flatMap((release) => release.sidecars || []);
        if (files.length === 0 && sidecars.length === 0) {
            setText('soulseek-selection-summary', '');
        } else {
            const totalSize = files.reduce((sum, file) => sum + (Number(file.size) || 0), 0);
            const knownDuration = files.reduce((sum, file) => sum + (Number(file.durationSeconds) || 0), 0);
            const qualities = new Set(files.map((file) => String(file.qualityCode || file.qualityLabel || 'UNKNOWN')));
            // Peers are named rather than assumed to be one, because the selection may span releases from
            // different peers and reporting only the open drawer hid every pick but the last.
            const peers = Array.from(new Set(releases.map((release) => String(release.username || '')).filter(Boolean)));
            const peerText = peers.length === 0
                ? ''
                : peers.length === 1 ? ` · ${peers[0]}` : ` · ${peers.length} peers`;
            const quality = qualities.size === 1 ? Array.from(qualities)[0] : 'Mixed permitted qualities';
            const duration = knownDuration > 0 ? ` · ${formatDuration(knownDuration)}` : '';
            const releaseText = releases.length > 1 ? ` · ${releases.length} releases` : '';
            const trackText = files.length > 0
                ? `${selectionCountPhrase(files, sidecars)} selected · ${formatBytes(totalSize)}${duration} · ${quality}`
                : `${sidecars.length} file${sidecars.length === 1 ? '' : 's'} selected`;
            setText('soulseek-selection-summary', `${trackText}${releaseText}${peerText}`);
        }

        const outcome = state.batchOutcome;
        setText('soulseek-batch-outcome', outcome
            ? `${outcome.queued} queued · ${outcome.skipped} skipped · ${outcome.failed} failed${outcome.message ? ` · ${outcome.message}` : ''}`
            : '');
    }

    /**
     * The stereo folders a file may land in.
     *
     * <p>
     *     These are the app's own destinations, not paths: the folder list is already filtered to the ones
     *     enabled for downloads and to stereo content, which is all that exists so far. An empty list is
     *     stated rather than left as a dead control, because a reader who cannot queue anything deserves to
     *     know it is a folder problem and not a Soulseek one.
     * </p>
     */
    async function loadDestinations() {
        if (state.destinationsLoaded) {
            return;
        }

        state.destinationsLoaded = true;
        try {
            // The folder list belongs to the library API, not to this engine's base path, so it is fetched
            // directly. Going through request() would prefix /api/v1/soulseek and come back as a 404, which
            // reads as an empty list - exactly what "you have no folders" looks like.
            const response = await global.fetch(
                '/api/library/folders?downloadOnly=true&contentType=stereo',
                { credentials: 'same-origin' });
            const folders = response.ok ? await response.json() : null;
            state.destinations = Array.isArray(folders) ? folders : [];
        } catch (error) {
            state.destinations = [];
        }

        renderDestinations();
    }

    function renderDestinations() {
        const host = byId('soulseek-destination');
        if (!host) {
            return;
        }

        const hint = byId('soulseek-destination-hint');
        if (state.destinations.length === 0) {
            host.innerHTML = '<option value="">No destination folder is available</option>';
            host.disabled = true;
            if (hint) {
                setText('soulseek-destination-hint', 'Add an enabled stereo folder in Settings \u203a Folders to queue a Soulseek download.');
            }
            return;
        }

        // A folder that has since been removed from the selection must not stay on the item.
        if (Number(state.destinationFolderId) > 0
            && !state.destinations.some((folder) => Number(folder.id) === Number(state.destinationFolderId))) {
            state.destinationFolderId = null;
        }

        if (!state.destinationFolderId && state.destinations.length === 1) {
            state.destinationFolderId = Number(state.destinations[0].id) || null;
        }

        host.disabled = false;
        host.innerHTML = ['<option value="">Choose a destination folder\u2026</option>']
            .concat(state.destinations.map((folder) =>
                `<option value="${escapeHtml(String(folder.id))}"${Number(state.destinationFolderId) === Number(folder.id) ? ' selected' : ''}>${escapeHtml(String(folder.displayName || `Folder ${folder.id}`))}</option>`))
            .join('');

        if (hint) {
            const chosen = state.destinations.find((folder) => Number(folder.id) === Number(state.destinationFolderId));
            setText(
                'soulseek-destination-hint',
                chosen
                    ? `Downloads will use ${String(chosen.displayName || `Folder ${chosen.id}`)}.`
                    : 'A Soulseek file is not a library track until you say where it goes.');
        }
    }

    /**
     * Describes the selected candidate.
     *
     * <p>
     *     This is a candidate report, not a transfer. The panel deliberately shows no progress, because the
     *     Downloads tab already owns transfer and import state for every engine including this one.
     * </p>
     */
    function renderBest() {
        const host = byId('soulseek-best');
        if (!host) {
            return;
        }

        const ranked = acceptedRanking();
        const selected = focusedCandidate();
        updatePanelActions(selected, ranked);

        if (!selected) {
            host.innerHTML = '<p class="soulseek-empty-side">No candidate selected yet.</p>';
            return;
        }

        // Text values are passed to bestRow raw and escaped there, exactly once. Only the rows that are
        // genuinely markup (the chip, the availability dot, the health bar) opt into isHtml.
        const title = String(selected.title || state.title || '');
        const artist = String(selected.artist || state.artist || '');
        const peer = String(selected.username || '');
        // The panel row is labelled Filename, so it is given the file's name. The peer's full path is on
        // the row in the table, as the tooltip.
        const filename = fileNameOf(String(selected.filename || ''));
        const format = String(selected.qualityLabel || bandLabel(selected.quality));
        const chip = bandForCode(selected.quality) === 'hi-res' || bandForCode(selected.quality) === 'hi-res-lossless'
            ? '<span class="soulseek-chip">HI-RES</span>'
            : '';
        const size = formatBytes(selected.size);
        const duration = formatDuration(selected.durationSeconds);
        const queueLength = Number(selected.peerQueueLength);
        const queue = Number.isFinite(queueLength) ? String(queueLength) : '';
        const free = selected.peerHasFreeUploadSlot === true;
        const online = selected.online === true;
        const health = peerHealth(selected);

        // The name is parsed out of a peer's filename, so it is escaped here like every other peer field.
        const rejected = selected.accepted === false
            ? bestRow('Not offered', String(selected.rejectedBecause || 'rejected'), false, true)
            : '';

        host.innerHTML = `
            <div class="soulseek-best-head-row">
                ${coverImage({
                    url: state.coverUrl,
                    alt: coverAltText(selected.album || title, artist || state.artist),
                    className: 'soulseek-cover soulseek-cover--panel'
                })}
                <div class="soulseek-best-heading">
                    <h4 class="soulseek-best-title">${escapeHtml(title)}</h4>
                    <div class="soulseek-best-artist">${escapeHtml(artist)}</div>
                </div>
            </div>
            <div class="soulseek-best-rows">
                ${bestRow('Audio format', `${escapeHtml(format)}${chip}`, true)}
                ${bestRow('File size', size)}
                ${bestRow('Duration', duration)}
                ${bestRow('Source / User', peer)}
                ${bestRow('Availability', online ? '<span style="color:var(--success-color)">\u25cf Online now</span>' : '<span class="soulseek-dim">\u25cb Unknown</span>', true)}
                ${bestRow('Queue length', queue === '' ? '' : `${escapeHtml(queue)}${free ? ' &mdash; free slot' : ''}`, true)}
                ${bestRow('Source health', health.html, true)}
                ${bestRow('Filename', filename, false, true)}
                ${rejected}
            </div>`;
    }

    /**
     * The folder a file sits in, as the peer shared it.
     *
     * <p>
     *     A Soulseek release is a folder, and the folder is the album far more often than the file's name says
     *     so. When the name reveals no album, the folder is what the album is.
     * </p>
     */
    function folderOf(path) {
        const value = String(path || '');
        const cut = Math.max(value.lastIndexOf('/'), value.lastIndexOf('\\'));
        return cut > 0 ? value.slice(0, cut) : '';
    }

    function normalizeRemotePath(path) {
        return String(path || '').trim().replace(/\\/g, '/').split('/').filter(Boolean).join('/');
    }

    function releaseKey(username, remoteDirectory) {
        return `${String(username || '').trim().toLowerCase()}\u0000${normalizeRemotePath(remoteDirectory).toLowerCase()}`;
    }

    /** The result row the exact-peer release drawer is centred on. */
    function albumAnchor() {
        return state.byId.get(state.selectedId) || acceptedRanking()[0] || null;
    }

    /** Search candidates from the same peer and exact remote directory as the anchor. */
    function albumTracks() {
        const anchor = albumAnchor();
        if (!anchor) return [];
        const key = releaseKey(anchor.username, folderOf(anchor.filename));
        return Array.from(state.byId.values())
            .filter((candidate) => releaseKey(candidate.username, folderOf(candidate.filename)) === key)
            .sort(compareTrackOrder);
    }

    function compareTrackOrder(left, right) {
        const leftTrack = Number(left.trackNumber);
        const rightTrack = Number(right.trackNumber);
        if (Number.isFinite(leftTrack) && Number.isFinite(rightTrack) && leftTrack !== rightTrack) {
            return leftTrack - rightTrack;
        }
        if (Number.isFinite(leftTrack) !== Number.isFinite(rightTrack)) {
            return Number.isFinite(leftTrack) ? -1 : 1;
        }
        return String(left.displayFilename || left.filename || '').localeCompare(String(right.displayFilename || right.filename || ''));
    }

    function currentSelection() {
        const drawer = state.albumDrawer;
        if (!drawer) return new Set();
        const key = releaseKey(drawer.username, drawer.remoteDirectory);
        if (!state.albumSelections.has(key)) {
            state.albumSelections.set(key, new Set());
        }
        return state.albumSelections.get(key);
    }

    /**
     * Every release that currently holds a selection, with the files it holds.
     *
     * <p>
     *     This is what the row checkbox writes to and what the panel's total reads from. It spans every
     *     release rather than only the open drawer, because the reader's decision spans every release: they
     *     may tick two albums from two peers and queue them together. Reading only
     *     <c>state.albumDrawer</c> made the earlier picks invisible and unqueueable the moment another
     *     folder was opened, which is verified in the guardrail test.
     *     <br /><br />
     *     A release whose directory has not been read yet contributes nothing, because choosing which
     *     tracks to take is a decision made where the tracks are listed. The count therefore never claims
     *     files the app has not seen.
     * </p>
     */
    function selectedReleases() {
        const found = [];
        for (const [key, ids] of state.albumSelections.entries()) {
            if (!ids || ids.size === 0) {
                continue;
            }

            // The drawer's own listing is authoritative for this release while it is loaded, because it
            // carries the eligibility and rejection reasons the search response does not.
            const drawer = state.albumDrawer
                && releaseKey(state.albumDrawer.username, state.albumDrawer.remoteDirectory) === key
                && !state.albumDrawer.loading
                && !state.albumDrawer.error
                ? state.albumDrawer
                : null;

            if (drawer) {
                // Anything the reader ticked, not only what the ladder preferred: a ticked track is a manual
                // pick whichever quality it happens to be. Tracks and sidecars are split on the same
                // sidecarFetchable test the drawer renders from, because a ticked cover image counted as a
                // track made the summary report "12 selected" for a folder of eleven tracks and one picture.
                const ticked = drawer.files.filter((file) => !fileIsUnavailable(file) && ids.has(String(file.id)));
                const files = ticked.filter((file) => file.sidecarFetchable !== true);
                const sidecarFiles = ticked.filter((file) => file.sidecarFetchable === true);
                if (ticked.length > 0) {
                    found.push({ key, username: drawer.username, remoteDirectory: drawer.remoteDirectory, files, sidecars: sidecarFiles });
                }
                continue;
            }

            // Otherwise the search candidates carry the same identity, and a candidate the download would
            // refuse is never counted as selected.
            const candidates = Array.from(state.byId.values())
                .filter((candidate) => releaseKey(candidate.username, folderOf(candidate.filename)) === key)
                .filter((candidate) => candidate.accepted !== false)
                .filter((candidate) => ids.has(String(candidate.id)))
                .sort(compareTrackOrder);
            if (candidates.length > 0) {
                const first = candidates[0];
                found.push({
                    key,
                    username: first.username,
                    remoteDirectory: folderOf(first.filename),
                    files: candidates,
                    sidecars: []
                });
            }
        }

        return found;
    }

    /** The files queued by every release with a selection, across every release. */
    function selectedAlbumFiles() {
        return selectedReleases().flatMap((release) => release.files);
    }

    /** The sidecars ticked alongside the tracks, across every release with a selection. */
    function selectedSidecarFiles() {
        return selectedReleases().flatMap((release) => release.sidecars || []);
    }

    /**
     * What one release row reports about its own selection: how many files are ticked and whether the row
     * checkbox should read as all, some or none.
     */
    function releaseSelection(entry) {
        const ids = state.albumSelections.get(entry.key);
        const selected = ids ? ids.size : 0;

        // The denominator is everything the reader could take, not just the tracks the ladder prefers. A
        // folder whose cover art is ticked held 20 ids against 19 eligible files, and the row reported
        // "20 of 19 selected" - a count that cannot be true and that made a correct selection look wrong.
        // The search response carries no sidecars, so the drawer's listing is the only place the real total
        // exists; until it is read, the eligible count is all that is honestly known.
        const drawer = state.albumDrawer
            && releaseKey(state.albumDrawer.username, state.albumDrawer.remoteDirectory) === entry.key
            && !state.albumDrawer.loading
            && !state.albumDrawer.error
            ? state.albumDrawer
            : null;
        const takeable = drawer
            ? drawer.files.filter((file) => !fileIsUnavailable(file)).length
            : entry.eligibleCount;
        const listed = drawer ? drawer.files.length : entry.matched;

        return {
            selected,
            eligibleCount: entry.eligibleCount,
            takeableCount: takeable,

            // Whether the folder has actually been read, and therefore which of two different numbers the row
            // is entitled to print. These are not the same measurement and reporting one under the other's
            // label is what made a row claim the download could take 1 of 11 files while the drawer it had
            // just been expanded from listed all eleven as eligible.
            //
            //   'search' - only the search results are known. eligibleCount counts candidates the query
            //              matcher accepted, so it measures how much of the folder matched what was searched
            //              for. A peer who named the tracks "01 - Angel.flac" matched one file out of eleven,
            //              because only "09 - Mezzanine.flac" contains a word of the query. Nothing about
            //              those ten is untakeable.
            //   'browse' - the folder has been read, so every figure here comes from the peer's own listing
            //              and counts files rather than query matches.
            source: drawer ? 'browse' : 'search',
            listed,
            state: selected === 0 ? 'none' : (takeable > 0 && selected >= takeable ? 'all' : 'some')
        };
    }

    /**
     * The non-audio files the reader ticked alongside the tracks.
 *
 * <p>
 *     Held apart from the tracks deliberately: a cover image is not a download, and sending it through
 *     the audio queue request would have the planner treat it as a track that cannot be played.
 * </p>
 */
    function toggleAllEligible(checked) {
        const drawer = state.albumDrawer;
        if (!drawer) return;
        const selection = currentSelection();
        for (const file of drawer.files) {
            // Everything the reader could take, not only what the ladder prefers. "Select all" that silently
            // skipped half the folder left the reader to tick those by hand with no way to tell which they had
            // already chosen.
            if (fileIsUnavailable(file)) continue;
            if (checked) selection.add(String(file.id));
            else selection.delete(String(file.id));
        }
        state.batchOutcome = null;
        renderGroups();
        renderPanel();
    }

    function toggleAlbumFile(fileId, checked) {
        const drawer = state.albumDrawer;
        const file = drawer && drawer.files.find((entry) => String(entry.id) === String(fileId));
        // A real file the ladder merely passed over is selectable like any other: the reader ticked it, which
        // is the app's whole model of a manual pick. Only a blocked peer, a blocked filename pattern, a file
        // the peer has locked and a non-audio file that is not an opted-in sidecar are refused.
        if (!file || fileIsUnavailable(file)) return;
        const selection = currentSelection();
        if (checked) selection.add(String(file.id));
        else selection.delete(String(file.id));
        state.batchOutcome = null;
        renderGroups();
        renderPanel();
    }

    function syncDrawerIndeterminate() {
        const input = byId('soulseek-album-select-all');
        const drawer = state.albumDrawer;
        if (!input || !drawer) return;
        // Checked state is measured against everything takeable, because that is what the label next to it
        // counts. Measuring only eligible tracks made the box read "all selected" while a ticked cover image
        // sat outside the tally it claimed to cover.
        const takeable = drawer.files.filter((file) => !fileIsUnavailable(file));
        const selection = currentSelection();
        const selected = takeable.filter((file) => selection.has(String(file.id))).length;
        input.checked = takeable.length > 0 && selected === takeable.length;
        input.indeterminate = selected > 0 && selected < takeable.length;
    }

    function renderAlbumView(host) {
        const anchor = albumAnchor();
        updatePanelActions(anchor, acceptedRanking());
        if (!anchor) {
            host.innerHTML = '<p class="soulseek-empty-side">No album to show yet.</p>';
            return;
        }

        const folder = folderOf(anchor.filename);
        const drawer = state.albumDrawer;
        const sameRelease = drawer && releaseKey(drawer.username, drawer.remoteDirectory) === releaseKey(anchor.username, folder);
        const count = sameRelease && !drawer.loading ? drawer.files.length : albumTracks().length;
        host.innerHTML = `
            <h4 class="soulseek-best-title">${escapeHtml(String(anchor.album || fileNameOf(folder) || 'Unnamed release'))}</h4>
            <div class="soulseek-best-artist">${escapeHtml(String(anchor.artist || 'Unknown artist'))}</div>
            <p class="soulseek-hint">${escapeHtml(String(count))} file${count === 1 ? '' : 's'} from ${escapeHtml(String(anchor.username || ''))}. Open the row to read this exact peer folder and choose from it.</p>`;
    }

    function albumQualityMix(files) {
        const counts = new Map();
        for (const file of files) {
            const label = String(file.qualityLabel || file.qualityCode || 'Unknown quality');
            counts.set(label, (counts.get(label) || 0) + 1);
        }
        return Array.from(counts.entries())
            .map(([label, count]) => `${escapeHtml(label)} (${escapeHtml(String(count))})`)
            .join(' · ');
    }

    function rejectionLabel(code) {
        return String(code || 'not eligible').replace(/_/g, ' ');
    }

    /**
     * The drawer's own chrome: the region the row's expansion control points at, and the tools that
     * collapse it again.
     *
     * <p>
     *     The id is the one `aria-controls` names, so the control always refers to a region that exists in
     *     every state the drawer can be in - loading, failed, empty and listed alike. Collapsing is offered
     *     in the failure state too, because a peer that will not answer must not leave the reader stuck
     *     looking at an error they cannot dismiss.
     * </p>
     */
    function albumDrawerShell(anchor, body) {
        const peer = escapeHtml(state.albumDrawer.username);
        const folderName = escapeHtml(fileNameOf(state.albumDrawer.remoteDirectory) || 'Unnamed release');

        return `<tr class="soulseek-album-drawer-row"><td colspan="7" class="soulseek-album-drawer-cell">
                    <section class="soulseek-album-drawer" id="${escapeHtml(drawerDomId(anchor.id))}" aria-label="Files in ${folderName} from ${peer}">
                        ${body}
                    </section>
                </td></tr>`;
    }

    /**
     * A peer's upload speed, in the unit slskd's own UI uses.
     *
     * <p>
     *     slskd reports a bare number of bytes per second and shows a human rate beside it, because a reader
     *     choosing between peers wants to know which one will finish first and "1048576" does not say that.
     *     The same reading is offered here so the two screens agree.
     * </p>
     */
    function formatUploadSpeed(bytesPerSecond) {
        const value = Number(bytesPerSecond) || 0;
        if (value <= 0) {
            return '';
        }

        if (value >= 1_048_576) {
            return `${(value / 1_048_576).toFixed(value >= 10_485_760 ? 0 : 1)} MB/s`;
        }

        return `${Math.round(value / 1024)} KB/s`;
    }

    /**
     * The bit depth and sample rate of the folder's audio, the way a quality badge states them.
     *
     * <p>
     *     Taken from the first file that reports both, because peers disagree per file and the folder is
     *     described by the best evidence in it rather than by whichever file happened to sort first. Returns
     *     nothing when no file carries them, which is most folders: slskd reports a bitrate for a search but
     *     not a depth or a rate for a browse listing.
     * </p>
     */
    /**
     * The part of a quality label worth putting on a badge.
     *
     * <p>
     *     slskd's own label is the code plus the code again plus the specification: "FLAC_HI_RES (24-bit/96kHz)".
     *     That is fine in a tooltip and too long for a badge beside "11/11 tracks", and printing the depth and
     *     rate beside it as well made the same two numbers appear twice in two shapes. The badge takes the name
     *     and the specification stays on the tooltip, so nothing is lost and nothing is printed twice.
     * </p>
     */
    function shortQualityLabel(label) {
        return String(label || '').replace(/[_\s]+/g, ' ').trim();
    }

    function folderAudioSpec(files) {
        for (const file of files) {
            const depth = Number(file.bitDepth) || 0;
            const rate = Number(file.sampleRateHz) || 0;
            if (depth > 0 && rate > 0) {
                return `${depth}-bit / ${(rate / 1000).toFixed(rate % 1000 === 0 ? 0 : 1)}kHz`;
            }
        }

        return '';
    }

    /** The year a folder name states, when it states one. Never guessed from anything else. */
    function folderYear(folderName) {
        const match = /\b(19[5-9]\d|20[0-4]\d)\b/.exec(String(folderName || ''));
        return match ? match[1] : '';
    }

    function renderAlbumDrawer(anchor) {
        const drawer = state.albumDrawer;

        // The row the drawer hangs from. It is where the peer's own facts live, because a directory listing
        // carries none: slskd reports upload speed and queue length with a search response, not a listing.
        const row = releaseRowById.get(String(anchor.id)) || {};
        const entryPeerSpeed = row.peerUploadSpeed || 0;
        const entryQueue = row.peerQueueLength || 0;
        const entryOnline = row.online === true;
        if (!drawer || !drawer.expanded) return '';
        if (!isDrawerOpenFor(anchor)) return '';
        const peer = escapeHtml(drawer.username);
        const folderName = escapeHtml(fileNameOf(drawer.remoteDirectory) || 'Unnamed release');

        if (drawer.loading) {
            // The listing appears under the exact row immediately, before the peer has answered, so the
            // control that was clicked visibly did something.
            return albumDrawerShell(anchor,
                `<div class="soulseek-album-drawer-head">
                    <div class="soulseek-album-drawer-identity">
                        <div class="soulseek-album-drawer-heading">
                            <h4 class="soulseek-album-drawer-title">${folderName}</h4>
                            <div class="soulseek-album-drawer-summary">${peer}</div>
                        </div>
                    </div>
                </div>
                <div class="soulseek-album-drawer-message" role="status">Reading ${folderName} from ${peer}&hellip;</div>`);
        }

        if (drawer.error) {
            // A peer that will not answer is a reason to try again, not a reason to lose the search. The
            // results stay exactly as they are and only this region changes.
            // A folder the peer does not publish is not a transient failure, so offering Retry on it is an
            // invitation to press a button that cannot succeed.
            return albumDrawerShell(anchor,
                `<div class="soulseek-album-drawer-head">
                    <div class="soulseek-album-drawer-identity">
                        <div class="soulseek-album-drawer-heading">
                            <h4 class="soulseek-album-drawer-title">${folderName}</h4>
                            <div class="soulseek-album-drawer-summary">${peer}</div>
                        </div>
                    </div>
                    ${drawer.errorRetryable
                        ? `<div class="soulseek-album-drawer-tools">
                            <button type="button" class="action-btn action-btn-sm" data-soulseek-drawer-retry="${escapeHtml(encodeURIComponent(anchor.id))}">Retry</button>
                        </div>`
                        : ''}
                </div>
                <div class="soulseek-album-drawer-message soulseek-album-drawer-message--error" role="alert">${escapeHtml(drawer.error)}</div>`);
        }

        const files = drawer.files.slice().sort(compareTrackOrder);
        const selection = currentSelection();
        const eligible = files.filter((file) => file.eligible);

        // A peer folder is not just the tracks. A cover image or a cue sheet sitting next to the audio is
        // often the only artwork or lyrics that will ever exist for a release no streaming service carries,
        // so a sidecar the reader has opted in to is offered as its own selection rather than being hidden
        // among the rejected files.
        const sidecars = files.filter((file) => file.sidecarFetchable === true);
        const chosenSidecars = sidecars.filter((file) => selection.has(String(file.id)));
        const nonAudio = files.filter((file) => file.rejectedBecause === 'non_audio_file'
            && file.sidecarFetchable !== true).length;
        const totalSize = eligible.reduce((sum, file) => sum + (Number(file.size) || 0), 0);
        const knownDuration = eligible.reduce((sum, file) => sum + (Number(file.durationSeconds) || 0), 0);
        const totalDuration = knownDuration > 0 ? formatDuration(knownDuration) : '';

        // Audio first, then the sidecars. Sorting the whole list by track number would interleave them, and a
        // cover image numbered 1 would read as if it were a track. The row number counts down the listing as
        // it is shown rather than repeating the peer's own track number: peers name files as "1-02 - X",
        // "1-03 - Y", which the parser reads as track 1 for every one of them, so a column of 1s said
        // nothing. The peer's own numbering is on the raw filename line beneath each title.
        const audioFiles = files.filter((file) => file.sidecarFetchable !== true);

        // What the album counts are the audio files. A cover image and a cue sheet travel with a release and
        // are worth taking, but they are not tracks, and counting them made a complete album read as missing
        // files: a 12-file folder of which 11 were audio showed "11/12 tracks" because the cue sheet was in
        // the denominator.
        const audioEligible = audioFiles.filter((file) => file.eligible === true).length;
        const audioRows = audioFiles.map((file, index) =>
            albumFileRow(file, index + 1, selection, anchor, false)).join('');
        const sidecarRows = sidecars.map((file, index) =>
            albumFileRow(file, audioFiles.length + index + 1, selection, anchor, true)).join('');
        const rows = audioRows + sidecarRows;

        const takeable = files.filter((file) => !fileIsUnavailable(file));
        const taken = takeable.filter((file) => selection.has(String(file.id)));
        const takenTracks = taken.filter((file) => file.sidecarFetchable !== true);
        const takenSidecars = taken.filter((file) => file.sidecarFetchable === true);
        const selectAllLabel = `Select all (${taken.length}/${takeable.length})`;
        const takenSize = takenTracks.reduce((sum, file) => sum + (Number(file.size) || 0), 0);
        const takenSummary = escapeHtml(selectionCountPhrase(takenTracks, takenSidecars));

        // The identity of the release on the left, and the facts a reader chooses a peer on the right.
        // Peer facts come from the search response rather than the listing, because a listing carries no
        // upload speed; the row holds them because it was built from those responses.
        const artist = files.find((file) => String(file.artist || '').trim().length > 0);
        const releaseYear = folderYear(folderName);
        const spec = folderAudioSpec(files);
        const qualityCode = String((files.find((file) => file.eligible === true) || files[0] || {}).qualityCode || '');
        const qualityLabel = String((files.find((file) => file.eligible === true) || files[0] || {}).qualityLabel || '');
        const uploadSpeed = formatUploadSpeed(entryPeerSpeed);
        // Audio files only, for the same reason the track count is: the header describes the album, and a
        // cover image is not a track of it. Sidecars are named separately so nothing is hidden by omission.
        const summaryParts = [
            `${audioFiles.length} track${audioFiles.length === 1 ? '' : 's'}`,
            releaseYear,
            sidecars.length > 0
                ? `+ ${sidecars.length} ${escapeHtml(sidecarSummaryLabel(sidecars).replace(/ files?$/, ''))}`
                : ''
        ].filter(Boolean);

        return albumDrawerShell(anchor,
            `<div class="soulseek-album-drawer-head">
                <div class="soulseek-album-drawer-identity">
                    <div class="soulseek-album-drawer-art">
                        ${coverImage({
                            url: state.coverUrl,
                            alt: coverAltText(drawer.remoteDirectory, state.artist),
                            className: 'soulseek-cover soulseek-cover--drawer'
                        })}
                    </div>
                    <div class="soulseek-album-drawer-heading">
                        ${artist
                            ? `<div class="soulseek-album-drawer-artist">${escapeHtml(String(artist.artist).trim())}</div>`
                            : ''}
                        <h4 class="soulseek-album-drawer-title">${folderName}</h4>
                        <div class="soulseek-album-drawer-summary">${summaryParts.join(' · ')}</div>
                    </div>
                </div>
                <dl class="soulseek-album-facts">
                    <div class="soulseek-album-fact-row">
                        <dt>Source / User</dt>
                        <dd><span class="material-icons soulseek-album-fact-icon" aria-hidden="true">person</span><span>${peer}</span></dd>
                    </div>
                    <div class="soulseek-album-fact-row">
                        <dt>Folder path</dt>
                        <dd><span class="material-icons soulseek-album-fact-icon" aria-hidden="true">folder</span><span class="soulseek-album-path" title="${escapeHtml(drawer.remoteDirectory)}">${escapeHtml(drawer.remoteDirectory)}</span></dd>
                    </div>
                    <div class="soulseek-album-fact-row">
                        <dt>Quality</dt>
                        <dd>
                            ${qualityCode
                                ? `<span class="soulseek-album-quality" title="${escapeHtml(spec ? `${qualityLabel || qualityCode} \u00b7 ${spec}` : (qualityLabel || qualityCode))}">${escapeHtml(shortQualityLabel(qualityLabel || qualityCode))}</span>`
                                : `<span class="soulseek-album-quality soulseek-album-quality--none">No quality reported</span>`}
                            <span class="soulseek-album-fact-tail">${escapeHtml(String(audioEligible))}/${escapeHtml(String(audioFiles))} tracks</span>
                        </dd>
                    </div>
                    <div class="soulseek-album-fact-row">
                        <dt>Availability</dt>
                        <dd>
                            <span class="soulseek-album-status soulseek-album-status--${entryOnline ? 'on' : 'off'}">
                                <span class="soulseek-album-status-dot" aria-hidden="true"></span>${entryOnline ? 'Online' : 'Offline'}
                            </span>
                            <span class="soulseek-album-fact-sep" aria-hidden="true"></span>
                            <span class="soulseek-album-fact-tail">${escapeHtml(entryQueue > 0 ? `Queue ${entryQueue}` : 'Queue clear')}</span>
                        </dd>
                    </div>
                    <div class="soulseek-album-fact-row">
                        <dt>Upload speed</dt>
                        <dd>
                            ${uploadSpeed
                                ? `<span class="soulseek-album-speed"><span class="material-icons soulseek-album-fact-icon" aria-hidden="true">network_upload</span>${escapeHtml(uploadSpeed)}</span>`
                                : `<span class="soulseek-album-speed soulseek-album-speed--unknown">Not reported</span>`}
                        </dd>
                    </div>
                </dl>
            </div>
            <div class="soulseek-album-drawer-statusline">
                <span class="soulseek-album-drawer-selected" role="status" aria-live="polite">${takenSummary} selected · ${escapeHtml(formatBytes(takenSize))}</span>
                <span class="soulseek-album-drawer-size">${escapeHtml(formatBytes(totalSize))}${nonAudio > 0 ? ` · ${escapeHtml(String(nonAudio))} non-audio` : ''}</span>
            </div>
            </div>
            <div class="soulseek-album-file-head" role="row">
                    <span class="soulseek-album-file-n">#</span>
                    <span class="soulseek-album-file-title">Title</span>
                    <span class="soulseek-album-file-size">Size</span>
                    <span class="soulseek-album-file-eligibility">Eligibility</span>
                    <span class="soulseek-album-file-reason">Reason</span>
                    <span class="soulseek-album-check-cell">
                        <label class="soulseek-album-select-all" title="${escapeHtml(selectAllLabel)}">
                            <input type="checkbox" id="soulseek-album-select-all">
                            <span class="soulseek-visually-hidden">${escapeHtml(selectAllLabel)}</span>
                        </label>
                    </span>
                    <span class="soulseek-album-download"><span class="soulseek-visually-hidden">Download</span></span>
                </div>
            ${rows || '<div class="soulseek-album-drawer-message">This peer folder holds nothing.</div>'}
            ${chosenSidecars.length > 0
                ? `<div class="soulseek-album-sidecar-note">${escapeHtml(String(chosenSidecars.length))} peer file(s) will be taken with this album: ${escapeHtml(chosenSidecars.map((file) => file.displayFilename).join(', '))}</div>`
                : ''}`);
    }

    /**
     * One row of the album listing.
     *
     * <p>
     *     A sidecar row is a real, selectable file rather than a rejected one: the reader turned the setting
     *     on, so the server reported it as takeable, and the row says what it is and that it came from the
     *     peer. A file that is neither audio nor a takeable sidecar keeps the disabled control and the
     *     server's own reason, because that is how the reader learns the row was considered at all.
     * </p>
     */
    /**
 * Whether a file in a peer folder can be downloaded at all.
 *
 * <p>
 *     The download ladder refusing a file and a reader being unable to take it are different things. A peer
 *     the reader has blocked, a filename pattern they have blocked, a file the peer has locked and a
 *     non-audio file that is not an opted-in sidecar are all genuinely unavailable, and no amount of asking
 *     will produce them. Everything else &mdash; a quality the ladder is not walking, an unknown quality, a
 *     duplicate of a track already in hand &mdash; is a real audio file sitting in the folder. Those refusals
 *     exist to stop the <em>automatic</em> album pick from choosing badly; a reader who ticks one, or presses
 *     its download button, has said which file they want, and the whole point of this tab is that the reader
 *     chooses. Withholding them turned the folder into a list of what the app would take rather than what is
 *     there.
 * </p>
 */
    function fileIsUnavailable(file) {
        const reason = String(file.rejectedBecause || '');
        return reason === 'blocked_user'
            || reason === 'blocked_filename_pattern'
            || reason === 'file_locked'
            || (reason === 'non_audio_file' && file.sidecarFetchable !== true);
    }

    function albumFileRow(file, position, selection, anchor, isSidecar) {
        const id = String(file.id || '');
        const checked = selection.has(id);
        const rawFilename = String(file.displayFilename || fileNameOf(file.filename));
        const filename = escapeHtml(rawFilename);
        const remotePath = escapeHtml(String(file.filename || ''));
        const reasonId = `${drawerDomId(anchor.id)}-reason-${id.slice(0, 12)}`;
        // A file the reader may take: anything real in the folder, not only what the ladder would pick.
        const selectable = !fileIsUnavailable(file);
        const label = isSidecar
            ? `${sidecarLabel(file.sidecarRole)} the peer shared`
            : `Select ${filename}`;

        // The parsed title leads and the raw filename sits beneath it, but only when the two differ. When
        // the parser returned no title the raw name was the title, and printing it twice read as a
        // duplication rather than as two pieces of information.
        const parsed = String(file.title || '').trim();
        const hasParsedTitle = parsed.length > 0 && parsed !== rawFilename;
        const title = escapeHtml(hasParsedTitle ? parsed : rawFilename);
        const subtitle = hasParsedTitle ? `<small class="soulseek-album-file-raw">${filename}</small>` : '';

        const reason = isSidecar
            ? `<span class="soulseek-album-sidecar-role" id="${escapeHtml(reasonId)}">${escapeHtml(sidecarLabel(file.sidecarRole))}${file.sidecarEnabled === true ? '' : ' &mdash; turn it on in Soulseek settings'}</span>`
            : file.rejectedBecause
                ? `<span class="soulseek-album-reason" id="${escapeHtml(reasonId)}" title="${escapeHtml(rejectionLabel(file.rejectedBecause))}">${escapeHtml(rejectionLabel(file.rejectedBecause))}</span>`
                : '';

        // Eligibility says what the download would do with this file on its own, and it is deliberately not the
        // same thing as "can I take it". A file the ladder passed over is still the reader's to take, so it
        // reads "Not preferred" rather than "Not eligible"; only a genuinely unavailable file says so, and
        // that is the one case where the control is withheld.
        const eligibility = isSidecar
            ? `<span class="soulseek-album-eligibility soulseek-album-eligibility--sidecar">Sidecar</span>`
            : file.eligible === true
                ? `<span class="soulseek-album-eligibility soulseek-album-eligibility--yes">&#9679; Eligible</span>`
                : selectable
                    ? `<span class="soulseek-album-eligibility soulseek-album-eligibility--other">&#9675; Not preferred</span>`
                    : `<span class="soulseek-album-eligibility soulseek-album-eligibility--no">&#9679; Unavailable</span>`;

        const checkbox = `<label class="soulseek-album-check">
                <input type="checkbox" data-soulseek-album-file="${escapeHtml(encodeURIComponent(id))}"
                    aria-label="${escapeHtml(label)}"${reason ? ` aria-describedby="${escapeHtml(reasonId)}"` : ''}${checked ? ' checked' : ''}${selectable ? '' : ' disabled'}>
            </label>`;

        const size = escapeHtml(formatBytes(file.size));

        // The row is the app's own tracklist row, so a peer's folder reads as a tracklist rather than a data
        // grid: the catalogue artwork sits at the left of the title, the number is centred, and the choice and
        // the action are on the right where the app puts them. Format and duration are gone - the format is
        // the group's own label and slskd reports no duration for these results, so both columns were blank
        // on every row while still reserving width.
        const art = state.coverUrl
            ? `<img class="soulseek-album-file-art" src="${escapeHtml(state.coverUrl)}" alt="" loading="lazy">`
            : '<span class="soulseek-album-file-art soulseek-album-file-art--placeholder" aria-hidden="true">&#9834;</span>';

        const download = `<span class="soulseek-album-download">
                <button type="button" class="track-quick-download-btn" data-soulseek-file-download="${escapeHtml(encodeURIComponent(id))}"
                    ${selectable ? '' : 'disabled'}
                    title="${escapeHtml(selectable ? `Download ${isSidecar ? sidecarLabel(file.sidecarRole).toLowerCase() : title}` : `${title} cannot be downloaded`)}"
                    aria-label="${escapeHtml(selectable ? `Download ${title}` : `${title} cannot be downloaded`)}">
                    <span class="material-icons" aria-hidden="true">file_download</span>
                </button>
            </span>`;

        return `<div class="soulseek-album-file${checked ? ' is-selected' : ''}${selectable ? '' : ' is-ineligible'}${isSidecar ? ' is-sidecar' : ''}" role="row">
                    <span class="soulseek-album-file-n" role="cell">${isSidecar ? '' : escapeHtml(String(position))}</span>
                    <span class="soulseek-album-file-title" role="cell">
                        ${art}
                        <span class="soulseek-album-file-name" title="${remotePath}">${title}${subtitle}</span>
                    </span>
                    <span class="soulseek-album-file-fact soulseek-album-file-size" role="cell">${isSidecar ? '' : size}</span>
                    <span class="soulseek-album-file-eligibility" role="cell">${eligibility}</span>
                    <span class="soulseek-album-file-reason" role="cell">${reason}</span>
                    <span class="soulseek-album-check-cell" role="cell">${checkbox}</span>
                    ${download}
                </div>`;
    }

    /**
 * How to name a mixed set of ticked sidecars in one phrase.
 *
 * <p>
 *     "cover" reads better than "cover image" beside a count, and a folder that carries both artwork and
 *     lyrics says "cover + lyrics" rather than the vaguer "files", because the point of naming them is so the
 *     reader can see what is about to be taken.
 * </p>
 */
    function sidecarSummaryLabel(files) {
        const roles = Array.from(new Set(files.map((file) => sidecarLabel(file.sidecarRole).toLowerCase())));
        if (roles.length === 0) {
            return 'file';
        }

        return roles.length === 1 ? `${roles[0]} file${files.length === 1 ? '' : 's'}` : roles.join(' + ');
    }

    /**
     * How many ticked tracks and ticked sidecars there are, in one phrase.
     *
     * <p>
     *     The drawer and the panel both have to say this, and they said it differently: the drawer counted a
     *     ticked cover image as a track and the panel left it out, so the same selection read "19 of 19
     *     selected" in one place and "11 selected" in another. Naming the sidecars is also the point, because
     *     a ticked cover or lyrics file is about to be taken and the reader should see that rather than have
     *     to add it up.
     * </p>
     */
    function selectionCountPhrase(tracks, sidecars) {
        // Plain text, not markup: the drawer interpolates this into innerHTML and the panel assigns it as
        // textContent, and a helper that escaped for one of them would show entities in the other.
        const trackPart = `${tracks.length} track${tracks.length === 1 ? '' : 's'}`;
        if (!sidecars || sidecars.length === 0) {
            return trackPart;
        }

        return `${trackPart} + ${sidecars.length} ${sidecarSummaryLabel(sidecars)}`;
    }

    /** What the drawer calls each kind of non-audio file a peer is sharing. */
    function sidecarLabel(role) {
        switch (String(role || '')) {
            case 'cover':
                return 'Cover image';
            case 'lyrics':
                return 'Lyrics file';
            case 'cuesheet':
                return 'Cue sheet';
            case 'log':
                return 'Rip log';
            case 'playlist':
                return 'Playlist';
            default:
                return 'Other file';
        }
    }

    /**
     * The rows currently rendered, by the id their Browse button carries.
     *
     * <p>
     *     The drawer needs the row's remote directory and its matched candidates, and neither can be recovered
     *     from a single candidate: one candidate's path names a folder only when the peer really has it. The row
     *     is the thing that knows, because it was built by grouping every candidate of that peer and folder
     *     together, so the drawer reads it from here instead of re-deriving it and getting a different answer.
     * </p>
     */
    const releaseRowById = new Map();

    async function openAlbumDrawer(encodedId) {
        const id = decodeURIComponent(String(encodedId || ''));
        // The row passes its own release key. Deriving the folder from a single candidate's filename here is
        // what produced rows that named a folder the peer does not have, because the search's paths can be a
        // stale index rather than a real directory: a peer whose 5,543 files all sit directly in 'music' was
        // reported by the search as holding 'music/Massive Attack/Mezzanine'.
        const row = releaseRowById.get(id);
        if (!row) return;
        const key = row.key;
        const peer = String(row.username || '').trim();
        const folder = String(row.remoteDirectory || '');
        if (!peer || !folder) return;
        const current = state.albumDrawer;

        if (current && current.expanded && releaseKey(current.username, current.remoteDirectory) === key && !current.error) {
            // Collapsing keeps the listing and the selection, so reopening the same folder is instant and
            // loses nothing the reader ticked.
            current.expanded = false;
            renderGroups();
            renderPanel();
            return;
        }

        if (current && !current.expanded && releaseKey(current.username, current.remoteDirectory) === key
            && !current.loading && !current.error) {
            current.expanded = true;
            state.selectedId = row.id;
            state.panelView = 'album';
            renderGroups();
            renderPanel();
            return;
        }

        // Selections are NOT scoped to the open folder. A row is a release, so the reader may tick two of
        // them from two peers and queue them together, and the panel's total has to keep counting both.
        // Clearing on open is what made the earlier picks invisible the moment a second folder was read.
        // A new search still resets everything, because those picks belonged to a different question.
        if (!state.albumSelections.has(key)) state.albumSelections.set(key, new Set());
        state.selectedId = row.id;
        state.panelView = 'album';
        const drawer = {
            anchorId: row.id,
            username: peer,
            remoteDirectory: folder,
            loading: true,
            files: [],
            error: '',
            errorRetryable: true,
            expanded: true
        };
        // One drawer at a time: the previous folder's region disappears the moment another is opened.
        state.albumDrawer = drawer;
        renderGroups();
        renderPanel();

        try {
            const result = await request(`/users/${encodeURIComponent(peer)}/directory?path=${encodeURIComponent(folder)}`);
            const files = [];
            for (const directory of (result && result.directories) || []) {
                for (const file of directory.files || []) {
                    if (releaseKey(file.username || peer, file.remoteDirectory || folder) === key) files.push(file);
                }
            }

            // A slow peer can answer after the reader has collapsed this drawer or opened another one. The
            // answer belongs to the release it was asked for and is dropped if that is no longer on screen,
            // rather than overwriting a different folder's listing.
            if (state.albumDrawer && state.albumDrawer.anchorId === row.id
                && releaseKey(state.albumDrawer.username, state.albumDrawer.remoteDirectory) === key) {
                state.albumDrawer = Object.assign({}, state.albumDrawer, {
                    loading: false,
                    files,
                    error: '',
                    expanded: state.albumDrawer.expanded
                });
            }
        } catch (error) {
            if (state.albumDrawer && state.albumDrawer.anchorId === row.id
                && releaseKey(state.albumDrawer.username, state.albumDrawer.remoteDirectory) === key) {
                state.albumDrawer = Object.assign({}, state.albumDrawer, {
                    loading: false,
                    files: [],
                    error: browseErrorMessage(error),
                    errorRetryable: error.payload ? error.payload.retryable !== false : true,
                    expanded: true
                });
            }
        }
        renderGroups();
        renderPanel();
    }

    /**
     * What a failed peer browse tells the reader.
     *
     * <p>
     *     The server answers an expected peer failure with a reason code and a retry flag, so the message
     *     can say whether trying again is worth anything instead of leaving the reader to guess from a bare
     *     status. The search results are never touched by this: a folder that will not open is a detail of
     *     one release, not a reason to empty the table.
     * </p>
     */
    function browseErrorMessage(error) {
        const reasonCode = error && error.payload ? String(error.payload.reasonCode || '') : '';
        const detail = error && error.message ? String(error.message) : 'That folder could not be read.';

        if (reasonCode === 'peer_browse_failed' || reasonCode === 'soulseek_unavailable') {
            return `${detail} The results above are unaffected.`;
        }

        if (reasonCode === 'username_required' || reasonCode === 'remote_directory_required') {
            return `${detail} This result has no folder to browse.`;
        }

        // A folder the peer does not publish is not an empty folder, and saying so is the whole point of asking
        // before browsing. Without this the drawer claims the peer holds nothing, which is a claim about the
        // peer that a wrong folder name does not support.
        if (reasonCode === 'folder_not_published') {
            return 'This peer does not publish a folder at this path.';
        }

        return detail;
    }

    function bestRow(label, value, isHtml, isDim) {
        if (value === '') {
            return '';
        }
        return `<div class="soulseek-best-row">
                    <span class="soulseek-best-row-label">${escapeHtml(label)}</span>
                    <span class="soulseek-best-row-value${isDim ? ' soulseek-dim' : ''}">${isHtml ? value : escapeHtml(value)}</span>
                </div>`;
    }

    /**
     * Scores a peer from the facts slskd already sends with every response.
     *
     * <p>
     *     No extra request is made: upload speed, free-slot advertisement and lock state all arrive with the
     *     search response, so "source health" costs nothing to show.
     * </p>
     */
    function peerHealth(candidate) {
        const speed = Number(candidate.peerUploadSpeed) || 0;
        const free = candidate.peerHasFreeUploadSlot === true;
        const locked = candidate.isLocked === true;
        const queueLength = Math.max(0, Number(candidate.peerQueueLength) || 0);

        let score = 0;
        if (speed > 0) score += 45;
        if (speed >= 1024 * 1024) score += 20;
        if (free) score += 25;
        if (queueLength === 0) score += 10;
        if (locked) score -= 40;
        score = Math.max(0, Math.min(100, score));

        const notes = [];
        if (speed > 0) notes.push(`${formatSpeed(speed)} advertised`);
        if (free) notes.push('free slot');
        if (locked) notes.push('file locked');

        return {
            html: `<span class="soulseek-health">
                        <span class="soulseek-health-bar"><i style="width:${score}%"></i></span>
                        <span class="soulseek-health-pct">${score}%</span>
                    </span>
                    ${notes.length ? `<span class="soulseek-dim" style="font-size:11.5px;margin-top:3px">${escapeHtml(notes.join(' · '))}</span>` : ''}`
        };
    }

    /* ---------------------------------------------------------------- hub */

    function renderSearchUpdate(update) {
        // Only the current search may touch the list. Anything from a superseded search is dropped, which is
        // what stops a previous search's results appearing under a new one.
        if (!update || !update.searchId || update.searchId !== state.searchId) {
            return;
        }

        // A failed final update is handled before merging. Merging it would fall through to the empty-result branch and
        // report the failure as "no peers answered", and the final fetch that follows would only re-read the same
        // persisted failure. Both are replaced by rendering the failure once, here.
        if (update.final === true && isSearchFailed(update)) {
            renderSearchFailure(update);
            return;
        }

        mergeResults(
            update.results || [],
            update.completed === true,
            update.timedOut === true,
            Number(update.responseCount || 0),
            update.qualityGroups,
            update.outcome);

        if (update.final === true) {
            void fetchFinalResults(update.searchId);
        }
    }

    /**
     * Queues the chosen peer.
     *
     * <p>
     *     A Soulseek result is a candidate rather than a known file, so this hands the decision to the
     *     download ladder instead of fetching anything here. The ladder, the dedupe pass and the delivered
     *     quality guard all run afterwards, and the Downloads tab reports what was actually taken. This
     *     panel deliberately shows no progress of its own, so it never becomes a second source of truth.
     * </p>
     */
    async function queueCandidate(encodedId) {
        const candidate = state.byId.get(decodeURIComponent(encodedId));
        if (!candidate || candidate.accepted === false || !isCandidateEnabled(candidate)) return;

        // Checked here as well as on the tab, because a session can end while the results are still on screen:
        // the reader would otherwise click a queue button that is guaranteed to come back refused.
        if (!isSoulseekActive()) {
            state.statusText = 'Log in to enable Soulseek.';
            renderStatus();
            renderAvailability();
            return;
        }

        // The peer and the exact remote path are what make this request "this file". Without either of them the
        // queue can only run a new search for the track and download whatever that turns up, so the request is
        // refused here - where the reader can select again - instead of in the queue, where the mismatch reads
        // as the network's fault.
        if (!String(candidate.username || '').trim() || !String(candidate.filename || '').trim()) {
            state.statusText = 'The selected Soulseek file is missing its peer or remote path. Refresh the Soulseek results and select it again.';
            renderStatus();
            return;
        }

        // A Soulseek search is a peer browse, so the file the reader picked is the request. It travels as a
        // pinned candidate - the peer and the exact remote path - so the engine fetches that file instead of
        // running a second search for the same track and landing on a different one.
        //
        // The identity comes from the candidate, not from the search box: a free-text search has no artist, and
        // queueing it as one is what the endpoint rejected. The release folder is the next best artist when the
        // filename carried none, which is what a compilation or a loose folder needs.
        const folder = fileNameOf(folderOf(candidate.filename));
        const title = String(candidate.title || fileNameOf(String(candidate.filename || '')).replace(/\.[a-z0-9]{1,5}$/i, '')).trim()
            || String(state.title || '').trim();
        const artist = String(candidate.artist || state.artist || candidate.album || folder || '').trim();
        const album = String(candidate.album || folder || '').trim();
        const quality = candidate.quality || candidate.qualityCode || '';
        const destinationId = Number(state.destinationFolderId) || null;

        // The shared download client, which is what every other source enqueues through: it owns destination
        // selection, the pending-queue bookkeeping and the queue toast, so a Soulseek queue confirms in the
        // same place and the same way as a Spotify or Deezer one instead of raising a toast of its own.
        const client = global.DeezSpoTagDownload;
        if (!client || typeof client.enqueueIntentWithPreference !== 'function') {
            state.statusText = 'The download service on this page is not available.';
            renderStatus();
            return;
        }

        const notifier = typeof client.createQueueNotifier === 'function'
            ? client.createQueueNotifier({}, candidate.filename)
            : { notify: () => {}, notifyQueue: () => {} };

        try {
            const result = await client.enqueueIntentWithPreference({
                sourceService: 'soulseek',
                sourceUrl: '',
                isrc: '',
                url: candidate.filename || '',
                destinationId,
                options: {
                    metadata: {
                        title,
                        artist,
                        album,
                        quality,
                        contentType: 'stereo',
                        // The catalogue artwork the card is already showing, carried for the queue entry and
                        // for nothing else: the tagging pipeline sources its own.
                        displayCoverUrl: state.coverUrl || '',
                        durationMs: Number(candidate.durationSeconds || 0) * 1000 || 0
                    }
                },
                intentContext: {
                    allowQualityUpgrade: false,
                    // The reader chose Soulseek in Soulseek, so the intent says so rather than asking the UI
                    // which engine it happens to prefer. A ladder download reaches Soulseek on its own.
                    preferredEngine: 'soulseek',
                    preferredQuality: quality
                },
                notify: notifier.notify,
                notifyQueue: notifier.notifyQueue,
                soulseek: {
                    username: candidate.username || '',
                    remotePath: candidate.filename || '',
                    remoteSizeBytes: Number(candidate.size) || 0
                }
            });

            state.statusText = result && result.success
                ? `Queued ${artist} - ${title} from ${candidate.username || 'a peer'}. Progress appears in the Downloads tab.`
                : ((result && result.message) || 'Download was not queued.');
        } catch (error) {
            state.statusText = error.message;
            notifier.notify(error.message || 'Download failed', 'error');
        }
        renderStatus();
    }

    /**
     * Queues one file from the open folder on its own.
     *
     * <p>
     *     The checkbox and the panel's single Queue button made taking one file out of a peer folder a
     *     two-step: tick it, then queue a batch of one. The app's tracklist already offers a per-row download
     *     for exactly this, so a peer folder behaves like everywhere else tracks are listed. It goes through
     *     the same batch endpoint and therefore the same manual-admission path as the batch; the file is
     *     simply the only member of the batch.
     * </p>
     */
    async function queueSingleAlbumFile(encodedId) {
        if (state.batchBusy) return;
        const drawer = state.albumDrawer;
        const id = decodeURIComponent(String(encodedId || ''));
        const file = drawer && !drawer.loading && !drawer.error
            ? drawer.files.find((entry) => String(entry.id) === id)
            : null;
        const destinationFolderId = Number(state.destinationFolderId) || null;
        if (!file) {
            return;
        }

        // A real audio file the ladder merely passed over is still the reader's to take: the button is here for
        // the files the download would not choose on its own. Only a genuinely unavailable file has nothing
        // offered.
        if (fileIsUnavailable(file)) {
            return;
        }

        // Nothing said where the file should go. This used to return in silence, which is why pressing a
        // live, enabled button produced no request and no message at all; the reader was left to guess that
        // the folder picker was the problem. The panel's Queue button states the same requirement in its
        // tooltip, so the same words are used here.
        if (!destinationFolderId) {
            state.statusText = 'Choose where this file should go first.';
            renderStatus();
            return;
        }

        // Which list a file belongs in is decided by what it is, not by whether the download ladder preferred
        // it. Splitting on eligible sent a takeable but ineligible track to the endpoint as a sidecar, where
        // it was refused outright as not takeable.
        const isSidecar = file.sidecarFetchable === true;
        const sidecars = isSidecar ? [file] : [];
        const files = isSidecar ? [] : [file];
        if (files.length === 0 && sidecars.length === 0) {
            return;
        }

        state.batchBusy = true;
        state.batchOutcome = null;
        renderPanel();
        try {
            const response = await request('/downloads/queue-batch', {
                method: 'POST',
                body: {
                    username: drawer.username,
                    remoteDirectory: drawer.remoteDirectory,
                    destinationFolderId,
                    displayCoverUrl: state.coverUrl || '',
                    files: files.map((entry) => ({
                        remotePath: entry.filename,
                        remoteSizeBytes: Number(entry.size) || 0,
                        title: entry.title || '',
                        artist: entry.artist || '',
                        album: entry.album || '',
                        trackNumber: Number(entry.trackNumber) || null,
                        durationMs: Number(entry.durationSeconds) > 0 ? Number(entry.durationSeconds) * 1000 : null,
                        quality: entry.qualityCode || entry.quality || ''
                    })),
                    sidecars: sidecars.map((entry) => entry.filename)
                }
            });

            const responseResults = (response && response.results) || [];
            const accepted = responseResults.some((result) => result.status === 'queued');
            const selection = currentSelection();
            if (accepted) {
                // Only a file the server actually took leaves the selection; a refusal stays ticked so the
                // reader can see it and retry rather than silently losing the pick.
                selection.delete(String(file.id));
            }

            const firstFailure = responseResults.find((result) => result.status !== 'queued' && result.message);
            state.batchOutcome = {
                queued: accepted ? 1 : 0,
                skipped: accepted ? 0 : 1,
                failed: accepted ? 0 : 1,
                message: firstFailure ? String(firstFailure.message) : ''
            };
        } catch (error) {
            state.batchOutcome = { queued: 0, skipped: 0, failed: 1, message: error.message || '' };
            state.statusText = error.message;
            renderStatus();
        } finally {
            state.batchBusy = false;
            renderGroups();
            renderPanel();
        }
    }

    async function queueSelectedAlbum() {
        if (state.batchBusy) return;
        const releases = selectedReleases();
        const files = releases.flatMap((release) => release.files);
        const sidecars = releases.flatMap((release) => release.sidecars || []);
        const destinationFolderId = Number(state.destinationFolderId) || null;
        // A selection of nothing but a ticked cover or lyrics file is still a selection. The endpoint takes
        // both lists in one request, so the guard is on what is ticked rather than on the track count.
        if (releases.length === 0 || (files.length === 0 && sidecars.length === 0) || !destinationFolderId) return;

        state.batchBusy = true;
        state.batchOutcome = null;
        renderPanel();

        // One request per release, because the endpoint names exactly one peer and one remote directory and
        // re-checks every path against the folder that was actually browsed. A selection spanning two peers
        // is therefore two requests, and a release that fails does not abandon the ones after it.
        const totals = { queued: 0, skipped: 0, failed: 0 };
        let firstMessage = '';
        try {
            for (const release of releases) {
                const selection = state.albumSelections.get(release.key);
                const sidecars = release.sidecars || [];
                try {
                    const response = await request('/downloads/queue-batch', {
                        method: 'POST',
                        body: {
                            username: release.username,
                            remoteDirectory: release.remoteDirectory,
                            destinationFolderId,
                            displayCoverUrl: state.coverUrl || '',
                            files: release.files.map((file) => ({
                                remotePath: file.filename,
                                remoteSizeBytes: Number(file.size) || 0,
                                title: file.title || '',
                                artist: file.artist || '',
                                album: file.album || '',
                                trackNumber: Number(file.trackNumber) || null,
                                durationMs: Number(file.durationSeconds) > 0 ? Number(file.durationSeconds) * 1000 : null,
                                quality: file.qualityCode || file.quality || ''
                            })),

                            // The peer's own cover and lyrics travel as their own list. The server re-checks every
                            // one against the folder that was actually browsed, so a path that is not a direct child
                            // of it is refused rather than quietly fetched.
                            sidecars: sidecars.map((file) => file.filename)
                        }
                    });

                    const responseResults = (response && response.results) || [];
                    const queuedPaths = new Set();
                    for (const result of responseResults) {
                        if (result.status === 'queued') {
                            queuedPaths.add(normalizeRemotePath(result.remotePath).toLowerCase());
                        }
                    }

                    // A file the server accepted leaves the selection. One it refused stays ticked, so the
                    // reader can see which file was refused and fix the cause rather than lose the pick.
                    for (const file of release.files) {
                        if (queuedPaths.has(normalizeRemotePath(file.filename).toLowerCase()) && selection) {
                            selection.delete(String(file.id));
                        }
                    }

                    // The sidecars are not queue items in their own right, so they are not in the per-file
                    // results. They are cleared only when this release was accepted as a whole: a rejected
                    // batch left them exactly as ticked.
                    const accepted = Number(response && response.failed) === 0 && Number(response && response.skipped) === 0;
                    if (accepted) {
                        for (const file of sidecars) {
                            selection.delete(String(file.id));
                        }
                    }

                    totals.queued += Number(response && response.queued) || 0;
                    totals.skipped += Number(response && response.skipped) || 0;
                    totals.failed += Number(response && response.failed) || 0;
                    if (!firstMessage) {
                        const failure = responseResults.find((result) => result.status !== 'queued' && result.message);
                        if (failure) firstMessage = String(failure.message);
                    }
                } catch (releaseError) {
                    // One release failing is reported and the rest still go, rather than stranding the picks
                    // the reader made for other albums behind it. A release that held only a ticked cover or
                    // lyrics file is counted by what was actually ticked, so a failure there does not report
                    // itself as "0 failed".
                    const tickedHere = release.files.length + (release.sidecars || []).length;
                    totals.failed += Math.max(tickedHere, 1);
                    if (!firstMessage) firstMessage = releaseError.message || '';
                }
            }

            state.batchOutcome = { ...totals, message: firstMessage };
        } catch (error) {
            state.batchOutcome = { queued: 0, skipped: 0, failed: Math.max(files.length + sidecars.length, 1), message: error.message || '' };
            state.statusText = error.message;
            renderStatus();
        } finally {
            state.batchBusy = false;
            renderGroups();
            renderPanel();
        }
    }

    async function fetchFinalResults(searchId) {
        // The final event carries a capped slice, so the complete set is fetched once at the end.
        try {
            const result = await request(`/searches/${encodeURIComponent(searchId)}/results`);
            if (result && searchId === state.searchId) {
                // The persisted results carry the cover too, so the artwork survives the handover from the
                // live search to the recorded one.
                applySearchCover(result);

                // Checked before the merge, which is hardcoded to treat this as a completed search: a persisted
                // retrieval failure would otherwise be merged as a finished search with no candidates, which is
                // the "found nothing" answer this is not.
                if (isSearchFailed(result)) {
                    renderSearchFailure(result);
                    return;
                }

                mergeResults(result.candidates || [], true, result.timedOut === true, result.responseCount || 0, result.qualityGroups, result.outcome);
            }
        } catch (error) {
            state.statusText = error.message;
            renderStatus();
        }
    }

    function bindHubEvents(connection) {
        connection.on('connection_state', (update) => {
            state.connection = {
                state: update && update.state,
                message: update && update.message,
                username: update && update.username,
                lastError: update && update.lastError,
                usable: update && update.state === 'connected'
            };
            renderConnection();
        });

        connection.on('engine_health', (update) => {
            setText('soulseek-engine-health', update && update.state ? String(update.state) : '');
            setText('soulseek-engine-health-message', (update && update.message) || '');
        });

        connection.on('search_update', renderSearchUpdate);

        connection.on('search_result', (update) => {
            // The same id guard as every other handler, so an event for a superseded search cannot overwrite
            // the state of the one now on screen.
            if (update && update.searchId !== state.searchId) {
                return;
            }

            // A failed final result carries no candidates. Summarised as "0 candidate(s), 0 accepted" it reads
            // as a search that finished and matched nothing, which is the outcome this whole path exists to
            // prevent.
            if (isSearchFailed(update)) {
                renderSearchFailure(update);
                return;
            }

            const count = Number(update?.candidateCount || 0);
            const accepted = Number(update?.acceptedCount || 0);
            state.statusText = `Search finished · ${count} candidate(s), ${accepted} accepted.`;
            renderStatus();
        });

        connection.on('share_sync_update', (update) => {
            const enabled = Number(update?.enabledCount || 0);
            const missing = Number(update?.missingCount || 0);
            const unexpected = Number(update?.unexpectedCount || 0);
            setText(
                'soulseek-share-summary',
                `${enabled} shared folder(s) · ${missing} missing in slskd · ${unexpected} unexpected`
            );
            setVisible('soulseek-share-summary', true);
        });

        connection.on('share_scan_update', (update) => {
            if (update && update.isRunning === true) {
                setText('soulseek-share-summary', 'Scanning shares…');
            }
        });
    }

    async function startHub() {
        if (hub || hubStarting) {
            return;
        }
        if (!global.signalR || typeof global.signalR.HubConnectionBuilder !== 'function') {
            return;
        }

        hubStarting = true;
        try {
            const connection = new global.signalR.HubConnectionBuilder()
                .withUrl(HUB_URL)
                .withAutomaticReconnect()
                .configureLogging(global.signalR.LogLevel.Warning)
                .build();

            bindHubEvents(connection);
            await connection.start();
            hub = connection;
        } catch (error) {
            // A failed hub must not break the page. The panel still works through plain API calls.
            // The hub is a progressive enhancement: the panel still works over plain requests.
        } finally {
            hubStarting = false;
        }
    }

    /* ---------------------------------------------------------------- init */

    function bindPanel() {
        // The quality pills and the groups they filter are siblings inside the same panel section. One
        // delegated listener covers both: binding to #soulseek-groups alone left the pills outside it, so
        // clicking a pill did nothing. Rows are re-rendered on every live tick, so binding per row would
        // leak handlers and lose clicks between renders.
        const groups = byId('soulseek-groups');
        const pills = byId('soulseek-qtabs');
        const panel = pills && groups && pills.parentElement === groups.parentElement
            ? groups.parentElement
            : (groups || document.body);

        panel.addEventListener('click', (event) => {
            const albumButton = event.target.closest('[data-soulseek-album]');
            if (albumButton) {
                event.preventDefault();
                void openAlbumDrawer(albumButton.dataset.soulseekAlbum);
                return;
            }

            const retry = event.target.closest('[data-soulseek-drawer-retry]');
            if (retry) {
                event.preventDefault();
                state.albumDrawer = null;
                void openAlbumDrawer(retry.dataset.soulseekDrawerRetry);
                return;
            }

            const fileDownload = event.target.closest('[data-soulseek-file-download]');
            if (fileDownload) {
                event.preventDefault();
                void queueSingleAlbumFile(fileDownload.dataset.soulseekFileDownload);
                return;
            }

            const queueButton = event.target.closest('[data-soulseek-queue]');
            if (queueButton) {
                event.preventDefault();
                void queueCandidate(queueButton.dataset.soulseekQueue);
                return;
            }

            const tab = event.target.closest('[data-soulseek-group]');
            if (tab) {
                event.preventDefault();
                state.activeGroup = tab.dataset.soulseekGroup || null;
                renderGroups();
                return;
            }

            const expand = event.target.closest('[data-soulseek-expand]');
            if (expand) {
                event.preventDefault();
                const code = expand.dataset.soulseekExpand;
                if (state.expandedGroups.has(code)) {
                    state.expandedGroups.delete(code);
                } else {
                    state.expandedGroups.add(code);
                }
                renderGroups();
                return;
            }

            // A row is a peer's copy of a release, and the listing of that peer's folder is what the row is
            // for, so the row itself is the control that opens it. It used to select a single candidate to
            // drive the panel, which was coherent when a row was one file and is not now: the panel would
            // describe one arbitrary track of the album the row stands for. The chevron remains, but as a
            // redundant affordance rather than the only way in - a 30px target in the last column is not
            // discoverable as "click the row to browse this folder".
            const row = event.target.closest('tr[data-soulseek-release]');
            if (row) {
                event.preventDefault();
                const id = decodeURIComponent(row.dataset.soulseekId || '');
                const drawer = state.albumDrawer;
                const isOpen = drawer
                    && drawer.expanded
                    && drawer.anchorId === id;

                if (isOpen) {
                    drawer.expanded = false;
                    renderGroups();
                    renderPanel();
                    return;
                }

                void openAlbumDrawer(encodeURIComponent(id));
                return;
            }
        });

        panel.addEventListener('change', (event) => {
            if (event.target.matches('#soulseek-album-select-all')) {
                toggleAllEligible(event.target.checked);
                return;
            }

            if (event.target.matches('[data-soulseek-album-file]')) {
                toggleAlbumFile(decodeURIComponent(event.target.dataset.soulseekAlbumFile || ''), event.target.checked);
            }
        });

        const acceptedOnly = byId('soulseek-accepted-only');
        if (acceptedOnly) {
            acceptedOnly.addEventListener('change', () => {
                state.acceptedOnly = acceptedOnly.checked;
                renderGroups();
            });
        }

        const resultFilter = byId('soulseek-result-filter');
        if (resultFilter) {
            resultFilter.addEventListener('input', () => {
                state.resultFilter = resultFilter.value || '';
                renderGroups();
            });
        }

        const resultSort = byId('soulseek-result-sort');
        if (resultSort) {
            resultSort.addEventListener('change', () => {
                state.resultSort = resultSort.value || 'match';
                renderGroups();
            });
        }

        // The side panel is a separate subtree from the results, so it needs its own delegated listener.
        // One listener covers the view switcher, the album's track rows and the peer-folder button, all of
        // which are re-rendered on every tick.
        const side = document.querySelector('.soulseek-side') || byId('soulseek-best');
        if (side) {
            // The destination is read from the control itself, so it is bound once and never re-bound: the
            // options are re-rendered on every change, and a per-option handler would leak.
            const destination = byId('soulseek-destination');
            if (destination) {
                destination.addEventListener('change', () => {
                    state.destinationFolderId = Number(destination.value) || null;
                    renderDestinations();
                    renderPanel();
                });
            }

            side.addEventListener('click', (event) => {
                const view = event.target.closest('[data-soulseek-view]');
                if (view) {
                    event.preventDefault();
                    state.panelView = view.dataset.soulseekView || 'best';
                    renderPanel();
                    return;
                }

                const queueButton = event.target.closest('[data-soulseek-queue]');
                if (queueButton) {
                    event.preventDefault();
                    void queueCandidate(queueButton.dataset.soulseekQueue);
                    return;
                }

                const track = event.target.closest('[data-soulseek-id]');
                if (track) {
                    event.preventDefault();
                    state.selectedId = decodeURIComponent(track.dataset.soulseekId || '');
                    state.panelView = 'selected';
                    renderGroups();
                    renderPanel();
                }
            });
        }

        const bestButton = byId('soulseek-best-queue');
        if (bestButton) {
            bestButton.addEventListener('click', (event) => {
                event.preventDefault();
                if (selectedAlbumFiles().length > 0) {
                    void queueSelectedAlbum();
                    return;
                }
                const encoded = bestButton.dataset.soulseekQueue;
                if (encoded) {
                    void queueCandidate(encoded);
                }
            });
        }

        const stop = byId('soulseek-stop');
        if (stop) {
            stop.addEventListener('click', () => {
                void stopSearch();
            });
        }

        // Re-check on arrival: the user may have connected or saved credentials on the login page since the
        // panel was last rendered.
        const tab = document.querySelector('.soulseek-source-btn');
        if (tab) {
            tab.addEventListener('shown.bs.tab', () => {
                void loadConnection();
                // A tab shown with a term but no search behind it starts one now: the background search
                // never ran, or its POST was refused. runSearch already refuses to re-query a term that
                // answered, so arriving here cannot throw away results the user is reading.
                if (state.title && !state.searchId) {
                    void runSearch();
                }
            });
        }
    }

    /**
     * Stops a running search by deleting it at the source.
     *
     * <p>
     *     Stopping has to reach slskd, because it is the one holding the search open. The panel then goes to
     *     its done state with whatever arrived, which is what the Stop button in the concept promises.
     * </p>
     */
    async function stopSearch() {
        if (!state.searchId) {
            return;
        }

        const searchId = state.searchId;
        const generation = state.generation;
        const stop = byId('soulseek-stop');
        if (stop) stop.disabled = true;
        try {
            await request(`/searches/${encodeURIComponent(searchId)}`, { method: 'DELETE' });
            if (generation !== state.generation || searchId !== state.searchId) return;
            state.statusText = 'Search stopped.';
            state.phase = 'stopped';
        } catch (error) {
            if (generation !== state.generation || searchId !== state.searchId) return;
            state.statusText = error.message;
            // A failed cancellation does not mean the server stopped collecting responses.
        }
        renderStatus();
        renderPhase();
    }

    function init() {
        if (!byId('soulseek-groups')) {
            return;
        }

        bindPanel();
        void loadConnection();
        void loadDestinations();
        void startHub();
    }

    /**
     * Puts a page search term into the fields a Soulseek search is built from.
     *
     * <para>
     *     The term is passed through verbatim rather than being split into an artist and a title, because
     *     splitting free text is a guess and a wrong split searches for a track that does not exist.
     *     Soulseek matches the whole string, and the scorer tolerates the looser match in manual mode.
     * </para>
     */
    function applyPageTerm(term) {
        state.artist = '';
        state.title = term;
        state.album = '';
        state.isrc = '';
        state.durationMs = 0;
    }

    /**
     * Searches Soulseek for the term already in the page's search box.
     *
     * <para>
     *     This is what makes Soulseek behave like every other source tab: open the tab, results appear for
     *     the current search.
     * </para>
     */
    function searchCurrentTerm() {
        const term = String(global.__dsSearchTerm || currentSearchTerm || '').trim();
        if (!term) {
            state.statusText = 'Search for something first, then open the Soulseek tab.';
            renderStatus();
            return;
        }

        applyPageTerm(term);

        const tab = document.querySelector('.soulseek-source-btn');
        if (tab && global.bootstrap?.Tab) {
            global.bootstrap.Tab.getOrCreateInstance(tab).show();
        }

        // The backend owns the fresh probe and reconnect attempt. A cached browser status must not prevent
        // the request that can restore the Soulseek session.
        void runSearch();
    }

    /**
     * Searches Soulseek for a term without bringing the tab to the front.
     *
     * <para>
     *     Every other source is warmed in the background while the page searches them, so its tab already
     *     has an answer by the time it is opened. Soulseek is not a catalogue source and cannot ride that
     *     prefetch loop, so this is its equivalent: the same peer query, started for the page's term,
     *     leaving the panel where it is. runSearch owns the deduplication, so the warm-up that runs on
     *     every search cannot stack a second query on top of a term that has already answered.
     * </para>
     *
     * <para>
     *     The connection is checked first, unlike in searchCurrentTerm. A background request must not be
     *     fired when the app already knows it will be refused; the click on the tab still forces the
     *     backend probe that can restore the Soulseek session.
     * </para>
     */
    async function searchInBackground(term) {
        const wanted = String(term || '').trim();
        if (!wanted) {
            return;
        }

        const connection = state.connection || await loadConnection();
        if (connection && connection.usable !== true) {
            return;
        }

        applyPageTerm(wanted);
        void runSearch();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    global.Soulseek = Object.assign(global.Soulseek || {}, {
        init,
        loadConnection,
        runSearch,
        stopSearch,
        searchCurrentTerm,
        searchInBackground,
        state
    });
})(window);
