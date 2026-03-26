function normalizeSpotifyBiographyText(biography) {
    const biographyRaw = typeof biography === 'string' ? biography.trim() : '';
    if (!biographyRaw) {
        return '';
    }

    const temp = document.createElement('div');
    temp.innerHTML = biographyRaw;
    return (temp.textContent || '').replaceAll(/[\s\u00A0]+/g, ' ').trim();
}

const ARTIST_BIO_CLAMP_CLASS = 'bio-text--clamped';
let artistBiographyToggleInitialized = false;
let artistBiographyResizeAnimationFrame = 0;

function isUnavailableBiographyText(text) {
    return /^biography unavailable/i.test(text) || /^n\/?a$/i.test(text);
}

function biographyRequiresClamp(bioEl) {
    const wasExpanded = bioEl.dataset.expanded === 'true';
    const hadClampClass = bioEl.classList.contains(ARTIST_BIO_CLAMP_CLASS);

    bioEl.classList.add(ARTIST_BIO_CLAMP_CLASS);
    bioEl.dataset.expanded = 'false';
    const requiresClamp = (bioEl.scrollHeight - bioEl.clientHeight) > 1;

    if (wasExpanded) {
        bioEl.classList.remove(ARTIST_BIO_CLAMP_CLASS);
        bioEl.dataset.expanded = 'true';
    } else if (!hadClampClass) {
        bioEl.classList.remove(ARTIST_BIO_CLAMP_CLASS);
    }

    return requiresClamp;
}

function updateArtistBiographyToggle(forceCollapse = true) {
    const bioEl = document.getElementById('artistBiography');
    const toggleEl = document.getElementById('artistBiographyToggle');
    if (!bioEl || !toggleEl) {
        return;
    }

    const biographyText = (bioEl.textContent || '').trim();
    if (!biographyText || isUnavailableBiographyText(biographyText)) {
        bioEl.dataset.expanded = 'false';
        bioEl.classList.remove(ARTIST_BIO_CLAMP_CLASS);
        toggleEl.hidden = true;
        toggleEl.setAttribute('aria-expanded', 'false');
        toggleEl.textContent = 'See more';
        return;
    }

    const canExpand = biographyRequiresClamp(bioEl);
    if (!canExpand) {
        bioEl.dataset.expanded = 'false';
        bioEl.classList.remove(ARTIST_BIO_CLAMP_CLASS);
        toggleEl.hidden = true;
        toggleEl.setAttribute('aria-expanded', 'false');
        toggleEl.textContent = 'See more';
        return;
    }

    const shouldExpand = !forceCollapse && bioEl.dataset.expanded === 'true';
    if (shouldExpand) {
        bioEl.classList.remove(ARTIST_BIO_CLAMP_CLASS);
        bioEl.dataset.expanded = 'true';
        toggleEl.setAttribute('aria-expanded', 'true');
        toggleEl.textContent = 'See less';
    } else {
        bioEl.classList.add(ARTIST_BIO_CLAMP_CLASS);
        bioEl.dataset.expanded = 'false';
        toggleEl.setAttribute('aria-expanded', 'false');
        toggleEl.textContent = 'See more';
    }

    toggleEl.hidden = false;
}

function initializeArtistBiographyToggle() {
    if (artistBiographyToggleInitialized) {
        return;
    }

    const bioEl = document.getElementById('artistBiography');
    const toggleEl = document.getElementById('artistBiographyToggle');
    if (!bioEl || !toggleEl) {
        return;
    }

    toggleEl.addEventListener('click', () => {
        const expanded = bioEl.dataset.expanded === 'true';
        bioEl.dataset.expanded = expanded ? 'false' : 'true';
        updateArtistBiographyToggle(false);
    });

    globalThis.addEventListener('resize', () => {
        if (artistBiographyResizeAnimationFrame) {
            cancelAnimationFrame(artistBiographyResizeAnimationFrame);
        }

        artistBiographyResizeAnimationFrame = requestAnimationFrame(() => {
            updateArtistBiographyToggle(false);
            artistBiographyResizeAnimationFrame = 0;
        });
    });

    artistBiographyToggleInitialized = true;
}

function sanitizeMediaUrl(url) {
    const value = (url || '').toString().trim();
    if (!value) {
        return '';
    }

    for (let index = 0; index < value.length; index += 1) {
        const codePoint = value.codePointAt(index) ?? 0;
        if (codePoint <= 0x1f || codePoint === 0x7f) {
            return '';
        }
    }

    if (value.startsWith('/') || value.startsWith('./') || value.startsWith('../')) {
        return value;
    }

    try {
        const parsed = new URL(value, globalThis.location?.origin || 'http://localhost');
        const allowedProtocol = parsed.protocol === 'http:' || parsed.protocol === 'https:';
        if (!allowedProtocol) {
            return '';
        }

        return value;
    } catch {
        return '';
    }
}

function toSafeCssImageValue(url) {
    const safeUrl = sanitizeMediaUrl(url);
    if (!safeUrl) {
        return '';
    }

    return `url("${safeUrl.replaceAll('"', '%22')}")`;
}

