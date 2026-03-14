const CACHE_NAME = "deezspotag-pwa-v6";
const CORE_ASSETS = [
  "/images/logo.svg",
  "/images/pwa/icon-180.png",
  "/images/pwa/icon-192.png",
  "/images/pwa/icon-512.png",
  "/manifest.webmanifest"
];

globalThis.addEventListener("install", (event) => {
  event.waitUntil(
    caches.open(CACHE_NAME).then((cache) => cache.addAll(CORE_ASSETS))
  );
  globalThis.skipWaiting();
});

globalThis.addEventListener("activate", (event) => {
  event.waitUntil(
    caches.keys().then((keys) =>
      Promise.all(
        keys
          .filter((key) => key !== CACHE_NAME)
          .map((key) => caches.delete(key))
      )
    )
  );
  globalThis.clients.claim();
});

globalThis.addEventListener("fetch", (event) => {
  if (event.request.method !== "GET") {
    return;
  }

  const requestUrl = new URL(event.request.url);
  if (requestUrl.origin !== globalThis.location.origin) {
    return;
  }

  if (event.request.mode === "navigate") {
    event.respondWith(
      fetch(event.request)
        .then((response) => response)
        .catch(() =>
          caches
            .match(event.request)
            .then((cached) => cached || caches.match("/"))
            .then((cached) => cached || new Response("Offline", { status: 503 }))
        )
    );
    return;
  }

  // For API and other dynamic calls, use network first, fallback to cache
  if (requestUrl.pathname.startsWith("/api/") || requestUrl.pathname.includes("/stream")) {
    event.respondWith(
      fetch(event.request).catch(() =>
        caches
          .match(event.request)
          .then((cached) => cached || new Response("Offline", { status: 503 }))
      )
    );
    return;
  }

  const isVersionedAsset = requestUrl.searchParams.has("v");
  if (!isVersionedAsset) {
    event.respondWith(
      fetch(event.request).catch(() =>
        caches.match(event.request).then((cached) => {
          if (cached) {
            return cached;
          }
          if (requestUrl.pathname === "/favicon.ico") {
            return new Response("", {
              status: 404,
              headers: {
                "Content-Type": "image/x-icon"
              }
            });
          }
          return new Response("Offline", { status: 503 });
        })
      )
    );
    return;
  }

  event.respondWith(
    fetch(event.request)
      .then((response) => {
        const copy = response.clone();
        caches.open(CACHE_NAME).then((cache) => cache.put(event.request, copy));
        return response;
      })
      .catch(() =>
        caches
          .match(event.request)
          .then((cached) => cached || new Response("Offline", { status: 503 }))
      )
  );
});
