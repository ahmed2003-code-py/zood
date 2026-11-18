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
 *
 * <p>A station payable works the same way since B6 (ADR 0020): its cached balance changes only
 * here, under the payable row's lock, and every payable entry records the balance it leaves. A
 * payable is locked <b>after</b> every wallet the posting touches, never before: wallet, (vehicle,
 * tag,) payable. A payout locks only the payable; a fueling and a reversal lock the wallet first.
 * Unlike a wallet, a payable has no floor: a fueling reversed after it was paid out leaves it
 * negative, which is what the station then owes.
 */
public interface Ledger {

	/** What caused a posting. One source id posts at most once per type. */
	enum SourceType {

		/** A confirmed top-up by bank transfer: {@code platform:bank} to the company wallet. */
		TOPUP,
		/**
		 * A billable fueling: company wallet to the station payable (B4). An offline fueling the
		 * wallet cannot cover debits the wallet what it holds and the company receivable the rest (B5).
		 */
		FUELING,
		/** A station payout (B6): station payable to {@code platform:bank}. */
		PAYOUT,
		/** The mirror of an earlier transaction (B6, two-person rule); posted with {@link Ledger#reverse}. */
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

			PLATFORM_BANK, COMPANY_WALLET,
			/**
			 * What a company owes for fuel it took while its wallet could not cover it (B5 offline
			 * sync, ADR 0019). An asset of the platform: a debit raises the debt, a credit settles it.
			 * It has no cached balance, no lock and no floor: those belong to the wallet alone.
			 */
			COMPANY_RECEIVABLE, STATION_PAYABLE

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

		public static Account companyReceivable(UUID companyId) {
			return new Account(Kind.COMPANY_RECEIVABLE, Objects.requireNonNull(companyId, "companyId"));
		}

		public static Account stationPayable(UUID stationId) {
			return new Account(Kind.STATION_PAYABLE, Objects.requireNonNull(stationId, "stationId"));
		}

		/**
		 * The company whose money the account holds, or null for the bank and a station's payable.
		 * Entries on a company's account carry it (the database checks they agree, V6).
		 */
		public UUID companyId() {
			return kind == Kind.COMPANY_WALLET || kind == Kind.COMPANY_RECEIVABLE ? ownerId : null;
		}

		/**
		 * The account's code as stored: {@code platform:bank}, {@code company:{id}:wallet},
		 * {@code company:{id}:receivable}, {@code station:{id}:payable}.
		 */
		public String code() {
			return switch (kind) {
				case PLATFORM_BANK -> "platform:bank";
				case COMPANY_WALLET -> "company:" + ownerId + ":wallet";
				case COMPANY_RECEIVABLE -> "company:" + ownerId + ":receivable";
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

	/**
	 * A station payable's state while its row is locked: what the platform owes the station.
	 * Negative when the station owes the platform.
	 */
	record Payable(UUID stationId, UUID accountId, long balancePiastres, Instant updatedAt) {
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

	/** How a posting moved one station payable. */
	record PayableChange(UUID stationId, long balanceBefore, long balanceAfter) {
	}

	/**
	 * @param wallets one change per wallet the posting touched, in ascending company id; empty when
	 * it touched none (an offline fueling charged wholly to the receivable)
	 * @param payables one change per station payable the posting touched, in ascending station id
	 */
	record Posted(UUID transactionId, List<WalletChange> wallets, List<PayableChange> payables) {
	}

	/**
	 * The mirror of an earlier transaction (PRD §6.4): every account the original debited is
	 * credited the same amount and the reverse, so the two cancel out exactly.
	 *
	 * @param reversalId the source of the posting: the reversal record that was approved
	 * @param originalTransactionId the ledger transaction to mirror
	 */
	record Reversal(UUID reversalId, UUID originalTransactionId, String reference, String description, Poster poster) {

		public Reversal {
			Objects.requireNonNull(reversalId, "reversalId");
			Objects.requireNonNull(originalTransactionId, "originalTransactionId");
			Objects.requireNonNull(poster, "poster");
		}

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
	 * Reads the station's payable and holds its row until the transaction ends, opening it (at zero)
	 * when the station has never been posted to. Must run inside the caller's transaction, in the
	 * platform scope. Take it after any wallet lock, never before (see the class comment).
	 *
	 * @throws com.zawed.api.platform.web.ApiException 404 when there is no such station
	 */
	Payable lockPayable(UUID stationId);

	/**
	 * Posts a balanced transaction, locking every wallet and then every station payable it touches,
	 * updating each cached balance and writing the entries, all in the caller's transaction.
	 *
	 * @throws com.zawed.api.platform.web.ApiException 409 {@code ALREADY_POSTED} for a source that
	 * has posted before; 409 {@code INSUFFICIENT_BALANCE} when a wallet debit would take its balance
	 * below zero; 404 when a wallet is not in the caller's scope
	 * @throws IllegalArgumentException for a {@link SourceType#REVERSAL}: a reversal is posted with
	 * {@link #reverse}, which builds the mirror itself
	 */
	Posted post(Posting posting);

	/**
	 * Posts the exact mirror of an earlier transaction as a {@link SourceType#REVERSAL}, with the
	 * same locks and guarantees as {@link #post}. A transaction is reversed at most once, and a
	 * reversal is never reversed; the database checks both, and that the two cancel out (V9).
	 *
	 * @throws com.zawed.api.platform.web.ApiException 409 {@code ALREADY_REVERSED} when the original
	 * was reversed before; 409 {@code ALREADY_POSTED} when this reversal id posted before; 404 when
	 * the original does not exist
	 */
	Posted reverse(Reversal reversal);

}
