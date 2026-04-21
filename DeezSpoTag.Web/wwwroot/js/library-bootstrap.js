document.addEventListener('DOMContentLoaded', async () => {
    try {
        applyLibraryScopeSelectionFromLocation();
        updateTopSongsTracklistLink(null);
        const elements = getLibraryBootstrapElements();
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

        const targets = getLibraryLoadTargets();
        if (!shouldInitializeLibraryForCurrentPage(targets)) {
            return;
        }
        bindGlobalLibraryInteractionHandlers();
        if (targets.shouldLoadArtists) {
            primePendingLibraryReturnState();
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

        bindLibraryFilterEvents(targets.viewSelect, targets.searchInput, targets.sortSelect);
        bindAlbumDownloadButton(targets.downloadAlbumButton);
        if (targets.analysisButton) {
            targets.analysisButton.addEventListener('click', runAnalysis);
        }
        startLibraryRefreshIntervals(targets.shouldLoadAnalysis, targets.shouldLoadScanStatus);
    } catch (error) {
        const urlHint = error?.libraryUrl ? ` (${error.libraryUrl})` : '';
        DeezSpoTag.ui.alert(`Library error: ${error.message}${urlHint}`, { title: 'Library Error' });
        console.error('Library initialization failed.', error);
    }
});
