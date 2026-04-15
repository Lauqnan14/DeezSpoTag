(() => {
    const DEFAULT_CONFIG = {
        platforms: ["deezer", "itunes"],
        path: null,
        customPath: null,
        playlistPath: null,
        tags: ["genre", "style", "bpm", "releaseDate", "label"],
        downloadTags: ["genre", "bpm", "releaseDate", "label"],
        gapFillTags: ["genre", "style", "bpm", "key", "releaseDate", "label", "syncedLyrics", "unsyncedLyrics"],
        overwriteTags: [],
        separators: {
            id3: ", ",
            vorbis: null,
            mp4: ", "
        },
        id3v24: true,
        overwrite: false,
        threads: 16,
        strictness: 0.7,
        mergeGenres: true,
        albumArtFile: false,
        camelot: false,
        parseFilename: false,
        filenameTemplate: "%artists% - %title%",
        shortTitle: false,
        downloadTagSource: "engine",
        matchDuration: false,
        maxDurationDifference: 30,
        matchById: false,
        multipleMatches: "Default",
        stylesOptions: "default",
        stylesCustomTag: { id3: "STYLE", vorbis: "STYLE", mp4: "STYLE" },
        trackNumberLeadingZeroes: 0,
        enableShazam: true,
        forceShazam: false,
        conflictResolution: null,
        skipTagged: false,
        onlyYear: false,
        includeSubfolders: true,
        multiplatform: false,
        titleRegex: null,
        moveSuccess: false,
        moveSuccessLibraryFolderId: null,
        moveSuccessPath: null,
        moveFailed: false,
        moveFailedPath: null,
        writeLrc: false,
        enhancedLrc: false,
        capitalizeGenres: false,
        id3CommLang: null,
        spotify: {
            clientId: null,
            clientSecret: null
        },
        custom: {},
        organizer: {
            onlyMoveWhenTagged: false,
            moveUntaggedPath: null,
            dryRun: false
        },
        enhancement: {
            folderUniformity: {
                folderIds: [],
                enforceFolderStructure: true,
                moveMisplacedFiles: true,
                mergeIntoExistingDestinationFolders: true,
                renameFilesToTemplate: true,
                removeEmptyFolders: true,
                resolveSameTrackQualityConflicts: true,
                keepBothOnUnresolvedConflicts: true,
                onlyMoveWhenTagged: false,
                onlyReorganizeAlbumsWithFullTrackSets: false,
                skipCompilationFolders: false,
                skipVariousArtistsFolders: false,
                generateReconciliationReport: false,
                useShazamForUntaggedFiles: false,
                duplicateConflictPolicy: "keep_best",
                artworkPolicy: "preserve_existing",
                lyricsPolicy: "merge",
                runDedupe: true,
                useShazamForDedupe: false,
                duplicatesFolderName: "%duplicates%",
                renameSpotifyArtistFolders: DEFAULT_RENAME_SPOTIFY_ARTIST_FOLDERS,
                includeSubfolders: true
            },
            coverMaintenance: {
                folderIds: [],
                minResolution: 500,
                replaceMissingEmbeddedCovers: false,
                syncExternalCovers: false,
                queueAnimatedArtwork: false,
                workerCount: 8
            },
            qualityChecks: {
                folderIds: [],
                scope: "all",
                technicalProfiles: [],
                queueAtmosAlternatives: false,
                queueLyricsRefresh: false,
                flagDuplicates: false,
                flagMissingTags: false,
                flagMismatchedMetadata: false,
                useDuplicatesFolder: true,
                useShazamForDedupe: false,
                duplicatesFolderName: "%duplicates%",
                cooldownMinutes: null
            }
        }
    };

    const TAGS = [
        { tag: "albumArt", label: "Album Art", tooltip: "Resolution is platform dependent" },
        { tag: "album", label: "Album" },
        { tag: "albumArtist", label: "Album Artist" },
        { tag: "artist", label: "Artist" },
        { tag: "title", label: "Title" },
        { tag: "version", label: "Version" },
        { tag: "remixer", label: "Remixers", tooltip: "Available from Beatport & Beatsource" },
        { tag: "genre", label: "Genre", tooltip: "Spotify will populate multiple genres based on artist" },
        { tag: "style", label: "Style / Subgenre", tooltip: "Style is available from Discogs & Bandcamp, Subgenre from Beatport only" },
        { tag: "label", label: "Label" },
        { tag: "releaseId", label: "Release ID" },
        { tag: "trackId", label: "Track ID" },
        { tag: "bpm", label: "BPM" },
        { tag: "key", label: "Key" },
        { tag: "mood", label: "Mood" },
        { tag: "catalogNumber", label: "Catalog Number" },
        { tag: "trackNumber", label: "Track Number" },
        { tag: "discNumber", label: "Disc Number" },
        { tag: "duration", label: "Duration" },
        { tag: "trackTotal", label: "Track Total" },
        { tag: "isrc", label: "ISRC" },
        { tag: "publishDate", label: "Publish Date", tooltip: "Available from Beatport only" },
        { tag: "releaseDate", label: "Release Date" },
        { tag: "url", label: "URL" },
        { tag: "otherTags", label: "Other Tags", tooltip: "Specific tags only for some platforms (Beatport, Discogs)" },
        { tag: "metaTags", label: "OneTagger Tags", tooltip: "Adds 1T_TAGGEDDATE tag with timestamp" },
        { tag: "unsyncedLyrics", label: "Unsynced Lyrics" },
        { tag: "syncedLyrics", label: "Synced Lyrics" },
        { tag: "ttmlLyrics", label: "TTML Lyrics" }
    ];

    const DOWNLOAD_TAG_LABELS = {
        title: "Title",
        artist: "Artist",
        artists: "Artists (multi)",
        album: "Album",
        albumArtist: "Album Artist",
        trackNumber: "Track Number",
        trackTotal: "Track Total",
        discNumber: "Disc Number",
        discTotal: "Disc Total",
        genre: "Genre",
        year: "Year",
        date: "Date (full)",
        explicit: "Explicit",
        isrc: "ISRC",
        length: "Length",
        barcode: "Barcode/UPC",
        bpm: "BPM",
        key: "Key",
        danceability: "Danceability",
        energy: "Energy",
        valence: "Valence",
        acousticness: "Acousticness",
        instrumentalness: "Instrumentalness",
        speechiness: "Speechiness",
        loudness: "Loudness",
        liveness: "Liveness",
        tempo: "Tempo (BPM)",
        timeSignature: "Time Signature",
        replayGain: "ReplayGain",
        label: "Label",
        lyrics: "Lyrics (unsynced)",
        syncedLyrics: "Synced Lyrics",
        ttmlLyrics: "TTML Lyrics",
        copyright: "Copyright",
        composer: "Composer",
        involvedPeople: "Credits (producer, mixer, etc.)",
        cover: "Cover Art",
        source: "Source ID",
        url: "Source URL",
        trackId: "Track ID (source)",
        releaseId: "Release ID (source)",
        rating: "Rating"
    };
    const HIDDEN_SPOTIFY_AUDIO_FEATURE_TAGS = [
        "danceability",
        "energy",
        "valence",
        "acousticness",
        "instrumentalness",
        "speechiness",
        "loudness",
        "liveness",
        "tempo",
        "timeSignature"
    ];
    const HIDDEN_SPOTIFY_AUDIO_FEATURE_TAG_SET = new Set(
        HIDDEN_SPOTIFY_AUDIO_FEATURE_TAGS.map((tag) => String(tag).toLowerCase())
    );
    const DEFAULT_LYRICS_TYPE_SELECTION = "lyrics,syllable-lyrics,unsynced-lyrics";
    const DEFAULT_ARTWORK_SOURCE_ORDER = Object.freeze(["apple", "deezer", "spotify"]);
    const DEFAULT_LYRICS_SOURCE_ORDER = Object.freeze(["apple", "deezer", "spotify", "lrclib"]);
    const ARTWORK_SOURCE_ORDER = [...DEFAULT_ARTWORK_SOURCE_ORDER];
    const LYRICS_SOURCE_ORDER = [...DEFAULT_LYRICS_SOURCE_ORDER];
    const SOURCE_LABELS = new Map([
        ["apple", "Apple Music"],
        ["deezer", "Deezer"],
        ["spotify", "Spotify"],
        ["lrclib", "LRCLIB"],
        ["musixmatch", "Musixmatch"],
        ["shazam", "Shazam"],
        ["boomplay", "Boomplay"]
    ]);
    const PROVIDER_PLATFORM_IDS = Object.freeze({
        apple: Object.freeze(["itunes", "applemusic", "apple"]),
        deezer: Object.freeze(["deezer"]),
        spotify: Object.freeze(["spotify"]),
        lrclib: Object.freeze(["lrclib"]),
        musixmatch: Object.freeze(["musixmatch"]),
        shazam: Object.freeze(["shazam"]),
        boomplay: Object.freeze(["boomplay"])
    });
    const ARTWORK_CAPABILITY_KEYS = Object.freeze(["cover", "albumart", "artwork"]);
    const LYRICS_CAPABILITY_KEYS = Object.freeze([
        "lyrics",
        "syncedlyrics",
        "unsyncedlyrics",
        "ttmllyrics",
        "syllablelyrics",
        "timesyncedlyrics"
    ]);
    const ALWAYS_AVAILABLE_LYRICS_PROVIDERS = Object.freeze([]);

    const LYRICS_TYPE_OPTIONS = [
        { value: "lyrics", label: "Synced Lyrics" },
        { value: "syllable-lyrics", label: "Time Synced Lyrics" },
        { value: "unsynced-lyrics", label: "Unsynced Lyrics" }
    ];

    const LYRICS_TYPE_ALIASES = {
        "synced-lyrics": "lyrics",
        "time-synced-lyrics": "syllable-lyrics",
        "timesynced-lyrics": "syllable-lyrics",
        "time_synced_lyrics": "syllable-lyrics",
        "syllablelyrics": "syllable-lyrics",
        "unsunsynced-lyrics": "unsynced-lyrics",
        "unsyncedlyrics": "unsynced-lyrics",
        "unsynced": "unsynced-lyrics",
        "unsynchronized-lyrics": "unsynced-lyrics",
        "unsynchronised-lyrics": "unsynced-lyrics"
    };


    const AUTOTAG_SELECTED_PLATFORMS_KEY = "autotag-selected-platforms";
    const AUTOTAG_PREFERENCES_KEY = "autotag-preferences";
    const AUTOTAG_ACTIVE_PROFILE_KEY = "autotag-active-profile-id";
    const AUTOTAG_FOLDER_UNIFORMITY_JOB_KEY = "autotag-folder-uniformity-job-id";
    const AUTOTAG_FOLDER_UNIFORMITY_STATUS_SNAPSHOT_KEY = "autotag-folder-uniformity-status-snapshot";
    const AUTOTAG_FOLDER_UNIFORMITY_LAST_SCAN_KEY = "autotag-folder-uniformity-last-scan";
    const AUTOTAG_LIBRARY_FOLDERS_API = "/api/library/folders";
    const PROFILE_AUTOSAVE_DEBOUNCE_MS = 900;
    const ENHANCEMENT_LAST_SCAN_BANNER_MS = 15000;
    const DEFAULT_RECENT_DOWNLOAD_WINDOW_HOURS = 24;
    const DEFAULT_RECENT_DOWNLOAD_WINDOW_DAYS = Math.max(0, Math.round(DEFAULT_RECENT_DOWNLOAD_WINDOW_HOURS / 24));
    const DEFAULT_RENAME_SPOTIFY_ARTIST_FOLDERS = true;

    const state = {
        config: structuredClone(DEFAULT_CONFIG),
        platforms: [],
        libraryFolders: [],
        profiles: [],
        profilesLoaded: false,
        activeProfileId: null,
        authReady: false,
        jobId: null,
        pollTimer: null,
        profileSaveTimer: null,
        profileSaveInFlight: false,
        profileSaveQueued: false,
        profileSaveDirty: false,
        autoTagDefaults: null,
        autoTagDefaultsLoaded: false,
        autoTagDefaultsDirty: false,
        spotifyStatus: { connected: false, expiresAt: null, redirectUri: null },
        platformAuth: {},
        lockedTabTarget: null,
        settingsCache: null,
        folderUniformityJobId: null,
        folderUniformityResumeInFlight: false,
        folderUniformityLastScanTimer: null,
        syncLyricsFallbackOrder: null,
        syncArtworkFallbackOrder: null,
        syncArtistArtworkFallbackOrder: null,
        technicalProfilesCatalog: [],
        technicalProfilesLoading: false
    };
    let draggedPlatformId = null;

    const el = (id) => document.getElementById(id);
    const FOLDER_PREVIEW_SAMPLES = Object.freeze({
        artist: "Artist Name",
        artists: "Artist Name",
        album: "Album Name",
        playlist: "Playlist Name",
        title: "Track Name",
        track: "Track Name",
        tracknumber: "01",
        discnumber: "1",
        year: "2026",
        date: "2026-02-08"
    });
    const platformIconPreloadState = new Map();

    function canonicalizeProviderKey(value) {
        const normalized = normalizePlatformId(value);
        if (!normalized) {
            return "";
        }

        for (const [provider, mappedIds] of Object.entries(PROVIDER_PLATFORM_IDS)) {
            if ((mappedIds || []).some((id) => normalizePlatformId(id) === normalized)) {
                return provider;
            }
        }

        return normalized;
    }

    function getSourceLabel(value) {
        const key = canonicalizeProviderKey(value);
        return SOURCE_LABELS.get(key) || value;
    }

    function getLyricsSourceLabel(value) {
        const key = canonicalizeProviderKey(value);
        if (key === "apple") {
            return "Apple";
        }
        return getSourceLabel(value);
    }

    function normalizeProviderOrder(value, allowedOrder) {
        const allowed = new Set((allowedOrder || []).map((item) => String(item).toLowerCase()));
        const tokens = Array.isArray(value)
            ? value
            : String(value || "").split(",");
        const normalized = [];
        const seen = new Set();

        tokens.forEach((token) => {
            const provider = canonicalizeProviderKey(token);
            if (!provider || !allowed.has(provider) || seen.has(provider)) {
                return;
            }
            seen.add(provider);
            normalized.push(provider);
        });

        if (normalized.length === 0) {
            return [...allowedOrder];
        }

        return normalized;
    }

    function normalizeCapabilityToken(value) {
        return String(value || "")
            .trim()
            .toLowerCase()
            .replaceAll(/[^a-z0-9]/g, "");
    }

    function getPlatformCapabilityTokenSet(platform) {
        const tokens = new Set();
        const supported = Array.isArray(platform?.supportedTags) ? platform.supportedTags : [];
        const download = Array.isArray(platform?.downloadTags) ? platform.downloadTags : [];

        [...supported, ...download].forEach((tag) => {
            const normalized = normalizeCapabilityToken(tag);
            if (normalized) {
                tokens.add(normalized);
            }
        });

        if (platform?.supportsLyrics === true) {
            tokens.add("lyrics");
        }

        return tokens;
    }

    function platformHasCapability(platform, capabilityType) {
        if (!platform) {
            return false;
        }

        const tokens = getPlatformCapabilityTokenSet(platform);
        const requiredKeys = capabilityType === "artwork" ? ARTWORK_CAPABILITY_KEYS : LYRICS_CAPABILITY_KEYS;
        return requiredKeys.some((key) => tokens.has(key));
    }

    function getAvailableFallbackProviders(capabilityType, allowedOrder) {
        if (!Array.isArray(allowedOrder) || allowedOrder.length === 0) {
            return [];
        }

        if (!Array.isArray(state.platforms) || state.platforms.length === 0) {
            return [...allowedOrder];
        }

        const enabledPlatforms = new Set((state.config.platforms || []).map((id) => normalizePlatformId(id)));
        const platformById = new Map(
            state.platforms.map((platform) => [normalizePlatformId(platform?.id), platform])
        );

        const available = [];
        allowedOrder.forEach((provider) => {
            const providerKey = String(provider || "").toLowerCase();
            const mappedIds = PROVIDER_PLATFORM_IDS[providerKey] || [providerKey];
            const knownMappedIds = mappedIds.filter((id) => platformById.has(normalizePlatformId(id)));

            if (
                capabilityType === "lyrics"
                && ALWAYS_AVAILABLE_LYRICS_PROVIDERS.includes(providerKey)
                && knownMappedIds.length === 0
            ) {
                available.push(providerKey);
                return;
            }

            const enabledMappedIds = mappedIds.filter((id) => enabledPlatforms.has(normalizePlatformId(id)));
            if (enabledMappedIds.length === 0) {
                return;
            }

            const hasCapability = enabledMappedIds.some((id) => {
                const platform = platformById.get(normalizePlatformId(id));
                // If an enabled mapped platform id is unknown to metadata, keep provider visible.
                if (!platform) {
                    return true;
                }
                return platformHasCapability(platform, capabilityType);
            });

            if (hasCapability) {
                available.push(providerKey);
            }
        });

        return available;
    }

    function applyDefaultSourceAvailability(selectId, allowedOrder, availableOrder) {
        ensureDefaultSourceOptions(selectId, allowedOrder);
        const select = el(selectId);
        if (!(select instanceof HTMLSelectElement)) {
            return;
        }

        const availableSet = new Set((availableOrder || []).map((item) => String(item).toLowerCase()));
        const hasAvailabilityFilter = Array.isArray(state.platforms) && state.platforms.length > 0;
        Array.from(select.options).forEach((option) => {
            const optionValue = String(option.value || "").toLowerCase();
            const isAvailable = !hasAvailabilityFilter || availableSet.has(optionValue);
            option.disabled = !isAvailable;
            option.hidden = !isAvailable;
        });

        select.dataset.hasAvailableSources = (!hasAvailabilityFilter || availableSet.size > 0) ? "1" : "0";
        if (hasAvailabilityFilter && !availableSet.has(String(select.value || "").toLowerCase())) {
            const preferred = allowedOrder.find((provider) => availableSet.has(provider));
            if (preferred) {
                select.value = preferred;
            }
        }
    }

    function getPrimaryProvider(value, allowedOrder) {
        const normalized = normalizeProviderOrder(value, allowedOrder);
        if (normalized.length > 0) {
            return normalized[0];
        }
        return allowedOrder[0] || "";
    }

    function buildPreferredProviderOrder(primary, currentOrder, allowedOrder) {
        const normalizedCurrent = normalizeProviderOrder(currentOrder, allowedOrder);
        const normalizedPrimary = String(primary || "").trim().toLowerCase();
        const effectivePrimary = allowedOrder.includes(normalizedPrimary)
            ? normalizedPrimary
            : getPrimaryProvider(normalizedCurrent, allowedOrder);

        const merged = [effectivePrimary];
        normalizedCurrent.forEach((provider) => {
            if (!merged.includes(provider)) {
                merged.push(provider);
            }
        });
        allowedOrder.forEach((provider) => {
            if (!merged.includes(provider)) {
                merged.push(provider);
            }
        });

        return merged;
    }

    function resolveProviderForPlatform(platformId) {
        const normalizedId = normalizePlatformId(platformId);
        if (!normalizedId) {
            return null;
        }
        return canonicalizeProviderKey(normalizedId);
    }

    function ensureDefaultSourceOptions(selectId, allowedOrder) {
        const select = el(selectId);
        if (!(select instanceof HTMLSelectElement)) {
            return;
        }

        const current = canonicalizeProviderKey(select.value);
        select.innerHTML = "";
        (allowedOrder || []).forEach((provider) => {
            const option = document.createElement("option");
            option.value = provider;
            option.textContent = selectId === "lyricsDefaultSource"
                ? getLyricsSourceLabel(provider)
                : getSourceLabel(provider);
            select.appendChild(option);
        });

        if (current && (allowedOrder || []).includes(current)) {
            select.value = current;
        }
    }

    function rebuildSourceOrdersFromPlatforms() {
        const updateOrder = (target, next) => {
            target.splice(0, target.length, ...(Array.isArray(next) ? next : []));
        };

        const hasPlatformMetadata = Array.isArray(state.platforms) && state.platforms.length > 0;
        if (!hasPlatformMetadata) {
            updateOrder(ARTWORK_SOURCE_ORDER, DEFAULT_ARTWORK_SOURCE_ORDER);
            updateOrder(LYRICS_SOURCE_ORDER, DEFAULT_LYRICS_SOURCE_ORDER);
            return;
        }

        const enabledPlatforms = new Set((state.config.platforms || []).map((id) => normalizePlatformId(id)));
        const artworkOrder = [];
        const lyricsOrder = [];

        state.platforms.forEach((platform) => {
            const platformId = normalizePlatformId(platform?.id);
            if (!platformId || !enabledPlatforms.has(platformId)) {
                return;
            }

            const provider = resolveProviderForPlatform(platformId);
            if (!provider) {
                return;
            }

            if (platform?.name) {
                SOURCE_LABELS.set(provider, platform.name);
            }

            if (platformHasCapability(platform, "artwork") && !artworkOrder.includes(provider)) {
                artworkOrder.push(provider);
            }

            if (platformHasCapability(platform, "lyrics") && !lyricsOrder.includes(provider)) {
                lyricsOrder.push(provider);
            }
        });

        updateOrder(
            ARTWORK_SOURCE_ORDER,
            artworkOrder.length > 0 ? artworkOrder : DEFAULT_ARTWORK_SOURCE_ORDER
        );
        updateOrder(
            LYRICS_SOURCE_ORDER,
            lyricsOrder.length > 0 ? lyricsOrder : DEFAULT_LYRICS_SOURCE_ORDER
        );
    }

    function syncDefaultSourceSelectFromOrder(selectId, orderValue, allowedOrder) {
        const select = el(selectId);
        if (!(select instanceof HTMLSelectElement)) {
            return;
        }

        const primary = getPrimaryProvider(orderValue, allowedOrder);
        if (primary && allowedOrder.includes(primary)) {
            select.value = primary;
        }
    }

    function resolveSavedFallbackOrder({ fallbackEnabled, defaultSourceId, fallbackOrderValue, allowedOrder }) {
        if (fallbackEnabled) {
            return normalizeProviderOrder(fallbackOrderValue, allowedOrder).join(",");
        }

        const defaultSource = defaultSourceId ? el(defaultSourceId)?.value : null;
        return buildPreferredProviderOrder(defaultSource, fallbackOrderValue, allowedOrder).join(",");
    }

    function preloadPlatformIcon(url) {
        if (!url || platformIconPreloadState.has(url)) {
            return;
        }

        platformIconPreloadState.set(url, "loading");
        const image = new Image();
        image.addEventListener("load", () => {
            platformIconPreloadState.set(url, "ready");
        }, { once: true });
        image.addEventListener("error", () => {
            platformIconPreloadState.set(url, "error");
        }, { once: true });
        image.src = url;
    }

    function preloadPlatformIcons(platforms) {
        if (!Array.isArray(platforms) || platforms.length === 0) {
            return;
        }

        platforms.forEach((platform) => {
            const primary = platform?.icon || "";
            const fallback = platform?.fallbackIcon || "";
            preloadPlatformIcon(primary);
            if (fallback && fallback !== primary) {
                preloadPlatformIcon(fallback);
            }
        });
    }

    function renderTags(containerId, selected, name) {
        const container = el(containerId);
        container.innerHTML = "";
        const list = name === "downloadTags" ? getDownloadTagsList() : TAGS;
        const enhancementMirrorOnly = name === "gapFillTags";
        list.forEach((tag) => {
            const label = document.createElement("label");
            const input = document.createElement("input");
            input.type = "checkbox";
            input.dataset.tag = tag.tag;
            input.name = name;
            input.checked = selected.includes(tag.tag);
            const supported = name === "downloadTags"
                ? isDownloadTagSupported(tag.tag)
                : isTagSupported(tag.tag);
            if (!supported) {
                input.disabled = true;
                label.classList.add("tag-disabled");
            }
            if (enhancementMirrorOnly) {
                input.disabled = true;
                label.classList.add("tag-disabled");
                label.title = "Enhancement tags mirror Download and Enrichment tag selections.";
            }
            label.appendChild(input);
            label.appendChild(document.createTextNode(tag.label));
            if (tag.tooltip) {
                const tooltip = document.createElement("span");
                tooltip.className = "autotag-tooltip-icon";
                tooltip.title = tag.tooltip;
                tooltip.innerHTML = '<i class="fas fa-question-circle"></i>';
                label.appendChild(tooltip);
            }
            container.appendChild(label);
        });
    }

    function getDownloadTagSource() {
        const engineToggle = el("metadataSourceEngineEnabled");
        const deezerToggle = el("metadataSourceDeezerEnabled");
        const spotifyToggle = el("metadataSourceSpotifyEnabled");
        const configuredSource = normalizeDownloadTagSource(state.config.downloadTagSource);

        if (engineToggle?.checked === true && deezerToggle?.checked !== true && spotifyToggle?.checked !== true) {
            return "engine";
        }
        if (deezerToggle?.checked === true && spotifyToggle?.checked !== true) {
            return "deezer";
        }
        if (spotifyToggle?.checked === true && deezerToggle?.checked !== true) {
            return "spotify";
        }

        return configuredSource;
    }

    function setDownloadTagSource(source, options = {}) {
        const { syncUi = true } = options;
        const normalized = normalizeDownloadTagSource(source);
        state.config.downloadTagSource = normalized;

        if (!syncUi) {
            return;
        }

        const engineToggle = el("metadataSourceEngineEnabled");
        const deezerToggle = el("metadataSourceDeezerEnabled");
        const spotifyToggle = el("metadataSourceSpotifyEnabled");
        if (engineToggle) {
            engineToggle.checked = normalized === "engine";
        }
        if (deezerToggle) {
            deezerToggle.checked = normalized === "deezer";
        }
        if (spotifyToggle) {
            spotifyToggle.checked = normalized === "spotify";
        }

    }

    function enforceSingleDownloadSource(changedId) {
        const engineToggle = el("metadataSourceEngineEnabled");
        const deezerToggle = el("metadataSourceDeezerEnabled");
        const spotifyToggle = el("metadataSourceSpotifyEnabled");
        if (!engineToggle || !deezerToggle || !spotifyToggle) {
            setDownloadTagSource(state.config.downloadTagSource || "engine");
            return;
        }

        let nextSource = null;
        if (engineToggle.checked && !deezerToggle.checked && !spotifyToggle.checked) {
            nextSource = "engine";
        } else if (deezerToggle.checked && !engineToggle.checked && !spotifyToggle.checked) {
            nextSource = "deezer";
        } else if (spotifyToggle.checked && !engineToggle.checked && !deezerToggle.checked) {
            nextSource = "spotify";
        } else if (changedId === "metadataSourceEngineEnabled") {
            nextSource = "engine";
        } else if (changedId === "metadataSourceSpotifyEnabled") {
            nextSource = "spotify";
        } else if (changedId === "metadataSourceDeezerEnabled") {
            nextSource = "deezer";
        }

        if (!nextSource) {
            nextSource = normalizeDownloadTagSource(state.config.downloadTagSource || "engine");
        }

        setDownloadTagSource(nextSource);
    }

    function normalizeDownloadTagSource(downloadTagSource) {
        const normalized = String(downloadTagSource || "").trim().toLowerCase();
        if (normalized === "engine") {
            return "engine";
        }
        if (normalized === "spotify") {
            return "spotify";
        }
        return "deezer";
    }

    function getCurrentDownloadEngineId() {
        const normalized = String(state.settingsCache?.service || "").trim().toLowerCase();
        if (!normalized || normalized === "auto") {
            return "auto";
        }
        if (normalized === "apple" || normalized === "applemusic" || normalized === "itunes") {
            return "apple";
        }
        return normalized;
    }

    function getCurrentDownloadEngineLabel() {
        const engine = getCurrentDownloadEngineId();
        switch (engine) {
        case "amazon":
            return "Amazon Music";
        case "apple":
            return "Apple Music";
        case "auto":
            return "Auto";
        case "deezer":
            return "Deezer";
        case "qobuz":
            return "Qobuz";
        case "tidal":
            return "TIDAL";
        default:
            return engine ? engine.charAt(0).toUpperCase() + engine.slice(1) : "current download engine";
        }
    }

    function getDownloadSourcePlatform(downloadTagSource) {
        const source = normalizeDownloadTagSource(downloadTagSource);
        if (source === "engine") {
            const engine = getCurrentDownloadEngineId();
            if (engine === "apple") {
                return "itunes";
            }
            if (engine === "deezer" || engine === "spotify" || engine === "boomplay") {
                return engine;
            }
            return null;
        }
        if (source === "deezer") {
            return "deezer";
        }
        if (source === "spotify") {
            return "spotify";
        }
        return null;
    }

    function renderDownloadTagSourceContext() {
        const source = getDownloadTagSource();
        const helper = el("download-tag-source-helper");
        if (!helper) {
            return;
        }

        if (source === "engine") {
            const engineLabel = getCurrentDownloadEngineLabel();
            const platformId = getDownloadSourcePlatform(source);
            helper.textContent = platformId
                ? `Follows Settings > Download Source (${engineLabel}). The download engine stays there; this profile now mirrors that engine for download-stage tag metadata.`
                : `Follows Settings > Download Source (${engineLabel}). The download engine stays there; this profile will use engine-native metadata during download when available.`;
            return;
        }

        const sourceLabel = source === "spotify" ? "Spotify" : "Deezer";
        helper.textContent = `Overrides Settings > Download Source only for tag metadata during download. Files still use the engine from Settings, while ${sourceLabel} supplies the tags written immediately on download.`;
    }

    function updateDownloadSourceAvailability() {
        const engineToggle = el("metadataSourceEngineEnabled");
        const deezerToggle = el("metadataSourceDeezerEnabled");
        const spotifyToggle = el("metadataSourceSpotifyEnabled");
        if (!(deezerToggle instanceof HTMLInputElement)
            || !(spotifyToggle instanceof HTMLInputElement)
            || !(engineToggle instanceof HTMLInputElement)) {
            return;
        }

        const spotifyUnavailable = state.authReady && state.spotifyStatus.connected !== true;
        spotifyToggle.title = spotifyUnavailable
            ? "Spotify login is not detected right now. You can still select Spotify, but some tags may be unavailable until connected."
            : "";
        engineToggle.disabled = false;
        deezerToggle.disabled = false;
    }

    function trimTrailingPathSeparators(value) {
        let end = value.length;
        while (end > 0) {
            const ch = value.codePointAt(end - 1);
            if (ch === 47 || ch === 92) {
                end -= 1;
                continue;
            }
            break;
        }

        return value.slice(0, end);
    }

    function normalizePathKey(path) {
        const normalized = String(path || "").trim().replaceAll("\\", "/");
        return trimTrailingPathSeparators(normalized).toLowerCase();
    }

    function isFolderEnabledFlag(value) {
        const normalized = String(value ?? "").trim().toLowerCase();
        if (!normalized) {
            return true;
        }
        if (typeof value === "boolean") {
            return value;
        }
        if (typeof value === "number") {
            return value !== 0;
        }
        return !/^(false|0|no|off|disabled)$/i.test(normalized);
    }

    function getSuccessLibraryById(id) {
        if (!Number.isFinite(id)) {
            return null;
        }
        return state.libraryFolders.find((folder) => folder.id === id && isMusicEnhancementEligibleFolder(folder)) || null;
    }

    function getSuccessLibraryByPath(path) {
        const key = normalizePathKey(path);
        if (!key) {
            return null;
        }
        return state.libraryFolders.find((folder) =>
            normalizePathKey(folder.rootPath) === key && isMusicEnhancementEligibleFolder(folder)) || null;
    }

    function renderSuccessLibraryOptions() {
        const select = el("autotag-move-success-library");
        if (!select) {
            return;
        }

        select.innerHTML = "";
        const placeholder = document.createElement("option");
        placeholder.value = "";
        placeholder.textContent = "Select library folder";
        select.appendChild(placeholder);

        state.libraryFolders
            .filter(isMusicEnhancementEligibleFolder)
            .forEach((folder) => {
            const label = folder.displayName || folder.libraryName || folder.rootPath || "Unnamed library";
            const option = document.createElement("option");
            option.value = String(folder.id);
            option.textContent = label;
            select.appendChild(option);
            });
    }

    function resolveLibraryFolderContentMode(folder) {
        const normalized = String(folder?.desiredQuality ?? "").trim().toLowerCase();
        if (!normalized) {
            return "music";
        }
        if (normalized.includes("video")) {
            return "video";
        }
        if (normalized.includes("podcast")) {
            return "podcast";
        }
        if (normalized.includes("atmos") || normalized === "5") {
            return "atmos";
        }
        return "music";
    }

    function isMusicEnhancementEligibleFolder(folder) {
        if (!folder || !isFolderEnabledFlag(folder.enabled)) {
            return false;
        }

        const contentMode = resolveLibraryFolderContentMode(folder);
        return contentMode !== "video" && contentMode !== "podcast";
    }

    function updateFolderSelectionSummary(triggerId, hiddenInputId, selectedIds) {
        const trigger = el(triggerId);
        const hiddenInput = el(hiddenInputId);
        const normalizedIds = parseFolderIdList(selectedIds);
        if (hiddenInput) {
            hiddenInput.value = normalizedIds.join(",");
        }
        if (!trigger) {
            return;
        }

        if (normalizedIds.length === 0) {
            trigger.textContent = "All enabled music folders";
            return;
        }

        const selectedFolders = state.libraryFolders.filter((folder) => normalizedIds.includes(folder.id));
        if (selectedFolders.length === 1) {
            trigger.textContent = selectedFolders[0].displayName || selectedFolders[0].libraryName || selectedFolders[0].rootPath || "1 folder selected";
            return;
        }

        trigger.textContent = `${selectedFolders.length} folders selected`;
    }

    function updateCoverMaintenanceFolderSummary(selectedIds) {
        updateFolderSelectionSummary("enhancementCoverFolderTrigger", "enhancementCoverFolder", selectedIds);
    }

    function updateFolderUniformityFolderSummary(selectedIds) {
        updateFolderSelectionSummary("enhancementFolderUniformityFolderTrigger", "enhancementFolderUniformityFolder", selectedIds);
    }

    function updateQualityChecksFolderSummary(selectedIds) {
        updateFolderSelectionSummary("enhancementQualityFolderTrigger", "enhancementQualityFolder", selectedIds);
    }

    function setupSimpleDropdown(dropdownId, triggerId, menuId) {
        const dropdown = el(dropdownId);
        const trigger = el(triggerId);
        const menu = el(menuId);
        if (!dropdown || !trigger || !menu || dropdown.dataset.bound === "true") {
            return;
        }

        dropdown.dataset.bound = "true";
        trigger.addEventListener("click", (event) => {
            event.preventDefault();
            event.stopPropagation();
            menu.classList.toggle("show");
        });

        document.addEventListener("click", (event) => {
            if (!dropdown.contains(event.target)) {
                menu.classList.remove("show");
            }
        });
    }

    function setupCoverMaintenanceFolderDropdown() {
        setupSimpleDropdown("enhancementCoverFolderDropdown", "enhancementCoverFolderTrigger", "enhancementCoverFolderMenu");
    }

    function setupQualityChecksFolderDropdown() {
        setupSimpleDropdown("enhancementQualityFolderDropdown", "enhancementQualityFolderTrigger", "enhancementQualityFolderMenu");
    }

    function setupFolderUniformityFolderDropdown() {
        setupSimpleDropdown("enhancementFolderUniformityFolderDropdown", "enhancementFolderUniformityFolderTrigger", "enhancementFolderUniformityFolderMenu");
    }

    function renderEnhancementFolderOptions() {
        ensureEnhancementDefaults();
        const folderUniformityOptions = el("enhancementFolderUniformityFolderOptions");
        if (folderUniformityOptions) {
            const selectedIds = parseFolderIdList(state.config.enhancement.folderUniformity.folderIds);
            folderUniformityOptions.innerHTML = "";
            state.libraryFolders
                .filter(isMusicEnhancementEligibleFolder)
                .forEach((folder) => {
                    const label = document.createElement("label");
                    label.className = "fallback-source-toggle";

                    const checkbox = document.createElement("input");
                    checkbox.type = "checkbox";
                    checkbox.dataset.uniformityFolderId = String(folder.id);
                    checkbox.checked = selectedIds.includes(folder.id);
                    checkbox.addEventListener("change", () => {
                        const ids = collectCheckedFolderIds(
                            folderUniformityOptions,
                            "input[data-uniformity-folder-id]:checked",
                            "uniformityFolderId"
                        );
                        updateFolderUniformityFolderSummary(ids);
                    });

                    label.appendChild(checkbox);
                    label.append(` ${folder.displayName || folder.libraryName || folder.rootPath || "Unnamed library"}`);
                    folderUniformityOptions.appendChild(label);
                });

            updateFolderUniformityFolderSummary(selectedIds);
            setupFolderUniformityFolderDropdown();
        }

        const coverOptions = el("enhancementCoverFolderOptions");
        if (coverOptions) {
            const selectedIds = parseFolderIdList(state.config.enhancement.coverMaintenance.folderIds);
            coverOptions.innerHTML = "";
            state.libraryFolders
                .filter(isMusicEnhancementEligibleFolder)
                .forEach((folder) => {
                    const label = document.createElement("label");
                    label.className = "fallback-source-toggle";

                    const checkbox = document.createElement("input");
                    checkbox.type = "checkbox";
                    checkbox.dataset.folderId = String(folder.id);
                    checkbox.checked = selectedIds.includes(folder.id);
                    checkbox.addEventListener("change", () => {
                        const ids = collectCheckedFolderIds(
                            coverOptions,
                            "input[data-folder-id]:checked",
                            "folderId"
                        );
                        updateCoverMaintenanceFolderSummary(ids);
                    });

                    label.appendChild(checkbox);
                    label.append(` ${folder.displayName || folder.libraryName || folder.rootPath || "Unnamed library"}`);
                    coverOptions.appendChild(label);
                });

            updateCoverMaintenanceFolderSummary(selectedIds);
            setupCoverMaintenanceFolderDropdown();
        }

        const qualityFolderOptions = el("enhancementQualityFolderOptions");
        if (qualityFolderOptions) {
            const selectedIds = parseFolderIdList(state.config.enhancement.qualityChecks.folderIds);
            qualityFolderOptions.innerHTML = "";
            state.libraryFolders
                .filter(isMusicEnhancementEligibleFolder)
                .forEach((folder) => {
                    const label = document.createElement("label");
                    label.className = "fallback-source-toggle";

                    const checkbox = document.createElement("input");
                    checkbox.type = "checkbox";
                    checkbox.dataset.qualityFolderId = String(folder.id);
                    checkbox.checked = selectedIds.includes(folder.id);
                    checkbox.addEventListener("change", () => {
                        const ids = collectCheckedFolderIds(
                            qualityFolderOptions,
                            "input[data-quality-folder-id]:checked",
                            "qualityFolderId"
                        );
                        updateQualityChecksFolderSummary(ids);
                        state.config.enhancement.qualityChecks.folderIds = parseFolderIdList(ids);
                        void refreshEnhancementTechnicalProfiles();
                        scheduleProfileAutoSave();
                    });

                    label.appendChild(checkbox);
                    label.append(` ${folder.displayName || folder.libraryName || folder.rootPath || "Unnamed library"}`);
                    qualityFolderOptions.appendChild(label);
                });

            updateQualityChecksFolderSummary(selectedIds);
            setupQualityChecksFolderDropdown();
        }

        setupTechnicalProfilesDropdown();
        void refreshEnhancementTechnicalProfiles();
    }

    function renderEnhancementTechnicalProfilesCatalog() {
        const container = el("enhancementTechnicalProfilesOptions");
        const status = el("enhancementTechnicalProfilesStatus");
        if (!container) {
            return;
        }

        const catalog = Array.isArray(state.technicalProfilesCatalog) ? state.technicalProfilesCatalog : [];
        const selectedProfiles = normalizeTechnicalProfiles(state.config?.enhancement?.qualityChecks?.technicalProfiles);
        const selectedSet = new Set(selectedProfiles.map((item) => item.toLowerCase()));

        container.innerHTML = "";
        if (state.technicalProfilesLoading) {
            const loading = document.createElement("div");
            loading.className = "helper";
            loading.textContent = "Loading technical profiles...";
            container.appendChild(loading);
            updateTechnicalProfilesSummary([]);
            return;
        }

        if (catalog.length === 0) {
            const empty = document.createElement("div");
            empty.className = "helper";
            empty.textContent = "No technical profiles found in the selected scope.";
            container.appendChild(empty);
            if (status) {
                status.textContent = "No profile data available for this folder/scope.";
            }
            state.config.enhancement.qualityChecks.technicalProfiles = [];
            updateTechnicalProfilesSummary([]);
            return;
        }

        const nextSelected = [];
        catalog.forEach((item) => {
            const profileValue = String(item?.value || "").trim();
            if (!profileValue) {
                return;
            }

            const row = document.createElement("label");
            row.className = "checkbox-group d-flex justify-content-between align-items-center";

            const input = document.createElement("input");
            input.type = "checkbox";
            input.dataset.technicalProfile = profileValue;
            input.checked = selectedSet.has(profileValue.toLowerCase());
            input.addEventListener("change", () => {
                syncSelectedTechnicalProfilesFromUI();
                scheduleProfileAutoSave();
            });

            const text = document.createElement("span");
            text.textContent = profileValue;
            text.className = "ms-2 flex-grow-1";

            const count = document.createElement("span");
            count.className = "helper";
            count.textContent = String(Number(item?.count || 0));

            row.appendChild(input);
            row.appendChild(text);
            row.appendChild(count);
            container.appendChild(row);

            if (input.checked) {
                nextSelected.push(profileValue);
            }
        });

        state.config.enhancement.qualityChecks.technicalProfiles = normalizeTechnicalProfiles(nextSelected);
        updateTechnicalProfilesSummary(nextSelected);
        if (status) {
            status.textContent = `${catalog.length} technical profile${catalog.length === 1 ? "" : "s"} available.`;
        }
    }

    function syncSelectedTechnicalProfilesFromUI() {
        const container = el("enhancementTechnicalProfilesOptions");
        if (!container) {
            return;
        }

        const selected = Array.from(container.querySelectorAll("input[data-technical-profile]:checked"))
            .map((input) => String(input.dataset.technicalProfile || "").trim());
        state.config.enhancement.qualityChecks.technicalProfiles = normalizeTechnicalProfiles(selected);
        updateTechnicalProfilesSummary(state.config.enhancement.qualityChecks.technicalProfiles);
    }

    function updateTechnicalProfilesSummary(selectedProfiles) {
        const trigger = el("enhancementTechnicalProfilesTrigger");
        if (!trigger) {
            return;
        }

        const normalized = normalizeTechnicalProfiles(selectedProfiles);
        if (normalized.length === 0) {
            trigger.textContent = "All profiles";
            return;
        }

        if (normalized.length === 1) {
            trigger.textContent = normalized[0];
            return;
        }

        trigger.textContent = `${normalized.length} profiles selected`;
    }

    function setupTechnicalProfilesDropdown() {
        setupSimpleDropdown("enhancementTechnicalProfilesDropdown", "enhancementTechnicalProfilesTrigger", "enhancementTechnicalProfilesMenu");
    }

    async function refreshEnhancementTechnicalProfiles() {
        const container = el("enhancementTechnicalProfilesOptions");
        if (!container) {
            return;
        }

        state.technicalProfilesLoading = true;
        renderEnhancementTechnicalProfilesCatalog();

        const qualityFolderInput = el("enhancementQualityFolder");
        const folderIds = parseFolderIdList(
            qualityFolderInput?.value ?? (state.config.enhancement.qualityChecks.folderIds ?? []).join(",")
        );
        const scope = "all";

        const query = new URLSearchParams();
        query.set("scope", scope);
        if (folderIds.length > 0) {
            query.set("folderIds", folderIds.join(","));
        }

        try {
            const response = await fetch(`/api/autotag/enhancement/technical-profiles?${query.toString()}`);
            if (!response.ok) {
                throw new Error(`HTTP ${response.status}`);
            }

            const payload = await response.json().catch(() => null);
            const profiles = Array.isArray(payload?.profiles) ? payload.profiles : [];
            state.technicalProfilesCatalog = profiles
                .map((item) => ({
                    value: String(item?.value || "").trim(),
                    count: Number(item?.count || 0)
                }))
                .filter((item) => item.value.length > 0)
                .sort(compareTechnicalProfilesByQuality);
        } catch (error) {
            state.technicalProfilesCatalog = [];
            const status = el("enhancementTechnicalProfilesStatus");
            if (status) {
                status.textContent = `Failed to load technical profiles: ${error?.message || error}`;
            }
        } finally {
            state.technicalProfilesLoading = false;
            renderEnhancementTechnicalProfilesCatalog();
        }
    }

    async function loadEnrichmentLibraryFolders() {
        try {
            const response = await fetch(AUTOTAG_LIBRARY_FOLDERS_API);
            if (!response.ok) {
                throw new Error(`HTTP ${response.status}`);
            }

            const data = await response.json();
            const source = Array.isArray(data) ? data : [];
            const filtered = source
                .filter((item) => item?.rootPath)
                .filter((item) => isFolderEnabledFlag(item?.enabled))
                .map((item) => {
                    const id = Number.parseInt(String(item.id ?? ""), 10);
                    return {
                        id: Number.isFinite(id) ? id : 0,
                        enabled: isFolderEnabledFlag(item.enabled),
                        rootPath: String(item.rootPath || "").trim(),
                        displayName: String(item.displayName || item.libraryName || "").trim(),
                        libraryName: String(item.libraryName || "").trim(),
                        desiredQuality: String(item.desiredQuality || "").trim()
                    };
                })
                .filter((item) => item.id > 0 && item.rootPath.length > 0);

            const dedup = new Map();
            filtered.forEach((item) => {
                const key = normalizePathKey(item.rootPath);
                if (!key || dedup.has(key)) {
                    return;
                }
                dedup.set(key, item);
            });

            state.libraryFolders = Array.from(dedup.values()).sort((a, b) => {
                const aName = (a.displayName || a.libraryName || a.rootPath).toLowerCase();
                const bName = (b.displayName || b.libraryName || b.rootPath).toLowerCase();
                if (aName < bName) {
                    return -1;
                }
                if (aName > bName) {
                    return 1;
                }
                return 0;
            });
        } catch (error) {
            console.warn("Failed to load library folders for enrichment destinations", error);
            state.libraryFolders = [];
        }

        renderSuccessLibraryOptions();
        renderEnhancementFolderOptions();
    }

    function ensureEffectivePlatforms(config) {
        if (!config || typeof config !== "object") {
            return config;
        }

        config.downloadTagSource = normalizeDownloadTagSource(config.downloadTagSource || getDownloadTagSource());
        const seen = new Set();
        const platforms = [];
        const sourceList = Array.isArray(config.platforms) ? config.platforms : [];

        sourceList.forEach((platform) => {
            const normalized = String(platform || "").trim();
            if (!normalized) {
                return;
            }
            const key = normalized.toLowerCase();
            if (seen.has(key)) {
                return;
            }
            seen.add(key);
            platforms.push(normalized);
        });

        config.platforms = platforms;
        config.multiplatform = platforms.length > 1;
        return config;
    }

    function getDownloadTagsList() {
        const source = getDownloadTagSource();
        if (!source) {
            return [];
        }
        const platformId = getDownloadSourcePlatform(source);
        const platform = state.platforms.find((item) => item.id === platformId);
        const platformTags = Array.isArray(platform?.downloadTags) && platform.downloadTags.length > 0
            ? platform.downloadTags
            : [];
        return platformTags
            .filter((tagId) => !isHiddenSpotifyAudioFeatureTag(tagId))
            .map((tagId) => ({
            tag: tagId,
            label: DOWNLOAD_TAG_LABELS[tagId] || tagId
        }));
    }

    function getDownloadTagIds() {
        const source = getDownloadTagSource();
        if (!source) {
            return [];
        }
        const platformId = getDownloadSourcePlatform(source);
        const platform = state.platforms.find((item) => item.id === platformId);
        if (!platform || !Array.isArray(platform.downloadTags)) {
            return [];
        }
        return platform.downloadTags.filter((tagId) => !isHiddenSpotifyAudioFeatureTag(tagId));
    }

    function normalizeDownloadTags(selected) {
        const allowed = getDownloadTagIds();
        if (!allowed.length) {
            return selected;
        }
        const allowedSet = new Set(allowed.map((tag) => String(tag).toLowerCase()));
        const filtered = (selected || []).filter((tag) => allowedSet.has(String(tag).toLowerCase()));
        if (filtered.length > 0) {
            return filtered;
        }
        if (selected && selected.length > 0) {
            return allowed.slice();
        }
        return selected || [];
    }

    function buildMergedTagSelection(primary, secondary) {
        const merged = [];
        const seen = new Set();
        [primary, secondary].forEach((source) => {
            (source || []).forEach((value) => {
                const tag = String(value || "").trim();
                if (!tag) {
                    return;
                }
                const key = tag.toLowerCase();
                if (seen.has(key)) {
                    return;
                }
                seen.add(key);
                merged.push(tag);
            });
        });
        return merged;
    }

    function syncEnhancementTagsWithDownloadAndEnrichment() {
        const enrichmentTags = Array.isArray(state.config.tags) ? state.config.tags : [];
        const normalizedDownloadTags = normalizeDownloadTags(
            Array.isArray(state.config.downloadTags) ? state.config.downloadTags : []
        );
        state.config.tags = enrichmentTags;
        state.config.downloadTags = normalizedDownloadTags;
        state.config.gapFillTags = buildMergedTagSelection(normalizedDownloadTags, enrichmentTags);
    }

    function isDownloadTagSupported(tagKey) {
        if (isHiddenSpotifyAudioFeatureTag(tagKey)) {
            return false;
        }

        const source = getDownloadTagSource();
        if (!source) {
            return false;
        }
        const platformId = getDownloadSourcePlatform(source);
        const platform = state.platforms.find((item) => item.id === platformId);
        if (!platform || !Array.isArray(platform.downloadTags) || platform.downloadTags.length === 0) {
            return false;
        }
        return platform.downloadTags.includes(tagKey);
    }

    function refreshDownloadTagsForSource() {
        renderDownloadTagSourceContext();
        updateDownloadTagsVisibility();
        const downloadTagsContainer = el("autotag-download-tags");
        if (!getDownloadTagSource() || getDownloadTagIds().length === 0) {
            if (downloadTagsContainer) {
                downloadTagsContainer.innerHTML = "";
            }
            syncEnhancementTagsWithDownloadAndEnrichment();
            renderTags("gap-fill-tags", state.config.gapFillTags || [], "gapFillTags");
            return;
        }
        const selected = normalizeDownloadTags(state.config.downloadTags || []);
        state.config.downloadTags = selected;
        syncEnhancementTagsWithDownloadAndEnrichment();
        renderTags("autotag-download-tags", selected, "downloadTags");
        renderTags("gap-fill-tags", state.config.gapFillTags || [], "gapFillTags");
    }

    function updateDownloadTagsVisibility() {
        const source = getDownloadTagSource();
        const enabled = Boolean(source);
        const hasTagCatalog = enabled && getDownloadTagIds().length > 0;
        const helper = el("download-tags-helper");
        const grid = el("autotag-download-tags");
        const toggleButton = document.querySelector('button[data-tags-target="downloadTags"]');
        const toolbar = toggleButton?.closest(".tags-toolbar");
        if (toolbar) {
            toolbar.style.display = hasTagCatalog ? "flex" : "none";
        }
        if (grid) {
            grid.style.display = hasTagCatalog ? "grid" : "none";
        }
        if (helper) {
            const platformId = getDownloadSourcePlatform(source);
            if (!enabled) {
                helper.textContent = "Enable a metadata source to choose download tags.";
            } else if (source === "engine" && !platformId) {
                helper.textContent = `The active download engine (${getCurrentDownloadEngineLabel()}) does not publish a curated AutoTag download-tag list. Native engine metadata will still be used during download when available.`;
            } else if (source === "engine") {
                helper.textContent = `These tags follow Settings > Download Source (${getCurrentDownloadEngineLabel()}). Change this profile only if you want a different metadata source than the download engine.`;
            } else {
                helper.textContent = "Select which tags to write during the download process. This metadata source only affects tags written during download, not the engine that downloads the file.";
            }
        }
    }

    function ensureCustomDefaults() {
        if (!state.config.custom) {
            state.config.custom = {};
        }
        if (!state.config.custom.itunes) {
            state.config.custom.itunes = {};
        }
        if (!state.config.custom.itunes.art_resolution) {
            state.config.custom.itunes.art_resolution = 1000;
        }
    }

    function ensurePlatformCustomDefaults() {
        if (!state.platforms.length) {
            return;
        }
        if (!state.config.custom) {
            state.config.custom = {};
        }
        state.platforms.forEach((platform) => {
            const options = platform.customOptions?.options || [];
            if (!options.length) {
                return;
            }
            if (!state.config.custom[platform.id]) {
                state.config.custom[platform.id] = {};
            }
            options.forEach((option) => {
                if (state.config.custom[platform.id][option.id] === undefined) {
                    state.config.custom[platform.id][option.id] = option.value?.value ?? null;
                }
            });
        });
    }

    function parseOptionalFolderId(value) {
        const parsed = Number.parseInt(String(value ?? "").trim(), 10);
        return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
    }

    function collectCheckedFolderIds(container, selector, dataKey) {
        if (!container) {
            return [];
        }

        const values = Array.from(container.querySelectorAll(selector))
            .map((input) => parseOptionalFolderId(input?.dataset?.[dataKey]))
            .filter((item, index, array) => item != null && array.indexOf(item) === index);
        return values;
    }

    function parseFolderIdList(value) {
        const values = Array.isArray(value)
            ? value
            : String(value ?? "").split(",");
        return values
            .map((item) => parseOptionalFolderId(item))
            .filter((item, index, array) => item != null && array.indexOf(item) === index);
    }

    function normalizeTechnicalProfiles(value) {
        const source = Array.isArray(value) ? value : [];
        const seen = new Set();
        const normalized = [];
        source.forEach((item) => {
            const profile = String(item || "").trim();
            if (!profile) {
                return;
            }
            const key = profile.toLowerCase();
            if (seen.has(key)) {
                return;
            }
            seen.add(key);
            normalized.push(profile);
        });
        return normalized;
    }

    function getTechnicalProfileSortKey(profileValue) {
        const text = String(profileValue || "").trim();
        const parts = text.split("•").map((part) => part.trim());
        const extension = (parts[0] || "").replace(/^\./, "").toLowerCase();
        const bitsMatch = /(\d+)/.exec(parts[1] || "");
        const rateMatch = /([\d.]+)/.exec(parts[2] || "");

        const bitDepth = bitsMatch ? Number.parseInt(bitsMatch[1], 10) : 0;
        const sampleRateKhz = rateMatch ? Number.parseFloat(rateMatch[1]) : 0;

        const losslessExtensions = new Set(["flac", "alac", "wav", "aiff", "aif", "ape", "dsf", "dff"]);

        let formatRank = 1;
        if (text.toLowerCase().includes("atmos")) {
            formatRank = 4;
        } else if (losslessExtensions.has(extension)) {
            formatRank = bitDepth >= 24 || sampleRateKhz > 48 ? 3 : 2;
        }

        return { formatRank, bitDepth, sampleRateKhz, extension };
    }

    function compareTechnicalProfilesByQuality(a, b) {
        const aKey = getTechnicalProfileSortKey(a?.value);
        const bKey = getTechnicalProfileSortKey(b?.value);
        if (aKey.formatRank !== bKey.formatRank) {
            return aKey.formatRank - bKey.formatRank;
        }
        if (aKey.bitDepth !== bKey.bitDepth) {
            return aKey.bitDepth - bKey.bitDepth;
        }
        if (aKey.sampleRateKhz !== bKey.sampleRateKhz) {
            return aKey.sampleRateKhz - bKey.sampleRateKhz;
        }
        if (aKey.extension !== bKey.extension) {
            return aKey.extension.localeCompare(bKey.extension);
        }
        return String(a?.value || "").localeCompare(String(b?.value || ""));
    }

    function parseOptionalBoundedInt(value, min, max) {
        const text = String(value ?? "").trim();
        if (!text) {
            return null;
        }
        const parsed = Number.parseInt(text, 10);
        if (!Number.isFinite(parsed)) {
            return null;
        }
        return Math.max(min, Math.min(max, parsed));
    }

    function mapArtworkProviderToCoverSource(provider) {
        const normalized = String(provider || "").trim().toLowerCase();
        if (normalized === "apple") {
            return "itunes";
        }
        if (normalized === "deezer") {
            return "deezer";
        }
        return null;
    }

    function getCoverTargetResolutionPlatformProfiles() {
        if (!Array.isArray(state.platforms) || state.platforms.length === 0) {
            return [];
        }

        return state.platforms
            .map((platform) => {
                const options = platform?.customOptions?.options || [];
                const option = options.find((candidate) => candidate?.id === "art_resolution");
                if (!option) {
                    return null;
                }

                const min = Number.parseInt(String(option?.value?.min ?? 100), 10);
                const max = Number.parseInt(String(option?.value?.max ?? 5000), 10);
                const fallback = Number.parseInt(String(option?.value?.value ?? 1200), 10);

                return {
                    id: platform.id,
                    name: platform.name || platform.id,
                    min: Number.isFinite(min) ? min : 100,
                    max: Number.isFinite(max) ? max : 5000,
                    fallback: Number.isFinite(fallback) ? fallback : 1200
                };
            })
            .filter((profile) => profile != null);
    }

    function normalizeCoverTargetResolutionPlatform(platformId, profiles = null) {
        const availableProfiles = Array.isArray(profiles)
            ? profiles
            : getCoverTargetResolutionPlatformProfiles();
        const requested = String(platformId || "").trim();
        const requestedNormalized = normalizePlatformId(requested);

        if (availableProfiles.length === 0) {
            return requested || "deezer";
        }

        const matched = availableProfiles.find((profile) => normalizePlatformId(profile.id) === requestedNormalized);
        if (matched) {
            return matched.id;
        }

        const deezer = availableProfiles.find((profile) => normalizePlatformId(profile.id) === "deezer");
        if (deezer) {
            return deezer.id;
        }

        return availableProfiles[0].id;
    }

    function resolveCoverMaintenanceTargetResolution(artworkSettings = null) {
        const settings = artworkSettings || readArtworkSettingsFromUI(state.settingsCache || {});
        const fallbackEnabled = settings.artworkFallbackEnabled ?? true;
        const providerOrder = fallbackEnabled
            ? normalizeProviderOrder(settings.artworkFallbackOrder || ARTWORK_SOURCE_ORDER.join(","), ARTWORK_SOURCE_ORDER)
            : buildPreferredProviderOrder(
                settings.artworkDefaultSource || el("artworkDefaultSource")?.value || ARTWORK_SOURCE_ORDER[0],
                settings.artworkFallbackOrder || ARTWORK_SOURCE_ORDER.join(","),
                ARTWORK_SOURCE_ORDER
            );
        const profiles = getCoverTargetResolutionPlatformProfiles();
        if (profiles.length > 0) {
            const preferredProvider = providerOrder.find((provider) =>
                profiles.some((profile) => normalizePlatformId(profile.id) === normalizePlatformId(provider))
            );
            const platformId = normalizeCoverTargetResolutionPlatform(preferredProvider || "deezer", profiles);
            const profile = profiles.find((candidate) => candidate.id === platformId) || profiles[0];
            const configuredRaw = Number.parseInt(
                String(state.config?.custom?.[profile.id]?.art_resolution ?? profile.fallback),
                10
            );
            const configured = Number.isFinite(configuredRaw) ? configuredRaw : profile.fallback;
            const clampedToProfile = Math.max(profile.min, Math.min(profile.max, configured));
            const resolution = Math.max(300, Math.min(5000, clampedToProfile));
            return {
                mode: "platform",
                resolution,
                platformId: profile.id,
                platformName: profile.name
            };
        }

        return {
            mode: "manual",
            resolution: 1200,
            platformId: null,
            platformName: null
        };
    }

    function resolveCoverMaintenancePolicyFromTechnical(artworkSettings = null) {
        const settings = artworkSettings || readArtworkSettingsFromUI(state.settingsCache || {});
        const fallbackEnabled = settings.artworkFallbackEnabled ?? true;
        const providerOrder = fallbackEnabled
            ? normalizeProviderOrder(settings.artworkFallbackOrder || ARTWORK_SOURCE_ORDER.join(","), ARTWORK_SOURCE_ORDER)
            : buildPreferredProviderOrder(
                settings.artworkDefaultSource || el("artworkDefaultSource")?.value || ARTWORK_SOURCE_ORDER[0],
                settings.artworkFallbackOrder || ARTWORK_SOURCE_ORDER.join(","),
                ARTWORK_SOURCE_ORDER
            );

        const mappedSources = [];
        providerOrder.forEach((provider) => {
            const mapped = mapArtworkProviderToCoverSource(provider);
            if (mapped && !mappedSources.includes(mapped)) {
                mappedSources.push(mapped);
            }
        });

        return {
            providerOrder,
            sources: mappedSources,
            targetPolicy: resolveCoverMaintenanceTargetResolution(settings)
        };
    }

    function updateCoverMaintenanceTargetResolutionPolicyUI() {
        updateActiveProfileSummary();
    }

    function ensureEnhancementDefaults(config = state.config) {
        if (!config || typeof config !== "object") {
            return;
        }

        if (!config.enhancement || typeof config.enhancement !== "object") {
            config.enhancement = structuredClone(DEFAULT_CONFIG.enhancement);
            return;
        }

        const enhancement = config.enhancement;
        if (!enhancement.folderUniformity || typeof enhancement.folderUniformity !== "object") {
            enhancement.folderUniformity = structuredClone(DEFAULT_CONFIG.enhancement.folderUniformity);
        }
        if (!enhancement.coverMaintenance || typeof enhancement.coverMaintenance !== "object") {
            enhancement.coverMaintenance = structuredClone(DEFAULT_CONFIG.enhancement.coverMaintenance);
        }
        if (!enhancement.qualityChecks || typeof enhancement.qualityChecks !== "object") {
            enhancement.qualityChecks = structuredClone(DEFAULT_CONFIG.enhancement.qualityChecks);
        }

        const folderUniformity = enhancement.folderUniformity;
        folderUniformity.folderIds = parseFolderIdList(folderUniformity.folderIds);
        delete folderUniformity.folderId;
        folderUniformity.enforceFolderStructure = folderUniformity.enforceFolderStructure !== false;
        folderUniformity.moveMisplacedFiles = folderUniformity.moveMisplacedFiles !== false;
        folderUniformity.mergeIntoExistingDestinationFolders = folderUniformity.mergeIntoExistingDestinationFolders !== false;
        folderUniformity.renameFilesToTemplate = folderUniformity.renameFilesToTemplate !== false;
        folderUniformity.removeEmptyFolders = folderUniformity.removeEmptyFolders !== false;
        folderUniformity.resolveSameTrackQualityConflicts = folderUniformity.resolveSameTrackQualityConflicts !== false;
        folderUniformity.keepBothOnUnresolvedConflicts = folderUniformity.keepBothOnUnresolvedConflicts !== false;
        folderUniformity.onlyMoveWhenTagged = folderUniformity.onlyMoveWhenTagged === true;
        folderUniformity.onlyReorganizeAlbumsWithFullTrackSets = folderUniformity.onlyReorganizeAlbumsWithFullTrackSets === true;
        folderUniformity.skipCompilationFolders = folderUniformity.skipCompilationFolders === true;
        folderUniformity.skipVariousArtistsFolders = folderUniformity.skipVariousArtistsFolders === true;
        folderUniformity.generateReconciliationReport = folderUniformity.generateReconciliationReport === true;
        folderUniformity.useShazamForUntaggedFiles = folderUniformity.useShazamForUntaggedFiles === true;
        folderUniformity.duplicateConflictPolicy = String(folderUniformity.duplicateConflictPolicy || "keep_best").trim().toLowerCase() || "keep_best";
        folderUniformity.artworkPolicy = String(folderUniformity.artworkPolicy || "preserve_existing").trim().toLowerCase() || "preserve_existing";
        folderUniformity.lyricsPolicy = String(folderUniformity.lyricsPolicy || "merge").trim().toLowerCase() || "merge";
        folderUniformity.runDedupe = folderUniformity.runDedupe !== false;
        folderUniformity.useShazamForDedupe = folderUniformity.useShazamForDedupe === true;
        folderUniformity.includeSubfolders = folderUniformity.includeSubfolders !== false;
        folderUniformity.duplicatesFolderName = String(
            folderUniformity.duplicatesFolderName || DEFAULT_CONFIG.enhancement.folderUniformity.duplicatesFolderName
        ).trim() || DEFAULT_CONFIG.enhancement.folderUniformity.duplicatesFolderName;
        delete folderUniformity.usePrimaryArtistFolders;
        delete folderUniformity.multiArtistSeparator;
        delete folderUniformity.createArtistFolder;
        delete folderUniformity.artistNameTemplate;
        delete folderUniformity.createAlbumFolder;
        delete folderUniformity.albumNameTemplate;
        delete folderUniformity.createCDFolder;
        delete folderUniformity.createStructurePlaylist;
        delete folderUniformity.createSingleFolder;
        delete folderUniformity.createPlaylistFolder;
        delete folderUniformity.playlistNameTemplate;
        delete folderUniformity.illegalCharacterReplacer;
        delete folderUniformity.preferredExtensions;

        const coverMaintenance = enhancement.coverMaintenance;
        coverMaintenance.folderIds = parseFolderIdList(coverMaintenance.folderIds);
        delete coverMaintenance.folderId;
        coverMaintenance.minResolution = Number.parseInt(String(coverMaintenance.minResolution ?? 500), 10);
        if (!Number.isFinite(coverMaintenance.minResolution)) {
            coverMaintenance.minResolution = 500;
        }
        coverMaintenance.minResolution = Math.max(100, Math.min(2000, coverMaintenance.minResolution));
        coverMaintenance.replaceMissingEmbeddedCovers = coverMaintenance.replaceMissingEmbeddedCovers === true;
        coverMaintenance.syncExternalCovers = coverMaintenance.syncExternalCovers === true;
        coverMaintenance.queueAnimatedArtwork = coverMaintenance.queueAnimatedArtwork === true;
        coverMaintenance.workerCount = Number.parseInt(String(coverMaintenance.workerCount ?? 8), 10);
        if (!Number.isFinite(coverMaintenance.workerCount)) {
            coverMaintenance.workerCount = 8;
        }
        coverMaintenance.workerCount = Math.max(1, Math.min(32, coverMaintenance.workerCount));

        const qualityChecks = enhancement.qualityChecks;
        qualityChecks.folderIds = parseFolderIdList(qualityChecks.folderIds);
        delete qualityChecks.folderId;
        qualityChecks.scope = String(qualityChecks.scope || "all").toLowerCase() === "watchlist" ? "watchlist" : "all";
        qualityChecks.technicalProfiles = normalizeTechnicalProfiles(qualityChecks.technicalProfiles);
        qualityChecks.queueAtmosAlternatives = qualityChecks.queueAtmosAlternatives === true;
        qualityChecks.queueLyricsRefresh = qualityChecks.queueLyricsRefresh === true;
        qualityChecks.flagDuplicates = qualityChecks.flagDuplicates === true;
        qualityChecks.flagMissingTags = qualityChecks.flagMissingTags === true;
        qualityChecks.flagMismatchedMetadata = qualityChecks.flagMismatchedMetadata === true;
        qualityChecks.useDuplicatesFolder = qualityChecks.useDuplicatesFolder !== false;
        qualityChecks.useShazamForDedupe = qualityChecks.useShazamForDedupe === true;
        qualityChecks.duplicatesFolderName = String(
            qualityChecks.duplicatesFolderName || DEFAULT_CONFIG.enhancement.qualityChecks.duplicatesFolderName
        ).trim() || DEFAULT_CONFIG.enhancement.qualityChecks.duplicatesFolderName;
        delete qualityChecks.minFormat;
        delete qualityChecks.minBitDepth;
        delete qualityChecks.minSampleRateKhz;
        qualityChecks.cooldownMinutes = parseOptionalBoundedInt(qualityChecks.cooldownMinutes, 0, 43200);
    }

    function createPlatformSpeedIcon(platform) {
        const speed = document.createElement("span");
        speed.className = "platform-meta-icon";
        speed.innerHTML = '<i class="fas fa-tachometer-alt"></i>';
        if (platform.maxThreads === 1) {
            speed.title = "This platform allows up to 1 concurrent search";
            return speed;
        }
        if (platform.maxThreads > 1) {
            speed.title = `This platform allows up to ${platform.maxThreads} concurrent searches`;
            return speed;
        }
        speed.title = "This platform allows unlimited concurrent searches";
        return speed;
    }

    function createPlatformMeta(platform) {
        const meta = document.createElement("div");
        meta.className = "platform-meta";
        meta.appendChild(createPlatformSpeedIcon(platform));

        if (platform.requiresAuth) {
            const lock = document.createElement("span");
            lock.className = "platform-meta-icon";
            lock.innerHTML = '<i class="fas fa-lock"></i>';
            lock.title = "Platform requires an account";
            meta.appendChild(lock);
        }

        if (platform.supportsLyrics) {
            const lyrics = document.createElement("span");
            lyrics.className = "platform-meta-icon";
            lyrics.innerHTML = '<i class="fas fa-microphone"></i>';
            lyrics.title = "Platform can fetch lyrics";
            meta.appendChild(lyrics);
        }

        return meta;
    }

    function createPlatformIcon(platform) {
        const primaryIcon = platform.icon || platform.fallbackIcon || "";
        const fallbackIcon = platform.fallbackIcon || "";
        preloadPlatformIcon(primaryIcon);
        if (fallbackIcon && fallbackIcon !== primaryIcon) {
            preloadPlatformIcon(fallbackIcon);
        }

        const iconEl = document.createElement("img");
        iconEl.src = primaryIcon;
        iconEl.alt = platform.name || platform.id;
        iconEl.className = "platform-icon";
        iconEl.loading = "eager";
        iconEl.decoding = "sync";
        if ("fetchPriority" in iconEl) {
            iconEl.fetchPriority = "high";
        }

        let usedFallbackIcon = false;
        iconEl.addEventListener("error", () => {
            if (!usedFallbackIcon && fallbackIcon && fallbackIcon !== primaryIcon) {
                usedFallbackIcon = true;
                iconEl.src = fallbackIcon;
                return;
            }
            iconEl.style.opacity = "0.45";
        });

        return iconEl;
    }

    function createPlatformCheckbox(platform, isSelected) {
        const checkbox = document.createElement("input");
        checkbox.type = "checkbox";
        checkbox.checked = isSelected;
        const selectable = canEnablePlatform(platform);
        checkbox.disabled = !selectable;
        if (!selectable && platform.requiresAuth) {
            checkbox.title = state.authReady
                ? "Login required before enabling this platform."
                : "Checking login status...";
        }
        checkbox.addEventListener("change", () => togglePlatform(platform.id));
        return checkbox;
    }

    function createPlatformInfo(platform, isSelected) {
        const info = document.createElement("div");
        info.className = "platform-info";
        info.appendChild(createPlatformCheckbox(platform, isSelected));
        info.appendChild(createPlatformIcon(platform));

        const titleRow = document.createElement("div");
        titleRow.className = "platform-title";

        const name = document.createElement("span");
        name.textContent = platform.name;
        titleRow.appendChild(name);
        titleRow.appendChild(createPlatformMeta(platform));

        const text = document.createElement("div");
        text.className = "platform-text";
        text.appendChild(titleRow);
        if (platform.description) {
            const description = document.createElement("div");
            description.className = "platform-description";
            description.textContent = platform.description;
            text.appendChild(description);
        }

        info.appendChild(text);
        return info;
    }

    function createPlatformHandle(isSelected) {
        const handle = document.createElement("div");
        handle.className = "platform-handle";
        if (isSelected) {
            handle.classList.add("is-draggable");
            handle.setAttribute("draggable", "true");
        } else {
            handle.classList.add("is-disabled");
        }
        handle.innerHTML = `
            <svg class="platform-handle-icon" width="16" height="16" viewBox="0 0 16 16" aria-hidden="true" focusable="false">
                <rect x="2.5" y="3" width="11" height="1.6" rx="0.8"></rect>
                <rect x="2.5" y="7.2" width="11" height="1.6" rx="0.8"></rect>
                <rect x="2.5" y="11.4" width="11" height="1.6" rx="0.8"></rect>
            </svg>
        `;
        return handle;
    }

    function appendPlatformRow(container, platform) {
        const isSelected = state.config.platforms.includes(platform.id);
        const row = document.createElement("div");
        row.className = "platform-row";
        row.dataset.platformId = platform.id;
        if (isSelected) {
            row.classList.add("platform-row-draggable");
        }

        const handle = createPlatformHandle(isSelected);
        row.appendChild(createPlatformInfo(platform, isSelected));
        row.appendChild(handle);
        bindPlatformDrag(row, handle, platform.id, container);
        container.appendChild(row);
    }

    function renderPlatforms() {
        const container = el("autotag-platforms");
        container.innerHTML = "";
        rebuildSourceOrdersFromPlatforms();
        const order = state.config.platforms;
        const platforms = getPlatformsSortedBySelectionOrder(order);
        platforms.forEach((platform) => appendPlatformRow(container, platform));

        if (typeof state.syncLyricsFallbackOrder === "function") {
            state.syncLyricsFallbackOrder();
        }
        if (typeof state.syncArtworkFallbackOrder === "function") {
            state.syncArtworkFallbackOrder();
        }
        if (typeof state.syncArtistArtworkFallbackOrder === "function") {
            state.syncArtistArtworkFallbackOrder();
        }
        syncFallbackSourceControls();
        renderPlatformOptions();
    }

    function getPlatformsSortedBySelectionOrder(order) {
        return [...state.platforms].sort((a, b) => {
            const aIndex = order.indexOf(a.id);
            const bIndex = order.indexOf(b.id);
            const safeA = aIndex === -1 ? 999 : aIndex;
            const safeB = bIndex === -1 ? 999 : bIndex;
            return safeA - safeB;
        });
    }

    function bindPlatformDrag(row, handle, platformId, container) {
        if (handle.dataset.dragBound) {
            return;
        }

        handle.dataset.dragBound = "true";

        handle.addEventListener("dragstart", (event) => {
            if (!row.classList.contains("platform-row-draggable")) {
                event.preventDefault();
                return;
            }
            draggedPlatformId = platformId;
            event.dataTransfer.effectAllowed = "move";
            event.dataTransfer.setData("text/plain", platformId);
            row.classList.add("is-dragging");
        });

        handle.addEventListener("dragend", () => {
            draggedPlatformId = null;
            row.classList.remove("is-dragging");
            container.querySelectorAll(".platform-row").forEach((item) => item.classList.remove("is-drag-over"));
        });

        row.addEventListener("dragover", (event) => {
            if (!draggedPlatformId || draggedPlatformId === platformId || !row.classList.contains("platform-row-draggable")) {
                return;
            }
            event.preventDefault();
            row.classList.add("is-drag-over");
        });

        row.addEventListener("dragleave", () => {
            row.classList.remove("is-drag-over");
        });

        row.addEventListener("drop", (event) => {
            if (!draggedPlatformId || draggedPlatformId === platformId || !row.classList.contains("platform-row-draggable")) {
                return;
            }
            event.preventDefault();
            row.classList.remove("is-drag-over");
            reorderPlatform(draggedPlatformId, platformId);
        });
    }

    function togglePlatform(id) {
        const index = state.config.platforms.indexOf(id);
        if (index === -1) {
            const platform = state.platforms.find((item) => item.id === id);
            if (platform && !canEnablePlatform(platform)) {
                if (platform.requiresAuth) {
                    showToast(`${platform.name} requires successful login before it can be enabled.`, "warning");
                }
                renderPlatforms();
                return;
            }
            state.config.platforms.push(id);
        } else {
            state.config.platforms.splice(index, 1);
        }
        storeSelectedPlatforms();
        renderPlatforms();
        syncEnhancementTagsWithDownloadAndEnrichment();
        renderTags("autotag-tags", state.config.tags, "tags");
        renderTags("gap-fill-tags", state.config.gapFillTags || [], "gapFillTags");
        renderTags("autotag-overwrite-tags", state.config.overwriteTags, "overwriteTags");
    }

    function reorderPlatform(draggedId, targetId) {
        const order = state.config.platforms;
        const fromIndex = order.indexOf(draggedId);
        const toIndex = order.indexOf(targetId);
        if (fromIndex === -1 || toIndex === -1 || fromIndex === toIndex) {
            return;
        }
        order.splice(fromIndex, 1);
        order.splice(toIndex, 0, draggedId);
        renderPlatforms();
        storeSelectedPlatforms();
    }

    function ensurePlatformOptionDefaults(platformId, options) {
        if (!state.config.custom[platformId]) {
            state.config.custom[platformId] = {};
        }
        (options || []).forEach((option) => {
            if (state.config.custom[platformId][option.id] === undefined) {
                state.config.custom[platformId][option.id] = option.value?.value ?? null;
            }
        });
    }

    function normalizePlatformId(platformId) {
        return String(platformId || "").trim().toLowerCase();
    }

    const TECHNICAL_PLATFORM_ORDER = Object.freeze([
        "boomplay",
        "lastfm",
        "discogs",
        "deezer",
        "beatport",
        "beatsource",
        "itunes",
        "shazam",
        "musicbrainz"
    ]);

    const TECHNICAL_PLATFORM_ORDER_INDEX = Object.freeze(
        TECHNICAL_PLATFORM_ORDER.reduce((acc, platformId, index) => {
            acc[platformId] = index;
            return acc;
        }, {})
    );

    function clampItunesArtResolution(value, fallback = 1000) {
        const parsed = Number.parseInt(String(value ?? ""), 10);
        if (!Number.isFinite(parsed)) {
            return fallback;
        }
        return Math.max(100, Math.min(5000, parsed));
    }

    function createPlatformOptionLabel(option) {
        const label = document.createElement("label");
        label.textContent = option.label;
        if (option.tooltip) {
            label.title = option.tooltip;
        }
        return label;
    }

    function setPlatformOptionValue(platformId, optionId, value) {
        state.config.custom[platformId][optionId] = value;
        if (optionId === "art_resolution") {
            updateCoverMaintenanceTargetResolutionPolicyUI();
        }
    }

    function createBooleanPlatformOptionField(platform, option) {
        const field = document.createElement("div");
        field.className = "form-group";
        const wrapper = document.createElement("div");
        wrapper.className = "checkbox-group";
        const input = document.createElement("input");
        input.type = "checkbox";
        input.checked = Boolean(state.config.custom[platform.id][option.id]);
        input.addEventListener("change", () => {
            setPlatformOptionValue(platform.id, option.id, input.checked);
        });

        wrapper.appendChild(input);
        const text = document.createElement("span");
        text.textContent = option.label;
        if (option.tooltip) {
            text.title = option.tooltip;
        }
        wrapper.appendChild(text);
        field.appendChild(wrapper);
        return field;
    }

    function createNumberPlatformOptionField(platform, option) {
        const field = document.createElement("div");
        field.className = "form-group";
        field.appendChild(createPlatformOptionLabel(option));

        const min = option.value?.min ?? 0;
        const max = option.value?.max ?? 0;
        const step = option.value?.step ?? 1;
        const fallback = option.value?.value ?? 0;
        const rawValue = Number(state.config.custom[platform.id][option.id]);
        const initial = Number.isFinite(rawValue) ? rawValue : fallback;
        setPlatformOptionValue(platform.id, option.id, initial);

        if (option.value?.slider) {
            const row = document.createElement("div");
            row.className = "autotag-slider-row";

            const slider = document.createElement("input");
            slider.type = "range";
            slider.min = min;
            slider.max = max;
            slider.step = step;
            slider.value = initial;
            slider.className = "autotag-slider";

            const numberInput = document.createElement("input");
            numberInput.type = "number";
            numberInput.min = min;
            numberInput.max = max;
            numberInput.step = step;
            numberInput.value = initial;
            numberInput.className = "autotag-slider-number";

            const value = document.createElement("span");
            value.className = "autotag-slider-value";
            value.textContent = `${initial}`;

            const update = (nextRaw) => {
                const parsed = Number(nextRaw);
                if (!Number.isFinite(parsed)) {
                    return;
                }
                const clamped = Math.min(max, Math.max(min, parsed));
                slider.value = clamped;
                numberInput.value = clamped;
                value.textContent = `${clamped}`;
                setPlatformOptionValue(platform.id, option.id, clamped);
            };

            slider.addEventListener("input", () => update(slider.value));
            numberInput.addEventListener("input", () => update(numberInput.value));

            row.appendChild(slider);
            row.appendChild(numberInput);
            row.appendChild(value);
            field.appendChild(row);
            return field;
        }

        const input = document.createElement("input");
        input.type = "number";
        input.min = min;
        input.max = max;
        input.step = step;
        input.value = initial;
        if (platform.normalizedId === "itunes" && option.id === "art_resolution") {
            input.id = "autotag-itunes-art-resolution";
        }
        input.addEventListener("input", () => {
            const parsed = Number.parseFloat(input.value);
            setPlatformOptionValue(platform.id, option.id, Number.isNaN(parsed) ? fallback : parsed);
        });
        field.appendChild(input);
        return field;
    }

    function createSelectPlatformOptionField(platform, option) {
        const field = document.createElement("div");
        field.className = "form-group";
        field.appendChild(createPlatformOptionLabel(option));
        const input = document.createElement("select");
        (option.value?.values || []).forEach((value) => {
            const opt = document.createElement("option");
            opt.value = value;
            opt.textContent = value;
            input.appendChild(opt);
        });
        input.value = state.config.custom[platform.id][option.id];
        input.addEventListener("change", () => {
            setPlatformOptionValue(platform.id, option.id, input.value);
        });
        field.appendChild(input);
        return field;
    }

    function createTextPlatformOptionField(platform, option) {
        const field = document.createElement("div");
        field.className = "form-group";
        field.appendChild(createPlatformOptionLabel(option));
        const input = document.createElement("input");
        input.type = option.value?.hidden ? "password" : "text";
        input.value = state.config.custom[platform.id][option.id] ?? "";
        input.addEventListener("input", () => {
            setPlatformOptionValue(platform.id, option.id, input.value);
        });
        field.appendChild(input);
        return field;
    }

    function createPlatformOptionField(platform, option) {
        const optionType = option.value?.type;
        if (optionType === "boolean") {
            return createBooleanPlatformOptionField(platform, option);
        }
        if (optionType === "number") {
            return createNumberPlatformOptionField(platform, option);
        }
        if (optionType === "option") {
            return createSelectPlatformOptionField(platform, option);
        }
        return createTextPlatformOptionField(platform, option);
    }

    function renderPlatformOptions() {
        const container = el("autotag-platform-options");
        if (!container) {
            return;
        }
        container.innerHTML = "";

        const selected = new Set((state.config.platforms || []).map((id) => normalizePlatformId(id)));
        const platformsForConfig = state.platforms
            .map((platform) => ({
                id: platform.id,
                normalizedId: normalizePlatformId(platform.id),
                name: platform.name,
                description: platform.description || "",
                icon: platform.icon || "",
                fallbackIcon: platform.fallbackIcon || "",
                options: platform.customOptions?.options || []
            }))
            .filter((platform) => platform.options.length > 0)
            .sort((a, b) => {
                const aOrder = TECHNICAL_PLATFORM_ORDER_INDEX[a.normalizedId];
                const bOrder = TECHNICAL_PLATFORM_ORDER_INDEX[b.normalizedId];
                const aRank = Number.isFinite(aOrder) ? aOrder : Number.MAX_SAFE_INTEGER;
                const bRank = Number.isFinite(bOrder) ? bOrder : Number.MAX_SAFE_INTEGER;
                if (aRank !== bRank) {
                    return aRank - bRank;
                }
                return a.name.localeCompare(b.name, undefined, { sensitivity: "base" });
            });

        if (platformsForConfig.length === 0) {
            const empty = document.createElement("div");
            empty.className = "helper";
            empty.textContent = "No platform-specific preferences are exposed by the current platform adapters.";
            container.appendChild(empty);
            return;
        }

        platformsForConfig.forEach((platform) => {
            ensurePlatformOptionDefaults(platform.id, platform.options);
            const isEnabled = selected.has(platform.normalizedId);

            const section = document.createElement("section");
            section.className = "autotag-platform-option-block";
            section.dataset.platformId = platform.id;
            section.classList.toggle("is-enabled", isEnabled);
            section.classList.toggle("is-disabled", !isEnabled);

            const header = document.createElement("div");
            header.className = "autotag-platform-option-header";

            const headerMain = document.createElement("div");
            headerMain.className = "autotag-platform-option-header-main";

            const iconEl = document.createElement("img");
            iconEl.className = "autotag-platform-option-icon";
            iconEl.alt = `${platform.name} icon`;
            const primaryIcon = platform.icon || platform.fallbackIcon || "";
            const fallbackIcon = platform.fallbackIcon || "";
            if (primaryIcon) {
                iconEl.src = primaryIcon;
                let usedFallbackIcon = false;
                iconEl.addEventListener("error", () => {
                    if (!usedFallbackIcon && fallbackIcon && fallbackIcon !== primaryIcon) {
                        usedFallbackIcon = true;
                        iconEl.src = fallbackIcon;
                        return;
                    }
                    iconEl.style.opacity = "0.45";
                });
            } else {
                iconEl.style.opacity = "0.35";
            }
            headerMain.appendChild(iconEl);

            const titleWrap = document.createElement("div");
            titleWrap.className = "autotag-platform-option-title-wrap";
            const heading = document.createElement("h6");
            heading.textContent = platform.name;
            titleWrap.appendChild(heading);

            const subtitle = document.createElement("div");
            subtitle.className = "autotag-platform-option-subtitle";
            const extraOptionCount = platform.normalizedId === "shazam" ? 1 : 0;
            const optionCount = platform.options.length + extraOptionCount;
            subtitle.textContent = `${platform.id} • ${optionCount} ${optionCount === 1 ? "setting" : "settings"}`;
            titleWrap.appendChild(subtitle);

            headerMain.appendChild(titleWrap);
            header.appendChild(headerMain);

            const badges = document.createElement("div");
            badges.className = "autotag-platform-badges";

            const enabledBadge = document.createElement("span");
            enabledBadge.className = `autotag-platform-badge ${isEnabled ? "is-enabled" : "is-disabled"}`;
            enabledBadge.textContent = isEnabled ? "Enabled" : "Disabled";
            badges.appendChild(enabledBadge);

            header.appendChild(badges);
            section.appendChild(header);

            if (platform.description) {
                const description = document.createElement("div");
                description.className = "helper";
                description.textContent = platform.description;
                section.appendChild(description);
            }

            if (platform.normalizedId === "itunes") {
                const itunesNote = document.createElement("div");
                itunesNote.className = "helper";
                itunesNote.textContent = "Uses iTunes metadata/artwork. Lyrics require an active Apple Music subscription.";
                section.appendChild(itunesNote);
            }

            const optionsLabel = document.createElement("div");
            optionsLabel.className = "autotag-platform-option-label";
            optionsLabel.textContent = "Settings";
            section.appendChild(optionsLabel);

            const optionsFields = document.createElement("div");
            optionsFields.className = "autotag-platform-option-fields";

            if (platform.normalizedId === "shazam") {
                const conflictField = document.createElement("div");
                conflictField.className = "form-group";

                const conflictWrapper = document.createElement("div");
                conflictWrapper.className = "checkbox-group";

                const conflictInput = document.createElement("input");
                conflictInput.type = "checkbox";
                conflictInput.id = "conflictShazamGate";
                conflictInput.value = "shazam";
                conflictInput.checked = String(state.config.conflictResolution || "").trim().toLowerCase() === "shazam";
                conflictInput.addEventListener("change", () => {
                    state.config.conflictResolution = conflictInput.checked ? "shazam" : null;
                });

                const conflictLabel = document.createElement("span");
                conflictLabel.textContent = "Use Shazam for conflicting metadata";
                conflictWrapper.appendChild(conflictInput);
                conflictWrapper.appendChild(conflictLabel);
                const conflictTooltip = document.createElement("span");
                conflictTooltip.className = "autotag-tooltip-icon";
                conflictTooltip.title = "If Shazam cannot recognize a file, original metadata is retained and the file is marked failed/conflict.";
                conflictTooltip.setAttribute("aria-label", conflictTooltip.title);
                conflictTooltip.innerHTML = '<i class="fas fa-question-circle"></i>';
                conflictWrapper.appendChild(conflictTooltip);
                conflictField.appendChild(conflictWrapper);
                optionsFields.appendChild(conflictField);
            }

            platform.options.forEach((option) => {
                optionsFields.appendChild(createPlatformOptionField(platform, option));
            });

            if (optionsFields.children.length === 0) {
                const noOptions = document.createElement("div");
                noOptions.className = "autotag-platform-no-options";
                noOptions.textContent = "No settings are exposed for this platform.";
                section.appendChild(noOptions);
                container.appendChild(section);
                return;
            }

            section.appendChild(optionsFields);
            container.appendChild(section);
        });
        updateCoverMaintenanceTargetResolutionPolicyUI();
    }

    function loadConfigToUI() {
        ensureCustomDefaults();
        ensureEnhancementDefaults();
        renderSuccessLibraryOptions();
        renderEnhancementFolderOptions();
        renderPlatforms();
        setDownloadTagSource(state.config.downloadTagSource || "engine");
        syncEnhancementTagsWithDownloadAndEnrichment();
        renderTags("autotag-tags", state.config.tags || [], "tags");
        refreshDownloadTagsForSource();
        renderTags("autotag-overwrite-tags", state.config.overwriteTags, "overwriteTags");
        updateDownloadSourceAvailability();

        const setChecked = (id, value) => {
            const field = el(id);
            if (field) {
                field.checked = value;
            }
        };
        const setValue = (id, value) => {
            const field = el(id);
            if (field) {
                field.value = value ?? "";
            }
        };

        setChecked("autotag-overwrite", state.config.overwrite);
        setChecked("autotag-id3v24", state.config.id3v24);
        setChecked("autotag-short-title", state.config.shortTitle);
        setChecked("autotag-album-art-file", state.config.albumArtFile);
        setChecked("autotag-merge-genres", state.config.mergeGenres);
        setChecked("autotag-camelot", state.config.camelot);
        if (el("autotag-only-year")) {
            setChecked("autotag-only-year", state.config.onlyYear);
        }
        if (el("autotag-multiplatform")) {
            setChecked("autotag-multiplatform", state.config.multiplatform);
        }
        if (el("autotag-parse-filename")) {
            setChecked("autotag-parse-filename", state.config.parseFilename);
        }
        if (el("autotag-filename-template")) {
            setValue("autotag-filename-template", state.config.filenameTemplate || "%artists% - %title%");
        }
        setValue("autotag-title-regex", state.config.titleRegex || "");
        // Manual custom intake path is intentionally disabled.
        // if (el("autotag-custom-path")) {
        //     const defaultIntakePath = String(state.settingsCache?.downloadLocation || "").trim();
        //     setValue("autotag-custom-path", state.config.customPath || defaultIntakePath);
        // }
        // Playlist intake is intentionally disabled for AutoTag manual runs.
        // if (el("autotag-playlist-path")) {
        //     setValue("autotag-playlist-path", state.config.playlistPath || "");
        // }
        if (el("autotag-multiple-matches")) {
            setValue("autotag-multiple-matches", state.config.multipleMatches);
        }
        if (el("autotag-track-zeroes")) {
            setValue("autotag-track-zeroes", state.config.trackNumberLeadingZeroes);
        }
        setValue("autotag-id3-lang", state.config.id3CommLang || "");
        setValue("autotag-sep-id3", state.config.separators.id3 || ", ");
        setValue("autotag-sep-mp4", state.config.separators.mp4 || ", ");
        setValue("autotag-sep-vorbis", state.config.separators.vorbis || "");
        setValue("autotag-styles-options", state.config.stylesOptions);
        setValue("autotag-styles-id3", state.config.stylesCustomTag.id3);
        setValue("autotag-styles-vorbis", state.config.stylesCustomTag.vorbis);
        setValue("autotag-styles-mp4", state.config.stylesCustomTag.mp4);
        setChecked("autotag-move-success", state.config.moveSuccess);
        if (!Number.isFinite(state.config.moveSuccessLibraryFolderId)) {
            state.config.moveSuccessLibraryFolderId = null;
        }
        if (state.config.moveSuccessLibraryFolderId == null && state.config.moveSuccessPath) {
            const inferredLibrary = getSuccessLibraryByPath(state.config.moveSuccessPath);
            if (inferredLibrary) {
                state.config.moveSuccessLibraryFolderId = inferredLibrary.id;
            }
        }
        setValue(
            "autotag-move-success-library",
            state.config.moveSuccessLibraryFolderId == null ? "" : String(state.config.moveSuccessLibraryFolderId)
        );
        setChecked("autotag-move-failed", state.config.moveFailed);
        setValue("autotag-move-failed-path", state.config.moveFailedPath || "");
        setChecked("autotag-organizer-dryrun", state.config.organizer?.dryRun);
        setValue("autotag-untaggable-path", state.config.organizer?.moveUntaggedPath || "");
        setChecked("enforceFolderStructure", state.config.enhancement.folderUniformity.enforceFolderStructure);
        setChecked("moveMisplacedFiles", state.config.enhancement.folderUniformity.moveMisplacedFiles);
        setChecked("mergeIntoExistingDestinationFolders", state.config.enhancement.folderUniformity.mergeIntoExistingDestinationFolders);
        setChecked("renameFilesToTemplate", state.config.enhancement.folderUniformity.renameFilesToTemplate);
        setChecked("removeEmptyFolders", state.config.enhancement.folderUniformity.removeEmptyFolders);
        setChecked("resolveSameTrackQualityConflicts", state.config.enhancement.folderUniformity.resolveSameTrackQualityConflicts);
        setChecked("keepBothOnUnresolvedConflicts", state.config.enhancement.folderUniformity.keepBothOnUnresolvedConflicts);
        setChecked("folderUniformityOnlyMoveWhenTagged", state.config.enhancement.folderUniformity.onlyMoveWhenTagged);
        setChecked("folderUniformityOnlyFullTrackSets", state.config.enhancement.folderUniformity.onlyReorganizeAlbumsWithFullTrackSets);
        setChecked("folderUniformitySkipCompilationFolders", state.config.enhancement.folderUniformity.skipCompilationFolders);
        setChecked("folderUniformitySkipVariousArtistsFolders", state.config.enhancement.folderUniformity.skipVariousArtistsFolders);
        setChecked("folderUniformityGenerateReport", state.config.enhancement.folderUniformity.generateReconciliationReport);
        setChecked("folderUniformityUseShazamForUntaggedFiles", state.config.enhancement.folderUniformity.useShazamForUntaggedFiles);
        setValue("folderUniformityDuplicateConflictPolicy", state.config.enhancement.folderUniformity.duplicateConflictPolicy || "keep_best");
        setValue("folderUniformityArtworkPolicy", state.config.enhancement.folderUniformity.artworkPolicy || "preserve_existing");
        setValue("folderUniformityLyricsPolicy", state.config.enhancement.folderUniformity.lyricsPolicy || "merge");
        setChecked("runFolderUniformityDedupe", state.config.enhancement.folderUniformity.runDedupe);
        setChecked("folderUniformityUseShazamForDedupe", state.config.enhancement.folderUniformity.useShazamForDedupe);
        setValue("folderUniformityDuplicatesFolderName", state.config.enhancement.folderUniformity.duplicatesFolderName || "%duplicates%");
        updateFolderUniformityFolderSummary(state.config.enhancement.folderUniformity.folderIds ?? []);
        setChecked("replaceMissingCovers", state.config.enhancement.coverMaintenance.replaceMissingEmbeddedCovers);
        setChecked("syncExternalCovers", state.config.enhancement.coverMaintenance.syncExternalCovers);
        setChecked("queueAnimatedArtwork", state.config.enhancement.coverMaintenance.queueAnimatedArtwork);
        updateCoverMaintenanceFolderSummary(state.config.enhancement.coverMaintenance.folderIds ?? []);
        setValue("coverWorkerCount", state.config.enhancement.coverMaintenance.workerCount ?? 8);
        setChecked("flagDuplicates", state.config.enhancement.qualityChecks.flagDuplicates);
        setChecked("flagMissingTags", state.config.enhancement.qualityChecks.flagMissingTags);
        setChecked("flagMismatchedMetadata", state.config.enhancement.qualityChecks.flagMismatchedMetadata);
        setChecked("qualityChecksUseShazamForDedupe", state.config.enhancement.qualityChecks.useShazamForDedupe);
        setValue("qualityChecksDuplicatesFolderName", state.config.enhancement.qualityChecks.duplicatesFolderName || "%duplicates%");
        updateQualityChecksFolderSummary(state.config.enhancement.qualityChecks.folderIds ?? []);
        state.config.enhancement.qualityChecks.scope = "all";
        state.config.enhancement.qualityChecks.technicalProfiles = normalizeTechnicalProfiles(
            state.config.enhancement.qualityChecks.technicalProfiles
        );
        setChecked("enhancementQueueAtmosAlternatives", state.config.enhancement.qualityChecks.queueAtmosAlternatives);
        setChecked("enhancementQueueLyricsRefresh", state.config.enhancement.qualityChecks.queueLyricsRefresh);
        state.config.enhancement.qualityChecks.cooldownMinutes = null;
        renderEnhancementTechnicalProfilesCatalog();
        void refreshEnhancementTechnicalProfiles();
        setChecked("autotag-write-lrc", state.config.writeLrc);
        setChecked("autotag-enhanced-lrc", state.config.enhancedLrc);
        setChecked("autotag-capitalize-genres", state.config.capitalizeGenres);

        updateConditionalSections();
        renderPlatformOptions();
        loadItunesArtOptions();
        storeSelectedPlatforms();
    }

    function loadStoredPreferences() {
        try {
            const stored = localStorage.getItem(AUTOTAG_PREFERENCES_KEY);
            if (!stored) {
                return null;
            }
            return JSON.parse(stored);
        } catch (error) {
            console.warn("Failed to load AutoTag preferences", error);
            return null;
        }
    }

    function normalizeStoredProfileId(value) {
        const normalized = typeof value === "string" ? value.trim() : "";
        return normalized.length > 0 ? normalized : null;
    }

    function loadStoredActiveProfileId() {
        try {
            const stored = normalizeStoredProfileId(localStorage.getItem(AUTOTAG_ACTIVE_PROFILE_KEY));
            if (stored) {
                return stored;
            }

            return normalizeStoredProfileId(globalThis.__userPrefsData?.autoTagActiveProfileId);
        } catch (error) {
            console.warn("Failed to load active AutoTag profile selection", error);
            return normalizeStoredProfileId(globalThis.__userPrefsData?.autoTagActiveProfileId);
        }
    }

    function persistActiveProfileId(profileId) {
        try {
            if (profileId) {
                localStorage.setItem(AUTOTAG_ACTIVE_PROFILE_KEY, profileId);
            } else {
                localStorage.removeItem(AUTOTAG_ACTIVE_PROFILE_KEY);
            }
            if (globalThis.UserPrefs) {
                globalThis.UserPrefs.set('autoTagActiveProfileId', profileId || null);
            }
        } catch (error) {
            console.warn("Failed to persist active AutoTag profile selection", error);
        }
    }

    function getProfileById(profileId) {
        const id = typeof profileId === "string" ? profileId.trim() : "";
        if (!id) {
            return null;
        }
        return state.profiles.find((profile) => String(profile?.id || "").toLowerCase() === id.toLowerCase()) || null;
    }

    function getActiveProfile() {
        return getProfileById(state.activeProfileId);
    }

    function getActiveProfileName() {
        const profile = getActiveProfile();
        if (profile?.name) {
            return profile.name;
        }
        const inputName = el("autotag-profile-name")?.value?.trim();
        return inputName || null;
    }

    function formatCoverMaintenancePolicySummary() {
        ensureEnhancementDefaults();
        const policy = resolveCoverMaintenancePolicyFromTechnical(readArtworkSettingsFromUI(state.settingsCache || {}));
        const targetPolicy = policy.targetPolicy;
        if (targetPolicy.mode === "platform" && targetPolicy.platformName) {
            return `Cover target: inherit ${targetPolicy.platformName} art_resolution (${targetPolicy.resolution}px).`;
        }

        return `Cover target: manual ${targetPolicy.resolution}px.`;
    }

    function updateActiveProfileSummary() {
        const summaryField = el("autotag-profile-summary");
        if (!summaryField) {
            return;
        }

        const activeProfile = getActiveProfile();
        if (!state.activeProfileId || !activeProfile) {
            summaryField.textContent = "No active profile loaded.";
            return;
        }

        const profileName = activeProfile.name || getActiveProfileName() || "Active profile";
        summaryField.textContent = `${profileName} • ${formatCoverMaintenancePolicySummary()}`;
    }

    function hasLoadedActiveProfile() {
        return !!(state.activeProfileId && getActiveProfile());
    }

    function ensureLoadedActiveProfile() {
        if (hasLoadedActiveProfile()) {
            return true;
        }

        const selectedProfile = getSelectedProfile();
        if (selectedProfile && applyLoadedProfile(selectedProfile)) {
            return true;
        }

        if (Array.isArray(state.profiles) && state.profiles.length > 0) {
            return restoreActiveProfileSelection();
        }

        return false;
    }

    function activateProfilesTab() {
        const profilesTab = el("autotag-profiles-tab");
        if (!profilesTab) {
            return;
        }
        if (profilesTab.classList.contains("active")) {
            return;
        }
        if (globalThis.bootstrap?.Tab) {
            new bootstrap.Tab(profilesTab).show();
            return;
        }
        profilesTab.click();
    }

    function getActiveAutoTagTabTarget() {
        const activeTab = document.querySelector("#autotagTabs .nav-link.active");
        return String(activeTab?.dataset?.bsTarget || "").trim() || null;
    }

    function keepAutoTagTabVisible(tab) {
        if (!(tab instanceof HTMLElement) || typeof tab.scrollIntoView !== "function") {
            return;
        }
        if (typeof globalThis.matchMedia === "function" && !globalThis.matchMedia("(max-width: 992px)").matches) {
            return;
        }
        tab.scrollIntoView({
            behavior: "smooth",
            block: "nearest",
            inline: "center"
        });
    }

    function findAutoTagTabByTarget(targetSelector) {
        const normalized = String(targetSelector || "").trim();
        if (!normalized) {
            return null;
        }

        const tabs = Array.from(document.querySelectorAll("#autotagTabs .nav-link"));
        return tabs.find((tab) => tab?.dataset?.bsTarget === normalized) || null;
    }

    function activateAutoTagTabByTarget(targetSelector) {
        const tab = findAutoTagTabByTarget(targetSelector);
        if (!(tab instanceof HTMLButtonElement) || tab.disabled) {
            return false;
        }

        if (tab.classList.contains("active")) {
            return true;
        }

        if (globalThis.bootstrap?.Tab) {
            new bootstrap.Tab(tab).show();
            return true;
        }

        tab.click();
        return true;
    }

    function guardProfileLockedTabNavigation(eventTarget, event) {
        if (!(eventTarget instanceof HTMLElement)) {
            return false;
        }

        const tab = eventTarget.closest("#autotagTabs .nav-link");
        if (!(tab instanceof HTMLButtonElement)) {
            return false;
        }

        const isProfilesTab = tab.id === "autotag-profiles-tab";
        if (isProfilesTab || ensureLoadedActiveProfile()) {
            return false;
        }

        event.preventDefault();
        event.stopPropagation();
        activateProfilesTab();
        if (state.profilesLoaded && (!Array.isArray(state.profiles) || state.profiles.length === 0)) {
            showToast("Create and load a profile to unlock the other tabs.", "warning");
        }
        return true;
    }

    function bindProfileTabNavigationGuards() {
        const tabs = el("autotagTabs");
        if (!tabs) {
            return;
        }

        tabs.addEventListener("click", (event) => {
            guardProfileLockedTabNavigation(event.target, event);
        });

        tabs.addEventListener("show.bs.tab", (event) => {
            guardProfileLockedTabNavigation(event.target, event);
        });

        tabs.addEventListener("shown.bs.tab", (event) => {
            keepAutoTagTabVisible(event.target);
        });
    }

    function applyProfileSelectionGuards() {
        const hasActiveProfile = ensureLoadedActiveProfile();
        document.querySelectorAll("#autotagTabs .nav-link").forEach((tab) => {
            const isProfilesTab = tab.id === "autotag-profiles-tab";
            const disabled = !hasActiveProfile && !isProfilesTab;
            tab.disabled = disabled;
            tab.classList.toggle("disabled", disabled);
            tab.setAttribute("aria-disabled", disabled ? "true" : "false");
            if (disabled) {
                tab.title = "Select and load a profile to unlock this tab.";
            } else {
                tab.removeAttribute("title");
            }
        });

        if (!hasActiveProfile) {
            const activeTarget = getActiveAutoTagTabTarget();
            if (activeTarget && activeTarget !== "#autotag-profiles-panel") {
                state.lockedTabTarget = activeTarget;
            }
            activateProfilesTab();
            return;
        }

        if (state.lockedTabTarget && state.lockedTabTarget !== "#autotag-profiles-panel") {
            activateAutoTagTabByTarget(state.lockedTabTarget);
        }
        state.lockedTabTarget = null;
    }

    function clearProfileAutoSaveState() {
        if (state.profileSaveTimer) {
            clearTimeout(state.profileSaveTimer);
            state.profileSaveTimer = null;
        }
        state.profileSaveInFlight = false;
        state.profileSaveQueued = false;
        state.profileSaveDirty = false;
    }

    function isProfileAutoSaveTarget(target) {
        if (!(target instanceof HTMLInputElement || target instanceof HTMLSelectElement || target instanceof HTMLTextAreaElement)) {
            return false;
        }
        if (!target.closest("#autotagTabsContent")) {
            return false;
        }
        if (target.closest("#autotag-folders-panel")) {
            return false;
        }
        return target.id !== "autotag-profile-select";
    }

    async function flushProfileAutoSave() {
        if (!state.activeProfileId || !state.profileSaveDirty) {
            return;
        }
        if (state.profileSaveInFlight) {
            state.profileSaveQueued = true;
            return;
        }
        state.profileSaveInFlight = true;
        state.profileSaveDirty = false;
        if (state.profileSaveTimer) {
            clearTimeout(state.profileSaveTimer);
            state.profileSaveTimer = null;
        }
        try {
            await upsertProfileFromUi({ silent: true, requireActiveProfile: true });
        } catch (error) {
            state.profileSaveDirty = true;
            console.warn("Auto-saving active AutoTag profile failed", error);
        } finally {
            state.profileSaveInFlight = false;
            if (state.profileSaveQueued) {
                state.profileSaveQueued = false;
                scheduleProfileAutoSave();
            }
        }
    }

    function scheduleProfileAutoSave() {
        if (!state.activeProfileId) {
            return;
        }
        state.profileSaveDirty = true;
        if (state.profileSaveTimer) {
            clearTimeout(state.profileSaveTimer);
        }
        state.profileSaveTimer = setTimeout(() => {
            flushProfileAutoSave();
        }, PROFILE_AUTOSAVE_DEBOUNCE_MS);
    }

    function setActiveProfileId(profileId, options = {}) {
        const { persist = true } = options;
        const normalized = typeof profileId === "string" && profileId.trim().length > 0
            ? profileId.trim()
            : null;
        if (!normalized) {
            clearProfileAutoSaveState();
        }
        state.activeProfileId = normalized;
        if (persist) {
            persistActiveProfileId(normalized);
        }
        applyProfileSelectionGuards();
        updateActiveProfileSummary();
    }

    function applyLoadedProfile(profile) {
        if (!profile) {
            return false;
        }
        const profileId = profile.id || profile.Id || "";
        const select = el("autotag-profile-select");
        if (select) {
            select.value = profileId;
        }
        const autoTagData = profile.autoTag?.data
            || profile.autoTag
            || profile.AutoTag?.data
            || profile.AutoTag
            || {};
        applyProfileConfig(autoTagData);
        applyFolderStructureToUI(profile.folderStructure || profile.FolderStructure);
        applyTechnicalSettingsToUI(profile.technical || profile.Technical);
        const nameInput = el("autotag-profile-name");
        if (nameInput) {
            nameInput.value = profile.name || profile.Name || "";
        }
        setActiveProfileId(profileId || null);
        return true;
    }

    function restoreActiveProfileSelection() {
        const storedProfileId = normalizeStoredProfileId(state.activeProfileId) || loadStoredActiveProfileId();
        const profile = storedProfileId ? getProfileById(storedProfileId) : null;
        if (profile) {
            return applyLoadedProfile(profile);
        }

        const select = el("autotag-profile-select");
        if (select) {
            select.value = "";
        }

        // Do not silently load a different profile.
        // Rule: only the last loaded profile is auto-restored.
        // If it cannot be loaded, keep tabs locked and stay on the first tab (Profiles).
        if (!storedProfileId) {
            state.activeProfileId = null;
            clearProfileAutoSaveState();
            updateActiveProfileSummary();
        }

        return false;
    }

    async function savePreferences() {
        const activeProfileName = getActiveProfileName();
        if (!activeProfileName) {
            showToast("Select and load a profile before saving preferences.", "warning");
            return;
        }
        const config = readConfigFromUI();
        state.config = config;
        try {
            if (state.profileSaveTimer) {
                clearTimeout(state.profileSaveTimer);
                state.profileSaveTimer = null;
            }
            state.profileSaveDirty = false;
            await saveLyricsSettings();
            await upsertProfileFromUi({ silent: true, requireActiveProfile: true });
            try {
                localStorage.setItem(AUTOTAG_PREFERENCES_KEY, JSON.stringify(config));
                if (globalThis.UserPrefs) globalThis.UserPrefs.set('autoTagPreferences', config);
            } catch (storageError) {
                console.warn("Failed to persist AutoTag preference cache", storageError);
            }
            showToast(`Preferences saved for profile "${activeProfileName}".`, "success");
        } catch (error) {
            console.error("Failed to save AutoTag preferences.", error);
            showToast(`Failed to save preferences for profile "${activeProfileName}".`, "error");
        }
    }

    function applyProfileConfig(config) {
        const merged = structuredClone(DEFAULT_CONFIG);
        Object.assign(merged, config || {});
        if (!merged.separators) {
            merged.separators = structuredClone(DEFAULT_CONFIG.separators);
        }
        if (!merged.stylesCustomTag) {
            merged.stylesCustomTag = structuredClone(DEFAULT_CONFIG.stylesCustomTag);
        }
        if (!merged.custom) {
            merged.custom = {};
        }
        delete merged.moveSuccessMode;
        const moveSuccessLibraryId = Number.parseInt(String(merged.moveSuccessLibraryFolderId ?? ""), 10);
        merged.moveSuccessLibraryFolderId = Number.isFinite(moveSuccessLibraryId) ? moveSuccessLibraryId : null;
        ensureEffectivePlatforms(merged);
        state.config = merged;
        ensureCustomDefaults();
        ensurePlatformCustomDefaults();
        syncEnhancementTagsWithDownloadAndEnrichment();
        loadConfigToUI();
    }

    function storeSelectedPlatforms() {
        try {
            localStorage.setItem(AUTOTAG_SELECTED_PLATFORMS_KEY, JSON.stringify(state.config.platforms || []));
            if (globalThis.UserPrefs) globalThis.UserPrefs.set('autoTagSelectedPlatforms', state.config.platforms || []);
            globalThis.dispatchEvent(new CustomEvent("autotagPlatformsChanged", {
                detail: { platforms: state.config.platforms || [] }
            }));
        } catch (error) {
            console.warn("Failed to persist AutoTag platform selection", error);
        }
    }

    function isTagSupported(tag) {
        if (!state.platforms.length) {
            return true;
        }
        if (tag === "metaTags") {
            return true;
        }
        if (isHiddenSpotifyAudioFeatureTag(tag)) {
            return false;
        }
        return state.platforms.some((platform) =>
            state.config.platforms.includes(platform.id) &&
            Array.isArray(platform.supportedTags) &&
            platform.supportedTags.includes(tag)
        );
    }

    function isHiddenSpotifyAudioFeatureTag(tag) {
        return HIDDEN_SPOTIFY_AUDIO_FEATURE_TAG_SET.has(String(tag || "").toLowerCase());
    }

    async function loadPlatforms() {
        try {
            const response = await fetch("/api/autotag/platforms");
            if (!response.ok) {
                throw new Error("Failed to load platforms");
            }
            const data = await response.json();
            state.platforms = (data || []).map((platform) => {
                const platformInfo = platform.platform || {};
                const supportedTags = platform.supportedTags || platformInfo.supportedTags || [];
                const downloadTags = platform.downloadTags || platformInfo.downloadTags || [];
                const platformId = platform.id || platformInfo.id;
                const overrideIcon = platformId === "deezer" ? "/images/icons/deezer.png" : null;
                return {
                    id: platformId,
                    name: platformInfo.name || platform.id,
                    description: platformInfo.description || "",
                    maxThreads: platformInfo.maxThreads ?? 0,
                    requiresAuth: platformInfo.requiresAuth ?? false,
                    supportedTags,
                    downloadTags,
                    customOptions: platformInfo.customOptions || { options: [] },
                    icon: overrideIcon || platform.icon || "",
                    fallbackIcon: platformId ? `/images/icons/${platformId}.png` : ""
                };
            });
            preloadPlatformIcons(state.platforms);
        } catch (error) {
            console.warn("Failed to load AutoTag platforms", error);
            state.platforms = [];
        }
    }

    function hasSpotifyAuthFromPlatformState() {
        const spotify = state.platformAuth?.spotify;
        if (!spotify || typeof spotify !== "object") {
            return false;
        }

        const hasCookieAuth = Boolean(
            String(spotify.webPlayerSpDc || "").trim()
            && String(spotify.webPlayerSpKey || "").trim()
        );
        if (hasCookieAuth) {
            return true;
        }

        const activeAccount = String(spotify.activeAccount || "").trim();
        const accounts = Array.isArray(spotify.accounts) ? spotify.accounts : [];
        if (activeAccount && accounts.length > 0) {
            const active = accounts.find((account) =>
                String(account?.name || "").trim().toLowerCase() === activeAccount.toLowerCase());
            if (active) {
                const hasBlobPath = Boolean(
                    String(active.blobPath || "").trim()
                    || String(active.webPlayerBlobPath || "").trim()
                    || String(active.librespotBlobPath || "").trim()
                );
                if (hasBlobPath) {
                    return true;
                }
            }
        }

        return Boolean(
            String(spotify.clientId || "").trim()
            && String(spotify.clientSecret || "").trim()
        );
    }

    function buildAuthFetchOptions() {
        return {
            cache: "no-store",
            credentials: "same-origin",
            headers: {
                Accept: "application/json"
            }
        };
    }

    function parseSettledJson(result) {
        if (result.status !== "fulfilled" || !result.value.ok) {
            return null;
        }

        return result.value.json();
    }

    function accountHasAnyBlob(account) {
        if (!account || typeof account !== "object") {
            return false;
        }

        return Boolean(
            String(account.blobPath || "").trim()
            || String(account.webPlayerBlobPath || "").trim()
            || String(account.librespotBlobPath || "").trim()
        );
    }

    function resolveAccountsConnected(accountsData, statusData) {
        const accounts = Array.isArray(accountsData?.accounts) ? accountsData.accounts : [];
        if (accounts.length === 0) {
            return false;
        }

        const activeFromAccounts = String(accountsData?.activeAccount || "").trim();
        const activeFromStatus = String(statusData?.activeAccount || "").trim();
        const activeName = activeFromAccounts || activeFromStatus;
        if (activeName) {
            const active = accounts.find((account) =>
                String(account?.name || "").trim().toLowerCase() === activeName.toLowerCase());
            if (active) {
                return accountHasAnyBlob(active);
            }
        }

        return accounts.some((account) => accountHasAnyBlob(account));
    }

    function resolveStatusConnected(statusData) {
        if (!statusData || typeof statusData !== "object") {
            return false;
        }

        const probeOk = statusData.ok === true
            || statusData.webPlayerOk === true
            || statusData.librespotOk === true;
        const hasBlob = Boolean(
            String(statusData.webPlayerBlobPath || "").trim()
            || String(statusData.librespotBlobPath || "").trim()
        );
        return probeOk && hasBlob;
    }

    async function loadSpotifyStatus() {
        try {
            const fetchOptions = buildAuthFetchOptions();
            const [configResult, accountsResult, statusResult] = await Promise.allSettled([
                fetch("/api/spotify-credentials/config", fetchOptions),
                fetch("/api/spotify-credentials/accounts", fetchOptions),
                fetch("/api/spotify-credentials/status", fetchOptions)
            ]);

            let config = null;
            let accountsData = null;
            let statusData = null;

            config = await parseSettledJson(configResult);
            accountsData = await parseSettledJson(accountsResult);
            statusData = await parseSettledJson(statusResult);

            if (config) {
                state.config.spotify = {
                    clientId: config.clientId || null,
                    clientSecret: config.clientSecret || null
                };
            }

            const hasConfig = Boolean(config && (
                config.hasConfig === true
                || (config.clientId && config.clientSecretSaved)
            ));
            const hasAccounts = resolveAccountsConnected(accountsData, statusData);
            const hasStatus = resolveStatusConnected(statusData);
            const hasPlatformAuth = hasSpotifyAuthFromPlatformState();
            const activeAccountFromPlatform = String(state.platformAuth?.spotify?.activeAccount || "").trim() || null;
            const activeAccountFromAccounts = String(accountsData?.activeAccount || "").trim() || null;
            const activeAccountFromStatus = String(statusData?.activeAccount || "").trim() || null;
            const activeAccount = activeAccountFromAccounts || activeAccountFromStatus || activeAccountFromPlatform;

            state.spotifyStatus = {
                connected: hasStatus || hasAccounts || hasConfig || hasPlatformAuth,
                activeAccount
            };
        } catch (error) {
            console.warn("Failed to load Spotify config status", error);
        } finally {
            state.authReady = true;
        }
    }

    async function applyProjectSpotifyConfig(config) {
        try {
            const response = await fetch("/api/spotify-credentials/config", buildAuthFetchOptions());
            if (!response.ok) {
                return;
            }
            const data = await response.json();
            config.spotify = {
                clientId: data.clientId || null,
                clientSecret: data.clientSecret || null
            };
        } catch (error) {
            console.warn("Failed to load project Spotify credentials", error);
        }
    }

    async function loadStoredAuth() {
        try {
            const response = await fetch("/api/platform-auth", buildAuthFetchOptions());
            if (!response.ok) {
                return null;
            }

            const data = await response.json();
            state.platformAuth = data || {};
            return data;
        } catch (error) {
            console.warn("Failed to load platform auth state", error);
            state.platformAuth = {};
            return null;
        }
    }

    function isPlatformConnected(platformKey) {
        const key = String(platformKey || "").toLowerCase().replaceAll(/\s+/g, "");
        const auth = state.platformAuth || {};
        if (key === "discogs") {
            return Boolean(auth.discogs?.token);
        }
        if (key === "bpmsupreme") {
            return Boolean(auth.bpmSupreme?.email && auth.bpmSupreme?.password);
        }
        if (key === "lastfm") {
            return Boolean(auth.lastFm?.hasApiKey || auth.lastFm?.apiKey);
        }
        if (key === "applemusic") {
            return Boolean(auth.appleMusic?.wrapperReady);
        }
        if (key === "itunes") {
            return Boolean(auth.appleMusic?.wrapperReady);
        }
        if (key === "plex") {
            return Boolean(auth.plex?.url && auth.plex?.token);
        }
        if (key === "jellyfin") {
            return Boolean(auth.jellyfin?.url && (auth.jellyfin?.apiKey || auth.jellyfin?.username));
        }
        return false;
    }

    function isPlatformAuthenticated(platformId) {
        const key = String(platformId || "").toLowerCase().replaceAll(/\s+/g, "");
        if (!key) {
            return false;
        }
        if (key === "spotify") {
            return state.spotifyStatus.connected === true;
        }
        return isPlatformConnected(key);
    }

    function canEnablePlatform(platform) {
        if (!platform?.requiresAuth) {
            return true;
        }
        if (!state.authReady) {
            return false;
        }
        return isPlatformAuthenticated(platform.id);
    }

    function mergeStoredAuth(config, data) {
        if (!data) {
            return;
        }

        if (data.spotify) {
            const currentSpotify = config.spotify || {};
            config.spotify = {
                clientId: currentSpotify.clientId || data.spotify.clientId || null,
                clientSecret: currentSpotify.clientSecret || data.spotify.clientSecret || null
            };
        }
    }

    function normalizeSpotifyConfig(config) {
        const spotify = config.spotify;
        if (!spotify?.clientId || !spotify?.clientSecret) {
            config.spotify = null;
        }
    }

    function readConfigFromUI() {
        ensureCustomDefaults();
        ensurePlatformCustomDefaults();
        ensureEnhancementDefaults();
        state.config.tags = getCheckedTags("tags");
        state.config.downloadTags = normalizeDownloadTags(getCheckedTags("downloadTags"));
        syncEnhancementTagsWithDownloadAndEnrichment();
        state.config.overwriteTags = getCheckedTags("overwriteTags");

        const getChecked = (id, fallback = false) => el(id)?.checked ?? fallback;
        const getValue = (id, fallback = "") => el(id)?.value ?? fallback;

        state.config.overwrite = getChecked("autotag-overwrite", state.config.overwrite);
        state.config.id3v24 = getChecked("autotag-id3v24", state.config.id3v24);
        state.config.shortTitle = getChecked("autotag-short-title", state.config.shortTitle);
        state.config.albumArtFile = getChecked("autotag-album-art-file", state.config.albumArtFile);
        state.config.mergeGenres = getChecked("autotag-merge-genres", state.config.mergeGenres);
        state.config.camelot = getChecked("autotag-camelot", state.config.camelot);
        state.config.skipTagged = el("autotag-skip-tagged")
            ? getChecked("autotag-skip-tagged", state.config.skipTagged)
            : DEFAULT_CONFIG.skipTagged;
        if (el("autotag-only-year")) {
            state.config.onlyYear = getChecked("autotag-only-year", state.config.onlyYear);
        }
        if (el("autotag-multiplatform")) {
            state.config.multiplatform = getChecked("autotag-multiplatform", state.config.multiplatform);
        }
        state.config.includeSubfolders = el("autotag-include-subfolders")
            ? getChecked("autotag-include-subfolders", state.config.includeSubfolders)
            : DEFAULT_CONFIG.includeSubfolders;
        if (el("autotag-parse-filename")) {
            state.config.parseFilename = getChecked("autotag-parse-filename", state.config.parseFilename);
        }
        if (el("autotag-filename-template")) {
            state.config.filenameTemplate = getValue("autotag-filename-template", state.config.filenameTemplate || "%artists% - %title%").trim();
        }
        state.config.titleRegex = getValue("autotag-title-regex", state.config.titleRegex || "").trim() || null;
        // Manual custom intake path is intentionally disabled.
        // state.config.customPath = el("autotag-custom-path")
        //     ? getValue("autotag-custom-path", state.config.customPath || "").trim() || null
        //     : null;
        state.config.customPath = null;
        // Playlist intake is intentionally disabled for AutoTag manual runs.
        // if (el("autotag-playlist-path")) {
        //     state.config.playlistPath = getValue("autotag-playlist-path", state.config.playlistPath || "").trim() || null;
        // }
        if (el("autotag-multiple-matches")) {
            state.config.multipleMatches = getValue("autotag-multiple-matches", state.config.multipleMatches);
        }
        if (el("autotag-track-zeroes")) {
            state.config.trackNumberLeadingZeroes = Number.parseInt(getValue("autotag-track-zeroes", state.config.trackNumberLeadingZeroes), 10) || 0;
        }
        state.config.id3CommLang = getValue("autotag-id3-lang", state.config.id3CommLang || "").trim() || null;
        state.config.separators.id3 = getValue("autotag-sep-id3", state.config.separators.id3 || ", ") || ", ";
        state.config.separators.mp4 = getValue("autotag-sep-mp4", state.config.separators.mp4 || ", ") || ", ";
        state.config.separators.vorbis = getValue("autotag-sep-vorbis", state.config.separators.vorbis || "") || null;
        state.config.stylesOptions = getValue("autotag-styles-options", state.config.stylesOptions);
        state.config.stylesCustomTag = {
            id3: getValue("autotag-styles-id3", state.config.stylesCustomTag?.id3 || "STYLE") || "STYLE",
            vorbis: getValue("autotag-styles-vorbis", state.config.stylesCustomTag?.vorbis || "STYLE") || "STYLE",
            mp4: getValue("autotag-styles-mp4", state.config.stylesCustomTag?.mp4 || "STYLE") || "STYLE"
        };
        state.config.conflictResolution = getChecked(
            "conflictShazamGate",
            String(state.config.conflictResolution || "").toLowerCase() === "shazam"
        )
            ? "shazam"
            : null;
        state.config.moveSuccess = getChecked("autotag-move-success", state.config.moveSuccess);
        const moveSuccessLibraryRaw = getValue("autotag-move-success-library", state.config.moveSuccessLibraryFolderId ?? "");
        const moveSuccessLibraryIdParsed = Number.parseInt(String(moveSuccessLibraryRaw || "").trim(), 10);
        const moveSuccessLibraryFolderId = Number.isFinite(moveSuccessLibraryIdParsed) ? moveSuccessLibraryIdParsed : null;
        const selectedLibrary = getSuccessLibraryById(moveSuccessLibraryFolderId);
        delete state.config.moveSuccessMode;
        state.config.moveSuccessLibraryFolderId = selectedLibrary?.id ?? null;
        state.config.moveSuccessPath = selectedLibrary?.rootPath || null;
        state.config.moveFailed = getChecked("autotag-move-failed", state.config.moveFailed);
        state.config.moveFailedPath = getValue("autotag-move-failed-path", state.config.moveFailedPath || "").trim() || null;
        if (!state.config.organizer) {
            state.config.organizer = {};
        }
        state.config.organizer.onlyMoveWhenTagged = el("autotag-only-move-tagged")
            ? getChecked("autotag-only-move-tagged", state.config.organizer.onlyMoveWhenTagged)
            : false;
        state.config.organizer.dryRun = getChecked("autotag-organizer-dryrun", state.config.organizer.dryRun);
        state.config.organizer.moveUntaggedPath = el("autotag-untaggable-path")
            ? getValue("autotag-untaggable-path", state.config.organizer.moveUntaggedPath || "").trim() || null
            : null;
        delete state.config.organizer.preferredExtensions;
        const folderUniformity = state.config.enhancement.folderUniformity;
        folderUniformity.folderIds = parseFolderIdList(getValue("enhancementFolderUniformityFolder", (folderUniformity.folderIds ?? []).join(",")));
        folderUniformity.enforceFolderStructure = getChecked("enforceFolderStructure", folderUniformity.enforceFolderStructure);
        folderUniformity.moveMisplacedFiles = getChecked("moveMisplacedFiles", folderUniformity.moveMisplacedFiles);
        folderUniformity.mergeIntoExistingDestinationFolders = getChecked("mergeIntoExistingDestinationFolders", folderUniformity.mergeIntoExistingDestinationFolders);
        folderUniformity.renameFilesToTemplate = getChecked("renameFilesToTemplate", folderUniformity.renameFilesToTemplate);
        folderUniformity.removeEmptyFolders = getChecked("removeEmptyFolders", folderUniformity.removeEmptyFolders);
        folderUniformity.resolveSameTrackQualityConflicts = getChecked("resolveSameTrackQualityConflicts", folderUniformity.resolveSameTrackQualityConflicts);
        folderUniformity.keepBothOnUnresolvedConflicts = getChecked("keepBothOnUnresolvedConflicts", folderUniformity.keepBothOnUnresolvedConflicts);
        folderUniformity.onlyMoveWhenTagged = getChecked("folderUniformityOnlyMoveWhenTagged", folderUniformity.onlyMoveWhenTagged);
        folderUniformity.onlyReorganizeAlbumsWithFullTrackSets = getChecked("folderUniformityOnlyFullTrackSets", folderUniformity.onlyReorganizeAlbumsWithFullTrackSets);
        folderUniformity.skipCompilationFolders = getChecked("folderUniformitySkipCompilationFolders", folderUniformity.skipCompilationFolders);
        folderUniformity.skipVariousArtistsFolders = getChecked("folderUniformitySkipVariousArtistsFolders", folderUniformity.skipVariousArtistsFolders);
        folderUniformity.generateReconciliationReport = getChecked("folderUniformityGenerateReport", folderUniformity.generateReconciliationReport);
        folderUniformity.useShazamForUntaggedFiles = getChecked("folderUniformityUseShazamForUntaggedFiles", folderUniformity.useShazamForUntaggedFiles);
        folderUniformity.duplicateConflictPolicy = getValue("folderUniformityDuplicateConflictPolicy", folderUniformity.duplicateConflictPolicy || "keep_best").trim() || "keep_best";
        folderUniformity.artworkPolicy = getValue("folderUniformityArtworkPolicy", folderUniformity.artworkPolicy || "preserve_existing").trim() || "preserve_existing";
        folderUniformity.lyricsPolicy = getValue("folderUniformityLyricsPolicy", folderUniformity.lyricsPolicy || "merge").trim() || "merge";
        folderUniformity.runDedupe = getChecked("runFolderUniformityDedupe", folderUniformity.runDedupe);
        folderUniformity.useShazamForDedupe = getChecked("folderUniformityUseShazamForDedupe", folderUniformity.useShazamForDedupe);
        folderUniformity.duplicatesFolderName = getValue(
            "folderUniformityDuplicatesFolderName",
            folderUniformity.duplicatesFolderName || "%duplicates%"
        ).trim() || "%duplicates%";
        delete folderUniformity.preferredExtensions;

        const coverMaintenance = state.config.enhancement.coverMaintenance;
        coverMaintenance.folderIds = parseFolderIdList(getValue("enhancementCoverFolder", (coverMaintenance.folderIds ?? []).join(",")));
        coverMaintenance.minResolution = Math.max(100, Math.min(2000, Number.parseInt(String(coverMaintenance.minResolution ?? 500), 10) || 500));
        coverMaintenance.replaceMissingEmbeddedCovers = getChecked("replaceMissingCovers", coverMaintenance.replaceMissingEmbeddedCovers);
        coverMaintenance.syncExternalCovers = getChecked("syncExternalCovers", coverMaintenance.syncExternalCovers);
        coverMaintenance.queueAnimatedArtwork = getChecked("queueAnimatedArtwork", coverMaintenance.queueAnimatedArtwork);
        coverMaintenance.workerCount = Number.parseInt(getValue("coverWorkerCount", coverMaintenance.workerCount ?? 8), 10);
        if (!Number.isFinite(coverMaintenance.workerCount)) {
            coverMaintenance.workerCount = 8;
        }
        coverMaintenance.workerCount = Math.max(1, Math.min(32, coverMaintenance.workerCount));

        const qualityChecks = state.config.enhancement.qualityChecks;
        qualityChecks.folderIds = parseFolderIdList(getValue("enhancementQualityFolder", (qualityChecks.folderIds ?? []).join(",")));
        qualityChecks.scope = "all";
        qualityChecks.queueAtmosAlternatives = getChecked("enhancementQueueAtmosAlternatives", qualityChecks.queueAtmosAlternatives);
        qualityChecks.queueLyricsRefresh = getChecked("enhancementQueueLyricsRefresh", qualityChecks.queueLyricsRefresh);
        qualityChecks.flagDuplicates = getChecked("flagDuplicates", qualityChecks.flagDuplicates);
        qualityChecks.flagMissingTags = getChecked("flagMissingTags", qualityChecks.flagMissingTags);
        qualityChecks.flagMismatchedMetadata = getChecked("flagMismatchedMetadata", qualityChecks.flagMismatchedMetadata);
        qualityChecks.useDuplicatesFolder = true;
        qualityChecks.useShazamForDedupe = getChecked("qualityChecksUseShazamForDedupe", qualityChecks.useShazamForDedupe);
        qualityChecks.duplicatesFolderName = getValue(
            "qualityChecksDuplicatesFolderName",
            qualityChecks.duplicatesFolderName || "%duplicates%"
        ).trim() || "%duplicates%";
        syncSelectedTechnicalProfilesFromUI();
        qualityChecks.technicalProfiles = normalizeTechnicalProfiles(qualityChecks.technicalProfiles);
        qualityChecks.cooldownMinutes = null;
        state.config.writeLrc = getChecked("autotag-write-lrc", state.config.writeLrc);
        state.config.enhancedLrc = getChecked("autotag-enhanced-lrc", state.config.enhancedLrc);
        state.config.capitalizeGenres = getChecked("autotag-capitalize-genres", state.config.capitalizeGenres);
        state.config.custom.itunes.art_resolution = Number.parseInt(getValue("autotag-itunes-art-resolution", state.config.custom?.itunes?.art_resolution || 1000), 10) || 1000;
        state.config.custom.itunes.animated_artwork = getAnimatedArtworkValue(
            state.config.custom?.itunes?.animated_artwork ?? state.settingsCache?.saveAnimatedArtwork ?? false
        );
        state.config.downloadTagSource = normalizeDownloadTagSource(getDownloadTagSource() || state.config.downloadTagSource || "engine");
        ensureEffectivePlatforms(state.config);

        return state.config;
    }

    function setEnhancementStatus(elementId, message) {
        const target = el(elementId);
        if (target) {
            target.textContent = message;
        }
    }

    function saveFolderUniformityStatusSnapshot(status) {
        if (!status || typeof status !== "object") {
            return;
        }

        const snapshot = {
            jobId: String(status.jobId || "").trim(),
            status: String(status.status || "running").trim().toLowerCase(),
            phase: String(status.phase || "").trim(),
            percentComplete: Number(status.percentComplete ?? 0),
            completedSteps: Number(status.completedSteps ?? 0),
            totalSteps: Number(status.totalSteps ?? 0),
            foldersProcessed: Number(status.foldersProcessed ?? 0),
            foldersSkipped: Number(status.foldersSkipped ?? 0),
            totalFolders: Number(status.totalFolders ?? 0),
            currentArtistFolder: String(status.currentArtistFolder || "").trim(),
            artistFoldersProcessed: Number(status.artistFoldersProcessed ?? 0),
            artistFoldersTotal: Number(status.artistFoldersTotal ?? 0),
            updatedAtUtc: new Date().toISOString()
        };

        try {
            localStorage.setItem(AUTOTAG_FOLDER_UNIFORMITY_STATUS_SNAPSHOT_KEY, JSON.stringify(snapshot));
        } catch (error) {
            console.debug("Unable to persist folder uniformity status snapshot.", error);
        }
    }

    function clearFolderUniformityStatusSnapshot() {
        try {
            localStorage.removeItem(AUTOTAG_FOLDER_UNIFORMITY_STATUS_SNAPSHOT_KEY);
        } catch (error) {
            console.debug("Unable to clear folder uniformity status snapshot.", error);
        }
    }

    function loadFolderUniformityStatusSnapshot() {
        try {
            const raw = localStorage.getItem(AUTOTAG_FOLDER_UNIFORMITY_STATUS_SNAPSHOT_KEY);
            if (!raw) {
                return null;
            }

            const parsed = JSON.parse(raw);
            if (!parsed || typeof parsed !== "object") {
                return null;
            }

            return parsed;
        } catch (error) {
            console.debug("Unable to load folder uniformity status snapshot.", error);
            return null;
        }
    }

    function restoreFolderUniformityStatusSnapshot() {
        const snapshot = loadFolderUniformityStatusSnapshot();
        if (!snapshot) {
            return false;
        }

        const message = buildFolderUniformityProgressMessage(snapshot);
        if (!String(message || "").trim()) {
            return false;
        }

        setEnhancementStatus("folderUniformityStatus", message);
        return true;
    }

    function persistFolderUniformityLastScan(summary) {
        const normalizedSummary = String(summary || "").trim();
        if (!normalizedSummary) {
            return;
        }

        const entry = {
            summary: normalizedSummary,
            completedAtUtc: new Date().toISOString()
        };

        try {
            localStorage.setItem(AUTOTAG_FOLDER_UNIFORMITY_LAST_SCAN_KEY, JSON.stringify(entry));
        } catch (error) {
            console.debug("Unable to persist folder uniformity last scan message.", error);
        }
    }

    function loadFolderUniformityLastScan() {
        try {
            const raw = localStorage.getItem(AUTOTAG_FOLDER_UNIFORMITY_LAST_SCAN_KEY);
            if (!raw) {
                return null;
            }

            const parsed = JSON.parse(raw);
            if (!parsed || typeof parsed !== "object") {
                return null;
            }

            return parsed;
        } catch (error) {
            console.debug("Unable to load folder uniformity last scan message.", error);
            return null;
        }
    }

    function clearFolderUniformityLastScanTimer() {
        if (state.folderUniformityLastScanTimer) {
            clearTimeout(state.folderUniformityLastScanTimer);
            state.folderUniformityLastScanTimer = null;
        }
    }

    function showFolderUniformityLastScanBanner() {
        if (getActiveFolderUniformityJobId()) {
            return;
        }

        const entry = loadFolderUniformityLastScan();
        if (!entry) {
            return;
        }

        const summary = String(entry.summary || "").trim();
        if (!summary) {
            return;
        }

        const completedAt = new Date(String(entry.completedAtUtc || ""));
        const completedAtLabel = Number.isNaN(completedAt.getTime())
            ? "unknown time"
            : completedAt.toLocaleString();
        const message = `Last scan: ${completedAtLabel} • ${summary}`;
        setEnhancementStatus("folderUniformityStatus", message);

        clearFolderUniformityLastScanTimer();
        state.folderUniformityLastScanTimer = setTimeout(() => {
            state.folderUniformityLastScanTimer = null;
            if (getActiveFolderUniformityJobId()) {
                return;
            }

            const target = el("folderUniformityStatus");
            if (target && String(target.textContent || "").trim() === message) {
                target.textContent = "";
            }
        }, ENHANCEMENT_LAST_SCAN_BANNER_MS);
    }

    function isEnhancementTabActive() {
        const panel = el("autotag-stage3-panel");
        return !!(panel && panel.classList.contains("active") && panel.classList.contains("show"));
    }

    function bindEnhancementTabLastScanBanner() {
        const enhancementTab = el("autotag-stage3-tab");
        if (!enhancementTab) {
            return;
        }

        enhancementTab.addEventListener("shown.bs.tab", () => {
            if (getActiveFolderUniformityJobId()) {
                clearFolderUniformityLastScanTimer();
                restoreFolderUniformityStatusSnapshot();
                void resumeFolderUniformityRunIfNeeded();
                return;
            }

            showFolderUniformityLastScanBanner();
        });
    }

    function createUniformityReportStat(label, value) {
        const stat = document.createElement("div");
        stat.className = "uniformity-report-stat";

        const statLabel = document.createElement("span");
        statLabel.className = "uniformity-report-stat-label";
        statLabel.textContent = label;

        const statValue = document.createElement("span");
        statValue.className = "uniformity-report-stat-value";
        statValue.textContent = String(value ?? 0);

        stat.appendChild(statLabel);
        stat.appendChild(statValue);
        return stat;
    }

    function renderFolderUniformityReports(reports) {
        const container = el("folderUniformityReports");
        if (!container) {
            return;
        }

        container.innerHTML = "";
        if (!Array.isArray(reports) || reports.length === 0) {
            container.classList.add("d-none");
            return;
        }

        const orderedStats = [
            ["Candidate files", "candidateFiles"],
            ["Planned moves", "plannedMoves"],
            ["Moved files", "movedFiles"],
            ["Replaced duplicates", "replacedDuplicates"],
            ["Quarantined duplicates", "quarantinedDuplicates"],
            ["Skipped conflicts", "skippedConflicts"]
        ];

        reports.forEach((report) => {
            const card = document.createElement("article");
            card.className = "uniformity-report-card";

            const header = document.createElement("div");
            header.className = "uniformity-report-header";

            const titleWrap = document.createElement("div");
            const title = document.createElement("h6");
            title.className = "uniformity-report-title";
            title.textContent = String(report?.folderName || "Library folder");

            const subtitle = document.createElement("div");
            subtitle.className = "uniformity-report-subtitle";
            subtitle.textContent = `Folder ${String(report?.folderId ?? "-")}`;

            titleWrap.appendChild(title);
            titleWrap.appendChild(subtitle);

            const summary = document.createElement("div");
            summary.className = "uniformity-report-subtitle";
            summary.textContent = `${Number(report?.movedFolders ?? 0)} folders moved, ${Number(report?.movedSidecars ?? 0)} sidecars moved, ${Number(report?.mergedLyricsSidecars ?? 0)} lyrics merged`;

            header.appendChild(titleWrap);
            header.appendChild(summary);

            const statsGrid = document.createElement("div");
            statsGrid.className = "uniformity-report-stats";
            orderedStats.forEach(([label, key]) => {
                statsGrid.appendChild(createUniformityReportStat(label, report?.[key]));
            });

            card.appendChild(header);
            card.appendChild(statsGrid);

            const entries = Array.isArray(report?.entries)
                ? report.entries.filter((entry) => typeof entry === "string" && entry.trim().length > 0)
                : [];
            if (entries.length > 0) {
                const details = document.createElement("details");
                details.className = "uniformity-report-details";

                const detailsSummary = document.createElement("summary");
                detailsSummary.textContent = `View reconciliation log (${entries.length})`;

                const pre = document.createElement("pre");
                pre.className = "uniformity-report-entries";
                pre.textContent = entries.join("\n");

                details.appendChild(detailsSummary);
                details.appendChild(pre);
                card.appendChild(details);
            }

            container.appendChild(card);
        });

        container.classList.remove("d-none");
    }

    function setExternalStartStatus(message) {
        const target = el("autotag-external-start-status");
        if (target) {
            target.textContent = message || "";
            target.classList.toggle("d-none", !message);
        }
    }

    function buildFolderUniformityProgressMessage(status) {
        const phase = String(status?.phase || "Running folder uniformity...");
        const percent = Number(status?.percentComplete ?? 0);
        const completedSteps = Number(status?.completedSteps ?? 0);
        const totalSteps = Number(status?.totalSteps ?? 0);
        const folderProgress = `${Number(status?.foldersProcessed ?? 0) + Number(status?.foldersSkipped ?? 0)}/${Number(status?.totalFolders ?? 0)}`;
        const sourceFolderProcessed = Number(status?.artistFoldersProcessed ?? 0);
        const sourceFolderTotal = Number(status?.artistFoldersTotal ?? 0);
        const sourceFolder = String(status?.currentArtistFolder || "").trim();
        const planningPhase = phase.toLowerCase().includes("planning");
        const sourceProgressLabel = planningPhase ? "files" : "source folders";
        const sourceFolderProgress = sourceFolderTotal > 0
            ? ` - ${sourceProgressLabel} ${sourceFolderProcessed}/${sourceFolderTotal}`
            : "";
        const sourceFolderSuffix = sourceFolder.length > 0
            ? ` - current: ${sourceFolder}`
            : "";
        if (totalSteps > 0) {
            return `${phase} (${percent}%) - ${completedSteps}/${totalSteps} steps - ${folderProgress} folders${sourceFolderProgress}${sourceFolderSuffix}`;
        }

        return `${phase} (${percent}%) - ${folderProgress} folders${sourceFolderProgress}${sourceFolderSuffix}`;
    }

    function renderFolderUniformityLiveLog(status) {
        const container = el("folderUniformityLiveLogContainer");
        const pre = el("folderUniformityLiveLog");
        if (!container || !pre) {
            return;
        }

        const logs = Array.isArray(status?.logs)
            ? status.logs
                .map((line) => String(line || "").trim())
                .filter((line) => line.length > 0)
            : [];
        const errors = Array.isArray(status?.errors)
            ? status.errors
                .map((line) => String(line || "").trim())
                .filter((line) => line.length > 0)
            : [];
        const combined = [...logs];
        if (errors.length > 0) {
            combined.push(...errors.map((line) => `[error] ${line}`));
        }

        if (combined.length === 0) {
            pre.textContent = "";
            container.classList.add("d-none");
            return;
        }

        const tailCount = 28;
        pre.textContent = combined.slice(-tailCount).join("\n");
        container.classList.remove("d-none");
    }

    async function pollFolderUniformityStatus(jobId) {
        const id = String(jobId || "").trim();
        if (!id) {
            throw new Error("Folder uniformity job id was not provided by the server.");
        }

        let transientFailures = 0;
        let latestStatus = null;
        while (true) {
            try {
                const response = await fetch(`/api/autotag/enhancement/folder-uniformity/status?jobId=${encodeURIComponent(id)}`);
                if (!response.ok) {
                    const payload = await response.json().catch(() => null);
                    const message = payload?.error || payload?.message || `Request failed (${response.status})`;
                    throw new Error(message);
                }

                latestStatus = await response.json();
                transientFailures = 0;
                saveFolderUniformityStatusSnapshot(latestStatus);
                clearFolderUniformityLastScanTimer();
                setEnhancementStatus("folderUniformityStatus", buildFolderUniformityProgressMessage(latestStatus));
                renderFolderUniformityLiveLog(latestStatus);

                if (Array.isArray(latestStatus?.reconciliationReports)) {
                    renderFolderUniformityReports(latestStatus.reconciliationReports);
                }

                const runStatus = String(latestStatus?.status || "").toLowerCase();
                if (runStatus && runStatus !== "running") {
                    clearActiveFolderUniformityJob();
                    return latestStatus;
                }
            } catch (error) {
                transientFailures += 1;
                if (transientFailures >= 4) {
                    throw error;
                }
            }

            await new Promise((resolve) => setTimeout(resolve, 1500));
        }
    }

    function rememberActiveFolderUniformityJob(jobId) {
        const id = String(jobId || "").trim();
        if (!id) {
            return;
        }

        state.folderUniformityJobId = id;
        localStorage.setItem(AUTOTAG_FOLDER_UNIFORMITY_JOB_KEY, id);
    }

    function clearActiveFolderUniformityJob() {
        state.folderUniformityJobId = null;
        localStorage.removeItem(AUTOTAG_FOLDER_UNIFORMITY_JOB_KEY);
    }

    function getActiveFolderUniformityJobId() {
        const inMemory = String(state.folderUniformityJobId || "").trim();
        if (inMemory) {
            return inMemory;
        }

        return String(localStorage.getItem(AUTOTAG_FOLDER_UNIFORMITY_JOB_KEY) || "").trim();
    }

    function setFolderUniformityRunButtonDisabled(disabled) {
        const button = el("runFolderUniformity");
        if (button) {
            button.disabled = !!disabled;
        }
    }

    function applyFolderUniformityCompletion(payload, showNotification = true) {
        const foldersProcessed = Number(payload?.foldersProcessed ?? 0);
        const foldersSkipped = Number(payload?.foldersSkipped ?? 0);
        const dedupe = payload?.dedupe;
        const dedupeSummary = dedupe
            ? ` Dedupe: ${Number(dedupe.duplicatesFound ?? 0)} duplicates handled into ${String(dedupe.duplicatesFolderName || "%duplicates%")}.`
            : "";
        const reportSummary = Array.isArray(payload?.reconciliationReports) && payload.reconciliationReports.length > 0
            ? ` Report: ${payload.reconciliationReports.length} folder report(s) generated.`
            : "";
        const summary = payload?.skipped
            ? String(payload?.message || "Folder uniformity skipped.")
            : String(payload?.message || `Folder uniformity finished: ${foldersProcessed} processed, ${foldersSkipped} skipped.`) + dedupeSummary + reportSummary;
        clearFolderUniformityStatusSnapshot();
        persistFolderUniformityLastScan(summary);
        renderFolderUniformityReports(payload?.reconciliationReports);
        renderFolderUniformityLiveLog(payload);
        setEnhancementStatus("folderUniformityStatus", summary);
        const normalizedStatus = String(payload?.status || "").toLowerCase();
        const failed = normalizedStatus === "error" || normalizedStatus === "canceled" || payload?.success === false;
        if (showNotification) {
            showToast(summary, failed ? "warning" : "success");
        }
    }

    async function resumeFolderUniformityRunIfNeeded() {
        if (state.folderUniformityResumeInFlight) {
            return;
        }

        state.folderUniformityResumeInFlight = true;
        const jobId = getActiveFolderUniformityJobId();
        if (!jobId) {
            state.folderUniformityResumeInFlight = false;
            return;
        }

        try {
            restoreFolderUniformityStatusSnapshot();
            const response = await fetch(`/api/autotag/enhancement/folder-uniformity/status?jobId=${encodeURIComponent(jobId)}`);
            if (!response.ok) {
                if (response.status === 404) {
                    clearActiveFolderUniformityJob();
                    clearFolderUniformityStatusSnapshot();
                    if (isEnhancementTabActive()) {
                        showFolderUniformityLastScanBanner();
                    }
                }
                return;
            }

            const status = await response.json();
            saveFolderUniformityStatusSnapshot(status);
            renderFolderUniformityLiveLog(status);
            if (Array.isArray(status?.reconciliationReports)) {
                renderFolderUniformityReports(status.reconciliationReports);
            }

            const runStatus = String(status?.status || "").toLowerCase();
            if (runStatus && runStatus !== "running") {
                clearActiveFolderUniformityJob();
                applyFolderUniformityCompletion(status, false);
                setFolderUniformityRunButtonDisabled(false);
                return;
            }

            rememberActiveFolderUniformityJob(jobId);
            clearFolderUniformityLastScanTimer();
            setEnhancementStatus("folderUniformityStatus", buildFolderUniformityProgressMessage(status));
            setFolderUniformityRunButtonDisabled(true);
            const payload = await pollFolderUniformityStatus(jobId);
            applyFolderUniformityCompletion(payload, true);
        } catch (error) {
            // Keep the active job id so monitoring can resume on next focus/page load.
            console.debug("Folder uniformity monitoring failed.", error);
        } finally {
            setFolderUniformityRunButtonDisabled(false);
            state.folderUniformityResumeInFlight = false;
        }
    }

    async function runFolderUniformity() {
        const button = el("runFolderUniformity");
        if (button) {
            button.disabled = true;
        }

        try {
            const config = readConfigFromUI();
            const options = config.enhancement.folderUniformity;
            renderFolderUniformityReports([]);
            renderFolderUniformityLiveLog({ logs: [], errors: [] });
            setEnhancementStatus("folderUniformityStatus", "Starting folder uniformity run...");
            clearFolderUniformityLastScanTimer();
            saveFolderUniformityStatusSnapshot({
                jobId: getActiveFolderUniformityJobId(),
                status: "running",
                phase: "Starting folder uniformity run...",
                percentComplete: 0,
                completedSteps: 0,
                totalSteps: 0,
                foldersProcessed: 0,
                foldersSkipped: 0,
                totalFolders: 0
            });
            const startResponse = await fetch("/api/autotag/enhancement/folder-uniformity/start", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    folderIds: parseFolderIdList(options.folderIds),
                    enforceFolderStructure: options.enforceFolderStructure,
                    moveMisplacedFiles: options.moveMisplacedFiles,
                    mergeIntoExistingDestinationFolders: options.mergeIntoExistingDestinationFolders,
                    renameFilesToTemplate: options.renameFilesToTemplate,
                    removeEmptyFolders: options.removeEmptyFolders,
                    resolveSameTrackQualityConflicts: options.resolveSameTrackQualityConflicts,
                    keepBothOnUnresolvedConflicts: options.keepBothOnUnresolvedConflicts,
                    onlyMoveWhenTagged: options.onlyMoveWhenTagged,
                    onlyReorganizeAlbumsWithFullTrackSets: options.onlyReorganizeAlbumsWithFullTrackSets,
                    skipCompilationFolders: options.skipCompilationFolders,
                    skipVariousArtistsFolders: options.skipVariousArtistsFolders,
                    generateReconciliationReport: options.generateReconciliationReport,
                    useShazamForUntaggedFiles: options.useShazamForUntaggedFiles,
                    duplicateConflictPolicy: options.duplicateConflictPolicy,
                    artworkPolicy: options.artworkPolicy,
                    lyricsPolicy: options.lyricsPolicy,
                    runDedupe: options.runDedupe,
                    useShazamForDedupe: options.useShazamForDedupe,
                    duplicatesFolderName: options.duplicatesFolderName,
                    includeSubfolders: options.includeSubfolders !== false
                })
            });
            const startPayload = await startResponse.json().catch(() => null);
            if (!startResponse.ok) {
                const message = startPayload?.error || startPayload?.message || `Request failed (${startResponse.status})`;
                throw new Error(message);
            }

            const jobId = String(startPayload?.jobId || startPayload?.state?.jobId || "").trim();
            if (!jobId) {
                throw new Error("Folder uniformity job did not return a valid id.");
            }
            rememberActiveFolderUniformityJob(jobId);
            saveFolderUniformityStatusSnapshot({
                jobId,
                status: "running",
                phase: "Preparing scope",
                percentComplete: 0,
                completedSteps: 0,
                totalSteps: 0,
                foldersProcessed: 0,
                foldersSkipped: 0,
                totalFolders: 0
            });

            if (startPayload?.started === false) {
                setEnhancementStatus("folderUniformityStatus", "Folder uniformity is already running. Monitoring current progress...");
            }

            const payload = await pollFolderUniformityStatus(jobId);
            applyFolderUniformityCompletion(payload, true);
        } catch (error) {
            const message = `Folder uniformity failed: ${error?.message || error}`;
            setEnhancementStatus("folderUniformityStatus", message);
            clearFolderUniformityStatusSnapshot();
            showToast(message, "error");
        } finally {
            const activeJobId = getActiveFolderUniformityJobId();
            if (!activeJobId) {
                clearActiveFolderUniformityJob();
            }
            if (button) {
                button.disabled = false;
            }
        }
    }

    async function runEnhancementQualityChecks() {
        const button = el("runEnhancementQualityChecks");
        if (button) {
            button.disabled = true;
        }

        try {
            const config = readConfigFromUI();
            const checks = config.enhancement.qualityChecks;
            setEnhancementStatus("enhancementQualityChecksStatus", "Running selected quality checks...");
            const response = await fetch("/api/autotag/enhancement/quality-checks", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    folderIds: parseFolderIdList(checks.folderIds),
                    scope: checks.scope,
                    flagDuplicates: checks.flagDuplicates,
                    flagMissingTags: checks.flagMissingTags,
                    flagMismatchedMetadata: checks.flagMismatchedMetadata,
                    useDuplicatesFolder: true,
                    useShazamForDedupe: checks.useShazamForDedupe,
                    duplicatesFolderName: checks.duplicatesFolderName,
                    queueAtmosAlternatives: checks.queueAtmosAlternatives,
                    queueLyricsRefresh: checks.queueLyricsRefresh,
                    cooldownMinutes: checks.cooldownMinutes,
                    technicalProfiles: normalizeTechnicalProfiles(checks.technicalProfiles)
                })
            });
            const payload = await response.json().catch(() => null);
            if (!response.ok) {
                const message = payload?.error || payload?.message || `Request failed (${response.status})`;
                throw new Error(message);
            }

            let qualityStarted = "Quality Scanner skipped";
            if (payload?.qualityScanner?.requested) {
                qualityStarted = payload?.qualityScanner?.started
                    ? "Quality Scanner started"
                    : "Quality Scanner already running";
            }
            const duplicateSummary = payload?.duplicateCheck
                ? `Duplicates: ${Number(payload.duplicateCheck.duplicatesFound ?? 0)} found, ${Number(payload.duplicateCheck.deleted ?? 0)} moved to ${String(payload.duplicateCheck.duplicatesFolderName || "%duplicates%")}`
                : "Duplicate Cleaner skipped";
            let lyricsSummary = "Lyrics refresh skipped";
            if (payload?.lyricsRefresh) {
                if (payload.lyricsRefresh.disabledByTechnicalPreference) {
                    lyricsSummary = "Lyrics refresh skipped: enable Download Lyrics in Technical tab";
                } else {
                    lyricsSummary = `Lyrics: ${Number(payload.lyricsRefresh.enqueued ?? 0)} enqueued, ${Number(payload.lyricsRefresh.skipped ?? 0)} skipped`;
                }
            }
            const message = `${qualityStarted}. ${duplicateSummary}. ${lyricsSummary}.`;
            setEnhancementStatus("enhancementQualityChecksStatus", message);
            showToast("Enhancement quality checks submitted.", "success");
        } catch (error) {
            const message = `Enhancement quality checks failed: ${error?.message || error}`;
            setEnhancementStatus("enhancementQualityChecksStatus", message);
            showToast(message, "error");
        } finally {
            if (button) {
                button.disabled = false;
            }
        }
    }

    async function runCoverMaintenance() {
        const button = el("runCoverMaintenance");
        if (button) {
            button.disabled = true;
        }

        try {
            const config = readConfigFromUI();
            const options = config.enhancement.coverMaintenance;
            const artworkSettings = readArtworkSettingsFromUI(state.settingsCache || {});
            const policy = resolveCoverMaintenancePolicyFromTechnical(artworkSettings);
            const targetPolicy = policy.targetPolicy;
            const inheritedTargetResolution = targetPolicy.mode === "platform" ? targetPolicy.resolution : null;
            const requestSources = policy.sources.length > 0 ? policy.sources : null;
            const targetPolicyLabel = targetPolicy.mode === "platform" && targetPolicy.platformName
                ? `inherited from ${targetPolicy.platformName} art_resolution`
                : "manual target";
            const sourceLabel = requestSources ? requestSources.join(", ") : "default sources";
            setEnhancementStatus(
                "coverMaintenanceStatus",
                `Running cover maintenance (${targetPolicy.resolution}px, ${targetPolicyLabel}, sources: ${sourceLabel})...`
            );
            const response = await fetch("/api/cover-maintenance/run", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    folderIds: parseFolderIdList(options.folderIds),
                    includeSubfolders: config.includeSubfolders !== false,
                    workerCount: options.workerCount,
                    upgradeLowResolutionCovers: true,
                    minResolution: options.minResolution,
                    targetResolution: targetPolicy.resolution,
                    targetResolutionMode: "platform",
                    targetResolutionPlatform: targetPolicy.platformId,
                    inheritedTargetResolution,
                    sizeTolerancePercent: 25,
                    preserveSourceFormat: false,
                    replaceMissingEmbeddedCovers: options.replaceMissingEmbeddedCovers,
                    syncExternalCovers: options.syncExternalCovers,
                    queueAnimatedArtwork: options.queueAnimatedArtwork,
                    sources: requestSources
                })
            });
            const payload = await response.json().catch(() => null);
            if (!response.ok) {
                const message = payload?.error || payload?.message || `Request failed (${response.status})`;
                throw new Error(message);
            }

            const summary = payload?.message
                || `Cover maintenance finished: ${Number(payload?.albumsUpdated ?? 0)} updated, ${Number(payload?.albumsSkipped ?? 0)} skipped, ${Number(payload?.errors ?? 0)} errors.`;
            setEnhancementStatus("coverMaintenanceStatus", summary);
            showToast("Cover maintenance completed.", "success");
        } catch (error) {
            const message = `Cover maintenance failed: ${error?.message || error}`;
            setEnhancementStatus("coverMaintenanceStatus", message);
            showToast(message, "error");
        } finally {
            if (button) {
                button.disabled = false;
            }
        }
    }

    function readFolderStructureFromUI() {
        return {
            createPlaylistFolder: el("createPlaylistFolder")?.checked ?? false,
            playlistNameTemplate: el("playlistNameTemplate")?.value.trim() || "%playlist%",
            createArtistFolder: el("createArtistFolder")?.checked ?? false,
            artistNameTemplate: el("artistNameTemplate")?.value.trim() || "%artist%",
            createAlbumFolder: el("createAlbumFolder")?.checked ?? false,
            albumNameTemplate: el("albumNameTemplate")?.value.trim() || "%album%",
            createCDFolder: el("createCDFolder")?.checked ?? false,
            createStructurePlaylist: el("createStructurePlaylist")?.checked ?? false,
            createSingleFolder: el("createSingleFolder")?.checked ?? false,
            illegalCharacterReplacer: el("illegalCharacterReplacer")?.value ?? "_"
        };
    }

    function applyFolderStructureToUI(folderStructure) {
        const data = folderStructure || {};
        if (el("createPlaylistFolder")) {
            el("createPlaylistFolder").checked = !!data.createPlaylistFolder;
        }
        if (el("playlistNameTemplate")) {
            el("playlistNameTemplate").value = data.playlistNameTemplate || "%playlist%";
        }
        if (el("createArtistFolder")) {
            el("createArtistFolder").checked = data.createArtistFolder !== false;
        }
        if (el("artistNameTemplate")) {
            el("artistNameTemplate").value = data.artistNameTemplate || "%artist%";
        }
        if (el("createAlbumFolder")) {
            el("createAlbumFolder").checked = data.createAlbumFolder !== false;
        }
        if (el("albumNameTemplate")) {
            el("albumNameTemplate").value = data.albumNameTemplate || "%album%";
        }
        if (el("createCDFolder")) {
            el("createCDFolder").checked = !!data.createCDFolder;
        }
        if (el("createStructurePlaylist")) {
            el("createStructurePlaylist").checked = !!data.createStructurePlaylist;
        }
        if (el("createSingleFolder")) {
            el("createSingleFolder").checked = !!data.createSingleFolder;
        }
        if (el("illegalCharacterReplacer")) {
            el("illegalCharacterReplacer").value = data.illegalCharacterReplacer || "_";
        }
        updateFolderStructureVisibility();
        renderFolderStructurePreview();
    }

    function updateFolderStructureVisibility() {
        const playlistGroup = el("playlistNameGroup");
        const artistGroup = el("artistNameGroup");
        const albumGroup = el("albumNameGroup");
        if (playlistGroup && el("createPlaylistFolder")) {
            playlistGroup.style.display = el("createPlaylistFolder").checked ? "block" : "none";
        }
        if (artistGroup && el("createArtistFolder")) {
            artistGroup.style.display = el("createArtistFolder").checked ? "block" : "none";
        }
        if (albumGroup && el("createAlbumFolder")) {
            albumGroup.style.display = el("createAlbumFolder").checked ? "block" : "none";
        }
        renderFolderStructurePreview();
    }

    function renderTemplatePreview(template, fallback) {
        const rawTemplate = String(template || fallback || "").trim() || String(fallback || "");
        const illegalCharacterReplacer = (el("illegalCharacterReplacer")?.value || "_").charAt(0) || "_";
        const interpolated = rawTemplate.replaceAll(/%(\w+)%/g, (_, token) => {
            const key = String(token || "").toLowerCase();
            return FOLDER_PREVIEW_SAMPLES[key] ?? token;
        });
        return interpolated.replaceAll(/[\\/:*?"<>|]/g, illegalCharacterReplacer);
    }

    function appendFolderPreviewLine(tree, text, indent, isFile = false) {
        const line = document.createElement("div");
        line.className = "folder-preview-line";
        if (indent > 0 && indent <= 3) {
            line.classList.add(`indent-${indent}`);
        } else if (indent > 3) {
            line.style.paddingLeft = `${indent * 24}px`;
        }

        const icon = document.createElement("i");
        icon.className = isFile ? "fas fa-file-audio text-info" : "fas fa-folder text-warning";
        line.appendChild(icon);

        const value = document.createElement("span");
        value.textContent = text;
        line.appendChild(value);

        tree.appendChild(line);
    }

    function appendAlbumPathPreview(tree, options) {
        let albumIndent = 1;
        if (options.createArtistFolder) {
            appendFolderPreviewLine(tree, options.artistName, albumIndent++, false);
        }
        if (options.createAlbumFolder) {
            appendFolderPreviewLine(tree, options.albumName, albumIndent++, false);
        }
        if (options.createCDFolder) {
            appendFolderPreviewLine(tree, "CD1", albumIndent++, false);
        }
        appendFolderPreviewLine(tree, options.albumTrackName, albumIndent, true);
    }

    function appendPlaylistPathPreview(tree, options) {
        if (!options.createPlaylistFolder) {
            return;
        }

        appendFolderPreviewLine(tree, options.playlistName, 1, false);
        let playlistIndent = 2;
        if (options.createStructurePlaylist) {
            if (options.createArtistFolder) {
                appendFolderPreviewLine(tree, options.artistName, playlistIndent++, false);
            }
            if (options.createAlbumFolder) {
                appendFolderPreviewLine(tree, options.albumName, playlistIndent++, false);
            }
            if (options.createCDFolder) {
                appendFolderPreviewLine(tree, "CD1", playlistIndent++, false);
            }
        }
        appendFolderPreviewLine(tree, options.playlistTrackName, playlistIndent, true);
    }

    function appendSinglePathPreview(tree, options) {
        if (!options.createSingleFolder) {
            return;
        }

        appendFolderPreviewLine(tree, "Singles", 1, false);
        let singleIndent = 2;
        if (options.createArtistFolder) {
            appendFolderPreviewLine(tree, options.artistName, singleIndent++, false);
        }
        appendFolderPreviewLine(tree, options.defaultTrackName, singleIndent, true);
    }

    function renderFolderStructurePreview() {
        const tree = el("folderPreviewTree");
        if (!tree) {
            return;
        }

        tree.innerHTML = "";
        appendFolderPreviewLine(tree, "Library", 0, false);

        const previewOptions = {
            createArtistFolder: el("createArtistFolder")?.checked ?? true,
            createAlbumFolder: el("createAlbumFolder")?.checked ?? true,
            createPlaylistFolder: el("createPlaylistFolder")?.checked ?? false,
            createStructurePlaylist: el("createStructurePlaylist")?.checked ?? false,
            createCDFolder: el("createCDFolder")?.checked ?? false,
            createSingleFolder: el("createSingleFolder")?.checked ?? false,
            artistName: renderTemplatePreview(el("artistNameTemplate")?.value, "%artist%"),
            albumName: renderTemplatePreview(el("albumNameTemplate")?.value, "%album%"),
            playlistName: renderTemplatePreview(el("playlistNameTemplate")?.value, "%playlist%"),
            defaultTrackName: `${renderTemplatePreview(el("autotag-trackname-template")?.value, "%artist% - %title%")}.flac`,
            albumTrackName: `${renderTemplatePreview(el("autotag-album-trackname-template")?.value, "%tracknumber% - %title%")}.flac`,
            playlistTrackName: `${renderTemplatePreview(el("autotag-playlist-trackname-template")?.value, "%artist% - %title%")}.flac`
        };

        appendAlbumPathPreview(tree, previewOptions);
        appendPlaylistPathPreview(tree, previewOptions);
        appendSinglePathPreview(tree, previewOptions);
    }

    function getCheckedTags(name) {
        return Array.from(document.querySelectorAll(`input[name="${name}"]:checked`))
            .map((input) => input.dataset.tag)
            .filter(Boolean);
    }

    function syncFallbackSourceControls() {
        const lyricsFallbackEnabled = el("lyricsFallbackEnabled")?.checked === true;
        const lyricsFallbackOrderGroup = el("lyricsFallbackOrderGroup");
        if (lyricsFallbackOrderGroup) {
            lyricsFallbackOrderGroup.style.display = lyricsFallbackEnabled ? "block" : "none";
        }
        const lyricsDefaultSource = el("lyricsDefaultSource");
        if (lyricsDefaultSource) {
            const hasAvailableLyricsSource = lyricsDefaultSource.dataset.hasAvailableSources !== "0";
            lyricsDefaultSource.disabled = lyricsFallbackEnabled || !hasAvailableLyricsSource;
        }

        const artworkFallbackEnabled = el("artworkFallbackEnabled")?.checked === true;
        const artworkFallbackOrderGroup = el("artworkFallbackOrderGroup");
        if (artworkFallbackOrderGroup) {
            artworkFallbackOrderGroup.style.display = artworkFallbackEnabled ? "block" : "none";
        }
        const artworkDefaultSourceGroup = el("artworkDefaultSourceGroup");
        if (artworkDefaultSourceGroup) {
            artworkDefaultSourceGroup.classList.toggle("source-priority-default-compact", !artworkFallbackEnabled);
        }
        const artworkDefaultSource = el("artworkDefaultSource");
        if (artworkDefaultSource) {
            const hasAvailableArtworkSource = artworkDefaultSource.dataset.hasAvailableSources !== "0";
            artworkDefaultSource.disabled = artworkFallbackEnabled || !hasAvailableArtworkSource;
        }

        const artistArtworkFallbackEnabled = el("artistArtworkFallbackEnabled")?.checked === true;
        const artistArtworkFallbackOrderGroup = el("artistArtworkFallbackOrderGroup");
        if (artistArtworkFallbackOrderGroup) {
            artistArtworkFallbackOrderGroup.style.display = artistArtworkFallbackEnabled ? "block" : "none";
        }
        const artistArtworkDefaultSourceGroup = el("artistArtworkDefaultSourceGroup");
        if (artistArtworkDefaultSourceGroup) {
            artistArtworkDefaultSourceGroup.classList.toggle("source-priority-default-compact", !artistArtworkFallbackEnabled);
        }
        const artistArtworkDefaultSource = el("artistArtworkDefaultSource");
        if (artistArtworkDefaultSource) {
            const hasAvailableArtistArtworkSource = artistArtworkDefaultSource.dataset.hasAvailableSources !== "0";
            artistArtworkDefaultSource.disabled = artistArtworkFallbackEnabled || !hasAvailableArtistArtworkSource;
        }
    }

    function updateToggleGroupVisibility(groupId, toggleId, options = {}) {
        const group = el(groupId);
        const toggle = el(toggleId);
        const enabled = toggle?.checked === true;
        if (group && toggle) {
            group.style.display = enabled ? (options.enabledDisplay || "block") : "none";
        }

        const input = options.inputId ? el(options.inputId) : null;
        if (input) {
            input.disabled = !enabled;
        }

        return enabled;
    }

    function updateConditionalSections() {
        const overwriteTags = el("autotag-overwrite-tags-group");
        const overwrite = el("autotag-overwrite");
        if (overwriteTags && overwrite) {
            overwriteTags.style.display = overwrite.checked ? "none" : "block";
        }

        const stylesGroup = el("autotag-styles-custom-group");
        const stylesSelect = el("autotag-styles-options");
        if (stylesGroup && stylesSelect) {
            stylesGroup.style.display = stylesSelect.value === "customTag" ? "block" : "none";
        }

        updateToggleGroupVisibility("autotag-move-success-library-group", "autotag-move-success", {
            inputId: "autotag-move-success-library"
        });
        updateToggleGroupVisibility("autotag-move-failed-group", "autotag-move-failed", {
            inputId: "autotag-move-failed-path"
        });
        updateToggleGroupVisibility("autotag-enhanced-lrc-group", "autotag-write-lrc", {
            enabledDisplay: "flex"
        });
        updateToggleGroupVisibility("autotag-filename-template-group", "autotag-parse-filename");

        syncFallbackSourceControls();
        syncMultiArtistHandlingState();
    }

    function syncMultiArtistHandlingState() {
        const singleAlbumArtist = el("singleAlbumArtist");
        const useMainArtistFolders = singleAlbumArtist ? singleAlbumArtist.checked !== false : true;
        const dependentGroup = el("autotag-multi-artist-dependent");
        const behaviorHint = el("singleAlbumArtistBehaviorHint");
        const dependentFieldIds = ["multiArtistSeparator", "albumVariousArtists", "removeDuplicateArtists"];

        dependentFieldIds.forEach((fieldId) => {
            const field = el(fieldId);
            if (!field) {
                return;
            }
            field.disabled = useMainArtistFolders;
        });

        if (dependentGroup) {
            dependentGroup.classList.toggle("autotag-disabled-group", useMainArtistFolders);
        }

        if (behaviorHint) {
            behaviorHint.textContent = useMainArtistFolders
                ? "Enabled: Album Artist stays single (main artist), and folders use the main artist. Disable this to allow multi-artist handling."
                : "Disabled: multi-artist handling controls are active; Album Artist can include multiple artists.";
        }
    }

    function setupProviderOrderControl(options) {
        const dropdown = document.getElementById(options.dropdownId);
        const trigger = document.getElementById(options.triggerId);
        const menu = document.getElementById(options.menuId);
        const list = document.getElementById(options.listId);
        const input = document.getElementById(options.inputId);
        const sourceToggles = options.sourceToggleContainerId
            ? document.getElementById(options.sourceToggleContainerId)
            : null;

        if (!dropdown || !trigger || !menu || !list || !input) {
            return;
        }

        const allowedOrder = Array.isArray(options.allowedOrder) && options.allowedOrder.length > 0
            ? options.allowedOrder
            : [];
        const itemClassName = options.itemClassName || 'provider-order-item';
        const noSourcesText = options.noSourcesText || 'No enabled sources';
        const handleIconName = options.handleIconName || 'drag_indicator';
        const capabilityType = options.capabilityType || 'artwork';
        const labelMap = options.labelMap || {};
        const getLabel = typeof options.getLabel === 'function'
            ? options.getLabel
            : (value) => labelMap[value] || getSourceLabel(value);
        const syncDefaultSource = typeof options.syncDefaultSource === 'function'
            ? options.syncDefaultSource
            : null;

        trigger.addEventListener('click', (event) => {
            event.stopPropagation();
            menu.classList.toggle('show');
        });
        menu.addEventListener('click', (event) => event.stopPropagation());
        document.addEventListener('click', () => menu.classList.remove('show'));

        const createOrderItem = (value) => {
            const item = document.createElement('li');
            item.className = itemClassName;
            item.draggable = true;
            item.dataset.value = value;

            const label = document.createElement('span');
            label.textContent = getLabel(value);
            item.appendChild(label);

            const handle = document.createElement('span');
            handle.className = 'material-icons drag-handle';
            if (options.setHandleAriaHidden) {
                handle.setAttribute('aria-hidden', 'true');
            }
            handle.textContent = handleIconName;
            item.appendChild(handle);

            return item;
        };

        const getOrder = () => Array.from(list.querySelectorAll(`.${itemClassName}`))
            .map((item) => item.dataset.value || '')
            .filter((value) => allowedOrder.includes(value));

        const resolveAvailableOrder = () => getAvailableFallbackProviders(capabilityType, allowedOrder);
        const syncToggleChecks = (order) => {
            if (!sourceToggles) {
                return;
            }

            const selected = new Set(order);
            const availableSet = new Set(resolveAvailableOrder());
            const hasAvailabilityFilter = Array.isArray(state.platforms) && state.platforms.length > 0;
            sourceToggles.querySelectorAll('input[type="checkbox"][data-value]').forEach((checkbox) => {
                const value = checkbox.dataset.value || '';
                const available = !hasAvailabilityFilter || availableSet.has(value);
                checkbox.disabled = !available;
                if (checkbox.parentElement) {
                    checkbox.parentElement.style.display = available ? '' : 'none';
                }
                checkbox.checked = available && selected.has(value);
            });
        };

        const applyEmptyState = (availableOrder) => {
            list.innerHTML = '';
            input.value = '';
            trigger.textContent = noSourcesText;
            syncDefaultSource?.(availableOrder, input.value);
            syncToggleChecks([]);
        };

        const ensureSourceToggleOptions = () => {
            if (!sourceToggles) {
                return;
            }

            sourceToggles.innerHTML = '';
            allowedOrder.forEach((provider) => {
                const label = document.createElement('label');
                label.className = 'fallback-source-toggle';
                const checkbox = document.createElement('input');
                checkbox.type = 'checkbox';
                checkbox.dataset.value = provider;
                label.appendChild(checkbox);
                label.append(` ${getLabel(provider)}`);
                sourceToggles.appendChild(label);
            });
        };

        const updateValue = () => {
            const availableOrder = resolveAvailableOrder();
            const availableSet = new Set(availableOrder);
            const hasAvailabilityFilter = Array.isArray(state.platforms) && state.platforms.length > 0;

            if (hasAvailabilityFilter && availableOrder.length === 0) {
                applyEmptyState(availableOrder);
                return;
            }

            let order = getOrder().filter((value) => !hasAvailabilityFilter || availableSet.has(value));
            if (order.length === 0) {
                if (availableOrder.length > 0) {
                    order = [availableOrder[0]];
                } else if (allowedOrder.length > 0) {
                    order = [allowedOrder[0]];
                }
                list.innerHTML = '';
                if (order[0]) {
                    list.appendChild(createOrderItem(order[0]));
                }
            }

            input.value = order.join(',');
            trigger.textContent = order.map((value) => getLabel(value)).join(' → ');
            syncDefaultSource?.(availableOrder, input.value);
            syncToggleChecks(order);
        };

        let dragged = null;

        list.addEventListener('dragstart', (event) => {
            const target = event.target.closest(`.${itemClassName}`);
            if (!target) {
                return;
            }
            dragged = target;
            event.dataTransfer?.setData('text/plain', target.dataset.value || '');
            event.dataTransfer?.setDragImage(target, 10, 10);
            target.classList.add('dragging');
        });

        list.addEventListener('dragend', () => {
            if (dragged) {
                dragged.classList.remove('dragging');
                dragged = null;
            }
            updateValue();
        });

        list.addEventListener('dragover', (event) => {
            event.preventDefault();
            const target = event.target.closest(`.${itemClassName}`);
            if (!dragged || !target || dragged === target) {
                return;
            }

            const rect = target.getBoundingClientRect();
            const before = event.clientY < rect.top + rect.height / 2;
            list.insertBefore(dragged, before ? target : target.nextSibling);
        });

        const syncFromInput = () => {
            ensureSourceToggleOptions();
            const availableOrder = resolveAvailableOrder();
            const availableSet = new Set(availableOrder);
            const hasAvailabilityFilter = Array.isArray(state.platforms) && state.platforms.length > 0;

            if (hasAvailabilityFilter && availableOrder.length === 0) {
                applyEmptyState(availableOrder);
                return;
            }

            let order = normalizeProviderOrder(input.value || '', allowedOrder)
                .filter((value) => !hasAvailabilityFilter || availableSet.has(value));
            if (order.length === 0 && availableOrder.length > 0) {
                order = [availableOrder[0]];
            }
            input.value = order.join(',');
            list.innerHTML = '';
            order.forEach((value) => {
                list.appendChild(createOrderItem(value));
            });
            updateValue();
        };

        if (sourceToggles) {
            sourceToggles.addEventListener('change', (event) => {
                const target = event.target;
                if (!(target instanceof HTMLInputElement) || target.type !== 'checkbox' || target.disabled) {
                    return;
                }

                const value = String(target.dataset.value || '').trim().toLowerCase();
                if (!allowedOrder.includes(value)) {
                    return;
                }

                const existing = list.querySelector(`.${itemClassName}[data-value="${value}"]`);
                if (target.checked) {
                    if (!existing) {
                        list.appendChild(createOrderItem(value));
                    }
                    updateValue();
                    return;
                }

                const currentOrder = getOrder();
                if (currentOrder.length <= 1 && currentOrder.includes(value)) {
                    target.checked = true;
                    return;
                }

                existing?.remove();
                updateValue();
            });
        }

        state[options.syncStateKey] = syncFromInput;
        syncFromInput();
    }

    function setupLyricsFallbackOrder() {
        setupProviderOrderControl({
            dropdownId: 'lyricsFallbackOrderDropdown',
            triggerId: 'lyricsFallbackOrderTrigger',
            menuId: 'lyricsFallbackOrderMenu',
            listId: 'lyricsFallbackOrderList',
            inputId: 'lyricsFallbackOrder',
            sourceToggleContainerId: 'lyricsFallbackSources',
            syncStateKey: 'syncLyricsFallbackOrder',
            allowedOrder: LYRICS_SOURCE_ORDER,
            capabilityType: 'lyrics',
            itemClassName: 'lyrics-order-item',
            noSourcesText: 'No enabled lyrics sources',
            getLabel: (value) => getLyricsSourceLabel(value),
            handleIconName: 'drag_handle',
            setHandleAriaHidden: true,
            syncDefaultSource: (availableOrder, inputValue) => {
                applyDefaultSourceAvailability('lyricsDefaultSource', LYRICS_SOURCE_ORDER, availableOrder);
                syncDefaultSourceSelectFromOrder('lyricsDefaultSource', inputValue, LYRICS_SOURCE_ORDER);
            }
        });
    }

    function setupArtworkOrderControl(options) {
        const labelMap = options.labelMap || {
            apple: 'iTunes',
            deezer: 'Deezer',
            spotify: 'Spotify'
        };

        setupProviderOrderControl({
            ...options,
            labelMap,
            allowedOrder: Array.isArray(options.allowedOrder) && options.allowedOrder.length > 0
                ? options.allowedOrder
                : Object.keys(labelMap),
            noSourcesText: options.noSourcesText || 'No enabled artwork sources',
            getLabel: options.getLabel || ((value) => labelMap[value] || getSourceLabel(value)),
            itemClassName: options.itemClassName || 'artwork-order-item',
            handleIconName: options.handleIconName || 'drag_handle',
            setHandleAriaHidden: options.setHandleAriaHidden ?? true,
            syncDefaultSource: options.defaultSelectId && Array.isArray(options.allowedOrder)
                ? (availableOrder, inputValue) => {
                    applyDefaultSourceAvailability(options.defaultSelectId, options.allowedOrder, availableOrder);
                    syncDefaultSourceSelectFromOrder(options.defaultSelectId, inputValue, options.allowedOrder);
                }
                : null
        });
    }

    function setupArtworkFallbackOrder() {
        setupArtworkOrderControl({
            dropdownId: "artworkFallbackOrderDropdown",
            triggerId: "artworkFallbackOrderTrigger",
            menuId: "artworkFallbackOrderMenu",
            listId: "artworkFallbackOrderList",
            inputId: "artworkFallbackOrder",
            syncStateKey: "syncArtworkFallbackOrder",
            defaultSelectId: "artworkDefaultSource",
            allowedOrder: ARTWORK_SOURCE_ORDER,
            sourceToggleContainerId: "artworkFallbackSources",
            capabilityType: "artwork"
        });
    }

    function setupArtistArtworkFallbackOrder() {
        setupArtworkOrderControl({
            dropdownId: "artistArtworkFallbackOrderDropdown",
            triggerId: "artistArtworkFallbackOrderTrigger",
            menuId: "artistArtworkFallbackOrderMenu",
            listId: "artistArtworkFallbackOrderList",
            inputId: "artistArtworkFallbackOrder",
            syncStateKey: "syncArtistArtworkFallbackOrder",
            defaultSelectId: "artistArtworkDefaultSource",
            allowedOrder: ARTWORK_SOURCE_ORDER,
            sourceToggleContainerId: "artistArtworkFallbackSources",
            capabilityType: "artwork"
        });
    }

    function normalizeEmbedLyricsFormat(value) {
        const normalized = String(value || "").trim().toLowerCase();
        if (normalized === "both") {
            return "both";
        }
        if (normalized === "ttml") {
            return "ttml";
        }
        return "lrc";
    }

    function normalizeLyricsTypeToken(value) {
        const normalized = String(value || "").trim().toLowerCase();
        if (!normalized) {
            return "";
        }

        if (LYRICS_TYPE_OPTIONS.some((option) => option.value === normalized)) {
            return normalized;
        }

        return LYRICS_TYPE_ALIASES[normalized] || "";
    }

    function normalizeLyricsTypeSetting(value) {
        const raw = String(value || "");
        const seen = new Set();
        const selected = [];

        raw.split(",").forEach((token) => {
            const normalized = normalizeLyricsTypeToken(token);
            if (!normalized || seen.has(normalized)) {
                return;
            }
            seen.add(normalized);
            selected.push(normalized);
        });

        if (selected.length === 0) {
            selected.push(...DEFAULT_LYRICS_TYPE_SELECTION.split(","));
        }

        return selected.join(",");
    }

    function parseLyricsTypeSetting(value) {
        return normalizeLyricsTypeSetting(value).split(",").filter(Boolean);
    }

    function setupLyricsTypeDropdown() {
        const dropdown = document.getElementById("lyricsTypeDropdown");
        const trigger = document.getElementById("lyricsTypeTrigger");
        const menu = document.getElementById("lyricsTypeMenu");
        const list = document.getElementById("lyricsTypeList");
        const input = document.getElementById("lrcType");
        if (!dropdown || !trigger || !menu || !list || !input) {
            return;
        }

        const labelsByValue = new Map(LYRICS_TYPE_OPTIONS.map((option) => [option.value, option.label]));

        trigger.addEventListener("click", (event) => {
            event.stopPropagation();
            menu.classList.toggle("show");
        });

        menu.addEventListener("click", (event) => {
            event.stopPropagation();
        });

        document.addEventListener("click", () => {
            menu.classList.remove("show");
        });

        const updateValue = () => {
            let selected = Array.from(list.querySelectorAll("input[type=\"checkbox\"][data-value]:checked"))
                .map((checkbox) => normalizeLyricsTypeToken(checkbox.dataset.value))
                .filter(Boolean);

            if (selected.length === 0) {
                const defaultCheckbox = list.querySelector("input[type=\"checkbox\"][data-value=\"lyrics\"]");
                if (defaultCheckbox) {
                    defaultCheckbox.checked = true;
                }
                selected = ["lyrics"];
            }

            input.value = normalizeLyricsTypeSetting(selected.join(","));

            const labels = parseLyricsTypeSetting(input.value).map((value) => labelsByValue.get(value) || value);
            trigger.textContent = labels.join(", ");
        };

        const syncFromInput = () => {
            const selected = new Set(parseLyricsTypeSetting(input.value));
            list.querySelectorAll("input[type=\"checkbox\"][data-value]").forEach((checkbox) => {
                const value = normalizeLyricsTypeToken(checkbox.dataset.value);
                checkbox.checked = selected.has(value);
            });
            updateValue();
        };

        list.addEventListener("change", (event) => {
            if (event.target instanceof HTMLInputElement && event.target.type === "checkbox") {
                updateValue();
            }
        });

        state.syncLyricsTypeSelection = syncFromInput;
        syncFromInput();
    }

    function updateEmbedLyricsFormatVisibility() {
        const saveLyrics = document.getElementById("saveLyrics");
        const embedLyricsFormatGroup = document.getElementById("embedLyricsFormatGroup");
        const lrcFormat = document.getElementById("lrcFormat");
        if (!embedLyricsFormatGroup) {
            return;
        }

        const show = saveLyrics?.checked === true;
        embedLyricsFormatGroup.style.display = show ? "block" : "none";
        if (lrcFormat) {
            lrcFormat.disabled = !show;
        }
    }

    function setTemplateInputEnabled(toggleId, groupId, inputId) {
        const toggle = document.getElementById(toggleId);
        const group = document.getElementById(groupId);
        const input = document.getElementById(inputId);
        if (!toggle || !group) {
            return;
        }

        const enabled = toggle.checked;
        group.style.opacity = enabled ? "1" : "0.55";
        if (input) {
            input.disabled = !enabled;
        }
    }

    function refreshArtworkTemplateFieldState() {
        setTemplateInputEnabled("saveArtwork", "coverImageTemplateGroup", "coverImageTemplate");
        setTemplateInputEnabled("saveArtworkArtist", "artistImageTemplateGroup", "artistImageTemplate");
    }

    function setupFallbackSourceSelectors() {
        const bindings = [
            {
                selectId: "lyricsDefaultSource",
                inputId: "lyricsFallbackOrder",
                allowedOrder: LYRICS_SOURCE_ORDER,
                syncStateKey: "syncLyricsFallbackOrder"
            },
            {
                selectId: "artworkDefaultSource",
                inputId: "artworkFallbackOrder",
                allowedOrder: ARTWORK_SOURCE_ORDER,
                syncStateKey: "syncArtworkFallbackOrder"
            }
        ];

        bindings.forEach(({ selectId, inputId, allowedOrder, syncStateKey }) => {
            const select = el(selectId);
            const input = el(inputId);
            if (!(select instanceof HTMLSelectElement) || !(input instanceof HTMLInputElement)) {
                return;
            }

            ensureDefaultSourceOptions(selectId, allowedOrder);
            syncDefaultSourceSelectFromOrder(selectId, input.value, allowedOrder);

            select.addEventListener("change", () => {
                input.value = buildPreferredProviderOrder(select.value, input.value, allowedOrder).join(",");
                const syncOrder = state[syncStateKey];
                if (typeof syncOrder === "function") {
                    syncOrder();
                }
                syncFallbackSourceControls();
            });
        });
    }

    function setupDownloadTagsUi() {
        setupLyricsTypeDropdown();

        const saveLyrics = document.getElementById("saveLyrics");
        if (saveLyrics) {
            saveLyrics.addEventListener("change", updateEmbedLyricsFormatVisibility);
        }
        updateEmbedLyricsFormatVisibility();

        const saveArtwork = document.getElementById("saveArtwork");
        if (saveArtwork) {
            saveArtwork.addEventListener("change", refreshArtworkTemplateFieldState);
        }

        const saveArtworkArtist = document.getElementById("saveArtworkArtist");
        if (saveArtworkArtist) {
            saveArtworkArtist.addEventListener("change", refreshArtworkTemplateFieldState);
        }
        refreshArtworkTemplateFieldState();

        updateCoverMaintenanceTargetResolutionPolicyUI();
    }

    function insertTemplateVariable(input, value) {
        if (!input || !value) {
            return;
        }

        const start = input.selectionStart ?? input.value.length;
        const end = input.selectionEnd ?? input.value.length;
        const before = input.value.slice(0, start);
        const after = input.value.slice(end);
        input.value = `${before}${value}${after}`;
        const cursor = start + value.length;
        input.setSelectionRange(cursor, cursor);
        input.focus();
    }

    function setupTemplateVariableHelpers() {
        const variableSets = {
            track: [
                "%title%", "%artist%", "%artists%", "%allartists%", "%mainartists%",
                "%featartists%", "%album%", "%albumartist%", "%tracknumber%",
                "%tracktotal%", "%discnumber%", "%disctotal%", "%genre%", "%year%",
                "%date%", "%bpm%", "%label%", "%isrc%", "%upc%", "%explicit%",
                "%track_id%", "%album_id%", "%artist_id%"
            ],
            album: [
                "%title%", "%artist%", "%artists%", "%allartists%", "%mainartists%",
                "%featartists%", "%album%", "%albumartist%", "%tracknumber%",
                "%tracktotal%", "%discnumber%", "%disctotal%", "%genre%", "%year%",
                "%date%", "%bpm%", "%label%", "%isrc%", "%upc%", "%explicit%",
                "%track_id%", "%album_id%", "%artist_id%"
            ],
            playlist: [
                "%title%", "%artist%", "%artists%", "%allartists%", "%mainartists%",
                "%featartists%", "%album%", "%albumartist%", "%tracknumber%",
                "%tracktotal%", "%discnumber%", "%disctotal%", "%genre%", "%year%",
                "%date%", "%bpm%", "%label%", "%isrc%", "%upc%", "%explicit%",
                "%track_id%", "%album_id%", "%artist_id%", "%playlist_id%", "%position%"
            ]
        };

        document.querySelectorAll("[data-template-variables]").forEach((container) => {
            const type = container.dataset.variables || "track";
            const list = container.querySelector(".template-variables-list");
            if (!list) {
                return;
            }
            const variables = variableSets[type] || variableSets.track;
            list.innerHTML = variables
                .map((variable) => `<span class="template-variable" data-variable="${variable}">${variable}</span>`)
                .join("");
        });

        document.querySelectorAll(".template-variable").forEach((item) => {
            item.addEventListener("click", () => {
                const value = item.dataset.variable || "";
                if (!value) {
                    return;
                }
                const container = item.closest("[data-template-variables]");
                const inputId = container?.dataset.input;
                if (!inputId) {
                    return;
                }
                const input = document.getElementById(inputId);
                insertTemplateVariable(input, value);
                showToast(`Added ${value}`, "success");
            });
        });
    }

    function applyFieldValueIfPresent(id, value) {
        const field = document.getElementById(id);
        if (!field || value === undefined || value === null) {
            return;
        }

        field.value = value;
    }

    function applyFieldCheckedTrueOnly(id, value) {
        const field = document.getElementById(id);
        if (!field) {
            return;
        }

        field.checked = value === true;
    }

    function applyFieldCheckedWhenBoolean(id, value) {
        const field = document.getElementById(id);
        if (!field || typeof value !== "boolean") {
            return;
        }

        field.checked = value;
    }

    function applyLyricsSettingsToUI(settings) {
        if (!settings) {
            return;
        }

        const saveLyrics = document.getElementById("saveLyrics");
        if (saveLyrics) {
            saveLyrics.checked = settings.saveLyrics === true || settings.syncedLyrics === true;
        }

        const embedLyrics = document.getElementById("embedLyrics");
        if (embedLyrics) {
            embedLyrics.checked = settings.embedLyrics !== false;
        }

        const lrcType = document.getElementById("lrcType");
        if (lrcType) {
            lrcType.value = normalizeLyricsTypeSetting(settings.lrcType || DEFAULT_LYRICS_TYPE_SELECTION);
        }

        const lrcFormat = document.getElementById("lrcFormat");
        if (lrcFormat) {
            lrcFormat.value = normalizeEmbedLyricsFormat(settings.lrcFormat || "both");
        }

        const fallbackEnabled = document.getElementById("lyricsFallbackEnabled");
        if (fallbackEnabled) {
            fallbackEnabled.checked = settings.lyricsFallbackEnabled !== false;
        }

        const fallbackOrder = document.getElementById("lyricsFallbackOrder");
        if (fallbackOrder) {
            fallbackOrder.value = settings.lyricsFallbackOrder || LYRICS_SOURCE_ORDER.join(",");
        }

        if (state.syncLyricsFallbackOrder) {
            state.syncLyricsFallbackOrder();
        }
        syncDefaultSourceSelectFromOrder(
            "lyricsDefaultSource",
            fallbackOrder?.value || settings.lyricsFallbackOrder,
            LYRICS_SOURCE_ORDER
        );

        if (state.syncLyricsTypeSelection) {
            state.syncLyricsTypeSelection();
        }

        updateEmbedLyricsFormatVisibility();
        syncFallbackSourceControls();
    }

    function applyTemplateSettingsToUI(settings) {
        if (!settings) {
            return;
        }

        const trackTemplate = document.getElementById("autotag-trackname-template");
        if (trackTemplate) {
            trackTemplate.value = settings.tracknameTemplate || "";
        }

        const albumTemplate = document.getElementById("autotag-album-trackname-template");
        if (albumTemplate) {
            albumTemplate.value = settings.albumTracknameTemplate || "";
        }

        const playlistTemplate = document.getElementById("autotag-playlist-trackname-template");
        if (playlistTemplate) {
            playlistTemplate.value = settings.playlistTracknameTemplate || "";
        }

        renderFolderStructurePreview();
    }

    function applyArtworkSettingsToUI(settings) {
        if (!settings) {
            return;
        }

        applyFieldCheckedTrueOnly("saveArtwork", settings.saveArtwork);
        applyFieldCheckedTrueOnly("saveAnimatedArtwork", settings.saveAnimatedArtwork);
        applyFieldCheckedTrueOnly("dlAlbumcoverForPlaylist", settings.dlAlbumcoverForPlaylist);
        applyFieldCheckedTrueOnly("saveArtworkArtist", settings.saveArtworkArtist);
        applyFieldValueIfPresent("coverImageTemplate", settings.coverImageTemplate ?? "cover");
        applyFieldValueIfPresent("artistImageTemplate", settings.artistImageTemplate ?? "folder");
        applyFieldValueIfPresent("localArtworkFormat", settings.localArtworkFormat ?? "jpg");
        applyFieldCheckedTrueOnly("embedMaxQualityCover", settings.embedMaxQualityCover ?? true);
        applyFieldCheckedTrueOnly("artworkFallbackEnabled", settings.artworkFallbackEnabled ?? true);
        applyFieldValueIfPresent("artworkFallbackOrder", settings.artworkFallbackOrder ?? ARTWORK_SOURCE_ORDER.join(","));
        applyFieldCheckedTrueOnly("artistArtworkFallbackEnabled", settings.artistArtworkFallbackEnabled ?? settings.artworkFallbackEnabled ?? true);
        applyFieldValueIfPresent("artistArtworkFallbackOrder", settings.artistArtworkFallbackOrder ?? settings.artworkFallbackOrder ?? ARTWORK_SOURCE_ORDER.join(","));
        applyFieldCheckedTrueOnly("coverDescriptionUTF8", settings.tags?.coverDescriptionUTF8 ?? settings.coverDescriptionUTF8);
        applyFieldValueIfPresent("jpegImageQuality", settings.jpegImageQuality ?? 90);
        refreshArtworkTemplateFieldState();

        loadItunesArtOptions();
        updateCoverMaintenanceTargetResolutionPolicyUI();

        if (state.syncArtworkFallbackOrder) {
            state.syncArtworkFallbackOrder();
        }
        syncDefaultSourceSelectFromOrder("artworkDefaultSource", settings.artworkFallbackOrder, ARTWORK_SOURCE_ORDER);
        if (state.syncArtistArtworkFallbackOrder) {
            state.syncArtistArtworkFallbackOrder();
        }
        syncDefaultSourceSelectFromOrder(
            "artistArtworkDefaultSource",
            settings.artistArtworkFallbackOrder ?? settings.artworkFallbackOrder,
            ARTWORK_SOURCE_ORDER
        );
        syncFallbackSourceControls();
    }

    function applyOtherSettingsToUI(settings) {
        if (!settings) {
            return;
        }

        applyFieldValueIfPresent("dateFormat", settings.dateFormat ?? "Y-M-D");
        applyFieldCheckedTrueOnly("albumVariousArtists", settings.albumVariousArtists);
        applyFieldCheckedTrueOnly("removeAlbumVersion", settings.removeAlbumVersion);
        applyFieldCheckedTrueOnly("removeDuplicateArtists", settings.removeDuplicateArtists);
        applyFieldValueIfPresent("featuredToTitle", settings.featuredToTitle ?? "0");
        applyFieldValueIfPresent("titleCasing", settings.titleCasing ?? "nothing");
        applyFieldValueIfPresent("artistCasing", settings.artistCasing ?? "nothing");

        if (settings.tags) {
            applyFieldCheckedTrueOnly("savePlaylistAsCompilation", settings.tags.savePlaylistAsCompilation);
            applyFieldCheckedTrueOnly("useNullSeparator", settings.tags.useNullSeparator);
            applyFieldCheckedTrueOnly("saveID3v1", settings.tags.saveID3v1);
            applyFieldValueIfPresent("multiArtistSeparator", settings.tags.multiArtistSeparator ?? "default");
            applyFieldCheckedTrueOnly("singleAlbumArtist", settings.tags.singleAlbumArtist ?? true);
        }

        syncMultiArtistHandlingState();
    }

    function applyTechnicalSettingsToUI(technical) {
        if (!technical) {
            return;
        }

        applyFieldCheckedWhenBoolean("savePlaylistAsCompilation", technical.savePlaylistAsCompilation);
        applyFieldCheckedWhenBoolean("useNullSeparator", technical.useNullSeparator);
        applyFieldCheckedWhenBoolean("saveID3v1", technical.saveID3v1);
        applyFieldValueIfPresent("multiArtistSeparator", technical.multiArtistSeparator);
        applyFieldCheckedWhenBoolean("singleAlbumArtist", technical.singleAlbumArtist);
        applyFieldCheckedWhenBoolean("coverDescriptionUTF8", technical.coverDescriptionUTF8);
        applyFieldCheckedWhenBoolean("albumVariousArtists", technical.albumVariousArtists);
        applyFieldCheckedWhenBoolean("removeDuplicateArtists", technical.removeDuplicateArtists);
        applyFieldCheckedWhenBoolean("removeAlbumVersion", technical.removeAlbumVersion);
        applyFieldValueIfPresent("dateFormat", technical.dateFormat);
        applyFieldValueIfPresent("featuredToTitle", technical.featuredToTitle);
        applyFieldValueIfPresent("titleCasing", technical.titleCasing);
        applyFieldValueIfPresent("artistCasing", technical.artistCasing);
        applyFieldCheckedWhenBoolean("saveLyrics", technical.saveLyrics ?? technical.syncedLyrics);
        applyFieldCheckedWhenBoolean("embedLyrics", technical.embedLyrics ?? true);
        applyFieldValueIfPresent("lrcType", normalizeLyricsTypeSetting(technical.lrcType || DEFAULT_LYRICS_TYPE_SELECTION));
        applyFieldValueIfPresent("lrcFormat", normalizeEmbedLyricsFormat(technical.lrcFormat || "both"));
        applyFieldCheckedWhenBoolean("lyricsFallbackEnabled", technical.lyricsFallbackEnabled);
        applyFieldValueIfPresent("lyricsFallbackOrder", technical.lyricsFallbackOrder || LYRICS_SOURCE_ORDER.join(","));
        applyFieldCheckedWhenBoolean("artworkFallbackEnabled", technical.artworkFallbackEnabled);
        applyFieldValueIfPresent("artworkFallbackOrder", technical.artworkFallbackOrder || ARTWORK_SOURCE_ORDER.join(","));
        applyFieldCheckedWhenBoolean("artistArtworkFallbackEnabled", technical.artistArtworkFallbackEnabled);
        applyFieldValueIfPresent("artistArtworkFallbackOrder", technical.artistArtworkFallbackOrder || ARTWORK_SOURCE_ORDER.join(","));

        if (state.syncLyricsFallbackOrder) {
            state.syncLyricsFallbackOrder();
        }
        syncDefaultSourceSelectFromOrder("lyricsDefaultSource", technical.lyricsFallbackOrder, LYRICS_SOURCE_ORDER);
        if (state.syncLyricsTypeSelection) {
            state.syncLyricsTypeSelection();
        }
        if (state.syncArtworkFallbackOrder) {
            state.syncArtworkFallbackOrder();
        }
        syncDefaultSourceSelectFromOrder("artworkDefaultSource", technical.artworkFallbackOrder, ARTWORK_SOURCE_ORDER);
        if (state.syncArtistArtworkFallbackOrder) {
            state.syncArtistArtworkFallbackOrder();
        }
        syncDefaultSourceSelectFromOrder(
            "artistArtworkDefaultSource",
            technical.artistArtworkFallbackOrder ?? technical.artworkFallbackOrder,
            ARTWORK_SOURCE_ORDER
        );
        updateEmbedLyricsFormatVisibility();

        syncFallbackSourceControls();
        syncMultiArtistHandlingState();
    }

    function applyFolderStructureSettingsToUI(settings) {
        if (!settings) {
            return;
        }

        applyFieldCheckedTrueOnly("createPlaylistFolder", settings.createPlaylistFolder !== false);
        applyFieldValueIfPresent("playlistNameTemplate", settings.playlistNameTemplate || "%playlist%");
        applyFieldCheckedTrueOnly("createArtistFolder", settings.createArtistFolder !== false);
        applyFieldValueIfPresent("artistNameTemplate", settings.artistNameTemplate || "%artist%");
        applyFieldCheckedTrueOnly("createAlbumFolder", settings.createAlbumFolder !== false);
        applyFieldValueIfPresent("albumNameTemplate", settings.albumNameTemplate || "%album%");
        applyFieldCheckedTrueOnly("createCDFolder", settings.createCDFolder);
        applyFieldCheckedTrueOnly("createStructurePlaylist", settings.createStructurePlaylist);
        applyFieldCheckedTrueOnly("createSingleFolder", settings.createSingleFolder);
        applyFieldValueIfPresent("illegalCharacterReplacer", settings.illegalCharacterReplacer || "_");
        updateFolderStructureVisibility();
        renderFolderStructurePreview();
    }

    async function loadLyricsSettings() {
        try {
            const response = await fetch("/api/settings");
            if (!response.ok) {
                throw new Error(`HTTP ${response.status}`);
            }
            const data = await response.json();
            state.settingsCache = data?.settings || null;
            applyLyricsSettingsToUI(state.settingsCache);
            applyArtworkSettingsToUI(state.settingsCache);
            applyTemplateSettingsToUI(state.settingsCache);
            applyOtherSettingsToUI(state.settingsCache);
            applyFolderStructureSettingsToUI(state.settingsCache);
            refreshDownloadTagsForSource();
            // Manual external intake path input is intentionally disabled.
            // const intakeInput = el("autotag-custom-path");
            // const currentIntakeValue = String(intakeInput?.value || "").trim();
            // const configuredIntakeValue = String(state.config.customPath || "").trim();
            // const defaultIntakePath = String(state.settingsCache?.downloadLocation || "").trim();
            // if (intakeInput && !currentIntakeValue && !configuredIntakeValue && defaultIntakePath) {
            //     intakeInput.value = defaultIntakePath;
            //     state.config.customPath = defaultIntakePath;
            // }
            state.config.customPath = null;
        } catch (error) {
            console.error("Failed to load lyrics settings.", error);
            showToast("Failed to load lyrics settings.", "error");
        }
    }

    function readLyricsSettingsFromUI(baseSettings) {
        const settings = baseSettings ? { ...baseSettings } : {};
        const getChecked = (id, fallback = false) => document.getElementById(id)?.checked ?? fallback;
        const getValue = (id, fallback = "") => document.getElementById(id)?.value ?? fallback;

        settings.saveLyrics = getChecked("saveLyrics", settings.saveLyrics ?? settings.syncedLyrics ?? false);
        settings.syncedLyrics = settings.saveLyrics;
        settings.embedLyrics = getChecked("embedLyrics", settings.embedLyrics ?? true);
        settings.lrcType = normalizeLyricsTypeSetting(getValue("lrcType", settings.lrcType ?? DEFAULT_LYRICS_TYPE_SELECTION));
        settings.lrcFormat = getValue("lrcFormat", settings.lrcFormat ?? "both");
        settings.lyricsFallbackEnabled = getChecked("lyricsFallbackEnabled", settings.lyricsFallbackEnabled ?? true);
        settings.lyricsFallbackOrder = resolveSavedFallbackOrder({
            fallbackEnabled: settings.lyricsFallbackEnabled,
            defaultSourceId: "lyricsDefaultSource",
            fallbackOrderValue: getValue("lyricsFallbackOrder", settings.lyricsFallbackOrder ?? LYRICS_SOURCE_ORDER.join(",")),
            allowedOrder: LYRICS_SOURCE_ORDER
        });

        return settings;
    }

    function readTemplateSettingsFromUI(baseSettings) {
        const settings = baseSettings ? { ...baseSettings } : {};
        const getValue = (id, fallback = "") => document.getElementById(id)?.value ?? fallback;

        settings.tracknameTemplate = getValue(
            "autotag-trackname-template",
            settings.tracknameTemplate ?? "%artist% - %title%"
        );
        settings.albumTracknameTemplate = getValue(
            "autotag-album-trackname-template",
            settings.albumTracknameTemplate ?? "%tracknumber% - %title%"
        );
        settings.playlistTracknameTemplate = getValue(
            "autotag-playlist-trackname-template",
            settings.playlistTracknameTemplate ?? "%artist% - %title%"
        );

        return settings;
    }

    function readArtworkSettingsFromUI(baseSettings) {
        const settings = baseSettings ? { ...baseSettings } : {};
        const getValue = (id, fallback = "") => document.getElementById(id)?.value ?? fallback;
        const getChecked = (id, fallback = false) => document.getElementById(id)?.checked ?? fallback;
        const getNumber = (id, fallback = 0) => Number.parseInt(getValue(id, ""), 10) || fallback;

        settings.saveArtwork = getChecked("saveArtwork", settings.saveArtwork ?? false);
        settings.saveAnimatedArtwork = getChecked("saveAnimatedArtwork", settings.saveAnimatedArtwork ?? false);
        settings.dlAlbumcoverForPlaylist = getChecked("dlAlbumcoverForPlaylist", settings.dlAlbumcoverForPlaylist ?? true);
        settings.saveArtworkArtist = getChecked("saveArtworkArtist", settings.saveArtworkArtist ?? false);
        settings.coverImageTemplate = getValue("coverImageTemplate", settings.coverImageTemplate ?? "cover");
        settings.artistImageTemplate = getValue("artistImageTemplate", settings.artistImageTemplate ?? "folder");
        settings.localArtworkFormat = getValue("localArtworkFormat", settings.localArtworkFormat ?? "jpg");
        settings.embedMaxQualityCover = getChecked("embedMaxQualityCover", settings.embedMaxQualityCover ?? true);
        settings.artworkFallbackEnabled = getChecked("artworkFallbackEnabled", settings.artworkFallbackEnabled ?? true);
        settings.artworkFallbackOrder = resolveSavedFallbackOrder({
            fallbackEnabled: settings.artworkFallbackEnabled,
            defaultSourceId: "artworkDefaultSource",
            fallbackOrderValue: getValue("artworkFallbackOrder", settings.artworkFallbackOrder ?? ARTWORK_SOURCE_ORDER.join(",")),
            allowedOrder: ARTWORK_SOURCE_ORDER
        });
        settings.artistArtworkFallbackEnabled = getChecked(
            "artistArtworkFallbackEnabled",
            settings.artistArtworkFallbackEnabled ?? settings.artworkFallbackEnabled ?? true
        );
        settings.artistArtworkFallbackOrder = resolveSavedFallbackOrder({
            fallbackEnabled: settings.artistArtworkFallbackEnabled,
            defaultSourceId: "artistArtworkDefaultSource",
            fallbackOrderValue: getValue(
                "artistArtworkFallbackOrder",
                settings.artistArtworkFallbackOrder ?? settings.artworkFallbackOrder ?? ARTWORK_SOURCE_ORDER.join(",")
            ),
            allowedOrder: ARTWORK_SOURCE_ORDER
        });
        const tags = settings.tags ? { ...settings.tags } : {};
        tags.coverDescriptionUTF8 = getChecked("coverDescriptionUTF8", tags.coverDescriptionUTF8 ?? false);
        settings.tags = tags;
        settings.jpegImageQuality = getNumber("jpegImageQuality", settings.jpegImageQuality ?? 90);

        return settings;
    }

    function readOtherSettingsFromUI(baseSettings) {
        const settings = baseSettings ? { ...baseSettings } : {};
        const getValue = (id, fallback = "") => document.getElementById(id)?.value ?? fallback;
        const getChecked = (id, fallback = false) => document.getElementById(id)?.checked ?? fallback;

        settings.dateFormat = getValue("dateFormat", settings.dateFormat ?? "Y-M-D");
        settings.albumVariousArtists = getChecked("albumVariousArtists", settings.albumVariousArtists ?? false);
        settings.removeAlbumVersion = getChecked("removeAlbumVersion", settings.removeAlbumVersion ?? false);
        settings.removeDuplicateArtists = getChecked("removeDuplicateArtists", settings.removeDuplicateArtists ?? false);
        settings.featuredToTitle = getValue("featuredToTitle", settings.featuredToTitle ?? "0");
        settings.titleCasing = getValue("titleCasing", settings.titleCasing ?? "nothing");
        settings.artistCasing = getValue("artistCasing", settings.artistCasing ?? "nothing");

        const tags = settings.tags ? { ...settings.tags } : {};
        tags.savePlaylistAsCompilation = getChecked("savePlaylistAsCompilation", tags.savePlaylistAsCompilation ?? false);
        tags.useNullSeparator = getChecked("useNullSeparator", tags.useNullSeparator ?? false);
        tags.saveID3v1 = getChecked("saveID3v1", tags.saveID3v1 ?? false);
        tags.multiArtistSeparator = getValue("multiArtistSeparator", tags.multiArtistSeparator ?? "default");
        tags.singleAlbumArtist = getChecked("singleAlbumArtist", tags.singleAlbumArtist ?? true);
        settings.tags = tags;

        return settings;
    }

    function readTechnicalSettingsFromUI(baseTechnical) {
        const technical = baseTechnical ? { ...baseTechnical } : {};
        const getValue = (id, fallback = "") => document.getElementById(id)?.value ?? fallback;
        const getChecked = (id, fallback = false) => document.getElementById(id)?.checked ?? fallback;

        technical.savePlaylistAsCompilation = getChecked("savePlaylistAsCompilation", technical.savePlaylistAsCompilation ?? false);
        technical.useNullSeparator = getChecked("useNullSeparator", technical.useNullSeparator ?? false);
        technical.saveID3v1 = getChecked("saveID3v1", technical.saveID3v1 ?? false);
        technical.multiArtistSeparator = getValue("multiArtistSeparator", technical.multiArtistSeparator ?? "default");
        technical.singleAlbumArtist = getChecked("singleAlbumArtist", technical.singleAlbumArtist ?? true);
        technical.coverDescriptionUTF8 = getChecked("coverDescriptionUTF8", technical.coverDescriptionUTF8 ?? true);
        technical.albumVariousArtists = getChecked("albumVariousArtists", technical.albumVariousArtists ?? false);
        technical.removeDuplicateArtists = getChecked("removeDuplicateArtists", technical.removeDuplicateArtists ?? false);
        technical.removeAlbumVersion = getChecked("removeAlbumVersion", technical.removeAlbumVersion ?? false);
        technical.dateFormat = getValue("dateFormat", technical.dateFormat ?? "Y-M-D");
        technical.featuredToTitle = getValue("featuredToTitle", technical.featuredToTitle ?? "0");
        technical.titleCasing = getValue("titleCasing", technical.titleCasing ?? "nothing");
        technical.artistCasing = getValue("artistCasing", technical.artistCasing ?? "nothing");
        technical.saveLyrics = getChecked("saveLyrics", technical.saveLyrics ?? technical.syncedLyrics ?? false);
        technical.syncedLyrics = technical.saveLyrics;
        technical.embedLyrics = getChecked("embedLyrics", technical.embedLyrics ?? true);
        technical.lrcType = normalizeLyricsTypeSetting(getValue("lrcType", technical.lrcType ?? DEFAULT_LYRICS_TYPE_SELECTION));
        technical.lrcFormat = normalizeEmbedLyricsFormat(getValue("lrcFormat", technical.lrcFormat ?? "both"));
        technical.lyricsFallbackEnabled = getChecked("lyricsFallbackEnabled", technical.lyricsFallbackEnabled ?? true);
        technical.lyricsFallbackOrder = resolveSavedFallbackOrder({
            fallbackEnabled: technical.lyricsFallbackEnabled,
            defaultSourceId: "lyricsDefaultSource",
            fallbackOrderValue: getValue("lyricsFallbackOrder", technical.lyricsFallbackOrder ?? LYRICS_SOURCE_ORDER.join(",")),
            allowedOrder: LYRICS_SOURCE_ORDER
        });
        technical.artworkFallbackEnabled = getChecked("artworkFallbackEnabled", technical.artworkFallbackEnabled ?? true);
        technical.artworkFallbackOrder = resolveSavedFallbackOrder({
            fallbackEnabled: technical.artworkFallbackEnabled,
            defaultSourceId: "artworkDefaultSource",
            fallbackOrderValue: getValue("artworkFallbackOrder", technical.artworkFallbackOrder ?? ARTWORK_SOURCE_ORDER.join(",")),
            allowedOrder: ARTWORK_SOURCE_ORDER
        });
        technical.artistArtworkFallbackEnabled = getChecked(
            "artistArtworkFallbackEnabled",
            technical.artistArtworkFallbackEnabled ?? technical.artworkFallbackEnabled ?? true
        );
        technical.artistArtworkFallbackOrder = resolveSavedFallbackOrder({
            fallbackEnabled: technical.artistArtworkFallbackEnabled,
            defaultSourceId: "artistArtworkDefaultSource",
            fallbackOrderValue: getValue(
                "artistArtworkFallbackOrder",
                technical.artistArtworkFallbackOrder ?? technical.artworkFallbackOrder ?? ARTWORK_SOURCE_ORDER.join(",")
            ),
            allowedOrder: ARTWORK_SOURCE_ORDER
        });

        return technical;
    }

    function readFolderStructureSettingsFromUI(baseSettings) {
        const settings = baseSettings ? { ...baseSettings } : {};
        const getValue = (id, fallback = "") => document.getElementById(id)?.value ?? fallback;
        const getChecked = (id, fallback = false) => document.getElementById(id)?.checked ?? fallback;

        settings.createPlaylistFolder = getChecked(
            "createPlaylistFolder",
            settings.createPlaylistFolder ?? true
        );
        settings.playlistNameTemplate = getValue(
            "playlistNameTemplate",
            settings.playlistNameTemplate ?? "%playlist%"
        );
        settings.createArtistFolder = getChecked(
            "createArtistFolder",
            settings.createArtistFolder ?? true
        );
        settings.artistNameTemplate = getValue(
            "artistNameTemplate",
            settings.artistNameTemplate ?? "%artist%"
        );
        settings.createAlbumFolder = getChecked(
            "createAlbumFolder",
            settings.createAlbumFolder ?? true
        );
        settings.albumNameTemplate = getValue(
            "albumNameTemplate",
            settings.albumNameTemplate ?? "%album%"
        );
        settings.createCDFolder = getChecked(
            "createCDFolder",
            settings.createCDFolder ?? false
        );
        settings.createStructurePlaylist = getChecked(
            "createStructurePlaylist",
            settings.createStructurePlaylist ?? false
        );
        settings.createSingleFolder = getChecked(
            "createSingleFolder",
            settings.createSingleFolder ?? false
        );
        settings.illegalCharacterReplacer = getValue(
            "illegalCharacterReplacer",
            settings.illegalCharacterReplacer ?? "_"
        );

        return settings;
    }

    async function saveLyricsSettings() {
        try {
            if (!state.settingsCache) {
                await loadLyricsSettings();
            }
            if (!state.settingsCache) {
                throw new Error("Settings unavailable");
            }
            let updated = readLyricsSettingsFromUI(state.settingsCache);
            updated = readArtworkSettingsFromUI(updated);
            updated = readTemplateSettingsFromUI(updated);
            updated = readOtherSettingsFromUI(updated);
            updated = readFolderStructureSettingsFromUI(updated);
            const response = await fetch("/api/settings", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(updated)
            });
            if (!response.ok) {
                const payload = await response.json().catch(() => null);
                const message = payload?.error || `HTTP ${response.status}`;
                throw new Error(message);
            }
            state.settingsCache = updated;
        } catch (error) {
            console.error("Failed to save lyrics settings.", error);
            showToast("Failed to save lyrics settings.", "error");
        }
    }

    function loadItunesArtOptions() {
        const select = el("autotag-itunes-art-resolution");
        const animatedControls = getAnimatedArtworkControls();
        if (!select && animatedControls.length === 0) {
            return;
        }
        ensureCustomDefaults();
        if (select) {
            const resolution = clampItunesArtResolution(state.config.custom.itunes.art_resolution, 1000);
            state.config.custom.itunes.art_resolution = resolution;
            select.value = String(resolution);
        }
        const configured = state.config?.custom?.itunes?.animated_artwork;
        const animatedValue = typeof configured === "boolean"
            ? configured
            : Boolean(state.settingsCache?.saveAnimatedArtwork);
        if (animatedControls.length > 0) {
            setAnimatedArtworkControls(animatedValue);
        }
    }

    function getAnimatedArtworkControls() {
        return [
            "saveAnimatedArtwork"
        ]
            .map((id) => el(id))
            .filter((field) => field instanceof HTMLInputElement);
    }

    function setAnimatedArtworkControls(value) {
        getAnimatedArtworkControls().forEach((field) => {
            field.checked = value === true;
        });
    }

    function getAnimatedArtworkValue(fallback = false) {
        const controls = getAnimatedArtworkControls();
        if (controls.length === 0) {
            return fallback === true;
        }
        return controls[0].checked;
    }

    async function startAutoTag() {
        const config = readConfigFromUI();
        if (String(config.conflictResolution || "").toLowerCase() === "shazam") {
            config.moveFailed = true;
            if (!String(config.moveFailedPath || "").trim()) {
                const message = "Set Failed/Conflict files destination path before running AutoTag.";
                setExternalStartStatus(message);
                showToast(message, "warning");
                return;
            }
        }
        if (config.moveSuccess && !config.moveSuccessPath) {
            const message = "Select a success library destination before running AutoTag.";
            setExternalStartStatus(message);
            showToast(message, "warning");
            return;
        }
        const storedAuth = await loadStoredAuth();
        mergeStoredAuth(config, storedAuth);
        await applyProjectSpotifyConfig(config);
        normalizeSpotifyConfig(config);

        // Manual enrichment runs in the configured download/staging folder only.
        const defaultStagingPath = String(state.settingsCache?.downloadLocation || "").trim();
        const targetPath = defaultStagingPath;

        if (!targetPath) {
            const message = "Set Download/Staging folder in Settings before running AutoTag.";
            setExternalStartStatus(message);
            showToast(message, "warning");
            return;
        }

        config.path = targetPath;
        config.customPath = null;
        // Playlist intake is intentionally disabled for AutoTag manual runs.
        // config.isPlaylist = false;
        setExternalStartStatus("Starting enrichment in Download/Staging folder...");

        const selectedProfile = getSelectedProfile();
        const resolvedProfileId = String(
            selectedProfile?.id
            || state.activeProfileId
            || ""
        ).trim();

        const response = await fetch("/api/autotag/start", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            // Playlist intake is intentionally disabled for AutoTag manual runs.
            // body: JSON.stringify({ path: targetPath, config, isPlaylist: false })
            body: JSON.stringify({
                path: targetPath,
                config,
                profileId: resolvedProfileId || null
            })
        });

        if (!response.ok) {
            const message = await response.text();
            setExternalStartStatus(message || "Failed to start external AutoTag.");
            showToast(message || "Failed to start AutoTag.", "error");
            return;
        }

        const data = await response.json();
        state.jobId = data.jobId;
        localStorage.setItem("autotagJobId", data.jobId);
        updateStatus("Running", data.status || "running");
        if (hasStatusUI()) {
            schedulePoll();
        }
        setExternalStartStatus("External AutoTag started.");
        showToast("External AutoTag started.", "success");
    }

    async function fetchJobById(jobId) {
        const id = String(jobId || "").trim();
        if (!id) {
            return null;
        }

        try {
            const response = await fetch(`/api/autotag/jobs/${encodeURIComponent(id)}`);
            if (!response.ok) {
                return null;
            }
            return await response.json();
        } catch (error) {
            console.debug("Failed to fetch AutoTag job by id.", error);
            return null;
        }
    }

    async function fetchLatestJob() {
        try {
            const response = await fetch("/api/autotag/jobs/latest");
            if (!response.ok) {
                return null;
            }
            return await response.json();
        } catch (error) {
            console.debug("Failed to fetch latest AutoTag job.", error);
            return null;
        }
    }

    function isAutoTagJobActive(job) {
        const status = String(job?.status || "").trim().toLowerCase();
        return status === "running" || status === "queued";
    }

    function rememberActiveAutoTagJob(jobId) {
        const id = String(jobId || "").trim();
        if (!id) {
            return;
        }

        state.jobId = id;
        localStorage.setItem("autotagJobId", id);
    }

    async function resolveActiveAutoTagJobId() {
        const inMemoryId = String(state.jobId || "").trim();
        if (inMemoryId) {
            const currentJob = await fetchJobById(inMemoryId);
            if (isAutoTagJobActive(currentJob)) {
                rememberActiveAutoTagJob(currentJob.id || inMemoryId);
                return state.jobId;
            }
        }

        const storedId = String(localStorage.getItem("autotagJobId") || "").trim();
        if (storedId) {
            const storedJob = await fetchJobById(storedId);
            if (isAutoTagJobActive(storedJob)) {
                rememberActiveAutoTagJob(storedJob.id || storedId);
                return state.jobId;
            }
        }

        const latestJob = await fetchLatestJob();
        if (isAutoTagJobActive(latestJob)) {
            rememberActiveAutoTagJob(latestJob.id);
            return state.jobId;
        }

        state.jobId = null;
        localStorage.removeItem("autotagJobId");
        return null;
    }

    async function stopAutoTag() {
        const activeJobId = await resolveActiveAutoTagJobId();
        if (!activeJobId) {
            showToast("No active AutoTag job.", "warning");
            return;
        }

        const response = await fetch(`/api/autotag/jobs/${encodeURIComponent(activeJobId)}/stop`, { method: "POST" });
        if (!response.ok) {
            if (response.status === 404) {
                localStorage.removeItem("autotagJobId");
                showToast("AutoTag job is no longer running.", "warning");
                return;
            }
            showToast("Failed to stop AutoTag.", "error");
            return;
        }

        updateStatus(activeJobId, "canceled");
        localStorage.removeItem("autotagJobId");
        showToast("AutoTag stopped.", "success");
    }

    function hasStatusUI() {
        return el("autotag-job") && el("autotag-state") && el("autotag-updated") && el("autotag-log");
    }

    function updateStatus(job, stateText) {
        if (!hasStatusUI()) {
            return;
        }
        el("autotag-job").textContent = job;
        el("autotag-state").textContent = stateText;
        el("autotag-updated").textContent = new Date().toLocaleTimeString();
    }

    async function pollJob() {
        if (!hasStatusUI()) {
            return;
        }

        try {
            const activeJobId = await resolveActiveAutoTagJobId();
            if (!activeJobId) {
                return;
            }

            const response = await fetch(`/api/autotag/jobs/${encodeURIComponent(activeJobId)}`);
            if (!response.ok) {
                updateStatus(activeJobId, "unavailable");
                return;
            }

            const job = await response.json();
            rememberActiveAutoTagJob(job.id || activeJobId);
            updateStatus(job.id, job.status);
            el("autotag-log").textContent = (job.logs || []).join("\n");

            if (job.status === "running") {
                schedulePoll();
            } else {
                localStorage.removeItem("autotagJobId");
            }
        } catch (error) {
            console.debug("AutoTag status polling failed.", error);
            updateStatus(state.jobId || "unknown", "error");
        }
    }

    function schedulePoll() {
        if (state.pollTimer) {
            clearTimeout(state.pollTimer);
        }
        state.pollTimer = setTimeout(pollJob, 2000);
    }

    function resetForm() {
        state.config = structuredClone(DEFAULT_CONFIG);
        loadConfigToUI();
        showToast("AutoTag settings reset.", "info");
    }

    async function loadProfiles() {
        state.profilesLoaded = false;
        try {
            const response = await fetch("/api/tagging/profiles");
            if (!response.ok) {
                throw new Error("Failed to load profiles");
            }
            state.profiles = await response.json();
        } catch (error) {
            console.warn("Failed to load AutoTag profiles", error);
            state.profiles = [];
        } finally {
            state.profilesLoaded = true;
        }
        renderProfileSelect();
        restoreActiveProfileSelection();
    }

    async function initAutoTagDefaultsPanel() {
        try {
            if (typeof globalThis.refreshAutoTagFolderDefaults === "function") {
                await globalThis.refreshAutoTagFolderDefaults();
            }
        } catch (error) {
            console.error("Failed to load AutoTag folder defaults.", error);
            showToast("Failed to load folder profile defaults.", "error");
        }
        try {
            await loadAutoTagDefaults();
        } catch (error) {
            console.error("Failed to load AutoTag defaults.", error);
            showToast("Failed to load enhancement defaults.", "error");
        }
    }

    function renderProfileSelect() {
        const select = el("autotag-profile-select");
        if (!select) {
            return;
        }
        select.innerHTML = "";
        const placeholder = document.createElement("option");
        placeholder.value = "";
        placeholder.textContent = "Select a profile";
        select.appendChild(placeholder);
        state.profiles.forEach((profile) => {
            const opt = document.createElement("option");
            opt.value = profile.id;
            opt.textContent = profile.name;
            select.appendChild(opt);
        });
        if (state.activeProfileId) {
            select.value = state.activeProfileId;
        }
    }

    async function readApiErrorMessage(response) {
        const payload = await response.clone().json().catch(() => null);
        const payloadMessage = payload?.message || payload?.error || payload?.title;
        if (typeof payloadMessage === "string" && payloadMessage.trim()) {
            return payloadMessage.trim();
        }

        const rawText = await response.text().catch(() => "");
        if (typeof rawText === "string" && rawText.trim()) {
            return rawText.trim();
        }

        return `HTTP ${response.status}`;
    }

    function normalizeAutoTagDefaults(defaults) {
        const source = defaults && typeof defaults === "object" ? defaults : {};
        const parsedRecentWindow = Number.parseInt(String(source.recentDownloadWindowHours ?? ""), 10);
        const recentDownloadWindowHours = Number.isFinite(parsedRecentWindow) && parsedRecentWindow >= 0
            ? parsedRecentWindow
            : DEFAULT_RECENT_DOWNLOAD_WINDOW_HOURS;
        return {
            defaultFileProfile: typeof source.defaultFileProfile === "string" && source.defaultFileProfile.trim()
                ? source.defaultFileProfile.trim()
                : null,
            librarySchedules: source.librarySchedules && typeof source.librarySchedules === "object"
                ? { ...source.librarySchedules }
                : {},
            recentDownloadWindowHours,
            renameSpotifyArtistFolders: source.renameSpotifyArtistFolders !== false
        };
    }

    function ensureRecentDownloadWindowControls() {
        if (el("enhancementRecentDownloadWindowDays")) {
            return;
        }

        const qualityChecksButton = el("runEnhancementQualityChecks");
        const section = qualityChecksButton?.closest(".download-section");
        if (!section) {
            return;
        }

        const insertionAnchor = el("enhancementTechnicalProfilesStatus")
            || el("enhancementQualityChecksStatus")
            || section.querySelector(".enhancement-action-row");
        const row = document.createElement("div");
        row.className = "download-grid download-grid-3 mt-3";
        row.innerHTML = `
            <div class="form-group">
                <div class="checkbox-group mb-2">
                    <input type="checkbox" id="enhancementRenameSpotifyArtistFolders" />
                    <label for="enhancementRenameSpotifyArtistFolders">
                        Rename artist folders to Spotify's canonical artist name
                    </label>
                </div>
                <span class="helper">Applies to Spotify artist lookups across the library and watchlist flows.</span>
            </div>
            <div class="form-group">
                <label for="enhancementRecentDownloadWindowDays">
                    Recent downloads window (days)
                    <span class="autotag-tooltip-icon ms-1"
                          title="Only files modified within this window in your library are enhanced. Set to 0 to disable time filtering."
                          aria-label="Only files modified within this window in your library are enhanced. Set to 0 to disable time filtering.">
                        <i class="fas fa-question-circle"></i>
                    </span>
                </label>
                <input type="number" id="enhancementRecentDownloadWindowDays" min="0" step="1" />
                <span class="helper">Applies to the automation run that enhances recent downloads moved into library folders.</span>
            </div>
            <div class="form-group d-flex align-items-end">
                <button type="button" class="action-btn action-btn-sm enhancement-action-btn" id="saveEnhancementDefaults">Save Enhancement Defaults</button>
            </div>
        `;

        section.insertBefore(row, insertionAnchor || null);
        if (!el("enhancementRecentDownloadWindowStatus")) {
            const status = document.createElement("span");
            status.className = "helper";
            status.id = "enhancementRecentDownloadWindowStatus";
            section.insertBefore(status, insertionAnchor || null);
        }
    }

    function convertRecentWindowHoursToDays(hours) {
        const normalizedHours = Number.isFinite(hours) && hours >= 0
            ? hours
            : DEFAULT_RECENT_DOWNLOAD_WINDOW_HOURS;
        if (normalizedHours === 0) {
            return 0;
        }
        return Math.max(1, Math.round(normalizedHours / 24));
    }

    function convertRecentWindowDaysToHours(days) {
        const normalizedDays = Number.isFinite(days) && days >= 0
            ? days
            : DEFAULT_RECENT_DOWNLOAD_WINDOW_DAYS;
        return normalizedDays * 24;
    }

    function readRecentDownloadWindowDays() {
        const field = el("enhancementRecentDownloadWindowDays");
        if (!field) {
            return DEFAULT_RECENT_DOWNLOAD_WINDOW_DAYS;
        }
        const parsed = Number.parseInt(String(field.value ?? "").trim(), 10);
        if (!Number.isFinite(parsed) || parsed < 0) {
            return DEFAULT_RECENT_DOWNLOAD_WINDOW_DAYS;
        }
        return parsed;
    }

    function applyAutoTagDefaultsToUi() {
        ensureRecentDownloadWindowControls();
        const renameFoldersCheckbox = el("enhancementRenameSpotifyArtistFolders");
        if (renameFoldersCheckbox instanceof HTMLInputElement) {
            renameFoldersCheckbox.checked = state.autoTagDefaults?.renameSpotifyArtistFolders !== false;
        }
        const field = el("enhancementRecentDownloadWindowDays");
        if (!field) {
            return;
        }
        const value = convertRecentWindowHoursToDays(
            state.autoTagDefaults?.recentDownloadWindowHours ?? DEFAULT_RECENT_DOWNLOAD_WINDOW_HOURS);
        field.value = value;
    }

    async function loadAutoTagDefaults() {
        state.autoTagDefaultsLoaded = false;
        try {
            const response = await fetch("/api/autotag/defaults");
            if (!response.ok) {
                throw new Error(await readApiErrorMessage(response));
            }
            const defaults = await response.json().catch(() => null);
            state.autoTagDefaults = normalizeAutoTagDefaults(defaults);
        } finally {
            state.autoTagDefaultsLoaded = true;
            applyAutoTagDefaultsToUi();
        }
    }

    async function saveEnhancementDefaults() {
        const button = el("saveEnhancementDefaults");
        if (button) {
            button.disabled = true;
        }
        try {
            if (!state.autoTagDefaultsLoaded) {
                await loadAutoTagDefaults();
            }
            const defaults = normalizeAutoTagDefaults(state.autoTagDefaults);
            const renameFoldersCheckbox = el("enhancementRenameSpotifyArtistFolders");
            const renameSpotifyArtistFolders = renameFoldersCheckbox instanceof HTMLInputElement
                ? renameFoldersCheckbox.checked
                : defaults.renameSpotifyArtistFolders !== false;
            const recentDownloadWindowDays = readRecentDownloadWindowDays();
            const recentDownloadWindowHours = convertRecentWindowDaysToHours(recentDownloadWindowDays);
            const response = await fetch("/api/autotag/defaults", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    defaultFileProfile: defaults.defaultFileProfile,
                    librarySchedules: defaults.librarySchedules,
                    recentDownloadWindowHours,
                    renameSpotifyArtistFolders
                })
            });
            if (!response.ok) {
                throw new Error(await readApiErrorMessage(response));
            }
            const saved = await response.json().catch(() => null);
            state.autoTagDefaults = normalizeAutoTagDefaults(saved);
            state.autoTagDefaultsDirty = false;
            applyAutoTagDefaultsToUi();
            setEnhancementStatus("enhancementRecentDownloadWindowStatus", "Enhancement defaults saved.");
            showToast("Enhancement defaults saved.", "success");
        } catch (error) {
            setEnhancementStatus(
                "enhancementRecentDownloadWindowStatus",
                `Failed to save enhancement defaults: ${error?.message || error}`);
            showToast(`Failed to save enhancement defaults: ${error?.message || error}`, "error");
        } finally {
            if (button) {
                button.disabled = false;
            }
        }
    }

    function getProfileAutoTagSnapshot(profile) {
        const rawAutoTag = profile?.autoTag?.data || profile?.autoTag || {};
        if (!rawAutoTag || typeof rawAutoTag !== "object") {
            return readConfigFromUI();
        }

        const snapshot = structuredClone(rawAutoTag);
        delete snapshot.data;
        return {
            ...snapshot,
            ...readConfigFromUI()
        };
    }

    function buildProfileUpsertPayload({ existing = null, profileId = null, name, isDefault = false }) {
        return {
            id: profileId,
            name,
            isDefault,
            tagConfig: null,
            autoTag: getProfileAutoTagSnapshot(existing),
            folderStructure: readFolderStructureFromUI(),
            technical: readTechnicalSettingsFromUI(existing?.technical || null),
            verification: structuredClone(existing?.verification || null)
        };
    }

    function resolveProfileSaveTarget(name, selectedProfile, activeProfile) {
        const normalizedName = typeof name === "string" ? name.trim() : "";
        const selectedName = String(selectedProfile?.name || "").trim();
        const activeName = String(activeProfile?.name || "").trim();
        const selectedMatchesName = selectedProfile && selectedName.localeCompare(normalizedName, undefined, { sensitivity: "accent" }) === 0;
        const activeMatchesName = activeProfile && activeName.localeCompare(normalizedName, undefined, { sensitivity: "accent" }) === 0;

        if (selectedMatchesName) {
            return {
                existing: selectedProfile,
                profileId: selectedProfile.id || state.activeProfileId || null,
                isDefault: Boolean(selectedProfile?.isDefault)
            };
        }

        if (activeMatchesName) {
            return {
                existing: activeProfile,
                profileId: activeProfile.id || state.activeProfileId || null,
                isDefault: Boolean(activeProfile?.isDefault)
            };
        }

        const sameNameProfile = getProfileByName(normalizedName);
        if (sameNameProfile) {
            return {
                existing: sameNameProfile,
                profileId: sameNameProfile.id || null,
                isDefault: Boolean(sameNameProfile?.isDefault)
            };
        }

        return {
            existing: null,
            profileId: null,
            isDefault: false
        };
    }

    async function upsertProfileFromUi(options = {}) {
        const { silent = false, requireActiveProfile = false } = options;
        const nameInput = el("autotag-profile-name");
        const currentActive = getActiveProfile();
        const selected = getSelectedProfile();
        const name = nameInput?.value.trim() || selected?.name?.trim() || currentActive?.name?.trim() || "";
        if (requireActiveProfile && !state.activeProfileId) {
            throw new Error("Select and load a profile first.");
        }
        if (!name) {
            throw new Error("Profile name is required.");
        }

        const saveTarget = resolveProfileSaveTarget(name, selected, currentActive);

        const response = await fetch("/api/tagging/profiles", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(buildProfileUpsertPayload({
                existing: saveTarget.existing,
                profileId: saveTarget.profileId,
                name,
                isDefault: saveTarget.isDefault
            }))
        });
        if (!response.ok) {
            throw new Error(await readApiErrorMessage(response));
        }

        const savedProfile = await response.json().catch(() => null);
        const savedProfileId = savedProfile?.id || savedProfile?.Id || saveTarget.profileId || null;
        const nextProfiles = Array.isArray(state.profiles) ? [...state.profiles] : [];
        let replaceIndex = -1;
        if (savedProfileId) {
            replaceIndex = nextProfiles.findIndex((profile) => String(profile?.id || "").toLowerCase() === String(savedProfileId).toLowerCase());
        }
        if (replaceIndex < 0) {
            replaceIndex = nextProfiles.findIndex((profile) => String(profile?.name || "").toLowerCase() === name.toLowerCase());
        }
        if (savedProfile) {
            if (replaceIndex >= 0) {
                nextProfiles[replaceIndex] = savedProfile;
            } else {
                nextProfiles.push(savedProfile);
            }
        }
        state.profileSaveDirty = false;
        state.profiles = nextProfiles;
        renderProfileSelect();

        const resolvedProfile = (savedProfileId ? getProfileById(savedProfileId) : null)
            || state.profiles.find((profile) => String(profile?.name || "").toLowerCase() === name.toLowerCase())
            || null;
        if (resolvedProfile) {
            applyLoadedProfile(resolvedProfile);
        } else if (nameInput) {
            nameInput.value = name;
        }

        await initAutoTagDefaultsPanel();
        if (!silent) {
            showToast(`Profile "${name}" saved.`, "success");
        }
        return resolvedProfile;
    }

    async function saveProfile() {
        const name = el("autotag-profile-name")?.value?.trim() || "profile";
        try {
            await upsertProfileFromUi({ silent: false });
        } catch (error) {
            showToast(`Failed to save profile "${name}": ${error?.message || error}`, "error");
        }
    }

    function getSelectedProfile() {
        const select = el("autotag-profile-select");
        const id = select?.value;
        if (!id) {
            return null;
        }
        return getProfileById(id);
    }

    function getProfileByName(profileName) {
        const normalized = typeof profileName === "string" ? profileName.trim() : "";
        if (!normalized) {
            return null;
        }
        return state.profiles.find((profile) =>
            String(profile?.name || "").trim().toLowerCase() === normalized.toLowerCase()) || null;
    }

    async function promptForCopiedProfileName(profile) {
        const currentName = profile?.name?.trim() || "Profile";
        const suggestedName = `${currentName} Copy`;
        const ui = globalThis.DeezSpoTag?.ui;
        if (!ui || typeof ui.prompt !== "function") {
            throw new Error("The app modal prompt is unavailable.");
        }

        const nextName = await ui.prompt("Enter a name for the copied profile.", {
            title: "Copy Profile",
            placeholder: suggestedName,
            value: suggestedName,
            okText: "Copy Profile",
            cancelText: "Cancel",
            autocomplete: "off"
        });

        if (nextName === null) {
            return null;
        }

        return String(nextName).trim();
    }

    async function copyProfile() {
        try {
            const profile = getActiveProfile() || getSelectedProfile();
            if (!profile) {
                showToast("Load a profile before copying it.", "error");
                return;
            }

            const copiedName = await promptForCopiedProfileName(profile);
            if (copiedName === null) {
                return;
            }
            if (!copiedName) {
                showToast("Copied profile name is required.", "error");
                return;
            }
            if (copiedName.toLowerCase() === String(profile.name || "").trim().toLowerCase()) {
                showToast("Rename the copied profile before saving it.", "error");
                return;
            }
            if (getProfileByName(copiedName)) {
                showToast(`A profile named "${copiedName}" already exists.`, "error");
                return;
            }

            const response = await fetch(`/api/tagging/profiles/${encodeURIComponent(profile.id)}/copy`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ name: copiedName })
            });
            if (!response.ok) {
                throw new Error(await readApiErrorMessage(response));
            }

            const savedProfile = await response.json().catch(() => null);
            if (!savedProfile) {
                throw new Error("Profile copy returned no data.");
            }

            state.profiles = [...(Array.isArray(state.profiles) ? state.profiles : []), savedProfile];
            state.profileSaveDirty = false;
            renderProfileSelect();
            applyLoadedProfile(savedProfile);
            await initAutoTagDefaultsPanel();
            showToast(`Profile copied to "${copiedName}".`, "success");
        } catch (error) {
            showToast(`Failed to copy profile: ${error?.message || error}`, "error");
        }
    }

    function loadProfile(options = {}) {
        const { profileId = null, silent = false } = options;
        const profile = profileId ? getProfileById(profileId) : getSelectedProfile();
        if (!profile) {
            const select = el("autotag-profile-select");
            if (select && state.activeProfileId) {
                select.value = state.activeProfileId;
            }
            if (!silent) {
                showToast("Select a profile to load.", "error");
            }
            return false;
        }
        applyLoadedProfile(profile);
        if (!silent) {
            showToast(`Profile "${profile.name}" loaded.`, "success");
        }
        return true;
    }

    async function deleteProfile() {
        const profile = getSelectedProfile();
        if (!profile) {
            showToast("Select a profile to delete.", "error");
            return;
        }
        const deletedActiveProfile = String(state.activeProfileId || "").toLowerCase() === String(profile.id || "").toLowerCase();
        const response = await fetch(`/api/tagging/profiles/${encodeURIComponent(profile.id)}`, {
            method: "DELETE"
        });
        if (!response.ok) {
            showToast("Failed to delete profile.", "error");
            return;
        }
        if (deletedActiveProfile) {
            setActiveProfileId(null);
        }
        await loadProfiles();
        await initAutoTagDefaultsPanel();
        if (deletedActiveProfile) {
            activateProfilesTab();
        }
        const nameInput = el("autotag-profile-name");
        if (nameInput && deletedActiveProfile) {
            nameInput.value = "";
        }
        showToast(`Profile "${profile.name}" deleted.`, "success");
    }

    function showToast(message, type = "info") {
        const safeType = ["success", "error", "warning", "info"].includes(type) ? type : "info";
        const toast = document.createElement("div");
        toast.className = `toast toast-${safeType}`;
        toast.textContent = message;
        document.body.appendChild(toast);
        setTimeout(() => toast.remove(), 3000);
    }

    function updateAutoTagStickyOffset() {
        const page = document.querySelector(".autotag-page");
        const stickyShell = el("autotagStickyShell");
        if (!(page instanceof HTMLElement) || !(stickyShell instanceof HTMLElement)) {
            return;
        }

        const topbar = document.querySelector(".main-content > .topbar");
        const topbarHeight = topbar instanceof HTMLElement ? topbar.offsetHeight : 0;
        const stickyOffset = Math.max(0, Math.ceil(topbarHeight));
        page.style.setProperty("--autotag-sticky-top", `${stickyOffset}px`);
    }

    function initializeAutoTagStickyShell() {
        const stickyShell = el("autotagStickyShell");
        if (!(stickyShell instanceof HTMLElement)) {
            return;
        }
        const mainContent = el("mainContent");
        if (mainContent instanceof HTMLElement) {
            mainContent.classList.add("autotag-sticky-enabled");
        }

        const refresh = () => {
            globalThis.requestAnimationFrame(updateAutoTagStickyOffset);
        };

        refresh();
        setTimeout(refresh, 0);
        globalThis.addEventListener("load", refresh);
        globalThis.addEventListener("resize", refresh, { passive: true });
        globalThis.addEventListener("orientationchange", refresh, { passive: true });

        const topbar = document.querySelector(".main-content > .topbar");
        if (topbar instanceof HTMLElement && typeof ResizeObserver !== "undefined") {
            const observer = new ResizeObserver(refresh);
            observer.observe(topbar);
            observer.observe(stickyShell);
        }
    }

    function getTagActionToastMessage(action) {
        if (action === "enable") {
            return "All tags enabled.";
        }
        if (action === "disable") {
            return "All tags disabled.";
        }
        if (action === "toggle") {
            return "Tags toggled.";
        }
        return null;
    }

    function tryApplyTagAction(action, targetName) {
        const current = Array.isArray(state.config[targetName]) ? state.config[targetName] : [];
        const list = targetName === "downloadTags" ? getDownloadTagsList() : TAGS;
        if (action === "enable") {
            state.config[targetName] = list.map((t) => t.tag);
            return true;
        }
        if (action === "disable") {
            state.config[targetName] = [];
            return true;
        }
        if (action === "toggle") {
            state.config[targetName] = list.map((t) => t.tag).filter((tag) => !current.includes(tag));
            return true;
        }
        return false;
    }

    function handleTagsActionButton(target) {
        const action = target.dataset.tagsAction;
        const targetName = target.dataset.tagsTarget || "tags";
        if (targetName === "gapFillTags") {
            syncEnhancementTagsWithDownloadAndEnrichment();
            loadConfigToUI();
            showToast("Enhancement tags mirror Download and Enrichment tags.", "info");
            return;
        }

        if (!tryApplyTagAction(action, targetName)) {
            return;
        }

        syncEnhancementTagsWithDownloadAndEnrichment();
        loadConfigToUI();
        const toastMessage = getTagActionToastMessage(action);
        if (toastMessage) {
            showToast(toastMessage, "info");
        }
    }

    document.addEventListener("change", (event) => {
        const target = event.target;
        if (target instanceof HTMLInputElement || target instanceof HTMLSelectElement) {
            if (target.closest("#autotagTabsContent") && !target.closest("#autotag-folders-panel")) {
                updateConditionalSections();
            }
            if (target instanceof HTMLInputElement
                && target.type === "checkbox"
                && ["tags", "downloadTags", "gapFillTags"].includes(target.name)) {
                state.config.tags = getCheckedTags("tags");
                state.config.downloadTags = normalizeDownloadTags(getCheckedTags("downloadTags"));
                syncEnhancementTagsWithDownloadAndEnrichment();
                renderTags("gap-fill-tags", state.config.gapFillTags || [], "gapFillTags");
            }
        }
        if (event.isTrusted && isProfileAutoSaveTarget(event.target)) {
            scheduleProfileAutoSave();
        }
    });

    document.addEventListener("input", (event) => {
        if (event.isTrusted && isProfileAutoSaveTarget(event.target)) {
            scheduleProfileAutoSave();
        }
    });

    document.addEventListener("click", (event) => {
        const target = event.target;
        if (!(target instanceof HTMLElement)) {
            return;
        }

        if (target.matches("button[data-tags-action]")) {
            handleTagsActionButton(target);
        }
    });

    const viewStatusLink = document.querySelector('.autotag-actions a[href^="/Activities"]');
    if (viewStatusLink) {
        viewStatusLink.addEventListener("click", () => {
            showToast("Opening AutoTag status.", "info");
        });
    }

    ensureRecentDownloadWindowControls();
    el("autotag-start")?.addEventListener("click", startAutoTag);
    el("autotag-stop")?.addEventListener("click", stopAutoTag);
    el("autotag-reset")?.addEventListener("click", resetForm);
    el("autotag-save")?.addEventListener("click", savePreferences);
    el("autotag-profile-select")?.addEventListener("change", () => {
        const selectedProfile = getSelectedProfile();
        const nameInput = el("autotag-profile-name");
        const select = el("autotag-profile-select");
        if (!selectedProfile) {
            if (select && state.activeProfileId) {
                select.value = state.activeProfileId;
            }
            return;
        }
        if (nameInput && !nameInput.matches(":focus")) {
            nameInput.value = selectedProfile.name || "";
        }
        loadProfile({ profileId: selectedProfile.id, silent: true });
    });
    el("autotag-profile-save")?.addEventListener("click", saveProfile);
    el("autotag-profile-load")?.addEventListener("click", loadProfile);
    el("autotag-profile-copy")?.addEventListener("click", copyProfile);
    el("autotag-profile-delete")?.addEventListener("click", deleteProfile);
    el("runCoverMaintenance")?.addEventListener("click", runCoverMaintenance);
    el("runFolderUniformity")?.addEventListener("click", runFolderUniformity);
    el("runEnhancementQualityChecks")?.addEventListener("click", runEnhancementQualityChecks);
    el("saveEnhancementDefaults")?.addEventListener("click", saveEnhancementDefaults);
    el("enhancementRenameSpotifyArtistFolders")?.addEventListener("change", () => {
        state.autoTagDefaultsDirty = true;
        setEnhancementStatus("enhancementRecentDownloadWindowStatus", "Enhancement defaults have unsaved changes.");
    });
    el("enhancementRecentDownloadWindowDays")?.addEventListener("input", () => {
        state.autoTagDefaultsDirty = true;
        setEnhancementStatus("enhancementRecentDownloadWindowStatus", "Enhancement defaults have unsaved changes.");
    });
    [
        "saveAnimatedArtwork"
    ].forEach((id) => {
        const field = el(id);
        if (field instanceof HTMLInputElement) {
            field.addEventListener("change", () => {
                setAnimatedArtworkControls(field.checked);
                ensureCustomDefaults();
                state.config.custom.itunes.animated_artwork = field.checked;
            });
        }
    });
    ["metadataSourceEngineEnabled", "metadataSourceDeezerEnabled", "metadataSourceSpotifyEnabled"].forEach((id) => {
        const field = el(id);
        if (field) {
            field.addEventListener("change", () => {
                enforceSingleDownloadSource(id);
                ensureEffectivePlatforms(state.config);
                renderPlatforms();
                refreshDownloadTagsForSource();
            });
        }
    });
    ["autotag-move-success-library"].forEach((id) => {
        const field = el(id);
        if (field) {
            field.addEventListener("change", updateConditionalSections);
        }
    });
    const autotagMoveFailedPath = el("autotag-move-failed-path");
    const browseAutoTagFailedPath = el("browseAutoTagFailedPath");
    if (browseAutoTagFailedPath && autotagMoveFailedPath && globalThis.DeezSpoTag?.ui?.browseServerFolder) {
        browseAutoTagFailedPath.addEventListener("click", async () => {
            const selected = await globalThis.DeezSpoTag.ui.browseServerFolder({
                title: "Failed/Conflict Files Destination",
                startPath: autotagMoveFailedPath.value || "",
                apiPath: "/api/library/folders/browse",
                selectText: "Use This Folder"
            });
            if (selected) {
                autotagMoveFailedPath.value = selected;
                autotagMoveFailedPath.dispatchEvent(new Event("input", { bubbles: true }));
                autotagMoveFailedPath.dispatchEvent(new Event("change", { bubbles: true }));
            }
        });
    }
    ["createPlaylistFolder", "createArtistFolder", "createAlbumFolder"].forEach((id) => {
        const field = el(id);
        if (field) {
            field.addEventListener("change", updateFolderStructureVisibility);
        }
    });
    ["createCDFolder", "createStructurePlaylist", "createSingleFolder"].forEach((id) => {
        const field = el(id);
        if (field) {
            field.addEventListener("change", renderFolderStructurePreview);
        }
    });
    ["singleAlbumArtist"].forEach((id) => {
        const field = el(id);
        if (field) {
            field.addEventListener("change", syncMultiArtistHandlingState);
        }
    });
    [
        "playlistNameTemplate",
        "artistNameTemplate",
        "albumNameTemplate",
        "illegalCharacterReplacer",
        "autotag-trackname-template",
        "autotag-album-trackname-template",
        "autotag-playlist-trackname-template"
    ].forEach((id) => {
        const field = el(id);
        if (field) {
            field.addEventListener("input", renderFolderStructurePreview);
            field.addEventListener("change", renderFolderStructurePreview);
        }
    });
    updateFolderStructureVisibility();
    renderFolderStructurePreview();
    setupLyricsFallbackOrder();
    setupArtworkFallbackOrder();
    setupArtistArtworkFallbackOrder();
    setupFallbackSourceSelectors();
    setupDownloadTagsUi();
    setupTemplateVariableHelpers();
    loadLyricsSettings();
    initializeAutoTagStickyShell();
    bindProfileTabNavigationGuards();
    bindEnhancementTabLastScanBanner();
    setActiveProfileId(loadStoredActiveProfileId(), { persist: false });
    restoreFolderUniformityStatusSnapshot();
    if (isEnhancementTabActive() && !getActiveFolderUniformityJobId()) {
        showFolderUniformityLastScanBanner();
    }

    Promise.all([loadPlatforms(), loadEnrichmentLibraryFolders()]).then(async () => {
        loadConfigToUI();
        enforceSingleDownloadSource();
        refreshDownloadTagsForSource();
        ensurePlatformCustomDefaults();
        await loadProfiles();
        if (!Array.isArray(state.profiles) || state.profiles.length === 0) {
            const storedPreferences = loadStoredPreferences();
            if (storedPreferences) {
                applyProfileConfig(storedPreferences);
                enforceSingleDownloadSource();
                refreshDownloadTagsForSource();
            }
        }
        await initAutoTagDefaultsPanel();
        await resumeFolderUniformityRunIfNeeded();
        const recoveredJobId = await resolveActiveAutoTagJobId();
        if (recoveredJobId && hasStatusUI()) {
            schedulePoll();
        }
        loadStoredAuth().then((data) => {
            mergeStoredAuth(state.config, data);
            loadSpotifyStatus().then(() => {
                renderPlatforms();
                updateDownloadSourceAvailability();
                refreshDownloadTagsForSource();
                syncEnhancementTagsWithDownloadAndEnrichment();
                renderTags("autotag-tags", state.config.tags, "tags");
                renderTags("gap-fill-tags", state.config.gapFillTags || [], "gapFillTags");
                renderTags("autotag-overwrite-tags", state.config.overwriteTags, "overwriteTags");
            });
        });
    });
})();
