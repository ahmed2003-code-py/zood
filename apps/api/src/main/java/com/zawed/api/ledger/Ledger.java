package com.zawed.api.ledger;

import java.time.Instant;
import java.util.List;
import java.util.Objects;
import java.util.UUID;

import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.ZawedPrincipal;

/**
 * The only way money moves (implementation plan §2, boundary rule 1). Other modules call it from
 * inside their own transaction: a top-up confirmation, a fueling (B4), a payout (B6) post here and
 * commit or roll back together with their own rows.
 *
 * <p>Amounts are integer piastres. A posting is double-entry and balanced; the database checks it
 * again at commit. A source posts once: a second posting of the same {@code (sourceType, sourceId)}
 * answers {@code 409 ALREADY_POSTED} and writes nothing.
 *
 * <p>A company wallet's cached balance changes only here, under the wallet row's lock, in the same
 * transaction as the entry that changes it. Lock order is wallet first (then the tag, in the fueling
 * engine): callers that lock the wallet themselves with {@link #lockWallet} before posting take the
 * same lock, so the posting never waits on them.
 */
public interface Ledger {

	/** What caused a posting. One source id posts at most once per type. */
	enum SourceType {

		/** A confirmed top-up by bank transfer: {@code platform:bank} to the company wallet. */
		TOPUP,
		/** A billable fueling (B4): company wallet to the station payable. */
		FUELING,
		/** A station payout (B6): station payable to {@code platform:bank}. */
		PAYOUT,
		/** The mirror of an earlier transaction (B6, two-person rule). */
		REVERSAL,
		/** A manual correction by a Super Admin (B6). Used by the B3 tests to debit a wallet. */
		ADJUSTMENT

	}

	enum Direction {

		DEBIT, CREDIT

	}

	/** A named account of plan §3.5. */
	record Account(Kind kind, UUID ownerId) {

		public enum Kind {

			PLATFORM_BANK, COMPANY_WALLET, STATION_PAYABLE

		}

		public Account {
			Objects.requireNonNull(kind, "kind");
			if ((kind == Kind.PLATFORM_BANK) != (ownerId == null)) {
				throw new IllegalArgumentException(kind + (ownerId == null ? " needs an owner" : " has no owner"));
			}
		}

		public static Account platformBank() {
			return new Account(Kind.PLATFORM_BANK, null);
		}

		public static Account companyWallet(UUID companyId) {
			return new Account(Kind.COMPANY_WALLET, Objects.requireNonNull(companyId, "companyId"));
		}

		public static Account stationPayable(UUID stationId) {
			return new Account(Kind.STATION_PAYABLE, Objects.requireNonNull(stationId, "stationId"));
		}

		/** The account's code as stored: {@code platform:bank}, {@code company:{id}:wallet}, {@code station:{id}:payable}. */
		public String code() {
			return switch (kind) {
				case PLATFORM_BANK -> "platform:bank";
				case COMPANY_WALLET -> "company:" + ownerId + ":wallet";
				case STATION_PAYABLE -> "station:" + ownerId + ":payable";
			};
		}

	}

	/** One side of a posting. The amount is always positive; the direction says which side. */
	record Entry(Account account, Direction direction, long piastres) {

		public Entry {
			Objects.requireNonNull(account, "account");
			Objects.requireNonNull(direction, "direction");
			if (piastres <= 0) {
				throw new IllegalArgumentException("An entry amount must be positive: " + piastres);
			}
		}

		public static Entry debit(Account account, long piastres) {
			return new Entry(account, Direction.DEBIT, piastres);
		}

		public static Entry credit(Account account, long piastres) {
			return new Entry(account, Direction.CREDIT, piastres);
		}

	}

	/** Who posts: a signed-in user, or the system (a job, later a device) with no user. */
	record Poster(UUID userId, Role role) {

		public static final Poster SYSTEM = new Poster(null, null);

		public static Poster of(ZawedPrincipal principal) {
			return new Poster(principal.userId(), principal.role());
		}

	}

	/**
	 * A balanced transaction to post.
	 *
	 * @param reference what a reader looks for on a statement: a bank reference, a receipt reference
	 * @param description a short English fallback; the UI words the entry from the source type
	 */
	record Posting(SourceType sourceType, UUID sourceId, String reference, String description, Poster poster,
			List<Entry> entries) {

		public Posting {
			Objects.requireNonNull(sourceType, "sourceType");
			Objects.requireNonNull(sourceId, "sourceId");
			Objects.requireNonNull(poster, "poster");
			entries = List.copyOf(entries);
			if (entries.size() < 2) {
				throw new IllegalArgumentException("A posting needs at least two entries");
			}
			long debits = 0;
			long credits = 0;
			for (Entry entry : entries) {
				if (entry.direction() == Direction.DEBIT) {
					debits = Math.addExact(debits, entry.piastres());
				}
				else {
					credits = Math.addExact(credits, entry.piastres());
				}
			}
			if (debits != credits) {
				throw new IllegalArgumentException("A posting must balance: debits " + debits + " <> credits " + credits);
			}
		}

	}

	/** A company wallet's state while its row is locked. */
	record Wallet(UUID companyId, UUID accountId, long balancePiastres, Instant updatedAt) {
	}

	/** How a posting moved one wallet. */
	record WalletChange(UUID companyId, long balanceBefore, long balanceAfter, long lowBalanceThreshold) {

		/** True when this posting took the wallet from at-or-above its threshold to below it. */
		public boolean crossedBelowThreshold() {
			return balanceBefore >= lowBalanceThreshold && balanceAfter < lowBalanceThreshold;
		}

		public boolean isLow() {
			return balanceAfter < lowBalanceThreshold;
		}

	}

	record Posted(UUID transactionId, List<WalletChange> wallets) {
	}

	/**
	 * Reads the company's wallet and holds its row until the transaction ends. Must run inside the
	 * caller's transaction.
	 *
	 * @throws com.zawed.api.platform.web.ApiException 404 when the company has no wallet in the
	 * caller's scope
	 */
	Wallet lockWallet(UUID companyId);

	/**
	 * Posts a balanced transaction, locking every wallet it touches, updating each cached balance
	 * and writing the entries, all in the caller's transaction.
	 *
	 * @throws com.zawed.api.platform.web.ApiException 409 {@code ALREADY_POSTED} for a source that
	 * has posted before; 409 {@code INSUFFICIENT_BALANCE} when a wallet debit would take its balance
	 * below zero; 404 when a wallet is not in the caller's scope
	 */
	Posted post(Posting posting);

}
