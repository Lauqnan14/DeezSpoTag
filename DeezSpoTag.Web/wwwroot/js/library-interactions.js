(function initLibraryInteractionsNamespace() {
    if (globalThis.DeezSpoTagLibraryInteractions) {
        return;
    }

    const interactionState = {
        dropdownBound: false,
        handlersBound: false
    };

    function bindIndexActionsDropdown() {
        const indexToggle = document.getElementById('indexActionsToggle');
        const indexDropdown = document.getElementById('indexActionsDropdown');
        if (!indexToggle || !indexDropdown || interactionState.dropdownBound) {
            return;
        }
        interactionState.dropdownBound = true;

        const closeDropdown = () => {
            indexDropdown.classList.remove('is-open');
            indexToggle.setAttribute('aria-expanded', 'false');
        };
        const openDropdown = () => {
            indexDropdown.classList.add('is-open');
            indexToggle.setAttribute('aria-expanded', 'true');
        };
        const getFocusableItems = () => Array.from(indexDropdown.querySelectorAll('button.dropdown-item:not([disabled])'));
        const focusDropdownItem = (offset) => {
            const items = getFocusableItems();
            if (!items.length) {
                return;
            }
            const activeElement = document.activeElement instanceof HTMLElement ? document.activeElement : null;
            const currentIndex = activeElement ? items.indexOf(activeElement) : -1;
            let nextIndex = items.length - 1;
            if (offset >= 0) {
                nextIndex = 0;
            }
            if (currentIndex >= 0) {
                nextIndex = (currentIndex + offset + items.length) % items.length;
            }
            items[nextIndex]?.focus();
        };

        indexToggle.addEventListener('click', (event) => {
            event.stopPropagation();
            const isOpen = indexDropdown.classList.contains('is-open');
            if (isOpen) {
                closeDropdown();
                return;
            }
            openDropdown();
        });

        indexToggle.addEventListener('keydown', (event) => {
            if (event.key === 'Enter' || event.key === ' ' || event.key === 'ArrowDown') {
                event.preventDefault();
                openDropdown();
                focusDropdownItem(1);
            } else if (event.key === 'Escape') {
                closeDropdown();
            }
        });

        indexDropdown.addEventListener('keydown', (event) => {
            if (event.key === 'Escape') {
                event.preventDefault();
                closeDropdown();
                indexToggle.focus();
                return;
            }
            if (event.key === 'ArrowDown') {
                event.preventDefault();
                focusDropdownItem(1);
                return;
            }
            if (event.key === 'ArrowUp') {
                event.preventDefault();
                focusDropdownItem(-1);
            }
        });

        document.addEventListener('click', (event) => {
            if (!indexDropdown.contains(event.target) && event.target !== indexToggle) {
                closeDropdown();
            }
        });
    }

    function bindGlobalLibraryInteractionHandlers(handlers) {
        if (interactionState.handlersBound || !handlers || typeof handlers !== 'object') {
            return;
        }
        interactionState.handlersBound = true;

        document.addEventListener('click', event => {
            const topSongPlayButton = event.target.closest('#spotifyTopTracksList .top-song-item__play.track-play');
            if (topSongPlayButton) {
                event.preventDefault();
                event.stopPropagation();
                event.stopImmediatePropagation();
                handlers.playSpotifyTrackInApp?.(topSongPlayButton.dataset.spotifyUrl || '', topSongPlayButton);
                return;
            }

            const topSongThumb = event.target.closest('#spotifyTopTracksList .top-song-item__thumb');
            if (topSongThumb) {
                const thumbPlayButton = topSongThumb.querySelector('.top-song-item__play.track-play');
                if (thumbPlayButton) {
                    event.preventDefault();
                    event.stopPropagation();
                    event.stopImmediatePropagation();
                    handlers.playSpotifyTrackInApp?.(thumbPlayButton.dataset.spotifyUrl || '', thumbPlayButton);
                    return;
                }
            }

            const target = event.target.closest('[data-spotify-url]');
            if (!target) {
                return;
            }
            event.preventDefault();
            event.stopPropagation();
            const url = target.dataset.spotifyUrl;
            if (!url) {
                return;
            }
            if (target.classList.contains('track-action')) {
                handlers.playSpotifyTrackInApp?.(url, target);
                return;
            }
            handlers.handleSpotifyRedirect?.(url, {
                title: target.dataset.spotifyTitle || '',
                artist: target.dataset.spotifyArtist || ''
            });
        });

        document.addEventListener('click', event => {
            const target = event.target.closest('[data-library-spectrogram-url]');
            if (!target) {
                return;
            }

            event.preventDefault();
            event.stopPropagation();
            const spectrogramUrl = target.dataset.librarySpectrogramUrl;
            const spectrogramTitle = target.dataset.librarySpectrogramTitle || 'Track';
            const trackId = target.dataset.libraryTrackId || '';
            const trackFilePath = target.dataset.libraryTrackFilePath || '';
            if (!spectrogramUrl) {
                return;
            }

            const menu = target.closest('details.track-actions-menu');
            if (menu) {
                menu.removeAttribute('open');
            }

            handlers.openLibrarySpectrogramModal?.(spectrogramUrl, spectrogramTitle, trackId, trackFilePath);
        });

        document.addEventListener('click', event => {
            const button = event.target.closest('[data-library-play-track]');
            if (!button) {
                return;
            }

            event.preventDefault();
            event.stopPropagation();
            const trackId = button.dataset.libraryPlayTrack;
            const preferredPath = button.dataset.libraryPlayPath || '';
            if (!trackId) {
                return;
            }

            handlers.playLocalLibraryTrackInApp?.(trackId, button, preferredPath);
        });
    }

    globalThis.DeezSpoTagLibraryInteractions = {
        bindIndexActionsDropdown,
        bindGlobalLibraryInteractionHandlers
    };
})();
