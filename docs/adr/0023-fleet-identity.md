# ADR 0023: Vehicle and tag identity

**Status:** Accepted, 2026-11-19 · **Source:** audit H1, H5, H6

## Decision
1. **Plates.**
   - A plate is unique among vehicles in service (not decommissioned) across the platform, because a physical plate is one vehicle.
   - A vehicle moving to another company is decommissioned at the old company first. Decommissioning frees the plate, and the history stays.
2. **Canonical key, Egyptian shape only.** For plates with exactly one digit group and one letter group, the comparison key puts the digits first, so the typing order does not matter. Any other shape keeps its characters in order, so two different plates never share a key.
   - V14 applies the rule to existing keys, and stops with a clear error if two vehicles would collide (`MigrationCompatibilityTests`).
   - Plate category (private, taxi, transport) is not part of identity. This needs research with real fleet data.
3. **Fleet code:** `fleet_code` is optional and unique per company among vehicles in service, case-insensitively.
4. **Tank ceiling:** a tank holds at most 1,500 L, the ceiling of one fueling. The year must be between 1950 and next year.
5. **Immutable identity columns.**
   - Tags: `company_id`, `vehicle_id`, and the UID columns.
   - Vehicles: `company_id`.
   - A trigger refuses changes for every role. A tag row is therefore one assignment of one UID to one vehicle for ever; with the UID unique for ever, `rfid_tags` is the assignment history.
   - A separate `vehicle_tag_assignments` table is not needed.

## Unchanged
At most one active tag per vehicle (partial unique index), the tag transition table, the vehicle lock before tag changes (concurrent issue tests in `FleetTests`), and fueling immutability.