function applyArtistHeroBackgroundImage(url, markLocalBackground = null) {
    const bgEl = document.querySelector('.artist-page');
    if (!bgEl) {
        return false;
    }

    const cssValue = toSafeCssImageValue(url);
    if (!cssValue) {
        return false;
    }

    bgEl.style.setProperty('--hero-url', cssValue);
    if (markLocalBackground === true) {
        bgEl.dataset.localBackground = 'true';
    } else if (markLocalBackground === false) {
        delete bgEl.dataset.localBackground;
    }

    return true;
}

function setArtistAvatarImageElement(avatarEl, url, altText, markLocalAvatar = null) {
    if (!avatarEl) {
        return false;
    }

    const safeUrl = sanitizeMediaUrl(url);
    if (!safeUrl) {
        return false;
    }

    const image = document.createElement('img');
    image.src = safeUrl;
    image.alt = (altText || 'Artist').toString();
    image.loading = 'lazy';
    image.decoding = 'async';
    avatarEl.replaceChildren(image);

    if (markLocalAvatar === true) {
        avatarEl.dataset.localAvatar = 'true';
    } else if (markLocalAvatar === false) {
        delete avatarEl.dataset.localAvatar;
    }

    return true;
}

function setSpotifyArtistLinkState(artist) {
    const spotifyIdEl = document.getElementById('artistSpotifyId');
    if (spotifyIdEl && artist.id) {
        spotifyIdEl.href = `https://open.spotify.com/artist/${artist.id}`;
        spotifyIdEl.style.display = 'inline-flex';
        spotifyIdEl.dataset.spotifyId = artist.id;
        if (!spotifyIdEl.dataset.defaultLabel) {
            spotifyIdEl.dataset.defaultLabel = spotifyIdEl.innerHTML;
        }
        if (spotifyIdEl.dataset.defaultLabel) {
            spotifyIdEl.innerHTML = spotifyIdEl.dataset.defaultLabel;
        }
        spotifyIdEl.title = `Spotify ID: ${artist.id}`;
    }
}

function setSpotifyArtistGenres(genres) {
    const genresEl = document.getElementById('artistGenres');
    if (!genresEl) {
        return;
    }

    const genreList = Array.isArray(genres) ? genres : [];
    genresEl.replaceChildren();
    genreList.slice(0, 6).forEach((genre) => {
        const value = (genre || '').toString().trim();
        if (!value) {
            return;
        }

        const tag = document.createElement('span');
        tag.className = 'genre-tag';
        tag.textContent = value;
        genresEl.appendChild(tag);
    });
}

function setSpotifyArtistStats(artist) {
    [
        ['artistFollowers', typeof artist.followers === 'number' ? formatCompactNumber(artist.followers) : null],
        ['artistPopularity', typeof artist.popularity === 'number' ? artist.popularity.toString() : null],
        ['artistMonthlyListeners', typeof artist.monthlyListeners === 'number' ? formatCompactNumber(artist.monthlyListeners) : '—'],
        ['artistRank', typeof artist.rank === 'number' ? `#${artist.rank.toLocaleString()}` : '—'],
        ['artistTotalAlbums', typeof artist.totalAlbums === 'number' ? artist.totalAlbums.toString() : '—']
    ].forEach(([id, value]) => {
        const element = document.getElementById(id);
        if (element && value !== null) {
            element.textContent = value;
        }
    });

    const verifiedBadgeEl = document.getElementById('artistVerifiedBadge');
    if (verifiedBadgeEl) {
        verifiedBadgeEl.style.display = artist.verified === true ? 'flex' : 'none';
    }
}

function setSpotifyArtistBiography(biography) {
    const bioEl = document.getElementById('artistBiography');
    if (!bioEl) {
        return;
    }

    const biographyText = normalizeSpotifyBiographyText(biography);
    const hasBiography = biographyText && !/^n\/?a$/i.test(biographyText);
    bioEl.textContent = hasBiography ? biographyText : 'Biography unavailable from Spotify.';
    const bioPanel = document.getElementById('spotifyBioPanel');
    if (bioPanel) {
        bioPanel.style.display = 'block';
    }

    initializeArtistBiographyToggle();
    updateArtistBiographyToggle(true);
}

globalThis.refreshArtistBiographyClamp = function refreshArtistBiographyClamp(forceCollapse = false) {
    initializeArtistBiographyToggle();
    updateArtistBiographyToggle(forceCollapse);
};

