// Decommissioned service worker: clear any existing caches and unregister.
globalThis.addEventListener("install", () => {
  globalThis.skipWaiting();
});

globalThis.addEventListener("activate", (event) => {
  event.waitUntil((async () => {
    try {
      const keys = await caches.keys();
      await Promise.all(keys.map((key) => caches.delete(key)));
    } catch {
      // Best effort cleanup.
    }

    try {
      await globalThis.registration.unregister();
    } catch {
      // Best effort cleanup.
    }

    try {
      const clients = await globalThis.clients.matchAll({ type: "window", includeUncontrolled: true });
      clients.forEach((client) => {
        client.navigate(client.url);
      });
    } catch {
      // Best effort cleanup.
    }
  })());
});

globalThis.addEventListener("fetch", () => {
  // Intentionally no-op. Network should flow directly without SW caching.
});
