/**
 * Enhanced deezer queue management with proper error handling and display
 * Ported from deezer webui queue handling
 */

class DeezerQueueEnhanced {
    constructor() {
        this.queue = {};
        this.queueOrder = [];
        this.queueComplete = [];
        this.activeDownloads = [];
        this.maxConcurrentDownloads = 1;
        
        this.initializeSignalR();
        this.initializeUI();
    }

    /**
     * Initialize SignalR connection for real-time updates
     */
    initializeSignalR() {
        this.connection = new signalR.HubConnectionBuilder()
            .withUrl("/deezerQueueHub")
            .build();

        // Handle queue updates with deezer-style error processing
        this.connection.on("updateQueue", (update) => {
            this.handleUpdateQueue(update);
        });

        // Handle items added to queue
        this.connection.on("addedToQueue", (item) => {
            this.handleAddedToQueue(item);
        });

        // Handle queue errors
        this.connection.on("queueError", (error) => {
            this.handleQueueError(error);
        });

        // Handle download warnings
        this.connection.on("downloadWarn", (warning) => {
            const message = warning?.data?.message || warning?.message || 'Download warning';
            this.showToast(message, 'warning');
        });


        // Handle download start
        this.connection.on("startDownload", (data) => {
            this.handleStartDownload(data);
        });

        // Handle download completion
        this.connection.on("finishDownload", (data) => {
            this.handleFinishDownload(data);
        });

        // Start the connection
        this.connection.start().then(() => {
            console.log("DeezerQueue SignalR connected");
            this.loadInitialQueue();
        }).catch(err => {
            console.error("DeezerQueue SignalR connection error:", err);
        });
    }

    /**
     * Initialize UI elements
     */
    initializeUI() {
        // Create queue container if it doesn't exist
        if (!document.getElementById('deezer-queue-container')) {
            this.createQueueContainer();
        }
    }

    /**
     * Create the queue container HTML structure
     */
    createQueueContainer() {
        const container = document.createElement('div');
        container.id = 'deezer-queue-container';
        container.innerHTML = `
            <div class="queue-header">
                <h3>Download Queue</h3>
                <div class="queue-controls">
                    <button id="clean-queue-btn" class="action-btn action-btn-sm" title="Clear Finished">🧹</button>
                    <button id="cancel-queue-btn" class="action-btn action-btn-sm btn-danger" title="Cancel All">❌</button>
                </div>
            </div>
            <div id="queue-list" class="queue-list"></div>
            <div id="queue-complete" class="queue-complete"></div>
        `;
        
        // Add to page (adjust selector as needed)
        const targetElement = document.querySelector('.main-content') || document.body;
        targetElement.appendChild(container);

        // Add event listeners
        document.getElementById('clean-queue-btn').addEventListener('click', () => this.cleanQueue());
        document.getElementById('cancel-queue-btn').addEventListener('click', () => this.cancelQueue());
    }

    /**
     * Load initial queue state
     */
    async loadInitialQueue() {
        try {
            const response = await fetch('/Activities/GetDownloadQueue');
            const data = await response.json();
            
            if (data.success) {
                this.initializeQueue(data.data);
            }
        } catch (error) {
            console.error("Error loading initial queue:", error);
        }
    }

    /**
     * Initialize queue with data from server
     */
    initializeQueue(data) {
        this.queue = data.queue || {};
        this.queueOrder = data.queueOrder || [];
        this.activeDownloads = data.activeDownloads || 0;
        this.maxConcurrentDownloads = data.maxConcurrentDownloads || 1;

        // Process existing items
        this.queueOrder.forEach(uuid => {
            if (this.queue[uuid]) {
                this.addQueueItemToUI(this.queue[uuid]);
            }
        });

        // Process completed items
        Object.values(this.queue).forEach(item => {
            if (this.isItemCompleted(item)) {
                this.addCompletedItemToUI(item);
            }
        });

        this.updateQueueStats();
    }

