(function () {
    "use strict";

    const ENABLED_KEY = "tabs-preference-enabled";
    const STORAGE_PREFIX = "tabs:last:";
    const TAB_SELECTOR = "[data-bs-toggle=\"tab\"]";
    const TAB_LIST_SELECTOR = ".nav-tabs, [role=\"tablist\"]";
    const OPT_OUT_ATTRIBUTE = "data-no-global-tab-fallback";
    // Per-trigger opt-out. A tab list is remembered as a unit, but a single tab inside it
    // can be excluded when it is not a restorable destination: the Search page's Soulseek
    // pane is a peer-to-peer transfer panel rather than a catalogue source, so it must not
    // be written as the remembered source nor restored from one. Without this, selecting
    // Soulseek stores its pane as the remembered tab, and on the next load that value no
    // longer maps to a real search source, so the last genuine source is lost.
    const TRIGGER_OPT_OUT_ATTRIBUTE = "data-no-tab-persist";

    function isTriggerOptedOut(trigger) {
        return trigger?.hasAttribute?.(TRIGGER_OPT_OUT_ATTRIBUTE) === true;
    }

    function isRememberEnabled() {
        try {
            const stored = globalThis.localStorage?.getItem(ENABLED_KEY);
            return stored === null || stored === "" || stored === "true";
        } catch (error) {
            logDebug("read tab preference flag", error);
            return true;
        }
    }

    function getTabList(trigger) {
        return trigger?.closest?.(TAB_LIST_SELECTOR) || null;
    }

    function isOptedOut(tabList) {
        return tabList?.hasAttribute?.(OPT_OUT_ATTRIBUTE) === true;
    }

    function getTabListId(tabList) {
        return String(tabList?.id || "").trim();
    }

    function getStorageKey(tabList) {
        return buildStorageKeyForId(getTabListId(tabList));
    }

    // Single source of truth for the remembered-tab storage key.
    function buildStorageKeyForId(tabListId) {
        const id = String(tabListId || "").trim();
        if (!id) {
            return "";
        }

        return `${STORAGE_PREFIX}${globalThis.location.pathname}:${id}`;
    }

    function getTargetSelector(trigger) {
        return String(trigger?.getAttribute?.("data-bs-target") || trigger?.getAttribute?.("href") || "").trim();
    }

    function getRestorableTrigger(tabList, targetSelector) {
        if (!tabList || !targetSelector) {
            return null;
        }

        const candidates = Array.from(tabList.querySelectorAll(TAB_SELECTOR));
        return candidates.find((trigger) => {
            if (trigger.disabled || trigger.classList.contains("disabled")) {
                return false;
            }

            // An opted-out trigger is never a restore destination either, so a value
            // stored before it was marked cannot resurrect it.
            if (isTriggerOptedOut(trigger)) {
                return false;
            }

            return getTargetSelector(trigger) === targetSelector;
        }) || null;
    }

    function normalizeRequestedTarget(value) {
        const requested = String(value || "").trim();
        if (!requested) {
            return "";
        }

        return requested.startsWith("#") ? requested : `#${requested}`;
    }

    function getRequestedTargetSelector() {
        try {
            const fromQuery = new URLSearchParams(globalThis.location.search || "").get("tab");
            const targetFromQuery = normalizeRequestedTarget(fromQuery);
            if (targetFromQuery) {
                return targetFromQuery;
            }

            return normalizeRequestedTarget(String(globalThis.location.hash || "").replace(/^#/, ""));
        } catch (error) {
            logDebug("read requested tab", error);
            return "";
        }
    }

    function hasExplicitRequestedTab(tabList) {
        const requestedTarget = getRequestedTargetSelector();
        return Boolean(requestedTarget && getRestorableTrigger(tabList, requestedTarget));
    }

    function rememberTab(trigger) {
        const tabList = getTabList(trigger);
        const storageKey = getStorageKey(tabList);
        const targetSelector = getTargetSelector(trigger);
        if (!storageKey || !targetSelector || isOptedOut(tabList) || isTriggerOptedOut(trigger)) {
            return;
        }

        try {
            if (!isRememberEnabled()) {
                globalThis.localStorage?.removeItem(storageKey);
                globalThis.UserPrefs?.setTabSelection?.(storageKey, "");
                return;
            }

            globalThis.localStorage?.setItem(storageKey, targetSelector);
            globalThis.UserPrefs?.setTabSelection?.(storageKey, targetSelector);
        } catch (error) {
            logDebug("persist tab preference", error);
        }
    }

    function restoreTabList(tabList) {
        const storageKey = getStorageKey(tabList);
        if (!storageKey || isOptedOut(tabList) || hasExplicitRequestedTab(tabList) || !isRememberEnabled()) {
            return;
        }

        try {
            const targetSelector = globalThis.localStorage?.getItem(storageKey) || "";
            const trigger = getRestorableTrigger(tabList, targetSelector);
            if (!trigger || trigger.classList.contains("active")) {
                return;
            }

            if (globalThis.bootstrap?.Tab) {
                globalThis.bootstrap.Tab.getOrCreateInstance(trigger).show();
                return;
            }

            trigger.click();
        } catch (error) {
            logDebug("restore tab preference", error);
        }
    }

    function restoreAllTabs() {
        const tabLists = Array.from(document.querySelectorAll(TAB_LIST_SELECTOR))
            .filter((tabList) => getTabListId(tabList) && tabList.querySelector(TAB_SELECTOR));
        tabLists.forEach(restoreTabList);
    }

    function bindTabPersistence() {
        document.addEventListener("shown.bs.tab", (event) => {
            rememberTab(event.target);
        });

        document.addEventListener("DOMContentLoaded", restoreAllTabs);
    }

    // Shared storage contract for tab groups that drive their own activation and so
    // cannot ride the shown.bs.tab path: the login platform tabs, the soundtrack
    // category tabs and the artist discography tabs. They keep their own activation
    // logic, but the key format, the setting check and the server-side mirror live
    // here so there is a single implementation.
    function readTabPreference(storageKey) {
        if (!storageKey || !isRememberEnabled()) {
            return null;
        }

        try {
            return globalThis.localStorage?.getItem(storageKey) || null;
        } catch (error) {
            logDebug("read tab preference", error);
            return null;
        }
    }

    function writeTabPreference(storageKey, value) {
        if (!storageKey) {
            return;
        }

        const normalized = String(value == null ? "" : value).trim();

        try {
            // Writing an empty value, or writing at all while the setting is off,
            // clears the remembered tab so it cannot come back as a stale default.
            if (!normalized || !isRememberEnabled()) {
                globalThis.localStorage?.removeItem(storageKey);
                globalThis.UserPrefs?.setTabSelection?.(storageKey, "");
                return;
            }

            globalThis.localStorage?.setItem(storageKey, normalized);
            globalThis.UserPrefs?.setTabSelection?.(storageKey, normalized);
        } catch (error) {
            logDebug("persist tab preference", error);
        }
    }

    function logDebug(action, error) {
        if (globalThis.console && typeof globalThis.console.debug === "function") {
            globalThis.console.debug(`[TabPreferences] Failed to ${action}.`, error);
        }
    }

    bindTabPersistence();

    globalThis.TabPreferences = {
        isEnabled: isRememberEnabled,
        keyFor: buildStorageKeyForId,
        read: readTabPreference,
        write: writeTabPreference
    };
})();
