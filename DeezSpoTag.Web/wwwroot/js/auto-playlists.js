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

    const hasPlaylistSections = Boolean(libraryGrid && autoGrid && countEl && sourceEl && libraryEmpty && autoEmpty);
    const hasRecommendationSection = Boolean(recommendationsGrid && recommendationsEmpty);
    if (!hasPlaylistSections && !hasRecommendationSection) {
        return;
    }

    const formatCount = (count) => `${count} playlist${count === 1 ? "" : "s"}`;
    const MELODAY_COVER_COUNT = 18;

    const stableHash = (value) => {
        const text = String(value || "");
        let hash = 0;
        for (let index = 0; index < text.length; index += 1) {
            hash = ((hash << 5) - hash) + text.codePointAt(index);
            hash = Math.trunc(hash);
        }
        return Math.abs(hash);
    };

    const resolveMelodayCoverUrl = (playlist) => {
        const covers = Array.isArray(playlist?.coverUrls) ? playlist.coverUrls.filter(Boolean) : [];
        if (covers.length > 0) {
            return covers[0];
        }

        const coverIndex = (stableHash(`${playlist?.id || ""}|${playlist?.name || ""}`) % MELODAY_COVER_COUNT) + 1;
        return `/images/meloday/${coverIndex}.jpg`;
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
        card.className = "watchlist-playlist-card-v2 meloday-playlist-card";

        const artButton = document.createElement("button");
        artButton.className = "watchlist-card-art";
        artButton.type = "button";
        artButton.addEventListener("click", () => openTracklist(playlist.id, "mix", playlist.libraryId));

        const img = document.createElement("img");
        img.src = resolveMelodayCoverUrl(playlist);
        img.alt = playlist.name || "Meloday playlist";
        img.addEventListener("error", () => {
            img.remove();
            if (!artButton.querySelector(".watchlist-card-art-placeholder")) {
                const placeholder = document.createElement("div");
                placeholder.className = "watchlist-card-art-placeholder";
                const icon = document.createElement("i");
                icon.className = "fa-solid fa-music";
                placeholder.appendChild(icon);
                artButton.appendChild(placeholder);
            }
        });
        artButton.appendChild(img);

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

        strip.append(title, meta, description);
        card.append(artButton, strip);
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
        title.textContent = normalizeRecommendationTitle(station);
        header.append(title);

        const desc = document.createElement("p");
        desc.className = "auto-tool-desc";
        desc.textContent = station.description || "Instant recommendations from your library.";

        const meta = document.createElement("div");
        meta.className = "auto-tool-meta";
        const trackCount = document.createElement("span");
        trackCount.textContent = station.trackCount ? `${station.trackCount} tracks` : "Daily mix";
        const mode = document.createElement("span");
        mode.textContent = normalizeRecommendationMode(station);
        meta.append(trackCount, mode);

        const body = document.createElement("div");
        body.className = "recommendation-tool-body";
        body.append(header, desc, meta);

        card.append(body);
        return card;
    };

    const renderLists = (playlists) => {
        if (!hasPlaylistSections) {
            return;
        }
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

    if (hasRecommendationSection) {
        loadRecommendations();
    }

    if (hasPlaylistSections) {
        fetch("/api/autoplaylists", { cache: "no-store" })
            .then((response) => response.json())
            .then((data) => {
                const playlists = Array.isArray(data?.playlists) ? data.playlists : [];
                if (data?.warning) {
                    setWarning(data.warning);
                }
                sourceEl.textContent = playlists.length > 0 ? data.source || "Plex" : "";
                renderLists(playlists);
                loadMixes();
            })
            .catch(() => {
                setWarning("Failed to load playlists.");
                sourceEl.textContent = "";
                renderLists([]);
                loadMixes();
            });
    }

    function loadMixes() {
        if (!hasPlaylistSections) {
            return;
        }
        fetch("/api/mixes", { cache: "no-store" })
            .then((response) => response.ok ? response.json() : [])
            .then((mixes) => {
                autoGrid.innerHTML = "";
                if (Array.isArray(mixes)) {
                    mixes.forEach((mix) => {
                        if (!mix?.id || !mix?.libraryId) {
                            return;
                        }
                        autoGrid.appendChild(renderAutoCard({
                            id: mix.id,
                            name: mix.name,
                            description: mix.description,
                            trackCount: mix.trackCount,
                            updated: mix.generatedAtUtc,
                            source: "Auto",
                            coverUrls: mix.coverUrls,
                            libraryId: mix.libraryId
                        }));
                    });
                }
                autoEmpty.hidden = autoGrid.children.length > 0;
            })
            .catch(() => {
                autoGrid.innerHTML = "";
                autoEmpty.hidden = false;
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
                    .catch(() => ({ libraryId, stations: [] }))
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
