(function () {
    "use strict";

    const ENABLED_KEY = "tabs-preference-enabled";
    const STORAGE_PREFIX = "tabs:last:";
    const TAB_SELECTOR = "[data-bs-toggle=\"tab\"]";
    const TAB_LIST_SELECTOR = ".nav-tabs, [role=\"tablist\"]";
    const OPT_OUT_ATTRIBUTE = "data-no-global-tab-fallback";

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
        const id = getTabListId(tabList);
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

            return getTargetSelector(trigger) === targetSelector;
        }) || null;
    }

    function rememberTab(trigger) {
        const tabList = getTabList(trigger);
        const storageKey = getStorageKey(tabList);
        const targetSelector = getTargetSelector(trigger);
        if (!storageKey || !targetSelector || isOptedOut(tabList)) {
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
        if (!storageKey || isOptedOut(tabList) || !isRememberEnabled()) {
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

    function logDebug(action, error) {
        if (globalThis.console && typeof globalThis.console.debug === "function") {
            globalThis.console.debug(`[TabPreferences] Failed to ${action}.`, error);
        }
    }

    bindTabPersistence();
})();
