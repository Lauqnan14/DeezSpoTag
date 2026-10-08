(function (global) {
    'use strict';

    function normalizeState(state) {
        const normalized = String(state || '').trim().toLowerCase();
        if (normalized === 'requested' || normalized === 'playing' || normalized === 'paused' || normalized === 'ended' || normalized === 'error') {
            return normalized;
        }
        return 'idle';
    }

    function transitionButtonState(button, state, options = {}) {
        const normalized = normalizeState(state);
        const isRequested = normalized === 'requested';
        const isPlaying = normalized === 'playing';
        // A paused track is still the active session: the row stays highlighted and the
        // button offers resume rather than being torn down. Only a terminal or idle
        // state releases the session.
        const isActive = isRequested || isPlaying || normalized === 'paused';

        if (!button) {
            if (!isActive && typeof options.clear === 'function') {
                options.clear();
            }
            return;
        }

        button.classList.toggle('is-starting', isRequested);

        if (isActive) {
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
