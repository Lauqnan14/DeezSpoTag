(() => {
    const logo = document.getElementById('logoIcon');
    if (!logo) {
        return;
    }

    const AudioContextCtor = globalThis.AudioContext || globalThis.webkitAudioContext;
    const hasMediaDevices = Boolean(navigator.mediaDevices && typeof navigator.mediaDevices.getUserMedia === 'function');
    const hasAudioContext = Boolean(AudioContextCtor);
    const hasLiveApi = hasMediaDevices && hasAudioContext;

    const SESSION_KEY = 'deezspotag-shazam-last';
    const defaultConfig = {
        enabled: true,
        useCenteredOverlay: true,
        captureDurationSeconds: 11,
        allowHttpFileFallback: true,
        remoteMemoryOnly: true
    };

    const config = { ...defaultConfig };

    let state = 'idle';
    let stream = null;
    let audioContext = null;
    let sourceNode = null;
    let processor = null;
    let sampleRate = 44100;
    let buffers = [];
    let autoStopTimer = null;
    let overlay = null;
    let fallbackInput = null;

    logo.setAttribute('role', 'button');
    logo.setAttribute('tabindex', '0');

    const notify = (message, type) => {
        if (globalThis.DeezSpoTag?.showNotification) {
            globalThis.DeezSpoTag.showNotification(message, type || 'info');
        }
    };

    const isLocalNetworkHost = (hostname) => {
        const value = String(hostname || '').trim().toLowerCase();
        if (!value) {
            return false;
        }

        if (value === 'localhost' || value === '::1' || value.endsWith('.local')) {
            return true;
        }

        if (value.startsWith('127.') || value.startsWith('10.') || value.startsWith('192.168.')) {
            return true;
        }

        const match172 = /^172\.(\d+)\./.exec(value);
        if (match172) {
            const octet = Number.parseInt(match172[1], 10);
            if (Number.isFinite(octet) && octet >= 16 && octet <= 31) {
                return true;
            }
        }

        return false;
    };

    const isLocalNetwork = isLocalNetworkHost(globalThis.location.hostname);

    const liveMicSupported = () => {
        if (!hasLiveApi) {
            return false;
        }

        if (globalThis.isSecureContext) {
            return true;
        }

        // Browsers generally block getUserMedia on insecure origins; keep localhost/private as best-effort allowance.
        return isLocalNetwork;
    };

    const shouldPersistCapturePayload = () => {
        return !config.remoteMemoryOnly || isLocalNetwork;
    };

    const ensureOverlayStyles = () => {
        if (document.getElementById('shz-capture-styles')) {
            return;
        }

        const style = document.createElement('style');
        style.id = 'shz-capture-styles';
        style.textContent = `
            .shz-capture-overlay {
                position: fixed;
                inset: 0;
                z-index: 3000;
                display: grid;
                place-items: center;
                background:
                    radial-gradient(circle at 50% 42%, color-mix(in srgb, var(--primary-color, #3aa6ff) 22%, transparent 78%), transparent 48%),
                    radial-gradient(circle at 50% 50%, color-mix(in srgb, var(--secondary-color, #5ad7ff) 18%, transparent 82%), transparent 64%),
                    var(--bg-primary, #040b1a);
            }

            .shz-capture-overlay.is-hidden {
                display: none;
            }

            .shz-capture-shell {
                width: min(90vw, 420px);
                text-align: center;
                color: var(--text-primary, #f4f8ff);
                display: grid;
                gap: 14px;
                justify-items: center;
            }

            .shz-capture-stage {
                position: relative;
                width: 300px;
                height: 300px;
                display: grid;
                place-items: center;
            }

            .shz-ring {
                position: absolute;
                inset: 0;
                border-radius: 999px;
                border: 2px solid color-mix(in srgb, var(--primary-color, #25daff) 55%, transparent 45%);
                opacity: 0;
                transform: scale(0.72);
            }

            .shz-ring-2 {
                border-color: color-mix(in srgb, var(--secondary-color, #0095ff) 45%, transparent 55%);
            }

            .shz-ring-3 {
                border-color: color-mix(in srgb, var(--secondary-color, #6ef2ff) 32%, transparent 68%);
            }

            .shz-capture-orb {
                width: 200px;
                height: 200px;
                border-radius: 999px;
                display: grid;
                place-items: center;
                border: 2px solid color-mix(in srgb, var(--primary-color, #58ecff) 70%, transparent 30%);
                background:
                    radial-gradient(
                        circle at 46% 30%,
                        color-mix(in srgb, var(--secondary-color, #43e7ff) 85%, transparent 15%),
                        color-mix(in srgb, var(--primary-color, #1989ff) 85%, transparent 15%) 46%,
                        color-mix(in srgb, var(--bg-secondary, #0f1d33) 90%, transparent 10%) 100%
                    );
                box-shadow:
                    0 0 26px color-mix(in srgb, var(--primary-color, #00c3ff) 40%, transparent 60%),
                    inset 0 0 22px rgba(255, 255, 255, 0.12);
                overflow: hidden;
                position: relative;
                isolation: isolate;
            }

            .shz-core-icon {
                width: 132px;
                height: 132px;
                border-radius: 999px;
                display: grid;
                place-items: center;
                background: rgba(255, 255, 255, 0.14);
                backdrop-filter: blur(2px);
                z-index: 2;
                padding: 6px;
            }

            .shz-core-icon .theme-logo,
            .shz-core-icon img {
                width: 100%;
                height: 100%;
                object-fit: contain;
                filter: drop-shadow(0 0 10px rgba(255, 255, 255, 0.35));
            }

            .shz-capture-orb::before {
                content: "";
                position: absolute;
                inset: -25%;
                background: conic-gradient(
                    from 0deg,
                    rgba(255, 255, 255, 0) 0deg,
                    color-mix(in srgb, var(--primary-color, #ffffff) 28%, transparent 72%) 44deg,
                    rgba(255, 255, 255, 0) 84deg,
                    rgba(255, 255, 255, 0) 360deg
                );
                opacity: 0;
                z-index: 1;
            }

            .shz-capture-overlay.is-listening .shz-ring-1 {
                animation: shzRingPulse 1.8s ease-out infinite;
            }

            .shz-capture-overlay.is-listening .shz-ring-2 {
                animation: shzRingPulse 1.8s ease-out 0.36s infinite;
            }

            .shz-capture-overlay.is-listening .shz-ring-3 {
                animation: shzRingPulse 1.8s ease-out 0.72s infinite;
            }

            .shz-capture-overlay.is-listening .shz-capture-orb {
                animation: shzOrbBreathe 1.8s ease-in-out infinite;
            }

            .shz-capture-overlay.is-searching .shz-ring {
                opacity: 1;
                transform: scale(1);
                animation: shzSearchRipple 1.6s ease-in-out infinite;
            }

            .shz-capture-overlay.is-searching .shz-ring-2 {
                animation-delay: 0.22s;
            }

            .shz-capture-overlay.is-searching .shz-ring-3 {
                animation-delay: 0.44s;
            }

            .shz-capture-overlay.is-searching .shz-capture-orb::before {
                opacity: 1;
                animation: shzSweepSpin 1.4s linear infinite;
            }

            .shz-capture-overlay.is-error .shz-capture-orb {
                border-color: var(--error-color, #ef4444);
                box-shadow: 0 0 0 6px color-mix(in srgb, var(--error-color, #ef4444) 18%, transparent 82%);
                animation: none;
            }

            .shz-capture-overlay.is-error .shz-ring {
                border-color: color-mix(in srgb, var(--error-color, #ef4444) 50%, transparent 50%);
                opacity: 1;
                transform: scale(1);
                animation: none;
            }

            .shz-capture-title {
                font-size: 28px;
                font-weight: 700;
                letter-spacing: 0.02em;
            }

            .shz-capture-subtitle {
                color: var(--text-secondary, #9fb2ce);
                font-size: 14px;
                min-height: 20px;
            }

            .shz-capture-actions {
                display: flex;
                gap: 10px;
                flex-wrap: wrap;
                justify-content: center;
            }

            .shz-capture-btn {
                border: 1px solid var(--border-secondary, #2a3f62);
                background: var(--bg-secondary, #0f1d33);
                color: var(--text-primary, #f4f8ff);
                border-radius: 999px;
                padding: 9px 16px;
                font-size: 13px;
                cursor: pointer;
            }

            .shz-capture-btn:hover {
                border-color: var(--primary-color, #00c2ff);
            }

            .shz-capture-btn.is-hidden {
                display: none;
            }

            @keyframes shzRingPulse {
                0% { opacity: 0.85; transform: scale(0.68); }
                100% { opacity: 0; transform: scale(1.26); }
            }

            @keyframes shzOrbBreathe {
                0%, 100% { transform: scale(1); }
                50% { transform: scale(1.05); }
            }

            @keyframes shzSearchRipple {
                0%, 100% { opacity: 0.46; }
                50% { opacity: 0.9; }
            }

            @keyframes shzSweepSpin {
                from { transform: rotate(0deg); }
                to { transform: rotate(360deg); }
            }

            @media (max-width: 700px) {
                .shz-capture-stage { width: 254px; height: 254px; }
                .shz-capture-orb { width: 170px; height: 170px; }
                .shz-core-icon { width: 110px; height: 110px; }
                .shz-capture-title { font-size: 24px; }
            }
        `;

        document.head.appendChild(style);
    };

    const ensureOverlay = () => {
        if (overlay) {
            return overlay;
        }

        ensureOverlayStyles();
        overlay = document.createElement('div');
        overlay.className = 'shz-capture-overlay is-hidden';
        overlay.innerHTML = `
            <div class="shz-capture-shell">
                <div class="shz-capture-stage">
                    <span class="shz-ring shz-ring-1"></span>
                    <span class="shz-ring shz-ring-2"></span>
                    <span class="shz-ring shz-ring-3"></span>
                    <div class="shz-capture-orb" id="shzCaptureOrb"></div>
                </div>
                <div class="shz-capture-title" id="shzCaptureTitle">Shazam</div>
                <div class="shz-capture-subtitle" id="shzCaptureSubtitle"></div>
                <div class="shz-capture-actions">
                    <button type="button" class="shz-capture-btn" id="shzCaptureFallback">Choose/Record Audio</button>
                    <button type="button" class="shz-capture-btn" id="shzCaptureCancel">Cancel</button>
                </div>
            </div>
        `;

        document.body.appendChild(overlay);

        const orb = overlay.querySelector('#shzCaptureOrb');
        if (orb) {
            orb.innerHTML = `
                <div class="shz-core-icon" aria-hidden="true">
                    <svg viewBox="155 0 90 100" xmlns="http://www.w3.org/2000/svg" class="theme-logo" role="img" aria-label="DeezSpoTag">
                        <defs>
                            <linearGradient id="shzPremiumGrad" x1="0%" y1="0%" x2="100%" y2="100%">
                                <stop offset="0%" class="logo-primary-stop" />
                                <stop offset="100%" class="logo-secondary-stop" />
                            </linearGradient>
                            <filter id="shzNeonGlow">
                                <feGaussianBlur stdDeviation="3" result="coloredBlur"/>
                                <feOffset result="offsetblur" in="coloredBlur" dx="0" dy="0"/>
                                <feFlood class="logo-glow-flood" flood-opacity="0.5"/>
                                <feComposite in2="offsetblur" operator="in"/>
                                <feMerge><feMergeNode/><feMergeNode in="SourceGraphic"/></feMerge>
                            </filter>
                        </defs>
                        <g transform="translate(200, 50)">
                            <path d="M-45,0 L-22.5,-39 L22.5,-39 L45,0 L22.5,39 L-22.5,39 Z" fill="none" stroke="url(#shzPremiumGrad)" stroke-width="2" filter="url(#shzNeonGlow)"/>
                            <path d="M-35,0 L-17.5,-30 L17.5,-30 L35,0 L17.5,30 L-17.5,30 Z" fill="none" stroke="url(#shzPremiumGrad)" stroke-width="1" opacity="0.5"/>
                            <g filter="url(#shzNeonGlow)">
                                <rect x="-20" y="-15" width="5" height="30" class="logo-bar-1">
                                    <animate attributeName="height" values="30;45;30" dur="0.8s" repeatCount="indefinite"/>
                                    <animate attributeName="y" values="-15;-22.5;-15" dur="0.8s" repeatCount="indefinite"/>
                                </rect>
                                <rect x="-10" y="-20" width="5" height="40" class="logo-bar-2">
                                    <animate attributeName="height" values="40;25;40" dur="1s" repeatCount="indefinite"/>
                                    <animate attributeName="y" values="-20;-12.5;-20" dur="1s" repeatCount="indefinite"/>
                                </rect>
                                <rect x="0" y="-12" width="5" height="24" class="logo-bar-3">
                                    <animate attributeName="height" values="24;42;24" dur="0.6s" repeatCount="indefinite"/>
                                    <animate attributeName="y" values="-12;-21;-12" dur="0.6s" repeatCount="indefinite"/>
                                </rect>
                                <rect x="10" y="-18" width="5" height="36" class="logo-bar-4">
                                    <animate attributeName="height" values="36;20;36" dur="0.9s" repeatCount="indefinite"/>
                                    <animate attributeName="y" values="-18;-10;-18" dur="0.9s" repeatCount="indefinite"/>
                                </rect>
                            </g>
                        </g>
                    </svg>
                </div>
            `;
        }

        const cancel = overlay.querySelector('#shzCaptureCancel');
        if (cancel) {
            cancel.addEventListener('click', () => {
                if (state === 'searching') {
                    return;
                }
                void cancelCapture();
            });
        }

        const fallbackBtn = overlay.querySelector('#shzCaptureFallback');
        if (fallbackBtn) {
            fallbackBtn.addEventListener('click', () => {
                if (state === 'searching') {
                    return;
                }
                fallbackInput?.click();
            });
        }

        overlay.addEventListener('click', (event) => {
            if (event.target === overlay && state === 'listening') {
                void stopAndSearch();
            }
        });

        return overlay;
    };

    const setElementText = (element, text) => {
        if (element) {
            element.textContent = text;
        }
    };

    const applyOverlayState = (root, next, title, subtitle, fallbackBtn, options) => {
        switch (next) {
            case 'listening':
                root.classList.add('is-listening');
                setElementText(title, 'Listening');
                setElementText(subtitle, 'Keep the sound clear. Tap logo again to stop early.');
                break;
            case 'searching':
                root.classList.add('is-searching');
                setElementText(title, 'Searching');
                setElementText(subtitle, 'Matching your audio with Shazam...');
                break;
            case 'fallback':
                setElementText(title, 'Audio Input Needed');
                setElementText(
                    subtitle,
                    options?.reason || 'Live microphone API is unavailable here. Choose or record an audio sample instead.'
                );
                fallbackBtn?.classList.remove('is-hidden');
                break;
            case 'error':
                root.classList.add('is-error');
                setElementText(title, 'No Match');
                setElementText(subtitle, 'Could not identify this sample. Try again with clearer audio.');
                break;
            default:
                break;
        }
    };

    const setOverlayState = (next, options = {}) => {
        if (!config.useCenteredOverlay) {
            return;
        }

        const root = ensureOverlay();
        const title = root.querySelector('#shzCaptureTitle');
        const subtitle = root.querySelector('#shzCaptureSubtitle');
        const fallbackBtn = root.querySelector('#shzCaptureFallback');

        root.classList.remove('is-listening', 'is-searching', 'is-error');
        if (next === 'idle') {
            root.classList.add('is-hidden');
            return;
        }

        root.classList.remove('is-hidden');
        fallbackBtn?.classList.add('is-hidden');
        applyOverlayState(root, next, title, subtitle, fallbackBtn, options);
    };

    const setState = (next, options = {}) => {
        state = next;
        logo.classList.remove('is-shazam-listening', 'is-shazam-searching', 'is-shazam-error', 'is-shazam-ready');

        if (next === 'listening') {
            logo.classList.add('is-shazam-listening');
            logo.title = 'Listening... click again to stop and search.';
            setOverlayState('listening');
            return;
        }

        if (next === 'searching') {
            logo.classList.add('is-shazam-searching');
            logo.title = 'Searching with Shazam...';
            setOverlayState('searching');
            return;
        }

        if (next === 'error') {
            logo.classList.add('is-shazam-error');
            logo.title = 'Shazam capture failed. Click to try again.';
            setOverlayState('error');
            return;
        }

        if (next === 'fallback') {
            logo.classList.add('is-shazam-ready');
            logo.title = 'Audio upload fallback';
            setOverlayState('fallback', options);
            return;
        }

        logo.classList.add('is-shazam-ready');
        logo.title = 'Click to listen and identify a song.';
        setOverlayState('idle');
    };

    const stopStream = () => {
        if (stream) {
            for (const track of stream.getTracks()) {
                track.stop();
            }
            stream = null;
        }
    };

    const releaseAudio = async () => {
        if (autoStopTimer) {
            globalThis.clearTimeout(autoStopTimer);
            autoStopTimer = null;
        }

        if (processor) {
            try {
                processor.disconnect();
            } catch {
                // ignored
            }
            processor.onaudioprocess = null;
            processor = null;
        }

        if (sourceNode) {
            try {
                sourceNode.disconnect();
            } catch {
                // ignored
            }
            sourceNode = null;
        }

        stopStream();

        if (audioContext) {
            try {
                await audioContext.close();
            } catch {
                // ignored
            }
            audioContext = null;
        }
    };

    const encodeWavBlob = (floatChunks, sr) => {
        const totalSamples = floatChunks.reduce((sum, chunk) => sum + chunk.length, 0);
        const pcm = new Int16Array(totalSamples);

        let offset = 0;
        for (const chunk of floatChunks) {
            for (const sample of chunk) {
                const clamped = Math.max(-1, Math.min(1, sample));
                pcm[offset] = clamped < 0 ? clamped * 0x8000 : clamped * 0x7fff;
                offset += 1;
            }
        }

        const buffer = new ArrayBuffer(44 + pcm.length * 2);
        const view = new DataView(buffer);

        const writeText = (position, text) => {
            for (let i = 0; i < text.length; i += 1) {
                view.setUint8(position + i, text.codePointAt(i) ?? 0);
            }
        };

        writeText(0, 'RIFF');
        view.setUint32(4, 36 + pcm.length * 2, true);
        writeText(8, 'WAVE');
        writeText(12, 'fmt ');
        view.setUint32(16, 16, true);
        view.setUint16(20, 1, true);
        view.setUint16(22, 1, true);
        view.setUint32(24, sr, true);
        view.setUint32(28, sr * 2, true);
        view.setUint16(32, 2, true);
        view.setUint16(34, 16, true);
        writeText(36, 'data');
        view.setUint32(40, pcm.length * 2, true);

        let writeOffset = 44;
        for (const sample of pcm) {
            view.setInt16(writeOffset, sample, true);
            writeOffset += 2;
        }

        return new Blob([buffer], { type: 'audio/wav' });
    };

    const persistPayloadForResults = (payload) => {
        if (!shouldPersistCapturePayload()) {
            return;
        }

        try {
            sessionStorage.setItem(SESSION_KEY, JSON.stringify({
                capturedAt: Date.now(),
                payload
            }));
        } catch {
            // ignore session storage errors
        }
    };

    const navigateToResults = (payload) => {
        const params = new URLSearchParams();
        const trackId = payload?.track?.id || payload?.recognition?.trackId;
        const query = payload?.query || [payload?.recognition?.title, payload?.recognition?.artist].filter(Boolean).join(' ');

        if (trackId) {
            params.set('trackId', trackId);
        }
        if (query) {
            params.set('q', query);
        }
        if (payload?.recognition?.title) {
            params.set('title', payload.recognition.title);
        }
        if (payload?.recognition?.artist) {
            params.set('artist', payload.recognition.artist);
        }

        globalThis.location.href = `/Shazam/Results?${params.toString()}`;
    };

    const runRecognitionFromBlob = async (audioBlob, filename) => {
        setState('searching');

        try {
            const form = new FormData();
            form.append('audio', audioBlob, filename || 'capture.wav');

            const response = await fetch('/api/shazam/recognize-mic', {
                method: 'POST',
                body: form
            });

            let payload = null;
            const contentType = response.headers.get('content-type') || '';
            if (contentType.includes('application/json')) {
                payload = await response.json();
            }

            if (!response.ok) {
                const reason = payload?.reason;
                const detail = payload?.error;
                setState('error');

                if (reason === 'recognizer_unavailable') {
                    notify(detail || 'Shazam recognizer is unavailable on the server.', 'error');
                } else if (reason === 'recognizer_error') {
                    notify(detail || 'Shazam recognizer failed while processing this sample.', 'error');
                } else {
                    notify(detail || `Shazam lookup failed (${response.status}).`, 'error');
                }

                globalThis.setTimeout(() => setState('idle'), 2200);
                return;
            }

            if (!payload?.matched) {
                if (payload?.reason === 'no_match') {
                    setState('error');
                    notify('No Shazam match found. Try cleaner audio.', 'warning');
                } else {
                    setState('error');
                    notify(payload?.error || 'Shazam could not process this sample.', 'error');
                }
                globalThis.setTimeout(() => setState('idle'), 1500);
                return;
            }

            persistPayloadForResults(payload);
            navigateToResults(payload);
        } catch (error) {
            console.error('Shazam recognition failed', error);
            setState('error');
            notify('Shazam lookup failed. Please try again.', 'error');
            globalThis.setTimeout(() => setState('idle'), 1500);
        }
    };

    const ensureFallbackInput = () => {
        if (fallbackInput) {
            return fallbackInput;
        }

        fallbackInput = document.createElement('input');
        fallbackInput.type = 'file';
        fallbackInput.accept = 'audio/*';
        fallbackInput.style.display = 'none';

        fallbackInput.addEventListener('change', () => {
            const file = fallbackInput?.files?.[0];
            if (!file) {
                if (state === 'fallback') {
                    setState('idle');
                }
                return;
            }

            void runRecognitionFromBlob(file, file.name || 'capture.webm');
            fallbackInput.value = '';
        });

        document.body.appendChild(fallbackInput);
        return fallbackInput;
    };

    const cancelCapture = async () => {
        await releaseAudio();
        setState('idle');
    };

    const startCapture = async () => {
        if (state === 'listening' || state === 'searching') {
            return;
        }

        if (!liveMicSupported()) {
            if (config.allowHttpFileFallback) {
                ensureFallbackInput();
                setState('fallback', {
                    reason: globalThis.isSecureContext
                        ? 'Live microphone capture is unavailable in this browser. Choose or record an audio file.'
                        : 'This HTTP session does not expose live microphone APIs. Choose or record an audio file instead.'
                });
                return;
            }

            notify('Microphone capture is not available in this browser/session. Enable Shazam HTTP fallback in Settings.', 'warning');
            return;
        }

        try {
            buffers = [];
            stream = await navigator.mediaDevices.getUserMedia({
                audio: {
                    channelCount: 1,
                    // Disable voice-call DSP features; they degrade music fingerprint accuracy.
                    noiseSuppression: false,
                    echoCancellation: false,
                    autoGainControl: false
                }
            });

            audioContext = new AudioContextCtor();
            sampleRate = audioContext.sampleRate || 44100;
            sourceNode = audioContext.createMediaStreamSource(stream);
            processor = audioContext.createScriptProcessor(4096, 1, 1);

            processor.onaudioprocess = (event) => {
                if (state !== 'listening') {
                    return;
                }

                const data = event.inputBuffer.getChannelData(0);
                buffers.push(new Float32Array(data));
            };

            sourceNode.connect(processor);
            processor.connect(audioContext.destination);
            setState('listening');

            autoStopTimer = globalThis.setTimeout(() => {
                void stopAndSearch();
            }, Math.max(3, Math.min(20, Number(config.captureDurationSeconds || 7))) * 1000);
        } catch (error) {
            console.error('Shazam capture start failed', error);
            await releaseAudio();

            if (config.allowHttpFileFallback) {
                ensureFallbackInput();
                setState('fallback', {
                    reason: globalThis.isSecureContext
                        ? 'Live microphone capture was blocked. Choose or record an audio file.'
                        : 'This HTTP session does not allow microphone capture. Choose or record an audio file instead.'
                });
                return;
            }

            setState('error');
            notify('Microphone access is required to identify songs.', 'warning');
            globalThis.setTimeout(() => setState('idle'), 1500);
        }
    };

    const stopAndSearch = async () => {
        if (state !== 'listening') {
            return;
        }

        const capturedChunks = buffers.slice();
        const capturedRate = sampleRate;
        await releaseAudio();

        if (capturedChunks.length === 0) {
            setState('error');
            notify('No audio captured. Try again.', 'warning');
            globalThis.setTimeout(() => setState('idle'), 1500);
            return;
        }

        const wav = encodeWavBlob(capturedChunks, capturedRate);
        await runRecognitionFromBlob(wav, 'capture.wav');
    };

    const setupHandlers = () => {
        logo.addEventListener('click', () => {
            if (!config.enabled) {
                notify('Shazam logo capture is disabled in Settings.', 'warning');
                return;
            }

            if (state === 'listening') {
                void stopAndSearch();
                return;
            }

            if (state === 'searching') {
                return;
            }

            void startCapture();
        });

        logo.addEventListener('keydown', (event) => {
            if (event.key === 'Enter' || event.key === ' ') {
                event.preventDefault();
                logo.click();
            }
        });

        globalThis.addEventListener('beforeunload', () => {
            void releaseAudio();
        });
    };

    const applySettings = (settings) => {
        config.enabled = settings?.shazamEnabled !== false;
        config.useCenteredOverlay = settings?.shazamUseCenteredOverlay !== false;
        config.captureDurationSeconds = Number(settings?.shazamCaptureDurationSeconds ?? defaultConfig.captureDurationSeconds);
        if (!Number.isFinite(config.captureDurationSeconds)) {
            config.captureDurationSeconds = defaultConfig.captureDurationSeconds;
        }
        config.captureDurationSeconds = Math.max(3, Math.min(20, Math.round(config.captureDurationSeconds)));
        config.allowHttpFileFallback = settings?.shazamAllowHttpFileFallback !== false;
        config.remoteMemoryOnly = settings?.shazamRemoteMemoryOnly !== false;
    };

    const loadSettings = async () => {
        try {
            const response = await fetch('/api/settings');
            if (!response.ok) {
                return;
            }
            const data = await response.json();
            applySettings(data?.settings || {});
        } catch {
            // keep defaults
        }
    };

    const init = async () => {
        await loadSettings();
        ensureFallbackInput();
        setupHandlers();
        setState('idle');

        if (!config.enabled) {
            logo.title = 'Shazam capture disabled in Settings.';
            return;
        }

        if (!liveMicSupported() && config.allowHttpFileFallback) {
            logo.title = 'Shazam fallback mode (audio file/record input)';
        }
    };

    void init();
})();
