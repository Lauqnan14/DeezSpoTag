const CACHE_NAME = "deezspotag-pwa-v7";
const CORE_ASSETS = [
  "/images/logo.svg",
  "/images/pwa/icon-180.png",
  "/images/pwa/icon-192.png",
  "/images/pwa/icon-512.png",
  "/manifest.webmanifest"
];
const API_STALE_BYPASS_PATTERNS = [
  /^\/api\/(?:download|connect|system-stats|platform-auth|autotag|media-server)\b/i,
  /^\/api\/library\/(?:analysis|scan)\b/i,
  /^\/api\/spotify\/tracklist\/matches\b/i
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

function isTruthyQueryFlag(value) {
  if (!value) {
    return false;
  }
  const normalized = String(value).trim().toLowerCase();
  return normalized === "1" || normalized === "true" || normalized === "yes";
}

function shouldBypassApiCache(requestUrl) {
  if (!requestUrl.pathname.startsWith("/api/")) {
    return true;
  }

  if (isTruthyQueryFlag(requestUrl.searchParams.get("refresh"))
    || isTruthyQueryFlag(requestUrl.searchParams.get("nocache"))
    || isTruthyQueryFlag(requestUrl.searchParams.get("noCache"))
    || isTruthyQueryFlag(requestUrl.searchParams.get("cacheBypass"))) {
    return true;
  }

  return API_STALE_BYPASS_PATTERNS.some((pattern) => pattern.test(requestUrl.pathname));
}

function isCacheableApiResponse(response) {
  if (!response?.ok) {
    return false;
  }

  const cacheControl = response.headers.get("Cache-Control") || "";
  if (cacheControl.toLowerCase().includes("no-store")) {
    return false;
  }

  const contentType = response.headers.get("Content-Type") || "";
  return contentType.toLowerCase().includes("application/json");
}

async function staleWhileRevalidateApi(event) {
  const request = event.request;
  const cache = await caches.open(CACHE_NAME);
  const cached = await cache.match(request);

  const networkPromise = fetch(request)
    .then((response) => {
      if (isCacheableApiResponse(response)) {
        event.waitUntil(cache.put(request, response.clone()));
      }
      return response;
    })
    .catch(() => null);

  if (cached) {
    event.waitUntil(networkPromise);
    return cached;
  }

  const networkResponse = await networkPromise;
  if (networkResponse) {
    return networkResponse;
  }

  return new Response("Offline", { status: 503 });
}

async function networkFirstWithCacheFallback(request) {
  try {
    return await fetch(request);
  } catch {
    const cached = await caches.match(request);
    return cached || new Response("Offline", { status: 503 });
  }
}

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
    if (requestUrl.pathname.startsWith("/api/") && !shouldBypassApiCache(requestUrl)) {
      event.respondWith(staleWhileRevalidateApi(event));
      return;
    }

    event.respondWith(networkFirstWithCacheFallback(event.request));
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
