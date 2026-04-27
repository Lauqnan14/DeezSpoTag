function initializeLibraryBootstrapBindings(elements) {
    if (typeof bindBootstrapScanActions === 'function') {
        bindBootstrapScanActions(elements);
    }
    wireExclusiveFolderDestinationRoles();
    bindFolderModalActions(elements);
    bindFolderPathBrowser(elements);
    bindFolderPathInput(elements.folderPathInput, updateSaveFolderState);
    bindFolderChangeInput(elements.folderConvertEnabledInput, syncFolderConversionFieldsState);
    bindFolderChangeInput(elements.folderConvertFormatInput, syncFolderConversionFieldsState);
    bindFolderChangeInput(elements.folderConvertBitrateInput, syncFolderConversionFieldsState);
    updateSaveFolderState();
    syncFolderConversionFieldsState();
}

function initializeLibraryBootstrapState(targets) {
    bindGlobalLibraryInteractionHandlers();
    if (targets.shouldLoadArtists) {
        primePendingLibraryReturnState();
        if (typeof bindLibraryReturnStateAutoPersist === 'function') {
            bindLibraryReturnStateAutoPersist();
        }
    }
    if (targets.shouldLoadSoundtracks && typeof primePendingSoundtrackReturnState === 'function') {
        primePendingSoundtrackReturnState();
    }
    bindDeferredSoundtrackInitialization(targets.shouldLoadSoundtracks);
    if (targets.shouldLoadArtistAlbums) {
        initializeDiscographyFilterState();
    }
    if (targets.shouldDeferViewFolderLoad) {
        ensureLibraryViewDefaultOption();
    }
    if (targets.searchInput) {
        targets.searchInput.value = libraryState.artistSearchQuery;
    }
    if (targets.sortSelect) {
        targets.sortSelect.value = libraryState.artistSortKey;
    }
}

async function initializeLibraryBootstrapData(targets) {
    bindIndexActionsDropdown();
    await runInitialLibraryLoads(targets);
    bindSavedPreferenceButtons();
    await initializeArtistAlbumsPage(targets.shouldLoadArtistAlbums);

    if (typeof initWatchlistToggle === 'function') {
        await initWatchlistToggle();
    }
    await initSpotifyIdEditor();
    if (targets.shouldLoadArtistAlbums && typeof initArtistActionsDropdown === 'function') {
        initArtistActionsDropdown();
    }
    initDiscographyFilters();
}

function initializeLibraryBootstrapEvents(targets) {
    bindLibraryFilterEvents(targets.viewSelect, targets.searchInput, targets.sortSelect);
    bindAlbumDownloadButton(targets.downloadAlbumButton);
    if (targets.analysisButton) {
        targets.analysisButton.addEventListener('click', runAnalysis);
    }
    startLibraryRefreshIntervals(targets.shouldLoadAnalysis, targets.shouldLoadScanStatus);
}

async function initializeLibraryBootstrap() {
    applyLibraryScopeSelectionFromLocation();
    updateTopSongsTracklistLink(null);
    const elements = getLibraryBootstrapElements();
    initializeLibraryBootstrapBindings(elements);

    const targets = getLibraryLoadTargets();
    if (!shouldInitializeLibraryForCurrentPage(targets)) {
        return;
    }

    initializeLibraryBootstrapState(targets);
    await initializeLibraryBootstrapData(targets);
    initializeLibraryBootstrapEvents(targets);
}

document.addEventListener('DOMContentLoaded', async () => {
    try {
        await initializeLibraryBootstrap();
    } catch (error) {
        const urlHint = error?.libraryUrl ? ` (${error.libraryUrl})` : '';
        DeezSpoTag.ui.alert(`Library error: ${error.message}${urlHint}`, { title: 'Library Error' });
        console.error('Library initialization failed.', error);
    }
});
