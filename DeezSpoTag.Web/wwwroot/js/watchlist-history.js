(() => {
    const TABLE_BODY_ID = "watchlist-history-body";
    const DOM_CONTENT_LOADED = "DOMContentLoaded";
    const WATCHLIST_HISTORY_URL = "/api/history/watchlist?limit=50&offset=0";
    const EMPTY_HISTORY_HTML = "<tr><td colspan=\"6\">No watchlist history yet.</td></tr>";
    const ERROR_PREFIX = "Failed to load watchlist history: ";
    const TABLE_COLSPAN = "6";
    const tableBody = document.getElementById(TABLE_BODY_ID);
    if (!tableBody) {
        return;
    }

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
        return String(value).replaceAll(/\b\w/g, (char) => char.toUpperCase());
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

    async function loadHistory() {
        try {
            const response = await fetch(WATCHLIST_HISTORY_URL);
            if (!response.ok) {
                throw new Error(await response.text());
            }
            const payload = await response.json();
            const entries = Array.isArray(payload?.entries) ? payload.entries : [];
            if (entries.length === 0) {
                tableBody.innerHTML = EMPTY_HISTORY_HTML;
                return;
            }

            tableBody.innerHTML = entries.map((entry) => {
                const name = escapeHtml(entry.name || "--");
                const artistName = entry.artistName ? ` • ${escapeHtml(entry.artistName)}` : "";
                let watchLabel = entry.watchType || entry.collectionType || "";
                if (entry.watchType && entry.collectionType && entry.watchType !== entry.collectionType) {
                    watchLabel = `${entry.watchType} ${entry.collectionType}`;
                }
                return `
<tr>
    <td>${escapeHtml(formatTime(entry.createdAt))}</td>
    <td>${escapeHtml(toTitleCase(entry.source))}</td>
    <td>${escapeHtml(toTitleCase(watchLabel))}</td>
    <td>${name}${artistName}</td>
    <td>${escapeHtml(entry.trackCount ?? "--")}</td>
    <td>${escapeHtml(toTitleCase(entry.status))}</td>
</tr>`;
            }).join("");
        } catch (error) {
            tableBody.innerHTML = `<tr><td colspan="${TABLE_COLSPAN}">${ERROR_PREFIX}${escapeHtml(error.message || error)}</td></tr>`;
        }
    }

    globalThis.DeezSpoTagWatchlistHistory = {
        refresh: () => loadHistory()
    };

    const historyTab = document.getElementById("activities-history-tab");
    historyTab?.addEventListener("shown.bs.tab", () => {
        void loadHistory();
    });

    document.addEventListener(DOM_CONTENT_LOADED, loadHistory);
})();