    /**
     * Handle queue updates with deezer-style error processing
     */
    handleUpdateQueue(update) {
        const { uuid, failed, downloaded, error, errid, data, type, progress, segmentProgress, segmentTotal, status } = update;

        if (!uuid || !this.queue[uuid]) return;

        const item = this.queue[uuid];

        // Update status if provided
        if (status) {
            item.status = status;
        }

        // Update progress
        if (progress !== undefined) {
            item.progress = progress;
        }

        if (typeof segmentProgress === 'number') {
            item.segmentProgress = segmentProgress;
        }
        if (typeof segmentTotal === 'number') {
            item.segmentTotal = segmentTotal;
        }

        // Update quality and engine when fallback changes them
        if (update.quality) {
            item.quality = update.quality;
            item.bitrate = update.quality;
            item.Bitrate = update.quality;
            this.updateQualityBadge(uuid, item);
        }
        if (update.engine) {
            item.engine = update.engine;
            this.updateEngineBadge(uuid, item);
        }

        // Handle successful download
        if (downloaded) {
            item.downloaded = (item.downloaded || 0) + 1;
            this.updateProgressBar(uuid, item);
        }

        // Handle failed download with deezer-style error reporting
        if (failed) {
            item.failed = (item.failed || 0) + 1;

            // Add error to item's error list (deezer style)
            if (!item.errors) item.errors = [];

            const errorObj = {
                message: error || "Unknown error",
                errid: errid || null,
                data: data || {},
                type: type || "track"
            };

            item.errors.push(errorObj);

            // Update UI to show error
            this.updateItemErrorDisplay(uuid, item);
        }

        // Update item status
        this.updateItemStatus(uuid, item);
        this.updateProgressBar(uuid, item);

        if (this.isItemCompleted(item)) {
            this.moveToCompleted(uuid);
        }
    }

    /**
     * Handle items added to queue
     */
    handleAddedToQueue(item) {
        if (Array.isArray(item)) {
            item.forEach(i => this.addSingleItemToQueue(i));
        } else {
            this.addSingleItemToQueue(item);
        }
    }

    /**
     * Add single item to queue
     */
    addSingleItemToQueue(item) {
        // Initialize item properties (deezer style)
        item.downloaded = item.downloaded || 0;
        item.failed = item.failed || 0;
        item.progress = item.progress || 0;
        item.errors = item.errors || [];
        item.status = item.status || "inQueue";

        this.queue[item.uuid] = item;
        
        if (!this.queueOrder.includes(item.uuid)) {
            this.queueOrder.push(item.uuid);
        }

        this.addQueueItemToUI(item);
        this.updateQueueStats();
    }

    /**
     * Handle queue errors (URL parsing, generation errors)
     */
    handleQueueError(error) {
        const { link, error: errorMessage, errid } = error;
        
        console.error("Queue error:", error);
        
        // Show user-friendly error message
        let displayMessage = errorMessage;
        if (errid) {
            displayMessage = this.getErrorMessage(errid) || errorMessage;
        }
        
        this.showToast(`Error adding "${link}": ${displayMessage}`, 'error');
    }

    /**
     * Handle download completion
     */
    handleFinishDownload(data) {
        const { uuid, title } = data;
        
        if (this.queue[uuid]) {
            this.queue[uuid].status = "completed";
            this.moveToCompleted(uuid);
            this.showToast(`Finished downloading "${title}"`, 'success');
        }
    }

    /**
     * Handle download start
     */
    handleStartDownload(data) {
        const uuid = typeof data === 'string' ? data : data.uuid;
        
        if (this.queue[uuid]) {
            this.queue[uuid].status = "downloading";
            this.updateItemStatus(uuid, this.queue[uuid]);
        }
    }

