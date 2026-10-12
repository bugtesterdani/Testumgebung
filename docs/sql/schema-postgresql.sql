-- Testumgebung – Datenbankschema für PostgreSQL
-- Erzeugt aus SqlDialect; die Anwendung migriert automatisch (tu_schema_version).

CREATE TABLE IF NOT EXISTS tu_schema_version (version BIGINT NOT NULL PRIMARY KEY, applied_utc VARCHAR(64) NOT NULL);

-- Version 1
CREATE TABLE tu_definitions (
    definition_id VARCHAR(128) NOT NULL PRIMARY KEY,
    test_name TEXT NOT NULL,
    test_version VARCHAR(64) NOT NULL,
    structure_json TEXT NOT NULL,
    created_utc BIGINT NOT NULL
);

CREATE TABLE tu_runs (
    run_id VARCHAR(128) NOT NULL PRIMARY KEY,
    station_run_id TEXT,
    station TEXT,
    dut_id VARCHAR(128) NOT NULL,
    definition_id VARCHAR(128) NOT NULL,
    test_name TEXT NOT NULL,
    test_version VARCHAR(64) NOT NULL,
    started_utc BIGINT NOT NULL,
    started_text VARCHAR(64) NOT NULL,
    ended_utc BIGINT NOT NULL,
    ended_text VARCHAR(64) NOT NULL,
    rating VARCHAR(64) NOT NULL,
    werror BIGINT NOT NULL,
    development_run BIGINT NOT NULL,
    application TEXT,
    stored_utc BIGINT NOT NULL,
    CONSTRAINT fk_tu_runs_definition FOREIGN KEY (definition_id) REFERENCES tu_definitions (definition_id)
);

CREATE INDEX ix_tu_runs_dut ON tu_runs (dut_id);

CREATE INDEX ix_tu_runs_started ON tu_runs (started_utc);

CREATE TABLE tu_step_executions (
    run_id VARCHAR(128) NOT NULL,
    seq BIGINT NOT NULL,
    kind VARCHAR(64) NOT NULL,
    node_id VARCHAR(128),
    path TEXT,
    iteration BIGINT NOT NULL,
    time_utc BIGINT NOT NULL,
    time_text VARCHAR(64) NOT NULL,
    rating VARCHAR(64),
    message TEXT,
    cancelled BIGINT NOT NULL,
    PRIMARY KEY (run_id, seq),
    CONSTRAINT fk_tu_steps_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
);

CREATE TABLE tu_measurements (
    run_id VARCHAR(128) NOT NULL,
    seq BIGINT NOT NULL,
    node_id VARCHAR(128),
    value_type VARCHAR(64) NOT NULL,
    value_double DOUBLE PRECISION,
    value_int BIGINT,
    value_text TEXT,
    unit VARCHAR(64),
    low_limit DOUBLE PRECISION,
    high_limit DOUBLE PRECISION,
    rating VARCHAR(64),
    PRIMARY KEY (run_id, seq),
    CONSTRAINT fk_tu_meas_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
);

CREATE TABLE tu_waveform_chunks (
    run_id VARCHAR(128) NOT NULL,
    waveform_no BIGINT NOT NULL,
    part BIGINT NOT NULL,
    parts BIGINT NOT NULL,
    waveform_id TEXT NOT NULL,
    event_seq BIGINT NOT NULL,
    node_id VARCHAR(128),
    channel TEXT NOT NULL,
    unit VARCHAR(64),
    sample_rate DOUBLE PRECISION NOT NULL,
    start_utc BIGINT NOT NULL,
    sample_offset BIGINT NOT NULL,
    sample_count BIGINT NOT NULL,
    total_samples BIGINT NOT NULL,
    sample_type VARCHAR(64) NOT NULL,
    samples BYTEA NOT NULL,
    times BYTEA,
    quality TEXT,
    sync_group TEXT,
    PRIMARY KEY (run_id, waveform_no, part),
    CONSTRAINT fk_tu_wave_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
);

CREATE TABLE tu_operator_inputs (
    run_id VARCHAR(128) NOT NULL,
    seq BIGINT NOT NULL,
    node_id VARCHAR(128),
    prompt TEXT,
    value_type VARCHAR(64),
    value_text TEXT,
    time_utc BIGINT NOT NULL,
    PRIMARY KEY (run_id, seq),
    CONSTRAINT fk_tu_input_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
);

CREATE TABLE tu_development_events (
    run_id VARCHAR(128) NOT NULL,
    seq BIGINT NOT NULL,
    user_name TEXT,
    details TEXT,
    time_utc BIGINT NOT NULL,
    PRIMARY KEY (run_id, seq),
    CONSTRAINT fk_tu_dev_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
);

INSERT INTO tu_schema_version (version, applied_utc) VALUES (1, 'manuell');

-- Version 2
CREATE TABLE tu_releases (
    release_id VARCHAR(128) NOT NULL PRIMARY KEY,
    project_name VARCHAR(128) NOT NULL,
    version VARCHAR(64) NOT NULL,
    state VARCHAR(64) NOT NULL,
    package BYTEA NOT NULL,
    package_sha256 VARCHAR(64) NOT NULL,
    package_size BIGINT NOT NULL,
    created_by TEXT NOT NULL,
    created_utc BIGINT NOT NULL,
    reviewed_by TEXT,
    reviewed_utc BIGINT,
    released_by TEXT,
    released_utc BIGINT,
    archived_utc BIGINT,
    review_report TEXT,
    CONSTRAINT uq_tu_releases_version UNIQUE (project_name, version)
);

CREATE TABLE tu_audit_log (
    seq BIGINT NOT NULL PRIMARY KEY,
    time_utc BIGINT NOT NULL,
    time_text VARCHAR(64) NOT NULL,
    user_name TEXT NOT NULL,
    action TEXT NOT NULL,
    details TEXT,
    release_id VARCHAR(128),
    prev_hash VARCHAR(64) NOT NULL,
    entry_hash VARCHAR(64) NOT NULL
);

INSERT INTO tu_schema_version (version, applied_utc) VALUES (2, 'manuell');

-- Version 3
ALTER TABLE tu_runs ADD order_id VARCHAR(128);

CREATE INDEX ix_tu_runs_order ON tu_runs (order_id);

INSERT INTO tu_schema_version (version, applied_utc) VALUES (3, 'manuell');

