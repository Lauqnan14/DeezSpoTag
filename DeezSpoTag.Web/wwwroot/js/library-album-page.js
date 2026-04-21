(() => {
    const common = globalThis.DeezSpoTagLibraryPageCommon;
    if (!common) {
        return;
    }

    const {
        appendCacheKey,
        buildLibraryScopedUrl,
        escapeHtml,
        fetchJson,
        fetchJsonOptional,
        playLocalLibraryTrackInApp,
        showToast,
        toSafeHttpUrl
    } = common;

    const albumPageState = {
        albumTracksById: new Map(),
        folders: [],
        trackSummaryCache: new Map(),
        spectrogramModalRefs: null,
        inlineAnalysisRefs: null
    };

    function getTrackSourceForTrack(track) {
        if (!track) {
            return null;
        }
        if (track.deezerUrl) {
            return { service: 'deezer', url: track.deezerUrl };
        }
        if (track.deezerTrackId) {
            return { service: 'deezer', url: `https://www.deezer.com/track/${track.deezerTrackId}` };
        }
        if (track.spotifyUrl) {
            return { service: 'spotify', url: track.spotifyUrl };
        }
        if (track.spotifyTrackId) {
            return { service: 'spotify', url: `https://open.spotify.com/track/${track.spotifyTrackId}` };
        }
        if (track.appleUrl) {
            return { service: 'apple', url: track.appleUrl };
        }
        return null;
    }

    function buildAlbumTrackBaseIntent(track) {
        return {
            title: track?.title || '',
            artist: track?.artistName || track?.artist || '',
            album: track?.albumTitle || track?.album || '',
            albumArtist: track?.artistName || track?.artist || '',
            isrc: track?.isrc || '',
            cover: track?.coverUrl || track?.image || '',
            durationMs: Number(track?.durationMs || track?.duration || 0) || 0,
            position: Number(track?.trackNo || 0) || 0,
            allowQualityUpgrade: true
        };
    }

    function buildAppleQueueIntents(track, source, baseIntent) {
        const hasStereoVariant = track?.hasStereoVariant === true || track?.hasStereoVariant === 'true';
        const hasAtmosVariant = track?.hasAtmosVariant === true || track?.hasAtmosVariant === 'true';
        const hasAtmosCapability = track?.hasAtmos === true
            || track?.hasAtmos === 'true'
            || track?.appleHasAtmos === true
            || track?.appleHasAtmos === 'true'
            || hasAtmosVariant
            || String(track?.audioVariant || '').toLowerCase() === 'atmos';
        const intents = [];

        if (hasAtmosCapability && !hasAtmosVariant) {
            intents.push({
                sourceService: 'apple',
                sourceUrl: source.url,
                contentType: 'atmos',
                hasAtmos: true,
                ...baseIntent
            });
        }
        if (!hasStereoVariant) {
            intents.push({
                sourceService: 'apple',
                sourceUrl: source.url,
                contentType: 'stereo',
                hasAtmos: hasAtmosCapability,
                ...baseIntent
            });
        }

        return intents;
    }

    function buildQueueIntentsForTrack(track, source) {
        if (!source?.url) {
            return { linked: false, intents: [] };
        }

        const baseIntent = buildAlbumTrackBaseIntent(track);
        if (source.service === 'apple') {
            return { linked: true, intents: buildAppleQueueIntents(track, source, baseIntent) };
        }
        if (source.service === 'spotify') {
            const spotifyId = track?.spotifyTrackId
                || (source.url.match(/(?:spotify:track:|open\.spotify\.com\/track\/)([a-z0-9]+)/i) || [])[1]
                || '';
            return {
                linked: true,
                intents: [{
                    sourceService: 'spotify',
                    sourceUrl: source.url,
                    spotifyId,
                    deezerId: track?.deezerTrackId || track?.deezerId || undefined,
                    ...baseIntent
                }]
            };
        }
        return {
            linked: true,
            intents: [{
                sourceService: source.service,
                sourceUrl: source.url,
                deezerId: track?.deezerTrackId || track?.deezerId || undefined,
                ...baseIntent
            }]
        };
    }

    function collectAlbumQueueIntents(tracks) {
        const intents = [];
        let linkedTrackCount = 0;
        tracks.forEach((track) => {
            const source = getTrackSourceForTrack(track);
            const built = buildQueueIntentsForTrack(track, source);
            if (!built.linked) {
                return;
            }
            linkedTrackCount += 1;
            intents.push(...built.intents);
        });
        return { intents, linkedTrackCount };
    }

    async function requestAlbumQueueIntents(intents, destinationFolderId) {
        if (!Array.isArray(intents) || intents.length === 0) {
            return { queuedCount: 0, deferredCount: 0, skippedCount: 0 };
        }

        const result = await fetchJson('/api/download/intent', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                resolveImmediately: true,
                destinationFolderId: Number.isFinite(destinationFolderId) ? destinationFolderId : null,
                intents
            })
        });

        return {
            queuedCount: Array.isArray(result?.queued) ? result.queued.length : 0,
            deferredCount: Number.isFinite(result?.deferredCount) ? result.deferredCount : 0,
            skippedCount: Number.isFinite(result?.skipped) ? result.skipped : 0
        };
    }

    function buildAlbumQueueSummary({ queuedCount, deferredCount, skippedCount }, unsupported) {
        return [
            queuedCount > 0 ? `Queued ${queuedCount}` : null,
            deferredCount > 0 ? `deferred ${deferredCount}` : null,
            skippedCount > 0 ? `skipped ${skippedCount}` : null,
            unsupported > 0 ? `unlinked ${unsupported}` : null
        ].filter(Boolean).join(' | ');
    }

    async function queueAlbumDownloads(tracks) {
        if (!Array.isArray(tracks) || tracks.length === 0) {
            showToast('No downloadable tracks selected.', true);
            return;
        }

        const destinationRaw = document.getElementById('downloadDestinationAlbum')?.value ?? '';
        const destinationFolderId = destinationRaw ? Number(destinationRaw) : null;
        const { intents, linkedTrackCount } = collectAlbumQueueIntents(tracks);
        const counts = await requestAlbumQueueIntents(intents, destinationFolderId);
        const unsupported = Math.max(0, tracks.length - linkedTrackCount);
        const summary = buildAlbumQueueSummary(counts, unsupported);

        if (!summary) {
            showToast('No tracks were queued.', true);
            return;
        }
        showToast(summary);
    }

    function formatDuration(durationMs) {
        if (!durationMs) {
            return '--:--';
        }
        const totalSeconds = Math.floor(durationMs / 1000);
        const minutes = Math.floor(totalSeconds / 60);
        const seconds = totalSeconds % 60;
        return `${minutes}:${seconds.toString().padStart(2, '0')}`;
    }

    function formatAnalysisClock(secondsValue) {
        const totalSeconds = Math.max(0, Math.round(Number(secondsValue || 0)));
        const minutes = Math.floor(totalSeconds / 60);
        const seconds = totalSeconds % 60;
        return `${minutes}:${seconds.toString().padStart(2, '0')}`;
    }

    function formatAnalysisFileSize(bytesValue) {
        const bytes = Number(bytesValue || 0);
        if (!Number.isFinite(bytes) || bytes <= 0) {
            return '—';
        }
        const units = ['B', 'KB', 'MB', 'GB', 'TB'];
        let value = bytes;
        let index = 0;
        while (value >= 1024 && index < units.length - 1) {
            value /= 1024;
            index++;
        }
        const precision = value >= 10 ? 1 : 2;
        return `${value.toFixed(precision)} ${units[index]}`;
    }

    function formatAnalysisNumber(value, decimals = 2, suffix = '') {
        const number = Number(value);
        if (!Number.isFinite(number)) {
            return '—';
        }
        return `${number.toFixed(decimals)}${suffix}`;
    }

    function formatChannelLabel(channelsValue) {
        if (channelsValue <= 0) {
            return '—';
        }
        if (channelsValue === 2) {
            return 'Stereo';
        }
        if (channelsValue === 1) {
            return 'Mono';
        }
        return `${channelsValue}`;
    }

    function ensureInlineAnalysisPanel() {
        if (albumPageState.inlineAnalysisRefs) {
            return albumPageState.inlineAnalysisRefs;
        }

        const panel = document.getElementById('trackAnalysisInline');
        if (!panel) {
            return null;
        }

        albumPageState.inlineAnalysisRefs = {
            subtitle: panel.querySelector('.track-analysis-inline__subtitle'),
            sampleRate: document.getElementById('inlineSampleRate'),
            bitDepth: document.getElementById('inlineBitDepth'),
            channels: document.getElementById('inlineChannels'),
            duration: document.getElementById('inlineDuration'),
            nyquist: document.getElementById('inlineNyquist'),
            size: document.getElementById('inlineSize'),
            dynamicRange: document.getElementById('inlineDynamicRange'),
            peak: document.getElementById('inlinePeak'),
            rms: document.getElementById('inlineRms'),
            samples: document.getElementById('inlineSamples')
        };
        return albumPageState.inlineAnalysisRefs;
    }

    function updateInlineAnalysis(summary) {
        const refs = ensureInlineAnalysisPanel();
        if (!refs) {
            return;
        }

        refs.sampleRate.textContent = Number(summary?.sampleRateHz) > 0
            ? `${(Number(summary.sampleRateHz) / 1000).toFixed(1)} kHz`
            : '—';
        refs.bitDepth.textContent = Number(summary?.bitsPerSample) > 0
            ? `${Number(summary.bitsPerSample)}-bit`
            : '—';
        refs.channels.textContent = formatChannelLabel(Number(summary?.channels || 0));
        refs.duration.textContent = Number(summary?.durationSeconds) > 0
            ? formatAnalysisClock(summary.durationSeconds)
            : '--:--';
        refs.nyquist.textContent = Number(summary?.nyquistHz) > 0
            ? `${(Number(summary.nyquistHz) / 1000).toFixed(1)} kHz`
            : '—';
        refs.size.textContent = formatAnalysisFileSize(summary?.fileSize);
        refs.dynamicRange.textContent = formatAnalysisNumber(summary?.dynamicRangeDb, 2, ' dB');
        refs.peak.textContent = formatAnalysisNumber(summary?.peakAmplitudeDb, 2, ' dB');
        refs.rms.textContent = formatAnalysisNumber(summary?.rmsLevelDb, 2, ' dB');
        const sampleCount = Number(summary?.totalSamples);
        refs.samples.textContent = Number.isFinite(sampleCount) && sampleCount > 0
            ? sampleCount.toLocaleString()
            : '—';
        if (refs.subtitle) {
            const artist = summary?.artist || 'Unknown Artist';
            const album = summary?.album || 'Unknown Album';
            refs.subtitle.textContent = `${artist} • ${album}`;
        }
    }

    async function loadTrackSummaryForInline(trackId, filePath) {
        try {
            const queryPath = typeof filePath === 'string' ? filePath.trim() : '';
            const url = queryPath
                ? `/api/library/analysis/track/${encodeURIComponent(trackId)}/summary?filePath=${encodeURIComponent(queryPath)}`
                : `/api/library/analysis/track/${encodeURIComponent(trackId)}/summary`;
            const summary = await fetchJson(url);
            updateInlineAnalysis(summary);
        } catch {
            // Keep panel as-is when unavailable.
        }
    }

    function ensureLibrarySpectrogramModal() {
        if (albumPageState.spectrogramModalRefs) {
            return albumPageState.spectrogramModalRefs;
        }

        const root = document.createElement('div');
        root.className = 'library-spectrogram-modal';
        root.innerHTML = `
            <div class="library-spectrogram-modal__backdrop" data-library-spectrogram-close></div>
            <div class="library-spectrogram-modal__panel" role="dialog" aria-modal="true" aria-label="Spectrogram viewer">
                <div class="library-spectrogram-modal__header">
                    <h3 id="librarySpectrogramModalTitle">Spectrogram</h3>
                    <button type="button" class="library-spectrogram-modal__close" data-library-spectrogram-close aria-label="Close spectrogram viewer">✕</button>
                </div>
                <div class="library-spectrogram-modal__status" id="librarySpectrogramModalStatus">Loading spectrogram...</div>
                <img id="librarySpectrogramModalImage" alt="Track spectrogram" />
            </div>
        `;
        document.body.appendChild(root);

        const title = root.querySelector('#librarySpectrogramModalTitle');
        const status = root.querySelector('#librarySpectrogramModalStatus');
        const image = root.querySelector('#librarySpectrogramModalImage');
        root.querySelectorAll('[data-library-spectrogram-close]').forEach((node) => {
            node.addEventListener('click', () => root.classList.remove('is-open'));
        });
        document.addEventListener('keydown', (event) => {
            if (event.key === 'Escape' && root.classList.contains('is-open')) {
                root.classList.remove('is-open');
            }
        });

        albumPageState.spectrogramModalRefs = { root, title, status, image };
        return albumPageState.spectrogramModalRefs;
    }

    function openLibrarySpectrogramModal(spectrogramUrl, trackTitle, trackId, filePath) {
        if (!spectrogramUrl) {
            return;
        }
        const modal = ensureLibrarySpectrogramModal();
        if (!modal?.root || !modal?.status || !modal?.image || !modal?.title) {
            return;
        }

        modal.title.textContent = trackTitle ? `Spectrogram • ${trackTitle}` : 'Spectrogram';
        modal.status.textContent = 'Loading spectrogram...';
        modal.status.style.display = '';
        modal.image.style.display = 'none';
        modal.root.classList.add('is-open');

        modal.image.onload = () => {
            modal.status.style.display = 'none';
            modal.image.style.display = 'block';
        };
        modal.image.onerror = () => {
            modal.status.style.display = '';
            modal.status.textContent = 'Unable to load spectrogram image.';
            modal.image.style.display = 'none';
        };

        const safeSpectrogramUrl = toSafeHttpUrl(appendCacheKey(spectrogramUrl));
        if (!safeSpectrogramUrl) {
            modal.status.textContent = 'Invalid spectrogram URL.';
            return;
        }
        modal.image.src = safeSpectrogramUrl;

        if (trackId) {
            void loadTrackSummaryForInline(trackId, filePath);
        }
    }

    function formatTrackFormat(track) {
        const extension = String(track?.extension || '').trim();
        const codec = String(track?.codec || '').trim();
        if (extension && codec) {
            return `${extension.replace(/^\./, '').toUpperCase()} / ${codec}`;
        }
        if (extension) {
            return extension.replace(/^\./, '').toUpperCase();
        }
        if (codec) {
            return codec;
        }
        return 'Unknown';
    }

    function formatTrackSampleRate(track) {
        const hz = Number(track?.sampleRateHz || 0);
        if (hz <= 0) {
            return '—';
        }
        const khz = hz / 1000;
        const precision = Number.isInteger(khz) ? 0 : 1;
        return `${khz.toFixed(precision)} kHz`;
    }

    function formatTrackBitDepth(track) {
        const bits = Number(track?.bitsPerSample || 0);
        return bits > 0 ? `${bits}-bit` : '—';
    }

    function formatTrackBitrate(track) {
        const kbps = Number(track?.bitrateKbps || 0);
        return kbps > 0 ? `${kbps} kbps` : '—';
    }

    function formatTrackVariantLabel(track) {
        const variant = String(track?.audioVariant || '').trim().toLowerCase();
        if (variant === 'atmos') return 'Atmos';
        if (variant === 'surround') return 'Surround';
        return 'Stereo';
    }

    function formatTrackVariantClass(track) {
        const variant = String(track?.audioVariant || '').trim().toLowerCase();
        if (variant === 'atmos') return 'track-variant-pill--atmos';
        if (variant === 'surround') return 'track-variant-pill--surround';
        return 'track-variant-pill--stereo';
    }

    function formatTrackAudioLabel(track) {
        const parts = [formatTrackFormat(track), formatTrackBitrate(track)]
            .filter((part) => part && part !== '—' && part !== 'Unknown');
        return parts.length ? parts.join(' · ') : 'Unknown';
    }

    function formatTrackQualityLabel(track) {
        const rank = Number(track?.qualityRank || 0);
        if (rank === 4) return 'Hi-Res';
        if (rank === 3) return 'Lossless';
        if (rank === 2) return 'High';
        if (rank === 1) return 'Standard';
        return 'Unknown';
    }

    function formatTrackQualityClass(track) {
        const rank = Number(track?.qualityRank || 0);
        if (rank >= 4) return 'quality-pill--hires';
        if (rank === 3) return 'quality-pill--lossless';
        if (rank === 2) return 'quality-pill--high';
        if (rank === 1) return 'quality-pill--standard';
        return 'quality-pill--unknown';
    }

    function formatTrackLyricsLabel(track) {
        const status = String(track?.lyricsStatus || '').trim();
        if (!status) {
            return 'None';
        }
        const normalized = status.toLowerCase().replaceAll(/\s+/g, '_');
        if (normalized === 'ttml_lrc_txt' || normalized === 'ttml_synced_unsynced') return 'TTML+LRC+TXT';
        if (normalized === 'ttml_lrc' || normalized === 'ttml_synced') return 'TTML+LRC';
        if (normalized === 'ttml_txt' || normalized === 'ttml_unsynced') return 'TTML+TXT';
        if (normalized === 'ttml') return 'TTML';
        if (normalized === 'lrc' || normalized === 'synced') return 'LRC';
        if (normalized === 'txt' || normalized === 'unsynced') return 'TXT';
        if (normalized === 'lrc_txt' || normalized === 'both') return 'LRC+TXT';
        if (normalized === 'embedded') return 'Embedded';
        if (normalized === 'missing' || normalized === 'none') return 'None';
        if (normalized === 'error') return 'Error';
        return status;
    }

    function formatTrackLyricsClass(track) {
        const normalized = String(track?.lyricsStatus || '').trim().toLowerCase().replaceAll(/\s+/g, '_');
        if (['lrc', 'synced', 'both', 'lrc_txt', 'embedded', 'ttml', 'ttml_lrc', 'ttml_lrc_txt', 'ttml_synced', 'ttml_synced_unsynced'].includes(normalized)) {
            return 'lyrics-pill--available';
        }
        if (['txt', 'unsynced', 'ttml_txt', 'ttml_unsynced'].includes(normalized)) {
            return 'lyrics-pill--partial';
        }
        if (normalized === 'missing' || normalized === 'none') {
            return 'lyrics-pill--missing';
        }
        if (normalized === 'error') {
            return 'lyrics-pill--error';
        }
        return 'lyrics-pill--unknown';
    }

    function formatMetricSampleRate(value) {
        const number = Number(value);
        if (!Number.isFinite(number) || number <= 0) {
            return '—';
        }
        return `${(number / 1000).toFixed(1)} kHz`;
    }

    function formatMetricBitDepth(value) {
        const number = Number(value);
        return Number.isFinite(number) && number > 0 ? `${number}-bit` : '—';
    }

    function formatMetricChannels(value) {
        const number = Number(value);
        if (!Number.isFinite(number) || number <= 0) {
            return '—';
        }
        if (number === 2) return 'Stereo';
        if (number === 1) return 'Mono';
        return `${number}`;
    }

    function formatMetricNyquist(value) {
        return formatMetricSampleRate(value);
    }

    function formatMetricSamples(value) {
        const number = Number(value);
        if (!Number.isFinite(number) || number <= 0) {
            return '—';
        }
        return number.toLocaleString();
    }

    function getTrackRowPlayPath(row) {
        return String(row.querySelector('[data-library-play-path]')?.dataset.libraryPlayPath || '')
            .trim()
            .toLowerCase();
    }

    function getTrackSummaryCacheKey(trackId, filePath) {
        const path = typeof filePath === 'string' ? filePath.trim().toLowerCase() : '';
        return `${trackId}|${path}`;
    }

    async function getTrackSummaryData(trackId, filePath) {
        const cacheKey = getTrackSummaryCacheKey(trackId, filePath);
        if (albumPageState.trackSummaryCache.has(cacheKey)) {
            return albumPageState.trackSummaryCache.get(cacheKey);
        }
        const queryPath = typeof filePath === 'string' ? filePath.trim() : '';
        const url = queryPath
            ? `/api/library/analysis/track/${encodeURIComponent(trackId)}/summary?filePath=${encodeURIComponent(queryPath)}`
            : `/api/library/analysis/track/${encodeURIComponent(trackId)}/summary`;
        const summary = await fetchJson(url);
        albumPageState.trackSummaryCache.set(cacheKey, summary);
        return summary;
    }

    function updateTrackRowMetrics(trackId, summary, variantKey, filePath) {
        const normalizedRequestedPath = typeof filePath === 'string' ? filePath.trim().toLowerCase() : '';
        const normalizedSummaryPath = typeof summary?.filePath === 'string' ? summary.filePath.trim().toLowerCase() : '';

        let rowEl = null;
        if (variantKey) {
            const variantKeyText = String(variantKey);
            const safeVariantKey = globalThis.CSS && typeof globalThis.CSS.escape === 'function'
                ? globalThis.CSS.escape(variantKeyText)
                : variantKeyText.replaceAll('\\', String.raw`\\`).replaceAll('"', String.raw`\"`);
            rowEl = document.querySelector(`.track-row[data-track-id="${trackId}"][data-track-variant-key="${safeVariantKey}"]`);
        }

        if (!rowEl && normalizedRequestedPath) {
            rowEl = Array.from(document.querySelectorAll(`.track-row[data-track-id="${trackId}"]`))
                .find((element) => getTrackRowPlayPath(element) === normalizedRequestedPath) || null;
        }
        if (!rowEl && normalizedSummaryPath) {
            rowEl = Array.from(document.querySelectorAll(`.track-row[data-track-id="${trackId}"]`))
                .find((element) => getTrackRowPlayPath(element) === normalizedSummaryPath) || null;
        }
        if (!rowEl) {
            rowEl = document.querySelector(`.track-row[data-track-id="${trackId}"][data-track-primary="true"]`)
                || document.querySelector(`.track-row[data-track-id="${trackId}"]`);
        }
        if (!rowEl) {
            return;
        }

        const setMetric = (key, value) => {
            const target = rowEl.querySelector(`[data-track-cell="${key}"]`);
            if (target) {
                target.textContent = value;
            }
        };

        setMetric('sample-rate', formatMetricSampleRate(summary?.sampleRateHz));
        setMetric('bit-depth', formatMetricBitDepth(summary?.bitsPerSample));
        setMetric('channels', formatMetricChannels(summary?.channels));
        setMetric('duration', formatAnalysisClock(summary?.durationSeconds));
        setMetric('nyquist', formatMetricNyquist(summary?.nyquistHz));
        setMetric('dynamic-range', formatAnalysisNumber(summary?.dynamicRangeDb, 2, ' dB'));
        setMetric('peak', formatAnalysisNumber(summary?.peakAmplitudeDb, 2, ' dB'));
        setMetric('rms', formatAnalysisNumber(summary?.rmsLevelDb, 2, ' dB'));
        setMetric('samples', formatMetricSamples(summary?.totalSamples));
    }

    async function scheduleTrackMetricsUpdate(trackId, filePath, variantKey) {
        if (!trackId) {
            return;
        }
        try {
            const summary = await getTrackSummaryData(trackId, filePath);
            updateTrackRowMetrics(trackId, summary, variantKey, filePath);
        } catch {
            // Ignore single-row summary errors.
        }
    }

    function buildAlbumTrackUrls(track, rowFilePath) {
        return {
            spectrogramUrl: rowFilePath
                ? `/api/library/analysis/track/${encodeURIComponent(track.id)}/spectrogram?filePath=${encodeURIComponent(rowFilePath)}`
                : `/api/library/analysis/track/${encodeURIComponent(track.id)}/spectrogram`,
            lrcEditorUrl: `/Lrc?trackId=${encodeURIComponent(track.id)}`,
            tagEditorUrl: `/AutoTag/QuickTag?trackId=${encodeURIComponent(track.id)}`
        };
    }

    function buildAlbumTrackNumberCell(track, trackIndexText, rowFilePath, playLabel) {
        if (!track.availableLocally) {
            return `<span class="track-number__index">${escapeHtml(trackIndexText)}</span>`;
        }

        return `<span class="track-number__index">${escapeHtml(trackIndexText)}</span>
            <button class="library-track-play track-action track-play" type="button" data-library-play-track="${escapeHtml(String(track.id || ''))}" data-library-play-path="${escapeHtml(rowFilePath)}" aria-label="Play ${playLabel}">
                <span class="material-icons preview-controls" aria-hidden="true">play_arrow</span>
            </button>`;
    }

    function buildAlbumTrackRow(track) {
        const trackNum = track.trackNo || '';
        const rowFilePath = track.filePath || '';
        const rowKey = String(track.variantKey || `${track.id}:0`);
        const playLabel = escapeHtml(track.title || 'track');
        const trackIndexText = trackNum || '—';
        const { spectrogramUrl, lrcEditorUrl, tagEditorUrl } = buildAlbumTrackUrls(track, rowFilePath);
        const numberCellContent = buildAlbumTrackNumberCell(track, trackIndexText, rowFilePath, playLabel);
        const variantLabel = formatTrackVariantLabel(track);
        const variantClass = formatTrackVariantClass(track);
        const audioLabel = formatTrackAudioLabel(track);
        const qualityLabel = formatTrackQualityLabel(track);
        const qualityClass = formatTrackQualityClass(track);
        const lyricsLabel = formatTrackLyricsLabel(track);
        const lyricsClass = formatTrackLyricsClass(track);
        const sampleRateLabel = formatTrackSampleRate(track);
        const bitDepthLabel = formatTrackBitDepth(track);
        const channelsLabel = formatMetricChannels(track?.channels);
        const nyquistLabel = Number(track?.sampleRateHz) > 0
            ? formatMetricNyquist(Number(track.sampleRateHz) / 2)
            : '—';

        return `
            <div class="track-row${track.availableLocally ? ' track-row--local' : ''}" data-track-id="${escapeHtml(String(track.id || ''))}" data-track-variant-key="${escapeHtml(rowKey)}" data-track-primary="${track.isPrimaryVariant ? 'true' : 'false'}">
                <div class="track-number">${numberCellContent}</div>
                <div class="track-meta"><strong>${escapeHtml(track.title || 'Unknown')}</strong></div>
                <div class="track-audio">
                    <span class="track-variant-pill ${variantClass}">${escapeHtml(variantLabel)}</span>
                    <span class="track-audio-label">${escapeHtml(audioLabel)}</span>
                </div>
                <div class="track-quality"><span class="quality-pill ${qualityClass}">${escapeHtml(qualityLabel)}</span></div>
                <div class="track-lyrics"><span class="lyrics-pill ${lyricsClass}">${escapeHtml(lyricsLabel)}</span></div>
                <span class="track-duration" data-track-cell="duration">${formatDuration(track.durationMs)}</span>
                <span class="track-metric track-metric--sample-rate" data-track-cell="sample-rate">${escapeHtml(sampleRateLabel)}</span>
                <span class="track-metric track-metric--bit-depth" data-track-cell="bit-depth">${escapeHtml(bitDepthLabel)}</span>
                <span class="track-metric track-metric--channels" data-track-cell="channels">${escapeHtml(channelsLabel)}</span>
                <span class="track-metric track-metric--nyquist" data-track-cell="nyquist">${escapeHtml(nyquistLabel)}</span>
                <span class="track-metric track-metric--dynamic-range" data-track-cell="dynamic-range">—</span>
                <span class="track-metric track-metric--peak" data-track-cell="peak">—</span>
                <span class="track-metric track-metric--rms" data-track-cell="rms">—</span>
                <span class="track-metric track-metric--samples" data-track-cell="samples">—</span>
                <div class="track-actions">
                    <details class="track-actions-menu">
                        <summary title="Track actions" aria-label="Track actions">⋯</summary>
                        <div class="track-actions-menu__list">
                            <a href="${escapeHtml(spectrogramUrl)}"
                               data-library-spectrogram-url="${escapeHtml(spectrogramUrl)}"
                               data-library-spectrogram-title="${escapeHtml(track.title || 'Track')}"
                               data-library-track-id="${escapeHtml(String(track.id || ''))}"
                               data-library-track-file-path="${escapeHtml(rowFilePath)}"
                            >View Spectrogram</a>
                            <a href="${escapeHtml(lrcEditorUrl)}">Open LRC Editor</a>
                            <a href="${escapeHtml(tagEditorUrl)}">Open Tag Editor</a>
                        </div>
                    </details>
                </div>
            </div>
        `;
    }

    function renderAlbumTrackRows(trackContainer, tracks) {
        trackContainer.innerHTML = tracks.map((track) => {
            if (!albumPageState.albumTracksById.has(String(track.id))) {
                albumPageState.albumTracksById.set(String(track.id), track);
            }
            return buildAlbumTrackRow(track);
        }).join('');
    }

    function scheduleAlbumTrackMetrics(tracks) {
        tracks.forEach((track) => {
            const key = String(track?.id ?? '');
            if (!key) {
                return;
            }
            void scheduleTrackMetricsUpdate(track.id, track.filePath || '', track.variantKey || `${track.id}:0`);
        });
    }

    function toggleAlbumHeroActions(tracks) {
        const allLocal = tracks.every((track) => track.availableLocally);
        const heroActions = document.querySelector('.album-hero__actions');
        if (heroActions) {
            heroActions.style.display = allLocal ? 'none' : '';
        }
    }

    function getUniqueAlbumTracks(tracks) {
        const uniqueTracks = new Map();
        tracks.forEach((track) => {
            const key = String(track?.id ?? '');
            if (key && !uniqueTracks.has(key)) {
                uniqueTracks.set(key, track);
            }
        });
        return Array.from(uniqueTracks.values());
    }

    function updateAlbumTrackSummary(summaryTracks) {
        const trackCountEl = document.getElementById('albumTrackCount');
        if (trackCountEl) {
            trackCountEl.textContent = `${summaryTracks.length} ${summaryTracks.length === 1 ? 'track' : 'tracks'}`;
        }

        const totalMs = summaryTracks.reduce((sum, track) => sum + (track.durationMs || 0), 0);
        const totalDurationEl = document.getElementById('albumDuration');
        if (!totalDurationEl || totalMs <= 0) {
            return;
        }
        const totalMin = Math.floor(totalMs / 60000);
        const totalSec = Math.floor((totalMs % 60000) / 1000);
        totalDurationEl.textContent = totalMin >= 60
            ? `${Math.floor(totalMin / 60)} hr ${totalMin % 60} min`
            : `${totalMin} min ${totalSec} sec`;
    }

    async function populateAlbumArtistLinks(album) {
        if (!album.artistId) {
            return;
        }
        const artist = await fetchJsonOptional(`/api/library/artists/${album.artistId}`);
        if (!artist) {
            return;
        }
        const artistUrl = buildLibraryScopedUrl(`/Library/Artist/${album.artistId}`);
        const artistLink = document.getElementById('albumArtistLink');
        const artistNameEl = document.getElementById('albumArtistName');
        if (artistLink) {
            artistLink.href = artistUrl;
            artistLink.textContent = artist.name || 'Artist';
        }
        if (artistNameEl) {
            artistNameEl.href = artistUrl;
            artistNameEl.textContent = artist.name || 'Artist';
        }
    }

    function populateAlbumHero(album) {
        const titleEl = document.getElementById('albumTitle');
        if (titleEl) {
            titleEl.textContent = album.title || 'Album';
        }

        const breadcrumbTitle = document.getElementById('albumBreadcrumbTitle');
        if (breadcrumbTitle) {
            breadcrumbTitle.textContent = album.title || 'Album';
        }

        const artworkEl = document.getElementById('albumArtwork');
        if (artworkEl && album.preferredCoverPath) {
            const albumArtworkPath = `/api/library/image?path=${encodeURIComponent(album.preferredCoverPath)}&size=560`;
            const albumArtworkUrl = toSafeHttpUrl(appendCacheKey(albumArtworkPath));
            if (albumArtworkUrl) {
                artworkEl.innerHTML = `<img src="${escapeHtml(albumArtworkUrl)}" alt="${escapeHtml(album.title || 'Album')}" loading="eager" />`;
            }
        }

        const foldersEl = document.getElementById('albumFolders');
        if (foldersEl) {
            const folders = album.localFolders || [];
            foldersEl.innerHTML = folders.length
                ? folders.map((folder) => `<span class="folder-pill">${escapeHtml(folder)}</span>`).join('')
                : '';
        }
    }

    async function loadAlbum() {
        const albumId = document.querySelector('[data-album-id]')?.dataset.albumId;
        if (!albumId) {
            return;
        }

        const [album, tracks] = await Promise.all([
            fetchJsonOptional(`/api/library/albums/${albumId}`),
            fetchJson(`/api/library/albums/${albumId}/tracks`)
        ]);
        if (!album) {
            showToast('Album not found. Refresh the library and try again.', true);
            return;
        }

        populateAlbumHero(album);
        await populateAlbumArtistLinks(album);

        const trackContainer = document.getElementById('albumTracks');
        if (!trackContainer) {
            return;
        }

        albumPageState.albumTracksById.clear();
        if (!Array.isArray(tracks) || tracks.length === 0) {
            trackContainer.innerHTML = '<p>No tracks found for this album.</p>';
            return;
        }

        const summaryTracks = getUniqueAlbumTracks(tracks);
        updateAlbumTrackSummary(summaryTracks);
        renderAlbumTrackRows(trackContainer, tracks);
        scheduleAlbumTrackMetrics(tracks);
        toggleAlbumHeroActions(tracks);
    }

    async function loadAlbumDownloadDestinationOptions() {
        const destinationSelect = document.getElementById('downloadDestinationAlbum');
        if (!destinationSelect) {
            return;
        }

        let folders = [];
        try {
            const loaded = await fetchJson('/api/library/folders');
            folders = Array.isArray(loaded) ? loaded : [];
        } catch {
            folders = [];
        }

        albumPageState.folders = folders;
        const enabledFolders = folders.filter((folder) => {
            const value = String(folder?.enabled ?? '').trim().toLowerCase();
            if (!value) return true;
            if (typeof folder?.enabled === 'boolean') return folder.enabled;
            if (typeof folder?.enabled === 'number') return folder.enabled !== 0;
            return !/^(false|0|no|off|disabled)$/i.test(value);
        });

        const remembered = localStorage.getItem('libraryAlbumDestinationFolderId') || '';
        destinationSelect.innerHTML = '<option value="">Default destination</option>';
        enabledFolders.forEach((folder) => {
            const option = document.createElement('option');
            option.value = String(folder.id);
            option.textContent = folder.displayName || folder.rootPath || `Folder ${folder.id}`;
            destinationSelect.appendChild(option);
        });

        if (remembered && enabledFolders.some((folder) => String(folder.id) === remembered)) {
            destinationSelect.value = remembered;
        }

        destinationSelect.addEventListener('change', () => {
            const current = destinationSelect.value || '';
            if (current) {
                localStorage.setItem('libraryAlbumDestinationFolderId', current);
                if (globalThis.UserPrefs) globalThis.UserPrefs.set('libraryAlbumDestinationFolderId', current);
            } else {
                localStorage.removeItem('libraryAlbumDestinationFolderId');
                if (globalThis.UserPrefs) globalThis.UserPrefs.set('libraryAlbumDestinationFolderId', null);
            }
        });
    }

    function bindAlbumDownloadButton() {
        const downloadAlbumButton = document.getElementById('downloadAlbum');
        if (!downloadAlbumButton) {
            return;
        }
        downloadAlbumButton.addEventListener('click', async () => {
            const tracks = Array.from(albumPageState.albumTracksById.values()).filter((track) => {
                const source = getTrackSourceForTrack(track);
                if (!source) {
                    return false;
                }
                if (source.service !== 'apple') {
                    return !track.availableLocally;
                }

                const hasStereoVariant = track?.hasStereoVariant === true || track?.hasStereoVariant === 'true';
                const hasAtmosVariant = track?.hasAtmosVariant === true || track?.hasAtmosVariant === 'true';
                const hasAtmosCapability = track?.hasAtmos === true
                    || track?.hasAtmos === 'true'
                    || track?.appleHasAtmos === true
                    || track?.appleHasAtmos === 'true'
                    || hasAtmosVariant
                    || String(track?.audioVariant || '').toLowerCase() === 'atmos';
                const needsStereo = !hasStereoVariant;
                const needsAtmos = hasAtmosCapability && !hasAtmosVariant;
                return needsStereo || needsAtmos;
            });

            if (!tracks.length) {
                showToast('All tracks are already available locally.');
                return;
            }

            try {
                await queueAlbumDownloads(tracks);
            } catch (error) {
                showToast(`Failed to queue downloads: ${error.message}`, true);
            }
        });
    }

    function bindAlbumInteractionHandlers() {
        globalThis.DeezSpoTagLibraryInteractions?.bindGlobalLibraryInteractionHandlers?.({
            openLibrarySpectrogramModal,
            playLocalLibraryTrackInApp
        });
    }

    document.addEventListener('DOMContentLoaded', async () => {
        const page = document.querySelector('.library-page.album-page[data-album-id]');
        if (!page) {
            return;
        }

        try {
            bindAlbumInteractionHandlers();
            bindAlbumDownloadButton();
            await loadAlbumDownloadDestinationOptions();
            await loadAlbum();
        } catch (error) {
            const urlHint = error?.libraryUrl ? ` (${error.libraryUrl})` : '';
            showToast(`Library album page failed: ${error.message}${urlHint}`, true);
            console.error('Album page initialization failed.', error);
        }
    });
})();
