-- Testumgebung – Datenbankschema für SQLite
-- Erzeugt aus SqlDialect; die Anwendung migriert automatisch (tu_schema_version).

CREATE TABLE IF NOT EXISTS tu_schema_version (version INTEGER NOT NULL PRIMARY KEY, applied_utc TEXT NOT NULL);

-- Version 1
CREATE TABLE tu_definitions (
    definition_id TEXT NOT NULL PRIMARY KEY,
    test_name TEXT NOT NULL,
    test_version TEXT NOT NULL,
    structure_json TEXT NOT NULL,
    created_utc INTEGER NOT NULL
);

CREATE TABLE tu_runs (
    run_id TEXT NOT NULL PRIMARY KEY,
    station_run_id TEXT,
    station TEXT,
    dut_id TEXT NOT NULL,
    definition_id TEXT NOT NULL,
    test_name TEXT NOT NULL,
    test_version TEXT NOT NULL,
    started_utc INTEGER NOT NULL,
    started_text TEXT NOT NULL,
    ended_utc INTEGER NOT NULL,
    ended_text TEXT NOT NULL,
    rating TEXT NOT NULL,
    werror INTEGER NOT NULL,
    development_run INTEGER NOT NULL,
    application TEXT,
    stored_utc INTEGER NOT NULL,
    CONSTRAINT fk_tu_runs_definition FOREIGN KEY (definition_id) REFERENCES tu_definitions (definition_id)
);

CREATE INDEX ix_tu_runs_dut ON tu_runs (dut_id);

CREATE INDEX ix_tu_runs_started ON tu_runs (started_utc);

CREATE TABLE tu_step_executions (
    run_id TEXT NOT NULL,
    seq INTEGER NOT NULL,
    kind TEXT NOT NULL,
    node_id TEXT,
    path TEXT,
    iteration INTEGER NOT NULL,
    time_utc INTEGER NOT NULL,
    time_text TEXT NOT NULL,
    rating TEXT,
    message TEXT,
    cancelled INTEGER NOT NULL,
    PRIMARY KEY (run_id, seq),
    CONSTRAINT fk_tu_steps_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
);

CREATE TABLE tu_measurements (
    run_id TEXT NOT NULL,
    seq INTEGER NOT NULL,
    node_id TEXT,
    value_type TEXT NOT NULL,
    value_double REAL,
    value_int INTEGER,
    value_text TEXT,
    unit TEXT,
    low_limit REAL,
    high_limit REAL,
    rating TEXT,
    PRIMARY KEY (run_id, seq),
    CONSTRAINT fk_tu_meas_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
);

CREATE TABLE tu_waveform_chunks (
    run_id TEXT NOT NULL,
    waveform_no INTEGER NOT NULL,
    part INTEGER NOT NULL,
    parts INTEGER NOT NULL,
    waveform_id TEXT NOT NULL,
    event_seq INTEGER NOT NULL,
    node_id TEXT,
    channel TEXT NOT NULL,
    unit TEXT,
    sample_rate REAL NOT NULL,
    start_utc INTEGER NOT NULL,
    sample_offset INTEGER NOT NULL,
    sample_count INTEGER NOT NULL,
    total_samples INTEGER NOT NULL,
    sample_type TEXT NOT NULL,
    samples BLOB NOT NULL,
    times BLOB,
    quality TEXT,
    sync_group TEXT,
    PRIMARY KEY (run_id, waveform_no, part),
    CONSTRAINT fk_tu_wave_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
);

CREATE TABLE tu_operator_inputs (
    run_id TEXT NOT NULL,
    seq INTEGER NOT NULL,
    node_id TEXT,
    prompt TEXT,
    value_type TEXT,
    value_text TEXT,
    time_utc INTEGER NOT NULL,
    PRIMARY KEY (run_id, seq),
    CONSTRAINT fk_tu_input_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
);

CREATE TABLE tu_development_events (
    run_id TEXT NOT NULL,
    seq INTEGER NOT NULL,
    user_name TEXT,
    details TEXT,
    time_utc INTEGER NOT NULL,
    PRIMARY KEY (run_id, seq),
    CONSTRAINT fk_tu_dev_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
);

INSERT INTO tu_schema_version (version, applied_utc) VALUES (1, 'manuell');

-- Version 2
CREATE TABLE tu_releases (
    release_id TEXT NOT NULL PRIMARY KEY,
    project_name TEXT NOT NULL,
    version TEXT NOT NULL,
    state TEXT NOT NULL,
    package BLOB NOT NULL,
    package_sha256 TEXT NOT NULL,
    package_size INTEGER NOT NULL,
    created_by TEXT NOT NULL,
    created_utc INTEGER NOT NULL,
    reviewed_by TEXT,
    reviewed_utc INTEGER,
    released_by TEXT,
    released_utc INTEGER,
    archived_utc INTEGER,
    review_report TEXT,
    CONSTRAINT uq_tu_releases_version UNIQUE (project_name, version)
);

CREATE TABLE tu_audit_log (
    seq INTEGER NOT NULL PRIMARY KEY,
    time_utc INTEGER NOT NULL,
    time_text TEXT NOT NULL,
    user_name TEXT NOT NULL,
    action TEXT NOT NULL,
    details TEXT,
    release_id TEXT,
    prev_hash TEXT NOT NULL,
    entry_hash TEXT NOT NULL
);

INSERT INTO tu_schema_version (version, applied_utc) VALUES (2, 'manuell');

-- Version 3
ALTER TABLE tu_runs ADD order_id TEXT;

CREATE INDEX ix_tu_runs_order ON tu_runs (order_id);

INSERT INTO tu_schema_version (version, applied_utc) VALUES (3, 'manuell');

