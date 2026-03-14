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

    function setHomeTrendingPreviewButtonState(button, isPlaying) {
        if (!button) {
            return;
        }
        button.classList.toggle('is-playing', isPlaying);
        const row = button.closest('.home-top-song-item');
        if (row) {
            row.classList.toggle('is-playing', isPlaying);
        }
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
                || item.picture
                || item.cover
                || item.artwork
                || buildDeezerImageUrlWithSize(imageHash, imageType, 250, 80)
                || buildDeezerImageUrl(imageHash, imageType)
                || '');
        const click = source === 'spotify' ? buildSpotifyBrowseClick(item) : buildHomeClick(item);
        const artStyle = image ? "style=\"background-image: url('" + image + "');\"" : '';
        const platformIcon = source === 'spotify' ? 'spotify' : 'deezer';
        const subtitleCandidate = (item.subtitle || item.artist || item.artists || item.owner || item.ownerName || '').toString().trim();
        const subtitle = /^(spotify|deezer)$/i.test(subtitleCandidate) ? '' : escapeHtml(subtitleCandidate);
        return "\n        <div class=\"top-genres-card top-genres-card--thumb\" onclick=\"" + click + "\" style=\"position: relative;\">\n            <div class=\"top-genres-platform\" style=\"--platform-icon: url('/images/availability/" + platformIcon + ".svg');\"></div>\n            <div class=\"top-genres-content\">\n                <div class=\"top-genres-title\">" + title + "</div>\n                " + (subtitle ? "<div class=\"top-genres-subtitle\">" + subtitle + "</div>" : '') + "\n            </div>\n            <div class=\"top-genres-art\" " + artStyle + "></div>\n        </div>\n    ";
    }

    function buildHomeClick(item) {
        const encodedId = encodeURIComponent(item.id || '');
        const source = (item.source || '').toString().toLowerCase();
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
        normalizeHomeDeezerPlaybackContext: normalizeHomeDeezerPlaybackContext,
        buildHomeDeezerStreamUrl: buildHomeDeezerStreamUrl,
        fetchHomeDeezerPlaybackContext: fetchHomeDeezerPlaybackContext,
        renderTopGenresItem: renderTopGenresItem,
        buildSpotifyBrowseClick: buildSpotifyBrowseClick,
        buildHomeClick: buildHomeClick,
        openSpotifyArtistFallback: openSpotifyArtistFallback,
        openSpotifyArtist: openSpotifyArtist
    };
})(globalThis);
