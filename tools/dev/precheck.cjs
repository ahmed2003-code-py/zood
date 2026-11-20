// Read-only pre-deploy checks for V12–V16 on zawed-dev (deployment-guide.md, "Before migrating").
// Runs in a READ ONLY transaction; prints counts and ids only.
const { Client } = require('pg');

(async () => {
  const c = new Client({ connectionString: process.env.DB_URL, ssl: { rejectUnauthorized: false } });
  await c.connect();
  await c.query('BEGIN READ ONLY');
  const q = async (label, sql) => {
    const r = await c.query(sql);
    console.log(`\n== ${label} (${r.rowCount} rows)`);
    for (const row of r.rows.slice(0, 20)) console.log(JSON.stringify(row));
  };
  await q('flyway history (last 3)', 'SELECT version, description, success FROM flyway_schema_history ORDER BY installed_rank DESC LIMIT 3');
  await q('plate collisions (must be 0)', `
    SELECT plate_key_c, array_agg(id) AS ids FROM (
      SELECT id, CASE WHEN plate_key ~ '^[0-9]+[^0-9]+$' OR plate_key ~ '^[^0-9]+[0-9]+$'
                      THEN regexp_replace(plate_key, '[^0-9]', '', 'g') || regexp_replace(plate_key, '[0-9]', '', 'g')
                      ELSE plate_key END AS plate_key_c
      FROM vehicles WHERE status <> 'DECOMMISSIONED') v
    GROUP BY plate_key_c HAVING count(*) > 1`);
  await q('vehicles over 1500 L (must be 0)', 'SELECT id, tank_capacity_ml FROM vehicles WHERE tank_capacity_ml > 1500000');
  await q('top-ups by status', 'SELECT status, count(*)::int FROM topup_requests GROUP BY 1 ORDER BY 1');
  await q('users by role', 'SELECT role, status, count(*)::int FROM users GROUP BY 1, 2 ORDER BY 1, 2');
  await q('table sizes', `SELECT (SELECT count(*) FROM fuelings)::int AS fuelings, (SELECT count(*) FROM vehicles)::int AS vehicles,
                                 (SELECT count(*) FROM companies)::int AS companies, (SELECT count(*) FROM ledger_entries)::int AS ledger_entries`);
  await c.query('ROLLBACK');
  await c.end();
})().catch((e) => { console.error('ERROR', e.message); process.exit(1); });
