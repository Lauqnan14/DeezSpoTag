(() => {
    const libraryGrid = document.getElementById("libraryPlaylistsGrid");
    const autoGrid = document.getElementById("autoToolsGrid");
    const recommendationsGrid = document.getElementById("recommendationsGrid");
    const countEl = document.getElementById("autoPlaylistsCount");
    const sourceEl = document.getElementById("autoPlaylistsSource");
    const libraryEmpty = document.getElementById("libraryPlaylistsEmpty");
    const autoEmpty = document.getElementById("autoPlaylistsEmpty");
    const recommendationsEmpty = document.getElementById("recommendationsEmpty");
    const warningEl = document.getElementById("autoPlaylistsWarning");

    if (!libraryGrid || !autoGrid || !recommendationsGrid || !countEl || !libraryEmpty || !autoEmpty || !recommendationsEmpty) {
        return;
    }

    const formatCount = (count) => `${count} playlist${count === 1 ? "" : "s"}`;

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

    const renderLibraryCard = (playlist) => {
        const card = document.createElement("div");
        card.className = "library-playlist-card";
        card.addEventListener("click", () => openTracklist(playlist.id, "plex", playlist.libraryId));

        const cover = document.createElement("div");
        cover.className = "library-playlist-cover";
        if (playlist.coverUrl) {
            const img = document.createElement("img");
            img.src = playlist.coverUrl;
            img.alt = "";
            cover.appendChild(img);
        }

        const body = document.createElement("div");
        body.className = "library-playlist-body";

        const title = document.createElement("h3");
        title.className = "library-playlist-title";
        title.textContent = playlist.name || "Untitled playlist";

        const desc = document.createElement("p");
        desc.className = "library-playlist-desc";
        desc.textContent = playlist.description || "Playlist available in Plex.";

        const meta = document.createElement("div");
        meta.className = "library-playlist-meta";
        const trackCount = document.createElement("span");
        trackCount.textContent = `${playlist.trackCount || 0} tracks`;
        const duration = document.createElement("span");
        duration.textContent = playlist.duration || "—";
        meta.append(trackCount, duration);

        body.append(title, desc, meta);
        card.append(cover, body);
        return card;
    };

    const renderAutoCard = (playlist) => {
        const card = document.createElement("div");
        card.className = "auto-tool-card";
        card.addEventListener("click", () => openTracklist(playlist.id, "mix", playlist.libraryId));

        const header = document.createElement("div");
        header.className = "auto-tool-header";

        const title = document.createElement("h3");
        title.className = "auto-tool-title";
        title.textContent = playlist.name || "Untitled auto playlist";

        const badge = document.createElement("span");
        badge.className = "auto-tool-badge";
        badge.textContent = playlist.source || "Auto";
        header.append(title, badge);

        const desc = document.createElement("p");
        desc.className = "auto-tool-desc";
        desc.textContent = playlist.description || "Generated from listening history.";

        const meta = document.createElement("div");
        meta.className = "auto-tool-meta";
        const trackCount = document.createElement("span");
        trackCount.textContent = `${playlist.trackCount || 0} tracks`;
        const updated = document.createElement("span");
        updated.textContent = playlist.updated || "Recently updated";
        meta.append(trackCount, updated);

        card.append(header, desc, meta);
        return card;
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
        const normalizedName = (station?.name || "")
            .replace(/^recommendations\s*-\s*/i, "")
            .trim();
        title.textContent = normalizedName || station.name || "Recommendation";
        header.append(title);

        const desc = document.createElement("p");
        desc.className = "auto-tool-desc";
        desc.textContent = station.description || "Instant recommendations from your library.";

        const meta = document.createElement("div");
        meta.className = "auto-tool-meta";
        const trackCount = document.createElement("span");
        trackCount.textContent = station.trackCount ? `${station.trackCount} tracks` : "Daily mix";
        const mode = document.createElement("span");
        mode.textContent = station.value ? station.value.replaceAll("-", " ") : station.type;
        meta.append(trackCount, mode);

        const body = document.createElement("div");
        body.className = "recommendation-tool-body";
        body.append(header, desc, meta);

        card.append(body);
        return card;
    };

    const renderLists = (playlists) => {
        libraryGrid.innerHTML = "";
        autoGrid.innerHTML = "";

        if (!Array.isArray(playlists) || playlists.length === 0) {
            libraryEmpty.hidden = false;
            autoEmpty.hidden = false;
            countEl.textContent = formatCount(0);
            return;
        }

        playlists.forEach((playlist) => {
            libraryGrid.appendChild(renderLibraryCard(playlist));
        });

        libraryEmpty.hidden = playlists.length > 0;
        autoEmpty.hidden = autoGrid.children.length > 0;
        countEl.textContent = formatCount(playlists.length);
    };

    fetch("/api/autoplaylists", { cache: "no-store" })
        .then((response) => response.json())
        .then((data) => {
            const playlists = Array.isArray(data?.playlists) ? data.playlists : [];
            if (data?.warning) {
                setWarning(data.warning);
            }
            if (playlists.length > 0) {
                sourceEl.textContent = data.source || "Plex";
                renderLists(playlists);
                loadMixes(playlists);
            } else {
                sourceEl.textContent = "";
                renderLists([]);
            }
            loadRecommendations(playlists);
        })
        .catch(() => {
            setWarning("Failed to load playlists.");
            sourceEl.textContent = "";
            renderLists([]);
            loadRecommendations([]);
        });

    function loadMixes(playlists) {
        const libraryId = resolveLibraryId(playlists);
        if (!libraryId) {
            return;
        }
        fetch(`/api/mixes?libraryId=${encodeURIComponent(libraryId)}`, { cache: "no-store" })
            .then((response) => response.json())
            .then((mixes) => {
                if (!Array.isArray(mixes)) {
                    return;
                }
                mixes.forEach((mix) => {
                    autoGrid.appendChild(renderAutoCard({
                        id: mix.id,
                        name: mix.name,
                        description: mix.description,
                        trackCount: mix.trackCount,
                        updated: mix.generatedAtUtc,
                        source: "Auto",
                        libraryId: mix.libraryId
                    }));
                });
                autoEmpty.hidden = autoGrid.children.length > 0;
            })
            .catch(() => {});
    }

    function resolveLibraryId(playlists) {
        const withLibrary = (playlists || []).find((item) => item.libraryId);
        return withLibrary ? withLibrary.libraryId : null;
    }

    async function loadRecommendations(playlists) {
        const libraryIds = await resolveRecommendationLibraryIds(playlists);
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
                    .catch(() => ({ libraryId, stations: [] }))
            )
        );

        recommendationsGrid.innerHTML = "";
        stationResponses.forEach((entry) => {
            entry.stations.forEach((station) => {
                recommendationsGrid.appendChild(renderRecommendationCard(station, entry.libraryId));
            });
        });
        recommendationsEmpty.hidden = recommendationsGrid.children.length > 0;
    }

    async function resolveRecommendationLibraryIds(playlists) {
        const fromPlaylists = (playlists || [])
            .map((item) => Number(item?.libraryId))
            .filter((value) => Number.isFinite(value) && value > 0);
        if (fromPlaylists.length > 0) {
            return [...new Set(fromPlaylists)];
        }

        try {
            const response = await fetch("/api/library/libraries", { cache: "no-store" });
            const libraries = response.ok ? await response.json() : [];
            return (Array.isArray(libraries) ? libraries : [])
                .map((item) => Number(item?.id))
                .filter((value) => Number.isFinite(value) && value > 0)
                .filter((value, index, array) => array.indexOf(value) === index);
        } catch (error) {
            console.warn("Failed to load recommendation library ids.", error);
        }
        return [];
    }
})();
