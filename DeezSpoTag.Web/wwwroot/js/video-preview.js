(function initializeVideoPreview(global) {
    'use strict';

    let hlsLibraryPromise = null;

    function sanitizeUrl(url) {
        const raw = String(url || '').trim();
        if (!raw) return '';
        if (raw.startsWith('/api/tidal/download/videos/preview')) return raw;

        try {
            const parsed = new URL(raw, global.location.origin);
            return parsed.protocol === 'http:' || parsed.protocol === 'https:' ? parsed.href : '';
        } catch {
            return '';
        }
    }

    function loadHlsLibraryAsync() {
        if (global.Hls) return Promise.resolve(global.Hls);
        if (hlsLibraryPromise) return hlsLibraryPromise;

        hlsLibraryPromise = new Promise((resolve, reject) => {
            const script = document.createElement('script');
            script.src = 'https://cdn.jsdelivr.net/npm/hls.js@1.5.17/dist/hls.min.js';
            script.async = true;
            script.onload = () => resolve(global.Hls || null);
            script.onerror = () => reject(new Error('Failed to load HLS player.'));
            document.head.appendChild(script);
        });
        return hlsLibraryPromise;
    }

    async function configureSource(video, safeUrl, onPlaybackError) {
        const isHlsCandidate = safeUrl.includes('.m3u8')
            || safeUrl.includes('/api/tidal/download/videos/preview');
        if (!isHlsCandidate || video.canPlayType('application/vnd.apple.mpegurl')) {
            video.src = safeUrl;
            return;
        }

        const Hls = await loadHlsLibraryAsync();
        if (!Hls?.isSupported?.()) {
            video.src = safeUrl;
            return;
        }

        const hls = new Hls();
        video._hls = hls;
        hls.loadSource(safeUrl);
        hls.attachMedia(video);
        hls.on(Hls.Events.MANIFEST_PARSED, () => video.play().catch(() => {}));
        hls.on(Hls.Events.ERROR, (_event, data) => {
            if (!data?.fatal) return;
            hls.destroy();
            video._hls = null;
            onPlaybackError?.();
        });
    }

    async function play(url, options = {}) {
        const safeUrl = sanitizeUrl(url);
        if (!safeUrl) {
            options.onInvalidUrl?.();
            return;
        }

        const video = document.createElement('video');
        video.controls = true;
        video.autoplay = true;
        video.playsInline = true;
        video.style.width = '100%';
        video.style.maxHeight = '70vh';

        if (global.DeezSpoTag?.ui?.showModal) {
            global.DeezSpoTag.ui.showModal({
                title: 'Preview',
                message: '',
                contentElement: video,
                buttons: [{ label: 'Close', value: true, primary: true }]
            });
        } else if (options.openWindowFallback !== false) {
            const previewWindow = global.open('', '_blank', 'noopener,width=640,height=360');
            if (previewWindow) {
                previewWindow.document.title = 'Preview';
                previewWindow.document.body.appendChild(video);
            }
        }

        try {
            await configureSource(video, safeUrl, options.onPlaybackError);
        } catch (error) {
            if (options.catchSetupError !== true) throw error;
            console.error('HLS preview setup failed', error);
            video.src = safeUrl;
        }
    }

    global.DeezSpoTagVideoPreview = Object.freeze({ play, sanitizeUrl });
})(globalThis);
