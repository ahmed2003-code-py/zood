BEGIN READ ONLY;
\echo == flyway history (last 3)
SELECT version, description, success FROM flyway_schema_history ORDER BY installed_rank DESC LIMIT 3;
\echo == plate collisions (must be 0 rows)
SELECT plate_key_c, array_agg(id) AS ids FROM (
  SELECT id, CASE WHEN plate_key ~ '^[0-9]+[^0-9]+$' OR plate_key ~ '^[^0-9]+[0-9]+$'
                  THEN regexp_replace(plate_key, '[^0-9]', '', 'g') || regexp_replace(plate_key, '[0-9]', '', 'g')
                  ELSE plate_key END AS plate_key_c
  FROM vehicles WHERE status <> 'DECOMMISSIONED') v
GROUP BY plate_key_c HAVING count(*) > 1;
\echo == vehicles over 1500 L (must be 0 rows)
SELECT id, tank_capacity_ml FROM vehicles WHERE tank_capacity_ml > 1500000;
\echo == top-ups by status
SELECT status, count(*) FROM topup_requests GROUP BY 1 ORDER BY 1;
\echo == users by role
SELECT role, status, count(*) FROM users GROUP BY 1, 2 ORDER BY 1, 2;
\echo == sizes
SELECT (SELECT count(*) FROM fuelings) AS fuelings, (SELECT count(*) FROM vehicles) AS vehicles,
       (SELECT count(*) FROM companies) AS companies, (SELECT count(*) FROM ledger_entries) AS ledger_entries;
\echo == database size
SELECT pg_size_pretty(pg_database_size(current_database()));
ROLLBACK;
