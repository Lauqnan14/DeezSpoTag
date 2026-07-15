CREATE TABLE IF NOT EXISTS artist (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    sort_name TEXT,
    preferred_image_path TEXT,
    preferred_background_path TEXT,
    apple_biography TEXT,
    apple_biography_checked_at TEXT,
    lastfm_images_checked_at TEXT,
    deezer_id TEXT,
    metadata_json TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS album (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    artist_id BIGINT NOT NULL REFERENCES artist(id) ON DELETE CASCADE,
    title TEXT NOT NULL,
    release_date DATE,
    preferred_cover_path TEXT,
    has_animated_artwork INTEGER NOT NULL DEFAULT 0,
    deezer_id TEXT,
    metadata_json TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS track (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    album_id BIGINT NOT NULL REFERENCES album(id) ON DELETE CASCADE,
    title TEXT NOT NULL,
    duration_ms INTEGER,
    disc INTEGER,
    track_no INTEGER,
    lyrics_status TEXT,
    lyrics_type TEXT,
    deezer_id TEXT,
    tag_title TEXT,
    tag_artist TEXT,
    tag_album TEXT,
    tag_album_artist TEXT,
    tag_version TEXT,
    tag_label TEXT,
    tag_catalog_number TEXT,
    tag_bpm INTEGER,
    tag_key TEXT,
    tag_track_total INTEGER,
    tag_duration_ms INTEGER,
    tag_year INTEGER,
    tag_track_no INTEGER,
    tag_disc INTEGER,
    tag_genre TEXT,
    tag_isrc TEXT,
    tag_release_date TEXT,
    tag_publish_date TEXT,
    tag_url TEXT,
    tag_release_id TEXT,
    tag_track_id TEXT,
    tag_meta_tagged_date TEXT,
    lyrics_unsynced TEXT,
    lyrics_synced TEXT,
    metadata_json TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);


CREATE TABLE IF NOT EXISTS artist_source (
    artist_id BIGINT NOT NULL REFERENCES artist(id) ON DELETE CASCADE,
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    url TEXT,
    data TEXT,
    PRIMARY KEY (artist_id, source),
    UNIQUE (source, source_id)
);

CREATE TABLE IF NOT EXISTS artist_metadata_policy (
    artist_id BIGINT NOT NULL REFERENCES artist(id) ON DELETE CASCADE,
    sync_blocked INTEGER NOT NULL DEFAULT 0,
    ocr_text_art_blocking_enabled INTEGER NOT NULL DEFAULT 1,
    selected_targets_json TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (artist_id)
);

CREATE TABLE IF NOT EXISTS artist_artwork_cache (
    artist_id BIGINT NOT NULL REFERENCES artist(id) ON DELETE CASCADE,
    role TEXT NOT NULL,
    identity TEXT NOT NULL,
    source TEXT,
    original_url TEXT,
    local_path TEXT,
    content_hash TEXT,
    width INTEGER,
    height INTEGER,
    ocr_status TEXT,
    detected_text TEXT,
    text_art_blocked INTEGER NOT NULL DEFAULT 0,
    user_blocked INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_seen_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (artist_id, role, identity)
);

CREATE INDEX IF NOT EXISTS idx_artist_artwork_cache_artist_role
ON artist_artwork_cache (artist_id, role);

CREATE TABLE IF NOT EXISTS artist_biography_cache (
    artist_id BIGINT NOT NULL REFERENCES artist(id) ON DELETE CASCADE,
    source TEXT NOT NULL,
    biography TEXT,
    language TEXT,
    selected INTEGER NOT NULL DEFAULT 0,
    fetched_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (artist_id, source)
);

CREATE TABLE IF NOT EXISTS artist_server_sync_state (
    artist_id BIGINT NOT NULL REFERENCES artist(id) ON DELETE CASCADE,
    server TEXT NOT NULL,
    last_cache_refresh_utc TEXT,
    last_sync_utc TEXT,
    last_avatar_hash TEXT,
    last_background_hash TEXT,
    last_biography_hash TEXT,
    avatar_rotation_index INTEGER NOT NULL DEFAULT 0,
    background_rotation_index INTEGER NOT NULL DEFAULT 0,
    last_result TEXT,
    last_error TEXT,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (artist_id, server)
);

CREATE INDEX IF NOT EXISTS idx_artist_server_sync_state_artist
ON artist_server_sync_state (artist_id);

CREATE TABLE IF NOT EXISTS album_source (
    album_id BIGINT NOT NULL REFERENCES album(id) ON DELETE CASCADE,
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    url TEXT,
    data TEXT,
    PRIMARY KEY (album_id, source),
    UNIQUE (source, source_id)
);

CREATE TABLE IF NOT EXISTS track_source (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    url TEXT,
    data TEXT,
    PRIMARY KEY (track_id, source),
    UNIQUE (source, source_id)
);

CREATE TABLE IF NOT EXISTS library (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS folder (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    root_path TEXT NOT NULL UNIQUE,
    display_name TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT TRUE,
    library_id BIGINT REFERENCES library(id) ON DELETE SET NULL,
    auto_tag_profile_id TEXT,
    auto_tag_enabled INTEGER NOT NULL DEFAULT TRUE,
    desired_quality INTEGER NOT NULL DEFAULT 27,
    desired_quality_value TEXT NOT NULL DEFAULT '27',
    convert_enabled INTEGER NOT NULL DEFAULT 0,
    convert_format TEXT,
    convert_bitrate TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS folder_alias (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    folder_id BIGINT NOT NULL REFERENCES folder(id) ON DELETE CASCADE,
    alias_name TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (folder_id, alias_name)
);

CREATE TABLE IF NOT EXISTS audio_file (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    path TEXT NOT NULL UNIQUE,
    relative_path TEXT,
    folder_id BIGINT NOT NULL REFERENCES folder(id) ON DELETE CASCADE,
    size BIGINT,
    mtime TEXT,
    content_hash TEXT,
    duration_ms INTEGER,
    bitrate INTEGER,
    codec TEXT,
    bitrate_kbps INTEGER,
    extension TEXT,
    sample_rate_hz INTEGER,
    bits_per_sample INTEGER,
    channels INTEGER,
    quality_rank INTEGER,
    audio_variant TEXT,
    queue_order INTEGER,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);


CREATE INDEX IF NOT EXISTS idx_audio_file_content_hash ON audio_file (content_hash);
CREATE UNIQUE INDEX IF NOT EXISTS idx_audio_file_folder_relative ON audio_file (folder_id, relative_path);
CREATE INDEX IF NOT EXISTS idx_artist_name_nocase ON artist (name COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS idx_album_artist_id ON album (artist_id);
CREATE INDEX IF NOT EXISTS idx_track_album_id ON track (album_id);

CREATE TABLE IF NOT EXISTS track_local (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    audio_file_id BIGINT NOT NULL REFERENCES audio_file(id) ON DELETE CASCADE,
    PRIMARY KEY (track_id, audio_file_id)
);
CREATE INDEX IF NOT EXISTS idx_track_local_audio_file_id ON track_local (audio_file_id);

CREATE TABLE IF NOT EXISTS track_genre (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    value TEXT NOT NULL,
    PRIMARY KEY (track_id, value)
);

CREATE TABLE IF NOT EXISTS track_style (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    value TEXT NOT NULL,
    PRIMARY KEY (track_id, value)
);

CREATE TABLE IF NOT EXISTS track_mood (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    value TEXT NOT NULL,
    PRIMARY KEY (track_id, value)
);

CREATE TABLE IF NOT EXISTS track_remixer (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    value TEXT NOT NULL,
    PRIMARY KEY (track_id, value)
);

CREATE TABLE IF NOT EXISTS track_other_tag (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    tag_key TEXT NOT NULL,
    tag_value TEXT NOT NULL,
    PRIMARY KEY (track_id, tag_key, tag_value)
);

CREATE TABLE IF NOT EXISTS album_local (
    album_id BIGINT NOT NULL REFERENCES album(id) ON DELETE CASCADE,
    folder_id BIGINT NOT NULL REFERENCES folder(id) ON DELETE CASCADE,
    PRIMARY KEY (album_id, folder_id)
);

CREATE TABLE IF NOT EXISTS match_candidate (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    entity_type TEXT NOT NULL,
    local_entity_id BIGINT,
    source TEXT,
    source_id TEXT,
    score REAL NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    payload TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS scan_job (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    folder_id BIGINT REFERENCES folder(id) ON DELETE SET NULL,
    status TEXT NOT NULL,
    started_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    finished_at TEXT,
    stats TEXT,
    error TEXT
);

CREATE TABLE IF NOT EXISTS spotizerr_task (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    type TEXT NOT NULL,
    status TEXT NOT NULL,
    source_id TEXT,
    payload TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (source_id)
);

CREATE TABLE IF NOT EXISTS spotizerr_task_item (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    task_id BIGINT NOT NULL REFERENCES spotizerr_task(id) ON DELETE CASCADE,
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    status TEXT NOT NULL,
    payload TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (task_id, source, source_id)
);

CREATE TABLE IF NOT EXISTS library_settings (
    id SMALLINT PRIMARY KEY DEFAULT 1,
    live_preview_ingest INTEGER NOT NULL DEFAULT FALSE,
    enable_signal_analysis INTEGER NOT NULL DEFAULT FALSE,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS library_scan_state (
    id SMALLINT PRIMARY KEY DEFAULT 1,
    last_run_utc TEXT,
    artist_count INTEGER NOT NULL DEFAULT 0,
    album_count INTEGER NOT NULL DEFAULT 0,
    track_count INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS library_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp_utc TEXT NOT NULL,
    level TEXT NOT NULL,
    message TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_library_log_timestamp ON library_log (timestamp_utc);

CREATE TABLE IF NOT EXISTS artist_page_cache (
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    fetched_utc TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (source, source_id)
);

CREATE INDEX IF NOT EXISTS idx_artist_page_cache_fetched ON artist_page_cache (fetched_utc);

CREATE TABLE IF NOT EXISTS artist_page_genre (
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    genre TEXT NOT NULL,
    PRIMARY KEY (source, source_id, genre)
);

CREATE INDEX IF NOT EXISTS idx_artist_page_genre ON artist_page_genre (source, source_id);

CREATE TABLE IF NOT EXISTS spotify_metadata_cache (
    type TEXT NOT NULL,
    source_id TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    fetched_utc TEXT NOT NULL,
    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (type, source_id)
);

CREATE INDEX IF NOT EXISTS idx_spotify_metadata_cache_fetched ON spotify_metadata_cache (fetched_utc);

CREATE TABLE IF NOT EXISTS artist_watchlist (
    artist_id BIGINT NOT NULL UNIQUE,
    artist_name TEXT NOT NULL,
    spotify_id TEXT,
    deezer_id TEXT,
    destination_folder_id BIGINT,
    album_groups_json TEXT,
    top_songs_enabled INTEGER,
    latest_releases_only INTEGER,
    preferred_engine TEXT,
    routing_rules_json TEXT,
    atmos_destination_folder_id BIGINT,
    download_variant_mode TEXT,
    top_songs_sync_mode TEXT,
    download_discography_enabled INTEGER,
    ignore_rules_json TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS artist_watch_state (
    artist_id BIGINT NOT NULL,
    spotify_id TEXT,
    batch_next_offset INTEGER,
    last_checked_utc TEXT,
    last_run_status TEXT,
    last_run_message TEXT,
    next_attempt_utc TEXT,
    consecutive_failures INTEGER,
    current_phase TEXT,
    heartbeat_utc TEXT,
    deadline_utc TEXT,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (artist_id)
);

CREATE TABLE IF NOT EXISTS artist_watch_album (
    artist_id BIGINT NOT NULL,
    source TEXT NOT NULL,
    album_source_id TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (artist_id, source, album_source_id)
);

CREATE INDEX IF NOT EXISTS idx_artist_watch_album_artist
    ON artist_watch_album (artist_id, source);

CREATE TABLE IF NOT EXISTS playlist_watchlist (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    name TEXT NOT NULL,
    image_url TEXT,
    description TEXT,
    track_count INTEGER,
    sync_priority INTEGER,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (source, source_id)
);

CREATE TABLE IF NOT EXISTS playlist_watch_preferences (
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    destination_folder_id BIGINT,
    atmos_destination_folder_id BIGINT,
    service TEXT,
    sync_targets_json TEXT,
    preferred_engine TEXT,
    download_engine_order_json TEXT,
    download_variant_mode TEXT,
    sync_mode TEXT,
    update_artwork INTEGER NOT NULL DEFAULT 1,
    reuse_saved_artwork INTEGER NOT NULL DEFAULT 0,
    plex_playlist_id TEXT,
    jellyfin_playlist_id TEXT,
    navidrome_playlist_id TEXT,
    routing_rules_json TEXT,
    ignore_rules_json TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (source, source_id)
);

CREATE TABLE IF NOT EXISTS playlist_watch_state (
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    snapshot_id TEXT,
    track_count INTEGER,
    batch_next_offset INTEGER,
    batch_processing_snapshot_id TEXT,
    last_checked_utc TEXT,
    last_run_status TEXT,
    last_run_message TEXT,
    next_attempt_utc TEXT,
    consecutive_failures INTEGER,
    current_phase TEXT,
    current_track_index INTEGER,
    current_track_total INTEGER,
    heartbeat_utc TEXT,
    deadline_utc TEXT,
    ignored_blocked_track_count INTEGER,
    rerouted_track_count INTEGER,
    presentation_updated_at TEXT,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (source, source_id)
);

CREATE TABLE IF NOT EXISTS watchlist_scheduler_state (
    watch_type TEXT NOT NULL PRIMARY KEY,
    active_source TEXT,
    active_source_id TEXT,
    active_started_utc TEXT,
    last_progress_utc TEXT,
    zero_queue_streak INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS watchlist_source_circuit_state (
    watch_type TEXT NOT NULL,
    source TEXT NOT NULL,
    is_open INTEGER NOT NULL DEFAULT 0,
    open_until_utc TEXT,
    reason TEXT,
    fingerprint TEXT,
    failure_count INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (watch_type, source)
);

CREATE TABLE IF NOT EXISTS playlist_track_candidate_cache (
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    snapshot_id TEXT,
    candidates_json TEXT NOT NULL,
    schema_version INTEGER NOT NULL DEFAULT 0,
    identity_revision TEXT,
    provider_readiness_revision TEXT,
    is_complete INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (source, source_id)
);

CREATE TABLE IF NOT EXISTS boomplay_deezer_track_mapping (
    boomplay_track_id TEXT NOT NULL PRIMARY KEY,
    deezer_track_id TEXT,
    isrc TEXT,
    title TEXT NOT NULL DEFAULT '',
    artist TEXT NOT NULL DEFAULT '',
    album TEXT NOT NULL DEFAULT '',
    cover_url TEXT,
    duration_ms INTEGER,
    source_fingerprint TEXT NOT NULL,
    matcher_version TEXT NOT NULL,
    status TEXT NOT NULL,
    last_error TEXT,
    next_retry_utc TEXT,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_boomplay_deezer_track_mapping_deezer
    ON boomplay_deezer_track_mapping (deezer_track_id);

CREATE INDEX IF NOT EXISTS idx_boomplay_deezer_track_mapping_retry
    ON boomplay_deezer_track_mapping (status, next_retry_utc);

CREATE TABLE IF NOT EXISTS recommendation_rejection (
    library_id BIGINT NOT NULL,
    folder_id BIGINT,
    station_id TEXT NOT NULL,
    track_source_id TEXT NOT NULL,
    isrc TEXT,
    title TEXT,
    artist TEXT,
    rejected_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (station_id, track_source_id)
);

CREATE INDEX IF NOT EXISTS idx_recommendation_rejection_library
    ON recommendation_rejection (library_id, folder_id, station_id);

CREATE INDEX IF NOT EXISTS idx_recommendation_rejection_rejected
    ON recommendation_rejection (rejected_at_utc);

CREATE TABLE IF NOT EXISTS recommendation_generation_state (
    library_id BIGINT NOT NULL,
    folder_id BIGINT NOT NULL,
    station_id TEXT NOT NULL,
    target_day TEXT NOT NULL,
    status TEXT NOT NULL,
    reason_code TEXT,
    started_at_utc TEXT,
    completed_at_utc TEXT,
    last_error TEXT,
    attempt_count INTEGER NOT NULL DEFAULT 0,
    updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (library_id, folder_id, target_day)
);

CREATE INDEX IF NOT EXISTS idx_recommendation_generation_state_status
    ON recommendation_generation_state (status, target_day, updated_at_utc);

CREATE TABLE IF NOT EXISTS playlist_watch_track (
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    track_source_id TEXT NOT NULL,
    isrc TEXT,
    status TEXT NOT NULL DEFAULT 'queued',
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    unavailable_reason TEXT,
    unavailable_since_utc TEXT,
    unavailable_last_checked_utc TEXT,
    unavailable_next_retry_utc TEXT,
    unavailable_settings_fingerprint TEXT,
    local_track_id BIGINT,
    identity_status TEXT,
    identity_reason TEXT,
    redirect_track_source_id TEXT,
    redirect_reason TEXT,
    verified_at_utc TEXT,
    PRIMARY KEY (source, source_id, track_source_id)
);

CREATE INDEX IF NOT EXISTS idx_playlist_watch_track_playlist
    ON playlist_watch_track (source, source_id);

CREATE TABLE IF NOT EXISTS playlist_watch_target_membership (
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    track_source_id TEXT NOT NULL,
    target_service TEXT NOT NULL,
    target_playlist_id TEXT NOT NULL,
    target_item_id TEXT,
    local_track_id BIGINT,
    sync_status TEXT NOT NULL DEFAULT 'waiting_for_target',
    verified_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (source, source_id, track_source_id, target_service)
);

CREATE INDEX IF NOT EXISTS idx_playlist_watch_target_membership_target
    ON playlist_watch_target_membership (target_service, target_playlist_id);

CREATE INDEX IF NOT EXISTS idx_playlist_watch_target_membership_track
    ON playlist_watch_target_membership (source, source_id, track_source_id);

CREATE TABLE IF NOT EXISTS playlist_watch_download_claim (
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    track_source_id TEXT NOT NULL,
    queue_uuid TEXT NOT NULL,
    destination_folder_id BIGINT,
    status TEXT NOT NULL DEFAULT 'pending',
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (source, source_id, track_source_id, queue_uuid)
);

CREATE INDEX IF NOT EXISTS idx_playlist_watch_download_claim_queue
    ON playlist_watch_download_claim (queue_uuid, status);

CREATE INDEX IF NOT EXISTS idx_playlist_watch_download_claim_status_updated
    ON playlist_watch_download_claim (status, updated_at);

CREATE TABLE IF NOT EXISTS watchlist_sync_job (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source TEXT NOT NULL,
    playlist_id TEXT NOT NULL,
    track_id TEXT NOT NULL,
    target_service TEXT NOT NULL,
    queue_uuid TEXT,
    destination_folder_id BIGINT,
    final_file_paths_json TEXT,
    attempt_count INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'pending',
    lease_owner TEXT,
    lease_until_utc TEXT,
    next_attempt_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_error TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (source, playlist_id, track_id, target_service)
);

CREATE TABLE IF NOT EXISTS watchlist_finalization_outbox (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    queue_uuid TEXT NOT NULL UNIQUE,
    payload_json TEXT,
    final_file_paths_json TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    attempt_count INTEGER NOT NULL DEFAULT 0,
    next_attempt_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    lease_owner TEXT,
    lease_until_utc TEXT,
    last_error TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_watchlist_finalization_outbox_due
    ON watchlist_finalization_outbox (status, next_attempt_utc, lease_until_utc, id);

CREATE INDEX IF NOT EXISTS idx_watchlist_sync_job_due
    ON watchlist_sync_job (next_attempt_utc, id);

CREATE TABLE IF NOT EXISTS watchlist_reconciliation_request (
    kind TEXT NOT NULL,
    source TEXT NOT NULL DEFAULT '',
    identifier TEXT NOT NULL DEFAULT '',
    status TEXT NOT NULL DEFAULT 'pending',
    attempt_count INTEGER NOT NULL DEFAULT 0,
    next_attempt_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    lease_owner TEXT,
    lease_until_utc TEXT,
    last_error TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (kind, source, identifier)
);

CREATE INDEX IF NOT EXISTS idx_watchlist_reconciliation_request_updated
    ON watchlist_reconciliation_request (updated_at, kind, source, identifier);

CREATE TABLE IF NOT EXISTS playlist_watch_ignore (
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    track_source_id TEXT NOT NULL,
    isrc TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (source, source_id, track_source_id)
);

CREATE INDEX IF NOT EXISTS idx_playlist_watch_ignore_playlist
    ON playlist_watch_ignore (source, source_id);

CREATE TABLE IF NOT EXISTS download_blocklist (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    field TEXT NOT NULL,
    value TEXT NOT NULL,
    normalized_value TEXT NOT NULL,
    is_enabled INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (field, normalized_value)
);

CREATE INDEX IF NOT EXISTS idx_download_blocklist_field
    ON download_blocklist (field, is_enabled);

CREATE INDEX IF NOT EXISTS idx_download_blocklist_normalized
    ON download_blocklist (normalized_value, is_enabled);

CREATE TABLE IF NOT EXISTS manual_unavailable_track (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    queue_uuid TEXT NOT NULL UNIQUE,
    title TEXT NOT NULL,
    artist TEXT NOT NULL,
    album TEXT,
    album_artist TEXT,
    isrc TEXT,
    engine TEXT,
    source_service TEXT,
    source_url TEXT,
    deezer_track_id TEXT,
    spotify_track_id TEXT,
    apple_track_id TEXT,
    qobuz_track_id TEXT,
    tidal_track_id TEXT,
    amazon_track_id TEXT,
    destination_folder_id INTEGER,
    expected_final_path TEXT,
    quality TEXT,
    content_type TEXT,
    reason TEXT,
    payload_json TEXT,
    first_unavailable_at_utc TEXT NOT NULL,
    next_retry_at_utc TEXT NOT NULL,
    added_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_manual_unavailable_track_added
    ON manual_unavailable_track (added_at_utc DESC);

CREATE INDEX IF NOT EXISTS idx_manual_unavailable_track_destination
    ON manual_unavailable_track (destination_folder_id);

CREATE TABLE IF NOT EXISTS track_shazam_cache (
    track_id BIGINT PRIMARY KEY REFERENCES track(id) ON DELETE CASCADE,
    shazam_track_id TEXT,
    title TEXT,
    artist TEXT,
    isrc TEXT,
    status TEXT NOT NULL DEFAULT 'pending',
    related_tracks_json TEXT,
    scanned_at_utc TEXT,
    error TEXT,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_track_shazam_cache_status
    ON track_shazam_cache (status);

CREATE INDEX IF NOT EXISTS idx_track_shazam_cache_scanned
    ON track_shazam_cache (scanned_at_utc);

CREATE TABLE IF NOT EXISTS watchlist_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source TEXT NOT NULL,
    watch_type TEXT NOT NULL,
    source_id TEXT NOT NULL,
    name TEXT NOT NULL,
    collection_type TEXT NOT NULL,
    track_count INTEGER NOT NULL,
    status TEXT NOT NULL,
    artist_name TEXT,
    item_key TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_watchlist_history_created
    ON watchlist_history (created_at);

CREATE TABLE IF NOT EXISTS download_artist (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    deezer_id TEXT,
    metadata_json TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS download_album (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    download_artist_id BIGINT REFERENCES download_artist(id) ON DELETE SET NULL,
    deezer_id TEXT,
    metadata_json TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS download_track (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    download_album_id BIGINT REFERENCES download_album(id) ON DELETE SET NULL,
    deezer_id TEXT,
    lyrics_status TEXT,
    file_extension TEXT,
    bitrate_kbps INTEGER,
    metadata_json TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS plex_user (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    username TEXT,
    plex_user_id TEXT,
    plex_server_url TEXT,
    plex_machine_identifier TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (plex_user_id, plex_server_url)
);

CREATE TABLE IF NOT EXISTS play_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    library_id BIGINT REFERENCES library(id) ON DELETE SET NULL,
    folder_id BIGINT REFERENCES folder(id) ON DELETE SET NULL,
    plex_user_id BIGINT NOT NULL REFERENCES plex_user(id) ON DELETE CASCADE,
    track_id BIGINT REFERENCES track(id) ON DELETE SET NULL,
    plex_track_key TEXT,
    plex_rating_key TEXT,
    event_key TEXT NOT NULL,
    played_at_utc TEXT NOT NULL,
    play_duration_ms INTEGER,
    source TEXT NOT NULL DEFAULT 'plex',
    remote_library_id TEXT,
    metadata_json TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (plex_user_id, source, event_key)
);

CREATE INDEX IF NOT EXISTS idx_play_history_library ON play_history (library_id);
CREATE INDEX IF NOT EXISTS idx_play_history_user_source_time
    ON play_history (plex_user_id, source, played_at_utc DESC);
CREATE INDEX IF NOT EXISTS idx_play_history_user_library_time
    ON play_history (plex_user_id, library_id, played_at_utc DESC);
CREATE INDEX IF NOT EXISTS idx_play_history_user_library_track_time
    ON play_history (plex_user_id, library_id, track_id, played_at_utc DESC);

CREATE TABLE IF NOT EXISTS background_job_state (
    job_key TEXT NOT NULL PRIMARY KEY,
    status TEXT NOT NULL DEFAULT 'idle',
    last_started_at_utc TEXT,
    last_finished_at_utc TEXT,
    next_due_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_background_job_state_due
    ON background_job_state (status, next_due_at_utc);

CREATE TABLE IF NOT EXISTS mix_cache (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    mix_id TEXT NOT NULL,
    library_id BIGINT REFERENCES library(id) ON DELETE SET NULL,
    plex_user_id BIGINT REFERENCES plex_user(id) ON DELETE SET NULL,
    name TEXT NOT NULL,
    description TEXT,
    track_count INTEGER NOT NULL DEFAULT 0,
    cover_urls_json TEXT,
    mix_type TEXT,
    generated_at_utc TEXT NOT NULL,
    expires_at_utc TEXT NOT NULL,
    metadata_json TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (mix_id, library_id, plex_user_id)
);

CREATE TABLE IF NOT EXISTS mix_item (
    mix_cache_id BIGINT NOT NULL REFERENCES mix_cache(id) ON DELETE CASCADE,
    position INTEGER NOT NULL,
    track_id BIGINT REFERENCES track(id) ON DELETE SET NULL,
    plex_track_key TEXT,
    metadata_json TEXT,
    PRIMARY KEY (mix_cache_id, position)
);

CREATE TABLE IF NOT EXISTS mix_sync (
    mix_cache_id BIGINT NOT NULL REFERENCES mix_cache(id) ON DELETE CASCADE,
    target TEXT NOT NULL,
    playlist_id TEXT,
    last_synced_utc TEXT,
    status TEXT NOT NULL DEFAULT 'pending',
    message TEXT,
    metadata_json TEXT,
    PRIMARY KEY (mix_cache_id, target)
);

CREATE TABLE IF NOT EXISTS track_analysis (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    library_id BIGINT REFERENCES library(id) ON DELETE SET NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    energy REAL,
    rms REAL,
    zero_crossing REAL,
    spectral_centroid REAL,
    bpm REAL,
    beats_count INTEGER,
    key TEXT,
    key_scale TEXT,
    key_strength REAL,
    loudness REAL,
    dynamic_range REAL,
    danceability REAL,
    instrumentalness REAL,
    acousticness REAL,
    speechiness REAL,
    danceability_ml REAL,
    valence REAL,
    arousal REAL,
    analyzed_at_utc TEXT,
    error TEXT,
    analysis_mode TEXT,
    analysis_version TEXT,
    mood_tags TEXT,
    mood_happy REAL,
    mood_sad REAL,
    mood_relaxed REAL,
    mood_aggressive REAL,
    mood_party REAL,
    mood_acoustic REAL,
    mood_electronic REAL,
    essentia_genres TEXT,
    lastfm_tags TEXT,
    metadata_json TEXT,
    PRIMARY KEY (track_id)
);

CREATE INDEX IF NOT EXISTS idx_track_analysis_library ON track_analysis (library_id);

CREATE TABLE IF NOT EXISTS mood_bucket (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    mood TEXT NOT NULL,
    score REAL NOT NULL DEFAULT 0,
    updated_at_utc TEXT,
    PRIMARY KEY (track_id, mood)
);

CREATE INDEX IF NOT EXISTS idx_mood_bucket_mood_score ON mood_bucket (mood, score DESC);

CREATE TABLE IF NOT EXISTS track_plex_metadata (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    plex_rating_key TEXT,
    user_rating INTEGER,
    genres_json TEXT,
    moods_json TEXT,
    updated_at_utc TEXT,
    PRIMARY KEY (track_id)
);

CREATE TABLE IF NOT EXISTS download_task (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    queue_uuid TEXT UNIQUE,
    engine TEXT NOT NULL DEFAULT 'deezer',
    artist_name TEXT NOT NULL,
    track_title TEXT NOT NULL,
    isrc TEXT,
    deezer_track_id TEXT,
    deezer_album_id TEXT,
    deezer_artist_id TEXT,
    spotify_track_id TEXT,
    spotify_album_id TEXT,
    spotify_artist_id TEXT,
    apple_track_id TEXT,
    apple_album_id TEXT,
    apple_artist_id TEXT,
    duration_ms INTEGER,
    destination_folder_id INTEGER,
    move_status TEXT,
    enrichment_status TEXT,
    quality_rank INTEGER,
    lyrics_status TEXT,
    file_extension TEXT,
    bitrate_kbps INTEGER,
    content_type TEXT,
    status TEXT NOT NULL DEFAULT 'queued',
    payload TEXT,
    final_destinations_json TEXT,
    progress REAL,
    downloaded INTEGER,
    failed INTEGER,
    error TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);


CREATE UNIQUE INDEX IF NOT EXISTS idx_download_task_queue_uuid ON download_task (queue_uuid);
CREATE INDEX IF NOT EXISTS idx_download_task_status ON download_task (status);
CREATE INDEX IF NOT EXISTS idx_download_task_created_at ON download_task (created_at);

CREATE TABLE IF NOT EXISTS quality_scan_automation_settings (
    id SMALLINT PRIMARY KEY DEFAULT 1,
    enabled INTEGER NOT NULL DEFAULT 0,
    interval_minutes INTEGER NOT NULL DEFAULT 360,
    scope TEXT NOT NULL DEFAULT 'watchlist',
    folder_id BIGINT REFERENCES folder(id) ON DELETE SET NULL,
    queue_atmos_alternatives INTEGER NOT NULL DEFAULT 0,
    cooldown_minutes INTEGER NOT NULL DEFAULT 1440,
    last_started_utc TEXT,
    last_finished_utc TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS quality_scan_run (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    trigger TEXT NOT NULL,
    status TEXT NOT NULL,
    scope TEXT NOT NULL,
    folder_id BIGINT REFERENCES folder(id) ON DELETE SET NULL,
    queue_atmos_alternatives INTEGER NOT NULL DEFAULT 0,
    total_tracks INTEGER NOT NULL DEFAULT 0,
    processed_tracks INTEGER NOT NULL DEFAULT 0,
    quality_met INTEGER NOT NULL DEFAULT 0,
    low_quality INTEGER NOT NULL DEFAULT 0,
    upgrades_queued INTEGER NOT NULL DEFAULT 0,
    atmos_queued INTEGER NOT NULL DEFAULT 0,
    duplicate_skipped INTEGER NOT NULL DEFAULT 0,
    match_missed INTEGER NOT NULL DEFAULT 0,
    phase TEXT,
    error_message TEXT,
    started_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    finished_at_utc TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_quality_scan_run_started ON quality_scan_run (started_at_utc);
CREATE INDEX IF NOT EXISTS idx_quality_scan_run_trigger ON quality_scan_run (trigger);

CREATE TABLE IF NOT EXISTS quality_scan_track_state (
    track_id BIGINT PRIMARY KEY REFERENCES track(id) ON DELETE CASCADE,
    last_run_id BIGINT REFERENCES quality_scan_run(id) ON DELETE SET NULL,
    last_scanned_utc TEXT,
    best_quality_rank INTEGER,
    desired_quality_rank INTEGER,
    last_action TEXT,
    last_upgrade_queued_utc TEXT,
    last_atmos_queued_utc TEXT,
    last_error TEXT,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS quality_scan_action_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id BIGINT REFERENCES quality_scan_run(id) ON DELETE CASCADE,
    track_id BIGINT REFERENCES track(id) ON DELETE CASCADE,
    action_type TEXT NOT NULL,
    source TEXT,
    quality TEXT,
    content_type TEXT,
    destination_folder_id BIGINT REFERENCES folder(id) ON DELETE SET NULL,
    queue_uuid TEXT,
    message TEXT,
    created_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_quality_scan_action_run ON quality_scan_action_log (run_id);
CREATE INDEX IF NOT EXISTS idx_quality_scan_action_track ON quality_scan_action_log (track_id);
