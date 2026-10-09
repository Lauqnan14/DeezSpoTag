(() => {
    const librarySections = document.getElementById("libraryPlaylistsSections");
    const autoGrid = document.getElementById("autoToolsGrid");
    const recommendationsGrid = document.getElementById("recommendationsGrid");
    const countEl = document.getElementById("autoPlaylistsCount");
    const libraryEmpty = document.getElementById("libraryPlaylistsEmpty");
    const syncMessageEl = document.getElementById("libraryPlaylistsSyncMessage");
    const autoEmpty = document.getElementById("autoPlaylistsEmpty");
    const recommendationsEmpty = document.getElementById("recommendationsEmpty");
    const warningEl = document.getElementById("autoPlaylistsWarning");

    const hasPlaylistSections = Boolean(librarySections && autoGrid && countEl && libraryEmpty && autoEmpty);
    let syncMessageTimer = null;
    const hasRecommendationSection = Boolean(recommendationsGrid && recommendationsEmpty);
    if (!hasPlaylistSections && !hasRecommendationSection) {
        return;
    }

    const formatCount = (count) => `${count} playlist${count === 1 ? "" : "s"}`;

    const melodayWeekdays = [
        { id: "sunday", label: "Sunday" },
        { id: "monday", label: "Monday" },
        { id: "tuesday", label: "Tuesday" },
        { id: "wednesday", label: "Wednesday" },
        { id: "thursday", label: "Thursday" },
        { id: "friday", label: "Friday" },
        { id: "saturday", label: "Saturday" }
    ];

    const melodayDayparts = [
        { id: "early-morning", order: 0 },
        { id: "morning", order: 1 },
        { id: "noon", order: 2 },
        { id: "afternoon", order: 3 },
        { id: "evening", order: 4 },
        { id: "late-evening", order: 5 }
    ];

    const resolvePlaylistWeekday = (playlist) => {
        const mixId = String(playlist?.id || "").trim().toLowerCase();
        const scheduledDay = melodayWeekdays.find((day) => mixId.endsWith(`-${day.id}`));
        if (scheduledDay) {
            return scheduledDay.id;
        }

        const generatedAt = new Date(playlist?.updated);
        return Number.isNaN(generatedAt.getTime())
            ? ""
            : melodayWeekdays[generatedAt.getDay()].id;
    };

    const resolvePlaylistDaypartOrder = (playlist) => {
        const mixId = String(playlist?.id || "").trim().toLowerCase();
        const daypart = melodayDayparts.find((candidate) =>
            ["direct", "sonic"].some((mode) =>
                mixId.endsWith(`-${candidate.id}-${mode}`)
                || melodayWeekdays.some((day) => mixId.endsWith(`-${candidate.id}-${mode}-${day.id}`))));
        return daypart?.order ?? Number.MAX_SAFE_INTEGER;
    };

    const resolveMelodayCoverUrl = (playlist) => {
        const covers = Array.isArray(playlist?.coverUrls) ? playlist.coverUrls.filter(Boolean) : [];
        return covers.length > 0 ? covers[0] : "";
    };

    const createCoverPlaceholder = () => {
        const placeholder = document.createElement("div");
        placeholder.className = "watchlist-card-art-placeholder";
        const icon = document.createElement("i");
        icon.className = "fa-solid fa-music";
        placeholder.appendChild(icon);
        return placeholder;
    };

    const formatUpdated = (value) => {
        if (!value) {
            return "Recently updated";
        }

        const date = new Date(value);
        if (Number.isNaN(date.getTime())) {
            return String(value);
        }

        return date.toLocaleDateString(undefined, {
            month: "short",
            day: "numeric",
            year: "numeric"
        });
    };

    const uniquePositiveNumbers = (values) => {
        const seen = new Set();
        const output = [];
        values.forEach((value) => {
            const parsed = Number(value);
            if (!Number.isFinite(parsed) || parsed <= 0 || seen.has(parsed)) {
                return;
            }
            seen.add(parsed);
            output.push(parsed);
        });
        return output;
    };

    const setWarning = (message) => {
        if (!warningEl) {
            return;
        }
        if (message) {
            warningEl.textContent = message;
            warningEl.hidden = false;
        } else {
            warningEl.hidden = true;
        }
    };

    const openTracklist = (playlistId, source, libraryId) => {
        if (!playlistId) {
            return;
        }
        const params = new URLSearchParams({
            id: playlistId,
            type: source === "mix" ? "mix" : "playlist",
            source: source
        });
        if (libraryId) {
            params.set("libraryId", libraryId);
        }
        globalThis.location.href = `/Tracklist?${params.toString()}`;
    };

    const confirmDeleteMix = async (playlist) => {
        const message = `Delete ${playlist.name || "this Meloday playlist"} from DeezSpoTag?`;
        if (globalThis.DeezSpoTag?.ui?.confirm) {
            return globalThis.DeezSpoTag.ui.confirm(message, { title: "Delete playlist" });
        }
        return false;
    };

    const deleteMix = async (playlist) => {
        if (!playlist?.id || !playlist?.libraryId) {
            return false;
        }

        if (!await confirmDeleteMix(playlist)) {
            return false;
        }

        const headers = new Headers();
        const csrfToken = document.querySelector('meta[name="deezspotag-csrf-token"]')?.getAttribute("content")?.trim();
        if (csrfToken) {
            headers.set("X-CSRF-TOKEN", csrfToken);
        }

        const response = await fetch(`/api/mixes/${encodeURIComponent(playlist.id)}?libraryId=${encodeURIComponent(playlist.libraryId)}`, {
            method: "DELETE",
            headers,
            credentials: "same-origin"
        });
        if (!response.ok && response.status !== 404) {
            throw new Error(`Delete failed: HTTP ${response.status}`);
        }
        await loadMixes();
        return true;
    };

    const openRecommendationStation = (station, libraryId) => {
        if (!station?.id || !station?.type) {
            return;
        }
        const params = new URLSearchParams({
            id: station.id,
            type: "recommendation",
            source: "recommendations"
        });
        if (station.value) {
            params.set("recommendationValue", station.value);
        }
        if (station.type) {
            params.set("recommendationType", station.type);
        }
        if (libraryId) {
            params.set("libraryId", libraryId);
        }
        globalThis.location.href = `/Tracklist?${params.toString()}`;
    };

    const normalizeRecommendationTitle = (station) => {
        const normalizedName = String(station?.name || "")
            .replace(/^recommendations\s*-\s*/i, "")
            .trim();
        return normalizedName || station?.name || "Recommendation";
    };

    const normalizeRecommendationMode = (station) => {
        const raw = station?.value || station?.type || "";
        return String(raw || "daily")
            .replaceAll("-", " ")
            .trim() || "daily";
    };

    const serverLabels = {
        plex: "Plex",
        jellyfin: "Jellyfin",
        navidrome: "Navidrome",
        ytmusic: "YouTube Music",
        spotify: "Spotify",
        deezer: "Deezer",
        qobuz: "Qobuz",
        tidal: "TIDAL",
        applemusic: "Apple Music"
    };

    const serverIconPaths = {
        plex: "/images/icons/plex.png",
        jellyfin: "/images/icons/jellyfin.png",
        navidrome: "/images/icons/navidrome.png",
        ytmusic: "/images/icons/youtube-music.png",
        spotify: "/images/icons/spotify.png",
        deezer: "/images/icons/deezer.png",
        qobuz: "/images/icons/qobuz.png",
        tidal: "/images/icons/tidal.png",
        applemusic: "/images/icons/apple-music.png"
    };

    // Servers present in the payload, i.e. configured and holding at least one playlist.
    // The section a card belongs to is its own server, so it is never offered as a target.
    let availableSyncTargets = [];

    // Connected servers, independent of whether they hold any library playlists. Meloday mixes
    // are generated locally, so all of these are valid destinations.
    let connectedSyncTargets = [];

    const loadConnectedSyncTargets = async () => {
        try {
            const targets = await fetchJson('/api/library/playlists/sync-targets');
            // Everything connected, both kinds. The panel splits them into its own blocks, so
            // filtering to platforms here would hide the self-hosted servers the user can also
            // push this playlist to.
            connectedSyncTargets = (Array.isArray(targets) ? targets : [])
                .map((target) => ({
                    value: String(target?.value || '').trim().toLowerCase(),
                    kind: String(target?.kind || '').trim().toLowerCase() === 'library' ? 'library' : 'platform',
                    label: String(target?.label || '').trim() || target?.value || ''
                }))
                .filter((target) => target.value && target.label);
        } catch {
            connectedSyncTargets = [];
        }
    };

    // Targets come from the connected-servers endpoint, NOT from the rendered sections. A
    // streaming platform such as YouTube Music never renders a library section of its own, so
    // deriving the list from the sections meant it could never be offered as a destination.
    const setAvailableSyncTargets = (targets) => {
        availableSyncTargets = (Array.isArray(targets) ? targets : [])
            .map((target) => ({
                value: String(target?.value || target?.server || "").toLowerCase(),
                // The kind travels with the entry because the panel groups by it. Anything not
                // explicitly a library is treated as a platform, which matches how the server
                // reports an unrecognised destination.
                kind: String(target?.kind || "").trim().toLowerCase() === "library" ? "library" : "platform",
                label: String(target?.label || target?.displayName || "").trim()
                    || serverLabels[target?.value || target?.server]
                    || target?.value || target?.server || ""
            }))
            .filter((target) => target.value && target.label);
    };

    const syncTargetsFor = (sourceServer) => availableSyncTargets
        .filter((target) => target.value !== String(sourceServer || "").toLowerCase());

    const setSyncMessage = (message, isError = false) => {
        if (!syncMessageEl) {
            return;
        }

        syncMessageEl.textContent = message || "";
        syncMessageEl.hidden = !message;
        syncMessageEl.classList.toggle("is-error", Boolean(isError) && Boolean(message));
    };

    const showSyncMessage = (message, isError = false) => {
        setSyncMessage(message, isError);
        if (!message) {
            return;
        }

        if (syncMessageTimer !== null) {
            window.clearTimeout(syncMessageTimer);
        }
        syncMessageTimer = window.setTimeout(() => {
            syncMessageTimer = null;
            setSyncMessage("");
        }, 6000);
    };

    /// <summary>
    /// Opens or closes one card's sync panel and keeps the stacking order honest. This page holds
    /// the library playlists and the Meloday playlists in two separate containers, and the Melody
    /// one is a positioned stacking context, so a panel left at its default z-index gets painted
    /// behind that container - which hid its own rows and left Sync unclickable. The card and the
    /// section holding it are lifted only while a panel is genuinely open.
    ///
    /// The classes are re-derived from the panels' actual hidden state on every change rather
    /// than toggled in place, so a section can never be left lifted after its panel has closed.
    /// </summary>
    const syncMenuStacking = () => {
        document.querySelectorAll("#libraryPlaylistsSections, #autoToolsGrid")
            .forEach((section) => section.classList.toggle(
                "menu-open",
                section.querySelector(".watchlist-action-dropdown:not([hidden])") !== null));
        document.querySelectorAll(".library-playlist-card, .watchlist-playlist-card-v2")
            .forEach((card) => card.classList.toggle(
                "menu-open",
                card.querySelector(".watchlist-action-dropdown:not([hidden])") !== null));
    };

    /// <summary>
    /// The panels that are open right now. Tracked so opening one closes the others.
    /// <para>
    /// Each card closes only its own panel on a click outside itself, so two panels could end up
    /// open at once and overlap - the earlier one clipped and unreadable behind the newer. Only one
    /// panel is ever meaningful: they are a one-at-a-time choice, and a second one opening is a
    /// new selection, not a second selection.
    /// </para>
    /// </summary>
    let openSyncPanel = null;

    const setMenuOpen = (wrapper, dropdown, toggle, open) => {
        if (open && openSyncPanel && openSyncPanel !== dropdown) {
            openSyncPanel.hidden = true;
            const otherToggle = openSyncPanel.parentElement?.querySelector(".watchlist-kebab-btn");
            otherToggle?.setAttribute("aria-expanded", "false");
        }

        openSyncPanel = open ? dropdown : (openSyncPanel === dropdown ? null : openSyncPanel);
        dropdown.hidden = !open;
        toggle.setAttribute("aria-expanded", String(open));
        syncMenuStacking();
    };


    /// <summary>
    /// Builds one titled block inside the sync panel. The panel's sections are grouped this way so
    /// the eye can tell "these are destinations" from "this is the image" without reading every
    /// row; the divider between blocks is the only thing that separates them.
    /// </summary>
    const syncSection = (title) => {
        const section = document.createElement("div");
        section.className = "library-playlist-sync-section";
        if (title) {
            const heading = document.createElement("div");
            heading.className = "library-playlist-sync-section-title";
            heading.textContent = title;
            section.appendChild(heading);
        }
        return section;
    };

    /// <summary>Upload ceiling and accepted types, shared by the picker and its server-side check.</summary>
    const MAX_ARTWORK_BYTES = 15 * 1024 * 1024;
    const ARTWORK_TYPES = ["image/jpeg", "image/png", "image/webp", "image/gif"];

    /// <summary>
    /// A titled block in the settings panel, built the way the watchlist builds its own so the two
    /// surfaces are the same component and not two things that merely resemble each other.
    /// </summary>
    const settingsSection = (title) => {
        const section = document.createElement('div');
        section.className = 'playlist-settings-section';
        const heading = document.createElement('div');
        heading.className = 'playlist-settings-section-title';
        heading.textContent = title;
        section.appendChild(heading);
        return section;
    };

    const settingsHelp = (text) => {
        const help = document.createElement('div');
        help.className = 'playlist-settings-help';
        help.textContent = text;
        return help;
    };

    /// <summary>
    /// The per-playlist sync settings.
    /// <para>
    /// Opens as a modal using the app's own <c>showModal</c> and the same
    /// <c>playlist-settings-modal</c> dialog class the watchlist playlist settings uses, so it is
    /// the same width, the same scrolling body, and the same Save/Cancel contract. The kebab beside
    /// each card stays a small action list that launches this, exactly as the watchlist does.
    /// </para>
    /// </summary>
    const openPlaylistSyncSettings = async (playlist, libraryTargets, platformTargets) => {
        if (!globalThis.DeezSpoTag?.ui?.showModal) {
            showSyncMessage('Sync settings are unavailable right now.', true);
            return;
        }

        const panel = document.createElement('div');
        panel.className = 'playlist-settings-panel watchlist-playlist-settings library-playlist-sync-settings';

        const intro = document.createElement('div');
        intro.className = 'playlist-settings-intro';
        intro.textContent = 'Choose what this playlist copies to, how often, and which tracks to leave behind.';
        panel.appendChild(intro);

        // Loaded up front so the panel opens with the saved state rather than defaults that the
        // first Save would then overwrite.
        let saved = null;
        try {
            saved = await fetchJson(
                `/api/library/playlists/sync-schedule?source=${encodeURIComponent(playlist.server)}&sourceId=${encodeURIComponent(playlist.id)}`);
        } catch {
            saved = null;
        }

        const savedTargets = new Set(
            (Array.isArray(saved?.targets) ? saved.targets : []).map((t) => String(t).toLowerCase()));
        const excluded = new Set(Array.isArray(saved?.excludedTrackIds) ? saved.excludedTrackIds.map(String) : []);

        let artworkDataUrl = null;
        let cadence = 'manual';
        let tracks = [];

        // ------------------------------------------------------------------ artwork
        const artworkSection = settingsSection('Artwork');
        const artworkGrid = document.createElement('div');
        artworkGrid.className = 'merge-artwork-grid';
        const artworkHint = document.createElement('div');
        artworkHint.className = 'merge-artwork-empty';
        artworkHint.textContent = 'No artwork chosen. The source playlist cover is used.';
        const artworkInput = document.createElement('input');
        artworkInput.type = 'file';
        artworkInput.accept = ARTWORK_TYPES.join(',');
        artworkInput.hidden = true;
        const artworkNote = settingsHelp('Square image, up to 15 MB. JPEG, PNG, WebP or animated GIF/WebP.');
        const artworkActions = document.createElement('div');
        artworkActions.className = 'merge-artwork-actions';
        const artworkPick = document.createElement('button');
        artworkPick.type = 'button';
        artworkPick.className = 'btn btn-outline-primary';
        artworkPick.textContent = 'Choose image';
        const artworkClear = document.createElement('button');
        artworkClear.type = 'button';
        artworkClear.className = 'btn btn-outline-secondary';
        artworkClear.textContent = 'Clear';
        artworkClear.hidden = true;
        artworkActions.append(artworkPick, artworkClear);

        const showArtwork = () => {
            artworkGrid.innerHTML = '';
            if (!artworkDataUrl) {
                artworkGrid.appendChild(artworkHint);
                artworkClear.hidden = true;
                return;
            }
            const tile = document.createElement('article');
            tile.className = 'playlist-artwork-tile is-active';
            const wrap = document.createElement('span');
            wrap.className = 'playlist-artwork-tile__image-wrap';
            const img = document.createElement('img');
            img.src = artworkDataUrl;
            img.alt = 'Selected playlist artwork';
            const active = document.createElement('span');
            active.className = 'playlist-artwork-tile__active';
            active.textContent = 'Active';
            wrap.append(img, active);
            tile.appendChild(wrap);
            artworkGrid.appendChild(tile);
            artworkClear.hidden = false;
        };

        artworkPick.addEventListener('click', () => artworkInput.click());
        artworkClear.addEventListener('click', () => {
            artworkDataUrl = null;
            showArtwork();
        });
        artworkInput.addEventListener('change', () => {
            const file = artworkInput.files && artworkInput.files[0];
            artworkInput.value = '';
            if (!file) {
                return;
            }
            if (!ARTWORK_TYPES.includes(file.type)) {
                artworkNote.textContent = 'Use a JPEG, PNG, WebP or GIF image.';
                return;
            }
            if (file.size > MAX_ARTWORK_BYTES) {
                artworkNote.textContent =
                    `That image is ${(file.size / (1024 * 1024)).toFixed(1)} MB. The limit is 15 MB.`;
                return;
            }
            const reader = new FileReader();
            reader.onload = () => {
                artworkDataUrl = String(reader.result || '');
                artworkNote.textContent = 'Square image, up to 15 MB. JPEG, PNG, WebP or animated GIF/WebP.';
                showArtwork();
            };
            reader.onerror = () => {
                artworkNote.textContent = 'That image could not be read.';
            };
            reader.readAsDataURL(file);
        });
        showArtwork();
        artworkSection.append(artworkGrid, artworkActions, artworkInput, artworkNote);
        panel.appendChild(artworkSection);

        // ------------------------------------------------------------------ description
        const descriptionSection = settingsSection('Description');
        const description = document.createElement('textarea');
        description.className = 'form-control';
        description.rows = 3;
        description.maxLength = 1000;
        description.placeholder = 'Write a description sent to every destination.';
        description.value = saved?.description || playlist.description || '';
        descriptionSection.appendChild(description);
        panel.appendChild(descriptionSection);

        // ------------------------------------------------------------------ destinations
        // Servers and platforms are separate sections because they are different kinds of thing: a
        // self-hosted server and a streaming platform fail, reconnect and report differently, and
        // one flat list would hide that.
        const destinationGroup = (title, targets, help) => {
            const section = settingsSection(title);
            if (targets.length === 0) {
                const none = document.createElement('div');
                none.className = 'playlist-settings-help';
                none.textContent = `No connected ${title.toLowerCase()}.`;
                section.appendChild(none);
                return section;
            }
            const grid = document.createElement('div');
            grid.className = 'artist-watch-options-grid';
            targets.forEach((target) => {
                const row = document.createElement('label');
                row.className = 'checkbox-group';
                const input = document.createElement('input');
                input.type = "checkbox";
                input.className = "library-playlist-sync-target";
                input.value = target.value;
                input.checked = savedTargets.has(target.value);
                const label = document.createElement('span');
                label.textContent = target.label;
                row.append(input, label);
                grid.appendChild(row);
            });
            section.appendChild(grid);
            if (help) {
                section.appendChild(settingsHelp(help));
            }
            return section;
        };

        panel.appendChild(destinationGroup('Servers', libraryTargets, null));
        panel.appendChild(destinationGroup('Platforms', platformTargets, null));

        // ------------------------------------------------------------------ how to sync
        const modeSection = settingsSection('How to sync');
        const modeGrid = document.createElement('div');
        modeGrid.className = 'artist-watch-options-grid';
        [
            { value: "mirror", label: "Mirror tracks", hint: "Match the destination playlist exactly." },
            { value: "append", label: "Append only", hint: "Only add tracks that are missing." }
        ].forEach((mode, index) => {
            const row = document.createElement('label');
            row.className = 'checkbox-group';
            const input = document.createElement('input');
            input.type = 'radio';
            input.name = `library-playlist-sync-mode-${playlist.server}-${playlist.id}`;
            input.className = 'library-playlist-sync-mode-option';
            input.value = mode.value;
            input.checked = index === 0;
            const label = document.createElement('span');
            label.textContent = mode.label;
            row.append(input, label);
            modeGrid.appendChild(row);
        });
        modeSection.appendChild(modeGrid, settingsHelp('Mirror replaces the destination playlist. Append only leaves anything you added yourself.'));
        panel.appendChild(modeSection);

        // ------------------------------------------------------------------ schedule
        const scheduleSection = settingsSection('Sync automatically');
        const scheduleSelect = document.createElement('select');
        scheduleSelect.className = 'form-select ps-schedule-select';
        [
            { value: 'manual', label: 'Never (manual only)' },
            { value: 'every15minutes', label: 'Every 15 minutes' },
            { value: 'hourly', label: 'Hourly' },
            { value: 'every6hours', label: 'Every 6 hours' },
            { value: 'daily', label: 'Daily' }
        ].forEach((option) => {
            scheduleSelect.appendChild(new Option(option.label, option.value));
        });
        const savedCadence = String(saved?.cadence || 'manual').toLowerCase();
        const cadenceOption = Array.from(scheduleSelect.options)
            .find((node) => node.value.toLowerCase() === savedCadence);
        scheduleSelect.value = cadenceOption ? cadenceOption.value : 'manual';
        cadence = scheduleSelect.value;
        const scheduleStatus = document.createElement('div');
        scheduleStatus.className = 'playlist-settings-help';
        if (cadence !== 'manual' && saved?.nextRunAtUtc) {
            scheduleStatus.textContent = `Next automatic sync: ${new Date(saved.nextRunAtUtc).toLocaleString()}`;
        } else {
            scheduleStatus.textContent = 'Manual only. Nothing syncs until you press Sync now.';
        }
        scheduleSection.append(scheduleSelect, scheduleStatus);
        panel.appendChild(scheduleSection);

        // ------------------------------------------------------------------ tracklist
        // Excluding a track here means "do not copy this one out of the library playlist". It is NOT
        // the watchlist blocklist, which stops a track being added to a playlist at all. The label
        // and the hint both say so, because the two controls sit one click apart in the same app.
        const trackSection = settingsSection('Tracks');
        const tracklist = document.createElement('div');
        tracklist.className = 'playlist-sync-tracklist';
        const trackHelp = settingsHelp(
            'Skipped tracks are not copied to any server or platform below. '
            + 'This is not the watchlist blocklist, which stops a track being added to a playlist.');

        const renderTracks = () => {
            tracklist.innerHTML = '';
            if (!Array.isArray(tracks) || tracks.length === 0) {
                const empty = document.createElement('div');
                empty.className = 'playlist-settings-help';
                empty.textContent = 'No tracks to show.';
                tracklist.appendChild(empty);
                return;
            }
            tracks.forEach((track) => {
                const key = String(track.id ?? track.title ?? track.name ?? '');

                const row = document.createElement('div');
                row.className = 'playlist-sync-track';

                // Cover, title and artist, duration, then the control - the same column order the
                // app's own tracklist uses, so the two read as one list.
                const art = document.createElement('div');
                art.className = 'playlist-sync-track__art';
                if (track.coverUrl) {
                    const img = document.createElement('img');
                    img.src = track.coverUrl;
                    img.alt = '';
                    img.loading = 'lazy';
                    img.addEventListener('error', () => art.classList.add('is-empty'));
                    art.appendChild(img);
                } else {
                    art.classList.add('is-empty');
                }

                const text = document.createElement('div');
                text.className = 'playlist-sync-track__text';
                const title = document.createElement('div');
                title.className = 'playlist-sync-track__title';
                title.textContent = track.title || track.name || 'Untitled track';
                const artist = document.createElement('div');
                artist.className = 'playlist-sync-track__artist';
                artist.textContent = track.artist || '';
                text.append(title, artist);

                // The recording identity, not the runtime. The ISRC is the thing that decides
                // whether a track matches the same recording on another service, so it is the
                // useful thing to show next to a track that is about to be copied to one.
                const isrc = document.createElement('span');
                isrc.className = 'playlist-sync-track__isrc';
                if (track.isrc) {
                    isrc.textContent = track.isrc;
                    isrc.title = 'ISRC recording identifier';
                } else {
                    isrc.textContent = 'No ISRC';
                    isrc.classList.add('is-missing');
                    isrc.title = 'This track has no recorded ISRC, so it is matched on title and '
                        + 'artist rather than on recording identity.';
                }

                const toggle = document.createElement('button');
                toggle.type = 'button';
                toggle.className = 'btn btn-outline-secondary btn-sm playlist-sync-track__exclude';
                const isExcluded = excluded.has(key);
                toggle.setAttribute('aria-pressed', String(isExcluded));
                toggle.textContent = isExcluded ? 'Skipped' : 'Skip in sync';
                toggle.title = 'Do not copy this track to any destination. Not the watchlist blocklist.';
                toggle.addEventListener('click', () => {
                    if (excluded.has(key)) {
                        excluded.delete(key);
                    } else {
                        excluded.add(key);
                    }
                    const nowExcluded = excluded.has(key);
                    toggle.setAttribute('aria-pressed', String(nowExcluded));
                    toggle.textContent = nowExcluded ? 'Skipped' : 'Skip in sync';
                    row.classList.toggle('is-ignored', nowExcluded);
                });

                row.classList.toggle('is-ignored', isExcluded);
                row.append(art, text, isrc, toggle);
                tracklist.appendChild(row);
            });
        };

        trackSection.append(tracklist, trackHelp);
        panel.appendChild(trackSection);

        // The track list is loaded while the modal is on screen, so a slow library read does not
        // delay the dialog appearing.
        try {
            const detail = await fetchJson(
                `/api/autoplaylists/${encodeURIComponent(playlist.id)}?server=${encodeURIComponent(playlist.server)}`);
            const rows = detail?.playlist?.tracks ?? detail?.playlist?.items ?? detail?.tracks ?? detail?.items;
            tracks = Array.isArray(rows) ? rows : [];
        } catch {
            tracks = [];
        }
        renderTracks();

        const result = await globalThis.DeezSpoTag.ui.showModal({
            title: `Sync — ${playlist.name || 'Playlist'}`,
            message: '',
            allowHtml: false,
            dialogClass: 'is-resizable playlist-settings-modal',
            contentElement: panel,
            buttons: [
                { label: 'Save', value: 'save', primary: true },
                { label: 'Cancel', value: 'cancel' }
            ]
        });

        if (result?.value !== 'save') {
            return;
        }

        const selected = Array.from(panel.querySelectorAll('.library-playlist-sync-target:checked'),
            (input) => input.value);
        if (selected.length === 0) {
            showSyncMessage('Select at least one destination.', true);
            return;
        }

        const selectedMode = panel.querySelector('.library-playlist-sync-mode-option:checked')?.value || 'mirror';
        const chosenCadence = scheduleSelect.value || 'manual';

        // The settings are saved first, so a destination that is down for the immediate sync does
        // not also lose the schedule the user just chose.
        try {
            await fetchJson('/api/library/playlists/sync-schedule', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    source: playlist.server,
                    sourcePlaylistId: playlist.id,
                    cadence: chosenCadence,
                    targets: selected,
                    excludedTrackIds: Array.from(excluded),
                    artworkDataUrl,
                    description: description.value
                })
            });
        } catch (error) {
            showSyncMessage(error?.message || 'The sync settings could not be saved.', true);
            return;
        }

        showSyncMessage('Settings saved. Syncing now…');
        try {
            const response = await fetchJson(
                `/api/autoplaylists/${encodeURIComponent(playlist.id)}/sync?server=${encodeURIComponent(playlist.server)}`,
                {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        targets: selected,
                        syncMode: selectedMode,
                        artworkDataUrl,
                        description: description.value
                    })
                });
            const failed = (response?.targets || []).filter((t) => !t.success);
            if (failed.length === 0) {
                showSyncMessage(response.message || 'Playlist synced.');
            } else {
                const names = failed.map((t) => t.displayName || t.target).join(', ');
                showSyncMessage(`${response.message} Failed: ${names}.`, true);
            }
        } catch (error) {
            showSyncMessage(error?.message || 'Playlist sync failed.', true);
        }
    };

    /// <summary>
    /// The per-card action list. Deliberately small: it holds only the actions, and the settings
    /// themselves live in the modal the watchlist already uses. The classes are the app's own, so
    /// this reads as the same menu as the one on a watchlist playlist card.
    /// </summary>
    const renderSyncMenu = (playlist, libraryTargets, platformTargets) => {
        const wrapper = document.createElement('div');
        wrapper.className = 'watchlist-action-menu watchlist-action-menu--hover library-playlist-actions';

        const toggle = document.createElement('button');
        toggle.className = 'watchlist-kebab-btn';
        toggle.type = 'button';
        toggle.title = 'Playlist actions';
        toggle.setAttribute('aria-label', `Actions for ${playlist.name || 'playlist'}`);
        toggle.setAttribute('aria-expanded', 'false');
        toggle.innerHTML = '<i class="fa-solid fa-ellipsis-vertical"></i>';

        const dropdown = document.createElement('div');
        dropdown.className = 'watchlist-action-dropdown watchlist-action-dropdown--hover';
        dropdown.hidden = true;

        const action = (icon, label, handler) => {
            const button = document.createElement('button');
            button.className = 'dropdown-item';
            button.type = 'button';
            button.innerHTML = `<i class="fa-solid ${icon}"></i><span>${label}</span>`;
            button.addEventListener('click', (event) => {
                event.stopPropagation();
                handler();
            });
            return button;
        };

        const hasDestinations = libraryTargets.length + platformTargets.length > 0;

        dropdown.appendChild(action('fa-sliders', 'Settings', () => {
            setMenuOpen(wrapper, dropdown, toggle, false);
            openPlaylistSyncSettings(playlist, libraryTargets, platformTargets);
        }));

        if (hasDestinations) {
            dropdown.appendChild(action('fa-rotate', 'Sync now', () => {
                setMenuOpen(wrapper, dropdown, toggle, false);
                runPlaylistSyncNow(playlist);
            }));
        }

        toggle.addEventListener('click', (event) => {
            event.stopPropagation();
            setMenuOpen(wrapper, dropdown, toggle, dropdown.hidden);
        });
        document.addEventListener('click', (event) => {
            if (!wrapper.contains(event.target) && !dropdown.hidden) {
                setMenuOpen(wrapper, dropdown, toggle, false);
            }
        });

        wrapper.append(toggle, dropdown);
        return wrapper;
    };

    /// <summary>One immediate sync to every connected destination, using the saved mirror mode.</summary>
    const runPlaylistSyncNow = async (playlist) => {
        let targets = [];
        try {
            const list = await fetchJson('/api/library/playlists/sync-targets');
            targets = (Array.isArray(list) ? list : [])
                .map((t) => ({ value: String(t?.value || '').toLowerCase(), kind: String(t?.kind || '') }))
                .filter((t) => t.value && t.value !== String(playlist.server).toLowerCase());
        } catch {
            showSyncMessage('No connected destination could be reached.', true);
            return;
        }

        if (targets.length === 0) {
            showSyncMessage('There is no other destination to sync this playlist to.', true);
            return;
        }

        showSyncMessage('Syncing…');
        try {
            const response = await fetchJson(
                `/api/autoplaylists/${encodeURIComponent(playlist.id)}/sync?server=${encodeURIComponent(playlist.server)}`,
                {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ targets: targets.map((t) => t.value), syncMode: 'mirror' })
                });
            const failed = (response?.targets || []).filter((t) => !t.success);
            if (failed.length === 0) {
                showSyncMessage(response.message || 'Playlist synced.');
            } else {
                const names = failed.map((t) => t.displayName || t.target).join(', ');
                showSyncMessage(`${response.message} Failed: ${names}.`, true);
            }
        } catch (error) {
            showSyncMessage(error?.message || 'Playlist sync failed.', true);
        }
    };

    const renderLibraryCard = (playlist) => {
        const card = document.createElement("div");
        card.className = "library-playlist-card";

        const art = document.createElement("div");
        art.className = "library-playlist-art";

        const cover = document.createElement("div");
        cover.className = "library-playlist-cover";
        if (playlist.coverUrl) {
            const img = document.createElement("img");
            img.src = playlist.coverUrl;
            img.alt = "";
            img.loading = "lazy";
            img.addEventListener("error", () => {
                img.remove();
                if (!cover.querySelector(".watchlist-card-art-placeholder")) {
                    cover.appendChild(createCoverPlaceholder());
                }
            });
            cover.appendChild(img);
        } else {
            cover.appendChild(createCoverPlaceholder());
        }

        const allTargets = syncTargetsFor(playlist.server);
        // Split by kind so the panel can show Servers and Platforms as their own blocks rather than
        // one flat list. The split is driven by the endpoint's `kind`, which is the same thing the
        // menu is about: a self-hosted server and a streaming platform fail and reconnect
        // differently, and lumping them together hides that.
        const libraryTargets = allTargets.filter((target) => target.kind === "library");
        const platformTargets = allTargets.filter((target) => target.kind !== "library");
        art.appendChild(cover);

        const body = document.createElement("div");
        body.className = "library-playlist-body";

        const title = document.createElement("h3");
        title.className = "library-playlist-title";
        title.textContent = playlist.name || "Untitled playlist";

        const desc = document.createElement("p");
        desc.className = "library-playlist-desc";
        desc.textContent = playlist.description || `Playlist available in ${serverLabels[playlist.server] || "your library"}.`;

        const meta = document.createElement("div");
        meta.className = "library-playlist-meta";
        const trackCount = document.createElement("span");
        trackCount.textContent = playlist.trackCount == null
            ? "Playlist"
            : `${playlist.trackCount} tracks`;
        const duration = document.createElement("span");
        duration.textContent = playlist.duration || "—";
        meta.append(trackCount, duration);

        body.append(title, desc, meta);
        art.addEventListener("click", () => openTracklist(playlist.id, playlist.server, playlist.libraryId));
        card.append(art, body);

        // Sibling of the clickable art, exactly as the artist cards do it. Inside the art the
        // click bubbled up and navigated while the user was picking a target.
        if (allTargets.length > 0) {
            card.appendChild(renderSyncMenu(playlist, libraryTargets, platformTargets));
        }

        return card;
    };

    const renderLibrarySections = (sections) => {
        librarySections.innerHTML = "";
        const populated = Array.isArray(sections) ? sections : [];
        setSyncMessage("");
        let total = 0;

        populated.forEach((section) => {
            const playlists = Array.isArray(section?.playlists) ? section.playlists : [];
            if (playlists.length === 0) {
                return;
            }

            total += playlists.length;
            const displayName = section.displayName || serverLabels[section.server] || section.server || "Library";

            const wrapper = document.createElement("section");
            wrapper.className = "auto-section library-playlist-server-section";
            wrapper.dataset.server = section.server || "";

            const header = document.createElement("div");
            header.className = "auto-section-header library-playlist-server-header";

            const identity = document.createElement("div");
            identity.className = "library-playlist-server-identity";
            const icon = document.createElement("img");
            icon.className = "library-playlist-server-icon";
            icon.src = serverIconPaths[section.server] || "/images/icons/plex.png";
            icon.alt = "";
            const heading = document.createElement("h3");
            heading.className = "library-playlist-server-name";
            heading.textContent = displayName;
            identity.append(icon, heading);

            const count = document.createElement("span");
            count.className = "library-playlist-server-count";
            count.textContent = formatCount(playlists.length);

            header.append(identity, count);

            const grid = document.createElement("div");
            grid.className = "library-playlist-grid";
            playlists.forEach((playlist) => grid.appendChild(renderLibraryCard(playlist)));

            wrapper.append(header);
            if (section.warning) {
                const warning = document.createElement("p");
                warning.className = "library-playlist-server-warning";
                warning.textContent = section.warning;
                wrapper.appendChild(warning);
            }
            wrapper.appendChild(grid);
            librarySections.appendChild(wrapper);
        });

        countEl.textContent = formatCount(total);
        libraryEmpty.hidden = total > 0;
    };

    /// <summary>
    /// Per-playlist sync action for a Meloday mix. Same shape as the library playlist menu:
    /// the other connected servers as targets, an append/match choice, one submit. The mix is
    /// not regenerated - it syncs what is already stored.
    /// </summary>
    const renderAutoCard = (playlist) => {
        const card = document.createElement("div");
        card.className = "watchlist-playlist-card-v2 meloday-playlist-card";

        const artButton = document.createElement("button");
        artButton.className = "watchlist-card-art";
        artButton.type = "button";
        artButton.addEventListener("click", () => openTracklist(playlist.id, "mix", playlist.libraryId));

        const coverUrl = resolveMelodayCoverUrl(playlist);
        if (coverUrl) {
            const img = document.createElement("img");
            img.src = coverUrl;
            img.alt = playlist.name || "Meloday playlist";
            img.addEventListener("error", () => {
                img.remove();
                if (!artButton.querySelector(".watchlist-card-art-placeholder")) {
                    artButton.appendChild(createCoverPlaceholder());
                }
            });
            artButton.appendChild(img);
        } else {
            artButton.appendChild(createCoverPlaceholder());
        }

        const badge = document.createElement("span");
        badge.className = "playlist-watchlist-priority-badge meloday-playlist-badge";
        badge.textContent = "M";
        badge.title = "Meloday";
        artButton.appendChild(badge);

        const stats = document.createElement("div");
        stats.className = "watchlist-card-stats";
        const generated = document.createElement("div");
        generated.className = "watchlist-card-stat";
        generated.textContent = formatUpdated(playlist.updated);
        stats.appendChild(generated);
        artButton.appendChild(stats);

        const strip = document.createElement("div");
        strip.className = "watchlist-card-strip";

        const title = document.createElement("div");
        title.className = "watchlist-card-name";
        title.textContent = playlist.name || "Untitled Meloday playlist";

        const meta = document.createElement("div");
        meta.className = "watchlist-card-meta";
        meta.textContent = `${playlist.trackCount || 0} tracks`;

        const description = document.createElement("div");
        description.className = "watchlist-card-meta meloday-playlist-description";
        description.textContent = playlist.description || "Generated from listening history.";

        const actions = document.createElement("div");
        actions.className = "meloday-playlist-actions";
        const deleteButton = document.createElement("button");
        deleteButton.className = "meloday-playlist-delete";
        deleteButton.type = "button";
        deleteButton.textContent = "Delete";
        deleteButton.addEventListener("click", async (event) => {
            event.stopPropagation();
            deleteButton.disabled = true;
            try {
                const deleted = await deleteMix(playlist);
                if (!deleted) {
                    deleteButton.disabled = false;
                }
            } catch {
                deleteButton.disabled = false;
            }
        });
        actions.appendChild(deleteButton);

        strip.append(title, meta, description, actions);
        card.append(artButton, strip);


        return card;
    };

    const renderAutoPlaylistSections = (playlists) => {
        autoGrid.innerHTML = "";
        const playlistsByDay = new Map(melodayWeekdays.map((day) => [day.id, []]));

        playlists.forEach((playlist) => {
            const weekday = resolvePlaylistWeekday(playlist);
            if (weekday) {
                playlistsByDay.get(weekday).push(playlist);
            }
        });

        melodayWeekdays.forEach((day) => {
            const playlistsForDay = playlistsByDay.get(day.id);
            if (!playlistsForDay || playlistsForDay.length === 0) {
                return;
            }

            playlistsForDay.sort((left, right) => resolvePlaylistDaypartOrder(left) - resolvePlaylistDaypartOrder(right));

            const daySection = document.createElement("section");
            daySection.className = "auto-playlists-day-section";

            const heading = document.createElement("h3");
            heading.className = "auto-playlists-day-heading";
            heading.textContent = day.label;

            const dayGrid = document.createElement("div");
            dayGrid.className = "auto-tools-grid";
            playlistsForDay.forEach((playlist) => dayGrid.appendChild(renderAutoCard(playlist)));

            daySection.append(heading, dayGrid);
            autoGrid.appendChild(daySection);
        });
    };

    const renderRecommendationCard = (station, libraryId) => {
        const card = document.createElement("div");
        card.className = "auto-tool-card recommendation-tool-card";
        card.addEventListener("click", () => openRecommendationStation(station, libraryId));

        const cover = document.createElement("div");
        cover.className = "recommendation-tool-cover";
        if (station?.imageUrl) {
            const img = document.createElement("img");
            img.src = station.imageUrl;
            img.alt = "";
            cover.appendChild(img);
        } else {
            const placeholder = document.createElement("div");
            placeholder.className = "recommendation-tool-cover-placeholder";
            placeholder.textContent = "Recommendations";
            cover.appendChild(placeholder);
        }
        card.appendChild(cover);

        const header = document.createElement("div");
        header.className = "auto-tool-header";

        const title = document.createElement("h3");
        title.className = "auto-tool-title";
        title.textContent = normalizeRecommendationTitle(station);
        header.append(title);


        const desc = document.createElement("p");
        desc.className = "auto-tool-desc";
        desc.textContent = station.description || "Instant recommendations from your library.";

        const meta = document.createElement("div");
        meta.className = "auto-tool-meta";
        const trackCount = document.createElement("span");
        trackCount.textContent = station.cadence === "weekly"
            ? `${station.trackCount || 0} tracks · ${station.distinctArtistCount || 0} artists`
            : (station.trackCount ? `${station.trackCount} tracks` : "Daily mix");
        const mode = document.createElement("span");
        mode.textContent = station.cadence === "weekly"
            ? (station.generatedAtUtc ? `Weekly · ${new Date(station.generatedAtUtc).toLocaleDateString()}` : (station.message || station.status || "Generating"))
            : normalizeRecommendationMode(station);
        if (station.generatedAtUtc && station.cadence !== "weekly")
            mode.textContent += ` · ${new Date(station.generatedAtUtc).toLocaleDateString()}`;
        if (station.message) mode.textContent += ` · ${station.message}`;
        meta.append(trackCount, mode);

        const body = document.createElement("div");
        body.className = "recommendation-tool-body";
        body.append(header, desc, meta);

        card.append(body);
        return card;
    };

    const renderAutoPlaylistsEmptyIfNeeded = () => {
        if (!hasPlaylistSections) {
            return;
        }
        autoEmpty.hidden = autoGrid.children.length > 0;
    };

    if (hasPlaylistSections) {
        // The connected destinations are loaded BEFORE the playlist sections are rendered. A card
        // only gets its sync menu when there is at least one destination to offer, so rendering
        // first meant every library playlist was built with an empty target list and got no menu at
        // all - the kebab simply never appeared, with nothing in the console to explain it.
        // The target list and the playlist list come from different endpoints, so the playlist fetch
        // runs in parallel while this one is awaited.
        loadConnectedSyncTargets()
            .then(() => setAvailableSyncTargets(connectedSyncTargets))
            .then(() => fetch("/api/autoplaylists", { cache: "no-store" }))
            .then((response) => response.json())
            .then((data) => {
                setWarning(data?.warning || "");
                renderLibrarySections(data?.sections);
                loadMixes();
            })
            .catch(() => {
                setWarning("Failed to load playlists.");
                renderLibrarySections([]);
                loadMixes();
            });
    }

    function loadMixes() {
        if (!hasPlaylistSections) {
            return;
        }
        fetch("/api/mixes", { cache: "no-store" })
            .then((response) => response.ok ? response.json() : [])
            .then(async (mixes) => {
                const playlists = Array.isArray(mixes)
                    ? mixes.filter((mix) => mix?.id && mix?.libraryId).map((mix) => ({
                            id: mix.id,
                            name: mix.name,
                            description: mix.description,
                            trackCount: mix.trackCount,
                            updated: mix.generatedAtUtc,
                            source: "Auto",
                            coverUrls: mix.coverUrls,
                            libraryId: mix.libraryId
                        }))
                    : [];
                // The card menu offers every connected destination. The list was already loaded
                // before the library sections rendered, and it is the same list for both menus, so
                // it is reused here rather than fetched a second time. A fresh fetch would also
                // mean a slow second request delaying the Meloday cards for no new information.
                setAvailableSyncTargets(connectedSyncTargets);
                renderAutoPlaylistSections(playlists);
                renderAutoPlaylistsEmptyIfNeeded();
            })
            .catch(() => {
                autoGrid.innerHTML = "";
                renderAutoPlaylistsEmptyIfNeeded();
            });
    }

    var recommendationCardsRefreshTimer;
    let recommendationLoadGeneration = 0;
    let recommendationFolderScopes = null;
    function scheduleRecommendationCardsRefresh(delay = 15000) {
        clearTimeout(recommendationCardsRefreshTimer);
        recommendationCardsRefreshTimer = setTimeout(() => { void loadRecommendations(); }, delay);
    }

    const compareRecommendationCards = (a, b) => {
        const libraryDiff = Number(a?.dataset?.recommendationOrder) - Number(b?.dataset?.recommendationOrder);
        if (libraryDiff !== 0 && Number.isFinite(libraryDiff)) return libraryDiff;
        const indexDiff = Number(a?.dataset?.recommendationIndex) - Number(b?.dataset?.recommendationIndex);
        return Number.isFinite(indexDiff) ? indexDiff : 0;
    };

    function renderRecommendationLibrary(libraryId, stations, libraryOrder) {
        const missingGrid = document.getElementById("weeklyMissingRecommendationsGrid");
        const similarGrid = document.getElementById("weeklySimilarRecommendationsGrid");
        const grids = [recommendationsGrid, missingGrid, similarGrid];
        for (const grid of grids) {
            if (!grid) continue;
            for (const card of Array.from(grid.children)) {
                if (card.dataset.recommendationLibrary === String(libraryId)) card.remove();
            }
        }
        for (const [index, station] of (stations || []).entries()) {
            const grid = station.type === "weekly-missing-favourites" ? missingGrid
                : station.type === "weekly-similar-artists" ? similarGrid : recommendationsGrid;
            if (!grid) continue;
            const card = renderRecommendationCard(station, libraryId);
            card.dataset.recommendationLibrary = String(libraryId);
            const folderIdentity = String(station.id || "").match(/^(?:daily|weekly)-rotation:l\d+:f(\d+)(?::|$)/);
            if (folderIdentity) card.dataset.recommendationFolder = folderIdentity[1];
            card.dataset.recommendationOrder = String(libraryOrder);
            card.dataset.recommendationIndex = String(index);
            grid.appendChild(card);
        }
        for (const grid of grids) {
            if (!grid) continue;
            for (const card of Array.from(grid.children).sort(compareRecommendationCards)) {
                card.remove();
                grid.appendChild(card);
            }
        }
        recommendationsEmpty.hidden = recommendationsGrid.children.length > 0;
    }

    async function loadRecommendations() {
        const generation = ++recommendationLoadGeneration;
        clearTimeout(recommendationCardsRefreshTimer);
        const libraryIds = await resolveRecommendationLibraryIds();
        if (generation !== recommendationLoadGeneration) return;
        if (libraryIds === null) {
            scheduleRecommendationCardsRefresh();
            return;
        }
        const currentLibraries = new Set(libraryIds.map(String));
        for (const grid of [recommendationsGrid, document.getElementById("weeklyMissingRecommendationsGrid"), document.getElementById("weeklySimilarRecommendationsGrid")]) {
            if (!grid) continue;
            for (const card of Array.from(grid.children)) {
                const folderKey = `${card.dataset.recommendationLibrary}:${card.dataset.recommendationFolder}`;
                if (!currentLibraries.has(card.dataset.recommendationLibrary)
                    || (recommendationFolderScopes !== null && card.dataset.recommendationFolder
                        && !recommendationFolderScopes.has(folderKey))) card.remove();
            }
        }
        if (libraryIds.length === 0) {
            for (const grid of [recommendationsGrid, document.getElementById("weeklyMissingRecommendationsGrid"), document.getElementById("weeklySimilarRecommendationsGrid")]) grid?.replaceChildren();
            recommendationsEmpty.hidden = false;
            return;
        }
        let refreshNeeded = false;
        await Promise.all(libraryIds.map(async libraryId => {
            try {
                const response = await fetch(`/api/library/recommendations/stations?libraryId=${encodeURIComponent(libraryId)}`, { cache: "no-store" });
                if (!response.ok) throw new Error(`Recommendations HTTP ${response.status}`);
                const payload = await response.json();
                if (generation !== recommendationLoadGeneration) return;
                const stations = Array.isArray(payload) ? payload : [];
                renderRecommendationLibrary(libraryId, stations, libraryIds.indexOf(libraryId));
                refreshNeeded ||= stations.some(station => ["generating", "waiting", "refreshing", "refresh_failed", "failed"].includes(station.status));
            } catch {
                // Keep existing cards for this library while its request is retried.
                refreshNeeded = true;
            }
        }));
        if (generation !== recommendationLoadGeneration) return;
        recommendationsEmpty.hidden = recommendationsGrid.children.length > 0;
        if (refreshNeeded) scheduleRecommendationCardsRefresh();
    }

    async function resolveRecommendationLibraryIds() {
        const generation = recommendationLoadGeneration;
        try {
            const response = await fetch("/api/library/folders?includeDisabled=false&contentType=stereo", { cache: "no-store" });
            if (!response.ok) throw new Error(`Folder scope HTTP ${response.status}`);
            const folders = await response.json();
            if (!Array.isArray(folders)) throw new Error("Invalid configured folder response");
            if (generation === recommendationLoadGeneration)
                recommendationFolderScopes = new Set(folders.map(folder => `${folder.libraryId}:${folder.id}`));
            return uniquePositiveNumbers(folders.map(folder => folder.libraryId));
        } catch (error) {
            console.warn("Failed to load configured recommendation folders.", error);
            return null;
        }
    }

    // Bootstrapped LAST: every piece of state this call touches - notably the
    // `let recommendationLoadGeneration` declaration above - has to be initialized first.
    // Calling it earlier put the call inside that declaration's temporal dead zone, so the
    // async function rejected with a ReferenceError before it issued a single request and
    // the Recommendations tab stayed blank forever.
    if (hasRecommendationSection) {
        loadRecommendations();
    }
})();
