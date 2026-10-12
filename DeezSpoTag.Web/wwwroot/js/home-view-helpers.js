(function (global) {
    'use strict';

    function showToast(message, type = 'info') {
        const toastType = type;
        if (global.DeezSpoTag && typeof global.DeezSpoTag.showNotification === 'function') {
            global.DeezSpoTag.showNotification(message, toastType);
            return;
        }

        const existingToasts = document.querySelectorAll('.toast-notification');
        existingToasts.forEach((toast) => toast.remove());

        const toast = document.createElement('div');
        toast.className = 'toast-notification toast-' + toastType;
        toast.textContent = message;

        toast.style.position = 'fixed';
        toast.style.top = '20px';
        toast.style.right = '20px';
        toast.style.padding = '12px 20px';
        toast.style.borderRadius = '6px';
        toast.style.fontWeight = '500';
        toast.style.zIndex = '10000';
        toast.style.maxWidth = '300px';
        toast.style.overflowWrap = 'break-word';

        document.body.appendChild(toast);

        setTimeout(function () {
            if (toast.parentNode) {
                toast.remove();
            }
        }, 5000);
    }

    function clearHomeTrendingPlayingMarkers(exceptButton) {
        global.document.querySelectorAll('.home-top-song-item__play').forEach((activeButton) => {
            if (exceptButton && activeButton === exceptButton) {
                return;
            }
            activeButton.classList.remove('is-playing', 'is-starting');
            delete activeButton.dataset.playbackState;
            const row = activeButton.closest('.home-top-song-item');
            if (row) {
                row.classList.remove('is-playing');
            }
        });
    }

    function setHomeTrendingPreviewButtonState(button, isPlaying) {
        if (!button) {
            if (!isPlaying) {
                clearHomeTrendingPlayingMarkers(null);
            }
            return;
        }
        if (isPlaying) {
            clearHomeTrendingPlayingMarkers(button);
        }
        button.classList.toggle('is-playing', isPlaying);
        const row = button.closest('.home-top-song-item');
        if (row) {
            row.classList.toggle('is-playing', isPlaying);
        }
    }

    function setHomeTrendingPlaybackState(button, state) {
        const playbackState = global.DeezerPlaybackState;
        if (playbackState && typeof playbackState.transitionButtonState === 'function') {
            playbackState.transitionButtonState(button, state, {
                setPlaying: setHomeTrendingPreviewButtonState,
                clear: () => clearHomeTrendingPlayingMarkers(null),
                onTransition: (targetButton, nextState) => {
                    if (nextState === 'requested') {
                        setHomeTrendingPreviewButtonState(targetButton, true);
                    }
                }
            });
            return;
        }
        setHomeTrendingPreviewButtonState(button, String(state || '').toLowerCase() === 'playing');
    }

    function normalizeHomeDeezerPlaybackContext(payload) {
        if (!global.DeezerPlaybackContext || typeof global.DeezerPlaybackContext.normalize !== 'function') {
            return null;
        }
        return global.DeezerPlaybackContext.normalize(payload);
    }

    function buildHomeDeezerStreamUrl(deezerId, context) {
        if (!global.DeezerPlaybackContext || typeof global.DeezerPlaybackContext.buildStreamUrl !== 'function') {
            return '';
        }
        return global.DeezerPlaybackContext.buildStreamUrl(deezerId, { context: context });
    }

    async function fetchHomeDeezerPlaybackContext(deezerId, contextCache, requestCache) {
        if (!global.DeezerPlaybackContext || typeof global.DeezerPlaybackContext.fetchContext !== 'function') {
            return null;
        }
        return await global.DeezerPlaybackContext.fetchContext(deezerId, {
            cache: contextCache,
            requests: requestCache
        });
    }

    function buildSpotifyBrowseClick(item) {
        const id = encodeURIComponent(item.id || '');
        const title = encodeURIComponent(item.title || item.name || '');
        if (!id) {
            return 'void(0)';
        }
        return "globalThis.location.href='/Spotify/Browse?categoryId=" + id + "&title=" + title + "'";
    }

    function safeDecode(value) {
        try {
            return decodeURIComponent((value || '').toString());
        } catch {
            return (value || '').toString();
        }
    }

    function renderTopGenresItem(item) {
        if (!item) {
            return '';
        }
        const title = escapeHtml(item.title || item.name || '');
        const source = (item.source || '').toString().toLowerCase();
        const linked = item.image_linked_item || item.imageLinkedItem || {};
        const imageType = linked.type || linked.TYPE || item.picture_type || item.pictureType || item.imageType || 'playlist';
        const imageHash = linked.md5 || linked.MD5 || item.md5_image || item.md5 || item.imageHash || item.cover?.md5 || item.cover?.MD5 || item.background_image?.md5;
        const image = source === 'spotify'
            ? upgradeSpotifyImageUrl(extractFirstUrl(item.coverUrl || item.imageUrl || item.image || item.picture || item.artwork || item.images || item.cover || ''))
            : ((item.image && (item.image.thumbUrl || item.image.thumb || item.image.fullUrl || item.image.full || item.image.url))
                || item.image
                || item.coverUrl
                || item.picture
                || item.cover
                || item.artwork
                || buildDeezerImageUrlWithSize(imageHash, imageType, 250, 80)
                || buildDeezerImageUrl(imageHash, imageType)
                || '');
        const click = source === 'spotify' ? buildSpotifyBrowseClick(item) : buildHomeClick(item);
        const artStyle = image ? "style=\"background-image: url('" + image + "');\"" : '';
        const platformIcon = source === 'ytmusic' ? '/images/icons/youtube-music.png' : `/images/availability/${source === 'spotify' || source === 'apple' ? source : 'deezer'}.svg`;
        const subtitleCandidate = (item.subtitle || item.artist || item.artists || item.owner || item.ownerName || '').toString().trim();
        const subtitle = /^(spotify|deezer|apple music|youtube music)$/i.test(subtitleCandidate) ? '' : escapeHtml(subtitleCandidate);
        return "\n        <div class=\"top-genres-card top-genres-card--thumb\" onclick=\"" + click + "\" style=\"position: relative;\">\n            <div class=\"top-genres-platform\" style=\"--platform-icon: url('" + platformIcon + "');\"></div>\n            <div class=\"top-genres-content\">\n                <div class=\"top-genres-title\">" + title + "</div>\n                " + (subtitle ? "<div class=\"top-genres-subtitle\">" + subtitle + "</div>" : '') + "\n            </div>\n            <div class=\"top-genres-art\" " + artStyle + "></div>\n        </div>\n    ";
    }

    function buildAppleBrowseClick(item) {
        if (item.type === 'browse-category') {
            const path = encodeURIComponent(item.target || '').replaceAll("'", '%27');
            return `openAppleCategory('${path}')`;
        }
        const params = new URLSearchParams({ id: item.appleId || item.id || '', type: item.type || 'track', source: 'apple' });
        if (item.appleUrl) {
            params.set('appleUrl', item.appleUrl);
            params.set('externalUrl', item.appleUrl);
        }
        return `globalThis.location.href='/Tracklist?${params.toString()}'`;
    }

    function appleAttribute(value) {
        return escapeHtml(String(value || '')).replaceAll('"', '&quot;').replaceAll("'", '&#39;');
    }

    function renderAppleBrowseItem(item) {
        const title = escapeHtml(item.title || item.name || '');
        const subtitle = escapeHtml(item.subtitle || item.artist || '');
        const image = appleAttribute(item.coverUrl || item.image || '');
        const click = appleAttribute(buildAppleBrowseClick(item));
        const preview = appleAttribute(item.previewUrl || '');
        const canPreview = !!(preview || item.appleId || item.id);
        const previewData = `data-preview="${preview}" data-apple-id="${appleAttribute(item.appleId || item.id)}" data-apple-type="${appleAttribute(item.type)}"`;
        if (item.type === 'video') {
            return `<div class="result-card video apple-card" onclick="${click}">
                <div class="apple-thumb"><img src="${image}" alt="${appleAttribute(item.title)}" loading="lazy" />
                    ${canPreview ? `<button class="video-overlay-btn video-overlay-btn--play" type="button" aria-label="Play preview" ${previewData} onclick="event.stopPropagation(); HomeViewHelpers.playAppleBrowsePreview(this)"><svg width="24" height="24" viewBox="0 0 24 24" fill="currentColor"><path d="M8 5v14l11-7z"/></svg></button>` : ''}
                </div><div class="apple-title">${title}</div>${subtitle ? `<div class="apple-sub">${subtitle}</div>` : ''}
            </div>`;
        }
        return `<div class="home-top-song-item" onclick="${click}">
            <div class="home-top-song-item__thumb"${canPreview ? ` onclick="event.preventDefault(); event.stopPropagation(); HomeViewHelpers.playAppleBrowsePreview(this.querySelector('.home-top-song-item__play'))"` : ''}><img src="${image}" alt="${appleAttribute(item.title)}" loading="lazy" />
                ${canPreview ? `<button class="home-top-song-item__play" type="button" aria-label="Play preview" data-home-trending-track="true" ${previewData} data-track-title="${appleAttribute(item.title)}" data-track-artist="${appleAttribute(item.subtitle)}" data-cover="${image}" onclick="event.stopPropagation(); HomeViewHelpers.playAppleBrowsePreview(this)">
                <span class="playback-glyph" aria-hidden="true"><svg class="playback-icon playback-icon--play" viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg><svg class="playback-icon playback-icon--pause" viewBox="0 0 24 24"><path d="M7 5h4v14H7zM13 5h4v14h-4z"/></svg></span></button>` : ''}
            </div><div class="home-top-song-item__info"><div class="home-top-song-item__title">${title}</div>${subtitle ? `<div class="home-top-song-item__album">${subtitle}</div>` : ''}</div>
        </div>`;
    }

    async function playAppleBrowsePreview(button) {
        if (!button || button.disabled) return;
        const resolvePreview = async () => {
            if (button.dataset.preview) return button.dataset.preview;
            button.disabled = true;
            button.setAttribute('aria-busy', 'true');
            try {
                const params = new URLSearchParams({ id: button.dataset.appleId, type: button.dataset.appleType });
                const response = await global.fetch(`/api/apple/browse/preview?${params}`);
                const data = await response.json();
                if (!response.ok || !data.success || !data.previewUrl) throw new Error(data.error || 'Preview unavailable.');
                button.dataset.preview = data.previewUrl;
                return data.previewUrl;
            } finally {
                button.disabled = false;
                button.removeAttribute('aria-busy');
            }
        };
        if (button.dataset.appleType === 'video') {
            try {
                await global.DeezSpoTagVideoPreview?.play(await resolvePreview());
            } catch (error) {
                showToast(error.message || 'Preview unavailable.', 'error');
            }
            return;
        }
        if (!global.DeezerUnifiedPlayback?.play) return;
        const storedVolume = Number.parseInt(global.localStorage.getItem('previewVolume') || '', 10);
        const sourceKey = button.dataset.appleId
            ? `apple:${button.dataset.appleType}:${button.dataset.appleId}`
            : `preview:${button.dataset.preview}`;
        await global.DeezerUnifiedPlayback.play({
            page: 'home', source: 'apple', button,
            key: button.dataset.preview ? `preview:${button.dataset.preview}` : sourceKey, sourceKey,
            volume: Number.isFinite(storedVolume) ? Math.max(0, Math.min(1, storedVolume / 100)) : 0.8,
            mediaMetadata: { title: button.dataset.trackTitle, artist: button.dataset.trackArtist, coverUrl: button.dataset.cover || '' },
            startFailedMessage: 'Playback failed for this track.', unavailableMessage: 'Preview unavailable.',
            onStateChange: state => setHomeTrendingPlaybackState(button, state),
            resolve: async () => {
                try {
                    const url = await resolvePreview();
                    return { url, key: `preview:${url}` };
                } catch (error) {
                    showToast(error.message || 'Preview unavailable.', 'error');
                    return { url: '', silentUnavailable: true };
                }
            }
        });
    }

    function buildYouTubeBrowseClick(item) {
        const url = String(item.url || '');
        if (!url.startsWith('https://music.youtube.com/')) return 'void(0)';
        if ((item.type === 'track' || item.type === 'video') && /^[A-Za-z0-9_-]{11}$/.test(String(item.id || ''))) return `HomeViewHelpers.playYouTubeBrowse(this.querySelector('.home-top-song-item__play[data-youtube-id]'))`;
        if (item.type === 'playlist' || item.type === 'album') {
            let id = String(item.browseId || item.id || '');
            if (!id && item.type === 'playlist') id = 'VL' + (new URL(url).searchParams.get('list') || '');
            if (!/^(VL[A-Za-z0-9_-]{10,}|MPRE[A-Za-z0-9_-]+)$/.test(id)) return 'void(0)';
            return `globalThis.location.href='/Tracklist?id=${encodeURIComponent(id)}&type=${item.type}&source=ytmusic'`;
        }
        return item.type === 'browse-category'
            ? `openYouTubeCategory('${encodeURIComponent(item.browseId || item.id)}','${encodeURIComponent(item.params || '')}')`
            : 'void(0)';
    }

    function renderYouTubeBrowseItem(item, renderer) {
        if (!String(item.url || '').startsWith('https://music.youtube.com/')) return '';
        let html = renderer({ ...item, source: 'ytmusic', image: item.coverUrl || item.image || '' });
        if (item.type !== 'track' && item.type !== 'video') return html;
        const id = String(item.id || item.videoId || '');
        if (!/^[A-Za-z0-9_-]{11}$/.test(id)) return '';
        if (html.includes('data-home-trending-track="true"') && html.includes('data-youtube-id="')) return html;
        const data = `data-youtube-id="${appleAttribute(id)}" data-track-title="${appleAttribute(item.title)}" data-track-artist="${appleAttribute(item.subtitle)}" data-cover="${appleAttribute(item.coverUrl)}"`;
        const play = `<button class="home-top-song-item__play" type="button" aria-label="Play" ${data} onclick="event.preventDefault(); event.stopPropagation(); HomeViewHelpers.playYouTubeBrowse(this)"><span class="playback-glyph" aria-hidden="true"><svg class="playback-icon playback-icon--play" viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg><svg class="playback-icon playback-icon--pause" viewBox="0 0 24 24"><path d="M7 5h4v14H7zM13 5h4v14h-4z"/></svg></span></button>`;
        // Reuse existing thumbnail play control; album/playlist browsing remains native.
        html = html.replace(/(<div class="home-top-song-item__thumb"[^>]*>[\s\S]*?)(<\/div>)/, '$1' + play + '$2');
        if (!html.includes('data-youtube-id')) html = html.replace(/(<div class="(?:playlist-image|top-genres-art)"[^>]*>)/, '$1' + play);
        return html;
    }

    async function playYouTubeBrowse(button) {
        if (!button || button.disabled) return;
        const id = button.dataset.youtubeId;
        if (!/^[A-Za-z0-9_-]{11}$/.test(id || '')) return;
        const volume = Number.parseInt(global.localStorage.getItem('previewVolume') || '', 10);
        await global.DeezerUnifiedPlayback.play({
            page: 'home', source: 'ytmusic', button, key: `youtube:${id}`, sourceKey: `youtube:${id}`,
            volume: Number.isFinite(volume) ? Math.max(0, Math.min(1, volume / 100)) : 0.8,
            mediaMetadata: { title: button.dataset.trackTitle, artist: button.dataset.trackArtist, coverUrl: button.dataset.cover || '' },
            onStateChange: state => setHomeTrendingPlaybackState(button, state),
            startFailedMessage: 'YouTube playback failed.', unavailableMessage: 'YouTube stream unavailable.',
            resolve: async () => {
                const response = await global.fetch(`/api/youtube/browse/playback?id=${encodeURIComponent(id)}`);
                const data = await response.json();
                if (!response.ok || !data.success || !data.url) throw new Error(data.error || 'YouTube stream unavailable.');
                return { url: data.url, key: `youtube:${id}` };
            }
        });
    }

    async function downloadYouTubeBrowse(button) {
        const id = button?.dataset.youtubeId;
        if (!/^[A-Za-z0-9_-]{11}$/.test(id || '') || button.disabled) return;
        const download = global.DeezSpoTagDownload;
        if (!download?.apiFetch) { showToast('Download client unavailable.', 'error'); return; }
        button.disabled = true;
        try {
            const context = download.buildIntentContext({});
            const response = await download.apiFetch('/api/download/intent', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ resolveImmediately: true, intents: [{ sourceService: 'ytmusic', sourceUrl: `https://music.youtube.com/watch?v=${id}`, preferredEngine: context.preferredEngine || 'auto', quality: context.preferredQuality }] }) });
            const data = await response.json();
            const results = data.results || data.items || [];
            if (!response.ok || data.success === false || results.some(result => result.success === false)) throw new Error(results.find(result => result.success === false)?.message || data.message || 'Download identity could not be resolved.');
            showToast('Added to download queue.', 'success');
        } catch (error) { showToast(error.message || 'Download failed.', 'error'); }
        finally { button.disabled = false; }
    }

    function buildHomeClick(item) {
        const encodedId = encodeURIComponent(item.id || '');
        const source = (item.source || '').toString().toLowerCase();
        if (source === 'apple') return buildAppleBrowseClick(item);
        if (source === 'ytmusic') return buildYouTubeBrowseClick(item);
        if (source === 'spotify') {
            return buildSpotifyHomeClick(item, encodedId);
        }

        if (item.target) {
            const targetClick = buildTargetHomeClick(item.target);
            if (targetClick) {
                return targetClick;
            }
        }

        return buildDefaultHomeClick(item, encodedId);
    }

    function buildSpotifyHomeClick(item, encodedId) {
        const type = (item.type || 'playlist').toString().toLowerCase();
        if (type === 'artist') {
            return "openSpotifyArtist('" + encodedId + "', '" + encodeURIComponent(item.name || '') + "')";
        }
        return "openSpotifyTracklist('" + encodedId + "', '" + encodeURIComponent(type) + "')";
    }

    function buildTargetHomeClick(targetValue) {
        const target = targetValue.toString();
        const tracklistTargets = [
            ['/playlist/', 'playlist'],
            ['/album/', 'album'],
            ['/smarttracklist/', 'smarttracklist']
        ];
        for (const [prefix, type] of tracklistTargets) {
            if (target.startsWith(prefix)) {
                return "openTracklist('" + encodeURIComponent(target.replace(prefix, '')) + "', '" + type + "')";
            }
        }
        if (target.startsWith('/artist/')) {
            return "openArtist('" + encodeURIComponent(target.replace('/artist/', '')) + "')";
        }
        if (target.startsWith('/channels/')) {
            return "openHomeChannel('" + target.replace(/^\/+/, '') + "')";
        }
        return '';
    }

    function buildDefaultHomeClick(item, encodedId) {
        const type = (item.type || '').toString().toLowerCase();
        const tracklistTypes = new Set(['playlist', 'album', 'show', 'smarttracklist']);
        if (tracklistTypes.has(type)) {
            return "openTracklist('" + encodedId + "', '" + type + "')";
        }
        if (type === 'artist') {
            return "openArtist('" + encodedId + "')";
        }
        if (type === 'channel') {
            const channelTarget = item.target || ('channels/' + (item.id || ''));
            return "openHomeChannel('" + channelTarget + "')";
        }
        return "openTracklist('" + encodedId + "', 'smarttracklist')";
    }

    function openSpotifyArtistFallback(artistId, artistName) {
        const normalizedName = (artistName || '').toString().trim();
        if (normalizedName) {
            global.location.href = '/Search?term=' + encodeURIComponent(normalizedName) + '&type=artist';
            return;
        }
        if (artistId) {
            global.location.href = '/Artist?id=' + encodeURIComponent(artistId) + '&source=spotify';
        }
    }

    async function openSpotifyArtist(id, encodedName) {
        const artistId = safeDecode(id).trim();
        const artistName = safeDecode(encodedName).trim();
        if (!artistId) {
            openSpotifyArtistFallback('', artistName);
            return;
        }
        global.location.href = '/Artist?id=' + encodeURIComponent(artistId) + '&source=spotify';
    }

    global.HomeViewHelpers = {
        showToast: showToast,
        setHomeTrendingPreviewButtonState: setHomeTrendingPreviewButtonState,
        setHomeTrendingPlaybackState: setHomeTrendingPlaybackState,
        clearHomeTrendingPlayingMarkers: clearHomeTrendingPlayingMarkers,
        normalizeHomeDeezerPlaybackContext: normalizeHomeDeezerPlaybackContext,
        buildHomeDeezerStreamUrl: buildHomeDeezerStreamUrl,
        fetchHomeDeezerPlaybackContext: fetchHomeDeezerPlaybackContext,
        renderTopGenresItem: renderTopGenresItem,
        buildSpotifyBrowseClick: buildSpotifyBrowseClick,
        buildHomeClick: buildHomeClick,
        buildAppleBrowseClick: buildAppleBrowseClick,
        renderAppleBrowseItem: renderAppleBrowseItem,
        renderYouTubeBrowseItem: renderYouTubeBrowseItem,
        buildYouTubeBrowseClick: buildYouTubeBrowseClick,
        playYouTubeBrowse: playYouTubeBrowse,
        downloadYouTubeBrowse: downloadYouTubeBrowse,
        playAppleBrowsePreview: playAppleBrowsePreview,
        openSpotifyArtistFallback: openSpotifyArtistFallback,
        openSpotifyArtist: openSpotifyArtist
    };
})(globalThis);
