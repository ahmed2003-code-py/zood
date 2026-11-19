# ADR 0026: Bulk vehicle import

**Status:** Accepted, 2026-11-19 · **Source:** audit F4; owner requirement (2,000+ vehicles per company)

## Decision
- **Flow:** Upload, Validate, Preview, Confirm, Background, Results. Ops only, one company per file.
- **Formats:** CSV (UTF-8, comma or semicolon) and XLSX (first sheet), with templates in Arabic and English. XLSX uses `org.dhatim:fastexcel-reader` and `fastexcel` 0.18.4, a small streaming library, instead of Apache POI. Archive checks use `commons-compress`, which the reader already depends on.
- **Limits:**
  - 5 MB and 5,000 rows per file. A larger file is refused, never truncated.
  - XLSX archives are inspected first: at most 200 entries and 60 MB once inflated, and any XML with a DOCTYPE is refused.
  - Unknown or duplicate headers are refused.
- **Validation:**
  - the same rules as a single vehicle;
  - duplicates within the file (plate key, fleet code);
  - plates already in service platform-wide, and fleet codes in the company;
  - department matched by name within the company only.
- **Storage:** every row is stored with its normalised values and errors. The file itself is not kept.
- **Semantics:**
  - `STRICT` (default): only a file without a single problem can be confirmed.
  - `VALID_ROWS_ONLY`: must be chosen explicitly; problem rows are marked `SKIPPED`.
  - At most one queued or running import per company (partial unique index).
- **Execution:** a PostgreSQL-backed worker (`FleetImportWorker`).
  - It claims a job with `FOR UPDATE SKIP LOCKED` and a 2-minute lease, and processes 250 rows per tick.
  - Each row is one transaction: the vehicle, its limits, and the row outcome with the job counter. A crash resumes at the first `VALID` row, so nothing is created twice.
  - A plate registered meanwhile makes the row `EXISTS` (the unique index is the last defence).
- **Scope:** the worker runs as the platform on behalf of the confirming Ops user (`DbScope.runAs`, a reviewed entry in `ArchitectureTests`).

## Alternatives
- **All-or-nothing in one transaction:** fine for 5,000 rows in time, but it gives no progress and holds plate index locks for the whole run.
- **A message broker:** no measured need, so rejected.

## Verified by
`FleetImportTests`: CSV (Arabic headers and digits), XLSX, bad files (type, encoding, too many rows, zip bomb, DOCTYPE), problem rows and the two modes, re-upload, a worker crash resumed by another worker, and overlapping imports in two concurrent workers.
