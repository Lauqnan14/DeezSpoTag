(() => {
    let runtimeLoadPromise = null;
    const runtimeVersion = resolveRuntimeVersion();

    function resolveRuntimeVersion() {
        const script = document.querySelector('script[src*="/js/library-album-page.js"]');
        if (!script) {
            return '';
        }

        try {
            const sourceUrl = new URL(script.src, globalThis.location?.origin || globalThis.location?.href || 'http://localhost');
            return sourceUrl.searchParams.get('v') || '';
        } catch {
            return '';
        }
    }

    function ensureLibraryRuntimeLoaded() {
        if (typeof globalThis.loadAlbum === 'function') {
            return Promise.resolve();
        }
        if (runtimeLoadPromise) {
            return runtimeLoadPromise;
        }
        runtimeLoadPromise = new Promise((resolve, reject) => {
            const script = document.createElement('script');
            script.src = runtimeVersion
                ? `/js/library.js?v=${encodeURIComponent(runtimeVersion)}`
                : '/js/library.js';
            script.async = true;
            script.onload = () => resolve();
            script.onerror = () => reject(new Error('Failed to load library runtime.'));
            document.head.appendChild(script);
        });
        return runtimeLoadPromise;
    }

    async function bootstrapAlbumPage() {
        const page = document.querySelector('.album-page[data-album-id], .library-album-page[data-album-id]');
        if (!page) {
            return;
        }
        try {
            await ensureLibraryRuntimeLoaded();
            if (typeof globalThis.bindGlobalLibraryInteractionHandlers === 'function') {
                globalThis.bindGlobalLibraryInteractionHandlers();
            }
            if (typeof globalThis.loadAlbum === 'function') {
                await globalThis.loadAlbum();
            }
        } catch (error) {
            if (globalThis.DeezSpoTag?.showNotification) {
                globalThis.DeezSpoTag.showNotification(error?.message || 'Failed to load album page.', 'error');
            } else {
                console.error(error);
            }
        }
    }

    document.addEventListener('DOMContentLoaded', () => {
        void bootstrapAlbumPage();
    });
})();
