(() => {
    const JOB_KEY = "autotagJobId";
    const STATUS_OK = "ok";
    const STATUS_TAGGED = "tagged";
    const STATUS_TAGGING = "tagging";
    const STATUS_ERROR = "error";
    const STATUS_SKIPPED = "skipped";
    const STATUS_RUNNING = "running";
    const STATUS_COMPLETED = "completed";
    const STATUS_FAILED = "failed";
    const pollIntervalMs = 3000;
    const runningDetailRefreshMs = 6000;
    const idleDetailRefreshMs = 20000;
    const weekDays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    let pollTimer = null;

    const state = {
        currentFilter: "all",
        historyStatus: [],
        liveJobId: null,
        liveJobPath: null,
        liveJobSummary: null,
        archivedRuns: [],
        manualHistorySelection: false,
        selectedRunId: null,
        runSelectionMessage: null,
        selectedDate: null,
        calendarMonth: new Date(new Date().getFullYear(), new Date().getMonth(), 1),
        calendarRequestId: 0,
        runsRequestId: 0,
        runDetailsRequestId: 0,
        lastLiveDetailsFetchedAt: 0
    };

    const el = (id) => document.getElementById(id);

    function showToast(message, type = "info") {
        if (globalThis.DeezSpoTag?.showNotification) {
            globalThis.DeezSpoTag.showNotification(String(message || ""), type);
            return;
        }

        const safeType = ["success", "error", "warning", "info"].includes(type) ? type : "info";
        const toast = document.createElement("div");
        toast.className = `toast toast-${safeType}`;
        toast.textContent = String(message || "");
        document.body.appendChild(toast);
        setTimeout(() => toast.remove(), 3000);
    }

    function hasStatusUI() {
        return el("autotag-job") && el("autotag-state") && el("autotag-updated") && el("autotag-log");
    }

    function formatDuration(start, end) {
        if (!start) {
            return "00:00";
        }
        const startMs = new Date(start).getTime();
        const endMs = end ? new Date(end).getTime() : Date.now();
        const totalSeconds = Math.max(0, Math.floor((endMs - startMs) / 1000));
        const minutes = Math.floor(totalSeconds / 60);
        const seconds = totalSeconds % 60;
        return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
    }

    function stripAnsi(value) {
        if (!value) {
            return value;
        }
        const esc = String.fromCodePoint(27);
        let output = String(value);
        while (true) {
            const start = output.indexOf(`${esc}[`);
            if (start === -1) {
                break;
            }

            const end = output.indexOf("m", start + 2);
            if (end === -1) {
                break;
            }

            output = `${output.slice(0, start)}${output.slice(end + 1)}`;
        }

        return output;
    }

    function escapeHtml(value) {
        if (value === null || value === undefined) {
            return "";
        }
        return String(value)
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll('"', "&quot;")
            .replaceAll("'", "&#39;");
    }

    function normalizeLogLines(logs) {
        if (Array.isArray(logs)) {
            return logs.map((line) => {
                if (line === null || line === undefined) {
                    return "";
                }
                return String(line);
            });
        }

        if (typeof logs === "string") {
            return [logs];
        }

        return [];
    }

    function toFileName(value) {
        if (!value) {
            return "--";
        }
        const normalized = value.replaceAll("\\", "/");
        const parts = normalized.split("/");
        return parts[parts.length - 1] || value;
    }

    function getStatusClass(status) {
        switch (status?.toLowerCase()) {
            case STATUS_OK:
            case STATUS_TAGGED:
                return "text-success";
            case STATUS_TAGGING:
                return "text-info";
            case STATUS_ERROR:
                return "text-danger";
            case STATUS_SKIPPED:
                return "text-warning";
            default:
                return "";
        }
    }

    function setText(id, value) {
        const node = el(id);
        if (node) {
            node.textContent = value;
        }
    }

    function formatDate(value) {
        if (!value) {
            return "--";
        }
        const parsedDateToken = parseDateToken(value);
        const parsed = parsedDateToken ?? new Date(value);
        if (Number.isNaN(parsed.getTime())) {
            return "--";
        }
        return parsed.toLocaleDateString(undefined, {
            weekday: "long",
            year: "numeric",
            month: "long",
            day: "numeric"
        });
    }

    function parseDateToken(value) {
        if (typeof value !== "string") {
            return null;
        }
        const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value.trim());
        if (!match) {
            return null;
        }

        const year = Number(match[1]);
        const month = Number(match[2]);
        const day = Number(match[3]);
        const parsed = new Date(year, month - 1, day);
        if (parsed.getFullYear() !== year || parsed.getMonth() !== month - 1 || parsed.getDate() !== day) {
            return null;
        }
        return parsed;
    }

    function isTodayDateToken(value) {
        return value === toDateToken(new Date());
    }

    function normalizeRunId(value) {
        if (value === null || value === undefined) {
            return "";
        }
        return String(value).trim();
    }

    function isTerminalRunStatus(status) {
        const normalized = String(status || "").toLowerCase();
        return normalized === STATUS_COMPLETED
            || normalized === STATUS_FAILED
            || normalized === "canceled"
            || normalized === "cancelled"
            || normalized === "interrupted"
            || normalized === "idle"
            || normalized === "error";
    }

    function hasActiveLiveRun() {
        const runId = normalizeRunId(state.liveJobSummary?.id);
        if (!runId) {
            return false;
        }

        return !isTerminalRunStatus(state.liveJobSummary?.status);
    }

    function isHistoryTabActive() {
        const historyPane = el("history-content");
        return !!historyPane?.classList.contains("active");
    }

    function isAutoTagStatusTabActive() {
        const statusPane = el("autotag-status-content");
        return !!statusPane?.classList.contains("active");
    }

    function canShowLiveRunForSelectedDate() {
        if (!hasActiveLiveRun()) {
            return false;
        }

        if (!state.selectedDate) {
            return true;
        }

        return isTodayDateToken(state.selectedDate);
    }

    function getLiveRunDateToken(job) {
        const sourceDate = job?.startedAt || job?.finishedAt || null;
        if (typeof sourceDate === "string") {
            const trimmed = sourceDate.trim();
            const isoPrefix = trimmed.slice(0, 10);
            if (/^\d{4}-\d{2}-\d{2}$/.test(isoPrefix)) {
                return isoPrefix;
            }
        }

        const parsed = sourceDate ? new Date(sourceDate) : null;
        if (parsed && !Number.isNaN(parsed.getTime())) {
            return toDateTokenUtc(parsed);
        }
        return toDateTokenUtc(new Date());
    }

    function shouldFollowLiveRunInHistory() {
        return isHistoryTabActive() && !state.manualHistorySelection && hasActiveLiveRun();
    }

    function getRunsForDisplay(runs) {
        const archivedRuns = Array.isArray(runs) ? runs : [];
        if (!canShowLiveRunForSelectedDate()) {
            return archivedRuns;
        }

        const liveRunId = normalizeRunId(state.liveJobSummary?.id);
        if (!liveRunId) {
            return archivedRuns;
        }

        if (archivedRuns.some((run) => normalizeRunId(run?.id) === liveRunId)) {
            return archivedRuns;
        }

        return [state.liveJobSummary, ...archivedRuns];
    }

    function toDateToken(date) {
        if (!(date instanceof Date) || Number.isNaN(date.getTime())) {
            return "";
        }
        const year = date.getFullYear();
        const month = String(date.getMonth() + 1).padStart(2, "0");
        const day = String(date.getDate()).padStart(2, "0");
        return `${year}-${month}-${day}`;
    }

    function toDateTokenUtc(date) {
        if (!(date instanceof Date) || Number.isNaN(date.getTime())) {
            return "";
        }
        const year = date.getUTCFullYear();
        const month = String(date.getUTCMonth() + 1).padStart(2, "0");
        const day = String(date.getUTCDate()).padStart(2, "0");
        return `${year}-${month}-${day}`;
    }

    async function fetchJson(url) {
        const response = await fetch(url, {
            cache: "no-store",
            headers: {
                "Cache-Control": "no-cache",
                Pragma: "no-cache"
            }
        });
        if (!response.ok) {
            const error = new Error(`Request failed: ${response.status}`);
            error.status = response.status;
            throw error;
        }
        return response.json();
    }

    function updateLiveMetadata(job) {
        const logLines = normalizeLogLines(job?.logs);
        const logCount = typeof job?.logCount === "number" ? job.logCount : logLines.length;
        setText("autotag-runtime", formatDuration(job?.startedAt, job?.finishedAt));
        setText("autotag-log-count", String(logCount));
        setText("autotag-exit-code", job?.exitCode ?? "--");
        setText("autotag-platform", job?.currentPlatform || "--");
        setText("autotag-last-track", toFileName(job?.lastStatus?.status?.path));
        setText("autotag-last-result", job?.lastStatus?.status?.status || "--");
        const accuracy = job?.lastStatus?.status?.accuracy;
        setText("autotag-last-accuracy", typeof accuracy === "number" ? accuracy.toFixed(2) : "--");
        setText("autotag-ok-count", String(job?.okCount ?? 0));
        setText("autotag-error-count", String(job?.errorCount ?? 0));
        setText("autotag-skipped-count", String(job?.skippedCount ?? 0));

        const lastLogEl = el("autotag-last-log");
        if (lastLogEl) {
            if (logLines.length) {
                lastLogEl.textContent = stripAnsi(logLines.at(-1));
            } else if (job?.lastLogLine) {
                lastLogEl.textContent = stripAnsi(job.lastLogLine);
            } else if (!job) {
                lastLogEl.textContent = "No recent log lines.";
            }
        }

        if (job?.rootPath) {
            state.liveJobPath = job.rootPath;
        }
    }

    function findLastStatusEntry(statusHistory) {
        if (!Array.isArray(statusHistory) || !statusHistory.length) {
            return null;
        }
        return statusHistory.at(-1) || null;
    }

    function renderStatusPanelForArchive(summary, archive) {
        if (!summary) {
            return;
        }

        const lastEntry = findLastStatusEntry(archive?.statusHistory);
        const lastStatus = lastEntry?.status?.status || {};
        const lastPlatform = lastEntry?.status?.platform || "--";
        const logs = Array.isArray(archive?.logs) ? archive.logs : [];

        updateStatus(summary.id, summary.status || "waiting");
        setText("autotag-runtime", formatDuration(summary.startedAt, summary.finishedAt));
        setText("autotag-log-count", String(summary.logCount ?? logs.length ?? 0));
        setText("autotag-exit-code", summary.exitCode ?? "--");
        setText("autotag-platform", lastPlatform);
        setText("autotag-last-track", toFileName(lastStatus.path));
        setText("autotag-last-result", lastStatus.status || "--");
        setText("autotag-last-accuracy", typeof lastStatus.accuracy === "number" ? lastStatus.accuracy.toFixed(2) : "--");
        setText("autotag-ok-count", String(summary.okCount ?? 0));
        setText("autotag-error-count", String(summary.errorCount ?? 0));
        setText("autotag-skipped-count", String(summary.skippedCount ?? 0));
        setText("autotag-last-log", logs.length ? stripAnsi(logs[logs.length - 1]) : "No recent log lines.");
        updateLogs(logs);
        updateProgressBar({
            status: summary.status,
            progress: typeof summary.progress === "number" ? summary.progress : null
        });
    }

    function updateProgressBar(job) {
        const bar = el("autotag-progress-bar");
        if (!bar) {
            return;
        }

        bar.className = "progress-bar";
        bar.style.width = "0%";

        const status = job?.status;
        const progress = typeof job?.progress === "number" ? Math.max(0, Math.min(1, job.progress)) : null;

        if (status === STATUS_RUNNING) {
            bar.classList.add("bg-info", "progress-bar-striped", "progress-bar-animated");
            bar.style.width = progress === null ? "100%" : `${Math.round(progress * 100)}%`;
        } else if (status === STATUS_COMPLETED) {
            bar.classList.add("bg-success");
            bar.style.width = "100%";
        } else if (status === STATUS_FAILED) {
            bar.classList.add("bg-danger");
            bar.style.width = "100%";
        } else if (status) {
            bar.classList.add("bg-secondary");
        }
    }

    function updateStatus(jobId, stateText) {
        if (!hasStatusUI()) {
            return;
        }
        setText("autotag-job", jobId || "Idle");
        setText("autotag-state", stateText || "waiting");
        setText("autotag-updated", new Date().toLocaleTimeString());
    }

    function updateLogs(logs) {
        if (!hasStatusUI()) {
            return;
        }
        const logLines = normalizeLogLines(logs);
        const text = logLines.length
            ? logLines.map(stripAnsi).join("\n")
            : "No AutoTag logs yet.";
        setText("autotag-log", text);
    }

    function updateFilterCountsFromHistory() {
        const statuses = Array.isArray(state.historyStatus) ? state.historyStatus : [];
        let ok = 0;
        let error = 0;
        let skipped = 0;

        statuses.forEach((entry) => {
            const result = String(entry?.status?.status?.status || "").toLowerCase();
            if (result === STATUS_OK || result === STATUS_TAGGED) {
                ok += 1;
            } else if (result === STATUS_ERROR) {
                error += 1;
            } else if (result === STATUS_SKIPPED) {
                skipped += 1;
            }
        });

        setText("autotag-filter-ok-count", String(ok));
        setText("autotag-filter-error-count", String(error));
        setText("autotag-filter-skipped-count", String(skipped));
    }

    function renderFilteredHistory() {
        const tableBody = el("autotag-status-table");
        if (!tableBody) {
            return;
        }

        let filteredRows = Array.isArray(state.historyStatus) ? state.historyStatus : [];
        if (state.currentFilter !== "all") {
            filteredRows = filteredRows.filter((entry) => {
                const result = String(entry?.status?.status?.status || "").toLowerCase();
                if (state.currentFilter === STATUS_OK) {
                    return result === STATUS_OK || result === STATUS_TAGGED;
                }
                return result === state.currentFilter;
            });
        }

        if (!state.selectedRunId) {
            const message = state.runSelectionMessage || "Select a run to load full AutoTag history.";
            tableBody.innerHTML = `<tr><td colspan="6">${escapeHtml(message)}</td></tr>`;
            return;
        }

        if (!filteredRows.length) {
            const message = state.currentFilter === "all"
                ? "No status history recorded for this run."
                : `No ${state.currentFilter} entries found for this run.`;
            tableBody.innerHTML = `<tr><td colspan="6">${message}</td></tr>`;
            return;
        }

        tableBody.innerHTML = filteredRows.map((entry) => {
            const status = entry?.status || {};
            const inner = status.status || {};
            const time = entry?.timestamp ? new Date(entry.timestamp).toLocaleTimeString() : "--";
            const platform = status.platform || "--";
            const result = inner.status || "--";
            const resultNormalized = String(result).toLowerCase();
            const statusClass = getStatusClass(result);
            const accuracy = typeof inner.accuracy === "number" ? inner.accuracy.toFixed(2) : "--";
            const track = toFileName(inner.path);
            const usedShazam = inner.usedShazam ? '<i class="fas fa-music ms-1" title="Identified with Shazam"></i>' : "";
            const encodedPath = inner.path ? encodeURIComponent(inner.path) : "";
            const encodedPlatform = platform && platform !== "--" ? encodeURIComponent(platform) : "";
            const canDiff = inner.path && encodedPlatform
                && (resultNormalized === STATUS_TAGGED || resultNormalized === STATUS_OK);
            const diffButton = canDiff
                ? `<button type="button" class="action-btn action-btn-sm autotag-diff-btn" data-path="${encodedPath}" data-platform="${encodedPlatform}">Diff</button>`
                : '<span class="text-muted">--</span>';
            return `<tr>
                <td>${escapeHtml(time)}</td>
                <td>${escapeHtml(platform)}</td>
                <td class="${statusClass}">${escapeHtml(result)}${usedShazam}</td>
                <td>${escapeHtml(accuracy)}</td>
                <td title="${escapeHtml(inner.path || "")}">${escapeHtml(track)}</td>
                <td>${diffButton}</td>
            </tr>`;
        }).join("");
    }

    function setFilter(filter) {
        state.currentFilter = filter;
        document.querySelectorAll(".autotag-filter-toolbar button[data-filter]").forEach((btn) => {
            btn.classList.toggle("active", btn.dataset.filter === filter);
        });
        renderFilteredHistory();
    }

    function renderRunSummary(summary, archive) {
        if (summary) {
            state.runSelectionMessage = null;
        }

        setText("autotag-history-selected-date", state.selectedDate ? formatDate(state.selectedDate) : "--");
        if (summary && archive) {
            renderStatusPanelForArchive(summary, archive);
        }
    }

    function renderLiveRunSelection(job, logs) {
        const liveSummary = buildSummaryFromLiveJob(job);
        if (!liveSummary?.id) {
            return;
        }

        state.selectedRunId = liveSummary.id;
        state.selectedDate = getLiveRunDateToken(job);
        state.historyStatus = Array.isArray(job?.statusHistory)
            ? job.statusHistory.slice().reverse()
            : [];
        renderRunSummary(liveSummary, {
            summary: liveSummary,
            statusHistory: job?.statusHistory || [],
            logs
        });
        updateFilterCountsFromHistory();
        renderFilteredHistory();
        renderRunList(state.archivedRuns);
        highlightSelectedRun();
    }

    function buildSummaryFromLiveJob(job) {
        if (!job?.id) {
            return null;
        }

        let logCount = 0;
        if (typeof job.logCount === "number") {
            logCount = job.logCount;
        } else if (Array.isArray(job.logs)) {
            logCount = job.logs.length;
        }

        let statusEntryCount = 0;
        if (typeof job.statusEntryCount === "number") {
            statusEntryCount = job.statusEntryCount;
        } else if (Array.isArray(job.statusHistory)) {
            statusEntryCount = job.statusHistory.length;
        }

        return {
            id: job.id,
            status: job.status || STATUS_RUNNING,
            startedAt: job.startedAt || null,
            finishedAt: job.finishedAt || null,
            exitCode: job.exitCode ?? null,
            rootPath: job.rootPath || null,
            trigger: job.trigger || "manual",
            okCount: job.okCount ?? 0,
            errorCount: job.errorCount ?? 0,
            skippedCount: job.skippedCount ?? 0,
            logCount,
            statusEntryCount,
            progress: typeof job.progress === "number" ? job.progress : null
        };
    }

    async function loadLiveRunDetails(runId) {
        const liveJob = await fetchJson(`/api/autotag/jobs/${encodeURIComponent(runId)}?includeLogs=true&includeStatusHistory=true`);
        const liveSummary = buildSummaryFromLiveJob(liveJob);
        state.selectedRunId = liveJob?.id || runId;
        state.historyStatus = Array.isArray(liveJob?.statusHistory) ? liveJob.statusHistory.slice().reverse() : [];
        state.lastLiveDetailsFetchedAt = Date.now();
        renderRunSummary(liveSummary, {
            summary: liveSummary,
            statusHistory: liveJob?.statusHistory || [],
            logs: liveJob?.logs || []
        });
        updateFilterCountsFromHistory();
        renderFilteredHistory();
        highlightSelectedRun();
    }

    function isStaleRunDetailsRequest(requestId) {
        return requestId !== state.runDetailsRequestId;
    }

    function canUseLiveRunSelection(runId) {
        return Boolean(state.liveJobId && runId === state.liveJobId && hasActiveLiveRun());
    }

    async function tryLoadPreferredLiveRun(runId, requestId) {
        if (!canUseLiveRunSelection(runId)) {
            return false;
        }

        return tryLoadLiveRunDetailsForSelection(
            runId,
            requestId,
            "Failed to load selected live AutoTag run");
    }

    async function tryLoadArchiveThenLiveFallback(runId, requestId) {
        try {
            await tryLoadArchiveRunDetails(runId, requestId);
            return true;
        } catch (error) {
            const loadedLive = await tryLoadLiveRunDetailsForSelection(
                runId,
                requestId,
                "Failed to load live AutoTag run fallback");
            if (!loadedLive) {
                console.warn("Failed to load archived AutoTag run", error);
                clearRunSelection("Failed to load the full AutoTag log.");
            }
            return loadedLive;
        }
    }

    async function tryLoadLiveRunDetailsForSelection(runId, requestId, warnMessage) {
        try {
            await loadLiveRunDetails(runId);
            return !isStaleRunDetailsRequest(requestId);
        } catch (error) {
            console.warn(warnMessage, error);
            return false;
        }
    }

    async function tryLoadArchiveRunDetails(runId, requestId) {
        const archive = await fetchJson(`/api/autotag/history/runs/${encodeURIComponent(runId)}`);
        if (isStaleRunDetailsRequest(requestId)) {
            return false;
        }

        const archivedStatusHistory = Array.isArray(archive?.statusHistory) ? archive.statusHistory : [];
        if (archivedStatusHistory.length === 0) {
            const loadedLiveFallback = await tryLoadLiveRunDetailsForSelection(
                runId,
                requestId,
                "Failed to load live AutoTag run fallback for empty archive history");
            if (loadedLiveFallback && Array.isArray(state.historyStatus) && state.historyStatus.length > 0) {
                return true;
            }
        }

        state.selectedRunId = archive?.summary?.id || runId;
        state.historyStatus = archivedStatusHistory.slice().reverse();
        renderRunSummary(archive?.summary || null, archive || null);
        updateFilterCountsFromHistory();
        renderFilteredHistory();
        highlightSelectedRun();
        return true;
    }

    function clearRunSelection(message) {
        state.selectedRunId = null;
        state.historyStatus = [];
        state.runSelectionMessage = message || "Select a run to load full AutoTag history.";
        renderRunSummary(null, null);
        renderFilteredHistory();
        updateFilterCountsFromHistory();
    }

    async function loadRunDetails(runId) {
        if (!runId) {
            clearRunSelection();
            return;
        }
        const requestId = ++state.runDetailsRequestId;
        if (await tryLoadPreferredLiveRun(runId, requestId)) {
            return;
        }
        await tryLoadArchiveThenLiveFallback(runId, requestId);
    }

    function highlightSelectedRun() {
        document.querySelectorAll(".autotag-run-item[data-run-id]").forEach((button) => {
            button.classList.toggle("is-selected", button.dataset.runId === state.selectedRunId);
        });
    }

    function renderRunList(runs) {
        const list = el("autotag-history-run-list");
        if (!list) {
            return;
        }

        state.archivedRuns = Array.isArray(runs) ? runs : [];
        const runsForDisplay = getRunsForDisplay(state.archivedRuns);

        if (!runsForDisplay.length) {
            list.innerHTML = '<div class="autotag-run-empty">No AutoTag runs were saved on this date.</div>';
            clearRunSelection("No AutoTag log was saved on this date.");
            return;
        }

        list.innerHTML = runsForDisplay.map((run) => {
            const started = run?.startedAt ? new Date(run.startedAt).toLocaleTimeString() : "--";
            const duration = formatDuration(run?.startedAt, run?.finishedAt);
            const path = run?.rootPath || "--";
            const liveBadge = hasActiveLiveRun() && normalizeRunId(run?.id) === normalizeRunId(state.liveJobSummary?.id)
                ? '<span class="badge bg-info text-dark">Live</span>'
                : "";
            return `<button type="button" class="autotag-run-item" data-run-id="${escapeHtml(run.id)}">
                <strong>${escapeHtml(started)} · ${escapeHtml(run.status || "--")} ${liveBadge}</strong>
                <div class="autotag-run-item-meta">
                    <span>${escapeHtml(duration)}</span>
                    <span>${escapeHtml(run.trigger || "manual")}</span>
                    <span>${escapeHtml(`logs ${run.logCount ?? 0}`)}</span>
                </div>
                <div class="autotag-run-item-path">${escapeHtml(path)}</div>
            </button>`;
        }).join("");

        const preferred = runsForDisplay.find((run) => run.id === state.selectedRunId) || runsForDisplay[0];
        const needsReload = preferred.id !== state.selectedRunId || !Array.isArray(state.historyStatus) || state.historyStatus.length === 0;
        if (needsReload) {
            void loadRunDetails(preferred.id);
        } else {
            highlightSelectedRun();
        }
    }

    async function loadRunsForDate(date, options = {}) {
        const requestId = ++state.runsRequestId;
        state.selectedDate = date;
        if (options.manual === true) {
            state.manualHistorySelection = true;
        }
        setText("autotag-history-selected-date", formatDate(date));
        try {
            const payload = await fetchJson(`/api/autotag/history/runs?date=${encodeURIComponent(date)}`);
            if (requestId !== state.runsRequestId) {
                return;
            }
            renderRunList(payload?.runs || []);
            highlightSelectedDay();
        } catch (error) {
            if (requestId !== state.runsRequestId) {
                return;
            }
            console.warn("Failed to load AutoTag runs for date", error);
            const list = el("autotag-history-run-list");
            if (list) {
                list.innerHTML = '<div class="autotag-run-empty">Failed to load AutoTag runs for this date.</div>';
            }
            clearRunSelection("Failed to load the full AutoTag log.");
        }
    }

    function highlightSelectedDay() {
        document.querySelectorAll(".autotag-calendar-day[data-date]").forEach((button) => {
            button.classList.toggle("is-selected", button.dataset.date === state.selectedDate);
        });
    }

    function renderCalendar(days) {
        const container = el("autotag-history-calendar");
        if (!container) {
            return;
        }

        const firstDay = new Date(state.calendarMonth.getFullYear(), state.calendarMonth.getMonth(), 1);
        const lastDay = new Date(state.calendarMonth.getFullYear(), state.calendarMonth.getMonth() + 1, 0);
        const counts = new Map((Array.isArray(days) ? days : []).map((day) => [day.date, day]));
        const fragments = [];

        weekDays.forEach((day) => {
            fragments.push(`<div class="autotag-calendar-weekday">${escapeHtml(day)}</div>`);
        });

        for (let index = 0; index < firstDay.getDay(); index += 1) {
            fragments.push('<div class="autotag-calendar-day is-empty" aria-hidden="true"></div>');
        }

        const todayToken = toDateToken(new Date());
        for (let dayNumber = 1; dayNumber <= lastDay.getDate(); dayNumber += 1) {
            const current = new Date(state.calendarMonth.getFullYear(), state.calendarMonth.getMonth(), dayNumber);
            const token = toDateToken(current);
            const info = counts.get(token);
            const classes = ["autotag-calendar-day"];
            if (token === todayToken) {
                classes.push("is-today");
            }
            if (token === state.selectedDate) {
                classes.push("is-selected");
            }

            let runCountLabel = "";
            if (info?.runCount) {
                const runSuffix = info.runCount === 1 ? "" : "s";
                runCountLabel = `${info.runCount} run${runSuffix}`;
            }
            fragments.push(`<button type="button" class="${classes.join(" ")}" data-date="${token}">
                <span class="autotag-calendar-day-number">${dayNumber}</span>
                <span class="autotag-calendar-day-count">${runCountLabel}</span>
            </button>`);
        }

        container.innerHTML = fragments.join("");
        setText("autotag-history-month-label", state.calendarMonth.toLocaleDateString(undefined, {
            month: "long",
            year: "numeric"
        }));
    }

    async function loadCalendar() {
        const requestId = ++state.calendarRequestId;
        const year = state.calendarMonth.getFullYear();
        const month = state.calendarMonth.getMonth() + 1;

        try {
            const payload = await fetchJson(`/api/autotag/history/calendar?year=${year}&month=${month}`);
            if (requestId !== state.calendarRequestId) {
                return;
            }
            const days = payload?.days || [];
            renderCalendar(days);

            const availableDates = days
                .map((day) => day.date)
                .filter(Boolean)
                .sort()
                .reverse();
            const selectedStillVisible = state.selectedDate
                && availableDates.includes(state.selectedDate)
                && state.selectedDate.startsWith(`${year}-${String(month).padStart(2, "0")}`);
            if (selectedStillVisible) {
                await loadRunsForDate(state.selectedDate);
                return;
            }

            const today = new Date();
            const todayToken = toDateToken(today);
            const viewedMonthIsCurrent = today.getFullYear() === year && today.getMonth() + 1 === month;
            let defaultDate = availableDates[0];
            if (availableDates.includes(todayToken)) {
                defaultDate = todayToken;
            } else if (!defaultDate) {
                defaultDate = viewedMonthIsCurrent
                    ? todayToken
                    : `${year}-${String(month).padStart(2, "0")}-01`;
            }
            await loadRunsForDate(defaultDate);
        } catch (error) {
            if (requestId !== state.calendarRequestId) {
                return;
            }
            console.warn("Failed to load AutoTag calendar", error);
            const container = el("autotag-history-calendar");
            if (container) {
                container.innerHTML = '<div class="autotag-run-empty">Failed to load AutoTag calendar.</div>';
            }
            clearRunSelection("Failed to load the full AutoTag log.");
        }
    }

    function hasLiveDetailPayload(job) {
        return Array.isArray(job?.statusHistory)
            || Array.isArray(job?.logs)
            || typeof job?.logs === "string";
    }

    function getLiveDetailRefreshIntervalMs(status) {
        return String(status || "").toLowerCase() === STATUS_RUNNING
            ? runningDetailRefreshMs
            : idleDetailRefreshMs;
    }

    async function refreshHistoryIfSelectedLiveRun(force = false) {
        if (!state.selectedRunId || state.selectedRunId !== state.liveJobId || !hasActiveLiveRun()) {
            return;
        }

        const now = Date.now();
        const refreshIntervalMs = getLiveDetailRefreshIntervalMs(state.liveJobSummary?.status);
        if (!force && (now - state.lastLiveDetailsFetchedAt) < refreshIntervalMs) {
            return;
        }

        try {
            await loadLiveRunDetails(state.selectedRunId);
        } catch (error) {
            console.warn("Failed to refresh selected live run", error);
        }
    }

    function shouldHydrateLiveRunDetails(job) {
        if (!job?.id || !hasActiveLiveRun()) {
            return false;
        }

        if (document.hidden) {
            return false;
        }

        if (isAutoTagStatusTabActive()) {
            return true;
        }

        if (shouldFollowLiveRunInHistory()) {
            return true;
        }

        return normalizeRunId(state.selectedRunId) === normalizeRunId(job.id);
    }

    async function tryHydrateLiveRunDetails(job) {
        if (!shouldHydrateLiveRunDetails(job)) {
            return;
        }

        const refreshIntervalMs = getLiveDetailRefreshIntervalMs(job?.status);
        if ((Date.now() - state.lastLiveDetailsFetchedAt) < refreshIntervalMs) {
            return;
        }

        try {
            const detailed = await fetchJson(`/api/autotag/jobs/${encodeURIComponent(job.id)}?includeLogs=true&includeStatusHistory=true`);
            state.lastLiveDetailsFetchedAt = Date.now();
            await applyPolledJob(detailed, normalizeLogLines(detailed.logs));
        } catch (error) {
            console.warn("Failed to hydrate detailed live AutoTag payload", error);
        }
    }

    async function pollJob() {
        try {
            const cachedJobId = localStorage.getItem(JOB_KEY) || "";
            const latestJob = await tryFetchLatestJob();
            if (latestJob?.id) {
                syncPolledJobId(cachedJobId, latestJob.id);
                await applyPolledJob(latestJob, normalizeLogLines(latestJob.logs));
                await tryHydrateLiveRunDetails(latestJob);
                schedulePoll();
                return;
            }

            if (!cachedJobId) {
                resetLivePollingState();
                schedulePoll();
                return;
            }

            try {
                const job = await fetchJson(`/api/autotag/jobs/${encodeURIComponent(cachedJobId)}?includeLogs=false&includeStatusHistory=false`);
                const hydratedJob = { ...job, id: job?.id || cachedJobId };
                await applyPolledJob(hydratedJob, normalizeLogLines(job.logs));
                await tryHydrateLiveRunDetails(hydratedJob);
            } catch (error) {
                console.warn("Failed to refresh live AutoTag status.", error);
                if (error?.status === 404) {
                    localStorage.removeItem(JOB_KEY);
                    resetLivePollingState();
                } else {
                    updateStatus(cachedJobId, STATUS_ERROR);
                    updateProgressBar({ status: STATUS_FAILED });
                }
            }

            schedulePoll();
        } catch (error) {
            console.warn("AutoTag polling loop failed; retrying.", error);
            schedulePoll();
        }
    }

    async function tryFetchLatestJob() {
        try {
            return await fetchJson("/api/autotag/jobs/latest?includeLogs=false&includeStatusHistory=false");
        } catch {
            // ignore and fall back to cached job
            return null;
        }
    }

    function syncPolledJobId(currentJobId, nextJobId) {
        if (!nextJobId || currentJobId === nextJobId) {
            return currentJobId;
        }

        localStorage.setItem(JOB_KEY, nextJobId);
        return nextJobId;
    }

    function resetLivePollingState() {
        state.liveJobId = null;
        state.liveJobSummary = null;
        state.lastLiveDetailsFetchedAt = 0;
        updateStatus("Idle", "waiting");
        updateLogs([]);
        updateLiveMetadata(null);
        updateProgressBar(null);
    }

    async function applyPolledJob(job, logs) {
        state.liveJobId = job?.id || null;
        state.liveJobSummary = buildSummaryFromLiveJob(job);
        updateStatus(job.id, job.status);
        const hasLogsPayload = Array.isArray(job?.logs) || typeof job?.logs === "string";
        if (hasLogsPayload) {
            updateLogs(logs);
        }
        updateLiveMetadata(hasLogsPayload ? { ...job, logs } : job);
        updateProgressBar(job);
        const hasDetails = hasLiveDetailPayload(job);
        if (shouldFollowLiveRunInHistory() && hasDetails) {
            renderLiveRunSelection(job, logs);
        } else if (hasActiveLiveRun() && isHistoryTabActive() && isTodayDateToken(state.selectedDate)) {
            renderRunList(state.archivedRuns);
        }
        if (hasActiveLiveRun() && hasDetails) {
            syncSelectedRunWithLiveJob(job, logs);
        }
        if (job.status === STATUS_RUNNING && !shouldFollowLiveRunInHistory() && hasDetails) {
            await refreshHistoryIfSelectedLiveRun();
        }
    }

    function syncSelectedRunWithLiveJob(job, logs) {
        if (!state.selectedRunId || state.selectedRunId !== job.id) {
            return;
        }

        state.historyStatus = Array.isArray(job.statusHistory)
            ? job.statusHistory.slice().reverse()
            : [];
        const liveSummary = buildSummaryFromLiveJob(job);
        renderRunSummary(liveSummary, {
            summary: liveSummary,
            statusHistory: job.statusHistory || [],
            logs
        });
        updateFilterCountsFromHistory();
        renderFilteredHistory();
    }

    function schedulePoll() {
        if (pollTimer) {
            clearTimeout(pollTimer);
        }
        pollTimer = setTimeout(pollJob, pollIntervalMs);
    }

    async function refreshNow(options = {}) {
        if (!hasStatusUI()) {
            return;
        }

        if (pollTimer) {
            clearTimeout(pollTimer);
            pollTimer = null;
        }

        if (options.resetLiveFollow === true) {
            state.manualHistorySelection = false;
        }

        if (options.loadCalendar === true) {
            await loadCalendar();
        }

        await pollJob();
    }

    function bindPageResumeRefresh() {
        globalThis.addEventListener("focus", () => {
            if (!document.hidden) {
                void refreshNow({ loadCalendar: isHistoryTabActive() });
            }
        });

        globalThis.addEventListener("pageshow", () => {
            if (!document.hidden) {
                void refreshNow({ loadCalendar: isHistoryTabActive() });
            }
        });

        document.addEventListener("visibilitychange", () => {
            if (!document.hidden) {
                void refreshNow({ loadCalendar: isHistoryTabActive() });
            }
        });
    }

    function bindFilterButtons() {
        document.querySelectorAll(".autotag-filter-toolbar button[data-filter]").forEach((btn) => {
            btn.addEventListener("click", () => setFilter(btn.dataset.filter || "all"));
        });
    }

    function bindDiffActions() {
        const tableBody = el("autotag-status-table");
        if (!tableBody) {
            return;
        }

        tableBody.addEventListener("click", (event) => {
            const button = event.target.closest(".autotag-diff-btn");
            if (!button) {
                return;
            }

            const encodedPath = button.dataset.path || "";
            const encodedPlatform = button.dataset.platform || "";
            if (!encodedPath || !encodedPlatform) {
                return;
            }

            showTagDiff(decodeURIComponent(encodedPath), decodeURIComponent(encodedPlatform));
        });
    }

    function bindHistoryNavigation() {
        el("autotag-history-prev-month")?.addEventListener("click", async () => {
            state.calendarMonth = new Date(state.calendarMonth.getFullYear(), state.calendarMonth.getMonth() - 1, 1);
            state.manualHistorySelection = true;
            await loadCalendar();
        });

        el("autotag-history-next-month")?.addEventListener("click", async () => {
            state.calendarMonth = new Date(state.calendarMonth.getFullYear(), state.calendarMonth.getMonth() + 1, 1);
            state.manualHistorySelection = true;
            await loadCalendar();
        });

        el("autotag-history-calendar")?.addEventListener("click", async (event) => {
            const button = event.target.closest(".autotag-calendar-day[data-date]");
            if (!button) {
                return;
            }

            await loadRunsForDate(button.dataset.date, { manual: true });
        });

        el("autotag-history-run-list")?.addEventListener("click", async (event) => {
            const button = event.target.closest(".autotag-run-item[data-run-id]");
            if (!button) {
                return;
            }

            state.manualHistorySelection = true;
            await loadRunDetails(button.dataset.runId);
        });
    }

    function normalizeCompareValue(value) {
        if (Array.isArray(value)) {
            return value.map((item) => String(item || "").trim()).filter(Boolean).join("|").toLowerCase();
        }
        if (value === null || value === undefined || value === "") {
            return "";
        }
        if (typeof value === "boolean") {
            return value ? "true" : "false";
        }
        return String(value).trim().toLowerCase();
    }

    function formatValue(value) {
        if (Array.isArray(value)) {
            const items = value.map((item) => String(item || "").trim()).filter(Boolean);
            return items.length ? items.join(", ") : "--";
        }
        if (value === null || value === undefined || value === "") {
            return "--";
        }
        if (typeof value === "boolean") {
            return value ? "Yes" : "No";
        }
        return String(value);
    }

    function normalizeTagMap(tags) {
        const map = new Map();
        if (!tags) {
            return map;
        }
        Object.entries(tags).forEach(([key, values]) => {
            map.set(String(key).toLowerCase(), {
                label: key,
                values: Array.isArray(values) ? values : []
            });
        });
        return map;
    }

    function formatRetainedSource(value) {
        if (!value) {
            return "--";
        }
        if (String(value).toLowerCase() === "original") {
            return "Original file";
        }
        return value;
    }

    function enableDiffColumnResizing(table) {
        if (!table) {
            return;
        }

        const headerCells = Array.from(table.querySelectorAll("thead th"));
        const cols = Array.from(table.querySelectorAll("colgroup col"));
        if (headerCells.length < 2 || cols.length !== headerCells.length) {
            return;
        }

        queueDiffTableResizeInitialization(table, headerCells, cols);
    }

    function queueDiffTableResizeInitialization(table, headerCells, cols) {
        let attempts = 0;
        const maxAttempts = 20;
        const waitForLayout = () => {
            attempts += 1;
            if (initializeDiffTableResizing(table, headerCells, cols)) {
                return;
            }
            if (attempts < maxAttempts) {
                globalThis.requestAnimationFrame(waitForLayout);
            }
        };
        globalThis.requestAnimationFrame(waitForLayout);
    }

    function initializeDiffTableResizing(table, headerCells, cols) {
        const tableRect = table.getBoundingClientRect();
        if (tableRect.width <= 0) {
            return false;
        }

        const measured = headerCells.map((cell) => Math.max(80, Math.round(cell.getBoundingClientRect().width)));
        const totalWidth = measured.reduce((sum, value) => sum + value, 0);
        cols.forEach((col, index) => {
            col.style.width = `${measured[index]}px`;
        });
        table.style.width = `${Math.max(640, totalWidth)}px`;
        table.style.tableLayout = "fixed";

        const minWidths = measured.map((value, index) => {
            if (index === 0) {
                return 140;
            }
            if (index === measured.length - 1) {
                return 100;
            }
            return 90;
        });

        headerCells.forEach((header, index) => {
            bindDiffHeaderResizeHandle(header, index, headerCells, cols, minWidths);
        });

        return true;
    }

    function bindDiffHeaderResizeHandle(header, index, headerCells, cols, minWidths) {
        if (index >= headerCells.length - 1 || header.dataset.colResizableBound === "1") {
            return;
        }

        header.dataset.colResizableBound = "1";
        header.classList.add("autotag-col-resizable");

        const handle = createDiffResizeHandle();
        header.appendChild(handle);

        const startResize = (event) => beginDiffColumnResize(event, index, header, headerCells, cols, minWidths);
        header.addEventListener("pointerdown", (event) => {
            if (!isDiffHeaderEdgePointerDown(event, header)) {
                return;
            }
            startResize(event);
        });
        handle.addEventListener("pointerdown", startResize);
    }

    function createDiffResizeHandle() {
        const handle = document.createElement("button");
        handle.type = "button";
        handle.className = "autotag-col-resize-handle";
        handle.setAttribute("aria-hidden", "true");
        handle.setAttribute("tabindex", "-1");
        return handle;
    }

    function isDiffHeaderEdgePointerDown(event, header) {
        if (event.button !== 0) {
            return false;
        }
        const rect = header.getBoundingClientRect();
        const edgeHotZone = 18;
        return (rect.right - event.clientX) <= edgeHotZone;
    }

    function beginDiffColumnResize(event, index, header, headerCells, cols, minWidths) {
        if (event.button !== 0) {
            return;
        }

        event.preventDefault();
        const currentWidths = cols.map((col, colIndex) => {
            const explicit = Number.parseFloat(col.style.width);
            return Number.isFinite(explicit) && explicit > 0
                ? explicit
                : Math.max(80, headerCells[colIndex]?.getBoundingClientRect().width || 80);
        });
        const startX = event.clientX;
        const leftStart = currentWidths[index];
        const rightStart = currentWidths[index + 1];
        const leftMin = minWidths[index];
        const rightMin = minWidths[index + 1];
        document.body.classList.add("autotag-col-resizing");

        const resizeState = {
            startX,
            leftStart,
            rightStart,
            leftMin,
            rightMin,
            cols,
            index
        };
        const onPointerMove = (moveEvent) => applyDiffColumnResize(moveEvent, resizeState);
        const onPointerUp = () => endDiffColumnResize(onPointerMove, onPointerUp);
        globalThis.addEventListener("pointermove", onPointerMove);
        globalThis.addEventListener("pointerup", onPointerUp);
        globalThis.addEventListener("pointercancel", onPointerUp);
    }

    function applyDiffColumnResize(moveEvent, resizeState) {
        const { startX, leftStart, rightStart, leftMin, rightMin, cols, index } = resizeState;
        const delta = moveEvent.clientX - startX;
        let leftWidth = leftStart + delta;
        let rightWidth = rightStart - delta;

        if (leftWidth < leftMin) {
            const deficit = leftMin - leftWidth;
            leftWidth += deficit;
            rightWidth -= deficit;
        }
        if (rightWidth < rightMin) {
            const deficit = rightMin - rightWidth;
            rightWidth += deficit;
            leftWidth -= deficit;
        }

        leftWidth = Math.max(leftMin, leftWidth);
        rightWidth = Math.max(rightMin, rightWidth);

        cols[index].style.width = `${Math.round(leftWidth)}px`;
        cols[index + 1].style.width = `${Math.round(rightWidth)}px`;
    }

    function endDiffColumnResize(onPointerMove, onPointerUp) {
        document.body.classList.remove("autotag-col-resizing");
        globalThis.removeEventListener("pointermove", onPointerMove);
        globalThis.removeEventListener("pointerup", onPointerUp);
        globalThis.removeEventListener("pointercancel", onPointerUp);
    }

    function buildDiffRows(before, after, diff) {
        const rows = [];
        const beforeMeta = before?.meta || {};
        const afterMeta = after?.meta || {};
        const retainedSources = diff?.retainedSources || {};
        const metaFields = [
            { key: "title", label: "Title" },
            { key: "artists", label: "Artists" },
            { key: "album", label: "Album" },
            { key: "albumArtists", label: "Album Artists" },
            { key: "composers", label: "Composers" },
            { key: "trackNumber", label: "Track #" },
            { key: "trackTotal", label: "Track Total" },
            { key: "discNumber", label: "Disc #" },
            { key: "discTotal", label: "Disc Total" },
            { key: "genres", label: "Genres" },
            { key: "bpm", label: "BPM" },
            { key: "rating", label: "Rating" },
            { key: "year", label: "Year" },
            { key: "key", label: "Key" },
            { key: "isrc", label: "ISRC" },
            { key: "hasArtwork", label: "Has Artwork" },
            { key: "artworkDescription", label: "Artwork Description" },
            { key: "artworkType", label: "Artwork Type" }
        ];

        metaFields.forEach((field) => {
            const beforeVal = beforeMeta[field.key];
            const afterVal = afterMeta[field.key];
            if (normalizeCompareValue(beforeVal) !== normalizeCompareValue(afterVal)) {
                rows.push({
                    label: field.label,
                    before: formatValue(beforeVal),
                    after: formatValue(afterVal),
                    retainedSource: retainedSources[field.key] || null
                });
            }
        });

        const beforeTags = normalizeTagMap(before?.tags);
        const afterTags = normalizeTagMap(after?.tags);
        const tagKeys = new Set([...beforeTags.keys(), ...afterTags.keys()]);
        tagKeys.forEach((key) => {
            const beforeEntry = beforeTags.get(key);
            const afterEntry = afterTags.get(key);
            const beforeVal = beforeEntry?.values ?? [];
            const afterVal = afterEntry?.values ?? [];
            if (normalizeCompareValue(beforeVal) !== normalizeCompareValue(afterVal)) {
                rows.push({
                    label: `Tag: ${afterEntry?.label || beforeEntry?.label || key}`,
                    before: formatValue(beforeVal),
                    after: formatValue(afterVal),
                    retainedSource: retainedSources[`tag:${String(key).toLowerCase()}`] || null
                });
            }
        });

        return rows;
    }

    function buildDiffContent(diff) {
        const container = document.createElement("div");
        container.className = "autotag-diff-panel";

        if (diff?.path) {
            const pathEl = document.createElement("div");
            pathEl.className = "autotag-diff-path";
            pathEl.textContent = diff.path;
            container.appendChild(pathEl);
        }

        if (diff?.targetPlatform) {
            const summaryEl = document.createElement("div");
            summaryEl.className = "autotag-diff-empty";
            summaryEl.textContent = diff?.isFinalPlatformDiff
                ? `Final comparison: ${diff?.basePlatform || "original"} -> ${diff.targetPlatform}.`
                : `Platform comparison: ${diff?.basePlatform || "previous"} -> ${diff.targetPlatform}.`;
            container.appendChild(summaryEl);
        }

        const notes = [];
        if (!diff?.before) {
            notes.push("Before snapshot not captured.");
        }
        if (!diff?.after) {
            notes.push("After snapshot not captured.");
        }
        if (notes.length) {
            const noteEl = document.createElement("div");
            noteEl.className = "autotag-diff-empty";
            noteEl.textContent = notes.join(" ");
            container.appendChild(noteEl);
        }

        const rows = buildDiffRows(diff?.before, diff?.after, diff);
        if (!rows.length) {
            const empty = document.createElement("div");
            empty.className = "autotag-diff-empty";
            empty.textContent = "No tag changes captured for this track.";
            container.appendChild(empty);
            return container;
        }

        const showRetainedColumn = rows.some((row) => !!row.retainedSource);
        const table = document.createElement("table");
        table.className = "autotag-diff-table";
        const colgroup = document.createElement("colgroup");
        const colCount = showRetainedColumn ? 4 : 3;
        for (let index = 0; index < colCount; index += 1) {
            colgroup.appendChild(document.createElement("col"));
        }
        table.appendChild(colgroup);
        const thead = document.createElement("thead");
        thead.innerHTML = showRetainedColumn
            ? "<tr><th>Field</th><th>Before</th><th>After</th><th>Retained Source</th></tr>"
            : "<tr><th>Field</th><th>Before</th><th>After</th></tr>";
        table.appendChild(thead);
        const tbody = document.createElement("tbody");
        rows.forEach((row) => {
            const tr = document.createElement("tr");
            const tdLabel = document.createElement("td");
            const tdBefore = document.createElement("td");
            const tdAfter = document.createElement("td");
            tdLabel.textContent = row.label;
            tdBefore.textContent = row.before;
            tdAfter.textContent = row.after;
            tr.appendChild(tdLabel);
            tr.appendChild(tdBefore);
            tr.appendChild(tdAfter);
            if (showRetainedColumn) {
                const tdRetained = document.createElement("td");
                tdRetained.textContent = formatRetainedSource(row.retainedSource);
                tr.appendChild(tdRetained);
            }
            tbody.appendChild(tr);
        });
        table.appendChild(tbody);
        const tableWrap = document.createElement("div");
        tableWrap.className = "autotag-diff-table-wrap";
        tableWrap.appendChild(table);
        container.appendChild(tableWrap);
        enableDiffColumnResizing(table);

        return container;
    }

    async function showTagDiff(path, platform) {
        const jobId = state.selectedRunId || state.liveJobId || localStorage.getItem(JOB_KEY);
        if (!jobId) {
            showToast("No AutoTag job available for diff.", "warning");
            return;
        }

        let diff = null;
        let diffErrorMessage = "";
        try {
            const query = new URLSearchParams({ path });
            if (platform) {
                query.set("platform", platform);
            }
            const response = await fetch(`/api/autotag/jobs/${encodeURIComponent(jobId)}/tag-diff?${query.toString()}`);
            if (response.ok) {
                diff = await response.json();
            } else {
                const payload = await tryReadTagDiffPayload(response);
                diffErrorMessage = buildTagDiffErrorMessage(response.status, payload);
            }
        } catch (error) {
            console.warn("Failed to load AutoTag diff", error);
            diffErrorMessage = "Failed to load tag diff due to a network or server error.";
        }

        if (!diff) {
            if (globalThis.DeezSpoTag?.ui?.showModal) {
                await globalThis.DeezSpoTag.ui.showModal({
                    title: "Tag diff unavailable",
                    message: diffErrorMessage || "No before/after snapshot is available for this track.",
                    buttons: [{ label: "Close", value: true, primary: true }]
                });
            } else {
                showToast(diffErrorMessage || "No tag diff available for this track.", "warning");
            }
            return;
        }

        const content = buildDiffContent(diff);
        if (globalThis.DeezSpoTag?.ui?.showModal) {
            await globalThis.DeezSpoTag.ui.showModal({
                title: `Tag diff: ${toFileName(path)}`,
                message: "",
                dialogClass: "is-resizable",
                contentElement: content,
                buttons: [{ label: "Close", value: true, primary: true }]
            });
        }
    }

    async function tryReadTagDiffPayload(response) {
        try {
            return await response.json();
        } catch {
            return null;
        }
    }

    function buildTagDiffErrorMessage(statusCode, payload) {
        if (statusCode === 404) {
            return payload?.message
                || "No before/after tag snapshot was captured for this track in the selected run.";
        }
        if (statusCode === 400) {
            return payload?.message
                || payload?.error
                || "Track path is outside configured AutoTag roots.";
        }
        if (statusCode === 401 || statusCode === 403) {
            return "You are not authorized to load tag diff data.";
        }

        return payload?.message
            || payload?.error
            || `Failed to load tag diff (HTTP ${statusCode}).`;
    }

    document.addEventListener("DOMContentLoaded", async () => {
        if (!hasStatusUI()) {
            return;
        }

        globalThis.DeezSpoTagAutoTagStatus = {
            refresh: (options) => refreshNow(options)
        };

        bindFilterButtons();
        bindDiffActions();
        bindHistoryNavigation();
        bindPageResumeRefresh();
        void pollJob();
        void loadCalendar();
    });
})();
