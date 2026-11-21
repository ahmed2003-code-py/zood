-- Bulk vehicle import (audit F4; ADR 0026). Operations uploads one company's file (CSV or XLSX);
-- every row is validated and stored before anything is created; Operations confirms; a worker in
-- the API creates the vehicles one short transaction per row, recording each row's outcome in the
-- same transaction as the vehicle. A crash or restart resumes at the first row not yet processed,
-- and the plate's unique index stays the last defence against a duplicate.
--
-- The file itself is not kept: only the normalised values of each row, so no spreadsheet (and no
-- formula) is ever stored or served back.

CREATE TABLE fleet_import_jobs (
  id                 uuid PRIMARY KEY,
  company_id         uuid NOT NULL REFERENCES companies (id),
  file_name          text NOT NULL CHECK (length(file_name) BETWEEN 1 AND 255),
  file_sha256        text NOT NULL CHECK (file_sha256 ~ '^[0-9a-f]{64}$'),
  file_format        text NOT NULL CHECK (file_format IN ('CSV', 'XLSX')),
  status             text NOT NULL CHECK (status IN ('VALIDATED', 'QUEUED', 'RUNNING', 'COMPLETED',
                                                     'COMPLETED_WITH_ERRORS', 'CANCELLED')),
  mode               text CHECK (mode IN ('STRICT', 'VALID_ROWS_ONLY')),
  row_count          integer NOT NULL CHECK (row_count BETWEEN 1 AND 5000),
  valid_count        integer NOT NULL CHECK (valid_count >= 0),
  invalid_count      integer NOT NULL CHECK (invalid_count >= 0),
  imported_count     integer NOT NULL DEFAULT 0 CHECK (imported_count >= 0),
  existing_count     integer NOT NULL DEFAULT 0 CHECK (existing_count >= 0),
  failed_count       integer NOT NULL DEFAULT 0 CHECK (failed_count >= 0),
  created_by         uuid NOT NULL,
  created_at         timestamptz NOT NULL,
  confirmed_by       uuid,
  confirmed_by_role  text,
  confirmed_at       timestamptz,
  started_at         timestamptz,
  finished_at        timestamptz,
  lease_owner        text,
  lease_until        timestamptz,
  updated_at         timestamptz NOT NULL,
  CONSTRAINT fleet_import_jobs_counts CHECK (valid_count + invalid_count = row_count),
  CONSTRAINT fleet_import_jobs_confirmed CHECK (
    (status IN ('VALIDATED', 'CANCELLED')) OR (confirmed_by IS NOT NULL AND confirmed_at IS NOT NULL AND mode IS NOT NULL)),
  CONSTRAINT fleet_import_jobs_strict_is_clean CHECK (mode IS DISTINCT FROM 'STRICT' OR invalid_count = 0),
  CONSTRAINT fleet_import_jobs_finished CHECK (
    (status IN ('COMPLETED', 'COMPLETED_WITH_ERRORS')) = (finished_at IS NOT NULL))
);

CREATE INDEX fleet_import_jobs_company_idx ON fleet_import_jobs (company_id, id);
CREATE INDEX fleet_import_jobs_status_idx ON fleet_import_jobs (status, id);
-- One import at a time per company: two overlapping files never race each other.
CREATE UNIQUE INDEX fleet_import_jobs_one_active ON fleet_import_jobs (company_id) WHERE status IN ('QUEUED', 'RUNNING');

CREATE TABLE fleet_import_rows (
  job_id        uuid NOT NULL REFERENCES fleet_import_jobs (id),
  row_number    integer NOT NULL CHECK (row_number >= 2),
  plate         text,
  plate_key     text,
  fleet_code    text,
  data          jsonb NOT NULL,
  status        text NOT NULL CHECK (status IN ('VALID', 'INVALID', 'DUPLICATE_IN_FILE', 'EXISTS', 'IMPORTED',
                                                'FAILED', 'SKIPPED')),
  errors        jsonb NOT NULL DEFAULT '[]'::jsonb,
  vehicle_id    uuid REFERENCES vehicles (id),
  processed_at  timestamptz,
  PRIMARY KEY (job_id, row_number),
  CONSTRAINT fleet_import_rows_imported_has_vehicle CHECK ((status = 'IMPORTED') = (vehicle_id IS NOT NULL))
);

CREATE INDEX fleet_import_rows_status_idx ON fleet_import_rows (job_id, status, row_number);
CREATE INDEX fleet_import_rows_vehicle_idx ON fleet_import_rows (vehicle_id) WHERE vehicle_id IS NOT NULL;

COMMENT ON TABLE fleet_import_jobs IS 'A bulk vehicle import: one file for one company, validated, confirmed, then run by the API worker.';
COMMENT ON TABLE fleet_import_rows IS 'One row of an import file, normalised, with its validation errors and its outcome.';

GRANT SELECT, INSERT ON fleet_import_jobs, fleet_import_rows TO zawed_app;
GRANT UPDATE (status, mode, imported_count, existing_count, failed_count, confirmed_by, confirmed_by_role, confirmed_at,
              started_at, finished_at, lease_owner, lease_until, updated_at) ON fleet_import_jobs TO zawed_app;
GRANT UPDATE (status, errors, vehicle_id, processed_at) ON fleet_import_rows TO zawed_app;

-- Operations only: an import is an Ops action on a company's fleet, never visible to the company.
ALTER TABLE fleet_import_jobs ENABLE ROW LEVEL SECURITY;
CREATE POLICY fleet_import_jobs_platform ON fleet_import_jobs USING (app_is_platform()) WITH CHECK (app_is_platform());
ALTER TABLE fleet_import_rows ENABLE ROW LEVEL SECURITY;
CREATE POLICY fleet_import_rows_platform ON fleet_import_rows USING (app_is_platform()) WITH CHECK (app_is_platform());
