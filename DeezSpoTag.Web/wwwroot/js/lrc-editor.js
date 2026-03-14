(() => {
    const state = {
        trackId: null,
        trackInfo: null,
        audio: new Audio(),
        entries: [],
        mode: 'edit',
        activeIndex: 0,
        syncHandleIndex: 0,
        currentIndex: -1,
        mergeEnabled: false,
        durationMs: 0,
        editorHover: false,
        embedLyrics: false,
        settingsCache: null,
        metadata: {
            ar: '',
            al: '',
            ti: '',
            by: '',
            length: '',
            offset: '',
            re: '',
            ve: ''
        }
    };

    const elements = {};
    let trackModal;
    let rawLyricsModal;
    let currentBrowsePath = '';

    function uiAlert(message, options = {}) {
        if (globalThis.DeezSpoTag?.ui?.alert) {
            return globalThis.DeezSpoTag.ui.alert(message, options);
        }
        alert(message);
        return Promise.resolve(true);
    }

    function uiConfirm(message, options = {}) {
        if (globalThis.DeezSpoTag?.ui?.confirm) {
            return globalThis.DeezSpoTag.ui.confirm(message, options);
        }
        return Promise.resolve(confirm(message));
    }

    function uiToast(message, type = 'info') {
        if (globalThis.DeezSpoTag?.showNotification) {
            globalThis.DeezSpoTag.showNotification(message, type);
            return;
        }
        uiAlert(message);
    }

    function initElements() {
        elements.audiofilebtn = document.getElementById('audiofilebtn');
        elements.playbackbtn = document.getElementById('playbackbtn');
        elements.stopbtn = document.getElementById('stopbtn');
        elements.volumedown = document.getElementById('volumedown');
        elements.volumeup = document.getElementById('volumeup');
        elements.volumedisp = document.getElementById('volumedisp');
        elements.seekbar = document.getElementById('seekbar');
        elements.entrySticks = document.getElementById('entrySticks');
        elements.position = document.getElementById('position');
        elements.duration = document.getElementById('duration');
        elements.statusFileName = document.getElementById('statusFileName');
        elements.statusFileType = document.getElementById('statusFileType');
        elements.modebtn = document.getElementById('lrcmodebtn');
        elements.modename = elements.modebtn?.querySelector('.modename');
        elements.editor = document.getElementById('lrcEditor');
        elements.mergeToggle = document.querySelector('#lrcmergetogglebtn .status');
        elements.exportBtn = document.getElementById('lrcexportbtn');
        elements.exportNoMetaBtn = document.getElementById('lrcexportnometabtn');
        elements.saveBtn = document.getElementById('lrcsavebtn');
        elements.clearBtn = document.getElementById('lrcclrbtn');
        elements.lrcFileBtn = document.getElementById('lrcfilebtn');
        elements.lrcFileInput = document.getElementById('lrcFileInput');
        elements.lrcPasteBtn = document.getElementById('lrcpastebtn');
        elements.lrcFromLibraryBtn = document.getElementById('lrcfromlibrarybtn');
        elements.embedToggle = document.querySelector('#lrcEmbedToggle .status');
        elements.rawLyricsImportBtn = document.getElementById('rawLyricsImportBtn');
        elements.rawLyricsInput = document.getElementById('rawLyricsInput');
        elements.browserPath = document.getElementById('browserPath');
        elements.browserList = document.getElementById('browserList');
        elements.metadataInputs = Array.from(document.querySelectorAll('[data-meta-key]'));
        elements.metadataCount = document.querySelector('.metadata-count');
    }

    function initAudio() {
        state.audio.preload = 'metadata';
        state.audio.volume = 0.8;
        updateVolumeDisplay();

        state.audio.addEventListener('loadedmetadata', () => {
            state.durationMs = state.audio.duration * 1000;
            elements.duration.textContent = formatTimeMs(state.durationMs);
            elements.playbackbtn.disabled = false;
            elements.stopbtn.disabled = false;
            elements.seekbar.value = 0;
            elements.seekbar.max = Math.max(1, Math.floor(state.durationMs));
            if (!state.metadata.length) {
                state.metadata.length = formatTimeMs(state.durationMs);
                syncMetadataInputs();
            }
            updateEntrySticks();
        });

        state.audio.addEventListener('timeupdate', () => {
            elements.position.textContent = formatTimeMs(state.audio.currentTime * 1000);
            elements.seekbar.value = Math.floor(state.audio.currentTime * 1000);
            updateCurrentRow();
        });

        state.audio.addEventListener('ended', () => {
            updatePlaybackButton(false);
        });
    }

    function initModals() {
        const trackModalEl = document.getElementById('trackPickerModal');
        const rawLyricsModalEl = document.getElementById('rawLyricsModal');
        if (trackModalEl && globalThis.bootstrap) {
            trackModal = new bootstrap.Modal(trackModalEl);
        }
        if (rawLyricsModalEl && globalThis.bootstrap) {
            rawLyricsModal = new bootstrap.Modal(rawLyricsModalEl);
        }
    }

    function bindEvents() {
        elements.audiofilebtn?.addEventListener('click', () => {
            trackModal?.show();
            openBrowser(currentBrowsePath);
        });
        elements.playbackbtn?.addEventListener('click', togglePlayback);
        elements.stopbtn?.addEventListener('click', stopPlayback);
        elements.volumedown?.addEventListener('click', () => adjustVolume(-0.05));
        elements.volumeup?.addEventListener('click', () => adjustVolume(0.05));
        elements.seekbar?.addEventListener('input', () => {
            state.audio.currentTime = Number(elements.seekbar.value) / 1000;
        });

        elements.modebtn?.addEventListener('click', (event) => {
            if (elements.modebtn.disabled) {
                return;
            }
            if (state.mode === 'edit') {
                state.mode = 'sync';
                state.syncHandleIndex = (event.altKey && state.currentIndex >= 0) ? state.currentIndex : 0;
                state.activeIndex = state.syncHandleIndex;
            } else {
                state.mode = 'edit';
            }
            updateModeState();
        });

        elements.mergeToggle?.parentElement?.addEventListener('click', () => {
            state.mergeEnabled = !state.mergeEnabled;
            localStorage.setItem('lrc-editor-merge', state.mergeEnabled ? '1' : '0');
            if (globalThis.UserPrefs) globalThis.UserPrefs.set('lrcEditorMerge', state.mergeEnabled);
            updateMergeToggle();
        });

        elements.exportBtn?.addEventListener('click', () => exportLrc(true));
        elements.exportNoMetaBtn?.addEventListener('click', () => exportLrc(false));
        elements.saveBtn?.addEventListener('click', saveLrcToLibrary);
        elements.clearBtn?.addEventListener('click', clearEntries);
        elements.lrcFileBtn?.addEventListener('click', () => elements.lrcFileInput?.click());
        elements.lrcFileInput?.addEventListener('change', handleLrcFileImport);
        elements.rawLyricsImportBtn?.addEventListener('click', importRawLyrics);
        elements.lrcFromLibraryBtn?.addEventListener('click', loadLrcFromLibrary);
        elements.embedToggle?.parentElement?.addEventListener('click', () => setEmbedLyrics(!state.embedLyrics));

        elements.editor?.addEventListener('pointerdown', handleEditorClick);
        elements.editor?.addEventListener('click', handleEditorClick);
        elements.editor?.addEventListener('input', handleEditorInput);
        elements.editor?.addEventListener('focusin', handleEditorFocus, true);
        elements.editor?.addEventListener('mouseenter', () => {
            state.editorHover = true;
            stopEditorScroll();
        });
        elements.editor?.addEventListener('mouseleave', () => {
            state.editorHover = false;
        });
        elements.editor?.addEventListener('keydown', handleEditorKeydown);
        document.addEventListener('keydown', handleGlobalKeydown);

        elements.metadataInputs.forEach(input => {
            input.addEventListener('input', () => {
                const key = input.dataset.metaKey;
                if (!key) {
                    return;
                }
                state.metadata[key] = input.value.trim();
                updateMetadataCount();
            });
        });
    }

    function updatePlaybackButton(isPlaying) {
        if (!elements.playbackbtn) {
            return;
        }
        const icon = elements.playbackbtn.querySelector('.fa');
        if (isPlaying) {
            icon?.classList.remove('fa-play');
            icon?.classList.add('fa-pause');
        } else {
            icon?.classList.remove('fa-pause');
            icon?.classList.add('fa-play');
        }
    }

    function togglePlayback() {
        if (state.audio.paused) {
            state.audio.play();
            updatePlaybackButton(true);
        } else {
            state.audio.pause();
            updatePlaybackButton(false);
        }
    }

    function stopPlayback() {
        state.audio.pause();
        state.audio.currentTime = 0;
        updatePlaybackButton(false);
    }

    function adjustVolume(delta) {
        state.audio.volume = Math.max(0, Math.min(1, state.audio.volume + delta));
        updateVolumeDisplay();
    }

    function updateVolumeDisplay() {
        if (elements.volumedisp) {
            elements.volumedisp.textContent = `${Math.round(state.audio.volume * 100)}%`;
        }
    }

    async function openBrowser(path = '') {
        try {
            const response = await fetch(`/api/lrc/browse?path=${encodeURIComponent(path)}`);
            if (!response.ok) {
                throw new Error('Browse failed');
            }
            const payload = await response.json();
            currentBrowsePath = payload.path || '';
            renderBrowser(payload.entries || []);
        } catch (error) {
            if (elements.browserList) {
                elements.browserList.innerHTML = '<div class="text-muted">Browse failed.</div>';
            }
            console.debug('LRC browse failed', error);
        }
    }

    function renderBrowser(entries) {
        if (!elements.browserList || !elements.browserPath) {
            return;
        }
        elements.browserPath.textContent = currentBrowsePath || 'Library roots';
        elements.browserList.innerHTML = '';

        if (currentBrowsePath) {
            const up = document.createElement('div');
            up.className = 'browser-item';
            up.innerHTML = `
                <div class="browser-icon"><span class="fa fa-level-up-alt"></span></div>
                <div class="browser-details">..</div>
            `;
            up.addEventListener('click', () => openBrowser(parentPath(currentBrowsePath)));
            elements.browserList.appendChild(up);
        }

        if (!Array.isArray(entries) || entries.length === 0) {
            const empty = document.createElement('div');
            empty.className = 'text-muted';
            empty.textContent = 'No files found.';
            elements.browserList.appendChild(empty);
            return;
        }

        entries.forEach(entry => {
            const item = document.createElement('div');
            item.className = 'browser-item';
            let icon = 'fa-file-audio';
            if (entry.type === 'folder') {
                icon = 'fa-folder';
            } else if (entry.name.toLowerCase().endsWith('.lrc')) {
                icon = 'fa-file-lines';
            }
            item.innerHTML = `
                <div class="browser-icon"><span class="fa ${icon}"></span></div>
                <div class="browser-details">${escapeHtml(entry.name)}</div>
            `;
            item.addEventListener('click', () => handleBrowserSelect(entry));
            elements.browserList.appendChild(item);
        });
    }

    function parentPath(path) {
        let normalized = path;
        while (normalized.endsWith('/') || normalized.endsWith('\\')) {
            normalized = normalized.slice(0, -1);
        }
        const idx = Math.max(normalized.lastIndexOf('/'), normalized.lastIndexOf('\\'));
        return idx > 0 ? normalized.slice(0, idx) : '';
    }

    async function handleBrowserSelect(entry) {
        if (entry.type === 'folder') {
            await openBrowser(entry.path);
            return;
        }
        if (entry.name.toLowerCase().endsWith('.lrc')) {
            await loadLrcFromFile(entry.path);
            return;
        }
        await selectAudioFile(entry.path);
    }

    async function selectAudioFile(path) {
        try {
            const response = await fetch(`/api/lrc/file/info?path=${encodeURIComponent(path)}`);
            if (!response.ok) {
                throw new Error('Audio load failed');
            }
            const info = await response.json();
            state.trackId = null;
            state.trackInfo = { ...info, sourcePath: info.filePath };
            state.audio.src = info.audioUrl;
            state.audio.load();
            elements.statusFileName.textContent = info.fileName || 'Selected file';
            elements.statusFileType.textContent = info.fileType || '';
            elements.modebtn.disabled = false;
            elements.saveBtn.disabled = false;
            updateModeState();
            setDefaultMetadataFromPath(info);
            trackModal?.hide();
            await loadLrcFromFile(info.filePath, true);
        } catch (error) {
            console.debug('Audio load failed', error);
            uiAlert('Failed to load audio file.', { title: 'Audio Load Failed' });
        }
    }

    async function selectTrackById(trackId, silent = false) {
        try {
            const response = await fetch(`/api/lrc/track/${encodeURIComponent(trackId)}`);
            if (!response.ok) {
                throw new Error('Track load failed');
            }

            const info = await response.json();
            state.trackId = Number(info.trackId || trackId);
            state.trackInfo = { ...info, sourcePath: info.filePath || '' };
            state.audio.src = info.audioUrl;
            state.audio.load();
            elements.statusFileName.textContent = info.title || 'Selected track';
            elements.statusFileType.textContent = info.fileType || '';
            elements.modebtn.disabled = false;
            elements.saveBtn.disabled = false;
            updateModeState();
            setDefaultMetadata(info);
            trackModal?.hide();
            await loadLrcFromLibrary(true);
            return true;
        } catch (error) {
            if (!silent) {
                uiAlert('Failed to load selected track.', { title: 'Track Load Failed' });
            }
            console.debug('Track load failed', error);
            return false;
        }
    }

    async function bootstrapTrackFromQuery() {
        try {
            const params = new URLSearchParams(globalThis.location.search || '');
            const rawTrackId = (params.get('trackId') || '').trim();
            if (!rawTrackId) {
                return;
            }

            const trackId = Number.parseInt(rawTrackId, 10);
            if (!Number.isFinite(trackId) || trackId <= 0) {
                return;
            }

            const loaded = await selectTrackById(trackId, true);
            if (!loaded) {
                uiToast('Unable to preload selected track in LRC Editor.', 'warning');
            }
        } catch (error) {
            // Ignore launch query failures.
            console.debug('Track bootstrap from query failed', error);
        }
    }

    function setDefaultMetadata(info) {
        state.metadata.ar = info.artist || '';
        state.metadata.al = info.album || '';
        state.metadata.ti = info.title || '';
        state.metadata.length = info.durationMs ? formatTimeMs(info.durationMs) : '';
        syncMetadataInputs();
    }

    function setDefaultMetadataFromPath(info) {
        const directory = info.directory || '';
        const parts = directory.split(/[\\/]/).filter(Boolean);
        state.metadata.ti = info.fileName || '';
        state.metadata.al = parts.length >= 1 ? parts[parts.length - 1] : '';
        state.metadata.ar = parts.length >= 2 ? parts[parts.length - 2] : '';
        state.metadata.length = '';
        syncMetadataInputs();
    }

    function syncMetadataInputs() {
        elements.metadataInputs.forEach(input => {
            const key = input.dataset.metaKey;
            if (!key) {
                return;
            }
            input.value = state.metadata[key] || '';
        });
        updateMetadataCount();
    }

    function updateMetadataCount() {
        const count = Object.values(state.metadata).filter(value => value?.trim()).length;
        if (elements.metadataCount) {
            elements.metadataCount.textContent = count ? count.toString() : '';
        }
    }

    function updateModeState() {
        if (!elements.modebtn) {
            return;
        }
        const isSync = state.mode === 'sync';
        const buttonText = isSync ? elements.modebtn.dataset.editmode : elements.modebtn.dataset.syncmode;
        elements.modename.textContent = buttonText;
        elements.modebtn.title = buttonText;
        if (isSync) {
            state.activeIndex = state.syncHandleIndex;
        }
        renderEntries();
    }

    function updateMergeToggle() {
        if (!elements.mergeToggle) {
            return;
        }
        elements.mergeToggle.textContent = state.mergeEnabled ? elements.mergeToggle.dataset.true : elements.mergeToggle.dataset.false;
    }

    function updateEmbedToggle() {
        if (!elements.embedToggle) {
            return;
        }
        elements.embedToggle.textContent = state.embedLyrics ? elements.embedToggle.dataset.true : elements.embedToggle.dataset.false;
    }

    function handleLrcFileImport(event) {
        const file = event.target.files?.[0];
        if (!file) {
            return;
        }
        file.text()
            .then(content => {
                applyParsedLrc(content);
                if (elements.lrcFileInput) {
                    elements.lrcFileInput.value = '';
                }
            })
            .catch(error => {
                console.debug('LRC file import failed', error);
                uiAlert('Failed to import selected LRC file.', { title: 'Import Failed' });
            });
    }

    function importRawLyrics() {
        const raw = elements.rawLyricsInput.value.trim();
        if (!raw) {
            rawLyricsModal?.hide();
            return;
        }
        const lines = raw.split(/\r?\n/);
        state.entries = lines.map(line => ({ timeMs: null, text: line.trim() }));
        state.activeIndex = 0;
        state.syncHandleIndex = 0;
        renderEntries();
        rawLyricsModal?.hide();
    }

    async function loadLrcFromLibrary(silent = false) {
        if (!state.trackInfo?.lrcUrl) {
            if (!silent) {
                uiAlert('Select a track first.', { title: 'No Track Selected' });
            }
            return;
        }
        try {
            const response = await fetch(state.trackInfo.lrcUrl);
            if (!response.ok) {
                if (!silent) {
                    uiAlert('No existing .LRC file found for this track.', { title: 'LRC Not Found' });
                }
                return;
            }
            const payload = await response.json();
            applyParsedLrc(payload.content || '');
        } catch (error) {
            if (!silent) {
                uiAlert('Failed to load LRC file.', { title: 'LRC Load Failed' });
            }
            console.debug('LRC library load failed', error);
        }
    }

    async function loadLrcFromFile(path, silent = false) {
        try {
            const response = await fetch(`/api/lrc/file/lrc?path=${encodeURIComponent(path)}`);
            if (!response.ok) {
                if (!silent) {
                    uiAlert('LRC file not found.', { title: 'LRC Not Found' });
                }
                return;
            }
            const payload = await response.json();
            applyParsedLrc(payload.content || '');
        } catch (error) {
            if (!silent) {
                uiAlert('Failed to load LRC file.', { title: 'LRC Load Failed' });
            }
            console.debug('LRC file load failed', error);
        }
    }

    function applyParsedLrc(content) {
        const parsed = parseLrc(content);
        state.entries = parsed.entries.length ? parsed.entries : [{ timeMs: null, text: '' }];
        state.metadata = { ...state.metadata, ...parsed.metadata };
        state.activeIndex = 0;
        state.syncHandleIndex = 0;
        syncMetadataInputs();
        renderEntries();
    }

    async function clearEntries() {
        const confirmed = await uiConfirm('Discard all lyrics?', { title: 'Discard Lyrics' });
        if (!confirmed) {
            return;
        }
        state.entries = [{ timeMs: null, text: '' }];
        state.activeIndex = 0;
        state.syncHandleIndex = 0;
        renderEntries();
    }

    function renderEntries() {
        if (!elements.editor) {
            return;
        }
        if (state.entries.length === 0) {
            state.entries.push({ timeMs: null, text: '' });
        }
        elements.editor.innerHTML = '';
        state.entries.forEach((entry, index) => {
            const row = document.createElement('div');
            row.className = 'editor-row';
            if (state.mode === 'sync') {
                row.classList.add('sync-mode');
            }
            if (state.mode === 'edit' && index === state.activeIndex) {
                row.classList.add('is-active');
            }
            if (state.mode === 'sync' && index === state.syncHandleIndex) {
                row.classList.add('is-handle');
            }
            const editable = state.mode === 'sync' ? 'false' : 'true';
            row.dataset.index = index;
            row.innerHTML = `
                <div class="row-tools row-tools-left">
                    <button class="btn btn-warning btn-sm" data-action="goto" title="Jump to timestamp" ${entry.timeMs == null ? 'disabled' : ''}>
                        <span class="fa fa-play"></span>
                    </button>
                    <div class="btn-group-vertical">
                        <button class="btn btn-primary btn-sm" data-action="step-forward" title="Step forward 100ms">
                            <span class="fa fa-caret-up"></span>
                        </button>
                        <button class="btn btn-primary btn-sm" data-action="step-backward" title="Step backward 100ms">
                            <span class="fa fa-caret-down"></span>
                        </button>
                    </div>
                </div>
                <span class="timestamp" contenteditable="${editable}" data-empty="0:00.000">${entry.timeMs == null ? '' : formatTimeMs(entry.timeMs)}</span>
                <span class="lyric-text" contenteditable="${editable}" data-empty="<break>">${escapeHtml(entry.text || '')}</span>
                <div class="row-tools row-tools-right">
                    <button class="btn btn-success btn-sm" data-action="add-up" title="Add row above">
                        <span class="fa fa-arrow-up"></span>
                    </button>
                    <button class="btn btn-success btn-sm" data-action="add-down" title="Add row below">
                        <span class="fa fa-arrow-down"></span>
                    </button>
                    <button class="btn btn-danger btn-sm" data-action="remove" title="Remove row">
                        <span class="fa fa-trash"></span>
                    </button>
                    <button class="btn btn-warning btn-sm" data-action="goto" title="Jump to timestamp" ${entry.timeMs == null ? 'disabled' : ''}>
                        <span class="fa fa-play"></span>
                    </button>
                </div>
            `;
            elements.editor.appendChild(row);
        });
        updateMergeToggle();
        updateCurrentRow();
        updateEntrySticks();
        if (state.mode === 'sync') {
            scrollHandleIntoView();
        }
    }

    function updateActiveRowHighlight() {
        if (!elements.editor || state.mode !== 'edit') {
            return;
        }
        elements.editor.querySelectorAll('.editor-row').forEach((row, index) => {
            row.classList.toggle('is-active', index === state.activeIndex);
        });
    }

    function handleEditorClick(event) {
        const row = event.target.closest('.editor-row');
        if (!row) {
            return;
        }
        const action = event.target.closest('button')?.dataset.action;
        if (action) {
            event.preventDefault();
        }
        const index = Number(row.dataset.index);
        if (!Number.isNaN(index)) {
            updateEditorSelection(index, event.target);
        }
        if (!action) {
            return;
        }
        handleEditorRowAction(action, index);
    }

    function updateEditorSelection(index, targetElement) {
        if (state.mode === 'sync') {
            state.syncHandleIndex = index;
            state.activeIndex = index;
            renderEntries();
            return;
        }

        state.activeIndex = index;
        if (targetElement.matches('.timestamp, .lyric-text')) {
            updateActiveRowHighlight();
            return;
        }

        renderEntries();
    }

    function handleEditorRowAction(action, index) {
        if (action === 'add-up') {
            insertRow(index);
            return;
        }
        if (action === 'add-down') {
            insertRow(index + 1);
            return;
        }
        if (action === 'remove') {
            removeRow(index);
            return;
        }
        if (action === 'goto') {
            const entry = state.entries[index];
            if (entry?.timeMs != null) {
                state.audio.currentTime = entry.timeMs / 1000;
            }
            return;
        }
        if (action === 'step-forward') {
            stepTimestamp(index, 100);
            return;
        }
        if (action === 'step-backward') {
            stepTimestamp(index, -100);
        }
    }

    function handleEditorInput(event) {
        const row = event.target.closest('.editor-row');
        if (!row) {
            return;
        }
        const index = Number(row.dataset.index);
        if (Number.isNaN(index)) {
            return;
        }
        const entry = state.entries[index];
        if (!entry) {
            return;
        }
        if (event.target.classList.contains('timestamp')) {
            const parsed = parseTimestamp(event.target.textContent);
            if (parsed == null) {
                event.target.classList.add('invalid');
                entry.timeMs = null;
            } else {
                event.target.classList.remove('invalid');
                entry.timeMs = parsed;
            }
            updateEntrySticks();
        } else if (event.target.classList.contains('lyric-text')) {
            entry.text = event.target.textContent || '';
        }
    }

    function handleEditorFocus(event) {
        const row = event.target.closest('.editor-row');
        if (!row) {
            return;
        }
        const index = Number(row.dataset.index);
        if (!Number.isNaN(index)) {
            state.activeIndex = index;
            updateActiveRowHighlight();
        }
    }

    function handleEditorKeydown(event) {
        if (event.key === 'Enter') {
            event.preventDefault();
        }
    }

    function insertRow(index) {
        const safeIndex = Math.max(0, Math.min(state.entries.length, index));
        state.entries.splice(safeIndex, 0, { timeMs: null, text: '' });
        state.activeIndex = safeIndex;
        if (state.mode === 'sync') {
            state.syncHandleIndex = safeIndex;
        }
        renderEntries();
    }

    function removeRow(index) {
        if (state.entries.length <= 1) {
            state.entries[0] = { timeMs: null, text: '' };
            state.activeIndex = 0;
        } else {
            state.entries.splice(index, 1);
            state.activeIndex = Math.max(0, Math.min(state.activeIndex, state.entries.length - 1));
            if (state.mode === 'sync') {
                state.syncHandleIndex = Math.max(0, Math.min(state.syncHandleIndex, state.entries.length - 1));
            }
        }
        renderEntries();
    }

    function stepTimestamp(index, delta) {
        const entry = state.entries[index];
        if (!entry) {
            return;
        }
        entry.timeMs = Math.max(0, (entry.timeMs || 0) + delta);
        renderEntries();
    }

    function updateCurrentRow() {
        if (!elements.editor) {
            return;
        }
        const currentMs = state.audio.currentTime * 1000;
        let activeCurrent = -1;
        state.entries.forEach((entry, index) => {
            if (entry.timeMs != null && entry.timeMs <= currentMs) {
                activeCurrent = index;
            }
        });
        const previousIndex = state.currentIndex;
        state.currentIndex = activeCurrent;
        const showCurrent = state.mode === 'edit';
        elements.editor.querySelectorAll('.editor-row').forEach((row, index) => {
            row.classList.toggle('is-current', showCurrent && index === activeCurrent);
        });
        if (showCurrent && activeCurrent >= 0 && activeCurrent !== previousIndex) {
            scrollCurrentIntoView();
        }
    }

    function updateEntrySticks() {
        if (!elements.entrySticks) {
            return;
        }
        elements.entrySticks.innerHTML = '';
        if (!state.durationMs) {
            return;
        }
        state.entries.forEach(entry => {
            if (entry.timeMs == null) {
                return;
            }
            const stick = document.createElement('div');
            stick.className = 'stick';
            const percent = Math.min(100, Math.max(0, (entry.timeMs / state.durationMs) * 100));
            stick.style.left = `calc(${percent}% - 1px)`;
            elements.entrySticks.appendChild(stick);
        });
    }

    function scrollHandleIntoView() {
        if (!elements.editor || state.mode !== 'sync') {
            return;
        }
        const row = elements.editor.querySelector(`.editor-row[data-index="${state.syncHandleIndex}"]`);
        if (!row) {
            return;
        }
        scrollHighlightedIntoView(row);
    }

    function scrollCurrentIntoView() {
        if (!elements.editor || state.mode !== 'edit') {
            return;
        }
        const row = elements.editor.querySelector(`.editor-row[data-index="${state.currentIndex}"]`);
        if (!row) {
            return;
        }
        scrollHighlightedIntoView(row);
    }

    const editorScrollState = {
        animationId: null,
        startTime: 0,
        startTop: 0,
        targetTop: 0,
        duration: 400
    };

    function stopEditorScroll() {
        if (editorScrollState.animationId != null) {
            cancelAnimationFrame(editorScrollState.animationId);
            editorScrollState.animationId = null;
        }
    }

    function scrollHighlightedIntoView(row) {
        if (!elements.editor || elements.editor.matches(':hover')) {
            return;
        }
        const editorRect = elements.editor.getBoundingClientRect();
        const rowRect = row.getBoundingClientRect();
        const targetTop = elements.editor.scrollTop
            + (rowRect.top - editorRect.top)
            - elements.editor.clientHeight / 2
            + row.clientHeight / 2;
        animateEditorScroll(targetTop);
    }

    function animateEditorScroll(targetTop) {
        if (!elements.editor) {
            return;
        }
        stopEditorScroll();
        editorScrollState.startTime = performance.now();
        editorScrollState.startTop = elements.editor.scrollTop;
        editorScrollState.targetTop = Math.max(0, targetTop);

        const step = (now) => {
            const elapsed = now - editorScrollState.startTime;
            const t = Math.min(1, elapsed / editorScrollState.duration);
            const eased = 0.5 - Math.cos(Math.PI * t) / 2;
            elements.editor.scrollTop = editorScrollState.startTop
                + (editorScrollState.targetTop - editorScrollState.startTop) * eased;
            if (t < 1) {
                editorScrollState.animationId = requestAnimationFrame(step);
            } else {
                editorScrollState.animationId = null;
            }
        };

        editorScrollState.animationId = requestAnimationFrame(step);
    }

    function handleGlobalKeydown(event) {
        const isInput = event.target.matches('input, textarea');
        const isSyncMode = state.mode === 'sync';
        const isContentEditable = event.target.matches('[contenteditable="true"]');

        if (handleTimestampShortcut(event, isSyncMode, isContentEditable)) {
            return;
        }

        if (handleTransportShortcuts(event, isInput, isContentEditable)) {
            return;
        }

        if (!isSyncMode) {
            return;
        }

        handleSyncShortcuts(event);
    }

    function handleTimestampShortcut(event, isSyncMode, isContentEditable) {
        if (isSyncMode
            || !isContentEditable
            || !event.target.classList.contains('timestamp')
            || !event.ctrlKey
            || (event.key !== 'ArrowUp' && event.key !== 'ArrowDown')) {
            return false;
        }

        event.preventDefault();
        const row = event.target.closest('.editor-row');
        if (!row) {
            return true;
        }
        const index = Number(row.dataset.index);
        if (Number.isNaN(index)) {
            return true;
        }
        const entry = state.entries[index];
        const delta = event.key === 'ArrowUp' ? 100 : -100;
        entry.timeMs = Math.max(0, (entry.timeMs || 0) + delta);
        event.target.textContent = formatTimeMs(entry.timeMs);
        event.target.classList.remove('invalid');
        return true;
    }

    function handleTransportShortcuts(event, isInput, isContentEditable) {
        if (event.code === 'Space' && !isInput && !isContentEditable) {
            event.preventDefault();
            togglePlayback();
            return true;
        }

        if (event.key === '.') {
            stopPlayback();
            return true;
        }

        if (event.key === 'PageUp' || event.key === 'PageDown') {
            event.preventDefault();
            let step = 0.05;
            if (event.shiftKey) {
                step = 0.1;
            } else if (event.altKey) {
                step = 0.01;
            }
            adjustVolume(event.key === 'PageUp' ? step : -step);
            return true;
        }

        if (event.key === 'ArrowLeft') {
            const step = event.shiftKey ? 5 : 2.5;
            state.audio.currentTime = Math.max(0, state.audio.currentTime - step);
            return true;
        }

        if (event.key === 'ArrowRight') {
            const step = event.shiftKey ? 5 : 2.5;
            state.audio.currentTime = Math.min(state.audio.duration || Infinity, state.audio.currentTime + step);
            return true;
        }

        return false;
    }

    function handleSyncShortcuts(event) {
        if (event.key === 'Enter' && !event.ctrlKey) {
            event.preventDefault();
            applySyncTimestamp();
            return;
        }
        if (event.key === 'Enter' && event.ctrlKey) {
            event.preventDefault();
            insertBreakEntry();
            return;
        }

        if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
            event.preventDefault();
            const delta = event.key === 'ArrowUp' ? -1 : 1;
            state.syncHandleIndex = Math.max(0, Math.min(state.entries.length - 1, state.syncHandleIndex + delta));
            state.activeIndex = state.syncHandleIndex;
            renderEntries();
            scrollHandleIntoView();
            return;
        }

        if (event.key !== 'Delete') {
            return;
        }

        event.preventDefault();
        const entry = state.entries[state.syncHandleIndex];
        if (entry) {
            entry.timeMs = null;
        }
        state.syncHandleIndex = Math.min(state.entries.length - 1, state.syncHandleIndex + 1);
        state.activeIndex = state.syncHandleIndex;
        renderEntries();
        scrollHandleIntoView();
    }

    function applySyncTimestamp() {
        const entry = state.entries[state.syncHandleIndex];
        if (!entry) {
            return;
        }
        entry.timeMs = Math.floor(state.audio.currentTime * 1000);
        if (state.syncHandleIndex < state.entries.length - 1) {
            state.syncHandleIndex += 1;
            state.activeIndex = state.syncHandleIndex;
        } else {
            state.mode = 'edit';
        }
        renderEntries();
        scrollHandleIntoView();
    }

    function insertBreakEntry() {
        const index = state.syncHandleIndex;
        state.entries.splice(index, 0, { timeMs: Math.floor(state.audio.currentTime * 1000), text: '' });
        state.syncHandleIndex = Math.min(state.entries.length - 1, index + 1);
        state.activeIndex = state.syncHandleIndex;
        renderEntries();
        scrollHandleIntoView();
    }

    function exportLrc(includeMetadata) {
        const content = buildLrc(includeMetadata);
        if (!content.trim()) {
            uiAlert('Nothing to export.', { title: 'Export Empty' });
            return;
        }
        const blob = new Blob([content], { type: 'text/plain;charset=utf-8' });
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        const baseName = state.trackInfo ? `${state.trackInfo.artist} - ${state.trackInfo.title}` : 'lyrics';
        link.href = url;
        link.download = `${baseName}.lrc`;
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
    }

    async function saveLrcToLibrary() {
        const content = buildLrc(true);
        if (!content.trim()) {
            uiAlert('Nothing to save.', { title: 'Save Empty' });
            return;
        }
        try {
            if (state.trackId && state.trackInfo?.lrcUrl) {
                const response = await fetch(`/api/lrc/track/${state.trackId}/lrc`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ content })
                });
                if (!response.ok) {
                    throw new Error('Save failed');
                }
                uiAlert('Saved to library.', { title: 'Save Complete' });
                return;
            }
            if (!state.trackInfo?.sourcePath) {
                uiAlert('Select an audio file first.', { title: 'No Audio Selected' });
                return;
            }
            const response = await fetch('/api/lrc/file/lrc', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ path: state.trackInfo.sourcePath, content })
            });
            if (!response.ok) {
                throw new Error('Save failed');
            }
            uiAlert('Saved to library.', { title: 'Save Complete' });
        } catch (error) {
            console.debug('LRC save failed', error);
            uiAlert('Failed to save LRC file.', { title: 'Save Failed' });
        }
    }

    function parseLrc(text) {
        const metadata = { ...state.metadata };
        const entries = [];
        const lines = text.split(/\r?\n/);
        const timeRegex = /\[(\d{1,2}):(\d{2})(?:[.:](\d{1,3}))?\]/g;
        lines.forEach(line => {
            if (!line.trim()) {
                return;
            }
            const timeMatches = [...line.matchAll(timeRegex)];
            if (timeMatches.length === 0) {
                const metaMatch = line.match(/^\[([a-z0-9]+):(.*)\]$/i);
                if (metaMatch) {
                    const key = metaMatch[1].toLowerCase();
                    if (Object.hasOwn(metadata, key)) {
                        metadata[key] = metaMatch[2].trim();
                    }
                }
                return;
            }
            const textPart = line.replaceAll(timeRegex, '').trim();
            timeMatches.forEach(match => {
                const timeMs = parseTimestamp(`${match[1]}:${match[2]}${match[3] ? '.' + match[3] : ''}`);
                if (timeMs != null) {
                    entries.push({ timeMs, text: textPart });
                }
            });
        });
        return { entries, metadata };
    }

    function buildLrc(includeMetadata) {
        const lines = [];
        if (includeMetadata) {
            ['ar', 'al', 'ti', 'by', 'length', 'offset', 're', 've'].forEach(key => {
                const value = state.metadata[key];
                if (value?.trim()) {
                    lines.push(`[${key}:${value.trim()}]`);
                }
            });
        }
        const entries = state.entries.filter(entry => entry.timeMs != null);
        if (state.mergeEnabled) {
            const grouped = new Map();
            entries.forEach(entry => {
                const text = entry.text || '';
                if (!grouped.has(text)) {
                    grouped.set(text, []);
                }
                grouped.get(text).push(entry.timeMs);
            });
            grouped.forEach((times, text) => {
                const stamps = times
                    .sort((a, b) => a - b)
                    .map(time => `[${formatTimeMs(time)}]`)
                    .join('');
                lines.push(`${stamps}${text}`);
            });
        } else {
            entries.forEach(entry => {
                lines.push(`[${formatTimeMs(entry.timeMs)}]${entry.text || ''}`);
            });
        }
        return lines.join('\n');
    }

    function parseTimestamp(value) {
        if (!value) {
            return null;
        }
        const match = value.trim().match(/^(\d{1,2}):([0-5]\d)(?:[.:](\d{1,3}))?$/);
        if (!match) {
            return null;
        }
        const minutes = Number(match[1]);
        const seconds = Number(match[2]);
        const fraction = match[3] || '';
        let ms = 0;
        if (fraction.length === 1) {
            ms = Number(fraction) * 100;
        } else if (fraction.length === 2) {
            ms = Number(fraction) * 10;
        } else if (fraction.length === 3) {
            ms = Number(fraction);
        }
        return (minutes * 60 + seconds) * 1000 + ms;
    }

    function formatTimeMs(ms) {
        if (!Number.isFinite(ms)) {
            return '0:00.00';
        }
        const totalSeconds = Math.max(0, Math.floor(ms / 1000));
        const minutes = Math.floor(totalSeconds / 60);
        const seconds = totalSeconds % 60;
        const hundredths = Math.floor((ms % 1000) / 10);
        return `${minutes}:${seconds.toString().padStart(2, '0')}.${hundredths.toString().padStart(2, '0')}`;
    }

    function escapeHtml(value) {
        return (value || '')
            .replaceAll('&', '&amp;')
            .replaceAll('<', '&lt;')
            .replaceAll('>', '&gt;')
            .replaceAll('"', '&quot;')
            .replaceAll("'", '&#39;');
    }

    async function hydrateDefaultCreator() {
        if (state.metadata.re?.trim()) {
            return;
        }
        const fallbackName = document.getElementById('profileName')?.textContent?.trim() || '';
        try {
            const response = await fetch('/api/account/profile');
            if (!response.ok) {
                throw new Error('Profile unavailable');
            }
            const data = await response.json();
            const username = (data?.username || '').trim();
            state.metadata.re = username || fallbackName || 'DeezSpoTag';
        } catch {
            state.metadata.re = fallbackName || 'DeezSpoTag';
        }
        syncMetadataInputs();
    }

    async function hydrateEmbedLyrics() {
        try {
            const response = await fetch('/api/getSettings');
            if (!response.ok) {
                throw new Error('Settings fetch failed');
            }
            const payload = await response.json();
            state.settingsCache = payload.settings || null;
            state.embedLyrics = Boolean(state.settingsCache?.embedLyrics ?? state.settingsCache?.saveLyrics);
            updateEmbedToggle();
        } catch (error) {
            state.embedLyrics = false;
            updateEmbedToggle();
            console.debug('Embed lyrics hydration failed', error);
        }
    }

    async function setEmbedLyrics(nextValue) {
        if (!state.settingsCache) {
            await hydrateEmbedLyrics();
            if (!state.settingsCache) {
                uiAlert('Settings are unavailable right now.', { title: 'Settings Error' });
                return;
            }
        }
        if (state.embedLyrics === nextValue) {
            updateEmbedToggle();
            return;
        }
        state.embedLyrics = nextValue;
        updateEmbedToggle();
        try {
            const settings = {
                saveLyrics: Boolean(state.settingsCache?.saveLyrics),
                embedLyrics: state.embedLyrics
            };
            const response = await fetch('/api/saveSettings', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(settings)
            });
            const result = await response.json().catch(() => null);
            if (!response.ok || result?.result === false) {
                throw new Error(result?.error || 'Failed to save settings.');
            }
            state.settingsCache = { ...state.settingsCache, embedLyrics: state.embedLyrics };
        } catch (error) {
            state.embedLyrics = !state.embedLyrics;
            updateEmbedToggle();
            uiAlert(error?.message || 'Failed to save settings.', { title: 'Settings Error' });
        }
    }

    function init() {
        initElements();
        initModals();
        initAudio();
        bindEvents();
        state.mergeEnabled = localStorage.getItem('lrc-editor-merge') === '1';
        updateMergeToggle();
        updateModeState();
        renderEntries();
        openBrowser('');
        hydrateDefaultCreator();
        hydrateEmbedLyrics();
        bootstrapTrackFromQuery();
    }

    document.addEventListener('DOMContentLoaded', init);
})();