    /**
     * Add queue item to UI with deezer-style display (replaces existing element if present)
     */
    addQueueItemToUI(item) {
        const queueList = document.getElementById('queue-list');
        if (!queueList) return;

        const itemElement = this.createQueueItemElement(item);
        const existing = document.getElementById(`queue-item-${item.uuid}`);
        if (existing) {
            existing.replaceWith(itemElement);
        } else {
            queueList.appendChild(itemElement);
        }
    }

    /**
     * Create queue item element with deezer-style error display
     */
    createQueueItemElement(item) {
        const element = document.createElement('div');
        element.className = 'queue-item';
        element.id = `queue-item-${item.uuid}`;
        element.dataset.uuid = item.uuid;

        const hasErrors = item.errors && item.errors.length > 0;
        const hasFails = item.failed > 0;
        const allFailed = item.size > 0 && item.failed >= item.size;

        // Determine status colors (deezer style)
        let statusClass = 'status-normal';
        if (allFailed) {
            statusClass = 'status-failed';
        } else if (hasFails || hasErrors) {
            statusClass = 'status-with-errors';
        } else if (item.status === 'completed') {
            statusClass = 'status-completed';
        }

        element.innerHTML = `
            <div class="item-info">
                <div class="item-cover">
                    <img src="${item.cover || '/images/default-cover.png'}" alt="Cover" />
                    ${(item.quality || item.bitrate || item.Bitrate || item.Quality) ? `<span class="bitrate-tag">${this.getQualityText(item.quality || item.bitrate || item.Bitrate || item.Quality)}</span>` : ''}
                </div>
                <div class="item-details">
                    <div class="item-title">
                        ${item.explicit ? '<i class="explicit-icon">🅴</i>' : ''}
                        ${item.title}
                    </div>
                    <div class="item-artist">${item.artist}</div>
                    ${(item.sourceService || item.engine) ? `<div class="item-engine">Engine: ${item.sourceService || item.engine}</div>` : ''}
                </div>
                <div class="item-status">
                    <div class="download-count">${item.downloaded + item.failed}/${item.size}</div>
                    ${hasFails ? `
                        <div class="error-count" onclick="deezerQueue.showErrors('${item.uuid}')" title="Click to view errors">
                            ${item.failed} <i class="error-icon">⚠️</i>
                        </div>
                    ` : ''}
                </div>
            </div>
            <div class="item-progress">
                <div class="progress-bar ${statusClass}">
                    <div class="progress-fill" style="width: ${item.progress || 0}%"></div>
                </div>
                <div class="segment-label">${this.formatSegmentLabel(item)}</div>
                <div class="item-actions">
                    ${this.getActionButton(item)}
                </div>
            </div>
        `;

        return element;
    }

    /**
     * Get action button based on item status (deezer style)
     */
    getActionButton(item) {
        const hasErrors = item.errors && item.errors.length > 0;
        const hasFails = item.failed > 0;
        const allFailed = item.size > 0 && item.failed >= item.size;

        if (item.status === 'completed' || item.status === 'download finished') {
            if (allFailed) {
                return '<button class="action-btn action-btn-sm retry-btn" onclick="deezerQueue.retryDownload(\'' + item.uuid + '\')">🔄</button>';
            } else if (hasFails || hasErrors) {
                return '<button class="action-btn action-btn-sm warning-btn" onclick="deezerQueue.retryDownload(\'' + item.uuid + '\')">⚠️</button>';
            } else {
                return '<button class="action-btn action-btn-sm success-btn">✅</button>';
            }
        } else if (item.status === 'downloading') {
            return '<button class="action-btn action-btn-sm btn-danger cancel-btn" onclick="deezerQueue.cancelDownload(\'' + item.uuid + '\')">❌</button>';
        } else {
            return '<button class="action-btn action-btn-sm btn-danger remove-btn" onclick="deezerQueue.removeFromQueue(\'' + item.uuid + '\')">🗑️</button>';
        }
    }

