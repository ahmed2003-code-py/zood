-- QA round 1 review: Egyptian contact numbers in one form, and a tax id unique however it is typed.
--
-- Since QA round 1 the API stores a tax id as nine digits, a commercial registration as its digits and
-- a phone as 0… (platform/text/EgyptianContact). Rows written before that may hold 123-456-789 or
-- Arabic-Indic digits, and the unique indexes compared the raw text, so the same tax id could belong
-- to two companies (or two operators).
--
--  1. contact_digits / tax_id_key: Arabic-Indic and Persian digits to ASCII, separators removed. The
--     unique indexes on tax_id now compare tax_id_key(tax_id), so every spelling of one number clashes,
--     whether the row is new or legacy.
--  2. Existing rows: a clash stops the migration and names the rows (decide which is the duplicate
--     first), as V14 did for plates. Otherwise a value is rewritten only when its cleaned form is
--     valid; anything else is kept exactly as typed, still readable, and corrected on its next edit.

-- ----------------------------------------------------------------------------------------------
-- 1. The comparison form
-- ----------------------------------------------------------------------------------------------

CREATE FUNCTION contact_digits(p_value text) RETURNS text
  LANGUAGE sql IMMUTABLE PARALLEL SAFE
  AS $$
    SELECT regexp_replace(translate(p_value, '٠١٢٣٤٥٦٧٨٩۰۱۲۳۴۵۶۷۸۹', '01234567890123456789'),
                          '[[:space:]\u00A0\u2000-\u200A\u202F\u205F\u3000().\u2010-\u2015-]', '', 'g')
  $$;

-- The same body as contact_digits, not a call to it: an index expression is evaluated with a
-- search_path of pg_catalog only (CREATE INDEX, VACUUM, ANALYZE), where contact_digits is not found.
CREATE FUNCTION tax_id_key(p_tax_id text) RETURNS text
  LANGUAGE sql IMMUTABLE PARALLEL SAFE
  AS $$
    SELECT regexp_replace(translate(p_tax_id, '٠١٢٣٤٥٦٧٨٩۰۱۲۳۴۵۶۷۸۹', '01234567890123456789'),
                          '[[:space:]\u00A0\u2000-\u200A\u202F\u205F\u3000().\u2010-\u2015-]', '', 'g')
  $$;

COMMENT ON FUNCTION tax_id_key(text) IS
  'The form two tax ids are compared in: digits in ASCII, no separators. The unique indexes and Ops search use it.';

GRANT EXECUTE ON FUNCTION contact_digits(text) TO zawed_app;
GRANT EXECUTE ON FUNCTION tax_id_key(text) TO zawed_app;

-- The phone rule of EgyptianContact.phone, for the backfill only.
CREATE FUNCTION v23_phone(p_value text) RETURNS text
  LANGUAGE sql IMMUTABLE
  AS $$
    SELECT CASE
             WHEN d LIKE '+20%' THEN '0' || substr(d, 4)
             WHEN d LIKE '0020%' THEN '0' || substr(d, 5)
             WHEN length(d) = 12 AND d LIKE '201%' THEN '0' || substr(d, 3)
             WHEN length(d) = 10 AND d LIKE '1%' THEN '0' || d
             ELSE d
           END
      FROM (SELECT contact_digits(p_value) AS d) cleaned
  $$;

-- ----------------------------------------------------------------------------------------------
-- 2. Existing rows
-- ----------------------------------------------------------------------------------------------

DO $$
DECLARE
  clash record;
BEGIN
  SELECT tax_id_key(tax_id) AS key, array_agg(id) AS ids INTO clash
    FROM companies WHERE tax_id IS NOT NULL
    GROUP BY tax_id_key(tax_id) HAVING count(*) > 1 LIMIT 1;
  IF FOUND THEN
    RAISE EXCEPTION 'V23: companies % would share the tax id %; correct the duplicate first', clash.ids, clash.key
      USING ERRCODE = 'unique_violation';
  END IF;
  SELECT tax_id_key(tax_id) AS key, array_agg(id) AS ids INTO clash
    FROM station_operators WHERE tax_id IS NOT NULL
    GROUP BY tax_id_key(tax_id) HAVING count(*) > 1 LIMIT 1;
  IF FOUND THEN
    RAISE EXCEPTION 'V23: station operators % would share the tax id %; correct the duplicate first', clash.ids,
      clash.key USING ERRCODE = 'unique_violation';
  END IF;
END
$$;

UPDATE companies SET tax_id = tax_id_key(tax_id)
  WHERE tax_id_key(tax_id) ~ '^[0-9]{9}$' AND tax_id <> tax_id_key(tax_id);
UPDATE station_operators SET tax_id = tax_id_key(tax_id)
  WHERE tax_id_key(tax_id) ~ '^[0-9]{9}$' AND tax_id <> tax_id_key(tax_id);

UPDATE companies SET commercial_registration = contact_digits(commercial_registration)
  WHERE contact_digits(commercial_registration) ~ '^[0-9]{3,12}$'
    AND commercial_registration <> contact_digits(commercial_registration);
UPDATE station_operators SET commercial_registration = contact_digits(commercial_registration)
  WHERE contact_digits(commercial_registration) ~ '^[0-9]{3,12}$'
    AND commercial_registration <> contact_digits(commercial_registration);

UPDATE companies SET contact_phone = v23_phone(contact_phone)
  WHERE v23_phone(contact_phone) ~ '^(01[0125][0-9]{8}|02[0-9]{8}|03[0-9]{7}|01[35][0-9]{7}|0[4-9][0-9]{8})$'
    AND contact_phone <> v23_phone(contact_phone);
UPDATE station_operators SET contact_phone = v23_phone(contact_phone)
  WHERE v23_phone(contact_phone) ~ '^(01[0125][0-9]{8}|02[0-9]{8}|03[0-9]{7}|01[35][0-9]{7}|0[4-9][0-9]{8})$'
    AND contact_phone <> v23_phone(contact_phone);
UPDATE agents SET phone = v23_phone(phone)
  WHERE v23_phone(phone) ~ '^01[0125][0-9]{8}$' AND phone <> v23_phone(phone);

DROP FUNCTION v23_phone(text);

-- ----------------------------------------------------------------------------------------------
-- 3. One tax id, however it is typed
-- ----------------------------------------------------------------------------------------------

DROP INDEX companies_tax_id_key;
CREATE UNIQUE INDEX companies_tax_id_key ON companies (tax_id_key(tax_id)) WHERE tax_id IS NOT NULL;

DROP INDEX station_operators_tax_id_key;
CREATE UNIQUE INDEX station_operators_tax_id_key ON station_operators (tax_id_key(tax_id)) WHERE tax_id IS NOT NULL;
