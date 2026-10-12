-- Testumgebung – Datenbankschema für Microsoft SQL Server
-- Erzeugt aus SqlDialect; die Anwendung migriert automatisch (tu_schema_version).

IF OBJECT_ID(N'tu_schema_version', N'U') IS NULL CREATE TABLE tu_schema_version (version BIGINT NOT NULL PRIMARY KEY, applied_utc NVARCHAR(64) NOT NULL);

-- Version 1
CREATE TABLE tu_definitions (
    definition_id NVARCHAR(128) NOT NULL PRIMARY KEY,
    test_name NVARCHAR(MAX) NOT NULL,
    test_version NVARCHAR(64) NOT NULL,
    structure_json NVARCHAR(MAX) NOT NULL,
    created_utc BIGINT NOT NULL
);

CREATE TABLE tu_runs (
    run_id NVARCHAR(128) NOT NULL PRIMARY KEY,
    station_run_id NVARCHAR(MAX),
    station NVARCHAR(MAX),
    dut_id NVARCHAR(128) NOT NULL,
    definition_id NVARCHAR(128) NOT NULL,
    test_name NVARCHAR(MAX) NOT NULL,
    test_version NVARCHAR(64) NOT NULL,
    started_utc BIGINT NOT NULL,
    started_text NVARCHAR(64) NOT NULL,
    ended_utc BIGINT NOT NULL,
    ended_text NVARCHAR(64) NOT NULL,
    rating NVARCHAR(64) NOT NULL,
    werror BIGINT NOT NULL,
    development_run BIGINT NOT NULL,
    application NVARCHAR(MAX),
    stored_utc BIGINT NOT NULL,
    CONSTRAINT fk_tu_runs_definition FOREIGN KEY (definition_id) REFERENCES tu_definitions (definition_id)
);

CREATE INDEX ix_tu_runs_dut ON tu_runs (dut_id);

CREATE INDEX ix_tu_runs_started ON tu_runs (started_utc);

CREATE TABLE tu_step_executions (
    run_id NVARCHAR(128) NOT NULL,
    seq BIGINT NOT NULL,
    kind NVARCHAR(64) NOT NULL,
    node_id NVARCHAR(128),
    path NVARCHAR(MAX),
    iteration BIGINT NOT NULL,
    time_utc BIGINT NOT NULL,
    time_text NVARCHAR(64) NOT NULL,
    rating NVARCHAR(64),
    message NVARCHAR(MAX),
    cancelled BIGINT NOT NULL,
    PRIMARY KEY (run_id, seq),
    CONSTRAINT fk_tu_steps_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
);

CREATE TABLE tu_measurements (
    run_id NVARCHAR(128) NOT NULL,
    seq BIGINT NOT NULL,
    node_id NVARCHAR(128),
    value_type NVARCHAR(64) NOT NULL,
    value_double FLOAT,
    value_int BIGINT,
    value_text NVARCHAR(MAX),
    unit NVARCHAR(64),
    low_limit FLOAT,
    high_limit FLOAT,
    rating NVARCHAR(64),
    PRIMARY KEY (run_id, seq),
    CONSTRAINT fk_tu_meas_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
);

CREATE TABLE tu_waveform_chunks (
    run_id NVARCHAR(128) NOT NULL,
    waveform_no BIGINT NOT NULL,
    part BIGINT NOT NULL,
    parts BIGINT NOT NULL,
    waveform_id NVARCHAR(MAX) NOT NULL,
    event_seq BIGINT NOT NULL,
    node_id NVARCHAR(128),
    channel NVARCHAR(MAX) NOT NULL,
    unit NVARCHAR(64),
    sample_rate FLOAT NOT NULL,
    start_utc BIGINT NOT NULL,
    sample_offset BIGINT NOT NULL,
    sample_count BIGINT NOT NULL,
    total_samples BIGINT NOT NULL,
    sample_type NVARCHAR(64) NOT NULL,
    samples VARBINARY(MAX) NOT NULL,
    times VARBINARY(MAX),
    quality NVARCHAR(MAX),
    sync_group NVARCHAR(MAX),
    PRIMARY KEY (run_id, waveform_no, part),
    CONSTRAINT fk_tu_wave_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
);

CREATE TABLE tu_operator_inputs (
    run_id NVARCHAR(128) NOT NULL,
    seq BIGINT NOT NULL,
    node_id NVARCHAR(128),
    prompt NVARCHAR(MAX),
    value_type NVARCHAR(64),
    value_text NVARCHAR(MAX),
    time_utc BIGINT NOT NULL,
    PRIMARY KEY (run_id, seq),
    CONSTRAINT fk_tu_input_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
);

CREATE TABLE tu_development_events (
    run_id NVARCHAR(128) NOT NULL,
    seq BIGINT NOT NULL,
    user_name NVARCHAR(MAX),
    details NVARCHAR(MAX),
    time_utc BIGINT NOT NULL,
    PRIMARY KEY (run_id, seq),
    CONSTRAINT fk_tu_dev_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
);

INSERT INTO tu_schema_version (version, applied_utc) VALUES (1, 'manuell');

-- Version 2
CREATE TABLE tu_releases (
    release_id NVARCHAR(128) NOT NULL PRIMARY KEY,
    project_name NVARCHAR(128) NOT NULL,
    version NVARCHAR(64) NOT NULL,
    state NVARCHAR(64) NOT NULL,
    package VARBINARY(MAX) NOT NULL,
    package_sha256 NVARCHAR(64) NOT NULL,
    package_size BIGINT NOT NULL,
    created_by NVARCHAR(MAX) NOT NULL,
    created_utc BIGINT NOT NULL,
    reviewed_by NVARCHAR(MAX),
    reviewed_utc BIGINT,
    released_by NVARCHAR(MAX),
    released_utc BIGINT,
    archived_utc BIGINT,
    review_report NVARCHAR(MAX),
    CONSTRAINT uq_tu_releases_version UNIQUE (project_name, version)
);

CREATE TABLE tu_audit_log (
    seq BIGINT NOT NULL PRIMARY KEY,
    time_utc BIGINT NOT NULL,
    time_text NVARCHAR(64) NOT NULL,
    user_name NVARCHAR(MAX) NOT NULL,
    action NVARCHAR(MAX) NOT NULL,
    details NVARCHAR(MAX),
    release_id NVARCHAR(128),
    prev_hash NVARCHAR(64) NOT NULL,
    entry_hash NVARCHAR(64) NOT NULL
);

INSERT INTO tu_schema_version (version, applied_utc) VALUES (2, 'manuell');

-- Version 3
ALTER TABLE tu_runs ADD order_id NVARCHAR(128);

CREATE INDEX ix_tu_runs_order ON tu_runs (order_id);

INSERT INTO tu_schema_version (version, applied_utc) VALUES (3, 'manuell');

