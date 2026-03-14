(() => {
    const root = document.getElementById("quicktag-app");
    if (!root) {
        return;
    }

    const API_BASE = "/api/quicktag";
    const LIBRARIES_API = "/api/library/folders";
    const QUICKTAG_PATH_KEY = "deezspotag.quicktag.path";
    const QUICKTAG_COLUMNS_KEY = "deezspotag.quicktag.columns";
    const QUICKTAG_COLUMN_PRESET_KEY = "deezspotag.quicktag.columnPreset";
    const QUICKTAG_SOURCE_TEMPLATE_KEY = "deezspotag.quicktag.sourceTemplate";
    const QUICKTAG_TAG_SOURCE_PROVIDER_KEY = "deezspotag.quicktag.tagSourceProvider";

    const DEFAULT_SEPARATORS = {
        id3: ", ",
        vorbis: null,
        mp4: ", "
    };

    const MOODS = [
        { mood: "Happy" },
        { mood: "Sad" },
        { mood: "Bright" },
        { mood: "Dark" },
        { mood: "Angry" },
        { mood: "Chill" },
        { mood: "Lovely" },
        { mood: "Powerful" },
        { mood: "Sexy" }
    ];

    const GENRES = [
        { genre: "2-step", subgenres: [] },
        { genre: "Acid", subgenres: [] },
        { genre: "Breakbeat", subgenres: [] },
        { genre: "Disco", subgenres: [] },
        { genre: "Drum & Bass", subgenres: [] },
        { genre: "Electro", subgenres: ["House", "Dubstep", "EDM"] },
        { genre: "Funk", subgenres: [] },
        { genre: "Hardcore", subgenres: [] },
        { genre: "Hiphop", subgenres: [] },
        { genre: "House", subgenres: [] },
        { genre: "Industrial", subgenres: [] },
        { genre: "Jungle", subgenres: [] },
        { genre: "Latin", subgenres: [] },
        { genre: "Minimal", subgenres: [] },
        { genre: "Nu-Disco", subgenres: [] },
        { genre: "Oldies", subgenres: [] },
        { genre: "Pop", subgenres: [] },
        { genre: "Reggae", subgenres: [] },
        { genre: "Rock", subgenres: [] },
        { genre: "Techno", subgenres: [] },
        { genre: "Trance", subgenres: [] }
    ];

    const CUSTOM_GROUPS = [
        {
            name: "Vibe",
            tag: { id3: "COMM", vorbis: "COMMENT", mp4: "\u00a9cmt" },
            values: ["Afro", "Asian", "Arabic", "Classic", "Dirty", "Etnic", "Funky", "Gangsta", "Glitchy", "Melodic", "Sensual", "Soulful"]
        },
        {
            name: "Situation",
            tag: { id3: "COMM", vorbis: "COMMENT", mp4: "\u00a9cmt" },
            values: ["Start", "Build", "Peak", "Sustain", "Release"]
        },
        {
            name: "Instruments",
            tag: { id3: "COMM", vorbis: "COMMENT", mp4: "\u00a9cmt" },
            values: ["Vocals", "Bass Heavy", "Congas", "Guitar", "Horns", "Organ", "Piano", "Strings", "Sax"]
        }
    ];

    const COLUMN_DEFS = [
        { key: "title", label: "Title", width: "260px", required: true },
        { key: "artist", label: "Artist", width: "190px" },
        { key: "album", label: "Album", width: "190px" },
        { key: "album-artist", label: "Album Artist", width: "190px" },
        { key: "composer", label: "Composer", width: "170px" },
        { key: "mood", label: "Mood", width: "120px" },
        { key: "energy", label: "Energy", width: "120px" },
        { key: "genre", label: "Genre", width: "190px" },
        { key: "year", label: "Year", width: "84px" },
        { key: "track", label: "Track", width: "84px" },
        { key: "disc", label: "Disc", width: "84px" },
        { key: "bpm", label: "BPM", width: "84px" },
        { key: "key", label: "Key", width: "84px" },
        { key: "publisher", label: "Publisher", width: "170px" },
        { key: "copyright", label: "Copyright", width: "170px" },
        { key: "language", label: "Language", width: "100px" },
        { key: "lyricist", label: "Lyricist", width: "170px" },
        { key: "conductor", label: "Conductor", width: "170px" },
        { key: "isrc", label: "ISRC", width: "140px" },
        { key: "custom", label: "Custom", width: "320px" },
        { key: "filename", label: "Filename", width: "200px", readOnly: true },
        { key: "bitrate", label: "Bitrate", width: "84px", readOnly: true },
        { key: "duration", label: "Duration", width: "84px", readOnly: true },
        { key: "filesize", label: "Size", width: "84px", readOnly: true }
    ];

    const COLUMN_PRESETS = {
        "puddletag-full": COLUMN_DEFS.map((column) => column.key),
        "quicktag-core": ["title", "artist", "album", "mood", "energy", "genre", "year", "track", "disc", "bpm", "key", "isrc", "custom"],
        "library-review": ["title", "artist", "album", "album-artist", "genre", "year", "track", "disc", "custom"]
    };

    const SOURCE_FIELD_KEYS = [
        "title", "artist", "album", "album_artist", "composer", "genre", "year",
        "track", "disc", "bpm", "key", "mood", "comment", "lyrics", "isrc", "grouping",
        "publisher", "copyright", "language", "lyricist", "conductor",
        "danceability", "energy_feature", "valence", "acousticness", "instrumentalness",
        "speechiness", "loudness", "tempo_feature", "time_signature", "liveness"
    ];

    const SOURCE_FIELD_MAPS = {
        id3: buildSourceFieldMap([
            "TIT2", "TPE1", "TALB", "TPE2", "TCOM", "TCON", "TDRC",
            "TRCK", "TPOS", "TBPM", "TKEY", "TMOO", "COMM", "USLT", "TSRC", "TIT1",
            "TPUB", "TCOP", "TLAN", "TEXT", "TPE3",
            "DANCEABILITY", "ENERGY", "VALENCE", "ACOUSTICNESS", "INSTRUMENTALNESS",
            "SPEECHINESS", "LOUDNESS", "TEMPO", "TIME_SIGNATURE", "LIVENESS"
        ]),
        vorbis: buildSourceFieldMap([
            "TITLE", "ARTIST", "ALBUM", "ALBUMARTIST", "COMPOSER", "GENRE", "DATE",
            "TRACKNUMBER", "DISCNUMBER", "BPM", "INITIALKEY", "MOOD", "COMMENT", "LYRICS", "ISRC", "GROUPING",
            "PUBLISHER", "COPYRIGHT", "LANGUAGE", "LYRICIST", "CONDUCTOR",
            "DANCEABILITY", "ENERGY", "VALENCE", "ACOUSTICNESS", "INSTRUMENTALNESS",
            "SPEECHINESS", "LOUDNESS", "TEMPO", "TIME_SIGNATURE", "LIVENESS"
        ]),
        mp4: buildSourceFieldMap([
            "\u00a9nam", "\u00a9ART", "\u00a9alb", "aART", "\u00a9wrt", "\u00a9gen", "\u00a9day",
            "trkn", "disk", "tmpo", "iTunes:KEY", "iTunes:MOOD", "\u00a9cmt", "\u00a9lyr", "iTunes:ISRC", "\u00a9grp",
            "iTunes:PUBLISHER", "cprt", "iTunes:LANGUAGE", "iTunes:LYRICIST", "iTunes:CONDUCTOR",
            "iTunes:DANCEABILITY", "iTunes:ENERGY", "iTunes:VALENCE", "iTunes:ACOUSTICNESS", "iTunes:INSTRUMENTALNESS",
            "iTunes:SPEECHINESS", "iTunes:LOUDNESS", "iTunes:TEMPO", "iTunes:TIME_SIGNATURE", "iTunes:LIVENESS"
        ])
    };

    const SOURCE_FIELD_OPTIONS = [...SOURCE_FIELD_KEYS];

    const TAG_SOURCE_PROVIDERS = {
        spotify: "Spotify",
        deezer: "Deezer",
        boomplay: "Boomplay",
        apple: "Apple Music",
        shazam: "Shazam",
        musicbrainz: "MusicBrainz",
        acoustid: "AcoustID",
        amazon: "Amazon",
        discogs: "Discogs"
    };

    const state = {
        path: "",
        browserPath: "",
        libraries: [],
        selectedLibraryRoot: "",
        librarySelectionMode: true,
        folders: [],
        tracks: [],
        filteredTracks: [],
        selectedPaths: new Set(),
        primaryPath: null,
        visibleColumns: new Set(COLUMN_PRESETS["puddletag-full"]),
        columnPreset: "puddletag-full",
        folderFilter: "",
        search: "",
        sortField: "title",
        sortDescending: false,
        sourceTemplate: "auto",
        tagSourceProvider: "spotify",
        tagSourceDetail: null,
        artworkViewIndex: 0,
        artworkClipboard: null,
        artworkFileMode: "add",
        failed: [],
        loadingFolder: false,
        loadingTracks: false,
        saving: false
    };

    const dom = {
        librarySelect: document.getElementById("qtLibrarySelect"),
        pathInput: document.getElementById("qtPathInput"),
        pathUpBtn: document.getElementById("qtPathUp"),
        pathReloadBtn: document.getElementById("qtPathReload"),
        folderFilterInput: document.getElementById("qtFolderFilter"),
        folderList: document.getElementById("qtFolderList"),
        artworkPreview: document.getElementById("qtArtworkPreview"),
        artworkPlaceholder: document.getElementById("qtArtworkPlaceholder"),
        artworkContext: document.getElementById("qtArtworkContext"),
        artworkMeta: document.getElementById("qtArtworkMeta"),
        artworkPrevBtn: document.getElementById("qtArtworkPrev"),
        artworkNextBtn: document.getElementById("qtArtworkNext"),
        artworkKeepBtn: document.getElementById("qtArtworkKeep"),
        artworkBlankBtn: document.getElementById("qtArtworkBlank"),
        artworkDescription: document.getElementById("qtArtworkDescription"),
        artworkType: document.getElementById("qtArtworkType"),
        artworkAddBtn: document.getElementById("qtArtworkAdd"),
        artworkChangeBtn: document.getElementById("qtArtworkChange"),
        artworkRemoveBtn: document.getElementById("qtArtworkRemove"),
        artworkCopyBtn: document.getElementById("qtArtworkCopy"),
        artworkPasteBtn: document.getElementById("qtArtworkPaste"),
        artworkSaveToFileBtn: document.getElementById("qtArtworkSaveToFile"),
        artworkFileInput: document.getElementById("qtArtworkFile"),
        searchInput: document.getElementById("qtSearch"),
        sortDirectionBtn: document.getElementById("qtSortDirection"),
        sortChips: Array.from(document.querySelectorAll(".qt-sort-chip[data-sort]")),
        gridViewport: document.getElementById("qtGridViewport"),
        mainPanel: document.querySelector(".qt-main-panel"),
        stats: document.getElementById("qtStats"),
        emptyState: document.getElementById("qtEmptyState"),
        trackList: document.getElementById("qtTrackList"),
        customGroups: document.getElementById("qtCustomGroups"),
        columnPreset: document.getElementById("qtColumnPreset"),
        columnToggles: document.getElementById("qtColumnToggles"),
        moodBar: document.getElementById("qtMoodBar"),
        genreBar: document.getElementById("qtGenreBar"),
        addNoteBtn: document.getElementById("qtAddNote"),
        sortValuesBtn: document.getElementById("qtSortValues"),
        dumpSelectedBtn: document.getElementById("qtDumpSelected"),
        saveSelectedBtn: document.getElementById("qtSaveSelected"),
        sourceTemplate: document.getElementById("qtSourceTemplate"),
        sourceFilter: document.getElementById("qtSourceFilter"),
        sourceTags: document.getElementById("qtSourceTags"),
        tagSourceProvider: document.getElementById("qtTagSourceProvider"),
        tagSourceSearchBtn: document.getElementById("qtTagSourceSearch"),
        tagSourceStatus: document.getElementById("qtTagSourceStatus"),
        tagSourceResults: document.getElementById("qtTagSourceResults"),
        tagSourceResultMeta: document.getElementById("qtTagSourceResultMeta"),
        tagSourceApplyCover: document.getElementById("qtTagSourceApplyCover"),
        tagSourceApplyAll: document.getElementById("qtTagSourceApplyAll"),
        prevTrackBtn: document.getElementById("qtPrevTrack"),
        nextTrackBtn: document.getElementById("qtNextTrack"),
        playPauseBtn: document.getElementById("qtPlayPause"),
        seek: document.getElementById("qtSeek"),
        volume: document.getElementById("qtVolume"),
        playerMeta: document.getElementById("qtPlayerMeta"),
        audio: document.getElementById("qtAudio")
    };

    const allGenreValues = (() => {
        const list = [];
        const seen = new Set();
        GENRES.forEach((item) => {
            if (!seen.has(item.genre.toLowerCase())) {
                seen.add(item.genre.toLowerCase());
                list.push(item.genre);
            }
            item.subgenres.forEach((sub) => {
                if (!seen.has(sub.toLowerCase())) {
                    seen.add(sub.toLowerCase());
                    list.push(sub);
                }
            });
        });
        return list;
    })();

    function notify(message, type) {
        if (typeof globalThis.DeezSpoTag?.showNotification === "function") {
            globalThis.DeezSpoTag.showNotification(message, type || "info");
            return;
        }

        const toast = document.createElement("div");
        toast.className = "qt-error-inline";
        toast.style.position = "fixed";
        toast.style.top = "16px";
        toast.style.right = "16px";
        toast.style.padding = "10px 14px";
        toast.style.borderRadius = "8px";
        toast.style.zIndex = "3000";
        const styles = getComputedStyle(document.documentElement);
        const errorColor = styles.getPropertyValue("--error-color").trim() || "#ef5350";
        const successColor = styles.getPropertyValue("--primary-color").trim() || "#00bfff";
        const bg = type === "error" ? errorColor : successColor;
        toast.style.background = `color-mix(in srgb, ${bg} 82%, black)`;
        toast.style.color = styles.getPropertyValue("--bg-primary").trim() || "#0a0f1c";
        toast.textContent = message;
        document.body.appendChild(toast);
        setTimeout(() => toast.remove(), 2800);
    }

    function buildSourceFieldMap(values) {
        return Object.fromEntries(SOURCE_FIELD_KEYS.map((key, index) => [key, values[index]]));
    }

    function getSourceProviderLabel(provider) {
        return provider ? TAG_SOURCE_PROVIDERS[provider] || provider : "Source";
    }

    function resolveSourceDetailValue(detail, col) {
        return detail[col.detailKey] ?? (col.fallbackDetailKey ? detail[col.fallbackDetailKey] : null);
    }

    function formatSourceDetailValue(col, rawValue) {
        return col.format ? String(col.format(rawValue)) : String(rawValue);
    }

    function insertPlainTextAtSelection(target, text) {
        const selection = globalThis.getSelection?.();
        if (!selection || selection.rangeCount === 0) {
            target.textContent = `${target.textContent || ""}${text}`;
            return;
        }

        const range = selection.getRangeAt(0);
        if (!target.contains(range.commonAncestorContainer)) {
            target.textContent = `${target.textContent || ""}${text}`;
            return;
        }

        range.deleteContents();
        const node = document.createTextNode(text);
        range.insertNode(node);
        range.setStartAfter(node);
        range.collapse(true);
        selection.removeAllRanges();
        selection.addRange(range);
    }

    async function requestJson(url, options) {
        const response = await fetch(url, options);
        let data = null;
        try {
            data = await response.json();
        } catch {
            data = null;
        }

        if (!response.ok) {
            const message = data && (data.error || data.message) ? (data.error || data.message) : `Request failed (${response.status})`;
            throw new Error(message);
        }

        return data;
    }

    function normalizePath(path) {
        return (path || "").trim();
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
        const normalized = normalizePath(path).replaceAll("\\", "/");
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

    async function showTagDump(includeArtworkData = false) {
        const track = getPrimaryTrack();
        if (!track) {
            notify("Select a track first.", "warning");
            return;
        }

        const qs = new URLSearchParams();
        qs.set("path", track.path);
        if (includeArtworkData) {
            qs.set("includeArtworkData", "true");
        }

        const payload = await requestJson(`${API_BASE}/dump?${qs.toString()}`);
        const pretty = JSON.stringify(payload, null, 2);

        const container = document.createElement("div");
        const toolbar = document.createElement("div");
        toolbar.style.display = "flex";
        toolbar.style.flexWrap = "wrap";
        toolbar.style.gap = "8px";
        toolbar.style.marginBottom = "10px";

        const copyBtn = document.createElement("button");
        copyBtn.type = "button";
        copyBtn.className = "qt-btn qt-btn-outline";
        copyBtn.textContent = "Copy JSON";
        copyBtn.addEventListener("click", async () => {
            try {
                await navigator.clipboard.writeText(pretty);
                notify("Dump copied to clipboard.", "success");
            } catch {
                notify("Copy failed.", "error");
            }
        });
        toolbar.appendChild(copyBtn);

        if (!includeArtworkData) {
            const includeBtn = document.createElement("button");
            includeBtn.type = "button";
            includeBtn.className = "qt-btn qt-btn-outline";
            includeBtn.textContent = "Include artwork data";
            includeBtn.addEventListener("click", () => {
                showTagDump(true).catch((error) => {
                    notify(error.message || "Failed to load tag dump.", "error");
                });
            });
            toolbar.appendChild(includeBtn);
        }

        const note = document.createElement("div");
        note.style.fontSize = "12px";
        note.style.opacity = "0.8";
        note.textContent = includeArtworkData
            ? "Artwork data included (base64)."
            : "Artwork data omitted for size. Click \"Include artwork data\" to embed base64.";

        const pre = document.createElement("pre");
        pre.textContent = pretty;
        pre.style.whiteSpace = "pre-wrap";
        pre.style.maxHeight = "60vh";
        pre.style.overflow = "auto";
        pre.style.padding = "12px";
        pre.style.margin = "0";
        pre.style.borderRadius = "10px";
        pre.style.background = "rgba(0, 0, 0, 0.35)";
        pre.style.border = "1px solid rgba(255, 255, 255, 0.08)";

        container.appendChild(toolbar);
        container.appendChild(note);
        container.appendChild(pre);

        if (globalThis.DeezSpoTag?.ui?.showModal) {
            globalThis.DeezSpoTag.ui.showModal({
                title: "Tag Dump",
                message: "",
                allowHtml: true,
                contentElement: container,
                buttons: [{ label: "Close", value: true, primary: true }]
            });
        } else {
            const w = globalThis.open("", "_blank", "noopener");
            if (!w) {
                notify("Popup blocked.", "warning");
                return;
            }
            const preElement = w.document.createElement("pre");
            preElement.textContent = pretty;
            w.document.title = "Tag Dump";
            w.document.body.replaceChildren(preElement);
        }
    }

    function samePath(a, b) {
        return normalizePathKey(a) === normalizePathKey(b);
    }

    function pathWithinRoot(path, rootPath) {
        const normalizedPath = normalizePath(path).replaceAll("\\", "/");
        const normalizedRoot = trimTrailingPathSeparators(normalizePath(rootPath).replaceAll("\\", "/"));
        if (!normalizedPath || !normalizedRoot) {
            return false;
        }

        const pathLower = normalizedPath.toLowerCase();
        const rootLower = normalizedRoot.toLowerCase();
        return pathLower === rootLower || pathLower.startsWith(`${rootLower}/`);
    }

    function isTextInputActive() {
        const active = document.activeElement;
        if (!active) {
            return false;
        }

        const tag = active.tagName.toLowerCase();
        return tag === "input" || tag === "textarea" || active.isContentEditable;
    }

    function escapeHtml(value) {
        return String(value || "")
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll('"', "&quot;")
            .replaceAll("'", "&#39;");
    }

    function moodToneClass(mood) {
        const key = String(mood || "").trim().toLowerCase();
        switch (key) {
            case "happy":
            case "sad":
            case "bright":
            case "dark":
            case "angry":
            case "relaxed":
            case "lovely":
            case "powerful":
            case "sexy":
                return `qt-mood-tone-${key}`;
            default:
                return "qt-mood-tone-default";
        }
    }

    function resolveTemplateByFormat(format) {
        const normalized = String(format || "").toLowerCase();
        if (normalized === "flac" || normalized === "ogg") {
            return "vorbis";
        }

        if (normalized === "mp4") {
            return "mp4";
        }

        return "id3";
    }

    function getEffectiveSourceTemplate(track) {
        if (state.sourceTemplate !== "auto") {
            return state.sourceTemplate;
        }

        return resolveTemplateByFormat(track ? track.format : "mp3");
    }

    function normalizeVisibleColumns(input) {
        const allowed = new Set(COLUMN_DEFS.map((column) => column.key));
        const output = new Set();
        (Array.isArray(input) ? input : []).forEach((key) => {
            if (allowed.has(key)) {
                output.add(key);
            }
        });

        COLUMN_DEFS.forEach((column) => {
            if (column.required) {
                output.add(column.key);
            }
        });

        if (output.size === 0) {
            COLUMN_PRESETS["puddletag-full"].forEach((key) => output.add(key));
        }

        return output;
    }

    function updateColumnLayoutVariable() {
        if (!dom.mainPanel) {
            return;
        }

        const widths = COLUMN_DEFS
            .filter((column) => state.visibleColumns.has(column.key))
            .map((column) => column.width);
        if (widths.length === 0) {
            return;
        }

        dom.mainPanel.style.setProperty("--qt-track-columns", widths.join(" "));
    }

    function applyColumnVisibility() {
        COLUMN_DEFS.forEach((column) => {
            const visible = state.visibleColumns.has(column.key);
            const nodes = root.querySelectorAll(`.qt-col-${column.key}`);
            nodes.forEach((node) => node.classList.toggle("is-col-hidden", !visible));
        });

        updateColumnLayoutVariable();
    }

    function syncColumnPresetSelection() {
        const keys = Object.keys(COLUMN_PRESETS);
        const active = keys.find((presetKey) => {
            const preset = normalizeVisibleColumns(COLUMN_PRESETS[presetKey]);
            if (preset.size !== state.visibleColumns.size) {
                return false;
            }

            for (const key of preset) {
                if (!state.visibleColumns.has(key)) {
                    return false;
                }
            }

            return true;
        });

        state.columnPreset = active || "custom";
        if (dom.columnPreset) {
            dom.columnPreset.value = state.columnPreset === "custom" ? "puddletag-full" : state.columnPreset;
        }
    }

    function saveColumnSettings() {
        const _cols = Array.from(state.visibleColumns);
        localStorage.setItem(QUICKTAG_COLUMNS_KEY, JSON.stringify(_cols));
        if (globalThis.UserPrefs) globalThis.UserPrefs.set('quickTagColumns', _cols);
        if (state.columnPreset !== "custom") {
            localStorage.setItem(QUICKTAG_COLUMN_PRESET_KEY, state.columnPreset);
            if (globalThis.UserPrefs) globalThis.UserPrefs.set('quickTagColumnPreset', state.columnPreset);
        }
    }

    function loadColumnSettings() {
        const preset = localStorage.getItem(QUICKTAG_COLUMN_PRESET_KEY);
        if (preset && COLUMN_PRESETS[preset]) {
            state.columnPreset = preset;
            state.visibleColumns = normalizeVisibleColumns(COLUMN_PRESETS[preset]);
        }

        const raw = localStorage.getItem(QUICKTAG_COLUMNS_KEY);
        if (!raw) {
            return;
        }

        try {
            const parsed = JSON.parse(raw);
            state.visibleColumns = normalizeVisibleColumns(parsed);
        } catch {
            state.visibleColumns = normalizeVisibleColumns(COLUMN_PRESETS[state.columnPreset] || COLUMN_PRESETS["puddletag-full"]);
        }
    }

    function loadSourceSettings() {
        const template = localStorage.getItem(QUICKTAG_SOURCE_TEMPLATE_KEY);
        if (template && (template === "auto" || template === "id3" || template === "vorbis" || template === "mp4")) {
            state.sourceTemplate = template;
        }

        if (dom.sourceTemplate) {
            dom.sourceTemplate.value = state.sourceTemplate;
        }
    }

    function loadTagSourceSettings() {
        const provider = (localStorage.getItem(QUICKTAG_TAG_SOURCE_PROVIDER_KEY) || "").trim().toLowerCase();
        if (provider && Object.hasOwn(TAG_SOURCE_PROVIDERS, provider)) {
            state.tagSourceProvider = provider;
        }

        if (dom.tagSourceProvider) {
            dom.tagSourceProvider.value = state.tagSourceProvider;
        }
    }

    function setTagSourceStatus(message, status) {
        if (!dom.tagSourceStatus) {
            return;
        }

        dom.tagSourceStatus.classList.remove("is-warning", "is-error", "is-success");
        if (status === "warning") {
            dom.tagSourceStatus.classList.add("is-warning");
        } else if (status === "error") {
            dom.tagSourceStatus.classList.add("is-error");
        } else if (status === "success") {
            dom.tagSourceStatus.classList.add("is-success");
        }

        dom.tagSourceStatus.textContent = message;
    }

    function clearTagSourceResults() {
        if (!dom.tagSourceResults) {
            return;
        }

        dom.tagSourceResults.innerHTML = "";
        hideTagSourceDetail();
    }

    function setTagSourceResultMeta(text) {
        if (!dom.tagSourceResultMeta) {
            return;
        }

        dom.tagSourceResultMeta.textContent = text || "";
    }

    // Maps detail API response keys → grid column CSS class and applyFieldValue key.
    // Order matches the grid columns in the track row template.
    const SOURCE_COL_MAP = [
        { col: "title",        detailKey: "title",       field: "title" },
        { col: "artist",       detailKey: "artist",      field: "artist" },
        { col: "album",        detailKey: "album",       field: "album" },
        { col: "album-artist", detailKey: "albumArtist", field: "albumArtist" },
        { col: "composer",     detailKey: "composer",     field: "composer" },
        { col: "mood",         detailKey: null },
        { col: "energy",       detailKey: null },
        { col: "genre",        detailKey: "genre",       field: "genre" },
        { col: "year",         detailKey: "year",        fallbackDetailKey: "date", field: "year" },
        { col: "track",        detailKey: "trackNumber", field: "track" },
        { col: "disc",         detailKey: "discNumber",  field: "disc" },
        { col: "bpm",          detailKey: "bpm",         field: "bpm" },
        { col: "key",          detailKey: "key",         field: "key" },
        { col: "publisher",    detailKey: "publisher",   field: "publisher" },
        { col: "copyright",    detailKey: "copyright",   field: "copyright" },
        { col: "language",     detailKey: "language",     field: "language" },
        { col: "lyricist",     detailKey: "lyricist",    field: "lyricist" },
        { col: "conductor",    detailKey: "conductor",   field: "conductor" },
        { col: "isrc",         detailKey: "isrc",        field: "isrc" },
        { col: "custom",       detailKey: null },
        { col: "filename",     detailKey: null },
        { col: "bitrate",      detailKey: null },
        { col: "duration",     detailKey: "durationMs",  field: null, format: (v) => v > 0 ? formatDuration(v) : "" },
        { col: "filesize",     detailKey: null },
    ];

    function hideTagSourceDetail() {
        state.tagSourceDetail = null;
        removeSourceRow();
        if (dom.tagSourceApplyCover) {
            dom.tagSourceApplyCover.style.display = "none";
        }
        if (dom.tagSourceApplyAll) {
            dom.tagSourceApplyAll.style.display = "none";
        }
    }

    function removeSourceRow() {
        if (!dom.trackList) {
            return;
        }
        const existing = dom.trackList.querySelector(".qt-source-row");
        if (existing) {
            existing.remove();
        }
    }

    async function fetchTagSourceDetail(provider, itemId) {
        if (!dom.trackList) {
            return;
        }

        // Show loading row immediately
        injectSourceRow('<div class="qt-source-loading qt-col-title" style="grid-column:1/-1;">Loading source metadata...</div>');

        try {
            const detail = await requestJson(
                `${API_BASE}/tag-sources/detail?provider=${encodeURIComponent(provider)}&id=${encodeURIComponent(itemId)}`
            );

            if (!detail) {
                injectSourceRow('<div class="qt-source-loading qt-col-title" style="grid-column:1/-1;">No details available.</div>');
                state.tagSourceDetail = null;
                return;
            }

            state.tagSourceDetail = detail;
            renderTagSourceRow();
        } catch (error) {
            injectSourceRow(`<div class="qt-source-loading qt-col-title" style="grid-column:1/-1;">Failed: ${escapeHtml(error.message)}</div>`);
            state.tagSourceDetail = null;
        }
    }

    function injectSourceRow(innerHtml) {
        removeSourceRow();
        if (!dom.trackList) {
            return;
        }

        const primary = getPrimaryTrack();
        const row = document.createElement("div");
        row.className = "qt-track qt-source-row";
        row.innerHTML = innerHtml;

        // Insert after the primary track's article element
        if (primary) {
            const trackEl = dom.trackList.querySelector(`[data-path="${CSS.escape(primary.path)}"]`);
            if (trackEl?.nextSibling) {
                dom.trackList.insertBefore(row, trackEl.nextSibling);
            } else if (trackEl) {
                dom.trackList.appendChild(row);
            } else {
                dom.trackList.prepend(row);
            }
        } else {
            dom.trackList.prepend(row);
        }

        applyColumnVisibility();
        row.scrollIntoView({ block: "nearest", behavior: "smooth" });
    }

    function renderTagSourceRow() {
        const detail = state.tagSourceDetail;
        if (!detail) {
            hideTagSourceDetail();
            return;
        }

        const providerLabel = escapeHtml(getSourceProviderLabel(detail.provider));

        const cells = SOURCE_COL_MAP.map((col) => {
            const cssClass = `qt-col-${col.col}`;

            if (!col.detailKey) {
                return `<div class="${cssClass}"><span class="qt-track-empty">-</span></div>`;
            }

            const rawValue = resolveSourceDetailValue(detail, col);
            if (rawValue == null || rawValue === "" || rawValue === 0) {
                return `<div class="${cssClass}"><span class="qt-track-empty">-</span></div>`;
            }

            const displayValue = formatSourceDetailValue(col, rawValue);
            const safeValue = escapeHtml(displayValue);

            // Title column gets the provider badge instead of artwork
            if (col.col === "title") {
                return `<div class="qt-track-title-cell ${cssClass}">` +
                    `<span class="qt-source-badge">${providerLabel}</span>` +
                    `<div class="qt-track-title qt-source-cell" data-field="${col.field}" data-value="${safeValue}">` +
                        `<span>${safeValue}</span>` +
                        (col.field ? `<button type="button" class="qt-source-apply" data-action="apply-source" title="Apply">&#8593;</button>` : "") +
                    `</div>` +
                `</div>`;
            }

            // Regular cell with apply arrow
            const applyBtn = col.field
                ? `<button type="button" class="qt-source-apply" data-action="apply-source" title="Apply">&#8593;</button>`
                : "";

            return `<div class="${cssClass} qt-source-cell" data-field="${col.field || ""}" data-value="${safeValue}">` +
                `<span>${safeValue}</span>` +
                applyBtn +
            `</div>`;
        }).join("");

        injectSourceRow(cells);
        if (dom.tagSourceApplyCover) {
            dom.tagSourceApplyCover.style.display = detail.coverUrl ? "" : "none";
        }
        if (dom.tagSourceApplyAll) {
            dom.tagSourceApplyAll.style.display = "";
        }
    }

    function applyTagSourceField(fieldKey, rawValue) {
        const track = getPrimaryTrack();
        if (!track || !fieldKey) {
            return;
        }

        applyFieldValue(track, fieldKey, String(rawValue ?? ""));
        renderTrackList(true);
        // Re-render source row since renderTrackList clears the DOM
        if (state.tagSourceDetail) {
            renderTagSourceRow();
        }
    }

    function applyAllTagSourceFields() {
        const detail = state.tagSourceDetail;
        const track = getPrimaryTrack();
        if (!detail || !track) {
            return;
        }

        for (const col of SOURCE_COL_MAP) {
            if (!col.detailKey || !col.field) {
                continue;
            }
            const rawValue = resolveSourceDetailValue(detail, col);
            if (rawValue == null || rawValue === "" || rawValue === 0) {
                continue;
            }
            const value = formatSourceDetailValue(col, rawValue);
            applyFieldValue(track, col.field, value);
        }

        applyTagSourceAudioFeatures(track, detail);
        applyTagSourceOtherTags(track, detail);

        renderTrackList(true);
        if (state.tagSourceDetail) {
            renderTagSourceRow();
        }
    }

    async function applyTagSourceCover() {
        const detail = state.tagSourceDetail;
        if (!detail?.coverUrl) {
            notify("Selected source has no artwork.", "warning");
            return;
        }

        const tracks = getSelectedTracks();
        if (tracks.length === 0) {
            notify("Select at least one track.", "warning");
            return;
        }

        try {
            const response = await fetch(
                `${API_BASE}/tag-sources/cover?provider=${encodeURIComponent(detail.provider || "")}&id=${encodeURIComponent(detail.id || "")}`
            );
            if (!response.ok) {
                throw new Error(`Failed to fetch source cover (${response.status}).`);
            }

            const blob = await response.blob();
            if (!(blob.type || "").toLowerCase().startsWith("image/")) {
                throw new Error("Source cover is not a valid image.");
            }

            const dataUrl = await blobToDataUrl(blob);
            const parsed = parseDataUrl(dataUrl);
            const applied = applyArtworkChangeToSelection({
                mode: "set",
                mimeType: parsed.mimeType,
                dataBase64: parsed.dataBase64,
                description: getArtworkDescriptionInputValue()
                    || `Cover from ${getSourceProviderLabel(detail.provider).toLowerCase()}`,
                imageType: getArtworkTypeInputValue()
            });

            if (applied) {
                state.artworkViewIndex = 2;
                notify("Source cover applied to selected tracks.", "success");
            }
        } catch (error) {
            notify(error.message || "Failed to apply source cover.", "error");
        }
    }

    function buildTagSourceQuery(track) {
        if (!track) {
            return "";
        }

        const title = String(track.title || "").trim();
        const artist = Array.isArray(track.artists) ? String(track.artists[0] || "").trim() : "";
        const album = String(track.album || "").trim();
        const isrc = String(track.isrc || "").trim();

        if (artist && title && album) {
            return `${artist} ${title} ${album}`;
        }

        if (artist && title) {
            return `${artist} ${title}`;
        }

        if (title && album) {
            return `${title} ${album}`;
        }

        if (title) {
            return title;
        }

        if (isrc) {
            return isrc;
        }

        if (artist) {
            return artist;
        }

        return "";
    }

    function renderTagSourceContext() {
        if (!dom.tagSourceStatus) {
            return;
        }

        const providerKey = state.tagSourceProvider;
        const providerLabel = TAG_SOURCE_PROVIDERS[providerKey] || "Tag Source";
        const primary = getPrimaryTrack();
        if (!primary) {
            clearTagSourceResults();
            setTagSourceResultMeta("");
            setTagSourceStatus("Select a track, then click Search.", "");
            return;
        }

        const query = buildTagSourceQuery(primary);
        if (!query) {
            clearTagSourceResults();
            setTagSourceResultMeta("");
            setTagSourceStatus("Selected track has insufficient metadata for search.", "warning");
            return;
        }

        setTagSourceStatus(`Ready to search ${providerLabel} for "${query}".`, "");
    }

    function renderTagSourceResults(items, providerLabel) {
        if (!dom.tagSourceResults) {
            return;
        }

        dom.tagSourceResults.innerHTML = "";
        (Array.isArray(items) ? items : []).forEach((item) => {
            const option = document.createElement("option");
            const title = String(item.title || "").trim() || "(Untitled)";
            const subtitle = String(item.subtitle || "").trim();
            option.textContent = subtitle ? `${title} - ${subtitle}` : title;
            option.value = String(item.url || item.id || title);
            option.dataset.id = String(item.id || "");
            option.dataset.url = String(item.url || "");
            option.dataset.details = String(item.details || "");
            option.dataset.provider = providerLabel || "";
            dom.tagSourceResults.appendChild(option);
        });
    }

    function persistTagSourceProvider(provider) {
        state.tagSourceProvider = Object.hasOwn(TAG_SOURCE_PROVIDERS, provider)
            ? provider
            : "spotify";
        localStorage.setItem(QUICKTAG_TAG_SOURCE_PROVIDER_KEY, state.tagSourceProvider);
        if (globalThis.UserPrefs) {
            globalThis.UserPrefs.set("quickTagTagSourceProvider", state.tagSourceProvider);
        }
        return state.tagSourceProvider;
    }

    function clearTagSourceSearchState(message, tone) {
        setTagSourceStatus(message, tone);
        clearTagSourceResults();
        setTagSourceResultMeta("");
    }

    function resolveTagSourceSearchContext() {
        const primary = getPrimaryTrack();
        if (!primary) {
            clearTagSourceSearchState("Select a track, then click Search.", "warning");
            return null;
        }

        const query = buildTagSourceQuery(primary);
        if (!query) {
            clearTagSourceSearchState("Selected track has insufficient metadata for search.", "warning");
            return null;
        }

        return {
            primary,
            query,
            providerLabel: TAG_SOURCE_PROVIDERS[state.tagSourceProvider] || state.tagSourceProvider
        };
    }

    function buildTagSourceSearchPayload(primary, query) {
        return {
            provider: state.tagSourceProvider,
            query: query || "",
            path: primary.path || "",
            title: primary.title || "",
            artist: Array.isArray(primary.artists) && primary.artists.length > 0 ? primary.artists[0] : "",
            album: primary.album || "",
            year: primary.year || null,
            trackNumber: Number.isFinite(primary.trackNumber) ? primary.trackNumber : null,
            isrc: primary.isrc || "",
            durationMs: Number.isFinite(primary.durationMs) ? primary.durationMs : null
        };
    }

    function handleTagSourceSearchSuccess(result, providerLabel) {
        const items = Array.isArray(result.items) ? result.items : [];
        renderTagSourceResults(items, providerLabel);

        if (result.supported === false) {
            setTagSourceStatus(result.message || `${providerLabel} is currently unavailable.`, "warning");
            setTagSourceResultMeta("");
            return;
        }

        setTagSourceStatus(result.message || `Found ${items.length} result(s) from ${providerLabel}.`, "success");
        if (items.length === 0) {
            setTagSourceResultMeta("");
            return;
        }

        const first = items[0];
        setTagSourceResultMeta(String(first.details || ""));
        if (dom.tagSourceResults) {
            dom.tagSourceResults.selectedIndex = 0;
        }
    }

    async function runTagSourceSearch() {
        const provider = dom.tagSourceProvider
            ? String(dom.tagSourceProvider.value || "spotify").trim().toLowerCase()
            : "spotify";
        persistTagSourceProvider(provider);
        const context = resolveTagSourceSearchContext();
        if (!context) {
            return;
        }

        const { primary, query, providerLabel } = context;
        setTagSourceStatus(`Searching ${providerLabel}...`, "");
        clearTagSourceResults();
        setTagSourceResultMeta("");

        try {
            const result = await requestJson(`${API_BASE}/tag-sources/search`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(buildTagSourceSearchPayload(primary, query))
            });
            handleTagSourceSearchSuccess(result, providerLabel);
        } catch (error) {
            setTagSourceStatus(`Tag source search failed: ${error.message}`, "error");
            clearTagSourceResults();
            setTagSourceResultMeta("");
        }
    }

    function renderColumnControls() {
        if (!dom.columnToggles) {
            return;
        }

        const html = COLUMN_DEFS.map((column) => {
            const checked = state.visibleColumns.has(column.key);
            const disabled = column.required ? "disabled" : "";
            return `
                <label class="qt-checkbox">
                    <input type="checkbox" data-action="column-toggle" data-column="${escapeHtml(column.key)}" ${checked ? "checked" : ""} ${disabled} />
                    <span>${escapeHtml(column.label)}</span>
                </label>
            `;
        }).join("");

        dom.columnToggles.innerHTML = html;
        if (dom.columnPreset) {
            dom.columnPreset.value = state.columnPreset === "custom" ? "puddletag-full" : state.columnPreset;
        }
    }

    function setColumnPreset(presetKey) {
        const preset = COLUMN_PRESETS[presetKey];
        if (!preset) {
            return;
        }

        state.visibleColumns = normalizeVisibleColumns(preset);
        state.columnPreset = presetKey;
        saveColumnSettings();
        renderColumnControls();
        applyColumnVisibility();
    }

    function toggleColumnVisibility(columnKey, visible) {
        const column = COLUMN_DEFS.find((item) => item.key === columnKey);
        if (!column || column.required) {
            return;
        }

        if (visible) {
            state.visibleColumns.add(columnKey);
        } else {
            state.visibleColumns.delete(columnKey);
        }

        syncColumnPresetSelection();
        saveColumnSettings();
        renderColumnControls();
        applyColumnVisibility();
    }

    function applyLibraryUiState() {
        const browsing = !state.librarySelectionMode;
        if (dom.pathInput) {
            dom.pathInput.disabled = !browsing;
        }
        if (dom.pathUpBtn) {
            dom.pathUpBtn.disabled = !browsing;
        }
        if (dom.pathReloadBtn) {
            dom.pathReloadBtn.disabled = !browsing;
        }
        if (dom.folderFilterInput) {
            dom.folderFilterInput.disabled = !browsing;
        }
    }

    function renderLibrarySelect() {
        if (!dom.librarySelect) {
            return;
        }

        const options = ['<option value="">Select audio library...</option>'];
        state.libraries.forEach((library) => {
            const label = library.displayName || "Unnamed library";
            options.push(`<option value="${escapeHtml(library.rootPath)}">${escapeHtml(label)}</option>`);
        });

        dom.librarySelect.innerHTML = options.join("");
        dom.librarySelect.value = state.selectedLibraryRoot || "";
    }

    function enterLibrarySelectionMode() {
        state.librarySelectionMode = true;
        state.selectedLibraryRoot = "";
        state.path = "";
        state.browserPath = "";
        state.folders = [];
        state.tracks = [];
        state.filteredTracks = [];
        state.failed = [];
        state.folderFilter = "";
        applySelection([], null);

        if (dom.pathInput) {
            dom.pathInput.value = "";
        }
        if (dom.folderFilterInput) {
            dom.folderFilterInput.value = "";
        }
        localStorage.removeItem(QUICKTAG_PATH_KEY);

        renderLibrarySelect();
        applyLibraryUiState();
        renderFolderList();
        renderTrackList();
        renderMoodBar();
        renderGenreBar();
        renderCustomGroups();
        renderArtworkEditor();
        renderSourceTags();
        renderTagSourceContext();
        updatePlayerMeta();
        loadAudioForPrimary(false);
    }

    async function loadLibraries() {
        const folders = await requestJson(LIBRARIES_API);
        const source = Array.isArray(folders) ? folders : [];
        const filtered = source
            .filter((item) => item && typeof item.rootPath === "string" && item.rootPath.trim().length > 0)
            .filter((item) => isFolderEnabledFlag(item?.enabled))
            .map((item) => ({
                rootPath: String(item.rootPath),
                displayName: String(item.displayName || item.libraryName || "")
            }));

        const dedup = new Map();
        filtered.forEach((item) => {
            const key = normalizePathKey(item.rootPath);
            if (!key || dedup.has(key)) {
                return;
            }
            dedup.set(key, item);
        });

        state.libraries = Array.from(dedup.values()).sort((a, b) => {
            const aName = (a.displayName || a.rootPath).toLowerCase();
            const bName = (b.displayName || b.rootPath).toLowerCase();
            if (aName < bName) {
                return -1;
            }
            if (aName > bName) {
                return 1;
            }
            return 0;
        });

        renderLibrarySelect();
    }

    async function selectLibrary(rootPath) {
        const target = state.libraries.find((library) => samePath(library.rootPath, rootPath));
        if (!target) {
            enterLibrarySelectionMode();
            return;
        }

        state.librarySelectionMode = false;
        state.selectedLibraryRoot = target.rootPath;
        state.path = target.rootPath;
        state.browserPath = target.rootPath;
        state.folderFilter = "";

        if (dom.pathInput) {
            dom.pathInput.value = state.path;
        }
        if (dom.folderFilterInput) {
            dom.folderFilterInput.value = "";
        }
        if (dom.librarySelect) {
            dom.librarySelect.value = state.selectedLibraryRoot;
        }

        localStorage.setItem(QUICKTAG_PATH_KEY, state.path);
        applyLibraryUiState();
        await loadFolder();
        await loadTracks();
    }

    function parseLaunchTrackId() {
        try {
            const params = new URLSearchParams(globalThis.location.search || "");
            const raw = String(params.get("trackId") || "").trim();
            if (!raw) {
                return null;
            }

            const trackId = Number.parseInt(raw, 10);
            if (!Number.isFinite(trackId) || trackId <= 0) {
                return null;
            }

            return trackId;
        } catch {
            return null;
        }
    }

    async function openTrackFromLaunch(trackId) {
        if (!Number.isFinite(trackId) || trackId <= 0) {
            return false;
        }

        const info = await requestJson(`${API_BASE}/track/${trackId}`);
        const filePath = normalizePath(info?.filePath || "");
        if (!filePath) {
            throw new Error("Selected track file is unavailable.");
        }

        const targetLibrary = state.libraries.find((library) => pathWithinRoot(filePath, library.rootPath));
        if (!targetLibrary) {
            throw new Error("Selected track is outside configured library roots.");
        }

        await selectLibrary(targetLibrary.rootPath);
        const directory = normalizePath(info?.directory || filePath.substring(0, Math.max(0, filePath.lastIndexOf("/"), filePath.lastIndexOf("\\"))));
        if (directory) {
            await navigateToPath(directory, true);
        }

        const matched = state.tracks.find((track) => samePath(track.path, filePath));
        if (!matched) {
            throw new Error("Track was not found in the loaded folder.");
        }

        applySelection([matched.path], matched.path);
        applySearchAndSort();
        renderTrackList();
        renderMoodBar();
        renderGenreBar();
        renderCustomGroups();
        renderSourceTags();
        renderTagSourceContext();
        loadAudioForPrimary(false);
        return true;
    }

    function valuesEqual(a, b) {
        if (a.length !== b.length) {
            return false;
        }

        for (let i = 0; i < a.length; i += 1) {
            if ((a[i] || "").toLowerCase() !== (b[i] || "").toLowerCase()) {
                return false;
            }
        }

        return true;
    }

    function distinctPreserveOrder(values) {
        const seen = new Set();
        const output = [];
        values.forEach((value) => {
            const cleaned = String(value || "").trim();
            if (!cleaned) {
                return;
            }

            const key = cleaned.toLowerCase();
            if (seen.has(key)) {
                return;
            }

            seen.add(key);
            output.push(cleaned);
        });
        return output;
    }

    function splitNote(note) {
        if (!note) {
            return [];
        }

        return distinctPreserveOrder(note.split(",").map((value) => value.trim()));
    }

    function splitCsvValues(raw) {
        if (!raw) {
            return [];
        }
        return distinctPreserveOrder(String(raw).split(/[;,]/).map((value) => value.trim()));
    }

    function normalizePositiveInteger(value) {
        const parsed = Number.parseInt(value, 10);
        return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
    }

    function parsePositionInput(value) {
        const text = String(value || "").trim();
        if (!text) {
            return { number: null, total: null };
        }

        const parts = text.split("/", 2).map((part) => part.trim());
        const number = normalizePositiveInteger(parts[0]);
        if (parts.length === 1) {
            if (!number) {
                return null;
            }
            return { number, total: null };
        }

        const total = normalizePositiveInteger(parts[1]);
        if (!number && !total) {
            return null;
        }

        return { number, total };
    }

    function formatPosition(numberValue, totalValue) {
        const number = normalizePositiveInteger(numberValue);
        const total = normalizePositiveInteger(totalValue);
        if (number && total) {
            return `${number}/${total}`;
        }
        if (number) {
            return String(number);
        }
        if (total) {
            return `0/${total}`;
        }
        return "";
    }

    function formatDuration(ms) {
        const totalSeconds = Math.floor(ms / 1000);
        const hours = Math.floor(totalSeconds / 3600);
        const minutes = Math.floor((totalSeconds % 3600) / 60);
        const seconds = totalSeconds % 60;
        if (hours > 0) {
            return `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
        }
        return `${minutes}:${String(seconds).padStart(2, "0")}`;
    }

    function formatFileSize(bytes) {
        if (bytes >= 1073741824) {
            return `${(bytes / 1073741824).toFixed(1)} GB`;
        }
        if (bytes >= 1048576) {
            return `${(bytes / 1048576).toFixed(1)} MB`;
        }
        if (bytes >= 1024) {
            return `${Math.round(bytes / 1024)} KB`;
        }
        return `${bytes} B`;
    }

    function renderEditableField(path, field, value, emptyPlaceholder) {
        const text = String(value || "").trim();
        const placeholder = emptyPlaceholder || "Edit value";
        return `<span class="qt-inline-edit ${text ? "" : "is-empty"}" contenteditable="true" spellcheck="false" data-action="inline-edit" data-edit-field="${escapeHtml(field)}" data-path="${escapeHtml(path)}" data-placeholder="${escapeHtml(placeholder)}">${escapeHtml(text)}</span>`;
    }

    function cloneTagMap(tags) {
        const output = {};
        if (!tags) {
            return output;
        }

        Object.keys(tags).forEach((key) => {
            const values = Array.isArray(tags[key]) ? tags[key] : [];
            output[key] = values.map((value) => String(value || "").trim()).filter((value) => value.length > 0);
        });

        return output;
    }

    function upsertSourceTagValues(track, tag, values) {
        if (!track || !tag) {
            return;
        }

        const cleanValues = Array.isArray(values)
            ? values.map((value) => String(value || "").trim()).filter((value) => value.length > 0)
            : [];

        if (!track.sourceTags || typeof track.sourceTags !== "object") {
            track.sourceTags = {};
        }

        if (cleanValues.length === 0) {
            delete track.sourceTags[tag];
            return;
        }

        track.sourceTags[tag] = cleanValues;
    }

    function normalizeNumericValue(value) {
        if (value == null || value === "") {
            return null;
        }

        const number = Number(value);
        return Number.isFinite(number) ? number : null;
    }

    function formatAudioFeatureValue(number, isInteger) {
        if (!Number.isFinite(number)) {
            return null;
        }

        if (isInteger) {
            return String(Math.round(number));
        }

        return String(Math.round(number * 10000) / 10000);
    }

    function applyTagSourceAudioFeatures(track, detail) {
        const mappings = [
            { detailKey: "danceability", fieldKey: "danceability", integer: false },
            { detailKey: "energy", fieldKey: "energy_feature", integer: false },
            { detailKey: "valence", fieldKey: "valence", integer: false },
            { detailKey: "acousticness", fieldKey: "acousticness", integer: false },
            { detailKey: "instrumentalness", fieldKey: "instrumentalness", integer: false },
            { detailKey: "speechiness", fieldKey: "speechiness", integer: false },
            { detailKey: "loudness", fieldKey: "loudness", integer: false },
            { detailKey: "tempo", fieldKey: "tempo_feature", integer: false },
            { detailKey: "timeSignature", fieldKey: "time_signature", integer: true },
            { detailKey: "liveness", fieldKey: "liveness", integer: false }
        ];

        mappings.forEach((mapping) => {
            const value = normalizeNumericValue(detail[mapping.detailKey]);
            if (value == null) {
                return;
            }

            const tag = rawTagByFormat(track.format, mapping.fieldKey);
            if (!tag) {
                return;
            }

            const formatted = formatAudioFeatureValue(value, mapping.integer);
            if (!formatted) {
                return;
            }

            upsertSourceTagValues(track, tag, [formatted]);
        });
    }

    function applyTagSourceOtherTags(track, detail) {
        if (!detail.otherTags || typeof detail.otherTags !== "object") {
            return;
        }

        Object.keys(detail.otherTags).forEach((tag) => {
            const normalizedTag = String(tag || "").trim();
            if (!normalizedTag) {
                return;
            }

            const values = Array.isArray(detail.otherTags[tag])
                ? detail.otherTags[tag]
                : [];
            const clean = distinctPreserveOrder(
                values.map((value) => String(value || "").trim()).filter((value) => value.length > 0)
            );
            if (clean.length === 0) {
                return;
            }

            upsertSourceTagValues(track, normalizedTag, clean);
        });
    }

    function listTagKeys(tags) {
        return Object.keys(tags || {}).filter((key) => key && key.trim().length > 0);
    }

    function sameTagValues(a, b) {
        const av = Array.isArray(a) ? a : [];
        const bv = Array.isArray(b) ? b : [];
        if (av.length !== bv.length) {
            return false;
        }

        for (let i = 0; i < av.length; i += 1) {
            if ((av[i] || "") !== (bv[i] || "")) {
                return false;
            }
        }

        return true;
    }

    function sourceTagsEqual(current, original) {
        const currentKeys = listTagKeys(current).sort((a, b) => a.localeCompare(b));
        const originalKeys = listTagKeys(original).sort((a, b) => a.localeCompare(b));
        if (!sameTagValues(currentKeys, originalKeys)) {
            return false;
        }

        for (const key of currentKeys) {
            if (!sameTagValues(current[key] || [], original[key] || [])) {
                return false;
            }
        }

        return true;
    }

    function trackHasEmbeddedArtwork(track) {
        return Boolean(track?.hasArtwork);
    }

    function trackHasPendingArtworkChange(track) {
        const pending = track?.pendingArtwork;
        if (!pending) {
            return false;
        }

        if (pending.mode === "set") {
            return Boolean(pending.dataBase64);
        }

        if (pending.mode === "clear") {
            return trackHasEmbeddedArtwork(track);
        }

        return false;
    }

    function buildArtworkThumbUrl(track, size, crop) {
        if (!track?.path) {
            return "";
        }

        const pending = track.pendingArtwork;
        if (pending?.mode === "set" && pending.dataBase64) {
            const mime = pending.mimeType || "image/jpeg";
            return `data:${mime};base64,${pending.dataBase64}`;
        }

        if (pending?.mode === "clear") {
            return "";
        }

        const query = new URLSearchParams();
        query.set("path", track.path);
        query.set("size", String(Math.max(24, Math.min(1200, size || 50))));
        query.set("crop", crop ? "true" : "false");
        if (Number.isFinite(track.artworkVersion) && track.artworkVersion > 0) {
            query.set("v", String(track.artworkVersion));
        }

        return `${API_BASE}/thumb?${query.toString()}`;
    }

    function parseDataUrl(dataUrl) {
        const raw = String(dataUrl || "");
        const match = /^data:([^;,]+);base64,(.+)$/i.exec(raw);
        if (!match) {
            throw new Error("Unsupported artwork format.");
        }

        return {
            mimeType: String(match[1] || "image/jpeg").toLowerCase(),
            dataBase64: match[2]
        };
    }

    function getArtworkDescriptionInputValue() {
        return dom.artworkDescription?.value || "";
    }

    function getArtworkTypeInputValue() {
        return Number(dom.artworkType?.value || 3);
    }

    function normalizeArtworkType(value, fallbackValue) {
        const numericValue = Number(value);
        if (Number.isFinite(numericValue)) {
            return Math.max(0, Math.min(20, numericValue));
        }

        const fallbackNumericValue = Number(fallbackValue);
        return Number.isFinite(fallbackNumericValue) ? fallbackNumericValue : 3;
    }

    function blobToDataUrl(blob) {
        return new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.onload = () => resolve(typeof reader.result === "string" ? reader.result : "");
            reader.onerror = () => reject(new Error("Failed to read image."));
            reader.readAsDataURL(blob);
        });
    }

    async function readImageFromSystemClipboard() {
        if (!navigator.clipboard || typeof navigator.clipboard.read !== "function") {
            return null;
        }

        try {
            const items = await navigator.clipboard.read();
            for (const item of items) {
                const imageType = (item.types || []).find((type) => String(type || "").toLowerCase().startsWith("image/"));
                if (!imageType) {
                    continue;
                }

                const blob = await item.getType(imageType);
                if (!blob || !(blob.type || "").toLowerCase().startsWith("image/")) {
                    continue;
                }

                const dataUrl = await blobToDataUrl(blob);
                return parseDataUrl(dataUrl);
            }
        } catch {
            return null;
        }

        return null;
    }

    function clampArtworkViewIndex(entries) {
        if (!Array.isArray(entries) || entries.length === 0) {
            state.artworkViewIndex = 0;
            return 0;
        }

        if (!Number.isFinite(state.artworkViewIndex)) {
            state.artworkViewIndex = 0;
        }
        state.artworkViewIndex = Math.max(0, Math.min(entries.length - 1, state.artworkViewIndex));
        return state.artworkViewIndex;
    }

    function getArtworkEntries(track) {
        const entries = [
            { key: "keep", label: "<keep>" },
            { key: "blank", label: "<blank>" }
        ];

        if (!track) {
            return entries;
        }

        const previewUrl = buildArtworkThumbUrl(track, 420, false);
        if (previewUrl) {
            entries.push({ key: "cover", label: "Cover", url: previewUrl });
        }

        return entries;
    }

    function getCurrentArtworkEntry(track) {
        const entries = getArtworkEntries(track);
        const index = clampArtworkViewIndex(entries);
        return {
            entries,
            index,
            entry: entries[index]
        };
    }

    function moodTagByFormat(format) {
        const f = String(format || "").toLowerCase();
        if (f === "flac" || f === "ogg") {
            return "MOOD";
        }

        if (f === "mp4") {
            return "iTunes:MOOD";
        }

        return "TMOO";
    }

    function commentTagByFormat(format) {
        const f = String(format || "").toLowerCase();
        if (f === "flac" || f === "ogg") {
            return "COMMENT";
        }

        if (f === "mp4") {
            return "\u00a9cmt";
        }

        return "COMM";
    }

    function rawTagByFormat(format, field) {
        const f = String(format || "").toLowerCase();
        let map = SOURCE_FIELD_MAPS.id3;
        if (f === "flac" || f === "ogg") {
            map = SOURCE_FIELD_MAPS.vorbis;
        } else if (f === "mp4") {
            map = SOURCE_FIELD_MAPS.mp4;
        }
        return map[field] || field;
    }

    function getTagValues(track, keys) {
        if (!track?.tags) {
            return [];
        }

        const map = track.tags;
        const mapKeys = Object.keys(map);
        const output = [];

        keys.forEach((key) => {
            const foundKey = mapKeys.find((candidate) => candidate.toLowerCase() === key.toLowerCase());
            if (!foundKey) {
                return;
            }

            const values = Array.isArray(map[foundKey]) ? map[foundKey] : [];
            values.forEach((value) => {
                if (value != null) {
                    output.push(String(value));
                }
            });
        });

        return distinctPreserveOrder(output);
    }

    function customValueSet() {
        const set = new Set();
        CUSTOM_GROUPS.forEach((group) => {
            group.values.forEach((value) => set.add(value.toLowerCase()));
        });
        return set;
    }

    const allCustomValuesSet = customValueSet();

    function readTrackField(raw, camelKey, pascalKey, fallback) {
        if (raw && Object.hasOwn(raw, camelKey)) {
            return raw[camelKey];
        }
        if (raw && Object.hasOwn(raw, pascalKey)) {
            return raw[pascalKey];
        }
        return fallback;
    }

    function getTrackArtworkState(raw) {
        const rawHasArtwork = readTrackField(raw, "hasArtwork", "HasArtwork", undefined);
        const artworkKnown = rawHasArtwork !== undefined && rawHasArtwork !== null;
        const hasArtwork = rawHasArtwork === true
            || rawHasArtwork === "true"
            || rawHasArtwork === 1
            || rawHasArtwork === "1";
        const rawArtworkVersion = readTrackField(raw, "artworkVersion", "ArtworkVersion", undefined);
        const rawArtworkDescription = readTrackField(raw, "artworkDescription", "ArtworkDescription", "");
        const rawArtworkType = readTrackField(raw, "artworkType", "ArtworkType", 3);
        return {
            hasArtwork,
            artworkKnown,
            artworkVersion: Number.isFinite(rawArtworkVersion) ? rawArtworkVersion : Date.now(),
            artworkDescription: String(rawArtworkDescription || ""),
            artworkType: Number.isFinite(Number(rawArtworkType)) ? Math.max(0, Math.min(20, Number(rawArtworkType))) : 3
        };
    }

    function createTrackOriginalSnapshot(track) {
        return {
            title: track.title,
            artists: track.artists.slice(),
            album: track.album,
            albumArtists: track.albumArtists.slice(),
            composers: track.composers.slice(),
            trackNumber: track.trackNumber,
            trackTotal: track.trackTotal,
            discNumber: track.discNumber,
            discTotal: track.discTotal,
            genres: track.genres.slice(),
            year: track.year,
            bpm: track.bpm,
            key: track.key,
            publisher: track.publisher,
            copyright: track.copyright,
            language: track.language,
            lyricist: track.lyricist,
            conductor: track.conductor,
            isrc: track.isrc,
            mood: track.mood,
            energy: track.energy,
            hasArtwork: track.hasArtwork,
            artworkKnown: track.artworkKnown,
            artworkVersion: track.artworkVersion,
            artworkDescription: track.artworkDescription,
            artworkType: track.artworkType,
            commentValues: buildCommentValues(track),
            sourceTags: cloneTagMap(track.sourceTags)
        };
    }

    function getTrackScalarChangeSpecs(track) {
        return [
            { changed: (track.title || "") !== (track.original.title || ""), change: { type: "title", value: track.title || "" } },
            { changed: !valuesEqual(track.artists, track.original.artists || []), change: { type: "artist", value: track.artists.slice() } },
            { changed: (track.album || "") !== (track.original.album || ""), change: { type: "album", value: track.album || "" } },
            { changed: !valuesEqual(track.albumArtists, track.original.albumArtists || []), change: { type: "albumartist", value: track.albumArtists.slice() } },
            { changed: !valuesEqual(track.composers, track.original.composers || []), change: { type: "composer", value: track.composers.slice() } },
            {
                changed: (track.trackNumber || 0) !== (track.original.trackNumber || 0)
                    || (track.trackTotal || 0) !== (track.original.trackTotal || 0),
                change: {
                    type: "track",
                    value: {
                        number: track.trackNumber || null,
                        total: track.trackTotal || null
                    }
                }
            },
            {
                changed: (track.discNumber || 0) !== (track.original.discNumber || 0)
                    || (track.discTotal || 0) !== (track.original.discTotal || 0),
                change: {
                    type: "disc",
                    value: {
                        number: track.discNumber || null,
                        total: track.discTotal || null
                    }
                }
            },
            { changed: !valuesEqual(track.genres, track.original.genres), change: { type: "genre", value: track.genres.slice() } },
            { changed: (track.year || 0) !== (track.original.year || 0), change: { type: "year", value: track.year || "" } },
            { changed: (track.bpm || 0) !== (track.original.bpm || 0), change: { type: "bpm", value: track.bpm || "" } },
            { changed: (track.key || "") !== (track.original.key || ""), change: { type: "key", value: track.key || "" } },
            { changed: (track.isrc || "") !== (track.original.isrc || ""), change: { type: "isrc", value: track.isrc || "" } },
            { changed: (track.energy || 0) !== (track.original.energy || 0), change: { type: "rating", value: track.energy || 0 } },
            {
                changed: (track.mood || "") !== (track.original.mood || ""),
                change: { type: "raw", tag: moodTagByFormat(track.format), value: track.mood ? [track.mood] : [] }
            }
        ];
    }

    function getTrackRawFieldChangeSpecs(track) {
        return [
            ["publisher", track.publisher],
            ["copyright", track.copyright],
            ["language", track.language],
            ["lyricist", track.lyricist],
            ["conductor", track.conductor]
        ].map(([field, value]) => ({
            changed: (track[field] || "") !== (track.original[field] || ""),
            change: { type: "raw", tag: rawTagByFormat(track.format, field), value: value ? [value] : [] }
        }));
    }

    function buildArtworkChange(track) {
        const pending = track.pendingArtwork;
        if (pending?.mode === "set" && pending.dataBase64) {
            return {
                type: "artwork",
                value: {
                    mode: "set",
                    mimeType: pending.mimeType || "image/jpeg",
                    dataBase64: pending.dataBase64,
                    description: pending.description || "",
                    imageType: Number.isFinite(Number(pending.imageType))
                        ? Math.max(0, Math.min(20, Number(pending.imageType)))
                        : 3
                }
            };
        }
        if (pending?.mode === "clear" && trackHasEmbeddedArtwork(track)) {
            return {
                type: "artwork",
                value: { mode: "clear" }
            };
        }
        return null;
    }

    function collectSourceTagChanges(track) {
        const currentSourceTags = track.sourceTags || {};
        const originalSourceTags = track.original.sourceTags || {};
        return Array.from(new Set([
            ...listTagKeys(currentSourceTags),
            ...listTagKeys(originalSourceTags)
        ])).filter((key) => !sameTagValues(currentSourceTags[key] || [], originalSourceTags[key] || []))
            .map((key) => ({
                type: "raw",
                tag: key,
                value: (currentSourceTags[key] || []).slice()
            }));
    }

    function setArtworkActionButtonsDisabled(disabled) {
        [
            dom.artworkAddBtn,
            dom.artworkChangeBtn,
            dom.artworkRemoveBtn,
            dom.artworkCopyBtn,
            dom.artworkPasteBtn,
            dom.artworkSaveToFileBtn,
            dom.artworkKeepBtn,
            dom.artworkBlankBtn,
            dom.artworkPrevBtn,
            dom.artworkNextBtn
        ].forEach((btn) => {
            if (btn) {
                btn.disabled = disabled;
            }
        });
    }

    function resetArtworkEditorEmptyState() {
        dom.artworkMeta.textContent = "Select one or more tracks to edit artwork.";
        dom.artworkContext.textContent = "No Images";
        dom.artworkPreview.removeAttribute("src");
        dom.artworkPreview.style.display = "none";
        dom.artworkPlaceholder.style.display = "flex";
        dom.artworkPlaceholder.textContent = "No artwork";
        if (dom.artworkDescription) {
            dom.artworkDescription.value = "";
            dom.artworkDescription.disabled = true;
        }
        if (dom.artworkType) {
            dom.artworkType.value = "3";
            dom.artworkType.disabled = true;
        }
    }

    function syncArtworkMetadataInputs(primary, canEditMetadata) {
        const pending = primary.pendingArtwork;
        if (dom.artworkDescription) {
            const descriptionValue = pending?.mode === "set"
                ? (pending.description || "")
                : (primary.artworkDescription || "");
            dom.artworkDescription.value = descriptionValue;
            dom.artworkDescription.disabled = !canEditMetadata;
        }
        if (dom.artworkType) {
            const typeValue = pending?.mode === "set"
                ? pending.imageType
                : primary.artworkType;
            dom.artworkType.value = String(Number.isFinite(Number(typeValue)) ? Number(typeValue) : 3);
            dom.artworkType.disabled = !canEditMetadata;
        }
    }

    function buildArtworkMetaText(tracks, entry) {
        const pendingCount = tracks.filter((track) => track.pendingArtwork != null).length;
        let meta = tracks.length > 1
            ? `Applies to ${tracks.length} selected tracks.`
            : "Applies to selected track.";
        if (pendingCount > 0) {
            const setCount = tracks.filter((track) => track.pendingArtwork?.mode === "set").length;
            const clearCount = tracks.filter((track) => track.pendingArtwork?.mode === "clear").length;
            if (setCount > 0 && clearCount === 0) {
                return `${meta} Pending: replace cover.`;
            }
            if (clearCount > 0 && setCount === 0) {
                return `${meta} Pending: remove cover.`;
            }
            return `${meta} Pending: mixed artwork changes.`;
        }
        return entry.key === "cover"
            ? `${meta} Embedded artwork detected.`
            : `${meta} ${entry.label} selected.`;
    }

    function showArtworkPreview(previewUrl, fallbackLabel) {
        if (!previewUrl) {
            dom.artworkPreview.removeAttribute("src");
            dom.artworkPreview.style.display = "none";
            dom.artworkPlaceholder.style.display = "flex";
            dom.artworkPlaceholder.textContent = fallbackLabel || "No artwork";
            return;
        }
        dom.artworkPreview.onerror = () => {
            dom.artworkPreview.removeAttribute("src");
            dom.artworkPreview.style.display = "none";
            dom.artworkPlaceholder.style.display = "flex";
            dom.artworkPlaceholder.textContent = "No artwork";
        };
        dom.artworkPreview.onload = () => {
            dom.artworkPreview.style.display = "block";
            dom.artworkPlaceholder.style.display = "none";
        };
        dom.artworkPreview.src = previewUrl;
        dom.artworkPreview.style.display = "block";
        dom.artworkPlaceholder.style.display = "none";
    }

    async function handleLibrarySelectChange() {
        const selected = dom.librarySelect ? dom.librarySelect.value : "";
        if (!selected) {
            enterLibrarySelectionMode();
            return;
        }
        await selectLibrary(selected);
    }

    async function handlePathInputKeydown(event) {
        if (event.key !== "Enter") {
            return;
        }
        event.preventDefault();
        await navigateToPath(dom.pathInput.value, true);
    }

    async function handlePathUpClick() {
        if (state.librarySelectionMode || !state.selectedLibraryRoot) {
            return;
        }
        const currentBrowsePath = state.browserPath || state.path || state.selectedLibraryRoot;
        if (samePath(currentBrowsePath, state.selectedLibraryRoot)) {
            enterLibrarySelectionMode();
            return;
        }
        await loadFolder("..");
        if (!state.browserPath) {
            return;
        }
        state.path = state.browserPath;
        if (dom.pathInput) {
            dom.pathInput.value = state.path;
        }
        localStorage.setItem(QUICKTAG_PATH_KEY, state.path);
        await loadTracks();
    }

    async function handleFolderEntryClick(event) {
        const target = event.target.closest("[data-action='open-entry']");
        if (!target) {
            return;
        }
        const path = target.dataset.path;
        const isDir = target.dataset.dir === "1";
        if (!path) {
            return;
        }
        if (isDir) {
            await navigateToPath(path, true);
            return;
        }
        state.path = path;
        localStorage.setItem(QUICKTAG_PATH_KEY, state.path);
        if (dom.pathInput) {
            dom.pathInput.value = state.path;
        }
        await loadTracks();
        renderFolderList();
    }

    function hydrateTrack(raw) {
        const artwork = getTrackArtworkState(raw);

        const track = {
            path: raw.path,
            format: raw.format,
            title: raw.title || "",
            artists: Array.isArray(raw.artists) ? raw.artists.slice() : [],
            album: raw.album || "",
            albumArtists: Array.isArray(raw.albumArtists) ? raw.albumArtists.slice() : [],
            composers: Array.isArray(raw.composers) ? raw.composers.slice() : [],
            trackNumber: normalizePositiveInteger(raw.trackNumber),
            trackTotal: normalizePositiveInteger(raw.trackTotal),
            discNumber: normalizePositiveInteger(raw.discNumber),
            discTotal: normalizePositiveInteger(raw.discTotal),
            genres: Array.isArray(raw.genres) ? raw.genres.slice() : [],
            bpm: normalizePositiveInteger(raw.bpm),
            year: normalizePositiveInteger(raw.year),
            key: raw.key || "",
            publisher: raw.publisher || "",
            copyright: raw.copyright || "",
            language: raw.language || "",
            lyricist: raw.lyricist || "",
            conductor: raw.conductor || "",
            isrc: raw.isrc || "",
            filename: raw.fileName || "",
            bitrate: raw.bitrateKbps || 0,
            durationMs: raw.durationMs || 0,
            fileSize: raw.fileSizeBytes || 0,
            rating: Number.isFinite(raw.rating) ? raw.rating : 0,
            hasArtwork: artwork.hasArtwork,
            artworkKnown: artwork.artworkKnown,
            artworkVersion: artwork.artworkVersion,
            artworkDescription: artwork.artworkDescription,
            artworkType: artwork.artworkType,
            pendingArtwork: null,
            tags: raw.tags || {},
            sourceTags: cloneTagMap(raw.tags || {}),
            mood: null,
            energy: Number.isFinite(raw.rating) ? raw.rating : 0,
            note: "",
            custom: CUSTOM_GROUPS.map(() => []),
            original: null
        };

        const moodCandidates = [
            moodTagByFormat(track.format),
            "MOOD",
            "TMOO",
            "iTunes:MOOD",
            "com.apple.iTunes:MOOD"
        ];
        const moodValues = getTagValues(track, moodCandidates);
        track.mood = moodValues.length > 0 ? moodValues[0] : null;

        const commentCandidates = [
            commentTagByFormat(track.format),
            "COMM",
            "COMMENT",
            "\u00a9cmt"
        ];
        const commentValues = getTagValues(track, commentCandidates);

        track.custom = CUSTOM_GROUPS.map((group, index) => {
            const matches = commentValues.filter((value) => containsIgnoreCase(group.values, value));
            return distinctPreserveOrder(matches);
        });

        const noteValues = commentValues.filter((value) => !allCustomValuesSet.has(value.toLowerCase()));
        track.note = noteValues.join(", ");

        track.original = createTrackOriginalSnapshot(track);

        return track;
    }

    function containsIgnoreCase(values, target) {
        const targetKey = String(target || "").toLowerCase();
        return values.some((value) => String(value || "").toLowerCase() === targetKey);
    }

    function getKnownGroupSortIndex(group, value) {
        const key = String(value || "").toLowerCase();
        const index = group.values.findIndex((candidate) => candidate.toLowerCase() === key);
        return index >= 0 ? index : Number.MAX_SAFE_INTEGER;
    }

    function compareCustomGroupValues(group, left, right) {
        return getKnownGroupSortIndex(group, left) - getKnownGroupSortIndex(group, right);
    }

    function trackHasGenre(track, genre) {
        return containsIgnoreCase(track.genres || [], genre);
    }

    function buildCommentValues(track) {
        const values = [];

        CUSTOM_GROUPS.forEach((group, groupIndex) => {
            const selected = Array.isArray(track.custom[groupIndex]) ? track.custom[groupIndex] : [];
            const lowerSet = new Set(selected.map((value) => value.toLowerCase()));

            group.values.forEach((value) => {
                if (lowerSet.has(value.toLowerCase())) {
                    values.push(value);
                }
            });

            selected.forEach((value) => {
                if (!containsIgnoreCase(group.values, value)) {
                    values.push(value);
                }
            });
        });

        splitNote(track.note).forEach((value) => values.push(value));

        return distinctPreserveOrder(values);
    }

    function getTrack(path) {
        return state.tracks.find((item) => item.path === path) || null;
    }

    function getSelectedTracks() {
        return Array.from(state.selectedPaths)
            .map((path) => getTrack(path))
            .filter((track) => track != null);
    }

    function getPrimaryTrack() {
        if (state.primaryPath) {
            const exact = getTrack(state.primaryPath);
            if (exact) {
                return exact;
            }
        }

        const selected = getSelectedTracks();
        return selected.length > 0 ? selected[0] : null;
    }

    function selectedTrackHasChanges(track) {
        if (!track?.original) {
            return false;
        }
        return [
            () => getTrackScalarChangeSpecs(track).some((entry) => entry.changed),
            () => getTrackRawFieldChangeSpecs(track).some((entry) => entry.changed),
            () => trackHasPendingArtworkChange(track),
            () => !valuesEqual(buildCommentValues(track), track.original.commentValues || []),
            () => !sourceTagsEqual(track.sourceTags || {}, track.original.sourceTags || {})
        ].some((check) => check());
    }

    function selectedHasUnsavedChanges() {
        return getSelectedTracks().some((track) => selectedTrackHasChanges(track));
    }

    function applySelection(paths, primaryPath) {
        state.selectedPaths = new Set(paths);

        if (state.selectedPaths.size === 0) {
            state.primaryPath = null;
        } else if (primaryPath && state.selectedPaths.has(primaryPath)) {
            state.primaryPath = primaryPath;
        } else {
            state.primaryPath = Array.from(state.selectedPaths)[0];
        }

        const primary = getPrimaryTrack();
        if (!primary) {
            state.artworkViewIndex = 0;
        } else if (primary.pendingArtwork?.mode === "clear") {
            state.artworkViewIndex = 1;
        } else if (primary.pendingArtwork?.mode === "set"
            || trackHasEmbeddedArtwork(primary)) {
            state.artworkViewIndex = 2;
        } else {
            state.artworkViewIndex = 0;
        }

        renderTrackList();
        renderMoodBar();
        renderGenreBar();
        renderCustomGroups();
        renderArtworkEditor();
        renderSourceTags();
        renderTagSourceContext();
        updatePlayerMeta();
        updatePlayPauseIcon();
    }

    async function onTrackSelect(path, additive) {
        const current = new Set(state.selectedPaths);

        if (!additive && selectedHasUnsavedChanges() && !(current.size === 1 && current.has(path))) {
            const shouldSave = globalThis.confirm("You have unsaved changes. Click OK to save before switching track, or Cancel to discard.");
            if (shouldSave) {
                await saveSelectedTracks();
            }
        }

        if (additive) {
            if (current.has(path)) {
                current.delete(path);
            } else {
                current.add(path);
            }
            applySelection(Array.from(current), path);
            return;
        }

        applySelection([path], path);
        loadAudioForPrimary(false);
    }

    function setMoodForSelected(targetMood) {
        const tracks = getSelectedTracks();
        if (tracks.length === 0) {
            return;
        }

        const allSame = tracks.every((track) => (track.mood || "") === (targetMood || ""));
        const newValue = allSame ? null : targetMood;

        tracks.forEach((track) => {
            track.mood = newValue;
        });

        renderTrackList();
        renderMoodBar();
    }

    function toggleGenreForSelected(genre) {
        const tracks = getSelectedTracks();
        if (tracks.length === 0) {
            return;
        }

        const normalized = genre.toLowerCase();
        const allHave = tracks.every((track) => track.genres.some((item) => item.toLowerCase() === normalized));

        tracks.forEach((track) => {
            if (allHave) {
                track.genres = track.genres.filter((item) => item.toLowerCase() !== normalized);
            } else if (!track.genres.some((item) => item.toLowerCase() === normalized)) {
                track.genres.push(genre);
            }
        });

        renderTrackList();
        renderGenreBar();
    }

    function toggleCustomForSelected(groupIndex, value) {
        const tracks = getSelectedTracks();
        if (tracks.length === 0) {
            return;
        }

        const key = value.toLowerCase();
        const allSelected = tracks.every((track) => track.custom[groupIndex].some((item) => item.toLowerCase() === key));

        tracks.forEach((track) => {
            if (allSelected) {
                track.custom[groupIndex] = track.custom[groupIndex].filter((item) => item.toLowerCase() !== key);
            } else if (!track.custom[groupIndex].some((item) => item.toLowerCase() === key)) {
                track.custom[groupIndex].push(value);
            }
        });

        renderTrackList();
        renderCustomGroups();
    }

    function hasCustomValue(track, groupIndex, value) {
        const target = String(value || "").toLowerCase();
        return (track.custom[groupIndex] || []).some((item) => item.toLowerCase() === target);
    }

    function getKnownGroupValues(group, groupIndex) {
        const baseValues = group.values.slice();
        const baseKeys = new Set(baseValues.map((value) => value.toLowerCase()));
        const extras = new Map();

        state.tracks.forEach((track) => {
            const groupValues = Array.isArray(track.custom[groupIndex]) ? track.custom[groupIndex] : [];
            groupValues.forEach((value) => {
                const cleaned = String(value || "").trim();
                if (!cleaned) {
                    return;
                }

                const key = cleaned.toLowerCase();
                if (baseKeys.has(key) || extras.has(key)) {
                    return;
                }

                extras.set(key, cleaned);
            });
        });

        const extraValues = Array.from(extras.values()).sort((a, b) => a.localeCompare(b));
        return baseValues.concat(extraValues);
    }

    function addCustomValuesForSelected(groupIndex, rawValue) {
        const tracks = getSelectedTracks();
        if (tracks.length === 0) {
            return false;
        }

        const values = splitCsvValues(rawValue);
        if (values.length === 0) {
            return false;
        }

        let changed = false;
        tracks.forEach((track) => {
            values.forEach((value) => {
                if (hasCustomValue(track, groupIndex, value)) {
                    return;
                }

                track.custom[groupIndex].push(value);
                changed = true;
            });
        });

        if (changed) {
            renderTrackList();
            renderCustomGroups();
        }

        return changed;
    }

    function sortSelectedCustomValues() {
        const tracks = getSelectedTracks();
        if (tracks.length === 0) {
            return;
        }

        tracks.forEach((track) => {
            CUSTOM_GROUPS.forEach((group, index) => {
                track.custom[index].sort(compareCustomGroupValues.bind(null, group));
            });
        });

        renderTrackList();
        renderCustomGroups();
    }

    function setEnergy(path, rating) {
        const track = getTrack(path);
        if (!track) {
            return;
        }

        if (state.selectedPaths.has(path) && state.selectedPaths.size > 1) {
            getSelectedTracks().forEach((selectedTrack) => {
                selectedTrack.energy = rating;
            });
        } else {
            track.energy = rating;
        }

        renderTrackList();
    }

    function removeCustomChip(path, groupIndex, value, kind) {
        const track = getTrack(path);
        if (!track) {
            return;
        }

        const targets = state.selectedPaths.has(path)
            ? getSelectedTracks()
            : [track];

        if (kind === "note") {
            targets.forEach((item) => {
                const notes = splitNote(item.note).filter((note) => note.toLowerCase() !== value.toLowerCase());
                item.note = notes.join(", ");
            });
        } else {
            targets.forEach((item) => {
                item.custom[groupIndex] = item.custom[groupIndex].filter((entry) => entry.toLowerCase() !== value.toLowerCase());
            });
        }

        renderTrackList();
        renderCustomGroups();
    }

    function getEditTargets(path) {
        const source = getTrack(path);
        if (!source) {
            return [];
        }

        if (state.selectedPaths.has(path)) {
            const selected = getSelectedTracks();
            if (selected.length > 0) {
                return selected;
            }
        }

        return [source];
    }

    function applyFieldValue(track, field, value) {
        switch (field) {
            case "title":
                track.title = value || "";
                break;
            case "artist":
                track.artists = splitCsvValues(value);
                break;
            case "album":
                track.album = value || "";
                break;
            case "albumArtist":
                track.albumArtists = splitCsvValues(value);
                break;
            case "composer":
                track.composers = splitCsvValues(value);
                break;
            case "genre":
                track.genres = splitCsvValues(value);
                break;
            case "year":
                track.year = normalizePositiveInteger(value);
                break;
            case "bpm":
                track.bpm = normalizePositiveInteger(value);
                break;
            case "key":
                track.key = String(value || "").trim();
                break;
            case "publisher":
                track.publisher = String(value || "").trim();
                break;
            case "copyright":
                track.copyright = String(value || "").trim();
                break;
            case "language":
                track.language = String(value || "").trim();
                break;
            case "lyricist":
                track.lyricist = String(value || "").trim();
                break;
            case "conductor":
                track.conductor = String(value || "").trim();
                break;
            case "isrc":
                track.isrc = String(value || "").trim();
                break;
            case "track":
                {
                    const parsedTrack = parsePositionInput(value);
                    if (parsedTrack == null) {
                        return false;
                    }
                    track.trackNumber = parsedTrack.number;
                    track.trackTotal = parsedTrack.total;
                    break;
                }
            case "disc":
                {
                    const parsedDisc = parsePositionInput(value);
                    if (parsedDisc == null) {
                        return false;
                    }
                    track.discNumber = parsedDisc.number;
                    track.discTotal = parsedDisc.total;
                    break;
                }
            default:
                return false;
        }

        return true;
    }

    function commitInlineField(path, field, rawValue) {
        const targets = getEditTargets(path);
        if (targets.length === 0) {
            return false;
        }

        const text = String(rawValue || "").trim();
        let valid = true;
        targets.forEach((track) => {
            if (!applyFieldValue(track, field, text)) {
                valid = false;
            }
        });

        if (!valid) {
            notify(`Invalid value for ${field}.`, "warning");
            return false;
        }

        applySearchAndSort();
        if (!state.selectedPaths.has(path)) {
            applySelection([path], path);
            return true;
        }

        renderTrackList(true);
        renderSourceTags();
        return true;
    }

    function getSourceMapForTrack(track) {
        const template = getEffectiveSourceTemplate(track);
        return SOURCE_FIELD_MAPS[template] || SOURCE_FIELD_MAPS.id3;
    }

    function getTagValuesByName(tags, key) {
        if (!tags || !key) {
            return [];
        }

        const foundKey = Object.keys(tags).find((candidate) => candidate.toLowerCase() === key.toLowerCase());
        if (!foundKey) {
            return [];
        }

        return Array.isArray(tags[foundKey])
            ? tags[foundKey].map((value) => String(value || "").trim()).filter((value) => value.length > 0)
            : [];
    }

    function formatSourceFieldLabel(field) {
        return field === "raw"
            ? "Raw"
            : field.replaceAll("_", " ").replaceAll(/\b\w/g, (ch) => ch.toUpperCase());
    }

    function renderSourceTags() {
        if (!dom.sourceTags) {
            return;
        }

        const primary = getPrimaryTrack();
        if (!primary) {
            dom.sourceTags.innerHTML = '<div class="qt-loading">Select a track to inspect source tags.</div>';
            return;
        }

        const sourceMap = getSourceMapForTrack(primary);
        const rawTags = primary.sourceTags || {};
        const filter = (dom.sourceFilter ? dom.sourceFilter.value : "").trim().toLowerCase();

        const mappedRows = SOURCE_FIELD_OPTIONS.map((field) => {
            const tag = sourceMap[field] || "";
            const values = tag
                ? getTagValuesByName(rawTags, tag).concat(getTagValuesByName(primary.tags || {}, tag))
                : [];
            const dedup = distinctPreserveOrder(values);
            return {
                field,
                tag: tag || "-",
                values: dedup.length > 0 ? dedup.join(", ") : "-"
            };
        });

        const mappedTagSet = new Set(
            Object.values(sourceMap)
                .filter((tag) => typeof tag === "string" && tag.length > 0)
                .map((tag) => tag.toLowerCase())
        );

        const extraRows = listTagKeys(rawTags)
            .filter((key) => !mappedTagSet.has(key.toLowerCase()))
            .sort((a, b) => a.localeCompare(b))
            .map((key) => {
                const values = getTagValuesByName(rawTags, key);
                return {
                    field: "raw",
                    tag: key,
                    values: values.length > 0 ? values.join(", ") : "-"
                };
            });

        const allRows = mappedRows.concat(extraRows).filter((row) => {
            if (!filter) {
                return true;
            }

            const haystack = `${row.field} ${row.tag} ${row.values}`.toLowerCase();
            return haystack.includes(filter);
        });

        if (allRows.length === 0) {
            dom.sourceTags.innerHTML = '<div class="qt-loading">No source tags match the current filter.</div>';
            return;
        }

        dom.sourceTags.innerHTML = `
            <div class="qt-source-grid-head">
                <div>Field</div>
                <div>Tag</div>
                <div>Value</div>
            </div>
            ${allRows.map((row) => `
                <div class="qt-source-row">
                    <div class="qt-source-cell qt-source-field">${escapeHtml(formatSourceFieldLabel(row.field))}</div>
                    <div class="qt-source-cell qt-source-tag" title="${escapeHtml(row.tag)}">${escapeHtml(row.tag)}</div>
                    <div class="qt-source-cell qt-source-value" title="${escapeHtml(row.values)}">${escapeHtml(row.values)}</div>
                </div>
            `).join("")}
        `;
    }

    function buildTrackChanges(track) {
        const changes = [];
        const appendChangedEntries = (entries) => {
            entries.forEach((entry) => {
                if (entry.changed) {
                    changes.push(entry.change);
                }
            });
        };
        appendChangedEntries(getTrackScalarChangeSpecs(track));
        appendChangedEntries(getTrackRawFieldChangeSpecs(track));
        const artworkChange = buildArtworkChange(track);
        if (artworkChange) {
            changes.push(artworkChange);
        }

        const commentValues = buildCommentValues(track);
        if (!valuesEqual(commentValues, track.original.commentValues || [])) {
            changes.push({ type: "raw", tag: commentTagByFormat(track.format), value: commentValues });
        }
        changes.push(...collectSourceTagChanges(track));
        return changes;
    }

    function setSaveButtonsState(saveButtons, disabled, text) {
        saveButtons.forEach((button) => {
            if (!button) {
                return;
            }
            button.disabled = disabled;
            button.textContent = text;
        });
    }

    function replaceSavedTrack(track, replacement) {
        const index = state.tracks.findIndex((item) => item.path === track.path);
        if (index >= 0) {
            state.tracks[index] = replacement;
        }

        const selectedPaths = Array.from(state.selectedPaths);
        if (!selectedPaths.includes(track.path)) {
            return;
        }

        selectedPaths.splice(selectedPaths.indexOf(track.path), 1, replacement.path);
        state.selectedPaths = new Set(selectedPaths);
        if (state.primaryPath === track.path) {
            state.primaryPath = replacement.path;
        }
    }

    async function saveTrackChanges(track) {
        const changes = buildTrackChanges(track);
        if (changes.length === 0) {
            return false;
        }

        const payload = {
            path: track.path,
            format: track.format,
            id3v24: false,
            id3CommLang: null,
            separators: DEFAULT_SEPARATORS,
            changes
        };

        const result = await requestJson(`${API_BASE}/save`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(payload)
        });

        if (result?.file) {
            replaceSavedTrack(track, hydrateTrack(result.file));
        }

        return true;
    }

    async function saveSelectedTracks() {
        if (state.saving) {
            return;
        }

        const selected = getSelectedTracks();
        if (selected.length === 0) {
            notify("Select at least one track.", "warning");
            return;
        }

        const selectedWithChanges = selected.filter((track) => selectedTrackHasChanges(track));
        if (selectedWithChanges.length === 0) {
            const outsideSelectionChanges = state.tracks.filter((track) => selectedTrackHasChanges(track) && !state.selectedPaths.has(track.path));
            if (outsideSelectionChanges.length > 0) {
                notify(`No changes in selected tracks. ${outsideSelectionChanges.length} edited track(s) are outside current selection.`, "warning");
            } else {
                notify("No changes to save for selected tracks.", "info");
            }
            return;
        }

        const saveButtons = [dom.saveSelectedBtn];
        state.saving = true;
        setSaveButtonsState(saveButtons, true, "Saving...");

        let savedCount = 0;
        const failed = [];

        for (const track of selectedWithChanges) {
            try {
                if (await saveTrackChanges(track)) {
                    savedCount += 1;
                }
            } catch (error) {
                failed.push(`${track.title || track.path}: ${error.message}`);
            }
        }

        state.saving = false;
        setSaveButtonsState(saveButtons, false, "Save selected");

        applySearchAndSort();
        renderTrackList();
        renderMoodBar();
        renderGenreBar();
        renderCustomGroups();
        renderSourceTags();

        if (savedCount > 0) {
            notify(`Saved ${savedCount} track(s).`, "success");
        }

        if (failed.length > 0) {
            notify(`Some tracks failed to save (${failed.length}).`, "error");
            console.error("QuickTag save errors", failed);
        }
    }

    function applySearchAndSort() {
        const query = (state.search || "").trim().toLowerCase();

        let tracks = state.tracks.slice();
        if (query) {
            tracks = tracks.filter((track) => {
                const custom = buildCommentValues(track).join(" ").toLowerCase();
                const mood = (track.mood || "").toLowerCase();
                const genres = track.genres.join(" ").toLowerCase();
                const artists = track.artists.join(" ").toLowerCase();
                const album = (track.album || "").toLowerCase();
                const albumArtists = track.albumArtists.join(" ").toLowerCase();
                const composers = track.composers.join(" ").toLowerCase();
                const trackPos = formatPosition(track.trackNumber, track.trackTotal).toLowerCase();
                const discPos = formatPosition(track.discNumber, track.discTotal).toLowerCase();
                const sourceText = listTagKeys(track.sourceTags || {})
                    .map((key) => `${key} ${(track.sourceTags[key] || []).join(" ")}`)
                    .join(" ")
                    .toLowerCase();
                return (
                    (track.title || "").toLowerCase().includes(query)
                    || artists.includes(query)
                    || album.includes(query)
                    || albumArtists.includes(query)
                    || composers.includes(query)
                    || genres.includes(query)
                    || mood.includes(query)
                    || trackPos.includes(query)
                    || discPos.includes(query)
                    || String(track.year || "").includes(query)
                    || String(track.bpm || "").includes(query)
                    || (track.key || "").toLowerCase().includes(query)
                    || sourceText.includes(query)
                    || custom.includes(query)
                    || (track.path || "").toLowerCase().includes(query)
                );
            });
        }

        tracks.sort((a, b) => {
            let av = "";
            let bv = "";

            switch (state.sortField) {
                case "artist":
                    av = a.artists.join(", ").toLowerCase();
                    bv = b.artists.join(", ").toLowerCase();
                    break;
                case "album":
                    av = String(a.album || "").toLowerCase();
                    bv = String(b.album || "").toLowerCase();
                    break;
                case "album-artist":
                    av = a.albumArtists.join(", ").toLowerCase();
                    bv = b.albumArtists.join(", ").toLowerCase();
                    break;
                case "composer":
                    av = a.composers.join(", ").toLowerCase();
                    bv = b.composers.join(", ").toLowerCase();
                    break;
                case "genre":
                    av = a.genres.join(", ").toLowerCase();
                    bv = b.genres.join(", ").toLowerCase();
                    break;
                case "year":
                    av = String(a.year || 0);
                    bv = String(b.year || 0);
                    break;
                case "bpm":
                    av = String(a.bpm || 0);
                    bv = String(b.bpm || 0);
                    break;
                case "track":
                    av = String(a.trackNumber || 0).padStart(5, "0") + String(a.trackTotal || 0).padStart(5, "0");
                    bv = String(b.trackNumber || 0).padStart(5, "0") + String(b.trackTotal || 0).padStart(5, "0");
                    break;
                case "disc":
                    av = String(a.discNumber || 0).padStart(5, "0") + String(a.discTotal || 0).padStart(5, "0");
                    bv = String(b.discNumber || 0).padStart(5, "0") + String(b.discTotal || 0).padStart(5, "0");
                    break;
                case "key":
                    av = String(a.key || "").toLowerCase();
                    bv = String(b.key || "").toLowerCase();
                    break;
                default:
                    av = String(a.title || "").toLowerCase();
                    bv = String(b.title || "").toLowerCase();
                    break;
            }

            if (av < bv) {
                return -1;
            }
            if (av > bv) {
                return 1;
            }
            return 0;
        });

        if (state.sortDescending) {
            tracks.reverse();
        }

        state.filteredTracks = tracks;
    }

    function renderStats() {
        if (!dom.stats) {
            return;
        }

        const failedInfo = state.failed.length > 0
            ? ` | Failed to load: <span class="text-danger">${state.failed.length}</span>`
            : "";

        dom.stats.innerHTML = `Loaded files: <strong>${state.tracks.length}</strong> | Filtered: <strong>${state.filteredTracks.length}</strong>${failedInfo}`;
    }

    function createEnergyStars(track) {
        const stars = [];
        for (let i = 1; i <= 5; i += 1) {
            stars.push(`<button type="button" class="${i <= (track.energy || 0) ? "is-on" : ""}" data-action="set-energy" data-path="${escapeHtml(track.path)}" data-rating="${i}">★</button>`);
        }
        return stars.join("");
    }

    function createCustomChips(track) {
        const chips = [];

        CUSTOM_GROUPS.forEach((group, groupIndex) => {
            (track.custom[groupIndex] || []).forEach((value) => {
                chips.push(`<span class="qt-chip is-tag" data-action="remove-custom" data-kind="custom" data-group="${groupIndex}" data-value="${escapeHtml(value)}" data-path="${escapeHtml(track.path)}">${escapeHtml(value)} <i class="fas fa-times"></i></span>`);
            });
        });

        splitNote(track.note).forEach((value, noteIndex) => {
            chips.push(`<span class="qt-chip is-tag" data-action="remove-custom" data-kind="note" data-group="-1" data-note-index="${noteIndex}" data-value="${escapeHtml(value)}" data-path="${escapeHtml(track.path)}">${escapeHtml(value)} <i class="fas fa-times"></i></span>`);
        });

        return chips.join("");
    }

    function renderTrackList(preserveScroll) {
        if (!dom.trackList || !dom.emptyState) {
            return;
        }

        const keepScroll = !!preserveScroll;
        const viewport = dom.gridViewport;
        const previousScrollLeft = keepScroll && viewport ? viewport.scrollLeft : 0;
        const previousScrollTop = keepScroll && viewport ? viewport.scrollTop : 0;

        renderStats();
        applyColumnVisibility();

        if (state.loadingTracks) {
            dom.emptyState.style.display = "none";
            dom.trackList.style.display = "block";
            dom.trackList.innerHTML = '<div class="qt-loading">Loading tracks...</div>';
            return;
        }

        if (state.tracks.length === 0) {
            dom.trackList.style.display = "none";
            dom.emptyState.style.display = "flex";
            return;
        }

        dom.emptyState.style.display = "none";
        dom.trackList.style.display = "block";

        if (state.filteredTracks.length === 0) {
            dom.trackList.innerHTML = '<div class="qt-loading">No results.</div>';
            return;
        }

        const html = state.filteredTracks.map((track) => {
            const selected = state.selectedPaths.has(track.path);
            const mood = track.mood
                ? `<span class="qt-chip qt-mood-chip ${moodToneClass(track.mood)}" data-action="set-mood" data-mood="${escapeHtml(track.mood)}">${escapeHtml(track.mood)}</span>`
                : '<span class="qt-track-empty">-</span>';

            const keyText = renderEditableField(track.path, "key", track.key || "", "Edit key");
            const bpmText = renderEditableField(track.path, "bpm", track.bpm ? String(track.bpm) : "", "Edit BPM");
            const genreText = track.genres.length > 0
                ? track.genres.map((genre) => escapeHtml(genre)).join(", ")
                : '<span class="qt-track-empty">-</span>';
            const yearText = renderEditableField(track.path, "year", track.year ? String(track.year) : "", "Edit year");
            const albumText = renderEditableField(track.path, "album", track.album || "", "Edit album");
            const albumArtistText = renderEditableField(track.path, "albumArtist", track.albumArtists.join(", "), "Edit album artist");
            const composerText = renderEditableField(track.path, "composer", track.composers.join(", "), "Edit composer");
            const trackText = renderEditableField(track.path, "track", formatPosition(track.trackNumber, track.trackTotal), "Edit track");
            const discText = renderEditableField(track.path, "disc", formatPosition(track.discNumber, track.discTotal), "Edit disc");
            const publisherText = renderEditableField(track.path, "publisher", track.publisher || "", "Edit publisher");
            const copyrightText = renderEditableField(track.path, "copyright", track.copyright || "", "Edit copyright");
            const languageText = renderEditableField(track.path, "language", track.language || "", "Edit language");
            const lyricistText = renderEditableField(track.path, "lyricist", track.lyricist || "", "Edit lyricist");
            const conductorText = renderEditableField(track.path, "conductor", track.conductor || "", "Edit conductor");
            const isrcText = renderEditableField(track.path, "isrc", track.isrc || "", "Edit ISRC");
            const filenameText = escapeHtml(track.filename || "");
            const bitrateText = track.bitrate > 0 ? `${track.bitrate} kbps` : "-";
            const durationText = track.durationMs > 0 ? formatDuration(track.durationMs) : "-";
            const fileSizeText = track.fileSize > 0 ? formatFileSize(track.fileSize) : "-";
            const customChips = createCustomChips(track);
            const customText = customChips || '<span class="qt-track-empty">-</span>';
            const artUrl = buildArtworkThumbUrl(track, 50, true);
            const artHtml = artUrl
                ? `<img class="qt-track-art" src="${artUrl}" alt="Cover" onerror="this.style.opacity='0.4'; this.removeAttribute('src');" />`
                : '<div class="qt-track-art qt-track-art--empty"><i class="fas fa-image"></i></div>';
            const artistText = renderEditableField(track.path, "artist", track.artists.join(", "), "Edit artist");
            const titleText = renderEditableField(track.path, "title", track.title || "", "Edit title");

            return `
                <article class="qt-track ${selected ? "is-selected" : ""}" data-action="select-track" data-path="${escapeHtml(track.path)}">
                    <div class="qt-track-title-cell qt-col-title">
                        ${artHtml}
                        <div class="qt-track-title">${titleText}</div>
                    </div>
                    <div class="qt-track-artist qt-col-artist">${artistText}</div>
                    <div class="qt-track-album qt-col-album">${albumText}</div>
                    <div class="qt-track-album-artist qt-col-album-artist">${albumArtistText}</div>
                    <div class="qt-track-composer qt-col-composer">${composerText}</div>
                    <div class="qt-track-mood qt-col-mood">${mood}</div>
                    <div class="qt-track-stars qt-col-energy">${createEnergyStars(track)}</div>
                    <div class="qt-track-genre qt-col-genre">${genreText}</div>
                    <div class="qt-track-year qt-col-year">${yearText}</div>
                    <div class="qt-track-track qt-col-track">${trackText}</div>
                    <div class="qt-track-disc qt-col-disc">${discText}</div>
                    <div class="qt-track-bpm qt-col-bpm">${bpmText}</div>
                    <div class="qt-track-key qt-col-key">${keyText}</div>
                    <div class="qt-track-publisher qt-col-publisher">${publisherText}</div>
                    <div class="qt-track-copyright qt-col-copyright">${copyrightText}</div>
                    <div class="qt-track-language qt-col-language">${languageText}</div>
                    <div class="qt-track-lyricist qt-col-lyricist">${lyricistText}</div>
                    <div class="qt-track-conductor qt-col-conductor">${conductorText}</div>
                    <div class="qt-track-isrc qt-col-isrc">${isrcText}</div>
                    <div class="qt-track-custom qt-col-custom">${customText}</div>
                    <div class="qt-track-filename qt-col-filename">${filenameText}</div>
                    <div class="qt-track-bitrate qt-col-bitrate">${bitrateText}</div>
                    <div class="qt-track-duration qt-col-duration">${durationText}</div>
                    <div class="qt-track-filesize qt-col-filesize">${fileSizeText}</div>
                </article>
            `;
        }).join("");

        dom.trackList.innerHTML = html;
        applyColumnVisibility();

        if (keepScroll && viewport) {
            viewport.scrollLeft = previousScrollLeft;
            viewport.scrollTop = previousScrollTop;
        }
    }

    function renderFolderList() {
        if (!dom.folderList) {
            return;
        }

        if (state.librarySelectionMode) {
            dom.folderList.innerHTML = '<div class="qt-loading">Select an audio library from the dropdown above.</div>';
            return;
        }

        if (state.loadingFolder) {
            dom.folderList.innerHTML = '<div class="qt-loading">Loading folder...</div>';
            return;
        }

        const filter = [state.folderFilter || "", state.search || ""]
            .join(" ")
            .trim()
            .toLowerCase();
        let folders = state.folders.slice();
        if (filter) {
            folders = folders.filter((entry) => String(entry.filename || "").toLowerCase().includes(filter));
        }

        if (folders.length === 0) {
            dom.folderList.innerHTML = '<div class="qt-loading">No items.</div>';
            return;
        }

        dom.folderList.innerHTML = folders.map((entry) => {
            let icon = "fa-music";
            if (entry.dir) {
                icon = "fa-folder";
            } else if (entry.playlist) {
                icon = "fa-list";
            }

            const active = normalizePath(entry.path) === normalizePath(state.path);

            return `
                <div class="qt-folder-item ${active ? "is-active" : ""}" data-action="open-entry" data-path="${escapeHtml(entry.path)}" data-dir="${entry.dir ? "1" : "0"}" data-playlist="${entry.playlist ? "1" : "0"}">
                    <i class="fas ${icon}"></i>
                    <span>${escapeHtml(entry.filename)}</span>
                </div>
            `;
        }).join("");
    }

    function renderMoodBar() {
        if (!dom.moodBar) {
            return;
        }

        const selected = getSelectedTracks();
        const selectedMood = selected.length > 0 && selected.every((track) => (track.mood || "") === (selected[0].mood || ""))
            ? (selected[0].mood || "")
            : "";

        dom.moodBar.innerHTML = MOODS.map((item) => {
            const isSelected = selectedMood?.toLowerCase() === item.mood.toLowerCase();
            return `<button type="button" class="qt-mood ${moodToneClass(item.mood)} ${isSelected ? "is-selected" : ""}" data-action="mood-toggle" data-mood="${escapeHtml(item.mood)}">${escapeHtml(item.mood)}</button>`;
        }).join("");
    }

    function renderGenreBar() {
        if (!dom.genreBar) {
            return;
        }

        const selected = getSelectedTracks();

        dom.genreBar.innerHTML = allGenreValues.map((genre) => {
            const selectedByAll = selected.length > 0 && selected.every((track) => trackHasGenre(track, genre));
            return `<button type="button" class="qt-genre ${selectedByAll ? "is-selected" : ""}" data-action="genre-toggle" data-genre="${escapeHtml(genre)}">${escapeHtml(genre)}</button>`;
        }).join("");
    }

    function renderCustomGroups() {
        if (!dom.customGroups) {
            return;
        }

        const selected = getSelectedTracks();
        if (selected.length === 0) {
            dom.customGroups.innerHTML = '<div class="qt-loading">Select one or more tracks to edit custom tags.</div>';
            return;
        }

        dom.customGroups.innerHTML = CUSTOM_GROUPS.map((group, groupIndex) => {
            const knownValues = getKnownGroupValues(group, groupIndex);
            const valuesHtml = knownValues.map((value) => {
                const checked = selectedTracksContainCustomValue(selected, groupIndex, value);
                const isBuiltIn = containsIgnoreCase(group.values, value);
                return `
                    <label class="qt-checkbox ${isBuiltIn ? "" : "is-custom"}">
                        <input type="checkbox" data-action="custom-toggle" data-group="${groupIndex}" data-value="${escapeHtml(value)}" ${checked ? "checked" : ""} />
                        <span>${escapeHtml(value)}</span>
                    </label>
                `;
            }).join("");

            return `
                <section class="qt-custom-group">
                    <div class="qt-custom-title">${escapeHtml(group.name)}</div>
                    <div class="qt-custom-values">${valuesHtml}</div>
                    <div class="qt-custom-add">
                        <input type="text" class="qt-input qt-custom-add-input" data-action="custom-add-input" data-group="${groupIndex}" placeholder="Add ${escapeHtml(group.name)} value(s)" autocomplete="off" />
                        <button type="button" class="qt-btn qt-btn-outline qt-custom-add-btn" data-action="custom-add" data-group="${groupIndex}">Add</button>
                    </div>
                </section>
            `;
        }).join("");
    }

    function selectedTracksContainCustomValue(tracks, groupIndex, value) {
        return tracks.every((track) => hasCustomValue(track, groupIndex, value));
    }

    function renderArtworkEditor() {
        if (!dom.artworkMeta || !dom.artworkPreview || !dom.artworkPlaceholder || !dom.artworkContext) {
            return;
        }

        const tracks = getSelectedTracks();
        const hasSelection = tracks.length > 0;
        const primary = getPrimaryTrack() || (tracks.length > 0 ? tracks[0] : null);
        const disableActions = !hasSelection;
        setArtworkActionButtonsDisabled(disableActions);

        if (!hasSelection || !primary) {
            resetArtworkEditorEmptyState();
            return;
        }

        const current = getCurrentArtworkEntry(primary);
        const entry = current.entry;
        updateArtworkEditorControls(current, disableActions);

        const canEditMetadata = entry.key === "cover";
        syncArtworkMetadataInputs(primary, canEditMetadata);
        dom.artworkMeta.textContent = buildArtworkMetaText(tracks, entry);
        showArtworkPreview(entry.key === "cover" ? entry.url : "", entry.label || "No artwork");
    }

    function updateArtworkEditorControls(current, disableActions) {
        const { entry, entries, index } = current;
        if (dom.artworkContext) {
            dom.artworkContext.textContent = entry.key === "cover"
                ? `${index + 1}/${entries.length}`
                : `${entry.label} (${index + 1}/${entries.length})`;
        }
        if (dom.artworkPrevBtn) {
            dom.artworkPrevBtn.disabled = disableActions || index <= 0;
        }
        if (dom.artworkNextBtn) {
            dom.artworkNextBtn.disabled = disableActions || index >= entries.length - 1;
        }
        if (dom.artworkKeepBtn) {
            dom.artworkKeepBtn.classList.toggle("is-selected", entry.key === "keep");
        }
        if (dom.artworkBlankBtn) {
            dom.artworkBlankBtn.classList.toggle("is-selected", entry.key === "blank");
        }
    }

    function applyArtworkChangeToSelection(change) {
        const tracks = getSelectedTracks();
        if (tracks.length === 0) {
            notify("Select at least one track first.", "warning");
            return false;
        }

        if (change?.mode === "keep") {
            tracks.forEach((track) => {
                track.pendingArtwork = null;
            });
            state.artworkViewIndex = 0;
            renderArtworkEditor();
            renderTrackList(true);
            return true;
        }

        if (change?.mode === "clear" && tracks.every((track) => !trackHasEmbeddedArtwork(track))) {
            notify("Selected tracks already have no embedded artwork.", "info");
            return false;
        }

        tracks.forEach((track) => {
            if (change?.mode === "clear" && !trackHasEmbeddedArtwork(track)) {
                track.pendingArtwork = null;
                return;
            }

            if (change?.mode === "set") {
                track.pendingArtwork = {
                    mode: "set",
                    mimeType: change.mimeType || "image/jpeg",
                    dataBase64: change.dataBase64 || "",
                    description: String(change.description ?? (track.artworkDescription || "")),
                    imageType: normalizeArtworkType(change.imageType, track.artworkType)
                };
                track.artworkDescription = track.pendingArtwork.description;
                track.artworkType = track.pendingArtwork.imageType;
                return;
            }

            if (change?.mode === "clear") {
                track.pendingArtwork = { mode: "clear" };
            } else {
                track.pendingArtwork = change;
            }
        });

        if (change?.mode === "set") {
            state.artworkViewIndex = 2;
        } else if (change?.mode === "clear") {
            state.artworkViewIndex = 1;
        }

        renderArtworkEditor();
        renderTrackList(true);
        return true;
    }

    async function materializeCurrentArtwork(track) {
        if (!track) {
            return false;
        }

        if (track.pendingArtwork?.mode === "set" && track.pendingArtwork.dataBase64) {
            return true;
        }

        if (!trackHasEmbeddedArtwork(track)) {
            return false;
        }

        const sourceUrl = buildArtworkThumbUrl(track, 900, false);
        if (!sourceUrl) {
            return false;
        }

        try {
            const response = await fetch(sourceUrl);
            if (!response.ok) {
                return false;
            }
            const blob = await response.blob();
            if (!(blob.type || "").toLowerCase().startsWith("image/")) {
                return false;
            }
            const dataUrl = await blobToDataUrl(blob);
            const parsed = parseDataUrl(dataUrl);
            track.pendingArtwork = {
                mode: "set",
                mimeType: parsed.mimeType,
                dataBase64: parsed.dataBase64,
                description: track.artworkDescription || "",
                imageType: Number.isFinite(Number(track.artworkType)) ? Math.max(0, Math.min(20, Number(track.artworkType))) : 3
            };
            return true;
        } catch {
            return false;
        }
    }

    function updatePlayerMeta() {
        if (!dom.playerMeta) {
            return;
        }

        const track = getPrimaryTrack();
        if (!track) {
            dom.playerMeta.textContent = "No track selected";
            return;
        }

        const title = track.title || "Untitled";
        const artists = track.artists && track.artists.length > 0 ? track.artists.join(", ") : "Unknown artist";
        dom.playerMeta.textContent = `${title} — ${artists}`;
    }

    function updatePlayPauseIcon() {
        if (!dom.playPauseBtn || !dom.audio) {
            return;
        }

        const icon = dom.playPauseBtn.querySelector("i");
        if (!icon) {
            return;
        }

        if (dom.audio.paused) {
            icon.className = "fas fa-play";
        } else {
            icon.className = "fas fa-pause";
        }
    }

    function setSeekPosition() {
        if (!dom.audio || !dom.seek) {
            return;
        }

        const duration = Number.isFinite(dom.audio.duration) ? dom.audio.duration : 0;
        dom.seek.max = String(duration);
        dom.seek.value = String(dom.audio.currentTime || 0);
    }

    function loadAudioForPrimary(autoPlay) {
        const track = getPrimaryTrack();
        if (!track || !dom.audio) {
            if (dom.audio) {
                dom.audio.removeAttribute("src");
                dom.audio.load();
            }
            updatePlayerMeta();
            updatePlayPauseIcon();
            return;
        }

        const src = `${API_BASE}/audio?path=${encodeURIComponent(track.path)}`;
        if (dom.audio.src.endsWith(src)) {
            if (autoPlay) {
                dom.audio.play().catch(() => null);
            }
            return;
        }

        dom.audio.src = src;
        dom.audio.load();

        if (autoPlay) {
            dom.audio.play().catch(() => null);
        }

        updatePlayerMeta();
        updatePlayPauseIcon();
    }

    function focusTrackRelative(offset) {
        if (state.filteredTracks.length === 0) {
            return;
        }

        const currentPath = state.primaryPath || state.filteredTracks[0]?.path || "";
        let index = state.filteredTracks.findIndex((track) => track.path === currentPath);
        if (index < 0) {
            index = 0;
        }

        const nextIndex = Math.min(state.filteredTracks.length - 1, Math.max(0, index + offset));
        const nextTrack = state.filteredTracks[nextIndex];
        if (!nextTrack) {
            return;
        }

        applySelection([nextTrack.path], nextTrack.path);
        loadAudioForPrimary(false);

        const trackElement = dom.trackList ? dom.trackList.querySelector(`[data-action="select-track"][data-path="${CSS.escape(nextTrack.path)}"]`) : null;
        if (trackElement && typeof trackElement.scrollIntoView === "function") {
            trackElement.scrollIntoView({ behavior: "smooth", block: "center" });
        }
    }

    async function loadTracks() {
        if (state.librarySelectionMode || !state.selectedLibraryRoot) {
            state.tracks = [];
            state.filteredTracks = [];
            state.failed = [];
            applySelection([], null);
            renderTrackList();
            return;
        }

        state.loadingTracks = true;
        renderTrackList();

        try {
            const payload = {
                path: state.path,
                recursive: false,
                separators: DEFAULT_SEPARATORS
            };

            const result = await requestJson(`${API_BASE}/load`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(payload)
            });

            const tracks = Array.isArray(result.files) ? result.files.map(hydrateTrack) : [];
            state.tracks = tracks;
            state.failed = Array.isArray(result.failed) ? result.failed : [];

            if (tracks.length === 0) {
                applySelection([], null);
            } else {
                const existing = Array.from(state.selectedPaths).filter((path) => tracks.some((track) => track.path === path));
                if (existing.length > 0) {
                    applySelection(existing, existing[0]);
                } else {
                    applySelection([tracks[0].path], tracks[0].path);
                }
            }

            applySearchAndSort();
            renderTrackList();
            renderMoodBar();
            renderGenreBar();
            renderCustomGroups();
            renderSourceTags();
            updatePlayerMeta();
            loadAudioForPrimary(false);
        } catch (error) {
            state.tracks = [];
            state.filteredTracks = [];
            state.failed = [];
            applySelection([], null);
            renderTrackList();
            notify(`Failed to load tracks: ${error.message}`, "error");
        } finally {
            state.loadingTracks = false;
            renderTrackList();
        }
    }

    async function loadFolder(subdir) {
        if (state.librarySelectionMode || !state.selectedLibraryRoot) {
            state.loadingFolder = false;
            state.folders = [];
            renderFolderList();
            return;
        }

        state.loadingFolder = true;
        renderFolderList();

        try {
            const query = new URLSearchParams();
            if (state.browserPath) {
                query.set("path", state.browserPath);
            } else if (state.path) {
                query.set("path", state.path);
            }
            if (subdir) {
                query.set("subdir", subdir);
            }

            const result = await requestJson(`${API_BASE}/folder?${query.toString()}`);
            state.browserPath = result.path || state.browserPath || state.path || "";
            state.folders = Array.isArray(result.files) ? result.files : [];

            if (!state.path) {
                state.path = state.browserPath;
            }

            if (dom.pathInput) {
                dom.pathInput.value = state.path;
            }

            renderFolderList();
        } catch (error) {
            state.folders = [];
            renderFolderList();
            notify(`Failed to load folder: ${error.message}`, "error");
        } finally {
            state.loadingFolder = false;
            renderFolderList();
        }
    }

    async function navigateToPath(path, refreshFolder) {
        if (state.librarySelectionMode || !state.selectedLibraryRoot) {
            return;
        }

        const normalized = normalizePath(path);
        if (!normalized) {
            return;
        }

        if (!pathWithinRoot(normalized, state.selectedLibraryRoot)) {
            notify("Path is outside the selected library.", "warning");
            return;
        }

        state.path = normalized;
        localStorage.setItem(QUICKTAG_PATH_KEY, state.path);
        if (dom.pathInput) {
            dom.pathInput.value = state.path;
        }

        if (refreshFolder) {
            state.browserPath = normalized;
            await loadFolder();
        }

        await loadTracks();
    }

    function changeArtworkView(step) {
        const primary = getPrimaryTrack();
        const entries = getArtworkEntries(primary);
        state.artworkViewIndex = Math.max(0, Math.min(entries.length - 1, state.artworkViewIndex + step));
        const current = getCurrentArtworkEntry(primary);
        if (current.entry?.key === "keep") {
            applyArtworkChangeToSelection({ mode: "keep" });
            return;
        }
        if (current.entry?.key === "blank") {
            applyArtworkChangeToSelection({ mode: "clear" });
            return;
        }
        renderArtworkEditor();
    }

    function handleArtworkModeClick(mode, message, tone) {
        if (applyArtworkChangeToSelection({ mode })) {
            notify(message, tone);
        }
    }

    async function handleArtworkFileChange() {
        const file = dom.artworkFileInput.files?.[0];
        dom.artworkFileInput.value = "";
        if (!file) {
            return;
        }
        if (!(file.type || "").toLowerCase().startsWith("image/")) {
            notify("Selected file is not an image.", "warning");
            return;
        }

        try {
            const dataUrl = await blobToDataUrl(file);
            const parsed = parseDataUrl(dataUrl);
            const applied = applyArtworkChangeToSelection({
                mode: "set",
                mimeType: parsed.mimeType,
                dataBase64: parsed.dataBase64,
                description: getArtworkDescriptionInputValue(),
                imageType: getArtworkTypeInputValue()
            });
            if (applied) {
                state.artworkViewIndex = 2;
                notify(state.artworkFileMode === "change"
                    ? "Cover changed for selected tracks."
                    : "Cover added for selected tracks.", "success");
            }
        } catch (error) {
            notify(error.message || "Failed to read image.", "error");
        }
    }

    async function updateArtworkSelectionMetadata(update) {
        const tracks = getSelectedTracks();
        if (tracks.length === 0) {
            return;
        }

        for (const track of tracks) {
            const materialized = await materializeCurrentArtwork(track);
            if (!materialized || track.pendingArtwork?.mode !== "set") {
                continue;
            }
            update(track);
        }

        renderArtworkEditor();
    }

    async function handleTrackListClick(event) {
        const actionTarget = event.target.closest("[data-action]");
        if (!actionTarget) {
            return;
        }

        const action = actionTarget.dataset.action;
        if (action === "set-energy") {
            event.preventDefault();
            event.stopPropagation();
            const path = actionTarget.dataset.path;
            const rating = Number.parseInt(actionTarget.dataset.rating, 10);
            if (path && Number.isFinite(rating)) {
                setEnergy(path, rating);
            }
            return;
        }

        if (action === "remove-custom") {
            event.preventDefault();
            event.stopPropagation();
            const path = actionTarget.dataset.path;
            const group = Number.parseInt(actionTarget.dataset.group, 10);
            const value = actionTarget.dataset.value || "";
            const kind = actionTarget.dataset.kind || "custom";
            if (path && value) {
                removeCustomChip(path, group, value, kind);
            }
            return;
        }

        if (action === "set-mood") {
            event.preventDefault();
            event.stopPropagation();
            const mood = actionTarget.dataset.mood;
            if (mood) {
                setMoodForSelected(mood);
            }
            return;
        }

        if (action === "select-track") {
            const path = actionTarget.dataset.path;
            if (path) {
                await onTrackSelect(path, event.ctrlKey || event.metaKey);
            }
        }
    }

    function handlePlayPauseClick() {
        const track = getPrimaryTrack();
        if (!track) {
            return;
        }
        if (!dom.audio.src) {
            loadAudioForPrimary(true);
            return;
        }
        if (dom.audio.paused) {
            dom.audio.play().catch(() => null);
            return;
        }
        dom.audio.pause();
    }

    async function handleGlobalKeydown(event) {
        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "s") {
            event.preventDefault();
            await saveSelectedTracks();
            return;
        }
        if (isTextInputActive()) {
            return;
        }
        if (event.key === " ") {
            event.preventDefault();
            if (dom.playPauseBtn) {
                dom.playPauseBtn.click();
            }
            return;
        }
        if (event.key === "ArrowUp") {
            event.preventDefault();
            focusTrackRelative(-1);
            return;
        }
        if (event.key === "ArrowDown") {
            event.preventDefault();
            focusTrackRelative(1);
            return;
        }
        if (dom.audio && event.key === "ArrowLeft") {
            event.preventDefault();
            dom.audio.currentTime = Math.max(0, (dom.audio.currentTime || 0) - 10);
            return;
        }
        if (dom.audio && event.key === "ArrowRight") {
            event.preventDefault();
            dom.audio.currentTime = Math.min(dom.audio.duration || 0, (dom.audio.currentTime || 0) + 30);
        }
    }

    async function reloadCurrentPath() {
        if (state.librarySelectionMode || !state.selectedLibraryRoot) {
            return;
        }
        await loadFolder();
        await loadTracks();
    }

    function handleFolderFilterInput() {
        state.folderFilter = dom.folderFilterInput ? (dom.folderFilterInput.value || "") : "";
        renderFolderList();
    }

    function queueArtworkFileInput(mode) {
        state.artworkFileMode = mode;
        if (dom.artworkFileInput) {
            dom.artworkFileInput.click();
        }
    }

    async function handleArtworkCopyClick() {
        const primary = getPrimaryTrack();
        if (!primary) {
            notify("No track selected to copy cover from.", "warning");
            return;
        }

        let copied = null;
        if (primary.pendingArtwork?.mode === "set" && primary.pendingArtwork.dataBase64) {
            copied = {
                mimeType: primary.pendingArtwork.mimeType || "image/jpeg",
                dataBase64: primary.pendingArtwork.dataBase64
            };
        } else {
            const ok = await materializeCurrentArtwork(primary);
            if (ok && primary.pendingArtwork?.mode === "set") {
                copied = {
                    mimeType: primary.pendingArtwork.mimeType || "image/jpeg",
                    dataBase64: primary.pendingArtwork.dataBase64
                };
            }
        }

        if (!copied) {
            notify("Unable to copy current cover.", "warning");
            return;
        }

        state.artworkClipboard = copied;
        notify("Cover copied.", "success");
    }

    async function handleArtworkPasteClick() {
        let clipboardImage = state.artworkClipboard?.dataBase64
            ? state.artworkClipboard
            : null;

        if (!clipboardImage) {
            clipboardImage = await readImageFromSystemClipboard();
            if (clipboardImage?.dataBase64) {
                state.artworkClipboard = clipboardImage;
            }
        }

        if (!clipboardImage?.dataBase64) {
            notify("No copied cover available.", "warning");
            return;
        }

        const applied = applyArtworkChangeToSelection({
            mode: "set",
            mimeType: clipboardImage.mimeType || "image/jpeg",
            dataBase64: clipboardImage.dataBase64,
            description: getArtworkDescriptionInputValue(),
            imageType: getArtworkTypeInputValue()
        });
        if (applied) {
            state.artworkViewIndex = 2;
            notify("Cover pasted to selected tracks.", "success");
        }
    }

    function handleArtworkSaveToFileClick() {
        const primary = getPrimaryTrack();
        const current = getCurrentArtworkEntry(primary);
        if (!primary || current.entry?.key !== "cover") {
            notify("No cover selected to save.", "warning");
            return;
        }

        const link = document.createElement("a");
        link.href = current.entry.url;
        const safeTitle = (primary.title || "cover").replaceAll(/[^\w-]+/g, "_");
        link.download = `${safeTitle}_cover.jpg`;
        document.body.appendChild(link);
        link.click();
        link.remove();
    }

    async function handleArtworkDescriptionChange() {
        await updateArtworkSelectionMetadata((track) => {
            track.pendingArtwork.description = dom.artworkDescription ? (dom.artworkDescription.value || "") : "";
            track.artworkDescription = track.pendingArtwork.description;
        });
    }

    async function handleArtworkTypeChange() {
        const nextType = dom.artworkType && Number.isFinite(Number(dom.artworkType.value))
            ? Math.max(0, Math.min(20, Number(dom.artworkType.value)))
            : 3;
        await updateArtworkSelectionMetadata((track) => {
            track.pendingArtwork.imageType = nextType;
            track.artworkType = nextType;
        });
    }

    function handleSearchInput() {
        state.search = dom.searchInput ? (dom.searchInput.value || "") : "";
        applySearchAndSort();
        renderTrackList();
        renderFolderList();
    }

    function handleColumnToggleChange(event) {
        const input = event.target.closest("[data-action='column-toggle']");
        if (!input) {
            return;
        }

        const column = input.dataset.column;
        if (!column) {
            return;
        }

        toggleColumnVisibility(column, !!input.checked);
    }

    function updateSortDirectionIcon() {
        if (!dom.sortDirectionBtn) {
            return;
        }
        const icon = dom.sortDirectionBtn.querySelector("i");
        if (icon) {
            icon.className = state.sortDescending ? "fas fa-arrow-down" : "fas fa-arrow-up";
        }
    }

    function activateSortChip(chip) {
        dom.sortChips.forEach((node) => node.classList.toggle("is-active", node === chip));
    }

    function applySortAndRender() {
        updateSortDirectionIcon();
        applySearchAndSort();
        renderTrackList();
    }

    function onSortChipClick(chip) {
        const sort = chip.dataset.sort;
        if (!sort) {
            return;
        }

        if (state.sortField === sort) {
            state.sortDescending = !state.sortDescending;
        } else {
            state.sortField = sort;
            state.sortDescending = false;
        }

        activateSortChip(chip);
        applySortAndRender();
    }

    function handleTrackListDoubleClick(event) {
        const editable = event.target.closest("[data-action='inline-edit']");
        if (editable) {
            editable.focus();
        }
    }

    function handleTrackListInlineKeydown(event) {
        const editable = event.target.closest("[data-action='inline-edit']");
        if (!editable || event.key !== "Enter") {
            return;
        }
        event.preventDefault();
        editable.blur();
    }

    function handleTrackListInlinePaste(event) {
        const editable = event.target.closest("[data-action='inline-edit']");
        if (!editable) {
            return;
        }
        event.preventDefault();
        const text = event.clipboardData ? event.clipboardData.getData("text/plain") : "";
        insertPlainTextAtSelection(editable, text);
    }

    function handleTrackListInlineFocusOut(event) {
        const editable = event.target.closest("[data-action='inline-edit']");
        if (!editable) {
            return;
        }

        const path = editable.dataset.path;
        const field = editable.dataset.editField;
        if (!path || !field) {
            return;
        }

        commitInlineField(path, field, editable.textContent || "");
    }

    function handleTrackListInlineFocusIn(event) {
        const editable = event.target.closest("[data-action='inline-edit']");
        if (editable && typeof editable.scrollIntoView === "function") {
            editable.scrollIntoView({ block: "nearest", inline: "center" });
        }
    }

    function handleTagSourceResultSelected() {
        if (!dom.tagSourceResults) {
            return;
        }

        const selected = dom.tagSourceResults.options[dom.tagSourceResults.selectedIndex];
        if (!selected) {
            setTagSourceResultMeta("");
            hideTagSourceDetail();
            return;
        }

        const details = selected.dataset.details || "";
        const url = selected.dataset.url || "";
        setTagSourceResultMeta(details || url || "");

        const itemId = selected.dataset.id || "";
        if (itemId && state.tagSourceProvider) {
            fetchTagSourceDetail(state.tagSourceProvider, itemId);
            return;
        }
        hideTagSourceDetail();
    }

    function handleApplySourceClick(event) {
        const applyBtn = event.target.closest("[data-action='apply-source']");
        if (!applyBtn) {
            return;
        }

        event.stopPropagation();
        const cell = applyBtn.closest("[data-field]");
        if (!cell) {
            return;
        }

        const field = cell.dataset.field;
        const value = cell.dataset.value;
        if (!field) {
            return;
        }

        applyTagSourceField(field, value);
        cell.classList.add("qt-source-applied");
        setTimeout(() => cell.classList.remove("qt-source-applied"), 600);
    }

    function handleMoodToggleClick(event) {
        const target = event.target.closest("[data-action='mood-toggle']");
        const mood = target ? target.dataset.mood : "";
        if (mood) {
            setMoodForSelected(mood);
        }
    }

    function handleGenreToggleClick(event) {
        const target = event.target.closest("[data-action='genre-toggle']");
        const genre = target ? target.dataset.genre : "";
        if (genre) {
            toggleGenreForSelected(genre);
        }
    }

    function clearCustomInputIfAdded(group, input) {
        if (input && addCustomValuesForSelected(group, input.value)) {
            input.value = "";
        }
    }

    function handleCustomAddClick(event) {
        const target = event.target.closest("[data-action='custom-add']");
        if (!target) {
            return;
        }
        const group = Number.parseInt(target.dataset.group, 10);
        if (!Number.isFinite(group)) {
            return;
        }
        const container = target.closest(".qt-custom-add");
        const input = container ? container.querySelector("[data-action='custom-add-input']") : null;
        clearCustomInputIfAdded(group, input);
    }

    function handleCustomAddKeydown(event) {
        const input = event.target.closest("[data-action='custom-add-input']");
        if (!input || event.key !== "Enter") {
            return;
        }
        event.preventDefault();
        const group = Number.parseInt(input.dataset.group, 10);
        if (Number.isFinite(group)) {
            clearCustomInputIfAdded(group, input);
        }
    }

    function handleCustomToggleChange(event) {
        const input = event.target.closest("input[data-action='custom-toggle']");
        if (!input) {
            return;
        }
        const group = Number.parseInt(input.dataset.group, 10);
        const value = input.dataset.value || "";
        if (Number.isFinite(group) && value) {
            toggleCustomForSelected(group, value);
        }
    }

    async function handleAddNoteClick() {
        const tracks = getSelectedTracks();
        if (tracks.length === 0) {
            notify("Select a track first.", "warning");
            return;
        }

        const initial = tracks.length === 1 ? tracks[0].note || "" : "";
        let value = null;
        if (typeof globalThis.DeezSpoTag?.ui?.prompt === "function") {
            value = await globalThis.DeezSpoTag.ui.prompt("Enter note values separated by commas.", {
                title: "Custom Note",
                value: initial,
                placeholder: "e.g. Intro, Warmup"
            });
        } else {
            value = globalThis.prompt("Enter note values separated by commas.", initial);
        }

        if (value == null) {
            return;
        }

        tracks.forEach((track) => {
            track.note = String(value || "").trim();
        });
        renderTrackList();
        renderCustomGroups();
    }

    function handleSeekInput() {
        if (!dom.seek || !dom.audio) {
            return;
        }
        const value = Number.parseFloat(dom.seek.value);
        if (Number.isFinite(value)) {
            dom.audio.currentTime = value;
        }
    }

    function handleVolumeInput() {
        if (!dom.volume || !dom.audio) {
            return;
        }
        const value = Number.parseFloat(dom.volume.value);
        if (Number.isFinite(value)) {
            dom.audio.volume = Math.min(1, Math.max(0, value));
        }
    }

    function handleAudioEnded() {
        updatePlayPauseIcon();
        focusTrackRelative(1);
    }

    function onSortDirectionButtonClick() {
        state.sortDescending = !state.sortDescending;
        applySortAndRender();
    }

    function onSourceTemplateChange() {
        if (!dom.sourceTemplate) {
            return;
        }
        state.sourceTemplate = dom.sourceTemplate.value || "auto";
        localStorage.setItem(QUICKTAG_SOURCE_TEMPLATE_KEY, state.sourceTemplate);
        if (globalThis.UserPrefs) globalThis.UserPrefs.set("quickTagSourceTemplate", state.sourceTemplate);
        renderSourceTags();
    }

    function onTagSourceProviderChange() {
        if (!dom.tagSourceProvider) {
            return;
        }
        persistTagSourceProvider(String(dom.tagSourceProvider.value || "spotify").trim().toLowerCase());
        renderTagSourceContext();
    }

    function onDumpSelectedClick() {
        showTagDump().catch((error) => notify(error.message || "Failed to load tag dump.", "error"));
    }

    function onSaveSelectedClick() {
        saveSelectedTracks().catch((error) => notify(error.message || "Save failed.", "error"));
    }

    function bindLibraryEvents() {
        if (dom.librarySelect) dom.librarySelect.addEventListener("change", handleLibrarySelectChange);
        if (dom.pathInput) dom.pathInput.addEventListener("keydown", handlePathInputKeydown);
        if (dom.pathUpBtn) dom.pathUpBtn.addEventListener("click", handlePathUpClick);
        if (dom.pathReloadBtn) dom.pathReloadBtn.addEventListener("click", () => reloadCurrentPath());
        if (dom.folderFilterInput) dom.folderFilterInput.addEventListener("input", handleFolderFilterInput);
        if (dom.folderList) dom.folderList.addEventListener("click", handleFolderEntryClick);
    }

    function bindArtworkEvents() {
        if (dom.artworkPrevBtn) dom.artworkPrevBtn.addEventListener("click", () => changeArtworkView(-1));
        if (dom.artworkNextBtn) dom.artworkNextBtn.addEventListener("click", () => changeArtworkView(1));
        if (dom.artworkKeepBtn) dom.artworkKeepBtn.addEventListener("click", () => handleArtworkModeClick("keep", "Artwork mode set to <keep>.", "info"));
        if (dom.artworkBlankBtn) dom.artworkBlankBtn.addEventListener("click", () => handleArtworkModeClick("clear", "Artwork mode set to <blank>.", "info"));
        if (dom.artworkAddBtn && dom.artworkFileInput) dom.artworkAddBtn.addEventListener("click", () => queueArtworkFileInput("add"));
        if (dom.artworkChangeBtn && dom.artworkFileInput) dom.artworkChangeBtn.addEventListener("click", () => queueArtworkFileInput("change"));
        if (dom.artworkFileInput) dom.artworkFileInput.addEventListener("change", handleArtworkFileChange);
        if (dom.artworkRemoveBtn) dom.artworkRemoveBtn.addEventListener("click", () => handleArtworkModeClick("clear", "Cover removed for selected tracks.", "info"));
        if (dom.artworkCopyBtn) dom.artworkCopyBtn.addEventListener("click", () => handleArtworkCopyClick());
        if (dom.artworkPasteBtn) dom.artworkPasteBtn.addEventListener("click", () => handleArtworkPasteClick());
        if (dom.artworkSaveToFileBtn) dom.artworkSaveToFileBtn.addEventListener("click", handleArtworkSaveToFileClick);
        if (dom.artworkDescription) dom.artworkDescription.addEventListener("change", () => handleArtworkDescriptionChange());
        if (dom.artworkType) dom.artworkType.addEventListener("change", () => handleArtworkTypeChange());
    }

    function bindSortAndFilterEvents() {
        if (dom.searchInput) dom.searchInput.addEventListener("input", handleSearchInput);
        if (dom.columnPreset) dom.columnPreset.addEventListener("change", () => setColumnPreset(dom.columnPreset.value));
        if (dom.columnToggles) dom.columnToggles.addEventListener("change", handleColumnToggleChange);
        dom.sortChips.forEach((chip) => chip.addEventListener("click", () => onSortChipClick(chip)));
        if (dom.sortDirectionBtn) dom.sortDirectionBtn.addEventListener("click", onSortDirectionButtonClick);
    }

    function bindTrackListEvents() {
        if (!dom.trackList) {
            return;
        }
        dom.trackList.addEventListener("click", handleTrackListClick);
        dom.trackList.addEventListener("dblclick", handleTrackListDoubleClick);
        dom.trackList.addEventListener("keydown", handleTrackListInlineKeydown);
        dom.trackList.addEventListener("paste", handleTrackListInlinePaste);
        dom.trackList.addEventListener("focusout", handleTrackListInlineFocusOut);
        dom.trackList.addEventListener("focusin", handleTrackListInlineFocusIn);
        dom.trackList.addEventListener("click", handleApplySourceClick);
    }

    function bindTagSourceEvents() {
        if (dom.sourceTemplate) dom.sourceTemplate.addEventListener("change", onSourceTemplateChange);
        if (dom.sourceFilter) dom.sourceFilter.addEventListener("input", renderSourceTags);
        if (dom.tagSourceProvider) dom.tagSourceProvider.addEventListener("change", onTagSourceProviderChange);
        if (dom.tagSourceSearchBtn) dom.tagSourceSearchBtn.addEventListener("click", () => runTagSourceSearch());
        if (dom.tagSourceResults) {
            dom.tagSourceResults.addEventListener("change", handleTagSourceResultSelected);
            dom.tagSourceResults.addEventListener("dblclick", handleTagSourceResultSelected);
        }
        if (dom.tagSourceApplyAll) dom.tagSourceApplyAll.addEventListener("click", applyAllTagSourceFields);
        if (dom.tagSourceApplyCover) dom.tagSourceApplyCover.addEventListener("click", () => applyTagSourceCover());
    }

    function bindCustomTagEvents() {
        if (dom.moodBar) dom.moodBar.addEventListener("click", handleMoodToggleClick);
        if (dom.genreBar) dom.genreBar.addEventListener("click", handleGenreToggleClick);
        if (dom.customGroups) {
            dom.customGroups.addEventListener("click", handleCustomAddClick);
            dom.customGroups.addEventListener("keydown", handleCustomAddKeydown);
            dom.customGroups.addEventListener("change", handleCustomToggleChange);
        }
        if (dom.addNoteBtn) dom.addNoteBtn.addEventListener("click", () => handleAddNoteClick());
        if (dom.sortValuesBtn) dom.sortValuesBtn.addEventListener("click", sortSelectedCustomValues);
        if (dom.dumpSelectedBtn) dom.dumpSelectedBtn.addEventListener("click", onDumpSelectedClick);
        if (dom.saveSelectedBtn) dom.saveSelectedBtn.addEventListener("click", onSaveSelectedClick);
        if (dom.prevTrackBtn) dom.prevTrackBtn.addEventListener("click", () => focusTrackRelative(-1));
        if (dom.nextTrackBtn) dom.nextTrackBtn.addEventListener("click", () => focusTrackRelative(1));
    }

    function bindPlaybackEvents() {
        if (dom.playPauseBtn && dom.audio) dom.playPauseBtn.addEventListener("click", handlePlayPauseClick);
        if (dom.seek && dom.audio) dom.seek.addEventListener("input", handleSeekInput);
        if (dom.volume && dom.audio) dom.volume.addEventListener("input", handleVolumeInput);
        if (!dom.audio) {
            return;
        }
        dom.audio.addEventListener("loadedmetadata", setSeekPosition);
        dom.audio.addEventListener("timeupdate", setSeekPosition);
        dom.audio.addEventListener("play", updatePlayPauseIcon);
        dom.audio.addEventListener("pause", updatePlayPauseIcon);
        dom.audio.addEventListener("ended", handleAudioEnded);
    }

    function bindEvents() {
        bindLibraryEvents();
        bindArtworkEvents();
        bindSortAndFilterEvents();
        bindTrackListEvents();
        bindTagSourceEvents();
        bindCustomTagEvents();
        bindPlaybackEvents();
        globalThis.addEventListener("keydown", handleGlobalKeydown);
    }

    function applyResizerDrag(page, side, dragContext, limits, moveEvent) {
        const dx = moveEvent.clientX - dragContext.startX;
        const totalWidth = dragContext.shellWidth;

        if (side === "left") {
            let newLeft = dragContext.startLeft + dx;
            const maxLeft = totalWidth - dragContext.startRight - limits.minCenter - 12;
            newLeft = Math.max(limits.minLeft, Math.min(newLeft, maxLeft));
            page.style.setProperty("--qt-left-col", Math.round(newLeft) + "px");
            return;
        }

        let newRight = dragContext.startRight - dx;
        const maxRight = totalWidth - dragContext.startLeft - limits.minCenter - 12;
        newRight = Math.max(limits.minRight, Math.min(newRight, maxRight));
        page.style.setProperty("--qt-right-col", Math.round(newRight) + "px");
    }

    function persistResizerWidths(page, storageKeys) {
        const finalLeft = Number.parseFloat(getComputedStyle(page).getPropertyValue("--qt-left-col")) || 280;
        const finalRight = Number.parseFloat(getComputedStyle(page).getPropertyValue("--qt-right-col")) || 180;
        localStorage.setItem(storageKeys.left, Math.round(finalLeft));
        localStorage.setItem(storageKeys.right, Math.round(finalRight));
        if (globalThis.UserPrefs) {
            globalThis.UserPrefs.set("quickTagPanelLeftWidth", Math.round(finalLeft));
            globalThis.UserPrefs.set("quickTagPanelRightWidth", Math.round(finalRight));
        }
    }

    function bindSingleResizerPointer(page, shell, resizer, limits, storageKeys) {
        const side = resizer.dataset.resizer;
        if (!side) {
            return;
        }

        resizer.addEventListener("pointerdown", (startEvent) => {
            startEvent.preventDefault();
            resizer.classList.add("qt-resizer--active");
            resizer.setPointerCapture(startEvent.pointerId);

            const computedStyle = getComputedStyle(page);
            const dragContext = {
                startX: startEvent.clientX,
                shellWidth: shell.getBoundingClientRect().width,
                startLeft: Number.parseFloat(computedStyle.getPropertyValue("--qt-left-col")) || 280,
                startRight: Number.parseFloat(computedStyle.getPropertyValue("--qt-right-col")) || 180
            };

            const onMove = (moveEvent) => applyResizerDrag(page, side, dragContext, limits, moveEvent);
            const onUp = () => {
                resizer.classList.remove("qt-resizer--active");
                resizer.removeEventListener("pointermove", onMove);
                resizer.removeEventListener("pointerup", onUp);
                resizer.removeEventListener("pointercancel", onUp);
                persistResizerWidths(page, storageKeys);
            };

            resizer.addEventListener("pointermove", onMove);
            resizer.addEventListener("pointerup", onUp);
            resizer.addEventListener("pointercancel", onUp);
        });
    }

    function initPanelResizers() {
        const limits = {
            minLeft: 180,
            minRight: 140,
            minCenter: 300
        };
        const storageKeys = {
            left: "quicktag_left_col",
            right: "quicktag_right_col"
        };

        const page = document.querySelector(".qt-page");
        const shell = document.querySelector(".qt-shell");
        if (!page || !shell) {
            return;
        }

        const savedLeft = localStorage.getItem(storageKeys.left);
        const savedRight = localStorage.getItem(storageKeys.right);
        if (savedLeft) {
            page.style.setProperty("--qt-left-col", savedLeft + "px");
        }
        if (savedRight) {
            page.style.setProperty("--qt-right-col", savedRight + "px");
        }

        const resizers = shell.querySelectorAll("[data-resizer]");
        resizers.forEach((resizer) => {
            bindSingleResizerPointer(page, shell, resizer, limits, storageKeys);
        });
    }

    async function initialize() {
        loadSourceSettings();
        loadTagSourceSettings();
        loadColumnSettings();
        bindEvents();
        initPanelResizers();
        renderColumnControls();
        applyColumnVisibility();
        renderSourceTags();
        renderTagSourceContext();

        try {
            await loadLibraries();
        } catch (error) {
            notify(`Failed to load libraries: ${error.message}`, "error");
        }

        if (state.libraries.length === 0) {
            notify("No audio libraries are configured.", "warning");
        }
        const launchTrackId = parseLaunchTrackId();
        let launchOpened = false;
        if (launchTrackId) {
            try {
                launchOpened = await openTrackFromLaunch(launchTrackId);
            } catch (error) {
                notify(error.message || "Failed to open selected track in Tag Editor.", "warning");
            }
        }

        if (!launchOpened) {
            enterLibrarySelectionMode();
        }
        renderMoodBar();
        renderGenreBar();
        renderCustomGroups();
        renderSourceTags();
        renderTagSourceContext();
        updatePlayerMeta();
    }

    initialize().catch((error) => {
        notify(`Tag Editor failed to initialize: ${error.message}`, "error");
    });
})();
