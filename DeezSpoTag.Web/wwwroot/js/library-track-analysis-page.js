(() => {
    async function loadRuntimeScript() {
        if (typeof globalThis.loadTrackAnalysisPage === 'function') {
            return;
        }
        await new Promise((resolve, reject) => {
            const script = document.createElement('script');
            script.src = '/js/library.js';
            script.async = true;
            script.onload = resolve;
            script.onerror = () => reject(new Error('Failed to load track analysis runtime.'));
            document.head.appendChild(script);
        });
    }

    async function initializeTrackAnalysisPage() {
        if (!document.querySelector('.library-track-analysis-page[data-track-id]')) {
            return;
        }
        try {
            await loadRuntimeScript();
            if (typeof globalThis.loadTrackAnalysisPage === 'function') {
                await globalThis.loadTrackAnalysisPage();
            }
        } catch (error) {
            if (globalThis.DeezSpoTag?.showNotification) {
                globalThis.DeezSpoTag.showNotification(error?.message || 'Failed to load track analysis page.', 'error');
            } else {
                console.error(error);
            }
        }
    }

    document.addEventListener('DOMContentLoaded', () => {
        void initializeTrackAnalysisPage();
    });
})();