    /**
     * Update item error display (deezer style)
     */
    updateItemErrorDisplay(uuid, item) {
        const element = document.getElementById(`queue-item-${uuid}`);
        if (!element) return;

        const errorCountElement = element.querySelector('.error-count');
        if (errorCountElement) {
            errorCountElement.innerHTML = `${item.failed} <i class="error-icon">⚠️</i>`;
        } else if (item.failed > 0) {
            // Add error count if it doesn't exist
            const statusElement = element.querySelector('.item-status');
            if (statusElement) {
                const errorDiv = document.createElement('div');
                errorDiv.className = 'error-count';
                errorDiv.onclick = () => this.showErrors(uuid);
                errorDiv.title = 'Click to view errors';
                errorDiv.innerHTML = `${item.failed} <i class="error-icon">⚠️</i>`;
                statusElement.appendChild(errorDiv);
            }
        }

        // Update progress bar color
        this.updateProgressBarColor(element, item);
    }

    /**
     * Update progress bar color based on status (deezer style)
     */
    updateProgressBarColor(element, item) {
        const progressBar = element.querySelector('.progress-bar');
        if (!progressBar) return;

        // Remove existing status classes
        progressBar.classList.remove('status-normal', 'status-with-errors', 'status-failed', 'status-completed');

        // Add appropriate status class
        const hasErrors = item.errors && item.errors.length > 0;
        const hasFails = item.failed > 0;
        const allFailed = item.size > 0 && item.failed >= item.size;

        if (allFailed) {
            progressBar.classList.add('status-failed');
        } else if (hasFails || hasErrors) {
            progressBar.classList.add('status-with-errors');
        } else if (item.status === 'completed') {
            progressBar.classList.add('status-completed');
        } else {
            progressBar.classList.add('status-normal');
        }
    }

    /**
     * Update progress bar
     */
    updateProgressBar(uuid, item) {
        const element = document.getElementById(`queue-item-${uuid}`);
        if (!element) return;

        const progressFill = element.querySelector('.progress-fill');
        if (progressFill) {
            progressFill.style.width = `${item.progress || 0}%`;
        }

        const segmentLabel = element.querySelector('.segment-label');
        if (segmentLabel) {
            segmentLabel.textContent = this.formatSegmentLabel(item);
        }

        const downloadCount = element.querySelector('.download-count');
        if (downloadCount) {
            downloadCount.textContent = `${item.downloaded + item.failed}/${item.size}`;
        }

        this.updateProgressBarColor(element, item);
    }

    formatSegmentLabel(item) {
        const total = item.segmentTotal || 0;
        if (!total) {
            return '';
        }

        const current = Math.min(item.segmentProgress || 0, total);
        return `${current}/${total} segments`;
    }

    /**
     * Update item status
     */
    updateItemStatus(uuid, item) {
        const element = document.getElementById(`queue-item-${uuid}`);
        if (!element) return;

        // Update action button
        const actionsElement = element.querySelector('.item-actions');
        if (actionsElement) {
            actionsElement.innerHTML = this.getActionButton(item);
        }
    }

    /**
     * Update the quality badge on a queue item element
     */
    updateQualityBadge(uuid, item) {
        const element = document.getElementById(`queue-item-${uuid}`);
        if (!element) return;

        const coverDiv = element.querySelector('.item-cover');
        if (!coverDiv) return;

        const existingTag = coverDiv.querySelector('.bitrate-tag');
        const qualityVal = item.quality || item.bitrate || item.Bitrate || item.Quality;
        if (qualityVal) {
            const text = this.getQualityText(qualityVal);
            if (existingTag) {
                existingTag.textContent = text;
            } else {
                const span = document.createElement('span');
                span.className = 'bitrate-tag';
                span.textContent = text;
                coverDiv.appendChild(span);
            }
        } else if (existingTag) {
            existingTag.remove();
        }
    }

