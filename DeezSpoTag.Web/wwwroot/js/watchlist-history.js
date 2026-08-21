(() => {
    const TABLE_BODY_ID = "watchlist-history-body";
    const DOM_CONTENT_LOADED = "DOMContentLoaded";
    const WATCHLIST_HISTORY_URL = "/api/history/watchlist";
    const SIGNALR_HUB_URL = "/activitiesHub";
    const REFRESH_INTERVAL_MS = 10000;
    const EVENT_REFRESH_DELAY_MS = 300;
    const DEFAULT_LIMIT = 50;
    const MAX_LIMIT = 200;
    const EMPTY_HISTORY_HTML = "<tr><td colspan=\"6\">No watchlist history yet.</td></tr>";
    const LOADING_HISTORY_HTML = "<tr><td colspan=\"6\">Loading watchlist history…</td></tr>";
    const ERROR_PREFIX = "Failed to load watchlist history: ";
    const TABLE_COLSPAN = "6";
    const tableBody = document.getElementById(TABLE_BODY_ID);
    if (!tableBody) {
        return;
    }
    const controlsHost = tableBody.closest("table")?.parentElement ?? null;
    let refreshTimerId = null;
    let requestId = 0;
    let activeRequestId = 0;
    let requestInFlight = false;
    let initialized = false;
    let eventRefreshTimerId = null;
    let signalRConnection = null;
    const state = {
        limit: DEFAULT_LIMIT,
        offset: 0,
        total: 0,
        lastSeenId: 0
    };
    const controls = {
        container: null,
        status: null,
        prev: null,
        next: null,
        refresh: null
    };

    const escapeHtml = (value) => {
        if (value === null || value === undefined) {
            return "";
        }
        return String(value)
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll('"', "&quot;")
            .replaceAll("'", "&#39;");
    };

    const toTitleCase = (value) => {
        if (!value) {
            return "--";
        }
        return String(value)
            .replaceAll(/[_-]+/g, " ")
            .replaceAll(/\b\w/g, (char) => char.toUpperCase());
    };

    const formatTime = (value) => {
        if (!value) {
            return "--";
        }
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) {
            return "--";
        }
        return date.toLocaleString();
    };

    function isHistoryTabActive() {
        const historyPane = document.getElementById("history-content");
        return !!historyPane?.classList.contains("active");
    }

    function clampLimit(limit) {
        const numeric = Number(limit);
        if (!Number.isFinite(numeric)) {
            return DEFAULT_LIMIT;
        }
        return Math.min(MAX_LIMIT, Math.max(1, Math.floor(numeric)));
    }

    function buildHistoryUrl(limit, offset, sinceId = 0) {
        const params = new URLSearchParams({
            limit: String(clampLimit(limit)),
            offset: String(Math.max(0, Math.floor(Number(offset) || 0))),
            _: String(Date.now())
        });
        const normalizedSinceId = Math.max(0, Math.floor(Number(sinceId) || 0));
        if (normalizedSinceId > 0) {
            params.set("sinceId", String(normalizedSinceId));
        }
        return `${WATCHLIST_HISTORY_URL}?${params.toString()}`;
    }

    function updateLastSeenId(entries) {
        if (!Array.isArray(entries)) {
            return;
        }

        const maxId = entries.reduce((current, entry) => Math.max(current, Number(entry?.id) || 0), state.lastSeenId);
        state.lastSeenId = Math.max(state.lastSeenId, maxId);
    }

    function buildEntryRow(entry) {
        const name = escapeHtml(entry.name || "--");
        const artistName = entry.artistName ? ` • ${escapeHtml(entry.artistName)}` : "";
        let watchLabel = entry.watchType || entry.collectionType || "";
        if (entry.watchType && entry.collectionType && entry.watchType !== entry.collectionType) {
            watchLabel = `${entry.watchType} ${entry.collectionType}`;
        }
        return `
<tr data-history-id="${escapeHtml(entry.id || "")}">
    <td data-label="Date">${escapeHtml(formatTime(entry.createdAt))}</td>
    <td data-label="Source">${escapeHtml(toTitleCase(entry.source))}</td>
    <td data-label="Type">${escapeHtml(toTitleCase(watchLabel))}</td>
    <td data-label="Name">${name}${artistName}</td>
    <td data-label="Tracks">${escapeHtml(entry.trackCount ?? "--")}</td>
    <td data-label="Status">${escapeHtml(toTitleCase(entry.status))}</td>
</tr>`;
    }

    function renderEntries(entries) {
        tableBody.innerHTML = entries.map(buildEntryRow).join("");
    }

    function ensureControls() {
        if (controls.container || !controlsHost) {
            return;
        }

        const container = document.createElement("div");
        container.className = "d-flex flex-wrap justify-content-between align-items-center gap-2 mt-3";
        container.innerHTML = `
            <div id="watchlist-history-status" class="text-muted small">--</div>
            <div class="btn-group btn-group-sm" role="group" aria-label="Watchlist history pagination">
                <button type="button" class="btn btn-outline-secondary" id="watchlist-history-refresh">Refresh</button>
                <button type="button" class="btn btn-outline-secondary" id="watchlist-history-prev">Previous</button>
                <button type="button" class="btn btn-outline-secondary" id="watchlist-history-next">Next</button>
            </div>`;
        controlsHost.appendChild(container);

        controls.container = container;
        controls.status = container.querySelector("#watchlist-history-status");
        controls.refresh = container.querySelector("#watchlist-history-refresh");
        controls.prev = container.querySelector("#watchlist-history-prev");
        controls.next = container.querySelector("#watchlist-history-next");

        controls.refresh?.addEventListener("click", () => {
            void loadHistory({ force: true, showLoading: true });
        });
        controls.prev?.addEventListener("click", () => {
            const nextOffset = Math.max(0, state.offset - state.limit);
            if (nextOffset === state.offset) {
                return;
            }
            state.offset = nextOffset;
            void loadHistory({ force: true, showLoading: true });
        });
        controls.next?.addEventListener("click", () => {
            const nextOffset = state.offset + state.limit;
            if (nextOffset >= state.total) {
                return;
            }
            state.offset = nextOffset;
            void loadHistory({ force: true, showLoading: true });
        });
    }

    function updateControls() {
        if (!controls.status || !controls.prev || !controls.next) {
            return;
        }

        const total = Math.max(0, Number(state.total) || 0);
        const offset = Math.max(0, Number(state.offset) || 0);
        const limit = clampLimit(state.limit);

        if (total === 0) {
            controls.status.textContent = "No entries";
            controls.prev.disabled = true;
            controls.next.disabled = true;
            return;
        }

        const start = Math.min(total, offset + 1);
        const end = Math.min(total, offset + limit);
        controls.status.textContent = `Showing ${start}-${end} of ${total}`;
        controls.prev.disabled = requestInFlight || offset <= 0;
        controls.next.disabled = requestInFlight || end >= total;
    }

    function stopAutoRefresh() {
        if (refreshTimerId !== null) {
            clearInterval(refreshTimerId);
            refreshTimerId = null;
        }
        if (eventRefreshTimerId !== null) {
            clearTimeout(eventRefreshTimerId);
            eventRefreshTimerId = null;
        }
    }

    function startAutoRefresh() {
        if (refreshTimerId !== null) {
            return;
        }
        refreshTimerId = setInterval(() => {
            if (document.hidden || !isHistoryTabActive()) {
                return;
            }
            void loadHistory();
        }, REFRESH_INTERVAL_MS);
    }

    function syncAutoRefreshState() {
        if (!document.hidden && isHistoryTabActive()) {
            startAutoRefresh();
            return;
        }
        stopAutoRefresh();
    }

    async function loadHistory(options = {}) {
        const force = options.force === true;
        const showLoading = options.showLoading === true;
        if (requestInFlight && !force) {
            return;
        }

        requestInFlight = true;
        ensureControls();
        updateControls();
        if (showLoading) {
            tableBody.innerHTML = LOADING_HISTORY_HTML;
        }
        const currentRequestId = ++requestId;
        activeRequestId = currentRequestId;

        try {
            const response = await fetch(buildHistoryUrl(state.limit, state.offset), {
                cache: "no-store",
                headers: {
                    "Cache-Control": "no-cache",
                    Pragma: "no-cache"
                }
            });
            if (!response.ok) {
                throw new Error(await response.text());
            }
            const payload = await response.json();
            if (currentRequestId !== activeRequestId) {
                return;
            }
            const entries = Array.isArray(payload?.entries) ? payload.entries : [];
            state.total = Math.max(0, Number(payload?.total) || 0);
            state.limit = clampLimit(payload?.limit ?? state.limit);
            state.offset = Math.max(0, Number(payload?.offset) || state.offset);
            if (entries.length === 0) {
                if (state.offset > 0 && state.total > 0) {
                    state.offset = Math.max(0, state.offset - state.limit);
                    void loadHistory({ force: true, showLoading: true });
                    return;
                }
                tableBody.innerHTML = EMPTY_HISTORY_HTML;
                updateControls();
                return;
            }

            updateLastSeenId(entries);
            renderEntries(entries);
            updateControls();
        } catch (error) {
            if (currentRequestId !== activeRequestId) {
                return;
            }
            tableBody.innerHTML = `<tr><td colspan="${TABLE_COLSPAN}">${ERROR_PREFIX}${escapeHtml(error.message || error)}</td></tr>`;
            updateControls();
        } finally {
            if (currentRequestId === activeRequestId) {
                requestInFlight = false;
                updateControls();
            }
        }
    }

    async function loadChangedHistory() {
        if (requestInFlight) {
            return;
        }

        if (state.offset > 0 || state.lastSeenId <= 0) {
            state.offset = 0;
            await loadHistory({ force: true, showLoading: false });
            return;
        }

        try {
            const response = await fetch(buildHistoryUrl(state.limit, 0, state.lastSeenId), {
                cache: "no-store",
                headers: {
                    "Cache-Control": "no-cache",
                    Pragma: "no-cache"
                }
            });
            if (!response.ok) {
                throw new Error(await response.text());
            }

            const payload = await response.json();
            const entries = Array.isArray(payload?.entries) ? payload.entries : [];
            if (entries.length === 0) {
                return;
            }

            updateLastSeenId(entries);
            state.total += entries.length;
            applyChangedEntries(entries);
            updateControls();
        } catch (error) {
            console.warn("Failed to load changed watchlist history", error);
            await loadHistory({ force: true, showLoading: false });
        }
    }

    function applyChangedEntries(entries) {
        const currentRows = Array.from(tableBody.querySelectorAll("tr[data-history-id]"));
        if (currentRows.length === 0) {
            renderEntries(entries.slice(0, state.limit));
            return;
        }

        entries.slice().reverse().forEach((entry) => {
            const id = String(entry?.id || "");
            if (!id) {
                return;
            }

            Array.from(tableBody.querySelectorAll("tr[data-history-id]"))
                .find((row) => row.dataset.historyId === id)
                ?.remove();
            tableBody.insertAdjacentHTML("afterbegin", buildEntryRow(entry));
        });

        Array.from(tableBody.querySelectorAll("tr[data-history-id]"))
            .slice(state.limit)
            .forEach((row) => row.remove());
    }

    function queueEventRefresh() {
        if (document.hidden || !isHistoryTabActive()) {
            return;
        }

        if (eventRefreshTimerId !== null) {
            clearTimeout(eventRefreshTimerId);
        }
        eventRefreshTimerId = setTimeout(() => {
            eventRefreshTimerId = null;
            void loadChangedHistory();
        }, EVENT_REFRESH_DELAY_MS);
    }

    function connectRealtime() {
        if (signalRConnection || !globalThis.signalR) {
            return;
        }

        signalRConnection = new globalThis.signalR.HubConnectionBuilder()
            .withUrl(SIGNALR_HUB_URL)
            .withAutomaticReconnect()
            .build();
        signalRConnection.on("watchlistHistoryChanged", queueEventRefresh);
        signalRConnection.start().catch((error) => {
            console.warn("Watchlist history realtime connection failed", error);
            signalRConnection = null;
        });
    }

    globalThis.DeezSpoTagWatchlistHistory = {
        refresh: (options) => loadHistory(options),
        syncAutoRefresh: () => syncAutoRefreshState()
    };

    function initializeWatchlistHistory() {
        if (initialized) {
            return;
        }
        initialized = true;
        ensureControls();
        connectRealtime();
        void loadHistory({ showLoading: true });
        syncAutoRefreshState();
    }

    if (document.readyState === "loading") {
        document.addEventListener(DOM_CONTENT_LOADED, initializeWatchlistHistory);
    } else {
        initializeWatchlistHistory();
    }
    document.addEventListener("visibilitychange", () => {
        syncAutoRefreshState();
        if (!document.hidden && isHistoryTabActive()) {
            void loadHistory({ showLoading: false });
        }
    });
    document.addEventListener("shown.bs.tab", (event) => {
        syncAutoRefreshState();
        const trigger = event?.target?.closest?.("[data-bs-toggle='tab']");
        const targetSelector = trigger?.dataset?.bsTarget;
        if (targetSelector === "#history-content") {
            void loadHistory({ force: true, showLoading: true });
        }
    });
    globalThis.addEventListener("focus", () => {
        if (!document.hidden && isHistoryTabActive()) {
            void loadHistory();
        }
    });
    globalThis.addEventListener("deezspotag:activities-live-update", () => {
        queueEventRefresh();
    });
    globalThis.addEventListener("beforeunload", stopAutoRefresh);
})();
