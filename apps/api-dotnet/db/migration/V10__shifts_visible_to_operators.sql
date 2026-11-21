-- Shifts for the station portal (plan §7 B6: "last shift" filters and the shift view). A station
-- manager may read the agent sessions at the operator's own stations. The token is stored only as
-- a SHA-256 hash of 32 random bytes, which cannot be turned back into a token; the API never
-- returns it in any case.
CREATE POLICY agent_sessions_operator_read ON agent_sessions FOR SELECT
  USING (app_scope_type() = 'OPERATOR' AND operator_id = app_scope_id());
