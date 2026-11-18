/**
 * Station finance (plan §2, phase B6; roadmap delta A1, A3, A4; ADR 0020): where a station is paid
 * (bank accounts under change control), what it is paid (payouts with dual control), what waits
 * (held payables and their release), what it can read (the statement of its payable account) and
 * how a completed fueling is corrected (a two-person reversal).
 *
 * <p>This module never writes money. It asks {@code ledger.Ledger} to post, inside its own
 * transaction, and keeps the business records around the posting. Every decision here is taken
 * while the station's payable is locked ({@code Ledger.lockPayable}), after any wallet lock.
 */
package com.zawed.api.stationfinance;
