(function initSpotifyUrlHelpers(globalObj) {
    'use strict';

    function buildSpotifyWebUrl(uri) {
        if (!uri) {
            return '';
        }
        const value = String(uri).trim();
        if (!value) {
            return '';
        }
        if (value.startsWith('http://') || value.startsWith('https://')) {
            return value;
        }
        if (value.startsWith('spotify:')) {
            const parts = value.split(':');
            if (parts.length >= 3 && parts[1] && parts[2]) {
                return `https://open.spotify.com/${parts[1]}/${parts[2]}`;
            }
        }
        return '';
    }

    function parseSpotifyUrl(url) {
        if (!url) {
            return null;
        }
        const trimmed = String(url).trim();
        if (trimmed.startsWith('spotify:')) {
            const uriParts = trimmed.split(':');
            if (uriParts.length >= 3 && uriParts[1] && uriParts[2]) {
                return { type: uriParts[1].toLowerCase(), id: uriParts[2] };
            }
        }

        const directMatch = /open\.spotify\.com\/(?:intl-[a-z]+\/)?(album|playlist|track|show|episode|artist|station)\/([a-z0-9]+)/i.exec(trimmed);
        if (directMatch) {
            return { type: directMatch[1].toLowerCase(), id: directMatch[2] };
        }

        try {
            const parsed = new URL(trimmed);
            const segments = parsed.pathname.split('/').filter(Boolean);
            const kindIndex = segments.findIndex((seg) => /^(album|playlist|track|show|episode|artist|station)$/i.test(seg));
            if (kindIndex >= 0 && segments[kindIndex + 1]) {
                return { type: segments[kindIndex].toLowerCase(), id: segments[kindIndex + 1] };
            }
        } catch {
            return null;
        }
        return null;
    }

    function buildSpotifyTrackMatchPayload(link, request) {
        return {
            link: String(link || '').trim(),
            title: String(request?.title || '').trim(),
            artist: String(request?.artist || '').trim(),
            album: String(request?.album || '').trim(),
            isrc: String(request?.isrc || '').trim(),
            durationMs: Number.isFinite(Number(request?.durationMs)) ? Number(request.durationMs) : 0
        };
    }

    globalObj.SpotifyUrlHelpers = Object.freeze({
        buildSpotifyWebUrl,
        parseSpotifyUrl,
        buildSpotifyTrackMatchPayload
    });
})(globalThis);