    /**
     * Update the engine label on a queue item element
     */
    updateEngineBadge(uuid, item) {
        const element = document.getElementById(`queue-item-${uuid}`);
        if (!element) return;

        const engineDiv = element.querySelector('.item-engine');
        const engineText = item.sourceService || item.engine;
        if (engineDiv && engineText) {
            engineDiv.textContent = `Engine: ${engineText}`;
        } else if (!engineDiv && engineText) {
            const detailsDiv = element.querySelector('.item-details');
            if (detailsDiv) {
                const div = document.createElement('div');
                div.className = 'item-engine';
                div.textContent = `Engine: ${engineText}`;
                detailsDiv.appendChild(div);
            }
        }
    }

    /**
     * Show errors for an item (deezer style)
     */
    showErrors(uuid) {
        const item = this.queue[uuid];
        if ((item?.errors?.length ?? 0) <= 0) return;

        const rows = item.errors.map(error => `
            <tr>
                <td>${error.data?.id || 'N/A'}</td>
                <td>${error.data?.artist || 'Unknown'}</td>
                <td>${error.data?.title || 'Unknown'}</td>
                <td title="${error.stack || ''}">${error.message}</td>
            </tr>
        `).join('');

        const tableHtml = `
            <table class="error-table">
                <thead>
                    <tr>
                        <th>ID</th>
                        <th>Artist</th>
                        <th>Title</th>
                        <th>Error</th>
                    </tr>
                </thead>
                <tbody>
                    ${rows}
                </tbody>
            </table>
        `;

        if (globalThis.DeezSpoTag?.ui?.alert) {
            DeezSpoTag.ui.alert(tableHtml, { title: `Errors for ${item.title}`, allowHtml: true });
        }
    }

    /**
     * Get user-friendly error message (deezer style)
     */
    getErrorMessage(errorId) {
        const errorMessages = {
            'notOnDeezer': 'Track not available on Deezer!',
            'notEncoded': 'Track not yet encoded!',
            'notEncodedNoAlternative': 'Track not yet encoded and no alternative found!',
            'wrongBitrate': 'Track not found at desired bitrate.',
            'wrongBitrateNoAlternative': 'Track not found at desired bitrate and no alternative found!',
            'wrongLicense': 'Your account can\'t stream the track at the desired bitrate.',
            'no360RA': 'Track is not available in Reality Audio 360.',
            'notAvailable': 'Track not available on deezer\'s servers!',
            'notAvailableNoAlternative': 'Track not available on deezer\'s servers and no alternative found!',
            'noSpaceLeft': 'No space left on target drive, clean up some space for the tracks.',
            'albumDoesntExists': 'Track\'s album does not exist, failed to gather info.',
            'notLoggedIn': 'You need to login to download tracks.',
            'wrongGeolocation': 'Your account can\'t stream the track from your current country.',
            'wrongGeolocationNoAlternative': 'Your account can\'t stream the track from your current country and no alternative found.',
            'invalidURL': 'URL not recognized',
            'unsupportedURL': 'URL not supported yet',
            'trackNotOnDeezer': 'Track not found on Deezer!',
            'albumNotOnDeezer': 'Album not found on Deezer!'
        };

        return errorMessages[errorId] || 'Unknown error occurred';
    }

    /**
     * Get human-readable quality text for any engine
     */
    getQualityText(quality) {
        if (!quality) return '';
        const q = String(quality).trim();
        const upper = q.toUpperCase();
        switch (upper) {
            // Deezer numeric bitrate codes
            case '9': return 'FLAC';
            case '3': return '320';
            case '1': return '128';
            case '15': return '360HQ';
            case '14': return '360MQ';
            case '13': return '360LQ';
            // Apple
            case 'ALAC': return 'ALAC';
            case 'AAC': return 'AAC';
            // Qobuz
            case '27': return 'Hi-Res';
            case '7': return 'FLAC 24';
            case '6': return 'FLAC 16';
            // Tidal
            case 'HI_RES_LOSSLESS': return 'Hi-Res';
            case 'LOSSLESS': return 'Lossless';
            // Amazon
            case 'FLAC': return 'FLAC';
            // Already human-readable (from NormalizePayloadKeys mapping)
            case 'FLAC LOSSLESS': return 'FLAC';
            case 'MP3 320KBPS': return '320';
            case 'MP3 128KBPS': return '128';
            case 'FLAC 16-BIT': return 'FLAC 16';
            case 'FLAC 24-BIT': return 'FLAC 24';
            case 'HI-RES 24-BIT': return 'Hi-Res';
            case 'HI-RES LOSSLESS': return 'Hi-Res';
            default: return q;
        }
    }

