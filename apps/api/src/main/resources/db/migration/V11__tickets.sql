-- Support tickets (roadmap delta A5; plan §7 B6 "tickets and requests"). A company or a station
-- asks Operations for something (a lost tag, a limit change, a device fault, a payout question, a
-- dispute); Operations answers in the same thread and moves the ticket to resolved. The company
-- and station portals stay read-only for master data: changes still happen at Operations, from
-- these requests.

CREATE SEQUENCE ticket_reference_seq START 1001;

CREATE TABLE tickets (
  id           uuid PRIMARY KEY,
  reference    text NOT NULL,
  scope_type   text NOT NULL CHECK (scope_type IN ('COMPANY', 'OPERATOR')),
  company_id   uuid REFERENCES companies (id),
  operator_id  uuid REFERENCES station_operators (id),
  type         text NOT NULL CHECK (type IN ('LOST_TAG', 'STOLEN_TAG', 'LIMIT_CHANGE', 'VEHICLE_CHANGE', 'TOPUP_QUERY',
                 'DEVICE_FAULT', 'TAG_READ_ISSUE', 'PAYOUT_QUERY', 'PRICE_QUERY', 'DISPUTE', 'OTHER')),
  subject      text NOT NULL CHECK (length(subject) BETWEEN 1 AND 200),
  status       text NOT NULL CHECK (status IN ('OPEN', 'IN_PROGRESS', 'RESOLVED', 'CLOSED')),
  vehicle_id   uuid REFERENCES vehicles (id),
  station_id   uuid REFERENCES stations (id),
  fueling_id   uuid REFERENCES fuelings (id),
  created_by   uuid NOT NULL,
  created_at   timestamptz NOT NULL,
  updated_at   timestamptz NOT NULL,
  assigned_to  uuid,
  resolved_at  timestamptz,
  CONSTRAINT tickets_scope CHECK (
       (scope_type = 'COMPANY' AND company_id IS NOT NULL AND operator_id IS NULL)
    OR (scope_type = 'OPERATOR' AND operator_id IS NOT NULL AND company_id IS NULL)),
  CONSTRAINT tickets_resolved CHECK ((status IN ('RESOLVED', 'CLOSED')) = (resolved_at IS NOT NULL))
);

CREATE UNIQUE INDEX tickets_reference_key ON tickets (reference);
CREATE INDEX tickets_status_idx ON tickets (status, id);
CREATE INDEX tickets_company_idx ON tickets (company_id, id);
CREATE INDEX tickets_operator_idx ON tickets (operator_id, id);

CREATE TABLE ticket_messages (
  id           uuid PRIMARY KEY,
  ticket_id    uuid NOT NULL REFERENCES tickets (id),
  author_id    uuid NOT NULL,
  author_role  text NOT NULL,
  body         text NOT NULL CHECK (length(body) BETWEEN 1 AND 4000),
  -- An Operations note the company or station never sees.
  internal     boolean NOT NULL,
  created_at   timestamptz NOT NULL
);

CREATE INDEX ticket_messages_ticket_idx ON ticket_messages (ticket_id, created_at);

GRANT SELECT, INSERT ON tickets TO zawed_app;
GRANT UPDATE (status, assigned_to, updated_at, resolved_at) ON tickets TO zawed_app;
GRANT SELECT, INSERT ON ticket_messages TO zawed_app;                         -- append only
GRANT USAGE ON SEQUENCE ticket_reference_seq TO zawed_app;

-- A company sees and opens its own tickets, an operator its own; Operations all. Only Operations
-- changes a ticket's status or assignee (the column grants allow the update; this policy limits
-- who passes it).
ALTER TABLE tickets ENABLE ROW LEVEL SECURITY;
CREATE POLICY tickets_read ON tickets FOR SELECT
  USING (app_is_platform()
         OR (app_scope_type() = 'COMPANY' AND company_id = app_scope_id())
         OR (app_scope_type() = 'OPERATOR' AND operator_id = app_scope_id()));
CREATE POLICY tickets_open ON tickets FOR INSERT
  WITH CHECK (app_is_platform()
              OR (app_scope_type() = 'COMPANY' AND scope_type = 'COMPANY' AND company_id = app_scope_id())
              OR (app_scope_type() = 'OPERATOR' AND scope_type = 'OPERATOR' AND operator_id = app_scope_id()));
CREATE POLICY tickets_update ON tickets FOR UPDATE
  USING (app_is_platform()) WITH CHECK (app_is_platform());

-- A message is visible with its ticket, except Operations' internal notes; a company or station
-- may only add a public message to its own ticket.
ALTER TABLE ticket_messages ENABLE ROW LEVEL SECURITY;
CREATE POLICY ticket_messages_read ON ticket_messages FOR SELECT
  USING (app_is_platform()
         OR (NOT internal AND EXISTS (SELECT 1 FROM tickets t WHERE t.id = ticket_id)));
CREATE POLICY ticket_messages_write ON ticket_messages FOR INSERT
  WITH CHECK (app_is_platform()
              OR (NOT internal AND EXISTS (SELECT 1 FROM tickets t WHERE t.id = ticket_id)));
