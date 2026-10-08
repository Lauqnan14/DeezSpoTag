(function (global) {
    'use strict';

    // Owns the OS-level "now playing" surface: the Android media notification, the lock
    // screen transport controls, Bluetooth/headset buttons and the hardware media keys.
    //
    // Playback itself stays in DeezerUnifiedPlayback; this module only mirrors it into
    // navigator.mediaSession and routes the platform's actions back. Every source
    // (Deezer, Tidal, Qobuz, Amazon Music, Apple Music) already funnels through that one
    // module, so a single adapter covers all of them.
    //
    // The Media Session action set is closed (play, pause, stop, seek*, previoustrack,
    // nexttrack, ...). A "Download" button cannot live inside the media notification, so
    // it is posted as its own notification from the service worker instead.

    const FALLBACK_ARTWORK = '/images/pwa/icon-512.png';
    const NOTIFICATION_TAG = 'deezspotag-now-playing';
    const DOWNLOAD_MESSAGE = 'deezspotag:download-current';
    const POSITION_UPDATE_INTERVAL_MS = 1000;
    const SEEK_STEP_SECONDS = 10;

    let audio = null;
    let lastSessionKey = '';
    let lastPositionUpdateAt = 0;
    let notificationShownForKey = '';

    function trim(value) {
        return String(value || '').trim();
    }

    function getPlayer() {
        const player = global.DeezerUnifiedPlayback;
        return player && typeof player === 'object' ? player : null;
    }

    function getMediaSession() {
        const session = global.navigator && global.navigator.mediaSession;
        return session && typeof session === 'object' ? session : null;
    }

    function isSupported() {
        return Boolean(getMediaSession());
    }

    // Prefer the row's own cover, then the Deezer cover for a matched id, then the app
    // icon so the notification never renders an empty tile.
    function buildArtwork(request, session) {
        const source = request?.mediaMetadata || request?.spotifyMetadata || request?.metadata || null;
        const cover = trim(source?.cover || source?.coverUrl || source?.imageUrl);
        if (cover) {
            return cover;
        }

        const deezerId = trim(session?.deezerId || request?.deezerId);
        if (deezerId) {
            return `https://e-cdns-images.dzcdn.net/images/cover/${encodeURIComponent(deezerId)}/500x500-000000-80-0-0.jpg`;
        }

        return FALLBACK_ARTWORK;
    }

    function resolveMetadata(request, session) {
        const source = request?.mediaMetadata || request?.spotifyMetadata || request?.metadata || null;
        const title = trim(source?.title) || 'DeezSpoTag';
        const artist = trim(source?.artist);
        const album = trim(source?.album);
        const artwork = buildArtwork(request, session);

        const rawDurationMs = Number.parseInt(String(source?.durationMs || '0'), 10);
        const durationMs = Number.isFinite(rawDurationMs) && rawDurationMs > 0
            ? rawDurationMs
            : 0;

        return { title, artist, album, artwork, durationMs };
    }

    function syncMetadata() {
        const mediaSession = getMediaSession();
        if (!mediaSession || typeof global.MediaMetadata !== 'function') {
            return;
        }

        const player = getPlayer();
        const session = player?.getSession ? player.getSession() : null;
        const request = player?.getCurrentRequest ? player.getCurrentRequest() : null;
        if (!session) {
            mediaSession.metadata = null;
            return;
        }

        const resolved = resolveMetadata(request, session);
        mediaSession.metadata = new global.MediaMetadata({
            title: resolved.title,
            artist: resolved.artist,
            album: resolved.album,
            artwork: [{ src: resolved.artwork, sizes: '512x512', type: 'image/jpeg' }]
        });
    }

    function syncPlaybackState() {
        const mediaSession = getMediaSession();
        if (!mediaSession) {
            return;
        }

        if (!audio) {
            mediaSession.playbackState = 'none';
            return;
        }

        mediaSession.playbackState = audio.paused ? 'paused' : 'playing';
    }

    function syncPositionState(force) {
        const mediaSession = getMediaSession();
        if (!mediaSession || typeof mediaSession.setPositionState !== 'function' || !audio) {
            return;
        }

        const now = Date.now();
        if (!force && now - lastPositionUpdateAt < POSITION_UPDATE_INTERVAL_MS) {
            return;
        }
        lastPositionUpdateAt = now;

        const duration = Number(audio.duration);
        // Streams often report an unknown duration; the scrubber needs a real range.
        if (!Number.isFinite(duration) || duration <= 0) {
            return;
        }

        try {
            mediaSession.setPositionState({
                duration,
                playbackRate: Number(audio.playbackRate) || 1,
                position: Math.min(Math.max(Number(audio.currentTime) || 0, 0), duration)
            });
        } catch {
            // A rejected position state must never break playback.
        }
    }

    function setActionHandler(action, handler) {
        const mediaSession = getMediaSession();
        if (!mediaSession) {
            return;
        }

        // Support varies per browser and per action, so probe instead of assuming.
        try {
            mediaSession.setActionHandler(action, handler);
        } catch {
            // Unsupported action on this browser; the rest still register.
        }
    }

    function registerActionHandlers() {
        const player = getPlayer();
        if (!player) {
            return;
        }

        setActionHandler('play', () => {
            if (typeof player.resume === 'function') {
                void player.resume();
            }
        });
        setActionHandler('pause', () => {
            if (typeof player.pause === 'function') {
                void player.pause();
            }
        });
        setActionHandler('stop', () => {
            if (typeof player.stop === 'function') {
                void player.stop('idle');
            }
        });
        setActionHandler('nexttrack', () => {
            if (typeof player.next === 'function') {
                void player.next();
            }
        });
        setActionHandler('previoustrack', () => {
            if (typeof player.previous === 'function') {
                void player.previous();
            }
        });
        setActionHandler('seekbackward', (details) => {
            if (!audio) {
                return;
            }
            const step = Number(details?.seekOffset) || SEEK_STEP_SECONDS;
            audio.currentTime = Math.max((Number(audio.currentTime) || 0) - step, 0);
            syncPositionState(true);
        });
        setActionHandler('seekforward', (details) => {
            if (!audio) {
                return;
            }
            const step = Number(details?.seekOffset) || SEEK_STEP_SECONDS;
            const duration = Number(audio.duration);
            const target = (Number(audio.currentTime) || 0) + step;
            audio.currentTime = Number.isFinite(duration) && duration > 0
                ? Math.min(target, duration)
                : target;
            syncPositionState(true);
        });
        setActionHandler('seekto', (details) => {
            const seekTime = Number(details?.seekTime);
            if (!audio || !Number.isFinite(seekTime) || seekTime < 0) {
                return;
            }
            audio.currentTime = seekTime;
            syncPositionState(true);
        });
    }

    function bindAudioElement() {
        const player = getPlayer();
        const nextAudio = typeof player?.getAudio === 'function' ? player.getAudio() : null;
        if (!nextAudio || nextAudio === audio) {
            return false;
        }

        audio = nextAudio;
        audio.addEventListener('play', () => {
            requestNotificationPermissionOnce();
            syncPlaybackState();
            syncPositionState(true);
        });
        audio.addEventListener('pause', () => syncPlaybackState());
        audio.addEventListener('ended', () => syncPlaybackState());
        audio.addEventListener('loadedmetadata', () => syncPositionState(true));
        audio.addEventListener('timeupdate', () => syncPositionState(false));
        return true;
    }

    function requestNotificationPermissionOnce() {
        if (!globalThis.__deezspotRequestNotificationPermissionOnPlay) {
            return;
        }

        globalThis.__deezspotRequestNotificationPermissionOnPlay = false;
        if (!global.Notification || typeof global.Notification.requestPermission !== 'function') {
            return;
        }

        // Asked on the first play, never on page load: the user has just expressed
        // intent to listen, so the prompt has context.
        try {
            void global.Notification.requestPermission();
        } catch {
            // Permission can only be requested from a user gesture on some browsers.
        }
    }

    function showDownloadNotification() {
        const player = getPlayer();
        const session = player?.getSession ? player.getSession() : null;
        if (!session) {
            return;
        }

        if (!global.navigator.serviceWorker?.controller) {
            return;
        }

        const request = player.getCurrentRequest ? player.getCurrentRequest() : null;
        const resolved = resolveMetadata(request, session);
        if (resolved.title === 'DeezSpoTag' && !resolved.artist) {
            return;
        }

        if (notificationShownForKey === session.key) {
            return;
        }
        notificationShownForKey = session.key;

        global.navigator.serviceWorker.controller.postMessage({
            type: 'deezspotag:show-download-notification',
            tag: NOTIFICATION_TAG,
            payload: {
                title: resolved.title,
                body: resolved.artist,
                icon: '/images/pwa/icon-192.png'
            }
        });
    }

    async function handleServiceWorkerMessage(event) {
        if (event?.data?.type !== DOWNLOAD_MESSAGE) {
            return;
        }

        const downloader = global.DeezSpoTagDownload;
        const player = getPlayer();
        const session = player?.getSession ? player.getSession() : null;
        if (!downloader || typeof downloader.downloadSelectedTracks !== 'function' || !session) {
            return;
        }

        // Only a track with a resolved download identity can be enqueued; link-only
        // rows never offer the action in the first place.
        const trackIds = session.deezerId ? [session.deezerId] : [];
        if (trackIds.length === 0) {
            return;
        }

        try {
            await downloader.downloadSelectedTracks(trackIds);
        } catch (error) {
            console.warn('Download request from the media notification failed:', error);
        }
    }

    function sync() {
        bindAudioElement();

        const player = getPlayer();
        const session = player?.getSession ? player.getSession() : null;
        const sessionKey = trim(session?.key);

        if (sessionKey !== lastSessionKey) {
            lastSessionKey = sessionKey;
            notificationShownForKey = '';
            syncMetadata();
        }

        syncPlaybackState();
        syncPositionState(true);
        showDownloadNotification();
    }

    function attach() {
        bindAudioElement();
        registerActionHandlers();

        if (global.navigator?.serviceWorker) {
            global.navigator.serviceWorker.addEventListener('message', handleServiceWorkerMessage);
        }

        sync();
    }

    global.DeezSpoTagMediaSession = {
        attach,
        sync,
        isSupported
    };
})(globalThis);
