(() => {
    const common = globalThis.DeezSpoTagLibraryPageCommon;
    if (!common) {
        return;
    }

    const { appendCacheKey, fetchJson, showToast } = common;

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

    function renderAnalysisStat(label, value) {
        return `<span class="track-analysis-stat"><span class="track-analysis-stat__label">${label}:</span> <span class="track-analysis-stat__value">${value}</span></span>`;
    }

    function updateTrackAnalysisHeader(titleEl, subtitleEl, summary) {
        if (titleEl) {
            titleEl.textContent = summary?.title || 'Track Analysis';
        }
        if (subtitleEl) {
            const artist = summary?.artist || 'Unknown Artist';
            const album = summary?.album || 'Unknown Album';
            subtitleEl.textContent = `${artist} • ${album}`;
        }
    }

    function buildPrimaryTrackAnalysisStats(summary) {
        const sampleRateText = Number(summary?.sampleRateHz) > 0
            ? `${(Number(summary.sampleRateHz) / 1000).toFixed(1)} kHz`
            : '—';
        const bitDepthText = Number(summary?.bitsPerSample) > 0
            ? `${Number(summary.bitsPerSample)}-bit`
            : '—';
        const channelsText = formatChannelLabel(Number(summary?.channels || 0));
        const durationText = Number(summary?.durationSeconds) > 0
            ? formatAnalysisClock(summary.durationSeconds)
            : '--:--';
        const nyquistText = Number(summary?.nyquistHz) > 0
            ? `${(Number(summary.nyquistHz) / 1000).toFixed(1)} kHz`
            : '—';
        const fileSizeText = formatAnalysisFileSize(summary?.fileSize);

        return [
            renderAnalysisStat('Sample Rate', sampleRateText),
            renderAnalysisStat('Bit Depth', bitDepthText),
            renderAnalysisStat('Channels', channelsText),
            renderAnalysisStat('Duration', durationText),
            renderAnalysisStat('Nyquist', nyquistText),
            renderAnalysisStat('Size', fileSizeText)
        ].join('');
    }

    function buildSecondaryTrackAnalysisStats(summary) {
        const dynamicRangeText = formatAnalysisNumber(summary?.dynamicRangeDb, 2, ' dB');
        const peakText = formatAnalysisNumber(summary?.peakAmplitudeDb, 2, ' dB');
        const rmsText = formatAnalysisNumber(summary?.rmsLevelDb, 2, ' dB');
        const sampleCount = Number(summary?.totalSamples);
        const sampleCountText = Number.isFinite(sampleCount) && sampleCount > 0
            ? sampleCount.toLocaleString()
            : '—';
        return [
            renderAnalysisStat('Dynamic Range', dynamicRangeText),
            renderAnalysisStat('Peak', peakText),
            renderAnalysisStat('RMS', rmsText),
            renderAnalysisStat('Samples', sampleCountText)
        ].join('');
    }

    function buildTrackAnalysisSpectrogramUrl(trackId, summary) {
        const spectrogramSeconds = Math.max(10, Math.min(600, Number(summary?.spectrogramSeconds || 120)));
        const spectrogramWidth = Math.max(320, Math.min(4096, Number(summary?.spectrogramWidth || 1600)));
        const spectrogramHeight = Math.max(180, Math.min(2160, Number(summary?.spectrogramHeight || 720)));
        return appendCacheKey(
            `/api/library/analysis/track/${encodeURIComponent(trackId)}/spectrogram?width=${spectrogramWidth}&height=${spectrogramHeight}&seconds=${spectrogramSeconds}`
        );
    }

    function renderTrackAnalysisSpectrogram(trackId, summary, plotStatusEl, spectrogramEl) {
        spectrogramEl.onload = () => {
            plotStatusEl.style.display = 'none';
            spectrogramEl.style.display = 'block';
        };
        spectrogramEl.onerror = () => {
            plotStatusEl.style.display = '';
            plotStatusEl.textContent = 'Unable to load spectrogram image.';
            spectrogramEl.style.display = 'none';
        };
        spectrogramEl.src = buildTrackAnalysisSpectrogramUrl(trackId, summary);
    }

    async function loadTrackAnalysisPage() {
        const page = document.querySelector('.library-track-analysis-page[data-track-id]');
        if (!page) {
            return;
        }

        const trackId = page.dataset.trackId;
        if (!trackId) {
            return;
        }

        const titleEl = document.getElementById('trackAnalysisTitle');
        const subtitleEl = document.getElementById('trackAnalysisSubtitle');
        const primaryEl = document.getElementById('trackAnalysisPrimary');
        const secondaryEl = document.getElementById('trackAnalysisSecondary');
        const pathEl = document.getElementById('trackAnalysisPath');
        const plotStatusEl = document.getElementById('trackAnalysisPlotStatus');
        const spectrogramEl = document.getElementById('trackAnalysisSpectrogram');
        if (!primaryEl || !secondaryEl || !pathEl || !plotStatusEl || !spectrogramEl) {
            return;
        }

        try {
            const summary = await fetchJson(`/api/library/analysis/track/${encodeURIComponent(trackId)}/summary`);
            updateTrackAnalysisHeader(titleEl, subtitleEl, summary);
            primaryEl.innerHTML = buildPrimaryTrackAnalysisStats(summary);
            secondaryEl.innerHTML = buildSecondaryTrackAnalysisStats(summary);
            pathEl.textContent = summary?.filePath || '';
            renderTrackAnalysisSpectrogram(trackId, summary, plotStatusEl, spectrogramEl);
            if (summary?.analysisWarning) {
                showToast(`Analysis warning: ${summary.analysisWarning}`, true);
            }
        } catch (error) {
            if (titleEl) {
                titleEl.textContent = 'Track Analysis';
            }
            if (subtitleEl) {
                subtitleEl.textContent = 'Failed to load track analysis.';
            }
            plotStatusEl.style.display = '';
            plotStatusEl.textContent = error?.message
                ? `Failed to load analysis: ${error.message}`
                : 'Failed to load analysis.';
            spectrogramEl.style.display = 'none';
        }
    }

    document.addEventListener('DOMContentLoaded', () => {
        void loadTrackAnalysisPage();
    });
})();
