// Enhancement run resume: page-load re-attach + resume banner for enhancement jobs
// that ended paused/interrupted/failed. Single shared module used by the AutoTag page
// and the Activities page. Backing endpoint: POST /api/autotag/jobs/{id}/resume.
(function () {
    "use strict";

    var RESUMABLE_STATUSES = ["paused", "interrupted", "failed"];
    var dismissedInSession = {};

    function normalize(value) {
        return String(value || "").trim().toLowerCase();
    }

    function isResumable(job) {
        if (!job) {
            return false;
        }
        var status = normalize(job.status || job.Status);
        if (RESUMABLE_STATUSES.indexOf(status) === -1) {
            return false;
        }
        // A job without a checkpoint cannot resume mid-run; it needs a fresh start.
        return Boolean(job.resumeCheckpoint || job.ResumeCheckpoint);
    }

    function removeBanner() {
        var existing = document.getElementById("enhancement-resume-banner");
        if (existing) {
            existing.remove();
        }
    }

    function showToastSafe(message, kind) {
        if (typeof window.showToast === "function") {
            window.showToast(message, kind);
        }
    }

    function showBanner(jobId, job) {
        removeBanner();
        var banner = document.createElement("div");
        banner.id = "enhancement-resume-banner";
        banner.style.cssText = "display:flex;align-items:center;gap:12px;flex-wrap:wrap;"
            + "padding:10px 14px;margin:10px 0;border:1px solid var(--accent, #f5a623);border-radius:8px;"
            + "background:var(--surface-2, rgba(255,255,255,0.04));font-size:14px;";

        var scope = String(job.rootPath || job.RootPath || "").trim();
        var feature = String(job.enhancementFeature || job.EnhancementFeature || "").trim();
        var status = normalize(job.status || job.Status);
        var label = document.createElement("span");
        label.textContent = "Enhancement run " + status
            + (scope ? " for " + scope : "")
            + (feature ? " (" + feature + ")" : "") + ".";

        var resumeBtn = document.createElement("button");
        resumeBtn.type = "button";
        resumeBtn.style.cssText = "padding:4px 12px;cursor:pointer;";
        resumeBtn.textContent = "Resume";
        resumeBtn.addEventListener("click", async function () {
            resumeBtn.disabled = true;
            resumeBtn.textContent = "Resuming…";
            try {
                var response = await fetch("/api/autotag/jobs/" + encodeURIComponent(jobId) + "/resume", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" }
                });
                var payload = await response.json().catch(function () { return null; });
                if (!response.ok) {
                    resumeBtn.disabled = false;
                    resumeBtn.textContent = "Resume";
                    showToastSafe((payload && (payload.error || payload.message)) || "Resume failed.", "error");
                    return;
                }
                var resumedId = payload && payload.resumedJobId;
                if (resumedId) {
                    try { localStorage.setItem("autotagJobId", String(resumedId)); } catch (e) { /* storage unavailable */ }
                }
                removeBanner();
                showToastSafe("Enhancement run resumed.", "success");
                document.dispatchEvent(new CustomEvent("enhancement-resumed", { detail: { jobId: jobId, resumedJobId: resumedId } }));
            } catch (error) {
                resumeBtn.disabled = false;
                resumeBtn.textContent = "Resume";
                showToastSafe("Resume failed: " + (error && error.message ? error.message : error), "error");
            }
        });

        var dismissBtn = document.createElement("button");
        dismissBtn.type = "button";
        dismissBtn.style.cssText = "padding:4px 12px;cursor:pointer;";
        dismissBtn.textContent = "Dismiss";
        dismissBtn.addEventListener("click", function () {
            dismissedInSession[jobId] = true;
            try { localStorage.removeItem("autotagJobId"); } catch (e) { /* storage unavailable */ }
            removeBanner();
        });

        banner.appendChild(label);
        banner.appendChild(resumeBtn);
        banner.appendChild(dismissBtn);

        var anchor = document.getElementById("content");
        if (anchor && anchor.firstChild) {
            anchor.insertBefore(banner, anchor.firstChild);
        } else {
            document.body.insertBefore(banner, document.body.firstChild);
        }
    }

    async function checkJob(jobId) {
        var id = String(jobId || "").trim();
        if (!id || dismissedInSession[id]) {
            return;
        }
        try {
            var response = await fetch("/api/autotag/jobs/" + encodeURIComponent(id)
                + "?includeLogs=false&includeStatusHistory=false");
            if (!response.ok) {
                return;
            }
            var job = await response.json().catch(function () { return null; });
            if (isResumable(job)) {
                showBanner(id, job);
            }
        } catch (error) {
            console.debug("Enhancement resume check failed.", error);
        }
    }

    function checkStoredJob() {
        var storedId = "";
        try { storedId = String(localStorage.getItem("autotagJobId") || "").trim(); } catch (e) { /* storage unavailable */ }
        if (storedId) {
            checkJob(storedId);
        }
    }

    // Called by pollers when a monitored job reaches a non-running terminal state.
    function offerResume(jobId, job) {
        var id = String(jobId || "").trim();
        if (!id || dismissedInSession[id] || !isResumable(job)) {
            return;
        }
        showBanner(id, job);
    }

    function init() {
        if (document.readyState === "loading") {
            document.addEventListener("DOMContentLoaded", checkStoredJob);
        } else {
            checkStoredJob();
        }
    }

    init();

    window.EnhancementResume = {
        checkJob: checkJob,
        offerResume: offerResume
    };
})();