# ADR 0026: Bulk vehicle import

**Status:** Accepted, 2026-11-19 · **Source:** audit F4; owner requirement (2,000+ vehicles per company)

## Decision
- **Flow:** Upload, Validate, Preview, Confirm, Background, Results. Ops only, one company per file.
- **Formats:** CSV (UTF-8, comma or semicolon) and XLSX (first sheet), with templates in Arabic and English. XLSX uses `org.dhatim:fastexcel-reader` and `fastexcel` 0.18.4, a small streaming library, instead of Apache POI. Archive checks use `commons-compress`, which the reader already depends on.
- **Limits:**
  - 5 MB and 5,000 rows per file. A larger file is refused, never truncated.
  - XLSX archives are inspected first: at most 200 entries and 60 MB once inflated.
  - Every XML part is scanned whole, across read boundaries and in any letter case. A `<!DOCTYPE` or `<!ENTITY` anywhere refuses the file, and so does a part that is not ASCII-compatible (UTF-16 or UTF-32). This was amended on 2026-11-19 after review; before that, only the first 4 KB were read.
  - Unknown or duplicate headers are refused.
- **Validation:**
  - the same rules as a single vehicle, including `INVALID_FLEET_CODE`: once normalised (whitespace becomes one space), a fleet code must hold no control character, which is the same set the database refuses in V14;
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
  - Any other failure of a row makes that row `FAILED` with the code `INTERNAL`, and the job moves on.
    - Before 2026-11-19 such a failure escaped the worker, the row stayed `VALID`, and the job was reclaimed and failed on it forever, which blocked every later import.
    - The log records the job, the row number, the exception type and the SQLState. It never records the row's data or the exception message, because a PostgreSQL constraint error includes the whole failing row.
  - When the job finishes, its counters are recomputed from the rows. A row committed by a worker whose lease had just been taken over cannot fall out of the totals.
- **Scope:** the worker runs as the platform on behalf of the confirming Ops user (`DbScope.runAs`, a reviewed entry in `ArchitectureTests`).

## Alternatives
- **All-or-nothing in one transaction:** fine for 5,000 rows in time, but it gives no progress and holds plate index locks for the whole run.
- **A message broker:** no measured need, so rejected.

## Verified by
`FleetImportTests`: CSV (Arabic headers and digits), XLSX, bad files (type, encoding, too many rows, zip bomb, DOCTYPE), problem rows and the two modes, re-upload, a worker crash resumed by another worker, and overlapping imports in two concurrent workers. Since 2026-11-19 it also covers:
- a DOCTYPE behind 40 KB of comments, and a UTF-16 part;
- a control character in a fleet code;
- a row that fails unexpectedly, after which the job still finishes;
- the counters recomputed when the job finishes.

`FleetIdentityTests` covers `INVALID_FLEET_CODE` (400, not 500) on the vehicle endpoint.