function applySpotifyArtistProfile(artist) {
    if (!artist) {
        return;
    }
    libraryState.currentSpotifyArtist = artist;
    updateTopSongsTracklistLink(artist);
    setSpotifyArtistLinkState(artist);
    setSpotifyArtistGenres(artist.genres);
    setSpotifyArtistStats(artist);
    setSpotifyArtistBiography(artist.biography);

    const bestImage = selectImage(artist.images, 'large');
    const avatarImage = selectImage(artist.images, 'medium') || bestImage;

    const avatarEl = document.getElementById('artistAvatar');
    if (avatarEl && avatarImage && avatarEl.dataset.localAvatar !== 'true') {
        setArtistAvatarImageElement(avatarEl, avatarImage, artist.name || 'Artist');
    }

    libraryState.artistVisuals.headerImageUrl = artist.headerImageUrl || null;
    const spotifyImages = [];
    const pushSpotifyVisual = (url, label) => {
        const value = (url || '').toString().trim();
        if (!value) {
            return;
        }
        spotifyImages.push({
            url: value,
            label,
            source: 'spotify'
        });
    };
    pushSpotifyVisual(bestImage, artist.name ? `Spotify • ${artist.name}` : 'Spotify');
    pushSpotifyVisual(artist.headerImageUrl, artist.name ? `Spotify • ${artist.name} header` : 'Spotify header');
    const galleryRaw = Array.isArray(artist.gallery) ? artist.gallery : [];
    const gallery = galleryRaw
        .map((item) => {
            if (!item) {
                return '';
            }
            if (typeof item === 'string') {
                return item.trim();
            }
            if (typeof item === 'object') {
                return (item.url || item.Url || item.src || item.Src || '').toString().trim();
            }
            return '';
        })
        .filter(Boolean);
    libraryState.artistVisuals.gallery = gallery;
    gallery.forEach((url, index) => {
        pushSpotifyVisual(url, artist.name
            ? `Spotify • ${artist.name} gallery ${index + 1}`
            : `Spotify gallery ${index + 1}`);
    });
    libraryState.artistVisuals.spotifyImages = spotifyImages;

    const bgEl = document.querySelector('.artist-page');
    const heroImage = artist.headerImageUrl || bestImage;
    if (bgEl && heroImage && bgEl.dataset.localBackground !== 'true') {
        applyArtistHeroBackgroundImage(heroImage);
    }

    const artistIdValue = document.querySelector('.artist-page')?.dataset?.artistId || '';
    renderArtistVisualPicker(artistIdValue);
    applyStoredArtistVisuals(artistIdValue);
    loadExternalArtistVisuals(artist?.name || '', artistIdValue);
}

function initArtistActionsDropdown() {
    const toggleButton = document.getElementById('artistActionsToggle');
    const dropdown = document.getElementById('artistActionsDropdown');
    const cacheRefreshButton = document.getElementById('spotify-cache-refresh-button');
    const cachePanel = document.getElementById('spotify-cache-panel');

    if (!toggleButton || !dropdown) {
        return;
    }

    dropdown.setAttribute('role', 'menu');
    dropdown.querySelectorAll('.dropdown-item').forEach((item) => {
        item.setAttribute('role', 'menuitem');
    });

    const getMenuItems = () => Array.from(dropdown.querySelectorAll('.dropdown-item'))
        .filter((item) => !item.disabled && item.offsetParent !== null);

    const closeDropdown = (restoreFocus = false) => {
        dropdown.classList.remove('is-open');
        toggleButton.setAttribute('aria-expanded', 'false');
        if (restoreFocus) {
            toggleButton.focus();
        }
    };

    const openDropdown = () => {
        dropdown.classList.add('is-open');
        toggleButton.setAttribute('aria-expanded', 'true');
    };

    toggleButton.addEventListener('click', (event) => {
        event.stopPropagation();
        const isOpen = dropdown.classList.contains('is-open');
        if (isOpen) {
            closeDropdown();
            return;
        }

        openDropdown();
        const firstItem = getMenuItems()[0];
        if (firstItem) {
            firstItem.focus();
        }
        if (cachePanel) {
            cachePanel.classList.add('is-open');
        }
    });

    toggleButton.addEventListener('keydown', (event) => {
        if (event.key !== 'ArrowDown' && event.key !== 'Enter' && event.key !== ' ') {
            return;
        }

        event.preventDefault();
        if (!dropdown.classList.contains('is-open')) {
            openDropdown();
        }
        const firstItem = getMenuItems()[0];
        if (firstItem) {
            firstItem.focus();
        }
    });

    dropdown.addEventListener('keydown', (event) => {
        if (event.key === 'Escape') {
            event.preventDefault();
            closeDropdown(true);
            return;
        }

        if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') {
            return;
        }

        const items = getMenuItems();
        if (!items.length) {
            return;
        }

        event.preventDefault();
        const currentIndex = items.indexOf(document.activeElement);
        const step = event.key === 'ArrowDown' ? 1 : -1;
        let nextIndex;
        if (currentIndex < 0) {
            nextIndex = step > 0 ? 0 : items.length - 1;
        } else {
            nextIndex = (currentIndex + step + items.length) % items.length;
        }
        items[nextIndex].focus();
    });

    document.addEventListener('click', (event) => {
        if (!dropdown.contains(event.target) && !toggleButton.contains(event.target)) {
            closeDropdown();
        }
    });

    document.addEventListener('keydown', (event) => {
        if (event.key === 'Escape' && dropdown.classList.contains('is-open')) {
            closeDropdown(true);
        }
    });

    if (cacheRefreshButton && cachePanel) {
        cacheRefreshButton.addEventListener('click', () => {
            cachePanel.classList.add('is-open');
        });
    }
}