    /**
     * Check if item is completed
     */
    isItemCompleted(item) {
        return ['completed', 'withErrors', 'failed'].includes(item.status);
    }

    /**
     * Move item to completed section
     */
    moveToCompleted(uuid) {
        const element = document.getElementById(`queue-item-${uuid}`);
        if (element) {
            const queueComplete = document.getElementById('queue-complete');
            if (queueComplete) {
                queueComplete.appendChild(element);
            }
        }

        // Remove from active queue order
        const index = this.queueOrder.indexOf(uuid);
        if (index > -1) {
            this.queueOrder.splice(index, 1);
        }

        // Add to completed list
        if (!this.queueComplete.includes(uuid)) {
            this.queueComplete.push(uuid);
        }

        this.updateQueueStats();
    }

    /**
     * Update queue statistics
     */
    updateQueueStats() {
        // Update queue counters, progress, etc.
        // This would update any UI elements showing queue statistics
    }

    /**
     * Show toast notification
     */
    showToast(message, type = 'info') {
        // Simple toast implementation
        const toast = document.createElement('div');
        toast.className = `toast toast-${type}`;
        toast.textContent = message;
        
        document.body.appendChild(toast);
        
        setTimeout(() => {
            toast.remove();
        }, 3000);
    }

    /**
     * Clean completed downloads
     */
    cleanQueue() {
        fetch('/Activities/ClearCompletedDownloads', { method: 'POST' })
            .then(() => {
                this.queueComplete.forEach(uuid => {
                    const element = document.getElementById(`queue-item-${uuid}`);
                    if (element) element.remove();
                    delete this.queue[uuid];
                });
                this.queueComplete = [];
                this.updateQueueStats();
            })
            .catch(console.error);
    }

    /**
     * Cancel all downloads
     */
    async cancelQueue() {
        const confirmed = await DeezSpoTag.ui.confirm('Are you sure you want to cancel all downloads?', {
            title: 'Cancel Downloads'
        });
        if (!confirmed) {
            return;
        }
        fetch('/Activities/CancelAll', { method: 'POST' })
            .then(() => {
                // Clear UI
                document.getElementById('queue-list').innerHTML = '';
                this.queue = {};
                this.queueOrder = [];
                this.updateQueueStats();
            })
            .catch(console.error);
    }

    /**
     * Retry download
     */
    retryDownload(uuid) {
        fetch('/Activities/RetryFailed', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ uuid: uuid })
        })
            .then(response => {
                if (!response.ok) {
                    return response.text().then(text => {
                        throw new Error(text || 'Retry failed');
                    });
                }
                return response.json();
            })
            .then(() => this.refreshQueue())
            .catch(error => console.error('Retry download failed:', error));
    }

    /**
     * Cancel specific download
     */
    cancelDownload(uuid) {
        fetch(`/api/deezer/download/cancel/${uuid}`, { method: 'POST' })
            .catch(console.error);
    }

    /**
     * Remove item from queue
     */
    removeFromQueue(uuid) {
        fetch(`/Activities/RemoveFromQueue/${uuid}`, { method: 'POST' })
            .catch(console.error);
    }
}

// Initialize the enhanced deezer queue when the page loads
let deezerQueue;
document.addEventListener('DOMContentLoaded', () => {
    deezerQueue = new DeezerQueueEnhanced();
});
