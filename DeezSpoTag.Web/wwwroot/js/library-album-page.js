(() => {
    let runtimeLoadPromise = null;

    function ensureLibraryRuntimeLoaded() {
        if (typeof globalThis.loadAlbum === 'function') {
            return Promise.resolve();
        }
        if (runtimeLoadPromise) {
            return runtimeLoadPromise;
        }
        runtimeLoadPromise = new Promise((resolve, reject) => {
            const script = document.createElement('script');
            script.src = '/js/library.js';
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
