// DeezSpoTag service worker: notifications only.
//
// This worker deliberately registers NO fetch handler. The app serves every response
// with no-store and keeps user state on the server, and the previous caching worker was
// decommissioned in commit 54224db63 because it served stale API JSON. With no fetch
// handler nothing is intercepted or cached, so that class of bug cannot return.
//
// Its only job is to post the "download this track" notification, which is the one
// control the OS media notification cannot render: navigator.mediaSession accepts a
// closed set of actions (play, pause, stop, seek*, previoustrack, nexttrack) with no
// slot for a custom button, so the download action ships as its own notification.

const DOWNLOAD_MESSAGE = "deezspotag:download-current";
const SHOW_NOTIFICATION_MESSAGE = "deezspotag:show-download-notification";

globalThis.addEventListener("install", () => {
  globalThis.skipWaiting();
});

globalThis.addEventListener("activate", (event) => {
  // Claim immediately so the page can postMessage to this worker without a reload.
  event.waitUntil(globalThis.clients.claim());
});

globalThis.addEventListener("message", (event) => {
  if (event?.data?.type !== SHOW_NOTIFICATION_MESSAGE) {
    return;
  }

  const { tag, payload } = event.data;
  event.waitUntil(showDownloadNotification(tag, payload));
});

async function showDownloadNotification(tag, payload) {
  const title = String(payload?.title || "DeezSpoTag").trim();
  const body = String(payload?.body || "").trim();
  if (!title) {
    return;
  }

  try {
    await globalThis.registration.showNotification(title, {
      // A stable tag makes each new track replace the previous notification rather
      // than stacking a new one per play.
      tag: String(tag || "deezspotag-now-playing"),
      body,
      icon: String(payload?.icon || "/images/pwa/icon-192.png"),
      badge: "/images/pwa/icon-192.png",
      renotify: false,
      silent: true,
      actions: [
        {
          action: "download",
          title: "Download"
        }
      ]
    });
  } catch {
    // Notifications can be blocked or rate limited; playback must not be affected.
  }
}

globalThis.addEventListener("notificationclick", (event) => {
  event.notification.close();

  if (event.action !== "download") {
    return;
  }

  // A worker cannot run the app's download pipeline, so hand the request to a live
  // client. Playback keeps a client alive, which is the normal case.
  event.waitUntil(deliverDownloadRequest());
});

async function deliverDownloadRequest() {
  const clientList = await globalThis.clients.matchAll({
    type: "window",
    includeUncontrolled: true
  });

  const existing = clientList.find((client) => "focus" in client);
  if (existing) {
    await existing.focus();
    existing.postMessage({ type: DOWNLOAD_MESSAGE });
    return;
  }

  await globalThis.clients.openWindow("/");
}
