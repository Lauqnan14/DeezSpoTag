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
        if (station?.cadence === "weekly" && station?.libraryName) {
            return String(station.libraryName).trim();
        }
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
        mode.textContent = normalizeRecommendationMode(station);
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

    if (hasRecommendationSection) {
        loadRecommendations();
    }

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
                renderAutoPlaylistSections(playlists);
                renderAutoPlaylistsEmptyIfNeeded();
            })
            .catch(() => {
                autoGrid.innerHTML = "";
                renderAutoPlaylistsEmptyIfNeeded();
            });
    }

    async function loadRecommendations() {
        const libraryIds = await resolveRecommendationLibraryIds();
        if (libraryIds.length === 0) {
            recommendationsGrid.innerHTML = "";
            recommendationsEmpty.hidden = false;
            return;
        }

        const stationResponses = await Promise.all(
            libraryIds.map((libraryId) =>
                fetch(`/api/library/recommendations/stations?libraryId=${encodeURIComponent(libraryId)}`, { cache: "no-store" })
                    .then((response) => response.ok ? response.json() : [])
                    .then((stations) => ({ libraryId, stations: Array.isArray(stations) ? stations : [] }))
                    .catch(() => ({ libraryId, stations: [], failed: true }))
            )
        );

        recommendationsGrid.innerHTML = "";
        const fragment = document.createDocumentFragment();
        stationResponses.forEach((entry) => {
            entry.stations.forEach((station) => {
                fragment.appendChild(renderRecommendationCard(station, entry.libraryId));
            });
        });
        recommendationsGrid.appendChild(fragment);
        recommendationsEmpty.hidden = recommendationsGrid.children.length > 0;
    }

    async function resolveRecommendationLibraryIds() {
        try {
            const folderResponse = await fetch("/api/library/folders?includeDisabled=false&contentType=stereo", { cache: "no-store" });
            const folders = folderResponse.ok ? await folderResponse.json() : [];
            const folderLibraryIds = uniquePositiveNumbers((Array.isArray(folders) ? folders : []).map((item) => item?.libraryId));
            if (folderLibraryIds.length > 0) {
                return folderLibraryIds;
            }
        } catch (error) {
            console.warn("Failed to load recommendation folder scope.", error);
        }

        try {
            const libraryResponse = await fetch("/api/library/libraries", { cache: "no-store" });
            const libraries = libraryResponse.ok ? await libraryResponse.json() : [];
            return uniquePositiveNumbers((Array.isArray(libraries) ? libraries : []).map((item) => item?.id));
        } catch (error) {
            console.warn("Failed to load library scope.", error);
        }

        return [];
    }
})();
