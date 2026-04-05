(function (global) {
    'use strict';

    function normalizeState(state) {
        const normalized = String(state || '').trim().toLowerCase();
        if (normalized === 'requested' || normalized === 'playing' || normalized === 'ended' || normalized === 'error') {
            return normalized;
        }
        return 'idle';
    }

    function transitionButtonState(button, state, options = {}) {
        const normalized = normalizeState(state);
        const isRequested = normalized === 'requested';
        const isPlaying = normalized === 'playing';

        if (!button) {
            if (!isRequested && !isPlaying && typeof options.clear === 'function') {
                options.clear();
            }
            return;
        }

        button.classList.toggle('is-starting', isRequested);

        if (isRequested || isPlaying) {
            button.dataset.playbackState = normalized;
        } else {
            delete button.dataset.playbackState;
        }

        if (typeof options.setPlaying === 'function') {
            options.setPlaying(button, isPlaying);
        }
        if (typeof options.onTransition === 'function') {
            options.onTransition(button, normalized);
        }
    }

    function beginRequest(session, intentKey) {
        if (!session || typeof session !== 'object') {
            return null;
        }

        const normalizedIntent = intentKey ? String(intentKey) : '';
        if (normalizedIntent && session.pendingKey === normalizedIntent) {
            return null;
        }

        const nextRequestId = Number.isFinite(Number(session.requestId))
            ? Number(session.requestId) + 1
            : 1;
        session.requestId = nextRequestId;
        session.pendingKey = normalizedIntent || null;

        return {
            requestId: nextRequestId,
            isStale: function () {
                return session.requestId !== nextRequestId;
            },
            finalize: function () {
                if (session.requestId === nextRequestId) {
                    session.pendingKey = null;
                }
            }
        };
    }

    global.DeezerPlaybackState = {
        transitionButtonState: transitionButtonState,
        beginRequest: beginRequest
    };
})(globalThis);
