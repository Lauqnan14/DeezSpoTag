// Enhancement resume actions for the Activities "Runs on" card.
// The run list (autotag-status.js) renders Resume/Cancel buttons inline beside the
// job id of every paused/interrupted run; this module is their single action path.
// There is deliberately no banner and no alternative mount point.
(function () {
    "use strict";

    function showToastSafe(message, kind) {
        if (typeof window.showToast === "function") {
            window.showToast(message, kind);
        }
    }

    // Resumes a paused/interrupted run from its checkpoint. The server remains the
    // gatekeep: it refuses while downloads or enrichment occupy the pipeline.
    async function resumeJob(jobId) {
        var id = String(jobId || "").trim();
        if (!id) {
            return { ok: false };
        }

        try {
            var response = await fetch("/api/autotag/jobs/" + encodeURIComponent(id) + "/resume", {
                method: "POST",
                headers: { "Content-Type": "application/json" }
            });
            var payload = await response.json().catch(function () { return null; });
            if (!response.ok) {
                showToastSafe((payload && (payload.error || payload.message)) || "Resume failed.", "error");
                return { ok: false };
            }

            var resumedId = payload && payload.resumedJobId;
            if (resumedId) {
                try { localStorage.setItem("autotagJobId", String(resumedId)); } catch (e) { /* storage unavailable */ }
            }
            showToastSafe("Enhancement run resumed.", "success");
            document.dispatchEvent(new CustomEvent("enhancement-resumed", { detail: { jobId: id, resumedJobId: resumedId } }));
            return { ok: true, resumedJobId: resumedId };
        } catch (error) {
            showToastSafe("Resume failed: " + (error && error.message ? error.message : error), "error");
            return { ok: false };
        }
    }

    // Cancels a paused/interrupted run outright. The server applies the canceled
    // status (a paused run holds no live execution to stop).
    async function cancelJob(jobId) {
        var id = String(jobId || "").trim();
        if (!id) {
            return { ok: false };
        }

        try {
            var response = await fetch("/api/autotag/jobs/" + encodeURIComponent(id) + "/stop", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ actor: "user" })
            });
            var payload = await response.json().catch(function () { return null; });
            if (!response.ok) {
                showToastSafe((payload && (payload.error || payload.message)) || "Cancel failed.", "error");
                return { ok: false };
            }

            showToastSafe("Enhancement run canceled.", "success");
            document.dispatchEvent(new CustomEvent("enhancement-run-canceled", { detail: { jobId: id } }));
            return { ok: true, status: payload && payload.status };
        } catch (error) {
            showToastSafe("Cancel failed: " + (error && error.message ? error.message : error), "error");
            return { ok: false };
        }
    }

    // Called by pollers when a monitored run reaches a paused/interrupted state.
    // The actions already render inline in the "Runs on" card entry; this only
    // nudges the user once per run.
    var announcedRuns = Object.create(null);
    function offerResume(jobId, job) {
        var id = String(jobId || "").trim();
        var status = String((job && (job.status || job.Status)) || "").trim().toLowerCase();
        if (!id || announcedRuns[id] || (status !== "paused" && status !== "interrupted")) {
            return;
        }

        announcedRuns[id] = true;
        showToastSafe("Enhancement run " + status + ". Use Resume in the Runs list.", "warning");
    }

    window.EnhancementResume = {
        resumeJob: resumeJob,
        cancelJob: cancelJob,
        offerResume: offerResume
    };
})();
